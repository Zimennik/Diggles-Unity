#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using UnityEngine.Scripting;

    public interface IOriginalSaveActionCandidateStager
    {
        /// <summary>
        /// Prepares the complete action set without installing actions or invoking runtime callbacks.
        /// </summary>
        OriginalSavePreparedActionSet StageCandidates(
            OriginalSaveRuntimeSnapshot source,
            IReadOnlyDictionary<int, DigglesTclEntity> candidateEntitiesById,
            DigglesTclContext candidateContext);
    }

    public sealed class OriginalSaveActionCandidateStager : IOriginalSaveActionCandidateStager
    {
        private readonly IOriginalSaveActionFactory _factory;

        [Preserve]
        public OriginalSaveActionCandidateStager(IOriginalSaveActionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public OriginalSavePreparedActionSet StageCandidates(
            OriginalSaveRuntimeSnapshot source,
            IReadOnlyDictionary<int, DigglesTclEntity> candidateEntitiesById,
            DigglesTclContext candidateContext)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (candidateEntitiesById == null)
                throw new ArgumentNullException(nameof(candidateEntitiesById));
            if (candidateContext == null) throw new ArgumentNullException(nameof(candidateContext));

            LoreTrackRecord[] loreTrackRecords = StageLoreTracks(source.ActionAuxiliaryRecords);
            var loreTrackOwners = new HashSet<int>();
            for (int index = 0; index < loreTrackRecords.Length; index++)
                loreTrackOwners.Add(loreTrackRecords[index].OwnerId);

            var prepared = new OriginalSavePreparedAction[source.Actions.Count];
            var preparedByOwnerId = new Dictionary<int, OriginalSavePreparedAction>(prepared.Length);
            var unmetActivationConditions = new List<string>();
            for (int index = 0; index < source.Actions.Count; index++)
            {
                OriginalSaveRuntimeActionSnapshot stagedAction = source.Actions[index];
                if (!candidateEntitiesById.TryGetValue(
                    stagedAction.OwnerObjectId,
                    out DigglesTclEntity? owner))
                {
                    throw new InvalidOperationException(
                        $"Candidate entity {stagedAction.OwnerObjectId} required by the native action table is missing.");
                }
                if (stagedAction.Type == OriginalSaveActionType.Lore &&
                    !loreTrackOwners.Contains(stagedAction.OwnerObjectId))
                {
                    throw new InvalidOperationException(
                        $"Native Lore action owner {stagedAction.OwnerObjectId} has no staged Lore track record.");
                }
                OriginalSavePreparedAction candidate;
                try
                {
                    candidate = _factory.CreateCandidate(
                        stagedAction,
                        owner,
                        candidateContext,
                        source.GameTime);
                }
                catch (NotSupportedException exception)
                {
                    unmetActivationConditions.Add(
                        $"Native action {stagedAction.Type} for owner " +
                        $"{stagedAction.OwnerObjectId} has no runtime continuation: " +
                        exception.Message);
                    candidate = new OriginalSavePreparedAction(
                        stagedAction.Type,
                        owner.Handle,
                        null,
                        "",
                        "",
                        0,
                        null,
                        null);
                }
                prepared[index] = candidate;
                preparedByOwnerId.Add(candidate.OwnerObjectId, candidate);
            }
            return new OriginalSavePreparedActionSet(
                prepared,
                preparedByOwnerId,
                candidateContext.LoreTracks,
                loreTrackRecords,
                unmetActivationConditions.ToArray());
        }

        private static LoreTrackRecord[] StageLoreTracks(
            IReadOnlyList<OriginalSaveRuntimeActionAuxiliarySnapshot> sources)
        {
            var result = new LoreTrackRecord[sources.Count];
            var owners = new HashSet<int>();
            for (int recordIndex = 0; recordIndex < sources.Count; recordIndex++)
            {
                OriginalSaveRuntimeActionAuxiliarySnapshot source = sources[recordIndex];
                if (!owners.Add(source.Value))
                {
                    throw new InvalidOperationException(
                        $"Lore track owner {source.Value} appears more than once in the native registry.");
                }
                var waypoints = new LoreTrackWaypoint[source.Vectors.Count];
                for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
                {
                    IReadOnlyList<int> values = source.Vectors[waypointIndex];
                    if (values.Count != 8)
                    {
                        throw new InvalidOperationException(
                            $"Lore track owner {source.Value} has a waypoint with {values.Count} values.");
                    }
                    waypoints[waypointIndex] = new LoreTrackWaypoint(
                        new Vector3(Single(values[0]), Single(values[1]), Single(values[2])),
                        new Vector3(Single(values[3]), Single(values[4]), Single(values[5])),
                        Single(values[6]),
                        Single(values[7]));
                }

                if (source.TrailingValues.Count != 3)
                {
                    throw new InvalidOperationException(
                        $"Lore track owner {source.Value} has {source.TrailingValues.Count} trailing values.");
                }
                result[recordIndex] = new LoreTrackRecord(
                    source.Value,
                    waypoints,
                    Single(source.TrailingValues[0]),
                    Single(source.TrailingValues[1]),
                    Single(source.TrailingValues[2]));
            }
            return result;
        }

        private static float Single(int bits) => BitConverter.Int32BitsToSingle(bits);
    }

    public sealed class OriginalSavePreparedActionSet
    {
        private readonly ReadOnlyCollection<OriginalSavePreparedAction> _actions;
        private readonly ReadOnlyDictionary<int, OriginalSavePreparedAction> _actionsByOwnerId;
        private readonly ReadOnlyCollection<LoreTrackRecord> _loreTrackRecords;
        private readonly ReadOnlyCollection<string> _unmetActivationConditions;

        internal OriginalSavePreparedActionSet(
            OriginalSavePreparedAction[] actions,
            Dictionary<int, OriginalSavePreparedAction> actionsByOwnerId,
            LoreTrackSystem loreTrackSystem,
            LoreTrackRecord[] loreTrackRecords,
            string[] unmetActivationConditions)
        {
            _actions = Array.AsReadOnly(actions ?? throw new ArgumentNullException(nameof(actions)));
            _actionsByOwnerId = new ReadOnlyDictionary<int, OriginalSavePreparedAction>(
                actionsByOwnerId ?? throw new ArgumentNullException(nameof(actionsByOwnerId)));
            LoreTrackSystem = loreTrackSystem ??
                throw new ArgumentNullException(nameof(loreTrackSystem));
            _loreTrackRecords = Array.AsReadOnly(loreTrackRecords ??
                throw new ArgumentNullException(nameof(loreTrackRecords)));
            _unmetActivationConditions = Array.AsReadOnly(unmetActivationConditions ??
                throw new ArgumentNullException(nameof(unmetActivationConditions)));
        }

        public IReadOnlyList<OriginalSavePreparedAction> Actions => _actions;
        public IReadOnlyDictionary<int, OriginalSavePreparedAction> ActionsByOwnerId =>
            _actionsByOwnerId;
        public LoreTrackSystem LoreTrackSystem { get; }
        public IReadOnlyList<LoreTrackRecord> LoreTrackRecords => _loreTrackRecords;
        public IReadOnlyList<string> UnmetActivationConditions =>
            _unmetActivationConditions;
    }
}
