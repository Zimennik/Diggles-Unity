#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Text;

    // One marker-7 spatial-index record: a dormant scenery seed. Docs/save-re-reports/
    // B3-support-class-spatial.md identifies these as one-shot scenery spawn seeds living in the
    // CObjMgr bucket grid at CObjMgr+0x84A4 (transfer FUN_005c6cf0). At the native detail gate the
    // per-tick LOD pass FUN_005d4b40 spawns a lightweight render instance from each record and
    // removes it (swap-with-last); the classes are grass/moss/beams/walls (Dummy_*), cave skins
    // (Urw_caves_*) and light props.
    //
    // Serialized layout (stride 0x34 in memory, transfer order FUN_005c6cf0; verified against the
    // reader's SkipSpatialIndexRecord):
    // +0x00..+0x14 6 × Int32 float-bits position X/Y/Z, rotation X/Y/Z (Euler radians)
    // (class registry index +0x28 written as a NAME) class name native string
    // +0x18, +0x1C 2 × Int32 variant/model selectors (small enums)
    // +0x24 Int32 0 or 2 (unnamed)
    // +0x2C Int32 flags word (bit 4 = delete-when-rect-cleared)
    // +0x30 byte (ver > 0x8C) LOD tier 0/1/2
    // +0x31 byte (ver > 0x8C) player/visibility mask
    public readonly struct OriginalSaveSpatialSeed
    {
        internal OriginalSaveSpatialSeed(
            string className,
            float positionX,
            float positionY,
            float positionZ,
            float rotationX,
            float rotationY,
            float rotationZ,
            int selectorA,
            int selectorB,
            int word24,
            int flags,
            byte lodTier,
            byte visibilityMask)
        {
            ClassName = className;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            RotationX = rotationX;
            RotationY = rotationY;
            RotationZ = rotationZ;
            SelectorA = selectorA;
            SelectorB = selectorB;
            Word24 = word24;
            Flags = flags;
            LodTier = lodTier;
            VisibilityMask = visibilityMask;
        }

        public string ClassName { get; }
        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public float RotationX { get; }
        public float RotationY { get; }
        public float RotationZ { get; }
        public int SelectorA { get; }
        public int SelectorB { get; }
        public int Word24 { get; }
        public int Flags { get; }
        public byte LodTier { get; }
        public byte VisibilityMask { get; }
    }

    // Typed decode of the whole marker-7 spatial index. The raw bytes stay retained verbatim by
    // OriginalSaveStructureBuilder for byte-exact round-trip; this document is the read-only view
    // the load path uses to materialize the deco (OriginalSaveRuntimeSessionStager →
    // DigglesTclEngine.MaterializeSaveDecoSeeds). Nothing here is written back.
    public sealed class OriginalSaveSpatialSeedDocument
    {
        private const int MaximumStringLength = 1024 * 1024;
        private const int MaximumRecordCount = 4 * 1024 * 1024;
        private readonly ReadOnlyCollection<OriginalSaveSpatialSeed> _records;

        internal OriginalSaveSpatialSeedDocument(
            byte[] sourcePayload,
            OriginalSaveObjectManifest manifest)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            int start = checked((int)manifest.SpatialIndexOffset);
            int end = checked((int)manifest.Marker7Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(manifest));

            var records = new List<OriginalSaveSpatialSeed>(manifest.SpatialRecordCount);
            var cursor = new Cursor(sourcePayload, start, end);
            for (int bucket = 0; bucket < manifest.SpatialBucketCount; bucket++)
            {
                int recordCount = cursor.ReadInt32();
                if (recordCount < 0 || recordCount > MaximumRecordCount)
                    throw new InvalidDataException($"Invalid spatial-index bucket count {recordCount}.");
                for (int record = 0; record < recordCount; record++)
                {
                    float positionX = cursor.ReadSingle();
                    float positionY = cursor.ReadSingle();
                    float positionZ = cursor.ReadSingle();
                    float rotationX = cursor.ReadSingle();
                    float rotationY = cursor.ReadSingle();
                    float rotationZ = cursor.ReadSingle();
                    string className = cursor.ReadNativeString();
                    int selectorA = cursor.ReadInt32();
                    int selectorB = cursor.ReadInt32();
                    int word24 = cursor.ReadInt32();
                    int flags = cursor.ReadInt32();
                    byte lodTier = cursor.ReadByte();
                    byte visibilityMask = cursor.ReadByte();
                    records.Add(new OriginalSaveSpatialSeed(
                        className,
                        positionX,
                        positionY,
                        positionZ,
                        rotationX,
                        rotationY,
                        rotationZ,
                        selectorA,
                        selectorB,
                        word24,
                        flags,
                        lodTier,
                        visibilityMask));
                }
            }

            cursor.ExpectEnd();
            if (records.Count != manifest.SpatialRecordCount)
            {
                throw new InvalidDataException(
                    $"Typed spatial-index decode found {records.Count} records; the manifest " +
                    $"counted {manifest.SpatialRecordCount}.");
            }
            _records = new ReadOnlyCollection<OriginalSaveSpatialSeed>(records);
        }

        public IReadOnlyList<OriginalSaveSpatialSeed> Records => _records;

        private sealed class Cursor
        {
            private static readonly Encoding Windows1252 = Encoding.GetEncoding(1252);
            private readonly byte[] _source;
            private readonly int _end;
            private int _position;

            internal Cursor(byte[] source, int start, int end)
            {
                _source = source;
                _position = start;
                _end = end;
            }

            internal byte ReadByte()
            {
                EnsureAvailable(sizeof(byte));
                return _source[_position++];
            }

            internal int ReadInt32()
            {
                EnsureAvailable(sizeof(int));
                int value = OriginalSaveWorldDocument.ReadInt32(_source, _position);
                _position += sizeof(int);
                return value;
            }

            internal float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

            internal string ReadNativeString()
            {
                int length = ReadInt32();
                if (length <= 0 || length > MaximumStringLength)
                    throw new InvalidDataException($"Invalid native spatial-seed string length {length}.");
                EnsureAvailable(length);
                int textLength = length - 1;
                string value = Windows1252.GetString(_source, _position, textLength);
                if (_source[_position + textLength] != 0)
                    throw new InvalidDataException("Native spatial-seed string is not NUL-terminated.");
                _position += length;
                return value;
            }

            internal void ExpectEnd()
            {
                if (_position != _end)
                {
                    throw new InvalidDataException(
                        $"Typed spatial-index decode ended at {_position}, expected {_end}.");
                }
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)_position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native spatial index.");
            }
        }
    }
}
