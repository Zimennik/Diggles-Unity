#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    ///12 regression tests for the FAITHFUL movement speeds. Decompile ground truth:
    // • walk→run promotion is TIME-based, only for speedtype 0, only when the run animset
    // exists: FUN_004234a0 (gait select; speedtype 2 ⇒ run now, speedtype 0 arms +0x7be),
    // FUN_00418280:107 (armed && legTicks > 0x14 ⇒ FUN_00423470 ⇒ run iff +0x1ec ≠ -1).
    // The old "RunDistance travelled" gate was an invention (plus a scene-serialized copy
    // that silently diverged from the code default).
    // • TRUE ROOT-MOTION SPEED (the unit law, solved — replaces the body-speed
    // model): floor advance/tick = 0.1 × rm(ACTIVE walk slot 6/7/8) (FUN_00418280:59-62);
    // climb advance/tick = 0.1 × 0.25 × Σ rm(slots 9..12 = climb_up/down/right/left,
    // FUN_00401010 registration order; DAT_00737058 = 0.25). rm = the slot record's CACHED
    // cruise (record[+8], FUN_00423270) = clip |VecA| × 0.05, in native tiles/s — settled by
    // the LIVE MEMORY DUMP slot records (walk bank 0.811087 = 16.2218×0.05; run bank
    // 1.714336 = 34.2867×0.05; climb slots = klettern |VecA|s × 0.05; all three walk-bank
    // slots cache the LOOP value — bank uniformity). The earlier ×0.125 start_001-wait
    // calibration was a misread (the wait doesn't bound the full crossing) and ran the
    // world 2.5× fast. See DigglesAnimController.NativeArcToWorld.
    // • the ANIMSET decides the speed bank: `do_action run` = `action walk -animsets 1` with
    // default speedtype — it must cruise at RUN speed from the first tick (speedtype only
    // selects promotion behaviour, FUN_004234a0).
    // • the climb MOUNT (FUN_00418db0 phase machine) is a held STATIONARY ground_to_wall
    // one-shot at the wall-base floor cell (the clip's VecA = 0) — never a mid-air approach.
    // • climb facing: INTO the wall (the Tcl wall pose roty=π → world +Z; z_procs.tcl:1707),
    // laterals re-binned for that facing (dx>0 ⇒ climb_right).
    // All data-free (mock clips with baked |VecA| root motions), ticked at the 0.1 s logic step.
    public sealed class MoveSpeedTests
    {
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        static DigglesAnimClip Clip(string name, int frames, Vector3 vecA = default)
        {
            var c = new DigglesAnimClip { Name = name, Frames = new Mesh[frames], MotionOffset = vecA };
            for (int i = 0; i < frames; i++) c.Frames[i] = new Mesh();
            return c;
        }

        // Outer body + inner Model child, with the mann walk/run/climb clips carrying the REAL
        // |VecA| root motions (vecA probe of mann.3db,) and the z_anims animset maps:
        // 0 = STANDARDWALK (walk + climb slots), 1 = RUN.
        (GameObject body, DigglesAnimController ctrl, DigglesAnimator anim) Rig()
        {
            var body = new GameObject("body");
            _gos.Add(body);
            var model = new GameObject("Model");
            model.transform.SetParent(body.transform, false);
            var anim = model.AddComponent<DigglesAnimator>();
            var ctrl = model.AddComponent<DigglesAnimController>();
            ctrl.Animator = anim;
            anim.AddClip(Clip("standard", 2));
            anim.AddClip(Clip("gehen_start", 6, new Vector3(0f, -16.2218f, 0f)));
            anim.AddClip(Clip("gehen_loop", 10, new Vector3(0f, -16.2218f, 0f)));
            anim.AddClip(Clip("gehen_end", 6));
            anim.AddClip(Clip("laufen_schnell_start", 3, new Vector3(0f, -34.2867f, 0f)));
            anim.AddClip(Clip("laufen_schnell_loop", 10, new Vector3(0f, -34.2867f, 0f)));
            anim.AddClip(Clip("laufen_schnell_end", 6));
            anim.AddClip(Clip("kletterstand", 12));
            anim.AddClip(Clip("stand_zu_kletterstand", 9));           // mount — VecA = 0 (stationary)
            anim.AddClip(Clip("kletterstand_zu_stand", 9));           // dismount — VecA = 0
            anim.AddClip(Clip("kletter_hoch_loop", 19, new Vector3(0f, 0f, 8.3571f)));
            anim.AddClip(Clip("kletter_runter_loop", 19, new Vector3(0f, 0f, -8.2914f)));
            anim.AddClip(Clip("kletter_links_loop", 19, new Vector3(8.2883f, 0f, 0f)));
            anim.AddClip(Clip("kletter_rechts_loop", 19, new Vector3(-8.3065f, 0f, 0f)));
            var baseMap = new Dictionary<string, string>
            {
                ["standard"] = "standard",
                ["walk_start"] = "gehen_start",
                ["walk_loop"] = "gehen_loop",
                ["walk_stop"] = "gehen_end",
            };
            var sets = new Dictionary<int, Dictionary<string, string>>
            {
                [0] = new()
                {
                    ["walk_start"] = "gehen_start",
                    ["walk_loop"] = "gehen_loop",
                    ["walk_stop"] = "gehen_end",
                    ["climb_standard"] = "kletterstand",
                    ["climb_up"] = "kletter_hoch_loop",
                    ["climb_down"] = "kletter_runter_loop",
                    ["climb_left"] = "kletter_links_loop",
                    ["climb_right"] = "kletter_rechts_loop",
                    ["ground_to_wall"] = "stand_zu_kletterstand",
                    ["wall_to_ground"] = "kletterstand_zu_stand",
                },
                [1] = new()
                {
                    ["walk_start"] = "laufen_schnell_start",
                    ["walk_loop"] = "laufen_schnell_loop",
                    ["walk_stop"] = "laufen_schnell_end",
                },
            };
            ctrl.SetTables(baseMap, sets);
            return (body, ctrl, anim);
        }

        // The native-law speeds of the rig's clips (== mann's real numbers, |VecA| × 0.05 —
        // the live-dump slot values: 0.811087 / 1.714336 / 0.41554):
        const float ExpectedWalkSpeed = 16.2218f * 0.05f;     // 0.8111 wu/s (slot rec @0x2d07d1d0)
        const float ExpectedRunSpeed = 34.2867f * 0.05f;      // 1.7143 wu/s (slot rec @0x2d07d3b4)
        static float ExpectedClimbSpeed =>                     // 0.4155 wu/s (FUN_00418db0:74-82)
            0.25f * (8.3571f + 8.2914f + 8.2883f + 8.3065f) * 0.05f;

        // ------------------------------------------------------------------
        // MetaWalk: the walk→run promotion law
        // ------------------------------------------------------------------

        static MetaWalkAction Walk(DigglesAnimController ctrl, Transform body, int speedtype)
            => new MetaWalkAction(ctrl, body,
                new List<Vector3> { body.position + Vector3.forward * 50f }, 0, 1, speedtype);

        [Test]
        public void Speedtype0_PromotesToRun_After20LogicTicks()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 0);
            for (int t = 1; t <= 19; t++) walk.Tick(0.1);
            Assert.AreEqual("gehen_loop", anim.CurrentAnimName,
                "at 1.9 s the gnome must still WALK (promotion is 0x14 ticks = 2.0 s, FUN_00418280:107)");
            walk.Tick(0.1);   // tick 20 — stride time hits 2.0 s
            Assert.AreEqual("laufen_schnell_start", anim.CurrentAnimName,
                "after 20 logic ticks in stride a speedtype-0 walk breaks into the RUN bank (FUN_00423470)");
        }

        [Test]
        public void Speedtype1_NeverPromotes()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 1);
            for (int t = 1; t <= 40; t++) walk.Tick(0.1);
            Assert.AreEqual("gehen_loop", anim.CurrentAnimName,
                "speedtype 1 never arms the promotion (FUN_004234a0 sets +0x7be only for speedtype 0)");
        }

        [Test]
        public void Speedtype0_NoRunAnimset_NeverPromotes()
        {
            var (body, ctrl, anim) = Rig();
            var walk = new MetaWalkAction(ctrl, body.transform,
                new List<Vector3> { body.transform.position + Vector3.forward * 50f }, 0, -1, 0);
            for (int t = 1; t <= 40; t++) walk.Tick(0.1);
            Assert.AreEqual("gehen_loop", anim.CurrentAnimName,
                "fast=-1 (tired/pannier/box) forbids the run — FUN_00423470 gates on +0x1ec ≠ -1");
        }

        [Test]
        public void Speedtype2_RunsFromTheFirstStep()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 2);
            walk.Tick(0.1);
            Assert.AreEqual("laufen_schnell_start", anim.CurrentAnimName,
                "speedtype 2 (double-click / event_repeat) starts in the RUN bank (FUN_004234a0:10-12)");
        }

        // ------------------------------------------------------------------
        // Climb: speed = walk base × the native budget ratio; facing + laterals
        // ------------------------------------------------------------------

        static ClimbAction Climb(DigglesAnimController ctrl, Transform body, params Vector3[] wallPts)
        {
            var path = new List<NavWaypoint>();
            foreach (var p in wallPts) path.Add(new NavWaypoint(p, true, NavWorldService.WallOutward));
            return new ClimbAction(ctrl, body, path, 0, 1, 0);
        }

        [Test]
        public void ClimbSpeed_IsNativeLaw_QuarterSumVecA_TimesArcScale()
        {
            var (body, ctrl, anim) = Rig();
            var climb = Climb(ctrl, body.transform, new Vector3(0f, -2f, 0f), new Vector3(0f, -4f, 0f));
            Vector3 start = body.transform.position;
            climb.Tick(0.1);                                   // Enter → Advance (no motion yet)
            for (int t = 0; t < 10; t++) climb.Tick(0.1);      // 1.0 s of advancing
            float moved = Vector3.Distance(start, body.transform.position);
            Assert.AreEqual(ExpectedClimbSpeed, moved, 0.02f,
                "climb cruise must be 0.25 × Σ|VecA|(kletter up/down/left/right) × 0.05 wu/s " +
                "(FUN_00418db0:74-82; live-dump slot values) — ≈0.4155 wu/s, no hand-tuned factor");
            Assert.AreEqual("kletter_runter_loop", anim.CurrentAnimName,
                "a descending leg plays climb_down");
        }

        // ------------------------------------------------------------------
        // Walk/run cruise = the clip's |VecA| × 0.05 (the slot-cruise law, dump-verified)
        // ------------------------------------------------------------------

        [Test]
        public void WalkCruise_FollowsClipRootMotion_NativeLaw()
        {
            var (body, ctrl, anim) = Rig();
            var walk = Walk(ctrl, body.transform, 1);          // speedtype 1 — never promotes
            Vector3 start = body.transform.position;
            for (int t = 0; t < 10; t++) walk.Tick(0.1);       // 1.0 s (gehen_start rm == loop rm)
            float moved = Vector3.Distance(start, body.transform.position);
            Assert.AreEqual(ExpectedWalkSpeed, moved, 0.01f,
                "walk cruise must be |VecA|(gehen_loop) × 0.05 = 0.811 wu/s (live-dump slot 7)");
        }

        [Test]
        public void RunBank_Animset1_CruisesAtRunSpeed_FromTheFirstTick()
        {
            // The cutscene `do_action run` = `action walk -animsets 1` with DEFAULT speedtype:
            // the ANIMSET decides the speed bank (its walk_loop = laufen_schnell_loop), speedtype
            // only governs promotion. The old cruise keyed off speedtype and ran the run bank at
            // walk speed — the start_001 "gnomes stopped short of crossing" Play symptom.
            var (body, ctrl, anim) = Rig();
            var walk = new MetaWalkAction(ctrl, body.transform,
                new List<Vector3> { body.transform.position + Vector3.forward * 50f }, 1, -1, 0);
            Vector3 start = body.transform.position;
            walk.Tick(0.1);
            Assert.AreEqual("laufen_schnell_start", anim.CurrentAnimName,
                "animset 1's walk_start IS the run bank's start clip");
            for (int t = 0; t < 9; t++) walk.Tick(0.1);        // 1.0 s total
            float moved = Vector3.Distance(start, body.transform.position);
            Assert.AreEqual(ExpectedRunSpeed, moved, 0.02f,
                "the run bank must cruise at |VecA|(laufen) × 0.05 = 1.714 wu/s IMMEDIATELY — " +
                "no walk-speed crawl, no 2 s promotion wait");
        }

        // ------------------------------------------------------------------
        // The start_001 pen-swap pin — REINTERPRETED under the dump law. The runners cover
        // 1.714 wu/s × 5.1 s ≈ 8.74 wu during the `do_wait time 5.1` beat and KEEP RUNNING
        // through the subsequent beats (the wait never bounded the full 21.4-wu crossing;
        // reading it as a bound was the ×0.125 miscalibration that ran the world 2.5× fast).
        // The pin now asserts the law distance, both bounds.
        // ------------------------------------------------------------------

        [Test]
        public void Start001_RunBank_CoversTheLawDistance_DuringThe5p1sBeat()
        {
            var (body, ctrl, anim) = Rig();
            Vector3 start = body.transform.position;
            Vector3 target = start + Vector3.forward * 21.4086f;   // the full pen gap
            var walk = new MetaWalkAction(ctrl, body.transform,
                new List<Vector3> { target }, 1, -1, 0);       // do_action run = -animsets 1
            for (int t = 1; t <= 51; t++) walk.Tick(0.1);      // the authored 5.1 s beat
            float travelled = Vector3.Distance(start, body.transform.position);
            float law = ExpectedRunSpeed * 5.1f;               // ≈ 8.74 wu
            Assert.AreEqual(law, travelled, 0.15f,
                "during start_001's 5.1 s beat a runner covers v_run × 5.1 ≈ 8.74 wu " +
                "(live-dump slot cruise 1.714 wu/s) — neither stalled nor 2.5× overdriven");
        }

        // ------------------------------------------------------------------
        // Climb mount: stationary ground_to_wall at the wall-base floor cell (symptom (b):
        // gnomes mounted from mid-air over the pit center via the removed Approach leg)
        // ------------------------------------------------------------------

        [Test]
        public void ClimbMount_PlaysGroundToWall_StationaryAtTheWallBase()
        {
            var (body, ctrl, anim) = Rig();
            // BuildWalkSegments shape: leading boundary FLOOR wp (the walk's exit anchor at the
            // wall base), wall records DOWN the face, trailing claimed floor record.
            Vector3 baseFloor = new Vector3(0f, 0f, -0.5f);
            body.transform.position = baseFloor;               // the floor segment delivered us here
            var path = new List<NavWaypoint>
            {
                new(baseFloor, false),
                new(new Vector3(0f, -1f, 0f), true, NavWorldService.WallOutward),
                new(new Vector3(0f, -2f, 0f), true, NavWorldService.WallOutward),
                new(new Vector3(0f, -2.2f, -0.4f), false),
            };
            var climb = new ClimbAction(ctrl, body.transform, path, 0, -1, 0);

            climb.Tick(0.1);                                   // Enter → Mount
            Assert.AreEqual("stand_zu_kletterstand", anim.CurrentAnimName,
                "the mount must play ground_to_wall (FUN_00418db0 phase 0, slot 0x10)");
            Assert.AreEqual(baseFloor, body.transform.position,
                "the mount is STATIONARY at the wall-base floor cell — VecA(stand_zu_kletterstand)=0");
            for (int t = 0; t < 8; t++) climb.Tick(0.1);       // hold the 9-frame one-shot (0.9 s)
            Assert.AreEqual(baseFloor, body.transform.position,
                "NO translation during the whole mount hold (the mid-air Approach regression)");

            // First advance tick: the floor→wall hop is consumed at CLIMB speed (≤ cruise×dt),
            // not teleported.
            Vector3 before = body.transform.position;
            climb.Tick(0.1);   // hold expires → Advance phase armed
            climb.Tick(0.1);
            float step = (body.transform.position - before).magnitude;
            Assert.LessOrEqual(step, ExpectedClimbSpeed * 0.2f + 0.001f,
                "the first climb leg must move at climb cruise — never a walk-speed/teleport approach");

            // Run the climb out: it must land exactly on the trailing claimed floor record.
            int guard = 0; while (climb.Tick(0.1) && guard++ < 2000) { }
            Assert.Less(guard, 2000, "climb never finished");
            Assert.AreEqual(0f, (body.transform.position - path[3].Pos).magnitude, 0.01f,
                "the dismount must land ON the claimed boundary floor record (wall base)");
        }

        [Test]
        public void ClimbFacing_TurnsIntoTheWall()
        {
            var (body, ctrl, anim) = Rig();
            // Start facing the camera/air (−Z): the climber must yaw to face the WALL (+Z =
            // −WallOutward; the Tcl wall pose roty=π → +Z, z_procs.tcl:1707).
            body.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            var climb = Climb(ctrl, body.transform, new Vector3(0f, -2f, 0f), new Vector3(0f, -4f, 0f));
            for (int t = 0; t < 12; t++) climb.Tick(0.1);
            Vector3 f = ctrl.transform.rotation * Vector3.forward;
            Assert.Greater(f.z, 0.99f,
                "while climbing the body must face INTO the wall (world +Z), back to the camera");
        }

        [Test]
        public void ClimbLaterals_FollowTheWallFacingBinning()
        {
            var (body, ctrl, anim) = Rig();
            // Mostly-lateral leg toward world +X: facing the wall (+Z), the climber's anatomic
            // right is +X ⇒ climb_right (the re-bin; Play showed the old pairing wrong).
            var climb = Climb(ctrl, body.transform, new Vector3(2f, -0.2f, 0f), new Vector3(4f, -0.2f, 0f));
            climb.Tick(0.1);
            climb.Tick(0.1);
            Assert.AreEqual("kletter_rechts_loop", anim.CurrentAnimName,
                "a leg moving world +X (screen right, wall-facing) must play climb_right");

            var (body2, ctrl2, anim2) = Rig();
            var climb2 = Climb(ctrl2, body2.transform, new Vector3(-2f, -0.2f, 0f), new Vector3(-4f, -0.2f, 0f));
            climb2.Tick(0.1);
            climb2.Tick(0.1);
            Assert.AreEqual("kletter_links_loop", anim2.CurrentAnimName,
                "a leg moving world −X must play climb_left");
        }
    }
}
