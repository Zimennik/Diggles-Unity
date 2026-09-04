#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;

    // The faithful CElf core (ElfState), verified against the decompile:
    // FUN_00427e30 Tick (200 Hz step, mode machine 0-5, lean math, hover bob, frame advance)
    // FUN_00428b80 FlyToward (off-screen teleport, screen-edge entry, damp 0.75/accel 0.15/
    // clamp 25, move-only-while-correct-loop-anim gate, ease-out, arrival 0.2)
    // FUN_00428630 AdvanceAnim (outro-on-switch, one-shot auto-return + finishcode-on-enter)
    // FUN_004288c0 path setup (quantized velocity: stepLen = dist/round(dist/s))
    // FUN_004290a0 DirAnim, FUN_00427610 Reset, FUN_00649640 dispatcher verb semantics.
    // Headless: the two screen callbacks (IsOnScreen / UnprojectScreenToNative) + screen size +
    // clip frame counts are mocked.
    public sealed class ElfStateTests
    {
        static void Ticks(ElfState e, int n)
        {
            for (int i = 0; i < n; i++) e.Tick(ElfState.FixedStep);
        }

        // ---- FlyToward: off-screen teleport (FUN_00428b80 head) ----

        [Test]
        public void FlyToward_BothEndpointsOffScreen_Teleports()
        {
            var elf = new ElfState { IsOnScreen = (p, m) => false };
            elf.CmdSetPos(new Vector3(2f, 0f, 16f));
            elf.CmdMove(new Vector3(9f, 0f, 16f));
            elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(9f, elf.Position.x, 1e-4f, "off-screen flight is a TELEPORT to the target");
            Assert.AreEqual(2, elf.Mode, "still mode 2 on the teleport tick (arrival check is next tick)");
            elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(0, elf.Mode, "dist^2 < 0.04 ends the move");
        }

        // ---- FlyToward: fresh/reset elf enters from the screen edge ----

        [Test]
        public void FlyToward_FreshElf_EntersFromScreenEdge()
        {
            // Screen = native x in [0,8] for the 50 px edge walk; the 400 px teleport margin is
            // treated as "everything on screen" so the teleport branch stays off.
            var elf = new ElfState { IsOnScreen = (p, m) => m >= 400 || (p.x >= 0f && p.x <= 8f) };
            // Fresh elf: pos == origin (|pos|^2 < 0.01 arms the edge entry).
            elf.CmdMove(new Vector3(4f, 0f, 16f));
            elf.Tick(ElfState.FixedStep);
            // She must appear at the screen edge (last on-screen point walking outward from the
            // target along pos-target), not at the target and not at the origin.
            Assert.IsTrue(elf.Position.x >= 0f && elf.Position.x <= 8f, "lands ON screen (50px rect)");
            Assert.Less(elf.Position.x, 1f, "near the left edge (dir from target toward origin)");
            Assert.Greater((elf.Position - elf.Target).magnitude, 3f, "entered well away from the target");
        }

        // ---- mode 2: arrival ends the move and fires the finishcode exactly once ----

        [Test]
        public void Move_Arrival_Mode0_FinishcodeFiredOnce()
        {
            int evals = 0;
            var elf = new ElfState { IsOnScreen = (p, m) => true };
            elf.SetEvalCallback(_ => evals++);
            elf.IsObjAlive = id => id == 7;
            elf.CmdSetPos(new Vector3(10f, 10f, 16f));
            elf.CmdFinishcode(7, "seq_enable elf");
            elf.CmdMove(new Vector3(10.5f, 10f, 16f));      // inside the ease-out band
            Ticks(elf, 40);                                  // 10%/tick glide crosses 0.2 in ~10 ticks
            Assert.AreEqual(0, elf.Mode, "arrived");
            Assert.AreEqual(1, evals, "finishcode fired");
            Assert.IsNull(elf.FinishCode, "registration consumed");
            Assert.LessOrEqual((elf.Position - new Vector3(10.5f, 10f, 16f)).magnitude, 0.2f,
                "mode 2 ends inside the 0.2 arrival band (its own 0.04 dist^2 pregate)");
            Ticks(elf, 40);
            Assert.AreEqual(1, evals, "never fires twice");
        }

        // ---- mode 3: hide keeps flying until off-screen, then Reset(keep texvar) + invisible ----

        [Test]
        public void Hide_FliesUntilOffScreen_ThenResetKeepsTexvar()
        {
            int evals = 0;
            var elf = new ElfState
            {
                IsOnScreen = (p, m) => p.x < 5f,             // off-screen once she passes x=5
                GetClipFrameCount = c => 1,                  // 1-frame clips: fast anim machine
            };
            elf.SetEvalCallback(_ => evals++);
            elf.CmdSetPos(new Vector3(0.5f, 0f, 16f));
            elf.CmdTexVar(3);
            elf.CmdFinishcode(-2, "seq_enable elf");
            elf.CmdMove(new Vector3(40f, 0f, 16f));
            elf.CmdHide();                                   // dispatcher: lookat=-1, lockview=0, mode=3
            Assert.IsTrue(elf.Visible, "hide does NOT vanish instantly — she flies out");
            int ticks = 0;
            for (; ticks < 4000 && elf.Visible; ticks++) elf.Tick(ElfState.FixedStep);
            Assert.IsFalse(elf.Visible, "invisible once off-screen");
            Assert.Greater(ticks, 50, "took a real flight (anim gate + cruise), not an instant pop");
            Assert.AreEqual(1, evals, "hide completion fired the finishcode");
            Assert.AreEqual(3, elf.TexVariant, "Reset(keep texvar) — hide completion keeps it");
            Assert.AreEqual(0f, elf.Position.sqrMagnitude, 1e-6f, "pos -> origin re-arms edge entry");
            Assert.AreEqual(0, elf.Mode);
        }

        // ---- mode 4: crash falls 0.05/tick into the -3.0 band, then aufprall + finishcode ----

        [Test]
        public void Crash_FallsToTarget_ThenAufprall()
        {
            int evals = 0;
            var elf = new ElfState { GetClipFrameCount = c => 2 };
            elf.SetEvalCallback(_ => evals++);
            elf.CmdFinishcode(-2, "seq_enable elf");
            elf.CmdCrash(new Vector3(7f, 20f, 16f));
            Assert.AreEqual(5f, elf.Position.y, 1e-4f, "spawns 15 above (y-down: y-15, DAT_00737568)");
            Assert.AreEqual(7f, elf.Position.x, 1e-4f);
            Assert.AreEqual(4, elf.Mode);
            Ticks(elf, 30);
            Assert.AreEqual("abstuerzen_fall", elf.ModelAnim, "fall clip loops during the descent");
            Assert.AreEqual(4, elf.Mode);
            int ticks = 0;
            for (; ticks < 500 && elf.Mode == 4; ticks++) elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(0, elf.Mode, "landed");
            Assert.AreEqual(17f, elf.Position.y, 0.1f, "impact at target.y - 3.0 (the band, FUN_00427e30 case 4)");
            Assert.AreEqual(7f, elf.Position.x, 1e-4f, "x never drifts");
            Assert.AreEqual(1, evals, "landing fired the finishcode");
            Assert.IsTrue(elf.ReqAnimIdx == 26 || elf.CurAnimIdx == 26, "abstuerzen_aufprall (idx 26) plays");
            Ticks(elf, 200);
            Assert.AreEqual(0, elf.CurAnimIdx, "aufprall is a one-shot: auto-returns to standard");
        }

        // ---- mode 5: path = quantized velocity, ~6 u/s at s=0.6, no anim gating ----

        [Test]
        public void Path_QuantizedVelocity_SixUnitsPerSecond()
        {
            var elf = new ElfState();
            elf.CmdPath(new Vector3(0f, 0f, 16f), new Vector3(6f, 0f, 16f), 0.6f);
            // dist 6 / round(6/0.6)=10 steps -> stepLen 0.6 -> velocity 0.6*0.05 = 0.03/tick = 6 u/s.
            Assert.AreEqual(0.03f, elf.Velocity.x, 1e-5f);
            Assert.AreEqual(5, elf.Mode);
            Assert.AreEqual(0f, elf.Position.x, 1e-5f, "path snaps to p1");

            // Quantization: dist 6.2 / round(10.333)=10 -> stepLen 0.62.
            elf.CmdPath(new Vector3(0f, 0f, 16f), new Vector3(6.2f, 0f, 16f), 0.6f);
            Assert.AreEqual(0.031f, elf.Velocity.x, 1e-5f);

            // s < 0.01 coerces to 1.0 (FUN_004288c0 head).
            elf.CmdPath(new Vector3(0f, 0f, 16f), new Vector3(6f, 0f, 16f), 0f);
            Assert.AreEqual(0.05f, elf.Velocity.x, 1e-5f);

            // Path movement is not anim-gated (unlike FlyToward): moves from the first tick.
            elf.CmdPath(new Vector3(0f, 0f, 16f), new Vector3(6f, 0f, 16f), 0.6f);
            Ticks(elf, 10);
            Assert.AreEqual(0.3f, elf.Position.x, 1e-4f, "10 ticks x 0.03 — no waiting for a turn anim");
            Ticks(elf, 200);
            Assert.AreEqual(0, elf.Mode, "ends inside the 0.25 band (0.0625 dist^2)");
            Assert.Greater(elf.Position.x, 5.7f);
        }

        // ---- one-shot anims auto-return to standard; finishcode fires on enter and clears ----

        [Test]
        public void OneShot_AutoReturnsToStandard_FiresAndClearsFinishcode()
        {
            int evals = 0;
            var elf = new ElfState { GetClipFrameCount = c => 2 };   // 40 ticks per clip
            elf.SetEvalCallback(_ => evals++);
            elf.CmdStop();
            Ticks(elf, 1);                                   // standard starts (cur=0, standanim)
            Assert.AreEqual(0, elf.CurAnimIdx);
            elf.CmdFinishcode(-2, "seq_enable elf");
            elf.CmdAction("kopf_schuetteln");
            Assert.AreEqual(31, elf.ReqAnimIdx);
            Ticks(elf, 45);                                  // standanim must finish before the switch
            Assert.AreEqual(31, elf.CurAnimIdx, "one-shot entered at the clip boundary");
            Assert.AreEqual(0, elf.ReqAnimIdx, "entering a one-shot immediately re-requests standard");
            Assert.AreEqual(1, evals, "finishcode fires on one-shot ENTER (FUN_00428630)");
            Assert.IsNull(elf.FinishCode, "and is cleared");
            Ticks(elf, 45);                                  // the gesture ends
            Assert.AreEqual(0, elf.CurAnimIdx, "auto-returned to standard (no last-frame freeze)");
            Assert.AreEqual("standanim", elf.ModelAnim);
            Assert.AreEqual(1, evals, "no second fire");
        }

        // ---- unknown anim names clamp to 0 = standard; kichern resolves (slot 16) ----

        [Test]
        public void Action_UnknownName_ClampsToStandard()
        {
            var elf = new ElfState();
            elf.CmdAction("reden_b");
            Assert.AreEqual(30, elf.ReqAnimIdx);
            // kichern is registered: the unrecovered slot 16 (alphabetical fit between
            // gucken_rechts and koketieren), the giggle urw_6a:159 requests.
            elf.CmdAction("kichern");
            Assert.AreEqual(16, elf.ReqAnimIdx, "kichern = the alphabetical slot 16");
            elf.CmdAction("no_such_anim_xyz");
            Assert.AreEqual(0, elf.ReqAnimIdx, "FindAnim miss -> 0 (dispatcher action branch)");
            Assert.IsTrue(elf.Visible, "any elf command sets visible");
        }

        // ---- directional anim selection from velocity (FUN_004290a0 quadrants) ----

        [Test]
        public void DirAnim_QuadrantsFromVelocity()
        {
            Assert.AreEqual(4, ElfState.DirAnim(1f, 0f), "right");
            Assert.AreEqual(3, ElfState.DirAnim(-1f, 0.5f), "left");
            Assert.AreEqual(2, ElfState.DirAnim(1f, 1.6f), "|vy| beats |vx|*1.5 -> vertical, down (y-down)");
            Assert.AreEqual(1, ElfState.DirAnim(0.5f, -0.8f), "up");
            Assert.AreEqual(2, ElfState.DirAnim(1f, 1.5f), "tie goes vertical (strict < for horizontal)");

            // Through mode 5: anim picked from the live velocity each tick.
            var elf = new ElfState();
            elf.CmdPath(new Vector3(0f, 0f, 16f), new Vector3(0f, 8f, 16f), 0.6f);
            elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(2, elf.ReqAnimIdx, "path heading +y selects move_d");
            elf.CmdPath(new Vector3(0f, 0f, 16f), new Vector3(-8f, 0f, 16f), 0.6f);
            elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(3, elf.ReqAnimIdx, "path heading -x selects move_l");
        }

        // ---- the move gate: no position change until the directional loop clip is playing ----

        [Test]
        public void FlyToward_MoveGate_WaitsForTheTurnAnim()
        {
            var elf = new ElfState
            {
                IsOnScreen = (p, m) => true,
                GetClipFrameCount = c => 2,                  // every clip = 40 ticks
            };
            elf.CmdSetPos(new Vector3(10f, 10f, 16f));
            elf.CmdMove(new Vector3(30f, 10f, 16f));
            Ticks(elf, 30);                                  // fliegen_rechts_norm_start still playing
            Assert.AreEqual("fliegen_rechts_norm_start", elf.ModelAnim, "turn-in clip first");
            Assert.AreEqual(0, elf.AnimPhase, "start phase");
            Assert.AreEqual(10f, elf.Position.x, 1e-4f, "GATED: no movement during the _start clip");
            Ticks(elf, 30);                                  // start ends -> loop phase -> moves
            Assert.AreEqual("fliegen_rechts_norm_loop", elf.ModelAnim);
            Assert.Greater(elf.Position.x, 10.1f, "movement begins once the right LOOP plays");
        }

        // ---- hover bob: only while the registered anim 0 (standard) is playing ----

        [Test]
        public void HoverBob_OnlyDuringStandardAnim()
        {
            var elf = new ElfState { GetClipFrameCount = c => 10 };
            elf.CmdStop();
            Ticks(elf, 1);                                   // standanim starts
            float y0 = elf.Position.y;
            Ticks(elf, 50);
            Assert.Greater(Mathf.Abs(elf.Position.y - y0), 1e-4f,
                "the standard hover bobs (sin(t*0.31+0.1)*0.0008/tick)");

            var idle = new ElfState { GetClipFrameCount = c => 10 };
            idle.CmdAction("idle");
            Ticks(idle, 1);                                  // cur=6 (rumhaengen) — not anim 0
            Vector3 p0 = idle.Position;
            Ticks(idle, 50);
            Assert.AreEqual(p0, idle.Position, "no bob while a non-standard anim plays");
        }

        // ---- movescreen: 800x600-reference scaling + edge-relative negatives (z<0 mode) ----

        [Test]
        public void MoveScreen_800x600ReferenceScaling()
        {
            Vector3 captured = default;
            var elf = new ElfState
            {
                IsOnScreen = (p, m) => true,
                ScreenSize = () => new Vector2(1600f, 1200f),
                UnprojectScreenToNative = (x, y, z) => { captured = new Vector3(x, y, z); return new Vector3(0f, 0f, z); },
            };
            elf.CmdSetPos(new Vector3(5f, 5f, 16f));         // off-origin: no edge-entry detour

            // z<0: x,y are 800x600-reference coords scaled by realW/800, realH/600; depth = -z.
            elf.CmdMoveScreen(new Vector3(400f, 300f, -16f));
            elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(new Vector3(800f, 600f, 16f), captured, "center scales 2x on a 1600x1200 screen");

            // Negative coords add the real screen dims first, then the z<0 branch scales —
            // exactly the decompile's statement order (FUN_00427e30 case 1).
            elf.CmdMoveScreen(new Vector3(-100f, 300f, -16f));
            elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(new Vector3((1600f - 100f) * 2f, 600f, 16f), captured);

            // z>=0: raw real pixels, no scaling.
            elf.CmdMoveScreen(new Vector3(400f, 300f, 16f));
            elf.Tick(ElfState.FixedStep);
            Assert.AreEqual(new Vector3(400f, 300f, 16f), captured);
        }

        // ---- 200 Hz fixed-step determinism: coarse sim steps == the same count of 5 ms ticks ----

        [Test]
        public void Accumulator_TwoCoarseSteps_EqualFiveFixedTicks()
        {
            ElfState Make()
            {
                var e = new ElfState { GetClipFrameCount = c => 5 };
                e.CmdPath(new Vector3(0f, 0f, 16f), new Vector3(6f, 0f, 16f), 0.6f);
                return e;
            }
            var coarse = Make();
            coarse.Tick(0.0125);
            coarse.Tick(0.0125);                             // 0.025 s = exactly 5 fixed steps
            var fine = Make();
            for (int i = 0; i < 5; i++) fine.Tick(0.005);
            Assert.AreEqual(fine.Position.x, coarse.Position.x, 1e-6f, "identical tick count + math");
            Assert.AreEqual(5 * 0.03f, coarse.Position.x, 1e-5f, "five path ticks of 0.03");
        }

        // ---- Reset semantics + the every-command visibility flag ----

        [Test]
        public void Reset_ClearsState_TexvarOnlyWhenAsked()
        {
            var elf = new ElfState();
            elf.CmdTexVar(5);
            elf.CmdLookat(3);
            elf.CmdLockView(true);
            elf.CmdMove(new Vector3(4f, 4f, 16f));
            elf.Reset(false);
            Assert.IsFalse(elf.Visible);
            Assert.AreEqual(5, elf.TexVariant, "Reset(false) keeps the texture variant");
            Assert.AreEqual(-1, elf.LookatObjId);
            Assert.IsFalse(elf.LockView);
            Assert.AreEqual(0, elf.Mode);
            Assert.AreEqual(-1, elf.CurAnimIdx);
            Assert.AreEqual(-1, elf.ReqAnimIdx, "Reset leaves NO anim requested (FUN_00427610)");
            elf.Reset(true);
            Assert.AreEqual(0, elf.TexVariant, "Reset(true) = new game clears it");
            Assert.AreEqual(Vector3.zero, elf.CmdGetPos(), "pos at origin");
            Assert.IsTrue(elf.Visible, "even get_pos marks her visible (dispatcher :103)");
        }
    }
}
