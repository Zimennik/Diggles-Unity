#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    // CAMPAIGN STORY PROGRESSION — item 16 (+ item 7 rider).
    //
    // Ground truth:
    // • sm_send_message = "TCL:CTclObjMethods::SMSendMessage" (FUN_006a9f40) →
    // CStoryMgr::HandleTriggerMessage (FUN_005f8cf0):
    // 1) auto-SETS every registered story event whose (trigger class, keyword) matches
    // (sender class, message) and whose flag is still 0 — keyword defaults to
    // "activate" (SMAddEvent FUN_0065a820 builds the literal "activate" for 3-arg
    // registrations, overwritten only when argc == 4);
    // 2) evals the sm_register'd callback as "<proc> <class> <msg> <objid>"
    // ("%s %s %s %i", FUN_006b9040) in the registered object's interp →
    // story.tcl:398 `proc callback {cb_trg cb_msg cb_tid}` → newslist.
    // • sm_add_event redefinition is a native TCL_ERROR ("Story Event redefinition",
    // FUN_0065a820:118-125) — an existing event's metadata is NEVER overwritten
    // (Trigger_Urwald.tcl:678 re-registers Torwaechterin_befreit inside catch{}).
    // • Message senders: sequencer.tcl "create" (:348) / "activate" (:391) / "shut"
    // (:2002,:2169); custom "RdL" (Trigger_Urwald.tcl:1071).
    // • The Metalltor ("Gates of Life") cutscene condition start_seq
    // (Trigger_Urwald.tcl:691-712) gates on `sm_get_event Torwaechterin_befreit`
    // (CampaignInit.tcl:593 binds that event to Trigger_urw_unq_troll_005_Befreien) —
    // with sm_send_message stubbed the gate scene could NEVER fire.
    // • The fairy-quest END trigger (Trigger_Urw_unq_feen_End, Trigger_Urwald.tcl:876-918)
    // polls CheckWasser: dwarf within 5 AND `[get_fsource $wflag -volume] <= 50`
    // against the feen well's registered budget (wasser.tcl:57; template volume 85 —
    // splt_urw_unq_feen_tBC.tcl:91). The bulk "0" stub for get_fsource made the volume
    // read lie → QL_Save_the_Fairys completed the moment a dwarf walked in (item 16a).
    public sealed class StoryProgressTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [TearDown]
        public void ResetLogAssert() => LogAssert.ignoreFailingMessages = false;

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host(bool realData = false)
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            if (realData)
            {
                interp.TolerateExprErrors = true;
                interp.TolerateMissingVars = true;
                ctx.DataRoot = DataRoot;
                ctx.TolerateMissingEntities = true;
                ctx.TolerateMissingClass = true;
                interp.UnknownCommandHandler = (i, a) => "0";
                interp.RegisterCommand("puts", (i, a) => "");
            }
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static string V(DigglesTclEntity e, string n)
            => e.VarsScope.Vars.TryGetValue(n, out var v) ? v : "<none>";

        static void StepSeconds(DigglesGameSim sim, double secs)
        {
            int n = (int)System.Math.Round(secs / 0.1);
            for (int i = 0; i < n; i++) sim.Step(0.1);   // one 10 Hz logic tick per Step
        }

        // ---------------------------------------------------------------------------
        // sm_send_message — event auto-set + story callback (unit surface)
        // ---------------------------------------------------------------------------

        [Test]
        public void SmSendMessage_DefaultKeyword_SetsEventOnActivateOnly()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval("def_class Trigger_Foo none dummy 0 {} {}");
            var t = Spawn(interp, ctx, "Trigger_Foo", "10 10 10");

            // 3-arg registration → keyword defaults to "activate" (FUN_0065a820).
            interp.Eval("sm_add_event Foo_Entdeckt Trigger_Foo");
            Assert.AreEqual("0", interp.Eval("sm_get_event Foo_Entdeckt").Trim());

            // "create"/"shut" (the other sequencer lifecycle messages) must NOT set it.
            interp.Eval($"sm_send_message {t.Handle} create");
            interp.Eval($"sm_send_message {t.Handle} shut");
            Assert.AreEqual("0", interp.Eval("sm_get_event Foo_Entdeckt").Trim(),
                "default keyword is 'activate' — create/shut must not latch the event");

            interp.Eval($"sm_send_message {t.Handle} activate");
            Assert.AreEqual("1", interp.Eval("sm_get_event Foo_Entdeckt").Trim(),
                "sequencer_activate's `sm_send_message this activate` must latch the event " +
                "(FUN_005f8cf0 first loop — the Torwaechterin_befreit mechanism)");
        }

        [Test]
        public void SmSendMessage_CustomKeyword_And_WrongClass()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval("def_class Trigger_RingChk none dummy 0 {} {}");
            interp.Eval("def_class Trigger_Other none dummy 0 {} {}");
            var rc = Spawn(interp, ctx, "Trigger_RingChk", "10 10 10");
            var other = Spawn(interp, ctx, "Trigger_Other", "10 10 10");

            // The CampaignInit.tcl:594 shape: sm_add_event Ring_Der_Natur_erhalten Trigger_RingCheck "RdL"
            interp.Eval("sm_add_event Ring_X_erhalten Trigger_RingChk RdL");

            interp.Eval($"sm_send_message {rc.Handle} activate");            // wrong message
            interp.Eval($"sm_send_message {other.Handle} RdL");              // wrong sender class
            Assert.AreEqual("0", interp.Eval("sm_get_event Ring_X_erhalten").Trim());

            interp.Eval($"sm_send_message {rc.Handle} RdL");                 // Trigger_Urwald.tcl:1071
            Assert.AreEqual("1", interp.Eval("sm_get_event Ring_X_erhalten").Trim());
        }

        [Test]
        public void SmAddEvent_Redefinition_KeepsTriggerMetadata()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval("def_class Trigger_Foo none dummy 0 {} {}");
            var t = Spawn(interp, ctx, "Trigger_Foo", "10 10 10");

            interp.Eval("sm_add_event Foo_Frei Trigger_Foo");
            // The Metalltor obj_init shape (Trigger_Urwald.tcl:678): defensive bare re-add.
            // Native = TCL_ERROR before the metadata store — the binding must SURVIVE.
            interp.Eval("catch { sm_add_event Foo_Frei }");

            interp.Eval($"sm_send_message {t.Handle} activate");
            Assert.AreEqual("1", interp.Eval("sm_get_event Foo_Frei").Trim(),
                "a bare sm_add_event redefinition must not clobber the trigger binding " +
                "(FUN_0065a820 errors out BEFORE the metadata store)");
        }

        [Test]
        public void SmSendMessage_InvokesRegisteredStoryCallback()
        {
            var (interp, ctx, sim) = Host();
            // story.tcl shape: the StoryMgr object sm_register's its own per-object `callback`
            // proc (story.tcl: `sm_register this "callback"`), signature {cb_trg cb_msg cb_tid}.
            interp.Eval(@"
                def_class SMgr none dummy 0 {} {
                    obj_init {
                        set got {}
                        proc callback {cb_trg cb_msg cb_tid} {
                            global got
                            lappend got [list $cb_trg $cb_msg $cb_tid]
                        }
                    }
                }
                def_class Trigger_Foo none dummy 0 {} {}");
            var sm = Spawn(interp, ctx, "SMgr", "0 0 0");
            var t = Spawn(interp, ctx, "Trigger_Foo", "10 10 10");
            interp.Eval($"sm_register {sm.Handle} callback");

            interp.Eval($"sm_send_message {t.Handle} shut");
            // "%s %s %s %i" → "callback Trigger_Foo shut <id>" evaluated in the SMgr's scope.
            Assert.AreEqual($"{{Trigger_Foo shut {t.Handle}}}", V(sm, "got"),
                "the registered story callback must receive (senderClass, message, senderId) — " +
                "the newslist/questlog feed (story.tcl:398)");
        }

        // ---------------------------------------------------------------------------
        // Metalltor ("Gates of Life") cutscene chain — REAL Trigger_Urwald.tcl
        // ---------------------------------------------------------------------------

        [Test]
        public void Metalltor_GateScene_FiresAfterGatekeeperFreed_RingCarried()
        {
            LogAssert.ignoreFailingMessages = true;   // the fire runs the real sequencer headless
            var (interp, ctx, sim) = Host(realData: true);
            interp.Eval("def_class Zwerg none gnome 0 {} {}");
            interp.Eval("def_class Ring_Des_Lebens none material 0 {} {}");
            interp.Eval("call scripts/classes/story/Trigger_Urwald.tcl");

            // CampaignInit.tcl:593 — the event↔trigger binding (default keyword "activate").
            interp.Eval("sm_add_event Torwaechterin_befreit Trigger_urw_unq_troll_005_Befreien");

            var gate = Spawn(interp, ctx, "Trigger_urw_unq_metalltor_oeffnen", "100 100 10");
            var dwarf = Spawn(interp, ctx, "Zwerg", "102 100 10");            // within range 5/6
            interp.Eval($"set_owner {dwarf.Handle} 0");
            var ring = Spawn(interp, ctx, "Ring_Des_Lebens", "102 100 10");
            interp.Eval($"inv_add {dwarf.Handle} {ring.Handle}");

            // Arm (+1 s timer) and poll a few times: the gatekeeper is NOT freed yet →
            // start_seq's first gate (Trigger_Urwald.tcl:693) returns 0 — nothing may fire.
            StepSeconds(sim, 6.0);
            // Not-fired sentinel: sequencer.tcl:302 seeds every sequencer object with the
            // seq_test2.tcl placeholder; start_seq is the only writer of a real scene name.
            Assert.AreEqual("data/scripts/classes/sequencer/sequences/seq_test2.tcl", V(gate, "sequencescript"),
                "gate scene must NOT fire before Torwaechterin_befreit is set");
            Assert.IsFalse(ctx.SequenceActive);

            // Free the gatekeeper: her trigger's sequence ACTIVATES → sequencer.tcl:391
            // sends "activate" → the event latches (this exact line was the dead stub).
            var befreien = Spawn(interp, ctx, "Trigger_urw_unq_troll_005_Befreien", "300 300 10");
            interp.Eval($"sm_send_message {befreien.Handle} activate");
            Assert.AreEqual("1", interp.Eval("sm_get_event Torwaechterin_befreit").Trim(),
                "Trigger_urw_unq_troll_005_Befreien 'activate' must latch Torwaechterin_befreit");

            // Next polls: gate passes, ringcheck finds the carried ring → Urw_040_b.
            StepSeconds(sim, 4.0);
            Assert.AreEqual("Urw_040_b", V(gate, "sequencescript"),
                "with the gatekeeper freed and the Ring des Lebens carried, the gate trigger " +
                "must select the Urw_040_b scene (Trigger_Urwald.tcl:708) and fire");
        }

        // ---------------------------------------------------------------------------
        // Fairy-quest END trigger — REAL Trigger_Urwald.tcl + wasser.tcl
        // ---------------------------------------------------------------------------

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim,
                DigglesTclEntity feenEnd, DigglesTclEntity well) FairyHost()
        {
            var (interp, ctx, sim) = Host(realData: true);
            interp.Eval("def_class Zwerg none gnome 0 {} {}");
            // FogRemover stand-in (real one lives in infoobjs.tcl — irrelevant here).
            interp.Eval(@"
                def_class FogRemover none info 0 {} {
                    method fog_remove {a b c} {}
                    method timer_delete {t} {}
                }");
            interp.Eval("call scripts/classes/items/wasser.tcl");
            interp.Eval("call scripts/classes/story/Trigger_Urwald.tcl");

            // The feen well: WasserTrans type "out", volume 85 (splt_urw_unq_feen_tBC.tcl:86-91).
            var well = Spawn(interp, ctx, "WasserTrans", "5 0 0");
            interp.Eval($"call_method {well.Handle} Editor_Set_Info {{{{type out}} {{volume 85}} {{vpf 0.32}}}}");

            var feenEnd = Spawn(interp, ctx, "Trigger_Urw_unq_feen_End", "0 0 0");
            var dwarf = Spawn(interp, ctx, "Zwerg", "1 0 0");   // within CheckWasser's range 5
            interp.Eval($"set_owner {dwarf.Handle} 0");
            return (interp, ctx, sim, feenEnd, well);
        }

        [Test]
        public void FairyQuestEnd_DoesNotComplete_WhileWellBudgetHigh()
        {
            LogAssert.ignoreFailingMessages = true;
            var (interp, ctx, sim, feenEnd, well) = FairyHost();

            // WasserTrans registers its fsource at +1 s; the End trigger arms at +1.5 s and
            // polls CheckWasser every second. Volume 85 > 50 → NOT done, dwarf or no dwarf.
            StepSeconds(sim, 8.0);
            Assert.IsNotNull(ctx.TriggerService!.FindByOwner(feenEnd.Handle),
                "the exact playtest bug: with the well budget at 85 the clip_010 trigger must " +
                "NOT fire just because a dwarf stands next to it (CheckWasser reads " +
                "[get_fsource -volume] = 85 > 50 → not done; the old '0' stub lied)");
            Assert.IsFalse(ctx.SequenceActive, "no cutscene may start");
            Assert.AreEqual("85", interp.Eval($"get_fsource {well.Handle} -volume").Trim());
        }

        [Test]
        public void FairyQuestEnd_Completes_WhenWellBudgetDrained()
        {
            LogAssert.ignoreFailingMessages = true;
            var (interp, ctx, sim, feenEnd, well) = FairyHost();
            StepSeconds(sim, 3.0);   // arm + a first (false) poll

            // The solved puzzle drains the well below the law's threshold (native fluid sim;
            // stand-in: set the stored budget directly). CheckWasser: 40 ≤ 50 AND dwarf near → done.
            interp.Eval($"set_fsource {well.Handle} -volume 40");
            StepSeconds(sim, 4.0);
            Assert.IsNull(ctx.TriggerService!.FindByOwner(feenEnd.Handle),
                "once the volume law passes, the trigger must fire its script " +
                "('remove_fow; trigger delete this; sequencer_activate') and delete itself");
        }

        // ---------------------------------------------------------------------------
        // set_fsource / get_fsource surface
        // ---------------------------------------------------------------------------

        [Test]
        public void FSource_MergeSemantics_AndDefaults()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval("def_class WTrans none dummy 0 {} {}");
            var w = Spawn(interp, ctx, "WTrans", "0 0 0");

            // Unregistered object → empty fluid cell: numeric 0, type water (FUN_006997b0 law).
            Assert.AreEqual("0", interp.Eval($"get_fsource {w.Handle} -volume").Trim());
            Assert.AreEqual("water", interp.Eval($"get_fsource {w.Handle} -type").Trim());

            // wasser.tcl:57 registration shape.
            interp.Eval($"set_fsource {w.Handle} -pos {{0 -1 0}} -type water -vpf 0 -volume 85");
            Assert.AreEqual("85", interp.Eval($"get_fsource {w.Handle} -volume").Trim());

            // wasser.tcl:85 `set_fsource this -vpf 0` — a partial update must MERGE.
            interp.Eval($"set_fsource {w.Handle} -vpf 0.32");
            Assert.AreEqual("85", interp.Eval($"get_fsource {w.Handle} -volume").Trim(),
                "partial set_fsource must keep the other stored fields");
            Assert.AreEqual("0.32", interp.Eval($"get_fsource {w.Handle} -vpf").Trim());

            // argc law: get_fsource takes exactly obj + flag (native wrong-#-args).
            Assert.AreEqual("1", interp.Eval($"catch {{ get_fsource {w.Handle} }}").Trim());
        }

        // ---------------------------------------------------------------------------
        // Start-meadow mushrooms: the template ages must survive boot.
        // urw_unq_start spawns 5 Pilz on the meadow with Editor_Set_Info ages {2,2,2,1,3}
        // (splt_urw_unq_start_tBB.tcl:37-70) — ONE full-grown mushroom, not five.
        // ---------------------------------------------------------------------------

        [Test]
        public void StartMeadow_PilzAges_MatchTemplateLaw()
        {
            LogAssert.ignoreFailingMessages = true;
            var (interp, ctx, sim) = Host(realData: true);
            interp.Eval("call scripts/classes/items/pilz.tcl");

            int[] ages = { 2, 2, 2, 1, 3 };   // the tBB meadow cluster, template order
            var pilze = new List<DigglesTclEntity>();
            for (int k = 0; k < ages.Length; k++)
            {
                var p = Spawn(interp, ctx, "Pilz", $"{24 + k} 30 9");
                interp.Eval($"set_owner {p.Handle} -1");
                interp.Eval($"call_method {p.Handle} Editor_Set_Info {{{{age {ages[k]}}}}}");
                pilze.Add(p);
            }

            // Editor_Set_Info sets agephase = age-1 and schedules one aging step at +2 s
            // (pilz.tcl:52-57); the step advances agephase to the authored age (pilz.tcl:89-116).
            StepSeconds(sim, 4.0);

            for (int k = 0; k < ages.Length; k++)
            {
                Assert.AreEqual(ages[k].ToString(), V(pilze[k], "agephase"),
                    $"Pilz #{k} must sit at its authored growth phase (template age {ages[k]}) — " +
                    "the original start meadow shows ONE full mushroom + sprouts, not five");
                if (ages[k] < 3)
                    Assert.AreNotEqual("pilz.standard", pilze[k].CurrentAnim,
                        $"Pilz #{k} (age {ages[k]}) must NOT wear the full-grown anim at boot");
            }
            // Diagnostic dump for the render-side follow-up (visible in the test log).
            for (int k = 0; k < ages.Length; k++)
                Debug.Log($"[pilz-diag] #{k} age={ages[k]} agephase={V(pilze[k], "agephase")} anim={pilze[k].CurrentAnim}");
        }
    }
}
