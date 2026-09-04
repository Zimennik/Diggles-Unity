#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;

    public sealed class OriginalSaveFluidDocument
    {
        const int SourceRecordSize = 37;
        const int StopperRecordSize = 41;
        const int SparseSetCount = 4;

        readonly ReadOnlyCollection<OriginalSaveIntPairCollection> _sparseSets;

        internal OriginalSaveFluidDocument(
            byte[] payload,
            int offset,
            OriginalSaveWorldMetadata metadata)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int sourceCount = ReadAndValidateCount(
                payload, ref offset, metadata.FluidPrimaryCount, "fluid source");
            Sources = new OriginalSaveFluidSourceCollection(payload, offset, sourceCount);
            offset = checked(offset + sourceCount * SourceRecordSize);

            int stopperCount = ReadAndValidateCount(
                payload, ref offset, metadata.FluidSecondaryCount, "fluid stopper");
            Stoppers = new OriginalSaveFluidStopperCollection(payload, offset, stopperCount);
            offset = checked(offset + stopperCount * StopperRecordSize);

            CurrentBufferIndex = OriginalSaveWorldDocument.ReadInt32(payload, offset);
            PreviousBufferIndex = OriginalSaveWorldDocument.ReadInt32(payload, offset + sizeof(int));
            offset = checked(offset + 2 * sizeof(int));

            var sparseSets = new OriginalSaveIntPairCollection[SparseSetCount];
            for (int set = 0; set < sparseSets.Length; set++)
            {
                int count = ReadAndValidateCount(
                    payload, ref offset, metadata.FluidSparseCounts[set], "fluid sparse state");
                sparseSets[set] = new OriginalSaveIntPairCollection(payload, offset, count);
                offset = checked(offset + count * OriginalSaveIntPairCollection.RecordSize);
            }
            _sparseSets = Array.AsReadOnly(sparseSets);

            EnsureAvailable(payload, offset, 1, "fluid debug flag");
            DebugEnabled = payload[offset] != 0;
            EndOffset = offset + 1;
        }

        public OriginalSaveFluidSourceCollection Sources { get; }
        public OriginalSaveFluidStopperCollection Stoppers { get; }
        public int CurrentBufferIndex { get; }
        public int PreviousBufferIndex { get; }
        public IReadOnlyList<OriginalSaveIntPairCollection> SparseSets => _sparseSets;
        public bool DebugEnabled { get; }
        public int EndOffset { get; }

        internal static int ReadAndValidateCount(
            byte[] payload, ref int offset, int expected, string label)
        {
            EnsureAvailable(payload, offset, sizeof(int), label + " count");
            int actual = OriginalSaveWorldDocument.ReadInt32(payload, offset);
            offset = checked(offset + sizeof(int));
            if (actual != expected)
                throw new ArgumentException(
                    $"The {label} count changed between archive validation and document mapping.",
                    nameof(payload));
            return actual;
        }

        internal static void EnsureAvailable(byte[] payload, int offset, int size, string label)
        {
            if (offset < 0 || size < 0 || (long)offset + size > payload.Length)
                throw new ArgumentException($"The {label} exceeds the archive payload.", nameof(payload));
        }
    }

    public sealed class OriginalSaveFluidSourceCollection : IReadOnlyList<OriginalSaveFluidSourceRecord>
    {
        const int RecordSize = 37;

        readonly byte[] _payload;
        readonly int _offset;

        internal OriginalSaveFluidSourceCollection(byte[] payload, int offset, int count)
        {
            OriginalSaveFluidDocument.EnsureAvailable(
                payload, offset, checked(count * RecordSize), "fluid source records");
            _payload = payload;
            _offset = offset;
            Count = count;
        }

        public int Count { get; }

        public OriginalSaveFluidSourceRecord this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                int offset = checked(_offset + index * RecordSize);
                return new OriginalSaveFluidSourceRecord(
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 4),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 8),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 12),
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset + 16),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 20),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 24),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 28),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 32),
                    _payload[offset + 36] != 0);
            }
        }

        public IEnumerator<OriginalSaveFluidSourceRecord> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public readonly struct OriginalSaveFluidSourceRecord
    {
        public const float UnitScale = 128f;
        public const float DisabledValue = -1f;

        public OriginalSaveFluidSourceRecord(
            int objectId,
            float positionX,
            float positionY,
            float positionZ,
            int fluidType,
            float state14,
            float state18,
            float state1C,
            float state20,
            bool vented)
        {
            ObjectId = objectId;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            FluidType = fluidType;
            State14 = state14;
            State18 = state18;
            State1C = state1C;
            State20 = state20;
            Vented = vented;
        }

        public int ObjectId { get; }
        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public int FluidType { get; }

        // SetFSource writes -volume * 128 to both +0x14 and +0x18. The first value retains the
        // configured amount while the second is consumed by the simulation.
        public float ConfiguredVolumeUnits => State14;
        public float RemainingVolumeUnits => State18;

        // -height and -vpf are mutually exclusive. SetFSource stores -vpf * 128 at +0x20.
        public float HeightLimit => State1C;
        public float VolumePerFrameUnits => State20;
        public bool HasVolume => RemainingVolumeUnits >= 0f;
        public bool HasHeightLimit => HeightLimit != DisabledValue;
        public bool HasVolumePerFrame => VolumePerFrameUnits != DisabledValue;

        internal float State14 { get; }
        internal float State18 { get; }
        internal float State1C { get; }
        internal float State20 { get; }
        public bool Vented { get; }
    }

    public sealed class OriginalSaveFluidStopperCollection : IReadOnlyList<OriginalSaveFluidStopperRecord>
    {
        const int RecordSize = 41;

        readonly byte[] _payload;
        readonly int _offset;

        internal OriginalSaveFluidStopperCollection(byte[] payload, int offset, int count)
        {
            OriginalSaveFluidDocument.EnsureAvailable(
                payload, offset, checked(count * RecordSize), "fluid stopper records");
            _payload = payload;
            _offset = offset;
            Count = count;
        }

        public int Count { get; }

        public OriginalSaveFluidStopperRecord this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                int offset = checked(_offset + index * RecordSize);
                return new OriginalSaveFluidStopperRecord(
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 4),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 8),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 12),
                    _payload[offset + 16] != 0,
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset + 17),
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset + 21),
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset + 25),
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset + 29),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 33),
                    OriginalSaveWorldDocument.ReadSingle(_payload, offset + 37));
            }
        }

        public IEnumerator<OriginalSaveFluidStopperRecord> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public readonly struct OriginalSaveFluidStopperRecord
    {
        public OriginalSaveFluidStopperRecord(
            int objectId,
            float cachedPositionX,
            float cachedPositionY,
            float cachedPositionZ,
            bool dirty,
            int minX,
            int minY,
            int maxX,
            int maxY,
            float permeabilityX,
            float permeabilityY)
        {
            ObjectId = objectId;
            CachedPositionX = cachedPositionX;
            CachedPositionY = cachedPositionY;
            CachedPositionZ = cachedPositionZ;
            Dirty = dirty;
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            PermeabilityX = permeabilityX;
            PermeabilityY = permeabilityY;
        }

        public int ObjectId { get; }
        public float CachedPositionX { get; }
        public float CachedPositionY { get; }
        public float CachedPositionZ { get; }
        public bool Dirty { get; }
        public int MinX { get; }
        public int MinY { get; }
        public int MaxX { get; }
        public int MaxY { get; }
        public float PermeabilityX { get; }
        public float PermeabilityY { get; }
    }

    public sealed class OriginalSaveIntPairCollection : IReadOnlyList<OriginalSaveIntPair>
    {
        internal const int RecordSize = 2 * sizeof(int);

        readonly byte[] _payload;
        readonly int _offset;

        internal OriginalSaveIntPairCollection(byte[] payload, int offset, int count)
        {
            OriginalSaveFluidDocument.EnsureAvailable(
                payload, offset, checked(count * RecordSize), "native pair records");
            _payload = payload;
            _offset = offset;
            Count = count;
        }

        public int Count { get; }

        public OriginalSaveIntPair this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                int offset = checked(_offset + index * RecordSize);
                return new OriginalSaveIntPair(
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset),
                    OriginalSaveWorldDocument.ReadInt32(_payload, offset + sizeof(int)));
            }
        }

        public IEnumerator<OriginalSaveIntPair> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public readonly struct OriginalSaveIntPair
    {
        public OriginalSaveIntPair(int first, int second)
        {
            First = first;
            Second = second;
        }

        public int First { get; }
        public int Second { get; }
    }
}
