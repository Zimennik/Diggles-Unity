#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    public sealed class NewsTickerSystemTests
    {
        static (TclInterpreter Interp, DigglesTclContext Context, DigglesGameSim Sim) Host()
        {
            var interpreter = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            var context = DigglesTclEngine.Register(interpreter);
            var sim = new DigglesGameSim(context);
            context.IsStoryMgr = true;
            return (interpreter, context, sim);
        }

        [Test]
        public void Command_ImplementsNativeDefaultsOptionsOwnerFilterAndClick()
        {
            var (interpreter, context, _) = Host();

            Assert.That(interpreter.Eval("newsticker new 1 -unknown ignored"), Is.Empty,
                "foreign-owner entries are rejected before native option parsing");
            Assert.That(context.NewsTicker.ActiveEntries, Is.Empty);

            string id = interpreter.Eval("newsticker new 0");
            Assert.That(id, Is.EqualTo("1"));
            Assert.That(context.NewsTicker.TryGetActive(1, out var defaults), Is.True);
            Assert.That(defaults.Text, Is.EqualTo("msg"));
            Assert.That(defaults.Category, Is.EqualTo(NewsTickerCategory.Fight));
            Assert.That(defaults.Color, Is.EqualTo(new UnityEngine.Color32(255, 255, 255, 255)));
            Assert.That(defaults.Priority, Is.EqualTo(1f));
            Assert.That(defaults.RemainingTime, Is.EqualTo(NewsTickerSystem.DefaultTimeout));
            Assert.That(defaults.AppearsInHistory, Is.True);

            Assert.That(interpreter.Eval("newsticker change 1 -text {Story alert} "
                + "-category PROGRESS -color {300 -2 64} -priority 5.5 -time 12 "
                + "-click {set ticker_clicked yes} -history true"), Is.EqualTo("1"));
            Assert.That(context.NewsTicker.TryGetActive(1, out var changed), Is.True);
            Assert.That(changed.Text, Is.EqualTo("Story alert"));
            Assert.That(changed.Category, Is.EqualTo(NewsTickerCategory.Progress));
            Assert.That(changed.Color, Is.EqualTo(new UnityEngine.Color32(255, 0, 64, 255)));
            Assert.That(changed.Priority, Is.EqualTo(5.5f));
            Assert.That(changed.RemainingTime, Is.EqualTo(12f));
            Assert.That(context.NewsTicker.Click(1), Is.True);
            Assert.That(interpreter.Eval("set ticker_clicked"), Is.EqualTo("yes"));
            Assert.That(context.NewsTicker.HistoryEntries, Has.Count.EqualTo(1));
            Assert.That(context.NewsTicker.HistoryEntries[0].Text, Is.EqualTo("Story alert"));

            Assert.Throws<TclException>(() => interpreter.Eval("newsticker change 1 -unknown value"));
            Assert.Throws<TclException>(() => interpreter.Eval("newsticker change 1 -text"));
            Assert.Throws<TclException>(() => interpreter.Eval("newsticker new 0 -category invalid"));
        }

        [Test]
        public void KftFont_ParsesStdVisualFontAndMeasuresNativeWidths()
        {
            var (_, fontPath) = RequireFont("stdvis", "std_stdfont.kft");
            KftFont font = KftFont.Load(fontPath);

            Assert.That(font.LineHeight, Is.GreaterThan(0));
            Assert.That(font.MeasureWidth("Finde die 6 Ringe"), Is.GreaterThan(0));
            Assert.That(font.MeasureWidth(""), Is.Zero);
            Assert.That(font.MeasureWidth("A"), Is.LessThan(font.MeasureWidth("AA")));
            Assert.That(font.MeasureWidth("AA"), Is.LessThan(font.MeasureWidth("AAA")));
        }

        [Test]
        public void TextOption_UsesStandardKftMetrics()
        {
            var (dataRoot, fontPath) = RequireFont("Fonts", "Verdana_11.kft");
            KftFont font = KftFont.Load(fontPath);
            var (interpreter, context, _) = Host();
            context.DataRoot = dataRoot;

            Assert.That(interpreter.Eval("newsticker new 0"), Is.EqualTo("1"));
            NewsTickerSystem.EntrySaveState defaults = context.NewsTicker.Capture().Active[0];
            Assert.That(defaults.TextMetricX, Is.EqualTo(font.MeasureWidth("msg")));
            Assert.That(defaults.TextMetricY, Is.EqualTo(font.LineHeight));

            Assert.That(interpreter.Eval(
                "newsticker change 1 -text {Finde die 6 Ringe}"), Is.EqualTo("1"));
            NewsTickerSystem.EntrySaveState changed = context.NewsTicker.Capture().Active[0];
            Assert.That(changed.TextMetricX,
                Is.EqualTo(font.MeasureWidth("Finde die 6 Ringe")));
            Assert.That(changed.TextMetricY, Is.EqualTo(font.LineHeight));
        }

        [Test]
        public void MaskHistoryDeleteAndChangeMinusOne_MatchNativeContract()
        {
            var (interpreter, context, _) = Host();
            interpreter.Eval("newsticker new 0 -text fight -click {set history_clicked 1}");
            interpreter.Eval("newsticker new 0 -text progress -category progress -history false");
            Assert.That(context.NewsTicker.HistoryEntries, Has.Count.EqualTo(1));

            Assert.That(interpreter.Eval("newsticker mask ?fight ?progress"), Is.EqualTo("1"));
            Assert.That(interpreter.Eval("newsticker mask -fight -progress ?fight ?progress"), Is.EqualTo("0"));
            var visible = new List<NewsTickerEntry>();
            context.NewsTicker.CopyVisibleEntries(visible);
            Assert.That(visible, Is.Empty);
            Assert.That(interpreter.Eval("newsticker mask +progress ?progress"), Is.EqualTo("1"));
            context.NewsTicker.CopyVisibleEntries(visible);
            Assert.That(visible, Has.Count.EqualTo(1));
            Assert.That(visible[0].Text, Is.EqualTo("progress"));

            interpreter.Eval("newsticker delete 1 ignored_by_native");
            Assert.That(context.NewsTicker.TryGetActive(1, out _), Is.False);
            Assert.That(context.NewsTicker.HistoryEntries, Has.Count.EqualTo(1),
                "delete removes the active/display record but retains the native history copy");
            Assert.That(context.NewsTicker.Click(1), Is.True,
                "expanded native history rows retain their click handler after active expiry/deletion");
            Assert.That(interpreter.Eval("set history_clicked"), Is.EqualTo("1"));

            Assert.That(interpreter.Eval("newsticker change -1 -text replacement"), Is.EqualTo("3"));
            Assert.That(context.NewsTicker.TryGetActive(3, out var replacement), Is.True);
            Assert.That(replacement.Text, Is.EqualTo("replacement"));
        }

        [Test]
        public void ScaledTick_ExpiresPositiveTimeoutsAndKeepsNonPositiveEntries()
        {
            var (interpreter, context, sim) = Host();
            interpreter.Eval("newsticker new 0 -text expiring -time 1");
            interpreter.Eval("newsticker new 0 -text permanent -time 0");

            sim.Step(0.75);
            Assert.That(context.NewsTicker.TryGetActive(1, out _), Is.True);
            sim.Step(0.30);
            Assert.That(context.NewsTicker.TryGetActive(1, out _), Is.False);
            Assert.That(context.NewsTicker.TryGetActive(2, out _), Is.True);
            Assert.That(context.NewsTicker.HistoryEntries, Has.Count.EqualTo(2),
                "expiry does not erase the separate history list");
        }

        [Test]
        public void SaveState_RestoresTickerAndRejectsMalformedStateBeforeMutation()
        {
            var (interpreter, context, _) = Host();
            interpreter.Eval("newsticker new 0 -text saved -category mood -time 77");
            interpreter.Eval("newsticker mask -mood");
            NewsTickerSystem.SaveState snapshot = context.NewsTicker.Capture();
            Assert.That(snapshot.Active[0].PackedColorWord, Is.EqualTo(-1));
            Assert.That(snapshot.Active[0].NativeCategoryWord,
                Is.EqualTo((int)NewsTickerCategory.Mood));
            snapshot.Active[0].PackedColorWord = unchecked((int)0xff123456);
            snapshot.Active[0].NativeCategoryWord = unchecked((int)0xdfdfdfdf);
            snapshot.Active[0].TextMetricX = 97;
            snapshot.Active[0].TextMetricY = 11;
            snapshot.Active[0].EffectivePriority = 50f;
            snapshot.Active[0].PresentationFlag0 = 1;
            snapshot.Active[0].PresentationFlag1 = 0;
            snapshot.Active[0].Timestamp = 1234.5d;
            snapshot.History[0].PackedColorWord = snapshot.Active[0].PackedColorWord;
            snapshot.History[0].NativeCategoryWord = snapshot.Active[0].NativeCategoryWord;
            snapshot.DisplayQueue = new[]
            {
                new NewsTickerSystem.DisplayQueueSaveState
                {
                    Id = 1,
                    Position = 1380.65234375f,
                },
            };

            interpreter.Eval("newsticker delete 1");
            interpreter.Eval("newsticker mask +mood");
            context.NewsTicker.Restore(snapshot);

            Assert.That(context.NewsTicker.TryGetActive(1, out var restored), Is.True);
            Assert.That(restored.Text, Is.EqualTo("saved"));
            Assert.That(restored.Category, Is.EqualTo(NewsTickerCategory.Mood));
            Assert.That(restored.RemainingTime, Is.EqualTo(77f));
            Assert.That(context.NewsTicker.IsCategoryEnabled(NewsTickerCategory.Mood), Is.False);
            NewsTickerSystem.SaveState recaptured = context.NewsTicker.Capture();
            Assert.That(recaptured.Active[0].PackedColorWord,
                Is.EqualTo(snapshot.Active[0].PackedColorWord));
            Assert.That(recaptured.Active[0].NativeCategoryWord,
                Is.EqualTo(snapshot.Active[0].NativeCategoryWord));
            Assert.That(recaptured.Active[0].TextMetricX, Is.EqualTo(97));
            Assert.That(recaptured.Active[0].TextMetricY, Is.EqualTo(11));
            Assert.That(recaptured.Active[0].EffectivePriority, Is.EqualTo(50f));
            Assert.That(recaptured.Active[0].PresentationFlag0, Is.EqualTo(1));
            Assert.That(recaptured.Active[0].PresentationFlag1, Is.Zero);
            Assert.That(recaptured.Active[0].Timestamp, Is.EqualTo(1234.5d));
            Assert.That(recaptured.History[0].PackedColorWord,
                Is.EqualTo(snapshot.History[0].PackedColorWord));
            Assert.That(recaptured.History[0].NativeCategoryWord,
                Is.EqualTo(snapshot.History[0].NativeCategoryWord));
            Assert.That(recaptured.DisplayQueue, Has.Length.EqualTo(1));
            Assert.That(recaptured.DisplayQueue[0].Id, Is.EqualTo(1));
            Assert.That(recaptured.DisplayQueue[0].Position, Is.EqualTo(1380.65234375f));
            context.NewsTicker.Tick(0.25d);
            Assert.That(context.NewsTicker.DisplayQueueEntries, Has.Count.EqualTo(1),
                "timer progress must not invalidate live layout state");
            Assert.That(interpreter.Eval("newsticker new 0"), Is.EqualTo("2"));
            Assert.That(context.NewsTicker.DisplayQueueEntries, Is.Empty,
                "ticker mutations invalidate the layout producer state");

            NewsTickerSystem.SaveState malformed = context.NewsTicker.Capture();
            malformed.Active[0].Category = NewsTickerSystem.CategoryCount;
            interpreter.Eval("set ticker_validation live");
            Assert.Throws<ArgumentException>(() => context.NewsTicker.Restore(malformed));
            Assert.That(interpreter.Eval("set ticker_validation"), Is.EqualTo("live"));
        }

        [Test]
        public void Click_WithAction_EvaluatesExactlyOnceInMainInterpreterGlobalScope()
        {
            var (interpreter, context, _) = Host();
            interpreter.Eval("set ticker_click_count 0");
            interpreter.Eval("proc create_click_ticker {} {"
                + "set ticker_click_count 99; "
                + "newsticker new 0 -click {incr ticker_click_count}}");

            Assert.That(interpreter.Eval("create_click_ticker"), Is.EqualTo("1"));
            Assert.That(context.NewsTicker.TryGetActive(1, out var entry), Is.True);
            Assert.That(entry.ClickScript, Is.EqualTo("incr ticker_click_count"));

            // Native dispatches entry+0x28 once through Tcl_EvalObjEx(..., 0) from the main UI
            // event path (FUN_00519800:61-77; FUN_00555a00:50-57; FUN_006bc2c0:70-76).
            // Creating the entry in a proc gives it a local shadow; clicking after that proc returns
            // must therefore increment the main interpreter's global, not the producer's local.
            Assert.That(context.NewsTicker.Click(1), Is.True);
            Assert.That(interpreter.Eval("set ticker_click_count"), Is.EqualTo("1"));
        }

        [Test]
        public void Click_WithoutAction_IsInert()
        {
            var (interpreter, context, _) = Host();
            interpreter.Eval("set inert_ticker_sentinel unchanged");
            Assert.That(interpreter.Eval("newsticker new 0 -text inert"), Is.EqualTo("1"));
            Assert.That(context.NewsTicker.TryGetActive(1, out var entry), Is.True);
            Assert.That(entry.ClickScript, Is.Empty);

            // Native skips the evaluator when the selected action object is null
            // (FUN_00519800:61-77, 91-95).
            Assert.That(context.NewsTicker.Click(1), Is.False);
            Assert.That(interpreter.Eval("set inert_ticker_sentinel"), Is.EqualTo("unchanged"));
        }

        [Test]
        public void Click_SetViewAction_MutatesCameraTargetState()
        {
            var (interpreter, context, _) = Host();
            var camera = context.Camera!;
            camera.Focus = new UnityEngine.Vector3(-4f, -5f, 7f);

            // Scripts/classes/zwerg/baby.tcl:427-438 stores this same set_view shape for
            // the zwerggeboren message.
            Assert.That(interpreter.Eval(
                "newsticker new 0 -click {set_view 42.5 17.25 0 -0.35 0}"),
                Is.EqualTo("1"));
            Assert.That(context.NewsTicker.Click(1), Is.True);

            // Native SetView writes x/y/zoom TARGETS at +0x134/+0x138/+0x13c and dx/dy at
            // +0x14c/+0x150 (FUN_006284e0:132-173, 175-245). The zoom literal 0 is stored RAW —
            // SetView has no zoom clamp (only SetViewPos FUN_00629fe0:297-307 clamps its own arg)
            // — so the port's target state must hold the raw 0 here. The GAMEPLAY consumption of
            // that 0 (snap + per-frame clamp to min zoom 1.0) is pinned separately below.
            Assert.That(camera.Focus.x, Is.EqualTo(42.5f));
            Assert.That(camera.Focus.y, Is.EqualTo(17.25f));
            Assert.That(camera.Focus.z, Is.Zero);
            Assert.That(camera.Zoom, Is.Zero);
            Assert.That(camera.Dx, Is.EqualTo(-0.35f).Within(0.00001f));
            Assert.That(camera.Dy, Is.Zero);
            Assert.That(camera.HasView, Is.True);
        }

        // The zoom a GAMEPLAY set_view target lands at, per the native consumer
        // CGameThread::Draw FUN_006b5d90 (":93 CGameThread::Draw if(m_fViewMoveToX>-1000.0f)"):
        // *:128-135 — moving flag false → live view = targets VERBATIM (zoom 0 included);
        // *:552-562 — same-frame live-zoom rewrite m_fZoom = min(max(m_fZoom,
        // DAT_00737138=1.0), DAT_007373d8=2.5) on the gameplay path (game+0x598 clear).
        // So the canonical news-click `set_view $x $y 0 -0.35 0` (baby.tcl:436,
        // z_events.tcl:2004, z_work_common.tcl:630, Skirmish.tcl:254) shows the newborn at the
        // gameplay MINIMUM zoom 1.0 — zero is neither "keep current" nor the cutscene 0.5 floor.
        [TestCase(0f, 1.0f, TestName = "GameplayZoom_ZeroTarget_LandsAtGameplayMin")]
        [TestCase(0.5f, 1.0f, TestName = "GameplayZoom_BelowMin_ClampsUpTo1")]
        [TestCase(1.0f, 1.0f, TestName = "GameplayZoom_AtMin_Passes")]
        [TestCase(1.478f, 1.478f, TestName = "GameplayZoom_CampaignInitial_PassesThrough")]
        [TestCase(2.5f, 2.5f, TestName = "GameplayZoom_AtMax_Passes")]
        [TestCase(6.0f, 2.5f, TestName = "GameplayZoom_CutsceneMax_ClampsDownTo2p5")]
        public void SetViewZoom_GameplayConsumption_UsesNativeDrawClamp(float target, float expected)
        {
            Assert.That(Diggles.Runtime.CutsceneCameraDriver.GameplayZoomFor(target),
                Is.EqualTo(expected).Within(0.00001f));
        }

        private static (string DataRoot, string FontPath) RequireFont(
            string directory,
            string fileName)
        {
            string dataRoot;
            try
            {
                dataRoot = OriginalDataTestFixture.DataRoot;
            }
            catch (Exception exception)
            {
                Assert.Ignore(exception.Message);
                return (string.Empty, string.Empty);
            }

            string fontPath = Path.Combine(
                dataRoot,
                "GUI",
                directory,
                fileName);
            if (!File.Exists(fontPath))
                Assert.Ignore($"KFT font was not found at '{fontPath}'.");
            return (dataRoot, fontPath);
        }
    }
}
