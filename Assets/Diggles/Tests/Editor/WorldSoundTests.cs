#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // World-sound system (boot soundinit + `sound` verbs + anim-frame SFX seam).
    //
    // RE ground truth:
    // * dispatcher = CTclGlobalMethods::Sound FUN_00643a60 (verbs bind/anim/animnoseq/play/
    // playglobal/volume/animsound; material+room args validated then DISCARDED);
    // * registration = CSoundMgr::AddAnimSound FUN_004cf770 (value-deduped entry table +
    // per-frame entry-index lists, duplicates append → double random weight);
    // * playback = per-object anim tick FUN_005cb9a0 (frame-change loop, ONE random entry
    // per frame, noseq suppressed while a sequence runs) → FUN_005d0820 (volume variance,
    // >20/100 gate) → FUN_004d19a0 (positional falloff/pan);
    // * boot = systeminit.tcl:31-34 (soundinit + adaptiveinit right after classinit).
    //
    // Headless: no audio device in -batchmode — these tests assert the recorded INTENT
    // (AudioState tables/queues + the DigglesAnimator.AnimFrameAdvanced seam), never audibility.
    public sealed class WorldSoundTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
            DigglesAnimator.PublishTimeScale(1.0);
        }

        // Tolerant host (no data files) — the SparetimeBehaviorTests.Host pattern.
        static (TclInterpreter interp, DigglesTclContext ctx) Host(bool strict = false, string dataRoot = "")
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = dataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = !strict;
            return (interp, ctx);
        }

        static bool HasData => System.IO.Directory.Exists(DataRoot);

        // ------------------------------------------------------------------
        // sound bind — file validation (FUN_00643a60:250-272)
        // ------------------------------------------------------------------

        [Test]
        public void Bind_MissingFile_ErrorsAndIsCatchable()
        {
            if (!HasData) Assert.Ignore("original data not present");
            var (interp, ctx) = Host(strict: true, dataRoot: DataRoot);
            // native: fopen fails → Tcl error "can't find sample file." — soundinit.tcl:35-47
            // wraps optional binds in catch{} relying on exactly this.
            Assert.AreEqual("1", interp.Eval("catch {sound bind nope data/sound/sprache/cow.wav}"));
            Assert.IsNull(ctx.Audio!.ResolveSound("nope"));
            // a real file binds fine
            interp.Eval("sound bind ok data/sound/kiste_ablegen.wav");
            Assert.AreEqual("data/sound/kiste_ablegen.wav", ctx.Audio.ResolveSound("ok"));
        }

        // ------------------------------------------------------------------
        // sound anim/animnoseq — registration table (FUN_004cf770)
        // ------------------------------------------------------------------

        [Test]
        public void Anim_RegistersDedupedEntriesPerFrame()
        {
            var (interp, ctx) = Host();
            var au = ctx.Audio!;
            interp.Eval("sound bind lbr_beissen data/sound/Brut/lbr_beissen.wav");
            // soundinit.tcl:493 — vol 0.9, freq 1.0, volvar 0, freqvar 0
            interp.Eval("sound anim brut.beissen_a 6 lbr_beissen any any 0.9 1.0 0 0");
            Assert.IsTrue(au.AnimSounds.ContainsKey("brut.beissen_a"));
            var frames = au.AnimSounds["brut.beissen_a"];
            Assert.IsTrue(frames.ContainsKey(6));
            Assert.AreEqual(1, frames[6].Count);
            var e = au.AnimSoundEntries[frames[6][0]];
            Assert.AreEqual("lbr_beissen", e.Sound);
            Assert.AreEqual(0.9f, e.Volume, 1e-4f);
            Assert.AreEqual(1.0f, e.Freq, 1e-4f);
            Assert.IsFalse(e.NoSeq);

            // identical re-registration: entry table dedups (FUN_004cf770:50-69) but the frame
            // list appends AGAIN (:113-117) — doubles the pick weight.
            interp.Eval("sound anim brut.beissen_a 6 lbr_beissen any any 0.9 1.0 0 0");
            Assert.AreEqual(1, au.AnimSoundEntries.Count);
            Assert.AreEqual(2, frames[6].Count);

            // animnoseq sets the noseq byte (entry[5], suppressed during sequences)
            interp.Eval("sound animnoseq brut.beissen_a 3 lbr_beissen any any 0.5 1.0 0 0");
            var e2 = au.AnimSoundEntries[au.AnimSounds["brut.beissen_a"][3][0]];
            Assert.IsTrue(e2.NoSeq);
        }

        [Test]
        public void Anim_MaterialAndRoomValidatedButDiscarded()
        {
            var (interp, ctx) = Host();
            interp.Eval("sound bind s x.wav");
            // invalid material / room → Tcl error (FUN_00643a60:374-442)
            Assert.AreEqual("1", interp.Eval("catch {sound anim a.b 0 s bogus}"));
            Assert.AreEqual("1", interp.Eval("catch {sound anim a.b 0 s any bogusroom}"));
            // every legal material/room accepted, value not stored anywhere (dead args in the
            // shipped exe — FUN_004cf770 has no such params)
            interp.Eval("sound anim a.b 0 s stone tunnel 0.7 1.0 0 0");
            var e = ctx.Audio!.AnimSoundEntries[ctx.Audio.AnimSounds["a.b"][0][0]];
            Assert.AreEqual(0.7f, e.Volume, 1e-4f);
        }

        [Test]
        public void Anim_UnknownSound_ErrorsInStrictHost()
        {
            var (interp, ctx) = Host(strict: true);
            // FUN_00643a60:351-358 "unknown sound" — the sample must be bound first
            Assert.AreEqual("1", interp.Eval("catch {sound anim a.b 0 neverbound}"));
            Assert.IsFalse(ctx.Audio!.AnimSounds.ContainsKey("a.b"));
        }

        // ------------------------------------------------------------------
        // TryPickAnimSound — random selection + noseq + variance (FUN_005cb9a0/FUN_005d0820)
        // ------------------------------------------------------------------

        [Test]
        public void Pick_RandomAmongFrameEntries_AndNoSeqSuppression()
        {
            var (interp, ctx) = Host();
            var au = ctx.Audio!;
            // feuerstelle.brennt frame 0 holds 8 entries; playback rotates them at random
            // (soundinit.tcl:1019-1026; FUN_005cb9a0:153-166 rand%count)
            for (int i = 1; i <= 8; i++)
            {
                interp.Eval($"sound bind Feuerstelle_{i} data/sound/produktion/Feuerstelle_{i}.wav");
                interp.Eval($"sound anim feuerstelle.brennt 0 Feuerstelle_{i} any any 0.9 1.0 0 0");
            }
            var rng = new System.Random(1234);
            var seen = new HashSet<string>();
            for (int i = 0; i < 200; i++)
                if (au.TryPickAnimSound("feuerstelle.brennt", 0, false, rng, out var e, out _, out _))
                    seen.Add(e.Sound);
            Assert.AreEqual(8, seen.Count, "all 8 crackle samples must be reachable");

            // noseq entries are silent while a sequence runs (FUN_005cb9a0:171)
            interp.Eval("sound bind hungrig x.wav");
            interp.Eval("sound animnoseq mann.hungrig 5 hungrig any any 0.65 1.0 0 0");
            Assert.IsTrue(au.TryPickAnimSound("mann.hungrig", 5, false, rng, out _, out float v, out _));
            Assert.AreEqual(0.65f, v, 1e-4f);
            Assert.IsFalse(au.TryPickAnimSound("mann.hungrig", 5, true, rng, out _, out _, out _));

            // `sound animsound 0` disables everything (global+0x95d8)
            interp.Eval("sound animsound 0");
            Assert.IsFalse(au.TryPickAnimSound("mann.hungrig", 5, false, rng, out _, out _, out _));
            Assert.AreEqual("1", interp.Eval("sound animsound 1"));
        }

        [Test]
        public void Pick_VolumeVariance_AddsRandom01TimesVar()
        {
            var (interp, ctx) = Host();
            interp.Eval("sound bind s x.wav");
            // volvar 0.4 → effVol ∈ [0.5, 0.9) (FUN_005d0820:16-19: vol += rand01 * volvar)
            interp.Eval("sound anim a.b 2 s any any 0.5 1.0 0.4 0");
            var rng = new System.Random(7);
            for (int i = 0; i < 50; i++)
            {
                Assert.IsTrue(ctx.Audio!.TryPickAnimSound("a.b", 2, false, rng, out _, out float v, out _));
                Assert.That(v, Is.InRange(0.5f, 0.9f));
            }
        }

        // ------------------------------------------------------------------
        // sound play / playglobal — the one-shot queue
        // ------------------------------------------------------------------

        [Test]
        public void Play_QueuesPositionalShots_NoneLost()
        {
            var (interp, ctx) = Host();
            var au = ctx.Audio!;
            interp.Eval("sound play boom 0.8 {12 34 15}");
            interp.Eval("sound playglobal bombpl 2");
            Assert.AreEqual(2, au.OneShotCount);
            Assert.AreEqual(2, au.PendingOneShots.Count, "both shots queued (the old LastOneShot poll dropped one)");
            var first = au.PendingOneShots.Dequeue();
            Assert.AreEqual("boom", first.Name);
            Assert.IsTrue(first.Positional);
            Assert.AreEqual(new Vector3(12, 34, 15), first.Pos);
            Assert.AreEqual(0.8f, first.Volume, 1e-4f);
            var second = au.PendingOneShots.Dequeue();
            Assert.AreEqual("bombpl", second.Name);
            Assert.IsFalse(second.Positional);   // playglobal — no falloff/pan (FUN_004d1bb0)
        }

        // ------------------------------------------------------------------
        // adaptive_sound — registration semantics (FUN_00642d70)
        // ------------------------------------------------------------------

        [Test]
        public void Adaptive_VolfactPercent_MarkerRadius_Delmarker_AutofightPrimary()
        {
            var (interp, ctx) = Host();
            var au = ctx.Audio!;
            // volfact takes an INT PERCENT (FUN_00642d70:139-152) — scripts duck with 0..100
            interp.Eval("adaptive_sound volfact 40");
            Assert.AreEqual(0.4f, au.MusicBaseVolume, 1e-4f);
            // marker radius arg (FUN_00642d70:291-294), default 50 (:521)
            interp.Eval("adaptive_sound marker cave {3 0 5} 1000");
            interp.Eval("adaptive_sound marker feen {9 9 5}");
            Assert.AreEqual(2, au.MusicMarkers.Count);
            Assert.AreEqual(1000f, au.MusicMarkers[0].Radius, 1e-3f);
            Assert.AreEqual(50f, au.MusicMarkers[1].Radius, 1e-3f);
            // delmarker removes the marker within 1.0 of pos (FUN_00642d70:313-318)
            interp.Eval("adaptive_sound delmarker {3.5 0 5}");
            Assert.AreEqual(1, au.MusicMarkers.Count);
            Assert.AreEqual("feen", au.MusicMarkers[0].Theme);
            interp.Eval("adaptive_sound delmarker {20 20 0}");   // too far — no-op
            Assert.AreEqual(1, au.MusicMarkers.Count);
            // primary <theme> is the no-marker fallback THEME (FUN_00642d70:272-281), not a mode
            interp.Eval("adaptive_sound primary start");
            Assert.AreEqual("start", au.PrimaryTheme);
            Assert.AreEqual("start", au.CurrentTheme);   // first primary kicks playback off
            // autofight <theme> stores the combat switch target (FUN_00642d70:419-426)
            interp.Eval("adaptive_sound autofight trolle");
            Assert.AreEqual("trolle", au.AutoFightTheme);
        }

        // ------------------------------------------------------------------
        // DigglesAnimator.AnimFrameAdvanced — the anim-SFX seam
        // ------------------------------------------------------------------

        DigglesAnimator MakeAnimator(string objName, string clipName, int frames)
        {
            var go = new GameObject("anim");
            _gos.Add(go);
            var an = go.AddComponent<DigglesAnimator>();
            an.Interpolate = false;
            an.AddClip(new DigglesAnimClip
            {
                Name = clipName,
                SourceObjName = objName,
                Frames = new Mesh[frames],
            });
            return an;
        }

        [Test]
        public void AnimatorEvent_FiresPerFrame_WithNativeIterationContract()
        {
            DigglesAnimator.PublishTimeScale(1.0);
            var an = MakeAnimator("brut", "beissen_a", 8);
            var events = new List<(int prev, int cur)>();
            void H(DigglesAnimator a, DigglesAnimClip c, int prev, int cur)
            {
                Assert.AreEqual("brut", c.SourceObjName);
                Assert.AreEqual("beissen_a", c.Name);
                events.Add((prev, cur));
            }
            DigglesAnimator.AnimFrameAdvanced += H;
            try
            {
                an.SetAnim("beissen_a", 0, DigglesAnimator.AnimMode.Once);
                Assert.AreEqual(1, events.Count, "clip start applies frame 0 (prev = -1)");
                Assert.AreEqual((-1, 0), events[0]);
                // 10 FPS native clock: 0.1 s per frame (frame = start + elapsed*10)
                for (int i = 0; i < 7; i++) an.TickClock(0.1f);
                Assert.AreEqual(8, events.Count, "one event per entered frame");
                for (int i = 1; i < 8; i++) Assert.AreEqual((i - 1, i), events[i]);
                // a lag spike skips frames in ONE event; the subscriber replays the native
                // (prev, cur) exclusive + cur loop (FUN_005cb9a0:136-220)
                events.Clear();
                an.SetAnim("beissen_a", 0, DigglesAnimator.AnimMode.Once);
                events.Clear();
                an.TickClock(0.35f);
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual((0, 3), events[0]);
            }
            finally { DigglesAnimator.AnimFrameAdvanced -= H; }
        }

        [Test]
        public void AnimatorEvent_DrivesAnimSoundTable_RightSampleAtRightFrame()
        {
            DigglesAnimator.PublishTimeScale(1.0);
            var (interp, ctx) = Host();
            var au = ctx.Audio!;
            interp.Eval("sound bind lbr_beissen data/sound/Brut/lbr_beissen.wav");
            interp.Eval("sound anim brut.beissen_a 6 lbr_beissen any any 0.9 1.0 0 0");

            var an = MakeAnimator("brut", "beissen_a", 8);
            var rng = new System.Random(42);
            var fired = new List<(int frame, string sound, float vol)>();
            // mirror WorldSoundService.OnAnimFrame: native frame loop + AudioState pick
            void H(DigglesAnimator a, DigglesAnimClip c, int prev, int cur)
            {
                string qualified = c.SourceObjName + "." + c.Name;
                void Fire(int f)
                {
                    if (au.TryPickAnimSound(qualified, f, ctx.SequenceActive, rng,
                            out var e, out float v, out _))
                        fired.Add((f, e.Sound, v));
                }
                if (cur > prev + 1) for (int f = prev + 1; f < cur; f++) Fire(f);
                Fire(cur);
            }
            DigglesAnimator.AnimFrameAdvanced += H;
            try
            {
                an.SetAnim("beissen_a", 0, DigglesAnimator.AnimMode.Once);
                for (int i = 0; i < 7; i++) an.TickClock(0.1f);
            }
            finally { DigglesAnimator.AnimFrameAdvanced -= H; }
            Assert.AreEqual(1, fired.Count, "exactly ONE sound across the whole clip");
            Assert.AreEqual(6, fired[0].frame, "the bite lands on frame 6 (soundinit.tcl:493)");
            Assert.AreEqual("lbr_beissen", fired[0].sound);
            Assert.AreEqual(0.9f, fired[0].vol, 1e-4f);
        }

        // ------------------------------------------------------------------
        // the real boot: soundinit.tcl + adaptiveinit.tcl
        // ------------------------------------------------------------------

        [Test]
        public void RealSoundinit_BindsAndAnimTables_Register()
        {
            if (!HasData) Assert.Ignore("original data not present");
            // tolerant host = EXACTLY the real Play boot's flags (DigglesEngine.Build sets
            // TolerateMissingFiles=true) — this test certifies the path the game actually runs.
            var (interp, ctx) = Host(strict: false, dataRoot: DataRoot);
            interp.Eval("call scripts/init/soundinit.tcl");
            var au = ctx.Audio!;
            // ~1000+ binds across soundinit + z_soundinit + t_soundinit
            Assert.That(au.SoundBindings.Count, Is.GreaterThan(950), "sample binds");
            Assert.AreEqual("data/sound/produktion/Steintuer_auf.wav", au.ResolveSound("Steintuer_auf"));
            // anim tables: ~2900 sound anim/animnoseq lines across soundinit + z_ + t_
            Assert.That(au.AnimSounds.Count, Is.GreaterThan(800), "distinct anims with SFX");
            // dwarf footsteps (z_soundinit: mann.gehen_loop 0 gehen_l … 0.5 1.0 0 0)
            Assert.IsTrue(au.AnimSounds.ContainsKey("mann.gehen_loop"), "mann walk loop");
            var step = au.AnimSoundEntries[au.AnimSounds["mann.gehen_loop"][0][0]];
            Assert.AreEqual("gehen_l", step.Sound);
            Assert.AreEqual(0.5f, step.Volume, 1e-4f);
            // female mirror table exists (frau.*)
            Assert.IsTrue(au.AnimSounds.ContainsKey("frau.gehen_loop"), "frau walk loop");
            // the 8-way fire crackle rotation (soundinit.tcl:1019-1026)
            Assert.AreEqual(8, au.AnimSounds["feuerstelle.brennt"][0].Count);
        }

        [Test]
        public void RealAdaptiveinit_ThemesRegister()
        {
            if (!HasData) Assert.Ignore("original data not present");
            var (interp, ctx) = Host(strict: false, dataRoot: DataRoot);
            interp.Eval("call scripts/init/adaptiveinit.tcl");
            var au = ctx.Audio!;
            Assert.That(au.Themes.Count, Is.GreaterThanOrEqualTo(40), "themes registered");
            Assert.AreEqual("data/music/m076.mp3", au.Themes["menue"].Intro);
            Assert.AreEqual("data/music/m009.mp3", au.Themes["cave"].Intro);
            Assert.That(au.Themes["cave"].Standard.Count, Is.GreaterThanOrEqualTo(11));
            Assert.AreEqual("data/music/m001.mp3", au.Themes["trolle"].Fight);
        }
    }
}
