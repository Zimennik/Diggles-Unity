#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    // Headless bench for the WORLD-GROWTH hitch: Play F3 captures show
    // sim.step frames of 374/738/1797 ms — up to 2.8 s — with 66 MB..1 GB single-frame managed
    // allocations, firing at the StoryMgr growth moments (a new cave cluster is stamped as the
    // player digs). This bench reproduces the growth pipeline headless and MEASURES which phase
    // allocates the hundreds of MB, using GC.GetAllocatedBytesForCurrentThread (gen0 collection
    // counts are dead on the post-classinit Boehm heap). Wall-clock is ±25% noisy — read >2x only.
    //
    // Phases measured, matching the real generate_next order:
    // A. lg solver — sm chain + lg_start retry loop + lg_get_level (the lg_get_level PerfTrace).
    // B. stamp burst — LiveBlitPmp per placement (PMP parse + blit + RefreshRect → nav revalidate).
    // C. deferred remesh — the debounced TerrainChunkGrid.RebuildDirty the stamps queued.
    //
    // [Explicit] — run on demand:
    // Unity.exe -batchmode -nographics -projectPath <p> -runTests -testPlatform EditMode
    // -testFilter "Diggles.Tests.PerfWorldGrowthBench" -testResults <xml> -logFile <log> (no -quit)
    [Explicit("perf")]
    public sealed class PerfWorldGrowthBench
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static readonly Func<long>? AllocBytes = CreateAllocBytes();
        static Func<long>? CreateAllocBytes()
        {
            var mi = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",
                BindingFlags.Static | BindingFlags.Public);
            if (mi == null) return null;
            var f = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), mi);
            long a = f(); var junk = new byte[64 * 1024]; long b = f(); GC.KeepAlive(junk);
            return b > a ? f : null;
        }
        static long Now() => AllocBytes?.Invoke() ?? GC.GetTotalMemory(false);
        static string MB(long bytes) => $"{bytes / (1024.0 * 1024.0):F2} MB";

        static void StableGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

        // ------------------------------------------------------------------ A. lg solver

        // Drive the REAL Urwald generation chain (like LevelGenTests.Generate_RealUrwald) and measure
        // the per-generation managed allocation of the lg_start retry loop + lg_get_level. No terrain.
        [Test]
        public void Solver_RealUrwald_GenerationAlloc()
        {
            if (!Directory.Exists(DataRoot)) { Assert.Ignore($"data root {DataRoot} missing"); return; }
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingFiles = true; ctx.TolerateMissingClass = true; ctx.TolerateMissingEntities = true;
            foreach (var f in new[] { "scripts/init/lgtools.tcl", "scripts/init/lginit.tcl" })
                try { interp.Eval("call " + f); } catch (Exception e) { TestContext.WriteLine($"[load] {f}: {e.Message}"); }
            interp.Eval("lg_tp_clear");
            interp.Eval("lg_tp_addtemplatesets \"Urwald.Std\"");
            interp.Eval("sm_create_map 512 640");

            // One generation = the chain the StoryMgr runs per breakthrough. Measured in isolation.
            long RunOneGeneration(int cx, int cy, int dir)
            {
                ctx.StoryLatchDir = dir; ctx.StoryLatchX = cx * 4; ctx.StoryLatchY = cy * 4; ctx.StoryLatchConsumed = false;
                interp.Eval("sm_scan");
                interp.Eval($"sm_get_level_info {cx} {cy}");
                interp.Eval("lg_set_templateratio 0 0.1 1.0");
                interp.Eval("lg_set_starttemplatepro 1 1");
                StableGc();
                long b0 = Now();
                var sw = Stopwatch.StartNew();
                string started = "0";
                int tries = 0;
                for (int t = 0; t < 25 && started != "1"; t++) { started = interp.Eval("lg_start").Trim(); tries++; }
                string level = interp.Eval("lg_get_level").Trim();
                sw.Stop();
                long used = Now() - b0;
                int pieces = TclList.Parse(level).Count;
                Debug.Log($"[PERFBENCH-GROWTH] solver gen @({cx},{cy}) dir{dir}: started={started} tries={tries} " +
                          $"pieces={pieces} | {sw.Elapsed.TotalMilliseconds:F1} ms | alloc={MB(used)}");
                return used;
            }

            // A handful of generations down the meadow — the sequence a play session produces.
            long total = 0; int gens = 0;
            foreach (var (cx, cy, dir) in new[] { (10, 3, 2), (12, 8, 2), (14, 14, 2), (16, 22, 2), (18, 30, 2) })
            { total += RunOneGeneration(cx, cy, dir); gens++; }
            Debug.Log($"[PERFBENCH-GROWTH] solver TOTAL {gens} gens: alloc={MB(total)} (avg {MB(total / Math.Max(1, gens))}/gen)");
        }

        // ------------------------------------------------------------------ B/C. stamp burst + remesh

        // Boot the REAL start meadow (as DigChurn does), then reproduce a growth generation's STAMP
        // burst: LiveBlitPmp a cluster of real template PMPs growing down from the meadow, then flush
        // the debounced remesh. Measures the blit+nav half (sim.step) and the remesh half separately.
        [Test]
        public void StampBurst_RealMeadow_GrowthAlloc()
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

                long b0 = Now(); var swEmit = Stopwatch.StartNew();
                engine.Map.EmitTerrain(rootGo);
                swEmit.Stop();
                var r = engine.Map.WorldResult!;
                var grid = r.Chunks!;
                var terr = rootGo.GetComponent<DiggableTerrain>()!;
                Debug.Log($"[PERFBENCH-GROWTH] boot EmitTerrain: {swEmit.Elapsed.TotalMilliseconds:F0} ms | alloc={MB(Now() - b0)} | " +
                          $"bbox X {r.RenderXMin}..{r.RenderXMax} Y {r.RenderYMin}..{r.RenderYMax} | chunks={grid.Chunks.Count} | rebuilds={grid.RebuildCount}");

                // Real growth cluster PMPs (small Urwald cave pieces), stamped down-and-around from the
                // meadow like a StoryMgr generation. Absolute positions ~ the meadow origin (208,*).
                var stamps = new List<(string pmp, int x, int y)>();
                string[] pool = { "hol_020.pmp", "hol_021.pmp", "hol_022.pmp", "hol_023.pmp", "hol_024.pmp",
                                  "gng_001.pmp", "gng_002.pmp", "gng_003.pmp", "gng_007.pmp", "gng_012.pmp",
                                  "caves_1.pmp", "caves_2.pmp", "caves_3.pmp" };
                int k = 0;
                for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 5; col++)
                    {
                        int px = 200 + col * 20;
                        int py = 40 + row * 16;
                        stamps.Add((pool[k % pool.Length], px, py));
                        k++;
                    }

                // --- B. the stamp burst (blit + PMP parse + RefreshRect → nav revalidate). This is the
                // work that runs synchronously inside the generate_next sim.step. ---
                StableGc();
                long sb0 = Now(); var swStamp = Stopwatch.StartNew();
                foreach (var (pmp, x, y) in stamps)
                    engine.Interp.Eval($"map_template {pmp} {x} {y}");
                swStamp.Stop();
                long stampAlloc = Now() - sb0;
                Debug.Log($"[PERFBENCH-GROWTH] STAMP burst ({stamps.Count} PMPs): {swStamp.Elapsed.TotalMilliseconds:F0} ms | " +
                          $"alloc={MB(stampAlloc)} ({MB(stampAlloc / stamps.Count)}/stamp) | " +
                          $"bbox X {r.RenderXMin}..{r.RenderXMax} Y {r.RenderYMin}..{r.RenderYMax} | dirty={grid.DirtyChunks.Count}");

                // --- C. the deferred remesh those stamps queued (chunk re-emit + collider cook). In Play
                // this is the debounced DiggableTerrain.Update remesh, a separate frame. ---
                StableGc();
                long rb0 = Now(); var swRemesh = Stopwatch.StartNew();
                terr.FlushRemesh();
                swRemesh.Stop();
                long remeshAlloc = Now() - rb0;
                Debug.Log($"[PERFBENCH-GROWTH] REMESH after burst: {swRemesh.Elapsed.TotalMilliseconds:F0} ms | " +
                          $"alloc={MB(remeshAlloc)} | rebuilds(total)={grid.RebuildCount} | chunks={grid.Chunks.Count}");

                // --- D. a FAR growth stamp — the union-corner default-earth fill case. A stamp far from
                // the current bbox extends it to a big union rectangle; SyncBbox then dirties every newly
                // covered chunk. This is the compounding O(bbox) remesh the report's 2.8 s hitch matches. ---
                StableGc();
                long fb0 = Now(); var swFar = Stopwatch.StartNew();
                engine.Interp.Eval("map_template hol_023.pmp 300 200");   // far corner
                terr.FlushRemesh();
                swFar.Stop();
                long farAlloc = Now() - fb0;
                Debug.Log($"[PERFBENCH-GROWTH] FAR stamp+remesh @(300,200): {swFar.Elapsed.TotalMilliseconds:F0} ms | " +
                          $"alloc={MB(farAlloc)} | bbox X {r.RenderXMin}..{r.RenderXMax} Y {r.RenderYMin}..{r.RenderYMax} | " +
                          $"chunks={grid.Chunks.Count} | rebuilds(total)={grid.RebuildCount}");
            }
            finally { UnityEngine.Object.DestroyImmediate(rootGo); }
        }

        // ------------------------------------------------------------------ F. model-parse breakdown

        // The measured sim.step storm is ThreeDbParser.Parse of big character models (frau/mann
        // 30-33 MB → ~77 MB retained). Break the parse down by array type so the fix targets the
        // largest reducible chunk. (GetTotalMemory delta = retained heap growth.)
        [Test]
        public void ModelParse_Breakdown()
        {
            string frau = System.IO.Path.Combine(DataRoot, "3db", "frau.3db");
            if (!File.Exists(frau)) { Assert.Ignore($"missing {frau}"); return; }
            StableGc();
            long b = Now(); var sw = Stopwatch.StartNew();
            var m = Diggles.Runtime.ThreeDbParser.Parse(frau);
            sw.Stop();
            long used = Now() - b;
            long triBytes = 0; foreach (var t in m.Triangles) triBytes += (t.Raw?.Length ?? 0) + 24 + 24;
            // 6 bytes/vertex after the ThreeDbQVert (3× int16) compaction — was 12 (Vector3Int).
            long vBytes = 0, vCount = 0; foreach (var v in m.VLists) { vBytes += (long)v.Length * 6 + 24; vCount += v.Length; }
            long uvBytes = 0; foreach (var u in m.UvLists) uvBytes += (long)u.Length * 8 + 24;
            long nBytes = 0; foreach (var n in m.NLists) nBytes += (n?.Length ?? 0) + 24;
            long idxBytes = 0; foreach (var ix in m.IndexLists) idxBytes += (long)(ix?.Length ?? 0) * 2 + 24;
            long shadowBytes = (long)m.Shadows.Count * (0x400 + 24);
            long meshBlocks = (long)m.Meshes.Count * (128 + 48 + 48);
            Debug.Log($"[PERFBENCH-GROWTH] frau.3db parse: {sw.Elapsed.TotalMilliseconds:F0} ms, retained={MB(used)} | " +
                      $"tris={m.Triangles.Count}(~{MB(triBytes)}) verts={vCount}(~{MB(vBytes)}) " +
                      $"uv~{MB(uvBytes)} n~{MB(nBytes)} idx~{MB(idxBytes)} meshes={m.Meshes.Count}(~{MB(meshBlocks)}) " +
                      $"shadows={m.Shadows.Count}(~{MB(shadowBytes)}) anims={m.Anims.Count}");
        }

        // ------------------------------------------------------------------ E. FULL CAMPAIGN sim.step

        // GROUND TRUTH: boot the REAL campaign (StoryMgr load_level Campaign), dig a shaft to trigger a
        // StoryMgr growth generation, and MEASURE per-sim.step managed allocation + wall time — the
        // exact path PerfHud labels "sim.step". Records the top allocating/slowest steps and whether
        // each was a generate_next (level appears) or a load_next (a stamp lands). This reproduces the
        // Play 66 MB..1 GB sim.step hitch headless. (Adapted from WorldGrowthPlayReproTests.)
        [Test]
        public void Campaign_GrowthStep_PerStepAlloc()
        {
            if (!Directory.Exists(DataRoot)) { Assert.Ignore($"data root {DataRoot} missing"); return; }
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl",
                                          "scripts/init/classinit.tcl", "scripts/init/lginit.tcl" })
                    try { engine.Interp.Eval("call " + s); }
                    catch (Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
                engine.Interp.Eval("obj_clear\nsel /obj\nset sm [new StoryMgr]\ncall_method $sm load_level Campaign");
                engine.Interp.Eval("");
                long bootAlloc;
                { StableGc(); long b = Now(); engine.Map.EmitTerrain(rootGo); bootAlloc = Now() - b; }
                var ctx = engine.Ctx; var interp = engine.Interp;
                ctx.Terrain = rootGo.GetComponent<DiggableTerrain>();
                var terr = ctx.Terrain!;
                Debug.Log($"[PERFBENCH-GROWTH] CAMPAIGN boot: {ctx.Entities.Count} entities, EmitTerrain alloc={MB(bootAlloc)}");

                // wire the drop-on-dig hook exactly as DigglesWorldRuntime does (fires in sim.step)
                if (ctx.EntitySpawner is Diggles.Runtime.RuntimeWorldSpawner rws)
                {
                    rws.Terrain = terr;
                    terr.TilesChanged += (a, b, c, d) => Diggles.Runtime.RuntimeWorldSpawner.ResettleLooseItems(
                        terr.Nav, ctx.Entities.Values, a, b, c, d, e => rws.ApplyPosition(e.GameObject, e.Position));
                }

                // Per-step instrumentation. Returns alloc+ms and whether it was a growth event.
                int liveStamps() { int n = 0; foreach (var d in engine.Map.Diagnostics) if (d.Contains("LIVE pmp")) n++; return n; }
                long worstAlloc = 0; double worstMs = 0; string worstWhat = "";
                var topSteps = new List<(long alloc, double ms, string what)>();
                int prevLive = liveStamps();
                void Step(string tag)
                {
                    long b = Now(); var sw = Stopwatch.StartNew();
                    engine.Sim.Step(0.1);
                    sw.Stop(); long a = Now() - b; double ms = sw.Elapsed.TotalMilliseconds;
                    int nowLive = liveStamps();
                    string what = nowLive > prevLive ? $"STAMP(+{nowLive - prevLive})" : tag;
                    prevLive = nowLive;
                    if (a > 4 * 1024 * 1024 || ms > 30) topSteps.Add((a, ms, what));
                    if (a > worstAlloc) { worstAlloc = a; worstMs = ms; worstWhat = what; }
                }

                for (int i = 0; i < 70; i++) Step("settle");

                // STEADY-STATE BASELINE (no growth): avg per-step alloc for the populated world.
                // If this is already tens of MB, the churn is general per-entity think, not growth.
                StableGc();
                long baseB = Now(); var baseSw = Stopwatch.StartNew();
                long baseMax = 0;
                for (int i = 0; i < 60; i++)
                {
                    long b = Now(); engine.Sim.Step(0.1); long a = Now() - b;
                    if (a > baseMax) baseMax = a;
                }
                baseSw.Stop();
                long baseTotal = Now() - baseB;
                Debug.Log($"[PERFBENCH-GROWTH] STEADY baseline ({ctx.Entities.Count} ents): " +
                          $"avg={MB(baseTotal / 60)}/step, max={MB(baseMax)}/step, {baseSw.Elapsed.TotalMilliseconds / 60:F1} ms/step");

                // Dig a shaft down from a dwarf to latch a growth breakthrough (as the repro test does).
                var dwarf = System.Linq.Enumerable.FirstOrDefault(ctx.Entities.Values, e => e.Class?.Name == "Zwerg");
                if (dwarf == null) { Assert.Ignore("campaign spawned no dwarves"); return; }
                int zx = Mathf.RoundToInt(dwarf.Position.x), zy = Mathf.RoundToInt(dwarf.Position.y);
                int floorY = zy; while (floorY < terr.Height - 1 && !terr.Solid(zx, floorY)) floorY++;
                int StoryCellOf(int v) => (v + ((v >> 31) & 3)) >> 2;
                int lastGrownCy = 0;
                int cellX = StoryCellOf(zx);
                for (int cy = 0; cy < 60; cy++)
                    for (int cx = cellX; cx <= cellX + 1; cx++)
                        if (interp.Eval($"sm_map_get {cx} {cy}").Trim() == "1") lastGrownCy = Mathf.Max(lastGrownCy, cy);

                var (ax, ay) = DigMarquee.SnapAnchor(zx, floorY + 1);
                int wantBottom = Mathf.Min(terr.Height - 12, Math.Max((lastGrownCy + 4) * 4, floorY + 24));
                var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, wantBottom);
                var (x0, y0, x1, y1) = DigMarquee.SnapRect(ax, ay, bx, by);
                // paint standard-tunnel marquee (mode-1 dummy) via the terrain directly
                bool Solid(int x, int y) => terr.NwCornerHeight(x, y) > 1;
                bool Open(int x, int y) => (terr.RawDigNibble(x, y, 0) & 8) != 0;
                bool Exposed(int x, int y) => terr.TileMaterialDiggable(x, y);
                for (int ty = y0; ty <= y1; ty++)
                    for (int tx = x0; tx <= x1; tx++)
                        if (DigMarquee.CellValid(tx, ty, terr.Width, terr.Height, Solid, Open, Exposed))
                            terr.SetDigMark(tx, ty, 0, 0, dummy: true);
                terr.IncPlayerDigMarkId();

                int before = liveStamps();
                int tyCursor = y0;
                for (int attempt = 0; attempt < 8 && liveStamps() == before && tyCursor <= y1; attempt++)
                {
                    bool deepLatch = false;
                    for (; tyCursor <= y1 && !deepLatch; tyCursor++)
                    {
                        if (!terr.IsDigMarked(x0, tyCursor, 0)) continue;
                        interp.Eval($"dig_apply {{{x0} {tyCursor} 8}}");
                        deepLatch = ctx.StoryLatchDir == 2 && !ctx.StoryLatchConsumed && StoryCellOf(ctx.StoryLatchY) > lastGrownCy;
                    }
                    if (!deepLatch) break;
                    for (int s = 0; s < 600 && liveStamps() == before; s++) Step("drain");
                }
                for (int i = 0; i < 400; i++) Step("stream");   // let the whole level stream in (one stamp/tick)

                int grown = liveStamps() - before;
                topSteps.Sort((p, q) => q.alloc.CompareTo(p.alloc));
                Debug.Log($"[PERFBENCH-GROWTH] CAMPAIGN growth: {grown} stamps landed | WORST step alloc={MB(worstAlloc)} ({worstMs:F0} ms, {worstWhat})");
                for (int i = 0; i < Math.Min(12, topSteps.Count); i++)
                    Debug.Log($"[PERFBENCH-GROWTH]   top step {i}: alloc={MB(topSteps[i].alloc)} {topSteps[i].ms:F0} ms [{topSteps[i].what}]");
                if (grown == 0) TestContext.WriteLine("no growth triggered (dig/latch chain missed) — WORST step still reported");
            }
            finally { UnityEngine.Object.DestroyImmediate(rootGo); }
        }
    }
}
