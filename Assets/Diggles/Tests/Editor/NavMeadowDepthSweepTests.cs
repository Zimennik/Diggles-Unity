#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // ============================================================================================
    // PLAY-REGRESSION repro:.
    //
    // The missing-battery hole: every existing meadow FindPath test drives goals at the SEED depth
    // (GetHmapSub of the goal cell = 15 → band-snap → 14.5) or asserts only (x,y)/support — none
    // ever asserts that a MID-DEPTH goal (the free-roaming dig axis, z 4..13) LANDS at its own
    // depth. Ground truth for the assert: the live dump has meadow gnomes spread across DEPTH
    // z = 6..15 on the y=30.5 ground line (walkability audit, NavWorld.cs frame note) —
    // mid-meadow depths ARE ordinary standing spots; and the native final-leg law keeps the STORED
    // target depth, merely band-snapped (FUN_0041aac0:97-98 seeds the flood depth from owner+0x30;
    // FUN_0041d8a0:240-296 emits the snapped row·0.25) — the meadow band is [h(above)·4+1, 59)
    // with h(above) ≈ 0, so a clicked depth 4..13 must SURVIVE to the landing.
    //
    // Sweeps a grid of click-style goals (many x columns × depths {4,7,10,13} + click-rounding
    // y-jitter) across the open meadow line and asserts each landing keeps the clicked column AND
    // the clicked depth (no front-edge 14.5 / back-edge h+1 collapse), no depth detours along the
    // way, no z≈15/val-0 cosmetic waypoints, and that FloorPathMap's T-junction goal-pin fallback
    // (the 13.07 hunk) never fires for ordinary meadow goals.
    // ============================================================================================
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class NavMeadowDepthSweepTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static (GameObject root, NavWorld nav, NavWorldService svc) BuildStartMeadow()
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
            var svc = rootGo.GetComponent<NavWorldService>() ?? rootGo.AddComponent<NavWorldService>();
            return (rootGo, terr.Nav!, svc);
        }

        // The goal column's own ground-line row: nearest +0x3c FLOOR cell to the reference row.
        static int FindLineRow(NavWorld nav, int sx, int refSy)
        {
            int best = -1, bestD = int.MaxValue;
            for (int sy = refSy - 16; sy <= refSy + 16; sy++)
                if (nav.SubIsFloor(sx, sy))
                {
                    int d = (sy - refSy) * (sy - refSy);
                    if (d < bestD) { bestD = d; best = sy; }
                }
            return best;
        }

        [Test]
        public void MeadowSweep_MidDepthGoals_LandAtClickedDepth_NotAtEdges()
        {
            var (rootGo, nav, svc) = BuildStartMeadow();
            try
            {
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                // The longest open meadow ground line (same scan as NavWorldRealTerrainTests).
                (int sx, int sy) a = default, b = default;
                for (int sy = 23 * 8; sy < 33 * 8 && b == default; sy++)
                    for (int sx = 230 * 8; sx < 262 * 8; sx++)
                    {
                        if (!nav.SubIsFloor(sx, sy)) continue;
                        int end = sx; while (nav.SubIsFloor(end + 1, sy)) end++;
                        if (end - sx >= 80) { a = (sx + 4, sy); b = (end - 4, sy); }
                        sx = end;
                    }
                Assert.AreNotEqual(default((int, int)), b, "no straight meadow line found");

                // Depth profile diagnostics: what the band/seat law sees per column.
                var prof = new StringBuilder("[PROFILE] col: h(sy-2) h(sy-1) h(sy) h(sy+1) | band rows\n");
                for (int gx = a.sx; gx <= b.sx; gx += 32)
                {
                    int gy = FindLineRow(nav, gx, a.sy);
                    if (gy < 0) { prof.AppendLine($"  sx={gx}: NO LINE"); continue; }
                    int h2 = nav.GetHmapSub(gx, gy - 2), h1 = nav.GetHmapSub(gx, gy - 1);
                    int h0 = nav.GetHmapSub(gx, gy), hB = nav.GetHmapSub(gx, gy + 1);
                    prof.AppendLine($"  sx={gx} lineSy={gy}: {h2} {h1} {h0} {hB} | [{h1 * 4 + 1},{hB * 4 - 1})");
                }
                TestContext.WriteLine(prof.ToString());

                // Start mid-line at a MID depth (a body roaming the meadow, dump law z∈[6..15]).
                int hAStart = nav.GetHmapSub(a.sx, a.sy - 1);
                float zStart = Mathf.Clamp(10f, hAStart + 0.5f, 14.5f);
                Vector3 startW = W(new Vector3(a.sx * 0.125f, a.sy * 0.125f, zStart));

                FloorPathMap.PinFallbackFires = 0;
                var fails = new StringBuilder();
                int nGoals = 0, nNull = 0;
                var depths = new[] { 4f, 7f, 10f, 13f };

                for (int gx = a.sx + 16; gx <= b.sx; gx += 16)
                {
                    int gy = FindLineRow(nav, gx, a.sy);
                    if (gy < 0) continue;
                    int hA = nav.GetHmapSub(gx, gy - 1);

                    foreach (float depth in depths)
                    {
                        // only depths inside this column's own standable band (band = [hA·4+1, 59)
                        // over an undug floor — StampBands, FUN_0041c4b0:135-226)
                        if (depth < hA + 0.5f || depth > 14f) continue;

                        // Three click-rounding variants: exactly ON the line + one row up (open
                        // pocket) + one row down (solid) — a real click/vector_fix can round ±1.
                        foreach (int dy in new[] { 0, -1, +1 })
                        {
                            nGoals++;
                            var goalN = new Vector3(gx * 0.125f, (gy + dy) * 0.125f, depth);
                            var path = svc.FindPath(startW, W(goalN));
                            if (path == null)
                            {
                                nNull++;
                                fails.AppendLine($"  sx={gx} dy={dy} depth={depth}: NULL " +
                                    $"({NavWorldService.LastNullReason})");
                                continue;
                            }
                            Vector3 last = N(path[path.Count - 1].Pos);
                            // (a) lands at the clicked column (the lateral contract)
                            float dXY = Vector2.Distance(new Vector2(last.x, last.y),
                                new Vector2(gx * 0.125f, gy * 0.125f));
                            if (dXY > 0.6f)
                                fails.AppendLine($"  sx={gx} dy={dy} depth={depth}: landed " +
                                    $"({last.x:0.00},{last.y:0.00}) — {dXY:0.00} off the clicked column");
                            // (b) the landing DEPTH survives (the Play regression: collapses to the
                            // back edge ≈ h+1 or the front edge 14.5)
                            if (Mathf.Abs(last.z - depth) > 0.75f)
                                fails.AppendLine($"  sx={gx} dy={dy} depth={depth}: landed z={last.z:0.00} " +
                                    $"— depth collapse (hA={hA}, seat={Mathf.Min(hA + 1f, 14.5f):0.0})");
                            // (c) intermediates: no depth detour to an edge, no z≈15/val-0 cosmetics
                            float zLo = Mathf.Min(zStart, depth) - 1.5f;
                            float zHi = Mathf.Max(zStart, depth) + 1.5f;
                            for (int k = 1; k < path.Count - 1; k++)
                            {
                                if (path[k].IsWall) continue;
                                Vector3 wp = N(path[k].Pos);
                                if (wp.z < zLo || wp.z > zHi)
                                    fails.AppendLine($"  sx={gx} dy={dy} depth={depth}: wp[{k}] " +
                                        $"z={wp.z:0.00} outside [{zLo:0.0},{zHi:0.0}] — edge detour");
                                if (wp.z > 14.6f)
                                    fails.AppendLine($"  sx={gx} dy={dy} depth={depth}: wp[{k}] " +
                                        $"z={wp.z:0.00} — the z=15/val-0 cosmetic waypoint (T10 note)");
                            }
                        }
                    }
                }

                // Cross-slope FLOOD routes (the T10 "cosmetic z=15/val-0 waypoints" note): a goal
                // on the bowl-slope band (h(above) ≥ 8 ⇒ band [h·4+1,59)) forces the straight LOS
                // through masked rows of the slope columns → the 5-pass flood + interior settle
                // run. The settle must rest waypoints ON the ground line (probe row = subY+1,
                // FUN_0041d8a0:985,1013-1022) — the old own-row overshoot parked them one row
                // into the solid, where AirClamp raised z to 15 (front-edge stretches mid-route).
                for (int gx = a.sx; gx <= b.sx + 64; gx += 8)
                {
                    int gy = FindLineRow(nav, gx, a.sy);
                    if (gy < 0) continue;
                    int hA = nav.GetHmapSub(gx, gy - 1);
                    if (hA < 8 || hA >= 14) continue;          // want a slope-band goal column
                    float depth = Mathf.Min(hA + 1.5f, 14f);
                    nGoals++;
                    var path = svc.FindPath(startW, W(new Vector3(gx * 0.125f, gy * 0.125f, depth)));
                    if (path == null)
                    {
                        fails.AppendLine($"  SLOPE sx={gx} depth={depth:0.0}: NULL " +
                            $"({NavWorldService.LastNullReason})");
                        continue;
                    }
                    Vector3 lastS = N(path[path.Count - 1].Pos);
                    if (Mathf.Abs(lastS.z - depth) > 0.75f)
                        fails.AppendLine($"  SLOPE sx={gx} depth={depth:0.0}: landed z={lastS.z:0.00}");
                    for (int k = 1; k < path.Count - 1; k++)
                    {
                        if (path[k].IsWall) continue;
                        Vector3 wp = N(path[k].Pos);
                        if (wp.z > 14.6f)
                            fails.AppendLine($"  SLOPE sx={gx} depth={depth:0.0}: wp[{k}] " +
                                $"z={wp.z:0.00} — z=15/val-0 cosmetic (settle overshoot)");
                    }
                    break;   // one slope goal is enough — keep the sweep fast
                }

                TestContext.WriteLine($"sweep: {nGoals} goals ({nNull} null), " +
                    $"pin-fallback fires = {FloorPathMap.PinFallbackFires}");
                if (fails.Length > 0) TestContext.WriteLine("FAILURES:\n" + fails);
                Assert.Greater(nGoals, 12, "sweep degenerated — too few band-legal goals");
                Assert.AreEqual(0, fails.Length,
                    "mid-depth meadow goals must land at their clicked depth/column:\n" + fails);
                // Hunk-B inertness (the goal-pin fallback): ordinary meadow goals are always
                // band-snappable — the FUN_0041bbd0-style force-pin must never engage here.
                Assert.AreEqual(0, FloorPathMap.PinFallbackFires,
                    "the T-junction goal-pin fallback fired for ordinary meadow goals");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }
    }
}
