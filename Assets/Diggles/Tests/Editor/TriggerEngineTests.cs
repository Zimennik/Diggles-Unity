#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // THE NATIVE TRIGGER ENGINE (wiggles/Wiggles/Objects/Trigger.cpp) — port contract tests.
    //
    // Ground truth (all disasm-verified, constants extracted from Diggles.exe.data):
    // • `trigger <verb> <obj> …` dispatcher = FUN_0060e920 — kinds at:127-145
    // (single_timer/repeat_timer/any_object/unique_object/callback), execution-mode
    // strings at:235-365 INCLUDING the native's own "when_inavtive" typo (:346).
    // • per-tick scan = FUN_0060df40, called once per SrvDoThink logic tick
    // (006b4a30:391); per-record checktimer gate:49; sequence-mode suppression:24.
    // • record ctor FUN_0060da90: execution default 2 (on_activate), checktimer 3.0,
    // idle poll 3.0, near poll 0.5, range 5.0, repeat interval 1.0, first check due
    // at once (lastCheck = now − 4.9).
    // • firing = FUN_0060db80: the FIRE SCRIPT (create's 5th word) is EVALUATED in the
    // OWNER object's own Tcl interp — no event raise. Actuators = object-id strings
    // (itoa(obj+0xdc), FUN_005caf90), returned by `trigger get_actuators`.
    // • consumers: Trigger_Urwald.tcl:18 (wusch),:221-236 (FirstContact),
    //:925-940 (Trigger_Urw_006 → urw_3); Trigger_Tutorial.tcl:1028-1030;
    // sequencer.tcl:397 (get_actuators inside catch); z_work_prod.tcl:1430-1444.
    public sealed class TriggerEngineTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        const double PollMarginSeconds = 0.2;

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        // Light classes: a dummy trigger owner (the Trigger_* objects are `def_class … none
        // dummy 0`) + gnome/monster-kind actuator stand-ins.
        const string LightClasses = @"
            def_class TOwner none dummy 0 {} { obj_init { set fired 0 ; set nfires 0 ; set armflag 0 } }
            def_class Zwerg none gnome 0 {} {}
            def_class TMonster none monster 0 {} {}
        ";

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static string V(DigglesTclEntity e, string n)
            => e.VarsScope.Vars.TryGetValue(n, out var v) ? v : "<none>";

        static void StepSeconds(DigglesGameSim sim, double secs)
        {
            int n = (int)System.Math.Round(secs / 0.1);
            for (int i = 0; i < n; i++) sim.Step(0.1);   // 0.1 = one 10 Hz logic tick per Step
        }

        static void StepPastCurrentPoll(DigglesGameSim sim, DigglesTclContext ctx, int ownerHandle)
        {
            var trigger = ctx.TriggerService!.FindByOwner(ownerHandle);
            Assert.IsNotNull(trigger, "the trigger must exist before advancing its poll interval");
            StepSeconds(sim, trigger!.CheckTimer + PollMarginSeconds);
        }

        // ---------------------------------------------------------------------------
        // create / delete / get_actuators surface
        // ---------------------------------------------------------------------------

        [Test]
        public void Create_ReturnsSlotIndex_AndDeleteDisarms()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "10 10 10");

            // create returns the slot index (FUN_0060e920:203-206 Tcl_NewIntObj) — first = 0.
            Assert.AreEqual("0", interp.Eval($"trigger create {own.Handle} any_object {{set fired 1}}").Trim());
            Assert.NotNull(ctx.TriggerService!.FindByOwner(own.Handle));
            // get_actuators on a never-fired trigger → empty list (+0x68 starts null).
            Assert.AreEqual("", interp.Eval($"trigger get_actuators {own.Handle}").Trim());

            // re-create on the SAME object deletes the old record first (one trigger per
            // object — obj+0x2a4; FUN_0060e920:111-113) and reuses the freed slot.
            Assert.AreEqual("0", interp.Eval($"trigger create {own.Handle} callback {{set fired 1}}").Trim());
            Assert.AreEqual(1, ctx.TriggerService.LiveCount);
            Assert.AreEqual(TriggerKind.Callback, ctx.TriggerService.FindByOwner(own.Handle)!.Kind);

            // delete (:215-221) — afterwards every set_/get_ verb errors "object has no
            // trigger" (:415-419); sequencer.tcl:397 banks on that inside `catch`.
            interp.Eval($"trigger delete {own.Handle}");
            Assert.IsNull(ctx.TriggerService.FindByOwner(own.Handle));
            Assert.AreEqual("1", interp.Eval($"catch {{ trigger get_actuators {own.Handle} }}").Trim(),
                "get_actuators without a trigger must THROW (native 'object has no trigger') — " +
                "sequencer.tcl:397 wraps it in catch and falls back to obj_query");

            // unknown kind / unknown verb errors (:151 /:641-645).
            Assert.Throws<TclException>(() => interp.Eval($"trigger create {own.Handle} bogus_kind {{x}}"));
            interp.Eval($"trigger create {own.Handle} any_object {{x}}");
            Assert.Throws<TclException>(() => interp.Eval($"trigger frobnicate {own.Handle}"));
        }

        // ---------------------------------------------------------------------------
        // any_object proximity (FUN_0060e410) — the Trigger_Urwald workhorse
        // ---------------------------------------------------------------------------

        [Test]
        public void AnyObject_FiresWhenActuatorEntersRange_NotBefore()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "10 10 10");
            // Native-frame distance is the FULL 3D euclidean over the raw position triple
            // (FUN_0060e410:94-98, obj+0xc/0x10/0x14) — offset on the Y (2nd) component.
            var dw = Spawn(interp, ctx, "Zwerg", "10 19 10");   // distance 9 > range 2

            interp.Eval($@"
                trigger create {own.Handle} any_object {{set fired 1 ; incr nfires}}
                trigger set_target_range {own.Handle} 2
                trigger set_target_class {own.Handle} Zwerg
                trigger set_target_count {own.Handle} 1
            ");   // the wusch shape, Trigger_Urwald.tcl:105-108

            StepSeconds(sim, 2.0);
            Assert.AreEqual("0", V(own, "fired"), "out of range (9 > 2) — must NOT fire");
            // With candidates present but none in range the poll tightens to the NEAR
            // interval 0.5 s (FUN_0060e410:123-129, DAT_00737230).
            Assert.AreEqual(0.5, ctx.TriggerService!.FindByOwner(own.Handle)!.CheckTimer, 1e-9);

            dw.Position = new Vector3(10f, 11.5f, 10f);          // distance 1.5 ≤ 2
            StepSeconds(sim, 0.7);                                // ≥ one 0.5 s near-poll
            Assert.AreEqual("1", V(own, "fired"), "actuator entered range — must fire");
            // Actuator list = the object ids that tripped it (FUN_0060e410:99-111).
            Assert.AreEqual(dw.Handle.ToString(), interp.Eval($"trigger get_actuators {own.Handle}").Trim());
        }

        [Test]
        public void AnyObject_IsOneShot_UntilSetInactive_Rearms()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            var dw = Spawn(interp, ctx, "Zwerg", "1 0 0");       // already in default range 5

            interp.Eval($"trigger create {own.Handle} any_object {{incr nfires}}");
            StepSeconds(sim, 0.2);
            Assert.AreEqual("1", V(own, "nfires"), "fires on the first due check (lastCheck = now − 4.9)");

            // NOTHING in FUN_0060e410 ever clears the active flag — leaving and re-entering
            // range must NOT re-fire (one-shot until a script re-arms it).
            dw.Position = new Vector3(50f, 0f, 0f);
            StepSeconds(sim, 2.0);
            dw.Position = new Vector3(1f, 0f, 0f);
            StepSeconds(sim, 2.0);
            Assert.AreEqual("1", V(own, "nfires"), "any_object is one-shot — active stays set");

            // set_inactive (+0x38 = 0, FUN_0060e920:627-637) re-arms the edge.
            interp.Eval($"trigger set_inactive {own.Handle}");
            StepPastCurrentPoll(sim, ctx, own.Handle);
            Assert.AreEqual("2", V(own, "nfires"), "set_inactive re-arms the on_activate edge");
        }

        [Test]
        public void AnyObject_TargetCountGatesThePopulation_NotTheInRangeCount()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            var near = Spawn(interp, ctx, "Zwerg", "1 0 0");     // in range

            // FUN_0060e410:62 gates on the FILTERED POPULATION (`targetCount <= candidates`);
            // the fire itself needs only ONE candidate within range (:94-113).
            interp.Eval($@"
                trigger create {own.Handle} any_object {{set fired 1}}
                trigger set_target_class {own.Handle} Zwerg
                trigger set_target_count {own.Handle} 2
            ");
            StepSeconds(sim, 2.0);
            Assert.AreEqual("0", V(own, "fired"), "population 1 < target_count 2 — gate closed");

            Spawn(interp, ctx, "Zwerg", "200 200 200");          // 2nd dwarf ANYWHERE opens the gate
            StepSeconds(sim, 1.0);
            Assert.AreEqual("1", V(own, "fired"),
                "population 2 ≥ count 2 and one candidate in range — must fire " +
                "(the gate counts the population, not the in-range set)");
        }

        [Test]
        public void AnyObject_ClassTypeAndOwnerFilters()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            var monster = Spawn(interp, ctx, "TMonster", "1 0 0");
            var dw = Spawn(interp, ctx, "Zwerg", "1 0 0");
            interp.Eval($"set_owner {dw.Handle} 1");             // wrong player

            // FirstContact shape (Trigger_Urwald.tcl:225-229): type gnome + owner 0.
            interp.Eval($@"
                trigger create {own.Handle} any_object {{set fired 1}}
                trigger set_target_range {own.Handle} 2.0
                trigger set_target_type {own.Handle} gnome
                trigger set_target_owner {own.Handle} 0
                trigger set_target_count {own.Handle} 1
            ");
            StepSeconds(sim, 2.0);
            Assert.AreEqual("0", V(own, "fired"),
                "monster fails the type filter (rec+0x58), the owner-1 dwarf fails the owner mask (rec+0x5c)");

            interp.Eval($"set_owner {dw.Handle} 0");
            StepPastCurrentPoll(sim, ctx, own.Handle);
            Assert.AreEqual("1", V(own, "fired"), "owner 0 dwarf of kind gnome passes both filters");
            Assert.AreEqual(dw.Handle.ToString(), interp.Eval($"trigger get_actuators {own.Handle}").Trim(),
                "only the dwarf actuates — the monster never enters the list");
        }

        // ---------------------------------------------------------------------------
        // unique_object (FUN_0060e780)
        // ---------------------------------------------------------------------------

        [Test]
        public void UniqueObject_TracksOnlyTheTargetRef()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            var tgt = Spawn(interp, ctx, "Zwerg", "100 0 0");
            Spawn(interp, ctx, "Zwerg", "1 0 0");                // a DIFFERENT dwarf, already close

            // Trigger_Urwald.tcl:510-513 shape.
            interp.Eval($@"
                trigger create {own.Handle} unique_object {{set fired 1}}
                trigger set_target_count {own.Handle} 1
                trigger set_target_range {own.Handle} 15
                trigger set_target_ref {own.Handle} {tgt.Handle}
            ");
            StepSeconds(sim, 2.0);
            Assert.AreEqual("0", V(own, "fired"), "only the set_target_ref object counts — the near stranger doesn't");

            tgt.Position = new Vector3(10f, 0f, 0f);             // within 15
            StepPastCurrentPoll(sim, ctx, own.Handle);
            Assert.AreEqual("1", V(own, "fired"));
            Assert.AreEqual(tgt.Handle.ToString(), interp.Eval($"trigger get_actuators {own.Handle}").Trim(),
                "actuators = [the ref'd object] (FUN_0060e780:53-78)");
        }

        // ---------------------------------------------------------------------------
        // timers (FUN_0060df40:58-80)
        // ---------------------------------------------------------------------------

        [Test]
        public void SingleTimer_FiresOnceAtDeadline()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            // Trigger_Schwefel.tcl:151 / Trigger_Trailer.tcl:33 shape.
            interp.Eval($@"
                trigger create {own.Handle} single_timer {{incr nfires}}
                trigger set_timer {own.Handle} 2
            ");
            // set_timer clamps the checktimer to ≤ 1.0 s (DAT_007372a8, FUN_0060e920:389-394),
            // so the deadline is hit within a second of t=2.
            StepSeconds(sim, 1.9);
            Assert.AreEqual("0", V(own, "nfires"), "deadline now+2 not reached (FUN_0060df40:59)");
            StepSeconds(sim, 1.2);
            Assert.AreEqual("1", V(own, "nfires"), "fires once the deadline passes");
            StepSeconds(sim, 3.0);
            Assert.AreEqual("1", V(own, "nfires"), "single_timer is one-shot (+0x38 latches)");
        }

        [Test]
        public void RepeatTimer_PulsesOncePerPeriod()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            interp.Eval($@"
                trigger create {own.Handle} repeat_timer {{incr nfires}}
                trigger set_timer {own.Handle} 1
                trigger set_checktimer {own.Handle} 0.2
            ");
            // Pulse machine (FUN_0060df40:65-80): deadline reached → activate+fire,
            // deadline += interval; the next check INSIDE the period deactivates (no fire
            // with on_activate) → ~1 fire per second.
            StepSeconds(sim, 4.6);
            int n = int.Parse(V(own, "nfires"));
            Assert.That(n, Is.InRange(3, 5), $"~1 fire/second over 4.6 s, got {n}");
        }

        // ---------------------------------------------------------------------------
        // callback condition (FUN_0060df40:88-130) + execution modes
        // ---------------------------------------------------------------------------

        [Test]
        public void Callback_EdgeFires_OnActivate_AndOnInactivate()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            interp.Eval($@"
                trigger create {own.Handle} callback {{incr nfires}}
                trigger set_callback {own.Handle} {{expr $armflag}}
                trigger set_checktimer {own.Handle} 0.5
            ");
            StepSeconds(sim, 2.0);
            Assert.AreEqual("0", V(own, "nfires"), "condition false — no rising edge yet");

            own.VarsScope.Vars["armflag"] = "1";                 // condition turns true
            StepSeconds(sim, 1.0);
            Assert.AreEqual("1", V(own, "nfires"), "rising edge fires once (FUN_0060df40:124-127)");
            StepSeconds(sim, 2.0);
            Assert.AreEqual("1", V(own, "nfires"), "condition STAYING true must not re-fire (level → edge)");

            own.VarsScope.Vars["armflag"] = "0";                 // falls
            StepSeconds(sim, 1.0);
            own.VarsScope.Vars["armflag"] = "1";                 // rises again
            StepSeconds(sim, 1.0);
            Assert.AreEqual("2", V(own, "nfires"), "false→true re-arms the rising edge");

            // on_inactivate (mode 3) fires on the FALLING edge (:118-123).
            var own2 = Spawn(interp, ctx, "TOwner", "0 0 0");
            interp.Eval($@"
                trigger create {own2.Handle} callback {{incr nfires}}
                trigger set_callback {own2.Handle} {{expr $armflag}}
                trigger set_checktimer {own2.Handle} 0.5
                trigger set_execution {own2.Handle} on_inactivate
            ");
            own2.VarsScope.Vars["armflag"] = "1";
            StepSeconds(sim, 1.0);
            Assert.AreEqual("0", V(own2, "nfires"), "on_inactivate ignores the rising edge");
            own2.VarsScope.Vars["armflag"] = "0";
            StepSeconds(sim, 1.0);
            Assert.AreEqual("1", V(own2, "nfires"), "fires on the falling edge");
        }

        [Test]
        public void Tutorial1028Shape_CallbackExprOne_FiresOnFirstScan()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            // Trigger_Tutorial.tcl:1028-1030 verbatim shape (fire script swapped for a var).
            interp.Eval($@"
                trigger create {own.Handle} callback {{set fired 1}}
                trigger set_callback {own.Handle} {{expr 1}}
                trigger set_checktimer {own.Handle} 1
            ");
            StepSeconds(sim, 0.2);
            Assert.AreEqual("1", V(own, "fired"),
                "ctor lastCheck = now − 4.9 makes the first check due at once; `expr 1` → rising edge");
        }

        [Test]
        public void Execution_WhenInavtiveTypo_RunsEveryCheckWhileInactive_CorrectSpellingErrors()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            interp.Eval($@"
                trigger create {own.Handle} callback {{incr nfires}}
                trigger set_callback {own.Handle} {{expr 0}}
                trigger set_checktimer {own.Handle} 0.5
                trigger set_execution {own.Handle} when_inavtive
            ");
            // Mode 5: the fire script runs EVERY check while the trigger is inactive
            // (FUN_0060df40:190-208) — not edge-gated.
            StepSeconds(sim, 2.2);
            Assert.GreaterOrEqual(int.Parse(V(own, "nfires")), 3,
                "when_inavtive (the native's own typo, FUN_0060e920:346) runs per check while inactive");

            // The CORRECTLY spelled word matches nothing in the native string table →
            // "invalid trigger execution" (FUN_0060e920:367-374). Preserved verbatim.
            var ex = Assert.Throws<TclException>(
                () => interp.Eval($"trigger set_execution {own.Handle} when_inactive"));
            StringAssert.Contains("invalid trigger execution", ex!.Message);
        }

        // ---------------------------------------------------------------------------
        // scan gating + lifecycle
        // ---------------------------------------------------------------------------

        [Test]
        public void SequenceMode_SuppressesTheWholeScan()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            Spawn(interp, ctx, "Zwerg", "1 0 0");                // in default range 5
            interp.Eval($"trigger create {own.Handle} any_object {{set fired 1}}");

            ctx.SequenceActive = true;                           // set_sequence 1
            StepSeconds(sim, 2.0);
            Assert.AreEqual("0", V(own, "fired"),
                "FUN_0060df40:24 — no trigger scans while the sequence byte (+0x8c) is set");

            ctx.SequenceActive = false;
            StepSeconds(sim, 0.2);
            Assert.AreEqual("1", V(own, "fired"), "scan resumes after set_sequence 0");
        }

        [Test]
        public void OwnerDestroyed_TriggerIsReaped()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            interp.Eval($"trigger create {own.Handle} any_object {{set fired 1}}");
            StepSeconds(sim, 0.2);
            Assert.AreEqual(1, ctx.TriggerService!.LiveCount);

            interp.Eval($"del {own.Handle}");
            // Owner slot empty → kind=Dead (FUN_0060e410:46-49); the NEXT pass reaps the
            // dead record (FUN_0060df40:132-143). Default polls are 0.5/3.0 s apart.
            StepSeconds(sim, 7.0);
            Assert.AreEqual(0, ctx.TriggerService.LiveCount, "triggers die with their owner");
        }

        [Test]
        public void FireScript_CanDeleteItsOwnTrigger_MidScan()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            Spawn(interp, ctx, "Zwerg", "1 0 0");
            // Trigger_Urwald.tcl:564 fire script shape: `trigger delete this; …` — the scan
            // must survive the record vanishing under it (the native re-reads the slot,:148).
            interp.Eval($"trigger create {own.Handle} any_object {{trigger delete this ; set fired 1}}");
            StepSeconds(sim, 0.5);
            Assert.AreEqual("1", V(own, "fired"));
            Assert.AreEqual(0, ctx.TriggerService!.LiveCount);
        }

        // ---------------------------------------------------------------------------
        // intro-compat guard (DigglesWorldRuntime ↔ TriggerService mutual exclusion)
        // ---------------------------------------------------------------------------

        [Test]
        public void SuppressSequence_ConsumesArmedTrigger_AndLateArmedOne()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            var own = Spawn(interp, ctx, "TOwner", "0 0 0");
            var dw = Spawn(interp, ctx, "Zwerg", "100 0 0");     // out of range — armed, unfired
            interp.Eval($"trigger create {own.Handle} any_object {{set fired 1}}");
            own.VarsScope.Vars["sequencescript"] = "urw_3";
            StepSeconds(sim, 0.2);

            // Sweep: the armed same-sequence trigger is consumed (≡ native set_active).
            Assert.AreEqual(1, ctx.TriggerService!.SuppressSequence("urw_3"));
            dw.Position = new Vector3(1f, 0f, 0f);
            StepSeconds(sim, 2.0);
            Assert.AreEqual("0", V(own, "fired"), "consumed trigger must not fire after the staged intro played");

            // Late arm: a trigger CREATED after the suppression (its arming event was queued
            // behind the cutscene freeze) is swallowed once at fire time, then the guard clears.
            var own2 = Spawn(interp, ctx, "TOwner", "0 0 0");
            own2.VarsScope.Vars["sequencescript"] = "urw_3";
            interp.Eval($"trigger create {own2.Handle} any_object {{incr nfires}}");
            StepSeconds(sim, 1.0);
            Assert.AreEqual("0", V(own2, "nfires"), "late-armed same-sequence trigger swallowed once");
            Assert.IsNull(ctx.TriggerService.SuppressedSequence, "the late guard is one-shot");
            interp.Eval($"trigger set_inactive {own2.Handle}");
            StepPastCurrentPoll(sim, ctx, own2.Handle);
            Assert.AreEqual("1", V(own2, "nfires"), "after the guard cleared the trigger works normally");
        }

        // ---------------------------------------------------------------------------
        // Trigger_Urwald end-to-end — the urw_3 arming chain (Trigger_Urw_006 shape)
        // ---------------------------------------------------------------------------

        [Test]
        public void Urw006Shape_TimerArmsProximityTrigger_FiresHandler()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval(LightClasses);
            // Trigger_Urwald.tcl:921-940 verbatim shape — only the fire script is swapped
            // from `sequencer_activate` to a var write (the proc chain is covered by the
            // [Explicit] real-file test below).
            interp.Eval(@"
                def_class TrigUrw006 none dummy 0 {} {
                    def_event evt_timer0
                    obj_init {
                        set seq_fired 0
                        timer_event this evt_timer0 -repeat 0 -userid 0 -attime [expr [gettime]+5]
                    }
                    handle_event evt_timer0 {
                        trigger create this any_object ""set seq_fired 1""
                        set sequencescript ""urw_3""
                        trigger set_target_range this 40
                        trigger set_target_class this Zwerg
                        trigger set_target_owner this 0
                        trigger set_target_count this 1
                    }
                }
            ");
            var trig = Spawn(interp, ctx, "TrigUrw006", "33 40 8");
            var dw = Spawn(interp, ctx, "Zwerg", "60 40 8");     // distance 27 < 40 — at the camp
            // The trigger filters `set_target_owner 0`; spawn default is now OWNERLESS (-1,
            // FUN_005c83f0:196) — give the founder the player owner it has in the real boot.
            interp.Eval($"set_owner {dw.Handle} 0");

            StepSeconds(sim, 4.8);
            Assert.IsNull(ctx.TriggerService!.FindByOwner(trig.Handle),
                "nothing arms before the t=5 evt_timer0 (Trigger_Urwald.tcl:928)");
            Assert.AreEqual("0", V(trig, "seq_fired"));

            StepSeconds(sim, 0.4);   // t≈5.2: timer delivers → handler arms → same-pass scan fires
            Assert.AreEqual("1", V(trig, "seq_fired"),
                "the founders are within range 40 → the urw_3 trigger fires at once (the original boot)");
            Assert.AreEqual("urw_3", V(trig, "sequencescript"));
            Assert.AreEqual(dw.Handle.ToString(), interp.Eval($"trigger get_actuators {trig.Handle}").Trim(),
                "the in-range dwarf is the actuator the sequencer will add_actor (sequencer.tcl:397-403)");
        }

        // Heavy end-to-end: the REAL Trigger_Urwald.tcl class file + the REAL sequencer,
        // driven purely by the trigger engine — arm at t=5, proximity fire, sequencer_activate
        // engages sequence mode, scene plays out and shuts. [Explicit]: loads the full story
        // class file (~30 def_class bodies, each pulling sequencer.tcl).
        [Test, Explicit("heavy: real Trigger_Urwald.tcl + real sequencer end-to-end")]
        public void RealTriggerUrw006_DrivesTheSequencer_EndToEnd()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            interp.RegisterCommand("puts", (i, a) => "");
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);

            interp.Eval("def_class Zwerg none gnome 0 {} {}");   // light dwarf — the chain needs only class+kind+pos
            interp.Eval("call scripts/classes/story/Trigger_Urwald.tcl");
            Assert.IsTrue(ctx.Classes.ContainsKey("Trigger_Urw_006"), "real story class loaded");

            var trig = ctx.GetEntity(interp.Eval("new Trigger_Urw_006 \"\" \"33 40 8\" \"0 0 0\"").Trim())!;
            var dw = ctx.GetEntity(interp.Eval("new Zwerg \"\" \"133 40 8\" \"0 0 0\"").Trim())!;   // 100 away

            StepSeconds(sim, 5.3);    // t=5: evt_timer0 → `trigger create this any_object "sequencer_activate"`
            var rec = ctx.TriggerService!.FindByOwner(trig.Handle);
            Assert.NotNull(rec, "the real handler armed the trigger (Trigger_Urwald.tcl:934)");
            Assert.AreEqual(40f, rec!.TargetRange, "range 40 (:936)");
            Assert.AreEqual("urw_3", V(trig, "sequencescript"));
            Assert.IsFalse(ctx.SequenceActive, "dwarf 100 away — nothing fired yet");

            // Point the armed trigger at a tiny scene instead of the full urw_3 production
            // (parse_file takes absolute paths — same redirect the cutscene tests use).
            string seqPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"dgl_trigseq_{System.DateTime.Now.Ticks}.tcl").Replace('\\', '/');
            System.IO.File.WriteAllText(seqPath, "do_wait time 1\n");
            try
            {
                trig.VarsScope.Vars["sequencescript"] = seqPath;
                dw.Position = new Vector3(35f, 40f, 8f);          // walks into range 40
                StepSeconds(sim, 1.0);
                Assert.IsTrue(ctx.SequenceActive,
                    "proximity fire → sequencer_activate (the REAL proc) → set_sequence 1");

                int steps = 0;
                for (; steps < 2000 && ctx.SequenceActive; steps++) sim.Step(0.05);
                Assert.IsFalse(ctx.SequenceActive, "scene must end (do_wait 1 → EOF → shut_sequencer)");
                // shut_sequencer's destroy chain deletes the trigger OBJECT; the scan then
                // reaps the orphaned record (owner slot empty → dead → removed).
                StepSeconds(sim, 7.0);
                Assert.AreEqual(0, ctx.TriggerService.LiveCount, "trigger died with its object");
            }
            finally { try { System.IO.File.Delete(seqPath); } catch { } }
        }
    }
}
