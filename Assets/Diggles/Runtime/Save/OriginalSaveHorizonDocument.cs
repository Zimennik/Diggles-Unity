#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveHorizonRegionDocument
    {
        internal OriginalSaveHorizonRegionDocument(
            OriginalSaveRaw32 positionX,
            OriginalSaveRaw32 positionY,
            OriginalSaveRaw32 positionZ,
            int red,
            int green,
            int blue,
            OriginalSaveRaw32 depth,
            OriginalSaveRaw32 size)
        {
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            Red = red;
            Green = green;
            Blue = blue;
            Depth = depth;
            Size = size;
        }

        public OriginalSaveRaw32 PositionX { get; }
        public OriginalSaveRaw32 PositionY { get; }
        public OriginalSaveRaw32 PositionZ { get; }
        public int Red { get; }
        public int Green { get; }
        public int Blue { get; }
        public OriginalSaveRaw32 Depth { get; }
        public OriginalSaveRaw32 Size { get; }
    }

    public sealed class OriginalSaveHorizonDocument
    {
        private const int MaximumRecordCount = 4 * 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveHorizonRegionDocument> _regions;

        internal OriginalSaveHorizonDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.Marker15Offset + sizeof(int));
            int end = checked((int)metadata.Marker16Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            int position = start;
            int regionCount = ReadInt32(sourcePayload, ref position, end);
            if (regionCount < 0 || regionCount > MaximumRecordCount)
                throw new InvalidDataException($"Invalid native horizon-region count {regionCount}.");
            if (regionCount != metadata.EightValueRecordCount)
            {
                throw new InvalidDataException(
                    "Typed horizon parsing disagrees with the structurally validated region count.");
            }

            var regions = new OriginalSaveHorizonRegionDocument[regionCount];
            for (int index = 0; index < regions.Length; index++)
            {
                regions[index] = new OriginalSaveHorizonRegionDocument(
                    ReadRaw32(sourcePayload, ref position, end),
                    ReadRaw32(sourcePayload, ref position, end),
                    ReadRaw32(sourcePayload, ref position, end),
                    ReadInt32(sourcePayload, ref position, end),
                    ReadInt32(sourcePayload, ref position, end),
                    ReadInt32(sourcePayload, ref position, end),
                    ReadRaw32(sourcePayload, ref position, end),
                    ReadRaw32(sourcePayload, ref position, end));
            }
            if (position != end)
            {
                throw new InvalidDataException(
                    $"Typed horizon registry ended at {position}, expected marker 16 at {end}.");
            }
            _regions = Array.AsReadOnly(regions);
        }

        public IReadOnlyList<OriginalSaveHorizonRegionDocument> Regions => _regions;

        private static OriginalSaveRaw32 ReadRaw32(byte[] source, ref int position, int end) =>
            new(ReadInt32(source, ref position, end));

        private static int ReadInt32(byte[] source, ref int position, int end)
        {
            if (position > end - sizeof(int))
                throw new EndOfStreamException("Unexpected end of native horizon registry.");
            int value = OriginalSaveWorldDocument.ReadInt32(source, position);
            position += sizeof(int);
            return value;
        }
    }

    public sealed class OriginalSaveRuntimeHorizonSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeHorizonRegion> _regions;

        internal OriginalSaveRuntimeHorizonSnapshot(OriginalSaveHorizonDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var regions = new OriginalSaveRuntimeHorizonRegion[source.Regions.Count];
            for (int index = 0; index < regions.Length; index++)
            {
                OriginalSaveHorizonRegionDocument region = source.Regions[index];
                regions[index] = new OriginalSaveRuntimeHorizonRegion(
                    region.PositionX.AsSingle,
                    region.PositionY.AsSingle,
                    region.PositionZ.AsSingle,
                    region.Red,
                    region.Green,
                    region.Blue,
                    region.Depth.AsSingle,
                    region.Size.AsSingle);
            }
            _regions = Array.AsReadOnly(regions);
        }

        public IReadOnlyList<OriginalSaveRuntimeHorizonRegion> Regions => _regions;
    }

    public readonly struct OriginalSaveRuntimeHorizonRegion
    {
        internal OriginalSaveRuntimeHorizonRegion(
            float positionX,
            float positionY,
            float positionZ,
            int red,
            int green,
            int blue,
            float depth,
            float size)
        {
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            Red = red;
            Green = green;
            Blue = blue;
            Depth = depth;
            Size = size;
        }

        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public int Red { get; }
        public int Green { get; }
        public int Blue { get; }
        public float Depth { get; }
        public float Size { get; }
    }
}
