#nullable enable

namespace Diggles.Runtime.Save
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine;

    // Writer-ready semantic capture of the save-canonical section graph. Values come from
    // live services; fields explicitly named Compatibility/Residue are the only retained bytes.
    public sealed class OriginalSaveCanonicalCapture
    {
        public double GameTime { get; internal set; }
        public int GroundBeginRow { get; internal set; } = -10000;
        public OriginalSaveCanonicalEntityState[] Entities { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalEntityState>();
        public OriginalSaveCanonicalOwnerAttributeBlock[] OwnerAttributes { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalOwnerAttributeBlock>();
        public OriginalSaveTimerRecord[] GlobalTimers { get; internal set; } =
            System.Array.Empty<OriginalSaveTimerRecord>();
        public TimerRegistry.Entry[] TimerRegistryEntries { get; internal set; } =
            System.Array.Empty<TimerRegistry.Entry>();
        public TimerScheduler.Entry[] ScheduledTimers { get; internal set; } =
            System.Array.Empty<TimerScheduler.Entry>();
        public TriggerRecord?[] Triggers { get; internal set; } =
            System.Array.Empty<TriggerRecord?>();
        public AiSystem.SaveState Ai { get; internal set; } = new();
        public OriginalSaveCanonicalStoryState Story { get; internal set; } = new();
        public OriginalSaveCanonicalPlayerState Players { get; internal set; } = new();
        public int[][] SelectionControlGroups { get; internal set; } =
            System.Array.Empty<int[]>();
        public SparetimeRegistration[] Sparetime { get; internal set; } =
            System.Array.Empty<SparetimeRegistration>();
        public HorizonRegionState[] Horizon { get; internal set; } =
            System.Array.Empty<HorizonRegionState>();
        public AudioState.MusicMarker[] AdaptiveSoundMarkers { get; internal set; } =
            System.Array.Empty<AudioState.MusicMarker>();
        public NewsTickerSystem.SaveState NewsTicker { get; internal set; } = new();
        public DiggableTerrain.CaveRecord[] CaveRecords { get; internal set; } =
            System.Array.Empty<DiggableTerrain.CaveRecord>();
        public EnergySystem.SaveState Energy { get; internal set; } = new();
        public ParticleSystemState.SaveState Particles { get; internal set; } = new();
        public LoreTrackRecord[] LoreTracks { get; internal set; } =
            System.Array.Empty<LoreTrackRecord>();
        public OriginalSaveCanonicalClassLightState[] ClassLights { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalClassLightState>();
        public OriginalSaveCanonicalAnimationState[] AnimationControllers { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalAnimationState>();
        public OriginalSaveObjectIconRecord[] ObjectIcons { get; internal set; } =
            System.Array.Empty<OriginalSaveObjectIconRecord>();
        public RuntimeEntityActionSaveStateEntry[] Actions { get; internal set; } =
            System.Array.Empty<RuntimeEntityActionSaveStateEntry>();
        // Surviving marker-7 dormant scenery seeds (ctx.MapDecoSeeds entries still standing at
        // capture time, in archive order). Lets the writer re-emit the spatial index when the
        // object graph changed and the retained bytes cannot be spliced (empty = deco lost for
        // good — the native never regenerates dropped records).
        public OriginalSaveSpatialSeed[] DecoSeeds { get; internal set; } =
            System.Array.Empty<OriginalSaveSpatialSeed>();
        public int[] ProductionSlotOverrides { get; internal set; } = System.Array.Empty<int>();
        public ElfState.NativeSaveState? Elf { get; internal set; }
        public DiggableTerrain.SaveState? Terrain { get; internal set; }
        public FogOfWarState.SaveState? FogOfWar { get; internal set; }
        public FluidSystem.SaveState Fluid { get; internal set; } = new();
    }

    public sealed class OriginalSaveCanonicalOwnerAttributeBlock
    {
        public int Owner { get; internal set; }
        public int[] PrimaryValueBits { get; internal set; } = new int[512];
        public int[] SecondaryCompatibilityBits { get; internal set; } = new int[512];
    }

    public sealed class OriginalSaveCanonicalClassLightState
    {
        public int SlotIndex { get; internal set; }
        public OriginalSaveRaw32 PositionX { get; internal set; }
        public OriginalSaveRaw32 PositionY { get; internal set; }
        public OriginalSaveRaw32 PositionZ { get; internal set; }
        public int Red { get; internal set; }
        public int Green { get; internal set; }
        public int Blue { get; internal set; }
        public OriginalSaveRaw32 Radius { get; internal set; }
        public int SourceToken { get; internal set; }
        public OriginalSaveRaw32 TrailingCompatibilityValue { get; internal set; }
        public bool IsRetainedObjectBound { get; internal set; }
        public bool IsObjectBound => SourceToken > 1;
    }

    public sealed class OriginalSaveCanonicalEntityState
    {
        public int ObjectId { get; internal set; }
        public string ClassName { get; internal set; } = "";
        public int ObjectType { get; internal set; }
        public string InstanceName { get; internal set; } = "";
        // Native obj+0x360 info string (set_objinfo, e.g. the gnome's "EinZwerg").
        public string InfoString { get; internal set; } = "";
        // Native obj+0x200/+0x204/+0x208: live/target per-class state-table indices + the
        // think-enable word (port StateEnabledCount + 1). Sourced verbatim from the archive
        // while the restored machine is untouched, else derived from the live entity.
        public int StateLiveIndex { get; internal set; } = -1;
        public int StateTargetIndex { get; internal set; } = -1;
        public int StateEnabledWord { get; internal set; }
        public Vector3 Position { get; internal set; }
        public Vector3 Velocity { get; internal set; }
        public Vector3 Rotation { get; internal set; }
        public int[] CoreStateBits { get; internal set; } = new int[13];
        public int Owner { get; internal set; }
        public float CreationTimeHours { get; internal set; }
        public float WorkStart { get; internal set; }
        public float WorkLength { get; internal set; }
        public string[] TaskList { get; internal set; } = System.Array.Empty<string>();
        public int[] FogBrightnessCache { get; internal set; } = System.Array.Empty<int>();

        // CObj icon holder lanes (obj+0x2B8): global icon-table slot per lane, -1 = empty.
        // Every active slot in ObjectIcons must appear in its owner's lanes, or the original
        // can never expire the icon (permanent HP bars).
        public int[] IconHolderSlots { get; internal set; } = System.Array.Empty<int>();

        // Native render-dirty latch obj+0x3B4 (the N35 byte): 1 after any real visibility
        // flip / model destruct, never cleared (FUN_0067cca0:79-94).
        public bool RenderDirtyLatch { get; internal set; }
        public int FogOfWarSizeX { get; internal set; }
        public int FogOfWarSizeY { get; internal set; }
        public float FogOfWarLastX { get; internal set; }
        public float FogOfWarLastY { get; internal set; }
        public float FogOfWarLastZ { get; internal set; }
        public int[] ParticleStateWords { get; internal set; } = System.Array.Empty<int>();
        public byte WalkingActionActive { get; internal set; }
        public byte WorkTimeState { get; internal set; }
        // Per-object light (object-form change_light): N22 words 5..7/9 + N23 color ints
        // (color × 20; corpus default [20,18,10] = {1, 0.9, 0.5}).
        public Vector3 LightOffset { get; internal set; } = new Vector3(0f, -0.6f, -2f);
        public float LightRange { get; internal set; } = 2f;
        public Vector3 LightColor { get; internal set; } = new Vector3(1f, 0.9f, 0.5f);
        public int NativeMutationFlags { get; internal set; }
        public int WalkResult { get; internal set; }
        public int WeaponClassId { get; internal set; }
        public int ShieldClassId { get; internal set; }
        public int PickTargetMode { get; internal set; }
        public bool AttackInProgress { get; internal set; }
        public int BallisticResult { get; internal set; }
        public bool DiedInFight { get; internal set; }
        public bool ClimbAbility { get; internal set; }
        public int ObjectFlags { get; internal set; }
        // Native N10/marker-12 coupling: action rows are a strict subset of N10 owners.
        // HasActionRow = the object entered the action system (class-scripted at creation,
        // or acting now; retained = archived row presence). OwnsTclState additionally covers
        // lazy interpreter owners — deco that only ever had a variable set on it owns N10
        // with no action row (35 Dummy_/Urw_caves_ bodies there).
        public bool OwnsTclState { get; internal set; }
        public bool HasActionRow { get; internal set; }
        public OriginalSaveTclVariable[] TclVariables { get; internal set; } =
            System.Array.Empty<OriginalSaveTclVariable>();
        public bool IsTclVariableCompatibilityPassthrough { get; internal set; }
        public int AnimationModelDatabaseSlot { get; internal set; } = -1;
        public int AnimationFrameValue { get; internal set; } = -1;
        public int AnimationState { get; internal set; } = -1;
        public int AnimationMode { get; internal set; }
        public int InitialAnimationModelDatabaseSlot { get; internal set; }
        public int InitialAnimationState { get; internal set; }
        public int InitialAnimationStartFrame { get; internal set; }
        public int InitialAnimationMode { get; internal set; }
        public int[] CurrentPartFrames { get; internal set; } = System.Array.Empty<int>();
        public OriginalSaveCanonicalTextureAnimationState[] TextureAnimations { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalTextureAnimationState>();
        public int InventoryCapacity { get; internal set; }
        public int[] Inventory { get; internal set; } = System.Array.Empty<int>();
        public int ContainedInObjectId { get; internal set; } = -1;
        public int LinkedObjectId { get; internal set; } = -1;
        public int LinkedSlot { get; internal set; } = -1;
        public int PlaceSnapMode { get; internal set; }
        public int DigOrderId { get; internal set; } = -1;
        public int[] AttributePageBits { get; internal set; } = new int[512];
        // False when the native optional attribute grid was absent for this object (presence 0);
        // the emitter reproduces the absence instead of synthesizing a page.
        public bool HasNativeAttributePage { get; internal set; } = true;
        public OriginalSaveCanonicalEventQueueState EventQueue { get; internal set; } = new();
        public OriginalSaveCanonicalFamilyState? Family { get; internal set; }
        public OriginalSaveCanonicalTalkIssueState? TalkIssues { get; internal set; }
        public OriginalSaveCanonicalPlannerState? Planner { get; internal set; }
        public OriginalSaveCanonicalProductionState? Production { get; internal set; }
    }

    public sealed class OriginalSaveCanonicalTextureAnimationState
    {
        public int[] FrameValues { get; internal set; } = System.Array.Empty<int>();
        public int Cursor { get; internal set; }
        public int PlaybackMode { get; internal set; }
    }

    public sealed class OriginalSaveCanonicalEventQueueState
    {
        public int ReadCursor { get; internal set; }
        public int WriteCursor { get; internal set; }
        public OriginalSaveTimerEventPayloadDocument?[] Slots { get; internal set; } =
            new OriginalSaveTimerEventPayloadDocument?[OriginalSaveObjectEventQueueDocument.Capacity];
        public OriginalSaveTimerEventPayloadDocument[] DelayedRecords { get; internal set; } =
            System.Array.Empty<OriginalSaveTimerEventPayloadDocument>();
    }

    public sealed class OriginalSaveCanonicalFamilyState
    {
        public int PartnerId { get; internal set; }
        public int PartnerState { get; internal set; }
        public int PartnerReferenceObjectId { get; internal set; } = -1;
        public int MotherId { get; internal set; }
        public int MotherState { get; internal set; }
        public int MotherReferenceObjectId { get; internal set; } = -1;
        public int FatherId { get; internal set; }
        public int FatherState { get; internal set; }
        public int FatherReferenceObjectId { get; internal set; } = -1;
        public string[] ChildrenNames { get; internal set; } = System.Array.Empty<string>();
        public bool PregnancyFlag { get; internal set; }
    }

    public sealed class OriginalSaveCanonicalTalkIssueState
    {
        public KeyValuePair<string, double>[] Entries { get; internal set; } =
            System.Array.Empty<KeyValuePair<string, double>>();
        public double StoredSum { get; internal set; }
    }

    public sealed class OriginalSaveCanonicalPlannerState
    {
        public int IdleAnnounceFlag { get; internal set; }
        public float IdleSince { get; internal set; }
        public int CurrentWorkplaceObjectId { get; internal set; } = -1;
        public int AssignedWorkplaceObjectId { get; internal set; } = -1;
    }

    public sealed class OriginalSaveCanonicalProductionSlotState
    {
        public int RegistryValue1 { get; internal set; }
        public int RegistryValue2 { get; internal set; }
        public string ClassName { get; internal set; } = "";
        public string ResourceName { get; internal set; } = "";
        public int OrderCount { get; internal set; }
        public int DemandBits { get; internal set; }
        public byte Invented { get; internal set; }
        public byte Buildable { get; internal set; }
    }

    public readonly struct OriginalSaveCanonicalGuestSeatState
    {
        public OriginalSaveCanonicalGuestSeatState(int linkId, int guestObjectId, int orderCount)
        {
            LinkId = linkId;
            GuestObjectId = guestObjectId;
            OrderCount = orderCount;
        }

        public int LinkId { get; }
        public int GuestObjectId { get; }
        public int OrderCount { get; }
    }

    public sealed class OriginalSaveCanonicalProductionState
    {
        public OriginalSaveCanonicalProductionSlotState[] Slots { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalProductionSlotState>();
        public int WorkState { get; internal set; }
        public int AssignedWorkerObjectId { get; internal set; } = -1;
        // Seven bytes in native order. Element 6 deliberately repeats element 0 (+0x28).
        public byte[] RequestBytes { get; internal set; } = new byte[7];
        public int[] PendingPositionBits { get; internal set; } = new int[3];
        public byte[] FlowCompatibilityBytes { get; internal set; } = System.Array.Empty<byte>();
        public int[] ProductionValues { get; internal set; } = new int[4];
        public byte ProductionEnabled { get; internal set; }
        public int[] EnergyValues { get; internal set; } = new int[5];
        public OriginalSaveCanonicalGuestSeatState[] GuestSeats { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalGuestSeatState>();
        public byte State3C { get; internal set; }
        public byte State140 { get; internal set; }
    }

    public readonly struct RuntimeEntityActionSaveStateEntry
    {
        public RuntimeEntityActionSaveStateEntry(int ownerObjectId)
            : this(ownerObjectId, null, null, null)
        {
        }

        public RuntimeEntityActionSaveStateEntry(int ownerObjectId, RuntimeEntityActionSaveState state)
            : this(ownerObjectId, state, null, null)
        {
        }

        public RuntimeEntityActionSaveStateEntry(
            int ownerObjectId,
            RuntimeEntityActionSaveState? state,
            string? finishCode,
            string? breakCode)
        {
            ObjectId = ownerObjectId;
            State = state;
            FinishCode = finishCode;
            BreakCode = breakCode;
        }

        public int ObjectId { get; }
        public RuntimeEntityActionSaveState? State { get; }
        // Native action-Common Tcl codes: the port keeps them on the entity
        // (ent.ActionFinish/ActionBreak), not on the action object. Losing them breaks the
        // idle discipline of a restored save: the `wait 1 {state_enable this}` completes
        // with an empty finishcode, the gnome never re-enables, and its state machine stays
        // think-dead forever, in the port and in the original.
        public string? FinishCode { get; }
        public string? BreakCode { get; }
    }

    public sealed class OriginalSaveCanonicalAnimationState
    {
        public int ObjectId { get; internal set; }
        public string ClipName { get; internal set; } = "";
        public int Mode { get; internal set; } = -1;
        public int CurrentFrame { get; internal set; }
        public int FrameCount { get; internal set; }
        // Exact native.3db mesh-index vector; the original renderer consumes these values.
        public int[] FrameValues { get; internal set; } = System.Array.Empty<int>();
        public double StartTime { get; internal set; }
        public int ModelDatabaseSlot { get; internal set; } = -1;
        public int AnimationState { get; internal set; } = -1;
        public bool IsRetainedOrphan { get; internal set; }
        // Orphan-only verbatim words. A controller that outlived its object carries allocator
        // residue in these fields (the corpus holds mode words like 0xDFDFDFDF), so orphans
        // bypass semantic validation and re-emit the archived bytes untouched.
        public byte RawStateByte { get; internal set; }
        public long RawTimeBits { get; internal set; }
        public int RepeatedControllerIndex { get; internal set; }
        public int RawControlB { get; internal set; }
    }

    public sealed class OriginalSaveCanonicalStoryState
    {
        public KeyValuePair<string, string>[] Events { get; internal set; } =
            System.Array.Empty<KeyValuePair<string, string>>();
        public string[] EventOrder { get; internal set; } = System.Array.Empty<string>();
        public OriginalSaveCanonicalStoryTrigger[] EventTriggers { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalStoryTrigger>();
        public OriginalSaveCanonicalStoryMapEntry[] Map { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalStoryMapEntry>();
        public KeyValuePair<int, string>[] Callbacks { get; internal set; } =
            System.Array.Empty<KeyValuePair<int, string>>();
        public OriginalSaveCanonicalStoryZone[] Zones { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalStoryZone>();
        public OriginalSaveCanonicalStoryTemp[] Temps { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalStoryTemp>();
        public KeyValuePair<string, string>[] TempGroups { get; internal set; } =
            System.Array.Empty<KeyValuePair<string, string>>();
        public (string Name, int X, int Y)[] PlacedTemps { get; internal set; } =
            System.Array.Empty<(string, int, int)>();
        public int DigWeight { get; internal set; }
        public int DigThreshold { get; internal set; }
        public bool DigMessage { get; internal set; }
        public int LatchDirection { get; internal set; }
        public int LatchX { get; internal set; }
        public int LatchY { get; internal set; }
        public bool LatchConsumed { get; internal set; }
        public int MapWidth { get; internal set; }
        public int MapHeight { get; internal set; }
        public int RectX { get; internal set; }
        public int RectY { get; internal set; }
        public int RectWidth { get; internal set; }
        public int RectHeight { get; internal set; }
        public string UniqueTemp { get; internal set; } = "";
        public int RawRectX { get; internal set; }
        public int RawRectY { get; internal set; }
        public int RawRectWidth { get; internal set; }
        public int RawRectHeight { get; internal set; }
        public int ScanX { get; internal set; }
        public int ScanY { get; internal set; }
        public bool UniqueTempPlaced { get; internal set; }
        public string UniqueTempPlacedName { get; internal set; } = "";
        public int UniqueTempWorldX { get; internal set; }
        public int UniqueTempWorldY { get; internal set; }
        public int UniqueTempExtentWidth { get; internal set; }
        public int UniqueTempExtentHeight { get; internal set; }
        public int ZoneFloor { get; internal set; }
    }

    public readonly struct OriginalSaveCanonicalStoryTrigger
    {
        public OriginalSaveCanonicalStoryTrigger(string name, string trigger, string extra)
        {
            Name = name;
            Trigger = trigger;
            Extra = extra;
        }

        public string Name { get; }
        public string Trigger { get; }
        public string Extra { get; }
    }

    public readonly struct OriginalSaveCanonicalStoryMapEntry
    {
        public OriginalSaveCanonicalStoryMapEntry(int x, int y, int value)
        {
            X = x;
            Y = y;
            Value = value;
        }

        public int X { get; }
        public int Y { get; }
        public int Value { get; }
    }

    public readonly struct OriginalSaveCanonicalStoryZone
    {
        public OriginalSaveCanonicalStoryZone(string name, int bound)
        {
            Name = name;
            Bound = bound;
        }

        public string Name { get; }
        public int Bound { get; }
    }

    public sealed class OriginalSaveCanonicalStoryTemp
    {
        public string Name { get; internal set; } = "";
        public int Low { get; internal set; }
        public int High { get; internal set; }
        public int Zone { get; internal set; } = -1;
        public bool Exclusive { get; internal set; }
        public string ZoneBorder { get; internal set; } = "";
        public int StoneIndex { get; internal set; } = -1;
        public string MinimumDistanceReference { get; internal set; } = "";
        public float MinimumDistance { get; internal set; }
        public float MiddleDistance { get; internal set; }
        public float NonRelevant { get; internal set; }
        public string DigConnect { get; internal set; } = "";
        public string Connect { get; internal set; } = "";
        public int CheckAlign { get; internal set; } = -1;
        public bool Placed { get; internal set; }
        public int PlacedX { get; internal set; }
        public int PlacedY { get; internal set; }
        public string[] EventNames { get; internal set; } = System.Array.Empty<string>();
        public string[] SeenChildren { get; internal set; } = System.Array.Empty<string>();
    }

    public sealed class OriginalSaveCanonicalPlayerState
    {
        public int LocalPlayer { get; internal set; }
        // Legacy archive-model name for the proven 32-word prodslot_override table.
        public int[] ReservedFraming { get; internal set; } = System.Array.Empty<int>();
        public int[] OwnerRace { get; internal set; } = System.Array.Empty<int>();
        public OriginalSaveCanonicalPlayerSlot[] Slots { get; internal set; } =
            System.Array.Empty<OriginalSaveCanonicalPlayerSlot>();
        public int[,] Diplomacy { get; internal set; } = new int[0, 0];
    }

    public sealed class OriginalSaveCanonicalPlayerSlot
    {
        public string PlayerName { get; internal set; } = "";
        public int SlotState { get; internal set; }
        public int PlayerOwnerId { get; internal set; }
        public int ClanOwnerId { get; internal set; }
        public string SecondaryName { get; internal set; } = "";
    }
}
