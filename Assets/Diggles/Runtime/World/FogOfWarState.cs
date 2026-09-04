using System;
using System.Collections.Generic;

namespace Diggles.Runtime.World
{
    // Faithful port of the original CFogOfWar (Diggles.exe 0x0057dfc0..0x00580610; full RE:
    // Docs/FOG_OF_WAR_RE.md). One instance per world (native *(World+0x20)).
    //
    // The fog map is ONE persistent byte per tile per player (native tile+0x34..0x3B):
    // 0x00 = never seen (BLACK), 0x01 = explored floor (GRAY), 0x02..0xFE = recently seen,
    // fading -4 per update, 0xFF = currently visible.
    // There is NO separate current-visibility layer. Renderers use the local player's lane:
    // 0 → black volume; 1..0x7F → full gray; 0x80..0xE2 → fade ramp
    // fogFactor=(255-2*min(128,floor((b-0x80)*1.3)))/255; ≥0xE3 → clear (FUN_004ca830).
    //
    // Update cadence: native = every 4th CGameThread::SrvDoThink ((game+0x6c)&3==3,
    // 006b4a30:393-421); the port calls Begin/Reveal/EndUpdate from DigglesGameSim on every
    // 4th 10 Hz think tick.
    public sealed class FogOfWarState
    {
        [Serializable]
        public sealed class SaveState
        {
            public int Width;
            public int Height;
            public byte[] Fog = Array.Empty<byte>();
            public int[] FowBegin = Array.Empty<int>();
            public int[] FadeOffsets = Array.Empty<int>();
            public int[] FadeItems = Array.Empty<int>();
            public int[] ExploredRect = Array.Empty<int>();
            public int Tick;
            public int LocalPlayer;
            public bool Enabled;
            public bool ShowFog;
            public bool RevealAllPending;
            public int RoundRobin;
            public int CacheVersion;
        }

        public const int Players = 8;                 // native: 8 owner lanes everywhere
        public const byte GrayFloor = 1;              // FadeBlocks clamp (0057e710:101-105)
        public const int FadeStep = 4;                // byte -= 4 per update (0057e710:100)
        public const int RockPenetration = 3;         // rays run 3 tiles into solid then stop (0057e120:152-162)
        public const int SolidFill = 0xF;             // detail fill nibble that blocks reveal rays (>0xE)

        public readonly int Width, Height;

        // (ty*W+tx)*8 + player — mirrors the native per-tile layout tile+0x34[player].
        readonly byte[] _fog;

        // this+0x0c int[8] — tile row where fog begins; rows above are permanently revealed
        // and never enter the fade lists (00580610 + the reveal gate 0057e120:184-190).
        readonly int[] _fowBegin = new int[Players];

        // this+0x2c 8×{ptr,count} — per-player active fade lists; entries = packed (ty*W+tx).
        // A tile is in the list iff its byte is in 2..0xFF and it sits below the fowBegin row.
        readonly List<int>[] _fade = new List<int>[Players];

        // game+0xa328 — per-player explored bounding rect {minX,minY,maxX,maxY} in tiles,
        // extended by every reveal; 0x8000 sentinels when empty (00558770:744). Minimap/camera food.
        public readonly int[][] ExploredRect = new int[Players][];

        public int Tick;                              // this+0x04
        public int LocalPlayer;                       // this+0x00 (native caches game+0x64 per update)
        public bool Enabled = true;                   // game+0x8e (cleared by -nofow); when false the
                                                      // pending flag floods everything visible once.
        public bool ShowFog = true;                   // game+0x59c (Tcl show_fogofwar). Dead in the
                                                      // shipped exe (write-only); the PORT honors it
                                                      // as a render gate (cutscene reveal).
        bool _revealAllPending = true;                // this+0x08, set at world load (005931f0:253)
        int _roundRobin;                              // DAT_00c3c9b4 (mod-6 object stagger)

        // GetMaxFow caches on entities are invalidated by bumping this every update
        // (native FUN_0057fde0 writes -1 into every object's obj+0x6c[16]).
        public int CacheVersion;

        // Tile fill nibble 0..15 (detail cell byte0 & 0xF; out-of-bounds = 15/solid).
        readonly Func<int, int, int> _fillAt;

        // Max terrain fill around a tile center (FUN_00597460: max detail byte over the 8×8 fine
        // window x*8-4..x*8+3 — spans the 4 tiles around the corner; OOB = 0xF). Injected so the
        // sim can wire the real voxel data; the default approximates with the 4 tile fill nibbles.
        readonly Func<int, int, int> _maxFillAround;

        // NewVisibleBlock dig-mark reconciliation hooks (tile+0x08 owner nibbles — the port's
        // DiggableTerrain dig marks). Optional; see OnFirstSight below.
        public Func<int, int, int, int>? DigNibbleAt;          // (x, y, player) → nibble
        public Action<int, int, int>? ClearDigNibble;          // (x, y, player)
        public Func<int, int, int>? TileDigState;              // tile+0x18 & 3 (0=solid..3=fully dug)

        // Raised when any byte of the LOCAL player's lane changes category the renderer cares
        // about (reveal 0→0xFF, fade crossings). Tile-rect, inclusive. Replaces the native
        // dirty-region masks 0x50/0x10 (FUN_005a3fd0) — the Unity presenter re-uploads the fog
        // texture and the black-overlay rebuilds these tiles.
        public event Action<int, int, int, int>? RegionDirty;

        public FogOfWarState(int width, int height, Func<int, int, int> fillAt,
                             Func<int, int, int>? maxFillAround = null)
        {
            Width = width; Height = height;
            _fog = new byte[width * height * Players];
            _fillAt = fillAt;
            _maxFillAround = maxFillAround ?? DefaultMaxFillAround;
            Array.Fill(_fowBegin, -1);
            for (int p = 0; p < Players; p++)
            {
                _fade[p] = new List<int>();
                ExploredRect[p] = new[] { 0x8000, 0x8000, -0x8000, -0x8000 };
            }
        }

        // FUN_00597460 approximation over tile fill nibbles: the fine window around the corner of
        // tile (x,y) touches tiles (x-1..x, y-1..y). Exact per-fine data can be injected instead.
        int DefaultMaxFillAround(int x, int y)
        {
            int m = 0;
            for (int dy = -1; dy <= 0; dy++)
                for (int dx = -1; dx <= 0; dx++)
                    m = Math.Max(m, _fillAt(x + dx, y + dy));
            return m;
        }

        public byte FogAt(int tx, int ty, int player)
            => (uint)tx < (uint)Width && (uint)ty < (uint)Height && (uint)player < Players
               ? _fog[(ty * Width + tx) * Players + player] : (byte)0;

        // The player's ACTIVE fade list (packed ty*W+tx) — every recently-seen tile (byte
        // 2..0xFF below the sky line) until FadeBlocks clamps it to the gray floor. Exposed
        // for the presenter: the veil of these tiles keeps moving BETWEEN the quantized
        // dirty-rects (every 5th tick / 0x7C crossing — the native re-mesh law, kept for the
        // overlay), so the texture feed re-decodes exactly this set per fog tick.
        public IReadOnlyList<int> FadeList(int player)
            => (uint)player < Players ? _fade[player] : (IReadOnlyList<int>)Array.Empty<int>();

        public int FowBegin(int player) => (uint)player < Players ? _fowBegin[player] : 0;

        // ---------------------------------------------------------------- world-edge render band
        // CScapeRenderer::RenderScape hard-clips its per-tile pass (mesh build/reuse AND the
        // black front covers) to the world tile rect INSET: a tile is drawn only when
        // minX+2 < tx < maxX−2 AND minY+2 < ty < maxY−2 (004c4de0:569-578; tiles
        // without a terrain record are skipped the same way,:579). The outer band — 3 tiles
        // at the min edges, 2 at the max edges — is NEVER rendered and never receives covers,
        // so past the last drawable tile the native screen shows only the black background:
        // the raw world edge is unseeable by construction (it reads as permanent black fog).
        // The REVEAL side has NO matching clamp — RemoveFog bounds each ray step by the FULL
        // rect (0057e120:137-140) and band bytes still reveal/fade in sim (fade lists,
        // explored rect, GetMaxFow) — so this is a RENDER predicate for the presenter/overlay
        // (FogOfWarOverlay.PinnedBlack), NOT a write clamp on the fog bytes.
        public const int RenderBandMin = 3;   // first drawn tile index (min+2 < t)
        public const int RenderBandMax = 2;   // undrawn tiles at each max edge (t < max−2)

        public bool IsRenderedTile(int tx, int ty) =>
            tx >= RenderBandMin && tx < Width - RenderBandMax &&
            ty >= RenderBandMin && ty < Height - RenderBandMax;

        public SaveState CaptureSaveState()
        {
            var offsets = new int[Players + 1];
            int fadeCount = 0;
            for (int player = 0; player < Players; player++)
            {
                offsets[player] = fadeCount;
                fadeCount += _fade[player].Count;
            }
            offsets[Players] = fadeCount;
            var fadeItems = new int[fadeCount];
            int cursor = 0;
            for (int player = 0; player < Players; player++)
                foreach (int item in _fade[player]) fadeItems[cursor++] = item;
            var explored = new int[Players * 4];
            for (int player = 0; player < Players; player++)
                Array.Copy(ExploredRect[player], 0, explored, player * 4, 4);
            return new SaveState
            {
                Width = Width,
                Height = Height,
                Fog = (byte[])_fog.Clone(),
                FowBegin = (int[])_fowBegin.Clone(),
                FadeOffsets = offsets,
                FadeItems = fadeItems,
                ExploredRect = explored,
                Tick = Tick,
                LocalPlayer = LocalPlayer,
                Enabled = Enabled,
                ShowFog = ShowFog,
                RevealAllPending = _revealAllPending,
                RoundRobin = _roundRobin,
                // Port-only presentation-invalidation counter (restore bumps it deliberately) —
                // never native state; canonical captures always emit the deterministic 0.
                CacheVersion = 0,
            };
        }

        public void RestoreSaveState(SaveState state)
        {
            if (state.Width != Width || state.Height != Height || state.Fog.Length != _fog.Length ||
                state.FowBegin.Length != Players || state.FadeOffsets.Length != Players + 1 ||
                state.ExploredRect.Length != Players * 4 || state.FadeOffsets[0] != 0 ||
                state.FadeOffsets[Players] != state.FadeItems.Length)
                throw new ArgumentException("Saved fog-of-war dimensions are invalid.", nameof(state));
            for (int player = 0; player < Players; player++)
                if (state.FadeOffsets[player] > state.FadeOffsets[player + 1] ||
                    state.FadeOffsets[player] < 0 || state.FadeOffsets[player + 1] > state.FadeItems.Length)
                    throw new ArgumentException("Saved fog-of-war fade lists are invalid.", nameof(state));

            Array.Copy(state.Fog, _fog, _fog.Length);
            Array.Copy(state.FowBegin, _fowBegin, Players);
            for (int player = 0; player < Players; player++)
            {
                _fade[player].Clear();
                for (int i = state.FadeOffsets[player]; i < state.FadeOffsets[player + 1]; i++)
                    _fade[player].Add(state.FadeItems[i]);
                Array.Copy(state.ExploredRect, player * 4, ExploredRect[player], 0, 4);
            }
            Tick = state.Tick;
            LocalPlayer = state.LocalPlayer;
            Enabled = state.Enabled;
            ShowFog = state.ShowFog;
            _revealAllPending = state.RevealAllPending;
            _roundRobin = state.RoundRobin;
            CacheVersion = state.CacheVersion + 1;
            RegionDirty?.Invoke(0, 0, Width - 1, Height - 1);
        }

        // ------------------------------------------------------------------ update loop

        // UpdateFoW head (0057f5c0): tick++, refresh local player, fade all 8 lanes, advance the
        // mod-6 stagger. The caller then feeds every owned entity through UpdateRevealer and
        // finishes with EndUpdate.
        public void BeginUpdate(int localPlayer)
        {
            Tick++;
            LocalPlayer = localPlayer;
            if (!Enabled)
            {
                // -nofow path (0057f5c0:28): one-shot full reveal after world load.
                if (_revealAllPending) RevealAll();
                return;
            }
            _revealAllPending = false;
            for (int p = 0; p < Players; p++) FadeBlocks(p);
            _roundRobin = (_roundRobin + 1) % 6;
        }

        // UpdateFoW tail (0057f5c0:289-301): the native quick-sorts one player's fade list
        // (render cache locality only — no semantic effect, not ported) and invalidates every
        // object's GetMaxFow cache (FUN_0057fde0:60-71) — ported as a version bump.
        public void EndUpdate() => CacheVersion++;

        // One object's reveal step (0057f5c0:134-198). Positions in native tile units (floats).
        // sizeX/sizeY are the STORED values (already ×8 = fine units; exactly -1 = disabled).
        // slotId = the object's stagger id (native obj+0xdc). transport: -1 none, 0 = elevator
        // (reveal down travelLen), 1 = ladder (reveal up travelLen) — native obj+0x3ec.
        // lastX/lastY start at 0 like the zero-filled native obj+0x26c — a fresh revealer away
        // from the origin "crosses a tile" on its first update and reveals immediately.
        // Returns true when a reveal ran.
        public bool UpdateRevealer(float x, float y, ref float lastX, ref float lastY,
                                   int sizeX, int sizeY, int owner, int slotId,
                                   int transport = -1, float transportLen = 0f)
        {
            if (!Enabled) return false;
            if ((uint)owner >= Players) return false;
            // Native fine coords: xF=(int)(x*8), yF=-(int)(y*-8) — i.e. trunc toward zero on the
            // negated product; for positive tile coords both reduce to trunc(x*8) with the y form
            // rounding up at exact negatives (constants 0073752c=8.0 / 007377b0=-8.0).
            int xF = (int)(x * 8f);
            int yF = -(int)(y * -8f);
            int yTop = yF - 12;
            int lxF = (int)(lastX * 8f);
            int lyF = -(int)(lastY * -8f) - 12;

            bool moved = (xF + (xF >> 31 & 7)) >> 3 != (lxF + (lxF >> 31 & 7)) >> 3
                      || (yTop + (yTop >> 31 & 7)) >> 3 != (lyF + (lyF >> 31 & 7)) >> 3;
            bool due = _roundRobin == ((slotId % 6) + 6) % 6;
            lastX = x; lastY = y;                       // native stores last pos for EVERY owned obj
            if (sizeX == -1 || (!moved && !due)) return false;

            if (transport == 0)
            {
                // Elevator (006792c0 writes *tl=0): reveal DOWN the shaft (0057f5c0:178-186).
                int len = (int)Math.Round(transportLen * 8f + 16f, MidpointRounding.AwayFromZero);
                RemoveFogLine(xF, yTop, yTop + len, sizeX, sizeY, owner);
            }
            else if (transport == 1)
            {
                // Ladder: reveal UP the climb length (0057f5c0:187-193).
                int len = (int)Math.Round(transportLen * 8f, MidpointRounding.AwayFromZero);
                RemoveFogLine(xF, yTop - len - 0x10, yF + 4, sizeX, sizeY, owner);
            }
            else if (sizeY >= 1)
            {
                // round(yTop - 0.5) (DAT_0073708c): banker-free away-from-zero round as the FPU did.
                int yr = (int)Math.Round(yTop - 0.5, MidpointRounding.AwayFromZero);
                RemoveFog(xF, yr, sizeX, sizeY, owner, 0f, 1f);
            }
            else if (sizeX >= 1)
            {
                RemoveFog(xF, yF + 4, sizeX, -sizeY, owner, 0f, 1f);
            }
            else
            {
                // BOTH sizes negative → box through solid rock (FogRemover -50 -50).
                RemoveFogBoxNoCollision(xF, yTop, -sizeX, -sizeY, owner);
            }
            return true;
        }

        // ------------------------------------------------------------------ reveal primitives

        // Shared per-tile reveal (0057e120:163-234 et al.): 0→0xFF raises first-sight, tiles below
        // the sky line join the fade list, the local lane dirties the renderer.
        void RevealTile(int tx, int ty, int owner)
        {
            int idx = (ty * Width + tx) * Players + owner;
            byte b = _fog[idx];
            if (b == 0xFF) return;
            if (b < 2)
            {
                if (b == 0) OnFirstSight(tx, ty, owner);
                if (ty >= _fowBegin[owner])
                {
                    // The fade list never holds duplicates: append only on the <2 → 0xFF
                    // transition; fading entries (2..0xFE) skip this branch, and FadeBlocks
                    // removes the entry when it clamps to 1.
                    _fade[owner].Add(ty * Width + tx);
                }
            }
            if (owner == LocalPlayer && b < 0xE3)
                RegionDirty?.Invoke(tx - 1, ty - 1, tx + 1, ty + 1);
            _fog[idx] = 0xFF;
        }

        void ExtendExploredRect(int owner, int minTx, int minTy, int maxTx, int maxTy)
        {
            var r = ExploredRect[owner];
            if (minTx < r[0]) r[0] = minTx;
            if (minTy < r[1]) r[1] = minTy;
            if (maxTx > r[2]) r[2] = maxTx;
            if (maxTy > r[3]) r[3] = maxTy;
        }

        // CFogOfWar::RemoveFog (0057e120) — elliptical radial raycast WITH terrain collision.
        // x/y/rangeX/rangeY in FINE units (tile*8). fFrom/fTo = fraction of the full circle
        // (UpdateFoW always passes 0..1; sectors are possible). Constants byte-verified from
        //.rdata: numRays = max(rxC,ryC)*2π (double π 00737378), angle = i/N*2π (float π
        // 00737440), step = dir/1.1 (00737150), start offset +0.5 (0073708c), scale 0.125
        // (0073703c). Rays run RockPenetration tiles into solid fill (nibble 0xF) then stop.
        public void RemoveFog(int x, int y, int rangeX, int rangeY, int owner, float fFrom, float fTo)
        {
            if ((uint)owner >= Players) return;
            // World fine rect pre-check (native +0x94..+0xa0 = tile rect *8).
            if (x + rangeX < 0 || y + rangeY < 0 || x - rangeX > Width * 8 || y - rangeY > Height * 8)
                return;

            int rxC = (rangeX + (rangeX >> 31 & 7)) >> 3;
            int ryC = (rangeY + (rangeY >> 31 & 7)) >> 3;
            int maxC = Math.Max(rxC, ryC);
            int cx = (x + (x >> 31 & 7)) >> 3;
            int cy = (y + (y >> 31 & 7)) >> 3;
            if (cx > 0 && cy > 0)
                ExtendExploredRect(owner, cx - rxC, cy - ryC, cx + rxC, cy + ryC);

            int numRays = (int)(maxC * Math.PI * 2.0);
            int i0 = (int)Math.Round(numRays * fFrom, MidpointRounding.AwayFromZero);
            int i1 = (int)Math.Round(numRays * fTo, MidpointRounding.AwayFromZero);
            if (i0 > i1 || numRays <= 0 || rxC <= 0) return;

            float squash = rxC != 0 ? (float)ryC / rxC : 0f;
            float startX = x * 0.125f;
            float startY = y * 0.125f;
            for (int i = i0; i <= i1; i++)
            {
                float ang = (float)i / numRays * 2f * 3.1415927f;
                float dirX = (float)Math.Cos(ang);
                float dirY = (float)Math.Sin(ang) * squash;
                float fx = startX, fy = startY;
                int pen = 0;
                for (int step = 0; step < rxC; step++)
                {
                    int tx = (int)(fx + 0.5f);
                    int ty = (int)(fy + 0.5f);
                    if ((uint)tx < (uint)Width && (uint)ty < (uint)Height)
                    {
                        // Penetration counter (0057e120:152-162): starts on the first solid tile
                        // AFTER the origin step, then counts every step and breaks at 3.
                        if (step != 0)
                        {
                            if (pen == RockPenetration) break;
                            if (pen > 0) pen++;
                            else if (_fillAt(tx, ty) > 0xE) pen = 1;
                        }
                        RevealTile(tx, ty, owner);
                    }
                    fx += dirX / 1.1f;
                    fy += dirY / 1.1f;
                }
            }
        }

        // CFogOfWar::RemoveFogLine (0057ea10) — vertical band y0..y1; per row two horizontal
        // sweeps (right to +xSize, left to -xSize) each with its own 3-solid-tile stop.
        // Elevator shafts / ladders only. ySize extends the explored rect, not the sweep.
        public void RemoveFogLine(int x0, int y0, int y1, int xSize, int ySize, int owner)
        {
            if ((uint)owner >= Players) return;
            int xs = x0 - xSize, xe = x0 + xSize;
            if (xe < -0x20 || xs > Width * 8 + 0x20 || y1 < -0x20 || y0 > Height * 8 + 0x20) return;
            ExtendExploredRect(owner,
                (xs + (xs >> 31 & 7)) >> 3, (y0 - ySize + (y0 - ySize >> 31 & 7)) >> 3,
                (xe + (xe >> 31 & 7)) >> 3, (y1 + ySize + (y1 + ySize >> 31 & 7)) >> 3);

            int cxF = (x0 + 4) & ~7;
            for (int yF = (y0 + 4) & ~7; yF < ((y1 + 4) & ~7); yF += 8)
            {
                int ty = (yF + (yF >> 31 & 7)) >> 3;
                if ((uint)ty >= (uint)Height) continue;
                SweepRow(cxF, yF, xe, +8, ty, owner);
                SweepRow(cxF - 8, yF, xs, -8, ty, owner);
            }
        }

        void SweepRow(int fromF, int yF, int toF, int stepF, int ty, int owner)
        {
            int pen = 0;
            for (int xF = fromF; stepF > 0 ? xF < toF : xF > toF; xF += stepF)
            {
                int tx = (xF + (xF >> 31 & 7)) >> 3;
                if ((uint)tx >= (uint)Width) break;
                if (pen == RockPenetration) break;
                if (pen > 0) pen++;
                else if (_fillAt(tx, ty) > 0xE) pen = 1;
                RevealTile(tx, ty, owner);
            }
        }

        // CFogOfWar::RemoveFogBoxNoCollision (0057f170) — plain rect ±xSize/±ySize (fine) around
        // the snapped center; earth does NOT stop it. Selected by negative set_fogofwar sizes.
        public void RemoveFogBoxNoCollision(int x0, int y0, int xSize, int ySize, int owner)
        {
            if ((uint)owner >= Players) return;
            int cxF = (x0 + 4) & ~7;
            int cyF = (y0 + 4) & ~7;
            int l = Math.Max(cxF - xSize, -0x10), r = Math.Min(cxF + xSize, Width * 8 + 0x10);
            int t = Math.Max(cyF - ySize, -0x10), b = Math.Min(cyF + ySize, Height * 8 + 0x10);
            ExtendExploredRect(owner, (l + (l >> 31 & 7)) >> 3, (t + (t >> 31 & 7)) >> 3,
                                      (r + (r >> 31 & 7)) >> 3, (b + (b >> 31 & 7)) >> 3);
            for (int yF = t; yF < b; yF += 8)
            {
                int ty = (yF + (yF >> 31 & 7)) >> 3;
                if ((uint)ty >= (uint)Height) continue;
                for (int xF = l; xF < r; xF += 8)
                {
                    int tx = (xF + (xF >> 31 & 7)) >> 3;
                    if ((uint)tx >= (uint)Width) continue;
                    RevealTile(tx, ty, owner);
                }
            }
        }

        // CFogOfWar::FadeBlocks (0057e710): walk the player's fade list backwards; byte -= 4;
        // clamp at 1 (GRAY) and drop from the list. The native dirty-rects the local lane at the
        // 0x80 render boundary ((v&~3)==0x7C) and every 5th tick while inside the visible ramp —
        // the port dirties the same way so the fog texture/overlay refresh matches.
        void FadeBlocks(int player)
        {
            var list = _fade[player];
            for (int i = list.Count - 1; i >= 0; i--)
            {
                int packed = list[i];
                int tx = packed % Width, ty = packed / Width;
                int idx = packed * Players + player;
                int v = _fog[idx] - FadeStep;
                if (v < 2)
                {
                    v = GrayFloor;
                    list[i] = list[list.Count - 1];        // native FUN_00581090 swap-last remove
                    list.RemoveAt(list.Count - 1);
                    if (player == LocalPlayer)
                        RegionDirty?.Invoke(tx - 1, ty - 1, tx + 1, ty + 1);
                }
                else if (v > 0x7B && v < 0xE3 && player == LocalPlayer
                         && ((v & ~3) == 0x7C || Tick % 5 == 0))
                {
                    RegionDirty?.Invoke(tx - 1, ty - 1, tx + 1, ty + 1);
                }
                _fog[idx] = (byte)v;
            }
        }

        // CFogOfWar::NewVisibleBlock (0057dfc0) — on the 0→seen transition reconcile the player's
        // dig-mark nibble (native tile+0x08): a fully-solid or fully-dug tile forgets the mark;
        // otherwise the mark's decoded dig target (n==0→15, !(n&8)→11, else (n&3)*4-1 — the
        // DecodeMarkTarget law) is compared against the actual fill around the tile: already dug
        // to target → stale mark, clear it. Raises no events.
        void OnFirstSight(int tx, int ty, int player)
        {
            if (DigNibbleAt == null || ClearDigNibble == null) return;
            int state = TileDigState?.Invoke(tx, ty) ?? 1;
            if (state == 0 || state == 3) { ClearDigNibble(tx, ty, player); return; }
            int n = DigNibbleAt(tx, ty, player) & 0xF;
            if (n == 0) return;
            int threshold = (n & 8) == 0 ? 11 : ((n & 3) != 0 ? (n & 3) * 4 - 1 : 0);
            if (_maxFillAround(tx, ty) <= threshold) ClearDigNibble(tx, ty, player);
        }

        // ------------------------------------------------------------------ Tcl-facing ops

        // remove_black_fog (FUN_0066aec0): tile rect, pure BLACK→GRAY flip (0→1 ONLY — no
        // NewVisibleBlock, no fade list, bytes ≥1 untouched).
        public void RemoveBlackFog(int player, int l, int t, int r, int b)
        {
            if ((uint)player >= Players) return;
            l = Math.Max(l, 0); t = Math.Max(t, 0);
            r = Math.Min(r, Width - 1); b = Math.Min(b, Height - 1);
            for (int ty = t; ty <= b; ty++)
                for (int tx = l; tx <= r; tx++)
                {
                    int idx = (ty * Width + tx) * Players + player;
                    if (_fog[idx] == 0) _fog[idx] = GrayFloor;
                }
            if (player == LocalPlayer) RegionDirty?.Invoke(l, t, r, b);
        }

        // set_fow_begin (FUN_0064b380 → FUN_00580610): clamp the row, permanently reveal the sky
        // band rows [minRow+5, row) (full width minus the right 2 cols), prune fade entries above
        // the line. Re-run per player at world load in the native; the port calls it from the
        // command and can re-apply on load.
        public void SetFowBegin(int row, int player)
        {
            if ((uint)player >= Players) return;
            row = Math.Min(row, Height - 2);
            _fowBegin[player] = row;
            for (int ty = 5; ty < row; ty++)
            {
                if ((uint)ty >= (uint)Height) break;
                for (int tx = 0; tx < Width - 2; tx++)
                {
                    int idx = (ty * Width + tx) * Players + player;
                    if (_fog[idx] == 0) OnFirstSight(tx, ty, player);
                    _fog[idx] = 0xFF;
                }
            }
            var list = _fade[player];
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] / Width < row)
                {
                    list[i] = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                }
            }
            if (player == LocalPlayer) RegionDirty?.Invoke(0, 0, Width - 1, row + 2);
        }

        // The -nofow / reveal-all path (0057f5c0:28).
        public void RevealAll()
        {
            _revealAllPending = false;
            for (int i = 0; i < _fog.Length; i++) _fog[i] = 0xFF;
            for (int p = 0; p < Players; p++)
            {
                ExploredRect[p][0] = 0; ExploredRect[p][1] = 0;
                ExploredRect[p][2] = Width - 1; ExploredRect[p][3] = Height - 1;
                _fade[p].Clear();
            }
            RegionDirty?.Invoke(0, 0, Width - 1, Height - 1);
        }

        // CObj::GetMaxFow sampler (FUN_00580ea0): 7 samples over the object's tile rect — center,
        // 4 corners, 2 vertical-edge midpoints; result = max byte; early-out 0xFF when any sample
        // is already ≥0xE3. Entity-side caching (obj+0x6c) lives on DigglesTclEntity keyed by
        // CacheVersion.
        public int SampleMaxFow(int x0, int y0, int x1, int y1, int player)
        {
            if ((uint)player >= Players) return 0;
            if (x1 < 0 || y1 < 0 || x0 >= Width || y0 >= Height) return 0;
            int midY = (y0 + y1) >> 1;
            Span<int> sx = stackalloc int[7] { (x0 + x1) >> 1, x0, x1, x0, x1, x0, x1 };
            Span<int> sy = stackalloc int[7] { (y0 + y1) >> 1, y0, y0, y1, y1, midY, midY };
            int max = 0;
            for (int i = 0; i < 7; i++)
            {
                int b = FogAt(Math.Clamp(sx[i], 0, Width - 1), Math.Clamp(sy[i], 0, Height - 1), player);
                if (b >= 0xE3) return 0xFF;
                if (b > max) max = b;
            }
            return max;
        }
    }
}
