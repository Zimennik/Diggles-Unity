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

    // CHOP-AGE LAW (regression, manual test):
    //
    // User law (original-game verified): when the player MANUALLY orders a mushroom chopped,
    // the gnome chops it REGARDLESS of ripeness. Only the AUTO / prod-dispatcher path gates on
    // ripeness.
    //
    // RE — the native manual chop chain is AGE-BLIND end to end:
    // • eventgen.tcl:236-243 — a command-click on a Pilz fires evt_task_harvest with NO age
    // branch (Pilz is subtype "food", so it never reaches the production/attack gates).
    // • z_events.tcl:900 evt_task_harvest_proc → z_procs.tcl:739 `harvest` — no ripeness gate;
    // PilzAge is read ONLY to scale the number of chop swings (abschlag,:763).
    // • pilz.tcl die (:118) yields by phase (phase1→1 hat/0 wood, phase2→1/1, phase3→2/1) —
    // an unripe chop still kills the mushroom and drops the smaller yield.
    //
    // The ONE ripeness-sensitive step in the manual chain is z_procs.tcl:1721
    // check_lockedbyother — it aborts (tasklist_clear) IFF get_lock $item AND
    // current_lock_obj != $item. pilz.tcl:23 set_lock 1 at spawn, cleared only at agephase 3
    // (:108), so an UNRIPE pilz IS locked. The manual chain's own harvest→lock_item
    // (z_procs.tcl:746) sets current_lock_obj = $item FIRST, so for the ordering gnome the
    // check must PASS even on a locked (unripe) pilz. A RIPE pilz has get_lock 0 and never
    // reaches the comparison — which is why the port's bug only showed on unripe.
    //
    // The AUTO path (ProdDispatcher, IsLocked gate — FUN_005f4220:236) is intentionally the
    // OPPOSITE: it skips locked (unripe) resources. That gate is preserved.
    public sealed class ChopAgeLawTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            _ = new DigglesGameSim(ctx);
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class Pilz food material 0 {} {}");

            // The real z_procs.tcl lock procs (verbatim), which the manual harvest chain runs.
            interp.Eval(
                "proc lock_item {item} {\n" +
                "  global current_lock_obj\n" +
                "  if { $current_lock_obj != 0 } { set_lock $current_lock_obj 0; set current_lock_obj 0 }\n" +
                "  if { $item != 0 } { set current_lock_obj $item; set_lock $current_lock_obj 1 }\n" +
                "}");
            interp.Eval(
                "proc check_lockedbyother {item} {\n" +
                "  global current_lock_obj\n" +
                "  if [obj_valid $item] {\n" +
                "    if [get_lock $item] {\n" +
                "      if {$current_lock_obj!=$item} { tasklist_clear this }\n" +
                "    }\n" +
                "  }\n" +
                "}");
            return (interp, ctx);
        }

        static string LockVar(DigglesTclEntity e)
            => e.VarsScope.Vars.TryGetValue("current_lock_obj", out var v) ? v : "<unset>";

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static DigglesTclEntity Gnome(TclInterpreter interp, DigglesTclContext ctx, bool initLock)
        {
            var g = Spawn(interp, ctx, "G", "32 30 8");
            g.Owner = 0;
            if (initLock) g.VarsScope.Vars["current_lock_obj"] = "0";   // zwerg.tcl:44
            return g;
        }

        // ------------------------------------------------------------------
        // THE REPRO: manual chop of an UNRIPE (locked) pilz. harvest locks the item, then
        // check_lockedbyother must NOT abort the task list.
        // ------------------------------------------------------------------
        [Test]
        public void ManualChop_UnripeLockedPilz_NotAborted()
        {
            var (interp, ctx) = Host();
            var g = Gnome(interp, ctx, initLock: true);
            var pilz = Spawn(interp, ctx, "Pilz", "40 30 8");
            interp.Eval($"set_lock {pilz.Handle} 1");   // pilz.tcl:23 — unripe is locked

            // The manual chain: harvest's first act is lock_item (z_procs.tcl:746).
            DigglesTclEngine.RunInEntityContext(ctx, g, $"lock_item {pilz.Handle}");
            g.TaskList.Add("play_anim standstill");   // stand-in for the built chop steps
            //...then, during task exec, check_lockedbyother runs.
            DigglesTclEngine.RunInEntityContext(ctx, g, $"check_lockedbyother {pilz.Handle}");

            Assert.AreEqual("1", interp.Eval($"get_lock {pilz.Handle}").Trim(),
                "pilz stays locked (it is the gnome's current_lock_obj)");
            Assert.AreEqual(pilz.Handle.ToString(), LockVar(g),
                "lock_item claimed the pilz for THIS gnome");
            Assert.AreEqual(1, g.TaskList.Count,
                "manual chop of an unripe (locked) pilz must NOT be aborted by check_lockedbyother");
        }

        // The FAILURE MODE the port hit: current_lock_obj never initialized (gnome obj_init did
        // not seed it, or lock_item's `!= 0` expr on an empty value mis-fired) → lock_item did not
        // claim the item → check_lockedbyother sees locked + current_lock_obj != item → abort.
        [Test]
        public void ManualChop_UnripeLockedPilz_UninitLockVar_StillNotAborted()
        {
            var (interp, ctx) = Host();
            var g = Gnome(interp, ctx, initLock: false);   // NO seed
            var pilz = Spawn(interp, ctx, "Pilz", "40 30 8");
            interp.Eval($"set_lock {pilz.Handle} 1");

            DigglesTclEngine.RunInEntityContext(ctx, g, $"lock_item {pilz.Handle}");
            g.TaskList.Add("play_anim standstill");
            DigglesTclEngine.RunInEntityContext(ctx, g, $"check_lockedbyother {pilz.Handle}");

            Assert.AreEqual(pilz.Handle.ToString(), LockVar(g),
                "lock_item must claim the pilz even from an uninitialized current_lock_obj");
            Assert.AreEqual(1, g.TaskList.Count,
                "manual chop must survive even when current_lock_obj started unset");
        }

        // Sanity: a RIPE pilz (get_lock 0) is trivially fine — the comparison is never reached.
        [Test]
        public void ManualChop_RipePilz_NotAborted()
        {
            var (interp, ctx) = Host();
            var g = Gnome(interp, ctx, initLock: true);
            var pilz = Spawn(interp, ctx, "Pilz", "40 30 8");
            interp.Eval($"set_lock {pilz.Handle} 0");   // ripe (agephase 3, pilz.tcl:108)

            DigglesTclEngine.RunInEntityContext(ctx, g, $"lock_item {pilz.Handle}");
            g.TaskList.Add("play_anim standstill");
            DigglesTclEngine.RunInEntityContext(ctx, g, $"check_lockedbyother {pilz.Handle}");

            Assert.AreEqual(1, g.TaskList.Count, "ripe chop must never abort");
        }

        // ==================================================================
        // END-TO-END (real classes): raise the actual evt_task_harvest the manual click fires and
        // step the sim, comparing an UNRIPE (locked) pilz vs a RIPE one. This is the faithful Play
        // repro the proc-level test could not surface.
        // ==================================================================
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
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

        DiggableTerrain MakeOpenTerrain(DigglesTclContext ctx, int w, int h)
        {
            var go = new GameObject("DiggableTerrain");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            for (int k = 0; k < block.Length; k++) block[k] = 59;
            var bdb = BdbParser.Parse(DataRoot + "/BlockDB2.BDB");
            t.Init(new BdbPmpWorldBuilder.TclWorldResult { Width = w, Height = h, BlockIdx = block, Terrain = new byte[w * h] }, bdb, "", () => { });
            ctx.Terrain = t;
            return t;
        }

        static DigglesTclEntity RSpawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        // Drive the real manual chop and return whether the chop chain ran to completion
        // (the pilz was killed / a Pilzhut or Pilzstamm dropped), plus the peak task count reached.
        (bool pilzGone, bool yieldDropped, int peakTasks, int minTasksAfterBuild) RunManualChop(bool ripe)
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            MakeOpenTerrain(ctx, 64, 64);

            var g = RSpawn(interp, ctx, "Zwerg", "33 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");

            var pilz = RSpawn(interp, ctx, "Pilz", "35 30 8");
            pilz.Owner = 0;
            if (ripe)
            {
                interp.Eval($"set_attrib {pilz.Handle} PilzAge 3");
                interp.Eval($"set_lock {pilz.Handle} 0");   // agephase 3 unlocks (pilz.tcl:108)
            }
            else
            {
                interp.Eval($"set_lock {pilz.Handle} 1");   // unripe stays locked (pilz.tcl:23)
            }
            int pilzHandle = pilz.Handle;
            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle both to idle

            // The manual click fires evt_task_harvest on the gnome (eventgen.tcl:240).
            var seq = new List<string>();
            var watch = new HashSet<string> { "lock_item", "unlock_item", "stop_prod", "change_tool",
                "check_lockedbyother", "check_valid", "tasklist_clear", "harvest", "beam_back", "prod_gnome_state" };
            interp.CommandTrace = (cmd, cargs, res) =>
            {
                if (!watch.Contains(cmd)) return;
                string gl = g.VarsScope.Vars.TryGetValue("current_lock_obj", out var v) ? v : "?";
                string a = cargs.Length > 1 ? cargs[1] : "";
                if (seq.Count < 60) seq.Add($"{cmd} {a} | gLock={gl}");
            };
            ctx.This = g;
            interp.Eval($"set_event this evt_task_harvest -subject1 {pilzHandle}");
            ctx.This = null;

            string GnomeLock() => g.VarsScope.Vars.TryGetValue("current_lock_obj", out var v) ? v : "<unset>";
            string RootLock() => interp.GlobalScope.Vars.TryGetValue("current_lock_obj", out var v) ? v : "<unset>";
            TestContext.WriteLine($"AFTER-EVENT gnomeLock={GnomeLock()} rootLock={RootLock()} pilzLock={interp.Eval($"get_lock {pilzHandle}").Trim()}");

            int peak = 0, minAfterBuild = int.MaxValue; bool built = false; bool yielded = false;
            string lastTop = "<none>"; var trace = new List<string>();
            for (int i = 0; i < 400; i++)
            {
                string top = g.TaskList.Count > 0 ? g.TaskList[0] : "<empty>";
                if (top != lastTop) { trace.Add($"[{i}] n={g.TaskList.Count} top={top} gLock={GnomeLock()} rLock={RootLock()}"); lastTop = top; }
                sim.Step(0.1);
                int tc = g.TaskList.Count;
                if (tc > 0) built = true;
                peak = Mathf.Max(peak, tc);
                if (built) minAfterBuild = Mathf.Min(minAfterBuild, tc);
                foreach (var e in ctx.Entities.Values)
                    if (e.Class?.Name == "Pilzhut" || e.Class?.Name == "Pilzstamm") { yielded = true; break; }
                if (i == 12) { interp.CommandTrace = null; }   // stop tracing after the chain settled
            }
            foreach (var tl in trace) TestContext.WriteLine("  " + tl);
            TestContext.WriteLine("=== CMD SEQ ===");
            foreach (var sq in seq) TestContext.WriteLine("  " + sq);
            bool gone = !ctx.Entities.ContainsKey(pilzHandle)
                        || (ctx.Entities.TryGetValue(pilzHandle, out var p) && p.Class?.Name != "Pilz");
            TestContext.WriteLine($"RESULT ripe={ripe}: pilzGone={gone} yielded={yielded} peakTasks={peak} minTasksAfterBuild={(minAfterBuild==int.MaxValue?-1:minAfterBuild)}");
            return (gone, yielded, peak, minAfterBuild == int.MaxValue ? -1 : minAfterBuild);
        }

        [Test, Explicit]
        public void E2E_ManualChop_UnripePilz_Completes()
        {
            var r = RunManualChop(ripe: false);
            Assert.Greater(r.peakTasks, 0, "harvest must BUILD a chop task list for an unripe pilz");
            Assert.IsTrue(r.pilzGone || r.yieldDropped,
                "manual chop of an UNRIPE pilz must run to completion (pilz dies / yield drops) — the native law");
        }

        [Test, Explicit]
        public void E2E_ManualChop_RipePilz_Completes()
        {
            var r = RunManualChop(ripe: true);
            Assert.Greater(r.peakTasks, 0, "harvest must build a chop task list for a ripe pilz");
            Assert.IsTrue(r.pilzGone || r.yieldDropped,
                "manual chop of a ripe pilz completes (control)");
        }

        // ==================================================================
        // PLAY-MIRROR the
        // manual chain must survive a camp WITH OPEN AUTO JOBS. Native law: the evt_task procs
        // mark the gnome planner-BUSY via `prod_gnome_state this harvest <item>` (z_events.tcl:
        // 935) — TCL:CTclObjMethods::ProdGnomeState (FUN_006a7230) clears the planner record's
        // availability byte for EVERY non-"idle" disposition (inline: guard:152, fight:175,
        // putdown:262, pickup:304, explore:390 all write rec+4=0; routed: "unknown"
        // FUN_005ebdf0:10, harvest FUN_005ec2e0:17 + state code 0xd:34; only "idle" writes 1,
        //:109). The port ignored the stamp → the ordered chopper stayed in the dispatcher's
        // idle pool → the next sweep reserved him for any open job (kind-4 haul etc.), whose
        // step events stomped the manual tasklist mid-approach → arrival with an empty list →
        // tool-putaway cleanup. Round-1's e2e world had NO jobs, so the theft never fired.
        // ==================================================================

        (bool stolen, bool pilzGone) RunManualChopWithBaitJob()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            MakeOpenTerrain(ctx, 64, 64);
            var disp = ctx.ProdDispatcher!;

            // BAIT: a production place missing ONE Pilzhut + a far unlocked Pilzhut in the world
            // (outside the |Δx|<4 absorb box) = a permanent open kind-4 haul job, the player's-camp
            // stand-in (same shape as ProdMaterialHaulTests.Haul_*).
            interp.Eval("def_class TB none production 0 {} {\n"
                        + "method prod_items {} {return {TProd}}\n"
                        + "method prod_item_materials {item} {return {Pilzhut}}\n}");
            interp.Eval("def_attrib BpTProd 0 1 1");   // pre-invented
            var bait = RSpawn(interp, ctx, "TB", "45 30 8");
            bait.Owner = 0; bait.BuildupComplete = true; bait.ProdSlots["TProd"] = 1;
            var hut = RSpawn(interp, ctx, "Pilzhut", "52 30 8");
            hut.Owner = 0;

            var g = RSpawn(interp, ctx, "Zwerg", "33 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");

            var pilz = RSpawn(interp, ctx, "Pilz", "35 30 8");
            pilz.Owner = 0;
            interp.Eval($"set_lock {pilz.Handle} 1");   // unripe = spawn-locked (pilz.tcl:23)
            int pilzHandle = pilz.Handle;

            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle
            disp.OnGnomeIdle(g);   // the pre-order idle announce (work_idle think, z_work_states.tcl:247)

            // THE PLAY CLICK: the unmodified event_generator decides, then the spec fires —
            // exactly ClickToMoveService's consumption (eventgen.tcl:236-243 → evt_task_harvest).
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, pilzHandle, pilz.Position, "-command");
            bool fired = DigglesTclEngine.EvtGenFire(ctx, g.Handle);
            TestContext.WriteLine($"eventgen verb='{verb.Trim()}' fired={fired}");
            Assert.IsTrue(fired, "eventgen must answer the Pilz click with an event spec");

            Assert.AreEqual(1, disp.MissingMaterials(bait, "TProd").Count,
                "bait sanity: the open kind-4 haul deficit must be live, else the theft has nothing to steal with");

            sim.Step(0.1);   // the handler runs: harvest builds the chain + prod_gnome_state harvest

            // PLAY-WALK SURROGATE: headless walks degrade to instant waits, so the chain would
            // outrun the 5 s gnome_failed_work breather (evt_task_harvest_proc's cleanup top,
            // z_events.tcl:913 → the port's FailRetaskDelayTicks=50 embargo) that shields the
            // gnome from the sweep right after the order. In Play the approach walk + hat anims
            // last well past it. Model that: hold the chain busy for ~10 s mid-approach.
            g.TaskList.Insert(0, "action this wait 10");

            bool stolen = false, yielded = false;
            for (int i = 0; i < 500 && !yielded; i++)
            {
                stolen |= disp.HasPlan(g.Handle) || AnyEvent(g, "evt_change_muetze",
                    "evt_autoprod_workat", "evt_autoprod_harvest", "evt_autoprod_dig", "evt_autoprod_transport");
                sim.Step(0.1);   // think tick also decays the breather (DigglesGameSim:361)
                if (i % 10 == 9) disp.SweepNow(0);   // the recurring dispatcher cadence
                foreach (var e in ctx.Entities.Values)
                    if (e.Class?.Name == "Pilzstamm" || (e.Class?.Name == "Pilzhut" && e.Handle != hut.Handle))
                    { yielded = true; break; }
            }
            bool gone = !ctx.Entities.ContainsKey(pilzHandle);
            TestContext.WriteLine($"RESULT bait: stolen={stolen} pilzGone={gone} yielded={yielded}");
            return (stolen, gone || yielded);
        }

        [Test, Explicit]
        public void E2E_ManualChop_UnripePilz_WithOpenAutoJob_NotStolenByDispatcher()
        {
            var r = RunManualChopWithBaitJob();
            Assert.IsFalse(r.stolen,
                "the dispatcher must NOT reserve/steal a manually-ordered chopper — "
                + "prod_gnome_state harvest = planner-busy (FUN_006a7230 → FUN_005ec2e0:17 rec+4=0)");
            Assert.IsTrue(r.pilzGone,
                "the manual chop of the unripe pilz must complete in a camp with open auto jobs");
        }

        // ==================================================================
        // AUTO PATH keeps its native RIPENESS gate: the dispatcher's harvest escalation skips
        // LOCKED resources (`(obj+0x3d0 & 4) == 0`, FUN_005f4220:236; port = IsLocked at the
        // CollectHarvestJobs resource scan). An unripe pilz is locked (pilz.tcl:23/:108), so the
        // AUTO chop must NOT dispatch — only the manual order is age-blind.
        // ==================================================================

        (TclInterpreter interp, DigglesTclContext ctx, DigglesTclEntity gnome) AutoHost(bool pilzLocked)
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            _ = new DigglesGameSim(ctx);
            ctx.LocalPlayer = 0;
            interp.Eval("def_class G none gnome 0 {} {}");
            // A production place whose recipe needs ONE Pilzhut (ProduceMap: Pilzhut → Pilz).
            interp.Eval("def_class B none production 0 {} {\n"
                        + "method prod_items {} {return {Prod}}\n"
                        + "method prod_item_materials {item} {return {Pilzhut}}\n}");
            interp.Eval("def_class Pilzhut none item 0 {} {}");
            interp.Eval("def_class Pilz food material 0 {} {}");
            interp.Eval("def_attrib BpProd 0 1 1");   // pre-invented

            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;
            b.BuildupComplete = true;
            b.ProdSlots["Prod"] = 1;

            var pilz = Spawn(interp, ctx, "Pilz", "40 30 8");
            pilz.Owner = 0;
            interp.Eval($"set_lock {pilz.Handle} {(pilzLocked ? 1 : 0)}");

            var g = Spawn(interp, ctx, "G", "32 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            return (interp, ctx, g);
        }

        static bool AnyEvent(DigglesTclEntity e, params string[] names)
        {
            foreach (var ev in e.PendingEvents)
                foreach (var n in names)
                    if (ev.Name == n) return true;
            return false;
        }

        [Test]
        public void AutoPath_UnripeLockedPilz_NeverDispatchesChop()
        {
            var (_, ctx, g) = AutoHost(pilzLocked: true);
            var disp = ctx.ProdDispatcher!;
            for (int i = 0; i < 10; i++) { disp.OnGnomeIdle(g); disp.SweepNow(0); disp.DecayCooldowns(); }
            Assert.IsFalse(AnyEvent(g, "evt_change_muetze", "evt_autoprod_harvest"),
                "AUTO harvest must SKIP a locked (unripe) pilz — the native resource-scan lock gate "
                + "(FUN_005f4220:236); only the MANUAL order is age-blind");
            Assert.IsFalse(disp.HasPlan(g.Handle), "no kind-6 plan on an all-locked resource field");
        }

        [Test]
        public void AutoPath_RipeUnlockedPilz_DispatchesChop()
        {
            var (_, ctx, g) = AutoHost(pilzLocked: false);
            var disp = ctx.ProdDispatcher!;
            bool dispatched = false;
            for (int i = 0; i < 10 && !dispatched; i++)
            {
                disp.OnGnomeIdle(g); disp.SweepNow(0); disp.DecayCooldowns();
                dispatched = AnyEvent(g, "evt_change_muetze", "evt_autoprod_harvest") || disp.HasPlan(g.Handle);
            }
            Assert.IsTrue(dispatched,
                "control: with the pilz RIPE (unlocked) the auto harvest escalation must dispatch");
        }

        // ==================================================================
        // LAWS — the dispatcher must not steal a manually-tasked gnome.
        //
        // (a) prod_gnome_state <g> <non-idle> = planner-BUSY. TCL:CTclObjMethods::ProdGnomeState
        // (FUN_006a7230): only "idle" sets the availability byte rec+4 = 1 (:109); every task
        // disposition clears it (guard:152 / fight:175 / putdown:262 / pickup:304 /
        // explore:390 inline; "unknown" FUN_005ebdf0:10; harvest FUN_005ec2e0:17, code 0xd
        //:34). The manual chop chain's ONLY planner notification is exactly this stamp
        // (evt_task_harvest_proc, z_events.tcl:935) — no gnome_idle call.
        //
        // (b) a TRANSIENT state must run NEITHER its enter NOR its leave. CObj::HandleStates
        // (005cac90): enter (:56-80) and leave (:30-55) both run at THINK time comparing live
        // +0x200 vs target +0x204; StateTrigger (FUN_0069ec80:109) only writes the target.
        // zwerg.tcl:269-276 pairs state_enter idle {gnome_idle this 1} with state_leave idle
        // {gnome_idle this 0} (work_idle likewise, z_work_states.tcl:219-235) — running the
        // enter for a state that never goes live LEAKS a pool registration with no paired
        // remove, and the sweep then steals the gnome mid-manual-task (
        // axe out → walk up → putaway).
        // ==================================================================

        [Test]
        public void ProdGnomeState_TaskDisposition_MarksPlannerBusy()
        {
            var (interp, ctx, g) = AutoHost(pilzLocked: false);   // ripe pilz = an open auto job exists
            var disp = ctx.ProdDispatcher!;
            disp.OnGnomeIdle(g);                                  // pre-order idle announce
            // The manual order's planner stamp (z_events.tcl:935) — with the item arg, like the real chain.
            interp.Eval($"prod_gnome_state {g.Handle} harvest 999");
            disp.SweepNow(0);
            Assert.IsFalse(disp.HasPlan(g.Handle) || AnyEvent(g, "evt_change_muetze", "evt_autoprod_harvest"),
                "after `prod_gnome_state harvest` the gnome is planner-BUSY (FUN_005ec2e0:17 rec+4=0) — "
                + "the sweep must not reserve him");
        }

        [Test]
        public void TransientIdle_DoesNotLeakIntoDispatcherPool()
        {
            var (interp, ctx, g) = AutoHost(pilzLocked: false);   // ripe pilz = an open auto job exists
            var disp = ctx.ProdDispatcher!;
            // The gnome class with the REAL idle announce discipline (zwerg.tcl:269-276) + a work
            // state; register AFTER AutoHost's def_class G by redefining a fresh class.
            interp.Eval("def_class GZ none gnome 0 {} {\n"
                        + "state_enter idle { gnome_idle this 1 }\n"
                        + "state_leave idle { gnome_idle this 0 }\n"
                        + "state idle {}\n"
                        + "state work_dispatch {}\n"
                        + "state task {}\n}");
            var z = Spawn(interp, ctx, "GZ", "31 30 8");
            z.Owner = 0;
            interp.Eval($"set_worktime {z.Handle} 0 12");

            // A TRANSIENT idle inside one handler (native: neither enter nor leave runs —
            // HandleStates never sees idle as live): e.g. an event chain that bounces through
            // idle to a work state within a single dispatch.
            DigglesTclEngine.RunInEntityContext(ctx, z,
                "state_triggerfresh this idle\nstate_triggerfresh this work_dispatch");

            disp.SweepNow(0);
            Assert.IsFalse(disp.HasPlan(z.Handle) || AnyEvent(z, "evt_change_muetze", "evt_autoprod_harvest"),
                "a transient idle must not register the gnome in the planner pool "
                + "(state_enter runs only when the state goes LIVE — CObj_HandleStates 005cac90:56-80)");
        }

        [Test]
        public void LiveIdle_EnterStillRegisters_AndLeaveStillRemoves()
        {
            var (interp, ctx, g) = AutoHost(pilzLocked: false);
            var disp = ctx.ProdDispatcher!;
            interp.Eval("def_class GZ none gnome 0 {} {\n"
                        + "state_enter idle { gnome_idle this 1 }\n"
                        + "state_leave idle { gnome_idle this 0 }\n"
                        + "state idle {}\n"
                        + "state work_dispatch {}\n}");
            var z = Spawn(interp, ctx, "GZ", "31 30 8");
            z.Owner = 0;
            interp.Eval($"set_worktime {z.Handle} 0 12");

            // idle goes LIVE (a think passes) → the enter must have run → pool has him.
            DigglesTclEngine.RunInEntityContext(ctx, z, "state_triggerfresh this idle");
            DigglesTclEngine.TickEntityState(ctx, z);   // the think tick (HandleStates)
            disp.SweepNow(0);
            bool engaged = disp.HasPlan(z.Handle) || AnyEvent(z, "evt_change_muetze", "evt_autoprod_harvest");
            Assert.IsTrue(engaged,
                "control: a LIVE idle registers via its enter (gnome_idle 1) and the sweep can dispatch him");
        }
    }
}
