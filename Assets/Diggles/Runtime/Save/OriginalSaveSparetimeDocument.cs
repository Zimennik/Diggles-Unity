#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveSparetimeRecord
    {
        internal OriginalSaveSparetimeRecord(int category, int objectId)
        {
            Category = category;
            ObjectId = objectId;
        }

        public int Category { get; }
        public int ObjectId { get; }
    }

    public sealed class OriginalSaveSparetimeDocument
    {
        private const int MaximumRecordCount = 4 * 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveSparetimeRecord> _records;

        internal OriginalSaveSparetimeDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.Marker13Offset + sizeof(int));
            int end = checked((int)metadata.Marker14Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            int position = start;
            int recordCount = ReadInt32(sourcePayload, ref position, end);
            if (recordCount < 0 || recordCount > MaximumRecordCount)
                throw new InvalidDataException(
                    $"Invalid native sparetime registration count {recordCount}.");
            if (recordCount != metadata.PairRecordCount)
            {
                throw new InvalidDataException(
                    "Typed sparetime parsing disagrees with the structurally validated record count.");
            }

            var records = new OriginalSaveSparetimeRecord[recordCount];
            for (int index = 0; index < records.Length; index++)
            {
                records[index] = new OriginalSaveSparetimeRecord(
                    ReadInt32(sourcePayload, ref position, end),
                    ReadInt32(sourcePayload, ref position, end));
            }
            if (position != end)
            {
                throw new InvalidDataException(
                    $"Typed sparetime registry ended at {position}, expected marker 14 at {end}.");
            }
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveSparetimeRecord> Records => _records;

        private static int ReadInt32(byte[] source, ref int position, int end)
        {
            if (position > end - sizeof(int))
                throw new EndOfStreamException("Unexpected end of native sparetime registry.");
            int value = OriginalSaveWorldDocument.ReadInt32(source, position);
            position += sizeof(int);
            return value;
        }
    }

    public sealed class OriginalSaveRuntimeSparetimeSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeSparetimeRegistration> _registrations;

        internal OriginalSaveRuntimeSparetimeSnapshot(OriginalSaveSparetimeDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var registrations = new OriginalSaveRuntimeSparetimeRegistration[source.Records.Count];
            for (int index = 0; index < registrations.Length; index++)
            {
                OriginalSaveSparetimeRecord record = source.Records[index];
                registrations[index] = new OriginalSaveRuntimeSparetimeRegistration(
                    record.Category,
                    record.ObjectId);
            }
            _registrations = Array.AsReadOnly(registrations);
        }

        public IReadOnlyList<OriginalSaveRuntimeSparetimeRegistration> Registrations => _registrations;
    }

    public readonly struct OriginalSaveRuntimeSparetimeRegistration
    {
        internal OriginalSaveRuntimeSparetimeRegistration(int category, int objectId)
        {
            Category = category;
            ObjectId = objectId;
        }

        public int Category { get; }
        public int ObjectId { get; }
    }
}
