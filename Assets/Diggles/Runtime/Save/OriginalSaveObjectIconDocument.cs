#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveObjectIconRecord
    {
        internal OriginalSaveObjectIconRecord(
            int slot,
            int objectId,
            int value,
            int opaqueRuntimeWord,
            int iconType,
            OriginalSaveRaw32 offsetX,
            OriginalSaveRaw32 offsetY,
            OriginalSaveRaw32 offsetZ)
        {
            Slot = slot;
            ObjectId = objectId;
            Value = value;
            OpaqueRuntimeWord = opaqueRuntimeWord;
            IconType = iconType;
            OffsetX = offsetX;
            OffsetY = offsetY;
            OffsetZ = offsetZ;
        }

        public int Slot { get; }
        public int ObjectId { get; }
        public int Value { get; }
        public int OpaqueRuntimeWord { get; }
        public int IconType { get; }
        public OriginalSaveRaw32 OffsetX { get; }
        public OriginalSaveRaw32 OffsetY { get; }
        public OriginalSaveRaw32 OffsetZ { get; }
        public bool IsActive => IconType != -1 && ObjectId >= 0;
    }

    public sealed class OriginalSaveObjectIconDocument
    {
        public const int SlotCount = 128;
        private const int SerializedWordsPerSlot = 7;

        private readonly ReadOnlyCollection<OriginalSaveObjectIconRecord> _slots;

        internal OriginalSaveObjectIconDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.ObjectIconStateOffset);
            int end = checked((int)metadata.ObjectIconStateEndOffset);
            int expectedLength = checked(SlotCount * SerializedWordsPerSlot * sizeof(int));
            if (start < 0 || end < start || end > sourcePayload.Length || end - start != expectedLength)
                throw new InvalidDataException("Native object-icon table has an invalid extent.");

            var records = new OriginalSaveObjectIconRecord[SlotCount];
            int offset = start;
            for (int slot = 0; slot < records.Length; slot++)
            {
                records[slot] = new OriginalSaveObjectIconRecord(
                    slot,
                    ReadInt32(sourcePayload, ref offset),
                    ReadInt32(sourcePayload, ref offset),
                    ReadInt32(sourcePayload, ref offset),
                    ReadInt32(sourcePayload, ref offset),
                    new OriginalSaveRaw32(ReadInt32(sourcePayload, ref offset)),
                    new OriginalSaveRaw32(ReadInt32(sourcePayload, ref offset)),
                    new OriginalSaveRaw32(ReadInt32(sourcePayload, ref offset)));
            }
            if (offset != end)
                throw new InvalidDataException("Typed object-icon parsing did not consume its section.");
            _slots = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveObjectIconRecord> Slots => _slots;

        private static int ReadInt32(byte[] source, ref int offset)
        {
            int value = OriginalSaveWorldDocument.ReadInt32(source, offset);
            offset += sizeof(int);
            return value;
        }
    }

    public sealed class OriginalSaveRuntimeObjectIconSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeObjectIconEntry> _slots;

        internal OriginalSaveRuntimeObjectIconSnapshot(OriginalSaveObjectIconDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var slots = new OriginalSaveRuntimeObjectIconEntry[source.Slots.Count];
            for (int index = 0; index < slots.Length; index++)
                slots[index] = new OriginalSaveRuntimeObjectIconEntry(source.Slots[index]);
            _slots = Array.AsReadOnly(slots);
        }

        public IReadOnlyList<OriginalSaveRuntimeObjectIconEntry> Slots => _slots;
    }

    public readonly struct OriginalSaveRuntimeObjectIconEntry
    {
        internal OriginalSaveRuntimeObjectIconEntry(OriginalSaveObjectIconRecord source)
        {
            Slot = source.Slot;
            ObjectId = source.ObjectId;
            Value = source.Value;
            OpaqueRuntimeWord = source.OpaqueRuntimeWord;
            IconType = source.IconType;
            OffsetX = source.OffsetX.AsSingle;
            OffsetY = source.OffsetY.AsSingle;
            OffsetZ = source.OffsetZ.AsSingle;
        }

        public int Slot { get; }
        public int ObjectId { get; }
        public int Value { get; }
        public int OpaqueRuntimeWord { get; }
        public int IconType { get; }
        public float OffsetX { get; }
        public float OffsetY { get; }
        public float OffsetZ { get; }
        public bool IsActive => IconType != -1 && ObjectId >= 0;
    }
}
