#nullable enable

namespace Diggles.Tests
{
    using System.Globalization;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Gnome life fixes, light half (no real data files). Verified against the decompile:
    // • the clock: 1 hour = 150 s (DAT_00737630 = 150.0f binary-verified; FUN_0068b790:110,
    // FUN_006955d0:59);
    // • get_worktime next*/last* return absolute gettime seconds (consumers compare vs
    // [gettime]±100 — std_ai.tcl:1443, Trigger_Tutorial.tcl:763-766); bare form = {start dur};
    // • per-tick half-hour window snap (FUN_005cb760:24-29 — x = floor(2x+0.05)·0.5);
    // • auto_choose_workingtime = the native 12-bucket histogram argmin (FUN_00676b50);
    // • set/get_prodautoschedule = obj+0x3d0 bit 0x800000 inverted (FUN_00680e20/FUN_00681050,
    // ctor default ON via flag word 0x01100101 FUN_005c83f0:224) + the dispatcher gate
    // (CProdPlanner_MaintenanceTick FUN_00433a70:112);
    // • the harvest ripeness gate: locked objects are invisible to the planner's resource scans
    // ((obj+0x3d0 & 4) == 0 — FUN_005f1500:114, FUN_005f4220:236);
    // • talkissue = the per-gnome weighted ledger (FUN_006aa650 + helpers FUN_005ff160/2c0/510/660);
    // • -randompath picks a real floor wander goal (FUN_004158a0:33→FUN_005a22f0) instead of
    // the old fake-arrival no-op.
    public sealed class GnomeLifeTests
    {
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) BuildHost()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos = "0 0 0")
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static double Num(string s) => double.Parse(s.Trim(), CultureInfo.InvariantCulture);

        // ---------------- the time model ----------------

        [Test]
        public void Clock_HourIs150GameSeconds()
        {
            // DAT_00737630 = 150.0f — every native consumer divides gettime by it.
            // 1 day = 12 h = 1800 s.
            Assert.AreEqual(150.0, ProdDispatcher.WorktimeHourSeconds, 1e-9);
            Assert.AreEqual(12.0, ProdDispatcher.DayHours, 1e-9);

            var (interp, _, sim) = BuildHost();
            for (int i = 0; i < 300; i++) sim.Step(0.5);   // t = 150 s
            Assert.AreEqual(150.0, Num(interp.Eval("gettime")), 1e-6, "gettime = game seconds");
            Assert.AreEqual(1.0, Num(interp.Eval("gethours")), 1e-6, "gethours = gettime / 150");
        }

        [Test]
        public void GetWorktime_EdgesAreGettimeSeconds_StartStaysHours()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G");
            for (int i = 0; i < 100; i++) sim.Step(0.5);   // t = 50 s → phase = 1/3 h
            interp.Eval($"set_worktime {g.Handle} 0 6");

            // bare form → the {start duration} pair (FUN_0068b790:72-91 argc!=3 path)
            var pair = TclList.Parse(interp.Eval($"get_worktime {g.Handle}").Trim());
            Assert.AreEqual(2, pair.Count, "bare get_worktime returns {start duration}");
            Assert.AreEqual(0.0, Num(pair[0]), 1e-6);
            Assert.AreEqual(6.0, Num(pair[1]), 1e-6);

            // hour-of-day shapes (Trigger_Tutorial.tcl:705 `(int([get_worktime $g start])+6)%12`)
            Assert.AreEqual(0.0, Num(interp.Eval($"get_worktime {g.Handle} start")), 1e-6);
            Assert.AreEqual(6.0, Num(interp.Eval($"get_worktime {g.Handle} duration")), 1e-6);
            Assert.AreEqual(6.0, Num(interp.Eval($"get_worktime {g.Handle} end")), 1e-6);

            // next/last edges are absolute gettime seconds: window [0,6) on the first day →
            // next end edge = 6 h = 900 s, next start = 12 h = 1800 s.
            Assert.AreEqual(900.0, Num(interp.Eval($"get_worktime {g.Handle} nextend")), 1e-3);
            Assert.AreEqual(1800.0, Num(interp.Eval($"get_worktime {g.Handle} nextstart")), 1e-3);
            Assert.AreEqual(0.0, Num(interp.Eval($"get_worktime {g.Handle} laststart")), 1e-3);
            Assert.AreEqual(-900.0, Num(interp.Eval($"get_worktime {g.Handle} lastend")), 1e-3);

            // the std_ai.tcl:1443 poller idiom must be expressible (seconds vs gettime±100)
            Assert.AreEqual("1",
                interp.Eval($"expr {{[get_worktime {g.Handle} nextend] > [gettime]-100}}").Trim());
        }

        [Test]
        public void GetRemainingSparetime_ReturnsClockHours()
        {
            var (interp, ctx, sim) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G");
            for (int i = 0; i < 100; i++) sim.Step(0.5);   // t = 50 s → phase = 1/3 h

            interp.Eval($"set_worktime {g.Handle} 0 6");
            Assert.AreEqual(0.0, Num(interp.Eval($"get_remaining_sparetime {g.Handle}")), 1e-6,
                "inside the window → 0.0 (FUN_006955d0:58)");

            // window [6,12): phase 1/3 h is sparetime, next start at 6 h → remaining 6 − 1/3 h.
            interp.Eval($"set_worktime {g.Handle} 6 6");
            Assert.AreEqual(6.0 - 1.0 / 3.0, Num(interp.Eval($"get_remaining_sparetime {g.Handle}")), 0.01,
                "sparetime → remaining CLOCK-HOURS until the next work start (t/150 units)");
        }

        [Test]
        public void Worktime_SnapsToHalfHours_PerDispatcherTick()
        {
            // FUN_005cb760:24-29 — x = floor(2x + 0.05) * 0.5 every pre-tick for planner-tracked
            // (gnome/baby) objects. 3.7 → 3.5, 5.2 → 5.0.
            var (interp, ctx, sim) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G");
            interp.Eval($"set_worktime {g.Handle} 3.7 5.2");
            sim.Step(0.1);
            Assert.AreEqual(3.5f, g.WorkStart, 1e-4f);
            Assert.AreEqual(5.0f, g.WorkLen, 1e-4f);
        }

        // ---------------- auto_choose_workingtime (FUN_00676b50) ----------------

        [Test]
        public void AutoChooseWorkingtime_NoOtherGnomes_Defaults0And6()
        {
            // total coverage 0 → start = 0, dur = 6.0 (FUN_00676b50:164-167, 0x40c00000 = 6.0f).
            var (interp, ctx, _) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G");
            g.WorkStart = 99f; g.WorkLen = 99f;
            interp.Eval($"auto_choose_workingtime {g.Handle}");
            Assert.AreEqual(0f, g.WorkStart, 1e-4f);
            Assert.AreEqual(6f, g.WorkLen, 1e-4f);
        }

        [Test]
        public void AutoChooseWorkingtime_FreshSpawns_DontPolluteTheHistogram()
        {
            // The spawn default is no window (CObj ctor zeroes start+duration,
            // FUN_005c83f0:130-132) and the coverage histogram only counts windows with
            // int(dur) > 0 (FUN_00676b50:127-156 `total += int(dur)`). A stale 6h default for
            // not-yet-chosen gnomes would fake coverage and stop the first dwarf from hitting
            // the native total==0 → [0,6) branch (FUN_00676b50:164-167).
            var (interp, ctx, _) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            for (int k = 0; k < 4; k++) { var o = Spawn(interp, ctx, "G"); o.Owner = 0; }   // un-chosen
            var g = Spawn(interp, ctx, "G");
            g.Owner = 0;
            interp.Eval($"auto_choose_workingtime {g.Handle}");
            Assert.AreEqual(0f, g.WorkStart, 1e-4f, "first CHOSEN dwarf must get the total==0 default");
            Assert.AreEqual(6f, g.WorkLen, 1e-4f);
        }

        [Test]
        public void AutoChooseWorkingtime_HistogramArgmin_AvoidsTheCrowd()
        {
            // Six same-owner gnomes on [0,6) → hist 166×6,0×6 (×999/36); after 3 smoothing passes
            // the minima are buckets {8,9} (hand-computed through the exact int math), so
            // start = (argmin+10)%12 ∈ {6,7} with dur 6.0 — the new gnome rests while they work.
            var (interp, ctx, _) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            for (int k = 0; k < 6; k++)
            {
                var o = Spawn(interp, ctx, "G");
                o.WorkStart = 0f; o.WorkLen = 6f; o.Owner = 0;
            }
            var g = Spawn(interp, ctx, "G");
            g.Owner = 0;
            interp.Eval($"auto_choose_workingtime {g.Handle}");
            Assert.AreEqual(6f, g.WorkLen, 1e-4f, "duration is always 6.0 (FUN_00676b50:266)");
            Assert.IsTrue(g.WorkStart == 6f || g.WorkStart == 7f,
                $"start must land 2h before an off-peak bucket (8|9 → 6|7), got {g.WorkStart}");
        }

        // ---------------- prodautoschedule flag + dispatcher gate ----------------

        [Test]
        public void ProdAutoSchedule_RoundTrip_DefaultOn()
        {
            var (interp, ctx, _) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G");
            // ctor flag word 0x01100101 — bit 0x800000 clear → auto on (FUN_005c83f0:224)
            Assert.AreEqual("1", interp.Eval($"get_prodautoschedule {g.Handle}").Trim());
            interp.Eval($"set_prodautoschedule {g.Handle} 0");
            Assert.AreEqual("0", interp.Eval($"get_prodautoschedule {g.Handle}").Trim());
            interp.Eval($"set_prodautoschedule {g.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"get_prodautoschedule {g.Handle}").Trim());
            // unparsable bool → bit set = auto off (FUN_00680e20:79-81 Tcl_GetBooleanFromObj failure)
            interp.Eval($"set_prodautoschedule {g.Handle} bogus");
            Assert.AreEqual("0", interp.Eval($"get_prodautoschedule {g.Handle}").Trim());
        }

        static EntityEvent? FirstEvent(DigglesTclEntity g, string name)
        {
            foreach (var ev in g.PendingEvents)
                if (ev.Name == name) return ev;
            return null;
        }

        [Test]
        public void Dispatcher_SkipsAutoscheduleOffGnome_AndLockedPilz()
        {
            var (interp, ctx, _) = BuildHost();
            ctx.LocalPlayer = 0;
            interp.Eval("def_class Pilz none material 0 {} {}");
            // Harvest demand is tracked per missing-recipe class (FUN_00434e60:447-500 →
            // FUN_00432690 produce map): the workshop carries the Grillpilz←Pilzhut recipe and
            // the item is pre-invented (else the order would dispatch the invention workat
            // instead of feeding harvest demand).
            interp.Eval("def_class B none production 0 {} {\n"
                        + "method prod_item_materials {item} {return {Pilzhut}}\n}");
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_attrib BpGrillpilz 0 1 1");

            // demand: an owned, built workshop with a scheduled slot (HasActiveProduction gate).
            // BuildupComplete must be set explicitly: spawn default is unbuilt (CObj
            // ctor flag word 0x01100101, bit 0x400000 clear — FUN_005c83f0:224); only a real
            // class's genericprod obj-init flips it (`set_buildupstate this 1`,
            // genericprod.tcl:451), and this fabricated empty-bodied class has none. Unbuilt,
            // the demand gate stays closed and — kind "production" — CollectBuildupJobs would
            // emit a competing kind-7 buildup job that steals the gnome from harvest.
            var b = Spawn(interp, ctx, "B", "40 30 8");
            b.Owner = 0; b.ProdSlots["Grillpilz"] = 1;
            b.BuildupComplete = true;

            var locked = Spawn(interp, ctx, "Pilz", "31 30 8");     // nearer — would win without the gate
            var ripe = Spawn(interp, ctx, "Pilz", "35 30 8");
            interp.Eval($"set_lock {locked.Handle} 1");              // pilz.tcl:23 — unripe

            var g = Spawn(interp, ctx, "G", "30 30 8");
            g.Owner = 0;
            interp.Eval($"set_worktime {g.Handle} 0 12");            // always worktime

            // auto-schedule off → the dispatcher must not assign (FUN_00433a70:112). Dispatch =
            // pool (gnome_idle) + the maintenance pass (SweepNow) — the idle-report one-shot
            // FUN_00434bc0 never matches new jobs.
            interp.Eval($"set_prodautoschedule {g.Handle} 0");
            ctx.ProdDispatcher!.OnGnomeIdle(g);
            ctx.ProdDispatcher!.SweepNow(0);
            Assert.AreEqual(0, g.PendingEvents.Count, "manual-schedule gnome must receive no evt_autoprod_*");

            // auto on → assigned, and the ripe (unlocked) Pilz wins even though the locked one is
            // nearer — the planner's resource scans skip locked objects (FUN_005f1500:114,
            // FUN_005f4220:236: `(obj+0x3d0 & 4) == 0`).
            interp.Eval($"set_prodautoschedule {g.Handle} 1");
            ctx.ProdDispatcher!.OnGnomeIdle(g);
            ctx.ProdDispatcher!.SweepNow(0);
            var ev = FirstEvent(g, "evt_autoprod_harvest");
            Assert.IsNotNull(ev, "auto-schedule gnome was not dispatched evt_autoprod_harvest");
            Assert.IsNotNull(ev!.Value.Payload, "harvest event carries a payload");
            Assert.AreEqual(ripe.Handle.ToString(), ev.Value.Payload!["-subject1"],
                "the dispatcher must harvest the UNLOCKED Pilz, not the nearer locked one");
        }

        // ---------------- talkissue ledger (FUN_006aa650) ----------------

        [Test]
        public void Talkissue_AddSetGetSumListReduceDelete()
        {
            var (interp, ctx, _) = BuildHost();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G");
            string H = g.Handle.ToString(CultureInfo.InvariantCulture);

            interp.Eval($"talkissue {H} add wtm 5");
            Assert.AreEqual(5.0, Num(interp.Eval($"talkissue {H} get wtm")), 1e-6);
            interp.Eval($"talkissue {H} add wtm -1");
            Assert.AreEqual(4.0, Num(interp.Eval($"talkissue {H} get wtm")), 1e-6);

            interp.Eval($"talkissue {H} set npw 2");
            Assert.AreEqual(6.0, Num(interp.Eval($"talkissue {H} sum")), 1e-6, "sum drives spt_talk_desire");

            // clamp to 50.0 (DAT_007375cc — the z_spare_talk.tcl:444 >50 warning is unreachable)
            interp.Eval($"talkissue {H} set big 100");
            Assert.AreEqual(50.0, Num(interp.Eval($"talkissue {H} get big")), 1e-6);

            var names = TclList.Parse(interp.Eval($"talkissue {H} list").Trim());
            Assert.IsTrue(names.Contains("wtm") && names.Contains("npw") && names.Contains("big"),
                "list returns the issue names (FUN_005ff510)");

            // reduce multiplies only names whose first char > '9' (FUN_005ff660:27)
            interp.Eval($"talkissue {H} set 7story 10");
            interp.Eval($"talkissue {H} reduce 0.5");
            Assert.AreEqual(2.0, Num(interp.Eval($"talkissue {H} get wtm")), 1e-6);
            Assert.AreEqual(1.0, Num(interp.Eval($"talkissue {H} get npw")), 1e-6);
            Assert.AreEqual(10.0, Num(interp.Eval($"talkissue {H} get 7story")), 1e-6,
                "numeric-prefixed (story) issues are immune to reduce");

            // a negative add landing below 3.0 deletes the entry (FUN_005ff2c0:51-67, DAT_00737428=3.0)
            interp.Eval($"talkissue {H} add wtm -1.0");   // 2.0 − 1.0 = 1.0 < 3.0 → gone
            Assert.AreEqual(0.0, Num(interp.Eval($"talkissue {H} get wtm")), 1e-6);
            Assert.IsFalse(TclList.Parse(interp.Eval($"talkissue {H} list").Trim()).Contains("wtm"));

            interp.Eval($"talkissue {H} delete npw");
            Assert.AreEqual(0.0, Num(interp.Eval($"talkissue {H} get npw")), 1e-6);
        }

        // ---------------- -randompath wander goal ----------------

        static NavWorld FloorLineNav(int w, int h, int floorSubY)
        {
            var sub = new byte[w * h * 64];
            int tileRow = floorSubY >> 3, subRow = floorSubY & 7;
            for (int tx = 0; tx < w; tx++)
                for (int col = 0; col < 8; col++)
                    sub[(tileRow * w + tx) * 64 + subRow * 8 + col] = 0x03;   // walkable + floor
            return new NavWorld(w, h, new uint[w * h], sub, new BdbModel());
        }

        [Test]
        public void RandomPath_PicksInBoundsFloorTarget_NotTheStart()
        {
            // A 12×12-tile world with one continuous ground line at subY 44. Start on the line.
            var nav = FloorLineNav(12, 12, 44);
            var start = new Vector3(6.0f, 5.5f, 8f);   // sub (48, 44)
            Random.InitState(20260610);

            bool ok = DigglesTclEngine.TryPickRandomPathTarget(nav, start, 5, 4f, 0f, out var t);
            Assert.IsTrue(ok, "a floor goal must exist within 5 tiles of the start");
            Assert.IsTrue(t.x != start.x || t.y != start.y, "the goal is a real move, not the start");
            int tsx = Mathf.RoundToInt(t.x * 8f), tsy = Mathf.RoundToInt(t.y * 8f);
            Assert.IsTrue(nav.SubIsFloor(tsx, tsy), "the goal lands on a FLOOR sub-cell");
            Assert.LessOrEqual(Mathf.Abs(t.x - start.x), 5.2f, "lateral wander ≤ N tiles");
            Assert.IsTrue(t.z >= 1f && t.z <= 14.5f, "depth clamped to the valid range (≤ DAT_00737564=14.5)");
        }

        [Test]
        public void RandomPath_UnsticksAnOffFloorStart()
        {
            // FUN_005a22f0:78-114 — an unwalkable start probes the 4 axis directions ≤ 33 sub-cells.
            var nav = FloorLineNav(12, 12, 44);
            var start = new Vector3(6.0f, 4.0f, 8f);   // sub (48, 32) — 12 below the line, off-floor
            Random.InitState(424242);
            bool ok = DigglesTclEngine.TryPickRandomPathTarget(nav, start, 4, 2f, 0f, out var t);
            Assert.IsTrue(ok, "the start un-stick probe must reach the ground line and still pick a goal");
            Assert.IsTrue(nav.SubIsFloor(Mathf.RoundToInt(t.x * 8f), Mathf.RoundToInt(t.y * 8f)));
        }

        [Test]
        public void ActionWalk_RandomPath_HeadlessStillFinishes()
        {
            // Headless (no nav/transform): the walk degrades to the documented instant-arrival wait
            // so the wildlife idle loop keeps scheduling — and the finishcode still fires.
            var (interp, ctx, sim) = BuildHost();
            interp.Eval("def_class W none monster 0 {} {}");
            var e = Spawn(interp, ctx, "W", "30 30 8");
            ctx.This = e;
            interp.Eval("action this walk \"-randompath 5 -randomz 4\" {set rnd_done 1}");
            ctx.This = null;
            for (int i = 0; i < 20; i++) sim.Step(0.05);
            Assert.IsTrue(e.VarsScope.Vars.TryGetValue("rnd_done", out var v) && v == "1",
                "the -randompath walk must complete headless (finishcode fired)");
            Assert.AreEqual(4, e.WalkResult, "degraded walk reports ARRIVED so walkfail gates pass");
        }
    }

    // Heavy half: the real Pilz lifecycle on the unmodified pilz.tcl (full classinit —
    // run on demand like WorkLoopTests). Validates the timer/userid machinery end-to-end:
    // pilz.tcl:40 `timer_event this evt_pilz_aging -userid 1 -repeat 3 -interval 170`,
    // pilz.tcl:108 `set_lock this 0` at ripeness, pilz.tcl:111 `timer_unset this 1`.
    [Explicit]   // loads ~300 real classes from the data dir; run on demand
    public sealed class GnomeLifeWave3PilzTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

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
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        static string Var(DigglesTclEntity e, string name)
            => e.VarsScope.Vars.TryGetValue(name, out var v) ? v : "<unset>";

        static void StepTo(DigglesGameSim sim, double t)
        {
            while (sim.GameTime < t) sim.Step(0.5);
        }

        [Test, Explicit]
        public void Pilz_Lifecycle_AgesUnlocksAndSwapsPhaseAnims()
        {
            var (interp, ctx, sim) = RealHost();
            Assert.IsTrue(ctx.Classes.ContainsKey("Pilz"), "Pilz class not loaded");

            // the phase anim table registered by the class body (pilz.tcl:8-11)
            var cls = ctx.Classes["Pilz"];
            Assert.AreEqual("pilz.wachsen01", cls.AnimMap["phase0"]);
            Assert.AreEqual("pilz.wachsen02", cls.AnimMap["phase1"]);
            Assert.AreEqual("pilz.wachsen03", cls.AnimMap["phase2"]);
            Assert.AreEqual("pilz.standard", cls.AnimMap["phase3"]);

            var p = Spawn(interp, ctx, "Pilz", "30 30 8");
            Assert.AreEqual("1", Var(p, "_lock"), "a fresh Pilz spawns LOCKED (pilz.tcl:23)");
            // the myzel-grown path: agephase 0 → the full 3×170 s ladder (pilz.tcl:68-75)
            interp.Eval($"call_method {p.Handle} has_myzel farm");
            Assert.AreEqual("0", Var(p, "agephase"));

            StepTo(sim, 180);   // past the 1st userid-1 firing at t=170
            Assert.AreEqual("1", Var(p, "agephase"), "agephase 0→1 at t=170");
            Assert.AreEqual("1", Var(p, "_lock"), "still locked while growing");

            StepTo(sim, 350);   // past the 2nd firing at t=340
            Assert.AreEqual("2", Var(p, "agephase"), "agephase 1→2 at t=340");

            StepTo(sim, 520);   // past the 3rd firing at t=510 (+ headless anim settle)
            Assert.AreEqual("3", Var(p, "agephase"), "agephase 2→3 at t=510");
            Assert.AreEqual("0", Var(p, "_lock"), "ripeness clears the lock (pilz.tcl:108)");
            Assert.AreEqual("pilz.standard", p.CurrentAnim,
                "the phase2 action's finishcode swaps to the ripe anim (pilz.tcl:113)");

            StepTo(sim, 700);   // timer_unset this 1 (pilz.tcl:111) — no further aging
            Assert.AreEqual("3", Var(p, "agephase"), "aging timer cancelled at ripeness");
        }

        [Test, Explicit]
        public void Pilz_Death_CreatesMyzel_AndDropsParts()
        {
            var (interp, ctx, sim) = RealHost();
            var p = Spawn(interp, ctx, "Pilz", "30 30 8");
            int pilzHandle = p.Handle;

            // a bare-new Pilz fast-forwards via evt_timer0 (+1 s → agephase 1|2, pilz.tcl:83-88)
            StepTo(sim, 6);
            int age = int.Parse(Var(p, "agephase"), CultureInfo.InvariantCulture);
            Assert.GreaterOrEqual(age, 1, "evt_timer0 must have seeded a non-myzel agephase");

            interp.Eval($"call_method {pilzHandle} die");
            StepTo(sim, 10);   // the die anim (~0.1 s headless) + its finishcode (drops + del this)

            DigglesTclEntity? myzel = null;
            int huts = 0, stems = 0;
            foreach (var e in ctx.Entities.Values)
            {
                if (e.Class == null) continue;
                if (e.Class.Name == "PilzMyzel") myzel = e;
                else if (e.Class.Name == "Pilzhut") huts++;
                else if (e.Class.Name == "Pilzstamm") stems++;
            }
            Assert.IsNotNull(myzel, "die must plant a PilzMyzel (pilz.tcl:127-133 — the respawn anchor)");
            Assert.AreEqual("1", Var(myzel!, "nopilz"), "the myzel is armed to regrow (set_nopilz 1, pilz.tcl:137)");
            Assert.IsFalse(ctx.Entities.ContainsKey(pilzHandle), "the dead Pilz is deleted after its fall anim");
            Assert.GreaterOrEqual(huts, 1, $"agephase {age} death drops at least one Pilzhut (pilz.tcl:142-144)");
            if (age >= 2) Assert.GreaterOrEqual(stems, 1, "agephase ≥2 death also drops a Pilzstamm");
        }
    }
}
