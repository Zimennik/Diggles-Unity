#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveTriggerSlot
    {
        internal OriginalSaveTriggerSlot(int serializedPointerValue, OriginalSaveTriggerRecord? record)
        {
            SerializedPointerValue = serializedPointerValue;
            Record = record;
        }

        // The native pointer-array serializer writes the raw pointer value as its presence token.
        // It is process-specific and opaque; zero means an empty slot.
        public int SerializedPointerValue { get; }
        public OriginalSaveTriggerRecord? Record { get; }
    }

    public sealed class OriginalSaveTriggerRecord
    {
        internal OriginalSaveTriggerRecord(
            int index,
            int kind,
            int execution,
            int ownerObjectId,
            OriginalSaveNativeString fireScript,
            double lastCheck,
            double checkTimer,
            double checkIdle,
            double checkNear,
            bool active,
            OriginalSaveRaw32 targetRange,
            double fireTime,
            double interval,
            int targetObjectId,
            OriginalSaveNativeString targetClass,
            int targetTypeMask,
            OriginalSaveNativeString targetOwnerSpec,
            int targetCount,
            OriginalSaveNativeString callbackScript,
            OriginalSaveNativeString actuators)
        {
            Index = index;
            Kind = kind;
            Execution = execution;
            OwnerObjectId = ownerObjectId;
            FireScript = fireScript ?? throw new ArgumentNullException(nameof(fireScript));
            LastCheck = lastCheck;
            CheckTimer = checkTimer;
            CheckIdle = checkIdle;
            CheckNear = checkNear;
            Active = active;
            TargetRange = targetRange;
            FireTime = fireTime;
            Interval = interval;
            TargetObjectId = targetObjectId;
            TargetClass = targetClass ?? throw new ArgumentNullException(nameof(targetClass));
            TargetTypeMask = targetTypeMask;
            TargetOwnerSpec = targetOwnerSpec ?? throw new ArgumentNullException(nameof(targetOwnerSpec));
            TargetCount = targetCount;
            CallbackScript = callbackScript ?? throw new ArgumentNullException(nameof(callbackScript));
            Actuators = actuators ?? throw new ArgumentNullException(nameof(actuators));
        }

        public int Index { get; }
        public int Kind { get; }
        public int Execution { get; }
        public int OwnerObjectId { get; }
        public OriginalSaveNativeString FireScript { get; }
        public double LastCheck { get; }
        public double CheckTimer { get; }
        public double CheckIdle { get; }
        public double CheckNear { get; }
        public bool Active { get; }
        public OriginalSaveRaw32 TargetRange { get; }
        public double FireTime { get; }
        public double Interval { get; }
        public int TargetObjectId { get; }
        public OriginalSaveNativeString TargetClass { get; }
        public int TargetTypeMask { get; }
        public OriginalSaveNativeString TargetOwnerSpec { get; }
        public int TargetCount { get; }
        public OriginalSaveNativeString CallbackScript { get; }
        public OriginalSaveNativeString Actuators { get; }
    }

    public sealed class OriginalSaveTriggerTableDocument
    {
        private const int MaximumStringLength = 1024 * 1024;
        private const int MaximumSlotCount = 4 * 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveTriggerSlot> _slots;

        internal OriginalSaveTriggerTableDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.Marker14Offset + sizeof(int));
            int end = checked((int)metadata.Marker15Offset);
            var cursor = new Cursor(sourcePayload, start, end);
            int slotCount = cursor.ReadCount("trigger slot");
            if (slotCount != metadata.SchedulerSlotCount)
                throw new InvalidDataException(
                    "Typed trigger parsing disagrees with the structurally validated slot count.");

            var slots = new OriginalSaveTriggerSlot[slotCount];
            int recordCount = 0;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                int pointerValue = cursor.ReadInt32();
                OriginalSaveTriggerRecord? record = pointerValue == 0
                    ? null
                    : ReadRecord(cursor);
                if (record != null) recordCount++;
                slots[slot] = new OriginalSaveTriggerSlot(pointerValue, record);
            }

            if (cursor.Position != end)
                throw new InvalidDataException(
                    $"Typed trigger parsing ended at {cursor.Position}, expected marker 15 at {end}.");
            if (recordCount != metadata.SchedulerRecordCount)
                throw new InvalidDataException(
                    "Typed trigger parsing disagrees with the structurally validated record count.");
            _slots = Array.AsReadOnly(slots);
        }

        public IReadOnlyList<OriginalSaveTriggerSlot> Slots => _slots;

        private static OriginalSaveTriggerRecord ReadRecord(Cursor cursor)
        {
            int index = cursor.ReadInt32();
            int kind = cursor.ReadInt32();
            if ((uint)kind > 5)
                throw new InvalidDataException($"Native trigger record {index} has invalid kind {kind}.");
            int execution = cursor.ReadInt32();
            if (execution < 1 || execution > 6)
                throw new InvalidDataException(
                    $"Native trigger record {index} has invalid execution mode {execution}.");

            int ownerObjectId = cursor.ReadInt32();
            OriginalSaveNativeString fireScript = cursor.ReadNativeString();
            double lastCheck = cursor.ReadDouble();
            double checkTimer = cursor.ReadDouble();
            double checkIdle = cursor.ReadDouble();
            double checkNear = cursor.ReadDouble();
            bool active = cursor.ReadBooleanByte("trigger active flag");
            OriginalSaveRaw32 targetRange = cursor.ReadRaw32();
            double fireTime = cursor.ReadDouble();
            double interval = cursor.ReadDouble();
            int targetObjectId = cursor.ReadInt32();
            OriginalSaveNativeString targetClass = cursor.ReadNativeString();
            int targetTypeMask = cursor.ReadInt32();
            OriginalSaveNativeString targetOwnerSpec = cursor.ReadNativeString();
            int targetCount = cursor.ReadInt32();
            OriginalSaveNativeString callbackScript = cursor.ReadNativeString();
            OriginalSaveNativeString actuators = cursor.ReadNativeString();
            return new OriginalSaveTriggerRecord(
                index,
                kind,
                execution,
                ownerObjectId,
                fireScript,
                lastCheck,
                checkTimer,
                checkIdle,
                checkNear,
                active,
                targetRange,
                fireTime,
                interval,
                targetObjectId,
                targetClass,
                targetTypeMask,
                targetOwnerSpec,
                targetCount,
                callbackScript,
                actuators);
        }

        private sealed class Cursor
        {
            private readonly byte[] _source;
            private readonly int _end;

            internal Cursor(byte[] source, int start, int end)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                if (start < 0 || end < start || end > source.Length)
                    throw new ArgumentOutOfRangeException(nameof(start));
                Position = start;
                _end = end;
            }

            internal int Position { get; private set; }

            internal byte ReadByte()
            {
                EnsureAvailable(sizeof(byte));
                return _source[Position++];
            }

            internal bool ReadBooleanByte(string label)
            {
                byte value = ReadByte();
                if (value > 1) throw new InvalidDataException($"Invalid {label} value {value}.");
                return value != 0;
            }

            internal int ReadInt32()
            {
                EnsureAvailable(sizeof(int));
                int value = OriginalSaveWorldDocument.ReadInt32(_source, Position);
                Position += sizeof(int);
                return value;
            }

            internal int ReadCount(string label)
            {
                int count = ReadInt32();
                if (count < 0 || count > MaximumSlotCount)
                    throw new InvalidDataException($"Invalid native {label} count {count}.");
                return count;
            }

            internal OriginalSaveRaw32 ReadRaw32() => new(ReadInt32());

            internal double ReadDouble()
            {
                EnsureAvailable(sizeof(double));
                double value = BitConverter.ToDouble(_source, Position);
                Position += sizeof(double);
                return value;
            }

            internal OriginalSaveNativeString ReadNativeString()
            {
                int length = ReadInt32();
                if (length <= 0 || length > MaximumStringLength)
                    throw new InvalidDataException($"Invalid native trigger string length {length}.");
                EnsureAvailable(length);
                var bytes = new byte[length];
                Buffer.BlockCopy(_source, Position, bytes, 0, length);
                Position += length;
                if (bytes[^1] != 0)
                    throw new InvalidDataException("Native trigger string is not NUL-terminated.");
                return new OriginalSaveNativeString(bytes);
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native trigger table.");
            }
        }
    }
}
