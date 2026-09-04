#nullable enable

namespace Diggles.Runtime.Navigation
{
    using Diggles.Runtime.World;

    public enum NavTransportKind
    {
        None,
        Door,
        Ladder,
        Elevator,
    }

    // The pathfinding world model — the original engine's per-tile graph + 8×8 sub-cell layer,
    // derived from the loaded world (PMP-authored navigation plane + BDB aux per-sub-cell heights).
    // Ground truth: Docs/ORIGINAL_PATHFINDING_RE.md §1 (FUN_005a6350 / FUN_004093f0).
    //
    // The native plane stays sparse — gnomes walk thin 1-sub-cell shelves, not inferred open ground.
    // Copy the RLE seed verbatim, then let only the native EFFPASSWALK detail law overwrite validated
    // cells; never densify or bridge it heuristically.
    //
    // Sub-cell layer (tile+0x3c, tile-major [tile*64 + (subY&7)*8 + (subX&7)]): bit 0x01 walkable, 0x02 floor
    // surface. PMP RLE supplies the initial authored values 0/1/3 verbatim. EFFPASSWALK may subsequently
    // overwrite a validated rectangle from the BDB 0x59-record detail bytes; port overlays use the upper bits.
    //
    // Derived per-tile graph (FUN_005a6350 — the post-load "floor-flag" pass that makes the sparse sub-cells a
    // navigable tile graph):
    // BaseCost[t] = tile+0x06: -1 = blocked; else 20 (floor/object passage) or 50 (plain walk).
    // ExitPairAndFlags[t]= tile+0x12 verbatim: bits 0..2 subX, bits 3..5 subY, bit 6 floor/object-passage,
    // bit 7 an independent persisted flag.
    // Calc1 (P1) runs a tile-level search on these records; the packed pair supplies each output waypoint.
    public sealed class NavWorld
    {
        public readonly int Width, Height;
        public int SubW => Width * 8;
        public int SubH => Height * 8;

        readonly uint[] _blockIdx;   // shared with DiggableTerrain (mutated on dig → call DeriveTile on the dirty region)
        readonly byte[] _sub;        // per-tile tile-major 8×8 sub-cell passability: [tile*64 + (subY&7)*8 + (subX&7)]
        readonly BdbModel _bdb;
        readonly uint _nBlocks;
        readonly System.Collections.Generic.Dictionary<int, InfluenceRecord> _influences = new();
        readonly int[] _hardBlockCount;
        readonly int[] _costBias;
        readonly int[] _climbSubCount;
        readonly int[] _climbTileCount;
        readonly int[] _ladderSubCount;

        // FUN_005a6350-derived tile graph.
        public readonly sbyte[] BaseCost;           // tile+0x06
        public readonly byte[] ExitPairAndFlags;    // tile+0x12, preserved as one native byte

        public const byte SubWalk  = 0x01;   // walkable (the pathfinder may occupy)
        public const byte SubFloor = 0x02;   // floor surface (not a wall face)
        public const byte SubWater = 0x04;   // water (needs CanSwim)
        public const byte SubObjectPassage = 0x08; // native EFFOBJPF passage bit consumed by EFFBLOCK
        public const byte SubClimb = SubObjectPassage;

        public const byte ExitSubXMask = 0x07;
        public const byte ExitSubYMask = 0x38;
        public const byte ExitPairMask = ExitSubXMask | ExitSubYMask;
        public const byte ExitFloorOrPassageFlag = 0x40;
        public const byte ExitHighFlag = 0x80;

        // EFFPASSWALK's only tile precondition is authored tile+0x18 bit 7
        // (FUN_005a6b40:15). Template presence is not part of that law. The direct stamp composite
        // 0x40000 expands to bits 1|2 plus render/minimap work (FUN_005a3fd0:36-41), while the later
        // full-world 0x5f validation adds bit 0 (FUN_005931f0:246-252). The runner identifies and orders
        // bit 0 EFFPASSWALK, bit 1 EFFOBJPF, bit 2 EFFBLOCK (FUN_005a46d0:112-266), so boot order is
        // RLE seed -> derived plane -> object plane overlay -> tile records. Force-wall state lives in
        // _nativeTerrainFlags. The hasTpl constructor input remains only for source compatibility with
        // world fixtures; neither EFFPASSWALK nor EFFBLOCK has a template-coverage predicate.
        readonly ushort[]? _nativeTerrainFlags;

        // Monotonic mutation counter: bumped whenever the walkability data is re-derived
        // (RevalidateRect / DeriveAll) so overlays (NavWorldGizmo) can cheaply detect staleness
        // — the NavWorld object identity never changes across digs, only its contents.
        public int Version { get; private set; }

        public NavWorld(int width, int height, uint[] blockIdx, byte[] subGrid, BdbModel bdb,
                        System.Collections.BitArray? hasTpl = null,
                        ushort[]? nativeTerrainFlags = null,
                        bool deriveImmediately = true)
        {
            Width = width; Height = height;
            _blockIdx = blockIdx; _sub = subGrid; _bdb = bdb; _nBlocks = bdb.Header.NBlocks;
            int n = width * height;
            if (nativeTerrainFlags != null && nativeTerrainFlags.Length != n)
                throw new System.ArgumentException(
                    "Native terrain flags do not match navigation dimensions.",
                    nameof(nativeTerrainFlags));
            _nativeTerrainFlags = nativeTerrainFlags;
            BaseCost = new sbyte[n];
            ExitPairAndFlags = new byte[n];
            for (int tile = 0; tile < n; tile++) BaseCost[tile] = -1;
            _hardBlockCount = new int[n];
            _costBias = new int[n];
            _climbSubCount = new int[n * 64];
            _climbTileCount = new int[n];
            _ladderSubCount = new int[n * 64];
            if (deriveImmediately) DeriveAll();
        }

        // ---- sub-cell accessors (tile+0x3c) ----
        public byte SubAt(int subX, int subY)
        {
            if ((uint)subX >= (uint)SubW || (uint)subY >= (uint)SubH) return 0;
            int tile = (subY >> 3) * Width + (subX >> 3);
            int sub = tile * 64 + ((subY & 7) << 3) + (subX & 7);
            byte value = _sub[sub];
            return _climbSubCount[sub] > 0 ? (byte)(value | SubWalk | SubClimb) : value;
        }
        public byte BaseSubAt(int subX, int subY)
        {
            if ((uint)subX >= (uint)SubW || (uint)subY >= (uint)SubH) return 0;
            int tile = (subY >> 3) * Width + (subX >> 3);
            return _sub[tile * 64 + ((subY & 7) << 3) + (subX & 7)];
        }
        public bool SubWalkable(int subX, int subY) => (SubAt(subX, subY) & SubWalk) != 0;
        public bool SubIsFloor(int subX, int subY)  => (SubAt(subX, subY) & SubFloor) != 0;
        public bool SubIsWater(int subX, int subY)  => (SubAt(subX, subY) & SubWater) != 0;

        /// <summary>
        /// Rebuilds the transient water classification over the existing walkable sub-cell graph.
        /// </summary>
        public void RebuildWaterFlags(System.Func<int, int, bool> isSubmerged)
        {
            if (isSubmerged == null)
                throw new System.ArgumentNullException(nameof(isSubmerged));

            RebuildWaterFlags(0, 0, Width, Height, isSubmerged);
        }

        /// <summary>
        /// Rebuilds transient water classification for one terrain tile.
        /// </summary>
        public void RebuildWaterFlagsForTile(
            int tileX,
            int tileY,
            System.Func<int, int, bool> isSubmerged)
        {
            if (isSubmerged == null)
                throw new System.ArgumentNullException(nameof(isSubmerged));
            if (!InTile(tileX, tileY)) return;

            RebuildWaterFlags(tileX, tileY, tileX + 1, tileY + 1, isSubmerged);
        }

        private void RebuildWaterFlags(
            int tileXMin,
            int tileYMin,
            int tileXMaxExclusive,
            int tileYMaxExclusive,
            System.Func<int, int, bool> isSubmerged)
        {
            bool changed = false;
            for (int subY = tileYMin << 3; subY < tileYMaxExclusive << 3; subY++)
            for (int subX = tileXMin << 3; subX < tileXMaxExclusive << 3; subX++)
            {
                int tile = (subY >> 3) * Width + (subX >> 3);
                int index = tile * 64 + ((subY & 7) << 3) + (subX & 7);
                byte current = _sub[index];
                byte updated = (current & SubWalk) != 0 && isSubmerged(subX, subY)
                    ? (byte)(current | SubWater)
                    : (byte)(current & ~SubWater);
                if (current == updated) continue;
                _sub[index] = updated;
                changed = true;
            }
            if (changed) Version++;
        }

        public bool SubHasInfluenceClimb(int subX, int subY)
        {
            if ((uint)subX >= (uint)SubW || (uint)subY >= (uint)SubH) return false;
            int tile = (subY >> 3) * Width + (subX >> 3);
            int sub = tile * 64 + ((subY & 7) << 3) + (subX & 7);
            return _climbSubCount[sub] > 0;
        }

        // get_hmap (FUN_004093f0): per-sub-cell height nibble 0..15 from the tile's current block aux.
        // Out of range / missing block = 15 (solid), as the wrapper FUN_0062faf0 returns.
        // World Z = GetHmapSub * 0.5 + 7.0. This is the slab-depth-into-the-screen coordinate (the dig
        // axis) — use it for depth placement only, never as a gameplay-vertical/standable test.
        public int GetHmapSub(int subX, int subY) => CurrentBlockDetailAt(subX, subY);

        private int CurrentBlockDetailAt(int subX, int subY)
        {
            if ((uint)subX >= (uint)SubW || (uint)subY >= (uint)SubH) return 15;
            // FUN_00595e40:69-77 gathers the 0x59 record through node+0 on every pass. The native
            // dig mutates that node block id first (FUN_00596b80:64-134), so this lookup must stay
            // indirect through the shared live array; caching a pristine PMP block would make a
            // fully carved tile look solid forever.
            uint blk = _blockIdx[(subY >> 3) * Width + (subX >> 3)];
            if (blk >= _nBlocks) return 15;
            return _bdb.AuxHeightAt((int)blk, ((subY & 7) << 3) + (subX & 7));
        }

        // Walkability — the +0x3c sub-grid is the map.
        // The tile grid X×Y is the side view (the ant farm): native +y = down/deeper (gravity, the climb
        // axis); the height nibble = slab depth into the screen (the dig axis), not gameplay-up. A
        // walkable floor is a 1-sub-cell-thick ground line in the (x,y) plane — the boundary row between
        // an open pocket (above, −y) and supporting material (below, +y) — extending into the screen
        // along the depth axis.
        //
        // value-3 cells mark exactly these lines: either h(own) > h(above) (the first solid row under the
        // pocket) or h(below) > h(own) (the last open row over h=15 support, e.g. the h=11 corridors).
        // Gnomes stand at the lines and spread across depth z=6..15 — the free-roaming axis is the slab
        // depth. Do not densify the sub-grid, and do not gate it on block_idx.
        public bool SubIsClimbWall(int subX, int subY)
        {
            byte b = SubAt(subX, subY);
            return (b & SubWalk) != 0 && (b & SubFloor) == 0;   // +0x3c value 1 = climbable wall (vertical face)
        }

        // ---- FUN_005a6350 / EFFBLOCK: derive tile+0x06/+0x12 from the current +0x3c plane ----
        public void DeriveAll()
        {
            DeriveTileGraphRect(0, 0, Width - 1, Height - 1);
            Version++;
        }

        // EFFBLOCK-only refresh for mutations that changed plane overlays or tile+0x18 state but did
        // not change the underlying 0x59-record detail bytes (FUN_005a6350:99-290).
        public void RebuildTileGraphRect(int tx0, int ty0, int tx1, int ty1)
        {
            if (!ClampTileRect(ref tx0, ref ty0, ref tx1, ref ty1)) return;
            DeriveEffBlockSubRect(
                tx0 * PmpMap.SubGridSide,
                ty0 * PmpMap.SubGridSide,
                tx1 * PmpMap.SubGridSide + PmpMap.SubGridSide - 1,
                ty1 * PmpMap.SubGridSide + PmpMap.SubGridSide - 1);
            Version++;
        }

        // Direct stamp validation. FUN_005992b0:538-567 blits the authored RLE plane, then its
        // 0x40000 composite expands to 0x006 (EFFOBJPF|EFFBLOCK), not bit 0 EFFPASSWALK
        // (FUN_005a3fd0:36-40). EFFBLOCK therefore consumes the authored bytes verbatim here.
        // Full-load 0x5f validation and dig 0x10000 dirtying invoke EFFPASSWALK separately.
        // EFFOBJPF/per-object cost remains the existing influence seam below; reapplying it here
        // would duplicate FUN_005a6890's contribution.
        public void RevalidateStampRect(int tx0, int ty0, int tx1, int ty1)
        {
            if (!ClampTileRect(ref tx0, ref ty0, ref tx1, ref ty1)) return;
            DeriveEffBlockSubRect(
                tx0 * PmpMap.SubGridSide,
                ty0 * PmpMap.SubGridSide,
                tx1 * PmpMap.SubGridSide + PmpMap.SubGridSide - 1,
                ty1 * PmpMap.SubGridSide + PmpMap.SubGridSide - 1);
            Version++;
        }

        private void DeriveTileGraphRect(int tx0, int ty0, int tx1, int ty1)
        {
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                    DeriveTile(tx, ty);
        }

        private bool ClampTileRect(ref int tx0, ref int ty0, ref int tx1, ref int ty1)
        {
            tx0 = System.Math.Max(0, tx0);
            ty0 = System.Math.Max(0, ty0);
            tx1 = System.Math.Min(Width - 1, tx1);
            ty1 = System.Math.Min(Height - 1, ty1);
            return tx1 >= tx0 && ty1 >= ty0;
        }

        // Original saves serialize the tile graph explicitly: record +4 is tile+0x06 and record
        // +13 is tile+0x12 (FUN_00592f90 / FUN_005931f0). The latter remains one opaque byte:
        // its packed coordinates and both flags round-trip without reinterpretation.
        public void RestoreTileGraph(byte[] navigationState, byte[] exitPairAndFlagsState)
        {
            if (navigationState == null)
                throw new System.ArgumentNullException(nameof(navigationState));
            if (exitPairAndFlagsState == null)
                throw new System.ArgumentNullException(nameof(exitPairAndFlagsState));
            if (navigationState.Length != BaseCost.Length || exitPairAndFlagsState.Length != BaseCost.Length)
                throw new System.ArgumentException("Saved tile graph does not match navigation dimensions.");

            for (int tile = 0; tile < BaseCost.Length; tile++)
            {
                BaseCost[tile] = unchecked((sbyte)navigationState[tile]);
                ExitPairAndFlags[tile] = exitPairAndFlagsState[tile];
            }
            Version++;
        }

        public byte[] CaptureNavigationState()
        {
            var result = new byte[BaseCost.Length];
            for (int tile = 0; tile < result.Length; tile++)
                result[tile] = unchecked((byte)BaseCost[tile]);
            return result;
        }

        public byte[] CaptureExitPairAndFlagsState() => (byte[])ExitPairAndFlags.Clone();

        // Schema-compatibility name used by the original-save layer. The byte is not split:
        // it is exactly CaptureExitPairAndFlagsState's packed +0x12 record.
        public byte[] CaptureFloorAndExitState() => CaptureExitPairAndFlagsState();

        // MapValidator:EFFBLOCK, FUN_005a6350:99-290. The scan selects the first strict maximum,
        // prioritizing bit-1 cells and then L1 distance to (4,4). +0x12 is a packed pair, not a
        // scalar: x=bits0..2 and y=bits3..5. The all-walkable branch settles that pair down through
        // consecutive bit-1 cells (:256-278); the partial branch writes it directly (:281-286).
        public void DeriveTile(int tx, int ty)
        {
            int tile = ty * Width + tx;
            int baseIdx = tile * 64;
            int bestScore = 0;
            int bestPair = 0x24;
            bool noWalk = true;
            bool allWalk = true;
            bool hasObjectPassage = false;
            for (int s = 0; s < 64; s++)
            {
                byte b = _sub[baseIdx + s];
                hasObjectPassage |= (b & SubObjectPassage) != 0;
                if ((b & SubWalk) == 0)
                {
                    allWalk = false;
                    continue;
                }
                noWalk = false;
                int row = s >> 3, col = s & 7;
                int score = ((b & SubFloor) != 0 ? 0x100 : 0) - Abs(4 - row) - Abs(4 - col) + 8;
                if (score <= bestScore) continue;
                bestScore = score;
                bestPair = s;
            }
            if (noWalk)
            {
                BaseCost[tile] = -1;
                return;
            }

            // FUN_005a6350:132-245: the 2x2 tile+0x18 bit-7 AND blocks the tile unless EFFOBJPF
            // supplied plane bit 3. Like the native -1 branches, +0x12 is intentionally left stale.
            if (TileWindowAllSolid(tx, ty) && !hasObjectPassage)
            {
                BaseCost[tile] = -1;
                return;
            }

            int weightedScore = hasObjectPassage ? bestScore | 0x100 : bestScore;
            byte packedPair;
            if (allWalk)
            {
                if ((weightedScore >> 8) == 0)
                {
                    packedPair = 0x24;
                }
                else
                {
                    packedPair = (byte)bestPair;
                    while (packedPair < 0x38 &&
                           (_sub[baseIdx + packedPair + PmpMap.SubGridSide] & SubFloor) != 0)
                    {
                        packedPair += PmpMap.SubGridSide;
                    }
                    packedPair |= ExitFloorOrPassageFlag;
                }
            }
            else
            {
                packedPair = (byte)(((weightedScore >> 2) & ExitFloorOrPassageFlag) | bestPair);
            }

            ExitPairAndFlags[tile] = packedPair;
            // FUN_005a6350:279/:287: 50 is selected only when the category high byte is zero;
            // a floor-bearing best cell or plane bit 3 selects 20. The earlier:236-245 branches
            // select -1 and leave +0x12 stale.
            BaseCost[tile] = (sbyte)((weightedScore >> 8) != 0 ? 20 : 50);
        }

        bool TileWindowAllSolid(int tx, int ty)
        {
            if (_nativeTerrainFlags == null || tx + 1 >= Width || ty + 1 >= Height) return false;
            int tile = ty * Width + tx;
            return (_nativeTerrainFlags[tile] &
                    _nativeTerrainFlags[tile + 1] &
                    _nativeTerrainFlags[tile + Width] &
                    _nativeTerrainFlags[tile + Width + 1] & 0x80) != 0;
        }

        // ---- tile-graph queries for Calc1 (P1) ----
        public bool TileValid(int tx, int ty) => EffectiveTileBaseCost(tx, ty) != -1;
        public bool TileHasFloorOrPassage(int tx, int ty) =>
            InTile(tx, ty) && EffectiveTileHasFloorOrPassage(ty * Width + tx);
        public int TileBaseCost(int tx, int ty) => EffectiveTileBaseCost(tx, ty);

        public bool HasFloorOrPassageFlag(int tile) =>
            (ExitPairAndFlags[tile] & ExitFloorOrPassageFlag) != 0;

        public byte PackedExitPair(int tile) => (byte)(ExitPairAndFlags[tile] & ExitPairMask);

        public int ExitSubX(int tile) => ExitPairAndFlags[tile] & ExitSubXMask;

        public int ExitSubY(int tile) => (ExitPairAndFlags[tile] & ExitSubYMask) >> 3;

        public int EffectiveTileBaseCost(int tx, int ty)
        {
            if (!InTile(tx, ty)) return -1;
            return EffectiveTileBaseCost(ty * Width + tx);
        }

        public int EffectiveTileBaseCost(int tile)
        {
            if (_hardBlockCount[tile] > 0) return -1;
            int raw = BaseCost[tile];
            if (raw == -1 && _climbTileCount[tile] == 0) return -1;
            long cost = (raw == -1 ? 20 : raw) + (long)_costBias[tile];
            return (int)System.Math.Clamp(cost, 1L, int.MaxValue / 4L);
        }

        public bool EffectiveTileHasFloorOrPassage(int tile) =>
            HasFloorOrPassageFlag(tile) || _climbTileCount[tile] > 0;

        public byte EffectivePackedExitPair(int tile)
        {
            // Influences are strictly additive: the native ladder writer stamps only the −20 cost
            // rectangle onto the influence layers (FUN_0060b600:88-98 — obj+0x264 = 0xffffffec,
            // obj+0x268 = 4); no influence ever rewrites the FUN_005a6350-derived packed exit pair
            // (tile+0x12). A tile whose terrain crossing is a real floor cell therefore keeps it
            // even while a ladder band overlaps the tile — overriding it would move corridor floor
            // tiles onto a wall-plane column cell, giving every route through the corridor a
            // phantom climb run. The ladder column may substitute a crossing only where the terrain
            // offers none: wall tiles the route climbs through, and overlay-only tiles.
            int baseSub = tile * 64;
            byte terrainPair = PackedExitPair(tile);
            if (BaseCost[tile] != -1 && (_sub[baseSub + terrainPair] & SubFloor) != 0)
                return terrainPair;
            int bestScore = int.MinValue;
            int bestSub = terrainPair;
            for (int sub = 0; sub < 64; sub++)
            {
                if (_ladderSubCount[baseSub + sub] == 0) continue;
                int row = sub >> 3;
                int col = sub & 7;
                int score = -Abs(4 - row) - Abs(4 - col);
                if (score <= bestScore) continue;
                bestScore = score;
                bestSub = sub;
            }
            if (bestScore != int.MinValue) return (byte)bestSub;
            if (BaseCost[tile] != -1) return terrainPair;
            bestSub = 0x24;
            for (int sub = 0; sub < 64; sub++)
            {
                if (_climbSubCount[baseSub + sub] == 0) continue;
                int row = sub >> 3;
                int col = sub & 7;
                int score = -Abs(4 - row) - Abs(4 - col);
                if (score <= bestScore) continue;
                bestScore = score;
                bestSub = sub;
            }
            return (byte)bestSub;
        }

        public void SetInfluence(
            int sourceHandle,
            int x0,
            int y0,
            int x1,
            int y1,
            int cost,
            bool addsClimbSurface,
            NavTransportKind transportKind = NavTransportKind.None,
            int? climbX0 = null,
            int? climbY0 = null,
            int? climbX1 = null,
            int? climbY1 = null)
        {
            if (_influences.TryGetValue(sourceHandle, out var previous))
                ApplyInfluence(previous, -1);

            if (cost == 0 && !addsClimbSurface)
            {
                _influences.Remove(sourceHandle);
                Version++;
                return;
            }

            int influenceX0 = System.Math.Min(x0, x1);
            int influenceY0 = System.Math.Min(y0, y1);
            int influenceX1 = System.Math.Max(x0, x1);
            int influenceY1 = System.Math.Max(y0, y1);
            bool hasClimbBounds = climbX0.HasValue && climbY0.HasValue
                && climbX1.HasValue && climbY1.HasValue;
            int surfaceX0 = hasClimbBounds
                ? System.Math.Min(climbX0.GetValueOrDefault(), climbX1.GetValueOrDefault())
                : influenceX0;
            int surfaceY0 = hasClimbBounds
                ? System.Math.Min(climbY0.GetValueOrDefault(), climbY1.GetValueOrDefault())
                : influenceY0;
            int surfaceX1 = hasClimbBounds
                ? System.Math.Max(climbX0.GetValueOrDefault(), climbX1.GetValueOrDefault())
                : influenceX1;
            int surfaceY1 = hasClimbBounds
                ? System.Math.Max(climbY0.GetValueOrDefault(), climbY1.GetValueOrDefault())
                : influenceY1;
            var influence = new InfluenceRecord(
                influenceX0,
                influenceY0,
                influenceX1,
                influenceY1,
                cost,
                addsClimbSurface,
                transportKind,
                surfaceX0,
                surfaceY0,
                surfaceX1,
                surfaceY1);
            _influences[sourceHandle] = influence;
            ApplyInfluence(influence, 1);
            Version++;
        }

        public void RemoveInfluence(int sourceHandle)
        {
            if (!_influences.Remove(sourceHandle, out var influence)) return;
            ApplyInfluence(influence, -1);
            Version++;
        }

        public bool HasInfluence(int sourceHandle) => _influences.ContainsKey(sourceHandle);

        public bool TryGetTransportInfluence(
            int subX,
            int subY,
            out NavTransportKind kind,
            out int sourceHandle)
        {
            kind = NavTransportKind.None;
            sourceHandle = -1;
            foreach (var pair in _influences)
            {
                var influence = pair.Value;
                if (influence.TransportKind == NavTransportKind.None
                    || subX < influence.TransportX0 || subX > influence.TransportX1
                    || subY < influence.TransportY0 || subY > influence.TransportY1)
                    continue;
                if (influence.TransportKind <= kind) continue;
                kind = influence.TransportKind;
                sourceHandle = pair.Key;
            }
            return kind != NavTransportKind.None;
        }

        // The column is consumed before the unchanged floor-band emitter builds its mount and
        // dismount legs; callers never need to infer it from the wider cost rectangle.
        public bool TryGetLadderColumn(int sourceHandle, out int subX)
        {
            subX = 0;
            if (!_influences.TryGetValue(sourceHandle, out var influence)
                || influence.TransportKind != NavTransportKind.Ladder
                || influence.ClimbX0 != influence.ClimbX1)
                return false;
            subX = influence.ClimbX0;
            return true;
        }

        // Diggability byte for the Calc1 edge cost: BDB aux[0x24] (the centre byte) of the tile's
        // block — `dig(t) = BDB.block[t.block_idx].byte[+0x24]`, doc §2.3.
        public int TileDigByte(int tx, int ty)
        {
            if (!InTile(tx, ty)) return 0;
            uint blk = _blockIdx[ty * Width + tx];
            return blk >= _nBlocks ? 0 : _bdb.AuxCenter((int)blk);
        }

        // EFFPASSWALK — FUN_005a6b40 (Docs/re_parts/carve-validator.md §2).
        // Re-derives the +0x3c passability from the per-sub-cell heightmap — the engine runs this over
        // every dirty rect after a carve, before the FUN_005a6350 tile-graph pass (EFFBLOCK).
        // Per sub-cell:
        // precondition: the (+4,+4)-offset tile's authored tile+0x18 bit 7 must be clear;
        // open band: all 31 relative cells (the fan above, dy −15..−1, dx ±7) have h < 14;
        // floor/wall: value 3 iff h(sy−1)+2 < h and h(sy−11)+2 < h (first solid row)
        // else if h+2 < h(sy+1): value = (h(sy−10)+2 < h(sy+1)) ? 3: 1 (last open row)
        // else value 1 (open band, no step ⇒ climbable wall);
        // blocked ⇒ 0. The writer overwrites the byte wholesale (as FUN_00596550 does).
        private static readonly (sbyte Dx, sbyte Dy)[] OpenBand =
        {
            (0, -8), (-7, -15), (7, -15), (0, -15), (0, -1), (-7, -8), (7, -8),
            (-4, -3), (4, -3), (-7, -5), (7, -5), (-7, -12), (7, -12),
            (-4, -15), (4, -15), (0, -12), (0, -4), (-4, -8), (4, -8),
            (-3, -13), (3, -13), (-5, -11), (5, -11), (-2, -10), (2, -10),
            (-2, -6), (2, -6), (-5, -5), (5, -5), (-2, -2), (2, -2),
        };
        private const int OpenThreshold = 0xE;
        private const int EffPassWalkLeftInset = 0x18;
        private const int EffPassWalkTopInset = 0x10;
        private const int EffPassWalkRightInset = 0x18;
        private const int EffPassWalkBottomInset = 0x18;
        private const int EffBlockLeftInset = 0x12;
        private const int EffBlockTopInset = 0x0E;
        private const int EffBlockRightInset = 0x13;
        private const int EffBlockBottomInset = 0x15;

        public void ReDeriveSubGrid(int sx0, int sy0, int sx1, int sy1)
        {
            // FUN_005a6b40:55-75 clamps its exclusive rectangle to
            // [worldMinX+0x18, worldMaxX-0x18) × [worldMinY+0x10, worldMaxY-0x18).
            sx0 = System.Math.Max(EffPassWalkLeftInset, sx0);
            sy0 = System.Math.Max(EffPassWalkTopInset, sy0);
            sx1 = System.Math.Min(SubW - EffPassWalkRightInset - 1, sx1);
            sy1 = System.Math.Min(SubH - EffPassWalkBottomInset - 1, sy1);
            if (sx1 < sx0 || sy1 < sy0) return;
            for (int sy = sy0; sy <= sy1; sy++)
            for (int sx = sx0; sx <= sx1; sx++)
            {
                byte v = DeriveSubCell(sx, sy);
                _sub[((sy >> 3) * Width + (sx >> 3)) * 64 + ((sy & 7) << 3) + (sx & 7)] = v;
            }
        }

        public byte DeriveSubCell(int sx, int sy)
        {
            // Precondition: tile at ((sx+4)>>3, (sy+4)>>3) must not carry authored bit 7
            // (FUN_005a6b40:15). There is no hasTpl predicate in the native routine.
            int ptx = (sx + 4) >> 3, pty = (sy + 4) >> 3;
            if (!InTile(ptx, pty)) return 0;
            int preconditionTile = pty * Width + ptx;
            // Native force-wall override (tile+0x18 bit 7, EFFPASSWALK FUN_005a6b40:110).
            if (_nativeTerrainFlags != null &&
                (_nativeTerrainFlags[preconditionTile] & 0x80) != 0)
            {
                return 0;
            }
            // The 31-cell gnome capsule above the foot must be < 0x0e. Each sample resolves the
            // current node block id (FUN_00595e40:69-77), after FUN_00596b80's dig swap
            // (FUN_005a6b40:116-419).
            if (TryGetEffPassWalkBlockingProbe(sx, sy, out _, out _, out _)) return 0;
            // Floor(3) vs wall(1): the >2 detail-byte jump and nested confirmations
            // (FUN_005a6b40:419-474). The second fallback reads y-10: native VA 0x5a7381 builds
            // that row offset and 0x5a76a8 passes it to the bounds-checked buffer accessor.
            int here = CurrentBlockDetailAt(sx, sy);
            int above1 = CurrentBlockDetailAt(sx, sy - 1);
            int below1 = CurrentBlockDetailAt(sx, sy + 1);
            if (above1 + 2 < here && CurrentBlockDetailAt(sx, sy - 11) + 2 < here) return 3;
            if (here + 2 < below1)
                return (byte)(CurrentBlockDetailAt(sx, sy - 10) + 2 < below1 ? 3 : 1);
            return 1;
        }

        public bool TryGetEffPassWalkBlockingProbe(
            int sx,
            int sy,
            out int dx,
            out int dy,
            out int detail)
        {
            foreach ((sbyte probeX, sbyte probeY) in OpenBand)
            {
                detail = CurrentBlockDetailAt(sx + probeX, sy + probeY);
                if (detail < OpenThreshold) continue;
                dx = probeX;
                dy = probeY;
                return true;
            }
            dx = 0;
            dy = 0;
            detail = 0;
            return false;
        }

        // The post-dig contract (carve-validator §3/§5): EFFPASSWALK over the dirty rect (expanded by
        // the band's influence: a height change affects cells up to 15 rows below it, 1 above, ±7 cols),
        // then EFFBLOCK (DeriveTile) over the covered tiles — in that order.
        public void RevalidateRect(int sx0, int sy0, int sx1, int sy1)
        {
            // Synchronous nav re-derive — a hitch candidate on big rects (world growth / apron
            // extension). Named so the freeze watchdog can attribute slow frames.
            using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section("nav.revalidate", 50);
            int rx0 = sx0 - 8, rx1 = sx1 + 8;
            int ry0 = sy0 - 2, ry1 = sy1 + 16;
            ReDeriveSubGrid(rx0, ry0, rx1, ry1);
            DeriveEffBlockSubRect(rx0, ry0, rx1, ry1);
            Version++;
        }

        private void DeriveEffBlockSubRect(int sx0, int sy0, int sx1, int sy1)
        {
            // FUN_005a6350:40-69 clamps the half-open subcell rect to
            // [worldMinX+0x12, worldMaxX-0x13) × [worldMinY+0x0e, worldMaxY-0x15),
            // then processes every tile intersecting that clipped rect.
            int clippedX0 = System.Math.Max(EffBlockLeftInset, sx0);
            int clippedY0 = System.Math.Max(EffBlockTopInset, sy0);
            int clippedX1Exclusive = System.Math.Min(SubW - EffBlockRightInset, sx1 + 1);
            int clippedY1Exclusive = System.Math.Min(SubH - EffBlockBottomInset, sy1 + 1);
            if (clippedX1Exclusive <= clippedX0 || clippedY1Exclusive <= clippedY0) return;

            int tx0 = clippedX0 >> 3;
            int ty0 = clippedY0 >> 3;
            int tx1 = (clippedX1Exclusive - 1) >> 3;
            int ty1 = (clippedY1Exclusive - 1) >> 3;
            DeriveTileGraphRect(tx0, ty0, tx1, ty1);
        }

        // Count of walkable sub-cells over the whole map — a sparsity probe (the live game is ~0.5%).
        public int WalkableSubCellCount()
        {
            int c = 0;
            for (int i = 0; i < _sub.Length; i++) if ((_sub[i] & SubWalk) != 0) c++;
            return c;
        }

        void ApplyInfluence(InfluenceRecord influence, int direction)
        {
            int x0 = System.Math.Max(0, influence.X0);
            int y0 = System.Math.Max(0, influence.Y0);
            int x1 = System.Math.Min(SubW - 1, influence.X1);
            int y1 = System.Math.Min(SubH - 1, influence.Y1);
            if (x0 > x1 || y0 > y1) return;

            int tx0 = x0 >> 3;
            int ty0 = y0 >> 3;
            int tx1 = x1 >> 3;
            int ty1 = y1 >> 3;
            for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int tile = ty * Width + tx;
                if (influence.Cost == int.MaxValue)
                {
                    _hardBlockCount[tile] += direction;
                }
                else
                {
                    long updated = _costBias[tile] + (long)direction * influence.Cost;
                    _costBias[tile] = (int)System.Math.Clamp(updated, int.MinValue, int.MaxValue);
                }
            }

            // The native ladder writer's ±12 rectangle is a cost/type influence band
            // (FUN_0060b600), not permission to climb every column in that rectangle. Keep
            // cost stamping above and surface stamping here on its explicit traversal bounds.
            if (!influence.AddsClimbSurface) return;
            int climbX0 = System.Math.Max(0, influence.ClimbX0);
            int climbY0 = System.Math.Max(0, influence.ClimbY0);
            int climbX1 = System.Math.Min(SubW - 1, influence.ClimbX1);
            int climbY1 = System.Math.Min(SubH - 1, influence.ClimbY1);
            if (climbX0 > climbX1 || climbY0 > climbY1) return;

            int climbTileX0 = climbX0 >> 3;
            int climbTileY0 = climbY0 >> 3;
            int climbTileX1 = climbX1 >> 3;
            int climbTileY1 = climbY1 >> 3;
            for (int ty = climbTileY0; ty <= climbTileY1; ty++)
            for (int tx = climbTileX0; tx <= climbTileX1; tx++)
                _climbTileCount[ty * Width + tx] += direction;

            for (int sy = climbY0; sy <= climbY1; sy++)
            for (int sx = climbX0; sx <= climbX1; sx++)
            {
                int tile = (sy >> 3) * Width + (sx >> 3);
                int sub = tile * 64 + ((sy & 7) << 3) + (sx & 7);
                _climbSubCount[sub] += direction;
                if (influence.TransportKind == NavTransportKind.Ladder)
                    _ladderSubCount[sub] += direction;
            }
        }

        readonly struct InfluenceRecord
        {
            public readonly int X0;
            public readonly int Y0;
            public readonly int X1;
            public readonly int Y1;
            public readonly int Cost;
            public readonly bool AddsClimbSurface;
            public readonly NavTransportKind TransportKind;
            public readonly int ClimbX0;
            public readonly int ClimbY0;
            public readonly int ClimbX1;
            public readonly int ClimbY1;

            public int TransportX0 => AddsClimbSurface ? ClimbX0 : X0;
            public int TransportY0 => AddsClimbSurface ? ClimbY0 : Y0;
            public int TransportX1 => AddsClimbSurface ? ClimbX1 : X1;
            public int TransportY1 => AddsClimbSurface ? ClimbY1 : Y1;

            public InfluenceRecord(int x0, int y0, int x1, int y1, int cost,
                bool addsClimbSurface, NavTransportKind transportKind,
                int climbX0, int climbY0, int climbX1, int climbY1)
            {
                X0 = x0;
                Y0 = y0;
                X1 = x1;
                Y1 = y1;
                Cost = cost;
                AddsClimbSurface = addsClimbSurface;
                TransportKind = transportKind;
                ClimbX0 = climbX0;
                ClimbY0 = climbY0;
                ClimbX1 = climbX1;
                ClimbY1 = climbY1;
            }
        }

        bool InTile(int tx, int ty) => (uint)tx < (uint)Width && (uint)ty < (uint)Height;
        static int Abs(int v) => v < 0 ? -v : v;
    }
}
