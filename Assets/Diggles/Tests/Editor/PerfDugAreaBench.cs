#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    // Headless evidence-gathering bench for the "steady 20-30 FPS once the player digs down"
    // report. Everything here scales with dug area in Play:
    // (a) dropped items (Stein/ore/…) grow the entity count N → sim O(N) passes + obj_query;
    // (b) the fog-of-war layer works over the revealed area (reveal rays, fade lists,
    // overlay chunk rebuilds, presenter texture pumps);
    // (c) continuous digging fires a per-carve pipeline (nav revalidate + chunk remesh with
    // a synchronous MeshCollider cook + dig-mark overlay rebuild).
    // Each test prints [PERFBENCH-DUG] lines; wall-clock is ±25% noisy — read only >2x effects.
    // Alloc metric = GC.CollectionCount(0) delta (same convention as PerfSimBench).
    //
    // [Explicit] — run on demand:
    // Unity.exe -batchmode -nographics -projectPath <p> -runTests -testPlatform EditMode
    // -testFilter "Diggles.Tests.PerfDugAreaBench" -testResults <xml> -logFile <log> (no -quit)
    [Explicit("perf")]
    public sealed class PerfDugAreaBench
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // ------------------------------------------------------------------ shared plumbing

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            return (interp, ctx, sim);
        }

        static void Spawn(TclInterpreter interp, string cls, string pos)
            => interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"");

        static void StableGc()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }

        // Measure steps of an already-settled sim. Returns (usPerStep, gen0).
        static (double us, int gen0) MeasureSteps(DigglesGameSim sim, int steps)
        {
            StableGc();
            int gc0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) sim.Step(0.025);
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds * 1000.0 / steps, GC.CollectionCount(0) - gc0);
        }

        // ------------------------------------------------------------------ 1. sim vs item count

        // Does the fixed-step sim get steadily more expensive as dug-out drops pile up?
        // 20 gnomes + K Stein items (the dug-area variable). Also probes the AI's
        // O(N) primitive (obj_query) at each population.
        [Test]
        public void Sim_ItemScaling()
        {
            foreach (int items in new[] { 0, 300, 1200 })
            {
                var (interp, ctx, sim) = RealHost();
                for (int i = 0; i < 20; i++) Spawn(interp, "Zwerg", $"{20 + (i % 10)} {30 + (i / 10)} 8");
                for (int i = 0; i < items; i++)
                    Spawn(interp, "Stein", $"{20 + (i % 40)} {28 + ((i / 40) % 16)} 8");
                for (int i = 0; i < 80; i++) sim.Step(0.025);   // settle init timers/events

                var (us, gen0) = MeasureSteps(sim, 400);
                Debug.Log($"[PERFBENCH-DUG] sim items={items,5} ents={ctx.Entities.Count,5} : " +
                          $"{us,8:F2} us/step | gen0={gen0} | ff-x10-frame≈{us * 40 / 1000.0:F2} ms");

                // The AI "find nearest" primitive that every digger/hauler leans on — cost per call
                // at this population. Real call shape from z_procs.tcl:916.
                const string q = "obj_query this \"-class {Steinbrocken Kohlebrocken Stein} -range 10 -limit 1 -flagneg locked\"";
                interp.Eval("sel /obj");
                var first = ctx.Entities.Count > 0 ? "this" : "";
                StableGc();
                int qgc0 = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 500; i++) interp.Eval(q);
                sw.Stop();
                Debug.Log($"[PERFBENCH-DUG] objquery ents={ctx.Entities.Count,5} : " +
                          $"{sw.Elapsed.TotalMilliseconds * 1000.0 / 500.0,8:F2} us/call | gen0={GC.CollectionCount(0) - qgc0}");
                _ = first;
            }
        }

        // ------------------------------------------------------------------ 2. sim FoW pass cost

        // Same population, with the fog-of-war layer wired the way DigglesWorldRuntime wires it
        // (512×640 state; every 4th 10 Hz think = every 16 steps). The fill function models an
        // open dug region (rays run their full radius — the worst/realistic dug-area case).
        // Headless entities have no GameObject, so this measures the reveal half (BeginUpdate
        // fades + per-entity UpdateRevealer raycasts); the render-visibility sweep is Play-only.
        [Test]
        public void Sim_FowPassCost()
        {
            var (interp, ctx, sim) = RealHost();
            for (int i = 0; i < 20; i++) Spawn(interp, "Zwerg", $"{20 + (i % 10)} {30 + (i / 10)} 8");
            for (int i = 0; i < 300; i++)
                Spawn(interp, "Stein", $"{20 + (i % 40)} {28 + ((i / 40) % 16)} 8");
            for (int i = 0; i < 80; i++) sim.Step(0.025);

            var (usNoFow, _) = MeasureSteps(sim, 400);

            // Open cave 0..79 × 8..59, solid elsewhere (fill nibble 15 blocks reveal rays).
            ctx.FogOfWar = new FogOfWarState(512, 640,
                (x, y) => (uint)x < 80 && y >= 8 && y < 60 ? 0 : 15);
            ctx.LocalPlayer = 0;
            for (int i = 0; i < 32; i++) sim.Step(0.025);       // let the first updates run

            var (usFow, gen0) = MeasureSteps(sim, 400);
            // 400 steps = 25 FoW updates → per-update cost = delta × 16.
            double perUpdateUs = (usFow - usNoFow) * 16.0;
            Debug.Log($"[PERFBENCH-DUG] fowpass ents={ctx.Entities.Count} : " +
                      $"{usNoFow:F2} → {usFow:F2} us/step | ≈{perUpdateUs:F0} us/FoW-update | gen0={gen0}");
        }

        // ------------------------------------------------------------------ 3. FoW state micro

        // The pure sim half on a full-size map: 40 moving revealers (gnome size 14×8 tiles →
        // fine 112×64) in a big open region — reveal rays + fade lists at steady state.
        [Test]
        public void FowState_UpdateCost()
        {
            var fog = new FogOfWarState(512, 640,
                (x, y) => (uint)x < 200 && y >= 8 && y < 208 ? 0 : 15);
            const int N = 40;
            var px = new float[N]; var py = new float[N];
            var lx = new float[N]; var ly = new float[N];
            var rng = new System.Random(42);
            for (int i = 0; i < N; i++) { px[i] = 20 + rng.Next(160); py[i] = 20 + rng.Next(170); }

            void Update(int tick)
            {
                fog.BeginUpdate(0);
                for (int i = 0; i < N; i++)
                {
                    // wander 0.5 tiles/update — every revealer crosses a tile every ~2 updates
                    px[i] += (float)(rng.NextDouble() - 0.5); py[i] += (float)(rng.NextDouble() - 0.5);
                    fog.UpdateRevealer(px[i], py[i], ref lx[i], ref ly[i], 112, 64, 0, i);
                }
                fog.EndUpdate();
            }

            for (int t = 0; t < 50; t++) Update(t);   // reach steady state (fade lists populated)
            StableGc();
            int gc0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int t = 0; t < 200; t++) Update(t);
            sw.Stop();
            Debug.Log($"[PERFBENCH-DUG] fowstate 40 movers : {sw.Elapsed.TotalMilliseconds * 1000.0 / 200.0:F0} us/update " +
                      $"| gen0={GC.CollectionCount(0) - gc0} " +
                      $"| x1 cadence=2.5/s, ff-x10=25/s");
        }

        // ------------------------------------------------------------------ 4. FoW overlay chunks

        // The black-volume mesh: full-map build cost + the per-chunk rebuild that fade
        // crossings / reveals trigger during play (boundary chunk = worst case: walls emitted).
        [Test]
        public void FowOverlay_ChunkRebuildCost()
        {
            var heights = new int[512 * 640];
            for (int i = 0; i < heights.Length; i++) heights[i] = 15;
            // dug-open region 0..199 × 8..207 (nibble 0 floor)
            for (int y = 8; y < 208; y++)
                for (int x = 0; x < 200; x++) heights[y * 512 + x] = 0;

            var fog = new FogOfWarState(512, 640,
                (x, y) => (uint)x < 200 && y >= 8 && y < 208 ? 0 : 15);
            // Explore the dug region (owner 0 = local): box reveal through everything.
            fog.RemoveFogBoxNoCollision(100 * 8, 100 * 8, 110 * 8, 110 * 8, 0);

            var go = new GameObject("FowOverlayBench");
            try
            {
                var sw0 = Stopwatch.StartNew();
                var ov = go.AddComponent<FogOfWarOverlay>();
                ov.Init(fog, (x, y) => (uint)x < 512 && (uint)y < 640 ? heights[y * 512 + x] : 15);
                sw0.Stop();
                Debug.Log($"[PERFBENCH-DUG] fowoverlay full build ({ov.ChunksX}x{ov.ChunksY} chunks) : {sw0.Elapsed.TotalMilliseconds:F1} ms");

                // Steady-state: a fade crossing dirties a 3×3 rect at the black/explored boundary
                // → its chunk(s) rebuild on the next Pump. ×200.
                StableGc();
                int gc0 = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    ov.MarkRectDirty(198, 100 + (i % 40), 200, 102 + (i % 40));   // boundary chunk(s)
                    ov.Pump();
                }
                sw.Stop();
                Debug.Log($"[PERFBENCH-DUG] fowoverlay boundary-chunk rebuild : {sw.Elapsed.TotalMilliseconds * 1000.0 / 200.0:F0} us/pump " +
                          $"| gen0={GC.CollectionCount(0) - gc0}");

                // Worst case: a whole row of black interior chunks dirtied (big reveal front).
                StableGc();
                sw.Restart();
                for (int i = 0; i < 50; i++)
                {
                    ov.MarkRectDirty(200, 8, 511, 40);   // ~10 solid-black chunks
                    ov.Pump();
                }
                sw.Stop();
                Debug.Log($"[PERFBENCH-DUG] fowoverlay 10-black-chunk rebuild : {sw.Elapsed.TotalMilliseconds / 50.0:F2} ms/pump");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // ------------------------------------------------------------------ 5. FoW presenter pump

        // The render texture feed: dirty-rect target rebuild + the temporal-smoothing Advance +
        // the full 512×640 RG16 SetPixelData/Apply that runs every frame while any texel fades.
        [Test]
        public void FowPresenter_PumpCost()
        {
            var fog = new FogOfWarState(512, 640, (x, y) => 0);
            var go = new GameObject("FowPresenterBench");
            try
            {
                var pr = go.AddComponent<FogOfWarPresenter>();
                pr.Init(fog);

                // Simulate a walking crowd: each "FoW update" reveals a fresh band (RegionDirty
                // fires), then 6 render frames pump the smoothing. Rect ~ the union dirty rect a
                // spread-out crowd produces (quarter map).
                StableGc();
                int gc0 = GC.CollectionCount(0);
                var sw = Stopwatch.StartNew();
                int pumps = 0;
                for (int t = 0; t < 40; t++)
                {
                    fog.RemoveFogBoxNoCollision((60 + t) * 8, (60 + t) * 8, 40 * 8, 40 * 8, 0);
                    for (int f = 0; f < 6; f++) { pr.Pump(0.016f); pumps++; }
                }
                sw.Stop();
                Debug.Log($"[PERFBENCH-DUG] fowpresenter : {sw.Elapsed.TotalMilliseconds / pumps:F2} ms/frame-pump " +
                          $"(incl. full-tex SetPixelData+Apply while fading) | gen0={GC.CollectionCount(0) - gc0}");

                // Idle presenter (nothing chasing): the every-frame floor.
                for (int f = 0; f < 200; f++) pr.Pump(0.016f);   // drain chasing
                sw.Restart();
                for (int f = 0; f < 400; f++) pr.Pump(0.016f);
                sw.Stop();
                Debug.Log($"[PERFBENCH-DUG] fowpresenter idle : {sw.Elapsed.TotalMilliseconds * 1000.0 / 400.0:F1} us/frame-pump");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // ------------------------------------------------------------------ 6. dig churn pipeline

        // The per-carve chain on the real start meadow: CarveTileNow (dig + synchronous
        // NavWorld.RevalidateRect) + the debounced chunk remesh (mesh re-emit + RecalculateNormals
        // + synchronous MeshCollider cook) + the DigMarkOverlay full-remark rebuild. Also proves /
        // refutes "chunk rebuilds get dearer as the chunk fills with dug cliff geometry".
        [Test]
        public void DigChurn_CarvePipeline()
        {
            if (!Directory.Exists(DataRoot)) { Assert.Ignore($"data root {DataRoot} missing"); return; }
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                engine.Interp.Eval("map create 512 640");
                engine.Interp.Eval("set temppos {208 0}");
                try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
                catch (Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
                engine.Map.EmitTerrain(rootGo);
                var terr = rootGo.GetComponent<DiggableTerrain>();
                Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
                var grid = engine.Map.WorldResult!.Chunks!;

                // Find a diggable interior spot (solid tile inside the meshed bbox).
                int cx = -1, cy = -1;
                for (int ty = 12; ty < 60 && cx < 0; ty++)
                    for (int tx = 214; tx < 280; tx++)
                        if (terr!.GetHmap(tx, ty) >= 14 && terr.Diggable(tx, ty)) { cx = tx; cy = ty; break; }
                Assert.GreaterOrEqual(cx, 0, "no diggable solid tile found in the meadow bbox");
                TestContext.WriteLine($"carving around ({cx},{cy})");

                // (a) carve+nav cost: CarveTileNow = dig + synchronous RevalidateDirtyNav, remesh deferred.
                StableGc();
                int carves = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    int nx = cx + (i % 20), ny = cy + (i / 20);
                    if (terr!.CarveTileNow(nx, ny)) carves++;
                }
                sw.Stop();
                Debug.Log($"[PERFBENCH-DUG] carve+nav : {sw.Elapsed.TotalMilliseconds / Math.Max(1, carves):F2} ms/carve ({carves}/200 effective)");

                // (b) the deferred remesh those carves queued (chunk re-emit + collider cook).
                sw.Restart();
                terr!.FlushRemesh();
                sw.Stop();
                Debug.Log($"[PERFBENCH-DUG] remesh after 200 carves : {sw.Elapsed.TotalMilliseconds:F1} ms");

                // (c) single-chunk rebuild before/after heavy digging inside it — the "dug chunks
                // get denser" growth claim. Chunk of the carve area:
                int chunkTx = (cx / BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles) * BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles + 8;
                int chunkTy = (cy / BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles) * BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles + 8;
                long VertsOfChunk()
                {
                    var key = BdbPmpWorldBuilder.TerrainChunkGrid.Key(
                        cx / BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles,
                        cy / BdbPmpWorldBuilder.TerrainChunkGrid.ChunkTiles);
                    return grid.Chunks.TryGetValue(key, out var ci) ? ci.VertexCount : -1;
                }
                double MeasureChunkRebuild()
                {
                    var sw2 = Stopwatch.StartNew();
                    for (int i = 0; i < 50; i++)
                    {
                        grid.MarkTileDirty(chunkTx, chunkTy);
                        grid.RebuildDirty();
                    }
                    sw2.Stop();
                    return sw2.Elapsed.TotalMilliseconds / 50.0;
                }
                double msBefore = MeasureChunkRebuild();
                long vBefore = VertsOfChunk();
                // dig hard inside this chunk: 5 passes over a 12×12 node patch
                for (int pass = 0; pass < 5; pass++)
                    for (int dy = 0; dy < 12; dy++)
                        for (int dx = 0; dx < 12; dx++)
                            terr.CarveTileNow(cx + dx, cy + dy);
                terr.FlushRemesh();
                double msAfter = MeasureChunkRebuild();
                long vAfter = VertsOfChunk();
                Debug.Log($"[PERFBENCH-DUG] chunk rebuild density : before {msBefore:F2} ms ({vBefore} verts) → after {msAfter:F2} ms ({vAfter} verts)");

                // (d) DigMarkOverlay: full RebuildMarks with a big committed marquee — the cost a
                // single nibble flip re-pays on the next frame during active digging.
                var ovGo = new GameObject("digmarks");
                try
                {
                    var ov = ovGo.AddComponent<DigMarkOverlay>();
                    ov.Init(terr);
                    foreach (int marks in new[] { 200, 2000 })
                    {
                        int painted = 0;
                        for (int i = 0; painted < marks && i < marks * 4; i++)
                        {
                            int tx = 216 + (i % 60), ty = 12 + (i / 60);
                            if (terr.Diggable(tx, ty)) { terr.SetDigMark(tx, ty, 0, 0); painted++; }
                        }
                        ov.Pump();   // absorb the paint burst
                        StableGc();
                        var sw3 = Stopwatch.StartNew();
                        for (int i = 0; i < 50; i++)
                        {
                            terr.SetDigMark(216, 12, 0, 0);   // one nibble write → _dirtyMarks
                            ov.Pump();                          // full RebuildMarks
                        }
                        sw3.Stop();
                        Debug.Log($"[PERFBENCH-DUG] digmark rebuild marks≈{painted} : {sw3.Elapsed.TotalMilliseconds / 50.0:F2} ms/rebuild");
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(ovGo); }
            }
            finally { UnityEngine.Object.DestroyImmediate(rootGo); }
        }
    }
}
