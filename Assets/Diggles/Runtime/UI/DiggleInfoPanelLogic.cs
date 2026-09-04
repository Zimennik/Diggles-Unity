#nullable enable

namespace Diggles.Runtime.UI
{
    using System;
    using System.Collections.Generic;

    public enum DiggleWorkStatus
    {
        WorktimeWithJob,
        WorktimeWithoutJob,
        Freetime,
        ManualMode
    }

    public enum DiggleMood
    {
        Happy,
        Neutral,
        Sad
    }

    // Unity-free decisions used by the controller and EditMode tests.
    public static class DiggleInfoPanelLogic
    {
        // FUN_00535490:345-364 converts atr_Mood to a rounded percentage, selects sad below
        // 34, neutral from 34 through 65, and happy from 66 onward.
        public const int SadUpperPercentExclusive = 34;
        public const int HappyLowerPercentInclusive = 66;

        // The canonical menu law: DigglesWorldRuntime.BindSession:322 computes
        // _atMenu = OriginalSaveState == null && !IsStoryMgr. Real gameplay is therefore
        // "campaign (IsStoryMgr) OR any loaded save" — exactly the pair the letterbox gate
        // reads (ScreenManager.ShouldShowLetterbox). The tutorial world is IsStoryMgr==false
        // yet real gameplay, reached via a save load, so both signals must be read.
        public static bool IsGameplayWorld(bool isStoryMgr, bool hasOriginalSave)
            => isStoryMgr || hasOriginalSave;

        // Panel visibility = the authored open/mode state AND real gameplay AND no cutscene.
        // The gameplay HUD yields to cutscenes (the same SequenceActive gate the bottom menu
        // uses, BottomMenuController.Tick), and the panel never exists over the main menu's
        // ambient backdrop worlds (the _atMenu law above).
        public static bool IsPanelVisible(
            bool isCollapsed,
            RightPanelMode mode,
            bool inGameplayWorld,
            bool sequenceActive)
            => !isCollapsed && mode == RightPanelMode.Gnomes
               && inGameplayWorld && !sequenceActive;

        public static bool IsStatsVisible(bool panelVisible, bool primaryIsOwnedGnome)
            => panelVisible && primaryIsOwnedGnome;

        public static bool IsRosterMember(
            string? kind,
            int owner,
            int localPlayer,
            bool diedInFight,
            bool deleteInProgress,
            bool hitpointsDefined,
            double hitpoints)
        {
            // Children spawn as class Baby with kind "baby" (bear_child → `new Baby`,
            // z_spare_reprod.tcl:530-548; def_class Baby none baby — baby.tcl:1) and the
            // native roster lists them alongside adults.
            bool rosterKind = string.Equals(kind, "gnome", StringComparison.Ordinal)
                || string.Equals(kind, "baby", StringComparison.Ordinal);
            return rosterKind
                && owner == localPlayer
                && !diedInFight
                && !deleteInProgress
                && (!hitpointsDefined || hitpoints > 0d);
        }

        // The native row's work-status icon law (FUN_00535490:230-253):
        //:231 the auto-schedule flag is checked first — obj+0x3d0 bit 0x800000 set (auto
        // off) → the manual icon 0x80 (:248), regardless of worktime. The compact row
        // variant repeats it (FUN_00535d70:157-163). The bit is the same flag the
        // dispatcher gates pool entry on (FUN_00433a70:112, FUN_00436750:69,
        // FUN_00434e60:606-608) and the clock widget's auto button toggles
        // (FUN_0050c2b0:75-89 obj+0x3d0 ^= 0x800000); the port mirrors it as
        // entity.ProdAutoSchedule (set/get_prodautoschedule, ProdDispatcher:459-462).
        //:232 auto on but outside the work window (byte +0x35c clear) → freetime icon 8.
        //:233-245 inside worktime: the planner record (+0x3e4) decides — a live workplace
        // binding (rec+0x40 != -1) or queued jobs → the working icons (0x8000/0x800),
        // none → waiting (0x10/1). Projected onto the port's with/without-job pair.
        public static DiggleWorkStatus ResolveWorkStatus(
            bool autoScheduleOn,
            bool isInsideWorktime,
            bool hasAssignedJob)
        {
            if (!autoScheduleOn) return DiggleWorkStatus.ManualMode;
            if (!isInsideWorktime) return DiggleWorkStatus.Freetime;
            return hasAssignedJob
                ? DiggleWorkStatus.WorktimeWithJob
                : DiggleWorkStatus.WorktimeWithoutJob;
        }

        public static DiggleMood ResolveMood(double moodValue)
        {
            double clamped = moodValue < 0d ? 0d : moodValue > 1d ? 1d : moodValue;
            int percent = (int)Math.Round(clamped * 100d, MidpointRounding.ToEven);
            if (percent < SadUpperPercentExclusive) return DiggleMood.Sad;
            return percent < HappyLowerPercentInclusive ? DiggleMood.Neutral : DiggleMood.Happy;
        }

        // ---- Knowledge page row law (native skill page FUN_00536f90) --------------------
        // The native page does not list every defined skill. Its collect loop (:263-401)
        // scans the attribute registry for names prefixed "exp_" (DAT_00721330), skips
        // "exp_Kampf" (:333-353 — Kampf feeds the page's dedicated combat cur/max line,
        //:469-540, drawn separately) and keeps only values > 0.0 (:381-390, threshold
        // DAT_00736f78 = 0.0f). The survivors are bubble-sorted descending by value —
        // swapping only on strictly-greater, so ties keep registry order (:402-459) — and
        // capped at the top 5 (:461-463). The combat aggregate line is not ported (the
        // authored panel has no two-value row); documented delta.
        public const int MaxKnowledgeRows = 5;

        public readonly struct KnowledgeCandidate
        {
            public readonly string Name;
            public readonly double Value;

            public KnowledgeCandidate(string name, double value)
            {
                Name = name;
                Value = value;
            }
        }

        public static bool IsKnowledgeSkill(string? name)
            => name != null
               && name.StartsWith("exp_", StringComparison.Ordinal)
               && !string.Equals(name, "exp_Kampf", StringComparison.Ordinal);

        // FUN_00536f90:383 — a row exists only while the gnome actually has the skill.
        public static bool IsKnowledgeRowVisible(string? name, double value)
            => IsKnowledgeSkill(name) && value > 0d;

        // Stable descending sort + top-5 cap (:402-459,:461-463). Insertion sort keeps
        // ties in registry order, matching the native strictly-greater bubble swap.
        public static void OrderKnowledgeRows(List<KnowledgeCandidate> rows)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                KnowledgeCandidate current = rows[i];
                int j = i - 1;
                while (j >= 0 && rows[j].Value < current.Value)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }
                rows[j + 1] = current;
            }
            if (rows.Count > MaxKnowledgeRows)
                rows.RemoveRange(MaxKnowledgeRows, rows.Count - MaxKnowledgeRows);
        }
    }
}
