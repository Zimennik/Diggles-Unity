#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveStructureBuilderTests
    {
        private const string BdbRelativePath = "BlockDB2.BDB";

        [Test]
        public void DeclarationsAscendingWithTerminators()
        {
            OriginalSaveArchiveDocument source = ReadFixture("save_tutorial.sav");
            OriginalSaveObjectManifest manifest = source.Metadata.Objects ??
                throw new AssertionException("The tutorial fixture has no object manifest.");
            byte[] rebuilt = new OriginalSaveStructureBuilder().BuildDeclarations(manifest);
            byte[] expected = Slice(
                source.CopyPayload(),
                manifest.DeclarationsOffset,
                manifest.SupportSectionOffset);

            int previousObjectId = 1;
            foreach (OriginalSaveObjectDeclaration declaration in manifest.Declarations)
            {
                Assert.That(declaration.ObjectId, Is.GreaterThan(previousObjectId));
                previousObjectId = declaration.ObjectId;
            }
            int terminatorOffset = checked((int)(
                manifest.DeclarationTerminatorOffset - manifest.DeclarationsOffset));
            Assert.That(BitConverter.ToInt32(rebuilt, terminatorOffset), Is.EqualTo(-1));
            CollectionAssert.AreEqual(expected, rebuilt);
        }

        [Test]
        public void ObjectBodyTailReproducesQuirks()
        {
            (OriginalSaveArchiveDocument Document, OriginalSaveObjectDocument Body) fixture =
                FindProductionResidueFixture();
            OriginalSaveObjectProductionExtensionDocument production =
                fixture.Body.ProductionExtension ??
                throw new AssertionException("The selected object has no CProd extension.");
            byte[] rebuilt = new OriginalSaveStructureBuilder().BuildObjectBody(fixture.Body);
            byte[] expected = BodyBytes(fixture.Document, fixture.Body);

            Assert.That(production.RequestBytes, Has.Count.EqualTo(7));
            Assert.That(production.RequestBytes[6], Is.EqualTo(production.RequestBytes[0]));
            CollectionAssert.Contains(new byte[] { 0xDF, 0x73 }, production.RequestBytes[5]);
            Assert.That(production.GuestSeats, Has.Count.EqualTo(16));
            CollectionAssert.AreEqual(expected, rebuilt);
        }

        [Test]
        public void UnchangedEntityBodiesAreByteIdentical()
        {
            OriginalSaveArchiveDocument source = ReadFixture("save_tutorial.sav");
            OriginalSaveObjectCollection objects = source.Objects ??
                throw new AssertionException("The tutorial fixture has no typed object bodies.");
            var builder = new OriginalSaveStructureBuilder();

            for (int index = 0; index < objects.Count; index++)
            {
                OriginalSaveObjectDocument body = objects[index];
                CollectionAssert.AreEqual(
                    BodyBytes(source, body),
                    builder.BuildObjectBody(body),
                    $"Object {body.ObjectId} ({body.ClassName}) changed during a no-op rebuild.");
            }
        }

        [Test]
        public void ActionTableSentinelsExact()
        {
            OriginalSaveArchiveDocument source = ReadFixture("save_tutorial.sav");
            OriginalSaveLogicMetadata logic = source.Metadata.Logic ??
                throw new AssertionException("The tutorial fixture has no logic metadata.");
            OriginalSaveActionTableDocument actions = source.Actions ??
                throw new AssertionException("The tutorial fixture has no typed action table.");
            byte[] rebuilt = new OriginalSaveStructureBuilder().BuildActionTable(actions);
            byte[] expected = Slice(
                source.CopyPayload(),
                logic.ActionSectionOffset,
                logic.Marker13Offset);

            int firstSentinelOffset = sizeof(int);
            for (int index = 0; index < actions.Records.Count; index++)
            {
                firstSentinelOffset = checked(
                    firstSentinelOffset + sizeof(int) * 2 + actions.Records[index].PayloadLength);
            }
            Assert.That(
                BitConverter.ToInt32(rebuilt, firstSentinelOffset),
                Is.EqualTo(OriginalSaveStructureBuilder.ActionTableSentinel));
            Assert.That(
                BitConverter.ToInt32(rebuilt, rebuilt.Length - sizeof(int)),
                Is.EqualTo(OriginalSaveStructureBuilder.ActionAuxiliaryTableSentinel));
            CollectionAssert.AreEqual(expected, rebuilt);
        }

        [Test]
        public void EmptyClassTableAndSpatialAreWellFormed()
        {
            using var harness = new SessionHarness();
            OriginalSaveArchiveDocument source = ReadFixture("save_tutorial.sav");
            OriginalSaveRuntimeSnapshot baseline = Stage(source);
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, baseline);
                DigglesTclContext context = candidate.Engine.Ctx;
                int addedObjectId = NextObjectId(context);
                AddPlacedEntity(context, addedObjectId, "D2b_empty_structures");

                OriginalSaveArchiveDocument rebuilt = Rebuild(source, baseline, context);
                OriginalSaveObjectManifest manifest = rebuilt.Metadata.Objects ??
                    throw new AssertionException("The rebuilt tutorial has no object manifest.");
                var builder = new OriginalSaveStructureBuilder();
                OriginalSaveRetainedStructureSections structures =
                    builder.CaptureRetainedStructures(rebuilt);

                Assert.That(manifest.ClassTableEntryCount, Is.Zero);
                // The class table self-heals from live objects, so a graph-changed save emits
                // it empty — but the spatial index does not (dormant seeds have no live
                // counterpart), so the writer re-emits the surviving seeds. With none carved,
                // that reproduces the source section byte-for-byte.
                OriginalSaveObjectManifest sourceManifest = source.Metadata.Objects ??
                    throw new AssertionException("The tutorial fixture has no object manifest.");
                Assert.That(
                    manifest.SpatialRecordCount,
                    Is.EqualTo(sourceManifest.SpatialRecordCount));
                Assert.That(
                    manifest.SpatialBucketCount,
                    Is.EqualTo(OriginalSaveStructureBuilder.CalculateSpatialBucketCount(
                        rebuilt.World.Width,
                        rebuilt.World.Height)));
                CollectionAssert.AreEqual(builder.BuildEmptyClassTable(), structures.ClassTable);
                CollectionAssert.AreEqual(new byte[sizeof(int)], structures.CleanupQueue);
                CollectionAssert.AreEqual(
                    builder.CaptureRetainedStructures(source).SpatialIndex,
                    structures.SpatialIndex);
                AssertEmptyClassTable(structures.ClassTable);

                OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(rebuilt);
                OriginalSaveRuntimeSnapshot restaged = new OriginalSaveRuntimeStager().Stage(plan);
                Assert.That(restaged.EntitiesById.ContainsKey(addedObjectId), Is.True);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void RetainedStructuresAreByteIdenticalOnNoOp()
        {
            using var harness = new SessionHarness();
            OriginalSaveArchiveDocument source = ReadFixture("save_tutorial.sav");
            OriginalSaveRuntimeSnapshot baseline = Stage(source);
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, baseline);
                OriginalSaveArchiveDocument rebuilt = Rebuild(
                    source,
                    baseline,
                    candidate.Engine.Ctx);
                var builder = new OriginalSaveStructureBuilder();
                OriginalSaveRetainedStructureSections expected =
                    builder.CaptureRetainedStructures(source);
                OriginalSaveRetainedStructureSections actual =
                    builder.CaptureRetainedStructures(rebuilt);

                CollectionAssert.AreEqual(expected.ClassTable, actual.ClassTable);
                CollectionAssert.AreEqual(expected.CleanupQueue, actual.CleanupQueue);
                CollectionAssert.AreEqual(expected.SpatialIndex, actual.SpatialIndex);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        // The graph-changed writer path re-emits the marker-7 spatial index from the surviving
        // seeds (an empty index would lose the dormant deco for good — the native never
        // regenerates dropped records). This locks the whole law at once: feeding every archived
        // record back through the seed writer must reproduce the retained section byte-for-byte
        // (bucket assignment, per-bucket order, and the FUN_005c6cf0 record transfer).
        [Test]
        public void SpatialIndexRebuildFromSeedsMatchesRetainedBytes()
        {
            OriginalSaveArchiveDocument source = ReadFixture("save_test.sav");
            OriginalSaveObjectManifest manifest = source.Metadata.Objects ??
                throw new AssertionException("save_test has no object manifest.");
            Assume.That(
                manifest.SpatialRecordCount,
                Is.GreaterThan(0),
                "save_test carries no spatial records; the rebuild law needs a populated fixture.");
            OriginalSaveSpatialSeedDocument seeds = source.SpatialSeeds ??
                throw new AssertionException("save_test decoded no spatial seed document.");

            var builder = new OriginalSaveStructureBuilder();
            byte[] expected = builder.CaptureRetainedStructures(source).SpatialIndex;
            byte[] rebuilt = builder.BuildSpatialIndexFromSeeds(
                seeds.Records,
                source.World.Width,
                source.World.Height);

            Assert.That(seeds.Records.Count, Is.EqualTo(manifest.SpatialRecordCount));
            CollectionAssert.AreEqual(expected, rebuilt);
        }

        [Test]
        public void BeginningSaveWarmClassTableIsRetainedByteIdentical()
        {
            using var harness = new SessionHarness();
            string path = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "..",
                "..",
                "savework",
                "save_begining_save.sav"));
            Assume.That(File.Exists(path), $"Beginning-save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            OriginalSaveArchiveDocument source = new OriginalSaveArchiveCodec(reader).Read(path);
            OriginalSaveObjectManifest manifest = source.Metadata.Objects ??
                throw new AssertionException("The beginning save has no object manifest.");
            OriginalSaveRuntimeSnapshot baseline = Stage(source);
            RuntimeGameSession candidate = harness.CreateCandidate();
            var builder = new OriginalSaveStructureBuilder();
            byte[] expected = builder.CaptureRetainedStructures(source).ClassTable;
            try
            {
                harness.StageForCapture(candidate, baseline);
                OriginalSaveArchiveDocument rebuilt = Rebuild(
                    source,
                    baseline,
                    candidate.Engine.Ctx);
                byte[] actual = builder.CaptureRetainedStructures(rebuilt).ClassTable;

                Assert.That(manifest.ClassTableEntryCount, Is.EqualTo(75));
                CollectionAssert.AreEqual(expected, actual);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void AddedAndRemovedObjectsRoundTrip()
        {
            using var harness = new SessionHarness();
            OriginalSaveArchiveDocument source = ReadFixture("save_tutorial.sav");
            OriginalSaveRuntimeSnapshot baseline = Stage(source);
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, baseline);
                DigglesTclContext context = candidate.Engine.Ctx;
                DigglesTclEntity? removed = FindRemovableEntity(baseline, context);
                Assume.That(removed, Is.Not.Null,
                    "The tutorial fixture has no isolated type-0 entity suitable for deletion.");
                int removedObjectId = removed!.Handle;
                int addedObjectId = NextObjectId(context);
                DigglesTclEntity added = AddPlacedEntity(
                    context,
                    addedObjectId,
                    "D2b_added_object");
                context.Particles!.Configure(
                    addedObjectId,
                    0,
                    13,
                    new Vector3(0f, -0.4f, 0.1f),
                    new Vector3(0f, -0.07f, 0.01f),
                    300,
                    50,
                    false,
                    -1,
                    -1);
                var presentation = new GameObject("W7a.FreshTextureAnimation");
                presentation.transform.SetParent(candidate.ContentRoot.transform, false);
                presentation.AddComponent<MeshRenderer>();
                DigglesTextureAnimator textureAnimator =
                    presentation.AddComponent<DigglesTextureAnimator>();
                added.GameObject = presentation;
                int[] eyeFrames =
                {
                    0, 0, 0, 9, 0, 0, 0, 0, 0, 0, 0,
                    9, 0, 0, 0, 0, 0, 0, 0, 9, 0,
                };
                textureAnimator.SetTextureAnimation(
                    0,
                    new Texture2D?[1],
                    new[] { 1 },
                    0,
                    2);
                textureAnimator.SetTextureAnimation(
                    4,
                    new Texture2D?[eyeFrames.Length],
                    eyeFrames,
                    19,
                    1);
                context.Interp.Eval($"set_anim {addedObjectId} mann.standard 0 2");
                context.Interp.Eval(
                    $"del {removedObjectId.ToString(CultureInfo.InvariantCulture)}");
                int[] expectedObjectIds = context.Entities.Keys.OrderBy(value => value).ToArray();

                OriginalSaveArchiveDocument rebuilt = Rebuild(source, baseline, context);
                OriginalSaveObjectManifest manifest = rebuilt.Metadata.Objects ??
                    throw new AssertionException("The rebuilt tutorial has no object manifest.");
                OriginalSaveObjectCollection objects = rebuilt.Objects ??
                    throw new AssertionException("The rebuilt tutorial has no typed objects.");
                OriginalSaveObjectDocument addedBody = objects.FindById(addedObjectId) ??
                    throw new AssertionException("The added object has no rebuilt body.");

                Assert.That(objects.Select(value => value.ObjectId), Is.EqualTo(expectedObjectIds));
                Assert.That(objects.FindById(removedObjectId), Is.Null);
                AssertAddedCoreState(added, addedBody, eyeFrames);
                OriginalSaveActionTableDocument actionTable = rebuilt.Actions ??
                    throw new AssertionException("The rebuilt tutorial has no action table.");
                OriginalSaveActionRecord addedAction = actionTable.Records.Single(record =>
                    record.OwnerObjectId == addedObjectId);
                Assert.That(addedAction.Type, Is.EqualTo(OriginalSaveActionType.Null),
                    "a fresh idle N10 owner requires a marker-12 type-0 row");

                byte[] payload = rebuilt.CopyPayload();
                int managerCursorOffset = checked((int)(
                    manifest.OwnerAttributeTables.Offset + 9L * 2L * 512L * sizeof(int)));
                int maximumUsedSlot = expectedObjectIds[^1];
                Assert.That(BitConverter.ToInt32(payload, managerCursorOffset), Is.EqualTo(-1));
                // The manager tail stores the maximum used object id verbatim (byte-verified
                // against native saves), not a next-id (+1).
                Assert.That(
                    BitConverter.ToInt32(payload, managerCursorOffset + sizeof(int)),
                    Is.EqualTo(maximumUsedSlot));
                AssertEmptyLodGroups(payload, managerCursorOffset + sizeof(int) * 2);

                OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(rebuilt);
                OriginalSaveRuntimeSnapshot restaged = new OriginalSaveRuntimeStager().Stage(plan);
                Assert.That(
                    restaged.Entities.Select(value => value.ObjectId),
                    Is.EqualTo(expectedObjectIds));
                Assert.That(restaged.EntitiesById.ContainsKey(removedObjectId), Is.False);
                OriginalSaveRuntimeEntitySnapshot restagedAdded =
                    restaged.EntitiesById[addedObjectId];
                Assert.That(restagedAdded.ClassName, Is.EqualTo(added.Class.Name));
                Assert.That(restagedAdded.ObjectType, Is.EqualTo(added.NativeObjectType));
                Assert.That(restagedAdded.InstanceName, Is.EqualTo(added.InstanceName));
                Assert.That(restagedAdded.HasTclState, Is.True,
                    "an owning entity with zero variables must retain N10 presence");
                AssertRawSingle(added.Position.x, restagedAdded.Position.X);
                AssertRawSingle(added.Position.y, restagedAdded.Position.Y);
                AssertRawSingle(added.Position.z, restagedAdded.Position.Z);
                AssertRawSingle(added.Velocity.x, restagedAdded.Velocity.X);
                AssertRawSingle(added.Velocity.y, restagedAdded.Velocity.Y);
                AssertRawSingle(added.Velocity.z, restagedAdded.Velocity.Z);
                AssertRawSingle(added.Rotation.x, restagedAdded.Rotation.X);
                AssertRawSingle(added.Rotation.y, restagedAdded.Rotation.Y);
                AssertRawSingle(added.Rotation.z, restagedAdded.Rotation.Z);
                Assert.That(restagedAdded.CurrentPartFrames,
                    Is.EqualTo(new[] { 1, 0, 0, 0, 9 }));
                Assert.That(restagedAdded.TextureAnimations, Has.Count.EqualTo(5));
                Assert.That(restagedAdded.TextureAnimations[4].FrameValues,
                    Is.EqualTo(eyeFrames));
                Assert.That(restagedAdded.TextureAnimations[4].Cursor, Is.EqualTo(19));
                Assert.That(restagedAdded.TextureAnimations[4].PlaybackMode, Is.EqualTo(1));
                Assert.That(restagedAdded.InitialAnimationModelDatabaseSlot, Is.Zero);
                Assert.That(restagedAdded.InitialAnimationState, Is.EqualTo(4));
                Assert.That(restagedAdded.InitialAnimationStartFrame, Is.Zero);
                Assert.That(restagedAdded.InitialAnimationMode, Is.EqualTo(2));
                Assert.That(restagedAdded.Owner, Is.EqualTo(added.Owner));
                AssertRawSingle(added.WorkStart, restagedAdded.WorkStart);
                AssertRawSingle(added.WorkLen, restagedAdded.WorkLength);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        private static (OriginalSaveArchiveDocument, OriginalSaveObjectDocument)
            FindProductionResidueFixture()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories)
                .OrderBy(path => FixturePriority(Path.GetFileName(path)))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assume.That(paths.Length, Is.GreaterThanOrEqualTo(98));

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = new OriginalSaveArchiveCodec().Read(path);
                OriginalSaveObjectCollection objects = document.Objects ??
                    throw new AssertionException($"Fixture '{path}' has no typed object bodies.");
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    if (body.ProductionExtension is OriginalSaveObjectProductionExtensionDocument
                        production && production.RequestBytes.Count == 7 &&
                        production.RequestBytes[0] == production.RequestBytes[6] &&
                        production.RequestBytes[5] is 0xDF or 0x73)
                    {
                        return (document, body);
                    }
                }
            }
            throw new AssertionException(
                "No fixture contains a CProd allocator-residue byte 0xDF or 0x73.");
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

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private static OriginalSaveRuntimeSnapshot Stage(OriginalSaveArchiveDocument document)
        {
            OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(document);
            return new OriginalSaveRuntimeStager().Stage(plan);
        }

        private static OriginalSaveArchiveDocument Rebuild(
            OriginalSaveArchiveDocument source,
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context)
        {
            OriginalSaveCurrentArchivePatch patch =
                new OriginalSaveRuntimePatchMapper().Capture(baseline, context);
            return new OriginalSaveCurrentArchiveWriter(new OriginalSaveArchiveReader())
                .Rebuild(source, patch);
        }

        private static int NextObjectId(DigglesTclContext context)
        {
            int maximumObjectId = context.Entities.Keys.Max();
            Assume.That(maximumObjectId, Is.LessThan(0x1FFF),
                "The tutorial fixture has no free native object ID.");
            return maximumObjectId + 1;
        }

        private static DigglesTclEntity AddPlacedEntity(
            DigglesTclContext context,
            int objectId,
            string instanceName)
        {
            DigglesTclEntity? template = context.Entities.Values.FirstOrDefault(entity =>
                entity.NativeObjectType == 0 && ClassOwnsTclState(entity.Class) &&
                context.Classes.TryGetValue(entity.Class.Name, out DigglesClass? registered) &&
                ReferenceEquals(registered, entity.Class));
            Assume.That(template, Is.Not.Null,
                "The tutorial fixture has no boot-registered type-0 class.");

            double phase = context.Sim!.GameTime / ProdDispatcher.WorktimeHourSeconds %
                ProdDispatcher.DayHours;
            var added = new DigglesTclEntity
            {
                Class = template!.Class,
                InstanceName = instanceName,
                NativeObjectType = 0,
                Position = new Vector3(123.25f, 45.5f, 6.75f),
                SpawnPosition = new Vector3(123.25f, 45.5f, 6.75f),
                HasSpawnPosition = true,
                Velocity = new Vector3(0.5f, -0.25f, 1.25f),
                Rotation = new Vector3(0.125f, 0.25f, 0.5f),
                Owner = 0,
                CreationTimeHours = (float)(context.Sim.GameTime /
                    ProdDispatcher.WorktimeHourSeconds),
                WorkStart = (float)((phase + ProdDispatcher.DayHours - 0.5) %
                    ProdDispatcher.DayHours),
                WorkLen = 1f,
                FowSizeX = 16,
                FowSizeY = 24,
                FowLastX = 120f,
                FowLastY = 40f,
                FowLastZ = 6.5f,
                LinkedSlot = -1,
                PlaceSnapMode = 0,
                WalkResult = 0,
                WeaponClassId = 0,
                ShieldClassId = 0,
                PickTargetMode = 0,
                ClimbAbility = true,
                ContainedIn = -1,
                InventoryCapacity = 99,
                DigSetId = -1,
            };
            added.TaskList.Add("play_anim sleeploop");
            added.TaskList.Add("play_anim sleepstop");
            added.FowCache = new[] { 255, 254, 253, 252, 251, 250, 249, 248 };
            added.FowCacheVersion = context.FogOfWar!.CacheVersion;
            context.RegisterEntityAtHandle(added, objectId);
            return added;
        }

        private static bool ClassOwnsTclState(DigglesClass runtimeClass) =>
            !runtimeClass.DisableScripting &&
            (!string.IsNullOrEmpty(runtimeClass.InitCode) ||
             !string.IsNullOrEmpty(runtimeClass.ExitCode) ||
             runtimeClass.MemberVars.Count != 0 ||
             runtimeClass.EventHandlers.Count != 0 ||
             runtimeClass.States.Count != 0 ||
             runtimeClass.Methods.Count != 0);

        private static DigglesTclEntity? FindRemovableEntity(
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context)
        {
            // Scripting-disabled deco is the safest removal candidate (no exit chains, no
            // behavior) — the old !DisableScripting guard starved the pick to Inconclusive
            // once the idiobj generators were correctly marked scripting-disabled.
            return context.Entities.Values
                .OrderBy(entity => entity.Handle)
                .FirstOrDefault(entity =>
                    entity.NativeObjectType == 0 &&
                    entity.Inventory.Count == 0 && entity.ContainedIn < 0 &&
                    entity.ActiveAction == null && entity.PendingEvents.Count == 0 &&
                    entity.LinkedToHandle <= 0 &&
                    !HasExitHandler(entity.Class, context, new HashSet<string>(StringComparer.Ordinal)) &&
                    !IsReferenced(entity.Handle, baseline, context));
        }

        private static bool HasExitHandler(
            DigglesClass objectClass,
            DigglesTclContext context,
            HashSet<string> visited)
        {
            if (!visited.Add(objectClass.Name)) return false;
            if (!string.IsNullOrEmpty(objectClass.ExitCode)) return true;
            for (int index = 0; index < objectClass.Inherits.Count; index++)
            {
                if (context.Classes.TryGetValue(
                        objectClass.Inherits[index],
                        out DigglesClass? baseClass) &&
                    HasExitHandler(baseClass, context, visited))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsReferenced(
            int objectId,
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context)
        {
            if (baseline.ReferrersByObjectId.ContainsKey(objectId) ||
                baseline.ActionsByOwnerId.ContainsKey(objectId) ||
                baseline.Timers.Records.Any(timer => timer.OwnerObjectId == objectId ||
                    timer.EventOwnerObjectId == objectId ||
                    timer.Subject1ObjectId == objectId ||
                    timer.Subject2ObjectId == objectId ||
                    timer.Subject3ObjectId == objectId) ||
                baseline.Triggers.Any(trigger => trigger != null &&
                    (trigger.OwnerObjectId == objectId || trigger.TargetObjectId == objectId)) ||
                baseline.ClassLights.Records.Any(value =>
                    value.IsObjectBound && value.SourceToken == objectId) ||
                baseline.Energy.Sources.Any(value => value.SourceObjectId == objectId ||
                    value.Leases.Any(lease => lease.ConsumerObjectId == objectId)) ||
                baseline.Sparetime.Registrations.Any(value => value.ObjectId == objectId) ||
                baseline.AdaptiveSoundMarkers.Markers.Any(value => value.ObjectId == objectId) ||
                baseline.Particles.Records.Any(value => value.LinkedObjectToken == objectId) ||
                baseline.ObjectIcons.Slots.Any(value => value.ObjectId == objectId) ||
                baseline.SelectionControlGroups.Groups.Any(group => group.Contains(objectId)) ||
                context.LoreTracks.CaptureRecords().Any(value => value.OwnerId == objectId))
            {
                return true;
            }

            foreach (DigglesTclEntity entity in context.Entities.Values)
            {
                if (entity.Handle == objectId) continue;
                if (entity.Inventory.Contains(objectId) || entity.LinkedToHandle == objectId)
                    return true;
            }
            foreach (OriginalSaveRuntimeEntitySnapshot entity in baseline.Entities)
            {
                if (entity.Family != null &&
                    (entity.Family.Partner.ReferenceObjectId == objectId ||
                     entity.Family.Mother.ReferenceObjectId == objectId ||
                     entity.Family.Father.ReferenceObjectId == objectId))
                {
                    return true;
                }
                if (entity.Planner != null &&
                    (entity.Planner.CurrentWorkplaceObjectId == objectId ||
                     entity.Planner.AssignedWorkplaceObjectId == objectId))
                {
                    return true;
                }
                if (entity.Production != null &&
                    (entity.Production.AssignedWorkerObjectId == objectId ||
                     entity.Production.GuestSeats.Any(value => value.GuestObjectId == objectId)))
                {
                    return true;
                }
                if (entity.EventQueueSlots.Any(value => value != null &&
                    EventReferences(value, objectId)) ||
                    entity.DelayedEvents.Any(value => EventReferences(value, objectId)))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool EventReferences(
            OriginalSaveRuntimeEntityEventSnapshot value,
            int objectId) =>
            value.OwnerObjectId == objectId || value.Subject1ObjectId == objectId ||
            value.Subject2ObjectId == objectId || value.Subject3ObjectId == objectId;

        private static bool EventReferences(
            OriginalSaveTimerEventPayloadDocument value,
            int objectId) =>
            value.OwnerObjectId == objectId || value.Subject1ObjectId == objectId ||
            value.Subject2ObjectId == objectId || value.Subject3ObjectId == objectId;

        private static void AssertAddedCoreState(
            DigglesTclEntity expected,
            OriginalSaveObjectDocument actual,
            IReadOnlyList<int> eyeFrames)
        {
            Assert.That(actual.ClassName, Is.EqualTo(expected.Class.Name));
            Assert.That(actual.ObjectType, Is.EqualTo(expected.NativeObjectType));
            Assert.That(actual.InstanceName, Is.EqualTo(expected.InstanceName));
            AssertRawSingle(expected.Position.x, actual.Position.X);
            AssertRawSingle(expected.Position.y, actual.Position.Y);
            AssertRawSingle(expected.Position.z, actual.Position.Z);
            // N04 is a loader-cleared transient: the canonical post-load value is [0,0,0]
            // (the writer emits zeros regardless of the live position).
            AssertRawSingle(0f, actual.TransientPosition.X);
            AssertRawSingle(0f, actual.TransientPosition.Y);
            AssertRawSingle(0f, actual.TransientPosition.Z);
            AssertRawSingle(expected.Velocity.x, actual.Velocity.X);
            AssertRawSingle(expected.Velocity.y, actual.Velocity.Y);
            AssertRawSingle(expected.Velocity.z, actual.Velocity.Z);
            AssertRawSingle(expected.Rotation.x, actual.Rotation.X);
            AssertRawSingle(expected.Rotation.y, actual.Rotation.Y);
            AssertRawSingle(expected.Rotation.z, actual.Rotation.Z);
            Assert.That(actual.CoreState, Is.EqualTo(new[]
            {
                0,
                BitConverter.SingleToInt32Bits(expected.Velocity.x),
                BitConverter.SingleToInt32Bits(expected.Velocity.y),
                BitConverter.SingleToInt32Bits(expected.Velocity.z),
                0,
                0,
                0,
                BitConverter.SingleToInt32Bits(expected.Rotation.x),
                BitConverter.SingleToInt32Bits(expected.Rotation.y),
                BitConverter.SingleToInt32Bits(expected.Rotation.z),
                BitConverter.SingleToInt32Bits(expected.Rotation.x),
                BitConverter.SingleToInt32Bits(expected.Rotation.y),
                BitConverter.SingleToInt32Bits(expected.Rotation.z),
            }), "fresh N06 core words");
            Assert.That(actual.Owner, Is.EqualTo(expected.Owner));
            AssertRawSingle(expected.CreationTimeHours, actual.CreationTimeHours);
            AssertRawSingle(expected.WorkStart, actual.WorkStart);
            AssertRawSingle(expected.WorkLen, actual.WorkLength);
            Assert.That(actual.Strings, Is.EqualTo(expected.TaskList), "fresh N08 task list");
            Assert.That(actual.StateAfterStrings.Take(8), Is.EqualTo(expected.FowCache),
                "fresh N09 warm cache lanes");
            Assert.That(actual.StateAfterStrings.Skip(8), Is.All.EqualTo(-1),
                "fresh N09 unmodeled cache lanes remain cold");
            Assert.That(actual.CommonState.FogOfWarSizeX, Is.EqualTo(expected.FowSizeX));
            Assert.That(actual.CommonState.FogOfWarSizeY, Is.EqualTo(expected.FowSizeY));
            AssertRawSingle(expected.FowLastZ, actual.CommonState.FogOfWarLastZ);
            Assert.That(actual.CommonState.State26C.Skip(5), Is.EqualTo(new[]
            {
                BitConverter.SingleToInt32Bits(0f),
                BitConverter.SingleToInt32Bits(-0.6f),
                BitConverter.SingleToInt32Bits(-2f),
                -1,
                BitConverter.SingleToInt32Bits(2f),
            }), "fresh N22 light defaults");
            Assert.That(actual.CommonState.ParticleState.IsPresent, Is.True, "fresh N24");
            OriginalSaveObjectParticleSourceRecord aura =
                actual.CommonState.ParticleState.Sources[0];
            Assert.That(aura.ParticleHandle, Is.GreaterThanOrEqualTo(0));
            AssertRawSingle(0f, aura.RelativePositionX.AsSingle);
            AssertRawSingle(-0.4f, aura.RelativePositionY.AsSingle);
            AssertRawSingle(0.1f, aura.RelativePositionZ.AsSingle);
            Assert.That(aura.RawLinkId, Is.EqualTo(-1));
            Assert.That(actual.CommonState.ParticleState.RawValues.Skip(1).Take(31),
                Is.All.EqualTo(-1));
            Assert.That(actual.CommonState.ParticleState.RawValues.Skip(128),
                Is.All.EqualTo(-1));
            Assert.That(actual.CommonState.State2B4, Is.Zero, "fresh N26 idle state");
            Assert.That(actual.CommonState.State35C, Is.EqualTo(1), "fresh N27 work latch");
            Assert.That(actual.CommonState.State35D, Is.EqualTo(1), "fresh N28 work latch");
            Assert.That(actual.PickTargetMode, Is.EqualTo(expected.PickTargetMode));
            Assert.That(actual.HasTclState, Is.True,
                "the selected scripted class owns an interpreter even with zero variables");
            // Echo law: every interp-owning object's dump starts with the Tcl 8.3.2 interpreter
            // builtins — a fresh object with no genuine writes carries exactly them.
            Assert.That(
                actual.TclVariables.Select(variable => (variable.Name, variable.Value)),
                Is.EqualTo(new[]
                {
                    ("env", ""),
                    ("tclDefaultLibrary", ""),
                    ("tcl_patchLevel", "8.3.2"),
                    ("tcl_platform", ""),
                    ("tcl_version", "8.3"),
                }));
            Assert.That(actual.CommonState.State39C,
                Is.EqualTo(new[] { -1, -1, -1, 0, 2 }),
                "fresh N34 mirrors the controller: no resolved clip on this modelless test " +
                "object, but the set_anim above did latch playback mode LOOP");
            Assert.That(actual.CommonState.RouteValues,
                Is.EqualTo(new[] { 1, 0, 0, 0, 9 }),
                "fresh N16 follows the current texture frame in each submesh channel");
            Assert.That(actual.CommonState.State3B4, Is.Zero,
                "fresh N35 stays clear because the port has no deferred render-job latch");
            Assert.That(actual.CommonState.State3BC,
                Is.EqualTo(new[] { 0, 4, 0, 2 }),
                "fresh N36 serializes the first-animation tuple");
            Assert.That(actual.AttachmentHistory.IsPresent, Is.True);
            Assert.That(actual.AttachmentHistory.Records, Has.Count.EqualTo(5));
            Assert.That(actual.AttachmentHistory.Records[0].RouteValues,
                Is.EqualTo(new[] { 1 }));
            Assert.That(actual.AttachmentHistory.Records[0].TrailingValue1, Is.Zero);
            Assert.That(actual.AttachmentHistory.Records[0].TrailingValue2, Is.EqualTo(2));
            for (int submesh = 1; submesh < 4; submesh++)
            {
                Assert.That(actual.AttachmentHistory.Records[submesh].RouteValues, Is.Empty);
                Assert.That(actual.AttachmentHistory.Records[submesh].TrailingValue1, Is.Zero);
                Assert.That(actual.AttachmentHistory.Records[submesh].TrailingValue2, Is.Zero);
            }
            Assert.That(actual.AttachmentHistory.Records[4].RouteValues,
                Is.EqualTo(eyeFrames));
            Assert.That(actual.AttachmentHistory.Records[4].TrailingValue1, Is.EqualTo(19));
            Assert.That(actual.AttachmentHistory.Records[4].TrailingValue2, Is.EqualTo(1));
            Assert.That(actual.CommonState.LateCoreState[1],
                Is.EqualTo(actual.CommonState.ObjectFlags),
                "N38 mirrors N37 object flags");
            Assert.That(actual.CommonState.DirtyFlags, Is.EqualTo(expected.NativeMutationFlags),
                "fresh N39 native mutation mask");
            Assert.That(actual.CommonState.LateCoreState[5], Is.EqualTo(-1),
                "N42 native late-core law");
        }

        private static void AssertRawSingle(float expected, float actual)
        {
            Assert.That(
                BitConverter.SingleToInt32Bits(actual),
                Is.EqualTo(BitConverter.SingleToInt32Bits(expected)));
        }

        private static void AssertEmptyClassTable(byte[] value)
        {
            const int capacity = 0x3000;
            int topOffset = sizeof(int) + capacity * sizeof(int);
            int byteOffset = topOffset + sizeof(int);
            int firstVectorOffset = byteOffset + sizeof(byte);
            int secondVectorOffset = firstVectorOffset + sizeof(int);
            int terminatorOffset = secondVectorOffset + sizeof(int);

            Assert.That(value.Length, Is.EqualTo(terminatorOffset + sizeof(int)));
            Assert.That(BitConverter.ToInt32(value, 0), Is.Zero);
            Assert.That(BitConverter.ToInt32(value, sizeof(int)), Is.EqualTo(capacity - 1));
            Assert.That(
                BitConverter.ToInt32(value, sizeof(int) + (capacity - 1) * sizeof(int)),
                Is.Zero);
            Assert.That(BitConverter.ToInt32(value, topOffset), Is.EqualTo(capacity - 1));
            Assert.That(value[byteOffset], Is.EqualTo(byte.MaxValue));
            Assert.That(BitConverter.ToInt32(value, firstVectorOffset), Is.Zero);
            Assert.That(BitConverter.ToInt32(value, secondVectorOffset), Is.Zero);
            Assert.That(BitConverter.ToInt32(value, terminatorOffset), Is.EqualTo(-1));
        }

        private static void AssertEmptyLodGroups(byte[] payload, int offset)
        {
            for (int group = 0; group < 3; group++)
            {
                Assert.That(BitConverter.ToInt32(payload, offset), Is.Zero);
                Assert.That(BitConverter.ToInt32(payload, offset + sizeof(int)), Is.Zero);
                Assert.That(BitConverter.ToInt32(payload, offset + sizeof(int) * 2), Is.Zero);
                Assert.That(payload[offset + sizeof(int) * 3], Is.Zero);
                Assert.That(payload[offset + sizeof(int) * 3 + sizeof(byte)], Is.EqualTo(1));
                offset += sizeof(int) * 3 + sizeof(byte) * 2;
            }
            Assert.That(BitConverter.ToInt32(payload, offset), Is.Zero);
        }

        private static byte[] BodyBytes(
            OriginalSaveArchiveDocument document,
            OriginalSaveObjectDocument body) =>
            Slice(document.CopyPayload(), body.Offset, body.Offset + body.Size);

        private static byte[] Slice(byte[] source, long start, long end)
        {
            int offset = checked((int)start);
            int length = checked((int)(end - start));
            var result = new byte[length];
            Buffer.BlockCopy(source, offset, result, 0, length);
            return result;
        }

        private sealed class SessionHarness : IDisposable
        {
            private readonly GameObject _stableRoot;
            private readonly IDigglesEngineFactory _engineFactory;
            private readonly RuntimeGameSession _initialSession;
            private readonly IRuntimeWorldSessionHost _sessionHost;
            private readonly IOriginalSaveRuntimeSessionStager _sessionStager;
            private readonly IOriginalSaveActionInstaller _actionInstaller;

            internal SessionHarness()
            {
                _stableRoot = new GameObject(nameof(OriginalSaveStructureBuilderTests));
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                _engineFactory = new DigglesEngineFactory();
                _initialSession = RuntimeGameSession.CreateInitial(
                    _engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        BdbRelativePath),
                    initialRoot);
                _sessionHost = new RuntimeWorldSessionHost(
                    _engineFactory,
                    new ActiveGameSession(_initialSession),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                var actionFactory = new OriginalSaveActionFactory(
                    new RuntimeSaveableActionFactory());
                _actionInstaller = new OriginalSaveActionInstaller();
                _sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    _actionInstaller);
            }

            internal RuntimeGameSession CreateCandidate() => _sessionHost.CreateCandidate(
                OriginalDataTestFixture.DataRoot,
                BdbRelativePath);

            internal OriginalSaveRuntimeSessionState StageForCapture(
                RuntimeGameSession candidate,
                OriginalSaveRuntimeSnapshot snapshot)
            {
                OriginalSaveRuntimeSessionState state =
                    _sessionStager.StageCandidate(candidate, snapshot, BdbRelativePath);
                // StageCandidate already installs prepared actions internally;
                // a second Install would trip the already-has-action guard.
                _actionInstaller.InstallPendingEntityEvents(
                    state.PendingEntityEvents,
                    state.Entities,
                    candidate.Engine.Ctx,
                    snapshot.GameTime);
                return state;
            }

            public void Dispose()
            {
                _initialSession.Dispose();
                UnityEngine.Object.DestroyImmediate(_stableRoot);
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }
    }
}
