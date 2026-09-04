#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Cutscene / cinematic sequencer tests. The sequencer logic is 100% the original
    // Tcl (classes/story/sequencer.tcl); we implement the native LEAF primitives. These
    // tests drive the REAL Tcl through our engine and assert behaviour. See
    // Docs/CUTSCENE_SYSTEM.md.
    //
    // FOUNDATION (task #1): file_wrapper.tcl line-by-line file I/O (open/gets/eof/close in
    // pure Tcl), and that a Sequencer trigger class loads + instantiates.
    public sealed class CutsceneSequencerTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        static string Script(string rel) => Path.Combine(DataRoot, "Scripts", rel);
        static string LoadLatin1(string path)
        {
            Assert.IsTrue(File.Exists(path), $"missing file: {path}");
            return File.ReadAllText(path, Encoding.GetEncoding("ISO-8859-1"));
        }

        // Engine host with the real data root + an unknown-command recorder (so a leaf we
        // haven't implemented yet is a recorded no-op rather than a throw). The recorder list
        // is the "still-stubbed leaves" shopping list we drive toward empty.
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim, List<string> unknown) BuildHost()
        {
            var interp = new TclInterpreter();
            interp.TolerateExprErrors = true;
            interp.TolerateMissingVars = true;
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            // NOTE: TolerateMissingFiles is intentionally OFF — the sequencer's parse_file relies
            // on `open` (file_wrapper) failing (load_file throwing) for missing scripts.
            var sim = new DigglesGameSim(ctx);
            var unknown = new List<string>();
            interp.UnknownCommandHandler = (i, a) => { unknown.Add(a[0]); return "0"; };
            interp.RegisterCommand("puts", (i, a) => "");
            return (interp, ctx, sim, unknown);
        }

        // ---- file_wrapper.tcl: pure-Tcl open/gets/eof/close over load_file ----

        [Test]
        public void FileWrapper_ReadsRealScriptLineByLine()
        {
            var (interp, _, _, _) = BuildHost();
            // Eval'd outside a class def → defines open/close/gets/eof procs (in_class_def=0).
            interp.Eval(LoadLatin1(Script("misc/file_wrapper.tcl")));
            Assert.IsTrue(interp.ProcExists("open"), "file_wrapper should define proc open");
            Assert.IsTrue(interp.ProcExists("gets"), "file_wrapper should define proc gets");

            // Read Gespraech.tcl (a real cutscene, ~39 lines) line by line.
            string n = interp.Eval(@"
                set f [open data/scripts/sequences/Test/Gespraech.tcl]
                set count 0
                set sawcam 0
                while {![eof $f]} {
                    gets $f line
                    set line [string trim $line]
                    if {[string length $line] > 0} { incr count }
                    if {[string equal -length 9 ""sq_camera"" $line]} { incr sawcam }
                }
                close $f
                list $count $sawcam");
            var parts = n.Split(' ');
            Assert.That(int.Parse(parts[0]), Is.GreaterThan(20), "should read many non-blank lines");
            Assert.That(int.Parse(parts[1]), Is.GreaterThanOrEqualTo(2), "Gespraech has >=2 sq_camera lines");
        }

        [Test]
        public void FileWrapper_OpenMissingFile_ReturnsZero()
        {
            var (interp, _, _, _) = BuildHost();
            interp.Eval(LoadLatin1(Script("misc/file_wrapper.tcl")));
            string r = interp.Eval("open data/scripts/sequences/Test/does_not_exist_xyz.tcl");
            Assert.AreEqual("0", r.Trim(), "open of a missing file returns 0 (per file_wrapper)");
        }

        // ---- sequencer.tcl: class definition + instantiation ----

        // A trigger class that pulls in the sequencer exactly like the game's Sequence_instant
        // (sequencer.tcl is dual-loaded: once in the class def for methods/states, once in
        // obj_init for the procs + member init).
        const string TestSeqClass = @"
            def_class TestSeq none dummy 0 {} {
                call scripts/classes/story/sequencer.tcl
                obj_init {
                    call scripts/classes/story/sequencer.tcl
                }
                method run {} { sequencer_activate }
            }";

        // A minimal stand-in actor (no model/nav) so actor-targeted sequencer commands
        // (do_action/do_text → action verbs) operate on real handles and degrade gracefully.
        const string TestActorClass = @"def_class TestActor none dummy 0 {} { obj_init {} }";

        [Test]
        public void Sequencer_ClassLoads_DefinesProcs_And_Members()
        {
            var (interp, ctx, _, _) = BuildHost();
            interp.Eval(TestSeqClass);
            interp.Eval(@"set seq [new TestSeq """" ""0 0 0"" ""0 0 0""]");

            // The DSL procs are now real (un-shadowed): defined by sequencer.tcl's else branch.
            Assert.IsTrue(interp.ProcExists("sq_camera"), "sq_camera proc should exist");
            Assert.IsTrue(interp.ProcExists("do_text"), "do_text proc should exist");
            Assert.IsTrue(interp.ProcExists("parse_next"), "parse_next proc should exist");

            // Member vars initialised in the trigger's own scope by obj_init.
            int h = int.Parse(interp.Eval("set seq"));
            var ent = ctx.GetEntity(h.ToString());
            Assert.IsNotNull(ent, "trigger entity should exist");
            Assert.IsTrue(ent!.VarsScope.Vars.ContainsKey("actors"), "trigger should have member 'actors'");
            Assert.IsTrue(ent.VarsScope.Vars.ContainsKey("sequencescript"), "trigger should have 'sequencescript'");
        }

        [Test]
        public void Sequencer_SetSequenceScript_StoresPath()
        {
            var (interp, ctx, _, _) = BuildHost();
            interp.Eval(TestSeqClass);
            interp.Eval(@"set seq [new TestSeq """" ""0 0 0"" ""0 0 0""]");
            interp.Eval(@"call_method $seq set_sequencescript Gespraech");
            int h = int.Parse(interp.Eval("set seq"));
            var ent = ctx.GetEntity(h.ToString())!;
            Assert.AreEqual("Gespraech", ent.VarsScope.Vars["sequencescript"]);
        }

        // ---- end-to-end: drive a real cutscene through the eval_sequence state machine ----

        // Activates a sequencer on Gespraech.tcl (camera + walk + rotate + multi-actor dialogue)
        // with 3 preset stand-in actors, then ticks the sim. The sequencer must read the script
        // line-by-line, honour the do_-blocking + wait gates, progress to EOF, and shut itself
        // down (shut_sequencer → del this). Leaf primitives not yet implemented are recorded
        // (the shopping list for tasks #2-#7); the state machine + flow must work regardless.
        [Test]
        public void Sequencer_DrivesGespraech_ToCompletion()
        {
            var (interp, ctx, sim, unknown) = BuildHost();
            interp.Eval(TestActorClass);
            interp.Eval(TestSeqClass);
            interp.Eval(@"
                set seq [new TestSeq """" ""0 0 0"" ""0 0 0""]
                set a0 [new TestActor """" ""0 0 0"" ""0 0 0""]
                set a1 [new TestActor """" ""1 0 0"" ""0 0 0""]
                set a2 [new TestActor """" ""2 0 0"" ""0 0 0""]
                call_method $seq preset_actors [list $a0 $a1 $a2]
                call_method $seq set_sequencescript Gespraech
                call_method $seq run");

            int seqH = int.Parse(interp.Eval("set seq"));
            var seqEnt = ctx.GetEntity(seqH.ToString());
            Assert.IsNotNull(seqEnt, "sequencer should be live right after activate");

            // "Finished" = shut_sequencer ran (it does `incr sequence_counter`), OR the entity
            // was removed by its `del this`. sequence_counter is the faithful engine signal.
            bool Finished()
            {
                var e = ctx.GetEntity(seqH.ToString());
                if (e == null) return true;
                return e.VarsScope.Vars.TryGetValue("sequence_counter", out var sc) && sc != "0" && sc != "";
            }

            bool done = false;
            int steps = 0;
            for (; steps < 6000 && !done; steps++)
            {
                sim.Step(0.05);
                if (Finished()) done = true;
            }

            string seen = unknown.Count == 0 ? "(none)" : string.Join(", ", unknown.Distinct().OrderBy(x => x));
            string diag = "";
            if (!done)
            {
                var ent = ctx.GetEntity(seqH.ToString());
                if (ent != null)
                {
                    var v = ent.VarsScope.Vars;
                    string Get(string k) => v.TryGetValue(k, out var s) ? s : "<none>";
                    string fileH = Get("file");
                    // file seek/length live as globals named off the handle (FW_FileSeek_N / FW_FileLength_N)
                    string seek = "<?>", len = "<?>";
                    if (fileH.StartsWith("FW_File_"))
                    {
                        string suf = fileH.Substring("FW_File_".Length);
                        seek = v.TryGetValue("FW_FileSeek_" + suf, out var sk) ? sk : "<g?>";
                        len = v.TryGetValue("FW_FileLength_" + suf, out var ln) ? ln : "<g?>";
                    }
                    diag = $" | state={ent.CurrentState} enCnt={ent.StateEnabledCount} act={(ent.ActiveAction != null)} " +
                           $"seqCounter={Get("sequence_counter")} file={fileH} seek={seek}/{len} wait_time_var={Get("wait_time_var")} " +
                           $"wait_camera={Get("wait_camera")} wait_forelf={Get("wait_forelf")} enable_elf={Get("enable_elf")} " +
                           $"enable_list=({Get("enable_list")}) wait_for=({Get("wait_for")}) actors=({Get("actors")})";
                }
                else diag = " | trigger entity already gone (?)";
            }
            Assert.IsTrue(done,
                $"sequencer did not finish within {steps} steps ({sim.GameTime:F1}s). Unimplemented leaves seen: {seen}{diag}");
            TestContext.WriteLine($"Gespraech completed in {steps} steps ({sim.GameTime:F1}s). Unimplemented leaves: {seen}");
        }

        // ---- camera primitives (task #2) ----

        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        [Test]
        public void Camera_SetViewpos_Snap_SetsFocusAndZoom()
        {
            var (interp, ctx, _, _) = BuildHost();
            interp.Eval("set_viewpos 5 1 3 2 0 0 0 0 1");   // move=0 → snap
            var c = ctx.Camera!;
            Assert.AreEqual(5f, c.Focus.x, 1e-3f);
            Assert.AreEqual(1f, c.Focus.y, 1e-3f);
            Assert.AreEqual(3f, c.Focus.z, 1e-3f);
            Assert.AreEqual(2f, c.Zoom, 1e-3f);
            Assert.IsFalse(c.Moving, "a snap is not 'moving'");
            Assert.AreEqual("0", interp.Eval("get_cameramoving"));
        }

        [Test]
        public void Camera_SetViewpos_Move_AnimatesThenArrives()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval("set_viewpos 10 0 0 1 0 0 1 0 5");  // move=1, speed 5
            Assert.AreEqual("1", interp.Eval("get_cameramoving"), "moving right after an animated move");
            for (int i = 0; i < 400 && ctx.Camera!.Moving; i++) sim.Step(0.05);
            Assert.IsFalse(ctx.Camera!.Moving, "camera should reach its target");
            Assert.AreEqual(10f, ctx.Camera.Focus.x, 0.05f);
        }

        [Test]
        public void Camera_Follow_TracksEntityPosition()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(TestActorClass);
            interp.Eval(@"set a [new TestActor """" ""8 0 6"" ""0 0 0""]");
            interp.Eval(@"set_camerafollow $a 1.5");
            for (int i = 0; i < 100; i++) sim.Step(0.05);
            Assert.AreEqual(8f, ctx.Camera!.Focus.x, 0.1f);
            Assert.AreEqual(6f, ctx.Camera.Focus.z, 0.1f);
            Assert.That(F(interp.Eval("get_camerafollowdist")), Is.LessThan(0.5f));
            interp.Eval("set_camerafollow -1");
            Assert.AreEqual(-1, ctx.Camera.FollowHandle, "follow stops on -1");
        }

        [Test]
        public void Camera_GetView_RoundTripsThroughSetView()
        {
            var (interp, ctx, _, _) = BuildHost();
            interp.Eval("set_viewpos 4 2 7 3 0 0 0");        // snap to focus (4,2,7) zoom 3
            interp.Eval("set vp [get_view]");
            interp.Eval("set_viewpos 0 0 0 1 0 0 0");        // move camera away (snap)
            interp.Eval("eval \"set_view $vp\"");            // restore from saved view
            var c = ctx.Camera!;
            Assert.AreEqual(4f, c.Focus.x, 1e-2f);
            Assert.AreEqual(2f, c.Focus.y, 1e-2f);
            Assert.AreEqual(3f, c.Zoom, 1e-2f);
        }

        // ErsterKontakt.tcl:298-307 ends with an animated move followed by `sq_camera get`.
        // A skip drains those commands without animation ticks; natural playback settles the move first.
        [Test]
        public void SequencerSkipCameraTail_RestoresNaturalEndPose_AndNextSequenceSeedsFromIt()
        {
            var naturalEnd = RunScriptedCameraTail(skip: false);
            var skippedEnd = RunScriptedCameraTail(skip: true);

            Assert.AreEqual(64f, naturalEnd.Focus.x, 1e-3f);
            Assert.AreEqual(32f, naturalEnd.Focus.y, 1e-3f);
            Assert.AreEqual(1.7f, naturalEnd.Zoom, 1e-3f);
            Assert.AreEqual(-0.25f, naturalEnd.Dx, 1e-3f);
            Assert.AreEqual(0.4f, naturalEnd.Dy, 1e-3f);
            Assert.AreEqual(naturalEnd.Focus.x, skippedEnd.Focus.x, 1e-3f);
            Assert.AreEqual(naturalEnd.Focus.y, skippedEnd.Focus.y, 1e-3f);
            Assert.AreEqual(naturalEnd.Zoom, skippedEnd.Zoom, 1e-3f);
            Assert.AreEqual(naturalEnd.Dx, skippedEnd.Dx, 1e-3f);
            Assert.AreEqual(naturalEnd.Dy, skippedEnd.Dy, 1e-3f);

            skippedEnd.SeedPose(7f, 9f, 1.2f, -0.1f, 0.2f);
            Assert.AreEqual(7f, skippedEnd.Focus.x, 1e-3f);
            Assert.AreEqual(9f, skippedEnd.Focus.y, 1e-3f);
            Assert.AreEqual(1.2f, skippedEnd.Zoom, 1e-3f);
            Assert.AreEqual(-0.1f, skippedEnd.Dx, 1e-3f);
            Assert.AreEqual(0.2f, skippedEnd.Dy, 1e-3f);
            Assert.IsFalse(skippedEnd.Moving);
        }

        private static CutsceneCameraState RunScriptedCameraTail(bool skip)
        {
            var (interp, ctx, sim, _) = BuildHost();
            var camera = ctx.Camera!;
            interp.Eval("set_view 12 18 1.1 -0.1 -0.2");
            interp.Eval("set_sequence 1");
            interp.Eval("set_view 64 32 1.7 -0.25 0.4 1 0 0.25");

            if (skip)
            {
                ctx.SequenceBreaked = true;
            }
            else
            {
                for (int i = 0; i < 400 && camera.Moving; i++) sim.Step(0.05);
                Assert.IsFalse(camera.Moving, "natural-end fixture camera did not settle");
            }

            string finalView = interp.Eval("get_view 1");
            interp.Eval("set_sequence 0");
            interp.Eval($"set_view {finalView}");
            Assert.IsFalse(ctx.SequenceBreaked, "sequence teardown must clear the break flag");
            return camera;
        }

        // The "2nd-cutscene camera fly-in" law. Native keeps ONE camera object shared by gameplay and
        // cutscenes: set_sequence (FUN_00649080) never resets it, and a `move` op interpolates FROM the
        // live view (FUN_00629fe0:478-525 copies the live fields +0x154.. into the move's FROM slots).
        // The port SPLIT that into CutsceneCameraState (cutscene) + DigglesCameraController (gameplay),
        // so at a 2nd sequence's start the cutscene camera still holds the PRIOR sequence's leftover
        // Focus → its first `sq_camera move` (ZweiterKontakt/Urw_045/clip_009/urw_22/wusch all open with
        // `move`) glides in from that stale point. The driver re-seeds the cutscene camera from the live
        // gameplay pose at the sequence-start edge (CutsceneCameraState.SeedPose); this pins that seam.
        [Test]
        public void SequenceStart_SeedPose_MoveGlidesFromLivePose_NotStaleLeftover()
        {
            var (interp, ctx, sim, _) = BuildHost();
            var c = ctx.Camera!;

            // A prior sequence left the cutscene camera at a far leftover pose L (e.g. a close-up).
            interp.Eval("set_viewpos 300 300 0 2 0 0 0");   // snap → Focus = L (300,300)
            Assert.AreEqual(300f, c.Focus.x, 1e-3f, "leftover Focus from the prior sequence");

            // Sequence start: the driver re-seeds the cutscene camera from the LIVE gameplay pose G.
            c.SeedPose(10f, 10f, 1f, 0f, 0f);               // G = (10,10)
            Assert.AreEqual(10f, c.Focus.x, 1e-3f, "seed replaces the stale leftover Focus");
            Assert.AreEqual(10f, c.Focus.y, 1e-3f);
            Assert.IsFalse(c.Moving, "seeding is not a move");

            // First camera op of the new sequence: an animated move to target T=(20,20).
            interp.Eval("set_viewpos 20 20 0 1 0 0 1 0 1");  // move=1, speed 1
            sim.Step(0.05);                                   // one small tick — just started gliding

            // It must glide from the SEEDED pose G (~10→20), never teleport toward the stale L (300).
            Assert.That(c.Focus.x, Is.GreaterThan(9f).And.LessThan(14f),
                $"first move must glide from the seeded gameplay pose (~10), got {c.Focus.x}");
            Assert.That(c.Focus.x, Is.LessThan(50f),
                $"the fly-in bug: gliding from the stale leftover (300) instead of the seed — Focus.x={c.Focus.x}");
        }

        // ---- dialogue / subtitles (task #3) ----

        [Test]
        public void Dialogue_SetSpeechout_SetsLinesColorsAndTimer()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval("set_speechout 3 {{255 0 0} {Hello} {0 255 0} {World}}");
            var d = ctx.Dialogue!;
            Assert.AreEqual("Hello", d.Lines[0].Text);
            Assert.AreEqual(255, d.Lines[0].Color.r);
            Assert.AreEqual("World", d.Lines[1].Text);
            Assert.AreEqual(255, d.Lines[1].Color.g);
            Assert.That(d.SpeechRemaining, Is.EqualTo(3f).Within(1e-3f));
            Assert.That(d.CurrentSpeechText(), Does.Contain("Hello").And.Contain("World"));
            for (int i = 0; i < 80; i++) sim.Step(0.05);   // 4s > 3s
            Assert.AreEqual(0f, d.SpeechRemaining, 1e-3f);
            Assert.AreEqual("", d.Lines[0].Text, "speech clears when its timer elapses");
        }

        [Test]
        public void Dialogue_CalcTextTime_ScalesWithLength()
        {
            var (interp, _, _, _) = BuildHost();
            float shortT = F(interp.Eval("calc_text_time Hi"));
            float longT = F(interp.Eval(@"calc_text_time ""This is a much longer line of dialogue."""));
            Assert.That(longT, Is.GreaterThan(shortT));
            Assert.That(shortT, Is.GreaterThan(0f));
        }

        [Test]
        public void Dialogue_OptionAndLocale()
        {
            var (interp, _, _, _) = BuildHost();
            Assert.AreEqual("5", interp.Eval("option get TextSpeed"));
            interp.Eval("option set TextSpeed 8");
            Assert.AreEqual("8", interp.Eval("option get TextSpeed"));
            Assert.AreEqual("en", interp.Eval("locale"));
        }

        [Test]
        public void Dialogue_SpeechIcon_AddExpire()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(@"speechicon 5 add ""hi"" 2");
            Assert.IsTrue(ctx.Dialogue!.Bubbles.ContainsKey(5));
            for (int i = 0; i < 10; i++) sim.Step(0.05);   // 0.5s
            Assert.IsTrue(ctx.Dialogue.Bubbles.ContainsKey(5), "bubble still up before its time");
            for (int i = 0; i < 40; i++) sim.Step(0.05);   // +2s → expired
            Assert.IsFalse(ctx.Dialogue.Bubbles.ContainsKey(5), "bubble expires after its time");
        }

        [Test]
        public void Dialogue_ParseText_LoadsRealTextId()
        {
            string previousLocale = Diggles.Runtime.DigglesText.Locale;
            try
            {
                Diggles.Runtime.DigglesText.SetLocale(
                    Diggles.Runtime.DigglesText.DetectLocale(DataRoot));
                var (interp, _, _, _) = BuildHost();
                interp.Eval(TestSeqClass);
                interp.Eval(@"new TestSeq """" ""0 0 0"" ""0 0 0""");   // defines parse_text + file_wrapper procs
                interp.Eval("set textfile Tutorial; set known_vars {}");
                string t = interp.Eval("parse_text 1100a");
                Assert.IsNotEmpty(t.Trim().Trim('"'), "the locale-specific Tutorial text must load");
                Assert.That(t.ToLowerInvariant(), Does.Not.Contain("not found"),
                    $"expected the locale-specific 1100a line, got: {t}");
            }
            finally
            {
                Diggles.Runtime.DigglesText.SetLocale(previousLocale);
            }
        }

        // ---- audio (task #4) ----

        [Test]
        public void Audio_SoundBindAndPlay_RecordsOneShot()
        {
            var (interp, ctx, _, _) = BuildHost();
            // bind validates the file exists (native fopen check, FUN_00643a60:250-272) — this
            // host runs strict (TolerateMissingFiles off) against the real data root, so bind a
            // REAL sample (soundinit.tcl:92 binds this very file).
            interp.Eval("sound bind step data/sound/kiste_ablegen.wav");
            Assert.AreEqual("data/sound/kiste_ablegen.wav", ctx.Audio!.ResolveSound("step"));
            interp.Eval("sound play step 0.8");
            Assert.AreEqual(1, ctx.Audio.OneShotCount);
            Assert.AreEqual("step", ctx.Audio.LastOneShot!.Value.Name);
            Assert.AreEqual(0.8f, ctx.Audio.LastOneShot.Value.Volume, 1e-3f);
        }

        [Test]
        public void Audio_AdaptiveSound_ThemeAndPlayback()
        {
            var (interp, ctx, _, _) = BuildHost();
            interp.Eval("adaptive_sound setintrostream cave cave_intro.mp3");
            interp.Eval("adaptive_sound addstandardstream cave cave_1.mp3");
            interp.Eval("adaptive_sound marker cave {3 0 5}");
            interp.Eval("adaptive_sound changethemenow cave");
            var au = ctx.Audio!;
            Assert.AreEqual("cave", au.CurrentTheme);
            Assert.IsTrue(au.MusicPlaying);
            Assert.AreEqual("cave_intro.mp3", au.Themes["cave"].Intro);
            Assert.AreEqual(1, au.Themes["cave"].Standard.Count);
            Assert.AreEqual(1, au.MusicMarkers.Count);
        }

        [Test]
        public void Audio_SeqAudiostream_VoiceWindowCountsDown()
        {
            var (interp, ctx, sim, _) = BuildHost();
            Assert.AreEqual("1", interp.Eval("seq_audiostream open clip0470"));
            interp.Eval("seq_audiostream play 1.0 3.0");     // 2.0s window
            var au = ctx.Audio!;
            Assert.IsTrue(au.VoicePlaying);
            Assert.AreEqual(2.0f, au.VoiceRemaining, 1e-3f);
            for (int i = 0; i < 50; i++) sim.Step(0.05);     // 2.5s
            Assert.IsFalse(au.VoicePlaying, "voice stops at the end of its window");
        }

        // sq_audio open → parse_audio_file reads data/scripts/sequences/soundmarker/<clip>_en.txt
        // (CRLF-encoded) and must parse the markers despite line endings. Before the load_file
        // CRLF→LF normalisation the 'marker' header line read as "marker\r" so bMarker never
        // flipped → zero markers → "Audiomarker not found" → silent cutscene (no voice). #5.
        [Test]
        public void Audio_ParseAudioFile_LoadsMarkersFromCrlfFile()
        {
            string previousLocale = Diggles.Runtime.DigglesText.Locale;
            try
            {
                Diggles.Runtime.DigglesText.SetLocale(
                    Diggles.Runtime.DigglesText.DetectLocale(DataRoot));
                var (interp, ctx, _, _) = BuildHost();
                interp.Eval(TestSeqClass);
                interp.Eval(@"new TestSeq """" ""0 0 0"" ""0 0 0""");   // defines sq_audio/parse_audio_file/get_audio_marker + file_wrapper
                interp.Eval("sq_audio open Clip_3");                    // loads the locale-specific marker file and opens clip0003.mp3
                string am = interp.Eval("get_audio_marker Ahh").Trim();
                Assert.IsNotEmpty(am, "Ahh marker must load (CRLF normalisation) — empty means 'Audiomarker not found'");
                var parts = am.Split(' ');
                Assert.AreEqual(2, parts.Length, $"Ahh marker must expose its start/end times: '{am}'");
                float start = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                float end = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.That(start, Is.GreaterThanOrEqualTo(0f), $"Ahh start time, got: '{am}'");
                Assert.That(end, Is.GreaterThan(start), $"Ahh end time, got: '{am}'");
                Assert.That(ctx.Audio!.VoiceFile, Is.EqualTo("clip0003.mp3").IgnoreCase,
                    "parse_audio_file should seq_audiostream-open the soundfile (voice playback source)");
            }
            finally
            {
                Diggles.Runtime.DigglesText.SetLocale(previousLocale);
            }
        }

        [Test]
        public void Audio_WavLoader_ParsesRealGameWav()
        {
            var dir = Path.Combine(DataRoot, "Sound");
            Assert.IsTrue(Directory.Exists(dir), "data/Sound missing");
            var wav = Directory.GetFiles(dir, "*.wav", SearchOption.AllDirectories);
            Assert.IsNotEmpty(wav, "no .wav under data/Sound");
            var data = Diggles.Runtime.WavLoader.ParseWav(File.ReadAllBytes(wav[0]));
            Assert.That(data.Channels, Is.GreaterThanOrEqualTo(1));
            Assert.That(data.SampleRate, Is.GreaterThan(0));
            Assert.That(data.Samples.Length, Is.GreaterThan(0), $"no samples decoded from {Path.GetFileName(wav[0])}");
            Assert.That(data.Duration, Is.GreaterThan(0f));
        }

        // ---- particles / FX (task #5) ----

        [Test]
        public void Particles_ChangeSetFree_ConfiguresSource()
        {
            var (interp, ctx, _, _) = BuildHost();
            interp.Eval(TestActorClass);
            interp.Eval(@"set o [new TestActor """" ""0 0 0"" ""0 0 0""]");
            // change_particlesource <obj> <idx> <type> <relpos> <dir> <max> <rate> <visfog> [link] [maxrng] [objverts] [submesh]
            interp.Eval(@"change_particlesource $o 0 27 {0 0 0} {0 0.1 0} 511 16 0 3");
            int o = int.Parse(interp.Eval("set o"));
            Assert.IsTrue(ctx.Particles!.Sources.TryGetValue((o, 0), out var s), "source configured");
            Assert.AreEqual(27, s!.Type);
            Assert.AreEqual(511, s.MaxParticles);
            Assert.AreEqual(16, s.NumPerFrame);
            Assert.IsFalse(s.Active, "configured but not yet activated");
            interp.Eval(@"set_particlesource $o 0 1");
            Assert.IsTrue(ctx.Particles.Sources[(o, 0)].Active, "activated");
            interp.Eval(@"set_particlesource $o 0 0");
            Assert.IsFalse(ctx.Particles.Sources[(o, 0)].Active, "deactivated");
            interp.Eval(@"free_particlesource $o 0");
            Assert.IsFalse(ctx.Particles.Sources.ContainsKey((o, 0)), "freed");
        }

        [Test]
        public void Particles_CreateTemp_ExpiresAfterLifetime()
        {
            var (interp, ctx, sim, _) = BuildHost();
            // create_particlesource <type> <pos> <dir> <num> <time> (do_particle create maps here)
            interp.Eval(@"create_particlesource 13 {0 0.4 0.1} {0 -0.1 0.1} 16 2");
            Assert.AreEqual(1, ctx.Particles!.Temps.Count);
            Assert.AreEqual(13, ctx.Particles.Temps[0].Type);
            for (int i = 0; i < 50; i++) sim.Step(0.05);     // 2.5s > 2s
            Assert.AreEqual(0, ctx.Particles.Temps.Count, "temp source expires after its lifetime");
        }

        [Test]
        public void Particles_DoParticle_ViaSequencerProc()
        {
            // do_particle create <type> <pen> <dir> <num> <time> is a sequencer proc → create_particlesource.
            var (interp, ctx, _, _) = BuildHost();
            interp.Eval(TestSeqClass);
            interp.Eval(@"new TestSeq """" ""0 0 0"" ""0 0 0""");   // defines do_particle + parse_pos
            interp.Eval("set TriggerPos {2 0 3}");
            interp.Eval(@"do_particle create 7 TriggerPos {0 0.4 0.1} 8 2");
            Assert.AreEqual(1, ctx.Particles!.Temps.Count, "do_particle create spawned a source");
            Assert.AreEqual(7, ctx.Particles.Temps[0].Type);
        }

        // ---- screen FX (task #6) ----

        [Test]
        public void ScreenFx_FadeToBlack_And_FromBlack()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval("start_fade 0.5 0");                  // to black in 0.5s
            Assert.IsTrue(ctx.ScreenFx!.Fading);
            for (int i = 0; i < 20; i++) sim.Step(0.05);      // 1s
            Assert.AreEqual(1f, ctx.ScreenFx.FadeAlpha, 1e-3f, "faded fully to black");
            Assert.IsFalse(ctx.ScreenFx.Fading);
            interp.Eval("start_fade 1 1");                    // from black
            Assert.AreEqual(1f, ctx.ScreenFx.FadeAlpha, 1e-3f, "starts black");
            for (int i = 0; i < 30; i++) sim.Step(0.05);      // 1.5s
            Assert.AreEqual(0f, ctx.ScreenFx.FadeAlpha, 1e-3f, "faded to clear");
        }

        [Test]
        public void ScreenFx_Screenvibe_RawAndPreset()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval("screenvibe 2 3 2 0.1 103 0.1 200");  // att+sus+dec = 7s
            Assert.IsTrue(ctx.ScreenFx!.ShakeActive);
            Assert.AreEqual(7f, ctx.ScreenFx.ShakeRemaining, 1e-3f);
            Assert.AreEqual(0.1f, ctx.ScreenFx.CurrentShake.AmpX, 1e-3f);
            for (int i = 0; i < 160; i++) sim.Step(0.05);     // 8s > 7s
            Assert.IsFalse(ctx.ScreenFx.ShakeActive, "shake envelope ends");

            // sq_screenvibe equake7 (sequencer proc) → screenvibe 2 3 2 …
            interp.Eval(TestSeqClass);
            interp.Eval(@"new TestSeq """" ""0 0 0"" ""0 0 0""");
            interp.Eval("set is_breaked 0");
            interp.Eval("sq_screenvibe equake7");
            Assert.IsTrue(ctx.ScreenFx.ShakeActive, "preset resolved to a real shake");
        }

        [Test]
        public void ScreenFx_LightningAndLaserbeam()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval("lightning {5 0 2}");
            Assert.AreEqual(1, ctx.ScreenFx!.LightningCount);
            Assert.AreEqual(5f, ctx.ScreenFx.LastLightningPos.x, 1e-3f);
            interp.Eval("laserbeam {0 0 0} {3 0 0} {255 0 0} 18 0 0 1");
            Assert.AreEqual(1, ctx.ScreenFx.Beams.Count);
            interp.Eval("delete_laserbeams");
            Assert.AreEqual(0, ctx.ScreenFx.Beams.Count);
        }

        // ---- actor / anim glue (task #7) ----

        [Test]
        public void ActorGlue_ActionRotate_SetsAbsoluteFacing()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(TestActorClass);
            interp.Eval(@"set a [new TestActor """" ""0 0 0"" ""0 0 0""]");
            interp.Eval(@"action $a rotate 1.57");          // left (π/2), absolute radians
            int a = int.Parse(interp.Eval("set a"));
            Assert.AreEqual(1.57f, ctx.GetEntity(a.ToString())!.Rotation.y, 1e-2f);
            for (int i = 0; i < 10; i++) sim.Step(0.05);
            Assert.IsNull(ctx.GetEntity(a.ToString())!.ActiveAction, "rotate completes (no hang)");
        }

        [Test]
        public void ActorGlue_ClassAnimInfo_RoundTrips()
        {
            var (interp, _, _, _) = BuildHost();
            Assert.AreEqual("", interp.Eval("get_classaniminfo Zwerg reden_a"), "empty when none registered");
            interp.Eval("set_classaniminfo Zwerg reden_a 3");
            Assert.AreEqual("3", interp.Eval("get_classaniminfo Zwerg reden_a"));
        }

        [Test]
        public void ActorGlue_DbAnimlength_ZeroWithoutLibrary()
        {
            var (interp, _, _, _) = BuildHost();
            Assert.AreEqual("0", interp.Eval("db_animlength elfe.reden_a"));   // no model lib headless
        }

        // ---- the Elf guide + StoryManager (task #8) ----

        [Test]
        public void Elf_Move_FiresFinishcodeOnArrival()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(@"elf finishcode this ""set elfdone 1""");
            interp.Eval("elf move {5 0 0}");
            Assert.IsTrue(ctx.Elf!.Moving);
            for (int i = 0; i < 60 && ctx.Elf.Moving; i++) sim.Step(0.05);
            Assert.IsFalse(ctx.Elf.Moving, "elf reaches its target");
            Assert.AreEqual(5f, ctx.Elf.Position.x, 0.05f);
            Assert.AreEqual("1", interp.GetVar("elfdone"), "arrival ran the elf finishcode");
        }

        [Test]
        public void Elf_BeamAnimHide()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval("elf set_pos {2 0 2}");
            Assert.AreEqual(2f, ctx.Elf!.Position.x, 1e-3f);
            interp.Eval("elf anim reden_a");
            Assert.AreEqual("reden_a", ctx.Elf.CurrentAnim);
            interp.Eval("elf hide");
            // Faithful hide = mode 3: she flies until OFF-SCREEN, then resets invisible — needs a
            // tick to evaluate IsOnScreen (headless default = off-screen ⇒ completes immediately).
            sim.Step(0.05);
            Assert.IsFalse(ctx.Elf.Visible);
        }

        [Test]
        public void StoryManager_EventFlags_SetGet()
        {
            var (interp, _, _, _) = BuildHost();
            Assert.AreEqual("0", interp.Eval("sm_get_event Drache_angegriffen"));
            interp.Eval("sm_set_event Drache_angegriffen 1");
            Assert.AreEqual("1", interp.Eval("sm_get_event Drache_angegriffen"));
        }

        // ---- repro: the pen/scope bug (TriggerPos correct, but parse_pos pen → 0) ----

        // Mirrors the sequencer: a global set inside a METHOD (like init_pens during `run`),
        // then read via a PROC using `global $name` + `subst $$name` (like parse_pos) during a
        // STATE tick (like eval_sequence). If this returns "" instead of the value, that's the
        // pen bug that collapses every cutscene position/camera to 0.
        [Test]
        public void Pen_GlobalSetInMethod_ReadViaProcInStateTick()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(@"proc readpen {name} { global $name ; return [subst $$name] }");
            interp.Eval(@"
                def_class PenT none dummy 0 {} {
                    member result
                    obj_init { set result NONE }
                    method setup {} { global MyPen ; set MyPen {4 5 6} }
                    state tick { set result [readpen MyPen] ; state_disable this }
                }
                set h [new PenT """" ""1 2 3"" ""0 0 0""]
                call_method $h setup
                state_trigger $h tick
                state_enable $h");
            sim.Step(0.1);   // one 10 Hz logic tick
            var ent = ctx.GetEntity(interp.Eval("set h"))!;
            Assert.AreEqual("4 5 6", ent.VarsScope.Vars["result"],
                "a global set in a method must be readable via `global`+`subst $$` in a proc during a state tick");
        }

        // Same, but the global is set by a PROC (like sq_text) rather than a method — this is the
        // path that WORKED in the live log (textfile), so it should pass; pins down the difference.
        [Test]
        public void Pen_GlobalSetInProc_ReadViaProcInStateTick()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(@"proc readpen {name} { global $name ; return [subst $$name] }");
            interp.Eval(@"proc setpen {} { global MyPen ; set MyPen {7 8 9} }");
            interp.Eval(@"
                def_class PenU none dummy 0 {} {
                    member result
                    obj_init { set result NONE }
                    state tick { setpen ; set result [readpen MyPen] ; state_disable this }
                }
                set h [new PenU """" ""1 2 3"" ""0 0 0""]
                state_trigger $h tick
                state_enable $h");
            sim.Step(0.1);   // one 10 Hz logic tick
            var ent = ctx.GetEntity(interp.Eval("set h"))!;
            Assert.AreEqual("7 8 9", ent.VarsScope.Vars["result"]);
        }

        // Faithful repro of the live bug: drive urw_3 with actors at realistic NON-zero campaign
        // positions (so TriggerPos != 0), then assert the camera focus follows them and doesn't
        // collapse to the origin. Reproduces the pen-resolution bug seen in Play.
        [Test]
        public void Repro_Urw3_CameraFocusFollowsActors_NotOrigin()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(TestActorClass);
            interp.Eval(TestSeqClass);
            interp.Eval(@"
                set seq [new TestSeq """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a0 [new TestActor """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a1 [new TestActor """" ""234.0 30.44 12.58"" ""0 0 0""]
                set a2 [new TestActor """" ""235.0 30.44 12.58"" ""0 0 0""]
                call_method $seq preset_actors [list $a0 $a1 $a2]
                call_method $seq set_sequencescript urw_3
                call_method $seq run");
            // Past the opening do_wait time 4 → the +sq_pen/+sq_camera fix lines.
            for (int i = 0; i < 300; i++) sim.Step(0.05);
            var c = ctx.Camera!;
            TestContext.WriteLine($"urw_3 camera focus after opening: {c.Focus}");
            Assert.That(c.Focus.x, Is.GreaterThan(100f),
                $"camera focus collapsed toward origin (pen-resolution bug): Focus={c.Focus} (expected near the actors ~228..235)");
        }

        // FAITHFUL repro: full class system (loadRealClasses) + REAL Zwerg actors (whose AI
        // ticks during the cutscene) + urw_3 — matching the live Play setup. The TestActor repro
        // passed, so the bug needs this environment. If the camera focus collapses to origin here,
        // we've reproduced it headlessly. [Explicit] — slow (loads ~684 classes).
        [Test, Explicit("Slow: loads the full class system to reproduce the live cutscene env")]
        public void Repro_Urw3_FullClasses_RealZwerge_CameraFocus()
        {
            var (interp, ctx, sim, unknown) = BuildHost();
            try { interp.Eval("call scripts/init/txtinit.tcl"); } catch { }
            try { interp.Eval("call scripts/init/animinit.tcl"); } catch { }
            try { interp.Eval("call scripts/init/classinit.tcl"); } catch { }
            interp.Eval(TestSeqClass);
            interp.Eval(@"
                set seq [new TestSeq """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a0 [new Zwerg """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a1 [new Zwerg """" ""234.0 30.44 12.58"" ""0 0 0""]
                set a2 [new Zwerg """" ""235.0 30.44 12.58"" ""0 0 0""]
                set a3 [new Zwerg """" ""232.8 30.50 12.48"" ""0 0 0""]
                set a4 [new Zwerg """" ""236.1 30.50 11.98"" ""0 0 0""]
                call_method $seq preset_actors [list $a0 $a1 $a2 $a3 $a4]
                call_method $seq set_sequencescript urw_3
                call_method $seq run");
            for (int i = 0; i < 400; i++) sim.Step(0.05);
            var c = ctx.Camera!;
            TestContext.WriteLine($"FULL-env urw_3 camera focus: {c.Focus}");
            Assert.That(c.Focus.x, Is.GreaterThan(100f),
                $"REPRODUCED: camera focus collapsed to origin with full classes — Focus={c.Focus}");
        }

        // MOST faithful repro: load classes AND run the real Campaign.tcl (the full world spawn —
        // the one thing the other repros skipped), then drive urw_3 with the spawned dwarves as
        // actors. CutsceneTrace on, so the set_viewpos probe logs the live scope reads. If pens
        // resolve to 0 here, reproduced headlessly. [Explicit] — slow + noisy.
        [Test, Explicit("Slow: full class load + Campaign.tcl world spawn to reproduce the live env")]
        public void Repro_Urw3_FullCampaign_PenResolution()
        {
            var (interp, ctx, sim, _) = BuildHost();
            Diggles.Runtime.CutsceneTrace.Enabled = true;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl", "scripts/gameplay/Campaign.tcl" })
                try { interp.Eval("call " + s); } catch (System.Exception e) { TestContext.WriteLine($"({s} aborted: {e.Message})"); }

            // Collect spawned dwarf actors (as PlayCutscene does).
            var actors = new List<int>();
            int firstGnome = 0;
            foreach (var e in ctx.Entities.Values)
                if (e.Class != null && e.Class.Name == "Zwerg") { if (firstGnome == 0) firstGnome = e.Handle; actors.Add(e.Handle); if (actors.Count >= 8) break; }
            TestContext.WriteLine($"Campaign spawned {ctx.Entities.Count} entities, {actors.Count} Zwerg actors");
            Assert.That(actors.Count, Is.GreaterThan(0), "Campaign.tcl should spawn dwarves");

            interp.Eval(TestSeqClass);
            string posStr = interp.Eval($"get_pos {firstGnome}");
            interp.Eval($"set seq [new TestSeq \"\" \"{posStr}\" \"0 0 0\"]");
            interp.Eval($"call_method $seq preset_actors [list {string.Join(" ", actors)}]");
            interp.Eval("call_method $seq set_sequencescript urw_3");
            interp.Eval("call_method $seq run");
            for (int i = 0; i < 400; i++) sim.Step(0.05);
            var firstEnt = ctx.GetEntity(firstGnome.ToString())!;
            TestContext.WriteLine($"camera focus={ctx.Camera!.Focus}; first gnome pos={firstEnt.Position}");
            Assert.That(ctx.Camera.Focus.x, Is.GreaterThan(100f), $"REPRODUCED pen=0: focus={ctx.Camera.Focus}");
        }

        // ---- comprehensive: every real cutscene script (task #9, acceptance gate) ----

        // Spawn 4 generic actors + a TestSeq trigger, point it at the given script, activate, and
        // step the sim. Returns (finished, exceptionOrNull). Shared by the sweep + sanity tests.
        static (bool finished, string? error) RunSequence(string name, int maxSteps,
            out List<string> unknownLeaves)
        {
            var (interp, ctx, sim, unknown) = BuildHost();
            unknownLeaves = unknown;
            try
            {
                interp.Eval(TestActorClass);
                interp.Eval(TestSeqClass);
                interp.Eval(@"
                    set seq [new TestSeq """" ""0 0 0"" ""0 0 0""]
                    set a0 [new TestActor """" ""0 0 0""   ""0 0 0""]
                    set a1 [new TestActor """" ""1 0 0""   ""0 0 0""]
                    set a2 [new TestActor """" ""2 0 0""   ""0 0 0""]
                    set a3 [new TestActor """" ""3 0 0""   ""0 0 0""]
                    call_method $seq preset_actors [list $a0 $a1 $a2 $a3]");
                interp.Eval($"call_method $seq set_sequencescript {name}");
                interp.Eval("call_method $seq run");
                int seqH = int.Parse(interp.Eval("set seq"));
                bool fin = false;
                for (int j = 0; j < maxSteps && !fin; j++)
                {
                    sim.Step(0.05);
                    var e = ctx.GetEntity(seqH.ToString());
                    fin = e == null || (e.VarsScope.Vars.TryGetValue("sequence_counter", out var sc) && sc != "0" && sc != "");
                }
                return (fin, null);
            }
            catch (System.Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
        }

        // #3/#6 freeze: during a cutscene EVERY dwarf — the preset actors AND the ambient
        // bystanders — must have its state machine frozen, so their sparetime/idle AI doesn't
        // fight the sequence (repeated SetAnim → frozen anim frame). The reliable mechanism is
        // park_obj_actions {Zwerg …} (autoidling=1 in urw_3): it sets Parked on all dwarves;
        // remove_parked_obj must NOT un-freeze preset actors. This reproduces the live env
        // (preset + ambient) and asserts none of them runs sparetime.
        [Test, Explicit("Slow: loads the full class system")]
        public void CutsceneFreezesAllDwarves_NoSparetime()
        {
            var (interp, ctx, sim, _) = BuildHost();
            try { interp.Eval("call scripts/init/txtinit.tcl"); } catch { }
            try { interp.Eval("call scripts/init/animinit.tcl"); } catch { }
            try { interp.Eval("call scripts/init/classinit.tcl"); } catch { }
            interp.Eval(TestSeqClass);
            interp.Eval(@"
                set seq [new TestSeq """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a0 [new Zwerg """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a1 [new Zwerg """" ""234.0 30.44 12.58"" ""0 0 0""]
                set b0 [new Zwerg """" ""232.0 30.44 13.0"" ""0 0 0""]
                set b1 [new Zwerg """" ""233.0 30.44 13.0"" ""0 0 0""]
                set b2 [new Zwerg """" ""235.0 30.44 13.0"" ""0 0 0""]");
            // Let AI settle into its normal running states first (so counts vary, like live).
            for (int i = 0; i < 40; i++) sim.Step(0.05);
            // Preset only a0/a1; b0/b1/b2 are ambient bystanders.
            interp.Eval("call_method $seq preset_actors [list $a0 $a1]");
            interp.Eval("call_method $seq set_sequencescript urw_3");
            Diggles.Runtime.CutsceneTrace.Enabled = true;
            interp.Eval("call_method $seq run");
            var dwarves = new List<int>();
            foreach (var v in new[] { "a0", "a1", "b0", "b1", "b2" }) dwarves.Add(int.Parse(interp.Eval($"set {v}")));
            var sawSparetime = new List<string>();
            for (int i = 0; i < 300; i++)
            {
                sim.Step(0.05);
                foreach (var h in dwarves)
                {
                    var e = ctx.GetEntity(h.ToString());
                    if (e?.CurrentState != null && e.CurrentState.Contains("SPARETIME"))
                        sawSparetime.Add($"#{h} state={e.CurrentState} enCnt={e.StateEnabledCount} parked={e.Parked}");
                }
            }
            foreach (var h in dwarves)
            {
                var e = ctx.GetEntity(h.ToString())!;
                TestContext.WriteLine($"#{h} final state={e.CurrentState} enCnt={e.StateEnabledCount} parked={e.Parked}");
            }
            Assert.IsEmpty(sawSparetime,
                "a dwarf ran sparetime during the cutscene (AI not frozen): " + string.Join("; ", sawSparetime.GetRange(0, System.Math.Min(5, sawSparetime.Count))));
        }

        // #1 timing: urw_3 is paced by ~30 `do_wait time X` summing to ~80-100s. If the
        // sequencer doesn't actually block on do_wait, lines race → audio cuts off + the cutscene
        // ends in seconds. Measure the GameTime the cutscene runs before shut_sequencer.
        [Test, Explicit("Slow: loads the full class system")]
        public void Diag_Urw3_Duration_AndParkWindow()
        {
            var (interp, ctx, sim, _) = BuildHost();
            try { interp.Eval("call scripts/init/txtinit.tcl"); } catch { }
            try { interp.Eval("call scripts/init/animinit.tcl"); } catch { }
            try { interp.Eval("call scripts/init/classinit.tcl"); } catch { }
            interp.Eval(TestSeqClass);
            interp.Eval(@"
                set seq [new TestSeq """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a0 [new Zwerg """" ""231.54 30.44 12.78"" ""0 0 0""]
                set a1 [new Zwerg """" ""234.0 30.44 12.58"" ""0 0 0""]
                set a2 [new Zwerg """" ""235.0 30.44 12.58"" ""0 0 0""]
                set a3 [new Zwerg """" ""232.8 30.50 12.48"" ""0 0 0""]
                set a4 [new Zwerg """" ""236.1 30.50 11.98"" ""0 0 0""]
                set b0 [new Zwerg """" ""240.0 30.44 13.0"" ""0 0 0""]");
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            interp.Eval("call_method $seq preset_actors [list $a0 $a1 $a2 $a3 $a4]");
            interp.Eval("call_method $seq set_sequencescript urw_3");
            int seqH = int.Parse(interp.Eval("set seq"));
            interp.Eval("call_method $seq run");
            int b0 = int.Parse(interp.Eval("set b0"));
            var eB0 = ctx.GetEntity(b0.ToString())!;
            double endTime = -1; int parkedTrueSteps = 0; bool everParked = false;
            for (int i = 0; i < 3000; i++)
            {
                sim.Step(0.05);
                if (eB0.Parked) { everParked = true; parkedTrueSteps++; }
                bool ended = ctx.GetEntity(seqH.ToString()) == null;
                if (ended) { endTime = sim.GameTime; break; }
            }
            TestContext.WriteLine($"urw_3 ended at GameTime={endTime:F1}s (step≈{endTime/0.05:F0}); ambient b0 everParked={everParked} parkedSteps={parkedTrueSteps}");
            // urw_3's do_wait times sum to well over 30s; if it ends in <20s the waits aren't blocking.
            Assert.That(endTime, Is.GreaterThan(30.0).Or.EqualTo(-1.0),
                $"urw_3 ended too fast ({endTime:F1}s) — do_wait not blocking → audio cutoff");
        }

        // Per-object proc isolation (#1 root cause): the original gives each object its own
        // interp, so a `proc` of the same name in two classes doesn't collide. Our shared global
        // proc table let the last-loaded clobber the rest — e.g. a dwarf's `proc wait_time
        // {seconds}` clobbered the sequencer's `proc wait_time {}`, so do_wait never blocked and
        // cutscenes raced (audio cut off). This verifies same-named procs resolve per active class.
        [Test]
        public void PerClassProcs_SameName_ResolvePerActiveObject()
        {
            var (interp, ctx, _, _) = BuildHost();
            // Two classes define `tag` with different arity/result in their obj_init (like the
            // sequencer's wait_time {} vs a dwarf's wait_time {seconds}).
            interp.Eval(@"def_class ClassA none dummy 0 {} {
                obj_init { proc tag {} { return ""A0"" } }
                method whoami {} { return [tag] }
            }");
            interp.Eval(@"def_class ClassB none dummy 0 {} {
                obj_init { proc tag {x} { return ""B-$x"" } }
                method whoami {} { return [tag 9] }
            }");
            interp.Eval(@"set a [new ClassA """" ""0 0 0"" ""0 0 0""]");
            interp.Eval(@"set b [new ClassB """" ""0 0 0"" ""0 0 0""]");   // loads ClassB's tag LAST (clobbers global)
            Assert.AreEqual("A0", interp.Eval("call_method $a whoami").Trim(),
                "ClassA must see its OWN proc tag {} (0-arg), not ClassB's tag {x} that loaded later");
            Assert.AreEqual("B-9", interp.Eval("call_method $b whoami").Trim(),
                "ClassB must see its OWN proc tag {x}");
        }

        // ---- skip drain: get_sequencebreaked / set_sequence semantics + the Tcl drain ----

        sealed class DrainPresentationSpawner : IDigglesEntitySpawner
        {
            public int SpawnCalls { get; private set; }
            // ent.CurrentAnim AT SPAWN TIME — the pose the real spawner would resolve the
            // model from. The pump's boxed/pose-correction seeding is asserted through this.
            public string? LastSpawnAnim { get; private set; }
            public (string Clip, int Frame, DigglesAnimator.AnimMode Mode)? LastAnim
            { get; private set; }

            public GameObject Spawn(DigglesTclEntity ent, Transform parent)
            {
                SpawnCalls++;
                LastSpawnAnim = ent.CurrentAnim;
                var go = new GameObject($"drain_{ent.Handle}");
                go.transform.SetParent(parent, false);
                // Mirror RuntimeWorldSpawner's outer/inner shape: an inner "Model" with a
                // DigglesAnimator, so link attach (RestoreLinkAttachment → FindInnerModel +
                // GetComponent<DigglesAnimator>) operates on its real anchors headless.
                var model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                model.AddComponent<DigglesAnimator>();
                return go;
            }

            public void Destroy(GameObject go) => Object.DestroyImmediate(go);
            public void ApplyPosition(GameObject go, Vector3 nativePos) { }
            public void ApplyRotation(GameObject go, Vector3 nativeRotRadians) { }
            public void ApplyAnim(
                GameObject go,
                string animName,
                int startFrame,
                DigglesAnimator.AnimMode mode) => LastAnim = (animName, startFrame, mode);
        }

        [Test]
        public void SequenceDrain_DefersPresentationSpawn_UntilDrainCompletes()
        {
            var (interp, ctx, _, _) = BuildHost();
            var root = new GameObject("CutsceneDrainPresentationRoot");
            var spawner = new DrainPresentationSpawner();
            ctx.SpawnParent = root.transform;
            ctx.EntitySpawner = spawner;
            try
            {
                interp.Eval("def_class DrainProp none dummy 0 {} {}");
                interp.Eval("set_sequence 1");
                ctx.SequenceBreaked = true;
                Assert.AreEqual("1", interp.Eval("get_sequencebreaked"));

                int surviving = int.Parse(interp.Eval(
                    "new DrainProp surviving {2 3 4} {0 0 0}"));
                Assert.That(ctx.Entities.ContainsKey(surviving), Is.True);
                Assert.That(spawner.SpawnCalls, Is.Zero,
                    "the skip drain must not enter the model/asset spawner");
                Assert.That(ctx.DeferredSequencePresentationCount, Is.EqualTo(1));

                interp.Eval("set_sequence 0");
                Assert.That(
                    DigglesTclEngine.PumpDeferredSequencePresentation(ctx),
                    Is.EqualTo(1));
                Assert.That(spawner.SpawnCalls, Is.EqualTo(1));
                Assert.That(ctx.Entities[surviving].GameObject, Is.Not.Null);

                interp.Eval("set_sequence 1");
                ctx.SequenceBreaked = true;
                interp.Eval("get_sequencebreaked");
                int transient = int.Parse(interp.Eval(
                    "new DrainProp transient {5 6 7} {0 0 0}"));
                interp.Eval($"del {transient}");
                interp.Eval("set_sequence 0");
                Assert.That(
                    DigglesTclEngine.PumpDeferredSequencePresentation(ctx),
                    Is.Zero,
                    "a logically deleted drain-only prop must never materialize");
                Assert.That(spawner.SpawnCalls, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        ///: presentation issued DURING the drain must be recorded as
        // LOGICAL state and replayed by the pump — not counted-and-dropped (R1: flipbooks),
        // and the replay must use the alias-resolved clip + real start frame (R3: CurrentAnim
        // keeps the RAW script name by the save-echo law, and PlayClip resolves obj prefixes
        // but NOT set_class_anim aliases). The R2 seeding-order fix (initial-anim seed now
        // runs BEFORE the drain gate in CmdSetAnim) needs a model library to observe and is
        // covered by the save-echo suites; here the same reorder feeds the recorded payload.
        [Test]
        public void SequenceDrain_ReplaysAliasAnim_AndTextureAnimation_AfterSkip()
        {
            var (interp, ctx, _, _) = BuildHost();
            var root = new GameObject("CutsceneDrainReplayRoot");
            var spawner = new DrainPresentationSpawner();
            ctx.SpawnParent = root.transform;
            ctx.EntitySpawner = spawner;
            try
            {
                interp.Eval(
                    "def_class DrainActor none dummy 0 {} { set_class_anim rebound mann.abprallen }");
                interp.Eval("set_sequence 1");
                ctx.SequenceBreaked = true;
                interp.Eval("get_sequencebreaked");

                int actor = int.Parse(interp.Eval("new DrainActor drained {2 3 4} {0 0 0}"));
                // The z_anims.tcl:1031 start_001 shape: `set_anim [Actor 0] rebound 11 1` —
                // a class-anim ALIAS with a start frame, play-once.
                interp.Eval($"set_anim {actor} rebound 11 1");
                // The sq_actor eyes shape: a 2-frame play-once kick on submesh 4. Issue-time
                // cutscene reduction (single-pose collapse — SequenceActive is still TRUE
                // during the drain) must apply BEFORE recording.
                interp.Eval($"set_textureanimation {actor} 4 {{11 0}} 0 1");

                Assert.That(spawner.SpawnCalls, Is.Zero, "drain must not touch the spawner");
                Assert.IsTrue(
                    DigglesTclEngine.TryPeekDrainDeferredTextureAnim(
                        ctx, actor, 4, out int[] frames, out int texStart, out int texType),
                    "the drained set_textureanimation must be RECORDED (was: counted and dropped)");
                Assert.AreEqual(new[] { 11, 11 }, frames,
                    "the issue-time single-pose collapse applies before recording");
                Assert.AreEqual(0, texStart);
                Assert.AreEqual(1, texType);

                interp.Eval("set_sequence 0");
                Assert.That(
                    DigglesTclEngine.PumpDeferredSequencePresentation(ctx),
                    Is.EqualTo(1));
                Assert.That(spawner.SpawnCalls, Is.EqualTo(1));
                Assert.IsTrue(spawner.LastAnim.HasValue, "the recorded anim must be replayed");
                Assert.AreEqual("mann.abprallen", spawner.LastAnim!.Value.Clip,
                    "replay must use the ALIAS-RESOLVED clip, not the raw script name");
                Assert.AreEqual(11, spawner.LastAnim.Value.Frame,
                    "replay must keep the recorded start frame (was: hardcoded 0)");
                Assert.AreEqual(DigglesAnimator.AnimMode.Once, spawner.LastAnim.Value.Mode);
                Assert.IsFalse(
                    DigglesTclEngine.TryPeekDrainDeferredTextureAnim(
                        ctx, actor, 4, out _, out _, out _),
                    "texture-anim records are consumed by the replay");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // the skipped intro's summoned Feuerstelle
        // (urw_3.tcl:29 summon → packtobox) drains `set_anim this.standard` — genericprod's
        // set_buildupanim with an empty $standard_anim splits $cname to "" (genericprod.tcl:
        // 803-806); the native set_anim ERRORS into the surrounding catch and changes nothing,
        // but CmdSetAnim latches CurrentAnim=".standard", and the pump's deferred spawn then
        // resolved the model FROM that garbage pose → placeholder without a controller → the
        // boxed kiste swap silently no-oped. The pump must seed a boxed drain-born entity's
        // spawn with the boxed presentation (kiste.standard — the save loader's exact staging,
        // OriginalSaveRuntimeSessionStager.MaterializeEntityPresentation) while the RAW recorded
        // pose stays on the entity (save-echo law).
        [Test]
        public void SequenceDrain_BoxedDrainBornEntity_SpawnsWithBoxedPresentation()
        {
            var (interp, ctx, _, _) = BuildHost();
            var root = new GameObject("CutsceneDrainBoxedRoot");
            var spawner = new DrainPresentationSpawner();
            ctx.SpawnParent = root.transform;
            ctx.EntitySpawner = spawner;
            try
            {
                interp.Eval("def_class DrainProd wood production 0 {} {}");
                interp.Eval("set_sequence 1");
                ctx.SequenceBreaked = true;
                interp.Eval("get_sequencebreaked");

                int h = int.Parse(interp.Eval("new DrainProd crate {2 3 4} {0 0 0}"));
                interp.Eval($"set_anim {h} .standard 0 0");
                interp.Eval($"set_boxed {h} 1");
                Assert.That(spawner.SpawnCalls, Is.Zero, "drain must not touch the spawner");

                interp.Eval("set_sequence 0");
                Assert.That(
                    DigglesTclEngine.PumpDeferredSequencePresentation(ctx),
                    Is.EqualTo(1));
                Assert.That(spawner.SpawnCalls, Is.EqualTo(1));
                Assert.AreEqual("kiste.standard", spawner.LastSpawnAnim,
                    "a drain-born BOXED entity must spawn with the boxed kiste presentation, " +
                    "not the (possibly unbuildable) recorded pose");
                Assert.AreEqual(".standard", ctx.Entities[h].CurrentAnim,
                    "the RAW recorded pose stays on the entity (save-echo law)");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // link_obj issued during the drain records only the
        // logical link (LinkedToHandle/LinkedSlot — RestoreLinkAttachment early-outs while the
        // child's spawn is deferred). The work-hat chain lands exactly there (create_muetze:
        // `new $muetze_name` + `link_obj $m_ref this 4`, z_work_common.tcl:767-775). The pump
        // must replay the visual attach when it finally spawns the child.
        [Test]
        public void SequenceDrain_ReplaysLinkAttachment_ForDrainBornChild()
        {
            var (interp, ctx, _, _) = BuildHost();
            var root = new GameObject("CutsceneDrainLinkRoot");
            var spawner = new DrainPresentationSpawner();
            ctx.SpawnParent = root.transform;
            ctx.EntitySpawner = spawner;
            try
            {
                interp.Eval("def_class DrainGnome none gnome 0 {} {}");
                interp.Eval("def_class DrainHat none tool 0 {} {}");
                // The carrier exists BEFORE the skip → live GameObject.
                int gnome = int.Parse(interp.Eval("new DrainGnome wig {1 2 3} {0 0 0}"));
                Assert.IsNotNull(ctx.Entities[gnome].GameObject);

                interp.Eval("set_sequence 1");
                ctx.SequenceBreaked = true;
                interp.Eval("get_sequencebreaked");
                int hat = int.Parse(interp.Eval("new DrainHat muetze {0 0 0} {0 0 0}"));
                interp.Eval($"link_obj {hat} {gnome} 4");
                Assert.IsNull(ctx.Entities[hat].GameObject, "hat spawn stays deferred");
                Assert.AreEqual(gnome, ctx.Entities[hat].LinkedToHandle,
                    "the LOGICAL link must be recorded during the drain");
                interp.Eval("set_sequence 0");

                Assert.That(
                    DigglesTclEngine.PumpDeferredSequencePresentation(ctx),
                    Is.EqualTo(1));
                var hatGo = ctx.Entities[hat].GameObject;
                Assert.IsNotNull(hatGo, "the pump must spawn the drain-born hat");
                Assert.IsNotNull(hatGo!.GetComponent<DigglesLinkAttachment>(),
                    "the pump must replay the drain-time link_obj (hat back on link-4)");
                Assert.IsTrue(
                    hatGo.transform.IsChildOf(ctx.Entities[gnome].GameObject!.transform),
                    "the attach re-parents the hat under the carrier's Model subtree");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // End-of-talk mouth law. Native chain: the
        // sound tick installs the viseme track play-once on submesh 3 (FUN_004d34d0:678-690 →
        // FUN_004d9be0(3, frames, 0, 1)); the generic type-1 tick end is freeze-at-frame-0
        // (CObj_Tick FUN_005cc600: cursor=(cursor+1)%count, wrap of type 1 → type 0), and the
        // face-mood layer immediately repaints the resting face (set_fanim → single-frame
        // set_textureanimation this 3, z_faceanim.tcl; cutscene actors via free_face_anim,
        // sequencer.tcl:543); a cleared/absent channel renders variant 0 = BASE texture
        // (FUN_005f5ef0 cursor-past-count branch). The port's frozen cutscene actors get no
        // mood repaint, so the TALK channel closes itself to base at completion — while the
        // scripted pose-kick law (sq_actor mouth/eyes hold) keeps the generic freeze.
        [Test]
        public void TalkMouth_PlayOnceFlipbook_RevertsToBaseTexture_AtTalkEnd()
        {
            var go = new GameObject("TalkMouthHost");
            Material? baseMat = null;
            try
            {
                var r = go.AddComponent<MeshRenderer>();
                var shader = Shader.Find("Hidden/Internal-Colored");
                Assert.IsNotNull(shader, "editor built-in shader must exist");
                baseMat = new Material(shader) { name = "face-base" };
                var mats = new Material[4];
                for (int k = 0; k < 4; k++) mats[k] = baseMat;
                r.sharedMaterials = mats;

                var tex = go.AddComponent<DigglesTextureAnimator>();
                var frames = new Texture2D?[] { null, null };
                var ids = new List<int> { 9, 0 };

                // TALK channel: revert-to-base at completion.
                tex.SetTextureAnimation(3, frames, ids, 0, 1, revertToBaseOnComplete: true);
                Assert.IsFalse(ReferenceEquals(r.sharedMaterials[3], baseMat),
                    "while the mouth talks, submesh 3 runs on the per-instance clone");
                tex.Step(0.11f);   // frame 0 → 1
                Assert.IsFalse(ReferenceEquals(r.sharedMaterials[3], baseMat),
                    "still talking on the last viseme");
                tex.Step(0.11f);   // past the end → talk complete
                Assert.IsTrue(ReferenceEquals(r.sharedMaterials[3], baseMat),
                    "talk end must CLEAR the mouth channel back to the base face material");

                // Scripted pose kick (sq_actor mouth/eyes): the SAME shape WITHOUT the talk
                // flag keeps the native freeze-at-frame-0 (pose held; sunglasses law).
                tex.SetTextureAnimation(3, frames, ids, 0, 1);
                for (int k = 0; k < 5; k++) tex.Step(0.11f);
                Assert.IsFalse(ReferenceEquals(r.sharedMaterials[3], baseMat),
                    "a scripted play-once pose must HOLD (freeze at frame 0), not clear");
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (baseMat != null) Object.DestroyImmediate(baseMat);
            }
        }

        // The break flag surface, faithful to the natives:
        // get_sequencebreaked = FUN_00649330 → byte DAT_00772400+0x8d;
        // set_sequence = FUN_00649080 → acts on VALUE CHANGE, always clears +0x8d, and the
        // 1→0 transition stops+closes the cutscene voice stream (SoloudAudioPlayer::Stop/Close).
        [Test]
        public void SequenceBreaked_FlagSurface_SetSequenceClears_AndStopsVoice()
        {
            var (interp, ctx, _, _) = BuildHost();
            Assert.AreEqual("0", interp.Eval("get_sequencebreaked"), "default = not breaked");
            ctx.SequenceBreaked = true;
            Assert.AreEqual("1", interp.Eval("get_sequencebreaked"), "flag must be REAL, not the old '0' stub");

            interp.Eval("set_sequence 1");
            Assert.IsTrue(ctx.SequenceActive);
            Assert.IsFalse(ctx.SequenceBreaked, "any set_sequence transition clears the break flag (FUN_00649080:90)");

            interp.Eval("seq_audiostream open clip0470");
            interp.Eval("seq_audiostream play 0 60");
            Assert.IsTrue(ctx.Audio!.VoicePlaying);
            ctx.SequenceBreaked = true;
            interp.Eval("set_sequence 0");
            Assert.IsFalse(ctx.SequenceBreaked, "set_sequence 0 clears the flag");
            Assert.IsFalse(ctx.Audio.VoicePlaying, "set_sequence 0 stops the voice stream (FUN_00649080:100-104)");
            Assert.IsNull(ctx.Audio.VoiceFile, "…and closes it");
        }

        // ESC mid-cutscene: the flag triggers eval_sequence's drain (sequencer.tcl:228-239) —
        // is_breaked=1 → parse_next fast-forwards every remaining non-'-' line → shut_sequencer:
        // actors re-enabled (set_sequenceactive 0 + state_enable), voice stopped, restore_obj_actions,
        // and `destroy` (now a REAL proc, un-shadowed from the stub list) → `del this` removes the
        // trigger. This is the faithful skip the port previously short-circuited with a raw del.
        [Test]
        public void Sequencer_Breaked_DrainsToShut_RestoresActors_AndDeletesTrigger()
        {
            var (interp, ctx, sim, _) = BuildHost();
            interp.Eval(TestActorClass);
            interp.Eval(TestSeqClass);
            interp.Eval(@"
                set seq [new TestSeq """" ""0 0 0"" ""0 0 0""]
                set a0 [new TestActor """" ""0 0 0"" ""0 0 0""]
                set a1 [new TestActor """" ""1 0 0"" ""0 0 0""]
                set a2 [new TestActor """" ""2 0 0"" ""0 0 0""]
                call_method $seq preset_actors [list $a0 $a1 $a2]
                call_method $seq set_sequencescript Gespraech
                call_method $seq run");
            int seqH = int.Parse(interp.Eval("set seq"));
            var actorHandles = new List<int>();
            foreach (var v in new[] { "a0", "a1", "a2" }) actorHandles.Add(int.Parse(interp.Eval($"set {v}")));

            // Let it play into the middle of the script…
            for (int i = 0; i < 60; i++) sim.Step(0.05);
            Assert.IsTrue(ctx.SequenceActive, "cutscene should be running before the break");
            Assert.IsNotNull(ctx.GetEntity(seqH.ToString()), "trigger alive before the break");

            // …then ESC (FUN_0052c510:353-355 → +0x8d).
            ctx.SequenceBreaked = true;
            int steps = 0;
            for (; steps < 200 && ctx.GetEntity(seqH.ToString()) != null; steps++) sim.Step(0.05);

            Assert.IsNull(ctx.GetEntity(seqH.ToString()),
                $"drain must reach shut_sequencer → destroy → del this (still alive after {steps} steps)");
            Assert.IsFalse(ctx.SequenceActive, "shut_sequencer ran set_sequence 0");
            Assert.IsFalse(ctx.Audio!.VoicePlaying, "voice stream stopped by set_sequence 0");
            foreach (var h in actorHandles)
            {
                var e = ctx.GetEntity(h.ToString())!;
                Assert.That(e.StateEnabledCount, Is.GreaterThanOrEqualTo(0),
                    $"actor #{h}: shut_sequencer state_enable must revive the AI (preset actors were state_disabled by add_actor)");
                Assert.IsFalse(e.Parked, $"actor #{h}: restore_obj_actions must un-park");
                if (e.VarsScope.Vars.TryGetValue("sequenceactive", out var sa))
                    Assert.AreNotEqual("1", sa, $"actor #{h}: set_sequenceactive 0 must clear the flag");
            }
            Assert.That(sim.TimeFactor, Is.EqualTo(1.0).Within(1e-9), "time factor sane after the skip");
        }

        [Test]
        public void AllSequences_StepThroughSequencer_WithoutExceptions()
        {
            var dir = Path.Combine(DataRoot, "Scripts", "sequences");
            Assert.IsTrue(Directory.Exists(dir), "sequences dir missing");
            var files = Directory.GetFiles(dir, "*.tcl", SearchOption.AllDirectories);
            Assert.IsNotEmpty(files);

            var failures = new List<string>();
            var unknownAll = new SortedSet<string>(System.StringComparer.Ordinal);
            int completed = 0;
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                var (fin, err) = RunSequence(name, 120, out var unknown);
                if (err != null) failures.Add($"{name}: {err}");
                else if (fin) completed++;
                foreach (var u in unknown) unknownAll.Add(u);
            }

            TestContext.WriteLine($"Sequences: {files.Length}, completed: {completed}, threw: {failures.Count}");
            TestContext.WriteLine("Distinct unimplemented leaves across ALL sequences: " +
                (unknownAll.Count == 0 ? "(none)" : string.Join(", ", unknownAll)));
            foreach (var fl in failures) TestContext.WriteLine("THREW: " + fl);

            Assert.IsEmpty(failures, $"{failures.Count} of {files.Length} sequences threw while stepping");
        }
    }
}
