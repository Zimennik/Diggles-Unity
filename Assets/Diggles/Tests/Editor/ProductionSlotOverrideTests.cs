#nullable enable

namespace Diggles.Tests
{
    using System.Reflection;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.UI;
    using NUnit.Framework;

    public sealed class ProductionSlotOverrideTests
    {
        static (TclInterpreter Interp, DigglesTclContext Context) Host()
        {
            var interpreter = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            var context = DigglesTclEngine.Register(interpreter);
            _ = new DigglesGameSim(context);
            context.TolerateMissingClass = true;
            context.TolerateMissingFiles = true;
            context.IsStoryMgr = true;
            return (interpreter, context);
        }

        static DigglesTclEntity ProductionBuilding(TclInterpreter interpreter, DigglesTclContext context)
        {
            interpreter.Eval("def_class OverrideFactory none production 0 {} {\n"
                + "method prod_items {} {return {ProductA ProductB ProductC}}\n"
                + "method prod_item_materials {item} {return {}}\n"
                + "method prod_item_attribs {item} {return {}}\n}");
            var building = context.GetEntity(
                interpreter.Eval("new OverrideFactory {} {20 20 8} {0 0 0}").Trim())!;
            building.Owner = 0;
            building.BuildupComplete = true;
            return building;
        }

        static void Tick(BottomMenuController controller)
            => controller.GetType().GetMethod("VContainer.Unity.ITickable.Tick",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null);

        static void Refresh(BottomMenuController controller, DigglesTclContext context, int handle)
        {
            context.SelectedHandle = 0;
            Tick(controller);
            context.SelectedHandle = handle;
            Tick(controller);
        }

        [Test]
        public void Command_ImplementsNativeStatesQueryResetAndValidation()
        {
            var (interpreter, context) = Host();

            Assert.That(interpreter.Eval("prodslot_override query 0"), Is.EqualTo("0"));
            interpreter.Eval("prodslot_override disable 0");
            interpreter.Eval("prodslot_override HIDE 31");
            Assert.That(interpreter.Eval("prodslot_override query 0"), Is.EqualTo("1"));
            Assert.That(interpreter.Eval("prodslot_override query 31"), Is.EqualTo("2"));
            Assert.That(context.ProdSlotOverrides[0], Is.EqualTo(ProductionSlotOverride.Disabled));

            interpreter.Eval("prodslot_override normal 0");
            Assert.That(interpreter.Eval("prodslot_override query 0"), Is.EqualTo("0"));
            interpreter.Eval("prodslot_override reset ignored_by_native");
            Assert.That(interpreter.Eval("prodslot_override query 31"), Is.EqualTo("0"));

            Assert.Throws<TclException>(() => interpreter.Eval("prodslot_override disable -1"));
            Assert.Throws<TclException>(() => interpreter.Eval("prodslot_override hide 32"));
            Assert.Throws<TclException>(() => interpreter.Eval("prodslot_override normal"));
            Assert.Throws<TclException>(() => interpreter.Eval("prodslot_override unknown 0"));
        }

        [Test]
        public void ProductPositions_DisableBlocksInputAndHideRemovesCell()
        {
            var (interpreter, context) = Host();
            var building = ProductionBuilding(interpreter, context);
            var controller = new BottomMenuController(context);
            interpreter.Eval("prodslot_override disable 1");
            interpreter.Eval("prodslot_override hide 2");

            Refresh(controller, context, building.Handle);

            var first = controller.Model.Entries.Find(entry => entry.Id == "prod:ProductA");
            var second = controller.Model.Entries.Find(entry => entry.Id == "prod:ProductB");
            var third = controller.Model.Entries.Find(entry => entry.Id == "prod:ProductC");
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.Disabled, Is.False);
            Assert.That(second, Is.Not.Null);
            Assert.That(second!.Disabled, Is.True);
            Assert.That(second.Dimmed, Is.True);
            Assert.That(third, Is.Null, "state 2 removes the product-list position from the window");

            controller.Click("prod:ProductB", rightButton: false);
            controller.Click("prod:ProductC", rightButton: false);
            Assert.That(interpreter.Eval($"get_prod_slot_cnt {building.Handle} ProductB"), Is.EqualTo("0"));
            Assert.That(interpreter.Eval($"get_prod_slot_cnt {building.Handle} ProductC"), Is.EqualTo("0"));
            controller.Click("prod:ProductA", rightButton: false);
            Assert.That(interpreter.Eval($"get_prod_slot_cnt {building.Handle} ProductA"), Is.EqualTo("1"));
        }

        [Test]
        public void Slot31_ControlsPackCellVisibilityAndInput()
        {
            var (interpreter, context) = Host();
            var building = ProductionBuilding(interpreter, context);
            var controller = new BottomMenuController(context);

            interpreter.Eval("prodslot_override disable 31");
            Refresh(controller, context, building.Handle);
            var pack = controller.Model.Entries.Find(entry => entry.Id == "act:pack");
            Assert.That(pack, Is.Not.Null);
            Assert.That(pack!.Disabled, Is.True);
            controller.Click("act:pack", rightButton: false);
            Assert.That(building.PackRequested, Is.False, "disabled slot 31 must not start packing");

            interpreter.Eval("prodslot_override hide 31");
            Refresh(controller, context, building.Handle);
            Assert.That(controller.Model.Entries.Find(entry => entry.Id == "act:pack"), Is.Null);

            interpreter.Eval("prodslot_override normal 31");
            Refresh(controller, context, building.Handle);
            Assert.That(controller.Model.Entries.Find(entry => entry.Id == "act:pack"), Is.Not.Null);
            controller.Click("act:pack", rightButton: false);
            Assert.That(building.PackRequested, Is.True);
        }

        [Test]
        public void DisabledIcon_ComposesOriginalCrossoutOverlay()
        {
            using var icons = new IconLoader(OriginalDataTestFixture.DataRoot, sharpen: false);
            var source = icons.Get("packtobox");
            var crossed = icons.GetCrossedOut("packtobox", product: false);

            Assert.That(source, Is.Not.Null);
            Assert.That(crossed, Is.Not.Null);
            Assert.That(crossed!.texture, Is.Not.SameAs(source!.texture));
            Assert.That(crossed.texture.GetPixels32(), Is.Not.EqualTo(source.texture.GetPixels32()),
                "the virtual Crossout icon must consume xoverlay_Crossout.tga, not only dim the cell");
        }
    }
}
