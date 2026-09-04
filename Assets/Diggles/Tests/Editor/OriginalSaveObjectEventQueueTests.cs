#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveObjectEventQueueTests
    {
        private const string MissingEventName = "c2c_missing_event";

        [Test]
        public void LiveRingRetainsConsumedPayloadsAndWrapsWithoutClearing()
        {
            const string eventName = "evt_ring_probe";
            var entity = new DigglesTclEntity { Handle = 77 };
            for (int index = 0; index <= DigglesTclEntity.NativeEventRingCapacity; index++)
            {
                var payload = new Dictionary<string, string>
                {
                    ["-subject1"] = "41",
                    ["-subject2"] = "42",
                    ["-subject3"] = "43",
                    ["-pos1"] = "1.5 -2 3.25",
                    ["-pos2"] = "4 5.5 -6",
                    ["-pos3"] = "7.75 8 9",
                    ["-text1"] = index.ToString(CultureInfo.InvariantCulture),
                    ["-text2"] = "native residue",
                    ["-num1"] = "10.5",
                    ["-num2"] = "-11",
                    ["-num3"] = "12.25",
                };
                entity.EnqueueEvent(new EntityEvent(eventName, payload), 100d + index);
                Assert.That(entity.DequeueEvent().Name, Is.EqualTo(eventName));
            }

            Assert.That(entity.PendingEvents, Is.Empty);
            Assert.That(entity.NativeEventRingReadCursor, Is.EqualTo(1));
            Assert.That(entity.NativeEventRingWriteCursor, Is.EqualTo(1));
            Assert.That(entity.NativeEventRingRevision,
                Is.EqualTo((DigglesTclEntity.NativeEventRingCapacity + 1L) * 2L));
            Assert.That(entity.NativeEventRingSlots.Count, Is.EqualTo(
                DigglesTclEntity.NativeEventRingCapacity));
            Assert.That(entity.NativeEventRingSlots.All(slot => slot != null), Is.True);

            OriginalSaveTimerEventPayloadDocument wrapped = entity.NativeEventRingSlots[0] ??
                throw new AssertionException("Wrapped native event slot 0 is empty.");
            byte[] fixedName = wrapped.CopyFixedEventName();
            Assert.That(fixedName, Has.Length.EqualTo(32));
            Assert.That(fixedName[eventName.Length], Is.Zero);
            Assert.That(wrapped.EventName, Is.EqualTo(eventName));
            Assert.That(wrapped.OwnerObjectId, Is.EqualTo(entity.Handle));
            Assert.That(wrapped.Subject1ObjectId, Is.EqualTo(41));
            Assert.That(wrapped.Subject2ObjectId, Is.EqualTo(42));
            Assert.That(wrapped.Subject3ObjectId, Is.EqualTo(43));
            float[] expectedPositions = { 1.5f, -2f, 3.25f, 4f, 5.5f, -6f, 7.75f, 8f, 9f };
            OriginalSaveRaw32[] actualPositions =
            {
                wrapped.Position1X, wrapped.Position1Y, wrapped.Position1Z,
                wrapped.Position2X, wrapped.Position2Y, wrapped.Position2Z,
                wrapped.Position3X, wrapped.Position3Y, wrapped.Position3Z,
            };
            for (int index = 0; index < actualPositions.Length; index++)
            {
                Assert.That(actualPositions[index].Bits,
                    Is.EqualTo(BitConverter.SingleToInt32Bits(expectedPositions[index])));
            }
            Assert.That(wrapped.Text1.Text, Is.EqualTo("64"));
            Assert.That(wrapped.Text2.Text, Is.EqualTo("native residue"));
            Assert.That(wrapped.Number1.Bits, Is.EqualTo(BitConverter.SingleToInt32Bits(10.5f)));
            Assert.That(wrapped.Number2.Bits, Is.EqualTo(BitConverter.SingleToInt32Bits(-11f)));
            Assert.That(wrapped.Number3.Bits, Is.EqualTo(BitConverter.SingleToInt32Bits(12.25f)));
            Assert.That(wrapped.CreatedAt.Bits, Is.EqualTo(BitConverter.DoubleToInt64Bits(164d)));
        }

        [Test]
        public void WindowIsEmptyForAllButFourCorpusObjects()
        {
            string[] paths = FindCorpusPaths();
            var nonEmptyWindows = new List<string>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveObjectCollection objects = codec.Read(path).Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                foreach (OriginalSaveObjectDocument obj in objects)
                {
                    int windowSize = obj.EventQueueDocument.PendingWindowCount;
                    if (windowSize == 0) continue;
                    nonEmptyWindows.Add(
                        $"{Path.GetFileName(path)}#{obj.ObjectId}:{windowSize}");
                }
            }

            nonEmptyWindows.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(
                new[]
                {
                    "save_tutorial3.sav#1827:51",
                    "save_tutorial3.sav#1828:51",
                    "save_tutorial4.sav#1827:43",
                    "save_tutorial4.sav#1828:43",
                },
                nonEmptyWindows);
        }

        [Test]
        public void PendingWindowResolvesToRegisteredEvents()
        {
            string[] paths = FindCorpusPaths();
            var codec = new OriginalSaveArchiveCodec();
            var planner = new OriginalSaveLoadPlanner();
            var stager = new OriginalSaveRuntimeStager();
            Assert.That(planner.RegisteredEventNames, Is.Not.Empty);
            Assert.That(planner.RegisteredEventNames.Count, Is.LessThanOrEqualTo(128));
            int pendingCount = 0;
            int residueCount = 0;
            int delayedCount = 0;
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveObjectCollection objects = document.Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                OriginalSaveRuntimeSnapshot snapshot = stager.Stage(planner.Prepare(document));
                foreach (OriginalSaveObjectDocument obj in objects)
                {
                    OriginalSaveObjectEventQueueDocument queue = obj.EventQueueDocument;
                    OriginalSaveRuntimeEntitySnapshot staged = snapshot.EntitiesById[obj.ObjectId];
                    Assert.That(staged.EventQueueReadCursor, Is.EqualTo(queue.ReadCursor), path);
                    Assert.That(staged.EventQueueWriteCursor, Is.EqualTo(queue.WriteCursor), path);
                    Assert.That(
                        staged.EventQueueSlots,
                        Has.Count.EqualTo(OriginalSaveObjectEventQueueDocument.Capacity),
                        path);

                    for (int slotIndex = 0; slotIndex < queue.Slots.Count; slotIndex++)
                    {
                        OriginalSaveTimerEventPayloadDocument? parsed = queue.Slots[slotIndex];
                        OriginalSaveRuntimeEntityEventSnapshot? copied =
                            staged.EventQueueSlots[slotIndex];
                        if (parsed == null)
                        {
                            Assert.That(copied, Is.Null, $"{path}, object {obj.ObjectId}, slot {slotIndex}");
                            continue;
                        }

                        OriginalSaveRuntimeEntityEventSnapshot copiedEvent = copied ??
                            throw new AssertionException(
                                $"{path}, object {obj.ObjectId}, slot {slotIndex} was not copied.");
                        bool isPending = queue.IsPendingSlot(slotIndex);
                        Assert.That(
                            copiedEvent.IsDeliverable,
                            Is.EqualTo(isPending),
                            $"{path}, object {obj.ObjectId}, slot {slotIndex}");
                        Assert.That(copiedEvent.RingSlotIndex, Is.EqualTo(slotIndex));
                        Assert.That(copiedEvent.IsDelayed, Is.False);
                        AssertPayloadCopied(
                            parsed,
                            copiedEvent,
                            $"{path}, object {obj.ObjectId}, slot {slotIndex}");
                        if (isPending)
                        {
                            Assert.That(
                                planner.IsRegisteredEventName(copiedEvent.EventName),
                                Is.True,
                                $"{path}, object {obj.ObjectId}, slot {slotIndex}");
                            pendingCount++;
                        }
                        else
                        {
                            residueCount++;
                        }
                    }

                    Assert.That(
                        staged.DelayedEvents,
                        Has.Count.EqualTo(queue.DelayedRecords.Count),
                        $"{path}, object {obj.ObjectId}");
                    for (int delayedIndex = 0; delayedIndex < staged.DelayedEvents.Count; delayedIndex++)
                    {
                        OriginalSaveRuntimeEntityEventSnapshot delayed =
                            staged.DelayedEvents[delayedIndex];
                        Assert.That(delayed.IsDelayed, Is.True);
                        Assert.That(delayed.IsDeliverable, Is.True);
                        Assert.That(delayed.RingSlotIndex, Is.Null);
                        AssertPayloadCopied(
                            queue.DelayedRecords[delayedIndex],
                            delayed,
                            $"{path}, object {obj.ObjectId}, delayed {delayedIndex}");
                        Assert.That(
                            planner.IsRegisteredEventName(delayed.EventName),
                            Is.True,
                            $"{path}, object {obj.ObjectId}, delayed {delayedIndex}");
                        delayedCount++;
                    }

                    int expectedPendingCount =
                        queue.PendingWindowCount + queue.DelayedRecords.Count;
                    Assert.That(
                        staged.PendingEvents,
                        Has.Count.EqualTo(expectedPendingCount),
                        $"{path}, object {obj.ObjectId}");
                    for (int pendingIndex = 0;
                         pendingIndex < queue.PendingWindowCount;
                         pendingIndex++)
                    {
                        int slotIndex = queue.GetPendingSlotIndex(pendingIndex);
                        Assert.That(
                            staged.PendingEvents[pendingIndex],
                            Is.SameAs(staged.EventQueueSlots[slotIndex]),
                            $"{path}, object {obj.ObjectId}, pending {pendingIndex}");
                    }
                }
            }

            Assert.That(pendingCount, Is.EqualTo(188));
            Assert.That(residueCount, Is.GreaterThan(0));
            Assert.That(delayedCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void UnresolvableEventNameFailsPreflight()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial3.sav");
            OriginalSaveObjectCollection objects = baseline.Objects ??
                throw new AssertionException("Tutorial 3 has no typed object collection.");
            OriginalSaveObjectDocument pendingOwner = objects.FindById(1827) ??
                throw new AssertionException("Tutorial 3 object 1827 was not found.");
            OriginalSaveObjectEventQueueDocument pendingQueue = pendingOwner.EventQueueDocument;
            int pendingSlotIndex = pendingQueue.GetPendingSlotIndex(0);
            long pendingNameOffset = pendingQueue.GetSlotPayloadOffset(pendingSlotIndex);
            var planner = new OriginalSaveLoadPlanner();
            Assert.That(planner.IsRegisteredEventName(MissingEventName), Is.False);

            byte[] pendingPayload = baseline.CopyPayload();
            WriteFixedEventName(pendingPayload, pendingNameOffset, MissingEventName);
            OriginalSaveArchiveDocument corruptedPending = ReadPayload(pendingPayload);
            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => planner.Prepare(corruptedPending))!;
            Assert.That(exception.Message, Does.Contain(MissingEventName));
            Assert.That(exception.Message, Does.Contain("1827"));
            Assert.That(exception.Message, Does.Contain(pendingNameOffset.ToString()));

            (OriginalSaveObjectDocument residueOwner, int residueSlotIndex) =
                FindOccupiedResidueSlot(objects);
            long residueNameOffset = residueOwner.EventQueueDocument.GetSlotPayloadOffset(
                residueSlotIndex);
            byte[] residuePayload = baseline.CopyPayload();
            WriteFixedEventName(residuePayload, residueNameOffset, MissingEventName);
            OriginalSaveArchiveDocument corruptedResidue = ReadPayload(residuePayload);
            Assert.DoesNotThrow(() => planner.Prepare(corruptedResidue));
        }

        [Test]
        public void StagedTutorial3CandidateCarriesPendingEvents()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial3.sav");
            OriginalSaveObjectCollection objects = document.Objects ??
                throw new AssertionException("Tutorial 3 has no typed object collection.");
            OriginalSaveObjectDocument settledGnome = objects.FirstOrDefault(
                obj => IsGnome(obj) &&
                       obj.EventQueueDocument.PendingWindowCount == 0 &&
                       obj.EventQueueDocument.DelayedRecords.Count == 0) ??
                throw new AssertionException("Tutorial 3 contains no settled gnome.");
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));

            Assert.That(snapshot.EntitiesById[1827].PendingEvents, Has.Count.EqualTo(51));
            Assert.That(snapshot.EntitiesById[1828].PendingEvents, Has.Count.EqualTo(51));
            Assert.That(snapshot.EntitiesById[settledGnome.ObjectId].PendingEvents, Is.Empty);

            using var session = new SessionHarness();
            var eventTclCommands = new List<string>();
            DigglesTclContext context = session.Candidate.Engine.Ctx;
            session.Candidate.Engine.Interp.CommandTrace = (command, _, _) =>
            {
                if (context.CurrentEvent != null ||
                    command is "set_event" or "clone_event" or "event_get")
                {
                    eventTclCommands.Add(command);
                }
            };
            OriginalSaveRuntimeSessionState staged = session.Stage(snapshot);

            Assert.That(staged.PendingEntityEvents[1827], Has.Count.EqualTo(51));
            Assert.That(staged.PendingEntityEvents[1828], Has.Count.EqualTo(51));
            Assert.That(staged.PendingEntityEvents[settledGnome.ObjectId], Is.Empty);
            Assert.That(
                staged.PendingEntityEvents[1827],
                Is.SameAs(snapshot.EntitiesById[1827].PendingEvents));
            Assert.That(
                staged.PendingEntityEvents[1828],
                Is.SameAs(snapshot.EntitiesById[1828].PendingEvents));
            Assert.That(staged.PendingEntityEvents[1827].All(
                pending => pending.IsDeliverable && pending.OwnerObjectId == 1827), Is.True);
            Assert.That(staged.PendingEntityEvents[1828].All(
                pending => pending.IsDeliverable && pending.OwnerObjectId == 1828), Is.True);
            Assert.That(staged.Entities[1827].PendingEvents, Is.Empty);
            Assert.That(staged.Entities[1828].PendingEvents, Is.Empty);
            Assert.That(staged.Entities[settledGnome.ObjectId].PendingEvents, Is.Empty);
            AssertNativeRingRestored(snapshot.EntitiesById[1827], staged.Entities[1827]);
            AssertNativeRingRestored(snapshot.EntitiesById[1828], staged.Entities[1828]);
            AssertNativeRingRestored(
                snapshot.EntitiesById[settledGnome.ObjectId],
                staged.Entities[settledGnome.ObjectId]);
            Assert.That(eventTclCommands, Is.Empty, "pending event Tcl ran during staging");
            var schedulerEntries = session.Candidate.Engine.Sim.Scheduler.CaptureEntries();
            Assert.That(
                schedulerEntries,
                Has.Length.EqualTo(snapshot.Timers.Records.Count),
                "per-object pending events must not add scheduler entries during staging");
            for (int index = 0; index < schedulerEntries.Length; index++)
            {
                Assert.That(
                    schedulerEntries[index].Handle,
                    Is.EqualTo(snapshot.Timers.Records[index].OwnerObjectId));
                Assert.That(
                    schedulerEntries[index].Event,
                    Is.EqualTo(snapshot.Timers.Records[index].EventName));
            }
        }

        private static bool IsGnome(OriginalSaveObjectDocument obj) =>
            obj.ClassName is "Zwerg" or "Frau" or "PseudoZwerg";

        private static void AssertPayloadCopied(
            OriginalSaveTimerEventPayloadDocument expected,
            OriginalSaveRuntimeEntityEventSnapshot actual,
            string message)
        {
            Assert.That(actual.EventName, Is.EqualTo(expected.EventName), message);
            CollectionAssert.AreEqual(
                expected.CopyFixedEventName(),
                actual.CopyFixedEventName(),
                message);
            Assert.That(actual.OwnerObjectId, Is.EqualTo(expected.OwnerObjectId), message);
            Assert.That(actual.Subject1ObjectId, Is.EqualTo(expected.Subject1ObjectId), message);
            Assert.That(actual.Subject2ObjectId, Is.EqualTo(expected.Subject2ObjectId), message);
            Assert.That(actual.Subject3ObjectId, Is.EqualTo(expected.Subject3ObjectId), message);
            var expectedArguments = new[]
            {
                expected.Position1X,
                expected.Position1Y,
                expected.Position1Z,
                expected.Position2X,
                expected.Position2Y,
                expected.Position2Z,
                expected.Position3X,
                expected.Position3Y,
                expected.Position3Z,
            };
            Assert.That(actual.Arguments, Has.Count.EqualTo(expectedArguments.Length), message);
            for (int index = 0; index < expectedArguments.Length; index++)
                Assert.That(actual.Arguments[index].Bits, Is.EqualTo(expectedArguments[index].Bits), message);
            CollectionAssert.AreEqual(
                expected.Text1.CopyBytesIncludingTerminator(),
                actual.Text1.CopyBytesIncludingTerminator(),
                message);
            CollectionAssert.AreEqual(
                expected.Text2.CopyBytesIncludingTerminator(),
                actual.Text2.CopyBytesIncludingTerminator(),
                message);
            var expectedWords = new[]
            {
                expected.Number1,
                expected.Number2,
                expected.Number3,
            };
            Assert.That(actual.Word1.Bits, Is.EqualTo(expectedWords[0].Bits), message);
            Assert.That(actual.Word2.Bits, Is.EqualTo(expectedWords[1].Bits), message);
            Assert.That(actual.Word3.Bits, Is.EqualTo(expectedWords[2].Bits), message);
            Assert.That(actual.DueTime.Bits, Is.EqualTo(expected.CreatedAt.Bits), message);
        }

        private static void AssertNativeRingRestored(
            OriginalSaveRuntimeEntitySnapshot expected,
            DigglesTclEntity actual)
        {
            string message = $"object {expected.ObjectId} live native event ring";
            Assert.That(actual.NativeEventRingReadCursor,
                Is.EqualTo(expected.EventQueueReadCursor), message);
            Assert.That(actual.NativeEventRingWriteCursor,
                Is.EqualTo(expected.EventQueueWriteCursor), message);
            Assert.That(actual.NativeEventRingRevision, Is.Zero, message);
            Assert.That(actual.NativeEventRingSlots.Count,
                Is.EqualTo(DigglesTclEntity.NativeEventRingCapacity), message);
            for (int slot = 0; slot < actual.NativeEventRingSlots.Count; slot++)
            {
                OriginalSaveRuntimeEntityEventSnapshot? saved = expected.EventQueueSlots[slot];
                OriginalSaveTimerEventPayloadDocument? live = actual.NativeEventRingSlots[slot];
                if (saved == null)
                {
                    Assert.That(live, Is.Null, $"{message}, slot {slot}");
                    continue;
                }
                Assert.That(live, Is.Not.Null, $"{message}, slot {slot}");
                AssertPayloadCopied(live!, saved, $"{message}, slot {slot}");
            }
        }

        private static (OriginalSaveObjectDocument owner, int slotIndex) FindOccupiedResidueSlot(
            OriginalSaveObjectCollection objects)
        {
            foreach (OriginalSaveObjectDocument obj in objects)
            {
                OriginalSaveObjectEventQueueDocument queue = obj.EventQueueDocument;
                for (int slotIndex = 0; slotIndex < queue.Slots.Count; slotIndex++)
                {
                    if (queue.Slots[slotIndex] != null && !queue.IsPendingSlot(slotIndex))
                        return (obj, slotIndex);
                }
            }

            throw new AssertionException("Tutorial 3 contains no occupied residue slot.");
        }

        private static string[] FindCorpusPaths()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths.Length, Is.GreaterThanOrEqualTo(98));
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private static OriginalSaveArchiveDocument ReadPayload(byte[] payload)
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true))
                gzip.Write(payload, 0, payload.Length);
            compressed.Position = 0;
            return new OriginalSaveArchiveCodec().Read(compressed);
        }

        private static void WriteFixedEventName(byte[] destination, long offset, string eventName)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(eventName);
            if (encoded.Length >= 32)
                throw new ArgumentException("A native event name must fit before the terminator.", nameof(eventName));
            int destinationOffset = checked((int)offset);
            Array.Clear(destination, destinationOffset, 32);
            Buffer.BlockCopy(encoded, 0, destination, destinationOffset, encoded.Length);
        }

        private sealed class SessionHarness : IDisposable
        {
            private readonly GameObject _stableRoot;
            private readonly RuntimeGameSession _initial;

            internal SessionHarness()
            {
                _stableRoot = new GameObject("OriginalSaveObjectEventQueueTests");
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                var engineFactory = new DigglesEngineFactory();
                _initial = RuntimeGameSession.CreateInitial(
                    engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                var host = new RuntimeWorldSessionHost(
                    engineFactory,
                    new ActiveGameSession(_initial),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                Candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                Candidate.Engine.LoadRuntimeDefinitions();
                Candidate.Engine.Ctx.EntitySpawner = new NullSpawner();
            }

            internal RuntimeGameSession Candidate { get; }

            internal OriginalSaveRuntimeSessionState Stage(OriginalSaveRuntimeSnapshot snapshot)
            {
                var saveableActionFactory = new RuntimeSaveableActionFactory();
                var actionFactory = new OriginalSaveActionFactory(saveableActionFactory);
                var actionStager = new OriginalSaveActionCandidateStager(actionFactory);
                var sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    actionStager,
                    new OriginalSaveActionInstaller());
                return sessionStager.StageCandidate(Candidate, snapshot, "BlockDB2.BDB");
            }

            public void Dispose()
            {
                Candidate.Dispose();
                _initial.Dispose();
                UnityEngine.Object.DestroyImmediate(_stableRoot);
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }

        private sealed class NullSpawner : IDigglesEntitySpawner
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
