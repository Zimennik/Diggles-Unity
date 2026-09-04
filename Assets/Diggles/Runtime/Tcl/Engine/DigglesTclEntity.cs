#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using UnityEngine;

    // Tcl-side representation of an "object" — a `new ClassName...`
    // instance. Holds runtime state visible to engine commands.
    // Optionally bound to a Unity GameObject + DigglesObject when the
    // engine context has a spawn parent set.
    public sealed class DigglesTclEntity
    {
        public const int NativeEventRingCapacity = 64;

        private const int NativeEventRingMask = NativeEventRingCapacity - 1;
        private readonly OriginalSaveTimerEventPayloadDocument?[] _nativeEventRingSlots =
            new OriginalSaveTimerEventPayloadDocument?[NativeEventRingCapacity];

        public sealed class NativeProductionSlotState
        {
            public int RegistryValue1;
            public int RegistryValue2;
            public string ClassName = "";
            public string ResourceName = "";
            public int DemandBits;
            public byte Invented;
            public byte Buildable;
        }

        public int Handle;
        public DigglesClass Class = null!;
        public string InstanceName = "";
        // set_objinfo string (native obj+0x360 display/info text, e.g. zwerg.tcl:154
        // `set_objinfo. EinZwerg`) — shown by the original's info UI and serialized in saves.
        public string InfoString = "";

        // Native +0x200 mirror: the live state's per-class table index (CObj::HandleStates
        // 005cac90:81 commits target→live at think time). CurrentState is the target (+0x204)
        // name; this index trails it until the state goes live. -1 = no live state.
        public int LiveStateIndex = -1;
        // Raw archived +0x200/+0x204/+0x208 words from a staged save. While the state machine
        // is untouched after restore (StateWordsDirty false), canonical capture round-trips
        // these verbatim so registry gaps can never perturb corpus byte identity.
        // int.MinValue = the entity was not restored from an archive.
        public int RestoredStateLive = int.MinValue;
        public int RestoredStateTarget = int.MinValue;
        public int RestoredStateEnabled = int.MinValue;
        // Set by every state mutation (state_trigger/enable/disable/reset, think commit) —
        // switches capture from the restored-verbatim words to the live derivation.
        public bool StateWordsDirty;
        // Native object-body discriminator. Restored into the live entity so canonical capture
        // never has to consult the staged object document for structural type dispatch.
        public int NativeObjectType;

        // Native object flags the gender/clothing scripts query via obj_query -flagpos /
        // get_objgender / auto_choose_gender. Gender mirrors the engine's 0x3d0 male/female
        // bits ("male"/"female"/"neutral"); Owner = player index (0 = human player) for -owner.
        public string Gender = "neutral";
        // Ownerless by construction: the native CObj ctor writes owner = -1
        // (FUN_005c83f0:196); ownership exists only where a script/set_owner assigns it.
        // The clan-flag producer only emits a flag for owner >= 0 (FUN_005edea0:92-104).
        public int Owner = -1;

        // set_alternateanimdb flag (engine bit 0x2000): a female dwarf sets it so its model
        // resolves from the alternate db (frau) instead of the primary (mann). Read by
        // get_alternateanimdb (sequencer uses it to detect female actors).
        public bool AlternateAnimDb;

        // Diggles native coords: X horizontal, Y depth, Z height.
        // Stored unmodified — same convention as TCL spawn args.
        public Vector3 Position;
        // The native position captured once at spawn (Position drifts as the entity moves; the sim
        // syncs it from the transform each tick). Anchors wander actions (fly) to the spawn spot
        // so butterflies flutter near where they started instead of random-walking away.
        public Vector3 SpawnPosition;
        public bool HasSpawnPosition;
        public Vector3 Rotation;       // Euler in radians (Diggles convention)
        public Vector3 Velocity;
        internal Vector3 NativePreviousPosition;
        internal Vector3 NativeMotionDelta;
        internal float NativeMotionSpeed;
        internal Vector3 NativePreviousRotation;
        internal bool NativeCoreStateInitialized;
        internal bool NativeInitialAnimationWasSeeded;
        internal bool NativeInitialAnimationUsesRawIdentity;
        internal string? NativeInitialAnimation;
        internal Diggles.Runtime.DigglesAnimClip? NativeInitialAnimationClip;
        internal int NativeInitialAnimationModelDatabaseSlot = -1;
        internal int NativeInitialAnimationState = -1;
        internal int NativeInitialAnimationStartFrame;
        internal int NativeInitialAnimationMode;
        // Set by the sim when an ActiveAction ticks (it drives the transform, not Position directly);
        // the position-sync phase reads Position back from the transform only for these, so idle
        // entities skip the per-step transform read + InverseTransformPoint. set_pos and the set_vel
        // integrator write Position directly, so they don't set this.
        // See DigglesGameSim.Step phases 2b/2c.
        public bool PositionDirty;

        public string? CurrentAnim;
        // The anim mode (DigglesAnimator.AnimMode as int) last set via set_anim; -1 = unset. Stored even
        // when the GameObject doesn't exist yet (obj_init runs before spawn) so the spawner can re-apply
        // the obj_init-chosen pose with the right loop/still mode.
        public int CurrentAnimMode = -1;
        public string? Action;

        // The engine action layer: at most one active action + its Tcl finish/break callbacks,
        // advanced by DigglesGameSim, plus the task list the state body pops commands from
        // (tasklist_*).
        // The action carrying AttackInProgress. Natively +0x2b5 lives exactly as long as
        // CAct_Attack, so the flag can never outlive the action slot: it is registered by
        // FightResolver.StartAnimation and any slot transition away from the carrier clears it.
        // The Tcl finish/break clear-string is the scripted half of the same law.
        public object? AttackCarrier;

        IEntityAction? _activeAction;
        public IEntityAction? ActiveAction
        {
            get => _activeAction;
            set
            {
                if (!ReferenceEquals(_activeAction, value)
                    && AttackCarrier != null
                    && ReferenceEquals(_activeAction, AttackCarrier))
                {
                    AttackInProgress = false;
                    AttackCarrier = null;
                }
                _activeAction = value;
            }
        }
        public string? ActionFinish;
        public string? ActionBreak;
        public List<string> TaskList = new();
        // Archived N08 residue: the native task strings restored from a save. Not installed
        // into the live queue (the port's think loops on them); capture emits the residue
        // verbatim until the live queue mutates (TaskListDirty).
        internal string[]? RestoredTaskListResidue;
        public bool TaskListDirty;
        public bool ProcessActions = true;   // get_/set_processactions

        // Per-object light (native CObj+0x280..+0x2A0; Tcl object-form
        // `change_light OBJ {ox oy oz} RANGE {r g b}` — FUN_00673F40): offset, range, and the
        // color triple the archive stores as N22 words 5..9 + N23 (color × 20; the universal
        // corpus default [20,18,10] = {1, 0.9, 0.5} — every begining variant is a clean ×20
        // multiple: [30,16,0]={1.5,0.8,0}, [7,13,19]={0.35,0.65,0.95}, [18,18,15]={0.9,0.9,0.75}).
        public Vector3 LightOffset = new Vector3(0f, -0.6f, -2f);
        public float LightRange = 2f;
        public Vector3 LightColor = new Vector3(1f, 0.9f, 0.5f);

        // Per-instance variable storage. Wrapped as a TclScope so method
        // invocations can `upvar`-style alias member names into the local
        // method scope and have writes round-trip back to the entity.
        public TclScope VarsScope = new TclScope();

        // Per-entity stat values (Hitpoints/Nutrition/skills/…). Typed store,
        // separate from the string var-dict; read/written via the StatService on
        // the context (def_attrib registry → clamp + lazy spawn defaults).
        public Diggles.Runtime.Stats.StatValues Stats = new();

        // State machine (states / state_enter / state_leave). Diggles uses
        // an enable refcount so multiple subsystems can hold a state.
        public string? CurrentState;
        public int StateEnabledCount;
        // Did CurrentState ever go live (its think tick ran)? Mirrors the native live-vs-target
        // split: StateTrigger (FUN_0069ec80:109) only writes the target index obj+0x204; leave/
        // enter run later in CObj::HandleStates (005cac90:30-55/:56-80) comparing the live index
        // +0x200, which advances only at think (:81). Two state_triggers inside one event handler
        // (evt_task_harvest_proc z_events.tcl:922→:933) therefore coalesce natively — the
        // transient state's leave (state_leave task = unlock_item, zwerg.tcl:338-342) never runs.
        // CmdStateTrigger skips the leave while this is false; TickEntityState sets it.
        public bool StateWentLive;

        // park_obj_actions: a cutscene freezes ambient objects of given classes so
        // their AI doesn't fight the sequence. While parked, the sim skips state-thinking
        // (autonomous transitions) but still ticks direct actions (eaction anims).
        // restore_obj_actions clears it; remove_parked_obj clears it for one object
        // (preset actors are un-parked, then frozen instead via state_disable).
        public bool Parked;

        // Native obj+0x3d0 bit 0x200000 — set/cleared by set_sequenceactive
        // (TCL:CTclObjMethods::SetSequenceActive, FUN_006816a0:74-88). The per-object exemption
        // from the global cutscene freeze: while sequence mode is on (set_sequence 1,
        // FUN_00649080 → DAT_00772400+0x8c), CGameThread::SrvDoThink drains an object's events
        // and thinks its state only when `!seqMode || (flags & 0x200000)`
        // (FUN_005d38c0:138 +:185-187). Ambient life sets it 1 at init so fish/butterflies keep
        // moving through cutscenes (Fisch.tcl:15, hamster.tcl:95, Schmetterling.tcl:15,
        // raupe.tcl:38, fee.tcl:4); the sequencer sets it on itself (init_sequencer,
        // sequencer.tcl:530) so eval_sequence keeps ticking. Everything else — including objects
        // summoned mid-sequence via `sq_object summon` — freezes: a summoned troll's evt_timer0
        // (troll.tcl:98 → troll_init/state_enable) stays queued, so its wander AI
        // (state idle → walk_random, troll.tcl:221-266) can never fight the script.
        // Defaults clear: the CObj ctor flag word is 0x01100101 (FUN_005c83f0:224) — bit 21 unset.
        public bool SequenceActiveFlag;

        // Inbox of matured timer events + script-raised events waiting
        // to fire their handle_event handlers on the next sim tick.
        // Drained by DigglesGameSim.Step.
        public readonly Queue<EntityEvent> PendingEvents = new();
        public int NativeEventRingReadCursor { get; private set; }
        public int NativeEventRingWriteCursor { get; private set; }
        public long NativeEventRingRevision { get; private set; }
        public IReadOnlyList<OriginalSaveTimerEventPayloadDocument?> NativeEventRingSlots =>
            _nativeEventRingSlots;

        public void EnqueueEvent(EntityEvent pendingEvent, double gameTime)
        {
            OriginalSaveTimerEventPayloadDocument nativePayload =
                OriginalSaveTimerEventPayloadDocument.CreateRuntimePayload(
                    pendingEvent.Name,
                    pendingEvent.Payload,
                    Handle,
                    gameTime);
            PendingEvents.Enqueue(pendingEvent);
            _nativeEventRingSlots[NativeEventRingWriteCursor] = nativePayload;
            NativeEventRingWriteCursor =
                (NativeEventRingWriteCursor + 1) & NativeEventRingMask;
            NativeEventRingRevision++;
        }

        public EntityEvent DequeueEvent()
        {
            EntityEvent pendingEvent = PendingEvents.Dequeue();
            NativeEventRingReadCursor =
                (NativeEventRingReadCursor + 1) & NativeEventRingMask;
            NativeEventRingRevision++;
            return pendingEvent;
        }

        internal void EnqueueRestoredEvent(EntityEvent pendingEvent) =>
            PendingEvents.Enqueue(pendingEvent);

        internal void RestoreNativeEventRing(
            int readCursor,
            int writeCursor,
            IReadOnlyList<OriginalSaveTimerEventPayloadDocument?> slots)
        {
            if ((uint)readCursor >= NativeEventRingCapacity)
                throw new ArgumentOutOfRangeException(nameof(readCursor));
            if ((uint)writeCursor >= NativeEventRingCapacity)
                throw new ArgumentOutOfRangeException(nameof(writeCursor));
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (slots.Count != NativeEventRingCapacity)
            {
                throw new ArgumentException(
                    "A native entity event ring must contain exactly 64 slots.",
                    nameof(slots));
            }

            for (int slot = 0; slot < _nativeEventRingSlots.Length; slot++)
                _nativeEventRingSlots[slot] = slots[slot];
            NativeEventRingReadCursor = readCursor;
            NativeEventRingWriteCursor = writeCursor;
            NativeEventRingRevision = 0;
        }

        // Inventory / containment (inv_*). `Inventory` holds the handles of entities packed
        // into this object (a dwarf's basket, a barrel's contents, a building's input store).
        // `ContainedIn` = the handle of the entity whose inventory holds this one (-1 = free in
        // the world). A contained entity is hidden + excluded from world obj_query. `InStore` is
        // the set_instore flag (item parked in a Lager). Capacity gates inv_check/inv_add.
        public List<int> Inventory = new();
        public int ContainedIn = -1;
        public int InventoryCapacity = 99;
        public bool InStore;

        // Native fight state stored directly on CObj. Weapon/shield values are indices into
        // FightSystem.Weapons (zero is the registered unarmed definition in shipped fight.tcl).
        public int WeaponClassId;
        public int ShieldClassId;
        // Native obj+0x2AC pick/target mode is produced by set_collision (FUN_005cd750): 0 when
        // collision is off (and for "baum" classes), 1 for ordinary collidable objects, 2 for
        // gnomes/babies. The same call owns object-flag bit 0x80000.
        public int PickTargetMode;
        public bool AttackInProgress;
        public int BallisticResult;
        public bool DiedInFight;
        public bool ClimbAbility = true;
        // Native obj+0x408 — the object's live news-ticker entry id (the "under attack" news).
        // -1 = none; ntid_reset (CTclObjMethods::NTIDReset, FUN_0067c140:57-89) deletes the
        // entry when the id is >= 0 and then stamps -2.
        public int NewsTickerId = -1;

        // link_obj state — the entity-side mirror of the native child+0x278 (linked-to object
        // id; 0 = none here, native uses -1) and +0x27c (link slot: head=4, back=5, hand=0).
        // Written by CmdLinkObj so headless law tests and get_linked_to see links even when
        // no GameObjects exist; the DigglesLinkAttachment component stays the Play-side driver.
        public int LinkedToHandle;
        public int LinkedSlot = -1;

        // obj+0x224 — set_placesnapmode/get_placesnapmode (FUN_0067d8b0/FUN_0067dac0).
        // 0 = normal ground placement, 1 = wall-mounted placement, 2 = ceiling-constrained
        // placement. The placement singleton maps these to native tick modes 2/8/9.
        public int PlaceSnapMode;
        public bool SnapToWall;
        public bool ForcePositionInterpolation;
        public PathInfluenceState? PathInfluence;
        public TransportLogicState? TransportLogic;

        // Fog-of-war reveal size (native obj+0x364/+0x368, fine units = tiles*8; set_fogofwar
        // FUN_0068a980 stores arg*8; exactly -1 = no reveal; negative values select the
        // no-collision box variant — FogRemover's `-50 -50`). FowLast* = the last processed
        // position (obj+0x26c/+0x270/+0x274) for CFogOfWar::UpdateFoW.
        // FowCache = the per-player GetMaxFow cache (obj+0x6c), valid while FowCacheVersion
        // matches FogOfWarState.CacheVersion (native: flushed to -1 every UpdateFoW).
        public int FowSizeX = -1, FowSizeY = -1;
        public float FowLastX, FowLastY, FowLastZ; // zero-filled like the native ctor
        public int[]? FowCache;
        public int FowCacheVersion = -1;

        // CObj construction timestamp at obj+0x350, in native clock-hours (game seconds / 150).
        // It is immutable after CmdNew; work-window start/length occupy the following two words.
        public float CreationTimeHours;

        // CObj+0x3d8 is a cumulative mutation-channel mask, not a render/model flag:
        // bit 0 inventory topology, bit 1 transform, bit 2 gender, bit 3 animation assignment.
        // Native construction invokes SetPosition + SetAnim, so every live object starts at 10.
        public const int NativeMutationInventory = 1;
        public const int NativeMutationTransform = 2;
        public const int NativeMutationGender = 4;
        public const int NativeMutationAnimation = 8;
        public int NativeMutationFlags = NativeMutationTransform | NativeMutationAnimation;

        public void MarkNativeMutation(int mask) => NativeMutationFlags |= mask;
        // Model half-extents in native tiles (the native link box obj+0x39c/+0x3a0 that
        // FUN_005cb250 expands the GetMaxFow rect by). Computed once from the renderer-bounds
        // union on the first GetMaxFow; -1 = not yet computed (retry while no renderers).
        // A cave-spanning vine needs its true span here; a pivot-only rect would keep it hidden
        // until a gnome reached its middle.
        public float FowExtX = -1f, FowExtY = -1f;
        // Set by the fog render pass when the entity is hidden by fog (GetMaxFow(local) < 0x80
        // and not viewinfog); composed with the script _visibility flag by ApplyRenderVisible.
        public bool FowHidden;

        // Native render-dirty latch obj+0x3B4 (the N35 save byte): stamped 1 by every real
        // set_visibility flip / model destruct (FUN_0067cca0:79-94, FUN_0067bcf0:55), never
        // cleared. Restored from the archive; captured verbatim into fresh saves.
        public bool RenderDirtyLatch;

        // Spawned by the during-dig template-deco pass (MapDecoBeautifier via
        // DigglesTclEngine.MapDecoSpawn — tunnel beams/supports/treasure). Marks the entity for
        // the deco removal laws: the cave-skin completion purge (CaveSkinDigCheck) and the
        // per-carve sweep (MapDecoCarveSweep) — the port analog of the native MapValidator
        // DUMMYDES pass (FUN_005a46d0 label 0x10 → FUN_005eb7b0), which destroys objtype-dummy
        // objects whose anim bbox overlaps a carve dirty rect (FUN_005eae80 deferred destroy).
        public bool MapDecoSpawned;

        // Registered from a save's marker-7 spatial-index record (dormant scenery seed) rather
        // than from a live dig (DigglesTclEngine.RegisterDecoSeed, B3 §3). Seeds are not registered
        // in ctx.Entities — they are presentation props tracked in ctx.MapDecoSeeds — so the
        // save-capture object graph stays byte-identical to the archive (the retained marker-7 bytes
        // round-trip verbatim; the native itself never re-serializes these as CObj bodies). The
        // per-carve sweep owns them via ctx.MapDecoSeeds, and this flag also carries MapDecoSpawned
        // so any shared deco predicate treats them alike.
        public bool MapDecoSaveSeed;

        // One-shot native seed conversion: a class/model that cannot materialize must not be
        // retried every LOD scan forever. A successful conversion is also represented by GameObject.
        public bool MapDecoSeedMaterializationAttempted;

        // The archived marker-7 record this seed was registered from, kept whole so the save
        // writer can re-emit the spatial index (minus carved seeds) when the object graph changed
        // and the retained bytes cannot be reused. Seeds have no other producer: the native
        // re-materializer (FUN_005cd1d0:96-120) only converts live display instances back into
        // records, so a record dropped from a save is deco lost for good.
        public Diggles.Runtime.Save.OriginalSaveSpatialSeed? MapDecoSeedRecord;

        // Deferred-destroy guard for deco removal — the native obj+0x404 flag (FUN_005eae80
        // sets it before queueing the object on the frame's destroy list so a second sweep
        // never double-queues). Set by DigglesTclEngine.MapDecoRetire when the crumble/retreat
        // presentation starts or the instant delete runs (headless); every deco sweep
        // skips entities already retiring.
        public bool MapDecoRetiring;

        // The native deferred-delete guard at obj+0x404. An obj_exit block can call back into
        // deletion helpers, so the port keeps the object resolvable while preventing re-entry.
        public bool DeleteInProgress;

        // gnome+0x250 — the current dig-order id: the tile+0x0c nibble cached at carve time
        // (FUN_00596b80:232). dig_next adds +8 cost to voxels whose per-owner id differs
        // (FUN_00597b20:144) and its matchId arg hard-rejects them, so a dwarf finishes its current
        // vein before switching batches; dig_resetid clears the filter (-1 = none yet).
        public int DigSetId = -1;

        // Production-building state. ProdSlots[itemClass] = scheduled count (set_prod_slot_cnt);
        // BuildupComplete mirrors the native "built" flag, obj+0x3d0 bit 0x400000 (get_buildupstate
        // FUN_0067e410:57 reads it, set_buildupstate FUN_0067e1e0:76-85 writes it). Defaults to
        // unbuilt: the CObj ctor flag word is 0x01100101 (FUN_005c83f0:224) — bit 0x400000 clear;
        // every real building flips it to built in its genericprod object-init
        // (`set_buildupstate this 1`, genericprod.tcl:451), and the construction chain then drives
        // it through set_buildupanim (genericprod.tcl:812-813:
        // built ⇔ buildup_step > max_buildup_step).
        public System.Collections.Generic.Dictionary<string, double> ProdSlots = new();
        public bool BuildupComplete;
        // Native production/energy record fields. Energy sources use range/store/max-store and
        // consumers use consumption; both use EnergyClass. Source classes may power the same or a
        // lower consumer class. Active loads live in the context-owned EnergySystem.
        public int EnergyClass;
        public float EnergyConsumption;
        public float EnergyRange;
        public float EnergyStore;
        public float EnergyMaxStore;
        // The planner's per-prodplace "construction requested" byte (planner record obj+0x3e0,
        // byte +0x2a): written by set_prod_buildup (FUN_006937e0:86), read by get_prod_buildup
        // (FUN_00693610:59), auto-armed by the prodplace idle tick from a pending placement order
        // (FUN_005edea0:136), turned into a kind-7 buildup job by the planner's job collector
        // (FUN_005f0850:22-54) and cleared when the job is dispatched (FUN_005f3a50:134-138).
        public bool BuildupRequested;
        // The sibling "pack requested" byte (+0x29): set_prod_pack (FUN_006933b0:86) /
        // get_prod_pack (FUN_006931e0:59), armed by the building window's pack button
        // (FUN_005242b0:304-330). Collected as the kind-0 pack job (FUN_005f0850:56-89), cleared
        // at dispatch (one-shot, FUN_005f3a50:65-69); prod_builddown gates on it mid-chain
        // (z_work_common.tcl:444), so clearing it cancels a pack in flight.
        public bool PackRequested;
        // The "unpack requested" byte (+0x28): "go place this box at OrderPos". Written by the
        // placement confirm's deferred-order branch (FUN_00531310:316-331 via FUN_005ee250) and
        // by set_prod_unpack (FUN_00692f80:86 — a BOOL, Tcl_GetBooleanFromObj; reader
        // get_prod_unpack FUN_00692db0:59); stop_prod cancels a yanked carrier's order with
        // `set_prod_unpack $item 0` (z_procs.tcl:1240). Collected as the kind-1 unpack job
        // (FUN_005f0850:90-126), cleared at dispatch (one-shot, FUN_005f3a50:79-84).
        public bool UnpackRequested;
        // The stored order position (rec+0x30..0x38) + the "asbox" byte (+0x2d): where the
        // deferred unpack order places the crate, and whether as a bare box (putdown) or as
        // production (build up). Written together with UnpackRequested (FUN_005ee250:10-16 /
        // FUN_00531310:325-331); the unpack job carries them to the emit as -pos1 / -num1
        // (FUN_005f0850:118-125, FUN_005f3a50:87-89).
        public Vector3 OrderPos;
        public bool OrderAsBox;
        // rec+0x3c — the deferred move flag ("pack, then re-place at OrderPos"): armed by the
        // ALT-variant of the window's pack button (FUN_005242b0:314/:324-329 — ALT is the options
        // key byte FUN_004ecd40+0x11a = GetAsyncKeyState(VK_MENU), FUN_004ed0b0:74; the button
        // relabels to icons/unpack + "Umzug", FUN_00521c80:692-731) together with the placement
        // confirm's order write. The prodplace idle tick consumes it once the building is boxed
        // and arms the unpack order at the stored pos (FUN_005edea0:122-138); the port applies
        // that conversion in set_boxed's boxing side effects.
        public bool MoveAfterPack;
        // rec+0x3e — "order ghost outstanding": set by the deferred-order write (+0x3d word =
        // 0x100, FUN_005ee250:12 / FUN_00531310:331) and re-set by the buildup dispatch
        // (FUN_005f3a50:139); cleared by hide_obj_ghost (FUN_0069b440:58 → FUN_0046a960) and by
        // the window's order buttons (+0x3d word = 0, FUN_005242b0:316/:344). The building
        // window reads it for the S1 "order pending" icon variants (FUN_00521c80:504-518).
        public bool OrderGhostShown;
        // The conquer/repair "rope-pull" owner-strength — the prod record float at rec+0x68:
        // SetProdOwnerStrength (FUN_00693c20:87) writes it raw (no clamp — the Tcl caps at 1.0
        // itself, z_work_common.tcl:645), GetProdOwnerStrength (FUN_00693a40) reads it, and the
        // record ctor defaults it to 1.0f (FUN_005c9fa0:219 — rec[0x1a] = 0x3f800000). The Tcl
        // branches on it: prod_autorepair `< 0.95` → conquer else prod_repair
        // (z_work_common.tcl:313-317); conquer_callback pulls it toward 0 (foreign worker) or
        // back to 1 (own worker) by ±offset per rope-pull anim (:613-657).
        public float ProdOwnerStrength = 1f;

        // C# mirror of the building's current construction stage (genericprod's per-object
        // buildup_step var): index of the bau_* stage mesh last shown (0=bau_a … 9=bau_k, j skipped;
        // genericprod.tcl:787-799), -1 = no stage shown yet. Maintained by the set_anim stage hook
        // and informational only — the Tcl var stays authoritative.
        public int BuildupStep = -1;
        // The planner record's current-gnome ref (+0x24): which gnome is working/constructing this
        // prodplace. The prodplace-idle gate tests it before re-arming a buildup request
        // (FUN_005edea0:129) and the job-release path clears it (FUN_0043a0c0:31). -1 = unclaimed.
        public int CurrentWorkerHandle = -1;
        public bool ProdScheduled;
        // ---- production progress surface (info_start_prod / info_progress_prod / info_end_prod —
        // the native per-object icon progress word) ----
        // CurrentProdItem = the item class this building is producing right now ("" = idle):
        // written by info_start_prod (genericprod.tcl:714/:733, always right before the first
        // info_progress_prod), cleared by info_end_prod. ProdProgressCur/Max = the last
        // info_progress_prod <cur> <max> pair (genericprod.tcl prod_progress:362 — the work loop
        // bumps it once per completed task step; prod_progressjump:366-369 rescales both).
        // ProdProgressPct derives 0..100 for the UI (BottomMenuController.BuildProduction).
        public string CurrentProdItem = "";
        public int ProdProgressCur, ProdProgressMax;
        public int ProdProgressPct => ProdProgressMax > 0
            ? UnityEngine.Mathf.Clamp(ProdProgressCur * 100 / ProdProgressMax, 0, 100) : 0;

        // ---- Work-dispatch state (the native CProdPlanner port) ----
        // WalkResult mirrors the engine's obj+0x248 pathfind-result enum, set by the move executor
        // (WalkResultAction): 0=none, 1=walking, 2=blocked/failed, 4=arrived. The Tcl work loop
        // tests `4 != [get_walkresult this]` (walkfail, z_dignwalk.tcl). See get_walkresult.
        // Port-internal virgin = 0 (the sparetime pipeline keys its first-walk flow on it);
        // the native virgin is 1 (begining corpus 327/335) — canonical capture translates
        // 0 → 1 for fresh entities so archives carry the native value.
        public int WalkResult;
        // gnome+0x3e8 busy/cooldown: set to AssignCooldown on assign, decremented per Step. While >0 the
        // gnome is not re-dispatched. 0 = available to the idle pool.
        public int WorkBusyCooldown;
        // Worktime window in hour-units [WorkStart, WorkStart+WorkLen) (set_worktime /
        // auto_choose_workingtime); get_remaining_sparetime compares the day phase to it to decide
        // work vs sparetime. The faithful default is no window: the CObj ctor zeroes both fields
        // (FUN_005c83f0:130-132 — param_1[0xd5]=0 → start +0x354, param_1[0xd6]=0 → duration
        // +0x358) and the per-tick window test requires duration > 0 (FUN_005cb760:35
        // `if (0 < dur)`), so a fresh object is never "at work" until a real window is assigned
        // (zwerg.tcl:147 obj_init auto_choose_workingtime; the founders' couple override
        // z_methods.tcl:268-278 wt 0.0/6.0; baby.tcl:422-424). A non-zero default would also
        // pollute auto_choose_workingtime's coverage histogram (the first dwarf must see
        // total==0 → window [0,6), FUN_00676b50:164-167).
        public float WorkStart = 0f, WorkLen = 0f;
        // The native auto-schedule flag, obj+0x3d0 bit 0x800000 inverted (bit set = manual).
        // CObj ctor inits the flag word to 0x01100101 (FUN_005c83f0:224) — bit 0x800000 clear —
        // so auto-schedule defaults ON. Written by set_prodautoschedule (FUN_00680e20: arg true →
        // clear bit, false/unparsable → set bit), read by get_prodautoschedule (FUN_00681050:57
        // returns (flags & 0x800000) == 0) and the WorkClock AutoProd toggle. The dispatcher only
        // auto-assigns work while this is true (CProdPlanner_MaintenanceTick FUN_00433a70:112).
        public bool ProdAutoSchedule = true;
        // The planner record's preferred workplace handle (native planner sub+0x40; -1 = none).
        // Distinct from the current workplace (sub+0x3C, derivable from the in-flight plan): this is
        // the sticky job preference. FUN_00438210 applies the production/invention scoring law and
        // FUN_00433710 answers "is this building claimed". The fourth planner-extension save word
        // persists this field verbatim; stale ids are retained and consumers resolve them through
        // the live registry (B2 §4, 51 dangling in the corpus).
        public int PreferredWorkplaceHandle = -1;

        // Compatibility name used by the established native planner-slot restore seam.
        public int AssignedWorkplaceId
        {
            get => PreferredWorkplaceHandle;
            set => PreferredWorkplaceHandle = value;
        }

        // talkissue ledger (native per-gnome store at obj+0x3f4, FUN_006aa650 = TCL:
        // CTclGlobalMethods::Talkissue): issue name → weight. Lazily allocated on first use.
        // Values clamp to [0, 50] (DAT_007375cc); a negative `add` that lands below 3.0
        // (DAT_00737428) deletes the entry (FUN_005ff2c0:51-67).
        public Dictionary<string, double>? TalkIssues;
        // The ledger's running talk-desire sum (native sub+0x18, FUN_005ff730:33). This is not
        // Σ(weights): the native `reduce` subtracts a spoken issue's value from the sum without
        // deleting the entry (FUN_006aa650:336-356), so the stored sum drifts below Σweights over
        // a gnome's conversations — it differs from Σweights in 1,732/2,581 corpus gnomes (B2 §3).
        // It is therefore persisted verbatim across save/load rather than recomputed. The live
        // `talkissue sum` command still returns Σ (unchanged); this field carries the saved running
        // sum so a round-trip reproduces it faithfully.
        public double TalkIssuesSum;

        // Save-resident family tri-state. Tcl variables expose live linked ids, while these
        // fields retain the native 0/1/2 state and reference word so a later save can represent
        // deaths and dangling family links without consulting the staged document.
        public bool HasFamilyState;
        public int FamilyPartnerId, FamilyPartnerState, FamilyPartnerReferenceObjectId = -1;
        public int FamilyMotherId, FamilyMotherState, FamilyMotherReferenceObjectId = -1;
        public int FamilyFatherId, FamilyFatherState, FamilyFatherReferenceObjectId = -1;
        public List<string> FamilyChildrenNames = new();
        public bool FamilyPregnancyFlag;

        // Native planner four-word continuation state. Pool membership is live in ProdDispatcher;
        // the timestamp and current-workplace identity are explicit here for canonical capture.
        public int PlannerIdleAnnounceFlag;
        public float PlannerIdleSince;
        public int PlannerCurrentWorkplaceId = -1;
        // prod_gnome_state (FUN_006a7230) — the engine's per-gnome work-disposition string written
        // by the Tcl every tick ("idle"/"sparetime"/"dig"/"walk"/...). Stored faithfully; "idle"
        // additionally feeds the dispatcher pool.
        public string ProdState = "";

        // Leisure registry kind (sparetime announce). Native category order is
        // sleep=0, eat=1, bath=2, fun=3, home=4, sex=5, ill=6 (FUN_005f4ee0); "" = not a leisure place.
        public string SpareKind = "";
        // Guest-seat link, occupancy, and per-seat order weight for guest-production buildings
        // (prod_guest: school seats, hospital patient queue). The native CProd table always has
        // sixteen records. A link id of -1 and a guest handle of 0 identify an unused seat.
        public const int GuestSeatCount = 16;
        public int[]? GuestLinks;
        public int[]? Guests;
        public double[]? GuestOrders;

        // CProd compatibility values that have no script-facing mutation seam. Counts, requests,
        // progress, energy, and guests stay in the live fields above; this store carries only the
        // opaque slot metadata/flow bytes and the proven +0x2D residue.
        public List<NativeProductionSlotState>? NativeProductionSlots;
        public byte NativeProductionUnpackFlag;
        public byte NativeProductionPackFlag;
        public byte NativeProductionResidue2D;
        public byte[] NativeProductionFlowBytes = { 0, 0, 0, 0, 0, 1, 1, 1 };
        public bool ProdSwitchMode
        {
            get => NativeProductionFlowBytes.Length == 8 && NativeProductionFlowBytes[2] != 0;
            set
            {
                EnsureProductionFlowBytes();
                NativeProductionFlowBytes[2] = value ? (byte)1 : (byte)0;
            }
        }
        public bool ProdExclusiveMode
        {
            get => NativeProductionFlowBytes.Length == 8 && NativeProductionFlowBytes[4] != 0;
            set
            {
                EnsureProductionFlowBytes();
                NativeProductionFlowBytes[4] = value ? (byte)1 : (byte)0;
            }
        }
        public int NativeProductionCurrentProductStamp = -1;
        public int NativeProductionInventionCandidateSlot = -1;
        public float EnergyCurrentLoad;
        public byte NativeProductionState3C;
        public byte NativeProductionState140;

        private void EnsureProductionFlowBytes()
        {
            if (NativeProductionFlowBytes.Length != 8)
                NativeProductionFlowBytes = new byte[8];
        }

        internal void InitializeNativeCoreState()
        {
            NativePreviousPosition = Position;
            // CObj's constructor writes plain zero here (FUN_005C83F0:200-215). A stationary
            // CObj_Tick subsequently produces -0 under the native round-down subtraction.
            NativeMotionDelta = Vector3.zero;
            NativeMotionSpeed = 0f;
            NativePreviousRotation = Rotation;
            NativeCoreStateInitialized = true;
        }

        internal void CaptureNativeCoreState()
        {
            if (!NativeCoreStateInitialized)
            {
                InitializeNativeCoreState();
                return;
            }

            NativeMotionDelta = new Vector3(
                NativeSubtract(Position.x, NativePreviousPosition.x),
                NativeSubtract(Position.y, NativePreviousPosition.y),
                NativeSubtract(Position.z, NativePreviousPosition.z));
            NativeMotionSpeed = NativeMagnitude(NativeMotionDelta);
            NativePreviousPosition = Position;
            NativePreviousRotation = Rotation;
        }

        internal static float NativeMagnitude(Vector3 value)
        {
            float x2 = RoundDown((double)value.x * value.x);
            float y2 = RoundDown((double)value.y * value.y);
            float z2 = RoundDown((double)value.z * value.z);
            float sum = RoundDown((double)x2 + y2);
            sum = RoundDown((double)sum + z2);
            return RoundDown(Math.Sqrt(sum));
        }

        private static float NativeSubtract(float left, float right)
        {
            double result = (double)left - right;
            return result == 0d
                ? BitConverter.Int32BitsToSingle(int.MinValue)
                : RoundDown(result);
        }

        private static float RoundDown(double value)
        {
            float rounded = (float)value;
            if (float.IsNaN(rounded) || float.IsNegativeInfinity(rounded) ||
                (double)rounded <= value)
            {
                return rounded;
            }

            int bits = BitConverter.SingleToInt32Bits(rounded);
            return BitConverter.Int32BitsToSingle(rounded > 0f ? bits - 1 : bits + 1);
        }

        // Unity binding (optional — set if engine context spawns real GOs)
        public GameObject? GameObject;
    }
}
