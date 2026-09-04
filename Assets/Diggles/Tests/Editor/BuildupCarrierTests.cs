#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // The carrier-only construction model (RE'd from buildup-carrier-re.md; the dispatch model
    // routes through the single-player planner — CProdPlannerMgr::Tick FUN_00432720:30 runs
    // CProdPlanner only while the MP flag DAT_0076837a == 0; the direct-emit 005f allocator
    // (FUN_005f3a50) is the mutually-exclusive multiplayer path):
    //
    // • A request job exists only while a per-object request byte is set — there is no scan
    // that arms buildup for placed-unbuilt buildings (byte writers: the window aufbau button
    // FUN_005242b0:342, the deferred re-place tick FUN_005edea0:136, Tcl set_prod_buildup —
    // unused by shipped scripts). The former CollectBuildupJobs auto-arm fan-out was removed.
    // • Placement is a native ghost mode (FUN_00530e80 enter / FUN_00531310 confirm), never a
    // ground click. Confirm branches on the selection (FUN_005e35a0, FUN_00531310:227-236):
    // gnome selected (crate in its inventory) → eventgen → evt_task_buildup on the carrier;
    // crate itself selected → the deferred order (+0x28/+0x30/+0x2d, FUN_005ee250).
    // • SP job collection (FUN_00436440): +0x29 pack → kind-0 @ building; +0x2a buildup and
    // +0x28 unpack both → kind-1 (there is no separate SP buildup job kind; the aufbau
    // resume rides the unpack event's not-boxed branch). Need = 7.5 (:87/:118).
    // • Kind-1 dispatch = a step plan (FUN_00439990 → steps emitted one per gnome-idle via
    // FUN_0043a460/FUN_0043a8a0): boxed crate → muetze → walk → pick up the crate → walk →
    // evt_autoprod_unpack; not boxed → straight to evt_autoprod_unpack at the object's own
    // position (:81-84). The gnome fetches the crate first — prod_buildup_check would cancel
    // the order at the spot otherwise ("dont have it", z_work_common.tcl:355-357).
    // • New jobs dispatch only on the periodic maintenance pass (one per pass,
    // FUN_00433a70:409-450); the gnome_idle one-shot FUN_00434bc0 never matches new jobs —
    // tests drive the pass through the SweepNow seam.
    // • Every request branch is gated on not contained (obj+0x3d0 bit 2) — a carried crate is
    // invisible to the dispatcher (MP collector FUN_005f0850:22/:56/:90; the SP plan would
    // die at the Tcl pickup's is_contained gate, z_procs.tcl:462).
    // • Requests are one-shot at dispatch (port model; the SP planner gates re-collection on
    // the record leaving idle, FUN_00436440:31) and claim the place (rec+0x24,
    // FUN_005ede50:17); a dying unpack plan re-arms the byte so the order retries.
    //
    // Headless: fabricated classes with the real kinds; the eventgen-backed tests source the
    // unchanged Scripts/init/eventgen.tcl (classinit.tcl:111 does the same `call` in the game).
    public sealed class BuildupCarrierTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

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
            interp.Eval("def_class B none production 0 {} {}");
            return (interp, ctx, sim);
        }

        // Host + the real eventgen.tcl for the placement-confirm decision tree.
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) EvtGenHost()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/Scripts/init/eventgen.tcl"),
                "original data dir not present — eventgen-backed placement tests skipped");
            var (interp, ctx, sim) = Host();
            ctx.DataRoot = DataRoot;
            interp.Eval("call scripts/init/eventgen.tcl");
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

        static EntityEvent? FirstEvent(DigglesTclEntity g, string name)
        {
            foreach (var ev in g.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }

        // ------------------------------------------------------------------
        // 1. no fan-out — the regression test for the deleted auto-arm (spec §10.6.2)
        // ------------------------------------------------------------------

        [Test]
        public void NoFanOut_PlacedUnbuiltBuilding_IsNeverAutoDispatched()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;

            // A placed-but-unbuilt building: not boxed, BuildupComplete = ctor default unbuilt
            // (CObj flag word 0x01100101, bit 0x400000 clear — FUN_005c83f0:224), request byte clear.
            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;
            Assert.IsFalse(b.BuildupComplete, "precondition: placed-unbuilt");
            Assert.IsFalse(b.BuildupRequested, "precondition: no request armed");

            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;
            // Both dispatch paths: the idle report AND many full maintenance passes.
            for (int i = 0; i < 50; i++) { disp.OnGnomeIdle(g); disp.SweepNow(0); }
            Assert.IsNull(FirstEvent(g, "evt_autoprod_unpack"),
                "NO kind-1 job without the +0x2a/+0x28 request — the dispatcher must never " +
                "fabricate construction fan-out for placed-unbuilt buildings (FUN_00436440:31-54)");
            Assert.IsNull(FirstEvent(g, "evt_autoprod_pickup"), "…and no pickup leg either");
        }

        // ------------------------------------------------------------------
        // 2. resume — the aufbau button arms +0x2a; exactly one gnome; one-shot (spec §10.6.3)
        // ------------------------------------------------------------------

        [Test]
        public void ResumeRequest_DispatchesExactlyOneGnome_OneShot()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;                                     // placed-unbuilt (ctor default)
            var g1 = Gnome(interp, ctx, "32 30 8");
            var g2 = Gnome(interp, ctx, "31 30 8");
            var disp = ctx.ProdDispatcher!;
            var pc = ctx.Placement!;

            // The window's resume (aufbau) button (FUN_005242b0 case 4: u16 +0x29 = 0x100 →
            // buildup byte set + pack byte clear; +0x3d word = 0; +0x64 = 1).
            b.PackRequested = true;                          // must be zeroed by the u16 write
            pc.OrderResume(b.Handle);
            Assert.IsTrue(b.BuildupRequested);
            Assert.IsFalse(b.PackRequested, "the u16 write at +0x29 zeroes the pack byte (FUN_005242b0:342)");
            Assert.AreEqual("1", interp.Eval($"get_prod_enabled {b.Handle}").Trim(), "+0x64 = 1 (:347)");

            // New jobs dispatch on the maintenance pass, not on the idle report (the one-shot
            // FUN_00434bc0 only advances existing plans). The SP buildup byte emits a kind-1 job
            // (FUN_00436440:33-52) whose not-boxed plan goes straight to evt_autoprod_unpack at
            // the object's position (FUN_00439990:81-84 + FUN_0043a8a0:113-118) — the Tcl
            // not-boxed branch is the prod_buildup_rep resume (z_work_common.tcl:335).
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            var ev = FirstEvent(g1, "evt_autoprod_unpack");
            Assert.IsNotNull(ev, "the armed request must dispatch as the SP kind-1 unpack");
            Assert.AreEqual(b.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.AreEqual("0", ev.Value.Payload!["-num1"], "the buildup byte's job carries asbox 0 (FUN_00436440:52)");
            Assert.AreEqual("33.0 30.0 8.0", ev.Value.Payload!["-pos1"],
                "not-boxed kind-1 targets the OBJECT's own position (FUN_00439990:81-84)");
            Assert.IsFalse(b.BuildupRequested, "one-shot: the dispatch clears the request bytes");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle, "the dispatch claims the place (FUN_005ede50:17)");

            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g2, "evt_autoprod_unpack"),
                "a SECOND gnome must get nothing — the request is one-shot, never a fan-out");
        }

        // Interrupted worker → user re-orders → another gnome takes it (spec §6: resume is
        // explicit, the dispatcher only picks which gnome serves the re-armed request).
        [Test]
        public void ReleasedWorker_ReArmedRequest_GoesToSecondGnome()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;
            var g1 = Gnome(interp, ctx, "32 30 8");
            var g2 = Gnome(interp, ctx, "31 30 8");
            var disp = ctx.ProdDispatcher!;
            var pc = ctx.Placement!;

            pc.OrderResume(b.Handle);
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g1, "evt_autoprod_unpack"), "g1 must take the resume job");

            // g1 is interrupted (gnome_failed_work): reservation released, claim dropped
            // (FUN_0043a0c0:31), g1 gets the 5 s embargo, the job pos the 20-tick reopen.
            interp.Eval($"gnome_failed_work {g1.Handle}");
            Assert.AreEqual(-1, b.CurrentWorkerHandle, "release must drop the construction claim");

            // The player clicks the aufbau button again; after the reopen cooldown the second
            // gnome takes it (g1 still breathes out its embargo).
            pc.OrderResume(b.Handle);
            for (int i = 0; i < 21; i++) disp.DecayCooldowns();
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g2, "evt_autoprod_unpack"),
                "after the reopen cooldown the re-armed request must dispatch to the next free gnome");
            Assert.AreEqual(g2.Handle, b.CurrentWorkerHandle);
        }

        // ------------------------------------------------------------------
        // 3. the deferred order — crate selected, ghost confirm → +0x28/+0x30/+0x2d →
        // the kind-1 step plan: pickup first, then evt_autoprod_unpack -pos1 -num1
        // (FUN_00436440:54-87 + FUN_00439990:81-141; spec §10.6.4)
        // ------------------------------------------------------------------

        [Test]
        public void DeferredOrder_Confirm_WritesOrder_ThenUnpackDispatchesWithPosAndNum()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var pc = ctx.Placement!;

            // The crate window's build button → placement mode (FUN_005242b0 case 1 →
            // FUN_00530e80); the crate is the selection (its window).
            Assert.IsTrue(pc.OrderBuild(crate.Handle));
            Assert.AreEqual(crate.Handle, ctx.SelectedHandle);
            Assert.IsFalse(pc.Valid, "validity starts FALSE until the first cursor update (+0xcc)");

            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Valid);
            Assert.IsTrue(pc.Confirm());

            // The deferred order = the FUN_005ee250 record write (pos→+0x30, asbox→+0x2d,
            // +0x28 = 1, +0x3d word = 0x100).
            Assert.IsTrue(crate.UnpackRequested);
            Assert.AreEqual("1", interp.Eval($"get_prod_unpack {crate.Handle}").Trim(),
                "the typed flag must read back through get_prod_unpack (FUN_00692db0:59)");
            Assert.AreEqual(new Vector3(28f, 20f, 8f), crate.OrderPos);
            Assert.IsFalse(crate.OrderAsBox);
            Assert.IsTrue(crate.OrderGhostShown);
            Assert.IsTrue(pc.OrderGhosts.ContainsKey(crate.Handle), "the order ghost stands at the spot");
            Assert.IsFalse(pc.Active, "confirm exits placement mode");

            // Dispatch (maintenance pass): the boxed crate opens the kind-1 step plan
            // (FUN_00439990) — first the muetze step ('evt_change_muetze -text1 unpack',:72-80
            // → prod_change_muetze unpack → the Dummy_Muetze_dienstleistung carrier hat goes on
            // before the fetch), then pickup, then unpack — one per idle report. One-shot +
            // claim at dispatch.
            var disp = ctx.ProdDispatcher!;
            var g1 = Gnome(interp, ctx, "20 20 8");
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            var mu = FirstEvent(g1, "evt_change_muetze");
            Assert.IsNotNull(mu, "the plan's first step is the carrier-hat muetze (FUN_00439990:72-80)");
            Assert.AreEqual("unpack", mu!.Value.Payload!["-text1"],
                "-text1 unpack → get_nameofmuetze 'unpack' → Dummy_Muetze_dienstleistung (z_work_common.tcl:823)");
            Assert.IsNull(FirstEvent(g1, "evt_autoprod_pickup"), "the pickup step waits for the muetze leg");
            Assert.IsTrue(disp.HasPlan(g1.Handle), "the gnome holds the kind-1 step plan");
            Assert.IsFalse(crate.UnpackRequested, "one-shot at dispatch (record leaves idle, FUN_00436440:31)");
            Assert.AreEqual(g1.Handle, crate.CurrentWorkerHandle);

            var g2 = Gnome(interp, ctx, "21 20 8");
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g2, "evt_change_muetze"), "no second dispatch for a one-shot order");

            // The muetze leg ends (hat anims via the task state) → the next idle report advances
            // to the pickup: the gnome goes to fetch the crate (a direct-unpack emit would walk
            // him to the spot empty-handed — prod_buildup_check cancels with 'dont have it',
            // z_work_common.tcl:355-357 — regression: the pickup leg must run first).
            disp.DecayCooldowns(); disp.DecayCooldowns();    // the 2-tick assign cooldown
            disp.OnGnomeIdle(g1);
            var pk = FirstEvent(g1, "evt_autoprod_pickup");
            Assert.IsNotNull(pk, "the plan must advance to the pickup step (FUN_0043a8a0:49-59)");
            Assert.AreEqual(crate.Handle.ToString(), pk!.Value.Payload!["-subject1"]);
            Assert.IsNull(FirstEvent(g1, "evt_autoprod_unpack"), "the unpack step waits for the pickup leg");

            // The pickup leg lands the crate in the inventory (walk_near_item → take_item →
            // inv_add, z_procs.tcl:477-482); the next idle report emits the unpack step with the
            // stored order pos and asbox (FUN_0043a8a0:113-118). Every advance re-stamps the
            // 2-tick cooldown (gnome+0x3e8 = 2, FUN_00434bc0:18) — decay it first.
            interp.Eval($"inv_add {g1.Handle} {crate.Handle}");
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            var ev = FirstEvent(g1, "evt_autoprod_unpack");
            Assert.IsNotNull(ev, "the plan must advance to the unpack step once the crate is held");
            Assert.AreEqual(crate.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.AreEqual("28.0 20.0 8.0", ev.Value.Payload!["-pos1"], "-pos1 = the stored ORDER pos (rec+0x30)");
            Assert.AreEqual("0", ev.Value.Payload!["-num1"], "-num1 = the asbox byte (+0x2d)");
        }

        [Test]
        public void DeferredOrder_AsBoxToggle_EmitsNum1_1()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var pc = ctx.Placement!;

            pc.OrderBuild(crate.Handle);
            pc.ToggleAsBox();                                // +0xcd — crate modes honor it (FUN_00531310:91-97)
            Assert.IsTrue(pc.AsBox);
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(crate.OrderAsBox, "asbox → +0x2d = 1 (FUN_005ee250:10)");

            var disp = ctx.ProdDispatcher!;
            var g = Gnome(interp, ctx, "20 20 8");
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g, "evt_change_muetze"), "muetze leg first (boxed crate)");
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g);
            Assert.IsNotNull(FirstEvent(g, "evt_autoprod_pickup"), "then the pickup leg");
            interp.Eval($"inv_add {g.Handle} {crate.Handle}");
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g);
            var ev = FirstEvent(g, "evt_autoprod_unpack");
            Assert.IsNotNull(ev);
            Assert.AreEqual("1", ev!.Value.Payload!["-num1"],
                "-num1 = 1 → the handler's putdown route (place as bare BOX)");
        }

        // stop_prod's cancel really cancels (spec §10.6.5): set_prod_unpack <item> 0
        // (z_procs.tcl:1240) clears the typed flag → no re-dispatch.
        [Test]
        public void StopProdCancel_SetProdUnpack0_NoRedispatch()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            pc.Confirm();
            Assert.IsTrue(crate.UnpackRequested);

            interp.Eval($"set_prod_unpack {crate.Handle} 0");   // the stop_prod cancel line
            Assert.IsFalse(crate.UnpackRequested, "the typed flag must clear (was a string-var hack)");

            var g = Gnome(interp, ctx, "20 20 8");
            ctx.ProdDispatcher!.OnGnomeIdle(g);
            ctx.ProdDispatcher!.SweepNow(0);
            Assert.IsNull(FirstEvent(g, "evt_change_muetze"), "a cancelled order must not open a plan");
            Assert.IsNull(FirstEvent(g, "evt_autoprod_pickup"), "a cancelled order must not dispatch a pickup leg");
            Assert.IsNull(FirstEvent(g, "evt_autoprod_unpack"), "a cancelled order must not dispatch");
        }

        // ------------------------------------------------------------------
        // 4. pack — the window button arms +0x29 → evt_autoprod_pack; one-shot (spec §10.6.6)
        // ------------------------------------------------------------------

        [Test]
        public void Pack_RequestDispatches_OneShot_AndSlotsCleared()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;
            b.BuildupComplete = true;                        // a built building
            b.ProdSlots["Grillpilz"] = 3;
            var pc = ctx.Placement!;

            // The window's pack button (FUN_005242b0 case 2): per-slot clear loop (:39),
            // then +0x29 = 1.
            pc.OrderPack(b.Handle);
            Assert.IsTrue(b.PackRequested);
            Assert.AreEqual(0, (int)b.ProdSlots["Grillpilz"], "pack clears the production schedule (FUN_005edc70 loop)");

            var disp = ctx.ProdDispatcher!;
            var g1 = Gnome(interp, ctx, "32 30 8");
            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            // The kind-0 pack is a step plan — the muetze leg first (FUN_00439760:58, category
            // "pack" → the service hat, z_work_common.tcl:822), the pack step on the next idle
            // advance (code 6, FUN_0043a8a0:11). Regression: a direct evt_autoprod_pack
            // emit at dispatch skips the hat and leaves the order planless.
            var mu = FirstEvent(g1, "evt_change_muetze");
            Assert.IsNotNull(mu, "the pack dispatch opens with the muetze step (FUN_00439760:58)");
            Assert.AreEqual("pack", mu!.Value.Payload!["-text1"]);
            // The pack byte survives the dispatch — the Tcl chain re-reads it mid-flight and a
            // cleared byte is the native cancel (prod_builddown: `if {![get_prod_pack $item]}
            // { return }`, z_work_common.tcl:443-446). The native clears it only at boxing
            // (set_boxed side effects, FUN_0067dc80:157-161). Regression: clearing the byte at
            // dispatch instead turned every pack order into a walk-over no-op.
            Assert.IsTrue(b.PackRequested, "the pack byte must survive dispatch (z_work_common.tcl:444 gate)");
            Assert.AreEqual(g1.Handle, b.CurrentWorkerHandle);

            // the plan's advance emits the pack step with the byte still armed
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            var ev = FirstEvent(g1, "evt_autoprod_pack");
            Assert.IsNotNull(ev, "the advance emits the pack step (kind-0 job, FUN_00436440:89-118)");
            Assert.AreEqual(b.Handle.ToString(), ev!.Value.Payload!["-subject1"]);

            var g2 = Gnome(interp, ctx, "31 30 8");
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g2, "evt_autoprod_pack"),
                "no second packer — re-dispatch is gated by the rec+0x24 claim, not the byte");
            Assert.IsNull(FirstEvent(g2, "evt_change_muetze"), "…no second plan either");

            // packtobox → set_boxed 1: the boxing side effects kill the byte (FUN_0067dc80:157-161).
            interp.Eval($"set_boxed {b.Handle} 1");
            Assert.IsFalse(b.PackRequested, "boxing clears the pack byte");
        }

        // ------------------------------------------------------------------
        // 4b. pack cancel — the armed button's second click drops the byte; the walking packer
        // stands down at the prod_builddown gate (z_work_common.tcl:443-446); the window
        // shows S1packtobox + "Cancel" while armed (FUN_00521c80:743-757).
        // ------------------------------------------------------------------

        [Test]
        public void Pack_CancelDropsTheByte()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;
            b.BuildupComplete = true;
            var pc = ctx.Placement!;
            pc.OrderPack(b.Handle);
            Assert.IsTrue(b.PackRequested);
            pc.CancelPack(b.Handle);
            Assert.IsFalse(b.PackRequested, "cancel = the byte drop (the whole native cancel)");
            // nothing dispatches afterwards
            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g, "evt_autoprod_pack"), "cancelled pack must not dispatch");
        }

        // ------------------------------------------------------------------
        // 4c. move (ALT+pack, "Umzug") — FUN_005242b0:314-329: ghost placement + rec+0x3c;
        // confirm arms pack with the stored order; boxing converts it into the unpack at
        // the ordered pos (FUN_005edea0:122-138 via the set_boxed side effects).
        // ------------------------------------------------------------------

        [Test]
        public void Move_AltPack_PacksThenReplacesAtTheOrderedSpot()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Spawn(interp, ctx, "B", "33 30 8");
            b.Owner = 0;
            b.BuildupComplete = true;
            var pc = ctx.Placement!;

            Assert.IsTrue(pc.OrderMove(b.Handle), "a BUILT building enters the move placement");
            Assert.AreEqual(PlacementController.PlacementMode.MoveBuilt, pc.Mode);
            pc.UpdateCursor(new Vector3(40f, 30f, 8f));
            Assert.IsTrue(pc.Valid, "open headless spot must validate");
            Assert.IsTrue(pc.Confirm(), "move confirm");
            Assert.IsTrue(b.PackRequested, "move first packs the building");
            Assert.IsTrue(b.MoveAfterPack, "rec+0x3c armed");
            Assert.IsFalse(b.UnpackRequested, "the unpack is DEFERRED until boxed");
            Assert.AreEqual(40f, b.OrderPos.x, 0.6f, "the ordered spot is stored (rec+0x30)");

            // the pack chain finishes → packtobox → set_boxed 1 → the deferred conversion
            interp.Eval($"set_boxed {b.Handle} 1");
            Assert.IsFalse(b.PackRequested, "boxing clears the pack byte");
            Assert.IsFalse(b.MoveAfterPack, "rec+0x3c consumed");
            Assert.IsTrue(b.UnpackRequested, "the stored order re-arms as the unpack (FUN_005edea0:122-138)");
            Assert.IsFalse(b.OrderAsBox, "a moved building re-builds as production");
        }

        // ------------------------------------------------------------------
        // 4d. buildup facing data — the stage-rep teleport/turn law (prod_buildup_rep,
        // z_work_common.tcl:516-518: `set_pos this [vector_add [get_linkpos $item
        // $steplink] [get_pos $item]]; set_roty this [linkrot.y + π]`) reads the item's
        // build-dummy link (feuerstelle build_dummys = {16 16 17}, feuerstelle.tcl:308)
        // off the current (bau_*) stage frame. This pins that the stage frames actually
        // carry those links in the shipped data — if they do, a live "gnome does not turn
        // to the building" is a set_roty consumer bug, not missing link data.
        // ------------------------------------------------------------------

        [Test]
        public void BuildDummyLinks_ExistOnFeuerstelleStageFrames()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/3db/produktionsstaetten.3db"),
                "original data dir not present — link-data test skipped");
            var lib = new DigglesModelLibrary(DataRoot);
            foreach (var clipName in new[] { "bau_a", "bau_b", "bau_c", "standard" })
            {
                var clip = lib.GetClip("feuerstelle", clipName);
                Assert.IsNotNull(clip, $"feuerstelle.{clipName} must resolve");
                Assert.IsNotNull(clip!.SourceModel, "clip carries its source model");
                int mesh0 = clip.SourceMeshIndices![0];
                foreach (int link in new[] { 16, 17 })
                {
                    bool hasRot = Diggles.Runtime.DigglesLinkPoints.TryGetLocalEuler(
                        clip.SourceModel, mesh0, link, out var eul);
                    bool hasPos = Diggles.Runtime.DigglesLinkPoints.TryGetEngineOffset(
                        clip.SourceModel, mesh0, link, out var off);
                    TestContext.WriteLine($"{clipName} link {link}: rot={hasRot} " +
                        $"eul=({eul.x:0.00},{eul.y:0.00},{eul.z:0.00}) pos={hasPos} " +
                        $"off=({off.x:0.00},{off.y:0.00},{off.z:0.00})");
                    Assert.IsTrue(hasRot, $"{clipName} link {link}: linkrot data missing");
                    Assert.IsTrue(hasPos, $"{clipName} link {link}: linkpos data missing");
                }
            }
        }

        // ------------------------------------------------------------------
        // 5. contained gate — a carried crate is invisible to the dispatcher (spec §0.4)
        // ------------------------------------------------------------------

        [Test]
        public void CarriedCrate_AllRequests_InvisibleToDispatcher()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var holder = Gnome(interp, ctx, "30 30 8");
            var crate = Spawn(interp, ctx, "B", "30 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            interp.Eval($"inv_add {holder.Handle} {crate.Handle}");
            Assert.GreaterOrEqual(crate.ContainedIn, 0, "precondition: crate rides in the inventory");

            // Even with every request byte armed, a contained object emits no job
            // (obj+0x3d0 bit 2 gate at FUN_005f0850:22/:56/:90).
            crate.BuildupRequested = true;
            crate.PackRequested = true;
            crate.UnpackRequested = true;
            crate.OrderPos = new Vector3(28f, 30f, 8f);

            var g = Gnome(interp, ctx, "31 30 8");
            var disp = ctx.ProdDispatcher!;
            for (int i = 0; i < 20; i++) { disp.OnGnomeIdle(g); disp.SweepNow(0); }
            Assert.IsNull(FirstEvent(g, "evt_autoprod_pack"), "contained: no pack job");
            Assert.IsNull(FirstEvent(g, "evt_change_muetze"), "contained: no plan opened");
            Assert.IsNull(FirstEvent(g, "evt_autoprod_pickup"), "contained: no pickup leg");
            Assert.IsNull(FirstEvent(g, "evt_autoprod_unpack"), "contained: no unpack job");
        }

        // ------------------------------------------------------------------
        // 6. the carrier route — ghost confirm through the unchanged eventgen (spec §10.6.1)
        // ------------------------------------------------------------------

        [Test]
        public void Confirm_CarrierRoute_RaisesEvtTaskBuildup_OnTheCarrierOnly()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var crate = Spawn(interp, ctx, "B", "30 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            interp.Eval($"inv_add {g.Handle} {crate.Handle}");
            ctx.SelectedHandle = g.Handle;                  // the gnome is the selection
            var pc = ctx.Placement!;

            // The inventory crate click routes through eventgen and returns "placement"
            // (boxed + isininv + -command without -placement, eventgen.tcl:180-182).
            Assert.IsTrue(pc.ClickInventoryCrate(g.Handle, crate.Handle),
                "the eventgen 'placement' verb must enter placement mode");
            Assert.AreEqual(PlacementController.PlacementMode.CrateFromInventory, pc.Mode);
            Assert.AreEqual(crate.Handle, pc.PlacingHandle);

            pc.UpdateCursor(new Vector3(34.3f, 30f, 8f));   // native snap: 34.3 → 34.5
            Assert.IsTrue(pc.Valid);
            Assert.IsTrue(pc.Confirm());

            // The confirm round ran eventgen with '-command -placement -asprod'
            // (FUN_00531310:237) → evt_task_buildup -subject1 crate -pos1 spot on the carrier
            // (eventgen.tcl:172-174) — and no deferred order on the record.
            var ev = FirstEvent(g, "evt_task_buildup");
            Assert.IsNotNull(ev, "the carrier must receive evt_task_buildup");
            Assert.AreEqual(crate.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.AreEqual("34.5 30 8", ev.Value.Payload!["-pos1"]);
            Assert.IsFalse(crate.UnpackRequested,
                "the carrier route never writes the deferred order (that is the crate-selected branch)");
            Assert.IsFalse(pc.Active, "confirm exits placement mode");
            Assert.IsTrue(pc.OrderGhosts.ContainsKey(crate.Handle),
                "the persistent order ghost is registered at the confirmed spot (FUN_0046a880)");
        }

        [Test]
        public void Confirm_CarrierRoute_AsBox_RaisesPutdown()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var crate = Spawn(interp, ctx, "B", "30 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            interp.Eval($"inv_add {g.Handle} {crate.Handle}");
            ctx.SelectedHandle = g.Handle;
            var pc = ctx.Placement!;

            Assert.IsTrue(pc.ClickInventoryCrate(g.Handle, crate.Handle));
            pc.ToggleAsBox();
            pc.UpdateCursor(new Vector3(34.5f, 30f, 8f));
            Assert.IsTrue(pc.Confirm());

            // '-asbox' (no 'asprod' in txtpar) → the putdown branch (eventgen.tcl:176-178).
            Assert.IsNull(FirstEvent(g, "evt_task_buildup"));
            var ev = FirstEvent(g, "evt_task_putdown");
            Assert.IsNotNull(ev, "asbox confirm must raise evt_task_putdown on the carrier");
            Assert.AreEqual(crate.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
        }

        // ------------------------------------------------------------------
        // 7. validity / ghost lifecycle / UI seam
        // ------------------------------------------------------------------

        [Test]
        public void Confirm_InvalidSpot_IsNoOp_StaysInPlacement()
        {
            var (interp, ctx, _) = Host();
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var blocker = Spawn(interp, ctx, "B", "25 20 8");   // an un-contained world object
            blocker.Owner = 0;
            var pc = ctx.Placement!;

            pc.OrderBuild(crate.Handle);
            pc.UpdateCursor(new Vector3(24.8f, 20f, 8f));       // native snap: 24.8 → 25.0, ON the blocker
            Assert.IsFalse(pc.Valid, "object overlap must invalidate the spot (check_ghost_coll analog)");
            Assert.IsFalse(pc.Confirm(), "confirm is a NO-OP while the spot is invalid (FUN_00531310:102)");
            Assert.IsTrue(pc.Active, "a failed confirm stays in placement mode");
            Assert.IsFalse(crate.UnpackRequested, "no order written by a no-op confirm");

            pc.UpdateCursor(new Vector3(30f, 20f, 8f));         // clear ground
            Assert.IsTrue(pc.Valid, "gnome-free open ground is valid (gnomes would NOT invalidate)");
            Assert.IsTrue(pc.Confirm());
        }

        [Test]
        public void HideObjGhost_ClearsTheOrderGhostAndFlag()
        {
            var (interp, ctx, _) = Host();
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            pc.Confirm();
            Assert.IsTrue(pc.OrderGhosts.ContainsKey(crate.Handle));
            Assert.IsTrue(crate.OrderGhostShown);

            // The chain's cleanup (prod_buildup z_work_common.tcl:416 / unpackfrombox
            // genericprod.tcl:204 / the cancel:364) — FUN_0069b440:58 → FUN_0046a960.
            interp.Eval($"hide_obj_ghost {crate.Handle}");
            Assert.IsFalse(pc.OrderGhosts.ContainsKey(crate.Handle), "the standing ghost is removed");
            Assert.IsFalse(crate.OrderGhostShown, "rec+0x3e clears with it (spec §10.2)");
        }

        [Test]
        public void Cancel_LeavesModeWithoutAnOrder()
        {
            var (interp, ctx, _) = Host();
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            pc.Cancel();
            Assert.IsFalse(pc.Active);
            Assert.IsFalse(crate.UnpackRequested, "cancel writes nothing");
            Assert.IsFalse(pc.OrderGhosts.ContainsKey(crate.Handle), "no order ghost without a confirm");
        }

        // The building-window button model (spec §10.4): build on a crate, resume on a
        // placed-unbuilt building, pack on a built one — with the S1 "pending" variants
        // (FUN_00521c80:361/:503-518).
        [Test]
        public void QueryButtons_MirrorsTheNativeWindowSurface()
        {
            var (interp, ctx, _) = Host();
            var pc = ctx.Placement!;

            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            interp.Eval($"set_boxed {crate.Handle} 1");
            var bb = pc.QueryButtons(crate.Handle);
            Assert.IsTrue(bb.ShowBuild); Assert.IsFalse(bb.ShowResume); Assert.IsFalse(bb.ShowPack);
            Assert.IsFalse(bb.BuildPending);
            crate.UnpackRequested = true;
            Assert.IsTrue(pc.QueryButtons(crate.Handle).BuildPending, "S1 icon while the order is pending");

            var half = Spawn(interp, ctx, "B", "24 20 8");      // placed-unbuilt
            bb = pc.QueryButtons(half.Handle);
            Assert.IsFalse(bb.ShowBuild); Assert.IsTrue(bb.ShowResume); Assert.IsFalse(bb.ShowPack);
            half.BuildupRequested = true;
            Assert.IsTrue(pc.QueryButtons(half.Handle).ResumePending, "S1aufbau while +0x2a pending");

            var built = Spawn(interp, ctx, "B", "28 20 8");
            built.BuildupComplete = true;
            bb = pc.QueryButtons(built.Handle);
            Assert.IsFalse(bb.ShowBuild); Assert.IsFalse(bb.ShowResume); Assert.IsTrue(bb.ShowPack);
        }
    }
}
