#nullable enable

namespace Diggles.Runtime.UI
{
    using System.Collections.Generic;
    using TMPro;
    using UnityEngine;
    using VContainer;

    // The bottom context menu's scene view — the layout (panel + container + the entry prefab)
    // is authored in the Unity editor; this binder spawns one entry-prefab instance per model
    // cell and refreshes them when the controller bumps the model version. Assign in the scene
    // and reference it from DigglesGameInstaller (bottomMenuView).
    //
    // Icons load at runtime from the original data (IconLoader → data/GUI/icons/<name>.tga,
    // alpha conditioned by IconAlpha) — nothing baked into the project.
    public sealed class BottomMenuView : MonoBehaviour
    {
        [Header("Layout (author these)")]
        [Tooltip("Parent for the spawned entry cells (e.g. a HorizontalLayoutGroup).")]
        [SerializeField] RectTransform? container;
        [Tooltip("The entry cell prefab carrying a BottomMenuEntryView.")]
        [SerializeField] BottomMenuEntryView? entryPrefab;
        [Tooltip("Optional: the selected object's name.")]
        [SerializeField] TMP_Text? title;
        [Tooltip("Optional: the panel root toggled off when the menu is hidden (defaults to this GO).")]
        [SerializeField] GameObject? panelRoot;

        BottomMenuController? _controller;
        IconLoader? _icons;
        int _shownVersion = -1;
        readonly List<BottomMenuEntryView> _cells = new();

        [Inject]
        public void Construct(BottomMenuController controller, IconLoader icons)
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
            if (_controller == null) return;
            var model = _controller.Model;
            if (model.Version == _shownVersion) return;
            _shownVersion = model.Version;
            Render(model);
        }

        void Render(BottomMenuModel model)
        {
            var root = panelRoot != null ? panelRoot : gameObject;
            bool visible = model.Mode != BottomMenuMode.None;
            if (root.activeSelf != visible) root.SetActive(visible);
            if (!visible) return;
            if (title != null) title.text = model.Title;
            if (container == null || entryPrefab == null) return;

            // Grow/shrink the cell pool to the entry count, then bind.
            while (_cells.Count < model.Entries.Count)
            {
                var cell = Instantiate(entryPrefab, container);
                _cells.Add(cell);
            }
            for (int i = 0; i < _cells.Count; i++)
            {
                bool used = i < model.Entries.Count;
                _cells[i].gameObject.SetActive(used);
                if (!used) continue;
                var e = model.Entries[i];
                Sprite? sprite = null;
                if (_icons != null)
                {
                    bool product = e.Id.StartsWith("prod:", System.StringComparison.Ordinal);
                    sprite = e.Disabled
                        ? _icons.GetCrossedOut(e.Icon, product)
                        : product ? _icons.GetProduct(e.Icon, !e.Dimmed) : _icons.Get(e.Icon);
                    // The S1<name> "pending" icon variants don't all ship — fall back to the
                    // base icon (the cell still dims) instead of vanishing entirely.
                    if (sprite == null && e.Icon.StartsWith("S1", System.StringComparison.Ordinal))
                        sprite = _icons.Get(e.Icon.Substring(2));
                }
                _cells[i].Bind(e, sprite, _controller!.Click);
            }
        }
    }
}
