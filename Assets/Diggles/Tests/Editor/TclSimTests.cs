#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Phase 7 — DigglesGameSim event loop + state body ticks.
    // Tests drive the sim manually (no MonoBehaviour) so they're fast,
    // deterministic, and Edit-mode runnable.
    //
    // Note on scope: event handler / state body code runs in the entity's
    // scope (so `set foo X` writes a per-entity var, not a global —
    // matches the Diggles convention). Tests inspect ent.VarsScope.Vars
    // directly when verifying per-entity state.
    public sealed class TclSimTests
    {
        (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) BuildHost()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity EntFor(TclInterpreter interp, DigglesTclContext ctx, string handleVarName = "h")
        {
            int h = int.Parse(interp.Eval($"set {handleVarName}"));
            return ctx.GetEntity(h.ToString())!;
        }

        [Test]
        public void GameTime_AdvancesByDt()
        {
            var (_, _, sim) = BuildHost();
            sim.Step(0.025);
            sim.Step(0.025);
            sim.Step(0.025);
            Assert.That(sim.GameTime, Is.EqualTo(0.075).Within(1e-9));
        }

        [Test]
        public void Gametime_TclCommand_ReadsSimTime()
        {
            var (interp, _, sim) = BuildHost();
            sim.Step(0.5);
            string t = interp.Eval("gametime");
            Assert.That(double.Parse(t,
                System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(1e-9));
        }

        [Test]
        public void TimerEvent_FiresOnceAtScheduledTime()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class Pilz none material 0 {} {
                    member counter
                    obj_init { set counter 0 }
                    handle_event aging { set counter [expr {$counter + 1}] }
                }
                set h [new Pilz """" ""0 0 0"" ""0 0 0""]
                timer_event $h aging -attime 0.05 -repeat 1
            ");
            var ent = EntFor(interp, ctx);

            sim.Step(0.025);  // t=0.025 — not yet
            Assert.AreEqual("0", ent.VarsScope.Vars["counter"]);
            sim.Step(0.025);  // t=0.05 — fires
            Assert.AreEqual("1", ent.VarsScope.Vars["counter"]);
            sim.Step(0.025);  // t=0.075 — already fired
            Assert.AreEqual("1", ent.VarsScope.Vars["counter"]);
        }

        [Test]
        public void TimerEvent_RepeatThree_FiresThreeTimes()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    handle_event tick { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h tick -interval 0.1 -repeat 3 -attime 0.1
            ");
            var ent = EntFor(interp, ctx);
            // Step in 0.05-unit increments past t=0.4
            for (int i = 0; i < 8; i++) sim.Step(0.05);
            Assert.AreEqual("3", ent.VarsScope.Vars["n"]);
        }

        [Test]
        public void TimerUnset_CancelsScheduledEvent()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    handle_event tick { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h tick -interval 0.1 -repeat 5 -attime 0.1
                timer_unset $h tick
            ");
            var ent = EntFor(interp, ctx);
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            Assert.AreEqual("0", ent.VarsScope.Vars["n"]);
        }

        [Test]
        public void StateBody_TicksEachLogicTick()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    state idle { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                state_trigger $h idle
            ");
            // State thinks fire on the 10 Hz LOGIC tick (SrvDoThink, DAT_007371a8 = 0.1), not on
            // every fine sim step — a 0.1 Step = exactly one think.
            sim.Step(0.1);
            sim.Step(0.1);
            sim.Step(0.1);
            var ent = EntFor(interp, ctx);
            Assert.AreEqual("3", ent.VarsScope.Vars["n"]);
        }

        [Test]
        public void StateBody_NotTickedAfterStateLeave()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    state working { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                state_trigger $h working
            ");
            sim.Step(0.1);   // 10 Hz logic ticks
            sim.Step(0.1);
            // Transition out (no second arg → CurrentState becomes null → sim skips).
            interp.Eval("state_trigger $h");
            sim.Step(0.1);
            sim.Step(0.1);
            var ent = EntFor(interp, ctx);
            Assert.AreEqual("2", ent.VarsScope.Vars["n"]);
        }

        [Test]
        public void EventGet_ReadsPayloadFromCurrentHandler()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member msg
                    obj_init { set msg """" }
                    handle_event shout { set msg [event_get -item text] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                set_event $h shout -item text ""hello""
            ");
            sim.Step(0.025);
            var ent = EntFor(interp, ctx);
            Assert.AreEqual("hello", ent.VarsScope.Vars["msg"]);
        }

        [Test]
        public void EventGet_BareReturnsEventName()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member last
                    obj_init { set last """" }
                    handle_event ping { set last [event_get] }
                    handle_event pong { set last [event_get] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                set_event $h pong
            ");
            sim.Step(0.025);
            var ent = EntFor(interp, ctx);
            Assert.AreEqual("pong", ent.VarsScope.Vars["last"]);
        }

        [Test]
        public void SetEvent_FiresOnNextSimStep()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member fired
                    obj_init { set fired 0 }
                    handle_event tick { set fired 1 }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                set_event $h tick
            ");
            var ent = EntFor(interp, ctx);
            // Before stepping, the event sits in the pending queue.
            Assert.AreEqual("0", ent.VarsScope.Vars["fired"]);
            sim.Step(0.025);
            Assert.AreEqual("1", ent.VarsScope.Vars["fired"]);
        }

        [Test]
        public void Del_CancelsScheduledTimers()
        {
            var (interp, _, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    handle_event tick { }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h tick -interval 0.1 -repeat 99 -attime 0.1
            ");
            Assert.AreEqual(1, sim.Scheduler.Count);
            interp.Eval("del $h");
            Assert.AreEqual(0, sim.Scheduler.Count);
        }

        [Test]
        public void Scheduler_DoesNotDeliverToDestroyedEntity()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member fired
                    obj_init { set fired 0 }
                    handle_event tick { set fired 1 }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
            ");
            int h = int.Parse(interp.Eval("set h"));
            sim.Scheduler.Schedule(h, "tick", 0.05, 0.0, 1, null);
            // Out-of-band kill — bypass del so we test the scheduler's
            // own missing-entity guard.
            ctx.Entities.Remove(h);
            sim.Step(0.1);
            // No crash. Test passes if we got here.
            Assert.AreEqual(0, sim.Scheduler.Count);
        }
    }
}
