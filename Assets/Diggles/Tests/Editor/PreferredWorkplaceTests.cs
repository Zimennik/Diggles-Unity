#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using System.Reflection;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Native preferred-workplace law: FUN_006a81b0 setter, FUN_00438210 candidate score,
    // FUN_00433710 claimed-site exclusivity, and FUN_005d0ce0 planner sub+0x40 persistence.
    public sealed class PreferredWorkplaceTests
    {
        private static (TclInterpreter Interp, DigglesTclContext Context, DigglesGameSim Sim)
            Host(bool invented = true)
        {
            var interp = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            interp.UnknownCommandHandler = (i, a) => "0";
            DigglesTclContext context = DigglesTclEngine.Register(interp);
            context.TolerateMissingEntities = true;
            context.TolerateMissingClass = true;
            context.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(context);
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class B none production 0 {} {\n"
                + "method prod_items {} {return {Prod}}\n"
                + "method prod_item_materials {item} {return {}}\n"
                + "method prod_item_attribs {item} {return {}}\n}");
            interp.Eval($"def_attrib BpProd 0 1 {(invented ? 1 : 0)}");
            return (interp, context, sim);
        }

        private static DigglesTclEntity Spawn(
            TclInterpreter interp,
            DigglesTclContext context,
            string className,
            string position)
        {
            return context.GetEntity(
                interp.Eval($"new {className} \"\" \"{position}\" \"0 0 0\"").Trim())!;
        }

        private static DigglesTclEntity Gnome(
            TclInterpreter interp,
            DigglesTclContext context,
            string position,
            bool workTime = true)
        {
            DigglesTclEntity gnome = Spawn(interp, context, "G", position);
            gnome.Owner = 0;
            if (workTime) interp.Eval($"set_worktime {gnome.Handle} 0 12");
            return gnome;
        }

        private static DigglesTclEntity Building(
            TclInterpreter interp,
            DigglesTclContext context,
            string position,
            int slots = 1)
        {
            DigglesTclEntity building = Spawn(interp, context, "B", position);
            building.Owner = 0;
            building.BuildupComplete = true;
            if (slots > 0) building.ProdSlots["Prod"] = slots;
            return building;
        }

        private static EntityEvent? FirstEvent(DigglesTclEntity entity, string name)
        {
            foreach (EntityEvent pending in entity.PendingEvents)
                if (pending.Name == name) return pending;
            return null;
        }

        private static void AssertWorkTarget(DigglesTclEntity gnome, DigglesTclEntity building)
        {
            EntityEvent? pending = FirstEvent(gnome, "evt_autoprod_workat");
            Assert.That(pending, Is.Not.Null, "the expected production assignment was not emitted");
            Assert.That(pending!.Value.Payload!["-subject1"],
                Is.EqualTo(building.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        [Test]
        public void Command_ToggleLawTable_StoresInFirstClassField()
        {
            var (interp, context, _) = Host();
            DigglesTclEntity gnome = Gnome(interp, context, "0 0 0");
            DigglesTclEntity first = Building(interp, context, "1 0 0", 0);
            DigglesTclEntity second = Building(interp, context, "2 0 0", 0);
            var cases = new (string Label, int Initial, int? Target, int Expected)[]
            {
                ("set", -1, first.Handle, first.Handle),
                ("same handle clears", first.Handle, first.Handle, -1),
                ("different handle replaces", first.Handle, second.Handle, second.Handle),
                ("missing target clears", first.Handle, null, -1),
                ("invalid target clears", first.Handle, 999_999, -1),
            };

            foreach ((string label, int initial, int? target, int expected) in cases)
            {
                gnome.PreferredWorkplaceHandle = initial;
                string command = target.HasValue
                    ? $"prod_gnome_preferred_workplace {gnome.Handle} {target.Value}"
                    : $"prod_gnome_preferred_workplace {gnome.Handle}";
                interp.Eval(command);
                Assert.That(gnome.PreferredWorkplaceHandle, Is.EqualTo(expected), label);
                string getter = interp.Eval(
                    $"prod_gnome_get_preferred_workplace {gnome.Handle}");
                Assert.That(getter, Is.EqualTo(expected > 0
                    ? expected.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "0"), label);
            }

            Assert.That(gnome.VarsScope.Vars.ContainsKey("_preferred_workplace"), Is.False,
                "the removed Tcl compatibility variable must not be recreated");
            TclException? noGnome = Assert.Throws<TclException>(() => interp.Eval(
                $"prod_gnome_preferred_workplace {first.Handle} {second.Handle}"));
            Assert.That(noGnome!.Message, Does.Contain("no gnome"));
        }

        [Test]
        public void RegularProduction_PreferredBuildingWinsAtDistance()
        {
            var (interp, context, _) = Host();
            DigglesTclEntity gnome = Gnome(interp, context, "0 0 0");
            DigglesTclEntity near = Building(interp, context, "2 0 0");
            DigglesTclEntity preferred = Building(interp, context, "40 0 0");
            interp.Eval(
                $"prod_gnome_preferred_workplace {gnome.Handle} {preferred.Handle}");

            ProdDispatcher dispatcher = context.ProdDispatcher!;
            dispatcher.OnGnomeIdle(gnome);
            dispatcher.SweepNow(0);

            AssertWorkTarget(gnome, preferred);
            Assert.That(near.CurrentWorkerHandle, Is.EqualTo(-1));
        }

        [Test]
        public void RegularProduction_OtherBuildingPenaltyLetsUnboundGnomeWin()
        {
            var (interp, context, _) = Host();
            DigglesTclEntity home = Building(interp, context, "20 0 0", 0);
            DigglesTclEntity job = Building(interp, context, "1 0 0");
            DigglesTclEntity preferredGnome = Gnome(interp, context, "0 0 0");
            DigglesTclEntity unboundGnome = Gnome(interp, context, "5 0 0");
            interp.Eval(
                $"prod_gnome_preferred_workplace {preferredGnome.Handle} {home.Handle}");

            ProdDispatcher dispatcher = context.ProdDispatcher!;
            dispatcher.OnGnomeIdle(preferredGnome);
            dispatcher.OnGnomeIdle(unboundGnome);
            dispatcher.SweepNow(0);

            Assert.That(FirstEvent(preferredGnome, "evt_autoprod_workat"), Is.Null,
                "the nearer gnome must carry the native x10 other-building distance penalty");
            AssertWorkTarget(unboundGnome, job);
        }

        [Test]
        public void ClaimedProductionSite_IsExclusiveUntilPreferredGnomeCanWork()
        {
            var (interp, context, _) = Host();
            DigglesTclEntity site = Building(interp, context, "5 0 0");
            DigglesTclEntity claimant = Gnome(interp, context, "30 0 0", workTime: false);
            DigglesTclEntity other = Gnome(interp, context, "5 0 0");
            interp.Eval(
                $"prod_gnome_preferred_workplace {claimant.Handle} {site.Handle}");

            ProdDispatcher dispatcher = context.ProdDispatcher!;
            dispatcher.OnGnomeIdle(other);
            dispatcher.SweepNow(0);
            Assert.That(FirstEvent(other, "evt_autoprod_workat"), Is.Null,
                "a different/unbound gnome must not enter a claimed production site");
            Assert.That(site.CurrentWorkerHandle, Is.EqualTo(-1));

            interp.Eval($"set_worktime {claimant.Handle} 0 12");
            dispatcher.OnGnomeIdle(claimant);
            dispatcher.SweepNow(0);
            AssertWorkTarget(claimant, site);
        }

        [TestCase(false, TestName = "Dead preferred handle is ignored")]
        [TestCase(true, TestName = "Other-owner preferred handle is ignored")]
        public void InvalidPreference_DoesNotPenalizeDispatcherCandidate(bool otherOwner)
        {
            var (interp, context, _) = Host();
            DigglesTclEntity job = Building(interp, context, "1 0 0");
            DigglesTclEntity near = Gnome(interp, context, "0 0 0");
            DigglesTclEntity far = Gnome(interp, context, "15 0 0");
            int storedHandle;
            if (otherOwner)
            {
                DigglesTclEntity foreign = Building(interp, context, "20 0 0", 0);
                foreign.Owner = 1;
                interp.Eval(
                    $"prod_gnome_preferred_workplace {near.Handle} {foreign.Handle}");
                storedHandle = foreign.Handle;
                Assert.That(interp.Eval(
                    $"prod_gnome_get_preferred_workplace {near.Handle}"),
                    Is.EqualTo(foreign.Handle.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                    "the native setter/getter has no ownership validation");
            }
            else
            {
                storedHandle = 999_999;
                near.PreferredWorkplaceHandle = storedHandle;
                Assert.That(interp.Eval(
                    $"prod_gnome_get_preferred_workplace {near.Handle}"), Is.EqualTo("0"));
            }

            ProdDispatcher dispatcher = context.ProdDispatcher!;
            dispatcher.OnGnomeIdle(near);
            dispatcher.OnGnomeIdle(far);
            dispatcher.SweepNow(0);

            AssertWorkTarget(near, job);
            Assert.That(FirstEvent(far, "evt_autoprod_workat"), Is.Null);
            Assert.That(near.PreferredWorkplaceHandle, Is.EqualTo(storedHandle),
                "dispatch validation must not scrub the persisted handle");
        }

        [Test]
        public void Invention_PreferredBuildingGetsEightfoldScoreBonus()
        {
            var (interp, context, _) = Host(invented: false);
            DigglesTclEntity gnome = Gnome(interp, context, "0 0 0");
            _ = Building(interp, context, "1 0 0");
            DigglesTclEntity preferred = Building(interp, context, "8 0 0");
            interp.Eval(
                $"prod_gnome_preferred_workplace {gnome.Handle} {preferred.Handle}");

            ProdDispatcher dispatcher = context.ProdDispatcher!;
            dispatcher.OnGnomeIdle(gnome);
            dispatcher.SweepNow(0);

            AssertWorkTarget(gnome, preferred);
            EntityEvent? pending = FirstEvent(gnome, "evt_autoprod_workat");
            Assert.That(pending!.Value.Payload!["-text1"], Is.EqualTo("BpProd"));
        }

        [Test]
        public void PackDispatch_IsUnchangedByPreferredWorkplace()
        {
            var (interp, context, _) = Host();
            DigglesTclEntity building = Building(interp, context, "1 0 0", 0);
            building.PackRequested = true;
            DigglesTclEntity preferredGnome = Gnome(interp, context, "20 0 0");
            DigglesTclEntity nearGnome = Gnome(interp, context, "0 0 0");
            interp.Eval(
                $"prod_gnome_preferred_workplace {preferredGnome.Handle} {building.Handle}");

            ProdDispatcher dispatcher = context.ProdDispatcher!;
            dispatcher.OnGnomeIdle(preferredGnome);
            dispatcher.OnGnomeIdle(nearGnome);
            dispatcher.SweepNow(0);

            Assert.That(building.CurrentWorkerHandle, Is.EqualTo(nearGnome.Handle),
                "preferred-workplace scoring is restricted to production/invention dispositions");
            Assert.That(dispatcher.HasPlan(nearGnome.Handle), Is.True);
            Assert.That(dispatcher.HasPlan(preferredGnome.Handle), Is.False);
        }

        [Test]
        public void PreferredWorkplace_RoundTripsThroughFourthPlannerWord()
        {
            var (interp, context, _) = Host();
            DigglesTclEntity gnome = Gnome(interp, context, "0 0 0");
            DigglesTclEntity building = Building(interp, context, "1 0 0", 0);
            interp.Eval(
                $"prod_gnome_preferred_workplace {gnome.Handle} {building.Handle}");

            MethodInfo? captureMethod = typeof(OriginalSaveRuntimePatchMapper).GetMethod(
                "CapturePlannerState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(captureMethod, Is.Not.Null);
            var captured = (OriginalSaveCanonicalPlannerState?)captureMethod!.Invoke(
                null, new object[] { gnome, context });
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.AssignedWorkplaceObjectId, Is.EqualTo(building.Handle));

            MethodInfo? writeMethod = typeof(OriginalSaveStructureBuilder).GetMethod(
                "WriteCanonicalPlanner", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(writeMethod, Is.Not.Null);
            byte[] plannerWords;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writeMethod!.Invoke(null, new object?[] { writer, gnome.Handle, captured });
                writer.Flush();
                plannerWords = stream.ToArray();
            }

            Assert.That(plannerWords, Has.Length.EqualTo(sizeof(int) * 4));
            int restoredSlot = BitConverter.ToInt32(plannerWords, sizeof(int) * 3);
            var restored = new DigglesTclEntity
            {
                Class = gnome.Class,
                NativeObjectType = 1,
            };
            restored.AssignedWorkplaceId = restoredSlot;
            Assert.That(restored.PreferredWorkplaceHandle, Is.EqualTo(building.Handle));
        }
    }
}
