#nullable enable

namespace Diggles.Tests
{
    using System.Globalization;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // ======================= Wall-dig cluster repro =======================
    // Full-stack headless repro of the wall-dig pathology: real terrain (urw_unq_start),
    // real Zwerg classes, real movement (the gnome keeps its GameObject, so `action walk` builds the
    // MetaWalk/Climb executors instead of the WaitAction degrade) and the unchanged z_dignwalk.tcl dig
    // loop driven through evt_task_dig.
    //
    // Native laws under test (decompile ground truth):
    // • Wall-start law — digging a wall face with no floor at the mouth: vector_fixdig
    // (FUN_00594e00) snaps the standoff to the nearest walkable sub-cell (+0x3c & 1, includes
    // climb cells), then scans ≤8 rows down (+y) for a floor cell and — decompile 00594e00, the
    // band loop before LAB_00595590: `uVar9 = uVar12; if (floor) break; … uVar9 = local_44;` —
    // reverts to the wall cell when no floor exists in the band. The gnome walks/climbs there,
    // hangs (get_gnomeposition == 1, kletterstand — z_procs.tcl:1707) and digs in the wall pose
    // (dig_anim wall branch, z_dignwalk.tcl:516-545). Only once a floor line is carved within the
    // band does the standoff land on it and the pose flips to the floor dig.
    // • Anchor law — while the gnome hangs and digs, dig_continue re-evaluates
    // `vector_fixdig $digpoint [get_pos this]` each cycle; with the faithful step-toward-gnome
    // (f += (g−f)/d², or f = g when d² < 1 — 00594e00:44-50) + the band revert + the faithful Z
    // (z = min(max(gnome.z, h(fx, fy−5)+1), 14.5) — LAB_00595590 reads (local_44−5) for both
    // samples) the standoff converges to the digger's own cell, `walkdist <= nonewwalk` and no
    // new walk is issued — the digger stays anchored while the wall recedes under his pick.
    //
    // Port bugs this reproduces (pre-fix): the +8-rows-down push when no floor found + the Z
    // re-sample at the carve-active body row + the NavWorldService floor-settle of deliberate wall
    // goals ⇒ the standoff never converges ⇒ dig_endanim + walk_pos_dig every dig_continue ⇒ the
    // gnome yo-yos between depth planes / climbs in place / climbs on air.
    [Explicit("Loads real data + BDB + full classinit (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class WallDigReproTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static string Inv(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static (GameObject root, DigglesEngine engine, DiggableTerrain terr, NavWorld nav) BuildStartMeadow()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);   // the Q_x(-90) hack
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            Assert.IsNotNull(terr!.Nav, "no NavWorld on the real terrain");
            engine.Ctx.Terrain = terr;
            engine.Ctx.NavService = rootGo.AddComponent<NavWorldService>();
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { engine.Interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(engine.Ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            return (rootGo, engine, terr, terr.Nav!);
        }

        static (int sx, int sy) FindMeadowAnchor(NavWorld nav)
        {
            for (int sx = 238 * 8; sx < 252 * 8; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        // Fabricate a sheer wall face: carve a 2-wide × 4-deep pit off the meadow line (the DigShaft
        // idiom of NavWorldRealTerrainTests — a 2-node-wide carve leaves an 8-column climb ribbon
        // on each side wall). The wall-dig site is then the pit's west
        // wall one tile below the rim: solid rock, a climbable sheer face, and the nearest floor
        // (the pit bottom) ≥2 tiles below the mouth — outside vector_fixdig's 8-row band, so the
        // wall-start law must engage. The natural cliffs of urw_unq_start all stair-step (~45°,
        // 1-2 wall cells per column), which is why no natural site qualifies.
        static void DigPit(DiggableTerrain terr, NavWorld nav, int ptx, int pty, int nodesWide, int nodesDeep)
        {
            // The production dig chain (mode-1 dummy marquee order → CarveStep to completion →
            // target-11 walk skin), not CarveTileNow: a full-depth (h=0) carve is the special
            // depth-tool shape and leaves depth-0 climb columns — atypical for the real dig
            // scenario under test (the marquee tunnel).
            //
            // The meadow bowl is granite-crusted (unq_start.pmp byte 4 → material type 0):
            // natively undiggable, and CarveStep/dig_next now enforce the material band
            // (FUN_005976b0:36-38 / FUN_00597b20:128-129). These repros pin the wall-dig
            // nav/standoff mechanics, not the material law — paint the pit earth first.
            for (int ty = pty - 2; ty < pty + nodesDeep + 2; ty++)
                for (int tx = ptx - 2; tx < ptx + nodesWide + 2; tx++)
                    if ((uint)tx < (uint)terr.Width && (uint)ty < (uint)terr.Height)
                        terr.Terrain[ty * terr.Width + tx] = 2;   // earth
            for (int ny = pty; ny < pty + nodesDeep; ny++)
                for (int nx = ptx; nx < ptx + nodesWide; nx++)
                    terr.SetDigMark(nx, ny, 0, 0, dummy: true);
            for (int round = 0; round < 96; round++)
            {
                bool any = false;
                for (int ny = pty; ny < pty + nodesDeep; ny++)
                    for (int nx = ptx; nx < ptx + nodesWide; nx++)
                    {
                        int steps = 0;
                        while (terr.CarveStep(nx, ny, 0, out _) && steps < 64) { steps++; any = true; }
                    }
                if (!any) break;
            }
            terr.RevalidateDirtyNav();
            nav.RevalidateRect((ptx - 2) * 8, (pty - 2) * 8,
                (ptx + nodesWide + 2) * 8 + 7, (pty + nodesDeep + 2) * 8 + 7);
        }

        // Nearest floor cell to (sx, sy) within a box scan (row-major upward bias kept small).
        static (int sx, int sy) FindFloorNear(NavWorld nav, int sx, int sy, int range)
        {
            (int sx, int sy) best = default; int bestD = int.MaxValue;
            for (int y = sy - range; y <= sy + range; y++)
                for (int x = sx - range; x <= sx + range; x++)
                    if (nav.SubIsFloor(x, y))
                    {
                        int d = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                        if (d < bestD) { bestD = d; best = (x, y); }
                    }
            return best;
        }

        [Test]
        public void WallDig_TunnelIntoCliff_AnchoredWallPose_NoDepthOscillation()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                var anchor = FindMeadowAnchor(nav);
                Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
                // Pit east of the anchor; its west wall carries the dig site.
                int ptx = anchor.sx / 8 + 6, pty = anchor.sy / 8 + 1;
                DigPit(terr, nav, ptx, pty, nodesWide: 2, nodesDeep: 4);
                DumpClassWindow(nav, ptx - 4, ptx + 5, pty - 2, pty + 6, anchor.sy);
                // sanity: a climb ribbon near the pit's west wall (the exact column depends on how
                // EFFPASSWALK classifies the carved edge — scan a small band)
                int ribbon = 0, ribbonSx = -1;
                for (int sx = ptx * 8 - 2; sx <= ptx * 8 + 14; sx++)
                {
                    int n = 0;
                    for (int sy = pty * 8; sy < (pty + 4) * 8; sy++)
                        if (nav.SubIsClimbWall(sx, sy)) n++;
                    if (n > ribbon) { ribbon = n; ribbonSx = sx; }
                }
                TestContext.WriteLine($"pit x[{ptx}..{ptx + 1}] y[{pty}..{pty + 3}], best west climb column = " +
                    $"sx={ribbonSx} with {ribbon} cells");
                Assert.Greater(ribbon, 4, "the fabricated pit has no climbable west wall");
                // the dig site: 2×2 tunnel westward into the pit's west wall, one tile below the rim
                var site = (wtx: ptx - 1, wy: pty + 1, dir: -1, faceSx: ribbonSx);
                TestContext.WriteLine($"meadow anchor=({anchor.sx},{anchor.sy}) wall site: wtx={site.wtx} wy={site.wy} " +
                    $"dir={site.dir} faceSx={site.faceSx}");

                // Mark the 2×2 standard tunnel into the wall at the mouth rows (mode-1 dummy = the
                // partially-dug main-tunnel order of the original start).
                var marks = new System.Collections.Generic.List<(int tx, int ty)>();
                for (int k = 0; k < 2; k++)
                    for (int r = 0; r < 2; r++)
                        marks.Add((site.wtx + k * site.dir, site.wy + r));
                foreach (var (mtx, mty) in marks) terr.SetDigMark(mtx, mty, 0, 0, dummy: true);
                terr.IncPlayerDigMarkId();

                // Spawn the gnome ON the meadow floor a couple of tiles west of the pit rim.
                var spawn = FindFloorNear(nav, (ptx - 3) * 8 + 4, anchor.sy, 20);
                Assert.AreNotEqual(default((int, int)), spawn, "no spawn floor near the wall base");
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g, "no gnome entity");
                Assert.IsNotNull(g!.GameObject, "gnome must keep its GameObject (real movement repro)");
                TestContext.WriteLine($"gnome spawned at ({g.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00}) handle={h}");
                interp.Eval($"set_worktime {g.Handle} 0 12");
                for (int i = 0; i < 10; i++) sim.Step(0.1);

                // Cycle visibility: gnome_announce_dig is called at dig_starttask entry, per
                // dig_continue candidate and at dig_execute entry (z_dignwalk.tcl:143,227,313) —
                // log each call (cosmetic native; the marker keepalive is irrelevant headless).
                var announceLog = new System.Collections.Generic.List<string>();
                interp.RegisterCommand("gnome_announce_dig", (ii, aa) =>
                {
                    var pp = g.Position;
                    announceLog.Add($"t={sim.GameTime:0.0} announce {(aa.Length > 2 ? aa[2] : "?")} " +
                        $"pos=({pp.x:0.00},{pp.y:0.00},{pp.z:0.00}) act={g.ActiveAction?.GetType().Name ?? "-"} " +
                        $"tasks=[{string.Join(" | ", g.TaskList)}]");
                    // keep the production side effect (the dispatcher's announce record)
                    if (aa.Length >= 3 && ctx.ProdDispatcher != null)
                    {
                        var ap = TclListHead3(aa[2]);
                        if (ap != null) ctx.ProdDispatcher.AnnounceDig(
                            Mathf.RoundToInt(ap.Value.x), Mathf.RoundToInt(ap.Value.y));
                    }
                    return "";
                });

                ctx.This = g;
                interp.Eval($"set_event this evt_task_dig -target {g.Handle} -pos1 {{{marks[0].tx} {marks[0].ty}}}");
                ctx.This = null;

                // ---------------- drive + trace ----------------
                var zs = new System.Collections.Generic.List<float>();   // native depth per step
                var ys = new System.Collections.Generic.List<float>();   // native vertical per step
                var zNearEpisode = new System.Collections.Generic.List<float>();  // depth while at the face
                int zFlipsNear = 0;   // depth reversals accumulated over near-face episodes only
                int walkBuilds = 0, wr3Ticks = 0; object? lastAction = null;
                int wallPoseTicks = 0, floorPoseTicks = 0, wallPoseWhileNoFloor = 0, floorPoseAfterFloor = 0;
                bool mouthFloorSeen = false;
                int stepsRun = 0; int doneAt = -1;
                var poseLog = new StringBuilder();
                for (int i = 0; i < 6000; i++)   // rand-paced swings — give slow orders headroom
                {
                    sim.Step(0.1);
                    stepsRun++;
                    var p = g.Position;
                    zs.Add(p.z); ys.Add(p.y);
                    if (!ReferenceEquals(g.ActiveAction, lastAction))
                    {
                        lastAction = g.ActiveAction;
                        if (g.ActiveAction is WalkResultAction or RetryWalkAction or WalkGuardAction) walkBuilds++;
                    }
                    if (g.WalkResult == 3) wr3Ticks++;   // WS_NOPASSAGE dwell = the FindPath-NULL churn
                    // mouth floor: any floor cell in the tunnel tiles within the mouth rows
                    bool mouthFloor = false;
                    for (int sy = site.wy * 8; sy <= (site.wy + 2) * 8 + 4 && !mouthFloor; sy++)
                        foreach (var (mtx, _) in marks)
                        {
                            for (int sx = mtx * 8; sx < mtx * 8 + 8; sx++)
                                if (nav.SubIsFloor(sx, sy)) { mouthFloor = true; break; }
                            if (mouthFloor) break;
                        }
                    mouthFloorSeen |= mouthFloor;
                    // pose bookkeeping while the gnome is near the dig face (within 2.5 native units)
                    float dNearest = float.MaxValue;
                    foreach (var (mtx, mty) in marks)
                        dNearest = Mathf.Min(dNearest, new Vector2(p.x - mtx, p.y - mty).magnitude);
                    if (dNearest <= 2.5f)
                    {
                        bool onWall = interp.Eval($"get_gnomeposition {g.Handle}").Trim() == "1";
                        if (onWall) { wallPoseTicks++; if (!mouthFloor) wallPoseWhileNoFloor++; }
                        else { floorPoseTicks++; if (mouthFloor) floorPoseAfterFloor++; }
                        zNearEpisode.Add(p.z);
                    }
                    else if (zNearEpisode.Count > 0)
                    {
                        zFlipsNear += CountReversals(zNearEpisode, 0.5f);   // close the episode
                        zNearEpisode.Clear();
                    }
                    if (i % 50 == 0)
                    {
                        int rim0 = terr.RimAtNode(marks[0].tx, marks[0].ty);
                        poseLog.AppendLine($"  step {i}: pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) state={g.CurrentState} " +
                            $"wr={g.WalkResult} walks={walkBuilds} rim0={rim0} mouthFloor={mouthFloor} " +
                            $"gp={interp.Eval($"get_gnomeposition {g.Handle}").Trim()} tasks={g.TaskList.Count} " +
                            $"marks={CountMarks(terr, marks)}");
                    }
                    if (CountMarks(terr, marks) == 0 && g.TaskList.Count == 0 && g.ActiveAction == null)
                    { doneAt = i; break; }
                }
                TestContext.WriteLine(poseLog.ToString());
                TestContext.WriteLine($"[ANNOUNCE] {announceLog.Count} calls; the last 60:");
                for (int i = Mathf.Max(0, announceLog.Count - 60); i < announceLog.Count; i++)
                    TestContext.WriteLine("  " + announceLog[i]);

                // ---------------- post-mortem (the frozen/churn state) ----------------
                {
                    var p = g.Position;
                    int bsx = Mathf.RoundToInt(p.x * 8f), bsy = Mathf.RoundToInt(p.y * 8f);
                    TestContext.WriteLine($"[POST] gnome native=({p.x:0.000},{p.y:0.000},{p.z:0.000}) sub=({bsx},{bsy}) " +
                        $"SubAt={nav.SubAt(bsx, bsy)} tileBase={TileBase(nav, bsx, bsy)} h={nav.GetHmapSub(bsx, bsy)}");
                    ctx.This = g;
                    string edge = "", dnext = "", fixd = "";
                    try
                    {
                        edge = interp.Eval($"get_digedge {{{Inv(marks[0].tx)} {Inv(marks[0].ty)} 8}} {g.Handle}").Trim();
                        dnext = interp.Eval($"dig_next {{{Inv(p.x)} {Inv(p.y)} {Inv(p.z)}}} {g.Handle} 0").Trim();
                        var dn = TclListHead3(dnext);
                        if (dn != null) fixd = interp.Eval($"vector_fixdig {{{dnext}}} {{{Inv(p.x)} {Inv(p.y)} {Inv(p.z)}}}").Trim();
                    }
                    finally { ctx.This = null; }
                    TestContext.WriteLine($"[POST] get_digedge={edge} | dig_next={dnext} | vector_fixdig={fixd}");
                    // decisive carve-path probes: the dig_execute gates + one direct dig_apply
                    ctx.This = g;
                    try
                    {
                        string attrib = interp.Eval("get_attrib this exp_Stein").Trim();
                        string mat = interp.Eval($"get_material {{{dnext}}}").Trim();
                        int randPass = 0;
                        for (int r = 0; r < 20; r++)
                            if (interp.Eval("expr {rand()<0.3}").Trim() == "1") randPass++;
                        var dn = TclListHead3(dnext);
                        string dist = dn == null ? "-" : interp.Eval(
                            $"vector_dist {{{Inv(dn.Value.x)} {Inv(dn.Value.y)} {Inv(dn.Value.z - 1)}}} " +
                            $"{{{Inv(g.Position.x)} {Inv(g.Position.y - 0.8f)} {Inv(g.Position.z)}}}").Trim();
                        TestContext.WriteLine($"[POST] exp_Stein={attrib} material={mat} rand<0.3 → {randPass}/20 " +
                            $"vector_dist(digpoint,thispos)={dist}");
                        if (dn != null)
                        {
                            int ax = Mathf.RoundToInt(dn.Value.x), ay = Mathf.RoundToInt(dn.Value.y);
                            int rimB = terr.RimAtNode(ax, ay);
                            bool markB = terr.IsDigMarked(ax, ay, 0);
                            string ap = interp.Eval($"dig_apply {{{dnext}}} {g.Handle}").Trim();
                            TestContext.WriteLine($"[POST] dig_apply({ax},{ay}) = {ap}; rim {rimB}→{terr.RimAtNode(ax, ay)} " +
                                $"mark {markB}→{terr.IsDigMarked(ax, ay, 0)}");
                        }
                    }
                    finally { ctx.This = null; }
                    var fx3 = TclListHead3(fixd);
                    if (fx3 != null && ctx.SpawnParent != null)
                    {
                        var t3 = fx3.Value;
                        int tsx = Mathf.RoundToInt(t3.x * 8f), tsy = Mathf.RoundToInt(t3.y * 8f);
                        TestContext.WriteLine($"[POST] walk target sub=({tsx},{tsy}) SubAt={nav.SubAt(tsx, tsy)} " +
                            $"tileBase={TileBase(nav, tsx, tsy)} h={nav.GetHmapSub(tsx, tsy)}");
                        var svc = ctx.NavService!;
                        Vector3 wS = ctx.SpawnParent.TransformPoint(RuntimeWorldSpawner.NativeToLocal(p));
                        Vector3 wT = ctx.SpawnParent.TransformPoint(RuntimeWorldSpawner.NativeToLocal(t3));
                        var pm = svc.FindPath(wS, wT);
                        TestContext.WriteLine($"[POST] FindPath body→target = {(pm == null ? "NULL" : pm.Count + " wps")}");
                        // sub-grid window around the body
                        for (int sy = bsy - 10; sy <= bsy + 10; sy++)
                        {
                            var sb = new StringBuilder($"[POST] y{sy,4} ");
                            for (int sx = bsx - 12; sx <= bsx + 12; sx++)
                                sb.Append((nav.SubAt(sx, sy) & 3) switch { 3 => 'F', 1 => 'W', _ => '.' });
                            sb.Append("   h:");
                            for (int sx = bsx - 12; sx <= bsx + 12; sx++)
                                sb.Append(nav.GetHmapSub(sx, sy).ToString("X1"));
                            TestContext.WriteLine(sb.ToString());
                        }
                    }
                }

                // ---------------- metrics ----------------
                if (zNearEpisode.Count > 0) zFlipsNear += CountReversals(zNearEpisode, 0.5f);
                int zFlips = CountReversals(zs, 0.5f);
                int yFlips = CountReversals(ys, 0.5f);
                float zTravel = TotalTravel(zs), yTravel = TotalTravel(ys);
                bool marksCleared = CountMarks(terr, marks) == 0;
                TestContext.WriteLine($"RESULT: steps={stepsRun} doneAt={doneAt} walkBuilds={walkBuilds} wr3Ticks={wr3Ticks} " +
                    $"zFlips={zFlips} (nearFace={zFlipsNear}) zTravel={zTravel:0.0} yFlips={yFlips} yTravel={yTravel:0.0}");
                TestContext.WriteLine($"        wallPoseTicks={wallPoseTicks} (whileNoMouthFloor={wallPoseWhileNoFloor}) " +
                    $"floorPoseTicks={floorPoseTicks} (afterFloor={floorPoseAfterFloor}) mouthFloorSeen={mouthFloorSeen} " +
                    $"marksCleared={marksCleared}");
                DumpClassWindow(nav, site.wtx - 3, site.wtx + 4, site.wy - 2, anchor.sy / 8 + 2, anchor.sy);

                // ---------------- the laws ----------------
                // Bounds calibrated against the measured pre-fix pathology (walkBuilds=303,
                // zTravel=147.5, dig stalled/never opened a floor, ended frozen on the wall in a
                // FindPath-NULL storm) vs the fixed run (walkBuilds=10, zTravel=58, dig complete,
                // gnome standing on the tunnel floor).
                Assert.IsTrue(marksCleared, $"the tunnel order never completed ({CountMarks(terr, marks)} marks left — dig loop stalled)");
                // Wall-start law: before any floor existed at the mouth, near-face digging happened
                // from the wall (kletterstand), not from thin air / the floor.
                Assert.Greater(wallPoseWhileNoFloor, 0,
                    "the gnome never dug from the wall pose while the mouth had no floor (wall-start law)");
                // Floor-transition law: the carve opens a floor at the mouth (the tunnel becomes
                // standable).
                Assert.IsTrue(mouthFloorSeen, "the carve never produced a floor line at the tunnel mouth");
                // Anchor / no-churn law: the standoff converges — no walk-per-cycle storm and no
                // FindPath-NULL churn. Pre-fix: 303 walk builds plus a permanent WS_NOPASSAGE
                // storm; healthy runs build ≤ ~20 walks (one per standoff change / dispatch round)
                // and read NOPASSAGE only in stray ticks. The depth-teleport symptom is driven by
                // these rewalks — the z-flip counts themselves are dominated by legitimate commute
                // legs between the rim / pit / mouth surfaces in this small arena, so they are
                // reported above but the churn counters are the assertable signature.
                Assert.LessOrEqual(walkBuilds, 60,
                    $"walk churn: {walkBuilds} walk actions built (rewalk storm — the anchor law is broken)");
                Assert.LessOrEqual(wr3Ticks, 200,
                    $"pathfind churn: {wr3Ticks} ticks in WS_NOPASSAGE (the FindPath-NULL storm)");
                // Enter law (descend/enter the carved tunnel): a fresh walk into the tunnel arrives.
                var goal = FindTunnelFloor(nav, marks, site.wy);
                TestContext.WriteLine($"tunnel floor goal = {goal}");
                if (goal != default)
                {
                    float goalZ = Mathf.Min(nav.GetHmapSub(goal.sx, goal.sy) + 1, 14.5f);
                    SilenceAi(interp, ctx, g);
                    ctx.This = g;
                    interp.Eval($"action {g.Handle} walk \"-target {{{Inv(goal.sx / 8f)} {Inv(goal.sy / 8f)} {Inv(goalZ)}}}\"");
                    ctx.This = null;
                    for (int i = 0; i < 600 && g.ActiveAction != null; i++) sim.Step(0.1);
                    var gp = g.Position;
                    TestContext.WriteLine($"enter-tunnel walk: wr={g.WalkResult} pos=({gp.x:0.00},{gp.y:0.00},{gp.z:0.00}) " +
                        $"goal=({goal.sx / 8f:0.00},{goal.sy / 8f:0.00},{goalZ:0.00})");
                    Assert.AreEqual(4, g.WalkResult, "walk into the freshly dug tunnel did not arrive");
                    Assert.Less(new Vector2(gp.x - goal.sx / 8f, gp.y - goal.sy / 8f).magnitude, 1.5f,
                        "the gnome did not physically reach the tunnel floor");
                }
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // Digs a shaft down from the meadow through the real Tcl loop, then descends into it.
        [Test]
        public void ShaftDig_ThenDescendIntoOwnShaft()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                var anchor = FindMeadowAnchor(nav);
                Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
                int ptx = anchor.sx / 8 + 3, pty = anchor.sy / 8 + 1;
                // Earth-paint the shaft site — the meadow bowl crust is granite (material 0),
                // natively undiggable under the FUN_005976b0/FUN_00597b20 material band; this
                // test pins the shaft dig/descend chain, not the material law (see DigPit note).
                for (int ty = pty - 2; ty < pty + 5; ty++)
                    for (int tx = ptx - 2; tx < ptx + 4; tx++)
                        if ((uint)tx < (uint)terr.Width && (uint)ty < (uint)terr.Height)
                            terr.Terrain[ty * terr.Width + tx] = 2;   // earth
                var marks = new System.Collections.Generic.List<(int tx, int ty)>();
                for (int ny = pty; ny < pty + 3; ny++)
                    for (int nx = ptx; nx < ptx + 2; nx++)
                        marks.Add((nx, ny));
                foreach (var (mtx, mty) in marks) terr.SetDigMark(mtx, mty, 0, 0, dummy: true);
                terr.IncPlayerDigMarkId();
                TestContext.WriteLine($"shaft marks x[{ptx}..{ptx + 1}] y[{pty}..{pty + 2}], meadow line subY={anchor.sy}");

                var spawn = FindFloorNear(nav, ptx * 8 - 3 * 8, anchor.sy, 20);
                Assert.AreNotEqual(default((int, int)), spawn, "no spawn floor near the shaft");
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome GameObject");
                TestContext.WriteLine($"gnome spawned at ({g!.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00})");
                interp.Eval($"set_worktime {g.Handle} 0 12");
                for (int i = 0; i < 10; i++) sim.Step(0.1);

                ctx.This = g;
                interp.Eval($"set_event this evt_task_dig -target {g.Handle} -pos1 {{{ptx} {pty}}}");
                ctx.This = null;

                var ys = new System.Collections.Generic.List<float>();
                var zs = new System.Collections.Generic.List<float>();
                int doneAt = -1;
                for (int i = 0; i < 4000; i++)
                {
                    sim.Step(0.1);
                    ys.Add(g.Position.y); zs.Add(g.Position.z);
                    if (i % 100 == 0)
                        TestContext.WriteLine($"  step {i}: pos=({g.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00}) " +
                            $"state={g.CurrentState} wr={g.WalkResult} tasks={g.TaskList.Count} " +
                            $"marked={CountMarks(terr, marks)}");
                    if (CountMarks(terr, marks) == 0 && g.TaskList.Count == 0 && g.ActiveAction == null)
                    { doneAt = i; break; }
                }
                int zFlips = CountReversals(zs, 0.5f), yFlips = CountReversals(ys, 0.5f);
                TestContext.WriteLine($"RESULT: doneAt={doneAt} marksLeft={CountMarks(terr, marks)} zFlips={zFlips} yFlips={yFlips}");
                Assert.AreEqual(0, CountMarks(terr, marks), "the shaft order never completed");

                // descend: walk to the deepest floor cell of the shaft.
                (int sx, int sy) bottom = default;
                for (int sy = (pty + 4) * 8; sy > anchor.sy && bottom == default; sy--)
                    for (int sx = ptx * 8 - 4; sx <= (ptx + 2) * 8 + 4; sx++)
                        if (nav.SubIsFloor(sx, sy)) { bottom = (sx, sy); break; }
                Assert.AreNotEqual(default((int, int)), bottom, "no floor at the shaft bottom");
                float bz = Mathf.Min(nav.GetHmapSub(bottom.sx, bottom.sy) + 1, 14.5f);
                // walk him out first so the descend is a real re-entry, not "already there".
                SilenceAi(interp, ctx, g);
                ctx.This = g;
                interp.Eval($"action {g.Handle} walk \"-target {{{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}}}\"");
                ctx.This = null;
                for (int i = 0; i < 900 && g.ActiveAction != null; i++) sim.Step(0.1);
                TestContext.WriteLine($"walk-out: wr={g.WalkResult} pos=({g.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00})");
                ctx.This = g;
                interp.Eval($"action {g.Handle} walk \"-target {{{Inv(bottom.sx / 8f)} {Inv(bottom.sy / 8f)} {Inv(bz)}}}\"");
                ctx.This = null;
                for (int i = 0; i < 900 && g.ActiveAction != null; i++) sim.Step(0.1);
                var p = g.Position;
                TestContext.WriteLine($"descend walk: wr={g.WalkResult} pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) " +
                    $"bottom=({bottom.sx / 8f:0.00},{bottom.sy / 8f:0.00})");
                Assert.AreEqual(4, g.WalkResult, "descend into the freshly dug shaft did not arrive");
                Assert.Less(Mathf.Abs(p.y - bottom.sy / 8f), 1.0f,
                    "the gnome did not physically reach the shaft bottom row");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------- helpers ----------------------------------

        static int TileBase(NavWorld nav, int sx, int sy)
            => (uint)(sx >> 3) < (uint)nav.Width && (uint)(sy >> 3) < (uint)nav.Height
                ? nav.BaseCost[(sy >> 3) * nav.Width + (sx >> 3)] : -99;

        static Vector3? TclListHead3(string list)
        {
            var parts = list.Split(new[] { ' ', '\t', '{', '}' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return null;
            if (y <= 0) return null;   // sentinel
            return new Vector3(x, y, z);
        }

        // Stop the gnome's autonomous AI so a direct `action walk` order is not replaced mid-flight
        // (the prod dispatcher re-assigns idle gnomes; a replaced walk leaves WalkResult=1).
        static void SilenceAi(Diggles.Runtime.Tcl.TclInterpreter interp, DigglesTclContext ctx, DigglesTclEntity g)
        {
            g.ProdAutoSchedule = false;
            ctx.This = g;
            try
            {
                interp.Eval("tasklist_clear this");
                interp.Eval("state_disable this");
            }
            finally { ctx.This = null; }
        }

        static int CountMarks(DiggableTerrain terr, System.Collections.Generic.List<(int tx, int ty)> marks)
        {
            int n = 0;
            foreach (var (mtx, mty) in marks) if (terr.IsDigMarked(mtx, mty, 0)) n++;
            return n;
        }

        static (int sx, int sy) FindTunnelFloor(NavWorld nav,
            System.Collections.Generic.List<(int tx, int ty)> marks, int wy)
        {
            foreach (var (mtx, _) in marks)
                for (int sy = wy * 8 - 4; sy <= (wy + 2) * 8 + 12; sy++)
                    for (int sx = mtx * 8; sx < mtx * 8 + 8; sx++)
                        if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        // Direction reversals of a series with hysteresis `eps`: a flip counts only after the value
        // moved ≥ eps in one direction and then ≥ eps back.
        static int CountReversals(System.Collections.Generic.List<float> v, float eps)
        {
            int flips = 0, dir = 0;
            float anchor = v.Count > 0 ? v[0] : 0f;
            foreach (var x in v)
            {
                float d = x - anchor;
                if (dir >= 0 && d <= -eps) { if (dir > 0) flips++; dir = -1; anchor = x; }
                else if (dir <= 0 && d >= eps) { if (dir < 0) flips++; dir = +1; anchor = x; }
                else if ((dir > 0 && d > 0) || (dir < 0 && d < 0)) anchor = x;
            }
            return flips;
        }

        static float TotalTravel(System.Collections.Generic.List<float> v)
        {
            float t = 0f;
            for (int i = 1; i < v.Count; i++) t += Mathf.Abs(v[i] - v[i - 1]);
            return t;
        }

        static void DumpClassWindow(NavWorld nav, int tx0, int tx1, int ty0, int ty1, int anchorSy)
        {
            TestContext.WriteLine($"[CLASS] +0x3c (F=floor, W=wall, .=none) tiles x[{tx0}..{tx1}] y[{ty0}..{ty1}]:");
            for (int sy = ty0 * 8; sy < (ty1 + 1) * 8; sy++)
            {
                var sb = new StringBuilder($"y{sy,4}{(sy == anchorSy ? '*' : ' ')}");
                for (int sx = tx0 * 8; sx < (tx1 + 1) * 8; sx++)
                    sb.Append((nav.SubAt(sx, sy) & 3) switch { 3 => 'F', 1 => 'W', _ => '.' });
                TestContext.WriteLine(sb.ToString());
            }
        }
    }
}
