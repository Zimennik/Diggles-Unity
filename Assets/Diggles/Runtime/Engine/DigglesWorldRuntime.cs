#nullable enable

namespace Diggles.Runtime
{
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using VContainer;

    // Play-mode world runtime — the standalone-build path for spawning + driving
    // entities entirely from Tcl, at runtime (not the editor Tcl Spawn Test which
    // bakes a static scene). On Start it:
    // 1. builds a Tcl interpreter + engine bridge + DigglesModelLibrary;
    // 2. wires a RuntimeWorldSpawner (world coordinate convention + a
    // DigglesAnimController per entity) under a Q_x(-90) world root;
    // 3. runs a spawn script (test "one of each" by default, or a campaign tcl);
    // 4. drives the faithful Tcl idle (real seq_idle) on dwarves so they animate;
    // 5. ticks DigglesGameSim every frame so Tcl actions/states run.
    //
    // Drop on an empty GameObject and press Play. Terrain is independent — reuse a
    // baked terrain in the scene or none (characters spawn at their tcl positions).
    [DisallowMultipleComponent]
    public sealed class DigglesWorldRuntime : MonoBehaviour, IRuntimeSessionPresentationParticipant
    {
        public enum SpawnMode { OneOfEach, CampaignTcl }

        /// <summary>Native single-player modes exposed by the New Game sub-screen.</summary>
        public enum NewGameVariant
        {
            Introduction,
            Campaign,
        }

        // Native menu dispatch table:../../re/notes/strings/Diggles.strings.txt:7772-7773.
        /// <summary>Native campaign dispatch path.</summary>
        public const string CampaignBootScript = "data/GameScripts/single_Startcamp.tcl";
        /// <summary>Native introduction dispatch path.</summary>
        public const string IntroductionBootScript = "data/GameScripts/single_Starttut.tcl";

        [Header("Data")]
        public string dataRoot = "";

        [Header("What to spawn")]
        public SpawnMode mode = SpawnMode.OneOfEach;
        [Tooltip("Campaign tcl, data-relative (used when mode = CampaignTcl).")]
        public string campaignTclRel = "Scripts/gameplay/Campaign.tcl";
        [Tooltip("BDB block archive, data-relative (terrain geometry source). " +
                 "BlockDB2.BDB matches the urw_unq_start campaign.")]
        public string bdbRel = "BlockDB2.BDB";
        [Tooltip("Run the real class system (animinit + classinit → ~300 classes from " +
                 "data) before spawning, so entities get their real obj_init / animsets / " +
                 "idle / states. Off: use the lightweight stubs/hardcode fallback. " +
                 "Opt-in (off by default) while interpreter coverage is filled in — turn on to test; " +
                 "the recursion guard converts a runaway load into a catchable error " +
                 "instead of crashing Unity. Expect gap warnings in the console.")]
        public bool loadRealClasses = false;

        [Header("Animation")]
        [Tooltip("Drive dwarves with the faithful seq_idle picker (weighted random " +
                 "idle variants). Off: each entity just loops its default pose.")]
        public bool driveFaithfulIdle = true;
        [Tooltip("GPU vertex-morph interpolation (smooth 10 FPS).")]
        public bool interpolate = true;

        [Header("Locomotion (tune by eye, like DigglesTclAnimDemo)")]
        [Tooltip("Body walk speed = model height × this per second (run scales by the " +
                 "clip gait ratio). Lower = slower body.")]
        public float walkBodyFactor = 1f;
        [Tooltip("Leg-cycle rate (1 = native authored 10 FPS — the original look). " +
                 "Raise only if feet drag, lower if they slip forward.")]
        public float legCadence = 1f;
        // Walk→run promotion is no longer a scene knob: the faithful law is time-based —
        // a speedtype-0 walk breaks into a run after 20 logic ticks (2.0 s) in stride
        // (FUN_00418280:107 + FUN_00423470; MetaWalkAction.RunPromoteSeconds). The old
        // serialized runDistance silently overrode the code default and caused the
        // "everyone runs everywhere" drift between scene and code.

        DigglesEngine? _engine;
        [Tooltip("Scene composition root (assign in the Inspector). Built once the engine is ready.")]
        [SerializeField] GameLifetimeScope? gameScope;
        TclInterpreter? _interp;
        DigglesTclContext? _ctx;

        // Public access for tools/UI (e.g. a hand-built subtitle Canvas reading Context.Dialogue).
        public DigglesTclContext? Context => _ctx;
        public DigglesCameraController? CameraController => _camController;
        DigglesModelLibrary? _lib;
        RuntimeMapBuilder? _map;
        private RuntimeMapBuilder? _observedMap;
        DigglesClassAssetRegistry? _classRegistry;
        GameObject? _worldHostRootGo;
        GameObject? _worldRootGo;
        Transform? _worldRoot;
        readonly List<DigglesTclEntity> _spawned = new();

        // Gameplay camera (resolved from Camera.main; the RE'd controller is added to it). Click-to-move
        // moved to ClickToMoveService.
        Camera? _cam;
        DigglesCameraController? _camController;

        [Header("Cutscene testing")]
        [Tooltip("Show the in-Play CutsceneTester panel (pick + play any cutscene, with a " +
                 "Reload+Play that respawns a fresh world). No scene setup needed.")]
        public bool showCutsceneTester = false;
        [Tooltip("If set, auto-play this cutscene (sequence script name, no .tcl) once the world " +
                 "has loaded. The CutsceneTester tool drives this; you can also set it by hand.")]
        public string autoPlayCutscene = "";

        [Header("Boot / Main menu (M9)")]
        [Tooltip("Build the world as a live 3D backdrop and wait at the main menu (a MainMenuView " +
                 "Canvas+TMP in the scene). New Game hands control to gameplay. Off = straight into " +
                 "interactive gameplay (fast iteration / tests).")]
        public bool startAtMainMenu = true;
        [Tooltip("Force a single menu backdrop cutscene (sequence name, no .tcl). Leave empty to cycle a " +
                 "random one from Scripts/sequences/Menue/start_*.tcl (faithful Trigger_StartScreen).")]
        public string menuCutscene = "";
        [Tooltip("Empty (default) = build the faithful built-in menu diorama (systeminit.tcl:74-97: " +
                 "unq_menue + urw_gng patches + menu camera). Set a data-relative tcl to override the menu " +
                 "world with a file. Either way New Game tears it down and builds campaignTclRel fresh.")]
        public string menuCampaignTclRel = "";
        [Tooltip("Cutscene (sequence name, no .tcl) auto-played on New Game once the campaign is built — " +
                 "the opening cinematic (faithful: Trigger_Urw_006 → urw_3). Empty = no intro.")]
        public string campaignIntroCutscene = "urw_3";
        [Tooltip("Show the in-Play coordinate probe (native↔world deltas, camera focus marker, " +
                 "right-click terrain to read native coords). Use to diagnose position shifts.")]
        public bool showCoordProbe = false;
        [Tooltip("Dig-tool feedback — the native DigMark plane meshes: committed dig orders stay " +
                 "visibly marked until dug/erased, and an armed dig tool highlights the cells under " +
                 "the cursor before the click (DigMarkOverlay MeshRenderers on the front plane). " +
                 "Live-toggleable.")]
        public bool showDigMarks = true;

        // Persisted across a scene reload (Reload+Replay) so the fresh world auto-plays it.
        static string? s_pendingCutscene;
        int _activeCutscene;          // handle of the live test sequencer trigger (0 = none)
        // The persistent menu trigger (faithful Trigger_StartScreen — one object replays forever,
        // Trigger_Urwald.tcl:8-15 destroy_overload → run): its per-object globals survive across
        // plays, so `sequence_counter` accumulates (start_006's `do_script change st006_end_2 0.4 10`
        // 10-view gate) and the actor list starts empty each play (actors_preset=0 → reset,
        // sequencer.tcl:366). Recreating a trigger per play used to reset the counter and preset leftover
        // actors, shifting every [Actor n] index (the "wrong dwarf walks" menu bug).
        int _menuTrigger;
        public bool WorldReady { get; private set; }
        /// <summary>Gets whether a completed Tcl batch is waiting for synchronous world activation.</summary>
        public bool HasPendingWorldSwap => _pendingWorldSwap != null;
        /// <summary>Gets the number of script-driven world candidates activated by this runtime.</summary>
        public int CompletedWorldSwapCount { get; private set; }
        /// <summary>Gets the number of distinct terrain/root presentation pairs wired.</summary>
        public int PresentationWireCount { get; private set; }
        bool _newGameStarting;
        bool _realClassesLoaded, _idleLoaded;   // load the class system / idle plumbing once across rebuilds
        bool _atMenu;                             // showing the main menu (cycle random backdrop cutscenes)
        readonly List<string> _menuSeqs = new();  // the start_*.tcl backdrop set
        int _lastMenuSeq = -1;
        float _menuIdleT;                         // seconds since a backdrop cinematic was last active
        const float MenuBackdropGap = 1.0f;       // gap before auto-picking the next (covers empty-stub cutscenes)
        private IRuntimeWorldSessionHost? _worldSessionHost;
        private RuntimeGameSession? _boundSession;
        private RuntimeGameSession? _pendingWorldSwap;
        private RuntimeGameSession? _nonStoryGameplaySession;

        [Header("Debug")]
        [Tooltip("Write a buffered trace of a few tracked gnomes to <project>/diggles_debug.log.")]
        public bool fileDebugLog = false;
        [Tooltip("Trace entity event/state bodies command-by-command to the Console (DigglesTclEngine.VerboseScriptLog). " +
                 "Shows the exact step where a chain stops: '=> !!ERR' = a thrown command; a body that just ends = a proc " +
                 "returned early (e.g. dig_starttask bailing on get_digedge -1). Verbose — enable only to debug one action.")]
        public bool verboseChainTrace = false;
        [Tooltip("[ANIMTRC]/[FINTRC] — log every anim clip resolution (which clip an actor actually got: the start_001 " +
                 "wrong-anim runner, the urw_3 sit-stand timeline) + every action finishcode (stale followaction replays). " +
                 "Toggleable live.")]
        public bool traceAnimClips = false;
        [Tooltip("[CLIMBTRC] — ~2 Hz body-vs-record positions during climbs (a discriminator for the body sinking into " +
                 "the wall: constant delta ⇒ pose/clip-layer bug; growing delta ⇒ an external writer moves the body). " +
                 "Toggleable live.")]
        public bool traceClimb = false;

        void Start()
        {
            // Keep running (and sounding) on focus loss — Unity's default pause stalled the
            // audio sources and the adaptive base layer restarted from zero on refocus.
            Application.runInBackground = true;
            // Let a shipped/user config override the serialized dev-machine data path before
            // it is first consumed (_engine.Build below). No file / parse error → keeps the
            // serialized value; the loader logs which source won. Editor works unchanged.
            dataRoot = DigglesConfig.ResolveDataRoot(dataRoot);
            if (string.IsNullOrEmpty(dataRoot) || !Directory.Exists(dataRoot))
                Debug.LogError($"[Diggles] Original game data not found at \"{dataRoot}\". Set \"dataRoot\" in "
                    + "Assets/StreamingAssets/diggles_config.json (or in a diggles_config.json next to the executable) "
                    + "to the game's data folder.");
            // Text locale + byte encoding for the original data — not hardcoded. An explicit
            // "locale" in the config wins (override); otherwise auto-detect from the data set (Steam
            // ships exactly one locale's "_[xx]" text files and re-downloads on a language switch);
            // "en" only as a last resort. ru is read as Windows-1251, the rest as Latin-1.
            var locale = DigglesConfig.ResolveLocale("");
            if (string.IsNullOrEmpty(locale)) locale = DigglesText.DetectLocale(dataRoot);
            DigglesText.SetLocale(string.IsNullOrEmpty(locale) ? "en" : locale);
            Debug.Log($"[Diggles] original-data text locale = {DigglesText.Locale}");
            if (fileDebugLog)
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin(
                    System.IO.Path.Combine(Application.dataPath, "..", "diggles_debug.log"));
            // World root carries the Q_x(-90) hack so the inner model pitch chain resolves
            // upright (see Docs/COORDINATE_SYSTEM.md §3). Created first: the engine's spawner
            // parents entities under it.
            _worldHostRootGo = new GameObject("WorldRoot");
            _worldHostRootGo.transform.SetParent(transform, false);
            _worldHostRootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            _worldRootGo = new GameObject("WorldContent.Initial");
            _worldRootGo.transform.SetParent(_worldHostRootGo.transform, false);
            _worldRoot = _worldRootGo.transform;

            // Engine core (interp + ctx + state objects + library + spawner + map + sim), built in
            // one place so the Play composition and the DI scope share the exact order.
            _engine = new DigglesEngine();
            _engine.Build(dataRoot, _worldRoot, bdbRel);
            _interp = _engine.Interp;
            _ctx = _engine.Ctx;
            _lib = _engine.Lib;
            _map = _engine.Map;
            if (fileDebugLog)
            {
                // Full command trace for the tracked gnome: every Tcl command it runs,
                // with args + return value (incl. nested [cmd] and proc internals).
                _interp.CommandTrace = (name, args, result) =>
                {
                    var t = _ctx?.This;
                    if (t == null || !Diggles.Runtime.Diagnostics.DigglesDebugLog.IsTracked(t.Handle)) return;
                    var sb = new System.Text.StringBuilder("  ");
                    sb.Append(name);
                    for (int k = 1; k < args.Length && k <= 6; k++) { sb.Append(' '); sb.Append(TraceTrunc(args[k], 28)); }
                    sb.Append(" -> ").Append(TraceTrunc(result, 48));
                    Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(sb.ToString());
                };
            }
            // VContainer composition root: the scene-authored GameLifetimeScope (assigned in the
            // Inspector). Built now that the engine exists — the scope registers the core (engine +
            // context + settings + tick/click services) and runs the installer (the scene controllers).
            if (gameScope != null)
            {
                RuntimeGameSession initialSession =
                    RuntimeGameSession.CreateInitial(_engine, _worldRootGo);
                _boundSession = initialSession;
                gameScope.BuildWith(_engine, new ActiveGameSession(initialSession), this);
                if (gameScope.Container.TryResolve<IRuntimeWorldSessionHost>(out var worldSessionHost))
                    ConfigureWorldSwapHost(worldSessionHost);
            }
            else Debug.LogError("[DigglesWorldRuntime] GameLifetimeScope not assigned in the Inspector — NO DI " +
                                "container is built: the sim won't tick (no AI / cutscenes), click-to-move is dead, " +
                                "and no controllers are injected. Assign the scene's GameLifetimeScope to fix.");

            // Hand the per-class override registry (if assigned on the installer) to the spawner — it
            // was built in DigglesEngine before this container existed, so it can't be DI-injected.
            if (gameScope != null && _ctx?.EntitySpawner is RuntimeWorldSpawner rwsReg
                && gameScope.Container.TryResolve<DigglesClassAssetRegistry>(out var classReg))
            {
                _classRegistry = classReg;
                rwsReg.ClassRegistry = classReg;
            }
            ObserveMapBuilder(_map);

            // Gameplay camera — the RE'd Diggles camera (perspective orbit) + user pan/zoom/rotate.
            // It poses Camera.main every frame, but yields (Suppress) while a cutscene runs, when the
            // CutsceneCameraDriver drives it (DriveScripted) instead. See DigglesCameraController.
            EnsureCameraController();

            // Locomotion tuning (same knobs the demo uses — read by WalkAction /
            // MetaWalkAction). Body speed couples to model height; leg cadence to it.
            WalkAction.BodySpeedFactor = walkBodyFactor;
            WalkAction.LegCadence = legCadence;

            // At the main menu, build the menu's own backdrop world — the faithful unq_menue
            // diorama by default (systeminit.tcl:74-97, inline), or a file override via menuCampaignTclRel.
            // Not the campaign. Otherwise go straight into gameplay (fast iteration / tests).
            if (startAtMainMenu)
            {
                // Before the build: RunWorldScript gates fog-of-war creation on _atMenu — set late
                // (after the build, in StartMenuBackdrop), the menu meadow got a fog state and started
                // half-black, patchily revealed by the backdrop actors' set_fogofwar.
                _atMenu = true;
                if (!string.IsNullOrEmpty(menuCampaignTclRel)) BuildWorld(menuCampaignTclRel);
                else BuildWorld(campaignTclRel, MenuBootTcl());   // built-in menu diorama (inline tcl)

                // Resolve the menu from the DI container (registered in DigglesGameInstaller) — no scene lookup.
                MainMenuView? menu = null;
                if (gameScope != null) gameScope.Container.TryResolve(out menu);
                if (menu != null)
                {
                    menu.Show(this);
                    // Menu music arming — the systeminit.tcl:91-92 lines the boot path skips:
                    // an adaptive marker at the diorama anchor + the 'menue' primary theme
                    // (registered by adaptiveinit.tcl:211-213). Without these the menu is silent —
                    // AudioController's base layer only plays once a primary theme is set.
                    if (_interp != null)
                        try
                        {
                            _interp.Eval("adaptive_sound marker menue {32.368 39.5 10}");   // systeminit.tcl:87,91
                            _interp.Eval("adaptive_sound primary menue");                    // systeminit.tcl:92
                        }
                        catch (System.Exception e) { Debug.LogWarning($"[Menu] adaptive music arm: {e.Message}"); }
                    StartMenuBackdrop();   // cycle the random start_*.tcl backdrop cinematics
                }
                else
                {
                    Debug.Log("[DigglesWorldRuntime] startAtMainMenu is on but no MainMenuView is registered " +
                              "(assign it in DigglesGameInstaller) — showing the menu world without a menu UI.");
                }
            }
            else { if (_ctx != null) _ctx.IsStoryMgr = true; BuildWorld(campaignTclRel); }   // direct campaign load = StoryMgr world
        }

        public void BindSession(RuntimeGameSession session)
        {
            if (session == null) throw new System.ArgumentNullException(nameof(session));

            _boundSession = session;
            _engine = session.Engine;
            _interp = session.Engine.Interp;
            _ctx = session.Engine.Ctx;
            _lib = session.Engine.Lib;
            _map = _ctx.MapBuilder as RuntimeMapBuilder ?? session.Engine.Map;
            _worldRootGo = session.ContentRoot;
            _worldRoot = session.ContentRoot.transform;
            _ctx.SpawnParent = _worldRoot;
            ObserveMapBuilder(_map);
            _spawned.Clear();
            foreach (DigglesTclEntity entity in _ctx.Entities.Values)
                if (entity.GameObject != null) _spawned.Add(entity);

            if (_ctx.EntitySpawner is RuntimeWorldSpawner spawner)
                spawner.ClassRegistry = _classRegistry;

            WorldReady = _ctx.Terrain != null;
            // A session staged from a.sav archive is never the main menu — the original
            // cannot save its menu at all (and the F2 guard refuses too). IsStoryMgr alone
            // is not the menu signal: the tutorial world has no StoryMgr object yet is
            // gameplay. Save-loaded tutorials carry OriginalSaveState; native single_* tutorials
            // carry the explicit nonStoryGameplay marker across candidate activation.
            bool wasAtMenu = _atMenu;
            bool nonStoryGameplay = ReferenceEquals(session, _nonStoryGameplaySession) ||
                                    ReferenceEquals(session, _pendingWorldSwap);
            _atMenu = !nonStoryGameplay && session.OriginalSaveState == null && !_ctx.IsStoryMgr;
            // Loading from the menu must also dismiss the menu UI (the New Game button's
            // flow hides it view-side; a console `gameload` / F5 never touched it, leaving
            // the menu panel over the loaded world).
            if (wasAtMenu && !_atMenu && gameScope != null &&
                gameScope.Container.TryResolve(out MainMenuView? loadedMenu) &&
                loadedMenu != null)
            {
                loadedMenu.Hide();
            }
            _menuTrigger = 0;
            _activeCutscene = 0;
            _pendingIntroSeq = null;

            // A staged session (a.sav load) swaps ContentRoot in without re-running the boot
            // — rewire the whole presentation stack (fog/base light/backdrop/probe) for the
            // bound world. Otherwise the retired menu world's presentation stays live over
            // loaded games: fog off, 64×64 menu base light stretched over the save, the menu
            // horizon zone/clear inside campaign caves.
            WirePresentation();

            EnsureCameraController();
            if (_camController != null)
            {
                _camController.WorldRoot = _worldRoot;
                _camController.Suppress = () => _ctx != null && _ctx.SequenceActive;
                if (_ctx.Camera != null)
                {
                    _camController.SetState(
                        _ctx.Camera.Focus.x,
                        _ctx.Camera.Focus.y,
                        _ctx.Camera.Zoom,
                        _ctx.Camera.Dx,
                        _ctx.Camera.Dy);
                    if (_map != null)
                        _camController.SetWorldBounds(Vector2.zero, new Vector2(_map.Width, _map.Height));
                }
            }
        }

        private void EnsureCameraController()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return;
            if (_camController == null)
            {
                if (!_cam.TryGetComponent(out DigglesCameraController cameraController))
                    cameraController = _cam.gameObject.AddComponent<DigglesCameraController>();
                _camController = cameraController;
            }
            _camController.TargetCamera = _cam;
            _camController.WorldRoot = _worldRoot;
            _camController.Suppress = () => _ctx != null && _ctx.SequenceActive;
        }

        /// <summary>
        /// Supplies the session host used by script-driven map recreation. The scene composition
        /// resolves this automatically; EditMode hosts can bind the same activation seam explicitly.
        /// </summary>
        public void ConfigureWorldSwapHost(IRuntimeWorldSessionHost worldSessionHost)
        {
            _worldSessionHost = worldSessionHost ??
                throw new System.ArgumentNullException(nameof(worldSessionHost));
        }

        /// <summary>
        /// Supplies the gameplay camera controller explicitly. Play resolves it through
        /// Camera.main (EnsureCameraController); batch EditMode hosts must inject theirs —
        /// Camera.main does not resolve there, so BindSession's SetState/SetWorldBounds pass
        /// silently skipped and the controller kept its serialized defaults (FocusX 246.34).
        /// </summary>
        public void ConfigureCameraController(DigglesCameraController cameraController)
        {
            _camController = cameraController ??
                throw new System.ArgumentNullException(nameof(cameraController));
            if (_camController.TargetCamera != null) _cam = _camController.TargetCamera;
        }

        /// <summary>
        /// Returns the native GameScripts dispatch entry for a new-game variant.
        /// </summary>
        public static string GetBootScript(NewGameVariant variant) => variant switch
        {
            NewGameVariant.Introduction => IntroductionBootScript,
            NewGameVariant.Campaign => CampaignBootScript,
            _ => throw new System.ArgumentOutOfRangeException(nameof(variant), variant, null),
        };

        // single_Starttut.tcl:2-6 contains no StoryMgr; single_CampaignX.tcl:1-4 creates one.
        /// <summary>
        /// Returns whether the variant owns a StoryMgr. The introduction and tutorial chain does not.
        /// </summary>
        public static bool HasStoryManager(NewGameVariant variant) => variant switch
        {
            NewGameVariant.Introduction => false,
            NewGameVariant.Campaign => true,
            _ => throw new System.ArgumentOutOfRangeException(nameof(variant), variant, null),
        };

        // Called by MainMenuView's existing campaign button. The variant overload is the menu
        // sub-screen binding seam; this parameterless entry preserves the current campaign law.
        public void NewGame() => NewGame(NewGameVariant.Campaign);

        /// <summary>
        /// Starts the selected native single-player boot path.
        /// </summary>
        public void NewGame(NewGameVariant variant)
        {
            _ = GetBootScript(variant);
            if (_newGameStarting) return;
            _newGameStarting = true;
            WorldReady = false;
            _atMenu = false;                       // stop cycling backdrop cutscenes
            if (_ctx != null) _ctx.IsStoryMgr = HasStoryManager(variant);
            _nonStoryGameplaySession = variant == NewGameVariant.Introduction
                ? _boundSession
                : null;
            StopCutscene();
            if (startAtMainMenu && _ctx != null)
            {
                _ctx.ClearDeferredSequencePresentation();
                _ctx.SequencePresentationDrain = false;
            }
            // New-game clock reset — before the rebuild, so every obj_init timer is armed against a
            // fresh clock. The original's game clock starts at 0 per game: zwerg.tcl banks on it with
            // the absolute `-attime 3` init timer (:183), the founder windows (`[gethours]<2.0`
            // gender-balance z_methods.tcl:219, `<1.0` marriage pairing:263) and the relative
            // `-attime [expr {[gettime]+2}]` timers (:186-187). Without the reset, menu time leaks
            // into the campaign (founder branches silently skipped after ~2 menu "hours").
            // TimeFactor/ClockRunning also reset — a hard-stopped slow-mo backdrop must not leak
            // its gametime factor into the campaign (FUN_0061ece0 state is per game thread).
            if (_ctx?.Sim != null)
            {
                _ctx.Sim.GameTime = 0.0;
                _ctx.Sim.TimeFactor = 1.0;
                _ctx.Sim.ClockRunning = true;
            }
            StartCoroutine(NewGameRoutine(variant));
        }

        private IEnumerator NewGameRoutine(NewGameVariant variant)
        {
            // Publish the menu-hide/loading state before any teardown or campaign work begins.
            yield return null;
            try
            {
                if (startAtMainMenu || variant == NewGameVariant.Introduction)
                {
                    long teardownStarted =
                        Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
                    using (Diggles.Runtime.Diagnostics.PerfTrace.Section(
                               "world.boot.teardown",
                               50))
                    {
                        TearDownWorld();
                    }
                    Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                        "campaign.teardown",
                        teardownStarted);
                    // Give the player loop one heartbeat between releasing the menu world and
                    // entering the still-synchronous Tcl/model build.
                    yield return null;
                    if (variant == NewGameVariant.Introduction)
                    {
                        // The native introduction is plain Tcl, not a StoryMgr level:
                        // single_Starttut.tcl:2-6 -> startseqs.tcl -> set_context tutorial.
                        BuildWorld(
                            campaignTclRel,
                            ReadBootScriptContent(IntroductionBootScript),
                            requireRuntimeDefinitions: true);
                    }
                    else
                    {
                        BuildWorld(campaignTclRel);
                    }
                    gameScope?.BindSaveBackendToCurrentSession(clearBaseline: true);
                }
                else
                {
                    WorldReady = _ctx?.Terrain != null;
                }
                // Stage the opening cinematic (faithful: Trigger_Urw_006 → urw_3 on entry) — deferred,
                // never immediate. The original's observable boot timeline:
                // t=0 the level is up and live — the player watches the camp; the founders idle
                // in place (zwerg.tcl `state idle` ends every pass with set_idle_anim +
                // `action this wait 1 {state_enable this}`,:316-318; no walk is reachable
                // before idletimeout>5, and the virgin camp has no work →
                // state_work_dispatch_proc falls to work_idle, z_work_states.tcl:164-168).
                // t=3 every dwarf's evt_timer0 init fires on screen (zwerg.tcl:183 `-attime 3`
                // → z_methods init: gender/clothes/hair/frau-reskin) — player-visible
                // "gender flips a few seconds in".
                // t=5 Trigger_Urw_006's own timer matures (`-attime [expr [gettime]+5]`,
                // Trigger_Urwald.tcl:928) and spawns the urw_3 proximity trigger (range 40,
                // Zwerg, owner 0 —:934-940); the founders are at the camp, so it fires at
                // once → sequencer_activate → set_sequence 1.
                // t≈11 urw_3 itself beams all five actors into the WiggleStart pens (urw_3.tcl:
                // 19, shipped) — the sequence presets actor positions, masking any drift.
                // An earlier instant RunBootWindow compressed t=0..5 into one frame: the flip looked
                // instant and the AI got 5 hidden seconds to scatter the founders. Update fires the
                // staged intro when the live clock reaches the mark. (The trigger engine
                // (TriggerService) now arms Trigger_Urw_006's timer+proximity pair too — Update's
                // mutual-exclusion guard lets whichever path fires first win and disarms the other.)
                if (variant == NewGameVariant.Campaign &&
                    !string.IsNullOrEmpty(campaignIntroCutscene))
                {
                    // Only when this world actually authored the intro's trigger — a mod
                    // campaign without Trigger_Urw_006 must not play the original intro.
                    if (WorldSpawnedIntroTrigger(campaignIntroCutscene))
                        StageIntro(campaignIntroCutscene);
                    else
                        Debug.Log($"[DigglesWorldRuntime] intro '{campaignIntroCutscene}' not staged — " +
                                  "the built world has no authoring trigger for it (mod campaign?).");
                }
            }
            finally
            {
                _newGameStarting = false;
            }
        }

        // ---- faithful intro pre-roll (Trigger_Urw_006's `-attime [gettime]+5` arm) ----
        public const double IntroPreRollSeconds = 5.0;   // Trigger_Urwald.tcl:928
        string? _pendingIntroSeq;     // staged cinematic; fires when GameTime reaches _pendingIntroAt
        double _pendingIntroAt;

        void StageIntro(string sequenceName)
        {
            _pendingIntroSeq = sequenceName;
            _pendingIntroAt = (_ctx?.Sim?.GameTime ?? 0.0) + IntroPreRollSeconds;
        }

        // The staged pre-roll is a fallback double of the authored trigger chain (urw_3 ↔
        // Trigger_Urw_006, spawned by the original Campaign.tcl's start template). A world that
        // never spawned that trigger must not inherit the original intro: a mod campaign that
        // replaces Campaign.tcl wholesale (TrollQuest) could otherwise get urw_3 played over its
        // own map. Resolve the sequence's owner class through the parsed
        // sequence→trigger map and require a live entity of it in the built world.
        public bool WorldSpawnedIntroTrigger(string sequenceName)
        {
            if (_ctx == null || string.IsNullOrEmpty(sequenceName)) return false;
            _seqTriggerMap ??= BuildSequenceTriggerMap();
            if (!_seqTriggerMap.TryGetValue(sequenceName, out var triggerClass)
                && !SequenceTriggers.TryGetValue(sequenceName, out triggerClass))
                return false;
            foreach (var entity in _ctx.Entities.Values)
                if (entity.Class?.Name == triggerClass) return true;
            return false;
        }

        // The faithful built-in menu diorama (systeminit.tcl:74-97, minus Trigger_StartScreen — the port
        // drives the random start_*.tcl loop itself in StartMenuBackdrop). unq_menue.tcl defines its own
        // MapTemplateSet proc (then we invoke it); same call+MapTemplateSet pattern as the campaign's
        // first_level. set_view = the fixed menu camera. Run via BuildWorld's inline path.
        static string MenuBootTcl() =>
            "map create 64 64\n" +
            "call templates/unq_menue.tcl\n" +
            "MapTemplateSet 25 28\n" +
            "call templates/urw_gng_021_a.tcl\n" +
            "MapTemplateSet 21 40\n" +
            "call templates/urw_gng_022_a.tcl\n" +
            "MapTemplateSet 45 40\n" +
            "set_view 32.368 40.858 1.38 -0.355 0.165\n";

        // Menu backdrop: cycle a random Scripts/sequences/Menue/start_*.tcl cinematic, re-picking when
        // one ends (faithful Trigger_StartScreen). A non-empty menuCutscene forces that single one.
        void StartMenuBackdrop()
        {
            _atMenu = true;
            if (_ctx != null) _ctx.IsStoryMgr = false;   // menu backdrop is not a StoryMgr world (red clan hats)
            _menuSeqs.Clear();
            if (!string.IsNullOrEmpty(menuCutscene)) _menuSeqs.Add(menuCutscene);
            else
            {
                try
                {
                    var dir = Path.Combine(dataRoot, "Scripts", "sequences", "Menue");
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, "start_*.tcl"))
                            if (new FileInfo(f).Length >= 100)   // skip the empty "zur freien Verfuegung" stubs
                                _menuSeqs.Add(Path.GetFileNameWithoutExtension(f));
                }
                catch { /* none → static backdrop */ }
            }
            if (_menuSeqs.Count == 0)
                Debug.Log("[DigglesWorldRuntime] no menu backdrop cutscenes found (Scripts/sequences/Menue/start_*.tcl) — static menu.");
            PlayRandomMenuCutscene();
        }

        void PlayRandomMenuCutscene()
        {
            var next = NextMenuBackdrop();
            if (next.Length > 0) PlayMenuCutscene(next);
        }

        // The Trigger_StartScreen pick (Trigger_Urwald.tcl:45-55): a random scene, never the same
        // one twice in a row. Shared by the C# watchdog path (first play / fallback) and the
        // faithful Tcl destroy_overload chain (the `_menu_next_backdrop` native below).
        string NextMenuBackdrop()
        {
            if (!_atMenu || _menuSeqs.Count == 0) return "";
            int i = _menuSeqs.Count == 1 ? 0 : UnityEngine.Random.Range(0, _menuSeqs.Count);
            if (_menuSeqs.Count > 1 && i == _lastMenuSeq) i = (i + 1) % _menuSeqs.Count;
            _lastMenuSeq = i;
            _menuIdleT = 0f;
            return _menuSeqs[i];
        }

        // The menu-backdrop trigger class — the faithful Trigger_StartScreen shape
        // (Trigger_Urwald.tcl:4-58 + sequencer.tcl:2153-2173):
        // • `run` clears followaction_list/eyefocus_list (Trigger_Urwald.tcl:43-44 — the guard
        // against a previous scene's actionlist replaying on the next scene's actors: a stale
        // `{{anim discoc} loop}` popped by seq_enable makes the new actor "dance" instead of
        // running its beat), forces `gametime factor 1` (:56) and activates.
        // • `destroy_overload` chains the next backdrop synchronously: natively shut_sequencer →
        // destroy → destroy_overload → run → sequencer_activate → set_sequence 1 is one Tcl
        // call chain, so the world never executes an un-frozen tick between menu scenes. The
        // port's old empty overload left a ≥1 s live-sim gap (the C# watchdog) in which any
        // leftover actor's queued obj_init events (troll.tcl:98 evt_timer0 → state idle →
        // walk_random) matured and fired — the "troll walks away mid-scene" leak (the freeze
        // gate FUN_005d38c0:185-187 only holds while the set_sequence byte is up).
        // `_menu_next_backdrop` (C#) supplies the pick; "" (not at the menu / New Game
        // pressed) parks the trigger exactly like before.
        public const string MenuBackdropSeqTcl =
            "def_class _MenuBackdrop_Seq none dummy 0 {} {\n" +
            "  call scripts/classes/story/sequencer.tcl\n" +
            "  obj_init { call scripts/classes/story/sequencer.tcl }\n" +
            "  method run {} {\n" +
            "    global followaction_list eyefocus_list\n" +
            "    set followaction_list [list]\n" +
            "    set eyefocus_list [list]\n" +
            "    gametime factor 1\n" +
            "    sequencer_activate\n" +
            "  }\n" +
            "  method destroy_overload {} {\n" +
            "    set next \"\"\n" +
            "    catch { set next [_menu_next_backdrop] }\n" +
            "    if {$next != \"\"} {\n" +
            "      call_method this set_sequencescript $next\n" +
            "      call_method this run\n" +
            "    }\n" +
            "  }\n" +
            "}";

        // The faithful menu playback (Trigger_StartScreen `run`, Trigger_Urwald.tcl:39-58):
        // reuse one persistent trigger, no actor preset (the original menu trigger's actor list
        // is rebuilt empty by sequencer_activate each play — every start_* script does its own
        // `sq_actor find` after its own summons), `gametime factor 1`, then sequencer_activate.
        // Scene-to-scene replay runs inside shut_sequencer via destroy_overload (see
        // MenuBackdropSeqTcl); the C# watchdog remains only as the first-play driver and a
        // fallback for scripts that never enter sequence mode (empty stubs / parse failures).
        int PlayMenuCutscene(string sequenceName)
        {
            if (_interp == null || _ctx == null || string.IsNullOrEmpty(sequenceName)) return 0;
            try
            {
                // The pick native the Tcl chain calls from destroy_overload. Idempotent re-register.
                _interp.RegisterCommand("_menu_next_backdrop", (i, a) =>
                {
                    var next = NextMenuBackdrop();
                    if (next.Length > 0)
                        Debug.Log($"[MenuBackdrop] chaining '{next}' (destroy_overload — same-tick faithful loop)");
                    return next;
                });
                if (!_ctx.Classes.ContainsKey("_MenuBackdrop_Seq"))
                    _interp.Eval(MenuBackdropSeqTcl);
                if (_menuTrigger == 0 || !_ctx.Entities.ContainsKey(_menuTrigger))
                {
                    // Position is irrelevant for start_*: every pen derives from the
                    // Info_Pos_Zwerg/Tmp markers via Getobjpos (range-100 query reaches them
                    // from the origin).
                    var h = _interp.Eval("new _MenuBackdrop_Seq \"\" \"0 0 0\" \"0 0 0\"").Trim();
                    _menuTrigger = int.TryParse(h, out var mh) ? mh : 0;
                }
                if (_menuTrigger == 0) return 0;
                _interp.Eval($"call_method {_menuTrigger} set_sequencescript {sequenceName}");
                _interp.Eval("gametime factor 1");   // Trigger_StartScreen run, Trigger_Urwald.tcl:56
                _interp.Eval($"call_method {_menuTrigger} run");
                _activeCutscene = _menuTrigger;
                Debug.Log($"[MenuBackdrop] playing '{sequenceName}' (persistent trigger {_menuTrigger})");
                return _menuTrigger;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MenuBackdrop] PlayMenuCutscene('{sequenceName}') failed: {e.Message}");
                return 0;
            }
        }

        // Tear down the live world (entities + terrain + nav) and build a fresh one in the same
        // scene/interp. The Tcl classes stay loaded (they persist); only instances + terrain reset.
        public void RebuildWorld(string campaignRel)
        {
            TearDownWorld();
            BuildWorld(campaignRel);
            gameScope?.BindSaveBackendToCurrentSession(clearBaseline: true);
        }

        void TearDownWorld()
        {
            if (_ctx != null)
            {
                foreach (var e in _ctx.Entities.Values)
                {
                    _ctx.Sim?.Scheduler.UnsetAll(e.Handle);
                    // The registry side of the timer pair — without this, dead menu-world
                    // registrations linger forever (the save capture rightly rejects a
                    // registry entry whose scheduler counterpart is gone).
                    _ctx.Timers.DropHandle(e.Handle);
                    if (e.GameObject != null) DestroyImmediate(e.GameObject);
                }
                _ctx.Entities.Clear();
                _ctx.EntitiesVersion++;   // liveness probes in DigglesGameSim key off this
                _ctx.ClearDeferredSequencePresentation();
                _ctx.SequencePresentationDrain = false;
                _ctx.MapDecoSeeds.Clear();
                _ctx.MapDecoSeedLodCursor = 0;
                _ctx.SelectedHandle = 0;
                _ctx.Terrain = null;
                if (_ctx.EntitySpawner is RuntimeWorldSpawner deadRws) deadRws.Terrain = null;
                _ctx.NavService = null;
                _ctx.SaveGames = null;
                // The dead world's place locks die with it. They store absolute expiry times, and
                // NewGame resets the game clock to 0 — stale menu-world locks would otherwise
                // resurrect as live for their full duration inside the fresh campaign (the menu
                // meadow and the campaign start meadow share coordinate ranges).
                _ctx.PlaceLocks.Clear();
                _ctx.HorizonRegions.Clear();
                _ctx.Audio?.MusicMarkers.Clear();
            }
            _menuTrigger = 0;   // the persistent menu trigger died with the world
            _pendingIntroSeq = null;   // a staged intro belongs to the world that staged it
            _spawned.Clear();
            if (_worldRootGo != null)
            {
                for (int i = _worldRootGo.transform.childCount - 1; i >= 0; i--)
                {
                    var child = _worldRootGo.transform.GetChild(i).gameObject;
                    // Keep the presenter-owned cutscene elf model across rebuilds — ElfPresenter spawns it
                    // once (hidden) in its IStartable; destroying it leaves a dangling ref and the elf
                    // vanishes from all post-New-Game cutscenes.
                    if (child.name == "CutsceneElf") continue;
                    DestroyImmediate(child);
                }
                RemoveWorldComponent<Diggles.Runtime.World.DiggableTerrain>();
                RemoveWorldComponent<Diggles.Runtime.Navigation.NavWorldService>();
                RemoveWorldComponent<Diggles.Runtime.Navigation.NavWorldGizmo>();
                // Backdrop presenter: its scene children died with the root's child sweep above;
                // Clear additionally resets the horizon-fog shader globals + the camera clear
                // colour it may have overridden (a stale zone clear would tint the next world).
                var deadBackdrop = _worldRootGo.GetComponent<Diggles.Runtime.World.BackdropPresenter>();
                if (deadBackdrop != null) deadBackdrop.Clear();
            }
            // Fresh map builder (the old one accumulated the previous map's templates).
            _map = new Diggles.Runtime.World.RuntimeMapBuilder(dataRoot, System.IO.Path.Combine(dataRoot, bdbRel));
            if (_ctx != null) _ctx.MapBuilder = _map;
            ObserveMapBuilder(_map);
            WorldReady = false;
        }

        void RemoveWorldComponent<T>() where T : Component
        {
            var c = _worldRootGo != null ? _worldRootGo.GetComponent<T>() : null;
            if (c != null) DestroyImmediate(c);
        }

        private void ObserveMapBuilder(RuntimeMapBuilder? mapBuilder)
        {
            if (ReferenceEquals(_observedMap, mapBuilder)) return;
            if (_observedMap != null)
                _observedMap.WorldRecreationRequested -= OnWorldRecreationRequested;
            _observedMap = mapBuilder;
            if (_observedMap != null)
                _observedMap.WorldRecreationRequested += OnWorldRecreationRequested;
        }

        private void OnWorldRecreationRequested(RuntimeMapBuilder mapBuilder)
        {
            if (!ReferenceEquals(mapBuilder, _map) || _pendingWorldSwap != null) return;
            if (_worldSessionHost == null)
                throw new System.InvalidOperationException(
                    "A world session host is required before an emitted Tcl map can be recreated.");
            if (_boundSession == null || _engine == null || _ctx == null || _worldRootGo == null)
                throw new System.InvalidOperationException(
                    "The active runtime session must be bound before an emitted Tcl map can be recreated.");
            Transform? stableRoot = _worldRootGo.transform.parent;
            if (stableRoot == null)
                throw new System.InvalidOperationException(
                    "The active content root must have a stable parent before an emitted Tcl map can be recreated.");

            var candidateRoot = new GameObject(
                $"WorldContent.ScriptSwap.{CompletedWorldSwapCount + 1}.Candidate");
            candidateRoot.transform.SetParent(stableRoot, false);
            candidateRoot.SetActive(false);
            // RuntimeWorldSessionHost.CreateCandidate is the save-load entry and intentionally
            // creates a fresh engine. Here the deferred Tcl batch is already executing in the live
            // interpreter, so its candidate wraps that same engine while retaining the identical
            // Candidate -> Validated -> ActivateCandidate -> BindSession lifecycle.
            _pendingWorldSwap = new RuntimeGameSession(
                _engine,
                candidateRoot,
                RuntimeGameSessionState.Candidate);

            _ctx.SpawnParent = candidateRoot.transform;
            _ctx.Terrain = null;
            _ctx.NavService = null;
            _ctx.FogOfWar = null;
            _ctx.BaseLight = null;
            _ctx.TunnelTorches = null;
            // A save-load candidate gets fresh world registries with its new engine. This
            // same-interpreter candidate must reset the equivalent world-local state before
            // oberwelt.tcl stamps and registers its replacement data.
            _ctx.Fluids.Reset();
            _ctx.Energy.ResetTransientState();
            _ctx.MapDecoSeeds.Clear();
            _ctx.MapDecoSeedLodCursor = 0;
            _ctx.PlaceLocks.Clear();
            _ctx.HorizonRegions.Clear();
            _ctx.Audio?.MusicMarkers.Clear();
            if (_ctx.EntitySpawner is RuntimeWorldSpawner spawner)
            {
                spawner.Terrain = null;
                spawner.FogPresenter = null;
                spawner.FogState = null;
            }
            _menuTrigger = 0;
            _activeCutscene = 0;
            _pendingIntroSeq = null;
            _spawned.Clear();
            WorldReady = false;
        }

        /// <summary>
        /// Emits and activates a Tcl map recreated after the prior terrain was already live.
        /// Returns false when no script-driven swap is pending.
        /// </summary>
        public bool FlushPendingWorldSwap()
        {
            RuntimeGameSession? candidate = _pendingWorldSwap;
            if (candidate == null) return false;
            if (_worldSessionHost == null || _ctx == null || _map == null)
                throw new System.InvalidOperationException(
                    "The pending Tcl world recreation has no bound activation services.");
            IRuntimeWorldSessionHost worldSessionHost = _worldSessionHost;
            DigglesTclContext context = _ctx;
            RuntimeMapBuilder mapBuilder = _map;
            if (!mapBuilder.WorldRecreationPending)
                throw new System.InvalidOperationException(
                    "The pending Tcl world candidate has no recreated map arrays.");

            mapBuilder.EmitTerrain(candidate.ContentRoot);
            var terrain = candidate.ContentRoot.GetComponent<DiggableTerrain>();
            var navService = candidate.ContentRoot.GetComponent<Diggles.Runtime.Navigation.NavWorldService>();
            if (terrain == null || navService == null)
                throw new System.InvalidOperationException(
                    "The recreated Tcl world did not emit terrain and navigation.");

            context.Terrain = terrain;
            context.NavService = navService;
            RetireCurrentWorldPresentation();
            candidate.MarkValidated();

            RuntimeGameSession retired = worldSessionHost.ActivateCandidate(candidate);
            if (!ReferenceEquals(_boundSession, candidate)) BindSession(candidate);
            _nonStoryGameplaySession = candidate;
            mapBuilder.CompleteWorldRecreation();
            _pendingWorldSwap = null;
            retired.Dispose();

            DriveSpawned();
            (context.EntitySpawner as RuntimeWorldSpawner)?.UploadClassLights();
            gameScope?.BindSaveBackendToCurrentSession(clearBaseline: true);
            CompletedWorldSwapCount++;
            WorldReady = true;
            return true;
        }

        private void RetireCurrentWorldPresentation()
        {
            RemoveWorldComponent<Diggles.Runtime.World.FogOfWarOverlay>();
            RemoveWorldComponent<Diggles.Runtime.World.FogOfWarPresenter>();
            RemoveWorldComponent<Diggles.Runtime.World.BaseLightPresenter>();
            RemoveWorldComponent<Diggles.Runtime.World.BackdropPresenter>();
            RemoveWorldComponent<Diggles.Runtime.World.FluidPresenter>();
            RemoveWorldComponent<Diggles.Runtime.World.RimProbeTool>();
            RemoveWorldComponent<Diggles.Runtime.World.NavProbeTool>();
            RemoveWorldComponent<Diggles.Runtime.World.DigMarkOverlay>();
            RemoveWorldComponent<Diggles.Runtime.Navigation.NavWorldGizmo>();
            RemoveWorldComponent<Diggles.Runtime.Navigation.NavWorldService>();
            RemoveWorldComponent<Diggles.Runtime.World.DiggableTerrain>();
        }

        // Build the Urwald world: load real classes, run the campaign spawn, ensure anims, go
        // sim-ready. Idempotent. Called from Start (no-menu mode) or the menu's "New Game".
        // (gui_new_game would route here once the StoryMgr boot chain lands.)
        public void BuildWorld(
            string campaignRel,
            string? inlineScript = null,
            bool requireRuntimeDefinitions = false)
        {
            if (WorldReady) return;
            long buildStarted = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();

            // Load the real class system once — classes persist across world rebuilds, so a
            // menu→game (or replay) rebuild must not reload them. Gives `new <Class>` its true
            // obj_init / animsets / idle table / states (runs before spawn).
            if ((loadRealClasses || requireRuntimeDefinitions) && !_realClassesLoaded)
            {
                long classLoadStarted =
                    Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
                using (Diggles.Runtime.Diagnostics.PerfTrace.Section("classes.load", 100))
                {
                    _engine!.LoadRuntimeDefinitions();
                }
                Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                    _atMenu ? "menu.classes" : "campaign.classes",
                    classLoadStarted);
                _realClassesLoaded = true;
            }

            // Faithful idle plumbing (global procs/tables) — loaded once so any dwarf can run seq_idle.
            if (driveFaithfulIdle && !_idleLoaded) { LoadIdlePlumbing(); _idleLoaded = true; }

            try
            {
                if (inlineScript != null)
                {
                    if (_atMenu) RunWorldScriptCore(inlineScript);
                    else RunWorldScript(inlineScript);
                }
                else if (mode == SpawnMode.CampaignTcl) RunCampaign(campaignRel);
                else RunOneOfEach();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DigglesWorldRuntime] spawn failed: {e.Message}\n{e.StackTrace}");
            }

            long finalizeStarted = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            // Walk/run/turn clips resolve through the class animsets (set_class_animset
            // from z_anims.tcl). In CampaignTcl the campaign may not have loaded them
            // for the dwarf class, so ensure it here — the controllers hold the class's
            // Animsets dict by reference, so populating it now reaches already-spawned
            // dwarves. (Mirrors what DigglesTclAnimDemo does before spawning.)
            EnsureClassAnims("Zwerg");
            EnsureClassAnims("Frau");

            DriveSpawned();
            Debug.Log($"[DigglesWorldRuntime] spawned {_spawned.Count} entities (mode={mode})");

            // Prewarm the female dwarf model: auto_choose_gender runs at
            // game-time +3 s (zwerg.tcl init → set_alternateanimdb frau) and the first female
            // re-skin parses frau.3db (30.8 MB file → tens of MB managed) synchronously inside a
            // sim step — a visible mid-play freeze in the live pre-intro window. Parsing it here
            // moves that cost into the loading phase the player already waits through. Boot-time
            // only; menu dioramas (no gender init) skip it.
            if (mode == SpawnMode.CampaignTcl && _ctx?.EntitySpawner is RuntimeWorldSpawner rws)
            {
                long t0 = Diggles.Runtime.Diagnostics.PerfTrace.Now();
                try { rws.Library.GetClip("frau", "standard"); }
                catch (System.Exception e) { Debug.LogWarning($"[DigglesWorldRuntime] frau prewarm failed: {e.Message}"); }
                Diggles.Runtime.Diagnostics.PerfTrace.LogIfSlow("model.prewarm(frau)", t0, 0);
            }
            WorldReady = true;
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                _atMenu ? "menu.finalize" : "campaign.finalize",
                finalizeStarted);
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                _atMenu ? "menu.build" : "campaign.build",
                buildStarted);

            // In-Play cutscene test panel (opt-in).
            if (showCutsceneTester)
                gameObject.AddComponent<Tools.CutsceneTester>().Runtime = this;

            // In-Play coordinate probe (opt-in) — diagnose native↔world position shifts.
            if (showCoordProbe)
            {
                var probe = gameObject.AddComponent<Tools.DigglesCoordProbe>();
                probe.Runtime = this;
            }

            // Auto-play a cutscene once the world is up: a pending one from a Reload+Replay
            // takes priority, else the inspector field. (CutsceneTester drives both.)
            string? auto = s_pendingCutscene ?? (string.IsNullOrEmpty(autoPlayCutscene) ? null : autoPlayCutscene);
            s_pendingCutscene = null;
            if (!string.IsNullOrEmpty(auto))
            {
                // Same contract as NewGame's intro: a fresh world's init timers (zwerg.tcl:183
                // -attime 3) must mature in live time before sequence mode engages — stage the
                // cutscene at +5 s (Trigger_Urw_006's arm delay) and let Update fire it. The
                // tester therefore sees the original's real pre-roll: idle camp, t=3 dress-up,
                // then the cinematic — never an instant flip or a frozen-undressed cast.
                StageIntro(auto!);
            }
        }

        // ---- cutscene testing API (used by CutsceneTester) ----

        // Classes whose spawned instances are preset as the cutscene's actors.
        static readonly string[] ActorClasses = { "Zwerg", "Frau", "PseudoZwerg", "Wuker", "Troll", "Drachenbaby" };

        // Stage a cutscene into the currently-loaded world: spawn a sequencer trigger, preset the
        // live dwarves/actors, point it at <sequenceName> and activate it. Returns the trigger
        // handle (0 on failure). The DigglesCutscenePresenter renders it (set_sequence gates the
        // camera). Does not reload the world — use ReloadAndPlay for a fresh environment.
        public int PlayCutscene(string sequenceName)
        {
            if (_interp == null || _ctx == null || string.IsNullOrEmpty(sequenceName)) return 0;
            StopCutscene();
            // Turn on tracing so the console shows what should happen: the sequencer's own slog
            // (set seq_log_enabled below) logs each command it processes (sq_camera/do_text/
            // set_viewpos …) and the presenter logs what it renders (camera/subtitle).
            CutsceneTrace.Enabled = true;
            DigglesTclEngine.VerboseScriptLog = true;
            // Open a dumpable file log for this run. CutsceneTrace mirrors every [cutscene] line into
            // it and dbg.tcl's dbg_actor/dbg_log write straight to it; per-handle gating keeps the
            // gnomes' AI `log` spam out of the file, so the dump stays focused.
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin("diggles_cutscene.log");
            Debug.Log($"[CutsceneTest] trace → {System.IO.Path.GetFullPath("diggles_cutscene.log")}");
            try
            {
                if (!_ctx.Classes.ContainsKey("_CutsceneTest_Seq"))
                    _interp.Eval(
                        "def_class _CutsceneTest_Seq none dummy 0 {} {\n" +
                        "  call scripts/classes/story/sequencer.tcl\n" +
                        "  obj_init { call scripts/classes/story/sequencer.tcl ; set seq_log_enabled 1 }\n" +
                        "  method run {} { sequencer_activate }\n" +
                        "}");

                // Gather live actors + a spawn position near them (the trigger pos = TriggerPos pen).
                var actors = new List<int>();
                Vector3 pos = Vector3.zero; bool gotPos = false;
                foreach (var e in _ctx.Entities.Values)
                {
                    if (e.GameObject == null || e.Class == null) continue;
                    if (System.Array.IndexOf(ActorClasses, e.Class.Name) < 0) continue;
                    actors.Add(e.Handle);
                    if (!gotPos) { pos = e.Position; gotPos = true; }
                    Vector3 wp = e.GameObject.transform.position;
                    CutsceneTrace.Log($"actor[{actors.Count - 1}] = {e.Class.Name}#{e.Handle} native={e.Position} world={wp}");
                    if (actors.Count >= 8) break;
                }

                // The cutscene's pens are all relative to the trigger's own position (sq_pen set
                // Lagerplatz TriggerPos; TriggerPos = get_pos this). Using the first actor as the
                // origin shifts every pen off the meadow (the Wiggles beam into a wall). Prefer the
                // real authored trigger entity for this sequence if the campaign spawned it.
                Vector3 triggerPos = pos;
                if (TryFindSequenceTrigger(sequenceName, out var trigEnt) && trigEnt != null)
                {
                    triggerPos = trigEnt.Position;
                    CutsceneTrace.Log($"[POSDBG] using REAL trigger {trigEnt.Class?.Name}#{trigEnt.Handle} native={triggerPos} (was firstActor={pos})");
                }
                else
                {
                    // No spawned trigger → we anchor on the first actor, which shifts every
                    // TriggerPos-relative pen (this is the "everything far-left" symptom). Warn loudly.
                    Debug.LogWarning($"[CutsceneTest] '{sequenceName}': no spawned trigger found — anchoring on the " +
                        $"first actor (native {pos}). Positions may be shifted. Load the campaign with " +
                        $"loadRealClasses so the sequence's Trigger_* is spawned.");
                }
                pos = triggerPos;
                DumpPosDebug(sequenceName, actors, pos);
                CutsceneTrace.Log($"'{sequenceName}': {actors.Count} actors, triggerPos(native)={pos}. " +
                                  (actors.Count == 0 ? "NO ACTORS — load a campaign with dwarves (loadRealClasses + CampaignTcl)." : ""));

                string posStr = $"{pos.x.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"{pos.y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                $"{pos.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                _interp.Eval($"set __ct_seq [new _CutsceneTest_Seq \"\" \"{posStr}\" \"0 0 0\"]");
                if (actors.Count > 0)
                    _interp.Eval($"call_method $__ct_seq preset_actors [list {string.Join(" ", actors)}]");
                _interp.Eval($"call_method $__ct_seq set_sequencescript {sequenceName}");
                _interp.Eval("call_method $__ct_seq run");
                _activeCutscene = int.TryParse(_interp.Eval("set __ct_seq"), out var h) ? h : 0;
                Debug.Log($"[CutsceneTest] playing '{sequenceName}' (trigger {_activeCutscene}, {actors.Count} actors)");
                return _activeCutscene;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[CutsceneTest] PlayCutscene('{sequenceName}') failed: {e.Message}");
                return 0;
            }
        }

        // Hardcoded fallback for the sequence→trigger map (used if class bodies aren't loaded, e.g.
        // stub mode). With loadRealClasses on, the generic scan below covers everything automatically.
        static readonly Dictionary<string, string> SequenceTriggers = new()
        {
            { "urw_3", "Trigger_Urw_006" },
        };

        // Auto-built sequence→trigger-class map, parsed once from the loaded Trigger_* class bodies.
        Dictionary<string, string>? _seqTriggerMap;
        static readonly System.Text.RegularExpressions.Regex s_seqScriptRx =
            new("set\\s+sequencescript\\s+\"?([A-Za-z0-9_]+)\"?", System.Text.RegularExpressions.RegexOptions.Compiled);

        // The trigger object's position = TriggerPos, which all of a cutscene's pens are relative to
        // (sequencer init_pens: `set TriggerPos [get_pos this]`). Each Trigger_* class body contains
        // `set sequencescript "<name>"`, so we derive sequence→trigger generically — any cinematic
        // resolves to its real authored trigger without a hand-maintained table.
        Dictionary<string, string> BuildSequenceTriggerMap()
        {
            var map = new Dictionary<string, string>();
            if (_ctx == null) return map;
            foreach (var kv in _ctx.Classes)
            {
                var cls = kv.Value;
                if (cls == null || string.IsNullOrEmpty(cls.Name) || !cls.Name.StartsWith("Trigger")) continue;
                ScanSeqScript(cls.Body, cls.Name, map);
                foreach (var h in cls.EventHandlers.Values) ScanSeqScript(h, cls.Name, map);
            }
            return map;
        }

        static void ScanSeqScript(string? code, string clsName, Dictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(code)) return;
            foreach (System.Text.RegularExpressions.Match m in s_seqScriptRx.Matches(code))
            {
                var seq = m.Groups[1].Value;
                if (!map.ContainsKey(seq)) map[seq] = clsName;   // first definer wins
            }
        }

        // Find the campaign-spawned trigger entity whose position the sequence's pens key off.
        bool TryFindSequenceTrigger(string sequenceName, out DigglesTclEntity? trigger)
        {
            trigger = null;
            if (_ctx == null) return false;
            if (!SequenceTriggers.TryGetValue(sequenceName, out var trigClass) || string.IsNullOrEmpty(trigClass))
            {
                _seqTriggerMap ??= BuildSequenceTriggerMap();
                _seqTriggerMap.TryGetValue(sequenceName, out trigClass);
            }
            if (string.IsNullOrEmpty(trigClass)) return false;
            foreach (var e in _ctx.Entities.Values)
                if (e.Class != null && e.Class.Name == trigClass) { trigger = e; return true; }
            return false;
        }

        // Filterable position-pipeline dump ([POSDBG]) for diagnosing cutscene position shifts.
        // Reports every Trigger_* entity (to spot the real authored trigger), the chosen trigger
        // origin, and the nav graph's native coverage bounds (so we can see if the pens fall inside
        // the walkable area). Grep the console for "[POSDBG]".
        void DumpPosDebug(string sequenceName, List<int> actors, Vector3 chosenTriggerPos)
        {
            if (_ctx == null) return;
            Debug.Log($"[POSDBG] === sequence '{sequenceName}' === chosenTriggerPos(native)={chosenTriggerPos} actors={actors.Count}");
            int trig = 0;
            foreach (var e in _ctx.Entities.Values)
            {
                if (e.Class == null || e.Class.Name == null) continue;
                if (!e.Class.Name.StartsWith("Trigger", System.StringComparison.Ordinal)) continue;
                var wp = e.GameObject != null ? e.GameObject.transform.position : Vector3.zero;
                Debug.Log($"[POSDBG] trigger {e.Class.Name}#{e.Handle} native={e.Position} world={wp}");
                if (++trig >= 40) { Debug.Log("[POSDBG] (more triggers truncated)"); break; }
            }
            // Nav coverage: the faithful NavWorld (+0x3c walkability + FUN_005a6350 tile graph).
            var nav = _ctx.NavService;
            var navWorld = nav != null ? nav.World : null;
            if (navWorld != null)
                Debug.Log($"[POSDBG] NavWorld {navWorld.Width}x{navWorld.Height} tiles, " +
                          $"walkable +0x3c sub-cells={navWorld.WalkableSubCellCount()}");
            else Debug.Log("[POSDBG] NavWorld not available");
        }

        // Stop the active cutscene — the faithful skip: set the native
        // break flag (ESC → DAT_00772400+0x8d, FUN_0052c510:353-355), then pump the sim so the
        // sequencer's own eval_sequence drain runs to completion now (sequencer.tcl:228-239:
        // `set is_breaked 1; while {[parse_next 1]} {}; shut_sequencer`). The drain executes every
        // remaining non-'-' line with the per-proc is_breaked reductions — the script's own
        // `+sq_object delete all` / `+gametime factor 1` / `+do_elf hide` cleanup — and
        // shut_sequencer restores the camera viewpos, re-enables the actors
        // (set_sequenceactive 0 + state_enable), un-parks everything (restore_obj_actions),
        // stops the voice (set_sequence 0 → audio stop) and destroys the trigger.
        //
        // wait_time chunks its state-disabled waits to ≤0.5 game-seconds (sequencer.tcl:1378
        // `hmin 0.5 …`), so the drain is reached within ~0.5 game-seconds of stepping — the
        // pump cap is generous. If the drain can't finish (clock stopped / factor 0 / trigger
        // never activated), fall back to the old hard teardown so the world is never left in
        // cutscene mode.
        public void StopCutscene()
        {
            if (_interp == null || _ctx == null) { _activeCutscene = 0; return; }
            try
            {
                if (_ctx.SequenceActive && _ctx.Sim != null)
                {
                    _ctx.SequenceBreaked = true;
                    // Bound the drain pump by wall-clock: each Step is a full O(N-entity) pass, so 800 of
                    // them in one frame is an O(800xN) freeze after several caves (the diagnosis's ~5 s
                    // hitch candidate). Cap the wall time — if the sequence still hasn't drained, the hard
                    // fallback below (set_sequence 0) tears it down cleanly, exactly as when the pump ran
                    // to its 800 limit. Faithful either way (same teardown); only the worst-case latency
                    // is bounded. Clock checked every 16 steps (cheap).
                    const long budgetMs = 120;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int steps = 0;
                    for (; steps < 800 && _ctx.SequenceActive; steps++)
                    {
                        _ctx.Sim.Step(0.025);
                        if ((steps & 15) == 15 && sw.ElapsedMilliseconds > budgetMs) break;
                    }
                    if (_ctx.SequenceActive)
                        Debug.LogWarning($"[StopCutscene] drain pump stopped after {steps} steps / {sw.ElapsedMilliseconds} ms — hard teardown.");
                }
                if (_ctx.SequenceActive)
                {
                    // Hard fallback — pre-drain behavior (set_sequence 0 now also clears the
                    // break flag + stops the voice, mirroring FUN_00649080).
                    _interp.Eval("set_sequence 0");
                    _interp.Eval("catch { restore_obj_actions }");
                }
                // Delete a test trigger the drain didn't (destroy → del this normally removes it).
                // Never delete the persistent menu trigger — its survival (destroy_overload) is
                // what keeps sequence_counter accumulating across menu plays.
                if (_activeCutscene != 0 && _activeCutscene != _menuTrigger
                    && _ctx.Entities.ContainsKey(_activeCutscene))
                    _interp.Eval($"catch {{ del {_activeCutscene} }}");
            }
            catch { /* best-effort */ }
            _activeCutscene = 0;
        }

        // Reload the scene (fresh, fully-respawned world) and auto-play <sequenceName> once it's
        // up. This is the faithful way to re-test: a cutscene mutates the world, so a clean replay
        // needs a clean environment.
        public void ReloadAndPlay(string sequenceName)
        {
            s_pendingCutscene = sequenceName;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        // The 40 Hz fixed sim tick + click-to-move moved to EngineTickService + ClickToMoveService
        // (VContainer ITickable entry points). Update here only flushes the debug log.
        void Update()
        {
            FlushPendingWorldSwap();
            if (_ctx != null)
            {
                DigglesTclEngine.PumpDeferredSequencePresentation(_ctx, 1);
                Vector2 focus = _ctx.Camera?.Focus ?? Vector2.zero;
                DigglesTclEngine.PumpDecoSeedLod(_ctx, focus);
            }
            // Debug toggle: turn on the per-command work-chain trace and route it to a dedicated file
            // (diggles_work_chain.log in the project root) so the click→work chain can be inspected
            // without copying the whole console. Force-on only, so it never disables the cutscene tracer
            // that shares this flag. Restart Play to turn it back off.
            // Live-toggleable diagnostic traces (cheap statics; see the Debug tooltips).
            DigglesAnimController.TracePlayClip = traceAnimClips;
            Diggles.Runtime.Tcl.Engine.ClimbAction.TraceClimb = traceClimb;
            if (verboseChainTrace)
            {
                // WorkDebug must be set independently of VerboseScriptLog: PlayCutscene already turns
                // VerboseScriptLog on at boot, so gating WorkDebug behind `!VerboseScriptLog` meant it
                // never armed once a cutscene ran (= the empty [WORKDBG] log). Idempotent + cheap.
                DigglesTclEngine.WorkDebug = true;
                if (!DigglesTclEngine.VerboseScriptLog)
                {
                    DigglesTclEngine.VerboseScriptLog = true;
                    if (!Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled)
                        Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin("diggles_work_chain.log");
                }
            }
            if (fileDebugLog || verboseChainTrace)
            {
                _flushAccum += Time.deltaTime;
                if (_flushAccum >= 1f) { Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush(); _flushAccum = 0f; }
            }
            // Staged intro pre-roll: fire the campaign cinematic once the live game clock reaches
            // the Trigger_Urw_006 mark (+5 s, Trigger_Urwald.tcl:928). Between NewGame and this
            // point the sim runs at real time on screen — founders idle, the t=3 init flips
            // gender/clothes visibly — exactly the original's pre-intro window.
            if (_pendingIntroSeq != null && WorldReady && !_atMenu && _ctx?.Sim != null
                && _ctx.Sim.GameTime >= _pendingIntroAt)
            {
                var seq = _pendingIntroSeq;
                _pendingIntroSeq = null;
                // Mutual exclusion with the real trigger engine (TriggerService): the campaign
                // template spawns the real Trigger_Urw_006 (splt_urw_unq_start_tCB.tcl:186),
                // whose evt_timer0 (+5 s, Trigger_Urwald.tcl:928) now arms a real any_object
                // trigger (range 40, Zwerg, owner 0 —:934-940). Whichever path fires first
                // wins; the loser is disarmed so urw_3 can never play twice:
                // • a sequence already active at the mark ⇒ the real chain fired inside this
                // Step's scan — drop the staged fallback for good (it used to stay pending
                // and would have replayed the intro after the cutscene ended);
                // • otherwise the staged fallback fires (real chain missing/stalled — e.g.
                // stub boot without story classes) and consumes the real trigger: armed
                // ones via the sweep (= native `trigger set_active`, FUN_0060e920:615-625),
                // a later-armed one via the one-shot SuppressedSequence guard (its arming
                // evt_timer0 may still be queued behind the cutscene event freeze).
                if (_ctx.SequenceActive)
                {
                    Debug.Log($"[DigglesWorldRuntime] intro '{seq}': the real trigger engine fired first — staged fallback dropped.");
                }
                else
                {
                    int consumed = _ctx.TriggerService?.SuppressSequence(seq!) ?? 0;
                    if (consumed > 0)
                        Debug.Log($"[DigglesWorldRuntime] intro '{seq}': staged fallback fired; consumed {consumed} armed real trigger(s).");
                    PlayCutscene(seq);
                }
            }
            // ESC: skip the running cutscene; if none, clear the unit selection (deselect).
            // Faithful skip = just raise the break flag (input handler FUN_0052c510:353-355 sets
            // DAT_00772400+0x8d) — the sequencer's next eval_sequence tick notices it (≤0.5
            // game-seconds, the wait_time chunk) and runs the Tcl drain + shut_sequencer itself.
            // No StopCutscene here: the drain is the stop.
            if (Input.GetKeyDown(KeyCode.Escape) && _ctx != null)
            {
                if (_ctx.SequenceActive) _ctx.SequenceBreaked = true;
                else _ctx.SelectedHandle = 0;
            }
            // Menu backdrop: keep a cinematic running. Watchdog (not edge-detect) so an empty/stub
            // start_*.tcl that never sets SequenceActive can't stall the loop — after a short idle gap
            // with nothing playing, pick the next random one. Continuous forever while at the menu.
            if (_atMenu && _ctx != null && _menuSeqs.Count > 0)
            {
                if (_ctx.SequenceActive) _menuIdleT = 0f;
                else
                {
                    _menuIdleT += Time.deltaTime;
                    if (_menuIdleT >= MenuBackdropGap) { _menuIdleT = 0f; PlayRandomMenuCutscene(); }
                }
            }
        }

        // Re-push the static class_light buffer each frame so runtime add/remove/move of light sources is
        // reflected in the Diggles shaders. Cheap (≤128-iter refill + a small constant upload).
        void LateUpdate()
        {
            (_ctx?.EntitySpawner as RuntimeWorldSpawner)?.UploadClassLights();
            // Live toggle for the dig-mark plane meshes (committed orders + armed-tool hover).
            Diggles.Runtime.World.DigMarkOverlay.GlobalEnabled = showDigMarks;
        }

        float _flushAccum;

        void OnDisable() => Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();

        void OnDestroy()
        {
            ObserveMapBuilder(null);
            if (_pendingWorldSwap == null) return;
            _pendingWorldSwap.Dispose();
            _pendingWorldSwap = null;
        }

        // Single-line, length-bounded form of a trace token (args/results may be huge
        // proc bodies or contain newlines).
        static string TraceTrunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            s = s.Replace('\n', '·').Replace('\r', ' ').Replace('\t', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // --- spawn flows ---

        void RunOneOfEach()
        {
            // Define a lightweight stub only for classes the real load didn't provide
            // (loadRealClasses populates the real ones — don't clobber them).
            var stubs = new (string cls, string anim)[]
            {
                ("Zwerg", "mann.standard"), ("Hamster", "hamster.standard"),
                ("Troll", "troll.standard"), ("Drache", "drache.sitzen_warten_a"),
                ("Baby", "baby.standard"), ("Spinne", "spinne.haengen_warten"),
                ("Pilz", "pilz.standard"),
            };
            foreach (var (cls, anim) in stubs)
            {
                if (_ctx!.Classes.ContainsKey(cls)) continue;        // real class loaded
                if (cls == "Zwerg")
                {
                    var z = new DigglesClass { Name = "Zwerg", DefaultAnim = anim, DefaultAnimMode = 2 };
                    _ctx.Classes["Zwerg"] = z;
                    LoadClassAnims(z);                                // animsets for walk/run
                }
                else
                {
                    try { _interp!.Eval($"def_class {cls} none thing 0 {{}} {{ class_defaultanim {anim} }}"); }
                    catch (System.Exception e) { Debug.LogWarning($"[DigglesWorldRuntime] stub {cls}: {e.Message}"); }
                }
            }

            // Lay out one of each along +X (native frame: X horiz, Y depth, Z elev).
            string[] names = { "Zwerg", "Hamster", "Troll", "Drache", "Baby", "Spinne", "Pilz" };
            for (int k = 0; k < names.Length; k++)
            {
                try
                {
                    var h = _interp!.Eval($"new {names[k]} \"\" \"{k * 4} 0 12\" \"0 4.71 0\"").Trim();
                    var ent = _ctx!.GetEntity(h);
                    if (ent != null) _spawned.Add(ent);
                }
                catch (System.Exception e) { Debug.LogWarning($"[DigglesWorldRuntime] spawn {names[k]}: {e.Message}"); }
            }
        }

        void RunCampaign(string campaignRel)
        {
            // Faithful boot chain: the original new-game launches
            // GameScripts/single_CampaignX.tcl — `obj_clear; sel /obj; set sm [new StoryMgr];
            // call_method $sm load_level Campaign` — so CampaignInit.tcl (sm_create_map 512 640,
            // sm_set_digcount 32, zones + unique temps) and Campaign.tcl run inside the StoryMgr
            // object context: its per-class procs (split_template/generate_next/load_next/
            // wait_time…) resolve, its idle state ticks on the normal state pass, and digging can
            // grow the world at runtime. A "Scripts/gameplay/<X>.tcl" target with the real StoryMgr
            // class loaded routes through this wrapper (level name = the file name); everything
            // else (stub mode, file overrides) keeps the direct eval.
            string levelName = Path.GetFileNameWithoutExtension(campaignRel);
            bool gameplayTcl = campaignRel.Replace('\\', '/')
                .StartsWith("Scripts/gameplay/", System.StringComparison.OrdinalIgnoreCase);
            if (gameplayTcl && _ctx != null && _ctx.Classes.ContainsKey("StoryMgr"))
            {
                Debug.Log($"[DigglesWorldRuntime] StoryMgr boot: new StoryMgr → load_level {levelName}");
                RunWorldScriptCore("obj_clear\nsel /obj\nset sm [new StoryMgr]\ncall_method $sm load_level " + levelName);
                return;
            }
            var abs = Path.Combine(dataRoot, campaignRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs)) { Debug.LogError($"[DigglesWorldRuntime] campaign not found: {abs}"); return; }
            // Running the campaign tcl drives both: map_template/map_setlayer2 →
            // _map (terrain), and `new ClassName...` → RuntimeWorldSpawner (entities).
            RunWorldScriptCore(DigglesText.ReadAllText(abs));
        }

        private string ReadBootScriptContent(string bootScript)
        {
            string relativePath = bootScript.Replace('\\', '/');
            if (relativePath.StartsWith("data/", System.StringComparison.OrdinalIgnoreCase))
                relativePath = relativePath.Substring(5);
            string absolutePath = Path.Combine(
                dataRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException(
                    $"The native new-game boot script was not found: {absolutePath}",
                    absolutePath);
            return DigglesText.ReadAllText(absolutePath);
        }

        /// <summary>
        /// Evaluates and emits a gameplay world synchronously. EditMode tests use this same seam
        /// as boot, including deferred map-recreation detection and presentation wiring.
        /// </summary>
        public void RunWorldScript(string content)
        {
            if (content == null) throw new System.ArgumentNullException(nameof(content));
            if (_interp == null || _ctx == null || _map == null || _worldRootGo == null)
                throw new System.InvalidOperationException(
                    "A runtime session must be bound before a gameplay world script can run.");
            _atMenu = false;
            _nonStoryGameplaySession = _boundSession;
            RunWorldScriptCore(content);
            WorldReady = _ctx?.Terrain != null;
        }

        // Eval a world tcl (campaign file content OR the inline menu diorama), build the terrain mesh +
        // nav graph, wire ctx, collect spawned entities, upload lights. Shared by campaign + menu builds.
        private void RunWorldScriptCore(string content)
        {
            // Boot-scale synchronous work (campaign eval + full terrain emit) — named for the
            // freeze watchdog so a Play stall here is attributed, not mysterious.
            using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section("world.boot", 500);
            long evalStarted = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            using (Diggles.Runtime.Diagnostics.PerfTrace.Section("world.boot.eval", 100))
            {
                _interp!.Eval(content);
            }
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                _atMenu ? "menu.eval" : "campaign.eval",
                evalStarted);
            if (_pendingWorldSwap != null)
            {
                FlushPendingWorldSwap();
                return;
            }
            // Build the terrain mesh + nav graph from the recorded map calls.
            long emitStarted = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            using (Diggles.Runtime.Diagnostics.PerfTrace.Section("world.boot.emit", 100))
            {
                if (_worldRootGo != null) _map!.EmitTerrain(_worldRootGo);
            }
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                _atMenu ? "menu.emit" : "campaign.emit",
                emitStarted);
            long wireStarted = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            using var _wirePerf =
                Diggles.Runtime.Diagnostics.PerfTrace.Section("world.boot.wire", 100);
            Debug.Log($"[DigglesWorldRuntime] terrain: {_map!.Width}x{_map.Height}, " +
                      $"{_map.PmpsLoaded} PMPs, {_map.L2msLoaded} L2Ms");
            // Wire the nav graph for `action this walk <target>` (click-to-move is Tcl-driven below).
            if (_worldRootGo != null)
            {
                _ctx!.NavService = _worldRootGo.GetComponent<Diggles.Runtime.Navigation.NavWorldService>();
                // Expose the live diggable terrain so the dig primitives (get_hmap/dig_apply/…) resolve it.
                _ctx.Terrain = _worldRootGo.GetComponent<Diggles.Runtime.World.DiggableTerrain>();
                WirePresentation();
            }
            // Collect every live entity that got a GameObject.
            foreach (var ent in _ctx!.Entities.Values)
                if (ent.GameObject != null) _spawned.Add(ent);
            // Upload the collected class_light sources as a static custom light buffer the Diggles shaders
            // loop (faithful software lighting; avoids URP Forward+ clustering flicker / light limits).
            (_ctx!.EntitySpawner as RuntimeWorldSpawner)?.UploadClassLights();

            // Center the gameplay camera on the loaded world's initial set_view (Campaign.tcl / the menu
            // diorama just set it on ctx.Camera). Without this the gameplay camera keeps its previous focus
            // (e.g. the menu diorama's) after a New Game rebuild and shows the wrong place.
            if (_camController != null && _ctx.Camera != null)
                _camController.SetState(_ctx.Camera.Focus.x, _ctx.Camera.Focus.y, _ctx.Camera.Zoom, _ctx.Camera.Dx, _ctx.Camera.Dy);
            if (_camController != null && _map != null)
                _camController.SetWorldBounds(Vector2.zero, new Vector2(_map.Width, _map.Height));
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record(
                _atMenu ? "menu.wire" : "campaign.wire",
                wireStarted);
        }

        // The world-presentation stack for the current (_ctx, _worldRootGo, _atMenu) triple:
        // fluids, backdrop/horizon zones, fog of war (campaign only), tunnel torches, base
        // light, tuning bind, rim probe, spawner terrain hooks. Called from the boot path
        // (RunWorldScript) and from BindSession — a save-loaded session swaps ContentRoot in
        // without re-running the boot; otherwise the retired world's whole presentation stays
        // live: fog off, the menu's 64×64 base-light texture stretched over a 500×200
        // save, the menu's horizon zone/clear over the caves (fowParams (0,0,0) + blParams
        // (64,64) + Info_Fog_Urw_d inside the tutorial cave).
        // Re-entry safe: the same (terrain, root) pair wires once (the boot path calls this,
        // then BindSession fires for the same session — TilesChanged must not double-subscribe).
        object? _wiredTerrain;
        GameObject? _wiredRoot;
        void WirePresentation()
        {
            if (_worldRootGo == null || _ctx == null) return;
            if (ReferenceEquals(_wiredTerrain, _ctx.Terrain) && _wiredRoot == _worldRootGo) return;
            _wiredTerrain = _ctx.Terrain;
            _wiredRoot = _worldRootGo;
            PresentationWireCount++;

            if (_ctx.Terrain != null)
            {
                var fluidPresenter = _worldRootGo.GetComponent<Diggles.Runtime.World.FluidPresenter>()
                    ?? _worldRootGo.AddComponent<Diggles.Runtime.World.FluidPresenter>();
                fluidPresenter.Init(_ctx.Fluids, _ctx.Terrain, dataRoot);
            }
            // Panorama backdrop + horizon zones (laws + citations in
            // BackdropLaws / Docs/BACKDROP_PARALLAX_RE.md). The component is attached to every
            // world. Gate scope: the gameplayActive provider gates only the
            // panorama/carpet — natively CDisplay::Draw runs the horizon-zone scan
            // FUN_004750b0 unconditionally (004781b0_CDisplay_Draw.c:210) and only the
            // panorama sits under `+0x38f0 && StoryMgr+0x84==0` (:203-206); the menu diorama
            // (obw_szene) authors Info_Fog_Urw_a/b and natively shows no raw earth rim, so
            // zone activation + the rear-skirt fade run over the menu too. Ground row = the
            // deepest load-time open-air row (the meadow surface line; pure cave worlds have
            // none → the FUN_00480ee0:38 focus-row gate never opens). The presenter drives
            // the horizon_set zone clear colour + the rear-skirt fade globals that hide the
            // flat earth rim (FUN_00484cd0:82-104).
            {
                var wr = _ctx.Terrain?.WorldResult ?? _map?.WorldResult;
                int groundRow = wr != null
                    ? Diggles.Runtime.World.BackdropLaws.ComputeGroundRow(wr.BlockIdx, wr.Width, wr.Height)
                    : int.MinValue;
                var backdrop = _worldRootGo.GetComponent<Diggles.Runtime.World.BackdropPresenter>()
                    ?? _worldRootGo.AddComponent<Diggles.Runtime.World.BackdropPresenter>();
                var zctx = _ctx!;
                // Panorama-only campaign gate (see above). `_atMenu` excludes save-loaded and
                // native single_* non-StoryMgr gameplay sessions as well as StoryMgr campaigns.
                backdrop.Init(dataRoot, _worldRootGo.transform, groundRow,
                    () => zctx.HorizonRegions,
                    () => !_atMenu);
            }
            // Fog of war (native CFogOfWar at *(World+0x20); Docs/FOG_OF_WAR_RE.md): created
            // with the world, fed by the sim's 4th-think pass, rendered by the presenter.
            // Only for a real campaign/skirmish world — the menu backdrop plays fogless
            // (StoryMgr worlds are the ones the original fogs; the menu meadow has no
            // set_fogofwar owners anyway, so fogging it would black the whole backdrop).
            if (_ctx.Terrain != null && !_atMenu)
            {
                var t = _ctx.Terrain;
                // Ray collision = the native detail-cell byte 0 (sub-cell (0,0) fill), not
                // the tile nibble — see DiggableTerrain.FogFillAt (meadow-skim law).
                // Reuse a staged session's state (a.sav load hydrates the fog lanes into
                // ctx.FogOfWar during staging — recreating here would wipe the loaded
                // exploration); create only when absent or mis-sized (menu→campaign rebuilds).
                var fogState = _ctx.FogOfWar;
                if (fogState == null || fogState.Width != t.Width || fogState.Height != t.Height)
                {
                    fogState = new Diggles.Runtime.World.FogOfWarState(
                        t.Width, t.Height, t.FogFillAt);
                    _ctx.FogOfWar = fogState;
                }
                fogState.DigNibbleAt = t.RawDigNibble;
                fogState.ClearDigNibble = t.ClearDigMark;
                    // Render half (Docs/FOG_OF_WAR_RE.md §4-5): the global fog texture the
                    // Diggles shaders sample (gray veil + soft black corner gradients) and the
                    // opaque black volume (front covers z=14.5 + boundary side walls). Both sit
                    // on the world root — its local space is the native render frame the
                    // shaders map positionWS into. Fog stays live through cutscenes (faithful;
                    // story scripts reveal their stages via remove_black_fog / FogRemover).
                    var fowPresenter = _worldRootGo.GetComponent<Diggles.Runtime.World.FogOfWarPresenter>()
                        ?? _worldRootGo.AddComponent<Diggles.Runtime.World.FogOfWarPresenter>();
                    fowPresenter.Init(_ctx.FogOfWar);
                    var fowOverlay = _worldRootGo.GetComponent<Diggles.Runtime.World.FogOfWarOverlay>()
                        ?? _worldRootGo.AddComponent<Diggles.Runtime.World.FogOfWarOverlay>();
                    // rendersAt: black-boundary tiles without terrain (load-time block-59 cave
                    // air / beyond the render bbox) get alpha gradient patches instead of a
                    // hard hole. presenter: patches persist until the smoothed display field
                    // catches up, so the reveal dissolve over meshless cave air fades instead
                    // of popping per tile.
                    fowOverlay.Init(_ctx.FogOfWar, t.GetHmap, t.RendersTerrain, fowPresenter);
                    // Dig carves change the slab depths under existing black side walls —
                    // refresh the touched rect (fog bytes there change too, but not always).
                    t.TilesChanged += (a, b, c, d) => fowOverlay.MarkRectDirty(a, b, c, d);
                    // Smooth class-light fog gate: the light refill
                    // samples the presenter's display texel at each light's tile — torch light
                    // fades with the same smoothed field the shaders veil by.
                    if (_ctx.EntitySpawner is RuntimeWorldSpawner rwsFog)
                    {
                        rwsFog.FogPresenter = fowPresenter;
                        rwsFog.FogState = _ctx.FogOfWar;
                    }

                    // Tunnel torches (Docs/TUNNEL_TORCH_LIGHTING_RE.md): the glow-drop →
                    // Misc_Light auto-spawn law, ticked from DigglesGameSim (3a4). Campaign
                    // worlds only; uses the world-agnostic BaseLight wired below.
                    _ctx.TunnelTorches = new Diggles.Runtime.World.TunnelTorchSystem(t.Width, t.Height, t.GetHmap)
                    {
                        LightBegin = _ctx.LightBeginRow,
                        NearExistingLight = (x, y) => DigglesTclEngine.AnyLightNear(_ctx, x, y),
                        SpawnTorch = (x, y, z) => DigglesTclEngine.TunnelTorchSpawn(_ctx, x, y, z),
                    };
                }
                else
                {
                    // Menu / fogless world rebuilt onto the same root: silence any fog
                    // presenter left over from a previous game world (shader globals off,
                    // black volume hidden) — the menu meadow must render un-fogged.
                    var oldPresenter = _worldRootGo.GetComponent<Diggles.Runtime.World.FogOfWarPresenter>();
                    if (oldPresenter != null) oldPresenter.Clear();
                    var oldOverlay = _worldRootGo.GetComponent<Diggles.Runtime.World.FogOfWarOverlay>();
                    if (oldOverlay != null) oldOverlay.Clear();
                    _ctx.TunnelTorches = null;
                    // …and the darkness baseline (the menu meadow renders at full brightness —
                    // a world-agnostic darkness experiment was reverted pending the
                    // original-render research).
                    var oldMenuBl = _worldRootGo.GetComponent<Diggles.Runtime.World.BaseLightPresenter>();
                    if (oldMenuBl != null) oldMenuBl.Clear();
                    _ctx.BaseLight = null;
                    // …and the smooth light gate (menu lights render at full).
                    if (_ctx.EntitySpawner is RuntimeWorldSpawner rwsFog)
                    {
                        rwsFog.FogPresenter = null;
                        rwsFog.FogState = null;
                    }
                }

                // Underground darkness + tunnel torches (Docs/TUNNEL_TORCH_LIGHTING_RE.md).
                // BaseLightMap = the native per-tile baselight byte (tile+0x07, FUN_005906a0:
                // sky rows = daylight 20, unlit underground ≈ 4/20 brightness) → global R8
                // texture the Diggles shaders multiply into ambient + main light (point
                // lights add on top). Campaign worlds only (the menu special case above).
                // set_light_begin ran during the boot eval and left the sky row on
                // ctx.LightBeginRow.
                if (_ctx.Terrain != null && !_atMenu)
                {
                    var t = _ctx.Terrain;
                    var bl = new Diggles.Runtime.World.BaseLightMap(t.Width, t.Height, t.GetHmap)
                    { LightBegin = _ctx.LightBeginRow };
                    bl.RecomputeAll();
                    _ctx.BaseLight = bl;
                    if (_ctx.TunnelTorches != null)
                        _ctx.TunnelTorches.SkyLitAt = bl.SkyLit;   // FUN_004753f0 daylight-shaft approx.
                    // A carve changes the tile's own fill + the down-right neighbours' up-left
                    // term + the sky scan of the 6 rows below (the scan looks upward) — pad.
                    t.TilesChanged += (a, b, c, d) => bl.RecomputeRect(
                        a - 1, b - 1, c + 1, d + Diggles.Runtime.World.BaseLightMap.SkyScanTiles + 1);
                    var blPresenter = _worldRootGo.GetComponent<Diggles.Runtime.World.BaseLightPresenter>()
                        ?? _worldRootGo.AddComponent<Diggles.Runtime.World.BaseLightPresenter>();
                    blPresenter.Init(bl);
                    // Live tuning: a scene-authored DigglesTuning component
                    // owns the knobs when present (attached wherever wanted in the scene; live
                    // sliders + the inspector Save button). json keys — when present — seed it
                    // at boot (Play-mode slider tweaks don't persist to the scene; the Save
                    // button's json is their home). No component in the scene → the json keys
                    // apply directly, so tuned installs work without it.
                    var tuning = _worldRootGo.GetComponent<DigglesTuning>();
                    if (tuning == null) tuning = FindFirstObjectByType<DigglesTuning>();
                    if (tuning != null)
                    {
                        tuning.baseLightMin = DigglesConfig.ResolveBaseLightMin(tuning.baseLightMin);
                        tuning.classLightCap = DigglesConfig.ResolveClassLightCap(tuning.classLightCap);
                        tuning.Bind(blPresenter, _ctx.EntitySpawner as RuntimeWorldSpawner);
                    }
                    else
                    {
                        blPresenter.MinFactor = DigglesConfig.ResolveBaseLightMin(blPresenter.MinFactor);
                        if (_ctx.EntitySpawner is RuntimeWorldSpawner lightSpawner)
                            lightSpawner.ClassLightCap = DigglesConfig.ResolveClassLightCap(lightSpawner.ClassLightCap);
                    }
                }
                else if (_ctx.Terrain == null)
                {
                    var oldBl = _worldRootGo.GetComponent<Diggles.Runtime.World.BaseLightPresenter>();
                    if (oldBl != null) oldBl.Clear();
                    _ctx.BaseLight = null;
                }
                // Rim probe: F8+click on any terrain polygon in
                // Play dumps its full tile/vertex/light state to <project>/RimProbe/. Wired
                // for every world (menu included) — read-only.
                if (_ctx.Terrain != null)
                {
                    var rimProbe = _worldRootGo.GetComponent<Diggles.Runtime.World.RimProbeTool>()
                        ?? _worldRootGo.AddComponent<Diggles.Runtime.World.RimProbeTool>();
                    rimProbe.Init(_ctx.Terrain, _ctx.FogOfWar, _ctx.BaseLight, _worldRootGo.transform);
                    // Nav probe: F9+click start then goal → Calc1 both
                    // climb variants + the full corridor plane/record dump to <project>/NavProbe/.
                    var navProbe = _worldRootGo.GetComponent<Diggles.Runtime.World.NavProbeTool>()
                        ?? _worldRootGo.AddComponent<Diggles.Runtime.World.NavProbeTool>();
                    navProbe.Init(_ctx.Terrain, _worldRootGo.transform);
                }
                // …and to the spawner, so dig drops settle out of solid rock at spawn (the
                // original's set_physic floor settle — RuntimeWorldSpawner.SettleDropPosition).
                if (_ctx.EntitySpawner is RuntimeWorldSpawner rws)
                {
                    rws.Terrain = _ctx.Terrain;
                    // Drop-on-dig (the original's continuous item physics): when a carve removes the
                    // ground under a resting loose item (a dug-out Stein / Steinbrocken boulder / ore
                    // chunk), re-settle it onto the new floor and move its GameObject.
                    if (_ctx.Terrain != null)
                        _ctx.Terrain.TilesChanged += (a, b, c, d) =>
                            RuntimeWorldSpawner.ResettleLooseItems(_ctx.Terrain!.Nav, _ctx.Entities.Values,
                                a, b, c, d, e => rws.ApplyPosition(e.GameObject, e.Position));
                }
        }

        // --- animation driving ---

        void DriveSpawned()
        {
            if (fileDebugLog)
            {
                var L = (System.Action<string>)Diggles.Runtime.Diagnostics.DigglesDebugLog.Log;
                int idleCap = 0, withGo = 0;
                foreach (var e in _spawned)
                {
                    if (e.GameObject != null) withGo++;
                    if (e.Class != null && e.Class.Methods.ContainsKey("seq_idle")) idleCap++;
                }
                L($"[DriveSpawned] loadRealClasses={loadRealClasses} classes={_ctx!.Classes.Count} " +
                  $"_spawned={_spawned.Count} withGameObject={withGo} idleCapable(seq_idle method)={idleCap}");
                if (_ctx.Classes.TryGetValue("Zwerg", out var zw))
                    L($"[class Zwerg] methods={zw.Methods.Count} hasSeqIdle={zw.Methods.ContainsKey("seq_idle")} " +
                      $"initCode={(zw.InitCode == null ? 0 : zw.InitCode.Length)} chars");
                else
                    L("[class Zwerg] NOT in registry");
                int shown = 0;
                foreach (var e in _spawned)
                {
                    if (shown++ >= 6) break;
                    bool tbl = e.VarsScope.Vars.TryGetValue("seq_idle_anims", out var tv) && !string.IsNullOrWhiteSpace(tv);
                    L($"[spawn] #{e.Handle} class={e.Class?.Name} methods={e.Class?.Methods.Count} " +
                      $"vars={e.VarsScope.Vars.Count} state={e.CurrentState ?? "<null>"} seq_idle_anims={tbl}");
                }
            }
            foreach (var ent in _spawned)
            {
                if (ent.GameObject == null) continue;
                var anim = ent.GameObject.GetComponentInChildren<DigglesAnimator>();
                if (anim != null) anim.Interpolate = interpolate;

                // No external idle kick. Idle is driven faithfully by the entity's
                // own state machine: the gnome `idle` state calls its set_idle_anim proc
                // (e.g. set_anim mann.stand_anim_a LOOP) each pass, paced by `action wait
                // 1`. (seq_idle — the weighted table — is for sequence/statist roles, a
                // different mechanism; kicking it here just got overwritten by the idle
                // state's wait action.) Requires get_remaining_sparetime > 0 so the idle
                // state reaches set_idle_anim instead of bailing to work_dispatch.

                // Trace the first few idle-capable entities (class declares a
                // seq_idle method). Enable their own state_log (scripts log "passing
                // state code X" per pass) and record state/vars/table, so we can see
                // whether the idle state runs and reaches set_idle_anim.
                if (fileDebugLog
                    && ent.Class != null && ent.Class.Methods.ContainsKey("seq_idle")
                    && Diggles.Runtime.Diagnostics.DigglesDebugLog.Tracked.Count < 1)
                {
                    Diggles.Runtime.Diagnostics.DigglesDebugLog.Tracked.Add(ent.Handle);
                    ent.VarsScope.Vars["state_log"] = "1";
                    bool hasTbl = ent.VarsScope.Vars.TryGetValue("seq_idle_anims", out var t)
                        && !string.IsNullOrWhiteSpace(t);
                    Diggles.Runtime.Diagnostics.DigglesDebugLog.Log(
                        $"[track] #{ent.Handle} {ent.Class.Name} state={ent.CurrentState ?? "<null>"} " +
                        $"enabled={ent.StateEnabledCount} vars={ent.VarsScope.Vars.Count} " +
                        $"seq_idle_anims={hasTbl} action={ent.ActiveAction?.GetType().Name ?? "<none>"}");
                }
            }
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
        }


        // --- faithful idle plumbing (mirrors DigglesTclAnimDemo) ---

        // Load z_anims (animsets + base map) into a class if it has none yet, so
        // walk/run/turn states resolve. Safe to call repeatedly (skips when present).
        void EnsureClassAnims(string className)
        {
            if (_ctx != null && _ctx.Classes.TryGetValue(className, out var cls)
                && cls != null && cls.Animsets.Count == 0)
                LoadClassAnims(cls);
        }

        void LoadClassAnims(DigglesClass cls)
        {
            var prev = _ctx!.CurrentClass;
            _ctx.CurrentClass = cls;
            try
            {
                EvalFileInClass("Scripts/misc/animclassinit.tcl");
                EvalFileInClass("Scripts/classes/zwerg/z_anims.tcl");
            }
            catch (System.Exception e) { Debug.LogWarning($"[DigglesWorldRuntime] LoadClassAnims: {e.Message}"); }
            finally { _ctx.CurrentClass = prev; }
        }

        void LoadIdlePlumbing()
        {
            const string table =
                "set seq_idle_anims [list]\n" +
                "lappend seq_idle_anims {10 {stand_anim_a}}\n" +
                "lappend seq_idle_anims {10 {stand_anim_b}}\n" +
                "lappend seq_idle_anims {10 {stand_anim_c}}\n" +
                "lappend seq_idle_anims {1 {spaehen}}\n" +
                "lappend seq_idle_anims {1 {zeigen_rechts}}\n" +
                "lappend seq_idle_anims {1 {strippe_ziehen}}\n" +
                "lappend seq_idle_anims {1 {raeuspern}}\n" +
                "lappend seq_idle_anims {3 {wippen}}\n" +
                "lappend seq_idle_anims {1 {verlegen}}\n" +
                "lappend seq_idle_anims {5 {warten}}\n" +
                "lappend seq_idle_anims {3 {kratzen}}\n" +
                "lappend seq_idle_anims {1 {kopf_kratzen}}\n" +
                "lappend seq_idle_anims {2 {aufatmen}}\n" +
                "lappend seq_idle_anims {1 {popo_waermen}}\n" +
                "lappend seq_idle_anims {2 {hungrig}}\n" +
                "lappend seq_idle_anims {w1 {kletterstand_anim}}\n" +
                "lappend seq_idle_anims {3 {blicken_rechts_links}}\n";
            try
            {
                _interp!.Eval(table);
                var path = Path.Combine(dataRoot, "Scripts", "misc", "seq_idle.tcl");
                if (File.Exists(path)) _interp.Eval(File.ReadAllText(path));
                else Debug.LogWarning($"[DigglesWorldRuntime] seq_idle.tcl not found at {path}");
            }
            catch (System.Exception e) { Debug.LogWarning($"[DigglesWorldRuntime] LoadIdlePlumbing: {e.Message}"); }
        }

        void EvalFileInClass(string relPath)
        {
            var path = Path.Combine(dataRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) _interp!.Eval(File.ReadAllText(path));
            else Debug.LogWarning($"[DigglesWorldRuntime] missing {path}");
        }
    }
}
