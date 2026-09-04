#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Off-work "camp life" — the idle/sparetime decision chain.
    //
    // RE'd flow under test (original Tcl):
    // • state idle (zwerg.tcl:278-320): every think → act_when_idle (z_procs.tcl:1266-1321);
    // if it acts → return (NO idle anim, NO sparetime). Else idletimeout>5 && remaining
    // sparetime>0 → state_triggerfresh sparetime_dispatch; else set_idle_anim +
    // state_disable + `action this wait 1 {state_enable this}` (1 think/second pacing).
    // • act_when_idle ground branch (z_procs.tcl:1295-1320): own-pos placelock (0.8, self
    // excepted) → walk_random 2..3; else any Zwerg/Baby in bbox {-0.6 -0.3 -1.2 0.6 0.3 1.2}
    // whose walkresult != 2 (2 = WS_NOTFINISHED, i.e. currently walking) → walk_random 2..3.
    // • sparetime_dispatch (z_spare_main.tcl:17-149): tasklist pump → init (future plan via
    // `switch [hceil [get_remaining_sparetime]]`,:53-62) → sparetime_decide_occup →
    // occupation start/loop (eat/slp/fun/bth) — the camp's sit-at-fire/sleep/talk/self-fun.
    //
    // Port fixes verified here (each decompile-cited at its implementation):
    // 1. WalkResult=2 stamped SYNCHRONOUSLY at walk-action creation (CActWalk ctor
    // FUN_00407430:63-65) — neighbours inside one 10 Hz think pass must see an
    // already-moving gnome as walking, else the whole post-cutscene cluster hops every
    // pass and never converges (the "wander back and forth non-stop" report).
    // 2. prod_guest = the native 16-slot {link, guest, order} seat table (FUN_006a8920);
    // getlink returns the MODEL LINK id registered by addlink (feuerstelle.tcl:297-306),
    // not the seat index — required for every sit-down occupation's walk_dummy.
    // 3. get_place native option walker (FUN_00656560): -rect takes 4 scalars, -checkfree
    // takes none and returns a boolean, -placelockidexcept honoured — the ground-sleep /
    // talk-approach / cartwheel placements parse correctly.
    // 4. sparetime queryrect = a real centre-relative rect (FUN_006aadd0:295-318).
    // 5. hceil renders a Tcl double ("6.0") for the z_spare_main.tcl:55 switch.
    public sealed class SparetimeBehaviorTests
    {
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        // ---- light host (PostCutsceneChurnTests.Host pattern — no data files) ----
        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos = "0 0 0")
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        // ------------------------------------------------------------------
        // 1) prod_guest — the native seat table (FUN_006a8920)
        // ------------------------------------------------------------------

        // Mirrors feuerstelle.tcl:297-306 exactly: 3 sit links {5 6 7} then 5 wait links {0..4}.
        // seat 0 must resolve to LINK 5 (an earlier revision returned the seat index → every fire-sit
        // walk_dummy targeted the wrong dummy).
        [Test]
        public void ProdGuest_AddlinkGetlink_SeatToLink()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class B none production 0 {} {}");
            var b = Spawn(interp, ctx, "B");
            foreach (var link in new[] { 5, 6, 7, 0, 1, 2, 3, 4 })
                interp.Eval($"prod_guest addlink {b.Handle} {link}");

            Assert.AreEqual("5", interp.Eval($"prod_guest getlink {b.Handle} 0").Trim(), "seat 0 → link 5");
            Assert.AreEqual("7", interp.Eval($"prod_guest getlink {b.Handle} 2").Trim(), "seat 2 → link 7");
            Assert.AreEqual("0", interp.Eval($"prod_guest getlink {b.Handle} 3").Trim(), "seat 3 → wait link 0");
            Assert.AreEqual("-1", interp.Eval($"prod_guest getlink {b.Handle} 15").Trim(), "unclaimed slot → -1");
        }

        [Test]
        public void ProdGuest_GuestFree_NeedsAddlink_AndSkipsSeated()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class B none production 0 {} {}");
            var b = Spawn(interp, ctx, "B");

            // No addlink yet → NO seats exist (native scans link != -1, FUN_006a8920:357-410).
            Assert.AreEqual("-1", interp.Eval($"prod_guest guestfree {b.Handle}").Trim(),
                "a building with no registered links must seat nobody");

            interp.Eval($"prod_guest addlink {b.Handle} 5");
            interp.Eval($"prod_guest addlink {b.Handle} 6");
            Assert.AreEqual("0", interp.Eval($"prod_guest guestfree {b.Handle}").Trim());

            // The guest must be a LIVE entity — guestfree GC's dead guest refs to empty seats
            // (FUN_006a8920:381-405), so a fabricated handle would read as free again.
            var guest = Spawn(interp, ctx, "B");
            interp.Eval($"prod_guest guestset {b.Handle} 0 {guest.Handle}");
            Assert.AreEqual(guest.Handle.ToString(), interp.Eval($"prod_guest guestget {b.Handle} 0").Trim());
            Assert.AreEqual("1", interp.Eval($"prod_guest guestfree {b.Handle}").Trim(), "seat 0 taken → next free is 1");

            // guestremove returns the freed seat index; the seat opens up again (:271-310).
            Assert.AreEqual("0", interp.Eval($"prod_guest guestremove {b.Handle} {guest.Handle}").Trim());
            Assert.AreEqual("0", interp.Eval($"prod_guest guestfree {b.Handle}").Trim());
            // removing an unknown ref → the native's 1 (:300-309)
            Assert.AreEqual("1", interp.Eval($"prod_guest guestremove {b.Handle} 9999").Trim());
        }

        [Test]
        public void ProdGuest_Orders_RemorderDecrements()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class B none production 0 {} {}");
            var b = Spawn(interp, ctx, "B");
            interp.Eval($"prod_guest addlink {b.Handle} 5");
            interp.Eval($"prod_guest addlink {b.Handle} 6");

            interp.Eval($"prod_guest addorder {b.Handle} 1");
            interp.Eval($"prod_guest addorder {b.Handle} 1");
            Assert.AreEqual("2", interp.Eval($"prod_guest getorder {b.Handle} 1").Trim());
            // REMORDER DECREMENTS (max(0, order−1), FUN_006a8920:436-455) — it does NOT zero.
            interp.Eval($"prod_guest remorder {b.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"prod_guest getorder {b.Handle} 1").Trim());
            interp.Eval($"prod_guest resetorder {b.Handle} 1");
            Assert.AreEqual("0", interp.Eval($"prod_guest getorder {b.Handle} 1").Trim());

            // nextorder = the SEATED guest with the highest order, else -1 (:476-530).
            Assert.AreEqual("-1", interp.Eval($"prod_guest nextorder {b.Handle}").Trim());
            interp.Eval($"prod_guest guestset {b.Handle} 0 7");
            interp.Eval($"prod_guest guestset {b.Handle} 1 8");
            interp.Eval($"prod_guest addorder {b.Handle} 0");
            interp.Eval($"prod_guest addorder {b.Handle} 1");
            interp.Eval($"prod_guest addorder {b.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"prod_guest nextorder {b.Handle}").Trim());
        }

        // ------------------------------------------------------------------
        // 2) WalkResult — synchronous WS_NOTFINISHED at creation
        // ------------------------------------------------------------------

        // CActWalk's ctor stamps obj+0x248 = 2 BEFORE `action … walk` returns (FUN_00407430:63-65).
        // act_when_idle reads NEIGHBOURS' `[get_walkresult $o] != 2` to pick who side-steps out of
        // a crowd (z_procs.tcl:1273-1278, 1301-1309): a gnome that just started walking THIS think
        // pass must already read as 2, so the rest of the cluster stays put (the anchors that let
        // the post-cutscene formation converge instead of shuffling forever).
        [Test]
        public void WalkResult_StampedWalking_AtActionCreation()
        {
            var (interp, ctx, sim) = Host();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G", "10 10 8");
            // Port-internal virgin = 0 (sparetime first-walk flow keys on it); the SAVE layer
            // translates 0 → the native virgin 1 at capture.
            Assert.AreEqual("0", interp.Eval($"get_walkresult {g.Handle}").Trim(), "fresh spawn = 0");

            // Headless degraded walk (no nav) — the CmdAction install path must still stamp 2.
            interp.Eval($"action {g.Handle} walk \"-target {{12 10 8}}\"");
            Assert.AreEqual("2", interp.Eval($"get_walkresult {g.Handle}").Trim(),
                "walkresult must read 2 (WS_NOTFINISHED) IMMEDIATELY after `action walk`, before any sim tick");
            Assert.That(g.ActiveAction, Is.InstanceOf<ISaveableEntityAction>());
            var walkState = (WalkActionSaveState)
                ((ISaveableEntityAction)g.ActiveAction!).CaptureSaveState();
            Assert.That(walkState.HasTarget, Is.True);
            Assert.That(walkState.TargetNative, Is.EqualTo(new Vector3(12f, 10f, 8f)));

            for (int i = 0; i < 5 && g.ActiveAction != null; i++) sim.Step(0.1);
            Assert.AreEqual("4", interp.Eval($"get_walkresult {g.Handle}").Trim(),
                "the degraded walk must still complete to 4 (WS_TARGETREACHED)");
        }

        // ------------------------------------------------------------------
        // 3) get_place option walker + hceil + sparetime queryrect
        // ------------------------------------------------------------------

        // The option arities (FUN_00656560): -rect eats FOUR scalars, -checkfree eats none and
        // flips the result to a boolean (:1072-1112, emit:1135-1140). Without a nav world the
        // vector form fails as "-1 0 0" and the boolean form as "0" — but neither may misparse
        // or throw (the old per-pair walker derailed on these exact real-script shapes).
        [Test]
        public void GetPlace_RectAndCheckfreeShapes_ParseWithoutNav()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class G none gnome 0 {} {}");
            var g = Spawn(interp, ctx, "G", "10 10 8");
            ctx.This = g;
            try
            {
                // sparetime_slp_start ground fallback shape (z_spare_procs.tcl:529)
                Assert.AreEqual("-1 0 0",
                    interp.Eval("get_place -center {10 10 8} -rect -8 -8 8 8 -walldist 1 -except this -placelockidexcept 42").Trim());
                // sptfun_check_wheeling shape (z_spare_fun.tcl:789) — BOOLEAN result
                Assert.AreEqual("0",
                    interp.Eval("get_place -checkfree -center {10 10 8} -rect -2 -1 0 1 -except this").Trim(),
                    "-checkfree must return a scalar boolean, not a position");
            }
            finally { ctx.This = null; }
        }

        [Test]
        public void Hceil_RendersTclDouble_ForSparetimePlanSwitch()
        {
            var (interp, _, _) = Host();
            // z_spare_main.tcl:55-62 switches on the EXACT strings "0.0"/"1.0"/…/"4.0".
            Assert.AreEqual("6.0", interp.Eval("hceil 5.2").Trim());
            Assert.AreEqual("1.0", interp.Eval("hceil 0.4").Trim());
            Assert.AreEqual("0.0", interp.Eval("hceil 0").Trim());
        }

        [Test]
        public void SparetimeQueryRect_HonorsTheRect()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class F none production 0 {} {}");
            interp.Eval("def_class G none gnome 0 {} {}");
            var fire = Spawn(interp, ctx, "F", "100 100 8");
            var g = Spawn(interp, ctx, "G", "10 100 8");
            // real buildings are BUILT at init (genericprod.tcl:451) — the query walkers gate
            // on the built flag + same owner (FUN_005f4d00:75-76; CampOwnershipAndTrapTests)
            interp.Eval($"set_buildupstate {fire.Handle} 1");
            interp.Eval($"sparetime {fire.Handle} announce eat");

            // full auto-schedule field {-160 -80 160 80} (z_spare_procs.tcl:133 via
            // sparetime_searchrange = stt_maxsearch_range = 160) — dx = 90 is inside
            Assert.AreEqual(fire.Handle.ToString(),
                interp.Eval($"sparetime {g.Handle} queryrect eat -160 -80 160 80").Trim());
            // the MANUAL-schedule field is 160×0.05 = 8 (z_spare_procs.tcl:5-8) — dx = 90 is OUT
            Assert.AreEqual("",
                interp.Eval($"sparetime {g.Handle} queryrect eat -8 -4 8 4").Trim(),
                "queryrect must honour the rect (the old 50-radius proxy ignored it)");
            // the vertical bound is the HALF range — a place 90 above is outside {-160 -80 160 80}
            var high = Spawn(interp, ctx, "F", "10 10 8");
            interp.Eval($"set_buildupstate {high.Handle} 1");
            interp.Eval($"sparetime {high.Handle} announce eat");
            Assert.IsFalse(
                TclList.Parse(interp.Eval($"sparetime {g.Handle} queryrect eat -160 -80 160 80").Trim())
                    .Contains(high.Handle.ToString()),
                "queryrect must honour the rect's y half-range too (place 90 rows above the asker)");
        }

        // ------------------------------------------------------------------
        // 3b) obj_query -boundingbox axis order — the crowd box is NOT "fat"
        // ------------------------------------------------------------------

        // Decompile- against the "wrong axis mapping" hypothesis: the native
        // parser lands the 6 flat floats f0→minX f1→minY f2→minZ f3→maxX f4→maxY f5→maxZ, added
        // to the query pos (FUN_005de500:2294-2419), and the mode-3 box test compares the
        // candidate's native pos components 1:1 (FUN_005e12a0:326-333 — min.x≤+0xc, min.y≤+0x10,
        // min.z≤+0x14, ≤max). act_when_idle's ground crowd box {-0.6 -0.3 -1.2 0.6 0.3 1.2}
        // (z_procs.tcl:1301) is therefore ±0.6 east (x), ±0.3 ground-line row (y), ±1.2 slab
        // depth (z) — the port must reproduce exactly these gates per axis.
        [Test]
        public void ObjQuery_CrowdBox_AxisOrder_NativeFaithful()
        {
            var (interp, ctx, _) = Host();
            interp.Eval("def_class G none gnome 0 {} {}");
            var me = Spawn(interp, ctx, "G", "100 100 8");
            var other = Spawn(interp, ctx, "G", "100 100 8");
            string Box() => interp.Eval(
                $"obj_query {me.Handle} -class G -boundingbox {{-0.6 -0.3 -1.2 0.6 0.3 1.2}}").Trim();
            string hit = other.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture);

            other.Position = new Vector3(100.5f, 100f, 8f);
            Assert.AreEqual(hit, Box(), "x (east) +0.5 is inside ±0.6");
            other.Position = new Vector3(100.7f, 100f, 8f);
            Assert.AreEqual("0", Box(), "x (east) +0.7 is OUTSIDE ±0.6 — the narrow axis");

            other.Position = new Vector3(100f, 100.25f, 8f);
            Assert.AreEqual(hit, Box(), "y (row) +0.25 is inside ±0.3");
            other.Position = new Vector3(100f, 100.4f, 8f);
            Assert.AreEqual("0", Box(), "y (row) +0.4 is OUTSIDE ±0.3 — the same-floor gate");

            other.Position = new Vector3(100f, 100f, 9f);
            Assert.AreEqual(hit, Box(), "z (slab depth) +1.0 is inside ±1.2 — the DEEP axis");
            other.Position = new Vector3(100f, 100f, 9.4f);
            Assert.AreEqual("0", Box(), "z (slab depth) +1.4 is OUTSIDE ±1.2");

            // The two-sublist {{min} {max}} form parses to the same box (FUN_005de500:2241-2290).
            other.Position = new Vector3(100.5f, 100f, 8f);
            Assert.AreEqual(hit, interp.Eval(
                $"obj_query {me.Handle} -class G -boundingbox {{{{-0.6 -0.3 -1.2}} {{0.6 0.3 1.2}}}}").Trim());
        }

        // ------------------------------------------------------------------
        // 4) [Explicit] the 5-gnome camp cluster on the REAL start meadow
        // ------------------------------------------------------------------

        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // act_when_idle's GROUND crowd box (z_procs.tcl:1301).
        static bool CrowdOverlap(Vector3 a, Vector3 b)
        {
            var d = a - b;
            return Mathf.Abs(d.x) < 0.6f && Mathf.Abs(d.y) < 0.3f && Mathf.Abs(d.z) < 1.2f;
        }

        // ---- camp forensics: one run must pinpoint WHICH branch feeds the churn ----
        //
        // CommandTrace fires for procs too (TclInterpreter.cs:124-127), so the recorder counts
        // the EXACT decision points of the RE'd loop:
        // • act_when_idle results — the wander/avoid gate (z_procs.tcl:1266-1321);
        // true blocks BOTH the idle→sparetime hop (zwerg.tcl:298) and the occupation start
        // (z_spare_main.tcl:135) — the churn engine when it never yields 0.
        // • placelock_check r=0.8 → 1 — the "leaves locked place" branch (z_procs.tcl:1296);
        // • crowd-box obj_query hits — the neighbour branch (z_procs.tcl:1301-1309);
        // • get_place failures — walk_random target-pick fails (z_dignwalk.tcl:725-726);
        // • `log` lines — the original's own diagnostics: "leaves locked
        // place" (z_procs.tcl:1297), "walk not successful" (walkfail → state_triggerfresh idle,
        // resets idletimeout — z_dignwalk.tcl:919-923), "X -> Y" occupation changes
        // (z_spare_main.tcl:128), "eatcheck!=eatstart" (z_spare_procs.tcl:209,220);
        // • the eat chain probes — sparetime_eat_check result (inv_check + the
        // z_spare_procs.tcl:72 obj_query), `sparetime queryrect eat` (z_spare_procs.tcl:133),
        // `prod_guest guestfree` (the seat gate, z_spare_procs.tcl:138);
        // • state_trigger(fresh) targets — the idle/work_idle/sparetime_dispatch traffic.
        sealed class CampForensics
        {
            static readonly HashSet<string> Procs = new()
            {
                "act_when_idle", "walk_random", "walk_pos", "walkfail", "gnome_failed_work",
                "sparetime_eat_check", "sparetime_eat_start", "sparetime_slp_start",
                "sparetime_bth_start", "sparetime_fun_start", "sparetime_decide_occup",
            };
            public readonly Dictionary<string, int> ProcCalls = new();
            public int ActIdleTrue, ActIdleFalse;
            public int LockBranchHits, CrowdQueries, CrowdHits;
            public int GetPlaceCalls, GetPlaceFails, WalkActions;
            // WHO does the crowd box actually match, and WHAT walkresult does the
            // act_when_idle foreach read for it — the two facts that decide between the
            // "gnomes really crowd" and the "phantom neighbour" readings of a 100% hit rate.
            public DigglesTclContext? Ctx;
            public readonly Dictionary<string, int> CrowdMatches = new();
            public readonly Dictionary<string, int> WalkresultSeen = new();
            public readonly Dictionary<string, int> StateTriggers = new();
            public readonly Dictionary<string, int> EatTrace = new();
            public readonly Dictionary<string, int> LogLines = new();
            public readonly List<string> OccupChanges = new();

            public void Attach(TclInterpreter interp) => interp.CommandTrace = Trace;

            void Bump(Dictionary<string, int> d, string key)
                => d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;

            void Trace(string name, string[] args, string result)
            {
                switch (name)
                {
                    case "act_when_idle":
                        Bump(ProcCalls, name);
                        if (result.Trim() == "1") ActIdleTrue++; else ActIdleFalse++;
                        return;
                    case "placelock_check":
                        // the act_when_idle own-pos probe is the ONLY 0.8-radius caller (z_procs.tcl:1296)
                        if (args.Length >= 3 && args[2] == "0.8" && result.Trim() == "1") LockBranchHits++;
                        return;
                    case "obj_query":
                        for (int k = 2; k < args.Length; k++)
                            if (args[k].Contains("-0.6 -0.3 -1.2"))
                            {
                                CrowdQueries++;
                                if (result.Trim() != "0")
                                {
                                    CrowdHits++;
                                    foreach (var h in TclList.Parse(result))
                                    {
                                        string cls = "?";
                                        if (Ctx != null)
                                            foreach (var e in Ctx.Entities.Values)
                                                if (e.Handle.ToString() == h) { cls = e.Class?.Name ?? "(noclass)"; break; }
                                        Bump(CrowdMatches, $"{cls}#{h}");
                                    }
                                }
                                break;
                            }
                        return;
                    case "get_walkresult":
                        Bump(WalkresultSeen, (args.Length > 1 ? args[1] : "?") + "→" + result.Trim());
                        return;
                    case "get_place":
                        GetPlaceCalls++;
                        if (result.TrimStart().StartsWith("-1")) GetPlaceFails++;
                        return;
                    case "action":
                        if (args.Length > 2 && args[2] == "walk") WalkActions++;
                        return;
                    case "state_trigger":
                    case "state_triggerfresh":
                        Bump(StateTriggers, args.Length > 2 ? args[2] : "(clear)");
                        return;
                    case "sparetime":
                        if (args.Length > 2 && args[2] == "queryrect")
                            Bump(EatTrace, "queryrect" + (string.IsNullOrWhiteSpace(result) ? "=MISS" : "=hit"));
                        return;
                    case "prod_guest":
                        if (args.Length > 1 && args[1] == "guestfree")
                            Bump(EatTrace, "guestfree=" + result.Trim());
                        return;
                    case "inv_check":
                        Bump(EatTrace, "inv_check=" + result.Trim());
                        return;
                    case "log":
                        string m = args.Length > 1 ? args[1] : "";
                        if (m.Contains("leaves locked place")) Bump(LogLines, "leaves locked place");
                        else if (m.Contains("walk not successful")) Bump(LogLines, "walk not successful");
                        else if (m.Contains("walkretry")) Bump(LogLines, "walkretry");
                        else if (m.Contains("eatcheck!=eatstart")) Bump(LogLines, "eatcheck!=eatstart");
                        else if (m.Contains(" -> ") && OccupChanges.Count < 60) OccupChanges.Add(m);
                        return;
                    default:
                        if (Procs.Contains(name))
                        {
                            Bump(ProcCalls, name);
                            if (name == "sparetime_eat_check") Bump(EatTrace, "eat_check=" + result.Trim());
                            if (name.EndsWith("_start") && result.StartsWith("!!ERR"))
                                Bump(EatTrace, name + "=ERR");
                        }
                        return;
                }
            }

            public string Summary()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("---- camp forensics ----");
                sb.AppendLine($"act_when_idle: true={ActIdleTrue} false={ActIdleFalse} " +
                              $"| lock-branch hits (placelock_check 0.8→1)={LockBranchHits} " +
                              $"| crowd-box queries={CrowdQueries} hits={CrowdHits}");
                sb.AppendLine($"get_place: calls={GetPlaceCalls} fails={GetPlaceFails} | action-walks={WalkActions}");
                sb.Append("proc calls:"); foreach (var kv in ProcCalls) sb.Append($" {kv.Key}={kv.Value}"); sb.AppendLine();
                sb.Append("state triggers:"); foreach (var kv in StateTriggers) sb.Append($" {kv.Key}={kv.Value}"); sb.AppendLine();
                sb.Append("eat trace:"); foreach (var kv in EatTrace) sb.Append($" {kv.Key}×{kv.Value}"); sb.AppendLine();
                sb.Append("orig logs:"); foreach (var kv in LogLines) sb.Append($" \"{kv.Key}\"×{kv.Value}"); sb.AppendLine();
                sb.Append("crowd matches:"); foreach (var kv in CrowdMatches) sb.Append($" {kv.Key}×{kv.Value}"); sb.AppendLine();
                sb.Append("walkresult reads:"); foreach (var kv in WalkresultSeen) sb.Append($" {kv.Key}×{kv.Value}"); sb.AppendLine();
                if (OccupChanges.Count > 0)
                    sb.AppendLine("occupation changes: " + string.Join(" | ", OccupChanges));
                return sb.ToString();
            }
        }

        static bool IsWalkAction(object? act)
            => act != null && act.GetType().Name.Contains("Walk");

        // THE camp-life regression: 5 Zwerge spawned inside each other's crowd boxes (the
        // post-cutscene formation) + a Feuerstelle, pure SPARETIME (work window far away).
        // Over 120 s of game time the cluster must (a) disperse and STOP side-stepping,
        // (b) spend well under 30% of its paced thinks walking, and (c) engage the sparetime
        // occupation engine (eat/slp/fun — sit/sleep/self-fun) instead of wandering.
        [Test, Explicit("Loads real data + BDB, builds the start-meadow terrain; run on demand")]
        public void CampCluster_Disperses_StopsSideStepping_EngagesOccupations()
        {
            UnityEngine.Random.InitState(20260610);   // deterministic irandom/random/get_place picks

            var rootGo = new GameObject("WorldRoot");
            _gos.Add(rootGo);
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            ctx.LocalPlayer = 0;

            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            Assert.IsTrue(ctx.Classes.ContainsKey("Feuerstelle"), "Feuerstelle class not loaded");

            // The campaign's start-meadow terrain (Campaign.tcl lines 2+13 — NavWorldRealTerrainTests).
            interp.Eval("map create 512 640");
            interp.Eval("set temppos {208 0}");
            try { interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[template] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            Assert.IsNotNull(terr!.Nav, "no NavWorld");
            ctx.Terrain = terr;
            ctx.NavService = rootGo.GetComponent<Diggles.Runtime.Navigation.NavWorldService>();
            Assert.IsNotNull(ctx.NavService, "no NavWorldService on the world root");

            // Campfire + prepared food (the eat occupation's offer scan, feuerstelle get_eatobjects
            // = obj_query Grillpilz -range 7). The fire's obj_init registers the 8 guest links and
            // announces `sparetime this announce eat` (feuerstelle.tcl:297-306,326) UNCONDITIONALLY
            // — no buildup gate in the original obj_init, so a RealHost `new Feuerstelle` is the
            // faithful spawn; BuildupComplete mirrors genericprod.tcl:451's pre-placed default.
            var fire = Spawn(interp, ctx, "Feuerstelle", "246 30.5 10");
            fire.Owner = 0;
            fire.BuildupComplete = true;
            for (int k = 0; k < 3; k++)
                if (ctx.Classes.ContainsKey("Grillpilz"))
                    Spawn(interp, ctx, "Grillpilz",
                        (245.5f + k * 0.5f).ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " 30.5 10").Owner = 0;

            // Faithful-prereq gates — if any of these fail the EAT plan is structurally dead and
            // the run's occupation trace is meaningless (these are the exact branch points of
            // sparetime_eat_start, z_spare_procs.tcl:133-141):
            Assert.AreEqual("0", interp.Eval($"prod_guest guestfree {fire.Handle}").Trim(),
                "Feuerstelle obj_init must have registered its guest links (feuerstelle.tcl:297-306)");

            // The post-cutscene formation: 5 Zwerge 0.3 apart laterally — every pair inside the
            // act_when_idle crowd box. Pure sparetime: work window [6,12), now = hour 0.
            var gnomes = new List<DigglesTclEntity>();
            for (int k = 0; k < 5; k++)
            {
                var g = Spawn(interp, ctx, "Zwerg",
                    (249.2f + 0.3f * k).ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " 30.5 10.5");
                g.Owner = 0;
                interp.Eval($"set_worktime {g.Handle} 6 6");
                gnomes.Add(g);
            }

            // More eat-prereq gates, now that an asker exists:
            // the announce field (sparetime_eat_start's place search, z_spare_procs.tcl:133)…
            Assert.That(TclList.Parse(interp.Eval(
                $"sparetime {gnomes[0].Handle} queryrect eat -160 -80 160 80").Trim()),
                Does.Contain(fire.Handle.ToString()),
                "the Feuerstelle must be announced as an eat place (feuerstelle.tcl:326)");
            // …and the ground-food query SHAPE of sparetime_eat_check (z_spare_procs.tcl:72) —
            // ripe, visible, unlocked, not contained:
            Assert.AreNotEqual("0", interp.Eval(
                $"obj_query {gnomes[0].Handle} -class {{Grillpilz}} -boundingbox {{-160 -80 -15 160 80 15}}" +
                " -owner {own -1} -flagpos visible -flagneg {contained locked} -limit 1").Trim(),
                "the Grillpilze must be findable by the sparetime_eat_check query shape");

            var forensics = new CampForensics { Ctx = ctx };
            forensics.Attach(interp);

            const int Ticks = 1200;          // 120 s of game time at the 10 Hz logic tick
            const int LateFrom = Ticks - 300; // the "settled" window = the last 30 s
            var lastAct = new object?[gnomes.Count];
            var walkStarts = new int[gnomes.Count];
            var lateWalkStarts = new int[gnomes.Count];
            int lateOverlapSamples = 0, lateOverlapHits = 0;
            var occupationsSeen = new HashSet<string>();
            bool sawEngagement = false;

            for (int t = 0; t < Ticks; t++)
            {
                sim.Step(0.1);
                for (int k = 0; k < gnomes.Count; k++)
                {
                    var g = gnomes[k];
                    if (!ReferenceEquals(g.ActiveAction, lastAct[k]))
                    {
                        if (IsWalkAction(g.ActiveAction))
                        {
                            walkStarts[k]++;
                            if (t >= LateFrom) lateWalkStarts[k]++;
                        }
                        lastAct[k] = g.ActiveAction;
                    }
                    if (g.VarsScope.Vars.TryGetValue("current_occupation", out var occ) && !string.IsNullOrEmpty(occ))
                        occupationsSeen.Add(occ);
                    // engagement markers: seated somewhere / sleeping / talking
                    if ((g.VarsScope.Vars.TryGetValue("sparetime_current_place_ref", out var pr) && pr != "0" && pr != "")
                        || (g.VarsScope.Vars.TryGetValue("is_sleeping", out var sl) && sl == "1")
                        || (g.VarsScope.Vars.TryGetValue("is_talking", out var tk) && tk == "1"))
                        sawEngagement = true;
                }
                if (t >= LateFrom)
                {
                    for (int x = 0; x < gnomes.Count; x++)
                        for (int y = x + 1; y < gnomes.Count; y++)
                        {
                            lateOverlapSamples++;
                            if (CrowdOverlap(gnomes[x].Position, gnomes[y].Position)) lateOverlapHits++;
                        }
                }
                if (t % 100 == 99)
                {
                    var sb = new System.Text.StringBuilder($"t={sim.GameTime:0.0}s ");
                    foreach (var g in gnomes)
                    {
                        g.VarsScope.Vars.TryGetValue("current_occupation", out var o);
                        g.VarsScope.Vars.TryGetValue("current_wish_occupation", out var w);
                        sb.Append($"[#{g.Handle} {g.CurrentState}/{o}>{w} p=({g.Position.x:0.0},{g.Position.y:0.0},{g.Position.z:0.0}) a={g.ActiveAction?.GetType().Name ?? "-"}] ");
                    }
                    TestContext.WriteLine(sb.ToString());
                }
            }

            float lateOverlapRate = lateOverlapSamples > 0 ? (float)lateOverlapHits / lateOverlapSamples : 0f;
            int lateWalksTotal = 0; foreach (var n in lateWalkStarts) lateWalksTotal += n;
            TestContext.WriteLine($"walkStarts per gnome: {string.Join(",", walkStarts)}");
            TestContext.WriteLine($"late (last 30 s): walkStarts={lateWalksTotal} overlapRate={lateOverlapRate:0.00}");
            TestContext.WriteLine($"occupations seen: {string.Join(",", occupationsSeen)} engagement={sawEngagement}");

            // Per-gnome end-state snapshot (the plan/goal vars of z_spare_main + the live walk
            // bookkeeping) + the branch counters — printed BEFORE the asserts so a red run still
            // carries the full diagnosis.
            foreach (var g in gnomes)
            {
                var v = g.VarsScope.Vars;
                string G(string key) => v.TryGetValue(key, out var s) ? s : "<none>";
                TestContext.WriteLine(
                    $"#{g.Handle}: state={g.CurrentState} plan={G("current_plan")} occ={G("current_occupation")}" +
                    $">{G("current_wish_occupation")} future=({G("sparetime_future")}) goal=({G("sparetime_goal")})" +
                    $" emergency={G("sparetime_emergency")} idletimeout={G("idletimeout")} walkresult={g.WalkResult}" +
                    $" placeref={G("sparetime_current_place_ref")} pos=({g.Position.x:0.0},{g.Position.y:0.0},{g.Position.z:0.0})");
            }
            TestContext.WriteLine(forensics.Summary());

            // (a) dispersal: the act_when_idle crowd query must be (mostly) empty in the settled
            // window — pairs out of each other's {±0.6 ±0.3 ±1.2} box — and side-stepping over.
            Assert.Less(lateOverlapRate, 0.25f,
                "the cluster never dispersed — act_when_idle's crowd box still sees neighbours in the last 30 s");
            Assert.LessOrEqual(lateWalksTotal, 12,
                "side-stepping never stopped — walks keep starting in the last 30 s (the wander ping-pong)");

            // (b) wander bound: the idle state paces ~1 think/s (`action this wait 1`), so 120 s
            // ≈ ≤120 paced thinks per gnome; <30% of them may be walks.
            for (int k = 0; k < gnomes.Count; k++)
                Assert.Less(walkStarts[k], 40,
                    $"gnome #{gnomes[k].Handle} spent ≥30% of its thinks walking ({walkStarts[k]} walks in 120 s) — wander dominates");

            // (c) the sparetime occupation engine engaged: some gnome left "idle" for a real
            // occupation (eat/slp/fun/bth) — sit at the fire / ground-sleep / self-fun.
            bool sawOccupation = occupationsSeen.Contains("eat") || occupationsSeen.Contains("slp")
                || occupationsSeen.Contains("fun") || occupationsSeen.Contains("bth");
            Assert.IsTrue(sawOccupation || sawEngagement,
                $"no sparetime occupation ever engaged (seen: {string.Join(",", occupationsSeen)}) — " +
                "the camp never settles into eat/sleep/fun behaviour");
        }
    }
}
