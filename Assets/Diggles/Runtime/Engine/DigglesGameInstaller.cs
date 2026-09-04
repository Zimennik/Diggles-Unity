#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // MonoInstaller for the scene-placed Unity controllers. Add it to a scene GameObject and assign
    // the controller references in the Inspector; GameLifetimeScope calls Install during its Configure.
    //
    // Registering by explicit Inspector reference (RegisterComponent) — not by scanning the scene
    // (RegisterComponentInHierarchy) — so the wiring is visible + deterministic, with no reflection
    // search at startup. RegisterComponent force-resolves each (injects it); AsImplementedInterfaces
    // registers the IStartable so the controller's init runs. An unassigned ref is skipped (that
    // feature is simply off).
    //
    // The engine core (DigglesEngine + DigglesTclContext), SettingsService, and the tick/click entry
    // points are not here — they are runtime/by-type registrations done by GameLifetimeScope (which
    // holds the runtime-built engine). This installer is only the Inspector-wired components.
    public sealed class DigglesGameInstaller : MonoBehaviour, IInstaller
    {
        [Header("Scene controllers (assign in the Inspector)")]
        [SerializeField] AudioController? audioController;
        [SerializeField] ScreenManager? screenManager;
        [SerializeField] ParticlePresenter? particlePresenter;
        [SerializeField] CutsceneCameraDriver? cutsceneCameraDriver;
        [SerializeField] ElfPresenter? elfPresenter;
        [Header("UI (Canvas + TMP / uGUI)")]
        [SerializeField] MainMenuView? mainMenuView;
        [SerializeField] GameplayHud? gameplayHud;
        [Tooltip("The bottom context menu (dig tools / building production). " +
                 "Author the panel + the entry-cell prefab, put BottomMenuView on the panel, assign here.")]
        [SerializeField] Diggles.Runtime.UI.BottomMenuView? bottomMenuView;
        [Tooltip("The gnome inventory panel (its own control — slot grid + slot prefab with " +
                 "InventorySlotView).")]
        [SerializeField] Diggles.Runtime.UI.InventoryPanelView? inventoryPanelView;

        [Header("Data assets")]
        [Tooltip("Optional per-class override descriptors (model/anim/material/...). Empty = no overrides.")]
        [SerializeField] DigglesClassAssetRegistry? classRegistry;

        public void Install(IContainerBuilder builder)
        {
            Register(builder, audioController, nameof(AudioController));
            Register(builder, screenManager, nameof(ScreenManager));
            Register(builder, particlePresenter, nameof(ParticlePresenter));
            Register(builder, cutsceneCameraDriver, nameof(CutsceneCameraDriver));
            Register(builder, elfPresenter, nameof(ElfPresenter));
            Register(builder, mainMenuView, nameof(MainMenuView));
            Register(builder, gameplayHud, nameof(GameplayHud));
            // Optional until the panels are authored — no warning when absent (the controller
            // models still run headlessly; the views are purely presentation).
            if (bottomMenuView != null)
                builder.RegisterComponent(bottomMenuView).AsImplementedInterfaces().AsSelf();
            if (inventoryPanelView != null)
                builder.RegisterComponent(inventoryPanelView).AsImplementedInterfaces().AsSelf();

            // SO registry (not a Component → RegisterInstance). The spawner pulls it from the
            // container (in DigglesWorldRuntime) since the spawner is built before this container.
            if (classRegistry != null) builder.RegisterInstance(classRegistry);
        }

        // Register an assigned controller (force-resolve injects it; AsImplementedInterfaces runs its
        // IStartable). An unassigned ref is skipped + warned — it would otherwise go silently dead (its
        // context never injected, its LateUpdate early-returns on a null context with no log).
        static void Register<T>(IContainerBuilder builder, T? component, string label) where T : Component
        {
            if (component != null) builder.RegisterComponent(component).AsImplementedInterfaces().AsSelf();
            else Debug.LogWarning($"[DigglesGameInstaller] {label} not assigned in the Inspector — that feature is disabled.");
        }
    }
}
