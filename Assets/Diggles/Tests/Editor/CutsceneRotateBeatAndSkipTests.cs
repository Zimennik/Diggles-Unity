#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // The/11 Play strike on the menu backdrops, reproduced HEADLESS with Play-shaped
    // actors (real GameObject rigs ⇒ the rotate beat builds the REAL TurnToFaceAction, not the
    // headless WaitAction degrade):
    //
    // A. start_002 (troll mage): the first troll must TURN to P2 and summon the second troll —
    // Play showed an ETERNAL TURN and the second troll never appeared.
    // B. start_008 (gnome meets the trolls): the gnome walks to P_Mitte, must TURN to P_End,
    // get shocked, run away, 3 trolls chase — Play showed the gnome looping ONE anim at the
    // center forever.
    // C. skipping the stuck scene left the gnome behind WITH A SWAPPED HAT and wedged every
    // later menu cutscene.
    //
    // SCRIPT-SIDE GROUND TRUTH (sequencer.tcl + the scene scripts):
    // • Both A and B stall on the SAME beat shape: `do_action rotate <pen> 0` issued while the
    // trigger's wait set includes actor 0 (`sq_wait 0` — start_002.tcl:28→31,
    // start_008.tcl:25→27). do_action (sequencer.tcl:1722) computes the absolute angle via
    // get_anglexz (:1811), calls `sequence_disable $nr` (enable_list[nr]=1,:1828) and
    // issues `action $actor rotate <ang> {catch {call_method <trigger> seq_enable $nr}} {}`
    // (:1830). The ONLY thing the script then awaits is that finishcode: eval_sequence
    // (:256-265) returns while wait_for[i]==1 && enable_list[i]==1; seq_enable (:47-73) →
    // sequence_enable → enable_list[i]=0. No event, no timer, no other gate. So the Play
    // "eternal turn" ⇒ the rotate ACTION never finishes (its finishcode never fires) — the
    // action side wedges, not a script gate.
    // • The skip drain (eval_sequence:228-239) runs `while {[parse_next 1]} {}` INSIDE one
    // trigger state-think — it never waits for actor actions, so a wedged rotate cannot block
    // it. parse_next:597 defaults plusfound=1 ("Test"), so the drain EVALS every remaining
    // non-'-' line: for start_008 that includes the 3 troll summons, `do_script change
    // st008_end_2 1` (chance 1.0 → the drain CONTINUES into st008_end_2.tcl) and its
    // Axt_3/Schild_3/Dummy_Muetze_kampf_03_a summon+link block — the "gnome changed hat" the
    // user saw on skip is FAITHFUL drain behaviour. The bug is only if the trailing
    // `+sq_object delete all` / shut_sequencer don't finish: natively the drain "terminates"
    // the stuck action by DELETING its owner.
    public sealed class CutsceneRotateBeatAndSkipTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // unq_menue.tcl markers after MapTemplateSet 25 28 (same constants as MenuCutsceneTests).
        static readonly Vector3 MarkerZwerg = new Vector3(45.769f, 42.5f, 12.96f);
        static readonly Vector3 MarkerZwergTmp = new Vector3(24.36f, 42.5f, 12.86f);

        const string MenuSeqClass = @"
            def_class MenuSeq none dummy 0 {} {
                call scripts/classes/story/sequencer.tcl
                obj_init {
                    call scripts/classes/story/sequencer.tcl
                }
                method run {} { sequencer_activate }
                method destroy_overload {} {}
            }";

        GameObject? _root;
        RigSpawner? _spawner;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
            _spawner = null;
        }

        // ------------------------------------------------------------------
        // Play-shaped entity spawner: outer GO (DigglesObject) + inner "Model"
        // (DigglesAnimator + DigglesAnimController) with the mann-shaped clip set —
        // walk/run banks with the REAL |vecA| root motions, the drehen turn slots and
        // `standard` — so `action <obj> rotate` takes the REAL TurnToFaceAction path
        // (CmdAction requires a DigglesObject on ent.GameObject) and its Settle can
        // resolve the `standard` idle. Positions use the RuntimeWorldSpawner native→world
        // mapping so the sim's phase-2c transform→Position sync round-trips exactly.
        // ------------------------------------------------------------------
        sealed class RigSpawner : IDigglesEntitySpawner
        {
            readonly Transform _root;
            public RigSpawner(Transform root) { _root = root; }

            // Fault injection for the skip-leak test: throw ONCE when destroying the GO whose
            // name starts with this prefix (a Play-side render-teardown failure stand-in).
            public string? ThrowOnDestroyOfPrefix;
            bool _destroyThrown;

            public GameObject? Spawn(DigglesTclEntity ent, Transform parent)
            {
                var outer = new GameObject($"{ent.Class?.Name ?? "?"}#{ent.Handle}");
                outer.transform.SetParent(parent, false);
                outer.transform.localPosition = RuntimeWorldSpawner.NativeToLocal(ent.Position);

                var model = new GameObject("Model");
                model.transform.SetParent(outer.transform, false);
                var anim = model.AddComponent<DigglesAnimator>();
                var ctrl = model.AddComponent<DigglesAnimController>();
                ctrl.Animator = anim;

                anim.AddClip(Clip("standard", 2));
                anim.AddClip(Clip("gehen_start", 6, new Vector3(0f, -16.2218f, 0f)));
                anim.AddClip(Clip("gehen_loop", 10, new Vector3(0f, -16.2218f, 0f)));
                anim.AddClip(Clip("gehen_end", 6));
                anim.AddClip(Clip("laufen_schnell_start", 3, new Vector3(0f, -34.2867f, 0f)));
                anim.AddClip(Clip("laufen_schnell_loop", 10, new Vector3(0f, -34.2867f, 0f)));
                anim.AddClip(Clip("laufen_schnell_end", 6));
                // Turn pairs: 4/5 frames = the FUN_00416c00 +0x214 preset, so TurnToFaceAction's
                // step = span/frames matches the native nominal rates.
                anim.AddClip(Clip("drehen_links", 4));
                anim.AddClip(Clip("drehen_rechts", 4));
                anim.AddClip(Clip("drehen_links_180", 5));
                anim.AddClip(Clip("drehen_rechts_180", 5));

                var baseMap = new Dictionary<string, string>
                {
                    ["standard"] = "standard",
                    ["standstill"] = "standard",
                    ["walk_start"] = "gehen_start",
                    ["walk_loop"] = "gehen_loop",
                    ["walk_stop"] = "gehen_end",
                    ["turn_left_90"] = "drehen_links",
                    ["turn_right_90"] = "drehen_rechts",
                    ["turn_left_180"] = "drehen_links_180",
                    ["turn_right_180"] = "drehen_rechts_180",
                };
                var sets = new Dictionary<int, Dictionary<string, string>>
                {
                    [0] = new()
                    {
                        ["walk_start"] = "gehen_start",
                        ["walk_loop"] = "gehen_loop",
                        ["walk_stop"] = "gehen_end",
                    },
                    [1] = new()
                    {
                        ["walk_start"] = "laufen_schnell_start",
                        ["walk_loop"] = "laufen_schnell_loop",
                        ["walk_stop"] = "laufen_schnell_end",
                    },
                };
                ctrl.SetTables(baseMap, sets);

                var dobj = outer.AddComponent<DigglesObject>();
                // EditMode AddComponent does not run Awake (no [ExecuteAlways]); invoke it so
                // SpawnRotation/SpawnFaceWorld are captured like in Play — without them
                // TurnToFaceAction's facing basis is the invalid zero quaternion.
                typeof(DigglesObject)
                    .GetMethod("Awake", System.Reflection.BindingFlags.Instance
                                      | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(dobj, null);
                return outer;
            }

            static DigglesAnimClip Clip(string name, int frames, Vector3 vecA = default)
            {
                var c = new DigglesAnimClip { Name = name, Frames = new Mesh[frames], MotionOffset = vecA };
                for (int i = 0; i < frames; i++) c.Frames[i] = new Mesh();
                return c;
            }

            public void Destroy(GameObject go)
            {
                if (go == null) return;
                if (!_destroyThrown && ThrowOnDestroyOfPrefix != null
                    && go.name.StartsWith(ThrowOnDestroyOfPrefix, System.StringComparison.Ordinal))
                {
                    _destroyThrown = true;
                    throw new System.Exception("spawner destroy boom (injected)");
                }
                Object.DestroyImmediate(go);
            }

            public void ApplyPosition(GameObject go, Vector3 nativePos)
            {
                if (go == null) return;
                // World-space write (a linked hat is reparented under its wearer's Model, so
                // localPosition would be wrong there) with the SAME mapping phase 2c inverts.
                go.transform.position = _root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nativePos));
            }

            public void ApplyRotation(GameObject go, Vector3 nativeRotRadians) { /* cosmetic here */ }
            public void ApplyAnim(GameObject go, string animName, int startFrame, DigglesAnimator.AnimMode mode) { /* cosmetic here */ }
        }

        // ------------------------------------------------------------------
        // Probe: beat cursor + spawn census + the optional rotate-action wedge.
        // • every `eval <line>` from parse_next is the live beat — keep a tail + the last
        // do_/sq_ line as the "cursor";
        // • every `new` records the spawn census (which troll/hat/prop got summoned);
        // • every `action <h> rotate …` records the INSTALLED action type (proving the test
        // exercises TurnToFaceAction, not the headless degrade) and, when WedgeFirstRotate
        // is armed, swaps the first one for a never-finishing stub — the exact Play failure
        // shape (the turn action never returns false ⇒ finishcode never fires) — while
        // KEEPING ActionFinish pending, as in Play.
        // ------------------------------------------------------------------
        sealed class EternalTurnStub : IEntityAction
        {
            public bool Tick(double dt) => true;   // the Play wedge: never finishes
        }

        sealed class SeqProbe
        {
            public DigglesTclContext? Ctx;
            public bool WedgeFirstRotate;
            public DigglesTclEntity? WedgedEntity;
            public EternalTurnStub? Stub;
            public readonly List<string> NewClasses = new();
            public readonly List<int> NewHandles = new();
            public readonly List<string> RotateActionTypes = new();
            public readonly List<string> EvalTail = new();
            public string LastBeat = "";

            public void Attach(TclInterpreter interp) => interp.CommandTrace = Trace;

            void Trace(string name, string[] args, string result)
            {
                switch (name)
                {
                    case "eval":
                        if (args.Length < 2) break;
                        var line = args[1].Trim();
                        EvalTail.Add(line);
                        if (EvalTail.Count > 24) EvalTail.RemoveAt(0);
                        if (line.StartsWith("do_", System.StringComparison.Ordinal)
                            || line.StartsWith("sq_", System.StringComparison.Ordinal))
                            LastBeat = line;
                        break;

                    case "new":
                        if (int.TryParse(result?.Trim(), out var h))
                        {
                            NewHandles.Add(h);
                            NewClasses.Add(args.Length > 1 ? args[1] : "?");
                        }
                        break;

                    case "action":
                        if (args.Length > 2 && args[2] == "rotate" && Ctx != null)
                        {
                            var ent = Ctx.GetEntity(args[1]);
                            if (ent?.ActiveAction == null) break;
                            RotateActionTypes.Add(ent.ActiveAction.GetType().Name);
                            if (WedgeFirstRotate && WedgedEntity == null)
                            {
                                Stub = new EternalTurnStub();
                                ent.ActiveAction = Stub;   // direct swap: no breakcode fires,
                                WedgedEntity = ent;        // ActionFinish (seq_enable) stays pending
                            }
                        }
                        break;
                }
            }

            public string CensusDelta(int fromIndex)
            {
                var tail = NewClasses.GetRange(fromIndex, NewClasses.Count - fromIndex);
                return string.Join(",", tail);
            }
        }

        // ---- host -------------------------------------------------------

        (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim, int trigger,
         HashSet<int> baseline, SeqProbe probe) BuildMenuWorld(int seed, bool wedgeFirstRotate)
        {
            UnityEngine.Random.InitState(seed);
            var interp = new TclInterpreter();
            interp.TolerateExprErrors = true;
            interp.TolerateMissingVars = true;
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            var sim = new DigglesGameSim(ctx);
            interp.UnknownCommandHandler = (i, a) => "0";
            interp.RegisterCommand("puts", (i, a) => "");

            _root = new GameObject("RotateBeatTestRoot");
            ctx.SpawnParent = _root.transform;
            _spawner = new RigSpawner(_root.transform);
            ctx.EntitySpawner = _spawner;

            interp.Eval(MenuSeqClass);
            interp.Eval($"new Info_Pos_Zwerg \"\" \"{V(MarkerZwerg)}\" \"0 0 0\"");
            interp.Eval($"new Info_Pos_ZwergTmp \"\" \"{V(MarkerZwergTmp)}\" \"0 0 0\"");
            int trigger = int.Parse(interp.Eval(@"set seq [new MenuSeq """" ""0 0 0"" ""0 0 0""]").Trim());
            var baseline = new HashSet<int>(ctx.Entities.Keys);

            var probe = new SeqProbe { Ctx = ctx, WedgeFirstRotate = wedgeFirstRotate };
            probe.Attach(interp);
            return (interp, ctx, sim, trigger, baseline, probe);
        }

        static string V(Vector3 v) =>
            $"{v.x.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"{v.y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"{v.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        static bool Finished(DigglesTclContext ctx, int trigger, string expectCounter = "1")
        {
            var e = ctx.GetEntity(trigger.ToString());
            if (e == null) return true;
            return e.VarsScope.Vars.TryGetValue("sequence_counter", out var sc) && sc == expectCounter;
        }

        // The stall dump the prompt asks for: beat cursor + per-actor action state.
        static string StallDiag(DigglesTclContext ctx, int trigger, DigglesGameSim sim, SeqProbe probe)
        {
            var e = ctx.GetEntity(trigger.ToString());
            if (e == null) return "(trigger gone)";
            var v = e.VarsScope.Vars;
            string G(string k) => v.TryGetValue(k, out var s) ? s : "<none>";
            var actorStates = new List<string>();
            foreach (var ah in TclList.Parse(G("actors")))
            {
                var ae = ctx.GetEntity(ah);
                actorStates.Add(ae == null
                    ? $"#{ah}=<dead>"
                    : $"#{ah}={ae.Class?.Name}:act={(ae.ActiveAction?.GetType().Name ?? "none")}" +
                      $":finishPending={(string.IsNullOrEmpty(ae.ActionFinish) ? "no" : "yes")}");
            }
            return $"beat='{probe.LastBeat}' t={sim.GameTime:F1} seqActive={ctx.SequenceActive} " +
                   $"counter={G("sequence_counter")} wait_for=({G("wait_for")}) enable_list=({G("enable_list")}) " +
                   $"wait_time_var={G("wait_time_var")} actors=[{string.Join(" | ", actorStates)}] " +
                   $"object_list=({G("object_list")}) evalTail=[{string.Join(" ⏎ ", probe.EvalTail)}]";
        }

        static int Count(List<string> list, string item)
        {
            int n = 0;
            foreach (var s in list) if (s == item) n++;
            return n;
        }

        // ------------------------------------------------------------------
        // A. start_002 — with REAL TurnToFaceAction rotate beats, the scene must get past
        // `do_action rotate P2 0` (start_002.tcl:31) and summon the SECOND troll, the
        // hamster, the third troll and the Hand of God, then clean up. Guards the exact
        // Play symptom: "first troll stuck in an eternal turn, second never summoned".
        // ------------------------------------------------------------------
        [Test]
        public void Start002_TrollMage_RealRotateBeatsComplete_SecondTrollSummoned()
        {
            var (interp, ctx, sim, trigger, baseline, probe) = BuildMenuWorld(880201, wedgeFirstRotate: false);
            interp.Eval($"call_method {trigger} set_sequencescript start_002");
            interp.Eval($"call_method {trigger} run");

            int steps = 0;
            for (; steps < 12000 && !Finished(ctx, trigger); steps++) sim.Step(0.05);
            Assert.IsTrue(Finished(ctx, trigger),
                $"start_002 stalled — the Play 'eternal turn' wedge | {StallDiag(ctx, trigger, sim, probe)}");

            // The test must have exercised the PLAY action shape, not the headless degrade:
            // both `do_action rotate` beats (lines 31 and 51) → TurnToFaceAction installs.
            Assert.That(probe.RotateActionTypes, Has.Some.EqualTo("TurnToFaceAction"),
                "the rotate beat must build the REAL TurnToFaceAction (rig spawner broken otherwise — " +
                $"saw [{string.Join(",", probe.RotateActionTypes)}])");
            Assert.That(probe.RotateActionTypes.Count, Is.GreaterThanOrEqualTo(2),
                "start_002 has two rotate beats (P2 for actor 0, P1 for actor 1) — both must issue");

            // Census: Troll(P1) → Troll(P2) → Hamster → Troll → Hand_Gottes (5 summons).
            Assert.AreEqual(3, Count(probe.NewClasses, "Troll"),
                $"all three trolls must be summoned (got: {string.Join(",", probe.NewClasses)}) — " +
                "a rotate wedge parks the cursor before the second troll's summon");
            Assert.That(probe.NewClasses, Does.Contain("Hamster"), "the hamster transmutation beat must run");
            Assert.That(probe.NewClasses, Does.Contain("Hand_Gottes"), "the Hand of God beat must run");

            for (int i = 0; i < 120; i++) sim.Step(0.05);
            AssertNoLeaks(ctx, baseline, "start_002 natural end");
        }

        // ------------------------------------------------------------------
        // B. start_008 — the "dancing gnome" Play report: the gnome walks to P_Mitte, the
        // rotate-to-P_End beat (start_008.tcl:27) completes, the trolls appear and chase.
        // Stuck = the gnome loops one clip at the center and no troll ever spawns.
        // ------------------------------------------------------------------
        [Test]
        public void Start008_GnomeAndTrolls_RotateBeatCompletes_TrollsAppear()
        {
            var (interp, ctx, sim, trigger, baseline, probe) = BuildMenuWorld(880202, wedgeFirstRotate: false);
            interp.Eval($"call_method {trigger} set_sequencescript start_008");
            interp.Eval($"call_method {trigger} run");

            int steps = 0;
            for (; steps < 12000 && !Finished(ctx, trigger); steps++) sim.Step(0.05);
            Assert.IsTrue(Finished(ctx, trigger),
                $"start_008 stalled — the Play 'gnome loops one anim at the center' wedge | {StallDiag(ctx, trigger, sim, probe)}");

            Assert.That(probe.RotateActionTypes, Has.Some.EqualTo("TurnToFaceAction"),
                "the rotate beat must build the REAL TurnToFaceAction");
            Assert.AreEqual(3, Count(probe.NewClasses, "Troll"),
                $"the three chasing trolls must spawn (got: {string.Join(",", probe.NewClasses)})");
            // The chance-1.0 st008_end_2 redirect runs in EVERY natural play: 8 summons total.
            Assert.AreEqual(8, probe.NewHandles.Count,
                $"start_008 + st008_end_2 = 8 summons (got: {string.Join(",", probe.NewClasses)})");

            for (int i = 0; i < 120; i++) sim.Step(0.05);
            AssertNoLeaks(ctx, baseline, "start_008 natural end");
        }

        // ------------------------------------------------------------------
        // The STALL SHAPE (A/B root cause, documented headless): wedge the first rotate
        // action exactly like the Play bug (the action never finishes; its seq_enable
        // finishcode stays pending) and prove the script parks at the rotate beat on the
        // wait_for/enable_list gate — and on NOTHING else. This is the regression net for
        // the TurnToFaceAction termination fix: if a future change re-wedges the turn, the
        // natural-end tests above fail with this exact signature.
        // ------------------------------------------------------------------
        [Test]
        public void Start008_WedgedRotateAction_ParksTheCursorAtTheRotateBeat()
        {
            var (interp, ctx, sim, trigger, _, probe) = BuildMenuWorld(880203, wedgeFirstRotate: true);
            interp.Eval($"call_method {trigger} set_sequencescript start_008");
            interp.Eval($"call_method {trigger} run");

            for (int i = 0; i < 800; i++) sim.Step(0.05);   // 40 game-seconds — far past the scene's span
            Assert.IsNotNull(probe.WedgedEntity, "the rotate beat must have issued (and been wedged)");
            Assert.IsFalse(Finished(ctx, trigger),
                "with a never-finishing rotate action the scene must stall (reproduces the Play wedge)");
            Assert.IsTrue(ctx.SequenceActive, "stalled mid-sequence");

            // The cursor sits ON the rotate beat …
            Assert.AreEqual("do_action rotate P_End 0", probe.LastBeat,
                $"beat cursor must park at the rotate beat | {StallDiag(ctx, trigger, sim, probe)}");
            // … held by the wait_for/enable_list gate (eval_sequence:256-265), with the actor's
            // finishcode still pending — the script awaits ONLY the action's completion callback.
            var trig = ctx.GetEntity(trigger.ToString())!;
            Assert.AreEqual("1", trig.VarsScope.Vars["wait_for"], "sq_wait 0 (start_008.tcl:25) armed the gate");
            Assert.AreEqual("1", trig.VarsScope.Vars["enable_list"],
                "sequence_disable 0 (do_action:1828) marked actor 0 busy; only the finishcode clears it");
            Assert.AreSame(probe.Stub, probe.WedgedEntity!.ActiveAction, "the wedged action is still in flight");
            StringAssert.Contains("seq_enable", probe.WedgedEntity.ActionFinish ?? "",
                "the pending finishcode is the seq_enable callback (do_action:1830)");
            // No troll was ever summoned — player-visible half of the symptom.
            Assert.AreEqual(0, Count(probe.NewClasses, "Troll"),
                "the summon beats live BEHIND the rotate gate — none may run while it stalls");
        }

        // ------------------------------------------------------------------
        // C. SKIP WEDGE: break (ESC) while the rotate action is wedged. The faithful drain
        // must terminate REGARDLESS of the stuck action: parse_next-1 evals every
        // remaining non-'-' line (incl. the st008_end_2 redirect — the faithful "hat
        // swap" the player saw) and the trailing `+sq_object delete all` deletes the stuck
        // actor — killing its wedged action with it — then shut_sequencer restores
        // set_sequence 0 (which also clears the break flag, FUN_00649080:90) and parks
        // the persistent trigger. THEN the next menu scene must play on the SAME trigger.
        // Play regression guarded: "gnome (war hat and all) stayed; every later menu
        // cutscene broken".
        // ------------------------------------------------------------------
        [Test]
        public void Start008_SkipDuringWedgedRotate_DrainCleans_AndNextScenePlays()
        {
            var (interp, ctx, sim, trigger, baseline, probe) = BuildMenuWorld(880204, wedgeFirstRotate: true);
            interp.Eval($"call_method {trigger} set_sequencescript start_008");
            interp.Eval($"call_method {trigger} run");

            // Run to the wedged stall (same shape the stall test pins).
            int steps = 0;
            for (; steps < 600 && probe.WedgedEntity == null; steps++) sim.Step(0.05);
            Assert.IsNotNull(probe.WedgedEntity, "must reach the wedged rotate beat first");
            for (int i = 0; i < 100; i++) sim.Step(0.05);   // soak in the stall
            Assert.IsTrue(ctx.SequenceActive, "pre-condition: stalled mid-sequence");
            int censusBeforeBreak = probe.NewClasses.Count;

            // ESC (FUN_0052c510:353-355) → the next eval_sequence think must drain NOW; the
            // wedged actor action must NOT block it (the drain never waits on enable_list).
            ctx.SequenceBreaked = true;
            for (steps = 0; steps < 600 && ctx.SequenceActive; steps++) sim.Step(0.05);
            Assert.IsFalse(ctx.SequenceActive,
                $"the drain must terminate DESPITE the wedged action | {StallDiag(ctx, trigger, sim, probe)}");

            // The drain executed the faithful tail: the troll summons, the st008_end_2
            // redirect (do_script change … 1 — chance 1.0) and its war-hat swap block.
            // (THIS is the "gnome changed hat" the player saw — faithful; only the
            // staying-behind is the bug.)
            string drained = probe.CensusDelta(censusBeforeBreak);
            Assert.AreEqual(3, Count(probe.NewClasses, "Troll"), $"drain summons the trolls (drained: {drained})");
            Assert.That(probe.NewClasses, Does.Contain("Axt_3"),
                $"the drain must follow do_script change into st008_end_2 (drained: {drained})");
            Assert.That(probe.NewClasses, Does.Contain("Schild_3"), $"st008_end_2 shield summon (drained: {drained})");
            Assert.That(probe.NewClasses, Does.Contain("Dummy_Muetze_kampf_03_a"),
                $"st008_end_2's war-hat swap — the 'changed hat' the user saw is THIS faithful drain line (drained: {drained})");

            // … and then DELETED everything: the wedged gnome (with both hats), the trolls,
            // the props. A leaked gnome is the exact Play regression.
            var leaked = new List<string>();
            foreach (var kv in ctx.Entities)
                if (!baseline.Contains(kv.Key)) leaked.Add($"#{kv.Key}={kv.Value.Class?.Name}");
            Assert.IsEmpty(leaked,
                $"`+sq_object delete all` must remove every summon — the stuck actor must die WITH its wedged action " +
                $"(leaked: {string.Join(",", leaked)}) | {StallDiag(ctx, trigger, sim, probe)}");

            var trig = ctx.GetEntity(trigger.ToString());
            Assert.IsNotNull(trig, "the persistent menu trigger survives (destroy_overload park)");
            Assert.AreEqual("1", trig!.VarsScope.Vars["sequence_counter"], "shut_sequencer ran");
            Assert.IsFalse(ctx.SequenceBreaked,
                "set_sequence 0 must clear the break flag (FUN_00649080:90) — a sticky flag self-skips every later scene");
            Assert.That(sim.TimeFactor, Is.EqualTo(1.0).Within(1e-9), "gametime factor restored");

            // THE SECOND HALF OF THE PLAY BUG: the NEXT menu cutscene must play normally on
            // the same trigger. A self-skip (sticky break flag) would still bump the counter
            // but summon NOTHING — so assert the summons too.
            int censusBeforeNext = probe.NewClasses.Count;
            interp.Eval($"call_method {trigger} set_sequencescript start_001");
            interp.Eval("gametime factor 1");
            interp.Eval($"call_method {trigger} run");
            for (steps = 0; steps < 12000 && !Finished(ctx, trigger, "2"); steps++) sim.Step(0.05);
            Assert.IsTrue(Finished(ctx, trigger, "2"),
                $"the NEXT scene must run to its natural end after the skip | {StallDiag(ctx, trigger, sim, probe)}");
            Assert.AreEqual(4, probe.NewClasses.Count - censusBeforeNext,
                "start_001 must really PLAY (Zwerg×2 + hats×2), not instantly self-skip " +
                $"(post-skip census: {probe.CensusDelta(censusBeforeNext)})");

            for (int i = 0; i < 120; i++) sim.Step(0.05);
            AssertNoLeaks(ctx, baseline, "start_001 after the skipped start_008");
        }

        // ------------------------------------------------------------------
        // C2. THE LEAK MECHANISM behind "the gnome (war hat and all) STAYED": sq_object
        // delete pops each handle from object_list BEFORE calling `del`
        // (sequencer.tcl:905-913 — lrem, then del). If ONE `del` throws (in Play: a
        // render-side teardown failure inside the spawner), the foreach aborts: that
        // handle is already OUT of object_list and every LATER handle never gets
        // deleted — the drain's `+sq_object delete all` silently half-runs, the
        // leaked dwarf is then FOUND by every later scene's `sq_actor find Zwerg`
        // (nearest query) and all subsequent menu cutscenes mis-target — the full
        // Play cascade. The native CObj delete never throws on render teardown, so
        // the faithful port contract is: CmdDel must isolate the spawner-destroy
        // exception exactly like CmdNew isolates the spawner-spawn one.
        // RED until that CmdDel isolation lands (engine-side fix).
        // ------------------------------------------------------------------
        [Test]
        public void Start008_SkipWhenOneDestroyThrows_DrainMustStillDeleteEverySummon()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;   // the injected throw is logged
            try
            {
                var (interp, ctx, sim, trigger, baseline, probe) = BuildMenuWorld(880205, wedgeFirstRotate: true);
                _spawner!.ThrowOnDestroyOfPrefix = "Zwerg#";   // the dwarf's teardown fails once

                interp.Eval($"call_method {trigger} set_sequencescript start_008");
                interp.Eval($"call_method {trigger} run");
                int steps = 0;
                for (; steps < 600 && probe.WedgedEntity == null; steps++) sim.Step(0.05);
                Assert.IsNotNull(probe.WedgedEntity, "must reach the wedged rotate beat first");

                ctx.SequenceBreaked = true;
                for (steps = 0; steps < 800 && ctx.SequenceActive; steps++) sim.Step(0.05);
                Assert.IsFalse(ctx.SequenceActive,
                    $"the drain must still shut the sequencer | {StallDiag(ctx, trigger, sim, probe)}");

                var leaked = new List<string>();
                foreach (var kv in ctx.Entities)
                    if (!baseline.Contains(kv.Key)) leaked.Add($"#{kv.Key}={kv.Value.Class?.Name}");
                Assert.IsEmpty(leaked,
                    "ONE throwing destroy must not abort `sq_object delete all` mid-list (lrem-then-del, " +
                    "sequencer.tcl:905-913) — this leak IS the Play 'gnome stayed with the war hat' cascade. " +
                    $"Leaked: {string.Join(",", leaked)}");
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        static void AssertNoLeaks(DigglesTclContext ctx, HashSet<int> baseline, string when)
        {
            var extra = new List<string>();
            foreach (var kv in ctx.Entities)
                if (!baseline.Contains(kv.Key)) extra.Add($"#{kv.Key}={kv.Value.Class?.Name}");
            Assert.IsEmpty(extra, $"{when}: leaked entities: {string.Join(", ", extra)}");
            Assert.IsFalse(ctx.SequenceActive, $"{when}: sequence must be inactive");
        }
    }
}
