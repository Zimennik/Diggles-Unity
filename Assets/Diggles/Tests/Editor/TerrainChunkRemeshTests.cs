#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // CHUNKED terrain remesh (dirty-rect) — the perf fix for the full-map RemeshFromArrays hitch:
    // every dig carve / world-growth stamp used to rebuild the ENTIRE render bbox (re-loading ~40
    // ground TGAs and re-cooking the giant colliders) on the 0.4s debounce. Now the bbox is
    // partitioned into absolute-grid-aligned 32×32-tile chunks (TerrainChunkGrid) and the debounce
    // re-emits ONLY the chunks the edit's ±1-tile-margined rect touches.
    //
    // What these tests lock:
    // • a single carve dirties (and rebuilds) only the chunks around its node — all other chunk
    // meshes keep their instance IDs and Version counters;
    // • a growth stamp (live map_template) dirties exactly the chunks of its rect; bbox growth
    // allocates new chunks without touching fully-interior old ones;
    // • visual parity: after the same edits, the incremental result has the same total triangle
    // count as a from-scratch full rebuild (chunked MarkAll AND the legacy monolithic renderer).
    public sealed class TerrainChunkRemeshTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        const int CT = BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        // ===================== pure marking math (no real data, runs always) =====================

        // MarkTileDirty/MarkRectDirty map tiles to chunks with the ±1-tile corner-weight margin and
        // clamp at the world border. No rebuild is triggered, so the missing BDB/textures are fine.
        [Test]
        public void MarkDirty_MapsTilesToChunks_WithCornerMargin()
        {
            var go = new GameObject("ChunkGridHost");
            _spawnedGos.Add(go);
            var r = new BdbPmpWorldBuilder.TclWorldResult { Width = 4 * CT, Height = 4 * CT };
            var grid = new BdbPmpWorldBuilder.TerrainChunkGrid(go, null!, r, "Z:/no-such-data-root");

            // Interior tile: margin stays inside chunk (1,1).
            grid.MarkTileDirty(CT + 1, CT + 1);
            CollectionAssert.AreEquivalent(
                new[] { BdbPmpWorldBuilder.TerrainChunkGrid.Key(1, 1) }, grid.DirtyChunks,
                "an interior tile must dirty exactly its own chunk");

            // Chunk-corner tile: the ±1 margin spills into all 4 neighbouring chunks.
            grid.MarkTileDirty(CT, CT);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    BdbPmpWorldBuilder.TerrainChunkGrid.Key(0, 0),
                    BdbPmpWorldBuilder.TerrainChunkGrid.Key(1, 0),
                    BdbPmpWorldBuilder.TerrainChunkGrid.Key(0, 1),
                    BdbPmpWorldBuilder.TerrainChunkGrid.Key(1, 1),
                }, grid.DirtyChunks,
                "a chunk-corner tile must dirty the 4 chunks meeting there (corner weights cross the seam)");

            // With an EMPTY render bbox (nothing emitted yet) a flush is a clean no-op that just
            // consumes the dirty set — no BDB/geometry access, no chunks allocated.
            grid.RebuildDirty();
            Assert.AreEqual(0, grid.DirtyChunks.Count, "RebuildDirty must consume the dirty set");
            Assert.AreEqual(0, grid.Chunks.Count, "an empty-bbox rebuild must not allocate chunks");

            // World-corner tile: margin clamps at 0 — no negative chunk coords.
            grid.MarkTileDirty(0, 0);
            CollectionAssert.AreEquivalent(
                new[] { BdbPmpWorldBuilder.TerrainChunkGrid.Key(0, 0) }, grid.DirtyChunks);
            grid.RebuildDirty();

            // Rect spanning chunks: every intersecting chunk (with margin) is marked, nothing else.
            grid.MarkRectDirty(10, 10, CT + 5, 12);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    BdbPmpWorldBuilder.TerrainChunkGrid.Key(0, 0),
                    BdbPmpWorldBuilder.TerrainChunkGrid.Key(1, 0),
                }, grid.DirtyChunks,
                "a rect dirties exactly the chunks its margined extent intersects");
        }

        // ===================== real meadow fixture (Explicit, real BDB + template) =====================

        (GameObject root, DigglesEngine engine, DiggableTerrain terr,
         BdbPmpWorldBuilder.TclWorldResult r, BdbPmpWorldBuilder.TerrainChunkGrid grid) BuildStartMeadow()
        {
            var rootGo = new GameObject("WorldRoot");
            _spawnedGos.Add(rootGo);
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);   // the Q_x(-90) hack
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            engine.Ctx.Terrain = terr;
            var r = engine.Map.WorldResult!;
            Assert.IsNotNull(r.Chunks, "runtime build must produce a TerrainChunkGrid");
            Assert.AreEqual(0, r.Chunks!.DirtyChunks.Count, "initial build must leave no dirty chunks");
            Assert.Greater(r.Chunks.Chunks.Count, 1, "the meadow bbox must span several chunks");
            return (rootGo, engine, terr!, r, r.Chunks);
        }

        // (version, mesh instance-ids) per chunk — instance ids survive as ints even if destroyed.
        static Dictionary<long, (int version, string meshes)> Snapshot(BdbPmpWorldBuilder.TerrainChunkGrid grid)
        {
            var snap = new Dictionary<long, (int, string)>();
            foreach (var kv in grid.Chunks)
            {
                var ids = new List<int>();
                foreach (var m in kv.Value.Meshes) ids.Add(m == null ? 0 : m.GetInstanceID());
                snap[kv.Key] = (kv.Value.Version, string.Join(",", ids));
            }
            return snap;
        }

        static HashSet<long> ChunkKeysForMarginedRect(int x0, int y0, int x1, int y1)
        {
            x0 = System.Math.Max(0, x0 - 1); y0 = System.Math.Max(0, y0 - 1);
            x1 += 1; y1 += 1;
            var keys = new HashSet<long>();
            for (int cy = y0 / CT; cy <= y1 / CT; cy++)
                for (int cx = x0 / CT; cx <= x1 / CT; cx++)
                    keys.Add(BdbPmpWorldBuilder.TerrainChunkGrid.Key(cx, cy));
            return keys;
        }

        // First node in the meadow band where the debug carve actually steps a corner. Failed
        // candidates are guaranteed no-ops (DigNode only reports true after a BlockIdx write).
        static (int nx, int ny) CarveOneNode(DiggableTerrain terr)
        {
            for (int ny = 28; ny < 44; ny++)
                for (int nx = 230; nx < 264; nx++)
                    if (terr.ApplyDig(nx, ny)) return (nx, ny);
            Assert.Fail("no carvable node found in the meadow band");
            return (-1, -1);
        }

        static int TotalTriangles(BdbPmpWorldBuilder.TerrainChunkGrid grid)
        {
            int t = 0;
            foreach (var kv in grid.Chunks) t += kv.Value.TriangleCount;
            return t;
        }

        static int TotalTrianglesUnder(Transform parent)
        {
            int t = 0;
            foreach (var mf in parent.GetComponentsInChildren<MeshFilter>())
                if (mf.sharedMesh != null) t += (int)(mf.sharedMesh.triangles.Length / 3);
            return t;
        }

        // Real dims of a template pmp (same subdir fallbacks RuntimeMapBuilder resolves with).
        static (int w, int h) PmpDims(string bareName)
        {
            foreach (var sub in new[] { "Templates/pmp", "Templates/pmp/split", "Templates" })
            {
                var p = Path.Combine(DataRoot, sub, bareName);
                if (File.Exists(p)) { var pmp = PmpParser.Parse(p); return ((int)pmp.Width, (int)pmp.Height); }
            }
            Assert.Fail($"pmp '{bareName}' not found under {DataRoot}");
            return (0, 0);
        }

        // One dig carve must dirty only the chunks around its node, and the debounced rebuild
        // (FlushRemesh = the exact path Update takes) must re-emit ONLY those — every other chunk
        // keeps its Version and its mesh instance IDs.
        [Test, Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
        public void CarveOneNode_RebuildsOnlyAffectedChunks()
        {
            var (rootGo, _, terr, r, grid) = BuildStartMeadow();
            var before = Snapshot(grid);
            int bx0 = r.RenderXMin, by0 = r.RenderYMin, bx1 = r.RenderXMax, by1 = r.RenderYMax;

            var (nx, ny) = CarveOneNode(terr);
            TestContext.WriteLine($"carved node ({nx},{ny})");

            // The carve steps the 2×2 tiles at the node; +1 corner-weight margin → (nx-2..nx+1, ny-2..ny+1).
            var expected = ChunkKeysForMarginedRect(nx - 1, ny - 1, nx, ny);
            // RENDER APRON (DiggableTerrain.EnsureRenderApron): a carve near the bbox edge
            // legitimately grows the meshed window and dirties the NEW bands — include the
            // bbox delta in the allowed set (far-away interior chunks must still survive).
            if (r.RenderXMin != bx0 || r.RenderYMin != by0 || r.RenderXMax != bx1 || r.RenderYMax != by1)
            {
                TestContext.WriteLine($"apron grew bbox ({bx0},{by0})..({bx1},{by1}) -> " +
                    $"({r.RenderXMin},{r.RenderYMin})..({r.RenderXMax},{r.RenderYMax})");
                if (r.RenderXMin < bx0) expected.UnionWith(ChunkKeysForMarginedRect(r.RenderXMin, r.RenderYMin, bx0, r.RenderYMax));
                if (r.RenderXMax > bx1) expected.UnionWith(ChunkKeysForMarginedRect(bx1, r.RenderYMin, r.RenderXMax, r.RenderYMax));
                if (r.RenderYMin < by0) expected.UnionWith(ChunkKeysForMarginedRect(r.RenderXMin, r.RenderYMin, r.RenderXMax, by0));
                if (r.RenderYMax > by1) expected.UnionWith(ChunkKeysForMarginedRect(r.RenderXMin, by1, r.RenderXMax, r.RenderYMax));
            }
            Assert.Greater(grid.DirtyChunks.Count, 0, "the carve must dirty at least one chunk");
            foreach (var key in grid.DirtyChunks)
                Assert.IsTrue(expected.Contains(key), $"dirty chunk {key} outside the carve's chunk set");

            int rebuildsBefore = grid.RebuildCount;
            int dirtyCount = grid.DirtyChunks.Count;
            terr.FlushRemesh();

            Assert.AreEqual(0, grid.DirtyChunks.Count, "flush must consume the dirty set");
            Assert.Greater(grid.RebuildCount - rebuildsBefore, 0, "the flush must rebuild something");
            Assert.LessOrEqual(grid.RebuildCount - rebuildsBefore, dirtyCount,
                $"the flush must rebuild at most the {dirtyCount} dirty chunks — NOT the whole map " +
                $"({grid.Chunks.Count} chunks total)");

            var after = Snapshot(grid);
            int untouched = 0, changed = 0;
            foreach (var kv in before)
            {
                if (expected.Contains(kv.Key)) { if (after[kv.Key].version != kv.Value.version) changed++; continue; }
                Assert.AreEqual(kv.Value.version, after[kv.Key].version,
                    $"chunk {kv.Key} outside the carve rect must keep its Version");
                Assert.AreEqual(kv.Value.meshes, after[kv.Key].meshes,
                    $"chunk {kv.Key} outside the carve rect must keep its mesh instances");
                untouched++;
            }
            Assert.Greater(changed, 0, "the carved node's chunk must actually re-emit");
            Assert.Greater(untouched, 0, "there must BE far-away chunks that survived untouched");
            TestContext.WriteLine($"chunks: {changed} rebuilt, {untouched} untouched of {before.Count}");
        }

        // A live world-growth stamp (post-EmitTerrain map_template) must dirty EXACTLY the chunks of
        // its rect; the bbox extension allocates new chunks without touching fully-interior old ones.
        [Test, Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
        public void GrowthStamp_DirtiesExactlyItsChunks_BboxGrowthLeavesInteriorAlone()
        {
            var (rootGo, engine, terr, r, grid) = BuildStartMeadow();
            int ox0 = r.RenderXMin, oy0 = r.RenderYMin, ox1 = r.RenderXMax, oy1 = r.RenderYMax;
            var before = Snapshot(grid);

            // Stamp a real cave template far from the start area (same target as the
            // live-mode test) — chunk-interior, so the expected dirty set is small and exact.
            const int sx = 100, sy = 200;
            var (pw, ph) = PmpDims("splt_hol_014_tAA.pmp");
            TestContext.WriteLine($"stamp template is {pw}x{ph}");
            Assert.IsFalse(r.HasTpl[sy * r.Width + sx], "stamp target must be un-templated");
            engine.Interp.Eval($"map_template splt_hol_014_tAA.pmp {sx} {sy}");

            var expected = ChunkKeysForMarginedRect(sx, sy, sx + pw - 1, sy + ph - 1);
            CollectionAssert.AreEquivalent(expected, new List<long>(grid.DirtyChunks),
                "the stamp must dirty exactly the chunks of its (margined) rect — bbox fill happens at flush");

            terr.FlushRemesh();

            // The stamped chunk exists and carries geometry now.
            var stampKey = BdbPmpWorldBuilder.TerrainChunkGrid.Key(sx / CT, sy / CT);
            Assert.IsTrue(grid.Chunks.ContainsKey(stampKey), "the stamp's chunk must be allocated");
            Assert.Greater(grid.Chunks[stampKey].TriangleCount, 0, "the stamp's chunk must hold geometry");

            // Chunks FULLY INSIDE the old bbox (their clip cannot have changed) and outside the
            // stamp rect must be untouched — bbox growth only re-emits edge/new chunks.
            var after = Snapshot(grid);
            int untouched = 0;
            foreach (var kv in before)
            {
                int cx = (int)(kv.Key & 0xFFFFFFFF), cy = (int)(kv.Key >> 32);
                bool interior = cx * CT >= ox0 && cx * CT + CT - 1 <= ox1
                             && cy * CT >= oy0 && cy * CT + CT - 1 <= oy1;
                if (!interior || expected.Contains(kv.Key)) continue;
                Assert.AreEqual(kv.Value.version, after[kv.Key].version,
                    $"interior chunk {cx},{cy} must keep its Version across a far-away stamp");
                Assert.AreEqual(kv.Value.meshes, after[kv.Key].meshes,
                    $"interior chunk {cx},{cy} must keep its mesh instances across a far-away stamp");
                untouched++;
            }
            TestContext.WriteLine($"interior chunks untouched: {untouched}; total chunks now {grid.Chunks.Count}");
        }

        // Visual-parity smoke: after a carve AND a growth stamp, the incremental (dirty-chunk)
        // result must show the exact same total triangle count as (a) a full chunked rebuild and
        // (b) the legacy monolithic renderer run over the same arrays/bbox.
        [Test, Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
        public void VisualParity_IncrementalMatchesFullRebuild()
        {
            var (rootGo, engine, terr, r, grid) = BuildStartMeadow();

            var (nx, ny) = CarveOneNode(terr);
            engine.Interp.Eval("map_template splt_hol_014_tAA.pmp 100 200");
            terr.FlushRemesh();   // incremental: carve chunks + stamp chunk + bbox fill

            int triIncremental = TotalTriangles(grid);
            var container = rootGo.transform.Find("TerrainMeshes");
            Assert.IsNotNull(container, "TerrainMeshes container must exist");
            Assert.AreEqual(triIncremental, TotalTrianglesUnder(container!),
                "chunk bookkeeping must match the actual scene meshes");

            // (a) full chunked rebuild over the same state.
            grid.MarkAllDirty();
            grid.RebuildDirty();
            Assert.AreEqual(triIncremental, TotalTriangles(grid),
                "incremental remesh must produce the same triangles as a full chunked rebuild");

            // (b) the legacy monolithic renderer over the same arrays/bbox into a scratch root.
            var bdb = BdbParser.Parse(Path.Combine(DataRoot, "BlockDB2.BDB"));
            var monoRoot = new GameObject("MonolithParity");
            _spawnedGos.Add(monoRoot);
            BdbPmpWorldBuilder.RenderWorldArrays(monoRoot, null, bdb,
                r.BlockIdx, r.Zone, r.Terrain, r.CellId, r.Width,
                r.RenderXMin, r.RenderYMin, r.RenderXMax, r.RenderYMax, DataRoot, r.DugFloor);
            int triMono = TotalTrianglesUnder(monoRoot.transform);
            Assert.AreEqual(triMono, triIncremental,
                "chunked remesh must produce the same triangles as the legacy monolithic renderer");
            TestContext.WriteLine($"parity: incremental={triIncremental} mono={triMono} (carve@({nx},{ny}) + stamp@100,200)");
        }
    }
}
