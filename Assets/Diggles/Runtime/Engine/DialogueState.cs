#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    // Headless-testable dialogue/subtitle model driven by the sequencer's text leaves
    // (set_speechout / speechicon / textwin). Pure state + a countdown; a Play-mode component
    // renders the subtitle bar + per-actor speech bubbles (TMP/Canvas) from this. The deciding
    // LOGIC (which text, when, colour, timing) is the original Tcl; we hold + time the output.
    // See Docs/CUTSCENE_SYSTEM.md §3.
    public sealed class DialogueState
    {
        public struct SubLine { public string Text; public Color32 Color; }

        // Up to 4 subtitle slots (one per speaking actor), as set by set_speechout.
        public readonly SubLine[] Lines = new SubLine[4];
        public float SpeechRemaining;     // seconds the current speech-out stays up
        public bool SpeechForced;         // set_speechout's `force` flag
        public bool TextWindowVisible = true;
        private bool _speechClockExternallyDriven;

        // Per-actor speech bubbles (handle → bubble). locked bubbles don't auto-expire.
        public struct Bubble { public string Text; public float Remaining; public bool Locked; }
        public readonly Dictionary<int, Bubble> Bubbles = new();

        public void SetSpeechOut(float maxTime, List<(Color32 col, string text)> slots, bool force)
        {
            for (int i = 0; i < 4; i++)
                Lines[i] = i < slots.Count
                    ? new SubLine { Text = slots[i].text, Color = slots[i].col }
                    : new SubLine { Text = "", Color = new Color32(255, 255, 255, 255) };
            SpeechRemaining = maxTime;
            SpeechForced = force;
            _speechClockExternallyDriven = false;
        }

        public void SetVoiceDrivenSpeechRemaining(float remaining)
        {
            _speechClockExternallyDriven = true;
            SpeechRemaining = Mathf.Max(0f, remaining);
            if (SpeechRemaining <= 0f) CompleteSpeech();
        }

        public void CompleteSpeech()
        {
            SpeechRemaining = 0f;
            _speechClockExternallyDriven = false;
            for (int i = 0; i < 4; i++)
                Lines[i] = new SubLine { Text = "", Color = new Color32(255, 255, 255, 255) };
        }

        // Combined visible subtitle text (non-empty slots joined by newline) — convenience for
        // a single-label renderer + test assertions.
        public string CurrentSpeechText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var l in Lines)
                if (!string.IsNullOrEmpty(l.Text))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(l.Text);
                }
            return sb.ToString();
        }

        public void SpeechIconAdd(int handle, string text, float time)
            => Bubbles[handle] = new Bubble { Text = text, Remaining = time, Locked = false };

        public void SpeechIconLock(int handle, bool locked)
        {
            if (Bubbles.TryGetValue(handle, out var b)) { b.Locked = locked; Bubbles[handle] = b; }
        }

        public void SpeechIconClear(int handle) => Bubbles.Remove(handle);

        public void Tick(double dt)
        {
            if (SpeechRemaining > 0f && !_speechClockExternallyDriven)
            {
                SpeechRemaining -= (float)dt;
                if (SpeechRemaining <= 0f)
                    CompleteSpeech();
            }
            if (Bubbles.Count > 0)
            {
                // Snapshot keys first — we mutate Bubbles (update/remove) inside the loop.
                _keys.Clear();
                _keys.AddRange(Bubbles.Keys);
                foreach (var h in _keys)
                {
                    if (!Bubbles.TryGetValue(h, out var b) || b.Locked) continue;
                    b.Remaining -= (float)dt;
                    if (b.Remaining <= 0f) Bubbles.Remove(h);
                    else Bubbles[h] = b;
                }
            }
        }

        readonly List<int> _keys = new();
    }
}
