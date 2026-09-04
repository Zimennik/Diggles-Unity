#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class EnergyRadiusOverlayTests
    {
        [Test]
        public void SelectionMapping_SourceUsesLiveBackendRange_DeselectAndNonSourceHide()
        {
            var (interpreter, context) = CreateHost();
            DigglesTclEntity source = Spawn(interpreter, context, "Power", new Vector3(12.5f, 31.25f, 16f));
            DigglesTclEntity consumer = Spawn(interpreter, context, "Workshop", new Vector3(18f, 22f, 16f));
            interpreter.Eval($"set_energyrange {source.Handle} 60");
            interpreter.Eval($"set_energymaxstore {source.Handle} 400");
            interpreter.Eval($"set_energystore {source.Handle} 300");

            context.SelectedHandle = source.Handle;
            EnergyRadiusOverlay.OverlayState selected =
                EnergyRadiusOverlay.ResolveState(context, context.SelectedHandle);
            Assert.IsTrue(selected.Visible);
            Assert.AreEqual(source.Handle, selected.SelectedHandle);
            Assert.AreEqual(new Vector2(12.5f, 31.25f), selected.Center);
            Assert.AreEqual(60f, selected.Radius);
            Assert.AreEqual(0.75f, selected.StoreRatio);

            interpreter.Eval($"set_energyrange {source.Handle} 73.5");
            EnergyRadiusOverlay.OverlayState changed =
                EnergyRadiusOverlay.ResolveState(context, context.SelectedHandle);
            Assert.AreEqual(73.5f, changed.Radius);

            context.SelectedHandle = 0;
            Assert.IsFalse(EnergyRadiusOverlay.ResolveState(context, context.SelectedHandle).Visible);

            consumer.EnergyRange = 999f;
            context.SelectedHandle = consumer.Handle;
            Assert.IsFalse(EnergyRadiusOverlay.ResolveState(context, context.SelectedHandle).Visible);
        }

        [Test]
        public void SelectionMapping_PackedOrDeadSourceHides()
        {
            var (interpreter, context) = CreateHost();
            DigglesTclEntity source = Spawn(interpreter, context, "Power", new Vector3(4f, 7f, 16f));
            interpreter.Eval($"set_energyrange {source.Handle} 20");

            Assert.IsTrue(EnergyRadiusOverlay.ResolveState(context, source.Handle).Visible);

            source.VarsScope.Vars["_boxed"] = "1";
            Assert.IsFalse(EnergyRadiusOverlay.ResolveState(context, source.Handle).Visible);

            source.VarsScope.Vars.Remove("_boxed");
            source.ContainedIn = 42;
            Assert.IsFalse(EnergyRadiusOverlay.ResolveState(context, source.Handle).Visible);

            source.ContainedIn = -1;
            context.Entities.Remove(source.Handle);
            Assert.IsFalse(EnergyRadiusOverlay.ResolveState(context, source.Handle).Visible);
        }

        private static (TclInterpreter interpreter, DigglesTclContext context) CreateHost()
        {
            var interpreter = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            interpreter.UnknownCommandHandler = (_, _) => "0";
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            context.TolerateMissingEntities = true;
            context.TolerateMissingClass = true;
            context.TolerateMissingFiles = true;
            interpreter.Eval("def_class Power none energy 0 {} {}");
            interpreter.Eval("def_class Workshop none production 0 {} {}");
            return (interpreter, context);
        }

        private static DigglesTclEntity Spawn(
            TclInterpreter interpreter,
            DigglesTclContext context,
            string className,
            Vector3 position)
        {
            string handle = interpreter.Eval(
                $"new {className} \"\" \"0 0 0\" \"0 0 0\"").Trim();
            DigglesTclEntity entity = context.GetEntity(handle)!;
            entity.Position = position;
            return entity;
        }
    }
}
