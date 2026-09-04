#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;

    public sealed class OriginalSaveWorldPatch
    {
        private readonly Dictionary<int, OriginalSaveTerrainTilePatch> _terrain = new();

        public IReadOnlyList<int>? FogBeginRows { get; set; }
        public int? LightBegin { get; set; }
        public int? GroundBegin { get; set; }
        public int? UnknownGameState
        {
            get => GroundBegin;
            set => GroundBegin = value;
        }
        public OriginalSaveFluidPatch? Fluid { get; set; }
        public IReadOnlyList<IReadOnlyList<OriginalSaveIntPair>>? FogFadeTilesByPlayer { get; set; }

        internal IEnumerable<int> TerrainIndices => _terrain.Keys;

        public void SetTerrainTile(OriginalSaveTerrainTilePatch patch)
        {
            if (patch == null) throw new ArgumentNullException(nameof(patch));
            if (patch.Index < 0) throw new ArgumentOutOfRangeException(nameof(patch));
            _terrain[patch.Index] = patch;
        }

        internal bool TryGetTerrainTile(int index, out OriginalSaveTerrainTilePatch? patch)
        {
            return _terrain.TryGetValue(index, out patch);
        }
    }

    public sealed class OriginalSaveTerrainTilePatch
    {
        public OriginalSaveTerrainTilePatch(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            Index = index;
        }

        public int Index { get; }
        public uint? BlockIndex { get; set; }
        public byte? NavigationState { get; set; }
        public uint? DigState { get; set; }
        public uint? DigOrderIds { get; set; }
        // Legacy schema name: raw tile+0x12 packed pair/flags byte.
        public byte? FloorAndExitState { get; set; }
        public byte? TemplateFlags { get; set; }
        public ushort? CellId { get; set; }
        public uint? TerrainState { get; set; }
        public byte? FluidTypeBuffer0 { get; set; }
        public byte? FluidTypeBuffer1 { get; set; }
        public float? FluidFillBuffer0 { get; set; }
        public float? FluidFillBuffer1 { get; set; }
        public ulong? FogState { get; set; }
    }

    public sealed class OriginalSaveFluidPatch
    {
        public IReadOnlyList<OriginalSaveFluidSourceRecord>? Sources { get; set; }
        public IReadOnlyList<OriginalSaveFluidStopperRecord>? Stoppers { get; set; }
        public int? CurrentBufferIndex { get; set; }
        public int? PreviousBufferIndex { get; set; }
        public IReadOnlyList<IReadOnlyList<OriginalSaveIntPair>>? SparseSets { get; set; }
        public bool? DebugEnabled { get; set; }
    }
}
