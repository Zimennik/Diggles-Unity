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

    // ================== item 5 — the T-JUNCTION nav break ==================
    //
    // USER REPRO: —.. —
    // —.
    //
    // Geometry (side-view ant farm, native +y = down): a vertical shaft dug DOWN from a floor,
    // then a horizontal tunnel/cave carved so it CROSSES the shaft — the shaft mouth becomes a
    // hole in the new tunnel's floor line. Two symptoms:
    // (a) pit bottom → upper tunnel floor: the climb OUT through the junction fails;
    // (b) upper floor across the junction: the walk ACROSS the mouth hole fails.
    //
    // Both are exercised at the FindPath level (plan law) — the Play symptom is the walkfail
    // churn (WalkResult 3 storms), which is a NULL plan, not an executor stall.
    //
    // Site: the start meadow's earth chimney (the only native-diggable surface column). Recipe
    // mirrors CaveDigReproTests.PrepareCaveSite (proven to carve): marquee shaft 12 tiles down,
    // then the crossing tunnel painted straight onto the dig state (the cave/depth tools' mode-0
    // painter FUN_005671a0 has NO flank validity — that is exactly how the player can cross an
    // existing shaft; the standard-tunnel CellValid would refuse cells flanking the open shaft).
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class NavTJunctionTests
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
            engine.Ctx.NavService = rootGo.GetComponent<NavWorldService>() ?? rootGo.AddComponent<NavWorldService>();
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
            TestContext.WriteLine($"marquee commit rect=({x0},{y0})..({x1},{y1}) marked={marks.Count}");
            return marks;
        }

        // The CROSSING tunnel painter — mode-0 style (no flank validity: the cave/depth tools'
        // painter FUN_005671a0 has none), so cells flanking the already-open shaft still mark.
        static List<(int tx, int ty)> PaintCrossingTunnel(DiggableTerrain terr, int x0, int x1, int rowTop)
        {
            var marks = new List<(int, int)>();
            for (int ty = rowTop; ty <= rowTop + 1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!terr.TileMaterialDiggable(tx, ty)) continue;   // granite / already fully open
                    terr.SetDigMark(tx, ty, 0, 0, dummy: true);
                    marks.Add((tx, ty));
                }
            terr.IncPlayerDigMarkId();
            TestContext.WriteLine($"crossing tunnel paint x[{x0}..{x1}] y[{rowTop}..{rowTop + 1}] marked={marks.Count}");
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
            terr.RevalidateDirtyNav();
            return total;
        }

        static void DumpWindow(NavWorld nav, int tx0, int tx1, int ty0, int ty1)
        {
            TestContext.WriteLine($"[CLASS+H] tiles x[{tx0}..{tx1}] y[{ty0}..{ty1}] (F=floor W=wall .=none | h hex):");
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

        static float StandZ(NavWorld nav, (int sx, int sy) c)
            => Mathf.Min(nav.GetHmapSub(c.sx, c.sy - 1) + 1, 14.5f);

        // Which layer nulls the path — Calc1 (tile graph) vs the depth assembly (FloorPathMap)?
        static string Diag(NavWorld nav, (int sx, int sy) a, (int sx, int sy) b)
        {
            var log = new StringBuilder();
            log.AppendLine($"  DIAG start sub({a.sx},{a.sy}) bits={nav.SubAt(a.sx, a.sy)} " +
                $"baseCost={nav.BaseCost[(a.sy >> 3) * nav.Width + (a.sx >> 3)]} | " +
                $"goal sub({b.sx},{b.sy}) bits={nav.SubAt(b.sx, b.sy)} " +
                $"baseCost={nav.BaseCost[(b.sy >> 3) * nav.Width + (b.sx >> 3)]}");
            var pf = new Calc1Pathfinder(nav);
            var subs = pf.FindPath(a.sx, a.sy, b.sx, b.sy, true, 100);
            if (subs == null) { log.AppendLine("  DIAG Calc1 = NULL (tile graph disconnect)"); return log.ToString(); }
            var sb = new StringBuilder($"  DIAG Calc1 ok ({subs.Count} cells): ");
            foreach (var (cx, cy) in subs)
            {
                byte bits = nav.SubAt(cx, cy);
                char cls = (bits & NavWorld.SubFloor) != 0 ? 'F' : (bits & NavWorld.SubWalk) != 0 ? 'W' : '.';
                sb.Append($"({cx},{cy}){cls} ");
            }
            log.AppendLine(sb.ToString());
            log.AppendLine("  DIAG ⇒ assembly-layer null (run splicing / FloorPathMap depth route)");
            return log.ToString();
        }

        // -----------------------------------------------------------------------------------
        // T1 — the player's exact order: shaft DOWN first, horizontal tunnel crossing it SECOND.
        // Assert the two broken routes: (a) pit bottom → tunnel arm floor (out of the pit) and
        // (b) across the junction if both arms carved.
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_ShaftThenCrossingTunnel_PitExit_And_CrossWalk()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

                // 1. vertical shaft 12+ tiles down (2 tiles wide: cols ax..ax+1).
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                Assert.Greater(shaft.Count, 3, "shaft marquee painted too few");
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");

                // 2. horizontal tunnel CROSSING the shaft at rows ay+8/ay+9 (lattice 4m+1).
                int rowT = ay + 8;
                var tunnel = PaintCrossingTunnel(terr, ax - 10, ax + 12, rowT);
                Assert.Greater(tunnel.Count, 3, "crossing tunnel painted too few");
                TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tunnel)} steps");

                // Junction inventory: tunnel floor line lives on the first rock row below rowT+1,
                // i.e. sub-row (rowT+2)*8 — probe arms WEST (x < ax) and EAST (x > ax+1).
                int floorRow = (rowT + 2) * 8;
                var west = FindArmFloor(nav, (ax - 8) * 8, ax * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (ax + 2) * 8 + 2, (ax + 11) * 8, floorRow);
                var bottom = FindPitBottom(nav, ax, rowT + 2, ay + 15);
                TestContext.WriteLine($"anchor=({ax},{ay}) rowT={rowT} west={west} east={east} bottom={bottom}");
                DumpWindow(nav, ax - 4, ax + 5, rowT - 2, rowT + 6);

                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor below the junction — geometry void");
                bool haveWest = west != default, haveEast = east != default;
                Assert.IsTrue(haveWest || haveEast, "no tunnel arm carved — geometry void");

                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 P((int sx, int sy) c) => W(new Vector3(c.sx / 8f, c.sy / 8f, StandZ(nav, c)));

                var fails = new StringBuilder();
                void Check(string label, (int sx, int sy) a, (int sx, int sy) b)
                {
                    var p = svc.FindPath(P(a), P(b));
                    TestContext.WriteLine($"[{label}] ({a.sx},{a.sy}) → ({b.sx},{b.sy}) = " +
                        (p == null ? "NULL" : $"{p.Count} wps"));
                    if (p == null)
                    {
                        fails.AppendLine($"{label}: NULL plan");
                        TestContext.WriteLine(Diag(nav, a, b));
                    }
                    else DumpPlan(p, root, nav);
                }

                var arm = haveWest ? west : east;
                Check("a: pit bottom → arm floor (OUT of the pit)", bottom, arm);
                Check("a': arm floor → pit bottom (INTO the pit)", arm, bottom);
                if (haveWest && haveEast)
                {
                    Check("b: west arm → east arm (ACROSS the junction)", west, east);
                    Check("b': east arm → west arm", east, west);
                }

                Assert.AreEqual(0, fails.Length,
                    "T-junction routes NULLed (the Play walkfail churn — 'can neither climb out nor go around'):\n" + fails);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // -----------------------------------------------------------------------------------
        // T2 — the rotated order: tunnel first (shaft + bottom tunnel = the proven CaveDigRepro
        // recipe), then a pit dug DOWN from the tunnel's mid-floor. Same T geometry, the OTHER
        // revalidation direction (the pit carve must re-derive the tunnel floor mouth cells).
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_PitDugFromTunnelFloor_CrossWalk_And_PitExit()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

                // 1. shaft down (rows ay..ay+13), 2. tunnel EAST at the bottom (rows ay+12/+13).
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                Assert.Greater(shaft.Count, 3, "shaft marquee painted too few");
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int ayH = ay + 12;
                var (tbx, tby) = DigMarquee.SnapDragCorner(ax, ayH, ax + 12f, ayH);
                var tun = CommitMarquee(terr, ax, ayH, tbx, tby);
                Assert.Greater(tun.Count, 3, "tunnel marquee painted too few");
                TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tun)} steps");

                // 3. pit DOWN from the tunnel's mid-floor: cols ax+8..ax+9, rows ayH+2..ayH+7
                // (mode-0 paint — the depth tool digs through its own floor).
                int px = ax + 8;
                var pit = new List<(int, int)>();
                for (int ty = ayH + 2; ty <= ayH + 7; ty++)
                    for (int tx = px; tx <= px + 1; tx++)
                        if (terr.TileMaterialDiggable(tx, ty))
                        { terr.SetDigMark(tx, ty, 0, 0, dummy: true); pit.Add((tx, ty)); }
                terr.IncPlayerDigMarkId();
                Assert.Greater(pit.Count, 3, "pit paint painted too few");
                TestContext.WriteLine($"pit carved in {CarveOut(terr, pit)} steps");

                int floorRow = (ayH + 2) * 8;
                var west = FindArmFloor(nav, (px - 6) * 8, px * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (px + 2) * 8 + 2, (px + 6) * 8, floorRow);
                var bottom = FindPitBottom(nav, px, ayH + 3, ayH + 10);
                TestContext.WriteLine($"anchor=({ax},{ay}) pit x={px} west={west} east={east} bottom={bottom}");
                DumpWindow(nav, px - 3, px + 4, ayH - 1, ayH + 9);

                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor below the tunnel — geometry void");
                Assert.IsTrue(west != default && east != default, "tunnel arms missing around the pit — geometry void");

                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 P((int sx, int sy) c) => W(new Vector3(c.sx / 8f, c.sy / 8f, StandZ(nav, c)));

                var fails = new StringBuilder();
                void Check(string label, (int sx, int sy) a, (int sx, int sy) b)
                {
                    var p = svc.FindPath(P(a), P(b));
                    TestContext.WriteLine($"[{label}] ({a.sx},{a.sy}) → ({b.sx},{b.sy}) = " +
                        (p == null ? "NULL" : $"{p.Count} wps"));
                    if (p == null)
                    {
                        fails.AppendLine($"{label}: NULL plan");
                        TestContext.WriteLine(Diag(nav, a, b));
                    }
                    else DumpPlan(p, root, nav);
                }

                Check("b: west arm → east arm (ACROSS the pit mouth)", west, east);
                Check("b': east arm → west arm", east, west);
                Check("a: pit bottom → west arm (OUT of the pit)", bottom, west);
                Check("a': west arm → pit bottom", west, bottom);

                Assert.AreEqual(0, fails.Length,
                    "T-junction routes NULLed (the Play walkfail churn):\n" + fails);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // -----------------------------------------------------------------------------------
        // T3 — the DEPTH-MISMATCH junction, the real Play tool mix: a standard pit-tunnel
        // (dummy target 11) crossed by a CAVE (mode-0 cnt-1 marks, target 3 — 
        //). The junction cells end at h=3 while the pit above/below stays h=11 —
        // the derived floor/climb cells at the mouth ride a 8-nibble depth STEP.
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_CaveDepthCrossing_PitExit_And_CrossWalk()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

                // 1. vertical pit-tunnel 12 tiles down (target 11).
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                Assert.Greater(shaft.Count, 3, "shaft marquee painted too few");
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");

                // 2. the CAVE crossing it: 4 tile rows of cnt-1 marks (target 3), wide span.
                int rowT = ay + 4;
                var cave = new List<(int, int)>();
                for (int ty = rowT; ty <= rowT + 3; ty++)
                    for (int tx = ax - 10; tx <= ax + 12; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 1 | 8)) continue;   // the mode-0 painter's own gate
                        terr.SetDigMark(tx, ty, 0, cnt: 1, dummy: false);
                        cave.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                Assert.Greater(cave.Count, 3, "cave paint painted too few");
                TestContext.WriteLine($"cave carved in {CarveOut(terr, cave)} steps (cells={cave.Count})");

                int floorRow = (rowT + 4) * 8;
                var west = FindArmFloor(nav, (ax - 8) * 8, ax * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (ax + 2) * 8 + 2, (ax + 11) * 8, floorRow);
                var bottom = FindPitBottom(nav, ax, rowT + 4, ay + 15);
                TestContext.WriteLine($"anchor=({ax},{ay}) caveRows[{rowT}..{rowT + 3}] west={west} east={east} bottom={bottom}");
                DumpWindow(nav, ax - 4, ax + 5, rowT + 2, rowT + 8);

                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor below the junction — geometry void");
                bool haveWest = west != default, haveEast = east != default;
                Assert.IsTrue(haveWest || haveEast, "no cave arm carved — geometry void");

                RunRouteChecks(rootGo, nav, west, east, bottom, haveWest, haveEast);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // -----------------------------------------------------------------------------------
        // T4 — the OTHER Play tool order: a CAVE room first (cnt-1, h=3), then a FULL-DEPTH pit
        // (cnt-0, target 0 — the only tool whose rim gate still accepts inside a h=3 cave) dug
        // from the cave's mid-floor. The mouth hole is now REAL (h=0 = the whole slab column
        // removed) — the cave floor line is interrupted, unlike T2's h=11 rim shelf.
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_FullDepthPitFromCaveFloor_CrossWalk_And_PitExit()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

                // 1. access shaft down + 2. the cave room east at the bottom (cnt-1, target 3).
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 8f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                Assert.Greater(shaft.Count, 3, "shaft marquee painted too few");
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int rowC = ay + 8;
                var cave = new List<(int, int)>();
                for (int ty = rowC; ty <= rowC + 3; ty++)
                    for (int tx = ax; tx <= ax + 12; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 1 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 1, dummy: false);
                        cave.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                Assert.Greater(cave.Count, 3, "cave paint painted too few");
                TestContext.WriteLine($"cave carved in {CarveOut(terr, cave)} steps");

                // 3. the FULL-DEPTH pit from the cave's mid-floor (cnt-0 → target 0).
                int px = ax + 6;
                var pit = new List<(int, int)>();
                for (int ty = rowC + 4; ty <= rowC + 9; ty++)
                    for (int tx = px; tx <= px + 1; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 0 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 0, dummy: false);
                        pit.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                Assert.Greater(pit.Count, 3, "pit paint painted too few");
                TestContext.WriteLine($"pit carved in {CarveOut(terr, pit)} steps");

                int floorRow = (rowC + 4) * 8;
                var west = FindArmFloor(nav, (px - 5) * 8, px * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (px + 2) * 8 + 2, (px + 6) * 8, floorRow);
                var bottom = FindPitBottom(nav, px, rowC + 5, rowC + 12);
                TestContext.WriteLine($"anchor=({ax},{ay}) cave rows[{rowC}..{rowC + 3}] pit x={px} west={west} east={east} bottom={bottom}");
                DumpWindow(nav, px - 3, px + 4, rowC + 2, rowC + 11);

                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor below the cave — geometry void");
                Assert.IsTrue(west != default && east != default, "cave arms missing around the pit — geometry void");

                RunRouteChecks(rootGo, nav, west, east, bottom, true, true);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // The shared 4-route battery + the waypoint EMBED audit (no waypoint may put the body
        // inside solid rock: native z must be >= min(h(own), h(row up)) of its own sub-column).
        static void RunRouteChecks(GameObject rootGo, NavWorld nav,
            (int sx, int sy) west, (int sx, int sy) east, (int sx, int sy) bottom,
            bool haveWest, bool haveEast)
        {
            var svc = rootGo.GetComponent<NavWorldService>()!;
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
            Vector3 P((int sx, int sy) c) => W(new Vector3(c.sx / 8f, c.sy / 8f, StandZ(nav, c)));

            var fails = new StringBuilder();
            void Check(string label, (int sx, int sy) a, (int sx, int sy) b)
            {
                var p = svc.FindPath(P(a), P(b));
                TestContext.WriteLine($"[{label}] ({a.sx},{a.sy}) → ({b.sx},{b.sy}) = " +
                    (p == null ? "NULL" : $"{p.Count} wps"));
                if (p == null)
                {
                    fails.AppendLine($"{label}: NULL plan");
                    TestContext.WriteLine(Diag(nav, a, b));
                    return;
                }
                DumpPlan(p, root, nav);
                for (int k = 1; k < p.Count; k++)   // [0] = the raw body start, not emitted by nav
                {
                    Vector3 n = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(p[k].Pos));
                    int sx = Mathf.RoundToInt(n.x * 8f), sy = Mathf.RoundToInt(n.y * 8f);
                    int h = Mathf.Min(nav.GetHmapSub(sx, sy), nav.GetHmapSub(sx, sy - 1));
                    if (n.z < h - 0.01f)
                        fails.AppendLine($"{label}: wp[{k}]{(p[k].IsWall ? "W" : "F")} EMBEDDED " +
                            $"({n.x:0.00},{n.y:0.00},{n.z:0.00}) vs h={h} (own {nav.GetHmapSub(sx, sy)}, up {nav.GetHmapSub(sx, sy - 1)})");
                }
            }

            var arm = haveWest ? west : east;
            Check("a: pit bottom → arm floor (OUT of the pit)", bottom, arm);
            Check("a': arm floor → pit bottom (INTO the pit)", arm, bottom);
            if (haveWest && haveEast)
            {
                Check("b: west arm → east arm (ACROSS the junction)", west, east);
                Check("b': east arm → west arm", east, west);
            }

            Assert.AreEqual(0, fails.Length,
                "T-junction routes broken (the Play 'can neither climb out nor go around'):\n" + fails);
        }

        // -----------------------------------------------------------------------------------
        // T5 — EXECUTION round trip on the uniform T (the player's order: shaft, then crossing
        // tunnel): drive the EXACT engine chain (MetaWalkAction + live support probe,
        // ClimbAction, WalkResultAction, RetryWalkAction — BuildWalkSegments' wiring) leg after
        // leg, each leg planned FROM THE BODY'S REAL END POSITION of the previous leg — the
        // Play loop that. Audits per leg: termination, WalkResult 4,
        // arrival, unsupported lateral glide, replan churn.
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_Execution_RoundTrip_UniformT()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int rowT = ay + 8;
                var tunnel = PaintCrossingTunnel(terr, ax - 10, ax + 12, rowT);
                TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tunnel)} steps");

                int floorRow = (rowT + 2) * 8;
                var west = FindArmFloor(nav, (ax - 8) * 8, ax * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (ax + 2) * 8 + 2, (ax + 11) * 8, floorRow);
                var bottom = FindPitBottom(nav, ax, rowT + 2, ay + 15);
                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor below the junction");
                Assert.IsTrue(west != default && east != default, "tunnel arms missing");
                DumpWindow(nav, ax - 3, ax + 4, rowT - 1, rowT + 6);

                RunExecutionRoundTrip(rootGo, nav, new[]
                {
                    ("west→east ACROSS", west, east),
                    ("east→bottom INTO", east, bottom),
                    ("bottom→west OUT", bottom, west),
                    ("west→bottom INTO2", west, bottom),
                    ("bottom→east OUT2", bottom, east),
                });
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // -----------------------------------------------------------------------------------
        // T6 — EXECUTION round trip on the full-depth-pit-in-cave T (T4's geometry: the mouth
        // hole is REAL — h=0 columns interrupt the cave floor line).
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_Execution_RoundTrip_FullDepthPitInCave()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 8f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int rowC = ay + 8;
                var cave = new List<(int, int)>();
                for (int ty = rowC; ty <= rowC + 3; ty++)
                    for (int tx = ax; tx <= ax + 12; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 1 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 1, dummy: false);
                        cave.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                TestContext.WriteLine($"cave carved in {CarveOut(terr, cave)} steps");
                int px = ax + 6;
                var pit = new List<(int, int)>();
                for (int ty = rowC + 4; ty <= rowC + 9; ty++)
                    for (int tx = px; tx <= px + 1; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 0 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 0, dummy: false);
                        pit.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                TestContext.WriteLine($"pit carved in {CarveOut(terr, pit)} steps");

                int floorRow = (rowC + 4) * 8;
                var west = FindArmFloor(nav, (px - 5) * 8, px * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (px + 2) * 8 + 2, (px + 6) * 8, floorRow);
                var bottom = FindPitBottom(nav, px, rowC + 5, rowC + 12);
                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor below the cave");
                Assert.IsTrue(west != default && east != default, "cave arms missing");
                DumpWindow(nav, px - 3, px + 4, rowC + 2, rowC + 11);

                RunExecutionRoundTrip(rootGo, nav, new[]
                {
                    ("west→east ACROSS", west, east),
                    ("east→bottom INTO", east, bottom),
                    ("bottom→west OUT", bottom, west),
                    ("west→bottom INTO2", west, bottom),
                    ("bottom→east OUT2", bottom, east),
                });
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // Drive the legs with the REAL engine chain; leg N+1 starts where leg N left the body.
        static void RunExecutionRoundTrip(GameObject rootGo, NavWorld nav,
            (string label, (int sx, int sy) a, (int sx, int sy) b)[] legs)
        {
            var svc = rootGo.GetComponent<NavWorldService>()!;
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
            Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

            bool UnsupportedForWalk(Vector3 pn)
            {
                int sx = Mathf.RoundToInt(pn.x * 8f), sy = Mathf.RoundToInt(pn.y * 8f);
                if (nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1))
                    return false;
                return (nav.SubAt(sx, sy) & NavWorld.SubWalk) == 0
                    || Mathf.Abs(pn.z - NavWorldService.SupportDepth(nav, sx, sy)) > 2.5f;
            }

            var ent = new DigglesTclEntity();
            var body = new GameObject("tjBody");
            var ctrl = body.AddComponent<DigglesAnimController>();
            System.Func<Vector3, bool> probe = svc.HasFloorSupportWorld;

            IEntityAction? BuildChain(List<NavWaypoint> p)
            {
                var segs = new List<IEntityAction>();
                for (int si = 0; si < p.Count;)
                {
                    bool wallRun = p[si].IsWall;
                    int sj = si; while (sj < p.Count && p[sj].IsWall == wallRun) sj++;
                    if (wallRun)
                    {
                        var run = new List<NavWaypoint>();
                        if (si > 0) run.Add(p[si - 1]);
                        for (int k = si; k < sj; k++) run.Add(p[k]);
                        if (sj < p.Count) run.Add(p[sj]);
                        segs.Add(new ClimbAction(ctrl, body.transform, run, 0, -1, 0));
                    }
                    else
                    {
                        var pts = new List<Vector3>(sj - si);
                        for (int k = si; k < sj; k++) pts.Add(p[k].Pos);
                        segs.Add(new MetaWalkAction(ctrl, body.transform, pts, 0, -1, 0, probe));
                    }
                    si = sj;
                }
                if (segs.Count == 0) return null;
                IEntityAction chain = segs.Count == 1 ? segs[0] : new SequenceAction(segs);
                return new WalkResultAction(chain, ent, 4);
            }

            try
            {
                // Seat the body on the first leg's start cell at the standing depth.
                var a0 = legs[0].a;
                body.transform.position = W(new Vector3(a0.sx / 8f, a0.sy / 8f, StandZ(nav, a0)));

                var fails = new StringBuilder();
                foreach (var (label, _, b) in legs)
                {
                    Vector3 wGoal = W(new Vector3(b.sx / 8f, b.sy / 8f, StandZ(nav, b)));
                    Vector3 startN = N(body.transform.position);
                    var p0 = svc.FindPath(body.transform.position, wGoal);
                    if (p0 == null)
                    {
                        fails.AppendLine($"{label}: NULL plan from live body " +
                            $"({startN.x:0.00},{startN.y:0.00},{startN.z:0.00})");
                        int ssx = Mathf.RoundToInt(startN.x * 8f), ssy = Mathf.RoundToInt(startN.y * 8f);
                        TestContext.WriteLine(Diag(nav, (ssx, ssy), b));
                        continue;   // next leg still starts from wherever the body is
                    }
                    int replans = 0;
                    IEntityAction walk = new RetryWalkAction(BuildChain(p0)!, ent, body.transform, wGoal, () =>
                    {
                        replans++;
                        var pR = svc.FindPath(body.transform.position, wGoal);
                        if (pR == null || pR.Count < 2) return null;
                        return BuildChain(pR);
                    });
                    float airLat = 0f; Vector3 prevN = startN;
                    int guard = 0; bool running = true;
                    while (running && guard++ < 12000)
                    {
                        running = walk.Tick(0.025);
                        Vector3 pn = N(body.transform.position);
                        if (UnsupportedForWalk(pn)) airLat += Mathf.Abs(pn.x - prevN.x);
                        prevN = pn;
                    }
                    Vector3 endN = N(body.transform.position);
                    bool endUnsup = UnsupportedForWalk(endN);
                    TestContext.WriteLine($"[{label}] start=({startN.x:0.00},{startN.y:0.00},{startN.z:0.00}) " +
                        $"goal=({b.sx / 8f:0.00},{b.sy / 8f:0.00}) end=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) " +
                        $"wr={ent.WalkResult} replans={replans} airLatX={airLat:0.00} endUnsup={endUnsup} ticks={guard}");
                    if (guard >= 12000) fails.AppendLine($"{label}: leg never terminated");
                    else if (ent.WalkResult != 4) fails.AppendLine($"{label}: WalkResult {ent.WalkResult} (walkfail churn)");
                    else if (Mathf.Abs(endN.x - b.sx / 8f) + Mathf.Abs(endN.y - b.sy / 8f) > 1.5f)
                        fails.AppendLine($"{label}: 'arrived' {Mathf.Abs(endN.x - b.sx / 8f) + Mathf.Abs(endN.y - b.sy / 8f):0.00} native off the goal");
                    if (airLat > 1.5f) fails.AppendLine($"{label}: unsupported lateral glide {airLat:0.00} (the Play air-crawl)");
                    if (endUnsup) fails.AppendLine($"{label}: body ended hanging unsupported");
                    if (replans > 6) fails.AppendLine($"{label}: {replans} AutoRetry replans (churn)");
                }
                Assert.AreEqual(0, fails.Length, "T-junction execution violations:\n" + fails);
            }
            finally { Object.DestroyImmediate(body); }
        }

        // -----------------------------------------------------------------------------------
        // T7 — the ASSEMBLY-NULL SWEEP: over EVERY walkable cell of the T region (floor lines,
        // climb ribbons — the spots a live gnome can actually BE, incl. mid-climb replans and
        // stale hover depths), plan to the key goals. ORACLE: whenever Calc1 (the tile graph)
        // finds a polyline, NavWorldService.FindPath must NOT return null — an assembly-layer
        // null (run splicing / FloorPathMap bands / snaps) is the walkfail churn the player sees
        // only some start cells/depths survive).
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_AssemblyNullSweep_UniformT()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int rowT = ay + 8;
                var tunnel = PaintCrossingTunnel(terr, ax - 10, ax + 12, rowT);
                TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tunnel)} steps");

                int floorRow = (rowT + 2) * 8;
                var west = FindArmFloor(nav, (ax - 8) * 8, ax * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (ax + 2) * 8 + 2, (ax + 11) * 8, floorRow);
                var bottom = FindPitBottom(nav, ax, rowT + 2, ay + 15);
                var surface = FindArmFloor(nav, (ax - 8) * 8, ax * 8 - 2, digFloor.sy);
                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor");
                Assert.IsTrue(west != default && east != default, "arms missing");

                SweepAssemblyNulls(rootGo, nav,
                    x0: (ax - 8) * 8, x1: (ax + 11) * 8,
                    y0: digFloor.sy - 8, y1: bottom.sy + 4,
                    goals: new[] { ("west", west), ("east", east), ("bottom", bottom), ("surface", surface) });
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        [Test]
        public void TJunction_AssemblyNullSweep_FullDepthPitInCave()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 8f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int rowC = ay + 8;
                var cave = new List<(int, int)>();
                for (int ty = rowC; ty <= rowC + 3; ty++)
                    for (int tx = ax; tx <= ax + 12; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 1 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 1, dummy: false);
                        cave.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                TestContext.WriteLine($"cave carved in {CarveOut(terr, cave)} steps");
                int px = ax + 6;
                var pit = new List<(int, int)>();
                for (int ty = rowC + 4; ty <= rowC + 9; ty++)
                    for (int tx = px; tx <= px + 1; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 0 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 0, dummy: false);
                        pit.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                TestContext.WriteLine($"pit carved in {CarveOut(terr, pit)} steps");

                int floorRow = (rowC + 4) * 8;
                var west = FindArmFloor(nav, (px - 5) * 8, px * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (px + 2) * 8 + 2, (px + 6) * 8, floorRow);
                var bottom = FindPitBottom(nav, px, rowC + 5, rowC + 12);
                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor");
                Assert.IsTrue(west != default && east != default, "arms missing");

                SweepAssemblyNulls(rootGo, nav,
                    x0: (ax - 2) * 8, x1: (px + 6) * 8,
                    y0: rowC * 8, y1: bottom.sy + 4,
                    goals: new[] { ("west", west), ("east", east), ("bottom", bottom) });
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // For every walkable cell in the window (val 1 AND val 3), plan svc.FindPath to each goal
        // from THREE start depths a live body really has there: the lawful seat (floor standing z /
        // wall support), the wall-record depth, and a STALE 14.0 (the pre-carve hover — the digger
        // whose column was just carved). Fail on Calc1-ok-but-assembly-NULL.
        static void SweepAssemblyNulls(GameObject rootGo, NavWorld nav,
            int x0, int x1, int y0, int y1, (string name, (int sx, int sy) cell)[] goals)
        {
            var svc = rootGo.GetComponent<NavWorldService>()!;
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));

            var pf = new Calc1Pathfinder(nav);
            int starts = 0, plans = 0, calc1Null = 0, asmNull = 0;
            var fails = new StringBuilder();
            for (int sy = y0; sy <= y1; sy++)
                for (int sx = x0; sx <= x1; sx++)
                {
                    byte b = nav.SubAt(sx, sy);
                    if ((b & NavWorld.SubWalk) == 0) continue;
                    bool isFloor = (b & NavWorld.SubFloor) != 0;
                    starts++;
                    float seat = isFloor
                        ? Mathf.Min(nav.GetHmapSub(sx, sy - 1) + 1, 14.5f)
                        : NavWorldService.SupportDepth(nav, sx, sy);
                    float[] zs = { seat, Mathf.Min(seat + 2f, 14.5f), 14.0f };
                    foreach (var (gname, g) in goals)
                    {
                        if (g == default) continue;
                        foreach (float z in zs)
                        {
                            plans++;
                            var p = svc.FindPath(
                                W(new Vector3(sx / 8f, sy / 8f, z)),
                                W(new Vector3(g.sx / 8f, g.sy / 8f,
                                    Mathf.Min(nav.GetHmapSub(g.sx, g.sy - 1) + 1, 14.5f))));
                            if (p != null) continue;
                            // Discriminate: does the tile graph route at all?
                            var subs = pf.FindPath(sx, sy, g.sx, g.sy, true, 100);
                            if (subs == null) { calc1Null++; continue; }   // graph-level no-route: native parity unknown, not this oracle
                            asmNull++;
                            if (fails.Length < 4000)
                                fails.AppendLine($"  ({sx},{sy})z{z:0.0}{(isFloor ? "F" : "W")} → {gname}: " +
                                    $"Calc1 ok ({subs.Count} cells) but FindPath NULL");
                        }
                    }
                }
            TestContext.WriteLine($"sweep: {starts} start cells, {plans} plans, calc1Null={calc1Null}, assemblyNull={asmNull}");
            if (fails.Length > 0) TestContext.WriteLine(fails.ToString());
            Assert.AreEqual(0, asmNull,
                "assembly-layer NULLs (Calc1 routes, FindPath nulls) — the T-junction walkfail churn:\n" + fails);
        }

        // -----------------------------------------------------------------------------------
        // T8 — the FULL TCL STACK on the uniform T: a real Zwerg (classes loaded), user walk
        // orders raised exactly as ClickToMoveService.FireWalkOrder does (`set_event this
        // evt_task_walk -pos1 {…}` → evt_task_walk_proc, z_events.tcl:336 → tasklist walk_pos →
        // walk_action → `action walk` → BuildMetaWalkFromOptions), simulated via Sim.Step.
        // Covers the state machine, the walkfail ladder, get_walkresult gating and the
        // RetryWalkAction — the whole Play stack minus rendering/click-picking.
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_FullTclStack_WalkOrders_ArriveEveryLeg()
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
            var terr = rootGo.GetComponent<DiggableTerrain>()!;
            var nav = terr.Nav!;
            engine.Ctx.Terrain = terr;
            engine.Ctx.NavService = rootGo.GetComponent<NavWorldService>() ?? rootGo.AddComponent<NavWorldService>();
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { engine.Interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(engine.Ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int rowT = ay + 8;
                var tunnel = PaintCrossingTunnel(terr, ax - 10, ax + 12, rowT);
                TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tunnel)} steps");

                int floorRow = (rowT + 2) * 8;
                var west = FindArmFloor(nav, (ax - 8) * 8, ax * 8 - 2, floorRow);
                var east = FindArmFloor(nav, (ax + 2) * 8 + 2, (ax + 11) * 8, floorRow);
                var bottom = FindPitBottom(nav, ax, rowT + 2, ay + 15);
                var surface = FindArmFloor(nav, (ax - 8) * 8, ax * 8 - 2, digFloor.sy);
                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor");
                Assert.IsTrue(west != default && east != default, "arms missing");

                // Spawn the gnome ON the west arm floor.
                float wz = StandZ(nav, west);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(west.sx / 8f)} {Inv(west.sy / 8f)} {Inv(wz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                for (int i = 0; i < 30; i++) sim.Step(0.1);

                var fails = new StringBuilder();
                void Order(string label, (int sx, int sy) goal, float goalZ)
                {
                    var inv = CultureInfo.InvariantCulture;
                    var prev = ctx.This;
                    ctx.This = g;
                    try
                    {
                        interp.Eval("set_event this evt_task_walk -pos1 {" +
                            (goal.sx / 8f).ToString("0.###", inv) + " " +
                            (goal.sy / 8f).ToString("0.###", inv) + " " +
                            goalZ.ToString("0.###", inv) + "}");
                    }
                    finally { ctx.This = prev; }
                    int step = 0; float best = float.MaxValue;
                    for (; step < 4000; step++)
                    {
                        sim.Step(0.1);
                        float d = Mathf.Abs(g.Position.x - goal.sx / 8f) + Mathf.Abs(g.Position.y - goal.sy / 8f);
                        if (d < best) best = d;
                        if (d < 1.0f) break;
                    }
                    TestContext.WriteLine($"[{label}] goal=({goal.sx / 8f:0.00},{goal.sy / 8f:0.00}) " +
                        $"pos=({g.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00}) " +
                        $"best={best:0.00} steps={step} wr={g.WalkResult} state={g.CurrentState}");
                    if (best >= 1.5f)
                        fails.AppendLine($"{label}: never arrived (best {best:0.00}, wr={g.WalkResult}, " +
                            $"state={g.CurrentState}, pos=({g.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00}))");
                }

                Order("1 west→east ACROSS", east, StandZ(nav, east));
                Order("2 east→bottom INTO the pit", bottom, StandZ(nav, bottom));
                Order("3 bottom→west OUT of the pit", west, StandZ(nav, west));
                if (surface != default) Order("4 west→surface UP", surface, StandZ(nav, surface));

                Assert.AreEqual(0, fails.Length,
                    "full-stack walk orders failed at the T-junction (the Play symptom):\n" + fails);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // -----------------------------------------------------------------------------------
        // T9 — the DEPTH-SEAM floor run in the DEPTH axis): a standard tunnel (h=11)
        // whose MIDDLE tiles are then deepened by the cave tool (cnt-1 → h=3; the mouth-over-
        // tunnel MarkRimGate law explicitly allows deepening a dug corridor). The floor LINE
        // stays on the same y row across the seam — ONE floor run, no climb cells — but the
        // standing depth jumps 11→3 across ~1 tile of cliff-block ramp. Suspicion: FloorPathMap's
        // 5-pass flood (max sustained slope 2 rows/col = 0.5 nibble/col) cannot bridge an
        // 8-nibble step over 8 columns → the whole leg nulls → the Play 
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_DepthSeam_TunnelDeepenedMiddle_WalkAcross_And_Out()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

                // access shaft + long tunnel east at the bottom (the proven recipe).
                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int ayH = ay + 12;
                var (tbx, tby) = DigMarquee.SnapDragCorner(ax, ayH, ax + 16f, ayH);
                var tun = CommitMarquee(terr, ax, ayH, tbx, tby);
                Assert.Greater(tun.Count, 3, "tunnel marquee painted too few");
                TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tun)} steps");

                // Deepen the tunnel's MIDDLE 2 tiles (x = ax+8..ax+9) with the cave tool.
                var deep = new List<(int, int)>();
                for (int ty = ayH; ty <= ayH + 1; ty++)
                    for (int tx = ax + 8; tx <= ax + 9; tx++)
                    {
                        if (!terr.MarkRimGate(tx, ty, 1 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 1, dummy: false);
                        deep.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                Assert.Greater(deep.Count, 1, "deepen paint painted too few (rim gate refused?)");
                TestContext.WriteLine($"deepened in {CarveOut(terr, deep)} steps");

                int floorRow = (ayH + 2) * 8;
                var west = FindArmFloor(nav, (ax + 2) * 8, (ax + 6) * 8, floorRow);
                var east = FindArmFloor(nav, (ax + 12) * 8, (ax + 15) * 8, floorRow);
                // the deepened well's own floor cell (standing z ≈ 4)
                (int sx, int sy) mid = default;
                for (int sx = (ax + 8) * 8; sx <= (ax + 10) * 8 && mid == default; sx++)
                    for (int sy = floorRow - 8; sy <= floorRow + 8; sy++)
                        if (nav.SubIsFloor(sx, sy) && nav.GetHmapSub(sx, sy - 1) <= 5) { mid = (sx, sy); break; }
                TestContext.WriteLine($"anchor=({ax},{ay}) ayH={ayH} west={west} east={east} midWell={mid}");
                DumpWindow(nav, ax + 5, ax + 13, ayH - 1, ayH + 3);
                Assert.IsTrue(west != default && east != default, "tunnel arms missing");
                Assert.AreNotEqual(default((int, int)), mid, "no deepened floor cell (seam not created)");
                TestContext.WriteLine($"seam check: west stand h={nav.GetHmapSub(west.sx, west.sy - 1)}, " +
                    $"well stand h={nav.GetHmapSub(mid.sx, mid.sy - 1)}, east stand h={nav.GetHmapSub(east.sx, east.sy - 1)}");

                RunRouteChecks2(rootGo, nav, new[]
                {
                    ("across the well W→E", west, east),
                    ("across the well E→W", east, west),
                    ("into the well", west, mid),
                    ("OUT of the well", mid, west),
                    ("OUT of the well east", mid, east),
                });
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        static void RunRouteChecks2(GameObject rootGo, NavWorld nav,
            (string label, (int sx, int sy) a, (int sx, int sy) b)[] routes)
        {
            var svc = rootGo.GetComponent<NavWorldService>()!;
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
            Vector3 P((int sx, int sy) c) => W(new Vector3(c.sx / 8f, c.sy / 8f, StandZ(nav, c)));
            var fails = new StringBuilder();
            foreach (var (label, a, b) in routes)
            {
                var p = svc.FindPath(P(a), P(b));
                TestContext.WriteLine($"[{label}] ({a.sx},{a.sy}) → ({b.sx},{b.sy}) = " +
                    (p == null ? "NULL" : $"{p.Count} wps"));
                if (p == null)
                {
                    fails.AppendLine($"{label}: NULL plan");
                    TestContext.WriteLine(Diag(nav, a, b));
                }
                else DumpPlan(p, root, nav);
            }
            Assert.AreEqual(0, fails.Length,
                "depth-seam routes NULLed (the Play T-junction symptom):\n" + fails);
        }

        // -----------------------------------------------------------------------------------
        // T10 — the REAL CAVE STENCIL, cave0 from data/gui) reached by shaft+tunnel
        // (the CaveDigRepro recipe), then a FULL-DEPTH pit (mode-0 cnt-0) dug through the cave's
        // interior floor — the player's exact tool mix for a T made of a cave + a pit. Routes +
        // the assembly-null sweep over the whole complex.
        // -----------------------------------------------------------------------------------
        [Test]
        public void TJunction_RealCaveStencil_PitThroughCaveFloor()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            try
            {
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

                var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
                var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
                TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");
                int ayH = ay + 12;
                var (tbx, tby) = DigMarquee.SnapDragCorner(ax, ayH, ax + 12f, ayH);
                var tun = CommitMarquee(terr, ax, ayH, tbx, tby);
                TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tun)} steps");

                var tpl = CaveTemplates.Load(DataRoot)[0];
                Assert.IsNotNull(tpl, "cave0.tga failed to load");
                int ayC = ayH + 2;
                int axC = -1;
                for (int cx = ax + 2; cx <= ax + 24; cx += 2)
                {
                    var verdicts = new List<(int tx, int ty, bool valid)>();
                    if (CaveStamp.Validate(tpl!, cx, ayC, terr, 0, verdicts)) { axC = cx; break; }
                }
                Assert.GreaterOrEqual(axC, 0, "cave0 never validated along the tunnel");
                int painted = CaveStamp.Commit(tpl!, axC, ayC, terr, 0, out int mouthX, out int mouthY);
                Assert.Greater(painted, 0, "cave stamp did not commit");
                var (cx0, cy0) = CaveStamp.Origin(axC, ayC);
                var caveMarks = new List<(int, int)>();
                for (int row = 0; row < CaveTemplate.Size; row++)
                    for (int col = 0; col < CaveTemplate.Size; col++)
                        if (tpl!.Cell(col, row) != CaveTemplate.Skip)
                            caveMarks.Add((cx0 + col, cy0 + row));
                TestContext.WriteLine($"cave0 at anchor ({axC},{ayC}), {painted} cells; carved in {CarveOut(terr, caveMarks)} steps");

                // The cave's interior floor cells (deep stand h ≤ 8): find the x span.
                var caveFloor = new List<(int sx, int sy)>();
                for (int sy = cy0 * 8; sy < (cy0 + CaveTemplate.Size + 2) * 8; sy++)
                    for (int sx = cx0 * 8; sx < (cx0 + CaveTemplate.Size) * 8; sx++)
                        if (nav.SubIsFloor(sx, sy) && nav.GetHmapSub(sx, sy - 1) <= 8)
                            caveFloor.Add((sx, sy));
                Assert.Greater(caveFloor.Count, 8, "no carved cave interior floor found");
                int minX = int.MaxValue, maxX = int.MinValue;
                foreach (var c in caveFloor) { minX = Mathf.Min(minX, c.sx); maxX = Mathf.Max(maxX, c.sx); }
                int midTx = ((minX + maxX) / 2) / 8;
                TestContext.WriteLine($"cave floor span sx[{minX}..{maxX}], pit at tiles x[{midTx}..{midTx + 1}]");

                // FULL-DEPTH pit through the cave floor (mode-0 cnt-0 → target 0), 6 tiles down.
                var pit = new List<(int, int)>();
                for (int ty = ayC; ty <= ayC + 10; ty++)
                    for (int tx = midTx; tx <= midTx + 1; tx++)
                    {
                        if (!terr.TileMaterialDiggable(tx, ty)) continue;
                        if (!terr.MarkRimGate(tx, ty, 0 | 8)) continue;
                        terr.SetDigMark(tx, ty, 0, cnt: 0, dummy: false);
                        pit.Add((tx, ty));
                    }
                terr.IncPlayerDigMarkId();
                Assert.Greater(pit.Count, 1, "pit paint painted too few");
                TestContext.WriteLine($"pit carved in {CarveOut(terr, pit)} steps");

                // Arms = cave floor cells clear of the pit cols; bottom = deepest pit floor.
                (int sx, int sy) west = default, east = default;
                foreach (var c in caveFloor)
                {
                    if (!nav.SubIsFloor(c.sx, c.sy)) continue;      // may have been re-derived away
                    if (c.sx < midTx * 8 - 2 && (west == default || c.sx > west.sx)) west = c;
                    if (c.sx > (midTx + 2) * 8 + 2 && (east == default || c.sx < east.sx)) east = c;
                }
                var bottom = FindPitBottom(nav, midTx, ayC + 1, ayC + 13);
                TestContext.WriteLine($"west={west} east={east} bottom={bottom}");
                DumpWindow(nav, midTx - 3, midTx + 4, ayC - 2, ayC + 12);
                Assert.AreNotEqual(default((int, int)), bottom, "no pit floor below the cave floor");
                bool haveWest = west != default, haveEast = east != default;
                Assert.IsTrue(haveWest || haveEast, "no cave floor arm survived the pit");

                RunRouteChecks(rootGo, nav, west, east, bottom, haveWest, haveEast);

                // And the sweep oracle over the whole cave+pit complex.
                SweepAssemblyNulls(rootGo, nav,
                    x0: cx0 * 8, x1: (cx0 + CaveTemplate.Size) * 8,
                    y0: (ayC - 2) * 8, y1: bottom.sy + 4,
                    goals: new[] { ("west", west), ("east", east), ("bottom", bottom) });
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // Arm floor probe: the first FLOOR cell scanning x toward the junction at ±8 rows of the
        // expected tunnel floor line row.
        static (int sx, int sy) FindArmFloor(NavWorld nav, int sxFrom, int sxTo, int floorRow)
        {
            for (int sx = sxTo; sx >= sxFrom; sx--)
                for (int sy = floorRow - 8; sy <= floorRow + 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        static (int sx, int sy) FindPitBottom(NavWorld nav, int ptx, int tyFrom, int tyTo)
        {
            for (int sy = tyTo * 8 + 7; sy >= tyFrom * 8; sy--)
                for (int sx = (ptx - 1) * 8; sx <= (ptx + 3) * 8; sx++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        static void DumpPlan(List<NavWaypoint> path, Transform root, NavWorld nav)
        {
            foreach (var wp in path)
            {
                Vector3 n = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(wp.Pos));
                int sx = Mathf.RoundToInt(n.x * 8f), sy = Mathf.RoundToInt(n.y * 8f);
                TestContext.WriteLine($"    {(wp.IsWall ? "WALL " : "floor")} native=({n.x:0.00},{n.y:0.00},{n.z:0.00}) " +
                    $"sub=({sx},{sy}) val={nav.SubAt(sx, sy) & 3} h={nav.GetHmapSub(sx, sy)}/{nav.GetHmapSub(sx, sy - 1)}");
            }
        }
    }
}
