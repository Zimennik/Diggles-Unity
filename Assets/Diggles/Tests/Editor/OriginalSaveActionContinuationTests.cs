#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveActionContinuationTests
    {
        [Test]
        public void LoreHeadAdvancesByGravityAndFriction()
        {
            var interpreter = new TclInterpreter();
            var context = new DigglesTclContext(interpreter);
            DigglesTclEntity owner = RegisterEntity(context, 1, "LoreOwner", Vector3.zero);
            DigglesTclEntity trailer = RegisterEntity(context, 2, "LoreTrailer", Vector3.zero);
            var start = new LoreTrackWaypoint(
                Vector3.zero,
                Vector3.zero,
                0f,
                10f);
            var end = new LoreTrackWaypoint(
                new Vector3(1f, 1f, 0f),
                Vector3.zero,
                0f,
                10f);
            float totalLength = LoreTrackSystem.CalculateSegmentLength(start.Position, end.Position);
            context.LoreTracks.RestoreRecords(new[]
            {
                new LoreTrackRecord(1, new[] { start, end }, 0f, totalLength, 0.5f),
            });

            int finishCount = 0;
            interpreter.RegisterCommand("mark_lore_finish", (_, _) =>
            {
                finishCount++;
                return "";
            });
            var state = new NativeLoreActionSaveState(
                Bits(1f),
                Bits(0.05f),
                new[] { new NativeLoreTargetSaveState(trailer.Handle, Bits(totalLength)) },
                WaypointBits(start, end));
            owner.ActiveAction = new RestoredNativeLoreAction(state, owner, context);
            owner.ActionFinish = "mark_lore_finish";
            var sim = new DigglesGameSim(context);

            sim.Step(0.1d);

            Assert.That(context.LoreTracks.TryGetRecord(owner.Handle, out LoreTrackRecord record),
                Is.True);
            Assert.That(record, Is.Not.Null);
            float gravityGain = 1f / totalLength * 1f * 0.1f / 2f;
            float expectedSpeed = 0.5f + gravityGain - 0.05f * 0.1f;
            Assert.That(record!.Speed, Is.EqualTo(expectedSpeed).Within(1e-6f));
            Assert.That(record.Progress, Is.EqualTo(expectedSpeed * 0.1f).Within(1e-6f));
            Assert.That(owner.Position, Is.EqualTo(start.Position));

            sim.Step(0.1d);

            Assert.That(owner.Position.x, Is.GreaterThan(0f));
            Assert.That(owner.Position.y, Is.GreaterThan(0f));
            for (int tick = 0; tick < 1000 && owner.ActiveAction != null; tick++)
                sim.Step(0.1d);

            Assert.That(owner.ActiveAction, Is.Null);
            Assert.That(record.Progress, Is.GreaterThanOrEqualTo(record.TotalLength));
            Assert.That(finishCount, Is.EqualTo(1));
            sim.Step(0.1d);
            Assert.That(finishCount, Is.EqualTo(1));
        }

        [Test]
        public void LoreRegistryLoadsWithoutActiveAction()
        {
            OriginalSaveRuntimeSnapshot snapshot = FindRegistryOnlySnapshot();
            Assert.That(snapshot.Actions.Any(action => action.Type == OriginalSaveActionType.Lore),
                Is.False);
            Assert.That(snapshot.ActionAuxiliaryRecords, Is.Not.Empty);

            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            var entities = new Dictionary<int, DigglesTclEntity>(snapshot.Entities.Count);
            foreach (OriginalSaveRuntimeEntitySnapshot source in snapshot.Entities)
            {
                DigglesTclEntity entity = RegisterEntity(
                    context,
                    source.ObjectId,
                    source.ClassName,
                    new Vector3(source.Position.X, source.Position.Y, source.Position.Z));
                entity.Rotation = new Vector3(
                    source.Rotation.X,
                    source.Rotation.Y,
                    source.Rotation.Z);
                entities.Add(entity.Handle, entity);
            }

            var factory = new OriginalSaveActionFactory(new RuntimeSaveableActionFactory());
            var stager = new OriginalSaveActionCandidateStager(factory);
            OriginalSavePreparedActionSet prepared = stager.StageCandidates(
                snapshot,
                entities,
                context);

            Assert.That(context.LoreTracks.Count, Is.Zero,
                "candidate staging must not install the Lore registry");
            new OriginalSaveActionInstaller().Install(prepared, entities);

            Assert.That(context.LoreTracks.Count,
                Is.EqualTo(snapshot.ActionAuxiliaryRecords.Count));
            LoreTrackRecord[] captured = context.LoreTracks.CaptureRecords();
            Assert.That(captured, Has.Length.EqualTo(snapshot.ActionAuxiliaryRecords.Count));
            foreach (OriginalSaveRuntimeActionAuxiliarySnapshot source in
                     snapshot.ActionAuxiliaryRecords)
            {
                Assert.That(context.LoreTracks.Contains(source.Value), Is.True);
                Assert.That(captured.Any(record => record.OwnerId == source.Value), Is.True);
            }
            Assert.That(commandCount, Is.Zero);
        }

        [Test]
        public void BallisticInFlightShotResolvesOnFirstTick()
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            interpreter.Eval("def_attrib atr_Hitpoints 0 100 100");
            DigglesTclEntity attacker = RegisterEntity(context, 1, "Attacker", Vector3.zero);
            DigglesTclEntity defender = RegisterEntity(
                context,
                2,
                "Defender",
                new Vector3(4f, 2f, 1f));
            context.Stats.Set(defender.Stats, "atr_Hitpoints", 100d);
            var sim = new DigglesGameSim(context);

            attacker.BallisticResult = 9;
            attacker.ActiveAction = new RestoredNativeAttackBallisticAction(
                BallisticState(defender, trailCompleted: true, damage: 7f),
                attacker,
                context);

            sim.Step(0.01d);

            Assert.That(context.Stats.Get(defender.Stats, "atr_Hitpoints"),
                Is.EqualTo(93d).Within(1e-6d));
            Assert.That(attacker.BallisticResult, Is.EqualTo(1));
            Assert.That(interpreter.Eval($"get_ballisticresult {attacker.Handle}").Trim(),
                Is.EqualTo("1"));
            Assert.That(attacker.ActiveAction, Is.Null);
            sim.Step(0.1d);
            Assert.That(context.Stats.Get(defender.Stats, "atr_Hitpoints"),
                Is.EqualTo(93d).Within(1e-6d),
                "a restored trail must apply its fixed damage only once");

            context.Stats.Set(defender.Stats, "atr_Hitpoints", 100d);
            attacker.BallisticResult = 1;
            attacker.ActiveAction = new RestoredNativeAttackBallisticAction(
                BallisticState(defender, trailCompleted: false, damage: 7f),
                attacker,
                context);

            sim.Step(0.01d);

            Assert.That(context.Stats.Get(defender.Stats, "atr_Hitpoints"),
                Is.EqualTo(100d).Within(1e-6d));
            Assert.That(attacker.BallisticResult, Is.Zero);
            Assert.That(attacker.ActiveAction, Is.Null);
            Assert.That(interpreter.Eval($"get_ballisticresult {attacker.Handle}").Trim(),
                Is.EqualTo("0"));
        }

        [Test]
        public void RestoredCrawlCountsDownAndFinishes()
        {
            var interpreter = new TclInterpreter();
            var context = new DigglesTclContext(interpreter);
            DigglesTclEntity owner = RegisterEntity(context, 1, "Crawler", Vector3.zero);
            int finishCount = 0;
            interpreter.RegisterCommand("mark_crawl_finish", (_, _) =>
            {
                finishCount++;
                return "";
            });
            owner.ActiveAction = new RestoredNativeCrawlAction(
                new NativeCrawlActionSaveState(2, Bits(Mathf.PI * 0.5f)));
            owner.ActionFinish = "mark_crawl_finish";
            var sim = new DigglesGameSim(context);

            sim.Step(0.05d);
            Assert.That(owner.ActiveAction, Is.Not.Null);
            sim.Step(0.05d);
            Assert.That(owner.ActiveAction, Is.Not.Null);
            sim.Step(0.1d);

            Assert.That(owner.ActiveAction, Is.Null);
            Assert.That(finishCount, Is.EqualTo(1));
            sim.Step(0.1d);
            Assert.That(finishCount, Is.EqualTo(1));
        }

        private static OriginalSaveRuntimeSnapshot FindRegistryOnlySnapshot()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveActionTableDocument? actions = document.Actions;
                if (actions == null || actions.AuxiliaryRecords.Count == 0 ||
                    actions.Records.Any(record => record.Type == OriginalSaveActionType.Lore))
                {
                    continue;
                }
                return new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
            }

            throw new AssertionException(
                "The original-save corpus has no Lore registry record without an active Lore action.");
        }

        private static NativeAttackBallisticActionSaveState BallisticState(
            DigglesTclEntity defender,
            bool trailCompleted,
            float damage)
        {
            return new NativeAttackBallisticActionSaveState(
                defender.Handle,
                0,
                Array.Empty<int>(),
                new[] { 0, 0 },
                new[] { 0x1234 },
                new[] { 0, Bits(damage) },
                0,
                new[]
                {
                    Bits(defender.Position.x),
                    Bits(defender.Position.y),
                    Bits(defender.Position.z),
                },
                trailCompleted ? (byte)1 : (byte)0,
                0,
                Bits(0f),
                0d);
        }

        private static DigglesTclEntity RegisterEntity(
            DigglesTclContext context,
            int handle,
            string className,
            Vector3 position)
        {
            var entity = new DigglesTclEntity
            {
                Class = new DigglesClass
                {
                    Name = className,
                    Kind = "object",
                },
                Position = position,
            };
            context.RegisterEntityAtHandle(entity, handle);
            return entity;
        }

        private static int[] WaypointBits(params LoreTrackWaypoint[] waypoints)
        {
            var result = new int[waypoints.Length * 8];
            int offset = 0;
            foreach (LoreTrackWaypoint waypoint in waypoints)
            {
                result[offset++] = Bits(waypoint.Position.x);
                result[offset++] = Bits(waypoint.Position.y);
                result[offset++] = Bits(waypoint.Position.z);
                result[offset++] = Bits(waypoint.Rotation.x);
                result[offset++] = Bits(waypoint.Rotation.y);
                result[offset++] = Bits(waypoint.Rotation.z);
                result[offset++] = Bits(waypoint.MinimumSpeed);
                result[offset++] = Bits(waypoint.MaximumSpeed);
            }
            return result;
        }

        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    }
}
