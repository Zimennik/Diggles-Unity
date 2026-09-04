#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    // Headless bench for the per-entity frame army. A representative Play session had ~1200
    // live entities → ~3600 MonoBehaviour Update/LateUpdate dispatches per frame
    // (DigglesAnimator + DigglesAnimController + NativePickBody on every model entity, + link
    // attachments). The central DigglesFrameDriver replaces the dispatches with one loop; this
    // bench measures the actual per-frame body cost of a real 1200-entity population (spawned
    // through the real RuntimeWorldSpawner from the real class scripts, real stein_XX/mann
    // clips) so the per-entity µs and alloc are pinned before/after. The native→managed
    // dispatch overhead itself is Play-only (no player loop headless) — PerfHud's `upd`/`late`
    // phase lines measure that half in Play.
    //
    // [Explicit] — run on demand:
    // Unity.exe -batchmode -nographics -projectPath <p> -runTests -testPlatform EditMode
    // -testFilter "Diggles.Tests.PerfEntityArmyBench" -testResults <xml> -logFile <log> (no -quit)
    [Explicit("perf")]
    public sealed class PerfEntityArmyBench
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        const int Gnomes = 20, Items = 1180;   // ≈1200 — a representative Play population
        const float Dt = 1f / 60f;

        TclInterpreter _interp = null!;
        DigglesTclContext _ctx = null!;
        GameObject _worldRoot = null!;
        readonly List<DigglesAnimator> _animators = new();
        readonly List<DigglesAnimController> _controllers = new();
        readonly List<NativePickBody> _pickBodies = new();
        readonly List<DigglesLinkAttachment> _links = new();

        [OneTimeSetUp]
        public void SpawnWorld()
        {
            _interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            _interp.UnknownCommandHandler = (i, a) => "0";
            _ctx = DigglesTclEngine.Register(_interp);
            _ctx.DataRoot = DataRoot;
            _ctx.TolerateMissingEntities = true;
            _ctx.TolerateMissingClass = true;
            _ctx.TolerateMissingFiles = true;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { _interp.Eval("call " + s); }
                catch (Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }

            for (int i = 0; i < Gnomes; i++)
                _interp.Eval($"new Zwerg \"\" \"{20 + (i % 10)} {30 + (i / 10)} 8\" \"0 0 0\"");
            for (int i = 0; i < Items; i++)
                _interp.Eval($"new Stein \"\" \"{20 + (i % 40)} {28 + ((i / 40) % 30)} 8\" \"0 0 0\"");

            // Real spawner → real Model GO + MeshFilter/Renderer + DigglesAnimator +
            // DigglesAnimController (+ BoxCollider + NativePickBody for non-Dummy classes),
            // playing the real spawn-pose clips (stein_0X.standard / mann.standard).
            _worldRoot = new GameObject("ArmyBenchRoot");
            var spawner = new RuntimeWorldSpawner(DataRoot);
            foreach (var e in _ctx.Entities.Values)
            {
                var go = spawner.Spawn(e, _worldRoot.transform);
                if (go == null) continue;
                e.GameObject = go;
                var anim = go.GetComponentInChildren<DigglesAnimator>();
                var ctrl = go.GetComponentInChildren<DigglesAnimController>();
                var pick = go.GetComponentInChildren<NativePickBody>();
                if (anim != null) _animators.Add(anim);
                if (ctrl != null) _controllers.Add(ctrl);
                if (pick != null) _pickBodies.Add(pick);
            }
            DigglesAnimator.PublishTimeScale(1.0);

            // Link riders (hats/held items — ~1 per gnome in Play): a child GO riding the
            // gnome's head link point through the real per-frame Place math.
            int made = 0;
            foreach (var a in _animators)
            {
                if (made >= Gnomes) break;
                if (a.CurrentFrameCount < 2) continue;             // gnomes only (multi-frame idle)
                var child = new GameObject("hatRider");
                var la = child.AddComponent<DigglesLinkAttachment>();
                la.Attach(a.transform, a, DigglesLinkPoints.Head, null);
                _links.Add(la);
                made++;
            }

            TestContext.WriteLine($"[PERFBENCH-ARMY] rigs: {_animators.Count} animators, " +
                $"{_controllers.Count} controllers, {_pickBodies.Count} pickbodies, {_links.Count} links " +
                $"({_ctx.Entities.Count} entities)");
            Assert.Greater(_animators.Count, 1000, "the real spawner must produce a 1000+ animator army");
        }

        [OneTimeTearDown]
        public void Teardown()
        {
            DigglesFrameDriver.RegisterInEditModeForTests = false;
            DigglesFrameDriver.ClearAllForTests();
            if (_worldRoot != null) UnityEngine.Object.DestroyImmediate(_worldRoot);
        }

        static readonly Func<long>? AllocBytes = CreateAllocBytes();
        static Func<long>? CreateAllocBytes()
        {
            var mi = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",
                BindingFlags.Static | BindingFlags.Public);
            if (mi == null) return null;
            var f = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), mi);
            long a = f();
            var junk = new byte[64 * 1024];
            long b = f();
            GC.KeepAlive(junk);
            return b > a ? f : null;
        }

        (double usPerFrame, double bytesPerFrame) Measure(int frames, Action body)
        {
            body();   // warm-up
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long b0 = AllocBytes?.Invoke() ?? GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++) body();
            sw.Stop();
            long b1 = AllocBytes?.Invoke() ?? GC.GetTotalMemory(false);
            return (sw.Elapsed.TotalMilliseconds * 1000.0 / frames,
                    Math.Max(0, b1 - b0) / (double)frames);
        }

        // The exact bodies the old per-instance Update/LateUpdate dispatches ran, called
        // directly — the floor the central driver cannot beat (its win is dispatch removal).
        [Test]
        public void Army_DirectBodies()
        {
            var (us, bytes) = Measure(600, () =>
            {
                for (int i = 0; i < _animators.Count; i++) _animators[i].TickClock(Dt);
                for (int i = 0; i < _controllers.Count; i++) _controllers[i].TickChain(Dt);
                for (int i = 0; i < _pickBodies.Count; i++) _pickBodies[i].SyncNow();
            });
            int n = _animators.Count;
            Debug.Log($"[PERFBENCH-ARMY] direct bodies @{n} ents : {us:F0} us/frame " +
                      $"({us * 1000.0 / n:F0} ns/ent) | {bytes:F0} B/frame (600 frames)");
            Assert.Less(bytes, 1024.0, "the per-frame army must be allocation-free");
        }

        // The same army through the central driver's actual phase loops (list iteration +
        // null-check + try/catch per item on top of the bodies).
        [Test]
        public void Army_ViaFrameDriver()
        {
            DigglesFrameDriver.RegisterInEditModeForTests = true;
            DigglesFrameDriver.ClearAllForTests();
            foreach (var a in _animators) DigglesFrameDriver.Register(a);
            foreach (var c in _controllers) DigglesFrameDriver.Register(c);
            foreach (var p in _pickBodies) DigglesFrameDriver.Register(p);
            foreach (var l in _links) DigglesFrameDriver.Register(l);
            try
            {
                var (us, bytes) = Measure(600, () =>
                {
                    DigglesFrameDriver.RunUpdatePhase(Dt);
                    DigglesFrameDriver.RunLatePhase();
                });
                int n = _animators.Count;
                Debug.Log($"[PERFBENCH-ARMY] frame driver @{n} ents (+{_links.Count} links) : {us:F0} us/frame " +
                          $"({us * 1000.0 / n:F0} ns/ent) | {bytes:F0} B/frame (600 frames)");
                Assert.Less(bytes, 1024.0, "the driver loops must be allocation-free");
            }
            finally
            {
                DigglesFrameDriver.RegisterInEditModeForTests = false;
                DigglesFrameDriver.ClearAllForTests();
            }
        }

        // Regression pin: the driver must tick every registered component exactly once per
        // phase call, and unregistered components must stop ticking (the enable/disable law).
        [Test]
        public void Driver_TickAndUnregisterContract()
        {
            var anim = _animators[0];
            double clock0 = anim.AnimClock;
            DigglesFrameDriver.RegisterInEditModeForTests = true;
            DigglesFrameDriver.ClearAllForTests();
            try
            {
                DigglesFrameDriver.Register(anim);
                DigglesFrameDriver.RunUpdatePhase(Dt);
                Assert.AreEqual(clock0 + Dt, anim.AnimClock, 1e-9, "registered animator must tick once");
                DigglesFrameDriver.Register(anim);   // double-register must not double-tick
                DigglesFrameDriver.RunUpdatePhase(Dt);
                Assert.AreEqual(clock0 + 2 * Dt, anim.AnimClock, 1e-9, "double-register must stay a single tick");
                DigglesFrameDriver.Unregister(anim);
                DigglesFrameDriver.RunUpdatePhase(Dt);
                Assert.AreEqual(clock0 + 2 * Dt, anim.AnimClock, 1e-9, "unregistered animator must stop ticking");
            }
            finally
            {
                DigglesFrameDriver.RegisterInEditModeForTests = false;
                DigglesFrameDriver.ClearAllForTests();
            }
        }
    }
}
