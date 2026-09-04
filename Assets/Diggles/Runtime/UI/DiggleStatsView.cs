#nullable enable

namespace Diggles.Runtime.UI
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using TMPro;
    using UnityEngine;
    using UnityEngine.Serialization;

    public sealed class DiggleStatsView : MonoBehaviour
    {
        [FormerlySerializedAs("root")]
        [SerializeField] GameObject? panelRoot;
        [FormerlySerializedAs("diggleName")]
        [SerializeField] TMP_Text? nameText;
        [FormerlySerializedAs("diggleWork")]
        [SerializeField] TMP_Text? activityText;

        [Header("Vitals")]
        [FormerlySerializedAs("hitpoints")]
        [SerializeField] DiggleStatView? health;
        [FormerlySerializedAs("saturation")]
        [SerializeField] DiggleStatView? nutrition;
        [FormerlySerializedAs("alertness")]
        [SerializeField] DiggleStatView? attention;
        [SerializeField] DiggleStatView? mood;

        [Header("Knowledge")]
        [FormerlySerializedAs("experiencesHolder")]
        [SerializeField] RectTransform? knowledgeContainer;
        [FormerlySerializedAs("experiencePrefab")]
        [SerializeField] DiggleExpView? knowledgePrefab;

        sealed class KnowledgeBinding
        {
            public string Name = string.Empty;
            public string Label = string.Empty;
            public DiggleExpView View = null!;
            public bool Instantiated;
        }

        readonly List<KnowledgeBinding> knowledge = new();
        readonly List<KnowledgeBinding> nextKnowledge = new();
        readonly Dictionary<string, KnowledgeBinding> knowledgeByName = new();
        readonly HashSet<DiggleExpView> usedViews = new();
        readonly List<DiggleExpView> authoredKnowledgeRows = new();
        readonly List<DiggleInfoPanelLogic.KnowledgeCandidate> knowledgeCandidates = new();

        DigglesTclContext? boundContext;
        bool referencesResolved;
        bool visible;
        bool warnedMissing;
        bool warnedKnowledge;
        string? shownName;
        string? shownActivity;

        void Awake() => ResolveReferences();

        void OnDestroy() => ClearKnowledgeRows();

        CanvasGroup? visibilityGroup;

        public void SetVisible(bool value)
        {
            ResolveReferences();
            bool becameVisible = value && !visible;
            visible = value;
            GameObject root = panelRoot != null ? panelRoot : gameObject;
            // Hide via CanvasGroup, not SetActive: the stats root shares the authored layout
            // chain with the gnome list, and a SetActive toggle reflows that group — the whole
            // list jumped on every select/deselect. The native right panel reserves a fixed
            // stats zone; alpha-hiding keeps the authored rect in layout so nothing above it
            // ever moves.
            if (!root.activeSelf) root.SetActive(true);   // authored-inactive roots join the layout once
            if (visibilityGroup == null)
            {
                visibilityGroup = root.GetComponent<CanvasGroup>();
                if (visibilityGroup == null) visibilityGroup = root.AddComponent<CanvasGroup>();
            }
            visibilityGroup.alpha = value ? 1f : 0f;
            visibilityGroup.blocksRaycasts = value;
            visibilityGroup.interactable = value;
            // Onappearance, mark only the dynamic-content layout root (the knowledge
            // container, whose rows changed while hidden). Never force-rebuild the stats/panel
            // root rect itself: treating it as a layout root re-runs its own fitter chain and
            // re-expands the authored rect (the refresh overflow past the screen top).
            if (becameVisible && knowledgeContainer != null)
                UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(knowledgeContainer);
        }

        public void Refresh(DigglesTclContext context, DigglesTclEntity entity, bool isInsideWorktime)
        {
            ResolveReferences();
            if (!visible || context == null || entity == null) return;

            if (!ReferenceEquals(boundContext, context))
            {
                ClearKnowledgeRows();
                boundContext = context;
            }

            if (nameText != null && shownName != entity.InstanceName)
            {
                shownName = entity.InstanceName;
                nameText.text = entity.InstanceName;
            }

            // Stable activity sources only (the native planner-record law — see
            // DiggleInfoStrings.ActivityFor): the ported record verb (ProdState), the
            // dispatcher carrier plan, and the workplace claim. Never the live micro-action.
            bool hasCarrierPlan = context.ProdDispatcher != null
                && context.ProdDispatcher.HasPlan(entity.Handle);
            bool hasWorkplaceClaim = false;
            foreach (KeyValuePair<int, DigglesTclEntity> pair in context.Entities)
                if (!ReferenceEquals(pair.Value, entity)
                    && pair.Value.CurrentWorkerHandle == entity.Handle)
                { hasWorkplaceClaim = true; break; }
            string activity = DiggleInfoStrings.ActivityFor(
                entity.ProdState, hasCarrierPlan, hasWorkplaceClaim, isInsideWorktime);
            if (activityText != null && shownActivity != activity)
            {
                shownActivity = activity;
                activityText.text = activity;
            }

            // Exact vital names/ranges: misc/genattribs.tcl:20-23. All four are normalized 0..1.
            health?.SetValue(DiggleInfoStrings.Health,
                context.Stats.Get(entity.Stats, DiggleInfoStrings.HitpointsAttribute));
            nutrition?.SetValue(DiggleInfoStrings.Nutrition,
                context.Stats.Get(entity.Stats, DiggleInfoStrings.NutritionAttribute));
            attention?.SetValue(DiggleInfoStrings.Attention,
                context.Stats.Get(entity.Stats, DiggleInfoStrings.AlertnessAttribute));
            mood?.SetValue(DiggleInfoStrings.Mood,
                context.Stats.Get(entity.Stats, DiggleInfoStrings.MoodAttribute));

            BuildKnowledgeCandidates(context, entity);
            SyncKnowledgeRows();
            for (int i = 0; i < knowledge.Count; i++)
            {
                KnowledgeBinding row = knowledge[i];
                for (int c = 0; c < knowledgeCandidates.Count; c++)
                {
                    if (knowledgeCandidates[c].Name != row.Name) continue;
                    row.View.SetValue(row.Label, knowledgeCandidates[c].Value);
                    break;
                }
            }
        }

        // The native knowledge-row law (FUN_00536f90:263-463, see DiggleInfoPanelLogic):
        // exp_-prefixed attributes only, exp_Kampf excluded, value > 0 required, sorted
        // descending by value, top 5. The gnome only lists the skills it actually has.
        void BuildKnowledgeCandidates(DigglesTclContext context, DigglesTclEntity entity)
        {
            knowledgeCandidates.Clear();
            IReadOnlyList<string> definitions = context.Stats.Registry.DefinedInOrder;
            for (int i = 0; i < definitions.Count; i++)
            {
                string name = definitions[i];
                if (!DiggleInfoPanelLogic.IsKnowledgeSkill(name)) continue;
                double value = context.Stats.Get(entity.Stats, name);
                if (!DiggleInfoPanelLogic.IsKnowledgeRowVisible(name, value)) continue;
                knowledgeCandidates.Add(new DiggleInfoPanelLogic.KnowledgeCandidate(name, value));
            }
            DiggleInfoPanelLogic.OrderKnowledgeRows(knowledgeCandidates);
        }

        void ResolveReferences()
        {
            if (referencesResolved) return;
            referencesResolved = true;
            if (panelRoot == null) panelRoot = gameObject;
            Transform root = panelRoot.transform;
            if (nameText == null)
                nameText = DiggleUiBindings.FindComponent<TMP_Text>(root, "DiggleName");
            if (activityText == null)
                activityText = DiggleUiBindings.FindComponent<TMP_Text>(root, "DiggleWork");
            if (health == null)
                health = DiggleUiBindings.FindComponent<DiggleStatView>(root, "DiggleHealth");
            if (nutrition == null)
                nutrition = DiggleUiBindings.FindComponent<DiggleStatView>(root, "DiggleSaturation")
                    ?? DiggleUiBindings.FindComponent<DiggleStatView>(root, "DiggleNutrition");
            if (attention == null)
                attention = DiggleUiBindings.FindComponent<DiggleStatView>(root, "DiggleAlertnes")
                    ?? DiggleUiBindings.FindComponent<DiggleStatView>(root, "DiggleAlertness");
            if (mood == null)
                mood = DiggleUiBindings.FindComponent<DiggleStatView>(root, "DiggleMood");
            if (knowledgeContainer == null)
                knowledgeContainer = DiggleUiBindings.FindDeep(root, "DiggleExperiences") as RectTransform;

            if (knowledgeContainer != null)
            {
                DiggleExpView[] existing = knowledgeContainer.GetComponentsInChildren<DiggleExpView>(true);
                authoredKnowledgeRows.AddRange(existing);
                for (int i = 0; i < existing.Length; i++) existing[i].gameObject.SetActive(false);
            }

            if (nameText == null || activityText == null || health == null
                || nutrition == null || attention == null || mood == null)
                DiggleUiBindings.WarnMissingOnce(this, ref warnedMissing,
                    "DiggleStatsView is missing one or more authored name/activity/vital bindings; available fields will still update.");
        }

        void SyncKnowledgeRows()
        {
            bool same = knowledgeCandidates.Count == knowledge.Count;
            if (same)
            {
                for (int i = 0; i < knowledgeCandidates.Count; i++)
                    if (knowledge[i].Name != knowledgeCandidates[i].Name) { same = false; break; }
            }
            if (same) return;

            knowledgeByName.Clear();
            usedViews.Clear();
            for (int i = 0; i < knowledge.Count; i++) knowledgeByName[knowledge[i].Name] = knowledge[i];
            nextKnowledge.Clear();

            for (int i = 0; i < knowledgeCandidates.Count; i++)
            {
                string name = knowledgeCandidates[i].Name;
                if (!knowledgeByName.TryGetValue(name, out KnowledgeBinding? binding))
                {
                    DiggleExpView? row = TakeUnusedAuthoredRow();
                    bool instantiated = false;
                    if (row == null && knowledgeContainer != null && knowledgePrefab != null)
                    {
                        row = Instantiate(knowledgePrefab, knowledgeContainer);
                        instantiated = true;
                    }
                    if (row == null)
                    {
                        DiggleUiBindings.WarnMissingOnce(this, ref warnedKnowledge,
                            "DiggleStatsView needs a DiggleExpView prefab or authored knowledge rows; knowledge data will be skipped.");
                        continue;
                    }
                    binding = new KnowledgeBinding
                    {
                        Name = name,
                        Label = DiggleInfoStrings.SkillLabel(name),
                        View = row,
                        Instantiated = instantiated
                    };
                }

                binding.View.gameObject.SetActive(true);
                binding.View.transform.SetSiblingIndex(nextKnowledge.Count);
                usedViews.Add(binding.View);
                nextKnowledge.Add(binding);
            }

            for (int i = 0; i < knowledge.Count; i++)
            {
                KnowledgeBinding old = knowledge[i];
                if (usedViews.Contains(old.View)) continue;
                if (old.Instantiated) Destroy(old.View.gameObject);
                else old.View.gameObject.SetActive(false);
            }

            knowledge.Clear();
            knowledge.AddRange(nextKnowledge);

            // Knowledge rows were activated/instantiated/reordered — mark only their own
            // layout root (the knowledge container) and let this frame's layout pass rebuild
            // it. Mutation-gated by the early `same` return above. Never the panel/stats root
            // rect: force-rebuilding an outer root re-runs its fitter chain and re-expands the
            // authored rect past the screen top.
            if (knowledgeContainer != null)
                UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(knowledgeContainer);
        }

        DiggleExpView? TakeUnusedAuthoredRow()
        {
            for (int i = 0; i < authoredKnowledgeRows.Count; i++)
            {
                DiggleExpView row = authoredKnowledgeRows[i];
                if (row == null || usedViews.Contains(row) || IsCurrentlyBound(row)) continue;
                return row;
            }
            return null;
        }

        bool IsCurrentlyBound(DiggleExpView row)
        {
            for (int i = 0; i < knowledge.Count; i++)
                if (knowledge[i].View == row) return true;
            return false;
        }

        void ClearKnowledgeRows()
        {
            for (int i = 0; i < knowledge.Count; i++)
            {
                KnowledgeBinding row = knowledge[i];
                if (row.View == null) continue;
                if (row.Instantiated) Destroy(row.View.gameObject);
                else row.View.gameObject.SetActive(false);
            }
            knowledge.Clear();
            nextKnowledge.Clear();
            knowledgeByName.Clear();
            usedViews.Clear();
        }
    }
}
