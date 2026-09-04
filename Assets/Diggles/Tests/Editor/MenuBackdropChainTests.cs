#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // The faithful menu-backdrop loop (cutscene-actor-leak fix). Ground truth: natively the menu
    // replays via one synchronous Tcl chain — shut_sequencer (sequencer.tcl:535-555) → destroy
    // (:2153-2166, `state_trigger this sleep; state_disable this; call_method this
    // destroy_overload`) → Trigger_StartScreen.destroy_overload → run (Trigger_Urwald.tcl:8-15,
    // 39-58: clears followaction_list/eyefocus_list, `gametime factor 1`, sequencer_activate) →
    // init_sequencer → set_sequence 1. The set_sequence byte (DAT_00772400+0x8c) therefore never
    // stays down across a backdrop boundary, and the SrvDoThink freeze (FUN_005d38c0:138,185-187
    // — events + state thinks only for objects with the 0x200000 sequenceactive bit) covers
    // leftovers continuously.
    //
    // The port used to replay via a C# watchdog with a ≥1 s live gap (SequenceActive false, sim
    // stepping): any actor left over from a broken/skipped scene had its queued obj_init events
    // (troll.tcl:98 evt_timer0 → state idle → walk_random:256-262) mature and burst in that gap
    // — the "troll walks away mid-scene" leak. These tests pin the fixed contract, driving the
    // production class body (DigglesWorldRuntime.MenuBackdropSeqTcl).
    public sealed class MenuBackdropChainTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // A troll-shaped wanderer: a delayed init timer arms the state machine (troll.tcl:98,
        // 13); every state think bumps a counter and moves (the observable "walks away").
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

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim, int trigger) BuildMenuWorld()
        {
            UnityEngine.Random.InitState(20260701);
            var interp = new TclInterpreter();
            interp.TolerateExprErrors = true;
            interp.TolerateMissingVars = true;
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            var sim = new DigglesGameSim(ctx);
            interp.RegisterCommand("puts", (i, a) => "");
            interp.UnknownCommandHandler = (i, a) => "0";
            interp.Eval(WanderlingClass);
            // The production class — the same body PlayMenuCutscene evals.
            interp.Eval(Diggles.Runtime.DigglesWorldRuntime.MenuBackdropSeqTcl);
            int trigger = int.Parse(interp.Eval(@"new _MenuBackdrop_Seq """" ""0 0 0"" ""0 0 0""").Trim());
            return (interp, ctx, sim, trigger);
        }

        static string SceneFile(string body)
        {
            string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"dgl_chain_{System.Guid.NewGuid():N}.tcl").Replace('\\', '/');
            System.IO.File.WriteAllText(p, body);
            return p;
        }

        static string Counter(DigglesTclContext ctx, int trigger)
        {
            var e = ctx.GetEntity(trigger.ToString());
            return e != null && e.VarsScope.Vars.TryGetValue("sequence_counter", out var sc) ? sc : "<gone>";
        }

        static int ThinkCount(DigglesTclContext ctx, string cls)
        {
            foreach (var e in ctx.Entities.Values)
                if (e.Class?.Name == cls && e.VarsScope.Vars.TryGetValue("think_count", out var v)
                    && int.TryParse(v, out var n))
                    return n;
            return -1;   // no such entity
        }

        // The core pin: scene A leaks a summoned Wanderling (a broken scene never reaching its
        // trailing delete); destroy_overload chains scene B in the same shut_sequencer call —
        // SequenceActive must never be observed false at step granularity across the boundary,
        // and the leftover's queued init event must not deliver (no think, no movement) while
        // the menu loop lives.
        [Test]
        public void DestroyOverload_ChainsNextScene_SameTick_LeftoverStaysFrozen()
        {
            var (interp, ctx, sim, trigger) = BuildMenuWorld();
            string sceneA = SceneFile(
                "sq_pen set P1 {30 40 8}\n" +
                "sq_object summon Wanderling P1\n" +
                "do_wait time 2\n");                      // NO delete — the leftover
            string sceneB = SceneFile(
                "do_wait time 2\n" +
                "+sq_object delete all\n");
            var queue = new Queue<string>(new[] { sceneB });   // what destroy_overload picks next
            interp.RegisterCommand("_menu_next_backdrop", (i, a) => queue.Count > 0 ? queue.Dequeue() : "");
            try
            {
                interp.Eval($"call_method {trigger} set_sequencescript {{{sceneA}}}");
                interp.Eval($"call_method {trigger} run");
                Assert.IsTrue(ctx.SequenceActive, "scene A must enter sequence mode");

                var wanderer = default(DigglesTclEntity);
                foreach (var e in ctx.Entities.Values) if (e.Class?.Name == "Wanderling") wanderer = e;

                // Drive across A's natural end (counter 0→1, chain → B) to B's end (1→2).
                int steps = 0; bool sawInactiveBeforeDone = false;
                for (; steps < 800 && Counter(ctx, trigger) != "2"; steps++)
                {
                    sim.Step(0.025);
                    if (Counter(ctx, trigger) != "2" && !ctx.SequenceActive) sawInactiveBeforeDone = true;
                    if (wanderer == null)
                        foreach (var e in ctx.Entities.Values) if (e.Class?.Name == "Wanderling") wanderer = e;
                }
                var trigD = ctx.GetEntity(trigger.ToString());
                string DV(string k) => trigD != null && trigD.VarsScope.Vars.TryGetValue(k, out var v) ? v : "<none>";
                TestContext.WriteLine($"[diag] counter={Counter(ctx, trigger)} seqActive={ctx.SequenceActive} " +
                    $"state={trigD?.CurrentState} en={trigD?.StateEnabledCount} script={DV("sequencescript")} " +
                    $"next={DV("next")} file={DV("file")} activated={DV("activated")} wait_time_var={DV("wait_time_var")}");
                Assert.AreEqual("2", Counter(ctx, trigger), "scene B must have played to its end via the chain");
                Assert.IsFalse(sawInactiveBeforeDone,
                    "SequenceActive dropped between backdrops — the un-frozen gap the native chain never has " +
                    "(shut_sequencer → destroy_overload → run → set_sequence 1 is one synchronous Tcl call)");
                Assert.IsNotNull(wanderer, "scene A must have summoned the Wanderling");
                Assert.IsTrue(ctx.Entities.ContainsKey(wanderer!.Handle), "the leftover survives scene A (never deleted)");
                Assert.AreEqual(0, int.Parse(wanderer.VarsScope.Vars["think_count"]),
                    "the leftover's queued evt_w0 DELIVERED across the scene boundary — its wander AI ran " +
                    "(the 'troll walks away mid-scene' leak)");

                // After B the picker is empty → the loop stops (destroy parked the trigger). Only
                // now may the world run un-frozen — and the queued init event delivers (control
                // that the freeze was the sequence chain, not a dead sim).
                for (int i = 0; i < 60; i++) sim.Step(0.025);
                Assert.IsFalse(ctx.SequenceActive, "empty pick must end sequence mode");
                Assert.IsNotNull(ctx.GetEntity(trigger.ToString()), "the persistent trigger survives (destroy_overload)");
                Assert.Greater(ThinkCount(ctx, "Wanderling"), 0,
                    "control: once the menu loop stops, the deferred event must deliver (deferred, not dropped)");
            }
            finally
            {
                try { System.IO.File.Delete(sceneA); } catch { }
                try { System.IO.File.Delete(sceneB); } catch { }
            }
        }

        // Trigger_Urwald.tcl:43-44 — `run` must clear followaction_list and eyefocus_list, so a
        // previous scene's `sq_actor actionlist` (e.g. start_004's `{{anim discoc} loop}`) can
        // never replay on the next scene's actors ("the gnome dances instead of turning").
        [Test]
        public void Run_ClearsStaleFollowactionAndEyefocus()
        {
            var (interp, ctx, sim, trigger) = BuildMenuWorld();
            string scene = SceneFile("do_wait time 1\n+sq_object delete all\n");
            interp.RegisterCommand("_menu_next_backdrop", (i, a) => "");
            try
            {
                var trig = ctx.GetEntity(trigger.ToString())!;
                trig.VarsScope.Vars["followaction_list"] = "{{anim discoc} loop}";
                trig.VarsScope.Vars["followaction0"] = "{anim discoc} loop";
                trig.VarsScope.Vars["eyefocus_list"] = "1 1";

                interp.Eval($"call_method {trigger} set_sequencescript {{{scene}}}");
                interp.Eval($"call_method {trigger} run");

                Assert.AreEqual("", trig.VarsScope.Vars["followaction_list"],
                    "run must clear followaction_list (Trigger_Urwald.tcl:43)");
                Assert.AreEqual("", trig.VarsScope.Vars["eyefocus_list"],
                    "run must clear eyefocus_list (Trigger_Urwald.tcl:44)");
                Assert.That(sim.TimeFactor, Is.EqualTo(1.0).Within(1e-9), "run forces gametime factor 1 (:56)");

                for (int i = 0; i < 200 && Counter(ctx, trigger) != "1"; i++) sim.Step(0.025);
                Assert.AreEqual("1", Counter(ctx, trigger), "the trivial scene must finish");
            }
            finally { try { System.IO.File.Delete(scene); } catch { } }
        }

        // The stall/dancing root cause: `reset` (sequencer.tcl:355) was shadowed by a native "0"
        // stub (dispatch checks natives before procs), so sequencer_activate:366 never cleared
        // followaction_list on the persistent trigger. A scene that set
        // `sq_actor actionlist 0 {… loop}` and got ESC-broken left the loop list stale; the next
        // scene's first do_action finish then popped the stale list in seq_enable
        // (sequencer.tcl:47-73) instead of running sequence_enable — the actor chained foreign
        // anims forever ("dances instead of turning") and the scene wedged at its first sq_wait
        // ("both stand ~10 s doing nothing"). With the stub removed the proc runs and scene B
        // must play to its end with no stale-anim replay.
        [Test]
        public void Reset_ProcNotShadowed_StaleActionlistDoesNotReplayOnNextScene()
        {
            var (interp, ctx, sim, trigger) = BuildMenuWorld();
            string sceneA = SceneFile(
                "sq_pen set P1 {30 40 8}\n" +
                "sq_object summon Wanderling P1\n" +
                "sq_actor find Wanderling\n" +
                "sq_actor actionlist 0 {{anim stale_foo} loop}\n" +
                "do_wait time 5\n" +
                "+sq_object delete all\n");
            string sceneB = SceneFile(
                "sq_pen set P1 {30 40 8}\n" +
                "sq_object summon Wanderling P1\n" +
                "sq_actor find Wanderling\n" +
                "sq_wait 0\n" +
                "do_action rotate 1.0 0\n" +          // the start_002 L31 shape — first waited beat
                "do_wait time 0.5\n" +
                "+sq_object delete all\n");
            interp.RegisterCommand("_menu_next_backdrop", (i, a) => "");
            var staleReplays = new List<string>();
            interp.CommandTrace = (name, args, result) =>
            {
                if (name == "action" && args.Length > 3 && args[3].Contains("stale_foo"))
                    staleReplays.Add(string.Join(" ", args));
            };
            try
            {
                interp.Eval($"call_method {trigger} set_sequencescript {{{sceneA}}}");
                interp.Eval($"call_method {trigger} run");
                for (int i = 0; i < 60; i++) sim.Step(0.025);      // 1.5 s — actionlist line executed
                ctx.SequenceBreaked = true;                        // ESC → the drain leaves the list stale
                for (int i = 0; i < 200 && ctx.SequenceActive; i++) sim.Step(0.025);
                Assert.AreEqual("1", Counter(ctx, trigger), "scene A must shut via the drain");

                staleReplays.Clear();                              // scene A may legitimately play its own list
                interp.Eval($"call_method {trigger} set_sequencescript {{{sceneB}}}");
                interp.Eval($"call_method {trigger} run");
                int steps = 0;
                for (; steps < 800 && Counter(ctx, trigger) != "2"; steps++) sim.Step(0.025);
                Assert.AreEqual("2", Counter(ctx, trigger),
                    $"scene B stalled (steps={steps}) — the stale followaction replay wedge " +
                    "(reset proc shadowed → followaction_list never cleared between plays)");
                Assert.IsEmpty(staleReplays,
                    "scene A's actionlist replayed on scene B's actor: " + string.Join("; ", staleReplays));
            }
            finally
            {
                try { System.IO.File.Delete(sceneA); } catch { }
                try { System.IO.File.Delete(sceneB); } catch { }
            }
        }

        // An empty pick ("" — New Game pressed / not at the menu) must park the trigger exactly
        // like the pre-chain flow: sequence over, trigger alive + state-disabled in `sleep`
        // (sequencer.tcl:2163-2166), world un-frozen for the teardown path.
        [Test]
        public void DestroyOverload_EmptyPick_ParksTrigger()
        {
            var (interp, ctx, sim, trigger) = BuildMenuWorld();
            string scene = SceneFile("do_wait time 1\n+sq_object delete all\n");
            interp.RegisterCommand("_menu_next_backdrop", (i, a) => "");
            try
            {
                interp.Eval($"call_method {trigger} set_sequencescript {{{scene}}}");
                interp.Eval($"call_method {trigger} run");
                for (int i = 0; i < 200 && Counter(ctx, trigger) != "1"; i++) sim.Step(0.025);
                Assert.AreEqual("1", Counter(ctx, trigger), "the scene must finish");
                Assert.IsFalse(ctx.SequenceActive, "sequence mode must end when nothing is chained");
                var trig = ctx.GetEntity(trigger.ToString());
                Assert.IsNotNull(trig, "the persistent trigger survives");
                Assert.AreEqual("sleep", trig!.CurrentState, "destroy parks the trigger in `sleep` (sequencer.tcl:2164)");
                Assert.Less(trig.StateEnabledCount, 0, "destroy state_disables the parked trigger (:2165)");
            }
            finally { try { System.IO.File.Delete(scene); } catch { } }
        }
    }
}
