#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.UI;
    using NUnit.Framework;

    public sealed class DiggleInfoPanelTests
    {
        sealed class Row
        {
            public readonly int Handle;
            public Row(int handle) => Handle = handle;
        }

        [TearDown]
        public void TearDown() => RightPanelModel.ResetForTests();

        [Test]
        public void RosterDiff_JoinLeaveDeathAndReorder_ReuseSurvivingRows()
        {
            var roster = new DiggleRoster<Row>();
            var released = new List<int>();
            roster.Sync(new[] { 10, 20 }, handle => new Row(handle), row => released.Add(row.Handle));
            Assert.IsTrue(roster.TryGet(10, out Row first));
            Assert.IsTrue(roster.TryGet(20, out Row second));

            roster.Sync(new[] { 20, 10, 30 }, handle => new Row(handle), row => released.Add(row.Handle));
            Assert.That(roster.Handles, Is.EqualTo(new[] { 20, 10, 30 }), "the requested stable order is applied");
            Assert.IsTrue(roster.TryGet(10, out Row reorderedFirst));
            Assert.IsTrue(roster.TryGet(20, out Row reorderedSecond));
            Assert.AreSame(first, reorderedFirst, "reordering must not rebuild a surviving row");
            Assert.AreSame(second, reorderedSecond, "joining another gnome must not rebuild survivors");
            Assert.IsTrue(roster.TryGet(30, out Row survivor));

            roster.Sync(new[] { 20, 30 }, handle => new Row(handle), row => released.Add(row.Handle));
            Assert.That(released, Is.EqualTo(new[] { 10 }), "a departed gnome releases one row");
            Assert.AreSame(survivor, Get(roster, 30));

            // A dead gnome is absent from the controller's desired-handle input just like a
            // despawned one; the diff must remove it without disturbing the remaining row.
            roster.Sync(new[] { 30 }, handle => new Row(handle), row => released.Add(row.Handle));
            Assert.That(released, Is.EqualTo(new[] { 10, 20 }));
            Assert.That(roster.Handles, Is.EqualTo(new[] { 30 }));
            Assert.AreSame(survivor, Get(roster, 30));
        }

        [Test]
        public void RosterMembership_ExcludesForeignNonGnomeAndDeadEntities()
        {
            Assert.IsTrue(DiggleInfoPanelLogic.IsRosterMember(
                "gnome", 0, 0, false, false, true, 1d));
            Assert.IsFalse(DiggleInfoPanelLogic.IsRosterMember(
                "gnome", 1, 0, false, false, true, 1d));
            // Children (class Baby, kind "baby" — bear_child z_spare_reprod.tcl:530-548,
            // def_class baby.tcl:1) list alongside adults in the native roster.
            Assert.IsTrue(DiggleInfoPanelLogic.IsRosterMember(
                "baby", 0, 0, false, false, true, 1d));
            Assert.IsFalse(DiggleInfoPanelLogic.IsRosterMember(
                "sheep", 0, 0, false, false, true, 1d),
                "non-roster kinds stay out");
            Assert.IsFalse(DiggleInfoPanelLogic.IsRosterMember(
                "gnome", 0, 0, true, false, true, 1d));
            Assert.IsFalse(DiggleInfoPanelLogic.IsRosterMember(
                "gnome", 0, 0, false, true, true, 1d));
            Assert.IsFalse(DiggleInfoPanelLogic.IsRosterMember(
                "gnome", 0, 0, false, false, true, 0d));
            Assert.IsTrue(DiggleInfoPanelLogic.IsRosterMember(
                "gnome", 0, 0, false, false, false, 0d),
                "a partial context with no HP definition must not classify every gnome as dead");
        }

        // The native row branches on the auto-schedule flag first (obj+0x3d0 bit 0x800000,
        // FUN_00535490:230-249; FUN_00535d70:157-163): auto off → the manual icon 0x80
        // regardless of worktime; auto on outside worktime → freetime; inside worktime the
        // planner record's job state decides.
        [TestCase(false, true, true, DiggleWorkStatus.ManualMode)]
        [TestCase(false, true, false, DiggleWorkStatus.ManualMode)]
        [TestCase(false, false, true, DiggleWorkStatus.ManualMode)]
        [TestCase(false, false, false, DiggleWorkStatus.ManualMode)]
        [TestCase(true, true, true, DiggleWorkStatus.WorktimeWithJob)]
        [TestCase(true, true, false, DiggleWorkStatus.WorktimeWithoutJob)]
        [TestCase(true, false, true, DiggleWorkStatus.Freetime)]
        [TestCase(true, false, false, DiggleWorkStatus.Freetime)]
        public void WorkStatusPredicate_ReadsTheNativeAutoScheduleFlag(
            bool autoScheduleOn,
            bool inWorktime,
            bool hasJob,
            DiggleWorkStatus expected)
        {
            Assert.AreEqual(expected,
                DiggleInfoPanelLogic.ResolveWorkStatus(autoScheduleOn, inWorktime, hasJob));
        }

        [TestCase(0.00d, DiggleMood.Sad)]
        [TestCase(0.33d, DiggleMood.Sad)]
        [TestCase(0.3349d, DiggleMood.Sad)]
        [TestCase(0.3351d, DiggleMood.Neutral)]
        [TestCase(0.34d, DiggleMood.Neutral)]
        [TestCase(0.65d, DiggleMood.Neutral)]
        [TestCase(0.6549d, DiggleMood.Neutral)]
        [TestCase(0.6551d, DiggleMood.Happy)]
        [TestCase(0.66d, DiggleMood.Happy)]
        [TestCase(1.00d, DiggleMood.Happy)]
        public void MoodThresholds_MatchRoundedPercentBoundaries(double value, DiggleMood expected)
        {
            Assert.AreEqual(expected, DiggleInfoPanelLogic.ResolveMood(value));
        }

        [Test]
        public void SelectionRoute_PlainReplacesAndShiftAdds_ThroughRealSelectionSet()
        {
            // Native click consumption clears unless Shift permits inclusion, then includes the
            // hovered handle (FUN_005309c0:79-95).
            var context = new DigglesTclContext(new TclInterpreter()) { LocalPlayer = 0 };
            var gnomeClass = new DigglesClass { Name = "TestGnome", Kind = "gnome" };
            context.RegisterEntityAtHandle(new DigglesTclEntity { Class = gnomeClass, Owner = 0 }, 11);
            context.RegisterEntityAtHandle(new DigglesTclEntity { Class = gnomeClass, Owner = 0 }, 22);

            context.Selection.ClickSelect(11, additive: false);
            int afterPlain = context.Selection.Version;
            Assert.That(context.Selection.Handles, Is.EqualTo(new[] { 11 }));

            context.Selection.ClickSelect(22, additive: true);
            Assert.Greater(context.Selection.Version, afterPlain);
            Assert.That(context.Selection.Handles, Is.EqualTo(new[] { 11, 22 }));
            Assert.AreEqual(11, context.SelectedHandle, "Shift-add preserves the primary");

            context.Selection.ClickSelect(22, additive: false);
            Assert.That(context.Selection.Handles, Is.EqualTo(new[] { 22 }));
            Assert.AreEqual(22, context.SelectedHandle, "plain click replaces the set");
        }

        [Test]
        public void VisibilityLaw_CoversCollapsedModeAndPrimarySelectionTransitions()
        {
            Assert.IsTrue(DiggleInfoPanelLogic.IsPanelVisible(
                false, RightPanelMode.Gnomes, inGameplayWorld: true, sequenceActive: false));
            Assert.IsFalse(DiggleInfoPanelLogic.IsPanelVisible(
                true, RightPanelMode.Gnomes, inGameplayWorld: true, sequenceActive: false));
            Assert.IsFalse(DiggleInfoPanelLogic.IsPanelVisible(
                false, RightPanelMode.Buildings, inGameplayWorld: true, sequenceActive: false));
            Assert.IsTrue(DiggleInfoPanelLogic.IsStatsVisible(panelVisible: true, primaryIsOwnedGnome: true));
            Assert.IsFalse(DiggleInfoPanelLogic.IsStatsVisible(panelVisible: true, primaryIsOwnedGnome: false));
            Assert.IsFalse(DiggleInfoPanelLogic.IsStatsVisible(panelVisible: false, primaryIsOwnedGnome: true));

            RightPanelModel.ResetForTests();
            RightPanelModel.SetMode(RightPanelMode.Gnomes);
            Assert.IsTrue(DiggleInfoPanelLogic.IsPanelVisible(
                RightPanelModel.Collapsed, RightPanelModel.Mode, true, false));
            RightPanelModel.SetCollapsed(true);
            Assert.IsFalse(DiggleInfoPanelLogic.IsPanelVisible(
                RightPanelModel.Collapsed, RightPanelModel.Mode, true, false));
            RightPanelModel.SetCollapsed(false);
            Assert.IsTrue(DiggleInfoPanelLogic.IsPanelVisible(
                RightPanelModel.Collapsed, RightPanelModel.Mode, true, false));
            RightPanelModel.SetMode(RightPanelMode.Buildings);
            Assert.IsFalse(DiggleInfoPanelLogic.IsPanelVisible(
                RightPanelModel.Collapsed, RightPanelModel.Mode, true, false));
        }

        [Test]
        public void VisibilityLaw_MenuAndCutsceneGates_MirrorTheLetterboxPair()
        {
            // Menu law (DigglesWorldRuntime.BindSession:322): _atMenu = no original save AND
            // not a StoryMgr world — the same pair ScreenManager.ShouldShowLetterbox reads.
            Assert.IsFalse(DiggleInfoPanelLogic.IsGameplayWorld(isStoryMgr: false, hasOriginalSave: false),
                "the menu backdrop world must hide the panel");
            Assert.IsTrue(DiggleInfoPanelLogic.IsGameplayWorld(isStoryMgr: true, hasOriginalSave: false),
                "a campaign world is gameplay");
            Assert.IsTrue(DiggleInfoPanelLogic.IsGameplayWorld(isStoryMgr: false, hasOriginalSave: true),
                "the tutorial (loaded save, IsStoryMgr false) is gameplay");
            Assert.IsTrue(DiggleInfoPanelLogic.IsGameplayWorld(isStoryMgr: true, hasOriginalSave: true));

            // The panel hides at the menu and yields to cutscenes even when open in Gnomes mode.
            Assert.IsFalse(DiggleInfoPanelLogic.IsPanelVisible(
                false, RightPanelMode.Gnomes, inGameplayWorld: false, sequenceActive: false));
            Assert.IsFalse(DiggleInfoPanelLogic.IsPanelVisible(
                false, RightPanelMode.Gnomes, inGameplayWorld: true, sequenceActive: true));
            Assert.IsTrue(DiggleInfoPanelLogic.IsPanelVisible(
                false, RightPanelMode.Gnomes, inGameplayWorld: true, sequenceActive: false),
                "the panel restores after the cutscene ends");
        }

        [Test]
        public void RightPanelModel_ModeToggleCompatibilityAndReset_AreCoherent()
        {
            RightPanelModel.ResetForTests();
            int legacyEvents = 0;
            int modeEvents = 0;
            int collapsedEvents = 0;
            RightPanelModel.ToggleRequested += _ => legacyEvents++;
            RightPanelModel.ModeChanged += _ => modeEvents++;
            RightPanelModel.CollapsedChanged += _ => collapsedEvents++;

            Assert.AreEqual(RightPanelMode.Buildings, RightPanelModel.Mode);
            Assert.IsFalse(RightPanelModel.Collapsed);
            Assert.IsFalse(RightPanelModel.Open);
            RightPanelModel.RequestToggle();
            Assert.AreEqual(RightPanelMode.Gnomes, RightPanelModel.Mode);
            Assert.IsTrue(RightPanelModel.Open);
            RightPanelModel.RequestToggle();
            Assert.AreEqual(RightPanelMode.Buildings, RightPanelModel.Mode);
            Assert.IsFalse(RightPanelModel.Open);

            RightPanelModel.SetOpen(true);
            Assert.AreEqual(RightPanelMode.Gnomes, RightPanelModel.Mode);
            Assert.IsFalse(RightPanelModel.Collapsed);
            Assert.IsTrue(RightPanelModel.Open);
            RightPanelModel.SetOpen(false);
            Assert.IsTrue(RightPanelModel.Collapsed);
            Assert.IsFalse(RightPanelModel.Open);
            Assert.AreEqual(4, legacyEvents);
            Assert.AreEqual(3, modeEvents);
            Assert.AreEqual(1, collapsedEvents);

            RightPanelModel.ResetForTests();
            RightPanelModel.SetMode(RightPanelMode.Gnomes);
            RightPanelModel.SetCollapsed(true);
            Assert.AreEqual(4, legacyEvents, "ResetForTests clears legacy subscribers");
            Assert.AreEqual(3, modeEvents, "ResetForTests clears mode subscribers");
            Assert.AreEqual(1, collapsedEvents, "ResetForTests clears collapse subscribers");
        }

        // ---- Activity line (the native planner-record law) --------------------------------
        // FUN_00536f90:85-127 renders the per-gnome record (gnome+0x3e4) via the plan
        // describe FUN_005ec410 — a per-task verb, stable for the task's lifetime. The
        // record verb is the ported ProdState (prod_gnome_state FUN_006a7230, written at
        // task take-up, z_events.tcl:361-2174); live micro-action state is not an input.

        [TestCase("walk", DiggleInfoStrings.Walking)]
        [TestCase("pickup", DiggleInfoStrings.PickingUp)]
        [TestCase("convert", DiggleInfoStrings.Converting)]
        [TestCase("putdown", DiggleInfoStrings.PuttingDown)]
        [TestCase("unpack", DiggleInfoStrings.Unpacking)]
        [TestCase("harvest", DiggleInfoStrings.Harvesting)]
        [TestCase("dig", DiggleInfoStrings.Digging)]
        [TestCase("fight", DiggleInfoStrings.Fighting)]
        [TestCase("guard", DiggleInfoStrings.Guarding)]
        [TestCase("explore", DiggleInfoStrings.Exploring)]
        [TestCase("transport", DiggleInfoStrings.Carrying)]
        public void ActivityLine_UsesTheStableRecordVerb(string prodState, string expected)
        {
            // The record verb wins over every other source and cannot flicker: the same
            // inputs always produce the same line until prod_gnome_state rewrites the record.
            Assert.AreEqual(expected, DiggleInfoStrings.ActivityFor(
                prodState, hasCarrierPlan: true, hasWorkplaceClaim: true, isInsideWorktime: true));
            Assert.AreEqual(expected, DiggleInfoStrings.ActivityFor(
                prodState, hasCarrierPlan: false, hasWorkplaceClaim: false, isInsideWorktime: false));
        }

        [Test]
        public void ActivityLine_FallsBackToPlanClaimThenWorktime()
        {
            // sparetime is the record's own kind 1 (FUN_005ec410 case 1).
            Assert.AreEqual(DiggleInfoStrings.FreeTime, DiggleInfoStrings.ActivityFor(
                "sparetime", hasCarrierPlan: true, hasWorkplaceClaim: true, isInsideWorktime: true));
            // idle/empty/unknown record → the dispatcher carrier plan (native kinds 0xb/0xc).
            Assert.AreEqual(DiggleInfoStrings.Carrying, DiggleInfoStrings.ActivityFor(
                "idle", hasCarrierPlan: true, hasWorkplaceClaim: true, isInsideWorktime: true));
            // No plan → the workplace claim (native kinds 2/3 + rec+0x40 binding).
            Assert.AreEqual(DiggleInfoStrings.Working, DiggleInfoStrings.ActivityFor(
                "", hasCarrierPlan: false, hasWorkplaceClaim: true, isInsideWorktime: true));
            // Nothing at all → waiting inside worktime, free time outside.
            Assert.AreEqual(DiggleInfoStrings.WaitingForWork, DiggleInfoStrings.ActivityFor(
                null, hasCarrierPlan: false, hasWorkplaceClaim: false, isInsideWorktime: true));
            Assert.AreEqual(DiggleInfoStrings.FreeTime, DiggleInfoStrings.ActivityFor(
                null, hasCarrierPlan: false, hasWorkplaceClaim: false, isInsideWorktime: false));
        }

        // ---- Knowledge rows (the native skill-page law, FUN_00536f90) ---------------------
        // Collect loop:263-401 — "exp_"-prefixed attributes only (DAT_00721330), skipping
        // "exp_Kampf" (:333-353, it feeds the separate combat line:469-540); a row exists
        // only while value > 0.0 (:381-390, DAT_00736f78); descending stable sort
        // (:402-459) and a top-5 cap (:461-463).

        [Test]
        public void KnowledgeRows_OnlySkillsTheGnomeActuallyHas()
        {
            Assert.IsTrue(DiggleInfoPanelLogic.IsKnowledgeRowVisible("exp_Holz", 0.25d));
            Assert.IsFalse(DiggleInfoPanelLogic.IsKnowledgeRowVisible("exp_Holz", 0d),
                "a zero-valued skill must not render a row");
            Assert.IsFalse(DiggleInfoPanelLogic.IsKnowledgeRowVisible("exp_Holz", -0.1d));
            Assert.IsFalse(DiggleInfoPanelLogic.IsKnowledgeRowVisible("exp_Kampf", 0.9d),
                "exp_Kampf feeds the dedicated combat line, never a plain row");
            Assert.IsFalse(DiggleInfoPanelLogic.IsKnowledgeRowVisible("atr_Mood", 0.9d),
                "non-exp attributes are not knowledge rows");
            Assert.IsTrue(DiggleInfoPanelLogic.IsKnowledgeSkill("exp_F_Sword"),
                "combat SUBskills carry the exp_ prefix and stay in the pool");
            Assert.IsFalse(DiggleInfoPanelLogic.IsKnowledgeSkill(null));
        }

        [Test]
        public void KnowledgeRows_SortDescendingStable_AndCapAtFive()
        {
            var rows = new List<DiggleInfoPanelLogic.KnowledgeCandidate>
            {
                new("exp_Nahrung", 0.10d),
                new("exp_Holz", 0.70d),
                new("exp_Stein", 0.30d),
                new("exp_Metall", 0.30d),   // tie with exp_Stein — registry order must hold
                new("exp_Transport", 0.20d),
                new("exp_Energie", 0.05d),
                new("exp_Service", 0.90d),
            };
            DiggleInfoPanelLogic.OrderKnowledgeRows(rows);

            Assert.AreEqual(DiggleInfoPanelLogic.MaxKnowledgeRows, rows.Count, "top-5 cap");
            Assert.AreEqual("exp_Service", rows[0].Name);
            Assert.AreEqual("exp_Holz", rows[1].Name);
            Assert.AreEqual("exp_Stein", rows[2].Name, "ties keep registry order (stable sort)");
            Assert.AreEqual("exp_Metall", rows[3].Name);
            Assert.AreEqual("exp_Transport", rows[4].Name);
        }

        static Row Get(DiggleRoster<Row> roster, int handle)
        {
            Assert.IsTrue(roster.TryGet(handle, out Row row));
            return row;
        }
    }
}
