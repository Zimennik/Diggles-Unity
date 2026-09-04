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

    // ======================= Play symptoms — full-stack repros =======================
    //
    // S1 the marquee STANDARD-tunnel shaft's
    // climb ribbon must be geometrically sane — no climb record parked on the FRONT depth
    // plane (native z < 1). Climb record z = NavWorldService.SupportDepth = FUN_004238e0:
    // the RAW height nibble one row up, min-of-4-neighbours only when it reads 15. An h=0
    // column above a wall cell puts the record at z≈0 → the climber rides the camera plane.
    //
    // S2 marquee marks ALONE (no
    // evt_task_dig) must funnel through the dispatcher's dig request list (FUN_0043bdd0 scan
    // → FUN_00435f50 kind-5 job) and dispatch an idle own gnome inside its work window via
    // evt_autoprod_dig — the native ambient-dig pipeline.
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class DigPlayReproTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static string Inv(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static (GameObject root, DigglesEngine engine, DiggableTerrain terr, NavWorld nav) BuildStartMeadow(bool classes)
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
            if (classes)
            {
                foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                    try { engine.Interp.Eval("call " + s); }
                    catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
                Assert.IsTrue(engine.Ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            }
            return (rootGo, engine, terr, terr.Nav!);
        }

        static (int sx, int sy) FindMeadowAnchor(NavWorld nav)
        {
            for (int sx = 238 * 8; sx < 252 * 8; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
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

        // The marquee's per-cell verdict — mirrors ClickToMoveService.CellAllowed (standard
        // tunnel) INCLUDING the M2 material band: the 3rd validity probe is the native
        // FUN_00566870:26-28 material gate (tile+0x18 & 3 ∈ {1,2}), NOT the old HasGeometry
        // stand-in — granite/open cells are silently dropped like the native painter.
        static bool CellAllowed(DiggableTerrain t, int tx, int ty)
            => DigMarquee.CellValid(tx, ty, t.Width, t.Height,
                (x, y) => t.NwCornerHeight(x, y) > 1,
                (x, y) => (t.RawDigNibble(x, y, 0) & 8) != 0,
                (x, y) => t.TileMaterialDiggable(x, y));

        // The NATIVE-DIGGABLE dig site. The start-meadow bowl is lined with a STONE crust
        // (unq_start.pmp raw byte 4 → material type 0 at rows 31-32, x≈212..254): there the
        // native painter refuses (FUN_005672d0:41-44) and dig_execute's swing gate
        // rand < digcount·[get_material]·0.7 is rand < 0 — the gnome grinds forever
        //. The ONLY earth (type 2) column band reaching the
        // meadow surface is the eastern-slope chimney x≈256..258 — the original forces the
        // player's first vertical dig there. Returns the chimney column + its floor sub-cell.
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

        // Paint a marquee-exact standard-tunnel order and return the marked tiles.
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

        // Carve every marked node to completion through the PRODUCTION chain: CarveStep, but only
        // on EXPOSED tiles per round — the dig_next serving order (FUN_00597b20:128's tile+0x18
        // gate; port TileExposed). Buried marks wait for the advancing front, like the live loop.
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

        static void DumpWindow(NavWorld nav, DiggableTerrain terr, int tx0, int tx1, int ty0, int ty1)
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

        // ---------------------------------------------------------------------------------------
        // S1 — climb-record geometry of a marquee shaft: no record on the front depth plane.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void MarqueeShaft_ClimbRecords_NotOnFrontPlane_AndDescendPathSane()
        {
            var (rootGo, _, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                // Marquee anchor: the player's click on the meadow surface (SnapAnchor eats the
                // raw pick — 4k+1 lands on/above the ground line; surface-adjacent rows are the
                // first VALID cells, so the order connects to the surface and digs progressively
                // downward like the original); drag DOWN ~7 tiles. Site = the EARTH CHIMNEY
                // (the meadow bowl elsewhere is granite-crusted — FindDiggableSurfaceColumn).
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 7f);
                var marks = CommitMarquee(terr, ax, ay, bx, by);
                Assert.Greater(marks.Count, 3, "marquee painted too few cells");
                int steps = CarveOut(terr, marks);
                TestContext.WriteLine($"shaft carved in {steps} CarveStep calls");
                foreach (var (mtx, mty) in marks)
                    Assert.IsFalse(terr.IsDigMarked(mtx, mty, 0), $"mark ({mtx},{mty}) never completed");

                int tx0 = ax - 2, tx1 = ax + 3, ty0 = ay - 2, ty1 = by + 3;
                DumpWindow(nav, terr, tx0, tx1, ty0, ty1);

                // Every climb cell in/around the shaft: record depth = SupportDepth (FUN_004238e0).
                int zLow = 0, zTotal = 0, zMin = 99;
                var lows = new StringBuilder();
                for (int sy = ty0 * 8; sy < (ty1 + 1) * 8; sy++)
                    for (int sx = tx0 * 8; sx < (tx1 + 1) * 8; sx++)
                    {
                        if (!nav.SubIsClimbWall(sx, sy)) continue;
                        int z = NavWorldService.SupportDepth(nav, sx, sy);
                        zTotal++;
                        if (z < zMin) zMin = z;
                        if (z < 1) { zLow++; if (zLow <= 40) lows.Append($"({sx},{sy})z={z} "); }
                    }
                TestContext.WriteLine($"climb cells in window: {zTotal}, records z<1: {zLow} (min z={zMin})");
                if (zLow > 0) TestContext.WriteLine("  front-plane records: " + lows);

                // Rim → bottom FindPath: the actual records the climber will ride.
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var rim = FindFloorNear(nav, (ax - 3) * 8, digFloor.sy, 24);
                (int sx, int sy) bottom = default;
                for (int sy = (by + 3) * 8; sy > digFloor.sy && bottom == default; sy--)
                    for (int sx = ax * 8 - 4; sx <= (ax + 2) * 8 + 4; sx++)
                        if (nav.SubIsFloor(sx, sy)) { bottom = (sx, sy); break; }
                Assert.AreNotEqual(default((int, int)), rim, "no rim floor");
                Assert.AreNotEqual(default((int, int)), bottom, "no shaft-bottom floor (not descendable)");
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));
                float rimZ = Mathf.Min(nav.GetHmapSub(rim.sx, rim.sy) + 1, 14.5f);
                float botZ = Mathf.Min(nav.GetHmapSub(bottom.sx, bottom.sy) + 1, 14.5f);
                var path = svc.FindPath(
                    W(new Vector3(rim.sx * 0.125f, rim.sy * 0.125f, rimZ)),
                    W(new Vector3(bottom.sx * 0.125f, bottom.sy * 0.125f, botZ)));
                Assert.IsNotNull(path, "no rim→bottom path — the marquee shaft is not descendable");
                float wallZMin = 99f; int wallN = 0;
                var wallXs = new HashSet<int>();
                for (int i = 0; i < path!.Count; i++)
                {
                    var n = N(path[i].Pos);
                    TestContext.WriteLine($"  wp[{i}] {(path[i].IsWall ? "WALL " : "floor")} " +
                        $"native=({n.x:0.00},{n.y:0.00},{n.z:0.00})");
                    if (path[i].IsWall)
                    {
                        wallN++;
                        if (n.z < wallZMin) wallZMin = n.z;
                        wallXs.Add(Mathf.RoundToInt(n.x * 8f));
                    }
                }
                TestContext.WriteLine($"RESULT: wps={path.Count} wallRecords={wallN} wallZmin={wallZMin:0.00} " +
                    $"wallColumns={wallXs.Count} bottom=({bottom.sx},{bottom.sy})");
                Assert.IsFalse(path[path.Count - 1].IsWall, "descent must end standing on a floor record");
                if (wallN > 0)
                {
                    // no climb record on the front depth plane.
                    Assert.GreaterOrEqual(wallZMin, 1f,
                        "climb record(s) at native z<1 — the climber rides the FRONT depth plane (air-crawl)");
                    // one ribbon column, not a zig-zag across x.
                    Assert.LessOrEqual(wallXs.Count, 2,
                        $"climb ribbon spans {wallXs.Count} x-columns — the shaft route is not vertical");
                }
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // S2 — marks alone dispatch an idle own gnome to dig (the ambient kind-5 funnel).
        // ---------------------------------------------------------------------------------------
        [Test]
        public void MarqueeMarksAlone_DispatchIdleGnome_ToDig()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var disp = ctx.ProdDispatcher!;

                // Dig site = the EARTH CHIMNEY (material type 2) — the meadow bowl elsewhere is
                // granite-crusted (type 0), where the faithful get_material makes the swing gate
                // rand < digcount·0·0.7 never fire (the pre-fix site ground forever).
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");

                // Spawn ONE own gnome on the meadow, near the dig site.
                var spawn = FindFloorNear(nav, digTx * 8 - 12, digFloor.sy, 24);
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                interp.Eval($"set_worktime {g.Handle} 0 12");
                for (int i = 0; i < 30; i++) sim.Step(0.1);
                interp.Eval($"set_worktime {g.Handle} 0 12");   // re-pin after the delayed init re-choose

                // Marquee-paint a standard tunnel at the chimney — NO evt_task_dig.
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (bx2, by2) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 5f);
                var marks = CommitMarquee(terr, ax, ay, bx2, by2);
                Assert.Greater(marks.Count, 0, "marquee painted nothing");
                int marks0 = marks.Count;

                bool sawAutoDig = false, sawDigTask = false;
                int firstDigStep = -1;
                var clearSteps = new List<int>();      // sim step at which each mark cleared (pacing probe)
                int lastMarks = marks0;
                for (int step = 0; step < 9000; step++)
                {
                    sim.Step(0.1);
                    if (step % 10 == 0) disp.SweepNow(0);   // accelerate the 4 s/player cadence
                    if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");
                    foreach (var ev in g.PendingEvents)
                        if (ev.Name == "evt_autoprod_dig") sawAutoDig = true;
                    g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                    if (wt == "dig" && !sawDigTask) { sawDigTask = true; firstDigStep = step; }
                    int m = CountMarks(terr, marks);
                    while (m < lastMarks) { clearSteps.Add(step); lastMarks--; }
                    if (step % 200 == 0)
                        TestContext.WriteLine($"step {step}: pooled={disp.IsPooled(g.Handle, 0)} " +
                            $"reqs={disp.DigRequestCount(0)} wt={wt} state={g.CurrentState} " +
                            $"pos=({g.Position.x:0.0},{g.Position.y:0.0},{g.Position.z:0.0}) " +
                            $"marksLeft={m}");
                    if (m == 0) break;                     // the whole order carved
                }
                int marksLeft = CountMarks(terr, marks);
                // Dig PACING: sim-seconds per
                // node. Native novice expectation (z_dignwalk.tcl:312-315): digcount =
                // max(0.5, sqrt(exp_Stein·10·airhammer)) with the no-tool ×0.5 ⇒ 0.5 for a fresh
                // dwarf; P(apply/swing) = 0.5·material·0.7 ≈ 0.30; ONE CarveStep per node at
                // target 11 ⇒ ≈3.3 swings/node ≈ 4-8 s/node + walk legs between standoffs.
                if (clearSteps.Count > 1)
                {
                    var deltas = new StringBuilder();
                    for (int k = 1; k < clearSteps.Count; k++)
                        deltas.Append((clearSteps[k] - clearSteps[k - 1]) * 0.1f).Append("s ");
                    TestContext.WriteLine($"[PACING] first node at {clearSteps[0] * 0.1f}s after order; " +
                        $"per-node deltas: {deltas}");
                    float avg = (clearSteps[clearSteps.Count - 1] - clearSteps[0]) * 0.1f
                              / Mathf.Max(1, clearSteps.Count - 1);
                    TestContext.WriteLine($"[PACING] avg {avg:0.0} s/node over {clearSteps.Count} nodes " +
                        "(native novice expectation ≈ 4-8 s/node)");
                }
                TestContext.WriteLine($"RESULT: sawAutoDig={sawAutoDig} sawDigTask={sawDigTask} " +
                    $"firstDigStep={firstDigStep} marks {marks0}→{marksLeft} reqs={disp.DigRequestCount(0)}");
                Assert.IsTrue(sawAutoDig,
                    "marks alone never raised evt_autoprod_dig — the dispatcher dig funnel is broken");
                Assert.IsTrue(sawDigTask, "the gnome never entered the dig worktask from the ambient dispatch");
                Assert.Less(marksLeft, marks0, "the dispatched gnome never actually carved a marked node");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        static int CountMarks(DiggableTerrain terr, List<(int tx, int ty)> marks)
        {
            int n = 0;
            foreach (var (mtx, mty) in marks) if (terr.IsDigMarked(mtx, mty, 0)) n++;
            return n;
        }
    }
}
