#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.UI;
    using NUnit.Framework;
    using UnityEngine;

    // items 18/17/19 — the production FREEZE family + bar/RMB laws.
    //
    // GROUND TRUTH (decompile):
    // • CProdPlanner::MaintenanceTick FUN_00433a70:110-131 re-validates EVERY tracked gnome's
    // active plan each pass: FUN_0043a050 finds the plan, FUN_0043a290 checks that the
    // CURRENT step's referenced object handles still resolve to live objects (step+0x24 via
    // FUN_00431e50, step+0x20 via FUN_00431de0 — FUN_0043a290:28-29), and a failed check
    // tears the plan down ON THE SPOT: FUN_0043d4b0 (plan removal) + FUN_0043a0c0 (claim +
    // request bytes + progress pair release, target +0x3e8 = 0x14 reopen stamp).
    // Assignments therefore do NOT persist unconditionally across passes — every pass
    // re-earns them.
    // • The dwarf DEATH chain releases through the same path: proc destroy runs stop_prod +
    // `gnome_failed_work this` BEFORE `del this` (z_procs.tcl:1884/:1895). Any port-side
    // delete that skips the Tcl destroy (direct `del`, script error mid-chain) used to leak
    // the plan FOREVER: TryGetHaulFor kept the blue delivery bar, PlanTargetsItem locked the
    // source item, the destination stayed claimed — the 13.07 freeze (blue bar, nobody
    // hauling, only rebuilding recovered).
    // • Bar law (window draw FUN_00520620:483-565): cell = bound rec+0x48 item with count ≥ 1
    // (:483/:491); BLUE solid while the pair is invalid or the record hauls
    // (`+0x50 < 1 || +0x4c < 0 || +0x1c == 3`,:492-493, fill forced 0:522-523); GREEN
    // cur*100/max otherwise (:548-553). For an INVENTION the Tcl feed freezes the pair at
    // 1/2 for the whole invent anim (prod_getitemtask Bp branch, genericprod.tcl:711-717)
    // — a meaningless half-fill; player-confirmed original look (05.07 + 13.07 reports)
    // is the full solid blue, so the port surfaces a live invention as full-blue
    // (documented interpretation in BottomMenuController).
    public sealed class ProdFreezeReleaseTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class B none production 0 {} {\n"
                        + "method prod_items {} {return {Prod}}\n"
                        + "method prod_item_materials {item} {return {Mat}}\n}");
            interp.Eval("def_class Mat none item 0 {} {}");
            interp.Eval("def_attrib BpProd 0 1 1");   // pre-invented default (genericprod.tcl:151)
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static DigglesTclEntity Gnome(TclInterpreter interp, DigglesTclContext ctx, string pos)
        {
            var g = Spawn(interp, ctx, "G", pos);
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            return g;
        }

        static DigglesTclEntity Building(TclInterpreter interp, DigglesTclContext ctx, string pos, int slot = 1)
        {
            var b = Spawn(interp, ctx, "B", pos);
            b.Owner = 0;
            b.BuildupComplete = true;
            if (slot > 0) b.ProdSlots["Prod"] = slot;
            return b;
        }

        static EntityEvent? FirstEvent(DigglesTclEntity e, string name)
        {
            foreach (var ev in e.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }

        // The controller implements VContainer.Unity.ITickable explicitly; the test asmdef has
        // no VContainer reference, so drive the tick via reflection (ProdMaterialHaulTests law).
        static void Tick(BottomMenuController c)
            => c.GetType().GetMethod("VContainer.Unity.ITickable.Tick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(c, null);

        // ------------------------------------------------------------------
        // 1. DEAD HAULER (item 18 case 1): the carrier dies mid-trip WITHOUT the Tcl death
        // cleanup (direct `del` — the leak path). The next maintenance pass must tear the
        // plan down (FUN_00433a70:110-131), clear the delivery-bar surface and release the
        // claims so the job re-dispatches to another gnome.
        // ------------------------------------------------------------------

        [Test]
        public void DeadHauler_PlanTornDown_BarClears_JobRedispatches()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var mat = Spawn(interp, ctx, "Mat", "45 30 8");   // far — a real carrier trip
            var g1 = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsTrue(disp.HasPlan(g1.Handle), "precondition: g1 opened the kind-4 haul plan");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle, "precondition: the dest is claimed");
            Assert.IsTrue(disp.TryGetHaulFor(b.Handle, out _), "precondition: the delivery bar is live");

            // The carrier dies WITHOUT gnome_failed_work (proc destroy skipped — the leak).
            interp.Eval($"del {g1.Handle}");

            // ONE maintenance pass must apply the release law (FUN_0043a290 fail → FUN_0043d4b0
            // + FUN_0043a0c0).
            disp.SweepNow(0);
            Assert.IsFalse(disp.HasPlan(g1.Handle), "the dead gnome's plan must not survive the pass");
            Assert.AreEqual(-1, b.CurrentWorkerHandle, "the claim releases (FUN_0043a0c0:31)");
            Assert.IsFalse(disp.TryGetHaulFor(b.Handle, out _),
                "the delivery-bar surface must clear — the 13.07 'blue bar forever, nobody hauling'");

            // …and the job re-dispatches to a second gnome after the reopen cooldown
            // (target +0x3e8 = 0x14 = 20 logic ticks, FUN_0043a0c0:30).
            var g2 = Gnome(interp, ctx, "31 30 8");
            bool redispatched = false;
            for (int i = 0; i < 60 && !redispatched; i++)
            {
                disp.DecayCooldowns();
                disp.OnGnomeIdle(g2);
                disp.SweepNow(0);
                redispatched = disp.HasPlan(g2.Handle);
            }
            Assert.IsTrue(redispatched, "the released job must re-dispatch within the reopen window");
            var mu = FirstEvent(g2, "evt_change_muetze");
            Assert.IsNotNull(mu, "g2 opens the fresh carrier trip");
            Assert.AreEqual("transport", mu!.Value.Payload!["-text1"]);
        }

        // ------------------------------------------------------------------
        // 2. DEAD WORKER holding a workat claim (item 18 case 2): the claimed cook dies
        // without cleanup → the claim + progress pair must scrub so the place re-opens
        // (FUN_0043a0c0's by-gnome loop clears EVERY place claimed by the released gnome,
        //:49-62) and the order re-dispatches.
        // ------------------------------------------------------------------

        [Test]
        public void DeadWorker_ClaimScrubbed_WorkatRedispatches()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var mat = Spawn(interp, ctx, "Mat", "33 30 8");
            interp.Eval($"inv_add {b.Handle} {mat.Handle}");   // recipe complete → kind-3 workat
            var g1 = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g1, "evt_autoprod_workat"), "precondition: g1 got the workat");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle);
            interp.Eval($"info_start_prod {b.Handle} Prod");
            interp.Eval($"info_progress_prod {b.Handle} 1 4");   // mid-production pair

            interp.Eval($"del {g1.Handle}");                     // dies without cleanup

            disp.SweepNow(0);
            Assert.AreEqual(-1, b.CurrentWorkerHandle,
                "a claim held by a no-longer-existing gnome must scrub on the pass");
            Assert.AreEqual(0, b.ProdProgressMax,
                "the progress pair invalidates with the release (FUN_0043a0c0:37-38) — no frozen green");

            var g2 = Gnome(interp, ctx, "31 30 8");
            bool redispatched = false;
            for (int i = 0; i < 60 && !redispatched; i++)
            {
                disp.DecayCooldowns();
                disp.OnGnomeIdle(g2);
                disp.SweepNow(0);
                redispatched = FirstEvent(g2, "evt_autoprod_workat") != null;
            }
            Assert.IsTrue(redispatched, "the order must re-dispatch — 'nobody ever starts' is the freeze");
        }

        // ------------------------------------------------------------------
        // 3. DEAD CRATE mid-unpack-plan: the fetched crate vanishes while the carrier walks
        // (FUN_0043a290's step check fails on the crate handle) → the plan dies on the pass
        // and the gnome is NOT wedged out of future work.
        // ------------------------------------------------------------------

        [Test]
        public void DeadCrate_UnpackPlanTornDownOnPass()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            crate.UnpackRequested = true;
            crate.OrderPos = new Vector3(28f, 20f, 8f);
            var g = Gnome(interp, ctx, "20 20 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsTrue(disp.HasPlan(g.Handle), "precondition: the kind-1 unpack plan opened");

            interp.Eval($"del {crate.Handle}");                  // the crate is destroyed

            disp.SweepNow(0);
            Assert.IsFalse(disp.HasPlan(g.Handle),
                "a plan whose current step references a dead object dies on the pass (FUN_0043a290)");

            // the gnome stays serviceable: a fresh workat order reaches it
            var b = Building(interp, ctx, "21 20 8");
            var mat = Spawn(interp, ctx, "Mat", "21 20 8");
            interp.Eval($"inv_add {b.Handle} {mat.Handle}");
            bool served = false;
            for (int i = 0; i < 60 && !served; i++)
            {
                disp.DecayCooldowns();
                disp.OnGnomeIdle(g);
                disp.SweepNow(0);
                served = FirstEvent(g, "evt_autoprod_workat") != null;
            }
            Assert.IsTrue(served, "the surviving gnome must not be wedged by the dead plan");
        }

        // ------------------------------------------------------------------
        // 4. DIVERTED HAULER (item 18 case 1, the divert flavor): a user order lands mid-trip —
        // the evt_task_* procs call gnome_failed_work at their top (z_events.tcl:346/415/…)
        // → ReleaseGnome. The bar must drop IMMEDIATELY (item 17b) and the job re-dispatch
        // on the reopen schedule. (Regression guard — this path already worked.)
        // ------------------------------------------------------------------

        [Test]
        public void DivertedHauler_BarDropsAndJobRedispatches()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var mat = Spawn(interp, ctx, "Mat", "45 30 8");
            var g1 = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsTrue(disp.TryGetHaulFor(b.Handle, out _), "precondition: delivery live");

            interp.Eval($"gnome_failed_work {g1.Handle}");       // the divert/failure release

            Assert.IsFalse(disp.TryGetHaulFor(b.Handle, out _),
                "a failed delivery must DROP the blue-bar surface at once (FUN_0043a0c0)");
            Assert.AreEqual(-1, b.CurrentWorkerHandle);

            var g2 = Gnome(interp, ctx, "31 30 8");
            bool redispatched = false;
            for (int i = 0; i < 80 && !redispatched; i++)
            {
                disp.DecayCooldowns();
                disp.OnGnomeIdle(g2);
                disp.SweepNow(0);
                redispatched = disp.HasPlan(g2.Handle);
            }
            Assert.IsTrue(redispatched, "the released haul re-dispatches within the reopen window");
        }

        // ------------------------------------------------------------------
        // 5. STALE rec+0x48 BINDING: a cancelled order's binding survived forever (first-wins),
        // so the NEXT order of a DIFFERENT item never got its delivering-blue bar and the
        // place LOOKED dead (05.07 'campfire never produced again until a different order').
        // The bind must fall to the live order once the bound one's count is 0.
        // ------------------------------------------------------------------

        [Test]
        public void StaleCancelledBinding_RebindsToLiveOrderOnHaulCollect()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            b.CurrentProdItem = "Ghost";                          // cancelled order's leftover bind
            var mat = Spawn(interp, ctx, "Mat", "45 30 8");
            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsTrue(disp.HasPlan(g.Handle), "precondition: the haul for Prod dispatched");
            Assert.AreEqual("Prod", b.CurrentProdItem,
                "a dead binding (its slot count is 0) must give way to the live order — else the "
                + "delivering bar never appears for the new order");
        }

        // ------------------------------------------------------------------
        // 6. INVENTION BAR (item 17a): a LIVE invention shows the FULL SOLID BLUE bar, not the
        // frozen half-green the 1/2 Tcl pair would paint (genericprod.tcl:711-717 feeds
        // cur=1 max=2 for the entire invent anim; player-confirmed original look = full blue).
        // ------------------------------------------------------------------

        [Test]
        public void BottomMenu_LiveInvention_FullBlueBar()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            ctx.IsStoryMgr = true;
            interp.Eval("set_owner_attrib 0 BpProd 0");           // NOT invented
            var b = Building(interp, ctx, "33 30 8");
            var g = Gnome(interp, ctx, "32 30 8");                // adjacent = arrived
            var disp = ctx.ProdDispatcher!;
            var ctrl = new BottomMenuController(ctx);
            ctx.SelectedHandle = b.Handle;

            // dispatch the kind-2 invention (requirement-free ⇒ anyone qualifies)
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_workat");
            Assert.IsNotNull(ev, "precondition: the invention job dispatched");
            Assert.AreEqual("BpProd", ev!.Value.Payload!["-text1"]);

            // the worker arrived and the Tcl chain fed the frozen invention pair (1/2)
            interp.Eval($"info_start_prod {b.Handle} BpProd");
            interp.Eval($"info_progress_prod {b.Handle} 1 2");

            Tick(ctrl);
            var cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.IsNotNull(cell, "the product cell exists");
            Assert.AreEqual(BottomMenuProgressKind.Delivering, cell!.ProgressKind,
                "a live INVENTION renders the solid blue bar (user-confirmed original look)");
            Assert.AreEqual(100f, cell.Progress, "full-width — never a partial fill");
        }

        // ------------------------------------------------------------------
        // 6b. PACK FAN-OUT: —, 
        //. Root cause: prod_builddown_rep's set_buildupstep writes run
        // set_buildupanim → `set_buildupstate this [buildup_step > max]` (genericprod.tcl:
        // 812-813); the engine's set_buildupstate(TRUE) carried an UNCITED port-glue
        // `CurrentWorkerHandle = -1` (native SetBuildUpState FUN_0067e1e0:74-86 writes ONLY
        // the flag word) — the pack claim dropped ~0.3 s after dispatch while the pack byte
        // deliberately survives → the collector re-dispatched a fresh packer EVERY sweep.
        // Real-class repro: PackFanoutReproTests (claim 4→-1 at t=1.2 s, bstep=9, byte True).
        // The hat half: the kind-0 plan's FIRST step is the muetze (FUN_00439760:58 — the
        // "pack" category → Dummy_Muetze_dienstleistung, z_work_common.tcl:822); the old
        // direct evt_autoprod_pack emit skipped it.
        // ------------------------------------------------------------------

        [Test]
        public void Pack_BuildupstateWriteMidPack_DoesNotReleaseClaim_NoSecondPacker()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;
            b.BuildupComplete = true;
            ctx.Placement!.OrderPack(b.Handle);
            var disp = ctx.ProdDispatcher!;
            var g1 = Gnome(interp, ctx, "32 30 8");
            var g2 = Gnome(interp, ctx, "31 30 8");

            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            var mu = FirstEvent(g1, "evt_change_muetze");
            Assert.IsNotNull(mu, "the kind-0 plan opens with the muetze step (FUN_00439760:58)");
            Assert.AreEqual("pack", mu!.Value.Payload!["-text1"],
                "the muetze category is 'pack' → the service hat (z_work_common.tcl:822)");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle);

            // The builddown's per-step buildupstate writes (genericprod.tcl:812-813) — the
            // 13.07 trigger. NEITHER direction may touch the claim (FUN_0067e1e0 = flags only).
            interp.Eval($"set_buildupstate {b.Handle} 1");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle,
                "set_buildupstate 1 must NOT release the pack claim — this exact write re-opened "
                + "the record every sweep and marched the whole camp onto the building");
            Assert.IsTrue(b.BuildupComplete, "the flag itself still lands");
            interp.Eval($"set_buildupstate {b.Handle} 0");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle, "the down-step write is inert too");

            // No second packer across many passes while the first one works.
            disp.OnGnomeIdle(g2);
            for (int i = 0; i < 40; i++) { disp.DecayCooldowns(); disp.SweepNow(0); disp.OnGnomeIdle(g2); }
            Assert.AreEqual(0, CountEvents(g2, "evt_change_muetze"), "one packer only (13.07 law)");
            Assert.AreEqual(0, CountEvents(g2, "evt_autoprod_pack"));

            // g1's plan advances to the pack step with the byte still armed.
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            var pk = FirstEvent(g1, "evt_autoprod_pack");
            Assert.IsNotNull(pk, "the advance emits the pack step (FUN_0043a8a0:106-111)");
            Assert.AreEqual(b.Handle.ToString(), pk!.Value.Payload!["-subject1"]);
            Assert.IsTrue(b.PackRequested, "the byte survives through the plan (the Tcl cancel gate reads it)");
        }

        static int CountEvents(DigglesTclEntity e, string name)
        {
            int n = 0;
            foreach (var ev in e.PendingEvents)
                if (ev.Name == name) n++;
            return n;
        }

        // ------------------------------------------------------------------
        // 7. RMB DECREMENT (item 19): right-click on the order icon = −1; reaching 0 cancels
        // the order (the evt_btn_on proddecrement raise runs the genericprod cancel law:
        // break the working cook + free_unneeded_items, genericprod.tcl:33-52) and the bar
        // hides (the:491 count gate).
        // ------------------------------------------------------------------

        [Test]
        public void Click_RightButton_DecrementsAndCancelsAtZero()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            ctx.IsStoryMgr = true;
            var b = Building(interp, ctx, "33 30 8", slot: 2);
            b.CurrentProdItem = "Prod";                           // bound order surface
            var ctrl = new BottomMenuController(ctx);
            ctx.SelectedHandle = b.Handle;

            ctrl.Click("prod:Prod", rightButton: true);
            Assert.AreEqual(1, (int)b.ProdSlots["Prod"], "RMB = −1 (FUN_005242b0:357-474 proddecrement)");

            ctrl.Click("prod:Prod", rightButton: true);
            Assert.AreEqual(0, (int)b.ProdSlots["Prod"], "RMB at 1 → 0 = the order is cancelled");
            var ev = FirstEvent(b, "evt_btn_on");
            Assert.IsNotNull(ev, "every decrement raises evt_btn_on on the building (FUN_005235a0:44)");
            Assert.AreEqual("proddecrement", ev!.Value.Payload!["-text1"]);

            // floor at 0 — a further RMB must not go negative
            ctrl.Click("prod:Prod", rightButton: true);
            Assert.AreEqual(0, (int)b.ProdSlots["Prod"]);

            // the bar hides with the dead order (the:491 count gate)
            Tick(ctrl);
            var cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.IsNotNull(cell);
            Assert.AreEqual(BottomMenuProgressKind.None, cell!.ProgressKind,
                "count 0 ⇒ no bar, even with the binding still on the record");

            // and LMB still increments (the left/right split reaches the controller intact)
            ctrl.Click("prod:Prod", rightButton: false);
            Assert.AreEqual(1, (int)b.ProdSlots["Prod"]);
        }

        // ------------------------------------------------------------------
        // 8. LAGER CATALOG ICONS: the Lager product cells showed NO icons.
        // The catalog entries are the storage-category SERVICE CLASSES with a leading
        // underscore (`_Kisten_einlagern _Nahrung_einlagern …` — tttitems_Lager,
        // techtreetunes.tcl:175; def_class lager.tcl:4-9), but the shipped icons are the
        // same names WITHOUT it (data/Gui/icons/Nahrung_einlagern.tga; no `_*.tga` ships).
        // IconLoader resolves a leading-underscore name through the trimmed file.
        // ------------------------------------------------------------------

        [Test]
        public void IconLoader_LagerStorageCategories_ResolveThroughTrimmedNames()
        {
            var dataRoot = OriginalDataTestFixture.DataRoot;
            var icons = new Diggles.Runtime.IconLoader(dataRoot, sharpen: false);
            // the full tttitems_Lager catalog (techtreetunes.tcl:175) + the 6th shipped category
            foreach (var cls in new[]
                     {
                         "_Kisten_einlagern", "_Nahrung_einlagern", "_Pilze_einlagern",
                         "_Rohmineralien_einlagern", "_Mineralien_einlagern",
                         "_Waffen_Werkzeug_und_Traenke_einlagern",
                     })
            {
                Assert.IsNotNull(icons.Get(cls),
                    $"{cls} must resolve via the trimmed icon file ({cls.TrimStart('_')}.tga)");
                // the product path the BottomMenuView actually takes (Red-variant fallback)
                Assert.IsNotNull(icons.GetProduct(cls, buildable: false),
                    $"GetProduct({cls}) must fall back through the underscore mapping too");
            }
            // ordinary names keep the exact-name convention…
            Assert.IsNotNull(icons.Get("unpack"), "bare action icons still resolve by exact name");
            // …and a genuinely missing name still reads as a clean null
            Assert.IsNull(icons.Get("_DoesNotExist_einlagern"));
        }
    }
}
