#nullable enable

namespace Diggles.Runtime
{
    using DG.Tweening;
    using UnityEngine;

    // Dumb letterbox view for the SoftMask cutout. ScreenManager calls SetShown(active); this scales the
    // mask holder (hiddenScale <-> shownScale) via DOTween and toggles the parent CanvasGroup:
    // enabled before the show animation, disabled after the hide animation. It does not poll game state.
    public sealed class LetterboxView : MonoBehaviour
    {
        [Tooltip("Parent CanvasGroup: raycasts/interactable/alpha turned ON before Show, OFF after Hide.")]
        [SerializeField] CanvasGroup? group;
        [Tooltip("Mask holder scaled on show/hide.")]
        [SerializeField] RectTransform? maskHolder;

        [Header("Scale")]
        [Tooltip("Uniform scale while hidden.")]
        [SerializeField] float hiddenScale = 1.5f;
        [Tooltip("Uniform scale while shown.")]
        [SerializeField] float shownScale = 1f;

        [Header("Animation")]
        [SerializeField] float showDuration = 0.5f;
        [SerializeField] float hideDuration = 0.4f;
        [SerializeField] Ease showEase = Ease.OutCubic;
        [SerializeField] Ease hideEase = Ease.InCubic;
        [Tooltip("Start hidden at launch (recommended).")]
        [SerializeField] bool hiddenAtStart = true;

        bool _shown;
        Tween? _scaleTween;

        void Awake()
        {
            if (group == null) group = GetComponentInParent<CanvasGroup>();
            if (hiddenAtStart) ApplyHiddenImmediate();
        }

        // Driven by ScreenManager; only animates on the actual show/hide edge.
        public void SetShown(bool shown)
        {
            if (shown == _shown) return;
            if (shown) Show(); else Hide();
        }

        // DOTween core (DOTween.To) — not the UI shortcut extensions (DOScale/DOFade), which live in the
        // DOTween modules (compiled into Assembly-CSharp), not the core DLL this asmdef references.
        Tween ScaleTo(RectTransform rt, float target, float dur, Ease e) =>
            DOTween.To(() => rt.localScale.x, v => rt.localScale = new Vector3(v, v, v), target, dur)
                .SetEase(e).SetUpdate(true);

        void Show()
        {
            _shown = true;
            KillTweens();
            SetGroup(true);   // enable before the show animation
            if (maskHolder != null) _scaleTween = ScaleTo(maskHolder, shownScale, showDuration, showEase);
        }

        void Hide()
        {
            _shown = false;
            KillTweens();
            if (maskHolder != null)
                _scaleTween = ScaleTo(maskHolder, hiddenScale, hideDuration, hideEase)
                    .OnComplete(() => SetGroup(false));   // disable after the hide animation
            else
                SetGroup(false);
        }

        void ApplyHiddenImmediate()
        {
            _shown = false;
            if (maskHolder != null) maskHolder.localScale = Vector3.one * hiddenScale;
            SetGroup(false);
        }

        void SetGroup(bool on)
        {
            if (group == null) return;
            group.alpha = on ? 1f : 0f;
            group.blocksRaycasts = on;
            group.interactable = on;
        }

        void KillTweens()
        {
            _scaleTween?.Kill();
            _scaleTween = null;
        }

        void OnDisable() => KillTweens();
    }
}
