#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;

    // dispatcher fixes — three native laws, decompile-cited:
    //
    // 1) HAUL SOURCES ARE FOG-GATED. The SP planner's source-item resolver (kind-4 haul chain
    // FUN_00436d40 → FUN_004319b0:63 → FUN_004317a0:73) accepts a candidate only while
    // `GetMaxFow(item, owner) > 0x8c` — (near-)currently seen. The demand scan's
    // availability REGISTRATION is looser: `GetMaxFow > 0` (FUN_00434e60:114 — explored
    // gray suffices; same law as the Tcl obj_find_next family, FUN_006a2000:212-213).
    // The port fetched construction/production materials out of BLACK fog.
    //
    // 2) THE RELEASE PATH KILLS REQUEST BYTES. FUN_0043a0c0 (every plan teardown — the
    // worker's next TRUE idle report via CProdPlanner::GnomeIdle FUN_00434a80 /
    // MaintenanceTick, and the gnome_failed_work/gnome_stopped_work handler FUN_00676500)
    // zeroes the claim rec+0x24, the progress pair, AND the request bytes +0x28 unpack /
    // +0x29 PACK / +0x2a buildup (:33-40/:55-61), plus stamps the target's reopen
    // +0x3e8 = 0x14. The PACK byte deliberately survives DISPATCH (prod_builddown re-reads
    // it mid-flight, z_work_common.tcl:443-446) — so without the release-path clear a
    // failed/interrupted packer left the order ARMED and the collector marched the next
    // gnome onto the crate every sweep ("pack order sends everyone").
    //
    // 3) INVENTIONS BIND rec+0x48 AT THE EMIT, NOT AT COLLECTION. The production section's
    // slot pick requires FUN_005ed620 = the slot item's tech-registry owner attrib ≠ 0
    // (invented; FUN_00436750:100-101), so the collection-time bind at:146-158 is
    // unreachable for an invention order; the kind-2 job stamps +0x48 only at the workat
    // emit (FUN_0043a8a0:89, plain class — the Bp marker rides -text1 only). The port's
    // collection-time bind painted the blue "in progress" bar over a merely queued
    // invention while no gnome had work time.
    public sealed class ProdFowPackInventTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host()
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
            return (interp, ctx, sim);
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

        static int CountEvents(DigglesTclEntity e, string name)
        {
            int n = 0;
            foreach (var ev in e.PendingEvents) if (ev.Name == name) n++;
            return n;
        }

        static EntityEvent? FirstEvent(DigglesTclEntity e, string name)
        {
            foreach (var ev in e.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }

        // ------------------------------------------------------------------
        // 1. HAUL SOURCE FOG GATE — black blocks, gray registers-but-not-resolves,
        // visible dispatches (FUN_004317a0:73 vs FUN_00434e60:114)
        // ------------------------------------------------------------------

        [Test]
        public void HaulSource_FogGate_BlackAndGrayBlock_VisibleDispatches()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            ctx.FogOfWar = new FogOfWarState(64, 64, (x, y) => 0);

            var b = Building(interp, ctx, "10 10 8");                 // needs one Mat
            var mat = Spawn(interp, ctx, "Mat", "40 10 8");
            mat.Owner = -1;                                           // neutral loose material
            var g = Gnome(interp, ctx, "11 10 8");
            var disp = ctx.ProdDispatcher!;
            disp.OnGnomeIdle(g);

            // BLACK fog (byte 0): the item is invisible to the resolver AND to the
            // availability surface.
            disp.SweepNow(0);
            Assert.IsFalse(disp.HasPlan(g.Handle),
                "an item in BLACK fog must never source a kind-4 haul (FUN_004317a0:73)");
            Assert.IsNull(FirstEvent(g, "evt_change_muetze"), "no carrier trip may open");
            Assert.IsFalse(disp.SlotBuildable(b, "Prod"),
                "unexplored stock must not count as available (FUN_00434e60:114)");

            // GRAY (explored, byte 1): registers as world stock, but the resolver's
            // 0x8c threshold still rejects it.
            ctx.FogOfWar.RemoveBlackFog(0, 35, 5, 45, 15);
            ctx.FogOfWar.EndUpdate();   // bump the GetMaxFow cache version
            disp.SweepNow(0);
            Assert.IsFalse(disp.HasPlan(g.Handle),
                "gray-explored (byte 1) must NOT source a haul — FUN_004317a0:73 needs > 0x8c");
            Assert.IsTrue(disp.SlotBuildable(b, "Prod"),
                "explored stock DOES count for the availability surface (GetMaxFow > 0, FUN_00434e60:114)");

            // VISIBLE (byte 0xFF): the fetch dispatches.
            ctx.FogOfWar.RemoveFogBoxNoCollision(40 * 8, 10 * 8, 40, 40, 0);
            ctx.FogOfWar.EndUpdate();
            disp.SweepNow(0);
            Assert.IsTrue(disp.HasPlan(g.Handle), "a visible item must dispatch the kind-4 fetch");
            Assert.IsNotNull(FirstEvent(g, "evt_change_muetze"), "the transport muetze step must open the trip");
        }

        // A fog-hidden material does NOT cover the deficit: the kind-6 chop escalation fires
        // exactly when the resolver finds nothing (its own gate, FUN_004317a0:73), while the
        // RESOURCE pick itself needs only exploration (registration law, FUN_00434e60:114).
        [Test]
        public void HarvestEscalation_FogHiddenMaterialDoesNotCover_UnexploredResourceNotChopped()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            ctx.FogOfWar = new FogOfWarState(64, 64, (x, y) => 0);
            interp.Eval("def_class B2 none production 0 {} {\n"
                        + "method prod_items {} {return {Grillpilz}}\n"
                        + "method prod_item_materials {item} {return {Pilzhut}}\n}");
            interp.Eval("def_class Pilzhut none item 0 {} {}");
            interp.Eval("def_class Pilz none resource 0 {} {}");
            interp.Eval("def_attrib BpGrillpilz 0 1 1");

            var b = Spawn(interp, ctx, "B2", "10 10 8");
            b.Owner = 0; b.BuildupComplete = true;
            b.ProdSlots["Grillpilz"] = 1;
            var hut = Spawn(interp, ctx, "Pilzhut", "40 10 8"); hut.Owner = -1;   // in BLACK fog
            var pilz = Spawn(interp, ctx, "Pilz", "14 10 8"); pilz.Owner = -1;    // in BLACK fog
            var g = Gnome(interp, ctx, "11 10 8");
            var disp = ctx.ProdDispatcher!;
            disp.OnGnomeIdle(g);

            // Resource unexplored → no chop at all (FUN_00434e60:114 registration gate).
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g, "evt_autoprod_harvest"),
                "an UNEXPLORED resource must never be chopped (FUN_00434e60:114)");

            // GRAY-explored resource: registration passes (FUN_00434e60:114), but the FINAL
            // resolve demands current visibility GetMaxFow>0x8c (FUN_004315a0:56) — the
            // user-reported S13-005 law: never chop into gray fog. (This retires the old
            // "gray suffices" contract, which conflated registration with final resolve.)
            ctx.FogOfWar.RemoveBlackFog(0, 12, 8, 16, 12);
            ctx.FogOfWar.EndUpdate();
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g, "evt_autoprod_harvest"),
                "a gray-explored resource must not be chopped (final resolve GetMaxFow>0x8c, "
                + "FUN_004315a0:56)");

            // VISIBLE resource (reveal around the pilz only; the Pilzhut at 40,10 stays hidden):
            // the chop fires and the fog-hidden material never covered the deficit.
            ctx.FogOfWar.BeginUpdate(0);
            ctx.FogOfWar.RemoveFog(14 * 8, 10 * 8, 24, 24, 0, 0f, 1f);
            ctx.FogOfWar.EndUpdate();
            disp.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_harvest");
            Assert.IsNotNull(ev,
                "a material hidden in fog does not cover the deficit — the kind-6 chop must fire "
                + "(the resolver's exact-failure law, FUN_004317a0:71-73)");
            Assert.AreEqual(pilz.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
        }

        // ------------------------------------------------------------------
        // 2. PACK RELEASE — ⚠ DELIBERATE PORT DEVIATION: the pack
        // byte SURVIVES an executor-loss release (
        //) — the native FUN_0043a0c0:35 would cancel. The order re-collects
        // and the NEXT free gnome takes over, one at a time (claim + step plan). The byte
        // dies ONLY at boxing (FUN_0067dc80:157-161) or the explicit Cancel button.
        // ------------------------------------------------------------------

        [Test]
        public void Pack_DivertedWorker_OrderSurvives_NextGnomeTakesOver()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8", slot: 0);
            ctx.Placement!.OrderPack(b.Handle);
            Assert.IsTrue(b.PackRequested);

            var disp = ctx.ProdDispatcher!;
            var g1 = Gnome(interp, ctx, "32 30 8");
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            // The kind-0 plan opens with the MUETZE step (FUN_00439760:58 — the "pack" category
            // → the service hat, z_work_common.tcl:822); the pack event follows on the advance.
            var mu = FirstEvent(g1, "evt_change_muetze");
            Assert.IsNotNull(mu, "the pack plan opens with the muetze step");
            Assert.AreEqual("pack", mu!.Value.Payload!["-text1"]);
            Assert.IsTrue(disp.HasPlan(g1.Handle), "the kind-0 step plan is live");
            Assert.IsTrue(b.PackRequested, "the byte survives DISPATCH (z_work_common.tcl:444 gate)");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle);

            // Mid-chain divert — a user order's gnome_failed_work cleanup (z_events.tcl:346…)
            // releases the claim and the plan, but the ORDER STAYS ARMED (the deviation).
            interp.Eval($"gnome_failed_work {g1.Handle}");
            Assert.IsTrue(b.PackRequested,
                "DEVIATION: the pack byte survives executor loss — the order must be picked up "
                + "by the next free gnome (user directive 13.07)");
            Assert.AreEqual(-1, b.CurrentWorkerHandle, "the claim releases (FUN_0043a0c0:31)");
            Assert.IsFalse(disp.HasPlan(g1.Handle), "the plan dies with the release");

            // The next free gnome takes over within the reopen window (+0x3e8 = 0x14 spacing);
            // the diverted one is breathing (FUN_00438210:35-38) and must not be re-picked.
            var g2 = Gnome(interp, ctx, "31 30 8");
            bool tookOver = false;
            for (int i = 0; i < 40 && !tookOver; i++)
            {
                disp.DecayCooldowns();
                disp.OnGnomeIdle(g2);
                disp.SweepNow(0);
                tookOver = disp.HasPlan(g2.Handle);
            }
            Assert.IsTrue(tookOver, "another free gnome must take the surviving pack order");
            var mu2 = FirstEvent(g2, "evt_change_muetze");
            Assert.IsNotNull(mu2, "the takeover re-runs the full plan — hat swap included");
            Assert.AreEqual("pack", mu2!.Value.Payload!["-text1"]);
            Assert.AreEqual(g2.Handle, b.CurrentWorkerHandle, "the takeover re-claims the place");
            Assert.AreEqual(0, CountEvents(g1, "evt_autoprod_pack"),
                "the diverted gnome never got the pack step and is not re-dispatched (breather)");

            // ONE at a time: a third gnome gets nothing while g2 holds the claim/plan.
            var g3 = Gnome(interp, ctx, "30 30 8");
            for (int i = 0; i < 30; i++) { disp.DecayCooldowns(); disp.OnGnomeIdle(g3); disp.SweepNow(0); }
            Assert.AreEqual(0, CountEvents(g3, "evt_change_muetze"),
                "single-assignee holds — the 07-12 'everyone packs' storm must not return");
        }

        [Test]
        public void Pack_WorkerIdleReportsWalkThePlan_ThenAbandonReopens()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8", slot: 0);
            ctx.Placement!.OrderPack(b.Handle);

            var disp = ctx.ProdDispatcher!;
            var g1 = Gnome(interp, ctx, "32 30 8");
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g1, "evt_change_muetze"), "muetze step first (FUN_00439760:58)");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle);

            // Idle report 1 = the plan ADVANCE (MaintenanceTick FUN_00433a70:355-407): the pack
            // step emits; the byte and the claim BOTH survive — a mid-plan idle bounce must not
            // cancel the order (the pre-13.07 shape had no plan and tore down here).
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            Assert.IsNotNull(FirstEvent(g1, "evt_autoprod_pack"), "the advance emits the pack step");
            Assert.IsTrue(b.PackRequested, "the byte survives the step advance");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle, "the claim survives the step advance");

            // Idle report 2: the plan is spent → it drops (trip handed to the Tcl); the claim
            // still holds through the working leg.
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            Assert.IsFalse(disp.HasPlan(g1.Handle), "the spent plan drops");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle, "the claim outlives the plan (rec+0x24)");

            // Idle report 3 = a TRUE planless idle: the claim releases (FUN_0043a0c0:31) but the
            // ORDER SURVIVES (the 13.07 deviation) — an abandoned pack RE-OPENS instead of
            // cancelling.
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            Assert.AreEqual(-1, b.CurrentWorkerHandle, "the planless idle report releases the claim");
            Assert.IsTrue(b.PackRequested,
                "DEVIATION: the abandon keeps the order armed — it re-opens for the pool");

            // The re-opened order re-dispatches (g1 is back in the pool and not breathing —
            // a plain idle release carries no failure embargo).
            bool redispatched = false;
            for (int i = 0; i < 40 && !redispatched; i++)
            {
                disp.DecayCooldowns();
                disp.OnGnomeIdle(g1);
                disp.SweepNow(0);
                redispatched = disp.HasPlan(g1.Handle);
            }
            Assert.IsTrue(redispatched, "the surviving order re-dispatches on a later sweep");
            Assert.AreEqual(2, CountEvents(g1, "evt_change_muetze"),
                "the retry re-runs the full plan (fresh muetze step)");

            // The explicit CANCEL is now the only user-side terminator: byte drops, nobody comes.
            interp.Eval($"gnome_failed_work {g1.Handle}");   // release the retry claim/plan
            ctx.Placement!.CancelPack(b.Handle);
            Assert.IsFalse(b.PackRequested, "Cancel drops the byte (the native cancel act)");
            var g2 = Gnome(interp, ctx, "31 30 8");
            for (int i = 0; i < 60; i++) { disp.DecayCooldowns(); disp.OnGnomeIdle(g2); disp.SweepNow(0); }
            Assert.AreEqual(0, CountEvents(g2, "evt_change_muetze"), "no dispatch off a cancelled order");
        }

        // ------------------------------------------------------------------
        // 3. INVENTION PROGRESS BINDING — no bind while queued; bind at the emit
        // (FUN_005ed620 / FUN_00436750:100 vs FUN_0043a8a0:89)
        // ------------------------------------------------------------------

        [Test]
        public void Invention_QueuedOrderDoesNotBind_EmitBindsPlainClass()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            interp.Eval("set_owner_attrib 0 BpProd 0");   // NOT invented
            var b = Building(interp, ctx, "20 20 8");      // ordered slot Prod = 1
            var disp = ctx.ProdDispatcher!;

            // The only gnome is OUTSIDE its work window → the job may be collected but never
            // assigned — exactly the player's "no gnome has work time" scenario.
            var g = Gnome(interp, ctx, "21 20 8");
            interp.Eval($"set_worktime {g.Handle} 6 1");   // GameTime 0 ⇒ day phase 0 ∉ [6,7)
            disp.OnGnomeIdle(g);
            for (int i = 0; i < 5; i++) disp.SweepNow(0);
            Assert.AreEqual("", b.CurrentProdItem,
                "a merely QUEUED invention must not bind rec+0x48 — the production section's slot "
                + "pick is gated on FUN_005ed620 (invented), so the :146-158 bind is unreachable; "
                + "the premature bind painted the blue in-progress bar with no worker");

            // The window opens → the kind-2 job dispatches → the EMIT stamps the PLAIN class.
            interp.Eval($"set_worktime {g.Handle} 0 12");
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_workat");
            Assert.IsNotNull(ev, "the invention job must dispatch once a qualified gnome is in its window");
            Assert.AreEqual("BpProd", ev!.Value.Payload!["-text1"], "the Bp marker rides -text1 (FUN_0043a8a0:96)");
            Assert.AreEqual("Prod", b.CurrentProdItem,
                "the workat emit stamps rec+0x48 with the PLAIN class (FUN_0043a8a0:89)");
        }

        // Regression guard for the asymmetry: an INVENTED ordered slot still binds at
        // COLLECTION (FUN_00436750:146-158) — the blue "order pending" bar for regular
        // production exists from the first service pass even with nobody dispatchable.
        [Test]
        public void Production_InventedOrderBindsAtCollection()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "20 20 8");      // BpProd default = invented
            var mat = Spawn(interp, ctx, "Mat", "50 50 8");
            interp.Eval($"inv_add {b.Handle} {mat.Handle}");   // recipe satisfied in-inventory

            // No dispatchable gnome at all — collection alone must bind.
            ctx.ProdDispatcher!.SweepNow(0);
            Assert.AreEqual("Prod", b.CurrentProdItem,
                "an invented ordered slot binds rec+0x48 at collection (FUN_00436750:146-158)");
        }
    }
}
