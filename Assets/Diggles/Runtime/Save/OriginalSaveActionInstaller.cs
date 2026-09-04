#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine.Scripting;

    public interface IOriginalSaveActionInstaller
    {
        /// <summary>
        /// Installs a completely prepared action set into an isolated candidate entity graph.
        /// </summary>
        void Install(
            OriginalSavePreparedActionSet prepared,
            IReadOnlyDictionary<int, DigglesTclEntity> candidateEntitiesById);

        /// <summary>
        /// Publishes the staged pending event window into candidate entity inboxes without
        /// dispatching an event or evaluating Tcl.
        /// </summary>
        void InstallPendingEntityEvents(
            IReadOnlyDictionary<
                int,
                IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot>> pendingEventsByEntity,
            IReadOnlyDictionary<int, DigglesTclEntity> candidateEntitiesById,
            DigglesTclContext context,
            double restoredGameTime);

        /// <summary>
        /// Raises one system event by queuing a delivery for every entity with a matching handler.
        /// </summary>
        int PublishSystemEvent(
            string eventName,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entitiesById);
    }

    public sealed class OriginalSaveActionInstaller : IOriginalSaveActionInstaller
    {
        [Preserve]
        public OriginalSaveActionInstaller() { }

        public void Install(
            OriginalSavePreparedActionSet prepared,
            IReadOnlyDictionary<int, DigglesTclEntity> candidateEntitiesById)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (candidateEntitiesById == null)
                throw new ArgumentNullException(nameof(candidateEntitiesById));
            if (prepared.LoreTrackSystem.Count != 0)
                throw new InvalidOperationException("The candidate Lore track registry is not empty.");

            // Validate the entire graph before the first write. The assignments below cannot invoke
            // Tcl or user callbacks, so this preflight gives candidate installation all-or-nothing
            // behavior even before the outer session transaction is activated.
            foreach (OriginalSavePreparedAction action in prepared.Actions)
            {
                if (!candidateEntitiesById.TryGetValue(
                    action.OwnerObjectId,
                    out DigglesTclEntity? owner))
                {
                    throw new InvalidOperationException(
                        $"Candidate entity {action.OwnerObjectId} required by the prepared action set is missing.");
                }
                if (owner.Handle != action.OwnerObjectId)
                {
                    throw new InvalidOperationException(
                        $"Candidate action owner key {action.OwnerObjectId} contains entity {owner.Handle}.");
                }
                if (owner.ActiveAction != null || owner.ActionFinish != null || owner.ActionBreak != null)
                {
                    throw new InvalidOperationException(
                        $"Candidate entity {owner.Handle} already has runtime action state.");
                }
            }
            prepared.LoreTrackSystem.RestoreRecords(prepared.LoreTrackRecords);
            foreach (OriginalSavePreparedAction action in prepared.Actions)
            {
                DigglesTclEntity owner = candidateEntitiesById[action.OwnerObjectId];
                owner.ActiveAction = action.Action;
                // A restored busy flag needs its carrier re-registered, or nothing ever
                // clears it once the restored clear-string chain is lost.
                if (owner.AttackInProgress) owner.AttackCarrier = owner.ActiveAction;
                owner.ActionFinish = EmptyToNull(action.FinishCode);
                owner.ActionBreak = EmptyToNull(action.BreakCode);
                if (action.InitialWalkResult is int walkResult)
                    owner.WalkResult = walkResult;
                if (action.InitialBallisticResult is int ballisticResult)
                    owner.BallisticResult = ballisticResult;
            }
        }

        public void InstallPendingEntityEvents(
            IReadOnlyDictionary<
                int,
                IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot>> pendingEventsByEntity,
            IReadOnlyDictionary<int, DigglesTclEntity> candidateEntitiesById,
            DigglesTclContext context,
            double restoredGameTime)
        {
            if (pendingEventsByEntity == null)
                throw new ArgumentNullException(nameof(pendingEventsByEntity));
            if (candidateEntitiesById == null)
                throw new ArgumentNullException(nameof(candidateEntitiesById));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (double.IsNaN(restoredGameTime) || double.IsInfinity(restoredGameTime))
                throw new ArgumentOutOfRangeException(nameof(restoredGameTime));
            if (pendingEventsByEntity.Count != candidateEntitiesById.Count)
            {
                throw new InvalidOperationException(
                    "The staged pending-event owner set does not match the candidate entity graph.");
            }

            foreach (DigglesTclEntity entity in candidateEntitiesById.Values)
            {
                if (entity.PendingEvents.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Candidate entity {entity.Handle} already has pending runtime events.");
                }
            }

            var installations = new List<(DigglesTclEntity Entity, EntityEvent Event)>();
            var delayedInstallations = new List<(
                int OwnerObjectId,
                string EventName,
                double DueTime,
                Dictionary<string, string> Payload)>();
            foreach (KeyValuePair<
                         int,
                         IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot>> pair in
                     pendingEventsByEntity)
            {
                if (!candidateEntitiesById.TryGetValue(
                        pair.Key,
                        out DigglesTclEntity? entity))
                {
                    throw new InvalidOperationException(
                        $"Candidate entity {pair.Key} required by the pending-event set is missing.");
                }
                if (entity.Handle != pair.Key)
                {
                    throw new InvalidOperationException(
                        $"Candidate pending-event owner key {pair.Key} contains entity {entity.Handle}.");
                }

                foreach (OriginalSaveRuntimeEntityEventSnapshot pending in pair.Value)
                {
                    if (!pending.IsDeliverable)
                    {
                        throw new InvalidOperationException(
                            $"Non-deliverable event residue for entity {pair.Key} reached commit installation.");
                    }
                    if (pending.OwnerObjectId != pair.Key)
                    {
                        throw new InvalidOperationException(
                            $"Pending event '{pending.EventName}' owner {pending.OwnerObjectId} " +
                            $"does not match candidate entity {pair.Key}.");
                    }
                    Dictionary<string, string> payload = CreateEventPayload(pending);
                    double dueTime = pending.DueTime.AsDouble;
                    if (pending.IsDelayed &&
                        (double.IsNaN(dueTime) || double.IsInfinity(dueTime)))
                    {
                        throw new InvalidOperationException(
                            $"Delayed event '{pending.EventName}' for entity {pair.Key} " +
                            "has a non-finite due time.");
                    }
                    if (pending.IsDelayed && dueTime > restoredGameTime)
                    {
                        delayedInstallations.Add((
                            entity.Handle,
                            pending.EventName,
                            dueTime,
                            payload));
                    }
                    else
                    {
                        installations.Add((
                            entity,
                            new EntityEvent(pending.EventName, payload)));
                    }
                }
            }

            TimerScheduler? delayedScheduler = delayedInstallations.Count == 0
                ? null
                : context.Sim?.Scheduler ??
                    throw new InvalidOperationException("The candidate event scheduler is unavailable.");
            foreach ((DigglesTclEntity entity, EntityEvent pending) in installations)
                entity.EnqueueRestoredEvent(pending);
            if (delayedScheduler == null) return;
            foreach ((
                         int ownerObjectId,
                         string eventName,
                         double dueTime,
                         Dictionary<string, string> payload) in delayedInstallations)
            {
                delayedScheduler.Schedule(
                    ownerObjectId,
                    eventName,
                    dueTime,
                    0d,
                    1,
                    payload);
            }
        }

        public int PublishSystemEvent(
            string eventName,
            DigglesTclContext context,
            IReadOnlyDictionary<int, DigglesTclEntity> entitiesById)
        {
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentException("A system event name is required.", nameof(eventName));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (entitiesById == null) throw new ArgumentNullException(nameof(entitiesById));

            var recipients = new List<DigglesTclEntity>();
            foreach (DigglesTclEntity entity in entitiesById.Values)
            {
                if (entity.Class != null &&
                    context.FindEventHandler(entity.Class, eventName) != null)
                {
                    recipients.Add(entity);
                }
            }

            foreach (DigglesTclEntity recipient in recipients)
            {
                recipient.EnqueueEvent(
                    new EntityEvent(eventName),
                    context.Sim?.GameTime ?? 0d);
            }
            return recipients.Count;
        }

        private static Dictionary<string, string> CreateEventPayload(
            OriginalSaveRuntimeEntityEventSnapshot source)
        {
            IReadOnlyList<OriginalSaveRaw32> values = source.Arguments;
            var payload = new Dictionary<string, string>
            {
                ["-pos1"] = FormatVector(values[0], values[1], values[2]),
                ["-pos2"] = FormatVector(values[3], values[4], values[5]),
                ["-pos3"] = FormatVector(values[6], values[7], values[8]),
                ["-num1"] = source.Word1.AsSingle.ToString("R", CultureInfo.InvariantCulture),
                ["-num2"] = source.Word2.AsSingle.ToString("R", CultureInfo.InvariantCulture),
                ["-num3"] = source.Word3.AsSingle.ToString("R", CultureInfo.InvariantCulture),
                ["-text1"] = source.Text1.Text,
                ["-text2"] = source.Text2.Text,
            };
            AddSubject(payload, "-subject1", source.Subject1ObjectId);
            AddSubject(payload, "-subject2", source.Subject2ObjectId);
            AddSubject(payload, "-subject3", source.Subject3ObjectId);
            return payload;
        }

        private static void AddSubject(
            Dictionary<string, string> payload,
            string key,
            int objectId)
        {
            if (objectId >= 0)
                payload[key] = objectId.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatVector(
            OriginalSaveRaw32 x,
            OriginalSaveRaw32 y,
            OriginalSaveRaw32 z) =>
            x.AsSingle.ToString("R", CultureInfo.InvariantCulture) + " " +
            y.AsSingle.ToString("R", CultureInfo.InvariantCulture) + " " +
            z.AsSingle.ToString("R", CultureInfo.InvariantCulture);

        private static string? EmptyToNull(string value) =>
            string.IsNullOrEmpty(value) ? null : value;
    }
}
