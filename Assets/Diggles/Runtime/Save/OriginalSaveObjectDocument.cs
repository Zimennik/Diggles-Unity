#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Text;

    public sealed class OriginalSaveObjectCollection : IReadOnlyList<OriginalSaveObjectDocument>
    {
        private const int MaximumTailEntryCount = 4 * 1024 * 1024;

        readonly byte[] _payload;
        readonly OriginalSaveObjectManifest _manifest;
        readonly OriginalSaveObjectDocument?[] _cache;

        internal OriginalSaveObjectCollection(byte[] payload, OriginalSaveObjectManifest manifest)
        {
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _cache = new OriginalSaveObjectDocument[manifest.Bodies.Count];
        }

        public int Count => _cache.Length;

        public OriginalSaveObjectDocument this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return _cache[index] ??= ReadObject(index);
            }
        }

        public OriginalSaveObjectDocument? FindById(int objectId)
        {
            int low = 0;
            int high = Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                int candidate = _manifest.Bodies[middle].ObjectId;
                if (candidate == objectId) return this[middle];
                if (candidate < objectId) low = middle + 1;
                else high = middle - 1;
            }
            return null;
        }

        public IEnumerator<OriginalSaveObjectDocument> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        OriginalSaveObjectDocument ReadObject(int index)
        {
            OriginalSaveObjectBodySummary summary = _manifest.Bodies[index];
            int start = checked((int)summary.Offset);
            int end = checked((int)(summary.Offset + summary.Size));
            var cursor = new OriginalSavePayloadCursor(_payload, start, end);

            string className = cursor.ReadNativeString();
            int objectType = cursor.ReadInt32();
            var position = new OriginalSaveVector3(
                cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
            var transientPosition = new OriginalSaveVector3(
                cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());

            var state350 = new int[3];
            cursor.ReadInt32Values(state350);
            var coreState = new int[13];
            cursor.ReadInt32Values(coreState);

            int[] childObjectIds = cursor.ReadInt32Vector();
            string[] strings = cursor.ReadStringVector();
            int[] stateAfterStrings = cursor.ReadFixedInt32Values(16);

            int tclPresence = cursor.ReadInt32();
            bool hasTclState;
            OriginalSaveTclVariable[] tclVariables;
            if (tclPresence == 0)
            {
                hasTclState = false;
                tclVariables = Array.Empty<OriginalSaveTclVariable>();
            }
            else if (tclPresence == 1)
            {
                hasTclState = true;
                int variableCount = cursor.ReadCount();
                tclVariables = new OriginalSaveTclVariable[variableCount];
                for (int variable = 0; variable < variableCount; variable++)
                {
                    tclVariables[variable] = new OriginalSaveTclVariable(
                        cursor.ReadNativeString(), cursor.ReadNativeString());
                }
            }
            else
            {
                throw new InvalidDataException(
                    $"Invalid Tcl state presence {tclPresence} in original object {summary.ObjectId}.");
            }

            int stateObjectId = cursor.ReadInt32();
            string instanceName = cursor.ReadNativeString();

            int[] state200 = cursor.ReadFixedInt32Values(4);
            int[] state220 = cursor.ReadFixedInt32Values(4);
            int[] routeValues = cursor.ReadInt32Vector();
            int routeState = cursor.ReadInt32();
            int routeTimeBits = cursor.ReadInt32();

            int gridPresence = cursor.ReadInt32();
            int[] gridState = gridPresence == 0
                ? Array.Empty<int>()
                : cursor.ReadFixedInt32Values(512);

            int[] state264 = cursor.ReadFixedInt32Values(2);
            int[] state254 = cursor.ReadFixedInt32Values(4);
            int[] state26C = cursor.ReadFixedInt32Values(10);
            int[] state298 = cursor.ReadFixedInt32Values(3);

            bool hasParticleState = cursor.ReadBooleanByte("object particle state presence");
            int[] particleState = hasParticleState
                ? cursor.ReadFixedInt32Values(160)
                : Array.Empty<int>();

            byte state2B4 = cursor.ReadByte();
            byte state35C = cursor.ReadByte();
            byte state35D = cursor.ReadByte();
            string displayState = cursor.ReadNativeString();

            int state364 = cursor.ReadInt32();
            int state368 = cursor.ReadInt32();
            int owner = cursor.ReadInt32();
            int state378 = cursor.ReadInt32();
            var state37C = new int[8];
            cursor.ReadInt32Values(state37C);
            var state39C = new int[5];
            cursor.ReadInt32Values(state39C);
            byte state3B4 = cursor.ReadByte();
            var state3BC = new int[4];
            cursor.ReadInt32Values(state3BC);
            var lateCoreState = new int[6];
            cursor.ReadInt32Values(lateCoreState);
            int state2A4 = cursor.ReadInt32();
            long opaqueTailOffset = cursor.Position;

            OriginalSaveObjectFamilyExtensionDocument? familyExtension;
            OriginalSaveObjectTalkIssueExtensionDocument? talkIssueExtension;
            OriginalSaveObjectProductionExtensionDocument? productionExtension;
            OriginalSaveObjectPlannerExtensionDocument? plannerExtension;
            OriginalSaveObjectAttachmentHistoryDocument attachmentHistory;
            OriginalSaveObjectRetiredGroupsDocument retiredGroups;
            OriginalSaveObjectTailFlagsDocument tailFlags;
            OriginalSaveObjectActionVariantDocument? actionVariant;
            OriginalSaveObjectEventQueueDocument eventQueueDocument;
            int pickTargetMode;
            byte destroyQueued;
            long tailParseEndOffset;
            try
            {
                familyExtension = objectType is 1 or 11
                    ? ReadFamilyExtension(cursor, summary.ObjectId)
                    : null;
                talkIssueExtension = objectType == 1
                    ? ReadTalkIssueExtension(cursor, summary.ObjectId)
                    : null;
                productionExtension = objectType is 5 or 6 or 7 or 10
                    ? ReadProductionExtension(cursor, summary.ObjectId)
                    : null;
                plannerExtension = objectType == 1
                    ? new OriginalSaveObjectPlannerExtensionDocument(
                        cursor.ReadFixedInt32Values(4))
                    : null;

                attachmentHistory = ReadAttachmentHistory(cursor, summary.ObjectId);
                retiredGroups = new OriginalSaveObjectRetiredGroupsDocument(
                    cursor.ReadFixedInt32Values(16),
                    cursor.ReadBytes(16),
                    cursor.ReadFixedInt32Values(16));
                tailFlags = new OriginalSaveObjectTailFlagsDocument(
                    cursor.ReadByte(),
                    cursor.ReadInt32(),
                    cursor.ReadByte());
                actionVariant = cursor.ReadBooleanByte(
                    "action variant presence", summary.ObjectId)
                    ? ReadActionVariant(cursor, summary.ObjectId)
                    : null;
                eventQueueDocument = ReadEventQueue(cursor, summary.ObjectId);

                int pickTargetModeOffset = cursor.Position;
                pickTargetMode = cursor.ReadInt32();
                if (pickTargetMode is < 0 or > 2)
                {
                    throw new InvalidDataException(
                        $"Invalid pick/target mode {pickTargetMode} in original object " +
                        $"{summary.ObjectId} at offset {pickTargetModeOffset}.");
                }

                destroyQueued = cursor.ReadByte();
                tailParseEndOffset = cursor.Position;
                if (tailParseEndOffset != end)
                {
                    throw new InvalidDataException(
                        $"Original object {summary.ObjectId} body ended at {tailParseEndOffset}, " +
                        $"expected {end}.");
                }
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException(
                    $"Original object {summary.ObjectId} tail exceeded its body boundary at " +
                    $"{cursor.Position}; expected end {end}.",
                    exception);
            }

            var commonState = new OriginalSaveObjectCommonStateDocument(
                state200,
                state220,
                routeValues,
                routeState,
                routeTimeBits,
                gridState,
                state264,
                state254,
                state26C,
                state298,
                particleState,
                state2B4,
                state35C,
                state35D,
                displayState,
                state364,
                state368,
                owner,
                state378,
                state37C,
                state39C,
                state3B4,
                state3BC,
                lateCoreState,
                state2A4);

            return new OriginalSaveObjectDocument(
                summary.ObjectId,
                className,
                objectType,
                position,
                transientPosition,
                state350,
                coreState,
                childObjectIds,
                strings,
                tclVariables,
                stateObjectId,
                instanceName,
                commonState,
                stateAfterStrings,
                hasTclState,
                summary.Offset,
                summary.Size,
                opaqueTailOffset,
                familyExtension,
                talkIssueExtension,
                productionExtension,
                plannerExtension,
                attachmentHistory,
                retiredGroups,
                tailFlags,
                actionVariant,
                eventQueueDocument,
                pickTargetMode,
                destroyQueued,
                tailParseEndOffset);
        }

        private static OriginalSaveObjectFamilyExtensionDocument ReadFamilyExtension(
            OriginalSavePayloadCursor cursor,
            int objectId)
        {
            var links = new OriginalSaveObjectActorLinkDocument[3];
            for (int index = 0; index < links.Length; index++)
            {
                links[index] = new OriginalSaveObjectActorLinkDocument(
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32());
            }

            return new OriginalSaveObjectFamilyExtensionDocument(
                links[0],
                links[1],
                links[2],
                cursor.ReadStringVector("family child", objectId),
                cursor.ReadBooleanByte("family pregnancy flag", objectId));
        }

        private static OriginalSaveObjectTalkIssueExtensionDocument ReadTalkIssueExtension(
            OriginalSavePayloadCursor cursor,
            int objectId)
        {
            int talkDesireSumBits = cursor.ReadInt32();
            var entries = new List<OriginalSaveObjectTalkIssueEntryDocument>();
            int sentinelOffset = cursor.Position;
            int sentinel = cursor.ReadInt32();
            while (sentinel != -1)
            {
                if (sentinel != 1)
                {
                    throw new InvalidDataException(
                        $"Invalid talk-issue sentinel {sentinel} in original object {objectId} " +
                        $"at offset {sentinelOffset}.");
                }
                if (entries.Count >= MaximumTailEntryCount)
                {
                    throw new InvalidDataException(
                        $"Invalid talk-issue entry count in original object {objectId} at " +
                        $"offset {sentinelOffset}.");
                }

                entries.Add(new OriginalSaveObjectTalkIssueEntryDocument(
                    cursor.ReadNativeString("talk-issue name", objectId),
                    cursor.ReadInt32()));
                sentinelOffset = cursor.Position;
                sentinel = cursor.ReadInt32();
            }

            return new OriginalSaveObjectTalkIssueExtensionDocument(
                talkDesireSumBits,
                entries.ToArray());
        }

        private static OriginalSaveObjectProductionExtensionDocument ReadProductionExtension(
            OriginalSavePayloadCursor cursor,
            int objectId)
        {
            int slotCount = cursor.ReadCount("production slot", objectId);
            var slots = new OriginalSaveObjectProductionSlotDocument[slotCount];
            for (int index = 0; index < slots.Length; index++)
            {
                slots[index] = new OriginalSaveObjectProductionSlotDocument(
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadNativeString("production slot class", objectId),
                    cursor.ReadNativeString("production slot resource", objectId),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadByte(),
                    cursor.ReadByte());
            }

            int workState = cursor.ReadInt32();
            int assignedWorkerObjectId = cursor.ReadInt32();
            byte[] requestBytes = cursor.ReadBytes(7);
            int[] pendingPositionBits = cursor.ReadFixedInt32Values(3);
            byte[] flowBytes = cursor.ReadBytes(8);
            int[] productionValues = cursor.ReadFixedInt32Values(4);
            byte productionEnabled = cursor.ReadByte();
            int[] energyValues = cursor.ReadFixedInt32Values(5);
            var guestSeats = new OriginalSaveObjectProductionGuestSeatDocument[16];
            for (int index = 0; index < guestSeats.Length; index++)
            {
                guestSeats[index] = new OriginalSaveObjectProductionGuestSeatDocument(
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32());
            }

            return new OriginalSaveObjectProductionExtensionDocument(
                slots,
                workState,
                assignedWorkerObjectId,
                requestBytes,
                pendingPositionBits,
                flowBytes,
                productionValues,
                productionEnabled,
                energyValues,
                guestSeats,
                cursor.ReadByte(),
                cursor.ReadByte());
        }

        private static OriginalSaveObjectAttachmentHistoryDocument ReadAttachmentHistory(
            OriginalSavePayloadCursor cursor,
            int objectId)
        {
            bool isPresent = cursor.ReadBooleanByte("attachment history presence", objectId);
            if (!isPresent)
            {
                return new OriginalSaveObjectAttachmentHistoryDocument(
                    false,
                    Array.Empty<OriginalSaveObjectAttachmentHistoryRecordDocument>());
            }

            int count = cursor.ReadCount("attachment history", objectId);
            var records = new OriginalSaveObjectAttachmentHistoryRecordDocument[count];
            for (int index = 0; index < records.Length; index++)
            {
                records[index] = new OriginalSaveObjectAttachmentHistoryRecordDocument(
                    cursor.ReadInt32Vector("attachment history route node", objectId),
                    cursor.ReadInt32(),
                    cursor.ReadInt32());
            }
            return new OriginalSaveObjectAttachmentHistoryDocument(true, records);
        }

        private static OriginalSaveObjectActionVariantDocument ReadActionVariant(
            OriginalSavePayloadCursor cursor,
            int objectId)
        {
            int variantOffset = cursor.Position;
            int variant = cursor.ReadInt32();
            switch (variant)
            {
                case 0:
                {
                    int[] leadingValues = cursor.ReadFixedInt32Values(8);
                    int entryCount = cursor.ReadCount("action variant zero entry", objectId);
                    int[] entryValues = cursor.ReadFixedInt32Values(checked(entryCount * 10));
                    int[] valuesBeforeFlag = cursor.ReadFixedInt32Values(66);
                    bool flag = cursor.ReadBooleanByte("action variant zero flag", objectId);
                    return new OriginalSaveObjectActionVariantDocument(
                        variant,
                        leadingValues,
                        entryCount,
                        entryValues,
                        valuesBeforeFlag,
                        flag,
                        Array.Empty<int>(),
                        Array.Empty<int>(),
                        Array.Empty<string>(),
                        Array.Empty<int>());
                }
                case 1:
                    return new OriginalSaveObjectActionVariantDocument(
                        variant,
                        Array.Empty<int>(),
                        null,
                        Array.Empty<int>(),
                        cursor.ReadFixedInt32Values(7),
                        cursor.ReadBooleanByte("action variant one flag", objectId),
                        cursor.ReadFixedInt32Values(2),
                        Array.Empty<int>(),
                        Array.Empty<string>(),
                        Array.Empty<int>());
                case 2:
                    return new OriginalSaveObjectActionVariantDocument(
                        variant,
                        Array.Empty<int>(),
                        null,
                        Array.Empty<int>(),
                        cursor.ReadFixedInt32Values(7),
                        cursor.ReadBooleanByte("action variant two flag", objectId),
                        cursor.ReadFixedInt32Values(1),
                        cursor.ReadInt32Vector("action variant two value", objectId),
                        cursor.ReadStringVector("action variant two string", objectId),
                        cursor.ReadFixedInt32Values(1));
                default:
                    throw new InvalidDataException(
                        $"Invalid action variant {variant} in original object {objectId} at " +
                        $"offset {variantOffset}.");
            }
        }

        private static OriginalSaveObjectEventQueueDocument ReadEventQueue(
            OriginalSavePayloadCursor cursor,
            int objectId)
        {
            int queueOffset = cursor.Position;
            int readCursor = cursor.ReadInt32();
            int writeCursor = cursor.ReadInt32();
            var slots = new OriginalSaveTimerEventPayloadDocument?[
                OriginalSaveObjectEventQueueDocument.Capacity];
            var slotPayloadOffsets = new long[slots.Length];
            for (int index = 0; index < slots.Length; index++)
            {
                slotPayloadOffsets[index] = -1;
                if (cursor.ReadBooleanByte("event queue slot presence", objectId))
                {
                    slotPayloadOffsets[index] = cursor.Position;
                    slots[index] = ReadEventPayload(cursor, objectId);
                }
            }

            int delayedCount = cursor.ReadCount("delayed event queue record", objectId);
            var delayedRecords = new OriginalSaveTimerEventPayloadDocument[delayedCount];
            var delayedPayloadOffsets = new long[delayedCount];
            for (int index = 0; index < delayedRecords.Length; index++)
            {
                delayedPayloadOffsets[index] = cursor.Position;
                delayedRecords[index] = ReadEventPayload(cursor, objectId);
            }

            return new OriginalSaveObjectEventQueueDocument(
                queueOffset,
                readCursor,
                writeCursor,
                slots,
                delayedRecords,
                slotPayloadOffsets,
                delayedPayloadOffsets);
        }

        private static OriginalSaveTimerEventPayloadDocument ReadEventPayload(
            OriginalSavePayloadCursor cursor,
            int objectId)
        {
            int payloadOffset = cursor.Position;
            try
            {
                return new OriginalSaveTimerEventPayloadDocument(
                    cursor.ReadBytes(32),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadInt32(),
                    cursor.ReadRaw32Values(9),
                    cursor.ReadNativeStringDocument("event queue text A", objectId),
                    cursor.ReadNativeStringDocument("event queue text B", objectId),
                    new OriginalSaveRaw32(cursor.ReadInt32()),
                    new OriginalSaveRaw32(cursor.ReadInt32()),
                    new OriginalSaveRaw32(cursor.ReadInt32()),
                    new OriginalSaveRaw64(cursor.ReadInt64()));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"Invalid event queue payload in original object {objectId} at " +
                    $"offset {payloadOffset}.",
                    exception);
            }
        }
    }

    public sealed class OriginalSaveObjectDocument
    {
        readonly ReadOnlyCollection<int> _state350;
        readonly ReadOnlyCollection<int> _coreState;
        readonly ReadOnlyCollection<int> _childObjectIds;
        readonly ReadOnlyCollection<string> _strings;
        readonly ReadOnlyCollection<int> _stateAfterStrings;
        readonly ReadOnlyCollection<OriginalSaveTclVariable> _tclVariables;

        internal OriginalSaveObjectDocument(
            int objectId,
            string className,
            int objectType,
            OriginalSaveVector3 position,
            OriginalSaveVector3 transientPosition,
            int[] state350,
            int[] coreState,
            int[] childObjectIds,
            string[] strings,
            OriginalSaveTclVariable[] tclVariables,
            int stateObjectId,
            string instanceName,
            OriginalSaveObjectCommonStateDocument commonState,
            int[] stateAfterStrings,
            bool hasTclState,
            long offset,
            long size,
            long opaqueTailOffset,
            OriginalSaveObjectFamilyExtensionDocument? familyExtension,
            OriginalSaveObjectTalkIssueExtensionDocument? talkIssueExtension,
            OriginalSaveObjectProductionExtensionDocument? productionExtension,
            OriginalSaveObjectPlannerExtensionDocument? plannerExtension,
            OriginalSaveObjectAttachmentHistoryDocument attachmentHistory,
            OriginalSaveObjectRetiredGroupsDocument retiredGroups,
            OriginalSaveObjectTailFlagsDocument tailFlags,
            OriginalSaveObjectActionVariantDocument? actionVariant,
            OriginalSaveObjectEventQueueDocument eventQueueDocument,
            int pickTargetMode,
            byte destroyQueued,
            long tailParseEndOffset)
        {
            ObjectId = objectId;
            ClassName = className;
            ObjectType = objectType;
            Position = position;
            TransientPosition = transientPosition;
            _state350 = Array.AsReadOnly(state350);
            _coreState = Array.AsReadOnly(coreState);
            _childObjectIds = Array.AsReadOnly(childObjectIds);
            _strings = Array.AsReadOnly(strings);
            _stateAfterStrings = Array.AsReadOnly(stateAfterStrings);
            _tclVariables = Array.AsReadOnly(tclVariables);
            HasTclState = hasTclState;
            StateObjectId = stateObjectId;
            InstanceName = instanceName;
            CommonState = commonState ?? throw new ArgumentNullException(nameof(commonState));
            Offset = offset;
            Size = size;
            OpaqueTailOffset = opaqueTailOffset;
            FamilyExtension = familyExtension;
            TalkIssueExtension = talkIssueExtension;
            ProductionExtension = productionExtension;
            PlannerExtension = plannerExtension;
            AttachmentHistory = attachmentHistory ??
                throw new ArgumentNullException(nameof(attachmentHistory));
            RetiredGroups = retiredGroups ?? throw new ArgumentNullException(nameof(retiredGroups));
            TailFlags = tailFlags ?? throw new ArgumentNullException(nameof(tailFlags));
            ActionVariant = actionVariant;
            EventQueueDocument = eventQueueDocument ??
                throw new ArgumentNullException(nameof(eventQueueDocument));
            PickTargetMode = pickTargetMode;
            DestroyQueued = destroyQueued;
            TailParseEndOffset = tailParseEndOffset;
        }

        public int ObjectId { get; }
        public string ClassName { get; }
        public int ObjectType { get; }
        public OriginalSaveVector3 Position { get; }

        // Native object +0x18..+0x20 is serialized but cleared after load. It is preserved separately
        // and must not be mistaken for the object's orientation.
        public OriginalSaveVector3 TransientPosition { get; }
        public IReadOnlyList<int> State350 => _state350;
        public IReadOnlyList<int> CoreState => _coreState;
        public float CreationTimeHours => BitConverter.Int32BitsToSingle(_state350[0]);
        public float WorkStart => BitConverter.Int32BitsToSingle(_state350[1]);
        public float WorkLength => BitConverter.Int32BitsToSingle(_state350[2]);
        public float MotionSpeed => BitConverter.Int32BitsToSingle(_coreState[0]);
        public OriginalSaveVector3 Velocity => new OriginalSaveVector3(
            BitConverter.Int32BitsToSingle(_coreState[1]),
            BitConverter.Int32BitsToSingle(_coreState[2]),
            BitConverter.Int32BitsToSingle(_coreState[3]));
        public OriginalSaveVector3 MotionDelta => new OriginalSaveVector3(
            BitConverter.Int32BitsToSingle(_coreState[4]),
            BitConverter.Int32BitsToSingle(_coreState[5]),
            BitConverter.Int32BitsToSingle(_coreState[6]));
        public OriginalSaveVector3 Rotation => new OriginalSaveVector3(
            BitConverter.Int32BitsToSingle(_coreState[7]),
            BitConverter.Int32BitsToSingle(_coreState[8]),
            BitConverter.Int32BitsToSingle(_coreState[9]));
        public OriginalSaveVector3 PreviousRotation => new OriginalSaveVector3(
            BitConverter.Int32BitsToSingle(_coreState[10]),
            BitConverter.Int32BitsToSingle(_coreState[11]),
            BitConverter.Int32BitsToSingle(_coreState[12]));
        public IReadOnlyList<int> ChildObjectIds => _childObjectIds;
        public IReadOnlyList<string> Strings => _strings;
        public IReadOnlyList<int> StateAfterStrings => _stateAfterStrings;
        public IReadOnlyList<OriginalSaveTclVariable> TclVariables => _tclVariables;
        public bool HasTclState { get; }
        public int StateObjectId { get; }
        public string InstanceName { get; }
        public OriginalSaveObjectCommonStateDocument CommonState { get; }
        public bool HasParticleState => CommonState.ParticleState.IsPresent;
        public string DisplayState => CommonState.DisplayState;
        public int Owner => CommonState.Owner;
        public long Offset { get; }
        public long Size { get; }
        public long OpaqueTailOffset { get; }
        public OriginalSaveObjectFamilyExtensionDocument? FamilyExtension { get; }
        public OriginalSaveObjectTalkIssueExtensionDocument? TalkIssueExtension { get; }
        public OriginalSaveObjectProductionExtensionDocument? ProductionExtension { get; }
        public OriginalSaveObjectPlannerExtensionDocument? PlannerExtension { get; }
        public OriginalSaveObjectAttachmentHistoryDocument AttachmentHistory { get; }
        public OriginalSaveObjectRetiredGroupsDocument RetiredGroups { get; }
        public OriginalSaveObjectTailFlagsDocument TailFlags { get; }
        public OriginalSaveObjectActionVariantDocument? ActionVariant { get; }
        public bool HasActionVariant => ActionVariant != null;
        public OriginalSaveObjectEventQueueDocument EventQueueDocument { get; }
        public int PickTargetMode { get; }
        public byte DestroyQueued { get; }
        public long TailParseEndOffset { get; }

        public bool TryGetTclVariable(string name, out string value)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            for (int index = _tclVariables.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(_tclVariables[index].Name, name, StringComparison.Ordinal)) continue;
                value = _tclVariables[index].Value;
                return true;
            }
            value = string.Empty;
            return false;
        }
    }

    public sealed class OriginalSaveObjectActorLinkDocument
    {
        internal OriginalSaveObjectActorLinkDocument(int id, int state, int referenceObjectId)
        {
            Id = id;
            State = state;
            ReferenceObjectId = referenceObjectId;
        }

        public int Id { get; }
        public int State { get; }
        public int ReferenceObjectId { get; }
    }

    public sealed class OriginalSaveObjectFamilyExtensionDocument
    {
        private readonly ReadOnlyCollection<string> _childrenNames;

        internal OriginalSaveObjectFamilyExtensionDocument(
            OriginalSaveObjectActorLinkDocument partnerLink,
            OriginalSaveObjectActorLinkDocument motherLink,
            OriginalSaveObjectActorLinkDocument fatherLink,
            string[] childrenNames,
            bool pregnancyFlag)
        {
            PartnerLink = partnerLink ?? throw new ArgumentNullException(nameof(partnerLink));
            MotherLink = motherLink ?? throw new ArgumentNullException(nameof(motherLink));
            FatherLink = fatherLink ?? throw new ArgumentNullException(nameof(fatherLink));
            _childrenNames = Array.AsReadOnly(
                childrenNames ?? throw new ArgumentNullException(nameof(childrenNames)));
            PregnancyFlag = pregnancyFlag;
        }

        public OriginalSaveObjectActorLinkDocument PartnerLink { get; }
        public OriginalSaveObjectActorLinkDocument MotherLink { get; }
        public OriginalSaveObjectActorLinkDocument FatherLink { get; }
        public IReadOnlyList<string> ChildrenNames => _childrenNames;
        public bool PregnancyFlag { get; }
    }

    public sealed class OriginalSaveObjectTalkIssueEntryDocument
    {
        internal OriginalSaveObjectTalkIssueEntryDocument(string name, int weightBits)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Weight = new OriginalSaveRaw32(weightBits);
        }

        public string Name { get; }
        public OriginalSaveRaw32 Weight { get; }
    }

    public sealed class OriginalSaveObjectTalkIssueExtensionDocument
    {
        private readonly ReadOnlyCollection<OriginalSaveObjectTalkIssueEntryDocument> _entries;

        internal OriginalSaveObjectTalkIssueExtensionDocument(
            int talkDesireSumBits,
            OriginalSaveObjectTalkIssueEntryDocument[] entries)
        {
            TalkDesireSum = new OriginalSaveRaw32(talkDesireSumBits);
            _entries = Array.AsReadOnly(entries ?? throw new ArgumentNullException(nameof(entries)));
        }

        public OriginalSaveRaw32 TalkDesireSum { get; }
        public IReadOnlyList<OriginalSaveObjectTalkIssueEntryDocument> Entries => _entries;
    }

    public sealed class OriginalSaveObjectPlannerExtensionDocument
    {
        private readonly ReadOnlyCollection<int> _values;

        internal OriginalSaveObjectPlannerExtensionDocument(int[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Length != 4)
                throw new ArgumentException("A planner extension must contain four words.", nameof(values));
            _values = Array.AsReadOnly(values);
        }

        public IReadOnlyList<int> Values => _values;
        public int IdleAnnounceFlag => _values[0];
        public OriginalSaveRaw32 IdleSince => new OriginalSaveRaw32(_values[1]);
        public int CurrentWorkplaceObjectId => _values[2];
        public int AssignedWorkplaceObjectId => _values[3];
    }

    public sealed class OriginalSaveObjectProductionSlotDocument
    {
        internal OriginalSaveObjectProductionSlotDocument(
            int registryValue1,
            int registryValue2,
            string className,
            string resourceName,
            int orderCount,
            int demandBits,
            byte invented,
            byte buildable)
        {
            RegistryValue1 = registryValue1;
            RegistryValue2 = registryValue2;
            ClassName = className ?? throw new ArgumentNullException(nameof(className));
            ResourceName = resourceName ?? throw new ArgumentNullException(nameof(resourceName));
            OrderCount = orderCount;
            Demand = new OriginalSaveRaw32(demandBits);
            Invented = invented;
            Buildable = buildable;
        }

        public int RegistryValue1 { get; }
        public int RegistryValue2 { get; }
        public string ClassName { get; }
        public string ResourceName { get; }
        public int OrderCount { get; }
        public OriginalSaveRaw32 Demand { get; }
        public byte Invented { get; }
        public byte Buildable { get; }
    }

    public sealed class OriginalSaveObjectProductionGuestSeatDocument
    {
        internal OriginalSaveObjectProductionGuestSeatDocument(
            int seatLinkId,
            int guestObjectId,
            int orderCount)
        {
            SeatLinkId = seatLinkId;
            GuestObjectId = guestObjectId;
            OrderCount = orderCount;
        }

        public int SeatLinkId { get; }
        public int GuestObjectId { get; }
        public int OrderCount { get; }
    }

    public sealed class OriginalSaveObjectProductionExtensionDocument
    {
        private readonly ReadOnlyCollection<OriginalSaveObjectProductionSlotDocument> _slots;
        private readonly ReadOnlyCollection<byte> _requestBytes;
        private readonly ReadOnlyCollection<int> _pendingPositionBits;
        private readonly ReadOnlyCollection<byte> _flowBytes;
        private readonly ReadOnlyCollection<int> _productionValues;
        private readonly ReadOnlyCollection<int> _energyValues;
        private readonly ReadOnlyCollection<OriginalSaveObjectProductionGuestSeatDocument> _guestSeats;

        internal OriginalSaveObjectProductionExtensionDocument(
            OriginalSaveObjectProductionSlotDocument[] slots,
            int workState,
            int assignedWorkerObjectId,
            byte[] requestBytes,
            int[] pendingPositionBits,
            byte[] flowBytes,
            int[] productionValues,
            byte productionEnabled,
            int[] energyValues,
            OriginalSaveObjectProductionGuestSeatDocument[] guestSeats,
            byte state3C,
            byte state140)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (requestBytes == null) throw new ArgumentNullException(nameof(requestBytes));
            if (pendingPositionBits == null)
                throw new ArgumentNullException(nameof(pendingPositionBits));
            if (flowBytes == null) throw new ArgumentNullException(nameof(flowBytes));
            if (productionValues == null)
                throw new ArgumentNullException(nameof(productionValues));
            if (energyValues == null) throw new ArgumentNullException(nameof(energyValues));
            if (guestSeats == null) throw new ArgumentNullException(nameof(guestSeats));
            if (requestBytes.Length != 7)
                throw new ArgumentException("A production extension must contain seven request bytes.", nameof(requestBytes));
            if (pendingPositionBits.Length != 3)
                throw new ArgumentException("A production extension must contain three position words.", nameof(pendingPositionBits));
            if (flowBytes.Length != 8)
                throw new ArgumentException("A production extension must contain eight flow bytes.", nameof(flowBytes));
            if (productionValues.Length != 4)
                throw new ArgumentException("A production extension must contain four production words.", nameof(productionValues));
            if (energyValues.Length != 5)
                throw new ArgumentException("A production extension must contain five energy words.", nameof(energyValues));
            if (guestSeats.Length != 16)
                throw new ArgumentException("A production extension must contain sixteen guest seats.", nameof(guestSeats));

            _slots = Array.AsReadOnly(slots);
            WorkState = workState;
            AssignedWorkerObjectId = assignedWorkerObjectId;
            _requestBytes = Array.AsReadOnly(requestBytes);
            _pendingPositionBits = Array.AsReadOnly(pendingPositionBits);
            _flowBytes = Array.AsReadOnly(flowBytes);
            _productionValues = Array.AsReadOnly(productionValues);
            ProductionEnabled = productionEnabled;
            _energyValues = Array.AsReadOnly(energyValues);
            _guestSeats = Array.AsReadOnly(guestSeats);
            State3C = state3C;
            State140 = state140;
        }

        public IReadOnlyList<OriginalSaveObjectProductionSlotDocument> Slots => _slots;
        public int WorkState { get; }
        public int AssignedWorkerObjectId { get; }
        public IReadOnlyList<byte> RequestBytes => _requestBytes;
        public IReadOnlyList<int> PendingPositionBits => _pendingPositionBits;
        public OriginalSaveRaw32 PendingPositionX => new OriginalSaveRaw32(_pendingPositionBits[0]);
        public OriginalSaveRaw32 PendingPositionY => new OriginalSaveRaw32(_pendingPositionBits[1]);
        public OriginalSaveRaw32 PendingPositionZ => new OriginalSaveRaw32(_pendingPositionBits[2]);
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
        public OriginalSaveRaw32 EnergyConsumption => new OriginalSaveRaw32(_energyValues[1]);
        public OriginalSaveRaw32 EnergyStore => new OriginalSaveRaw32(_energyValues[2]);
        public OriginalSaveRaw32 EnergyMaximumStore => new OriginalSaveRaw32(_energyValues[3]);
        public OriginalSaveRaw32 EnergyCurrentLoad => new OriginalSaveRaw32(_energyValues[4]);
        public IReadOnlyList<OriginalSaveObjectProductionGuestSeatDocument> GuestSeats => _guestSeats;
        public byte State3C { get; }
        public byte State140 { get; }
    }

    public sealed class OriginalSaveObjectAttachmentHistoryRecordDocument
    {
        private readonly ReadOnlyCollection<int> _routeValues;

        internal OriginalSaveObjectAttachmentHistoryRecordDocument(
            int[] routeValues,
            int trailingValue1,
            int trailingValue2)
        {
            _routeValues = Array.AsReadOnly(
                routeValues ?? throw new ArgumentNullException(nameof(routeValues)));
            TrailingValue1 = trailingValue1;
            TrailingValue2 = trailingValue2;
        }

        public IReadOnlyList<int> RouteValues => _routeValues;
        public int TrailingValue1 { get; }
        public int TrailingValue2 { get; }
    }

    public sealed class OriginalSaveObjectAttachmentHistoryDocument
    {
        private readonly ReadOnlyCollection<OriginalSaveObjectAttachmentHistoryRecordDocument> _records;

        internal OriginalSaveObjectAttachmentHistoryDocument(
            bool isPresent,
            OriginalSaveObjectAttachmentHistoryRecordDocument[] records)
        {
            IsPresent = isPresent;
            _records = Array.AsReadOnly(records ?? throw new ArgumentNullException(nameof(records)));
        }

        public bool IsPresent { get; }
        public IReadOnlyList<OriginalSaveObjectAttachmentHistoryRecordDocument> Records => _records;
    }

    public sealed class OriginalSaveObjectRetiredGroupsDocument
    {
        private readonly ReadOnlyCollection<int> _state2B8;
        private readonly ReadOnlyCollection<byte> _state2F8;
        private readonly ReadOnlyCollection<int> _state308;

        internal OriginalSaveObjectRetiredGroupsDocument(
            int[] state2B8,
            byte[] state2F8,
            int[] state308)
        {
            if (state2B8 == null) throw new ArgumentNullException(nameof(state2B8));
            if (state2F8 == null) throw new ArgumentNullException(nameof(state2F8));
            if (state308 == null) throw new ArgumentNullException(nameof(state308));
            if (state2B8.Length != 16 || state2F8.Length != 16 || state308.Length != 16)
                throw new ArgumentException("Retired object groups must each contain sixteen values.");
            _state2B8 = Array.AsReadOnly(state2B8);
            _state2F8 = Array.AsReadOnly(state2F8);
            _state308 = Array.AsReadOnly(state308);
        }

        public IReadOnlyList<int> State2B8 => _state2B8;
        public IReadOnlyList<byte> State2F8 => _state2F8;
        public IReadOnlyList<int> State308 => _state308;
    }

    public sealed class OriginalSaveObjectTailFlagsDocument
    {
        internal OriginalSaveObjectTailFlagsDocument(
            byte flag348,
            int countDownWord,
            byte moverStateByte)
        {
            Flag348 = flag348;
            CountDownWord = countDownWord;
            MoverStateByte = moverStateByte;
        }

        public byte Flag348 { get; }
        public int CountDownWord { get; }
        public byte MoverStateByte { get; }
    }

    public sealed class OriginalSaveObjectActionVariantDocument
    {
        private readonly ReadOnlyCollection<int> _leadingValues;
        private readonly ReadOnlyCollection<int> _entryValues;
        private readonly ReadOnlyCollection<int> _valuesBeforeFlag;
        private readonly ReadOnlyCollection<int> _valuesAfterFlag;
        private readonly ReadOnlyCollection<int> _vectorValues;
        private readonly ReadOnlyCollection<string> _stringValues;
        private readonly ReadOnlyCollection<int> _trailingValues;

        internal OriginalSaveObjectActionVariantDocument(
            int variant,
            int[] leadingValues,
            int? entryCount,
            int[] entryValues,
            int[] valuesBeforeFlag,
            bool flag,
            int[] valuesAfterFlag,
            int[] vectorValues,
            string[] stringValues,
            int[] trailingValues)
        {
            Variant = variant;
            _leadingValues = Array.AsReadOnly(
                leadingValues ?? throw new ArgumentNullException(nameof(leadingValues)));
            EntryCount = entryCount;
            _entryValues = Array.AsReadOnly(
                entryValues ?? throw new ArgumentNullException(nameof(entryValues)));
            _valuesBeforeFlag = Array.AsReadOnly(
                valuesBeforeFlag ?? throw new ArgumentNullException(nameof(valuesBeforeFlag)));
            Flag = flag;
            _valuesAfterFlag = Array.AsReadOnly(
                valuesAfterFlag ?? throw new ArgumentNullException(nameof(valuesAfterFlag)));
            _vectorValues = Array.AsReadOnly(
                vectorValues ?? throw new ArgumentNullException(nameof(vectorValues)));
            _stringValues = Array.AsReadOnly(
                stringValues ?? throw new ArgumentNullException(nameof(stringValues)));
            _trailingValues = Array.AsReadOnly(
                trailingValues ?? throw new ArgumentNullException(nameof(trailingValues)));
        }

        public int Variant { get; }
        public IReadOnlyList<int> LeadingValues => _leadingValues;
        public int? EntryCount { get; }
        public IReadOnlyList<int> EntryValues => _entryValues;
        public IReadOnlyList<int> ValuesBeforeFlag => _valuesBeforeFlag;
        public bool Flag { get; }
        public IReadOnlyList<int> ValuesAfterFlag => _valuesAfterFlag;
        public IReadOnlyList<int> VectorValues => _vectorValues;
        public IReadOnlyList<string> StringValues => _stringValues;
        public IReadOnlyList<int> TrailingValues => _trailingValues;
    }

    public sealed class OriginalSaveObjectEventQueueDocument
    {
        public const int Capacity = 64;

        private const int RingMask = Capacity - 1;
        private const int NativeSignedRingMask = unchecked((int)0x8000003F);
        private readonly ReadOnlyCollection<OriginalSaveTimerEventPayloadDocument?> _slots;
        private readonly ReadOnlyCollection<OriginalSaveTimerEventPayloadDocument> _delayedRecords;
        private readonly ReadOnlyCollection<long> _slotPayloadOffsets;
        private readonly ReadOnlyCollection<long> _delayedPayloadOffsets;

        internal OriginalSaveObjectEventQueueDocument(
            long queueOffset,
            int readCursor,
            int writeCursor,
            OriginalSaveTimerEventPayloadDocument?[] slots,
            OriginalSaveTimerEventPayloadDocument[] delayedRecords,
            long[] slotPayloadOffsets,
            long[] delayedPayloadOffsets)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (delayedRecords == null) throw new ArgumentNullException(nameof(delayedRecords));
            if (slotPayloadOffsets == null)
                throw new ArgumentNullException(nameof(slotPayloadOffsets));
            if (delayedPayloadOffsets == null)
                throw new ArgumentNullException(nameof(delayedPayloadOffsets));
            if (slots.Length != Capacity)
                throw new ArgumentException("An object event queue must contain sixty-four slots.", nameof(slots));
            if (slotPayloadOffsets.Length != Capacity)
            {
                throw new ArgumentException(
                    "An object event queue must contain sixty-four slot payload offsets.",
                    nameof(slotPayloadOffsets));
            }
            if (delayedPayloadOffsets.Length != delayedRecords.Length)
            {
                throw new ArgumentException(
                    "Every delayed object event must have a payload offset.",
                    nameof(delayedPayloadOffsets));
            }
            _slots = Array.AsReadOnly(slots);
            _delayedRecords = Array.AsReadOnly(delayedRecords);
            _slotPayloadOffsets = Array.AsReadOnly(slotPayloadOffsets);
            _delayedPayloadOffsets = Array.AsReadOnly(delayedPayloadOffsets);
            QueueOffset = queueOffset;
            ReadCursor = readCursor;
            WriteCursor = writeCursor;
        }

        public long QueueOffset { get; }
        public int ReadCursor { get; }
        public int WriteCursor { get; }
        public IReadOnlyList<OriginalSaveTimerEventPayloadDocument?> Slots => _slots;
        public IReadOnlyList<OriginalSaveTimerEventPayloadDocument> DelayedRecords => _delayedRecords;
        public IReadOnlyList<long> SlotPayloadOffsets => _slotPayloadOffsets;
        public IReadOnlyList<long> DelayedPayloadOffsets => _delayedPayloadOffsets;
        public int PendingWindowCount => ApplyNativeCursorMask(
            unchecked(WriteCursor - ReadCursor));

        public int GetPendingSlotIndex(int pendingIndex)
        {
            if ((uint)pendingIndex >= (uint)PendingWindowCount)
                throw new ArgumentOutOfRangeException(nameof(pendingIndex));
            return ApplyNativeCursorMask(unchecked(ReadCursor + pendingIndex));
        }

        public bool IsPendingSlot(int slotIndex)
        {
            if ((uint)slotIndex >= Capacity)
                throw new ArgumentOutOfRangeException(nameof(slotIndex));
            int distanceFromReadCursor = ApplyNativeCursorMask(
                unchecked(slotIndex - ReadCursor));
            return distanceFromReadCursor < PendingWindowCount;
        }

        public long GetSlotPayloadOffset(int slotIndex)
        {
            if ((uint)slotIndex >= Capacity)
                throw new ArgumentOutOfRangeException(nameof(slotIndex));
            return _slotPayloadOffsets[slotIndex];
        }

        public long GetDelayedPayloadOffset(int delayedIndex)
        {
            if ((uint)delayedIndex >= (uint)_delayedPayloadOffsets.Count)
                throw new ArgumentOutOfRangeException(nameof(delayedIndex));
            return _delayedPayloadOffsets[delayedIndex];
        }

        private static int ApplyNativeCursorMask(int value)
        {
            int nativeMasked = value & NativeSignedRingMask;
            return nativeMasked < 0 ? nativeMasked & RingMask : nativeMasked;
        }
    }

    public sealed class OriginalSaveObjectCommonStateDocument
    {
        readonly ReadOnlyCollection<int> _state200;
        readonly ReadOnlyCollection<int> _state220;
        readonly ReadOnlyCollection<int> _routeValues;
        readonly ReadOnlyCollection<int> _gridState;
        readonly ReadOnlyCollection<int> _state264;
        readonly ReadOnlyCollection<int> _state254;
        readonly ReadOnlyCollection<int> _state26C;
        readonly ReadOnlyCollection<int> _state298;
        readonly OriginalSaveObjectParticleStateDocument _particleState;
        readonly ReadOnlyCollection<int> _state37C;
        readonly ReadOnlyCollection<int> _state39C;
        readonly ReadOnlyCollection<int> _state3BC;
        readonly ReadOnlyCollection<int> _lateCoreState;

        internal OriginalSaveObjectCommonStateDocument(
            int[] state200,
            int[] state220,
            int[] routeValues,
            int routeState,
            int routeTimeBits,
            int[] gridState,
            int[] state264,
            int[] state254,
            int[] state26C,
            int[] state298,
            int[] particleState,
            byte state2B4,
            byte state35C,
            byte state35D,
            string displayState,
            int fogOfWarSizeX,
            int fogOfWarSizeY,
            int owner,
            int state378,
            int[] state37C,
            int[] state39C,
            byte state3B4,
            int[] state3BC,
            int[] lateCoreState,
            int triggerRecordId)
        {
            _state200 = Array.AsReadOnly(state200);
            _state220 = Array.AsReadOnly(state220);
            _routeValues = Array.AsReadOnly(routeValues);
            RouteState = routeState;
            RouteTimeBits = routeTimeBits;
            _gridState = Array.AsReadOnly(gridState);
            _state264 = Array.AsReadOnly(state264);
            _state254 = Array.AsReadOnly(state254);
            _state26C = Array.AsReadOnly(state26C);
            _state298 = Array.AsReadOnly(state298);
            _particleState = new OriginalSaveObjectParticleStateDocument(particleState);
            State2B4 = state2B4;
            State35C = state35C;
            State35D = state35D;
            DisplayState = displayState;
            FogOfWarSizeX = fogOfWarSizeX;
            FogOfWarSizeY = fogOfWarSizeY;
            Owner = owner;
            State378 = state378;
            _state37C = Array.AsReadOnly(state37C);
            _state39C = Array.AsReadOnly(state39C);
            State3B4 = state3B4;
            _state3BC = Array.AsReadOnly(state3BC);
            _lateCoreState = Array.AsReadOnly(lateCoreState);
            TriggerRecordId = triggerRecordId;
        }

        public IReadOnlyList<int> State200 => _state200;
        public IReadOnlyList<int> State220 => _state220;
        public IReadOnlyList<int> RouteValues => _routeValues;
        public int RouteState { get; }
        public int RouteTimeBits { get; }
        public IReadOnlyList<int> GridState => _gridState;
        public IReadOnlyList<int> State264 => _state264;
        public IReadOnlyList<int> State254 => _state254;
        public IReadOnlyList<int> State26C => _state26C;
        public IReadOnlyList<int> State298 => _state298;
        public OriginalSaveObjectParticleStateDocument ParticleState => _particleState;
        public byte State2B4 { get; }
        public byte State35C { get; }
        public byte State35D { get; }
        public string DisplayState { get; }
        public int FogOfWarSizeX { get; }
        public int FogOfWarSizeY { get; }
        public int Owner { get; }
        public int State378 { get; }
        public IReadOnlyList<int> State37C => _state37C;
        public IReadOnlyList<int> State39C => _state39C;
        public byte State3B4 { get; }
        public IReadOnlyList<int> State3BC => _state3BC;
        public IReadOnlyList<int> LateCoreState => _lateCoreState;
        public int TriggerRecordId { get; }

        public int PlaceSnapMode => _state220[1];
        public float FogOfWarLastX => BitConverter.Int32BitsToSingle(_state26C[0]);
        public float FogOfWarLastY => BitConverter.Int32BitsToSingle(_state26C[1]);
        public float FogOfWarLastZ => BitConverter.Int32BitsToSingle(_state26C[2]);
        public int LinkedObjectId => _state26C[3];
        public int LinkedSlot => _state26C[4];
        public int CarrierSlotId => _state39C[0];
        public int CarrierSubIndex => _state39C[1];
        public int ObjectFlags => _lateCoreState[0];
        public int DirtyFlags => _lateCoreState[2];
        public int DigOrderId => _lateCoreState[4];
    }

    public readonly struct OriginalSaveTclVariable
    {
        public OriginalSaveTclVariable(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public string Value { get; }
    }

    public readonly struct OriginalSaveVector3
    {
        public OriginalSaveVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
    }

    sealed class OriginalSavePayloadCursor
    {
        const int MaximumCollectionCount = 4 * 1024 * 1024;
        const int MaximumStringLength = 1024 * 1024;
        static readonly Encoding Windows1252 = Encoding.GetEncoding(1252);

        readonly byte[] _payload;
        readonly int _end;

        internal OriginalSavePayloadCursor(byte[] payload, int start, int end)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (start < 0 || end < start || end > payload.Length)
                throw new ArgumentOutOfRangeException(nameof(start));
            _payload = payload;
            Position = start;
            _end = end;
        }

        internal int Position { get; private set; }

        internal int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            int value = OriginalSaveWorldDocument.ReadInt32(_payload, Position);
            Position += sizeof(int);
            return value;
        }

        internal long ReadInt64()
        {
            uint low = unchecked((uint)ReadInt32());
            uint high = unchecked((uint)ReadInt32());
            return unchecked((long)((ulong)low | ((ulong)high << 32)));
        }

        internal byte ReadByte()
        {
            EnsureAvailable(sizeof(byte));
            return _payload[Position++];
        }

        internal bool ReadBooleanByte(string label)
        {
            byte value = ReadByte();
            if (value > 1) throw new InvalidDataException($"Invalid {label} value {value}.");
            return value != 0;
        }

        internal bool ReadBooleanByte(string label, int objectId)
        {
            int offset = Position;
            byte value = ReadByte();
            if (value > 1)
            {
                throw new InvalidDataException(
                    $"Invalid {label} value {value} in original object {objectId} at offset {offset}.");
            }
            return value != 0;
        }

        internal float ReadSingle()
        {
            EnsureAvailable(sizeof(float));
            float value = OriginalSaveWorldDocument.ReadSingle(_payload, Position);
            Position += sizeof(float);
            return value;
        }

        internal int ReadCount()
        {
            int count = ReadInt32();
            if (count < 0 || count > MaximumCollectionCount)
                throw new InvalidDataException($"Invalid original collection count {count}.");
            return count;
        }

        internal int ReadCount(string label, int objectId)
        {
            int offset = Position;
            int count = ReadInt32();
            if (count < 0 || count > MaximumCollectionCount)
            {
                throw new InvalidDataException(
                    $"Invalid {label} count {count} in original object {objectId} at offset {offset}.");
            }
            return count;
        }

        internal string ReadNativeString()
        {
            int length = ReadInt32();
            if (length <= 0 || length > MaximumStringLength)
                throw new InvalidDataException($"Invalid original string length {length}.");
            EnsureAvailable(length);
            if (_payload[Position + length - 1] != 0)
                throw new InvalidDataException("Original string is not NUL-terminated.");
            string value = Windows1252.GetString(_payload, Position, length - 1);
            Position += length;
            return value;
        }

        internal string ReadNativeString(string label, int objectId)
        {
            byte[] bytes = ReadNativeStringBytes(label, objectId);
            return Windows1252.GetString(bytes, 0, bytes.Length - 1);
        }

        internal OriginalSaveNativeString ReadNativeStringDocument(string label, int objectId) =>
            new OriginalSaveNativeString(ReadNativeStringBytes(label, objectId));

        internal int[] ReadInt32Vector()
        {
            int count = ReadCount();
            var values = new int[count];
            ReadInt32Values(values);
            return values;
        }

        internal int[] ReadInt32Vector(string label, int objectId)
        {
            int count = ReadCount(label, objectId);
            var values = new int[count];
            ReadInt32Values(values);
            return values;
        }

        internal string[] ReadStringVector()
        {
            int count = ReadCount();
            var values = new string[count];
            for (int index = 0; index < values.Length; index++) values[index] = ReadNativeString();
            return values;
        }

        internal string[] ReadStringVector(string label, int objectId)
        {
            int count = ReadCount(label, objectId);
            var values = new string[count];
            for (int index = 0; index < values.Length; index++)
                values[index] = ReadNativeString(label, objectId);
            return values;
        }

        internal byte[] ReadBytes(int count)
        {
            EnsureAvailable(count);
            var values = new byte[count];
            Buffer.BlockCopy(_payload, Position, values, 0, count);
            Position += count;
            return values;
        }

        internal OriginalSaveRaw32[] ReadRaw32Values(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var values = new OriginalSaveRaw32[count];
            for (int index = 0; index < values.Length; index++)
                values[index] = new OriginalSaveRaw32(ReadInt32());
            return values;
        }

        internal void ReadInt32Values(int[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            for (int index = 0; index < destination.Length; index++) destination[index] = ReadInt32();
        }

        internal int[] ReadFixedInt32Values(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var values = new int[count];
            ReadInt32Values(values);
            return values;
        }

        internal void SkipInt32Values(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            Skip(checked(count * sizeof(int)));
        }

        internal void Skip(int size)
        {
            EnsureAvailable(size);
            Position += size;
        }

        private byte[] ReadNativeStringBytes(string label, int objectId)
        {
            int offset = Position;
            int length = ReadInt32();
            if (length <= 0 || length > MaximumStringLength)
            {
                throw new InvalidDataException(
                    $"Invalid {label} length {length} in original object {objectId} at offset {offset}.");
            }

            byte[] bytes = ReadBytes(length);
            if (bytes[bytes.Length - 1] != 0)
            {
                throw new InvalidDataException(
                    $"Original {label} in object {objectId} is not NUL-terminated at offset {offset}.");
            }
            return bytes;
        }

        void EnsureAvailable(int size)
        {
            if (size < 0 || (long)Position + size > _end)
                throw new EndOfStreamException("Unexpected end of original object body.");
        }
    }
}
