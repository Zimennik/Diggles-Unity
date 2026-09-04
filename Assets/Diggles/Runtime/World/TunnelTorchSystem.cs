using System;
using System.Collections.Generic;

namespace Diggles.Runtime.World
{
    // Tunnel wall-torch auto-spawn — faithful port of the native glow-drop → Misc_Light
    // pipeline (full RE: Docs/TUNNEL_TORCH_LIGHTING_RE.md; all addresses Diggles.exe):
    //
    // • Gnomes with the Autolight flag (obj+0x3d0 bit 0x20; zwerg.tcl:156 `set_autolight
    // this 1`) drop a "glow point" near themselves every 16th render tick when owned by
    // the LOCAL player (FUN_005cbf70:81-107 → FUN_005cd520): position = feet + x jitter
    // ±0.5 tile, raised 0.2..1.2 tiles up the wall, only inside the light rect and only
    // when the tile is not solid rock (fill < 0xF).
    // • Glow points live in a small pending queue (light singleton +0x12000, entries
    // {x,y,z,age}); a new drop within manhattan distance < 10 tiles of a pending entry
    // is swallowed (FUN_005912a0 — age clamped ≤10, no new entry).
    // • The CObjMgr pass (FUN_005d4250:129-303) ages every entry +1 per pass; an entry
    // hitting age==3 spawns a REAL `Misc_Light` object (fackel model + class_light r=6
    // + flame particle, emitter.tcl:3-11) at the glow position if the placement law
    // passes; age==6 removes the entry.
    // • Placement (FUN_00590e60): inside the light rect with a 2.0-tile margin
    // (DAT_00737330); NOT above the sky line (y < DAT_007683d8 = Tcl set_light_begin →
    // reject — torches are underground only); not in a sky-lit dug shaft (FUN_004753f0,
    // approximated here by the BaseLightMap daylight test); and NOT within
    // distSq < radius + 4.0 (DAT_007374a0) of ANY existing light — that lopsided
    // comparison is the literal native code and yields the ~3.2-tile torch spacing.
    //
    // "Lights up when a gnome first passes / goes out when he leaves" is EMERGENT: the
    // torch is an ordinary object, so the existing fog-of-war gates run it (model ≥0x80,
    // flame ≥0x81, and the full gray veil lerp erases its terrain light) — no extra law.
    // Torches persist forever (native has no despawn; the CObj dtor only frees the light
    // slot, 005c8e90:245-248).
    //
    // This class is the POCO law core (headless-testable). Integration (who calls
    // DropAttempt/Tick, how Misc_Light is spawned) lives in DigglesTclEngine.TunnelTorchTick
    // + DigglesWorldRuntime wiring. PORT CADENCE DEVIATION (documented in the RE doc §8.1):
    // native drops every 16 render frames and ages once per frame; the port runs on the
    // 10 Hz sim tick — drop every 5th tick (≈0.5 s, same wall-clock rate), age once per tick
    // (spawn ≈0.3 s after the drop vs native ≈0.1 s).
    public sealed class TunnelTorchSystem
    {
        // --- native constants (Docs/TUNNEL_TORCH_LIGHTING_RE.md §2-4) ---
        public const float DedupeManhattan = 10f;   // DAT_00737534 (FUN_005912a0:40-41)
        public const int AgeCap = 10;               // FUN_005912a0:49-58 (clamp, no refresh)
        public const int SpawnAge = 3;              // FUN_005d4250:165
        public const int RemoveAge = 6;             // FUN_005d4250:280
        public const float SpacingAdd = 4f;         // DAT_007374a0 (FUN_00590e60:149)
        public const float BorderMargin = 2f;       // DAT_00737330 (FUN_00590e60:42-45)
        public const float DropRaise = 0.2f;        // DAT_00737054 (FUN_005cd520:25)
        public const float JitterHalf = 0.5f;       // DAT_0073708c (FUN_005cd520:29)
        public const int SolidFill = 0xF;           // FUN_005cd520:34 (fill < 0xF gate)
        public const int DropPeriodTicks = 5;       // port stand-in for "every 16th render tick"
        public const string TorchClass = "Misc_Light";  // FUN_005d4250:130

        public struct Glow
        {
            public float X, Y, Z;   // native tile floats (X east, Y row-down, Z depth)
            public int Age;
        }

        readonly List<Glow> _glow = new();
        readonly List<Glow> _spawnNow = new();   // collected per Tick, drained after the sweep

        public readonly int Width, Height;

        // Tcl set_light_begin (DAT_007683d8; default -10000 = "no sky", FUN_00594160:258).
        // Placement rejects y < LightBegin (rows ABOVE the sky line — FUN_00590e60:45).
        public int LightBegin = -10000;

        // Tile fill nibble 0..15 (OOB = 15/solid) — the drop-site rock gate.
        readonly Func<int, int, int> _fillAt;

        // Any existing light (class_light entities incl. earlier torches) with
        // distSq(x,y) < radius + 4? (FUN_00590e60:122-165 over the spatial buckets.)
        public Func<float, float, bool>? NearExistingLight;

        // Sky-lit dug-tile reject (FUN_004753f0 daylight-shaft list, approximated by the
        // BaseLightMap daylight value — RE doc §8.6).
        public Func<int, int, bool>? SkyLitAt;

        // Spawns the Misc_Light object (engine-side: the normal `new` pipeline).
        public Action<float, float, float>? SpawnTorch;

        // rand — injectable for deterministic tests (native FUN_006d46ea/32768).
        public Func<double> Rand = () => _sharedRand.NextDouble();
        static readonly Random _sharedRand = new Random();

        public int TickCounter;                     // drop stagger (handle % DropPeriodTicks)

        public TunnelTorchSystem(int width, int height, Func<int, int, int> fillAt)
        {
            Width = width; Height = height; _fillAt = fillAt;
        }

        public int GlowCount => _glow.Count;
        public Glow GlowAt(int i) => _glow[i];

        // FUN_005cd520 (byte-verified disasm 0x5cd520,): jitter the drop point
        // around the carrier, gate on bounds + "a wall must exist here" (center fill < 15 —
        // fully solid AND fully open both yield no torch), then z = the WALL-FACE depth:
        // MAX over the ±1 sub-cell cross of fill bytes, samples ==15 excluded, clamped ≤14.
        // The gnome's own z (obj+0x14) is NEVER read. FUN_005912a0 then dedupes.
        public const float DropCross = 0.125f;                    // ±1 sub-cell of 8-per-tile
        public void DropAttempt(float ox, float oy, float oz)
        {
            float raise = (float)Rand();                          // rand/32768 (0..1)
            float x = ox + (float)Rand() - JitterHalf;            // (rand − 0.5) + x
            float y = oy - DropRaise - raise;                     // up the wall 0.2..1.2
            int tx = (int)Math.Floor(x), ty = (int)Math.Floor(y); // fine ROUND(v*8) >> 3
            if (tx < 0 || tx >= Width || ty < 0 || ty >= Height) return;   // light rect (+0x94..0xa0)
            int d = _fillAt(tx, ty);
            if (d >= SolidFill) return;                           // center must be a REAL wall (<15)
            void Fold(float sx, float sy)
            {
                int v = _fillAt((int)Math.Floor(sx), (int)Math.Floor(sy));
                if (v > d && v < SolidFill) d = v;                // MAX, 15s excluded (cmovb law)
            }
            Fold(x - DropCross, y); Fold(x + DropCross, y);
            Fold(x, y - DropCross); Fold(x, y + DropCross);
            if (d > 14) d = 14;                                   // defensive clamp (mov eax,14/cmova)
            AddGlow(x, y, d);
        }

        // FUN_005912a0 verbatim: manhattan proximity swallows the drop (age clamped to ≤10 —
        // NOT refreshed); otherwise append with age 0.
        public void AddGlow(float x, float y, float z)
        {
            for (int i = 0; i < _glow.Count; i++)
            {
                var g = _glow[i];
                if (Math.Abs(x - g.X) + Math.Abs(y - g.Y) < DedupeManhattan)
                {
                    if (g.Age > AgeCap) { g.Age = AgeCap; _glow[i] = g; }
                    return;
                }
            }
            _glow.Add(new Glow { X = x, Y = y, Z = z, Age = 0 });
        }

        // FUN_005d4250:139-303 — one manager pass: age ALL entries first, then the
        // spawn/remove sweep. Spawn callbacks are drained after the sweep so a spawn can't
        // mutate the list mid-iteration.
        public void Tick()
        {
            for (int i = 0; i < _glow.Count; i++)
            {
                var g = _glow[i]; g.Age++; _glow[i] = g;
            }
            for (int i = 0; i < _glow.Count; i++)
            {
                var g = _glow[i];
                if (g.Age == SpawnAge && PlacementOk(g.X, g.Y))
                    _spawnNow.Add(g);
                if (g.Age >= RemoveAge)
                {
                    _glow.RemoveAt(i);
                    i--;
                }
            }
            if (_spawnNow.Count > 0)
            {
                foreach (var g in _spawnNow) SpawnTorch?.Invoke(g.X, g.Y, g.Z);
                _spawnNow.Clear();
            }
        }

        // FUN_00590e60 — the "may a torch stand here" law.
        public bool PlacementOk(float x, float y)
        {
            if (x < BorderMargin || x >= Width - BorderMargin) return false;   // float rect ± 2.0
            if (y < BorderMargin || y >= Height - BorderMargin) return false;
            if (y < LightBegin) return false;                     // above the sky line
            int tx = (int)Math.Floor(x + JitterHalf), ty = (int)Math.Floor(y + JitterHalf); // (x+0.5) tile probe (:46)
            if (SkyLitAt != null && SkyLitAt(tx, ty)) return false;   // daylight shaft (FUN_004753f0 approx.)
            if (NearExistingLight != null && NearExistingLight(x, y)) return false;  // distSq < r+4
            return true;
        }

        public void Clear() { _glow.Clear(); _spawnNow.Clear(); }

        // ---------------------------------------------------------------- wall z-settle
        // class_snaptowall (obj flag 0x20000, ctor FUN_005c7b70:80-82) — the LIVE settle law
        // FUN_005cbf70:125-176: every 16th tick, z = min(fill over the fine cross
        // {(0,0),(±2,0),(0,±2)} around the object, ±2 fine = ±¼ tile) + 0.01 (DAT_00736fe0).
        // Fill semantics: rock occupies native z ∈ [0, fill]; the exposed wall face of the
        // tile sits AT z = fill (deeper dig ⇒ smaller fill ⇒ face deeper into the screen) —
        // so the settled torch hangs ON the face, and TRACKS it when later digs (a cave over
        // the tunnel) recede the wall. x/y are never touched — the native has NO side-wall
        // snap. Port note: the sampler here is the TILE nibble (the port's fill source), so
        // the ±¼-tile cross only crosses into a neighbour tile when the anchor sits within
        // ¼ tile of its edge — the same law at tile resolution.
        public const float SettleLift = 0.01f;          // DAT_00736fe0
        public const float SettleCross = 0.25f;         // ±2 fine of 8-per-tile
        public const int SettlePeriod = 16;             // (obj+0xdc ^ tick) & 0xf == 0 (:128)

        public static float SettleZ(Func<int, int, int> fillAt, float x, float y)
        {
            int m = fillAt((int)Math.Floor(x), (int)Math.Floor(y));
            m = Math.Min(m, fillAt((int)Math.Floor(x - SettleCross), (int)Math.Floor(y)));
            m = Math.Min(m, fillAt((int)Math.Floor(x + SettleCross), (int)Math.Floor(y)));
            m = Math.Min(m, fillAt((int)Math.Floor(x), (int)Math.Floor(y - SettleCross)));
            m = Math.Min(m, fillAt((int)Math.Floor(x), (int)Math.Floor(y + SettleCross)));
            return m + SettleLift;
        }

        // Instance form over the system's own fill source. Bounds gate = the native margin-4
        // fine (= ½ tile) rect check (FUN_005cbf70:127-135 via FUN_00409450(...,4)); out of
        // range ⇒ NaN = "don't move".
        public float SettleZAt(float x, float y)
        {
            const float margin = 0.5f;
            if (x < margin || x >= Width - margin || y < margin || y >= Height - margin)
                return float.NaN;
            return SettleZ(_fillAt, x, y);
        }
    }
}
