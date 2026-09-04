#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // ============================================================================================
    // PLAY ROUND (post depth-fix) — the two residual dig-nav quirks:
    //
    // (1) DIG-ORDER DETOUR:; — 
    // Native law: dig_starttask (z_dignwalk.tcl:136-152) walks to get_digedge's standoff —
    // FUN_005981d0 floods from the marker only through the player's node+0x08 dig-mark nibble
    // (0x598424-0x5985a4), scores open/template-byte0<0x0c and FoW-explored frontier tiles,
    // and ranks them by squared distance to the DWARF with strict < (0x59836f-0x598408;
    // ref packing FUN_00635180:153-158). For a dwarf already inside the marked pit the open
    // mouth nearest him is his own line: the native digs IN PLACE, no rim
    // round-trip. The repro fires the REAL order (set_event evt_task_dig, the native
    // FUN_0052c510 command shape) at a gnome parked on the pit floor and audits his peak
    // RISE above the pit line before the marks start burning down.
    //
    // (2) POST-DIG DISMOUNT Z-EMBED: 
    //. The climb→floor seam: the boundary-claim waypoint rides the WALL record z
    // (FUN_0040bb70:115-139 copy), and the port's LOS 3-point floor leg then LERPS that deep
    // z across the whole corridor toward the landing — mid-lerp the body depth is smaller
    // (deeper) than its own column's open pocket ⇒ rendered inside rock. The native never
    // lerps that seam: the floor leg re-derives its start from the live body
    // (FUN_0041aac0:127-129) and every densified column waypoint is band-resolved
    // (FUN_0041d8a0 rows) — per-column depth, no long raw ramp.
    // ============================================================================================
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class NavDigOrderDismountTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static string Inv(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static (GameObject root, DigglesEngine engine, DiggableTerrain terr, NavWorld nav) BuildStartMeadow(bool classes)
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
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
            engine.Ctx.NavService = rootGo.GetComponent<NavWorldService>() ?? rootGo.AddComponent<NavWorldService>();
            if (classes)
            {
                foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                    try { engine.Interp.Eval("call " + s); }
                    catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
                Assert.IsTrue(engine.Ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            }
            return (rootGo, engine, terr, terr.Nav!);
        }

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

        static (int tx, (int sx, int sy) floor) FindDiggableSurfaceColumn(DiggableTerrain terr, NavWorld nav)
        {
            for (int tx = 212; tx <= 266; tx++)
            {
                var f = FindFloorNear(nav, tx * 8 + 4, 30 * 8, 20);
                if (f == default || f.sx / 8 != tx) continue;
                int ty = f.sy / 8;
                if (terr.TileMaterialDiggable(tx, ty + 1) && terr.TileMaterialDiggable(tx, ty + 2))
                    return (tx, f);
            }
            return (-1, default);
        }

        static bool CellAllowed(DiggableTerrain t, int tx, int ty)
            => DigMarquee.CellValid(tx, ty, t.Width, t.Height,
                (x, y) => t.NwCornerHeight(x, y) > 1,
                (x, y) => (t.RawDigNibble(x, y, 0) & 8) != 0,
                (x, y) => t.TileMaterialDiggable(x, y));

        static List<(int tx, int ty)> CommitMarquee(DiggableTerrain terr, int ax, int ay, int bx, int by)
        {
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(ax, ay, bx, by);
            var marks = new List<(int, int)>();
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!DigMarquee.CellMarkable(tx, ty, standardTunnel: true)) continue;
                    if (!CellAllowed(terr, tx, ty)) continue;
                    terr.SetDigMark(tx, ty, 0, 0, dummy: true);
                    marks.Add((tx, ty));
                }
            terr.IncPlayerDigMarkId();
            return marks;
        }

        static int CarveOut(DiggableTerrain terr, List<(int tx, int ty)> marks)
        {
            int total = 0;
            for (int round = 0; round < 96; round++)
            {
                bool any = false;
                foreach (var (nx, ny) in marks)
                {
                    if (!terr.IsDigMarked(nx, ny, 0) || !terr.TileExposed(nx, ny)) continue;
                    while (terr.CarveStep(nx, ny, 0, out _)) { any = true; if (++total > 8192) return total; }
                }
                if (!any) break;
            }
            return total;
        }

        static void DumpWindow(NavWorld nav, int tx0, int tx1, int ty0, int ty1)
        {
            TestContext.WriteLine($"[CLASS+H] tiles x[{tx0}..{tx1}] y[{ty0}..{ty1}]:");
            for (int sy = ty0 * 8; sy < (ty1 + 1) * 8; sy++)
            {
                var sb = new StringBuilder($"y{sy,4} ");
                for (int sx = tx0 * 8; sx < (tx1 + 1) * 8; sx++)
                    sb.Append((nav.SubAt(sx, sy) & 3) switch { 3 => 'F', 1 => 'W', _ => '.' });
                sb.Append("  h:");
                for (int sx = tx0 * 8; sx < (tx1 + 1) * 8; sx++)
                    sb.Append(nav.GetHmapSub(sx, sy).ToString("X1"));
                TestContext.WriteLine(sb.ToString());
            }
        }

        // ------------------------------------------------------------------------------------
        // QUIRK 1 — the player's exact flow: pit + deeper mark + gnome AT the bottom + dig order.
        // Scenario A (run-5 baseline, PASSED): idle work window, mode-0 deeper marks.
        // Scenario B (the Play deltas): OPEN work window + live ProdDispatcher sweeps + the
        // TUNNEL-target deeper marks the dig tool actually paints — if the detour comes from the
        // dispatcher hijacking the fresh user order (autoprod chore on the surface) or the
        // tunnel-mark rim gate displacing the mark set, B reproduces while A stays green.
        // ------------------------------------------------------------------------------------
        [Test]
        public void DigOrderAtPitBottom_StartsDiggingInPlace_NoRimClimbDetour()
            => RunDigOrderAtPitBottom(openWindow: false, tunnelMarks: false);

        [Test]
        public void DigOrderAtPitBottom_OpenWindow_TunnelMarks_NoRimClimbDetour()
            => RunDigOrderAtPitBottom(openWindow: true, tunnelMarks: true);

        void RunDigOrderAtPitBottom(bool openWindow, bool tunnelMarks)
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                // 1) the pit: marquee shaft down the earth chimney, carved OUT (the player's old dig).
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                int surfTy = digFloor.sy / 8;
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 8f);
                var marks = CommitMarquee(terr, ax, ay, bx, by);
                Assert.Greater(marks.Count, 3, "pit marquee painted too few");
                TestContext.WriteLine($"pit carved in {CarveOut(terr, marks)} steps (tiles y {ay}..{by})");
                nav.RevalidateRect((ax - 2) * 8, (ay - 2) * 8, (ax + 4) * 8 + 7, (by + 4) * 8 + 7);

                // 2) the pit's BOTTOM interior line.
                (int sx, int sy) bottom = default;
                for (int sy = (by + 3) * 8; sy > (ay + 1) * 8 && bottom == default; sy--)
                    for (int sx = ax * 8; sx <= (ax + 2) * 8; sx++)
                        if (nav.SubIsFloor(sx, sy)) { bottom = (sx, sy); break; }
                Assert.AreNotEqual(default((int, int)), bottom, "pit has no interior bottom line");
                int botTy = bottom.sy / 8;
                float botSeat = Mathf.Min(nav.GetHmapSub(bottom.sx, bottom.sy - 1) + 1, 14.5f);
                TestContext.WriteLine($"pit bottom line: sub({bottom.sx},{bottom.sy}) tileY={botTy} seat z={botSeat:0.0} " +
                    $"(surface tileY={surfTy}, depth {botTy - surfTy} tiles)");
                DumpWindow(nav, ax - 1, ax + 3, ay - 1, botTy + 2);

                // 3) the DEEPER mark. Scenario A: mode-0 full-depth cells (always re-markable).
                // Scenario B: the DIG TOOL's own marquee (standard tunnel targets) dragged
                // from the pit bottom downward — the rim re-mark gate then decides which
                // cells actually paint (completed-tunnel cells drop, virgin below-cells keep).
                var deeper = new List<(int, int)>();
                if (tunnelMarks)
                {
                    var (dax, day) = DigMarquee.SnapAnchor(ax, botTy + 1f);
                    var (dbx, dby) = DigMarquee.SnapDragCorner(dax, day, dax, day + 3f);
                    deeper.AddRange(CommitMarquee(terr, dax, day, dbx, dby));
                }
                else
                {
                    for (int ty = botTy; ty <= botTy + 2; ty++)
                        for (int tx = ax; tx <= ax + 1; tx++)
                        {
                            if (!terr.TileMaterialDiggable(tx, ty)) continue;
                            if (!terr.MarkRimGate(tx, ty, 0 | 8)) continue;
                            terr.SetDigMark(tx, ty, 0, cnt: 0, dummy: false);
                            deeper.Add((tx, ty));
                        }
                    terr.IncPlayerDigMarkId();
                }
                Assert.Greater(deeper.Count, 0, "no deeper-dig cells painted under the pit floor");
                var markTile = deeper[0];
                var markNat = new Vector3(markTile.Item1 + 0.5f, markTile.Item2 + 0.5f, 14f);
                TestContext.WriteLine($"deeper marks: {deeper.Count} cells from tileY {botTy}; click mark = {markNat}");

                // 4) the gnome: spawn on the surface (real spawn chain), then PARK on the pit
                // bottom line (harness scaffolding — the player's gnome stood there).
                var spawn = FindFloorNear(nav, digTx * 8 - 14, digFloor.sy, 24);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f))}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                // Scenario A: closed window (order-only). Scenario B: the Play state — window
                // OPEN + live dispatcher sweeps (does autoprod hijack the fresh dig order?).
                interp.Eval($"set_worktime {g.Handle} 0 {(openWindow ? 12 : 0)}");
                for (int i = 0; i < 80; i++) sim.Step(0.025);
                var seatN = new Vector3(bottom.sx / 8f, bottom.sy / 8f, botSeat);
                g.GameObject!.transform.position = W(seatN);
                g.Position = seatN;
                for (int i = 0; i < 40; i++) sim.Step(0.025);
                // re-park if the idle wander drifted him off the line before the order
                g.GameObject.transform.position = W(seatN);
                g.Position = seatN;
                TestContext.WriteLine($"gnome parked at ({seatN.x:0.00},{seatN.y:0.00},{seatN.z:0.0})");

                // 5) LAYER MATRIX before the order (which layer would send him to the rim?).
                string edge = interp.Eval($"get_digedge {{{Inv(markNat.x)} {Inv(markNat.y)} {Inv(markNat.z)}}} {g.Handle}").Trim();
                TestContext.WriteLine($"[L1] get_digedge(mark, gnome) = {edge}");
                string wfix = interp.Eval($"vector_fixdig {{{edge}}} {{{Inv(seatN.x)} {Inv(seatN.y)} {Inv(seatN.z)}}}").Trim();
                TestContext.WriteLine($"[L2] vector_fixdig(edge, gnome) = {wfix}");
                string vfx = interp.Eval($"vector_fix {{{wfix}}}").Trim();
                TestContext.WriteLine($"[L3] vector_fix(walkpos) = {vfx}");
                var svc = (NavWorldService)ctx.NavService!;
                var wp3 = vfx.Split(' ');
                if (wp3.Length >= 3
                    && float.TryParse(wp3[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float wx)
                    && float.TryParse(wp3[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float wy)
                    && float.TryParse(wp3[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float wz))
                {
                    var plan = svc.FindPath(g.GameObject.transform.position, W(new Vector3(wx, wy, wz)));
                    if (plan == null) TestContext.WriteLine($"[L4] FindPath(gnome→walkpos) = NULL ({NavWorldService.LastNullReason})");
                    else
                    {
                        float minY = float.MaxValue;
                        int walls = 0;
                        foreach (var wpt in plan)
                        {
                            var n0 = N(wpt.Pos);
                            if (n0.y < minY) minY = n0.y;
                            if (wpt.IsWall) walls++;
                        }
                        TestContext.WriteLine($"[L4] FindPath(gnome→walkpos): {plan.Count} wps, {walls} wall, " +
                            $"highest point y={minY:0.00} (gnome y={seatN.y:0.00}, rim y≈{digFloor.sy / 8f:0.00})");
                    }
                }

                // 5b) START-POSITION MATRIX: the post-dig body seat hugs the carve face (the
                // vector_fixdig pull), so the live (x,y) often rounds INTO the shaft-face
                // W column, not the clean line cell the park uses. Plan from every pit-
                // bottom start flavour (line cells + the wall-base W cells above the line)
                // to the walkpos and audit each plan's peak rise — the Calc1-route-via-rim
                // hypothesis dies or lives here.
                if (wp3.Length >= 3)
                {
                    var startsTried = new List<string>();
                    float worstRise = 0f; string worstLabel = "";
                    for (int sx2 = ax * 8 - 2; sx2 <= (ax + 2) * 8 + 2; sx2++)
                    {
                        for (int sy2 = bottom.sy - 10; sy2 <= bottom.sy + 2; sy2++)
                        {
                            byte v = (byte)(nav.SubAt(sx2, sy2) & 3);
                            if (v == 0) continue;
                            bool isWallCell = v == 1;
                            // only wall cells NEAR the line (the realistic post-dig hug band)
                            if (isWallCell && bottom.sy - sy2 > 8) continue;
                            float sz = isWallCell
                                ? NavWorldService.SupportDepth(nav, sx2, sy2)
                                : Mathf.Min(nav.GetHmapSub(sx2, sy2 - 1) + 1, 14.5f);
                            var p2 = svc.FindPath(
                                W(new Vector3(sx2 * 0.125f, sy2 * 0.125f, sz)),
                                W(new Vector3(
                                    float.Parse(wp3[0], CultureInfo.InvariantCulture),
                                    float.Parse(wp3[1], CultureInfo.InvariantCulture),
                                    float.Parse(wp3[2], CultureInfo.InvariantCulture))));
                            if (p2 == null) { startsTried.Add($"({sx2},{sy2},{v}): NULL"); continue; }
                            float minY2 = float.MaxValue;
                            foreach (var wpt in p2) { var n2 = N(wpt.Pos); if (n2.y < minY2) minY2 = n2.y; }
                            float rise2 = sy2 * 0.125f - minY2;
                            if (rise2 > worstRise) { worstRise = rise2; worstLabel = $"start=({sx2},{sy2}) val={v}"; }
                        }
                    }
                    TestContext.WriteLine($"[L5] start matrix: worst plan rise above its start = " +
                        $"{worstRise:0.00} tiles ({worstLabel}); nulls: {startsTried.Count}");
                    if (startsTried.Count > 0 && startsTried.Count < 12)
                        TestContext.WriteLine("  " + string.Join("\n  ", startsTried));
                    Assert.LessOrEqual(worstRise, 2.0f,
                        $"a pit-bottom start plans a route OVER THE RIM to an in-pit dig standoff ({worstLabel}) " +
                        "— the Play 'climbs up first' detour");
                }

                // 6) the REAL user order — the native command shape (FUN_0052c510 loop):
                // set_event <unit> evt_task_dig -pos1 <the clicked mark>.
                interp.Eval($"set_event {g.Handle} evt_task_dig -pos1 {{{Inv(markNat.x)} {Inv(markNat.y)} {Inv(markNat.z)}}}");

                // 7) run; audit the peak RISE above the pit-bottom seat before digging starts.
                var disp = ctx.ProdDispatcher;
                int marksLeft0 = deeper.Count;
                float minYSeen = seatN.y;                       // native y: smaller = higher (world up)
                int firstBurnStep = -1;
                var traj = new StringBuilder();
                int step = 0, maxSteps = (int)(600.0 / 0.025);
                for (; step < maxSteps; step++)
                {
                    sim.Step(0.025);
                    if (openWindow)
                    {
                        if (step % 40 == 0) disp?.SweepNow(0);
                        if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");
                    }
                    Vector3 pn = N(g.GameObject.transform.position);
                    if (pn.y < minYSeen) minYSeen = pn.y;
                    int left = 0; foreach (var (mtx2, mty2) in deeper) if (terr.IsDigMarked(mtx2, mty2, 0)) left++;
                    if (left < marksLeft0 && firstBurnStep < 0)
                    {
                        firstBurnStep = step;
                        TestContext.WriteLine($"step {step} (t={step * 0.025:0.0}s): first mark burned; " +
                            $"pos=({pn.x:0.00},{pn.y:0.00},{pn.z:0.00})");
                    }
                    if (step % 200 == 0 && traj.Length < 3000)
                        traj.AppendLine($"  t={step * 0.025:0.0}s pos=({pn.x:0.00},{pn.y:0.00},{pn.z:0.00}) " +
                            $"act={g.ActiveAction?.GetType().Name ?? "-"} state={g.CurrentState} marksLeft={left}");
                    if (left == 0) break;
                }
                TestContext.WriteLine(traj.ToString());
                float rise = seatN.y - minYSeen;   // tiles risen above the parked seat
                TestContext.WriteLine($"RESULT: firstBurn t={(firstBurnStep < 0 ? -1 : firstBurnStep * 0.025):0.0}s " +
                    $"peak rise above pit bottom = {rise:0.00} tiles (pit depth {bottom.sy / 8f - digFloor.sy / 8f:0.0})");

                Assert.GreaterOrEqual(firstBurnStep, 0, "the dig order never burned a mark — the digger stalled");
                // The native digs IN PLACE: the marked flood scores the open, explored frontier
                // nearest the dwarf (FUN_005981d0 0x59836f-0x598408). Rising more than ~2
                // tiles toward the rim before digging = the Play detour.
                Assert.LessOrEqual(rise, 2.0f,
                    "the digger climbed toward the rim before starting a dig whose mark sits under his feet");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ------------------------------------------------------------------------------------
        // QUIRK 2 — the C1 depth-contrast geometry: a TUNNEL corridor (pocket h≈11, the dig
        // tool's standard target) with a FULL-DEPTH pocket (h≈0-3) dug through its floor — the
        // user's. The route out of the pocket = climb the deep face
        // → dismount at the corridor line → floor run east across the h=11 corridor. The climb
        // hands the FLOOR run a wall-depth z (the FUN_0040bb70:115-139 claim copy); the port's
        // LOS 3-point floor leg lerps it across the corridor — mid-lerp the body depth is
        // SMALLER (deeper) than the corridor pocket h ⇒ rendered inside rock (
        //). The native never lerps this seam: its floor leg re-derives the start from
        // the live body (FUN_0041aac0:127-129) and every densified column waypoint is
        // band-resolved per column (FUN_0041d8a0).
        // ------------------------------------------------------------------------------------
        [Test]
        public void ShaftDescent_DismountAndCorridorWalk_NeverEmbedsByDepth()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var svc = (NavWorldService)engine.Ctx.NavService!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                // 1) TUNNEL shaft down + TUNNEL corridor east (the dig tool's standard targets —
                // pocket h ≈ 11, matching the player's dug caves), per the T10 recipe.
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft: {shaft.Count} marks, carved in {CarveOut(terr, shaft)} steps");
                int ayH = ay + 12;
                var (tbx, tby) = DigMarquee.SnapDragCorner(ax, ayH, ax + 12f, ayH);
                var tun = CommitMarquee(terr, ax, ayH, tbx, tby);
                TestContext.WriteLine($"tunnel: {tun.Count} marks, carved in {CarveOut(terr, tun)} steps");
                nav.RevalidateRect((ax - 2) * 8, (ay - 2) * 8, (ax + 15) * 8 + 7, (ayH + 6) * 8 + 7);

                // the corridor line = the widest floor row of the tunnel
                (int y, int x0, int x1) corridor = default;
                for (int sy = (ayH - 1) * 8; sy < (ayH + 4) * 8; sy++)
                    for (int sx = ax * 8; sx <= (ax + 12) * 8; sx++)
                    {
                        if (!nav.SubIsFloor(sx, sy)) continue;
                        int end = sx; while (nav.SubIsFloor(end + 1, sy)) end++;
                        if (end - sx > corridor.x1 - corridor.x0) corridor = (sy, sx, end);
                        sx = end;
                    }
                if (corridor.x1 - corridor.x0 < 24) { Assert.Ignore("no wide tunnel corridor formed"); return; }
                int corrH = nav.GetHmapSub((corridor.x0 + corridor.x1) / 2, corridor.y - 1);
                TestContext.WriteLine($"corridor row sy={corridor.y}: sx [{corridor.x0}..{corridor.x1}] pocket h={corrH}");

                // 2) the FULL-DEPTH pocket through the corridor floor at its WEST third.
                int pitTx = (corridor.x0 + 6) / 8;
                var pocket = new List<(int, int)>();
                for (int ty = corridor.y / 8; ty <= corridor.y / 8 + 2; ty++)
                    for (int tx = pitTx; tx <= pitTx + 1; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 0 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 0, dummy: false);
                        pocket.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                Assert.Greater(pocket.Count, 0, "no pocket cells painted through the corridor floor");
                TestContext.WriteLine($"pocket carved in {CarveOut(terr, pocket)} steps at tiles x[{pitTx}..{pitTx + 1}]");
                nav.RevalidateRect((pitTx - 2) * 8, (corridor.y / 8 - 2) * 8, (pitTx + 4) * 8 + 7, (corridor.y / 8 + 6) * 8 + 7);

                // the pocket's own bottom line (deep seat)
                (int sx, int sy) pbot = default;
                for (int sy = (corridor.y / 8 + 5) * 8; sy > corridor.y && pbot == default; sy--)
                    for (int sx = pitTx * 8; sx <= (pitTx + 2) * 8; sx++)
                        if (nav.SubIsFloor(sx, sy)) { pbot = (sx, sy); break; }
                if (pbot == default) { Assert.Ignore("pocket formed no interior line"); return; }
                float pbotSeat = Mathf.Min(nav.GetHmapSub(pbot.sx, pbot.sy - 1) + 1, 14.5f);
                TestContext.WriteLine($"pocket bottom ({pbot.sx},{pbot.sy}) seat z={pbotSeat:0.0} " +
                    $"(corridor pocket h={corrH} — depth contrast {corrH - (pbotSeat - 1):0.0} nibbles)");
                DumpWindow(nav, pitTx - 1, pitTx + 3, ayH - 1, corridor.y / 8 + 5);

                // 3) OUT of the pocket → the corridor's FAR EAST end at its own lawful seat.
                float goalSeat = Mathf.Min(nav.GetHmapSub(corridor.x1 - 2, corridor.y - 1) + 1, 14.5f);
                var start = W(new Vector3(pbot.sx * 0.125f, pbot.sy * 0.125f, pbotSeat));
                var goal = W(new Vector3((corridor.x1 - 2) * 0.125f, corridor.y * 0.125f, goalSeat));
                var path = svc.FindPath(start, goal);
                Assert.IsNotNull(path, $"no pocket→corridor-end path ({NavWorldService.LastNullReason})");

                var pathDump = new StringBuilder();
                foreach (var wpt in path!)
                {
                    var n0 = N(wpt.Pos);
                    int csx = Mathf.RoundToInt(n0.x * 8f), csy = Mathf.RoundToInt(n0.y * 8f);
                    int minH = Mathf.Min(nav.GetHmapSub(csx, csy), nav.GetHmapSub(csx, csy - 1));
                    pathDump.AppendLine($"  {(wpt.IsWall ? "WALL " : "floor")} ({n0.x:0.00},{n0.y:0.00},{n0.z:0.00}) " +
                        $"colMinH={minH} embed={(n0.z < minH - 0.01f ? "YES" : "-")}");
                }
                TestContext.WriteLine("plan:\n" + pathDump);

                // execute the real chain; per tick, EMBED metric = z − min(h(own), h(above)) at
                // the body's column (rock = z < h; the AirClamp bound, NavWorldService).
                var body = new GameObject("body");
                var ctrl = body.AddComponent<DigglesAnimController>();
                body.transform.position = path[0].Pos;
                float worstEmbed = 0f; Vector3 worstAt = default;
                int wallHops = 0;
                for (int si = 0; si < path.Count;)
                {
                    bool wallRun = path[si].IsWall;
                    int sj = si; while (sj < path.Count && path[sj].IsWall == wallRun) sj++;
                    IEntityAction seg;
                    if (wallRun)
                    {
                        var run = new List<NavWaypoint>();
                        if (si > 0) run.Add(path[si - 1]);
                        for (int k = si; k < sj; k++) run.Add(path[k]);
                        if (sj < path.Count) run.Add(path[sj]);
                        seg = new ClimbAction(ctrl, body.transform, run, 0, -1, 0);
                        wallHops++;
                    }
                    else
                    {
                        var pts = new List<Vector3>(sj - si);
                        for (int k = si; k < sj; k++) pts.Add(path[k].Pos);
                        seg = new MetaWalkAction(ctrl, body.transform, pts, 0, -1, 0);
                    }
                    int guard = 0; bool more = true;
                    while (more && guard++ < 20000)
                    {
                        more = seg.Tick(0.1);
                        Vector3 pn = N(body.transform.position);
                        int csx = Mathf.RoundToInt(pn.x * 8f), csy = Mathf.RoundToInt(pn.y * 8f);
                        // wall-borne ticks ride the face by design; audit FLOOR segments only
                        if (!wallRun)
                        {
                            int minH = Mathf.Min(nav.GetHmapSub(csx, csy), nav.GetHmapSub(csx, csy - 1));
                            float embed = minH - pn.z;              // >0 ⇒ inside rock by depth
                            if (embed > worstEmbed) { worstEmbed = embed; worstAt = pn; }
                        }
                    }
                    Assert.Less(guard, 20000, "segment never finished");
                    si = sj;
                }
                Vector3 endN = N(body.transform.position);
                TestContext.WriteLine($"RESULT: wallRuns={wallHops} end=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) " +
                    $"goalSeat={goalSeat:0.0} worstFloorEmbed={worstEmbed:0.00} at ({worstAt.x:0.00},{worstAt.y:0.00},{worstAt.z:0.00})");
                Assert.Greater(wallHops, 0, "the descent must contain a climb");
                // The Play any floor-segment tick more than 0.75 nibble
                // INSIDE its own column's rock is the visible embed.
                Assert.LessOrEqual(worstEmbed, 0.75f,
                    "the post-dismount floor walk carried the WALL depth across the corridor — body inside rock");
                Assert.LessOrEqual(Mathf.Abs(endN.z - goalSeat), 2.0f, "landing depth far off the corridor seat");
                Object.DestroyImmediate(body);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }
    }
}
