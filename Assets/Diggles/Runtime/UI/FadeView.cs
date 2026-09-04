#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;
    using UnityEngine.UI;

    // Dumb fade-to-black overlay view. ScreenManager (the screen-layer coordinator) sets its alpha
    // from the cutscene fade state — this component knows nothing about the game, it just paints the
    // overlay. Wire `overlay` to a fullscreen black Image in the Inspector.
    public sealed class FadeView : MonoBehaviour
    {
        [Tooltip("Fullscreen black Image whose alpha is driven by ScreenManager.")]
        [SerializeField] Image? overlay;

        public void SetAlpha(float alpha)
        {
            if (overlay == null) return;
            var c = overlay.color;
            c.a = Mathf.Clamp01(alpha);
            overlay.color = c;
        }
    }
}
