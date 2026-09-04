#nullable enable

namespace Diggles.Runtime.UI
{
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.Serialization;
    using UnityEngine.UI;

    public sealed class DiggleItemView : MonoBehaviour, IPointerClickHandler
    {
        [FormerlySerializedAs("diggleName")]
        [SerializeField] TMP_Text? nameText;
        [FormerlySerializedAs("healthFill")]
        [SerializeField] Image? hpFill;
        [FormerlySerializedAs("worktimeStatus")]
        [SerializeField] WorkStatusView? workStatus;
        [FormerlySerializedAs("happiness")]
        [SerializeField] MoodIconView? moodIcon;
        [FormerlySerializedAs("selectedState")]
        [SerializeField] GameObject? selectedMarker;

        DiggleInfoPanelController? controller;
        int handle;
        bool warnedMissing;
        bool warnedUnboundClick;
        string? shownName;
        float shownHp = -1f;
        bool? shownSelected;

        public int Handle => handle;

        void Awake() => ResolveReferences();

        public void Bind(DiggleInfoPanelController owner, int entityHandle)
        {
            controller = owner;
            handle = entityHandle;
            ResolveReferences();
        }

        public void Render(
            string displayName,
            double hitpoints,
            DiggleWorkStatus status,
            DiggleMood mood,
            bool selected)
        {
            ResolveReferences();
            if (nameText != null && shownName != displayName)
            {
                shownName = displayName;
                nameText.text = displayName;
            }

            float hp = Mathf.Clamp01((float)hitpoints);
            if (hpFill != null && !Mathf.Approximately(shownHp, hp))
            {
                shownHp = hp;
                hpFill.fillAmount = hp;
            }

            workStatus?.SetStatus(status);
            moodIcon?.SetMood(mood);
            SetSelected(selected);
        }

        public void SetSelected(bool selected)
        {
            ResolveReferences();
            if (selectedMarker == null || shownSelected == selected) return;
            shownSelected = selected;
            selectedMarker.SetActive(selected);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || handle <= 0) return;
            if (controller != null)
            {
                controller.SelectFromUi(handle);
                return;
            }

            DiggleUiBindings.WarnMissingOnce(this, ref warnedUnboundClick,
                "DiggleItemView received a click before a DiggleInfoPanelController bound it.");
        }

        void ResolveReferences()
        {
            if (nameText == null)
                nameText = DiggleUiBindings.FindComponent<TMP_Text>(transform, "DiggleName");
            if (hpFill == null)
                hpFill = DiggleUiBindings.FindComponentBelow<Image>(transform, "HPBar", "Fill");
            if (workStatus == null) workStatus = GetComponentInChildren<WorkStatusView>(true);
            if (moodIcon == null) moodIcon = GetComponentInChildren<MoodIconView>(true);
            if (selectedMarker == null)
                selectedMarker = DiggleUiBindings.FindDeep(transform, "SelectedState")?.gameObject;

            if (nameText == null || hpFill == null || workStatus == null
                || moodIcon == null || selectedMarker == null)
                DiggleUiBindings.WarnMissingOnce(this, ref warnedMissing,
                    "DiggleItemView is missing one or more authored bindings; available row fields will still update.");
        }
    }
}
