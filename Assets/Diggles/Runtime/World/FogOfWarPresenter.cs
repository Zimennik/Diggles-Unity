#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using UnityEngine;

    // Fog-of-war RENDER feed — the global per-tile fog texture + shader params (the render
    // half of the native CFogOfWar; the sim half is FogOfWarState). Ground truth:
    // Docs/FOG_OF_WAR_RE.md §4-5
    //
    // The native applies fog per terrain VERTEX from the tile's 4 corner fog bytes
    // (FUN_004ca830:133-283 decode → FUN_004cc040 bilinear interpolation; objects use the
    // twin FUN_00485370/FUN_004856e0). The port uploads ONE Width×Height global texture that
    // DigglesGround/DigglesModel sample bilinearly — the same global-upload idiom as the
    // _DigLights class-light buffer (RuntimeWorldSpawner.UploadClassLights, re-pushed every
    // frame); the texture itself re-uploads only when fog bytes visibly change (RegionDirty —
    // the port of the native dirty-rect masks 0x50/0x10), at most once per FoW update.
    //
    // TEXEL ENCODING (documented deviation from a raw-byte R8 map): RG16, one byte per
    // channel, holding the DECODED native per-corner values — because both render laws are
    // non-linear in the byte and the native interpolates the DECODED corner values, not the
    // raw byte:
    // R = fogFactor (FUN_004ca830 table, DAT_007372E8 = 1.3):
    // 0 → 0; 1..0x80 → 255; 0x81..0xE2 → 255 − 2·min(128, ⌊(b−0x80)·1.3⌋); ≥0xE3 → 0.
    // G = black-corner indicator: byte==0 → 0, else 255. The native corner bias is −128
    // iff the byte is 0 (and ≥0 otherwise), and the vertex multiplier is
    // (bias/256 + 1) — i.e. a 0/1 indicator, bilinearly interpolated across the tile.
    // A raw byte cannot reproduce this under bilinear filtering: saturate(b·255) is
    // razor-sharp against fresh 0xFF reveals, saturate(b·255/8) darkens resting gray
    // (byte 1) by 7/8. Decoding on the CPU makes the GPU bilinear EXACTLY the native
    // per-vertex corner interpolation.
    //
    // TEMPORAL SMOOTHING (deliberate PORT DEVIATION, user-requested — the native reveal is
    // an instant byte 0→0xFF flip and its fade re-uploads are quantized to every 5th
    // update): each texel's DISPLAY value chases its decoded TARGET at a fixed rate (full
    // swing in SmoothSeconds). Black reveals fade IN, the gray veil fades continuously
    // instead of stepping. SIM bytes stay byte-faithful — only presentation is smoothed.
    // Init snaps display=target so a loaded world doesn't fade in from black.
    //
    // Shader globals:
    // _FowTex RG16 map above (bilinear, clamp, no mips);
    // _FowParams (Width, Height, fogActive, 0) — fogActive = Enabled && ShowFog.
    // Fog stays LIVE during cutscenes (faithful — the native has no
    // sequence gate; show_fogofwar remains functional, unlike the dead
    // CGame+0x59c in the shipped exe, so a script CAN still reveal);
    // _FowWorldToNative world → terrain-local frame: local x = native tileX, −local z =
    // native tileY, local y = the native render depth z (7 = deepest
    // floor.. 14.5 = front plane). Verified: RuntimeWorldSpawner
    //.NativeToLocal = (x, z·0.5+7, −y) / LocalToNative = (x, −z,
    // (y−7)·2), and BdbPmpWorldBuilder.EmitVertexW:1088 emits tile
    // (tx,ty) spanning local x∈[tx,tx+1], z∈[−(ty+1),−ty] with vertex
    // y = the palette render z. The fog byte of tile (tx,ty) is the
    // lattice sample at the tile's MIN corner (native corner sampling
    // 004ca830 reads tiles (gx,gy),(gx+1,gy),(gx,gy+1),(gx+1,gy+1)) —
    // hence the (nat + 0.5)/size texel-center mapping in the shaders.
    [DisallowMultipleComponent]
    public sealed class FogOfWarPresenter : MonoBehaviour
    {
        static readonly int FowTexId = Shader.PropertyToID("_FowTex");
        static readonly int FowParamsId = Shader.PropertyToID("_FowParams");
        static readonly int FowWorldToNativeId = Shader.PropertyToID("_FowWorldToNative");

        // fogFactor decode LUT — the FUN_004ca830:133-283 per-corner law (see header).
        static readonly byte[] GrayLut = BuildGrayLut();

        static byte[] BuildGrayLut()
        {
            var lut = new byte[256];
            for (int b = 1; b < 256; b++)
            {
                if (b <= 0x80) { lut[b] = 255; continue; }         // resting/parked gray band
                int u = Math.Min(0x80, (int)((b - 0x80) * 1.3)) * 2;
                lut[b] = (byte)Math.Max(0, 255 - u);               // fade ramp; ≥0xE3 → 0
            }
            return lut;                                            // lut[0] = 0 (black corner)
        }

        // Full display swing (0→255) duration — the reveal/veil fade-in pace.
        public const float SmoothSeconds = 0.7f;

        // Small-step glide time:
        // the old chase moved at the CONSTANT full-swing speed (255/SmoothSeconds ≈ 364 u/s),
        // so a fade-tick's −4-byte target step (~10 decoded units) completed in ~30 ms and
        // the veil then idled ~370 ms until the next fog tick — a visible 2.5 Hz stutter,
        // while black reveals (full 255 swings) looked smooth. Now each texel captures a
        // per-channel speed when its target changes: speed = min(255/SmoothSeconds,
        // gap/GlideSeconds) — big swings keep the exact old pace (reveal dissolve timing
        // unchanged), small veil steps stretch over ~GlideSeconds (just past the 0.4 s fog
        // tick) so consecutive −4 steps chain into one continuous creep.
        public const float GlideSeconds = 0.45f;

        FogOfWarState? _fog;
        Texture2D? _tex;

        // World-edge render pin (FogOfWarState.IsRenderedTile / FogOfWarOverlay.PinnedBlack):
        // tiles the renderer can never draw read as permanent black in the RENDER lane no
        // matter what their sim bytes say — the native simply never draws them (RenderScape
        // inset clip 004c4de0:569-578), so their reveal state is invisible by construction.
        // Set by FogOfWarOverlay.Init (the overlay owns the predicate — it holds rendersAt/
        // heightAt); null = no pin (headless presenter-only tests keep raw-byte targets).
        Func<int, int, bool>? _pinnedBlackAt;
        byte[] _pixels = Array.Empty<byte>();    // DISPLAY values (what the GPU sees)
        byte[] _target = Array.Empty<byte>();    // decoded targets from the sim bytes

        // Per-texel chase state (keyed by the R-channel byte index i; covers r at i and g at
        // i+1). Float positions so sub-unit-per-frame glides accumulate instead of being
        // forced to ≥1 unit/frame; per-channel speeds captured at target-change time (see
        // GlideSeconds). Entries exist only while chasing — the dirty-rect discipline stands.
        struct Chase { public float R, G, RSpd, GSpd; }
        readonly System.Collections.Generic.Dictionary<int, Chase> _chasing = new();
        bool _texDirty;
        int _dl, _dt, _dr, _db;              // accumulated dirty tile rect
        int _seenPlayer = -1;
        int _seenFadeTick = -1;              // last fog tick whose fade list was re-decoded
        int _chgL, _chgT, _chgR, _chgB;      // tile rect of texels Advance moved this Pump

        public Texture2D? Texture => _tex;   // test/debug access

        // Raised after a Pump in which display G (black-indicator) texels moved — the reveal
        // chase or a snap — with the changed tile rect (inclusive). FogOfWarOverlay keys its
        // gradient-patch set to the display field (patches persist until display catches up —
        // geometry and shading driven by the same smoothed field), so it re-dirties these
        // chunks while tiles chase. G-only on purpose: the R (gray-veil) display also chases
        // continuously along the fade trail, but no overlay geometry reads R — rebuilding its
        // chunks at frame rate would be a standing perf tax for nothing.
        public event Action<int, int, int, int>? DisplayChanged;

        // Test/debug: the current DISPLAY texel (r = fogFactor, g = black indicator).
        public (byte r, byte g) DisplayAt(int tx, int ty)
        {
            if (_fog == null || (uint)tx >= _fog.Width || (uint)ty >= _fog.Height) return (0, 0);
            int i = (ty * _fog.Width + tx) * 2;
            return (_pixels[i], _pixels[i + 1]);
        }

        // Test/debug: the decoded TARGET texel the display chases. A stale target here while
        // the sim byte says otherwise = the dirty-rect union missed a rebuild (the texture
        // would stay black under covers the overlay already dropped — the "hard block" bug
        // class the sync tests certify against).
        public (byte r, byte g) TargetAt(int tx, int ty)
        {
            if (_fog == null || (uint)tx >= _fog.Width || (uint)ty >= _fog.Height) return (0, 0);
            int i = (ty * _fog.Width + tx) * 2;
            return (_target[i], _target[i + 1]);
        }

        public void Init(FogOfWarState fog)
        {
            if (_fog != null) _fog.RegionDirty -= OnRegionDirty;
            _fog = fog;
            fog.RegionDirty += OnRegionDirty;
            if (_tex == null || _tex.width != fog.Width || _tex.height != fog.Height)
            {
                if (_tex != null) DestroySafe(_tex);
                _tex = new Texture2D(fog.Width, fog.Height, TextureFormat.RG16, false, true)
                {
                    name = "FogOfWar",
                    // Bilinear (NOT point) — the GPU filter IS the native per-vertex corner
                    // interpolation (FUN_004cc040 Q8 bilerp of the 4 corner values).
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _pixels = new byte[fog.Width * fog.Height * 2];
                _target = new byte[fog.Width * fog.Height * 2];
            }
            _seenPlayer = fog.LocalPlayer;
            _seenFadeTick = fog.Tick;                       // full rebuild below covers this tick
            RebuildTargets(0, 0, fog.Width - 1, fog.Height - 1);
            Array.Copy(_target, _pixels, _target.Length);   // SNAP on init — no world fade-in
            _chasing.Clear();
            _texDirty = false;
            Pump(0f);   // push now (don't wait a frame)
        }

        // Install/remove the render-lane black pin (see _pinnedBlackAt). Re-pinning is an
        // Init/load-wiring event, so targets rebuild and the display SNAPS — a loaded world
        // must not visibly fade its edge band down to black.
        public void SetPinnedBlack(Func<int, int, bool>? pinnedAt)
        {
            _pinnedBlackAt = pinnedAt;
            if (_fog == null || _tex == null) return;
            RebuildTargets(0, 0, _fog.Width - 1, _fog.Height - 1);
            Array.Copy(_target, _pixels, _target.Length);
            _chasing.Clear();
            _texDirty = false;
            _tex.SetPixelData(_pixels, 0);
            _tex.Apply(false, false);
            DisplayChanged?.Invoke(0, 0, _fog.Width - 1, _fog.Height - 1);
        }

        // The RENDER-lane byte the texture encodes (pin-aware; sim bytes stay untouched).
        byte RenderByteAt(int tx, int ty, int p)
            => _pinnedBlackAt != null && _pinnedBlackAt(tx, ty) ? (byte)0 : _fog!.FogAt(tx, ty, p);

        // Un-wire (menu / fogless world on the same root): shaders fall back to no-fog.
        public void Clear()
        {
            if (_fog != null) _fog.RegionDirty -= OnRegionDirty;
            _fog = null;
            _pinnedBlackAt = null;   // wiring-scoped (overlay re-sets it on Init)
            Shader.SetGlobalVector(FowParamsId, Vector4.zero);
        }

        // Accumulate the dirty tile rect — RebuildTargets then walks ONLY it. A fog update at
        // fast-forward fires this 25×/s; a full 512×640 re-scan per fire was the FoW's own
        // contribution to the dug-world/fast-forward lag.
        void OnRegionDirty(int l, int t, int r, int b)
        {
            if (_fog == null) return;
            l = Math.Max(0, l); t = Math.Max(0, t);
            r = Math.Min(_fog.Width - 1, r); b = Math.Min(_fog.Height - 1, b);
            if (r < l || b < t) return;
            if (!_texDirty) { _dl = l; _dt = t; _dr = r; _db = b; _texDirty = true; return; }
            if (l < _dl) _dl = l;
            if (t < _dt) _dt = t;
            if (r > _dr) _dr = r;
            if (b > _db) _db = b;
        }

        void OnDestroy()
        {
            Clear();
            if (_tex != null) DestroySafe(_tex);
        }

        void OnDisable() => Shader.SetGlobalVector(FowParamsId, Vector4.zero);

        void LateUpdate() => Pump(Time.deltaTime);

        // Public so EditMode tests (no player loop) can drive it with an explicit dt.
        public void Pump(float dt)
        {
            if (_fog == null || _tex == null) return;
            // The fog lane is the LOCAL player's (native: renderer copies CGame+0x64 every
            // frame, 004c4de0:92); a lane switch SNAPS the whole map (another player's
            // exploration must not cross-fade from the previous one's).
            if (_fog.LocalPlayer != _seenPlayer)
            {
                _seenPlayer = _fog.LocalPlayer;
                RebuildTargets(0, 0, _fog.Width - 1, _fog.Height - 1);
                Array.Copy(_target, _pixels, _target.Length);
                _chasing.Clear();
                _texDirty = false;
                _tex.SetPixelData(_pixels, 0);
                _tex.Apply(false, false);
                DisplayChanged?.Invoke(0, 0, _fog.Width - 1, _fog.Height - 1);   // lane SNAP
            }
            else if (_texDirty)
            {
                _texDirty = false;
                RebuildTargets(_dl, _dt, _dr, _db);
            }
            // FADING texels move BETWEEN their quantized dirty-rects: FadeBlocks only raises
            // RegionDirty every 5th tick / at the 0x7C crossing (the native re-mesh law, kept
            // faithful for the sim + black-volume overlay). The pre-dirty-rect presenter
            // refreshed them incidentally (full-plane rebuild on ANY dirty); the c158834 rect
            // union stopped that, and the whole fading region's veil froze then jumped as one
            // synchronized block every ~5 updates (Play r6 "black fog renders as whole hard
            // blocks"). Re-decode the ACTIVE FADE LIST — exactly the recently-seen texels,
            // the native's own animating set — once per fog tick (bytes only change on ticks).
            if (_fog.Tick != _seenFadeTick)
            {
                _seenFadeTick = _fog.Tick;
                RefreshFadingTargets();
            }
            if (Advance(dt) > 0)
            {
                _tex.SetPixelData(_pixels, 0);
                _tex.Apply(false, false);
                if (_chgR >= _chgL) DisplayChanged?.Invoke(_chgL, _chgT, _chgR, _chgB);
            }
            bool fogActive = _fog.Enabled && _fog.ShowFog;
            Shader.SetGlobalTexture(FowTexId, _tex);
            Shader.SetGlobalVector(FowParamsId,
                new Vector4(_fog.Width, _fog.Height, fogActive ? 1f : 0f, 0f));
            // This component sits on the world root whose LOCAL space is the native render
            // frame (terrain meshes and entities are its children) — see the header.
            Shader.SetGlobalMatrix(FowWorldToNativeId, transform.worldToLocalMatrix);
        }

        // TARGET refill over the accumulated dirty rect. Only runs on RegionDirty (reveal /
        // fade crossings / remove_black_fog / fow_begin) — silent fade steps below 0x80 don't
        // change the decoded channels, exactly like the native's "no re-mesh below 0x7C" law
        // (FUN_0057e710:14). Changed texels join the chasing set for Advance.
        void RebuildTargets(int x0, int y0, int x1, int y1)
        {
            var fog = _fog!;
            int w = fog.Width, p = fog.LocalPlayer;
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    int i = (ty * w + tx) * 2;
                    byte b = RenderByteAt(tx, ty, p);
                    byte r = GrayLut[b];
                    byte g = b == 0 ? (byte)0 : (byte)255;
                    if (_target[i] != r || _target[i + 1] != g)
                    {
                        _target[i] = r;
                        _target[i + 1] = g;
                        StartChase(i);
                    }
                }
        }

        // Decode the local player's fade-list tiles against their CURRENT bytes (see Pump).
        // Cost = fade-list size (recently-seen tiles only), at most once per fog tick.
        void RefreshFadingTargets()
        {
            var fog = _fog!;
            int w = fog.Width, p = fog.LocalPlayer;
            var list = fog.FadeList(p);
            for (int k = 0; k < list.Count; k++)
            {
                int packed = list[k];
                int i = packed * 2;
                byte b = RenderByteAt(packed % w, packed / w, p);
                byte r = GrayLut[b];
                byte g = b == 0 ? (byte)0 : (byte)255;
                if (_target[i] != r || _target[i + 1] != g)
                {
                    _target[i] = r;
                    _target[i + 1] = g;
                    StartChase(i);
                }
            }
        }

        //register a texel's chase: keep the in-flight float position (or seed it from
        // the current display byte), capture per-channel speeds from the CURRENT gaps.
        // speed = min(255/SmoothSeconds, gap/GlideSeconds): full swings keep the historical
        // constant pace bit-for-bit; small veil steps stretch over GlideSeconds (header note).
        void StartChase(int i)
        {
            if (!_chasing.TryGetValue(i, out var c)) { c.R = _pixels[i]; c.G = _pixels[i + 1]; }
            c.RSpd = ChaseSpeed(_target[i] - c.R);
            c.GSpd = ChaseSpeed(_target[i + 1] - c.G);
            _chasing[i] = c;
        }

        static float ChaseSpeed(float gap)
        {
            float a = Math.Abs(gap);
            if (a <= 0f) return 0f;
            // ≥1 unit/s floor: even a 0.4-unit residue converges (exact settle invariants).
            return Math.Min(255f / SmoothSeconds, Math.Max(a / GlideSeconds, 1f));
        }

        static readonly System.Collections.Generic.List<int> s_done = new();
        static readonly System.Collections.Generic.List<int> s_keys = new();

        // Move display values toward targets; returns how many texels changed this call and
        // accumulates the tile rect of G-channel moves into _chg* (the DisplayChanged
        // payload; empty = _chgR < _chgL). R-only moves upload the texture but fire no event.
        // Float positions advance by their captured per-channel speed; bytes are the rounded
        // projection, and a texel leaves the set only when both floats SIT on their targets —
        // so the display still lands byte-exact (the sync tests' settle law).
        int Advance(float dt)
        {
            if (_chasing.Count == 0) return 0;
            int changed = 0;
            int w = _fog!.Width;
            _chgL = int.MaxValue; _chgT = int.MaxValue; _chgR = -1; _chgB = -1;
            s_done.Clear();
            s_keys.Clear();
            s_keys.AddRange(_chasing.Keys);          // value writes during enumeration are unsafe
            foreach (int i in s_keys)
            {
                var c = _chasing[i];
                c.R = StepF(c.R, _target[i], c.RSpd * dt);
                c.G = StepF(c.G, _target[i + 1], c.GSpd * dt);
                _chasing[i] = c;
                byte r = (byte)Math.Round(c.R);
                byte g = (byte)Math.Round(c.G);
                if (r != _pixels[i] || g != _pixels[i + 1])
                {
                    if (g != _pixels[i + 1])
                    {
                        int packed = i >> 1;
                        int tx = packed % w, ty = packed / w;
                        if (tx < _chgL) _chgL = tx;
                        if (tx > _chgR) _chgR = tx;
                        if (ty < _chgT) _chgT = ty;
                        if (ty > _chgB) _chgB = ty;
                    }
                    _pixels[i] = r; _pixels[i + 1] = g; changed++;
                }
                if (c.R == _target[i] && c.G == _target[i + 1]) s_done.Add(i);
            }
            foreach (int i in s_done) _chasing.Remove(i);
            return changed;
        }

        static float StepF(float cur, float tgt, float maxMove) =>
            cur < tgt ? Math.Min(cur + maxMove, tgt)
                      : cur > tgt ? Math.Max(cur - maxMove, tgt) : cur;

        static void DestroySafe(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
