#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveTriggerDocumentTests
    {
        [Test]
        public void ReadsKnownNativeTriggerAndStagesItsRuntimeState()
        {
            OriginalSaveArchiveDocument document = ReadFixture(
                "quick_Quicksave_20201222_1304.sav");
            Assert.That(document.Triggers, Is.Not.Null);
            OriginalSaveTriggerTableDocument triggers = document.Triggers!;
            Assert.That(triggers.Slots, Has.Count.EqualTo(10));
            Assert.That(triggers.Slots.All(slot => slot.SerializedPointerValue != 0), Is.True);
            Assert.That(triggers.Slots.All(slot => slot.Record != null), Is.True);

            OriginalSaveTriggerRecord record = triggers.Slots[0].Record!;
            Assert.That(record.Index, Is.Zero);
            Assert.That(record.Kind, Is.EqualTo(3));
            Assert.That(record.Execution, Is.EqualTo(2));
            Assert.That(record.OwnerObjectId, Is.EqualTo(1630));
            Assert.That(record.FireScript.Text, Is.EqualTo("check_activation"));
            Assert.That(record.TargetRange.AsSingle, Is.EqualTo(3f));
            Assert.That(record.TargetObjectId, Is.EqualTo(-1));
            Assert.That(record.TargetClass.Text, Is.EqualTo("?"));
            Assert.That(record.TargetTypeMask, Is.EqualTo(1 << 1));
            Assert.That(record.TargetOwnerSpec.Text, Is.EqualTo("0"));
            Assert.That(record.TargetCount, Is.EqualTo(1));

            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            OriginalSaveRuntimeTriggerSnapshot runtime = staged.Triggers[0]!;
            Assert.That(runtime.Index, Is.Zero);
            Assert.That(runtime.OwnerObjectId, Is.EqualTo(1630));
            Assert.That(runtime.TargetClass, Is.Null);
            Assert.That(runtime.TargetTypes, Is.EqualTo(new[] { "gnome" }));
            Assert.That(runtime.TargetOwnerSpec, Is.EqualTo("0"));
            Assert.That(runtime.UnsupportedTargetTypeMask, Is.Zero);
        }

        [Test]
        public void ReadsEveryAvailableCurrentSaveIntoTypedTriggerDocuments()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            // The port's own quicksaves land in this shared directory and are out of scope for
            // the native-corpus assertions.
            paths = Array.FindAll(paths, p => !OriginalDataTestFixture.IsPortWrittenSaveName(Path.GetFileName(p)));
            Assume.That(paths, Is.Not.Empty);

            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                Assert.DoesNotThrow(() => new OriginalSaveLoadPlanner().Prepare(document), path);
                Assert.That(document.Triggers, Is.Not.Null, path);
                Assert.That(document.Triggers!.Slots.Count,
                    Is.EqualTo(document.Metadata.Logic!.SchedulerSlotCount), path);
                Assert.That(document.Triggers.Slots.Count(slot => slot.Record != null),
                    Is.EqualTo(document.Metadata.Logic.SchedulerRecordCount), path);

                foreach (OriginalSaveTriggerSlot slot in document.Triggers.Slots)
                {
                    Assert.That(slot.Record == null,
                        Is.EqualTo(slot.SerializedPointerValue == 0), path);
                }
            }
        }

        [Test]
        [Explicit("Pins exact counts of a developer-local save corpus")]
        public void AuditsNativeTriggerReferenceCorpus()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            // The port's own quicksaves land in this shared directory and are out of scope for
            // the native-corpus assertions.
            paths = Array.FindAll(paths, p => !OriginalDataTestFixture.IsPortWrittenSaveName(Path.GetFileName(p)));
            Assume.That(paths, Is.Not.Empty);

            int records = 0;
            int recordIndexMismatches = 0;
            int unsupportedTypeMasks = 0;
            int nonFiniteValues = 0;
            int missingOwners = 0;
            int missingTargets = 0;
            int duplicateOwners = 0;
            int objectBackReferences = 0;
            int invalidObjectBackReferences = 0;
            int ownerBackReferenceMismatches = 0;
            int slots = 0;
            int deadTombstones = 0;
            int invalidDeadTombstones = 0;
            var anomalies = new List<string>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                var objects = document.Objects!.ToDictionary(obj => obj.ObjectId);
                var owners = new HashSet<int>();
                OriginalSaveTriggerTableDocument table = document.Triggers!;
                slots += table.Slots.Count;
                for (int slotIndex = 0; slotIndex < table.Slots.Count; slotIndex++)
                {
                    OriginalSaveTriggerRecord? record = table.Slots[slotIndex].Record;
                    if (record == null) continue;
                    records++;
                    if (record.Index != slotIndex)
                    {
                        recordIndexMismatches++;
                        anomalies.Add(
                            $"{Path.GetFileName(path)} slot={slotIndex} index={record.Index} " +
                            $"kind={record.Kind} owner={record.OwnerObjectId}");
                    }
                    if ((record.TargetTypeMask & ~0x1fff) != 0) unsupportedTypeMasks++;
                    if (!Finite(record.LastCheck) || !Finite(record.CheckTimer) ||
                        !Finite(record.CheckIdle) || !Finite(record.CheckNear) ||
                        !Finite(record.TargetRange.AsSingle) || !Finite(record.FireTime) ||
                        !Finite(record.Interval))
                    {
                        nonFiniteValues++;
                    }
                    if (!objects.ContainsKey(record.OwnerObjectId)) missingOwners++;
                    if (record.TargetObjectId >= 0 && !objects.ContainsKey(record.TargetObjectId))
                        missingTargets++;
                    if (!owners.Add(record.OwnerObjectId)) duplicateOwners++;
                    if (record.Kind == 0)
                    {
                        deadTombstones++;
                        if (record.Index != -1 || record.OwnerObjectId != -1)
                            invalidDeadTombstones++;
                    }
                    if (objects.TryGetValue(record.OwnerObjectId, out OriginalSaveObjectDocument owner) &&
                        owner.CommonState.TriggerRecordId != slotIndex)
                    {
                        ownerBackReferenceMismatches++;
                    }
                }

                foreach (OriginalSaveObjectDocument obj in document.Objects)
                {
                    int triggerId = obj.CommonState.TriggerRecordId;
                    if (triggerId < 0) continue;
                    objectBackReferences++;
                    if (triggerId >= table.Slots.Count ||
                        table.Slots[triggerId].Record?.OwnerObjectId != obj.ObjectId)
                    {
                        invalidObjectBackReferences++;
                    }
                }
            }

            TestContext.Out.WriteLine(
                $"slots={slots}; records={records}; indexMismatch={recordIndexMismatches}; " +
                $"unsupportedMask={unsupportedTypeMasks}; nonFinite={nonFiniteValues}; " +
                $"missingOwner={missingOwners}; missingTarget={missingTargets}; " +
                $"duplicateOwner={duplicateOwners}; objectBackRefs={objectBackReferences}; " +
                $"invalidObjectBackRefs={invalidObjectBackReferences}; " +
                $"ownerBackRefMismatch={ownerBackReferenceMismatches}; " +
                $"dead={deadTombstones}; invalidDead={invalidDeadTombstones}");
            foreach (string anomaly in anomalies) TestContext.Out.WriteLine(anomaly);
            Assert.That(slots, Is.EqualTo(304));
            Assert.That(records, Is.EqualTo(304));
            Assert.That(recordIndexMismatches, Is.EqualTo(deadTombstones));
            Assert.That(unsupportedTypeMasks, Is.Zero);
            Assert.That(nonFiniteValues, Is.Zero);
            Assert.That(missingOwners, Is.EqualTo(deadTombstones));
            Assert.That(missingTargets, Is.Zero);
            Assert.That(invalidObjectBackReferences, Is.Zero);
            Assert.That(ownerBackReferenceMismatches, Is.Zero);
            Assert.That(deadTombstones, Is.EqualTo(12));
            Assert.That(invalidDeadTombstones, Is.Zero);
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
