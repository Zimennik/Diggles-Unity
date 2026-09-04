#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using NUnit.Framework;
    using UnityEngine;

    // WorkClock UI — the pure ViWinClock math extracted into WorkClockModel
    // (GameUI/ViWinClock.cpp):
    // • angle ↔ hour mapping: rad = 2π·dayFrac − π/2, one hand revolution per 12-h day
    // (hours = gettime/150, FUN_0050ab10:668-758);
    // • sun drag snaps the start to whole hours via the native truncation
    // `(int)(frac·12 − 0.49)` and keeps the end fixed (Tick FUN_0050cae0:50-79);
    // • moon drag snaps the duration to half hours `(int)(x·24 − 0.49)/24` (Tick:81-109);
    // • center click/wheel shifts the whole window ±1 h (HandleMsg FUN_0050c2b0:138-169);
    // • markers only when all selected windows agree within 0.01 day-frac (DAT_00736fe0);
    // • the d.m.y calendar: 12-h days, 20-day months, 12-month years (Draw:759-791);
    // • the arc draws only for windows ≥ 1/48 day = 15 min (DAT_00737158).
    public sealed class WorkClockTests
    {
        const double HourSec = ProdDispatcher.WorktimeHourSeconds;   // 150.0, DAT_00737630

        // ---------------- angle ↔ hour ----------------

        [Test]
        public void Hand_TopAtMidnight_QuarterTurnAt3h_WrapsPast12()
        {
            // frac 0 = top of dial; the hand turns clockwise one revolution per 12-h day.
            Assert.AreEqual(0f, WorkClockModel.DayFrac(0.0, HourSec), 1e-6f);
            Assert.AreEqual(0f, WorkClockModel.HandZRotationDeg(0.0, HourSec), 1e-4f);

            // 3 h = 450 s → frac 0.25 → −90° (clockwise quarter turn).
            Assert.AreEqual(0.25f, WorkClockModel.DayFrac(3 * HourSec, HourSec), 1e-6f);
            Assert.AreEqual(-90f, WorkClockModel.HandZRotationDeg(3 * HourSec, HourSec), 1e-4f);

            // 13 h wraps to 1 h of the next day (frac 1/12).
            Assert.AreEqual(1f / 12f, WorkClockModel.DayFrac(13 * HourSec, HourSec), 1e-6f);
        }

        [Test]
        public void PointerAngle_RoundTripsMarkerDirection()
        {
            // Dir(frac) (y-up uGUI coords) and PointerDayFrac are inverses on the rim.
            foreach (var frac in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f / 12f, 11f / 12f, 0.123f })
            {
                var p = WorkClockModel.Dir(frac) * WorkClockModel.DialRadius;
                Assert.AreEqual(frac, WorkClockModel.PointerDayFrac(p), 1e-5f, $"frac {frac}");
            }
            // Cardinal sanity: top = 12 o'clock, right = 3 o'clock (clockwise dial).
            Assert.AreEqual(0f, WorkClockModel.PointerDayFrac(new Vector2(0f, 10f)), 1e-6f);
            Assert.AreEqual(0.25f, WorkClockModel.PointerDayFrac(new Vector2(10f, 0f)), 1e-6f);
            Assert.AreEqual(0.5f, WorkClockModel.PointerDayFrac(new Vector2(0f, -10f)), 1e-6f);
            Assert.AreEqual(0.75f, WorkClockModel.PointerDayFrac(new Vector2(-10f, 0f)), 1e-6f);
        }

        // ---------------- the native snapping ----------------

        [Test]
        public void SunDrag_SnapsStartToWholeHours_NativeTruncationBand()
        {
            // `start = (int)(frac·12 − 0.49)` (Tick:64-66, DAT_00737088 = 0.49) — not
            // round-to-nearest: the pointer must pass x.49 to reach hour x. (Margins kept off
            // the exact 0.49 boundary — float div/mul round-trip noise.)
            Assert.AreEqual(5f, WorkClockModel.SnapStartHours(5.51f / 12f), 1e-6f);
            Assert.AreEqual(4f, WorkClockModel.SnapStartHours(5.47f / 12f), 1e-6f);
            Assert.AreEqual(4f, WorkClockModel.SnapStartHours(5.00f / 12f), 1e-6f);   // the literal band
            Assert.AreEqual(0f, WorkClockModel.SnapStartHours(0.20f / 12f), 1e-6f);   // negative trunc → 0
            Assert.AreEqual(11f, WorkClockModel.SnapStartHours(11.6f / 12f), 1e-6f);
        }

        [Test]
        public void SunDrag_KeepsWorkEndFixed_WrapsAcrossMidnight()
        {
            // window {2,6} (end 8): drag the sun to ~4.6 h → start 4, len 4 (end still 8).
            var (s, l) = WorkClockModel.DragStart(4.6f / 12f, 2f, 6f);
            Assert.AreEqual(4f, s, 1e-6f);
            Assert.AreEqual(4f, l, 1e-6f);

            // window {9,6} (end 3, wrapped): drag the sun to ~10.6 h → start 10, len 5.
            (s, l) = WorkClockModel.DragStart(10.6f / 12f, 9f, 6f);
            Assert.AreEqual(10f, s, 1e-6f);
            Assert.AreEqual(5f, l, 1e-6f);
        }

        [Test]
        public void MoonDrag_SnapsDurationToHalfHours_StartUnchanged()
        {
            // `dur = (int)(x·24 − 0.49)/24` (Tick:96-100, DAT_00737588 = 24).
            // start 2, pointer 5.3 h → span 3.3 h → 3.0 h.
            var (s, l) = WorkClockModel.DragEnd(5.3f / 12f, 2f, 6f);
            Assert.AreEqual(2f, s, 1e-6f);
            Assert.AreEqual(3.0f, l, 1e-6f);

            // start 2, pointer 5.8 h → span 3.8 h → 3.5 h.
            (s, l) = WorkClockModel.DragEnd(5.8f / 12f, 2f, 6f);
            Assert.AreEqual(3.5f, l, 1e-6f);

            // wrap: start 9, pointer 1 h → span 4 h → snapped 3.5 (the 0.49 lead).
            (s, l) = WorkClockModel.DragEnd(1f / 12f, 9f, 6f);
            Assert.AreEqual(9f, s, 1e-6f);
            Assert.AreEqual(3.5f, l, 1e-6f);
        }

        [Test]
        public void CenterClick_ShiftsWindowOneHour_WrapsAt12()
        {
            // markers += 1/12 day (+1 h) or += 11/12 (−1 h) — HandleMsg:138-169.
            Assert.AreEqual(4f, WorkClockModel.ShiftHours(3f, +1), 1e-6f);
            Assert.AreEqual(0f, WorkClockModel.ShiftHours(11f, +1), 1e-6f);
            Assert.AreEqual(11f, WorkClockModel.ShiftHours(0f, -1), 1e-6f);
            Assert.AreEqual(4.5f, WorkClockModel.ShiftHours(3.5f, +1), 1e-6f);   // half-hour grid survives
        }

        // ---------------- selection / visibility rules ----------------

        [Test]
        public void SharedWindow_DetectedWithinTolerance_MixedHides()
        {
            // tol = 0.01 day-frac = 0.12 h (DAT_00736fe0, Tick:117-251).
            var same = new List<(float, float)> { (3f, 6f), (3.1f, 6f), (3f, 6.1f) };
            Assert.IsTrue(WorkClockModel.TrySharedWindow(same, out var s, out var l));
            Assert.AreEqual(3f, s, 1e-6f);
            Assert.AreEqual(6f, l, 1e-6f);

            var mixedStart = new List<(float, float)> { (3f, 6f), (3.2f, 6f) };
            Assert.IsFalse(WorkClockModel.TrySharedWindow(mixedStart, out _, out _), "starts differ > 0.12 h");

            var mixedLen = new List<(float, float)> { (3f, 6f), (3f, 6.2f) };
            Assert.IsFalse(WorkClockModel.TrySharedWindow(mixedLen, out _, out _), "durations differ > 0.12 h");

            Assert.IsFalse(WorkClockModel.TrySharedWindow(new List<(float, float)>(), out _, out _), "empty selection");

            // the single-select port case: one window always shares with itself.
            Assert.IsTrue(WorkClockModel.TrySharedWindow(new List<(float, float)> { (7f, 5f) }, out s, out l));
            Assert.AreEqual(7f, s, 1e-6f);
        }

        [Test]
        public void Sector_HiddenBelowQuarterHour()
        {
            // arc drawn only for windows ≥ 1/48 day = 15 game-min (DAT_00737158, Draw:84).
            Assert.IsFalse(WorkClockModel.SectorVisible(0.24f));
            Assert.IsTrue(WorkClockModel.SectorVisible(0.26f));
            Assert.IsTrue(WorkClockModel.SectorVisible(6f));
        }

        // ---------------- calendar ----------------

        [Test]
        public void Date_CalendarRollover_20DayMonths_12MonthYears()
        {
            // d=(h/12)%20+1, m=(h/240)%12+1, y=h/2880+1 (Draw:759-791; FUN_0050ab10:765-769).
            Assert.AreEqual((1, 1, 1), WorkClockModel.Date(0.0, HourSec));
            Assert.AreEqual((1, 1, 1), WorkClockModel.Date(11 * HourSec, HourSec), "still day 1 before 12 h");
            Assert.AreEqual((2, 1, 1), WorkClockModel.Date(12 * HourSec, HourSec), "day rolls at 12 h");
            Assert.AreEqual((20, 1, 1), WorkClockModel.Date(228 * HourSec, HourSec), "20th day");
            Assert.AreEqual((1, 2, 1), WorkClockModel.Date(240 * HourSec, HourSec), "month rolls at 240 h");
            Assert.AreEqual((1, 12, 1), WorkClockModel.Date(2640 * HourSec, HourSec), "12th month");
            Assert.AreEqual((1, 1, 2), WorkClockModel.Date(2880 * HourSec, HourSec), "year rolls at 2880 h");
        }
    }
}
