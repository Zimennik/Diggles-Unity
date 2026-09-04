#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;

    // Immutable, presentation-free world state prepared before a transactional live commit.
    public sealed class OriginalSaveRuntimeWorldSnapshot
    {
        private readonly uint[] _blockIndices;
        private readonly byte[] _zones;
        private readonly byte[] _terrainBytes;
        private readonly ushort[] _cellIds;
        private readonly ushort[] _nativeTerrainFlags;
        private readonly byte[] _navigationStates;
        private readonly byte[] _floorAndExitStates;
        private readonly bool[] _dugFloors;
        private readonly uint[] _digStates;
        private readonly uint[] _digOrderIds;
        private readonly bool[] _hasTemplate;
        private readonly byte[] _fog;
        private readonly int[] _fogBeginRows;
        private readonly int[] _fadeOffsets;
        private readonly int[] _fadeItems;
        private readonly int[] _exploredRect;
        private readonly FluidSystem.SaveState _fluid;

        internal OriginalSaveRuntimeWorldSnapshot(OriginalSaveWorldDocument world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            Width = world.Width;
            Height = world.Height;
            MinX = world.MinX;
            MinY = world.MinY;
            MaxXExclusive = world.MaxXExclusive;
            MaxYExclusive = world.MaxYExclusive;
            LightBegin = world.LightBegin;
            GroundBegin = world.GroundBegin;
            int tileCount = checked(Width * Height);
            _blockIndices = new uint[tileCount];
            _zones = new byte[tileCount];
            _terrainBytes = new byte[tileCount];
            _cellIds = new ushort[tileCount];
            _nativeTerrainFlags = new ushort[tileCount];
            _navigationStates = new byte[tileCount];
            _floorAndExitStates = new byte[tileCount];
            _dugFloors = new bool[tileCount];
            _digStates = new uint[tileCount];
            _digOrderIds = new uint[tileCount];
            _hasTemplate = new bool[tileCount];
            _fog = new byte[checked(tileCount * FogOfWarState.Players)];

            var fluidCells = new List<FluidSystem.CellSaveState>();
            const int authoritativeFluidBuffer = 1;
            for (int index = 0; index < tileCount; index++)
            {
                OriginalSaveTerrainTile tile = world.Terrain[index];
                _blockIndices[index] = tile.BlockIndex;
                _terrainBytes[index] = TerrainByte(tile.MaterialType);
                _cellIds[index] = tile.CellId;
                _nativeTerrainFlags[index] = tile.NativeTerrainFlags;
                // Original terrain records persist tile+0x06 at +4 and raw tile+0x12 at +13.
                // +0x12 remains the packed subX/subY pair plus flags. These graph bytes are
                // restored after full-map +0x3c derivation
                // (FUN_00592f90; FUN_005931f0:246-249).
                _navigationStates[index] = tile.NavigationState;
                _floorAndExitStates[index] = tile.FloorAndExitState;
                _dugFloors[index] = tile.BlockIndex == 59 &&
                    (tile.MaterialType == 1 || tile.MaterialType == 2);
                _digStates[index] = tile.DigState;
                _digOrderIds[index] = tile.DigOrderIds;
                _hasTemplate[index] = (tile.TemplateFlags & 1) != 0;
                for (int player = 0; player < FogOfWarState.Players; player++)
                    _fog[index * FogOfWarState.Players + player] = tile.FogForPlayer(player);

                // Native buffer 1 is the live cell state. Buffer 0 is a stale start-of-tick
                // snapshot, and the fluid current/previous indices select work sets instead.
                float fill = tile.FluidFillForBuffer(authoritativeFluidBuffer);
                int type = tile.FluidTypeForBuffer(authoritativeFluidBuffer) & 7;
                if (Finite(fill) && fill > 0.00001f && type != 0)
                {
                    fluidCells.Add(new FluidSystem.CellSaveState
                    {
                        X = index % Width,
                        Y = index / Width,
                        Fill = fill,
                        TypeMask = type,
                    });
                }
            }

            _fogBeginRows = Copy(world.FogBeginRows);
            BuildFogLists(world, out _fadeOffsets, out _fadeItems);
            _exploredRect = BuildExploredRect(_fog, Width, Height);
            _fluid = BuildFluidState(world.Fluid, fluidCells);
            FluidCompatibility = new OriginalFluidCompatibilityState(world);
        }

        public int Width { get; }
        public int Height { get; }
        public int MinX { get; }
        public int MinY { get; }
        public int MaxXExclusive { get; }
        public int MaxYExclusive { get; }
        public int LightBegin { get; }
        public int GroundBegin { get; }
        public OriginalFluidCompatibilityState FluidCompatibility { get; }

        public int OriginX => MinX;
        public int OriginY => MinY;
        public int LogicalWidth => MaxXExclusive;
        public int LogicalHeight => MaxYExclusive;

        public DiggableTerrain.SaveState CreateTerrainState(byte[] voxels)
        {
            if (voxels == null) throw new ArgumentNullException(nameof(voxels));
            var originalBlocks = (uint[])_blockIndices.Clone();
            for (int index = 0; index < originalBlocks.Length; index++)
                if (_dugFloors[index]) originalBlocks[index] = 0;
            BuildDigBounds(
                _digStates,
                Width,
                out int[] markX0,
                out int[] markY0,
                out int[] markX1,
                out int[] markY1);
            return new DiggableTerrain.SaveState
            {
                Width = Width,
                Height = Height,
                // Native FUN_005661c0 initializes the transient DigMark counter to zero. The
                // counter is absent from FUN_005931f0, so a new candidate session uses that default.
                PlayerDigMarkId = 0,
                BlockIdx = (uint[])_blockIndices.Clone(),
                Zone = (byte[])_zones.Clone(),
                Terrain = (byte[])_terrainBytes.Clone(),
                CellId = (ushort[])_cellIds.Clone(),
                NativeTerrainFlags = (ushort[])_nativeTerrainFlags.Clone(),
                NavigationState = (byte[])_navigationStates.Clone(),
                FloorAndExitState = (byte[])_floorAndExitStates.Clone(),
                OriginalBlock = originalBlocks,
                Dug = (bool[])_dugFloors.Clone(),
                DigState = (uint[])_digStates.Clone(),
                DigId = (uint[])_digOrderIds.Clone(),
                MarkX0 = markX0,
                MarkY0 = markY0,
                MarkX1 = markX1,
                MarkY1 = markY1,
                Voxels = (byte[])voxels.Clone(),
                HasTemplate = (bool[])_hasTemplate.Clone(),
                RenderXMin = 0,
                RenderYMin = 0,
                RenderXMax = Width - 1,
                RenderYMax = Height - 1,
            };
        }

        public BdbPmpWorldBuilder.TclWorldResult CreateWorldResult()
        {
            int tileCount = checked(Width * Height);
            var hasTemplate = new System.Collections.BitArray(tileCount);
            for (int index = 0; index < tileCount; index++)
                hasTemplate[index] = _hasTemplate[index];
            return new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = Width,
                Height = Height,
                MinX = MinX,
                MinY = MinY,
                MaxXExclusive = MaxXExclusive,
                MaxYExclusive = MaxYExclusive,
                BlockIdx = (uint[])_blockIndices.Clone(),
                Zone = (byte[])_zones.Clone(),
                Terrain = (byte[])_terrainBytes.Clone(),
                CellId = (ushort[])_cellIds.Clone(),
                NativeTerrainFlags = (ushort[])_nativeTerrainFlags.Clone(),
                HasTpl = hasTemplate,
                Voxels = new byte[checked(tileCount * PmpMap.SubGridLength)],
                DugFloor = (bool[])_dugFloors.Clone(),
                RenderXMin = 0,
                RenderYMin = 0,
                RenderXMax = Width - 1,
                RenderYMax = Height - 1,
            };
        }

        public FogOfWarState.SaveState CreateFogState()
        {
            return new FogOfWarState.SaveState
            {
                Width = Width,
                Height = Height,
                Fog = (byte[])_fog.Clone(),
                FowBegin = (int[])_fogBeginRows.Clone(),
                FadeOffsets = (int[])_fadeOffsets.Clone(),
                FadeItems = (int[])_fadeItems.Clone(),
                ExploredRect = (int[])_exploredRect.Clone(),
                Enabled = true,
                ShowFog = true,
            };
        }

        public FluidSystem.SaveState CreateFluidState()
        {
            return CloneFluid(_fluid);
        }

        private static FluidSystem.SaveState BuildFluidState(
            OriginalSaveFluidDocument fluid,
            List<FluidSystem.CellSaveState> cells)
        {
            var sources = new FluidSystem.SourceSaveState[fluid.Sources.Count];
            for (int index = 0; index < sources.Length; index++)
            {
                OriginalSaveFluidSourceRecord source = fluid.Sources[index];
                bool hasHeight = source.HasHeightLimit;
                bool hasVpf = source.HasVolumePerFrame;
                sources[index] = new FluidSystem.SourceSaveState
                {
                    Handle = source.ObjectId,
                    PositionX = source.PositionX,
                    PositionY = source.PositionY,
                    PositionZ = source.PositionZ,
                    TypeMask = source.FluidType,
                    Type = FluidTypeName(source.FluidType),
                    ConfiguredVolume = source.HasVolume
                        ? source.ConfiguredVolumeUnits / OriginalSaveFluidSourceRecord.UnitScale
                        : 0d,
                    Volume = source.HasVolume
                        ? source.RemainingVolumeUnits / OriginalSaveFluidSourceRecord.UnitScale
                        : 0d,
                    Vpf = hasVpf
                        ? source.VolumePerFrameUnits / OriginalSaveFluidSourceRecord.UnitScale
                        : 0d,
                    Height = hasHeight
                        ? DecodeHeight(source.HeightLimit)
                        : 0d,
                    HasVolume = source.HasVolume,
                    HasVpf = hasVpf,
                    HasHeight = hasHeight,
                    Vented = source.Vented,
                };
            }

            var stoppers = new FluidSystem.StopperSaveState[fluid.Stoppers.Count];
            for (int index = 0; index < stoppers.Length; index++)
            {
                OriginalSaveFluidStopperRecord stopper = fluid.Stoppers[index];
                stoppers[index] = new FluidSystem.StopperSaveState
                {
                    Handle = stopper.ObjectId,
                    StartX = stopper.MinX,
                    StartY = stopper.MinY,
                    EndX = stopper.MaxX,
                    EndY = stopper.MaxY,
                    Permeability = Math.Min(stopper.PermeabilityX, stopper.PermeabilityY),
                };
            }
            return new FluidSystem.SaveState
            {
                Enabled = true,
                Cells = cells.ToArray(),
                Sources = sources,
                Stoppers = stoppers,
            };
        }

        private static FluidSystem.SaveState CloneFluid(FluidSystem.SaveState source)
        {
            return new FluidSystem.SaveState
            {
                Enabled = source.Enabled,
                Cells = (FluidSystem.CellSaveState[])source.Cells.Clone(),
                Sources = (FluidSystem.SourceSaveState[])source.Sources.Clone(),
                Stoppers = (FluidSystem.StopperSaveState[])source.Stoppers.Clone(),
            };
        }

        private static void BuildFogLists(
            OriginalSaveWorldDocument world,
            out int[] offsets,
            out int[] items)
        {
            offsets = new int[FogOfWarState.Players + 1];
            int count = 0;
            for (int player = 0; player < FogOfWarState.Players; player++)
            {
                offsets[player] = count;
                count = checked(count + world.Fog.FadeTilesByPlayer[player].Count);
            }
            offsets[FogOfWarState.Players] = count;
            items = new int[count];
            int cursor = 0;
            for (int player = 0; player < FogOfWarState.Players; player++)
                foreach (OriginalSaveIntPair pair in world.Fog.FadeTilesByPlayer[player])
                    items[cursor++] = checked(pair.Second * world.Width + pair.First);
        }

        private static int[] BuildExploredRect(byte[] fog, int width, int height)
        {
            var result = new int[FogOfWarState.Players * 4];
            for (int player = 0; player < FogOfWarState.Players; player++)
            {
                int offset = player * 4;
                result[offset] = 0x8000;
                result[offset + 1] = 0x8000;
                result[offset + 2] = -0x8000;
                result[offset + 3] = -0x8000;
            }
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int tileOffset = (y * width + x) * FogOfWarState.Players;
                    for (int player = 0; player < FogOfWarState.Players; player++)
                    {
                        if (fog[tileOffset + player] == 0) continue;
                        int offset = player * 4;
                        result[offset] = Math.Min(result[offset], x);
                        result[offset + 1] = Math.Min(result[offset + 1], y);
                        result[offset + 2] = Math.Max(result[offset + 2], x);
                        result[offset + 3] = Math.Max(result[offset + 3], y);
                    }
                }
            }
            return result;
        }

        private static void BuildDigBounds(
            uint[] digStates,
            int width,
            out int[] minX,
            out int[] minY,
            out int[] maxX,
            out int[] maxY)
        {
            minX = new int[8];
            minY = new int[8];
            maxX = new int[8];
            maxY = new int[8];
            for (int player = 0; player < 8; player++) maxX[player] = maxY[player] = -1;
            for (int index = 0; index < digStates.Length; index++)
            {
                int x = index % width;
                int y = index / width;
                for (int player = 0; player < 8; player++)
                {
                    if ((digStates[index] >> (player * 4) & 0xFu) == 0) continue;
                    if (maxX[player] < 0)
                    {
                        minX[player] = maxX[player] = x;
                        minY[player] = maxY[player] = y;
                    }
                    else
                    {
                        minX[player] = Math.Min(minX[player], x);
                        minY[player] = Math.Min(minY[player], y);
                        maxX[player] = Math.Max(maxX[player], x);
                        maxY[player] = Math.Max(maxY[player], y);
                    }
                }
            }
        }

        private static byte TerrainByte(int material)
        {
            return material switch
            {
                0 => 4,
                1 => 3,
                2 => 2,
                3 => 5,
                _ => throw new InvalidOperationException($"Invalid native terrain material {material}."),
            };
        }

        private static string FluidTypeName(int type)
        {
            return type switch
            {
                1 => "water",
                2 => "sulfur",
                3 => "lava",
                4 => "lavawater",
                _ => "water",
            };
        }

        private static double DecodeHeight(float value) => value >= 3f ? value - 2f : value;

        private static int[] Copy(IReadOnlyList<int> source)
        {
            var result = new int[source.Count];
            for (int index = 0; index < result.Length; index++) result[index] = source[index];
            return result;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
