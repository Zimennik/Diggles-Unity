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

    // ======================= Play items — full-stack repros =======================
    //
    // #11 during LONG climbs the body
    // gradually embeds INTO the wall along world +Z (native z sinking under the face
    // height nibble). Repro: the longest NATURAL (map-authored) climb ribbon on the start
    // map — records at varying z, unlike the flat carved-shaft probe — traced two ways:
    // A. a pure ClimbAction over the FindPath records (executor isolation);
    // B. the full Tcl `action walk` stack at the Play cadence (fixed 0.025 s sim steps,
    // EngineTickService.SimStep) with ≥30 s of accumulated wall time.
    // Metric: airGap = bodyZ(native) − faceH(body sub-cell). Rock occupies z < h, so
    // airGap < 0 means the body point is INSIDE the rock. The wall standoff law parks the
    // root at ≈ +0.5 native (0.25 wu) on the air side; a jagged natural face may dip the
    // lerp ≈ 1 native below a step edge (the native lerps records identically,
    // FUN_00418db0:123-125). What must NEVER happen is a TIME drift: airGap sliding down
    // as the climb runs.
    //
    // dig pacing + suspected extra hits per block): swing-by-
    // swing instrumentation of the ambient dig loop (WorkLog timestamps → scratch log),
    // compared against the native law: P(apply per swing) = digcount·material·0.7
    // (z_dignwalk.tcl:312-315, digcount = 0.5 for a fresh dwarf), swing period =
    // clipFrames/10 s (the anim deadline law), novices add whiff accident swings
    // (z_dignwalk.tcl:396-411).
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class ClimbSinkAndDigPaceTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        const float PlayStep = 0.025f;   // EngineTickService.SimStep — the Play cadence

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

        // Every maximal vertical run of climb-wall cells (the natural ribbons + any carved ones),
        // longest first. len is in sub-cells (8 = one tile).
        static List<(int sx, int syTop, int len)> ScanWallRibbons(NavWorld nav, int minLen)
        {
            var ribbons = new List<(int sx, int syTop, int len)>();
            for (int sx = 8; sx < nav.SubW - 8; sx++)
            {
                int run = 0;
                for (int sy = 8; sy < nav.SubH - 8; sy++)
                {
                    if (nav.SubIsClimbWall(sx, sy)) { run++; continue; }
                    if (run >= minLen) ribbons.Add((sx, sy - run, run));
                    run = 0;
                }
                if (run >= minLen) ribbons.Add((sx, nav.SubH - 8 - run, run));
            }
            ribbons.Sort((a, b) => b.len.CompareTo(a.len));
            return ribbons;
        }

        // The start map has exactly ONE natural climb ribbon (the eastern slope, tx≈257) and it has
        // NO floor at its base (dead-end slope) — so a rim→base loop is impossible. Instead: route
        // top-FLOOR → a WALL cell deep on the ribbon (a DELIBERATE wall goal ⇒ the gnome climbs down
        // and HANGS, kletterstand) and back UP to the floor. Repeating those two legs is a real,
        // map-authored climb we can run for ≥30 s. Returns the down/up world paths + native anchors,
        // choosing the DEEPEST wall goal that still routes ≥ minWallWps wall records both ways.
        static (System.Collections.Generic.List<NavWaypoint> down,
                System.Collections.Generic.List<NavWaypoint> up,
                Vector3 topFloorN, Vector3 wallGoalN)? RouteFloorWall(
            NavWorldService svc, NavWorld nav, Transform root, int minWallWps)
        {
            Vector3 W(Vector3 n) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(n));
            foreach (var rib in ScanWallRibbons(nav, minLen: 12))
            {
                var top = FindFloorNear(nav, rib.sx, rib.syTop - 2, 20);
                if (top == default) continue;
                float topZ = Mathf.Min(nav.GetHmapSub(top.sx, top.sy) + 1, 14.5f);
                var topN = new Vector3(top.sx * 0.125f, top.sy * 0.125f, topZ);
                for (int k = rib.len - 2; k >= 4; k--)
                {
                    int wgx = rib.sx, wgy = rib.syTop + k;
                    var wgN = new Vector3(wgx * 0.125f, wgy * 0.125f, NavWorldService.SupportDepth(nav, wgx, wgy));
                    var down = svc.FindPath(W(topN), W(wgN));
                    var up = svc.FindPath(W(wgN), W(topN));
                    if (down == null || up == null) continue;
                    int wd = 0; foreach (var wp in down) if (wp.IsWall) wd++;
                    int wu = 0; foreach (var wp in up) if (wp.IsWall) wu++;
                    if (wd >= minWallWps && wu >= minWallWps) return (down, up, topN, wgN);
                }
            }
            return null;
        }

        // ------------------------------------------------------------------------------------
        // Carved-shaft fallback for the climb repros: the start map's ONE natural ribbon (the
        // eastern slope) fails to route on some data revisions ("no routable natural climb
        // ribbon"), and the manual test reports (D3) are about CARVED shafts anyway. Dig a
        // marquee shaft below the meadow through the PRODUCTION carve chain (CarveStep on
        // exposed tiles per round — the dig_next serving order) and let ScanWallRibbons pick up
        // its wall ribbons. Same recipe as DigPlayReproTests.MarqueeShaft_*.
        // ------------------------------------------------------------------------------------
        static void CarveShaftBelowMeadow(DiggableTerrain terr, NavWorld nav, float dragTiles = 10f)
        {
            // Site = the EARTH CHIMNEY (x≈256..258): the rest of the meadow bowl is granite-
            // crusted (material type 0) where the M2 marker law drops every crust cell.
            int digTx = -1; (int sx, int sy) digFloor = default;
            for (int tx3 = 212; tx3 <= 266 && digTx < 0; tx3++)
            {
                var f = FindFloorNear(nav, tx3 * 8 + 4, 30 * 8, 20);
                if (f == default || f.sx / 8 != tx3) continue;
                int ty3 = f.sy / 8;
                if (terr.TileMaterialDiggable(tx3, ty3 + 1) && terr.TileMaterialDiggable(tx3, ty3 + 2))
                { digTx = tx3; digFloor = f; }
            }
            Assert.GreaterOrEqual(digTx, 0, "no native-diggable earth surface column for the shaft carve");
            var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
            var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + dragTiles);
            var marks = CommitMarquee(terr, ax, ay, bx, by);
            Assert.Greater(marks.Count, 3, "shaft marquee painted too few cells");
            int total = 0;
            for (int round = 0; round < 96; round++)
            {
                bool any = false;
                foreach (var (nx, ny) in marks)
                {
                    if (!terr.IsDigMarked(nx, ny, 0) || !terr.TileExposed(nx, ny)) continue;
                    while (terr.CarveStep(nx, ny, 0, out _)) { any = true; if (++total > 8192) break; }
                }
                if (!any) break;
            }
            terr.RevalidateDirtyNav();
            TestContext.WriteLine($"[SHAFT] carved marquee ({ax},{ay})..({bx},{by}) in {total} CarveSteps");
        }

        struct GapSample { public float t, gap, z, h; public int gx, gy; public bool wall; }

        // airGap of a WORLD body position: native z minus the face height nibble at the body's
        // sub-cell. Rock occupies z < h ⇒ negative gap = inside the rock.
        static GapSample Gap(NavWorld nav, Transform root, Vector3 worldPos, float t)
        {
            Vector3 n = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(worldPos));
            int gx = Mathf.RoundToInt(n.x * 8f), gy = Mathf.RoundToInt(n.y * 8f);
            int h = nav.GetHmapSub(gx, gy);
            return new GapSample
            {
                t = t, z = n.z, h = h, gap = n.z - h, gx = gx, gy = gy,
                wall = nav.SubIsClimbWall(gx, gy)
            };
        }

        static void ReportGapSeries(string tag, List<GapSample> wallSamples)
        {
            if (wallSamples.Count < 4) { TestContext.WriteLine($"[{tag}] too few wall samples ({wallSamples.Count})"); return; }
            float min = float.MaxValue, max = float.MinValue;
            foreach (var s in wallSamples) { if (s.gap < min) min = s.gap; if (s.gap > max) max = s.gap; }
            int q = Mathf.Max(1, wallSamples.Count / 4);
            float firstQ = 0f, lastQ = 0f;
            for (int i = 0; i < q; i++) firstQ += wallSamples[i].gap;
            for (int i = wallSamples.Count - q; i < wallSamples.Count; i++) lastQ += wallSamples[i].gap;
            firstQ /= q; lastQ /= q;
            var f0 = wallSamples[0]; var f1 = wallSamples[wallSamples.Count - 1];
            TestContext.WriteLine(
                $"[{tag}] wallSamples={wallSamples.Count} span={f1.t - f0.t:0.0}s " +
                $"gap first={f0.gap:0.00} last={f1.gap:0.00} min={min:0.00} max={max:0.00} " +
                $"firstQavg={firstQ:0.00} lastQavg={lastQ:0.00} drift={lastQ - firstQ:0.00} (native units; -=inside rock)");
        }

        // ---------------------------------------------------------------------------------------
        // #11-A — pure ClimbAction over a natural ribbon's records: the executor in isolation.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void NaturalWall_PureClimbExecutor_AirGapBounded()
        {
            var (rootGo, _, terr, nav) = BuildStartMeadow(classes: false);
            try
            {
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                var ribbons = ScanWallRibbons(nav, minLen: 12);
                TestContext.WriteLine($"ribbons(len≥12)={ribbons.Count}; longest:");
                for (int i = 0; i < Mathf.Min(10, ribbons.Count); i++)
                    TestContext.WriteLine($"  sx={ribbons[i].sx} syTop={ribbons[i].syTop} len={ribbons[i].len}");
                var route = RouteFloorWall(svc, nav, root, minWallWps: 3);
                if (route == null)
                {
                    // The natural eastern-slope ribbon didn't route — carve a shaft (the actual
                    // manual test D3 scenario) and route its wall ribbon instead.
                    CarveShaftBelowMeadow(terr, nav);
                    route = RouteFloorWall(svc, nav, root, minWallWps: 3);
                }
                Assert.IsNotNull(route, "no routable climb ribbon (natural NOR carved shaft)");
                var (downPath, upPath, topFloorN, wallGoalN) = route!.Value;
                TestContext.WriteLine($"topFloor={topFloorN} wallGoal={wallGoalN} " +
                    $"downWps={downPath.Count} upWps={upPath.Count}");

                // Build the ClimbAction wall RUN out of a routed leg (BuildWalkSegments slice:
                // bounding floor wps included) — the FIRST wall run of the leg.
                static List<NavWaypoint> WallRun(List<NavWaypoint> path)
                {
                    int si = 0; while (si < path.Count && !path[si].IsWall) si++;
                    if (si >= path.Count) return null!;
                    int sj = si; while (sj < path.Count && path[sj].IsWall) sj++;
                    var run = new List<NavWaypoint>();
                    if (si > 0) run.Add(path[si - 1]);
                    for (int k = si; k < sj; k++) run.Add(path[k]);
                    if (sj < path.Count) run.Add(path[sj]);
                    return run;
                }

                var go = new GameObject("climber");
                go.transform.SetParent(root.parent, false);   // world space, like a spawned entity
                var ctrl = go.AddComponent<DigglesAnimController>();
                var wallSamples = new List<GapSample>();
                float t = 0f;

                // Alternate DOWN / UP legs for ≥30 s of accumulated wall time — the LONG climb.
                bool down = true;
                for (int leg = 0; leg < 12 && t < 35f; leg++)
                {
                    var run = WallRun(down ? downPath : upPath);
                    Assert.IsNotNull(run, "leg has no wall run");
                    // Seed AT the standoff plane the executor parks on (base 0.25 wu + the record's
                    // face clearance), replicating ClimbAction — otherwise a leg that STARTS on a
                    // wall (the UP leg) samples the raw support record for the first ~2 s before the
                    // integrator ramps out to the standoff, a pure seeding transient (not an embed).
                    go.transform.position = run[0].IsWall
                        ? run[0].Pos + NavWorldService.WallOutward * (0.25f + run[0].Clearance * 0.5f)
                        : run[0].Pos;
                    var climb = new ClimbAction(ctrl, go.transform, run, 0, 1, 1);
                    int guard = 0, legIdx = 0; var legWall = new List<GapSample>();
                    while (climb.Tick(PlayStep) && guard++ < 20000)
                    {
                        t += PlayStep;
                        var smp = Gap(nav, root, go.transform.position, t);
                        // Skip the first few wall samples of a leg: a leg SEEDED on a wall (the UP
                        // leg) needs a couple ticks for the integrator to reach the record's own
                        // standoff cell, and the rounded sample cell can briefly disagree — a
                        // seeding transient, not an embed (the full-Tcl repro, which spawns + walks
                        // in, has none). The sustained body is what the airGap law is about.
                        if (smp.wall && ++legIdx > 8) { wallSamples.Add(smp); legWall.Add(smp); }
                    }
                    TestContext.WriteLine($"leg {leg} {(down ? "DOWN" : "UP")}: t={t:0.0}s wallSamples={legWall.Count}");
                    if (legWall.Count > 2) ReportGapSeries($"PURE-LEG{leg}", legWall);
                    down = !down;
                }
                TestContext.WriteLine($"climb finished after {t:0.0}s");
                ReportGapSeries("PURE", wallSamples);
                Object.DestroyImmediate(go);

                Assert.Greater(wallSamples.Count, 8, "climb never rode the wall");
                float minGap = float.MaxValue;
                foreach (var s in wallSamples) if (s.gap < minGap) minGap = s.gap;
                // The face-clearance standoff keeps the body OUT of the rock even at the ribbon's
                // depth step (pre-fix this dipped to −0.5..−1.0 native at the eastern-slope base).
                Assert.GreaterOrEqual(minGap, -0.2f, "body sank into the rock at a wall depth step");
                int q = Mathf.Max(1, wallSamples.Count / 4);
                float fq = 0f, lq = 0f;
                for (int i = 0; i < q; i++) fq += wallSamples[i].gap;
                for (int i = wallSamples.Count - q; i < wallSamples.Count; i++) lq += wallSamples[i].gap;
                Assert.LessOrEqual(Mathf.Abs(lq / q - fq / q), 1.0f,
                    "air gap DRIFTED over the climb — an accumulating writer in the executor");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // #11-B — the full Tcl walk stack at the Play cadence, ≥30 s of accumulated wall time.
        // ---------------------------------------------------------------------------------------
        [Test]
        public void NaturalWall_FullTclWalk_LongClimb_NoEmbedDrift()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var svc = rootGo.GetComponent<NavWorldService>()!;
                var root = rootGo.transform;
                var route = RouteFloorWall(svc, nav, root, minWallWps: 3);
                if (route == null)
                {
                    CarveShaftBelowMeadow(terr, nav);
                    route = RouteFloorWall(svc, nav, root, minWallWps: 3);
                }
                Assert.IsNotNull(route, "no routable climb ribbon (natural NOR carved shaft)");
                var (_, _, topFloorN, wallGoalN) = route!.Value;
                TestContext.WriteLine($"topFloor={topFloorN} wallGoal={wallGoalN}");

                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(topFloorN.x)} {Inv(topFloorN.y)} {Inv(topFloorN.z)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                for (int i = 0; i < 20; i++) sim.Step(PlayStep);          // let obj_init settle
                interp.Eval($"state_disable {h}");                        // AI must not steal the walk

                var tf = g.GameObject!.transform;
                // The RENDER layer: the inner Model animator. In EditMode nothing ticks the anim
                // clock (no Play loop), so we drive TickClock manually every sim step — otherwise the
                // pose freezes on frame 0 and the VAT morph (hypothesis c) is never exercised. The
                // rendered body centroid = the current frame mesh's bounds centre in world.
                var anim = g.GameObject!.GetComponentInChildren<DigglesAnimator>();
                var ctrl = g.GameObject!.GetComponentInChildren<DigglesAnimController>();
                var wallSamples = new List<GapSample>();
                var renderRel = new List<float>();   // rendered-centroid native z − body transform native z
                var legReports = new StringBuilder();
                float totalWallTime = 0f, t = 0f;
                int leg = 0;
                bool down = true;

                float RenderZ()
                {
                    var fm = anim != null ? anim.CurrentFrameMesh() : null;
                    if (fm == null || anim == null) return float.NaN;
                    Vector3 wc = anim.transform.TransformPoint(fm.bounds.center);
                    return RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(wc)).z;
                }

                while (totalWallTime < 32f && leg < 16)
                {
                    Vector3 target = down ? wallGoalN : topFloorN;
                    interp.Eval($"action {h} walk \"-target {{{Inv(target.x)} {Inv(target.y)} {Inv(target.z)}}} " +
                                 "-animsets {0 1} -speedtype 1\"");
                    int steps = 0;
                    var legWall = new List<GapSample>();
                    while (g.WalkResult == 2 && steps++ < 8000)
                    {
                        sim.Step(PlayStep);
                        if (anim != null) anim.TickClock(PlayStep);       // morph the pose (EditMode)
                        if (ctrl != null) ctrl.TickChain(PlayStep);
                        t += PlayStep;
                        var smp = Gap(nav, root, tf.position, t);
                        if (smp.wall)
                        {
                            legWall.Add(smp); wallSamples.Add(smp); totalWallTime += PlayStep;
                            float rz = RenderZ();
                            if (!float.IsNaN(rz)) renderRel.Add(rz - smp.z);
                        }
                    }
                    var endN = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(tf.position));
                    legReports.AppendLine($"leg {leg} {(down ? "DOWN" : "UP")}: result={g.WalkResult} steps={steps} " +
                        $"end=({endN.x:0.00},{endN.y:0.00},{endN.z:0.00}) wallSamples={legWall.Count} clip={anim?.CurrentAnimName}");
                    if (legWall.Count > 2) ReportGapSeries($"LEG{leg}", legWall);
                    down = !down;
                    leg++;
                    for (int i = 0; i < 20; i++)
                    { sim.Step(PlayStep); if (anim != null) anim.TickClock(PlayStep); t += PlayStep; }
                }
                TestContext.WriteLine(legReports.ToString());
                ReportGapSeries("FULL", wallSamples);

                // RENDER drift diagnosis (hypothesis b/c): does the drawn body centroid slide relative
                // to the sim body over the long climb? A monotonic slide here = a render/pose
                // accumulator invisible to the transform. Reported, then asserted bounded.
                if (renderRel.Count >= 8)
                {
                    int rq = Mathf.Max(1, renderRel.Count / 4);
                    float rf = 0f, rl = 0f, rmin = float.MaxValue, rmax = float.MinValue;
                    for (int i = 0; i < rq; i++) rf += renderRel[i];
                    for (int i = renderRel.Count - rq; i < renderRel.Count; i++) rl += renderRel[i];
                    foreach (var v in renderRel) { if (v < rmin) rmin = v; if (v > rmax) rmax = v; }
                    TestContext.WriteLine($"[RENDERREL] samples={renderRel.Count} " +
                        $"firstQ={rf / rq:0.000} lastQ={rl / rq:0.000} drift={rl / rq - rf / rq:0.000} " +
                        $"min={rmin:0.000} max={rmax:0.000} (native z: render centroid − sim body)");
                    Assert.LessOrEqual(Mathf.Abs(rl / rq - rf / rq), 1.0f,
                        "RENDERED body centroid DRIFTED vs the sim body over the climb — a render/pose accumulator");
                }
                else TestContext.WriteLine($"[RENDERREL] too few render samples ({renderRel.Count}) — mesh unavailable headless");

                Assert.Greater(wallSamples.Count, 50, "the walk never climbed — no wall time accumulated");
                float minGap = float.MaxValue;
                foreach (var s in wallSamples) if (s.gap < minGap) minGap = s.gap;
                // Face-clearance standoff: the body stays OUT of the rock the whole climb, incl. the
                // ribbon's depth step (pre-fix min dipped to −0.55 native at the eastern-slope base).
                Assert.GreaterOrEqual(minGap, -0.2f, "body sank into the rock at a wall depth step");
                int q = Mathf.Max(1, wallSamples.Count / 4);
                float fq = 0f, lq = 0f;
                for (int i = 0; i < q; i++) fq += wallSamples[i].gap;
                for (int i = wallSamples.Count - q; i < wallSamples.Count; i++) lq += wallSamples[i].gap;
                Assert.LessOrEqual(Mathf.Abs(lq / q - fq / q), 1.0f,
                    "air gap DRIFTED across 30+ s of climbing — an accumulating writer above the executor");
            }
            finally { Object.DestroyImmediate(rootGo); }
        }

        // ---------------------------------------------------------------------------------------
        // Dig pacing — swing-by-swing audit of the ambient dig loop vs the native law.
        // ---------------------------------------------------------------------------------------
        static bool CellAllowed(DiggableTerrain t, int tx, int ty)
            => DigMarquee.CellValid(tx, ty, t.Width, t.Height,
                (x, y) => t.NwCornerHeight(x, y) > 1,
                (x, y) => (t.RawDigNibble(x, y, 0) & 8) != 0,
                (x, y) => t.TileMaterialDiggable(x, y));   // M2 material band (FUN_00566870:26-28)

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

        static (int sx, int sy) FindMeadowAnchor(NavWorld nav)
        {
            for (int sx = 238 * 8; sx < 252 * 8; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) return (sx, sy);
            return default;
        }

        static int CountMarks(DiggableTerrain terr, List<(int tx, int ty)> marks)
        {
            int n = 0;
            foreach (var (mtx, mty) in marks) if (terr.IsDigMarked(mtx, mty, 0)) n++;
            return n;
        }

        [Test]
        public void DigPace_SwingAudit_VsNativeLaw()
        {
            string logPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "diggles_digpace_audit.log");
            Diggles.Runtime.Diagnostics.DigglesDebugLog.MaxLines = 400000;
            Diggles.Runtime.Diagnostics.DigglesDebugLog.Begin(logPath);
            DigglesTclEngine.WorkDebug = true;

            var (rootGo, engine, terr, nav) = BuildStartMeadow(classes: true);
            var interp = engine.Interp; var ctx = engine.Ctx; var sim = engine.Sim;
            try
            {
                ctx.LocalPlayer = 0;
                var disp = ctx.ProdDispatcher!;

                // The dig site must be NATIVE-DIGGABLE: material type 1|2 under the floor line.
                // The start-meadow bowl is LINED WITH A STONE CRUST (unq_start.pmp raw byte 4 →
                // type 0 at rows 31-32 across x≈212..254) — there the native marker painter
                // refuses (FUN_005672d0:41-44) and dig_execute's P = digcount·0·0.7 = 0, so the
                // old FindMeadowAnchor+4 site measured NOTHING (run material=0,
                // 0 nodes carved). The ONLY earth chimney reaching the meadow surface is the
                // eastern-slope column band x≈256..258 (raw 2 → type 2 from row ~29 down) —
                // dig there, like the original forces the player to.
                int digTx = -1; (int sx, int sy) digFloor = default;
                for (int tx3 = 212; tx3 <= 266 && digTx < 0; tx3++)
                {
                    var f = FindFloorNear(nav, tx3 * 8 + 4, 30 * 8, 20);
                    if (f == default || f.sx / 8 != tx3) continue;
                    int ty3 = f.sy / 8;
                    int m1 = terr.GetMaterial(tx3, ty3 + 1), m2 = terr.GetMaterial(tx3, ty3 + 2);
                    if ((m1 == 1 || m1 == 2) && (m2 == 1 || m2 == 2)) { digTx = tx3; digFloor = f; }
                }
                Assert.GreaterOrEqual(digTx, 0, "no native-diggable (earth) surface column on the start meadow");
                TestContext.WriteLine($"[SITE] earth chimney at tx={digTx} floor=({digFloor.sx},{digFloor.sy}) " +
                    $"mat below: {terr.GetMaterial(digTx, digFloor.sy / 8 + 1)}/{terr.GetMaterial(digTx, digFloor.sy / 8 + 2)}");

                var spawn = FindFloorNear(nav, digTx * 8 - 12, digFloor.sy, 24);
                float gz = Mathf.Min(nav.GetHmapSub(spawn.sx, spawn.sy) + 1, 14.5f);
                string h = interp.Eval(
                    $"new Zwerg \"\" \"{Inv(spawn.sx / 8f)} {Inv(spawn.sy / 8f)} {Inv(gz)}\" \"0 0 0\"").Trim();
                var g = ctx.GetEntity(h);
                Assert.IsNotNull(g?.GameObject, "no live gnome");
                g!.Owner = 0;
                interp.Eval($"set_worktime {g.Handle} 0 12");
                for (int i = 0; i < 30; i++) sim.Step(0.1);
                interp.Eval($"set_worktime {g.Handle} 0 12");

                var (ax, ay) = DigMarquee.SnapAnchor(digTx, digFloor.sy / 8f);
                var (bx2, by2) = DigMarquee.SnapDragCorner(ax, ay, ax, ay + 5f);
                var marks = CommitMarquee(terr, ax, ay, bx2, by2);
                Assert.Greater(marks.Count, 0, "marquee painted nothing");
                int marks0 = marks.Count;

                // Law inputs: fresh exp_Stein + the material CLASS at the deepest mark.
                // get_material = tile+0x18 & 3 (FUN_00630bb0:93-94) — an INT class 0..3 from the
                // L2M/PMP raw terrain byte (FUN_005992b0: raw 2→2 soft, 3→1 hard, 4→0 rock,
                // 5→3 granite/meadow band). unq_start.pmp histogram: the diggable underground
                // earth is raw 2 ⇒ class 2 ⇒ P(apply/swing) = 0.5·2·0.7 = 0.7 for a fresh dwarf.
                string expStein = interp.Eval($"get_attrib {h} exp_Stein").Trim();
                var lawMark = marks[marks.Count - 1];   // deepest row — unambiguously earth
                string mat = interp.Eval($"get_material {{{lawMark.tx} {lawMark.ty} 11}}").Trim();
                TestContext.WriteLine($"[LAW] exp_Stein={expStein} material={mat} " +
                    "digcount=max(0.5,sqrt(exp·0.5·10)) P=digcount·mat·0.7");

                var clearSteps = new List<int>();
                var clipLen = new Dictionary<string, int>();
                int lastMarks = marks0;
                var anim = g.GameObject!.GetComponentInChildren<DigglesAnimator>();
                // D2 sampling (down-shaft digger drifts to the AIR side — native z
                // INCREASING / world −Z toward the camera — and ends HANGING off the front face).
                // Airborne:= body cell is not a climb wall AND no FLOOR bit within ±1 rows of the
                // body sub-cell (the get_gnomeposition law, FUN_0068d8a0:84-98).
                var bodyTf = g.GameObject!.transform;
                var airborne = new List<bool>();
                var bodyZ = new List<float>();
                for (int step = 0; step < 9000; step++)
                {
                    sim.Step(0.1);
                    if (step % 10 == 0) disp.SweepNow(0);
                    if (g.WorkLen < 11f) interp.Eval($"set_worktime {g.Handle} 0 12");
                    if (anim != null && anim.CurrentAnimName.Length > 0)
                        clipLen[anim.CurrentAnimName] = anim.CurrentFrameCount;
                    Vector3 nb = RuntimeWorldSpawner.LocalToNative(
                        rootGo.transform.InverseTransformPoint(bodyTf.position));
                    int bx = Mathf.RoundToInt(nb.x * 8f), byy = Mathf.RoundToInt(nb.y * 8f);
                    if (bx > 0 && byy > 0 && bx < nav.SubW - 1 && byy < nav.SubH - 1)
                    {
                        bool wall = nav.SubIsClimbWall(bx, byy);
                        bool floorNear =
                            ((nav.SubAt(bx, byy + 1) | nav.SubAt(bx, byy) | nav.SubAt(bx, byy - 1))
                             & Diggles.Runtime.Navigation.NavWorld.SubFloor) != 0;
                        airborne.Add(!wall && !floorNear);
                        bodyZ.Add(nb.z);
                    }
                    int m = CountMarks(terr, marks);
                    while (m < lastMarks) { clearSteps.Add(step); lastMarks--; }
                    if (m == 0) break;
                }
                int marksLeft = CountMarks(terr, marks);
                TestContext.WriteLine($"RESULT: marks {marks0}→{marksLeft}, nodes cleared at steps: " +
                    string.Join(" ", clearSteps));
                Assert.Less(marksLeft, marks0, "the gnome never carved a node — no pacing data");

                // ---- D2 verdict: no persistent hang off the front face ----
                if (airborne.Count >= 80)
                {
                    int air = 0; foreach (var a2 in airborne) if (a2) air++;
                    int tail = 40, tailAir = 0;
                    for (int i2 = airborne.Count - tail; i2 < airborne.Count; i2++) if (airborne[i2]) tailAir++;
                    TestContext.WriteLine($"[D2] samples={airborne.Count} airborne={air} " +
                        $"({100f * air / airborne.Count:0.0}%) tail{tail}={tailAir} " +
                        $"z first={bodyZ[0]:0.00} last={bodyZ[bodyZ.Count - 1]:0.00}");
                    Assert.Less(tailAir, tail,
                        "D2: the digger ENDED hanging in the air off the front face (persistent airborne tail)");
                }

                // Per-node pacing (sim-seconds).
                if (clearSteps.Count > 1)
                {
                    float avg = (clearSteps[clearSteps.Count - 1] - clearSteps[0]) * 0.1f
                              / Mathf.Max(1, clearSteps.Count - 1);
                    TestContext.WriteLine($"[PACING] first node at {clearSteps[0] * 0.1f:0.0}s; " +
                        $"avg {avg:0.00} s/node over {clearSteps.Count} nodes");
                }

                // Dig-relevant clip lengths seen during the run (frames; period = frames/10 s).
                var sbClips = new StringBuilder("[CLIPS] ");
                foreach (var kv in clipLen)
                    if (kv.Key.IndexOf("hack", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || kv.Key.IndexOf("dig", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || kv.Key.IndexOf("kletter", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        sbClips.Append($"{kv.Key}={kv.Value}f ");
                TestContext.WriteLine(sbClips.ToString());

                // ---- swing timeline from the WORKDBG log ----
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                var swings = new List<(double t, string clip)>();
                var walks = new List<double>();
                foreach (var line in System.IO.File.ReadLines(logPath))
                {
                    if (!line.Contains($"#{g.Handle} ") || !line.Contains("action SET verb=")) continue;
                    int ti = line.IndexOf("t=", System.StringComparison.Ordinal);
                    if (ti < 0) continue;
                    int te = line.IndexOf(' ', ti);
                    if (!double.TryParse(line.Substring(ti + 2, te - ti - 2), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double tt)) continue;
                    if (line.Contains("verb='anim'"))
                    {
                        int ci = line.IndexOf("clip='", System.StringComparison.Ordinal);
                        string clip = "";
                        if (ci >= 0)
                        {
                            int ce = line.IndexOf('\'', ci + 6);
                            if (ce > ci) clip = line.Substring(ci + 6, ce - ci - 6);
                        }
                        swings.Add((tt, clip));
                    }
                    else if (line.Contains("verb='walk'")) walks.Add(tt);
                }
                TestContext.WriteLine($"[SWINGS] anim actions={swings.Count} walks={walks.Count} (log: {logPath})");
                var digSwings = new List<(double t, string clip)>();
                foreach (var s in swings)
                    if (s.clip.IndexOf("dig", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || s.clip.IndexOf("hack", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || s.clip.IndexOf("airhamm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        digSwings.Add(s);
                TestContext.WriteLine($"[SWINGS] dig swings={digSwings.Count} over {clearSteps.Count} nodes " +
                    $"⇒ {(clearSteps.Count > 0 ? (float)digSwings.Count / clearSteps.Count : 0f):0.00} swings/node");

                // ---- D1 verdict: measured swings/node vs the NATIVE pace law ----
                // digattr = exp_Stein, pickaxe halves it (z_dignwalk.tcl:307);
                // digcount = max(0.5, sqrt(digattr·10)) (:312; tttinfluence_dig=10,
                // techtreetunes.tcl:1357);
                // P(apply per swing) = digcount·mat·0.7 (:315; mat = INT class,
                // FUN_00630bb0:93-94);
                // applies/node = 2 for the marquee dummy target 11 (rim 15→11→7: carve gate
                // target<=rim FUN_00596b80:63, completion sweep rim<target:152-155);
                // novice whiff: digattr<0.2 ⇒ one accident anim precedes EVERY real swing
                // (:396-411) and both match the clip filter ⇒ expected total = 4/P per node
                // (2/P if accident clips don't resolve in this rig).
                if (clearSteps.Count > 1 && digSwings.Count > 0
                    && int.TryParse(mat, NumberStyles.Integer, CultureInfo.InvariantCulture, out int matI)
                    && float.TryParse(expStein, NumberStyles.Float, CultureInfo.InvariantCulture, out float expF))
                {
                    Assert.That(matI == 1 || matI == 2,
                        $"start-meadow earth must read a DIGGABLE material class (1|2), got {matI}");
                    float digcount = Mathf.Max(0.5f, Mathf.Sqrt(expF * 0.5f * 10f));
                    float P = digcount * matI * 0.7f;
                    float perNode = (float)digSwings.Count / clearSteps.Count;
                    float lo = 2f / P, hi = 4f / P;
                    TestContext.WriteLine($"[D1] P={P:0.###} expected swings/node {lo:0.0}(real)–{hi:0.0}(+whiffs); " +
                        $"measured {perNode:0.00}");
                    Assert.LessOrEqual(perNode, hi * 1.6f,
                        "D1: too MANY swings per node vs the native law (pace regression)");
                    Assert.GreaterOrEqual(perNode, lo * 0.4f,
                        "D1: implausibly few swings per node — law inputs degraded");
                }
                var gaps = new StringBuilder("[SWING Δt] ");
                var gapHist = new Dictionary<double, int>();
                for (int i = 1; i < digSwings.Count; i++)
                {
                    double dt = System.Math.Round(digSwings[i].t - digSwings[i - 1].t, 2);
                    // gaps spanning a walk leg are re-walks, not dead time — skip them
                    bool spansWalk = false;
                    foreach (var wt in walks)
                        if (wt > digSwings[i - 1].t && wt <= digSwings[i].t) { spansWalk = true; break; }
                    if (spansWalk) continue;
                    gapHist.TryGetValue(dt, out int c); gapHist[dt] = c + 1;
                }
                foreach (var kv in gapHist) gaps.Append($"{kv.Key:0.00}s×{kv.Value} ");
                TestContext.WriteLine(gaps.ToString());
                var first30 = new StringBuilder("[SWING LIST] ");
                for (int i = 0; i < Mathf.Min(30, digSwings.Count); i++)
                    first30.Append($"{digSwings[i].t:0.0}:{digSwings[i].clip} ");
                TestContext.WriteLine(first30.ToString());
            }
            finally
            {
                DigglesTclEngine.WorkDebug = false;
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Flush();
                Diggles.Runtime.Diagnostics.DigglesDebugLog.Enabled = false;
                Object.DestroyImmediate(rootGo);
            }
        }
    }
}
