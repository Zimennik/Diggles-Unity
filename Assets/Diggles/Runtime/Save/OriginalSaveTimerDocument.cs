#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using Diggles.Runtime.Tcl;

    public sealed class OriginalSaveTimerEventPayloadDocument
    {
        private static readonly Encoding Windows1252 = Encoding.GetEncoding(1252);
        private static readonly Encoding RuntimeWindows1252 = Encoding.GetEncoding(
            1252,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        private readonly byte[] _fixedEventName;

        internal OriginalSaveTimerEventPayloadDocument(
            byte[] fixedEventName,
            int ownerObjectId,
            int subject1ObjectId,
            int subject2ObjectId,
            int subject3ObjectId,
            OriginalSaveRaw32[] positions,
            OriginalSaveNativeString text1,
            OriginalSaveNativeString text2,
            OriginalSaveRaw32 number1,
            OriginalSaveRaw32 number2,
            OriginalSaveRaw32 number3,
            OriginalSaveRaw64 createdAt)
        {
            if (fixedEventName == null) throw new ArgumentNullException(nameof(fixedEventName));
            if (fixedEventName.Length != 32)
                throw new ArgumentException("A native event name field must contain 32 bytes.", nameof(fixedEventName));
            int terminator = Array.IndexOf(fixedEventName, (byte)0);
            if (terminator < 0)
                throw new InvalidDataException("A native timer event name is not NUL-terminated within 32 bytes.");

            _fixedEventName = (byte[])fixedEventName.Clone();
            EventName = Windows1252.GetString(_fixedEventName, 0, terminator);
            OwnerObjectId = ownerObjectId;
            Subject1ObjectId = subject1ObjectId;
            Subject2ObjectId = subject2ObjectId;
            Subject3ObjectId = subject3ObjectId;
            if (positions == null || positions.Length != 9)
                throw new ArgumentException("A native timer event must contain three position vectors.", nameof(positions));
            Position1X = positions[0];
            Position1Y = positions[1];
            Position1Z = positions[2];
            Position2X = positions[3];
            Position2Y = positions[4];
            Position2Z = positions[5];
            Position3X = positions[6];
            Position3Y = positions[7];
            Position3Z = positions[8];
            Text1 = text1 ?? throw new ArgumentNullException(nameof(text1));
            Text2 = text2 ?? throw new ArgumentNullException(nameof(text2));
            Number1 = number1;
            Number2 = number2;
            Number3 = number3;
            CreatedAt = createdAt;
        }

        public string EventName { get; }
        public int OwnerObjectId { get; }
        public int Subject1ObjectId { get; }
        public int Subject2ObjectId { get; }
        public int Subject3ObjectId { get; }
        public OriginalSaveRaw32 Position1X { get; }
        public OriginalSaveRaw32 Position1Y { get; }
        public OriginalSaveRaw32 Position1Z { get; }
        public OriginalSaveRaw32 Position2X { get; }
        public OriginalSaveRaw32 Position2Y { get; }
        public OriginalSaveRaw32 Position2Z { get; }
        public OriginalSaveRaw32 Position3X { get; }
        public OriginalSaveRaw32 Position3Y { get; }
        public OriginalSaveRaw32 Position3Z { get; }
        public OriginalSaveNativeString Text1 { get; }
        public OriginalSaveNativeString Text2 { get; }
        public OriginalSaveRaw32 Number1 { get; }
        public OriginalSaveRaw32 Number2 { get; }
        public OriginalSaveRaw32 Number3 { get; }
        public OriginalSaveRaw64 CreatedAt { get; }

        public byte[] CopyFixedEventName() => (byte[])_fixedEventName.Clone();

        internal static OriginalSaveTimerEventPayloadDocument CreateRuntimePayload(
            string eventName,
            IReadOnlyDictionary<string, string>? payload,
            int ownerObjectId,
            double creationStamp)
        {
            var positions = new OriginalSaveRaw32[9];
            for (int vector = 0; vector < 3; vector++)
            {
                int[] bits = RuntimePayloadVectorBits(payload, "-pos" + (vector + 1));
                for (int component = 0; component < 3; component++)
                    positions[vector * 3 + component] = new OriginalSaveRaw32(bits[component]);
            }

            return new OriginalSaveTimerEventPayloadDocument(
                RuntimeFixedEventName(eventName),
                ownerObjectId,
                RuntimePayloadInt(payload, "-subject1", -1),
                RuntimePayloadInt(payload, "-subject2", -1),
                RuntimePayloadInt(payload, "-subject3", -1),
                positions,
                RuntimeNativeString(RuntimePayloadString(payload, "-text1")),
                RuntimeNativeString(RuntimePayloadString(payload, "-text2")),
                new OriginalSaveRaw32(RuntimePayloadFloatBits(payload, "-num1")),
                new OriginalSaveRaw32(RuntimePayloadFloatBits(payload, "-num2")),
                new OriginalSaveRaw32(RuntimePayloadFloatBits(payload, "-num3")),
                new OriginalSaveRaw64(BitConverter.DoubleToInt64Bits(creationStamp)));
        }

        private static byte[] RuntimeFixedEventName(string eventName)
        {
            // The native field is a fixed 32-byte strncpy buffer: longer names truncate, an
            // embedded NUL terminates, an empty name leaves the buffer zeroed. Never throw here
            // — this runs inside the sim's enqueue path.
            var result = new byte[32];
            if (string.IsNullOrEmpty(eventName)) return result;
            int nul = eventName.IndexOf('\0');
            if (nul >= 0) eventName = eventName.Substring(0, nul);
            byte[] encoded = RuntimeWindows1252.GetBytes(eventName);
            Buffer.BlockCopy(encoded, 0, result, 0, Math.Min(encoded.Length, 31));
            return result;
        }

        private static OriginalSaveNativeString RuntimeNativeString(string value)
        {
            byte[] encoded = RuntimeWindows1252.GetBytes(value);
            var terminated = new byte[encoded.Length + 1];
            Buffer.BlockCopy(encoded, 0, terminated, 0, encoded.Length);
            return new OriginalSaveNativeString(terminated);
        }

        // Live payload values are arbitrary script strings; the native record builders convert
        // them with atoi/atof semantics (non-numeric → 0, missing vector components → 0). A
        // throwing converter here would abort the sim step that raised the event.
        private static int RuntimePayloadInt(
            IReadOnlyDictionary<string, string>? payload,
            string key,
            int defaultValue)
        {
            if (payload == null || !payload.TryGetValue(key, out string? value)) return defaultValue;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : 0;
        }

        private static string RuntimePayloadString(
            IReadOnlyDictionary<string, string>? payload,
            string key) =>
            payload != null && payload.TryGetValue(key, out string? value) ? value : "";

        private static int RuntimePayloadFloatBits(
            IReadOnlyDictionary<string, string>? payload,
            string key)
        {
            if (payload == null || !payload.TryGetValue(key, out string? value)) return 0;
            return LenientFloatBits(value);
        }

        private static int[] RuntimePayloadVectorBits(
            IReadOnlyDictionary<string, string>? payload,
            string key)
        {
            var result = new int[3];
            if (payload == null || !payload.TryGetValue(key, out string? value)) return result;
            IReadOnlyList<string> values;
            try
            {
                values = TclList.Parse(value);
            }
            catch (Exception)
            {
                values = value.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            }
            int count = Math.Min(values.Count, result.Length);
            for (int index = 0; index < count; index++)
                result[index] = LenientFloatBits(values[index]);
            return result;
        }

        private static int LenientFloatBits(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ||
                float.IsNaN(result) || float.IsInfinity(result))
            {
                return 0;
            }
            return BitConverter.SingleToInt32Bits(result);
        }
    }

    public sealed class OriginalSaveTimerRecord
    {
        internal OriginalSaveTimerRecord(
            int userId,
            OriginalSaveRaw64 nextFire,
            OriginalSaveRaw64 interval,
            int repeatsLeft,
            int ownerObjectId,
            OriginalSaveTimerEventPayloadDocument eventPayload)
        {
            UserId = userId;
            NextFire = nextFire;
            Interval = interval;
            RepeatsLeft = repeatsLeft;
            OwnerObjectId = ownerObjectId;
            EventPayload = eventPayload ?? throw new ArgumentNullException(nameof(eventPayload));
        }

        public int UserId { get; }
        public OriginalSaveRaw64 NextFire { get; }
        public OriginalSaveRaw64 Interval { get; }
        public int RepeatsLeft { get; }
        public int OwnerObjectId { get; }
        public OriginalSaveTimerEventPayloadDocument EventPayload { get; }
    }

    public sealed class OriginalSaveTimerDocument
    {
        private const int MaximumStringLength = 1024 * 1024;
        private readonly ReadOnlyCollection<OriginalSaveTimerRecord> _records;

        internal OriginalSaveTimerDocument(byte[] payload, OriginalSaveObjectManifest manifest)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            int start = checked((int)manifest.SupportSectionOffset);
            int end = checked((int)manifest.BodySectionOffset);
            var cursor = new Cursor(payload, start, end);
            int count = cursor.ReadCount();
            if (count != manifest.SupportRecordCount)
            {
                throw new InvalidDataException(
                    $"Typed timer count {count} does not match structural count {manifest.SupportRecordCount}.");
            }

            var records = new OriginalSaveTimerRecord[count];
            for (int index = 0; index < records.Length; index++)
            {
                int userId = cursor.ReadInt32();
                var nextFire = new OriginalSaveRaw64(cursor.ReadInt64());
                var interval = new OriginalSaveRaw64(cursor.ReadInt64());
                int repeatsLeft = cursor.ReadInt32();
                int ownerObjectId = cursor.ReadInt32();
                byte[] eventName = cursor.ReadBytes(32);
                int eventOwnerObjectId = cursor.ReadInt32();
                int subject1ObjectId = cursor.ReadInt32();
                int subject2ObjectId = cursor.ReadInt32();
                int subject3ObjectId = cursor.ReadInt32();
                OriginalSaveRaw32[] positions = cursor.ReadRaw32s(9);
                OriginalSaveNativeString text1 = cursor.ReadNativeString();
                OriginalSaveNativeString text2 = cursor.ReadNativeString();
                var number1 = new OriginalSaveRaw32(cursor.ReadInt32());
                var number2 = new OriginalSaveRaw32(cursor.ReadInt32());
                var number3 = new OriginalSaveRaw32(cursor.ReadInt32());
                var createdAt = new OriginalSaveRaw64(cursor.ReadInt64());
                records[index] = new OriginalSaveTimerRecord(
                    userId,
                    nextFire,
                    interval,
                    repeatsLeft,
                    ownerObjectId,
                    new OriginalSaveTimerEventPayloadDocument(
                        eventName,
                        eventOwnerObjectId,
                        subject1ObjectId,
                        subject2ObjectId,
                        subject3ObjectId,
                        positions,
                        text1,
                        text2,
                        number1,
                        number2,
                        number3,
                        createdAt));
            }
            cursor.ExpectEnd();
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveTimerRecord> Records => _records;

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

            internal int Position { get; private set; }

            internal int ReadCount()
            {
                int count = ReadInt32();
                if (count < 0 || count > 4 * 1024 * 1024)
                    throw new InvalidDataException($"Invalid native timer count {count}.");
                return count;
            }

            internal int ReadInt32()
            {
                EnsureAvailable(4);
                int value = _source[Position] |
                    (_source[Position + 1] << 8) |
                    (_source[Position + 2] << 16) |
                    (_source[Position + 3] << 24);
                Position += 4;
                return value;
            }

            internal long ReadInt64()
            {
                uint low = unchecked((uint)ReadInt32());
                uint high = unchecked((uint)ReadInt32());
                return unchecked((long)((ulong)low | ((ulong)high << 32)));
            }

            internal byte[] ReadBytes(int count)
            {
                EnsureAvailable(count);
                var result = new byte[count];
                Buffer.BlockCopy(_source, Position, result, 0, count);
                Position += count;
                return result;
            }

            internal OriginalSaveRaw32[] ReadRaw32s(int count)
            {
                var result = new OriginalSaveRaw32[count];
                for (int index = 0; index < result.Length; index++)
                    result[index] = new OriginalSaveRaw32(ReadInt32());
                return result;
            }

            internal OriginalSaveNativeString ReadNativeString()
            {
                int length = ReadInt32();
                if (length <= 0 || length > MaximumStringLength)
                    throw new InvalidDataException($"Invalid native timer string length {length}.");
                byte[] bytes = ReadBytes(length);
                if (bytes[^1] != 0)
                    throw new InvalidDataException("A native timer string is not NUL-terminated.");
                return new OriginalSaveNativeString(bytes);
            }

            internal void ExpectEnd()
            {
                if (Position != _end)
                {
                    throw new InvalidDataException(
                        $"Typed timer table ended at {Position}, expected object bodies at {_end}.");
                }
            }

            private void EnsureAvailable(int count)
            {
                if (count < 0 || Position > _end - count)
                    throw new EndOfStreamException("Unexpected end of native timer table.");
            }
        }
    }

    public sealed class OriginalSaveRuntimeTimerSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeTimer> _records;

        internal OriginalSaveRuntimeTimerSnapshot(OriginalSaveTimerDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var records = new OriginalSaveRuntimeTimer[source.Records.Count];
            for (int index = 0; index < records.Length; index++)
                records[index] = new OriginalSaveRuntimeTimer(source.Records[index]);
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveRuntimeTimer> Records => _records;
    }

    public sealed class OriginalSaveRuntimeTimer
    {
        private readonly byte[] _fixedEventName;

        internal OriginalSaveRuntimeTimer(OriginalSaveTimerRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            UserId = source.UserId;
            NextFire = source.NextFire;
            Interval = source.Interval;
            RepeatsLeft = source.RepeatsLeft;
            OwnerObjectId = source.OwnerObjectId;
            OriginalSaveTimerEventPayloadDocument payload = source.EventPayload;
            _fixedEventName = payload.CopyFixedEventName();
            EventName = payload.EventName;
            EventOwnerObjectId = payload.OwnerObjectId;
            Subject1ObjectId = payload.Subject1ObjectId;
            Subject2ObjectId = payload.Subject2ObjectId;
            Subject3ObjectId = payload.Subject3ObjectId;
            Position1X = payload.Position1X;
            Position1Y = payload.Position1Y;
            Position1Z = payload.Position1Z;
            Position2X = payload.Position2X;
            Position2Y = payload.Position2Y;
            Position2Z = payload.Position2Z;
            Position3X = payload.Position3X;
            Position3Y = payload.Position3Y;
            Position3Z = payload.Position3Z;
            Text1 = new OriginalSaveNativeString(payload.Text1.CopyBytesIncludingTerminator());
            Text2 = new OriginalSaveNativeString(payload.Text2.CopyBytesIncludingTerminator());
            Number1 = payload.Number1;
            Number2 = payload.Number2;
            Number3 = payload.Number3;
            CreatedAt = payload.CreatedAt;
        }

        public int UserId { get; }
        public OriginalSaveRaw64 NextFire { get; }
        public OriginalSaveRaw64 Interval { get; }
        public int RepeatsLeft { get; }
        public int OwnerObjectId { get; }
        public string EventName { get; }
        public int EventOwnerObjectId { get; }
        public int Subject1ObjectId { get; }
        public int Subject2ObjectId { get; }
        public int Subject3ObjectId { get; }
        public OriginalSaveRaw32 Position1X { get; }
        public OriginalSaveRaw32 Position1Y { get; }
        public OriginalSaveRaw32 Position1Z { get; }
        public OriginalSaveRaw32 Position2X { get; }
        public OriginalSaveRaw32 Position2Y { get; }
        public OriginalSaveRaw32 Position2Z { get; }
        public OriginalSaveRaw32 Position3X { get; }
        public OriginalSaveRaw32 Position3Y { get; }
        public OriginalSaveRaw32 Position3Z { get; }
        public OriginalSaveNativeString Text1 { get; }
        public OriginalSaveNativeString Text2 { get; }
        public OriginalSaveRaw32 Number1 { get; }
        public OriginalSaveRaw32 Number2 { get; }
        public OriginalSaveRaw32 Number3 { get; }
        public OriginalSaveRaw64 CreatedAt { get; }

        public byte[] CopyFixedEventName() => (byte[])_fixedEventName.Clone();
    }
}
