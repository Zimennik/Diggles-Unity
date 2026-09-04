#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // timer fixes, verified against the native RE:
    // • timer_event parses -userid (FUN_0069fef0:376-404, default 0:191) and the
    // payload flags (-subject1..3/-pos1/-pos2/-num1/-num2/-text1,:406-731);
    // • timer_unset takes a numeric USERID — parse failure → 0, -1 = wildcard — and
    // removes every timer on the object with that userid (FUN_0069fc90:82-83 →
    // FUN_00606110:24). There is NO name-based unset in the original; every script
    // call passes an int (pilz.tcl:111, z_methods.tcl:520/555, baby.tcl:156).
    // • interval == 0 forces repeat = 1 (CTimerEvtMgr add, FUN_00605f40:45-47).
    // The old port dropped -userid and treated timer_unset arg2 as an event NAME, so
    // every `timer_unset this <int>` was a silent no-op → mushroom aging, invisibility
    // and baby growth ran corrupted timers forever.
    public sealed class TimerUseridTests
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

        [Test]
        public void UnsetByUserid_CancelsOnlyThatTimer()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member a
                    member b
                    obj_init { set a 0 ; set b 0 }
                    handle_event evt_a { set a [expr {$a + 1}] }
                    handle_event evt_b { set b [expr {$b + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h evt_a -userid 1 -interval 0.1 -repeat 3 -attime 0.1
                timer_event $h evt_b -userid 2 -interval 0.1 -repeat 3 -attime 0.1
                timer_unset $h 1
            ");
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            var e = Ent(interp, ctx);
            Assert.AreEqual("0", e.VarsScope.Vars["a"], "userid-1 timer was cancelled — must never fire");
            Assert.AreEqual("3", e.VarsScope.Vars["b"], "userid-2 timer must be untouched");
        }

        // The pilz.tcl shape: a -userid 1 repeating grow timer (pilz.tcl:40) cancelled
        // mid-flight by `timer_unset this 1` (pilz.tcl:111).
        [Test]
        public void PilzShape_RepeatingTimer_StopsWhenUnsetMidFlight()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    handle_event evt_grow { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h evt_grow -userid 1 -repeat 3 -interval 0.2 -attime 0.2
            ");
            var e = Ent(interp, ctx);
            for (int i = 0; i < 5; i++) sim.Step(0.05);   // t=0.25 → first fire delivered
            Assert.AreEqual("1", e.VarsScope.Vars["n"], "first fire lands");
            interp.Eval("timer_unset $h 1");
            for (int i = 0; i < 20; i++) sim.Step(0.05);  // t=1.25 — fires 2+3 would be due
            Assert.AreEqual("1", e.VarsScope.Vars["n"], "remaining fires are cancelled");
        }

        [Test]
        public void UnsetMinusOne_IsWildcard_CancelsAllTimersOnTheObject()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    handle_event evt_a { set n [expr {$n + 1}] }
                    handle_event evt_b { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h evt_a -userid 3 -interval 0.1 -repeat 2 -attime 0.1
                timer_event $h evt_b -userid 4 -interval 0.1 -repeat 2 -attime 0.1
                timer_unset $h -1
            ");
            for (int i = 0; i < 15; i++) sim.Step(0.05);
            Assert.AreEqual("0", Ent(interp, ctx).VarsScope.Vars["n"],
                "userid -1 cancels every timer on the object (FUN_00606110:24)");
        }

        [Test]
        public void DefaultUserid_IsZero_AndUnsetZeroCancelsIt()
        {
            // Lavawelt.tcl:344 `timer_unset this 0` cancels timers scheduled without -userid.
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    handle_event tick { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h tick -interval 0.1 -repeat 5 -attime 0.1
                timer_unset $h 0
            ");
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            Assert.AreEqual("0", Ent(interp, ctx).VarsScope.Vars["n"],
                "the timer_event default userid is 0 (FUN_0069fef0:191)");
        }

        [Test]
        public void SameEventName_DifferentUserids_UnsetCancelsOnlyOne()
        {
            // The delivery-filter path: both timers share the EVENT name, so the scheduler
            // entry can't be removed by name — the cancelled one must be dropped at delivery
            // while its same-name sibling still fires.
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    handle_event tick { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h tick -userid 1 -interval 0.1 -repeat 2 -attime 0.1
                timer_event $h tick -userid 2 -interval 0.1 -repeat 2 -attime 0.1
                timer_unset $h 1
            ");
            for (int i = 0; i < 15; i++) sim.Step(0.05);
            Assert.AreEqual("2", Ent(interp, ctx).VarsScope.Vars["n"],
                "only the userid-1 fires are dropped; the userid-2 timer fires its 2");
        }

        [Test]
        public void TimerPayload_UseridAndSubject1_ReadableViaEventGet()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member uid
                    member subj
                    obj_init { set uid """" ; set subj """" }
                    handle_event evt_x {
                        set uid  [event_get this -userid]
                        set subj [event_get this -subject1]
                    }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h evt_x -userid 5 -subject1 99 -interval 0.1 -attime 0.1
            ");
            for (int i = 0; i < 5; i++) sim.Step(0.05);
            var e = Ent(interp, ctx);
            Assert.AreEqual("5", e.VarsScope.Vars["uid"], "-userid must ride the event payload");
            Assert.AreEqual("99", e.VarsScope.Vars["subj"], "-subject1 must ride the event payload");
        }

        [Test]
        public void ZeroInterval_ForcesSingleFire_EvenWithRepeatMinusOne()
        {
            // FUN_00605f40:45-47 — interval == 0 ⇒ repeat forced to 1; a `-repeat -1`
            // without -interval must not rapid-fire forever.
            var (interp, ctx, sim) = BuildHost();
            interp.Eval(@"
                def_class P none material 0 {} {
                    member n
                    obj_init { set n 0 }
                    handle_event tick { set n [expr {$n + 1}] }
                }
                set h [new P """" ""0 0 0"" ""0 0 0""]
                timer_event $h tick -repeat -1 -attime 0.1
            ");
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            Assert.AreEqual("1", Ent(interp, ctx).VarsScope.Vars["n"],
                "zero interval forces exactly one fire");
        }

        [Test]
        public void NonNumericUseridArg_ParsesAsZero()
        {
            // Tcl_GetIntFromObj failure → userid 0 (FUN_0069fc90:82-83). A (non-original)
            // name-form call therefore cancels the default-userid timers, not nothing.
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
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            Assert.AreEqual("0", Ent(interp, ctx).VarsScope.Vars["n"]);
        }
    }
}
