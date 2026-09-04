#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The native work-dispatch throttles: dispatch happens on the maintenance pass only —
    // the gnome_idle one-shot FUN_00434bc0 never matches new jobs — so the tests pool via
    // OnGnomeIdle and drive the pass through SweepNow.
    //
    // • Dig: the persistent per-player request list (planner+0x3074, FUN_0043bdd0) funnels a
    // whole marked rect into one request → one kind-5 job → one digger. The scan seeds a
    // request with timer 1.0 (:279-309); gnome_announce_dig refreshes every request within
    // Manhattan<20 to timer 10.0 (FUN_0043bc90:41-51) — fired from dig_starttask
    // (z_dignwalk.tcl:143), re-stamped every dig cycle (:236/:313) and at job assignment
    // (FUN_0043a460:55-72). The job builder decays the timer 1.0/pass and emits nothing
    // while it is positive (FUN_00435f50:53-54,:142-156); the request maintenance decays
    // another 1.0/pass (FUN_0043bdd0:149-150) — an announced request stays closed ~5 passes
    // per stamp, and the active digger keeps re-stamping.
    //
    // • Harvest: demand-driven and single-emission — per-missing-recipe-class (a missing
    // material with no source item of the exact class, FUN_004317a0:71-73) maps through the
    // produce registry (FUN_00432690) to the resource that yields it and becomes one kind-6
    // harvest job per pass (FUN_0043ad40 request → FUN_0043b620 promote → plan
    // FUN_004394e0 → evt_autoprod_harvest, FUN_0043a8a0:70-74). Need = deficit × 0.2
    // (DAT_00737054); cadence ≥5 s pass / +10 s post-emit (FUN_005f1500:53/:159 — the MP
    // twin shares the throttle). One gnome at a time fells mushrooms — none at all while
    // nothing is scheduled or stock covers the class.
    public sealed class WorkDispatchCapsTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly System.Collections.Generic.List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static bool HasEvent(DigglesTclEntity g, string name)
        {
            foreach (var ev in g.PendingEvents) if (ev.Name == name) return true;
            return false;
        }

        // The WorkLoopTests open-terrain harness (one solid diggable tile in air) — needs the real
        // BDB for the faithful carve grid.
        static BdbModel? s_bdb;
        DiggableTerrain MakeOpenTerrain(int w, int h, params (int x, int y)[] solid)
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/BlockDB2.BDB"),
                "original data dir not present — terrain-backed dispatch tests skipped");
            s_bdb ??= BdbParser.Parse(DataRoot + "/BlockDB2.BDB");
            var go = new GameObject("DiggableTerrain");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            for (int k = 0; k < block.Length; k++) block[k] = 59;   // 59 = air
            foreach (var (x, y) in solid) if ((uint)x < (uint)w && (uint)y < (uint)h) block[y * w + x] = 0;
            t.Init(new BdbPmpWorldBuilder.TclWorldResult { Width = w, Height = h, BlockIdx = block, Terrain = new byte[w * h] }, s_bdb!, "", () => { });
            return t;
        }

        // ------------------------------------------------------------------
        // Dig — announce closes the tile (regression: every idle gnome rushed one dig order)
        // ------------------------------------------------------------------

        [Test]
        public void Dig_AnnouncedTile_NotDispatched_ReopensAfterDecay()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            interp.Eval("def_class G none gnome 0 {} {}");
            int dtx = 33, dty = 30;
            var t = MakeOpenTerrain(64, 64, (dtx, dty));
            ctx.Terrain = t;
            t.SetDigMark(dtx, dty, 0);

            var g = Spawn(interp, ctx, "G", "32 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            var disp = ctx.ProdDispatcher!;

            // Pass 1: the incremental bbox scan seeds the dig request (timer 1.0,
            // FUN_0043bdd0:279-309); the job builder runs before the scan, so the job waits for
            // the next pass (MaintenanceTick order: FUN_00435f50 at:172, FUN_0043bdd0 at:217).
            disp.SweepNow(0);
            Assert.AreEqual(1, disp.DigRequestCount(0), "the marked tile must seed one request");

            // A digger announces the point (the Tcl passes the dig point — fractional coords
            // round, FUN_0043bc90:23-24) → every request within Manhattan<20 refreshes to
            // timer 10.0 (:41-51) → the job builder emits nothing while the timer is positive
            // (FUN_00435f50:142-156). The timer decays 2.0 per pass (builder 1.0 + scan 1.0) —
            // 5 closed passes per stamp.
            interp.Eval($"gnome_announce_dig {g.Handle} {{{dtx}.4 {dty}.3 8}}");
            disp.OnGnomeIdle(g);
            for (int pass = 0; pass < 5; pass++)
            {
                disp.SweepNow(0);
                Assert.IsFalse(HasEvent(g, "evt_autoprod_dig"),
                    $"an announce-closed request must not dispatch (pass {pass + 1}/5)");
            }

            // Timer exhausted → the request re-opens and dispatches (need = age·0.5+1,
            // FUN_00435f50:100-130).
            disp.SweepNow(0);
            Assert.IsTrue(HasEvent(g, "evt_autoprod_dig"),
                "after the announce decays the request must dispatch again");
        }

        [Test]
        public void Dig_SecondGnome_NotPulledOntoAnnouncedTile()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            interp.Eval("def_class G none gnome 0 {} {}");
            int dtx = 33, dty = 30;
            var t = MakeOpenTerrain(64, 64, (dtx, dty));
            ctx.Terrain = t;
            t.SetDigMark(dtx, dty, 0);
            var disp = ctx.ProdDispatcher!;

            var g1 = Spawn(interp, ctx, "G", "32 30 8"); g1.Owner = 0;
            var g2 = Spawn(interp, ctx, "G", "31 30 8"); g2.Owner = 0;
            interp.Eval($"set_worktime {g1.Handle} 0 12");
            interp.Eval($"set_worktime {g2.Handle} 0 12");

            // Pass 1 seeds the request; pass 2 emits the job and g1 takes it (the assignment
            // itself re-announces the request — the dispatch tail advances the fresh plan
            // through FUN_0043a460, whose dig-step scan re-stamps timer 10.0,:55-72).
            disp.SweepNow(0);
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsTrue(HasEvent(g1, "evt_autoprod_dig"), "first gnome must take the dig job");

            // The unchanged Tcl would also announce at dig_starttask (z_dignwalk.tcl:143) —
            // simulated here since the fabricated class has no handlers.
            interp.Eval($"gnome_announce_dig {g1.Handle} {{{dtx} {dty} 8}}");

            // Even after g1's 6 s reservation placelock would have lapsed, the announce keeps
            // the request closed — the second gnome must idle on, not pile onto the same dig.
            ctx.PlaceLocks.Rem(g1.Handle);   // force-expire the reservation: announce must hold alone
            disp.OnGnomeIdle(g2);
            for (int pass = 0; pass < 5; pass++)
            {
                disp.SweepNow(0);
                Assert.IsFalse(HasEvent(g2, "evt_autoprod_dig"),
                    $"the announce (not just the 6 s placelock) must keep a worked request closed (pass {pass + 1}/5)");
            }

            // The working digger re-stamps every dig cycle (z_dignwalk.tcl:236/:313) — the
            // request stays closed as long as the announce keeps landing.
            interp.Eval($"gnome_announce_dig {g1.Handle} {{{dtx} {dty} 8}}");
            for (int pass = 0; pass < 3; pass++)
            {
                disp.SweepNow(0);
                Assert.IsFalse(HasEvent(g2, "evt_autoprod_dig"),
                    "a re-stamped announce must keep holding the request closed");
            }
        }

        // ------------------------------------------------------------------
        // Harvest — demand deficit + one emission per cadence (regression: gnomes
        // harvested mushrooms with no production needing them)
        // ------------------------------------------------------------------

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesTclEntity mill,
                DigglesTclEntity pilz, DigglesTclEntity g1, DigglesTclEntity g2) HarvestWorld()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            interp.Eval("def_class Pilz none material 0 {} {}");
            interp.Eval("def_class Pilzhut none material 0 {} {}");
            interp.Eval("def_class Pilzstamm none material 0 {} {}");
            // The kind-6 law made harvest demand per-missing-recipe-class
            // (FUN_00434e60:447-500 → produce map FUN_00432690 → request FUN_0043ad40): the
            // workshop needs a real recipe (Grillpilz ← Pilzhut) and the item pre-invented
            // (an un-invented order dispatches the invention workat instead).
            interp.Eval("def_class B none production 0 {} {\n"
                        + "method prod_item_materials {item} {return {Pilzhut}}\n}");
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_attrib BpGrillpilz 0 1 1");   // pre-invented (genericprod.tcl:151 law)
            var mill = Spawn(interp, ctx, "B", "40 30 8");
            mill.Owner = 0; mill.BuildupComplete = true;
            var pilz = Spawn(interp, ctx, "Pilz", "35 30 8");   // ripe (no lock set)
            var g1 = Spawn(interp, ctx, "G", "30 30 8"); g1.Owner = 0;
            var g2 = Spawn(interp, ctx, "G", "29 30 8"); g2.Owner = 0;
            interp.Eval($"set_worktime {g1.Handle} 0 12");
            interp.Eval($"set_worktime {g2.Handle} 0 12");
            return (interp, ctx, mill, pilz, g1, g2);
        }

        [Test]
        public void Harvest_NoScheduledProduction_NoJob()
        {
            var (_, ctx, mill, _, g1, _) = HarvestWorld();
            // No active slot anywhere → wanted == 0 → no demand → no harvest at game start
            // (FUN_005f0190:65 only reaches the emitters while the demand counter is non-zero).
            Assert.AreEqual(0, mill.ProdSlots.Count, "precondition: nothing scheduled");
            ctx.ProdDispatcher!.OnGnomeIdle(g1);
            ctx.ProdDispatcher!.SweepNow(0);
            Assert.IsFalse(HasEvent(g1, "evt_autoprod_harvest"),
                "idle gnomes must NOT fell mushrooms while no production is scheduled");
        }

        [Test]
        public void Harvest_StockCoversDemand_NoJob()
        {
            var (interp, ctx, mill, _, g1, _) = HarvestWorld();
            mill.ProdSlots["Grillpilz"] = 1;                       // one wanting slot
            Spawn(interp, ctx, "Pilzhut", "41 30 8").Owner = 0;    // produce already available
            // wanted 1 − available 1 ⇒ no deficit ⇒ no job (FUN_005f4220:247-253).
            ctx.ProdDispatcher!.OnGnomeIdle(g1);
            ctx.ProdDispatcher!.SweepNow(0);
            Assert.IsFalse(HasEvent(g1, "evt_autoprod_harvest"),
                "available Pilzhut stock must satisfy the demand — no felling run");
        }

        // The missing class is Pilzhut, but a stray Pilzstamm lies in the world — the old lumped
        // Pilzhut+Pilzstamm stock count read it as "produce available" and never chopped. The
        // per-class law (FUN_00434e60:447-500; even the MP emitter keeps the classes separate,
        // FUN_005f4220:247-253) must still dispatch.
        [Test]
        public void Harvest_WrongClassStock_StillChops()
        {
            var (interp, ctx, mill, pilz, g1, _) = HarvestWorld();
            mill.ProdSlots["Grillpilz"] = 1;                       // recipe wants Pilzhut
            Spawn(interp, ctx, "Pilzstamm", "50 30 8").Owner = 0;  // wrong-class stock, far away
            ctx.ProdDispatcher!.OnGnomeIdle(g1);
            ctx.ProdDispatcher!.SweepNow(0);
            Assert.IsTrue(HasEvent(g1, "evt_autoprod_harvest"),
                "a stray item of ANOTHER class must not satisfy the Pilzhut deficit — the chop "
                + "dispatches (missing Pilzhut → produce map → Pilz)");
            Assert.IsTrue(ctx.Entities.ContainsKey(pilz.Handle));
        }

        [Test]
        public void Harvest_OneDispatchPerCadence_SecondGnomeWaits()
        {
            var (_, ctx, mill, pilz, g1, g2) = HarvestWorld();
            mill.ProdSlots["Grillpilz"] = 1;
            var disp = ctx.ProdDispatcher!;

            // First idle gnome is dispatched onto the ripe Pilz…
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsTrue(HasEvent(g1, "evt_autoprod_harvest"), "the one open harvest job must dispatch");

            // …and the second one must wait — one emission per 5 s pass + 10 s post-emit
            // (FUN_005f1500:53/:159) — not run to the same mushrooms.
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.IsFalse(HasEvent(g2, "evt_autoprod_harvest"),
                "a second harvester inside the emit cadence — the whole camp would rush the mushrooms");

            // A failed dispatch retries on the native 20-tick job-reopen schedule instead of
            // burning the full 15 s (the native job persists in the planner array; only new
            // emissions pay the cadence).
            disp.ReleaseGnome(g1);
            for (int i = 0; i < JobReopen + 1; i++) disp.DecayCooldowns();
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.IsTrue(HasEvent(g2, "evt_autoprod_harvest"),
                "after the 20-tick reopen the (still-undone) job must dispatch to the next gnome");
            Assert.IsTrue(ctx.Entities.ContainsKey(pilz.Handle), "the target Pilz is still standing");
        }

        const int JobReopen = 20;   // FUN_0043a0c0 target+0x3e8 = 0x14 logic ticks

        // ------------------------------------------------------------------
        // Worktime — the spawn default is no window (regression: everyone "at work" at game start)
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Hat chain — prod_change_muetze end-to-end (regression: work hats never worn)
        // ------------------------------------------------------------------

        // The work hat is plain unchanged Tcl: the order handler calls `prod_change_muetze wood`
        // (evt_task_harvest_proc z_events.tcl:923) which queues muetze_ab/muetze_auf anims +
        // create_muetze into the task list (z_work_common.tcl:685-760), pumped by the task state.
        // Chain prerequisites verified here: is_human (z_methods.tcl:200 — get_objownertype is
        // unknown → the test harness's "0" handler → ==2 false → human), get_nameofmuetze_proc's
        // category table (z_work_common.tcl:799-825: harvest/wood → Dummy_Muetze_holz_<a|b>),
        // create_muetze's `new` + link_obj this 4 (z_work_common.tcl:767-777). The underlying
        // defect was upstream: the dispatch storm re-raised evt_* handlers whose tops run
        // `tasklist_clear this` (z_events.tcl:909-911), wiping the queued hat anims before the
        // pump ever ran them — fixed by the announce/cadence throttles above.
        [Test, Explicit("Loads ~300 real classes from the data dir; run on demand")]
        public void HatChain_HarvestOrder_WearsTheWoodHat()
        {
            Assume.That(System.IO.Directory.Exists(DataRoot), "original data dir not present");
            var (interp, ctx, sim) = Host();
            ctx.DataRoot = DataRoot;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);

            var g = Spawn(interp, ctx, "Zwerg", "30 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            var p = Spawn(interp, ctx, "Pilz", "31 30 8");
            interp.Eval($"set_lock {p.Handle} 0");           // ripe
            for (int i = 0; i < 60; i++) sim.Step(0.1);      // boot window: the t=3 init (gender/is_human) runs

            ctx.This = g;
            interp.Eval($"set_event this evt_task_harvest -subject1 {p.Handle}");
            ctx.This = null;

            string hat = "";
            for (int i = 0; i < 200; i++)
            {
                sim.Step(0.1);
                if (g.VarsScope.Vars.TryGetValue("current_muetze_name", out var m)
                    && m.StartsWith("Dummy_Muetze_holz_")) { hat = m; break; }
            }
            TestContext.WriteLine($"final hat: '{hat}' state={g.CurrentState} tasks={g.TaskList.Count}");
            Assert.IsTrue(hat.StartsWith("Dummy_Muetze_holz_"),
                "the harvest order must put on the wood-category work hat " +
                "(prod_change_muetze wood → create_muetze Dummy_Muetze_holz_<a|b>)");
        }

        // ------------------------------------------------------------------
        // Deferred crate order — the full chain with the real classes (regression: the gnome
        // walked to the build spot without the crate and the order died at
        // prod_buildup_check "dont have it"). The SP planner sends the gnome to fetch the
        // crate first (kind-1 step plan, FUN_00439990: muetze → walk → pickup → walk →
        // unpack), then prod_autounpack carries it to the order pos and prod_buildup_new /
        // prod_buildup_rep run the bau stages to unpackfrombox (set_boxed 0).
        // ------------------------------------------------------------------
        [Test, Explicit("Loads ~300 real classes from the data dir; run on demand")]
        public void DeferredCrateOrder_EndToEnd_FetchesCrate_ThenBuildsAtOrderPos()
        {
            Assume.That(System.IO.Directory.Exists(DataRoot), "original data dir not present");
            var (interp, ctx, sim) = Host();
            ctx.DataRoot = DataRoot;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            Assert.IsTrue(ctx.Classes.ContainsKey("Feuerstelle"), "Feuerstelle class not loaded");
            ctx.LocalPlayer = 0;
            // No terrain: the placement seat and the walk legs run in their headless
            // pass-through modes (TrySeatOnFloor without terrain accepts the pick; movement
            // degrades to wait actions) — the test exercises the dispatch+Tcl chain, not nav.

            // Headless harness law (the BuildingsTests pattern): walks degrade to instant arrivals
            // without moving the body, and take_item is distance-gated (>2 → gnome_failed_work,
            // z_procs.tcl:640+) — so everything must stand ≤ 0.7 apart. The chain (dispatch →
            // muetze → pickup → unpack → bau stages) is what this test verifies, not nav.
            var g = Spawn(interp, ctx, "Zwerg", "30 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            var crate = Spawn(interp, ctx, "Feuerstelle", "30.4 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            for (int i = 0; i < 60; i++) sim.Step(0.1);          // boot window (t=3 init)

            // The deferred order: crate window → build → ghost beside the crate → confirm
            // (FUN_00531310:316-331 → FUN_005ee250 record write).
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.OrderBuild(crate.Handle), "the crate window must enter placement mode");
            pc.UpdateCursor(new Vector3(30.5f, 30f, 8f));
            Assert.IsTrue(pc.Valid, "open ground must be a valid spot");
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(crate.UnpackRequested, "confirm must write the deferred order");

            var orderPos = crate.OrderPos;
            bool fetched = false, placed = false, unboxed = false;
            var disp = ctx.ProdDispatcher!;
            var trace = new System.Text.StringBuilder();
            for (int i = 0; i < 6000; i++)
            {
                sim.Step(0.1);
                if (!fetched && crate.ContainedIn == g.Handle) { fetched = true; trace.Append($" fetched@{i}"); }
                if (fetched && !placed && crate.ContainedIn < 0)
                {
                    placed = true;
                    trace.Append($" placed@{i}:{crate.Position}");
                }
                if (placed && !unboxed && interp.Eval($"get_boxed {crate.Handle}").Trim() == "0")
                { unboxed = true; trace.Append($" unboxed@{i}"); }
                if (unboxed && crate.BuildupComplete) { trace.Append($" built@{i}"); break; }
                if (i % 200 == 0)
                {
                    g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                    g.VarsScope.Vars.TryGetValue("current_muetze_name", out var hat);
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} wt={wt} hat={hat} " +
                        $"pooled={disp.IsPooled(g.Handle, 0)} plan={disp.HasPlan(g.Handle)} " +
                        $"cd={g.WorkBusyCooldown} unpackReq={crate.UnpackRequested} " +
                        $"claim={crate.CurrentWorkerHandle} contained={crate.ContainedIn} " +
                        $"ws={g.WorkStart} wl={g.WorkLen} tasks={g.TaskList.Count} ev={g.PendingEvents.Count}");
                }
            }
            string bstep = "?";
            try { bstep = interp.Eval($"call_method {crate.Handle} get_buildupstep").Trim(); } catch { }
            TestContext.WriteLine($"trace:{trace} state={g.CurrentState} buildupstep={bstep} " +
                $"pos={crate.Position} events={g.PendingEvents.Count}");

            Assert.IsTrue(fetched,
                "the gnome must FETCH the crate first (the evt_autoprod_pickup step) — walking to " +
                "the spot empty-handed cancels the order at prod_buildup_check (z_work_common.tcl:355-357)");
            Assert.IsTrue(placed, "the crate must be put down out of the inventory");
            Assert.Less((new Vector2(crate.Position.x, crate.Position.y)
                - new Vector2(orderPos.x, orderPos.y)).magnitude, 2f,
                "the building must stand at the ORDER pos (prod_buildup_new set_pos, z_work_common.tcl:408)");
            Assert.IsTrue(unboxed,
                "the bau chain must unbox the crate (prod_buildup_new, z_work_common.tcl:408)");
            Assert.IsTrue(crate.BuildupComplete,
                "the bau stages must run to completion — unpackfrombox → set_buildupstate 1 " +
                "(prod_buildup_rep, z_work_common.tcl:479-481 + genericprod.tcl:812-813)");
        }

        [Test]
        public void Worktime_SpawnDefault_NoWindow_NotDispatchable()
        {
            var (interp, ctx, mill, _, g1, _) = HarvestWorld();
            mill.ProdSlots["Grillpilz"] = 1;                       // an open job exists…
            var fresh = Spawn(interp, ctx, "G", "31 30 8");
            fresh.Owner = 0;                                       // …but NO set_worktime ran
            // CObj ctor zeroes the window (FUN_005c83f0:130-132: +0x354 start = 0, +0x358
            // duration = 0) and the per-tick gate needs duration > 0 (FUN_005cb760:35) — a fresh
            // gnome is in sparetime until auto_choose_workingtime / set_worktime assigns a window.
            Assert.AreEqual(0f, fresh.WorkLen, 1e-6f, "spawn default duration must be 0 (no window)");
            Assert.Greater(double.Parse(interp.Eval($"get_remaining_sparetime {fresh.Handle}").Trim(),
                System.Globalization.CultureInfo.InvariantCulture), 0.0,
                "a windowless gnome must read as sparetime, never worktime");
            ctx.ProdDispatcher!.OnGnomeIdle(fresh);
            ctx.ProdDispatcher!.SweepNow(0);
            Assert.IsFalse(HasEvent(fresh, "evt_autoprod_harvest"),
                "the dispatcher must not assign work to a gnome with no work window");
        }
    }
}
