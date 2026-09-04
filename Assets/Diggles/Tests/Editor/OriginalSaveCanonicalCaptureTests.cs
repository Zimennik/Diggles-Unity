#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveCanonicalCaptureTests
    {
        private const string BdbRelativePath = "BlockDB2.BDB";
        private const string RichFixtureName = "quick_Quicksave_20201220_2057.sav";

        [Test]
        public void LiveServiceCaptures_RestoreAndRecaptureBitExactly()
        {
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = PrepareRichFixture();
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                var mapper = new OriginalSaveRuntimePatchMapper();
                OriginalSaveCanonicalCapture first = mapper.CaptureCanonical(snapshot, context);

                context.Timers.RestoreEntries(first.TimerRegistryEntries);
                context.Sim!.Scheduler.RestoreEntries(first.ScheduledTimers);
                context.TriggerService!.RestoreRecords(first.Triggers);
                context.Ai!.RestoreSaveState(first.Ai);
                context.Audio!.RestoreMusicMarkers(first.AdaptiveSoundMarkers);
                context.NewsTicker.Restore(first.NewsTicker);
                context.Terrain!.RestoreCaveRecords(first.CaveRecords);
                context.Energy.Restore(first.Energy);
                context.Particles!.Restore(first.Particles);
                context.LoreTracks.RestoreRecords(first.LoreTracks);
                context.ProdSlotOverrides.Restore(first.ProductionSlotOverrides);

                OriginalSaveCanonicalCapture second = mapper.CaptureCanonical(snapshot, context);
                AssertSectionEqual("timer registry", first.TimerRegistryEntries,
                    second.TimerRegistryEntries);
                AssertSectionEqual("timer scheduler", first.ScheduledTimers,
                    second.ScheduledTimers);
                AssertSectionEqual("global timers", first.GlobalTimers, second.GlobalTimers);
                AssertSectionEqual("triggers", first.Triggers, second.Triggers);
                AssertSectionEqual("AI owners", first.Ai, second.Ai);
                AssertSectionEqual("story", first.Story, second.Story);
                AssertSectionEqual("players", first.Players, second.Players);
                AssertSectionEqual("selection", first.SelectionControlGroups,
                    second.SelectionControlGroups);
                AssertSectionEqual("sparetime", first.Sparetime, second.Sparetime);
                AssertSectionEqual("horizon", first.Horizon, second.Horizon);
                AssertSectionEqual("adaptive sound", first.AdaptiveSoundMarkers,
                    second.AdaptiveSoundMarkers);
                AssertSectionEqual("news ticker", first.NewsTicker, second.NewsTicker);
                AssertSectionEqual("cave records", first.CaveRecords, second.CaveRecords);
                AssertSectionEqual("energy", first.Energy, second.Energy);
                AssertSectionEqual("particles", first.Particles, second.Particles);
                AssertSectionEqual("Lore tracks", first.LoreTracks, second.LoreTracks);
                AssertSectionEqual("production-slot overrides", first.ProductionSlotOverrides,
                    second.ProductionSlotOverrides);
                AssertSectionEqual("owner attributes", first.OwnerAttributes,
                    second.OwnerAttributes);
                AssertSectionEqual("entities", first.Entities, second.Entities);
                AssertSectionEqual("class lights", first.ClassLights, second.ClassLights);
                AssertSectionEqual("animations", first.AnimationControllers,
                    second.AnimationControllers);
                AssertSectionEqual("object icons", first.ObjectIcons, second.ObjectIcons);
                AssertSectionEqual("actions", first.Actions, second.Actions);
                AssertSectionEqual("elf", first.Elf, second.Elf);
                AssertSectionEqual("terrain", first.Terrain, second.Terrain);
                AssertSectionEqual("fog", first.FogOfWar, second.FogOfWar);
                AssertSectionEqual("fluid", first.Fluid, second.Fluid);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void RichFixture_StageThenCapture_MatchesParsedCanonicalSections()
        {
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = PrepareRichFixture();
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                OriginalSaveRuntimeSessionState staged = harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                OriginalSaveCanonicalCapture actual =
                    new OriginalSaveRuntimePatchMapper().CaptureCanonical(snapshot, context);

                Assert.That(BitConverter.DoubleToInt64Bits(actual.GameTime),
                    Is.EqualTo(BitConverter.DoubleToInt64Bits(snapshot.GameTime)));
                AssertOwnerAttributes(snapshot.OwnerAttributes, actual.OwnerAttributes);
                AssertEntities(snapshot.Entities, actual.Entities);
                AssertTimers(snapshot.Timers.Records, actual.GlobalTimers);
                AssertTriggers(snapshot.Triggers, actual.Triggers);
                AssertAiOwners(snapshot.Ai, actual.Ai);
                AssertStory(snapshot.Story, actual.Story);
                AssertPlayers(snapshot.Players, actual.Players);
                AssertSelection(snapshot.SelectionControlGroups, actual.SelectionControlGroups);
                AssertSparetime(snapshot.Sparetime, actual.Sparetime);
                AssertHorizon(snapshot.Horizon, actual.Horizon);
                AssertAdaptiveSound(snapshot.AdaptiveSoundMarkers,
                    actual.AdaptiveSoundMarkers);
                AssertSectionEqual("news ticker", snapshot.NewsTicker.CreateSaveState(),
                    actual.NewsTicker);
                AssertSectionEqual("cave records", snapshot.CaveRecords.CreateRuntimeRecords(
                    staged.Terrain, context.LocalPlayer), actual.CaveRecords);
                AssertSectionEqual("energy", snapshot.Energy.CreateSaveState(), actual.Energy);
                AssertSectionEqual("Lore tracks",
                    staged.PreparedActions.LoreTrackRecords.ToArray(), actual.LoreTracks);
                Assert.That(actual.ProductionSlotOverrides,
                    Is.EqualTo(snapshot.Players.ReservedFraming));
                Assert.That(actual.Players.ReservedFraming,
                    Is.EqualTo(snapshot.Players.ReservedFraming));
                AssertSectionEqual("terrain", snapshot.RuntimeWorld.CreateTerrainState(
                    staged.WorldResult.Voxels), actual.Terrain);
                AssertSectionEqual("fog", snapshot.RuntimeWorld.CreateFogState(), actual.FogOfWar);
                AssertSectionEqual("fluid", snapshot.RuntimeWorld.CreateFluidState(), actual.Fluid);
                AssertClassLights(snapshot.ClassLights.Records, actual.ClassLights);
                AssertDerivableSections(snapshot, actual);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void LiveMutation_OverridesSemanticStateAndRetainsNativeResidue()
        {
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = PrepareRichFixture();
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                // At a safe save boundary every ring is fully drained (read == write in every
                // legal archive), so a fixture with a live pending window cannot exist. The
                // mutation is synthesized instead: take an object with consumed-ring history,
                // append one live event, and pin (a) the consumed residue slot verbatim and
                // (b) the appended event's serialization.
                OriginalSaveRuntimeEntitySnapshot? saved = snapshot.Entities.FirstOrDefault(
                    entity =>
                    {
                        int lastWritten = (entity.EventQueueWriteCursor - 1 +
                            OriginalSaveObjectEventQueueDocument.Capacity) &
                            (OriginalSaveObjectEventQueueDocument.Capacity - 1);
                        return entity.EventQueueWriteCursor != 0 &&
                            entity.EventQueueSlots[lastWritten] != null;
                    });
                Assume.That(saved, Is.Not.Null, "The rich fixture has no ring history at all.");
                Assume.That(context.RegisteredEventNames, Is.Not.Empty);
                Assume.That(context.Stats.Registry.DefinedInOrder, Is.Not.Empty);

                DigglesTclEntity live = context.Entities[saved!.ObjectId];
                int oldRead = saved.EventQueueReadCursor;
                int oldWrite = saved.EventQueueWriteCursor;
                int consumedSlot = (oldWrite - 1 +
                    OriginalSaveObjectEventQueueDocument.Capacity) &
                    (OriginalSaveObjectEventQueueDocument.Capacity - 1);
                string appendedEvent = context.RegisteredEventNames.First();
                live.EnqueueEvent(new EntityEvent(appendedEvent), context.Sim!.GameTime);

                // The capture places attributes by the native 512-page slot table, not the port
                // registry order — pick an attribute that has a native slot mapping.
                string? attributeName = context.Stats.Registry.DefinedInOrder.FirstOrDefault(
                    name => !string.Equals(name, "atr_Hitpoints", StringComparison.Ordinal) &&
                        OriginalSaveNativeAttributeSlots.SlotByName.ContainsKey(name));
                Assume.That(attributeName, Is.Not.Null.And.Not.Empty);
                var entityAttributes = (IDictionary<string, double>)live.Stats.Raw;
                entityAttributes[attributeName!] = double.PositiveInfinity;
                Assert.That(context.OwnerAttribs.TryGetValue(
                    0,
                    out Dictionary<string, double>? ownerAttributes), Is.True);
                ownerAttributes![attributeName!] = double.NegativeInfinity;
                live.WalkResult = 5;
                context.Interp.Eval($"set_collision {live.Handle} 0");
                live.ProdAutoSchedule = !live.ProdAutoSchedule;

                OriginalSaveCanonicalCapture capture =
                    new OriginalSaveRuntimePatchMapper().CaptureCanonical(snapshot, context);
                OriginalSaveCanonicalEntityState captured = capture.Entities.Single(
                    entity => entity.ObjectId == live.Handle);
                Assert.That(
                    OriginalSaveNativeAttributeSlots.SlotByName.TryGetValue(
                        attributeName!,
                        out int attributeSlot),
                    Is.True,
                    "the mutated attribute must map to a native page slot");
                Assert.That(captured.AttributePageBits[attributeSlot],
                    Is.EqualTo(BitConverter.SingleToInt32Bits(float.PositiveInfinity)),
                    "entity attribute capture must not clamp");
                OriginalSaveCanonicalOwnerAttributeBlock owner = capture.OwnerAttributes.Single(
                    block => block.Owner == 0);
                Assert.That(owner.PrimaryValueBits[attributeSlot],
                    Is.EqualTo(BitConverter.SingleToInt32Bits(float.NegativeInfinity)),
                    "owner attribute capture must not clamp");
                OriginalSaveRuntimeOwnerAttributeBlockSnapshot ownerResidue =
                    snapshot.OwnerAttributes.Blocks.Single(block => block.Owner == 0);
                Assert.That(owner.SecondaryCompatibilityBits,
                    Is.EqualTo(ownerResidue.SecondaryValueBits),
                    "the second owner page is retained raw");

                Assert.That(captured.WalkResult, Is.EqualTo(5));
                Assert.That(captured.EventQueue.ReadCursor, Is.EqualTo(oldRead),
                    "no live dispatch ran — the read cursor must hold its archived value");
                Assert.That(captured.EventQueue.WriteCursor,
                    Is.EqualTo((oldWrite + 1) &
                        (OriginalSaveObjectEventQueueDocument.Capacity - 1)));
                AssertPayload(saved.EventQueueSlots[consumedSlot]!,
                    captured.EventQueue.Slots[consumedSlot]!,
                    $"object {saved.ObjectId} consumed event residue");
                Assert.That(captured.EventQueue.Slots[oldWrite]!.EventName,
                    Is.EqualTo(appendedEvent));
                Assert.That(captured.EventQueue.Slots[oldWrite]!.Subject1ObjectId, Is.EqualTo(-1));
                Assert.That(captured.EventQueue.Slots[oldWrite]!.Subject2ObjectId, Is.EqualTo(-1));
                Assert.That(captured.EventQueue.Slots[oldWrite]!.Subject3ObjectId, Is.EqualTo(-1));
                Assert.That((captured.ObjectFlags & 0x00080000) != 0,
                    Is.False);
                Assert.That(captured.PickTargetMode, Is.Zero);
                Assert.That((captured.ObjectFlags & 0x00800000) == 0,
                    Is.EqualTo(live.ProdAutoSchedule));
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void CaptureCanonical_WithoutRetainedSnapshot_UsesOnlyLiveState()
        {
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = PrepareRichFixture();
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                context.Interp.Eval("set_ground_begin 17");
                DigglesTclEntity mutated = context.Entities.Values.First();
                mutated.WalkResult = 5;
                foreach (string name in new[]
                {
                    "_boxed", "_collision", "_autolight", "_partner_partner", "this",
                    "outobj", "wt",
                })
                {
                    mutated.VarsScope.Vars[name] = "w6a-filter";
                }
                foreach (string name in new[]
                {
                    "_invulnerable", "_undeletable", "_objectcollision",
                })
                {
                    mutated.VarsScope.Vars[name] = "1";
                }
                mutated.VarsScope.Vars["w6a_user_value"] = "kept";
                Assert.That(context.Stats.Registry.TryGet("atr_Hitpoints", out var hitpoints),
                    Is.True);
                int[] commandableUnitIds = context.Entities.Values
                    .Where(entity => entity.Owner == context.LocalPlayer &&
                        entity.NativeObjectType is 1 or 11 &&
                        (!entity.VarsScope.Vars.TryGetValue("_selectable", out string selectable) ||
                         selectable is "1" or "true"))
                    .OrderBy(entity => entity.Handle)
                    .Select(entity => entity.Handle)
                    .ToArray();
                Assert.That(commandableUnitIds, Is.Not.Empty);
                // Native icon law: a full-hitpoint dwarf gets no type-0 icon slot (the original
                // creates one only when it is worth showing; an unconditional slot made
                // permanent HP bars). Split the crew: the first half wounded at exactly half
                // HP (icon expected), the rest at full (none).
                int[] woundedUnitIds = commandableUnitIds
                    .Take(Math.Max(1, commandableUnitIds.Length / 2))
                    .ToArray();
                foreach (int objectId in commandableUnitIds)
                {
                    bool wounded = Array.IndexOf(woundedUnitIds, objectId) >= 0;
                    context.Stats.Set(
                        context.Entities[objectId].Stats,
                        "atr_Hitpoints",
                        wounded ? hitpoints.Max / 2d : hitpoints.Max);
                }

                OriginalSaveCanonicalCapture capture =
                    new OriginalSaveRuntimePatchMapper().CaptureCanonical(null, context);

                Assert.That(capture.GroundBeginRow, Is.EqualTo(17));
                Assert.That(capture.Entities, Has.Length.EqualTo(context.Entities.Count));
                Assert.That(capture.Entities.Single(entity => entity.ObjectId == mutated.Handle)
                    .WalkResult, Is.EqualTo(5));
                Assert.That(capture.Entities.All(entity =>
                    !entity.IsTclVariableCompatibilityPassthrough), Is.True);
                var animationsByObjectId = capture.AnimationControllers
                    .Where(animation => !animation.IsRetainedOrphan)
                    .ToDictionary(animation => animation.ObjectId);
                foreach (OriginalSaveCanonicalEntityState entity in capture.Entities)
                {
                    DigglesTclEntity liveEntity = context.Entities[entity.ObjectId];
                    Assert.That(entity.OwnsTclState,
                        Is.EqualTo(ClassOwnsTclState(liveEntity.Class) ||
                            entity.TclVariables.Length != 0 || liveEntity.ActiveAction != null),
                        $"object {entity.ObjectId} fresh Tcl-state ownership (class shape or lazy)");
                    Assert.That(entity.HasActionRow,
                        Is.EqualTo(ClassOwnsTclState(liveEntity.Class) ||
                            liveEntity.ActiveAction != null),
                        $"object {entity.ObjectId} fresh action-table participation");
                    Assert.That(entity.TclVariables.Any(variable =>
                            IsExcludedFreshTclVariable(variable.Name)),
                        Is.False,
                        $"object {entity.ObjectId} leaked a fresh bookkeeping Tcl variable");

                    OriginalSaveCanonicalAnimationState animation =
                        animationsByObjectId[entity.ObjectId];
                    int expectedFrameValue = animation.FrameValues.Length == 0
                        ? -1
                        : animation.FrameValues[Mathf.Clamp(
                            animation.CurrentFrame,
                            0,
                            animation.FrameValues.Length - 1)];
                    Assert.That(entity.AnimationModelDatabaseSlot,
                        Is.EqualTo(animation.ModelDatabaseSlot));
                    Assert.That(entity.AnimationFrameValue, Is.EqualTo(expectedFrameValue));
                    Assert.That(entity.AnimationState, Is.EqualTo(animation.AnimationState));
                    Assert.That(entity.AnimationMode, Is.EqualTo(animation.Mode));
                }
                Assert.That(capture.Actions.Select(action => action.ObjectId),
                    Is.EqualTo(capture.Entities
                        .Where(entity => entity.HasActionRow)
                        .Select(entity => entity.ObjectId)),
                    "marker-12 rows must exactly match the action-participant set");
                OriginalSaveObjectIconRecord[] hitpointIcons = capture.ObjectIcons
                    .Where(icon => icon.IsActive && icon.IconType == 0)
                    .ToArray();
                Assert.That(hitpointIcons.Select(icon => icon.ObjectId),
                    Is.EqualTo(woundedUnitIds),
                    "only wounded dwarves may own a type-0 hitpoint icon slot");
                Assert.That(hitpointIcons.All(icon => icon.Value == 8), Is.True,
                    "half HP rounds to 15·0.5 = 8");
                Assert.That(hitpointIcons.All(icon => icon.OffsetY.Bits ==
                    BitConverter.SingleToInt32Bits(-0.3f)), Is.True);
                // Holder linkage: every emitted slot must appear in its owner's icon-holder
                // lanes; a full-HP dwarf keeps all lanes empty (-1). Orphan slots reproduce the
                // permanent-HP-bar bug in the original.
                foreach (OriginalSaveObjectIconRecord icon in hitpointIcons)
                {
                    OriginalSaveCanonicalEntityState owner = capture.Entities
                        .Single(entity => entity.ObjectId == icon.ObjectId);
                    Assert.That(owner.IconHolderSlots, Does.Contain(icon.Slot),
                        $"object {icon.ObjectId} must link icon slot {icon.Slot} in its holder");
                }
                var hitpointSlotSet = new HashSet<int>(hitpointIcons.Select(icon => icon.Slot));
                foreach (int fullId in commandableUnitIds.Except(woundedUnitIds))
                {
                    OriginalSaveCanonicalEntityState fullOwner = capture.Entities
                        .Single(entity => entity.ObjectId == fullId);
                    Assert.That(
                        fullOwner.IconHolderSlots.Any(slot => hitpointSlotSet.Contains(slot)),
                        Is.False,
                        $"full-HP object {fullId} must not hold a hitpoint icon slot");
                }
                Assert.That(capture.ObjectIcons
                    .Where(icon => icon.IsActive && icon.IconType == 5)
                    .All(icon => icon.OffsetY.Bits == BitConverter.SingleToInt32Bits(-0.8f)),
                    Is.True);
                int enabledBits = BitConverter.SingleToInt32Bits(1f);
                foreach (OriginalSaveCanonicalOwnerAttributeBlock block in capture.OwnerAttributes)
                {
                    Assert.That(block.PrimaryValueBits.Skip(6).Take(5),
                        Is.All.EqualTo(enabledBits),
                        $"owner {block.Owner} primary dig-enable defaults");
                    Assert.That(block.SecondaryCompatibilityBits.Skip(6).Take(5),
                        Is.All.EqualTo(enabledBits),
                        $"owner {block.Owner} secondary dig-enable defaults");
                    Assert.That(block.SecondaryCompatibilityBits[11], Is.EqualTo(enabledBits));
                    Assert.That(block.SecondaryCompatibilityBits[26], Is.EqualTo(enabledBits));
                    Assert.That(block.SecondaryCompatibilityBits[27], Is.EqualTo(1063675494));
                    Assert.That(block.SecondaryCompatibilityBits[29], Is.EqualTo(1061997772));
                }
                Assume.That(context.Stats.Registry.DefinedInOrder, Is.Not.Empty);
                // Pages are laid out by native slot (OriginalSaveNativeAttributeSlots), not by
                // port registry order — verify a mapped attribute lands at its native slot and
                // that slots outside the native table stay zero on a fresh capture.
                string firstAttribute = context.Stats.Registry.DefinedInOrder[0];
                OriginalSaveCanonicalEntityState capturedMutated = capture.Entities.Single(
                    entity => entity.ObjectId == mutated.Handle);
                string[] capturedVariableNames = capturedMutated.TclVariables
                    .Select(variable => variable.Name)
                    .ToArray();
                Assert.That(capturedVariableNames,
                    Does.Not.Contain("_boxed").And.Not.Contain("_collision")
                        .And.Not.Contain("_autolight")
                        .And.Not.Contain("_partner_partner").And.Not.Contain("this")
                        .And.Not.Contain("outobj").And.Not.Contain("wt"));
                Assert.That(capturedVariableNames,
                    Does.Contain("_invulnerable")
                        .And.Contain("_undeletable").And.Contain("_objectcollision")
                        .And.Contain("w6a_user_value"));
                Assume.That(
                    OriginalSaveNativeAttributeSlots.SlotByName.TryGetValue(
                        firstAttribute, out int firstNativeSlot),
                    Is.True);
                Assert.That(capturedMutated.AttributePageBits[firstNativeSlot], Is.EqualTo(
                    BitConverter.SingleToInt32Bits((float)mutated.Stats.Get(
                        firstAttribute,
                        context.Stats.Registry))),
                    "new-campaign attributes must land at their NATIVE page slots");
                var nativeSlotSet = new HashSet<int>(
                    OriginalSaveNativeAttributeSlots.SlotByName.Values);
                for (int slot = 0; slot < capturedMutated.AttributePageBits.Length; slot++)
                {
                    if (nativeSlotSet.Contains(slot)) continue;
                    Assert.That(capturedMutated.AttributePageBits[slot], Is.Zero,
                        $"new-campaign slot {slot} is outside the native table and must stay zero");
                }
                Assert.That(capture.ClassLights.All(light => !light.IsRetainedObjectBound), Is.True);
                Assert.That(capture.AnimationControllers.All(animation =>
                    !animation.IsRetainedOrphan), Is.True);
                foreach (OriginalSaveCanonicalEntityState entity in capture.Entities)
                {
                    DigglesTclEntity liveEntity = context.Entities[entity.ObjectId];
                    Assert.That(entity.EventQueue.ReadCursor,
                        Is.EqualTo(liveEntity.NativeEventRingReadCursor),
                        $"object {entity.ObjectId} live native event-ring read cursor");
                    Assert.That(entity.EventQueue.WriteCursor,
                        Is.EqualTo(liveEntity.NativeEventRingWriteCursor),
                        $"object {entity.ObjectId} live native event-ring write cursor");
                    for (int slot = 0; slot < entity.EventQueue.Slots.Length; slot++)
                    {
                        OriginalSaveTimerEventPayloadDocument? expected =
                            liveEntity.NativeEventRingSlots[slot];
                        OriginalSaveTimerEventPayloadDocument? actual =
                            entity.EventQueue.Slots[slot];
                        if (expected == null)
                        {
                            Assert.That(actual, Is.Null,
                                $"object {entity.ObjectId} live native event-ring slot {slot}");
                            continue;
                        }
                        Assert.That(actual, Is.Not.Null,
                            $"object {entity.ObjectId} live native event-ring slot {slot}");
                        AssertPayload(expected, actual!,
                            $"object {entity.ObjectId} live native event-ring slot {slot}");
                    }
                }
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void FreshFeuerstelleCapture_SynthesizesNativeSlotsAndGlobalCounters()
        {
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = PrepareRichFixture();
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                DigglesTclEntity fireplace = context.Entities.Values.First(entity =>
                    string.Equals(entity.Class.Name, "Feuerstelle", StringComparison.Ordinal));
                fireplace.NativeProductionSlots = null;
                fireplace.ProdSlots.Clear();
                fireplace.NativeProductionCurrentProductStamp = -1;
                fireplace.ProdProgressCur = 0;
                fireplace.ProdProgressMax = 0;
                fireplace.NativeProductionInventionCandidateSlot = -1;

                foreach (Dictionary<string, double> values in context.OwnerAttribs.Values)
                {
                    values.Remove("Feuerstelle");
                    values.Remove("Zelt");
                }
                context.OwnerAttribs[0]["Feuerstelle"] = 2d;
                context.OwnerAttribs[0]["Zelt"] = 1d;

                OriginalSaveCanonicalCapture capture =
                    new OriginalSaveRuntimePatchMapper().CaptureCanonical(null, context);
                OriginalSaveCanonicalProductionState production = capture.Entities.Single(
                    entity => entity.ObjectId == fireplace.Handle).Production!;
                (string Item, int OwnerSlot, int BlueprintSlot)[] expected =
                {
                    ("Grillpilz", -1, 133),
                    ("Grillhamster", -1, 135),
                    ("Zelt", 392, 137),
                    ("Hauklotz", 145, 139),
                    ("Steinmetz", 317, 141),
                    ("Feuerstelle", 132, 143),
                };

                Assert.That(production, Is.Not.Null);
                Assert.That(production.Slots, Has.Length.EqualTo(expected.Length));
                for (int index = 0; index < expected.Length; index++)
                {
                    OriginalSaveCanonicalProductionSlotState slot = production.Slots[index];
                    Assert.That(slot.ClassName, Is.EqualTo(expected[index].Item));
                    Assert.That(slot.ResourceName, Is.EqualTo(expected[index].Item));
                    Assert.That(slot.RegistryValue1, Is.EqualTo(expected[index].OwnerSlot));
                    Assert.That(slot.RegistryValue2, Is.EqualTo(expected[index].BlueprintSlot));
                    Assert.That(slot.OrderCount, Is.Zero);
                    Assert.That(slot.DemandBits, Is.Zero);
                    Assert.That(slot.Invented, Is.Zero);
                    Assert.That(slot.Buildable, Is.Zero);
                }
                Assert.That(production.ProductionValues, Is.EqualTo(new[] { -1, 0, 0, -1 }));

                OriginalSaveCanonicalOwnerAttributeBlock global = capture.OwnerAttributes.Single(
                    block => block.Owner == -1);
                Assert.That(global.PrimaryValueBits[132],
                    Is.EqualTo(BitConverter.SingleToInt32Bits(2f)));
                Assert.That(global.PrimaryValueBits[392],
                    Is.EqualTo(BitConverter.SingleToInt32Bits(1f)));
                foreach (OriginalSaveCanonicalOwnerAttributeBlock owner in
                    capture.OwnerAttributes.Where(block => block.Owner >= 0))
                {
                    Assert.That(owner.PrimaryValueBits[132], Is.Zero,
                        $"owner {owner.Owner} Feuerstelle counter must stay global");
                    Assert.That(owner.PrimaryValueBits[392], Is.Zero,
                        $"owner {owner.Owner} Zelt counter must stay global");
                }
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void FreshCapture_DerivesCoreTextureAndInitialAnimationState()
        {
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = PrepareRichFixture();
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                DigglesTclEntity? live = null;
                DigglesAnimator? animator = null;
                DigglesAnimClip? clip = null;
                int animationState = -1;
                int modelSlot = -1;
                foreach (DigglesTclEntity entity in context.Entities.Values)
                {
                    DigglesAnimator? candidateAnimator = entity.GameObject?
                        .GetComponentInChildren<DigglesAnimator>(true);
                    DigglesAnimClip? candidateClip = candidateAnimator?.CurrentClip;
                    if (candidateAnimator == null || candidateClip?.SourceModel == null ||
                        string.IsNullOrEmpty(candidateClip.SourceObjName) ||
                        candidateAnimator.GetSubmeshBaseMaterialName(4) == null)
                    {
                        continue;
                    }
                    int candidateState = NativeAnimationState(candidateClip);
                    int candidateSlot = NativeModelSlot(candidateClip.SourceModel.ModelName);
                    if (candidateState < 0 || candidateSlot < 0) continue;
                    live = entity;
                    animator = candidateAnimator;
                    clip = candidateClip;
                    animationState = candidateState;
                    modelSlot = candidateSlot;
                    break;
                }
                Assume.That(live, Is.Not.Null,
                    "The rich fixture has no five-part entity with a native-addressable clip.");
                Assert.That(animator, Is.Not.Null);
                Assert.That(clip, Is.Not.Null);

                int probeId = context.Entities.Keys.Max() + 1;
                var probe = new DigglesTclEntity
                {
                    Class = live!.Class,
                    NativeObjectType = live.NativeObjectType,
                    Position = Vector3.zero,
                    Velocity = new Vector3(4f, -5f, 6f),
                    Rotation = new Vector3(1.25f, -1.5f, 1.75f),
                    Owner = live.Owner,
                    ContainedIn = -1,
                    LinkedSlot = -1,
                    DigSetId = -1,
                };
                context.RegisterEntityAtHandle(probe, probeId);
                InvokeEntityMethod(probe, "InitializeNativeCoreState");

                var mapper = new OriginalSaveRuntimePatchMapper();
                OriginalSaveCanonicalEntityState idle = mapper.CaptureCanonical(null, context)
                    .Entities.Single(entity => entity.ObjectId == probe.Handle);
                Assert.That(idle.CoreStateBits, Is.EqualTo(new[]
                {
                    0,
                    BitConverter.SingleToInt32Bits(4f),
                    BitConverter.SingleToInt32Bits(-5f),
                    BitConverter.SingleToInt32Bits(6f),
                    0,
                    0,
                    0,
                    BitConverter.SingleToInt32Bits(1.25f),
                    BitConverter.SingleToInt32Bits(-1.5f),
                    BitConverter.SingleToInt32Bits(1.75f),
                    BitConverter.SingleToInt32Bits(1.25f),
                    BitConverter.SingleToInt32Bits(-1.5f),
                    BitConverter.SingleToInt32Bits(1.75f),
                }));

                InvokeEntityMethod(probe, "CaptureNativeCoreState");
                OriginalSaveCanonicalEntityState stationary = mapper.CaptureCanonical(null, context)
                    .Entities.Single(entity => entity.ObjectId == probe.Handle);
                Assert.That(stationary.CoreStateBits.Skip(4).Take(3),
                    Is.EqualTo(new[] { int.MinValue, int.MinValue, int.MinValue }),
                    "a stationary native tick produces negative-zero deltas");

                probe.Position = new Vector3(
                    0.0379180908203125f,
                    0.0000019073486328125f,
                    -0.03257465362548828f);
                probe.Rotation = new Vector3(2.25f, -2.5f, 2.75f);
                InvokeEntityMethod(probe, "CaptureNativeCoreState");
                probe.Rotation = new Vector3(0.25f, -0.5f, 0.75f);

                DigglesTextureAnimator textureAnimator =
                    animator!.GetComponent<DigglesTextureAnimator>() ??
                    animator.gameObject.AddComponent<DigglesTextureAnimator>();
                textureAnimator.ClearChannels();
                int[] eyeFrames =
                {
                    0, 0, 0, 9, 0, 0, 0, 0, 0, 0, 0,
                    9, 0, 0, 0, 0, 0, 0, 0, 9, 0,
                };
                context.Interp.Eval($"set_textureanimation {live.Handle} 0 {{1}} 0 2");
                context.Interp.Eval(
                    $"set_textureanimation {live.Handle} 4 {{{string.Join(" ", eyeFrames)}}} 19 1");
                RestoreEmptyNativeTextureChannel(textureAnimator, 6, 37);

                context.Interp.Eval(
                    $"set_anim {probe.Handle} __w7a_missing_animation__ 8 2");
                OriginalSaveCanonicalEntityState unresolved = mapper
                    .CaptureCanonical(null, context).Entities
                    .Single(entity => entity.ObjectId == probe.Handle);
                Assert.That(new[]
                {
                    unresolved.InitialAnimationModelDatabaseSlot,
                    unresolved.InitialAnimationState,
                    unresolved.InitialAnimationStartFrame,
                    unresolved.InitialAnimationMode,
                }, Is.EqualTo(new[] { 0, 0, 0, 0 }),
                    "an unresolved native set_anim must not seed +0x3BC");
                context.Interp.Eval($"set_anim {probe.Handle} {clip!.QualifiedName} 4 1");
                context.Interp.Eval($"set_anim {probe.Handle} {clip.QualifiedName} 7 2");

                OriginalSaveCanonicalCapture capture = mapper.CaptureCanonical(null, context);
                OriginalSaveCanonicalEntityState capturedProbe = capture.Entities.Single(
                    entity => entity.ObjectId == probe.Handle);
                OriginalSaveCanonicalEntityState capturedTexture = capture.Entities.Single(
                    entity => entity.ObjectId == live.Handle);
                Assert.That(capturedProbe.CoreStateBits, Is.EqualTo(new[]
                {
                    1028440359,
                    BitConverter.SingleToInt32Bits(4f),
                    BitConverter.SingleToInt32Bits(-5f),
                    BitConverter.SingleToInt32Bits(6f),
                    1025200128,
                    905969664,
                    -1123717888,
                    BitConverter.SingleToInt32Bits(0.25f),
                    BitConverter.SingleToInt32Bits(-0.5f),
                    BitConverter.SingleToInt32Bits(0.75f),
                    BitConverter.SingleToInt32Bits(2.25f),
                    BitConverter.SingleToInt32Bits(-2.5f),
                    BitConverter.SingleToInt32Bits(2.75f),
                }), "N06 uses requested velocity, measured delta, and current/previous rotation");
                Assert.That(capturedTexture.CurrentPartFrames,
                    Is.EqualTo(new[] { 1, 0, 0, 0, 9, 0, 0 }));
                Assert.That(capturedTexture.TextureAnimations, Has.Length.EqualTo(5));
                Assert.That(capturedTexture.TextureAnimations[0].FrameValues,
                    Is.EqualTo(new[] { 1 }));
                Assert.That(capturedTexture.TextureAnimations[0].Cursor, Is.Zero);
                Assert.That(capturedTexture.TextureAnimations[0].PlaybackMode, Is.EqualTo(2));
                for (int submesh = 1; submesh < 4; submesh++)
                {
                    Assert.That(capturedTexture.TextureAnimations[submesh].FrameValues, Is.Empty);
                    Assert.That(capturedTexture.TextureAnimations[submesh].Cursor, Is.Zero);
                    Assert.That(capturedTexture.TextureAnimations[submesh].PlaybackMode, Is.Zero);
                }
                Assert.That(capturedTexture.TextureAnimations[4].FrameValues,
                    Is.EqualTo(eyeFrames));
                Assert.That(capturedTexture.TextureAnimations[4].Cursor, Is.EqualTo(19));
                Assert.That(capturedTexture.TextureAnimations[4].PlaybackMode, Is.EqualTo(1));
                Assert.That(capturedProbe.InitialAnimationModelDatabaseSlot, Is.EqualTo(modelSlot));
                Assert.That(capturedProbe.InitialAnimationState, Is.EqualTo(animationState));
                Assert.That(capturedProbe.InitialAnimationStartFrame, Is.EqualTo(4));
                Assert.That(capturedProbe.InitialAnimationMode, Is.EqualTo(1),
                    "N36 remains the first successful set_anim tuple");
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void FreshCapture_DerivesStateMiscBodyProducers()
        {
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = PrepareRichFixture();
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                Assert.That(context.Sim, Is.Not.Null);
                Assert.That(context.FogOfWar, Is.Not.Null);
                Assert.That(context.Particles, Is.Not.Null);

                context.Interp.Eval("def_class W7bGnome none gnome 0 {} {}");
                context.Interp.Eval("def_class W7bBarrel none material 0 {} {}");
                context.Interp.Eval("def_class w7bbaum none material 0 {} {}");
                int gnomeId = int.Parse(context.Interp.Eval("new W7bGnome").Trim());
                int barrelId = int.Parse(context.Interp.Eval("new W7bBarrel").Trim());
                int itemId = int.Parse(context.Interp.Eval("new W7bBarrel").Trim());
                int treeId = int.Parse(context.Interp.Eval("new w7bbaum").Trim());
                DigglesTclEntity gnome = context.Entities[gnomeId];
                DigglesTclEntity barrel = context.Entities[barrelId];
                DigglesTclEntity tree = context.Entities[treeId];

                float expectedCreationHours = (float)(context.Sim!.GameTime /
                    ProdDispatcher.WorktimeHourSeconds);
                context.Interp.Eval($"tasklist_add {gnomeId} {{play_anim sleeploop}}");
                context.Interp.Eval($"tasklist_add {gnomeId} {{play_anim sleepstop}}");
                context.Interp.Eval($"set_objgender {gnomeId} male");
                context.Interp.Eval($"set_collision {gnomeId} 1");
                context.Interp.Eval($"set_collision {barrelId} 1");
                context.Interp.Eval($"set_collision {treeId} 1");
                Assert.That(context.Interp.Eval($"inv_add {barrelId} {itemId}").Trim(),
                    Is.EqualTo("0"));
                context.Interp.Eval(
                    $"change_particlesource {barrelId} 0 13 {{0 -0.4 0.1}} " +
                    "{0 -0.07 0.01} 300 50 0");
                context.Interp.Eval(
                    $"action {gnomeId} walk \"-target {{1 1 1}}\"");

                double phase = context.Sim.GameTime / ProdDispatcher.WorktimeHourSeconds %
                    ProdDispatcher.DayHours;
                gnome.WorkStart = (float)((phase + ProdDispatcher.DayHours - 0.5) %
                    ProdDispatcher.DayHours);
                gnome.WorkLen = 1f;
                gnome.FowLastZ = 3.25f;
                gnome.FowCache = new[] { 255, 254, 253, 252, 251, 250, 249, 248 };
                gnome.FowCacheVersion = context.FogOfWar!.CacheVersion;
                tree.FowCache = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                tree.FowCacheVersion = context.FogOfWar.CacheVersion - 1;

                OriginalSaveCanonicalCapture capture =
                    new OriginalSaveRuntimePatchMapper().CaptureCanonical(null, context);
                OriginalSaveCanonicalEntityState capturedGnome = capture.Entities.Single(
                    entity => entity.ObjectId == gnomeId);
                OriginalSaveCanonicalEntityState capturedBarrel = capture.Entities.Single(
                    entity => entity.ObjectId == barrelId);
                OriginalSaveCanonicalEntityState capturedTree = capture.Entities.Single(
                    entity => entity.ObjectId == treeId);

                Assert.That(BitConverter.SingleToInt32Bits(capturedGnome.CreationTimeHours),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(expectedCreationHours)), "N05");
                Assert.That(capturedGnome.TaskList,
                    Is.EqualTo(new[] { "play_anim sleeploop", "play_anim sleepstop" }), "N08");
                Assert.That(capturedGnome.FogBrightnessCache.Take(8),
                    Is.EqualTo(gnome.FowCache), "N09 warm lanes");
                Assert.That(capturedGnome.FogBrightnessCache.Skip(8),
                    Is.All.EqualTo(-1), "N09 unmodeled native lanes stay cold");
                Assert.That(capturedTree.FogBrightnessCache,
                    Is.All.EqualTo(-1), "N09 stale cache is cold");
                Assert.That(BitConverter.SingleToInt32Bits(capturedGnome.FogOfWarLastZ),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(3.25f)), "N22 word 2");
                Assert.That(capturedGnome.WalkingActionActive, Is.EqualTo(1), "N26");
                Assert.That(capturedGnome.WorkTimeState, Is.EqualTo(1), "N27/N28");
                Assert.That(capturedGnome.NativeMutationFlags, Is.EqualTo(14), "N39 founder law");
                Assert.That(capturedBarrel.NativeMutationFlags, Is.EqualTo(11),
                    "N39 inventory-bearing barrel law");
                Assert.That(capturedTree.NativeMutationFlags, Is.EqualTo(10),
                    "N39 position-plus-animation baseline");

                ParticleSystemState.SourceEntry source = capture.Particles.Sources.Single(entry =>
                    entry.ObjectId == barrelId && entry.SourceIndex == 0);
                Assert.That(capturedBarrel.ParticleStateWords, Has.Length.EqualTo(160), "N24");
                Assert.That(capturedBarrel.ParticleStateWords[0],
                    Is.EqualTo(source.State.NativeParticleHandle));
                Assert.That(capturedBarrel.ParticleStateWords.Skip(1).Take(31),
                    Is.All.EqualTo(-1));
                Assert.That(capturedBarrel.ParticleStateWords[32],
                    Is.EqualTo(BitConverter.SingleToInt32Bits(0f)));
                Assert.That(capturedBarrel.ParticleStateWords[33],
                    Is.EqualTo(BitConverter.SingleToInt32Bits(-0.4f)));
                Assert.That(capturedBarrel.ParticleStateWords[34],
                    Is.EqualTo(BitConverter.SingleToInt32Bits(0.1f)));
                Assert.That(capturedBarrel.ParticleStateWords.Skip(128),
                    Is.All.EqualTo(-1));

                Assert.That(capturedGnome.PickTargetMode, Is.EqualTo(2));
                Assert.That(capturedBarrel.PickTargetMode, Is.EqualTo(1));
                Assert.That(capturedTree.PickTargetMode, Is.Zero,
                    "the native literal 'baum' exclusion keeps collision pick mode at zero");
                Assert.That(capturedGnome.ObjectFlags & 0x00080000, Is.Not.Zero);
                Assert.That(capturedBarrel.ObjectFlags & 0x00080000, Is.Not.Zero);
                Assert.That(capturedTree.ObjectFlags & 0x00080000, Is.Not.Zero,
                    "baum excludes pick mode, not the collision flag");
            }
            finally
            {
                candidate.Dispose();
            }
        }

        private static OriginalSaveRuntimeSnapshot PrepareRichFixture()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                RichFixtureName);
            Assume.That(File.Exists(path),
                $"Original save fixture was not found at '{path}'.");
            OriginalSaveArchiveDocument document = new OriginalSaveArchiveCodec().Read(path);
            return new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
        }

        private static int NativeAnimationState(DigglesAnimClip clip)
        {
            if (clip.SourceModel == null) return -1;
            var owner = clip.SourceModel.Objects.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, clip.SourceObjName, StringComparison.Ordinal));
            if (owner == null) return -1;
            int match = -1;
            foreach (uint index in owner.Indices)
            {
                if (index >= (uint)clip.SourceModel.Anims.Count ||
                    !string.Equals(
                        clip.SourceModel.Anims[(int)index].Name1,
                        clip.Name,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (match >= 0) return -1;
                match = (int)index;
            }
            return match;
        }

        private static int NativeModelSlot(string modelName)
        {
            if (string.Equals(modelName, "mann", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(modelName, "frau", StringComparison.OrdinalIgnoreCase)) return 1;
            return -1;
        }

        private static void InvokeEntityMethod(DigglesTclEntity entity, string methodName)
        {
            MethodInfo method = typeof(DigglesTclEntity).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new AssertionException($"DigglesTclEntity.{methodName} was not found.");
            method.Invoke(entity, null);
        }

        private static void RestoreEmptyNativeTextureChannel(
            DigglesTextureAnimator animator,
            int submesh,
            int currentFrameValue)
        {
            MethodInfo method = typeof(DigglesTextureAnimator).GetMethod(
                "RestoreNativeChannel",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new AssertionException(
                    "DigglesTextureAnimator.RestoreNativeChannel was not found.");
            method.Invoke(animator, new object?[]
            {
                submesh,
                Array.Empty<Texture2D?>(),
                Array.Empty<int>(),
                0,
                0,
                currentFrameValue,
            });
        }

        private static void AssertOwnerAttributes(
            OriginalSaveRuntimeOwnerAttributeSnapshot expected,
            IReadOnlyList<OriginalSaveCanonicalOwnerAttributeBlock> actual)
        {
            // Direct Count comparison: the runtime object is an array, whose ICollection.Count
            // is explicit — NUnit's reflective Has.Count cannot see it.
            Assert.That(actual.Count, Is.EqualTo(expected.Blocks.Count));
            for (int index = 0; index < actual.Count; index++)
            {
                Assert.That(actual[index].Owner, Is.EqualTo(expected.Blocks[index].Owner));
                Assert.That(actual[index].PrimaryValueBits,
                    Is.EqualTo(expected.Blocks[index].PrimaryValueBits),
                    $"owner attribute primary page {actual[index].Owner}");
                Assert.That(actual[index].SecondaryCompatibilityBits,
                    Is.EqualTo(expected.Blocks[index].SecondaryValueBits),
                    $"owner attribute secondary page {actual[index].Owner}");
            }
        }

        private static void AssertEntities(
            IReadOnlyList<OriginalSaveRuntimeEntitySnapshot> expected,
            IReadOnlyList<OriginalSaveCanonicalEntityState> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), "entity count");
            for (int index = 0; index < actual.Count; index++)
            {
                OriginalSaveRuntimeEntitySnapshot saved = expected[index];
                OriginalSaveCanonicalEntityState live = actual[index];
                string label = $"object {saved.ObjectId}";
                Assert.That(live.ObjectId, Is.EqualTo(saved.ObjectId), label);
                Assert.That(live.ClassName, Is.EqualTo(saved.ClassName), label);
                Assert.That(live.ObjectType, Is.EqualTo(saved.ObjectType), " object type");
                Assert.That(live.InstanceName, Is.EqualTo(saved.InstanceName), label);
                AssertRawVector(saved.Position, live.Position, " position");
                AssertRawVector(saved.Velocity, live.Velocity, " velocity");
                AssertRawVector(saved.Rotation, live.Rotation, " rotation");
                Assert.That(live.CoreStateBits, Is.EqualTo(new[]
                {
                    BitConverter.SingleToInt32Bits(saved.MotionSpeed),
                    BitConverter.SingleToInt32Bits(saved.Velocity.X),
                    BitConverter.SingleToInt32Bits(saved.Velocity.Y),
                    BitConverter.SingleToInt32Bits(saved.Velocity.Z),
                    BitConverter.SingleToInt32Bits(saved.MotionDelta.X),
                    BitConverter.SingleToInt32Bits(saved.MotionDelta.Y),
                    BitConverter.SingleToInt32Bits(saved.MotionDelta.Z),
                    BitConverter.SingleToInt32Bits(saved.Rotation.X),
                    BitConverter.SingleToInt32Bits(saved.Rotation.Y),
                    BitConverter.SingleToInt32Bits(saved.Rotation.Z),
                    BitConverter.SingleToInt32Bits(saved.PreviousRotation.X),
                    BitConverter.SingleToInt32Bits(saved.PreviousRotation.Y),
                    BitConverter.SingleToInt32Bits(saved.PreviousRotation.Z),
                }), label + " N06 core state");
                Assert.That(live.InitialAnimationModelDatabaseSlot,
                    Is.EqualTo(saved.InitialAnimationModelDatabaseSlot), label + " N36 model");
                Assert.That(live.InitialAnimationState,
                    Is.EqualTo(saved.InitialAnimationState), label + " N36 animation");
                Assert.That(live.InitialAnimationStartFrame,
                    Is.EqualTo(saved.InitialAnimationStartFrame), label + " N36 frame");
                Assert.That(live.InitialAnimationMode,
                    Is.EqualTo(saved.InitialAnimationMode), label + " N36 mode");
                Assert.That(live.CurrentPartFrames, Is.EqualTo(saved.CurrentPartFrames),
                    label + " N16 current texture frames");
                Assert.That(live.TextureAnimations,
                    Has.Length.EqualTo(saved.TextureAnimations.Count),
                    label + " texture animation count");
                for (int channel = 0; channel < live.TextureAnimations.Length; channel++)
                {
                    Assert.That(live.TextureAnimations[channel].FrameValues,
                        Is.EqualTo(saved.TextureAnimations[channel].FrameValues),
                        label + $" texture animation {channel} frames");
                    Assert.That(live.TextureAnimations[channel].Cursor,
                        Is.EqualTo(saved.TextureAnimations[channel].Cursor),
                        label + $" texture animation {channel} cursor");
                    Assert.That(live.TextureAnimations[channel].PlaybackMode,
                        Is.EqualTo(saved.TextureAnimations[channel].PlaybackMode),
                        label + $" texture animation {channel} mode");
                }
                Assert.That(live.Owner, Is.EqualTo(saved.Owner), " owner");
                AssertRawSingle(saved.WorkStart, live.WorkStart, " work start");
                AssertRawSingle(saved.WorkLength, live.WorkLength, " work length");
                Assert.That(live.FogOfWarSizeX, Is.EqualTo(saved.FogOfWarSizeX), " FoW size X");
                Assert.That(live.FogOfWarSizeY, Is.EqualTo(saved.FogOfWarSizeY), " FoW size Y");
                AssertRawSingle(saved.FogOfWarLastX, live.FogOfWarLastX, " FoW last X");
                AssertRawSingle(saved.FogOfWarLastY, live.FogOfWarLastY, " FoW last Y");
                Assert.That(live.WalkResult, Is.EqualTo(saved.WalkResult), " walk result");
                Assert.That(live.WeaponClassId, Is.EqualTo(saved.WeaponClassId), " weapon class");
                Assert.That(live.ShieldClassId, Is.EqualTo(saved.ShieldClassId), " shield class");
                Assert.That(live.PickTargetMode, Is.EqualTo(saved.PickTargetMode), " pick mode");
                Assert.That(live.LinkedObjectId, Is.EqualTo(saved.LinkedObjectId), " linked id");
                Assert.That(live.LinkedSlot, Is.EqualTo(saved.LinkedSlot), " linked slot");
                Assert.That(live.PlaceSnapMode, Is.EqualTo(saved.PlaceSnapMode), " place snap");
                Assert.That(live.DigOrderId, Is.EqualTo(saved.DigOrderId), " dig order");
                Assert.That(live.ObjectFlags, Is.EqualTo(saved.ObjectFlags), " flags");
                Assert.That(live.OwnsTclState, Is.EqualTo(saved.HasTclState),
                    " archived Tcl-state presence");
                Assert.That(live.HasActionRow, Is.EqualTo(saved.StagedAction != null),
                    " archived action-row presence");
                Assert.That(live.IsTclVariableCompatibilityPassthrough, Is.True,
                    " Tcl compatibility passthrough — breaker: " +
                    OriginalSaveRuntimePatchMapper.LastPassthroughBreaker);
                Assert.That(live.TclVariables, Is.EqualTo(saved.TclVariables),
                    " Tcl raw order and duplicates");
                AssertTclVariables(saved.TclVariables, live.TclVariables, label);
                if (saved.AttributePageBits.Count == 0)
                {
                    // Native absence (optional grid presence 0): the capture keeps a zeroed
                    // internal 512 buffer but flags the absence for the emitter.
                    Assert.That(live.HasNativeAttributePage, Is.False,
                        " must record the absent native attribute page");
                }
                else
                {
                    Assert.That(live.HasNativeAttributePage, Is.True, label);
                    Assert.That(live.AttributePageBits, Is.EqualTo(saved.AttributePageBits),
                        " attribute page");
                }
                AssertEventQueue(saved, live.EventQueue);
                AssertFamily(saved.Family, live.Family, label);
                AssertTalkIssues(saved.TalkIssues, live.TalkIssues, label);
                AssertPlanner(saved.Planner, live.Planner, label);
                AssertProduction(saved.Production, live.Production, label);
            }
        }

        private static void AssertTclVariables(
            IReadOnlyList<OriginalSaveTclVariable> expected,
            IReadOnlyList<OriginalSaveTclVariable> actual,
            string label)
        {
            var actualValues = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < actual.Count; index++)
                actualValues[actual[index].Name] = actual[index].Value;
            foreach (OriginalSaveTclVariable variable in expected)
            {
                Assert.That(actualValues.TryGetValue(variable.Name, out string? value), Is.True,
                    $"{label} Tcl variable {variable.Name}");
                Assert.That(value, Is.EqualTo(variable.Value),
                    $"{label} Tcl variable {variable.Name}");
            }
        }

        private static void AssertEventQueue(
            OriginalSaveRuntimeEntitySnapshot expected,
            OriginalSaveCanonicalEventQueueState actual)
        {
            string label = $"object {expected.ObjectId} event queue";
            Assert.That(actual.ReadCursor, Is.EqualTo(expected.EventQueueReadCursor), label);
            Assert.That(actual.WriteCursor, Is.EqualTo(expected.EventQueueWriteCursor), label);
            Assert.That(actual.Slots, Has.Length.EqualTo(
                OriginalSaveObjectEventQueueDocument.Capacity));
            for (int slot = 0; slot < actual.Slots.Length; slot++)
            {
                OriginalSaveRuntimeEntityEventSnapshot? saved = expected.EventQueueSlots[slot];
                if (saved == null)
                {
                    Assert.That(actual.Slots[slot], Is.Null, $"{label} slot {slot}");
                    continue;
                }
                Assert.That(actual.Slots[slot], Is.Not.Null, $"{label} slot {slot}");
                AssertPayload(saved, actual.Slots[slot]!, $"{label} slot {slot}");
            }
            Assert.That(actual.DelayedRecords, Has.Length.EqualTo(expected.DelayedEvents.Count));
            for (int index = 0; index < actual.DelayedRecords.Length; index++)
                AssertPayload(expected.DelayedEvents[index], actual.DelayedRecords[index],
                    $"{label} delayed {index}");
        }

        private static void AssertFamily(
            OriginalSaveRuntimeEntityFamilySnapshot? expected,
            OriginalSaveCanonicalFamilyState? actual,
            string label)
        {
            if (expected == null)
            {
                Assert.That(actual, Is.Null, label + " family");
                return;
            }
            Assert.That(actual, Is.Not.Null, label + " family");
            Assert.That(actual!.PartnerId, Is.EqualTo(expected.Partner.Id));
            Assert.That(actual.PartnerState, Is.EqualTo(expected.Partner.State));
            Assert.That(actual.PartnerReferenceObjectId,
                Is.EqualTo(expected.Partner.ReferenceObjectId));
            Assert.That(actual.MotherId, Is.EqualTo(expected.Mother.Id));
            Assert.That(actual.MotherState, Is.EqualTo(expected.Mother.State));
            Assert.That(actual.MotherReferenceObjectId,
                Is.EqualTo(expected.Mother.ReferenceObjectId));
            Assert.That(actual.FatherId, Is.EqualTo(expected.Father.Id));
            Assert.That(actual.FatherState, Is.EqualTo(expected.Father.State));
            Assert.That(actual.FatherReferenceObjectId,
                Is.EqualTo(expected.Father.ReferenceObjectId));
            Assert.That(actual.ChildrenNames, Is.EqualTo(expected.ChildrenNames), label + " ChildrenNames");
            Assert.That(actual.PregnancyFlag, Is.EqualTo(expected.PregnancyFlag), label + " PregnancyFlag");
        }

        private static void AssertTalkIssues(
            OriginalSaveRuntimeEntityTalkIssueSnapshot? expected,
            OriginalSaveCanonicalTalkIssueState? actual,
            string label)
        {
            if (expected == null)
            {
                Assert.That(actual, Is.Null, label + " talk issues");
                return;
            }
            Assert.That(actual, Is.Not.Null, label + " talk issues");
            AssertRawSingle(expected.Sum, (float)actual!.StoredSum, label + " talk sum");
            Assert.That(actual.Entries, Has.Length.EqualTo(expected.Entries.Count));
            for (int index = 0; index < actual.Entries.Length; index++)
            {
                Assert.That(actual.Entries[index].Key, Is.EqualTo(expected.Entries[index].Name));
                Assert.That(BitConverter.SingleToInt32Bits((float)actual.Entries[index].Value),
                    Is.EqualTo(expected.Entries[index].WeightBits));
            }
        }

        private static void AssertPlanner(
            OriginalSaveRuntimeEntityPlannerSnapshot? expected,
            OriginalSaveCanonicalPlannerState? actual,
            string label)
        {
            if (expected == null)
            {
                Assert.That(actual, Is.Null, label + " planner");
                return;
            }
            Assert.That(actual, Is.Not.Null, label + " planner");
            Assert.That(actual!.IdleAnnounceFlag, Is.EqualTo(expected.IdleAnnounceFlag));
            Assert.That(BitConverter.SingleToInt32Bits(actual.IdleSince),
                Is.EqualTo(expected.IdleSinceBits));
            Assert.That(actual.CurrentWorkplaceObjectId, Is.EqualTo(expected.CurrentWorkplaceObjectId), label + " CurrentWorkplaceObjectId");
            Assert.That(actual.AssignedWorkplaceObjectId, Is.EqualTo(expected.AssignedWorkplaceObjectId), label + " AssignedWorkplaceObjectId");
        }

        private static void AssertProduction(
            OriginalSaveRuntimeEntityProductionSnapshot? expected,
            OriginalSaveCanonicalProductionState? actual,
            string label)
        {
            if (expected == null)
            {
                Assert.That(actual, Is.Null, label + " production");
                return;
            }
            Assert.That(actual, Is.Not.Null, label + " production");
            Assert.That(actual!.Slots, Has.Length.EqualTo(expected.Slots.Count));
            for (int index = 0; index < actual.Slots.Length; index++)
            {
                OriginalSaveRuntimeEntityProductionSlotSnapshot saved = expected.Slots[index];
                OriginalSaveCanonicalProductionSlotState live = actual.Slots[index];
                string slotLabel = label + $" prod slot {index}";
                Assert.That(live.RegistryValue1, Is.EqualTo(saved.RegistryValue1), slotLabel + " reg1");
                Assert.That(live.RegistryValue2, Is.EqualTo(saved.RegistryValue2), slotLabel + " reg2");
                Assert.That(live.ClassName, Is.EqualTo(saved.ClassName), slotLabel + " class");
                Assert.That(live.ResourceName, Is.EqualTo(saved.ResourceName), slotLabel + " resource");
                Assert.That(live.OrderCount, Is.EqualTo(saved.OrderCount), slotLabel + " orders");
                Assert.That(live.DemandBits, Is.EqualTo(saved.DemandBits), slotLabel + " demand");
                Assert.That(live.Invented, Is.EqualTo(saved.Invented), slotLabel + " invented");
                Assert.That(live.Buildable, Is.EqualTo(saved.Buildable), slotLabel + " buildable");
            }
            Assert.That(actual.WorkState, Is.EqualTo(expected.WorkState), label + " WorkState");
            Assert.That(actual.AssignedWorkerObjectId, Is.EqualTo(expected.AssignedWorkerObjectId), label + " AssignedWorkerObjectId");
            Assert.That(actual.RequestBytes, Is.EqualTo(expected.RequestBytes), label + " RequestBytes");
            Assert.That(actual.RequestBytes[6], Is.EqualTo(actual.RequestBytes[0]),
                label + " CProd +0x28 double emission");
            Assert.That(actual.PendingPositionBits, Is.EqualTo(expected.PendingPositionBits), label + " PendingPositionBits");
            Assert.That(actual.FlowCompatibilityBytes, Is.EqualTo(expected.FlowBytes), label + " FlowCompatibilityBytes");
            Assert.That(actual.ProductionValues, Is.EqualTo(expected.ProductionValues), label + " ProductionValues");
            Assert.That(actual.ProductionEnabled, Is.EqualTo(expected.ProductionEnabled), label + " ProductionEnabled");
            Assert.That(actual.EnergyValues, Is.EqualTo(expected.EnergyValues), label + " EnergyValues");
            Assert.That(actual.GuestSeats, Has.Length.EqualTo(expected.GuestSeats.Count));
            for (int index = 0; index < actual.GuestSeats.Length; index++)
            {
                Assert.That(actual.GuestSeats[index].LinkId,
                    Is.EqualTo(expected.GuestSeats[index].SeatLinkId),
                    label + $" guest seat {index} link");
                Assert.That(actual.GuestSeats[index].GuestObjectId,
                    Is.EqualTo(expected.GuestSeats[index].GuestObjectId),
                    label + $" guest seat {index} occupant");
                Assert.That(actual.GuestSeats[index].OrderCount,
                    Is.EqualTo(expected.GuestSeats[index].OrderCount),
                    label + $" guest seat {index} orders");
            }
            Assert.That(actual.State3C, Is.EqualTo(expected.State3C), label + " State3C");
            Assert.That(actual.State140, Is.EqualTo(expected.State140), label + " State140");
        }

        private static void AssertTimers(
            IReadOnlyList<OriginalSaveRuntimeTimer> expected,
            IReadOnlyList<OriginalSaveTimerRecord> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), "global timer count");
            for (int index = 0; index < actual.Count; index++)
            {
                OriginalSaveRuntimeTimer saved = expected[index];
                OriginalSaveTimerRecord live = actual[index];
                Assert.That(live.UserId, Is.EqualTo(saved.UserId));
                Assert.That(live.NextFire.Bits, Is.EqualTo(saved.NextFire.Bits));
                Assert.That(live.Interval.Bits, Is.EqualTo(saved.Interval.Bits));
                Assert.That(live.RepeatsLeft, Is.EqualTo(saved.RepeatsLeft));
                Assert.That(live.OwnerObjectId, Is.EqualTo(saved.OwnerObjectId));
                AssertPayload(saved, live.EventPayload, $"global timer {index}");
            }
        }

        private static void AssertTriggers(
            IReadOnlyList<OriginalSaveRuntimeTriggerSnapshot?> expected,
            IReadOnlyList<TriggerRecord?> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), "trigger count");
            for (int index = 0; index < actual.Count; index++)
            {
                OriginalSaveRuntimeTriggerSnapshot? saved = expected[index];
                TriggerRecord? live = actual[index];
                if (saved == null)
                {
                    Assert.That(live, Is.Null, $"trigger slot {index}");
                    continue;
                }
                Assert.That(live, Is.Not.Null, $"trigger slot {index}");
                Assert.That(live!.Index, Is.EqualTo(saved.Index));
                Assert.That((int)live.Kind, Is.EqualTo(saved.Kind));
                Assert.That(live.Execution, Is.EqualTo(saved.Execution));
                Assert.That(live.OwnerHandle, Is.EqualTo(saved.OwnerObjectId));
                Assert.That(live.FireScript, Is.EqualTo(saved.FireScript));
                AssertRawDouble(saved.LastCheck, live.LastCheck, $"trigger {index} last check");
                AssertRawDouble(saved.CheckTimer, live.CheckTimer, $"trigger {index} timer");
                AssertRawDouble(saved.CheckIdle, live.CheckIdle, $"trigger {index} idle");
                AssertRawDouble(saved.CheckNear, live.CheckNear, $"trigger {index} near");
                Assert.That(live.Active, Is.EqualTo(saved.Active));
                AssertRawSingle(saved.TargetRange, live.TargetRange, $"trigger {index} range");
                AssertRawDouble(saved.FireTime, live.FireTime, $"trigger {index} fire time");
                AssertRawDouble(saved.Interval, live.Interval, $"trigger {index} interval");
                Assert.That(live.TargetRef, Is.EqualTo(saved.TargetObjectId));
                Assert.That(live.TargetClass, Is.EqualTo(saved.TargetClass));
                Assert.That(live.TargetTypes ?? new List<string>(), Is.EqualTo(saved.TargetTypes));
                Assert.That(live.TargetOwnerSpec ?? "", Is.EqualTo(saved.TargetOwnerSpec));
                Assert.That(live.TargetCount, Is.EqualTo(saved.TargetCount));
                Assert.That(live.CallbackScript, Is.EqualTo(saved.CallbackScript));
                Assert.That(live.Actuators, Is.EqualTo(saved.Actuators));
            }
        }

        private static void AssertAiOwners(
            OriginalSaveRuntimeAiSnapshot expected,
            AiSystem.SaveState actual)
        {
            Assert.That(actual.Owners, Has.Length.EqualTo(expected.Owners.Count));
            foreach (OriginalSaveRuntimeAiOwnerSnapshot saved in expected.Owners)
            {
                AiSystem.OwnerSaveState live = actual.Owners[saved.Owner];
                Assert.That(live.Enabled, Is.EqualTo(saved.Enabled));
                Assert.That(live.Initialized, Is.EqualTo(saved.InterpreterPresent));
                Assert.That(live.VariableNames, Has.Length.EqualTo(saved.Variables.Count));
                for (int index = 0; index < live.VariableNames.Length; index++)
                {
                    Assert.That(live.VariableNames[index], Is.EqualTo(saved.Variables[index].Name));
                    Assert.That(live.VariableValues[index], Is.EqualTo(saved.Variables[index].Value));
                }
            }
        }

        private static void AssertStory(
            OriginalSaveRuntimeStorySnapshot expected,
            OriginalSaveCanonicalStoryState actual)
        {
            Assert.That(actual.EventOrder, Has.Length.EqualTo(expected.Events.Count));
            Assert.That(actual.Events, Has.Length.EqualTo(expected.Events.Count));
            Assert.That(actual.EventTriggers, Has.Length.EqualTo(expected.Events.Count));
            for (int index = 0; index < expected.Events.Count; index++)
            {
                OriginalSaveRuntimeStoryEventSnapshot saved = expected.Events[index];
                Assert.That(actual.EventOrder[index], Is.EqualTo(saved.Name));
                Assert.That(actual.Events[index].Key, Is.EqualTo(saved.Name));
                Assert.That(actual.Events[index].Value,
                    Is.EqualTo(saved.Completed ? "1" : "0"));
                Assert.That(actual.EventTriggers[index].Name, Is.EqualTo(saved.Name));
                Assert.That(actual.EventTriggers[index].Trigger, Is.EqualTo(saved.Trigger));
                Assert.That(actual.EventTriggers[index].Extra, Is.EqualTo(saved.Extra));
            }
            Assert.That(actual.MapWidth, Is.EqualTo(expected.MapWidth), "story MapWidth");
            Assert.That(actual.MapHeight, Is.EqualTo(expected.MapHeight), "story MapHeight");
            Assert.That(actual.RectX, Is.EqualTo(expected.RectX), "story RectX");
            Assert.That(actual.RectY, Is.EqualTo(expected.RectY), "story RectY");
            Assert.That(actual.RectWidth, Is.EqualTo(expected.RectWidth), " RectWidth");
            Assert.That(actual.RectHeight, Is.EqualTo(expected.RectHeight), " RectHeight");
            Assert.That(actual.RawRectX, Is.EqualTo(expected.RawRectX), " RawRectX");
            Assert.That(actual.RawRectY, Is.EqualTo(expected.RawRectY), " RawRectY");
            Assert.That(actual.RawRectWidth, Is.EqualTo(expected.RawRectWidth), " RawRectWidth");
            Assert.That(actual.RawRectHeight, Is.EqualTo(expected.RawRectHeight), " RawRectHeight");
            Assert.That(actual.ScanX, Is.EqualTo(expected.ScanX), "story ScanX");
            Assert.That(actual.ScanY, Is.EqualTo(expected.ScanY), "story ScanY");
            Assert.That(actual.ZoneFloor, Is.EqualTo(expected.ZoneFloor), "story ZoneFloor");
            Assert.That(actual.LatchDirection, Is.EqualTo(expected.LatchDirection), "story LatchDirection");
            Assert.That(actual.LatchX, Is.EqualTo(expected.LatchX), "story LatchX");
            Assert.That(actual.LatchY, Is.EqualTo(expected.LatchY), "story LatchY");
            Assert.That(actual.LatchConsumed, Is.EqualTo(expected.LatchConsumed), " LatchConsumed");
            Assert.That(actual.DigWeight, Is.EqualTo(expected.DigWeight), " DigWeight");
            Assert.That(actual.DigMessage, Is.EqualTo(expected.DigMessageEnabled), " DigMessage");
            Assert.That(actual.DigThreshold, Is.EqualTo(expected.DigThreshold), "story DigThreshold");
            Assert.That(actual.Zones, Has.Length.EqualTo(expected.Zones.Count));
            for (int index = 0; index < actual.Zones.Length; index++)
            {
                Assert.That(actual.Zones[index].Name, Is.EqualTo(expected.Zones[index].Name));
                Assert.That(actual.Zones[index].Bound,
                    Is.EqualTo(expected.Zones[index].UpperBound));
            }
            int nonZeroMapValues = expected.MapValues.Count(value => value != 0);
            Assert.That(actual.Map, Has.Length.EqualTo(nonZeroMapValues));
        }

        private static void AssertPlayers(
            OriginalSaveRuntimePlayerSnapshot expected,
            OriginalSaveCanonicalPlayerState actual)
        {
            Assert.That(actual.Slots, Has.Length.EqualTo(expected.Slots.Count));
            for (int owner = 0; owner < actual.Slots.Length; owner++)
            {
                OriginalSaveRuntimePlayerSlotSnapshot saved = expected.Slots[owner];
                OriginalSaveCanonicalPlayerSlot live = actual.Slots[owner];
                Assert.That(live.PlayerName, Is.EqualTo(saved.PlayerName));
                Assert.That(live.SlotState, Is.EqualTo(saved.SlotState));
                Assert.That(live.PlayerOwnerId, Is.EqualTo(saved.PlayerOwnerId));
                Assert.That(live.ClanOwnerId, Is.EqualTo(saved.ClanOwnerId));
                Assert.That(live.SecondaryName, Is.EqualTo(saved.SecondaryName));
                for (int target = 0; target < saved.Diplomacy.Count; target++)
                    Assert.That(actual.Diplomacy[owner, target],
                        Is.EqualTo(saved.Diplomacy[target]));
            }
        }

        private static void AssertSelection(
            OriginalSaveRuntimeSelectionControlGroupSnapshot expected,
            IReadOnlyList<int[]> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Groups.Count));
            for (int index = 0; index < actual.Count; index++)
                Assert.That(actual[index], Is.EqualTo(expected.Groups[index]));
        }

        private static void AssertSparetime(
            OriginalSaveRuntimeSparetimeSnapshot expected,
            IReadOnlyList<SparetimeRegistration> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Registrations.Count));
            for (int index = 0; index < actual.Count; index++)
            {
                Assert.That((int)actual[index].Category,
                    Is.EqualTo(expected.Registrations[index].Category));
                Assert.That(actual[index].ObjectId,
                    Is.EqualTo(expected.Registrations[index].ObjectId));
            }
        }

        private static void AssertHorizon(
            OriginalSaveRuntimeHorizonSnapshot expected,
            IReadOnlyList<HorizonRegionState> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Regions.Count));
            for (int index = 0; index < actual.Count; index++)
            {
                OriginalSaveRuntimeHorizonRegion saved = expected.Regions[index];
                HorizonRegionState live = actual[index];
                AssertRawSingle(saved.PositionX, live.Position.x, $"horizon {index} x");
                AssertRawSingle(saved.PositionY, live.Position.y, $"horizon {index} y");
                AssertRawSingle(saved.PositionZ, live.Position.z, $"horizon {index} z");
                Assert.That(live.Red, Is.EqualTo(saved.Red));
                Assert.That(live.Green, Is.EqualTo(saved.Green));
                Assert.That(live.Blue, Is.EqualTo(saved.Blue));
                AssertRawSingle(saved.Depth, live.Depth, $"horizon {index} depth");
                AssertRawSingle(saved.Size, live.Size, $"horizon {index} size");
            }
        }

        private static void AssertAdaptiveSound(
            OriginalSaveRuntimeAdaptiveSoundMarkerSnapshot expected,
            IReadOnlyList<AudioState.MusicMarker> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Markers.Count));
            for (int index = 0; index < actual.Count; index++)
            {
                OriginalSaveRuntimeAdaptiveSoundMarker saved = expected.Markers[index];
                AudioState.MusicMarker live = actual[index];
                Assert.That(live.ObjectId, Is.EqualTo(saved.ObjectId));
                Assert.That(live.Theme, Is.EqualTo(saved.Theme));
                AssertRawSingle(saved.PositionX, live.Pos.x, $"sound marker {index} x");
                AssertRawSingle(saved.PositionY, live.Pos.y, $"sound marker {index} y");
                AssertRawSingle(saved.PositionZ, live.Pos.z, $"sound marker {index} z");
                AssertRawSingle(saved.Range, live.Radius, $"sound marker {index} radius");
            }
        }

        private static void AssertDerivableSections(
            OriginalSaveRuntimeSnapshot expected,
            OriginalSaveCanonicalCapture actual)
        {
            Assert.That(actual.ObjectIcons, Has.Length.EqualTo(
                OriginalSaveObjectIconDocument.SlotCount));
            Assert.That(actual.Actions.Select(action => action.ObjectId),
                Is.EqualTo(expected.Actions.Select(action => action.OwnerObjectId)),
                "native marker-12 order includes idle type-0 rows");
            for (int index = 0; index < actual.Actions.Length; index++)
            {
                Assert.That(actual.Actions[index].State != null,
                    Is.EqualTo(expected.Actions[index].HasActiveAction),
                    $"action row {index} active/idle state");
            }
            Assert.That(actual.AnimationControllers.Count(animation =>
                !animation.IsRetainedOrphan), Is.EqualTo(expected.Entities.Count));
            foreach (OriginalSaveCanonicalAnimationState animation in
                     actual.AnimationControllers.Where(animation => !animation.IsRetainedOrphan))
            {
                Assert.That(animation.Mode, Is.InRange(0, 2));
                Assert.That(BitConverter.DoubleToInt64Bits(animation.StartTime),
                    Is.EqualTo(BitConverter.DoubleToInt64Bits(actual.GameTime)));
            }
            var nativeParticleHandles = new HashSet<int>(
                expected.Particles.Records.Select(record => record.SlotIndex));
            foreach (ParticleSystemState.SourceEntry source in actual.Particles.Sources)
            {
                Assert.That(source.State.HasNativeState, Is.True);
                Assert.That(nativeParticleHandles.Contains(source.State.NativeParticleHandle),
                    Is.True, "captured particle source must map to a parsed native handle");
            }
            foreach (ParticleSystemState.TempSource source in actual.Particles.Temps)
            {
                Assert.That(nativeParticleHandles.Contains(source.NativeParticleHandle),
                    Is.True, "captured temporary particle must map to a parsed native handle");
            }
            ParticleSystemState.Validate(actual.Particles);
        }

        private static void AssertClassLights(
            IReadOnlyList<OriginalSaveRuntimeClassLight> expected,
            IReadOnlyList<OriginalSaveCanonicalClassLightState> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), " Count");
            for (int index = 0; index < expected.Count; index++)
            {
                OriginalSaveRuntimeClassLight saved = expected[index];
                OriginalSaveCanonicalClassLightState live = actual[index];
                string label = $"class light slot {saved.SlotIndex}";
                Assert.That(live.SlotIndex, Is.EqualTo(saved.SlotIndex), label);
                Assert.That(live.PositionX.Bits, Is.EqualTo(saved.PositionX.Bits), label);
                Assert.That(live.PositionY.Bits, Is.EqualTo(saved.PositionY.Bits), label);
                Assert.That(live.PositionZ.Bits, Is.EqualTo(saved.PositionZ.Bits), label);
                Assert.That(live.Red, Is.EqualTo(saved.Red), label);
                Assert.That(live.Green, Is.EqualTo(saved.Green), label);
                Assert.That(live.Blue, Is.EqualTo(saved.Blue), label);
                Assert.That(live.Radius.Bits, Is.EqualTo(saved.Radius.Bits), label);
                Assert.That(live.SourceToken, Is.EqualTo(saved.SourceToken), label);
                Assert.That(live.TrailingCompatibilityValue.Bits,
                    Is.EqualTo(saved.TrailingValue.Bits), label);
                Assert.That(live.IsRetainedObjectBound, Is.EqualTo(saved.IsObjectBound), label);
            }
        }

        private static bool ClassOwnsTclState(DigglesClass runtimeClass) =>
            !runtimeClass.DisableScripting &&
            (!string.IsNullOrEmpty(runtimeClass.InitCode) ||
             !string.IsNullOrEmpty(runtimeClass.ExitCode) ||
             runtimeClass.MemberVars.Count != 0 ||
             runtimeClass.EventHandlers.Count != 0 ||
             runtimeClass.States.Count != 0 ||
             runtimeClass.Methods.Count != 0);

        private static bool IsExcludedFreshTclVariable(string name)
        {
            if (name is
                "_invulnerable" or "_undeletable" or "_objectcollision")
            {
                return false;
            }
            return name.StartsWith("_", StringComparison.Ordinal) ||
                name is "this" or "outobj" or "wt";
        }

        private static void AssertPayload(
            OriginalSaveRuntimeEntityEventSnapshot expected,
            OriginalSaveTimerEventPayloadDocument actual,
            string label)
        {
            Assert.That(actual.CopyFixedEventName(), Is.EqualTo(expected.CopyFixedEventName()), label);
            Assert.That(actual.OwnerObjectId, Is.EqualTo(expected.OwnerObjectId), label);
            Assert.That(actual.Subject1ObjectId, Is.EqualTo(expected.Subject1ObjectId), label);
            Assert.That(actual.Subject2ObjectId, Is.EqualTo(expected.Subject2ObjectId), label);
            Assert.That(actual.Subject3ObjectId, Is.EqualTo(expected.Subject3ObjectId), label);
            OriginalSaveRaw32[] positions = Positions(actual);
            for (int index = 0; index < positions.Length; index++)
                Assert.That(positions[index].Bits, Is.EqualTo(expected.Arguments[index].Bits), label);
            Assert.That(actual.Text1.CopyBytesIncludingTerminator(),
                Is.EqualTo(expected.Text1.CopyBytesIncludingTerminator()), label);
            Assert.That(actual.Text2.CopyBytesIncludingTerminator(),
                Is.EqualTo(expected.Text2.CopyBytesIncludingTerminator()), label);
            Assert.That(actual.Number1.Bits, Is.EqualTo(expected.Word1.Bits), label);
            Assert.That(actual.Number2.Bits, Is.EqualTo(expected.Word2.Bits), label);
            Assert.That(actual.Number3.Bits, Is.EqualTo(expected.Word3.Bits), label);
            Assert.That(actual.CreatedAt.Bits, Is.EqualTo(expected.DueTime.Bits), label);
        }

        private static void AssertPayload(
            OriginalSaveRuntimeTimer expected,
            OriginalSaveTimerEventPayloadDocument actual,
            string label)
        {
            Assert.That(actual.CopyFixedEventName(), Is.EqualTo(expected.CopyFixedEventName()), label);
            Assert.That(actual.OwnerObjectId, Is.EqualTo(expected.EventOwnerObjectId), label);
            Assert.That(actual.Subject1ObjectId, Is.EqualTo(expected.Subject1ObjectId), label);
            Assert.That(actual.Subject2ObjectId, Is.EqualTo(expected.Subject2ObjectId), label);
            Assert.That(actual.Subject3ObjectId, Is.EqualTo(expected.Subject3ObjectId), label);
            OriginalSaveRaw32[] expectedPositions =
            {
                expected.Position1X, expected.Position1Y, expected.Position1Z,
                expected.Position2X, expected.Position2Y, expected.Position2Z,
                expected.Position3X, expected.Position3Y, expected.Position3Z,
            };
            OriginalSaveRaw32[] positions = Positions(actual);
            for (int index = 0; index < positions.Length; index++)
                Assert.That(positions[index].Bits, Is.EqualTo(expectedPositions[index].Bits), label);
            Assert.That(actual.Text1.CopyBytesIncludingTerminator(),
                Is.EqualTo(expected.Text1.CopyBytesIncludingTerminator()), label);
            Assert.That(actual.Text2.CopyBytesIncludingTerminator(),
                Is.EqualTo(expected.Text2.CopyBytesIncludingTerminator()), label);
            Assert.That(actual.Number1.Bits, Is.EqualTo(expected.Number1.Bits), label);
            Assert.That(actual.Number2.Bits, Is.EqualTo(expected.Number2.Bits), label);
            Assert.That(actual.Number3.Bits, Is.EqualTo(expected.Number3.Bits), label);
            Assert.That(actual.CreatedAt.Bits, Is.EqualTo(expected.CreatedAt.Bits), label);
        }

        private static void AssertPayload(
            OriginalSaveTimerEventPayloadDocument expected,
            OriginalSaveTimerEventPayloadDocument actual,
            string label)
        {
            Assert.That(actual.CopyFixedEventName(), Is.EqualTo(expected.CopyFixedEventName()), label);
            Assert.That(actual.OwnerObjectId, Is.EqualTo(expected.OwnerObjectId), label);
            Assert.That(actual.Subject1ObjectId, Is.EqualTo(expected.Subject1ObjectId), label);
            Assert.That(actual.Subject2ObjectId, Is.EqualTo(expected.Subject2ObjectId), label);
            Assert.That(actual.Subject3ObjectId, Is.EqualTo(expected.Subject3ObjectId), label);
            OriginalSaveRaw32[] expectedPositions = Positions(expected);
            OriginalSaveRaw32[] actualPositions = Positions(actual);
            for (int index = 0; index < actualPositions.Length; index++)
                Assert.That(actualPositions[index].Bits, Is.EqualTo(expectedPositions[index].Bits), label);
            Assert.That(actual.Text1.CopyBytesIncludingTerminator(),
                Is.EqualTo(expected.Text1.CopyBytesIncludingTerminator()), label);
            Assert.That(actual.Text2.CopyBytesIncludingTerminator(),
                Is.EqualTo(expected.Text2.CopyBytesIncludingTerminator()), label);
            Assert.That(actual.Number1.Bits, Is.EqualTo(expected.Number1.Bits), label);
            Assert.That(actual.Number2.Bits, Is.EqualTo(expected.Number2.Bits), label);
            Assert.That(actual.Number3.Bits, Is.EqualTo(expected.Number3.Bits), label);
            Assert.That(actual.CreatedAt.Bits, Is.EqualTo(expected.CreatedAt.Bits), label);
        }

        private static OriginalSaveRaw32[] Positions(
            OriginalSaveTimerEventPayloadDocument payload) =>
            new[]
            {
                payload.Position1X, payload.Position1Y, payload.Position1Z,
                payload.Position2X, payload.Position2Y, payload.Position2Z,
                payload.Position3X, payload.Position3Y, payload.Position3Z,
            };

        private static void AssertRawVector(
            OriginalSaveVector3 expected,
            Vector3 actual,
            string label)
        {
            AssertRawSingle(expected.X, actual.x, label + " x");
            AssertRawSingle(expected.Y, actual.y, label + " y");
            AssertRawSingle(expected.Z, actual.z, label + " z");
        }

        private static void AssertRawSingle(float expected, float actual, string label) =>
            Assert.That(BitConverter.SingleToInt32Bits(actual),
                Is.EqualTo(BitConverter.SingleToInt32Bits(expected)), label);

        private static void AssertRawDouble(double expected, double actual, string label) =>
            Assert.That(BitConverter.DoubleToInt64Bits(actual),
                Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)), label);

        private static void AssertSectionEqual(string section, object? expected, object? actual) =>
            AssertStructuralEqual(expected, actual, section);

        private static void AssertStructuralEqual(object? expected, object? actual, string path)
        {
            if (expected == null || actual == null)
            {
                Assert.That(actual, Is.EqualTo(expected), path);
                return;
            }
            Type type = expected.GetType();
            Assert.That(actual.GetType(), Is.EqualTo(type), path + " type");
            if (expected is float expectedFloat)
            {
                AssertRawSingle(expectedFloat, (float)actual, path);
                return;
            }
            if (expected is double expectedDouble)
            {
                AssertRawDouble(expectedDouble, (double)actual, path);
                return;
            }
            if (expected is OriginalSaveRaw32 raw32)
            {
                Assert.That(((OriginalSaveRaw32)actual).Bits, Is.EqualTo(raw32.Bits), path);
                return;
            }
            if (expected is OriginalSaveRaw64 raw64)
            {
                Assert.That(((OriginalSaveRaw64)actual).Bits, Is.EqualTo(raw64.Bits), path);
                return;
            }
            if (expected is OriginalSaveNativeString nativeString)
            {
                Assert.That(((OriginalSaveNativeString)actual).CopyBytesIncludingTerminator(),
                    Is.EqualTo(nativeString.CopyBytesIncludingTerminator()), path);
                return;
            }
            if (expected is Vector2 vector2)
            {
                AssertRawSingle(vector2.x, ((Vector2)actual).x, path + ".x");
                AssertRawSingle(vector2.y, ((Vector2)actual).y, path + ".y");
                return;
            }
            if (expected is Vector3 vector3)
            {
                AssertRawSingle(vector3.x, ((Vector3)actual).x, path + ".x");
                AssertRawSingle(vector3.y, ((Vector3)actual).y, path + ".y");
                AssertRawSingle(vector3.z, ((Vector3)actual).z, path + ".z");
                return;
            }
            if (expected is Vector4 vector4)
            {
                AssertRawSingle(vector4.x, ((Vector4)actual).x, path + ".x");
                AssertRawSingle(vector4.y, ((Vector4)actual).y, path + ".y");
                AssertRawSingle(vector4.z, ((Vector4)actual).z, path + ".z");
                AssertRawSingle(vector4.w, ((Vector4)actual).w, path + ".w");
                return;
            }
            if (expected is Quaternion quaternion)
            {
                AssertRawSingle(quaternion.x, ((Quaternion)actual).x, path + ".x");
                AssertRawSingle(quaternion.y, ((Quaternion)actual).y, path + ".y");
                AssertRawSingle(quaternion.z, ((Quaternion)actual).z, path + ".z");
                AssertRawSingle(quaternion.w, ((Quaternion)actual).w, path + ".w");
                return;
            }
            if (expected is Color color)
            {
                AssertRawSingle(color.r, ((Color)actual).r, path + ".r");
                AssertRawSingle(color.g, ((Color)actual).g, path + ".g");
                AssertRawSingle(color.b, ((Color)actual).b, path + ".b");
                AssertRawSingle(color.a, ((Color)actual).a, path + ".a");
                return;
            }
            if (expected is Color32 color32)
            {
                Assert.That((Color32)actual, Is.EqualTo(color32), path);
                return;
            }
            if (expected is OriginalSaveTimerEventPayloadDocument expectedPayload)
            {
                AssertPayloadDocuments(expectedPayload,
                    (OriginalSaveTimerEventPayloadDocument)actual, path);
                return;
            }
            if (type.IsPrimitive || type.IsEnum || expected is string || expected is decimal)
            {
                Assert.That(actual, Is.EqualTo(expected), path);
                return;
            }
            if (expected is Array expectedArray)
            {
                var actualArray = (Array)actual;
                Assert.That(actualArray.Rank, Is.EqualTo(expectedArray.Rank), path + " rank");
                for (int rank = 0; rank < expectedArray.Rank; rank++)
                    Assert.That(actualArray.GetLength(rank), Is.EqualTo(expectedArray.GetLength(rank)),
                        path + " length");
                Type? elementType = type.GetElementType();
                if (elementType != null &&
                    (elementType.IsPrimitive || elementType.IsEnum || elementType == typeof(string)))
                {
                    Assert.That(actualArray, Is.EqualTo(expectedArray), path);
                    return;
                }
                IEnumerator expectedValues = expectedArray.GetEnumerator();
                IEnumerator actualValues = actualArray.GetEnumerator();
                int index = 0;
                while (expectedValues.MoveNext())
                {
                    Assert.That(actualValues.MoveNext(), Is.True, path);
                    AssertStructuralEqual(expectedValues.Current, actualValues.Current,
                        $"{path}[{index++}]");
                }
                Assert.That(actualValues.MoveNext(), Is.False, path);
                return;
            }
            if (expected is IDictionary expectedDictionary)
            {
                var actualDictionary = (IDictionary)actual;
                Assert.That(actualDictionary.Count, Is.EqualTo(expectedDictionary.Count), path);
                foreach (DictionaryEntry entry in expectedDictionary)
                {
                    Assert.That(actualDictionary.Contains(entry.Key), Is.True, path);
                    AssertStructuralEqual(entry.Value, actualDictionary[entry.Key],
                        $"{path}[{entry.Key}]");
                }
                return;
            }
            if (expected is IEnumerable expectedEnumerable)
            {
                IEnumerator expectedValues = expectedEnumerable.GetEnumerator();
                IEnumerator actualValues = ((IEnumerable)actual).GetEnumerator();
                int index = 0;
                while (expectedValues.MoveNext())
                {
                    Assert.That(actualValues.MoveNext(), Is.True, path);
                    AssertStructuralEqual(expectedValues.Current, actualValues.Current,
                        $"{path}[{index++}]");
                }
                Assert.That(actualValues.MoveNext(), Is.False, path);
                return;
            }

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .ToArray();
            if (fields.Length == 0 && properties.Length == 0)
            {
                Assert.That(actual, Is.EqualTo(expected), path);
                return;
            }
            foreach (FieldInfo field in fields)
                AssertStructuralEqual(field.GetValue(expected), field.GetValue(actual),
                    path + "." + field.Name);
            foreach (PropertyInfo property in properties)
                AssertStructuralEqual(property.GetValue(expected), property.GetValue(actual),
                    path + "." + property.Name);
        }

        private static void AssertPayloadDocuments(
            OriginalSaveTimerEventPayloadDocument expected,
            OriginalSaveTimerEventPayloadDocument actual,
            string path)
        {
            Assert.That(actual.CopyFixedEventName(), Is.EqualTo(expected.CopyFixedEventName()), path);
            Assert.That(actual.OwnerObjectId, Is.EqualTo(expected.OwnerObjectId), path);
            Assert.That(actual.Subject1ObjectId, Is.EqualTo(expected.Subject1ObjectId), path);
            Assert.That(actual.Subject2ObjectId, Is.EqualTo(expected.Subject2ObjectId), path);
            Assert.That(actual.Subject3ObjectId, Is.EqualTo(expected.Subject3ObjectId), path);
            AssertStructuralEqual(Positions(expected), Positions(actual), path + ".positions");
            AssertStructuralEqual(expected.Text1, actual.Text1, path + ".text1");
            AssertStructuralEqual(expected.Text2, actual.Text2, path + ".text2");
            AssertStructuralEqual(expected.Number1, actual.Number1, path + ".number1");
            AssertStructuralEqual(expected.Number2, actual.Number2, path + ".number2");
            AssertStructuralEqual(expected.Number3, actual.Number3, path + ".number3");
            AssertStructuralEqual(expected.CreatedAt, actual.CreatedAt, path + ".createdAt");
        }

        private sealed class SessionHarness : IDisposable
        {
            private readonly GameObject _stableRoot;
            private readonly IDigglesEngineFactory _engineFactory;
            private readonly RuntimeGameSession _initialSession;
            private readonly IRuntimeWorldSessionHost _sessionHost;
            private readonly IOriginalSaveRuntimeSessionStager _sessionStager;
            private readonly IOriginalSaveActionInstaller _actionInstaller;

            internal SessionHarness()
            {
                _stableRoot = new GameObject(nameof(OriginalSaveCanonicalCaptureTests));
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                _engineFactory = new DigglesEngineFactory();
                _initialSession = RuntimeGameSession.CreateInitial(
                    _engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        BdbRelativePath),
                    initialRoot);
                _sessionHost = new RuntimeWorldSessionHost(
                    _engineFactory,
                    new ActiveGameSession(_initialSession),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                var actionFactory = new OriginalSaveActionFactory(
                    new RuntimeSaveableActionFactory());
                _actionInstaller = new OriginalSaveActionInstaller();
                _sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    _actionInstaller);
            }

            internal RuntimeGameSession CreateCandidate() => _sessionHost.CreateCandidate(
                OriginalDataTestFixture.DataRoot,
                BdbRelativePath);

            internal OriginalSaveRuntimeSessionState StageForCapture(
                RuntimeGameSession candidate,
                OriginalSaveRuntimeSnapshot snapshot)
            {
                OriginalSaveRuntimeSessionState state =
                    _sessionStager.StageCandidate(candidate, snapshot, BdbRelativePath);
                // StageCandidate already installs prepared actions internally;
                // a second Install would trip the already-has-action guard.
                _actionInstaller.InstallPendingEntityEvents(
                    state.PendingEntityEvents,
                    state.Entities,
                    candidate.Engine.Ctx,
                    snapshot.GameTime);
                return state;
            }

            public void Dispose()
            {
                _initialSession.Dispose();
                UnityEngine.Object.DestroyImmediate(_stableRoot);
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }
    }
}
