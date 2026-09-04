#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveAdaptiveSoundMarkerRecord
    {
        internal OriginalSaveAdaptiveSoundMarkerRecord(
            int objectId,
            OriginalSaveRaw32 positionX,
            OriginalSaveRaw32 positionY,
            OriginalSaveRaw32 positionZ,
            OriginalSaveNativeString theme,
            OriginalSaveRaw32 range)
        {
            ObjectId = objectId;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            Theme = theme ?? throw new ArgumentNullException(nameof(theme));
            Range = range;
        }

        public int ObjectId { get; }
        public OriginalSaveRaw32 PositionX { get; }
        public OriginalSaveRaw32 PositionY { get; }
        public OriginalSaveRaw32 PositionZ { get; }
        public OriginalSaveNativeString Theme { get; }
        public OriginalSaveRaw32 Range { get; }
        public bool IsObjectBound => ObjectId >= 0;
    }

    public sealed class OriginalSaveAdaptiveSoundMarkerDocument
    {
        private const int MaximumRecordCount = 4 * 1024 * 1024;
        private const int MaximumStringLength = 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveAdaptiveSoundMarkerRecord> _markers;

        internal OriginalSaveAdaptiveSoundMarkerDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.Marker16Offset + sizeof(int));
            int end = checked((int)metadata.Marker17Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            var cursor = new Cursor(sourcePayload, start, end);
            int markerCount = cursor.ReadInt32();
            if (markerCount < 0 || markerCount > MaximumRecordCount)
            {
                throw new InvalidDataException(
                    $"Invalid native adaptive-sound marker count {markerCount}.");
            }
            if (markerCount != metadata.NamedRecordCount)
            {
                throw new InvalidDataException(
                    "Typed adaptive-sound marker parsing disagrees with the structurally " +
                    "validated record count.");
            }

            var markers = new OriginalSaveAdaptiveSoundMarkerRecord[markerCount];
            for (int index = 0; index < markers.Length; index++)
            {
                markers[index] = new OriginalSaveAdaptiveSoundMarkerRecord(
                    cursor.ReadInt32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadRaw32(),
                    cursor.ReadNativeString(),
                    cursor.ReadRaw32());
            }
            cursor.ExpectEnd();
            _markers = Array.AsReadOnly(markers);
        }

        public IReadOnlyList<OriginalSaveAdaptiveSoundMarkerRecord> Markers => _markers;

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
                {
                    throw new InvalidDataException(
                        $"Invalid native adaptive-sound marker string length {length}.");
                }
                EnsureAvailable(length);
                var bytes = new byte[length];
                Buffer.BlockCopy(_source, Position, bytes, 0, length);
                Position += length;
                if (bytes[^1] != 0)
                {
                    throw new InvalidDataException(
                        "Native adaptive-sound marker string is not NUL-terminated.");
                }
                return new OriginalSaveNativeString(bytes);
            }

            internal void ExpectEnd()
            {
                if (Position != _end)
                {
                    throw new InvalidDataException(
                        $"Typed adaptive-sound marker registry ended at {Position}, " +
                        $"expected marker 17 at {_end}.");
                }
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                {
                    throw new EndOfStreamException(
                        "Unexpected end of native adaptive-sound marker registry.");
                }
            }
        }
    }

    public sealed class OriginalSaveRuntimeAdaptiveSoundMarkerSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeAdaptiveSoundMarker> _markers;

        internal OriginalSaveRuntimeAdaptiveSoundMarkerSnapshot(
            OriginalSaveAdaptiveSoundMarkerDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var markers = new OriginalSaveRuntimeAdaptiveSoundMarker[source.Markers.Count];
            for (int index = 0; index < markers.Length; index++)
            {
                OriginalSaveAdaptiveSoundMarkerRecord marker = source.Markers[index];
                markers[index] = new OriginalSaveRuntimeAdaptiveSoundMarker(
                    marker.ObjectId,
                    marker.PositionX.AsSingle,
                    marker.PositionY.AsSingle,
                    marker.PositionZ.AsSingle,
                    marker.Theme.Text,
                    marker.Range.AsSingle);
            }
            _markers = Array.AsReadOnly(markers);
        }

        public IReadOnlyList<OriginalSaveRuntimeAdaptiveSoundMarker> Markers => _markers;
    }

    public readonly struct OriginalSaveRuntimeAdaptiveSoundMarker
    {
        internal OriginalSaveRuntimeAdaptiveSoundMarker(
            int objectId,
            float positionX,
            float positionY,
            float positionZ,
            string theme,
            float range)
        {
            ObjectId = objectId;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            Theme = theme ?? throw new ArgumentNullException(nameof(theme));
            Range = range;
        }

        public int ObjectId { get; }
        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public string Theme { get; }
        public float Range { get; }
        public bool IsObjectBound => ObjectId >= 0;
    }
}
