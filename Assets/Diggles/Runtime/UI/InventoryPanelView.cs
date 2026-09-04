#nullable enable

namespace Diggles.Runtime.UI
{
    using System.Collections.Generic;
    using TMPro;
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.UI;
    using VContainer;

    // The gnome inventory panel's scene view — the layout (panel + two GridLayoutGroup
    // containers + a slot prefab with InventorySlotView) is authored in the Unity editor; this
    // binder spawns cells and refreshes on model version bumps. Assign in the scene and reference
    // from DigglesGameInstaller
    // (inventoryPanelView). Icons come from IconLoader at runtime.
    //
    // • mainContainer — the gnome's own grid (model.MainSlotCount cells);
    // • additionalContainer — the worn-basket bonus grid (model.ExtraSlotCount cells); the whole
    // container is hidden while there is no basket.
    //
    // Both grids keep a fixed row count (`rows`, 2 = the original look) and auto-grow sideways:
    // the container width is recomputed from the grid's own cellSize/spacing/padding, so cell size
    // can be retuned in the prefab without touching code (the parent HorizontalLayoutGroup then
    // re-flows the two grids). Design-time placeholder cells left inside the containers for layout
    // preview are cleared once before the first real fill.
    //
    // Visibility goes through the authored CanvasGroup when present (alpha fade + blocksRaycasts/
    // interactable gate, the GameObject stays active); without one it falls back to SetActive on
    // panelRoot.
    public sealed class InventoryPanelView : MonoBehaviour
    {
        [Header("Layout (author these)")]
        [FormerlySerializedAs("container")]
        [SerializeField] RectTransform? mainContainer;        // GridLayoutGroup (base slots)
        [SerializeField] RectTransform? additionalContainer;  // GridLayoutGroup (basket slots)
        [SerializeField] InventorySlotView? slotPrefab;
        [SerializeField] TMP_Text? title;
        [SerializeField] GameObject? panelRoot;               // toggled with visibility (defaults to this GO)
        [SerializeField] CanvasGroup? canvasGroup;            // optional; auto-found on panelRoot/self
        [SerializeField, Min(1)] int rows = 2;                // both grids keep this many rows
        [SerializeField, Min(0f)] float fadeDuration = 0.1f;  // CanvasGroup fade (0 = instant)

        InventoryPanelController? _controller;
        IconLoader? _icons;
        int _shownVersion = -1;
        bool _clearedAuthoringCells;
        float _fadeTarget = -1f;                              // -1 = not driven yet
        readonly List<InventorySlotView> _mainCells = new();
        readonly List<InventorySlotView> _extraCells = new();

        [Inject]
        public void Construct(InventoryPanelController controller, IconLoader icons)
        {
            _controller = controller;
            _icons = icons;
        }

        void OnEnable()
        {
            // Re-render after a ScreenManager/user toggle re-activates the panel holder.
            _shownVersion = -1;
        }

        void Update()
        {
            if (_controller != null)
            {
                var model = _controller.Model;
                if (model.Version != _shownVersion)
                {
                    _shownVersion = model.Version;
                    Render(model);
                }
            }
            TickFade();
        }

        void Render(InventoryPanelModel model)
        {
            ResolveRefs();
            ApplyVisibility(model.Visible);
            if (!model.Visible) return;
            if (title != null) title.text = model.Title;
            if (mainContainer == null || slotPrefab == null) return;
            ClearAuthoringCellsOnce();

            // No second grid wired → render everything in the main one (nothing hidden).
            int extra = additionalContainer != null ? model.ExtraSlotCount : 0;
            int main = additionalContainer != null
                ? model.MainSlotCount
                : model.MainSlotCount + model.ExtraSlotCount;

            FillGrid(mainContainer, _mainCells, model, firstItem: 0, count: main);
            if (additionalContainer != null)
            {
                bool show = extra > 0;
                if (additionalContainer.gameObject.activeSelf != show)
                    additionalContainer.gameObject.SetActive(show);
                if (show) FillGrid(additionalContainer, _extraCells, model, firstItem: main, count: extra);
            }
        }

        void FillGrid(RectTransform containerRt, List<InventorySlotView> cells, InventoryPanelModel model,
            int firstItem, int count)
        {
            while (cells.Count < count)
                cells.Add(Instantiate(slotPrefab!, containerRt));
            for (int i = 0; i < cells.Count; i++)
            {
                bool inGrid = i < count;
                cells[i].gameObject.SetActive(inGrid);
                if (!inGrid) continue;
                int idx = firstItem + i;
                var slot = idx < model.Items.Count ? model.Items[idx] : null;
                cells[i].Bind(slot, slot != null && _icons != null ? _icons.Get(slot.Icon) : null,
                    _controller!.ClickSlot);
            }
            SizeGrid(containerRt, count);
        }

        // Pin the grid to `rows` rows and grow it column-wise: width = columns worth of the
        // grid's own cell/spacing/padding values (cell size is authored, never hardcoded here).
        // The parent layout (HorizontalLayoutGroup with width control off) re-flows siblings
        // from the new rect width.
        void SizeGrid(RectTransform containerRt, int count)
        {
            var grid = containerRt.GetComponent<GridLayoutGroup>();
            if (grid == null) return;
            grid.constraint = GridLayoutGroup.Constraint.FixedRowCount;
            grid.constraintCount = rows;
            int cols = Mathf.Max(1, Mathf.CeilToInt(count / (float)rows));
            float w = grid.padding.horizontal + cols * grid.cellSize.x + (cols - 1) * grid.spacing.x;
            float h = grid.padding.vertical + rows * grid.cellSize.y + (rows - 1) * grid.spacing.y;
            containerRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);
            containerRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, h);
            LayoutRebuilder.MarkLayoutForRebuild(containerRt);
        }

        // The prefab keeps design-time placeholder cells inside the containers for layout
        // preview — drop them once, right before the first real fill.
        void ClearAuthoringCellsOnce()
        {
            if (_clearedAuthoringCells) return;
            _clearedAuthoringCells = true;
            ClearChildren(mainContainer);
            ClearChildren(additionalContainer);
        }

        static void ClearChildren(RectTransform? rt)
        {
            if (rt == null) return;
            for (int i = rt.childCount - 1; i >= 0; i--)
            {
                var child = rt.GetChild(i).gameObject;
                child.SetActive(false);      // hide now — Destroy only lands at end of frame
                Destroy(child);
            }
        }

        void ApplyVisibility(bool visible)
        {
            var rootGo = panelRoot != null ? panelRoot : gameObject;
            if (canvasGroup != null)
            {
                // CanvasGroup mode: the object stays active; alpha fades and raycasts are gated
                // so the hidden panel neither draws nor eats clicks.
                if (!rootGo.activeSelf) rootGo.SetActive(true);
                _fadeTarget = visible ? 1f : 0f;
                canvasGroup.blocksRaycasts = visible;
                canvasGroup.interactable = visible;
                if (fadeDuration <= 0f) canvasGroup.alpha = _fadeTarget;
            }
            else if (rootGo.activeSelf != visible)
            {
                rootGo.SetActive(visible);
            }
        }

        void TickFade()
        {
            if (canvasGroup == null || _fadeTarget < 0f) return;
            float a = canvasGroup.alpha;
            if (Mathf.Approximately(a, _fadeTarget)) return;
            canvasGroup.alpha = fadeDuration <= 0f
                ? _fadeTarget
                : Mathf.MoveTowards(a, _fadeTarget, Time.unscaledDeltaTime / fadeDuration);
        }

        // Field fallbacks so a not-yet-wired prefab still works: containers by their authored
        // names, the CanvasGroup from panelRoot (wherever it was authored) or this object.
        void ResolveRefs()
        {
            var searchRoot = (panelRoot != null ? panelRoot : gameObject).transform;
            if (mainContainer == null) mainContainer = FindDeep(searchRoot, "MainContainer");
            if (additionalContainer == null) additionalContainer = FindDeep(searchRoot, "AdditionalContainer");
            if (canvasGroup == null && panelRoot != null) canvasGroup = panelRoot.GetComponent<CanvasGroup>();
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
        }

        static RectTransform? FindDeep(Transform t, string childName)
        {
            if (t.name == childName) return t as RectTransform;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), childName);
                if (r != null) return r;
            }
            return null;
        }
    }
}
