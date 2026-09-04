#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    public sealed class DocumentWindowSystemTests
    {
        private static (TclInterpreter Interp, DigglesTclContext Context) Host(string dataRoot = "")
        {
            var interpreter = new TclInterpreter
            {
                TolerateExprErrors = true,
                TolerateMissingVars = true,
            };
            var context = DigglesTclEngine.Register(interpreter);
            context.DataRoot = dataRoot;
            context.IsStoryMgr = true;
            return (interpreter, context);
        }

        [Test]
        public void LayoutCommand_ImplementsNativePrintAndLinkContract()
        {
            string directory = CreateDocumentRoot(out string documentDirectory);
            string previousLocale = DigglesText.Locale;
            try
            {
                DigglesText.SetLocale("en");
                File.WriteAllText(Path.Combine(documentDirectory, "page1.tcl"),
                    "layout clear\n" +
                    "set was_present [layout present]\n" +
                    "layout print {Hello /p}\n" +
                    "layout print [layout autolink page2.tcl Next page3.tcl]\n" +
                    "layout print [layout autoxlink {set left_exec yes} Run {set right_exec yes}]\n");
                File.WriteAllText(Path.Combine(documentDirectory, "page2.tcl"),
                    "layout clear; layout print {Page Two}");
                File.WriteAllText(Path.Combine(documentDirectory, "page3.tcl"),
                    "layout clear; layout print {Page Three}");

                var (interpreter, context) = Host(directory);
                Assert.That(interpreter.Eval("layout present"), Is.EqualTo("0"));
                Assert.Throws<TclException>(() => interpreter.Eval("layout print outside"));

                interpreter.Eval("textwin run page1.tcl");
                Assert.That(interpreter.Eval("set was_present"), Is.EqualTo("1"));
                Assert.That(context.Documents.IsOpen, Is.True);
                Assert.That(context.Documents.Visible, Is.True);
                Assert.That(context.Documents.Markup, Does.Contain("Hello /p"));
                Assert.That(context.Documents.Markup, Does.Contain("/(su1)Next/(su0)"));
                Assert.That(context.Documents.Markup, Does.Contain("/(su2)Run/(su0)"));
                Assert.That(context.Documents.TryGetLink(1, out var pageLink), Is.True);
                Assert.That(pageLink.Target, Is.EqualTo("page2.tcl"));
                Assert.That(pageLink.RightTarget, Is.EqualTo("page3.tcl"));
                Assert.That(pageLink.ExecutesTcl, Is.False);
                Assert.That(context.Documents.TryGetLink(2, out var executeLink), Is.True);
                Assert.That(executeLink.ExecutesTcl, Is.True);

                Assert.That(context.Documents.Click(2), Is.True);
                Assert.That(interpreter.Eval("set left_exec"), Is.EqualTo("yes"));
                Assert.That(context.Documents.Click(2, rightButton: true), Is.True);
                Assert.That(interpreter.Eval("set right_exec"), Is.EqualTo("yes"));

                Assert.That(context.Documents.Click(1, rightButton: true), Is.True);
                Assert.That(context.Documents.CurrentScript, Does.EndWith("page3.tcl"));
                Assert.That(context.Documents.Markup, Is.EqualTo("Page Three"));
                Assert.That(context.Documents.Links, Has.Count.EqualTo(1),
                    "clear restores the native link-zero sentinel and discards the prior page links");
            }
            finally
            {
                DigglesText.SetLocale(previousLocale);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void TextWindow_RunReloadInfoHideAndRetailDeflink_AreFunctional()
        {
            string directory = CreateDocumentRoot(out string documentDirectory);
            string previousLocale = DigglesText.Locale;
            try
            {
                DigglesText.SetLocale("en");
                File.WriteAllText(Path.Combine(documentDirectory, "reload.tcl"),
                    "layout clear\n" +
                    "incr render_count\n" +
                    "layout print $render_count\n" +
                    "textwin deflink 4 next.tcl\n");
                File.WriteAllText(Path.Combine(documentDirectory, "next.tcl"),
                    "layout clear; layout print next");
                File.WriteAllText(Path.Combine(documentDirectory, "diary_15.tcl"),
                    "layout clear; layout print quest-page");

                var (interpreter, context) = Host(directory);
                interpreter.Eval("set render_count 0; textwin run reload.tcl");
                Assert.That(context.Documents.Markup, Is.EqualTo("1"));
                Assert.That(context.Documents.TryGetLink(4, out var fixedLink), Is.True);
                Assert.That(fixedLink.Target, Is.EqualTo("next.tcl"));

                Assert.That(context.Documents.Click(4), Is.True);
                Assert.That(context.Documents.Markup, Is.EqualTo("next"));
                interpreter.Eval("textwin run reload.tcl");
                interpreter.Eval("set x [layout present]");
                Assert.That(interpreter.Eval("set x"), Is.EqualTo("0"));
                File.WriteAllText(Path.Combine(documentDirectory, "reload_link.tcl"),
                    "layout clear; layout print [layout autoxlink {layout reload} Reload]");
                interpreter.Eval("textwin run reload_link.tcl");
                Assert.That(context.Documents.Click(1), Is.True);

                interpreter.Eval("textwin info questlog");
                Assert.That(context.Documents.CurrentScript, Does.EndWith("diary_15.tcl"));
                Assert.That(context.Documents.Markup, Is.EqualTo("quest-page"));
                interpreter.Eval("textwin info");
                Assert.That(context.Documents.IsOpen, Is.False);
                interpreter.Eval("textwin info questlog");
                interpreter.Eval("textwin hide");
                Assert.That(context.Documents.IsOpen, Is.False);
                Assert.That(context.Documents.Visible, Is.False);
                Assert.That(context.Documents.Markup, Is.Empty);
            }
            finally
            {
                DigglesText.SetLocale(previousLocale);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void OnlineHelpProcedures_AreNotShadowedByNativeFallbacks()
        {
            var (interpreter, _) = Host();
            interpreter.Eval(
                "proc questlog {event headline text} {global quest_proc_called; set quest_proc_called yes}\n" +
                "proc paragraph {text} {global paragraph_proc_called; set paragraph_proc_called $text}\n" +
                "proc linkline {target text} {global linkline_proc_called; set linkline_proc_called $target}\n" +
                "proc ohlp_initstyle {} {global style_proc_called; set style_proc_called yes}\n" +
                "proc pickone {items} {global pickone_proc_called; set pickone_proc_called yes}");

            interpreter.Eval("questlog evt title body; paragraph body; linkline page label; ohlp_initstyle; pickone {a b}");
            Assert.That(interpreter.Eval("set quest_proc_called"), Is.EqualTo("yes"));
            Assert.That(interpreter.Eval("set paragraph_proc_called"), Is.EqualTo("body"));
            Assert.That(interpreter.Eval("set linkline_proc_called"), Is.EqualTo("page"));
            Assert.That(interpreter.Eval("set style_proc_called"), Is.EqualTo("yes"));
            Assert.That(interpreter.Eval("set pickone_proc_called"), Is.EqualTo("yes"));
        }

        [Test]
        public void MarkupParser_ConvertsFormattingLinksImagesAndParagraphs()
        {
            var (_, context) = Host();
            context.Stats.Define("exp_Test", 0.0, 2.0, 0.0, null);
            var testClass = new DigglesClass { Name = "DocumentTest", Kind = "gnome" };
            context.Classes.Add(testClass.Name, testClass);
            var entity = new DigglesTclEntity { Class = testClass };
            context.RegisterEntity(entity);
            context.Stats.Set(entity.Stats, "exp_Test", 1.0);
            var blocks = new List<DocumentRenderBlock>();
            DocumentMarkupParser.Parse(
                $"/(fn2,ac)Heading/p/(al,fn0)Body /(su1)Next/(su0) " +
                $"/(ccAttribbar {entity.Handle} exp_Test)/p/(is5)/(irdata/gui/docpix/Page.tga)",
                context,
                blocks);

            Assert.That(blocks, Has.Count.EqualTo(2));
            Assert.That(blocks[0].Kind, Is.EqualTo(DocumentRenderBlockKind.Text));
            Assert.That(blocks[0].RichText, Does.Contain("<size=27>"));
            Assert.That(blocks[0].RichText, Does.Contain("Heading\n"));
            Assert.That(blocks[0].RichText, Does.Contain("<link=\"1\"><color=#F0C85A><u>Next</u></color></link>"));
            Assert.That(blocks[0].RichText, Does.Contain("[#####-----]"));
            Assert.That(blocks[1].Kind, Is.EqualTo(DocumentRenderBlockKind.Image));
            Assert.That(blocks[1].ImagePath, Is.EqualTo("data/gui/docpix/Page.tga"));
            Assert.That(blocks[1].Alignment, Is.EqualTo(DocumentBlockAlignment.Right));
            Assert.That(blocks[1].Inset, Is.EqualTo(5f));
        }

        [Test]
        public void DetectLocale_UsesLocalizedTalkFileSuffix()
        {
            string dataRoot = Path.Combine(Path.GetTempPath(), "diggles-locale-" + Guid.NewGuid().ToString("N"));
            string talkDirectory = Path.Combine(dataRoot, "Scripts", "text", "talk");
            try
            {
                Directory.CreateDirectory(talkDirectory);
                File.WriteAllText(Path.Combine(talkDirectory, "commontalk_de.tcl"), string.Empty);

                Assert.That(DigglesText.DetectLocale(dataRoot), Is.EqualTo("de"));
            }
            finally
            {
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
            }
        }

        [Test]
        public void ShippedQuestLogScript_RendersOnlyActiveStoryEvents()
        {
            string dataRoot = OriginalDataTestFixture.DataRoot;
            string previousLocale = DigglesText.Locale;
            try
            {
                string locale = DigglesText.DetectLocale(dataRoot);
                DigglesText.SetLocale(string.IsNullOrEmpty(locale) ? "en" : locale);
                var (interpreter, context) = Host(dataRoot);
                context.StoryEvents["QL_Free_Gatekeeper"] = "1";
                context.StoryEvents["QL_Bring_Hamsters"] = "1";

                interpreter.Eval("textwin info questlog");

                Assert.That(context.Documents.IsOpen, Is.True);
                Assert.That(context.Documents.Markup.Length, Is.GreaterThan(80));
                Assert.That(interpreter.Eval("set quests"), Is.EqualTo("2"),
                    "the unchanged diary_15.tcl and onlinehelputils.tcl must filter against StoryMgr events");
            }
            finally
            {
                DigglesText.SetLocale(previousLocale);
            }
        }

        [Test]
        public void SaveState_RestoresDocumentAndValidatesBeforeMutation()
        {
            string dataRoot = CreateDocumentRoot(out string documentDirectory);
            string previousLocale = DigglesText.Locale;
            try
            {
                DigglesText.SetLocale("en");
                File.WriteAllText(Path.Combine(documentDirectory, "saved.tcl"),
                    "layout clear; layout print {Saved Page}; layout print [layout autolink next.tcl Next]");
                var (interpreter, context) = Host(dataRoot);
                interpreter.Eval("textwin run saved.tcl");
                DocumentWindowSystem.SaveState snapshot = context.Documents.Capture();

                context.Documents.Close();
                context.Documents.Restore(snapshot);
                Assert.That(context.Documents.IsOpen, Is.True);
                Assert.That(context.Documents.Visible, Is.True);
                Assert.That(context.Documents.Markup, Does.Contain("Saved Page"));
                Assert.That(context.Documents.TryGetLink(1, out var link), Is.True);
                Assert.That(link.Target, Is.EqualTo("next.tcl"));

                DocumentWindowSystem.SaveState malformed = context.Documents.Capture();
                malformed.IsOpen = false;
                malformed.Visible = true;
                interpreter.Eval("set validation_guard live");
                Assert.Throws<ArgumentException>(() => context.Documents.Restore(malformed));
                Assert.That(interpreter.Eval("set validation_guard"), Is.EqualTo("live"));

                DocumentWindowSystem.SaveState oversizedLink = context.Documents.Capture();
                oversizedLink.Links[0].Id = int.MaxValue;
                Assert.Throws<ArgumentException>(() => context.Documents.Restore(oversizedLink));
                Assert.That(interpreter.Eval("set validation_guard"), Is.EqualTo("live"));
            }
            finally
            {
                DigglesText.SetLocale(previousLocale);
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
            }
        }

        private static string CreateDocumentRoot(out string documentDirectory)
        {
            string root = Path.Combine(Path.GetTempPath(), "diggles-doc-window-" + Guid.NewGuid().ToString("N"));
            documentDirectory = Path.Combine(root, "Scripts", "text", "doc", "en");
            Directory.CreateDirectory(documentDirectory);
            return root;
        }
    }
}
