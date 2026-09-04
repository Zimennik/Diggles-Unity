#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    // Live, mutable terrain model — the dig medium. Reproduces the original engine's carve
    // (FUN_00596b80) at the block level. Diggles has one dig mechanic (the "cave vs wall" split is only
    // an animation choice, get_gnomeposition); a dig lowers the surface by one rim step, not to the floor.
    // Each BDB block's 89-byte aux encodes a per-corner transition graph: aux[+0x40..0x4c] = "the block
    // this becomes when corner NW/NE/SW/SE drops one level". Following a corner steps it 15→11→7→3→0 down
    // to the flat-floor block 59 (self-loop). The carve operates per grid node (shared corner): digging a
    // node steps the matching corner of all up-to-4 tiles meeting there, so the surface stays seamless and
    // the surroundings slope/cliff into the dig (standard tunnel walls are the cliff blocks' own cell
    // geometry, which the renderer meshes directly; GetBlockHole is a separate authored-cutout path).
    // Repeat to deepen.
    //
    // The tile+0x08/+0x0c dig-state words are modelled: the eight nibbles belong to the eight owners
    // (gnome+0x36c = player), and each nibble encodes the target {0,3,7,11,15} for that owner's order.
    // The original terrain also stores one block_idx per tile. A standard tunnel is the shallow mode-1
    // target 11: its BDB transition retains the front rock and exposes the authored wall submeshes. No
    // stacked surface or additional roof layer exists in the native data model. CarveStep reproduces
    // FUN_00596b80's rim gate, gate==rim transition accept, and completion sweep.
    // Full RE + citations: Docs/DIGGING_MECHANIC.md + Docs/re_parts/carve-validator.md.
    //
    // Coordinates: native {X=east, Y=depth, Z=height}; tile = (round X, round Y). Drives the unmodified
    // z_dignwalk.tcl dig loop via the dig primitives in DigglesTclEngine.
    [DisallowMultipleComponent]
    public sealed class DiggableTerrain : MonoBehaviour
    {
        [Serializable]
        public sealed class SaveState
        {
            public int Width;
            public int Height;
            public int PlayerDigMarkId;
            public uint[] BlockIdx = Array.Empty<uint>();
            public byte[] Zone = Array.Empty<byte>();
            public byte[] Terrain = Array.Empty<byte>();
            public ushort[] CellId = Array.Empty<ushort>();
            public ushort[] NativeTerrainFlags = Array.Empty<ushort>();
            public byte[] NavigationState = Array.Empty<byte>();
            // Legacy archive schema name: raw tile+0x12 packed pair/flags, carried verbatim.
            public byte[] FloorAndExitState = Array.Empty<byte>();
            public uint[] OriginalBlock = Array.Empty<uint>();
            public bool[] Dug = Array.Empty<bool>();
            public uint[] DigState = Array.Empty<uint>();
            public uint[] DigId = Array.Empty<uint>();
            public int[] MarkX0 = Array.Empty<int>();
            public int[] MarkY0 = Array.Empty<int>();
            public int[] MarkX1 = Array.Empty<int>();
            public int[] MarkY1 = Array.Empty<int>();
            public byte[] Voxels = Array.Empty<byte>();
            public bool[] HasTemplate = Array.Empty<bool>();
            public int RenderXMin;
            public int RenderYMin;
            public int RenderXMax = -1;
            public int RenderYMax = -1;
        }

        public int Width, Height;
        public uint[] BlockIdx = Array.Empty<uint>();   // mutated in place; renderer reads it on re-mesh
        public byte[] Terrain = Array.Empty<byte>();
        uint[] _originalBlock = Array.Empty<uint>();
        byte[] _tileHeight = Array.Empty<byte>();        // max corner height of the current block (15=solid, 0=floor)
        bool[] _dug = Array.Empty<bool>();               // block became 59 via digging → renders flat floor
        // Native DigMark manager +0xB4[player]. FUN_005661c0 initializes every counter to zero.
        // The counters are transient manager state and are not part of FUN_005931f0's world archive.
        public int PlayerDigMarkId;

        // The dig-state words (carve-validator.md §1.2/§1.5, dig-natives.md §7):
        // _digState = tile+0x08 — 8 owner slots × 4 bits. dig_mark mode 0 paints (cnt|8) into the
        // owner's nibble (FUN_005671a0:50), mode 1 paints 3 (FUN_005672d0:61), mode 2 / the carve's
        // completion sweep clears it. The nibble slot is selected by the gnome's owner
        // (gnome+0x36c), one order per player.
        // _digId = tile+0x0c — the parallel per-owner 4-bit dig-order id (the owner's current mark
        // id at paint time); dig_next prefers same-id voxels (+8 cost otherwise) and the carve
        // caches it onto the gnome (+0x250) so a dwarf finishes its current vein first.
        uint[] _digState = Array.Empty<uint>();
        uint[] _digId = Array.Empty<uint>();
        int[] _digEdgeQueue = Array.Empty<int>();

        // Per-owner dig-mark bounding rect — the native per-player rect at DAT_00772400+0xa328
        // that the dispatcher's request scan iterates (FUN_0043bdd0:42-53). Paint-time expansion
        // only (clears don't shrink it; stale area just fails the nibble gate during the scan).
        readonly int[] _markX0 = new int[8], _markY0 = new int[8], _markX1 = new int[8], _markY1 = new int[8];

        // The faithful pathfinding data layer. Holds the 8×8 per-sub-cell
        // passability + the FUN_005a6350-derived per-tile graph; shares this terrain's BlockIdx array so
        // GetHmapSub tracks digs. Reachable as ctx.Terrain.Nav. See Docs/ORIGINAL_PATHFINDING_RE.md §1.
        public Navigation.NavWorld? Nav;

        // The loaded block DB (read-only consumers: the map-deco template-shape rasterizer,
        // MapDecoBeautifier.ShapeFor — aux 8×8 hmap byte 0x1b). Null on headless fixtures.
        public BdbModel? Bdb => _bdb;

        // Returns the native CFluid shape descriptor for one map cell. A zero-capacity block is
        // impermeable. The boundary ordering is validated because malformed BDB data must not
        // introduce negative volume into the deterministic simulation.
        public bool TryGetFluidShape(int x, int y, out int capacity, out int lower, out int upper)
        {
            capacity = 0;
            lower = 0;
            upper = 8;
            if (_bdb == null || (uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return false;
            int index = y * Width + x;
            if ((uint)index >= (uint)BlockIdx.Length || BlockIdx[index] >= _bdb.Header.NBlocks)
                return false;
            int block = (int)BlockIdx[index];
            capacity = _bdb.AuxFluidCapacity(block);
            upper = _bdb.AuxFluidUpperBoundary(block);
            lower = _bdb.AuxFluidLowerBoundary(block);
            return capacity > 0 && lower <= upper && upper <= 8;
        }

        public bool TryGetFluidRenderShape(
            int x,
            int y,
            out int capacity,
            out int lower,
            out int upper,
            out int depthStripCount)
        {
            depthStripCount = 0;
            if (!TryGetFluidShape(x, y, out capacity, out lower, out upper) || _bdb == null)
                return false;

            int block = (int)BlockIdx[y * Width + x];
            depthStripCount = (16 - (_bdb.AuxVariant(block) & 0x0f)) / 2;
            return true;
        }

        const int AirBlock = 59;        // BDB flat-floor / open block (corners 0/0/0/0, z≈7.0)
        // Corner ids (match aux offsets): 0=NW(+0x40/+0x50) 1=NE(+0x44/+0x51) 2=SW(+0x48/+0x52) 3=SE(+0x4c/+0x53)
        const int NW = 0, NE = 1, SW = 2, SE = 3;

        // Re-mesh dependencies (set by RuntimeMapBuilder.EmitTerrain).
        BdbPmpWorldBuilder.TclWorldResult? _result;

        // Read-only world data access for diagnostics (RimProbeTool) — never mutate through this.
        public BdbPmpWorldBuilder.TclWorldResult? WorldResult => _result;
        BdbModel? _bdb;
        string _dataRoot = "";
        Action? _navRebuild;
        bool _dirty;
        float _remeshAt;
        const float DebounceSec = 0.4f;

        public void Init(BdbPmpWorldBuilder.TclWorldResult r, BdbModel bdb, string dataRoot, Action navRebuild)
        {
            _result = r; _bdb = bdb; _dataRoot = dataRoot; _navRebuild = navRebuild;
            Width = r.Width; Height = r.Height;
            BlockIdx = r.BlockIdx;                          // share the array the renderer reads
            Terrain = r.Terrain;
            _originalBlock = (uint[])r.BlockIdx.Clone();
            int n = Width * Height;
            _tileHeight = new byte[n];
            _digState = new uint[n];
            _digId = new uint[n];
            _dug = new bool[n];
            if (r.NativeTerrainFlags.Length != n)
            {
                r.NativeTerrainFlags = new ushort[n];
                for (int index = 0; index < n; index++)
                {
                    // Same native shape as BuildNativeTerrainFlags: material + authored PMP
                    // force-wall bit 7 + appearance noise (FUN_005992b0:315,339-377).
                    r.NativeTerrainFlags[index] = unchecked((ushort)(
                        BdbPmpWorldBuilder.MaterialTypeAtTile(
                            r.Terrain, Width, Height, index % Width, index / Width) |
                        (r.Terrain[index] & 0x80) |
                        (BdbPmpWorldBuilder.TileAppearanceNoise(
                            index % Width, index / Width) << 8)));
                }
            }
            for (int o = 0; o < 8; o++) { _markX0[o] = 0; _markY0[o] = 0; _markX1[o] = -1; _markY1[o] = -1; }   // empty rects
            for (int t = 0; t < n; t++)
                _tileHeight[t] = (byte)(bdb != null && BlockIdx[t] < bdb.Header.NBlocks ? bdb.AuxMaxCorner((int)BlockIdx[t]) : 15);
            // Share the dug-floor mask so the renderer draws carved-59 floor tiles (and still skips load-air 59).
            r.DugFloor = _dug;
            // Build the faithful pathfinding data layer over the de-rastered authored navigation-plane seed.
            // FUN_005992b0:538-568 blits that RLE first; the full 0x5f validator then orders
            // EFFPASSWALK before EFFBLOCK (FUN_005931f0:246-252; FUN_005a46d0:112-266).
            // Shares BlockIdx, so both boot validation and later carves read the current 0x59 records.
            // Guarded: headless WorkLoopTests fabricate Init with bdb=null + an empty Voxels buffer.
            if (bdb != null && r.NavigationPlane.Length == n * PmpMap.SubGridLength)
            {
                ushort[]? nativeTerrainFlags = r.NativeTerrainFlags.Length == n
                    ? r.NativeTerrainFlags
                    : null;
                Nav = new Navigation.NavWorld(
                    Width,
                    Height,
                    BlockIdx,
                    r.NavigationPlane,
                    bdb,
                    r.HasTpl,
                    nativeTerrainFlags,
                    deriveImmediately: false);
                Nav.RevalidateRect(0, 0, Width * PmpMap.SubGridSide - 1, Height * PmpMap.SubGridSide - 1);
            }
        }

        public void InitializeFromSave(
            BdbPmpWorldBuilder.TclWorldResult result,
            BdbModel bdb,
            string dataRoot,
            Action navRebuild,
            SaveState state)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (bdb == null) throw new ArgumentNullException(nameof(bdb));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (result.Width <= 0 || result.Height <= 0)
                throw new ArgumentException("Saved terrain dimensions must be positive.", nameof(result));

            int tileCount = checked(result.Width * result.Height);
            int subGridLength = checked(tileCount * PmpMap.SubGridLength);
            if (result.BlockIdx.Length != tileCount || result.Zone.Length != tileCount ||
                result.Terrain.Length != tileCount || result.CellId.Length != tileCount ||
                result.NativeTerrainFlags.Length != tileCount || result.HasTpl.Length != tileCount ||
                result.Voxels.Length != subGridLength)
            {
                throw new ArgumentException(
                    "Saved world arrays do not match the declared terrain dimensions.",
                    nameof(result));
            }
            if (state.Width != result.Width || state.Height != result.Height ||
                state.BlockIdx.Length != tileCount || state.Zone.Length != tileCount ||
                state.Terrain.Length != tileCount || state.CellId.Length != tileCount ||
                state.NativeTerrainFlags.Length != tileCount ||
                state.NavigationState.Length != tileCount ||
                state.FloorAndExitState.Length != tileCount ||
                state.OriginalBlock.Length != tileCount || state.Dug.Length != tileCount ||
                state.DigState.Length != tileCount || state.DigId.Length != tileCount ||
                state.Voxels.Length != subGridLength || state.HasTemplate.Length != tileCount ||
                state.MarkX0.Length != 8 || state.MarkY0.Length != 8 ||
                state.MarkX1.Length != 8 || state.MarkY1.Length != 8)
            {
                throw new ArgumentException(
                    "Saved terrain state is incomplete or dimensionally inconsistent.",
                    nameof(state));
            }

            _result = result;
            _bdb = bdb;
            _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
            _navRebuild = navRebuild ?? throw new ArgumentNullException(nameof(navRebuild));
            Width = result.Width;
            Height = result.Height;
            BlockIdx = result.BlockIdx;
            Terrain = result.Terrain;
            PlayerDigMarkId = state.PlayerDigMarkId;

            Array.Copy(state.BlockIdx, result.BlockIdx, tileCount);
            Array.Copy(state.Zone, result.Zone, tileCount);
            Array.Copy(state.Terrain, result.Terrain, tileCount);
            Array.Copy(state.CellId, result.CellId, tileCount);
            Array.Copy(state.NativeTerrainFlags, result.NativeTerrainFlags, tileCount);
            Array.Copy(state.Voxels, result.Voxels, subGridLength);
            for (int index = 0; index < tileCount; index++)
                result.HasTpl[index] = state.HasTemplate[index];
            result.RenderXMin = state.RenderXMin;
            result.RenderYMin = state.RenderYMin;
            result.RenderXMax = state.RenderXMax;
            result.RenderYMax = state.RenderYMax;

            _originalBlock = (uint[])state.OriginalBlock.Clone();
            _dug = (bool[])state.Dug.Clone();
            _digState = (uint[])state.DigState.Clone();
            _digId = (uint[])state.DigId.Clone();
            Array.Copy(state.MarkX0, _markX0, 8);
            Array.Copy(state.MarkY0, _markY0, 8);
            Array.Copy(state.MarkX1, _markX1, 8);
            Array.Copy(state.MarkY1, _markY1, 8);
            _tileHeight = new byte[tileCount];
            for (int index = 0; index < tileCount; index++)
            {
                _tileHeight[index] = (byte)(BlockIdx[index] < bdb.Header.NBlocks
                    ? bdb.AuxMaxCorner((int)BlockIdx[index])
                    : 15);
            }

            _blockHasOpen.Clear();
            _dirtyTx0 = _dirtyTy0 = int.MaxValue;
            _dirtyTx1 = _dirtyTy1 = -1;
            _dirty = false;
            result.DugFloor = _dug;
            Nav = new Navigation.NavWorld(
                Width,
                Height,
                BlockIdx,
                result.Voxels,
                bdb,
                result.HasTpl,
                result.NativeTerrainFlags,
                deriveImmediately: false);
            // Native save load queues the full 0x5f validator mask (FUN_005931f0:246-249),
            // so +0x3c is refreshed from the saved block records. The archive then restores
            // +0x06 and the packed +0x12 byte verbatim; the navigation plane itself is not archived.
            Nav.ReDeriveSubGrid(0, 0, Width * 8 - 1, Height * 8 - 1);
            Nav.RestoreTileGraph(state.NavigationState, state.FloorAndExitState);
        }

        public SaveState CaptureSaveState()
        {
            bool[] hasTemplate = Array.Empty<bool>();
            if (_result?.HasTpl != null)
            {
                hasTemplate = new bool[_result.HasTpl.Length];
                _result.HasTpl.CopyTo(hasTemplate, 0);
            }
            byte[] navigationState;
            byte[] exitPairAndFlagsState;
            if (Nav != null)
            {
                navigationState = Nav.CaptureNavigationState();
                exitPairAndFlagsState = Nav.CaptureExitPairAndFlagsState();
            }
            else
            {
                navigationState = new byte[Width * Height];
                exitPairAndFlagsState = new byte[Width * Height];
                for (int tile = 0; tile < navigationState.Length; tile++)
                    navigationState[tile] = 0xFF;
            }
            return new SaveState
            {
                Width = Width,
                Height = Height,
                PlayerDigMarkId = PlayerDigMarkId,
                BlockIdx = (uint[])BlockIdx.Clone(),
                Zone = _result?.Zone != null ? (byte[])_result.Zone.Clone() : Array.Empty<byte>(),
                Terrain = (byte[])Terrain.Clone(),
                CellId = _result?.CellId != null
                    ? (ushort[])_result.CellId.Clone()
                    : Array.Empty<ushort>(),
                NativeTerrainFlags = _result?.NativeTerrainFlags != null
                    ? (ushort[])_result.NativeTerrainFlags.Clone()
                    : Array.Empty<ushort>(),
                NavigationState = navigationState,
                FloorAndExitState = exitPairAndFlagsState,
                OriginalBlock = (uint[])_originalBlock.Clone(),
                Dug = (bool[])_dug.Clone(),
                DigState = (uint[])_digState.Clone(),
                DigId = (uint[])_digId.Clone(),
                MarkX0 = (int[])_markX0.Clone(),
                MarkY0 = (int[])_markY0.Clone(),
                MarkX1 = (int[])_markX1.Clone(),
                MarkY1 = (int[])_markY1.Clone(),
                Voxels = _result?.Voxels != null ? (byte[])_result.Voxels.Clone() : Array.Empty<byte>(),
                HasTemplate = hasTemplate,
                RenderXMin = _result?.RenderXMin ?? 0,
                RenderYMin = _result?.RenderYMin ?? 0,
                RenderXMax = _result?.RenderXMax ?? -1,
                RenderYMax = _result?.RenderYMax ?? -1,
            };
        }

        public void RestoreSaveState(SaveState state)
        {
            int tileCount = Width * Height;
            if (state.Width != Width || state.Height != Height || state.BlockIdx.Length != tileCount ||
                state.Terrain.Length != tileCount || state.OriginalBlock.Length != tileCount ||
                state.Dug.Length != tileCount || state.DigState.Length != tileCount ||
                state.DigId.Length != tileCount || state.NavigationState.Length != tileCount ||
                state.FloorAndExitState.Length != tileCount)
                throw new ArgumentException("Saved terrain dimensions do not match the loaded world.", nameof(state));
            if (state.MarkX0.Length != 8 || state.MarkY0.Length != 8 ||
                state.MarkX1.Length != 8 || state.MarkY1.Length != 8)
                throw new ArgumentException("Saved terrain dig-mark bounds are invalid.", nameof(state));
            if (_result != null && (state.Zone.Length != tileCount || state.CellId.Length != tileCount ||
                state.NativeTerrainFlags.Length != tileCount ||
                state.Voxels.Length != _result.Voxels.Length ||
                state.HasTemplate.Length != _result.HasTpl.Length))
                throw new ArgumentException("Saved terrain detail data does not match the loaded world.", nameof(state));

            PlayerDigMarkId = state.PlayerDigMarkId;
            Array.Copy(state.BlockIdx, BlockIdx, tileCount);
            Array.Copy(state.Terrain, Terrain, tileCount);
            Array.Copy(state.OriginalBlock, _originalBlock, tileCount);
            Array.Copy(state.Dug, _dug, tileCount);
            Array.Copy(state.DigState, _digState, tileCount);
            Array.Copy(state.DigId, _digId, tileCount);
            Array.Copy(state.MarkX0, _markX0, 8);
            Array.Copy(state.MarkY0, _markY0, 8);
            Array.Copy(state.MarkX1, _markX1, 8);
            Array.Copy(state.MarkY1, _markY1, 8);
            if (_result != null)
            {
                Array.Copy(state.Zone, _result.Zone, tileCount);
                Array.Copy(state.CellId, _result.CellId, tileCount);
                Array.Copy(state.NativeTerrainFlags, _result.NativeTerrainFlags, tileCount);
                Array.Copy(state.Voxels, _result.Voxels, state.Voxels.Length);
                for (int i = 0; i < state.HasTemplate.Length; i++)
                    _result.HasTpl[i] = state.HasTemplate[i];
                _result.RenderXMin = state.RenderXMin;
                _result.RenderYMin = state.RenderYMin;
                _result.RenderXMax = state.RenderXMax;
                _result.RenderYMax = state.RenderYMax;
                _result.Chunks?.MarkRectDirty(0, 0, Width - 1, Height - 1);
            }
            for (int i = 0; i < tileCount; i++)
                _tileHeight[i] = (byte)(_bdb != null && BlockIdx[i] < _bdb.Header.NBlocks
                    ? _bdb.AuxMaxCorner((int)BlockIdx[i]) : 15);

            if (Nav != null)
            {
                Nav.ReDeriveSubGrid(0, 0, Width * 8 - 1, Height * 8 - 1);
                Nav.RestoreTileGraph(state.NavigationState, state.FloorAndExitState);
            }
            RequestRemesh();
        }

        // ---- World render apron (port-only render-window law) ----
        // The native holds and renders the entire fixed 512×640 map as solid rock — there is no
        // "edge of the meshed world" to see (map-create fills every tile with the BlockDB default
        // material + diggable class 2, FUN_00593ec0:88 / FUN_00598f10:154; only the sm_draw_stone
        // -border band makes the outer ~32 tiles undiggable). The port meshes only the render
        // bbox (boot start area + growth stamps), so virgin land beyond it is data-complete
        // (diggable, nav-covered — the arrays span the full map) but invisible: digging into it
        // would walk the player into visible void.
        // Full-map meshing at boot is ~10× the start-area chunk count — instead the bbox grows
        // ahead of the player: any dig mark or carve within ApronTrigger tiles of the bbox edge
        // extends it out to ApronExtend and dirties only the newly exposed chunk bands (chunks
        // are absolute-aligned, so old chunks never re-bin). The player always faces solid rock
        // past the dig front, exactly like the native's full-map render. Hysteresis: extend
        // strictly farther than the trigger, or a frontier dig would re-extend (and re-emit the
        // band's chunks) on every swing.
        public const int ApronTrigger = 16, ApronExtend = 48;
        void EnsureRenderApron(int tx, int ty)
        {
            var r = _result;
            if (r == null || r.RenderXMax < r.RenderXMin) return;
            int ox0 = r.RenderXMin, oy0 = r.RenderYMin, ox1 = r.RenderXMax, oy1 = r.RenderYMax;
            int nx0 = ox0, ny0 = oy0, nx1 = ox1, ny1 = oy1;
            if (tx - ApronTrigger < nx0) nx0 = Math.Max(0, tx - ApronExtend);
            if (ty - ApronTrigger < ny0) ny0 = Math.Max(0, ty - ApronExtend);
            if (tx + ApronTrigger > nx1) nx1 = Math.Min(Width - 1, tx + ApronExtend);
            if (ty + ApronTrigger > ny1) ny1 = Math.Min(Height - 1, ty + ApronExtend);
            if (nx0 == ox0 && ny0 == oy0 && nx1 == ox1 && ny1 == oy1) return;
            r.ExtendRenderBbox(nx0, ny0, nx1, ny1);
            if (r.Chunks != null)
            {
                // dirty only the added bands (the union corners are covered twice — harmless)
                if (nx0 < ox0) r.Chunks.MarkRectDirty(nx0, ny0, ox0, ny1);
                if (nx1 > ox1) r.Chunks.MarkRectDirty(ox1, ny0, nx1, ny1);
                if (ny0 < oy0) r.Chunks.MarkRectDirty(nx0, ny0, nx1, oy0);
                if (ny1 > oy1) r.Chunks.MarkRectDirty(nx0, oy1, nx1, ny1);
            }
            RequestRemesh();
        }

        // Dirty tile rect accumulated by carves since the last revalidation (MapValidator-style).
        int _dirtyTx0 = int.MaxValue, _dirtyTy0 = int.MaxValue, _dirtyTx1 = -1, _dirtyTy1 = -1;
        void MarkDirty(int tx, int ty)
        {
            if (tx < _dirtyTx0) _dirtyTx0 = tx;
            if (ty < _dirtyTy0) _dirtyTy0 = ty;
            if (tx > _dirtyTx1) _dirtyTx1 = tx;
            if (ty > _dirtyTy1) _dirtyTy1 = ty;
            // The carve changes height inputs, not authored tile+0x18 bit 7. Native terrain
            // updates preserve every high flag (`& 0xfffc | material`, FUN_005fc9d0:115-122).
            // With the non-native HasTpl predicate removed, clear-bit rubble re-enters
            // EFFPASSWALK without mutating either template coverage or the force-wall mask.
            // Render side: only the chunks this tile touches get re-meshed on the debounce.
            _result?.Chunks?.MarkTileDirty(tx, ty);
        }

        // ---- coordinate helpers (NATIVE {X=east,Y=depth,Z=height}) ----
        public (int tx, int ty) WorldToTile(Vector3 nativePos) => (Mathf.RoundToInt(nativePos.x), Mathf.RoundToInt(nativePos.y));
        public Vector3 TileToWorldCenter(int tx, int ty) => new Vector3(tx, ty, HmapToZ(tx, ty));
        public float HmapToZ(int tx, int ty) => InBounds(tx, ty) ? _tileHeight[ty * Width + tx] : 15;
        bool InBounds(int tx, int ty) => (uint)tx < (uint)Width && (uint)ty < (uint)Height;

        // ---- queries used by the dig primitives ----
        public int GetHmap(int tx, int ty) => InBounds(tx, ty) ? _tileHeight[ty * Width + tx] : 15;   // OOB = solid
        public bool Solid(int tx, int ty) => !InBounds(tx, ty) || _tileHeight[ty * Width + tx] >= 14;  // OOB = solid

        // Fog-of-war ray-collision probe — the native reads detail-cell byte 0 (= the fill of
        // sub-cell (0,0), the tile's top-left fine cell), not the whole-tile nibble
        // (CFogOfWar::RemoveFog 0057e120:150-159 `*(aux + tile[0]*0x59) & 0xf`). The difference
        // matters at grazing angles: a surface tile open at its top is transparent to fog rays,
        // so the FogRemover's shallow rays skim along the meadow surface and light all of it.
        // Regression: a whole-tile fill read those tiles as solid, producing black stripes on
        // the campaign meadow. Air / out-of-range blocks = open; no BDB (headless fixtures)
        // falls back to the tile nibble.
        public int FogFillAt(int tx, int ty)
        {
            if (!InBounds(tx, ty)) return 15;
            if (_bdb == null) return _tileHeight[ty * Width + tx];
            uint blk = BlockIdx[ty * Width + tx];
            if (blk >= _bdb.Header.NBlocks) return 0;   // air / no geometry = open
            return _bdb.AuxHeightAt((int)blk, 0);
        }
        public bool Diggable(int tx, int ty) => InBounds(tx, ty) && _tileHeight[ty * Width + tx] > 0;

        // get_material — the native law: the handler FUN_00630bb0:93-94 (registration pairing at
        // exe VA 0x61b610 name / 0x61b624 handler; its profiler tag "GroundPos" is a source
        // copy-paste mislabel) returns *(ushort*)(tile + 0x18) & 3 as a Tcl int — the per-tile
        // material type 0..3 (official API, Docs/ENGINE_TCL_API.txt:494: "0-stone 1/2-sand/earth
        // 3-scapehole"). The dig-pace multiplier, z_dignwalk.tcl:315: rand < digcount·
        // [get_material]·0.7 — type-2 earth digs 2× as fast as type-1 sand; 0/3 never dig
        // (precondition FUN_005976b0:36-38; std_ai.tcl:2920 `m==0||m==3 → ROCK`). Load-time
        // writer + the 2×2 zero-byte probe: TileMaterialType below (the shared marker/dig
        // validity law).
        public int GetMaterial(int tx, int ty) => TileMaterialType(tx, ty);

        public int GetFtype(int tx, int ty) => InBounds(tx, ty) ? Terrain[ty * Width + tx] : 0;

        // Rim height for dig_next's Z (FUN_005968b0): max of the block's 4 corner gate bytes
        // (aux[0x50..0x53] — the dig-transition gates). Doc §6: dig_next Z = rim+4 clamp [1,14].
        public int RimHeight(int tx, int ty)
        {
            if (!InBounds(tx, ty) || _bdb == null) return 15;
            uint blk = BlockIdx[ty * Width + tx];
            if (blk >= _bdb.Header.NBlocks) return 15;
            int m = 0;
            for (int c = 0; c < 4; c++) m = Math.Max(m, _bdb.AuxCornerGate((int)blk, c));
            return m;
        }

        // ---- dig marks (the faithful tile+0x08 / tile+0x0c per-owner nibbles) ----

        // ADDITIVE render hook (dig-mark overlay): raised whenever a tile's dig-state word
        // (tile+0x08 analogue) changes — paint, clear, the carve's completion sweep, growth-stamp
        // resets. Purely observational: no carve/nav behavior rides on it. The DigMarkOverlay
        // subscribes and refreshes its per-tile mark texture texel.
        public event Action<int, int>? DigMarkChanged;
        void NotifyMark(int tx, int ty) => DigMarkChanged?.Invoke(tx, ty);

        // dig_mark mode 0 (FUN_005671a0): nibble = (cnt|8); mode 1 (FUN_005672d0): nibble = 3.
        // cnt encodes the open-depth TARGET the carve stops at: 0→0 (full), 1→3, 2→7, 3→11 (§1.2).
        public void SetDigMark(int tx, int ty, int owner, int cnt = 0, bool dummy = false)
        {
            if (!InBounds(tx, ty) || (uint)owner >= 8) return;
            EnsureRenderApron(tx, ty);   // marks may extend past the meshed window (see below)
            int shift = owner * 4, idx = ty * Width + tx;
            uint nib = dummy ? 3u : (uint)((cnt & 3) | 8);
            _digState[idx] = (_digState[idx] & ~(0xFu << shift)) | (nib << shift);
            _digId[idx] = (_digId[idx] & ~(0xFu << shift)) | ((uint)(PlayerDigMarkId & 0xF) << shift);
            NotifyMark(tx, ty);
            // Expand the owner's mark rect (the FUN_0043bdd0 scan domain). First paint seeds it.
            if (_markX1[owner] < _markX0[owner]) { _markX0[owner] = _markX1[owner] = tx; _markY0[owner] = _markY1[owner] = ty; }
            else
            {
                if (tx < _markX0[owner]) _markX0[owner] = tx;
                if (tx > _markX1[owner]) _markX1[owner] = tx;
                if (ty < _markY0[owner]) _markY0[owner] = ty;
                if (ty > _markY1[owner]) _markY1[owner] = ty;
            }
        }

        // The owner's dig-mark bounding rect (inclusive), false while the owner never painted —
        // the native per-player rect FUN_0043bdd0:42-53 gates its whole request pass on.
        public bool TryGetDigMarkBounds(int owner, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = 0; x1 = y1 = -1;
            if ((uint)owner >= 8 || _markX1[owner] < _markX0[owner]) return false;
            x0 = _markX0[owner]; y0 = _markY0[owner]; x1 = _markX1[owner]; y1 = _markY1[owner];
            return true;
        }
        public void ClearDigMark(int tx, int ty, int owner)
        {
            if (!InBounds(tx, ty) || (uint)owner >= 8) return;
            _digState[ty * Width + tx] &= ~(0xFu << (owner * 4));
            NotifyMark(tx, ty);
        }
        // dig_next's mark gate (FUN_00597b20:127): (tile+0x08 & 0xB<<shift) != 0.
        public bool IsDigMarked(int tx, int ty, int owner)
            => InBounds(tx, ty) && (uint)owner < 8 && ((_digState[ty * Width + tx] >> (owner * 4)) & 0xB) != 0;
        // The RAW owner nibble (tile+0x08 >> owner*4 & 0xF) — DigNotify's growth gate reads it
        // directly ((n&8)==0 && n!=0, FUN_005fd060:75-77). OOB/bad owner = 0 (virgin → gate fails).
        public int RawDigNibble(int tx, int ty, int owner)
            => InBounds(tx, ty) && (uint)owner < 8 ? (int)((_digState[ty * Width + tx] >> (owner * 4)) & 0xF) : 0;
        public int DigMarkIdAt(int tx, int ty, int owner)
            => InBounds(tx, ty) && (uint)owner < 8 ? (int)((_digId[ty * Width + tx] >> (owner * 4)) & 0xF) : 0;
        public void IncPlayerDigMarkId() => PlayerDigMarkId++;

        // ---- cave records (the map-mgr list FUN_00567cb0:204-214 appends to) ----
        // One record per committed cave stamp: the painted cells' bbox + the template index —
        // consumed by the eraser's cave-click removal (FUN_0058ec00) and, later, by cave-skin
        // application when the cave is actually dug. MarkId = the stroke-id nibble the stamp's
        // cells carry (tile+0x0c), which the flood erase keys on.
        public readonly struct CaveRecord
        {
            public readonly int X0, Y0, X1, Y1;   // inclusive painted bbox
            public readonly int TemplateIdx;      // 0..3 (mgr +0xdc at commit time)
            public readonly int MarkId;           // the id nibble (counter & 0xF at paint time)
            public readonly int Owner;
            public CaveRecord(int x0, int y0, int x1, int y1, int tpl, int markId, int owner)
            { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; TemplateIdx = tpl; MarkId = markId; Owner = owner; }
            public bool Contains(int tx, int ty) => tx >= X0 && tx <= X1 && ty >= Y0 && ty <= Y1;
        }

        readonly List<CaveRecord> _caveRecords = new();
        public IReadOnlyList<CaveRecord> CaveRecords => _caveRecords;

        public CaveRecord[] CaptureCaveRecords() => _caveRecords.ToArray();

        public void RestoreCaveRecords(IEnumerable<CaveRecord> records)
        {
            ValidateCaveRecords(records);
            _caveRecords.Clear();
            _caveRecords.AddRange(records);
        }

        public static void ValidateCaveRecords(IEnumerable<CaveRecord>? records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            foreach (CaveRecord record in records)
            {
                if (record.X1 < record.X0 || record.Y1 < record.Y0 ||
                    (uint)record.TemplateIdx >= 4 || (uint)record.MarkId >= 16 ||
                    (uint)record.Owner >= 8)
                {
                    throw new ArgumentException("A cave record contains invalid native state.", nameof(records));
                }
            }
        }

        public void AddCaveRecord(int x0, int y0, int x1, int y1, int templateIdx, int markId, int owner)
        {
            // FUN_0058eb30 — both native appends (the stamp commit FUN_00567cb0:205 and
            // `cave_skin add` FUN_0066a9a0:216) first drop the first strictly-overlapping
            // existing rect (strict <, one only) so a re-stamped area doesn't stack records.
            for (int k = 0; k < _caveRecords.Count; k++)
            {
                var e = _caveRecords[k];
                if (x0 < e.X1 && e.X0 < x1 && y0 < e.Y1 && e.Y0 < y1)
                { _caveRecords.RemoveAt(k); break; }
            }
            _caveRecords.Add(new CaveRecord(x0, y0, x1, y1, templateIdx, markId, owner));
        }

        // FUN_0058f2a0 — swap-remove by index (the completion pass retires the finished rect).
        public void RemoveCaveRecord(int index) => _caveRecords.RemoveAt(index);

        // ---- per-carve deco sweep hook ----
        // Fired after every successful carve at node (nx,ny) on any path — the faithful marked
        // dig (CarveStep) and the Ctrl+LMB test carve (CarveTileNow). The engine wires this
        // (DigglesTclEngine.MapDecoOf) to MapDecoCarveSweep — the port analog of the native
        // MapValidator DUMMYDES phase (FUN_005a46d0 label 0x10 → FUN_005eb7b0), which destroys
        // objtype-dummy deco whose anim bbox overlaps a carve dirty rect (FUN_005eae80 deferred
        // destroy, obj+0x404 guard). In the original, tunnel boards/beams/moss vanish one by
        // one as a cave carve front reaches them: FUN_005976b0 is only the dig-guard that blocks
        // carving under standing objects (FUN_00596b80:45/281-282), a different mechanism — the
        // engine does delete deco on carve. Fires only when terrain actually changed.
        public System.Action<int, int>? OnCarveNode;

        // FUN_0058ec00 — `cave_skin rem <x> <y>`: drop the first record containing the point
        // (inclusive bounds, no owner filter — the Tcl caller is the AI/story script).
        public bool RemoveCaveRecordAt(int x, int y)
        {
            for (int k = 0; k < _caveRecords.Count; k++)
                if (_caveRecords[k].Contains(x, y)) { _caveRecords.RemoveAt(k); return true; }
            return false;
        }

        // ---- the mark eraser (tool 5 / dig:undo — FUN_00567410, BOTTOM_MENU_RE.md §3/§5) ----

        // Cave-click removal (FUN_00567410:100-123, flood asm-verified 0x5674bb–0x567792): a
        // click on a tile whose owner nibble carries the open bit 8 removes the whole cave —
        // the covering cave record is dropped (FUN_0058ec00) and every 4-connected tile with the
        // same stroke-id nibble + the cave bit flood-clears. Returns false when (tx,ty) is not
        // cave-marked (the caller then runs the stroke erase instead).
        public bool EraseCaveAt(int tx, int ty, int owner)
        {
            if (!InBounds(tx, ty) || (uint)owner >= 8) return false;
            if ((RawDigNibble(tx, ty, owner) & 8) == 0) return false;
            int id = DigMarkIdAt(tx, ty, owner);
            for (int k = _caveRecords.Count - 1; k >= 0; k--)
                if (_caveRecords[k].Owner == owner && _caveRecords[k].Contains(tx, ty))
                    _caveRecords.RemoveAt(k);
            var stack = new Stack<(int x, int y)>();
            stack.Push((tx, ty));
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (!InBounds(x, y)) continue;
                if ((RawDigNibble(x, y, owner) & 8) == 0 || DigMarkIdAt(x, y, owner) != id) continue;
                ClearDigMark(x, y, owner);
                stack.Push((x + 1, y)); stack.Push((x - 1, y));
                stack.Push((x, y + 1)); stack.Push((x, y - 1));
            }
            return true;
        }

        // Stroke-erase window (FUN_00567410:180-239): one interpolated stroke step clears the
        // owner's nibble over the 2×2 window [x, x+2) × [y, y+2) — but only plain tunnel marks
        // (no bit-8 cells; caves are click-removed above,:209). Returns the cleared count.
        public int EraseStrokeWindow(int x, int y, int owner)
        {
            if ((uint)owner >= 8) return 0;
            int cleared = 0;
            for (int ty = y; ty < y + 2; ty++)
                for (int tx = x; tx < x + 2; tx++)
                {
                    if (!InBounds(tx, ty)) continue;
                    int nib = RawDigNibble(tx, ty, owner);
                    if (nib == 0 || (nib & 8) != 0) continue;
                    ClearDigMark(tx, ty, owner);
                    cleared++;
                }
            return cleared;
        }

        // The open-depth threshold this owner's mark encodes (FUN_00596b80:48-61).
        public int DigTarget(int tx, int ty, int owner)
            => InBounds(tx, ty) && (uint)owner < 8
                ? DecodeTarget((_digState[ty * Width + tx] >> (owner * 4)) & 0xF)
                : 15;
        // The nibble decode as a public law (the cave-stamp validity FUN_00566a60:166-178 runs
        // the same inline decode against the template cell's required target).
        public static int DecodeMarkTarget(int nibble) => DecodeTarget((uint)nibble);

        // The painters' re-mark rim gate (mode-1 tunnel FUN_005672d0:40-53; mode-0/stencil
        // FUN_005671a0:32-45 — not part of the FUN_00566870/FUN_00566a60 validity, so the drop is
        // per-cell silent at paint time, exactly like the native): under the per-player explored
        // flag tile[0x34+player] (the no-fog port = always explored), the cell paints only while
        // the node rim FUN_005968b0 — our RimAtNode = the level the next carve step lands at
        // (virgin 11, once-carved 7, …, floor −1) — can still reach the decoded target
        // (FUN_00597430) of the nibble being painted (dummy 3 → 11, (cnt|8) → {0,3,7,11}).
        // The "existing nibble" reading is refuted by arithmetic: it would drop virgin rock
        // (rim 11 < unmarked-decode 15). With the painted-nibble reading every case lands:
        // • standard tunnel (target 11) over a completed tunnel (rim 7): dropped — marking
        // over an already-carved cell is dropped on mouse-up;
        // • standard tunnel on virgin rock / the natural surface (rim 11): 11 ≥ 11 ⇒ kept,
        // and re-marking a pending order stays legal;
        // • cave stamp cells (targets 0/3/7) over a carved tunnel mouth (rim 7): kept — the
        // cave deepens the corridor floor; only its cnt-3 lips (target 11) drop there
        // (already open to 11 — nothing to do);
        // • floor-carved nodes (rim −1): always dropped.
        public bool MarkRimGate(int tx, int ty, int paintNibble)
            => RimAtNode(tx, ty) >= DecodeMarkTarget(paintNibble);

        // FUN_00597430:12-22 (the inline copy lives at FUN_00596b80:49-61) decode: unmarked→15,
        // dummy ((v&8)==0)→0xb=11, (cnt|8)→ c==0 ? 0: 4c−1 = {0,3,7,11}.
        //
        // Native law: the mode-1 standard-tunnel dummy decodes to 11 — lower the shared-node rim
        // exactly one transition (h 15→11, the same open depth the authored corridors walk at).
        // Passability is decided by the complete node shape, not the decode depth: one node is
        // only a shared corner and legitimately leaves boundary blocks non-walkable. A horizontal
        // standard order has two adjacent Y node rows (vertical: two adjacent X columns), with
        // adjacent nodes along the drag axis bounding each one-tile cell; that 2xN shape produces
        // the floor/climb planes and a working rim→bottom path at target 11 and 0
        // (carve-validator §2.6 + shaft-floor §1.3). A single-tile Shift-click marks only one
        // column, which is never descendable at any depth — the marquee tool guarantees ≥2
        // columns (DigMarquee [min,max+2)). Full-depth digs remain available as the mode-0 depth
        // tools (cnt 0..3 → {0,3,7,11}). Side effect of 11: one dig_apply per node ⇒ one stone
        // roll per node — the native drop rate (the full-depth ×4 multiplier is gone).
        static int DecodeTarget(uint v)
        {
            if ((v & 0xF) == 0) return 15;          // unmarked → fully solid target
            if ((v & 8) == 0) return 11;            // mode-1 dummy → ONE dig step (FUN_00597430:15-16)
            return (v & 3) != 0 ? (int)(v & 3) * 4 - 1 : 0;   // (cnt|8) → {0,3,7,11}
        }

        // All currently dig-marked tiles of `owner` that still have material, as (tile, cnt).
        public IEnumerable<(int tx, int ty, int count)> CollectDigMarks(int owner, int maxCount)
        {
            if ((uint)owner >= 8) yield break;
            int shift = owner * 4, found = 0;
            for (int ty = 0; ty < Height && found < maxCount; ty++)
                for (int tx = 0; tx < Width && found < maxCount; tx++)
                {
                    int idx = ty * Width + tx;
                    if (((_digState[idx] >> shift) & 0xB) != 0 && _tileHeight[idx] > 0)
                    {
                        found++;
                        int c = (int)(_digState[idx] >> shift) & 3;
                        yield return (tx, ty, c > 0 ? c : 1);
                    }
                }
        }

        // get_digedge, FUN_005981d0 / Diggles.exe 0x5981d0-0x59860d. The BFS starts at the
        // marker tile and may expand only from this owner's marked nibbles (mask 0xB). Bit 2
        // of that nibble is native scratch-visited state and is cleared from every queued tile
        // before return (0x5985b4-0x59860d). Candidates do not use NavWorld: BDB record byte 0
        // must be <0x0c and tile+0x34[owner] must be explored. Rank tile coordinates by squared
        // Euclidean distance to the gnome with strict < (0x5983e1-0x598408).
        public bool TryFindDigEdge(
            int seedX,
            int seedY,
            int owner,
            int referenceTileX,
            int referenceTileY,
            out int edgeTileX,
            out int edgeTileY,
            FogOfWarState? fogOfWar = null)
        {
            edgeTileX = edgeTileY = -1;
            if ((uint)owner >= 8 || seedX < 1 || seedY < 1 ||
                seedX >= Width - 1 || seedY >= Height - 1)
            {
                return false;
            }

            int seedIndex = seedY * Width + seedX;
            int shift = owner * 4;
            uint markMask = 0xBu << shift;
            uint visitedMask = 4u << shift;

            int tileCount = Width * Height;
            if (_digEdgeQueue.Length != tileCount)
                _digEdgeQueue = new int[tileCount];

            int head = 0;
            int tail = 1;
            _digEdgeQueue[0] = seedIndex;
            _digState[seedIndex] |= visitedMask;

            long bestDistance = long.MaxValue;
            try
            {
                while (head < tail)
                {
                    int tile = _digEdgeQueue[head++];
                    int tx = tile % Width;
                    int ty = tile / Width;
                    if (tx < 1 || ty < 1 || tx >= Width - 1 || ty >= Height - 1) continue;

                    // Headless/no-fog worlds have no native +0x34 page; their established port
                    // contract is "all explored". Gameplay callers pass the live FogOfWarState.
                    bool explored = fogOfWar == null || fogOfWar.FogAt(tx, ty, owner) != 0;
                    if (explored && IsDigEdgeOpen(tile))
                    {
                        long dx = tx - (long)referenceTileX;
                        long dy = ty - (long)referenceTileY;
                        long distance = dx * dx + dy * dy;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            edgeTileX = tx;
                            edgeTileY = ty;
                        }
                    }

                    if ((_digState[tile] & markMask) == 0) continue;
                    EnqueueDigEdgeNeighbour(tx + 1, ty, visitedMask, ref tail);
                    EnqueueDigEdgeNeighbour(tx - 1, ty, visitedMask, ref tail);
                    EnqueueDigEdgeNeighbour(tx, ty + 1, visitedMask, ref tail);
                    EnqueueDigEdgeNeighbour(tx, ty - 1, visitedMask, ref tail);
                }
            }
            finally
            {
                for (int index = 0; index < tail; index++)
                    _digState[_digEdgeQueue[index]] &= ~visitedMask;
            }
            return edgeTileX >= 0;
        }

        bool IsDigEdgeOpen(int tile)
        {
            if (_bdb == null || (uint)tile >= (uint)BlockIdx.Length) return false;
            uint block = BlockIdx[tile];
            return block < _bdb.Header.NBlocks && _bdb.AuxHeightAt((int)block, 0) < 0x0C;
        }

        void EnqueueDigEdgeNeighbour(int tx, int ty, uint visitedMask, ref int tail)
        {
            if ((uint)tx >= (uint)Width || (uint)ty >= (uint)Height) return;
            int tile = ty * Width + tx;
            if ((_digState[tile] & visitedMask) != 0) return;
            _digState[tile] |= visitedMask;
            _digEdgeQueue[tail++] = tile;
        }

        // FUN_005968b0 — the rim at grid node (nx,ny): for each of the 4 tiles sharing the node,
        // follow that tile's corner transition (+0x40..0x4c) unless terminal/self and read the gate
        // byte (+0x50..0x53) of the block it lands on (own gate when terminal/self); rim = max of
        // the 4. Returns -1 ("no rim") when no corner followed a transition — the node is fully
        // self-contained (everything around terminal). This replaces the single-tile RimHeight
        // approximation for the carve gate and dig_next.
        public int RimAtNode(int nx, int ny)
        {
            if (_bdb == null) return -1;
            bool followed = false;
            int m = -1;
            Span<(int tx, int ty, int corner)> cells = stackalloc (int, int, int)[]
            { (nx - 1, ny - 1, SE), (nx, ny - 1, SW), (nx - 1, ny, NE), (nx, ny, NW) };
            foreach (var (tx, ty, corner) in cells)
            {
                if (!InBounds(tx, ty)) continue;
                uint blk = BlockIdx[ty * Width + tx];
                if (blk >= _bdb.Header.NBlocks) continue;
                uint cand = _bdb.AuxCornerNeighbour((int)blk, corner);
                byte gate;
                // cand >= NBlocks: out-of-catalogue index (defensive — StepCornerGated refuses
                // these too; synthetic test BDBs would otherwise read garbage aux) = terminal.
                if (cand == 0xFFFFFFFFu || cand == blk || cand >= _bdb.Header.NBlocks)
                    gate = _bdb.AuxCornerGate((int)blk, corner);
                else { gate = _bdb.AuxCornerGate((int)cand, corner); followed = true; }
                if (gate > m) m = gate;
            }
            return followed ? m : -1;
        }

        // 4-neighbour of (dx,dy) that is open (non-solid), nearest to gnomeNative; falls back to the dig tile.
        public (int x, int y) NearestStandableNeighbour(int dx, int dy, Vector3 gnomeNative)
        {
            var (gx, gy) = WorldToTile(gnomeNative);
            (int x, int y) best = (dx, dy); float bd = float.MaxValue; bool found = false;
            Span<(int, int)> nb = stackalloc (int, int)[] { (dx + 1, dy), (dx - 1, dy), (dx, dy + 1), (dx, dy - 1) };
            foreach (var (x, y) in nb)
            {
                if (!InBounds(x, y) || Solid(x, y)) continue;
                float d = (x - gx) * (x - gx) + (y - gy) * (y - gy);
                if (d < bd) { bd = d; best = (x, y); found = true; }
            }
            return found ? best : (dx, dy);
        }

        // ================= the faithful per-node carve =================
        // Step one tile's given corner down one rim level via the aux transition graph (the engine's carve,
        // FUN_00596b80). Returns true if the block changed. Terminal (corner already at floor) = no-op.
        bool StepCornerDown(int tx, int ty, int corner)
        {
            if (!InBounds(tx, ty) || _bdb == null) return false;
            int idx = ty * Width + tx;
            uint blk = BlockIdx[idx];
            if (blk >= _bdb.Header.NBlocks) return false;
            uint cand = _bdb.AuxCornerNeighbour((int)blk, corner);
            if (cand == 0xFFFFFFFFu || cand == blk || cand >= _bdb.Header.NBlocks) return false;  // terminal
            BlockIdx[idx] = cand;
            _tileHeight[idx] = (byte)_bdb.AuxMaxCorner((int)cand);
            _dug[idx] = cand == AirBlock && _originalBlock[idx] != AirBlock;   // carved floor renders; load-air skipped
            MarkDirty(tx, ty);                                                 // queue for EFFPASSWALK→EFFBLOCK
            return true;
        }

        // Dig the grid node (nx,ny): lower it one level by stepping the shared corner of each up-to-4 tiles
        // meeting there. Corner of each tile at this node: (nx-1,ny-1)=SE, (nx,ny-1)=SW, (nx-1,ny)=NE, (nx,ny)=NW.
        bool DigNode(int nx, int ny)
        {
            bool any = false;
            any |= StepCornerDown(nx - 1, ny - 1, SE);
            any |= StepCornerDown(nx,     ny - 1, SW);
            any |= StepCornerDown(nx - 1, ny,     NE);
            any |= StepCornerDown(nx,     ny,     NW);
            return any;
        }

        // One dig = lower one grid node by one rim level (the engine's atomic dig_apply unit). It steps the
        // shared corner of the up-to-4 tiles meeting at (nx,ny). This is also the native block-swap shape:
        // FUN_00596b80:64-134 follows +0x4c/+0x48/+0x44/+0x40 and writes the four surrounding node+0 ids.
        // The selected variants carry the changed 8×8 detail that EFFPASSWALK gathers on the same dirty pass.
        // Repeat clicks to deepen toward the flat floor (block 59), not a jump to the floor.
        // `nx,ny` is the dig node = the grid corner nearest the click (the rounded native point).
        public bool CarveTileNow(int nx, int ny)
        {
            if (_bdb == null) return false;
            EnsureRenderApron(nx, ny);
            bool any = DigNode(nx, ny);
            // Nav revalidation is synchronous with the carve; only the render remesh stays
            // debounced. The old coupling (revalidate inside RemeshNow, whose 0.4 s debounce is
            // re-armed by every carve) starved the nav layer for the whole active dig: Calc1 read
            // stale +0x3c bits while FloorPathMap read fresh heights, causing FindPath failures
            // (a climber stuck at one spot) and walks planned onto already-dug air. The native
            // never waits for rendering: the dirty rect goes straight to the MapValidator
            // (FUN_005ad040 → FUN_005a46d0, carve-validator §1.6/§3).
            if (any) { RequestRemesh(); RevalidateDirtyNav(); OnCarveNode?.Invoke(nx, ny); }
            return any;
        }

        // One engine carve step per dig_apply, at the digpoint's node — debug/unmarked form (the
        // Ctrl+LMB test carve): unconditional corner stepping, no dig-state gating.
        public bool ApplyDig(int nx, int ny) => CarveTileNow(nx, ny);

        // ================= the faithful carve — FUN_00596b80 =================
        // One carve step at grid node (nx,ny) for the digger's owner layer:
        // 1. decode the centre tile's dig nibble → the open-depth target {0,3,7,11,15} (§1.2);
        // 2. rim gate (§1.3): proceed only while target <= rim (rim from the 4-corner walk);
        // on failure clear the owner's nibble (§1.7) — "nothing (more) to dig here";
        // 3. step each of the 4 corner tiles whose transition candidate's gate byte equals the
        // rim (§1.4 — the seamlessness accept; unconditional stepping was an earlier revision);
        // 4. completion sweep (§1.5): clear every layer whose decoded level the lowered rim
        // dropped below — that layer's dig has reached its target depth;
        // 5. cache the tile's dig-order id (tile+0x0c nibble) for the gnome (+0x250, §1.6).
        // The Tcl dig_execute loop calls this once per swing — the slow faithful dig.
        public bool CarveStep(int nx, int ny, int owner, out int dugId)
        {
            dugId = 0;
            if (_bdb == null || (uint)owner >= 8) return false;
            if (nx < 1 || ny < 1 || nx >= Width - 1 || ny >= Height - 1) return false;
            EnsureRenderApron(nx, ny);   // digging toward unmeshed virgin land grows the window
            // The FUN_005976b0 precondition (called at FUN_00596b80:45-47): the tile material
            // tile+0x18 & 3 must be ∈ {1,2} (:36-38) — granite (0) and fully-open (3) refuse the
            // carve, and the refusal clears the owner's nibble ("nothing to dig here",
            // FUN_00596b80:281-282). The rim != -1 half of the precondition (:41-45) is the same
            // RimAtNode gate below. (:48.. object-standing scan not yet modelled — open item.)
            if (!TileMaterialDiggable(nx, ny)) { ClearDigMark(nx, ny, owner); return false; }
            dugId = DigMarkIdAt(nx, ny, owner);
            int target = DigTarget(nx, ny, owner);
            int rim = RimAtNode(nx, ny);
            if (rim < 0 || target > rim) { ClearDigMark(nx, ny, owner); return false; }
            bool changed = false;
            changed |= StepCornerGated(nx - 1, ny - 1, SE, rim);
            changed |= StepCornerGated(nx, ny - 1, SW, rim);
            changed |= StepCornerGated(nx - 1, ny, NE, rim);
            changed |= StepCornerGated(nx, ny, NW, rim);
            int newRim = RimAtNode(nx, ny);
            int idx = ny * Width + nx;
            bool swept = false;
            for (int orderOwner = 0; orderOwner < 8; orderOwner++)
            {
                uint v = (_digState[idx] >> (orderOwner * 4)) & 0xF;
                if (v != 0 && newRim < DecodeTarget(v))
                {
                    _digState[idx] &= ~(0xFu << (orderOwner * 4));
                    swept = true;
                }
            }
            if (swept) NotifyMark(nx, ny);
            // STALL EDGE (shaft-floor-rootcause.md §4): no corner stepped AND the rim cannot move —
            // happens when the rim's max comes from a TERMINAL corner's own gate, so no candidate's
            // gate equals it (StepCornerGated refuses all 4) and every later swing repeats identically
            // (the aux graph is static). The sweep above can't clear it either (newRim == rim >= target,
            // e.g. a shallow (cnt|8) mark with the rim parked at its target level), so dig_next would
            // re-serve this node forever and the dwarf grinds in place. The ORIGINAL escapes such marks through the
            // FUN_005976b0 exposure precondition (tile+0x18&3 ∈ {0,3} ⇒ FUN_00596b80:281-282 clears the
            // nibble) which we do not model — clear the owner's nibble here instead: "nothing (more)
            // to dig at this node".
            if (!changed && newRim == rim)
            {
                ClearDigMark(nx, ny, owner);
                return false;
            }
            // Sync nav revalidation — see CarveTileNow (the render remesh alone stays debounced).
            if (changed) { RequestRemesh(); RevalidateDirtyNav(); OnCarveNode?.Invoke(nx, ny); }
            return changed;
        }

        // StepCornerDown + the FUN_00596b80:74-118 accept: take the transition only when the
        // candidate block's gate byte equals the rim — keeps the carve seamless across the node.
        bool StepCornerGated(int tx, int ty, int corner, int rim)
        {
            if (!InBounds(tx, ty) || _bdb == null) return false;
            int idx = ty * Width + tx;
            uint blk = BlockIdx[idx];
            if (blk >= _bdb.Header.NBlocks) return false;
            uint cand = _bdb.AuxCornerNeighbour((int)blk, corner);
            if (cand == 0xFFFFFFFFu || cand == blk || cand >= _bdb.Header.NBlocks) return false;
            if (_bdb.AuxCornerGate((int)cand, corner) != rim) return false;
            BlockIdx[idx] = cand;
            _tileHeight[idx] = (byte)_bdb.AuxMaxCorner((int)cand);
            _dug[idx] = cand == AirBlock && _originalBlock[idx] != AirBlock;
            MarkDirty(tx, ty);
            return true;
        }

        // True if the tile currently renders any (non-air) geometry — a lenient "is there something to dig".
        public bool HasGeometry(int tx, int ty) => InBounds(tx, ty) && BlockIdx[ty * Width + tx] != AirBlock;

        // True if the RENDERER emits terrain geometry for this tile — the exact BuildTileBuckets
        // skip law (render bbox ∩ blockIdx < NBlocks ∩ not load-time air 59; a DUG-OUT 59 renders
        // its flat floor). The fog-of-war overlay consumes it: black-boundary tiles WITHOUT a
        // terrain surface can't carry the shader's corner gradient, so the overlay must close
        // them with gradient patches instead (FogOfWarOverlay — "hard
        // rectangular fog edges in caves").
        public bool RendersTerrain(int tx, int ty)
        {
            if (!InBounds(tx, ty)) return false;
            var r = _result;
            if (r == null || tx < r.RenderXMin || tx > r.RenderXMax
                          || ty < r.RenderYMin || ty > r.RenderYMax) return false;
            int i = ty * Width + tx;
            uint blk = BlockIdx[i];
            if (_bdb != null && blk >= _bdb.Header.NBlocks) return false;
            return blk != AirBlock || _dug[i];
        }

        // ---- dig exposure (the tile+0x18 & 3 ∈ {1,2} law) ----
        // The native keeps a per-tile exposure byte: 0 = fully buried, 1/2 = partially exposed
        // (a face borders open space), 3 = fully open. dig_next REFUSES buried candidates
        // (FUN_00597b20:128) and the carve preconditions on it (FUN_005976b0) — that is what
        // makes digging PROGRESSIVE: deep marks wait until the front reaches them. The port has
        // no +0x18 byte; proxy: a tile is exposed iff it has material AND any 4-neighbour block
        // (or its own) carries an OPEN sub-cell (min height nibble < 14 — the EFFPASSWALK open
        // threshold). Openness is a property of the BLOCK — cached per block id. Without this
        // gate the port carved BURIED nodes remotely: a marquee shaft became a SEALED void under
        // an uncarved cap and
        // EFFPASSWALK correctly derived nothing inside.
        readonly Dictionary<uint, bool> _blockHasOpen = new();
        public bool TileHasOpenCell(int tx, int ty)
        {
            if (!InBounds(tx, ty) || _bdb == null) return false;
            uint blk = BlockIdx[ty * Width + tx];
            if (blk >= _bdb.Header.NBlocks) return false;
            if (_blockHasOpen.TryGetValue(blk, out bool open)) return open;
            open = false;
            for (int s = 0; s < 64 && !open; s++)
                open = _bdb.AuxHeightAt((int)blk, s) < 14;
            _blockHasOpen[blk] = open;
            return open;
        }

        // Partially exposed: material present + open space on/next to the tile (the {1,2} band).
        public bool TileExposed(int tx, int ty)
            => HasGeometry(tx, ty)
               && (TileHasOpenCell(tx, ty)
                   || TileHasOpenCell(tx + 1, ty) || TileHasOpenCell(tx - 1, ty)
                   || TileHasOpenCell(tx, ty + 1) || TileHasOpenCell(tx, ty - 1));

        // ---- tile MATERIAL TYPE (the native tile+0x18 & 3) — the marker validity band ----
        // Ground truth: tile+0x18's low 2 bits are a per-tile MATERIAL TYPE
        // painted at load time, not a dynamic exposure byte:
        // • map create inits EVERY tile to type 2 (earth) — FUN_00598f10:154
        // (`tile+0x18 = rand<<8 | 2`);
        // • the PMP loader FUN_005992b0 first REWRITES the file terrain bytes (:150-216
        // switch: file 1 → (current&3)+1 = inherit, 2→3, 3→2, 4→1, 5→4; 0 untouched), then
        // stores buffer−1 into tile+0x18 (buffer 4 special-cased to 3,:317-377). Composite
        // FILE byte → type: 0/1 = keep (default 2), 2 = EARTH(2), 3 = SAND(1),
        // 4 = STONE/granite(0), 5 = fully OPEN/scapehole(3); bit 0x80 = a flag (bit 7).
        // • the level generator paints stone rims/regions through the same field
        // (FUN_005fc9d0:115-122 — skips tiles already 0/3, writes 1/2);
        // • NOTHING rewrites it on carve — static per load/growth-stamp.
        // Matches the official API doc (Docs/ENGINE_TCL_API.txt:494 "get_material: 0-stone
        // 1/2-sand/earth 3-scapehole") and the AI rock test (std_ai.tcl:2920 `$m==0||$m==3`).
        // EVERY marker validity law gates on type ∈ {1,2}: tunnel cell FUN_00566870:26-28,
        // tunnel painter FUN_005672d0:41-44, mode-0 painter FUN_005671a0:33-38, cave stamp cell
        // FUN_00566a60:132, and the dig precondition FUN_005976b0:36-38.
        // The loader probes the FIRST NONZERO of the 2×2 block {this, right, below, below-right}
        // (FUN_005992b0:242-306) — replicated lazily over the raw PMP bytes the port keeps in
        // Terrain[] (BdbPmpWorldBuilder blit; the growth blit copies them too).
        public int TileMaterialType(int tx, int ty)
        {
            if (!InBounds(tx, ty)) return 0;   // OOB = stone-like (never markable)
            if (_result?.NativeTerrainFlags is { } nativeFlags &&
                nativeFlags.Length == Width * Height)
            {
                return nativeFlags[ty * Width + tx] & 3;
            }
            switch (ProbeTerrainByte(tx, ty))
            {
                case 2: return 2;   // earth
                case 3: return 1;   // sand
                case 4: return 0;   // STONE (granite — undiggable)
                case 5: return 3;   // fully open (scapehole / air)
                default: return 2;  // 0 untouched / 1 inherit → the map-create default (FUN_00598f10:154)
            }
        }

        // The shared marker/dig validity gate: type ∈ {1,2} (not stone, not open).
        public bool TileMaterialDiggable(int tx, int ty)
        {
            int t = TileMaterialType(tx, ty);
            return t == 1 || t == 2;
        }

        // Whoever writes terrain BYTES after load (growth stamps, tests) must re-derive the
        // material band of the authoritative flag words over the touched rect — the native
        // paints tile+0x18 once at load and the level generator re-paints through the field.
        public void SyncNativeMaterialBand(int x0, int y0, int x1, int y1)
        {
            if (_result != null)
                BdbPmpWorldBuilder.SyncNativeTerrainMaterials(_result, x0, y0, x1, y1);
        }

        byte ProbeTerrainByte(int tx, int ty)
        {
            byte v = (byte)(Terrain[ty * Width + tx] & 0x7F);
            if (v != 0) return v;
            // First nonzero of BELOW / right / below-right — SOUTH before EAST, the proven
            // native order (FUN_005992b0:293-304: the y+1 byte is consulted before the x+1
            // byte; re-read, the old "decompile-ambiguous" note was wrong). Keep
            // in lockstep with BdbPmpWorldBuilder.MaterialTypeAtTile.
            if (ty + 1 < Height && (v = (byte)(Terrain[(ty + 1) * Width + tx] & 0x7F)) != 0) return v;
            if (tx + 1 < Width && (v = (byte)(Terrain[ty * Width + tx + 1] & 0x7F)) != 0) return v;
            if (tx + 1 < Width && ty + 1 < Height) return (byte)(Terrain[(ty + 1) * Width + tx + 1] & 0x7F);
            return 0;
        }

        // FUN_00566870:35-50 — standard-tunnel cell validity reads the neighbour BLOCK's aux byte
        // +0x00 (the NW corner of the 8×8 heightmap; FUN_004093b0 = base + idx*0x59) and requires
        // > 1 ("still solid"). OOB reads solid (15).
        public int NwCornerHeight(int tx, int ty)
            => InBounds(tx, ty) && _bdb != null && BlockIdx[ty * Width + tx] < _bdb.Header.NBlocks
                ? _bdb.AuxTrueCornerNW((int)BlockIdx[ty * Width + tx]) : 15;

        // Per-tile state sync for a tile a growth stamp ACTUALLY wrote (BlitPmpIntoWorld's
        // onTileStamped — native-transparent tiles never reach here). Replaces RefreshRect's
        // old rect-wide loop, which gated on a "touched by dig" test that was TRUE for every
        // tile whose block the stamp just CHANGED (BlockIdx != _originalBlock cannot tell a
        // blit write from a carve), so the stamped tiles' height/original/dug state was never
        // actually synced.
        public void SyncStampedTile(int tx, int ty)
        {
            if (!InBounds(tx, ty)) return;
            int idx = ty * Width + tx;
            _tileHeight[idx] = (byte)(_bdb != null && BlockIdx[idx] < _bdb.Header.NBlocks
                ? _bdb.AuxMaxCorner((int)BlockIdx[idx]) : 15);
            _originalBlock[idx] = BlockIdx[idx];
            // PRE-CUT CELLS (native law): a stamped block-59 tile is an authored OPEN cell —
            // the '_cut' template variants (hol_023_cut vs hol_023: interior tiles are block
            // 59 with valid floor heights) pre-carve part of the cave in DATA. The native
            // map_template blit copies block 59 verbatim (FUN_005992b0:379-415 — no 59
            // special case; the only skip gate is the all-zero terrain window at:292), and
            // the native renders such a cell like a dug-out floor. Mirror the carve outcome:
            // mark it dug so the renderer draws its flat floor — leaving it un-dug made every
            // stamped pre-cut tile INVISIBLE (a see-through hole; the hol_023_cut Lava pool
            // piece has 36 of them).
            _dug[idx] = BlockIdx[idx] == 59;
            if (_result?.NativeTerrainFlags is { } nativeFlags && nativeFlags.Length == Width * Height)
            {
                // Material via the native borrow probe (self→S→E→SE) — a stamped 0-byte tile
                // inherits its window class, incl. STATE 3 next to an authored cutout. Mid-blit
                // the S/E/SE bytes may still be pre-stamp; LiveBlitPmp runs the authoritative
                // rect-wide SyncNativeTerrainMaterials after the whole blit lands.
                nativeFlags[idx] = unchecked((ushort)(
                    (nativeFlags[idx] & ~0x83) |
                    (Terrain[idx] & 0x80) |
                    BdbPmpWorldBuilder.MaterialTypeAtTile(Terrain, Width, Height, tx, ty)));
            }
            // DIG MARKS SURVIVE THE STAMP (FUN_005992b0 re-read): the native blit
            // never touches the per-owner dig-claim nibbles (tile+0x08) or the yield codes
            // (+0x0c) — a tunnel order overlapped by a growth stamp keeps its marks and the
            // dwarf digs straight through the freshly stamped template mouth. (The old clear
            // here orphaned the player's order exactly at the carve↔stamp junction.)
        }

        public void RefreshStampedRect(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);
            if (x1 < x0 || y1 < y0) return;
            // Per-tile state (heights/original/dug) is synced by SyncStampedTile from the
            // blit itself — only for tiles it actually wrote. This rect pass owns the
            // render/nav dirtying below. NO seam reconciliation: the native never rewrites
            // geometry after a stamp — the effect passes re-derive DERIVED data only
            // (EFFBLOCK/OBJPF/RENDERJOBS/MINIMAP inside the rect, FLUIDALIST on the ±1 ring;
            // FUN_005a46d0 probes — none of them touch block idx / height / terrain). Corner
            // coherence in the native comes from the single-authority data model (a tile's
            // geometry IS its block archetype) + the destination-blind blit overwriting the
            // whole overlap zone. The former ReconcileStampSeam (corner minima at
            // carve-adjacent nodes) invented heights belonging to neither authored source —
            // the "wrong earth + mesh holes above it at the junction" artifact.

            // The RLE copy has already seeded this whole rect, including transparent tiles
            // (FUN_005992b0:538-568; FUN_00596550:31-141). The direct 0x40000 composite expands
            // to OBJPF|EFFBLOCK plus render/minimap work (FUN_005a3fd0:36-40), so live stamps
            // derive +0x06/+0x12 from those authored bytes without running EFFPASSWALK. Digging
            // later posts 0x10000 and re-derives the plane before EFFBLOCK (:42-49).
            int rx0 = Math.Max(0, x0 - 1), ry0 = Math.Max(0, y0 - 1);
            int rx1 = Math.Min(Width - 1, x1 + 1), ry1 = Math.Min(Height - 1, y1 + 1);
            _result?.Chunks?.MarkRectDirty(rx0, ry0, rx1, ry1);
            RequestRemesh();
            Nav?.RevalidateStampRect(x0, y0, x1, y1);
            TilesChanged?.Invoke(x0, y0, x1, y1);
        }

        // Live FUN_005fc9d0 material paint publishes mask 0x10 over rect±1
        // (FUN_005fc9d0:127-138). The dispatcher maps 0x10 to RENDERJOBS, not EFFBLOCK
        // (FUN_005a46d0:333-401); the painter preserves tile+0x18 high flags at:115-122,
        // so neither the navigation plane nor +0x06/+0x12 changes here.
        public void RefreshMaterialRect(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);
            if (x1 < x0 || y1 < y0) return;

            _result?.Chunks?.MarkRectDirty(x0, y0, x1, y1);
            RequestRemesh();
            TilesChanged?.Invoke(x0, y0, x1, y1);
        }

        // Render-only dirty rect (no dig-state reset, no nav): the live L2M overlay path —
        // CellId changes ground texturing only, but the affected chunks still must re-mesh.
        public void MarkRenderRectDirty(int x0, int y0, int x1, int y1)
        {
            _result?.Chunks?.MarkRectDirty(x0, y0, x1, y1);
            RequestRemesh();
        }

        public void RequestRemesh()
        {
            // Do NOT re-arm an already-pending debounce: every carve used to push the deadline
            // out again, so continuous digging starved the RENDER remesh indefinitely — the
            // terrain LOOKED undug while its nav was already open (
            //). One pending window ⇒ the mesh lags a carve by at
            // most DebounceSec.
            if (_dirty) return;
            _dirty = true;
            _remeshAt = Time.time + DebounceSec;
        }

        // Raised (inclusive TILE rect) whenever a carve / growth-stamp changes the terrain and the
        // matching nav derivative has refreshed — the hook loose WORLD ITEMS use to re-settle when the ground
        // under them was dug away (the original's set_physic drop, rohstoffe.tcl). Fires
        // SYNCHRONOUSLY inside the carve (before any debounced remesh) so headless callers see it
        // too. Subscribers must not carve re-entrantly (they only move entities).
        public event Action<int, int, int, int>? TilesChanged;

        // Carve MapValidator contract (carve-validator §3): EFFPASSWALK re-derives +0x3c from
        // the changed heightmap, THEN EFFBLOCK re-derives the tile graph. Direct stamp validation
        // is separate and consumes its authored RLE plane without this derive.
        public void RevalidateDirtyNav()
        {
            if (Nav == null || _dirtyTx1 < _dirtyTx0) return;
            int tx0 = _dirtyTx0, ty0 = _dirtyTy0, tx1 = _dirtyTx1, ty1 = _dirtyTy1;
            Nav.RevalidateRect(tx0 * 8, ty0 * 8, tx1 * 8 + 7, ty1 * 8 + 7);
            _dirtyTx0 = _dirtyTy0 = int.MaxValue; _dirtyTx1 = _dirtyTy1 = -1;
            TilesChanged?.Invoke(tx0, ty0, tx1, ty1);
        }

        void Update()
        {
            // Drain any STAGGERED chunk rebuilds spread across frames. HasPendingRebuilds is only
            // ever true when TerrainChunkGrid.StaggerRebuilds is on (default OFF) and a big rebuild
            // deferred chunks — so in the default configuration this branch is a strict no-op and
            // the debounce below is unchanged. Nav was already fully revalidated on the first flush;
            // the deferred work is purely visual mesh/collider, so no RevalidateDirtyNav here.
            var chunks = _result?.Chunks;
            if (chunks != null && chunks.HasPendingRebuilds)
            {
                chunks.RebuildDirty();
                return;
            }
            if (!_dirty || Time.time < _remeshAt) return;
            _dirty = false;
            RemeshNow();
        }

        // Flush the pending (debounced) remesh immediately. EditMode tests / headless callers have
        // no Update pump — this runs the exact same path the debounce would. No-op when idle.
        public void FlushRemesh()
        {
            if (!_dirty) return;
            _dirty = false;
            RemeshNow();
        }

        void RemeshNow()
        {
            if (_result == null || _bdb == null) return;
            // Names the (debounced) growth/dig chunk remesh + collider cook in a Play freeze log
            //. Alloc-free constant label.
            using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section("worldgen.emit", 40);
            // Re-run the SAME renderer over the mutated block_idx array: stepped tiles render their lowered
            // block (slopes/cliffs) and fully-dug tiles render their flat floor (59 via the shared DugFloor
            // mask). Correct-by-construction. Chunked worlds re-emit ONLY the dirty chunks (the tiles a
            // carve stepped / a growth stamp blitted, ±1-tile corner-weight margin) — the old full-bbox
            // RemeshFromArrays is kept solely for legacy un-chunked results.
            if (_result.Chunks != null) _result.Chunks.RebuildDirty();
            else BdbPmpWorldBuilder.RemeshFromArrays(gameObject, _bdb, _result, _dataRoot);
            RevalidateDirtyNav();
            _navRebuild?.Invoke();
        }
    }
}
