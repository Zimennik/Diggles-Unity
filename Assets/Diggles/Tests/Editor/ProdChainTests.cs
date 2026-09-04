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

    // production-chain wave:
    //
    // P1 script PREPROCESSOR — the native loader FUN_006bb140 treats `//#` lines as
    // DIRECTIVES (IF/IFNOT/IFDEF/IFNDEF push a skip scope:365-376, ELSE/ELIF flip:402,
    // ENDIF pops:124-131, DEFINE/UNDEF mutate the symbol table:174/:217, STOPIF/
    // STOPIFNOT abort the file:256-281); non-directive lines reach the interpreter only
    // while not skipping (:407-409). The retail exe defines FULL. The port's TclParser
    // stripped ALL `//` lines as comments → both branches of techtreetunes.tcl's
    // `//# IF FULL … //# ELSE … //# ENDIF` executed and the DEMO list won by last-write:
    // tttitems_Hauklotz {Holzkiepe Leiter Farm} (:49) instead of the FULL 6-item list
    // (:47) — the "sawmill shows only 3 products" bug.
    //
    // P4 INVENTION QUALIFICATION — the kind-2 invention job is emitted ONLY while a
    // qualified own gnome exists (demand scan FUN_00434e60:529-564 walks the owner's
    // gnomes comparing every tttinvent pair vs the gnome's own attribs, emission gate
    //:587 →:621), and the gnome MATCH accepts only a qualified gnome
    // (FUN_00438210:88-102 → FUN_00437a40 rejects on any gnomeAttrib < required).
    //
    // P2/P5 CANCEL + BAR LAW — the job-release path FUN_0043a0c0 invalidates the place's
    // progress pair (+0x4c/+0x50 = -1,:37-38) with the claim (+0x24,:31); the window
    // bar (FUN_00520620:483-562) draws only on the bound current item's cell
    // (rec+0x48, bound at slot pick FUN_00436750:146-158) while its ORDER COUNT ≥ 1
    // (:491) — BLUE (solid) while the pair is invalid or the record hauls (+0x1c==3),
    // GREEN cur/max fill once the worker feeds info_progress_prod.
    //
    // P7 FARM CONTROL BYTES — set_prod_materialneed writes rec+0x41 (FUN_00696920:73-82;
    // the recipe check FUN_00434c20:22 and demand scan FUN_00434e60:301 gate on it);
    // set_prod_schedule writes rec+0x43 (FUN_00696670:73-82; the SP production section
    // FUN_00436750:70 runs only while set). The farm drives both every 3 s
    // (farm.tcl:156-158/:166/:232-234) and is a SWITCHMODE place (farm.tcl:119-120) —
    // orders are an on/off toggle, exclusive across slots.
    public sealed class ProdChainTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host(string? dataRoot = null)
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            if (dataRoot != null) ctx.DataRoot = dataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static EntityEvent? FirstEvent(DigglesTclEntity e, string name)
        {
            foreach (var ev in e.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }

        // ------------------------------------------------------------------
        // P1 — the //# preprocessor
        // ------------------------------------------------------------------

        [Test]
        public void Preprocessor_FullDefined_KeepsFullBranch_DropsDemoBranch()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "diggles_pp_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "pp.tcl"),
                    "set a none\n" +
                    "//# IF FULL\n" +
                    "set a full\n" +
                    "//# ELSE\n" +
                    "set a demo\n" +
                    "//# ENDIF\n" +
                    "//# IFNOT FULL\n" +
                    "set b demoonly\n" +
                    "//# ELSE\n" +
                    "set b fullonly\n" +
                    "//# ENDIF\n" +
                    "//# IFDEF _DEBUG\n" +
                    "set c dbg\n" +
                    "//# ELIF\n" +
                    "set c release\n" +
                    "//# ENDIF\n" +
                    "set d before\n" +
                    "//# STOPIFNOT SOME_UNDEFINED_SYMBOL\n" +
                    "set d after\n");
                var (interp, ctx, _) = Host(dir);
                interp.Eval("call pp.tcl");
                Assert.AreEqual("full", interp.Eval("set a").Trim(),
                    "IF FULL keeps the full branch (FULL is defined, saegewerk.tcl:1 law)");
                Assert.AreEqual("fullonly", interp.Eval("set b").Trim(),
                    "IFNOT FULL skips; ELSE flips back on (spinne.tcl:1-4 shape)");
                Assert.AreEqual("release", interp.Eval("set c").Trim(),
                    "IFDEF _DEBUG skips; ELIF flips (t_procs.tcl:22-28 shape)");
                Assert.AreEqual("before", interp.Eval("set d").Trim(),
                    "STOPIFNOT <undefined> aborts the rest of the file (FUN_006bb140:279-281)");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        // The REAL P1 pin: with the preprocessor, techtreetunes' Hauklotz section keeps the
        // FULL catalog (techtreetunes.tcl:46-50) — 6 items, not the 3-item demo list the
        // last-write clobber produced ("sawmill shows only rucksack/ladder/farm").
        [Test]
        public void Preprocessor_RealTechtree_HauklotzCatalogIsFull()
        {
            var (interp, _, _) = Host(DataRoot);
            interp.Eval("set tttsection_tocall Hauklotz");
            interp.Eval("call scripts/misc/techtreetunes.tcl");
            var items = TclList.Parse(interp.Eval("set tttitems_Hauklotz").Trim());
            Assert.AreEqual(new[] { "Holzkiepe", "Leiter", "Farm", "Brauerei", "Schreinerei", "Holztuer" },
                items.ToArray(),
                "tttitems_Hauklotz must be the FULL 6-item list (techtreetunes.tcl:47)");

            interp.Eval("set tttsection_tocall Steinmetz");
            interp.Eval("call scripts/misc/techtreetunes.tcl");
            var stein = TclList.Parse(interp.Eval("set tttitems_Steinmetz").Trim());
            Assert.AreEqual(7, stein.Count,
                "tttitems_Steinmetz = the FULL 7-item list (techtreetunes.tcl:63), not the 2-item demo one (:65)");
        }

        // ------------------------------------------------------------------
        // P4 — invention requires a qualified gnome
        // ------------------------------------------------------------------

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesTclEntity b) InventHost()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class BI none production 0 {} {\n"
                        + "method prod_items {} {return {Thing}}\n"
                        + "method prod_item_materials {item} {return {}}\n"
                        + "method prod_item_attribs {item} {return {{exp_Holz 0.5}}}\n}");
            interp.Eval("def_attrib BpThing 0 1 0");     // NOT invented
            interp.Eval("def_attrib exp_Holz 0 1 0");
            var b = ctx.GetEntity(interp.Eval("new BI \"\" \"33 30 8\" \"0 0 0\"").Trim())!;
            b.Owner = 0;
            b.BuildupComplete = true;
            b.ProdSlots["Thing"] = 1;
            return (interp, ctx, b);
        }

        static DigglesTclEntity Gnome(TclInterpreter interp, DigglesTclContext ctx, string pos)
        {
            var g = Spawn(interp, ctx, "G", pos);
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            return g;
        }

        [Test]
        public void Invention_NoQualifiedGnome_NeverDispatches()
        {
            var (interp, ctx, b) = InventHost();
            var g = Gnome(interp, ctx, "32 30 8");       // exp_Holz = 0 < 0.5 required
            var disp = ctx.ProdDispatcher!;
            for (int i = 0; i < 10; i++) { disp.OnGnomeIdle(g); disp.SweepNow(0); disp.DecayCooldowns(); }
            Assert.IsNull(FirstEvent(g, "evt_autoprod_workat"),
                "a RED invention (nobody meets exp_Holz 0.5) must not dispatch at all "
                + "(emission gate FUN_00434e60:587)");
        }

        [Test]
        public void Invention_QualifiedGnomeExists_OnlyItGetsTheJob()
        {
            var (interp, ctx, b) = InventHost();
            var near = Gnome(interp, ctx, "32 30 8");    // closer, UNQUALIFIED
            var far = Gnome(interp, ctx, "40 30 8");     // farther, qualified
            interp.Eval($"set_attrib {far.Handle} exp_Holz 0.6");
            var disp = ctx.ProdDispatcher!;
            disp.OnGnomeIdle(near);
            disp.OnGnomeIdle(far);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(near, "evt_autoprod_workat"),
                "the unqualified gnome must NOT be matched even though it is nearer "
                + "(FUN_00438210:88-102 → FUN_00437a40)");
            var ev = FirstEvent(far, "evt_autoprod_workat");
            Assert.IsNotNull(ev, "the qualified gnome takes the invention job");
            Assert.AreEqual("BpThing", ev!.Value.Payload!["-text1"]);
        }

        // ------------------------------------------------------------------
        // P2/P5 — cancel clears progress; the bar law (count gate + blue/green)
        // ------------------------------------------------------------------

        static void Tick(BottomMenuController c)
            => c.GetType().GetMethod("VContainer.Unity.ITickable.Tick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(c, null);

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesTclEntity b, DigglesTclEntity g) ProdHost()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            ctx.IsStoryMgr = true;
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class B none production 0 {} {\n"
                        + "method prod_items {} {return {Prod}}\n"
                        + "method prod_item_materials {item} {return {Mat Mat}}\n}");
            interp.Eval("def_class Mat none item 0 {} {}");
            interp.Eval("def_attrib BpProd 0 1 1");      // pre-invented
            var b = ctx.GetEntity(interp.Eval("new B \"\" \"33 30 8\" \"0 0 0\"").Trim())!;
            b.Owner = 0;
            b.BuildupComplete = true;
            b.ProdSlots["Prod"] = 1;
            var g = Gnome(interp, ctx, "32 30 8");
            return (interp, ctx, b, g);
        }

        [Test]
        public void WorkerRelease_InvalidatesProgressPair_AndClaim()
        {
            var (interp, ctx, b, g) = ProdHost();
            var disp = ctx.ProdDispatcher!;
            b.CurrentWorkerHandle = g.Handle;
            interp.Eval($"info_start_prod {b.Handle} Prod");
            interp.Eval($"info_progress_prod {b.Handle} 2 8");
            Assert.AreEqual(8, b.ProdProgressMax);

            disp.ReleaseGnome(g);   // gnome_stopped_work (the cancel's evt_zwerg_break lands here)
            Assert.AreEqual(-1, b.CurrentWorkerHandle, "claim released (FUN_0043a0c0:31)");
            Assert.AreEqual(0, b.ProdProgressMax,
                "the progress pair invalidates with the release (FUN_0043a0c0:37-38) — "
                + "the bar must not freeze at the dead worker's value");
            Assert.AreEqual("Prod", b.CurrentProdItem,
                "the bound current item survives the release (rec+0x48 is not touched)");
        }

        // The FULL bar state machine:
        // ordered (bound, no worker) → BLUE solid (pair invalid, FUN_00520620:492)
        // hauling (kind-4 plan into place) → BLUE solid (+0x1c==3,:493 — even with a
        // valid pair)
        // worker assigned, WALKING (far) → BLUE solid (native: the plan walk leg runs
        // BEFORE the workat emit,
        // FUN_004391f0:83→:100; port:
        // AtWorkplace gate)
        // worker AT the place, pair fed → GREEN cur/max (:526-562)
        // count 0 (cancel/done) → NO bar (:491)
        [Test]
        public void Bar_StateMachine_BlueEnRouteAndHauling_GreenOnlyWhileWorking()
        {
            var (interp, ctx, b, g) = ProdHost();
            var disp = ctx.ProdDispatcher!;
            var mat = Spawn(interp, ctx, "Mat", "45 30 8");   // one far source → a haul serves it
            mat.Owner = -1;
            var ctrl = new BottomMenuController(ctx);
            ctx.SelectedHandle = b.Handle;

            // ---- phase 1: ordered + haul dispatched. The slot pick binds the current item
            // (FUN_00436750:146-158) → SOLID BLUE, full-width (pct forced 0 = full back).
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.AreEqual("Prod", b.CurrentProdItem, "slot pick binds rec+0x48");
            Assert.IsTrue(disp.HasPlan(g.Handle), "precondition: the haul plan opened");
            Tick(ctrl);
            var cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.IsNotNull(cell);
            Assert.AreEqual(BottomMenuProgressKind.Delivering, cell!.ProgressKind,
                "invalid pair ⇒ BLUE (FUN_00520620:492-494)");
            Assert.AreEqual(100f, cell.Progress, "the blue bar is SOLID full-width — no partial fill");

            // ---- phase 2: a valid pair must NOT go green while the haul is live — the record
            // haul state (+0x1c==3, FUN_0043c660:97) forces blue regardless (:493). This is the
            // B bug (green during the sawmill delivery).
            interp.Eval($"info_start_prod {b.Handle} Prod");
            interp.Eval($"info_progress_prod {b.Handle} 1 4");
            ctx.SelectedHandle = 0; Tick(ctrl);
            ctx.SelectedHandle = b.Handle; Tick(ctrl);
            cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.AreEqual(BottomMenuProgressKind.Delivering, cell!.ProgressKind,
                "hauling ⇒ BLUE even with a valid pair (+0x1c==3 law)");
            Assert.AreEqual(100f, cell.Progress);

            // ---- phase 3: the haul dies → release invalidates the pair (FUN_0043a0c0:37-38);
            // a worker is assigned but still WALKING (outside the FUN_00437370 workplace box) —
            // the native workat emit re-invalidates the pair for exactly this leg
            // (FUN_0043a8a0:79-80) → BLUE. This is the A bug (premature green).
            disp.ReleaseGnome(g);
            Assert.AreEqual(0, b.ProdProgressMax, "release invalidates the pair");
            b.CurrentWorkerHandle = g.Handle;                 // workat claim (rec+0x24)
            g.Position = new Vector3(45f, 30f, 8f);           // 12 units away — walking
            interp.Eval($"info_start_prod {b.Handle} Prod");  // the Tcl feed arrives pre-walk in the port
            interp.Eval($"info_progress_prod {b.Handle} 1 2");
            ctx.SelectedHandle = 0; Tick(ctrl);
            ctx.SelectedHandle = b.Handle; Tick(ctrl);
            cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.AreEqual(BottomMenuProgressKind.Delivering, cell!.ProgressKind,
                "worker en route ⇒ BLUE (the native plan walk-leg phase)");
            Assert.AreEqual(100f, cell.Progress);

            // ---- phase 4: the worker ARRIVES (inside the |Δx|<4,|Δy|<2,|Δz|<4 box) → GREEN
            // with the cur/max fill (invention 1/2 = 50%).
            g.Position = new Vector3(32f, 30f, 8f);
            ctx.SelectedHandle = 0; Tick(ctrl);
            ctx.SelectedHandle = b.Handle; Tick(ctrl);
            cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.AreEqual(BottomMenuProgressKind.Producing, cell!.ProgressKind,
                "worker at the place + valid pair ⇒ GREEN fill (FUN_00520620:526-562)");
            Assert.AreEqual(50f, cell.Progress);

            // ---- phase 5: CANCEL — count → 0 hides the bar instantly (the:491 cell-count
            // gate), even though the record state has not been torn down yet.
            interp.Eval($"set_prod_slot_cnt {b.Handle} Prod 0");
            ctx.SelectedHandle = 0; Tick(ctrl);
            ctx.SelectedHandle = b.Handle; Tick(ctrl);
            cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.IsNotNull(cell, "the catalog cell itself stays");
            Assert.AreEqual(BottomMenuProgressKind.None, cell!.ProgressKind,
                "order count 0 ⇒ NO bar (FUN_00520620:491) — the stale-bar-after-cancel bug");
        }

        // The workat DISPATCH re-invalidates the progress pair (step code 9 emit,
        // FUN_0043a8a0:79-80) — a stale pair from an interrupted previous cycle must not
        // paint the new worker's walk leg green.
        [Test]
        public void WorkatDispatch_InvalidatesStaleProgressPair()
        {
            var (interp, ctx, b, g) = ProdHost();
            var disp = ctx.ProdDispatcher!;
            // Recipe {Mat Mat} satisfied IN the place inventory → the workat dispatches.
            var m1 = Spawn(interp, ctx, "Mat", "33 30 8");
            var m2 = Spawn(interp, ctx, "Mat", "33 30 8");
            m1.ContainedIn = b.Handle; m2.ContainedIn = b.Handle;
            b.Inventory.Add(m1.Handle); b.Inventory.Add(m2.Handle);
            b.ProdProgressCur = 3; b.ProdProgressMax = 8;     // stale pair from a dead cycle
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g, "evt_autoprod_workat"), "precondition: workat dispatched");
            Assert.AreEqual(g.Handle, b.CurrentWorkerHandle, "the dispatch claims the place (rec+0x24)");
            Assert.AreEqual(0, b.ProdProgressMax,
                "the workat emit invalidates the pair (FUN_0043a8a0:79-80) — blue during the walk");
        }

        // ------------------------------------------------------------------
        // Crate content icon — front-face placement
        // ------------------------------------------------------------------

        // The boxed-crate icon must sit FLAT ON THE FRONT FACE of the crate (user-spec contract
        //), world-axis aligned, NOT underground: under the Q_x(-90) WorldRoot the
        // round-1 offset (0,0,-1.1) pointed local −z = world DOWN — the icon rendered a unit
        // below the ground. Front = the world −Z (camera-side) face of the model bounds.
        [Test]
        public void BoxContentIcon_OnCrateFrontFace_NotUnderground_NoBillboard()
        {
            var root = new GameObject("WorldRoot");
            try
            {
                root.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
                var owner = new GameObject("Crate");
                owner.transform.SetParent(root.transform, false);
                owner.transform.localPosition = new Vector3(246f, 11f, -30f);
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);   // the kiste stand-in
                body.transform.SetParent(owner.transform, false);

                Diggles.Runtime.BoxContentIcon.Attach(owner, "Zelt", DataRoot);
                var icon = owner.transform.Find("BoxContentIcon");
                Assert.IsNotNull(icon, "the icon child attaches (data/gui/icons/Zelt.tga)");
                var b = body.GetComponent<Renderer>().bounds;
                Assert.AreEqual(b.center.y, icon!.position.y, 0.05f,
                    "icon centered on the crate FACE — not sunk below it (world +Y = up)");
                Assert.AreEqual(b.min.z, icon.position.z, 0.06f,
                    "icon lies ON the front (world −Z, camera-side) face plane");
                Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, icon.rotation), 0.01f,
                    "flat world-aligned quad — no camera billboard");
                Assert.IsNull(icon.GetComponent<UnityEngine.SpriteRenderer>()!.GetComponent("CameraFacingBillboard"),
                    "the billboard component is gone");

                Diggles.Runtime.BoxContentIcon.Detach(owner);
                Assert.IsNull(owner.transform.Find("BoxContentIcon"), "unbox clears the icon (FUN_005dc6a0)");
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ------------------------------------------------------------------
        // P7 — materialneed / prod_schedule bytes + the switchmode toggle
        // ------------------------------------------------------------------

        // P2 wedge repro: a gnome whose STEP PLAN is live must keep advancing it on idle
        // reports even OUTSIDE its work window — the native one-shot FUN_00434bc0:12-21
        // advances an existing plan unconditionally (the window/auto-schedule gates only guard
        // POOL ENTRY for new work, FUN_00433a70:112). The old ordering (gate before advance)
        // froze the plan forever: destination claimed, workplace dead, bar stuck.
        [Test]
        public void HaulPlan_AdvancesOutsideWorkWindow_NoWedge()
        {
            var (interp, ctx, b, g) = ProdHost();
            var mat = Spawn(interp, ctx, "Mat", "45 30 8");
            mat.Owner = -1;
            var disp = ctx.ProdDispatcher!;
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsTrue(disp.HasPlan(g.Handle), "precondition: the haul plan opened");

            // the work window slams shut mid-plan (the shift ended while carrying)
            interp.Eval($"set_worktime {g.Handle} 0 0");
            g.PendingEvents.Clear();
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g);
            Assert.IsNotNull(FirstEvent(g, "evt_autoprod_pickup"),
                "the plan must ADVANCE on the idle report even off-shift (FUN_00434bc0 has no "
                + "window gate) — otherwise it wedges with the destination claimed forever");
        }

        [Test]
        public void MaterialNeedOff_WorkatUngated_NoHaulNoChop()
        {
            var (interp, ctx, b, g) = ProdHost();       // recipe {Mat Mat}, NO Mat anywhere
            var disp = ctx.ProdDispatcher!;
            interp.Eval($"set_prod_materialneed {b.Handle} 0");   // farm.tcl:118/:157 law
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_workat");
            Assert.IsNotNull(ev,
                "materialneed 0 ⇒ the recipe check is bypassed (FUN_00434c20:22 gate on rec+0x41) "
                + "and the workat dispatches with an empty place inventory");
            Assert.AreEqual("Prod", ev!.Value.Payload!["-text1"]);
            Assert.IsFalse(disp.HasPlan(g.Handle), "…and no haul plan was opened");
        }

        [Test]
        public void ProdScheduleOff_NoProductionDispatch()
        {
            var (interp, ctx, b, g) = ProdHost();
            var m1 = Spawn(interp, ctx, "Mat", "33 30 8");
            var m2 = Spawn(interp, ctx, "Mat", "33 30 8");
            interp.Eval($"inv_add {b.Handle} {m1.Handle}");
            interp.Eval($"inv_add {b.Handle} {m2.Handle}");
            interp.Eval($"set_prod_schedule {b.Handle} 0");       // farm.tcl:158 (breeding running)
            var disp = ctx.ProdDispatcher!;
            for (int i = 0; i < 6; i++) { disp.OnGnomeIdle(g); disp.SweepNow(0); disp.DecayCooldowns(); }
            Assert.IsNull(FirstEvent(g, "evt_autoprod_workat"),
                "prod_schedule 0 ⇒ the production section is skipped (FUN_00436750:70 rec+0x43)");
            interp.Eval($"set_prod_schedule {b.Handle} 1");       // farm.tcl:166 ("send me a gnome")
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g, "evt_autoprod_workat"), "re-armed ⇒ dispatches again");
        }

        [Test]
        public void Switchmode_TogglesExclusive_NoCountBadge()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            ctx.IsStoryMgr = true;
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class F none production 0 {} {\n"
                        + "method prod_items {} {return {Hamster Pilz}}\n"
                        + "method prod_item_materials {item} {return {}}\n}");
            interp.Eval("def_attrib BpHamster 0 1 1");
            interp.Eval("def_attrib BpPilz 0 1 1");
            var f = ctx.GetEntity(interp.Eval("new F \"\" \"33 30 8\" \"0 0 0\"").Trim())!;
            f.Owner = 0;
            f.BuildupComplete = true;
            interp.Eval($"set_prod_switchmode {f.Handle} 1");     // farm.tcl:119
            interp.Eval($"set_prod_exclusivemode {f.Handle} 1");  // farm.tcl:120
            var ctrl = new BottomMenuController(ctx);
            ctx.SelectedHandle = f.Handle;

            ctrl.Click("prod:Hamster", rightButton: false);
            // Native switch-mode arms with the SENTINEL 10, not 1 (FUN_005242b0:232-255;
            // info_end_prod preserves it, FUN_0069e210:82-96) — the farm-cascade law.
            Assert.AreEqual("10", interp.Eval($"get_prod_slot_cnt {f.Handle} Hamster").Trim(),
                "switchmode click = toggle ON (native sentinel 10)");
            ctrl.Click("prod:Pilz", rightButton: false);
            Assert.AreEqual("10", interp.Eval($"get_prod_slot_cnt {f.Handle} Pilz").Trim());
            Assert.AreEqual("0", interp.Eval($"get_prod_slot_cnt {f.Handle} Hamster").Trim(),
                "exclusivemode: turning Pilz on turns Hamster off (one species at a time, "
                + "farm.tcl:160-168 current_slot law)");

            Tick(ctrl);
            var cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Pilz");
            Assert.IsNotNull(cell);
            Assert.IsTrue(cell!.Toggle, "switchmode cells render as toggles (green lamp), not counts");
            Assert.IsTrue(cell.ToggledOn);
            Assert.AreEqual(-1, cell.Count, "no count badge in switchmode");

            ctrl.Click("prod:Pilz", rightButton: false);
            Assert.AreEqual("0", interp.Eval($"get_prod_slot_cnt {f.Handle} Pilz").Trim(),
                "clicking the active toggle turns it off");
        }
    }
}
