#nullable enable

namespace Diggles.Runtime.World
{
    using System.IO;
    using System.Text;
    using UnityEngine;

    /// <summary>
    /// Parses.BDB block-database archive. Layout (verified via Ghidra of CBlockDB::LoadDB
    /// and FUN_004ca830 per-tile renderer):
    ///
    /// 0x00..0x1B 7 × u32 header (version=0x0B, _, nBlocks, _, nIndex, nDescriptors, nPalette)
    /// 0x1C.. cells[nBlocks][3] × 6B (each = u16 vert_count + u32 descriptor_idx)
    /// 3 sub-cells per block = co-rendered geometry/material
    /// submeshes, not vertical voxel layers or alternate blocks
    /// +nBlocks·0x59 per-block aux (89 B): 24B header + 8x8 byte heightmap @ +0x18..+0x3F
    /// +nIndex·2 triangle-vertex-index pool (u16 each), indexed via descriptor ranges
    /// +nDescriptors·8 descriptor array: each (u32 offset_in_index_pool, u32 count)
    /// +nPalette·20 global vertex palette: 5 floats per entry (x, y, z, u, v) — model-local
    /// bulk per non-empty cell: vert_count × (u16 palette_idx + u8 normal_idx)
    ///
    /// To draw block_idx's mesh: for each sub_cell ∈ [0,2], look up its descriptor → idx range
    /// in the index pool → those u16s index the per-cell vertex pool → each pool entry has
    /// a palette index → palette gives (x, y, z, u, v).
    ///
    /// Texture mapping: every BDB shares one global atlas; UVs in palette are NOT normalised
    /// (range ~[0, 7.5]); shader applies `(uv - 0.5) * scale + 0.5` (see FUN_00462b40).
    /// </summary>
    public static class BdbParser
    {
        private const int HeaderSize = 0x1C;
        private const int CellsPerBlock = 3;
        private const int CellRecordSize = 6;
        private const int PerBlockAuxStride = 0x59; // 89 B
        private const int DescriptorStride = 8;
        private const int PaletteStride = 20;

        public static BdbModel Parse(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Parse(stream, stream.Length);
        }

        // Process-wide parsed-model cache: the 37 MB BlockDB2.BDB was re-parsed (~65-75 MB of GC
        // traffic) on every in-game load. BdbModel is source data — consumers only read it (plus
        // one idempotent lazy corner table) — so one parse per path serves every session. The
        // data root is fixed for the process lifetime.
        private static readonly System.Collections.Generic.Dictionary<string, BdbModel>
            _parseCache = new(System.StringComparer.OrdinalIgnoreCase);

        public static BdbModel ParseCached(string filePath)
        {
            string key = Path.GetFullPath(filePath);
            if (_parseCache.TryGetValue(key, out var cached)) return cached;
            BdbModel model = Parse(key);
            _parseCache[key] = model;
            return model;
        }

        public static BdbModel Parse(Stream stream, long fileSize)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var version = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var nBlocks = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var nIndex = reader.ReadUInt32();
            var nDescriptors = reader.ReadUInt32();
            var nPalette = reader.ReadUInt32();

            if (version != 0x0B)
            {
                throw new InvalidDataException($"BDB version {version:X} != 0x0B");
            }

            var blocks = new BdbBlock[nBlocks];
            for (var bi = 0; bi < nBlocks; bi++)
            {
                var cells = new BdbCell[CellsPerBlock];
                for (var ci = 0; ci < CellsPerBlock; ci++)
                {
                    var vertCount = reader.ReadUInt16();
                    var descIdx = reader.ReadUInt32();
                    cells[ci] = new BdbCell { VertCount = vertCount, DescriptorIdx = descIdx };
                }
                blocks[bi] = new BdbBlock { Cells = cells };
            }

            var perBlockAux = reader.ReadBytes((int)(nBlocks * PerBlockAuxStride));

            var indexPool = new ushort[nIndex];
            for (var i = 0; i < nIndex; i++) indexPool[i] = reader.ReadUInt16();

            var descriptors = new BdbDescriptor[nDescriptors];
            for (var i = 0; i < nDescriptors; i++)
            {
                var off = reader.ReadUInt32();
                var cnt = reader.ReadUInt32();
                descriptors[i] = new BdbDescriptor(off, cnt);
            }

            var palette = new BdbPaletteEntry[nPalette];
            for (var i = 0; i < nPalette; i++)
            {
                var x = reader.ReadSingle();
                var y = reader.ReadSingle();
                var z = reader.ReadSingle();
                var u = reader.ReadSingle();
                var v = reader.ReadSingle();
                palette[i] = new BdbPaletteEntry(new Vector3(x, y, z), new Vector2(u, v));
            }

            var bulkStart = (int)stream.Position;
            var bulkOffset = bulkStart;
            for (var bi = 0; bi < nBlocks; bi++)
            {
                var newCells = new BdbCell[CellsPerBlock];
                for (var ci = 0; ci < CellsPerBlock; ci++)
                {
                    var src = blocks[bi].Cells[ci];
                    newCells[ci] = new BdbCell
                    {
                        VertCount = src.VertCount,
                        DescriptorIdx = src.DescriptorIdx,
                        BulkOffset = bulkOffset - bulkStart,
                    };
                    bulkOffset += src.VertCount * 3; // (u16 palette_idx + u8 normal_idx)
                }
                blocks[bi] = new BdbBlock { Cells = newCells };
            }

            var bulkSize = bulkOffset - bulkStart;
            var bulkRaw = reader.ReadBytes(bulkSize);

            return new BdbModel
            {
                Header = new BdbHeader
                {
                    Version = version,
                    NBlocks = nBlocks,
                    NIndex = nIndex,
                    NDescriptors = nDescriptors,
                    NPalette = nPalette,
                },
                Blocks = blocks,
                PerBlockAux = perBlockAux,
                IndexPool = indexPool,
                Descriptors = descriptors,
                Palette = palette,
                BulkRaw = bulkRaw,
                FileSize = fileSize,
                BytesConsumed = stream.Position,
            };
        }
    }
}
