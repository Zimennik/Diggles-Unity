#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Headless integration tests for the dwarf work loop (harvest / dig): load the real Zwerg class
    // chain, spawn a gnome, raise the faithful task event, step the sim, and trace how far the
    // event->state->tasklist->action chain gets — diagnosing a work order where the dwarf does
    // nothing. (No GameObjects in EditMode, so walks degrade to brief WaitActions — but the
    // logic/tasklist flow still advances, which is what we verify here.)
    [Explicit]   // loads ~300 real classes from the data dir; run on demand
    public sealed class WorkLoopTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        // The real BDB — the faithful carve (block transitions, corner gates, rims) needs it; the old
        // null-bdb harness broke when the simple height-decrement dig was replaced by the aux
        // corner-graph carve. Parsed once per fixture.
        static BdbModel? s_bdb;
        static BdbModel Bdb() => s_bdb ??= BdbParser.Parse(DataRoot + "/BlockDB2.BDB");

        // A tiny all-solid terrain (block 0 = non-air ⇒ height 15) for the dig primitives.
        DiggableTerrain MakeSolidTerrain(int w, int h)
        {
            var go = new GameObject("DiggableTerrain");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var r = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h,
                BlockIdx = new uint[w * h],          // all 0 ⇒ non-air ⇒ solid/diggable
                Terrain = new byte[w * h],
                Voxels = new byte[w * h * PmpMap.SubGridLength],
            };
            t.Init(r, Bdb(), "", () => { });
            return t;
        }

        // An open terrain (all air ⇒ height 0) with the given tiles solid (a wall to dig). A real dig
        // needs the dwarf standing on open ground beside a solid wall tile that has open neighbours
        // (dig_check wants ≥2 free neighbours; dig_execute wants the dwarf within range), so an all-solid
        // grid can't be dug — this mirrors a wall edge.
        DiggableTerrain MakeOpenTerrain(int w, int h, params (int x, int y)[] solid)
        {
            var go = new GameObject("DiggableTerrain");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            var voxels = new byte[w * h * PmpMap.SubGridLength];
            for (int k = 0; k < block.Length; k++) block[k] = 59;   // 59 = air
            foreach (var (x, y) in solid) if ((uint)x < (uint)w && (uint)y < (uint)h) block[y * w + x] = 0;
            for (int tile = 0; tile < block.Length; tile++)
                if (block[tile] == 59)
                    voxels[tile * PmpMap.SubGridLength + 36] =
                        Diggles.Runtime.Navigation.NavWorld.SubWalk |
                        Diggles.Runtime.Navigation.NavWorld.SubFloor;
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w,
                Height = h,
                BlockIdx = block,
                Terrain = new byte[w * h],
                Voxels = voxels,
            }, Bdb(), "", () => { });
            return t;
        }

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
        {
            string h = interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"");
            return ctx.GetEntity(h)!;
        }

        // The preinvented-blueprint gate is critical: the native per-owner attrib table is
        // registry-shaped — an unset name reads its def_attrib default (FUN_0062c9b0 resolves
        // names through the registry, FUN_005da2f0 name→index). genericprod.tcl marks
        // preinvented items by re-defining `def_attrib Bp$itemtype 0 1 1` at classinit
        // (:147-156, tttpreinv_Feuerstelle = {Grillpilz Zelt Feuerstelle}, techtreetunes.tcl:32)
        // and nothing ever set_owner_attrib's them for the human player (only the AI seeds
        // explicitly, ai/std_ai.tcl:1270) — without the default fallback the Bp gate fails
        // closed for everything and no autonomous production job is ever emitted in-game.
        [Test]
        public void OwnerAttrib_UnsetName_ReadsDefAttribDefault()
        {
            var (interp, ctx, _) = RealHost();
            Assert.AreEqual("1", interp.Eval("get_owner_attrib 0 BpGrillpilz").Trim(),
                "preinvented BpGrillpilz must read its def_attrib default 1 with no seeding");
            // an explicit write overrides the default
            interp.Eval("set_owner_attrib 0 BpGrillpilz 0");
            Assert.AreEqual("0", interp.Eval("get_owner_attrib 0 BpGrillpilz").Trim());
            // add_owner_attrib starts read-modify-write from the registry default
            interp.Eval("def_attrib TestOwnerDefault 0 10 7");
            Assert.AreEqual("9", interp.Eval("add_owner_attrib 0 TestOwnerDefault 2").Trim());
            // names with no def_attrib at all still read 0
            Assert.AreEqual("0", interp.Eval("get_owner_attrib 0 BpNoSuchThing").Trim());
        }

        [Test, Explicit]
        public void Harvest_DriveLoop()
        {
            var (interp, ctx, sim) = RealHost();
            TestContext.WriteLine($"classes loaded: {ctx.Classes.Count}");
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            Assert.IsTrue(ctx.Classes.ContainsKey("Pilz"), "Pilz class not loaded");

            var g = Spawn(interp, ctx, "Zwerg", "100 100 8");
            var p = Spawn(interp, ctx, "Pilz", "103 100 8");
            TestContext.WriteLine($"spawned gnome#{g.Handle} pilz#{p.Handle}");

            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle into idle
            TestContext.WriteLine($"pre: state={g.CurrentState} tasklist={g.TaskList.Count} parked={g.Parked} stateEnabled={g.StateEnabledCount}");

            ctx.This = g;
            interp.Eval("set_event this evt_task_harvest -subject1 " + p.Handle);
            ctx.This = null;

            bool sawWork = false, sawAction = false;
            for (int i = 0; i < 40; i++)
            {
                sim.Step(0.1);
                if (g.ActiveAction != null) sawAction = true;
                g.VarsScope.Vars.TryGetValue("current_workplace", out var wp);
                g.VarsScope.Vars.TryGetValue("current_plan", out var pl);
                if (g.CurrentState == "work_dispatch" || g.CurrentState == "task" || !string.IsNullOrEmpty(wp)) sawWork = true;
                if (i % 3 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} plan={pl} wp={wp} tasklist={g.TaskList.Count} action={(g.ActiveAction != null)}");
            }
            TestContext.WriteLine($"RESULT harvest: sawWork={sawWork} sawAction={sawAction} finalState={g.CurrentState}");
            Assert.IsTrue(sawWork, "gnome never entered the work/harvest flow after evt_task_harvest");
        }

        // Diagnostic (symptom: work anims play at ~x100 / hats change instantly). The work_dispatch
        // pump pops one task per state-tick (z_work_states.tcl:13-22) and play_anim gates re-popping
        // via `state_disable this` + `action this anim X {state_enable this}` (z_procs.tcl:193-197):
        // the state must stop popping until the anim's finishcode re-enables it. Queue a chain of
        // play_anim tasks and measure the pop rate at 40 Hz: one pop per action-completion = correct;
        // one pop per tick = the re-pop race (= visual x100). Headless anims have no controller →
        // AnimAction lasts 0.1s, so correct pacing drains ~1 task / 0.1s (every ~4 ticks), a re-pop
        // drains ~1 / tick.
        [Test, Explicit]
        public void Diag_TaskPump_RePopRate()
        {
            var (interp, ctx, sim) = RealHost();
            var g = Spawn(interp, ctx, "Zwerg", "100 100 8");
            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle into idle
            ctx.This = g;
            interp.Eval("state_triggerfresh this work_dispatch");
            for (int k = 0; k < 8; k++) interp.Eval("tasklist_add this \"play_anim hackquloop\"");
            ctx.This = null;
            TestContext.WriteLine($"start: state={g.CurrentState} count={g.StateEnabledCount} tasks={g.TaskList.Count}");

            int lastTasks = g.TaskList.Count;
            double lastPopT = 0;
            var gaps = new List<double>();
            object? lastAction = g.ActiveAction;
            int actionSwaps = 0;
            for (int i = 0; i < 120 && g.TaskList.Count > 0; i++)
            {
                sim.Step(0.025);
                if (!ReferenceEquals(g.ActiveAction, lastAction)) { actionSwaps++; lastAction = g.ActiveAction; }
                if (g.TaskList.Count != lastTasks)
                {
                    double gap = sim.GameTime - lastPopT; lastPopT = sim.GameTime;
                    gaps.Add(gap);
                    TestContext.WriteLine($"t={sim.GameTime:0.000} POP → tasks={g.TaskList.Count} count={g.StateEnabledCount} gapSincePrev={gap:0.000} action={g.ActiveAction?.GetType().Name ?? "-"}");
                    lastTasks = g.TaskList.Count;
                }
            }
            double avgGap = gaps.Count > 1 ? (gaps[gaps.Count - 1] + gaps[1]) / 2 : -1;  // skip the first
            TestContext.WriteLine($"RESULT: {gaps.Count} pops, actionSwaps={actionSwaps}, avg gap≈{avgGap:0.000}s " +
                $"(0.025=one-pop-per-tick=RE-POP BUG; ~0.1=correct stub pacing)");
        }

        // Diagnostic (symptom: work anims play x100 / hats snap instantly). Build a real gnome with a
        // model+controller (DigglesEngine wires SpawnParent → RuntimeWorldSpawner builds the.3db), then
        // probe whether the work-loop clip names actually resolve and load with a non-zero frame count.
        // AnimAction times completion as CurrentFrameCount/10 — a clip that resolves to frameCount 0/1
        // makes every anim last ~0.1s, so the chain flashes ("x100"/"skipped"). Compares the work clips
        // to a cutscene clip (lookdownloop) known to play at normal speed.
        [Test, Explicit]
        public void Diag_WorkClips_FrameCounts()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            _spawnedGos.Add(rootGo);
            var engine = new Diggles.Runtime.DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            var interp = engine.Interp; var ctx = engine.Ctx;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); } catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");

            string h = interp.Eval("new Zwerg \"\" \"100 100 8\" \"0 0 0\"").Trim();
            var g = ctx.GetEntity(h);
            Assert.IsNotNull(g, "no gnome entity");
            var go = g!.GameObject;
            TestContext.WriteLine($"gnome#{h} hasGO={(go != null)}");
            var ctrl = go != null ? go.GetComponentInChildren<Diggles.Runtime.DigglesAnimController>() : null;
            if (ctrl == null) { TestContext.WriteLine("NO DigglesAnimController on the spawned gnome — model build failed in batch?"); return; }
            TestContext.WriteLine($"controller ObjName={ctrl.ObjName} activeAnimset={ctrl.ActiveAnimset} hasAnimator={(ctrl.Animator != null)}");

            string[] clips = { "standstill", "gehen_loop", "hackquloop", "hackqustart", "hackquend",
                               "hackfitloop", "sawhorloop", "hatonhead", "hatonhand", "hatongone",
                               "hatofhead", "lookdownloop", "stand_anim_a", "scout" };
            foreach (var c in clips)
            {
                string? resolved = ctrl.ResolveState(c);
                bool ok = ctrl.PlayClip(resolved ?? c, Diggles.Runtime.DigglesAnimator.AnimMode.Loop);
                int fc = ctrl.Animator != null ? ctrl.Animator.CurrentFrameCount : -1;
                TestContext.WriteLine($"  '{c}': resolved={(resolved ?? "<bare>")} play={ok} frameCount={fc} cur={ctrl.CurrentClip}");
            }
        }

        // A sealed marked tile has no legal standoff. FUN_005981d0 seeds owner bit 2, floods to a
        // value-3 floor, and returns the exact sentinel when none exists; it never returns rock.
        [Test, Explicit]
        public void GetDigEdge_SealedMarkedRockReturnsSentinel()
        {
            var (interp, ctx, _) = RealHost();
            var t = MakeSolidTerrain(32, 32);
            ctx.Terrain = t;

            // unmarked ⇒ the sentinel; the faithful sentinel is {-1,-1, 14.0} — the native
            // wrapper hard-codes Z=14.0 (dig-edge.md; the Tcl validity test is `lindex 0 > 0`).
            Assert.AreEqual("-1 -1 14", interp.Eval("get_digedge {10 10} 0").Trim(), "edge found with no dig marks");

            t.SetDigMark(10, 10, 0);   // owner 0, cnt 0 = full dig (the faithful tile+0x08 nibble)
            string edge = interp.Eval("get_digedge {10 10} 0").Trim();
            TestContext.WriteLine($"get_digedge after mark: '{edge}'");
            Assert.AreEqual("-1 -1 14", edge, "sealed rock must not be returned as a fake floor edge");
            Assert.AreEqual(12, t.RawDigNibble(10, 10, 0),
                "the failed flood still keeps the native seed OR side effect");

            // The faithful carve: each dig_apply steps the node's corners one rim level; the
            // loop runs until the rim collapses and the completion sweep clears the mark. A single
            // node dig notches the shared corner (a full flat tile takes its 4 nodes) — the loop
            // ending with the mark auto-cleared is the faithful completion signal.
            int rim0 = t.RimAtNode(10, 10);
            int applied = 0;
            for (int k = 0; k < 32 && interp.Eval("dig_apply {10 10 15} 0").Trim() == "1"; k++) applied++;
            int rim1 = t.RimAtNode(10, 10);
            TestContext.WriteLine($"dig_apply cycles: {applied}, node rim {rim0}→{rim1}, marked={t.IsDigMarked(10, 10, 0)}");
            Assert.Greater(applied, 0, "dig_apply never carved anything");
            Assert.IsTrue(rim1 < rim0, "the node rim never lowered");
            Assert.IsFalse(t.IsDigMarked(10, 10, 0), "the mark must self-clear when the dig completes");
        }

        // Primitive smoke: the dig natives in the exact Dig_DriveLoop setup (open ground + one
        // marked wall) — isolates the C# primitives from the Tcl chain when the loop test fails.
        [Test, Explicit]
        public void Dig_Primitives_Direct()
        {
            var (interp, ctx, _) = RealHost();
            int dtx = 33, dty = 30;
            var t = MakeOpenTerrain(64, 64, (dtx, dty));
            ctx.Terrain = t;
            var g = Spawn(interp, ctx, "Zwerg", "32 30 8");
            g.Owner = 0;
            t.SetDigMark(dtx, dty, 0);

            ctx.This = g;
            TestContext.WriteLine($"marked={t.IsDigMarked(dtx, dty, 0)} target={t.DigTarget(dtx, dty, 0)} rim={t.RimAtNode(dtx, dty)}");
            string dn = interp.Eval("dig_next {32 30 8} this").Trim();
            TestContext.WriteLine($"dig_next → '{dn}'");
            string de = interp.Eval("get_digedge {" + dtx + " " + dty + "} this").Trim();
            TestContext.WriteLine($"get_digedge → '{de}'");
            string fx = interp.Eval("vector_fixdig {" + dn + "} {32 30 8}").Trim();
            TestContext.WriteLine($"vector_fixdig → '{fx}'");
            int rim0 = t.RimAtNode(dtx, dty);
            string da = interp.Eval("dig_apply {" + dn + "} this").Trim();
            int rim1 = t.RimAtNode(dtx, dty);
            TestContext.WriteLine($"dig_apply → '{da}', rim {rim0}→{rim1}, blk now {t.BlockIdx[dty * t.Width + dtx]}");
            ctx.This = null;

            Assert.IsTrue(dn.StartsWith("33 30"), $"dig_next must pick the marked voxel, got '{dn}'");
            Assert.AreEqual("1", da, "dig_apply must carve the marked node");
            Assert.IsTrue(rim1 < rim0, "the node rim must lower after one carve");
        }

        // Temporary diagnostic: Dig_DriveLoop with the per-command chain trace on, dumping the tail.
        [Test, Explicit]
        public void Dig_DriveLoop_Traced()
        {
            var (interp, ctx, sim) = RealHost();
            int dtx = 33, dty = 30;
            var t = MakeOpenTerrain(64, 64, (dtx, dty));
            ctx.Terrain = t;
            var g = Spawn(interp, ctx, "Zwerg", "32 30 8");
            interp.Eval($"set_worktime {g.Handle} 0 12");
            for (int i = 0; i < 10; i++) sim.Step(0.1);
            t.SetDigMark(dtx, dty, 0);

            string logPath = "diggles_digtrace.log";
            Diggles.Runtime.Diagnostics.DigglesDebugLog.MaxLines = 20000;
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin(logPath);
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Tracked.Add(g.Handle);
            DigglesTclEngine.VerboseScriptLog = true;

            ctx.This = g;
            interp.Eval($"set_event this evt_task_dig -target {g.Handle} -pos1 {{{dtx} {dty}}}");
            ctx.This = null;
            for (int i = 0; i < 25; i++) sim.Step(0.1);
            DigglesTclEngine.VerboseScriptLog = false;
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled = false;

            var lines = System.IO.File.ReadAllLines(logPath);
            TestContext.WriteLine($"trace lines: {lines.Length}; rim={t.RimAtNode(dtx, dty)} marked={t.IsDigMarked(dtx, dty, 0)}");
            int from = System.Math.Max(0, lines.Length - 400);
            for (int i = from; i < lines.Length; i++) TestContext.WriteLine(lines[i]);
        }

        // The faithful dig order: evt_task_dig must build the dig tasklist (dig_starttask) and enter
        // work_dispatch (the same gate harvest uses). Mirrors tourn_2100's `set_event <z> evt_task_dig
        // -target <z> -pos1 {tx ty}`. (No GO ⇒ the walk degrades, but the order/tasklist flow is the point.)
        [Test, Explicit]
        public void Dig_DriveLoop()
        {
            var (interp, ctx, sim) = RealHost();
            int dtx = 33, dty = 30;
            var t = MakeOpenTerrain(64, 64, (dtx, dty));   // one diggable wall tile in open ground
            ctx.Terrain = t;

            var g = Spawn(interp, ctx, "Zwerg", "32 30 8");   // standing right beside the wall tile
            interp.Eval($"set_worktime {g.Handle} 0 12");   // always worktime → dig_continue gate passes
            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle to idle

            t.SetDigMark(dtx, dty, 0);                      // owner 0, cnt 0 = full dig
            int rim0 = t.RimAtNode(dtx, dty);
            ctx.This = g;
            interp.Eval($"set_event this evt_task_dig -target {g.Handle} -pos1 {{{dtx} {dty}}}");
            ctx.This = null;

            bool sawWork = false, sawDigTask = false;
            for (int i = 0; i < 80; i++)
            {
                sim.Step(0.1);
                if (g.CurrentState == "work_dispatch" || g.CurrentState == "task") sawWork = true;
                foreach (var task in g.TaskList) if (task.Contains("dig")) { sawDigTask = true; break; }
                if (i % 8 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} wr={g.WalkResult} rim={t.RimAtNode(dtx, dty)} marked={t.IsDigMarked(dtx, dty, 0)} tasklist={g.TaskList.Count} action={(g.ActiveAction != null)}");
            }
            int rim1 = t.RimAtNode(dtx, dty);
            TestContext.WriteLine($"RESULT dig: sawWork={sawWork} sawDigTask={sawDigTask} rim {rim0}->{rim1} marked={t.IsDigMarked(dtx, dty, 0)} finalState={g.CurrentState}");
            Assert.IsTrue(sawWork || sawDigTask, "gnome never entered the dig work flow after evt_task_dig");
            // The get_walkresult unblock plus the faithful carve: the loop must advance past the
            // walk and carve the marked node step by step until the completion sweep clears the mark.
            Assert.IsTrue(rim1 < rim0, $"dig never carved the marked node (rim {rim0}->{rim1})");
            Assert.IsFalse(t.IsDigMarked(dtx, dty, 0), "the dig never completed (mark still set)");
        }

        // The core proof: an idle gnome autonomously picks up a painted dig mark — no manual event.
        // The ProdDispatcher must register the idle gnome (gnome_idle this 1), see the mark, and raise
        // evt_autoprod_dig on it → the unchanged Tcl evt_autoprod_dig_proc → work_auto → it digs.
        [Test, Explicit]
        public void Dig_Autonomous()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            int dtx = 33, dty = 30;
            var t = MakeOpenTerrain(64, 64, (dtx, dty));
            ctx.Terrain = t;

            var g = Spawn(interp, ctx, "Zwerg", "32 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");   // always worktime → seeks work
            t.SetDigMark(dtx, dty, 0);                      // paint a dig order — no manual set_event

            int rim0 = t.RimAtNode(dtx, dty);
            bool wasPooled = false, sawAutoDig = false;
            for (int i = 0; i < 160; i++)
            {
                sim.Step(0.1);
                if (ctx.ProdDispatcher!.IsPooled(g.Handle, 0)) wasPooled = true;
                g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                if (wt == "dig") sawAutoDig = true;
                if (i % 8 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} wt={wt} pooled={ctx.ProdDispatcher!.IsPooled(g.Handle, 0)} cd={g.WorkBusyCooldown} ws={g.WorkStart} wl={g.WorkLen} rs={interp.Eval($"get_remaining_sparetime {g.Handle}").Trim()} rim={t.RimAtNode(dtx, dty)}");
            }
            int rim1 = t.RimAtNode(dtx, dty);
            TestContext.WriteLine($"RESULT auto-dig: wasPooled={wasPooled} sawAutoDig={sawAutoDig} rim {rim0}->{rim1} marked={t.IsDigMarked(dtx, dty, 0)}");
            // wasPooled is informational only: the faithful per-call one-shot (FUN_00434a80:50)
            // pools + assigns + unpools inside one gnome_idle call, so a per-step poll usually
            // never observes the pooled state — the dispatch itself (sawAutoDig) is the proof.
            Assert.IsTrue(sawAutoDig, "idle gnome was NOT autonomously dispatched evt_autoprod_dig for a painted mark");
            Assert.IsTrue(rim1 < rim0, $"autonomous dig never carved the marked node (rim {rim0}->{rim1})");
        }

        // Autonomous harvest: an idle gnome must pick up a free Pilz with no manual order — the
        // dispatcher raises evt_autoprod_harvest → unchanged evt_autoprod_harvest_proc → harvest flow.
        [Test, Explicit]
        public void Harvest_Autonomous()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);   // open ground, no walls (so dig has nothing; harvest wins)

            var g = Spawn(interp, ctx, "Zwerg", "30 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 9");
            var p = Spawn(interp, ctx, "Pilz", "31 30 8");
            // A fresh Pilz spawns locked (pilz.tcl obj_init: set_lock this 1, cleared at ripeness)
            // and the dispatcher's ripeness gate skips locked ones — ripen it for the test.
            interp.Eval($"set_lock {p.Handle} 0");
            // Autonomous harvest is demand-driven (the native emits it only when a production
            // building has unmet input demand) — stand up a scheduled, invented workshop so the
            // HasActiveProduction gate opens (the free-gather model is intentionally absent).
            var fire = Spawn(interp, ctx, "Feuerstelle", "40 30 8");
            fire.Owner = 0;
            fire.BuildupComplete = true;
            fire.ProdSlots["Grillpilz"] = 1;
            // No hand-seeding: preinvented Bp reads the def_attrib default (genericprod.tcl:147-156).
            Assert.AreEqual("1", interp.Eval("get_owner_attrib 0 BpGrillpilz").Trim(),
                "preinvented BpGrillpilz must read default 1 from classinit with no seeding");

            bool sawHarvest = false;
            for (int i = 0; i < 120; i++)
            {
                sim.Step(0.1);
                g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                if (wt == "harvest") sawHarvest = true;
                if (i % 12 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} wt={wt} pilzAlive={ctx.Entities.ContainsKey(p.Handle)}");
            }
            TestContext.WriteLine($"RESULT auto-harvest: sawHarvest={sawHarvest} pilzAlive={ctx.Entities.ContainsKey(p.Handle)}");
            Assert.IsTrue(sawHarvest, "idle gnome was NOT autonomously dispatched evt_autoprod_harvest for a free Pilz");
        }

        // Autonomous production: an owned workshop with a scheduled, invented item must pull an idle
        // gnome to work it — the dispatcher raises evt_autoprod_workat → evt_autoprod_workat_proc sets
        // current_workplace = the building and runs prod_autobuild.
        [Test, Explicit]
        public void Production_Autonomous()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);

            var fire = Spawn(interp, ctx, "Feuerstelle", "35 30 8");
            fire.Owner = 0;
            fire.BuildupComplete = true;
            fire.ProdSlots["Grillpilz"] = 1;                    // a scheduled product
            // No hand-seeding: preinvented Bp reads the def_attrib default (genericprod.tcl:147-156).
            Assert.AreEqual("1", interp.Eval("get_owner_attrib 0 BpGrillpilz").Trim(),
                "preinvented BpGrillpilz must read default 1 from classinit with no seeding");

            var g = Spawn(interp, ctx, "Zwerg", "33 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 9");

            bool sawWork = false; string wp = "";
            for (int i = 0; i < 120; i++)
            {
                sim.Step(0.1);
                g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                g.VarsScope.Vars.TryGetValue("current_workplace", out var w);
                if (wt == "work") { sawWork = true; wp = w ?? ""; }
                if (i % 12 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} wt={wt} wp={w}");
            }
            TestContext.WriteLine($"RESULT auto-prod: sawWork={sawWork} workplace={wp} building={fire.Handle}");
            Assert.IsTrue(sawWork, "idle gnome was NOT autonomously dispatched evt_autoprod_workat for a scheduled building");
            Assert.AreEqual(fire.Handle.ToString(), wp, "gnome's workplace is not the production building");
        }

        // Gap-fill smoke test: the de-stubbed return-critical commands must return real values (not "0"),
        // since the unchanged Tcl branches on them. Covers the list/vector primitives, ownership round-trip,
        // diplomacy, skill enumeration, tasklist_find sentinel, and the family store.
        [Test, Explicit]
        public void GapCommands_Smoke()
        {
            var (interp, ctx, _) = RealHost();

            // list ops
            var lorRes = TclList.Parse(interp.Eval("lor {a b} {b c}").Trim());
            Assert.AreEqual(3, lorRes.Count, "lor union size");
            Assert.IsTrue(lorRes.Contains("a") && lorRes.Contains("b") && lorRes.Contains("c"), "lor union members");
            Assert.AreEqual("2", interp.Eval("lcount {x a y a z} a").Trim(), "lcount occurrences of 'a'");

            // vector primitives
            Assert.AreEqual(5f, float.Parse(interp.Eval("vector_abs {3 4 0}").Trim(), System.Globalization.CultureInfo.InvariantCulture), 1e-3f, "vector_abs |{3,4,0}|");
            Assert.AreEqual("1", interp.Eval("vector_equal {1 0 0} {1.0 0.0 0.0}").Trim(), "vector_equal float-literal tolerance");
            Assert.AreEqual("0", interp.Eval("vector_equal {1 0 0} {2 0 0}").Trim(), "vector_equal differs");
            Assert.AreEqual("9", interp.Eval("vector_unpackz [vector_setz {1 2 3} 9]").Trim(), "vector_setz z");
            Assert.AreEqual("1", interp.Eval("vector_inbox {1 1 1} {0 0 0} {2 2 2}").Trim(), "vector_inbox inside");
            Assert.AreEqual("0", interp.Eval("vector_inbox {5 1 1} {0 0 0} {2 2 2}").Trim(), "vector_inbox outside");

            // diplomacy (string-compared): a self-pair reads "own" — the native diplomacy
            // table code 0 (FUN_00669000:130-135); an old "ally" expectation predated the
            // table port and asserted the shortcut, not the law.
            Assert.AreEqual("own", interp.Eval("get_diplomacy 0 0").Trim());
            Assert.AreEqual("neutral", interp.Eval("get_diplomacy 0 1").Trim());

            // ownership round-trip via the e.Owner field
            var it = Spawn(interp, ctx, "Zwerg", "10 10 8");
            interp.Eval($"set_owner {it.Handle} 2");
            Assert.AreEqual(2, it.Owner, "set_owner wrote e.Owner");
            Assert.AreEqual("2", interp.Eval($"get_owner {it.Handle}").Trim(), "get_owner reads e.Owner");

            // tasklist_find: index, or -1 when absent (not 0)
            it.TaskList.Clear(); it.TaskList.Add("foo"); it.TaskList.Add("bar");
            Assert.AreEqual("1", interp.Eval($"tasklist_find {it.Handle} bar").Trim(), "tasklist_find present");
            Assert.AreEqual("-1", interp.Eval($"tasklist_find {it.Handle} nope").Trim(), "tasklist_find absent = -1");

            // get_expattrib: ordered exp_ skill names (depends on def_attrib having loaded)
            string exps = interp.Eval("get_expattrib 0").Trim();
            Assert.IsTrue(exps.Contains("exp_"), "get_expattrib lists exp_ skills: '" + exps + "'");

            // partner_info family store round-trip
            ctx.This = it;
            Assert.AreEqual("0", interp.Eval($"partner_info getpregnancy {it.Handle}").Trim(), "default not pregnant");
            interp.Eval($"partner_info setpregnancy {it.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"partner_info getpregnancy {it.Handle}").Trim(), "pregnancy set");
            ctx.This = null;
        }

        // Multi-gnome anti-collision (placelock): two idle gnomes + two separated dig marks must each
        // take a different mark (nearest-gnome matching + placelock reservation), and both get dug —
        // not both piling on one tile while the other mark sits.
        [Test, Explicit]
        public void Dig_TwoGnomes_NoCollision()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            var t = MakeOpenTerrain(64, 64, (33, 30), (33, 44));   // two walls, 14 apart
            ctx.Terrain = t;

            var a = Spawn(interp, ctx, "Zwerg", "32 30 8"); a.Owner = 0; interp.Eval($"set_worktime {a.Handle} 0 9");
            var b = Spawn(interp, ctx, "Zwerg", "32 44 8"); b.Owner = 0; interp.Eval($"set_worktime {b.Handle} 0 9");
            t.SetDigMark(33, 30, 0);
            t.SetDigMark(33, 44, 0);
            int r10 = t.RimAtNode(33, 30), r20 = t.RimAtNode(33, 44);

            for (int i = 0; i < 160; i++) sim.Step(0.1);

            int r11 = t.RimAtNode(33, 30), r21 = t.RimAtNode(33, 44);
            TestContext.WriteLine($"RESULT two-gnomes: mark1 rim {r10}->{r11} marked={t.IsDigMarked(33, 30, 0)}; " +
                                  $"mark2 rim {r20}->{r21} marked={t.IsDigMarked(33, 44, 0)}");
            Assert.IsTrue(r11 < r10, "first mark not dug — gnomes failed to split the work");
            Assert.IsTrue(r21 < r20, "second mark not dug — gnomes failed to split the work");
        }
    }
}
