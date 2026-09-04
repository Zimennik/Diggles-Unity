#nullable enable

namespace Diggles.Runtime.World
{
    using System.Collections.Generic;
    using UnityEngine;

    public sealed class BdbHeader
    {
        public uint Version { get; init; }
        public uint NBlocks { get; init; }
        public uint NIndex { get; init; }       // u16 triangle-vertex-index pool count
        public uint NDescriptors { get; init; } // (offset, count) descriptor pairs into NIndex
        public uint NPalette { get; init; }     // global vertex palette entry count (5 floats each)
    }

    public sealed class BdbCell
    {
        public ushort VertCount { get; init; }   // per-cell vertex pool size
        public uint DescriptorIdx { get; init; } // index into Descriptors
        public int BulkOffset { get; init; }     // byte offset into bulk for per-cell vertex pool
    }

    public sealed class BdbBlock
    {
        public IReadOnlyList<BdbCell> Cells { get; init; } = System.Array.Empty<BdbCell>();
    }

    public readonly struct BdbDescriptor
    {
        public BdbDescriptor(uint offset, uint count) { this.Offset = offset; this.Count = count; }
        public uint Offset { get; }
        public uint Count { get; }
    }

    public readonly struct BdbPaletteEntry
    {
        public BdbPaletteEntry(Vector3 position, Vector2 uv) { this.Position = position; this.Uv = uv; }
        public Vector3 Position { get; }
        public Vector2 Uv { get; }
    }

    public sealed class BdbModel
    {
        public BdbHeader Header { get; init; } = new();
        public IReadOnlyList<BdbBlock> Blocks { get; init; } = System.Array.Empty<BdbBlock>();
        public byte[] PerBlockAux { get; init; } = System.Array.Empty<byte>();
        public ushort[] IndexPool { get; init; } = System.Array.Empty<ushort>();
        public BdbDescriptor[] Descriptors { get; init; } = System.Array.Empty<BdbDescriptor>();
        public BdbPaletteEntry[] Palette { get; init; } = System.Array.Empty<BdbPaletteEntry>();
        public byte[] BulkRaw { get; init; } = System.Array.Empty<byte>();
        public long FileSize { get; init; }
        public long BytesConsumed { get; init; }

        public long BytesRemaining => this.FileSize - this.BytesConsumed;

        /// <summary>
        /// Returns the triangle vertex index range (start, count) into IndexPool for one
        /// cell of one block. count is always a multiple of 3 (triangle list).
        /// Returns (0, 0) if the cell has no mesh.
        /// </summary>
        public (int Start, int Count) CellTriangleRange(int blockIdx, int subIdx)
        {
            if ((uint)blockIdx >= this.Blocks.Count) return (0, 0);
            var cell = this.Blocks[blockIdx].Cells[subIdx];
            if (cell.VertCount == 0 || cell.DescriptorIdx >= this.Descriptors.Length) return (0, 0);
            var d = this.Descriptors[cell.DescriptorIdx];
            if (d.Count == 0 || d.Count % 3 != 0) return (0, 0);
            var end = d.Offset + d.Count;
            if (end > (uint)this.IndexPool.Length) return (0, 0);
            return ((int)d.Offset, (int)d.Count);
        }

        /// <summary>
        /// Copies per-cell vertex pool (palette indices + normal indices) into caller buffers.
        /// Buffers must be at least cell.VertCount long.
        ///
        /// Note: in-file layout is not interleaved — first comes a packed `u16[vert_count]`
        /// palette-index array, then a packed `u8[vert_count]` normal-index array. See
        /// CBlockDB::LoadDB lines 215-220 (Ghidra) for the runtime pointer setup.
        /// </summary>
        public void CopyCellPaletteIndices(int blockIdx, int subIdx, ushort[] dst, byte[] normals)
        {
            if ((uint)blockIdx >= this.Blocks.Count) return;
            var cell = this.Blocks[blockIdx].Cells[subIdx];
            if (cell.VertCount == 0) return;
            int palBase = cell.BulkOffset;                       // u16 array
            int normBase = cell.BulkOffset + cell.VertCount * 2; // u8 array follows
            for (int i = 0; i < cell.VertCount && i < dst.Length && i < normals.Length; i++)
            {
                int p = palBase + i * 2;
                dst[i] = (ushort)(this.BulkRaw[p] | (this.BulkRaw[p + 1] << 8));
                normals[i] = this.BulkRaw[normBase + i];
            }
        }

        // Per-block aux blob accessors — 89 bytes per block, layout:
        // +0x00..0x3F 8×8 byte heightmap (values 0..15)
        // +0x40..0x4F 4 neighbour stitching block indices (uint32 each)
        // +0x50..0x53 4 corner heights (NW/NE/SW/SE — cached samples
        // from the heightmap corners, used by climb / pathfinding)
        // +0x54 variant/flag byte (low nibble = shader variant)
        // +0x55..0x58 fluid descriptor (u32): capacity bits 0..9,
        // upper boundary bits 10..13, lower boundary bits 14..17
        // See re/notes/RENDERING_PIPELINE_COMPLETE.md §2.1.1.
        public const int AuxStride = 89;
        public const int AuxOffCornerNW = 0x50;
        public const int AuxOffCornerNE = 0x51;
        public const int AuxOffCornerSW = 0x52;
        public const int AuxOffCornerSE = 0x53;
        public const int AuxOffVariant  = 0x54;
        public const int AuxOffFluid    = 0x55;
        public const int AuxOffCenter   = 0x24;  // center heightmap cell

        public byte AuxCornerNW(int blockIdx) => AuxAt(blockIdx, AuxOffCornerNW);
        public byte AuxCornerNE(int blockIdx) => AuxAt(blockIdx, AuxOffCornerNE);
        public byte AuxCornerSW(int blockIdx) => AuxAt(blockIdx, AuxOffCornerSW);
        public byte AuxCornerSE(int blockIdx) => AuxAt(blockIdx, AuxOffCornerSE);
        public byte AuxVariant(int blockIdx)  => AuxAt(blockIdx, AuxOffVariant);
        public byte AuxCenter(int blockIdx)   => AuxAt(blockIdx, AuxOffCenter);

        // CFluid reads this packed descriptor directly from the BDB block record. Capacity is
        // measured in native fluid-volume units; both boundaries are eighth-cell coordinates.
        public uint AuxFluidDescriptor(int blockIdx)
        {
            int p = blockIdx * AuxStride + AuxOffFluid;
            if (p < 0 || p + 4 > PerBlockAux.Length) return 0;
            return (uint)(PerBlockAux[p] | (PerBlockAux[p + 1] << 8) |
                (PerBlockAux[p + 2] << 16) | (PerBlockAux[p + 3] << 24));
        }

        public int AuxFluidCapacity(int blockIdx) => (int)(AuxFluidDescriptor(blockIdx) & 0x3ffu);
        public int AuxFluidUpperBoundary(int blockIdx) => (int)((AuxFluidDescriptor(blockIdx) >> 10) & 0xfu);
        public int AuxFluidLowerBoundary(int blockIdx) => (int)((AuxFluidDescriptor(blockIdx) >> 14) & 0xfu);

        // Per-sub-cell terrain height nibble (0..15) from the block's 8×8 heightmap (aux 0x00..0x3F,
        // row-major subIdx = (subY&7)*8 + (subX&7)). This is what the engine's get_hmap (FUN_004093f0)
        // reads: aux[block_idx*0x59 + ((subY&7)<<3 | (subX&7))]. World Z = nibble*0.5 + 7.0.
        // (Distinct from AuxMaxCorner, which collapses the tile to its highest corner.)
        public byte AuxHeightAt(int blockIdx, int subIdx) => AuxAt(blockIdx, subIdx & 0x3F);

        // True 8×8-heightmap corners (top-surface corner heights 0..15) — these drive the engine's
        // mesh + the GetBlockHole >4 cliff trigger. Distinct from the 0x50..0x53 cached gate bytes
        // above (which the dig-transition graph compares against the digger's layer; usually equal but
        // not always — e.g. block 22 corner SE=12 vs gate=11). Heightmap is row-major 8×8:
        // NW=(col0,row0)=+0x00 NE=(col7,row0)=+0x07 SW=(col0,row7)=+0x38 SE=(col7,row7)=+0x3F.
        public byte AuxTrueCornerNW(int blockIdx) => AuxAt(blockIdx, 0x00);
        public byte AuxTrueCornerNE(int blockIdx) => AuxAt(blockIdx, 0x07);
        public byte AuxTrueCornerSW(int blockIdx) => AuxAt(blockIdx, 0x38);
        public byte AuxTrueCornerSE(int blockIdx) => AuxAt(blockIdx, 0x3F);

        // The engine carve graph (FUN_00596b80 / FUN_005968b0). Per block, aux holds, for each of the 4
        // corners (0=NW 1=NE 2=SW 3=SE), the block this one becomes when that corner is dug one rim-step
        // lower — a u32 at +0x40/+0x44/+0x48/+0x4c (0xFFFFFFFF = terminal). Following one corner repeatedly
        // steps it 15→11→7→3→0 down to the flat floor (block 59 self-loops). The matching cached corner
        // height (the transition target) is the gate byte at +0x50/+0x51/+0x52/+0x53.
        // Verified on real BDB2: blk0 NW chain 0→54→34511→39259→47449(0). See Docs/DIGGING_MECHANIC.md.
        public uint AuxCornerNeighbour(int blockIdx, int corner)
        {
            int p = blockIdx * AuxStride + 0x40 + (corner & 3) * 4;
            if (p < 0 || p + 4 > this.PerBlockAux.Length) return 0xFFFFFFFFu;
            return (uint)(PerBlockAux[p] | (PerBlockAux[p + 1] << 8) | (PerBlockAux[p + 2] << 16) | (PerBlockAux[p + 3] << 24));
        }
        public byte AuxCornerGate(int blockIdx, int corner) => AuxAt(blockIdx, 0x50 + (corner & 3));
        public int AuxMaxCorner(int blockIdx) => System.Math.Max(
            System.Math.Max(AuxTrueCornerNW(blockIdx), AuxTrueCornerNE(blockIdx)),
            System.Math.Max(AuxTrueCornerSW(blockIdx), AuxTrueCornerSE(blockIdx)));

        // Open-corner pattern → the original game's own BDB block for that carved configuration.
        // Bit order NW=8 NE=4 SW=2 SE=1; a set bit = that corner is open/floor (height 0), clear =
        // solid (height 15). This reproduces, by terminal corner geometry, the block the engine's carve
        // resolves a tile to via the aux neighbour-transition graph (FUN_00596b80): 0b1111→59 (flat
        // floor), 0b0011→56 (straight wall), 0b0001→55 (inside corner), 0b0111→58 (outside corner), …
        // The walls render because these cliff blocks carry the side-face geometry in their own cells
        // (our renderer meshes cells directly — no separate GetBlockHole pass needed).
        // Built once by scanning every block for a pure-0/15 corner signature, keeping the lowest index
        // (the canonical base-family primitive). 0xFFFFFFFF = no such block (only the 0b1001 two-diagonal
        // case, which a contiguous dig never produces).
        uint[]? _cornerPatternTable;
        public uint CornerPatternBlock(int openMask)
            => (_cornerPatternTable ??= BuildCornerPatternTable())[openMask & 0xF];

        uint[] BuildCornerPatternTable()
        {
            var t = new uint[16];
            for (int m = 0; m < 16; m++) t[m] = 0xFFFFFFFF;
            int n = this.Blocks.Count;
            for (int b = 0; b < n; b++)
            {
                int nw = AuxTrueCornerNW(b), ne = AuxTrueCornerNE(b), sw = AuxTrueCornerSW(b), se = AuxTrueCornerSE(b);
                if ((nw == 0 || nw == 15) && (ne == 0 || ne == 15) && (sw == 0 || sw == 15) && (se == 0 || se == 15))
                {
                    int mask = (nw == 0 ? 8 : 0) | (ne == 0 ? 4 : 0) | (sw == 0 ? 2 : 0) | (se == 0 ? 1 : 0);
                    if (t[mask] == 0xFFFFFFFF) t[mask] = (uint)b;   // keep the lowest matching index
                }
            }
            return t;
        }

        byte AuxAt(int blockIdx, int offset)
        {
            int p = blockIdx * AuxStride + offset;
            if (p < 0 || p >= this.PerBlockAux.Length) return 0;
            return this.PerBlockAux[p];
        }
    }
}
