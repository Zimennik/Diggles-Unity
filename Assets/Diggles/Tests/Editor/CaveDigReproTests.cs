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

    // Cave dig cluster repros:
    //
    // C1 (a gnome dug into a cave and got stuck, the cave left unfinished): drive the
    // cave-stamp dig in the port's own dig_next serving order and assert, after every carve,
    // that the digger's standoff remains pathable back to the tunnel he came from — and that
    // the cave order completes (all marks cleared).
    //
    // C2 (only the ordered gnome digs the cave): cave-stamp marks alone (no evt_task_dig) must
    // funnel through the dispatcher's dig-request scan (FUN_0043bdd0 → kind-5 job,
    // FUN_00435f50) and dispatch an idle own gnome via evt_autoprod_dig — exactly like the
    // tunnel marquee marks already do (DigPlayReproTests S2).
    //
    // Site: the start meadow's earth chimney (the only native-diggable surface column) → a
    // marquee shaft down → a horizontal tunnel at the bottom → the cave0 (Tiny) stamp with its
    // mouth rows riding the tunnel rows (cave anchor lattice y=4m+3 ⇒ mouth rows {4m+1, 4m+2} =
    // exactly a horizontal marquee tunnel's 2 rows).
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class CaveDigReproTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        List<(int tx, int ty)> _lastTunnel = new();

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

        // Dig the chimney shaft + a bottom tunnel; return the cave anchor whose mouth rows ride
        // the tunnel rows, plus a floor cell inside the tunnel near the future cave mouth.
        static (int axC, int ayC, CaveTemplate tpl, List<(int, int)> tunnelMarks)
            PrepareCaveSite(DigglesEngine engine, DiggableTerrain terr, NavWorld nav)
        {
            var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
            Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
            var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

            // Vertical shaft: 12 tiles down (rows ay..ay+13; ay = 4k+1 ⇒ ay+12 = 4m+1).
            var (sbx, sby) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 12f);
            var shaft = CommitMarquee(terr, ax, ay, sbx, sby);
            Assert.Greater(shaft.Count, 3, "shaft marquee painted too few");
            TestContext.WriteLine($"shaft carved in {CarveOut(terr, shaft)} steps");

            // Horizontal tunnel from the shaft bottom to the east: anchor (ax, ay+12) drag +12.
            int ayH = ay + 12;
            var (tbx, tby) = DigMarquee.SnapDragCorner(ax, ayH, ax + 12f, ayH);
            var tunnel = CommitMarquee(terr, ax, ayH, tbx, tby);
            Assert.Greater(tunnel.Count, 3, "tunnel marquee painted too few");
            TestContext.WriteLine($"tunnel carved in {CarveOut(terr, tunnel)} steps");

            var tpl = CaveTemplates.Load(DataRoot)[0];
            Assert.IsNotNull(tpl, "cave0.tga failed to load");

            // Cave anchor: mouth rows {ayH, ayH+1}; scan even anchor x over the tunnel span for
            // the first valid stamp site; dump per-cell results of the last try otherwise.
            int ayC = ayH + 2;
            var verdicts = new List<(int tx, int ty, bool valid)>();
            for (int axC = ax + 2; axC <= ax + 24; axC += 2)
            {
                verdicts.Clear();
                if (CaveStamp.Validate(tpl!, axC, ayC, terr, 0, verdicts))
                {
                    TestContext.WriteLine($"cave site: anchor ({axC},{ayC})");
                    return (axC, ayC, tpl!, tunnel);
                }
            }
            var bad = new StringBuilder("cave0 never validated; last verdicts: ");
            foreach (var v in verdicts) if (!v.valid) bad.Append($"({v.tx},{v.ty}) ");
            Assert.Fail(bad.ToString());
            return default;
        }

        // -----------------------------------------------------------------------------------
        // C1 — data-layer cave dig in dig_next order: digger stays pathable, order completes.
        // -----------------------------------------------------------------------------------
        [Test]
        public void CaveStampDig_DigNextOrder_DiggerStaysPathable_AndCompletes()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var (axC, ayC, tpl, tunnelMarks) = PrepareCaveSite(engine, terr, nav);
                _lastTunnel = tunnelMarks;
                int painted = CaveStamp.Commit(tpl, axC, ayC, terr, 0, out int mouthX, out int mouthY);
                Assert.Greater(painted, 0, "cave stamp did not commit");
                TestContext.WriteLine($"cave0 committed: {painted} cells, seed ({mouthX},{mouthY})");

                // The digger starts on the tunnel floor at the mouth; the retreat target is the
                // shaft-bottom end of the tunnel (where he walked in from).
                var start = FindFloorNear(nav, mouthX * 8, (mouthY + 1) * 8, 16);
                Assert.AreNotEqual(default((int, int)), start, "no tunnel floor at the cave mouth");
                int tun0 = int.MaxValue;                       // west end of the tunnel (shaft side)
                foreach (var (mtx, _) in _lastTunnel) if (mtx < tun0) tun0 = mtx;
                var home = FindFloorNear(nav, (tun0 + 1) * 8, (mouthY + 1) * 8, 16);
                Assert.AreNotEqual(default((int, int)), home, "no tunnel floor at the shaft end");
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                var interp = engine.Interp;
                float gx = start.sx * 0.125f, gy = start.sy * 0.125f;
                float gz = Mathf.Min(nav.GetHmapSub(start.sx, start.sy) + 1, 14.5f);
                Vector3 homeN = new(home.sx * 0.125f, home.sy * 0.125f,
                                    Mathf.Min(nav.GetHmapSub(home.sx, home.sy) + 1, 14.5f));

                // The faithful z_dignwalk.tcl dig loop, walk legs teleported:
                // dig_continue (:203-290): digpoint = dig_next(pos, try) + {0 0 -1}; end when
                // unpacky<=0 or try>=9; dig_check gate else try+1; walkpos = vector_fixdig;
                // walk when dist3d > nonewwalk 0.5; dig_execute (:295-315): swing while
                // dist(digpoint, pos+{0,-0.8}) < 3.0 (+digattr·2, fresh=0) — dig_apply.
                // Task end with marks remaining = work_idle → the ambient dispatcher re-serves
                // the area (emulated as a restart) while progress is still being made.
                bool DigCheck(float cvx, float cvy, float cdz)
                {
                    string w = interp.Eval($"get_angle_num4 {{{Inv(cvx)} {Inv(cvy)} {Inv(cdz)}}} " +
                        $"{{{Inv(gx)} {Inv(gy - 0.8f)} {Inv(gz)}}}").Trim();
                    float[] xl, yl;
                    switch (w)
                    {
                        case "0": xl = new[] { 0f, -1f, 1f }; yl = new[] { -1f, -1f, -1f }; break;
                        case "1": xl = new[] { 1f, 1f, 1f }; yl = new[] { 0f, -1f, 1f }; break;
                        case "2": xl = new[] { 0f, -1f, 1f }; yl = new[] { 1f, 1f, 1f }; break;
                        default:  xl = new[] { -1f, -1f, -1f }; yl = new[] { 0f, -1f, 1f }; break;
                    }
                    int free = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        string h = interp.Eval($"get_hmap {Inv(cvx + xl[k])} {Inv(cvy + yl[k])}").Trim();
                        if (float.Parse(h, CultureInfo.InvariantCulture) < 13f) free++;
                    }
                    return free > 1;
                }

                int applies = 0, pathFails = 0, firstFail = -1, try_ = 0, restarts = 0, appliesAtEnd = -1;
                int walkLegFails = 0, firstWalkFail = -1;
                var failLog = new StringBuilder();

                // Wall-embed check (regression: gnomes falling through walls): a body at native
                // (x,y,z) is inside solid when its depth sits on the rock side of its own
                // sub-column — z < h(sx,sy) (air = z ≥ h; the standing law z = min(h+1, 14.5) and
                // the FloorPathMap band [h·4+1, …) both live on the air side). Wall-hang records
                // ride z = support ≥ h(own) − clearance; allow the record's own row-up support
                // read: probe the min of the body cell and one row up (the support row).
                int embedFails = 0; var embedLog = new StringBuilder();
                void CheckEmbed(float px, float py, float pz, string label)
                {
                    int sx = Mathf.RoundToInt(px * 8f), sy = Mathf.RoundToInt(py * 8f);
                    int h = Mathf.Min(nav.GetHmapSub(sx, sy), nav.GetHmapSub(sx, sy - 1));
                    if (pz < h - 0.01f)
                    {
                        embedFails++;
                        if (embedLog.Length < 1500)
                            embedLog.AppendLine($"  EMBED {label}: ({px:0.00},{py:0.00},{pz:0.00}) " +
                                $"h(sub {sx},{sy})={nav.GetHmapSub(sx, sy)} hUp={nav.GetHmapSub(sx, sy - 1)}");
                    }
                }

                // Which layer nulls the path? Calc1 (tile graph) vs the depth assembly (FloorPathMap).
                void Diag(float ax2, float ay2, float bx2, float by2)
                {
                    int sx = Mathf.RoundToInt(ax2 * 8f), sy = Mathf.RoundToInt(ay2 * 8f);
                    int qx = Mathf.RoundToInt(bx2 * 8f), qy = Mathf.RoundToInt(by2 * 8f);
                    failLog.AppendLine($"  DIAG start sub({sx},{sy}) bits={nav.SubAt(sx, sy)} " +
                        $"baseCost={nav.BaseCost[(sy >> 3) * nav.Width + (sx >> 3)]} | " +
                        $"goal sub({qx},{qy}) bits={nav.SubAt(qx, qy)} " +
                        $"baseCost={nav.BaseCost[(qy >> 3) * nav.Width + (qx >> 3)]}");
                    var pf = new Calc1Pathfinder(nav);
                    var subs = pf.FindPath(sx, sy, qx, qy, true, 100);
                    if (subs == null) { failLog.AppendLine("  DIAG Calc1 = NULL (tile graph disconnect)"); return; }
                    var sb = new StringBuilder($"  DIAG Calc1 ok ({subs.Count} cells): ");
                    int mnx = int.MaxValue, mxx = int.MinValue, mny = int.MaxValue, mxy = int.MinValue;
                    foreach (var (cx, cy) in subs)
                    {
                        byte b = nav.SubAt(cx, cy);
                        char cls = (b & NavWorld.SubFloor) != 0 ? 'F' : (b & NavWorld.SubWalk) != 0 ? 'W' : '.';
                        sb.Append($"({cx},{cy}){cls}h{nav.GetHmapSub(cx, cy - 1):X1}/{nav.GetHmapSub(cx, cy + 1):X1} ");
                        mnx = Mathf.Min(mnx, cx); mxx = Mathf.Max(mxx, cx);
                        mny = Mathf.Min(mny, cy); mxy = Mathf.Max(mxy, cy);
                    }
                    failLog.AppendLine(sb.ToString());
                    failLog.AppendLine("  DIAG ⇒ assembly-layer null (FloorPathMap depth route); window follows");
                    TestContext.WriteLine(failLog.ToString()); failLog.Clear();
                    DumpWindow(nav, terr, mnx / 8 - 1, mxx / 8 + 1, mny / 8 - 1, mxy / 8 + 1);
                }
                for (int it = 0; it < 8000; it++)
                {
                    bool taskEnd = try_ >= 9;
                    float vx = -1, vy = -1, vz = -1;
                    if (!taskEnd)
                    {
                        string nxt = interp.Eval($"dig_next {{{Inv(gx)} {Inv(gy)} {Inv(gz)}}} 0 {try_}").Trim();
                        var np = nxt.Split(' ');
                        vx = float.Parse(np[0], CultureInfo.InvariantCulture);
                        vy = float.Parse(np[1], CultureInfo.InvariantCulture);
                        vz = float.Parse(np[2], CultureInfo.InvariantCulture);
                        taskEnd = vy <= 0;
                    }
                    if (taskEnd)
                    {
                        if (applies == appliesAtEnd)
                        { TestContext.WriteLine($"HARD STALL at iter {it}: task ended twice with no progress (applies={applies})"); break; }
                        appliesAtEnd = applies; restarts++; try_ = 0;
                        continue;   // ambient re-dispatch while the front still moves
                    }
                    float dz = vz - 1f;   // the dig_continue {0 0 -1} add (z_dignwalk.tcl:210)
                    if (!DigCheck(vx, vy, dz)) { try_++; continue; }

                    string fixd = interp.Eval(
                        $"vector_fixdig {{{Inv(vx)} {Inv(vy)} {Inv(dz)}}} {{{Inv(gx)} {Inv(gy)} {Inv(gz)}}}").Trim();
                    var fp = fixd.Split(' ');
                    float wxp = float.Parse(fp[0], CultureInfo.InvariantCulture);
                    float wyp = float.Parse(fp[1], CultureInfo.InvariantCulture);
                    float wzp = float.Parse(fp[2], CultureInfo.InvariantCulture);
                    float walkDist = Mathf.Sqrt((wxp - gx) * (wxp - gx) + (wyp - gy) * (wyp - gy) + (wzp - gz) * (wzp - gz));
                    if (walkDist > 0.5f)
                    {
                        // The walk leg. A transient null here is tolerated by the native loop —
                        // walk_pos_dig failure fires walkfail_tasks = "dig_continue try+1"
                        // (z_dignwalk.tcl:245-252) — so mirror that: escalate try, don't move.
                        var wp = svc.FindPath(W(new Vector3(gx, gy, gz)), W(new Vector3(wxp, wyp, wzp)));
                        if (wp == null)
                        {
                            walkLegFails++;
                            if (firstWalkFail < 0)
                            {
                                firstWalkFail = it;
                                failLog.AppendLine($"first walk-leg null at iter {it} (applies={applies}): " +
                                    $"digger=({gx:0.00},{gy:0.00},{gz:0.00}) → standoff=({wxp:0.00},{wyp:0.00},{wzp:0.00}) voxel=({vx},{vy})");
                                Diag(gx, gy, wxp, wyp);
                            }
                            try_++;
                            continue;
                        }
                        // Land where the walk actually ends: the route's final waypoint is the
                        // band-resolved standing depth (FloorPathMap goalRow·0.25 / climb record),
                        // never the raw vector_fixdig z — the live gnome's z tracks the bands.
                        // (Teleporting to the raw z ratcheted gz to 14.5 forever — max in the
                        // vector_fixdig z law never lowers — an unphysical digger the real
                        // executor never produces.)
                        // Every waypoint the body will ride must stay out of solid.
                        int embedBefore = embedFails;
                        for (int k = 0; k < wp.Count; k++)
                        {
                            var wn = N(wp[k].Pos);
                            CheckEmbed(wn.x, wn.y, wn.z, $"iter {it} wp[{k}]{(wp[k].IsWall ? "W" : "F")}");
                        }
                        if (embedFails > embedBefore && embedLog.Length < 3000)
                        {
                            embedLog.AppendLine($"    full leg (digger {gx:0.00},{gy:0.00},{gz:0.00} → standoff {wxp:0.00},{wyp:0.00},{wzp:0.00}):");
                            for (int k = 0; k < wp.Count; k++)
                            {
                                var wn = N(wp[k].Pos);
                                embedLog.AppendLine($"      wp[{k}]{(wp[k].IsWall ? "W" : "F")} ({wn.x:0.00},{wn.y:0.00},{wn.z:0.00})");
                            }
                        }
                        var landed = N(wp[wp.Count - 1].Pos);
                        gx = landed.x; gy = landed.y; gz = landed.z;
                    }
                    // dig_execute range tether (z_dignwalk.tcl:313; digattr fresh = 0 → 3.0).
                    float dd = Mathf.Sqrt((vx - gx) * (vx - gx) + (vy - (gy - 0.8f)) * (vy - (gy - 0.8f)));
                    if (dd < 3f)
                    {
                        string ok = interp.Eval($"dig_apply {{{Inv(vx)} {Inv(vy)} {Inv(dz)}}} 0").Trim();
                        if (ok == "1")
                        {
                            applies++; try_ = 0;
                            {
                                // per-apply (= per intermediate rim step 15→11→7→3→0) check:
                                // the player's mid-dig stop + walk order: path back out of the cave.
                                var back = svc.FindPath(W(new Vector3(gx, gy, gz)), W(homeN));
                                if (back == null)
                                {
                                    pathFails++;
                                    if (firstFail < 0)
                                    {
                                        firstFail = it;
                                        failLog.AppendLine($"FIRST home path failure at iter {it} (applies={applies}): " +
                                            $"digger=({gx:0.00},{gy:0.00},{gz:0.00}) home=({homeN.x:0.00},{homeN.y:0.00})");
                                        Diag(gx, gy, homeN.x, homeN.y);
                                    }
                                }
                            }
                        }
                        else try_++;
                    }
                    else try_++;
                }
                TestContext.WriteLine($"total applies={applies}, restarts={restarts}, HOME path failures={pathFails} " +
                    $"(first at iter {firstFail}), tolerated walk-leg nulls={walkLegFails} (first at iter {firstWalkFail}), " +
                    $"EMBEDDED waypoints={embedFails}");
                if (embedLog.Length > 0) TestContext.WriteLine(embedLog.ToString());
                if (failLog.Length > 0) TestContext.WriteLine(failLog.ToString());
                // Final: the digger can walk home out of the finished cave.
                var finalPath = svc.FindPath(W(new Vector3(gx, gy, gz)), W(homeN));
                if (finalPath == null) { pathFails++; TestContext.WriteLine($"FINAL home path failure from ({gx:0.00},{gy:0.00},{gz:0.00})"); }

                // Remaining marks = the never-finished cave.
                var left = new List<(int, int)>();
                var (x0, y0) = CaveStamp.Origin(axC, ayC);
                for (int row = 0; row < CaveTemplate.Size; row++)
                    for (int col = 0; col < CaveTemplate.Size; col++)
                        if (tpl.Cell(col, row) != CaveTemplate.Skip &&
                            terr.IsDigMarked(x0 + col, y0 + row, 0))
                            left.Add((x0 + col, y0 + row));
                if (left.Count > 0)
                {
                    var sb = new StringBuilder($"unfinished cave cells ({left.Count}): ");
                    foreach (var (mx, my) in left) sb.Append($"({mx},{my})nib={terr.RawDigNibble(mx, my, 0)} ");
                    TestContext.WriteLine(sb.ToString());
                }
                DumpWindow(nav, terr, axC - 8, axC + 4, ayC - 8, ayC + 1);

                Assert.AreEqual(0, pathFails, "digger lost pathability mid-cave (the Play stuck-gnome)");
                Assert.AreEqual(0, left.Count, "cave order never completed");
                Assert.AreEqual(0, embedFails, "walk-leg waypoints put the body INSIDE SOLID (the Play wall-embed)");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // -----------------------------------------------------------------------------------
        // C2 — cave marks alone dispatch an idle own gnome to dig (the ambient kind-5 funnel).
        // -----------------------------------------------------------------------------------
        [Test]
        public void CaveMarksAlone_DispatchIdleGnome_ToDig()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var disp = ctx.ProdDispatcher!;
                var (axC, ayC, tpl, _) = PrepareCaveSite(engine, terr, nav);

                // Spawn one own gnome on the tunnel floor near the future cave mouth.
                var spawn = FindFloorNear(nav, (axC - 10) * 8, (ayC - 1 + 1) * 8, 20);
                Assert.AreNotEqual(default((int, int)), spawn, "no tunnel floor for the gnome");
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                interp.Eval($"set_worktime {g.Handle} 0 12");
                for (int i = 0; i < 30; i++) sim.Step(0.1);
                interp.Eval($"set_worktime {g.Handle} 0 12");

                int painted = CaveStamp.Commit(tpl, axC, ayC, terr, 0, out int mouthX, out int mouthY);
                Assert.Greater(painted, 0, "cave stamp did not commit");
                TestContext.WriteLine($"cave0 committed: {painted} cells, seed ({mouthX},{mouthY}); NO manual order");

                // Tcl-side trace of the digger (the `log` command routes here when enabled).
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cd2-c2-work.log"));
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Tracked.Add(g.Handle);
                Diggles.Runtime.Diagnostics.DigglesDebugLog.MaxLines = 3000;

                // Probe the digger's first walk leg (spawn → the get_digedge mouth edge) at the
                // FindPath level, so a wedge separates plan failure from execution stall.
                {
                    var svc = (NavWorldService)ctx.NavService!;
                    var root = rootGo.transform;
                    Vector3 Wp(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                    var probe = svc.FindPath(Wp(g.Position),
                        Wp(new Vector3(mouthX - 0.5f, mouthY + 0.5f, 14f)));
                    TestContext.WriteLine($"[probe] spawn→mouth FindPath = " +
                        (probe == null ? "NULL" : probe.Count + " wps"));
                }

                bool sawAutoDig = false, sawDigTask = false;
                int firstDigStep = -1, marks0 = CountCaveMarks(terr, tpl, axC, ayC);
                Vector3 lastPos = g.Position; int frozenSteps = 0; bool wedgeDumped = false;
                for (int step = 0; step < 9000; step++)
                {
                    sim.Step(0.1);
                    if (step % 10 == 0) disp.SweepNow(0);
                    if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");
                    foreach (var ev in g.PendingEvents)
                        if (ev.Name == "evt_autoprod_dig") sawAutoDig = true;
                    g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                    if (wt == "dig" && !sawDigTask) { sawDigTask = true; firstDigStep = step; }
                    int m = CountCaveMarks(terr, tpl, axC, ayC);
                    if (step % 200 == 0)
                        TestContext.WriteLine($"step {step}: pooled={disp.IsPooled(g.Handle, 0)} " +
                            $"reqs={disp.DigRequestCount(0)} wt={wt} state={g.CurrentState} wr={g.WalkResult} " +
                            $"tasks={g.TaskList.Count} pos=({g.Position.x:0.0},{g.Position.y:0.0},{g.Position.z:0.0}) marksLeft={m}");
                    // Wedge catcher: position + marks frozen for 60 sim-seconds while "digging".
                    if ((g.Position - lastPos).sqrMagnitude < 1e-4f) frozenSteps++; else frozenSteps = 0;
                    lastPos = g.Position;
                    if (frozenSteps == 600 && !wedgeDumped && m > 0)
                    {
                        wedgeDumped = true;
                        TestContext.WriteLine($"=== WEDGE at step {step}: pos=({g.Position.x:0.000},{g.Position.y:0.000},{g.Position.z:0.000}) " +
                            $"wr={g.WalkResult} state={g.CurrentState} wt={wt} tasksN={g.TaskList.Count} " +
                            $"action='{g.Action}' active={g.ActiveAction?.GetType().Name ?? "null"} " +
                            $"finish='{g.ActionFinish}' break='{g.ActionBreak}' proc={g.ProcessActions}");
                        for (int k = 0; k < g.TaskList.Count && k < 4; k++)
                            TestContext.WriteLine($"    task[{k}] = {g.TaskList[k]}");
                        Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                    }
                    if (m == 0) break;
                }
                int marksLeft = CountCaveMarks(terr, tpl, axC, ayC);
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled = false;
                TestContext.WriteLine($"RESULT: sawAutoDig={sawAutoDig} sawDigTask={sawDigTask} " +
                    $"firstDigStep={firstDigStep} marks {marks0}→{marksLeft} reqs={disp.DigRequestCount(0)}");
                Assert.IsTrue(sawAutoDig,
                    "cave marks alone never raised evt_autoprod_dig — the dispatcher dig funnel skips caves");
                Assert.IsTrue(sawDigTask, "the gnome never entered the dig worktask from the ambient dispatch");
                Assert.Less(marksLeft, marks0, "the dispatched gnome never carved a cave cell");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        static int CountCaveMarks(DiggableTerrain terr, CaveTemplate tpl, int axC, int ayC)
        {
            var (x0, y0) = CaveStamp.Origin(axC, ayC);
            int n = 0;
            for (int row = 0; row < CaveTemplate.Size; row++)
                for (int col = 0; col < CaveTemplate.Size; col++)
                    if (tpl.Cell(col, row) != CaveTemplate.Skip && terr.IsDigMarked(x0 + col, y0 + row, 0))
                        n++;
            return n;
        }

        // -----------------------------------------------------------------------------------
        // C2b — the wedge hunt: the ambient cave dispatch occasionally freezes (in the live game:
        // idle work-window gnomes never dig marked caves; headless: C2 fails intermittently). The
        // dig-loop randomness is UnityEngine.Random (TclExpr rand) — iterate seeds over fresh
        // worlds and, when a run freezes, dump the full entity/action/tasklist state.
        // -----------------------------------------------------------------------------------
        static readonly int[] WedgeHuntSeeds = { 1, 2, 3, 4, 5 };   // 4 = the historical wedge

        [Test]
        public void CaveDispatch_WedgeHunt_SeededRuns()
        {
            var wedges = new List<string>();
            foreach (int seed in WedgeHuntSeeds)
            {
                var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
                var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
                try
                {
                    ctx.LocalPlayer = 0;
                    var disp = ctx.ProdDispatcher!;
                    var (axC, ayC, tpl, _) = PrepareCaveSite(engine, terr, nav);
                    var spawn = FindFloorNear(nav, (axC - 10) * 8, ayC * 8, 20);
                    float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                    string h = interp.Eval(
                        $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                    var g = ctx.GetEntity(h)!;
                    g.Owner = 0;
                    interp.Eval($"set_worktime {g.Handle} 0 12");
                    for (int i = 0; i < 30; i++) sim.Step(0.1);
                    interp.Eval($"set_worktime {g.Handle} 0 12");

                    UnityEngine.Random.InitState(seed * 7919);
                    int painted = CaveStamp.Commit(tpl, axC, ayC, terr, 0, out _, out _);
                    Assert.Greater(painted, 0, "stamp failed");
                    int marks0 = CountCaveMarks(terr, tpl, axC, ayC);

                    Vector3 lastPos = g.Position; int frozen = 0; string verdict = "clean";
                    int mAtEnd = marks0;
                    for (int step = 0; step < 4000; step++)
                    {
                        sim.Step(0.1);
                        if (step % 10 == 0) disp.SweepNow(0);
                        if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");
                        if ((g.Position - lastPos).sqrMagnitude < 1e-4f) frozen++; else frozen = 0;
                        lastPos = g.Position;
                        mAtEnd = CountCaveMarks(terr, tpl, axC, ayC);
                        if (mAtEnd == 0) break;
                        if (frozen == 560) DigglesAnimController.TracePlayClip = true;   // [FINTRC] window
                        if (frozen == 600)
                        {
                            DigglesAnimController.TracePlayClip = false;
                            g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                            verdict = $"WEDGE step={step} pos=({g.Position.x:0.000},{g.Position.y:0.000},{g.Position.z:0.000}) " +
                                $"wr={g.WalkResult} state={g.CurrentState} wt={wt} tasksN={g.TaskList.Count} " +
                                $"active={g.ActiveAction?.GetType().Name ?? "null"} action='{g.Action}' " +
                                $"finish='{g.ActionFinish}' proc={g.ProcessActions} marksLeft={mAtEnd}";
                            for (int k = 0; k < g.TaskList.Count && k < 4; k++)
                                verdict += $"\n    task[{k}] = {g.TaskList[k]}";
                            // Where would the current walk go? probe pathability around the spot.
                            var svc = (NavWorldService)ctx.NavService!;
                            var root = rootGo.transform;
                            Vector3 Wp(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                            var back = svc.FindPath(Wp(g.Position), Wp(new Vector3(spawn.sx / 8f, spawn.sy / 8f, gz)));
                            verdict += $"\n    FindPath(pos→spawn) = {(back == null ? "NULL" : back.Count + " wps")}";
                            // Manual dig-loop iteration from the wedged pose: what does the loop see?
                            float px = g.Position.x, py = g.Position.y, pz = g.Position.z;
                            for (int tn = 0; tn < 4; tn++)
                            {
                                string nx = interp.Eval($"dig_next {{{Inv(px)} {Inv(py)} {Inv(pz)}}} {g.Handle} {tn}").Trim();
                                var np = nx.Split(' ');
                                float vx = float.Parse(np[0], CultureInfo.InvariantCulture);
                                float vy = float.Parse(np[1], CultureInfo.InvariantCulture);
                                float vz = float.Parse(np[2], CultureInfo.InvariantCulture) - 1f;
                                if (vy <= 0) { verdict += $"\n    dig_next[{tn}] = sentinel"; break; }
                                string fx = interp.Eval($"vector_fixdig {{{Inv(vx)} {Inv(vy)} {Inv(vz)}}} " +
                                    $"{{{Inv(px)} {Inv(py)} {Inv(pz)}}}").Trim();
                                var fp = fx.Split(' ');
                                float wx2 = float.Parse(fp[0], CultureInfo.InvariantCulture);
                                float wy2 = float.Parse(fp[1], CultureInfo.InvariantCulture);
                                float wz2 = float.Parse(fp[2], CultureInfo.InvariantCulture);
                                float wd = Mathf.Sqrt((wx2 - px) * (wx2 - px) + (wy2 - py) * (wy2 - py) + (wz2 - pz) * (wz2 - pz));
                                float dd = Mathf.Sqrt((vx - px) * (vx - px) + (vy - (py - 0.8f)) * (vy - (py - 0.8f)));
                                verdict += $"\n    dig_next[{tn}]=({vx},{vy},z{vz + 1}) fixdig=({wx2:0.00},{wy2:0.00},{wz2:0.00}) " +
                                    $"walkdist={wd:0.00} execRange2d={dd:0.00} rim={terr.RimAtNode((int)vx, (int)vy)} " +
                                    $"nib={terr.RawDigNibble((int)vx, (int)vy, 0)}";
                            }
                            break;
                        }
                    }
                    TestContext.WriteLine($"seed {seed}: {verdict}; marks {marks0}→{mAtEnd}");
                    if (verdict != "clean") wedges.Add($"seed {seed}: {verdict}");
                }
                finally { Object.DestroyImmediate(rootGo); }
            }
            if (wedges.Count > 0)
                Assert.Fail($"{wedges.Count}/{WedgeHuntSeeds.Length} seeded runs wedged:\n" + string.Join("\n", wedges));
        }
    }

    // The painters' rim re-mark gate (FUN_005672d0:40-53 mode-1 / FUN_005671a0:32-45 mode-0) —
    // DiggableTerrain.MarkRimGate: a marker cell whose node rim can no longer reach the existing
    // owner nibble's decoded target is silently dropped at paint time. Regression: tunnel markers
    // could be drawn over already-dug tunnels. Real BDB (the rim walk needs the aux
    // corner-transition graph).
    public sealed class MarkRimGateTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        DiggableTerrain MakeSolid(int w, int h)
        {
            if (!System.IO.File.Exists(DataRoot + "/BlockDB2.BDB"))
                Assert.Ignore("real BDB not present");
            var go = new GameObject("MarkRimGateHost");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h, BlockIdx = new uint[w * h], Terrain = new byte[w * h],
            }, BdbParser.Parse(DataRoot + "/BlockDB2.BDB"), "", () => { });
            return t;
        }

        const int Dummy = 3;            // the mode-1 standard-tunnel nibble (target 11)
        const int CaveDeep = 1 | 8;     // a mode-0 cave cell, cnt 1 (target 3)
        const int FullDepth = 0 | 8;    // the mode-0 full-depth nibble (target 0)

        [Test]
        public void MarkRimGate_PaintedNibbleVsRim_Law()
        {
            var t = MakeSolid(32, 32);

            // Virgin rock: RimAtNode = the next carve step's level = 11 → a standard tunnel
            // (dummy target 11) and every deeper tool paint fine.
            Assert.IsTrue(t.MarkRimGate(10, 10, Dummy), "virgin rock must accept a tunnel mark");
            Assert.IsTrue(t.MarkRimGate(10, 10, FullDepth), "virgin rock accepts full depth");

            // One carve step at node (10,10) = the corridor level dug out (rim drops to the next
            // level < 11): a new standard-tunnel mark is dropped — marking over an already-dug
            // tunnel is dropped on mouse-up (FUN_005672d0:40-53) — while the deeper cave/depth
            // nibbles still paint (the cave deepens the corridor).
            Assert.IsTrue(t.CarveTileNow(10, 10), "carve step failed");
            Assert.IsFalse(t.MarkRimGate(10, 10, Dummy),
                "a dug-out corridor cell must drop a re-tunnel mark");
            Assert.IsTrue(t.MarkRimGate(10, 10, CaveDeep),
                "a deeper cave cell still paints over a dug corridor (mouth-over-tunnel law)");

            // Fully carved to the floor: rim −1 → every nibble drops, even full depth.
            int guard = 0;
            while (t.CarveTileNow(10, 10) && guard++ < 32) { }
            Assert.IsFalse(t.MarkRimGate(10, 10, FullDepth), "a floor node accepts nothing");

            // Terrain away from the carve stays untouched and markable.
            Assert.IsTrue(t.MarkRimGate(14, 10, Dummy), "distant terrain stays markable");
        }
    }
}
