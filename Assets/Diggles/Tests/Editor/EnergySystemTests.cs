#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class EnergySystemTests
    {
        static (TclInterpreter Interp, DigglesTclContext Context, DigglesGameSim Sim) Host()
        {
            var interp = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            var context = DigglesTclEngine.Register(interp);
            var sim = new DigglesGameSim(context);
            return (interp, context, sim);
        }

        static DigglesTclEntity Spawn(DigglesTclContext context, string name, string kind,
            int owner, Vector3 position)
        {
            if (!context.Classes.TryGetValue(name, out var cls))
            {
                cls = new DigglesClass { Name = name, Kind = kind };
                context.Classes.Add(name, cls);
            }
            var entity = new DigglesTclEntity
            {
                Class = cls,
                Owner = owner,
                Position = position,
            };
            context.RegisterEntity(entity);
            return entity;
        }

        static float Number(string value) =>
            float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

        static void ConfigureSource(DigglesTclEntity source, int energyClass, float range, float store)
        {
            source.BuildupComplete = true;
            source.EnergyClass = energyClass;
            source.EnergyRange = range;
            source.EnergyStore = store;
            source.EnergyMaxStore = store;
        }

        [Test]
        public void TclCommands_ReadAndWriteNativeEnergyFields()
        {
            var (interp, context, _) = Host();
            var source = Spawn(context, "TestSource", "energy", 0, Vector3.zero);

            interp.Eval($"set_energyrange {source.Handle} 21.5");
            interp.Eval($"set_energyclass {source.Handle} 2");
            interp.Eval($"set_energyconsumption {source.Handle} 7.25");
            interp.Eval($"set_energymaxstore {source.Handle} 400");
            interp.Eval($"set_energystore {source.Handle} 123.5");

            Assert.That(source.EnergyRange, Is.EqualTo(21.5f));
            Assert.That(source.EnergyClass, Is.EqualTo(2));
            Assert.That(source.EnergyConsumption, Is.EqualTo(7.25f));
            Assert.That(source.EnergyMaxStore, Is.EqualTo(400f));
            Assert.That(Number(interp.Eval($"get_energystore {source.Handle}")), Is.EqualTo(123.5f));
        }

        [Test]
        public void CheckEnergy_UsesOwnerClassBuildStoreAndStrictHorizontalRange()
        {
            var (interp, context, _) = Host();
            var source = Spawn(context, "TestSource", "energy", 0, Vector3.zero);
            var consumer = Spawn(context, "TestConsumer", "production", 0,
                new Vector3(9.99f, 0f, 500f));
            ConfigureSource(source, 2, 10f, 20f);
            consumer.EnergyClass = 1;
            consumer.EnergyConsumption = 10f;

            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("1"),
                "height is ignored and a higher-class source can feed a lower-class consumer");

            consumer.Position = new Vector3(10f, 0f, 0f);
            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("0"),
                "the native range comparison is strict");
            consumer.Position = new Vector3(1f, 0f, 0f);
            consumer.EnergyClass = 3;
            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("0"));
            consumer.EnergyClass = 1;
            source.BuildupComplete = false;
            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("0"));
            source.BuildupComplete = true;
            source.Owner = 1;
            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("0"));
            source.Owner = 0;
            source.VarsScope.Vars["_prod_enabled"] = "0";
            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("0"));
            source.VarsScope.Vars.Remove("_prod_enabled");
            source.EnergyStore = 9f;
            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("0"));
            consumer.EnergyConsumption = 0f;
            Assert.That(interp.Eval($"check_energy {consumer.Handle}"), Is.EqualTo("1"));
        }

        [Test]
        public void Acquire_SelectsFullestSourceAndTracksIdempotentLoadUntilRelease()
        {
            var (interp, context, _) = Host();
            var lower = Spawn(context, "LowerSource", "energy", 0, Vector3.zero);
            var fuller = Spawn(context, "FullerSource", "energy", 0, new Vector3(2f, 0f, 0f));
            var consumer = Spawn(context, "TestConsumer", "production", 0, new Vector3(1f, 0f, 0f));
            ConfigureSource(lower, 1, 20f, 15f);
            ConfigureSource(fuller, 1, 20f, 30f);
            consumer.EnergyClass = 1;
            consumer.EnergyConsumption = 10f;

            Assert.That(interp.Eval($"get_energy {consumer.Handle}"), Is.EqualTo("1"));
            Assert.That(lower.EnergyStore, Is.EqualTo(15f));
            Assert.That(fuller.EnergyStore, Is.EqualTo(20f));
            Assert.That(Number(interp.Eval($"get_energysourceload {fuller.Handle}")), Is.EqualTo(10f));

            Assert.That(interp.Eval($"get_energy {consumer.Handle}"), Is.EqualTo("1"));
            Assert.That(fuller.EnergyStore, Is.EqualTo(20f), "an existing lease is not charged twice");

            interp.Eval($"release_energy {consumer.Handle}");
            Assert.That(Number(interp.Eval($"get_energysourceload {fuller.Handle}")), Is.Zero);
            Assert.That(fuller.EnergyStore, Is.EqualTo(20f), "release never refunds consumed energy");
        }

        [Test]
        public void Lease_ExpiresAfterSixThousandLogicTicks()
        {
            var (_, context, _) = Host();
            var source = Spawn(context, "TestSource", "energy", 0, Vector3.zero);
            var consumer = Spawn(context, "TestConsumer", "production", 0, new Vector3(1f, 0f, 0f));
            ConfigureSource(source, 1, 20f, 20f);
            consumer.EnergyClass = 1;
            consumer.EnergyConsumption = 10f;
            Assert.That(context.Energy.Acquire(consumer), Is.True);

            for (int i = 0; i < 5999; i++) context.Energy.Tick();
            Assert.That(context.Energy.GetSourceLoad(source), Is.EqualTo(10f));
            context.Energy.Tick();
            Assert.That(context.Energy.GetSourceLoad(source), Is.Zero);
        }

        [Test]
        public void SaveState_RestoresSourceOrderConfigurationAndLeaseCountdown()
        {
            var (_, context, _) = Host();
            var first = Spawn(context, "FirstSource", "energy", 0, Vector3.zero);
            var second = Spawn(context, "SecondSource", "energy", 0, Vector3.right);
            var consumer = Spawn(context, "Consumer", "production", 0, Vector3.zero);
            ConfigureSource(first, 1, 10f, 20f);
            ConfigureSource(second, 2, 20f, 30f);

            context.Energy.Restore(new EnergySystem.SaveState
            {
                Sources = new[]
                {
                    new EnergySystem.SourceSaveState
                    {
                        SourceHandle = second.Handle,
                        EnergyClass = 4,
                        Range = 44.5f,
                        Leases = new[]
                        {
                            new EnergySystem.LeaseSaveState
                            {
                                ConsumerHandle = consumer.Handle,
                                Amount = 7.25f,
                                TicksLeft = 2,
                            },
                        },
                    },
                    new EnergySystem.SourceSaveState
                    {
                        SourceHandle = first.Handle,
                        EnergyClass = 3,
                        Range = 33.5f,
                    },
                },
            });

            EnergySystem.SaveState restored = context.Energy.Capture();
            Assert.That(restored.Sources, Has.Length.EqualTo(2));
            Assert.That(restored.Sources[0].SourceHandle, Is.EqualTo(second.Handle));
            Assert.That(restored.Sources[1].SourceHandle, Is.EqualTo(first.Handle));
            Assert.That(second.EnergyClass, Is.EqualTo(4));
            Assert.That(second.EnergyRange, Is.EqualTo(44.5f));
            Assert.That(restored.Sources[0].Leases, Has.Length.EqualTo(1));
            Assert.That(restored.Sources[0].Leases[0].ConsumerHandle,
                Is.EqualTo(consumer.Handle));
            Assert.That(restored.Sources[0].Leases[0].Amount, Is.EqualTo(7.25f));
            Assert.That(restored.Sources[0].Leases[0].TicksLeft, Is.EqualTo(2));

            context.Energy.Tick();
            Assert.That(context.Energy.GetSourceLoad(second), Is.EqualTo(7.25f));
            context.Energy.Tick();
            Assert.That(context.Energy.GetSourceLoad(second), Is.Zero);
        }

        [Test]
        public void Dispatcher_BlocksUnpoweredWorkAndAcquiresOnAssignment()
        {
            var (_, context, _) = Host();
            var gnome = Spawn(context, "TestGnome", "gnome", 0, Vector3.zero);
            gnome.WorkStart = 0f;
            gnome.WorkLen = 24f;
            var workshop = Spawn(context, "TestWorkshop", "production", 0, new Vector3(4f, 0f, 0f));
            workshop.BuildupComplete = true;
            workshop.EnergyClass = 1;
            workshop.EnergyConsumption = 10f;
            workshop.ProdSlots["Widget"] = 1;
            workshop.VarsScope.Vars["_materialneed"] = "0";
            context.OwnerAttribs[0] = new Dictionary<string, double> { ["BpWidget"] = 1.0 };

            var dispatcher = context.ProdDispatcher!;
            dispatcher.OnGnomeIdle(gnome);
            dispatcher.SweepNow(0);
            Assert.That(gnome.PendingEvents, Is.Empty);

            var source = Spawn(context, "TestSource", "energy", 0, new Vector3(2f, 0f, 0f));
            ConfigureSource(source, 1, 20f, 20f);
            dispatcher.SweepNow(0);

            Assert.That(gnome.PendingEvents, Has.Count.EqualTo(1));
            Assert.That(gnome.PendingEvents.Peek().Name, Is.EqualTo("evt_autoprod_workat"));
            Assert.That(source.EnergyStore, Is.EqualTo(10f));
            Assert.That(context.Energy.GetSourceLoad(source), Is.EqualTo(10f));

            dispatcher.ReleaseGnome(gnome);
            Assert.That(context.Energy.GetSourceLoad(source), Is.Zero);
        }
    }
}
