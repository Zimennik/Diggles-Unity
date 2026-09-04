#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class IntroCutsceneRepairTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        string _previousLocale = "en";

        // Mirror the runtime boot's locale resolve (DigglesConfig.ResolveLocale): the sequencer
        // opens soundmarker files through the Tcl `locale` command, and Steam data ships exactly
        // one locale's files — a ru install has no *_en.txt, so an un-set locale stalls every
        // voice window (and with it the no-skip e2e).
        [SetUp]
        public void SetUp()
        {
            _previousLocale = DigglesText.Locale;
            var detected = DigglesText.DetectLocale(DataRoot);
            if (!string.IsNullOrEmpty(detected)) DigglesText.SetLocale(detected);
        }

        [TearDown]
        public void TearDown() => DigglesText.SetLocale(_previousLocale);

        [Test]
        public void IntroCast_ResolvesConcreteModelControllers_IncludingKingFromClassAnimTable()
        {
            var root = new GameObject("IntroCast.ModelRoot");
            try
            {
                DigglesEngine engine = new DigglesEngineFactory().Create(
                    DataRoot,
                    root.transform,
                    "BlockDB2.BDB");
                engine.LoadRuntimeDefinitions();

                DigglesClass kingClass = engine.Ctx.GetClassOrThrow("Koenig_im_Bett");
                Assert.That(engine.Lib.ResolveClass("Koenig_im_Bett"), Is.Null,
                    "the shipped runtime TSV deliberately has no king row; this must exercise the Tcl fallback");
                Assert.That(string.IsNullOrEmpty(kingClass.DefaultAnim), Is.True,
                    "actors.tcl:503-527 declares no class_defaultanim for the king");
                Assert.That(kingClass.AnimMap["kingwakeupstart"],
                    Is.EqualTo("mann.koenig_bett_wecken_start"));

                var cast = new[]
                {
                    (ClassName: "Koenig_im_Bett", ObjName: "mann", RealClass: true),
                    (ClassName: "Hand_Gottes", ObjName: "odinshand", RealClass: true),
                    (ClassName: "Odin", ObjName: "odin", RealClass: true),
                    (ClassName: "Fifi", ObjName: "fifi", RealClass: true),
                    (ClassName: "Dummy_Koenigsschlafmuetze", ObjName: "koenigsschlafmuetze", RealClass: false),
                    (ClassName: "Riesenelfe", ObjName: "riesenelfe", RealClass: true),
                    (ClassName: "Dummy_L1", ObjName: "l1", RealClass: false),
                };
                var spawner = (RuntimeWorldSpawner)engine.Ctx.EntitySpawner!;

                for (int index = 0; index < cast.Length; index++)
                {
                    var expected = cast[index];
                    bool found = engine.Ctx.Classes.TryGetValue(expected.ClassName, out DigglesClass? cls);
                    if (expected.RealClass)
                        Assert.That(found, Is.True,
                            $"the shipped class declaration for {expected.ClassName} did not load");
                    cls ??= new DigglesClass { Name = expected.ClassName, Kind = "dummy" };

                    var entity = new DigglesTclEntity
                    {
                        Handle = 10000 + index,
                        Class = cls,
                        CurrentAnim = null,
                    };
                    GameObject? spawned = spawner.Spawn(entity, root.transform);

                    Assert.That(spawned, Is.Not.Null, $"{expected.ClassName} did not spawn");
                    DigglesAnimController? controller =
                        spawned!.GetComponentInChildren<DigglesAnimController>();
                    Assert.That(controller, Is.Not.Null,
                        $"{expected.ClassName} fell through to the wireframe placeholder");
                    Assert.That(controller!.ObjName, Is.EqualTo(expected.ObjName),
                        $"{expected.ClassName} resolved the wrong 3db object");

                    UnityEngine.Object.DestroyImmediate(spawned);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AnimAction_WithoutAnimator_CompletesOnPointOneSecondFallbackClock()
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            var sim = new DigglesGameSim(context);
            interpreter.Eval("def_class PlaceholderActor none gnome 0 {} { obj_init {} }");
            string handle = interpreter.Eval(
                "new PlaceholderActor \"\" \"0 0 0\" \"0 0 0\"");
            DigglesTclEntity entity = context.GetEntity(handle)!;

            interpreter.Eval($"action {handle} anim kingwakeupstart");
            Assert.That(entity.ActiveAction, Is.Not.Null);

            sim.Step(0.099);
            Assert.That(entity.ActiveAction, Is.Not.Null,
                "the no-controller action finished before the native-compatible fallback deadline");

            sim.Step(0.002);
            Assert.That(entity.ActiveAction, Is.Null,
                "a placeholder actor's animation action wedged the sequencer gate");
        }

        [Test]
        public void Odin001_NoSkip_CrossesKingWakeupAndFirstExplanation_ThenEndsNaturally()
        {
            var root = new GameObject("Odin001.HeadlessRoot");
            try
            {
                DigglesEngine engine = new DigglesEngineFactory().Create(
                    DataRoot,
                    root.transform,
                    "BlockDB2.BDB");
                engine.LoadRuntimeDefinitions();
                string bootPath = Path.Combine(DataRoot, "GameScripts", "single_Starttut.tcl");
                engine.Interp.Eval(DigglesText.ReadAllText(bootPath));

                DigglesTclEntity king = engine.Ctx.Entities.Values.Single(
                    entity => entity.Class?.Name == "Koenig_im_Bett");
                DigglesAnimController? kingController =
                    king.GameObject?.GetComponentInChildren<DigglesAnimController>();
                Assert.That(kingController, Is.Not.Null);
                Assert.That(kingController!.ObjName, Is.EqualTo("mann"));

                bool sequenceActivated = false;
                bool sawKingWakeupStart = false;
                bool sawFirstExplanation = false;
                bool endedNaturally = false;
                int steps = 0;

                for (; steps < 7000 && !endedNaturally; steps++)
                {
                    engine.Sim.Step(0.05);
                    sequenceActivated |= engine.Ctx.SequenceActive;
                    sawKingWakeupStart |= king.CurrentAnim == "kingwakeupstart";
                    sawFirstExplanation |= sawKingWakeupStart && engine.Ctx.Audio!.VoicePlayCount > 0;
                    endedNaturally = sequenceActivated && sawFirstExplanation && !engine.Ctx.SequenceActive;
                }

                Assert.That(engine.Ctx.SequenceBreaked, Is.False,
                    "the regression must exercise natural playback, not the skip drain");
                Assert.That(sequenceActivated, Is.True, "Trigger_Auftrag_Odin never activated odin_001");
                Assert.That(sawKingWakeupStart, Is.True,
                    "odin_001 did not cross line 103: set_anim Actor0 kingwakeupstart");
                Assert.That(sawFirstExplanation, Is.True,
                    "odin_001 did not advance beyond the wakeup beat to line 115's first voice segment");
                Assert.That(endedNaturally, Is.True,
                    $"odin_001 did not shut itself down within {steps * 0.05:F1} sim seconds");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Odin001_AllAuthoredVoiceMarkers_ArePositiveAbsoluteMediaWindows()
        {
            // Steam ships exactly one locale's soundmarker files (a ru install has only
            // Obw_Odin001_ru.txt) — resolve whatever locale the data carries; the window law
            // (positive absolute [start,stop]) holds for every localization.
            string markerDir = Path.Combine(DataRoot, "Scripts", "sequences", "soundmarker");
            string[] markerFiles = Directory.GetFiles(markerDir, "Obw_Odin001_*.txt");
            Assert.That(markerFiles, Is.Not.Empty, $"no Obw_Odin001 markers under {markerDir}");
            string markerPath = markerFiles[0];
            var markers = new Dictionary<string, (float Start, float Stop)>(StringComparer.Ordinal);
            foreach (string rawLine in File.ReadAllLines(markerPath))
            {
                string[] fields = rawLine.Split(
                    new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 || !fields[0].StartsWith("001", StringComparison.Ordinal))
                    continue;
                markers.Add(
                    fields[0],
                    (float.Parse(fields[1], CultureInfo.InvariantCulture),
                     float.Parse(fields[2], CultureInfo.InvariantCulture)));
            }

            Assert.That(markers.Count, Is.EqualTo(28));
            foreach (KeyValuePair<string, (float Start, float Stop)> marker in markers)
            {
                float duration = marker.Value.Stop - marker.Value.Start;
                Assert.That(duration, Is.GreaterThan(0f), $"{marker.Key} has an empty/reversed window");
                Assert.That(
                    AudioState.VoiceWindowRemaining(
                        marker.Value.Start,
                        marker.Value.Stop,
                        marker.Value.Start),
                    Is.EqualTo(duration).Within(0.00001f),
                    $"{marker.Key} did not preserve stop-start");
                Assert.That(
                    AudioState.VoiceWindowRemaining(
                        marker.Value.Start,
                        marker.Value.Stop,
                        marker.Value.Stop),
                    Is.Zero,
                    $"{marker.Key} did not close at its absolute stop position");
            }

            string sequencePath = Path.Combine(
                DataRoot,
                "Scripts",
                "sequences",
                "Oberwelt",
                "odin_001.tcl");
            string[] speechLines = File.ReadAllLines(sequencePath)
                .Where(line => line.TrimStart().StartsWith("do_text ", StringComparison.Ordinal))
                .ToArray();
            Assert.That(speechLines.Length, Is.EqualTo(27));
            foreach (string speechLine in speechLines)
            {
                Match match = Regex.Match(speechLine, @"\b001[a-z]+\b", RegexOptions.CultureInvariant);
                Assert.That(match.Success, Is.True, $"speech line has no marker id: {speechLine}");
                Assert.That(markers.ContainsKey(match.Value), Is.True,
                    $"odin_001 references missing audio marker {match.Value}");
            }

            // Marker timings are per-locale (each localization is its own recording): pin the
            // exact 001a window only when the data carries the en file this pin was read from.
            if (markerPath.EndsWith("_en.txt", StringComparison.OrdinalIgnoreCase))
            {
                (float start, float stop) = markers["001a"];
                Assert.That(start, Is.EqualTo(55.453804f).Within(0.00001f));
                Assert.That(stop, Is.EqualTo(58.299623f).Within(0.00001f));
            }
        }

        [Test]
        public void VoiceWindow_UsesHeadlessClockUntilPlayback_ThenMediaClockDrivesSubtitle()
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            var sim = new DigglesGameSim(context);
            AudioState audio = context.Audio!;
            DialogueState dialogue = context.Dialogue!;
            const float start = 55.453804f;
            const float stop = 58.299623f;
            float duration = stop - start;
            string durationText = duration.ToString("R", CultureInfo.InvariantCulture);

            interpreter.Eval("seq_audiostream open clip0001.mp3");
            interpreter.Eval("seq_audiostream play 55.453804 58.299623");
            interpreter.Eval(
                $"set_speechout {durationText} \"{{255 255 255}} {{Odin explains}}\" 0");

            sim.Step(0.5);
            Assert.That(audio.VoiceRemaining,
                Is.EqualTo(duration - 0.5f).Within(0.0001f));
            Assert.That(dialogue.SpeechRemaining,
                Is.EqualTo(audio.VoiceRemaining).Within(0.0001f));

            Assert.That(audio.MarkVoiceStarted(start), Is.True);
            Assert.That(audio.VoiceRemaining, Is.EqualTo(duration).Within(0.0001f));
            Assert.That(dialogue.SpeechRemaining,
                Is.EqualTo(audio.VoiceRemaining).Within(0.0001f));

            sim.Step(duration + 1f);
            Assert.That(audio.VoiceRemaining, Is.EqualTo(duration).Within(0.0001f),
                "the sim clock cut a presenter-owned media window short");
            Assert.That(dialogue.SpeechRemaining,
                Is.EqualTo(audio.VoiceRemaining).Within(0.0001f),
                "the subtitle escaped the presenter-owned voice window");

            audio.UpdateVoicePlaybackPosition(stop - 0.25f);
            Assert.That(audio.VoiceRemaining, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(dialogue.SpeechRemaining, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(dialogue.CurrentSpeechText(), Does.Contain("Odin explains"));

            audio.UpdateVoicePlaybackPosition(stop);
            Assert.That(audio.VoicePlaying, Is.False);
            Assert.That(audio.VoiceRemaining, Is.Zero);
            Assert.That(dialogue.SpeechRemaining, Is.Zero);
            Assert.That(dialogue.CurrentSpeechText(), Is.Empty);
        }

        [Test]
        public void VoiceWindow_HeadlessFailureAndSequenceTeardown_BothDrainWithoutWedge()
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            var sim = new DigglesGameSim(context);
            AudioState audio = context.Audio!;
            DialogueState dialogue = context.Dialogue!;

            interpreter.Eval("seq_audiostream open clip0001.mp3");
            interpreter.Eval("seq_audiostream play 3.594123 6.020440");
            interpreter.Eval(
                "set_speechout 2.426317 \"{255 255 255} {headless line}\" 0");
            sim.Step(2.5);
            Assert.That(audio.VoicePlaying, Is.False,
                "a missing presenter left the headless audio window open");
            Assert.That(dialogue.SpeechRemaining, Is.Zero);

            interpreter.Eval("set_sequence 1");
            interpreter.Eval("seq_audiostream open clip0001.mp3");
            interpreter.Eval("seq_audiostream play 55.453804 58.299623");
            interpreter.Eval(
                "set_speechout 2.845819 \"{255 255 255} {skip drain line}\" 0");
            Assert.That(audio.MarkVoiceStarted(55.453804f), Is.True);

            interpreter.Eval("set_sequence 0");
            Assert.That(audio.VoicePlaying, Is.False);
            Assert.That(audio.VoiceFile, Is.Null);
            Assert.That(dialogue.SpeechRemaining, Is.Zero);
            Assert.That(dialogue.CurrentSpeechText(), Is.Empty);
        }
    }
}
