#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.UI;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The production MATERIAL GATE + the kind-4 HAUL family (wave; decompile RE in
    // the ProdDispatcher headers):
    //
    // • The workat (kind-3) job is GATED on the recipe check FUN_00434c20 (called from the SP
    // production section FUN_00436750:164): every recipe material (the class method
    // prod_item_materials = techtreetunes tttmaterial_*, MULTISET — duplicates encode
    // quantity) must already be IN THE PLACE'S INVENTORY. Nothing else counts — no world
    // stock, no reachability (FUN_00434c20:74-103).
    // • Missing materials SUPPRESS the workat entirely and spawn the kind-4 haul family
    // instead (FUN_00436750:216-279 demand upsert → FUN_004375c0 one kind-4 job per missing
    // entry {pos=place, +4=place, +8=concrete source item}). A material with NO eligible
    // source item never becomes a job — missing AND unreachable ⇒ the building starves.
    // • The kind-4 STEP PLAN (FUN_0043c660): muetze("transport") → walk(item) → pickup(item)
    // → walk(place) → transferprod(item, place) — dispatched one step per gnome-idle
    // round-trip; the place is CLAIMED for the whole trip (rec+0x1c=3 + rec+0x24,:97-102).
    // • Materials already LYING AT the place (|Δx|<4, |Δy|<2, |Δz|<4 — DAT_007374a0/
    // DAT_00737330) short-circuit: FUN_00437370 queues `set_event <place>
    // evt_prodplace_transferprod -subject1 <item>` — the building absorbs them itself.
    // • RECORD-BUSY dedup (FUN_00436440:31 rec+0x1c==0 + FUN_00438b80 plan scan): a place
    // whose order is in service contributes NOTHING, even with a request byte re-armed —
    // the double-builder symptom.
    // • set_boxed side effects (FUN_0067dc80:139-163): box → built bit + PACK byte clear;
    // unbox → UNPACK byte clear (a dangling re-click order dies at placement).
    // • info_start_prod / info_progress_prod <place> <cur> <max> store the RAW step pair
    // (native CProd+0x4c/+0x50, FUN_0069def0:139-141); the UI derives
    // clamp(cur*100/max, 0, 100) (FUN_00520620:548-562) — surfaced as
    // BottomMenuEntry.Progress + ProgressKind (Producing / Delivering).
    public sealed class ProdMaterialHaulTests
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
            // A production class with the genericprod-shaped recipe/catalog methods: one product
            // "Prod" needing TWO "Mat" (multiset — duplicates encode quantity).
            interp.Eval("def_class B none production 0 {} {\n"
                        + "method prod_items {} {return {Prod}}\n"
                        + "method prod_item_materials {item} {return {Mat Mat}}\n}");
            interp.Eval("def_class Mat none item 0 {} {}");
            interp.Eval("def_attrib BpProd 0 1 1");   // pre-invented (genericprod.tcl:151 law)
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

        static EntityEvent? FirstEvent(DigglesTclEntity e, string name)
        {
            foreach (var ev in e.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }

        static int CountEvents(DigglesTclEntity e, string name)
        {
            int n = 0;
            foreach (var ev in e.PendingEvents)
                if (ev.Name == name) n++;
            return n;
        }

        // ------------------------------------------------------------------
        // 1. THE GATE — missing AND unreachable ⇒ NO dispatch of any kind
        // ------------------------------------------------------------------

        [Test]
        public void Gate_MaterialsMissingAndUnreachable_NothingDispatches()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            for (int i = 0; i < 10; i++) { disp.OnGnomeIdle(g); disp.SweepNow(0); disp.DecayCooldowns(); }
            Assert.IsNull(FirstEvent(g, "evt_autoprod_workat"),
                "workat must be SUPPRESSED while recipe materials are missing (FUN_00434c20 gate)");
            Assert.IsNull(FirstEvent(g, "evt_change_muetze"),
                "no haul either — a missing material with NO source item never builds a job");
            Assert.IsFalse(disp.HasPlan(g.Handle));
        }

        [Test]
        public void Gate_InventoryComplete_WorkatDispatches()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var m1 = Spawn(interp, ctx, "Mat", "33 30 8");
            var m2 = Spawn(interp, ctx, "Mat", "33 30 8");
            interp.Eval($"inv_add {b.Handle} {m1.Handle}");
            interp.Eval($"inv_add {b.Handle} {m2.Handle}");
            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_workat");
            Assert.IsNotNull(ev, "with the full recipe in the PLACE INVENTORY the workat dispatches (kind-3)");
            Assert.AreEqual(b.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.AreEqual("Prod", ev.Value.Payload!["-text1"]);
        }

        // Quantity is MULTISET-exact: recipe {Mat Mat} with only ONE Mat in the inventory is
        // still missing (one item satisfies one entry, FUN_00434c20:83-95).
        [Test]
        public void Gate_PartialInventory_StillGated()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var m1 = Spawn(interp, ctx, "Mat", "33 30 8");
            interp.Eval($"inv_add {b.Handle} {m1.Handle}");
            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g, "evt_autoprod_workat"),
                "one of two Mat entries satisfied — the slot is still material-gated");
        }

        // ------------------------------------------------------------------
        // 2. THE HAUL — ONE BATCHED trip (FUN_0043c660:89-92,:121-188): muetze(transport) →
        // pickup(m1) → pickup(m2) → transferprod(m1) → transferprod(m2), then the workat.
        // The two Mat jobs are emitted per missing entry (FUN_004375c0) and fold into a
        // single carrier trip: both items lie within weighted dist² < 40 of each other and
        // serve the same destination; capacity = invMax(6) − 2 − held (wave —
        // the earlier one-item-per-trip port divergence is GONE, manual test P3).
        // ------------------------------------------------------------------

        [Test]
        public void Haul_BatchesBothMaterialsIntoOneTrip_ThenWorkatDispatches()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            // far outside the absorb box (Δx=12): a real carrier trip is required
            var m1 = Spawn(interp, ctx, "Mat", "45 30 8");
            var m2 = Spawn(interp, ctx, "Mat", "45 31 8");
            var g = Gnome(interp, ctx, "32 30 8");
            var g2 = Gnome(interp, ctx, "31 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var mu = FirstEvent(g, "evt_change_muetze");
            Assert.IsNotNull(mu, "the kind-4 plan opens with the muetze step (FUN_0043c660:189)");
            Assert.AreEqual("transport", mu!.Value.Payload!["-text1"],
                "the transport carrier hat (FUN_00431f20(\"transport\"))");
            Assert.IsNull(FirstEvent(g, "evt_autoprod_workat"), "no workat while materials are missing");
            Assert.IsTrue(disp.HasPlan(g.Handle));
            Assert.AreEqual(g.Handle, b.CurrentWorkerHandle,
                "the haul dispatch claims the destination place (FUN_0043c660:97-102)");

            // a second gnome gets NOTHING while the place is claimed (record-busy dedup)
            disp.OnGnomeIdle(g2);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g2, "evt_change_muetze"), "one carrier per place");

            // pickup phase: TWO pickup steps back-to-back (batched trip)
            var picked = new List<int>();
            for (int k = 0; k < 2; k++)
            {
                g.PendingEvents.Clear();
                disp.DecayCooldowns(); disp.DecayCooldowns();
                disp.OnGnomeIdle(g);
                var pk = FirstEvent(g, "evt_autoprod_pickup");
                Assert.IsNotNull(pk, $"pickup step {k} of the batched trip");
                Assert.AreEqual("Mat", pk!.Value.Payload!["-text1"]);
                int itemHandle = int.Parse(pk.Value.Payload!["-subject1"]);
                Assert.IsFalse(picked.Contains(itemHandle), "each pickup targets a DISTINCT item");
                picked.Add(itemHandle);
                interp.Eval($"inv_add {g.Handle} {itemHandle}");   // the pickup leg lands it
            }

            // transfer phase: TWO transferprod steps, same order, all into b
            for (int k = 0; k < 2; k++)
            {
                g.PendingEvents.Clear();
                disp.DecayCooldowns(); disp.DecayCooldowns();
                disp.OnGnomeIdle(g);
                var tr = FirstEvent(g, "evt_autoprod_transferprod");
                Assert.IsNotNull(tr, $"transfer step {k} of the batched trip");
                Assert.AreEqual(b.Handle.ToString(), tr!.Value.Payload!["-subject1"]);
                Assert.AreEqual(picked[k].ToString(), tr.Value.Payload!["-subject2"],
                    "transfers run in pickup order (FUN_0043c660:258-273 primary, :282+ riders)");
                interp.Eval($"inv_rem {g.Handle} {picked[k]}");
                interp.Eval($"inv_add {b.Handle} {picked[k]}");
            }

            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g);                       // trip over → the plan drops
            Assert.IsFalse(disp.HasPlan(g.Handle), "the finished batched plan is dropped");
            Assert.AreEqual(-1, b.CurrentWorkerHandle, "the claim dies with the plan");

            // both materials delivered in ONE trip → the workat dispatches
            g.PendingEvents.Clear();
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_workat");
            Assert.IsNotNull(ev, "full recipe in the place inventory → the kind-3 workat dispatches");
            Assert.AreEqual("Prod", ev!.Value.Payload!["-text1"]);
        }

        // ------------------------------------------------------------------
        // 3. THE ABSORB — a material already lying at the place needs no gnome
        // ------------------------------------------------------------------

        [Test]
        public void Absorb_MaterialWithinBox_PlaceTakesItDirectly()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var near = Spawn(interp, ctx, "Mat", "35 30 8");   // Δx=2 < 4, Δy=0 < 2, Δz=0 < 4
            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var ev = FirstEvent(b, "evt_prodplace_transferprod");
            Assert.IsNotNull(ev, "in-box material → evt_prodplace_transferprod on the PLACE (FUN_00437370)");
            Assert.AreEqual(near.Handle.ToString(), ev!.Value.Payload!["-subject1"]);
            Assert.IsFalse(disp.HasPlan(g.Handle), "no carrier trip for an in-box material");
            Assert.IsNull(FirstEvent(g, "evt_autoprod_workat"), "and the workat stays gated meanwhile");
        }

        // ------------------------------------------------------------------
        // 4. RE-CLICKED BUILD ORDER — exactly ONE gnome
        // ------------------------------------------------------------------

        [Test]
        public void ReclickedBuildOrder_WhilePending_DispatchesExactlyOneGnome()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var g1 = Gnome(interp, ctx, "20 20 8");
            var g2 = Gnome(interp, ctx, "21 20 8");
            var disp = ctx.ProdDispatcher!;
            var pc = ctx.Placement!;

            // click 1: the deferred build order
            Assert.IsTrue(pc.OrderBuild(crate.Handle));
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(crate.UnpackRequested);

            disp.OnGnomeIdle(g1);
            disp.SweepNow(0);
            Assert.IsNotNull(FirstEvent(g1, "evt_change_muetze"), "g1 takes the order (kind-1 plan)");
            Assert.AreEqual(g1.Handle, crate.CurrentWorkerHandle);

            // click 2 WHILE PENDING: the native confirm just rewrites the record (FUN_005ee250)
            // — the re-armed byte must NOT dispatch a second gnome while the record is busy
            // (FUN_00436440:31 rec+0x1c==0 gate).
            Assert.IsTrue(pc.OrderBuild(crate.Handle));
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(crate.UnpackRequested, "the re-click re-arms the byte (idempotent record rewrite)");

            for (int i = 0; i < 10; i++)
            {
                disp.OnGnomeIdle(g2);
                disp.SweepNow(0);
                disp.DecayCooldowns();
            }
            Assert.IsNull(FirstEvent(g2, "evt_change_muetze"), "no second carrier while the order is in service");
            Assert.IsNull(FirstEvent(g2, "evt_autoprod_pickup"), "…no pickup leg either");
            Assert.IsNull(FirstEvent(g2, "evt_autoprod_unpack"), "…and no unpack");
            Assert.AreEqual(g1.Handle, crate.CurrentWorkerHandle, "the claim survives the plan's idle gaps");

            // g1's plan advances normally (pickup → carried → unpack emit)
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            Assert.IsNotNull(FirstEvent(g1, "evt_autoprod_pickup"));
            interp.Eval($"inv_add {g1.Handle} {crate.Handle}");
            disp.DecayCooldowns(); disp.DecayCooldowns();
            disp.OnGnomeIdle(g1);
            Assert.IsNotNull(FirstEvent(g1, "evt_autoprod_unpack"));

            // placement opens the crate: prod_buildup_new runs `set_boxed $item 0`
            // (z_work_common.tcl:408) — the UNPACK byte dies with the unbox
            // (FUN_0067dc80:144-148), so the re-clicked order can never dangle into a
            // second dispatch after the build starts.
            interp.Eval($"set_boxed {crate.Handle} 0");
            Assert.IsFalse(crate.UnpackRequested, "unboxing clears the unpack order byte (+0x28)");

            for (int i = 0; i < 10; i++)
            {
                disp.OnGnomeIdle(g2);
                disp.SweepNow(0);
                disp.DecayCooldowns();
            }
            Assert.IsNull(FirstEvent(g2, "evt_change_muetze"), "still no second gnome after placement");
            Assert.IsNull(FirstEvent(g2, "evt_autoprod_unpack"), "…and no buildup rider");
        }

        [Test]
        public void SetBoxed_True_ClearsPackByteAndBuiltBit()
        {
            var (interp, ctx, _) = Host();
            var b = Building(interp, ctx, "33 30 8", slot: 0);
            b.PackRequested = true;
            interp.Eval($"set_boxed {b.Handle} 1");
            Assert.IsFalse(b.BuildupComplete, "boxing clears the built bit (FUN_0067dc80:157 & ~0x400000)");
            Assert.IsFalse(b.PackRequested, "boxing clears the PACK byte (+0x29, FUN_0067dc80:158-161)");
        }

        // ------------------------------------------------------------------
        // 5. buildable flag + progress surface
        // ------------------------------------------------------------------

        [Test]
        public void SlotBuildable_TracksWorldAndInventoryStock()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");

            Assert.AreEqual("0", interp.Eval($"get_prod_slot_buildable {b.Handle} Prod").Trim(),
                "no Mat anywhere → not buildable");

            var m1 = Spawn(interp, ctx, "Mat", "45 30 8");
            Assert.AreEqual("0", interp.Eval($"get_prod_slot_buildable {b.Handle} Prod").Trim(),
                "one of two needed → still not buildable");

            var m2 = Spawn(interp, ctx, "Mat", "45 31 8");
            Assert.AreEqual("1", interp.Eval($"get_prod_slot_buildable {b.Handle} Prod").Trim(),
                "two world sources cover the {Mat Mat} recipe");

            interp.Eval($"inv_add {b.Handle} {m1.Handle}");
            interp.Eval($"inv_add {b.Handle} {m2.Handle}");
            Assert.AreEqual("1", interp.Eval($"get_prod_slot_buildable {b.Handle} Prod").Trim(),
                "inventory stock counts too");
        }

        [Test]
        public void InfoProgressProd_StoresRawPair_AndEndClears()
        {
            var (interp, ctx, _) = Host();
            var b = Building(interp, ctx, "33 30 8", slot: 0);

            interp.Eval($"info_start_prod {b.Handle} Prod");
            Assert.AreEqual("Prod", b.CurrentProdItem);
            Assert.AreEqual(0, b.ProdProgressPct);

            interp.Eval($"info_progress_prod {b.Handle} 2 8");
            Assert.AreEqual(2, b.ProdProgressCur);
            Assert.AreEqual(8, b.ProdProgressMax);
            Assert.AreEqual(25, b.ProdProgressPct, "pct = clamp(cur*100/max) — FUN_00520620:548-562");

            interp.Eval($"info_progress_prod {b.Handle} 8 8");
            Assert.AreEqual(100, b.ProdProgressPct);

            interp.Eval($"info_end_prod {b.Handle}");
            Assert.AreEqual("", b.CurrentProdItem, "info_end_prod resets the surface (CProd+0x48/-1, +0x4c/+0x50 = 0)");
            Assert.AreEqual(0, b.ProdProgressMax);

            // invention orders surface as their product class ("Bp" stripped)
            interp.Eval($"info_start_prod {b.Handle} BpProd");
            Assert.AreEqual("Prod", b.CurrentProdItem);
        }

        // The controller implements VContainer.Unity.ITickable explicitly; the test asmdef has no
        // VContainer reference, so drive the tick via reflection.
        static void Tick(BottomMenuController c)
            => c.GetType().GetMethod("VContainer.Unity.ITickable.Tick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(c, null);

        [Test]
        public void BottomMenu_SurfacesProducingAndDeliveringProgress()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            ctx.IsStoryMgr = true;
            var b = Building(interp, ctx, "33 30 8");
            var g = Gnome(interp, ctx, "32 30 8");
            var far = Spawn(interp, ctx, "Mat", "45 30 8");
            var disp = ctx.ProdDispatcher!;
            var ctrl = new BottomMenuController(ctx);
            ctx.SelectedHandle = b.Handle;

            // Delivering: a live haul plan into b for the Prod cell → SOLID full blue (the
            // native blue back with pct forced 0, FUN_00520620:492-524 — no partial fill;
            // law B).
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsTrue(disp.HasPlan(g.Handle), "precondition: a haul plan is active");
            Tick(ctrl);
            var cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.IsNotNull(cell, "the product cell must exist (get_prod_slot_list via prod_items)");
            Assert.AreEqual(BottomMenuProgressKind.Delivering, cell!.ProgressKind);
            Assert.AreEqual(100f, cell.Progress, "the blue delivering bar is solid full-width");

            // Producing: the haul is over (release invalidates the pair, FUN_0043a0c0:37-38),
            // a worker is claimed AT the place and feeds info_progress_prod → green cur/max.
            // Force an immediate rebuild by bouncing the selection (the 4 Hz refresh window
            // would swallow a same-frame re-tick).
            disp.ReleaseGnome(g);
            b.CurrentWorkerHandle = g.Handle;   // workat claim; g at 32 vs b at 33 → arrived
            interp.Eval($"info_start_prod {b.Handle} Prod");
            interp.Eval($"info_progress_prod {b.Handle} 1 4");
            ctx.SelectedHandle = 0;
            Tick(ctrl);
            ctx.SelectedHandle = b.Handle;
            Tick(ctrl);
            cell = ctrl.Model.Entries.Find(x => x.Id == "prod:Prod");
            Assert.IsNotNull(cell);
            Assert.AreEqual(BottomMenuProgressKind.Producing, cell!.ProgressKind,
                "worker at the place + valid pair ⇒ green (the delivering phase is over)");
            Assert.AreEqual(25f, cell.Progress);
        }

        // ------------------------------------------------------------------
        // 6. get_class_type resolves CLASS NAMES (the boxed-product gate)
        // ------------------------------------------------------------------

        [Test]
        public void GetClassType_ResolvesClassName()
        {
            var (interp, _, _) = Host();
            // native: TCL:CTclGlobalMethods::GetClassType (FUN_0063fb30) takes a class NAME.
            // Every shipped caller passes one — feuerstelle.tcl:19 gates the BOXED product path
            // on `lsearch $BOXED_CLASSES [get_class_type $item]`; the old handle-only resolve
            // returned "" → an ordered Zelt spawned BUILT instead of as a packed crate.
            Assert.AreEqual("production", interp.Eval("get_class_type B").Trim());
            Assert.AreEqual("item", interp.Eval("get_class_type Mat").Trim());
        }

        // ------------------------------------------------------------------
        // 7. DELIVERED MATERIALS lie VISIBLE in front, LOCKED and inert (spec
        //). prod_transfertoprod (z_work_common.tcl:51-56): inv_add $prodplace +
        // set_pos to the delivery spot + set_visibility 1 + hoverable/selectable 0 — and the
        // item KEEPS the pickup's set_lock 1 (lock_item, z_procs.tcl:486; only the
        // current_lock_obj VAR is cleared,:57-59). Containment + the flags make it a
        // non-target everywhere: world obj_query skips contained, ClickToMoveService skips
        // _hoverable==0 (and inv_add turned its colliders off), the haul-source scan skips
        // contained AND locked. Release = release_item (genericprod.tcl:520-537) via
        // free_unneeded_items / stop_production.
        // ------------------------------------------------------------------

        [Test]
        public void DeliveredMaterial_LiesVisibleInFront_LockedAndInert()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            var b = Building(interp, ctx, "33 30 8");
            var g = Gnome(interp, ctx, "32 30 8");
            var mat = Spawn(interp, ctx, "Mat", "40 30 8");

            // the carry leg: pickup locks + pockets the item (lock_item + take_item)
            interp.Eval($"set_lock {mat.Handle} 1");
            interp.Eval($"inv_add {g.Handle} {mat.Handle}");
            // the delivery writes — EXACTLY what prod_transfertoprod does (z_work_common.tcl:51-56)
            interp.Eval($"inv_rem {g.Handle} {mat.Handle}");
            interp.Eval($"inv_add {b.Handle} {mat.Handle}");
            interp.Eval($"set_hoverable {mat.Handle} 0");
            interp.Eval($"set_selectable {mat.Handle} 0");
            interp.Eval($"set_visibility {mat.Handle} 1");
            interp.Eval($"set_pos {mat.Handle} {{33 30 9.5}}");   // the get_place front spot (depth band z+1..+3)

            Assert.AreEqual(b.Handle, mat.ContainedIn, "logically the building's stock");
            Assert.AreEqual(new Vector3(33f, 30f, 9.5f), mat.Position, "visually on the ground in front");
            Assert.AreEqual("1", interp.Eval($"get_visibility {mat.Handle}").Trim(), "stays rendered");
            Assert.AreEqual("0", interp.Eval($"get_hoverable {mat.Handle}").Trim(), "cursor-transparent");
            Assert.AreEqual("1", interp.Eval($"get_lock {mat.Handle}").Trim(),
                "keeps the pickup's lock — not a valid target until released");

            // the material gate COUNTS it as stock: one of the two Mat entries is satisfied
            var disp = ctx.ProdDispatcher!;
            Assert.AreEqual(1, disp.MissingMaterials(b, "Prod").Count,
                "the delivered item satisfies one recipe entry");

            // and it is NOT a haul source: with no other Mat in the world, nothing dispatches
            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            Assert.IsNull(FirstEvent(g, "evt_change_muetze"),
                "a delivered (contained+locked) item must never be re-hauled");
        }

        // ------------------------------------------------------------------
        // 8. INVENTION ORDER — an ordered slot whose Bp<item> owner attrib is unset dispatches
        // the INVENTION workat (-text1 Bp<item>, the native task code 8, FUN_0043a8a0:96 —
        // produce is code 9 with the plain class,:84), with NO material gate
        // (prod_getitemtask's Bp branch never calls prod_item_actions, genericprod.tcl:
        // 705-721). The unchanged Tcl ends in add_owner_attrib <owner> Bp<item> 1
        // (prod_inventattribute, z_work_common.tcl:8-32); the order SURVIVES (slot only
        // zeroed in switchmode,:28-30) and rolls into a real production on the next sweep.
        // ------------------------------------------------------------------

        [Test]
        public void InventionOrder_DispatchesBpWorkat_ThenRollsIntoProduction()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            interp.Eval("def_class B2 none production 0 {} {\n"
                        + "method prod_items {} {return {Prod2}}\n"
                        + "method prod_item_materials {item} {return {Mat}}\n}");
            interp.Eval("def_attrib BpProd2 0 1 0");            // NOT invented (default 0)
            var b = Spawn(interp, ctx, "B2", "33 30 8");
            b.Owner = 0; b.BuildupComplete = true;
            b.ProdSlots["Prod2"] = 1;
            var m = Spawn(interp, ctx, "Mat", "33 30 8");
            interp.Eval($"inv_add {b.Handle} {m.Handle}");      // recipe already in the place
            var g = Gnome(interp, ctx, "32 30 8");
            var disp = ctx.ProdDispatcher!;

            disp.OnGnomeIdle(g);
            disp.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_workat");
            Assert.IsNotNull(ev, "an ordered UNINVENTED slot must dispatch the invention workat "
                + "(the old gate skipped it entirely)");
            Assert.AreEqual("BpProd2", ev!.Value.Payload!["-text1"],
                "the invent marker rides -text1 (FUN_0043a8a0:96)");
            Assert.AreEqual(b.Handle.ToString(), ev.Value.Payload!["-subject1"]);

            // The unchanged Tcl chain's completion write:
            interp.Eval("add_owner_attrib 0 BpProd2 1");
            g.PendingEvents.Clear();
            for (int k = 0; k < 3; k++) disp.DecayCooldowns();
            disp.OnGnomeIdle(g);                                 // idle report dissolves the claim
            disp.SweepNow(0);
            ev = FirstEvent(g, "evt_autoprod_workat");
            Assert.IsNotNull(ev, "the SURVIVING order must roll into a real production");
            Assert.AreEqual("Prod2", ev!.Value.Payload!["-text1"],
                "after Bp lands the same slot produces under the plain class");
        }

        // The native window follows every production button with evt_btn_on on the building
        // ('set_event %i evt_btn_on -target %i -num1 0 -text1 <verb> -text2 <item>',
        // FUN_005235a0:44 ← the prodincrement/decrement handlers FUN_005242b0:351/:472) —
        // genericprod's handler (genericprod.tcl:33-52) is the order-cancel law: break the
        // working cook when the slot fell, release unneeded delivered materials when idle.
        [Test]
        public void PanelClick_SlotChange_RaisesEvtBtnOn()
        {
            var (interp, ctx, _) = Host();
            ctx.LocalPlayer = 0;
            ctx.IsStoryMgr = true;
            var b = Building(interp, ctx, "33 30 8");
            ctx.SelectedHandle = b.Handle;
            var ctrl = new BottomMenuController(ctx);

            ctrl.Click("prod:Prod", rightButton: true);   // proddecrement 1 → 0
            Assert.AreEqual("0", interp.Eval($"get_prod_slot_cnt {b.Handle} Prod").Trim());
            var ev = FirstEvent(b, "evt_btn_on");
            Assert.IsNotNull(ev, "every slot change must raise evt_btn_on on the building (FUN_005235a0)");
            Assert.AreEqual("proddecrement", ev!.Value.Payload!["-text1"]);
            Assert.AreEqual("Prod", ev.Value.Payload!["-text2"]);
        }
    }

    // ---------------------------------------------------------------------
    // The REAL-DATA end-to-end: order a Zelt at a built Feuerstelle with the ingredients ABSENT
    // from the workplace — the dispatcher hauls them in, the cook produces, and the product
    // appears as a PACKED CRATE (Zelt is class type "production" ∈ BOXED_CLASSES →
    // feuerstelle.tcl:44-54 boxed path → prod_createproduct_inv_boxed → packtobox → set_boxed 1).
    //
    // Terrain = the REAL campaign start meadow (map create + urw_unq_start template, the
    // SparetimeBehaviorTests camp-cluster harness): the delivery legs run get_place against the
    // nav world (prod_bringtoprod, z_work_common.tcl:71), which has NO walkable sub-cells on a
    // synthetic all-air grid.
    // ---------------------------------------------------------------------
    [Explicit]   // loads ~300 real classes + builds the meadow terrain; run on demand (batch)
    public sealed class ProdZeltE2ETests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            DigglesTclEngine.VerboseScriptLog = false;
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\""))!;

        // Lean real-class host (no terrain) for the non-walking chains.
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

        // The release law end-to-end on the REAL Tcl: a delivered ingredient lies
        // visible+locked in front of the Feuerstelle; CANCELLING the order (slot → 0 +
        // evt_btn_on, the native window's follow-up FUN_005235a0) runs genericprod's
        // free_unneeded_items → release_item (genericprod.tcl:566-613/:520-537) and the item
        // becomes a normal world item again.
        [Test, Explicit]
        public void CancelledOrder_ReleasesDeliveredMaterials()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            Assume.That(ctx.Classes.ContainsKey("Feuerstelle"), "real classes not loaded");

            var fire = Spawn(interp, ctx, "Feuerstelle", "35 30 8");
            fire.Owner = 0;
            fire.BuildupComplete = true;
            var g = Spawn(interp, ctx, "Zwerg", "34 30 8");
            g.Owner = 0;
            var stamm = Spawn(interp, ctx, "Pilzstamm", "30 30 8");
            stamm.Owner = -1;
            for (int i = 0; i < 15; i++) sim.Step(0.1);   // let the inits settle

            interp.Eval($"set_prod_slot_cnt {fire.Handle} Zelt 1");

            // The carry + the REAL delivery proc (the gnome stands within the 3.0 gate).
            interp.Eval($"set_lock {stamm.Handle} 1");
            interp.Eval($"inv_add {g.Handle} {stamm.Handle}");
            ctx.This = g;
            try { interp.Eval($"prod_transfertoprod {stamm.Handle} {fire.Handle} {{34 30 9.5}}"); }
            finally { ctx.This = null; }

            Assert.AreEqual(fire.Handle, stamm.ContainedIn, "delivered into the workplace stock");
            Assert.AreEqual(new Vector3(34f, 30f, 9.5f), stamm.Position, "lies at the delivery spot in front");
            Assert.AreEqual("1", interp.Eval($"get_visibility {stamm.Handle}").Trim(), "still rendered");
            Assert.AreEqual("0", interp.Eval($"get_hoverable {stamm.Handle}").Trim(), "not interactable");
            Assert.AreEqual("1", interp.Eval($"get_lock {stamm.Handle}").Trim(), "still locked");

            // CANCEL: the panel's decrement raise (BottomMenuController.Click law).
            interp.Eval($"set_prod_slot_cnt {fire.Handle} Zelt 0");
            ctx.This = null;
            interp.Eval($"set_event {fire.Handle} evt_btn_on -target {fire.Handle} -num1 0 "
                + "-text1 proddecrement -text2 Zelt");
            for (int i = 0; i < 30; i++) sim.Step(0.1);   // dispatch + handler

            Assert.AreEqual(-1, stamm.ContainedIn,
                "cancel must RELEASE the delivered material (free_unneeded_items → release_item)");
            Assert.AreEqual("0", interp.Eval($"get_lock {stamm.Handle}").Trim(), "unlocked again");
            Assert.AreEqual("1", interp.Eval($"get_hoverable {stamm.Handle}").Trim(), "interactable again");
            Assert.AreEqual(new Vector3(34f, 30f, 9.5f), stamm.Position, "stays where it lay");
        }

        int CountClass(DigglesTclContext ctx, string cls, bool worldOnly = false)
        {
            int n = 0;
            foreach (var e in ctx.Entities.Values)
                if (e.Class?.Name == cls && (!worldOnly || e.ContainedIn < 0)) n++;
            return n;
        }

        // Recipe: tttmaterial_Zelt = {Pilzstamm Pilzstamm Pilzhut} (techtreetunes.tcl:708);
        // BpZelt is pre-invented at the Feuerstelle (tttpreinv_Feuerstelle,:32).
        [Test, Explicit]
        public void ZeltOrder_MaterialsAbsent_HaulsProduces_BoxedCrate()
        {
            UnityEngine.Random.InitState(20260702);   // deterministic random/get_place picks

            var rootGo = new GameObject("WorldRoot");
            _spawnedGos.Add(rootGo);
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            ctx.LocalPlayer = 0;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(ctx.Classes.ContainsKey("Feuerstelle"), "Feuerstelle class not loaded");

            // The campaign's start-meadow terrain (the SparetimeBehaviorTests harness) — real
            // walkable floors so prod_bringtoprod's get_place delivery spots resolve.
            interp.Eval("map create 512 640");
            interp.Eval("set temppos {208 0}");
            try { interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[template] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            Assert.IsNotNull(terr!.Nav, "no NavWorld");
            ctx.Terrain = terr;
            ctx.NavService = rootGo.GetComponent<Diggles.Runtime.Navigation.NavWorldService>();

            var disp = ctx.ProdDispatcher!;
            DigglesTclEngine.VerboseScriptLog = true;   // surface the Tcl chain's `log` lines

            var fire = Spawn(interp, ctx, "Feuerstelle", "246 30.5 10");
            fire.Owner = 0;
            fire.BuildupComplete = true;

            var g = Spawn(interp, ctx, "Zwerg", "244 30.5 10");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");

            // Ingredients ABSENT from the workplace: on the meadow floor OUTSIDE the absorb box
            // (Δx ≈ 6 > 4 from the fire — a real carrier trip is required, FUN_00437370 box).
            var s1 = Spawn(interp, ctx, "Pilzstamm", "240 30.5 10");
            var s2 = Spawn(interp, ctx, "Pilzstamm", "240.5 30.5 10");
            var hut = Spawn(interp, ctx, "Pilzhut", "239.5 30.5 10");
            foreach (var m in new[] { s1, s2, hut }) m.Owner = -1;

            for (int i = 0; i < 20; i++) sim.Step(0.1);   // settle to idle
            // The Zwerg's DELAYED init re-chooses its work window ~1 s after spawn
            // (z_methods.tcl:213 `if {!$was_baby} {auto_choose_workingtime this}` via the init
            // timer) — re-pin the all-day window AFTER it so the dispatcher shift gate holds.
            interp.Eval($"set_worktime {g.Handle} 0 12");

            // The meadow template carries its own Pilzstamm stock — consumption asserts are
            // DELTAS against the post-spawn baseline (the recipe eats 2 Stamm + 1 Hut).
            int stamm0 = CountClass(ctx, "Pilzstamm");
            int hut0 = CountClass(ctx, "Pilzhut");

            // ORDER: one Zelt (the panel click writes exactly this)
            interp.Eval($"set_prod_slot_cnt {fire.Handle} Zelt 1");

            bool gateViolated = false, everHauled = false, everProduced = false;
            int maxPct = 0;
            int step = 0;
            const int budget = 6000;
            for (; step < budget; step++)
            {
                sim.Step(0.1);
                if (step % 10 == 0) disp.SweepNow(0);   // accelerate the 4 s/player cadence

                // Guard the shift window: the Zwerg's delayed init re-chooses its work window
                // (z_methods.tcl:213) at an interp-timer moment that can land after the settle —
                // keep the all-day window pinned so the dispatcher gate stays open.
                if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");

                // Watch EVERY own gnome — the meadow template ships its own dwarves and the
                // dispatcher legitimately picks the best one (movement is REAL on this terrain:
                // the nav world drives walk actions in EditMode, so the Tcl proximity gates are
                // satisfied by actual walking; no teleport shim).
                bool missingNow = disp.MissingMaterials(fire, "Zelt").Count > 0;
                foreach (var e in ctx.Entities.Values)
                {
                    if (e.Owner != 0 || e.Class == null || e.Class.Kind != "gnome") continue;
                    // the GATE: no workat may PEND while the recipe is not in the fire's inventory
                    if (missingNow && FirstEvent(e, "evt_autoprod_workat") != null) gateViolated = true;
                    e.VarsScope.Vars.TryGetValue("current_worktask", out var ewt);
                    if (ewt == "pickupitem" || ewt == "transferprod") everHauled = true;
                    if (ewt == "work") everProduced = true;
                }
                if (fire.CurrentProdItem == "Zelt") maxPct = Mathf.Max(maxPct, fire.ProdProgressPct);

                if (step % 300 == 0)
                {
                    g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                    TestContext.WriteLine($"step {step}: wt={wt} state={g.CurrentState} "
                        + $"missing=[{string.Join(",", disp.MissingMaterials(fire, "Zelt"))}] "
                        + $"prodItem={fire.CurrentProdItem} pct={fire.ProdProgressPct} "
                        + $"zelt={CountClass(ctx, "Zelt")} stamm={CountClass(ctx, "Pilzstamm")} "
                        + $"fireInv=[{interp.Eval($"inv_list {fire.Handle}").Trim()}] "
                        + $"hauled={everHauled} produced={everProduced} "
                        + $"claim={fire.CurrentWorkerHandle}");
                }

                if (CountClass(ctx, "Zelt") > 0 && everProduced) break;
            }

            // find the produced Zelt
            DigglesTclEntity? zelt = null;
            foreach (var e in ctx.Entities.Values)
                if (e.Class?.Name == "Zelt") { zelt = e; break; }

            bool boxed = zelt != null
                && zelt.VarsScope.Vars.TryGetValue("_boxed", out var bx) && (bx == "1" || bx == "true");
            TestContext.WriteLine($"RESULT: steps={step} gateViolated={gateViolated} everHauled={everHauled} "
                + $"everProduced={everProduced} maxPct={maxPct} zelt={zelt?.Handle} boxed={boxed} "
                + $"stammLeft={CountClass(ctx, "Pilzstamm")} hutLeft={CountClass(ctx, "Pilzhut")}");

            Assert.IsFalse(gateViolated, "workat pending while recipe materials were missing — the gate leaked");
            Assert.IsTrue(everHauled, "the gnome never ran a haul leg (pickupitem/transferprod)");
            Assert.IsTrue(everProduced, "the gnome never entered the produce (work) flow");
            Assert.IsNotNull(zelt, "no Zelt was produced within the step budget");
            Assert.IsTrue(boxed, "a produced building-type item must appear as a PACKED CRATE "
                + "(prod_createproduct_inv_boxed → packtobox → set_boxed 1), not a built tent");
            Assert.IsFalse(zelt!.BuildupComplete, "the boxed product must not read as a finished building");
            // The DROP SPOT law: prod_createproduct_box_with_dummybox places the crate via
            // `get_place -center <fire> -rect -10 z0 10 z1 -mindist 1.5 …` (z_work_prod.tcl:842)
            // — the native scorer's MINDIST floor applies even under -rect (FUN_005a9270:29-30),
            // so the crate may NEVER land on the workplace's own cell (the Zelt
            // crate sat on top of the campfire). Depth-weighted distance ≥ 1.5 − half a sub-cell.
            if (zelt.ContainedIn < 0)   // placed in the world (not still carried at cutoff)
            {
                float dx = zelt.Position.x - fire.Position.x, dy = zelt.Position.y - fire.Position.y;
                float dz = (zelt.Position.z - fire.Position.z) * 0.5f;
                Assert.GreaterOrEqual(Mathf.Sqrt(dx * dx + dy * dy + dz * dz), 1.4f,
                    "the finished crate must lie a mindist away from the campfire, not on it");
            }
            Assert.Greater(maxPct, 0, "info_progress_prod never moved for the Zelt production");
            Assert.AreEqual(stamm0 - 2, CountClass(ctx, "Pilzstamm"),
                "the production must CONSUME two Pilzstamm inputs (prod_walk_and_consume_itemtype → del)");
            Assert.AreEqual(hut0 - 1, CountClass(ctx, "Pilzhut"), "…and one Pilzhut");
        }

        static EntityEvent? FirstEvent(DigglesTclEntity e, string name)
        {
            foreach (var ev in e.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Invention-STATE verification at a FRESH start (user "all white except
    // Grillhamster-red"). Ground truth: fresh meadow gnomes carry exp 0 — the template spawns
    // set no editor infos (splt_urw_unq_start_tBB.tcl:7-35) and z_methods.tcl:425-429 only
    // applies get_info values; genattribs.tcl defaults every exp_* to 0. Against the real
    // thresholds (Grillhamster exp_Nahrung 0.039 / Hauklotz exp_Holz 0.008 / Steinmetz
    // exp_Stein 0.026 — techtreetunes.tcl:517/:43/:59) a TRULY fresh camp reads all three
    // RED; the tiniest exp gain (one mushroom chopped feeds exp_Holz per hack loop,
    // z_procs.tcl:832-866; digging feeds exp_Stein, z_dignwalk.tcl:323) legitimately flips
    // Hauklotz/Steinmetz white. This diag PROVES the computation path (tttinvent requirement
    // parse via prod_item_attribs + get_expattrib reads) on the REAL classes, so a Play
    // "white at fresh start" report can be attributed to already-gained exp, not a UI bug.
    // ---------------------------------------------------------------------
    [Explicit]   // loads ~300 real classes; run on demand (batch)
    public sealed class InventionStartStateDiagTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static void Tick(BottomMenuController c)
            => c.GetType().GetMethod("VContainer.Unity.ITickable.Tick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(c, null);

        [Test, Explicit]
        public void FreshStart_InventionStates_MatchZeroExp_AndFlipOnGain()
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
            Assume.That(ctx.Classes.ContainsKey("Feuerstelle"), "real classes not loaded");
            ctx.LocalPlayer = 0;
            ctx.IsStoryMgr = true;

            var fire = ctx.GetEntity(interp.Eval("new Feuerstelle \"\" \"35 30 8\" \"0 0 0\""))!;
            fire.Owner = 0; fire.BuildupComplete = true;
            var g = ctx.GetEntity(interp.Eval("new Zwerg \"\" \"34 30 8\" \"0 0 0\""))!;
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            for (int i = 0; i < 15; i++) sim.Step(0.1);
            interp.Eval($"set_worktime {g.Handle} 0 12");   // re-pin after the delayed init

            // 1. fresh exp values — the template law says all-zero (get_attrib = the exp VALUE
            // reader, z_work_common.tcl:229; get_expattrib only enumerates the names)
            foreach (var atr in new[] { "exp_Nahrung", "exp_Holz", "exp_Stein" })
                TestContext.WriteLine($"fresh {atr} = {interp.Eval($"get_attrib {g.Handle} {atr}").Trim()}");
            // 2. the REAL requirement lists must PARSE (an empty parse would fake all-white)
            foreach (var item in new[] { "Grillhamster", "Hauklotz", "Steinmetz" })
                TestContext.WriteLine($"prod_item_attribs {item} = "
                    + $"'{interp.Eval($"call_method_static Feuerstelle prod_item_attribs {{{item}}}").Trim()}'");

            var ctrl = new BottomMenuController(ctx);
            ctx.SelectedHandle = fire.Handle;
            Tick(ctrl);
            string StateOf(string item)
                => ctrl.Model.Entries.Find(x => x.Id == "prod:" + item)?.Invention.ToString() ?? "MISSING";
            TestContext.WriteLine($"fresh states: Grillhamster={StateOf("Grillhamster")} "
                + $"Hauklotz={StateOf("Hauklotz")} Steinmetz={StateOf("Steinmetz")}");
            Assert.AreEqual("Red", StateOf("Grillhamster"), "fresh camp: nobody meets exp_Nahrung 0.039");
            Assert.AreEqual("Red", StateOf("Hauklotz"), "fresh camp: nobody meets exp_Holz 0.008");
            Assert.AreEqual("Red", StateOf("Steinmetz"), "fresh camp: nobody meets exp_Stein 0.026");

            // 3. the tiniest wood exp (one chop's worth) flips Hauklotz white while the gnome
            // is inside his work window — the player's Play whites = the camp had worked.
            interp.Eval($"add_expattrib {g.Handle} exp_Holz 0.01");
            ctx.SelectedHandle = 0; Tick(ctrl);
            ctx.SelectedHandle = fire.Handle; Tick(ctrl);
            TestContext.WriteLine($"after +0.01 exp_Holz: Hauklotz={StateOf("Hauklotz")} "
                + $"Steinmetz={StateOf("Steinmetz")}");
            Assert.AreEqual("White", StateOf("Hauklotz"),
                "0.01 exp_Holz ≥ the 0.008 threshold + in work window → white");
            Assert.AreEqual("Red", StateOf("Steinmetz"), "stone exp untouched → still red");
        }
    }
}
