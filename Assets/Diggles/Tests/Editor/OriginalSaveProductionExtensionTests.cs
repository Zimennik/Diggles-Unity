#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveProductionExtensionTests
    {
        [Test]
        public void OrderCountsAndProgressRestore()
        {
            var requiredProgressPairs = new HashSet<(int Current, int Maximum)>
            {
                (4, 8),
                (5, 25),
                (-1, -1),
            };
            var observedProgressPairs = new HashSet<(int Current, int Maximum)>();
            (string Path, int ObjectId) fixture = FindProductionFixture(
                (body, production) =>
                {
                    var progress = (production.ProgressCurrent, production.ProgressMaximum);
                    if (requiredProgressPairs.Contains(progress)) observedProgressPairs.Add(progress);
                    return requiredProgressPairs.Contains(progress) &&
                        production.Slots.Any(slot => slot.OrderCount > 0);
                });

            foreach (string path in FindCorpusPaths())
            {
                OriginalSaveObjectCollection objects = ReadDocument(path).Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectProductionExtensionDocument? production =
                        objects[index].ProductionExtension;
                    if (production == null) continue;
                    var progress = (production.ProgressCurrent, production.ProgressMaximum);
                    if (requiredProgressPairs.Contains(progress)) observedProgressPairs.Add(progress);
                }
                if (observedProgressPairs.SetEquals(requiredProgressPairs)) break;
            }

            Assert.That(observedProgressPairs.SetEquals(requiredProgressPairs), Is.True,
                "the corpus did not expose all required production progress states");

            WithStagedFixture(fixture.Path, (snapshot, staged, _) =>
            {
                OriginalSaveRuntimeEntitySnapshot saved = snapshot.EntitiesById[fixture.ObjectId];
                OriginalSaveRuntimeEntityProductionSnapshot production = saved.Production ??
                    throw new AssertionException("The selected producer lost its CProd snapshot.");
                DigglesTclEntity entity = staged.Entities[fixture.ObjectId];

                foreach (OriginalSaveRuntimeEntityProductionSlotSnapshot slot in production.Slots)
                {
                    Assert.That(entity.ProdSlots.TryGetValue(slot.ClassName, out double count),
                        Is.True, $"producer {fixture.ObjectId}, slot {slot.ClassName}");
                    Assert.That(count, Is.EqualTo((double)slot.OrderCount),
                        $"producer {fixture.ObjectId}, slot {slot.ClassName}");
                }
                Assert.That(entity.ProdProgressCur, Is.EqualTo(production.ProgressCurrent));
                Assert.That(entity.ProdProgressMax, Is.EqualTo(production.ProgressMaximum));
            });
        }

        [Test]
        public void EnergyFieldsMatchTechtreetunes()
        {
            string path = FixturePath("save_test.sav");
            WithStagedFixture(path, (_, staged, _) =>
            {
                Assert.That(
                    staged.Entities.Values.Any(entity =>
                        entity.Class.Name == "Laufrad" &&
                        entity.EnergyStore == 370f &&
                        entity.EnergyMaxStore == 400f),
                    Is.True,
                    "save_test.sav has no Laufrad restored at store 370/max 400");
                Assert.That(
                    staged.Entities.Values.Any(entity =>
                        entity.Class.Name == "Dampfmaschine" &&
                        entity.EnergyStore == 1920f &&
                        entity.EnergyMaxStore == 2000f),
                    Is.True,
                    "save_test.sav has no Dampfmaschine restored at store 1920/max 2000");
            });
        }

        [Test]
        public void LateGameRecipeKnowledgeAndFarmSwitchesRestoreFromNativeSlots()
        {
            string path = FixturePath("save_test.sav");
            OriginalSaveArchiveDocument document = ReadDocument(path);
            OriginalSaveObjectCollection objects = document.Objects ??
                throw new AssertionException("save_test.sav has no typed object collection.");
            OriginalSaveObjectDocument kitchenBody = objects.Single(body => body.ObjectId == 6468);
            OriginalSaveObjectDocument sawmillBody = objects.Single(body => body.ObjectId == 155);
            OriginalSaveObjectDocument farmBody = objects.Single(body => body.ObjectId == 1753);
            OriginalSaveObjectProductionExtensionDocument kitchenProduction =
                kitchenBody.ProductionExtension ??
                throw new AssertionException("Industrial kitchen 6468 has no CProd extension.");
            OriginalSaveObjectProductionExtensionDocument sawmillProduction =
                sawmillBody.ProductionExtension ??
                throw new AssertionException("Sawmill 155 has no CProd extension.");
            OriginalSaveObjectProductionExtensionDocument farmProduction =
                farmBody.ProductionExtension ??
                throw new AssertionException("Farm 1753 has no CProd extension.");

            Assert.That(kitchenBody.ClassName, Is.EqualTo("Industriekueche"));
            Assert.That(
                kitchenProduction.Slots.Select(slot => slot.RegistryValue2),
                Is.EqualTo(new[] { 174, 176, 178, 180, 182 }));
            Assert.That(sawmillBody.ClassName, Is.EqualTo("Saegewerk"));
            Assert.That(
                sawmillProduction.Slots.Single(slot => slot.ClassName == "Schubkarren")
                    .RegistryValue2,
                Is.EqualTo(290));
            Assert.That(farmBody.ClassName, Is.EqualTo("Farm"));
            Assert.That(farmProduction.SwitchMode, Is.EqualTo(1));
            Assert.That(farmProduction.ExclusiveMode, Is.EqualTo(1));
            Assert.That(
                farmProduction.Slots.Select(slot => (slot.ClassName, slot.OrderCount)),
                Is.EqualTo(new[]
                {
                    ("Hamster", 10),
                    ("Pilz", 0),
                    ("Raupe", 0),
                }));

            WithStagedFixture(path, (snapshot, staged, context) =>
            {
                DigglesTclEntity kitchen = staged.Entities[6468];
                DigglesTclEntity sawmill = staged.Entities[155];
                DigglesTclEntity farm = staged.Entities[1753];
                OriginalSaveRuntimeOwnerAttributeBlockSnapshot ownerAttributes =
                    snapshot.OwnerAttributes.Blocks.Single(block => block.Owner == 0);

                foreach (OriginalSaveRuntimeEntityProductionSlotSnapshot slot in
                    snapshot.EntitiesById[6468].Production!.Slots)
                {
                    Assert.That(
                        BitConverter.Int32BitsToSingle(
                            ownerAttributes.PrimaryValueBits[slot.RegistryValue2]),
                        Is.EqualTo(1f),
                        $"industrial-kitchen recipe {slot.ClassName}");
                    Assert.That(
                        context.Interp.Eval(
                            $"get_prod_slot_invented {kitchen.Handle} {slot.ClassName}"),
                        Is.EqualTo("1"),
                        $"industrial-kitchen recipe {slot.ClassName}");
                }

                Assert.That(
                    context.Interp.Eval(
                        $"get_prod_slot_invented {sawmill.Handle} Schubkarren"),
                    Is.EqualTo("1"));
                Assert.That(
                    context.Interp.Eval(
                        $"get_prod_slot_invented {sawmill.Handle} Kettensaege"),
                    Is.EqualTo("0"));
                Assert.That(farm.ProdSlots.Keys,
                    Is.EquivalentTo(new[] { "Hamster", "Pilz", "Raupe" }));
                Assert.That(farm.ProdSlots["Hamster"], Is.EqualTo(10d));
                Assert.That(farm.ProdSlots["Pilz"], Is.Zero);
                Assert.That(farm.ProdSlots["Raupe"], Is.Zero);
                Assert.That(
                    context.Interp.Eval($"get_prod_switchmode {farm.Handle}"),
                    Is.EqualTo("1"));
                Assert.That(
                    context.Interp.Eval($"get_prod_exclusivemode {farm.Handle}"),
                    Is.EqualTo("1"));
            });
        }

        [Test]
        public void GuestSeatsRestoreSixteenWithLinkIds()
        {
            (string Path, int ObjectId) fixture = FindProductionFixture(
                (body, production) =>
                    body.ClassName == "Bar" &&
                    production.GuestSeats.Any(seat => seat.SeatLinkId >= 0));

            WithStagedFixture(fixture.Path, (snapshot, staged, _) =>
            {
                OriginalSaveRuntimeEntityProductionSnapshot production =
                    snapshot.EntitiesById[fixture.ObjectId].Production ??
                    throw new AssertionException("The selected service building lost its CProd snapshot.");
                DigglesTclEntity entity = staged.Entities[fixture.ObjectId];
                int[] links = entity.GuestLinks ??
                    throw new AssertionException("Guest seat links were not restored.");
                int[] guests = entity.Guests ??
                    throw new AssertionException("Guest seat occupants were not restored.");
                double[] orders = entity.GuestOrders ??
                    throw new AssertionException("Guest seat orders were not restored.");

                Assert.That(links, Has.Length.EqualTo(DigglesTclEntity.GuestSeatCount));
                Assert.That(guests, Has.Length.EqualTo(DigglesTclEntity.GuestSeatCount));
                Assert.That(orders, Has.Length.EqualTo(DigglesTclEntity.GuestSeatCount));
                for (int index = 0; index < DigglesTclEntity.GuestSeatCount; index++)
                {
                    OriginalSaveRuntimeEntityProductionGuestSeatSnapshot seat =
                        production.GuestSeats[index];
                    int expectedGuest = seat.GuestObjectId > 0 &&
                        staged.Entities.ContainsKey(seat.GuestObjectId)
                            ? staged.Entities[seat.GuestObjectId].Handle
                            : 0;
                    Assert.That(links[index], Is.EqualTo(seat.SeatLinkId), $"seat {index} link");
                    Assert.That(guests[index], Is.EqualTo(expectedGuest), $"seat {index} guest");
                    Assert.That(orders[index], Is.EqualTo(seat.OrderCount), $"seat {index} orders");
                }
            });
        }

        [Test]
        public void ResidueByteNotBooleanValidated()
        {
            (string Path, int ObjectId) fixture = FindProductionFixture(
                (_, production) => production.RequestBytes[5] == 0xDF);

            WithStagedFixture(fixture.Path, (snapshot, staged, _) =>
            {
                OriginalSaveRuntimeEntityProductionSnapshot production =
                    snapshot.EntitiesById[fixture.ObjectId].Production ??
                    throw new AssertionException("The residue fixture lost its CProd snapshot.");
                Assert.That(production.PlacementVariantByte, Is.EqualTo(0xDF));
                Assert.That(staged.Entities.ContainsKey(fixture.ObjectId), Is.True);
            });
        }

        private static (string Path, int ObjectId) FindProductionFixture(
            Func<
                OriginalSaveObjectDocument,
                OriginalSaveObjectProductionExtensionDocument,
                bool> predicate)
        {
            foreach (string path in FindCorpusPaths())
            {
                OriginalSaveObjectCollection objects = ReadDocument(path).Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    if (body.ProductionExtension is OriginalSaveObjectProductionExtensionDocument
                        production && predicate(body, production))
                    {
                        return (path, body.ObjectId);
                    }
                }
            }
            throw new AssertionException("No matching CProd fixture was found in the save corpus.");
        }

        private static string[] FindCorpusPaths()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths.Length, Is.GreaterThanOrEqualTo(98));
            return paths
                .OrderBy(path => FixturePriority(Path.GetFileName(path)))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        private static int FixturePriority(string fileName) => fileName switch
        {
            "save_test.sav" => 0,
            "save_tutorial.sav" => 1,
            "save_tutorial2.sav" => 2,
            "save_tutorial3.sav" => 3,
            "save_tutorial4.sav" => 4,
            _ => 5,
        };

        private static string FixturePath(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return path;
        }

        private static OriginalSaveArchiveDocument ReadDocument(string path) =>
            new OriginalSaveArchiveCodec().Read(path);

        private static void WithStagedFixture(
            string path,
            Action<
                OriginalSaveRuntimeSnapshot,
                OriginalSaveRuntimeSessionState,
                DigglesTclContext> assertions)
        {
            OriginalSaveArchiveDocument document = ReadDocument(path);
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            var stableRoot = new GameObject("OriginalSaveProductionExtensionTest");
            var initialRoot = new GameObject("WorldContent.Initial");
            initialRoot.transform.SetParent(stableRoot.transform, false);
            RuntimeGameSession? initial = null;
            RuntimeGameSession? candidate = null;
            try
            {
                var engineFactory = new DigglesEngineFactory();
                initial = RuntimeGameSession.CreateInitial(
                    engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                var host = new RuntimeWorldSessionHost(
                    engineFactory,
                    new ActiveGameSession(initial),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                candidate = host.CreateCandidate(OriginalDataTestFixture.DataRoot, "BlockDB2.BDB");
                candidate.Engine.LoadRuntimeDefinitions();
                candidate.Engine.Ctx.EntitySpawner = new RecordingSpawner();
                bool objectInitializationObserved = false;
                candidate.Engine.Interp.CommandTrace = (command, _, __) =>
                {
                    if (command == "obj_init") objectInitializationObserved = true;
                };

                var actionFactory = new OriginalSaveActionFactory(new RuntimeSaveableActionFactory());
                var sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    new OriginalSaveActionInstaller());
                OriginalSaveRuntimeSessionState staged = sessionStager.StageCandidate(
                    candidate,
                    snapshot,
                    "BlockDB2.BDB");

                Assert.That(objectInitializationObserved, Is.False,
                    "entity reconstruction must not evaluate obj_init Tcl");
                assertions(snapshot, staged, candidate.Engine.Ctx);
            }
            finally
            {
                candidate?.Dispose();
                initial?.Dispose();
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }

        private sealed class RecordingSpawner : IDigglesEntitySpawner
        {
            public GameObject? Spawn(DigglesTclEntity ent, Transform parent) => null;
            public void Destroy(GameObject go) => UnityEngine.Object.DestroyImmediate(go);
            public void ApplyPosition(GameObject go, Vector3 nativePos) { }
            public void ApplyRotation(GameObject go, Vector3 nativeRotRadians) { }
            public void ApplyAnim(
                GameObject go,
                string animName,
                int startFrame,
                DigglesAnimator.AnimMode mode) { }
        }
    }
}
