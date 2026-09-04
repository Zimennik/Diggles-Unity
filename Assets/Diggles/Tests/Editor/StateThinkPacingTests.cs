#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // state-think pacing — the FAITHFUL rule, verified against the native RE:
    // • CObj::HandleStates (005cac90:27-29) never gates on the active action; its only
    // gates are the per-object think counter, state-enabled (+0x208 > 0) and
    // has-a-state. SrvDoThink (005d38c0:251) calls it unconditionally per object.
    // • Pacing is the SCRIPTS' job: the gnome idle state ends with `set_idle_anim;
    // state_disable this; action this wait 1 {state_enable this}` (zwerg.tcl:316-318);
    // work waits (z_work_states.tcl:202-203) and every walk proc (z_dignwalk.tcl:594…)
    // follow the same state_disable → action → finish/state_enable discipline.
    // • state_enable/state_disable are ABSOLUTE writes, not a refcount: StateEnable
    // (FUN_0069f400:52) sets +0x208 = 1, StateDisable (FUN_0069f5a0:52) sets it = 0.
    // The old sim gate (`ActiveAction != null && TaskList.Count == 0 → skip`) was a
    // band-aid: with tasks queued an in-flight 3s walk saw ~120 state-body re-entries
    // (play_anim restarted per tick — the "anims x100 / hats snap instantly" Play bug).
    public sealed class StateThinkPacingTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) BuildHost()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Ent(TclInterpreter interp, DigglesTclContext ctx)
            => ctx.GetEntity(interp.Eval("set h").Trim())!;

        static int N(DigglesTclEntity e) => int.Parse(e.VarsScope.Vars["n"]);

        // The zwerg.tcl idle discipline: the body disables itself before its action and the
        // finishcode re-enables. The body must NOT re-enter while the wait runs, and MUST
        // resume after it completes.
        [Test]
        public void DisciplinedStateBody_PausesDuringAction_ResumesAfter()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    state idle {
                        set n [expr {$n + 1}]
                        state_disable this
                        action this wait 0.5 { state_enable this }
                    }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                state_trigger $h idle
            ");
            var e = Ent(interp, ctx);

            sim.Step(0.1);   // one LOGIC tick (state thinks fire at 10 Hz, DAT_007371a8)
            Assert.AreEqual(1, N(e), "first think runs the body once");
            // While the 0.5s wait runs the state is DISABLED — no re-entry.
            for (int i = 0; i < 4; i++) sim.Step(0.1);   // t = 0.5, action still in flight
            Assert.AreEqual(1, N(e), "the body must not re-enter while its action runs");
            // Past the wait: finishcode state_enables → the body thinks again.
            for (int i = 0; i < 2; i++) sim.Step(0.1);   // wait completes → cycle 2 begins
            Assert.AreEqual(2, N(e), "the body resumes after the action completes");
        }

        // The faithful rule itself: state-think is NOT gated on ActiveAction. A body that
        // skips the state_disable discipline keeps thinking while an action is in flight
        // (exactly what the native does — HandleStates has no action term).
        [Test]
        public void StateThink_IsNotGatedOnActiveAction()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    state watch { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                state_trigger $h watch
                action $h wait 10 {}
            ");
            var e = Ent(interp, ctx);
            Assert.IsNotNull(e.ActiveAction, "the 10s wait is in flight");
            // 8 fine steps = 0.2 s = exactly TWO 10 Hz logic ticks (SrvDoThink 006b4a30:129-153):
            // the body thinks on every LOGIC tick even while an action runs — but never faster.
            for (int i = 0; i < 8; i++) sim.Step(0.025);
            Assert.AreEqual(2, N(e),
                "an enabled state thinks every LOGIC tick (10 Hz) while an action runs (FUN_005cac90 has no action gate)");
        }

        // state_enable / state_disable are absolute (1/0), not a refcount: a bare extra
        // state_enable (zwerg.tcl:308 does one before state_triggerfresh) must not poison
        // a later state_disable, and a double disable is still lifted by ONE enable.
        [Test]
        public void StateEnableDisable_AreAbsolute_NotARefcount()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    state watch { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                state_trigger $h watch
            ");
            var e = Ent(interp, ctx);

            // Bare extra enable, then ONE disable → must actually disable.
            interp.Eval("state_enable $h");
            interp.Eval("state_enable $h");
            interp.Eval("state_disable $h");
            sim.Step(0.1); sim.Step(0.1);
            Assert.AreEqual(0, N(e), "one disable disables, regardless of prior enables (FUN_0069f5a0:52 sets 0)");

            // Double disable, then ONE enable → must actually enable.
            interp.Eval("state_disable $h");
            interp.Eval("state_disable $h");
            interp.Eval("state_enable $h");
            sim.Step(0.1);
            Assert.AreEqual(1, N(e), "one enable enables, regardless of prior disables (FUN_0069f400:52 sets 1)");
        }

        // Mini headless work-loop: the zwerg.tcl `task` state shape — pop ONE task per
        // think, each popped task runs the disable→action→enable discipline. The pump must
        // drain one task per ACTION COMPLETION (not one per 0.025s tick), and the chain
        // must still advance to completion (the WorkLoopTests instant-WaitAction pattern).
        [Test]
        public void TaskPump_PopsOnePerActionCompletion_AndDrains()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    state task {
                        if {[tasklist_cnt this] == 0} {
                            state_trigger this
                        } else {
                            set command [tasklist_get this 0]
                            tasklist_rem this 0
                            eval $command
                        }
                    }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                tasklist_add $h {set n [expr {$n + 1}] ; state_disable this ; action this wait 0.2 {state_enable this}}
                tasklist_add $h {set n [expr {$n + 1}] ; state_disable this ; action this wait 0.2 {state_enable this}}
                tasklist_add $h {set n [expr {$n + 1}] ; state_disable this ; action this wait 0.2 {state_enable this}}
                state_trigger $h task
            ");
            var e = Ent(interp, ctx);

            // After 0.1s (4 ticks) only the FIRST task may have run — its 0.2s action
            // paces the pump. The old TaskList.Count>0 exception re-entered every tick
            // and would have burned through all three.
            for (int i = 0; i < 4; i++) sim.Step(0.025);
            Assert.AreEqual(1, N(e), "one pop per action completion — not one per tick");

            // Drain: 3 × (pop + 0.2s wait) ≈ 0.7s; give it 1.5s.
            for (int i = 0; i < 60; i++) sim.Step(0.025);
            Assert.AreEqual(3, N(e), "the chain still advances to completion headlessly");
            Assert.AreEqual(0, e.TaskList.Count, "all tasks drained");
        }

        // Cutscene freeze still wins over everything: a state_disabled-by-the-sequencer
        // entity (count < 0) must not think even with tasks queued.
        [Test]
        public void FrozenState_DoesNotThink_EvenWithQueuedTasks()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    state watch { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                state_trigger $h watch
                state_disable $h
                tasklist_add $h ""whatever""
            ");
            var e = Ent(interp, ctx);
            for (int i = 0; i < 6; i++) sim.Step(0.025);
            Assert.AreEqual(0, N(e), "disabled state never thinks, queued tasks or not");
            interp.Eval("state_enable $h");
            sim.Step(0.1);   // a full logic tick after the enable
            Assert.AreEqual(1, N(e));
        }
    }
}
