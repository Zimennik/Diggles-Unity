using System;
using UnityEngine;

namespace Diggles.Runtime.World
{
    // Per-tile baselight — the native underground-darkness law (Docs/TUNNEL_TORCH_LIGHTING_RE.md §6).
    //
    // The original recomputes a per-tile base-light byte (tile+0x07) through the
    // "MapValidator:BASELIGHT" dirty-rect channel (FUN_005a46d0:273-327 → FUN_005906a0):
    // • Above the sky line (fineY < set_light_begin×8, DAT_007683d8) an upward scan
    // (24 steps of 2 fine ≈ 6 tiles) that finds an OPEN cell (fill < 0xD) → 0x14 (20)
    // = full daylight. Blocked → falls through to the underground term (sunless nook
    // under an overhang).
    // • Underground: a corner-sampled slope/occlusion sum — flat open tunnel ≈ 4,
    // corners against up-left rock → 0 (clamped). So unlit underground renders at
    // ~4/20 = 20% and point lights (torches / LightSrc markers) add on top.
    //
    // Port law (documented tile-scale rendition — the native samples 8×8 fine cells, the
    // port's fill source is the tile nibble):
    // sky: ty < LightBegin && any fill(tx, ty-k) < 0xD for k=0..6 → 20
    // otherwise: acc = Σ_{j=1..4} 3·clamp(own + j − fill(tx−1, ty−1), −8, 0)
    // b = clamp(acc/2 + 4, 0, 20)
    // (own = fill(tx,ty); the native's 12 diagonal fine samples land in the up-left
    // neighbourhood — the port reads the up-left neighbour tile for all of them.)
    //
    // The texture texel (tx,ty) holds b/20 at the tile's MIN corner — the same lattice
    // convention as the fog texture, so the shaders' bilinear sample reproduces the native
    // per-vertex corner interpolation (see FogOfWarPresenter header).
    public sealed class BaseLightMap
    {
        public const int Daylight = 20;      // 0x14 (FUN_005906a0:379)
        public const int FlatBase = 4;       // acc/2 + 4 with acc==0 (FUN_005906a0:333)
        public const int SkyScanTiles = 6;   // 24 steps × 2 fine (FUN_005906a0:335-378)
        public const int OpenFill = 0xD;     // fill < 0xD = open for the sky scan (:373)

        public readonly int Width, Height;
        readonly byte[] _light;              // raw 0..20 per tile
        readonly Func<int, int, int> _fillAt;

        // Tcl set_light_begin (DAT_007683d8; default -10000 = whole map is underground).
        public int LightBegin = -10000;

        // Inclusive tile rect whose values changed (presenter re-uploads).
        public event Action<int, int, int, int>? RegionChanged;

        public BaseLightMap(int width, int height, Func<int, int, int> fillAt)
        {
            Width = width; Height = height; _fillAt = fillAt;
            _light = new byte[width * height];
        }

        public byte At(int tx, int ty)
            => (uint)tx < (uint)Width && (uint)ty < (uint)Height ? _light[ty * Width + tx] : (byte)0;

        public bool SkyLit(int tx, int ty) => At(tx, ty) >= Daylight;

        public void RecomputeAll() => RecomputeRect(0, 0, Width - 1, Height - 1);

        // A dig carve changes fill nibbles; a tile's value depends on (tx-1,ty-1) and the
        // sky scan on the 6 rows above — callers should pad their rect (the runtime wiring
        // pads by 1 laterally and SkyScanTiles downward; RecomputeRect clamps).
        public void RecomputeRect(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);
            if (x1 < x0 || y1 < y0) return;
            bool changed = false;
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    byte v = ComputeTile(tx, ty);
                    int i = ty * Width + tx;
                    if (_light[i] != v) { _light[i] = v; changed = true; }
                }
            if (changed) RegionChanged?.Invoke(x0, y0, x1, y1);
        }

        byte ComputeTile(int tx, int ty)
        {
            // Sky branch — only for rows above the light-begin line (FUN_005906a0:334).
            if (ty < LightBegin)
            {
                for (int k = 0; k <= SkyScanTiles; k++)
                {
                    int y = ty - k;
                    if (y < 0) break;                      // scanned off the top: open sky
                    if (_fillAt(tx, y) < OpenFill) return Daylight;
                }
                if (ty - SkyScanTiles - 1 < 0) return Daylight;   // ran off the top = sky
            }
            // Underground / overhang term (FUN_005906a0:185-333 at tile-corner scale).
            int own = _fillAt(tx, ty);
            int upleft = _fillAt(tx - 1, ty - 1);
            int acc = 0;
            for (int j = 1; j <= 4; j++)
                acc += 3 * Math.Clamp(own + j - upleft, -8, 0);
            int b = acc / 2 + FlatBase;
            return (byte)Math.Clamp(b, 0, Daylight);
        }
    }

    // Uploads the BaseLightMap as a global R8 texture the Diggles shaders sample
    // (same world→native mapping + corner-texel law as the fog texture; see
    // FogOfWarPresenter's header for the frame proof). Sits on the world root.
    //
    // _BaseLightTex R8, bilinear, clamp — texel = b/20 (1.0 = daylight);
    // _BaseLightParams (Width, Height, active, minFactor);
    // _BaseLightToNative world → terrain-local (the component's transform).
    [DisallowMultipleComponent]
    public sealed class BaseLightPresenter : MonoBehaviour
    {
        static readonly int TexId = Shader.PropertyToID("_BaseLightTex");
        static readonly int ParamsId = Shader.PropertyToID("_BaseLightParams");
        static readonly int ToNativeId = Shader.PropertyToID("_BaseLightToNative");

        // User-facing darkness floor: the sampled base factor never drops below this
        // (max in the shaders via _BaseLightParams.w — live-tweakable, no texture
        // re-upload). 0 = the native law verbatim (pitch-black up-left corners); 0.6 is
        // the Play-calibrated default.
        // Per-install override: diggles_config.json "baseLightMin"
        // (DigglesConfig.ResolveBaseLightMin, wired by DigglesWorldRuntime);
        // live tweak: this field on the world root in the Inspector.
        [Range(0f, 1f)] public float MinFactor = 0.6f;

        BaseLightMap? _map;
        Texture2D? _tex;
        byte[] _pixels = Array.Empty<byte>();
        bool _dirty;
        int _dl, _dt, _dr, _db;

        public Texture2D? Texture => _tex;   // test/debug access

        public void Init(BaseLightMap map)
        {
            if (_map != null) _map.RegionChanged -= OnRegionChanged;
            _map = map;
            map.RegionChanged += OnRegionChanged;
            if (_tex == null || _tex.width != map.Width || _tex.height != map.Height)
            {
                if (_tex != null) DestroySafe(_tex);
                _tex = new Texture2D(map.Width, map.Height, TextureFormat.R8, false, true)
                {
                    name = "BaseLight",
                    filterMode = FilterMode.Bilinear,   // bilinear = native corner interpolation
                    wrapMode = TextureWrapMode.Clamp,
                };
                _pixels = new byte[map.Width * map.Height];
            }
            RefreshRect(0, 0, map.Width - 1, map.Height - 1);
            _tex.SetPixelData(_pixels, 0);
            _tex.Apply(false, false);
            _dirty = false;
            Push();
        }

        // Un-wire (menu / world without terrain): shaders fall back to full brightness.
        public void Clear()
        {
            if (_map != null) _map.RegionChanged -= OnRegionChanged;
            _map = null;
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
        }

        void OnRegionChanged(int l, int t, int r, int b)
        {
            if (!_dirty) { _dl = l; _dt = t; _dr = r; _db = b; _dirty = true; return; }
            if (l < _dl) _dl = l;
            if (t < _dt) _dt = t;
            if (r > _dr) _dr = r;
            if (b > _db) _db = b;
        }

        void RefreshRect(int l, int t, int r, int b)
        {
            if (_map == null) return;
            for (int ty = t; ty <= b; ty++)
                for (int tx = l; tx <= r; tx++)
                    _pixels[ty * _map.Width + tx] =
                        (byte)(_map.At(tx, ty) * 255 / BaseLightMap.Daylight);
        }

        void LateUpdate()
        {
            if (_map == null || _tex == null) return;
            if (_dirty)
            {
                _dirty = false;
                RefreshRect(Math.Max(0, _dl), Math.Max(0, _dt),
                            Math.Min(_map.Width - 1, _dr), Math.Min(_map.Height - 1, _db));
                _tex.SetPixelData(_pixels, 0);
                _tex.Apply(false, false);
            }
            Push();
        }

        void Push()
        {
            if (_map == null || _tex == null) return;
            Shader.SetGlobalTexture(TexId, _tex);
            Shader.SetGlobalVector(ParamsId, new Vector4(_map.Width, _map.Height, 1f, MinFactor));
            Shader.SetGlobalMatrix(ToNativeId, transform.worldToLocalMatrix);
        }

        // Re-push the shader params after a live MinFactor change (the DigglesTuning
        // inspector component drives this — no texture re-upload needed).
        public void PushParams() => Push();

        void OnDisable() => Shader.SetGlobalVector(ParamsId, Vector4.zero);

        void OnDestroy()
        {
            Clear();
            if (_tex != null) DestroySafe(_tex);
        }

        static void DestroySafe(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }
    }
}
