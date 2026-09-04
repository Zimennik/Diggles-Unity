#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;

    public sealed class OriginalSaveWorldDocument
    {
        private const int DimensionStateSize = 6 * sizeof(int);
        private const int TerrainRecordSize = 53;
        private const int WorldStateValueCount = 10;

        private readonly ReadOnlyCollection<int> _fogBeginRows;

        internal OriginalSaveWorldDocument(byte[] payload, OriginalSaveWorldMetadata metadata)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

            int terrainOffset = checked((int)metadata.SectionOffset + DimensionStateSize);
            long stateOffset = checked(
                (long)terrainOffset + (long)metadata.TerrainRecordCount * TerrainRecordSize);
            long stateEnd = checked(stateOffset + WorldStateValueCount * sizeof(int));
            if (terrainOffset < 0 || stateEnd > payload.Length)
                throw new ArgumentException("The native world section exceeds the archive payload.", nameof(payload));

            Terrain = new OriginalSaveTerrainCollection(
                payload, terrainOffset, metadata.TerrainRecordCount);

            var fogBeginRows = new int[8];
            for (int player = 0; player < fogBeginRows.Length; player++)
                fogBeginRows[player] = ReadInt32(payload, checked((int)stateOffset + player * sizeof(int)));
            _fogBeginRows = Array.AsReadOnly(fogBeginRows);
            LightBegin = ReadInt32(payload, checked((int)stateOffset + 8 * sizeof(int)));
            GroundBegin = ReadInt32(payload, checked((int)stateOffset + 9 * sizeof(int)));

            int fluidOffset = checked((int)stateEnd);
            Fluid = new OriginalSaveFluidDocument(payload, fluidOffset, metadata);
            Fog = new OriginalSaveFogDocument(payload, Fluid.EndOffset, metadata);
        }

        public OriginalSaveWorldMetadata Metadata { get; }
        public int Width => Metadata.Width;
        public int Height => Metadata.Height;
        public int MinX => Metadata.MinX;
        public int MinY => Metadata.MinY;
        public int MaxXExclusive => Metadata.MaxXExclusive;
        public int MaxYExclusive => Metadata.MaxYExclusive;

        // Compatibility aliases retained while callers migrate to the verified CMap bound names.
        public int OriginX => MinX;
        public int OriginY => MinY;
        public int LogicalWidth => MaxXExclusive;
        public int LogicalHeight => MaxYExclusive;
        public OriginalSaveTerrainCollection Terrain { get; }
        public OriginalSaveFluidDocument Fluid { get; }
        public OriginalSaveFogDocument Fog { get; }

        // Native CFogOfWar +0x0C int[8]. Rows above this value are permanently revealed.
        public IReadOnlyList<int> FogBeginRows => _fogBeginRows;

        // SetLightBegin writes DAT_007683D8; rows above this line use the native sky-light rule.
        public int LightBegin { get; }

        public int UnknownWorldState => LightBegin;

        // SetGroundBegin writes game+0x4410. SetLightBegin updates both serialized rows.
        public int GroundBegin { get; }

        public int UnknownGameState => GroundBegin;

        internal static int ReadInt32(byte[] source, int offset)
        {
            return unchecked((int)ReadUInt32(source, offset));
        }

        internal static uint ReadUInt32(byte[] source, int offset)
        {
            return (uint)(source[offset]
                | source[offset + 1] << 8
                | source[offset + 2] << 16
                | source[offset + 3] << 24);
        }

        internal static ushort ReadUInt16(byte[] source, int offset)
        {
            return (ushort)(source[offset] | source[offset + 1] << 8);
        }

        internal static float ReadSingle(byte[] source, int offset)
        {
            return BitConverter.Int32BitsToSingle(ReadInt32(source, offset));
        }
    }

    public sealed class OriginalSaveTerrainCollection : IReadOnlyList<OriginalSaveTerrainTile>
    {
        private const int RecordSize = 53;

        private readonly byte[] _payload;
        private readonly int _offset;

        internal OriginalSaveTerrainCollection(byte[] payload, int offset, int count)
        {
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            _offset = offset;
            Count = count;
        }

        public int Count { get; }

        public OriginalSaveTerrainTile this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                int offset = checked(_offset + index * RecordSize);
                return OriginalSaveTerrainTile.Read(_payload, offset);
            }
        }

        public IEnumerator<OriginalSaveTerrainTile> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public readonly struct OriginalSaveTerrainTile
    {
        internal OriginalSaveTerrainTile(
            uint blockIndex,
            byte navigationState,
            uint digState,
            uint digOrderIds,
            byte floorAndExitState,
            byte templateFlags,
            ushort cellId,
            uint terrainState,
            uint unknown1C,
            uint unknown20,
            uint unknown24,
            uint unknown28,
            uint unknown2C,
            uint unknown30,
            ulong fogState)
        {
            BlockIndex = blockIndex;
            NavigationState = navigationState;
            DigState = digState;
            DigOrderIds = digOrderIds;
            FloorAndExitState = floorAndExitState;
            TemplateFlags = templateFlags;
            CellId = cellId;
            TerrainState = terrainState;
            Unknown1C = unknown1C;
            Unknown20 = unknown20;
            Unknown24 = unknown24;
            Unknown28 = unknown28;
            Unknown2C = unknown2C;
            Unknown30 = unknown30;
            FogState = fogState;
        }

        public uint BlockIndex { get; }
        public byte NavigationState { get; }
        public uint DigState { get; }
        public uint DigOrderIds { get; }
        // Legacy schema name: raw tile+0x12 packed pair/flags byte.
        public byte FloorAndExitState { get; }
        public byte TemplateFlags { get; }
        public ushort CellId { get; }
        public uint TerrainState { get; }
        public ushort NativeTerrainFlags => unchecked((ushort)TerrainState);
        public int MaterialType => (int)(TerrainState & 3);
        public bool IsNavigationExposed => (NativeTerrainFlags & 0x80) == 0;

        // Native tile +0x1A/+0x1B store the fluid type for simulation buffers 0 and 1.
        // Both bytes share the serialized UInt32 with the terrain flags at +0x18/+0x19.
        public byte FluidTypeForBuffer(int buffer)
        {
            ValidateFluidBuffer(buffer);
            return (byte)(TerrainState >> (16 + buffer * 8));
        }

        // Native tile +0x1C/+0x20 are the fluid fill values for simulation buffers 0 and 1.
        public uint Unknown1C { get; }
        public uint Unknown20 { get; }

        public float FluidFillForBuffer(int buffer)
        {
            ValidateFluidBuffer(buffer);
            uint bits = buffer == 0 ? Unknown1C : Unknown20;
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        // Native tile +0x24..+0x33 are fluid flow/cache values. They remain lossless and unnamed
        // until their individual update equations are proven from the original executable.
        public uint Unknown24 { get; }
        public uint Unknown28 { get; }
        public uint Unknown2C { get; }
        public uint Unknown30 { get; }

        // Eight little-endian bytes mirror native tile +0x34[player].
        public ulong FogState { get; }

        public int DigStateForPlayer(int player)
        {
            ValidatePlayer(player);
            return (int)(DigState >> (player * 4) & 0xF);
        }

        public int DigOrderIdForPlayer(int player)
        {
            ValidatePlayer(player);
            return (int)(DigOrderIds >> (player * 4) & 0xF);
        }

        public byte FogForPlayer(int player)
        {
            ValidatePlayer(player);
            return (byte)(FogState >> (player * 8));
        }

        internal static OriginalSaveTerrainTile Read(byte[] source, int offset)
        {
            uint fogLow = OriginalSaveWorldDocument.ReadUInt32(source, offset + 45);
            uint fogHigh = OriginalSaveWorldDocument.ReadUInt32(source, offset + 49);
            return new OriginalSaveTerrainTile(
                OriginalSaveWorldDocument.ReadUInt32(source, offset),
                source[offset + 4],
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 5),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 9),
                source[offset + 13],
                source[offset + 14],
                OriginalSaveWorldDocument.ReadUInt16(source, offset + 15),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 17),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 21),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 25),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 29),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 33),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 37),
                OriginalSaveWorldDocument.ReadUInt32(source, offset + 41),
                fogLow | (ulong)fogHigh << 32);
        }

        static void ValidatePlayer(int player)
        {
            if ((uint)player >= 8) throw new ArgumentOutOfRangeException(nameof(player));
        }

        static void ValidateFluidBuffer(int buffer)
        {
            if ((uint)buffer >= 2) throw new ArgumentOutOfRangeException(nameof(buffer));
        }
    }
}
