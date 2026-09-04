#nullable enable

using System;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Diggles.Runtime.Save;
using Diggles.Runtime.Tcl.Engine;

namespace Diggles.Runtime
{
    // Scene composition root for the Play-mode game. Place this on a scene GameObject and assign the
    // DigglesGameInstaller (the Inspector-wired scene controllers). It stays INERT (auto-build
    // suppressed in Awake) until DigglesWorldRuntime — after building the engine core — calls
    // BuildWith(engine, activeSession). Configure then registers the core (engine + context + settings + frame-loop
    // services) and runs the installer (the controllers). Its container disposes automatically when the
    // scene unloads (incl. ReloadAndPlay).
    public class GameLifetimeScope : LifetimeScope
    {
        [Tooltip("MonoInstaller with the scene controller references (assign in the Inspector).")]
        [SerializeField] DigglesGameInstaller? installer;

        DigglesEngine? _engine;
        ActiveGameSession? _activeSession;
        DigglesWorldRuntime? _worldRuntime;

        // Suppress the base auto-build: the engine is not built until DigglesWorldRuntime.Start, so we
        // build explicitly via BuildWith once it is ready (base.Awake would Build too early, no engine).
        protected override void Awake() { }

        public void BuildWith(
            DigglesEngine engine,
            ActiveGameSession activeSession,
            DigglesWorldRuntime worldRuntime)
        {
            _engine = engine ?? throw new System.ArgumentNullException(nameof(engine));
            _activeSession = activeSession ?? throw new System.ArgumentNullException(nameof(activeSession));
            _worldRuntime = worldRuntime ?? throw new System.ArgumentNullException(nameof(worldRuntime));
            Build();   // -> Configure
            BindSaveBackendToCurrentSession(clearBaseline: true);
        }

        public void BindSaveBackendToCurrentSession(bool clearBaseline)
        {
            ActiveGameSession activeSession = _activeSession ??
                throw new InvalidOperationException("The game lifetime scope has not been built.");
            if (clearBaseline)
                Container.Resolve<OriginalSaveBaselineSessionStore>().Clear();
            Container.Resolve<OriginalSaveGameBackendParticipant>()
                .BindSession(activeSession.Current);
        }

        protected override void Configure(IContainerBuilder builder)
        {
            if (_engine == null) return;

            // Core registrations (runtime instances / by-type) — these can't be Inspector references.
            builder.RegisterInstance(_engine);
            builder.RegisterInstance(_engine.Ctx);
            builder.RegisterInstance(_activeSession!).AsSelf().As<IActiveGameSession>();
            builder.RegisterInstance(_worldRuntime!).As<IRuntimeSessionPresentationParticipant>();
            builder.Register<OriginalSaveBaselineSessionStore>(Lifetime.Singleton);
            builder.RegisterInstance<Func<DigglesCameraController?>>(
                () => _worldRuntime!.CameraController);
            builder.RegisterInstance<Func<OriginalSaveGameBackend>>(
                () => Container.Resolve<OriginalSaveGameBackend>());
            builder.Register<OriginalSaveGameBackendParticipant>(Lifetime.Singleton)
                .AsSelf()
                .As<IRuntimeSessionPresentationParticipant>();
            builder.Register<DigglesEngineFactory>(Lifetime.Singleton).As<IDigglesEngineFactory>();
            builder.Register<GameMutationGate>(Lifetime.Singleton).As<IGameMutationGate>();
            builder.Register<RuntimePresentationBinder>(Lifetime.Singleton)
                .As<IRuntimePresentationBinder>();
            builder.Register<OriginalSaveRuntimePresentationParticipant>(Lifetime.Singleton)
                .As<IRuntimeSessionPresentationParticipant>();
            builder.Register<RuntimeWorldSessionHost>(Lifetime.Singleton)
                .As<IRuntimeWorldSessionHost>();
            builder.Register<SettingsService>(Lifetime.Singleton);
            builder.Register<OriginalSaveArchiveReader>(Lifetime.Singleton);
            builder.Register<OriginalSaveArchiveCodecOptions>(Lifetime.Singleton);
            builder.Register<OriginalSaveArchiveCodec>(Lifetime.Singleton)
                .As<IOriginalSaveArchiveCodec>();
            builder.Register<OriginalSaveRepository>(Lifetime.Singleton)
                .As<IOriginalSaveRepository>();
            builder.Register<OriginalSaveLoadPlanner>(Lifetime.Singleton)
                .As<IOriginalSaveLoadPlanner>();
            builder.Register<OriginalSaveRuntimeStager>(Lifetime.Singleton)
                .As<IOriginalSaveRuntimeStager>();
            builder.Register<OriginalSaveRuntimeLightingInitializer>(Lifetime.Singleton)
                .As<IOriginalSaveRuntimeLightingInitializer>();
            builder.Register<OriginalSaveLoadPreparationService>(Lifetime.Singleton)
                .As<IOriginalSaveLoadPreparationService>();
            builder.RegisterInstance(new OriginalSaveGameLoadConfiguration(
                _worldRuntime!.bdbRel));
            builder.Register<OriginalSaveGameLoadService>(Lifetime.Singleton)
                .AsSelf()
                .As<IOriginalSaveGameLoadService>();
            builder.Register<OriginalSaveCurrentArchiveWriter>(Lifetime.Singleton)
                .As<IOriginalSaveCurrentArchiveWriter>();
            builder.Register<OriginalSaveRuntimePatchMapper>(Lifetime.Singleton)
                .As<IOriginalSaveRuntimePatchMapper>();
            builder.Register<OriginalSaveRuntimeArchiveService>(Lifetime.Singleton)
                .As<IOriginalSaveRuntimeArchiveService>();
            builder.Register<OriginalSaveGameBackend>(Lifetime.Singleton)
                .AsSelf()
                .As<IGameSaveBackend>();
            builder.Register<RuntimeSaveableActionFactory>(Lifetime.Singleton)
                .As<IRuntimeSaveableActionFactory>();
            builder.Register<OriginalSaveActionFactory>(Lifetime.Singleton)
                .As<IOriginalSaveActionFactory>();
            builder.Register<OriginalSaveActionCandidateStager>(Lifetime.Singleton)
                .As<IOriginalSaveActionCandidateStager>();
            builder.Register<OriginalSaveActionInstaller>(Lifetime.Singleton)
                .As<IOriginalSaveActionInstaller>();
            builder.Register<OriginalSaveRuntimeSessionStager>(Lifetime.Singleton)
                .As<IOriginalSaveRuntimeSessionStager>();

            // Runtime icon provider — loads GUI icons straight from the original data files
            // ({DataRoot}/GUI/icons/*.tga) on demand (no edit-time bake; same approach as the
            // WorkClock art). The authored building / inventory / properties UI injects it to
            // fill its uGUI Images.
            builder.RegisterInstance(new IconLoader(_engine.Ctx.DataRoot));
            // Game-text localization (data/messages.txt) + locale-aware art — the same shared
            // instance DigglesEngine.Build bound (LocalizedText/LocalizedSprite reach it via
            // GameLocalization.Current; controllers inject it here).
            if (Localization.GameLocalization.Current is { } localization)
            {
                builder.RegisterInstance(localization);
                builder.RegisterInstance(localization.Assets);
            }
            // Building-placement mode (engine-owned — created in DigglesTclEngine.Register so the
            // hide_obj_ghost native and headless tests reach it). Registered here so the
            // authored building-window UI can inject it (QueryButtons/OrderBuild/OrderResume/
            // OrderPack/ClickInventoryCrate — the §10.4 seam); ClickToMoveService routes the
            // per-frame cursor/confirm input into it via the context.
            if (_engine.Ctx.Placement != null) builder.RegisterInstance(_engine.Ctx.Placement);

            // Frame-loop services (ITickable). TimeControlService FIRST so a +/- press updates the
            // fast-forward level before EngineTickService reads EffectiveSpeed this same frame; AsSelf
            // so EngineTickService can inject the concrete type.
            builder.RegisterEntryPoint<TimeControlService>().AsSelf();
            builder.RegisterEntryPoint<QuickSaveHotkeyService>();
            builder.RegisterEntryPoint<AutoSaveService>();
            // F3 load/save panel seam — the authored panel view injects this controller
            // (same pattern as BottomMenuController / InventoryPanelController).
            builder.RegisterEntryPoint<Diggles.Runtime.UI.SaveGamePanelController>().AsSelf();
            // EngineTickService next so the 40 Hz sim steps before ClickToMoveService reads positions
            // for a click within the same frame.
            builder.RegisterEntryPoint<EngineTickService>();
            builder.RegisterEntryPoint<ClickToMoveService>();
            // Selection / hover aura (the native screen-space hull band over the selected unit +
            // the entity under the cursor). After ClickToMoveService so hover reads this frame's
            // selection.
            builder.RegisterEntryPoint<SelectionAuraService>();
            // In-game Tcl debug console (` / F10) — evals in the live context, `this` = selection.
            TclDebugConsole.Attach(_engine.Ctx);
            RemoteConsoleService.Attach();
            // The bottom context menu (dig tools / production) + the SEPARATE gnome inventory
            // panel. AsSelf so the scene-authored views inject the controllers directly.
            builder.RegisterEntryPoint<Diggles.Runtime.UI.BottomMenuController>().AsSelf();
            builder.RegisterEntryPoint<Diggles.Runtime.UI.InventoryPanelController>().AsSelf();
            // World SFX (sound play/playglobal queue + anim-frame sounds via the
            // DigglesAnimator.AnimFrameAdvanced seam). Code-registered (no scene object needed).
            builder.RegisterEntryPoint<WorldSoundService>();

            // The Inspector-wired scene controllers (AudioController / ScreenManager / ParticlePresenter
            // / CutsceneCameraDriver / ElfPresenter), registered by explicit reference.
            if (installer != null) installer.Install(builder);
        }
    }
}
