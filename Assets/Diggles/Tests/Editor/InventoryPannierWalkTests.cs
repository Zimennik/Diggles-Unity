#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Pins three laws around gnome basket/inventory transport:
    //
    // 1. is_wearing_pannier — native handler FUN_0069e930 (paired to the "is_wearing_pannier"
    // string at.rdata 0x732A2C by the registrar push sequence at 0x671AD2/0x671AE6) →
    // FUN_005cf820: scan the gnome's inventory for a type-9 container (item+0xe8 == 9) and
    // return 1 only when that container's own inventory is non-empty (005cf820:53-55 —
    // `if (0 < *(int*)(item+0x5c)) return 1`). An empty worn basket must not slow the gnome —
    // z_dignwalk.tcl:101-109 picks ANIMSET_WALKWITHPANNIER and kills the run speed off this
    // answer. Port flat-inventory equivalent (no nested container storage; the native size
    // model FUN_005ce010:61-196 overflows base-capacity-first into the pannier): "the basket
    // has content" ⇔ carried items exceed DigglesTclEngine.ZwergBaseInvSlots (= InvGetSize
    // FUN_005cde10 base 6) — exactly the items the inventory panel's basket grid renders.
    //
    // 2. Gnome inventory capacity — the 7th pickup without a basket must refuse (a flat 99-slot
    // inventory would let gnomes hoard forever). Effective capacity = base 6 + each carried
    // container's bonus (Holzkiepe +4 / Grosse_Holzkiepe +8 / Schubkarren +6 /
    // Hamsterkarren +6, the shared TransportSlotBonus table); the container itself occupies a
    // slot. Gates inv_add/inv_check; inv_getsize reports it (native InvGetSize).
    //
    // 3. Worn-basket visual (FUN_005cf940:221-412): a back-container (Holzkiepe /
    // Grosse_Holzkiepe) with content is link-attached to the carrier at link slot 5 (the
    // back;:268-275/:326-333) and made visible — the original renders the carried item's own
    // model there (that's why Grosse_Holzkiepe shows its big_holzkiepe.3db); empty → unlinked
    // + hidden. The wheelbarrow kind links at slot 0 (hands,:384-391) — not modelled yet, so
    // wheelbarrows must never back-link.
    public sealed class InventoryPannierWalkTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class Holzkiepe none transport 0 {} {}");
            interp.Eval("def_class Grosse_Holzkiepe none transport 0 {} {}");
            interp.Eval("def_class Schubkarren none transport 0 {} {}");
            interp.Eval("def_class M none material 0 {} {}");
            return (interp, ctx);
        }

        static Diggles.Runtime.Tcl.Engine.DigglesTclEntity Ent(DigglesTclContext ctx, string handle)
            => ctx.Entities[int.Parse(handle.Trim())];

        // ----- law 1: pannier walk only with basket CONTENT -----

        [Test]
        public void EmptyBasket_DoesNotTriggerPannierWalk()
        {
            var (interp, _) = Host();
            string g = interp.Eval("new G").Trim();
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(), "no basket at all");
            interp.Eval($"inv_add {g} [new Holzkiepe]");
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "an EMPTY worn basket must not trigger the pannier walk (FUN_005cf820:53-55)");
        }

        [Test]
        public void BasketWithOverflowContent_TriggersPannierWalk()
        {
            var (interp, _) = Host();
            string g = interp.Eval("new G").Trim();
            interp.Eval($"inv_add {g} [new Holzkiepe]");
            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots - 1; n++)
                interp.Eval($"inv_add {g} [new M]");
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "base slots still cover everything — the basket region is empty");
            interp.Eval($"inv_add {g} [new M]");
            Assert.AreEqual("1", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "the first item beyond base capacity lives in the basket → pannier walk");
        }

        [Test]
        public void FullBaseInventory_WithoutBasket_DoesNotTriggerPannierWalk()
        {
            var (interp, _) = Host();
            string g = interp.Eval("new G").Trim();
            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots; n++)
                interp.Eval($"inv_add {g} [new M]");
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "no transport container carried — never the pannier walk");
        }

        // ----- law 2: capacity gate -----

        [Test]
        public void CapacityLaw_SeventhItemRefusedWithoutBasket()
        {
            var (interp, _) = Host();
            string g = interp.Eval("new G").Trim();
            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots; n++)
                Assert.AreNotEqual("-1", interp.Eval($"inv_add {g} [new M]").Trim(),
                    $"item {n + 1} must fit the base grid");
            Assert.AreEqual("0", interp.Eval($"inv_check {g}").Trim(), "base grid is full");
            Assert.AreEqual("-1", interp.Eval($"inv_add {g} [new M]").Trim(),
                "the 7th item must be refused without a basket");
            Assert.AreEqual(DigglesTclEngine.ZwergBaseInvSlots.ToString(),
                interp.Eval($"inv_cnt {g}").Trim());
            Assert.AreEqual(DigglesTclEngine.ZwergBaseInvSlots.ToString(),
                interp.Eval($"inv_getsize {g}").Trim(), "native InvGetSize = base 6");
        }

        [Test]
        public void CapacityLaw_BasketRaisesCapacity()
        {
            var (interp, _) = Host();
            string g = interp.Eval("new G").Trim();
            interp.Eval($"inv_add {g} [new Holzkiepe]");
            Assert.AreEqual("10", interp.Eval($"inv_getsize {g}").Trim(),
                "Holzkiepe: 6 base + 4 basket slots");
            for (int n = 0; n < 9; n++)
                Assert.AreNotEqual("-1", interp.Eval($"inv_add {g} [new M]").Trim(),
                    $"item {n + 1} must fit (basket occupies one slot, 9 others fit)");
            Assert.AreEqual("-1", interp.Eval($"inv_add {g} [new M]").Trim(),
                "the 11th item exceeds 6+4");
            Assert.AreEqual("1", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "a full basket is definitely the pannier walk");
        }

        [Test]
        public void CapacityLaw_NonGnomeKeepsFlatCapacity()
        {
            var (interp, _) = Host();
            string crate = interp.Eval("new M").Trim();   // any non-gnome entity
            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots + 4; n++)
                Assert.AreNotEqual("-1", interp.Eval($"inv_add {crate} [new M]").Trim(),
                    "non-gnome containers keep the flat entity capacity (no base-6 gate)");
        }

        // ----- law 3: worn-basket visual (back link slot 5) -----

        [Test]
        public void BasketVisual_LinksToBackOnlyWithContent()
        {
            var (interp, ctx) = Host();
            string g = interp.Eval("new G").Trim();
            string k = interp.Eval("new Holzkiepe").Trim();
            interp.Eval($"inv_add {g} {k}");
            var kiepe = Ent(ctx, k);
            Assert.AreEqual(0, kiepe.LinkedToHandle, "empty basket stays off the back");
            Assert.AreEqual("0", kiepe.VarsScope.Vars["_visibility"], "empty basket stays hidden");

            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots; n++)
                interp.Eval($"inv_add {g} [new M]");   // 7 total → basket region non-empty
            Assert.AreEqual(int.Parse(g), kiepe.LinkedToHandle,
                "loaded basket link-attaches to the carrier (FUN_005cf940 → +0x278 = carrier)");
            Assert.AreEqual(DigglesTclEngine.BackLinkSlot, kiepe.LinkedSlot,
                "back link slot 5 (FUN_005cf940:275)");
            Assert.AreEqual("1", kiepe.VarsScope.Vars["_visibility"], "loaded basket is visible");
            Assert.AreEqual(g, interp.Eval($"get_linked_to {k}").Trim(), "get_linked_to de-stubbed");

            interp.Eval($"inv_rem {g} [inv_find {g} M]");   // back to 6 → basket empties
            Assert.AreEqual(0, kiepe.LinkedToHandle, "emptied basket detaches from the back");
            Assert.AreEqual("0", kiepe.VarsScope.Vars["_visibility"], "emptied basket hides again");
        }

        [Test]
        public void BasketVisual_DroppingTheBasketDetaches()
        {
            var (interp, ctx) = Host();
            string g = interp.Eval("new G").Trim();
            string k = interp.Eval("new Holzkiepe").Trim();
            interp.Eval($"inv_add {g} {k}");
            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots; n++)
                interp.Eval($"inv_add {g} [new M]");
            Assert.AreEqual(int.Parse(g), Ent(ctx, k).LinkedToHandle, "precondition: worn + shown");

            interp.Eval($"inv_rem {g} {k}");
            Assert.AreEqual(0, Ent(ctx, k).LinkedToHandle, "dropped basket is no longer linked");
            Assert.AreEqual("1", Ent(ctx, k).VarsScope.Vars["_visibility"],
                "dropped basket is visible on the ground (inv_rem restore)");
        }

        [Test]
        public void BasketVisual_ConsumingItemsViaDel_HidesTheBasket()
        {
            // Regression: building/consuming from inventory deletes the items (bar.tcl-style
            // `del` on a contained item) — CmdDel used to leave the dead handle in the carrier's
            // inventory, so the count never dropped and the basket stayed on forever (and
            // capacity slots leaked, and the pannier walk stuck slow).
            var (interp, ctx) = Host();
            string g = interp.Eval("new G").Trim();
            string k = interp.Eval("new Holzkiepe").Trim();
            interp.Eval($"inv_add {g} {k}");
            string firstItem = interp.Eval("new M").Trim();
            interp.Eval($"inv_add {g} {firstItem}");
            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots - 1; n++)
                interp.Eval($"inv_add {g} [new M]");
            Assert.AreEqual(int.Parse(g), Ent(ctx, k).LinkedToHandle, "precondition: basket shown at 7 items");

            interp.Eval($"del {firstItem}");   // consume one item the raw way (no inv_rem first)
            Assert.AreEqual(DigglesTclEngine.ZwergBaseInvSlots.ToString(),
                interp.Eval($"inv_cnt {g}").Trim(), "the dead handle must leave the inventory");
            Assert.AreEqual(0, Ent(ctx, k).LinkedToHandle,
                "back to 6 carried → the basket hides (del runs the FUN_005cf940 recompute)");
            Assert.AreEqual("0", Ent(ctx, k).VarsScope.Vars["_visibility"]);
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "and the pannier walk releases too");
        }

        [Test]
        public void BasketVisual_WheelbarrowNeverBackLinks()
        {
            var (interp, ctx) = Host();
            string g = interp.Eval("new G").Trim();
            string w = interp.Eval("new Schubkarren").Trim();
            interp.Eval($"inv_add {g} {w}");
            for (int n = 0; n < DigglesTclEngine.ZwergBaseInvSlots + 2; n++)
                interp.Eval($"inv_add {g} [new M]");
            Assert.AreEqual(0, Ent(ctx, w).LinkedToHandle,
                "the wheelbarrow kind links at hand slot 0 natively (FUN_005cf940:391) — " +
                "not modelled, must never ride the back");
        }
    }
}
