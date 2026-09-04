#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Diagnostics;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    // Headless allocation + timing micro-benchmark for the FIXED-STEP SIM (DigglesGameSim.Step).
    // This is the "VContainer tick-loop -> GC.Alloc" the Play profiler blames — the per-object Tcl
    // think/event loop. Boots the REAL campaign classes (like WorkLoopTests), spawns a representative
    // population, and measures gen0 GC collections + microseconds per Step. The dominant allocation
    // source is the Tcl interpreter re-parsing script bodies every eval (TclInterpreter.Eval), so a
    // parse-cache fix should collapse gen0 toward 0. Grep the log for [PERFBENCH-SIM].
    //
    // [Explicit] — loads ~300 classes; run on demand:
    // Unity.exe -batchmode -nographics -projectPath <p> -runTests -testPlatform EditMode
    // -testFilter "Diggles.Tests.PerfSimBench" -testResults <xml> -logFile <log> (Editor closed, NO -quit)
    [Explicit("perf")]
    public sealed class PerfSimBench
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

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

        void Measure(string label, int nZwerg, int steps)
        {
            var (interp, _, sim) = RealHost();
            // A representative crowd: gnomes (state-think heavy) + props (mushrooms/fires that
            // ambient-tick). Spread on a small patch so they coexist without overlapping handles.
            for (int i = 0; i < nZwerg; i++) Spawn(interp, "Zwerg", $"{20 + (i % 10)} {30 + (i / 10)} 8");
            for (int i = 0; i < nZwerg / 4 + 1; i++) Spawn(interp, "Pilz", $"{40 + i} 30 8");
            Spawn(interp, "Feuerstelle", "50 30 8");

            // Settle: let spawn/init events drain + states arm (first-touch allocs happen here).
            for (int i = 0; i < 80; i++) sim.Step(0.025);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            int gc0 = GC.CollectionCount(0);
            long m0 = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) sim.Step(0.025);
            sw.Stop();
            long m1 = GC.GetTotalMemory(false);
            int gen0 = GC.CollectionCount(0) - gc0;

            double usPerStep = sw.Elapsed.TotalMilliseconds * 1000.0 / steps;
            long heapDelta = m1 - m0;
            // ~40 Step calls == one render frame at fast-forward x10 (EngineTickService cap = 4*speed).
            double msPerFfFrame = usPerStep * 40.0 / 1000.0;
            Debug.Log($"[PERFBENCH-SIM] {label,-10} zwerg={nZwerg,3} steps={steps} : gen0={gen0,4} | heapDelta={heapDelta,10} B | {usPerStep,8:F2} us/step | {msPerFfFrame,6:F2} ms/ff-x10-frame");
        }

        [Test] public void Sim_20_gnomes() => Measure("20gnomes", 20, 400);
        [Test] public void Sim_50_gnomes() => Measure("50gnomes", 50, 400);
    }
}
