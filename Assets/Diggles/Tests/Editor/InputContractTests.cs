#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    public sealed class InputContractTests
    {
        private static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            ctx.LocalPlayer = 0;
            interp.Eval("def_class G none gnome 0 {} {}");
            return (interp, ctx);
        }

        private static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx)
        {
            var entity = ctx.GetEntity(interp.Eval("new G \"\" \"0 0 0\" \"0 0 0\"").Trim())!;
            entity.Owner = ctx.LocalPlayer;
            return entity;
        }

        [Test]
        public void ResolveCrateClick_PlainDefersToEventGenerator()
        {
            var result = ClickToMoveService.ResolveCrateClick(
                ownBoxedCrate: true, gnomeSelected: true, shiftHeld: false, altHeld: false);

            Assert.AreEqual(ClickToMoveService.CrateClickVerb.EventGen, result);
            Assert.AreNotEqual(ClickToMoveService.CrateClickVerb.PickupOrder, result);
        }

        [Test]
        public void ResolveCrateClick_ShiftOrdersPickup()
        {
            var result = ClickToMoveService.ResolveCrateClick(
                ownBoxedCrate: true, gnomeSelected: true, shiftHeld: true, altHeld: false);

            Assert.AreEqual(ClickToMoveService.CrateClickVerb.PickupOrder, result);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResolveCrateClick_AltDefersToEventGenerator(bool shiftHeld)
        {
            var result = ClickToMoveService.ResolveCrateClick(
                ownBoxedCrate: true, gnomeSelected: true,
                shiftHeld: shiftHeld, altHeld: true);

            Assert.AreEqual(ClickToMoveService.CrateClickVerb.EventGen, result);
        }

        [TestCase(false, true, false, false)]
        [TestCase(false, true, false, true)]
        [TestCase(false, true, true, false)]
        [TestCase(false, true, true, true)]
        [TestCase(true, false, false, false)]
        [TestCase(true, false, false, true)]
        [TestCase(true, false, true, false)]
        [TestCase(true, false, true, true)]
        public void ResolveCrateClick_NonCrateOrNoGnomeAlwaysDefers(
            bool ownBoxedCrate, bool gnomeSelected, bool shiftHeld, bool altHeld)
        {
            var result = ClickToMoveService.ResolveCrateClick(
                ownBoxedCrate, gnomeSelected, shiftHeld, altHeld);

            Assert.AreEqual(ClickToMoveService.CrateClickVerb.EventGen, result);
        }

        // The former FogBlocksCursor_OnlyZeroBlocks pin was dropped: it pinned
        // ClickToMoveService.FogBlocksCursor, a legacy raw-byte classifier the runtime no longer
        // calls — the live input gate is the sim's FowHidden verdict, pinned through the real
        // producer in InputInteractionTests.FogVerdict_RealSimPass_PinsGrayFogRenderLaw.

        [Test]
        public void PruneContained_RemovesContainedAndBumpsVersionOnce()
        {
            var (interp, ctx) = Host();
            var free = Spawn(interp, ctx);
            var contained = Spawn(interp, ctx);
            ctx.Selection.Include(free.Handle);
            ctx.Selection.Include(contained.Handle);
            contained.ContainedIn = free.Handle;
            int versionBefore = ctx.Selection.Version;

            Assert.IsTrue(ctx.Selection.PruneContained());
            Assert.AreEqual(versionBefore + 1, ctx.Selection.Version);
            Assert.AreEqual(1, ctx.Selection.Count);
            Assert.AreEqual(free.Handle, ctx.Selection.Primary);
            Assert.IsFalse(ctx.Selection.Contains(contained.Handle));
        }

        [Test]
        public void PruneContained_WhenNothingQualifies_IsIdempotent()
        {
            var (interp, ctx) = Host();
            var free = Spawn(interp, ctx);
            ctx.Selection.Include(free.Handle);
            int versionBefore = ctx.Selection.Version;

            Assert.IsFalse(ctx.Selection.PruneContained());
            Assert.AreEqual(versionBefore, ctx.Selection.Version);
            Assert.AreEqual(free.Handle, ctx.Selection.Primary);
        }

        [Test]
        public void PruneContained_LoneContainedPrimaryEmptiesSelection()
        {
            var (interp, ctx) = Host();
            var contained = Spawn(interp, ctx);
            ctx.Selection.Include(contained.Handle);
            contained.ContainedIn = 123;

            Assert.IsTrue(ctx.Selection.PruneContained());
            Assert.AreEqual(0, ctx.Selection.Count);
            Assert.AreEqual(0, ctx.Selection.Primary);
        }

        [Test]
        public void PruneContained_UnresolvablePrimaryEmptiesSelection()
        {
            var (interp, ctx) = Host();
            var removedEntity = Spawn(interp, ctx);
            ctx.Selection.Include(removedEntity.Handle);
            ctx.Entities.Remove(removedEntity.Handle);
            int versionBefore = ctx.Selection.Version;

            Assert.IsTrue(ctx.Selection.PruneContained());
            Assert.AreEqual(versionBefore + 1, ctx.Selection.Version);
            Assert.AreEqual(0, ctx.Selection.Primary);
        }

        [Test]
        public void ClickSelect_AdditiveCompositionRemainsUnchanged()
        {
            var (interp, ctx) = Host();
            var first = Spawn(interp, ctx);
            var second = Spawn(interp, ctx);

            ctx.Selection.ClickSelect(first.Handle, additive: false);
            ctx.Selection.ClickSelect(second.Handle, additive: true);

            Assert.AreEqual(2, ctx.Selection.Count);
            Assert.AreEqual(first.Handle, ctx.Selection.Primary);
            Assert.IsTrue(ctx.Selection.Contains(second.Handle));
        }
    }
}
