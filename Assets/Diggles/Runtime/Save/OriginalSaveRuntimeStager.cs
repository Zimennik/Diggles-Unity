#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine.Scripting;

    public interface IOriginalSaveRuntimeStager
    {
        OriginalSaveRuntimeSnapshot Stage(OriginalSaveLoadPlan plan);
    }

    public sealed class OriginalSaveRuntimeStager : IOriginalSaveRuntimeStager
    {
        [Preserve]
        public OriginalSaveRuntimeStager() { }

        public OriginalSaveRuntimeSnapshot Stage(OriginalSaveLoadPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            OriginalSaveObjectCollection objects = plan.Document.Objects
                ?? throw new InvalidOperationException("The load plan has no typed object collection.");
            OriginalSaveObjectManifest objectManifest = plan.Document.Metadata.Objects
                ?? throw new InvalidOperationException("The load plan has no object manifest.");
            OriginalSaveClassLightDocument classLightDocument = plan.Document.ClassLights
                ?? throw new InvalidOperationException("The load plan has no typed class-light state.");
            OriginalSaveEnergyDocument energyDocument = plan.Document.Energy
                ?? throw new InvalidOperationException("The load plan has no typed EnergyMgr state.");
            OriginalSaveAnimationControllerDocument animationControllerDocument =
                plan.Document.AnimationControllers ?? throw new InvalidOperationException(
                    "The load plan has no typed animation-controller state.");
            OriginalSaveParticleDocument particleDocument = plan.Document.Particles
                ?? throw new InvalidOperationException("The load plan has no typed particle state.");
            OriginalSaveTimerDocument timerDocument = plan.Document.Timers
                ?? throw new InvalidOperationException("The load plan has no typed timer state.");
            OriginalSaveActionTableDocument actionTable = plan.Document.Actions
                ?? throw new InvalidOperationException("The load plan has no typed action table.");
            OriginalSaveTriggerTableDocument triggerTable = plan.Document.Triggers
                ?? throw new InvalidOperationException("The load plan has no typed trigger table.");
            OriginalSaveAiDocument aiDocument = plan.Document.Ai
                ?? throw new InvalidOperationException("The load plan has no typed AI state.");
            OriginalSaveElfDocument elfDocument = plan.Document.Elf
                ?? throw new InvalidOperationException("The load plan has no typed CElf state.");
            OriginalSavePlayerDocument playerDocument = plan.Document.Players
                ?? throw new InvalidOperationException("The load plan has no typed player state.");
            OriginalSaveStoryDocument storyDocument = plan.Document.Story
                ?? throw new InvalidOperationException("The load plan has no typed story state.");
            OriginalSaveSelectionControlGroupDocument controlGroupDocument =
                plan.Document.SelectionControlGroups ?? throw new InvalidOperationException(
                    "The load plan has no typed selection control groups.");
            OriginalSaveSparetimeDocument sparetimeDocument = plan.Document.Sparetime
                ?? throw new InvalidOperationException("The load plan has no typed sparetime registry.");
            OriginalSaveHorizonDocument horizonDocument = plan.Document.Horizon
                ?? throw new InvalidOperationException("The load plan has no typed horizon registry.");
            OriginalSaveAdaptiveSoundMarkerDocument adaptiveSoundMarkerDocument =
                plan.Document.AdaptiveSoundMarkers ?? throw new InvalidOperationException(
                    "The load plan has no typed adaptive-sound marker registry.");
            OriginalSaveNewsTickerDocument newsTickerDocument = plan.Document.NewsTicker
                ?? throw new InvalidOperationException("The load plan has no typed news-ticker state.");
            OriginalSaveObjectIconDocument objectIconDocument = plan.Document.ObjectIcons
                ?? throw new InvalidOperationException("The load plan has no typed object-icon state.");
            OriginalSaveCaveRecordDocument caveRecordDocument = plan.Document.CaveRecords
                ?? throw new InvalidOperationException("The load plan has no typed cave records.");

            var actions = new OriginalSaveRuntimeActionSnapshot[actionTable.Records.Count];
            var actionsByOwnerId = new Dictionary<int, OriginalSaveRuntimeActionSnapshot>(
                actions.Length);
            for (int index = 0; index < actions.Length; index++)
            {
                var action = new OriginalSaveRuntimeActionSnapshot(actionTable.Records[index]);
                actions[index] = action;
                actionsByOwnerId.Add(action.OwnerObjectId, action);
            }

            var actionAuxiliaryRecords =
                new OriginalSaveRuntimeActionAuxiliarySnapshot[actionTable.AuxiliaryRecords.Count];
            for (int index = 0; index < actionAuxiliaryRecords.Length; index++)
            {
                actionAuxiliaryRecords[index] = new OriginalSaveRuntimeActionAuxiliarySnapshot(
                    actionTable.AuxiliaryRecords[index]);
            }

            var entities = new OriginalSaveRuntimeEntitySnapshot[objects.Count];
            var entitiesById = new Dictionary<int, OriginalSaveRuntimeEntitySnapshot>(objects.Count);
            for (int index = 0; index < objects.Count; index++)
            {
                OriginalSaveObjectDocument source = objects[index];
                actionsByOwnerId.TryGetValue(
                    source.ObjectId,
                    out OriginalSaveRuntimeActionSnapshot? stagedAction);
                var entity = new OriginalSaveRuntimeEntitySnapshot(source, stagedAction);
                entities[index] = entity;
                entitiesById.Add(entity.ObjectId, entity);
            }
            foreach (int ownerObjectId in actionsByOwnerId.Keys)
            {
                if (!entitiesById.ContainsKey(ownerObjectId))
                    throw new InvalidOperationException(
                        $"Staged action owner {ownerObjectId} has no staged entity identity.");
            }

            var mutableReferrers = new Dictionary<int, List<int>>();
            foreach (OriginalSaveRuntimeEntitySnapshot source in entities)
            {
                foreach (int childObjectId in source.ChildObjectIds)
                {
                    if (childObjectId < 0) continue;
                    if (!mutableReferrers.TryGetValue(childObjectId, out List<int>? referrers))
                    {
                        referrers = new List<int>();
                        mutableReferrers.Add(childObjectId, referrers);
                    }
                    referrers.Add(source.ObjectId);
                }
            }

            var referrersByObjectId = new Dictionary<int, IReadOnlyList<int>>(mutableReferrers.Count);
            foreach (KeyValuePair<int, List<int>> pair in mutableReferrers)
                referrersByObjectId.Add(pair.Key, Array.AsReadOnly(pair.Value.ToArray()));

            var triggers = new OriginalSaveRuntimeTriggerSnapshot?[triggerTable.Slots.Count];
            for (int index = 0; index < triggers.Length; index++)
            {
                OriginalSaveTriggerRecord? trigger = triggerTable.Slots[index].Record;
                if (trigger != null)
                    triggers[index] = new OriginalSaveRuntimeTriggerSnapshot(trigger);
            }

            return new OriginalSaveRuntimeSnapshot(
                plan.Document,
                new OriginalSaveRuntimeWorldSnapshot(plan.Document.World),
                new OriginalSaveRuntimeClassLightSnapshot(classLightDocument),
                new OriginalSaveRuntimeEnergySnapshot(energyDocument),
                new OriginalSaveRuntimeAnimationControllerSnapshot(animationControllerDocument),
                new OriginalSaveRuntimeParticleSnapshot(particleDocument),
                new OriginalSaveRuntimeTimerSnapshot(timerDocument),
                new OriginalSaveRuntimeOwnerAttributeSnapshot(objectManifest.OwnerAttributeTables),
                entities,
                entitiesById,
                referrersByObjectId,
                actions,
                actionsByOwnerId,
                actionAuxiliaryRecords,
                triggers,
                new OriginalSaveRuntimeAiSnapshot(aiDocument),
                new OriginalSaveRuntimeElfSnapshot(elfDocument),
                new OriginalSaveRuntimePlayerSnapshot(playerDocument),
                new OriginalSaveRuntimeStorySnapshot(storyDocument),
                new OriginalSaveRuntimeSelectionControlGroupSnapshot(controlGroupDocument),
                new OriginalSaveRuntimeSparetimeSnapshot(sparetimeDocument),
                new OriginalSaveRuntimeHorizonSnapshot(horizonDocument),
                new OriginalSaveRuntimeAdaptiveSoundMarkerSnapshot(adaptiveSoundMarkerDocument),
                new OriginalSaveRuntimeNewsTickerSnapshot(newsTickerDocument),
                new OriginalSaveRuntimeObjectIconSnapshot(objectIconDocument),
                new OriginalSaveRuntimeCaveRecordSnapshot(caveRecordDocument));
        }
    }

    public sealed class OriginalSaveRuntimeSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEntitySnapshot> _entities;
        private readonly ReadOnlyDictionary<int, OriginalSaveRuntimeEntitySnapshot> _entitiesById;
        private readonly ReadOnlyDictionary<int, IReadOnlyList<int>> _referrersByObjectId;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeActionSnapshot> _actions;
        private readonly ReadOnlyDictionary<int, OriginalSaveRuntimeActionSnapshot> _actionsByOwnerId;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeActionAuxiliarySnapshot>
            _actionAuxiliaryRecords;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeTriggerSnapshot?> _triggers;

        internal OriginalSaveRuntimeSnapshot(
            OriginalSaveArchiveDocument sourceDocument,
            OriginalSaveRuntimeWorldSnapshot runtimeWorld,
            OriginalSaveRuntimeClassLightSnapshot classLights,
            OriginalSaveRuntimeEnergySnapshot energy,
            OriginalSaveRuntimeAnimationControllerSnapshot animationControllers,
            OriginalSaveRuntimeParticleSnapshot particles,
            OriginalSaveRuntimeTimerSnapshot timers,
            OriginalSaveRuntimeOwnerAttributeSnapshot ownerAttributes,
            OriginalSaveRuntimeEntitySnapshot[] entities,
            Dictionary<int, OriginalSaveRuntimeEntitySnapshot> entitiesById,
            Dictionary<int, IReadOnlyList<int>> referrersByObjectId,
            OriginalSaveRuntimeActionSnapshot[] actions,
            Dictionary<int, OriginalSaveRuntimeActionSnapshot> actionsByOwnerId,
            OriginalSaveRuntimeActionAuxiliarySnapshot[] actionAuxiliaryRecords,
            OriginalSaveRuntimeTriggerSnapshot?[] triggers,
            OriginalSaveRuntimeAiSnapshot ai,
            OriginalSaveRuntimeElfSnapshot elf,
            OriginalSaveRuntimePlayerSnapshot players,
            OriginalSaveRuntimeStorySnapshot story,
            OriginalSaveRuntimeSelectionControlGroupSnapshot selectionControlGroups,
            OriginalSaveRuntimeSparetimeSnapshot sparetime,
            OriginalSaveRuntimeHorizonSnapshot horizon,
            OriginalSaveRuntimeAdaptiveSoundMarkerSnapshot adaptiveSoundMarkers,
            OriginalSaveRuntimeNewsTickerSnapshot newsTicker,
            OriginalSaveRuntimeObjectIconSnapshot objectIcons,
            OriginalSaveRuntimeCaveRecordSnapshot caveRecords)
        {
            SourceDocument = sourceDocument ?? throw new ArgumentNullException(nameof(sourceDocument));
            RuntimeWorld = runtimeWorld ?? throw new ArgumentNullException(nameof(runtimeWorld));
            ClassLights = classLights ?? throw new ArgumentNullException(nameof(classLights));
            Energy = energy ?? throw new ArgumentNullException(nameof(energy));
            AnimationControllers = animationControllers ??
                throw new ArgumentNullException(nameof(animationControllers));
            Particles = particles ?? throw new ArgumentNullException(nameof(particles));
            Timers = timers ?? throw new ArgumentNullException(nameof(timers));
            OwnerAttributes = ownerAttributes ?? throw new ArgumentNullException(nameof(ownerAttributes));
            _entities = Array.AsReadOnly(entities ?? throw new ArgumentNullException(nameof(entities)));
            _entitiesById = new ReadOnlyDictionary<int, OriginalSaveRuntimeEntitySnapshot>(
                entitiesById ?? throw new ArgumentNullException(nameof(entitiesById)));
            _referrersByObjectId = new ReadOnlyDictionary<int, IReadOnlyList<int>>(
                referrersByObjectId ?? throw new ArgumentNullException(nameof(referrersByObjectId)));
            _actions = Array.AsReadOnly(actions ?? throw new ArgumentNullException(nameof(actions)));
            _actionsByOwnerId = new ReadOnlyDictionary<int, OriginalSaveRuntimeActionSnapshot>(
                actionsByOwnerId ?? throw new ArgumentNullException(nameof(actionsByOwnerId)));
            _actionAuxiliaryRecords = Array.AsReadOnly(actionAuxiliaryRecords ??
                throw new ArgumentNullException(nameof(actionAuxiliaryRecords)));
            _triggers = Array.AsReadOnly(triggers ?? throw new ArgumentNullException(nameof(triggers)));
            Ai = ai ?? throw new ArgumentNullException(nameof(ai));
            Elf = elf ?? throw new ArgumentNullException(nameof(elf));
            Players = players ?? throw new ArgumentNullException(nameof(players));
            Story = story ?? throw new ArgumentNullException(nameof(story));
            SelectionControlGroups = selectionControlGroups ??
                throw new ArgumentNullException(nameof(selectionControlGroups));
            Sparetime = sparetime ?? throw new ArgumentNullException(nameof(sparetime));
            Horizon = horizon ?? throw new ArgumentNullException(nameof(horizon));
            AdaptiveSoundMarkers = adaptiveSoundMarkers ??
                throw new ArgumentNullException(nameof(adaptiveSoundMarkers));
            NewsTicker = newsTicker ?? throw new ArgumentNullException(nameof(newsTicker));
            ObjectIcons = objectIcons ?? throw new ArgumentNullException(nameof(objectIcons));
            CaveRecords = caveRecords ?? throw new ArgumentNullException(nameof(caveRecords));
        }

        public OriginalSaveArchiveDocument SourceDocument { get; }
        public OriginalSaveRuntimeWorldSnapshot RuntimeWorld { get; }
        public OriginalSaveRuntimeClassLightSnapshot ClassLights { get; }
        public OriginalSaveRuntimeEnergySnapshot Energy { get; }
        public OriginalSaveRuntimeAnimationControllerSnapshot AnimationControllers { get; }
        public OriginalSaveRuntimeParticleSnapshot Particles { get; }
        public OriginalSaveRuntimeTimerSnapshot Timers { get; }
        public OriginalSaveRuntimeOwnerAttributeSnapshot OwnerAttributes { get; }
        public double GameTime => SourceDocument.Metadata.GameTime;
        public OriginalSaveWorldDocument World => SourceDocument.World;
        public IReadOnlyList<OriginalSaveRuntimeEntitySnapshot> Entities => _entities;
        public IReadOnlyDictionary<int, OriginalSaveRuntimeEntitySnapshot> EntitiesById => _entitiesById;
        public IReadOnlyDictionary<int, IReadOnlyList<int>> ReferrersByObjectId => _referrersByObjectId;
        public IReadOnlyList<OriginalSaveRuntimeActionSnapshot> Actions => _actions;
        public IReadOnlyDictionary<int, OriginalSaveRuntimeActionSnapshot> ActionsByOwnerId =>
            _actionsByOwnerId;
        public IReadOnlyList<OriginalSaveRuntimeActionAuxiliarySnapshot> ActionAuxiliaryRecords =>
            _actionAuxiliaryRecords;
        public IReadOnlyList<OriginalSaveRuntimeTriggerSnapshot?> Triggers => _triggers;
        public OriginalSaveRuntimeAiSnapshot Ai { get; }
        public OriginalSaveRuntimeElfSnapshot Elf { get; }
        public OriginalSaveRuntimePlayerSnapshot Players { get; }
        public OriginalSaveRuntimeStorySnapshot Story { get; }
        public OriginalSaveRuntimeSelectionControlGroupSnapshot SelectionControlGroups { get; }
        public OriginalSaveRuntimeSparetimeSnapshot Sparetime { get; }
        public OriginalSaveRuntimeHorizonSnapshot Horizon { get; }
        public OriginalSaveRuntimeAdaptiveSoundMarkerSnapshot AdaptiveSoundMarkers { get; }
        public OriginalSaveRuntimeNewsTickerSnapshot NewsTicker { get; }
        public OriginalSaveRuntimeObjectIconSnapshot ObjectIcons { get; }
        public OriginalSaveRuntimeCaveRecordSnapshot CaveRecords { get; }
    }

    public sealed class OriginalSaveRuntimeOwnerAttributeSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeOwnerAttributeBlockSnapshot> _blocks;

        internal OriginalSaveRuntimeOwnerAttributeSnapshot(
            OriginalSaveOwnerAttributeTableDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var blocks = new OriginalSaveRuntimeOwnerAttributeBlockSnapshot[source.Blocks.Count];
            for (int index = 0; index < blocks.Length; index++)
                blocks[index] = new OriginalSaveRuntimeOwnerAttributeBlockSnapshot(source.Blocks[index]);
            _blocks = Array.AsReadOnly(blocks);
        }

        public IReadOnlyList<OriginalSaveRuntimeOwnerAttributeBlockSnapshot> Blocks => _blocks;
    }

    public sealed class OriginalSaveRuntimeOwnerAttributeBlockSnapshot
    {
        private readonly ReadOnlyCollection<int> _primaryValueBits;
        private readonly ReadOnlyCollection<int> _secondaryValueBits;

        internal OriginalSaveRuntimeOwnerAttributeBlockSnapshot(
            OriginalSaveOwnerAttributeBlockDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Owner = source.Owner;
            _primaryValueBits = CopyBits(source.PrimaryValueBits);
            _secondaryValueBits = CopyBits(source.SecondaryValueBits);
        }

        public int Owner { get; }
        public IReadOnlyList<int> PrimaryValueBits => _primaryValueBits;

        // An independent owner-attribute table carrying accumulator/timestamp-like state,
        // retained raw.
        public IReadOnlyList<int> SecondaryValueBits => _secondaryValueBits;

        private static ReadOnlyCollection<int> CopyBits(IReadOnlyList<int> source)
        {
            var copy = new int[source.Count];
            for (int index = 0; index < copy.Length; index++)
                copy[index] = source[index];
            return Array.AsReadOnly(copy);
        }
    }

    public sealed class OriginalSaveRuntimeAiSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeAiOwnerSnapshot> _owners;

        internal OriginalSaveRuntimeAiSnapshot(OriginalSaveAiDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var owners = new OriginalSaveRuntimeAiOwnerSnapshot[source.Owners.Count];
            for (int index = 0; index < owners.Length; index++)
                owners[index] = new OriginalSaveRuntimeAiOwnerSnapshot(source.Owners[index]);
            _owners = Array.AsReadOnly(owners);
        }

        public IReadOnlyList<OriginalSaveRuntimeAiOwnerSnapshot> Owners => _owners;
    }

    public sealed class OriginalSaveRuntimeAiOwnerSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveTclVariable> _variables;

        internal OriginalSaveRuntimeAiOwnerSnapshot(OriginalSaveAiOwnerDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Enabled = source.Enabled;
            Owner = source.Owner;
            ScriptName = source.ScriptName.Text;
            InterpreterPresent = source.InterpreterPresent;
            var variables = new OriginalSaveTclVariable[source.Variables.Count];
            for (int index = 0; index < variables.Length; index++)
                variables[index] = source.Variables[index];
            _variables = Array.AsReadOnly(variables);
        }

        public bool Enabled { get; }
        public int Owner { get; }
        public string ScriptName { get; }
        public bool InterpreterPresent { get; }
        public IReadOnlyList<OriginalSaveTclVariable> Variables => _variables;
    }

    public sealed class OriginalSaveRuntimeElfSnapshot
    {
        internal OriginalSaveRuntimeElfSnapshot(OriginalSaveElfDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            TextureVariant = source.TextureVariant;
            CurrentRegisteredAnimation = source.CurrentRegisteredAnimation;
            RequestedRegisteredAnimation = source.RequestedRegisteredAnimation;
            NativeModelDatabaseId = source.NativeModelDatabaseId;
            NativeMeshId = source.NativeMeshId;
            FrameAlpha = source.FrameAlpha.AsSingle;
            LookAtObjectId = source.LookAtObjectId;
            Position = source.Position;
            LeanA = source.LeanA.AsSingle;
            LeanB = source.LeanB.AsSingle;
            NativeState64Bits = source.NativeState64.Bits;
            Target = source.Target;
            Mode = source.Mode;
            DrawOverUi = source.DrawOverUi;
            AnimationHasMoreFrames = source.AnimationHasMoreFrames;
            FirstParticleSourceHandle = source.FirstParticleSourceHandle;
            SecondParticleSourceHandle = source.SecondParticleSourceHandle;
        }

        public int TextureVariant { get; }
        public int CurrentRegisteredAnimation { get; }
        public int RequestedRegisteredAnimation { get; }
        public int NativeModelDatabaseId { get; }
        public int NativeMeshId { get; }
        public float FrameAlpha { get; }
        public int LookAtObjectId { get; }
        public OriginalSaveVector3 Position { get; }
        public float LeanA { get; }
        public float LeanB { get; }
        public int NativeState64Bits { get; }
        public OriginalSaveVector3 Target { get; }
        public int Mode { get; }
        public bool DrawOverUi { get; }
        public bool AnimationHasMoreFrames { get; }
        public int FirstParticleSourceHandle { get; }
        public int SecondParticleSourceHandle { get; }
        public bool InferredVisible => NativeMeshId >= 0;
    }

    public sealed class OriginalSaveRuntimePlayerSnapshot
    {
        private readonly ReadOnlyCollection<int> _reservedFraming;
        private readonly ReadOnlyCollection<OriginalSaveRuntimePlayerSlotSnapshot> _slots;

        internal OriginalSaveRuntimePlayerSnapshot(OriginalSavePlayerDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var reserved = new int[source.ReservedFraming.Count];
            for (int index = 0; index < reserved.Length; index++)
                reserved[index] = source.ReservedFraming[index];
            _reservedFraming = Array.AsReadOnly(reserved);

            var slots = new OriginalSaveRuntimePlayerSlotSnapshot[source.Slots.Count];
            for (int index = 0; index < slots.Length; index++)
                slots[index] = new OriginalSaveRuntimePlayerSlotSnapshot(source.Slots[index]);
            _slots = Array.AsReadOnly(slots);
        }

        public IReadOnlyList<int> ReservedFraming => _reservedFraming;
        public IReadOnlyList<OriginalSaveRuntimePlayerSlotSnapshot> Slots => _slots;
    }

    public sealed class OriginalSaveRuntimePlayerSlotSnapshot
    {
        private readonly ReadOnlyCollection<int> _diplomacy;

        internal OriginalSaveRuntimePlayerSlotSnapshot(OriginalSavePlayerSlotDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            PlayerName = source.PlayerName.Text;
            SlotState = source.SlotState;
            PlayerOwnerId = source.PlayerOwnerId;
            ClanOwnerId = source.ClanOwnerId;
            SecondaryName = source.SecondaryName.Text;
            var diplomacy = new int[source.Diplomacy.Count];
            for (int index = 0; index < diplomacy.Length; index++)
                diplomacy[index] = source.Diplomacy[index];
            _diplomacy = Array.AsReadOnly(diplomacy);
        }

        public string PlayerName { get; }
        public int SlotState { get; }
        public int PlayerOwnerId { get; }
        public int ClanOwnerId { get; }
        public string SecondaryName { get; }
        public IReadOnlyList<int> Diplomacy => _diplomacy;
    }

    public sealed class OriginalSaveRuntimeTriggerSnapshot
    {
        private const int SupportedTargetTypeMask = (1 << 13) - 1;
        private static readonly string[] TargetTypeNames =
        {
            "dummy", "gnome", "monster", "material", "tool", "production", "energy",
            "protection", "store", "transport", "elevator", "baby", "info",
        };

        private readonly ReadOnlyCollection<string> _targetTypes;
        private readonly ReadOnlyCollection<string> _actuators;

        internal OriginalSaveRuntimeTriggerSnapshot(OriginalSaveTriggerRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Index = source.Index;
            Kind = source.Kind;
            Execution = source.Execution;
            OwnerObjectId = source.OwnerObjectId;
            FireScript = source.FireScript.Text;
            LastCheck = source.LastCheck;
            CheckTimer = source.CheckTimer;
            CheckIdle = source.CheckIdle;
            CheckNear = source.CheckNear;
            Active = source.Active;
            TargetRange = source.TargetRange.AsSingle;
            FireTime = source.FireTime;
            Interval = source.Interval;
            TargetObjectId = source.TargetObjectId;
            TargetClass = source.TargetClass.Text is "" or "?" ? null : source.TargetClass.Text;
            TargetTypeMask = source.TargetTypeMask;
            UnsupportedTargetTypeMask = source.TargetTypeMask & ~SupportedTargetTypeMask;
            var targetTypes = new List<string>();
            for (int bit = 0; bit < TargetTypeNames.Length; bit++)
            {
                if ((source.TargetTypeMask & (1 << bit)) != 0)
                    targetTypes.Add(TargetTypeNames[bit]);
            }
            _targetTypes = targetTypes.AsReadOnly();
            TargetOwnerSpec = source.TargetOwnerSpec.Text;
            TargetCount = source.TargetCount;
            CallbackScript = source.CallbackScript.Text;
            _actuators = TclList.Parse(source.Actuators.Text).AsReadOnly();
        }

        public int Index { get; }
        public int Kind { get; }
        public int Execution { get; }
        public int OwnerObjectId { get; }
        public string FireScript { get; }
        public double LastCheck { get; }
        public double CheckTimer { get; }
        public double CheckIdle { get; }
        public double CheckNear { get; }
        public bool Active { get; }
        public float TargetRange { get; }
        public double FireTime { get; }
        public double Interval { get; }
        public int TargetObjectId { get; }
        public string? TargetClass { get; }
        public int TargetTypeMask { get; }
        public int UnsupportedTargetTypeMask { get; }
        public IReadOnlyList<string> TargetTypes => _targetTypes;
        public string TargetOwnerSpec { get; }
        public int TargetCount { get; }
        public string CallbackScript { get; }
        public IReadOnlyList<string> Actuators => _actuators;

        internal TriggerRecord CreateRuntimeRecord(int sparseSlotIndex)
        {
            return new TriggerRecord
            {
                // The live lookup index comes from the sparse slot position: the native
                // loader reads the serialized record-index word without validating it
                // against the slot, so a live slot may carry -1 or a stale word. The raw
                // word stays retained on this snapshot for byte round-trip.
                Index = sparseSlotIndex,
                Kind = (TriggerKind)Kind,
                Execution = Execution,
                OwnerHandle = OwnerObjectId,
                FireScript = FireScript,
                LastCheck = LastCheck,
                CheckTimer = CheckTimer,
                CheckIdle = CheckIdle,
                CheckNear = CheckNear,
                Active = Active,
                TargetRange = TargetRange,
                FireTime = FireTime,
                Interval = Interval,
                TargetRef = TargetObjectId,
                TargetClass = TargetClass,
                TargetTypes = TargetTypes.Count == 0 ? null : new List<string>(TargetTypes),
                TargetOwnerSpec = string.IsNullOrEmpty(TargetOwnerSpec) ? null : TargetOwnerSpec,
                TargetCount = TargetCount,
                CallbackScript = CallbackScript,
                Actuators = new List<string>(Actuators),
            };
        }
    }

    public sealed class OriginalSaveRuntimeEntitySnapshot
    {
        private readonly ReadOnlyCollection<int> _childObjectIds;
        private readonly ReadOnlyCollection<string> _taskList;
        private readonly ReadOnlyCollection<int> _fogBrightnessCache;
        private readonly ReadOnlyCollection<OriginalSaveTclVariable> _tclVariables;
        private readonly ReadOnlyCollection<int> _attributePageBits;
        private readonly ReadOnlyCollection<int> _currentPartFrames;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeTextureAnimationSnapshot>
            _textureAnimations;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEntityEventSnapshot?> _eventQueueSlots;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEntityEventSnapshot> _delayedEvents;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEntityEventSnapshot> _pendingEvents;

        internal OriginalSaveRuntimeEntitySnapshot(
            OriginalSaveObjectDocument source,
            OriginalSaveRuntimeActionSnapshot? stagedAction)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            ObjectId = source.ObjectId;
            ClassName = source.ClassName;
            ObjectType = source.ObjectType;
            InstanceName = source.InstanceName;
            Position = source.Position;
            Velocity = source.Velocity;
            Rotation = source.Rotation;
            MotionSpeed = source.MotionSpeed;
            MotionDelta = source.MotionDelta;
            PreviousRotation = source.PreviousRotation;
            Owner = source.Owner;
            CreationTimeHours = source.CreationTimeHours;
            WorkStart = source.WorkStart;
            WorkLength = source.WorkLength;
            StateObjectId = source.StateObjectId;
            HasTclState = source.HasTclState;
            DisplayState = source.DisplayState;
            StagedAction = stagedAction;

            OriginalSaveObjectCommonStateDocument common = source.CommonState;
            // Native obj+0x200/+0x204/+0x208 (CObj::HandleStates): live/target per-class state
            // indices + the think-enable count. State200[3] (+0x20C) stays document-retained.
            StateLiveWord = common.State200[0];
            StateTargetWord = common.State200[1];
            StateEnabledWord = common.State200[2];
            FogOfWarSizeX = common.FogOfWarSizeX;
            FogOfWarSizeY = common.FogOfWarSizeY;
            FogOfWarLastX = common.FogOfWarLastX;
            FogOfWarLastY = common.FogOfWarLastY;
            FogOfWarLastZ = common.FogOfWarLastZ;
            // Per-object light (N22 words 5..9 + N23): offset XYZ, range, and the ×20 color.
            LightOffsetX = BitConverter.Int32BitsToSingle(common.State26C[5]);
            LightOffsetY = BitConverter.Int32BitsToSingle(common.State26C[6]);
            LightOffsetZ = BitConverter.Int32BitsToSingle(common.State26C[7]);
            LightRange = BitConverter.Int32BitsToSingle(common.State26C[9]);
            LightColorR = common.State298[0] / 20f;
            LightColorG = common.State298[1] / 20f;
            LightColorB = common.State298[2] / 20f;
            LinkedObjectId = common.LinkedObjectId;
            LinkedSlot = common.LinkedSlot;
            PlaceSnapMode = common.PlaceSnapMode;
            WalkResult = common.RouteState;
            WeaponClassId = common.State220[2];
            ShieldClassId = common.State220[3];
            PickTargetMode = source.PickTargetMode;
            ObjectFlags = common.ObjectFlags;
            DirtyFlags = common.DirtyFlags;
            DigOrderId = common.DigOrderId;
            TriggerRecordId = common.TriggerRecordId;
            InitialAnimationModelDatabaseSlot = common.State3BC[0];
            InitialAnimationState = common.State3BC[1];
            InitialAnimationStartFrame = common.State3BC[2];
            InitialAnimationMode = common.State3BC[3];
            InitialAnimationWasSeeded = common.State39C[2] != -1;
            RenderDirtyLatch = common.State3B4 != 0;
            ParticleState = new OriginalSaveRuntimeObjectParticleState(common.ParticleState);

            var currentPartFrames = new int[common.RouteValues.Count];
            for (int index = 0; index < currentPartFrames.Length; index++)
                currentPartFrames[index] = common.RouteValues[index];
            _currentPartFrames = Array.AsReadOnly(currentPartFrames);

            OriginalSaveObjectAttachmentHistoryDocument textureAnimations = source.AttachmentHistory;
            var animationSnapshots = new OriginalSaveRuntimeTextureAnimationSnapshot[
                textureAnimations.Records.Count];
            for (int index = 0; index < animationSnapshots.Length; index++)
            {
                animationSnapshots[index] = new OriginalSaveRuntimeTextureAnimationSnapshot(
                    textureAnimations.Records[index]);
            }
            _textureAnimations = Array.AsReadOnly(animationSnapshots);

            var attributePageBits = new int[common.GridState.Count];
            for (int index = 0; index < attributePageBits.Length; index++)
                attributePageBits[index] = common.GridState[index];
            _attributePageBits = Array.AsReadOnly(attributePageBits);

            OriginalSaveObjectEventQueueDocument eventQueue = source.EventQueueDocument;
            EventQueueReadCursor = eventQueue.ReadCursor;
            EventQueueWriteCursor = eventQueue.WriteCursor;
            var eventQueueSlots = new OriginalSaveRuntimeEntityEventSnapshot?[
                OriginalSaveObjectEventQueueDocument.Capacity];
            for (int slotIndex = 0; slotIndex < eventQueueSlots.Length; slotIndex++)
            {
                OriginalSaveTimerEventPayloadDocument? payload = eventQueue.Slots[slotIndex];
                if (payload == null) continue;
                eventQueueSlots[slotIndex] = new OriginalSaveRuntimeEntityEventSnapshot(
                    eventQueue.GetSlotPayloadOffset(slotIndex),
                    slotIndex,
                    isDelayed: false,
                    isDeliverable: eventQueue.IsPendingSlot(slotIndex),
                    source: payload);
            }
            _eventQueueSlots = Array.AsReadOnly(eventQueueSlots);

            var delayedEvents = new OriginalSaveRuntimeEntityEventSnapshot[
                eventQueue.DelayedRecords.Count];
            for (int delayedIndex = 0; delayedIndex < delayedEvents.Length; delayedIndex++)
            {
                delayedEvents[delayedIndex] = new OriginalSaveRuntimeEntityEventSnapshot(
                    eventQueue.GetDelayedPayloadOffset(delayedIndex),
                    null,
                    isDelayed: true,
                    isDeliverable: true,
                    source: eventQueue.DelayedRecords[delayedIndex]);
            }
            _delayedEvents = Array.AsReadOnly(delayedEvents);

            var pendingEvents = new OriginalSaveRuntimeEntityEventSnapshot[
                eventQueue.PendingWindowCount + delayedEvents.Length];
            for (int pendingIndex = 0; pendingIndex < eventQueue.PendingWindowCount; pendingIndex++)
            {
                int slotIndex = eventQueue.GetPendingSlotIndex(pendingIndex);
                pendingEvents[pendingIndex] = eventQueueSlots[slotIndex] ??
                    throw new InvalidOperationException(
                        $"Staged object {source.ObjectId} has an empty pending event slot " +
                        $"{slotIndex}.");
            }
            for (int delayedIndex = 0; delayedIndex < delayedEvents.Length; delayedIndex++)
            {
                pendingEvents[eventQueue.PendingWindowCount + delayedIndex] =
                    delayedEvents[delayedIndex];
            }
            _pendingEvents = Array.AsReadOnly(pendingEvents);

            var childObjectIds = new int[source.ChildObjectIds.Count];
            for (int index = 0; index < childObjectIds.Length; index++)
                childObjectIds[index] = source.ChildObjectIds[index];
            _childObjectIds = Array.AsReadOnly(childObjectIds);

            var taskList = new string[source.Strings.Count];
            for (int index = 0; index < taskList.Length; index++)
                taskList[index] = source.Strings[index];
            _taskList = Array.AsReadOnly(taskList);

            var fogBrightnessCache = new int[source.StateAfterStrings.Count];
            for (int index = 0; index < fogBrightnessCache.Length; index++)
                fogBrightnessCache[index] = source.StateAfterStrings[index];
            _fogBrightnessCache = Array.AsReadOnly(fogBrightnessCache);

            var tclVariables = new OriginalSaveTclVariable[source.TclVariables.Count];
            for (int index = 0; index < tclVariables.Length; index++)
                tclVariables[index] = source.TclVariables[index];
            _tclVariables = Array.AsReadOnly(tclVariables);

            // Type-1/11 social + planner extensions, decoded by the body-tail parser; the
            // snapshot carries the values immutably so the session stager can apply them.
            // The family {id,state} tri-state is kept verbatim as the save-side struct;
            // only linked (state 1) records materialize a Tcl variable.
            Family = source.FamilyExtension != null
                ? new OriginalSaveRuntimeEntityFamilySnapshot(source.FamilyExtension)
                : null;
            TalkIssues = source.TalkIssueExtension != null
                ? new OriginalSaveRuntimeEntityTalkIssueSnapshot(source.TalkIssueExtension)
                : null;
            Planner = source.PlannerExtension != null
                ? new OriginalSaveRuntimeEntityPlannerSnapshot(source.PlannerExtension)
                : null;
            Production = source.ProductionExtension != null
                ? new OriginalSaveRuntimeEntityProductionSnapshot(source.ProductionExtension)
                : null;
        }

        public int ObjectId { get; }
        public string ClassName { get; }
        public int ObjectType { get; }
        public string InstanceName { get; }
        public OriginalSaveVector3 Position { get; }
        public OriginalSaveVector3 Velocity { get; }
        public OriginalSaveVector3 Rotation { get; }
        public float MotionSpeed { get; }
        public OriginalSaveVector3 MotionDelta { get; }
        public OriginalSaveVector3 PreviousRotation { get; }
        public int Owner { get; }
        public float CreationTimeHours { get; }
        public float WorkStart { get; }
        public float WorkLength { get; }
        public int StateObjectId { get; }
        public bool HasTclState { get; }
        public string DisplayState { get; }
        public int StateLiveWord { get; }
        public int StateTargetWord { get; }
        public int StateEnabledWord { get; }
        public int FogOfWarSizeX { get; }
        public int FogOfWarSizeY { get; }
        public float FogOfWarLastX { get; }
        public float FogOfWarLastY { get; }
        public float FogOfWarLastZ { get; }
        public float LightOffsetX { get; }
        public float LightOffsetY { get; }
        public float LightOffsetZ { get; }
        public float LightRange { get; }
        public float LightColorR { get; }
        public float LightColorG { get; }
        public float LightColorB { get; }
        public int LinkedObjectId { get; }
        public int LinkedSlot { get; }
        public int PlaceSnapMode { get; }
        public int WalkResult { get; }
        public int WeaponClassId { get; }
        public int ShieldClassId { get; }
        public int PickTargetMode { get; }
        public int ObjectFlags { get; }
        public int DirtyFlags { get; }
        public int DigOrderId { get; }
        public int TriggerRecordId { get; }
        public int InitialAnimationModelDatabaseSlot { get; }
        public int InitialAnimationState { get; }
        public int InitialAnimationStartFrame { get; }
        public int InitialAnimationMode { get; }
        public bool InitialAnimationWasSeeded { get; }
        public bool RenderDirtyLatch { get; }
        public OriginalSaveRuntimeObjectParticleState ParticleState { get; }
        public OriginalSaveRuntimeActionSnapshot? StagedAction { get; }
        public IReadOnlyList<int> ChildObjectIds => _childObjectIds;
        public IReadOnlyList<string> TaskList => _taskList;
        public IReadOnlyList<int> FogBrightnessCache => _fogBrightnessCache;
        public IReadOnlyList<OriginalSaveTclVariable> TclVariables => _tclVariables;
        public IReadOnlyList<int> AttributePageBits => _attributePageBits;
        public IReadOnlyList<int> CurrentPartFrames => _currentPartFrames;
        public IReadOnlyList<OriginalSaveRuntimeTextureAnimationSnapshot> TextureAnimations =>
            _textureAnimations;
        public int EventQueueReadCursor { get; }
        public int EventQueueWriteCursor { get; }
        public IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot?> EventQueueSlots =>
            _eventQueueSlots;
        public IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot> DelayedEvents => _delayedEvents;
        public IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot> PendingEvents => _pendingEvents;

        // Type-1/11 family graph (null unless the body carried the extension).
        public OriginalSaveRuntimeEntityFamilySnapshot? Family { get; }
        // Type-1 talkissue ledger + running desire sum (null unless the body carried the extension).
        public OriginalSaveRuntimeEntityTalkIssueSnapshot? TalkIssues { get; }
        // Type-1 production-planner gnome sub-state (null unless the body carried the extension).
        public OriginalSaveRuntimeEntityPlannerSnapshot? Planner { get; }
        // Types 5/6/7/10 CProd state (null unless the body carried the extension).
        public OriginalSaveRuntimeEntityProductionSnapshot? Production { get; }
    }

    public sealed class OriginalSaveRuntimeTextureAnimationSnapshot
    {
        private readonly ReadOnlyCollection<int> _frameValues;

        internal OriginalSaveRuntimeTextureAnimationSnapshot(
            OriginalSaveObjectAttachmentHistoryRecordDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var frameValues = new int[source.RouteValues.Count];
            for (int index = 0; index < frameValues.Length; index++)
                frameValues[index] = source.RouteValues[index];
            _frameValues = Array.AsReadOnly(frameValues);
            Cursor = source.TrailingValue1;
            PlaybackMode = source.TrailingValue2;
        }

        public IReadOnlyList<int> FrameValues => _frameValues;
        public int Cursor { get; }
        public int PlaybackMode { get; }
    }

    public sealed class OriginalSaveRuntimeEntityProductionSlotSnapshot
    {
        internal OriginalSaveRuntimeEntityProductionSlotSnapshot(
            OriginalSaveObjectProductionSlotDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RegistryValue1 = source.RegistryValue1;
            RegistryValue2 = source.RegistryValue2;
            ClassName = source.ClassName;
            ResourceName = source.ResourceName;
            OrderCount = source.OrderCount;
            DemandBits = source.Demand.Bits;
            Invented = source.Invented;
            Buildable = source.Buildable;
        }

        public int RegistryValue1 { get; }
        public int RegistryValue2 { get; }
        public string ClassName { get; }
        public string ResourceName { get; }
        public int OrderCount { get; }
        public int DemandBits { get; }
        public float Demand => BitConverter.Int32BitsToSingle(DemandBits);
        public byte Invented { get; }
        public byte Buildable { get; }
    }

    public sealed class OriginalSaveRuntimeEntityProductionGuestSeatSnapshot
    {
        internal OriginalSaveRuntimeEntityProductionGuestSeatSnapshot(
            OriginalSaveObjectProductionGuestSeatDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SeatLinkId = source.SeatLinkId;
            GuestObjectId = source.GuestObjectId;
            OrderCount = source.OrderCount;
        }

        public int SeatLinkId { get; }
        public int GuestObjectId { get; }
        public int OrderCount { get; }
    }

    public sealed class OriginalSaveRuntimeEntityProductionSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEntityProductionSlotSnapshot> _slots;
        private readonly ReadOnlyCollection<byte> _requestBytes;
        private readonly ReadOnlyCollection<int> _pendingPositionBits;
        private readonly ReadOnlyCollection<byte> _flowBytes;
        private readonly ReadOnlyCollection<int> _productionValues;
        private readonly ReadOnlyCollection<int> _energyValues;
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEntityProductionGuestSeatSnapshot>
            _guestSeats;

        internal OriginalSaveRuntimeEntityProductionSnapshot(
            OriginalSaveObjectProductionExtensionDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var slots = new OriginalSaveRuntimeEntityProductionSlotSnapshot[source.Slots.Count];
            for (int index = 0; index < slots.Length; index++)
                slots[index] = new OriginalSaveRuntimeEntityProductionSlotSnapshot(
                    source.Slots[index]);
            _slots = Array.AsReadOnly(slots);

            WorkState = source.WorkState;
            AssignedWorkerObjectId = source.AssignedWorkerObjectId;
            _requestBytes = Copy(source.RequestBytes);
            _pendingPositionBits = Copy(source.PendingPositionBits);
            _flowBytes = Copy(source.FlowBytes);
            _productionValues = Copy(source.ProductionValues);
            ProductionEnabled = source.ProductionEnabled;
            _energyValues = Copy(source.EnergyValues);

            var guestSeats = new OriginalSaveRuntimeEntityProductionGuestSeatSnapshot[
                source.GuestSeats.Count];
            for (int index = 0; index < guestSeats.Length; index++)
            {
                guestSeats[index] = new OriginalSaveRuntimeEntityProductionGuestSeatSnapshot(
                    source.GuestSeats[index]);
            }
            _guestSeats = Array.AsReadOnly(guestSeats);
            State3C = source.State3C;
            State140 = source.State140;
        }

        private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> source)
        {
            var values = new T[source.Count];
            for (int index = 0; index < values.Length; index++) values[index] = source[index];
            return Array.AsReadOnly(values);
        }

        public IReadOnlyList<OriginalSaveRuntimeEntityProductionSlotSnapshot> Slots => _slots;
        public int WorkState { get; }
        public int AssignedWorkerObjectId { get; }
        public IReadOnlyList<byte> RequestBytes => _requestBytes;
        public byte UnpackRequestedByte => _requestBytes[0];
        public byte PackRequestedByte => _requestBytes[1];
        public byte BuildupRequestedByte => _requestBytes[2];
        public byte UnpackFlagByte => _requestBytes[3];
        public byte PackFlagByte => _requestBytes[4];
        public byte PlacementVariantByte => _requestBytes[5];
        public byte RepeatedUnpackRequestedByte => _requestBytes[6];
        public IReadOnlyList<int> PendingPositionBits => _pendingPositionBits;
        public int PendingPositionXBits => _pendingPositionBits[0];
        public int PendingPositionYBits => _pendingPositionBits[1];
        public int PendingPositionZBits => _pendingPositionBits[2];
        public float PendingPositionX => BitConverter.Int32BitsToSingle(PendingPositionXBits);
        public float PendingPositionY => BitConverter.Int32BitsToSingle(PendingPositionYBits);
        public float PendingPositionZ => BitConverter.Int32BitsToSingle(PendingPositionZBits);
        public IReadOnlyList<byte> FlowBytes => _flowBytes;
        public byte SwitchMode => _flowBytes[2];
        public byte ExclusiveMode => _flowBytes[4];
        public IReadOnlyList<int> ProductionValues => _productionValues;
        public int CurrentProductStamp => _productionValues[0];
        public int ProgressCurrent => _productionValues[1];
        public int ProgressMaximum => _productionValues[2];
        public int InventionCandidateSlot => _productionValues[3];
        public byte ProductionEnabled { get; }
        public IReadOnlyList<int> EnergyValues => _energyValues;
        public int EnergyClass => _energyValues[0];
        public int EnergyConsumptionBits => _energyValues[1];
        public int EnergyStoreBits => _energyValues[2];
        public int EnergyMaximumStoreBits => _energyValues[3];
        public int EnergyCurrentLoadBits => _energyValues[4];
        public float EnergyConsumption => BitConverter.Int32BitsToSingle(EnergyConsumptionBits);
        public float EnergyStore => BitConverter.Int32BitsToSingle(EnergyStoreBits);
        public float EnergyMaximumStore => BitConverter.Int32BitsToSingle(EnergyMaximumStoreBits);
        public float EnergyCurrentLoad => BitConverter.Int32BitsToSingle(EnergyCurrentLoadBits);
        public IReadOnlyList<OriginalSaveRuntimeEntityProductionGuestSeatSnapshot> GuestSeats =>
            _guestSeats;
        public byte State3C { get; }
        public byte State140 { get; }
    }

    // One restored family actor link (partner/mother/father). The {id,state} pair is the
    // save-side tri-state, kept verbatim; the reference word is retained for write-back.
    public sealed class OriginalSaveRuntimeEntityFamilyLink
    {
        internal OriginalSaveRuntimeEntityFamilyLink(int id, int state, int referenceObjectId)
        {
            Id = id;
            State = state;
            ReferenceObjectId = referenceObjectId;
        }

        public int Id { get; }
        // 0 = never set, 1 = linked/alive, 2 = target died.
        public int State { get; }
        public int ReferenceObjectId { get; }
    }

    public sealed class OriginalSaveRuntimeEntityFamilySnapshot
    {
        private readonly ReadOnlyCollection<string> _childrenNames;

        internal OriginalSaveRuntimeEntityFamilySnapshot(
            OriginalSaveObjectFamilyExtensionDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Partner = Link(source.PartnerLink);
            Mother = Link(source.MotherLink);
            Father = Link(source.FatherLink);
            var childrenNames = new string[source.ChildrenNames.Count];
            for (int index = 0; index < childrenNames.Length; index++)
                childrenNames[index] = source.ChildrenNames[index];
            _childrenNames = Array.AsReadOnly(childrenNames);
            PregnancyFlag = source.PregnancyFlag;
        }

        private static OriginalSaveRuntimeEntityFamilyLink Link(
            OriginalSaveObjectActorLinkDocument link) =>
            new OriginalSaveRuntimeEntityFamilyLink(link.Id, link.State, link.ReferenceObjectId);

        public OriginalSaveRuntimeEntityFamilyLink Partner { get; }
        public OriginalSaveRuntimeEntityFamilyLink Mother { get; }
        public OriginalSaveRuntimeEntityFamilyLink Father { get; }
        public IReadOnlyList<string> ChildrenNames => _childrenNames;
        public bool PregnancyFlag { get; }
    }

    public sealed class OriginalSaveRuntimeEntityTalkIssue
    {
        internal OriginalSaveRuntimeEntityTalkIssue(string name, int weightBits)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            WeightBits = weightBits;
        }

        public string Name { get; }
        public int WeightBits { get; }
        public float Weight => BitConverter.Int32BitsToSingle(WeightBits);
    }

    public sealed class OriginalSaveRuntimeEntityTalkIssueSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEntityTalkIssue> _entries;

        internal OriginalSaveRuntimeEntityTalkIssueSnapshot(
            OriginalSaveObjectTalkIssueExtensionDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SumBits = source.TalkDesireSum.Bits;
            var entries = new OriginalSaveRuntimeEntityTalkIssue[source.Entries.Count];
            for (int index = 0; index < entries.Length; index++)
                entries[index] = new OriginalSaveRuntimeEntityTalkIssue(
                    source.Entries[index].Name,
                    source.Entries[index].Weight.Bits);
            _entries = Array.AsReadOnly(entries);
        }

        // The stored running desire sum (native sub+0x18) — persisted, not Σ(weights).
        public int SumBits { get; }
        public float Sum => BitConverter.Int32BitsToSingle(SumBits);
        public IReadOnlyList<OriginalSaveRuntimeEntityTalkIssue> Entries => _entries;
    }

    public sealed class OriginalSaveRuntimeEntityPlannerSnapshot
    {
        internal OriginalSaveRuntimeEntityPlannerSnapshot(
            OriginalSaveObjectPlannerExtensionDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            IdleAnnounceFlag = source.IdleAnnounceFlag;
            IdleSinceBits = source.IdleSince.Bits;
            CurrentWorkplaceObjectId = source.CurrentWorkplaceObjectId;
            AssignedWorkplaceObjectId = source.AssignedWorkplaceObjectId;
        }

        // native sub+0x34 (0/1): 1 => the gnome was offering itself to the auto-work pool.
        public int IdleAnnounceFlag { get; }
        // native sub+0x38: idle-since / last-dispatch game time (float seconds).
        public int IdleSinceBits { get; }
        public float IdleSince => BitConverter.Int32BitsToSingle(IdleSinceBits);
        // native sub+0x3C: current workplace object id (-1 none); dangling ids tolerated.
        public int CurrentWorkplaceObjectId { get; }
        // native sub+0x40: assigned "home" workplace object id (-1 none); dangling tolerated.
        public int AssignedWorkplaceObjectId { get; }
    }

    public sealed class OriginalSaveRuntimeEntityEventSnapshot
    {
        private readonly byte[] _fixedEventName;
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _arguments;

        internal OriginalSaveRuntimeEntityEventSnapshot(
            long sourceOffset,
            int? ringSlotIndex,
            bool isDelayed,
            bool isDeliverable,
            OriginalSaveTimerEventPayloadDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (isDelayed == ringSlotIndex.HasValue)
            {
                throw new ArgumentException(
                    "A staged object event must identify either a ring slot or a delayed record.",
                    nameof(ringSlotIndex));
            }
            if (ringSlotIndex.HasValue &&
                (uint)ringSlotIndex.Value >= OriginalSaveObjectEventQueueDocument.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(ringSlotIndex));
            }
            if (isDelayed && !isDeliverable)
                throw new ArgumentException("A delayed object event is pending work.", nameof(isDeliverable));

            SourceOffset = sourceOffset;
            RingSlotIndex = ringSlotIndex;
            IsDelayed = isDelayed;
            IsDeliverable = isDeliverable;
            _fixedEventName = source.CopyFixedEventName();
            EventName = source.EventName;
            OwnerObjectId = source.OwnerObjectId;
            Subject1ObjectId = source.Subject1ObjectId;
            Subject2ObjectId = source.Subject2ObjectId;
            Subject3ObjectId = source.Subject3ObjectId;
            _arguments = Array.AsReadOnly(
                new[]
                {
                    source.Position1X,
                    source.Position1Y,
                    source.Position1Z,
                    source.Position2X,
                    source.Position2Y,
                    source.Position2Z,
                    source.Position3X,
                    source.Position3Y,
                    source.Position3Z,
                });
            Text1 = new OriginalSaveNativeString(source.Text1.CopyBytesIncludingTerminator());
            Text2 = new OriginalSaveNativeString(source.Text2.CopyBytesIncludingTerminator());
            Word1 = source.Number1;
            Word2 = source.Number2;
            Word3 = source.Number3;
            DueTime = source.CreatedAt;
        }

        public long SourceOffset { get; }
        public int? RingSlotIndex { get; }
        public bool IsDelayed { get; }
        public bool IsDeliverable { get; }
        public string EventName { get; }
        public int OwnerObjectId { get; }
        public int Subject1ObjectId { get; }
        public int Subject2ObjectId { get; }
        public int Subject3ObjectId { get; }
        public IReadOnlyList<OriginalSaveRaw32> Arguments => _arguments;
        public OriginalSaveNativeString Text1 { get; }
        public OriginalSaveNativeString Text2 { get; }
        public OriginalSaveRaw32 Word1 { get; }
        public OriginalSaveRaw32 Word2 { get; }
        public OriginalSaveRaw32 Word3 { get; }
        public OriginalSaveRaw64 DueTime { get; }

        public byte[] CopyFixedEventName() => (byte[])_fixedEventName.Clone();

        internal OriginalSaveTimerEventPayloadDocument CreatePayloadDocument()
        {
            var positions = new OriginalSaveRaw32[_arguments.Count];
            for (int index = 0; index < positions.Length; index++)
                positions[index] = _arguments[index];
            return new OriginalSaveTimerEventPayloadDocument(
                CopyFixedEventName(),
                OwnerObjectId,
                Subject1ObjectId,
                Subject2ObjectId,
                Subject3ObjectId,
                positions,
                new OriginalSaveNativeString(Text1.CopyBytesIncludingTerminator()),
                new OriginalSaveNativeString(Text2.CopyBytesIncludingTerminator()),
                Word1,
                Word2,
                Word3,
                DueTime);
        }
    }

    public sealed class OriginalSaveRuntimeActionSnapshot
    {
        private readonly byte[] _rawPayload;

        internal OriginalSaveRuntimeActionSnapshot(OriginalSaveActionRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            OwnerObjectId = source.OwnerObjectId;
            Type = source.Type;
            Payload = source.Payload;
            _rawPayload = source.CopyRawPayload();
        }

        public int OwnerObjectId { get; }
        public OriginalSaveActionType Type { get; }
        public OriginalSaveActionPayload Payload { get; }
        public OriginalSaveActionCommonState? Common => Payload.Common;
        public bool HasActiveAction =>
            Type != OriginalSaveActionType.Null && Type != OriginalSaveActionType.LegacySlide;

        public byte[] CopyRawPayload() => (byte[])_rawPayload.Clone();

        public double? AbsoluteDeadline => Payload switch
        {
            OriginalSaveWaitActionPayload wait => wait.AbsoluteDeadline,
            OriginalSaveAnimActionPayload animation => animation.AbsoluteDeadline,
            OriginalSaveAttackActionPayload attack => attack.AbsoluteDeadline,
            OriginalSaveAttackBallisticActionPayload ballistic => ballistic.AbsoluteDeadline,
            _ => null,
        };

        public double RemainingSecondsAt(double gameTime)
        {
            if (double.IsNaN(gameTime) || double.IsInfinity(gameTime))
                throw new ArgumentOutOfRangeException(nameof(gameTime));
            return AbsoluteDeadline is double deadline
                ? Math.Max(0d, deadline - gameTime)
                : 0d;
        }
    }

    public sealed class OriginalSaveRuntimeActionAuxiliarySnapshot
    {
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _vectors;
        private readonly ReadOnlyCollection<int> _trailingValues;

        internal OriginalSaveRuntimeActionAuxiliarySnapshot(
            OriginalSaveActionAuxiliaryRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Value = source.Value;
            var vectors = new IReadOnlyList<int>[source.Vectors.Count];
            for (int vector = 0; vector < vectors.Length; vector++)
            {
                var values = new int[source.Vectors[vector].Values.Count];
                for (int index = 0; index < values.Length; index++)
                    values[index] = source.Vectors[vector].Values[index];
                vectors[vector] = Array.AsReadOnly(values);
            }
            _vectors = Array.AsReadOnly(vectors);

            var trailingValues = new int[source.TrailingValues.Count];
            for (int index = 0; index < trailingValues.Length; index++)
                trailingValues[index] = source.TrailingValues[index];
            _trailingValues = Array.AsReadOnly(trailingValues);
        }

        public int Value { get; }
        public IReadOnlyList<IReadOnlyList<int>> Vectors => _vectors;
        public IReadOnlyList<int> TrailingValues => _trailingValues;
    }
}
