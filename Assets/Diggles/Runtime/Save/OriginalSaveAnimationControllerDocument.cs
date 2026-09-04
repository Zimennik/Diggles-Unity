#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public readonly struct OriginalSaveRaw64 : IEquatable<OriginalSaveRaw64>
    {
        public OriginalSaveRaw64(long bits)
        {
            Bits = bits;
        }

        public long Bits { get; }
        public double AsDouble => BitConverter.Int64BitsToDouble(Bits);

        public bool Equals(OriginalSaveRaw64 other) => Bits == other.Bits;
        public override bool Equals(object? obj) => obj is OriginalSaveRaw64 other && Equals(other);
        public override int GetHashCode() => Bits.GetHashCode();
        public static bool operator ==(OriginalSaveRaw64 left, OriginalSaveRaw64 right) =>
            left.Equals(right);
        public static bool operator !=(OriginalSaveRaw64 left, OriginalSaveRaw64 right) =>
            !left.Equals(right);
    }

    public sealed class OriginalSaveAnimationControllerRecord
    {
        private readonly ReadOnlyCollection<int> _values;

        internal OriginalSaveAnimationControllerRecord(
            int controllerIndex,
            byte rawStateByte,
            OriginalSaveRaw64 nativeTimeState,
            int declaredValueCount,
            int currentOrCompatibilityValue,
            int[] values,
            int repeatedControllerIndex,
            int rawControlA,
            int rawControlB,
            int modelDatabaseSlot,
            int animationState)
        {
            ControllerIndex = controllerIndex;
            RawStateByte = rawStateByte;
            NativeTimeState = nativeTimeState;
            DeclaredValueCount = declaredValueCount;
            CurrentOrCompatibilityValue = currentOrCompatibilityValue;
            _values = Array.AsReadOnly(values ?? throw new ArgumentNullException(nameof(values)));
            RepeatedControllerIndex = repeatedControllerIndex;
            RawControlA = rawControlA;
            RawControlB = rawControlB;
            ModelDatabaseSlot = modelDatabaseSlot;
            AnimationState = animationState;
        }

        public int ControllerIndex { get; }
        public byte RawStateByte { get; }
        public OriginalSaveRaw64 NativeTimeState { get; }
        public int DeclaredValueCount { get; }
        public int CurrentOrCompatibilityValue { get; }
        public IReadOnlyList<int> Values => _values;
        public int RepeatedControllerIndex { get; }
        public int RawControlA { get; }
        public int RawControlB { get; }
        public int ModelDatabaseSlot { get; }
        public int AnimationState { get; }
    }

    public sealed class OriginalSaveAnimationControllerDocument
    {
        private const int MaximumControllerCount = 8192;

        private readonly ReadOnlyCollection<OriginalSaveAnimationControllerRecord> _records;

        internal OriginalSaveAnimationControllerDocument(
            byte[] sourcePayload,
            OriginalSaveGraphicsMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.AnimationControllerSectionOffset);
            int end = checked((int)metadata.Marker9Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            var cursor = new Cursor(sourcePayload, start, end);
            var records = new OriginalSaveAnimationControllerRecord[
                metadata.AnimationControllerCount];
            int previousIndex = -1;
            int totalValueCount = 0;
            for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
            {
                int controllerIndex = cursor.ReadInt32();
                if (controllerIndex <= previousIndex || controllerIndex >= MaximumControllerCount)
                {
                    throw new InvalidDataException(
                        $"Invalid ordered native animation-controller index {controllerIndex}.");
                }

                byte rawStateByte = cursor.ReadByte();
                var nativeTimeState = new OriginalSaveRaw64(cursor.ReadInt64());
                int declaredValueCount = cursor.ReadInt32();
                int currentOrCompatibilityValue = cursor.ReadInt32();
                int valueCount = cursor.ReadInt32();
                if (valueCount < 0)
                {
                    throw new InvalidDataException(
                        $"Native animation controller {controllerIndex} has a negative value count.");
                }
                var values = new int[valueCount];
                for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
                    values[valueIndex] = cursor.ReadInt32();
                totalValueCount = checked(totalValueCount + valueCount);

                records[recordIndex] = new OriginalSaveAnimationControllerRecord(
                    controllerIndex,
                    rawStateByte,
                    nativeTimeState,
                    declaredValueCount,
                    currentOrCompatibilityValue,
                    values,
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32());
                previousIndex = controllerIndex;
            }

            int terminator = cursor.ReadInt32();
            if (terminator != -1)
                throw new InvalidDataException("Native animation-controller table has no -1 terminator.");
            cursor.ExpectEnd();
            if (totalValueCount != metadata.AnimationVectorValueCount)
            {
                throw new InvalidDataException(
                    "Typed animation-controller value count disagrees with structural metadata.");
            }
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveAnimationControllerRecord> Records => _records;

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

            internal long ReadInt64()
            {
                EnsureAvailable(sizeof(long));
                uint low = unchecked((uint)OriginalSaveWorldDocument.ReadInt32(_source, Position));
                uint high = unchecked((uint)OriginalSaveWorldDocument.ReadInt32(
                    _source,
                    Position + sizeof(int)));
                Position += sizeof(long);
                return unchecked((long)((ulong)high << 32 | low));
            }

            internal void ExpectEnd()
            {
                if (Position != _end)
                {
                    throw new InvalidDataException(
                        $"Typed animation-controller table ended at {Position}, " +
                        $"expected marker 9 at {_end}.");
                }
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native animation-controller table.");
            }
        }
    }

    public sealed class OriginalSaveRuntimeAnimationControllerSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeAnimationController> _records;

        internal OriginalSaveRuntimeAnimationControllerSnapshot(
            OriginalSaveAnimationControllerDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var records = new OriginalSaveRuntimeAnimationController[source.Records.Count];
            for (int index = 0; index < records.Length; index++)
                records[index] = new OriginalSaveRuntimeAnimationController(source.Records[index]);
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveRuntimeAnimationController> Records => _records;
    }

    public sealed class OriginalSaveRuntimeAnimationController
    {
        private readonly ReadOnlyCollection<int> _values;

        internal OriginalSaveRuntimeAnimationController(OriginalSaveAnimationControllerRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            ControllerIndex = source.ControllerIndex;
            RawStateByte = source.RawStateByte;
            NativeTimeState = source.NativeTimeState;
            DeclaredValueCount = source.DeclaredValueCount;
            CurrentOrCompatibilityValue = source.CurrentOrCompatibilityValue;
            var values = new int[source.Values.Count];
            for (int index = 0; index < values.Length; index++)
                values[index] = source.Values[index];
            _values = Array.AsReadOnly(values);
            RepeatedControllerIndex = source.RepeatedControllerIndex;
            RawControlA = source.RawControlA;
            RawControlB = source.RawControlB;
            ModelDatabaseSlot = source.ModelDatabaseSlot;
            AnimationState = source.AnimationState;
        }

        public int ControllerIndex { get; }
        public byte RawStateByte { get; }
        public OriginalSaveRaw64 NativeTimeState { get; }
        public int DeclaredValueCount { get; }
        public int CurrentOrCompatibilityValue { get; }
        public IReadOnlyList<int> Values => _values;
        public int RepeatedControllerIndex { get; }
        public int RawControlA { get; }
        public int RawControlB { get; }
        public int ModelDatabaseSlot { get; }
        public int AnimationState { get; }
    }
}
