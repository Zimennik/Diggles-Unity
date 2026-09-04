#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveRuntimeSessionStagerTests
    {
        [Test]
        public void TutorialSaveStagesAnInactiveExactIdWorldWithoutObjectTcl()
        {
            string savePath = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                "save_tutorial.sav");
            Assume.That(File.Exists(savePath), $"Original save fixture was not found at '{savePath}'.");

            OriginalSaveArchiveDocument document = new OriginalSaveArchiveCodec().Read(savePath);
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));

            var stableRoot = new GameObject("OriginalSaveRuntimeSessionStagerTest");
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
                initial.Engine.Sim.GameTime = 123.0;
                var host = new RuntimeWorldSessionHost(
                    engineFactory,
                    new ActiveGameSession(initial),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");

                candidate.Engine.LoadRuntimeDefinitions();
                var spawner = new RecordingSpawner();
                candidate.Engine.Ctx.EntitySpawner = spawner;
                int tracedCommands = 0;
                bool unexpectedAiCall = false;
                bool objectInitializationObserved = false;
                candidate.Engine.Interp.CommandTrace = (command, args, _) =>
                {
                    // The shell-construction window (obj_init per restored entity — the native
                    // creation-callback law) is lawful object Tcl: its `call`s pull the class
                    // behavior files. Outside it, the inert-staging law still holds.
                    if (candidate.Engine.Ctx.RestoredShellConstructionActive) return;
                    tracedCommands++;
                    if (command == "call" && args.Length > 1 &&
                        args[1] != OriginalSaveAiDocument.NativeStandardScriptPath &&
                        args[1] != "data/scripts/misc/techtreetunes.tcl")
                    {
                        unexpectedAiCall = true;
                    }
                    if (command == "obj_init") objectInitializationObserved = true;
                };

                var saveableActionFactory = new RuntimeSaveableActionFactory();
                var actionFactory = new OriginalSaveActionFactory(saveableActionFactory);
                var actionStager = new OriginalSaveActionCandidateStager(actionFactory);
                var sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    actionStager,
                    new OriginalSaveActionInstaller());

                OriginalSaveRuntimeSessionState staged = sessionStager.StageCandidate(
                    candidate,
                    snapshot,
                    "BlockDB2.BDB");

                Assert.That(candidate.State, Is.EqualTo(RuntimeGameSessionState.Candidate));
                Assert.That(candidate.ContentRoot.activeSelf, Is.False);
                Assert.That(candidate.OriginalSaveState, Is.SameAs(staged));
                Assert.That(staged.IsActivationReady, Is.False);
                Assert.That(staged.ActivationBlockers, Is.Not.Empty);
                Assert.That(tracedCommands, Is.GreaterThan(0),
                    "trusted native AI policy initialization should have run in the candidate");
                Assert.That(unexpectedAiCall, Is.False,
                    "saved AI state must not choose an arbitrary script path");
                Assert.That(objectInitializationObserved, Is.False,
                    "entity reconstruction must not evaluate obj_init Tcl");

                DigglesTclContext context = candidate.Engine.Ctx;
                Assert.That(context.Entities.Count, Is.EqualTo(snapshot.Entities.Count));
                Assert.That(staged.Entities.Keys, Is.EqualTo(snapshot.Entities.Select(entity => entity.ObjectId)));
                Assert.That(spawner.SpawnCount, Is.EqualTo(snapshot.Entities.Count));
                Assert.That(context.Terrain, Is.SameAs(staged.Terrain));
                Assert.That(context.NavService, Is.SameAs(staged.Navigation));
                Assert.That(context.MapBuilder, Is.SameAs(candidate.Engine.Map));
                Assert.That(candidate.Engine.Map.LiveMode, Is.True);
                Assert.That(staged.WorldResult.Chunks, Is.Not.Null);
                Assert.That(staged.WorldResult.Chunks!.Chunks, Is.Not.Empty);
                Assert.That(staged.Terrain.Nav, Is.Not.Null);
                var stagedNav = staged.Terrain.Nav!;
                Assert.That(stagedNav.Version, Is.GreaterThanOrEqualTo(1));
                // Save-load oracle: the archive persists tile+0x06 at record +4 and tile+0x12
                // at +13. The load validator may rebuild +0x3c, but Calc1 must see these saved
                // graph bytes verbatim afterward (FUN_00592f90; FUN_005931f0:246-249).
                DiggableTerrain.SaveState archivedTerrain =
                    snapshot.RuntimeWorld.CreateTerrainState(staged.WorldResult.Voxels);
                for (int tile = 0; tile < archivedTerrain.NavigationState.Length; tile++)
                {
                    if (stagedNav.BaseCost[tile] !=
                            unchecked((sbyte)archivedTerrain.NavigationState[tile]) ||
                        stagedNav.ExitPairAndFlags[tile] != archivedTerrain.FloorAndExitState[tile])
                    {
                        Assert.Fail($"Saved navigation graph diverged at tile {tile}.");
                    }
                }
                DiggableTerrain.SaveState recapturedTerrain = staged.Terrain.CaptureSaveState();
                CollectionAssert.AreEqual(
                    archivedTerrain.NavigationState,
                    recapturedTerrain.NavigationState);
                CollectionAssert.AreEqual(
                    archivedTerrain.FloorAndExitState,
                    recapturedTerrain.FloorAndExitState);
                Assert.That(context.LightBeginRow, Is.EqualTo(snapshot.RuntimeWorld.LightBegin));
                Assert.That(staged.Lighting.BaseLight.LightBegin, Is.EqualTo(snapshot.RuntimeWorld.LightBegin));
                Assert.That(staged.Lighting.TunnelTorches.LightBegin, Is.EqualTo(snapshot.RuntimeWorld.LightBegin));
                Assert.That(candidate.Engine.Sim.GameTime, Is.EqualTo(snapshot.GameTime).Within(1e-9));

                CollectionAssert.AreEqual(
                    snapshot.RuntimeWorld.CreateFogState().Fog,
                    staged.Fog.CaptureSaveState().Fog);
                Assert.That(context.Fluids.Capture().Cells.Length,
                    Is.EqualTo(snapshot.RuntimeWorld.CreateFluidState().Cells.Length));
                Assert.That(staged.FluidCompatibility,
                    Is.SameAs(snapshot.RuntimeWorld.FluidCompatibility));
                AssertCaveRecords(snapshot.CaveRecords, staged.Terrain.CaveRecords);

                int activeActionCount = context.Entities.Values.Count(entity => entity.ActiveAction != null);
                Assert.That(activeActionCount,
                    Is.EqualTo(snapshot.Actions.Count(action => action.HasActiveAction)));
                AssertTriggerState(snapshot.Triggers, context.TriggerService!.CaptureRecords());
                AssertSelectionControlGroupState(snapshot.SelectionControlGroups, context);
                AssertSparetimeState(snapshot.Sparetime, context);
                AssertHorizonState(snapshot.Horizon, context);
                AssertAdaptiveSoundMarkerState(snapshot.AdaptiveSoundMarkers, context);
                AssertNewsTickerState(snapshot.NewsTicker, context.NewsTicker.Capture());
                AssertEnergyState(snapshot.Energy, context.Energy.Capture());
                AssertParticleState(snapshot, context.Particles!);
                AssertTimerState(snapshot, context);
                AssertStoryState(snapshot.Story, context);
                AssertAiState(snapshot.Ai, context.Ai!.CaptureSaveState(), snapshot.GameTime);
                AssertElfState(snapshot.Elf, context.Elf!.CaptureNativeSaveState());
                AssertPlayerState(snapshot.Players, context);

                CollectionAssert.AreEqual(new[] { 50, 1787 }, staged.Entities[51].Inventory);
                CollectionAssert.AreEqual(new[] { 295 }, staged.Entities[158].Inventory);
                Assert.That(staged.Entities[50].ContainedIn, Is.EqualTo(51));
                Assert.That(staged.Entities[1787].ContainedIn, Is.EqualTo(51));
                Assert.That(staged.Entities[295].ContainedIn, Is.EqualTo(158));
                Assert.That(staged.Entities[50].VarsScope.Vars["_visibility"], Is.EqualTo("0"));
                Assert.That(staged.Entities[50].ProcessActions,
                    Is.EqualTo((snapshot.EntitiesById[50].ObjectFlags & 0x01000000) != 0));
                Assert.That(staged.Entities[50].BuildupComplete,
                    Is.EqualTo((snapshot.EntitiesById[50].ObjectFlags & 0x00400000) != 0));
                Assert.That(staged.Entities[50].InStore,
                    Is.EqualTo((snapshot.EntitiesById[50].ObjectFlags & 0x04000000) != 0));
                Assert.That(staged.Entities[1755].LinkedToHandle, Is.EqualTo(793));
                Assert.That(staged.Entities[1755].LinkedSlot, Is.EqualTo(4));

                int maximumId = snapshot.Entities[^1].ObjectId;
                DigglesClass extraClass = context.Classes[snapshot.Entities[0].ClassName];
                var extra = new DigglesTclEntity { Class = extraClass };
                Assert.That(context.RegisterEntity(extra), Is.EqualTo(maximumId + 1));

                candidate.ContentRoot.SetActive(true);
                new OriginalSaveRuntimePresentationParticipant().BindSession(candidate);
                Assert.That(candidate.ContentRoot.GetComponent<DigMarkOverlay>(), Is.Not.Null);
                Assert.That(candidate.ContentRoot.GetComponent<FluidPresenter>(), Is.Not.Null);
                Assert.That(candidate.ContentRoot.GetComponent<FogOfWarPresenter>(), Is.Not.Null);
                Assert.That(candidate.ContentRoot.GetComponent<FogOfWarOverlay>(), Is.Not.Null);
                Assert.That(candidate.ContentRoot.GetComponent<BaseLightPresenter>(), Is.Not.Null);
                Assert.That(objectInitializationObserved, Is.False,
                    "commit-time presentation binding must not evaluate obj_init Tcl");
                candidate.ContentRoot.SetActive(false);

                Assert.That(initial.Engine.Sim.GameTime, Is.EqualTo(123.0));
                Assert.That(initial.ContentRoot.activeSelf, Is.True);
                Assert.That(initial.Engine.Ctx.Terrain, Is.Null);
            }
            finally
            {
                candidate?.Dispose();
                initial?.Dispose();
                if (stableRoot != null) Object.DestroyImmediate(stableRoot);
            }
        }

        private static void AssertEnergyState(
            OriginalSaveRuntimeEnergySnapshot expected,
            EnergySystem.SaveState actual)
        {
            Assert.That(actual.Sources.Length, Is.EqualTo(expected.Sources.Count));
            for (int sourceIndex = 0; sourceIndex < actual.Sources.Length; sourceIndex++)
            {
                OriginalSaveRuntimeEnergySourceSnapshot saved = expected.Sources[sourceIndex];
                EnergySystem.SourceSaveState restored = actual.Sources[sourceIndex];
                Assert.That(restored.SourceHandle, Is.EqualTo(saved.SourceObjectId));
                Assert.That(restored.EnergyClass, Is.EqualTo(saved.EnergyClass));
                Assert.That(System.BitConverter.SingleToInt32Bits(restored.Range),
                    Is.EqualTo(System.BitConverter.SingleToInt32Bits(saved.Range)));
                Assert.That(restored.Leases.Length, Is.EqualTo(saved.Leases.Count));
                for (int leaseIndex = 0; leaseIndex < restored.Leases.Length; leaseIndex++)
                {
                    OriginalSaveEnergyLeaseRecord savedLease = saved.Leases[leaseIndex];
                    EnergySystem.LeaseSaveState restoredLease = restored.Leases[leaseIndex];
                    Assert.That(restoredLease.ConsumerHandle,
                        Is.EqualTo(savedLease.ConsumerObjectId));
                    Assert.That(System.BitConverter.SingleToInt32Bits(restoredLease.Amount),
                        Is.EqualTo(savedLease.Amount.Bits));
                    Assert.That(restoredLease.TicksLeft,
                        Is.EqualTo(savedLease.RemainingTicks));
                }
            }
        }

        private static void AssertParticleState(
            OriginalSaveRuntimeSnapshot snapshot,
            ParticleSystemState actual)
        {
            Dictionary<int, OriginalSaveRuntimeParticle> recordsByHandle =
                snapshot.Particles.Records.ToDictionary(record => record.SlotIndex);
            var attachedHandles = new HashSet<int>();
            int projectedAttachmentCount = 0;

            foreach (OriginalSaveRuntimeEntitySnapshot entity in snapshot.Entities)
            {
                foreach (OriginalSaveObjectParticleSourceRecord attachment in
                         entity.ParticleState.Sources)
                {
                    if (!attachment.HasParticleHandle) continue;
                    attachedHandles.Add(attachment.ParticleHandle);
                    if (!recordsByHandle.TryGetValue(
                            attachment.ParticleHandle,
                            out OriginalSaveRuntimeParticle native))
                    {
                        continue;
                    }

                    projectedAttachmentCount++;
                    Assert.That(actual.Sources.TryGetValue(
                        (entity.ObjectId, attachment.SourceIndex),
                        out ParticleSystemState.Source projected), Is.True);
                    AssertProjectedParticle(
                        projected!,
                        native,
                        attachment.RelativePositionX,
                        attachment.RelativePositionY,
                        attachment.RelativePositionZ,
                        NormalizeModelLink(attachment.RawLinkId),
                        false);
                }
            }

            OriginalSaveRuntimeParticle[] unattached = snapshot.Particles.Records
                .Where(record => !attachedHandles.Contains(record.SlotIndex))
                .ToArray();
            OriginalSaveRuntimeParticle[] futureTemporary = unattached
                .Where(record => record.Mode == 1 &&
                                 record.RawValue68.AsSingle > snapshot.GameTime)
                .ToArray();
            HashSet<int> temporaryHandles = futureTemporary
                .Select(record => record.SlotIndex)
                .ToHashSet();
            OriginalSaveRuntimeParticle[] worldAnchored = unattached
                .Where(record => !temporaryHandles.Contains(record.SlotIndex))
                .ToArray();

            Assert.That(actual.Sources.Count,
                Is.EqualTo(projectedAttachmentCount + worldAnchored.Length));
            Assert.That(actual.Temps.Count, Is.EqualTo(futureTemporary.Length));

            foreach (OriginalSaveRuntimeParticle native in worldAnchored)
            {
                Assert.That(actual.Sources.TryGetValue(
                    (ParticleSystemState.NativeWorldSourceObjectId, native.SlotIndex),
                    out ParticleSystemState.Source projected), Is.True);
                AssertProjectedParticle(
                    projected!,
                    native,
                    native.PositionX,
                    native.PositionY,
                    native.PositionZ,
                    -1,
                    true);
            }

            foreach (ParticleSystemState.TempSource projected in actual.Temps)
            {
                Assert.That(temporaryHandles.Contains(projected.NativeParticleHandle), Is.True);
                OriginalSaveRuntimeParticle native = recordsByHandle[projected.NativeParticleHandle];
                Assert.That(projected.Type, Is.EqualTo(native.ParticleKind));
                AssertVectorBits(projected.Pos, native.PositionX, native.PositionY, native.PositionZ);
                AssertVectorBits(
                    projected.Direction,
                    native.DirectionX,
                    native.DirectionY,
                    native.DirectionZ);
                Assert.That(projected.Num, Is.EqualTo(System.Math.Max(0, native.RawValue50)));
                Assert.That(projected.Remaining,
                    Is.EqualTo(System.Math.Max(
                        0.01f,
                        native.RawValue68.AsSingle - (float)snapshot.GameTime)));
            }
        }

        private static void AssertProjectedParticle(
            ParticleSystemState.Source projected,
            OriginalSaveRuntimeParticle native,
            OriginalSaveRaw32 positionX,
            OriginalSaveRaw32 positionY,
            OriginalSaveRaw32 positionZ,
            int expectedLinkId,
            bool worldAnchored)
        {
            Assert.That(projected.Type, Is.EqualTo(native.ParticleKind));
            AssertVectorBits(projected.RelPos, positionX, positionY, positionZ);
            AssertVectorBits(
                projected.Direction,
                native.DirectionX,
                native.DirectionY,
                native.DirectionZ);
            Assert.That(projected.MaxParticles,
                Is.EqualTo(System.Math.Max(0, native.ParticleCount - 1)));
            Assert.That(projected.NumPerFrame,
                Is.EqualTo(System.Math.Max(0, native.RawValue50)));
            Assert.That(projected.VisibleInFog, Is.EqualTo(native.RawFlag64 != 0));
            Assert.That(projected.LinkId, Is.EqualTo(expectedLinkId));
            Assert.That(projected.SubMesh, Is.EqualTo(NormalizeSubMesh(native.RawValue74)));
            Assert.That(projected.Active, Is.EqualTo(native.Mode == 1));
            Assert.That(projected.WorldAnchored, Is.EqualTo(worldAnchored));
            Assert.That(System.BitConverter.SingleToInt32Bits(projected.MaxRangeSquared),
                Is.EqualTo(native.RawValue60.Bits));
            Assert.That(projected.HasNativeState, Is.True);
            Assert.That(projected.NativeParticleHandle, Is.EqualTo(native.SlotIndex));
            Assert.That(projected.NativeMode, Is.EqualTo(native.Mode));
            Assert.That(projected.NativeRawObjectToken, Is.EqualTo(native.LinkedObjectToken));
            Assert.That(projected.NativeRawValue74, Is.EqualTo(native.RawValue74));
            Assert.That(projected.NativeRawValue80, Is.EqualTo(native.RawValue80));
        }

        private static void AssertVectorBits(
            Vector3 actual,
            OriginalSaveRaw32 expectedX,
            OriginalSaveRaw32 expectedY,
            OriginalSaveRaw32 expectedZ)
        {
            Assert.That(System.BitConverter.SingleToInt32Bits(actual.x),
                Is.EqualTo(expectedX.Bits));
            Assert.That(System.BitConverter.SingleToInt32Bits(actual.y),
                Is.EqualTo(expectedY.Bits));
            Assert.That(System.BitConverter.SingleToInt32Bits(actual.z),
                Is.EqualTo(expectedZ.Bits));
        }

        private static int NormalizeModelLink(int rawLinkId) =>
            rawLinkId >= -1 && rawLinkId < 32 ? rawLinkId : -1;

        private static int NormalizeSubMesh(int rawSubMesh) =>
            rawSubMesh >= -1 && rawSubMesh < 256 ? rawSubMesh : -1;

        private static void AssertTimerState(
            OriginalSaveRuntimeSnapshot snapshot,
            DigglesTclContext context)
        {
            TimerRegistry.Entry[] registry = context.Timers.CaptureEntries();
            TimerScheduler.Entry[] scheduler = context.Sim!.Scheduler.CaptureEntries();
            Assert.That(registry.Length, Is.EqualTo(snapshot.Timers.Records.Count));
            Assert.That(scheduler.Length, Is.EqualTo(snapshot.Timers.Records.Count));

            for (int index = 0; index < snapshot.Timers.Records.Count; index++)
            {
                OriginalSaveRuntimeTimer saved = snapshot.Timers.Records[index];
                TimerRegistry.Entry restoredRegistry = registry[index];
                TimerScheduler.Entry restoredScheduler = scheduler[index];
                long serial = index + 1L;
                Assert.That(restoredRegistry.Serial, Is.EqualTo(serial));
                Assert.That(restoredRegistry.Handle, Is.EqualTo(saved.OwnerObjectId));
                Assert.That(restoredRegistry.UserId, Is.EqualTo(saved.UserId));
                Assert.That(restoredRegistry.Event, Is.EqualTo(saved.EventName));
                Assert.That(restoredRegistry.RemainingFires, Is.EqualTo(saved.RepeatsLeft));
                Assert.That(restoredRegistry.Cancelled, Is.False);
                Assert.That(restoredScheduler.Handle, Is.EqualTo(saved.OwnerObjectId));
                Assert.That(restoredScheduler.Event, Is.EqualTo(saved.EventName));
                Assert.That(System.BitConverter.DoubleToInt64Bits(restoredScheduler.NextFire),
                    Is.EqualTo(saved.NextFire.Bits));
                Assert.That(System.BitConverter.DoubleToInt64Bits(restoredScheduler.Interval),
                    Is.EqualTo(saved.Interval.Bits));
                Assert.That(restoredScheduler.RepeatsLeft, Is.EqualTo(saved.RepeatsLeft));
                Dictionary<string, string> payload = restoredScheduler.Payload
                    ?? throw new AssertionException("A restored native timer has no event payload.");
                Assert.That(payload[TimerRegistry.SerialKey], Is.EqualTo(
                    serial.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(payload[TimerRegistry.UserIdKey], Is.EqualTo(
                    saved.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(payload["-pos1"], Is.EqualTo(ExpectedTimerVector(
                    saved.Position1X.AsSingle,
                    saved.Position1Y.AsSingle,
                    saved.Position1Z.AsSingle)));
                Assert.That(payload["-pos2"], Is.EqualTo(ExpectedTimerVector(
                    saved.Position2X.AsSingle,
                    saved.Position2Y.AsSingle,
                    saved.Position2Z.AsSingle)));
                Assert.That(payload["-pos3"], Is.EqualTo(ExpectedTimerVector(
                    saved.Position3X.AsSingle,
                    saved.Position3Y.AsSingle,
                    saved.Position3Z.AsSingle)));
                Assert.That(payload["-num1"], Is.EqualTo(saved.Number1.AsSingle.ToString(
                    "R", System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(payload["-num2"], Is.EqualTo(saved.Number2.AsSingle.ToString(
                    "R", System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(payload["-num3"], Is.EqualTo(saved.Number3.AsSingle.ToString(
                    "R", System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(payload["-text1"], Is.EqualTo(saved.Text1.Text));
                Assert.That(payload["-text2"], Is.EqualTo(saved.Text2.Text));
                AssertTimerSubject(payload, "-subject1", saved.Subject1ObjectId);
                AssertTimerSubject(payload, "-subject2", saved.Subject2ObjectId);
                AssertTimerSubject(payload, "-subject3", saved.Subject3ObjectId);
            }
        }

        private static void AssertTimerSubject(
            IReadOnlyDictionary<string, string> payload,
            string key,
            int objectId)
        {
            if (objectId >= 0)
                Assert.That(payload[key], Is.EqualTo(objectId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
            else
                Assert.That(payload.ContainsKey(key), Is.False);
        }

        private static string ExpectedTimerVector(float x, float y, float z) =>
            x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " " +
            y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " " +
            z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static void AssertTriggerState(
            IReadOnlyList<OriginalSaveRuntimeTriggerSnapshot?> expected,
            TriggerRecord?[] actual)
        {
            Assert.That(actual, Has.Length.EqualTo(expected.Count));
            for (int index = 0; index < actual.Length; index++)
            {
                OriginalSaveRuntimeTriggerSnapshot? saved = expected[index];
                TriggerRecord? restored = actual[index];
                if (saved == null)
                {
                    Assert.That(restored, Is.Null);
                    continue;
                }

                Assert.That(restored, Is.Not.Null);
                Assert.That(restored!.Index, Is.EqualTo(index));
                Assert.That((int)restored.Kind, Is.EqualTo(saved.Kind));
                Assert.That(restored.Execution, Is.EqualTo(saved.Execution));
                Assert.That(restored.OwnerHandle, Is.EqualTo(saved.OwnerObjectId));
                Assert.That(restored.FireScript, Is.EqualTo(saved.FireScript));
                Assert.That(restored.LastCheck, Is.EqualTo(saved.LastCheck));
                Assert.That(restored.CheckTimer, Is.EqualTo(saved.CheckTimer));
                Assert.That(restored.CheckIdle, Is.EqualTo(saved.CheckIdle));
                Assert.That(restored.CheckNear, Is.EqualTo(saved.CheckNear));
                Assert.That(restored.Active, Is.EqualTo(saved.Active));
                Assert.That(restored.TargetRange, Is.EqualTo(saved.TargetRange));
                Assert.That(restored.FireTime, Is.EqualTo(saved.FireTime));
                Assert.That(restored.Interval, Is.EqualTo(saved.Interval));
                Assert.That(restored.TargetRef, Is.EqualTo(saved.TargetObjectId));
                Assert.That(restored.TargetClass, Is.EqualTo(saved.TargetClass));
                if (saved.TargetTypes.Count == 0)
                    Assert.That(restored.TargetTypes, Is.Null);
                else
                    Assert.That(restored.TargetTypes, Is.EqualTo(saved.TargetTypes));
                Assert.That(restored.TargetOwnerSpec,
                    Is.EqualTo(saved.TargetOwnerSpec.Length == 0 ? null : saved.TargetOwnerSpec));
                Assert.That(restored.TargetCount, Is.EqualTo(saved.TargetCount));
                Assert.That(restored.CallbackScript, Is.EqualTo(saved.CallbackScript));
                Assert.That(restored.Actuators, Is.EqualTo(saved.Actuators));
            }
        }

        private static void AssertAiState(
            OriginalSaveRuntimeAiSnapshot expected,
            AiSystem.SaveState actual,
            double gameTime)
        {
            Assert.That(actual.Owners, Has.Length.EqualTo(expected.Owners.Count));
            for (int owner = 0; owner < actual.Owners.Length; owner++)
            {
                OriginalSaveRuntimeAiOwnerSnapshot saved = expected.Owners[owner];
                AiSystem.OwnerSaveState restored = actual.Owners[owner];
                Assert.That(restored.Initialized, Is.EqualTo(saved.InterpreterPresent));
                Assert.That(restored.Enabled, Is.EqualTo(saved.Enabled));
                if (!saved.InterpreterPresent)
                {
                    Assert.That(restored.VariableNames, Is.Empty);
                    Assert.That(restored.TickHandler, Is.Empty);
                    continue;
                }

                var variables = new Dictionary<string, string>();
                for (int index = 0; index < restored.VariableNames.Length; index++)
                    variables.Add(restored.VariableNames[index], restored.VariableValues[index]);
                Assert.That(variables, Has.Count.EqualTo(saved.Variables.Count),
                    $"AI owner {owner} contains globals not represented by the native save");
                foreach (OriginalSaveTclVariable variable in saved.Variables)
                {
                    Assert.That(variables.TryGetValue(variable.Name, out string value), Is.True,
                        $"AI owner {owner} is missing variable '{variable.Name}'");
                    Assert.That(value, Is.EqualTo(variable.Value),
                        $"AI owner {owner} variable '{variable.Name}' differs");
                }
                Assert.That(restored.TickHandler.Trim(), Is.EqualTo("dotickhandler"));
                Assert.That(restored.NextTick, Is.GreaterThanOrEqualTo(gameTime - 1e-9));
                Assert.That(restored.NextTick, Is.LessThan(gameTime + 5.0 + 1e-9));
            }
        }

        private static void AssertStoryState(
            OriginalSaveRuntimeStorySnapshot expected,
            DigglesTclContext actual)
        {
            Assert.That(actual.StoryEventOrder,
                Is.EqualTo(expected.Events.Select(storyEvent => storyEvent.Name)));
            Assert.That(actual.StoryEvents, Has.Count.EqualTo(expected.Events.Count));
            Assert.That(actual.StoryEventTriggers, Has.Count.EqualTo(expected.Events.Count));
            foreach (OriginalSaveRuntimeStoryEventSnapshot storyEvent in expected.Events)
            {
                Assert.That(actual.StoryEvents[storyEvent.Name],
                    Is.EqualTo(storyEvent.Completed ? "1" : "0"));
                Assert.That(actual.StoryEventTriggers[storyEvent.Name],
                    Is.EqualTo((storyEvent.Trigger, storyEvent.Extra)));
            }

            Assert.That(actual.StoryMapW, Is.EqualTo(expected.MapWidth));
            Assert.That(actual.StoryMapH, Is.EqualTo(expected.MapHeight));
            Assert.That(actual.StoryMap, Has.Count.EqualTo(expected.MapValues.Count(value => value != 0)));
            for (int index = 0; index < expected.MapValues.Count; index++)
            {
                int value = expected.MapValues[index];
                int x = index % expected.MapWidth;
                int y = index / expected.MapWidth;
                Assert.That(actual.StoryMap.TryGetValue((x, y), out int restored),
                    Is.EqualTo(value != 0));
                if (value != 0) Assert.That(restored, Is.EqualTo(value));
            }

            Assert.That(actual.StoryZones, Has.Count.EqualTo(expected.Zones.Count));
            for (int index = 0; index < expected.Zones.Count; index++)
            {
                Assert.That(actual.StoryZones[index].Name, Is.EqualTo(expected.Zones[index].Name));
                Assert.That(actual.StoryZones[index].Bound,
                    Is.EqualTo(expected.Zones[index].UpperBound));
            }
            Assert.That(actual.StoryZoneFloor, Is.EqualTo(expected.ZoneFloor));

            Assert.That(actual.StoryTempGroups, Has.Count.EqualTo(expected.Groups.Count));
            foreach (OriginalSaveRuntimeStoryGroupSnapshot group in expected.Groups)
                Assert.That(actual.StoryTempGroups[group.Name], Is.EqualTo(group.BuildPortSpec()));

            Assert.That(actual.StoryTemps, Has.Count.EqualTo(expected.Temps.Count));
            for (int index = 0; index < expected.Temps.Count; index++)
            {
                OriginalSaveRuntimeStoryTempSnapshot saved = expected.Temps[index];
                DigglesTclContext.StoryTemp restored = actual.StoryTemps[index];
                OriginalSaveRuntimeStoryGroupSnapshot? group = saved.TemplateGroupIndex >= 0
                    ? expected.Groups[saved.TemplateGroupIndex]
                    : null;
                Assert.That(restored.Name, Is.EqualTo(NormalizeStoryTemplateName(saved.Name)));
                Assert.That(restored.Lo, Is.EqualTo(saved.LowerDepth));
                Assert.That(restored.Hi, Is.EqualTo(saved.UpperDepth));
                Assert.That(restored.Zone, Is.EqualTo(saved.ZoneIndex));
                Assert.That(restored.Exclusive, Is.EqualTo(saved.Exclusive));
                Assert.That(restored.ZoneBorder, Is.EqualTo(saved.ZoneBorderIndex >= 0
                    ? expected.Zones[saved.ZoneBorderIndex].Name
                    : ""));
                Assert.That(restored.StoneIdx, Is.EqualTo(saved.StoneIndex));
                Assert.That(restored.MinDistRef,
                    Is.EqualTo(saved.MinimumDistanceReferenceIndex >= 0
                        ? NormalizeStoryTemplateName(
                            expected.Temps[saved.MinimumDistanceReferenceIndex].Name)
                        : ""));
                Assert.That(restored.MinDistVal, Is.EqualTo(saved.MinimumDistance));
                Assert.That(restored.MidDist, Is.EqualTo(saved.MiddleDistance));
                Assert.That(restored.NonRelevant, Is.EqualTo(saved.NonRelevantProbability));
                int digConnectGroupEntry = group != null ? group.HeaderValues[2] : 0;
                Assert.That(restored.DigConnect,
                    Is.EqualTo(digConnectGroupEntry + " " + saved.DigConnectValue));
                Assert.That(restored.Connect,
                    Is.EqualTo(group?.BuildPortConnectSpec() ?? ""));
                Assert.That(restored.CheckAlign, Is.EqualTo(saved.CheckAlign ? 1 : 0));
                Assert.That(restored.Placed, Is.EqualTo(saved.Placed));
                Assert.That(restored.PlacedX, Is.EqualTo(saved.PlacedX));
                Assert.That(restored.PlacedY, Is.EqualTo(saved.PlacedY));
                Assert.That(restored.EventNames,
                    Is.EqualTo(saved.EventIndices.Select(eventIndex => expected.Events[eventIndex].Name)));
                Assert.That(restored.SeenChildren, Is.EquivalentTo(
                    group?.Entries.Where(entry => entry.Completed)
                        .Select(entry => NormalizeStoryTemplateName(entry.Name)) ??
                    Enumerable.Empty<string>()));
            }
            Assert.That(actual.StoryPlacedTemps, Has.Count.EqualTo(expected.Temps.Count(temp => temp.Placed)));
            int placedIndex = 0;
            foreach (OriginalSaveRuntimeStoryTempSnapshot temp in expected.Temps.Where(temp => temp.Placed))
            {
                Assert.That(actual.StoryPlacedTemps[placedIndex++], Is.EqualTo((
                    NormalizeStoryTemplateName(temp.Name),
                    temp.PlacedX,
                    temp.PlacedY)));
            }

            Assert.That(actual.StoryCallbacks, Has.Count.EqualTo(expected.CallbackObjectId >= 0 ? 1 : 0));
            if (expected.CallbackObjectId >= 0)
                Assert.That(actual.StoryCallbacks[expected.CallbackObjectId],
                    Is.EqualTo(expected.CallbackScript));
            Assert.That(actual.StoryRectX, Is.EqualTo(expected.RectX));
            Assert.That(actual.StoryRectY, Is.EqualTo(expected.RectY));
            Assert.That(actual.StoryRectW, Is.EqualTo(expected.RectWidth));
            Assert.That(actual.StoryRectH, Is.EqualTo(expected.RectHeight));
            Assert.That(actual.StoryRawRectX, Is.EqualTo(expected.RawRectX));
            Assert.That(actual.StoryRawRectY, Is.EqualTo(expected.RawRectY));
            Assert.That(actual.StoryRawRectW, Is.EqualTo(expected.RawRectWidth));
            Assert.That(actual.StoryRawRectH, Is.EqualTo(expected.RawRectHeight));
            Assert.That(actual.StoryScanX, Is.EqualTo(expected.ScanX));
            Assert.That(actual.StoryScanY, Is.EqualTo(expected.ScanY));
            Assert.That(actual.StoryUtemp,
                Is.EqualTo(NormalizeStoryTemplateName(expected.SelectedTempName)));
            Assert.That(actual.StoryUtempPlaced, Is.False);
            Assert.That(actual.StoryLatchDir, Is.EqualTo(expected.LatchDirection));
            Assert.That(actual.StoryLatchX, Is.EqualTo(expected.LatchX));
            Assert.That(actual.StoryLatchY, Is.EqualTo(expected.LatchY));
            Assert.That(actual.StoryLatchConsumed, Is.EqualTo(expected.LatchConsumed));
            Assert.That(actual.StoryDigWeight, Is.EqualTo(expected.DigWeight));
            Assert.That(actual.StoryDigMessage, Is.EqualTo(expected.DigMessageEnabled));
            Assert.That(actual.StoryDigThreshold, Is.EqualTo(expected.DigThreshold));
        }

        private static void AssertSelectionControlGroupState(
            OriginalSaveRuntimeSelectionControlGroupSnapshot expected,
            DigglesTclContext actual)
        {
            Assert.That(actual.SelectionControlGroups, Has.Length.EqualTo(expected.Groups.Count));
            for (int group = 0; group < actual.SelectionControlGroups.Length; group++)
                Assert.That(actual.SelectionControlGroups[group], Is.EqualTo(expected.Groups[group]));
        }

        private static void AssertSparetimeState(
            OriginalSaveRuntimeSparetimeSnapshot expected,
            DigglesTclContext actual)
        {
            Assert.That(actual.SpareRegistry, Has.Count.EqualTo(expected.Registrations.Count));
            for (int index = 0; index < actual.SpareRegistry.Count; index++)
            {
                SparetimeRegistration restored = actual.SpareRegistry[index];
                OriginalSaveRuntimeSparetimeRegistration saved = expected.Registrations[index];
                Assert.That((int)restored.Category, Is.EqualTo(saved.Category));
                Assert.That(restored.ObjectId, Is.EqualTo(saved.ObjectId));
                if (actual.Entities.TryGetValue(restored.ObjectId, out DigglesTclEntity? entity))
                {
                    Assert.That(entity.SpareKind,
                        Is.EqualTo(SparetimeCategoryCodec.GetName(restored.Category)));
                }
            }
        }

        private static void AssertHorizonState(
            OriginalSaveRuntimeHorizonSnapshot expected,
            DigglesTclContext actual)
        {
            Assert.That(actual.HorizonRegions, Has.Count.EqualTo(expected.Regions.Count));
            for (int index = 0; index < actual.HorizonRegions.Count; index++)
            {
                HorizonRegionState restored = actual.HorizonRegions[index];
                OriginalSaveRuntimeHorizonRegion saved = expected.Regions[index];
                Assert.That(restored.Position.x, Is.EqualTo(saved.PositionX));
                Assert.That(restored.Position.y, Is.EqualTo(saved.PositionY));
                Assert.That(restored.Position.z, Is.EqualTo(saved.PositionZ));
                Assert.That(restored.Red, Is.EqualTo(saved.Red));
                Assert.That(restored.Green, Is.EqualTo(saved.Green));
                Assert.That(restored.Blue, Is.EqualTo(saved.Blue));
                Assert.That(restored.Depth, Is.EqualTo(saved.Depth));
                Assert.That(restored.Size, Is.EqualTo(saved.Size));
            }
        }

        private static void AssertAdaptiveSoundMarkerState(
            OriginalSaveRuntimeAdaptiveSoundMarkerSnapshot expected,
            DigglesTclContext actual)
        {
            Assert.That(actual.Audio, Is.Not.Null);
            Assert.That(actual.Audio!.MusicMarkers, Has.Count.EqualTo(expected.Markers.Count));
            for (int index = 0; index < actual.Audio.MusicMarkers.Count; index++)
            {
                AudioState.MusicMarker restored = actual.Audio.MusicMarkers[index];
                OriginalSaveRuntimeAdaptiveSoundMarker saved = expected.Markers[index];
                Assert.That(restored.ObjectId, Is.EqualTo(saved.ObjectId));
                Assert.That(restored.Pos.x, Is.EqualTo(saved.PositionX));
                Assert.That(restored.Pos.y, Is.EqualTo(saved.PositionY));
                Assert.That(restored.Pos.z, Is.EqualTo(saved.PositionZ));
                Assert.That(restored.Theme, Is.EqualTo(saved.Theme));
                Assert.That(restored.Radius, Is.EqualTo(saved.Range));
            }
        }

        private static void AssertNewsTickerState(
            OriginalSaveRuntimeNewsTickerSnapshot expected,
            NewsTickerSystem.SaveState actual)
        {
            NewsTickerSystem.SaveState saved = expected.CreateSaveState();
            Assert.That(actual.NextId, Is.EqualTo(saved.NextId));
            Assert.That(actual.EnabledMask, Is.EqualTo(saved.EnabledMask));
            AssertNewsTickerEntries(saved.Active, actual.Active);
            AssertNewsTickerEntries(saved.History, actual.History);
            Assert.That(actual.DisplayQueue, Has.Length.EqualTo(saved.DisplayQueue.Length));
            for (int index = 0; index < actual.DisplayQueue.Length; index++)
            {
                Assert.That(actual.DisplayQueue[index].Id,
                    Is.EqualTo(saved.DisplayQueue[index].Id));
                Assert.That(actual.DisplayQueue[index].Position,
                    Is.EqualTo(saved.DisplayQueue[index].Position));
            }
        }

        private static void AssertCaveRecords(
            OriginalSaveRuntimeCaveRecordSnapshot expected,
            IReadOnlyList<DiggableTerrain.CaveRecord> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Records.Count));
            for (int index = 0; index < actual.Count; index++)
            {
                Assert.That(actual[index].X0, Is.EqualTo(expected.Records[index].X0));
                Assert.That(actual[index].Y0, Is.EqualTo(expected.Records[index].Y0));
                Assert.That(actual[index].X1, Is.EqualTo(expected.Records[index].X1));
                Assert.That(actual[index].Y1, Is.EqualTo(expected.Records[index].Y1));
                Assert.That(actual[index].TemplateIdx,
                    Is.EqualTo(expected.Records[index].TemplateIndex));
            }
        }

        private static void AssertNewsTickerEntries(
            IReadOnlyList<NewsTickerSystem.EntrySaveState> expected,
            IReadOnlyList<NewsTickerSystem.EntrySaveState> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            for (int index = 0; index < actual.Count; index++)
            {
                Assert.That(actual[index].Id, Is.EqualTo(expected[index].Id));
                Assert.That(actual[index].Text, Is.EqualTo(expected[index].Text));
                Assert.That(actual[index].Category, Is.EqualTo(expected[index].Category));
                Assert.That(actual[index].Red, Is.EqualTo(expected[index].Red));
                Assert.That(actual[index].Green, Is.EqualTo(expected[index].Green));
                Assert.That(actual[index].Blue, Is.EqualTo(expected[index].Blue));
                Assert.That(actual[index].Priority, Is.EqualTo(expected[index].Priority));
                Assert.That(actual[index].RemainingTime,
                    Is.EqualTo(expected[index].RemainingTime));
                Assert.That(actual[index].ClickScript,
                    Is.EqualTo(expected[index].ClickScript));
                Assert.That(actual[index].AppearsInHistory,
                    Is.EqualTo(expected[index].AppearsInHistory));
            }
        }

        private static string NormalizeStoryTemplateName(string value)
        {
            return value.EndsWith(".tcl", System.StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - 4)
                : value;
        }

        private static void AssertElfState(
            OriginalSaveRuntimeElfSnapshot expected,
            ElfState.NativeSaveState actual)
        {
            Assert.That(actual.TextureVariant, Is.EqualTo(expected.TextureVariant));
            Assert.That(actual.CurrentRegisteredAnimation,
                Is.EqualTo(expected.CurrentRegisteredAnimation));
            Assert.That(actual.RequestedRegisteredAnimation,
                Is.EqualTo(expected.RequestedRegisteredAnimation));
            Assert.That(actual.NativeModelDatabaseId,
                Is.EqualTo(expected.NativeModelDatabaseId));
            Assert.That(actual.NativeMeshId, Is.EqualTo(expected.NativeMeshId));
            Assert.That(actual.FrameAlpha, Is.EqualTo(expected.FrameAlpha));
            Assert.That(actual.LookAtObjectId, Is.EqualTo(expected.LookAtObjectId));
            Assert.That(actual.Position.x, Is.EqualTo(expected.Position.X));
            Assert.That(actual.Position.y, Is.EqualTo(expected.Position.Y));
            Assert.That(actual.Position.z, Is.EqualTo(expected.Position.Z));
            Assert.That(actual.LeanA, Is.EqualTo(expected.LeanA));
            Assert.That(actual.LeanB, Is.EqualTo(expected.LeanB));
            Assert.That(actual.NativeState64Bits, Is.EqualTo(expected.NativeState64Bits));
            Assert.That(actual.Target.x, Is.EqualTo(expected.Target.X));
            Assert.That(actual.Target.y, Is.EqualTo(expected.Target.Y));
            Assert.That(actual.Target.z, Is.EqualTo(expected.Target.Z));
            Assert.That(actual.Mode, Is.EqualTo(expected.Mode));
            Assert.That(actual.DrawOverUi, Is.EqualTo(expected.DrawOverUi));
            Assert.That(actual.AnimationHasMoreFrames,
                Is.EqualTo(expected.AnimationHasMoreFrames));
            Assert.That(actual.FirstParticleSourceHandle,
                Is.EqualTo(expected.FirstParticleSourceHandle));
            Assert.That(actual.SecondParticleSourceHandle,
                Is.EqualTo(expected.SecondParticleSourceHandle));
            Assert.That(actual.NativeMeshId >= 0, Is.EqualTo(expected.InferredVisible));
            if (actual.NativeMeshId >= 0)
            {
                Assert.That(actual.ResolvedModelAnimation, Is.Not.Empty);
                Assert.That(actual.ResolvedModelAnimationFrame, Is.GreaterThanOrEqualTo(0));
            }
        }

        private static void AssertPlayerState(
            OriginalSaveRuntimePlayerSnapshot expected,
            DigglesTclContext actual)
        {
            Assert.That(actual.NativePlayerReservedFraming,
                Is.EqualTo(expected.ReservedFraming));
            Assert.That(actual.PlayerSlots, Has.Length.EqualTo(expected.Slots.Count));
            for (int owner = 0; owner < actual.PlayerSlots.Length; owner++)
            {
                OriginalSaveRuntimePlayerSlotSnapshot saved = expected.Slots[owner];
                DigglesTclContext.PlayerSlotState restored = actual.PlayerSlots[owner];
                Assert.That(restored.PlayerName, Is.EqualTo(saved.PlayerName));
                Assert.That(restored.SlotState, Is.EqualTo(saved.SlotState));
                Assert.That(restored.PlayerOwnerId, Is.EqualTo(saved.PlayerOwnerId));
                Assert.That(restored.ClanOwnerId, Is.EqualTo(saved.ClanOwnerId));
                Assert.That(restored.SecondaryName, Is.EqualTo(saved.SecondaryName));
                for (int target = 0; target < actual.PlayerSlots.Length; target++)
                {
                    Assert.That(actual.Diplomacy[owner, target],
                        Is.EqualTo(saved.Diplomacy[target]));
                }
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }

        private sealed class RecordingSpawner : IDigglesEntitySpawner
        {
            public int SpawnCount { get; private set; }

            public GameObject? Spawn(DigglesTclEntity ent, Transform parent)
            {
                SpawnCount++;
                return null;
            }

            public void Destroy(GameObject go) => Object.DestroyImmediate(go);
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
