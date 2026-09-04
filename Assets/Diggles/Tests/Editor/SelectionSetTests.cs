#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // The multi-select model — pinned against the RE'd native selection laws:
    // • the selection is a HANDLE LIST (CGame+0xa0c4/+0xa0c8), primary = selection[0];
    // • caninclude (FUN_005e32b0): own + selectable; joining a non-empty selection is for
    // gnome/baby CANDIDATES only (the gate is one-sided — the trailing compat clause is
    // dead code): a structure never joins, but gnomes/babies DO join a selected structure
    // (mixed set, structure stays primary);
    // • the click law (FUN_005309c0): Shift adds, plain replaces, no-target clears,
    // incompatible-join replaces;
    // • the band-box law (FUN_0052d170): plain box replaces, Shift box adds, candidates in
    // handle order — first enters, gnomes/babies join, further structures are skipped
    // (the native gnome-clear override is defensive dead code);
    // • Tcl surface: get_selectedobject = PRIMARY (FUN_00647100 reads selection[0]),
    // is_selected = MEMBERSHIP (FUN_0069e750 → FUN_005e35a0), `selection
    // check/include/exclude/caninclude/clear` = the raw ops (FUN_0061e7a0);
    // • command fan-out: a walk order / F9 dropall reaches EVERY selected unit
    // (FUN_00530b90's per-selection eventgen loop / FUN_0052c510's key loop).
    public sealed class SelectionSetTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            ctx.LocalPlayer = 0;
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class B none production 0 {} {}");
            interp.Eval("def_class Baby none baby 0 {} {}");
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, int owner = 0)
        {
            var e = ctx.GetEntity(interp.Eval($"new {cls} \"\" \"0 0 0\" \"0 0 0\"").Trim())!;
            e.Owner = owner;
            return e;
        }

        // ------------------------------------------------------------------
        // caninclude / include laws (FUN_005e32b0 / FUN_005e3350)
        // ------------------------------------------------------------------

        [Test]
        public void Include_OwnGnome_Works_ForeignRejected()
        {
            var (interp, ctx) = Host();
            var own = Spawn(interp, ctx, "G", 0);
            var foreign = Spawn(interp, ctx, "G", 1);
            Assert.IsTrue(ctx.Selection.CanInclude(own.Handle));
            Assert.IsFalse(ctx.Selection.CanInclude(foreign.Handle), "owner gate: obj+0x36c must equal the local player");
            ctx.Selection.Include(own.Handle);
            ctx.Selection.Include(foreign.Handle);
            Assert.AreEqual(1, ctx.Selection.Count);
            Assert.AreEqual(own.Handle, ctx.Selection.Primary);
        }

        [Test]
        public void Include_SecondGnome_Appends_PrimaryStaysFirst()
        {
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            ctx.Selection.Include(g1.Handle);
            ctx.Selection.Include(g2.Handle);
            ctx.Selection.Include(g2.Handle);   // dup-safe (the native dup scan)
            Assert.AreEqual(2, ctx.Selection.Count);
            Assert.AreEqual(g1.Handle, ctx.Selection.Primary, "primary = selection[0], the native get_selectedobject");
            Assert.IsTrue(ctx.Selection.Contains(g2.Handle));
        }

        [Test]
        public void Building_NeverJoins_ButGnomeJoinsBuilding()
        {
            // The native join gate is ONE-SIDED (FUN_005e32b0's count>0 branch tests only the
            // CANDIDATE's type ∈ {1,0xb}; the trailing compat clause is dead code): a structure
            // can never JOIN a non-empty selection, but a gnome/baby CAN join a selected
            // structure — the native allows the MIXED set with the structure as primary.
            var (interp, ctx) = Host();
            var b = Spawn(interp, ctx, "B");
            var b2 = Spawn(interp, ctx, "B");
            var g = Spawn(interp, ctx, "G");
            Assert.IsTrue(ctx.Selection.CanInclude(b.Handle), "empty selection: any own selectable object may enter");
            ctx.Selection.Include(b.Handle);
            Assert.IsFalse(ctx.Selection.CanInclude(b2.Handle), "a second structure never joins");
            ctx.Selection.Include(b2.Handle);
            Assert.AreEqual(1, ctx.Selection.Count);
            Assert.IsTrue(ctx.Selection.CanInclude(g.Handle), "a gnome candidate passes the {1,0xb} gate");
            ctx.Selection.Include(g.Handle);
            Assert.AreEqual(2, ctx.Selection.Count, "mixed structure+gnome selection is native-legal");
            Assert.AreEqual(b.Handle, ctx.Selection.Primary, "the structure stays selection[0]");
            // …and the reverse: a building can't join gnomes.
            ctx.Selection.Clear();
            ctx.Selection.Include(g.Handle);
            Assert.IsFalse(ctx.Selection.CanInclude(b.Handle), "a structure never joins a non-empty selection");
        }

        [Test]
        public void BabyAndGnome_Mix()
        {
            var (interp, ctx) = Host();
            var g = Spawn(interp, ctx, "G");
            var baby = Spawn(interp, ctx, "Baby");
            ctx.Selection.Include(g.Handle);
            Assert.IsTrue(ctx.Selection.CanInclude(baby.Handle), "native join set = objtype 1|0xb (gnome|baby)");
            ctx.Selection.Include(baby.Handle);
            Assert.AreEqual(2, ctx.Selection.Count);
        }

        [Test]
        public void SetSelectableZero_BlocksInclude()
        {
            var (interp, ctx) = Host();
            var g = Spawn(interp, ctx, "G");
            g.VarsScope.Vars["_selectable"] = "0";   // the native flag 0x200 cleared
            Assert.IsFalse(ctx.Selection.CanInclude(g.Handle));
        }

        // ------------------------------------------------------------------
        // the click law (FUN_005309c0)
        // ------------------------------------------------------------------

        [Test]
        public void ClickSelect_PlainReplaces_ShiftAdds()
        {
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            ctx.Selection.ClickSelect(g1.Handle, additive: false);
            Assert.AreEqual(1, ctx.Selection.Count);
            ctx.Selection.ClickSelect(g2.Handle, additive: true);      // Shift+click = ADD
            Assert.AreEqual(2, ctx.Selection.Count);
            Assert.AreEqual(g1.Handle, ctx.Selection.Primary);
            ctx.Selection.ClickSelect(g2.Handle, additive: false);     // plain click = REPLACE
            Assert.AreEqual(1, ctx.Selection.Count);
            Assert.AreEqual(g2.Handle, ctx.Selection.Primary);
        }

        [Test]
        public void ClickSelect_NoTarget_Clears()
        {
            var (interp, ctx) = Host();
            var g = Spawn(interp, ctx, "G");
            ctx.Selection.Include(g.Handle);
            ctx.Selection.ClickSelect(0, additive: true);
            Assert.AreEqual(0, ctx.Selection.Count, "hover < 0/none → FUN_0040b980(0)");
        }

        [Test]
        public void ClickSelect_ShiftOnBuilding_WithGnomes_ReplacesWithBuilding()
        {
            var (interp, ctx) = Host();
            var g = Spawn(interp, ctx, "G");
            var b = Spawn(interp, ctx, "B");
            ctx.Selection.Include(g.Handle);
            // native: caninclude fails (a structure can't join) → clear first → include alone
            ctx.Selection.ClickSelect(b.Handle, additive: true);
            Assert.AreEqual(1, ctx.Selection.Count);
            Assert.AreEqual(b.Handle, ctx.Selection.Primary);
        }

        // ------------------------------------------------------------------
        // the band-box law (FUN_0052d170)
        // ------------------------------------------------------------------

        [Test]
        public void BoxEligible_OwnBandKindsOnly()
        {
            var (interp, ctx) = Host();
            var own = Spawn(interp, ctx, "G", 0);
            var foreign = Spawn(interp, ctx, "G", 1);
            var cloaked = Spawn(interp, ctx, "G", 0);
            cloaked.VarsScope.Vars["_cloaked"] = "1";
            var building = Spawn(interp, ctx, "B", 0);
            Assert.IsTrue(ClickToMoveService.BoxEligible(own, 0));
            Assert.IsFalse(ClickToMoveService.BoxEligible(foreign, 0), "foreign units never band-select");
            Assert.IsFalse(ClickToMoveService.BoxEligible(cloaked, 0));
            Assert.IsTrue(ClickToMoveService.BoxEligible(building, 0), "native band kinds include structures");
        }

        [Test]
        public void BoxInclude_HandleOrder_StructureFirst_GnomesJoin_SecondStructureSkipped()
        {
            // FUN_0052d170's release loop, handle order: the first candidate enters; gnome/baby
            // candidates JOIN (an own selectable gnome never fails caninclude — the native
            // gnome-clear branch is defensive dead code); further structures are skipped.
            var (interp, ctx) = Host();
            var b = Spawn(interp, ctx, "B");     // lower handle → iterated first (handle order)
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            var b2 = Spawn(interp, ctx, "B");
            ctx.Selection.Clear();               // plain box clears first (:110-113)
            ctx.Selection.BoxIncludeStep(b.Handle);
            Assert.AreEqual(b.Handle, ctx.Selection.Primary, "empty → the building enters first");
            ctx.Selection.BoxIncludeStep(g1.Handle);
            ctx.Selection.BoxIncludeStep(g2.Handle);
            ctx.Selection.BoxIncludeStep(b2.Handle);
            Assert.AreEqual(3, ctx.Selection.Count, "gnomes join the boxed structure; the 2nd structure is skipped");
            Assert.AreEqual(b.Handle, ctx.Selection.Primary, "the structure stays selection[0]");
            Assert.IsTrue(ctx.Selection.Contains(g1.Handle) && ctx.Selection.Contains(g2.Handle));
            Assert.IsFalse(ctx.Selection.Contains(b2.Handle));
        }

        [Test]
        public void BoxInclude_GnomesOnly_AllAccumulate()
        {
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            var baby = Spawn(interp, ctx, "Baby");
            ctx.Selection.Clear();
            ctx.Selection.BoxIncludeStep(g1.Handle);
            ctx.Selection.BoxIncludeStep(g2.Handle);
            ctx.Selection.BoxIncludeStep(baby.Handle);
            Assert.AreEqual(3, ctx.Selection.Count, "the box gathers every own gnome/baby");
            Assert.AreEqual(g1.Handle, ctx.Selection.Primary, "primary = first in handle order");
        }

        [Test]
        public void BoxDragGate_IsAreaProduct250()
        {
            // FUN_0052fe80:70 — |dx|·|dy| > 0xfa; both axes must move.
            Assert.IsFalse(ClickToMoveService.BoxDragStarts(new Vector2(0, 0), new Vector2(300, 0)),
                "a pure horizontal drag never bands (area product 0)");
            Assert.IsFalse(ClickToMoveService.BoxDragStarts(new Vector2(0, 0), new Vector2(15, 15)));  // 225 ≤ 250
            Assert.IsTrue(ClickToMoveService.BoxDragStarts(new Vector2(0, 0), new Vector2(16, 16)));   // 256 > 250
            Assert.IsTrue(ClickToMoveService.BoxDragStarts(new Vector2(100, 100), new Vector2(84, 116)));
        }

        // ------------------------------------------------------------------
        // primary compat: ctx.SelectedHandle + the Tcl surface
        // ------------------------------------------------------------------

        [Test]
        public void SelectedHandle_Property_RoundTrips_AsPrimary()
        {
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            ctx.SelectedHandle = g1.Handle;                 // legacy single-select write = replace
            Assert.AreEqual(g1.Handle, ctx.SelectedHandle);
            ctx.Selection.Include(g2.Handle);
            Assert.AreEqual(g1.Handle, ctx.SelectedHandle, "primary stays selection[0] under multi");
            ctx.SelectedHandle = g2.Handle;                 // a write REPLACES the whole set
            Assert.AreEqual(1, ctx.Selection.Count);
            ctx.SelectedHandle = 0;                         // 0 (and any invalid) = deselect
            Assert.AreEqual(0, ctx.Selection.Count);
        }

        [Test]
        public void Tcl_SelectionSurface_Faithful()
        {
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            interp.Eval($"selection include {g1.Handle}");
            interp.Eval($"selection include {g2.Handle}");
            Assert.AreEqual(g1.Handle.ToString(), interp.Eval("get_selectedobject").Trim(),
                "get_selectedobject = the PRIMARY only (FUN_00647100 reads selection[0])");
            Assert.AreEqual("1", interp.Eval($"is_selected {g2.Handle}").Trim(),
                "is_selected = LIST membership (FUN_0069e750), not primary equality");
            Assert.AreEqual("1", interp.Eval($"selection check {g2.Handle}").Trim());
            interp.Eval($"selection exclude {g2.Handle}");
            Assert.AreEqual("0", interp.Eval($"selection check {g2.Handle}").Trim());
            Assert.AreEqual("1", interp.Eval($"selection caninclude {g2.Handle}").Trim());
            interp.Eval("selection clear");
            Assert.AreEqual("0", interp.Eval("get_selectedobject").Trim());
            interp.Eval($"set_selectedobject {g2.Handle}");
            Assert.AreEqual("1", interp.Eval($"is_selected {g2.Handle}").Trim());
            interp.Eval("set_selectedobject 0");
            Assert.AreEqual(0, ctx.Selection.Count, "set_selectedobject 0 deselects (drachenb.tcl:219)");
        }

        // ------------------------------------------------------------------
        // command fan-out over the selection
        // ------------------------------------------------------------------

        [Test]
        public void WalkOrder_FansOutToEverySelectedUnit_SameTarget()
        {
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            var b = Spawn(interp, ctx, "B");
            ctx.Selection.Include(g1.Handle);
            ctx.Selection.Include(g2.Handle);
            var svc = new ClickToMoveService(ctx);
            int walked = svc.FireWalkOrder(new Vector3(12f, 34f, 0f));
            Assert.AreEqual(2, walked, "the native command loop runs once per selected unit");
            foreach (var g in new[] { g1, g2 })
            {
                EntityEvent? ev = null;
                foreach (var e in g.PendingEvents) if (e.Name == "evt_task_walk") ev = e;
                Assert.IsNotNull(ev, $"gnome #{g.Handle} got the walk order");
                Assert.IsTrue(ev!.Value.Payload != null
                        && ev.Value.Payload.TryGetValue("-pos1", out var pos)
                        && pos.StartsWith("12"),
                    "every unit walks to the SAME point (one pospar for all — FUN_00530b90)");
            }
            Assert.AreEqual(0, b.PendingEvents.Count, "a structure never gets a walk order");
        }

        [Test]
        public void DropAll_FansOutToEverySelectedOwnUnit()
        {
            var (interp, ctx) = Host();
            var g1 = Spawn(interp, ctx, "G");
            var g2 = Spawn(interp, ctx, "G");
            ctx.Selection.Include(g1.Handle);
            ctx.Selection.Include(g2.Handle);
            var svc = new ClickToMoveService(ctx);
            svc.FireDropAll();
            foreach (var g in new[] { g1, g2 })
            {
                bool got = false;
                foreach (var e in g.PendingEvents) if (e.Name == "evt_task_dropall") got = true;
                Assert.IsTrue(got, $"unit #{g.Handle} got evt_task_dropall (FUN_0052c510 loops the selection)");
            }
        }
    }
}
