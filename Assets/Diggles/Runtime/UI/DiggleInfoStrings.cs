#nullable enable

namespace Diggles.Runtime.UI
{
    using System;

    // The port's lmsg command currently returns its key (DigglesTclEngine.cs:3808), so
    // there is no clean localized-text lookup for this panel yet. Keep every English literal
    // centralized here until that seam exists.
    public static class DiggleInfoStrings
    {
        public const string HitpointsAttribute = "atr_Hitpoints";
        public const string NutritionAttribute = "atr_Nutrition";
        public const string AlertnessAttribute = "atr_Alertness";
        public const string MoodAttribute = "atr_Mood";

        public const string Health = "Health";
        public const string Nutrition = "Nutrition";
        public const string Attention = "Attention";
        public const string Mood = "Mood";

        public const string WaitingForWork = "Waiting for work";
        public const string FreeTime = "Free time";
        public const string Walking = "Walking";
        public const string PickingUp = "Picking up";
        public const string Converting = "Converting";
        public const string PuttingDown = "Putting down";
        public const string Unpacking = "Unpacking";
        public const string Harvesting = "Harvesting";
        public const string Digging = "Digging";
        public const string Fighting = "Fighting";
        public const string Packing = "Packing";
        public const string Carrying = "Carrying";
        public const string Repairing = "Repairing";
        public const string Building = "Building";
        public const string Inventing = "Inventing";
        public const string Working = "Working";
        public const string Guarding = "Guarding";
        public const string Exploring = "Exploring";

        // Native activity-line law: the info page renders the per-gnome planner record
        // (gnome+0x3e4), not the live action verb — the record's kind is described by the
        // plan-describe FUN_005ec410 ("sparetime"/"guard"/"fight"/"build %s @ %s"/"pickup
        // <item>"/"transport <item>"/…) and drawn by the details page FUN_00536f90:85-93,
        // plus "Assigned to <workplace>" while rec+0x40 != -1 (:96-127). The record is
        // written once per task: manual chains via prod_gnome_state (FUN_006a7230; the
        // evt_task_* take-ups z_events.tcl:361-2174, sparetime z_spare_main.tcl:87, back to
        // idle z_work_states.tcl:247), auto plans by the planner's builders. The line is
        // therefore stable for the task's lifetime. Port projection of the same stable
        // sources, in the record's own precedence:
        // 1. ProdState — the ported record verb;
        // 2. the dispatcher carrier step plan (native kinds 0xb unpack / 0xc transport);
        // 3. the workplace claim (native kinds 2/3 build/invent + rec+0x40 binding);
        // 4. waiting/free-time fallbacks.
        // Live micro-state (entity.Action / TaskList head / CurrentState) is deliberately
        // not an input — those flip per micro-step and made the line flicker.
        public static string ActivityFor(
            string? prodState,
            bool hasCarrierPlan,
            bool hasWorkplaceClaim,
            bool isInsideWorktime)
        {
            if (prodState == "sparetime") return FreeTime;      // FUN_005ec410 case 1
            string? described = DescribeStateToken(prodState);
            if (described != null) return described;
            if (hasCarrierPlan) return Carrying;                // kinds 0xb/0xc — the carrier trip
            if (hasWorkplaceClaim) return Working;              // kinds 2/3 — bound to a workplace
            return isInsideWorktime ? WaitingForWork : FreeTime;
        }

        // The prod_gnome_state verb vocabulary (FUN_006a7230 dispatch; script writers listed
        // above). Unknown/idle/empty tokens fall through to the plan/claim sources.
        static string? DescribeStateToken(string? token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            switch (token)
            {
                case "walk": return Walking;
                case "pickup": return PickingUp;
                case "convert": return Converting;
                case "putdown": return PuttingDown;
                case "unpack": return Unpacking;
                case "harvest": return Harvesting;
                case "dig": return Digging;
                case "fight": return Fighting;
                case "pack": return Packing;
                case "guard": return Guarding;
                case "explore": return Exploring;
                case "transport": return Carrying;
                case "build": return Building;
                case "invent": return Inventing;
                case "work": return Working;
                default: return null;
            }
        }

        public static string SkillLabel(string statName)
        {
            return statName switch
            {
                "exp_Nahrung" => "Food",
                "exp_Holz" => "Wood",
                "exp_Stein" => "Stone",
                "exp_Metall" => "Metal",
                "exp_Transport" => "Transport",
                "exp_Energie" => "Energy",
                "exp_Service" => "Service",
                "exp_Kampf" => "Combat",
                "exp_F_Sword" => "Sword",
                "exp_F_Twohanded" => "Two-handed",
                "exp_F_Defense" => "Defense",
                "exp_F_Ballistic" => "Ballistics",
                "exp_F_Kungfu" => "Kung fu",
                _ => CleanStatName(statName)
            };
        }

        static string CleanStatName(string statName)
        {
            if (string.IsNullOrEmpty(statName)) return string.Empty;
            return statName.StartsWith("exp_", StringComparison.Ordinal)
                ? statName.Substring(4).Replace('_', ' ')
                : statName.Replace('_', ' ');
        }
    }
}
