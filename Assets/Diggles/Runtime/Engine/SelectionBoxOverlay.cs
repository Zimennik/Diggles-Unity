#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // The drag-box (band-select) rectangle visual — a minimal screen-space IMGUI overlay.
    // The static colour/width fields are the restyle hook (a skin authored in the Unity
    // editor can retint or replace it without touching the gesture code); this is a
    // world-gesture cue like DigMarqueePreviewLines, created on demand by ClickToMoveService.
    //
    // The native band visual is a world-space rectangle between the two drag anchors
    // unprojected on the z≈15 front plane (FUN_00530770 publishes them to DAT_0076a324+0x466c..
    // +0x4680 each frame; cleared with -1s via FUN_0046a1c0 on release). A screen-space rect is
    // the same shape under the game's fixed-tilt camera — a deliberate deviation, chosen so the
    // overlay stays legible at any zoom.
    public sealed class SelectionBoxOverlay : MonoBehaviour
    {
        // Restyle hooks.
        public static Color Fill = new(0.35f, 1f, 0.45f, 0.08f);
        public static Color Border = new(0.45f, 1f, 0.55f, 0.9f);
        public static float BorderPx = 1.5f;

        public bool Active;
        // Screen-space corners in Unity Input pixels (origin bottom-left; any corner order).
        public Vector2 A, B;

        void OnGUI()
        {
            if (!Active) return;
            // Input pixels are bottom-left origin; GUI pixels top-left — flip Y.
            float x0 = Mathf.Min(A.x, B.x), x1 = Mathf.Max(A.x, B.x);
            float yTop = Screen.height - Mathf.Max(A.y, B.y);
            float yBot = Screen.height - Mathf.Min(A.y, B.y);
            var r = new Rect(x0, yTop, x1 - x0, yBot - yTop);
            var tex = Texture2D.whiteTexture;
            var prev = GUI.color;
            GUI.color = Fill;
            GUI.DrawTexture(r, tex);
            GUI.color = Border;
            float b = Mathf.Max(1f, BorderPx);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, b), tex);                    // top
            GUI.DrawTexture(new Rect(r.x, r.yMax - b, r.width, b), tex);             // bottom
            GUI.DrawTexture(new Rect(r.x, r.y, b, r.height), tex);                   // left
            GUI.DrawTexture(new Rect(r.xMax - b, r.y, b, r.height), tex);            // right
            GUI.color = prev;
        }
    }
}
