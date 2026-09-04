#nullable enable

namespace Diggles.Runtime.World
{
    public sealed class PmpMap
    {
        private byte[] _authoredNavigationPlane = System.Array.Empty<byte>();

        public const int SubGridSide = 8;
        public const int SubGridLength = SubGridSide * SubGridSide;

        public uint Width { get; init; }
        public uint Height { get; init; }
        public byte[] TerrainType { get; init; } = System.Array.Empty<byte>();
        public uint[] ColorOrLighting { get; init; } = System.Array.Empty<uint>();
        public byte[] AuthoredNavigationPlane
        {
            get => this._authoredNavigationPlane;
            init => this._authoredNavigationPlane = value ?? System.Array.Empty<byte>();
        }
        public byte[] SolidityVoxels
        {
            get => this._authoredNavigationPlane;
            init => this._authoredNavigationPlane = value ?? System.Array.Empty<byte>();
        }
        public byte[] HeightMap { get; init; } = System.Array.Empty<byte>();
        public long FileSize { get; init; }
        public long BytesConsumed { get; init; }

        public long BytesRemaining => this.FileSize - this.BytesConsumed;

        public byte TerrainAt(int x, int y) => this.TerrainType[y * this.Width + x];

        public byte HeightAt(int x, int y) => this.HeightMap[y * this.Width + x];

        // Field names per FUN_005992b0:
        // ColorOrLighting is the per-tile BDB block_idx (u32 stream).
        // HeightMap is actually the tile "zone" byte (default=4, template=20).
        // AuthoredNavigationPlane is the native tile+0x3c seed (2-bit values, RLE-encoded).
        // EFFPASSWALK may overwrite validated cells from the stamped BDB detail records.
        // SolidityVoxels is a source-compatible alias for editor tooling.
        public uint BlockIdxAt(int x, int y) => this.ColorOrLighting[y * this.Width + x];
        public byte ZoneAt(int x, int y) => this.HeightMap[y * this.Width + x];

        // FUN_005992b0:538-544 decodes value=word&3 and run=word>>2 (zero means 0x4000), then
        // FUN_005992b0:563-567 passes the raster directly to FUN_00596550. The plane is a sub-cell
        // raster row-major across the whole pmp — index (y*8+subY)*(Width*8) + (x*8+subX) — not 64
        // contiguous bytes per tile. (subY = the second packed exit coordinate.)
        public byte NavigationAt(int x, int y, int subX, int subY)
        {
            int gx = x * SubGridSide + subX;
            int gy = y * SubGridSide + subY;
            int k = gy * ((int)this.Width * SubGridSide) + gx;
            return (uint)k < (uint)this.AuthoredNavigationPlane.Length
                ? this.AuthoredNavigationPlane[k]
                : (byte)0;
        }

        public byte SolidityAt(int x, int y, int subX, int subY) =>
            this.NavigationAt(x, y, subX, subY);
    }
}
