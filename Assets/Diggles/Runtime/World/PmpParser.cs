#nullable enable

namespace Diggles.Runtime.World
{
    using System.IO;
    using System.Text;

    public static class PmpParser
    {
        private const int RleOverflowCount = 0x4000;
        private const byte NavigationValueMask = 0x3;

        public static PmpMap Parse(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Parse(stream, stream.Length);
        }

        public static PmpMap Parse(Stream stream, long fileSize)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            var cellCount = (int)(width * height);

            var terrain = reader.ReadBytes(cellCount);
            if (terrain.Length != cellCount)
            {
                throw new InvalidDataException(
                    $"PMP truncated reading terrain ({terrain.Length}/{cellCount})");
            }

            var colors = new uint[cellCount];
            for (var i = 0; i < cellCount; i++) colors[i] = reader.ReadUInt32();

            var trailerCount = reader.ReadUInt32();
            var authoredNavigationPlane = ExpandAuthoredNavigationPlane(
                reader,
                (int)trailerCount,
                cellCount);

            var heightMap = reader.ReadBytes(cellCount);
            if (heightMap.Length != cellCount)
            {
                throw new InvalidDataException(
                    $"PMP truncated reading heightmap ({heightMap.Length}/{cellCount})");
            }

            return new PmpMap
            {
                Width = width,
                Height = height,
                TerrainType = terrain,
                ColorOrLighting = colors,
                AuthoredNavigationPlane = authoredNavigationPlane,
                HeightMap = heightMap,
                FileSize = fileSize,
                BytesConsumed = stream.Position,
            };
        }

        private static byte[] ExpandAuthoredNavigationPlane(
            BinaryReader reader, int trailerCount, int cellCount)
        {
            var expected = cellCount * PmpMap.SubGridLength;
            var output = new byte[expected];
            var cursor = 0;
            for (var i = 0; i < trailerCount; i++)
            {
                // FUN_005992b0:538-544: low two bits are the authored plane byte;
                // the high fourteen bits are the run, with zero spelling 0x4000.
                var unit = reader.ReadUInt16();
                var count = unit >> 2;
                if (count == 0) count = RleOverflowCount;
                var value = (byte)(unit & NavigationValueMask);
                if (cursor + count > expected)
                {
                    throw new InvalidDataException(
                        $"PMP navigation RLE overrun at unit {i}: " +
                        $"cursor={cursor} count={count} expected={expected}");
                }
                for (var c = 0; c < count; c++) output[cursor + c] = value;
                cursor += count;
            }
            if (cursor != expected)
            {
                throw new InvalidDataException(
                    $"PMP navigation RLE underrun: filled {cursor}/{expected} bytes");
            }
            return output;
        }
    }
}
