#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine.Scripting;

    public interface IOriginalSaveLoadPlanner
    {
        OriginalSaveLoadPlan Prepare(OriginalSaveArchiveDocument document);
    }

    // Transactional preflight. It resolves the identity domain and rejects unsafe cross-references
    // before any live Unity or Tcl object is created, destroyed, or mutated.
    public sealed class OriginalSaveLoadPlanner : IOriginalSaveLoadPlanner
    {
        private const string DataRootEnvironmentVariable = "DIGGLES_DATA_ROOT";
        private const int NativeEventRegistryCapacity = 128;

        private static readonly Lazy<OriginalSaveEventNameRegistry> EventNameRegistry =
            new(CreateEventNameRegistry);

        private static readonly string[] RuntimeDefinitionScripts =
        {
            "scripts/init/txtinit.tcl",
            "scripts/init/animinit.tcl",
            "scripts/init/classinit.tcl",
            "scripts/init/soundinit.tcl",
            "scripts/init/adaptiveinit.tcl",
            "scripts/init/lginit.tcl",
        };

        [Preserve]
        public OriginalSaveLoadPlanner() { }

        /// <summary>
        /// Names registered by def_event while evaluating the declaration-only runtime boot scripts.
        /// </summary>
        public IReadOnlyList<string> RegisteredEventNames => EventNameRegistry.Value.Names;

        /// <summary>
        /// Returns whether the declaration-only runtime boot registered the exact event name.
        /// </summary>
        public bool IsRegisteredEventName(string eventName)
        {
            if (eventName == null) throw new ArgumentNullException(nameof(eventName));
            return EventNameRegistry.Value.Contains(eventName);
        }

        public OriginalSaveLoadPlan Prepare(OriginalSaveArchiveDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            OriginalSaveObjectCollection objects = document.Objects
                ?? throw new NotSupportedException(
                    $"Object mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");

            var identities = new Dictionary<int, OriginalSaveObjectDocument>(objects.Count);
            foreach (OriginalSaveObjectDocument obj in objects)
            {
                if (string.IsNullOrEmpty(obj.ClassName))
                    throw new InvalidDataException($"Original object {obj.ObjectId} has no class name.");
                if (!Finite(obj.Position.X) || !Finite(obj.Position.Y) || !Finite(obj.Position.Z))
                    throw new InvalidDataException($"Original object {obj.ObjectId} has a non-finite position.");
                if (obj.Owner < -1 || obj.Owner > 7)
                    throw new InvalidDataException(
                        $"Original object {obj.ObjectId} has invalid player owner {obj.Owner}.");
                ValidateObjectParticleState(obj);
                if (!identities.TryAdd(obj.ObjectId, obj))
                    throw new InvalidDataException($"Original object ID {obj.ObjectId} is declared more than once.");
            }

            foreach (OriginalSaveObjectDocument obj in objects)
            {
                ValidateObjectEventQueueState(obj, identities);
                ValidateObjectReference(obj.ObjectId, "state object", obj.StateObjectId, identities);
                foreach (int childObjectId in obj.ChildObjectIds)
                {
                    ValidateObjectReference(obj.ObjectId, "child", childObjectId, identities);
                }
            }

            OriginalSaveTimerDocument timers = document.Timers ??
                throw new NotSupportedException(
                    $"Timer mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateTimerState(timers, identities);

            OriginalSaveClassLightDocument classLights = document.ClassLights ??
                throw new NotSupportedException(
                    $"Class-light mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateClassLightState(classLights, identities);

            OriginalSaveEnergyDocument energy = document.Energy ??
                throw new NotSupportedException(
                    $"Energy mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");
            ValidateEnergyState(energy, identities);

            OriginalSaveAnimationControllerDocument animationControllers =
                document.AnimationControllers ?? throw new NotSupportedException(
                    $"Animation-controller mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateAnimationControllerState(animationControllers, identities);

            OriginalSaveParticleDocument particles = document.Particles ??
                throw new NotSupportedException(
                    $"Particle mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateParticleState(particles, identities);

            ValidateFluidReferences(document.World.Fluid, identities);
            OriginalSaveStoryDocument story = document.Story ??
                throw new NotSupportedException(
                    $"Story mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");
            ValidateStoryState(story, identities);
            OriginalSaveSelectionControlGroupDocument selectionControlGroups =
                document.SelectionControlGroups ?? throw new NotSupportedException(
                    $"Selection control-group mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateSelectionControlGroups(selectionControlGroups, identities);
            OriginalSaveSparetimeDocument sparetime = document.Sparetime ??
                throw new NotSupportedException(
                    $"Sparetime registry mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateSparetimeState(sparetime);
            OriginalSaveActionTableDocument actions = document.Actions ??
                throw new NotSupportedException(
                    $"Action mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");
            ValidateActionReferences(actions, identities);
            OriginalSaveTriggerTableDocument triggers = document.Triggers ??
                throw new NotSupportedException(
                    $"Trigger mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");
            ValidateTriggerReferences(triggers, identities);
            OriginalSaveHorizonDocument horizon = document.Horizon ??
                throw new NotSupportedException(
                    $"Horizon mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateHorizonState(horizon);
            OriginalSaveAdaptiveSoundMarkerDocument adaptiveSoundMarkers =
                document.AdaptiveSoundMarkers ?? throw new NotSupportedException(
                    $"Adaptive-sound marker mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateAdaptiveSoundMarkers(adaptiveSoundMarkers);
            OriginalSaveNewsTickerDocument newsTicker = document.NewsTicker ??
                throw new NotSupportedException(
                    $"News-ticker mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateNewsTickerState(newsTicker);
            OriginalSaveObjectIconDocument objectIcons = document.ObjectIcons ??
                throw new NotSupportedException(
                    $"Object-icon mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateObjectIconState(objectIcons);
            OriginalSaveCaveRecordDocument caveRecords = document.CaveRecords ??
                throw new NotSupportedException(
                    $"Cave-record mapping is not implemented for original save version " +
                    $"0x{document.Metadata.Version:X}.");
            ValidateCaveRecordState(caveRecords);
            OriginalSaveAiDocument ai = document.Ai ??
                throw new NotSupportedException(
                    $"AI mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");
            ValidateAiState(ai);
            OriginalSaveElfDocument elf = document.Elf ??
                throw new NotSupportedException(
                    $"CElf mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");
            ValidateElfState(elf, identities);
            OriginalSavePlayerDocument players = document.Players ??
                throw new NotSupportedException(
                    $"Player-state mapping is not implemented for original save version 0x{document.Metadata.Version:X}.");
            ValidatePlayerState(players);
            ValidateFog(document.World);
            return new OriginalSaveLoadPlan(document, identities);
        }

        static void ValidateObjectReference(
            int sourceObjectId,
            string role,
            int targetObjectId,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            if (targetObjectId >= 0 && !identities.ContainsKey(targetObjectId))
            {
                throw new InvalidDataException(
                    $"Original object {sourceObjectId} references undeclared {role} {targetObjectId}.");
            }
        }

        static void ValidateObjectParticleState(OriginalSaveObjectDocument obj)
        {
            foreach (OriginalSaveObjectParticleSourceRecord source in
                     obj.CommonState.ParticleState.Sources)
            {
                if (source.ParticleHandle < -1 || source.ParticleHandle >= 4096)
                {
                    throw new InvalidDataException(
                        $"Original object {obj.ObjectId} particle source {source.SourceIndex} has " +
                        $"invalid native handle {source.ParticleHandle}.");
                }
                if (!Finite(source.RelativePositionX.AsSingle) ||
                    !Finite(source.RelativePositionY.AsSingle) ||
                    !Finite(source.RelativePositionZ.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Original object {obj.ObjectId} particle source {source.SourceIndex} has " +
                        "a non-finite relative position.");
                }
            }
        }

        private static void ValidateObjectEventQueueState(
            OriginalSaveObjectDocument obj,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            OriginalSaveObjectEventQueueDocument queue = obj.EventQueueDocument;
            if ((uint)queue.ReadCursor >= OriginalSaveObjectEventQueueDocument.Capacity)
            {
                throw new InvalidDataException(
                    $"Original object {obj.ObjectId} has invalid event-queue read cursor " +
                    $"{queue.ReadCursor} at offset {queue.QueueOffset}.");
            }
            if ((uint)queue.WriteCursor >= OriginalSaveObjectEventQueueDocument.Capacity)
            {
                throw new InvalidDataException(
                    $"Original object {obj.ObjectId} has invalid event-queue write cursor " +
                    $"{queue.WriteCursor} at offset {queue.QueueOffset + sizeof(int)}.");
            }

            for (int pendingIndex = 0; pendingIndex < queue.PendingWindowCount; pendingIndex++)
            {
                int slotIndex = queue.GetPendingSlotIndex(pendingIndex);
                OriginalSaveTimerEventPayloadDocument payload = queue.Slots[slotIndex] ??
                    throw new InvalidDataException(
                        $"Original object {obj.ObjectId} has an empty pending event-queue slot " +
                        $"{slotIndex} at offset {queue.QueueOffset}.");
                ValidatePendingObjectEvent(
                    obj.ObjectId,
                    $"ring slot {slotIndex}",
                    queue.GetSlotPayloadOffset(slotIndex),
                    payload,
                    identities);
            }

            for (int delayedIndex = 0; delayedIndex < queue.DelayedRecords.Count; delayedIndex++)
            {
                ValidatePendingObjectEvent(
                    obj.ObjectId,
                    $"delayed record {delayedIndex}",
                    queue.GetDelayedPayloadOffset(delayedIndex),
                    queue.DelayedRecords[delayedIndex],
                    identities);
            }
        }

        private static void ValidatePendingObjectEvent(
            int objectId,
            string recordLabel,
            long payloadOffset,
            OriginalSaveTimerEventPayloadDocument payload,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            if (string.IsNullOrEmpty(payload.EventName))
            {
                throw new InvalidDataException(
                    $"Original object {objectId} pending event {recordLabel} has an empty " +
                    $"event name at offset {payloadOffset}.");
            }
            if (!EventNameRegistry.Value.Contains(payload.EventName))
            {
                throw new InvalidDataException(
                    $"Original object {objectId} pending event {recordLabel} names unregistered " +
                    $"def_event '{payload.EventName}' at offset {payloadOffset}.");
            }
            if (payload.OwnerObjectId != objectId)
            {
                throw new InvalidDataException(
                    $"Original object {objectId} pending event '{payload.EventName}' in " +
                    $"{recordLabel} references owner {payload.OwnerObjectId} at offset " +
                    $"{payloadOffset}.");
            }
            if (!Finite(payload.CreatedAt.AsDouble))
            {
                throw new InvalidDataException(
                    $"Original object {objectId} pending event '{payload.EventName}' in " +
                    $"{recordLabel} has a non-finite due time at offset {payloadOffset}.");
            }

            ValidatePendingEventSubject(
                objectId,
                recordLabel,
                payloadOffset,
                payload.EventName,
                "first",
                payload.Subject1ObjectId,
                identities);
            ValidatePendingEventSubject(
                objectId,
                recordLabel,
                payloadOffset,
                payload.EventName,
                "second",
                payload.Subject2ObjectId,
                identities);
            ValidatePendingEventSubject(
                objectId,
                recordLabel,
                payloadOffset,
                payload.EventName,
                "third",
                payload.Subject3ObjectId,
                identities);
        }

        private static void ValidatePendingEventSubject(
            int objectId,
            string recordLabel,
            long payloadOffset,
            string eventName,
            string subjectLabel,
            int subjectObjectId,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            if (subjectObjectId >= -1 &&
                (subjectObjectId < 0 || identities.ContainsKey(subjectObjectId)))
            {
                return;
            }

            throw new InvalidDataException(
                $"Original object {objectId} pending event '{eventName}' in {recordLabel} " +
                $"references invalid {subjectLabel} subject {subjectObjectId} at offset " +
                $"{payloadOffset}.");
        }

        static void ValidateTimerState(
            OriginalSaveTimerDocument timers,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            foreach (OriginalSaveTimerRecord timer in timers.Records)
            {
                OriginalSaveTimerEventPayloadDocument payload = timer.EventPayload;
                if (string.IsNullOrEmpty(payload.EventName))
                    throw new InvalidDataException("A native timer has an empty event name.");
                if (!Finite(timer.NextFire.AsDouble) || !Finite(timer.Interval.AsDouble) ||
                    !Finite(payload.CreatedAt.AsDouble))
                {
                    throw new InvalidDataException(
                        $"Native timer '{payload.EventName}' for object {timer.OwnerObjectId} " +
                        "contains a non-finite time value.");
                }
                if (timer.OwnerObjectId < 0 || !identities.ContainsKey(timer.OwnerObjectId))
                {
                    throw new InvalidDataException(
                        $"Native timer '{payload.EventName}' references undeclared owner " +
                        $"{timer.OwnerObjectId}.");
                }
                if (payload.OwnerObjectId != timer.OwnerObjectId)
                {
                    throw new InvalidDataException(
                        $"Native timer '{payload.EventName}' owner {timer.OwnerObjectId} does not " +
                        $"match event owner {payload.OwnerObjectId}.");
                }

                ValidateTimerSubject(timer, "first", payload.Subject1ObjectId, identities);
                ValidateTimerSubject(timer, "second", payload.Subject2ObjectId, identities);
                ValidateTimerSubject(timer, "third", payload.Subject3ObjectId, identities);
                if (!Finite(payload.Position1X.AsSingle) ||
                    !Finite(payload.Position1Y.AsSingle) ||
                    !Finite(payload.Position1Z.AsSingle) ||
                    !Finite(payload.Position2X.AsSingle) ||
                    !Finite(payload.Position2Y.AsSingle) ||
                    !Finite(payload.Position2Z.AsSingle) ||
                    !Finite(payload.Position3X.AsSingle) ||
                    !Finite(payload.Position3Y.AsSingle) ||
                    !Finite(payload.Position3Z.AsSingle) ||
                    !Finite(payload.Number1.AsSingle) ||
                    !Finite(payload.Number2.AsSingle) ||
                    !Finite(payload.Number3.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Native timer '{payload.EventName}' for object {timer.OwnerObjectId} " +
                        "contains a non-finite event value.");
                }
            }
        }

        static void ValidateTimerSubject(
            OriginalSaveTimerRecord timer,
            string label,
            int subjectObjectId,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            if (subjectObjectId < -1 ||
                (subjectObjectId >= 0 && !identities.ContainsKey(subjectObjectId)))
            {
                throw new InvalidDataException(
                    $"Native timer '{timer.EventPayload.EventName}' for object " +
                    $"{timer.OwnerObjectId} references invalid {label} subject " +
                    $"{subjectObjectId}.");
            }
        }

        static void ValidateFluidReferences(
            OriginalSaveFluidDocument fluid,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            if ((uint)fluid.CurrentBufferIndex > 1 || (uint)fluid.PreviousBufferIndex > 1 ||
                fluid.CurrentBufferIndex == fluid.PreviousBufferIndex)
                throw new InvalidDataException("Original fluid buffer indices are invalid.");

            foreach (OriginalSaveFluidSourceRecord source in fluid.Sources)
            {
                if (!identities.ContainsKey(source.ObjectId))
                    throw new InvalidDataException(
                        $"Original fluid source references undeclared object {source.ObjectId}.");
            }
            foreach (OriginalSaveFluidStopperRecord stopper in fluid.Stoppers)
            {
                if (!identities.ContainsKey(stopper.ObjectId))
                    throw new InvalidDataException(
                        $"Original fluid stopper references undeclared object {stopper.ObjectId}.");
            }
        }

        static void ValidateEnergyState(
            OriginalSaveEnergyDocument energy,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            var sourceIds = new HashSet<int>();
            foreach (OriginalSaveEnergySourceRecord source in energy.Sources)
            {
                if (!identities.ContainsKey(source.SourceObjectId))
                {
                    throw new InvalidDataException(
                        $"Native EnergyMgr source references undeclared object {source.SourceObjectId}.");
                }
                if (!sourceIds.Add(source.SourceObjectId))
                {
                    throw new InvalidDataException(
                        $"Native EnergyMgr source {source.SourceObjectId} is duplicated.");
                }
                if (!Finite(source.Range.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Native EnergyMgr source {source.SourceObjectId} has a non-finite range.");
                }
                foreach (OriginalSaveEnergyLeaseRecord lease in source.Leases)
                {
                    if (!identities.ContainsKey(lease.ConsumerObjectId))
                    {
                        throw new InvalidDataException(
                            $"Native EnergyMgr lease references undeclared consumer {lease.ConsumerObjectId}.");
                    }
                    if (!Finite(lease.Amount.AsSingle))
                    {
                        throw new InvalidDataException(
                            $"Native EnergyMgr lease for consumer {lease.ConsumerObjectId} has a non-finite amount.");
                    }
                }
            }
        }

        static void ValidateClassLightState(
            OriginalSaveClassLightDocument classLights,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            var boundObjectIds = new HashSet<int>();
            foreach (OriginalSaveClassLightRecord light in classLights.Records)
            {
                if (!Finite(light.PositionX.AsSingle) ||
                    !Finite(light.PositionY.AsSingle) ||
                    !Finite(light.PositionZ.AsSingle) ||
                    !Finite(light.Radius.AsSingle) ||
                    !Finite(light.TrailingValue.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Native class-light slot {light.SlotIndex} has non-finite numeric state.");
                }
                if (light.Radius.AsSingle <= 0f)
                {
                    throw new InvalidDataException(
                        $"Native class-light slot {light.SlotIndex} has invalid radius " +
                        $"{light.Radius.AsSingle}.");
                }
                if ((uint)light.Red > 31 || (uint)light.Green > 31 || (uint)light.Blue > 31)
                {
                    throw new InvalidDataException(
                        $"Native class-light slot {light.SlotIndex} has an invalid 5-bit color.");
                }
                if (light.SourceToken < 0)
                {
                    throw new InvalidDataException(
                        $"Native class-light slot {light.SlotIndex} has invalid source token " +
                        $"{light.SourceToken}.");
                }
                if (!light.IsObjectBound) continue;
                if (!identities.ContainsKey(light.SourceToken))
                {
                    throw new InvalidDataException(
                        $"Native class-light slot {light.SlotIndex} references undeclared object " +
                        $"{light.SourceToken}.");
                }
                if (!boundObjectIds.Add(light.SourceToken))
                {
                    throw new InvalidDataException(
                        $"Native class-light object {light.SourceToken} is bound more than once.");
                }
            }
        }

        static void ValidateAnimationControllerState(
            OriginalSaveAnimationControllerDocument animationControllers,
            IReadOnlyDictionary<int, OriginalSaveObjectDocument> identities)
        {
            foreach (OriginalSaveAnimationControllerRecord controller in animationControllers.Records)
            {
                // Finiteness is asserted only for renderable object-backed records (declared
                // object index + model slot 0..255). Orphan/logic-only controllers legally carry
                // allocator-garbage doubles (save-re-reports/B8) and must be retained raw.
                bool renderableObjectBacked =
                    identities.ContainsKey(controller.ControllerIndex) &&
                    controller.ModelDatabaseSlot is >= 0 and <= 255;
                if (renderableObjectBacked && !Finite(controller.NativeTimeState.AsDouble))
                {
                    throw new InvalidDataException(
                        $"Native animation controller {controller.ControllerIndex} has a non-finite " +
                        "time/state value.");
                }
                if (controller.DeclaredValueCount != controller.Values.Count)
                {
                    throw new InvalidDataException(
                        $"Native animation controller {controller.ControllerIndex} declares " +
                        $"{controller.DeclaredValueCount} values but stores " +
                        $"{controller.Values.Count}.");
                }
                if (controller.RepeatedControllerIndex != controller.ControllerIndex)
                {
                    throw new InvalidDataException(
                        $"Native animation controller {controller.ControllerIndex} repeats index " +
                        $"{controller.RepeatedControllerIndex}.");
                }
                if (controller.ModelDatabaseSlot < -1 || controller.ModelDatabaseSlot > 255)
                {
                    throw new InvalidDataException(
                        $"Native animation controller {controller.ControllerIndex} has invalid " +
                        $"model-database slot {controller.ModelDatabaseSlot}.");
                }
            }
        }

        static void ValidateParticleState(
            OriginalSaveParticleDocument particles,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            foreach (OriginalSaveParticleRecord particle in particles.Records)
            {
                if (particle.ParticleCount < 0)
                {
                    throw new InvalidDataException(
                        $"Native particle slot {particle.SlotIndex} has negative particle count " +
                        $"{particle.ParticleCount}.");
                }
                if (!Finite(particle.PositionX.AsSingle) ||
                    !Finite(particle.PositionY.AsSingle) ||
                    !Finite(particle.PositionZ.AsSingle) ||
                    !Finite(particle.DirectionX.AsSingle) ||
                    !Finite(particle.DirectionY.AsSingle) ||
                    !Finite(particle.DirectionZ.AsSingle) ||
                    !Finite(particle.RawValue60.AsSingle) ||
                    !Finite(particle.RawValue68.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Native particle slot {particle.SlotIndex} has a non-finite floating value.");
                }
                if (particle.RawFlag64 > 1)
                {
                    throw new InvalidDataException(
                        $"Native particle slot {particle.SlotIndex} has invalid flag " +
                        $"{particle.RawFlag64}.");
                }
                if (particle.LinkedObjectToken < -1)
                {
                    throw new InvalidDataException(
                        $"Native particle slot {particle.SlotIndex} has invalid object token " +
                        $"{particle.LinkedObjectToken}.");
                }
                if (particle.IsObjectLinked && !identities.ContainsKey(particle.LinkedObjectToken))
                {
                    throw new InvalidDataException(
                        $"Native particle slot {particle.SlotIndex} references undeclared object " +
                        $"{particle.LinkedObjectToken}.");
                }
            }
        }

        static void ValidateActionReferences(
            OriginalSaveActionTableDocument actions,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            foreach (OriginalSaveActionRecord action in actions.Records)
            {
                if (!identities.ContainsKey(action.OwnerObjectId))
                    throw new InvalidDataException(
                        $"Original action references undeclared owner {action.OwnerObjectId}.");

                switch (action.Payload)
                {
                    case OriginalSaveAttackBallisticActionPayload ballistic:
                        ValidateObjectReference(
                            action.OwnerObjectId,
                            "ballistic target",
                            ballistic.TargetObjectId,
                            identities);
                        break;

                    case OriginalSaveLoreActionPayload lore:
                        foreach (OriginalSaveLoreTarget target in lore.Targets)
                        {
                            ValidateObjectReference(
                                action.OwnerObjectId,
                                "Lore target",
                                target.ObjectId,
                                identities);
                        }
                        break;
                }
            }
        }

        static void ValidateStoryState(
            OriginalSaveStoryDocument story,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            var eventNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (OriginalSaveStoryEventDocument storyEvent in story.Events)
            {
                if (storyEvent.Name.Text.Length == 0 || !eventNames.Add(storyEvent.Name.Text))
                {
                    throw new InvalidDataException(
                        $"Native story state contains invalid or duplicate event " +
                        $"'{storyEvent.Name.Text}'.");
                }
            }

            var tempNames = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < story.Temps.Count; index++)
            {
                OriginalSaveStoryTempDocument temp = story.Temps[index];
                if (temp.Name.Text.Length == 0 || !tempNames.Add(temp.Name.Text))
                {
                    throw new InvalidDataException(
                        $"Native story state contains invalid or duplicate unique template " +
                        $"'{temp.Name.Text}'.");
                }
                foreach (int eventIndex in temp.EventIndices)
                {
                    if ((uint)eventIndex >= (uint)story.Events.Count)
                    {
                        throw new InvalidDataException(
                            $"Native story template {index} references invalid event {eventIndex}.");
                    }
                }
                ValidateOptionalIndex(
                    temp.MinimumDistanceReferenceIndex,
                    story.Temps.Count,
                    $"story template {index} minimum-distance reference");
                ValidateOptionalIndex(
                    temp.TemplateGroupIndex,
                    story.Groups.Count,
                    $"story template {index} group reference");
                ValidateOptionalIndex(
                    temp.ZoneIndex,
                    story.Zones.Count,
                    $"story template {index} zone reference");
                ValidateOptionalIndex(
                    temp.ZoneBorderIndex,
                    story.Zones.Count,
                    $"story template {index} zone-border reference");
                if (!Finite(temp.MinimumDistance.AsSingle) ||
                    !Finite(temp.MiddleDistance.AsSingle) ||
                    !Finite(temp.NonRelevantProbability.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Native story template {index} contains a non-finite value.");
                }
                if (temp.NativeWidth < 0 || temp.NativeHeight < 0)
                {
                    throw new InvalidDataException(
                        $"Native story template {index} has invalid dimensions " +
                        $"{temp.NativeWidth}x{temp.NativeHeight}.");
                }
            }

            var groupNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (OriginalSaveStoryGroupDocument group in story.Groups)
            {
                if (group.Name.Text.Length == 0 || !groupNames.Add(group.Name.Text))
                {
                    throw new InvalidDataException(
                        $"Native story state contains invalid or duplicate group " +
                        $"'{group.Name.Text}'.");
                }
            }

            if (story.PairRecords.Count != 0)
            {
                throw new NotSupportedException(
                    $"Native story state contains {story.PairRecords.Count} unmapped pair records.");
            }
            if (story.MapWidth < 0 || story.MapHeight < 0 ||
                (long)story.MapWidth * story.MapHeight != story.MapValues.Count)
            {
                throw new InvalidDataException(
                    $"Native story map dimensions {story.MapWidth}x{story.MapHeight} do not match " +
                    $"{story.MapValues.Count} values.");
            }
            foreach (int value in story.MapValues)
            {
                if ((uint)value > 2)
                    throw new InvalidDataException($"Native story map contains invalid value {value}.");
            }

            var zoneNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (OriginalSaveStoryZoneDocument zone in story.Zones)
            {
                if (zone.Name.Text.Length == 0 || !zoneNames.Add(zone.Name.Text))
                {
                    throw new InvalidDataException(
                        $"Native story state contains invalid or duplicate zone '{zone.Name.Text}'.");
                }
            }
            if (story.ZoneFloor < 0 || story.ZoneFloor > story.Zones.Count)
                throw new InvalidDataException($"Native story zone floor {story.ZoneFloor} is invalid.");
            if (story.LatchDirection != 0 && story.LatchDirection != 1 &&
                story.LatchDirection != 2 && story.LatchDirection != 4 &&
                story.LatchDirection != 8)
            {
                throw new InvalidDataException(
                    $"Native story latch direction {story.LatchDirection} is invalid.");
            }
            if (story.DigWeight < 0 || story.DigThreshold < 0)
                throw new InvalidDataException("Native story dig counters are invalid.");
            ValidateObjectReference(
                -1,
                "story callback object",
                story.CallbackObjectId,
                identities);
        }

        static void ValidateSelectionControlGroups(
            OriginalSaveSelectionControlGroupDocument controlGroups,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            for (int group = 0; group < OriginalSaveSelectionControlGroupDocument.GroupCount; group++)
            {
                IReadOnlyList<int> expected = controlGroups.Groups[group];
                var members = new HashSet<int>();
                foreach (int objectId in expected)
                {
                    if (!identities.ContainsKey(objectId))
                    {
                        throw new InvalidDataException(
                            $"Native selection control group {group + 1} references undeclared " +
                            $"object {objectId}.");
                    }
                    if (!members.Add(objectId))
                    {
                        throw new InvalidDataException(
                            $"Native selection control group {group + 1} contains duplicate object " +
                            $"{objectId}.");
                    }
                }

                for (int copy = 1;
                    copy < OriginalSaveSelectionControlGroupDocument.SerializedCopyCount;
                    copy++)
                {
                    IReadOnlyList<int> actual = controlGroups.GetSerializedGroup(copy, group);
                    if (actual.Count != expected.Count)
                    {
                        throw new InvalidDataException(
                            $"Native selection control-group copy {copy} differs at group {group + 1}.");
                    }
                    for (int member = 0; member < expected.Count; member++)
                    {
                        if (actual[member] != expected[member])
                        {
                            throw new InvalidDataException(
                                $"Native selection control-group copy {copy} differs at group " +
                                $"{group + 1}, member {member}.");
                        }
                    }
                }
            }
        }

        static void ValidateSparetimeState(OriginalSaveSparetimeDocument sparetime)
        {
            var registeredObjects = new HashSet<int>();
            foreach (OriginalSaveSparetimeRecord registration in sparetime.Records)
            {
                if ((uint)registration.Category > 6)
                {
                    throw new InvalidDataException(
                        $"Native sparetime registration for object {registration.ObjectId} has " +
                        $"invalid category {registration.Category}.");
                }
                if (!registeredObjects.Add(registration.ObjectId))
                {
                    throw new InvalidDataException(
                        $"Native sparetime object {registration.ObjectId} is registered more than once.");
                }
            }
        }

        static void ValidateOptionalIndex(int value, int count, string label)
        {
            if (value < -1 || value >= count)
                throw new InvalidDataException($"Native {label} index {value} is invalid.");
        }

        static void ValidateTriggerReferences(
            OriginalSaveTriggerTableDocument triggers,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            const int supportedTargetTypeMask = (1 << 13) - 1;
            for (int slotIndex = 0; slotIndex < triggers.Slots.Count; slotIndex++)
            {
                OriginalSaveTriggerRecord? trigger = triggers.Slots[slotIndex].Record;
                if (trigger == null) continue;
                if (!Finite(trigger.LastCheck) || !Finite(trigger.CheckTimer) ||
                    !Finite(trigger.CheckIdle) || !Finite(trigger.CheckNear) ||
                    !Finite(trigger.TargetRange.AsSingle) || !Finite(trigger.FireTime) ||
                    !Finite(trigger.Interval))
                {
                    throw new InvalidDataException(
                        $"Native trigger slot {slotIndex} has a non-finite numeric value.");
                }
                int unsupportedMask = trigger.TargetTypeMask & ~supportedTargetTypeMask;
                if (unsupportedMask != 0)
                {
                    throw new NotSupportedException(
                        $"Native trigger slot {slotIndex} has unsupported target type mask " +
                        $"0x{unsupportedMask:X8}.");
                }

                if (trigger.Kind == 0)
                {
                    if (trigger.Index != -1 || trigger.OwnerObjectId != -1)
                    {
                        throw new InvalidDataException(
                            $"Native dead trigger slot {slotIndex} is not a canonical tombstone.");
                    }
                    continue;
                }

                if (!identities.TryGetValue(
                        trigger.OwnerObjectId,
                        out OriginalSaveObjectDocument owner))
                {
                    throw new InvalidDataException(
                        $"Native trigger slot {slotIndex} references undeclared owner " +
                        $"{trigger.OwnerObjectId}.");
                }
                if (owner.CommonState.TriggerRecordId != slotIndex)
                {
                    throw new InvalidDataException(
                        $"Native trigger slot {slotIndex} owner {trigger.OwnerObjectId} points back " +
                        $"to trigger {owner.CommonState.TriggerRecordId}.");
                }
                ValidateObjectReference(
                    trigger.OwnerObjectId,
                    "trigger target",
                    trigger.TargetObjectId,
                    identities);
            }

            foreach (OriginalSaveObjectDocument obj in identities.Values)
            {
                int triggerRecordId = obj.CommonState.TriggerRecordId;
                if (triggerRecordId < 0) continue;
                if (triggerRecordId >= triggers.Slots.Count ||
                    triggers.Slots[triggerRecordId].Record?.OwnerObjectId != obj.ObjectId)
                {
                    throw new InvalidDataException(
                        $"Original object {obj.ObjectId} references invalid trigger slot " +
                        $"{triggerRecordId}.");
                }
            }
        }

        static void ValidateHorizonState(OriginalSaveHorizonDocument horizon)
        {
            foreach (OriginalSaveHorizonRegionDocument region in horizon.Regions)
            {
                if (!Finite(region.PositionX.AsSingle) || !Finite(region.PositionY.AsSingle) ||
                    !Finite(region.PositionZ.AsSingle) || !Finite(region.Depth.AsSingle) ||
                    !Finite(region.Size.AsSingle))
                {
                    throw new InvalidDataException(
                        "Native horizon registry contains a non-finite region value.");
                }
                if ((uint)region.Red > byte.MaxValue ||
                    (uint)region.Green > byte.MaxValue ||
                    (uint)region.Blue > byte.MaxValue)
                {
                    throw new InvalidDataException(
                        "Native horizon registry contains a color outside the byte range.");
                }
            }
        }

        static void ValidateAdaptiveSoundMarkers(
            OriginalSaveAdaptiveSoundMarkerDocument adaptiveSoundMarkers)
        {
            foreach (OriginalSaveAdaptiveSoundMarkerRecord marker in adaptiveSoundMarkers.Markers)
            {
                if (marker.ObjectId < -1)
                {
                    throw new InvalidDataException(
                        $"Native adaptive-sound marker has invalid object ID {marker.ObjectId}.");
                }
                if (!Finite(marker.PositionX.AsSingle) || !Finite(marker.PositionY.AsSingle) ||
                    !Finite(marker.PositionZ.AsSingle) || !Finite(marker.Range.AsSingle))
                {
                    throw new InvalidDataException(
                        "Native adaptive-sound marker registry contains a non-finite value.");
                }
            }
        }

        static void ValidateNewsTickerState(OriginalSaveNewsTickerDocument newsTicker)
        {
            var activeIds = new HashSet<int>();
            foreach (OriginalSaveNewsTickerActiveRecord entry in newsTicker.Active)
            {
                if (entry.Id <= 0 || !activeIds.Add(entry.Id))
                {
                    throw new InvalidDataException(
                        $"Native news ticker contains invalid or duplicate active ID {entry.Id}.");
                }
                if (!Finite(entry.Priority.AsSingle) ||
                    !Finite(entry.EffectivePriority.AsSingle) ||
                    !Finite(entry.RemainingTime.AsSingle) ||
                    !Finite(entry.Timestamp))
                {
                    throw new InvalidDataException(
                        $"Native news-ticker entry {entry.Id} contains a non-finite value.");
                }
            }

            var historyIds = new HashSet<int>();
            foreach (OriginalSaveNewsTickerHistoryRecord entry in newsTicker.History)
            {
                if (entry.Id <= 0 || !historyIds.Add(entry.Id))
                {
                    throw new InvalidDataException(
                        $"Native news ticker contains invalid or duplicate history ID {entry.Id}.");
                }
            }

            foreach (OriginalSaveNewsTickerDisplayQueueRecord entry in newsTicker.DisplayQueue)
            {
                if (entry.Id <= 0 || !Finite(entry.Position.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Native news-ticker display queue contains invalid entry {entry.Id}.");
                }
            }
        }

        static void ValidateObjectIconState(OriginalSaveObjectIconDocument objectIcons)
        {
            if (objectIcons.Slots.Count != OriginalSaveObjectIconDocument.SlotCount)
                throw new InvalidDataException("Native object-icon table does not contain 128 slots.");
            foreach (OriginalSaveObjectIconRecord slot in objectIcons.Slots)
            {
                if (!Finite(slot.OffsetX.AsSingle) || !Finite(slot.OffsetY.AsSingle) ||
                    !Finite(slot.OffsetZ.AsSingle))
                {
                    throw new InvalidDataException(
                        $"Native object-icon slot {slot.Slot} contains a non-finite offset.");
                }
            }
        }

        static void ValidateCaveRecordState(OriginalSaveCaveRecordDocument caveRecords)
        {
            foreach (OriginalSaveCaveRecord record in caveRecords.Records)
            {
                if (record.X0 > record.X1 || record.Y0 > record.Y1)
                    throw new InvalidDataException("Native cave record has reversed bounds.");
            }
        }

        static void ValidateAiState(OriginalSaveAiDocument ai)
        {
            if (ai.Owners.Count != 8)
                throw new InvalidDataException("Native AI state does not contain eight owner slots.");

            for (int owner = 0; owner < ai.Owners.Count; owner++)
            {
                OriginalSaveAiOwnerDocument state = ai.Owners[owner];
                if (state.Owner != owner)
                    throw new InvalidDataException(
                        $"Native AI slot {owner} contains owner {state.Owner}.");
                if (state.Enabled && !state.InterpreterPresent)
                    throw new InvalidDataException(
                        $"Native AI owner {owner} is enabled without interpreter state.");
                if (!state.InterpreterPresent)
                {
                    if (state.ScriptName.Text.Length != 0 || state.Variables.Count != 0)
                    {
                        throw new InvalidDataException(
                            $"Native AI owner {owner} has state without an interpreter.");
                    }
                    continue;
                }
                if (!string.Equals(
                        state.ScriptName.Text,
                        OriginalSaveAiDocument.NativeStandardScriptPath,
                        StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        $"Native AI owner {owner} references unsupported script " +
                        $"'{state.ScriptName.Text}'.");
                }

                var variableNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (OriginalSaveTclVariable variable in state.Variables)
                {
                    if (!variableNames.Add(variable.Name))
                    {
                        throw new InvalidDataException(
                            $"Native AI owner {owner} contains duplicate variable " +
                            $"'{variable.Name}'.");
                    }
                }
            }
        }

        static void ValidateElfState(
            OriginalSaveElfDocument elf,
            Dictionary<int, OriginalSaveObjectDocument> identities)
        {
            if (elf.CurrentRegisteredAnimation < -1 || elf.CurrentRegisteredAnimation >= 34 ||
                elf.RequestedRegisteredAnimation < -1 || elf.RequestedRegisteredAnimation >= 34)
            {
                throw new InvalidDataException("Native CElf registered animation index is invalid.");
            }
            if (elf.Mode < 0 || elf.Mode > 5)
                throw new InvalidDataException($"Native CElf mode {elf.Mode} is invalid.");
            if (!Finite(elf.FrameAlpha.AsSingle) ||
                !Finite(elf.Position.X) || !Finite(elf.Position.Y) || !Finite(elf.Position.Z) ||
                !Finite(elf.LeanA.AsSingle) || !Finite(elf.LeanB.AsSingle) ||
                !Finite(elf.NativeState64.AsSingle) ||
                !Finite(elf.Target.X) || !Finite(elf.Target.Y) || !Finite(elf.Target.Z))
            {
                throw new InvalidDataException("Native CElf state contains a non-finite value.");
            }
            if (elf.LookAtObjectId >= 0 && !identities.ContainsKey(elf.LookAtObjectId))
            {
                throw new InvalidDataException(
                    $"Native CElf references undeclared look-at object {elf.LookAtObjectId}.");
            }
            ValidateParticleHandle(elf.FirstParticleSourceHandle, "first");
            ValidateParticleHandle(elf.SecondParticleSourceHandle, "second");
        }

        static void ValidateParticleHandle(int handle, string label)
        {
            if (handle < -1 || handle >= 0x2000)
                throw new InvalidDataException(
                    $"Native CElf {label} particle-source handle {handle} is invalid.");
        }

        static void ValidatePlayerState(OriginalSavePlayerDocument players)
        {
            if (players.ReservedFraming.Count != 32 || players.Slots.Count != 8)
                throw new InvalidDataException("Native player state has invalid fixed dimensions.");
            for (int owner = 0; owner < players.Slots.Count; owner++)
            {
                OriginalSavePlayerSlotDocument slot = players.Slots[owner];
                if (slot.SlotState < 0 || slot.SlotState > 2)
                    throw new InvalidDataException(
                        $"Native player slot {owner} has invalid state {slot.SlotState}.");
                if ((uint)slot.PlayerOwnerId >= 8 || (uint)slot.ClanOwnerId >= 8)
                    throw new InvalidDataException(
                        $"Native player slot {owner} has invalid owner mapping " +
                        $"{slot.PlayerOwnerId}/{slot.ClanOwnerId}.");
                if (slot.Diplomacy.Count != 8)
                    throw new InvalidDataException(
                        $"Native player slot {owner} has an invalid diplomacy row.");
                for (int target = 0; target < slot.Diplomacy.Count; target++)
                {
                    int relation = slot.Diplomacy[target];
                    if (relation < 0 || relation > 4)
                    {
                        throw new InvalidDataException(
                            $"Native diplomacy relation {owner}->{target} has invalid value " +
                            $"{relation}.");
                    }
                    if (owner == target && relation != 0)
                    {
                        throw new InvalidDataException(
                            $"Native player slot {owner} does not own itself.");
                    }
                }
            }
        }

        static void ValidateFog(OriginalSaveWorldDocument world)
        {
            for (int player = 0; player < world.FogBeginRows.Count; player++)
            {
                int row = world.FogBeginRows[player];
                if (row < -1 || row >= world.Height)
                    throw new InvalidDataException(
                        $"Original fog begin row {row} is outside the world for player {player}.");

                foreach (OriginalSaveIntPair tile in world.Fog.FadeTilesByPlayer[player])
                {
                    if ((uint)tile.First >= (uint)world.Width || (uint)tile.Second >= (uint)world.Height)
                    {
                        throw new InvalidDataException(
                            $"Original fog fade tile ({tile.First}, {tile.Second}) is outside the world.");
                    }
                }
            }
        }

        private static OriginalSaveEventNameRegistry CreateEventNameRegistry()
        {
            string dataRoot;
            try
            {
                string? environmentDataRoot = Environment.GetEnvironmentVariable(
                    DataRootEnvironmentVariable);
                dataRoot = string.IsNullOrWhiteSpace(environmentDataRoot)
                    ? DigglesConfig.ResolveDataRoot(string.Empty)
                    : Path.GetFullPath(environmentDataRoot!.Trim());
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "The original data root could not be resolved for def_event preflight.",
                    exception);
            }
            string scriptsRoot = Path.Combine(dataRoot, "Scripts");
            if (!Directory.Exists(scriptsRoot))
            {
                throw new InvalidDataException(
                    $"The original Scripts directory was not found at '{scriptsRoot}' for " +
                    "def_event preflight.");
            }

            var interpreter = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            interpreter.UnknownCommandHandler = (_, _) => "0";
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            context.DataRoot = dataRoot;
            context.TolerateMissingClass = true;
            context.TolerateMissingEntities = true;
            context.TolerateMissingFiles = true;
            _ = new DigglesGameSim(context);

            var eventNames = new HashSet<string>(StringComparer.Ordinal);
            interpreter.RegisterCommand("def_event", (_, arguments) =>
            {
                if (arguments.Length != 2)
                    throw new TclException("wrong # args: should be \"def_event name\"");
                if (arguments[1].Length == 0)
                    throw new TclException("def_event requires a non-empty name");
                eventNames.Add(arguments[1]);
                return string.Empty;
            });

            var failures = new List<string>();
            foreach (string script in RuntimeDefinitionScripts)
            {
                try
                {
                    interpreter.Eval("call " + script);
                }
                catch (Exception exception)
                {
                    failures.Add($"{script}: {exception.Message}");
                }
            }

            bool requiredNamesPresent =
                eventNames.Contains("evt_timer0") &&
                eventNames.Contains("evt_zwerg_workannounce") &&
                eventNames.Contains("evt_zwerg_attribupdate") &&
                eventNames.Contains("evt_pilz_aging");
            if (!requiredNamesPresent ||
                eventNames.Count > NativeEventRegistryCapacity ||
                context.Entities.Count != 0)
            {
                string failureDetails = failures.Count == 0
                    ? "none"
                    : string.Join(" | ", failures);
                throw new InvalidDataException(
                    $"The def_event registry boot was incomplete or exceeded its native " +
                    $"{NativeEventRegistryCapacity}-entry capacity. Registered " +
                    $"{eventNames.Count} unique names and created {context.Entities.Count} " +
                    $"entities. Definition failures: {failureDetails}.");
            }

            return new OriginalSaveEventNameRegistry(eventNames);
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private sealed class OriginalSaveEventNameRegistry
        {
            private readonly HashSet<string> _names;

            internal OriginalSaveEventNameRegistry(HashSet<string> names)
            {
                if (names == null) throw new ArgumentNullException(nameof(names));
                _names = new HashSet<string>(names, StringComparer.Ordinal);
                var orderedNames = new string[_names.Count];
                _names.CopyTo(orderedNames);
                Array.Sort(orderedNames, StringComparer.Ordinal);
                Names = Array.AsReadOnly(orderedNames);
            }

            internal IReadOnlyList<string> Names { get; }

            internal bool Contains(string eventName) => _names.Contains(eventName);
        }
    }

    public sealed class OriginalSaveLoadPlan
    {
        private readonly ReadOnlyDictionary<int, OriginalSaveObjectDocument> _objectsById;

        internal OriginalSaveLoadPlan(
            OriginalSaveArchiveDocument document,
            Dictionary<int, OriginalSaveObjectDocument> objectsById)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            _objectsById = new ReadOnlyDictionary<int, OriginalSaveObjectDocument>(
                objectsById ?? throw new ArgumentNullException(nameof(objectsById)));
        }

        public OriginalSaveArchiveDocument Document { get; }
        public IReadOnlyDictionary<int, OriginalSaveObjectDocument> ObjectsById => _objectsById;
    }
}
