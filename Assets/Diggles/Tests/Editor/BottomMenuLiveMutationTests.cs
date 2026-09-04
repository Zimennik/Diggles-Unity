#nullable enable

namespace Diggles.Tests
{
    using System.Reflection;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.UI;
    using NUnit.Framework;

    // LIVE production-panel regressions. Native row selection is driven by the boxed, built,
    // request, and order-count state (FUN_00521c80:352-361; FUN_00520620:483-491).
    public sealed class BottomMenuLiveMutationTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static (TclInterpreter Interpreter, DigglesTclContext Context) Host()
        {
            var interpreter = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            interpreter.UnknownCommandHandler = (i, a) => "0";
            var context = DigglesTclEngine.Register(interpreter);
            context.TolerateMissingEntities = true;
            context.TolerateMissingClass = true;
            context.TolerateMissingFiles = true;
            context.LocalPlayer = 0;
            context.IsStoryMgr = true;
            _ = new DigglesGameSim(context);
            interpreter.Eval("def_class PanelGnome none gnome 0 {} {}");
            interpreter.Eval("def_class PanelFactory none production 0 {} {\n"
                + "method prod_items {} {return {Prod}}\n"
                + "method prod_item_materials {item} {return {}}\n"
                + "method prod_item_attribs {item} {return {{exp_Test 1}}}\n}");
            interpreter.Eval("def_attrib BpProd 0 1 1");
            return (interpreter, context);
        }

        private static DigglesTclEntity Building(
            TclInterpreter interpreter,
            DigglesTclContext context,
            int slotCount = 1)
        {
            var building = context.GetEntity(
                interpreter.Eval("new PanelFactory {} {20 20 8} {0 0 0}").Trim())!;
            building.Owner = 0;
            building.BuildupComplete = true;
            building.ProdSlots["Prod"] = slotCount;
            return building;
        }

        private static DigglesTclEntity Worker(TclInterpreter interpreter, DigglesTclContext context)
        {
            var worker = context.GetEntity(
                interpreter.Eval("new PanelGnome {} {20 20 8} {0 0 0}").Trim())!;
            worker.Owner = 0;
            return worker;
        }

        private static void Tick(BottomMenuController controller)
            => controller.GetType().GetMethod(
                    "VContainer.Unity.ITickable.Tick",
                    PrivateInstance)!
                .Invoke(controller, null);

        private static void HoldTimedRefreshGate(BottomMenuController controller)
            => typeof(BottomMenuController).GetField("_nextRefresh", PrivateInstance)!
                .SetValue(controller, float.PositiveInfinity);

        private static BottomMenuEntry RequireEntry(BottomMenuController controller, string id)
        {
            var entry = controller.Model.Entries.Find(candidate => candidate.Id == id);
            Assert.That(entry, Is.Not.Null, $"Expected model entry '{id}'.");
            return entry!;
        }

        [Test]
        public void PackOrderAndCancel_RefreshImmediatelyWithoutSelectionOrClockChange()
        {
            var (interpreter, context) = Host();
            interpreter.Eval("set_owner_attrib 0 BpProd 1");
            var building = Building(interpreter, context);
            var controller = new BottomMenuController(context);
            context.SelectedHandle = building.Handle;

            Tick(controller);
            Assert.That(RequireEntry(controller, "prod:Prod"), Is.Not.Null);
            Assert.That(building.PackRequested, Is.False);
            var initialPack = RequireEntry(controller, "act:pack");
            Assert.That(initialPack.Icon, Is.EqualTo("packtobox"));
            Assert.That(initialPack.Tooltip, Is.EqualTo("Pack into a crate"));
            int initialVersion = controller.Model.Version;

            HoldTimedRefreshGate(controller);
            context.Placement!.OrderPack(building.Handle);
            Assert.That(building.PackRequested, Is.True);
            Tick(controller);

            Assert.That(controller.Model.Version, Is.GreaterThan(initialVersion));
            Assert.That(controller.Model.Entries, Has.Count.EqualTo(1));
            var armedPack = RequireEntry(controller, "act:pack");
            Assert.That(armedPack.Icon, Is.EqualTo("S1packtobox"));
            Assert.That(armedPack.Tooltip, Is.EqualTo("Cancel"));
            Assert.That(controller.Model.Entries.Find(entry => entry.Id == "prod:Prod"), Is.Null);
            int armedVersion = controller.Model.Version;

            HoldTimedRefreshGate(controller);
            context.Placement.CancelPack(building.Handle);
            Assert.That(building.PackRequested, Is.False);
            Tick(controller);

            Assert.That(controller.Model.Version, Is.GreaterThan(armedVersion));
            Assert.That(RequireEntry(controller, "prod:Prod"), Is.Not.Null);
            var restoredPack = RequireEntry(controller, "act:pack");
            Assert.That(restoredPack.Icon, Is.EqualTo("packtobox"));
            Assert.That(restoredPack.Tooltip, Is.EqualTo("Pack into a crate"));
        }

        [Test]
        public void BoxingCompletion_RefreshesImmediatelyToUnpackOnly()
        {
            var (interpreter, context) = Host();
            interpreter.Eval("set_owner_attrib 0 BpProd 1");
            var building = Building(interpreter, context);
            var controller = new BottomMenuController(context);
            context.SelectedHandle = building.Handle;
            Tick(controller);

            HoldTimedRefreshGate(controller);
            context.Placement!.OrderPack(building.Handle);
            Tick(controller);
            Assert.That(RequireEntry(controller, "act:pack").Icon, Is.EqualTo("S1packtobox"));
            int armedVersion = controller.Model.Version;

            HoldTimedRefreshGate(controller);
            interpreter.Eval($"set_boxed {building.Handle} 1");
            Assert.That(building.PackRequested, Is.False);
            Tick(controller);

            Assert.That(controller.Model.Version, Is.GreaterThan(armedVersion));
            Assert.That(controller.Model.Entries, Has.Count.EqualTo(1));
            var unpack = RequireEntry(controller, "act:unpack");
            Assert.That(unpack.Icon, Is.EqualTo("unpack"));
            Assert.That(controller.Model.Entries.Find(entry => entry.Id == "act:pack"), Is.Null);
            Assert.That(controller.Model.Entries.Find(entry => entry.Id == "prod:Prod"), Is.Null);
        }

        [Test]
        public void UnpackOrder_RefreshesImmediatelyToPendingPresentation()
        {
            var (interpreter, context) = Host();
            var building = Building(interpreter, context);
            interpreter.Eval($"set_boxed {building.Handle} 1");
            var controller = new BottomMenuController(context);
            context.SelectedHandle = building.Handle;

            Tick(controller);
            var initialUnpack = RequireEntry(controller, "act:unpack");
            Assert.That(initialUnpack.Icon, Is.EqualTo("unpack"));
            Assert.That(initialUnpack.Dimmed, Is.False);
            int initialVersion = controller.Model.Version;

            HoldTimedRefreshGate(controller);
            interpreter.Eval($"set_prod_unpack {building.Handle} 1");
            Assert.That(building.UnpackRequested, Is.True);
            Tick(controller);

            Assert.That(controller.Model.Version, Is.GreaterThan(initialVersion));
            Assert.That(controller.Model.Entries, Has.Count.EqualTo(1));
            var pendingUnpack = RequireEntry(controller, "act:unpack");
            Assert.That(pendingUnpack.Icon, Is.EqualTo("S1unpack"));
            Assert.That(pendingUnpack.Dimmed, Is.True);
        }

        [Test]
        public void InventionCancel_RefreshesImmediatelyAndClearsProgressIndicator()
        {
            var (interpreter, context) = Host();
            interpreter.Eval("set_owner_attrib 0 BpProd 0");
            var building = Building(interpreter, context);
            var worker = Worker(interpreter, context);
            building.CurrentWorkerHandle = worker.Handle;
            building.CurrentProdItem = "Prod";
            building.ProdProgressCur = 1;
            building.ProdProgressMax = 2;
            var controller = new BottomMenuController(context);
            context.SelectedHandle = building.Handle;

            Tick(controller);
            var activeInvention = RequireEntry(controller, "prod:Prod");
            Assert.That(activeInvention.Invention, Is.Not.EqualTo(BottomMenuInventionState.None));
            Assert.That(activeInvention.Progress, Is.EqualTo(100f));
            Assert.That(activeInvention.ProgressKind, Is.EqualTo(BottomMenuProgressKind.Delivering));
            int activeVersion = controller.Model.Version;

            HoldTimedRefreshGate(controller);
            interpreter.Eval($"set_prod_slot_cnt {building.Handle} Prod 0");
            interpreter.Eval($"gnome_failed_work {worker.Handle}");
            Assert.That(building.ProdProgressCur, Is.Zero);
            Assert.That(building.ProdProgressMax, Is.Zero);
            Tick(controller);

            Assert.That(controller.Model.Version, Is.GreaterThan(activeVersion));
            var cancelledInvention = RequireEntry(controller, "prod:Prod");
            Assert.That(cancelledInvention.Progress, Is.EqualTo(-1f));
            Assert.That(cancelledInvention.ProgressKind, Is.EqualTo(BottomMenuProgressKind.None));
        }

        // The native production toggle arms switch-mode slots with the sentinel value 10, not
        // a finite 1 (FUN_005242b0:232-255), and info_end_prod preserves the sentinel
        // (FUN_0069e210:82-96). Arming with 1 let the first completion decrement to 0, which
        // farm.tcl:171-175 reads as cancellation -> free_parenthamsters: regression: hamster
        // breeding cascade (escapes, dead breed button, random order cancels).
        [Test]
        public void SwitchModeToggle_ArmsTheNativeSentinelTen()
        {
            var (interpreter, context) = Host();
            interpreter.Eval("set_owner_attrib 0 BpProd 1");
            var building = Building(interpreter, context, slotCount: 0);
            interpreter.Eval($"set_prod_switchmode {building.Handle} 1");
            var controller = new BottomMenuController(context);
            context.SelectedHandle = building.Handle;

            Tick(controller);
            RequireEntry(controller, "prod:Prod");
            Assert.That(interpreter.Eval($"get_prod_slot_list {building.Handle}").Trim(),
                Does.Contain("Prod"), "the click path's catalog read must resolve");
            Assert.That(interpreter.Eval($"get_prod_switchmode {building.Handle}").Trim(),
                Is.EqualTo("1"), "switch mode must be armed on the entity");
            controller.Click("prod:Prod", false);
            Assert.That(building.ProdSlots["Prod"], Is.EqualTo(10d),
                "switch-mode arm must write the native sentinel 10");

            controller.Click("prod:Prod", false);
            double off = building.ProdSlots.TryGetValue("Prod", out double v) ? v : 0d;
            Assert.That(off, Is.Zero, "second click must toggle the slot off");
        }
    }
}
