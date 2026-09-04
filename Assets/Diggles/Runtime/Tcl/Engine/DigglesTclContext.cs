#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using UnityEngine;

    // Bridge: when Tcl spawns / mutates entities, the engine optionally
    // calls into a host-provided spawner that owns the Unity-scene side
    // (build GameObject pair, mirror position/rotation/anim writes, etc).
    // The Editor assembly implements this via MarkerModelResolver; the
    // bare runtime can be used without it (tests, headless eval).
    public interface IDigglesEntitySpawner
    {
        // Returns the spawned outer GameObject (or null if class is
        // invisible / unresolvable). Spawner is responsible for the full
        // outer/inner GO pair, axis conversion, attaching DigglesObject,
        // and parenting under `parent`.
        GameObject? Spawn(DigglesTclEntity ent, Transform parent);

        void Destroy(GameObject go);

        // Reflect Tcl-side mutators to the GameObject. Native = Diggles
        // frame; spawner applies the same axis conversion it used at spawn.
        void ApplyPosition(GameObject go, Vector3 nativePos);
        void ApplyRotation(GameObject go, Vector3 nativeRotRadians);
        void ApplyAnim(GameObject go, string animName, int startFrame,
            DigglesAnimator.AnimMode mode);
    }

    public interface IGameSaveBackend
    {
        string Save(string name);
        string Load(string name);
    }

    public enum SparetimeCategory
    {
        Sleep = 0,
        Eat = 1,
        Bath = 2,
        Fun = 3,
        Home = 4,
        Sex = 5,
        Ill = 6,
    }

    public readonly struct SparetimeRegistration
    {
        public SparetimeRegistration(SparetimeCategory category, int objectId)
        {
            Category = category;
            ObjectId = objectId;
        }

        public SparetimeCategory Category { get; }
        public int ObjectId { get; }
    }

    public static class SparetimeCategoryCodec
    {
        public static bool TryParse(string value, out SparetimeCategory category)
        {
            switch (value.ToLowerInvariant())
            {
                case "sleep": category = SparetimeCategory.Sleep; return true;
                case "eat": category = SparetimeCategory.Eat; return true;
                case "bath": category = SparetimeCategory.Bath; return true;
                case "fun": category = SparetimeCategory.Fun; return true;
                case "home": category = SparetimeCategory.Home; return true;
                case "sex": category = SparetimeCategory.Sex; return true;
                case "ill": category = SparetimeCategory.Ill; return true;
                default: category = default; return false;
            }
        }

        public static string GetName(SparetimeCategory category)
        {
            return category switch
            {
                SparetimeCategory.Sleep => "sleep",
                SparetimeCategory.Eat => "eat",
                SparetimeCategory.Bath => "bath",
                SparetimeCategory.Fun => "fun",
                SparetimeCategory.Home => "home",
                SparetimeCategory.Sex => "sex",
                SparetimeCategory.Ill => "ill",
                _ => "",
            };
        }
    }

    public readonly struct HorizonRegionState
    {
        public HorizonRegionState(
            Vector3 position,
            int red,
            int green,
            int blue,
            float depth,
            float size)
        {
            Position = position;
            Red = red;
            Green = green;
            Blue = blue;
            Depth = depth;
            Size = size;
        }

        public Vector3 Position { get; }
        public int Red { get; }
        public int Green { get; }
        public int Blue { get; }
        public float Depth { get; }
        public float Size { get; }
    }

    // Engine-side registry of scheduled timer_event entries, mirroring the native
    // CTimerEvtMgr list as far as `timer_unset` needs it. The original timer entry
    // (FUN_00605f40, 0x28 bytes) stores the userid at +0x00 and the owner at +0x1c;
    // timer_unset (FUN_0069fc90) parses its 2nd arg as an int (parse failure → 0) and
    // removes every entry whose owner matches and whose userid equals the arg, with
    // -1 acting as a wildcard (FUN_00606110:24 `entry.userid == id || id == -1`).
    //
    // The port's TimerScheduler stores (handle, eventName) only, so this registry adds
    // the userid dimension: each scheduled timer carries a serial in its payload
    // (SerialKey); cancellation marks the serial dead and the sim's delivery hook
    // (DigglesGameSim.OnEventMatured → FilterDelivery) drops matured fires of dead
    // serials. Entries self-prune as their finite repeats are delivered.
    public sealed class TimerRegistry
    {
        public const string SerialKey = "__tserial";
        public const string UserIdKey = "-userid";

        public sealed class Entry
        {
            public long Serial;
            public int Handle;
            public int UserId;
            public string Event = "";
            public int RemainingFires;   // < 0 = infinite (-repeat -1)
            public bool Cancelled;
        }

        long _nextSerial = 1;
        readonly List<Entry> _entries = new();

        public int LiveCount
        {
            get { int n = 0; foreach (var e in _entries) if (!e.Cancelled) n++; return n; }
        }

        public Entry[] CaptureEntries()
        {
            var result = new Entry[_entries.Count];
            for (int i = 0; i < _entries.Count; i++)
            {
                var source = _entries[i];
                result[i] = new Entry
                {
                    Serial = source.Serial,
                    Handle = source.Handle,
                    UserId = source.UserId,
                    Event = source.Event,
                    RemainingFires = source.RemainingFires,
                    Cancelled = source.Cancelled,
                };
            }
            return result;
        }

        public void RestoreEntries(IEnumerable<Entry> entries)
        {
            ValidateEntries(entries);
            _entries.Clear();
            _nextSerial = 1;
            foreach (var source in entries)
            {
                var entry = new Entry
                {
                    Serial = source.Serial,
                    Handle = source.Handle,
                    UserId = source.UserId,
                    Event = source.Event,
                    RemainingFires = source.RemainingFires,
                    Cancelled = source.Cancelled,
                };
                _entries.Add(entry);
                if (entry.Serial >= _nextSerial) _nextSerial = entry.Serial + 1;
            }
        }

        public static void ValidateEntries(IEnumerable<Entry>? entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var serials = new HashSet<long>();
            foreach (Entry? entry in entries)
            {
                string? violation =
                    entry == null ? "null entry" :
                    entry.Serial <= 0 ? $"serial {entry.Serial}" :
                    !serials.Add(entry.Serial) ? $"duplicate serial {entry.Serial}" :
                    entry.Handle < 0 ? $"handle {entry.Handle}" :
                    string.IsNullOrEmpty(entry.Event) ? "empty event" :
                    entry.Event.Length > 31 ? $"event length {entry.Event.Length}" :
                    entry.Event.IndexOf('\0') >= 0 ? "NUL in event" :
                    // RemainingFires == 0 is legal corpus state: the native removes exhausted
                    // timers lazily, so saves carry them (e.g. evt_pilz_aging, handle 351 in a
                    // real quicksave). They finish naturally on the first ordinary tick.
                    null;
                if (violation != null)
                {
                    throw new ArgumentException(
                        $"A timer registry entry contains invalid native state ({violation}; " +
                        $"event '{entry?.Event ?? "<null>"}', handle {entry?.Handle}, " +
                        $"serial {entry?.Serial}).",
                        nameof(entries));
                }
            }
        }

        public long Register(int handle, int userId, string evt, int repeats)
        {
            var e = new Entry
            {
                Serial = _nextSerial++,
                Handle = handle,
                UserId = userId,
                Event = evt,
                RemainingFires = repeats,
            };
            _entries.Add(e);
            return e.Serial;
        }

        // Cancel every registration on `handle` whose userid matches (`userId == -1` =
        // all, FUN_00606110:24). Calls `unsetByName(event)` for event names that have no
        // surviving registration on the handle — letting the caller physically remove
        // the scheduler entries in the unambiguous (overwhelmingly common) case; the
        // ambiguous ones stay scheduled but their fires are dropped at delivery.
        // Returns the number of registrations cancelled.
        public int Cancel(int handle, int userId, System.Action<string>? unsetByName)
        {
            var cancelledNames = new HashSet<string>();
            int n = 0;
            foreach (var e in _entries)
            {
                if (e.Cancelled || e.Handle != handle) continue;
                if (userId != -1 && e.UserId != userId) continue;
                e.Cancelled = true;
                cancelledNames.Add(e.Event);
                n++;
            }
            if (n == 0) return 0;
            foreach (var name in cancelledNames)
            {
                bool survivor = false;
                foreach (var e in _entries)
                    if (!e.Cancelled && e.Handle == handle && e.Event == name) { survivor = true; break; }
                if (!survivor)
                {
                    unsetByName?.Invoke(name);
                    _entries.RemoveAll(e => e.Cancelled && e.Handle == handle && e.Event == name);
                }
            }
            return n;
        }

        // Forget everything for a destroyed handle (del → Scheduler.UnsetAll already
        // removed the scheduler entries).
        public void DropHandle(int handle) => _entries.RemoveAll(e => e.Handle == handle);

        // Delivery hook: true ⇒ this matured event belongs to a cancelled timer and must
        // be dropped. Also decrements finite repeat counts so exhausted registrations
        // self-prune. Events without our serial (set_event / direct Schedule calls) pass.
        public bool FilterDelivery(Dictionary<string, string>? payload)
        {
            if (payload == null || !payload.TryGetValue(SerialKey, out var s)
                || !long.TryParse(s, out var serial)) return false;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Serial != serial) continue;
                bool drop = e.Cancelled;
                // Count the fire either way so an exhausted registration self-prunes
                // (a cancelled finite timer's scheduler entry keeps maturing until its
                // repeats run out — its registry entry must die with it).
                if (e.RemainingFires > 0 && --e.RemainingFires == 0) _entries.RemoveAt(i);
                return drop;
            }
            return false;   // unknown serial (registry already pruned) — deliver
        }
    }

    // The pending click-command event spec the `evtgen_attrib` native fills while the
    // event_generator script (Scripts/init/eventgen.tcl) runs. Native ground truth:
    // TCL:CTclGlobalMethods::EvtGenAttrib (FUN_0063eb70) writes a global spec struct at
    // DAT_00772400+0xa134… — option set (string-compared in the handler, addresses
    // binary-verified): -evtid (+0xa134, via the event-name resolver FUN_005ba5b0),
    // -object (+0xa138 = the object the event is raised on), -target (+0xa13c),
    // -subject1/2/3 (+0xa140/44/48), -pos1/2/3 (3 floats each, +0xa160/6c/78),
    // -num1/2/3 (+0xa154/58/5c), -delay (+0xa184), -text1/-text2 (+0xa14c/50),
    // -desc (+0xa18c), -cursor (cursor setter FUN_006b91c0); anything else errors
    // "unknown parameter". The UI consumes the spec on a -command click by raising
    // the evtid on the -object (≡ set_event) — DigglesTclEngine.EvtGenFire mirrors that.
    public sealed class EvtGenSpec
    {
        public string EvtId = "";
        public int Object;                  // raise target (0 = use the objpar fallback)
        public string Target = "";
        public string Subject1 = "", Subject2 = "", Subject3 = "";
        public string Pos1 = "", Pos2 = "", Pos3 = "";
        public string Num1 = "", Num2 = "", Num3 = "";
        public string Delay = "";
        public string Text1 = "", Text2 = "";
        public string Desc = "";
        public string Cursor = "";

        public void Reset()
        {
            EvtId = ""; Object = 0; Target = "";
            Subject1 = Subject2 = Subject3 = "";
            Pos1 = Pos2 = Pos3 = "";
            Num1 = Num2 = Num3 = "";
            Delay = ""; Text1 = Text2 = ""; Desc = ""; Cursor = "";
        }
    }

    // Shared state for engine-aware Tcl commands. Tracks class
    // definitions, spawned entities (handle table), the current `this`
    // entity (set when running per-instance scripts / methods) and
    // a target Transform for spawning Unity GameObjects.
    //
    // One context per TclInterpreter; commands close over it.
    public sealed class DigglesTclContext
    {
        public TclInterpreter Interp { get; }

        // Class registry
        public Dictionary<string, DigglesClass> Classes { get; } = new();

        // Bumped on every Entities add/remove — lets the sim's per-step passes skip the
        // per-entity ContainsKey liveness probe when nothing spawned/despawned since the
        // snapshot (the common case; a destroy mid-loop bumps this and re-enables probing).
        internal int EntitiesVersion;

        // Stat/attribute systems (def_attrib registry + attribute & skill domains).
        // The Tcl attrib commands route through this; per-entity values live on
        // DigglesTclEntity.Stats.
        public Diggles.Runtime.Stats.StatService Stats { get; } = new();

        // Session fight registry and combat-query service. The original stores this data in the
        // global CFightActionMgr and object fields; keeping it on the Tcl context preserves the
        // same per-game lifetime without introducing another Unity singleton.
        public FightSystem Fight { get; } = new();

        // Native EnergyMgr session state: source selection, store consumption, and timed loads.
        public EnergySystem Energy { get; }

        // Native CFluid session state: sparse cells, sources, barriers, queries, and flow.
        public FluidSystem Fluids { get; }

        // Native global Lore registry. Track geometry and motion persist independently of whether
        // an owner currently has a Lore action, matching the action-table auxiliary block.
        public LoreTrackSystem LoreTracks { get; }

        // Native CGame+0xbc production-window override table. Story scripts use it to expose
        // tutorial actions one cell at a time; index 31 is the shared pack/unpack action.
        public ProductionSlotOverrides ProdSlotOverrides { get; } = new();

        // Native ViWinNewsTicker session model: active notifications, retained history,
        // category mask, priorities, timeouts, and click Tcl handlers.
        public NewsTickerSystem NewsTicker { get; }

        // Native ViWinLayout session model used by textwin, online help, diary pages, and the
        // quest journal. Original Tcl remains responsible for authoring each page.
        public DocumentWindowSystem Documents { get; }

        // Per-owner opponent Tcl state and native five-second AI cadence.
        public AiSystem? Ai;

        // Save/load application boundary. It remains null until the native.sav mapper can restore
        // a complete live graph transactionally; the retired.dqs backend is never assigned here.
        public IGameSaveBackend? SaveGames;

        // Handle → entity. Handles are integer strings exposed to Tcl
        // (e.g. `set h [new Zwerg...]` → h = "1", "2",...).
        public Dictionary<int, DigglesTclEntity> Entities { get; } = new();
        int nextHandle = 1;

        // Currently-evaluated class body (for class_* commands).
        public DigglesClass? CurrentClass;

        // Current `this` entity (set during method/event invocation).
        public DigglesTclEntity? This;

        // Breakcode to inherit for an action created during the currently-running finishcode. A chain
        // like hatongone(break=create_muetze)→hatonhand{}→hatonhead{} authors empty intermediate breaks;
        // if the chain is interrupted mid-way the cleanup (create_muetze) would be lost. While running a
        // finishcode we expose the finishing action's break here so the next chained action inherits it
        // (when its own break is empty) — so interrupting any link still runs the deferred cleanup.
        public string? ChainBreak;

        // exec_deferred schedules global Tcl for the next shell tick. A fixed snapshot is drained
        // by DigglesGameSim so scripts queued by a deferred script wait for one more tick.
        public readonly Queue<string> DeferredScripts = new();

        // Optional parent Transform for spawning real GameObjects.
        // When set together with EntitySpawner, `new ClassName...` in
        // Tcl creates a real GameObject under this transform.
        public Transform? SpawnParent;

        // Optional bridge to a host that materializes Tcl entities as
        // Unity GameObjects. When null, `new` is in-memory only.
        public IDigglesEntitySpawner? EntitySpawner;

        // Optional model library (textures/clips). Set by the runtime host so engine
        // commands can resolve texture variants — `def_texture` registers them and
        // `set_textureanimation` looks them up. Null in headless/test runs (commands no-op).
        public DigglesModelLibrary? ModelLibrary;

        // Optional faithful pathfinder (Calc1 over NavWorld) for `action this walk <target>`.
        // Set by the runtime host (DigglesWorldRuntime) after the terrain builds.
        public Diggles.Runtime.Navigation.NavWorldService? NavService;

        // Optional fixed-step simulator. When wired, timer_event
        // schedules into Sim.Scheduler and SrvDoThink-equivalent passes
        // (event dispatch + state body ticks) run on Sim.Step.
        public DigglesGameSim? Sim;

        // Cinematic camera model driven by the sequencer's camera commands
        // (set_view/set_viewpos/set_camerafollow/…). Created in DigglesTclEngine.Register;
        // ticked by Sim.Step. A Play-mode component poses the real Camera from its state.
        public CutsceneCameraState? Camera;

        // Dialogue/subtitle model driven by set_speechout / speechicon / textwin. Created in
        // Register; ticked by Sim.Step. A Play component renders the subtitle bar + bubbles.
        public DialogueState? Dialogue;

        // Game options (the `option get/set` command), e.g. TextSpeed. Per-context.
        public readonly Dictionary<string, string> Options = new();

        // Per-(class, anim) face-animation info registered by set_classaniminfo, read by
        // get_classaniminfo (do_action anim → drive mouth/eyes during a talk clip). Key = "class\nanim".
        public readonly Dictionary<string, string> ClassAnimInfo = new();

        // Audio model driven by sound / adaptive_sound / seq_audiostream. Created in Register;
        // ticked by Sim.Step. A Play component loads the real WAV/MP3 into AudioSources from it.
        public AudioState? Audio;

        // Particle/FX model driven by *_particlesource / particle_processing. Created in Register;
        // ticked by Sim.Step. A Play component maps int type → Unity ParticleSystem presets.
        public ParticleSystemState? Particles;

        // Screen-FX model (fade / camera shake / lightning / laserbeam / change_light). Created
        // in Register; ticked by Sim.Step. A Play component drives the fade overlay + shake.
        public ScreenFxState? ScreenFx;

        // The Elf guide (do_elf / `elf <verb>`). Created in Register; ticked by Sim.Step.
        public ElfState? Elf;

        // True while a cutscene runs (set_sequence 1/0). The Play presenter only drives the
        // camera + subtitles while this is set, so cutscenes don't hijack normal gameplay.
        public bool SequenceActive;

        // True while the save stager runs the native shell-construction half — obj_init
        // evaluated per restored entity. Object-scope Tcl during this window is lawful
        // constructor code (the pre-activation object-Tcl tripwire in the activation tests
        // exempts it); everything outside it keeps the inert-staging law.
        public bool RestoredShellConstructionActive;

        // The cutscene break flag — the byte at DAT_00772400+0x8d. Set by ESC while a sequence
        // runs (input handler FUN_0052c510:353-355); read by `get_sequencebreaked` (FUN_00649330);
        // cleared by every `set_sequence` transition (FUN_00649080:90). The sequencer's
        // eval_sequence state polls it and runs the faithful skip drain (sequencer.tcl:228-239):
        // every remaining non-'-' line executes with the per-proc is_breaked reductions, then
        // shut_sequencer restores camera/actors/voice and the trigger destroys itself.
        public bool SequenceBreaked;

        public bool SequencePresentationDrain;
        internal long SequencePresentationDrainStarted;
        internal int SequencePresentationDeferredOps;
        readonly Queue<DigglesTclEntity> _deferredSequencePresentation = new();
        readonly HashSet<int> _deferredSequencePresentationHandles = new();

        public int DeferredSequencePresentationCount => _deferredSequencePresentation.Count;

        internal void DeferSequencePresentation(DigglesTclEntity entity)
        {
            SequencePresentationDeferredOps++;
            if (!_deferredSequencePresentationHandles.Add(entity.Handle)) return;
            _deferredSequencePresentation.Enqueue(entity);
        }

        internal bool TryTakeDeferredSequencePresentation(out DigglesTclEntity entity)
        {
            if (_deferredSequencePresentation.Count == 0)
            {
                entity = null!;
                return false;
            }
            entity = _deferredSequencePresentation.Dequeue();
            _deferredSequencePresentationHandles.Remove(entity.Handle);
            return true;
        }

        internal void ClearDeferredSequencePresentation()
        {
            _deferredSequencePresentation.Clear();
            _deferredSequencePresentationHandles.Clear();
        }

        // True while a StoryMgr/campaign world is active (native is_storymgr / im_in_campaign). False at the
        // main-menu backdrop + skirmish/test worlds. Drives campaign-only Tcl: notably hatcolor=0 in the
        // campaign (z_methods.tcl:194) → yellow work hats. Set by DigglesWorldRuntime (= !_atMenu).
        public bool IsStoryMgr;

        // The player's selection — natively a handle list (CGame+0xa0c4/+0xa0c8), not a single
        // handle; all the native selection laws live on SelectionSet. Lazy (`this` can't feed a
        // field initializer).
        SelectionSet? _selection;
        public SelectionSet Selection => _selection ??= new SelectionSet(this);

        // Native numeric selection groups 1..9 (CGame+0xA0EC, nine Int32 vectors). They retain
        // stable object handles independently from the current selection.
        public readonly List<int>[] SelectionControlGroups =
        {
            new(), new(), new(), new(), new(), new(), new(), new(), new(),
        };

        // The primary selected object handle (0 = none) — selection[0], exactly what the native
        // get_selectedobject returns (FUN_00647100). Kept as a property over the set so every
        // legacy single-select writer/reader (ClickToMoveService, placement, UI controllers,
        // Tcl set_selectedobject) keeps working: set = replace the whole selection.
        public int SelectedHandle
        {
            get => Selection.Primary;
            set => Selection.ReplaceWith(value);
        }

        // The pending evtgen_attrib event spec (native DAT_00772400+0xa134…). Reset by
        // DigglesTclEngine.EvtGenRun before each event_generator evaluation; consumed by
        // EvtGenFire on a -command click.
        public readonly EvtGenSpec EvtGen = new();

        // StoryManager event flags (sm_set_event / sm_get_event). Per-context.
        public readonly Dictionary<string, string> StoryEvents = new();

        // Native def_event declarations captured during the live definition boot. Save
        // preflight uses this registry because the original resolves event ids by declaration
        // index and cannot load an unknown event name safely.
        public readonly HashSet<string> RegisteredEventNames =
            new(System.StringComparer.Ordinal);

        // sm_add_event registry (FUN_0065a820, native name "SMAddEvent"): the native event array.
        // Insertion order = the event index (FUN_005f8670 append; a redefinition is a native
        // TCL_ERROR); each entry carries an optional Trigger_* class + keyword (:122-162).
        // sm_get_event/sm_set_event read the flag in StoryEvents above (create-on-write, so the
        // growth/zone path via check_uebergang→sm_get_event Uebergang_* works without registration
        // → sm_add_event is not generator-relevant). This makes an event quest-log-visible and
        // holds trigger metadata; the trigger-firing side (AchievementManager::CheckTriggerName +
        // FUN_005f8840 on set-true, spawning the Trigger_* story objects) is story/quest — read by
        // no growth decision — deferred.
        public readonly List<string> StoryEventOrder = new();
        public readonly Dictionary<string, (string Trigger, string Extra)> StoryEventTriggers = new();

        // Per-owner resource/invention tallies (owner_attrib): owner index → attrib name → value.
        // Bp<ClassName> is known when nonzero; <ClassName> counts owned items (add_owner_attrib).
        public readonly Dictionary<int, Dictionary<string, double>> OwnerAttribs = new();

        // Live diggable terrain — set after EmitTerrain; the dig primitives read/mutate it.
        public Diggles.Runtime.World.DiggableTerrain? Terrain;

        // Fog of war (native CFogOfWar at *(World+0x20) — full RE: Docs/FOG_OF_WAR_RE.md).
        // Created by DigglesWorldRuntime right after Terrain is wired (headless tests construct
        // it directly); updated by DigglesGameSim on every 4th 10 Hz think tick (the native
        // "every 4th SrvDoThink"); rendered by the FogOfWar presenter/overlay.
        public Diggles.Runtime.World.FogOfWarState? FogOfWar;

        // Tunnel wall-torch auto-spawn + underground darkness baseline (native glow-drop →
        // Misc_Light pipeline + the MapValidator base-light channel — full RE:
        // Docs/TUNNEL_TORCH_LIGHTING_RE.md). Created by DigglesWorldRuntime with the fog;
        // torches ticked by DigglesGameSim on the 10 Hz think tick.
        public Diggles.Runtime.World.TunnelTorchSystem? TunnelTorches;
        public Diggles.Runtime.World.BaseLightMap? BaseLight;
        // Tcl set_light_begin (DAT_007683d8, native default -10000 = "no sky") — arrives
        // during the campaign boot eval before the world systems exist, so it lands here
        // and seeds TunnelTorches/BaseLight at creation. The native command also couples the
        // independently serialized game+0x4410 ground-begin row to this value.
        public int LightBeginRow = -10000;
        public int GroundBeginRow = -10000;

        // The autonomous work dispatcher (native CProdPlanner port). Ticked by Sim.Step; assigns
        // idle gnomes to dig/harvest/production/haul jobs by raising evt_autoprod_* on them. Created in
        // DigglesTclEngine.Register. PlaceLocks = the shared reservation store (placelock_*).
        public Diggles.Runtime.Sim.ProdDispatcher? ProdDispatcher;
        public Diggles.Runtime.Sim.PlaceLockStore PlaceLocks = new();

        // The native trigger engine (wiggles/Wiggles/Objects/Trigger.cpp): per-object trigger
        // records armed by the `trigger` Tcl command (FUN_0060e920) — the whole Trigger_*
        // story-quest chain. Created in DigglesTclEngine.Register; scanned by DigglesGameSim
        // on the 10 Hz logic tick (FUN_0060df40 ← CGameThread::SrvDoThink 006b4a30:391).
        public TriggerService? TriggerService;

        // Building-placement mode (the native ghost-placement singleton DAT_0076a338,
        // FUN_00530e80 enter / FUN_00531310 confirm): the engine-side controller the click layer
        // and the authored building UI drive (EnterPlacement/Confirm/Cancel) and the
        // hide_obj_ghost native mutates. Created in DigglesTclEngine.Register.
        public Diggles.Runtime.PlacementController? Placement;

        // Ordered leisure-place registry (sparetime announce/query). Native records retain both the
        // exact category enum and stable object ID; updating an existing object preserves its order.
        public readonly List<SparetimeRegistration> SpareRegistry = new();
        // Ordered native horizon/fog regions created by horizon_set and serialized after trigger state.
        public readonly List<HorizonRegionState> HorizonRegions = new();
        // StoryMgr exploration dedup grid (sm_map_set/get) + per-object story callback procs (sm_register).
        public readonly Dictionary<(int, int), int> StoryMap = new();
        public readonly Dictionary<int, string> StoryCallbacks = new();

        // Level generator (the lg_* world-growth engine the StoryMgr drives). Created in Register.
        public Diggles.Runtime.World.LevelGenerator? LevelGen;

        // The during-dig template-deco pass (CMapBeautifier beams/treasure — the sibling of the
        // cave-skin completion swap). Created lazily by DigglesTclEngine.MapDecoOf once Terrain
        // and LevelGen exist. Docs/DIGGING_MECHANIC.md §9.B.
        public Diggles.Runtime.World.MapDecoBeautifier? MapDeco;

        // Dormant scenery seed shells restored from a loaded save's marker-7 spatial index
        // (DigglesTclEngine.RegisterDecoSeed, B3 §3). Deliberately separate from Entities:
        // these become presentation-only props under the LOD pump, so the save-capture
        // object graph — which enumerates Entities — stays byte-identical to the archive while the
        // retained marker-7 bytes round-trip verbatim. The per-carve deco sweep
        // (DigglesTclEngine.MapDecoCarveSweep) also walks this list so a later dig strands them.
        public readonly System.Collections.Generic.List<DigglesTclEntity> MapDecoSeeds = new();
        internal int MapDecoSeedLodCursor;

        // ----- CStoryMgr native state (world growth during digging) -----
        // Mirrors the lazy singleton FUN_005f8080 (alloc 0x189) the sm_* commands and DigNotify
        // operate on.
        public int StoryDigWeight;            // +0x98 — the growth-pressure counter. Zeroed by the
                                              // ctor reset (FUN_005fa7c0:31) and by every
                                              // placement-emitting lg_get_level
                                              // (FUN_00652d10:73/:280 — the growth re-arm;
                                              // a failed/empty generation leaves it, so the
                                              // next dig re-latches = the failcount retry).
        public int StoryDigThreshold = 5;     // +0x9c — sm_set_digcount (FUN_0065c750:233); ctor default 5.
        public bool StoryDigMessage;          // +0xa0 — sm_dig_message (FUN_0065c500:83-84); ctor
                                              // default 0 (FUN_005fa7c0:33). Gates the DigNotify
                                              // story callback (FUN_005fd060:33 `if (+0xa0
                                              // != 0 && +0x78 != -1)`). Dead in shipped data: the
                                              // `sm_dig_message 1` calls are commented out
                                              // (story.tcl:524/539/555); only `sm_dig_message 0`
                                              // (:792) is live.
        public int StoryLatchDir;             // +0x88 — 0 none; 1=−y(up) 2=+y(down) 4=−x 8=+x.
        public int StoryLatchX, StoryLatchY;  // +0x8c/+0x90 — raw breakthrough tile (masked &~3 by consumers).
        public bool StoryLatchConsumed;       // +0x94 — set by sm_scan (consume-once).
        public int StoryMapW, StoryMapH;      // story grid dims = sm_create_map W/4 × H/4 (FUN_0065f430:176-180).
                                              // 0 = unsized (no bounds clamping — test/menu paths).
        // sm_get_level_info → lg_set_starttemplatepro shared rect+utemp state (+0x40/+0x44/+0x38/+0x3c/+0x50).
        public int StoryRectX, StoryRectY, StoryRectW, StoryRectH;   // free rect, story cells
        public string StoryUtemp = "";        // picked unique-temp name ("" = none)
        // The raw maximal free rect + scan cursor from sm_get_level_info stage-1, persisted so the
        // FUN_005fc0a0 rect re-placement that lg_set_starttemplatepro runs later can re-fit. Native:
        // the raw bounds live on the StoryMgr at +0x58/+0x5c/+0x60/+0x64 (FUN_005fac90:114-117) and
        // the scan cursor at +0x48/+0x4c (:628-629); FUN_005fc0a0 reads them back verbatim.
        public int StoryRawRectX, StoryRawRectY, StoryRawRectW, StoryRawRectH;   // free rect, story cells
        public int StoryScanX, StoryScanY;    // the sm_get_level_info scan cell (= story cursor +0x48/+0x4c)

        // The selected unique-temp's placed world rect (native SM+0x54 record + temp+0x48/+0x4c origin
        // + temp+0x30/+0x34 size), captured by lg_set_starttemplatepro when the composite lands. Read
        // by `sm_draw_stone -exlimit` (the undiggable rim at the cave's depth) and `-utmat` (the cave's
        // per-rect scape material), which story.tcl:227-228 issues right after a successful lg_start.
        // World tiles. StoryUtempPlaced=false ⇒ no cave this generation ⇒ both draw_stone branches no-op.
        public bool StoryUtempPlaced;
        public string StoryUtempPlacedName = "";
        public int StoryUtempWorldX, StoryUtempWorldY, StoryUtempExtW, StoryUtempExtH;

        // Zone bands (sm_add_zone / sm_set_zone / sm_force_zone — FUN_005fcdf0): each zone has an
        // upper world-Y bound (−1 = +inf); zone i spans (bound[i−1], bound[i]].
        public sealed class StoryZone { public string Name = ""; public int Bound = -1; }
        public readonly List<StoryZone> StoryZones = new();
        public int StoryZoneFloor;            // +0x84 — sm_force_zone clamp (lookup never returns below it)

        // sm_add_temp registry (the unique-template windows; 0x64-byte records natively).
        public sealed class StoryTemp
        {
            public string Name = "";
            public int Lo, Hi;                // Y-depth window in world tiles, zone-relative (rec+0x20/+0x24)
            public int Zone = -1;             // rec+0x50 — the zone index current at registration time
                                              // (FUN_005f91e0:94 stores zoneCount−1; CampaignInit interleaves
                                              // sm_add_zone/sm_add_temp so each temp belongs to its zone).
                                              // The utemp pick requires rec.Zone == zone(scan cell)
                                              // (FUN_005fac90:254) — without it swamp/crystal/lava uniques
                                              // leak into Urwald growth.
            public bool Exclusive;            // -exclusive
            public string ZoneBorder = "";    // -zoneborder
            public int StoneIdx = -1;         // -stoneidx
            public string MinDistRef = "";    // -mindist <temp> <v>
            public float MinDistVal;
            public float MidDist;             // -middist (parsed; neutral in the port)
            public float NonRelevant;         // -nonrelevant probability roll
            public string DigConnect = "", Connect = "";   // parsed pass-through
            public int CheckAlign = -1;
            public bool Placed;               // set by sm_set_temp (FUN_005fa320: pos + used flag)
            public int PlacedX, PlacedY;
            public readonly List<string> EventNames = new();
            // Composite completion tracking (FUN_005fa320 recursion): sub-template names of this
            // record's sm_def_temp_group seen placed — Placed sets when all children placed.
            public readonly System.Collections.Generic.HashSet<string> SeenChildren = new();
        }
        public readonly List<StoryTemp> StoryTemps = new();
        // sm_def_temp_group <name> <spec> — group → raw chunk spec (consumed by sm_draw_stone natively;
        // stored for completeness).
        public readonly Dictionary<string, string> StoryTempGroups = new();
        // sm_set_temp placed-template registry (also feeds sm_get_temppos + the -mindist checks).
        public readonly List<(string name, int x, int y)> StoryPlacedTemps = new();
        // The shared story/worldgen random stream. Native = one game-wide MT19937 (FUN_0049c670
        // genrand + FUN_004454d0 sgenrand) lazily seeded with the constant 0x1105 (the only seeder
        // callers: FUN_0049c670:33, FUN_00598f10:38 — no time reseed anywhere). We run the same
        // MT19937 with the native base seed (Diggles.Runtime.World.Mt19937). Its per-session variety
        // in the original comes from every engine system advancing the same stream before worldgen
        // samples it; the port has no such interleaving (our other systems roll their own RNGs), so
        // a bare MT(0x1105) worldgen-only stream would regenerate the same caves every playthrough.
        // We emulate the shared-stream decorrelation with a bounded per-session prewarm keyed on
        // GUID/TickCount entropy (Mt19937.CreateSessionStream) — native algorithm + native seed,
        // reproducible per session, varied across sessions. This is not a bit-exact reproduction of
        // a specific original playthrough (that would need the full cross-system stream interleaving
        // plus a captured gameplay trace, which the available evidence doesn't provide).
        // Variety gate: LevelGenTests.Generate_RealUrwald_VarietyAcrossSessions.
        // Tests needing determinism overwrite this with a seeded Random (System.Random or Mt19937).
        public System.Random StoryRng = Diggles.Runtime.World.Mt19937.CreateSessionStream(
            unchecked((System.Environment.TickCount * 397) ^ System.Guid.NewGuid().GetHashCode()));

        // The local human player's owner index — used by dig_mark/is_dig_marked. (Player = owner 0.)
        public int LocalPlayer;

        // ----- cave skins (CMapBeautifier, Docs/DIGGING_MECHANIC.md) -----
        // The `add_cave_skin_class <set> <detail> <size>` rule table (beautifier+0x28, 0x1c stride,
        // FUN_00617570:146-168): one row per registered skin class. Set = the zone band index
        // (Urw 0 / Met 1 / Kris 2 / Golden(Lava) 3 — matches sm_add_zone order, FUN_005fcdf0);
        // Detail = 2/1/0 (class-name letter a/b/c, animinit.tcl:408-413); Size = 0..3 (caves_1..4 =
        // the cave-stamp template index the completed rect carries). Consumed by the dig-completion
        // pass (DigglesTclEngine.CaveSkinDigCheck — the faithful FUN_0058ba00→FUN_0058e9c0 pick).
        public sealed class CaveSkinRule
        {
            public int Set; public int Detail; public int Size; public string ClassName = "";
        }
        public readonly List<CaveSkinRule> CaveSkinRules = new();

        // Per-player race table (native game-setup slot; set_ownerrace/get_ownerrace —
        // ENGINE_TCL_API.txt:530/1305 take an owner index, not an object ref). Races:
        // 0 = plain gnome, 1 Voodoo, 2 Knocker, 3 Brain, 4 Vampir (z_methods.tcl:294).
        // Campaign + skirmish boots map owners 0..4 → races 0..4 identically
        // (CampaignInit.tcl:15-19, Skirmish.tcl:50-54). get_objrace resolves an object's
        // race through this table via its owner (there is no set_objrace anywhere in the
        // API). Default 0 = plain, so unconfigured worlds (menu backdrop) dress their
        // owner-0 dwarves as regular player gnomes.
        public readonly int[] OwnerRace = new int[8];

        // Native game player slots persisted by FUN_00557c70. These fields are separate from race
        // and owner attributes; multiplayer/session setup owns the two IDs and display strings,
        // while the final eight values are the authoritative diplomacy row below.
        public sealed class PlayerSlotState
        {
            public string PlayerName = "Nobody";
            public int SlotState;
            public int PlayerOwnerId;
            public int ClanOwnerId;
            public string SecondaryName = "";
        }

        public readonly PlayerSlotState[] PlayerSlots = CreatePlayerSlots();
        public readonly int[] NativePlayerReservedFraming = new int[32];

        static PlayerSlotState[] CreatePlayerSlots()
        {
            var slots = new PlayerSlotState[8];
            for (int owner = 0; owner < slots.Length; owner++)
            {
                slots[owner] = new PlayerSlotState
                {
                    SlotState = owner == 0 ? 1 : 2,
                    PlayerOwnerId = owner,
                    ClanOwnerId = owner,
                };
            }
            return slots;
        }

        // The 8×8 owner diplomacy table (native: game+0xa420, 8 rows × 0x34 bytes, the
        // relation ints at row+0x14+col*4). Codes: 0 own, 1 ally, 2 friend, 3 neutral,
        // 4 enemy (get_diplomacy FUN_00669000:131-152). Init law (FUN_0055a060:218-233):
        // every pair = neutral(3), then the diagonal = own(0). Written one-directionally by
        // set_diplomacy (FUN_00669360; scripts set both directions — CampaignInit.tcl:4-13),
        // read by get_diplomacy and by the obj_query/trigger owner-word masks
        // (FUN_005e2330: enemy/friend/ally/neutral → per-relation bit sets).
        public readonly int[,] Diplomacy = CreateDiplomacyTable();
        static int[,] CreateDiplomacyTable()
        {
            var t = new int[8, 8];
            for (int a = 0; a < 8; a++)
                for (int b = 0; b < 8; b++)
                    t[a, b] = a == b ? 0 : 3;
            return t;
        }

        internal void SetMutualEnemy(int firstOwner, int secondOwner)
        {
            if ((uint)firstOwner >= 8u || (uint)secondOwner >= 8u || firstOwner == secondOwner)
                return;
            Diplomacy[firstOwner, secondOwner] = 4;
            Diplomacy[secondOwner, firstOwner] = 4;
        }

        // Optional bridge to a host that materializes the world grid.
        // When null, map_create / map_template / map_setlayer2 / split_template
        // are no-ops (still parse without throwing — useful for tests
        // and headless Tcl runs).
        public IDigglesMapBuilder? MapBuilder;

        // Set during DigglesTclEngine.DispatchEvent — read by `event_get`
        // inside the handler body. Null outside event dispatch.
        public EntityEvent? CurrentEvent;

        // Live timer registrations — the engine-side mirror of the native CTimerEvtMgr
        // entries needed for `timer_unset <obj> <userid>` (FUN_0069fc90 → FUN_00606110:
        // remove every entry with entry.owner == obj && (entry.userid == id || id == -1);
        // there is no name-based unset in the original). Each timer_event records its
        // (handle, userid, event, repeats) here keyed by a serial the scheduler payload
        // carries; cancellation either physically Unsets the scheduler entry (when the
        // event name is unambiguous) or marks the serial cancelled so DigglesGameSim's
        // delivery hook drops the matured event.
        public readonly TimerRegistry Timers = new();

        public DigglesTclContext(TclInterpreter interp)
        {
            Interp = interp;
            Energy = new EnergySystem(this);
            Fluids = new FluidSystem(this);
            LoreTracks = new LoreTrackSystem(this);
            NewsTicker = new NewsTickerSystem(this);
            Documents = new DocumentWindowSystem(this);
        }

        // Native auto-name counters ("<Class>_<n>", one per class). Restored sessions seed
        // them past the largest archived suffix so continuation numbering never collides.
        readonly Dictionary<string, int> _autoNameCounters = new();

        public string NextAutoInstanceName(string className)
        {
            _autoNameCounters.TryGetValue(className, out int used);
            used++;
            _autoNameCounters[className] = used;
            return className + "_" + used.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public void SeedAutoInstanceName(string className, string instanceName)
        {
            if (instanceName.Length <= className.Length + 1 ||
                !instanceName.StartsWith(className, StringComparison.Ordinal) ||
                instanceName[className.Length] != '_')
            {
                return;
            }
            if (!int.TryParse(
                    instanceName.Substring(className.Length + 1),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int suffix))
            {
                return;
            }
            _autoNameCounters.TryGetValue(className, out int used);
            if (suffix > used) _autoNameCounters[className] = suffix;
        }

        public int RegisterEntity(DigglesTclEntity ent)
        {
            int h = nextHandle++;
            ent.Handle = h;
            Entities[h] = ent;
            EntitiesVersion++;
            return h;
        }

        public void RegisterEntityAtHandle(DigglesTclEntity ent, int handle)
        {
            if (handle <= 0) throw new System.ArgumentOutOfRangeException(nameof(handle));
            if (Entities.ContainsKey(handle))
                throw new System.InvalidOperationException($"Entity handle {handle} is already registered.");
            ent.Handle = handle;
            Entities[handle] = ent;
            if (handle >= nextHandle) nextHandle = handle + 1;
            EntitiesVersion++;
        }

        public void RebaseNextHandle()
        {
            int maximum = 0;
            foreach (int handle in Entities.Keys)
                if (handle > maximum) maximum = handle;
            nextHandle = maximum + 1;
        }

        public DigglesTclEntity? GetEntity(string handle)
        {
            if (int.TryParse(handle, out int h) && Entities.TryGetValue(h, out var e))
                return e;
            return null;
        }

        // Root of the original game's `data/` directory. Used to resolve
        // `call scripts/...` paths. Leave empty to disable file loading
        // (commands then no-op or throw depending on tolerance flags).
        public string DataRoot = "";

        // Current working directory, relative to DataRoot (forward slashes, no
        // leading/trailing slash; "" = DataRoot). Maintained by the `cd` command;
        // `glob` / `pwd` / `file` resolve against DataRoot + Cwd. classinit.tcl uses
        // `cd <dir>; glob *.tcl; cd..` to enumerate the class files.
        public string Cwd = "";

        // Cache of fully-loaded file contents. Real Tcl `call` re-executes
        // each time (Diggles' "cached" flag) but doesn't re-read disk.
        public System.Collections.Generic.Dictionary<string, string> FileCache = new();

        // Script-preprocessor symbol table (the native loader FUN_006bb140's DEFINE list,
        // DAT_00c3c7a4/8): `//#` lines are directives (IF/IFNOT/ELSE/ENDIF/DEFINE/UNDEFINE/
        // STOPIF/STOPIFNOT), not comments. The shipped retail exe runs the FULL game —
        // techtreetunes.tcl's `//# IF FULL … //# ELSE … //# ENDIF` blocks must keep the FULL
        // branch (e.g. tttitems_Hauklotz 6 items,:47) and drop the demo one (:49); without
        // the preprocessor both `set` lines ran and the demo list won by last-write (the
        // "sawmill shows only 3 products" bug). Symbols are case-insensitive
        // (native __strupr's both sides) and persist across files like the native table.
        public readonly System.Collections.Generic.HashSet<string> PreprocDefines =
            new(System.StringComparer.OrdinalIgnoreCase) { "FULL" };

        // If true, `call` to a missing file silently no-ops instead of
        // throwing. Useful for partial game data setups (test envs etc.).
        public bool TolerateMissingFiles;

        // When set, ResolveEntity returns a throw-away entity instead of
        // throwing on missing `this` / unknown handles. Used by test
        // harnesses that load real Diggles scripts without setting up
        // the full entity lifecycle.
        public bool TolerateMissingEntities;

        // When set, class_* / method / state / handle_event / etc. become
        // no-ops if CurrentClass is null (i.e. called outside def_class).
        // Diggles "snippet" files (`t_methods.tcl`, `z_anims.tcl`,...) are
        // designed to be embedded via `call scripts/...` inside def_class,
        // so loading them in isolation hits this case.
        public bool TolerateMissingClass;
        DigglesTclEntity? scratchEntity;

        public DigglesTclEntity ResolveEntity(string handleOrThis)
        {
            // "." is the native self-handle (zwerg.tcl:154 `set_objinfo. EinZwerg` is the
            // source of the N29 'EinZwerg' every native dwarf save carries).
            if (handleOrThis == "this" || handleOrThis == "$this" || handleOrThis == ".")
            {
                if (This != null) return This;
                if (TolerateMissingEntities) return GetScratch();
                throw new TclException("no `this` entity in current context");
            }
            var e = GetEntity(handleOrThis);
            if (e != null) return e;
            if (TolerateMissingEntities) return GetScratch();
            throw new TclException($"unknown entity handle \"{handleOrThis}\"");
        }

        DigglesTclEntity GetScratch()
            => scratchEntity ??= new DigglesTclEntity { Class = new DigglesClass { Name = "_scratch_" } };

        public DigglesClass GetClassOrThrow(string name)
        {
            if (Classes.TryGetValue(name, out var c)) return c;
            throw new TclException($"unknown class \"{name}\"");
        }

        // ----- inheritance-aware lookup helpers -----

        public TclProc? FindMethod(DigglesClass c, string name)
        {
            if (c.Methods.TryGetValue(name, out var m)) return m;
            foreach (var baseName in c.Inherits)
                if (Classes.TryGetValue(baseName, out var bc))
                {
                    var found = FindMethod(bc, name);
                    if (found != null) return found;
                }
            return null;
        }

        // Per-class proc lookup (class then base classes). Used by the interpreter's
        // ResolveClassProc hook so the active object's own `proc` wins over a same-named
        // proc another class happened to define later into the shared global table.
        public TclProc? FindProc(DigglesClass c, string name)
        {
            if (c.Procs.TryGetValue(name, out var p)) return p;
            foreach (var baseName in c.Inherits)
                if (Classes.TryGetValue(baseName, out var bc))
                {
                    var found = FindProc(bc, name);
                    if (found != null) return found;
                }
            return null;
        }

        public string? FindEventHandler(DigglesClass c, string evt)
        {
            if (c.EventHandlers.TryGetValue(evt, out var code)) return code;
            foreach (var baseName in c.Inherits)
                if (Classes.TryGetValue(baseName, out var bc))
                {
                    var found = FindEventHandler(bc, evt);
                    if (found != null) return found;
                }
            return null;
        }

        public string? FindState(DigglesClass c, string state)
        {
            if (c.States.TryGetValue(state, out var code)) return code;
            foreach (var baseName in c.Inherits)
                if (Classes.TryGetValue(baseName, out var bc))
                {
                    var found = FindState(bc, state);
                    if (found != null) return found;
                }
            return null;
        }

        public HashSet<string> CollectMemberVars(DigglesClass c)
        {
            var s = new HashSet<string>(c.MemberVars);
            foreach (var baseName in c.Inherits)
                if (Classes.TryGetValue(baseName, out var bc))
                    s.UnionWith(CollectMemberVars(bc));
            return s;
        }

        // The per-instance default value for a member var: the declaring class's captured value
        // (this class first, then bases). "" when undeclared/no value. Lets `member ANIM_STILL`
        // (= 0 in the class-def scope) seed the instance with 0 instead of "" — so $ANIM_STILL
        // resolves correctly in obj_init instead of shadowing the global with empty.
        public string MemberDefaultFor(DigglesClass c, string name)
        {
            if (c.MemberDefaults.TryGetValue(name, out var v)) return v;
            foreach (var baseName in c.Inherits)
                if (Classes.TryGetValue(baseName, out var bc))
                {
                    var bv = MemberDefaultFor(bc, name);
                    if (!string.IsNullOrEmpty(bv)) return bv;
                }
            return "";
        }
    }
}
