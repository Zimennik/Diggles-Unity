#nullable enable

namespace Diggles.Runtime.UI
{
    using System;
    using System.Globalization;
    using TMPro;
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.UI;

    public sealed class DiggleExpView : MonoBehaviour
    {
        [FormerlySerializedAs("nameText")]
        [SerializeField] TMP_Text? label;
        [FormerlySerializedAs("barFill")]
        [SerializeField] Image? fill;
        [FormerlySerializedAs("amountText")]
        [SerializeField] TMP_Text? valueText;

        bool warnedMissing;
        string? lastLabel;
        int lastWholeValue = int.MinValue;
        float lastFill = -1f;

        void Awake() => ResolveReferences();

        public void SetValue(string labelText, double skillValue)
        {
            ResolveReferences();
            if (label != null && lastLabel != labelText)
            {
                lastLabel = labelText;
                label.text = labelText;
            }

            int wholeValue = (int)Math.Floor(skillValue);
            float normalized = Mathf.Clamp01((float)(skillValue / 100d));
            if (fill != null && !Mathf.Approximately(lastFill, normalized))
            {
                lastFill = normalized;
                fill.fillAmount = normalized;
            }
            if (valueText != null && lastWholeValue != wholeValue)
            {
                lastWholeValue = wholeValue;
                valueText.text = wholeValue.ToString(CultureInfo.InvariantCulture) + "/100";
            }
        }

        void ResolveReferences()
        {
            if (label == null) label = DiggleUiBindings.FindComponent<TMP_Text>(transform, "DiggleWork");
            if (fill == null) fill = DiggleUiBindings.FindComponentBelow<Image>(transform, "FillMask", "Fill");
            if (valueText == null) valueText = DiggleUiBindings.FindComponent<TMP_Text>(transform, "Text");
            if (label == null || fill == null || valueText == null)
                DiggleUiBindings.WarnMissingOnce(this, ref warnedMissing,
                    "DiggleExpView is missing an authored label, Fill image, or value text; available fields will still update.");
        }
    }
}
