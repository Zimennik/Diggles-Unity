#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using Diggles.Runtime.World;

    public readonly struct OriginalSaveCaveRecord
    {
        internal OriginalSaveCaveRecord(int x0, int y0, int x1, int y1, int templateIndex)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            TemplateIndex = templateIndex;
        }

        public int X0 { get; }
        public int Y0 { get; }
        public int X1 { get; }
        public int Y1 { get; }
        public int TemplateIndex { get; }
    }

    public sealed class OriginalSaveCaveRecordDocument
    {
        private const int SerializedWordsPerRecord = 5;
        private readonly ReadOnlyCollection<OriginalSaveCaveRecord> _records;

        internal OriginalSaveCaveRecordDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.CaveRecordStateOffset);
            int end = checked((int)metadata.CaveRecordStateEndOffset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            int offset = start;
            int count = ReadInt32(sourcePayload, ref offset);
            if (count != metadata.LateFiveValueRecordCount || count < 0)
                throw new InvalidDataException("Native cave-record count disagrees with structural metadata.");
            int expectedEnd = checked(offset + count * SerializedWordsPerRecord * sizeof(int));
            if (expectedEnd != end)
                throw new InvalidDataException("Native cave-record table has an invalid extent.");

            var records = new OriginalSaveCaveRecord[count];
            for (int index = 0; index < records.Length; index++)
            {
                records[index] = new OriginalSaveCaveRecord(
                    ReadInt32(sourcePayload, ref offset),
                    ReadInt32(sourcePayload, ref offset),
                    ReadInt32(sourcePayload, ref offset),
                    ReadInt32(sourcePayload, ref offset),
                    ReadInt32(sourcePayload, ref offset));
            }
            if (offset != end)
                throw new InvalidDataException("Typed cave-record parsing did not consume its section.");
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveCaveRecord> Records => _records;

        private static int ReadInt32(byte[] source, ref int offset)
        {
            int value = OriginalSaveWorldDocument.ReadInt32(source, offset);
            offset += sizeof(int);
            return value;
        }
    }

    public sealed class OriginalSaveRuntimeCaveRecordSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveCaveRecord> _records;

        internal OriginalSaveRuntimeCaveRecordSnapshot(OriginalSaveCaveRecordDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var records = new OriginalSaveCaveRecord[source.Records.Count];
            for (int index = 0; index < records.Length; index++)
                records[index] = source.Records[index];
            _records = Array.AsReadOnly(records);
        }

        public IReadOnlyList<OriginalSaveCaveRecord> Records => _records;

        public DiggableTerrain.CaveRecord[] CreateRuntimeRecords(
            DiggableTerrain terrain,
            int defaultOwner)
        {
            if (terrain == null) throw new ArgumentNullException(nameof(terrain));
            if ((uint)defaultOwner >= 8) defaultOwner = 0;
            var records = new DiggableTerrain.CaveRecord[Records.Count];
            for (int index = 0; index < records.Length; index++)
            {
                OriginalSaveCaveRecord source = Records[index];
                int owner = ResolveOwner(terrain, source, defaultOwner);
                int markId = ResolveMarkId(terrain, source, owner);
                records[index] = new DiggableTerrain.CaveRecord(
                    source.X0,
                    source.Y0,
                    source.X1,
                    source.Y1,
                    source.TemplateIndex,
                    markId,
                    owner);
            }
            return records;
        }

        private static int ResolveOwner(
            DiggableTerrain terrain,
            OriginalSaveCaveRecord record,
            int defaultOwner)
        {
            for (int owner = 0; owner < 8; owner++)
            {
                if (FindMarkedTile(terrain, record, owner, out _, out _))
                    return owner;
            }
            return defaultOwner;
        }

        private static int ResolveMarkId(
            DiggableTerrain terrain,
            OriginalSaveCaveRecord record,
            int owner)
        {
            return FindMarkedTile(terrain, record, owner, out int x, out int y)
                ? terrain.DigMarkIdAt(x, y, owner)
                : 0;
        }

        private static bool FindMarkedTile(
            DiggableTerrain terrain,
            OriginalSaveCaveRecord record,
            int owner,
            out int foundX,
            out int foundY)
        {
            int x0 = Math.Max(0, Math.Min(record.X0, record.X1));
            int y0 = Math.Max(0, Math.Min(record.Y0, record.Y1));
            int x1 = Math.Min(terrain.Width - 1, Math.Max(record.X0, record.X1));
            int y1 = Math.Min(terrain.Height - 1, Math.Max(record.Y0, record.Y1));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if ((terrain.RawDigNibble(x, y, owner) & 8) == 0) continue;
                    foundX = x;
                    foundY = y;
                    return true;
                }
            }
            foundX = 0;
            foundY = 0;
            return false;
        }
    }
}
