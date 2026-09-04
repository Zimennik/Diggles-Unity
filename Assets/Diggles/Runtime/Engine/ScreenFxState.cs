#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    // Headless-testable screen-FX model driven by screenvibe / start_fade / cancel_fade /
    // lightning / laserbeam / change_light. Holds the fullscreen fade alpha, the camera-shake
    // envelope, and short-lived FX records; Sim.Step advances them. A Play component reads this
    // to drive a fade overlay + camera shake + beam/flash visuals. See Docs/CUTSCENE_SYSTEM.md §3.
    public sealed class ScreenFxState
    {
        // ---- fade (start_fade <dur> <0=to black | 1=from black>) ----
        public float FadeAlpha;          // 0 = clear, 1 = black
        float _fadeTarget;
        float _fadeSpeed;                // alpha units / second
        public bool Fading;

        public void StartFade(float dur, bool fromBlack)
        {
            if (fromBlack) { FadeAlpha = 1f; _fadeTarget = 0f; }
            else _fadeTarget = 1f;        // to black, from the current alpha
            _fadeSpeed = 1f / Mathf.Max(0.01f, dur);
            Fading = !Mathf.Approximately(FadeAlpha, _fadeTarget);
        }

        public void CancelFade() { Fading = false; _fadeTarget = FadeAlpha; }
        // Snap the screen back to fully clear (used when a cutscene ends so a trailing fade-to-black
        // never persists into gameplay).
        public void ClearFade() { Fading = false; FadeAlpha = 0f; _fadeTarget = 0f; }

        // ---- camera shake (screenvibe att sus dec amx frx amy fry) ----
        public struct Shake { public float Att, Sus, Dec, AmpX, FreqX, AmpY, FreqY; }
        public Shake CurrentShake;
        public float ShakeRemaining;     // att + sus + dec, counted down
        public bool ShakeActive => ShakeRemaining > 0f;

        public void StartShake(float att, float sus, float dec, float amx, float frx, float amy, float fry)
        {
            CurrentShake = new Shake { Att = att, Sus = sus, Dec = dec, AmpX = amx, FreqX = frx, AmpY = amy, FreqY = fry };
            ShakeRemaining = Mathf.Max(0f, att) + Mathf.Max(0f, sus) + Mathf.Max(0f, dec);
        }

        // Current shake offset (for the Play camera) at sim time `t` within the envelope.
        public Vector2 ShakeOffset(double t)
        {
            if (!ShakeActive) return Vector2.zero;
            float total = CurrentShake.Att + CurrentShake.Sus + CurrentShake.Dec;
            float elapsed = total - ShakeRemaining;
            float env;
            if (elapsed < CurrentShake.Att && CurrentShake.Att > 0f) env = elapsed / CurrentShake.Att;
            else if (elapsed < CurrentShake.Att + CurrentShake.Sus) env = 1f;
            else env = CurrentShake.Dec > 0f ? Mathf.Max(0f, ShakeRemaining / CurrentShake.Dec) : 0f;
            float x = Mathf.Sin((float)t * CurrentShake.FreqX) * CurrentShake.AmpX * env;
            float y = Mathf.Sin((float)t * CurrentShake.FreqY) * CurrentShake.AmpY * env;
            return new Vector2(x, y);
        }

        // ---- transient FX records (lightning / laserbeam / change_light) ----
        public struct Beam { public Vector3 From, To; public Color32 Color; public float Remaining; }
        public readonly List<Beam> Beams = new();
        public Vector3 LastLightningPos;
        public int LightningCount;
        public readonly Dictionary<int, (float intensity, Color32 col)> Lights = new();

        public void Lightning(Vector3 pos) { LastLightningPos = pos; LightningCount++; }
        public void AddBeam(Vector3 from, Vector3 to, Color32 col, float life)
            => Beams.Add(new Beam { From = from, To = to, Color = col, Remaining = Mathf.Max(0.05f, life) });
        public void SetLight(int id, float intensity, Color32 col) => Lights[id] = (intensity, col);

        public void Tick(double dt)
        {
            if (Fading)
            {
                FadeAlpha = Mathf.MoveTowards(FadeAlpha, _fadeTarget, _fadeSpeed * (float)dt);
                if (Mathf.Approximately(FadeAlpha, _fadeTarget)) { FadeAlpha = _fadeTarget; Fading = false; }
            }
            if (ShakeRemaining > 0f) { ShakeRemaining -= (float)dt; if (ShakeRemaining < 0f) ShakeRemaining = 0f; }
            for (int i = Beams.Count - 1; i >= 0; i--)
            {
                var b = Beams[i]; b.Remaining -= (float)dt;
                if (b.Remaining <= 0f) Beams.RemoveAt(i); else Beams[i] = b;
            }
        }
    }
}
