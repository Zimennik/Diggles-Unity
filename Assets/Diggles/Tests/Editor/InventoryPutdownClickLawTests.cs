#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // The click-verb law, pinned against the unchanged Scripts/init/eventgen.tcl:
    //
    // 1. Crate click = select — an own ground crate/building click answers "select" with no
    // event spec (evtgen_gnome's structure gate before the boxed branch, eventgen.tcl:
    // 161-162); the OrderBuild build-button stand-in is removed from ClickToMoveService —
    // placement starts only from the bottom panel's unpack cell (FUN_005242b0 case 1).
    // 2. Inventory item interactions (the native drag&drop's click-driven equivalents):
    // a. click → putdown-place mode (eventgen "placement", eventgen.tcl:268-269/:318-319 →
    // PlacementController.ItemPutdown); confirm = the native FUN_00531310:236-239 round
    // ("-command -placement -asprod") → evt_task_putdown -pos1 <spot> (:263-267);
    // b. Alt+click = use (evt_task_useitem:13; a use-less book → open_box:253-258);
    // c. Double-click → putdown where he stands = the native -invclick confirm
    // (FUN_00531310:68-71/:240-242; eventgen zeroes pospar:99-103 → -pos1 {0 0 0} →
    // evt_task_putdown_proc's drop-in-place branch, z_events.tcl:750-771);
    // d. F9 (not F8) drops the whole inventory: the key handler FUN_0052c510:242-276 evals
    // "set_event %d evt_task_dropall -target %d" per selected unit (VK 0x78 = F9;
    // 0x77/F8 toggles camera-follow,:277-292);
    // e. the -alternate verb sweep (use/read/defuse/assign/convert/pickup) all fire
    // through the generator.
    // 3. Dig-marker click = force-dig — over a tile whose owner dig nibble is set the native
    // hover reports the world object handle 1 (ViWinRoot_Tick raw 0052a646; pick
    // FUN_004796d0:175-192 tests the current owner's tile+0x08 nibble) and eventgen.tcl:
    // 132-139 answers "dig" → evt_task_dig -pos1 <hit>; alt = plain walk (:133-134).
    public sealed class InventoryPutdownClickLawTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // Host plus the actual eventgen.tcl (the same `call` classinit.tcl:111 performs in the game).
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) EvtGenHost()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/Scripts/init/eventgen.tcl"),
                "original data dir not present — eventgen-backed law tests skipped");
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            ctx.DataRoot = DataRoot;
            var sim = new DigglesGameSim(ctx);
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class B none production 0 {} {}");
            interp.Eval("def_class M none material 0 {} {}");
            interp.Eval("def_class U none tool 0 {} { method use {user} {} }");
            interp.Eval("def_class Schatzbuch none material 0 {} {}");
            interp.Eval("def_attrib PilzAge 0 1 0");
            interp.Eval("def_class Bombe none tool 0 {} {}");
            interp.Eval("def_class Baby none baby 0 {} {}");
            interp.Eval("call scripts/init/eventgen.tcl");
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static DigglesTclEntity Gnome(TclInterpreter interp, DigglesTclContext ctx, string pos)
        {
            var g = Spawn(interp, ctx, "G", pos);
            g.Owner = 0;
            return g;
        }

        static EntityEvent? FirstEvent(DigglesTclEntity g, string name)
        {
            foreach (var ev in g.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }

        // ------------------------------------------------------------------
        // 1. crate click = select (the stand-in is gone)
        // ------------------------------------------------------------------

        [Test]
        public void CrateClick_WithGnomeSelected_VerbIsSelect_NothingFires()
        {
            var (interp, ctx, _) = EvtGenHost();
            ctx.LocalPlayer = 0;
            var g = Gnome(interp, ctx, "30 30 8");
            var crate = Spawn(interp, ctx, "B", "33 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            ctx.SelectedHandle = g.Handle;

            // The own-structure gate answers "select" before the boxed branch (eventgen.tcl:
            // 161-162: `!$otherenemy && !$alternate && !$isininv` → select) — a ground-crate
            // command click carries no event spec, so the click layer's only action is
            // selection; placement can start only from the panel's unpack cell (OrderBuild).
            string verb = DigglesTclEngine.EvtGenRun(
                ctx, g.Handle, crate.Handle, crate.Position, ClickToMoveService.CommandTxtPar(false));
            Assert.AreEqual("select", verb.Trim(), "own ground crate + dwarf selected = select verb");
            Assert.IsTrue(string.IsNullOrEmpty(ctx.EvtGen.EvtId), "select carries no event spec");
            Assert.IsFalse(DigglesTclEngine.EvtGenFire(ctx, g.Handle), "nothing fires on a select");
            Assert.IsFalse(ctx.Placement!.Active, "and no placement mode opens from a bare crate click");
        }

        [Test]
        public void GroundCrate_AltClick_IsPickup_NotAssign()
        {
            var (interp, ctx, _) = EvtGenHost();
            ctx.LocalPlayer = 0;
            var g = Gnome(interp, ctx, "30 30 8");
            var crate = Spawn(interp, ctx, "B", "33 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");

            // The boxed branch needs the real get_boxed (was unregistered — the tolerant "0"
            // sent ALT-clicks down the not-boxed path to "assign"/workprod_prefer). Native:
            // ALT+click an own ground crate = pickup — carry it (eventgen.tcl:187-189).
            string verb = DigglesTclEngine.EvtGenRun(
                ctx, g.Handle, crate.Handle, crate.Position, ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("pickup", verb.Trim());
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, g.Handle));
            Assert.IsNotNull(FirstEvent(g, "evt_task_pickup"));
        }

        // ------------------------------------------------------------------
        // 2a. click → putdown-place mode; confirm → evt_task_putdown -pos1 <spot>
        // ------------------------------------------------------------------

        [Test]
        public void InvItemClick_EntersPutdownPlaceMode()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "30 30 8");
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            ctx.SelectedHandle = g.Handle;
            var pc = ctx.Placement!;

            // eventgen's material in-inventory -command verb is "placement" (eventgen.tcl:
            // 268-269 — the native drag&drop arming); the port opens the lightweight
            // putdown-place mode for it.
            Assert.IsTrue(pc.ClickInventoryCrate(g.Handle, item.Handle),
                "a plain item's placement verb must open the putdown-place mode");
            Assert.AreEqual(PlacementController.PlacementMode.ItemPutdown, pc.Mode);
            Assert.AreEqual(item.Handle, pc.PlacingHandle);
            Assert.AreEqual(g.Handle, ctx.SelectedHandle, "the gnome stays the selection (the carrier)");
            Assert.IsFalse(pc.Valid, "validity starts FALSE until the first cursor update");
        }

        [Test]
        public void PutdownPlace_Confirm_RaisesPutdownAtSpot()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "30 30 8");
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            ctx.SelectedHandle = g.Handle;
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.ClickInventoryCrate(g.Handle, item.Handle));

            pc.UpdateCursor(new Vector3(34.3f, 30f, 8f));   // native x snap: 34.3 → 34.5
            Assert.IsFalse(pc.AsBox, "the item mode has NO box branch — the item is placed as itself");
            Assert.IsTrue(pc.Valid);
            Assert.IsTrue(pc.Confirm());

            // The confirm round = the native FUN_00531310:236-239 eventgen re-run
            // ("-command -placement -asprod") → the material in-inventory branch's
            // evt_task_putdown -subject1 <item> -pos1 <spot> (eventgen.tcl:263-267) on the
            // carrier; evt_task_putdown_proc then walks him there and bends
            // (putdown_tasklist's walk_near_item + benda/beam_from_inv_to_pos/bendb,
            // z_procs.tcl:722-730).
            var ev = FirstEvent(g, "evt_task_putdown");
            Assert.IsNotNull(ev, "the carrier must receive evt_task_putdown");
            Assert.AreEqual(item.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.AreEqual("34.5 30 8", ev.Value.Payload!["-pos1"], "-pos1 = the confirmed spot");
            Assert.IsFalse(pc.Active, "confirm exits the mode");
            Assert.IsTrue(pc.OrderGhosts.ContainsKey(item.Handle),
                "the order ghost registers for EVERY confirm (FUN_0046a880 via FUN_00531310:225) — " +
                "evt_task_putdown_proc's set_ghostlist/kill_old_ghosts manage it (z_events.tcl:766-767)");
        }

        [Test]
        public void PutdownPlace_Cancel_ExitsWithoutEvent()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "30 30 8");
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            ctx.SelectedHandle = g.Handle;
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.ClickInventoryCrate(g.Handle, item.Handle));
            pc.UpdateCursor(new Vector3(34.5f, 30f, 8f));

            pc.Cancel();   // Esc / RMB (ClickToMoveService.UpdatePlacement routes both here)
            Assert.IsFalse(pc.Active);
            Assert.IsNull(FirstEvent(g, "evt_task_putdown"), "cancel fires nothing");
            Assert.IsFalse(pc.OrderGhosts.ContainsKey(item.Handle), "no order ghost without a confirm");
        }

        [Test]
        public void InvCrateClick_StillOpensBuildingPlacement()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var crate = Spawn(interp, ctx, "B", "30 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            interp.Eval($"inv_add {g.Handle} {crate.Handle}");
            ctx.SelectedHandle = g.Handle;
            var pc = ctx.Placement!;

            // Regression guard: a boxed crate keeps the full building-ghost placement
            // (box/building swap + depth budget), not the item putdown-place mode.
            Assert.IsTrue(pc.ClickInventoryCrate(g.Handle, crate.Handle));
            Assert.AreEqual(PlacementController.PlacementMode.CrateFromInventory, pc.Mode);
        }

        // ------------------------------------------------------------------
        // 2c. double-click = putdown where he stands (the native -invclick confirm)
        // ------------------------------------------------------------------

        [Test]
        public void DoubleClick_PutsDownAtTheGnomesFeet()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "30 30 8");
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            ctx.SelectedHandle = g.Handle;
            var ipc = new Diggles.Runtime.UI.InventoryPanelController(ctx);

            // First click pends (deferred by the double-click window — arming placement
            // immediately would hide the panel and the double's second click would fall into
            // the world as a confirm).
            ipc.ClickSlot(item.Handle, alt: false, now: 10.0);
            Assert.IsNull(FirstEvent(g, "evt_task_putdown"), "the first click must not fire yet");
            Assert.IsFalse(ctx.Placement!.Active, "…nor arm placement yet");

            // Second click inside the window = the native -invclick confirm: eventgen zeroes
            // pospar (eventgen.tcl:99-103) → evt_task_putdown -pos1 {0 0 0} (:262-263) →
            // evt_task_putdown_proc's invalid-pos branch drops the item where the gnome stands
            // (z_events.tcl:750-754/:763-771).
            ipc.ClickSlot(item.Handle, alt: false, now: 10.1);
            var ev = FirstEvent(g, "evt_task_putdown");
            Assert.IsNotNull(ev, "the double-click must raise the immediate putdown");
            Assert.AreEqual(item.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.AreEqual("0 0 0", ev.Value.Payload!["-pos1"],
                "-invclick zeroes pospar — the drop-in-place sentinel");
            Assert.IsFalse(ctx.Placement.Active, "no placement mode from a double-click");

            // The pending click was consumed — a later flush must not also arm placement.
            ipc.FlushPendingClick(11.0);
            Assert.IsFalse(ctx.Placement.Active);
        }

        [Test]
        public void SingleClick_FlushesIntoPutdownPlace_AfterTheWindow()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "30 30 8");
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            ctx.SelectedHandle = g.Handle;
            var ipc = new Diggles.Runtime.UI.InventoryPanelController(ctx);

            ipc.ClickSlot(item.Handle, alt: false, now: 10.0);
            ipc.FlushPendingClick(10.2);   // still inside the window — must stay pending
            Assert.IsFalse(ctx.Placement!.Active);

            ipc.FlushPendingClick(10.5);   // window passed — the single click arms the mode
            Assert.IsTrue(ctx.Placement.Active);
            Assert.AreEqual(PlacementController.PlacementMode.ItemPutdown, ctx.Placement.Mode);
            Assert.AreEqual(item.Handle, ctx.Placement.PlacingHandle);
        }

        // ------------------------------------------------------------------
        // 2b/2e. the ALT (alternate) verb sweep through the unchanged generator
        // ------------------------------------------------------------------

        [Test]
        public void AltClick_UseMethodItem_FiresUseItem()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "U", "30 30 8");   // class with a `use` method
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            ctx.SelectedHandle = g.Handle;
            var ipc = new Diggles.Runtime.UI.InventoryPanelController(ctx);

            // ALT+click = use (eventgen.tcl:13: any class passing `check_method use` →
            // evt_task_useitem — food, potions, Taucherglocke, Karte).
            ipc.ClickSlot(item.Handle, alt: true, now: 10.0);
            var ev = FirstEvent(g, "evt_task_useitem");
            Assert.IsNotNull(ev, "ALT+click on a use-method item must fire evt_task_useitem");
            Assert.AreEqual(item.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.IsFalse(ctx.Placement!.Active, "an ALT click never pends/arms placement");
            ipc.FlushPendingClick(11.0);
            Assert.IsFalse(ctx.Placement.Active);
        }

        [Test]
        public void AltClick_InventoryBook_OpensBox()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "30 30 8");   // material without a use method
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            ctx.SelectedHandle = g.Handle;

            // The in-inventory alternate read branch (eventgen.tcl:253-258: "Das Buch aus der
            // Inventory gleich benutzen") → evt_task_open_box, verb "use".
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, item.Handle, g.Position,
                Diggles.Runtime.UI.InventoryPanelController.AltClickTxtPar());
            Assert.AreEqual("use", verb.Trim());
            Assert.AreEqual("evt_task_open_box", ctx.EvtGen.EvtId);
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, g.Handle));
        }

        [Test]
        public void AltClick_SchatzbuchOnGround_ReadsIt()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var book = Spawn(interp, ctx, "Schatzbuch", "33 30 8");

            // The Schatz* ground branch (eventgen.tcl:279-282): ALT = read → evt_task_open_box.
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, book.Handle, book.Position,
                ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("use", verb.Trim());
            Assert.AreEqual("evt_task_open_box", ctx.EvtGen.EvtId);
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, g.Handle));
            Assert.IsNotNull(FirstEvent(g, "evt_task_open_box"));
        }

        [Test]
        public void AltClick_Bombe_DefuseAndEnable()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var bomb = Spawn(interp, ctx, "Bombe", "33 30 8");

            // The Bombe alternate branch (eventgen.tcl:327-337): PilzAge 1.0 = armed → defuse;
            // 0.0 = inert → enable; mid-fuse (0.5..1.0 exclusive) → no action.
            interp.Eval($"set_attrib {bomb.Handle} PilzAge 1.0");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, bomb.Handle, bomb.Position,
                ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("use", verb.Trim(), "armed bomb → defuse");
            Assert.AreEqual("evt_task_bombe", ctx.EvtGen.EvtId);

            interp.Eval($"set_attrib {bomb.Handle} PilzAge 0.0");
            verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, bomb.Handle, bomb.Position,
                ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("use", verb.Trim(), "inert bomb → enable");
            Assert.AreEqual("evt_task_bombe", ctx.EvtGen.EvtId);
        }

        [Test]
        public void AltClick_OwnBuilding_AssignsWorker()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var b = Spawn(interp, ctx, "B", "33 30 8");     // own unboxed production
            b.Owner = 0;

            // ALT+click on an own working building = assign (evt_task_workprod_prefer,
            // eventgen.tcl:21 — the preferred-workplace order).
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, b.Handle, b.Position,
                ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("assign", verb.Trim());
            Assert.AreEqual("evt_task_workprod_prefer", ctx.EvtGen.EvtId);
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, g.Handle));
        }

        [Test]
        public void AltClick_GroundMaterial_Walks()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "33 30 8");   // plain ground material

            // ALT on a plain ground material = walk (eventgen.tcl:290-292) — the alternate key
            // suppresses the default pickup.
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, item.Handle, item.Position,
                ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("walk", verb.Trim());
            Assert.AreEqual("evt_task_walk", ctx.EvtGen.EvtId);
        }

        [Test]
        public void AltClick_EnemyBaby_Converts()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            var baby = Spawn(interp, ctx, "Baby", "33 30 8");
            baby.Owner = 1;                                  // a foreign toddler

            // ALT+click an enemy baby = kidnap-convert (eventgen.tcl:380-388 →
            // evt_task_convert, verb "pickup").
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, baby.Handle, baby.Position,
                ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("pickup", verb.Trim());
            Assert.AreEqual("evt_task_convert", ctx.EvtGen.EvtId);
        }

        // ------------------------------------------------------------------
        // 2d. F9 = drop the whole inventory
        // ------------------------------------------------------------------

        [Test]
        public void DropAll_NativeCommandShape_AndFire()
        {
            // The exact native format string (FUN_0052c510:262).
            Assert.AreEqual("set_event 7 evt_task_dropall -target 7",
                ClickToMoveService.DropAllCommand(7));

            var (interp, ctx, _) = EvtGenHost();
            ctx.LocalPlayer = 0;
            var g = Gnome(interp, ctx, "30 30 8");
            ctx.SelectedHandle = g.Handle;
            var svc = new ClickToMoveService(ctx);

            svc.FireDropAll();
            var ev = FirstEvent(g, "evt_task_dropall");
            Assert.IsNotNull(ev, "F9 must raise evt_task_dropall on the selected own unit");
            Assert.AreEqual(g.Handle.ToString(), ev!.Value.Payload!["-target"]);
        }

        [Test]
        public void DropAll_NeverCommandsAForeignUnit()
        {
            var (interp, ctx, _) = EvtGenHost();
            ctx.LocalPlayer = 0;
            var foreign = Gnome(interp, ctx, "30 30 8");
            foreign.Owner = 1;
            ctx.SelectedHandle = foreign.Handle;   // stale/forced selection of a foreign unit
            new ClickToMoveService(ctx).FireDropAll();
            Assert.IsNull(FirstEvent(foreign, "evt_task_dropall"));
        }

        // ------------------------------------------------------------------
        // 2e. class_objsnapclass = native placement mode 7
        // ------------------------------------------------------------------

        [Test]
        public void ObjectSnapPlacement_SelectsClosestWeightedHost_AndRaisesSnapItem()
        {
            var (interp, ctx, _) = EvtGenHost();
            interp.Eval("def_class SnapHost none info 0 {} {}");
            interp.Eval("def_class Crystal none material 0 {} { class_objsnapclass SnapHost 1.5 }");
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "Crystal", "30 30 8");
            item.Owner = 0;
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            var depthHost = Spawn(interp, ctx, "SnapHost", "34.5 30 10");
            var nearHost = Spawn(interp, ctx, "SnapHost", "35 30 8");
            ctx.SelectedHandle = g.Handle;

            Assert.AreEqual("SnapHost", item.Class!.ObjectSnapClass);
            Assert.AreEqual(1.5f, item.Class.ObjectSnapRange, 1e-6f);
            Assert.IsTrue(ctx.Placement!.ClickInventoryCrate(g.Handle, item.Handle));

            // Cursor x=34.1 snaps to 34.5. The depth host pays (dz*0.5)^2 = 1, while
            // nearHost pays dx^2 = 0.25, so the native weighted metric selects nearHost.
            ctx.Placement.UpdateCursor(new Vector3(34.1f, 30f, 8f));
            Assert.AreEqual(nearHost.Handle, ctx.Placement.SnapTargetHandle);
            Assert.AreEqual(nearHost.Position, ctx.Placement.GhostPos,
                "without a rendered link channel, mode 7 falls back to the host origin");
            Assert.IsTrue(ctx.Placement.Valid);
            Assert.IsFalse(ctx.Placement.AsBox);

            Assert.IsTrue(ctx.Placement.Confirm());
            var ev = FirstEvent(g, "evt_task_snapitem");
            Assert.IsNotNull(ev, "the confirm must take eventgen's unchanged -snapobj branch");
            Assert.AreEqual(item.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.AreEqual(nearHost.Handle.ToString(), ev.Value.Payload!["-subject2"]);
            Assert.AreNotEqual(depthHost.Handle.ToString(), ev.Value.Payload!["-subject2"]);
        }

        [Test]
        public void ObjectSnapPlacement_NoHostInsideRange_FallsBackToPutdown()
        {
            var (interp, ctx, _) = EvtGenHost();
            interp.Eval("def_class SnapHost none info 0 {} {}");
            interp.Eval("def_class Crystal none material 0 {} { class_objsnapclass SnapHost 1.5 }");
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "Crystal", "30 30 8");
            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            Spawn(interp, ctx, "SnapHost", "34 30 8");
            ctx.SelectedHandle = g.Handle;
            Assert.IsTrue(ctx.Placement!.ClickInventoryCrate(g.Handle, item.Handle));

            ctx.Placement.UpdateCursor(new Vector3(40f, 30f, 8f));
            Assert.AreEqual(-1, ctx.Placement.SnapTargetHandle);
            Assert.IsTrue(ctx.Placement.Valid, "mode 7 falls through to ordinary placement when no host wins");
            Assert.IsTrue(ctx.Placement.Confirm());
            Assert.IsNotNull(FirstEvent(g, "evt_task_putdown"));
            Assert.IsNull(FirstEvent(g, "evt_task_snapitem"));
        }

        // ------------------------------------------------------------------
        // 3. dig-marker click = force-dig (the world-object subpar law)
        // ------------------------------------------------------------------

        [Test]
        public void MarkedTerrainClick_WorldSubpar_FiresDigAtTheMark()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            ctx.SelectedHandle = g.Handle;

            // Over a dig-marked tile the native hover reports the world object — the literal
            // handle 1 (ViWinRoot_Tick raw 0052a646; FUN_004796d0:175-192's tile+0x08 nibble
            // test = the port's RawDigNibble gate in ClickToMoveService). eventgen.tcl:132-139
            // answers "dig" with evt_task_dig -pos1 <hit>; evt_task_dig_proc then runs the same
            // dig_starttask chain the marquee commit uses.
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, ClickToMoveService.WorldHandle,
                new Vector3(40f, 30f, 8f), ClickToMoveService.CommandTxtPar(false));
            Assert.AreEqual("dig", verb.Trim());
            Assert.AreEqual("evt_task_dig", ctx.EvtGen.EvtId);
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, g.Handle));
            var ev = FirstEvent(g, "evt_task_dig");
            Assert.IsNotNull(ev);
            Assert.AreEqual("40 30 8", ev!.Value.Payload!["-pos1"], "-pos1 = the clicked mark");
        }

        [Test]
        public void MarkedTerrainClick_Alt_IsPlainWalk()
        {
            var (interp, ctx, _) = EvtGenHost();
            var g = Gnome(interp, ctx, "30 30 8");
            ctx.SelectedHandle = g.Handle;

            // ALT over marked terrain = plain walk (eventgen.tcl:133-134) — the alternate key
            // suppresses the force-dig, all inside the unchanged generator.
            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, ClickToMoveService.WorldHandle,
                new Vector3(40f, 30f, 8f), ClickToMoveService.CommandTxtPar(true));
            Assert.AreEqual("walk", verb.Trim());
            Assert.AreEqual("evt_task_walk", ctx.EvtGen.EvtId);
        }
    }
}
