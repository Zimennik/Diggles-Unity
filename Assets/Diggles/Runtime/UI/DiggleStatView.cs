#nullable enable

namespace Diggles.Runtime.UI
{
    using System.Globalization;
    using TMPro;
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.UI;

    public sealed class DiggleStatView : MonoBehaviour
    {
        [FormerlySerializedAs("nameText")]
        [SerializeField] TMP_Text? label;
        [FormerlySerializedAs("barFill")]
        [SerializeField] Image? fill;
        [FormerlySerializedAs("amountText")]
        [SerializeField] TMP_Text? valueText;

        bool warnedMissing;
        string? lastLabel;
        int lastPercent = int.MinValue;
        float lastFill = -1f;

        void Awake() => ResolveReferences();

        public void SetValue(string labelText, double normalizedValue)
        {
            ResolveReferences();
            float normalized = Mathf.Clamp01((float)normalizedValue);
            if (label != null && lastLabel != labelText)
            {
                lastLabel = labelText;
                label.text = labelText;
            }
            if (fill != null && !Mathf.Approximately(lastFill, normalized))
            {
                lastFill = normalized;
                fill.fillAmount = normalized;
            }

            int percent = Mathf.FloorToInt(normalized * 100f);
            if (valueText != null && lastPercent != percent)
            {
                lastPercent = percent;
                valueText.text = percent.ToString(CultureInfo.InvariantCulture) + "%";
            }
        }

        void ResolveReferences()
        {
            if (label == null) label = DiggleUiBindings.FindComponent<TMP_Text>(transform, "DiggleWork");
            if (fill == null) fill = DiggleUiBindings.FindComponentBelow<Image>(transform, "FillMask", "Fill");
            if (valueText == null) valueText = DiggleUiBindings.FindComponent<TMP_Text>(transform, "Text");
            if (label == null || fill == null || valueText == null)
                DiggleUiBindings.WarnMissingOnce(this, ref warnedMissing,
                    "DiggleStatView is missing an authored label, Fill image, or value text; available fields will still update.");
        }
    }
}
