#nullable enable

namespace Diggles.Runtime.UI
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using UnityEngine.Serialization;
    using VContainer;

    public sealed class DiggleInfoPanelController : MonoBehaviour
    {
        [Header("Authored panel bindings")]
        [FormerlySerializedAs("group")]
        [SerializeField] CanvasGroup? canvasGroup;
        [FormerlySerializedAs("rowsHolder")]
        [SerializeField] RectTransform? digglesHolder;
        [FormerlySerializedAs("itemPrefab")]
        [SerializeField] DiggleItemView? diggleItemPrefab;
        [FormerlySerializedAs("detailsView")]
        [SerializeField] DiggleStatsView? statsView;
        [SerializeField, Min(0f)] float fadeDuration = 0.1f;
        [SerializeField, Min(0.05f)] float refreshInterval = 0.25f;

        sealed class RowBinding
        {
            public DiggleItemView View = null!;
            public string WorktimeCommand = string.Empty;
            public bool IsInsideWorktime;
        }

        readonly DiggleRoster<RowBinding> roster = new();
        readonly List<int> desiredHandles = new();
        readonly HashSet<int> claimedWorkers = new();

        IActiveGameSession? sessions;
        DigglesTclContext? explicitContext;
        DigglesTclContext? observedContext;
        DigglesWorldRuntime? worldRuntime;
        float nextWorldLookup;
        float nextRefresh;
        float fadeTarget = -1f;
        int lastSelectionVersion = int.MinValue;
        int primaryGnomeHandle;
        string primaryWorktimeCommand = string.Empty;
        bool panelVisible;
        bool referencesResolved;
        bool subscribed;
        bool warnedMissing;
        bool warnedContext;
        bool warnedWorktime;

        [Inject]
        public void Construct(IActiveGameSession activeSessions) => sessions = activeSessions;

        // Headless/manual binding seam. Normal scene use resolves IActiveGameSession through DI;
        // the DigglesWorldRuntime fallback lets the authored scene component degrade safely if it
        // has not yet been added to the installer registration list.
        public void BindContext(DigglesTclContext context)
        {
            explicitContext = context ?? throw new ArgumentNullException(nameof(context));
            ObserveContext(context);
        }

        void Awake()
        {
            ResolveReferences();
            ApplyPanelVisibility();
            statsView?.SetVisible(false);
        }

        void OnEnable()
        {
            Subscribe();
            nextRefresh = 0f;
            lastSelectionVersion = int.MinValue;
            ApplyPanelVisibility();
        }

        void OnDisable() => Unsubscribe();

        void OnDestroy()
        {
            Unsubscribe();
            roster.Clear(ReleaseRow);
        }

        void Update()
        {
            DigglesTclContext? context = ResolveContext();
            if (!ReferenceEquals(context, observedContext)) ObserveContext(context);

            if (context != null)
            {
                if (context.Selection.Version != lastSelectionVersion)
                {
                    lastSelectionVersion = context.Selection.Version;
                    UpdateSelectionState(context, autoOpenGnomes: true);
                }

                if (Time.unscaledTime >= nextRefresh)
                {
                    nextRefresh = Time.unscaledTime + Mathf.Max(0.05f, refreshInterval);
                    RefreshRoster(context);
                    UpdateSelectionState(context, autoOpenGnomes: false);
                    RefreshStats(context);
                }
            }

            // The world/cutscene gates flip outside any panel event (cutscene start, menu ↔
            // gameplay session swap) — re-evaluate every frame so the hide edge is immediate.
            ApplyPanelVisibility();
            TickFade();
        }

        public void SetCollapsed(bool collapsed) => RightPanelModel.SetCollapsed(collapsed);

        public void SetMode(RightPanelMode mode) => RightPanelModel.SetMode(mode);

        public void ToggleMode() => RightPanelModel.RequestToggle();

        public void SelectFromUi(int handle)
        {
            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            SelectFromUi(handle, shiftHeld);
        }

        public void SelectFromUi(int handle, bool additive)
        {
            DigglesTclContext? context = ResolveContext();
            if (context == null)
            {
                DiggleUiBindings.WarnMissingOnce(this, ref warnedContext,
                    "DiggleInfoPanelController cannot select a row until an engine context is available.");
                return;
            }

            // The world click path uses this exact call; keeping the route shared preserves the
            // native replace/add/can-include behavior (FUN_005309c0:79-95) and all observers.
            context.Selection.ClickSelect(handle, additive);
        }

        void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            RightPanelModel.ModeChanged += OnModeChanged;
            RightPanelModel.CollapsedChanged += OnCollapsedChanged;
        }

        void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            RightPanelModel.ModeChanged -= OnModeChanged;
            RightPanelModel.CollapsedChanged -= OnCollapsedChanged;
        }

        void OnModeChanged(RightPanelMode _) => ApplyPanelVisibility();
        void OnCollapsedChanged(bool _) => ApplyPanelVisibility();

        void ObserveContext(DigglesTclContext? context)
        {
            if (ReferenceEquals(observedContext, context)) return;
            observedContext = context;
            roster.Clear(ReleaseRow);
            desiredHandles.Clear();
            primaryGnomeHandle = 0;
            primaryWorktimeCommand = string.Empty;
            lastSelectionVersion = int.MinValue;
            nextRefresh = 0f;
            statsView?.SetVisible(false);
        }

        DigglesTclContext? ResolveContext()
        {
            if (explicitContext != null) return explicitContext;
            if (sessions != null) return sessions.Current.Engine.Ctx;
            if (worldRuntime != null && worldRuntime.Context != null) return worldRuntime.Context;
            if (Time.unscaledTime < nextWorldLookup) return null;
            nextWorldLookup = Time.unscaledTime + 1f;
            worldRuntime = FindFirstObjectByType<DigglesWorldRuntime>();
            return worldRuntime != null ? worldRuntime.Context : null;
        }

        void RefreshRoster(DigglesTclContext context)
        {
            desiredHandles.Clear();
            claimedWorkers.Clear();
            foreach (KeyValuePair<int, DigglesTclEntity> pair in context.Entities)
            {
                if (IsRosterGnome(context, pair.Value)) desiredHandles.Add(pair.Key);
                // Workplace claims (native rec+0x40 workplace binding): a bench worker holds
                // no dispatcher step plan, so collect who is bound to a place this refresh.
                if (pair.Value.CurrentWorkerHandle > 0)
                    claimedWorkers.Add(pair.Value.CurrentWorkerHandle);
            }
            desiredHandles.Sort();

            ResolveReferences();
            if (digglesHolder == null || diggleItemPrefab == null) return;
            bool rosterChanged = roster.Sync(desiredHandles, CreateRow, ReleaseRow);
            for (int i = 0; i < roster.Handles.Count; i++)
            {
                int handle = roster.Handles[i];
                if (!roster.TryGet(handle, out RowBinding? row)
                    || !context.Entities.TryGetValue(handle, out DigglesTclEntity? entity)) continue;

                row.View.transform.SetSiblingIndex(i);
                row.IsInsideWorktime = QueryWorktime(context, row.WorktimeCommand, row.IsInsideWorktime);
                // The native row reads the auto-schedule flag (obj+0x3d0 bit 0x800000,
                // FUN_00535490:230-249) — the port mirror is entity.ProdAutoSchedule. The
                // job half is the planner record's binding/queue (rec+0x40/+0x34): projected
                // as "has a dispatcher step plan OR is claimed by a workplace".
                bool hasJob = (context.ProdDispatcher != null && context.ProdDispatcher.HasPlan(handle))
                    || claimedWorkers.Contains(handle);
                DiggleWorkStatus status = DiggleInfoPanelLogic.ResolveWorkStatus(
                    entity.ProdAutoSchedule, row.IsInsideWorktime, hasJob);
                double hitpoints = context.Stats.Get(entity.Stats, DiggleInfoStrings.HitpointsAttribute);
                double mood = context.Stats.Get(entity.Stats, DiggleInfoStrings.MoodAttribute);
                row.View.Render(entity.InstanceName, hitpoints, status,
                    DiggleInfoPanelLogic.ResolveMood(mood), context.Selection.Contains(handle));
            }

            // Row creation/removal/reordering dirties only the rows' own layout chain — mark
            // digglesHolder (the group that owns the rows) and let CanvasUpdateRegistry rebuild
            // it in this frame's layout pass. Never rebuild the panel root rect: forcing the
            // root treats it as a layout root and its own fitter chain re-expands the authored
            // rect (the "list climbs past the top of the screen" overflow). Deferred marking
            // still lands before this frame renders, so no immediate rebuild is needed.
            if (rosterChanged && digglesHolder != null)
                UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(digglesHolder);
        }

        RowBinding CreateRow(int handle)
        {
            DiggleItemView rowView = Instantiate(diggleItemPrefab!, digglesHolder!);
            rowView.Bind(this, handle);
            return new RowBinding
            {
                View = rowView,
                WorktimeCommand = BuildWorktimeCommand(handle)
            };
        }

        static void ReleaseRow(RowBinding row)
        {
            if (row.View == null) return;
            row.View.gameObject.SetActive(false);
            Destroy(row.View.gameObject);
        }

        void UpdateSelectionState(DigglesTclContext context, bool autoOpenGnomes)
        {
            int primary = context.SelectedHandle;
            bool ownsPrimaryGnome = primary > 0
                && context.Entities.TryGetValue(primary, out DigglesTclEntity? entity)
                && IsRosterGnome(context, entity);

            int newPrimary = ownsPrimaryGnome ? primary : 0;
            if (newPrimary != primaryGnomeHandle)
            {
                primaryGnomeHandle = newPrimary;
                primaryWorktimeCommand = newPrimary > 0 ? BuildWorktimeCommand(newPrimary) : string.Empty;
            }
            if (autoOpenGnomes && ownsPrimaryGnome) RightPanelModel.SetOpen(true);

            for (int i = 0; i < roster.Handles.Count; i++)
            {
                int handle = roster.Handles[i];
                if (roster.TryGet(handle, out RowBinding? row))
                    row.View.SetSelected(context.Selection.Contains(handle));
            }

            ApplyPanelVisibility();
        }

        void RefreshStats(DigglesTclContext context)
        {
            if (statsView == null || primaryGnomeHandle <= 0
                || !context.Entities.TryGetValue(primaryGnomeHandle, out DigglesTclEntity? entity)) return;

            bool inWorktime;
            if (roster.TryGet(primaryGnomeHandle, out RowBinding? row))
                inWorktime = row.IsInsideWorktime;
            else
                inWorktime = QueryWorktime(context, primaryWorktimeCommand, previous: false);
            statsView.Refresh(context, entity, inWorktime);
        }

        bool QueryWorktime(DigglesTclContext context, string command, bool previous)
        {
            if (string.IsNullOrEmpty(command)) return previous;
            try
            {
                // Reuse the engine command that owns the work-window law. The native command
                // reads its worktime flag at FUN_006955d0:58-70; the dispatcher consumes that
                // same flag when assigning work at CProdPlanner_MaintenanceTick:110-113.
                string result = context.Interp.Eval(command);
                return double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out double remaining)
                    ? Math.Abs(remaining) < 0.0001d
                    : previous;
            }
            catch (Exception exception)
            {
                if (!warnedWorktime)
                {
                    warnedWorktime = true;
                    Debug.LogWarning("DiggleInfoPanelController could not query the shared worktime command: "
                        + exception.Message, this);
                }
                return previous;
            }
        }

        static string BuildWorktimeCommand(int handle)
            => "get_remaining_sparetime " + handle.ToString(CultureInfo.InvariantCulture);

        static bool IsRosterGnome(DigglesTclContext context, DigglesTclEntity entity)
        {
            // An undefined attribute reads as zero, so only interpret HP<=0 as death once the
            // authoritative def_attrib has actually reached this session's registry.
            bool hpDefined = context.Stats.Registry.TryGet(DiggleInfoStrings.HitpointsAttribute, out _);
            double hp = hpDefined
                ? context.Stats.Get(entity.Stats, DiggleInfoStrings.HitpointsAttribute)
                : 0d;
            return DiggleInfoPanelLogic.IsRosterMember(
                entity.Class?.Kind,
                entity.Owner,
                context.LocalPlayer,
                entity.DiedInFight,
                entity.DeleteInProgress,
                hpDefined,
                hp);
        }

        void ApplyPanelVisibility()
        {
            ResolveReferences();
            DigglesTclContext? context = observedContext;
            // Menu gate — the canonical letterbox law pair (ScreenManager.ShouldShowLetterbox
            // mirrors DigglesWorldRuntime.BindSession:322 _atMenu = OriginalSaveState == null
            // && !IsStoryMgr; read both). An explicitly bound context (the headless/manual
            // seam) is by definition a gameplay world — it has no session to interrogate.
            bool inGameplayWorld = explicitContext != null
                || (context != null && DiggleInfoPanelLogic.IsGameplayWorld(
                    context.IsStoryMgr,
                    sessions != null && sessions.Current.OriginalSaveState != null));
            // Cutscene gate — the native HUD yields to sequences (same ctx.SequenceActive
            // gate the bottom menu uses).
            bool sequenceActive = context != null && context.SequenceActive;
            panelVisible = DiggleInfoPanelLogic.IsPanelVisible(
                RightPanelModel.Collapsed, RightPanelModel.Mode, inGameplayWorld, sequenceActive);
            fadeTarget = panelVisible ? 1f : 0f;
            if (canvasGroup != null)
            {
                canvasGroup.blocksRaycasts = panelVisible;
                canvasGroup.interactable = panelVisible;
                if (fadeDuration <= 0f) canvasGroup.alpha = fadeTarget;
            }
            statsView?.SetVisible(DiggleInfoPanelLogic.IsStatsVisible(
                panelVisible, primaryGnomeHandle > 0));
        }

        void TickFade()
        {
            if (canvasGroup == null || fadeTarget < 0f
                || Mathf.Approximately(canvasGroup.alpha, fadeTarget)) return;
            canvasGroup.alpha = fadeDuration <= 0f
                ? fadeTarget
                : Mathf.MoveTowards(canvasGroup.alpha, fadeTarget,
                    Time.unscaledDeltaTime / fadeDuration);
        }

        void ResolveReferences()
        {
            if (referencesResolved) return;
            referencesResolved = true;
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            if (digglesHolder == null)
                digglesHolder = DiggleUiBindings.FindDeep(transform, "DigglesHolder") as RectTransform;
            if (statsView == null)
                statsView = DiggleUiBindings.FindComponent<DiggleStatsView>(transform, "DigglesStats");

            if (canvasGroup == null || digglesHolder == null || diggleItemPrefab == null || statsView == null)
                DiggleUiBindings.WarnMissingOnce(this, ref warnedMissing,
                    "DiggleInfoPanelController is missing one or more authored panel/holder/prefab/stats bindings; available bindings will still run.");
        }
    }
}
