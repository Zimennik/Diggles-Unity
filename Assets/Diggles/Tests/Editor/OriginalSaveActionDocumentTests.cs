#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    public sealed class OriginalSaveActionDocumentTests
    {
        [Test]
        public void ReadsTypedTutorialActionTableLosslessly()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial.sav");
            Assert.That(document.Actions, Is.Not.Null);
            OriginalSaveActionTableDocument actions = document.Actions!;

            Assert.That(actions.Records, Has.Count.EqualTo(327));
            Assert.That(actions.AuxiliaryRecords, Is.Empty);
            Assert.That(
                CountTypes(actions.Records),
                Is.EquivalentTo(new[]
                {
                    new KeyValuePair<OriginalSaveActionType, int>(OriginalSaveActionType.Null, 309),
                    new KeyValuePair<OriginalSaveActionType, int>(OriginalSaveActionType.Wait, 1),
                    new KeyValuePair<OriginalSaveActionType, int>(OriginalSaveActionType.Anim, 16),
                    new KeyValuePair<OriginalSaveActionType, int>(OriginalSaveActionType.Walk, 1),
                }));

            foreach (OriginalSaveActionRecord record in actions.Records)
            {
                Assert.That(record.PayloadOffset, Is.GreaterThan(0));
                Assert.That(record.CopyRawPayload(), Has.Length.EqualTo(record.PayloadLength));
                if (record.Type == OriginalSaveActionType.Null)
                {
                    Assert.That(record.Payload, Is.TypeOf<OriginalSaveNullActionPayload>());
                    Assert.That(record.PayloadLength, Is.Zero);
                    continue;
                }

                Assert.That(record.Payload.Common, Is.Not.Null);
                Assert.That(record.Payload.Common!.OwnerObjectId, Is.EqualTo(record.OwnerObjectId));
                AssertNativeString(record.Payload.Common.Argument);
                AssertNativeString(record.Payload.Common.FinishCode);
                AssertNativeString(record.Payload.Common.BreakCode);
                Assert.That(record.PayloadLength, Is.GreaterThan(0));
            }

            var walk = (OriginalSaveWalkActionPayload)actions.Records
                .Single(record => record.Type == OriginalSaveActionType.Walk)
                .Payload;
            Assert.That(walk.AnimationBanks, Has.Count.EqualTo(4));
            Assert.That(walk.AnimationBanks.All(bank => bank.SlotValues.Count == 24 * 5), Is.True);
            Assert.That(walk.TargetAndDistanceValues, Has.Count.EqualTo(4));
            Assert.That(walk.MoveFlags, Has.Count.EqualTo(4));
            AssertNativeString(walk.WalkTimerCallback);
            AssertNativeString(walk.WalkCollisionCallback);
            AssertNativeString(walk.ObjectUseCallback);
        }

        [Test]
        public void ReadsAllFixtureBackedActionPayloadTypes()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_test.sav");
            Assert.That(document.Actions, Is.Not.Null);
            IReadOnlyList<OriginalSaveActionRecord> records = document.Actions!.Records;

            Assert.That(records.OfTypeByPayload<OriginalSaveWaitActionPayload>().Length, Is.EqualTo(8));
            Assert.That(records.OfTypeByPayload<OriginalSaveAnimActionPayload>().Length, Is.EqualTo(54));
            Assert.That(records.OfTypeByPayload<OriginalSaveRotateActionPayload>().Length, Is.EqualTo(2));
            Assert.That(records.OfTypeByPayload<OriginalSaveFlyActionPayload>().Length, Is.EqualTo(8));
            Assert.That(records.OfTypeByPayload<OriginalSaveWalkActionPayload>().Length, Is.EqualTo(21));
            Assert.That(records.OfTypeByPayload<OriginalSaveMoveActionPayload>().Length, Is.EqualTo(8));

            foreach (OriginalSaveFlyActionPayload fly in
                records.Select(record => record.Payload).OfType<OriginalSaveFlyActionPayload>())
            {
                Assert.That(fly.MotionValues, Has.Count.EqualTo(12));
            }
            foreach (OriginalSaveMoveActionPayload move in
                records.Select(record => record.Payload).OfType<OriginalSaveMoveActionPayload>())
            {
                Assert.That(move.Vectors, Has.Count.EqualTo(9));
            }
        }

        [Test]
        public void RuntimeStagerIndexesActionsAndFactoryPreparesWithoutSideEffects()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial.sav");
            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));

            Assert.That(staged.Actions, Has.Count.EqualTo(document.Actions!.Records.Count));
            Assert.That(staged.ActionsByOwnerId, Has.Count.EqualTo(staged.Actions.Count));
            Assert.That(staged.ActionAuxiliaryRecords,
                Has.Count.EqualTo(document.Actions.AuxiliaryRecords.Count));
            for (int index = 0; index < staged.Actions.Count; index++)
            {
                OriginalSaveRuntimeActionSnapshot action = staged.Actions[index];
                OriginalSaveActionRecord source = document.Actions.Records[index];
                Assert.That(action.OwnerObjectId, Is.EqualTo(source.OwnerObjectId));
                Assert.That(action.Type, Is.EqualTo(source.Type));
                CollectionAssert.AreEqual(source.CopyRawPayload(), action.CopyRawPayload());
                Assert.That(staged.EntitiesById[action.OwnerObjectId].StagedAction,
                    Is.SameAs(action));
            }

            OriginalSaveRuntimeActionSnapshot wait = staged.Actions.Single(
                action => action.Type == OriginalSaveActionType.Wait);
            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            var owner = new DigglesTclEntity
            {
                Class = new DigglesClass { Name = staged.EntitiesById[wait.OwnerObjectId].ClassName },
            };
            context.RegisterEntityAtHandle(owner, wait.OwnerObjectId);
            var factory = new OriginalSaveActionFactory(new RuntimeSaveableActionFactory());

            OriginalSavePreparedAction prepared = factory.CreateCandidate(
                wait,
                owner,
                context,
                staged.GameTime);

            Assert.That(prepared.Action, Is.TypeOf<WaitAction>());
            Assert.That(prepared.FinishCode, Is.EqualTo(wait.Common!.FinishCode.Text));
            Assert.That(prepared.BreakCode, Is.EqualTo(wait.Common.BreakCode.Text));
            var waitState = ((ISaveableEntityAction)prepared.Action!).CaptureSaveState()
                as WaitActionSaveState;
            Assert.That(waitState, Is.Not.Null);
            Assert.That(waitState!.RemainingSeconds,
                Is.EqualTo(wait.RemainingSecondsAt(staged.GameTime)));
            Assert.That(owner.ActiveAction, Is.Null);
            Assert.That(commandCount, Is.Zero,
                "candidate preparation must not evaluate Tcl or install the action");

            OriginalSaveRuntimeActionSnapshot walk = staged.Actions.Single(
                action => action.Type == OriginalSaveActionType.Walk);
            DigglesTclEntity walkOwner = new()
            {
                Class = new DigglesClass
                {
                    Name = staged.EntitiesById[walk.OwnerObjectId].ClassName,
                },
            };
            context.RegisterEntityAtHandle(walkOwner, walk.OwnerObjectId);
            OriginalSavePreparedAction preparedWalk = factory.CreateCandidate(
                walk,
                walkOwner,
                context,
                staged.GameTime);
            Assert.That(preparedWalk.Action, Is.TypeOf<RestoredWalkAction>());
            Assert.That(preparedWalk.InitialWalkResult, Is.EqualTo(2));
            var walkState = (WalkActionSaveState)
                ((ISaveableEntityAction)preparedWalk.Action!).CaptureSaveState();
            var walkPayload = (OriginalSaveWalkActionPayload)walk.Payload;
            AssertVector(walkState.TargetNative, walkPayload.TargetAndDistanceValues, 1);
            Assert.That(walkState.WalkTimerCallback,
                Is.EqualTo(walkPayload.WalkTimerCallback.Text));
            Assert.That(walkOwner.ActiveAction, Is.Null);
            Assert.That(walkOwner.WalkResult, Is.Zero,
                "candidate construction must not publish native walking state");
            Assert.That(commandCount, Is.Zero);

            var candidateInterpreter = new TclInterpreter();
            int candidateCommands = 0;
            candidateInterpreter.CommandTrace = (_, _, _) => candidateCommands++;
            var candidateContext = new DigglesTclContext(candidateInterpreter);
            var candidateEntities = new Dictionary<int, DigglesTclEntity>(staged.Entities.Count);
            foreach (OriginalSaveRuntimeEntitySnapshot entityState in staged.Entities)
            {
                var entity = new DigglesTclEntity
                {
                    Class = new DigglesClass { Name = entityState.ClassName },
                };
                candidateContext.RegisterEntityAtHandle(entity, entityState.ObjectId);
                candidateEntities.Add(entityState.ObjectId, entity);
            }
            var candidateStager = new OriginalSaveActionCandidateStager(factory);

            OriginalSavePreparedActionSet preparedSet = candidateStager.StageCandidates(
                staged,
                candidateEntities,
                candidateContext);
            Assert.That(preparedSet.Actions, Has.Count.EqualTo(staged.Actions.Count));
            Assert.That(preparedSet.ActionsByOwnerId[walk.OwnerObjectId].Action,
                Is.TypeOf<RestoredWalkAction>());
            Assert.That(candidateEntities.Values.All(entity => entity.ActiveAction == null), Is.True,
                "complete action staging must not install any prepared action");
            Assert.That(candidateCommands, Is.Zero);
        }

        [Test]
        public void RestoredWalkDefersRouteBuildUntilFirstOrdinaryTick()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial.sav");
            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            OriginalSaveRuntimeActionSnapshot walk = staged.Actions.Single(
                action => action.Type == OriginalSaveActionType.Walk);
            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            DigglesTclEntity owner = CreateCandidateOwner(staged, walk.OwnerObjectId);
            context.RegisterEntityAtHandle(owner, walk.OwnerObjectId);
            OriginalSavePreparedAction prepared =
                new OriginalSaveActionFactory(new RuntimeSaveableActionFactory()).CreateCandidate(
                    walk,
                    owner,
                    context,
                    staged.GameTime);

            Assert.That(owner.WalkResult, Is.Zero, "untouched until the first ordinary tick");
            Assert.That(commandCount, Is.Zero);

            Assert.That(prepared.Action!.Tick(0.01d), Is.True);

            Assert.That(owner.WalkResult, Is.EqualTo(2));
            Assert.That(commandCount, Is.Zero);
            var state = (WalkActionSaveState)
                ((ISaveableEntityAction)prepared.Action).CaptureSaveState();
            Assert.That(state.TickCounter,
                Is.EqualTo(((OriginalSaveWalkActionPayload)walk.Payload).TickCounter + 1));
        }

        [Test]
        public void FixtureBackedAttackStagesWithoutInstallationOrTcl()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial2.sav");
            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            Assert.That(staged.Actions.Any(action => action.Type == OriginalSaveActionType.Attack),
                Is.True);
            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            var entities = new Dictionary<int, DigglesTclEntity>(staged.Entities.Count);
            foreach (OriginalSaveRuntimeEntitySnapshot entity in staged.Entities)
            {
                DigglesTclEntity candidate = CreateCandidateOwner(staged, entity.ObjectId);
                context.RegisterEntityAtHandle(candidate, entity.ObjectId);
                entities.Add(entity.ObjectId, candidate);
            }

            OriginalSavePreparedActionSet prepared =
                new OriginalSaveActionCandidateStager(
                    new OriginalSaveActionFactory(new RuntimeSaveableActionFactory()))
                    .StageCandidates(staged, entities, context);

            OriginalSavePreparedAction[] attacks = prepared.Actions
                .Where(action => action.SourceType == OriginalSaveActionType.Attack)
                .ToArray();
            Assert.That(attacks, Has.Length.EqualTo(4));
            Assert.That(attacks.All(action => action.Action is RestoredNativeAttackAction), Is.True);
            Assert.That(entities.Values.All(entity => entity.ActiveAction == null), Is.True);
            Assert.That(entities.Values.All(entity => entity.WalkResult == 0), Is.True);
            Assert.That(commandCount, Is.Zero);
        }

        [Test]
        public void RestoredAttackDispatchesSavedChainOnlyOnTerminalTick()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial2.sav");
            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            OriginalSaveRuntimeActionSnapshot source = staged.Actions.Single(action =>
                action.Type == OriginalSaveActionType.Attack &&
                ((OriginalSaveAttackActionPayload)action.Payload).ChainedActionVerb.Text == "wait");
            var interpreter = new TclInterpreter();
            var context = new DigglesTclContext(interpreter);
            DigglesTclEntity owner = CreateCandidateOwner(staged, source.OwnerObjectId);
            context.RegisterEntityAtHandle(owner, source.OwnerObjectId);
            int actionCommands = 0;
            interpreter.RegisterCommand("action", (_, args) =>
            {
                actionCommands++;
                Assert.That(args[1], Is.EqualTo(owner.Handle.ToString()));
                Assert.That(args[2], Is.EqualTo("wait"));
                owner.ActiveAction = new WaitAction(0.3d);
                return "";
            });
            OriginalSavePreparedAction prepared =
                new OriginalSaveActionFactory(new RuntimeSaveableActionFactory()).CreateCandidate(
                    source,
                    owner,
                    context,
                    staged.GameTime);

            owner.ActiveAction = prepared.Action;
            Assert.That(actionCommands, Is.Zero);
            var state = (NativeAttackActionSaveState)
                ((ISaveableEntityAction)prepared.Action!).CaptureSaveState();
            bool running = prepared.Action!.Tick(state.RemainingSeconds + 0.1d);

            Assert.That(running, Is.True);
            Assert.That(actionCommands, Is.EqualTo(1));
            Assert.That(owner.ActiveAction, Is.TypeOf<WaitAction>());
        }

        [Test]
        public void PreparedActionInstallerPreflightsWholeSetAndInstallsWithoutTcl()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial2.sav");
            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            var entities = new Dictionary<int, DigglesTclEntity>(staged.Entities.Count);
            foreach (OriginalSaveRuntimeEntitySnapshot entity in staged.Entities)
            {
                DigglesTclEntity candidate = CreateCandidateOwner(staged, entity.ObjectId);
                context.RegisterEntityAtHandle(candidate, entity.ObjectId);
                entities.Add(entity.ObjectId, candidate);
            }
            OriginalSavePreparedActionSet prepared =
                new OriginalSaveActionCandidateStager(
                    new OriginalSaveActionFactory(new RuntimeSaveableActionFactory()))
                    .StageCandidates(staged, entities, context);
            var installer = new OriginalSaveActionInstaller();
            int removedId = prepared.Actions[^1].OwnerObjectId;
            DigglesTclEntity removed = entities[removedId];
            entities.Remove(removedId);

            Assert.Throws<InvalidOperationException>(() => installer.Install(prepared, entities));
            Assert.That(entities.Values.All(entity => entity.ActiveAction == null), Is.True);
            Assert.That(entities.Values.All(entity => entity.ActionFinish == null), Is.True);
            Assert.That(entities.Values.All(entity => entity.ActionBreak == null), Is.True);

            entities.Add(removedId, removed);
            installer.Install(prepared, entities);

            foreach (OriginalSavePreparedAction action in prepared.Actions)
            {
                DigglesTclEntity owner = entities[action.OwnerObjectId];
                Assert.That(owner.ActiveAction, Is.SameAs(action.Action));
                if (action.InitialWalkResult is int walkResult)
                    Assert.That(owner.WalkResult, Is.EqualTo(walkResult));
            }
            Assert.That(commandCount, Is.Zero);
        }

        [Test]
        public void OriginalActionFactoryRestoresMoveFromSavedTargetAndRemainingTicks()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_test.sav");
            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            OriginalSaveRuntimeActionSnapshot move = staged.Actions.First(
                action => action.Type == OriginalSaveActionType.Move);
            var payload = (OriginalSaveMoveActionPayload)move.Payload;
            OriginalSaveRuntimeEntitySnapshot entityState =
                staged.EntitiesById[move.OwnerObjectId];
            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            var owner = new DigglesTclEntity
            {
                Class = new DigglesClass { Name = entityState.ClassName },
                Position = new UnityEngine.Vector3(
                    entityState.Position.X,
                    entityState.Position.Y,
                    entityState.Position.Z),
            };
            context.RegisterEntityAtHandle(owner, move.OwnerObjectId);
            UnityEngine.Vector3 originalPosition = owner.Position;

            OriginalSavePreparedAction prepared =
                new OriginalSaveActionFactory(new RuntimeSaveableActionFactory()).CreateCandidate(
                    move,
                    owner,
                    context,
                    staged.GameTime);

            Assert.That(prepared.Action, Is.TypeOf<MoveAction>());
            var state = ((ISaveableEntityAction)prepared.Action!).CaptureSaveState()
                as MoveActionSaveState;
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.Start, Is.EqualTo(originalPosition));
            Assert.That(state.Target.x, Is.EqualTo(payload.Vectors[3].AsSingle));
            Assert.That(state.Target.y, Is.EqualTo(payload.Vectors[4].AsSingle));
            Assert.That(state.Target.z, Is.EqualTo(payload.Vectors[5].AsSingle));
            Assert.That(state.RemainingNativeTicks, Is.EqualTo(payload.RemainingNativeTicks));
            Assert.That(owner.Position, Is.EqualTo(originalPosition));
            Assert.That(owner.ActiveAction, Is.Null);
            Assert.That(commandCount, Is.Zero);
        }

        [Test]
        public void OriginalActionFactoryRestoresRotateAndFlyWithoutAdvancingThem()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_test.sav");
            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            OriginalSaveRuntimeActionSnapshot rotate = staged.Actions.First(
                action => action.Type == OriginalSaveActionType.Rotate);
            OriginalSaveRuntimeActionSnapshot fly = staged.Actions.First(
                action => action.Type == OriginalSaveActionType.Fly);
            var rotatePayload = (OriginalSaveRotateActionPayload)rotate.Payload;
            var flyPayload = (OriginalSaveFlyActionPayload)fly.Payload;
            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            DigglesTclEntity rotateOwner = CreateCandidateOwner(staged, rotate.OwnerObjectId);
            DigglesTclEntity flyOwner = CreateCandidateOwner(staged, fly.OwnerObjectId);
            context.RegisterEntityAtHandle(rotateOwner, rotate.OwnerObjectId);
            context.RegisterEntityAtHandle(flyOwner, fly.OwnerObjectId);
            var factory = new OriginalSaveActionFactory(new RuntimeSaveableActionFactory());

            OriginalSavePreparedAction preparedRotate = factory.CreateCandidate(
                rotate,
                rotateOwner,
                context,
                staged.GameTime);
            OriginalSavePreparedAction preparedFly = factory.CreateCandidate(
                fly,
                flyOwner,
                context,
                staged.GameTime);

            Assert.That(preparedRotate.Action, Is.TypeOf<TurnToFaceAction>());
            var rotateState = (RotateActionSaveState)
                ((ISaveableEntityAction)preparedRotate.Action!).CaptureSaveState();
            Assert.That(rotateState.AbsoluteTargetYawRadians,
                Is.EqualTo(rotatePayload.AbsoluteTargetYaw.AsSingle));
            Assert.That(rotateState.AngularStepRadiansPerTick,
                Is.EqualTo(Math.Abs(rotatePayload.AngularStep.AsSingle)));
            Assert.That(rotateState.NativeDirectionSign, Is.EqualTo(rotatePayload.DirectionSign));
            Assert.That(rotateState.NativeTurnClipFrameCount,
                Is.EqualTo(rotatePayload.TurnClipFrameCount));

            Assert.That(preparedFly.Action, Is.TypeOf<FlyAction>());
            var flyState = (FlyActionSaveState)
                ((ISaveableEntityAction)preparedFly.Action!).CaptureSaveState();
            AssertVector(flyState.TargetWorld, flyPayload.MotionValues, 0);
            AssertVector(flyState.VelocityOrStepWorld, flyPayload.MotionValues, 3);
            AssertVector(flyState.PreviousPositionWorld, flyPayload.MotionValues, 6);
            Assert.That(flyState.NativeHeadingOrPhase,
                Is.EqualTo(flyPayload.MotionValues[9].AsSingle));
            Assert.That(flyState.NativeAngularVelocity,
                Is.EqualTo(flyPayload.MotionValues[10].AsSingle));
            Assert.That(flyState.NativeVerticalAccumulator,
                Is.EqualTo(flyPayload.MotionValues[11].AsSingle));
            Assert.That(flyState.TickCount, Is.EqualTo(flyPayload.TickCount));
            Assert.That(flyState.SwimMode, Is.EqualTo(flyPayload.SwimMode));
            Assert.That(rotateOwner.ActiveAction, Is.Null);
            Assert.That(flyOwner.ActiveAction, Is.Null);
            Assert.That(commandCount, Is.Zero);
        }

        [Test]
        public void ReadsFixtureBackedAttackPayload()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial2.sav");
            Assert.That(document.Actions, Is.Not.Null);
            OriginalSaveAttackActionPayload[] attacks = document.Actions!.Records
                .Select(record => record.Payload)
                .OfType<OriginalSaveAttackActionPayload>()
                .ToArray();

            Assert.That(attacks, Has.Length.EqualTo(4));
            foreach (OriginalSaveAttackActionPayload attack in attacks)
            {
                Assert.That(attack.LeadingRawValues, Has.Count.EqualTo(5));
                Assert.That(attack.DeadlineFlags, Has.Count.EqualTo(3));
                Assert.That(attack.FrameFlags, Has.Count.EqualTo(4));
                Assert.That(attack.ImpactAndTrailingRawValues, Has.Count.EqualTo(5));
                Assert.That(double.IsNaN(attack.AbsoluteDeadline), Is.False);
                AssertNativeString(attack.ChainedActionVerb);
                AssertNativeString(attack.ChainedActionArguments);
            }
        }

        [Test]
        public void CompatibilityOnlyNativeActionsRoundTripWithoutConstructionSideEffects()
        {
            var interpreter = new TclInterpreter();
            int commandCount = 0;
            interpreter.CommandTrace = (_, _, _) => commandCount++;
            var context = new DigglesTclContext(interpreter);
            var owner = new DigglesTclEntity { Class = new DigglesClass { Name = "Test" } };
            context.RegisterEntityAtHandle(owner, 77);
            var factory = new RuntimeSaveableActionFactory();

            var crawlSource = new NativeCrawlActionSaveState(2, unchecked((int)0xDEADBEEF));
            IEntityAction crawl = factory.Create(crawlSource, owner, context);
            Assert.That(crawl, Is.TypeOf<RestoredNativeCrawlAction>());
            Assert.That(crawl.DrivesTransform, Is.False);
            Assert.That(crawl.Tick(0.05d), Is.True);
            Assert.That(crawl.Tick(0.05d), Is.True);
            Assert.That(crawl.Tick(0.1d), Is.False);
            var crawlCaptured = (NativeCrawlActionSaveState)
                ((ISaveableEntityAction)crawl).CaptureSaveState();
            Assert.That(crawlCaptured.RemainingTicks, Is.Zero);
            Assert.That(crawlCaptured.RandomYawOrPhaseBits,
                Is.EqualTo(crawlSource.RandomYawOrPhaseBits));

            var ballisticSource = new NativeAttackBallisticActionSaveState(
                78,
                3,
                new[] { 10, 20 },
                new[] { 30, 40 },
                new[] { 0x1234 },
                new[] { 50, 60 },
                1,
                new[] { 70, 80, 90 },
                2,
                3,
                100,
                0.25d);
            IEntityAction ballistic = factory.Create(ballisticSource, owner, context);
            Assert.That(ballistic, Is.TypeOf<RestoredNativeAttackBallisticAction>());
            Assert.That(ballistic.DrivesTransform, Is.False);
            Assert.That(ballistic.Tick(0.3d), Is.False);
            var ballisticCaptured = (NativeAttackBallisticActionSaveState)
                ((ISaveableEntityAction)ballistic).CaptureSaveState();
            Assert.That(ballisticCaptured.TargetObjectId, Is.EqualTo(78));
            Assert.That(ballisticCaptured.CopyScheduleValues(), Is.EqualTo(new[] { 10, 20 }));
            Assert.That(ballisticCaptured.CopyFirstRawBits(), Is.EqualTo(new[] { 30, 40 }));
            Assert.That(ballisticCaptured.CopyTransientHandles(), Is.EqualTo(new[] { 0x1234 }));
            Assert.That(ballisticCaptured.CopySecondRawBits(), Is.EqualTo(new[] { 50, 60 }));
            Assert.That(ballisticCaptured.CopyLastTargetBits(), Is.EqualTo(new[] { 70, 80, 90 }));
            Assert.That(ballisticCaptured.RemainingSeconds, Is.Zero);

            var loreSource = new NativeLoreActionSaveState(
                101,
                102,
                new[] { new NativeLoreTargetSaveState(78, 103) },
                new[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            IEntityAction lore = factory.Create(loreSource, owner, context);
            Assert.That(lore, Is.TypeOf<RestoredNativeLoreAction>());
            Assert.That(lore.DrivesTransform, Is.False);
            Assert.That(lore.Tick(100d), Is.True);
            var loreCaptured = (NativeLoreActionSaveState)
                ((ISaveableEntityAction)lore).CaptureSaveState();
            Assert.That(loreCaptured.GravityBits, Is.EqualTo(101));
            Assert.That(loreCaptured.FrictionBits, Is.EqualTo(102));
            Assert.That(loreCaptured.CopyTargets().Single().ObjectId, Is.EqualTo(78));
            Assert.That(loreCaptured.CopyTargets().Single().OffsetOrDistanceBits, Is.EqualTo(103));
            Assert.That(loreCaptured.CopyWaypointBits(),
                Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(owner.ActiveAction, Is.Null);
            Assert.That(commandCount, Is.Zero);
        }

        [Test]
        public void ReadsEveryAvailableCurrentSaveIntoTypedActionDocuments()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                Assert.That(document.Actions, Is.Not.Null, path);
                Assert.That(
                    document.Actions!.Records.Count,
                    Is.EqualTo(document.Metadata.Logic!.ActionCount),
                    path);

                foreach (OriginalSaveActionRecord record in document.Actions.Records)
                {
                    if (record.Payload.Common != null)
                    {
                        Assert.That(
                            record.Payload.Common.OwnerObjectId,
                            Is.EqualTo(record.OwnerObjectId),
                            path);
                    }
                }
            }
        }

        [Test]
        public void RejectsActionWhoseCommonOwnerDiffersFromTableOwner()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            OriginalSaveActionRecord waitRecord = baseline.Actions!.Records
                .Single(record => record.Type == OriginalSaveActionType.Wait);
            var wait = (OriginalSaveWaitActionPayload)waitRecord.Payload;
            byte[] payload = baseline.CopyPayload();
            int commonOwnerOffset = checked(
                waitRecord.PayloadOffset + 4 + wait.Common!.Argument.DeclaredLength);
            WriteInt32(payload, commonOwnerOffset, waitRecord.OwnerObjectId + 1);

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
                gzip.Write(payload, 0, payload.Length);
            compressed.Position = 0;

            var codec = new OriginalSaveArchiveCodec();
            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => codec.Read(compressed))!;
            Assert.That(exception.Message, Does.Contain("does not match common owner"));
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private static DigglesTclEntity CreateCandidateOwner(
            OriginalSaveRuntimeSnapshot staged,
            int objectId)
        {
            OriginalSaveRuntimeEntitySnapshot source = staged.EntitiesById[objectId];
            return new DigglesTclEntity
            {
                Class = new DigglesClass { Name = source.ClassName },
                Position = new UnityEngine.Vector3(
                    source.Position.X,
                    source.Position.Y,
                    source.Position.Z),
                Rotation = new UnityEngine.Vector3(
                    source.Rotation.X,
                    source.Rotation.Y,
                    source.Rotation.Z),
            };
        }

        private static void AssertVector(
            UnityEngine.Vector3 actual,
            IReadOnlyList<OriginalSaveRaw32> values,
            int offset)
        {
            Assert.That(actual.x, Is.EqualTo(values[offset].AsSingle));
            Assert.That(actual.y, Is.EqualTo(values[offset + 1].AsSingle));
            Assert.That(actual.z, Is.EqualTo(values[offset + 2].AsSingle));
        }

        private static Dictionary<OriginalSaveActionType, int> CountTypes(
            IEnumerable<OriginalSaveActionRecord> records)
        {
            return records
                .GroupBy(record => record.Type)
                .ToDictionary(group => group.Key, group => group.Count());
        }

        private static void AssertNativeString(OriginalSaveNativeString value)
        {
            byte[] bytes = value.CopyBytesIncludingTerminator();
            Assert.That(bytes, Has.Length.EqualTo(value.DeclaredLength));
            Assert.That(bytes[^1], Is.Zero);
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = unchecked((byte)value);
            bytes[offset + 1] = unchecked((byte)(value >> 8));
            bytes[offset + 2] = unchecked((byte)(value >> 16));
            bytes[offset + 3] = unchecked((byte)(value >> 24));
        }
    }

    internal static class OriginalSaveActionRecordTestExtensions
    {
        internal static TPayload[] OfTypeByPayload<TPayload>(
            this IEnumerable<OriginalSaveActionRecord> records)
            where TPayload : OriginalSaveActionPayload
        {
            return records.Select(record => record.Payload).OfType<TPayload>().ToArray();
        }
    }
}
