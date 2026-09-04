#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Immutable native fluid state retained beside the port's simplified live simulation.
    /// </summary>
    public sealed class OriginalFluidCompatibilityState
    {
        private readonly OriginalFluidCompatibilityTileState[] _tiles;
        private readonly IReadOnlyList<OriginalSaveFluidSourceRecord> _sources;
        private readonly IReadOnlyList<OriginalSaveFluidStopperRecord> _stoppers;
        private readonly IReadOnlyList<IReadOnlyList<OriginalSaveIntPair>> _sparseSets;

        internal OriginalFluidCompatibilityState(OriginalSaveWorldDocument world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            _tiles = new OriginalFluidCompatibilityTileState[world.Terrain.Count];
            for (int index = 0; index < _tiles.Length; index++)
            {
                OriginalSaveTerrainTile tile = world.Terrain[index];
                _tiles[index] = new OriginalFluidCompatibilityTileState(
                    tile.FluidTypeForBuffer(0),
                    tile.FluidTypeForBuffer(1),
                    tile.Unknown1C,
                    tile.Unknown20,
                    tile.Unknown24,
                    tile.Unknown28,
                    tile.Unknown2C,
                    tile.Unknown30);
            }

            var sources = new OriginalSaveFluidSourceRecord[world.Fluid.Sources.Count];
            for (int index = 0; index < sources.Length; index++)
                sources[index] = world.Fluid.Sources[index];
            _sources = Array.AsReadOnly(sources);

            var stoppers = new OriginalSaveFluidStopperRecord[world.Fluid.Stoppers.Count];
            for (int index = 0; index < stoppers.Length; index++)
                stoppers[index] = world.Fluid.Stoppers[index];
            _stoppers = Array.AsReadOnly(stoppers);

            var sparseSets = new IReadOnlyList<OriginalSaveIntPair>[world.Fluid.SparseSets.Count];
            for (int set = 0; set < sparseSets.Length; set++)
            {
                var pairs = new OriginalSaveIntPair[world.Fluid.SparseSets[set].Count];
                for (int index = 0; index < pairs.Length; index++)
                    pairs[index] = world.Fluid.SparseSets[set][index];
                sparseSets[set] = Array.AsReadOnly(pairs);
            }
            _sparseSets = Array.AsReadOnly(sparseSets);

            CurrentBufferIndex = world.Fluid.CurrentBufferIndex;
            PreviousBufferIndex = world.Fluid.PreviousBufferIndex;
            DebugEnabled = world.Fluid.DebugEnabled;
        }

        public int TileCount => _tiles.Length;
        public int CurrentBufferIndex { get; }
        public int PreviousBufferIndex { get; }
        public bool DebugEnabled { get; }
        public IReadOnlyList<OriginalSaveFluidSourceRecord> Sources => _sources;
        public IReadOnlyList<OriginalSaveFluidStopperRecord> Stoppers => _stoppers;
        public IReadOnlyList<IReadOnlyList<OriginalSaveIntPair>> SparseSets => _sparseSets;

        public OriginalFluidCompatibilityTileState GetTile(int index)
        {
            if ((uint)index >= (uint)_tiles.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _tiles[index];
        }
    }

    public readonly struct OriginalFluidCompatibilityTileState
    {
        internal OriginalFluidCompatibilityTileState(
            byte typeBuffer0,
            byte typeBuffer1,
            uint fillBuffer0Bits,
            uint fillBuffer1Bits,
            uint cacheWord0,
            uint cacheWord1,
            uint cacheWord2,
            uint cacheWord3)
        {
            TypeBuffer0 = typeBuffer0;
            TypeBuffer1 = typeBuffer1;
            FillBuffer0Bits = fillBuffer0Bits;
            FillBuffer1Bits = fillBuffer1Bits;
            CacheWord0 = cacheWord0;
            CacheWord1 = cacheWord1;
            CacheWord2 = cacheWord2;
            CacheWord3 = cacheWord3;
        }

        public byte TypeBuffer0 { get; }
        public byte TypeBuffer1 { get; }
        public uint FillBuffer0Bits { get; }
        public uint FillBuffer1Bits { get; }
        public uint CacheWord0 { get; }
        public uint CacheWord1 { get; }
        public uint CacheWord2 { get; }
        public uint CacheWord3 { get; }
    }
}
