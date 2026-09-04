#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    public sealed class OriginalSaveStructureBuilder
    {
        public const int ActionTableSentinel = 0x626C6133;
        public const int ActionAuxiliaryTableSentinel = 0x626C6134;

        private const int MaximumObjectCount = 8192;
        private const int MaximumObjectId = 0x1FFF;
        private const int MaximumSupportRecordCount = 0x3000;
        private const int ClassTableCapacity = 0x3000;
        private const int OwnerAttributeBlockCount = 9;
        private const int AttributeSlotCount = 512;
        private const int MaximumStructureRecordCount = 4 * 1024 * 1024;
        private const int RetiredFloatMinusOne = unchecked((int)0xBF800000);
        private static readonly Encoding Windows1252 = Encoding.GetEncoding(
            1252,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);

        public byte[] BuildDeclarations(OriginalSaveObjectManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
                WriteDeclarations(writer, manifest.Declarations, manifest.SeparatorText);
            return output.ToArray();
        }

        public byte[] BuildSupportRecords(OriginalSaveTimerDocument timers)
        {
            if (timers == null) throw new ArgumentNullException(nameof(timers));
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
                WriteSupportRecords(writer, timers.Records);
            return output.ToArray();
        }

        public byte[] BuildObjectBody(OriginalSaveObjectDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return BuildObjectBody(source, null, null);
        }

        public byte[] BuildActionTable(OriginalSaveActionTableDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
                WriteActionTable(writer, source);
            return output.ToArray();
        }

        public byte[] BuildEmptyClassTable()
        {
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
                WriteEmptyClassTable(writer);
            return output.ToArray();
        }

        public byte[] BuildEmptySpatialIndex(int worldWidth, int worldHeight)
        {
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
                WriteEmptySpatialIndex(writer, worldWidth, worldHeight);
            return output.ToArray();
        }

        public byte[] BuildSpatialIndexFromSeeds(
            IReadOnlyList<OriginalSaveSpatialSeed> seeds,
            int worldWidth,
            int worldHeight)
        {
            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
                WriteSpatialIndexFromSeeds(writer, seeds, worldWidth, worldHeight);
            return output.ToArray();
        }

        public OriginalSaveRetainedStructureSections CaptureRetainedStructures(
            OriginalSaveArchiveDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            OriginalSaveObjectManifest manifest = source.Metadata.Objects ??
                throw new InvalidOperationException("The current save has no object manifest.");
            byte[] payload = source.CopyPayload();
            RetainedStructureLayout layout = FindRetainedStructureLayout(payload, manifest);
            return new OriginalSaveRetainedStructureSections(
                Slice(payload, layout.ClassTableOffset, layout.SpatialIndexOffset),
                Slice(payload, layout.CleanupQueueOffset, layout.CleanupQueueEndOffset),
                Slice(payload, layout.SpatialIndexOffset, checked((int)manifest.Marker7Offset)));
        }

        public static int CalculateSpatialBucketCount(int worldWidth, int worldHeight)
        {
            if (worldWidth <= 0) throw new ArgumentOutOfRangeException(nameof(worldWidth));
            if (worldHeight <= 0) throw new ArgumentOutOfRangeException(nameof(worldHeight));
            int count;
            try
            {
                count = checked(((worldWidth + 7) / 8) * ((worldHeight + 7) / 8));
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The native spatial bucket count overflowed.", exception);
            }
            if (count > MaximumStructureRecordCount)
            {
                throw new InvalidDataException(
                    $"The native spatial index requires {count} buckets; the maximum is " +
                    $"{MaximumStructureRecordCount}.");
            }
            return count;
        }

        internal void WriteDeclarations(
            BinaryWriter writer,
            OriginalSaveObjectManifest? retainedManifest,
            IReadOnlyList<OriginalSaveCanonicalEntityState> entities)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            ValidateObjectCount(entities.Count);

            int previousObjectId = 1;
            int retainedIndex = 0;
            for (int index = 0; index < entities.Count; index++)
            {
                OriginalSaveCanonicalEntityState entity = entities[index] ??
                    throw new InvalidDataException("A canonical object registry slot is null.");
                ValidateObjectId(entity.ObjectId, previousObjectId);

                while (retainedManifest != null &&
                       retainedIndex < retainedManifest.Declarations.Count &&
                       retainedManifest.Declarations[retainedIndex].ObjectId < entity.ObjectId)
                {
                    retainedIndex++;
                }

                string className = entity.ClassName;
                if (retainedManifest != null &&
                    retainedIndex < retainedManifest.Declarations.Count &&
                    retainedManifest.Declarations[retainedIndex].ObjectId == entity.ObjectId)
                {
                    // Degenerate shells keep the archived class spelling even when their live
                    // placeholder has no registered native class handle.
                    className = retainedManifest.Declarations[retainedIndex].ClassName;
                }
                ValidateClassName(className, entity.ObjectId);
                writer.Write(entity.ObjectId);
                WriteNativeString(writer, className);
                previousObjectId = entity.ObjectId;
            }

            writer.Write(-1);
            WriteNativeString(writer, retainedManifest?.SeparatorText ?? string.Empty);
        }

        internal void WriteSupportRecords(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveTimerRecord> records)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (records.Count > MaximumSupportRecordCount)
            {
                throw new InvalidDataException(
                    $"The native support table contains {records.Count} records; " +
                    $"the maximum is {MaximumSupportRecordCount}.");
            }

            writer.Write(records.Count);
            for (int index = 0; index < records.Count; index++)
            {
                OriginalSaveTimerRecord record = records[index] ??
                    throw new InvalidDataException("A native support record is null.");
                // RepeatsLeft == 0 is legal emitted state: the native retains exhausted timers
                // lazily (registry/scheduler/mapper validators carry the same law).
                if (record.OwnerObjectId < 2 || record.OwnerObjectId > MaximumObjectId)
                {
                    throw new InvalidDataException(
                        $"Native support record {index} has an invalid owner.");
                }

                writer.Write(record.UserId);
                writer.Write(record.NextFire.Bits);
                writer.Write(record.Interval.Bits);
                writer.Write(record.RepeatsLeft);
                writer.Write(record.OwnerObjectId);
                WriteEventPayload(writer, record.EventPayload, record.OwnerObjectId);
            }
        }

        internal byte[] BuildObjectBody(
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live,
            OriginalSaveObjectCorePatch? patch)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (live != null && (live.ObjectId != source.ObjectId ||
                live.ObjectType != source.ObjectType ||
                !string.Equals(live.ClassName, source.ClassName, StringComparison.Ordinal)))
            {
                throw new NotSupportedException(
                    $"Cannot rebuild retained object {live.ObjectId} after its identity, class, " +
                    "or native type changed.");
            }

            using var output = new MemoryStream(checked((int)source.Size));
            using (var writer = new BinaryWriter(output, Windows1252, true))
            {
                WriteCommonObjectBody(writer, source, live, patch);
                WriteObjectBodyTail(writer, source, live);
            }
            return output.ToArray();
        }

        internal byte[] BuildObjectBody(
            OriginalSaveCanonicalEntityState live,
            double gameTime)
        {
            return BuildObjectBody(live, gameTime, -1);
        }

        internal byte[] BuildObjectBody(
            OriginalSaveCanonicalEntityState live,
            double gameTime,
            int triggerRecordId)
        {
            if (live == null) throw new ArgumentNullException(nameof(live));
            if (double.IsNaN(gameTime) || double.IsInfinity(gameTime) || gameTime < 0d)
                throw new InvalidDataException("The native game clock is invalid.");
            if (triggerRecordId < -1)
                throw new ArgumentOutOfRangeException(nameof(triggerRecordId));
            ValidateObjectId(live.ObjectId, 1);
            ValidateClassName(live.ClassName, live.ObjectId);
            if (live.ObjectType is < -1 or > 12)
                throw new InvalidDataException(
                    $"Native object {live.ObjectId} has invalid type {live.ObjectType}.");

            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
                WriteNewObjectBody(writer, live, gameTime, triggerRecordId);
            return output.ToArray();
        }

        internal void WriteObjectManagerTail(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveCanonicalOwnerAttributeBlock> ownerAttributes,
            IReadOnlyList<OriginalSaveCanonicalEntityState> entities)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (ownerAttributes == null) throw new ArgumentNullException(nameof(ownerAttributes));
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            if (ownerAttributes.Count != OwnerAttributeBlockCount)
                throw new InvalidDataException("Native owner attributes require nine blocks.");

            for (int index = 0; index < ownerAttributes.Count; index++)
            {
                OriginalSaveCanonicalOwnerAttributeBlock block = ownerAttributes[index] ??
                    throw new InvalidDataException("A native owner-attribute block is null.");
                if (block.Owner != index - 1)
                    throw new InvalidDataException("Native owner-attribute blocks are out of order.");
                WriteValues(
                    writer,
                    block.PrimaryValueBits,
                    AttributeSlotCount,
                    "owner attribute primary page");
                WriteValues(
                    writer,
                    block.SecondaryCompatibilityBits,
                    AttributeSlotCount,
                    "owner attribute secondary page");
            }

            int maximumObjectId = entities.Count == 0 ? -1 : entities[entities.Count - 1].ObjectId;
            writer.Write(-1);
            // The native manager tail stores the highest id, not next-free (the loader rescans
            // the occupancy array and rebuilds next-id itself, FUN_005d6790:424-444 — the field
            // is dead on load; this is byte parity with native saves, not behavior).
            writer.Write(maximumObjectId);
            for (int group = 0; group < 3; group++)
            {
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write((byte)0);
                writer.Write((byte)1);
            }
            writer.Write(0);
        }

        internal void WriteEmptyClassTable(BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Write(0);
            for (int slot = ClassTableCapacity - 1; slot >= 0; slot--) writer.Write(slot);
            writer.Write(ClassTableCapacity - 1);
            writer.Write(byte.MaxValue);
            writer.Write(0);
            writer.Write(0);
            writer.Write(-1);
        }

        internal void WriteEmptySpatialIndex(
            BinaryWriter writer,
            int worldWidth,
            int worldHeight)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            int bucketCount = CalculateSpatialBucketCount(worldWidth, worldHeight);
            for (int bucket = 0; bucket < bucketCount; bucket++) writer.Write(0);
        }

        // Re-emit the marker-7 spatial index from the surviving dormant scenery seeds. Used when
        // the object graph changed and the retained bytes cannot be spliced back: an empty index
        // would lose the deco for good — the native never regenerates dropped records (its only
        // producer, FUN_005cd1d0:96-120, converts live display instances back into records; the
        // archived, not-yet-consumed seeds have no live counterpart). Bucket law: trunc(x)/8,
        // trunc(y)/8, gridW*by+bx, <1 → 0, % bucketCount. Record transfer order = FUN_005c6cf0.
        // Feeding every archived record back through this writer reproduces the retained section
        // byte-for-byte (OriginalSaveStructureBuilderTests.SpatialIndexRebuildFromSeedsMatchesRetainedBytes).
        internal void WriteSpatialIndexFromSeeds(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveSpatialSeed> seeds,
            int worldWidth,
            int worldHeight)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (seeds == null) throw new ArgumentNullException(nameof(seeds));
            int bucketCount = CalculateSpatialBucketCount(worldWidth, worldHeight);
            int gridWidth = (worldWidth + 7) / 8;
            var buckets = new List<OriginalSaveSpatialSeed>?[bucketCount];
            for (int index = 0; index < seeds.Count; index++)
            {
                OriginalSaveSpatialSeed seed = seeds[index];
                int bucketX = ((int)seed.PositionX) / 8;
                int bucketY = ((int)seed.PositionY) / 8;
                int bucket = gridWidth * bucketY + bucketX;
                bucket = bucket < 1 ? 0 : bucket % bucketCount;
                (buckets[bucket] ??= new List<OriginalSaveSpatialSeed>()).Add(seed);
            }
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                List<OriginalSaveSpatialSeed>? records = buckets[bucket];
                writer.Write(records?.Count ?? 0);
                if (records == null) continue;
                for (int index = 0; index < records.Count; index++)
                {
                    OriginalSaveSpatialSeed seed = records[index];
                    writer.Write(BitConverter.SingleToInt32Bits(seed.PositionX));
                    writer.Write(BitConverter.SingleToInt32Bits(seed.PositionY));
                    writer.Write(BitConverter.SingleToInt32Bits(seed.PositionZ));
                    writer.Write(BitConverter.SingleToInt32Bits(seed.RotationX));
                    writer.Write(BitConverter.SingleToInt32Bits(seed.RotationY));
                    writer.Write(BitConverter.SingleToInt32Bits(seed.RotationZ));
                    WriteNativeString(writer, seed.ClassName);
                    writer.Write(seed.SelectorA);
                    writer.Write(seed.SelectorB);
                    writer.Write(seed.Word24);
                    writer.Write(seed.Flags);
                    writer.Write(seed.LodTier);
                    writer.Write(seed.VisibilityMask);
                }
            }
        }

        internal void WriteActionTable(
            BinaryWriter writer,
            OriginalSaveActionTableDocument? retained,
            OriginalSaveCanonicalCapture capture)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (capture == null) throw new ArgumentNullException(nameof(capture));

            var retainedByOwner = new Dictionary<int, OriginalSaveActionRecord>();
            if (retained != null)
            {
                for (int index = 0; index < retained.Records.Count; index++)
                {
                    OriginalSaveActionRecord record = retained.Records[index];
                    retainedByOwner.Add(record.OwnerObjectId, record);
                }
            }

            // The disk table is not sorted by owner: 61 of the 103 corpus saves carry
            // out-of-order records (the native list keeps re-created actions at its tail).
            // Owner uniqueness and range stay hard; the capture's retained-aligned order is
            // emitted verbatim.
            var owners = new HashSet<int>();
            for (int index = 0; index < capture.Actions.Length; index++)
            {
                RuntimeEntityActionSaveStateEntry entry = capture.Actions[index];
                if (entry.ObjectId < 0 || entry.ObjectId > MaximumObjectId ||
                    !owners.Add(entry.ObjectId))
                {
                    throw new InvalidDataException(
                        $"Canonical action for owner {entry.ObjectId} is duplicated, " +
                        "or out of range.");
                }

                RuntimeEntityActionSaveState? state = entry.State == null
                    ? null
                    : UnwrapActionState(entry.State);
                OriginalSaveActionType type = state == null
                    ? OriginalSaveActionType.Null
                    : ActionTypeOf(state);
                retainedByOwner.TryGetValue(entry.ObjectId, out OriginalSaveActionRecord? source);
                writer.Write(entry.ObjectId);
                writer.Write((int)type);
                if (source != null && source.Type == type &&
                    (state == null
                        ? source.Payload is OriginalSaveNullActionPayload
                        : ActionStateMatches(source.Payload, state, capture.GameTime)))
                {
                    writer.Write(source.CopyRawPayload());
                }
                else
                {
                    WriteRuntimeActionPayload(
                        writer,
                        entry,
                        state,
                        capture.GameTime,
                        source?.Payload);
                }
            }

            writer.Write(-1);
            writer.Write(ActionTableSentinel);
            WriteLoreTracks(writer, capture.LoreTracks);
            writer.Write(ActionAuxiliaryTableSentinel);
        }

        private static void WriteDeclarations(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveObjectDeclaration> declarations,
            string separatorText)
        {
            ValidateObjectCount(declarations.Count);
            int previousObjectId = 1;
            for (int index = 0; index < declarations.Count; index++)
            {
                OriginalSaveObjectDeclaration declaration = declarations[index] ??
                    throw new InvalidDataException("A native object declaration is null.");
                ValidateObjectId(declaration.ObjectId, previousObjectId);
                ValidateClassName(declaration.ClassName, declaration.ObjectId);
                writer.Write(declaration.ObjectId);
                WriteNativeString(writer, declaration.ClassName);
                previousObjectId = declaration.ObjectId;
            }
            writer.Write(-1);
            WriteNativeString(writer, separatorText);
        }

        private static void WriteNewObjectBody(
            BinaryWriter writer,
            OriginalSaveCanonicalEntityState live,
            double gameTime,
            int triggerRecordId)
        {
            WriteNativeString(writer, live.ClassName);
            writer.Write(live.ObjectType);
            WriteVector(writer, live.Position);
            // N04 = a runtime transient the native loader clears on read: the original re-saves
            // every object with [0,0,0] here regardless of input, while live native saves carry
            // stale operation traces (nondeterministic by construction). Zero is the canonical
            // post-load value and keeps the round-trip byte-idempotent; duplicating Position
            // was a port invention.
            WriteVector(writer, new OriginalSaveVector3(0f, 0f, 0f));
            writer.Write(BitConverter.SingleToInt32Bits(live.CreationTimeHours));
            writer.Write(BitConverter.SingleToInt32Bits(live.WorkStart));
            writer.Write(BitConverter.SingleToInt32Bits(live.WorkLength));
            WriteNewCoreState(writer, live);
            WriteInt32Vector(writer, live.Inventory);
            WriteStringVector(writer, live.TaskList);
            if (live.FogBrightnessCache.Length == 0)
            {
                for (int index = 0; index < 16; index++) writer.Write(-1);
            }
            else
            {
                WriteValues(
                    writer,
                    live.FogBrightnessCache,
                    16,
                    nameof(live.FogBrightnessCache));
            }

            writer.Write(live.OwnsTclState ? 1 : 0);
            if (live.OwnsTclState)
            {
                writer.Write(live.TclVariables.Length);
                for (int index = 0; index < live.TclVariables.Length; index++)
                {
                    OriginalSaveTclVariable variable = live.TclVariables[index];
                    WriteNativeString(writer, variable.Name);
                    WriteNativeString(writer, variable.Value);
                }
            }

            // Native uses self here for 329/335 bodies; the six founder/StoryMgr -1 records
            // have no derivation law, so fresh bodies stay self.
            writer.Write(live.ObjectId);
            WriteNativeString(writer, live.InstanceName);
            // +0x200/+0x204/+0x208: live/target state-table indices + the think-enable word.
            // The old constant -1/-1/-1 left every fresh-written object with no state machine
            // and think-disabled forever — in the original (HandleStates +0x208>0 gate) and in
            // the port on reload.
            writer.Write(live.StateLiveIndex);
            writer.Write(live.StateTargetIndex);
            writer.Write(live.StateEnabledWord);
            writer.Write(0);
            writer.Write(1);
            writer.Write(live.PlaceSnapMode);
            writer.Write(live.WeaponClassId);
            writer.Write(live.ShieldClassId);
            WriteInt32Vector(writer, live.CurrentPartFrames);
            writer.Write(live.WalkResult);
            writer.Write(-1);
            WriteAttributePage(writer, live.AttributePageBits, live.HasNativeAttributePage);
            writer.Write(0);
            writer.Write(0);
            for (int index = 0; index < 4; index++) writer.Write(0);
            writer.Write(BitConverter.SingleToInt32Bits(live.FogOfWarLastX));
            writer.Write(BitConverter.SingleToInt32Bits(live.FogOfWarLastY));
            writer.Write(BitConverter.SingleToInt32Bits(live.FogOfWarLastZ));
            writer.Write(live.LinkedObjectId);
            writer.Write(live.LinkedSlot);
            // N22 words 5..9 + N23: the per-object light (object-form change_light,
            // FUN_00673F40) — offset XYZ, render-light handle (cold −1), range, then the color
            // triple scaled ×20 (corpus-proven; universal default [20,18,10] = {1,0.9,0.5}).
            writer.Write(BitConverter.SingleToInt32Bits(live.LightOffset.x));
            writer.Write(BitConverter.SingleToInt32Bits(live.LightOffset.y));
            writer.Write(BitConverter.SingleToInt32Bits(live.LightOffset.z));
            writer.Write(-1);
            writer.Write(BitConverter.SingleToInt32Bits(live.LightRange));
            writer.Write(UnityEngine.Mathf.RoundToInt(live.LightColor.x * 20f));
            writer.Write(UnityEngine.Mathf.RoundToInt(live.LightColor.y * 20f));
            writer.Write(UnityEngine.Mathf.RoundToInt(live.LightColor.z * 20f));
            writer.Write(live.ParticleStateWords.Length == 0 ? (byte)0 : (byte)1);
            if (live.ParticleStateWords.Length != 0)
            {
                WriteValues(
                    writer,
                    live.ParticleStateWords,
                    160,
                    nameof(live.ParticleStateWords));
            }
            writer.Write(live.WalkingActionActive);
            writer.Write(live.WorkTimeState);
            writer.Write(live.WorkTimeState);
            WriteNativeString(writer, live.InfoString);
            writer.Write(live.FogOfWarSizeX);
            writer.Write(live.FogOfWarSizeY);
            writer.Write(live.Owner);
            writer.Write(0);
            // The eight per-view render-job handles (CObj+0x37C..+0x398). The native allocator
            // (FUN_005F6A20:228-247) only assigns a render job when a handle is -1; the ctor
            // initializes all eight to -1 (FUN_005C83F0:227-238) and the port restores them
            // verbatim on load. Writing eight zeros with an empty render-job table would mean
            // "I already own job 0" for every object, so no job would ever be allocated, the
            // draw gate would never pass, and every model would be invisible.
            for (int index = 0; index < 8; index++) writer.Write(-1);
            writer.Write(live.AnimationModelDatabaseSlot);
            writer.Write(live.AnimationFrameValue);
            writer.Write(live.AnimationState);
            writer.Write(0);
            writer.Write(live.AnimationMode);
            writer.Write((byte)0);
            writer.Write(live.InitialAnimationModelDatabaseSlot);
            writer.Write(live.InitialAnimationState);
            writer.Write(live.InitialAnimationStartFrame);
            writer.Write(live.InitialAnimationMode);
            writer.Write(live.ObjectFlags);
            writer.Write(live.ObjectFlags);
            writer.Write(live.NativeMutationFlags);
            writer.Write(0);
            writer.Write(live.DigOrderId);
            writer.Write(-1);
            writer.Write(triggerRecordId);

            if (live.ObjectType is 1 or 11)
                WriteCanonicalFamily(writer, live.ObjectId, live.Family);
            if (live.ObjectType == 1)
                WriteCanonicalTalkIssues(writer, live.ObjectId, live.TalkIssues);
            if (live.ObjectType is 5 or 6 or 7 or 10)
                WriteCanonicalProduction(writer, live.ObjectId, live.Production);
            if (live.ObjectType == 1)
                WriteCanonicalPlanner(writer, live.ObjectId, live.Planner);

            WriteTextureAnimations(writer, live.TextureAnimations);
            // CObj icon holder (obj+0x2B8/2F8/308): lane word = the global icon-table slot this
            // object owns (-1 = empty), lane float = the live expiry countdown. An emitted slot
            // with a -1 holder is an orphan the original never expires (permanent HP bars). 2.5f
            // is the verified native founder countdown (byte-inspected in native gnome bodies).
            bool hasHolderLanes = live.IconHolderSlots.Length == 16;
            for (int index = 0; index < 16; index++)
                writer.Write(hasHolderLanes ? live.IconHolderSlots[index] : -1);
            writer.Write((byte)1);
            for (int index = 1; index < 8; index++) writer.Write((byte)0);
            for (int index = 8; index < 16; index++) writer.Write((byte)1);
            for (int index = 0; index < 16; index++)
            {
                bool laneLinked = hasHolderLanes && live.IconHolderSlots[index] >= 0;
                writer.Write(laneLinked
                    ? BitConverter.SingleToInt32Bits(2.5f)
                    : RetiredFloatMinusOne);
            }
            // N35 = the render-dirty latch obj+0x3B4: 1 after any real visibility flip /
            // model destruct, never cleared (FUN_0067cca0:79-94; byte-verified 232/103 split
            // across native bodies — aging mushrooms 1, never-toggled dwarves 0).
            writer.Write(live.RenderDirtyLatch ? (byte)1 : (byte)0);
            writer.Write(15);
            writer.Write((byte)0);
            writer.Write((byte)0);
            WriteEventQueue(writer, live.ObjectId, live.EventQueue);
            writer.Write(live.PickTargetMode);
            writer.Write((byte)0);
        }

        private static void WriteNewCoreState(
            BinaryWriter writer,
            OriginalSaveCanonicalEntityState live)
        {
            WriteValues(writer, live.CoreStateBits, 13, nameof(live.CoreStateBits));
        }

        private static void WriteCommonObjectBody(
            BinaryWriter writer,
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live,
            OriginalSaveObjectCorePatch? patch)
        {
            WriteNativeString(writer, source.ClassName);
            writer.Write(source.ObjectType);
            WriteVector(writer, live != null
                ? new OriginalSaveVector3(live.Position.x, live.Position.y, live.Position.z)
                : patch?.Position ?? source.Position);
            WriteVector(writer, source.TransientPosition);
            WriteState350(writer, source.State350, live, patch);
            WriteCoreState(writer, source.CoreState, live, patch);

            IReadOnlyList<int> childObjectIds = live?.Inventory ??
                patch?.ChildObjectIds ?? source.ChildObjectIds;
            WriteInt32Vector(writer, childObjectIds);
            WriteStringVector(writer, source.Strings);
            WriteValues(writer, source.StateAfterStrings, 16, nameof(source.StateAfterStrings));

            IReadOnlyList<OriginalSaveTclVariable> tclVariables = live?.TclVariables ??
                patch?.TclVariables ?? source.TclVariables;
            bool hasTclState = live != null
                ? live.OwnsTclState
                : patch?.TclVariables != null || source.HasTclState;
            writer.Write(hasTclState ? 1 : 0);
            if (hasTclState)
            {
                writer.Write(tclVariables.Count);
                for (int index = 0; index < tclVariables.Count; index++)
                {
                    WriteNativeString(writer, tclVariables[index].Name);
                    WriteNativeString(writer, tclVariables[index].Value);
                }
            }

            writer.Write(patch?.StateObjectId ?? source.StateObjectId);
            WriteNativeString(writer, live?.InstanceName ?? patch?.InstanceName ?? source.InstanceName);

            OriginalSaveObjectCommonStateDocument common = source.CommonState;
            // +0x200/+0x204/+0x208 come from the canonical capture when present (verbatim-
            // retained while the machine is untouched, live-derived after any state mutation);
            // the +0x20C dead word stays document-retained.
            if (live != null)
            {
                writer.Write(live.StateLiveIndex);
                writer.Write(live.StateTargetIndex);
                writer.Write(live.StateEnabledWord);
                writer.Write(common.State200[3]);
            }
            else
            {
                writer.Write(patch?.StateLiveWord ?? common.State200[0]);
                writer.Write(patch?.StateTargetWord ?? common.State200[1]);
                writer.Write(patch?.StateEnabledWord ?? common.State200[2]);
                writer.Write(common.State200[3]);
            }
            WriteState220(writer, common.State220, live, patch);
            WriteInt32Vector(writer, common.RouteValues);
            writer.Write(live?.WalkResult ?? common.RouteState);
            writer.Write(common.RouteTimeBits);

            if (live != null)
            {
                WriteAttributePage(writer, live.AttributePageBits, live.HasNativeAttributePage);
            }
            else
            {
                writer.Write(common.GridState.Count == 0 ? 0 : 1);
                if (common.GridState.Count != 0)
                    WriteValues(writer, common.GridState, 512, nameof(common.GridState));
            }

            WriteValues(writer, common.State264, 2, nameof(common.State264));
            WriteValues(writer, common.State254, 4, nameof(common.State254));
            WriteState26C(writer, common.State26C, live, patch);
            WriteValues(writer, common.State298, 3, nameof(common.State298));

            writer.Write(common.ParticleState.IsPresent ? (byte)1 : (byte)0);
            if (common.ParticleState.IsPresent)
            {
                WriteValues(
                    writer,
                    common.ParticleState.RawValues,
                    160,
                    nameof(common.ParticleState));
            }

            writer.Write(common.State2B4);
            writer.Write(common.State35C);
            writer.Write(common.State35D);
            WriteNativeString(writer, live != null
                ? live.InfoString
                : patch?.DisplayState ?? common.DisplayState);
            writer.Write(live?.FogOfWarSizeX ?? patch?.FogOfWarSizeX ?? common.FogOfWarSizeX);
            writer.Write(live?.FogOfWarSizeY ?? patch?.FogOfWarSizeY ?? common.FogOfWarSizeY);
            writer.Write(live?.Owner ?? patch?.Owner ?? common.Owner);
            writer.Write(common.State378);
            WriteValues(writer, common.State37C, 8, nameof(common.State37C));
            WriteValues(writer, common.State39C, 5, nameof(common.State39C));
            writer.Write(common.State3B4);
            WriteValues(writer, common.State3BC, 4, nameof(common.State3BC));
            WriteLateCoreState(writer, common.LateCoreState, live, patch);
            writer.Write(common.TriggerRecordId);
        }

        private static void WriteObjectBodyTail(
            BinaryWriter writer,
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live)
        {
            if (source.ObjectType is 1 or 11)
                WriteFamily(writer, source, live);
            if (source.ObjectType == 1)
                WriteTalkIssues(writer, source, live);
            if (source.ObjectType is 5 or 6 or 7 or 10)
                WriteProduction(writer, source, live);
            if (source.ObjectType == 1)
                WritePlanner(writer, source, live);

            WriteAttachmentHistory(writer, source.AttachmentHistory);
            WriteValues(writer, source.RetiredGroups.State2B8, 16, "retired State2B8");
            WriteBytes(writer, source.RetiredGroups.State2F8, 16, "retired State2F8");
            WriteValues(writer, source.RetiredGroups.State308, 16, "retired State308");
            writer.Write(source.TailFlags.Flag348);
            writer.Write(source.TailFlags.CountDownWord);
            writer.Write(source.TailFlags.MoverStateByte);

            writer.Write(source.ActionVariant != null ? (byte)1 : (byte)0);
            if (source.ActionVariant != null) WriteActionVariant(writer, source.ActionVariant);

            WriteEventQueue(writer, source, live);
            writer.Write(live?.PickTargetMode ?? source.PickTargetMode);
            writer.Write(source.DestroyQueued);
        }

        private static void WriteFamily(
            BinaryWriter writer,
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live)
        {
            if (live?.Family is OriginalSaveCanonicalFamilyState family)
            {
                WriteCanonicalFamily(writer, source.ObjectId, family);
                return;
            }

            OriginalSaveObjectFamilyExtensionDocument retained = source.FamilyExtension ??
                throw new InvalidDataException(
                    $"Native object {source.ObjectId} is missing its family extension.");
            WriteActorLink(writer, retained.PartnerLink);
            WriteActorLink(writer, retained.MotherLink);
            WriteActorLink(writer, retained.FatherLink);
            WriteStringVector(writer, retained.ChildrenNames);
            writer.Write(retained.PregnancyFlag ? (byte)1 : (byte)0);
        }

        private static void WriteCanonicalFamily(
            BinaryWriter writer,
            int objectId,
            OriginalSaveCanonicalFamilyState? family)
        {
            if (family == null)
                throw new InvalidDataException(
                    $"Native object {objectId} is missing its canonical family state.");
            WriteActorLink(
                writer,
                family.PartnerId,
                family.PartnerState,
                family.PartnerReferenceObjectId);
            WriteActorLink(
                writer,
                family.MotherId,
                family.MotherState,
                family.MotherReferenceObjectId);
            WriteActorLink(
                writer,
                family.FatherId,
                family.FatherState,
                family.FatherReferenceObjectId);
            WriteStringVector(writer, family.ChildrenNames);
            writer.Write(family.PregnancyFlag ? (byte)1 : (byte)0);
        }

        private static void WriteTalkIssues(
            BinaryWriter writer,
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live)
        {
            if (live?.TalkIssues is OriginalSaveCanonicalTalkIssueState talkIssues)
            {
                WriteCanonicalTalkIssues(writer, source.ObjectId, talkIssues);
                return;
            }

            OriginalSaveObjectTalkIssueExtensionDocument retained = source.TalkIssueExtension ??
                throw new InvalidDataException(
                    $"Native object {source.ObjectId} is missing its talk-issue extension.");
            writer.Write(retained.TalkDesireSum.Bits);
            for (int index = 0; index < retained.Entries.Count; index++)
            {
                writer.Write(1);
                WriteNativeString(writer, retained.Entries[index].Name);
                writer.Write(retained.Entries[index].Weight.Bits);
            }
            writer.Write(-1);
        }

        private static void WriteCanonicalTalkIssues(
            BinaryWriter writer,
            int objectId,
            OriginalSaveCanonicalTalkIssueState? talkIssues)
        {
            if (talkIssues == null)
                throw new InvalidDataException(
                    $"Native object {objectId} is missing its canonical talk-issue state.");
            writer.Write(BitConverter.SingleToInt32Bits((float)talkIssues.StoredSum));
            for (int index = 0; index < talkIssues.Entries.Length; index++)
            {
                KeyValuePair<string, double> entry = talkIssues.Entries[index];
                writer.Write(1);
                WriteNativeString(writer, entry.Key);
                writer.Write(BitConverter.SingleToInt32Bits((float)entry.Value));
            }
            writer.Write(-1);
        }

        private static void WriteProduction(
            BinaryWriter writer,
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live)
        {
            if (live?.Production is OriginalSaveCanonicalProductionState production)
            {
                WriteCanonicalProduction(writer, source.ObjectId, production);
                return;
            }

            OriginalSaveObjectProductionExtensionDocument retained = source.ProductionExtension ??
                throw new InvalidDataException(
                    $"Native object {source.ObjectId} is missing its CProd extension.");
            writer.Write(retained.Slots.Count);
            for (int index = 0; index < retained.Slots.Count; index++)
            {
                OriginalSaveObjectProductionSlotDocument slot = retained.Slots[index];
                writer.Write(slot.RegistryValue1);
                writer.Write(slot.RegistryValue2);
                WriteNativeString(writer, slot.ClassName);
                WriteNativeString(writer, slot.ResourceName);
                writer.Write(slot.OrderCount);
                writer.Write(slot.Demand.Bits);
                writer.Write(slot.Invented);
                writer.Write(slot.Buildable);
            }
            writer.Write(retained.WorkState);
            writer.Write(retained.AssignedWorkerObjectId);
            // Preserve the same repeated +0x28 byte and +0x2D allocator residue on round-trip.
            WriteBytes(writer, retained.RequestBytes, 7, "CProd request bytes");
            WriteValues(writer, retained.PendingPositionBits, 3, "CProd position");
            WriteBytes(writer, retained.FlowBytes, 8, "CProd flow bytes");
            WriteValues(writer, retained.ProductionValues, 4, "CProd production state");
            writer.Write(retained.ProductionEnabled);
            WriteValues(writer, retained.EnergyValues, 5, "CProd energy state");
            if (retained.GuestSeats.Count != 16)
                throw new InvalidDataException("A native CProd extension requires sixteen guests.");
            for (int index = 0; index < retained.GuestSeats.Count; index++)
            {
                OriginalSaveObjectProductionGuestSeatDocument guest = retained.GuestSeats[index];
                writer.Write(guest.SeatLinkId);
                writer.Write(guest.GuestObjectId);
                writer.Write(guest.OrderCount);
            }
            writer.Write(retained.State3C);
            writer.Write(retained.State140);
        }

        private static void WriteCanonicalProduction(
            BinaryWriter writer,
            int objectId,
            OriginalSaveCanonicalProductionState? production)
        {
            if (production == null)
                throw new InvalidDataException(
                    $"Native object {objectId} is missing its canonical CProd state.");
            ValidateProduction(production, objectId);
            writer.Write(production.Slots.Length);
            for (int index = 0; index < production.Slots.Length; index++)
            {
                OriginalSaveCanonicalProductionSlotState slot = production.Slots[index] ??
                    throw new InvalidDataException(
                        $"Native object {objectId} has a null production slot.");
                writer.Write(slot.RegistryValue1);
                writer.Write(slot.RegistryValue2);
                WriteNativeString(writer, slot.ClassName);
                WriteNativeString(writer, slot.ResourceName);
                writer.Write(slot.OrderCount);
                writer.Write(slot.DemandBits);
                writer.Write(slot.Invented);
                writer.Write(slot.Buildable);
            }
            writer.Write(production.WorkState);
            writer.Write(production.AssignedWorkerObjectId);
            writer.Write(production.RequestBytes);
            WriteValues(writer, production.PendingPositionBits, 3, "production position");
            writer.Write(production.FlowCompatibilityBytes);
            WriteValues(writer, production.ProductionValues, 4, "production state");
            writer.Write(production.ProductionEnabled);
            WriteValues(writer, production.EnergyValues, 5, "production energy");
            for (int index = 0; index < production.GuestSeats.Length; index++)
            {
                OriginalSaveCanonicalGuestSeatState guest = production.GuestSeats[index];
                writer.Write(guest.LinkId);
                writer.Write(guest.GuestObjectId);
                writer.Write(guest.OrderCount);
            }
            writer.Write(production.State3C);
            writer.Write(production.State140);
        }

        private static void WritePlanner(
            BinaryWriter writer,
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live)
        {
            if (live?.Planner is OriginalSaveCanonicalPlannerState planner)
            {
                WriteCanonicalPlanner(writer, source.ObjectId, planner);
                return;
            }

            OriginalSaveObjectPlannerExtensionDocument retained = source.PlannerExtension ??
                throw new InvalidDataException(
                    $"Native object {source.ObjectId} is missing its planner extension.");
            WriteValues(writer, retained.Values, 4, "planner extension");
        }

        private static void WriteCanonicalPlanner(
            BinaryWriter writer,
            int objectId,
            OriginalSaveCanonicalPlannerState? planner)
        {
            if (planner == null)
                throw new InvalidDataException(
                    $"Native object {objectId} is missing its canonical planner state.");
            writer.Write(planner.IdleAnnounceFlag);
            writer.Write(BitConverter.SingleToInt32Bits(planner.IdleSince));
            writer.Write(planner.CurrentWorkplaceObjectId);
            writer.Write(planner.AssignedWorkplaceObjectId);
        }

        private static void WriteAttachmentHistory(
            BinaryWriter writer,
            OriginalSaveObjectAttachmentHistoryDocument history)
        {
            writer.Write(history.IsPresent ? (byte)1 : (byte)0);
            if (!history.IsPresent) return;
            writer.Write(history.Records.Count);
            for (int index = 0; index < history.Records.Count; index++)
            {
                OriginalSaveObjectAttachmentHistoryRecordDocument record = history.Records[index];
                WriteInt32Vector(writer, record.RouteValues);
                writer.Write(record.TrailingValue1);
                writer.Write(record.TrailingValue2);
            }
        }

        private static void WriteTextureAnimations(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveCanonicalTextureAnimationState> animations)
        {
            writer.Write(animations.Count == 0 ? (byte)0 : (byte)1);
            if (animations.Count == 0) return;
            writer.Write(animations.Count);
            for (int index = 0; index < animations.Count; index++)
            {
                OriginalSaveCanonicalTextureAnimationState animation = animations[index] ??
                    throw new InvalidDataException("A canonical texture animation is null.");
                WriteInt32Vector(writer, animation.FrameValues);
                writer.Write(animation.Cursor);
                writer.Write(animation.PlaybackMode);
            }
        }

        private static void WriteActionVariant(
            BinaryWriter writer,
            OriginalSaveObjectActionVariantDocument variant)
        {
            writer.Write(variant.Variant);
            switch (variant.Variant)
            {
                case 0:
                    WriteValues(writer, variant.LeadingValues, 8, "action variant zero leading");
                    int entryCount = variant.EntryCount ??
                        throw new InvalidDataException("Action variant zero has no entry count.");
                    writer.Write(entryCount);
                    WriteValues(
                        writer,
                        variant.EntryValues,
                        checked(entryCount * 10),
                        "action variant zero entries");
                    WriteValues(
                        writer,
                        variant.ValuesBeforeFlag,
                        66,
                        "action variant zero values");
                    writer.Write(variant.Flag ? (byte)1 : (byte)0);
                    return;
                case 1:
                    WriteValues(writer, variant.ValuesBeforeFlag, 7, "action variant one values");
                    writer.Write(variant.Flag ? (byte)1 : (byte)0);
                    WriteValues(writer, variant.ValuesAfterFlag, 2, "action variant one tail");
                    return;
                case 2:
                    WriteValues(writer, variant.ValuesBeforeFlag, 7, "action variant two values");
                    writer.Write(variant.Flag ? (byte)1 : (byte)0);
                    WriteValues(writer, variant.ValuesAfterFlag, 1, "action variant two tail");
                    WriteInt32Vector(writer, variant.VectorValues);
                    WriteStringVector(writer, variant.StringValues);
                    WriteValues(writer, variant.TrailingValues, 1, "action variant two trailing");
                    return;
                default:
                    throw new InvalidDataException(
                        $"Unsupported native object action variant {variant.Variant}.");
            }
        }

        private static void WriteEventQueue(
            BinaryWriter writer,
            OriginalSaveObjectDocument source,
            OriginalSaveCanonicalEntityState? live)
        {
            if (live != null)
            {
                WriteEventQueue(writer, source.ObjectId, live.EventQueue);
                return;
            }

            int readCursor;
            int writeCursor;
            IReadOnlyList<OriginalSaveTimerEventPayloadDocument?> slots;
            IReadOnlyList<OriginalSaveTimerEventPayloadDocument> delayed;
            readCursor = source.EventQueueDocument.ReadCursor;
            writeCursor = source.EventQueueDocument.WriteCursor;
            slots = source.EventQueueDocument.Slots;
            delayed = source.EventQueueDocument.DelayedRecords;

            ValidateEventQueue(readCursor, writeCursor, slots, source.ObjectId);
            writer.Write(readCursor);
            writer.Write(writeCursor);
            for (int index = 0; index < slots.Count; index++)
            {
                OriginalSaveTimerEventPayloadDocument? payload = slots[index];
                writer.Write(payload != null ? (byte)1 : (byte)0);
                if (payload != null) WriteEventPayload(writer, payload, source.ObjectId);
            }
            writer.Write(delayed.Count);
            for (int index = 0; index < delayed.Count; index++)
                WriteEventPayload(writer, delayed[index], source.ObjectId);
        }

        private static void WriteEventQueue(
            BinaryWriter writer,
            int objectId,
            OriginalSaveCanonicalEventQueueState queue)
        {
            if (queue == null)
                throw new InvalidDataException(
                    $"Native object {objectId} is missing its canonical event queue.");
            ValidateEventQueue(queue.ReadCursor, queue.WriteCursor, queue.Slots, objectId);
            writer.Write(queue.ReadCursor);
            writer.Write(queue.WriteCursor);
            for (int index = 0; index < queue.Slots.Length; index++)
            {
                OriginalSaveTimerEventPayloadDocument? payload = queue.Slots[index];
                writer.Write(payload != null ? (byte)1 : (byte)0);
                if (payload != null) WriteEventPayload(writer, payload, objectId);
            }
            writer.Write(queue.DelayedRecords.Length);
            for (int index = 0; index < queue.DelayedRecords.Length; index++)
                WriteEventPayload(writer, queue.DelayedRecords[index], objectId);
        }

        private static RetainedStructureLayout FindRetainedStructureLayout(
            byte[] payload,
            OriginalSaveObjectManifest manifest)
        {
            int managerTailOffset = checked((int)manifest.BodyStreamEndOffset);
            int marker7Offset = checked((int)manifest.Marker7Offset);
            var cursor = new OriginalSavePayloadCursor(payload, managerTailOffset, marker7Offset);
            cursor.SkipInt32Values(OwnerAttributeBlockCount * 2 * AttributeSlotCount);
            cursor.SkipInt32Values(2);
            for (int group = 0; group < 3; group++)
            {
                SkipObjectManagerArray(cursor);
                SkipObjectManagerArray(cursor);
                cursor.SkipInt32Values(cursor.ReadCount());
                cursor.Skip(2);
            }
            int actionRecordCount = cursor.ReadCount();
            if (actionRecordCount != 0)
                throw new InvalidDataException(
                    "Class-specific object-manager actions cannot be retained structurally.");

            int classTableOffset = cursor.Position;
            cursor.SkipInt32Values(1 + ClassTableCapacity + 1);
            cursor.Skip(1);
            cursor.SkipInt32Values(cursor.ReadCount());
            int cleanupQueueOffset = cursor.Position;
            cursor.SkipInt32Values(cursor.ReadCount());
            int cleanupQueueEndOffset = cursor.Position;

            int previousEntryIndex = -1;
            int entryIndex = cursor.ReadInt32();
            while (entryIndex != -1)
            {
                if (entryIndex <= previousEntryIndex || entryIndex >= ClassTableCapacity)
                    throw new InvalidDataException(
                        $"Invalid retained class-table entry index {entryIndex}.");
                SkipClassTableEntry(cursor);
                previousEntryIndex = entryIndex;
                entryIndex = cursor.ReadInt32();
            }

            int spatialIndexOffset = cursor.Position;
            for (int bucket = 0; bucket < manifest.SpatialBucketCount; bucket++)
            {
                int recordCount = cursor.ReadCount();
                for (int record = 0; record < recordCount; record++) SkipSpatialIndexRecord(cursor);
            }
            if (cursor.Position != marker7Offset)
                throw new InvalidDataException(
                    $"Retained structures ended at {cursor.Position}; expected {marker7Offset}.");

            return new RetainedStructureLayout(
                classTableOffset,
                cleanupQueueOffset,
                cleanupQueueEndOffset,
                spatialIndexOffset);
        }

        private static void SkipObjectManagerArray(OriginalSavePayloadCursor cursor)
        {
            int count = cursor.ReadCount();
            for (int index = 0; index < count; index++)
            {
                cursor.ReadNativeString();
                cursor.SkipInt32Values(7);
                cursor.Skip(3);
                cursor.ReadNativeString();
                cursor.SkipInt32Values(1);
            }
        }

        private static void SkipClassTableEntry(OriginalSavePayloadCursor cursor)
        {
            cursor.SkipInt32Values(20);
            cursor.SkipInt32Values(cursor.ReadCount());
            cursor.SkipInt32Values(2);
            cursor.ReadNativeString();
            cursor.Skip(1);
            cursor.SkipInt32Values(4);
            cursor.Skip(2);
            cursor.SkipInt32Values(10);
            cursor.ReadNativeString();
            cursor.Skip(3);
            cursor.SkipInt32Values(1);
            cursor.SkipInt32Values(6);
            cursor.Skip(1);
        }

        private static void SkipSpatialIndexRecord(OriginalSavePayloadCursor cursor)
        {
            cursor.SkipInt32Values(6);
            cursor.ReadNativeString();
            cursor.SkipInt32Values(4);
            cursor.Skip(2);
        }

        private static byte[] Slice(byte[] source, int start, int end)
        {
            if (start < 0 || end < start || end > source.Length)
                throw new ArgumentOutOfRangeException(nameof(start));
            var result = new byte[end - start];
            Buffer.BlockCopy(source, start, result, 0, result.Length);
            return result;
        }

        private static void WriteActionTable(
            BinaryWriter writer,
            OriginalSaveActionTableDocument source)
        {
            var owners = new HashSet<int>();
            for (int index = 0; index < source.Records.Count; index++)
            {
                OriginalSaveActionRecord record = source.Records[index];
                if (record.OwnerObjectId < 0 || record.OwnerObjectId > MaximumObjectId ||
                    !owners.Add(record.OwnerObjectId))
                {
                    throw new InvalidDataException(
                        $"Native action record {index} has an invalid or duplicate owner.");
                }
                writer.Write(record.OwnerObjectId);
                writer.Write((int)record.Type);
                WriteActionPayload(writer, record.Payload);
            }

            writer.Write(-1);
            writer.Write(ActionTableSentinel);
            writer.Write(source.AuxiliaryRecords.Count);
            for (int index = 0; index < source.AuxiliaryRecords.Count; index++)
            {
                OriginalSaveActionAuxiliaryRecord record = source.AuxiliaryRecords[index];
                writer.Write(record.Value);
                writer.Write(record.Vectors.Count);
                for (int vector = 0; vector < record.Vectors.Count; vector++)
                {
                    WriteValues(
                        writer,
                        record.Vectors[vector].Values,
                        8,
                        "action auxiliary vector");
                }
                WriteValues(writer, record.TrailingValues, 3, "action auxiliary tail");
            }
            writer.Write(ActionAuxiliaryTableSentinel);
        }

        private static void WriteActionPayload(
            BinaryWriter writer,
            OriginalSaveActionPayload payload)
        {
            switch (payload)
            {
                case OriginalSaveNullActionPayload:
                case OriginalSaveLegacySlideActionPayload:
                    return;
                case OriginalSaveWaitActionPayload wait:
                    WriteActionCommon(writer, wait.Common!);
                    writer.Write(wait.AbsoluteDeadline);
                    return;
                case OriginalSaveAnimActionPayload animation:
                    WriteActionCommon(writer, animation.Common!);
                    writer.Write(animation.AbsoluteDeadline);
                    return;
                case OriginalSaveCrawlActionPayload crawl:
                    WriteActionCommon(writer, crawl.Common!);
                    writer.Write(crawl.RemainingTicks);
                    writer.Write(crawl.RandomYawOrPhase.Bits);
                    return;
                case OriginalSaveRotateActionPayload rotate:
                    WriteActionCommon(writer, rotate.Common!);
                    writer.Write(rotate.DirectionSign);
                    writer.Write(rotate.LastAppliedDirectionOrState);
                    writer.Write(rotate.AngularStep.Bits);
                    writer.Write(rotate.LeftAnimationSlot);
                    writer.Write(rotate.RightAnimationSlot);
                    writer.Write(rotate.TurnClipFrameCount);
                    writer.Write(rotate.AbsoluteTargetYaw.Bits);
                    return;
                case OriginalSaveAttackActionPayload attack:
                    WriteNativeString(writer, attack.ChainedActionVerb);
                    WriteNativeString(writer, attack.ChainedActionArguments);
                    WriteRaw32Values(writer, attack.LeadingRawValues, 5, "attack leading values");
                    writer.Write(attack.AbsoluteDeadline);
                    WriteBytes(writer, attack.DeadlineFlags, 3, "attack deadline flags");
                    writer.Write(attack.CurrentFrameOrTick);
                    WriteBytes(writer, attack.FrameFlags, 4, "attack frame flags");
                    WriteRaw32Values(
                        writer,
                        attack.ImpactAndTrailingRawValues,
                        5,
                        "attack impact values");
                    writer.Write(attack.TrailingFlag);
                    WriteActionCommon(writer, attack.Common!);
                    return;
                case OriginalSaveAttackBallisticActionPayload ballistic:
                    writer.Write(ballistic.TargetObjectId);
                    writer.Write(ballistic.Mode);
                    WriteInt32Vector(writer, ballistic.ScheduleValues);
                    WriteRaw32Values(writer, ballistic.FirstRawValues, 2, "ballistic first values");
                    WriteInt32Vector(writer, ballistic.TransientHandles);
                    WriteRaw32Values(writer, ballistic.SecondRawValues, 2, "ballistic second values");
                    writer.Write(ballistic.PrimaryFlag);
                    WriteRaw32Values(writer, ballistic.LastTarget, 3, "ballistic last target");
                    writer.Write(ballistic.SecondaryFlag);
                    writer.Write(ballistic.TertiaryFlag);
                    writer.Write(ballistic.TargetVerticalOffset.Bits);
                    writer.Write(ballistic.AbsoluteDeadline);
                    WriteActionCommon(writer, ballistic.Common!);
                    return;
                case OriginalSaveFlyActionPayload fly:
                    WriteActionCommon(writer, fly.Common!);
                    WriteRaw32Values(writer, fly.MotionValues, 12, "fly motion values");
                    writer.Write(fly.ArrivedOrCloseMode);
                    writer.Write(fly.TickCount);
                    writer.Write(fly.SwimMode);
                    return;
                case OriginalSaveLoreActionPayload lore:
                    WriteActionCommon(writer, lore.Common!);
                    writer.Write(lore.Gravity.Bits);
                    writer.Write(lore.Friction.Bits);
                    writer.Write(lore.Targets.Count);
                    for (int index = 0; index < lore.Targets.Count; index++)
                    {
                        writer.Write(lore.Targets[index].ObjectId);
                        writer.Write(lore.Targets[index].OffsetOrDistance.Bits);
                    }
                    writer.Write(lore.Waypoints.Count);
                    for (int index = 0; index < lore.Waypoints.Count; index++)
                        WriteRaw32Values(writer, lore.Waypoints[index].Values, 8, "lore waypoint");
                    return;
                case OriginalSaveWalkActionPayload walk:
                    WriteActionCommon(writer, walk.Common!);
                    if (walk.AnimationBanks.Count != 4)
                        throw new InvalidDataException("A native Walk action requires four banks.");
                    for (int index = 0; index < walk.AnimationBanks.Count; index++)
                    {
                        writer.Write(walk.AnimationBanks[index].BankStateOrAnimationSet);
                        WriteRaw32Values(
                            writer,
                            walk.AnimationBanks[index].SlotValues,
                            24 * 5,
                            "walk animation bank");
                    }
                    writer.Write(walk.MoveNeededOrApproach);
                    writer.Write(walk.Waving);
                    writer.Write(walk.DontStop);
                    WriteRaw32Values(
                        writer,
                        walk.TargetAndDistanceValues,
                        4,
                        "walk target values");
                    writer.Write(walk.CanClimb);
                    writer.Write(walk.UseObjects);
                    writer.Write(walk.RandomPath);
                    writer.Write(walk.RandomZ);
                    WriteBytes(writer, walk.MoveFlags, 4, "walk move flags");
                    writer.Write(walk.ObjectBreakClassId);
                    writer.Write(walk.ObjectBreakDistance.Bits);
                    writer.Write(walk.TargetDistance.Bits);
                    writer.Write(walk.ClimbRotationX.Bits);
                    writer.Write(walk.ClimbRotationY.Bits);
                    writer.Write(walk.ClimbRotationZ.Bits);
                    writer.Write(walk.DiagonalSqueezeFlag);
                    writer.Write(walk.RandomPathWallDistance.Bits);
                    writer.Write(walk.WalkTimerCounter);
                    writer.Write(walk.TickCounter);
                    writer.Write(walk.SpeedType);
                    WriteNativeString(writer, walk.WalkTimerCallback);
                    WriteNativeString(writer, walk.WalkCollisionCallback);
                    WriteNativeString(writer, walk.ObjectUseCallback);
                    return;
                case OriginalSaveMoveActionPayload move:
                    WriteActionCommon(writer, move.Common!);
                    WriteRaw32Values(writer, move.Vectors, 9, "move vectors");
                    writer.Write(move.RemainingNativeTicks);
                    return;
                default:
                    throw new InvalidDataException(
                        $"Unsupported native action payload {payload.GetType().Name}.");
            }
        }

        private static void WriteRuntimeActionPayload(
            BinaryWriter writer,
            RuntimeEntityActionSaveStateEntry entry,
            RuntimeEntityActionSaveState? state,
            double gameTime,
            OriginalSaveActionPayload? retained)
        {
            switch (state)
            {
                case null:
                    return;
                case WaitActionSaveState wait:
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    writer.Write(AbsoluteDeadline(gameTime, wait.RemainingSeconds));
                    return;
                case AnimActionSaveState animation:
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    writer.Write(AbsoluteDeadline(gameTime, animation.RemainingSeconds));
                    return;
                case NativeCrawlActionSaveState crawl:
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    writer.Write(crawl.RemainingTicks);
                    writer.Write(crawl.RandomYawOrPhaseBits);
                    return;
                case RotateActionSaveState rotate:
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    writer.Write(rotate.NativeDirectionSign);
                    writer.Write(rotate.NativeLastAppliedDirectionOrState);
                    writer.Write(rotate.AngularStepRadiansPerTick);
                    writer.Write(rotate.NativeLeftAnimationSlot);
                    writer.Write(rotate.NativeRightAnimationSlot);
                    writer.Write(rotate.NativeTurnClipFrameCount);
                    writer.Write(rotate.AbsoluteTargetYawRadians);
                    return;
                case NativeAttackActionSaveState attack:
                    WriteNativeString(writer, attack.ChainedActionVerb);
                    WriteNativeString(writer, attack.ChainedActionArguments);
                    WriteValues(writer, attack.LeadingRawBits, 5, "attack leading values");
                    writer.Write(AbsoluteDeadline(gameTime, attack.RemainingSeconds));
                    WriteBytes(writer, attack.DeadlineFlags, 3, "attack deadline flags");
                    writer.Write(attack.CurrentFrameOrTick);
                    WriteBytes(writer, attack.FrameFlags, 4, "attack frame flags");
                    WriteValues(
                        writer,
                        attack.ImpactAndTrailingRawBits,
                        5,
                        "attack impact values");
                    writer.Write(attack.TrailingFlag);
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    return;
                case NativeAttackBallisticActionSaveState ballistic:
                    writer.Write(ballistic.TargetObjectId);
                    writer.Write(ballistic.Mode);
                    WriteInt32Vector(writer, ballistic.CopyScheduleValues());
                    WriteValues(writer, ballistic.CopyFirstRawBits(), 2, "ballistic first values");
                    WriteInt32Vector(writer, ballistic.CopyTransientHandles());
                    WriteValues(writer, ballistic.CopySecondRawBits(), 2, "ballistic second values");
                    writer.Write(ballistic.PrimaryFlag);
                    WriteValues(writer, ballistic.CopyLastTargetBits(), 3, "ballistic last target");
                    writer.Write(ballistic.SecondaryFlag);
                    writer.Write(ballistic.TertiaryFlag);
                    writer.Write(ballistic.TargetVerticalOffsetBits);
                    writer.Write(AbsoluteDeadline(gameTime, ballistic.RemainingSeconds));
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    return;
                case FlyActionSaveState fly:
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    WriteVector(writer, fly.TargetWorld);
                    WriteVector(writer, fly.VelocityOrStepWorld);
                    WriteVector(writer, fly.PreviousPositionWorld);
                    writer.Write(fly.NativeHeadingOrPhase);
                    writer.Write(fly.NativeAngularVelocity);
                    writer.Write(fly.NativeVerticalAccumulator);
                    writer.Write(fly.ArrivedOrCloseMode);
                    writer.Write(fly.TickCount);
                    writer.Write(fly.SwimMode);
                    return;
                case NativeLoreActionSaveState lore:
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    writer.Write(lore.GravityBits);
                    writer.Write(lore.FrictionBits);
                    NativeLoreTargetSaveState[] targets = lore.CopyTargets();
                    writer.Write(targets.Length);
                    for (int index = 0; index < targets.Length; index++)
                    {
                        writer.Write(targets[index].ObjectId);
                        writer.Write(targets[index].OffsetOrDistanceBits);
                    }
                    int[] waypointBits = lore.CopyWaypointBits();
                    writer.Write(waypointBits.Length / 8);
                    WriteValues(writer, waypointBits, waypointBits.Length, "lore waypoints");
                    return;
                case WalkActionSaveState walk:
                    WriteRuntimeWalkAction(writer, entry, walk, retained?.Common);
                    return;
                case MoveActionSaveState move:
                    WriteRuntimeActionCommon(writer, entry, state, retained?.Common);
                    WriteVector(writer, move.Start);
                    WriteVector(writer, move.Target);
                    int totalTicks = Math.Max(1, (int)Math.Round(move.DurationSeconds / 0.1d));
                    WriteVector(writer, (move.Target - move.Start) / totalTicks);
                    writer.Write(move.RemainingNativeTicks);
                    return;
                default:
                    throw new NotSupportedException(
                        $"Runtime action state {state.GetType().Name} has no native writer.");
            }
        }

        private static void WriteRuntimeWalkAction(
            BinaryWriter writer,
            RuntimeEntityActionSaveStateEntry entry,
            WalkActionSaveState walk,
            OriginalSaveActionCommonState? retainedCommon)
        {
            WriteRuntimeActionCommon(writer, entry, walk, retainedCommon);
            int[] bankStates = walk.CopyNativeAnimationBankStates();
            int[] slotBits = walk.CopyNativeAnimationSlotBits();
            if (bankStates.Length != 4 || slotBits.Length != 4 * 24 * 5)
                throw new InvalidDataException("A runtime Walk action has incomplete animation banks.");
            int slotOffset = 0;
            for (int bank = 0; bank < bankStates.Length; bank++)
            {
                writer.Write(bankStates[bank]);
                for (int value = 0; value < 24 * 5; value++)
                    writer.Write(slotBits[slotOffset++]);
            }
            writer.Write(walk.MoveNeededOrApproach);
            writer.Write(walk.Waving);
            writer.Write(walk.DontStop);
            writer.Write(walk.StopAt);
            WriteVector(writer, walk.TargetNative);
            writer.Write(walk.CanClimb);
            writer.Write(walk.UseObjects);
            writer.Write(walk.RandomPath);
            writer.Write(walk.RandomZ);
            byte[] moveFlags = walk.CopyMoveFlags();
            if (moveFlags.Length != 4)
                throw new InvalidDataException("A runtime Walk action has incomplete move flags.");
            writer.Write(moveFlags);
            writer.Write(walk.ObjectBreakClassId);
            writer.Write(walk.ObjectBreakDistance);
            writer.Write(walk.TargetDistance);
            WriteVector(writer, walk.ClimbRotation);
            writer.Write(walk.DiagonalSqueezeFlag);
            writer.Write(walk.RandomPathWallDistance);
            writer.Write(walk.WalkTimerCounter);
            writer.Write(walk.TickCounter);
            writer.Write(walk.SpeedType);
            WriteNativeString(writer, walk.WalkTimerCallback);
            WriteNativeString(writer, walk.WalkCollisionCallback);
            WriteNativeString(writer, walk.ObjectUseCallback);
        }

        private static void WriteRuntimeActionCommon(
            BinaryWriter writer,
            RuntimeEntityActionSaveStateEntry entry,
            RuntimeEntityActionSaveState state,
            OriginalSaveActionCommonState? retained)
        {
            if (state is AnimActionSaveState animation)
                WriteNativeString(writer, animation.Clip);
            else if (retained != null)
                WriteNativeString(writer, retained.Argument);
            else if (state is WalkActionSaveState walk)
                WriteNativeString(writer, walk.NativeArgument);
            else
                WriteNativeString(writer, string.Empty);

            writer.Write(entry.ObjectId);
            // The finish/break Tcl codes are load-bearing: the whole idle discipline re-enables
            // state thinking through `action this wait 1 {state_enable this}` — a Wait restored
            // with an empty finishcode leaves its owner think-dead forever (native corpus Waits
            // carry 'state_enable this'). The live codes sit on the entity (ent.ActionFinish/
            // ActionBreak), captured into the entry; the retained Common is only the fallback
            // for actions the live session never touched.
            if (entry.FinishCode != null) WriteNativeString(writer, entry.FinishCode);
            else if (retained != null) WriteNativeString(writer, retained.FinishCode);
            else WriteNativeString(writer, string.Empty);
            if (entry.BreakCode != null) WriteNativeString(writer, entry.BreakCode);
            else if (retained != null) WriteNativeString(writer, retained.BreakCode);
            else WriteNativeString(writer, string.Empty);
            writer.Write(retained?.StatusOrOpaque ??
                (state is WalkActionSaveState walkState ? walkState.StatusOrOpaque : 0));
        }

        private static void WriteActionCommon(
            BinaryWriter writer,
            OriginalSaveActionCommonState common)
        {
            WriteNativeString(writer, common.Argument);
            writer.Write(common.OwnerObjectId);
            WriteNativeString(writer, common.FinishCode);
            WriteNativeString(writer, common.BreakCode);
            writer.Write(common.StatusOrOpaque);
        }

        private static void WriteLoreTracks(BinaryWriter writer, IReadOnlyList<LoreTrackRecord> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var owners = new HashSet<int>();
            writer.Write(records.Count);
            for (int index = 0; index < records.Count; index++)
            {
                LoreTrackRecord record = records[index] ??
                    throw new InvalidDataException("A canonical Lore track is null.");
                if (!owners.Add(record.OwnerId))
                    throw new InvalidDataException(
                        $"Lore track owner {record.OwnerId} appears more than once.");
                writer.Write(record.OwnerId);
                writer.Write(record.Waypoints.Count);
                for (int waypointIndex = 0; waypointIndex < record.Waypoints.Count; waypointIndex++)
                {
                    LoreTrackWaypoint waypoint = record.Waypoints[waypointIndex];
                    WriteVector(writer, waypoint.Position);
                    WriteVector(writer, waypoint.Rotation);
                    writer.Write(waypoint.MinimumSpeed);
                    writer.Write(waypoint.MaximumSpeed);
                }
                writer.Write(record.Progress);
                writer.Write(record.TotalLength);
                writer.Write(record.Speed);
            }
        }

        private static bool ActionStateMatches(
            OriginalSaveActionPayload source,
            RuntimeEntityActionSaveState state,
            double gameTime)
        {
            switch (source)
            {
                case OriginalSaveWaitActionPayload wait when state is WaitActionSaveState live:
                    return SameRemaining(wait.AbsoluteDeadline, gameTime, live.RemainingSeconds);
                case OriginalSaveAnimActionPayload animation when state is AnimActionSaveState live:
                    return string.Equals(
                            animation.Common!.Argument.Text,
                            live.Clip,
                            StringComparison.Ordinal) &&
                        SameRemaining(animation.AbsoluteDeadline, gameTime, live.RemainingSeconds);
                case OriginalSaveCrawlActionPayload crawl when state is NativeCrawlActionSaveState live:
                    return crawl.RemainingTicks == live.RemainingTicks &&
                        crawl.RandomYawOrPhase.Bits == live.RandomYawOrPhaseBits;
                case OriginalSaveRotateActionPayload rotate when state is RotateActionSaveState live:
                    return SameBits(rotate.AbsoluteTargetYaw, live.AbsoluteTargetYawRadians) &&
                        SameBits(rotate.AngularStep, live.AngularStepRadiansPerTick) &&
                        rotate.DirectionSign == live.NativeDirectionSign &&
                        rotate.LastAppliedDirectionOrState == live.NativeLastAppliedDirectionOrState &&
                        rotate.LeftAnimationSlot == live.NativeLeftAnimationSlot &&
                        rotate.RightAnimationSlot == live.NativeRightAnimationSlot &&
                        rotate.TurnClipFrameCount == live.NativeTurnClipFrameCount;
                case OriginalSaveAttackActionPayload attack when state is NativeAttackActionSaveState live:
                    return AttackMatches(attack, live, gameTime);
                case OriginalSaveAttackBallisticActionPayload ballistic
                    when state is NativeAttackBallisticActionSaveState live:
                    return BallisticMatches(ballistic, live, gameTime);
                case OriginalSaveFlyActionPayload fly when state is FlyActionSaveState live:
                    return FlyMatches(fly, live);
                case OriginalSaveLoreActionPayload lore when state is NativeLoreActionSaveState live:
                    return LoreMatches(lore, live);
                case OriginalSaveWalkActionPayload walk when state is WalkActionSaveState live:
                    return WalkMatches(walk, live);
                case OriginalSaveMoveActionPayload move when state is MoveActionSaveState live:
                    return move.RemainingNativeTicks == live.RemainingNativeTicks &&
                        SameVector(move.Vectors, 3, live.Target);
                default:
                    return false;
            }
        }

        private static bool AttackMatches(
            OriginalSaveAttackActionPayload source,
            NativeAttackActionSaveState live,
            double gameTime)
        {
            return string.Equals(
                    source.ChainedActionVerb.Text,
                    live.ChainedActionVerb,
                    StringComparison.Ordinal) &&
                string.Equals(
                    source.ChainedActionArguments.Text,
                    live.ChainedActionArguments,
                    StringComparison.Ordinal) &&
                RawBitsEqual(source.LeadingRawValues, live.LeadingRawBits) &&
                SameRemaining(source.AbsoluteDeadline, gameTime, live.RemainingSeconds) &&
                BytesEqual(source.DeadlineFlags, live.DeadlineFlags) &&
                source.CurrentFrameOrTick == live.CurrentFrameOrTick &&
                BytesEqual(source.FrameFlags, live.FrameFlags) &&
                RawBitsEqual(
                    source.ImpactAndTrailingRawValues,
                    live.ImpactAndTrailingRawBits) &&
                source.TrailingFlag == live.TrailingFlag;
        }

        private static bool BallisticMatches(
            OriginalSaveAttackBallisticActionPayload source,
            NativeAttackBallisticActionSaveState live,
            double gameTime)
        {
            return source.TargetObjectId == live.TargetObjectId && source.Mode == live.Mode &&
                ValuesEqual(source.ScheduleValues, live.CopyScheduleValues()) &&
                RawBitsEqual(source.FirstRawValues, live.CopyFirstRawBits()) &&
                ValuesEqual(source.TransientHandles, live.CopyTransientHandles()) &&
                RawBitsEqual(source.SecondRawValues, live.CopySecondRawBits()) &&
                source.PrimaryFlag == live.PrimaryFlag &&
                RawBitsEqual(source.LastTarget, live.CopyLastTargetBits()) &&
                source.SecondaryFlag == live.SecondaryFlag &&
                source.TertiaryFlag == live.TertiaryFlag &&
                source.TargetVerticalOffset.Bits == live.TargetVerticalOffsetBits &&
                SameRemaining(source.AbsoluteDeadline, gameTime, live.RemainingSeconds);
        }

        private static bool FlyMatches(OriginalSaveFlyActionPayload source, FlyActionSaveState live)
        {
            return SameVector(source.MotionValues, 0, live.TargetWorld) &&
                SameVector(source.MotionValues, 3, live.VelocityOrStepWorld) &&
                SameVector(source.MotionValues, 6, live.PreviousPositionWorld) &&
                SameBits(source.MotionValues[9], live.NativeHeadingOrPhase) &&
                SameBits(source.MotionValues[10], live.NativeAngularVelocity) &&
                SameBits(source.MotionValues[11], live.NativeVerticalAccumulator) &&
                source.ArrivedOrCloseMode == live.ArrivedOrCloseMode &&
                source.TickCount == live.TickCount && source.SwimMode == live.SwimMode;
        }

        private static bool LoreMatches(
            OriginalSaveLoreActionPayload source,
            NativeLoreActionSaveState live)
        {
            if (source.Gravity.Bits != live.GravityBits ||
                source.Friction.Bits != live.FrictionBits)
                return false;
            NativeLoreTargetSaveState[] targets = live.CopyTargets();
            if (source.Targets.Count != targets.Length) return false;
            for (int index = 0; index < targets.Length; index++)
            {
                if (source.Targets[index].ObjectId != targets[index].ObjectId ||
                    source.Targets[index].OffsetOrDistance.Bits !=
                    targets[index].OffsetOrDistanceBits)
                    return false;
            }
            int[] waypointBits = live.CopyWaypointBits();
            if (source.Waypoints.Count * 8 != waypointBits.Length) return false;
            int offset = 0;
            for (int waypoint = 0; waypoint < source.Waypoints.Count; waypoint++)
            {
                for (int value = 0; value < 8; value++)
                {
                    if (source.Waypoints[waypoint].Values[value].Bits != waypointBits[offset++])
                        return false;
                }
            }
            return true;
        }

        private static bool WalkMatches(
            OriginalSaveWalkActionPayload source,
            WalkActionSaveState live)
        {
            int[] bankStates = live.CopyNativeAnimationBankStates();
            int[] slotBits = live.CopyNativeAnimationSlotBits();
            if (source.AnimationBanks.Count != bankStates.Length ||
                slotBits.Length != source.AnimationBanks.Count * 24 * 5)
                return false;
            int offset = 0;
            for (int bank = 0; bank < bankStates.Length; bank++)
            {
                if (source.AnimationBanks[bank].BankStateOrAnimationSet != bankStates[bank])
                    return false;
                for (int value = 0; value < 24 * 5; value++)
                {
                    if (source.AnimationBanks[bank].SlotValues[value].Bits != slotBits[offset++])
                        return false;
                }
            }

            return source.MoveNeededOrApproach == live.MoveNeededOrApproach &&
                source.Waving == live.Waving && source.DontStop == live.DontStop &&
                SameBits(source.TargetAndDistanceValues[0], live.StopAt) &&
                SameVector(source.TargetAndDistanceValues, 1, live.TargetNative) &&
                source.CanClimb == live.CanClimb && source.UseObjects == live.UseObjects &&
                source.RandomPath == live.RandomPath && source.RandomZ == live.RandomZ &&
                BytesEqual(source.MoveFlags, live.CopyMoveFlags()) &&
                source.ObjectBreakClassId == live.ObjectBreakClassId &&
                SameBits(source.ObjectBreakDistance, live.ObjectBreakDistance) &&
                SameBits(source.TargetDistance, live.TargetDistance) &&
                SameBits(source.ClimbRotationX, live.ClimbRotation.x) &&
                SameBits(source.ClimbRotationY, live.ClimbRotation.y) &&
                SameBits(source.ClimbRotationZ, live.ClimbRotation.z) &&
                source.DiagonalSqueezeFlag == live.DiagonalSqueezeFlag &&
                SameBits(source.RandomPathWallDistance, live.RandomPathWallDistance) &&
                source.WalkTimerCounter == live.WalkTimerCounter &&
                source.TickCounter == live.TickCounter && source.SpeedType == live.SpeedType &&
                string.Equals(
                    source.WalkTimerCallback.Text,
                    live.WalkTimerCallback,
                    StringComparison.Ordinal) &&
                string.Equals(
                    source.WalkCollisionCallback.Text,
                    live.WalkCollisionCallback,
                    StringComparison.Ordinal) &&
                string.Equals(
                    source.ObjectUseCallback.Text,
                    live.ObjectUseCallback,
                    StringComparison.Ordinal);
        }

        private static RuntimeEntityActionSaveState UnwrapActionState(
            RuntimeEntityActionSaveState state)
        {
            while (state is TimedImpactActionSaveState timed) state = timed.InnerAction;
            return state;
        }

        private static OriginalSaveActionType ActionTypeOf(RuntimeEntityActionSaveState state) =>
            state switch
            {
                WaitActionSaveState => OriginalSaveActionType.Wait,
                AnimActionSaveState => OriginalSaveActionType.Anim,
                NativeCrawlActionSaveState => OriginalSaveActionType.Crawl,
                RotateActionSaveState => OriginalSaveActionType.Rotate,
                NativeAttackActionSaveState => OriginalSaveActionType.Attack,
                NativeAttackBallisticActionSaveState => OriginalSaveActionType.AttackBallistic,
                FlyActionSaveState => OriginalSaveActionType.Fly,
                NativeLoreActionSaveState => OriginalSaveActionType.Lore,
                WalkActionSaveState => OriginalSaveActionType.Walk,
                MoveActionSaveState => OriginalSaveActionType.Move,
                _ => throw new NotSupportedException(
                    $"Runtime action state {state.GetType().Name} has no native type code."),
            };

        private static void WriteEventPayload(
            BinaryWriter writer,
            OriginalSaveTimerEventPayloadDocument payload,
            int expectedOwnerObjectId)
        {
            if (payload == null) throw new InvalidDataException("A native event payload is null.");
            byte[] name = payload.CopyFixedEventName();
            int terminator = Array.IndexOf(name, (byte)0);
            // Subjects 1..3 are usually -1 in the corpus but not always (e.g. the real global
            // timer event 'evt_task_recyclebuilding' carries live subject ids), and the payload
            // owner word does not always match the queue owner (a real quicksave carries
            // 'evt_hit_sound_notify' in another entity's queue) — emit both verbatim; only the
            // 32-byte name shape is a hard contract. `expectedOwnerObjectId` stays for callers
            // that want the common case documented, but it is not enforced.
            _ = expectedOwnerObjectId;
            if (name.Length != 32 || terminator < 0 || terminator > 31)
            {
                throw new InvalidDataException(
                    $"Native event '{payload.EventName}' violates its fixed name shape.");
            }

            writer.Write(name);
            writer.Write(payload.OwnerObjectId);
            writer.Write(payload.Subject1ObjectId);
            writer.Write(payload.Subject2ObjectId);
            writer.Write(payload.Subject3ObjectId);
            writer.Write(payload.Position1X.Bits);
            writer.Write(payload.Position1Y.Bits);
            writer.Write(payload.Position1Z.Bits);
            writer.Write(payload.Position2X.Bits);
            writer.Write(payload.Position2Y.Bits);
            writer.Write(payload.Position2Z.Bits);
            writer.Write(payload.Position3X.Bits);
            writer.Write(payload.Position3Y.Bits);
            writer.Write(payload.Position3Z.Bits);
            WriteNativeString(writer, payload.Text1);
            WriteNativeString(writer, payload.Text2);
            writer.Write(payload.Number1.Bits);
            writer.Write(payload.Number2.Bits);
            writer.Write(payload.Number3.Bits);
            writer.Write(payload.CreatedAt.Bits);
        }

        private static void WriteState350(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveCanonicalEntityState? live,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 3, "State350");
            writer.Write(values[0]);
            writer.Write(live != null
                ? BitConverter.SingleToInt32Bits(live.WorkStart)
                : patch?.WorkStart.HasValue == true
                    ? BitConverter.SingleToInt32Bits(patch.WorkStart.Value)
                    : values[1]);
            writer.Write(live != null
                ? BitConverter.SingleToInt32Bits(live.WorkLength)
                : patch?.WorkLength.HasValue == true
                    ? BitConverter.SingleToInt32Bits(patch.WorkLength.Value)
                    : values[2]);
        }

        private static void WriteCoreState(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveCanonicalEntityState? live,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 13, "CoreState");
            for (int index = 0; index < values.Count; index++)
            {
                float? replacement = index switch
                {
                    1 => live?.Velocity.x ?? patch?.Velocity?.X,
                    2 => live?.Velocity.y ?? patch?.Velocity?.Y,
                    3 => live?.Velocity.z ?? patch?.Velocity?.Z,
                    7 => live?.Rotation.x ?? patch?.Rotation?.X,
                    8 => live?.Rotation.y ?? patch?.Rotation?.Y,
                    9 => live?.Rotation.z ?? patch?.Rotation?.Z,
                    _ => null,
                };
                writer.Write(replacement.HasValue
                    ? BitConverter.SingleToInt32Bits(replacement.Value)
                    : values[index]);
            }
        }

        private static void WriteState220(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveCanonicalEntityState? live,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 4, "State220");
            writer.Write(values[0]);
            writer.Write(live?.PlaceSnapMode ?? patch?.PlaceSnapMode ?? values[1]);
            writer.Write(live?.WeaponClassId ?? values[2]);
            writer.Write(live?.ShieldClassId ?? values[3]);
        }

        private static void WriteState26C(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveCanonicalEntityState? live,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 10, "State26C");
            for (int index = 0; index < values.Count; index++)
            {
                int value = index switch
                {
                    0 when live != null => BitConverter.SingleToInt32Bits(live.FogOfWarLastX),
                    0 when patch?.FogOfWarLastX.HasValue == true =>
                        BitConverter.SingleToInt32Bits(patch.FogOfWarLastX.Value),
                    1 when live != null => BitConverter.SingleToInt32Bits(live.FogOfWarLastY),
                    1 when patch?.FogOfWarLastY.HasValue == true =>
                        BitConverter.SingleToInt32Bits(patch.FogOfWarLastY.Value),
                    3 when live != null => live.LinkedObjectId,
                    3 when patch?.LinkedObjectId.HasValue == true => patch.LinkedObjectId.Value,
                    4 when live != null => live.LinkedSlot,
                    4 when patch?.LinkedSlot.HasValue == true => patch.LinkedSlot.Value,
                    _ => values[index],
                };
                writer.Write(value);
            }
        }

        private static void WriteLateCoreState(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveCanonicalEntityState? live,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 6, "LateCoreState");
            for (int index = 0; index < values.Count; index++)
            {
                int value = index switch
                {
                    0 when live != null => live.ObjectFlags,
                    0 when patch?.ObjectFlags.HasValue == true => patch.ObjectFlags.Value,
                    2 when patch?.DirtyFlags.HasValue == true => patch.DirtyFlags.Value,
                    4 when live != null => live.DigOrderId,
                    4 when patch?.DigOrderId.HasValue == true => patch.DigOrderId.Value,
                    _ => values[index],
                };
                writer.Write(value);
            }
        }

        private static void WriteAttributePage(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            bool hasNativePage)
        {
            // Absent native pages (optional grid presence 0) are legal corpus state and are
            // reproduced as absence; a synthesized page is emitted only when the native had one
            // or the entity is new.
            if (!hasNativePage)
            {
                writer.Write(0);
                return;
            }
            ValidateCount(values, 512, "attribute page");
            writer.Write(1);
            for (int index = 0; index < values.Count; index++) writer.Write(values[index]);
        }

        private static void ValidateProduction(
            OriginalSaveCanonicalProductionState production,
            int objectId)
        {
            if (production.RequestBytes.Length != 7 ||
                production.RequestBytes[0] != production.RequestBytes[6] ||
                production.PendingPositionBits.Length != 3 ||
                production.FlowCompatibilityBytes.Length != 8 ||
                production.ProductionValues.Length != 4 ||
                production.EnergyValues.Length != 5 || production.GuestSeats.Length != 16)
            {
                throw new InvalidDataException(
                    $"Canonical CProd state for object {objectId} is incomplete.");
            }
        }

        private static void ValidateEventQueue(
            int readCursor,
            int writeCursor,
            IReadOnlyList<OriginalSaveTimerEventPayloadDocument?> slots,
            int objectId)
        {
            if ((uint)readCursor >= OriginalSaveObjectEventQueueDocument.Capacity ||
                (uint)writeCursor >= OriginalSaveObjectEventQueueDocument.Capacity ||
                slots.Count != OriginalSaveObjectEventQueueDocument.Capacity)
            {
                throw new InvalidDataException(
                    $"Native object {objectId} has an invalid event-ring structure.");
            }
            int pending = (writeCursor - readCursor + OriginalSaveObjectEventQueueDocument.Capacity) &
                (OriginalSaveObjectEventQueueDocument.Capacity - 1);
            for (int index = 0; index < pending; index++)
            {
                int slot = (readCursor + index) &
                    (OriginalSaveObjectEventQueueDocument.Capacity - 1);
                if (slots[slot] == null)
                {
                    throw new InvalidDataException(
                        $"Native object {objectId} has an empty pending event slot {slot}.");
                }
            }
        }

        private static void ValidateObjectCount(int count)
        {
            if (count < 0 || count > MaximumObjectCount)
                throw new InvalidDataException(
                    $"The native object registry contains {count} objects; the maximum is " +
                    $"{MaximumObjectCount}.");
        }

        private static void ValidateObjectId(int objectId, int previousObjectId)
        {
            if (objectId < 2 || objectId > MaximumObjectId || objectId <= previousObjectId)
                throw new InvalidDataException(
                    $"Native object declarations are not strictly ascending at ID {objectId}.");
        }

        private static void ValidateClassName(string className, int objectId)
        {
            if (string.IsNullOrEmpty(className) || className.IndexOf('\0') >= 0)
                throw new InvalidDataException(
                    $"Native object {objectId} has an invalid archived class name.");
        }

        private static double AbsoluteDeadline(double gameTime, double remainingSeconds)
        {
            if (double.IsNaN(gameTime) || double.IsInfinity(gameTime) ||
                double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds))
                throw new InvalidDataException("A runtime action deadline is non-finite.");
            return gameTime + Math.Max(0d, remainingSeconds);
        }

        private static bool SameRemaining(
            double absoluteDeadline,
            double gameTime,
            double remainingSeconds)
        {
            double retained = Math.Max(0d, absoluteDeadline - gameTime);
            return BitConverter.DoubleToInt64Bits(retained) ==
                    BitConverter.DoubleToInt64Bits(remainingSeconds) ||
                Math.Abs(retained - remainingSeconds) <= 1e-9d;
        }

        private static bool SameBits(OriginalSaveRaw32 source, float value) =>
            source.Bits == BitConverter.SingleToInt32Bits(value);

        private static bool SameVector(
            IReadOnlyList<OriginalSaveRaw32> source,
            int offset,
            Vector3 value) =>
            SameBits(source[offset], value.x) && SameBits(source[offset + 1], value.y) &&
            SameBits(source[offset + 2], value.z);

        private static bool ValuesEqual(IReadOnlyList<int> left, IReadOnlyList<int> right)
        {
            if (left.Count != right.Count) return false;
            for (int index = 0; index < left.Count; index++)
                if (left[index] != right[index]) return false;
            return true;
        }

        private static bool RawBitsEqual(
            IReadOnlyList<OriginalSaveRaw32> left,
            IReadOnlyList<int> right)
        {
            if (left.Count != right.Count) return false;
            for (int index = 0; index < left.Count; index++)
                if (left[index].Bits != right[index]) return false;
            return true;
        }

        private static bool BytesEqual(IReadOnlyList<byte> left, IReadOnlyList<byte> right)
        {
            if (left.Count != right.Count) return false;
            for (int index = 0; index < left.Count; index++)
                if (left[index] != right[index]) return false;
            return true;
        }

        private static void WriteActorLink(
            BinaryWriter writer,
            OriginalSaveObjectActorLinkDocument link) =>
            WriteActorLink(writer, link.Id, link.State, link.ReferenceObjectId);

        private static void WriteActorLink(
            BinaryWriter writer,
            int id,
            int state,
            int referenceObjectId)
        {
            writer.Write(id);
            writer.Write(state);
            writer.Write(referenceObjectId);
        }

        private static void WriteVector(BinaryWriter writer, OriginalSaveVector3 value)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static void WriteInt32Vector(BinaryWriter writer, IReadOnlyList<int> values)
        {
            if (values == null) throw new InvalidDataException("A native Int32 vector is null.");
            writer.Write(values.Count);
            for (int index = 0; index < values.Count; index++) writer.Write(values[index]);
        }

        private static void WriteStringVector(BinaryWriter writer, IReadOnlyList<string> values)
        {
            if (values == null) throw new InvalidDataException("A native string vector is null.");
            writer.Write(values.Count);
            for (int index = 0; index < values.Count; index++)
                WriteNativeString(writer, values[index]);
        }

        private static void WriteRaw32Values(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveRaw32> values,
            int expectedCount,
            string label)
        {
            ValidateCount(values, expectedCount, label);
            for (int index = 0; index < values.Count; index++) writer.Write(values[index].Bits);
        }

        private static void WriteValues(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            int expectedCount,
            string label)
        {
            ValidateCount(values, expectedCount, label);
            for (int index = 0; index < values.Count; index++) writer.Write(values[index]);
        }

        private static void WriteBytes(
            BinaryWriter writer,
            IReadOnlyList<byte> values,
            int expectedCount,
            string label)
        {
            ValidateCount(values, expectedCount, label);
            for (int index = 0; index < values.Count; index++) writer.Write(values[index]);
        }

        private static void ValidateCount<T>(
            IReadOnlyCollection<T> values,
            int expectedCount,
            string label)
        {
            if (values == null) throw new InvalidDataException($"Native {label} is null.");
            if (values.Count != expectedCount)
                throw new InvalidDataException(
                    $"Native {label} contains {values.Count} values; expected {expectedCount}.");
        }

        private static void WriteNativeString(BinaryWriter writer, string value)
        {
            if (value == null) throw new InvalidDataException("A native string is null.");
            if (value.IndexOf('\0') >= 0)
                throw new InvalidDataException("A native string contains an embedded NUL.");
            byte[] bytes = Windows1252.GetBytes(value);
            writer.Write(checked(bytes.Length + 1));
            writer.Write(bytes);
            writer.Write((byte)0);
        }

        private static void WriteNativeString(
            BinaryWriter writer,
            OriginalSaveNativeString value)
        {
            if (value == null) throw new InvalidDataException("A native string document is null.");
            byte[] bytes = value.CopyBytesIncludingTerminator();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private readonly struct RetainedStructureLayout
        {
            internal RetainedStructureLayout(
                int classTableOffset,
                int cleanupQueueOffset,
                int cleanupQueueEndOffset,
                int spatialIndexOffset)
            {
                ClassTableOffset = classTableOffset;
                CleanupQueueOffset = cleanupQueueOffset;
                CleanupQueueEndOffset = cleanupQueueEndOffset;
                SpatialIndexOffset = spatialIndexOffset;
            }

            internal int ClassTableOffset { get; }
            internal int CleanupQueueOffset { get; }
            internal int CleanupQueueEndOffset { get; }
            internal int SpatialIndexOffset { get; }
        }
    }

    public sealed class OriginalSaveRetainedStructureSections
    {
        internal OriginalSaveRetainedStructureSections(
            byte[] classTable,
            byte[] cleanupQueue,
            byte[] spatialIndex)
        {
            ClassTable = classTable ?? throw new ArgumentNullException(nameof(classTable));
            CleanupQueue = cleanupQueue ?? throw new ArgumentNullException(nameof(cleanupQueue));
            SpatialIndex = spatialIndex ?? throw new ArgumentNullException(nameof(spatialIndex));
        }

        public byte[] ClassTable { get; }
        public byte[] CleanupQueue { get; }
        public byte[] SpatialIndex { get; }
    }
}
