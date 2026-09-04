#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    // Allocation/latency benchmark for the terrain re-mesher (BdbPmpWorldBuilder.TerrainChunkGrid).
    // The Play profiler showed multi-hundred-MB single-frame GC spikes (multi-second freezes) each
    // time the terrain re-meshes (world load, world-growth dig, render-apron expansion), because
    // every chunk rebuild used to DestroyImmediate the old GO+meshes and allocate fresh List/Dict
    // working buffers + a new Mesh + new MeshCollider. The fix REUSES the chunk meshes/GOs/colliders
    // (mesh.Clear + refill) and POOLS the Bucket working buffers.
    //
    // This bench builds the real start-meadow world (same recipe as NavWorldRealTerrainTests) then
    // repeatedly re-meshes it and reports the per-rebuild GC gen0 collections + managed heap delta
    // + wall time. GC.GetTotalAllocatedBytes is unavailable in this Mono runtime, so
    // GC.CollectionCount(0) is the headline number: it must drop sharply after the fix.
    //
    // Explicit — run headless with:
    // Unity.exe -batchmode -nographics -projectPath <worktree> -runTests -testPlatform EditMode \
    // -testFilter "Diggles.Tests.PerfTerrainBench" -testResults <xml> -logFile <log>
    [Explicit("perf")]
    public sealed class PerfTerrainBench
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        (GameObject root, DigglesEngine engine, BdbPmpWorldBuilder.TerrainChunkGrid grid) BuildStartMeadow()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);   // the Q_x(-90) hack
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var r = engine.Map.WorldResult;
            Assert.IsNotNull(r, "no WorldResult after EmitTerrain");
            Assert.IsNotNull(r!.Chunks, "runtime build must produce a TerrainChunkGrid");
            return (rootGo, engine, r.Chunks!);
        }

        static (int chunks, long verts) Totals(BdbPmpWorldBuilder.TerrainChunkGrid grid)
        {
            int c = 0; long v = 0;
            foreach (var kv in grid.Chunks) { c++; v += kv.Value.VertexCount; }
            return (c, v);
        }

        static void StableGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Report gen0 collections + managed-heap delta + ms over `iters` runs of `rebuild`.
        static void Measure(string label, int iters, int chunks, long verts, Action rebuild)
        {
            StableGc();
            int gen0Before = GC.CollectionCount(0);
            long memBefore = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++) rebuild();
            sw.Stop();
            int gen0After = GC.CollectionCount(0);
            long memAfter = GC.GetTotalMemory(false);

            double gen0PerRebuild = (gen0After - gen0Before) / (double)iters;
            double heapKbPerRebuild = (memAfter - memBefore) / 1024.0 / iters;
            double msPerRebuild = sw.Elapsed.TotalMilliseconds / iters;
            Debug.Log($"[PERFBENCH-TERRAIN] {label}: chunks={chunks} verts={verts} iters={iters} " +
                      $"gen0Δ={gen0After - gen0Before} (={gen0PerRebuild:0.00}/rebuild) " +
                      $"heapΔ={(memAfter - memBefore) / 1024}KB (={heapKbPerRebuild:0.0}KB/rebuild) " +
                      $"ms/rebuild={msPerRebuild:0.0}");
        }

        [Test]
        public void RemeshAllocProfile()
        {
            if (!Directory.Exists(DataRoot)) { Assert.Ignore($"data root {DataRoot} missing"); return; }
            var (rootGo, _, grid) = BuildStartMeadow();
            var (chunks, verts) = Totals(grid);
            TestContext.WriteLine($"world built: {chunks} chunks, {verts} verts");
            Assert.Greater(chunks, 1, "the meadow bbox must span several chunks");

            // Warm up the reuse path (first re-mesh after the initial build allocates the pooled
            // buffers + steady-state mesh capacity; subsequent ones must be near-zero-garbage).
            grid.MarkAllDirty(); grid.RebuildDirty();
            grid.MarkAllDirty(); grid.RebuildDirty();

            // (A) FULL-MAP rebuild — the world-load / render-apron / big-growth spike. Every chunk
            // re-emits each iteration (reusing its meshes after the fix).
            Measure("full-map", 20, chunks, verts, () =>
            {
                grid.MarkAllDirty();
                grid.RebuildDirty();
            });

            // (B) SINGLE-CHUNK rebuild — the common dig carve. Dirty one chunk-interior tile so only
            // its chunk re-emits.
            int midCy = 0, midCx = 0, best = -1;
            foreach (var kv in grid.Chunks)
                if (kv.Value.TriangleCount > best) { best = kv.Value.TriangleCount; midCx = kv.Value.Cx; midCy = kv.Value.Cy; }
            int tileX = midCx * BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles + 16;
            int tileY = midCy * BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles + 16;
            TestContext.WriteLine($"single-chunk dirty tile = ({tileX},{tileY}) in chunk ({midCx},{midCy}) tris={best}");
            Measure("single-chunk", 200, 1, grid.Chunks.TryGetValue(
                BdbPmpWorldBuilder.TerrainChunkGrid.Key(midCx, midCy), out var ci) ? ci.VertexCount : 0, () =>
            {
                grid.MarkTileDirty(tileX, tileY);
                grid.RebuildDirty();
            });

            UnityEngine.Object.DestroyImmediate(rootGo);
        }
    }
}
