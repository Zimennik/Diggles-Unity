#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveParticleRecord
    {
        internal OriginalSaveParticleRecord(
            int slotIndex,
            int particleKind,
            int particleCount,
            OriginalSaveRaw32 positionX,
            OriginalSaveRaw32 positionY,
            OriginalSaveRaw32 positionZ,
            OriginalSaveRaw32 directionX,
            OriginalSaveRaw32 directionY,
            OriginalSaveRaw32 directionZ,
            int rawValue50,
            byte rawFlag64,
            OriginalSaveRaw32 rawValue60,
            int mode,
            OriginalSaveRaw32 rawValue68,
            int linkedObjectToken,
            int rawValue74,
            int rawValue80)
        {
            SlotIndex = slotIndex;
            ParticleKind = particleKind;
            ParticleCount = particleCount;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            DirectionX = directionX;
            DirectionY = directionY;
            DirectionZ = directionZ;
            RawValue50 = rawValue50;
            RawFlag64 = rawFlag64;
            RawValue60 = rawValue60;
            Mode = mode;
            RawValue68 = rawValue68;
            LinkedObjectToken = linkedObjectToken;
            RawValue74 = rawValue74;
            RawValue80 = rawValue80;
        }

        public int SlotIndex { get; }
        public int ParticleKind { get; }
        public int ParticleCount { get; }
        public OriginalSaveRaw32 PositionX { get; }
        public OriginalSaveRaw32 PositionY { get; }
        public OriginalSaveRaw32 PositionZ { get; }
        public OriginalSaveRaw32 DirectionX { get; }
        public OriginalSaveRaw32 DirectionY { get; }
        public OriginalSaveRaw32 DirectionZ { get; }
        public int RawValue50 { get; }
        public byte RawFlag64 { get; }
        public OriginalSaveRaw32 RawValue60 { get; }
        public int Mode { get; }
        public OriginalSaveRaw32 RawValue68 { get; }
        public int LinkedObjectToken { get; }
        public int RawValue74 { get; }
        public int RawValue80 { get; }
        public bool IsObjectLinked => LinkedObjectToken > 1;
    }

    public sealed class OriginalSaveParticleDocument
    {
        private const int MaximumParticleSlotCount = 4096;

        private readonly ReadOnlyCollection<OriginalSaveParticleRecord> _records;

        internal OriginalSaveParticleDocument(
            byte[] sourcePayload,
            OriginalSaveGraphicsMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.GraphicsEntrySectionOffset);
            int end = checked((int)metadata.Marker10Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            var cursor = new Cursor(sourcePayload, start, end);
            var records = new OriginalSaveParticleRecord[metadata.GraphicsEntryCount];
            int previousIndex = -1;
            for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
            {
                int slotIndex = cursor.ReadInt32();
                if (slotIndex <= previousIndex || slotIndex >= MaximumParticleSlotCount)
                {
                    throw new InvalidDataException(
                        $"Invalid ordered native particle slot {slotIndex}.");
                }

                records[recordIndex] = new OriginalSaveParticleRecord(
                    slotIndex,
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadInt32(),
                    cursor.ReadByte(),
                    cursor.ReadRaw32(),
                    cursor.ReadInt32(),
                    cursor.ReadRaw32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32());
                previousIndex = slotIndex;
            }

            int terminator = cursor.ReadInt32();
            if (terminator != -1)
                throw new InvalidDataException("Native particle table has no -1 terminator.");
            cursor.ExpectEnd();
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveParticleRecord> Records => _records;

        private sealed class Cursor
        {
            private readonly byte[] _source;
            private readonly int _end;

            internal Cursor(byte[] source, int start, int end)
            {
                _source = source;
                Position = start;
                _end = end;
            }

            private int Position { get; set; }

            internal byte ReadByte()
            {
                EnsureAvailable(sizeof(byte));
                return _source[Position++];
            }

            internal int ReadInt32()
            {
                EnsureAvailable(sizeof(int));
                int value = OriginalSaveWorldDocument.ReadInt32(_source, Position);
                Position += sizeof(int);
                return value;
            }

            internal OriginalSaveRaw32 ReadRaw32() => new(ReadInt32());

            internal void ExpectEnd()
            {
                if (Position != _end)
                {
                    throw new InvalidDataException(
                        $"Typed particle table ended at {Position}, expected marker 10 at {_end}.");
                }
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native particle table.");
            }
        }
    }

    public sealed class OriginalSaveRuntimeParticleSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeParticle> _records;

        internal OriginalSaveRuntimeParticleSnapshot(OriginalSaveParticleDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var records = new OriginalSaveRuntimeParticle[source.Records.Count];
            for (int index = 0; index < records.Length; index++)
                records[index] = new OriginalSaveRuntimeParticle(source.Records[index]);
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveRuntimeParticle> Records => _records;
    }

    public readonly struct OriginalSaveRuntimeParticle
    {
        internal OriginalSaveRuntimeParticle(OriginalSaveParticleRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SlotIndex = source.SlotIndex;
            ParticleKind = source.ParticleKind;
            ParticleCount = source.ParticleCount;
            PositionX = source.PositionX;
            PositionY = source.PositionY;
            PositionZ = source.PositionZ;
            DirectionX = source.DirectionX;
            DirectionY = source.DirectionY;
            DirectionZ = source.DirectionZ;
            RawValue50 = source.RawValue50;
            RawFlag64 = source.RawFlag64;
            RawValue60 = source.RawValue60;
            Mode = source.Mode;
            RawValue68 = source.RawValue68;
            LinkedObjectToken = source.LinkedObjectToken;
            RawValue74 = source.RawValue74;
            RawValue80 = source.RawValue80;
        }

        public int SlotIndex { get; }
        public int ParticleKind { get; }
        public int ParticleCount { get; }
        public OriginalSaveRaw32 PositionX { get; }
        public OriginalSaveRaw32 PositionY { get; }
        public OriginalSaveRaw32 PositionZ { get; }
        public OriginalSaveRaw32 DirectionX { get; }
        public OriginalSaveRaw32 DirectionY { get; }
        public OriginalSaveRaw32 DirectionZ { get; }
        public int RawValue50 { get; }
        public byte RawFlag64 { get; }
        public OriginalSaveRaw32 RawValue60 { get; }
        public int Mode { get; }
        public OriginalSaveRaw32 RawValue68 { get; }
        public int LinkedObjectToken { get; }
        public int RawValue74 { get; }
        public int RawValue80 { get; }
        public bool IsObjectLinked => LinkedObjectToken > 1;
    }
}
