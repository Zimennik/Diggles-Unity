#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Regression: global anim freeze. DigglesAnimator must stamp clip starts and measure elapsed
    // time in one clock domain (_animClock); mixing in absolute Unity time made every clip look
    // stale at issue, so Once clips finished instantly and loops ran unscaled.
    //
    // Ownership: DigglesAnimator.GlobalTimeScale is published only by the active frame-loop
    // driver (EngineTickService / GameSimDriver). DigglesGameSim.Step must never write it, or a
    // stale or paused sim stomps the global every step and freezes every animator in the world.
    //
    // Tests drive the animator's clock with TickClock (fake render frames) — no Play mode, no
    // Time.* dependence. Frame targets sit mid-frame (e.g. phase 2.5) so float rounding can
    // never flip an assert across a frame boundary.
    public sealed class AnimClockRegressionTests
    {
        readonly List<Object> _trash = new();

        [SetUp]
        public void SetUp() => DigglesAnimator.PublishTimeScale(1.0);

        [TearDown]
        public void TearDown()
        {
            DigglesAnimator.PublishTimeScale(1.0);   // never leak a scale into other suites
            foreach (var o in _trash)
                if (o != null) Object.DestroyImmediate(o);
            _trash.Clear();
        }

        // ----- harness -------------------------------------------------------

        DigglesAnimClip MakeClip(string name, int frames)
        {
            var meshes = new Mesh[frames];
            for (int i = 0; i < frames; i++)
            {
                meshes[i] = new Mesh { name = $"{name}_f{i}" };
                _trash.Add(meshes[i]);
            }
            return new DigglesAnimClip { Name = name, Frames = meshes };
        }

        DigglesAnimator NewAnimator(int frames = 6, string clipName = "clip")
        {
            var go = new GameObject("anim_clock_test");
            _trash.Add(go);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            var anim = go.AddComponent<DigglesAnimator>();
            anim.Interpolate = false;            // hard mesh swaps — no VAT/shader needed headless
            anim.AddClip(MakeClip(clipName, frames));
            return anim;
        }

        // ----- the core clock-domain regression --------------------------------

        // A freshly issued Once clip must start at its start frame and play through — not be
        // born stale, snap to its last frame and go Still at issue time.
        [Test]
        public void OnceClip_StartsAtStartFrame_PlaysThrough_ThenFreezesOnLast()
        {
            var anim = NewAnimator(frames: 6);
            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Once);
            Assert.AreEqual(DigglesAnimator.AnimMode.Once, anim.CurrentMode,
                "a fresh Once must NOT already be finished at issue time");
            Assert.AreEqual(0, anim.CurrentFrame, "a fresh Once poses its start frame");

            anim.TickClock(0.25f);                  // 10 fps → phase 2.5
            Assert.AreEqual(2, anim.CurrentFrame, "the clip ADVANCES with the ticked clock");
            Assert.AreEqual(DigglesAnimator.AnimMode.Once, anim.CurrentMode);

            anim.TickClock(10f);                    // run far past the end
            Assert.AreEqual(5, anim.CurrentFrame, "Once settles on its last frame");
            Assert.AreEqual(DigglesAnimator.AnimMode.Still, anim.CurrentMode,
                "finished Once freezes (engine semantics)");
        }

        // Native CAnimCtrl completion is context-free: Once clamps to its last frame and becomes
        // Still. A cutscene `action anim` therefore keeps that pose after its timed action ends.
        // Gameplay paths that need an idle explicitly schedule it through PlayOnceThen; that
        // higher-level continuation must remain intact so finished work clips do not freeze.
        [Test]
        public void ClipEndPolicy_CutsceneActionHolds_GameplayContinuationFallsBackToIdle()
        {
            var cutsceneAnim = NewAnimator(frames: 6, clipName: "sitdown");
            var cutsceneCtrl = cutsceneAnim.gameObject.AddComponent<DigglesAnimController>();
            cutsceneCtrl.Animator = cutsceneAnim;
            var cutsceneAction = new AnimAction(cutsceneCtrl, "sitdown");

            Assert.That(cutsceneAction.Tick(0.0), Is.True, "the cutscene action starts its clip");
            cutsceneAnim.TickClock(10f);
            Assert.That(cutsceneAction.Tick(10.0), Is.False, "the timed action completes");
            Assert.That(cutsceneAnim.CurrentAnimName, Is.EqualTo("sitdown"),
                "action completion does not order a replacement clip");
            Assert.That(cutsceneAnim.CurrentFrame, Is.EqualTo(5),
                "the completed cutscene Once holds its final pose");
            Assert.That(cutsceneAnim.CurrentMode, Is.EqualTo(DigglesAnimator.AnimMode.Still));

            var gameplayAnim = NewAnimator(frames: 4, clipName: "workstop");
            gameplayAnim.AddClip(MakeClip("standstill", frames: 2));
            var gameplayCtrl = gameplayAnim.gameObject.AddComponent<DigglesAnimController>();
            gameplayCtrl.Animator = gameplayAnim;

            Assert.That(gameplayCtrl.PlayOnceThen("workstop", "standstill"), Is.True);
            gameplayAnim.TickClock(10f);
            Assert.That(gameplayAnim.CurrentAnimName, Is.EqualTo("workstop"));
            Assert.That(gameplayAnim.CurrentMode, Is.EqualTo(DigglesAnimator.AnimMode.Still),
                "raw Once completion still holds until the gameplay continuation runs");

            gameplayCtrl.TickChain(10f);
            Assert.That(gameplayAnim.CurrentAnimName, Is.EqualTo("standstill"),
                "gameplay's explicit fallback remains preserved");
            Assert.That(gameplayAnim.CurrentMode, Is.EqualTo(DigglesAnimator.AnimMode.Loop));
        }

        // Slow-mo end-to-end at the animator: factor 0.3 slows the frame flips to 0.3×, and
        // restoring factor 1 resumes full rate from the held phase (no snap, no wedge).
        [Test]
        public void Scale0p3_SlowsFrameFlips_AndRestoreResumes()
        {
            var anim = NewAnimator(frames: 8);
            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Loop);

            DigglesAnimator.PublishTimeScale(0.3);
            anim.TickClock(1.05f);                  // 1.05 real-s × 0.3 = 0.315 game-s → phase 3.15
            Assert.AreEqual(3, anim.CurrentFrame,
                "factor 0.3 must reach the SCREEN: ~3 frame flips per real second, not 10");

            DigglesAnimator.PublishTimeScale(1.0);
            anim.TickClock(0.2f);                   // +0.2 game-s → phase 5.15
            Assert.AreEqual(5, anim.CurrentFrame,
                "restoring factor 1 resumes full rate from the held phase");
        }

        // gametime stop / factor 0: playback freezes; a clip issued DURING the freeze (cutscene
        // summons get set_anim before their first unfrozen Update) poses its start frame at once
        // and resumes cleanly when time returns — no permanent frame wedge.
        [Test]
        public void ScaleZero_FreezesPlayback_IssueWhileFrozen_PosesStartFrame_ThenResumes()
        {
            var anim = NewAnimator(frames: 6);
            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Loop);
            anim.TickClock(0.15f);                  // phase 1.5
            Assert.AreEqual(1, anim.CurrentFrame);

            DigglesAnimator.PublishTimeScale(0.0);
            for (int i = 0; i < 30; i++) anim.TickClock(0.033f);
            Assert.AreEqual(1, anim.CurrentFrame, "a stopped game clock holds the frame (faithful)");

            anim.SetAnim("clip", 2, DigglesAnimator.AnimMode.Once);
            Assert.AreEqual(2, anim.CurrentFrame, "issued under scale 0, the clip still POSES its start frame");
            for (int i = 0; i < 10; i++) anim.TickClock(0.1f);
            Assert.AreEqual(2, anim.CurrentFrame, "and stays held while frozen");

            DigglesAnimator.PublishTimeScale(1.0);
            anim.TickClock(0.15f);                  // phase 3.5
            Assert.AreEqual(3, anim.CurrentFrame,
                "when time resumes the clip advances from its start frame — no frame-0 wedge");
            Assert.AreEqual(DigglesAnimator.AnimMode.Once, anim.CurrentMode);
        }

        // ----- the SetAnim loop dedupe edges -----------------------------------

        // Think-tick churn protection: re-issuing the loop that is already looping must not
        // rewind it, and the deduped loop must keep advancing rather than wedge.
        [Test]
        public void LoopDedupe_SameClipSameAnchor_KeepsPhase_AndKeepsPlaying()
        {
            var anim = NewAnimator(frames: 6);
            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Loop);
            int restarts = anim.RestartCount;
            anim.TickClock(0.25f);                  // phase 2.5
            Assert.AreEqual(2, anim.CurrentFrame);

            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Loop);   // churn re-issue
            Assert.AreEqual(restarts, anim.RestartCount, "same-loop re-issue dedupes (no restart)");
            Assert.AreEqual(2, anim.CurrentFrame, "dedupe must not rewind the phase");

            anim.TickClock(0.2f);                   // phase 4.5
            Assert.AreEqual(4, anim.CurrentFrame, "the deduped loop keeps advancing — never wedges");
        }

        [Test]
        public void LoopDedupe_ExplicitSeekToAnotherFrame_IsHonoured()
        {
            var anim = NewAnimator(frames: 6);
            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Loop);
            int restarts = anim.RestartCount;
            anim.TickClock(0.15f);

            anim.SetAnim("clip", 4, DigglesAnimator.AnimMode.Loop);   // explicit same-frame seek
            Assert.AreEqual(restarts + 1, anim.RestartCount, "a different anchor frame restarts");
            Assert.AreEqual(4, anim.CurrentFrame, "and poses the seek frame");
        }

        // Once → finished (Still) → the same clip re-issued as a Loop must restart and play;
        // the dedupe must not eat it.
        [Test]
        public void FinishedOnce_ThenSameClipLoopReissue_RestartsAndPlays()
        {
            var anim = NewAnimator(frames: 4);
            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Once);
            anim.TickClock(2f);                     // finish → Still on last frame
            Assert.AreEqual(DigglesAnimator.AnimMode.Still, anim.CurrentMode);
            Assert.AreEqual(3, anim.CurrentFrame);

            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Loop);
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, anim.CurrentMode,
                "a finished Once (sitting in Still) must not dedupe away the loop re-issue");
            Assert.AreEqual(0, anim.CurrentFrame);
            anim.TickClock(0.25f);                  // phase 2.5
            Assert.AreEqual(2, anim.CurrentFrame, "the reissued loop plays");
        }

        // ----- GlobalTimeScale ownership (the two-sim stomp) --------------------

        static DigglesGameSim NewSim()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            return new DigglesGameSim(ctx);
        }

        // DigglesGameSim.Step must never publish the global scale. A stale factor-0 or stopped
        // sim stepping in the background would otherwise freeze every animator each step.
        [Test]
        public void SimStep_NeverWritesGlobalTimeScale()
        {
            DigglesAnimator.PublishTimeScale(1.0);

            var stale = NewSim();
            stale.ClockRunning = false;             // a paused background sim
            stale.TimeFactor = 0.0;
            for (int i = 0; i < 10; i++) stale.Step(0.025);
            Assert.AreEqual(1.0, DigglesAnimator.GlobalTimeScale,
                "a stepped sim must not stomp the global — frozen clock must never come from an inactive sim");

            var slow = NewSim();
            slow.TimeFactor = 0.3;                  // a slow-mo sim that is not the active context
            slow.Step(0.025);
            Assert.AreEqual(1.0, DigglesAnimator.GlobalTimeScale,
                "only the active frame-loop driver publishes — never Step itself");
        }

        // Two sims alive (a stale frozen one plus the active sim in slow-mo): the frame loop
        // steps both, the driver publishes from the active sim only, and the animators run at
        // the active sim's 0.5× — the stale sim cannot freeze the world.
        [Test]
        public void TwoSims_ActiveSimOwnsTheScale_StaleFrozenSimCannotFreezeAnimators()
        {
            var staleSim = NewSim();
            staleSim.ClockRunning = false;          // frozen, no longer the active context
            var activeSim = NewSim();
            activeSim.TimeFactor = 0.5;             // the live context, in slow-mo

            var anim = NewAnimator(frames: 8);
            anim.SetAnim("clip", 0, DigglesAnimator.AnimMode.Loop);

            for (int i = 0; i < 3; i++)             // 3 fake render frames of 0.3 real-s each
            {
                staleSim.Step(0.025);               // background stepping must be inert
                activeSim.Step(0.025);
                // EngineTickService's publish line, verbatim, for the active sim:
                DigglesAnimator.PublishTimeScale(
                    activeSim.ClockRunning ? activeSim.TimeFactor : 0.0);
                anim.TickClock(0.3f);
            }
            // 0.9 real-s × 0.5 = 0.45 game-s → phase 4.5.
            Assert.AreEqual(4, anim.CurrentFrame,
                "animators must run at the ACTIVE sim's scale; the stale frozen sim must not freeze them");
        }

        // ----- the controller's start→loop chain under slow-mo ------------------

        // PlayStartLoop's handoff timer must run on GAME time: under factor 0.3 the start clip
        // plays 3.33× longer, so a real-time timer would cut to the loop at ~30% played.
        [Test]
        public void StartLoopChain_StretchesWithSlowMo()
        {
            var anim = NewAnimator(frames: 5, clipName: "start_clip");   // 0.5 game-s start clip
            anim.AddClip(MakeClip("loop_clip", 4));
            var ctrl = anim.gameObject.AddComponent<DigglesAnimController>();
            ctrl.Animator = anim;

            DigglesAnimator.PublishTimeScale(0.3);
            Assert.IsTrue(ctrl.PlayStartLoop("start_clip", "loop_clip"));
            Assert.AreEqual("start_clip", anim.CurrentAnimName);

            ctrl.TickChain(0.5f);                   // 0.5 real-s = 0.15 game-s < 0.5 — not due yet
            Assert.AreEqual("start_clip", anim.CurrentAnimName,
                "the chain must stretch with the game clock (real-time firing cut start clips short)");

            ctrl.TickChain(1.3f);                   // total 0.54 game-s ≥ 0.5 → handoff
            Assert.AreEqual("loop_clip", anim.CurrentAnimName, "the loop takes over when the start clip is DONE in game time");
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, anim.CurrentMode);
        }
    }
}
