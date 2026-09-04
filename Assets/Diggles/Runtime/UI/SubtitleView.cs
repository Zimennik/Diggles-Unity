#nullable enable

namespace Diggles.Runtime
{
    using System.Text;
    using TMPro;
    using UnityEngine;

    // Dumb subtitle view. ScreenManager feeds it the cutscene DialogueState each frame via Render;
    // this component decides show/hide (speech present), formats the per-actor rich text, and fades
    // its CanvasGroup. It does not poll game state (no FindObjectOfType / no DI) — purely a renderer
    // of the DialogueState it is handed. Wire the CanvasGroup + TMP_Text in the Inspector.
    public sealed class SubtitleView : MonoBehaviour
    {
        [Tooltip("CanvasGroup faded in/out as speech shows/hides.")]
        [SerializeField] CanvasGroup? group;
        [Tooltip("TMP text that displays the subtitle lines (per-actor colour).")]
        [SerializeField] TMP_Text? label;

        bool _shown;

        // Change gate: Render runs every frame while speech is up; rebuilding the rich text
        // (2 StringBuilders + ToString("X2")×3 per line) and re-assigning label.text (a full
        // TMP re-layout) each frame was a steady per-frame allocator during dialogue. The lines
        // only change on set_speechout — compare and skip.
        readonly DialogueState.SubLine[] _lastLines = new DialogueState.SubLine[4];
        readonly StringBuilder _richText = new(512);
        bool _lastValid;
        bool _layoutPrewarmed;

        void Awake()
        {
            if (group == null) group = GetComponent<CanvasGroup>();   // fallback if on the same GameObject
            Hide();   // start hidden
        }

        // Render the current speech (or hide if none). Called every frame by ScreenManager.
        public void Render(DialogueState? d)
        {
            if (d == null) { if (_shown) Hide(); return; }

            bool hasText = false;
            foreach (var l in d.Lines) if (!string.IsNullOrEmpty(l.Text)) { hasText = true; break; }
            // Note: do not gate on TextWindowVisible — that's the gameplay message window
            // (`textwin hide` is called at cutscene start). Subtitles are driven by set_speechout.
            bool show = d.SpeechRemaining > 0f && hasText;

            if (show)
            {
                if (label != null && LinesChanged(d)) SetRichText(d);
                if (!_shown) Show();
            }
            else if (_shown) Hide();
        }

        public void PrewarmLayout()
        {
            if (_layoutPrewarmed || label == null) return;
            _layoutPrewarmed = true;
            long started = Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Start();
            _richText.Clear();
            _richText.Append('M', 512);
            label.SetText(_richText);
            label.ForceMeshUpdate(true, true);
            _richText.Clear();
            label.SetText(_richText);
            _lastValid = false;
            Diggles.Runtime.Diagnostics.HitchFamilyTimingDiag.Record("subtitle.prewarm", started);
        }

        bool LinesChanged(DialogueState d)
        {
            if (_lastValid)
            {
                bool same = true;
                for (int i = 0; i < 4; i++)
                {
                    // Text is assigned once per set_speechout — reference identity is exact.
                    // Colour compared field-wise (Color32.Equals(object) would box).
                    ref readonly var a = ref _lastLines[i];
                    var b = d.Lines[i];
                    if (!ReferenceEquals(a.Text, b.Text)
                        || a.Color.r != b.Color.r || a.Color.g != b.Color.g
                        || a.Color.b != b.Color.b || a.Color.a != b.Color.a) { same = false; break; }
                }
                if (same) return false;
            }
            for (int i = 0; i < 4; i++) _lastLines[i] = d.Lines[i];
            _lastValid = true;
            return true;
        }

        void Show()
        {
            _shown = true;
            if (group == null) return;
            group.alpha = 1f;
            group.blocksRaycasts = true;
        }

        void Hide()
        {
            _shown = false;
            if (group == null) return;
            group.alpha = 0f;
            group.blocksRaycasts = false;
        }

        void SetRichText(DialogueState d)
        {
            _richText.Clear();
            foreach (var l in d.Lines)
            {
                if (string.IsNullOrEmpty(l.Text)) continue;
                if (_richText.Length > 0) _richText.Append('\n');
                _richText.Append("<color=#");
                AppendHexByte(_richText, l.Color.r);
                AppendHexByte(_richText, l.Color.g);
                AppendHexByte(_richText, l.Color.b);
                _richText.Append('>');
                AppendNormalized(_richText, l.Text);
                _richText.Append("</color>");
            }
            label!.SetText(_richText);
        }

        static void AppendHexByte(StringBuilder builder, byte value)
        {
            const string Hex = "0123456789ABCDEF";
            builder.Append(Hex[value >> 4]);
            builder.Append(Hex[value & 0x0F]);
        }

        // The sequence text files are Windows-1252 but loaded as ISO-8859-1, so the "smart"
        // punctuation bytes (0x85, 0x91-0x97) land as C1 control chars (U+0085, U+0091..U+0097)
        // that the SDF font lacks (shown as a box). Map both those AND the proper Unicode
        // typographic forms to plain ASCII the font has. Uses integer code points so the source
        // stays pure ASCII.
        static void AppendNormalized(StringBuilder builder, string text)
        {
            foreach (char c in text)
            {
                int u = c;
                if (u == 0x91 || u == 0x2018 || u == 0x92 || u == 0x2019) builder.Append('\'');
                else if (u == 0x93 || u == 0x201C || u == 0x94 || u == 0x201D) builder.Append('"');
                else if (u == 0x96 || u == 0x2013 || u == 0x97 || u == 0x2014) builder.Append('-');
                else if (u == 0x85 || u == 0x2026) builder.Append("...");
                else if (u == 0x95 || u == 0x2022) builder.Append('*');
                else builder.Append(c);
            }
        }
    }
}
