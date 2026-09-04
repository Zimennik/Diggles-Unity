#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Single-owner pickup law: two multi-selected gnomes ordered onto the same ground item must
    // never duplicate the handle into both inventories.
    //
    // The order fan-out itself is native (every selected gnome receives evt_task_pickup); the
    // native game stays consistent through this chain:
    //
    // • evt_task_pickup_proc (z_events.tcl:406-433) → pickup (z_procs.tcl:446-489). Seed-time
    // gates: inv_find_obj (:451), inv_check (:455), get_instore (:461), is_contained (:463);
    // the claim is lock_item (:486) — but nothing in the seed chain reads the lock, so when
    // both gnomes are ordered in the same instant both seed and both walk.
    // (check_lockedbyother, z_procs.tcl:1721, guards only the eat/fun chains:817/:849.)
    // • Native inv_check = FUN_005cf490: a pure size check (item+0x220 vs the DAT_00772478
    // capacity tiers; non-Zwerg holders return 1 unconditionally). No lock gate, no
    // containment gate — inv_check is not the anti-dupe mechanism.
    // • The serializer sits in the native InvAdd (TCL:CTclObjMethods::InvAdd, FUN_0069bd30):
    //:132 clears the +0x3d0 bit-0 visible flag,:133-141 dedupes against the same holder
    // (returns the existing slot), and:145-151 moves the item to the world origin
    // (CObj::SetPosition(&{0,0,0}) = FUN_005cd8e0) and pulls it from the world grid
    // (FUN_005ebc90/FUN_005eb470) before the core add FUN_005ce010. The core add sets the
    // contained flag (+0x3d0 bit 2, 005ce010:166/:204) but never tests it — cross-holder
    // protection is the pos zero, not a flag check.
    // • So the second gnome arrives and take_item (z_procs.tcl:592-665) finds the item at
    // {0 0 0}: the arrival gate `vector_dist3d > 3.0` (:648) fails → play bowllose +
    // gnome_failed_work + return false (:650-654). No dupe, no retry hang.
    // • The scripts confirm the pos-zero law: every flow that wants a contained item to keep a
    // world position re-sets it after inv_add — genericprod.tcl:506-508 (saves pos before,
    // restores after), z_work_prod.tcl:461-464 (next_item_of_itemtype), z_work_common.tcl:
    // 51-56 (prod_transfertoprod: inv_rem → inv_add → set_pos), lager.tcl:734-737
    // (store_item shelf placement). genericprod.tcl:505 additionally gates
    // `is_contained || get_lock → return false` before its inv_add — no original call site
    // relies on an implicit cross-holder transfer (all transfers inv_rem first).
    //
    // Port bug: inv_add appended the handle + repointed ContainedIn with no cross-holder guard
    // and no pos zero — so the second take_item still measured the old ground position, passed
    // the ≤3.0 gate, and beamto_inv'd the same handle into a second inventory.
    //
    // Fix under test (DigglesTclEngine inv_add):
    // 1. an item contained in a different holder refuses with -1;
    // 2. a successful add moves the item to {0 0 0} (FUN_0069bd30:145-147), which lets the
    // unchanged take_item distance law kill the second pickup natively.
    //
    // The lock side needs no port work: set_lock/get_lock are real entity props
    // (RegisterEntityProp "lock") and the lock_item claim law is pinned by ChopAgeLawTests.
    public sealed class PickupClaimTests
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
            interp.Eval("def_class M none material 0 {} {}");
            interp.Eval("def_class B none production 0 {} {}");
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static DigglesTclEntity Gnome(TclInterpreter interp, DigglesTclContext ctx, string pos)
        {
            var g = Spawn(interp, ctx, "G", pos);
            g.Owner = 0;
            g.VarsScope.Vars["current_lock_obj"] = "0";   // zwerg.tcl:44 seed
            return g;
        }

        static int HoldersOf(DigglesTclContext ctx, int handle)
        {
            int n = 0;
            foreach (var e in ctx.Entities.Values)
                if (e.Inventory.Contains(handle)) n++;
            return n;
        }

        // ------------------------------------------------------------------
        // engine units: the inv_add contract
        // ------------------------------------------------------------------

        [Test]
        public void InvAdd_SecondHolder_Refused_ItemStaysWithFirst()
        {
            var (interp, ctx) = Host();
            var g1 = Gnome(interp, ctx, "30 30 8");
            var g2 = Gnome(interp, ctx, "31 30 8");
            var item = Spawn(interp, ctx, "M", "30.5 30 8");

            Assert.AreEqual("0", interp.Eval($"inv_add {g1.Handle} {item.Handle}").Trim(),
                "first add lands in slot 0");
            Assert.AreEqual("-1", interp.Eval($"inv_add {g2.Handle} {item.Handle}").Trim(),
                "an item contained in ANOTHER holder must REFUSE (single-owner guard; native "
                + "reaches the same end via the FUN_0069bd30:145-147 pos zero + take_item's "
                + "distance gate)");

            Assert.IsTrue(g1.Inventory.Contains(item.Handle), "first holder keeps the item");
            Assert.IsFalse(g2.Inventory.Contains(item.Handle), "second holder must NOT get the handle");
            Assert.AreEqual(1, HoldersOf(ctx, item.Handle), "exactly ONE inventory holds the handle");
            Assert.AreEqual(g1.Handle, item.ContainedIn, "ContainedIn stays on the first holder");
        }

        [Test]
        public void InvAdd_ZeroesTheItemsWorldPosition()
        {
            // FUN_0069bd30:145-147: nonzero pos → CObj::SetPosition(&{0,0,0}). genericprod.tcl:
            // 506-508 / z_work_prod.tcl:461-464 / lager.tcl:734-737 all save the pos before
            // inv_add and re-set it after — pointless unless the add clobbers it.
            var (interp, ctx) = Host();
            var g = Gnome(interp, ctx, "30 30 8");
            var item = Spawn(interp, ctx, "M", "40 25 8");

            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            Assert.AreEqual(Vector3.zero, item.Position,
                "native InvAdd moves the item to the world origin — the anti-double-pickup law");
            Assert.AreEqual("0 0 0", interp.Eval($"get_pos {item.Handle}").Trim());
        }

        [Test]
        public void InvAdd_SameHolder_ReturnsExistingSlot()
        {
            // FUN_0069bd30:133-141 — re-adding to the same holder returns the existing slot
            // index without duplicating.
            var (interp, ctx) = Host();
            var g = Gnome(interp, ctx, "30 30 8");
            var m0 = Spawn(interp, ctx, "M", "30 30 8");
            var m1 = Spawn(interp, ctx, "M", "30 30 8");
            interp.Eval($"inv_add {g.Handle} {m0.Handle}");
            interp.Eval($"inv_add {g.Handle} {m1.Handle}");

            Assert.AreEqual("1", interp.Eval($"inv_add {g.Handle} {m1.Handle}").Trim(),
                "same-holder re-add answers the existing slot, not -1");
            Assert.AreEqual(2, g.Inventory.Count, "and never duplicates the handle");
        }

        [Test]
        public void InvRem_Then_InvAdd_TransfersCleanly()
        {
            // The only legit transfer shape in the original scripts: inv_rem first, then inv_add
            // (prod_transfertoprod z_work_common.tcl:51-56, next_item_of_itemtype
            // z_work_prod.tcl:461-464, the lager store/retrieve pair). The guard must not break it.
            var (interp, ctx) = Host();
            var g = Gnome(interp, ctx, "30 30 8");
            var b = Spawn(interp, ctx, "B", "33 30 8");
            var item = Spawn(interp, ctx, "M", "30.5 30 8");

            interp.Eval($"inv_add {g.Handle} {item.Handle}");
            interp.Eval($"inv_rem {g.Handle} {item.Handle}");
            Assert.AreEqual("0", interp.Eval($"inv_add {b.Handle} {item.Handle}").Trim(),
                "after inv_rem the item is free — the prodplace add must succeed");
            Assert.AreEqual(b.Handle, item.ContainedIn);
            Assert.AreEqual(1, HoldersOf(ctx, item.Handle));
            Assert.IsFalse(g.Inventory.Contains(item.Handle));
        }

        // ------------------------------------------------------------------
        // End to end: two gnomes, one item, both pickup chains seeded before either take_item
        // runs (the multi-select fan-out timing), then both arrivals. Runs the real z_procs.tcl
        // chain procs verbatim in each gnome's object scope.
        // ------------------------------------------------------------------

        static void DefineRealChainProcs(TclInterpreter interp)
        {
            // z_procs.tcl:63-85 — the claim/release pair.
            interp.Eval(
                "proc lock_item {item} {\n" +
                "  global current_lock_obj\n" +
                "  if { $current_lock_obj != 0 } { set_lock $current_lock_obj 0; set current_lock_obj 0 }\n" +
                "  if { $item != 0 } { set current_lock_obj $item; set_lock $current_lock_obj 1 }\n" +
                "}");
            interp.Eval(
                "proc unlock_item {} {\n" +
                "  global current_lock_obj\n" +
                "  if { $current_lock_obj != 0 } {\n" +
                "    if {[obj_valid $current_lock_obj]} {\n" +
                "      if {[get_objclass $current_lock_obj]!=\"Pilz\"||[get_attrib $current_lock_obj PilzAge]==3} {\n" +
                "        catch {set_lock $current_lock_obj 0}\n" +
                "      }\n" +
                "    }\n" +
                "    set current_lock_obj 0\n" +
                "  }\n" +
                "}");
            // z_procs.tcl:255-285 (beamto_inv), Schatzbuch/diplomacy side branches intact —
            // they no-op on the light host (unknown → "0").
            interp.Eval(
                "proc beamto_inv {item} {\n" +
                "  if {![obj_valid $item]} { return }\n" +
                "  unlock_item\n" +
                "  inv_add this $item\n" +
                "  set_prodalloclock $item 0\n" +
                "  set_rotx $item 0.0\n" +
                "  set_rotz $item 0.0\n" +
                "  set_owner $item [get_owner this]\n" +
                "  return true\n" +
                "}");
            // z_procs.tcl:446-489 (pickup), hamster branch omitted (M is a plain material —
            // the generic `walk_near_item` seeding is tasklist_add'ed exactly as shipped).
            interp.Eval(
                "proc pickup {item} {\n" +
                "  if {![obj_valid $item]} { return false }\n" +
                "  if {[inv_find_obj this $item] != -1} { return true }\n" +
                "  if {[inv_check this $item] == 0} {\n" +
                "    tasklist_add this \"play_anim leftright\"\n" +
                "    tasklist_add this \"play_anim dontknow\"\n" +
                "    return false\n" +
                "  }\n" +
                "  if { [get_instore $item]} { return [pickup_from_store $item] } elseif { [is_contained $item] } { return false }\n" +
                "  tasklist_add this \"walk_near_item $item 0.6 0.2\"\n" +
                "  tasklist_add this \"rotate_towards $item\"\n" +
                "  tasklist_add this \"play_anim_pickup $item\"\n" +
                "  tasklist_add this \"take_item $item\"\n" +
                "  lock_item $item\n" +
                "  return true\n" +
                "}");
            // z_procs.tcl:592-665 (take_item), hamster/flag branches intact where reachable.
            interp.Eval(
                "proc take_item {item} {\n" +
                "  global current_worklist\n" +
                "  if {![obj_valid $item] || ![inv_check this $item]} {\n" +
                "    tasklist_clear this\n" +
                "    gnome_failed_work this\n" +
                "    return false\n" +
                "  }\n" +
                "  set mypos [get_pos this]\n" +
                "  set itempos [get_pos $item]\n" +
                "  if {[get_gnomeposition this]} {\n" +
                "    if {[lindex $mypos 2]>[lindex $itempos 2]+2||[vector_dist $mypos $itempos]>2.0} {\n" +
                "      set current_worklist {}\n" +
                "      tasklist_clear this\n" +
                "      gnome_failed_work this\n" +
                "      return false\n" +
                "    }\n" +
                "  } elseif {[vector_dist3d $mypos $itempos] > 3.0} {\n" +
                "    set current_worklist {}\n" +
                "    tasklist_clear this\n" +
                "    play_anim bowllose\n" +
                "    gnome_failed_work this\n" +
                "    return false\n" +
                "  }\n" +
                "  beamto_inv $item\n" +
                "  return true\n" +
                "}");
        }

        static string RunFor(DigglesTclContext ctx, DigglesTclEntity g, string code)
        {
            // Result capture through the entity scope (RunInEntityContext returns void).
            DigglesTclEngine.RunInEntityContext(ctx, g, $"set __res [{code}]");
            return g.VarsScope.Vars.TryGetValue("__res", out var v) ? v.Trim() : "<unset>";
        }

        [Test]
        public void TwoGnomes_SamePickupOrder_ExactlyOneEndsUpHolding()
        {
            var (interp, ctx) = Host();
            DefineRealChainProcs(interp);
            var gA = Gnome(interp, ctx, "30 30 8");
            var gB = Gnome(interp, ctx, "31 30 8");
            var item = Spawn(interp, ctx, "M", "30.5 30 8");   // ≤3.0 from both gnomes

            // t0: the fan-out — both evt_task_pickup_procs seed while the item is still free.
            Assert.AreEqual("true", RunFor(ctx, gA, $"pickup {item.Handle}"), "gnome A seeds");
            Assert.AreEqual("true", RunFor(ctx, gB, $"pickup {item.Handle}"),
                "gnome B seeds TOO — nothing in the native seed chain reads the lock, both walk "
                + "(the fan-out is correct; the serializer is at arrival)");
            Assert.AreEqual("1", interp.Eval($"get_lock {item.Handle}").Trim(),
                "the item is claimed (lock_item, z_procs.tcl:486)");

            // t1: gnome A arrives first — his take_item pockets the item.
            Assert.AreEqual("true", RunFor(ctx, gA, $"take_item {item.Handle}"));
            Assert.AreEqual(gA.Handle, item.ContainedIn, "A holds it");
            Assert.AreEqual(Vector3.zero, item.Position,
                "InvAdd moved it to the origin (FUN_0069bd30:145-147)");
            Assert.AreEqual("0", interp.Eval($"get_lock {item.Handle}").Trim(),
                "beamto_inv released the claim (unlock_item before inv_add, z_procs.tcl:265)");

            // t2: gnome B arrives — his take_item must fail on the unchanged native law (item at
            // {0 0 0} → the >3.0 arrival gate, z_procs.tcl:648-654). His tasklist still carries
            // the seeded steps; the fail path must clear them (no retry hang).
            Assert.Greater(gB.TaskList.Count, 0, "B's seeded pickup steps are pending");
            Assert.AreEqual("false", RunFor(ctx, gB, $"take_item {item.Handle}"),
                "the second take_item must fail — not beamto_inv a contained item");
            Assert.AreEqual(0, gB.TaskList.Count,
                "take_item's fail path tasklist_clears (no retry hang, z_procs.tcl:650-651)");

            // Anti-dupe assertion: one holder, consistent containment.
            Assert.AreEqual(1, HoldersOf(ctx, item.Handle),
                "the item must exist in exactly ONE inventory (the 13.07 dupe repro)");
            Assert.IsTrue(gA.Inventory.Contains(item.Handle));
            Assert.IsFalse(gB.Inventory.Contains(item.Handle));
            Assert.AreEqual(gA.Handle, item.ContainedIn);
        }

        [Test]
        public void SecondPickup_SeededAfterTheTake_FailsAtTheContainedGate()
        {
            // The slower repro shape: B's order arrives after A already pocketed the item —
            // pickup's own seed gate kills it (is_contained, z_procs.tcl:463) and B never walks.
            var (interp, ctx) = Host();
            DefineRealChainProcs(interp);
            var gA = Gnome(interp, ctx, "30 30 8");
            var gB = Gnome(interp, ctx, "31 30 8");
            var item = Spawn(interp, ctx, "M", "30.5 30 8");

            Assert.AreEqual("true", RunFor(ctx, gA, $"pickup {item.Handle}"));
            Assert.AreEqual("true", RunFor(ctx, gA, $"take_item {item.Handle}"));

            Assert.AreEqual("false", RunFor(ctx, gB, $"pickup {item.Handle}"),
                "a contained item must refuse at pickup's is_contained seed gate");
            Assert.AreEqual(0, gB.TaskList.Count, "no walk/take steps seeded for B");
            Assert.AreEqual(1, HoldersOf(ctx, item.Handle));
            Assert.AreEqual(gA.Handle, item.ContainedIn);
        }

        // ------------------------------------------------------------------
        // Full-stack E2E (real classes, the same drive-loop pattern used elsewhere in the item
        // tests): the unchanged z_events.tcl/z_procs.tcl running on the 300-class load — two
        // Zwergs, one Stein, both evt_task_pickups fanned out in the same instant, sim stepped to
        // completion. Explicit (heavy class load) — run via direct filter.
        // ------------------------------------------------------------------
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/Scripts/init/classinit.tcl"),
                "original data dir not present");
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

        [Test, Explicit]
        public void TwoGnomes_RealChain_E2E_ExactlyOneHolds()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            var gA = ctx.GetEntity(interp.Eval("new Zwerg \"\" \"100 100 8\" \"0 0 0\"").Trim())!;
            var gB = ctx.GetEntity(interp.Eval("new Zwerg \"\" \"101.2 100 8\" \"0 0 0\"").Trim())!;
            gA.Owner = 0; gB.Owner = 0;
            interp.Eval($"set_worktime {gA.Handle} 0 12");
            interp.Eval($"set_worktime {gB.Handle} 0 12");
            // Stein 0.5/0.7 from A/B — walk_near_item (0.6 0.2) short-circuits for both, so the
            // whole chain runs without Play-mode movement.
            var s = ctx.GetEntity(interp.Eval("new Stein \"\" \"100.5 100 8\" \"0 0 0\"").Trim())!;
            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle into idle

            // The multi-select fan-out instant: both gnomes get evt_task_pickup on the same
            // item before either moves (the duplicate-pickup regression).
            DigglesTclEngine.EvtGenRun(ctx, gA.Handle, s.Handle, s.Position, "-command");
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, gA.Handle));
            DigglesTclEngine.EvtGenRun(ctx, gB.Handle, s.Handle, s.Position, "-command");
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, gB.Handle));

            // Phase 1: run until the take lands + a settle window for the loser's fail path.
            int settled = -1;
            for (int i = 0; i < 150; i++)
            {
                sim.Step(0.1);
                if (settled < 0 && s.ContainedIn >= 0) settled = i;
                if (i % 15 == 0)
                    TestContext.WriteLine($"step {i}: A(state={gA.CurrentState} tl={gA.TaskList.Count} inv={gA.Inventory.Count}) "
                                        + $"B(state={gB.CurrentState} tl={gB.TaskList.Count} inv={gB.Inventory.Count}) containedIn={s.ContainedIn}");
                if (settled >= 0 && i >= settled + 30) break;   // both chains have resolved
            }

            bool aHolds = gA.Inventory.Contains(s.Handle);
            bool bHolds = gB.Inventory.Contains(s.Handle);
            var loser = aHolds ? gB : gA;
            TestContext.WriteLine($"RESULT: aHolds={aHolds} bHolds={bHolds} containedIn={s.ContainedIn} pos={s.Position} "
                                + $"loser(state={loser.CurrentState} tl={loser.TaskList.Count})");
            Assert.IsTrue(aHolds || bHolds, "somebody must have picked the Stein up");
            Assert.IsFalse(aHolds && bHolds, "THE DUPE: the Stein must never sit in BOTH inventories");
            Assert.AreEqual(1, HoldersOf(ctx, s.Handle), "exactly one inventory holds the handle");
            Assert.AreEqual(aHolds ? gA.Handle : gB.Handle, s.ContainedIn, "ContainedIn = the real holder");
            Assert.AreEqual(0, loser.Inventory.Count, "the loser carries nothing");
            // Graceful end: the loser must not be stuck grinding the failed pickup — his
            // remaining steps (if any) belong to a new autonomous job the dispatcher handed him
            // (the fail path tasklist_clears, z_procs.tcl:650-651; observed: he re-enters
            // work_idle within ~1.5 s and may later pick up prodfill work — that's normal, not a hang).
            bool grindingOldPickup = false;
            foreach (var t in loser.TaskList)
                if (t.Contains($"take_item {s.Handle}")) grindingOldPickup = true;
            Assert.IsFalse(grindingOldPickup,
                "the loser's tasklist must not still carry the failed take_item (no retry hang)");
        }
    }
}
