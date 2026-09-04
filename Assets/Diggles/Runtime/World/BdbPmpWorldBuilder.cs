#nullable enable

namespace Diggles.Runtime.World
{
    using Diggles.Runtime.World;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using UnityEngine;

    /// <summary>
    /// Builds the world mesh from real PMP files (no runtime dump needed).
    ///
    /// PMP layout:
    /// <code>
    /// +0x00 uint32 width
    /// +0x04 uint32 height
    /// +0x08 byte[w*h] terrain_type (high bit = "carved", low 7 bits = type code 0..5)
    /// +... uint32[w*h] block_idx ← directly blitted into tile_struct[+0x00]
    /// +... uint32 rle_count
    /// +... uint16[rle_count] navigation_plane_rle (expands to w*h*64 authored 0/1/3 seed bytes)
    /// +... byte[w*h] zone (4=default, 20=template area, etc.)
    /// </code>
    ///
    /// The engine builds the world by:
    /// 1. Pre-fills entire world with the default block (=0, solid earth).
    /// 2. For each <c>map_template &lt;path.pmp&gt; xoff yoff</c> Tcl call,
    /// blits the PMP block_idx + zone bytes into the world at (xoff, yoff).
    /// 3. <c>map_setlayer2</c> overlays L2M material/cellId data.
    /// 4. <c>sm_draw_stone -funnel</c> modifies diggability flags procedurally.
    ///
    /// This overload loads a single PMP directly.
    /// </summary>
    public static class BdbPmpWorldBuilder
    {
        public sealed class BuildArgs
        {
            public string BdbPath { get; init; } = "";
            public string PmpPath { get; init; } = "";
            public int OffsetX { get; init; }
            public int OffsetY { get; init; }
        }

        /// <summary>Overrides for which textures to bind at each stage. Null = use default.</summary>
        public sealed class TextureOverrides
        {
            public string? TopStage0Pattern;   // e.g. "Ground/Gnd_B{0:D2}.tga" — {0} = variant
            public string? TopStage1Pattern;   // e.g. "Ground/detail/urw_1111.tga" (no {0}=fixed)
            public string? SideStage0Pattern;
            public string? SideStage1Pattern;
            public string? BottomStage0Pattern;
            public string? BottomStage1Pattern;
            public bool RandomizeVariant = true;
            public int FixedVariant = 1;
        }

        /// <summary>Single PMP load into Unity directly.</summary>
        public static void BuildWorld(GameObject root, System.Action<string, UnityEngine.Object>? ctx, BuildArgs args)
        {
            var bdb = BdbParser.Parse(args.BdbPath);
            var pmp = PmpParser.Parse(args.PmpPath);
            Debug.Log($"[BdbPmpWorldBuilder] PMP {Path.GetFileName(args.PmpPath)}: {pmp.Width}x{pmp.Height} " +
                      $"({pmp.Width * pmp.Height} tiles), BDB {bdb.Header.NBlocks} blocks");

            var groundVerts = new List<Vector3>(); var groundUvs = new List<Vector2>(); var groundTris = new List<int>();
            var stoneVerts = new List<Vector3>(); var stoneUvs = new List<Vector2>(); var stoneTris = new List<int>();
            var groundIdx = new Dictionary<long, int>();
            var stoneIdx = new Dictionary<long, int>();

            const int MaxVerts = 4096;
            var palIdxBuf = new ushort[MaxVerts];
            var normIdxBuf = new byte[MaxVerts];

            int rendered = 0, airSkipped = 0, oob = 0;

            for (int py = 0; py < pmp.Height; py++)
            {
                for (int px = 0; px < pmp.Width; px++)
                {
                    int tx = px + args.OffsetX;
                    int ty = py + args.OffsetY;

                    uint blockIdx = pmp.BlockIdxAt(px, py);
                    byte zone = pmp.ZoneAt(px, py);
                    byte terrain = pmp.TerrainAt(px, py);

                    if (blockIdx >= bdb.Header.NBlocks) { oob++; continue; }
                    if (blockIdx == 59) { airSkipped++; continue; }

                    // Material: terrain 0x84/0x04 = stone wall, everything else = ground.
                    bool isStone = (terrain & 0x7F) == 0x04 || (zone == 20 && (terrain & 0x7F) != 0x05);
                    isStone = (terrain == 0x84) || (terrain == 0x04);
                    var (verts, uvs, tris, dedupe) = isStone
                        ? (stoneVerts, stoneUvs, stoneTris, stoneIdx)
                        : (groundVerts, groundUvs, groundTris, groundIdx);

                    for (int sub = 0; sub < 3; sub++)
                    {
                        var (triStart, triCount) = bdb.CellTriangleRange((int)blockIdx, sub);
                        if (triCount == 0) continue;
                        var cell = bdb.Blocks[(int)blockIdx].Cells[sub];
                        if (cell.VertCount == 0 || cell.VertCount > palIdxBuf.Length) continue;
                        bdb.CopyCellPaletteIndices((int)blockIdx, sub, palIdxBuf, normIdxBuf);

                        var idxPool = bdb.IndexPool;
                        for (int t = 0; t < triCount; t += 3)
                        {
                            int a = EmitVertex(idxPool[triStart + t], palIdxBuf, cell.VertCount, bdb, tx, ty, verts, uvs, dedupe);
                            int b = EmitVertex(idxPool[triStart + t + 1], palIdxBuf, cell.VertCount, bdb, tx, ty, verts, uvs, dedupe);
                            int c = EmitVertex(idxPool[triStart + t + 2], palIdxBuf, cell.VertCount, bdb, tx, ty, verts, uvs, dedupe);
                            if (a < 0 || b < 0 || c < 0) continue;
                            tris.Add(a); tris.Add(b); tris.Add(c);
                        }
                    }
                    rendered++;
                }
            }

            Debug.Log($"[BdbPmpWorldBuilder] rendered={rendered} air-skipped={airSkipped} oob={oob}");
            Debug.Log($"[BdbPmpWorldBuilder] ground={groundVerts.Count}v/{groundTris.Count / 3}t  stone={stoneVerts.Count}v/{stoneTris.Count / 3}t");

            BuildSubmesh(root, ctx, "Ground (PMP)", new Color(0.52f, 0.40f, 0.26f),
                groundVerts, groundUvs, groundTris, "PmpGroundMesh", "PmpGroundMat");
            BuildSubmesh(root, ctx, "Stone (PMP)", new Color(0.48f, 0.45f, 0.42f),
                stoneVerts, stoneUvs, stoneTris, "PmpStoneMesh", "PmpStoneMat");
        }

        /// <summary>
        /// Full-world build from a Campaign.tcl. Initializes a 512×640 world with the
        /// default solid block (= block 0), then blits every template's PMP block_idx
        /// into that world at its (xoffset, yoffset). Mirrors what the engine does
        /// during world load.
        ///
        /// Optionally crops to a bbox before rendering (saves Unity mesh memory and
        /// the empty solid earth around the playable area is mostly the same block 0).
        /// </summary>
        public static TextureOverrides? CurrentTextureOverrides;

        // Marker Y transform — applies the engine's canonical Z formula:
        // world.Y = TCL_Z * 0.5 + 7.0
        // where 0.5 = DAT_0073708c, 7.0 = DAT_00737520 in Diggles.exe.
        // This is FUN_004c6b20's formula (top_z = (aux[0x54] & 0xF) * 0.5 + 7.0)
        // applied at marker draw time. Terrain's palette.z is already in
        // post-transform units; TCL marker Z is in pre-transform units (raw
        // nibble-scale 0..15), so the transform must be applied here.
        public static float MarkerHeightScale = 0.5f;
        public static float MarkerYOffset = 7.0f;
        public static float MarkerScale = 1.0f;

        // Tcl-driven world result — exposes the in-memory tile arrays so
        // downstream systems (pathfinding, fog-of-war, save) can read them.
        // Lifetime: returned from BuildFromTclDriven; held by EditorMapBuilder.
        public sealed class TclWorldResult
        {
            public int Width;
            public int Height;
            public int MinX;
            public int MinY;
            public int MaxXExclusive;
            public int MaxYExclusive;
            public uint[] BlockIdx = System.Array.Empty<uint>();
            // Port presentation height-map compatibility data. Native BASELIGHT is derived and is
            // not serialized in the terrain tile record.
            public byte[] Zone = System.Array.Empty<byte>();
            public byte[] Terrain = System.Array.Empty<byte>();
            public ushort[] CellId = System.Array.Empty<ushort>();
            public ushort[] NativeTerrainFlags = System.Array.Empty<ushort>();
            public System.Collections.BitArray HasTpl = new(0);
            // Tiles whose PMP tile record (+0x18/block/height) was actually written. This is the
            // delayed-paint ordering mask for both boot and live stamps. Unlike HasTpl,
            // native-transparent padding is false here.
            public System.Collections.BitArray TemplateRecordWrites = new(0);
            // Legacy field name retained for save/editor callers. This is the native authored
            // Initial tile+0x3c navigation-plane seed, tile-major after the PMP raster is blitted.
            // MapValidator may overwrite validated cells from the BDB 0x59 records afterward.
            public byte[] Voxels = System.Array.Empty<byte>();
            public byte[] NavigationPlane => Voxels;
            public int RenderXMin, RenderYMin, RenderXMax = -1, RenderYMax = -1;   // rendered tile bbox (for re-mesh)
            // Per-tile flag: this block-59 tile is a dug-out floor (carved by the player) and must render
            // its flat floor — as opposed to a load-time block-59 "open air above the bowl" tile, which is
            // skipped. Shared with DiggableTerrain (it owns + flips this array). Null = nothing dug yet.
            public bool[]? DugFloor;
            // Chunked render state (dirty-rect remeshing). Set by BuildFromTclDriven for runtime worlds;
            // null on legacy/editor-importer builds (those keep the monolithic full-bbox remesh).
            public TerrainChunkGrid? Chunks;

            // Grow the rendered tile bbox to cover a runtime-stamped rect (world growth — a template
            // blitted in after the initial build). Without this the re-mesh skips the new tiles
            // (RemeshFromArrays renders RenderXMin..RenderXMax only).
            public void ExtendRenderBbox(int x0, int y0, int x1, int y1)
            {
                x0 = System.Math.Max(0, x0); y0 = System.Math.Max(0, y0);
                x1 = System.Math.Min(Width - 1, x1); y1 = System.Math.Min(Height - 1, y1);
                if (x1 < x0 || y1 < y0) return;
                if (RenderXMax < RenderXMin) { RenderXMin = x0; RenderYMin = y0; RenderXMax = x1; RenderYMax = y1; return; }
                if (x0 < RenderXMin) RenderXMin = x0;
                if (y0 < RenderYMin) RenderYMin = y0;
                if (x1 > RenderXMax) RenderXMax = x1;
                if (y1 > RenderYMax) RenderYMax = y1;
            }
        }

        // Tcl-driven entry point. Caller provides the BDB + pre-resolved PMP/L2M paths
        // (accumulated from a Tcl run of `map create / map_template / map_setlayer2 /
        // split_template`). No static parsing, no marker scan — markers spawn through Tcl
        // `new` + EditorEntitySpawner and reach the scene separately. Used by
        // EditorMapBuilder.EmitTerrain.
        //
        // Returns the populated world tile arrays so callers can derive a pathfinding
        // walkable bitmap or other tile-driven systems without re-parsing PMPs.
        public static TclWorldResult BuildFromTclDriven(
            GameObject root, System.Action<string, UnityEngine.Object>? ctx,
            string bdbPath,
            IReadOnlyList<(string pmpAbsPath, int worldX, int worldY)> pmps,
            IReadOnlyList<(string l2mAbsPath, int worldX, int worldY)> l2ms,
            int worldWidth, int worldHeight,
            string dataRoot,
            bool renderDefaultEarth = false)
        {
            var bdb = BdbParser.Parse(bdbPath);
            int N = worldWidth * worldHeight;
            var worldBlockIdx = new uint[N];
            var worldZone = new byte[N];
            var worldTerrain = new byte[N];
            var worldCellId = new ushort[N];
            var worldHasTpl = new System.Collections.BitArray(N);
            var worldTemplateRecordWrites = new System.Collections.BitArray(N);
            var worldNavigationPlane = new byte[N * PmpMap.SubGridLength];
            for (int i = 0; i < N; i++) worldZone[i] = 4;

            int loadedPmp = 0, loadedL2m = 0;
            foreach (var (path, wx, wy) in pmps)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var pmp = PmpParser.Parse(path);
                    BlitPmpIntoWorld(pmp, wx, wy, worldWidth, worldHeight,
                        worldBlockIdx, worldZone, worldTerrain, worldHasTpl, worldNavigationPlane,
                        (x, y) => worldTemplateRecordWrites[y * worldWidth + x] = true);
                    loadedPmp++;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[BdbPmpWorldBuilder.BuildFromTclDriven] PMP {Path.GetFileName(path)} @({wx},{wy}) failed: {e.Message}");
                }
            }
            foreach (var (path, wx, wy) in l2ms)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var l2m = L2mParser.Parse(path);
                    BlitL2mIntoWorld(l2m, wx, wy, worldWidth, worldHeight, worldCellId);
                    loadedL2m++;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[BdbPmpWorldBuilder.BuildFromTclDriven] L2M {Path.GetFileName(path)} @({wx},{wy}) failed: {e.Message}");
                }
            }
            Debug.Log($"[BdbPmpWorldBuilder.BuildFromTclDriven] loaded: {loadedPmp}/{pmps.Count} PMPs, {loadedL2m}/{l2ms.Count} L2Ms");

            int xMin = worldWidth, yMin = worldHeight, xMax = -1, yMax = -1;
            if (renderDefaultEarth)
            {
                xMin = 0; yMin = 0; xMax = worldWidth - 1; yMax = worldHeight - 1;
            }
            else
            {
                for (int ty = 0; ty < worldHeight; ty++)
                for (int tx = 0; tx < worldWidth; tx++)
                {
                    if (worldHasTpl[ty * worldWidth + tx])
                    {
                        if (tx < xMin) xMin = tx;
                        if (ty < yMin) yMin = ty;
                        if (tx > xMax) xMax = tx;
                        if (ty > yMax) yMax = ty;
                    }
                }
                if (xMax < 0)
                {
                    Debug.LogWarning("[BdbPmpWorldBuilder.BuildFromTclDriven] No templates loaded — nothing to render");
                    var empty = BuildResult(worldWidth, worldHeight, worldBlockIdx, worldZone, worldTerrain, worldCellId, worldHasTpl, bdb);
                    empty.Voxels = worldNavigationPlane;
                    empty.TemplateRecordWrites = worldTemplateRecordWrites;
                    // Still attach a chunk grid (bbox empty → builds nothing) so later live-mode
                    // growth stamps can extend the bbox and mesh incrementally.
                    if (ctx == null) empty.Chunks = new TerrainChunkGrid(root, bdb, empty, dataRoot);
                    return empty;
                }
                xMin = System.Math.Max(0, xMin - 4);
                yMin = System.Math.Max(0, yMin - 4);
                xMax = System.Math.Min(worldWidth - 1, xMax + 4);
                yMax = System.Math.Min(worldHeight - 1, yMax + 4);
            }
            Debug.Log($"[BdbPmpWorldBuilder.BuildFromTclDriven] render bbox X {xMin}..{xMax}, Y {yMin}..{yMax}");

            // NB: markers come via Tcl `new` + EditorEntitySpawner, NOT
            // SpawnTclMarkers — so we don't call it here.
            var result = BuildResult(worldWidth, worldHeight, worldBlockIdx, worldZone, worldTerrain, worldCellId, worldHasTpl, bdb);
            result.Voxels = worldNavigationPlane;
            result.TemplateRecordWrites = worldTemplateRecordWrites;
            result.RenderXMin = xMin; result.RenderYMin = yMin; result.RenderXMax = xMax; result.RenderYMax = yMax;
            if (ctx == null)
            {
                // Runtime path: chunked renderer — same per-tile geometry/material logic, but split
                // into fixed 32×32-tile chunks so a dig/growth stamp later remeshes only dirty chunks.
                result.Chunks = new TerrainChunkGrid(root, bdb, result, dataRoot);
                result.Chunks.RebuildDirty();   // first call sees the virgin bbox → builds every chunk
            }
            else
            {
                // Editor-importer path (asset ctx registration): keep the legacy monolithic build.
                RenderWorldArrays(root, ctx, bdb,
                    worldBlockIdx, worldZone, worldTerrain, worldCellId, worldWidth,
                    xMin, yMin, xMax, yMax, dataRoot);
            }
            return result;
        }

        // Full re-mesh of the terrain from the (mutated) world arrays. On chunked results this marks
        // every chunk dirty and rebuilds them all (identical output, shared textures/materials kept);
        // on legacy results it drops the whole TerrainMeshes container and re-runs the monolithic
        // renderer. The normal dig/growth path does not come through here any more — DiggableTerrain's
        // debounce rebuilds only the dirty chunks (TerrainChunkGrid.RebuildDirty). Kept as the
        // full-rebuild entry (EmitTerrain-era callers, parity tests, legacy results).
        public static void RemeshFromArrays(GameObject root, BdbModel bdb, TclWorldResult r, string dataRoot)
        {
            if (r.RenderXMax < r.RenderXMin) return;   // nothing was rendered
            if (r.Chunks != null)
            {
                r.Chunks.MarkAllDirty();
                r.Chunks.RebuildDirty();
                return;
            }
            // Drop all terrain meshes (the whole TerrainMeshes container) and rebuild from the mutated
            // arrays — carved tiles (block 59) are skipped on the re-bake, so the dug block disappears.
            var container = root.transform.Find("TerrainMeshes");
            if (container != null) UnityEngine.Object.DestroyImmediate(container.gameObject);
            RenderWorldArrays(root, null, bdb, r.BlockIdx, r.Zone, r.Terrain, r.CellId, r.Width,
                r.RenderXMin, r.RenderYMin, r.RenderXMax, r.RenderYMax, dataRoot, r.DugFloor);
        }

        // ===================== Chunked terrain renderer (dirty-rect remesh) =====================
        // The render bbox is partitioned into fixed 32×32-tile chunks aligned to the absolute tile
        // grid (chunk coord = tile >> 5), so bbox growth never re-bins existing tiles. Each chunk =
        // one "Chunk_{cx}_{cy}" GameObject under TerrainMeshes with one child mesh per scape bucket
        // present in it. A dig carve / growth stamp marks only the chunks its (±1-tile-margined)
        // rect intersects; the debounced rebuild re-emits only those — the rest of the world keeps
        // its meshes, colliders and (shared, cached) materials untouched.
        //
        // Seams: BuildTileBuckets computes every cross-tile attribute (corner weights, cellId corner
        // patterns, variant hashes) from the global arrays at global node coords, and adjacent tiles
        // never shared vertex indices even inside the old monolithic mesh (the dedupe key includes
        // the tile coord) — so per-chunk meshes produce bit-identical border vertices/normals and
        // the chunk borders are invisible, exactly like the old tile borders were.
        public sealed class TerrainChunkGrid
        {
            public const int ChunkTiles = 32;   // ~1k tiles/chunk → low-ms rebuild incl. collider cook

            // A reusable per-scape sub-object: one child GameObject with its Mesh + MeshFilter/
            // MeshRenderer/MeshCollider. Reused across rebuilds (mesh.Clear + refill, collider
            // re-cook) — created only when a chunk first gains that scape, destroyed only when it
            // loses it. This is what kills the DestroyImmediate + new-Mesh + new-GameObject churn
            // (and the synchronous collider re-cook is the only unavoidable cost left).
            public sealed class ChunkSub
            {
                public int ScapeId;
                public GameObject Go = null!;
                public Mesh Mesh = null!;
                public MeshFilter Mf = null!;
                public MeshRenderer Mr = null!;
                public MeshCollider Mc = null!;
            }

            public sealed class ChunkInfo
            {
                public int Cx, Cy;
                public GameObject? Go;                       // null = chunk currently holds no geometry
                // Reused per-scape sub-objects keyed by scapeId — their Mesh/GO/collider survive
                // rebuilds (only added/removed when the chunk's scape set changes).
                public readonly Dictionary<int, ChunkSub> Subs = new();
                // Flat list of the currently-active meshes, rebuilt (in Subs iteration order) each
                // pass. Kept for the remesh tests, which snapshot mesh instance IDs to prove reuse.
                public readonly List<Mesh> Meshes = new();
                public int Version;                          // bumped on every rebuild — test/debug bookkeeping
                public int VertexCount, TriangleCount;
                // Tile rect this chunk rendered last build (chunk ∩ render bbox at the time);
                // empty (RX1 < RX0) when never built / built empty. Drives bbox-growth re-clips.
                public int RX0, RY0, RX1 = -1, RY1 = -1;
            }

            readonly BdbModel _bdb;
            readonly TclWorldResult _r;
            readonly Transform _container;
            readonly TerrainTextureCache _tex;
            readonly Dictionary<long, ChunkInfo> _chunks = new();
            readonly HashSet<long> _dirty = new();
            // Pooled working state reused across every RebuildChunk (single-threaded; each call
            // fully consumes the buckets — SetVertices/etc. copy out — before the next reuses them).
            // These remove the per-rebuild List/Dictionary garbage that dominated the GC spike.
            readonly Dictionary<long, Bucket> _bucketPool = new();
            readonly List<long> _buildOrder = new();
            readonly HashSet<int> _presentScapes = new();
            readonly List<int> _retireScapes = new();
            // Render-bbox snapshot of the last RebuildDirty — lets bbox growth (ExtendRenderBbox)
            // re-clip only the chunks whose visible rect actually changed (incl. the union-corner
            // fill a monolithic remesh would have rendered).
            int _bx0 = int.MaxValue, _by0 = int.MaxValue, _bx1 = -1, _by1 = -1;

            public int RebuildCount { get; private set; }    // total chunkbuilds — perf bookkeeping
            public IReadOnlyDictionary<long, ChunkInfo> Chunks => _chunks;
            public IReadOnlyCollection<long> DirtyChunks => _dirty;

            // Opt-in frame amortization (default off — it changes when terrain appears, a visible
            // timing change): cap a big multi-chunk rebuild to MaxChunksPerRebuild nearest-camera
            // chunks per RebuildDirty and leave the rest dirty for later frames
            // (DiggableTerrain.Update drains via HasPendingRebuilds). The mesh/buffer reuse above
            // is behavior-identical and always on; only this spreads the pop-in.
            public bool StaggerRebuilds = false;
            public int MaxChunksPerRebuild = 4;
            // True only when a staggered RebuildDirty intentionally deferred chunks to later frames.
            // Deliberately not `_dirty.Count > 0` — that is also true between a carve's MarkTileDirty
            // and the debounced flush, and draining then would bypass DiggableTerrain's remesh
            // debounce. In the default config (StaggerRebuilds off) this is always false.
            bool _staggerLeftover;
            public bool HasPendingRebuilds => _staggerLeftover;

            public static long Key(int cx, int cy) => ((long)cy << 32) | (uint)cx;
            public static (int cx, int cy) ChunkOf(int tx, int ty) => (tx / ChunkTiles, ty / ChunkTiles);

            public TerrainChunkGrid(
                GameObject root,
                BdbModel bdb,
                TclWorldResult r,
                string dataRoot,
                bool resetSharedTextureCache = true)
            {
                _bdb = bdb;
                _r = r;
                // The cutout alpha-test predicate reads the native tile+0x18 state word.
                // Runtime results (BuildResult / save load) already carry it; hand-rolled
                // results (tests, tools) get the same terrain-byte synthesis DiggableTerrain
                // applies to legacy states — never a silent second predicate.
                if (_r.NativeTerrainFlags.Length != _r.BlockIdx.Length)
                    _r.NativeTerrainFlags = BuildNativeTerrainFlags(_r.Terrain, _r.Width, _r.Height, _r.HasTpl);
                _container = GetOrCreateTerrainContainer(root);
                // Textures + per-scape materials load once for the world's lifetime — the old
                // monolithic remesh re-read ~40 TGAs and re-allocated materials on every dig.
                // A transactional save candidate shares the active session's immutable source
                // textures. Clearing here would destroy Texture2D objects still referenced by the
                // active world's materials before candidate activation can succeed or roll back.
                if (resetSharedTextureCache) GroundTextureBlender.ClearCache();
                _tex = BuildTextureCache(dataRoot);
            }

            // Mark the chunks a changed tile affects. ±1-tile margin: the corner weights / UV1
            // slices of a tile sample the 4 tiles around each node, so a change can recolor the
            // shared corners of its 8 neighbours.
            public void MarkTileDirty(int tx, int ty) => MarkRectDirty(tx, ty, tx, ty);

            public void MarkRectDirty(int x0, int y0, int x1, int y1)
            {
                x0 = System.Math.Max(0, x0 - 1); y0 = System.Math.Max(0, y0 - 1);
                x1 = System.Math.Min(_r.Width - 1, x1 + 1); y1 = System.Math.Min(_r.Height - 1, y1 + 1);
                if (x1 < x0 || y1 < y0) return;
                for (int cy = y0 / ChunkTiles; cy <= y1 / ChunkTiles; cy++)
                    for (int cx = x0 / ChunkTiles; cx <= x1 / ChunkTiles; cx++)
                        _dirty.Add(Key(cx, cy));
            }

            // Full rebuild (RemeshFromArrays path): every chunk the render bbox touches.
            public void MarkAllDirty()
            {
                if (_r.RenderXMax < _r.RenderXMin) return;
                for (int cy = _r.RenderYMin / ChunkTiles; cy <= _r.RenderYMax / ChunkTiles; cy++)
                    for (int cx = _r.RenderXMin / ChunkTiles; cx <= _r.RenderXMax / ChunkTiles; cx++)
                        _dirty.Add(Key(cx, cy));
            }

            // Rebuild only the dirty chunks (plus any chunk whose clip changed because the render
            // bbox grew). Returns the number of chunks re-emitted. Called from DiggableTerrain's
            // debounced Update — this is the replacement for the full-map RemeshFromArrays hitch.
            public int RebuildDirty()
            {
                SyncBbox();
                if (_dirty.Count == 0) return 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Build order = the whole dirty set, unless staggering caps it to the K
                // nearest-camera chunks (the rest stay dirty for subsequent frames). Snapshotted
                // into _buildOrder so we can mutate _dirty while draining it.
                _buildOrder.Clear();
                _buildOrder.AddRange(_dirty);
                bool staggered = StaggerRebuilds && MaxChunksPerRebuild > 0
                                 && _buildOrder.Count > MaxChunksPerRebuild;
                if (staggered)
                {
                    var cam = Camera.main;
                    Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
                    _buildOrder.Sort((a, b) =>
                        ChunkCamDistSq(a, camPos).CompareTo(ChunkCamDistSq(b, camPos)));
                    _buildOrder.RemoveRange(MaxChunksPerRebuild, _buildOrder.Count - MaxChunksPerRebuild);
                }

                int n = 0; long verts = 0;
                foreach (var key in _buildOrder)
                {
                    bool built = RebuildChunk(key);
                    _dirty.Remove(key);
                    if (built) { n++; if (_chunks.TryGetValue(key, out var ci)) verts += ci.VertexCount; }
                }
                // Leftover only exists when we capped a staggered batch; non-staggered drains fully.
                _staggerLeftover = staggered && _dirty.Count > 0;
                sw.Stop();

                double ms = sw.Elapsed.TotalMilliseconds;
                if (n > 0)
                    Debug.Log($"[TerrainChunkGrid] rebuilt {n} chunk(s) of {_chunks.Count}" +
                              (staggered ? $" (staggered, {_dirty.Count} pending)" : "") +
                              $" in {ms:0.0} ms");
                if (ms > 40.0 && !Application.isBatchMode)
                    Debug.LogWarning($"[TerrainChunkGrid] SLOW terrain remesh: {n} chunk(s), {verts} verts, " +
                                     $"{ms:0.0} ms in one frame — enable StaggerRebuilds to amortize it");
                return n;
            }

            // Squared world distance from the camera to a chunk's centre (terrain-local vertex
            // formula: local x = tx, z = −ty). Only used to order a staggered batch nearest-first.
            float ChunkCamDistSq(long key, Vector3 camPos)
            {
                int cx = (int)(key & 0xFFFFFFFF);
                int cy = (int)(key >> 32);
                float lx = cx * ChunkTiles + ChunkTiles * 0.5f;
                float lz = -(cy * ChunkTiles + ChunkTiles * 0.5f);
                Vector3 world = _container.TransformPoint(new Vector3(lx, 0f, lz));
                return (world - camPos).sqrMagnitude;
            }

            // Bbox growth (growth stamps / live L2M): dirty every chunk in the new bbox whose
            // visible clip differs from what it last rendered. Covers freshly allocated chunks,
            // edge chunks that were clipped by the old bbox, and the union-corner default-earth
            // fill (bbox = union of rects ⊃ both rects) the monolithic remesh used to render.
            // Old chunks with an unchanged clip are not touched.
            void SyncBbox()
            {
                if (_r.RenderXMin == _bx0 && _r.RenderYMin == _by0 &&
                    _r.RenderXMax == _bx1 && _r.RenderYMax == _by1) return;
                if (_r.RenderXMax >= _r.RenderXMin)
                {
                    for (int cy = _r.RenderYMin / ChunkTiles; cy <= _r.RenderYMax / ChunkTiles; cy++)
                        for (int cx = _r.RenderXMin / ChunkTiles; cx <= _r.RenderXMax / ChunkTiles; cx++)
                        {
                            long key = Key(cx, cy);
                            var (x0, y0, x1, y1) = Clip(cx, cy);
                            if (!_chunks.TryGetValue(key, out var c)
                                || c.RX0 != x0 || c.RY0 != y0 || c.RX1 != x1 || c.RY1 != y1)
                                _dirty.Add(key);
                        }
                }
                _bx0 = _r.RenderXMin; _by0 = _r.RenderYMin; _bx1 = _r.RenderXMax; _by1 = _r.RenderYMax;
            }

            // The tile rect of chunk (cx,cy) clipped to the current render bbox (empty if disjoint).
            (int x0, int y0, int x1, int y1) Clip(int cx, int cy)
            {
                int x0 = System.Math.Max(cx * ChunkTiles, _r.RenderXMin);
                int y0 = System.Math.Max(cy * ChunkTiles, _r.RenderYMin);
                int x1 = System.Math.Min(cx * ChunkTiles + ChunkTiles - 1, _r.RenderXMax);
                int y1 = System.Math.Min(cy * ChunkTiles + ChunkTiles - 1, _r.RenderYMax);
                return (x0, y0, x1, y1);
            }

            bool RebuildChunk(long key)
            {
                int cx = (int)(key & 0xFFFFFFFF);
                int cy = (int)(key >> 32);
                var (x0, y0, x1, y1) = Clip(cx, cy);
                bool empty = x1 < x0 || y1 < y0 || _r.RenderXMax < _r.RenderXMin;
                _chunks.TryGetValue(key, out var info);
                if (info == null)
                {
                    if (empty) return false;          // nothing visible, nothing built — skip
                    info = new ChunkInfo { Cx = cx, Cy = cy };
                    _chunks[key] = info;
                }

                if (empty)
                {
                    // Nothing visible now — retire the whole chunk (meshes are assets: destroy them
                    // explicitly, then the parent GO with its child sub-objects).
                    TearDownChunk(info);
                    info.VertexCount = info.TriangleCount = 0;
                    info.RX0 = info.RY0 = 0; info.RX1 = info.RY1 = -1;
                    info.Version++;
                    return true;
                }

                // Emit the per-tile geometry into the pooled buckets (Clear+refill — no per-call
                // List/Dictionary garbage). Identical geometry to the old fresh-allocation path.
                var buckets = BuildTileBuckets(_bdb, _r.BlockIdx, _r.Terrain, _r.NativeTerrainFlags,
                    _r.CellId, _r.Width, x0, y0, x1, y1, _r.DugFloor, _bucketPool);

                info.Meshes.Clear();
                info.VertexCount = info.TriangleCount = 0;
                _presentScapes.Clear();
                foreach (var kv in buckets)
                {
                    var b = kv.Value;
                    if (b.Verts.Count == 0) continue;
                    _presentScapes.Add(b.ScapeId);
                    if (info.Go == null)
                    {
                        info.Go = new GameObject($"Chunk_{cx}_{cy}");
                        info.Go.transform.SetParent(_container, false);
                    }
                    // Reuse the per-scape sub-object if it already exists (mesh.Clear + refill,
                    // collider re-cook); only a new scape allocates a GO/Mesh/collider.
                    if (!info.Subs.TryGetValue(b.ScapeId, out var sub))
                    {
                        sub = CreateChunkSub(info.Go.transform, b.ScapeId, cx, cy);
                        info.Subs[b.ScapeId] = sub;
                    }
                    RefillSubmesh(sub, b, _tex.MaterialFor(b.ScapeId), $"TileMesh_c{cx}_{cy}_s{b.ScapeId}");
                    info.Meshes.Add(sub.Mesh);
                    info.VertexCount += b.Verts.Count;
                    info.TriangleCount += b.Tris.Count / 3;
                }

                // Retire sub-objects for scapes no longer present in this chunk (the only case that
                // destroys geometry on a normal rebuild — the scape set changed).
                if (info.Subs.Count > _presentScapes.Count)
                {
                    _retireScapes.Clear();
                    foreach (var kv in info.Subs)
                        if (!_presentScapes.Contains(kv.Key)) _retireScapes.Add(kv.Key);
                    foreach (var sid in _retireScapes)
                    {
                        var sub = info.Subs[sid];
                        info.Subs.Remove(sid);
                        if (sub.Mesh != null) UnityEngine.Object.DestroyImmediate(sub.Mesh);
                        if (sub.Go != null) UnityEngine.Object.DestroyImmediate(sub.Go);
                    }
                }

                // A non-empty clip that produced no geometry (all air/oob) drops its parent GO —
                // matches the old "no bucket → no Chunk_cx_cy GO" scene graph.
                if (info.Meshes.Count == 0 && info.Go != null)
                {
                    UnityEngine.Object.DestroyImmediate(info.Go);
                    info.Go = null;
                }

                info.RX0 = x0; info.RY0 = y0; info.RX1 = x1; info.RY1 = y1;
                info.Version++;
                RebuildCount++;
                return true;
            }

            // Create a reusable per-scape child sub-object (GO + Mesh + MeshFilter/Renderer/Collider).
            // Name/components match the old BuildGroundSubmesh exactly, but the objects now persist.
            ChunkSub CreateChunkSub(Transform parent, int scapeId, int cx, int cy)
            {
                var go = new GameObject($"Ground scape {scapeId} (3-mat blend)");
                go.transform.SetParent(parent, false);
                var mesh = new Mesh { name = $"TileMesh_c{cx}_{cy}_s{scapeId}" };
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                return new ChunkSub { ScapeId = scapeId, Go = go, Mesh = mesh, Mf = mf, Mr = mr, Mc = mc };
            }

            // Refill an existing sub-object's mesh in place (reuses the Mesh's native buffers) and
            // re-cook its collider. Produces byte-identical geometry/collision to a fresh mesh.
            static void RefillSubmesh(ChunkSub sub, Bucket b, Material mat, string meshKey)
            {
                var mesh = sub.Mesh;
                var wantFormat = b.Verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.Clear();
                if (mesh.indexFormat != wantFormat) mesh.indexFormat = wantFormat;
                mesh.name = meshKey;
                mesh.SetVertices(b.Verts);
                mesh.SetUVs(0, b.Uvs);
                if (b.Uvs1.Count == b.Verts.Count) mesh.SetUVs(1, b.Uvs1);
                mesh.SetColors(b.Colors);
                mesh.SetTriangles(b.Tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                sub.Mr.sharedMaterial = mat;
                // Re-cook the collider for the new geometry. Null-then-assign forces PhysX to drop
                // the stale cooked data and bake the fresh mesh (a same-reference re-assign can be a
                // no-op). The cook is synchronous — unavoidable — but no GO/component churn.
                sub.Mc.sharedMesh = null;
                sub.Mc.sharedMesh = mesh;
            }

            // Destroy every mesh (assets) then the parent GO (with its child sub-objects), and reset
            // the reuse bookkeeping. Used when a chunk becomes fully empty.
            static void TearDownChunk(ChunkInfo info)
            {
                foreach (var kv in info.Subs)
                    if (kv.Value.Mesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.Mesh);
                info.Subs.Clear();
                info.Meshes.Clear();
                if (info.Go != null) { UnityEngine.Object.DestroyImmediate(info.Go); info.Go = null; }
            }
        }

        static TclWorldResult BuildResult(int w, int h, uint[] blk, byte[] zone, byte[] terrain, ushort[] cell, System.Collections.BitArray hasTpl, BdbModel bdb)
        {
            return new TclWorldResult
            {
                Width = w, Height = h,
                MinX = 0, MinY = 0, MaxXExclusive = w, MaxYExclusive = h,
                BlockIdx = blk, Zone = zone, Terrain = terrain, CellId = cell,
                NativeTerrainFlags = BuildNativeTerrainFlags(terrain, w, h, hasTpl),
                HasTpl = hasTpl,
            };
        }

        private static ushort[] BuildNativeTerrainFlags(
            byte[] terrain,
            int width,
            int height,
            System.Collections.BitArray hasTemplate)
        {
            _ = hasTemplate;   // template presence lives in HasTpl/templateFlags, not here
            var result = new ushort[terrain.Length];
            for (int index = 0; index < result.Length; index++)
            {
                int material = MaterialTypeAtTile(
                    terrain, width, height, index % width, index / width);
                // Native tile+0x18: bits 0-1 material, bit 7 authored force-wall, high byte
                // appearance noise. Stamp blit copies PMP terrain bit 7 into +0x18
                // (FUN_005992b0:315,339-377); it is data, not a synthetic "no template" marker.
                result[index] = unchecked((ushort)(
                    material | (terrain[index] & 0x80) |
                    (TileAppearanceNoise(index % width, index / width) << 8)));
            }
            return result;
        }

        // Stable per-tile stand-in for the native generator's MT19937 appearance draw: the
        // value is cosmetic (texture variation hash — never consumed by nav/dig), but a fresh
        // save must carry a native-shaped nonzero spread, and captures must see the same value
        // on every pass (a live field must not drift between saves).
        public static int TileAppearanceNoise(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ 0x2545F491u;
                h ^= h >> 13;
                h *= 0x5bd1e995u;
                return (int)((h >> 18 ^ h) & 0xFF);
            }
        }

        public static int MaterialTypeFromTerrainByte(byte value)
        {
            return (value & 0x7F) switch
            {
                4 => 0,
                3 => 1,
                5 => 3,
                _ => 2,
            };
        }

        // The native loader's material paint for one tile: a 0-byte tile borrows the first
        // nonzero of {below, right, below-right} before the byte→type switch
        // (FUN_005992b0:293-304). Flag synthesis must run this probe or 0-byte tiles beside
        // granite would read earth where the native tile+0x18 says stone.
        public static int MaterialTypeAtTile(byte[] terrain, int width, int height, int x, int y)
        {
            byte v = (byte)(terrain[y * width + x] & 0x7F);
            if (v == 0 && y + 1 < height)
                v = (byte)(terrain[(y + 1) * width + x] & 0x7F);
            if (v == 0 && x + 1 < width)
                v = (byte)(terrain[y * width + x + 1] & 0x7F);
            if (v == 0 && x + 1 < width && y + 1 < height)
                v = (byte)(terrain[(y + 1) * width + x + 1] & 0x7F);
            return MaterialTypeFromTerrainByte(v);
        }

        // Re-derive the material band of NativeTerrainFlags from the terrain bytes over a rect
        // (growth stamps / carve-time byte writes). The probe window reaches one tile up-left,
        // so the touched rect is widened accordingly; flag bits above the band are preserved.
        public static void SyncNativeTerrainMaterials(
            TclWorldResult result,
            int x0,
            int y0,
            int x1,
            int y1)
        {
            if (result.NativeTerrainFlags.Length != result.Width * result.Height) return;
            x0 = System.Math.Max(0, x0 - 1);
            y0 = System.Math.Max(0, y0 - 1);
            x1 = System.Math.Min(result.Width - 1, x1);
            y1 = System.Math.Min(result.Height - 1, y1);
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int index = y * result.Width + x;
                int material = MaterialTypeAtTile(
                    result.Terrain, result.Width, result.Height, x, y);
                result.NativeTerrainFlags[index] = unchecked((ushort)(
                    result.NativeTerrainFlags[index] & ~3 | material));
            }
        }

        public static void BlitL2mIntoWorld(
            L2mMap l2m, int wx, int wy, int dstW, int dstH, ushort[] dst)
        {
            for (int py = 0; py < l2m.Height; py++)
            {
                int dy = wy + py;
                if (dy < 0 || dy >= dstH) continue;
                for (int px = 0; px < l2m.Width; px++)
                {
                    int dx = wx + px;
                    if (dx < 0 || dx >= dstW) continue;
                    var c = l2m.CellAt(px, py);
                    if (c != 0) dst[dy * dstW + dx] = c;
                }
            }
        }

        // Native transparency law (map_template blit FUN_005992b0:292): a source tile whose 2×2
        // terrain-corner window (this tile, +x, +y, +x+y — out-of-range corners ignored, matching
        // the:248/:265/:278 bounds guards) is all zero (&0x7f) skips the per-tile
        // material/block/height record writes, so those pre-existing world fields survive. The
        // independent HasTpl and +0x3c whole-rectangle passes still cover it. 34 shipped template
        // PMPs carry such filler tiles (all block 0 in the data), e.g. the Urwald shaft pieces
        // gng_029/030/032 — blitting their block-0 filler verbatim walled over whatever the world
        // had there.
        // ---- sm_draw_stone painter — FUN_005fc9d0 (the StoryMgr's probabilistic stone-class
        // rect painter; `sm_draw_stone -border` = FUN_0065e090:930-941 issues three of these).
        //
        // The native writes the tile's stone class (tile+0x18 low 2 bits: 0=granite/undiggable,
        // 1=sand, 2=earth, 3=fully open — every dig/marker gate requires ∈{1,2}). The port's
        // canonical representation of that field is the raw terrain byte in Terrain[]
        // (DiggableTerrain.TileMaterialType: byte 4→class 0, 3→1, 2→2, 5→3, 0/1→2 the
        // map-create default per FUN_00598f10:154) — so the painter writes the byte that maps
        // to the requested class. Verbatim from the decompile:
        // • rect clamped to the map (:28-38); requested class 1↔2 swapped (:40-46);
        // • strength' = strength / (min(w,h) · 0.2) (:54, DAT_00737054 = 0.2);
        // • per tile, accept probability p starts at 1.0 (:58, DAT_00737138) and loses a
        // quadratic falloff past the band midpoint along each flagged direction
        // (:96-114): flag 4 fades x > midX, flag 8 fades x < midX, flag 1 fades y > midY,
        // flag 2 fades y < midY — p −= ((d/span)·d/span)/strength';
        // • only tiles whose current class is 1 or 2 repaint (:115-117 — 0 and 3 survive);
        // • accept iff (rand&0x7fff) <= p·32768 (:118-120, _DAT_007376dc = 32768);
        // • loop excludes the last row/column (:56/:62 `< limit-1`) — a native quirk kept.
        // skipTiles is the port's delayed-paint ordering mask. It skips only tiles whose PMP record
        // was actually written. HasTpl cannot be used here: native-transparent padding is covered
        // by tile+0x13 (:461-529) but skips +0x18 (:292-454), so the earlier paint survives.
        public static void DrawStoneIntoWorld(
            byte[] terrain, System.Collections.BitArray? skipTiles, int worldW, int worldH,
            int x0, int y0, int x1, int y1, uint dirFlags, int matClass, float strength,
            System.Random rng)
        {
            x0 = System.Math.Max(x0, 0); y0 = System.Math.Max(y0, 0);           //:28-38
            x1 = System.Math.Min(x1, worldW); y1 = System.Math.Min(y1, worldH);
            if (matClass == 1) matClass = 2; else if (matClass == 2) matClass = 1;   //:40-46
            byte outByte = matClass switch { 0 => (byte)4, 1 => (byte)3, 2 => (byte)2, _ => (byte)5 };
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return;
            float strengthP = strength / (System.Math.Min(w, h) * 0.2f);          //:54
            int midX = w / 2 + x0, midY = h / 2 + y0;                              //:93-94
            for (int x = x0; x < x1 - 1; x++)                                      //:56
                for (int y = y0; y < y1 - 1; y++)                                  //:62
                {
                    float p = 1.0f;                                                //:95
                    if ((dirFlags & 4) != 0 && midX < x)                           //:96-99
                        p -= (((midX - x) / (float)w) * (midX - x)) / w / strengthP;
                    if ((dirFlags & 8) != 0 && x < midX)                           //:100-104
                        p -= (((midX - x) / (float)w) * (midX - x)) / w / strengthP;
                    if ((dirFlags & 1) != 0 && midY < y)                           //:19
                        p -= (((midY - y) / (float)h) * (midY - y)) / h / strengthP;
                    if ((dirFlags & 2) != 0 && y < midY)                           //:110-114
                        p -= (((midY - y) / (float)h) * (midY - y)) / h / strengthP;
                    int idx = y * worldW + x;
                    if (skipTiles != null && idx < skipTiles.Length && skipTiles[idx]) continue;
                    int cur = MaterialTypeFromTerrainByte(terrain[idx]);
                    if (cur == 0 || cur == 3) continue;                            //:115-117
                    if ((rng.Next() & 0x7fff) <= p * 32768f)                       //:118-120
                        terrain[idx] = outByte;                                    //:121
                }
        }

        public static bool PmpTileTransparent(PmpMap pmp, int px, int py)
        {
            for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    int xx = px + dx, yy = py + dy;
                    if (xx >= pmp.Width || yy >= pmp.Height) continue;
                    if ((pmp.TerrainAt(xx, yy) & 0x7f) != 0) return false;
                }
            return true;
        }

        public static void BlitPmpIntoWorld(
            PmpMap pmp, int wx, int wy, int dstW, int dstH,
            uint[] dstBlock, byte[] dstZone, byte[] dstTerrain,
            System.Collections.BitArray dstHasTpl, byte[]? dstNavigationPlane = null,
            System.Action<int, int>? onTileStamped = null)
        {
            int pw = (int)pmp.Width;
            for (int py = 0; py < pmp.Height; py++)
            {
                int dy = wy + py;
                if (dy < 0 || dy >= dstH) continue;
                for (int px = 0; px < pmp.Width; px++)
                {
                    int dx = wx + px;
                    if (dx < 0 || dx >= dstW) continue;
                    // Template wins (native law, FUN_005992b0): the blit is destination-blind —
                    // its only dest read is the terrain word for the source-code-1 "inherit class"
                    // remap (:128-129/:162-173). It never tests dug state (+0x08 nibbles), block
                    // 59, or +0x13 before writing: a growth stamp overwrites player-dug tiles
                    // wholesale (block:379-415, terrain:317-377, height:453), erasing the carve.
                    // Junction coherence is the anchor's job (the dig-direction start piece lands
                    // door-lattice-aligned on the breakthrough), not a blit-merge concern. A prior
                    // "carve wins" skipTile approach manufactured template-block-next-to-carved-
                    // block seams inside the stamp footprint — a state the native can never reach.
                    // Dig marks survive a stamp (+0x08/+0x0c are never touched by the native blit),
                    // so the dwarf keeps digging through the freshly stamped template mouth.
                    int di = dy * dstW + dx;
                    if (dstNavigationPlane != null)
                    {
                        // Authored +0x3c is a whole-stamp seed plane, not part of the transparent-tile
                        // record write: FUN_005992b0:292-454 gates block/zone/tile+0x18 per tile, then
                        //:553-568 independently hands the complete expanded plane to FUN_00596550,
                        // whose:31-141 loops the full clipped rectangle. So transparent seam tiles
                        // keep their world tile records but still receive the stamp seed before
                        // validation — this copy must run before the transparency `continue` below,
                        // or RLE seed cells get erased on live template blits.
                        //
                        // De-raster the PMP-authored navigation plane into tile+0x3c layout. The PMP
                        // trailer RLE expands to a sub-cell raster buffer — row-major across the whole
                        // pmp: byte index (ty*8+subY)*(pmpW*8) + (tx*8+subX) — not 64 contiguous bytes
                        // per tile (matches FUN_00596550: src = globalSubRow*regionSubWidth +
                        // globalSubCol; dst = tile+0x3c + subY*8+subX). A naive per-tile 64-byte copy
                        // scrambles the walkable shelves across sub-rows. Dst sub-index = subY*8 + subX
                        // (matches tile+0x3c / get_hmap / classifier). See Docs/ORIGINAL_PATHFINDING_RE.md §1.5.
                        int subRowStride = pw * PmpMap.SubGridSide;        // raster row width = pmpW * 8
                        int dstBase = di * PmpMap.SubGridLength;
                        int pxs = px * PmpMap.SubGridSide, pys = py * PmpMap.SubGridSide;
                        for (int sy = 0; sy < PmpMap.SubGridSide; sy++)
                        for (int sx = 0; sx < PmpMap.SubGridSide; sx++)
                        {
                            int srcK = (pys + sy) * subRowStride + (pxs + sx);
                            if ((uint)srcK < (uint)pmp.AuthoredNavigationPlane.Length
                                && dstBase + (sy << 3) + sx < dstNavigationPlane.Length)
                            {
                                dstNavigationPlane[dstBase + (sy << 3) + sx] =
                                    pmp.AuthoredNavigationPlane[srcK];
                            }
                        }
                    }
                    // Native tile+0x13 coverage is a separate clipped-rectangle pass after the
                    // record loop (FUN_005992b0:461-529), so it includes transparent padding too.
                    // It is generator occupancy metadata, never an EFFPASSWALK predicate.
                    dstHasTpl[di] = true;
                    // Native transparency (FUN_005992b0:292-454): an all-zero terrain window
                    // leaves block/zone/tile+0x18 untouched. It does not suppress the independent
                    // full-plane +0x3c copy above (FUN_005992b0:553-568; FUN_00596550:31-141).
                    if (PmpTileTransparent(pmp, px, py)) continue;
                    dstBlock[di] = pmp.BlockIdxAt(px, py);
                    dstZone[di] = pmp.ZoneAt(px, py);
                    dstTerrain[di] = pmp.TerrainAt(px, py);
                    // Live-stamp per-tile state sync (DiggableTerrain.SyncStampedTile): only tiles
                    // the blit actually wrote — native-transparent tiles keep their dig
                    // state/heights. (The old rect-wide refresh skipped every tile whose
                    // block the stamp changed — it couldn't tell a blit write from a
                    // carve — so stamped tiles never got their height/dug-floor state synced.)
                    onTileStamped?.Invoke(dx, dy);
                }
            }
        }

        /// <summary>
        /// One mesh bucket per unique tile texture signature. A signature is the combination
        /// (base_letter, overlay_letter, 4-corner-pattern). Tiles with the same signature
        /// share one baked texture and end up in the same Unity submesh.
        /// </summary>
        private sealed class Bucket
        {
            public readonly List<Vector3> Verts = new();
            public readonly List<Vector2> Uvs  = new();
            public readonly List<Vector2> Uvs1 = new();   //.x = variant slice index
            public readonly List<Color> Colors = new();
            public readonly List<int> Tris = new();
            public readonly Dictionary<long, int> Dedupe = new();
            public string Name = "";
            public Color FallbackColor = Color.gray;
            public int ScapeId;   // textures/material come from TerrainTextureCache.MaterialFor(ScapeId)

            // Clear the working buffers for reuse across rebuilds (pooled by TerrainChunkGrid),
            // keeping each backing array's capacity so refills don't re-grow from empty. Identity
            // fields (Name/FallbackColor/ScapeId) are keyed by ScapeId and stay valid.
            public void Reset()
            {
                Verts.Clear(); Uvs.Clear(); Uvs1.Clear(); Colors.Clear(); Tris.Clear(); Dedupe.Clear();
            }
        }

        // Shared terrain textures + per-scape materials. Built once per world (TerrainChunkGrid holds
        // one for its whole lifetime) — the old monolithic remesh re-loaded ~40 TGAs into fresh
        // Texture2DArrays and allocated new Materials on every dig, which was a large part of the
        // Play hitch (and leaked the old ones). Visuals are identical: same files, same uniforms.
        public sealed class TerrainTextureCache
        {
            public Texture2DArray? StoneArr;
            public Texture2DArray? EarthArr;
            public readonly Dictionary<int, Texture2DArray?> ScapeDetail = new();
            readonly Dictionary<int, Material> _mats = new();

            public Material MaterialFor(int scapeId)
            {
                if (_mats.TryGetValue(scapeId, out var m)) return m;
                var shader = DigglesShaders.Ground
                    ?? throw new System.InvalidOperationException("No Diggles/Ground or URP shader");
                m = new Material(shader) { name = $"TerrainMat_scape{scapeId}" };
                var detail = (ScapeDetail.TryGetValue(scapeId, out var d) && d != null)
                    ? d
                    : (ScapeDetail.TryGetValue(0, out var d0) ? d0 : null);
                if (StoneArr != null) m.SetTexture("_StoneArr", StoneArr);
                if (EarthArr != null) m.SetTexture("_EarthArr", EarthArr);
                if (detail   != null) m.SetTexture("_MossArr",  detail);
                if (m.HasProperty("_Brightness")) m.SetFloat("_Brightness", 1.0f);
                // urw (0) = identity (forest unchanged). Colored scapes: a visibility floor (<1 so
                // the base still shows through — not opaque), a gentle hue assertion, and contrast=1
                // (no crush → keeps texture detail). Same values the per-bucket materials carried.
                if (m.HasProperty("_DetailFloor"))
                    m.SetFloat("_DetailFloor", scapeId == 0 ? 0f : (scapeId == 3 ? 0.70f : 0.60f));
                if (m.HasProperty("_DetailSaturate"))
                    m.SetFloat("_DetailSaturate", scapeId == 0 ? 0f : (scapeId == 3 ? 0.55f : 0.50f));
                if (m.HasProperty("_DetailContrast")) m.SetFloat("_DetailContrast", 1f);
                _mats[scapeId] = m;
                return m;
            }
        }

        // Loads the shared ground texture arrays (stone/earth variants + the 4 per-scape detail
        // families). This is the expensive disk-bound part of a terrain build.
        public static TerrainTextureCache BuildTextureCache(string dataRoot)
        {
            var cache = new TerrainTextureCache
            {
                StoneArr = BuildVariantArray(dataRoot, "Gnd_A{0:D2}.tga", 15),
                EarthArr = BuildVariantArray(dataRoot, "Gnd_C{0:D2}.tga", 15),
            };
            // Per-tile "scape" detail family from the L2M cellId (bits 5..7): 0=urw (forest/moss),
            // 1=swf (sulfur), 2=kris (crystal), 3=lava. Each has its own Ground/detail/<prefix><PPPP> set.
            cache.ScapeDetail[0] = BuildMossArray(dataRoot, 16, "urw_");
            cache.ScapeDetail[1] = BuildMossArray(dataRoot, 16, "swf_");
            cache.ScapeDetail[2] = BuildMossArray(dataRoot, 16, "kris_");
            cache.ScapeDetail[3] = BuildMossArray(dataRoot, 16, "lava_");
            return cache;
        }

        // 4-corner code as a packed int: NW=bit24, NE=bit16, SW=bit8, SE=bit0 (ascii char).
        private static int PackCorners(char nw, char ne, char sw, char se) =>
            (nw << 24) | (ne << 16) | (sw << 8) | se;

        /// <summary>
        /// Maps a PMP terrain_type byte to a base ground letter (A/B/C).
        /// Avg RGB sampled from Gnd_X01.tga:
        /// A01 = (92, 86, 75) — dark olive → grass-ish
        /// B01 = (97, 76, 45) — brown → dirt / earth (default)
        /// C01 = (148, 124, 99) — light tan → stone / rock
        /// </summary>
        private static char TerrainToBaseLetter(byte terrain)
        {
            byte t = (byte)(terrain & 0x7F);
            if (t == 0x05) return 'A'; // 0x85 = grass meadow
            if (t == 0x04) return 'C'; // 0x84 = stone wall (light-tan)
            return 'B';                 // dirt / default earth (brown)
        }

        private static char OverlayLetterFor(char baseLetter)
        {
            // Default secondary material for blends — looks fine until BlockType-driven
            // overlay selection is RE'd. Grass blends with dirt, dirt with stone, stone with dirt.
            return baseLetter switch
            {
                'A' => 'B',  // grass on dirt
                'B' => 'C',  // dirt on stone
                _ => 'B',    // stone on dirt
            };
        }

        // Legacy monolithic build — one mesh per scape bucket over the whole bbox. Kept for the
        // editor importer (asset-ctx registration) and for full rebuilds of un-chunked results.
        // Runtime worlds render through TerrainChunkGrid instead, which shares BuildTileBuckets —
        // identical per-tile geometry either way.
        public static void RenderWorldArrays(
            GameObject root, System.Action<string, UnityEngine.Object>? ctx, BdbModel bdb,
            uint[] worldBlockIdx, byte[] worldZone, byte[] worldTerrain, ushort[] worldCellId, int worldW,
            int xMin, int yMin, int xMax, int yMax, string dataRoot, bool[]? dugFloor = null,
            ushort[]? nativeTerrainFlags = null)
        {
            // Wipe blender cache from the previous build so stale Texture2Ds don't leak.
            GroundTextureBlender.ClearCache();
            var tex = BuildTextureCache(dataRoot);
            // Callers without a live tile+0x18 word (editor importer previews) get the same
            // terrain-byte synthesis the runtime uses for legacy results.
            ushort[] nodeFlags = nativeTerrainFlags != null && nativeTerrainFlags.Length == worldBlockIdx.Length
                ? nativeTerrainFlags
                : BuildNativeTerrainFlags(worldTerrain, worldW, worldBlockIdx.Length / worldW, null);
            var buckets = BuildTileBuckets(bdb, worldBlockIdx, worldTerrain, nodeFlags, worldCellId, worldW,
                xMin, yMin, xMax, yMax, dugFloor);
            Debug.Log($"[BdbPmpWorldBuilder] unique tile signatures: {buckets.Count}");

            var container = GetOrCreateTerrainContainer(root);
            int meshNo = 0;
            foreach (var kv in buckets)
            {
                var b = kv.Value;
                if (b.Verts.Count == 0) continue;
                var meshKey = $"TileMesh_{meshNo:D3}";
                var mat = tex.MaterialFor(b.ScapeId);
                var mesh = BuildGroundSubmesh(container, b, mat, meshKey);
                if (ctx != null)
                {
                    ctx.Invoke(meshKey, mesh);
                    ctx.Invoke($"TileMat_{meshNo:D3}_sub{kv.Key}", mat);
                }
                meshNo++;
            }
        }

        // Parent terrain meshes under a single "TerrainMeshes" container so a full re-mesh can
        // reliably drop all of them at once (and the chunk grid has a stable parent).
        private static Transform GetOrCreateTerrainContainer(GameObject root)
        {
            var container = root.transform.Find("TerrainMeshes");
            if (container == null)
            {
                container = new GameObject("TerrainMeshes").transform;
                container.SetParent(root.transform, false);
            }
            return container;
        }

        // The per-tile geometry pass — shared verbatim by the legacy monolithic build and the
        // chunked renderer. Walks tiles [xMin..xMax]×[yMin..yMax] and emits all three co-rendered
        // BDB submeshes of the tile's single block_idx into per-scape Buckets. They are geometry/
        // material bands, not stacked terrain states. All cross-tile inputs (corner weights, cellId
        // corner patterns, variant hashes) are computed from the global world arrays at global node
        // coordinates, so two adjacent tiles produce bit-identical shared-corner attributes no
        // matter which call (or chunk) emits them — chunk borders are seamless by construction.
        // (Adjacent tiles never share vertex indices anyway — the dedupe key includes the tile
        // coord — so splitting tiles across meshes changes neither normals nor blending.)
        // Reusable scratch for the per-cell palette/normal index copy — single-threaded and
        // transient within BuildTileBuckets (written then read per cell), so one shared buffer
        // avoids the per-rebuild ~12 KB allocation.
        static ushort[]? _palIdxScratch;
        static byte[]? _normIdxScratch;

        private static Dictionary<long, Bucket> BuildTileBuckets(
            BdbModel bdb, uint[] worldBlockIdx, byte[] worldTerrain, ushort[] worldNodeFlags,
            ushort[] worldCellId, int worldW,
            int xMin, int yMin, int xMax, int yMax, bool[]? dugFloor,
            Dictionary<long, Bucket>? pool = null)
        {
            // 3-material weighted blend: one material per sub-mesh with three textures bound
            // (stone, earth, moss); per-vertex RGB carries the blend weights. Adjacent tiles share
            // corner vertices, so weights computed from the 4 tiles meeting at a corner give
            // seamless transitions without replicating the original engine's Msk_XXXX01 system.
            //
            // RE finding: D3DMgr slot 4 (scape_urw_mt0) is one 64×64 surface whose contents change
            // per-tile via DDraw Blit from urw_<NNNN>.tga — the handle stays the same, only the
            // backing pixels change. Ported by binding the matching urw_NNNN.tga-derived atlas
            // slice per tile; the atlases (16 corner patterns each, 4×4 grid, built offline by
            // tools/build_atlas.py) live in Assets/Diggles/Runtime/Textures/, loaded from an
            // absolute path outside dataRoot (Diggles' own Ground/ files stay at dataRoot).
            //
            // One bucket for everything — cliffs share floor textures so there is no visible
            // junction between a diggable wall and diggable floor. (Textures/materials are not
            // resolved here — TerrainTextureCache.MaterialFor(scapeId) binds the per-scape arrays
            // at mesh-build time, shared across chunks/rebuilds.) Buckets are pooled across
            // rebuilds when the caller passes its grid pool (Reset+refill each pass, keeping
            // backing capacity — no per-call Dictionary/List garbage); a fresh dict is used for the
            // one-shot monolithic path. Pooled buckets are keyed by scapeId, so their identity
            // fields (Name/ScapeId) stay valid across reuse; only the working buffers are cleared.
            var buckets = pool ?? new Dictionary<long, Bucket>();
            if (pool != null)
                foreach (var kv in buckets) kv.Value.Reset();
            Bucket GetBucket(int scapeId)
            {
                long key = scapeId;  // one bucket per scape (urw/swf/kris/lava) — only the detail texture varies
                if (buckets.TryGetValue(key, out var b)) return b;
                b = new Bucket {
                    Name = $"Ground scape {scapeId} (3-mat blend)",
                    FallbackColor = new Color(0.45f, 0.35f, 0.22f),
                    ScapeId = scapeId,
                };
                buckets[key] = b;
                return b;
            }

            int MaterialClassFor(byte terrain)
            {
                byte t = (byte)(terrain & 0x7F);
                if (t == 0x05) return 2;   // grass meadow → moss (B weight)
                if (t == 0x04) return 0;   // stone (R weight)
                return 1;                   // dirt / default earth (G weight)
            }
            // Per-corner weights for the 3-tex blend (R=stone, G=earth, B=moss).
            //
            // Stone/earth weights from material class of the 4 tiles around node.
            // Moss weight is more nuanced:
            // * grass-meadow neighbour → full moss contribution
            // * cellId-flagged corner on a non-grass tile → partial moss
            // (lets cliff/wall tiles get moss drips at their tagged corners)
            //
            // A corner node is "open" when its native tile+0x18 word carries state 3 — the
            // authored backdrop-hole class (the blitter writes low2=3 for file terrain byte 5
            // only; digging never writes the word; the.sav stores it verbatim). The predicate
            // must be the state, not blockIdx==59: the cutout ring lies on solid blocks (71 &
            // relief) whose nodes still carry state 3 — the block-59 predicate missed them,
            // leaving the back-wall strip fully lit (a visible ring artifact). Coordinates clamp into the map like the
            // native node accessor FUN_00592f10. Adjacent cells evaluate the same nodes →
            // identical alphas at shared corners → a seamless cut isoline.
            int worldH = worldBlockIdx.Length / worldW;
            bool OpenNode(int nx, int ny)
            {
                if (nx < 0) nx = 0; else if (nx >= worldW) nx = worldW - 1;
                if (ny < 0) ny = 0; else if (ny >= worldH) ny = worldH - 1;
                int ni = ny * worldW + nx;
                if ((uint)ni >= (uint)worldNodeFlags.Length) return false;
                return (worldNodeFlags[ni] & 3) == 3;
            }

            // Adjacent tiles share the same 4-tile set at their shared corner →
            // identical weights → seamless transitions.
            Color CornerWeights(int cx, int cy)
            {
                int wh = worldBlockIdx.Length / worldW;
                float ws = 0f, we = 0f, wm = 0f, cnt = 0f;
                for (int dy = -1; dy <= 0; dy++)
                for (int dx = -1; dx <= 0; dx++)
                {
                    int tx2 = cx + dx, ty2 = cy + dy;
                    if ((uint)tx2 >= worldW || (uint)ty2 >= wh) continue;
                    int idx = ty2 * worldW + tx2;
                    byte t = worldTerrain[idx];
                    int m = MaterialClassFor(t);
                    if (m == 0)      ws += 1f;                     // stone
                    else if (m == 1) we += 1f;                     // earth
                    else { wm += 1f; ws += 0f; we += 0f; }         // grass → moss
                    // Add extra moss contribution from cellId bit at this corner.
                    // Corner-of-tile encoding: dx=-1,dy=-1 → SE corner → bits 8-9, etc.
                    ushort cell = worldCellId[idx];
                    int shift = (dx == -1 && dy == -1) ?  8 :
                                (dx ==  0 && dy == -1) ? 10 :
                                (dx == -1 && dy ==  0) ? 12 :
                                                          14 ;
                    if (((cell >> shift) & 3) != 0) wm += 0.5f;
                    cnt += 1f;
                }
                if (cnt < 0.01f) return new Color(0f, 1f, 0f, 1f);
                return new Color(ws / cnt, we / cnt, wm / cnt, 1f);
            }
            int VariantFor(int tx, int ty)
            {
                unchecked {
                    int h = tx * 73856093 ^ ty * 19349663;
                    h ^= h >> 13;
                    return ((h & 0x7FFFFFFF) % 15) + 1;
                }
            }
            // Corner pattern from L2M cellId bits 8..15 (4 corners × 2 bits each).
            // Each pair is 0..2 (pair-encoded). Pattern bit = 1 if corner value > 0.
            int CornerPatternFromCellId(ushort cellId)
            {
                int nw = ((cellId >> 14) & 3) != 0 ? 1 : 0;
                int ne = ((cellId >> 12) & 3) != 0 ? 1 : 0;
                int sw = ((cellId >> 10) & 3) != 0 ? 1 : 0;
                int se = ((cellId >>  8) & 3) != 0 ? 1 : 0;
                return (nw << 3) | (ne << 2) | (sw << 1) | se;
            }


            const int MaxVerts = 4096;
            var palIdxBuf = _palIdxScratch ??= new ushort[MaxVerts];
            var normIdxBuf = _normIdxScratch ??= new byte[MaxVerts];

            int rendered = 0, airSkipped = 0, oob = 0;

            for (int ty = yMin; ty <= yMax; ty++)
            {
                for (int tx = xMin; tx <= xMax; tx++)
                {
                    int i = ty * worldW + tx;
                    uint blockIdx = worldBlockIdx[i];
                    byte terrain = worldTerrain[i];
                    ushort cellId = worldCellId[i];

                    if (blockIdx >= bdb.Header.NBlocks) { oob++; continue; }
                    bool openNw = OpenNode(tx,     ty    );
                    bool openNe = OpenNode(tx + 1, ty    );
                    bool openSw = OpenNode(tx,     ty + 1);
                    bool openSe = OpenNode(tx + 1, ty + 1);
                    // Native FUN_004ca830:291 — a cell whose four corner nodes all carry state 3
                    // emits nothing, whatever its block: the authored backdrop hole (the display
                    // gate byte +0x40a is ctor-constant 1, FUN_004c4420:91).
                    if (openNw && openNe && openSw && openSe) { airSkipped++; continue; }
                    // Skip block 59 only when it's load-time "open air above the bowl". A tile the player
                    // dug out is also block 59 but must render its flat floor (dugFloor[i] set by the carve).
                    if (blockIdx == 59 && (dugFloor == null || i >= dugFloor.Length || !dugFloor[i])) { airSkipped++; continue; }

                    // Per-corner weights for 3 materials (stone, earth, moss).
                    // Computed from the 4 surrounding tiles at each corner node →
                    // adjacent tiles share the same weights at shared corners →
                    // seamless transitions in the shader.
                    Color wNw_top = CornerWeights(tx,     ty    );
                    Color wNe_top = CornerWeights(tx + 1, ty    );
                    Color wSw_top = CornerWeights(tx,     ty + 1);
                    Color wSe_top = CornerWeights(tx + 1, ty + 1);
                    // Alpha = the native cutout alpha-test law: the corner byte [0x4d2..0x4d5] is 0
                    // at a state-3 node, 0xff otherwise (FUN_004ca830:388-391/:437-489);
                    // FUN_004cc040:55-57 blends the four bytes bilinearly into the vertex diffuse
                    // alpha (:218). The terrain material scape_solid draws with ALPHABLEND OFF +
                    // ALPHATEST GREATER ref=126 (FUN_00457b90:737-749; alpha source = vertex diffuse
                    // only, SELECTARG1 — FUN_004564c0:140-143), so the alpha acts as a stencil: the
                    // half-tile strip toward the hole (bilinear < 127) is cut away, the mid-tile
                    // shelf of the ring blocks (bilinear exactly 127) and everything solid stays opaque
                    // stone. No translucent band exists natively — DigglesGround clips at the
                    // same 126.5 isoline.
                    wNw_top.a = openNw ? 0f : 1f;
                    wNe_top.a = openNe ? 0f : 1f;
                    wSw_top.a = openSw ? 0f : 1f;
                    wSe_top.a = openSe ? 0f : 1f;
                    // For top faces: smooth blend across all materials.
                    // For cliff (sub=1) / bottom (sub=2): force pure stone weight
                    // so cliffs are always Gnd_A gray (no inherited earth tint),
                    // but keep moss weight so MODULATE2X can put moss drips where
                    // adjacent tile above is grass.
                    Color CliffWeight(Color tw) => new Color(1f, 0f, tw.b, tw.a);   // keep the air-corner luminance

                    for (int sub = 0; sub < 3; sub++)
                    {
                        var (triStart, triCount) = bdb.CellTriangleRange((int)blockIdx, sub);
                        if (triCount == 0) continue;
                        var cell = bdb.Blocks[(int)blockIdx].Cells[sub];
                        if (cell.VertCount == 0 || cell.VertCount > palIdxBuf.Length) continue;
                        bdb.CopyCellPaletteIndices((int)blockIdx, sub, palIdxBuf, normIdxBuf);

                        int scapeId = L2mMap.BlockType(cellId);   // cellId bits 5..7: 0=urw 1=swf 2=kris 3=lava
                        // Pattern from the tile's own corner bits (cellId 8..15). 0000 → slice 0 = neutral
                        // = base earth (no scape). ~half of lava tiles (and 86% of forest) are 0000, so the
                        // scape appears as sparse tile-level inclusions over earth — matching the original.
                        // Adjacent tiles' shared corner bits agree (authored 100%) → seamless.
                        int cellPattern = CornerPatternFromCellId(cellId);
                        // Full (1111) tiles vary among the base + <scape>_1111_a..f variants (slices 16..21)
                        // for an organic, hole-y look instead of one repeated texture. Partial patterns
                        // (region edges) keep their own slice. Original uses these a..f fills the same way.
                        int mossSlice = cellPattern;
                        if (cellPattern == 15)
                        {
                            unchecked
                            {
                                int hv = (tx * 73856093) ^ (ty * 19349663) ^ 0x5bd1e995;
                                mossSlice = 15 + ((hv & 0x7fffffff) % 7);   // 15 (base) or 16..21 (a..f)
                            }
                        }
                        var palette = bdb.Palette;
                        var idxPool = bdb.IndexPool;
                        for (int t = 0; t < triCount; t += 3)
                        {
                            ushort pa = idxPool[triStart + t];
                            ushort pb = idxPool[triStart + t + 1];
                            ushort pc = idxPool[triStart + t + 2];
                            var bucket = GetBucket(scapeId);
                            int a = EmitVertexW(pa, palIdxBuf, cell.VertCount, bdb, tx, ty,
                                wNw_top, wNe_top, wSw_top, wSe_top, bucket, mossSlice);
                            int b = EmitVertexW(pb, palIdxBuf, cell.VertCount, bdb, tx, ty,
                                wNw_top, wNe_top, wSw_top, wSe_top, bucket, mossSlice);
                            int c = EmitVertexW(pc, palIdxBuf, cell.VertCount, bdb, tx, ty,
                                wNw_top, wNe_top, wSw_top, wSe_top, bucket, mossSlice);
                            if (a < 0 || b < 0 || c < 0) continue;
                            bucket.Tris.Add(a); bucket.Tris.Add(b); bucket.Tris.Add(c);
                        }
                    }
                    rendered++;
                }
            }

            if (rendered + airSkipped + oob > 4096)   // keep per-chunk rebuilds quiet, log big passes
                Debug.Log($"[BdbPmpWorldBuilder] rendered={rendered} air-skipped={airSkipped} oob={oob}");
            return buckets;
        }

        private static int EmitVertexA(
            ushort poolIdx, ushort[] palIdxBuf, int vertCount, BdbModel bdb,
            int tx, int ty,
            float aNw, float aNe, float aSw, float aSe,
            Bucket bucket) => -1;

        // Emits a vertex with per-vertex RGB color = bilinear interp of 4 corner
        // material weights (stone=R, earth=G, moss=B). Shader uses this to blend
        // the 3 bound textures per pixel.
        private static int EmitVertexW(
            ushort poolIdx, ushort[] palIdxBuf, int vertCount, BdbModel bdb,
            int tx, int ty,
            Color wNw, Color wNe, Color wSw, Color wSe,
            Bucket bucket, int mossSlice, float yShift = 0f, float yShiftThreshold = float.PositiveInfinity)
        {
            if (poolIdx >= vertCount) return -1;
            int palIdx = palIdxBuf[poolIdx];
            if (palIdx >= bdb.Palette.Length) return -1;

            long key = ((long)palIdx << 32) | ((long)(uint)(ty * 65536 + tx));
            if (bucket.Dedupe.TryGetValue(key, out var existing)) return existing;

            var p = bdb.Palette[palIdx];
            // Per-block Y shift — only applied to vertices above threshold (top face).
            // Cliff bottoms stay at original Y so adjacent tiles' cliffs still meet.
            float vy = p.Position.z;
            if (vy >= yShiftThreshold) vy += yShift;
            var pos = new Vector3(p.Position.x + tx, vy, -(p.Position.y + ty));

            float u = Mathf.Clamp01(p.Position.x);
            float v = Mathf.Clamp01(p.Position.y);
            float w00 = (1f - u) * (1f - v);
            float w10 = u        * (1f - v);
            float w01 = (1f - u) * v;
            float w11 = u        * v;
            var w =
                new Color(
                    wNw.r * w00 + wNe.r * w10 + wSw.r * w01 + wSe.r * w11,
                    wNw.g * w00 + wNe.g * w10 + wSw.g * w01 + wSe.g * w11,
                    wNw.b * w00 + wNe.b * w10 + wSw.b * w01 + wSe.b * w11,
                    // Alpha = air-corner luminance (native FUN_004cc040:55-57 bilinear of the
                    // per-corner [0x4d2..0x4d5] bytes) — 1 over solid ground, ramping to 0 at
                    // backdrop-hole corner nodes; DigglesGround multiplies the lit colour by it.
                    wNw.a * w00 + wNe.a * w10 + wSw.a * w01 + wSe.a * w11);

            // Per-vertex variation:
            // uv1.x = stone/earth slice (random per tile + Z-modulated for depth variety)
            // uv1.y = detail/scape slice = the autotile corner pattern (0..15), passed in so
            // adjacent same-scape tiles share corner masks → seamless continuous field.
            unchecked {
                int h = tx * 73856093 ^ ty * 19349663;
                h ^= h >> 13;
                int abs = h & 0x7FFFFFFF;
                int zBin = (int)Mathf.Floor(pos.y * 0.5f);
                int stoneSlice = (abs + zBin * 7) % 15;
                bucket.Uvs1.Add(new Vector2((float)stoneSlice, (float)mossSlice));
            }

            int idx = bucket.Verts.Count;
            bucket.Verts.Add(pos);
            bucket.Uvs.Add(new Vector2(p.Uv.x, 1f - p.Uv.y));
            bucket.Colors.Add(w);
            bucket.Dedupe[key] = idx;
            return idx;
        }

        // Builds one terrain sub-mesh GameObject under `parent` (a chunk root or the legacy
        // TerrainMeshes container). The material is shared (per-scape, from TerrainTextureCache) —
        // identical uniforms to the old per-bucket materials, but stable across rebuilds.
        private static Mesh BuildGroundSubmesh(Transform parent, Bucket b, Material mat, string meshKey)
        {
            var go = new GameObject(b.Name);
            go.transform.SetParent(parent, false);
            var mesh = new Mesh
            {
                name = meshKey,
                indexFormat = b.Verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(b.Verts);
            mesh.SetUVs(0, b.Uvs);
            if (b.Uvs1.Count == b.Verts.Count) mesh.SetUVs(1, b.Uvs1);
            mesh.SetColors(b.Colors);
            mesh.SetTriangles(b.Tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            mr.receiveShadows = true;
            // Collider for click-to-move target picking.
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            return mesh;
        }

        // Builds a Texture2DArray with `count` slices from filename pattern.
        // pattern uses {0:D2} for the 1-based variant index.
        private static Texture2DArray? BuildVariantArray(string dataRoot, string filePattern, int count)
        {
            Texture2D? first = null;
            var slices = new Texture2D?[count];
            for (int i = 0; i < count; i++)
            {
                slices[i] = TryLoadTga(Path.Combine(dataRoot, "Ground", string.Format(filePattern, i + 1)));
                if (first == null && slices[i] != null) first = slices[i];
            }
            if (first == null) {
                Debug.LogWarning($"BuildVariantArray: no slices loaded for {filePattern}");
                return null;
            }
            int w = first.width, h = first.height;
            var arr = new Texture2DArray(w, h, count, TextureFormat.RGBA32, mipChain: true)
            {
                name = filePattern,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            for (int i = 0; i < count; i++)
            {
                var tex = slices[i] ?? first;
                if (tex.width != w || tex.height != h) tex = first;  // size mismatch fallback
                Graphics.CopyTexture(tex, 0, 0, arr, i, 0);
            }
            arr.Apply(updateMipmaps: true);
            return arr;
        }

        // Per-scape detail array (urw_/swf_/kris_/lava_) packed in numeric corner-pattern order:
        // slice i = <prefix><binary(i)>.tga (binary read NW NE SW SE), so the slice index equals
        // the 4-bit autotile pattern value from CornerPatternFromScape. Slice 0 (=0000, no
        // same-scape corner) and any missing pattern file fall back to a neutral 0.5-gray tile
        // (= MODULATE2X identity → shows pure base, never a stray corner mask). Pass count=16.
        private static Texture2DArray? BuildMossArray(string dataRoot, int count, string prefix = "urw_")
        {
            const int VARIANTS = 6;            // <prefix>1111_a..f — alternate full-coverage tiles for variety
            int total = count + VARIANTS;
            var slices = new Texture2D?[total];
            Texture2D? sample = null;
            for (int i = 0; i < count; i++)
            {
                string bin = System.Convert.ToString(i, 2).PadLeft(4, '0');
                slices[i] = TryLoadTga(Path.Combine(dataRoot, "Ground", "detail", prefix + bin + ".tga"));
                if (sample == null && slices[i] != null) sample = slices[i];
            }
            for (int v = 0; v < VARIANTS; v++)
            {
                char c = (char)('a' + v);
                slices[count + v] = TryLoadTga(Path.Combine(dataRoot, "Ground", "detail", prefix + "1111_" + c + ".tga"));
            }
            if (sample == null) return null;
            int w = sample.width, h = sample.height;
            var neutral = NeutralDetailTex(w, h);
            var full = slices[count - 1];      // base 1111 slice — fallback for any missing a..f variant
            var arr = new Texture2DArray(w, h, total, TextureFormat.RGBA32, mipChain: true)
            {
                name = prefix + "array",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            for (int i = 0; i < total; i++)
            {
                var tex = slices[i];
                if (tex == null || tex.width != w || tex.height != h)
                    tex = (i >= count && full != null && full.width == w && full.height == h) ? full : neutral;
                Graphics.CopyTexture(tex, 0, 0, arr, i, 0);
            }
            arr.Apply(updateMipmaps: true);
            return arr;
        }

        // 0.5-gray = MODULATE2X identity (base * 0.5 * 2 = base). Used for the empty (0000) slice
        // and any missing corner-pattern file, so an absent pattern shows pure base material.
        private static Texture2D NeutralDetailTex(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(128, 128, 128, 255);
            t.SetPixels32(px);
            t.Apply(updateMipmaps: false);
            return t;
        }

        private static Texture2D? TryLoadTga(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"  Texture file missing: {path}");
                return null;
            }
            try
            {
                if (path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = File.ReadAllBytes(path);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
                    {
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Bilinear,
                        name = System.IO.Path.GetFileName(path),
                    };
                    tex.LoadImage(bytes);
                    return tex;
                }
                var r = TgaLoader.LoadFromFile(path);
                return r?.Texture;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"  Texture load failed for {path}: {e.Message}");
                return null;
            }
        }

        private static int EmitVertex(
            ushort poolIdx, ushort[] palIdxBuf, int vertCount,
            BdbModel bdb, int tx, int ty,
            List<Vector3> verts, List<Vector2> uvs, Dictionary<long, int> dedupe)
        {
            if (poolIdx >= vertCount) return -1;
            int palIdx = palIdxBuf[poolIdx];
            if (palIdx >= bdb.Palette.Length) return -1;

            long key = ((long)palIdx << 32) | ((long)(uint)(ty * 65536 + tx));
            if (dedupe.TryGetValue(key, out var existing)) return existing;

            var p = bdb.Palette[palIdx];
            var pos = new Vector3(p.Position.x + tx, p.Position.z, -(p.Position.y + ty));

            int idx = verts.Count;
            verts.Add(pos);
            uvs.Add(new Vector2(p.Uv.x, 1f - p.Uv.y));
            dedupe[key] = idx;
            return idx;
        }

        private static void BuildSubmesh(
            GameObject root, System.Action<string, UnityEngine.Object>? ctx, string childName, Color fallbackColor,
            List<Vector3> verts, List<Vector2> uvs, List<int> tris,
            string meshKey, string matKey, Texture2D? tex = null)
        {
            if (verts.Count == 0) return;

            var go = new GameObject(childName);
            go.transform.SetParent(root.transform, false);
            var mesh = new Mesh
            {
                name = meshKey,
                indexFormat = verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (ctx != null) ctx?.Invoke(meshKey, mesh);

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No URP or Standard shader");
            var mat = new Material(shader) { name = matKey };
            // fallbackColor doubles as the tint multiplier when a texture is bound.
            // For texture-less materials it's just the solid colour.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", fallbackColor);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", fallbackColor);
            mat.color = fallbackColor;
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                mat.mainTexture = tex;
                // Diggles palette UVs are non-normalised ([0, 7.5]); Unity wraps via
                // the texture's wrapMode (already set to Repeat in TgaLoader).
            }
            if (ctx != null) ctx?.Invoke(matKey, mat);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }
    }
}
