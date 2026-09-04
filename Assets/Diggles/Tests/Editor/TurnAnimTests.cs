#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Reflection;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Regression tests for the faithful turn-in-place animations (decompile ground
    // truth: FUN_00418850 floor-mover gate, FUN_004185a0/FUN_00418730 stationary turn
    // integrator, FUN_00418280 in-stride steering, FUN_00416c00/FUN_00417010
    // CAct_Rotate plan/tick, FUN_00422a50 turn-rate defaults).
    //
    // All tests are data-free (mock clips with baked VecB.z rotation rates) and run
    // headlessly in EditMode at the fixed 0.1 s logic tick — one tick = one native
    // anim frame. The hierarchy mirrors the real spawn: outer "body" GO (moved/yawed
    // by the actions) + inner "Model" child carrying DigglesAnimator+Controller.
    public sealed class TurnAnimTests
    {
        const float LogicTickSeconds = 0.1f;
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

        static DigglesAnimClip Clip(string name, int frames, float rotZ = 0f)
        {
            var c = new DigglesAnimClip
            {
                Name = name,
                Frames = new Mesh[frames],
                RotationDelta = new Vector3(0f, 0f, rotZ),   // 3db VecB — rad per frame-step
            };
            for (int i = 0; i < frames; i++) c.Frames[i] = new Mesh();
            return c;
        }

        // Outer body + inner Model child (animator + controller), with the mann turn
        // clips (real frame counts + VecB.z rates from mann.3db) and the z_anims
        // tables: base map = walk states + the bare aliases (z_anims.tcl:31-37);
        // animset 0 = STANDARDWALK turn slots (:1289-1292), 7 = WALKWITHBOX kiste
        // turns (:1509), 3 = a swim-like set whose turn slots map to a clip
        // with no baked rotation (:1379-1382).
        (GameObject body, DigglesAnimController ctrl, DigglesAnimator anim) Rig()
        {
            var body = Go("body");
            var model = new GameObject("Model");
            model.transform.SetParent(body.transform, false);
            var anim = model.AddComponent<DigglesAnimator>();
            var ctrl = model.AddComponent<DigglesAnimController>();
            ctrl.Animator = anim;
            anim.AddClip(Clip("standard", 2));
            anim.AddClip(Clip("gehen_start", 6));
            anim.AddClip(Clip("gehen_loop", 10));
            anim.AddClip(Clip("gehen_end", 6));
            anim.AddClip(Clip("drehen_links", 4, +0.5265f));
            anim.AddClip(Clip("drehen_rechts", 4, -0.5236f));
            anim.AddClip(Clip("drehen_ganz", 5, +0.7854f));        // right-180 slot; art sign sloppy — engine abses
            anim.AddClip(Clip("drehen_ganz_links", 5, -0.7854f));
            anim.AddClip(Clip("kiste_drehen_links", 7, +0.26f));
            anim.AddClip(Clip("kiste_drehen_rechts", 7, -0.26f));
            anim.AddClip(Clip("schwimmen_loop", 8, 0f));
            var baseMap = new Dictionary<string, string>
            {
                ["walk_start"] = "gehen_start",
                ["walk_loop"] = "gehen_loop",
                ["walk_stop"] = "gehen_end",
                ["standard"] = "standard",
                ["turnright"] = "drehen_rechts",
                ["turnleft"] = "drehen_links",
                ["turn180right"] = "drehen_ganz",
                ["turn180left"] = "drehen_ganz_links",
            };
            var sets = new Dictionary<int, Dictionary<string, string>>
            {
                [0] = new()
                {
                    ["turn_left_90"] = "drehen_links",
                    ["turn_right_90"] = "drehen_rechts",
                    ["turn_left_180"] = "drehen_ganz_links",
                    ["turn_right_180"] = "drehen_ganz",
                },
                [7] = new()
                {
                    ["turn_left_90"] = "kiste_drehen_links",
                    ["turn_right_90"] = "kiste_drehen_rechts",
                    ["turn_left_180"] = "kiste_drehen_links",
                    ["turn_right_180"] = "kiste_drehen_rechts",
                },
                [3] = new()
                {
                    ["turn_left_90"] = "schwimmen_loop",
                    ["turn_right_90"] = "schwimmen_loop",
                    ["turn_left_180"] = "schwimmen_loop",
                    ["turn_right_180"] = "schwimmen_loop",
                },
            };
            ctrl.SetTables(baseMap, sets);
            return (body, ctrl, anim);
        }

        // World direction `deg` degrees clockwise (about +Y) from the model's spawn
        // facing (+Z). Positive = Unity SignedAngle > 0 = the port's right-turn slots.
        static Vector3 DirAt(float deg) =>
            new Vector3(Mathf.Sin(deg * Mathf.Deg2Rad), 0f, Mathf.Cos(deg * Mathf.Deg2Rad));

        // Current visual facing yaw (degrees from +Z) of the inner model.
        static float FacingYawDeg(DigglesAnimController ctrl)
        {
            Vector3 f = ctrl.transform.rotation * Vector3.forward;
            f.y = 0f;
            return Vector3.SignedAngle(Vector3.forward, f.normalized, Vector3.up);
        }

        MetaWalkAction Walk(DigglesAnimController ctrl, Transform body, float deg,
            float dist = 2f, int animset = 0)
        {
            var pts = new List<Vector3> { body.position + DirAt(deg) * dist };
            return new MetaWalkAction(ctrl, body, pts, animset, -1, 0);
        }

        // ------------------------------------------------------------------
        // MetaWalk: the π/4 in-place turn gate (FUN_00418850:93)
        // ------------------------------------------------------------------

        [Test]
        public void MetaWalk_NoTurnPhase_Below45Deg()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 40f);
            walk.Tick(LogicTickSeconds);
            Assert.AreEqual("gehen_start", anim.CurrentAnimName,
                "a 40° heading error is below the π/4 gate — walk_start plays, no pivot");
            Assert.AreEqual(WalkAction.FallbackWalkSpeed * LogicTickSeconds,
                body.transform.position.magnitude, 0.00001f,
                "below the gate the body must consume one full fallback-cruise step on the first tick");
        }

        [Test]
        public void MetaWalk_TurnPhase_At50Deg_TranslationFrozen_ClipLoops()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 50f);
            walk.Tick(0.1);
            Assert.AreEqual("drehen_rechts", anim.CurrentAnimName,
                "≥ π/4 error to the RIGHT must loop the animset's turn_right_90 clip");
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, anim.CurrentMode,
                "turn slots 2-5 play LOOPING (FUN_00419c60:91-95 mode 2)");
            Assert.AreEqual(Vector3.zero, body.transform.position,
                "the in-place turn never touches position (FUN_00418730)");
        }

        [Test]
        public void MetaWalk_TurnPair_180Beyond135Deg_90Below()
        {
            var (b1, c1, a1) = Rig();
            Walk(c1, b1.transform, 150f).Tick(0.1);
            Assert.AreEqual("drehen_ganz", a1.CurrentAnimName, "150° right → turn_right_180");

            var (b2, c2, a2) = Rig();
            Walk(c2, b2.transform, 100f).Tick(0.1);
            Assert.AreEqual("drehen_rechts", a2.CurrentAnimName, "100° (≤ 3π/4) right → turn_right_90");

            var (b3, c3, a3) = Rig();
            Walk(c3, b3.transform, -150f).Tick(0.1);
            Assert.AreEqual("drehen_ganz_links", a3.CurrentAnimName, "150° left → turn_left_180");

            var (b4, c4, a4) = Rig();
            Walk(c4, b4.transform, -50f).Tick(0.1);
            Assert.AreEqual("drehen_links", a4.CurrentAnimName, "50° left → turn_left_90");
        }

        // ------------------------------------------------------------------
        // MetaWalk: yaw rate = the clip's baked VecB.z per 0.1 s logic tick
        // ------------------------------------------------------------------

        [Test]
        public void MetaWalk_TurnYawRate_MatchesClipRotationDelta_PerLogicTick()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 90f);   // drehen_rechts, |VecB.z| = 0.5236

            walk.Tick(0.1);
            Assert.AreEqual(0.5236f * Mathf.Rad2Deg, FacingYawDeg(ctrl), 0.1f,
                "one logic tick must yaw exactly |clip VecB.z| rad (FUN_00418730 step = +0x34)");
            Assert.AreEqual(Vector3.zero, body.transform.position);

            walk.Tick(0.1);
            Assert.AreEqual(2f * 0.5236f * Mathf.Rad2Deg, FacingYawDeg(ctrl), 0.1f);
            Assert.AreEqual(Vector3.zero, body.transform.position);

            walk.Tick(0.1);   // remaining ≈ one step → exact snap (90° turn = 3 ticks = 0.3 s)
            Assert.AreEqual(90f, FacingYawDeg(ctrl), 0.01f,
                "the turn must SNAP exactly onto the travel heading");
            Assert.AreEqual(Vector3.zero, body.transform.position,
                "translation stays frozen through the snap tick");

            walk.Tick(LogicTickSeconds);   // turn done → walk_start replays, translation resumes
            Assert.AreEqual("gehen_start", anim.CurrentAnimName,
                "after the snap walk_start must replay (FUN_00418850:94-96 re-arm)");
            Assert.AreEqual(WalkAction.FallbackWalkSpeed * LogicTickSeconds,
                body.transform.position.magnitude, 0.00001f,
                "translation must resume with one full fallback-cruise step right after the turn");
        }

        [Test]
        public void MetaWalk_TurnRate_DefaultsWhenClipHasNoRotation()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 60f, animset: 3);   // swim turn slots → zero-rotation clip
            walk.Tick(0.1);
            Assert.AreEqual("schwimmen_loop", anim.CurrentAnimName);
            Assert.AreEqual(0.1f * Mathf.Rad2Deg, FacingYawDeg(ctrl), 0.1f,
                "a rotation-less 90-pair clip falls back to 0.1 rad/tick (FUN_00422a50:174-185)");
        }

        // ------------------------------------------------------------------
        // MetaWalk: sharp corner mid-walk → stop, pivot, replay walk_start
        // ------------------------------------------------------------------

        [Test]
        public void MetaWalk_SharpCorner_StopsPivots_ReplaysWalkStart_ThenFinishes()
        {
            var (body, ctrl, anim) = Rig();
            Vector3 corner = new Vector3(0f, 0f, 2f);
            Vector3 end = new Vector3(2f, 0f, 2f);                 // 90° right bend
            var pts = new List<Vector3> { corner, end };
            var walk = new MetaWalkAction(ctrl, body.transform, pts, 0, -1, 0);

            var seq = new List<string>();
            bool more = true; int guard = 0;
            while (more && guard++ < 500)
            {
                more = walk.Tick(0.1);
                string cur = anim.CurrentAnimName;
                if (seq.Count == 0 || seq[seq.Count - 1] != cur) seq.Add(cur);
                if (cur == "drehen_rechts")
                    Assert.Less((body.transform.position - corner).magnitude, 0.25f,
                        "during the corner pivot the body must stay pinned at the bend");
            }
            Assert.Less(guard, 500, "walk never finished");

            int loop1 = seq.IndexOf("gehen_loop");
            int turn = seq.IndexOf("drehen_rechts");
            int start2 = seq.IndexOf("gehen_start", turn < 0 ? 0 : turn);
            Assert.AreEqual("gehen_start", seq[0], "walk begins with walk_start");
            Assert.Greater(turn, loop1, "the pivot happens at the corner, after cruising began");
            Assert.Greater(start2, turn, "walk_start must REPLAY after the pivot, before walk_loop resumes");
            Assert.AreEqual("standard", anim.CurrentAnimName, "the walk settles to the animset idle");
            Assert.Less((body.transform.position - end).magnitude, 0.05f, "the walk must still arrive");
        }

        // ------------------------------------------------------------------
        // MetaWalk: in-stride steering (FUN_00418280) — 0.1 rad/tick + 0.051 snap
        // ------------------------------------------------------------------

        [Test]
        public void MetaWalk_Steering_DriftClampedAt0p1RadPerTick()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 40f, dist: 20f);   // below the gate → steer in stride
            walk.Tick(LogicTickSeconds);
            Assert.AreEqual(0.1f * Mathf.Rad2Deg, FacingYawDeg(ctrl), 0.1f,
                "in-stride drift is ±0.1 rad per logic tick (DAT_00737034) — not 540°/s");
            Assert.AreEqual(WalkAction.FallbackWalkSpeed * LogicTickSeconds,
                body.transform.position.magnitude, 0.00001f,
                "steering must preserve one full fallback-cruise step");
        }

        [Test]
        public void MetaWalk_Steering_SnapsWithin0p051Rad()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 2.5f, dist: 20f);  // 0.0436 rad ≤ 0.051 → snap
            walk.Tick(0.1);
            Assert.AreEqual(2.5f, FacingYawDeg(ctrl), 0.01f,
                "≤ 0.051 rad error must snap exactly onto the travel heading (_DAT_00737014)");
        }

        // ------------------------------------------------------------------
        // MetaWalk: turn clips resolve through the active animset slots
        // ------------------------------------------------------------------

        [Test]
        public void MetaWalk_TurnClip_FromActiveAnimsetSlots()
        {
            var (body, ctrl, anim) = Rig();
            Walk(ctrl, body.transform, 90f, animset: 7).Tick(0.1);   // WALKWITHBOX
            Assert.AreEqual("kiste_drehen_rechts", anim.CurrentAnimName,
                "a box carrier must turn with the animset-7 kiste clip, not the bare drehen");
        }

        [Test]
        public void MetaWalk_TurnClip_AliasFallback_WhenAnimsetLacksSlots()
        {
            var (body, ctrl, anim) = Rig();
            Walk(ctrl, body.transform, 90f, animset: 5).Tick(0.1);   // animset 5 undefined
            Assert.AreEqual("drehen_rechts", anim.CurrentAnimName,
                "missing animset slots fall back to the class-anim aliases (turnright → drehen_rechts)");
        }

        // ------------------------------------------------------------------
        // TurnToFaceAction = faithful CAct_Rotate (FUN_00416c00 / FUN_00417010)
        // ------------------------------------------------------------------

        DigglesObject DObj(GameObject body)
        {
            var d = body.AddComponent<DigglesObject>();
            // EditMode never calls Awake on plain MonoBehaviours — run it so
            // SpawnRotation/SpawnFaceWorld get captured (identity / +Z here).
            typeof(DigglesObject).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(d, null);
            return d;
        }

        static int TicksToFinish(IEntityAction a, int max = 100)
        {
            for (int i = 1; i <= max; i++) if (!a.Tick(0.1)) return i;
            return -1;
        }

        [Test]
        public void TurnToFace_DurationProportionalToAngle()
        {
            // 170° → 180 pair, drehen_ganz (5 frames): step = π/5, n = round(2.967/0.628) = 5 ticks.
            var (b1, c1, _) = Rig();
            var d1 = DObj(b1);
            int t170 = TicksToFinish(new TurnToFaceAction(d1, c1, Quaternion.AngleAxis(170f, Vector3.up)));
            Assert.AreEqual(5, t170, "170° must take 5 logic ticks ((π)/5-frame clip = 36°/tick)");

            // 90° → 90 pair, drehen_rechts (4 frames): step = (π/2)/4, n = 4 ticks.
            var (b2, c2, _) = Rig();
            var d2 = DObj(b2);
            int t90 = TicksToFinish(new TurnToFaceAction(d2, c2, Quaternion.AngleAxis(90f, Vector3.up)));
            Assert.AreEqual(4, t90, "90° must take 4 logic ticks ((π/2)/4-frame clip = 22.5°/tick)");

            // 36° → 90 pair: n = round(0.628/0.393) = 2 ticks.
            var (b3, c3, _) = Rig();
            var d3 = DObj(b3);
            int t36 = TicksToFinish(new TurnToFaceAction(d3, c3, Quaternion.AngleAxis(36f, Vector3.up)));
            Assert.AreEqual(2, t36, "36° must take 2 logic ticks — duration ∝ angle, not one clip length");
        }

        [Test]
        public void TurnToFace_LoopsClip_SnapsExactly_SettlesToStandard()
        {
            var (body, ctrl, anim) = Rig();
            var d = DObj(body);
            var target = Quaternion.AngleAxis(170f, Vector3.up);
            var act = new TurnToFaceAction(d, ctrl, target);

            Assert.IsTrue(act.Tick(0.1));
            Assert.AreEqual("drehen_ganz", anim.CurrentAnimName, "the 180-pair clip drives the beat");
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, anim.CurrentMode,
                "the turn clip LOOPS during the rotate (FUN_00417010:54-74) — Once froze the pose");
            Assert.AreEqual(170f / 5f, FacingYawDeg(ctrl), 0.1f,
                "constant angular step per tick (step = |delta|/n)");

            int guard = 0;
            while (act.Tick(0.1) && guard++ < 50) { }
            Assert.Less(guard, 50, "rotate never finished");
            Assert.Less(Quaternion.Angle(body.transform.rotation, target), 0.01f,
                "the rotate must SNAP exactly onto the target facing");
            Assert.AreEqual("standard", anim.CurrentAnimName,
                "after the snap the action settles to the `standard` idle (FUN_00417010:48-50)");
            Assert.AreEqual(DigglesAnimator.AnimMode.Loop, anim.CurrentMode);
        }

        [Test]
        public void TurnToFace_TinyAngle_FinishesImmediately_NoSpin()
        {
            var (body, ctrl, anim) = Rig();
            var d = DObj(body);
            var target = Quaternion.AngleAxis(3f, Vector3.up);
            var act = new TurnToFaceAction(d, ctrl, target);
            Assert.IsFalse(act.Tick(0.1), "an angle that rounds to 0 ticks snaps immediately (n < 1)");
            Assert.Less(Quaternion.Angle(body.transform.rotation, target), 0.01f);
            Assert.AreEqual("standard", anim.CurrentAnimName, "settled straight to idle");
        }

        // ------------------------------------------------------------------
        // TurnToFaceAction termination guards: the troll cutscene start_002 showed an
        // eternal turn — trolls have no drehen slots/aliases and no `standard` clip,
        // so every exit path must still terminate and must not leave a turn clip looping.
        // ------------------------------------------------------------------

        [Test]
        public void TurnToFace_ClipLessRig_TerminatesOnThe4FrameDefault()
        {
            // A troll-like rig: controller with no clips and no tables at all. The plan must
            // fall back to the FUN_00416c00 4-frame preset (nominal = (π/2)/4) and finish in
            // n = round(remain/nominal) ticks — never spin.
            var body = Go("body");
            var model = new GameObject("Model");
            model.transform.SetParent(body.transform, false);
            var ctrl = model.AddComponent<DigglesAnimController>();   // bare
            var d = DObj(body);
            var target = Quaternion.AngleAxis(112.5f, Vector3.up);    // = 5 × 22.5° → n = 5
            var act = new TurnToFaceAction(d, ctrl, target);
            int t = TicksToFinish(act);
            Assert.AreEqual(5, t, "a clip-less turn must plan on the 4-frame default and finish");
            Assert.Less(Quaternion.Angle(body.transform.rotation, target), 0.01f,
                "the clip-less turn must still SNAP exactly onto the target");
        }

        [Test]
        public void TurnToFace_ZeroDtTicking_NeverInfinite()
        {
            // dt-starved ticking (a stalled clock / zero `gametime factor`) must hit the hard
            // call cap and settle with the exact target facing instead of spinning forever.
            var (body, ctrl, anim) = Rig();
            var d = DObj(body);
            var target = Quaternion.AngleAxis(90f, Vector3.up);
            var act = new TurnToFaceAction(d, ctrl, target);
            bool finished = false;
            for (int i = 0; i < 1500 && !finished; i++) finished = !act.Tick(0.0);
            Assert.IsTrue(finished, "zero-dt ticking must terminate via the hard call cap");
            Assert.Less(Quaternion.Angle(body.transform.rotation, target), 0.01f,
                "the guard exit must still apply the exact target facing");
        }

        [Test]
        public void TurnToFace_NoIdleResolvable_TurnClipDoesNotLoopForever()
        {
            // Rig with turn clips but with no `standard`/`standstill` idle anywhere (the troll
            // shape, had it inherited turn slots): after the snap the looping drehen must be
            // downgraded to Once — the eternal-turn-animation symptom.
            var body = Go("body");
            var model = new GameObject("Model");
            model.transform.SetParent(body.transform, false);
            var anim = model.AddComponent<DigglesAnimator>();
            var ctrl = model.AddComponent<DigglesAnimController>();
            ctrl.Animator = anim;
            anim.AddClip(Clip("drehen_rechts", 4, -0.5236f));
            ctrl.SetTables(new Dictionary<string, string> { ["turnright"] = "drehen_rechts" }, null);
            var d = DObj(body);
            var act = new TurnToFaceAction(d, ctrl, Quaternion.AngleAxis(90f, Vector3.up));
            int guard = 0;
            while (act.Tick(0.1) && guard++ < 100) { }
            Assert.Less(guard, 100, "rotate never finished");
            Assert.AreEqual("drehen_rechts", anim.CurrentAnimName);
            Assert.AreEqual(DigglesAnimator.AnimMode.Once, anim.CurrentMode,
                "with no idle resolvable the settled turn clip must play ONCE (hold), not loop forever");
        }

        [Test]
        public void TurnToFace_HonorsAnimsetArg_SlotsOverAliases()
        {
            // `action <obj> rotate "<ang> 7"` — the optional animset id selects the slot set
            // (FUN_00416da0:64-77): a box carrier rotates with kiste_drehen_*, 7 frames → 7 ticks.
            var (body, ctrl, anim) = Rig();
            var d = DObj(body);
            var act = new TurnToFaceAction(d, ctrl, Quaternion.AngleAxis(90f, Vector3.up), animset: 7);
            Assert.IsTrue(act.Tick(0.1));
            Assert.AreEqual("kiste_drehen_rechts", anim.CurrentAnimName,
                "the animset arg must route the turn through that set's slots");
            int more = TicksToFinish(act);
            Assert.AreEqual(6, more, "7-frame clip → (π/2)/7 per tick → 7 ticks total (1 spent above)");
        }
    }
}
