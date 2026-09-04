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

    // Regression: a gnome digging a vertical shaft downward under himself must not end up
    // hanging in the air when the block he stands on is carved away, nor crawl sideways through
    // the air to reach a wall.
    //
    // Native law: the carve itself never repositions a movable. FUN_00596b80 only mutates
    // terrain state; the MapValidator OBJPHYSIC stage (FUN_005eb7b0 → FUN_005eae80 → the +0x40c
    // queue consumed by FUN_00605c10) is a deferred delete for static deco classes (class+0x68
    // physic mode 1/3) — movables are mode 0 and untouched. The digger's reaction is the action
    // layer: the next walk of the dig loop (z_dignwalk.tcl dig_continue → walk_pos_dig) plans
    // from the gnome's current sub-cell; with the floor bit gone the surface classifier
    // (FUN_00423730: +0x3c bit2 clear ⇒ wall) makes the first MetaWalk segment a climb
    // (FUN_004163a0 → FUN_00415e10 CAct_Climb) whose records ride the FUN_004238e0 support depth
    // — the gnome mounts the wall at his own column (the visible snap) and climbs to the
    // standoff. He never translates through dead air, and while wall-borne his depth rides the
    // support nibble, not a stale value.
    //
    // This test drives the real loop (dispatcher → z_dignwalk → carve → walk) on the real
    // start-meadow terrain and asserts two invariants:
    // (1) no dead-air travel: the body never accumulates lateral X translation while its own
    // sub-cell is neither floor nor climb band (value 0).
    // (2) no persistent hang: once the support tile under the body is carved, the body becomes
    // supported again (floor bit at subY+1/subY/subY-1, or a climb-band cell with the depth
    // riding the support nibble) within a few sim seconds.
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class DigUnderfootMountTests
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

        static int CountMarks(DiggableTerrain terr, List<(int tx, int ty)> marks)
        {
            int n = 0;
            foreach (var (mtx, mty) in marks) if (terr.IsDigMarked(mtx, mty, 0)) n++;
            return n;
        }

        // Support classification at a native body position — the discriminator the invariants ride.
        // FLOOR: a +0x3c floor bit at subY+1 / subY / subY-1 (the get_gnomeposition probe rows,
        // FUN_0068d8a0:86-93).
        // WALL: the body cell is a climb-band cell (value 1) — wall-borne (surface classifier
        // FUN_00423730 type 0 with walkable bit).
        // AIR: neither — dead air (value 0). The native never moves a gnome through it.
        enum Support { Floor, Wall, Air }

        static Support ClassifySupport(NavWorld nav, Vector3 nativePos)
        {
            int sx = Mathf.RoundToInt(nativePos.x * 8f), sy = Mathf.RoundToInt(nativePos.y * 8f);
            if (nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1))
                return Support.Floor;
            if ((nav.SubAt(sx, sy) & NavWorld.SubWalk) != 0) return Support.Wall;
            return Support.Air;
        }

        // Standing seat law (vector_fixdig FUN_00594e00:332-413): z = support-pocket nibble + 1,
        // capped 14.5. Wall seat law (climb records, FUN_004238e0): z = the support depth of the
        // cell. Both read heights around the body cell; tolerance covers the ±1 row jitter.
        static float LawfulSeatZ(NavWorld nav, Vector3 p, Support sup)
        {
            int sx = Mathf.RoundToInt(p.x * 8f), sy = Mathf.RoundToInt(p.y * 8f);
            if (sup == Support.Wall) return NavWorldService.SupportDepth(nav, sx, sy);
            // floor: the open-pocket depth just above the ground line
            int h = Mathf.Min(nav.GetHmapSub(sx, sy - 1), nav.GetHmapSub(sx, sy - 2));
            return Mathf.Min(h + 1f, 14.5f);
        }

        // ---------------------------------------------------------------------------------------
        // Pins the exact post-carve digger moment, deterministic (no dispatcher): a body hangs
        // where its floor was just carved away (the shaft mouth interior, stale row and stale z).
        // The follow-up dig walk to the shaft interior must plan (no walkfail) and mount (first
        // run classified wall, ClimbAction) — the native surface classifier FUN_00423730 keys off
        // the +0x3c floor bit of the relaxed start, and Calc1's +y-first start relax
        // 0059d640:113-148 is the mount law — and the executed chain must never translate the
        // body laterally through dead-air cells at the stale row.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void SupportCarvedUnderBody_NextWalk_MountsWall_NoLateralAirGlide()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                var anchor = FindMeadowAnchor(nav);
                Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
                int ptx = anchor.sx / 8, pty = anchor.sy / 8 + 1;

                // Body = the digger the instant his support was carved: he stood at the meadow
                // line over what is now the shaft mouth; his (x,y,z) are stale (the carve never
                // repositions a movable — FUN_00596b80 has no object write; OBJPHYSIC only
                // deletes static deco classes, FUN_005eb7b0→FUN_005eae80→FUN_00605c10).
                int bodySx = ptx * 8 + 8;                     // over the mouth interior (2nd tile)
                float standZ = Mathf.Min(nav.GetHmapSub(bodySx, anchor.sy - 1) + 1, 14.5f);
                Vector3 bodyN = new Vector3(bodySx / 8f, anchor.sy / 8f, standZ);

                DigShaft(terr, nav, ptx, pty, nodesWide: 2, nodesDeep: 4);

                var supAfter = ClassifySupport(nav, bodyN);
                TestContext.WriteLine($"body native=({bodyN.x:0.00},{bodyN.y:0.00},{bodyN.z:0.00}) " +
                    $"supportAfterCarve={supAfter}");
                if (supAfter == Support.Floor) { Assert.Ignore("carve did not remove the floor under the body"); return; }

                // The follow-up dig walk target: a floor cell inside the shaft below (the next
                // dig standoff vector_fixdig would produce — z = support+1, the standing law).
                var bottom = FindShaftBottom(nav, ptx, pty, anchor.sy);
                Assert.AreNotEqual(default((int, int)), bottom, "the shaft has no interior floor line");
                float botZ = Mathf.Min(nav.GetHmapSub(bottom.sx, bottom.sy - 1) + 1, 14.5f);
                var goalN = new Vector3(bottom.sx / 8f, bottom.sy / 8f, botZ);
                TestContext.WriteLine($"standoff native=({goalN.x:0.00},{goalN.y:0.00},{goalN.z:0.00})");

                var path = svc.FindPath(W(bodyN), W(goalN));
                Assert.IsNotNull(path, "the post-carve follow-up walk must PLAN — a NULL path is the " +
                    "walkfail churn (the digger hangs at the stale position for seconds)");
                DumpPath(path!, root, nav);

                // The mount law: the plan from the unsupported start must enter the world as a
                // climb (path[0] rides the relaxed start's wall class), never as a floor run
                // gliding from the stale row.
                Assert.IsTrue(path![0].IsWall || (path.Count > 1 && path[1].IsWall),
                    "the first run after losing the floor must be a CLIMB (mount) — a floor-classified " +
                    "start glides the body laterally through the air (the Play 'slides sideways through the air')");

                // Execute the chain; audit dead-air lateral travel + the wall depth seat.
                var body = new GameObject("body");
                var ctrl = body.AddComponent<DigglesAnimController>();
                body.transform.position = W(bodyN);
                float airLatX = 0f; float worstSeatErr = 0f;
                Vector3 prevN = bodyN;
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
                        var sup = ClassifySupport(nav, pn);
                        if (sup == Support.Air) airLatX += Mathf.Abs(pn.x - prevN.x);
                        else if (sup == Support.Wall)
                        {
                            float err = Mathf.Abs(pn.z - NavWorldService.SupportDepth(
                                nav, Mathf.RoundToInt(pn.x * 8f), Mathf.RoundToInt(pn.y * 8f)));
                            if (err > worstSeatErr) worstSeatErr = err;
                        }
                        prevN = pn;
                    }
                    Assert.Less(guard, 20000, "segment never finished");
                    si = sj;
                }
                Vector3 endN = N(body.transform.position);
                TestContext.WriteLine($"end native=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) " +
                    $"airLatX={airLatX:0.00} worstWallSeatErr={worstSeatErr:0.0}");
                Object.DestroyImmediate(body);

                Assert.LessOrEqual(airLatX, 0.5f,
                    "the executed chain translated the body laterally through DEAD AIR — the Play crawl; " +
                    "the native rides climb records (support depth) or floor lines only");
                Assert.LessOrEqual(worstSeatErr, 2.5f,
                    "wall-borne depth seat drifted off the record law (FUN_004238e0 support depth)");
                Assert.Less(Mathf.Abs(endN.x - goalN.x) + Mathf.Abs(endN.y - goalN.y), 1.0f,
                    "the follow-up walk must land the digger at the shaft standoff");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // The mid-walk carve law, deterministic: a walk is in flight across the meadow line when
        // a shaft is carved open under the route (the digger's own dig_execute, a co-digger, or a
        // growth stamp — all reach the walker the same way).
        //
        // Native law: CAct_WalkFloor consumes its route in ~32-sub-cell legs, each leg rebuilt
        // from current terrain (FUN_0041a470 arc split + FUN_0041aac0 band stamping) — the leg
        // over the fresh hole fails with WS_NOPASSAGE (3); CActWalk_AutoRetry (FUN_00408160:17-19)
        // then silently rebuilds a fresh MetaWalk from the current position iff the gnome moved
        // since the last plan (FUN_00407fb0:30-35). The fresh plan's start cell now classifies as
        // wall (FUN_00423730) → CAct_Climb → the gnome mounts the shaft wall and descends/crosses
        // riding the FUN_004238e0 support depths. He never glides through the mouth's dead air on
        // the stale route.
        //
        // The "frozen" walker below reproduces the un-fixed behavior for comparison: it freezes
        // the whole run at plan time (no leg re-read) and crosses the carved mouth in a straight
        // line through dead air at the stale row. The "engine" walker (live support probe +
        // NOPASSAGE retry) must not reproduce that glide.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void MidWalkCarve_WalkerAbortsAndRemounts_NoAirGlide()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                // Site = the earth chimney (the only carve-graph-steppable surface column — the
                // meadow line elsewhere is granite whose blocks have no aux transitions; a carve
                // there is a silent no-op and the test proves nothing).
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                int floorTy = digFloor.sy / 8;

                // A (west of the chimney, on the bowl line) → B = the chimney top itself: a walk
                // toward ground that a carve removes mid-flight.
                var A = FindLineCellWide(nav, (digTx - 8) * 8, (digTx - 1) * 8 - 2, digFloor.sy, 16);
                var B = digFloor;
                Assert.AreNotEqual(default((int, int)), A, "no floor west of the chimney");
                float zA = Mathf.Min(nav.GetHmapSub(A.sx, A.sy - 1) + 1, 14.5f);
                float zB = Mathf.Min(nav.GetHmapSub(B.sx, B.sy - 1) + 1, 14.5f);
                Vector3 wA = W(new Vector3(A.sx / 8f, A.sy / 8f, zA));
                Vector3 wB = W(new Vector3(B.sx / 8f, B.sy / 8f, zB));

                var path = svc.FindPath(wA, wB);
                Assert.IsNotNull(path, "baseline A→B path must exist");
                TestContext.WriteLine($"A=({A.sx},{A.sy}) B=({B.sx},{B.sy}) chimney tx={digTx} " +
                    $"floorTy={floorTy} baseline wps={path!.Count}");

                // Two walkers ride the same pre-carve plan side by side:
                // F = the frozen chain (no probe, no retry) — reproduces the un-fixed glide;
                // E = the engine-shaped chain (live probe + WalkResult + NOPASSAGE retry) — the law.
                var bodyF = new GameObject("frozen");
                var ctrlF = bodyF.AddComponent<DigglesAnimController>();
                bodyF.transform.position = wA;
                var ptsF = new List<Vector3>();
                foreach (var wp in path) if (!wp.IsWall) ptsF.Add(wp.Pos);
                IEntityAction frozen = new MetaWalkAction(ctrlF, bodyF.transform, ptsF, 0, -1, 0);

                var ent = new DigglesTclEntity();
                var bodyE = new GameObject("walker");
                var ctrlE = bodyE.AddComponent<DigglesAnimController>();
                bodyE.transform.position = wA;
                System.Func<Vector3, bool> probe = svc.HasFloorSupportWorld;
                IEntityAction BuildChain(List<NavWaypoint> p)
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
                            segs.Add(new ClimbAction(ctrlE, bodyE.transform, run, 0, -1, 0));
                        }
                        else
                        {
                            var pts = new List<Vector3>(sj - si);
                            for (int k = si; k < sj; k++) pts.Add(p[k].Pos);
                            segs.Add(new MetaWalkAction(ctrlE, bodyE.transform, pts, 0, -1, 0, probe));
                        }
                        si = sj;
                    }
                    IEntityAction chain = segs.Count == 1 ? segs[0] : new SequenceAction(segs);
                    return new WalkResultAction(chain, ent, 4);
                }
                IEntityAction walk = new RetryWalkAction(BuildChain(path), ent, bodyE.transform, wB, () =>
                {
                    var pR = svc.FindPath(bodyE.transform.position, wB);
                    if (pR == null || pR.Count < 2) return null;
                    TestContext.WriteLine($"  [retry] replanned from " +
                        $"{N(bodyE.transform.position).x:0.00},{N(bodyE.transform.position).y:0.00} — {pR.Count} wps");
                    return BuildChain(pR);
                });

                // Walk both until the engine walker nears the chimney's west rim, then carve the
                // chimney open under the route — the mid-flight support loss.
                bool runF = true, runE = true;
                int lead = 0;
                while (runE && lead++ < 600 && N(bodyE.transform.position).x < digTx - 1.2f)
                { runE = walk.Tick(0.1); if (runF) runF = frozen.Tick(0.1); }
                Assert.IsTrue(runE, "the engine walker finished before reaching the chimney — bad site");
                for (int round = 0; round < 64; round++)
                {
                    bool any = false;
                    for (int ny = floorTy + 1; ny <= floorTy + 4; ny++)
                        for (int nx = digTx; nx <= digTx + 2; nx++)
                            any |= terr.CarveTileNow(nx, ny);
                    if (!any) break;
                }
                nav.RevalidateRect((digTx - 2) * 8, (floorTy - 2) * 8, (digTx + 4) * 8 + 7, (floorTy + 6) * 8 + 7);
                // The line over the chimney must actually have opened, else the site refuses the
                // carve and the scenario is void.
                var mouthSup = ClassifySupport(nav, new Vector3(digTx + 0.5f, digFloor.sy / 8f,
                    Mathf.Min(nav.GetHmapSub(digTx * 8 + 4, digFloor.sy - 1) + 1, 14.5f)));
                TestContext.WriteLine($"carved chimney x[{digTx}..{digTx + 2}] mid-walk; mouth support={mouthSup}; " +
                    $"E at {N(bodyE.transform.position).x:0.00},{N(bodyE.transform.position).y:0.00}");
                if (mouthSup == Support.Floor) { Assert.Ignore("the carve did not open the line — site not steppable"); return; }

                // "Unsupported for a walker": no floor bit in the get_gnomeposition rows and not
                // wall-borne (a walkable value-1 cell with the depth riding the FUN_004238e0
                // support seat is a legitimate climb body). The carved mouth derives value-1, so a
                // pure dead-air (value-0) metric misses the glide: a floor walk crossing the climb
                // band at a stale line depth is unsupported too.
                bool UnsupportedForWalk(Vector3 pn)
                {
                    int sx = Mathf.RoundToInt(pn.x * 8f), sy = Mathf.RoundToInt(pn.y * 8f);
                    if (nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1))
                        return false;
                    return (nav.SubAt(sx, sy) & NavWorld.SubWalk) == 0
                        || Mathf.Abs(pn.z - NavWorldService.SupportDepth(nav, sx, sy)) > 2.5f;
                }

                // Finish both walks, auditing unsupported lateral travel.
                float glideF = 0f; Vector3 pvF = N(bodyF.transform.position);
                int gF = 0;
                while (runF && gF++ < 4000)
                {
                    runF = frozen.Tick(0.1);
                    Vector3 pn = N(bodyF.transform.position);
                    if (UnsupportedForWalk(pn)) glideF += Mathf.Abs(pn.x - pvF.x);
                    pvF = pn;
                }
                bool frozenEndsUnsupported = UnsupportedForWalk(N(bodyF.transform.position));
                TestContext.WriteLine($"[CONTROL frozen-leg] unsupported lateral glide = {glideF:0.00} native, " +
                    $"ends unsupported = {frozenEndsUnsupported} — the pre-fix Play 'hangs in the air, slides sideways'");
                if (glideF < 0.3f && !frozenEndsUnsupported)
                { Assert.Ignore("the carve did not un-support the frozen route — scenario void at this site"); return; }

                float airLatX = 0f; double airTime = 0, worstAir = 0;
                Vector3 prevN = N(bodyE.transform.position);
                int guard = 0;
                while (runE && guard++ < 6000)
                {
                    runE = walk.Tick(0.1);
                    Vector3 pn = N(bodyE.transform.position);
                    if (UnsupportedForWalk(pn)) { airLatX += Mathf.Abs(pn.x - prevN.x); airTime += 0.1; }
                    else { if (airTime > worstAir) worstAir = airTime; airTime = 0; }
                    prevN = pn;
                }
                if (airTime > worstAir) worstAir = airTime;
                Vector3 endN = N(bodyE.transform.position);
                bool endUnsupported = UnsupportedForWalk(endN);
                TestContext.WriteLine($"RESULT: walkResult={ent.WalkResult} end=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) " +
                    $"endUnsupported={endUnsupported} airLatX={airLatX:0.00} worstAirTime={worstAir:0.0}s guard={guard}");
                Object.DestroyImmediate(bodyF);
                Object.DestroyImmediate(bodyE);

                Assert.Less(guard, 6000, "the walk never terminated after the mid-walk carve");
                // The law: no stale-plan glide across the carved mouth without support…
                Assert.LessOrEqual(airLatX, 0.5f,
                    "the walker crossed the carved shaft mouth UNSUPPORTED on the stale plan — " +
                    "the per-leg rebuild law (FUN_0041a470/FUN_0041aac0 → WS_NOPASSAGE → FUN_00408160 " +
                    "replan-from-here) must end the leg and remount instead");
                // …and the walk resolves like the native: re-routed (4 — the relaxed goal now sits
                // inside the shaft) or a clean NOPASSAGE for the Tcl walkfail (3) — never an
                // "arrived" stamp while stranded in the air.
                Assert.IsTrue(ent.WalkResult == 4 || ent.WalkResult == 3,
                    $"terminal WalkResult must be 4 (re-routed) or 3 (no passage), got {ent.WalkResult}");
                Assert.IsFalse(endUnsupported,
                    "the walker ended hanging unsupported — the native ends standing (floor) or wall-mounted " +
                    "(kletterstand at the support seat)");
                if (ent.WalkResult == 4)
                    Assert.Less(Mathf.Abs(endN.x - (digTx + 1f)), 4f,
                        "an 'arrived' walk must resolve near the (relaxed) chimney goal");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // The standing case, every stale hover column: the digger can be anywhere over the mouth
        // when his support is carved (dig_execute's multi-carve scoops several tiles from one spot
        // — z_dignwalk.tcl:314-381 never moves the gnome inside the loop; the carve never
        // repositions a movable). His next walk (dig_continue standoff, a fresh order,
        // walk_down_from_wall) then plans from that stale mid-air (x,y,z).
        //
        // Native law for that plan: Calc1 relaxes the start on the walkable bit, ring probes
        // +y,+x,−x,−y (FUN_0059d640:113-148); the first run's records begin at the relaxed cell
        // and the body is root-motion-driven continuously from its stale spot into record[0]
        // (FUN_00419c60:36-45 re-reads the live position every tick — no teleport); the run's
        // sub-action comes from FUN_00423730 on the relaxed cell — wall band ⇒ CAct_Climb (the
        // kletter descent the player sees). The gnome must end supported (floor or wall seat),
        // never gliding laterally through dead air in the walk pose, never stranded hanging.
        //
        // This drives the engine-shaped chain (live probe + WalkResult + NOPASSAGE retry — the
        // exact BuildWalkSegments/RetryWalkAction wiring) from every mouth column of a deep shaft
        // (deeper than the 32-sub-cell relax reach, so center columns sit over a long dead-air
        // drop) and audits lateral unsupported travel and end-state per column.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void DeepShaftHover_NextWalkFromEveryMouthColumn_MountsNeverCrawls()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                int ptx = ax;

                // Stage 1 — the marquee shaft partway down (the CaveDigRepro recipe; the chimney
                // column is 12+ tiles of earth): the digger has ridden the shaft floor down.
                // Capture the interior line seats across the mouth — the exact spots a digger
                // stands on mid-shaft (his (x,y,z) when the next carve removes them).
                var (s1x, s1y) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 6f);
                var marks1 = CommitMarquee(terr, ax, ay, s1x, s1y);
                Assert.Greater(marks1.Count, 3, "stage-1 marquee painted too few");
                TestContext.WriteLine($"stage-1 carved {CarveOut(terr, marks1)} steps");

                // Seats = the stage-1 interior floor line cells, plus hover seats synthesized at
                // the line row across the whole mouth width — the digger can stand anywhere on
                // the old line when the carve takes it (dig_execute swings from one spot,
                // z_dignwalk.tcl:314-381), including columns whose own cell never carried the
                // floor bit (the line narrows to the centre columns of an enclosed shaft).
                int mouthSx0 = ptx * 8 + 1, mouthSx1 = (ptx + 2) * 8 - 2;
                var found = new List<(int sx, int sy, float z)>();
                for (int sx = mouthSx0; sx <= mouthSx1; sx++)
                {
                    (int sx, int sy) line = default;
                    for (int sy = digFloor.sy + 2; sy <= (ay + 12) * 8 && line == default; sy++)
                        if (nav.SubIsFloor(sx, sy)) line = (sx, sy);
                    if (line == default) continue;
                    float z = Mathf.Min(nav.GetHmapSub(line.sx, line.sy - 1) + 1, 14.5f);
                    found.Add((line.sx, line.sy, z));
                }
                Assert.Greater(found.Count, 0, "no stage-1 interior line inside the shaft");
                int lineRow = found[0].sy; float lineZ = found[0].z;
                var stale = new List<(int sx, int sy, float z)>();
                for (int sx = mouthSx0 + 1; sx <= mouthSx1 - 1; sx += 3)
                {
                    var own = found.FindIndex(s => s.sx == sx);
                    stale.Add(own >= 0 ? found[own] : (sx, lineRow, lineZ));
                }
                TestContext.WriteLine($"stage-1 line: {found.Count} floor cells at row ~{lineRow}; " +
                    $"hover seats: {stale.Count}");
                Assert.Greater(stale.Count, 2, "no stage-1 interior line across the mouth");

                // Stage 2 — the support carve: the shaft deepens 6 more tiles under those seats
                // (48 sub-rows — beyond the 32-sub-cell Calc1 start-relax reach straight down).
                var (s2x, s2y) = DigMarquee.SnapDragCorner(ax, ay + 8, ax, ay + 8 + 4f);
                var marks2 = CommitMarquee(terr, ax, ay + 8, s2x, s2y);
                Assert.Greater(marks2.Count, 3, "stage-2 marquee painted too few");
                TestContext.WriteLine($"stage-2 carved {CarveOut(terr, marks2)} steps");

                (int sx, int sy) bottom = default;
                for (int sy = (ay + 16) * 8; sy > digFloor.sy && bottom == default; sy--)
                    for (int sx = ptx * 8; sx <= (ptx + 2) * 8; sx++)
                        if (nav.SubIsFloor(sx, sy)) { bottom = (sx, sy); break; }
                Assert.AreNotEqual(default((int, int)), bottom, "the shaft has no interior floor line");
                int depthRows = stale.Count > 0 ? bottom.sy - stale[0].sy : 0;
                float botZ = Mathf.Min(nav.GetHmapSub(bottom.sx, bottom.sy - 1) + 1, 14.5f);
                Vector3 goalN = new Vector3(bottom.sx / 8f, bottom.sy / 8f, botZ);
                Vector3 wGoal = W(goalN);
                TestContext.WriteLine($"shaft tx={ptx} mouth sy={digFloor.sy} bottom=({bottom.sx},{bottom.sy}) " +
                    $"rowsBelowSeats={depthRows} goal=({goalN.x:0.00},{goalN.y:0.00},{goalN.z:0.00})");
                Assert.Greater(depthRows, 32, "stage-2 did not deepen past the 32-sub-cell relax reach");

                bool UnsupportedForWalk(Vector3 pn)
                {
                    int sx = Mathf.RoundToInt(pn.x * 8f), sy = Mathf.RoundToInt(pn.y * 8f);
                    if (nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1))
                        return false;
                    return (nav.SubAt(sx, sy) & NavWorld.SubWalk) == 0
                        || Mathf.Abs(pn.z - NavWorldService.SupportDepth(nav, sx, sy)) > 2.5f;
                }

                var failures = new StringBuilder();
                int scenarios = 0;
                foreach (var (bsx, bsy, bz) in stale)
                {
                    Vector3 bodyN = new Vector3(bsx / 8f, bsy / 8f, bz);
                    var sup = ClassifySupport(nav, bodyN);
                    if (sup == Support.Floor) continue;              // this column kept its floor
                    scenarios++;

                    var ent = new DigglesTclEntity();
                    var body = new GameObject($"hover_{bsx}");
                    var ctrl = body.AddComponent<DigglesAnimController>();
                    body.transform.position = W(bodyN);
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

                    var path0 = svc.FindPath(body.transform.position, wGoal);
                    string firstClass = path0 == null ? "NULL"
                        : path0[0].IsWall ? "WALL" : (path0.Count > 1 && path0[1].IsWall ? "floor+WALL" : "FLOOR");
                    if (path0 == null)
                    {
                        failures.AppendLine($"  col sx={bsx} sup={sup}: NULL plan — the digger hangs " +
                            "through the whole walkfail ladder (the Play 'hangs in the air')");
                        Object.DestroyImmediate(body);
                        continue;
                    }
                    IEntityAction walk = new RetryWalkAction(BuildChain(path0)!, ent, body.transform, wGoal, () =>
                    {
                        var pR = svc.FindPath(body.transform.position, wGoal);
                        if (pR == null || pR.Count < 2) return null;
                        return BuildChain(pR);
                    });

                    float airLat = 0f; double airTime = 0, worstAirT = 0;
                    Vector3 prevN = bodyN;
                    int guard = 0; bool running = true;
                    while (running && guard++ < 8000)
                    {
                        running = walk.Tick(0.025);                 // the runtime sim step
                        Vector3 pn = N(body.transform.position);
                        if (UnsupportedForWalk(pn)) { airLat += Mathf.Abs(pn.x - prevN.x); airTime += 0.025; }
                        else { if (airTime > worstAirT) worstAirT = airTime; airTime = 0; }
                        prevN = pn;
                    }
                    if (airTime > worstAirT) worstAirT = airTime;
                    Vector3 endN = N(body.transform.position);
                    bool endUnsup = UnsupportedForWalk(endN);
                    TestContext.WriteLine($"col sx={bsx} sup={sup} first={firstClass} wr={ent.WalkResult} " +
                        $"end=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) endUnsup={endUnsup} " +
                        $"airLatX={airLat:0.00} worstAirT={worstAirT:0.0}s guard={guard}");
                    if (guard >= 8000)
                        failures.AppendLine($"  col sx={bsx}: walk never terminated");
                    // The native envelope: the mover drives the body continuously from its stale
                    // spot into record[0] (FUN_00419c60:36-45 — no teleport), and in an enclosed
                    // shaft the walkable ribbon is central (the 31-cell EFFPASSWALK fan dies at
                    // the side walls), so a pull of up to ~the mouth width is expected native
                    // behavior. Only a multi-tile crawl (> 1.5 native) exceeds what the relax
                    // reach can produce here.
                    if (airLat > 1.5f)
                        failures.AppendLine($"  col sx={bsx} sup={sup} first={firstClass}: lateral " +
                            $"UNSUPPORTED travel {airLat:0.00} — the Play 'slides sideways through the air'");
                    if (endUnsup)
                        failures.AppendLine($"  col sx={bsx} sup={sup} first={firstClass} wr={ent.WalkResult}: " +
                            "ended hanging unsupported — the native ends standing or wall-seated");
                    Object.DestroyImmediate(body);
                }
                TestContext.WriteLine($"hover scenarios exercised: {scenarios}");
                Assert.Greater(scenarios, 0, "the carve un-supported no mouth column — geometry void");
                Assert.AreEqual(0, failures.Length,
                    "hover columns violating the mount law:\n" + failures);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        static (int sx, int sy) FindLineCell(NavWorld nav, int sxFrom, int sxTo, int anchorSy)
            => FindLineCellWide(nav, sxFrom, sxTo, anchorSy, 8);

        static (int sx, int sy) FindLineCellWide(NavWorld nav, int sxFrom, int sxTo, int anchorSy, int dy)
        {
            for (int sx = sxFrom; sx < sxTo; sx++)
                for (int sy = anchorSy - dy; sy <= anchorSy + dy; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        static (int sx, int sy) FindShaftBottom(NavWorld nav, int ptx, int pty, int anchorSy)
        {
            for (int sy = (pty + 6) * 8; sy > anchorSy; sy--)
                for (int sx = (ptx - 1) * 8; sx <= (ptx + 3) * 8; sx++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        // ---------------------------------------------------------------------------------------
        // The stale-air-start law, minimal geometry (no dig at all): any walk that starts with
        // the body hanging in dead air (the digger whose floor was carved mid-swing; the
        // post-carve idle gnome given his next order) must:
        // (1) plan — the native builds the route from the relaxed start cell (FUN_0041aac0
        // densifies owner[+0x10], the relaxed Calc1 polyline; start relax
        // FUN_0059d640:113-148). Seeding the floor-run control polyline with the raw stale
        // (x,y) puts control[0] on a column whose standable band is empty (StampBands
        // [h·4+1, h·4−1) around an air cell) → SnapHoriz finds nothing → the whole plan
        // nulls → WalkResult 3 → the walkfail ladder spins while the body hangs.
        // (2) execute without churn — the native mover re-reads the live body every tick and
        // drives it continuously into the route (FUN_00419c60:36-45); the per-leg terrain
        // law re-reads tiles under the route, never the body's own support. A probe that
        // fires from tick 1 makes the lawful off-support approach die with WS_NOPASSAGE
        // every couple of ticks, and AutoRetry replans from the barely-moved position — a
        // stuttering glide per replan. The probe must arm only after the body has been
        // supported (from then on, the mid-walk carve law above applies unchanged).
        // ---------------------------------------------------------------------------------------
        [Test]
        public void StaleAirStart_FloorWalk_PlansImmediately_SettlesWithoutChurn()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                var line = FindMeadowAnchor(nav);
                Assert.AreNotEqual(default((int, int)), line, "no meadow line found");
                float seatZ = Mathf.Min(nav.GetHmapSub(line.sx, line.sy - 1) + 1, 14.5f);
                // The stale body: 4 sub-rows above the line (the geometry of "the floor dropped
                // from under him" — his y/z froze, the ground line is now below his probe rows).
                Vector3 bodyN = new Vector3(line.sx / 8f, (line.sy - 4) / 8f, seatZ);
                Assert.AreNotEqual(Support.Floor, ClassifySupport(nav, bodyN),
                    "harness: the hover cell still reads floor-supported");

                var goalCell = FindLineCellWide(nav, line.sx + 20, line.sx + 40, line.sy, 12);
                Assert.AreNotEqual(default((int, int)), goalCell, "no goal floor cell east on the line");
                float goalZ = Mathf.Min(nav.GetHmapSub(goalCell.sx, goalCell.sy - 1) + 1, 14.5f);
                Vector3 wGoal = W(new Vector3(goalCell.sx / 8f, goalCell.sy / 8f, goalZ));

                // (1) The plan law.
                var path = svc.FindPath(W(bodyN), wGoal);
                Assert.IsNotNull(path,
                    "a walk from a stale mid-air start must PLAN (the native relaxes the start and " +
                    "routes from the relaxed cell) — a NULL plan is the walkfail churn: the gnome " +
                    "hangs with no reaction until an unrelated order moves him");
                DumpPath(path!, root, nav);

                // (2) The no-churn execution law — the exact engine chain (probe + result + retry).
                var ent = new DigglesTclEntity();
                var body = new GameObject("staleAir");
                var ctrl = body.AddComponent<DigglesAnimController>();
                body.transform.position = W(bodyN);
                System.Func<Vector3, bool> probe = svc.HasFloorSupportWorld;
                int replans = 0;
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
                IEntityAction walk = new RetryWalkAction(BuildChain(path!)!, ent, body.transform, wGoal, () =>
                {
                    replans++;
                    var pR = svc.FindPath(body.transform.position, wGoal);
                    if (pR == null || pR.Count < 2) return null;
                    return BuildChain(pR);
                });

                int guard = 0; bool running = true;
                while (running && guard++ < 8000) running = walk.Tick(0.025);
                Vector3 endN = N(body.transform.position);
                var endSup = ClassifySupport(nav, endN);
                TestContext.WriteLine($"RESULT: wr={ent.WalkResult} replans={replans} guard={guard} " +
                    $"end=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) sup={endSup}");
                Object.DestroyImmediate(body);

                Assert.Less(guard, 8000, "the walk never terminated");
                Assert.AreEqual(4, ent.WalkResult,
                    "the stale-air-start walk must ARRIVE (the native settles the body onto the " +
                    "route and walks) — a 3 here is the walkfail churn");
                Assert.AreEqual(0, replans,
                    "the lawful off-support approach must not die into AutoRetry replans — the " +
                    "2-tick probe churn is the Play 'crawls through the air' stutter");
                Assert.AreEqual(Support.Floor, endSup, "must end standing on the line");
                Assert.Less(Mathf.Abs(endN.x - goalCell.sx / 8f) + Mathf.Abs(endN.y - goalCell.sy / 8f),
                    1.0f, "must end at the goal cell");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // The idle-hang case: the dig loop is not the thing that reacts here — the gnome stopped
        // digging (order done / work window closed) the moment his support was carved, so no
        // dig_continue walk ever came. The native reaction for that state is the script
        // idle/sparetime layer (all unmodified Tcl running on the native systems):
        // • work_idle think (z_work_states.tcl:277): idletimeout>10 && get_gnomeposition &&
        // get_prodautoschedule && !walk_down_from_wall → the descent walk;
        // • idle think (zwerg.tcl:313): same walk_down_from_wall after sparetime checks;
        // • sparetime fun/self (z_spare_fun.tcl:264): on wall → sparetime_climb_somewhere →
        // walk_pos/walk_down_from_wall (z_spare_procs.tcl:968-992);
        // • the walk itself then plans from the stale mid-air start: Calc1 relaxes the start
        // (+y,+x,−x,−y — FUN_0059d640:113-148), the run classifies by the relaxed cell
        // (FUN_00423730) and the body is driven continuously into record[0]
        // (FUN_00419c60:36-45) — a kletter-posed mount/descent, never a walk-pose glide,
        // never a permanent hang. There is no passive engine reaction for movables — gnomes
        // have physic off (zwerg.tcl:234, petrified-only), so the OBJPHYSIC fall pipeline
        // never touches them; the script walk is the law.
        //
        // Repro: run the real dispatcher dig loop on the deep marquee shaft; the moment the
        // digger loses floor support while not walking (mid-swing — the ActiveAction is the dig
        // anim/wait), close his work window right as the support went. Then audit the
        // idle/sparetime recovery for 120 sim seconds.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void SupportLostMidSwing_WindowCloses_IdleGnomeDescends_NeverHangsOrCrawls()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var disp = ctx.ProdDispatcher!;
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");

                var spawn = FindFloorNear(nav, digTx * 8 - 12, digFloor.sy, 24);
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                interp.Eval($"set_worktime {g.Handle} 0 12");
                for (int i = 0; i < 120; i++) sim.Step(0.025);
                interp.Eval($"set_worktime {g.Handle} 0 12");

                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 16f);
                var marks = CommitMarquee(terr, ax, ay, bx, by);
                Assert.Greater(marks.Count, 3, "marquee painted too few cells");

                bool UnsupportedNow(Vector3 pn)
                {
                    int sx = Mathf.RoundToInt(pn.x * 8f), sy = Mathf.RoundToInt(pn.y * 8f);
                    if (nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1))
                        return false;
                    return (nav.SubAt(sx, sy) & NavWorld.SubWalk) == 0
                        || Mathf.Abs(pn.z - NavWorldService.SupportDepth(nav, sx, sy)) > 2.5f;
                }
                bool IsMover(object? act)
                {
                    string n = act?.GetType().Name ?? "";
                    return n.Contains("Walk") || n.Contains("Climb") || n.Contains("Sequence")
                        || n.Contains("Retry");
                }

                // Phase 1 — dig until the mid-swing support loss.
                bool triggered = false;
                int step = 0;
                for (; step < 60000 && !triggered; step++)
                {
                    sim.Step(0.025);
                    if (step % 40 == 0) disp.SweepNow(0);
                    if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");
                    Vector3 p = g.Position;
                    if (ClassifySupport(nav, p) != Support.Floor && !IsMover(g.ActiveAction))
                    {
                        triggered = true;
                        interp.Eval($"set_worktime {g.Handle} 0 0");     // the window closes now
                        TestContext.WriteLine($"step {step}: support lost mid-swing " +
                            $"(act={g.ActiveAction?.GetType().Name ?? "-"} state={g.CurrentState}) " +
                            $"pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) → work window closed");
                    }
                }
                if (!triggered) { Assert.Ignore("no mid-swing support loss in this run — geometry kept the digger grounded"); return; }

                // Phase 2 — the idle/sparetime recovery audit (native: descent within ~10-20 s).
                double unsupTime = 0, worstUnsup = 0; float airDrift = 0, worstDrift = 0;
                Vector3 prev = g.Position;
                var log = new StringBuilder();
                int recovery = (int)(120.0 / 0.025);
                for (int k = 0; k < recovery; k++)
                {
                    sim.Step(0.025);
                    if (k % 40 == 0) disp.SweepNow(0);
                    Vector3 p = g.Position;
                    bool unsup = UnsupportedNow(p);
                    float dx = Mathf.Abs(p.x - prev.x);
                    if (unsup)
                    {
                        unsupTime += 0.025;
                        if (ClassifySupport(nav, p) == Support.Air) airDrift += dx;
                        if (log.Length < 3000 && (dx > 0.01f || unsupTime < 0.06))
                            log.AppendLine($"    k={k} unsup pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) " +
                                $"dx={dx:0.000} act={g.ActiveAction?.GetType().Name ?? "-"} state={g.CurrentState}");
                    }
                    else
                    {
                        if (unsupTime > worstUnsup) worstUnsup = unsupTime;
                        if (airDrift > worstDrift) worstDrift = airDrift;
                        unsupTime = 0; airDrift = 0;
                    }
                    prev = p;
                }
                if (unsupTime > worstUnsup) worstUnsup = unsupTime;
                if (airDrift > worstDrift) worstDrift = airDrift;
                bool endUnsup = UnsupportedNow(g.Position);
                TestContext.WriteLine($"RESULT: worstUnsup={worstUnsup:0.0}s worstAirDrift={worstDrift:0.00} " +
                    $"endUnsup={endUnsup} endPos=({g.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00}) " +
                    $"state={g.CurrentState}");
                if (log.Length > 0) TestContext.WriteLine("unsupported episodes:\n" + log);

                Assert.LessOrEqual(worstUnsup, 30.0,
                    "the idle gnome hung unsupported for over 30 s — the native idle/sparetime layer " +
                    "(walk_down_from_wall / sparetime_climb_somewhere) descends him within seconds " +
                    "(the Play 'hangs in the air, no reaction')");
                Assert.IsFalse(endUnsup, "still unsupported after 120 s of idle recovery");
                Assert.LessOrEqual(worstDrift, 0.5f,
                    "the recovery walk translated the body laterally through DEAD AIR — the Play " +
                    "'slides sideways through the air'");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // The front/back depth split: two gnomes dig the same pit, one standing in front (camera
        // side), the other behind. When the block is carved, front/back is the native depth seat
        // (z, dig axis): the standoff law FUN_00594e00:332-413 keeps a larger stale gnome z —
        // z = min(max(gnome.z, h(fx,fy−5)+1), 14.5) (verified in the decompile:
        // `if (fVar16 <= h+1) fVar16 = h+1` then the 14.5 cap) — so a digger who arrived from the
        // un-dug surface rides z ≈ 14.5 (front) while his co-digger who re-seated onto the carved
        // pocket line rides z ≈ pocket+1 (back). Same (x,y), same carve — only the depth differs.
        //
        // This test drives the exact post-carve reaction chain (FindPath → probe-wired segments
        // → RetryWalkAction, the BuildMetaWalkFromOptions wiring) from both depth seats over the
        // same carved mouth and audits:
        // (1) the reaction walk must plan (a null plan is the walkfail churn). Native law: the
        // leg builder passes the movable's live (x,y,z) (FUN_0041aac0:127-129) into
        // FUN_0041d8a0, whose backtrace-origin snap is ±16 cols × ±16 rows
        // (FUN_0041bbd0 at:189) — a high-z start must still resolve because the mouth's
        // standable band spans up to the 14.5 cap (StampBands [floorH·4+1, ceilH·4−1),
        // ceil undug = 15, rows up to 58 legal).
        // (2) the executed chain must never translate the body laterally through unsupported
        // cells beyond the native relax envelope, and must end supported (floor or wall
        // seat) — the front seat's larger depth delta must not turn the mount into a
        // walk-pose air glide.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void FrontAndBackDepthSeats_PostCarveReactionWalk_MountsBoth_NoHangNoAirCrawl()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                Vector3 N(Vector3 w) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(w));

                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                int surfRow = digFloor.sy, floorTy = surfRow / 8;
                int csx = digTx * 8 + 4;                     // the mouth's centre column
                // The front seat depth, captured before any carve: the un-dug surface pocket
                // (vector_fixdig's max law FUN_00594e00:332-413 preserves it through every later
                // standoff, so the camera-side digger rides it indefinitely).
                float zFront = Mathf.Min(nav.GetHmapSub(csx, surfRow - 1) + 1, 14.5f);

                // Open the pit under the surface line the same way the mid-walk-carve test does:
                // CarveTileNow rounds walk each tile's block-transition chain.
                for (int round = 0; round < 64; round++)
                {
                    bool any = false;
                    for (int ny = floorTy + 1; ny <= floorTy + 5; ny++)
                        for (int nx = digTx; nx <= digTx + 2; nx++)
                            any |= terr.CarveTileNow(nx, ny);
                    if (!any) break;
                }
                nav.RevalidateRect((digTx - 2) * 8, (floorTy - 2) * 8, (digTx + 4) * 8 + 7, (floorTy + 7) * 8 + 7);

                // The mouth must actually have opened under the centre column.
                var mouthSup = ClassifySupport(nav, new Vector3(csx / 8f, surfRow / 8f, zFront));
                if (mouthSup == Support.Floor) { Assert.Ignore("the carve did not open the line — site not steppable"); return; }

                // The interior line below = the co-digger's seat depth source + the walk goal.
                (int sx, int sy) bottom = default;
                for (int sy = (floorTy + 7) * 8; sy > surfRow + 2 && bottom == default; sy--)
                    for (int sx = digTx * 8; sx <= (digTx + 3) * 8; sx++)
                        if (nav.SubIsFloor(sx, sy)) { bottom = (sx, sy); break; }
                Assert.AreNotEqual(default((int, int)), bottom, "the pit has no interior floor line");
                float botZ = Mathf.Min(nav.GetHmapSub(bottom.sx, bottom.sy - 1) + 1, 14.5f);
                float zBack = botZ;                          // the pocket-line seat depth (behind)
                TestContext.WriteLine($"site: csx={csx} surfRow={surfRow} zFront={zFront:0.0} " +
                    $"bottom=({bottom.sx},{bottom.sy}) zBack={zBack:0.0} " +
                    $"(depth split {zFront - zBack:0.0} nibbles)");
                Vector3 wGoal = W(new Vector3(bottom.sx / 8f, bottom.sy / 8f, botZ));

                bool UnsupportedForWalk(Vector3 pn)
                {
                    int sx = Mathf.RoundToInt(pn.x * 8f), sy = Mathf.RoundToInt(pn.y * 8f);
                    if (nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1))
                        return false;
                    return (nav.SubAt(sx, sy) & NavWorld.SubWalk) == 0
                        || Mathf.Abs(pn.z - NavWorldService.SupportDepth(nav, sx, sy)) > 2.5f;
                }

                // The two depth seats over one mouth cell — same (x,y), only the depth differs
                // (front = camera side, back = the carved pocket depth):
                var seats = new (string name, Vector3 bodyN)[]
                {
                    ("front", new Vector3(csx / 8f, surfRow / 8f, zFront)),
                    ("back",  new Vector3(csx / 8f, surfRow / 8f, zBack)),
                };

                var failures = new StringBuilder();
                int scenarios = 0;
                foreach (var (name, bodyN) in seats)
                {
                    var sup = ClassifySupport(nav, bodyN);
                    if (sup == Support.Floor)
                    { TestContext.WriteLine($"[{name}] still floor-supported — seat skipped"); continue; }
                    scenarios++;

                    var ent = new DigglesTclEntity();
                    var body = new GameObject($"seat_{name}");
                    var ctrl = body.AddComponent<DigglesAnimController>();
                    body.transform.position = W(bodyN);
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

                    var path0 = svc.FindPath(body.transform.position, wGoal);
                    string firstClass = path0 == null ? "NULL"
                        : path0[0].IsWall ? "WALL" : (path0.Count > 1 && path0[1].IsWall ? "floor+WALL" : "FLOOR");
                    TestContext.WriteLine($"[{name}] body=({bodyN.x:0.00},{bodyN.y:0.00},{bodyN.z:0.00}) " +
                        $"sup={sup} plan={firstClass}");
                    if (path0 == null)
                    {
                        failures.AppendLine($"  [{name}] NULL reaction plan — the walkfail churn " +
                            "(the Play 'hangs in the air, no reaction')");
                        Object.DestroyImmediate(body);
                        continue;
                    }
                    DumpPath(path0, root, nav);
                    IEntityAction walk = new RetryWalkAction(BuildChain(path0)!, ent, body.transform, wGoal, () =>
                    {
                        var pR = svc.FindPath(body.transform.position, wGoal);
                        if (pR == null || pR.Count < 2) return null;
                        return BuildChain(pR);
                    });

                    float airLat = 0f; double airTime = 0, worstAirT = 0;
                    Vector3 prevN = bodyN;
                    int guard = 0; bool running = true;
                    var traj = new StringBuilder();
                    while (running && guard++ < 8000)
                    {
                        running = walk.Tick(0.025);
                        Vector3 pn = N(body.transform.position);
                        if (UnsupportedForWalk(pn)) { airLat += Mathf.Abs(pn.x - prevN.x); airTime += 0.025; }
                        else { if (airTime > worstAirT) worstAirT = airTime; airTime = 0; }
                        if (guard % 40 == 0 && traj.Length < 2500)
                            traj.AppendLine($"    t={guard * 0.025:0.0}s pos=({pn.x:0.00},{pn.y:0.00},{pn.z:0.00}) " +
                                $"unsup={UnsupportedForWalk(pn)}");
                        prevN = pn;
                    }
                    if (airTime > worstAirT) worstAirT = airTime;
                    Vector3 endN = N(body.transform.position);
                    bool endUnsup = UnsupportedForWalk(endN);
                    TestContext.WriteLine($"[{name}] wr={ent.WalkResult} " +
                        $"end=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) endUnsup={endUnsup} " +
                        $"airLatX={airLat:0.00} worstAirT={worstAirT:0.0}s guard={guard}");
                    if (traj.Length > 0) TestContext.WriteLine(traj.ToString());
                    Object.DestroyImmediate(body);

                    if (guard >= 8000)
                        failures.AppendLine($"  [{name}] walk never terminated");
                    if (airLat > 1.5f)
                        failures.AppendLine($"  [{name}] lateral UNSUPPORTED travel {airLat:0.00} — " +
                            "the Play 'slides sideways through the air'");
                    // The drive-in bound: the native mounts by riding the climb records from the
                    // stale seat (FUN_00419c60:36-45 continuous, climb cruise ≈ 1.04 wu/s over a
                    // ≤ mouth-diagonal + depth-delta approach) — seconds, not tens of seconds.
                    if (worstAirT > 8.0)
                        failures.AppendLine($"  [{name}] hung/glided unsupported {worstAirT:0.0}s — " +
                            "the Play 'hangs in the air'");
                    if (endUnsup)
                        failures.AppendLine($"  [{name}] ended hanging unsupported (wr={ent.WalkResult})");
                }
                Assert.Greater(scenarios, 0, "the carve un-supported no seat — geometry void");
                Assert.AreEqual(0, failures.Length, "depth seats violating the mount law:\n" + failures);
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // Two-gnome scene: one gnome digs the pit, the second stands on the pit's surface tile
        // (the camera-side gnome, z = the un-dug surface pocket ≈ 14.5) when the digger's
        // dig_apply carves the block under him. The bystander is not walking (idle think loop) —
        // his reaction is the script layer only (dig loop for a digger / idle-walk_down_from_wall
        // for an idler; there is no passive engine reaction for movables). Audit his recovery: no
        // dead-air lateral crawl, no hang beyond the idle-think latency, ends supported.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void DiggerCarvesTileUnderIdleGnome_IdlerDescends_NeverHangsOrAirCrawls()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var disp = ctx.ProdDispatcher!;
                var root = rootGo.transform;
                Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);

                // The digger, west of the site, work window pinned open.
                var spawn = FindFloorNear(nav, digTx * 8 - 12, digFloor.sy, 24);
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h0 = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var digger = ctx.GetEntity(h0);
                Assert.IsNotNull(digger?.GameObject, "no live digger");
                digger!.Owner = 0;
                interp.Eval($"set_worktime {digger.Handle} 0 12");

                // The bystander spawns beside the digger; once the dig opens an interior line
                // inside the pit (tiles ax..ax+1), he is parked on it, standing on the pit floor
                // while the first keeps carving. No work window — the idle/sparetime think layer
                // owns his reaction. Parking is harness scaffolding (transform+Position while
                // idle); the post-carve physics is the law under test.
                var spawn1 = FindFloorNear(nav, digTx * 8 - 18, digFloor.sy, 24);
                float gz1 = Mathf.Min(nav.GetHmapSub(spawn1.sx, spawn1.sy) + 1, 14.5f);
                string h1 = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn1.sx / 8f)} {Inv(spawn1.sy / 8f)} {Inv(gz1)}\" \"0 0 0\"").Trim();
                var idler = ctx.GetEntity(h1);
                Assert.IsNotNull(idler?.GameObject, "no live idler");
                idler!.Owner = 0;
                interp.Eval($"set_worktime {idler.Handle} 0 0");
                TestContext.WriteLine($"digger spawn=({spawn.sx / 8f:0.00},{spawn.sy / 8f:0.00},{gz:0.0}) " +
                    $"idler spawn=({spawn1.sx / 8f:0.00},{spawn1.sy / 8f:0.00},{gz1:0.0})");

                for (int i = 0; i < 120; i++) sim.Step(0.025);
                interp.Eval($"set_worktime {digger.Handle} 0 12");

                var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 8f);
                var marks = CommitMarquee(terr, ax, ay, bx, by);
                Assert.Greater(marks.Count, 3, "marquee painted too few cells");
                int marks0 = marks.Count;

                bool UnsupportedNow(Vector3 pn)
                {
                    int sx = Mathf.RoundToInt(pn.x * 8f), sy = Mathf.RoundToInt(pn.y * 8f);
                    if (nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1))
                        return false;
                    return (nav.SubAt(sx, sy) & NavWorld.SubWalk) == 0
                        || Mathf.Abs(pn.z - NavWorldService.SupportDepth(nav, sx, sy)) > 2.5f;
                }
                bool IsMover(object? act)
                {
                    string n = act?.GetType().Name ?? "";
                    return n.Contains("Walk") || n.Contains("Climb") || n.Contains("Sequence")
                        || n.Contains("Retry");
                }

                // Phase 1 — dig; once an interior line exists inside the pit, park the idler on
                // it; then keep digging until the carve removes his line — the trigger. He must
                // still be on his seat when it happens (the idle wander may drift him — re-park
                // while he is still floor-supported and un-triggered; pre-trigger positioning is
                // harness scaffolding, the post-trigger physics is the law under test).
                const double stepDt = 0.025;
                bool triggered = false, parked = false;
                Vector3 seatN = default;
                int step = 0, maxSteps = (int)(2500.0 / stepDt);
                for (; step < maxSteps && !triggered; step++)
                {
                    sim.Step(stepDt);
                    if (step % 40 == 0) disp.SweepNow(0);
                    if (digger.WorkLen < 11f) interp.Eval($"set_worktime {digger.Handle} 0 12");
                    if (!parked)
                    {
                        // the pit's topmost interior line (below the original surface rows)
                        (int sx, int sy) line = default;
                        for (int sy = ay * 8 + 4; sy < (ay + 9) * 8 && line == default; sy++)
                            for (int sx = ax * 8; sx < (ax + 2) * 8; sx++)
                                if (nav.SubIsFloor(sx, sy)) { line = (sx, sy); break; }
                        if (line != default && idler.GameObject != null)
                        {
                            float lz = Mathf.Min(nav.GetHmapSub(line.sx, line.sy - 1) + 1, 14.5f);
                            seatN = new Vector3(line.sx / 8f, line.sy / 8f, lz);
                            idler.GameObject.transform.position = W(seatN);
                            idler.Position = seatN;
                            parked = true;
                            TestContext.WriteLine($"step {step}: idler parked on the pit line " +
                                $"({seatN.x:0.00},{seatN.y:0.00},{seatN.z:0.00})");
                        }
                        continue;
                    }
                    Vector3 ip = idler.Position;
                    if (ClassifySupport(nav, ip) != Support.Floor && !IsMover(idler.ActiveAction))
                    {
                        triggered = true;
                        TestContext.WriteLine($"step {step}: idler support carved " +
                            $"(act={idler.ActiveAction?.GetType().Name ?? "-"} state={idler.CurrentState}) " +
                            $"pos=({ip.x:0.00},{ip.y:0.00},{ip.z:0.00})");
                        break;
                    }
                    // re-park a wandering idler while his seat still stands
                    if (!IsMover(idler.ActiveAction)
                        && (Mathf.Abs(ip.x - seatN.x) > 1.0f || Mathf.Abs(ip.y - seatN.y) > 1.0f)
                        && ClassifySupport(nav, seatN) == Support.Floor
                        && idler.GameObject != null)
                    {
                        idler.GameObject.transform.position = W(seatN);
                        idler.Position = seatN;
                    }
                    if (CountMarks(terr, marks) == 0) break;
                }
                if (!triggered)
                { Assert.Ignore($"the dig never carved the idler's seat (parked={parked}) — scenario void"); return; }

                // Diagnostics: the [WORKDBG] walk-build trace of the recovery (targets + paths).
                Diggles.Runtime.Diagnostics.DigglesDebugLog.MaxLines = 20000;
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "idler-workdbg.log"));
                DigglesTclEngine.WorkDebug = true;

                // Phase 2 — the recovery audit. Native envelope: the carve triggers one
                // idle-think latency (work_idle idletimeout>10, z_work_states.tcl:277 /
                // zwerg.tcl:313), then one recovery walk that ends supported (walks land on floor
                // lines by construction — FUN_0041d8a0's landing is band-resolved on the relaxed
                // floor cell; a climb ends on the wall seat or dismounts onto the boundary floor
                // cell). A walk that terminates unsupported mid-pit buys a whole extra idle cycle
                // per leg — in a deep pit that chains into repeated hangs. Law: the worst
                // continuous unsupported episode is at most one think latency plus the walk's own
                // transit (12.5 s), with no second long hang.
                double unsupTime = 0, worstUnsup = 0; float airDrift = 0, worstDrift = 0;
                int longEpisodes = 0, lossEvents = 0, arrivedUnsupported = 0;
                bool prevUnsup = true;   // the trigger left him unsupported
                Vector3 prev = idler.Position;
                var log = new StringBuilder();
                string prevAct = idler.ActiveAction?.GetType().Name ?? "-";
                string prevState = idler.CurrentState ?? "-";
                int prevWr = idler.WalkResult;
                int recovery = (int)(120.0 / stepDt);
                for (int k = 0; k < recovery; k++)
                {
                    sim.Step(stepDt);
                    if (k % 40 == 0) disp.SweepNow(0);
                    Vector3 p = idler.Position;
                    bool unsup = UnsupportedNow(p);
                    float dx = Mathf.Abs(p.x - prev.x);
                    // transition trace — who owns the body at each beat of the recovery
                    string act = idler.ActiveAction?.GetType().Name ?? "-";
                    string st = idler.CurrentState ?? "-";
                    bool wasMover = prevAct.Contains("Walk") || prevAct.Contains("Climb")
                        || prevAct.Contains("Sequence") || prevAct.Contains("Retry");
                    bool isMover = act.Contains("Walk") || act.Contains("Climb")
                        || act.Contains("Sequence") || act.Contains("Retry");
                    if ((act != prevAct || st != prevState || idler.WalkResult != prevWr) && log.Length < 6000)
                        log.AppendLine($"    k={k} act {prevAct}→{act} state {prevState}→{st} " +
                            $"wr={idler.WalkResult} pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) unsup={unsup}");
                    // The landing law: a walk chain that finishes as arrived (wr=4) ends the body
                    // supported — the FUN_0041d8a0 landing is band-resolved on a floor line and a
                    // climb ends on the wall seat / boundary floor cell. An arrived stamp at an
                    // unsupported spot buys an idle cycle mid-air.
                    if (wasMover && !isMover && idler.WalkResult == 4 && unsup)
                    {
                        arrivedUnsupported++;
                        int bsx = Mathf.RoundToInt(p.x * 8f), bsy = Mathf.RoundToInt(p.y * 8f);
                        if (log.Length < 6200)
                            log.AppendLine($"    k={k} *** ARRIVED-UNSUPPORTED at " +
                                $"({p.x:0.00},{p.y:0.00},{p.z:0.00}) cell=({bsx},{bsy}) " +
                                $"val={nav.SubAt(bsx, bsy) & 3} h(above)={nav.GetHmapSub(bsx, bsy - 1)} " +
                                $"h(own)={nav.GetHmapSub(bsx, bsy)} h(below)={nav.GetHmapSub(bsx, bsy + 1)} " +
                                $"support={NavWorldService.SupportDepth(nav, bsx, bsy)} ***");
                        if (arrivedUnsupported == 1)
                            DumpWindow(nav, bsx / 8 - 1, bsx / 8 + 1, bsy / 8 - 1, bsy / 8 + 1);
                    }
                    if (unsup && !prevUnsup && !isMover) lossEvents++;   // a fresh carve took his floor
                    prevAct = act; prevState = st; prevWr = idler.WalkResult; prevUnsup = unsup;
                    if (unsup)
                    {
                        unsupTime += stepDt;
                        if (ClassifySupport(nav, p) == Support.Air) airDrift += dx;
                    }
                    else
                    {
                        if (unsupTime > worstUnsup) worstUnsup = unsupTime;
                        if (unsupTime > 2.0) longEpisodes++;
                        if (airDrift > worstDrift) worstDrift = airDrift;
                        unsupTime = 0; airDrift = 0;
                    }
                    prev = p;
                }
                if (unsupTime > worstUnsup) worstUnsup = unsupTime;
                if (unsupTime > 2.0) longEpisodes++;
                if (airDrift > worstDrift) worstDrift = airDrift;
                DigglesTclEngine.WorkDebug = false;
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled = false;
                bool endUnsup = UnsupportedNow(idler.Position);
                TestContext.WriteLine($"RESULT idler: worstUnsup={worstUnsup:0.0}s longEpisodes={longEpisodes} " +
                    $"lossEvents={lossEvents} arrivedUnsupported={arrivedUnsupported} " +
                    $"worstAirDrift={worstDrift:0.00} endUnsup={endUnsup} " +
                    $"end=({idler.Position.x:0.00},{idler.Position.y:0.00},{idler.Position.z:0.00}) " +
                    $"state={idler.CurrentState} | digger end=({digger.Position.x:0.00},{digger.Position.y:0.00}," +
                    $"{digger.Position.z:0.00}) marks {marks0}→{CountMarks(terr, marks)}");
                if (log.Length > 0) TestContext.WriteLine("idler recovery transitions:\n" + log);

                Assert.LessOrEqual(worstUnsup, 12.5,
                    "the idler hung unsupported past ONE idle-think latency + walk transit — the " +
                    "native recovery is one think (≤10 s) + one walk that ends supported (the Play " +
                    "'hangs in the air, no reaction')");
                Assert.AreEqual(0, arrivedUnsupported,
                    "a recovery walk stamped ARRIVED (wr=4) at an UNSUPPORTED spot — the native " +
                    "landing law ends every arrived walk on a floor line or wall seat; an arrived " +
                    "mid-air stamp buys an extra idle hang per leg (the Play 'hangs, crawls, " +
                    "hangs again')");
                Assert.LessOrEqual(longEpisodes, System.Math.Max(1, lossEvents + 1),
                    "more long unsupported episodes than support-loss events — recovery walks are " +
                    "terminating mid-air and buying extra idle cycles");
                Assert.LessOrEqual(worstDrift, 0.5f,
                    "the idler's recovery translated him laterally through DEAD AIR — the Play " +
                    "'crawls through the air to the wall'");
                Assert.IsFalse(endUnsup, "the idler is still unsupported 120 s after the carve");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // Marked-cell carve-to-completion (the CaveDigRepro recipe): CarveStep only advances on
        // marked+exposed tiles but walks the full block-transition chain — the way to open a deep
        // shaft in EditMode (CarveTileNow alone stops at one rim step per tile).
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

        static (int sx, int sy) FindMeadowAnchor(NavWorld nav)
        {
            for (int sx = 238 * 8; sx < 252 * 8; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        static void DumpPath(List<NavWaypoint> path, Transform root, NavWorld nav)
        {
            foreach (var wp in path)
            {
                Vector3 n = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(wp.Pos));
                int sx = Mathf.RoundToInt(n.x * 8f), sy = Mathf.RoundToInt(n.y * 8f);
                TestContext.WriteLine($"    {(wp.IsWall ? "WALL " : "floor")} native=({n.x:0.00},{n.y:0.00},{n.z:0.00}) " +
                    $"sub=({sx},{sy}) cellVal={nav.SubAt(sx, sy) & 3}");
            }
        }

        [Test]
        public void ShaftDigUnderSelf_DiggerNeverAirCrawls_AndRemountsAfterSupportCarve()
            => RunShaftDigUnderSelf(stepDt: 0.1);

        // Same scenario at the runtime sim cadence: the sim steps at a fixed 0.025 s
        // (EngineTickService.SimStep — fast-forward runs more steps, never a bigger dt), so state
        // thinks fire every 4th step and the walk/climb executors advance in 4× finer slices than
        // the 0.1-stepped run above. The support-loss reaction path (probe miss count, retry
        // replans, think-gated dig_continue) interleaves differently at this cadence.
        [Test]
        public void ShaftDigUnderSelf_PlayCadence40Hz_DiggerNeverAirCrawls()
            => RunShaftDigUnderSelf(stepDt: 0.025);

        void RunShaftDigUnderSelf(double stepDt)
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var disp = ctx.ProdDispatcher!;

                // Dig site = the earth chimney (the only native-diggable surface column).
                var (digTx, digFloor) = FindDiggableSurfaceColumn(terr, nav);
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column");

                // One gnome near the site; pin his work window open.
                var spawn = FindFloorNear(nav, digTx * 8 - 12, digFloor.sy, 24);
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                interp.Eval($"set_worktime {g.Handle} 0 12");
                for (int i = 0; i < (int)(3.0 / stepDt); i++) sim.Step(stepDt);
                interp.Eval($"set_worktime {g.Handle} 0 12");

                // A deep vertical shaft straight down the chimney. Depth matters: near the
                // surface the meadow bowl keeps the open band generous; the failure geometry is a
                // deep enclosed shaft where the walkable ribbon narrows to the centre columns (the
                // 31-cell EFFPASSWALK fan hits the side walls).
                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 16f);
                var marks = CommitMarquee(terr, ax, ay, bx, by);
                Assert.Greater(marks.Count, 3, "marquee painted too few cells");
                int marks0 = marks.Count;

                // ---- per-step trajectory audit ----
                var animCtrl = g.GameObject!.GetComponentInChildren<DigglesAnimController>();
                string prevClip = "";
                Vector3 prev = g.Position;
                Support prevSup = ClassifySupport(nav, prev);
                double airTime = 0, worstAirTime = 0;      // consecutive dead-air seconds
                float airDriftX = 0, worstAirDriftX = 0;   // lateral native X accumulated in dead air
                double seatBadTime = 0, worstSeatBadTime = 0;   // supported but z far off the lawful seat
                float worstSeatErr = 0;
                int transitions = 0;
                var worstLog = new StringBuilder();
                var episodeLog = new StringBuilder();
                var seatLog = new StringBuilder();
                int stepsDone = 0;
                int maxSteps = (int)(3000.0 / stepDt);          // 3000 s of sim at any cadence
                int sweepEvery = Mathf.Max(1, (int)(1.0 / stepDt));
                int reportEvery = Mathf.Max(1, (int)(40.0 / stepDt));

                for (int step = 0; step < maxSteps; step++)
                {
                    sim.Step(stepDt);
                    stepsDone = step + 1;
                    if (step % sweepEvery == 0) disp.SweepNow(0);
                    if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");

                    Vector3 p = g.Position;
                    Support sup = ClassifySupport(nav, p);
                    float dx = Mathf.Abs(p.x - prev.x);

                    if (sup == Support.Air)
                    {
                        airTime += stepDt;
                        airDriftX += dx;
                        if (episodeLog.Length < 4000 && (dx > 0.005f || airTime < 0.15))
                            episodeLog.AppendLine($"    step {step} AIR pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) " +
                                $"dx={dx:0.000} cell={(nav.SubAt(Mathf.RoundToInt(p.x * 8f), Mathf.RoundToInt(p.y * 8f)) & 3)}");
                    }
                    else
                    {
                        if (airTime > worstAirTime)
                        {
                            worstAirTime = airTime;
                            worstLog.Clear(); worstLog.Append(episodeLog);
                        }
                        if (airDriftX > worstAirDriftX) worstAirDriftX = airDriftX;
                        airTime = 0; airDriftX = 0; episodeLog.Clear();

                        // Depth-seat audit: a supported body whose z sits far off the lawful seat
                        // renders floating off the wall face / inside the rock even though (x,y)
                        // is on-nav.
                        float seat = LawfulSeatZ(nav, p, sup);
                        float err = Mathf.Abs(p.z - seat);
                        if (err > 2.5f)
                        {
                            seatBadTime += stepDt;
                            if (err > worstSeatErr) worstSeatErr = err;
                            if (seatLog.Length < 3000 && seatBadTime < 1.0)
                                seatLog.AppendLine($"    step {step} {sup} pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) " +
                                    $"seat={seat:0.0} err={err:0.0}");
                        }
                        else
                        {
                            if (seatBadTime > worstSeatBadTime) worstSeatBadTime = seatBadTime;
                            seatBadTime = 0;
                        }
                    }
                    if (sup != prevSup)
                    {
                        transitions++;
                        if (transitions <= 80)
                            TestContext.WriteLine($"step {step}: {prevSup}→{sup} " +
                                $"pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) marksLeft={CountMarks(terr, marks)}");
                        prevSup = sup;
                    }
                    // Pose audit: which clip plays while off the floor (the visible "snap to the
                    // wall" is the kletter/digclimb pose family — z_dignwalk dig_anim wall branch,
                    // set_idle_anim's kletterstand). A ground/stand clip while wall-borne looks
                    // like standing in mid-air even when (x,y,z) is lawful.
                    string clip = animCtrl != null ? animCtrl.CurrentClip : "";
                    if (clip != prevClip && sup != Support.Floor && transitions <= 80)
                        TestContext.WriteLine($"step {step}: [{sup}] clip → {clip}");
                    prevClip = clip;
                    prev = p;

                    if (step % reportEvery == 0)
                    {
                        g.VarsScope.Vars.TryGetValue("current_worktask", out var wt);
                        TestContext.WriteLine($"step {step}: wt={wt} state={g.CurrentState} sup={sup} " +
                            $"pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) marksLeft={CountMarks(terr, marks)}");
                    }
                    if (CountMarks(terr, marks) == 0) break;
                }
                // final open episode counts too
                if (airTime > worstAirTime) { worstAirTime = airTime; worstLog.Clear(); worstLog.Append(episodeLog); }
                if (airDriftX > worstAirDriftX) worstAirDriftX = airDriftX;
                if (seatBadTime > worstSeatBadTime) worstSeatBadTime = seatBadTime;

                int marksLeft = CountMarks(terr, marks);
                TestContext.WriteLine($"RESULT: steps={stepsDone} marks {marks0}→{marksLeft} " +
                    $"worstAirTime={worstAirTime:0.0}s worstAirDriftX={worstAirDriftX:0.00} " +
                    $"worstSeatBadTime={worstSeatBadTime:0.0}s worstSeatErr={worstSeatErr:0.0} " +
                    $"finalSup={ClassifySupport(nav, g.Position)} finalPos=({g.Position.x:0.00},{g.Position.y:0.00},{g.Position.z:0.00})");
                if (worstLog.Length > 0) TestContext.WriteLine("worst dead-air episode:\n" + worstLog);
                if (seatLog.Length > 0) TestContext.WriteLine("depth-seat violations:\n" + seatLog);

                // window dump on any anomaly — the class ribbon around the shaft explains why
                if (worstAirTime > 3.0 || worstAirDriftX > 0.5f || worstSeatBadTime > 3.0)
                    DumpWindow(nav, ax - 2, ax + 3, ay - 2, by + 2);

                Assert.AreEqual(0, marksLeft, "the dig order never completed — the digger stalled");
                // (1) the "crawls sideways through air" discriminator: while in dead air (cell
                // value 0) the native gnome never translates laterally — his only dead-air time is
                // the beat between the support carve and the next walk's climb records taking him.
                Assert.LessOrEqual(worstAirDriftX, 0.5f,
                    "the digger translated laterally through DEAD AIR (value-0 cells) — the Play " +
                    "'slides sideways through the air' symptom; the native rides climb records (wall) or floor lines only");
                // (2) the "hangs in the air" discriminator: unsupported time is bounded by the
                // dig loop's own cadence (carve → dig_continue → walk build ≈ a few tenths).
                Assert.LessOrEqual(worstAirTime, 3.0,
                    "the digger hung unsupported in dead air for seconds — the native mounts the wall " +
                    "(CAct_Climb from the now-wall start cell) as soon as the next dig walk plans");
                // (3) the depth-seat law: a supported body rides the lawful seat depth (standing
                // z = pocket+1 / wall z = support depth); a persistent multi-nibble error is a
                // Z-axis drift (floating in air or sinking into the wall).
                Assert.LessOrEqual(worstSeatBadTime, 3.0,
                    $"the body rode {worstSeatErr:0.0} nibbles off the lawful depth seat for seconds — " +
                    "the Play Z-drift (floats off the wall face / sinks into rock)");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }
    }
}
