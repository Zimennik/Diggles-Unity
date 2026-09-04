#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using TMPro;
    using UnityEngine;
    using UnityEngine.UI;

    // Code-built presentation for the original ViWinNewsTicker. The native root is positioned at
    // (195,18), is 28 logical pixels high, and spans screenWidth-460 pixels
    // (ViWinRoot::Init/FUN_00527fa0). Its fill/border colors come from FUN_00519300. The model and
    // Tcl ownership stay in NewsTickerSystem; this component only renders and forwards clicks.
    public sealed class NewsTickerWidget : MonoBehaviour
    {
        const float UiScale = 1080f / 600f;
        const float LeftInset = 195f;
        const float RightInset = 265f;
        const float TopInset = 18f;
        const float Height = 28f;
        const float ExpandedHeight = 190f;
        const float ToggleSize = 32f;
        const int HistoryRows = 8;
        const float ScrollSpeed = 34f * UiScale;

        DigglesTclContext? _context;
        RectTransform? _root;
        RectTransform? _viewport;
        RectTransform? _content;
        RectTransform? _historyContent;
        CanvasGroup? _group;
        int _shownVersion = -1;
        bool _shownGameplay;
        bool _visible;
        bool _expanded;
        float _scrollX;
        Image? _toggleImage;
        Sprite? _toggleDown;
        Sprite? _toggleUp;
        readonly List<NewsTickerEntry> _entries = new();
        readonly List<Cell> _cells = new();
        readonly List<Cell> _historyCells = new();
        readonly List<NewsTickerDisplayQueueEntry> _displayQueue = new();

        public static NewsTickerWidget Create(RectTransform parent, DigglesTclContext context)
        {
            var go = new GameObject("NewsTicker", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(LeftInset * UiScale, -((TopInset + Height) * UiScale));
            rect.offsetMax = new Vector2(-(RightInset - ToggleSize) * UiScale, -(TopInset * UiScale));

            var widget = go.AddComponent<NewsTickerWidget>();
            widget._context = context;
            widget._root = rect;
            widget.BuildUi();
            return widget;
        }

        void BuildUi()
        {
            var background = gameObject.AddComponent<Image>();
            background.color = new Color32(0x14, 0x14, 0x14, 0xbe);
            background.raycastTarget = true;
            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
            var outline = gameObject.AddComponent<Outline>();
            outline.effectColor = new Color32(0xc8, 0xc8, 0xc8, 0x78);
            outline.effectDistance = new Vector2(1f, -1f);

            var viewportObject = new GameObject("MessageViewport", typeof(RectTransform));
            var viewport = (RectTransform)viewportObject.transform;
            _viewport = viewport;
            viewport.SetParent(transform, false);
            viewport.anchorMin = new Vector2(0f, 1f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.pivot = new Vector2(0.5f, 1f);
            viewport.offsetMin = new Vector2(0f, -(Height * UiScale));
            viewport.offsetMax = new Vector2(-(ToggleSize * UiScale), 0f);
            viewportObject.AddComponent<RectMask2D>();

            var contentObject = new GameObject("Messages", typeof(RectTransform));
            _content = (RectTransform)contentObject.transform;
            _content.SetParent(viewport, false);
            _content.anchorMin = new Vector2(0f, 0f);
            _content.anchorMax = new Vector2(0f, 1f);
            _content.pivot = new Vector2(0f, 0.5f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;

            var layout = contentObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 2, 2);
            layout.spacing = 18f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            var fitter = contentObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            BuildToggle();
            BuildHistory();
        }

        void BuildToggle()
        {
            _toggleDown = LoadSprite("GUI/buttons/LO_Pfeil_down.tga");
            _toggleUp = LoadSprite("GUI/buttons/LO_Pfeil_up.tga");
            var toggleObject = new GameObject("ToggleHistory", typeof(RectTransform));
            var rect = (RectTransform)toggleObject.transform;
            rect.SetParent(transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.one * (ToggleSize * UiScale);
            _toggleImage = toggleObject.AddComponent<Image>();
            _toggleImage.sprite = _toggleDown;
            _toggleImage.preserveAspect = true;
            _toggleImage.raycastTarget = true;
            var button = toggleObject.AddComponent<Button>();
            button.targetGraphic = _toggleImage;
            button.onClick.AddListener(ToggleHistory);
        }

        void BuildHistory()
        {
            var historyObject = new GameObject("History", typeof(RectTransform));
            _historyContent = (RectTransform)historyObject.transform;
            _historyContent.SetParent(transform, false);
            _historyContent.anchorMin = new Vector2(0f, 1f);
            _historyContent.anchorMax = new Vector2(1f, 1f);
            _historyContent.pivot = new Vector2(0.5f, 1f);
            _historyContent.offsetMin = new Vector2(6f, -(ExpandedHeight * UiScale));
            _historyContent.offsetMax = new Vector2(-(ToggleSize * UiScale + 6f), -(Height * UiScale));
            var layout = historyObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 1f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            historyObject.SetActive(false);
        }

        void Update()
        {
            if (_context == null || _content == null || _viewport == null) return;
            bool gameplay = _context.IsStoryMgr && !_context.SequenceActive && _context.Sim != null;
            if (_shownVersion != _context.NewsTicker.Version || _shownGameplay != gameplay)
            {
                _shownVersion = _context.NewsTicker.Version;
                _shownGameplay = gameplay;
                Render(gameplay);
            }
            if (!_visible) return;

            float viewportWidth = _viewport.rect.width;
            float contentWidth = LayoutUtility.GetPreferredWidth(_content);
            if (_entries.Count == 0)
            {
                _scrollX = 0f;
            }
            else
            {
                _scrollX -= Time.unscaledDeltaTime * ScrollSpeed;
                if (_scrollX < -contentWidth)
                    _scrollX = viewportWidth - CellLeft(0);
            }
            _content.anchoredPosition = new Vector2(_scrollX, 0f);
            PublishDisplayQueue();
        }

        void Render(bool gameplay)
        {
            if (_context == null || _content == null) return;
            _context.NewsTicker.CopyVisibleEntries(_entries);
            bool visible = gameplay;
            _visible = visible;
            if (_group != null)
            {
                _group.alpha = visible ? 1f : 0f;
                _group.interactable = visible;
                _group.blocksRaycasts = visible;
            }
            if (!visible)
            {
                ClearDisplayQueue();
                return;
            }

            while (_cells.Count < _entries.Count) _cells.Add(CreateCell());
            for (int i = 0; i < _cells.Count; i++)
            {
                bool used = i < _entries.Count;
                _cells[i].Root.SetActive(used);
                if (used) Bind(_cells[i], _entries[i]);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            RestoreOrInitializeScrollPosition();
            _content.anchoredPosition = new Vector2(_scrollX, 0f);
            PublishDisplayQueue();
            RenderHistory();
        }

        Cell CreateCell()
        {
            var root = new GameObject("Message", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(_content, false);
            var image = root.AddComponent<Image>();
            image.color = new Color32(255, 255, 255, 10);
            image.raycastTarget = true;
            var button = root.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1.35f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = Color.white;
            button.colors = colors;
            var element = root.AddComponent<LayoutElement>();
            element.minHeight = Height * UiScale - 4f;

            var textObject = new GameObject("Text", typeof(RectTransform));
            var textRect = (RectTransform)textObject.transform;
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 0f);
            textRect.offsetMax = new Vector2(-10f, 0f);
            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = 22f;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            return new Cell(rect, root, button, element, text);
        }

        Cell CreateHistoryCell()
        {
            var cell = CreateCell();
            cell.Root.transform.SetParent(_historyContent, false);
            cell.Layout.minHeight = 18f * UiScale;
            cell.Layout.preferredHeight = 18f * UiScale;
            cell.Text.fontSize = 19f;
            return cell;
        }

        void Bind(Cell cell, NewsTickerEntry entry)
        {
            cell.Text.text = entry.Text;
            cell.Text.color = entry.Color;
            cell.Layout.preferredWidth = Mathf.Max(80f, cell.Text.GetPreferredValues(entry.Text).x + 24f);
            cell.Button.onClick.RemoveAllListeners();
            int id = entry.Id;
            cell.Button.interactable = !string.IsNullOrEmpty(entry.ClickScript);
            if (cell.Button.interactable)
                cell.Button.onClick.AddListener(() => _context?.NewsTicker.Click(id));
        }

        void ToggleHistory()
        {
            _expanded = !_expanded;
            ApplyExpandedGeometry();
            RenderHistory();
        }

        void ApplyExpandedGeometry()
        {
            if (_root != null)
            {
                var minimum = _root.offsetMin;
                minimum.y = -((TopInset + (_expanded ? ExpandedHeight : Height)) * UiScale);
                _root.offsetMin = minimum;
            }
            if (_historyContent != null) _historyContent.gameObject.SetActive(_expanded);
            if (_toggleImage != null) _toggleImage.sprite = _expanded ? _toggleUp : _toggleDown;
        }

        void RenderHistory()
        {
            if (!_expanded || _context == null || _historyContent == null) return;
            int count = Mathf.Min(HistoryRows, _context.NewsTicker.HistoryEntries.Count);
            while (_historyCells.Count < count) _historyCells.Add(CreateHistoryCell());
            for (int i = 0; i < _historyCells.Count; i++)
            {
                bool used = i < count;
                _historyCells[i].Root.SetActive(used);
                if (used) Bind(_historyCells[i], _context.NewsTicker.HistoryEntries[i]);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(_historyContent);
        }

        void RestoreOrInitializeScrollPosition()
        {
            if (_context == null || _viewport == null || _entries.Count == 0)
            {
                _scrollX = 0f;
                return;
            }

            IReadOnlyList<NewsTickerDisplayQueueEntry> restored =
                _context.NewsTicker.DisplayQueueEntries;
            for (int queueIndex = 0; queueIndex < restored.Count; queueIndex++)
            {
                for (int entryIndex = 0; entryIndex < _entries.Count; entryIndex++)
                {
                    if (_entries[entryIndex].Id != restored[queueIndex].Id) continue;
                    _scrollX = restored[queueIndex].Position - CellLeft(entryIndex);
                    return;
                }
            }

            _scrollX = _viewport.rect.width - CellLeft(0);
        }

        void PublishDisplayQueue()
        {
            if (_context == null) return;
            _displayQueue.Clear();
            for (int index = 0; index < _entries.Count; index++)
            {
                _displayQueue.Add(new NewsTickerDisplayQueueEntry(
                    _entries[index].Id,
                    _scrollX + CellLeft(index)));
            }
            _context.NewsTicker.ReplaceDisplayQueue(_displayQueue);
        }

        void ClearDisplayQueue()
        {
            if (_context == null) return;
            _displayQueue.Clear();
            _context.NewsTicker.ReplaceDisplayQueue(_displayQueue);
        }

        float CellLeft(int index) =>
            _cells[index].Rect.anchoredPosition.x + _cells[index].Rect.rect.xMin;

        Sprite? LoadSprite(string relativePath)
        {
            if (_context == null || string.IsNullOrEmpty(_context.DataRoot)) return null;
            string path = Path.Combine(_context.DataRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var result = TgaLoader.LoadFromFile(path);
                if (result == null) return null;
                result.Texture.wrapMode = TextureWrapMode.Clamp;
                return Sprite.Create(result.Texture,
                    new Rect(0f, 0f, result.Texture.width, result.Texture.height),
                    new Vector2(0.5f, 0.5f), 100f);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[NewsTicker] failed to load {relativePath}: {exception.Message}");
                return null;
            }
        }

        void OnDestroy()
        {
            DestroySprite(_toggleDown);
            if (_toggleUp != _toggleDown) DestroySprite(_toggleUp);
        }

        static void DestroySprite(Sprite? sprite)
        {
            if (sprite == null) return;
            var texture = sprite.texture;
            if (Application.isPlaying)
            {
                Destroy(sprite);
                if (texture != null) Destroy(texture);
            }
            else
            {
                DestroyImmediate(sprite);
                if (texture != null) DestroyImmediate(texture);
            }
        }

        sealed class Cell
        {
            public readonly RectTransform Rect;
            public readonly GameObject Root;
            public readonly Button Button;
            public readonly LayoutElement Layout;
            public readonly TMP_Text Text;

            public Cell(
                RectTransform rect,
                GameObject root,
                Button button,
                LayoutElement layout,
                TMP_Text text)
            {
                Rect = rect;
                Root = root;
                Button = button;
                Layout = layout;
                Text = text;
            }
        }
    }
}
