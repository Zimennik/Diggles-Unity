#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // S16-005: research discovery is a live maintenance scan, not a workplace-registration
    // snapshot. Native MaintenanceTick calls FUN_00434e60 on every pass
    // (00433a70_CProdPlanner_MaintenanceTick.c:169); that builder walks every place's current
    // slot array and count (00434e60_FUN_00434e60.c:26,269-300), appending a kind-2 job for
    // each positive, qualified uninvented slot (:437-447,580-651). Regular production is a
    // different builder, called later (00433a70:174 -> FUN_00436750).
    //
    // — the kind-2 NEED law (constants byte-read from Diggles.exe.rdata):
    // • need = slot priority × DAT_00737534 = 10.0 (FUN_00434e60:646-647), where the slot
    // priority is the CProd record's +0x14 float ("CObj::Tick Prod" FUN_005cc600:291 →
    // FUN_005edea0:49-85): count==0 ⇒ 0; count>0 ⇒ += DAT_00736fb0 = 0.0005 per 10 Hz
    // logic tick (DAT_00736fac = 0.0003 for the count==10 ∞ order), clamped at
    // DAT_00737138 = 1.0. The queue COUNT never multiplies the need.
    // • user pack/buildup/unpack requests carry the constant need 7.5 = 0x40f00000
    // (FUN_00436440:87/:118), so a queued invention outranks them only after
    // ~1500 unserviced ticks (~150 s; 2500 at the ∞ ramp) — never merely by queue size.
    public sealed class InventionDispatchTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (_, _) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            _ = new DigglesGameSim(ctx);

            interp.Eval("def_class Researcher none gnome 0 {} {}");
            interp.Eval("def_class MissingPlank none item 0 {} {}");
            interp.Eval("def_class Sawmill none production 0 {} {\n"
                        + "method prod_items {} {return {Farm Door}}\n"
                        + "method prod_item_materials {item} {return {MissingPlank}}\n"
                        + "method prod_item_attribs {item} {return {}}\n}");
            interp.Eval("def_attrib BpFarm 0 1 1");
            interp.Eval("def_attrib BpDoor 0 1 1");
            interp.Eval("set_owner_attrib 0 BpFarm 0");
            interp.Eval("set_owner_attrib 0 BpDoor 0");
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(
            TclInterpreter interp, DigglesTclContext ctx, string cls, string pos) =>
            ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static EntityEvent? FirstEvent(DigglesTclEntity gnome, string name, string payloadKey,
            string payloadValue)
        {
            foreach (var pending in gnome.PendingEvents)
                if (pending.Name == name
                    && pending.Payload != null
                    && pending.Payload.TryGetValue(payloadKey, out var value)
                    && value == payloadValue)
                    return pending;
            return null;
        }

        static EntityEvent? FirstWorkat(DigglesTclEntity gnome, string workClass) =>
            FirstEvent(gnome, "evt_autoprod_workat", "-text1", workClass);

        static void RunLogicTicks(ProdDispatcher dispatcher, int ticks)
        {
            for (int k = 0; k < ticks; k++) dispatcher.DecayCooldowns();
        }

        [Test]
        public void QueuedInventions_AreLiveScanned_AndSecondFollowsWithoutRepack()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;

            var sawmill = Spawn(interp, ctx, "Sawmill", "20 20 8");
            sawmill.Owner = 0;
            sawmill.BuildupComplete = true;
            var researcher = Spawn(interp, ctx, "Researcher", "21 20 8");
            researcher.Owner = 0;
            interp.Eval($"set_worktime {researcher.Handle} 0 12");

            var dispatcher = ctx.ProdDispatcher!;
            dispatcher.OnGnomeIdle(researcher);
            dispatcher.SweepNow(0); // establish the already-registered building with no orders
            Assert.IsNull(FirstWorkat(researcher, "BpFarm"));
            Assert.IsNull(FirstWorkat(researcher, "BpDoor"));

            // This is the bottom-menu's actual write surface: an ordinary, plain-class
            // production slot. No pack/unpack, respawn, or workplace registration happens
            // after the write. (Door is queued AFTER the Farm dispatch below: two same-tick
            // orders carry EQUAL native needs — priority × 10, FUN_00434e60:646-647, not the
            // old count+2 — so their relative pick order is not a pinnable contract.)
            interp.Eval($"set_prod_slot_cnt {sawmill.Handle} Farm 2");
            dispatcher.SweepNow(0);

            var farmResearch = FirstWorkat(researcher, "BpFarm");
            Assert.IsNotNull(farmResearch,
                "the next maintenance pass must discover the newly queued Farm invention from "
                + "the live plain slot table, without rebuilding the sawmill");
            Assert.AreEqual(researcher.Handle, sawmill.CurrentWorkerHandle);

            interp.Eval($"set_prod_slot_cnt {sawmill.Handle} Door 1");

            // Model normal completion of the first research. The finite count drops 2 -> 1, so
            // Farm is now an INVENTED regular-production order. Its MissingPlank cannot be hauled;
            // the old combined loop broke on this starved first slot and never offered BpDoor.
            interp.Eval("set_owner_attrib 0 BpFarm 1");
            sawmill.VarsScope.Vars["current_itemtype"] = "Farm";
            interp.Eval($"info_end_prod {sawmill.Handle}");
            Assert.AreEqual(1d, sawmill.ProdSlots["Farm"],
                "research completion leaves the remaining finite Farm order live");

            dispatcher.DecayCooldowns();
            dispatcher.DecayCooldowns();
            interp.Eval($"placelock_rem {researcher.Handle}");
            dispatcher.OnGnomeIdle(researcher);
            Assert.AreEqual(-1, sawmill.CurrentWorkerHandle,
                "the completed worker releases the workplace before the next sweep");

            dispatcher.SweepNow(0);
            Assert.IsNotNull(FirstWorkat(researcher, "BpDoor"),
                "the independent live research scan must skip the starved invented Farm slot and "
                + "dispatch the second queued invention without pack/unpack or re-ordering");
            Assert.AreEqual("Door", sawmill.CurrentProdItem,
                "kind-2 dispatch binds the plain slot class; Bp is only the event marker");
        }

        // crossover, side A: a big uninvented queue (count 10 = the panel's ∞
        // order) must NOT outrank a user pack request. Native math: kind-2 need =
        // priority × 10.0 (FUN_00434e60:646-647) with priority = ticks × 0.0003 (count==10
        // ramp, FUN_005edea0:66-79) ⇒ after 100 ticks need = 0.3 — a live, dispatchable job,
        // but far below the request's constant 7.5 (FUN_00436440:87/:118). The old port need
        // (count + 2 = 12 > 7.5) dispatched the researcher instead and the record-busy claim
        // then hid the pack until research completed — this test fails on that code.
        [Test]
        public void TenQueuedUninvented_PlusPackOrder_PackerStillDispatches()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;

            var sawmill = Spawn(interp, ctx, "Sawmill", "20 20 8");
            sawmill.Owner = 0;
            sawmill.BuildupComplete = true;
            var gnome = Spawn(interp, ctx, "Researcher", "21 20 8");
            gnome.Owner = 0;
            interp.Eval($"set_worktime {gnome.Handle} 0 12");

            var dispatcher = ctx.ProdDispatcher!;
            interp.Eval($"set_prod_slot_cnt {sawmill.Handle} Farm 10");
            dispatcher.SweepNow(0);          // observe the slot (empty idle pool: job built, none served)
            RunLogicTicks(dispatcher, 100);  // priority 0.03 -> need 0.3: alive, but << 7.5

            sawmill.PackRequested = true;    // the window's pack button (FUN_005242b0:315-318)
            dispatcher.OnGnomeIdle(gnome);
            dispatcher.SweepNow(0);

            Assert.IsNull(FirstWorkat(gnome, "BpFarm"),
                "kind-2 need = priority x 10 = 0.3 must lose to the pack request's 7.5 "
                + "(FUN_00434e60:646-647 vs FUN_00436440:87/:118) — the queue count is not "
                + "part of the native need");
            Assert.IsNotNull(FirstEvent(gnome, "evt_change_muetze", "-text1", "pack"),
                "the packer dispatches: the kind-0 plan opens with its muetze leg "
                + "(FUN_00439760:58)");
            Assert.IsTrue(dispatcher.HasPlan(gnome.Handle), "the pack step plan is open");
            Assert.AreEqual(gnome.Handle, sawmill.CurrentWorkerHandle,
                "the pack dispatch claims the place (FUN_005ede50:17)");
        }

        // crossover, side B: the native math DOES let a long-ignored invention
        // outrank the pack. At the ∞ ramp the priority passes 0.75 after 2500 unserviced logic
        // ticks (FUN_005edea0:66-79), so at 2600 ticks need = 7.8 > 7.5 and the researcher —
        // not the packer — takes the pass.
        [Test]
        public void LongIgnoredInvention_OutranksPack_WhenTheNativeRampCrossesOver()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;

            var sawmill = Spawn(interp, ctx, "Sawmill", "20 20 8");
            sawmill.Owner = 0;
            sawmill.BuildupComplete = true;
            var gnome = Spawn(interp, ctx, "Researcher", "21 20 8");
            gnome.Owner = 0;
            interp.Eval($"set_worktime {gnome.Handle} 0 12");

            var dispatcher = ctx.ProdDispatcher!;
            interp.Eval($"set_prod_slot_cnt {sawmill.Handle} Farm 10");
            dispatcher.SweepNow(0);           // observe the slot (priority entry starts at 0)
            RunLogicTicks(dispatcher, 2600);  // priority 0.78 -> need 7.8 > 7.5

            sawmill.PackRequested = true;
            dispatcher.OnGnomeIdle(gnome);
            dispatcher.SweepNow(0);

            Assert.IsNotNull(FirstWorkat(gnome, "BpFarm"),
                "past the native crossover (priority x 10 > 7.5) the ignored invention "
                + "legitimately outranks the pack request");
            Assert.IsFalse(dispatcher.HasPlan(gnome.Handle),
                "no pack plan opened — the single dispatch of this pass went to the researcher");
            Assert.IsTrue(sawmill.PackRequested,
                "the un-serviced pack byte survives and competes again next pass");
        }
    }
}
