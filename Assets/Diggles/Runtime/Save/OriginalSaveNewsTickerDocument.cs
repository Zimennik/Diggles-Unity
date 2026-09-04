#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;

    public sealed class OriginalSaveNewsTickerActiveRecord
    {
        internal OriginalSaveNewsTickerActiveRecord(
            OriginalSaveNativeString text,
            int packedColor,
            int category,
            int textMetricX,
            int textMetricY,
            OriginalSaveRaw32 priority,
            OriginalSaveRaw32 effectivePriority,
            OriginalSaveRaw32 remainingTime,
            int id,
            byte presentationFlag0,
            byte presentationFlag1,
            OriginalSaveNativeString clickScript,
            OriginalSaveRaw32 timestampLow,
            OriginalSaveRaw32 timestampHigh,
            byte appearsInHistory)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            PackedColor = packedColor;
            NativeCategory = category;
            TextMetricX = textMetricX;
            TextMetricY = textMetricY;
            Priority = priority;
            EffectivePriority = effectivePriority;
            RemainingTime = remainingTime;
            Id = id;
            PresentationFlag0 = presentationFlag0;
            PresentationFlag1 = presentationFlag1;
            ClickScript = clickScript ?? throw new ArgumentNullException(nameof(clickScript));
            TimestampLow = timestampLow;
            TimestampHigh = timestampHigh;
            AppearsInHistory = appearsInHistory;
        }

        public OriginalSaveNativeString Text { get; }
        public int PackedColor { get; }
        public int NativeCategory { get; }
        public int TextMetricX { get; }
        public int TextMetricY { get; }
        public OriginalSaveRaw32 Priority { get; }
        public OriginalSaveRaw32 EffectivePriority { get; }
        public OriginalSaveRaw32 RemainingTime { get; }
        public int Id { get; }
        public byte PresentationFlag0 { get; }
        public byte PresentationFlag1 { get; }
        public OriginalSaveNativeString ClickScript { get; }
        public OriginalSaveRaw32 TimestampLow { get; }
        public OriginalSaveRaw32 TimestampHigh { get; }
        public byte AppearsInHistory { get; }
        public double Timestamp => BitConverter.Int64BitsToDouble(
            unchecked((long)(((ulong)(uint)TimestampHigh.Bits << 32) |
                             (uint)TimestampLow.Bits)));
    }

    public sealed class OriginalSaveNewsTickerHistoryRecord
    {
        internal OriginalSaveNewsTickerHistoryRecord(
            OriginalSaveNativeString text,
            int packedColor,
            OriginalSaveNativeString clickScript,
            int category,
            int id)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            PackedColor = packedColor;
            ClickScript = clickScript ?? throw new ArgumentNullException(nameof(clickScript));
            NativeCategory = category;
            Id = id;
        }

        public OriginalSaveNativeString Text { get; }
        public int PackedColor { get; }
        public OriginalSaveNativeString ClickScript { get; }
        public int NativeCategory { get; }
        public int Id { get; }
    }

    public sealed class OriginalSaveNewsTickerDisplayQueueRecord
    {
        internal OriginalSaveNewsTickerDisplayQueueRecord(int id, OriginalSaveRaw32 position)
        {
            Id = id;
            Position = position;
        }

        public int Id { get; }
        public OriginalSaveRaw32 Position { get; }
    }

    public sealed class OriginalSaveNewsTickerDocument
    {
        private const int MaximumRecordCount = 4 * 1024 * 1024;
        private const int MaximumStringLength = 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveNewsTickerActiveRecord> _active;
        private readonly ReadOnlyCollection<OriginalSaveNewsTickerHistoryRecord> _history;
        private readonly ReadOnlyCollection<OriginalSaveNewsTickerDisplayQueueRecord> _displayQueue;

        internal OriginalSaveNewsTickerDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.NewsTickerStateOffset);
            int end = checked((int)metadata.NewsTickerStateEndOffset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            var cursor = new Cursor(sourcePayload, start, end);
            int activeCount = ReadCount(cursor, "active notification");
            if (activeCount != metadata.NewsTickerActiveCount)
            {
                throw new InvalidDataException(
                    "Typed news-ticker parsing disagrees with the structurally validated " +
                    "active count.");
            }
            var active = new OriginalSaveNewsTickerActiveRecord[activeCount];
            for (int index = 0; index < active.Length; index++)
            {
                active[index] = new OriginalSaveNewsTickerActiveRecord(
                    cursor.ReadNativeString(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadInt32(),
                    cursor.ReadByte(),
                    cursor.ReadByte(),
                    cursor.ReadNativeString(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadByte());
            }

            int historyCount = ReadCount(cursor, "history notification");
            if (historyCount != metadata.NewsTickerHistoryCount)
            {
                throw new InvalidDataException(
                    "Typed news-ticker parsing disagrees with the structurally validated " +
                    "history count.");
            }
            var history = new OriginalSaveNewsTickerHistoryRecord[historyCount];
            for (int index = 0; index < history.Length; index++)
            {
                history[index] = new OriginalSaveNewsTickerHistoryRecord(
                    cursor.ReadNativeString(),
                    cursor.ReadInt32(),
                    cursor.ReadNativeString(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32());
            }

            EnabledCategoryMask = cursor.ReadInt32();
            int queueCount = ReadCount(cursor, "display-queue");
            if (queueCount != metadata.NewsTickerDisplayQueueCount)
            {
                throw new InvalidDataException(
                    "Typed news-ticker parsing disagrees with the structurally validated " +
                    "display-queue count.");
            }
            var displayQueue = new OriginalSaveNewsTickerDisplayQueueRecord[queueCount];
            for (int index = 0; index < displayQueue.Length; index++)
            {
                displayQueue[index] = new OriginalSaveNewsTickerDisplayQueueRecord(
                    cursor.ReadInt32(),
                    cursor.ReadRaw32());
            }
            cursor.ExpectEnd();

            _active = Array.AsReadOnly(active);
            _history = Array.AsReadOnly(history);
            _displayQueue = Array.AsReadOnly(displayQueue);
            DerivedNextId = DeriveNextId(active, history, displayQueue);
        }

        public IReadOnlyList<OriginalSaveNewsTickerActiveRecord> Active => _active;
        public IReadOnlyList<OriginalSaveNewsTickerHistoryRecord> History => _history;
        public int EnabledCategoryMask { get; }
        public IReadOnlyList<OriginalSaveNewsTickerDisplayQueueRecord> DisplayQueue => _displayQueue;
        public int DerivedNextId { get; }

        private static int ReadCount(Cursor cursor, string label)
        {
            int count = cursor.ReadInt32();
            if (count < 0 || count > MaximumRecordCount)
                throw new InvalidDataException($"Invalid native news-ticker {label} count {count}.");
            return count;
        }

        private static int DeriveNextId(
            OriginalSaveNewsTickerActiveRecord[] active,
            OriginalSaveNewsTickerHistoryRecord[] history,
            OriginalSaveNewsTickerDisplayQueueRecord[] displayQueue)
        {
            int maximum = 0;
            foreach (OriginalSaveNewsTickerActiveRecord entry in active)
                maximum = Math.Max(maximum, entry.Id);
            foreach (OriginalSaveNewsTickerHistoryRecord entry in history)
                maximum = Math.Max(maximum, entry.Id);
            foreach (OriginalSaveNewsTickerDisplayQueueRecord entry in displayQueue)
                maximum = Math.Max(maximum, entry.Id);
            if (maximum == int.MaxValue)
                throw new NotSupportedException("Native news-ticker ID space is exhausted.");
            return maximum + 1;
        }

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

            internal OriginalSaveNativeString ReadNativeString()
            {
                int length = ReadInt32();
                if (length <= 0 || length > MaximumStringLength)
                    throw new InvalidDataException($"Invalid native news-ticker string length {length}.");
                EnsureAvailable(length);
                var bytes = new byte[length];
                Buffer.BlockCopy(_source, Position, bytes, 0, length);
                Position += length;
                if (bytes[^1] != 0)
                    throw new InvalidDataException("Native news-ticker string is not NUL-terminated.");
                return new OriginalSaveNativeString(bytes);
            }

            internal void ExpectEnd()
            {
                if (Position != _end)
                {
                    throw new InvalidDataException(
                        $"Typed news-ticker state ended at {Position}, expected {_end}.");
                }
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native news-ticker state.");
            }
        }
    }

    public sealed class OriginalSaveRuntimeNewsTickerSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeNewsTickerActiveEntry> _active;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeNewsTickerHistoryEntry> _history;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeNewsTickerDisplayQueueEntry> _displayQueue;

        internal OriginalSaveRuntimeNewsTickerSnapshot(OriginalSaveNewsTickerDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            NextId = source.DerivedNextId;
            EnabledCategoryMask = source.EnabledCategoryMask;

            var active = new OriginalSaveRuntimeNewsTickerActiveEntry[source.Active.Count];
            for (int index = 0; index < active.Length; index++)
                active[index] = new OriginalSaveRuntimeNewsTickerActiveEntry(source.Active[index]);
            _active = Array.AsReadOnly(active);

            var history = new OriginalSaveRuntimeNewsTickerHistoryEntry[source.History.Count];
            for (int index = 0; index < history.Length; index++)
                history[index] = new OriginalSaveRuntimeNewsTickerHistoryEntry(source.History[index]);
            _history = Array.AsReadOnly(history);

            var displayQueue =
                new OriginalSaveRuntimeNewsTickerDisplayQueueEntry[source.DisplayQueue.Count];
            for (int index = 0; index < displayQueue.Length; index++)
            {
                OriginalSaveNewsTickerDisplayQueueRecord entry = source.DisplayQueue[index];
                displayQueue[index] = new OriginalSaveRuntimeNewsTickerDisplayQueueEntry(
                    entry.Id,
                    entry.Position.AsSingle);
            }
            _displayQueue = Array.AsReadOnly(displayQueue);
        }

        public int NextId { get; }
        public int EnabledCategoryMask { get; }
        public IReadOnlyList<OriginalSaveRuntimeNewsTickerActiveEntry> Active => _active;
        public IReadOnlyList<OriginalSaveRuntimeNewsTickerHistoryEntry> History => _history;
        public IReadOnlyList<OriginalSaveRuntimeNewsTickerDisplayQueueEntry> DisplayQueue =>
            _displayQueue;

        public NewsTickerSystem.SaveState CreateSaveState()
        {
            var active = new NewsTickerSystem.EntrySaveState[Active.Count];
            for (int index = 0; index < active.Length; index++)
                active[index] = Active[index].CreateSaveState();
            var history = new NewsTickerSystem.EntrySaveState[History.Count];
            for (int index = 0; index < history.Length; index++)
                history[index] = History[index].CreateSaveState();
            var displayQueue = new NewsTickerSystem.DisplayQueueSaveState[DisplayQueue.Count];
            for (int index = 0; index < displayQueue.Length; index++)
            {
                displayQueue[index] = new NewsTickerSystem.DisplayQueueSaveState
                {
                    Id = DisplayQueue[index].Id,
                    Position = DisplayQueue[index].Position,
                };
            }
            return new NewsTickerSystem.SaveState
            {
                NextId = NextId,
                EnabledMask = EnabledCategoryMask,
                Active = active,
                History = history,
                DisplayQueue = displayQueue,
            };
        }

        internal static int Red(int packedColor) => (packedColor >> 16) & byte.MaxValue;
        internal static int Green(int packedColor) => (packedColor >> 8) & byte.MaxValue;
        internal static int Blue(int packedColor) => packedColor & byte.MaxValue;

        internal static int ToSemanticCategory(int nativeCategory)
        {
            // Native allocation leaves this word uninitialized unless Tcl supplies -category.
            // Preserve the raw word in the compatibility snapshot, but expose the command's
            // semantic default to the safe runtime model.
            return (uint)nativeCategory < NewsTickerSystem.CategoryCount
                ? nativeCategory
                : (int)NewsTickerCategory.Fight;
        }
    }

    public readonly struct OriginalSaveRuntimeNewsTickerActiveEntry
    {
        internal OriginalSaveRuntimeNewsTickerActiveEntry(
            OriginalSaveNewsTickerActiveRecord source)
        {
            Id = source.Id;
            Text = source.Text.Text;
            PackedColor = source.PackedColor;
            NativeCategory = source.NativeCategory;
            Category = OriginalSaveRuntimeNewsTickerSnapshot.ToSemanticCategory(NativeCategory);
            TextMetricX = source.TextMetricX;
            TextMetricY = source.TextMetricY;
            Priority = source.Priority.AsSingle;
            EffectivePriority = source.EffectivePriority.AsSingle;
            RemainingTime = source.RemainingTime.AsSingle;
            PresentationFlag0 = source.PresentationFlag0;
            PresentationFlag1 = source.PresentationFlag1;
            ClickScript = source.ClickScript.Text;
            Timestamp = source.Timestamp;
            AppearsInHistory = source.AppearsInHistory;
        }

        public int Id { get; }
        public string Text { get; }
        public int PackedColor { get; }
        public int NativeCategory { get; }
        public int Category { get; }
        public int TextMetricX { get; }
        public int TextMetricY { get; }
        public float Priority { get; }
        public float EffectivePriority { get; }
        public float RemainingTime { get; }
        public byte PresentationFlag0 { get; }
        public byte PresentationFlag1 { get; }
        public string ClickScript { get; }
        public double Timestamp { get; }
        public byte AppearsInHistory { get; }

        internal NewsTickerSystem.EntrySaveState CreateSaveState() => new()
        {
            Id = Id,
            Text = Text,
            Category = Category,
            Red = OriginalSaveRuntimeNewsTickerSnapshot.Red(PackedColor),
            Green = OriginalSaveRuntimeNewsTickerSnapshot.Green(PackedColor),
            Blue = OriginalSaveRuntimeNewsTickerSnapshot.Blue(PackedColor),
            PackedColorWord = PackedColor,
            NativeCategoryWord = NativeCategory,
            TextMetricX = TextMetricX,
            TextMetricY = TextMetricY,
            Priority = Priority,
            EffectivePriority = EffectivePriority,
            RemainingTime = RemainingTime,
            PresentationFlag0 = PresentationFlag0,
            PresentationFlag1 = PresentationFlag1,
            ClickScript = ClickScript,
            Timestamp = Timestamp,
            AppearsInHistory = AppearsInHistory != 0,
        };
    }

    public readonly struct OriginalSaveRuntimeNewsTickerHistoryEntry
    {
        internal OriginalSaveRuntimeNewsTickerHistoryEntry(
            OriginalSaveNewsTickerHistoryRecord source)
        {
            Id = source.Id;
            Text = source.Text.Text;
            PackedColor = source.PackedColor;
            ClickScript = source.ClickScript.Text;
            NativeCategory = source.NativeCategory;
            Category = OriginalSaveRuntimeNewsTickerSnapshot.ToSemanticCategory(NativeCategory);
        }

        public int Id { get; }
        public string Text { get; }
        public int PackedColor { get; }
        public string ClickScript { get; }
        public int NativeCategory { get; }
        public int Category { get; }

        internal NewsTickerSystem.EntrySaveState CreateSaveState() => new()
        {
            Id = Id,
            Text = Text,
            Category = Category,
            Red = OriginalSaveRuntimeNewsTickerSnapshot.Red(PackedColor),
            Green = OriginalSaveRuntimeNewsTickerSnapshot.Green(PackedColor),
            Blue = OriginalSaveRuntimeNewsTickerSnapshot.Blue(PackedColor),
            PackedColorWord = PackedColor,
            NativeCategoryWord = NativeCategory,
            Priority = 1f,
            RemainingTime = NewsTickerSystem.DefaultTimeout,
            ClickScript = ClickScript,
            AppearsInHistory = true,
        };
    }

    public readonly struct OriginalSaveRuntimeNewsTickerDisplayQueueEntry
    {
        internal OriginalSaveRuntimeNewsTickerDisplayQueueEntry(int id, float position)
        {
            Id = id;
            Position = position;
        }

        public int Id { get; }
        public float Position { get; }
    }
}
