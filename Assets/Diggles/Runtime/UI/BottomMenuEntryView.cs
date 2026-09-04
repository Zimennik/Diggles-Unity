#nullable enable

namespace Diggles.Runtime.UI
{
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    // One cell of the bottom context menu — lives on the entry prefab authored in the Unity
    // editor. Bind fills the visuals from a BottomMenuEntry; clicks route back to the
    // controller (left/right distinguished — right = proddecrement on product cells, native law).
    // All references are optional: assign the ones your layout uses.
    public sealed class BottomMenuEntryView : MonoBehaviour, IPointerClickHandler
    {
        [Header("Wire what your prefab has (all optional)")]
        [SerializeField] Image? icon;
        [SerializeField] TMP_Text? countBadge;    // order count; 10 renders as "oo"
        [Tooltip("Progress SLIDER (value is driven normalized 0..1; the whole slider hides when idle).")]
        [SerializeField] Slider? progressSlider;
        [Tooltip("The slider's fill Image — tinted by what the progress means.")]
        [SerializeField] Image? progressFillImage;
        [SerializeField] Color producingColor = new(0.25f, 0.9f, 0.25f, 1f);   // producing = green
        [SerializeField] Color deliveringColor = new(0.25f, 0.45f, 1f, 1f);    // delivering = blue
        [Tooltip("On/off indicator: separate objects for the ON and the OFF state.")]
        [SerializeField] GameObject? toggleOnMark;
        [SerializeField] GameObject? toggleOffMark;
        [Tooltip("Icon tints for SELECTABLE cells (the dig tools): the native brightens the " +
                 "selected tool — no toggle marks.")]
        [SerializeField] Color selectedIconColor = Color.white;
        [SerializeField] Color unselectedIconColor = new(0.62f, 0.62f, 0.62f, 1f);
        [Tooltip("Question-mark overlay for UNINVENTED products (icon + mark tinted by state: " +
                 "red = no skilled gnome, yellow = skilled gnome off-shift, white = can invent).")]
        [SerializeField] Image? inventionMark;
        [SerializeField] Color inventionRed = new(1f, 0.3f, 0.3f, 1f);
        [SerializeField] Color inventionYellow = new(1f, 0.85f, 0.35f, 1f);
        [SerializeField] Color inventionWhite = Color.white;
        [SerializeField] CanvasGroup? group;      // dimming for locked/unbuildable cells
        [Tooltip("The cell's Button (auto-found on this GO if unassigned). Used for its visual " +
                 "transitions and its interactable gate; clicks still route through this view's " +
                 "IPointerClickHandler (a Button can't see right-clicks — prod decrement needs them).")]
        [SerializeField] Button? button;

        BottomMenuEntry? _entry;
        System.Action<string, bool>? _onClick;
        bool _buttonBaseInteractable = true;
        bool _buttonInitialized;

        void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            _buttonBaseInteractable = button == null || button.interactable;
            _buttonInitialized = true;
        }

        public void Bind(BottomMenuEntry entry, Sprite? iconSprite, System.Action<string, bool> onClick)
        {
            _entry = entry;
            _onClick = onClick;
            Color? invTint = entry.Invention switch
            {
                BottomMenuInventionState.Red => inventionRed,
                BottomMenuInventionState.Yellow => inventionYellow,
                BottomMenuInventionState.White => inventionWhite,
                _ => (Color?)null,
            };
            if (icon != null)
            {
                icon.sprite = iconSprite;
                icon.enabled = iconSprite != null;
                icon.preserveAspect = true;
                icon.color = invTint ?? (entry.Selectable
                    ? (entry.Highlighted ? selectedIconColor : unselectedIconColor)
                    : Color.white);
            }
            if (inventionMark != null)
            {
                inventionMark.gameObject.SetActive(invTint.HasValue);
                if (invTint.HasValue) inventionMark.color = invTint.Value;
            }
            if (countBadge != null)
            {
                countBadge.gameObject.SetActive(entry.Count >= 0);
                countBadge.text = entry.Count >= 10 ? "oo"
                    : entry.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (toggleOnMark != null) toggleOnMark.SetActive(entry.Toggle && entry.ToggledOn);
            if (toggleOffMark != null) toggleOffMark.SetActive(entry.Toggle && !entry.ToggledOn);
            if (progressSlider != null)
            {
                bool hasProgress = entry.Progress >= 0f
                                   && entry.ProgressKind != BottomMenuProgressKind.None;
                progressSlider.gameObject.SetActive(hasProgress);
                if (hasProgress)
                {
                    progressSlider.normalizedValue = Mathf.Clamp01(entry.Progress / 100f);
                    if (progressFillImage != null)
                        progressFillImage.color = entry.ProgressKind == BottomMenuProgressKind.Delivering
                            ? deliveringColor : producingColor;
                }
            }
            if (group != null) group.alpha = entry.Dimmed && !entry.Disabled ? 0.45f : 1f;
            if (button != null)
            {
                if (!_buttonInitialized)
                {
                    _buttonBaseInteractable = button.interactable;
                    _buttonInitialized = true;
                }
                button.interactable = _buttonBaseInteractable && !entry.Disabled;
            }
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (_entry == null || _onClick == null) return;
            if (button != null && !button.interactable) return;   // the author disabled the cell
            if (e.button == PointerEventData.InputButton.Middle) return;
            _onClick(_entry.Id, e.button == PointerEventData.InputButton.Right);
        }
    }
}
