#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Regression tests for the post-cutscene "work-assignment storm" fix chain:
    // the Play storm was a per-gnome ~7 Hz recycle — assign → hat swap → walk fails instantly
    // (FindPath NULL at the campfire) → gnome_failed_work zeroed the cooldown + reopened the job
    // → instant reassign — amplified visually by frame-0 anim restarts from the think-tick churn.
    // Three independent dampers are under test here:
    // 1. DigglesAnimator.SetAnim same-clip LOOP dedupe (no frame-0 rewind on re-issue);
    // 2. ProdDispatcher failure breathers — the failed gnome's 5.0 s re-task embargo
    // (FUN_00676500 rec+0x48 = 5.0f, read by FUN_00438210:35-38) and the failed job's
    // 20-tick reopen cooldown (FUN_0043a0c0 target+0x3e8 = 0x14);
    // 3. MetaWalkAction phase-0 — full cruise from tick 1 (FUN_00418db0 is arc-length coupled,
    // no 0→cruise ramp) and the degenerate re-walk completing WITHOUT touching the animator.
    // All tests are data-free (no class loads, no BDB) and run headlessly in EditMode.
    public sealed class PostCutsceneChurnTests
    {
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        GameObject Go(string name)
        {
            var go = new GameObject(name);
            _gos.Add(go);
            return go;
        }

        static DigglesAnimClip Clip(string name, int frames = 6)
        {
            var c = new DigglesAnimClip { Name = name, Frames = new Mesh[frames] };
            for (int i = 0; i < frames; i++) c.Frames[i] = new Mesh();
            return c;
        }

        DigglesAnimator Animator(params string[] clips)
        {
            var a = Go("anim").AddComponent<DigglesAnimator>();
            foreach (var n in clips) a.AddClip(Clip(n));
            return a;
        }

        // ------------------------------------------------------------------
        // 1) Animator same-clip dedupe
        // ------------------------------------------------------------------

        [Test]
        public void Animator_SameLoopReissue_DoesNotRestart()
        {
            var a = Animator("gehen");
            a.SetAnim("gehen", 0, DigglesAnimator.AnimMode.Loop);
            Assert.AreEqual(1, a.RestartCount, "first SetAnim must start the clip");
            // The Tcl think-tick churn: the same loop re-issued every ~0.14 s must be a no-op.
            for (int i = 0; i < 5; i++) a.SetAnim("gehen", 0, DigglesAnimator.AnimMode.Loop);
            Assert.AreEqual(1, a.RestartCount, "same-clip LOOP re-issue must dedupe (no frame-0 rewind)");
            Assert.AreEqual("gehen", a.CurrentAnimName);
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, a.CurrentMode);
        }

        [Test]
        public void Animator_OnceReplay_Restarts()
        {
            var a = Animator("jumpa");
            a.SetAnim("jumpa", 0, DigglesAnimator.AnimMode.Once);
            a.SetAnim("jumpa", 0, DigglesAnimator.AnimMode.Once);
            Assert.AreEqual(2, a.RestartCount, "an explicit ONCE replay must restart the one-shot gesture");
        }

        [Test]
        public void Animator_DifferentClipOrMode_Restarts()
        {
            var a = Animator("gehen", "laufen");
            a.SetAnim("gehen", 0, DigglesAnimator.AnimMode.Loop);
            a.SetAnim("laufen", 0, DigglesAnimator.AnimMode.Loop);   // clip switch
            Assert.AreEqual(2, a.RestartCount);
            a.SetAnim("laufen", 0, DigglesAnimator.AnimMode.Once);   // mode switch on the same clip
            Assert.AreEqual(3, a.RestartCount);
            a.SetAnim("laufen", 3, DigglesAnimator.AnimMode.Loop);   // explicit seek re-anchors
            Assert.AreEqual(4, a.RestartCount);
        }

        [Test]
        public void Controller_PlayStateLoopReissue_Deduped()
        {
            var a = Animator("gehen_loop");
            var ctrl = a.gameObject.AddComponent<DigglesAnimController>();
            ctrl.Animator = a;
            ctrl.SetTables(new Dictionary<string, string> { ["walk_loop"] = "gehen_loop" }, null);
            Assert.IsTrue(ctrl.PlayState("walk_loop"));
            Assert.IsTrue(ctrl.PlayState("walk_loop"));
            Assert.IsTrue(ctrl.PlayState("walk_loop"));
            Assert.AreEqual(1, a.RestartCount, "PlayState loop churn must reach the animator deduped");
        }

        // ------------------------------------------------------------------
        // 2) Dispatcher failure breathers
        // ------------------------------------------------------------------

        static DigglesTclContext Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            return ctx;
        }

        static DigglesTclEntity Ent(DigglesTclContext ctx, string cls, string kind, Vector3 pos)
        {
            var e = new DigglesTclEntity
            {
                Class = new DigglesClass { Name = cls, Kind = kind },
                Position = pos,
                // Construction default is OWNERLESS (-1, FUN_005c83f0:196); the dispatcher pools
                // and matches per PLAYER owner, so these fabricated fixtures declare the owner
                // the real init chains would assign (the pre--1-law implicit assumption).
                Owner = 0,
            };
            ctx.RegisterEntity(e);
            return e;
        }

        // One open harvest job (demand-gated): an owned production building with a scheduled slot
        // + one ripe Pilz. No terrain/BDB/data needed — the job list is entity-derived.
        static (DigglesTclContext ctx, DigglesTclEntity gnome) HarvestWorld()
        {
            var ctx = Host();
            var mill = Ent(ctx, "Saegewerk", "building", new Vector3(102f, 100f, 8f));
            mill.ProdSlots["Holz"] = 1.0;          // an active slot + no Pilz produce in world → deficit 1
            // Fabricated building → no genericprod obj-init ran, and since the spawn default
            // is UNBUILT (CObj ctor word 0x01100101, bit 0x400000 clear). The harvest demand gate
            // (CountWantedHarvestInputs) requires BuildupComplete, so mark it built explicitly —
            // the same `set_buildupstate this 1` every real building runs (genericprod.tcl:451).
            mill.BuildupComplete = true;
            Ent(ctx, "Pilz", "plant", new Vector3(103f, 100f, 8f));
            var gnome = Ent(ctx, "Zwerg", "gnome", new Vector3(100f, 100f, 8f));
            // The fabricated gnome never ran auto_choose_workingtime, and the FAITHFUL spawn
            // default is now NO work window (CObj ctor zeroes start+duration, FUN_005c83f0:130-132)
            // — the dispatcher's IsWorkTime gate would skip it. Give it an explicit all-day window.
            gnome.WorkStart = 0f; gnome.WorkLen = 12f;
            return (ctx, gnome);
        }

        [Test]
        public void Dispatcher_FailedGnome_KeepsBreather_NotInstantlyReassigned()
        {
            var (ctx, g) = HarvestWorld();
            var disp = ctx.ProdDispatcher!;

            // Dispatch = pool (gnome_idle) + the maintenance pass (SweepNow) — the idle report
            // one-shot FUN_00434bc0 never matches new jobs.
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.AreEqual(2, g.WorkBusyCooldown, "idle gnome + open job must assign (cooldown = 2)");

            // The walk fails instantly → gnome_failed_work → ReleaseGnome.
            disp.ReleaseGnome(g);
            Assert.IsTrue(disp.IsBreathing(g.Handle), "a failed gnome must enter the 5 s re-task embargo");

            // Decay past the assign cooldown only (2 logic ticks) — the breather must still hold.
            disp.DecayCooldowns(); disp.DecayCooldowns();
            Assert.AreEqual(0, g.WorkBusyCooldown);
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.AreEqual(0, g.WorkBusyCooldown,
                "a breathing gnome must NOT be reassigned (the assign→fail→reassign storm)");
            Assert.IsTrue(disp.IsPooled(g.Handle, 0),
                "the breathing gnome stays POOLED (native keeps rec+0x0c==2; only the match scan skips it)");

            // After the full 5.0 s (50 logic ticks) the gnome is matchable again. The failed JOB's
            // 20-tick reopen cooldown has long expired by then too.
            for (int i = 0; i < 50; i++) disp.DecayCooldowns();
            Assert.IsFalse(disp.IsBreathing(g.Handle), "the breather must expire after 50 logic ticks");
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.AreEqual(2, g.WorkBusyCooldown, "after the breather the gnome must take the job again");
        }

        [Test]
        public void Dispatcher_FailedJob_ReopenDelayed_ForOtherGnomesToo()
        {
            var (ctx, g1) = HarvestWorld();
            var disp = ctx.ProdDispatcher!;
            var g2 = Ent(ctx, "Zwerg", "gnome", new Vector3(101f, 100f, 8f));
            g2.WorkStart = 0f; g2.WorkLen = 12f;   // explicit window (spawn default = none, FUN_005c83f0:130-132)

            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.AreEqual(2, g1.WorkBusyCooldown, "g1 must take the only job");
            disp.ReleaseGnome(g1);   // g1 fails it → the JOB cools down for 20 logic ticks

            // A FRESH gnome (no breather) may not pick the just-failed job up while it cools.
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.AreEqual(0, g2.WorkBusyCooldown,
                "the failed job must stay closed for 20 logic ticks (FUN_0043a0c0 target+0x3e8 = 0x14)");

            for (int i = 0; i < 20; i++) disp.DecayCooldowns();
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.AreEqual(2, g2.WorkBusyCooldown, "after the reopen delay the job must dispatch again");
        }

        // ------------------------------------------------------------------
        // 3) MetaWalkAction phase 0
        // ------------------------------------------------------------------

        [Test]
        public void MetaWalk_DegeneratePath_NoClipIssued_CompletesFirstTick()
        {
            var a = Animator("gehen_start", "gehen_loop", "gehen_stop", "standard");
            var ctrl = a.gameObject.AddComponent<DigglesAnimController>();
            ctrl.Animator = a;
            ctrl.SetTables(new Dictionary<string, string>
            {
                ["walk_start"] = "gehen_start",
                ["walk_loop"] = "gehen_loop",
                ["walk_stop"] = "gehen_stop",
                ["standard"] = "standard",
            }, null);
            ctrl.PlayState("standard");
            int restartsBefore = a.RestartCount;

            var body = Go("body");
            body.transform.position = new Vector3(3f, 1f, -10f);
            // A re-walk to the spot the body already occupies: every waypoint within the 0.2
            // arrival radius (the urw_3 "raised leg 0.5 s freeze" shape).
            var pts = new List<Vector3>
            {
                body.transform.position,
                body.transform.position + new Vector3(0.05f, 0f, 0.05f),
            };
            var walk = new MetaWalkAction(ctrl, body.transform, pts, 0, -1, 0);
            bool more = walk.Tick(0.1);
            Assert.IsFalse(more, "a degenerate re-walk must complete within one tick");
            Assert.AreEqual("standard", a.CurrentAnimName, "the running idle anim must be left alone");
            Assert.AreEqual(restartsBefore, a.RestartCount, "no clip may be (re)issued for a degenerate walk");
        }

        [Test]
        public void MetaWalk_FullCruiseFromFirstTick_NoRamp()
        {
            var ctrl = Go("model").AddComponent<DigglesAnimController>();   // bare/clip-less
            var body = Go("body");
            body.transform.position = Vector3.zero;
            // Waypoint ALONG the model's facing (+Z): a ≥π/4 heading error now faithfully
            // pivots in place first (no translation) — here we test the pure phase-0 cruise.
            var pts = new List<Vector3> { new Vector3(0f, 0f, 2f) };
            var walk = new MetaWalkAction(ctrl, body.transform, pts, 0, -1, 0);
            const float tickSeconds = 0.1f;
            walk.Tick(tickSeconds);
            // Clip-less cruise falls back to the mann walk law (16.2218 |VecA| × 0.05 =
            // 0.8111 wu/s, WalkAction.FallbackWalkSpeed) → 0.08111 wu per 0.1 s tick. The old
            // invented 0→cruise ramp moved ~0 on the first tick (t = 0); FUN_00418280 consumes
            // the full per-tick arc length from tick 1.
            float moved = body.transform.position.magnitude;
            float expected = WalkAction.FallbackWalkSpeed * tickSeconds;
            Assert.AreEqual(expected, moved, 0.00001f,
                "tick 1 must consume exactly one full fallback-cruise step without an acceleration ramp");
        }
    }
}
