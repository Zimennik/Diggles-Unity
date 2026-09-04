#nullable enable

namespace Diggles.Runtime
{
    using System.IO;

    // The cave dig tools' 32×32 template stencils — the faithful load half of the mark-manager
    // ctor FUN_005661c0:68-89:
    //
    // • four templates, `data/templates/cave{0..3}.tga` (32-bit TGA, 32×32), tool 1..4 =
    // template 0..3 (window tool − 1 → mgr +0xdc, FUN_005300f0:182);
    // • decode: every buffer byte b → (b + 0x20) >> 6 (FUN_005661c0:83-84); the stamp reads
    // the first byte of each 4-byte texel (FUN_00566a60:56 / FUN_00567cb0:163 index
    // (row*0x20 + col)*4) = the blue channel of the 32-bit BGRA TGA;
    // • cell values: 0..3 = carve — the mode-0 painter's cnt (nibble (cnt|8), open-depth
    // targets {0→0, 1→3, 2→7, 3→11}); 4 (white) = skip;
    // • row orientation: the stamp covers world rows
    // [ay−32, ay) with buffer row r = worldY − (ay−32) (FUN_00567cb0:150-163), and the
    // decoded carve shapes put the cnt-3 "door lip" rows at r=30..31 — the mouth sits at the
    // anchor and the cave body extends up (smaller y = up, the ant-farm frame). The TGAs are
    // bottom-left origin (descriptor 0x08), so stored row 0 = the mouth row = buffer row 31.
    // Decoded shapes: cave0 Tiny 8w×5h (cnt 1), cave1 Small 12×5 (cnt 0),
    // cave2 Medium 16×9 (cnt 0), cave3 Huge 12×12 dome (cnt 0) — all with cnt-3 lips on the
    // two mouth rows (BOTTOM_MENU_RE.md §3).
    public sealed class CaveTemplate
    {
        public const int Size = 32;
        public const int Skip = 4;

        public readonly int Index;              // template/tool index 0..3 (mgr +0xdc)
        readonly byte[] _cells;                 // row-major 32×32; row 0 = top, row 31 = the mouth row

        public CaveTemplate(int index, byte[] cells)
        {
            this.Index = index;
            this._cells = cells;
        }

        // Cell value at (col, row); outside the 32×32 stencil = Skip (the native indexes the
        // buffer blindly, but real templates keep their border rows/cols all-skip, so the
        // out-of-stencil read only ever feeds the border checks — treat it as skip).
        public int Cell(int col, int row)
            => (uint)col < Size && (uint)row < Size ? _cells[row * Size + col] : Skip;

        // Bounding box of the carve cells (col0, row0, col1, row1 inclusive) — test/diag surface.
        public (int c0, int r0, int c1, int r1) CarveBounds()
        {
            int c0 = Size, r0 = Size, c1 = -1, r1 = -1;
            for (int r = 0; r < Size; r++)
                for (int c = 0; c < Size; c++)
                    if (_cells[r * Size + c] != Skip)
                    {
                        if (c < c0) c0 = c;
                        if (r < r0) r0 = r;
                        if (c > c1) c1 = c;
                        if (r > r1) r1 = r;
                    }
            return (c0, r0, c1, r1);
        }

        // Decode one cave TGA (uncompressed type 2 or RLE type 10, 24/32 bpp). Blue = the first
        // byte per pixel in both layouts; cell = (blue + 0x20) >> 6 (FUN_005661c0:83-84). Rows
        // are stored bottom-up unless the descriptor's top-left bit (0x20) is set — normalized
        // here to row 0 = top so buffer row r maps to world y = (anchor − 32) + r verbatim.
        public static CaveTemplate Decode(byte[] tga, int index)
        {
            if (tga.Length < 18) throw new InvalidDataException("cave TGA too short");
            int idLen = tga[0], imageType = tga[2];
            int w = tga[12] | (tga[13] << 8), h = tga[14] | (tga[15] << 8);
            int bpp = tga[16], desc = tga[17];
            if (w != Size || h != Size)
                throw new InvalidDataException($"cave TGA must be 32×32, got {w}×{h}");
            if (bpp != 24 && bpp != 32)
                throw new InvalidDataException($"cave TGA unsupported bpp {bpp}");
            int bypp = bpp / 8, off = 18 + idLen;

            // Blue bytes in stored row order.
            var blue = new byte[w * h];
            switch (imageType)
            {
                case 2:
                    for (int i = 0; i < w * h; i++) blue[i] = tga[off + i * bypp];
                    break;
                case 10:
                    for (int i = 0, p = off; i < w * h;)
                    {
                        int hdr = tga[p++], cnt = (hdr & 0x7F) + 1;
                        if ((hdr & 0x80) != 0)
                        {
                            byte b = tga[p];
                            p += bypp;
                            for (int k = 0; k < cnt; k++) blue[i++] = b;
                        }
                        else
                            for (int k = 0; k < cnt; k++) { blue[i++] = tga[p]; p += bypp; }
                    }
                    break;
                default:
                    throw new InvalidDataException($"cave TGA unsupported image type {imageType}");
            }

            bool topDown = (desc & 0x20) != 0;
            var cells = new byte[w * h];
            for (int r = 0; r < h; r++)
            {
                int src = topDown ? r : h - 1 - r;   // normalize to row 0 = top
                for (int c = 0; c < w; c++)
                    cells[r * w + c] = (byte)((blue[src * w + c] + 0x20) >> 6);
            }
            return new CaveTemplate(index, cells);
        }
    }

    public static class CaveTemplates
    {
        // Load the four stencils from <dataRoot>/templates/cave{0..3}.tga (FUN_005661c0:68-71).
        // A missing/corrupt file leaves its slot null (the caller treats that tool as inert).
        public static CaveTemplate?[] Load(string dataRoot)
        {
            var result = new CaveTemplate?[4];
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(dataRoot, "templates", $"cave{i}.tga");
                try
                {
                    if (File.Exists(path))
                        result[i] = CaveTemplate.Decode(File.ReadAllBytes(path), i);
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogWarning($"[CaveTemplates] {path}: {e.Message}");
                }
            }
            return result;
        }
    }
}
