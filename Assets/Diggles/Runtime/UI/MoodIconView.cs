#nullable enable

namespace Diggles.Runtime.UI
{
    using UnityEngine;
    using UnityEngine.Serialization;

    public sealed class MoodIconView : MonoBehaviour
    {
        [FormerlySerializedAs("happyState")]
        [SerializeField] GameObject? happy;
        [FormerlySerializedAs("neutralState")]
        [SerializeField] GameObject? neutral;
        [FormerlySerializedAs("sadState")]
        [SerializeField] GameObject? sad;

        bool warnedMissing;
        DiggleMood shownMood = (DiggleMood)(-1);

        void Awake() => ResolveReferences();

        public void SetMood(DiggleMood mood)
        {
            ResolveReferences();
            if (shownMood == mood) return;
            shownMood = mood;
            SetActive(happy, mood == DiggleMood.Happy);
            SetActive(neutral, mood == DiggleMood.Neutral);
            SetActive(sad, mood == DiggleMood.Sad);
        }

        void ResolveReferences()
        {
            if (happy == null) happy = DiggleUiBindings.FindDeep(transform, "Happy")?.gameObject;
            if (neutral == null) neutral = DiggleUiBindings.FindDeep(transform, "Neutral")?.gameObject;
            if (sad == null) sad = DiggleUiBindings.FindDeep(transform, "Sad")?.gameObject;
            if (happy == null || neutral == null || sad == null)
                DiggleUiBindings.WarnMissingOnce(this, ref warnedMissing,
                    "MoodIconView is missing one or more authored mood objects; available states will still update.");
        }

        static void SetActive(GameObject? target, bool active)
        {
            if (target != null && target.activeSelf != active) target.SetActive(active);
        }
    }
}
