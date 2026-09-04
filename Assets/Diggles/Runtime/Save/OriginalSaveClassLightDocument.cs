#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveClassLightRecord
    {
        internal OriginalSaveClassLightRecord(
            int slotIndex,
            OriginalSaveRaw32 positionX,
            OriginalSaveRaw32 positionY,
            OriginalSaveRaw32 positionZ,
            int red,
            int green,
            int blue,
            OriginalSaveRaw32 radius,
            int sourceToken,
            OriginalSaveRaw32 trailingValue)
        {
            SlotIndex = slotIndex;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            Red = red;
            Green = green;
            Blue = blue;
            Radius = radius;
            SourceToken = sourceToken;
            TrailingValue = trailingValue;
        }

        public int SlotIndex { get; }
        public OriginalSaveRaw32 PositionX { get; }
        public OriginalSaveRaw32 PositionY { get; }
        public OriginalSaveRaw32 PositionZ { get; }
        public int Red { get; }
        public int Green { get; }
        public int Blue { get; }
        public OriginalSaveRaw32 Radius { get; }
        public int SourceToken { get; }
        public OriginalSaveRaw32 TrailingValue { get; }
        public bool IsObjectBound => SourceToken > 1;
    }

    public sealed class OriginalSaveClassLightDocument
    {
        private const int MaximumSlotCount = 2048;

        private readonly ReadOnlyCollection<OriginalSaveClassLightRecord> _records;

        internal OriginalSaveClassLightDocument(
            byte[] sourcePayload,
            OriginalSaveControlMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.ControlStateSectionOffset);
            int end = checked((int)metadata.Marker8Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            int offset = start;
            int previousSlot = -1;
            var records = new OriginalSaveClassLightRecord[metadata.ControlStateEntryCount];
            for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
            {
                int slotIndex = ReadInt32(sourcePayload, ref offset, end);
                if (slotIndex <= previousSlot || slotIndex >= MaximumSlotCount)
                {
                    throw new InvalidDataException(
                        $"Invalid ordered native class-light slot {slotIndex}.");
                }
                records[recordIndex] = new OriginalSaveClassLightRecord(
                    slotIndex,
                    ReadRaw32(sourcePayload, ref offset, end),
                    ReadRaw32(sourcePayload, ref offset, end),
                    ReadRaw32(sourcePayload, ref offset, end),
                    ReadInt32(sourcePayload, ref offset, end),
                    ReadInt32(sourcePayload, ref offset, end),
                    ReadInt32(sourcePayload, ref offset, end),
                    ReadRaw32(sourcePayload, ref offset, end),
                    ReadInt32(sourcePayload, ref offset, end),
                    ReadRaw32(sourcePayload, ref offset, end));
                previousSlot = slotIndex;
            }

            int terminator = ReadInt32(sourcePayload, ref offset, end);
            if (terminator != -1)
                throw new InvalidDataException("Native class-light table has no -1 terminator.");
            if (offset != end)
                throw new InvalidDataException("Typed class-light parsing did not consume its section.");
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveClassLightRecord> Records => _records;

        private static OriginalSaveRaw32 ReadRaw32(byte[] source, ref int offset, int end) =>
            new(ReadInt32(source, ref offset, end));

        private static int ReadInt32(byte[] source, ref int offset, int end)
        {
            if (offset < 0 || offset > end - sizeof(int))
                throw new EndOfStreamException("Unexpected end of native class-light table.");
            int value = OriginalSaveWorldDocument.ReadInt32(source, offset);
            offset += sizeof(int);
            return value;
        }
    }

    public sealed class OriginalSaveRuntimeClassLightSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeClassLight> _records;

        internal OriginalSaveRuntimeClassLightSnapshot(OriginalSaveClassLightDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var records = new OriginalSaveRuntimeClassLight[source.Records.Count];
            for (int index = 0; index < records.Length; index++)
                records[index] = new OriginalSaveRuntimeClassLight(source.Records[index]);
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveRuntimeClassLight> Records => _records;
    }

    public readonly struct OriginalSaveRuntimeClassLight
    {
        internal OriginalSaveRuntimeClassLight(OriginalSaveClassLightRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SlotIndex = source.SlotIndex;
            PositionX = source.PositionX;
            PositionY = source.PositionY;
            PositionZ = source.PositionZ;
            Red = source.Red;
            Green = source.Green;
            Blue = source.Blue;
            Radius = source.Radius;
            SourceToken = source.SourceToken;
            TrailingValue = source.TrailingValue;
        }

        public int SlotIndex { get; }
        public OriginalSaveRaw32 PositionX { get; }
        public OriginalSaveRaw32 PositionY { get; }
        public OriginalSaveRaw32 PositionZ { get; }
        public int Red { get; }
        public int Green { get; }
        public int Blue { get; }
        public OriginalSaveRaw32 Radius { get; }
        public int SourceToken { get; }
        public OriginalSaveRaw32 TrailingValue { get; }
        public bool IsObjectBound => SourceToken > 1;
    }
}
