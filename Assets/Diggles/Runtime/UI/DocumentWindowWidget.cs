#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    // Code-built presentation for ViWinLayout. Original Tcl owns the page contents; this view
    // supplies the dark overlay, scroll surface, TGA blocks, and clickable suN spans. The native
    // close control is xoverlay_Crossout.tga (FUN_00507560, 50x50 at the upper-right edge).
    public sealed class DocumentWindowWidget : MonoBehaviour
    {
        private const float UiScale = 1080f / 600f;
        private const float CloseSize = 50f;
        private const float PanelInsetX = 42f;
        private const float PanelInsetY = 28f;

        private readonly List<DocumentRenderBlock> _blocks = new();
        private readonly List<Sprite> _pageSprites = new();
        private DigglesTclContext? _context;
        private CanvasGroup? _group;
        private RectTransform? _content;
        private ScrollRect? _scroll;
        private Sprite? _closeSprite;
        private int _shownVersion = -1;
        private bool _shownVisible;

        public static DocumentWindowWidget Create(RectTransform parent, DigglesTclContext context)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (context == null) throw new ArgumentNullException(nameof(context));
            var rootObject = new GameObject("DocumentWindow", typeof(RectTransform));
            var root = (RectTransform)rootObject.transform;
            root.SetParent(parent, false);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;
            root.SetAsLastSibling();

            var widget = rootObject.AddComponent<DocumentWindowWidget>();
            widget._context = context;
            widget.BuildUi();
            return widget;
        }

        private void BuildUi()
        {
            var blocker = gameObject.AddComponent<Image>();
            blocker.color = new Color32(0, 0, 0, 0xb8);
            blocker.raycastTarget = true;
            _group = gameObject.AddComponent<CanvasGroup>();
            SetVisible(false);

            var panelObject = new GameObject("Panel", typeof(RectTransform));
            var panel = (RectTransform)panelObject.transform;
            panel.SetParent(transform, false);
            panel.anchorMin = Vector2.zero;
            panel.anchorMax = Vector2.one;
            panel.offsetMin = new Vector2(PanelInsetX * UiScale, PanelInsetY * UiScale);
            panel.offsetMax = new Vector2(-PanelInsetX * UiScale, -PanelInsetY * UiScale);
            var panelImage = panelObject.AddComponent<Image>();
            panelImage.color = new Color32(0x18, 0x16, 0x12, 0xf5);
            panelImage.raycastTarget = true;
            var outline = panelObject.AddComponent<Outline>();
            outline.effectColor = new Color32(0xc8, 0xb8, 0x8c, 0xd0);
            outline.effectDistance = new Vector2(2f, -2f);

            BuildScrollSurface(panel);
            BuildCloseButton(panel);
        }

        private void BuildScrollSurface(RectTransform panel)
        {
            var scrollObject = new GameObject("Scroll", typeof(RectTransform));
            var scrollRect = (RectTransform)scrollObject.transform;
            scrollRect.SetParent(panel, false);
            scrollRect.anchorMin = Vector2.zero;
            scrollRect.anchorMax = Vector2.one;
            scrollRect.offsetMin = new Vector2(18f * UiScale, 16f * UiScale);
            scrollRect.offsetMax = new Vector2(-18f * UiScale, -(CloseSize + 8f) * UiScale);

            _scroll = scrollObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 32f;

            var viewportObject = new GameObject("Viewport", typeof(RectTransform));
            var viewport = (RectTransform)viewportObject.transform;
            viewport.SetParent(scrollRect, false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
            var viewportImage = viewportObject.AddComponent<Image>();
            viewportImage.color = new Color32(0, 0, 0, 1);
            viewportImage.raycastTarget = true;
            viewportObject.AddComponent<RectMask2D>();

            var contentObject = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)contentObject.transform;
            _content.SetParent(viewport, false);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;
            var layout = contentObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 18);
            layout.spacing = 4f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = contentObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport = viewport;
            _scroll.content = _content;
        }

        private void BuildCloseButton(RectTransform panel)
        {
            _closeSprite = LoadSprite("GUI/icons/xoverlay_Crossout.tga", pageOwned: false);
            var buttonObject = new GameObject("Close", typeof(RectTransform));
            var rect = (RectTransform)buttonObject.transform;
            rect.SetParent(panel, false);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-5f * UiScale, -5f * UiScale);
            rect.sizeDelta = Vector2.one * (CloseSize * UiScale);
            var image = buttonObject.AddComponent<Image>();
            image.sprite = _closeSprite;
            image.preserveAspect = true;
            image.color = _closeSprite == null ? new Color32(0x70, 0x24, 0x20, 0xff) : Color.white;
            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => _context?.Documents.Close());

            if (_closeSprite == null)
            {
                var labelObject = new GameObject("Label", typeof(RectTransform));
                var labelRect = (RectTransform)labelObject.transform;
                labelRect.SetParent(rect, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
                var label = labelObject.AddComponent<TextMeshProUGUI>();
                label.text = "X";
                label.fontSize = 28f;
                label.alignment = TextAlignmentOptions.Center;
                label.raycastTarget = false;
            }
        }

        private void Update()
        {
            if (_context == null) return;
            bool visible = _context.Documents.Visible;
            if (_shownVersion != _context.Documents.Version || _shownVisible != visible)
            {
                _shownVersion = _context.Documents.Version;
                _shownVisible = visible;
                SetVisible(visible);
                if (visible) RenderPage();
            }
            if (visible && Input.GetKeyDown(KeyCode.Escape)) _context.Documents.Close();
        }

        private void SetVisible(bool visible)
        {
            if (_group == null) return;
            _group.alpha = visible ? 1f : 0f;
            _group.interactable = visible;
            _group.blocksRaycasts = visible;
        }

        private void RenderPage()
        {
            if (_context == null || _content == null) return;
            ClearPage();
            DocumentMarkupParser.Parse(_context.Documents.Markup, _context, _blocks);
            foreach (var block in _blocks)
            {
                if (block.Kind == DocumentRenderBlockKind.Text) CreateTextBlock(block);
                else CreateImageBlock(block);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
        }

        private void CreateTextBlock(DocumentRenderBlock block)
        {
            if (_content == null || _context == null) return;
            var textObject = new GameObject("Text", typeof(RectTransform));
            var rect = (RectTransform)textObject.transform;
            rect.SetParent(_content, false);
            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = block.RichText;
            text.fontSize = 20f;
            text.color = new Color32(0xf1, 0xec, 0xd9, 0xff);
            text.alignment = block.Alignment switch
            {
                DocumentBlockAlignment.Center => TextAlignmentOptions.Top,
                DocumentBlockAlignment.Right => TextAlignmentOptions.TopRight,
                _ => TextAlignmentOptions.TopLeft,
            };
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = true;
            var fitter = textObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var links = textObject.AddComponent<DocumentLinkText>();
            links.Initialize(text, _context.Documents);
        }

        private void CreateImageBlock(DocumentRenderBlock block)
        {
            if (_content == null) return;
            var sprite = LoadSprite(block.ImagePath, pageOwned: true);
            if (sprite == null) return;

            float availableWidth = Mathf.Max(64f, _content.rect.width - 24f * UiScale);
            float scale = Mathf.Min(1f, availableWidth / Mathf.Max(1f, sprite.rect.width));
            float width = sprite.rect.width * scale;
            float height = sprite.rect.height * scale;

            var rowObject = new GameObject("ImageRow", typeof(RectTransform));
            var row = (RectTransform)rowObject.transform;
            row.SetParent(_content, false);
            var rowLayout = rowObject.AddComponent<LayoutElement>();
            rowLayout.minHeight = height + block.Inset * UiScale;
            rowLayout.preferredHeight = rowLayout.minHeight;

            var imageObject = new GameObject("Image", typeof(RectTransform));
            var rect = (RectTransform)imageObject.transform;
            rect.SetParent(row, false);
            float anchor = block.Alignment switch
            {
                DocumentBlockAlignment.Center => 0.5f,
                DocumentBlockAlignment.Right => 1f,
                _ => 0f,
            };
            rect.anchorMin = rect.anchorMax = new Vector2(anchor, 1f);
            rect.pivot = new Vector2(anchor, 1f);
            rect.anchoredPosition = new Vector2(0f, -block.Inset * UiScale);
            rect.sizeDelta = new Vector2(width, height);
            var image = imageObject.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        private Sprite? LoadSprite(string relativePath, bool pageOwned)
        {
            if (_context == null || string.IsNullOrEmpty(_context.DataRoot) || string.IsNullOrWhiteSpace(relativePath))
                return null;
            string normalized = relativePath.Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(5);
            string path = Path.Combine(_context.DataRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!Path.HasExtension(path)) path += ".tga";
            var result = TgaLoader.LoadFromFile(path);
            if (result == null) return null;
            result.Texture.wrapMode = TextureWrapMode.Clamp;
            var sprite = Sprite.Create(result.Texture,
                new Rect(0f, 0f, result.Texture.width, result.Texture.height),
                new Vector2(0.5f, 0.5f), 100f);
            if (pageOwned) _pageSprites.Add(sprite);
            return sprite;
        }

        private void ClearPage()
        {
            if (_content != null)
                for (int i = _content.childCount - 1; i >= 0; i--)
                {
                    var child = _content.GetChild(i).gameObject;
                    child.SetActive(false);
                    DestroyUnityObject(child);
                }
            foreach (var sprite in _pageSprites) DestroySprite(sprite);
            _pageSprites.Clear();
        }

        private void OnDestroy()
        {
            ClearPage();
            DestroySprite(_closeSprite);
        }

        private static void DestroySprite(Sprite? sprite)
        {
            if (sprite == null) return;
            var texture = sprite.texture;
            DestroyUnityObject(sprite);
            if (texture != null) DestroyUnityObject(texture);
        }

        private static void DestroyUnityObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }

    public sealed class DocumentLinkText : MonoBehaviour, IPointerClickHandler
    {
        private TMP_Text? _text;
        private DocumentWindowSystem? _documents;

        public void Initialize(TMP_Text text, DocumentWindowSystem documents)
        {
            _text = text;
            _documents = documents;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_text == null || _documents == null) return;
            int index = TMP_TextUtilities.FindIntersectingLink(
                _text,
                eventData.position,
                eventData.pressEventCamera);
            if (index < 0 || index >= _text.textInfo.linkCount) return;
            string value = _text.textInfo.linkInfo[index].GetLinkID();
            if (!int.TryParse(value, out int id)) return;
            _documents.Click(id, eventData.button == PointerEventData.InputButton.Right);
        }
    }
}
