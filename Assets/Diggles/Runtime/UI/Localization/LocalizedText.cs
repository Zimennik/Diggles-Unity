#nullable enable

namespace Diggles.Runtime.UI
{
    using Diggles.Runtime.Localization;
    using TMPro;
    using UnityEngine;

    /// <summary>
    /// Drop-on localization for a TMP label: add next to any <see cref="TMP_Text"/>
    /// (TextMeshProUGUI or world-space), set <see cref="MessageKey"/> to a
    /// <c>data/messages.txt</c> key (e.g. <c>New Game</c> — messages.txt:2529), and the label
    /// tracks the active locale live. Until the game table binds, the scene-authored text is
    /// left untouched (design-time preview). Code-driven labels either set
    /// <see cref="MessageKey"/> here or inject <see cref="GameLocalization"/> directly.
    /// </summary>
    [ExecuteAlways]   // OnEnable must run in EditMode too: headless tests and the editor preview
                      // both rely on the Changed subscription (Play-only OnEnable left the label
                      // stale after a locale switch in EditMode suites).
    [DisallowMultipleComponent]
    [AddComponentMenu("Diggles/Localized Text (TMP)")]
    public sealed class LocalizedText : MonoBehaviour
    {
        [Tooltip("messages.txt key, e.g. 'New Game' (## \"New Game\" in data/messages.txt).")]
        [SerializeField] string messageKey = "";

        TMP_Text? _target;
        bool _missingTargetWarned;

        public string MessageKey
        {
            get => messageKey;
            set { messageKey = value; Apply(); }
        }

        void OnEnable()
        {
            GameLocalization.Changed += Apply;
            Apply();
        }

        void OnDisable() => GameLocalization.Changed -= Apply;

        void Apply()
        {
            if (GameLocalization.Current is not { } localization || string.IsNullOrEmpty(messageKey)) return;
            if (_target == null && !TryGetComponent(out _target))
            {
                if (!_missingTargetWarned)
                    Debug.LogWarning($"[LocalizedText] no TMP_Text on '{name}'", this);
                _missingTargetWarned = true;
                return;
            }
            _target!.text = localization.Get(messageKey);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (Application.isPlaying && isActiveAndEnabled) Apply();
        }
#endif
    }
}
