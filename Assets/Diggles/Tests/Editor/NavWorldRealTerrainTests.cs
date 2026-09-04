#nullable enable

namespace Diggles.Tests
{
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // GROUND TRUTH from the PORT (not the live-game dump): build the REAL start-meadow terrain
    // (urw_unq_start at temppos {208 0}) exactly as the campaign does, then dump what NavWorld
    // actually holds — block ids, per-sub-cell GetHmapSub heights, the floor/wall classification,
    // and the gizmo cube WORLD positions. This tells us whether the port's data matches the dump
    // (tools/verify_walkable.py) and whether the floor cubes are coplanar/flat or scattered.
    [Explicit("Loads real data + BDB and builds the terrain mesh; run on demand")]
    public sealed class NavWorldRealTerrainTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [Test]
        public void EffPassWalk_Target11OneTileTunnelAndShaftGainNativePlaneBits()
        {
            const int width = 64;
            const int height = 64;
            int tileCount = width * height;
            var terrainBytes = new byte[tileCount];
            for (int index = 0; index < terrainBytes.Length; index++) terrainBytes[index] = 2;
            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = width,
                Height = height,
                BlockIdx = new uint[tileCount],
                Zone = new byte[tileCount],
                Terrain = terrainBytes,
                CellId = new ushort[tileCount],
                HasTpl = new System.Collections.BitArray(tileCount),
                Voxels = new byte[tileCount * PmpMap.SubGridLength],
                RenderXMin = 0,
                RenderYMin = 0,
                RenderXMax = width - 1,
                RenderYMax = height - 1,
            };
            BdbModel bdb = BdbParser.Parse(System.IO.Path.Combine(DataRoot, "BlockDB2.BDB"));
            var root = new GameObject(nameof(EffPassWalk_Target11OneTileTunnelAndShaftGainNativePlaneBits));
            try
            {
                DiggableTerrain terrain = root.AddComponent<DiggableTerrain>();
                terrain.Init(result, bdb, DataRoot, () => { });
                NavWorld nav = terrain.Nav!;
                Assert.That(CountPlaneCells(nav, NavWorld.SubWalk), Is.Zero,
                    "the synthetic block-0 world must begin completely solid");

                uint[] beforeHorizontal = (uint[])result.BlockIdx.Clone();
                CarveTarget11Rect(terrain, 12, 20, 25, 21);
                AssertChangedTilePlanes(
                    nav,
                    result.BlockIdx,
                    beforeHorizontal,
                    requireFloor: true,
                    "horizontal one-tile tunnel");

                uint[] beforeVertical = (uint[])result.BlockIdx.Clone();
                CarveTarget11Rect(terrain, 40, 20, 41, 33);
                AssertChangedTilePlanes(
                    nav,
                    result.BlockIdx,
                    beforeVertical,
                    requireFloor: false,
                    "vertical one-tile shaft");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void CarveTarget11Rect(
            DiggableTerrain terrain,
            int x0,
            int y0,
            int x1,
            int y1)
        {
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                terrain.SetDigMark(x, y, 0, 0, dummy: true);

            int changes = 0;
            for (int round = 0; round < 64; round++)
            {
                bool changed = false;
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    bool nodeChanged = terrain.CarveStep(x, y, 0, out _);
                    changed |= nodeChanged;
                    if (nodeChanged) changes++;
                }
                if (!changed) break;
            }
            Assert.That(changes, Is.GreaterThan(0), "the target-11 carve must change native blocks");
        }

        static void AssertChangedTilePlanes(
            NavWorld nav,
            uint[] blocks,
            uint[] before,
            bool requireFloor,
            string shape)
        {
            int changedTiles = 0;
            for (int tile = 0; tile < blocks.Length; tile++)
            {
                if (blocks[tile] == before[tile]) continue;
                changedTiles++;
                int tileX = tile % nav.Width;
                int tileY = tile / nav.Width;
                Assert.That(TileHasPlaneBit(nav, tileX, tileY, NavWorld.SubWalk), Is.True,
                    $"{shape} changed tile ({tileX},{tileY}) has no walk bit; " +
                    FirstBlockingProbe(nav, tileX, tileY));
                if (requireFloor)
                    Assert.That(
                        TileHasPlaneBit(
                            nav,
                            tileX,
                            tileY,
                            (byte)(NavWorld.SubWalk | NavWorld.SubFloor)),
                        Is.True,
                        $"{shape} changed tile ({tileX},{tileY}) has no walk+floor cell");
            }
            Assert.That(changedTiles, Is.GreaterThan(0), $"{shape} changed no tile records");
        }

        static int CountPlaneCells(NavWorld nav, byte bit)
        {
            int count = 0;
            for (int subY = 0; subY < nav.Height * PmpMap.SubGridSide; subY++)
            for (int subX = 0; subX < nav.Width * PmpMap.SubGridSide; subX++)
                if ((nav.SubAt(subX, subY) & bit) != 0) count++;
            return count;
        }

        static bool TileHasPlaneBit(NavWorld nav, int tileX, int tileY, byte bit)
        {
            for (int localY = 0; localY < PmpMap.SubGridSide; localY++)
            for (int localX = 0; localX < PmpMap.SubGridSide; localX++)
                if ((nav.SubAt(tileX * 8 + localX, tileY * 8 + localY) & bit) == bit) return true;
            return false;
        }

        static string FirstBlockingProbe(NavWorld nav, int tileX, int tileY)
        {
            for (int localY = 0; localY < PmpMap.SubGridSide; localY++)
            for (int localX = 0; localX < PmpMap.SubGridSide; localX++)
            {
                int subX = tileX * PmpMap.SubGridSide + localX;
                int subY = tileY * PmpMap.SubGridSide + localY;
                if (!nav.TryGetEffPassWalkBlockingProbe(
                        subX,
                        subY,
                        out int dx,
                        out int dy,
                        out int detail))
                {
                    continue;
                }
                return $"first native blocker at foot ({subX},{subY}) is ({dx},{dy}) detail {detail}";
            }
            return "the native 31-probe capsule is clear; inspect tile+0x18 bit 7 and the floor transition operands";
        }

        [Test]
        public void DumpStartMeadow()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);   // the Q_x(-90) hack
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");

            // Replicate the campaign's TOP-LEVEL map load (Campaign.tcl lines 2 + 13).
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }

            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            var nav = terr!.Nav;
            TestContext.WriteLine($"map {engine.Map.Width}x{engine.Map.Height}, PMPs={engine.Map.PmpsLoaded}, nav={(nav == null ? "NULL" : "ok")}");
            if (nav == null) { TestContext.WriteLine("NAV IS NULL — Init guard failed (bdb/voxels mismatch)."); return; }

            // 1) the meadow tile a dump gnome stands on: block id + 8x8 GetHmapSub heights.
            DumpTile(nav, terr, 249, 30);
            DumpTile(nav, terr, 240, 25);
            DumpTile(nav, terr, 230, 24);

            // 2) classification map over the same window verify_walkable.py printed.
            // Frame-corrected: F = the tile contains a +0x3c value-3 GROUND LINE (the floor marker row),
            // W = it contains value-1 climb cells, '.' = neither (open air or solid rock).
            TestContext.WriteLine("\n[CLASS] x224..272 y22..46  (F=has +0x3c floor line, W=has climb cells, .=neither):");
            for (int ty = 22; ty <= 46; ty++)
            {
                var sb = new StringBuilder($"y{ty:00} ");
                for (int tx = 224; tx < 272; tx++)
                {
                    int f = 0, w = 0;
                    for (int sy = 0; sy < 8; sy++)
                    for (int sx = 0; sx < 8; sx++)
                    {
                        int gx = tx * 8 + sx, gy = ty * 8 + sy;
                        if (nav.SubIsClimbWall(gx, gy)) w++;
                        else if (nav.SubIsFloor(gx, gy)) f++;
                    }
                    sb.Append(f > 0 ? 'F' : w > 0 ? 'W' : '.');
                }
                TestContext.WriteLine(sb.ToString());
            }

            // 3) ground-line stats over the meadow band: every +0x3c floor cell carries a depth STRIP
            // (z = min(h_above,h_own).. max(h_own,h_below)) — the standable face into the screen.
            int nFloor = 0, fullDepth = 0, supportRow = 0, lastOpenRow = 0;
            var perRow = new System.Collections.Generic.SortedDictionary<int, int>();
            for (int ty = 23; ty <= 33; ty++)
            for (int tx = 224; tx <= 264; tx++)
            for (int sy = 0; sy < 8; sy++)
            for (int sx = 0; sx < 8; sx++)
            {
                int gx = tx * 8 + sx, gy = ty * 8 + sy;
                if (!nav.SubIsFloor(gx, gy)) continue;
                int hO = nav.GetHmapSub(gx, gy);
                int hA = nav.GetHmapSub(gx, gy - 1);
                int hB = nav.GetHmapSub(gx, gy + 1);
                int lo = Mathf.Min(hA, hO), hi = Mathf.Max(hO, hB);
                nFloor++;
                if (hO > hA) supportRow++; else if (hB > hO) lastOpenRow++;
                if (hi - lo >= 15) fullDepth++;
                perRow.TryGetValue(gy, out var c); perRow[gy] = c + 1;
            }
            TestContext.WriteLine($"\n[GROUND LINES] x224..264 y23..33: {nFloor} floor cells " +
                                  $"(support-row {supportRow} + last-open-row {lastOpenRow}; full-depth strips {fullDepth})");
            foreach (var kv in perRow)
                TestContext.WriteLine($"  subY {kv.Key} (tileY {kv.Key / 8}, line y={(kv.Key) * 0.125f:0.000}): {kv.Value} cells");
            TestContext.WriteLine("  Expect: lines concentrated on few subY rows (e.g. 244 = the y=30.5 meadow line), NOT spread per-tile.");

            Object.DestroyImmediate(rootGo);
        }

        // P1 verification (GNOME_BEHAVIOR_COMPLETE_RE §11 Phase 1): the EFFPASSWALK port must
        // REPRODUCE the PMP-loaded +0x3c from geometry alone — the python probe on the live dump
        // achieved 1672/1672 = 100% of value-3 cells (tools/probe_effpasswalk_verify.py); this is
        // the same check against the port's own world build. value-1 may differ slightly at band
        // edges (informational); every value-3 ground-line cell must be derived exactly.
        [Test]
        public void EffPassWalk_ReproducesLoadedSubGrid()
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
            var nav = rootGo.GetComponent<DiggableTerrain>()?.Nav;
            Assert.IsNotNull(nav, "no NavWorld");

            // Template interior (skip 2 tiles at each edge so the 31-cell open band has full context).
            int sx0 = (208 + 2) * 8, sx1 = (208 + 60 - 2) * 8 - 1;
            int sy0 = 2 * 8, sy1 = (36 - 2) * 8 - 1;
            int v3 = 0, v3ok = 0, v1 = 0, v1ok = 0, extra3 = 0, extra1 = 0;
            var misses = new StringBuilder();
            for (int sy = sy0; sy <= sy1; sy++)
            for (int sx = sx0; sx <= sx1; sx++)
            {
                byte loaded = (byte)(nav!.SubAt(sx, sy) & 3);
                byte derived = (byte)(nav.DeriveSubCell(sx, sy) & 3);
                if (loaded == 3)
                {
                    v3++;
                    if (derived == 3) v3ok++;
                    else if (misses.Length < 2000)
                        misses.AppendLine($"  miss3 sub({sx},{sy}) tile({sx / 8},{sy / 8}) derived={derived} " +
                            $"h={nav.GetHmapSub(sx, sy)} hA={nav.GetHmapSub(sx, sy - 1)} hB={nav.GetHmapSub(sx, sy + 1)}");
                }
                else if (loaded == 1) { v1++; if (derived == 1) v1ok++; }
                else { if (derived == 3) extra3++; else if (derived == 1) extra1++; }
            }
            TestContext.WriteLine($"value-3: {v3ok}/{v3}   value-1: {v1ok}/{v1}   extra3={extra3} extra1={extra1}");
            if (misses.Length > 0) TestContext.WriteLine(misses.ToString());
            Assert.Greater(v3, 0, "no value-3 cells in the window?");
            Assert.AreEqual(v3, v3ok, "EFFPASSWALK must reproduce every loaded value-3 ground-line cell");
            Object.DestroyImmediate(rootGo);
        }

        // P4 verification (GNOME_BEHAVIOR_COMPLETE_RE §11 Phase 4, corrected attribution): the
        // FloorPathMap is the FLOOR depth resolver — on the open meadow line the LOS short-circuit
        // must fire (2/3-point straight route, no flood) and end exactly at the goal.
        [Test]
        public void FloorPathMap_MeadowLine_LosShortCircuit()
        {
            var (rootGo, nav) = BuildStartMeadow();
            // Two value-3 floor cells on one ground line, ≥8 tiles apart.
            (int sx, int sy) a = default, b = default;
            for (int sy = 23 * 8; sy < 33 * 8 && b == default; sy++)
            {
                for (int sx = 230 * 8; sx < 262 * 8; sx++)
                {
                    if (!nav!.SubIsFloor(sx, sy)) continue;
                    int end = sx; while (nav.SubIsFloor(end + 1, sy)) end++;   // contiguous run
                    if (end - sx >= 64) { a = (sx, sy); b = (end, sy); }
                    sx = end;
                }
            }
            Assert.AreNotEqual(default((int, int)), b, "no straight meadow line found");
            float za = nav!.GetHmapSub(a.sx, a.sy), zb = nav.GetHmapSub(b.sx, b.sy);
            var start = new Vector3(a.sx * 0.125f, a.sy * 0.125f, za);
            var goal = new Vector3(b.sx * 0.125f, b.sy * 0.125f, zb);
            var control = new System.Collections.Generic.List<Vector2>
                { new(start.x, start.y), new(goal.x, goal.y) };
            var route = FloorPathMap.Build(nav, control, start, goal, forceThreePoint: true, finalLeg: true);
            Assert.IsNotNull(route, "no route along an open ground line");
            TestContext.WriteLine($"meadow ({a.sx},{a.sy})→({b.sx},{b.sy}) z {za}→{zb}: " +
                $"{route!.Count} wps: {string.Join("  ", route)}");
            Assert.LessOrEqual(route.Count, 3, "open line must LOS-short-circuit (2/3-pt), not flood");
            // The route ends at the goal's LATERAL (x,y); its z is the band-RESOLVED standable depth
            // (goalRow·0.25), NOT the raw seed z. Here the seed z=15 is the SOLID under the surface
            // (band [1,59)), so the dwarf stands at 14.5 — the surface. Emitting the raw z instead made
            // the final leg a pure-DEPTH step that turned the dwarf toward the camera (urw_3 facing fix,
            //): assert (x,y) lands on the goal and the depth sits at/above the seed (on the
            // surface, never inside the solid).
            var endWp = route[route.Count - 1];
            Assert.Less(Vector2.Distance(new Vector2(endWp.x, endWp.y), new Vector2(goal.x, goal.y)), 1e-3f,
                "route must end at the goal's (x,y)");
            Assert.LessOrEqual(endWp.z, goal.z + 0.01f, "standable depth must be at/above the seed (on the surface)");
            Object.DestroyImmediate(rootGo);
        }

        // P4 verification — the player's Play scenario end-to-end: dig a shaft below the meadow line
        // (the same carve path Ctrl+LMB drives), revalidate (EFFPASSWALK→EFFBLOCK over the dirty
        // rect, the P1 contract), then FindPath from the meadow into the pit. The WALL run must
        // come out as the CAct_Climb record mirror (the Calc1 cells at FUN_004238e0 support
        // depths — NOT a densified flood: the live-dump probe tools/probe_climb_band.py proved
        // the FloorPathMap bands are EMPTY on wall faces) and the path must END standing on the
        // pit-bottom floor cell.
        [Test]
        public void FindPath_DugShaft_ClimbRecordsOnWallFace()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var terr = rootGo.GetComponent<DiggableTerrain>()!;
            var svc = rootGo.AddComponent<NavWorldService>();

            // A meadow ground-line cell to anchor the pit (the y=30.5 line, subY 244).
            (int sx, int sy) anchor = default;
            for (int sx = 238 * 8; sx < 252 * 8 && anchor == default; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav!.SubIsFloor(sx, sy)) { anchor = (sx, sy); break; }
            Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
            int ptx = anchor.sx / 8, pty = anchor.sy / 8 + 1;   // the rock tile under the line
            TestContext.WriteLine($"meadow anchor ({anchor.sx},{anchor.sy}) → pit tiles ({ptx},{pty}..{pty + 2})");

            // Carve a 1-tile-wide, 3-tile-deep shaft: step all nodes of tiles (ptx, pty..pty+2)
            // down until terminal (the engine's atomic dig unit, repeated — as the gnome's loop).
            for (int round = 0; round < 64; round++)
            {
                bool any = false;
                for (int ny = pty; ny <= pty + 3; ny++)
                    for (int nx = ptx; nx <= ptx + 1; nx++)
                        any |= terr.CarveTileNow(nx, ny);
                if (!any) break;
            }
            // The Update half of the carve contract, invoked directly (no Play loop in EditMode).
            nav!.RevalidateRect((ptx - 2) * 8, (pty - 2) * 8, (ptx + 3) * 8 + 7, (pty + 5) * 8 + 7);

            // The shaft must now read as a climb band with a floor landing below it.
            (int sx, int y0, int y1) band = default;
            (int sx, int sy) bottom = default;
            for (int sx = (ptx - 1) * 8; sx <= (ptx + 2) * 8 && band == default; sx++)
                for (int sy = anchor.sy; sy < (pty + 5) * 8; sy++)
                {
                    if (!nav.SubIsClimbWall(sx, sy)) continue;
                    int yEnd = sy; while (nav.SubIsClimbWall(sx, yEnd)) yEnd++;
                    if (yEnd - sy >= 4)
                        for (int d = 0; d <= 4 && bottom == default; d++)
                            for (int ox = -4; ox <= 4 && bottom == default; ox++)
                                if (nav.SubIsFloor(sx + ox, yEnd + d)) { bottom = (sx + ox, yEnd + d); band = (sx, sy, yEnd); }
                    sy = yEnd;
                }
            if (band == default)
            {
                // Diagnose: dump the pit columns.
                for (int sy = anchor.sy - 4; sy < (pty + 5) * 8; sy++)
                {
                    var row = new StringBuilder($"  y={sy} ");
                    for (int sx = (ptx - 2) * 8; sx <= (ptx + 3) * 8; sx++)
                        row.Append((nav.SubAt(sx, sy) & 3) switch { 3 => 'F', 1 => 'W', _ => '.' });
                    TestContext.WriteLine(row.ToString());
                }
                Assert.Fail("the dug shaft produced no climb band with a floor landing");
            }
            TestContext.WriteLine($"band x={band.sx} y=[{band.y0}..{band.y1}) bottom=({bottom.sx},{bottom.sy})");

            // Start on the meadow line ~5 tiles to the side of the pit.
            (int sx, int sy) rim = default;
            for (int sx = band.sx - 40; sx < band.sx - 8 && rim == default; sx++)
                for (int sy = anchor.sy - 8; sy <= anchor.sy + 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) { rim = (sx, sy); break; }
            if (rim == default) { Assert.Ignore("no rim floor near the pit"); return; }
            TestContext.WriteLine($"rim=({rim.sx},{rim.sy})");

            var root = rootGo.transform;
            Vector3 W(Vector3 native) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native));
            var start = W(new Vector3(rim.sx * 0.125f, rim.sy * 0.125f, nav!.GetHmapSub(rim.sx, rim.sy)));
            var target = W(new Vector3(bottom.sx * 0.125f, bottom.sy * 0.125f, nav.GetHmapSub(bottom.sx, bottom.sy)));
            var path = svc.FindPath(start, target);
            Assert.IsNotNull(path, "no path rim→bottom");

            int walls = 0;
            var sb = new StringBuilder();
            foreach (var wp in path!)
            {
                Vector3 n = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(wp.Pos));
                sb.AppendLine($"  {(wp.IsWall ? "WALL " : "floor")} native=({n.x:0.00},{n.y:0.00},{n.z:0.00})");
                if (!wp.IsWall) continue;
                walls++;
                // Each climb record must sit at its FUN_004238e0 support depth (the wall FACE).
                int sx = Mathf.RoundToInt(n.x * 8f), sy = Mathf.RoundToInt(n.y * 8f);
                int want = NavWorldService.SupportDepth(nav, sx, sy);
                Assert.AreEqual(want, Mathf.RoundToInt(n.z),
                    $"climb record at ({sx},{sy}) must carry the support depth");
                // Wall-embedding regression (audit §2): OUTWARD = world −Z (air = the
                // larger depth nibble side; rock = world Z > the face plane). The record itself
                // rides ON the face: world z == −(supportDepth·0.5+7).
                Assert.AreEqual(new Vector3(0f, 0f, -1f), wp.Normal,
                    $"wall record at ({sx},{sy}) must carry the OUTWARD (air-side) normal (0,0,−1)");
                Assert.LessOrEqual(wp.Pos.z, -(want * 0.5f + 7f) + 0.01f,
                    $"wall record at ({sx},{sy}) must sit at/beyond the face plane on the air side");
            }
            TestContext.WriteLine(sb.ToString());
            TestContext.WriteLine($"wall records: {walls}/{path.Count}");
            Assert.Greater(walls, 0, "a cliff descent must contain WALL records");
            Assert.IsFalse(path[path.Count - 1].IsWall, "the descent must END on a floor waypoint");
            Vector3 last = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(path[path.Count - 1].Pos));
            Vector3 tgt = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(target));
            Assert.Less(Vector2.Distance(new Vector2(last.x, last.y), new Vector2(tgt.x, tgt.y)), 0.3f,
                "the path must end at the clicked floor cell");
            Object.DestroyImmediate(rootGo);
        }

        // Regression for the trailing-wall TRIM (9aeda6e, reverted): a goal ON a wall
        // cell must produce a path that CLIMBS to it and ENDS on the wall. FUN_00423730:30-35
        // returns type 0 when the floor bit is CLEAR (= WALL), and FUN_004163a0:69-72 dispatches a
        // type-0 run that reaches the path end to FUN_00415e10 = CAct_Climb — the original climbs
        // to wall goals and hangs there (kletterstand). The trim deleted the whole climb and left
        // the gnome standing at the rim.
        [Test]
        public void FindPath_WallGoal_ClimbsAndEndsOnWall_NoRimStop()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var terr = rootGo.GetComponent<DiggableTerrain>()!;
            var svc = rootGo.AddComponent<NavWorldService>();
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));

            var anchor = FindMeadowAnchor(nav!);
            Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
            int ptx = anchor.sx / 8, pty = anchor.sy / 8 + 1;
            DigShaft(terr, nav!, ptx, pty, nodesWide: 1, nodesDeep: 3);   // the 16-sub-cell-wide shaft

            // Deepest wall cell on the shaft face that the 16-sub-cell goal settle can NOT push
            // down to a floor — the goal must STAY on the wall (the original's relax keeps
            // walkable wall cells; the overhang nudge only slides over FLOOR-bit rows).
            (int sx, int sy) wallGoal = default;
            for (int sy = anchor.sy + 4; sy < (pty + 5) * 8; sy++)
                for (int sx = (ptx - 1) * 8; sx <= (ptx + 2) * 8; sx++)
                    if (nav!.SubIsClimbWall(sx, sy) && !GoalSettleFires(nav, sx, sy) && sy > wallGoal.sy)
                        wallGoal = (sx, sy);
            Assert.AreNotEqual(default((int, int)), wallGoal,
                "no mid-shaft wall cell beyond the settle reach (shaft too shallow?)");
            TestContext.WriteLine($"wall goal = ({wallGoal.sx},{wallGoal.sy}), meadow line subY {anchor.sy}");

            var rim = FindRim(nav!, ptx * 8, anchor.sy);
            if (rim == default) { Assert.Ignore("no rim floor near the pit"); return; }

            var start = W(new Vector3(rim.sx * 0.125f, rim.sy * 0.125f, nav!.GetHmapSub(rim.sx, rim.sy)));
            var target = W(new Vector3(wallGoal.sx * 0.125f, wallGoal.sy * 0.125f,
                NavWorldService.SupportDepth(nav, wallGoal.sx, wallGoal.sy)));
            var path = svc.FindPath(start, target);
            Assert.IsNotNull(path, "no path rim→wall goal");
            DumpPath(path, root, nav);

            int walls = 0;
            foreach (var wp in path!) if (wp.IsWall) walls++;
            Assert.Greater(walls, 0, "a wall-goal route must contain WALL climb records (trim regression)");
            Assert.IsTrue(path[path.Count - 1].IsWall,
                "the path must END ON the wall (CAct_Climb kletterstand hang), not stop at the rim");
            Vector3 lastN = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(path[path.Count - 1].Pos));
            Assert.Less(Mathf.Abs(lastN.x - wallGoal.sx * 0.125f), 0.3f, "final record must sit at the goal wall cell (x)");
            Assert.Less(Mathf.Abs(lastN.y - wallGoal.sy * 0.125f), 0.3f, "final record must sit at the goal wall cell (y)");
            Assert.Greater(lastN.y, (anchor.sy + 2) * 0.125f, "the hang must be DOWN the face, below the rim line");

            // Wall-embedding regression (audit): outward normals + the walk exit anchor.
            foreach (var wp in path)
                if (wp.IsWall)
                    Assert.AreEqual(new Vector3(0f, 0f, -1f), wp.Normal,
                        "wall records must carry the OUTWARD (air-side) normal (0,0,−1)");
            AssertApproachAnchor(path, root, nav!);

            // Tick the REAL executor chain to the kletterstand hang: every climb-tick position
            // must stay on the AIR side of its nearest record's face plane (rock = world Z >
            // face) — with the pre-fix inverted normal the standoff pushed the body 0.25 wu
            // INTO the rock and this fails.
            var body = new GameObject("body");
            var ctrl = body.AddComponent<DigglesAnimController>();   // bare = clip-less (headless)
            body.transform.position = path[0].Pos;
            var wallWps = new System.Collections.Generic.List<NavWaypoint>();
            foreach (var wp in path) if (wp.IsWall) wallWps.Add(wp);
            for (int si = 0; si < path.Count;)
            {
                bool wallRun = path[si].IsWall;
                int sj = si; while (sj < path.Count && path[sj].IsWall == wallRun) sj++;
                IEntityAction seg;
                if (wallRun)
                {
                    var run = new System.Collections.Generic.List<NavWaypoint>();
                    if (si > 0) run.Add(path[si - 1]);
                    for (int k = si; k < sj; k++) run.Add(path[k]);
                    if (sj < path.Count) run.Add(path[sj]);
                    seg = new ClimbAction(ctrl, body.transform, run, 0, -1, 0);
                }
                else
                {
                    var pts = new System.Collections.Generic.List<Vector3>(sj - si);
                    for (int k = si; k < sj; k++) pts.Add(path[k].Pos);
                    seg = new MetaWalkAction(ctrl, body.transform, pts, 0, -1, 0);
                }
                int guard = 0; bool more = true;
                while (more && guard++ < 20000)
                {
                    more = seg.Tick(0.1);
                    if (wallRun) AssertAirSide(body.transform.position, wallWps, "wall-goal climb tick");
                }
                Assert.Less(guard, 20000, "segment never finished");
                si = sj;
            }
            // The hang settles AT the goal record (plus the air-side standoff), not at the rim.
            Assert.Less(Mathf.Abs(body.transform.position.x - path[path.Count - 1].Pos.x), 0.3f,
                "the body must hang at the wall-goal record (x)");
            Assert.Less(Mathf.Abs(body.transform.position.y - path[path.Count - 1].Pos.y), 0.3f,
                "the body must hang at the wall-goal record (height)");
            Object.DestroyImmediate(body);
            Object.DestroyImmediate(rootGo);
        }

        // Regression for the dismount teleport (6fa93a8): each wall run ends with the CLAIMED
        // boundary floor record — FUN_004163a0:69-72 extends a type-0 (WALL) run by +1 to claim
        // the first floor record of the next run, and FUN_0040bb70:115-139 copies the last WALL
        // record's z onto it — so the dismount lands at the wall BASE (≤1-cell hop) and the
        // following MetaWalk floor segment walks the corridor. Ticks the REAL executor chain
        // (grouped exactly as DigglesTclEngine.BuildWalkSegments) and asserts no single sim tick
        // warps the body.
        [Test]
        public void FindPath_TwoLevelDescent_DismountLandsAtWallBase_NoTeleport()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var terr = rootGo.GetComponent<DiggableTerrain>()!;
            var svc = rootGo.AddComponent<NavWorldService>();
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));

            var anchor = FindMeadowAnchor(nav!);
            Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
            int ptx = anchor.sx / 8, pty = anchor.sy / 8 + 1;
            DigShaft(terr, nav!, ptx, pty, nodesWide: 4, nodesDeep: 3);   // wide bottom = a corridor

            // The widest single-row floor span below the meadow line = the bottom corridor.
            (int y, int x0, int x1) corridor = default;
            for (int sy = anchor.sy + 8; sy < (pty + 6) * 8; sy++)
                for (int sx = (ptx - 1) * 8; sx <= (ptx + 6) * 8; sx++)
                {
                    if (!nav!.SubIsFloor(sx, sy)) continue;
                    int end = sx; while (nav.SubIsFloor(end + 1, sy)) end++;
                    if (end - sx > corridor.x1 - corridor.x0) corridor = (sy, sx, end);
                    sx = end;
                }
            if (corridor.x1 - corridor.x0 < 12) { Assert.Ignore("no wide shaft-bottom corridor formed"); return; }
            TestContext.WriteLine($"corridor row sy={corridor.y}: sx [{corridor.x0}..{corridor.x1}]");

            var rim = FindRim(nav!, ptx * 8, anchor.sy);
            if (rim == default) { Assert.Ignore("no rim floor near the pit"); return; }

            // Descend (left wall), then the corridor to its FAR end.
            var start = W(new Vector3(rim.sx * 0.125f, rim.sy * 0.125f, nav!.GetHmapSub(rim.sx, rim.sy)));
            var goal = W(new Vector3(corridor.x1 * 0.125f, corridor.y * 0.125f,
                Mathf.Max(0, nav.GetHmapSub(corridor.x1, corridor.y) - 1)));
            var path = svc.FindPath(start, goal);
            Assert.IsNotNull(path, "no rim→corridor-end path");
            DumpPath(path, root, nav);
            int wallCount = 0, lastWall = -1;
            for (int k = 0; k < path!.Count; k++)
                if (path[k].IsWall) { wallCount++; lastWall = k; }
            Assert.Greater(wallCount, 0, "the two-level route must contain a climb");
            Assert.Less(lastWall + 1, path.Count, "the descent must continue past the wall onto the floor");

            // Path-level contract: the first floor waypoint AFTER the last wall record is the
            // wall-adjacent boundary record, not the corridor's far landing.
            float hop = (path[lastWall + 1].Pos - path[lastWall].Pos).magnitude;
            TestContext.WriteLine($"wall→boundary hop = {hop:0.000} world units");
            Assert.Less(hop, 1.2f, "the waypoint after the wall run must be the wall-BASE boundary record");

            // Wall-embedding regression (audit): outward normals + the walk exit anchor.
            var wallWps = new System.Collections.Generic.List<NavWaypoint>();
            foreach (var wp in path)
                if (wp.IsWall)
                {
                    Assert.AreEqual(new Vector3(0f, 0f, -1f), wp.Normal,
                        "wall records must carry the OUTWARD (air-side) normal (0,0,−1)");
                    wallWps.Add(wp);
                }
            AssertApproachAnchor(path, root, nav!);

            // Executor-level contract: tick the real segment chain; the dismount must not warp,
            // and every CLIMB-tick position must stay on the AIR side of the face plane.
            var body = new GameObject("body");
            var ctrl = body.AddComponent<DigglesAnimController>();   // bare = clip-less (headless)
            body.transform.position = path[0].Pos;
            var segs = new System.Collections.Generic.List<IEntityAction>();
            var segIsClimb = new System.Collections.Generic.List<bool>();
            for (int si = 0; si < path.Count;)
            {
                bool wallRun = path[si].IsWall;
                int sj = si; while (sj < path.Count && path[sj].IsWall == wallRun) sj++;
                if (wallRun)
                {
                    var run = new System.Collections.Generic.List<NavWaypoint>();
                    if (si > 0) run.Add(path[si - 1]);
                    for (int k = si; k < sj; k++) run.Add(path[k]);
                    if (sj < path.Count) run.Add(path[sj]);
                    segs.Add(new ClimbAction(ctrl, body.transform, run, 0, -1, 0));
                }
                else
                {
                    var pts = new System.Collections.Generic.List<Vector3>(sj - si);
                    for (int k = si; k < sj; k++) pts.Add(path[k].Pos);
                    segs.Add(new MetaWalkAction(ctrl, body.transform, pts, 0, -1, 0));
                }
                segIsClimb.Add(wallRun);
                si = sj;
            }
            float maxTick = 0f; int atSeg = -1;
            Vector3 prevPos = body.transform.position;
            for (int s = 0; s < segs.Count; s++)
            {
                int guard = 0; bool more = true;
                while (more && guard++ < 20000)
                {
                    more = segs[s].Tick(0.1);   // fixed sim step — the dismount snap lands HERE
                    float d = (body.transform.position - prevPos).magnitude;
                    if (d > maxTick) { maxTick = d; atSeg = s; }
                    prevPos = body.transform.position;
                    if (segIsClimb[s])
                    {
                        AssertAirSide(body.transform.position, wallWps, $"descent climb tick (seg {s})");
                        // Climb ticks move at CLIMB cruise (clip-less fallback 1.0389 wu/s →
                        // 0.104 wu/tick). A walk-speed (0.20 wu/tick) move inside a climb segment
                        // is the removed mid-air "Approach" leg sneaking back (
                        // gnomes mounted from mid-air over the pit center).
                        Assert.LessOrEqual(d, 0.15f,
                            $"climb seg {s} moved {d:0.000} wu in one tick — faster than climb cruise");
                    }
                }
                Assert.Less(guard, 20000, $"segment {s} never finished");
            }
            TestContext.WriteLine($"max single-tick move = {maxTick:0.000} world units (segment {atSeg}); " +
                $"final={body.transform.position} pathEnd={path[path.Count - 1].Pos}");
            Assert.Less(maxTick, 1.0f, "no single sim tick may warp the body (dismount teleport regression)");
            Assert.Less((body.transform.position - path[path.Count - 1].Pos).magnitude, 0.6f,
                "the segment chain must arrive at the corridor goal");
            Object.DestroyImmediate(body);
            Object.DestroyImmediate(rootGo);
        }

        // Pose-level lock for the facing fix (wall-embedding audit §1-2): the kletter
        // poses are authored with the body MASS on the model-FRONT side (mann.3db kletterstand
        // frame-0 vertex centroid ≈ +38.9 raw ≈ +0.19 wu along mesh +Z — probe
        // tools/probe_kletter_pose.py), so for the climber to hang in AIR the front must aim at
        // the wall's OUTWARD side = world −Z. Builds the REAL spawn chain (outer + inner Model
        // with Q_x(−90)·markerRot, mann marker rY=270°), steers the outer with ClimbAction's own
        // facing over records carrying the (0,0,−1) outward normal, then asserts the rendered
        // vertex centroid sits on the −Z (air) side of the root. Guards against a future
        // un-flip of either the climb facing or the pose-front convention.
        // facing flip (Play strike #3: — the bfdc9d0 air-facing
        // climber showed his belly to the camera): the climber must face INTO the wall (world +Z;
        // the Tcl wall pose pins roty=π, z_procs.tcl:1707, and the verified named-angle map sends
        // π → +Z), with the pose's front-mass bulge kept clear of the FACE PLANE by the 0.25-wu
        // outward WallStandoff (root at face−0.25 ⇒ centroid +0.19 lands ≈ on/just outside the face).
        [Test]
        public void KletterPose_FacesWall_MassStaysClearOfFacePlane()
        {
            var lib = new DigglesModelLibrary(DataRoot);
            DigglesAnimClip? clip = lib.GetClip("mann", "kletterstand");
            if (clip == null)
                foreach (var name in lib.GetClipNames("mann"))
                    if (name.StartsWith("kletter")) { clip = lib.GetClip("mann", name); break; }
            if (clip == null || clip.Frames == null || clip.Frames.Length == 0 || clip.Frames[0] == null)
            { Assert.Inconclusive("mann.3db kletter clips not loadable headless (data root missing?)"); return; }
            TestContext.WriteLine($"pose clip = {clip.Name}, frame-0 verts = {clip.Frames[0].vertexCount}");

            var outer = new GameObject("climber");
            var model = new GameObject("Model");
            model.transform.SetParent(outer.transform, false);
            // The spawn inner chain (RuntimeWorldSpawner.BuildInner/ApplyRotation): Q_x(−90) ·
            // markerRot; mann's marker rY = 270° native (DigglesObject spawn-facing note) makes
            // the mesh front (+Z) horizontal in world so the climb yaw can steer it.
            model.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f) * Quaternion.Euler(0f, -270f, 0f);
            model.transform.localScale = Vector3.one * DigglesModelLibrary.ModelToWorldScale;
            var ctrl = model.AddComponent<DigglesAnimController>();   // bare/clip-less — facing only

            // Steer with the real executor: a short wall run with the service's outward normal.
            var n = new Vector3(0f, 0f, -1f);
            var run = new System.Collections.Generic.List<NavWaypoint>
            {
                new(Vector3.zero, true, n),
                new(new Vector3(0f, -1f, 0f), true, n),
            };
            var climb = new ClimbAction(ctrl, outer.transform, run, 0, -1, 0);
            for (int t = 0; t < 200; t++) climb.Tick(0.05);

            // Sanity: the climb yaw must have converged INTO the wall (−outward = world +Z).
            Vector3 fwd = model.transform.rotation * Vector3.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { Assert.Inconclusive("inner forward degenerated to vertical"); return; }
            Assert.Greater(Vector3.Dot(fwd.normalized, -n), 0.95f,
                "the climb facing must aim the model front INTO the wall (world +Z, back to camera) — " +
                "the roty=π wall pose; an air-facing (−Z) result is the bfdc9d0 belly-out regression");

            // With the 0.25-wu outward standoff the pose's front-mass centroid (≈ +0.19 wu along
            // the model front, probe tools/probe_kletter_pose.py) must NOT cross the FACE PLANE
            // (face z = root z + WallStandoff): the body grips the wall, it doesn't sink into it.
            var verts = clip.Frames[0].vertices;
            Vector3 c = Vector3.zero;
            foreach (var v in verts) c += model.transform.TransformPoint(v);
            c /= verts.Length;
            float rootZ = outer.transform.position.z;
            float faceZ = rootZ + 0.25f;   // ClimbAction.WallStandoff along outward (−Z)
            TestContext.WriteLine($"centroid world = {c}, root z = {rootZ:0.000}, face z = {faceZ:0.000} " +
                                  $"(centroid−face Δz = {c.z - faceZ:0.000})");
            Assert.Less(c.z, faceZ + 0.02f,
                "the kletter mass centroid must stay on the air side of the face plane — " +
                "beyond it the standoff no longer covers the pose bulge (climber buried)");
            Object.DestroyImmediate(outer);
        }

        // P5 verification (GNOME_BEHAVIOR_COMPLETE_RE §11 Phase 5): the faithful carve loop.
        // A FULL mark (cnt 0 → target 0) must keep accepting CarveStep until the node is dug out and
        // then SELF-CLEAR via the completion sweep (rim < target ⇒ nibble cleared, next step
        // refused); a SHALLOW mark (cnt 3 → target 11) must stop strictly earlier; the dug rect
        // must EFFPASSWALK into new walkable lines.
        [Test]
        public void CarveStep_DigTargets_StopAndSelfClear()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var terr = rootGo.GetComponent<DiggableTerrain>()!;

            // Two meadow anchors far apart (independent digs).
            (int sx, int sy) anchor = default;
            for (int sx = 238 * 8; sx < 252 * 8 && anchor == default; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav!.SubIsFloor(sx, sy)) { anchor = (sx, sy); break; }
            Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
            int ax = anchor.sx / 8, ay = anchor.sy / 8 + 1;
            int bx = ax + 6, by = ay;

            // The meadow bowl is GRANITE-CRUSTED (unq_start.pmp byte 4 → material type 0 at the
            // bowl rows — see DigPlayReproTests.FindDiggableSurfaceColumn): natively UNDIGGABLE,
            // and CarveStep now enforces the FUN_005976b0 material precondition. This test pins
            // the CARVE MECHANICS (rim gate, completion sweep, EFFPASSWALK), not the material
            // law — paint the dig sites EARTH so the carve is legal.
            for (int ty = ay - 2; ty <= ay + 2; ty++)
                for (int tx = ax - 2; tx <= bx + 2; tx++)
                    if ((uint)tx < (uint)terr.Width && (uint)ty < (uint)terr.Height)
                        terr.Terrain[ty * terr.Width + tx] = 2;   // earth

            // FULL dig at (ax,ay): owner 0, cnt 0 → target 0.
            terr.SetDigMark(ax, ay, 0, 0);
            Assert.IsTrue(terr.IsDigMarked(ax, ay, 0), "mark must read back");
            int fullSteps = 0;
            while (terr.CarveStep(ax, ay, 0, out _) && fullSteps < 64) fullSteps++;
            TestContext.WriteLine($"full dig: {fullSteps} steps, rim now {terr.RimAtNode(ax, ay)}");
            Assert.Greater(fullSteps, 1, "a full dig must take several carve steps (the slow faithful dig)");
            Assert.IsFalse(terr.IsDigMarked(ax, ay, 0), "the completion sweep must clear the mark");

            // SHALLOW dig at (bx,by): cnt 3 → target 11 — must stop strictly earlier.
            int rimBefore = terr.RimAtNode(bx, by);
            terr.SetDigMark(bx, by, 0, 3);
            int shallowSteps = 0;
            while (terr.CarveStep(bx, by, 0, out _) && shallowSteps < 64) shallowSteps++;
            int rimAfter = terr.RimAtNode(bx, by);
            TestContext.WriteLine($"shallow dig: {shallowSteps} steps, rim {rimBefore} → {rimAfter}");
            Assert.IsFalse(terr.IsDigMarked(bx, by, 0), "the shallow mark must self-clear at its target");
            Assert.Less(shallowSteps, fullSteps, "target 11 must stop strictly earlier than target 0");
            Assert.IsTrue(rimAfter < 11 || rimAfter == -1, "shallow dig is done once rim < 11");

            // The dug rect must open walkability (EFFPASSWALK over the dirty rect).
            nav!.RevalidateRect((ax - 2) * 8, (ay - 2) * 8, (ax + 2) * 8 + 7, (ay + 4) * 8 + 7);
            bool anyNewWalkable = false;
            for (int sy = (ay - 1) * 8; sy < (ay + 3) * 8 && !anyNewWalkable; sy++)
                for (int sx = (ax - 1) * 8; sx < (ax + 2) * 8; sx++)
                    if (sy > anchor.sy && nav.SubWalkable(sx, sy)) { anyNewWalkable = true; break; }
            Assert.IsTrue(anyNewWalkable, "the carved pit must gain walkable cells below the old line");
            Object.DestroyImmediate(rootGo);
        }

        // Regression D (Play strike #3, — "every dug block leaves a stone INSIDE the wall
        // behind the tunnel"): dig_execute spawns the resource drop AT $digpoint (z_dignwalk.tcl:365-369),
        // whose z = dig_next's clamp(rim+4+2, 1, 14) (FUN_00597b20:229-254) and whose tile is still
        // partly solid mid-dig. The ORIGINAL settles it via item physics (rohstoffe.tcl `set_physic
        // this 1`); the port settles ONCE at spawn (RuntimeWorldSpawner.SettleDropPosition): nearest
        // walkable sub-cell → fall the open band to the floor line → depth clamped into the open
        // band (rock = z < h). Pin that every dig-like spawn point lands NON-buried.
        [Test]
        public void SettleDropPosition_LandsDigDropsInOpenAir_OnWalkableCells()
        {
            var (rootGo, nav) = BuildStartMeadow();
            Assert.IsNotNull(nav);
            int checkedN = 0, settled = 0;
            for (int ty = 29; ty < 36; ty++)
                for (int tx = 238; tx < 256; tx++)
                {
                    if (nav!.GetHmapSub(tx * 8, ty * 8) != 15) continue;   // a solid (mid-dig) tile
                    checkedN++;
                    // The dig_next-shaped spawn: tile centre, z = 14 (the usual rim+6 clamp).
                    var p = RuntimeWorldSpawner.SettleDropPosition(nav!, new Vector3(tx, ty, 14f));
                    int sx = Mathf.RoundToInt(p.x * 8f), sy = Mathf.RoundToInt(p.y * 8f);
                    if ((nav.SubAt(sx, sy) & NavWorld.SubWalk) == 0)
                        continue;   // no nav within 3 tiles — settle keeps the raw pos (acceptable)
                    settled++;
                    // "Out of rock" = the OPEN pocket ABOVE the floor row (a floor row's OWN
                    // column IS the ground, h often 15 — gnomes stand the same way: body depth
                    // bounded by the row-above height, StampBands).
                    int h = nav.GetHmapSub(sx, sy - 1);
                    Assert.GreaterOrEqual(p.z, h + 0.5f - 1e-3f,
                        $"spawn ({tx},{ty}) settled to ({p.x},{p.y},{p.z}) — still buried (row-above h={h})");
                    Assert.LessOrEqual(p.z, 14.5f + 1e-3f, "depth must respect the 14.5 cap (DAT_00737564)");
                }
            TestContext.WriteLine($"solid spawn points checked: {checkedN}, settled onto walkable cells: {settled}");
            Assert.Greater(checkedN, 4, "too few solid tiles — widen the scan window");
            Assert.Greater(settled, checkedN / 2, "most dig drops near the meadow must find a walkable cell");
            Object.DestroyImmediate(rootGo);
        }

        // Symptom-2 regression guard (idle gnomes "fly to the far wall"): on a FLAT meadow line, the
        // LOS short-circuit MID point must sit at the GOAL's (x,y), so the depth change happens
        // GRADUALLY along the lateral walk — NOT in place at the start (which dived the gnome to the
        // goal depth before moving). FUN_0041d8a0:235 indexes the densified array with the GOAL col.
        [Test]
        public void FloorPathMap_LosMid_IsAtGoalNotStart()
        {
            var (rootGo, nav) = BuildStartMeadow();
            (int sx, int sy) a = default, b = default;
            for (int sy = 23 * 8; sy < 33 * 8 && b == default; sy++)
                for (int sx = 230 * 8; sx < 262 * 8; sx++)
                {
                    if (!nav!.SubIsFloor(sx, sy)) continue;
                    int end = sx; while (nav.SubIsFloor(end + 1, sy)) end++;
                    if (end - sx >= 80) { a = (sx + 4, sy); b = (end - 4, sy); }
                    sx = end;
                }
            Assert.AreNotEqual(default((int, int)), b, "no meadow line");
            var control = new System.Collections.Generic.List<Vector2>
                { new(a.sx * 0.125f, a.sy * 0.125f), new(b.sx * 0.125f, b.sy * 0.125f) };
            var start = new Vector3(a.sx * 0.125f, a.sy * 0.125f, 6f);   // body depth
            var goal = new Vector3(b.sx * 0.125f, b.sy * 0.125f, 7f);    // get_place depth
            var route = FloorPathMap.Build(nav!, control, start, goal, forceThreePoint: true, finalLeg: true);
            Assert.IsNotNull(route);
            Assert.AreEqual(3, route!.Count, "differing depths over an open line → 3-point LOS path");
            // The mid's (x,y) must be at the GOAL end, not the start — i.e. the first segment is
            // NOT a pure in-place depth dive.
            float midToGoalXY = Vector2.Distance(new Vector2(route[1].x, route[1].y), new Vector2(goal.x, goal.y));
            float midToStartXY = Vector2.Distance(new Vector2(route[1].x, route[1].y), new Vector2(start.x, start.y));
            TestContext.WriteLine($"route: {string.Join("  ", route)}  midToGoalXY={midToGoalXY:0.0} midToStartXY={midToStartXY:0.0}");
            Assert.Less(midToGoalXY, midToStartXY, "the LOS mid must lie at the GOAL end (gradual depth), not the start");
            Object.DestroyImmediate(rootGo);
        }

        // Regression guard for the urw_3 cutscene "walks on air" / "wrong rim position" bug.
        // The gnome AI vector_fixes every walk goal to the floor first (z_dignwalk.tcl:596,612,…),
        // but the cutscene `do_action walk` passes parse_pos coords RAW, and the engine's `action
        // walk` does NOT vector_fix internally (FUN_00420840 just stores the target). The original
        // therefore RELAXES an off-floor goal to its floor cell (Calc1 +y-biased relax) and only
        // seeds the flood DEPTH from the stored target (FUN_0041aac0:97-98) — it never plants the
        // body at the raw target's gravity-plane Y. The urw_3 step-back targets WiggleElfLook* are
        // Elf1Pos+{…} with the flying elf's Elf1Pos.y = Lagerplatz.y−1.5 ⇒ 1.5 world-units airborne.
        // So raising a walk target above the floor must NOT change where the gnome ends up standing.
        [Test]
        public void FindPath_AirborneTarget_LandsOnFloorNotInAir()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var svc = rootGo.AddComponent<NavWorldService>();
            var root = rootGo.transform;
            Vector3 W(Vector3 native) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native));

            // Two floor cells on one open meadow line (start + goal), ≥8 tiles apart.
            (int sx, int sy) a = default, b = default;
            for (int sy = 23 * 8; sy < 33 * 8 && b == default; sy++)
                for (int sx = 230 * 8; sx < 262 * 8; sx++)
                {
                    if (!nav!.SubIsFloor(sx, sy)) continue;
                    int end = sx; while (nav.SubIsFloor(end + 1, sy)) end++;
                    if (end - sx >= 64) { a = (sx + 4, sy); b = (end - 4, sy); }
                    sx = end;
                }
            Assert.AreNotEqual(default((int, int)), b, "no straight meadow line found");

            var startW = W(new Vector3(a.sx * 0.125f, a.sy * 0.125f, nav!.GetHmapSub(a.sx, a.sy)));
            var goalFloorW = W(new Vector3(b.sx * 0.125f, b.sy * 0.125f, nav.GetHmapSub(b.sx, b.sy)));
            // Mirror the cutscene: lift the SAME target 1.5 world-units straight up (world +Y =
            // −native.y), as the elf-relative WiggleElfLook* spots are. Depth (world Z) is unchanged.
            var goalAirW = goalFloorW + Vector3.up * 1.5f;

            var pFloor = svc.FindPath(startW, goalFloorW);
            var pAir = svc.FindPath(startW, goalAirW);
            Assert.IsNotNull(pFloor, "no path to the floor target");
            Assert.IsNotNull(pAir, "no path to the airborne target");

            float yFloor = pFloor![pFloor.Count - 1].Pos.y;
            float yAir = pAir![pAir.Count - 1].Pos.y;
            TestContext.WriteLine($"final stand world-Y: floor-target={yFloor:0.000}  " +
                $"airborne-target={yAir:0.000}  (target was lifted +1.5)");
            // The body must end at the SAME standing height regardless of the target's altitude —
            // it must NOT float up toward the raised target. (Pre-fix: yAir ≈ yFloor + 1.5.)
            Assert.Less(Mathf.Abs(yAir - yFloor), 0.3f,
                "walking to an airborne target must land ON the floor, not in mid-air");
            Object.DestroyImmediate(rootGo);
        }

        // ============================================================================================
        // DIAGNOSTIC: the gnome cannot WALK ALONG
        // the BOTTOM of a dug vertical tunnel. Reproduce headlessly for BOTH carve shapes (the deep
        // CarveTileNow shaft and the new post-fix SHALLOW per-tile CarveStep dig) and pin the root
        // cause at the data/pathfinding level. Adds NO runtime changes; diagnosis only.
        [Test]
        public void Diag_TunnelBottomWalkability()
        {
            RunTunnelDiag(shallow: false, widthTiles: 1);
            TestContext.WriteLine("\n\n========================= SHALLOW (per-tile CarveStep, target 11) =========================\n");
            RunTunnelDiag(shallow: true, widthTiles: 1);
            TestContext.WriteLine("\n\n========================= WIDE BOTTOM (2 tiles wide, deep) =========================\n");
            RunTunnelDiag(shallow: false, widthTiles: 2);
        }

        static void RunTunnelDiag(bool shallow, int widthTiles)
        {
            var (rootGo, nav) = BuildStartMeadow();
            var terr = rootGo.GetComponent<DiggableTerrain>()!;
            var svc = rootGo.AddComponent<NavWorldService>();
            var root = rootGo.transform;
            Vector3 W(int sx, int sy, float z) =>
                root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(new Vector3(sx * 0.125f, sy * 0.125f, z)));

            // Anchor the shaft on a meadow ground-line cell (the y=30.5 line).
            (int sx, int sy) anchor = default;
            for (int sx = 238 * 8; sx < 252 * 8 && anchor == default; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav!.SubIsFloor(sx, sy)) { anchor = (sx, sy); break; }
            Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
            int ptx = anchor.sx / 8, pty = anchor.sy / 8 + 1;   // rock tile under the line
            int nxMax = ptx + widthTiles;   // node range [ptx..ptx+widthTiles] = widthTiles tiles wide
            TestContext.WriteLine($"meadow anchor sub=({anchor.sx},{anchor.sy}) tile=({ptx},{pty}) — carving shaft nodes x in [{ptx}..{nxMax}], y {pty}..{pty + 3}, widthTiles={widthTiles}");

            if (!shallow)
            {
                // DEEP shaft: round-robin CarveTileNow until no change (the existing dug-shaft pattern).
                for (int round = 0; round < 64; round++)
                {
                    bool any = false;
                    for (int ny = pty; ny <= pty + 3; ny++)
                        for (int nx = ptx; nx <= nxMax; nx++)
                            any |= terr.CarveTileNow(nx, ny);
                    if (!any) break;
                }
            }
            else
            {
                // SHALLOW per-tile dig: the NEW post-fix player dig shape — dummy mark (cnt 3 → target 11)
                // + CarveStep loop per node going down, one rim step per node.
                for (int ny = pty; ny <= pty + 3; ny++)
                    for (int nx = ptx; nx <= nxMax; nx++)
                    {
                        terr.SetDigMark(nx, ny, 0, 3);   // dummy: target 11
                        int steps = 0;
                        while (terr.CarveStep(nx, ny, 0, out _) && steps++ < 64) { }
                    }
            }
            nav!.RevalidateRect((ptx - 2) * 8, (pty - 2) * 8, (nxMax + 3) * 8 + 7, (pty + 6) * 8 + 7);

            // -------- (3) CLASS map + GetHmapSub over the shaft + 2 tiles around incl. the bottom --------
            int tx0 = ptx - 2, tx1 = nxMax + 2, ty0 = pty - 2, ty1 = pty + 6;
            TestContext.WriteLine($"\n[CLASS] sub grid x[{tx0 * 8}..{(tx1 + 1) * 8}) y[{ty0 * 8}..{(ty1 + 1) * 8})  (F=value3 floor, W=value1 wall, .=0):");
            var head = new StringBuilder("       ");
            for (int sx = tx0 * 8; sx < (tx1 + 1) * 8; sx++) head.Append(sx % 8 == 0 ? '|' : ' ');
            TestContext.WriteLine(head.ToString());
            for (int sy = ty0 * 8; sy < (ty1 + 1) * 8; sy++)
            {
                var sb = new StringBuilder($"y{sy,4} {(sy % 8 == 0 ? "-" : " ")}");
                for (int sx = tx0 * 8; sx < (tx1 + 1) * 8; sx++)
                    sb.Append((nav.SubAt(sx, sy) & 3) switch { 3 => 'F', 1 => 'W', _ => '.' });
                TestContext.WriteLine(sb.ToString());
            }
            TestContext.WriteLine($"\n[HMAP] GetHmapSub for the same window:");
            for (int sy = ty0 * 8; sy < (ty1 + 1) * 8; sy++)
            {
                var sb = new StringBuilder($"y{sy,4} ");
                for (int sx = tx0 * 8; sx < (tx1 + 1) * 8; sx++)
                    sb.Append(nav.GetHmapSub(sx, sy).ToString("X1"));   // 0..F hex nibble
                TestContext.WriteLine(sb.ToString());
            }

            // -------- block-id map: are the shaft WALL tiles cliff-edge blocks (sloped geometry)
            // or still the sheer interior solid block? (DIGGING_MECHANIC §0: orig walls
            // become family 55-58/60/74 cliff blocks; our carve only re-resolves dug tiles.)
            TestContext.WriteLine($"\n[BLOCK IDX] tiles x[{tx0}..{tx1}] y[{ty0}..{ty1}] (blockId per tile):");
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var sb = new StringBuilder($"  tileY={ty,2}: ");
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    uint blk = (uint)(ty * terr.Width + tx) < (uint)terr.BlockIdx.Length ? terr.BlockIdx[ty * terr.Width + tx] : 9999;
                    sb.Append($"{tx}={blk,-5} ");
                }
                TestContext.WriteLine(sb.ToString());
            }

            // -------- collect bottom floor cells: value-3 cells in the lowest dug rows --------
            var floorCells = new System.Collections.Generic.List<(int sx, int sy)>();
            for (int sy = anchor.sy + 1; sy < (ty1 + 1) * 8; sy++)
                for (int sx = (ptx - 1) * 8; sx < (ptx + 2) * 8; sx++)
                    if (nav.SubIsFloor(sx, sy)) floorCells.Add((sx, sy));
            TestContext.WriteLine($"\n[BOTTOM FLOOR CELLS] {floorCells.Count} value-3 cells below the meadow line in the shaft columns:");
            // group by sub-y row
            var byRow = new System.Collections.Generic.SortedDictionary<int, System.Collections.Generic.List<int>>();
            foreach (var (sx, sy) in floorCells)
            {
                if (!byRow.TryGetValue(sy, out var l)) { l = new(); byRow[sy] = l; }
                l.Add(sx);
            }
            foreach (var kv in byRow)
                TestContext.WriteLine($"  row sy={kv.Key} (tileY {kv.Key / 8}): sx = [{string.Join(",", kv.Value)}]  h={nav.GetHmapSub(kv.Value[0], kv.Key)}");

            // -------- (7b) BAND-REJECTION TRACE: for the bottom-most OPEN row of the shaft (where a
            // floor line SHOULD form across the full dug width), why does DeriveSubCell reject the
            // interior cells? Find the deepest row with any h==0 (fully open) in the shaft columns,
            // then per sub-cell print: open-band pass? floor/wall verdict, with the failing band cell.
            int probeRow = -1;
            for (int sy = (ty1 + 1) * 8 - 1; sy >= anchor.sy; sy--)
            {
                for (int sx = ptx * 8; sx < (nxMax + 1) * 8; sx++)
                    if (nav.GetHmapSub(sx, sy) == 0 && nav.GetHmapSub(sx, sy + 1) >= 14) { probeRow = sy; break; }
                if (probeRow >= 0) break;
            }
            if (probeRow >= 0)
            {
                TestContext.WriteLine($"\n[BAND TRACE] bottom-most open-over-solid row sy={probeRow} (the row that should be a floor line across the shaft):");
                for (int sx = (ptx - 1) * 8; sx < (nxMax + 1) * 8 + 8; sx++)
                {
                    bool blocked = nav.TryGetEffPassWalkBlockingProbe(
                        sx,
                        probeRow,
                        out int badDx,
                        out int badDy,
                        out int badDetail);
                    byte derived = (byte)(nav.DeriveSubCell(sx, probeRow) & 3);
                    if (blocked)
                        TestContext.WriteLine(
                            $"  sx={sx} h={nav.GetHmapSub(sx, probeRow)} derived={derived}: " +
                            $"BAND REJECT at (dx {badDx},dy {badDy}) → " +
                            $"sub({sx + badDx},{probeRow + badDy}) detail={badDetail}>=14");
                    else
                        TestContext.WriteLine($"  sx={sx} h={nav.GetHmapSub(sx, probeRow)} derived={derived}: band OK (open)");
                }
            }

            // -------- (4) two bottom floor positions 4+ sub-cells apart on the same row --------
            (int sx, int sy) bA = default, bB = default;
            foreach (var kv in byRow)
            {
                var xs = kv.Value; xs.Sort();
                if (xs.Count >= 2 && xs[xs.Count - 1] - xs[0] >= 4) { bA = (xs[0], kv.Key); bB = (xs[xs.Count - 1], kv.Key); break; }
            }
            if (bA != default)
            {
                int zMidA = System.Math.Max(0, nav.GetHmapSub(bA.sx, bA.sy) - 1);
                int zMidB = System.Math.Max(0, nav.GetHmapSub(bB.sx, bB.sy) - 1);
                TestContext.WriteLine($"\n[ALONG-BOTTOM] FindPath ({bA.sx},{bA.sy},z{zMidA}) → ({bB.sx},{bB.sy},z{zMidB}):");
                var pAB = svc.FindPath(W(bA.sx, bA.sy, zMidA), W(bB.sx, bB.sy, zMidB));
                DumpPath(pAB, root, nav);
            }
            else TestContext.WriteLine("\n[ALONG-BOTTOM] NO two bottom floor cells 4+ apart on a single row — cannot test lateral walk.");

            // -------- (5) descent: a meadow rim floor cell → the bottom floor cell --------
            (int sx, int sy) rim = default;
            for (int sx = anchor.sx - 40; sx < anchor.sx - 8 && rim == default; sx++)
                for (int sy = anchor.sy - 8; sy <= anchor.sy + 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) { rim = (sx, sy); break; }
            (int sx, int sy) goalBottom = bA != default ? bA : (floorCells.Count > 0 ? floorCells[floorCells.Count - 1] : default);
            if (rim != default && goalBottom != default)
            {
                int zr = nav.GetHmapSub(rim.sx, rim.sy), zg = System.Math.Max(0, nav.GetHmapSub(goalBottom.sx, goalBottom.sy) - 1);
                TestContext.WriteLine($"\n[DESCENT] FindPath rim ({rim.sx},{rim.sy},z{zr}) → bottom ({goalBottom.sx},{goalBottom.sy},z{zg}):");
                var pD = svc.FindPath(W(rim.sx, rim.sy, zr), W(goalBottom.sx, goalBottom.sy, zg));
                DumpPath(pD, root, nav);
            }
            else TestContext.WriteLine("\n[DESCENT] no rim or no bottom floor cell to descend to.");

            // -------- (6) vector_fix-style snap of a click at the shaft-bottom mid-strip --------
            if (goalBottom != default)
            {
                int cx = goalBottom.sx, cy = goalBottom.sy;
                // replicate TrySnapToSub(SubWalk) nearest-walkable spiral
                (int sx, int sy, byte v) snap = SnapWalk(nav, cx, cy, 24);
                string kind = (snap.v & 3) == 3 ? "FLOOR(3)" : (snap.v & 3) == 1 ? "WALL(1)" : "none";
                TestContext.WriteLine($"\n[SNAP] click at bottom ({cx},{cy}) → nearest SubWalk = ({snap.sx},{snap.sy}) value={snap.v & 3} = {kind}");
                // Also snap a click that lands one row ABOVE the floor (the body anchor / mid-strip):
                (int sx, int sy, byte v) snap2 = SnapWalk(nav, cx, cy - 1, 24);
                string kind2 = (snap2.v & 3) == 3 ? "FLOOR(3)" : (snap2.v & 3) == 1 ? "WALL(1)" : "none";
                TestContext.WriteLine($"[SNAP] click one row up ({cx},{cy - 1}) → nearest SubWalk = ({snap2.sx},{snap2.sy}) value={snap2.v & 3} = {kind2}");
            }

            // -------- (7) drill into Calc1/FloorPathMap on the bottom run if a path failed --------
            TestContext.WriteLine($"\n[CALC1 GATING] bottom tiles:");
            for (int ty = pty; ty <= pty + 4; ty++)
            {
                var sb = new StringBuilder($"  tileY={ty}: ");
                for (int tx = ptx - 1; tx <= ptx + 2; tx++)
                    sb.Append($"({tx},{ty}) valid={(nav.TileValid(tx, ty) ? 1 : 0)} base={nav.TileBaseCost(tx, ty)} floor={(nav.TileHasFloorOrPassage(tx, ty) ? 1 : 0)}  ");
                TestContext.WriteLine(sb.ToString());
            }
            if (bA != default)
            {
                int zMidA = System.Math.Max(0, nav.GetHmapSub(bA.sx, bA.sy) - 1);
                int zMidB = System.Math.Max(0, nav.GetHmapSub(bB.sx, bB.sy) - 1);
                var control = new System.Collections.Generic.List<Vector2>
                    { new(bA.sx * 0.125f, bA.sy * 0.125f), new(bB.sx * 0.125f, bB.sy * 0.125f) };
                var startV = new Vector3(bA.sx * 0.125f, bA.sy * 0.125f, zMidA);
                var goalV = new Vector3(bB.sx * 0.125f, bB.sy * 0.125f, zMidB);
                var route = FloorPathMap.Build(nav, control, startV, goalV, forceThreePoint: true, finalLeg: true);
                TestContext.WriteLine($"\n[FLOORPATHMAP] bottom run Build = {(route == null ? "NULL (WS_NOPASSAGE)" : route.Count + " wps: " + string.Join("  ", route))}");
                // print the per-column legal band for the two endpoints
                foreach (var (lx, ly, lbl) in new[] { (bA.sx, bA.sy, "A"), (bB.sx, bB.sy, "B") })
                {
                    int floorH = nav.GetHmapSub(lx, ly - 1), ceilH = nav.GetHmapSub(lx, ly + 1);
                    TestContext.WriteLine($"  band[{lbl}] sub=({lx},{ly}): floorH=h(sy-1)={floorH} ceilH=h(sy+1)={ceilH} → rows [{floorH * 4 + 1}..{ceilH * 4 - 1}) {(ceilH * 4 - 1 <= floorH * 4 + 1 ? "EMPTY" : "ok")}");
                }
            }

            Object.DestroyImmediate(rootGo);
        }

        // =========================================================================================
        // DIAGNOSTIC: the two shaft gnomes (actors 3 & 4) "run to the shaft,
        // end up far IN FRONT of the terrain / hang in the air, face the wrong way, and already start
        // the run off the terrain." Reproduce the EXACT cutscene walk chain headlessly with the REAL
        // authored trigger position (Trigger_Urw_006 @ urw_unq_start.tcl:273 → native
        // (41.3353+208, 30.5, 10.82)) and the real pen offsets from urw_3.tcl. Beams set the body
        // directly (set_pos, no nav); each walk/run is ONE FindPath leg chained from the previous
        // landing, so the accumulation the player sees is reproduced. Dump where each leg lands +
        // on-terrain/depth/facing. Diagnosis only — no asserts that can fail the suite.
        [Test]
        public void Diag_Urw3_ShaftWalkChain()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var svc = rootGo.AddComponent<NavWorldService>();
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
            Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

            var L = new Vector3(249.3353f, 30.5f, 10.82f);   // Lagerplatz = Trigger_Urw_006 native pos
            Vector3 D(float dx, float dy, float dz) => L + new Vector3(dx, dy, dz);
            TestContext.WriteLine($"Lagerplatz(TriggerPos) native = {L}");

            // ---- terrain around the shaft target (x 252..262, y 27..33): is there floor past the
            // meadow east edge (~x256), and at what depth? ----
            TestContext.WriteLine("\n[SHAFT TERRAIN] class (F=floor3 W=wall1 .=0)   |   hmap nibble, sub x[2016..2096) y[216..264):");
            for (int sy = 27 * 8; sy < 33 * 8; sy++)
            {
                var cls = new StringBuilder($"y{sy,4} ");
                var hm = new StringBuilder("h ");
                for (int sx = 252 * 8; sx < 262 * 8; sx++)
                {
                    cls.Append((nav!.SubAt(sx, sy) & 3) switch { 3 => 'F', 1 => 'W', _ => '.' });
                    hm.Append(nav.GetHmapSub(sx, sy).ToString("X1"));
                }
                TestContext.WriteLine(cls.ToString() + "   " + hm.ToString());
            }

            // on-terrain report for a native landing pos: nearest floor cell + lateral & depth deltas.
            void Report(string label, Vector3 nLand, Vector3? faceFrom)
            {
                int sx0 = Mathf.RoundToInt(nLand.x * 8f), sy0 = Mathf.RoundToInt(nLand.y * 8f);
                (int sx, int sy) best = default; float bestD = float.MaxValue;
                for (int r = 0; r <= 32 && best == default; r++)
                    for (int dy = -r; dy <= r; dy++)
                        for (int dx = -r; dx <= r; dx++)
                        {
                            if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                            int cx = sx0 + dx, cy = sy0 + dy;
                            if (nav!.SubIsFloor(cx, cy)) { float d = dx * dx + dy * dy; if (d < bestD) { bestD = d; best = (cx, cy); } }
                        }
                string onT;
                if (best == default) onT = "NO floor within 32 sub-cells (OFF-TERRAIN laterally)";
                else
                {
                    float lat = Mathf.Sqrt(bestD) / 8f;
                    int supp = NavWorldService.SupportDepth(nav!, best.sx, best.sy);
                    float dz = nLand.z - supp;
                    string verdict = dz < -0.75f ? "IN FRONT of terrain" : dz > 0.75f ? "BEHIND terrain" : "~on surface";
                    onT = $"nearest floor=({best.sx},{best.sy}) latDist={lat:0.00}t supportDepth={supp} actorDepth={nLand.z:0.0} Δnibble={dz:+0.0;-0.0} [{verdict}]";
                }
                string face = faceFrom.HasValue
                    ? $"  faceDirNative=({nLand.x - faceFrom.Value.x:+0.0;-0.0},{nLand.y - faceFrom.Value.y:+0.0;-0.0},{nLand.z - faceFrom.Value.z:+0.0;-0.0})"
                    : "";
                TestContext.WriteLine($"  [{label}] LAND native=({nLand.x:0.0},{nLand.y:0.0},{nLand.z:0.0}) → {onT}{face}");
            }

            void RunChain(string who, (string lbl, Vector3 tgt, bool beam)[] legs)
            {
                TestContext.WriteLine($"\n===== {who} =====");
                Vector3 cur = default;
                foreach (var (lbl, tgt, beam) in legs)
                {
                    if (beam) { cur = W(tgt); Report($"{lbl} BEAM", N(cur), null); continue; }
                    var path = svc.FindPath(cur, W(tgt));
                    if (path == null || path.Count == 0)
                    { TestContext.WriteLine($"  [{lbl}] FindPath → NULL (no path) target native=({tgt.x:0.0},{tgt.y:0.0},{tgt.z:0.0})"); continue; }
                    Vector3 prev = N(path[path.Count >= 2 ? path.Count - 2 : 0].Pos);
                    cur = path[path.Count - 1].Pos;
                    TestContext.WriteLine($"  [{lbl}] target native=({tgt.x:0.0},{tgt.y:0.0},{tgt.z:0.0}):");
                    DumpPath(path, root, nav!);
                    Report(lbl, N(cur), prev);
                }
            }

            var elf1 = D(5f, -1.5f, 9f);
            RunChain("ACTOR 3 (run to shaft, look down, step back to elf)", new[]
            {
                ("beam Start3",   D(-8f, 0f, 4f),               true),
                ("walk Mitte3",   D(0f, 0f, 4f),                false),
                ("walk Schacht1", D(9.5f, 0f, 3.5f),            false),
                ("run  Schacht1", D(9.5f, 0f, 3.5f),            false),
                ("walk ElfLook4", elf1 + new Vector3(1.2f, 0f, -3f), false),
            });
            RunChain("ACTOR 4 (run to shaft, look down, step back to elf)", new[]
            {
                ("beam Start5",   D(-6f, 0f, 4f),               true),
                ("walk Mitte5",   D(2f, 0f, 4f),                false),
                ("walk Ende5",    D(1.5f, 0f, 8f),              false),
                ("run  Schacht2", D(9.5f, 0f, 8f),              false),
                ("walk ElfLook5", elf1 + new Vector3(2.9f, 0f, -4f), false),
            });

            Object.DestroyImmediate(rootGo);
        }

        // REGRESSION GUARD:
        // the two gnomes that run to the shaft (urw_3.tcl:183-184 `do_action run WiggleSchachtNPos`).
        // WiggleSchacht1Pos = Lagerplatz+{9.5,0,3.5}, WiggleSchacht2Pos = Lagerplatz+{9.5,0,8} share
        // the SAME (x,y) and differ ONLY in DEPTH (z 12.32 vs 16.82 — the third pen coord is depth
        // into the scene). The original separates the pair by depth (perspective); the two must NOT
        // land at the same 3D point. The standing-seat cap collapsed them (it pulled the deeper
        // goal 15.75→h+1≈12 onto the shallow one's 12.25 — it could not tell an authored deep depth
        // from get_hmap garbage). With the cap removed the faithful band-snap (FUN_0041d8a0 emit +
        // StampBands) keeps the deep goal near the band max (~14.5) while the shallow stays ~12.25.
        // Fail-first evidence: WITH the cap the two landed z 12.25 vs 12.00 (Δ0.25 → stacked).
        // ⚠ Lagerplatz = TriggerPos + {-3,0,-2} (sq_pen set/move, urw_3.tcl:7-8) — dropping the move
        // (the old Diag_Urw3_ShaftWalkChain bug) overshoots the shaft into solid ground → false NULLs.
        [Test]
        public void Urw3_TwoShaftGnomes_SeparateByDepth_NotStacked()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var svc = rootGo.AddComponent<NavWorldService>();
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
            Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

            var L = new Vector3(249.3353f, 30.5f, 10.82f) + new Vector3(-3f, 0f, -2f);   // = (246.34, 30.5, 8.82)
            Vector3 D(float dx, float dy, float dz) => L + new Vector3(dx, dy, dz);
            TestContext.WriteLine($"Lagerplatz native = {L}");

            // Both shaft goals from a COMMON start (Mitte3), isolating the DEPTH law from the
            // approach: the only difference between the two is the goal's depth coordinate.
            Vector3 Land(string who, Vector3 start, Vector3 goal)
            {
                var path = svc.FindPath(W(start), W(goal));
                Assert.IsTrue(path != null && path.Count > 0,
                    $"{who}: FindPath NULL — the shaft-rim goal must be reachable");
                Vector3 land = N(path![path.Count - 1].Pos);
                TestContext.WriteLine($"{who}: goal=({goal.x:0.00},{goal.y:0.00},{goal.z:0.00}) " +
                    $"→ LAND=({land.x:0.00},{land.y:0.00},{land.z:0.00})");
                return land;
            }
            var start = D(0f, 0f, 4f);
            var s1 = Land("Schacht1 z=12.32", start, D(9.5f, 0f, 3.5f));
            var s2 = Land("Schacht2 z=16.82", start, D(9.5f, 0f, 8f));

            // Same (x,y) landing column (both relax to the shaft rim) …
            Assert.AreEqual(Mathf.RoundToInt(s1.x * 8f), Mathf.RoundToInt(s2.x * 8f),
                "both shaft goals share (x,y) — they relax to the same rim column");
            Assert.AreEqual(Mathf.RoundToInt(s1.y * 8f), Mathf.RoundToInt(s2.y * 8f));
            // … so the ONLY thing that keeps the two gnomes apart is DEPTH. The seat-cap collapse
            // left Δz = 0.25; the faithful band-snap gives Δz ≈ 2.25. Guard the separation.
            float dz = Mathf.Abs(s1.z - s2.z);
            TestContext.WriteLine($"depth separation Δz = {dz:0.00} (stacked ≈ 0.25, faithful ≈ 2.25)");
            Assert.Greater(dz, 1.0f,
                "the two shaft gnomes collapsed onto one depth — the standing-seat cap is pulling an " +
                "authored deep cutscene depth down to the shallow one (playtest 07-14 stacking)");

            Object.DestroyImmediate(rootGo);
        }

        // =========================================================================================
        // Post-cutscene work-walk repro (the urw_3 assignment storm): after the intro the five
        // gnomes stand at the WiggleKicher* pens — urw_3.tcl:78-90 defines DropPos = Lagerplatz +
        // {0.5,-0.5,1} and WiggleKicher1..5 = DropPos + {2,.5,0} {0,.5,-3.4} {-.5,.5,-3.4}
        // {1,.5,4} {-2,.5,2}; the final beams (urw_3.tcl:560-564) park everyone there. Lagerplatz
        // = Trigger_Urw_006 pos (41.3353+208, 30.5, 10.82) (urw_unq_start.tcl:273) moved
        // {-3,0,-2} (urw_3.tcl:7-8) → native (246.3353, 30.5, 8.82). The Play storm log showed
        // EVERY work walk from these spots returning FindPath NULL → walkfail → instant
        // reassign (~7 Hz/gnome). This pins the pathfinder half: routes from the EXACT pens (and
        // from 1.0 native-y above — live-dump body anchors ride up to ~1.0 above the ground
        // line) to (a) near floor targets on the meadow line and (b) a dig-edge target at the
        // pre-dug shaft must EXIST. If these pass, the Play NULL comes from upstream of
        // navigation (a garbage start transform / coordinate-space mismatch after the cutscene),
        // not from Calc1/FloorPathMap.
        [Test]
        public void FindPath_PostCutsceneCampfirePens_WorkWalksHavePaths()
        {
            var (rootGo, nav) = BuildStartMeadow();
            var svc = rootGo.AddComponent<NavWorldService>();
            var root = rootGo.transform;
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));

            var lager = new Vector3(41.3353f + 208f, 30.5f, 10.82f) + new Vector3(-3f, 0f, -2f);
            var drop = lager + new Vector3(0.5f, -0.5f, 1f);
            var pens = new (string name, Vector3 pos)[]
            {
                ("Kicher1", drop + new Vector3(2f, 0.5f, 0f)),
                ("Kicher2", drop + new Vector3(0f, 0.5f, -3.4f)),
                ("Kicher3", drop + new Vector3(-0.5f, 0.5f, -3.4f)),
                ("Kicher4", drop + new Vector3(1f, 0.5f, 4f)),
                ("Kicher5", drop + new Vector3(-2f, 0.5f, 2f)),
            };

            // (b) the dig-edge target: the pre-dug shaft mouth (WiggleSchacht1Pos = Lagerplatz +
            // {9.5,0,3.5}, urw_3.tcl:48-49). The dig walk goal in z_dignwalk is get_digedge's
            // nearest FLOOR cell to the dig point — emulate with the walkable-snap.
            var schacht = lager + new Vector3(9.5f, 0f, 3.5f);
            var edge = SnapWalk(nav!, Mathf.RoundToInt(schacht.x * 8f), Mathf.RoundToInt(schacht.y * 8f), 32);
            Assert.AreNotEqual(0, edge.v & NavWorld.SubWalk, "no walkable cell near the shaft mouth at all");
            int edgeZ = Mathf.Max(0, nav!.GetHmapSub(edge.sx, edge.sy) - 1);
            var edgeTarget = new Vector3(edge.sx * 0.125f, edge.sy * 0.125f, edgeZ);
            TestContext.WriteLine($"shaft pen=({schacht.x:0.00},{schacht.y:0.00}) → dig-edge cell " +
                $"({edge.sx},{edge.sy}) val={edge.v & 3} z={edgeZ}");

            int failures = 0;
            foreach (var (name, pen) in pens)
            {
                // (a) a near floor target ~2-3 tiles to the side of the pen, on the meadow line.
                (int sx, int sy) tgt = default;
                int psx = Mathf.RoundToInt(pen.x * 8f);
                for (int dx = 16; dx <= 32 && tgt == default; dx++)
                    for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    {
                        if (nav.SubIsFloor(psx + dx, sy)) { tgt = (psx + dx, sy); break; }
                        if (nav.SubIsFloor(psx - dx, sy)) { tgt = (psx - dx, sy); break; }
                    }
                Assert.AreNotEqual(default((int, int)), tgt, $"{name}: no meadow floor cell within ±4 tiles");
                int tz = Mathf.Max(0, nav.GetHmapSub(tgt.sx, tgt.sy) - 1);
                var floorTarget = new Vector3(tgt.sx * 0.125f, tgt.sy * 0.125f, tz);

                foreach (var (variant, start) in new[]
                {
                    ("pen", pen),
                    ("pen+1.0up", pen + new Vector3(0f, -1f, 0f)),   // body anchor above the line (native −y = up)
                })
                {
                    var pNear = svc.FindPath(W(start), W(floorTarget));
                    var pEdge = svc.FindPath(W(start), W(edgeTarget));
                    TestContext.WriteLine($"[{name} {variant}] start=({start.x:0.00},{start.y:0.00},{start.z:0.00}) " +
                        $"→ floor({floorTarget.x:0.00},{floorTarget.y:0.00},{floorTarget.z:0.0}): " +
                        $"{(pNear == null ? "NULL" : pNear.Count + "wp")}   " +
                        $"→ digEdge({edgeTarget.x:0.00},{edgeTarget.y:0.00},{edgeTarget.z:0.0}): " +
                        $"{(pEdge == null ? "NULL" : pEdge.Count + "wp")}");
                    if (pNear == null) { failures++; DiagnoseEndpoint(nav, "start", start); DiagnoseEndpoint(nav, "floorTarget", floorTarget); }
                    if (pEdge == null) { failures++; DiagnoseEndpoint(nav, "start", start); DiagnoseEndpoint(nav, "edgeTarget", edgeTarget); }
                }
            }

            if (failures > 0)
            {
                // Camp-window class map for the post-mortem.
                TestContext.WriteLine("\n[CLASS] camp window x242..258 y27..34 (F=floor3 W=wall1 .=0):");
                for (int sy = 27 * 8; sy < 34 * 8; sy++)
                {
                    var sb = new StringBuilder($"y{sy,4} ");
                    for (int sx = 242 * 8; sx < 258 * 8; sx++)
                        sb.Append((nav.SubAt(sx, sy) & 3) switch { 3 => 'F', 1 => 'W', _ => '.' });
                    TestContext.WriteLine(sb.ToString());
                }
            }
            Assert.AreEqual(0, failures,
                "post-cutscene campfire work walks must have paths (storm repro — see the diagnostics above)");
            Object.DestroyImmediate(rootGo);
        }

        // Replicate Calc1's endpoint handling step by step for a NULL post-mortem: the ≥40-inside
        // margin (FUN_0059d640:102-105), raw walkability, the 4-axis start relax r=1..32
        // (:113-148) and the 8-probe goal fan r=1..12 (:152-239), and the landing tile's validity.
        static void DiagnoseEndpoint(NavWorld nav, string label, Vector3 nativePos)
        {
            int sx = Mathf.RoundToInt(nativePos.x * 8f), sy = Mathf.RoundToInt(nativePos.y * 8f);
            bool inMargin = sx >= 40 && sy >= 40 && sx < nav.SubW - 40 && sy < nav.SubH - 40;
            bool Walk(int x, int y) => (uint)x < (uint)nav.SubW && (uint)y < (uint)nav.SubH
                && nav.TileBaseCost(x >> 3, y >> 3) != -1 && (nav.SubAt(x, y) & NavWorld.SubWalk) != 0;
            string relax = "n/a";
            if (!Walk(sx, sy))
            {
                relax = "MISS (start stays put → tile-validity reject)";
                for (int r = 1; r <= 32; r++)
                {
                    if (Walk(sx, sy + r)) { relax = $"+y r={r} → ({sx},{sy + r})"; break; }
                    if (Walk(sx + r, sy)) { relax = $"+x r={r} → ({sx + r},{sy})"; break; }
                    if (Walk(sx - r, sy)) { relax = $"-x r={r} → ({sx - r},{sy})"; break; }
                    if (Walk(sx, sy - r)) { relax = $"-y r={r} → ({sx},{sy - r})"; break; }
                }
            }
            TestContext.WriteLine($"    [diag {label}] native=({nativePos.x:0.00},{nativePos.y:0.00},{nativePos.z:0.00}) " +
                $"sub=({sx},{sy}) inMargin={inMargin} cellVal={nav.SubAt(sx, sy) & 3} " +
                $"tileBase={nav.TileBaseCost(sx >> 3, sy >> 3)} tileFloor={nav.TileHasFloorOrPassage(sx >> 3, sy >> 3)} " +
                $"h={nav.GetHmapSub(sx, sy)} hA={nav.GetHmapSub(sx, sy - 1)} hB={nav.GetHmapSub(sx, sy + 1)} " +
                $"startRelax={relax}");
        }

        static (int sx, int sy, byte v) SnapWalk(NavWorld nav, int sx, int sy, int maxR)
        {
            if ((nav.SubAt(sx, sy) & NavWorld.SubWalk) != 0) return (sx, sy, nav.SubAt(sx, sy));
            for (int r = 1; r <= maxR; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r) continue;
                        int cx = sx + dx, cy = sy + dy;
                        if ((nav.SubAt(cx, cy) & NavWorld.SubWalk) != 0) return (cx, cy, nav.SubAt(cx, cy));
                    }
            return (sx, sy, 0);
        }

        static void DumpPath(System.Collections.Generic.List<NavWaypoint>? path, Transform root, NavWorld nav)
        {
            if (path == null) { TestContext.WriteLine("  -> NULL (no path)"); return; }
            int walls = 0;
            foreach (var wp in path)
            {
                Vector3 n = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(wp.Pos));
                int sx = Mathf.RoundToInt(n.x * 8f), sy = Mathf.RoundToInt(n.y * 8f);
                byte v = (byte)(nav.SubAt(sx, sy) & 3);
                if (wp.IsWall) walls++;
                TestContext.WriteLine($"    {(wp.IsWall ? "WALL " : "floor")} native=({n.x:0.00},{n.y:0.00},{n.z:0.00}) sub=({sx},{sy}) cellVal={v}");
            }
            TestContext.WriteLine($"  -> {path.Count} wps, {walls} wall");
        }

        // Air-side half-space guard (wall-embedding audit §2/§5): a column of height
        // h holds ROCK at native z < h and AIR at z ≥ h, so in world (Z = −(z·0.5+7)) the rock
        // is at world Z GREATER than the face plane. Every climbing body position must sit at or
        // beyond its nearest wall record's face plane along world −Z (the record rides ON the
        // face; the executor adds the ≤0.25 wu standoff further INTO the air).
        static void AssertAirSide(Vector3 pos, System.Collections.Generic.List<NavWaypoint> wallWps, string ctx)
        {
            NavWaypoint best = wallWps[0]; float bestD = float.MaxValue;
            foreach (var w in wallWps)
            {
                float dx = w.Pos.x - pos.x, dy = w.Pos.y - pos.y;   // lateral + height (world X/Y)
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = w; }
            }
            Assert.LessOrEqual(pos.z, best.Pos.z + 0.02f,
                $"{ctx}: body at {pos} is on the ROCK side of the face plane z={best.Pos.z:0.000} (record {best.Pos})");
        }

        // Walk approach/exit anchor (FUN_00415f40:24-57 → +0x44 → FUN_0041a2f0 → +0x30, read by
        // FUN_0041aac0:97-99 on the run's final leg): a floor run that exits into a WALL stores
        // exit depth = min(15.0, min(h(wall1), h(wall2)) + 1.0) — h read straight at the first
        // two wall cells. That is half a world unit on the AIR side of the face: the walker
        // waits at the rim NEXT to the face, never inside the rock (the pre-fix SupportDepth
        // anchor put the exit ON the face plane = torso clipping). Allow ±0.3 native for the
        // FloorPathMap band snap (one grid row = 0.25).
        static void AssertApproachAnchor(System.Collections.Generic.List<NavWaypoint> path,
            Transform root, NavWorld nav)
        {
            int firstWall = path.FindIndex(w => w.IsWall);
            Assert.Greater(firstWall, 0, "expected a floor approach before the climb");
            Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));
            Vector3 exitN = N(path[firstWall - 1].Pos);
            Vector3 w1 = N(path[firstWall].Pos);
            Vector3 w2 = firstWall + 1 < path.Count && path[firstWall + 1].IsWall
                ? N(path[firstWall + 1].Pos) : w1;
            int h1 = nav.GetHmapSub(Mathf.RoundToInt(w1.x * 8f), Mathf.RoundToInt(w1.y * 8f));
            int h2 = nav.GetHmapSub(Mathf.RoundToInt(w2.x * 8f), Mathf.RoundToInt(w2.y * 8f));
            float want = Mathf.Min(15f, Mathf.Min(h1, h2) + 1f);
            TestContext.WriteLine($"approach anchor: exit z={exitN.z:0.00}, face record z={w1.z:0.00}, " +
                $"h(wall1)={h1} h(wall2)={h2} → want {want:0.00}");
            Assert.AreEqual(want, exitN.z, 0.3f,
                "the floor-run exit depth must follow the FUN_00415f40 wall-boundary rule (minH + 1, cap 15)");
            Assert.GreaterOrEqual(exitN.z, Mathf.Min(h1, h2) + 0.2f,
                "the exit anchor must sit STRICTLY on the air side of the face (deeper nibble than the face h)");
        }

        // First +0x3c value-3 ground-line cell of the start meadow (the y=30.5 line).
        static (int sx, int sy) FindMeadowAnchor(NavWorld nav)
        {
            for (int sx = 238 * 8; sx < 252 * 8; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        // A meadow floor cell a few tiles to the LEFT of nearSx (the descent start).
        static (int sx, int sy) FindRim(NavWorld nav, int nearSx, int anchorSy)
        {
            for (int sx = nearSx - 40; sx < nearSx - 8; sx++)
                for (int sy = anchorSy - 8; sy <= anchorSy + 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        // The deep CarveTileNow shaft + the P1 revalidation contract (EFFPASSWALK → EFFBLOCK),
        // shared by the climb tests. nodesWide/nodesDeep are INCLUSIVE extents past (ptx,pty).
        static void DigShaft(DiggableTerrain terr, NavWorld nav, int ptx, int pty, int nodesWide, int nodesDeep)
        {
            for (int round = 0; round < 64; round++)
            {
                bool any = false;
                for (int ny = pty; ny <= pty + nodesDeep; ny++)
                    for (int nx = ptx; nx <= ptx + nodesWide; nx++)
                        any |= terr.CarveTileNow(nx, ny);
                if (!any) break;
            }
            nav.RevalidateRect((ptx - 2) * 8, (pty - 2) * 8,
                (ptx + nodesWide + 2) * 8 + 7, (pty + nodesDeep + 2) * 8 + 7);
        }

        // Mirror of NavWorldService's goal settle (≤16 sub-cells down a contiguous walk band to
        // the first FLOOR-bit cell): true = a wall goal here would be settled onto a floor.
        static bool GoalSettleFires(NavWorld nav, int gx, int gy)
        {
            for (int lift = 1; lift <= 16; lift++)
            {
                byte below = nav.SubAt(gx, gy + lift);
                if ((below & NavWorld.SubFloor) != 0) return true;
                if ((below & NavWorld.SubWalk) == 0) return false;
            }
            return false;
        }

        static (GameObject root, NavWorld? nav) BuildStartMeadow()
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
            var nav = rootGo.GetComponent<DiggableTerrain>()?.Nav;
            Assert.IsNotNull(nav, "no NavWorld");
            return (rootGo, nav);
        }

        static void DumpTile(NavWorld nav, DiggableTerrain terr, int tx, int ty)
        {
            uint blk = (uint)(ty * terr.Width + tx) < (uint)terr.BlockIdx.Length ? terr.BlockIdx[ty * terr.Width + tx] : 9999;
            var sb = new StringBuilder($"\n[TILE {tx},{ty}] block={blk}  8x8 GetHmapSub:\n");
            for (int sy = 0; sy < 8; sy++)
            {
                sb.Append("   ");
                for (int sx = 0; sx < 8; sx++)
                    sb.Append(nav.GetHmapSub(tx * 8 + sx, ty * 8 + sy).ToString().PadLeft(3));
                sb.Append('\n');
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
