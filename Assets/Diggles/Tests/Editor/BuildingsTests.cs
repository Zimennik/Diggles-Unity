#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The building construction chain end-to-end on the unchanged original Tcl.
    //
    // The RE'd original chain (all cited):
    // • user order: eventgen.tcl:173/220 raises evt_task_buildup (-subject1 crate, -pos1 spot)
    // → evt_task_buildup_proc (z_events.tcl:858-894): walk + `prod_gnome_state this unpack`
    // + tasklist `unpack_pos_unfixed <crate> {pos} 0`;
    // • placement: unpack_pos_unfixed (z_procs.tcl:1172-1195) → prod_buildup_new
    // (z_work_common.tcl:403-411): `inv_rem; set_boxed 0; call_method set_buildupstep 0;
    // set_visibility 1; set_pos {pos}` — the crate becomes the building (same handle);
    // • construction: prod_buildup → prod_buildup_rep (z_work_common.tcl:413-508): per stage
    // teleport to the build dummy (get_linkpos), N hammer anims, `set_buildupstep step+1`;
    // • stage visuals: genericprod.tcl set_buildupstep (:212-221) → set_buildupanim (:776-814):
    // buildup_step 0..9 → single-frame bau_a..bau_k (j skipped), step > max → "standard" +
    // `set_buildupstate this 1` (the completion edge);
    // • completion: prod_buildup_rep's `bstep > maxbstep` → call_method unpackfrombox
    // (genericprod.tcl:194-210: set_boxed 0 + class init) → production usable
    // (z_work_prodfill.tcl:568 + the dispatcher's BuildupComplete gates);
    // • autonomous assignment (native): planner request byte +0x2a — writers: the window's
    // aufbau button (FUN_005242b0:342), the deferred re-place tick of the move flow
    // (FUN_005edea0:122-138) and Tcl set_prod_buildup (FUN_006937e0:86; unused by shipped
    // scripts) — → kind-7 job (FUN_005f0850:22-54) →
    // `set_event %s evt_autoprod_buildup -target %s -subject1 %s` (FUN_005f3a50:142) →
    // evt_autoprod_buildup_proc (z_events.tcl:1258-1288) sets current_worktask "buildup" →
    // prod_autobuildup (z_work_common.tcl:292-304, via z_work_states.tcl:135-139). There is
    // no auto-arm scan for placed-unbuilt buildings (buildup-carrier-re.md §0/§9.1) — the
    // request byte is the whole dispatch gate.
    //
    // Headless EditMode: walks degrade to brief WaitActions, so everything is placed ≤ 0.7 apart;
    // the logic chain (events → states → tasklist → genericprod methods → natives) runs verbatim.
    [Explicit]   // loads ~300 real classes from the data dir; run on demand
    public sealed class BuildingsTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        static BdbModel? s_bdb;
        static BdbModel Bdb() => s_bdb ??= BdbParser.Parse(DataRoot + "/BlockDB2.BDB");

        // Open-air terrain so the spatial natives (get_hmap etc.) behave; no walls needed.
        DiggableTerrain MakeOpenTerrain(int w, int h)
        {
            var go = new GameObject("DiggableTerrain");
            _spawnedGos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            for (int k = 0; k < block.Length; k++) block[k] = 59;   // 59 = air
            t.Init(new BdbPmpWorldBuilder.TclWorldResult { Width = w, Height = h, BlockIdx = block, Terrain = new byte[w * h] }, Bdb(), "", () => { });
            return t;
        }

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
        {
            string h = interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"");
            return ctx.GetEntity(h)!;
        }

        static string Var(DigglesTclEntity e, string name)
            => e.VarsScope.Vars.TryGetValue(name, out var v) ? v : "";

        // ---- 1. the natives: defaults, request flag, built-clears-boxed ------------------------

        [Test, Explicit]
        public void Buildup_Natives_StateAndRequestFlags()
        {
            var (interp, ctx, _) = RealHost();

            // Spawn default = unbuilt (CObj ctor flag word 0x01100101 — bit 0x400000 clear,
            // FUN_005c83f0:224). A placeholder class has no genericprod object-init, so it stays 0.
            var raw = Spawn(interp, ctx, "NoSuchClassW4", "10 10 8");
            Assert.AreEqual("0", interp.Eval($"get_buildupstate {raw.Handle}").Trim(),
                "raw entity must default UNBUILT (old port defaulted 'built')");

            // A real building runs genericprod's object-init `set_buildupstate this 1`
            // (genericprod.tcl:451) → reads built.
            var fire = Spawn(interp, ctx, "Feuerstelle", "12 10 8");
            Assert.AreEqual("1", interp.Eval($"get_buildupstate {fire.Handle}").Trim(),
                "genericprod object-init must flip a spawned building to built (genericprod.tcl:451)");

            // set/get_prod_buildup round-trip — the planner request byte +0x2a
            // (FUN_006937e0:86 / FUN_00693610:59). Was a "0" stub.
            Assert.AreEqual("0", interp.Eval($"get_prod_buildup {fire.Handle}").Trim());
            interp.Eval($"set_prod_buildup {fire.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"get_prod_buildup {fire.Handle}").Trim());
            Assert.IsTrue(fire.BuildupRequested, "request flag mirrors to the entity");
            interp.Eval($"set_prod_buildup {fire.Handle} 0");
            Assert.IsFalse(fire.BuildupRequested);

            // set_buildupstate true also clears the boxed bit:
            // flags = flags & 0xffffbfff | 0x400000 (FUN_0067e1e0:85).
            interp.Eval($"set_boxed {fire.Handle} 1");
            interp.Eval($"set_buildupstate {fire.Handle} 0");
            Assert.AreEqual("1", interp.Eval($"get_boxed {fire.Handle}").Trim(), "FALSE leaves boxed alone (:76)");
            interp.Eval($"set_buildupstate {fire.Handle} 1");
            Assert.AreEqual("0", interp.Eval($"get_boxed {fire.Handle}").Trim(),
                "TRUE must clear the boxed bit (FUN_0067e1e0:85)");
            Assert.AreEqual("1", interp.Eval($"get_buildupstate {fire.Handle}").Trim());
        }

        // ---- 2. the player order: crate placed at -pos1, constructed through stages to built -----

        [Test, Explicit]
        public void Buildup_TaskEvent_PlacesCrate_ThenConstructsToBuilt()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);

            var g = Spawn(interp, ctx, "Zwerg", "30 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");

            var f = Spawn(interp, ctx, "Feuerstelle", "30.2 30 8");
            f.Owner = 0;
            interp.Eval($"set_boxed {f.Handle} 1");      // a crated building…
            interp.Eval($"inv_add {g.Handle} {f.Handle}");   // …carried by the gnome (eventgen:165-174 precondition)
            Assert.GreaterOrEqual(f.ContainedIn, 0, "crate must sit in the gnome's inventory");

            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle into idle

            // The eventgen carried-crate path raises exactly this:
            // evt_task_buildup -subject1 <crate> -pos1 <spot> (eventgen.tcl:173).
            ctx.This = g;
            interp.Eval($"set_event this evt_task_buildup -subject1 {f.Handle} -pos1 {{30.5 30 8}}");
            ctx.This = null;

            bool sawPlacedUnbuilt = false, sawBauAnim = false;
            int maxStage = -1;
            for (int i = 0; i < 1500; i++)
            {
                sim.Step(0.1);
                bool boxed = Var(f, "_boxed") == "1";
                if (f.ContainedIn < 0 && !boxed && !f.BuildupComplete) sawPlacedUnbuilt = true;
                if (f.BuildupStep > maxStage) maxStage = f.BuildupStep;
                if (f.CurrentAnim != null && f.CurrentAnim.Contains("bau_")) sawBauAnim = true;
                if (i % 50 == 0)
                    TestContext.WriteLine($"step {i}: gState={g.CurrentState} tasks={g.TaskList.Count} " +
                        $"contained={f.ContainedIn} boxed={boxed} built={f.BuildupComplete} " +
                        $"stage={f.BuildupStep} bstepVar={Var(f, "buildup_step")} anim={f.CurrentAnim}");
                if (sawPlacedUnbuilt && f.BuildupComplete) break;   // construction finished
            }

            // Placement: the crate becomes the building (same handle) at -pos1 —
            // prod_buildup_new's `inv_rem; set_boxed 0; set_buildupstep 0; set_pos {pos}`
            // (z_work_common.tcl:408).
            Assert.IsTrue(sawPlacedUnbuilt, "building was never PLACED-UNBUILT (unpack/placement chain broke)");
            Assert.Less(f.ContainedIn, 0, "building must leave the gnome's inventory");
            Assert.AreEqual("0", interp.Eval($"get_boxed {f.Handle}").Trim(), "building must be unboxed");
            Assert.AreEqual(30.5f, f.Position.x, 1e-3f, "placed at -pos1.x");
            Assert.AreEqual(30f, f.Position.y, 1e-3f, "placed at -pos1.y");

            // Stages: buildup_step 0..max drives bau_a.. (genericprod.tcl:787-799); the C# mirror
            // (set_anim stage hook) must see the stage meshes advance.
            Assert.GreaterOrEqual(maxStage, 1, "construction stages never advanced past bau_a");
            Assert.IsTrue(sawBauAnim, "no bau_* stage anim was ever set on the building");

            // Completion: set_buildupstep max+1 → "standard" + set_buildupstate 1
            // (genericprod.tcl:809-813); Feuerstelle max_buildup_step = 3 (feuerstelle.tcl:39).
            Assert.IsTrue(f.BuildupComplete, "construction never completed (buildupstate stayed 0)");
            Assert.AreEqual("1", interp.Eval($"get_buildupstate {f.Handle}").Trim());
            Assert.AreEqual("4", Var(f, "buildup_step"), "final buildup_step must be max_buildup_step+1");
        }

        // ---- 3. autonomous construction: gates, evt_autoprod_buildup, completion opens production

        [Test, Explicit]
        public void Buildup_Dispatcher_AutoAssigns_RespectsGates_ThenProductionOpens()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);

            // Spawn the building built first (genericprod obj-init), then unbuild it only after
            // the gnome's flags are configured. Order matters: `new Zwerg` runs `state_trigger
            // this idle` synchronously in its obj-init (zwerg.tcl:180), whose state-enter fires
            // `gnome_idle this 1` (zwerg.tcl:275) and lets the dispatcher's one-shot re-task run
            // at spawn time, while ProdAutoSchedule still holds its default-on bit (CObj ctor
            // word 0x01100101, bit 0x800000 clear). If an open buildup job already existed here,
            // that spawn-time one-shot could grab it before the test clears the flag. (The
            // native one-shot FUN_00434bc0→FUN_0043a050 only takes a job the ~1 Hz allocator
            // pre-planned for the gnome — none exists at spawn — so this sequencing is a
            // port-divergence artifact of our immediate re-plan, not a missing gate.)
            var fire = Spawn(interp, ctx, "Feuerstelle", "30.5 30 8");
            fire.Owner = 0;

            var g = Spawn(interp, ctx, "Zwerg", "30 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");
            interp.Eval($"set_prodautoschedule {g.Handle} 0");   // gate A: manual schedule

            // A placed-but-unbuilt building (an interrupted construction): the faithful way to
            // get there is the building's own genericprod method — set_buildupstep 0 shows bau_a
            // and clears buildupstate (genericprod.tcl:212-221 +:812-813).
            interp.Eval($"call_method {fire.Handle} set_buildupstep 0");
            Assert.AreEqual("0", interp.Eval($"get_buildupstate {fire.Handle}").Trim(),
                "set_buildupstep 0 must leave the building UNBUILT");
            Assert.AreEqual(0, fire.BuildupStep, "stage mirror must show bau_a (step 0)");

            // Arm the request explicitly — the window's aufbau button (FUN_005242b0:342 →
            // +0x2a = 1). The old auto-arm sweep was removed (buildup-carrier-re.md §9.1): a
            // placed-unbuilt building dispatches only while its request byte is set, and the
            // request survives the closed gates below until the dispatch one-shot consumes it.
            interp.Eval($"set_prod_buildup {fire.Handle} 1");

            // The SP planner has no separate buildup dispatch: the +0x2a byte emits a kind-1 job
            // (FUN_00436440:33-52) → evt_autoprod_unpack, whose not-boxed branch is the
            // prod_buildup_rep resume (z_work_common.tcl:335). Worktask reads "unpack" (or
            // "repair" when hitpoints < 0.99, z_events.tcl:1314-1321).
            bool SawResumeIn(int steps)
            {
                for (int i = 0; i < steps; i++)
                {
                    sim.Step(0.1);
                    var wt = Var(g, "current_worktask");
                    if (wt == "unpack" || wt == "repair") return true;
                }
                return false;
            }

            // Gate A — auto-schedule off: the dispatcher must not hand out the job
            // (pool gate, FUN_00433a70:112 — flags & 0x800000 must be clear).
            Assert.IsFalse(SawResumeIn(80), "manual-schedule gnome must not be auto-assigned buildup");

            // Gate B — outside the work window: no dispatch either (IsWorkTime gate).
            interp.Eval($"set_prodautoschedule {g.Handle} 1");
            interp.Eval($"set_worktime {g.Handle} 0 0");         // zero-length window
            Assert.IsFalse(SawResumeIn(80), "off-shift gnome must not be auto-assigned buildup");

            // Gates open → evt_autoprod_unpack (kind-1) → evt_autoprod_unpack_proc sets
            // current_worktask + current_workplace (z_events.tcl:1291-1330).
            interp.Eval($"set_worktime {g.Handle} 0 12");
            Assert.IsTrue(SawResumeIn(400), "idle gnome was NOT dispatched the kind-1 resume " +
                "for the ARMED buildup request");
            Assert.AreEqual(fire.Handle.ToString(), Var(g, "current_workplace"),
                "gnome's workplace must be the unbuilt building");
            Assert.AreEqual(g.Handle, fire.CurrentWorkerHandle,
                "the building must carry the construction claim (planner +0x24 analog)");

            // Drive construction to completion (prod_autobuildup → walk_dummy + prod_buildup →
            // prod_buildup_rep stages, z_work_common.tcl:292-304/473-508).
            int stagesSeen = fire.BuildupStep;
            for (int i = 0; i < 1500 && !fire.BuildupComplete; i++)
            {
                sim.Step(0.1);
                if (fire.BuildupStep > stagesSeen) stagesSeen = fire.BuildupStep;
                if (i % 100 == 0)
                    TestContext.WriteLine($"build step {i}: gState={g.CurrentState} stage={fire.BuildupStep} " +
                        $"bstepVar={Var(fire, "buildup_step")} built={fire.BuildupComplete}");
            }
            Assert.IsTrue(fire.BuildupComplete, "autonomous construction never completed");
            Assert.GreaterOrEqual(stagesSeen, 1, "stages never advanced during autonomous construction");

            // Completion opens the production gate: a scheduled, invented item on the
            // now-built building must pull a worker (CollectProductionJobs gates on
            // BuildupComplete; original gate = z_work_prodfill.tcl:568 get_buildupstate).
            fire.ProdSlots["Grillpilz"] = 5;
            // No hand-seeding: preinvented Bp reads the def_attrib registry default
            // (genericprod.tcl:147-156 `def_attrib Bp$itemtype 0 1 1` at classinit;
            // native owner slots are registry-shaped, FUN_0062c9b0). The gate must
            // open by itself exactly like the real game.
            Assert.AreEqual("1", interp.Eval("get_owner_attrib 0 BpGrillpilz").Trim(),
                "preinvented BpGrillpilz must read default 1 from classinit with no seeding");

            bool sawWork = false;
            for (int i = 0; i < 600 && !sawWork; i++)
            {
                sim.Step(0.1);
                if (Var(g, "current_worktask") == "work") sawWork = true;
            }
            Assert.IsTrue(sawWork, "production never opened after construction completed " +
                "(evt_autoprod_workat not dispatched for the built building)");
        }

        // ---- 4. explicit request flag (set_prod_buildup) on a world crate: one-shot + claim ----

        [Test, Explicit]
        public void Buildup_RequestFlag_Dispatches_BoxedCrate_OneShot()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);

            // A crate standing in the world (not contained — the only native gate,
            // FUN_005f0850:22) with the request byte set: the planner must dispatch it even
            // though it is boxed. The Tcl then fast-finishes it: buildup_step is still "max"
            // (genericprod.tcl:460) → get_buildupstep = max+1 (:38) → prod_buildup_rep's
            // `bstep > maxbstep` branch → unpackfrombox (z_work_common.tcl:479-481).
            var crate = Spawn(interp, ctx, "Feuerstelle", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            interp.Eval($"set_prod_buildup {crate.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"get_prod_buildup {crate.Handle}").Trim());

            var g = Spawn(interp, ctx, "Zwerg", "20 20 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");

            // SP law: the +0x2a byte on a boxed crate emits the kind-1 job whose plan fetches
            // the crate first (FUN_00439990:81-113 pickup steps), then evt_autoprod_unpack —
            // the worktask passes through "pickupitem" into "unpack".
            bool sawUnpack = false;
            for (int i = 0; i < 400 && !sawUnpack; i++)
            {
                sim.Step(0.1);
                if (Var(g, "current_worktask") == "unpack") sawUnpack = true;
            }
            Assert.IsTrue(sawUnpack, "set_prod_buildup request was never dispatched (kind-1 pickup→unpack plan)");
            Assert.IsFalse(crate.BuildupRequested,
                "the request must clear when the job is dispatched — one-shot");
            // (No claim assert here: the pickup→unpack plan crosses an idle report between its
            // legs, and the idle report dissolves the transient rec+0x24 claim by design.)

            // Drive to the unbox: the boxed crate fast-finishes via unpackfrombox (set_boxed 0 + init).
            bool unboxed = false;
            for (int i = 0; i < 800 && !unboxed; i++)
            {
                sim.Step(0.1);
                if (interp.Eval($"get_boxed {crate.Handle}").Trim() == "0") unboxed = true;
            }
            Assert.IsTrue(unboxed, "the requested crate was never unpacked from its box");
            Assert.IsTrue(crate.BuildupComplete, "the fast-finished building must read built");
        }

        // ---- 5. the full placement model (buildup-carrier-re.md §2/§6/§10.6.1+7): inventory
        // crate click → eventgen "placement" → ghost → confirm → the carrier builds;
        // interrupt mid-build (a new user order drops the chain, the step survives);
        // the window's aufbau button re-arms +0x2a → another gnome resumes from the
        // saved step and finishes. -------------------------------------------------

        [Test, Explicit]
        public void Buildup_GhostConfirm_CarrierBuilds_InterruptedBuild_ResumedByOtherGnome()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);
            var pc = ctx.Placement!;

            var g1 = Spawn(interp, ctx, "Zwerg", "30 30 8");
            g1.Owner = 0;
            interp.Eval($"set_worktime {g1.Handle} 0 12");

            var f = Spawn(interp, ctx, "Feuerstelle", "30.2 30 8");
            f.Owner = 0;
            interp.Eval($"set_boxed {f.Handle} 1");
            interp.Eval($"inv_add {g1.Handle} {f.Handle}");
            for (int i = 0; i < 10; i++) sim.Step(0.1);          // settle into idle

            // The crate icon click in g1's inventory window routes through the real eventgen
            // (loaded by classinit.tcl:111) and returns "placement" (eventgen.tcl:180-182).
            ctx.SelectedHandle = g1.Handle;
            Assert.IsTrue(pc.ClickInventoryCrate(g1.Handle, f.Handle),
                "the inventory crate click must enter placement mode via the eventgen verb");
            pc.UpdateCursor(new Vector3(30.5f, 30f, 8f));
            Assert.IsTrue(pc.Valid, "open ground must validate");
            Assert.IsTrue(pc.Confirm(), "the valid-spot confirm must land");

            // The confirm raised evt_task_buildup on the carrier (eventgen.tcl:172-174) —
            // drive until the crate is placed and at least one construction stage ran.
            bool placed = false;
            for (int i = 0; i < 1200 && !(placed && f.BuildupStep >= 1); i++)
            {
                sim.Step(0.1);
                if (f.ContainedIn < 0 && Var(f, "_boxed") == "0" && !f.BuildupComplete) placed = true;
                if (i % 100 == 0)
                    TestContext.WriteLine($"carry step {i}: g1={g1.CurrentState} stage={f.BuildupStep} " +
                        $"contained={f.ContainedIn} boxed={Var(f, "_boxed")} built={f.BuildupComplete}");
            }
            Assert.IsTrue(placed, "the carrier never placed the crate (prod_buildup_new)");
            Assert.GreaterOrEqual(f.BuildupStep, 1, "construction never reached stage 1 before the interrupt");
            Assert.IsFalse(f.BuildupComplete, "must still be mid-build for the interrupt");
            int stepAtInterrupt = f.BuildupStep;

            // Interrupt: a new user order — its proc top drops the whole chain
            // (gnome_failed_work; tasklist_clear; stop_prod — z_events.tcl:346-349, spec §6.1).
            ctx.This = g1;
            interp.Eval("set_event this evt_task_walk -pos1 {28 30 8}");
            ctx.This = null;
            for (int i = 0; i < 60; i++) sim.Step(0.1);
            Assert.IsFalse(f.BuildupComplete, "the interrupted construction must NOT self-finish");
            Assert.GreaterOrEqual(f.BuildupStep, stepAtInterrupt,
                "the half-built building keeps its buildup_step across the interruption (§6.3)");

            // Take g1 off-shift so the resume goes to another gnome, then click the aufbau
            // button (FUN_005242b0:342 → +0x2a) — the dispatcher sends any free gnome whose
            // prod_buildup_rep resumes from the saved step (§6.4).
            interp.Eval($"set_worktime {g1.Handle} 0 0");
            var g2 = Spawn(interp, ctx, "Zwerg", "31 30 8");
            g2.Owner = 0;
            interp.Eval($"set_worktime {g2.Handle} 0 12");
            for (int i = 0; i < 10; i++) sim.Step(0.1);          // let g2 settle into idle
            pc.OrderResume(f.Handle);
            Assert.IsTrue(f.BuildupRequested, "the aufbau button must arm the request byte");

            bool g2Built = false;
            for (int i = 0; i < 2000 && !f.BuildupComplete; i++)
            {
                sim.Step(0.1);
                var wt2 = Var(g2, "current_worktask");
                if (wt2 == "unpack" || wt2 == "repair") g2Built = true;
                if (i % 100 == 0)
                    TestContext.WriteLine($"resume step {i}: g2={g2.CurrentState}/{Var(g2, "current_worktask")} " +
                        $"stage={f.BuildupStep} built={f.BuildupComplete}");
            }
            Assert.IsTrue(g2Built, "the SECOND gnome must take the resume (SP kind-1 → evt_autoprod_unpack)");
            Assert.IsTrue(f.BuildupComplete, "the resumed construction must finish");
            Assert.AreEqual("1", interp.Eval($"get_buildupstate {f.Handle}").Trim());
        }

        // ---- 6. the unattended-crate (deferred order) chain end-to-end: crate selected →
        // build button → ghost confirm writes +0x28/+0x30/+0x2d → evt_autoprod_unpack
        // -pos1 -num1 → prod_autounpack: asprod → pickup + unpack_pos_unfixed →
        // prod_buildup_new → built at the ordered pos; asbox → putdown → still boxed
        // at the ordered pos (spec §10.6.4). ---------------------------------------

        [Test, Explicit]
        public void Unpack_DeferredOrder_AsProdBuilds_AsBoxPutsDown()
        {
            var (interp, ctx, sim) = RealHost();
            ctx.LocalPlayer = 0;
            ctx.Terrain = MakeOpenTerrain(64, 64);
            var pc = ctx.Placement!;

            // --- asprod: crate standing in the world, build-button order at 30.5/30 ---
            // Order matters (same note as test 3): the order is armed before the gnome exists.
            // An idle gnome's haul behavior may otherwise pick the loose crate up first, and the
            // ensuing stop_prod then faithfully cancels a deferred order on a now-carried crate
            // (`set_prod_unpack $item 0`, z_procs.tcl:1240) — the unattended-crate scenario
            // requires the crate to actually be unattended when the dispatcher sees the request.
            var crate = Spawn(interp, ctx, "Feuerstelle", "30.2 30 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");

            Assert.IsTrue(pc.OrderBuild(crate.Handle), "the crate window's BUILD button enters placement");
            pc.UpdateCursor(new Vector3(30.5f, 30f, 8f));
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(crate.UnpackRequested, "the crate-selected confirm writes the deferred order");

            var g = Spawn(interp, ctx, "Zwerg", "30 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");

            // Note: the spawned crate reads built from its obj-init (set_buildupstate 1,
            // genericprod.tcl:451) until prod_buildup_new's `set_buildupstep 0` un-builds it —
            // so wait for the placement marker (unboxed at the ordered pos, the atomic line
            // z_work_common.tcl:408), then for the re-completed construction.
            bool placedAtPos = false;
            for (int i = 0; i < 2500 && !(placedAtPos && crate.BuildupComplete); i++)
            {
                sim.Step(0.1);
                if (!placedAtPos && crate.ContainedIn < 0 && Var(crate, "_boxed") == "0"
                    && Mathf.Abs(crate.Position.x - 30.5f) < 1e-3f) placedAtPos = true;
                if (i % 100 == 0)
                    Debug.Log($"asprod step {i}: g={g.CurrentState}/{Var(g, "current_worktask")} " +
                        $"tasks={g.TaskList.Count} stage={crate.BuildupStep} boxed={Var(crate, "_boxed")} " +
                        $"pos={crate.Position} contained={crate.ContainedIn} built={crate.BuildupComplete} " +
                        $"unpackReq={crate.UnpackRequested} worker={crate.CurrentWorkerHandle}");
            }
            Assert.IsTrue(placedAtPos,
                "the dispatched unpack (asprod) must PLACE the crate at the ordered -pos1 " +
                "(prod_buildup_new: inv_rem + set_boxed 0 + set_buildupstep 0 + set_pos)");
            Assert.IsTrue(crate.BuildupComplete,
                "the placed building must construct to BUILT (prod_buildup_rep stages)");
            Assert.AreEqual(30.5f, crate.Position.x, 1e-3f, "built at the ORDERED -pos1.x");
            Assert.AreEqual(30f, crate.Position.y, 1e-3f, "built at the ORDERED -pos1.y");

            // --- asbox: a second crate, same order surface with the asbox toggle ---
            // The shipped chain's possession gate: prod_autounpack's worklist runs
            // prod_buildup_check before its pickup step, and the check cancels unless the gnome
            // already has the boxed crate (`get_boxed && inv_find_obj < 0` → set_prod_unpack 0 +
            // hide_obj_ghost, z_work_common.tcl:354-368). In practice the crate reaches the
            // inventory through the idle-gnome loose-item pickup that runs right at spawn —
            // mirror the asprod leg's conditions exactly: retire the busy gnome and spawn a
            // fresh one next to the box after the order is armed.
            interp.Eval($"del {g.Handle}");
            var box = Spawn(interp, ctx, "Feuerstelle", "30.4 30 8");
            box.Owner = 0;
            interp.Eval($"set_boxed {box.Handle} 1");
            Assert.IsTrue(pc.OrderBuild(box.Handle));
            pc.ToggleAsBox();
            pc.UpdateCursor(new Vector3(33.5f, 30f, 8f));     // clear of the built Feuerstelle
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(box.OrderAsBox, "the toggle must mark the order as-box (+0x2d)");

            var g2 = Spawn(interp, ctx, "Zwerg", "30.6 30 8");
            g2.Owner = 0;
            interp.Eval($"set_worktime {g2.Handle} 0 12");

            bool putDown = false;
            for (int i = 0; i < 2000 && !putDown; i++)
            {
                sim.Step(0.1);
                putDown = box.ContainedIn < 0 && Var(box, "_boxed") == "1"
                    && Mathf.Abs(box.Position.x - 33.5f) < 1e-3f;
                if (i % 100 == 0)
                    Debug.Log($"asbox step {i}: g2={g2.CurrentState}/{Var(g2, "current_worktask")} " +
                        $"tasks={g2.TaskList.Count} pos={box.Position} boxed={Var(box, "_boxed")} " +
                        $"contained={box.ContainedIn} unpackReq={box.UnpackRequested} worker={box.CurrentWorkerHandle}");
            }
            Assert.IsTrue(putDown,
                "the asbox order must end with the crate PUT DOWN (still boxed) at the ordered pos " +
                "(unpack_pos_unfixed asbox=1 — bare inv_rem + set_pos, z_procs.tcl:1186-1193)");
            // The built flag legitimately survives boxing (set_boxed never touches it; only
            // set_buildupstep drives it) — the meaningful claim is that no construction stage
            // ran for an as-box putdown.
            Assert.Less(box.BuildupStep, 0, "an as-box placement must NOT run construction stages");
            Assert.AreEqual("1", interp.Eval($"get_boxed {box.Handle}").Trim(), "the put-down crate stays boxed");
        }
    }
}
