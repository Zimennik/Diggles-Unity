#nullable enable

namespace Diggles.Runtime.World
{
    public sealed class L2mMap
    {
        public uint Width { get; init; }
        public uint Height { get; init; }
        public ushort[] Cells { get; init; } = System.Array.Empty<ushort>();
        public long FileSize { get; init; }
        public long BytesConsumed { get; init; }

        public long BytesRemaining => this.FileSize - this.BytesConsumed;
        public ushort CellAt(int x, int y) => this.Cells[y * this.Width + x];

        public static (int nw, int ne, int sw, int se) DecodeCorners(ushort cell)
        {
            return (
                CornerBits(cell, 14),
                CornerBits(cell, 12),
                CornerBits(cell, 10),
                CornerBits(cell, 8));
        }

        public static int BlockType(ushort cell) => (cell >> 5) & 0x7;

        public static int AnimFrame(ushort cell) => cell & 0x1F;

        private static int CornerBits(ushort cell, int lsbPos)
            => (cell >> lsbPos) & 0x3;
    }
}
