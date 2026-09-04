#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    // Pure math/state for the WorkClock HUD — the C# mirror of ViWinClock (GameUI/ViWinClock.cpp:
    // Draw FUN_0050ab10, HandleMsg FUN_0050c2b0, Tick FUN_0050cae0). POCO (no scene state) so the
    // angle↔hour mapping, the native snapping and the selection-shared-window rule are
    // EditMode-testable (WorkClockTests).
    //
    // Conventions:
    // • day fraction ∈ [0,1) — fraction of the 12-hour clock day; 0 = 12-o'clock (top of dial),
    // increasing clockwise. Native angle = 2π·frac − π/2 in y-down screen coords
    // (FUN_0050ab10; π/2 = DAT_007371e0); here all directions are uGUI y-up, so
    // dir(frac) = (sin 2πf, cos 2πf) — same dial, flipped vertical axis.
    // • hour units = the entity fields (DigglesTclEntity.WorkStart/WorkLen, hour-of-day 0..12).
    public static class WorkClockModel
    {
        public const float DayHours = 12f;                           // DAT_00737540 = 12.0f
        public const float DialRadius = 37f;                         // ViWinClock ctor +0x60 = 37.0f
        public const float MarkerRadius = DialRadius + 10f;          // marker buttons ride the rim (Tick:336-358)
        public const float CenterClickRadius = 20f;                  // DAT_00737578 = 20.0f (HandleMsg:138-169)
        public const float SharedWindowTolHours = 0.01f * DayHours;  // DAT_00736fe0 = 0.01 day-frac (Tick:117-251)
        public const float MinSectorHours = DayHours / 48f;          // DAT_00737158 ≈ 1/48 day = 15 min (Draw:84)
        public const float HandRedrawHours = 0.05f;                  // DAT_00737190 = 0.05 h redraw threshold (Tick:110-116)
        public const byte SectorMaxAlpha = 0x60;                     // arc fade ceiling (Tick:220-223)
        public const byte SectorAlphaStep = 0x20;                    // fade step per UI tick (Tick:286-289)
        // The work-window arc colour, ctor +0x64 = 0x80dab674 (sandy amber).
        public static readonly Color32 SectorColor = new(0xDA, 0xB6, 0x74, 0xFF);
        public static readonly Color32 DateColor = new(0x96, 0x96, 0x96, 0xC8);   // Draw:759-791 ARGB 0xc8969696

        // ---- time → dial ----

        // Fraction of the 12-hour day. hours = gameTime / 150 (DAT_00737630, FUN_0050ab10:669);
        // day fraction = (hours % 12) / 12 (FUN_00565490 normalize).
        public static float DayFrac(double gameTime, double hourSec)
        {
            double h = (gameTime / hourSec) % DayHours;
            if (h < 0) h += DayHours;
            return (float)(h / DayHours);
        }

        public static float HoursToDayFrac(float hours)
        {
            float f = (hours % DayHours) / DayHours;
            return f < 0 ? f + 1f : f;
        }

        // uGUI z-rotation (degrees) that turns an up-pointing hand to the clock position —
        // negative = clockwise on screen.
        public static float HandZRotationDeg(double gameTime, double hourSec)
            => -360f * DayFrac(gameTime, hourSec);

        // Unit direction from the dial center to the dial position `frac`, y-up coords
        // (top of dial at frac 0, clockwise). Inverse of PointerDayFrac.
        public static Vector2 Dir(float dayFrac)
        {
            float a = 2f * Mathf.PI * dayFrac;
            return new Vector2(Mathf.Sin(a), Mathf.Cos(a));
        }

        // Pointer offset from the dial center (y-up) → day fraction (Tick:50-79 atan2(mouse−center),
        // converted out of the native y-down frame).
        public static float PointerDayFrac(Vector2 fromCenter)
        {
            float f = Mathf.Atan2(fromCenter.x, fromCenter.y) / (2f * Mathf.PI);
            if (f < 0f) f += 1f;
            return f;
        }

        // ---- the native snapping (drag sun = work start / drag moon = work end) ----

        // Sun drag: start snaps to whole hours via the native float→int truncation
        // `start = (int)(frac·12 − 0.49) / 12` (Tick:64-66; DAT_00737540=12, DAT_00737088=0.49).
        // Note: this is not round-to-nearest: the marker has to be dragged past x.49 to reach
        // hour x (e.g. 5.48 → 4 but 5.49 → 5) — kept literal.
        public static float SnapStartHours(float dayFrac)
        {
            int h = (int)(dayFrac * DayHours - 0.49f);   // truncation toward zero, like the x86 float→int
            if (h < 0) h = 0;                            // frac < 0.49 h lands at 0 anyway; guard the edge
            return h % 12;
        }

        // Moon drag: duration snaps to half hours, `dur = (int)(x·24 − 0.49) / 24` in day units
        // (Tick:96-100; DAT_00737588 = 24.0f) — i.e. half-hour count truncated with the same 0.49 lead.
        public static float SnapDurationHours(float dayFrac)
        {
            int k = (int)(dayFrac * 24f - 0.49f);
            if (k < 0) k = 0;
            return k * 0.5f;
        }

        // Drag the sun (work start): new start from the pointer angle snapped to whole hours;
        // duration recomputed so the end stays fixed (`dur = end − newStart`, wrap +1 day —
        // Tick:50-79).
        public static (float start, float len) DragStart(float pointerDayFrac, float startH, float lenH)
        {
            float end = (startH + lenH) % DayHours;
            float s = SnapStartHours(pointerDayFrac);
            float l = end - s;
            if (l < 0f) l += DayHours;
            return (s, l);
        }

        // Drag the moon (work end): duration = (pointer − start) wrapped, snapped to half hours;
        // start unchanged (Tick:81-109).
        public static (float start, float len) DragEnd(float pointerDayFrac, float startH, float lenH)
        {
            float x = pointerDayFrac - HoursToDayFrac(startH);
            if (x < 0f) x += 1f;
            return (startH, SnapDurationHours(x));
        }

        // Center click / wheel / PageUp/PageDown: shift the whole window ±1 h — both markers
        // += 1/12 day (DAT_00737028) or += 11/12 (≡ −1 h, DAT_0073710c); HandleMsg:138-169,
        //:228-253. Duration is untouched; only the start moves (mod 12).
        public static float ShiftHours(float startH, int deltaHours)
        {
            float s = (startH + deltaHours) % DayHours;
            if (s < 0f) s += DayHours;
            return s;
        }

        // Markers (and the arc, when a selection drives them) show only while every selected
        // gnome shares one window within 0.01 day-frac (DAT_00736fe0; Tick:117-251 — mixed
        // windows write the −1.0 sentinel and the markers hide). Compares each window to the
        // first. Empty input → no window.
        public static bool TrySharedWindow(
            IReadOnlyList<(float startH, float lenH)> windows, out float startH, out float lenH)
        {
            startH = 0f; lenH = 0f;
            if (windows.Count == 0) return false;
            (startH, lenH) = windows[0];
            for (int i = 1; i < windows.Count; i++)
            {
                if (Mathf.Abs(windows[i].startH - startH) > SharedWindowTolHours) return false;
                if (Mathf.Abs(windows[i].lenH - lenH) > SharedWindowTolHours) return false;
            }
            return true;
        }

        // The arc is drawn only when the window spans at least 1/48 day = 15 game-minutes
        // (Draw:84, DAT_00737158).
        public static bool SectorVisible(float lenH) => lenH / DayHours >= 1f / 48f;

        // The date line under the dial, `"%2d.%2d.%2d"` = day.month.year (Draw:759-791):
        // d = (h/12)%20+1, m = (h/240)%12+1, y = h/2880+1 — 20-day months, 12-month years.
        public static (int day, int month, int year) Date(double gameTime, double hourSec)
        {
            int h = (int)(gameTime / hourSec);
            if (h < 0) h = 0;
            return (h / 12 % 20 + 1, h / 240 % 12 + 1, h / 2880 + 1);
        }
    }
}
