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

    // The menu-backdrop cutscenes (Scripts/sequences/Menue/start_*.tcl) + the faithful skip
    // drain, driven headlessly through the real sequencer Tcl. The natives under test:
    // • gametime factor (FUN_0061ece0 → DigglesGameSim.TimeFactor — scales the whole sim step)
    // • get_sequencebreaked (FUN_00649330) + the eval_sequence drain (sequencer.tcl:228-239)
    // • action <obj> move (CAct_Move FUN_00416680/FUN_00416720/FUN_00416a30 — sq_object move)
    // • set_vel integration (CTclObjMethods::SetVel FUN_00685ec0 → sim phase 2b2)
    // • CmdNew spawner-failure isolation (sq_object summon's object_list stays aligned)
    // • the faithful menu loop: one persistent trigger (Trigger_Urwald.tcl:8-15 destroy_overload),
    // empty actor preset (sequencer.tcl:366), accumulating sequence_counter.
    //
    // The world is the faithful menu diorama reduced to what start_* needs: the two
    // Info_Pos_Zwerg/Info_Pos_ZwergTmp markers (unq_menue.tcl at MapTemplateSet 25 28 — every
    // start_* pen derives from them via Getobjpos) + a sequencer trigger with destroy_overload
    // (the original menu trigger survives shut_sequencer and replays forever).
    public sealed class MenuCutsceneTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // Marker positions: unq_menue.tcl's Info_Pos_Zwerg (:402) / Info_Pos_ZwergTmp (:408)
        // after the systeminit.tcl:76 `MapTemplateSet 25 28` offset.
        static readonly Vector3 MarkerZwerg = new Vector3(45.769f, 42.5f, 12.96f);
        static readonly Vector3 MarkerZwergTmp = new Vector3(24.36f, 42.5f, 12.86f);

        // The persistent menu trigger: same dual-load pattern as the game's triggers, plus
        // destroy_overload so shut_sequencer's destroy parks it (state sleep + disabled,
        // sequencer.tcl:2163-2166) instead of `del this` — mirrors Trigger_StartScreen.
        const string MenuSeqClass = @"
            def_class MenuSeq none dummy 0 {} {
                call scripts/classes/story/sequencer.tcl
                obj_init {
                    call scripts/classes/story/sequencer.tcl
                }
                method run {} { sequencer_activate }
                method destroy_overload {} {}
                method test_set_pen {name val} { global $name ; set $name $val }
                method test_summon {cls pen} { sq_object summon $cls $pen ; global object_list ; return [llength $object_list] }
                method test_object {idx} { return [Object $idx] }
                method test_move {idx pen speed} { sq_object move $idx $pen $speed }
            }";

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim, List<string> unknown) BuildHost()
        {
            var interp = new TclInterpreter();
            interp.TolerateExprErrors = true;
            interp.TolerateMissingVars = true;
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;   // Zwerg/Troll/Hamster/… synthesize as placeholders
            var sim = new DigglesGameSim(ctx);
            var unknown = new List<string>();
            interp.UnknownCommandHandler = (i, a) => { unknown.Add(a[0]); return "0"; };
            interp.RegisterCommand("puts", (i, a) => "");
            return (interp, ctx, sim, unknown);
        }

        static string V(Vector3 v) =>
            $"{v.x.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"{v.y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"{v.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        // Menu world = markers + one persistent trigger, no preset actors (faithful menu contract).
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim, int trigger,
                HashSet<int> baseline) BuildMenuWorld(int seed)
        {
            UnityEngine.Random.InitState(seed);   // pins irandom + expr rand (chance branches)
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(MenuSeqClass);
            interp.Eval($"new Info_Pos_Zwerg \"\" \"{V(MarkerZwerg)}\" \"0 0 0\"");
            interp.Eval($"new Info_Pos_ZwergTmp \"\" \"{V(MarkerZwergTmp)}\" \"0 0 0\"");
            int trigger = int.Parse(interp.Eval(@"set seq [new MenuSeq """" ""0 0 0"" ""0 0 0""]").Trim());
            var baseline = new HashSet<int>(ctx.Entities.Keys);
            return (interp, ctx, sim, trigger, baseline);
        }

        // Beat recorder over the interpreter's CommandTrace: every sq_object summon expands to
        // new → set_owner → set_pos (sequencer.tcl:884-889), so the (new result, first set_pos)
        // pairs reconstruct the spawn census + positions without keeping the (later deleted)
        // entities around.
        sealed class BeatRecorder
        {
            public readonly List<int> NewHandles = new();
            public readonly List<string> NewErrors = new();
            public readonly Dictionary<int, Vector3> FirstPos = new();
            public readonly List<int> Owners = new();
            public readonly List<string> FactorWrites = new();

            public void Attach(TclInterpreter interp) => interp.CommandTrace = Trace;

            static bool TryVec(string s, out Vector3 v)
            {
                var p = TclList.Parse(s);
                v = default;
                if (p.Count < 3) return false;
                if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
                if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
                if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)) return false;
                v = new Vector3(x, y, z);
                return true;
            }

            void Trace(string name, string[] args, string result)
            {
                switch (name)
                {
                    case "new":
                        if (result != null && result.StartsWith("!!ERR", System.StringComparison.Ordinal))
                            NewErrors.Add(args.Length > 1 ? args[1] + ": " + result : result);
                        else if (int.TryParse(result?.Trim(), out var nh))
                            NewHandles.Add(nh);
                        break;
                    case "set_pos":
                        if (args.Length >= 3 && int.TryParse(args[1], out var ph) && !FirstPos.ContainsKey(ph))
                        {
                            if (args.Length >= 5
                                && float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fx)
                                && float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fy)
                                && float.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fz))
                                FirstPos[ph] = new Vector3(fx, fy, fz);
                            else if (TryVec(args[2], out var pv))
                                FirstPos[ph] = pv;
                        }
                        break;
                    case "set_owner":
                        if (args.Length >= 3 && int.TryParse(args[2], out var ow)) Owners.Add(ow);
                        break;
                    case "gametime":
                        if (args.Length > 2 && args[1] == "factor") FactorWrites.Add(args[2]);
                        break;
                }
            }
        }

        static bool Finished(DigglesTclContext ctx, int trigger)
        {
            var e = ctx.GetEntity(trigger.ToString());
            if (e == null) return true;   // (non-menu classes del themselves)
            return e.VarsScope.Vars.TryGetValue("sequence_counter", out var sc) && sc != "0" && sc != "";
        }

        static string Diag(DigglesTclContext ctx, int trigger, DigglesGameSim sim)
        {
            var e = ctx.GetEntity(trigger.ToString());
            if (e == null) return "(trigger gone)";
            var v = e.VarsScope.Vars;
            string G(string k) => v.TryGetValue(k, out var s) ? s : "<none>";
            return $"state={e.CurrentState} enCnt={e.StateEnabledCount} act={(e.ActiveAction != null)} t={sim.GameTime:F1} " +
                   $"counter={G("sequence_counter")} wait_time_var={G("wait_time_var")} wait_for=({G("wait_for")}) " +
                   $"enable_list=({G("enable_list")}) actors=({G("actors")}) object_list=({G("object_list")})";
        }

        static void AssertCleanWorld(DigglesTclContext ctx, DigglesGameSim sim, HashSet<int> baseline, string when)
        {
            var extra = new List<int>();
            foreach (var h in ctx.Entities.Keys) if (!baseline.Contains(h)) extra.Add(h);
            var names = new List<string>();
            foreach (var h in extra) names.Add($"#{h}={ctx.Entities[h].Class?.Name}");
            Assert.IsEmpty(extra, $"{when}: leaked entities (summons must be cleaned by the script's own sq_object delete lines): {string.Join(", ", names)}");
            Assert.That(sim.TimeFactor, Is.EqualTo(1.0).Within(1e-9), $"{when}: gametime factor must be restored to 1");
            Assert.IsFalse(ctx.SequenceActive, $"{when}: sequence must be inactive");
            Assert.IsFalse(ctx.Audio!.VoicePlaying, $"{when}: voice stream must be stopped");
            foreach (var e in ctx.Entities.Values)
            {
                Assert.IsFalse(e.Parked, $"{when}: #{e.Handle} {e.Class?.Name} left Parked");
                // The trigger keeps its own sequenceactive=1 (init_sequencer sets it on `this`,
                // sequencer.tcl:530; shut only clears the actors' — faithful). Check actors/props only.
                if (e.Class?.Name == "MenuSeq") continue;
                Assert.IsFalse(e.SequenceActiveFlag,
                    $"{when}: #{e.Handle} {e.Class?.Name} left sequenceactive=1 (bit 0x200000)");
            }
        }

        // ---- every real menu scene, natural end ----
        //
        // Expected summon counts:
        // 001: Zwerg×2 + Muetze_a/b = 4 002: Troll×3 + Hamster + Hand_Gottes = 5
        // 004: Zwerg + gendered wooden hat = 2 005: Zwerg×2 + Taucherglocke + hats×2 + saber×2 = 7
        // 006: Zwerg×2 + Spitzhacke + hats×4 = 7 007: 2 base / 4 if the line-2 st007_end_5 branch (15%)
        // 008: 5 + st008_end_2 (chance 1.0!) 3 = 8 009: 4 (st009_end_2 summons nothing)
        [TestCase("start_001", 4)]
        [TestCase("start_002", 5)]
        [TestCase("start_004", 2)]
        [TestCase("start_005", 7)]
        [TestCase("start_006", 7)]
        [TestCase("start_007", -1)]
        [TestCase("start_008", 8)]
        [TestCase("start_009", 4)]
        public void MenuScene_NaturalEnd_SummonsCensusAndCleanup(string scene, int expectedSummons)
        {
            var (interp, ctx, sim, trigger, baseline) = BuildMenuWorld(seed: 987001);
            var rec = new BeatRecorder();
            rec.Attach(interp);

            interp.Eval($"call_method {trigger} set_sequencescript {scene}");
            interp.Eval($"call_method {trigger} run");

            int steps = 0;
            for (; steps < 12000 && !Finished(ctx, trigger); steps++) sim.Step(0.05);
            Assert.IsTrue(Finished(ctx, trigger),
                $"{scene} did not finish within {steps} steps ({sim.GameTime:F1} game-sec) | {Diag(ctx, trigger, sim)}");
            TestContext.WriteLine($"{scene}: finished in {steps} steps ({sim.GameTime:F1} game-sec), " +
                                  $"{rec.NewHandles.Count} summons, factors written: [{string.Join(",", rec.FactorWrites)}]");

            // Spawn census — every summon must have completed its new→set_owner→set_pos triple
            // (a CmdNew abort shifts every later [Object N] index — the missing-items bug).
            Assert.IsEmpty(rec.NewErrors, $"{scene}: `new` threw mid-summon: {string.Join("; ", rec.NewErrors)}");
            if (expectedSummons >= 0)
                Assert.AreEqual(expectedSummons, rec.NewHandles.Count, $"{scene}: summon count");
            else
                Assert.That(rec.NewHandles.Count, Is.EqualTo(2).Or.EqualTo(4),
                    $"{scene}: 2 (base path) or 4 (st007_end_5 line-2 branch) summons");
            foreach (var h in rec.NewHandles)
                Assert.IsTrue(rec.FirstPos.ContainsKey(h),
                    $"{scene}: summoned entity #{h} never got its set_pos (summon aborted mid-proc → index shift)");

            // First summon lands on the script's first pen (marker-derived — the "wrong spawn
            // position" guard). start_007's branch path has its own pens — only check the base path.
            if (FirstSummonPen.TryGetValue(scene, out var pen) && (scene != "start_007" || rec.NewHandles.Count == 2))
            {
                var got = rec.FirstPos[rec.NewHandles[0]];
                Assert.That((got - pen).magnitude, Is.LessThan(0.5f),
                    $"{scene}: first summon at {got}, expected pen {pen} (marker math broken?)");
            }
            if (scene == "start_005")
                Assert.That(rec.Owners, Does.Contain(4), "start_005: `sq_object summon Zwerg P_End 4` must set owner 4");

            // Let trailing particle lifetimes expire, then assert the clean menu world.
            for (int i = 0; i < 120; i++) sim.Step(0.05);
            AssertCleanWorld(ctx, sim, baseline, $"{scene} natural end");
            Assert.AreEqual(0, ctx.Particles!.Temps.Count, $"{scene}: do_particle temps must expire");
            var trig = ctx.GetEntity(trigger.ToString());
            Assert.IsNotNull(trig, $"{scene}: the menu trigger must SURVIVE (destroy_overload — Trigger_Urwald.tcl:8-15)");
            Assert.AreEqual("1", trig!.VarsScope.Vars["sequence_counter"], $"{scene}: shut_sequencer increments sequence_counter");
        }

        static readonly Dictionary<string, Vector3> FirstSummonPen = new()
        {
            ["start_001"] = MarkerZwerg,                            // P_Start = Getobjpos Info_Pos_Zwerg
            ["start_002"] = MarkerZwerg + new Vector3(-11, 0, -2),  // P1
            ["start_004"] = MarkerZwergTmp + new Vector3(2, 0, 0),  // P_Start
            ["start_005"] = MarkerZwerg + new Vector3(-5, 0, 0),    // P_Start
            ["start_006"] = MarkerZwerg + new Vector3(-5, 0, 0),    // P_Beam
            ["start_007"] = MarkerZwergTmp,                         // P_Start (base path only)
            ["start_008"] = MarkerZwerg + new Vector3(-5, 0, 0),    // P_Start
            ["start_009"] = MarkerZwergTmp,                         // P_Start
        };

        // ---- (iii): gametime factor — start_001's Matrix-jump slow-mo ----

        [Test]
        public void Start001_SlowMo_FactorScalesGameTime_AndRestoresTo1()
        {
            var (interp, ctx, sim, trigger, _) = BuildMenuWorld(seed: 987002);
            interp.Eval($"call_method {trigger} set_sequencescript start_001");
            interp.Eval($"call_method {trigger} run");

            // Step until the script's `gametime factor 0.3` beat lands.
            int steps = 0;
            for (; steps < 12000 && sim.TimeFactor > 0.9 && !Finished(ctx, trigger); steps++) sim.Step(0.05);
            Assert.That(sim.TimeFactor, Is.EqualTo(0.3).Within(1e-6),
                $"start_001 must reach `gametime factor 0.3` (after {steps} steps, {sim.GameTime:F1}s | {Diag(ctx, trigger, sim)})");

            // 1.0 s of real stepping now advances only 0.3 s of game time (FUN_005653f0 accum += factor*dt).
            double t0 = sim.GameTime;
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            Assert.That(sim.GameTime - t0, Is.EqualTo(0.3).Within(0.02),
                "factor 0.3 must scale the sim's whole game-time flow");

            for (steps = 0; steps < 12000 && !Finished(ctx, trigger); steps++) sim.Step(0.05);
            Assert.IsTrue(Finished(ctx, trigger), $"start_001 stuck after slow-mo | {Diag(ctx, trigger, sim)}");
            Assert.That(sim.TimeFactor, Is.EqualTo(1.0).Within(1e-9), "factor restored to 1 before EOF (script line 37)");
        }

        [Test]
        public void Gametime_TclSurface_FactorStartStopReset()
        {
            var (interp, _, sim, _) = BuildHost();
            Assert.AreEqual("1", interp.Eval("gametime factor"), "default factor");
            interp.Eval("gametime factor 0.25");
            Assert.That(sim.TimeFactor, Is.EqualTo(0.25).Within(1e-9));
            Assert.AreEqual("0.25", interp.Eval("gametime factor"));

            sim.Step(1.0);
            Assert.That(sim.GameTime, Is.EqualTo(0.25).Within(1e-9), "GameTime advances factor×dt");

            interp.Eval("gametime stop");           // +0xa098 = 0 → the clock freezes entirely
            sim.Step(1.0);
            Assert.That(sim.GameTime, Is.EqualTo(0.25).Within(1e-9), "stopped clock must not advance");
            interp.Eval("gametime start");
            sim.Step(1.0);
            Assert.That(sim.GameTime, Is.EqualTo(0.5).Within(1e-9));

            interp.Eval("gametime factor 0");       // scripts pause with factor 0
            sim.Step(5.0);
            Assert.That(sim.GameTime, Is.EqualTo(0.5).Within(1e-9), "factor 0 pauses game time");

            interp.Eval("gametime reset");
            Assert.That(sim.GameTime, Is.EqualTo(0.0).Within(1e-9), "reset zeroes the accumulator (+0xa088)");
        }

        // ---- skip-at-beat ≡ drained end state ----
        //
        // Break at several beat windows; the drain (sequencer.tcl:228-239) must execute every
        // remaining non-'-' line (incl. the trailing +sq_object delete all / +gametime factor 1)
        // and shut_sequencer must restore camera/actors/voice. End state ≡ the natural end's.
        [TestCase("start_002", 1)]
        [TestCase("start_002", 50)]
        [TestCase("start_002", 200)]
        [TestCase("start_002", 400)]
        [TestCase("start_001", 80)]
        [TestCase("start_009", 100)]
        public void MenuScene_SkipAtBeat_DrainsToCleanState(string scene, int breakAfterSteps)
        {
            var (interp, ctx, sim, trigger, baseline) = BuildMenuWorld(seed: 987003 + breakAfterSteps);
            var cam = ctx.Camera!;
            var focus0 = cam.Focus; float zoom0 = cam.Zoom;

            interp.Eval($"call_method {trigger} set_sequencescript {scene}");
            interp.Eval($"call_method {trigger} run");

            for (int i = 0; i < breakAfterSteps && !Finished(ctx, trigger); i++) sim.Step(0.05);

            ctx.SequenceBreaked = true;   // ESC (FUN_0052c510:353-355)
            int steps = 0;
            for (; steps < 200 && ctx.SequenceActive; steps++) sim.Step(0.05);
            Assert.IsFalse(ctx.SequenceActive,
                $"{scene}@{breakAfterSteps}: drain didn't reach shut_sequencer within {steps} steps | {Diag(ctx, trigger, sim)}");

            // Settle (camera interpolation + particle lifetimes), then the world must look untouched.
            for (int i = 0; i < 160; i++) sim.Step(0.05);
            AssertCleanWorld(ctx, sim, baseline, $"{scene} skip@{breakAfterSteps}");
            Assert.AreEqual(0, ctx.Particles!.Temps.Count, $"{scene}@{breakAfterSteps}: particle temps must expire after the skip");

            var trig = ctx.GetEntity(trigger.ToString());
            Assert.IsNotNull(trig, $"{scene}@{breakAfterSteps}: menu trigger must survive the skip (destroy_overload)");
            Assert.AreEqual("1", trig!.VarsScope.Vars["sequence_counter"],
                $"{scene}@{breakAfterSteps}: the drain must run shut_sequencer (counter incremented)");

            // shut_sequencer:550-552 — camera restored to the view captured at sequencer_activate.
            Assert.That((cam.Focus - focus0).magnitude, Is.LessThan(1.0f),
                $"{scene}@{breakAfterSteps}: camera focus must be restored to the activation view ({focus0} → {cam.Focus})");
            Assert.That(cam.Zoom, Is.EqualTo(zoom0).Within(0.25f),
                $"{scene}@{breakAfterSteps}: camera zoom must be restored");
        }

        // Skipping during the slow-mo window must still restore factor 1 — the drain executes the
        // un-prefixed `gametime factor 1` (line 37) and/or the `+gametime factor 1` drain line (43).
        [Test]
        public void Start001_SkipDuringSlowMo_RestoresFactor1()
        {
            var (interp, ctx, sim, trigger, baseline) = BuildMenuWorld(seed: 987004);
            interp.Eval($"call_method {trigger} set_sequencescript start_001");
            interp.Eval($"call_method {trigger} run");

            int steps = 0;
            for (; steps < 12000 && sim.TimeFactor > 0.9 && !Finished(ctx, trigger); steps++) sim.Step(0.05);
            Assert.That(sim.TimeFactor, Is.EqualTo(0.3).Within(1e-6), "must be inside the slow-mo window");

            ctx.SequenceBreaked = true;
            for (steps = 0; steps < 400 && ctx.SequenceActive; steps++) sim.Step(0.05);
            Assert.IsFalse(ctx.SequenceActive, $"drain stuck | {Diag(ctx, trigger, sim)}");
            Assert.That(sim.TimeFactor, Is.EqualTo(1.0).Within(1e-9),
                "a skip during slow-mo must restore factor 1 (else the whole game stays at 0.3×)");
            for (int i = 0; i < 120; i++) sim.Step(0.05);
            AssertCleanWorld(ctx, sim, baseline, "start_001 slow-mo skip");
        }

        // ---- the faithful menu loop: one trigger, accumulating counter, empty actor list ----

        [Test]
        public void MenuTrigger_PersistsAcrossPlays_CounterAccumulates()
        {
            var (interp, ctx, sim, trigger, baseline) = BuildMenuWorld(seed: 987005);

            foreach (var (scene, expectedCounter) in new[] { ("start_001", "1"), ("start_004", "2") })
            {
                interp.Eval($"call_method {trigger} set_sequencescript {scene}");
                interp.Eval("gametime factor 1");   // Trigger_StartScreen run does this defensively
                interp.Eval($"call_method {trigger} run");
                int steps = 0;
                var trigEnt = ctx.GetEntity(trigger.ToString())!;
                bool Done() => trigEnt.VarsScope.Vars.TryGetValue("sequence_counter", out var sc) && sc == expectedCounter;
                for (; steps < 12000 && !Done(); steps++) sim.Step(0.05);
                Assert.IsTrue(Done(), $"{scene} (play → counter {expectedCounter}) didn't finish | {Diag(ctx, trigger, sim)}");
                for (int i = 0; i < 120; i++) sim.Step(0.05);
                AssertCleanWorld(ctx, sim, baseline, $"{scene} (menu loop)");
            }

            // sequence_counter survived the replays — start_006's `do_script change st006_end_2 0.4 10`
            // (the ≥10-views alternate ending) depends on exactly this persistence.
            Assert.AreEqual("2", ctx.GetEntity(trigger.ToString())!.VarsScope.Vars["sequence_counter"]);
        }

        // The preset-shift bug: presetting a stray actor makes [Actor 0] the stray instead of the
        // script's own first summon — every do_action/rotate then drives the wrong dwarf. The
        // faithful menu contract is an empty actor list (sequencer_activate:366
        // `if {!$actors_preset} {reset}`), which the runtime's PlayMenuCutscene now follows (no
        // preset for menu backdrops).
        [Test]
        public void PresetActor_ShiftsActorIndices_WhyTheMenuMustStartEmpty()
        {
            var (interp, ctx, sim, trigger, _) = BuildMenuWorld(seed: 987006);
            string stray = interp.Eval($"new Zwerg \"\" \"{V(MarkerZwerg + new Vector3(1, 0, 0))}\" \"0 0 0\"").Trim();
            interp.Eval($"call_method {trigger} preset_actors [list {stray}]");
            interp.Eval($"call_method {trigger} set_sequencescript start_001");
            interp.Eval($"call_method {trigger} run");
            for (int i = 0; i < 400; i++) sim.Step(0.05);   // past the first `sq_actor find Zwerg`

            var actors = TclList.Parse(ctx.GetEntity(trigger.ToString())!.VarsScope.Vars["actors"]);
            Assert.That(actors.Count, Is.GreaterThanOrEqualTo(2), "stray + at least the first summoned Zwerg");
            Assert.AreEqual(stray, actors[0],
                "with a preset, [Actor 0] is the STRAY — the index shift that mis-targets every beat. " +
                "This is why the menu path must never preset actors.");
        }

        // ---- action <obj> move (CAct_Move) — start_002's Hand of God glide ----

        [Test]
        public void ActionMove_LinearGlide_FaithfulDurationAndTarget()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(@"def_class Prop none dummy 0 {} { obj_init {} }");
            interp.Eval(@"set o [new Prop """" ""0 0 0"" ""0 0 0""]");
            // sq_object move builds exactly: action <obj> move "{pos} speed" (sequencer.tcl:943).
            // dist 4, speed 0.3/tick → steps = round(4/0.3 + 0.5) = 14 ticks × 0.1 s = 1.4 game-sec.
            interp.Eval("action $o move \"{4 0 0} 0.3\" {set movedone 1}");
            int o = int.Parse(interp.Eval("set o"));
            var ent = ctx.GetEntity(o.ToString())!;
            Assert.IsNotNull(ent.ActiveAction, "move action installed (was the WaitAction(0.05) fallback)");

            for (int i = 0; i < 14; i++) sim.Step(0.05);   // 0.7 of 1.4 s — half-way
            Assert.That(ent.Position.x, Is.EqualTo(2f).Within(0.35f), "linear glide: ~half-way at half-time");
            Assert.IsNotNull(ent.ActiveAction, "still gliding at half-time");

            for (int i = 0; i < 30; i++) sim.Step(0.05);   // well past 1.4 s
            Assert.IsNull(ent.ActiveAction, "glide must END (CAct_Move step counter exhausted)");
            Assert.That((ent.Position - new Vector3(4, 0, 0)).magnitude, Is.LessThan(1e-3f), "lands ON the target");
            // finishcode runs in the owning entity's object scope (DigglesGameSim phase 2b).
            Assert.IsTrue(ent.VarsScope.Vars.TryGetValue("movedone", out var md) && md == "1",
                "finishcode fired on arrival");
        }

        [Test]
        public void ActionMove_ViaSqObjectMove_SequencerPath()
        {
            var (interp, ctx, sim, trigger, _) = BuildMenuWorld(seed: 987007);
            interp.Eval($"call_method {trigger} test_set_pen TriggerPos {{0 0 0}}");
            interp.Eval($"call_method {trigger} test_set_pen Odin1 {{3 0 0}}");
            Assert.AreEqual("1", interp.Eval($"call_method {trigger} test_summon Hand_Gottes TriggerPos").Trim(),
                "summon must append to object_list");
            string hand = interp.Eval($"call_method {trigger} test_object 0").Trim();
            // The real script line: `sq_object move 2 Odin1 0.3` → here index 0. Routed through a
            // method so object_list/Odin1 resolve in the trigger's object scope (per-object globals).
            interp.Eval($"call_method {trigger} test_move 0 Odin1 0.3");
            var ent = ctx.GetEntity(hand)!;
            for (int i = 0; i < 60; i++) sim.Step(0.05);
            Assert.That((ent.Position - new Vector3(3, 0, 0)).magnitude, Is.LessThan(0.01f),
                "the Hand of God must GLIDE to Odin1 (action <obj> move), not teleport-wait");
        }

        // ---- start_001 collision timing — the cutscene-run speed calibration pin ----
        //
        // The script's choreography (start_001.tcl:6-7,29-37): P_Start = [Getobjpos
        // Info_Pos_Zwerg], P_End = [Getobjpos Info_Pos_ZwergTmp] (the moved variants are
        // commented out); `do_action run P_End 0` + `do_action run P_Start 1` send the two
        // dwarves at each other; `do_wait time 5.1`; then `do_action wait 0 {0,1}` freezes
        // them, `set_anim rebound 11` plays the impact pose and `gametime factor 0.3`
        // slow-mos the Matrix jump.
        //
        // Speed law (see DigglesAnimController.NativeArcToWorld): cruise = the slot-cached
        // |VecA| × 0.05 ⇒ this rig's run bank (laufen_schnell) = 1.714 u/s — the live-dump
        // bank-1 slot value (1.714336). An earlier ×0.125 reading (4.286 u/s, "crossing
        // completes inside the wait") overdrove the whole world 2.5×.
        // Open question (for a speed-RE rerun): the original's start_001 runners visibly
        // collide mid-meadow, which needs ≈2.1 u/s closing-per-runner over the 5.1 s beat —
        // the native slot table holds four banks (+0x798 index 0..3, FUN_00423270) and only
        // banks 0/1 were dumped; the cutscene `run` may select a higher bank. Until that is
        // resolved the pin asserts the law distance for the bank our tables select (the
        // minimum-separation figure stays informational).
        // The exact action `do_action run` builds (sequencer.tcl:1730-1732 run→walk+animset 1,
        //:1785 `-target … -animsets 1 -dontstop 0`, no -speedtype ⇒ 0 ⇒
        // MetaWalkAction(walkAnimset=1, runAnimset=-1)), the script's own pens, the script's
        // own 5.1 s window, real mann |vecA| clip data. Bodies are pre-faced along travel so
        // the pin isolates cruise (the π/4 turn phase is covered separately).

        // Chest-to-chest contact ≈ one dwarf body width, native units. The measured failure
        // case sits at 1.5 u — visibly apart — so the pin must sit below that.
        const float CollisionContact = 1.0f;

        static (GameObject body, DigglesAnimController ctrl) SpeedRig(List<GameObject> made)
        {
            var body = new GameObject("runner");
            made.Add(body);
            var model = new GameObject("Model");
            model.transform.SetParent(body.transform, false);
            var anim = model.AddComponent<DigglesAnimator>();
            var ctrl = model.AddComponent<DigglesAnimController>();
            ctrl.Animator = anim;
            DigglesAnimClip Clip(string name, int frames, Vector3 vecA = default)
            {
                var c = new DigglesAnimClip { Name = name, Frames = new Mesh[frames], MotionOffset = vecA };
                for (int i = 0; i < frames; i++) c.Frames[i] = new Mesh();
                return c;
            }
            // Real mann.3db |vecA| root motions.
            anim.AddClip(Clip("standard", 2));
            anim.AddClip(Clip("gehen_start", 6, new Vector3(0f, -16.2218f, 0f)));
            anim.AddClip(Clip("gehen_loop", 10, new Vector3(0f, -16.2218f, 0f)));
            anim.AddClip(Clip("gehen_end", 6));
            anim.AddClip(Clip("laufen_schnell_start", 3, new Vector3(0f, -34.2867f, 0f)));
            anim.AddClip(Clip("laufen_schnell_loop", 10, new Vector3(0f, -34.2867f, 0f)));
            anim.AddClip(Clip("laufen_schnell_end", 6));
            var baseMap = new Dictionary<string, string> { ["standard"] = "standard" };
            var sets = new Dictionary<int, Dictionary<string, string>>
            {
                [0] = new()
                {
                    ["walk_start"] = "gehen_start",
                    ["walk_loop"] = "gehen_loop",
                    ["walk_stop"] = "gehen_end",
                },
                [1] = new()
                {
                    ["walk_start"] = "laufen_schnell_start",
                    ["walk_loop"] = "laufen_schnell_loop",
                    ["walk_stop"] = "laufen_schnell_end",
                },
            };
            ctrl.SetTables(baseMap, sets);
            return (body, ctrl);
        }

        [Test]
        public void Start001_RunCollision_SeparationAtTheWait0Freeze_PinsCutsceneRunSpeed()
        {
            var made = new List<GameObject>();
            try
            {
                // The script's own pens, in world space. NativeToLocal is 1:1 on the ground
                // plane (x→X, y→−Z; only the native-z height gets ×0.5), so pen distances and
                // the separation below are native-unit-faithful.
                Vector3 a0 = RuntimeWorldSpawner.NativeToLocal(MarkerZwerg);     // P_Start
                Vector3 a1 = RuntimeWorldSpawner.NativeToLocal(MarkerZwergTmp);  // P_End
                float penDist = Vector3.Distance(a0, a1);                        // ≈ 21.41

                var (bodyA, ctrlA) = SpeedRig(made);
                var (bodyB, ctrlB) = SpeedRig(made);
                bodyA.transform.position = a0;
                bodyB.transform.position = a1;
                bodyA.transform.rotation = Quaternion.LookRotation((a1 - a0).normalized, Vector3.up);
                bodyB.transform.rotation = Quaternion.LookRotation((a0 - a1).normalized, Vector3.up);

                // do_action run ⇒ action walk "-target <pen> -animsets 1 -dontstop 0":
                // walkAnimset=1 (the run bank as the gait), runAnimset absent ⇒ -1, speedtype 0.
                var runA = new MetaWalkAction(ctrlA, bodyA.transform, new List<Vector3> { a1 }, 1, -1, 0);
                var runB = new MetaWalkAction(ctrlB, bodyB.transform, new List<Vector3> { a0 }, 1, -1, 0);

                // The script's freeze window: do_wait time 5.1 → do_action wait 0 (replaces the
                // run, freezing each body wherever it stands). 51 × 0.1 s logic ticks. Track
                // the minimum separation — the meet moment (the original stops them there via
                // body collision; our executor lets them pass through, see the header note).
                float minSep = Vector3.Distance(bodyA.transform.position, bodyB.transform.position);
                for (int t = 0; t < 51; t++)
                {
                    runA.Tick(0.1); runB.Tick(0.1);
                    float s = Vector3.Distance(bodyA.transform.position, bodyB.transform.position);
                    if (s < minSep) minSep = s;
                }

                float travelled = Vector3.Distance(a0, bodyA.transform.position);
                float law = 34.2867f * DigglesAnimController.NativeArcToWorld * 5.1f;  // ≈ 8.74 u
                TestContext.WriteLine(
                    $"penDist={penDist:F2} minSep={minSep:F2} travelled/actor={travelled:F2} " +
                    $"implied speed={travelled / 5.1f:F2} u/s (law {law / 5.1f:F3} u/s; minSep informational " +
                    "until the cutscene-run bank question is resolved — see the header)");

                Assert.That(travelled, Is.EqualTo(law).Within(0.2f),
                    $"each start_001 runner must cover the law distance during the 5.1 s beat " +
                    $"(run-bank slot cruise 1.714 u/s × 5.1 ≈ 8.74 u) — travelled {travelled:F2}; " +
                    "more means the ×0.125 overdrive is back, less means the run bank stalled.");
            }
            finally
            {
                foreach (var go in made) if (go != null) Object.DestroyImmediate(go);
            }
        }

        // ---- set_vel — start_004's moonwalk slide / st009_end_2's sinking corpse ----

        [Test]
        public void SetVel_IntegratesPosition_AndScalesWithTimeFactor()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(@"def_class Prop none dummy 0 {} { obj_init {} }");
            interp.Eval(@"set o [new Prop """" ""0 0 0"" ""0 0 0""]");
            interp.Eval("set_vel $o {0.5 0 0}");
            int o = int.Parse(interp.Eval("set o"));
            var ent = ctx.GetEntity(o.ToString())!;

            for (int i = 0; i < 40; i++) sim.Step(0.05);   // 2 game-sec
            Assert.That(ent.Position.x, Is.EqualTo(1.0f).Within(0.01f),
                "velocity must INTEGRATE into the position (was written and never applied)");

            interp.Eval("gametime factor 0.5");            // slow-mo halves the slide speed
            for (int i = 0; i < 40; i++) sim.Step(0.05);   // 2 real → 1 game-sec
            Assert.That(ent.Position.x, Is.EqualTo(1.5f).Within(0.01f), "velocity advances on GAME time");
            interp.Eval("gametime factor 1");

            interp.Eval("set_vel $o {0 0 0}");
            var px = ent.Position.x;
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            Assert.That(ent.Position.x, Is.EqualTo(px).Within(1e-5f), "zero velocity stops the slide");
        }

        // ---- CmdNew robustness: a spawner throw must not corrupt object_list ----

        sealed class ThrowingSpawner : IDigglesEntitySpawner
        {
            public GameObject? Spawn(DigglesTclEntity ent, Transform parent) => throw new System.Exception("spawner boom");
            public void Destroy(GameObject go) { }
            public void ApplyPosition(GameObject go, Vector3 nativePos) { }
            public void ApplyRotation(GameObject go, Vector3 nativeRotRadians) { }
            public void ApplyAnim(GameObject go, string animName, int startFrame, Diggles.Runtime.DigglesAnimator.AnimMode mode) { }
        }

        [Test]
        public void SummonWithThrowingSpawner_ObjectListStaysAligned()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;   // CmdNew logs the isolated spawner error
            GameObject? root = null;
            try
            {
                var (interp, ctx, sim, _) = BuildHost();
                root = new GameObject("Wave8SpawnRoot");
                ctx.SpawnParent = root.transform;
                ctx.EntitySpawner = new ThrowingSpawner();

                interp.Eval(MenuSeqClass);
                int trigger = int.Parse(interp.Eval(@"set seq [new MenuSeq """" ""0 0 0"" ""0 0 0""]").Trim());
                interp.Eval($"call_method {trigger} test_set_pen TriggerPos {{1 2 3}}");

                // The summon's bookkeeping (sequencer.tcl:884-890) must complete despite the throw:
                Assert.AreEqual("1", interp.Eval($"call_method {trigger} test_summon Spitzhacke TriggerPos").Trim(),
                    "object_list must gain the entry — a spawner throw used to abort the proc before lappend");
                Assert.AreEqual("2", interp.Eval($"call_method {trigger} test_summon Dummy_Muetze_a TriggerPos").Trim());

                string h0 = interp.Eval($"call_method {trigger} test_object 0").Trim();
                string h1 = interp.Eval($"call_method {trigger} test_object 1").Trim();
                Assert.AreEqual("Spitzhacke", ctx.GetEntity(h0)!.Class!.Name, "[Object 0] index intact");
                Assert.AreEqual("Dummy_Muetze_a", ctx.GetEntity(h1)!.Class!.Name, "[Object 1] index intact (no shift)");
                Assert.IsNull(ctx.GetEntity(h0)!.GameObject, "entity lives on un-rendered");
                Assert.AreEqual("1 2 3", interp.Eval($"get_pos {h0}").Trim(), "set_pos after the failed spawn still applied");
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        // ---- the global cutscene freeze (set_sequence × per-object sequenceactive bit) ----
        //
        // RE ground truth: CGameThread::SrvDoThink drains an object's events and thinks its state
        // only when `!seqMode || (obj+0x3d0 & 0x200000)` (FUN_005d38c0:138 +:185-187); seqMode =
        // DAT_00772400+0x8c written by set_sequence (FUN_00649080), the bit by set_sequenceactive
        // (FUN_006816a0:74-88). This — not park_obj_actions (which ran before the summon existed,
        // sequencer_activate:371) and not add_actor's state_disable (props/extras are never
        // actors) — is what keeps a mid-sequence `sq_object summon` inert: e.g. start_002's third
        // Troll (line 99) is summoned and never sq_actor-found, yet its evt_timer0 init
        // (troll.tcl:98 → state idle → walk_random,:13,221-266) must not wander it off
        // mid-shot. Ambient classes opt out of the freeze at init (Fisch.tcl:15, hamster.tcl:95,
        // Schmetterling.tcl:15, raupe.tcl:38, fee.tcl:4) — fish keep swimming through cutscenes.

        // A minimal wanderer mirroring the Troll's init shape: a delayed init timer arms the
        // state machine (troll.tcl:98,13), and every state think bumps a counter + moves.
        const string WanderlingClass = @"
            def_class Wanderling none monster 0 {} {
                obj_init {
                    set think_count 0
                    timer_event this evt_w0 -repeat 0 -userid 0 -attime [expr [gettime]+0.5]
                }
                handle_event evt_w0 {
                    state_reset this
                    state_trigger this wander
                    state_enable this
                }
                state wander {
                    incr think_count
                    set_pos this [vector_add [get_pos this] {0.4 0 0}]
                }
            }";

        static DigglesTclEntity? FindByClass(DigglesTclContext ctx, string cls)
        {
            foreach (var e in ctx.Entities.Values)
                if (e.Class?.Name == cls) return e;
            return null;
        }

        static int ThinkCount(DigglesTclEntity e)
            => e.VarsScope.Vars.TryGetValue("think_count", out var v)
               && int.TryParse(v, out var n) ? n : 0;

        [Test]
        public void SummonedWanderer_FrozenDuringSequence_CleanAfterShut_ThinksAgainAfter()
        {
            var (interp, ctx, sim, trigger, baseline) = BuildMenuWorld(seed: 987010);
            interp.Eval(WanderlingClass);

            // A throwaway sequence: summon one Wanderling as a non-actor extra (the start_002
            // line-99 pattern), hold 4 s, then the standard `+sq_object delete all` drain line.
            string seqPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"dgl_wanderer_{System.DateTime.Now.Ticks}.tcl").Replace('\\', '/');
            System.IO.File.WriteAllText(seqPath,
                "+sq_object delete all\n" +
                "sq_pen set P1 {30 40 8}\n" +
                "sq_object summon Wanderling P1\n" +
                "do_wait time 4\n" +
                "+sq_object delete all\n");
            try
            {
                interp.Eval($"call_method {trigger} set_sequencescript {{{seqPath}}}");
                interp.Eval($"call_method {trigger} run");

                DigglesTclEntity? found = null;
                for (int i = 0; i < 80 && found == null; i++) { sim.Step(0.05); found = FindByClass(ctx, "Wanderling"); }
                Assert.IsNotNull(found, "the summon beat must have produced the Wanderling");
                var w = found!;
                Assert.IsTrue(ctx.SequenceActive, "sequence mode must be on while the script runs");
                Assert.IsFalse(w.SequenceActiveFlag,
                    "a fresh summon must NOT carry the sequenceactive bit (CObj ctor word 0x01100101 — bit 21 clear)");
                var pos0 = w.Position;

                // 3 s of game time — far past the +0.5 s init timer. The state body must never
                // run (events gated, FUN_005d38c0:185-187) and the summon must not move.
                for (int i = 0; i < 60; i++) sim.Step(0.05);
                Assert.IsTrue(ctx.Entities.ContainsKey(w.Handle), "summon lives until the drain delete");
                Assert.AreEqual(0, ThinkCount(w),
                    "the summon's state body THOUGHT mid-sequence — its class AI is fighting the script");
                Assert.That((w.Position - pos0).magnitude, Is.LessThan(0.01f),
                    "the summon moved between script beats — wander AI not frozen");

                int steps = 0;
                for (; steps < 4000 && !Finished(ctx, trigger); steps++) sim.Step(0.05);
                Assert.IsTrue(Finished(ctx, trigger), $"scene did not finish | {Diag(ctx, trigger, sim)}");
                for (int i = 0; i < 40; i++) sim.Step(0.05);
                AssertCleanWorld(ctx, sim, baseline, "wanderer summon scene");

                // Control — the freeze must not outlive the sequence: a post-shut Wanderling's
                // init timer delivers and its state thinks + moves (proves the instrument works).
                var w2 = ctx.GetEntity(interp.Eval("new Wanderling \"\" \"50 40 8\" \"0 0 0\"").Trim())!;
                var p2 = w2.Position;
                for (int i = 0; i < 60; i++) sim.Step(0.05);
                Assert.Greater(ThinkCount(w2), 0, "outside a sequence the wanderer must think");
                Assert.That((w2.Position - p2).magnitude, Is.GreaterThan(0.3f),
                    "outside a sequence the wanderer must move (control for the freeze assert)");
            }
            finally { try { System.IO.File.Delete(seqPath); } catch { } }
        }

        // The bit-level contract without the sequencer: during set_sequence 1 a flagged object
        // keeps thinking (the Fisch.tcl:15 ambient-life contract) while an unflagged one has its
        // events deferred — not dropped: they deliver after set_sequence 0 (the native ring
        // buffer just waits behind the FUN_005d38c0:185-187 gate).
        [Test]
        public void SequenceFreeze_GatesByTheBit_AndDefersEventsNotDrops()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(WanderlingClass);
            // A directly-armed walker — its state is live before the sequence starts, isolating
            // the state-think half of the gate (HandleStates side of FUN_005d38c0:185-187) from
            // the event-drain half the timer-armed Wanderling exercises.
            interp.Eval(@"
                def_class Strider none monster 0 {} {
                    obj_init {
                        set think_count 0
                        state_trigger this stride
                        state_enable this
                    }
                    state stride {
                        incr think_count
                        set_pos this [vector_add [get_pos this] {0.4 0 0}]
                    }
                }");
            // Pre-sequence control window for the strider only (the wanderlings spawn later so
            // their +0.5 s init timers cannot deliver before the sequence starts).
            var strider = ctx.GetEntity(interp.Eval("new Strider \"\" \"30 10 8\" \"0 0 0\"").Trim())!;
            for (int i = 0; i < 10; i++) sim.Step(0.05);   // 0.5 s — the armed state thinks
            int strides0 = ThinkCount(strider);
            Assert.Greater(strides0, 0, "control: the armed state thinks before the sequence");

            var frozen = ctx.GetEntity(interp.Eval("new Wanderling \"\" \"10 10 8\" \"0 0 0\"").Trim())!;
            var exempt = ctx.GetEntity(interp.Eval("new Wanderling \"\" \"20 10 8\" \"0 0 0\"").Trim())!;
            interp.Eval($"set_sequenceactive {exempt.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"get_sequenceactive {exempt.Handle}").Trim());
            Assert.AreEqual("0", interp.Eval($"get_sequenceactive {frozen.Handle}").Trim());

            interp.Eval("set_sequence 1");                 // before any step — timers mature mid-sequence
            for (int i = 0; i < 40; i++) sim.Step(0.05);   // 2 s — both init timers matured
            Assert.Greater(ThinkCount(exempt), 0,
                "a sequenceactive=1 object must keep thinking through a sequence (bit 0x200000 exemption)");
            Assert.AreEqual(0, ThinkCount(frozen),
                "an unflagged object must be fully frozen during a sequence (events + state thinks)");
            Assert.AreEqual(strides0, ThinkCount(strider),
                "an ALREADY-ARMED, enabled state must stop thinking during the sequence (the state half of the gate)");

            interp.Eval("set_sequence 0");
            for (int i = 0; i < 40; i++) sim.Step(0.05);
            Assert.Greater(ThinkCount(frozen), 0,
                "the queued init event must DELIVER after set_sequence 0 (deferred, not dropped)");
            Assert.Greater(ThinkCount(strider), strides0,
                "the armed state must RESUME thinking after set_sequence 0");
        }
    }
}
