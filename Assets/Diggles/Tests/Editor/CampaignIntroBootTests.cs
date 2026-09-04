#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    // Campaign-intro boot timeline — the original's observable pre-intro window, end to end.
    //
    // Ground truth (decompile + unmodified scripts):
    // • The sequence freeze is per-object and gates both halves: while seqMode is on
    // (set_sequence 1 → DAT_00772400+0x8c), CGameThread::SrvDoThink drains an object's event
    // ring and thinks its state only when `(flags & 0x1000000) && (!seqMode || (flags &
    // 0x200000))` (FUN_005d38c0:185-187). Deferred, not dropped. Bit 0x1000000 =
    // `set_activegameplay` (FUN_0069acb0 — Tcl-toggled, default on; story scripts clear it for
    // swallowed gnomes only). There is no side channel: CObj_HandleSingleEvent's only caller
    // chain is SrvDoThink → FUN_005cabe0 — timer events freeze through the same gate.
    // • Preset actors do not get the 0x200000 bit: `preset_actors` is a plain add_actor loop
    // (sequencer.tcl:18-23) and add_actor (:467-505) does remove_parked_obj + `action wait 0.0`
    // + state_disable — never set_sequenceactive (only sq_activate does,:1368).
    // • The original's real boot timeline (the acceptance bar — the player sees all of it):
    // t=0 level up and live; founders idle in place (zwerg.tcl `state idle` ends every
    // pass with set_idle_anim + `action this wait 1 {state_enable this}`,:316-318;
    // a virgin camp has no work → work_dispatch falls straight to work_idle,
    // z_work_states.tcl:164-168 — no walk is reachable before idletimeout>5).
    // t=3 every dwarf's evt_timer0 init fires on screen (zwerg.tcl:183 `-attime 3` →
    // z_methods.tcl init: auto_choose_gender, frau reskin, clothing/haircolor).
    // The flip is not instant — the player watches it happen "a few seconds in".
    // t=5 Trigger_Urw_006's timer matures (`-attime [expr [gettime]+5]`,
    // Trigger_Urwald.tcl:928) → urw_3 proximity trigger (:934-940) → fires at once
    // (founders within range 40) → sequencer_activate → set_sequence 1.
    // t≈11 urw_3 beams all five actors into the WiggleStart pens (urw_3.tcl:19,
    // shipped) — the sequence presets actor positions; any residual drift is masked.
    // • Port contract under test: no instant boot window (a prior regression compressed
    // t=0..5 into one frame — instant flip + 5 hidden seconds of AI scatter). The runtime
    // stages the intro at GameTime+5 (DigglesWorldRuntime.StageIntro/Update) and the sim runs
    // those seconds live. These tests drive the same timeline at the sim level.
    //
    // The host boots the real, unmodified dwarf class (zwerg.tcl pulls z_methods/z_events/z_anims/
    // z_procs/… itself) + the real sequencer — the same files the campaign runs.
    public sealed class CampaignIntroBootTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // Intro-shaped sequencer trigger — the same dual-load pattern DigglesWorldRuntime.PlayCutscene
        // stages (_CutsceneTest_Seq) and the game's own Trigger_* classes use.
        const string IntroSeqClass = @"
            def_class IntroSeq none dummy 0 {} {
                call scripts/classes/story/sequencer.tcl
                obj_init { call scripts/classes/story/sequencer.tcl }
                method run {} { sequencer_activate }
            }";

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) BuildCampaignHost(int seed)
        {
            UnityEngine.Random.InitState(seed);   // pins irandom (clothing/haircolor picks)
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            interp.RegisterCommand("puts", (i, a) => "");
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;    // Dummy_* hair/glasses props synthesize as placeholders
            ctx.TolerateMissingFiles = true;
            ctx.IsStoryMgr = true;              // campaign-shaped: is_campaign=1 → hatcolor 0 (z_methods.tcl:193-195)
            var sim = new DigglesGameSim(ctx);
            interp.Eval("call scripts/classes/zwerg/zwerg.tcl");   // the real dwarf class
            interp.Eval(IntroSeqClass);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string pos)
            => ctx.GetEntity(interp.Eval($"new Zwerg \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static string Var(DigglesTclEntity e, string name)
            => e.VarsScope.Vars.TryGetValue(name, out var v) ? v : "<none>";

        static bool Initialized(DigglesTclEntity e) => Var(e, "gnome_initialized") == "1";

        // Single CommandTrace hook recording (a) set_textureanimation writes per (handle, submesh)
        // — headless has no renderer, so the texture half of the init is asserted at the command
        // surface (channels 0/1 = clothing nibbles, 2 = haircolor; z_methods.tcl:415-424) — and
        // (b) every `action <obj> walk|run …` issued, the chokepoint all of z_dignwalk's
        // walk_pos/walk_random funnel through (pre-wander detector).
        sealed class BootRecorder
        {
            public readonly HashSet<(int handle, int submesh)> TextureWrites = new();
            public readonly List<(int handle, string type)> MoveActions = new();
            public void Attach(TclInterpreter interp, DigglesTclContext ctx) =>
                interp.CommandTrace = (name, args, result) =>
                {
                    // z_methods calls `set_textureanimation this 0 …` — resolve the literal `this`
                    // through the live entity context (the init runs in the dwarf's object scope).
                    int Handle(string tok) => tok == "this" ? (ctx.This?.Handle ?? -1)
                                            : int.TryParse(tok, out var ph) ? ph : -1;
                    if (name == "set_textureanimation" && args.Length >= 3)
                    {
                        int h = Handle(args[1]);
                        if (h >= 0 && int.TryParse(args[2], out var sm)) TextureWrites.Add((h, sm));
                    }
                    else if (name == "action" && args.Length >= 3 && (args[2] == "walk" || args[2] == "run"))
                    {
                        MoveActions.Add((Handle(args[1]), args[2]));
                    }
                };
        }

        static int StartIntro(TclInterpreter interp, List<int> dwarves, string seqPath, string pos)
        {
            int trig = int.Parse(interp.Eval($"new IntroSeq \"\" \"{pos}\" \"0 0 0\"").Trim());
            interp.Eval($"call_method {trig} preset_actors [list {string.Join(" ", dwarves)}]");
            interp.Eval($"call_method {trig} set_sequencescript {{{seqPath}}}");
            interp.Eval($"call_method {trig} run");
            return trig;
        }

        static string WriteSeq(string name, string body)
        {
            string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"dgl_{name}_{System.DateTime.Now.Ticks}.tcl").Replace('\\', '/');
            System.IO.File.WriteAllText(p, body);
            return p;
        }

        // ---- the faithful timeline: live pre-roll → t=3 flip → t=5 intro → dressed throughout ----
        [Test]
        public void NewGameBoot_LivePreRoll_FlipsAtT3_IntroAtT5_DressedThroughout()
        {
            var (interp, ctx, sim) = BuildCampaignHost(seed: 20260610);
            var rec = new BootRecorder();
            rec.Attach(interp, ctx);

            // NewGame shape: fresh clock, spawn the founders, zero steps so far. Spawned ≥2 units
            // apart — act_when_idle's spacing shuffle (`walk_random` when another Zwerg overlaps
            // the ±0.6 bounding box, z_procs.tcl:1300-1309) is legitimately reachable in the
            // original too; the no-pre-wander law below holds for non-overlapping founders.
            sim.GameTime = 0.0;
            var dwarves = new List<DigglesTclEntity>();
            foreach (var p in new[] { "30 40 8", "32 40 8", "34 40 8", "36 40 8" })
                dwarves.Add(Spawn(interp, ctx, p));
            var spawnPos = dwarves.ConvertAll(d => d.Position);
            foreach (var d in dwarves)
                Assert.AreEqual("unset", Var(d, "gnome_gender"),
                    "obj_init must leave gender for the t=3 init timer (zwerg.tcl:33,183)");

            // t=0 → 2.9: the player watches this — nothing flips yet (the init timer is absolute
            // `-attime 3`, zwerg.tcl:183). An instant flip here is a regression.
            for (int i = 0; i < 29; i++) sim.Step(0.1);
            foreach (var d in dwarves)
                Assert.IsFalse(Initialized(d),
                    $"#{d.Handle}: gender/clothes must NOT flip before t=3 — the original's flip is " +
                    "visibly 'a few seconds in', never instant (zwerg.tcl:183 -attime 3)");

            // t=2.9 → 5.0: the init matures and delivers live, before any sequence exists.
            for (int i = 0; i < 21; i++) sim.Step(0.1);

            int females = 0, males = 0;
            foreach (var d in dwarves)
            {
                Assert.IsTrue(Initialized(d),
                    $"#{d.Handle}: the t=3 init (zwerg.tcl:183 → z_methods init) must have completed " +
                    $"during the live pre-roll | gender={Var(d, "gnome_gender")}");
                if (d.Gender == "female") females++;
                if (d.Gender == "male") males++;
                Assert.IsTrue(rec.TextureWrites.Contains((d.Handle, 0)) && rec.TextureWrites.Contains((d.Handle, 1)),
                    $"#{d.Handle}: clothing texture channels 0/1 must be written by init (z_methods.tcl:416-417)");
                Assert.IsTrue(rec.TextureWrites.Contains((d.Handle, 2)),
                    $"#{d.Handle}: haircolor channel 2 must be written by init (z_methods.tcl:420)");
            }
            // auto_choose_gender balances the founders deterministically: m, f, m, f (engine
            // FUN_00676790 — females≥males → male; z_methods.tcl:228/239).
            Assert.AreEqual(2, females, "founder balance must yield 2 females");
            Assert.AreEqual(2, males, "founder balance must yield 2 males");
            foreach (var d in dwarves)
                if (d.Gender == "female")
                    Assert.IsTrue(d.AlternateAnimDb,
                        $"#{d.Handle}: a female founder must be frau-reskinned pre-intro " +
                        "(set_alternateanimdb this true, z_methods.tcl:252)");

            // No pre-wander through the whole pre-roll: non-overlapping founders idle in place
            // (set_idle_anim + wait-1 discipline, zwerg.tcl:316-318; virgin camp → work_dispatch
            // falls to work_idle, z_work_states.tcl:164-168). This is what the 5 hidden ticks of
            // the instant window broke in the live game.
            Assert.IsEmpty(rec.MoveActions,
                "no founder may start a walk/run during the t=0..5 pre-roll: " +
                string.Join(", ", rec.MoveActions.ConvertAll(m => $"#{m.handle}:{m.type}")));
            for (int i = 0; i < dwarves.Count; i++)
                Assert.AreEqual(spawnPos[i], dwarves[i].Position,
                    $"#{dwarves[i].Handle}: position must hold through the pre-roll " +
                    "(only the SEQUENCE moves actors — urw_3's shipped beam)");

            // t=5: the intro starts (urw_3-shaped: real sequencer, founders preset as actors) —
            // the earliest the original can engage it (Trigger_Urwald.tcl:928).
            string seqPath = WriteSeq("intro", "do_wait time 3\n");
            try
            {
                var handles = dwarves.ConvertAll(d => d.Handle);
                StartIntro(interp, handles, seqPath, "33 40 8");
                Assert.IsTrue(ctx.SequenceActive, "the intro must engage sequence mode (set_sequence 1)");

                // Mid-cutscene: the gnomes the player sees are dressed — gender/reskin/clothes all
                // survived into the sequence (nothing left to burst at skip).
                for (int i = 0; i < 10; i++) sim.Step(0.1);
                Assert.IsTrue(ctx.SequenceActive, "still mid-intro");
                foreach (var d in dwarves)
                {
                    Assert.IsTrue(Initialized(d), $"#{d.Handle}: initialized THROUGH the cutscene");
                    Assert.IsFalse(d.SequenceActiveFlag,
                        $"#{d.Handle}: preset actors must NOT carry the sequenceactive bit — " +
                        "add_actor never sets it (sequencer.tcl:467-505); the fix is boot ORDER, not the bit");
                    if (d.Gender == "female")
                        Assert.IsTrue(d.AlternateAnimDb, $"#{d.Handle}: frau reskin visible during the intro");
                }

                // Let the scene end (do_wait 3 → EOF → shut_sequencer) — actors must be released.
                int steps = 0;
                for (; steps < 2000 && ctx.SequenceActive; steps++) sim.Step(0.05);
                Assert.IsFalse(ctx.SequenceActive, "the intro must shut cleanly (set_sequence 0)");
                foreach (var d in dwarves)
                    Assert.GreaterOrEqual(d.StateEnabledCount, 0,
                        $"#{d.Handle}: shut_sequencer must state_enable the actors back (sequencer.tcl:545-546)");
            }
            finally { try { System.IO.File.Delete(seqPath); } catch { } }
        }

        // ---- the race this guards against: intro at t=0 defers the init chain (faithful freeze),
        // and it bursts after the sequence — the exact "snaps correct on skip" symptom.
        // Covers both halves of the fix: (1) don't "fix" this by setting the bit on actors —
        // the original defers actors' events too (timer delivery has no side channel:
        // CObj_HandleSingleEvent ← FUN_005cabe0 ← the gated SrvDoThink loop only);
        // (2) the runtime must therefore never PlayCutscene before the live t=5 mark. ----
        [Test]
        public void IntroAtTimeZero_DefersGenderInit_UntilTheSequenceEnds()
        {
            var (interp, ctx, sim) = BuildCampaignHost(seed: 20260611);
            sim.GameTime = 0.0;
            var dwarves = new List<DigglesTclEntity>();
            foreach (var p in new[] { "30 40 8", "32 40 8", "34 40 8", "36 40 8" })
                dwarves.Add(Spawn(interp, ctx, p));

            string seqPath = WriteSeq("intro0", "do_wait time 6\n");
            try
            {
                // Zero sim steps between spawn and sequence start — the old NewGame shape.
                StartIntro(interp, dwarves.ConvertAll(d => d.Handle), seqPath, "33 40 8");
                Assert.IsTrue(ctx.SequenceActive);

                // 5 s in: the t=3 init timers matured but the per-object freeze
                // (FUN_005d38c0:185-187) defers delivery — undressed gnomes mid-intro.
                for (int i = 0; i < 50; i++) sim.Step(0.1);
                Assert.IsTrue(ctx.SequenceActive,
                    "scene must still be running at t=5 (do_wait time 6) — else the deferral assert is vacuous");
                foreach (var d in dwarves)
                {
                    Assert.IsFalse(Initialized(d),
                        $"#{d.Handle}: with the intro at t=0 the init chain must be DEFERRED " +
                        "(events gated while seqMode && !sequenceactive) — the faithful freeze");
                    Assert.AreEqual("neutral", d.Gender,
                        $"#{d.Handle}: no gender mid-sequence (set_objgender runs inside the deferred init)");
                }

                // Sequence ends → the queued events deliver (deferred, not dropped) — the
                // "everything snaps correct after skipping" half of the fix.
                int steps = 0;
                for (; steps < 2000 && ctx.SequenceActive; steps++) sim.Step(0.05);
                Assert.IsFalse(ctx.SequenceActive, "scene must end (do_wait 6 → EOF → shut_sequencer)");
                for (int i = 0; i < 20; i++) sim.Step(0.1);
                foreach (var d in dwarves)
                    Assert.IsTrue(Initialized(d),
                        $"#{d.Handle}: the deferred init must BURST after set_sequence 0 " +
                        "(native ring drain — deferred, not dropped)");
            }
            finally { try { System.IO.File.Delete(seqPath); } catch { } }
        }

        // ---- the sequence presets positions: urw_3's shipped beam (urw_3.tcl:19) — the
        // sequencer can move a frozen actor (do_action beam = `action wait 0` + set_pos,
        // sequencer.tcl:1813-1816), which is what masks any pre-roll drift in the original.
        // Wrong actor positions at the camera's opening shot ⇒ this path is broken. ----
        [Test]
        public void SequenceBeam_PresetsActorPosition_WhileTheFreezeHolds()
        {
            var (interp, ctx, sim) = BuildCampaignHost(seed: 7);
            sim.GameTime = 0.0;
            var d0 = Spawn(interp, ctx, "30 40 8");
            var d1 = Spawn(interp, ctx, "32 40 8");

            // Live pre-roll first (the faithful shape — init done before the sequence).
            for (int i = 0; i < 50; i++) sim.Step(0.1);
            Assert.IsTrue(Initialized(d0), "pre-roll must initialize the founders first");

            // urw_3-shaped pen + beam: TriggerPos pen (init_pens: get_pos this = the trigger's own
            // position) moved by a delta, then `do_action beam <pen> 0`.
            string seqPath = WriteSeq("beam",
                "+sq_pen set TestPen TriggerPos\n" +
                "+sq_pen move TestPen {2 0 1}\n" +
                "do_action beam TestPen 0\n" +
                "do_wait time 3\n");
            try
            {
                StartIntro(interp, new List<int> { d0.Handle, d1.Handle }, seqPath, "33 40 8");
                Assert.IsTrue(ctx.SequenceActive);

                // A few beats in, the beam has run: actor 0 snapped to the pen (33+2, 40, 8+1)
                // even though its event ring/state are frozen; actor 1 (not beamed) holds.
                for (int i = 0; i < 20; i++) sim.Step(0.1);
                Assert.That(UnityEngine.Vector3.Distance(new UnityEngine.Vector3(35f, 40f, 9f), d0.Position),
                    Is.LessThan(0.01f),
                    $"do_action beam must set_pos the actor onto the pen mid-freeze (urw_3's preset) | at {d0.Position}");
                Assert.That(UnityEngine.Vector3.Distance(new UnityEngine.Vector3(32f, 40f, 8f), d1.Position),
                    Is.LessThan(0.01f),
                    $"an un-beamed actor must hold position (only the sequence moves actors) | at {d1.Position}");

                int steps = 0;
                for (; steps < 2000 && ctx.SequenceActive; steps++) sim.Step(0.05);
                Assert.IsFalse(ctx.SequenceActive, "beam scene must shut cleanly");
            }
            finally { try { System.IO.File.Delete(seqPath); } catch { } }
        }
    }
}
