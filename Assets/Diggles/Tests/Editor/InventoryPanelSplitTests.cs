#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.UI;
    using NUnit.Framework;

    // Pins the two-grid inventory display split: MAIN = the gnome's base grid (InvGetSize
    // FUN_005cde10 → Zwerg base 6), EXTRA = the worn-basket bonus grid (Holzkiepe +4 /
    // Grosse_Holzkiepe +8 / Schubkarren +6 / Hamsterkarren +6) that the view hides entirely at 0.
    // Overflow beyond main+bonus lands in EXTRA so a carried item is never hidden by the panel.
    public sealed class InventoryPanelSplitTests
    {
        [Test]
        public void NoBasket_BaseGridOnly()
        {
            var (main, extra) = InventoryPanelController.SplitSlots(InventoryPanelController.BaseSlots, 0, 3);
            Assert.AreEqual(6, main);
            Assert.AreEqual(0, extra, "no basket → the additional container stays hidden");
        }

        [Test]
        public void Holzkiepe_AddsFourExtraSlots()
        {
            var (main, extra) = InventoryPanelController.SplitSlots(InventoryPanelController.BaseSlots, 4, 5);
            Assert.AreEqual(6, main);
            Assert.AreEqual(4, extra);
        }

        [Test]
        public void GrosseHolzkiepe_AddsEightExtraSlots_EvenWhenEmpty()
        {
            var (main, extra) = InventoryPanelController.SplitSlots(InventoryPanelController.BaseSlots, 8, 0);
            Assert.AreEqual(6, main);
            Assert.AreEqual(8, extra, "capacity renders as empty cells, not just filled ones");
        }

        [Test]
        public void Overflow_WithoutBasket_SpillsIntoExtraGrid()
        {
            // Safety net: engine inventories should not exceed capacity, but the panel must
            // never hide a carried item — the surplus renders in the additional grid.
            var (main, extra) = InventoryPanelController.SplitSlots(InventoryPanelController.BaseSlots, 0, 9);
            Assert.AreEqual(6, main);
            Assert.AreEqual(3, extra);
        }

        [Test]
        public void Overflow_BeyondBasketBonus_GrowsExtraGrid()
        {
            var (main, extra) = InventoryPanelController.SplitSlots(InventoryPanelController.BaseSlots, 4, 12);
            Assert.AreEqual(6, main);
            Assert.AreEqual(6, extra, "12 items − 6 main = 6 > the Holzkiepe bonus 4");
        }
    }
}
