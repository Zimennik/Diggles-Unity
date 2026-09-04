#nullable enable

namespace Diggles.Runtime.World
{
    using System.IO;
    using System.Text;

    public static class L2mParser
    {
        private static readonly byte[] Magic = { (byte)'M', (byte)'2', (byte)'L', 0 };
        private const int HeaderSize = 12;

        public static L2mMap Parse(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Parse(stream, stream.Length);
        }

        public static L2mMap Parse(Stream stream, long fileSize)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (fileSize == 0)
            {
                return new L2mMap { FileSize = 0, BytesConsumed = 0 };
            }
            if (fileSize < HeaderSize)
            {
                throw new InvalidDataException($"L2M file too small: {fileSize}");
            }

            var magic = reader.ReadBytes(4);
            for (var i = 0; i < 4; i++)
            {
                if (magic[i] != Magic[i])
                {
                    throw new InvalidDataException(
                        $"L2M bad magic at offset 0: got " +
                        $"{magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}, " +
                        $"expected 4D 32 4C 00");
                }
            }

            var width = reader.ReadUInt32();
            var height = reader.ReadUInt32();
            var cellCount = (int)(width * height);
            var expected = HeaderSize + cellCount * 2L;
            if (fileSize != expected)
            {
                throw new InvalidDataException(
                    $"L2M size mismatch: file={fileSize} expected={expected} " +
                    $"(W={width} H={height})");
            }

            var cells = new ushort[cellCount];
            for (var i = 0; i < cellCount; i++) cells[i] = reader.ReadUInt16();

            return new L2mMap
            {
                Width = width,
                Height = height,
                Cells = cells,
                FileSize = fileSize,
                BytesConsumed = stream.Position,
            };
        }
    }
}
