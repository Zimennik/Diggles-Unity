#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveStoryEventDocument
    {
        internal OriginalSaveStoryEventDocument(
            OriginalSaveNativeString name,
            OriginalSaveNativeString trigger,
            OriginalSaveNativeString extra,
            bool completed)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
            Extra = extra ?? throw new ArgumentNullException(nameof(extra));
            Completed = completed;
        }

        public OriginalSaveNativeString Name { get; }
        public OriginalSaveNativeString Trigger { get; }
        public OriginalSaveNativeString Extra { get; }
        public bool Completed { get; }
    }

    public sealed class OriginalSaveStoryTempDocument
    {
        private readonly ReadOnlyCollection<int> _eventIndices;

        internal OriginalSaveStoryTempDocument(
            OriginalSaveNativeString name,
            int nativeIndexA,
            int nativeIndexB,
            bool placed,
            int[] eventIndices,
            int lowerDepth,
            int upperDepth,
            OriginalSaveRaw32 minimumDistance,
            int minimumDistanceReferenceIndex,
            int nativeWidth,
            int nativeHeight,
            int digConnectValue,
            int templateGroupIndex,
            bool exclusive,
            int nativeOriginX,
            int nativeOriginY,
            int zoneIndex,
            int zoneBorderIndex,
            int placedX,
            int placedY,
            bool checkAlign,
            int stoneIndex,
            OriginalSaveRaw32 middleDistance,
            OriginalSaveRaw32 nonRelevantProbability)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            NativeIndexA = nativeIndexA;
            NativeIndexB = nativeIndexB;
            Placed = placed;
            _eventIndices = Array.AsReadOnly(
                eventIndices ?? throw new ArgumentNullException(nameof(eventIndices)));
            LowerDepth = lowerDepth;
            UpperDepth = upperDepth;
            MinimumDistance = minimumDistance;
            MinimumDistanceReferenceIndex = minimumDistanceReferenceIndex;
            NativeWidth = nativeWidth;
            NativeHeight = nativeHeight;
            DigConnectValue = digConnectValue;
            TemplateGroupIndex = templateGroupIndex;
            Exclusive = exclusive;
            NativeOriginX = nativeOriginX;
            NativeOriginY = nativeOriginY;
            ZoneIndex = zoneIndex;
            ZoneBorderIndex = zoneBorderIndex;
            PlacedX = placedX;
            PlacedY = placedY;
            CheckAlign = checkAlign;
            StoneIndex = stoneIndex;
            MiddleDistance = middleDistance;
            NonRelevantProbability = nonRelevantProbability;
        }

        public OriginalSaveNativeString Name { get; }
        public int NativeIndexA { get; }
        public int NativeIndexB { get; }
        public bool Placed { get; }
        public IReadOnlyList<int> EventIndices => _eventIndices;
        public int LowerDepth { get; }
        public int UpperDepth { get; }
        public OriginalSaveRaw32 MinimumDistance { get; }
        public int MinimumDistanceReferenceIndex { get; }
        public int NativeWidth { get; }
        public int NativeHeight { get; }
        public int DigConnectValue { get; }
        public int TemplateGroupIndex { get; }
        public bool Exclusive { get; }
        public int NativeOriginX { get; }
        public int NativeOriginY { get; }
        public int ZoneIndex { get; }
        public int ZoneBorderIndex { get; }
        public int PlacedX { get; }
        public int PlacedY { get; }
        public bool CheckAlign { get; }
        public int StoneIndex { get; }
        public OriginalSaveRaw32 MiddleDistance { get; }
        public OriginalSaveRaw32 NonRelevantProbability { get; }
    }

    public sealed class OriginalSaveStoryGroupEntryDocument
    {
        private readonly ReadOnlyCollection<int> _values;

        internal OriginalSaveStoryGroupEntryDocument(
            OriginalSaveNativeString name,
            int[] values,
            bool completed)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _values = Array.AsReadOnly(values ?? throw new ArgumentNullException(nameof(values)));
            Completed = completed;
        }

        public OriginalSaveNativeString Name { get; }
        public IReadOnlyList<int> Values => _values;
        public bool Completed { get; }
    }

    public sealed class OriginalSaveStoryGroupDocument
    {
        private readonly ReadOnlyCollection<OriginalSaveStoryGroupEntryDocument> _entries;
        private readonly ReadOnlyCollection<int> _headerValues;
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _fourValueRecords;
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _fiveValueRecords;

        internal OriginalSaveStoryGroupDocument(
            OriginalSaveNativeString name,
            OriginalSaveStoryGroupEntryDocument[] entries,
            int[] headerValues,
            IReadOnlyList<int>[] fourValueRecords,
            IReadOnlyList<int>[] fiveValueRecords)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _entries = Array.AsReadOnly(entries ?? throw new ArgumentNullException(nameof(entries)));
            _headerValues = Array.AsReadOnly(
                headerValues ?? throw new ArgumentNullException(nameof(headerValues)));
            _fourValueRecords = Array.AsReadOnly(
                fourValueRecords ?? throw new ArgumentNullException(nameof(fourValueRecords)));
            _fiveValueRecords = Array.AsReadOnly(
                fiveValueRecords ?? throw new ArgumentNullException(nameof(fiveValueRecords)));
        }

        public OriginalSaveNativeString Name { get; }
        public IReadOnlyList<OriginalSaveStoryGroupEntryDocument> Entries => _entries;
        public IReadOnlyList<int> HeaderValues => _headerValues;
        public IReadOnlyList<IReadOnlyList<int>> FourValueRecords => _fourValueRecords;
        public IReadOnlyList<IReadOnlyList<int>> FiveValueRecords => _fiveValueRecords;
    }

    public sealed class OriginalSaveStoryPairRecordDocument
    {
        internal OriginalSaveStoryPairRecordDocument(
            OriginalSaveNativeString first,
            OriginalSaveNativeString second,
            int value)
        {
            First = first ?? throw new ArgumentNullException(nameof(first));
            Second = second ?? throw new ArgumentNullException(nameof(second));
            Value = value;
        }

        public OriginalSaveNativeString First { get; }
        public OriginalSaveNativeString Second { get; }
        public int Value { get; }
    }

    public sealed class OriginalSaveStoryZoneDocument
    {
        internal OriginalSaveStoryZoneDocument(OriginalSaveNativeString name, int upperBound)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            UpperBound = upperBound;
        }

        public OriginalSaveNativeString Name { get; }
        public int UpperBound { get; }
    }

    public sealed class OriginalSaveStoryDocument
    {
        private const int MaximumStringLength = 1024 * 1024;
        private const int MaximumCollectionCount = 4 * 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveStoryEventDocument> _events;
        private readonly ReadOnlyCollection<OriginalSaveStoryTempDocument> _temps;
        private readonly ReadOnlyCollection<OriginalSaveStoryGroupDocument> _groups;
        private readonly ReadOnlyCollection<OriginalSaveStoryPairRecordDocument> _pairRecords;
        private readonly ReadOnlyCollection<int> _mapValues;
        private readonly ReadOnlyCollection<OriginalSaveStoryZoneDocument> _zones;

        internal OriginalSaveStoryDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.FirstManagerSectionOffset);
            int end = checked((int)metadata.Marker11Offset);
            var cursor = new Cursor(sourcePayload, start, end);

            int eventCount = cursor.ReadCount("story event");
            var events = new OriginalSaveStoryEventDocument[eventCount];
            for (int index = 0; index < events.Length; index++)
            {
                events[index] = new OriginalSaveStoryEventDocument(
                    cursor.ReadNativeString("story event name"),
                    cursor.ReadNativeString("story event trigger"),
                    cursor.ReadNativeString("story event extra"),
                    cursor.ReadBool("story event completion"));
            }
            _events = Array.AsReadOnly(events);

            int tempCount = cursor.ReadCount("story unique template");
            var temps = new OriginalSaveStoryTempDocument[tempCount];
            for (int index = 0; index < temps.Length; index++)
                temps[index] = ReadTemp(cursor);
            _temps = Array.AsReadOnly(temps);

            int groupCount = cursor.ReadCount("story template group");
            var groups = new OriginalSaveStoryGroupDocument[groupCount];
            for (int index = 0; index < groups.Length; index++)
                groups[index] = ReadGroup(cursor);
            _groups = Array.AsReadOnly(groups);

            SelectedTempName = cursor.ReadNativeString("story selected template name");
            NativeSelectedTempValue = cursor.ReadInt32();
            RectWidth = cursor.ReadInt32();
            RectHeight = cursor.ReadInt32();
            RectX = cursor.ReadInt32();
            RectY = cursor.ReadInt32();
            ScanX = cursor.ReadInt32();
            ScanY = cursor.ReadInt32();
            RawRectY = cursor.ReadInt32();
            RawRectX = cursor.ReadInt32();
            RawRectWidth = cursor.ReadInt32();
            RawRectHeight = cursor.ReadInt32();

            int pairCount = cursor.ReadCount("story string-pair record");
            var pairRecords = new OriginalSaveStoryPairRecordDocument[pairCount];
            for (int index = 0; index < pairRecords.Length; index++)
            {
                pairRecords[index] = new OriginalSaveStoryPairRecordDocument(
                    cursor.ReadNativeString("story pair first string"),
                    cursor.ReadNativeString("story pair second string"),
                    cursor.ReadInt32());
            }
            _pairRecords = Array.AsReadOnly(pairRecords);

            int mapValueCount = cursor.ReadCount("story map value");
            var mapValues = new int[mapValueCount];
            for (int index = 0; index < mapValues.Length; index++)
                mapValues[index] = cursor.ReadInt32();
            _mapValues = Array.AsReadOnly(mapValues);
            MapWidth = cursor.ReadInt32();
            MapHeight = cursor.ReadInt32();

            int zoneCount = cursor.ReadCount("story zone");
            var zones = new OriginalSaveStoryZoneDocument[zoneCount];
            for (int index = 0; index < zones.Length; index++)
            {
                zones[index] = new OriginalSaveStoryZoneDocument(
                    cursor.ReadNativeString("story zone name"),
                    cursor.ReadInt32());
            }
            _zones = Array.AsReadOnly(zones);

            CallbackScript = cursor.ReadNativeString("story callback script");
            CallbackObjectId = cursor.ReadInt32();
            ZoneFloor = cursor.ReadInt32();
            LatchDirection = cursor.ReadInt32();
            LatchX = cursor.ReadInt32();
            LatchY = cursor.ReadInt32();
            LatchConsumed = cursor.ReadBool("story latch consumed");
            DigWeight = cursor.ReadInt32();
            DigMessageEnabled = cursor.ReadBool("story dig message");
            DigThreshold = cursor.ReadInt32();
            cursor.ExpectEnd("story manager state");
        }

        public IReadOnlyList<OriginalSaveStoryEventDocument> Events => _events;
        public IReadOnlyList<OriginalSaveStoryTempDocument> Temps => _temps;
        public IReadOnlyList<OriginalSaveStoryGroupDocument> Groups => _groups;
        public OriginalSaveNativeString SelectedTempName { get; }
        public int NativeSelectedTempValue { get; }
        public int RectWidth { get; }
        public int RectHeight { get; }
        public int RectX { get; }
        public int RectY { get; }
        public int ScanX { get; }
        public int ScanY { get; }
        public int RawRectX { get; }
        public int RawRectY { get; }
        public int RawRectWidth { get; }
        public int RawRectHeight { get; }
        public IReadOnlyList<OriginalSaveStoryPairRecordDocument> PairRecords => _pairRecords;
        public IReadOnlyList<int> MapValues => _mapValues;
        public int MapWidth { get; }
        public int MapHeight { get; }
        public IReadOnlyList<OriginalSaveStoryZoneDocument> Zones => _zones;
        public OriginalSaveNativeString CallbackScript { get; }
        public int CallbackObjectId { get; }
        public int ZoneFloor { get; }
        public int LatchDirection { get; }
        public int LatchX { get; }
        public int LatchY { get; }
        public bool LatchConsumed { get; }
        public int DigWeight { get; }
        public bool DigMessageEnabled { get; }
        public int DigThreshold { get; }

        private static OriginalSaveStoryTempDocument ReadTemp(Cursor cursor)
        {
            OriginalSaveNativeString name = cursor.ReadNativeString("story template name");
            int nativeIndexA = cursor.ReadInt32();
            int nativeIndexB = cursor.ReadInt32();
            bool placed = cursor.ReadBool("story template placed");
            int eventCount = cursor.ReadCount("story template event");
            var eventIndices = new int[eventCount];
            for (int index = 0; index < eventIndices.Length; index++)
                eventIndices[index] = cursor.ReadInt32();
            int lowerDepth = cursor.ReadInt32();
            int upperDepth = cursor.ReadInt32();
            var minimumDistance = new OriginalSaveRaw32(cursor.ReadInt32());
            int minimumDistanceReferenceIndex = cursor.ReadInt32();
            int nativeWidth = cursor.ReadInt32();
            int nativeHeight = cursor.ReadInt32();
            int digConnectValue = cursor.ReadInt32();
            int templateGroupIndex = cursor.ReadInt32();
            bool exclusive = cursor.ReadBool("story template exclusive");
            int nativeOriginX = cursor.ReadInt32();
            int nativeOriginY = cursor.ReadInt32();
            int zoneIndex = cursor.ReadInt32();
            int zoneBorderIndex = cursor.ReadInt32();
            int placedX = cursor.ReadInt32();
            int placedY = cursor.ReadInt32();
            bool checkAlign = cursor.ReadBool("story template alignment check");
            int stoneIndex = cursor.ReadInt32();
            var middleDistance = new OriginalSaveRaw32(cursor.ReadInt32());
            var nonRelevantProbability = new OriginalSaveRaw32(cursor.ReadInt32());
            return new OriginalSaveStoryTempDocument(
                name,
                nativeIndexA,
                nativeIndexB,
                placed,
                eventIndices,
                lowerDepth,
                upperDepth,
                minimumDistance,
                minimumDistanceReferenceIndex,
                nativeWidth,
                nativeHeight,
                digConnectValue,
                templateGroupIndex,
                exclusive,
                nativeOriginX,
                nativeOriginY,
                zoneIndex,
                zoneBorderIndex,
                placedX,
                placedY,
                checkAlign,
                stoneIndex,
                middleDistance,
                nonRelevantProbability);
        }

        private static OriginalSaveStoryGroupDocument ReadGroup(Cursor cursor)
        {
            OriginalSaveNativeString name = cursor.ReadNativeString("story group name");
            int entryCount = cursor.ReadCount("story group entry");
            var entries = new OriginalSaveStoryGroupEntryDocument[entryCount];
            for (int index = 0; index < entries.Length; index++)
            {
                OriginalSaveNativeString entryName =
                    cursor.ReadNativeString("story group entry name");
                var values = new int[4];
                for (int value = 0; value < values.Length; value++)
                    values[value] = cursor.ReadInt32();
                entries[index] = new OriginalSaveStoryGroupEntryDocument(
                    entryName,
                    values,
                    cursor.ReadBool("story group entry completion"));
            }

            var headerValues = new int[3];
            for (int index = 0; index < headerValues.Length; index++)
                headerValues[index] = cursor.ReadInt32();
            IReadOnlyList<int>[] fourValueRecords = cursor.ReadFixedRecords(
                "story group four-value record", 4);
            IReadOnlyList<int>[] fiveValueRecords = cursor.ReadFixedRecords(
                "story group five-value record", 5);
            return new OriginalSaveStoryGroupDocument(
                name,
                entries,
                headerValues,
                fourValueRecords,
                fiveValueRecords);
        }

        private sealed class Cursor
        {
            private readonly byte[] _source;
            private readonly int _end;

            internal Cursor(byte[] source, int start, int end)
            {
                _source = source;
                if (start < 0 || end < start || end > source.Length)
                    throw new ArgumentOutOfRangeException(nameof(start));
                Position = start;
                _end = end;
            }

            internal int Position { get; private set; }

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
                if (count < 0 || count > MaximumCollectionCount)
                    throw new InvalidDataException($"Invalid {label} count {count}.");
                return count;
            }

            internal bool ReadBool(string label)
            {
                EnsureAvailable(1);
                byte value = _source[Position++];
                if (value > 1)
                    throw new InvalidDataException($"Invalid {label} byte {value}.");
                return value != 0;
            }

            internal OriginalSaveNativeString ReadNativeString(string label)
            {
                int length = ReadInt32();
                if (length <= 0 || length > MaximumStringLength)
                    throw new InvalidDataException($"Invalid {label} length {length}.");
                EnsureAvailable(length);
                var bytes = new byte[length];
                Buffer.BlockCopy(_source, Position, bytes, 0, length);
                Position += length;
                if (bytes[^1] != 0)
                    throw new InvalidDataException($"Native {label} is not NUL-terminated.");
                return new OriginalSaveNativeString(bytes);
            }

            internal IReadOnlyList<int>[] ReadFixedRecords(string label, int width)
            {
                int count = ReadCount(label);
                var records = new IReadOnlyList<int>[count];
                for (int record = 0; record < records.Length; record++)
                {
                    var values = new int[width];
                    for (int value = 0; value < values.Length; value++)
                        values[value] = ReadInt32();
                    records[record] = Array.AsReadOnly(values);
                }
                return records;
            }

            internal void ExpectEnd(string label)
            {
                if (Position != _end)
                    throw new InvalidDataException(
                        $"Typed {label} parsing ended at {Position}, expected {_end}.");
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native story-manager state.");
            }
        }
    }
}
