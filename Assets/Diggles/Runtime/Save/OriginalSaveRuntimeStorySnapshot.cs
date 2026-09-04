#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using Diggles.Runtime.Tcl;

    public sealed class OriginalSaveRuntimeStorySnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeStoryEventSnapshot> _events;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeStoryTempSnapshot> _temps;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeStoryGroupSnapshot> _groups;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeStoryPairSnapshot> _pairRecords;
        private readonly ReadOnlyCollection<int> _mapValues;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeStoryZoneSnapshot> _zones;

        internal OriginalSaveRuntimeStorySnapshot(OriginalSaveStoryDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var events = new OriginalSaveRuntimeStoryEventSnapshot[source.Events.Count];
            for (int index = 0; index < events.Length; index++)
                events[index] = new OriginalSaveRuntimeStoryEventSnapshot(source.Events[index]);
            _events = Array.AsReadOnly(events);

            var temps = new OriginalSaveRuntimeStoryTempSnapshot[source.Temps.Count];
            for (int index = 0; index < temps.Length; index++)
                temps[index] = new OriginalSaveRuntimeStoryTempSnapshot(source.Temps[index]);
            _temps = Array.AsReadOnly(temps);

            var groups = new OriginalSaveRuntimeStoryGroupSnapshot[source.Groups.Count];
            for (int index = 0; index < groups.Length; index++)
                groups[index] = new OriginalSaveRuntimeStoryGroupSnapshot(source.Groups[index]);
            _groups = Array.AsReadOnly(groups);

            var pairRecords = new OriginalSaveRuntimeStoryPairSnapshot[source.PairRecords.Count];
            for (int index = 0; index < pairRecords.Length; index++)
                pairRecords[index] = new OriginalSaveRuntimeStoryPairSnapshot(source.PairRecords[index]);
            _pairRecords = Array.AsReadOnly(pairRecords);

            var mapValues = new int[source.MapValues.Count];
            for (int index = 0; index < mapValues.Length; index++)
                mapValues[index] = source.MapValues[index];
            _mapValues = Array.AsReadOnly(mapValues);

            var zones = new OriginalSaveRuntimeStoryZoneSnapshot[source.Zones.Count];
            for (int index = 0; index < zones.Length; index++)
                zones[index] = new OriginalSaveRuntimeStoryZoneSnapshot(source.Zones[index]);
            _zones = Array.AsReadOnly(zones);

            SelectedTempName = source.SelectedTempName.Text;
            NativeSelectedTempValue = source.NativeSelectedTempValue;
            RectWidth = source.RectWidth;
            RectHeight = source.RectHeight;
            RectX = source.RectX;
            RectY = source.RectY;
            ScanX = source.ScanX;
            ScanY = source.ScanY;
            RawRectX = source.RawRectX;
            RawRectY = source.RawRectY;
            RawRectWidth = source.RawRectWidth;
            RawRectHeight = source.RawRectHeight;
            MapWidth = source.MapWidth;
            MapHeight = source.MapHeight;
            CallbackScript = source.CallbackScript.Text;
            CallbackObjectId = source.CallbackObjectId;
            ZoneFloor = source.ZoneFloor;
            LatchDirection = source.LatchDirection;
            LatchX = source.LatchX;
            LatchY = source.LatchY;
            LatchConsumed = source.LatchConsumed;
            DigWeight = source.DigWeight;
            DigMessageEnabled = source.DigMessageEnabled;
            DigThreshold = source.DigThreshold;
        }

        public IReadOnlyList<OriginalSaveRuntimeStoryEventSnapshot> Events => _events;
        public IReadOnlyList<OriginalSaveRuntimeStoryTempSnapshot> Temps => _temps;
        public IReadOnlyList<OriginalSaveRuntimeStoryGroupSnapshot> Groups => _groups;
        public IReadOnlyList<OriginalSaveRuntimeStoryPairSnapshot> PairRecords => _pairRecords;
        public IReadOnlyList<int> MapValues => _mapValues;
        public IReadOnlyList<OriginalSaveRuntimeStoryZoneSnapshot> Zones => _zones;
        public string SelectedTempName { get; }
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
        public int MapWidth { get; }
        public int MapHeight { get; }
        public string CallbackScript { get; }
        public int CallbackObjectId { get; }
        public int ZoneFloor { get; }
        public int LatchDirection { get; }
        public int LatchX { get; }
        public int LatchY { get; }
        public bool LatchConsumed { get; }
        public int DigWeight { get; }
        public bool DigMessageEnabled { get; }
        public int DigThreshold { get; }
    }

    public sealed class OriginalSaveRuntimeStoryEventSnapshot
    {
        internal OriginalSaveRuntimeStoryEventSnapshot(OriginalSaveStoryEventDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Name = source.Name.Text;
            Trigger = source.Trigger.Text;
            Extra = source.Extra.Text;
            Completed = source.Completed;
        }

        public string Name { get; }
        public string Trigger { get; }
        public string Extra { get; }
        public bool Completed { get; }
    }

    public sealed class OriginalSaveRuntimeStoryTempSnapshot
    {
        private readonly ReadOnlyCollection<int> _eventIndices;

        internal OriginalSaveRuntimeStoryTempSnapshot(OriginalSaveStoryTempDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Name = source.Name.Text;
            NativeIndexA = source.NativeIndexA;
            NativeIndexB = source.NativeIndexB;
            Placed = source.Placed;
            var eventIndices = new int[source.EventIndices.Count];
            for (int index = 0; index < eventIndices.Length; index++)
                eventIndices[index] = source.EventIndices[index];
            _eventIndices = Array.AsReadOnly(eventIndices);
            LowerDepth = source.LowerDepth;
            UpperDepth = source.UpperDepth;
            MinimumDistanceBits = source.MinimumDistance.Bits;
            MinimumDistanceReferenceIndex = source.MinimumDistanceReferenceIndex;
            NativeWidth = source.NativeWidth;
            NativeHeight = source.NativeHeight;
            DigConnectValue = source.DigConnectValue;
            TemplateGroupIndex = source.TemplateGroupIndex;
            Exclusive = source.Exclusive;
            NativeOriginX = source.NativeOriginX;
            NativeOriginY = source.NativeOriginY;
            ZoneIndex = source.ZoneIndex;
            ZoneBorderIndex = source.ZoneBorderIndex;
            PlacedX = source.PlacedX;
            PlacedY = source.PlacedY;
            CheckAlign = source.CheckAlign;
            StoneIndex = source.StoneIndex;
            MiddleDistanceBits = source.MiddleDistance.Bits;
            NonRelevantProbabilityBits = source.NonRelevantProbability.Bits;
        }

        public string Name { get; }
        public int NativeIndexA { get; }
        public int NativeIndexB { get; }
        public bool Placed { get; }
        public IReadOnlyList<int> EventIndices => _eventIndices;
        public int LowerDepth { get; }
        public int UpperDepth { get; }
        public int MinimumDistanceBits { get; }
        public float MinimumDistance => BitConverter.Int32BitsToSingle(MinimumDistanceBits);
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
        public int MiddleDistanceBits { get; }
        public float MiddleDistance => BitConverter.Int32BitsToSingle(MiddleDistanceBits);
        public int NonRelevantProbabilityBits { get; }
        public float NonRelevantProbability =>
            BitConverter.Int32BitsToSingle(NonRelevantProbabilityBits);
    }

    public sealed class OriginalSaveRuntimeStoryGroupSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeStoryGroupEntrySnapshot> _entries;
        private readonly ReadOnlyCollection<int> _headerValues;
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _fourValueRecords;
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _fiveValueRecords;

        internal OriginalSaveRuntimeStoryGroupSnapshot(OriginalSaveStoryGroupDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Name = source.Name.Text;
            var entries = new OriginalSaveRuntimeStoryGroupEntrySnapshot[source.Entries.Count];
            for (int index = 0; index < entries.Length; index++)
                entries[index] = new OriginalSaveRuntimeStoryGroupEntrySnapshot(source.Entries[index]);
            _entries = Array.AsReadOnly(entries);
            _headerValues = CopyValues(source.HeaderValues);
            _fourValueRecords = CopyRecords(source.FourValueRecords);
            _fiveValueRecords = CopyRecords(source.FiveValueRecords);
        }

        public string Name { get; }
        public IReadOnlyList<OriginalSaveRuntimeStoryGroupEntrySnapshot> Entries => _entries;
        public IReadOnlyList<int> HeaderValues => _headerValues;
        public IReadOnlyList<IReadOnlyList<int>> FourValueRecords => _fourValueRecords;
        public IReadOnlyList<IReadOnlyList<int>> FiveValueRecords => _fiveValueRecords;

        public string BuildPortSpec()
        {
            var rows = new List<string>(_entries.Count + (_fiveValueRecords.Count == 0 ? 0 : 1));
            foreach (OriginalSaveRuntimeStoryGroupEntrySnapshot entry in _entries)
            {
                rows.Add(TclList.Format(new[]
                {
                    Format(entry.Values[0]),
                    Format(entry.Values[1]),
                    entry.Name,
                }));
            }

            if (_fiveValueRecords.Count != 0)
            {
                var matRow = new List<string>(_fiveValueRecords.Count + 1) { "mat" };
                foreach (IReadOnlyList<int> record in _fiveValueRecords)
                {
                    matRow.Add(TclList.Format(new[]
                    {
                        Format(record[4]),
                        Format(record[0]),
                        Format(record[1]),
                        Format(checked(record[2] - record[0])),
                        Format(checked(record[3] - record[1])),
                    }));
                }
                rows.Add(TclList.Format(matRow));
            }
            return TclList.Format(rows);
        }

        public string BuildPortConnectSpec()
        {
            var records = new string[_fourValueRecords.Count];
            for (int index = 0; index < records.Length; index++)
            {
                IReadOnlyList<int> record = _fourValueRecords[index];
                records[index] = TclList.Format(new[]
                {
                    Format(record[0]),
                    Format(record[1]),
                    Format(record[2]),
                    Format(record[3]),
                });
            }
            return TclList.Format(records);
        }

        private static ReadOnlyCollection<int> CopyValues(IReadOnlyList<int> source)
        {
            var values = new int[source.Count];
            for (int index = 0; index < values.Length; index++) values[index] = source[index];
            return Array.AsReadOnly(values);
        }

        private static ReadOnlyCollection<IReadOnlyList<int>> CopyRecords(
            IReadOnlyList<IReadOnlyList<int>> source)
        {
            var records = new IReadOnlyList<int>[source.Count];
            for (int index = 0; index < records.Length; index++)
                records[index] = CopyValues(source[index]);
            return Array.AsReadOnly(records);
        }

        private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class OriginalSaveRuntimeStoryGroupEntrySnapshot
    {
        private readonly ReadOnlyCollection<int> _values;

        internal OriginalSaveRuntimeStoryGroupEntrySnapshot(OriginalSaveStoryGroupEntryDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Name = source.Name.Text;
            var values = new int[source.Values.Count];
            for (int index = 0; index < values.Length; index++) values[index] = source.Values[index];
            _values = Array.AsReadOnly(values);
            Completed = source.Completed;
        }

        public string Name { get; }
        public IReadOnlyList<int> Values => _values;
        public bool Completed { get; }
    }

    public sealed class OriginalSaveRuntimeStoryPairSnapshot
    {
        internal OriginalSaveRuntimeStoryPairSnapshot(OriginalSaveStoryPairRecordDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            First = source.First.Text;
            Second = source.Second.Text;
            Value = source.Value;
        }

        public string First { get; }
        public string Second { get; }
        public int Value { get; }
    }

    public sealed class OriginalSaveRuntimeStoryZoneSnapshot
    {
        internal OriginalSaveRuntimeStoryZoneSnapshot(OriginalSaveStoryZoneDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Name = source.Name.Text;
            UpperBound = source.UpperBound;
        }

        public string Name { get; }
        public int UpperBound { get; }
    }
}
