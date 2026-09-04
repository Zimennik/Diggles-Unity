#nullable enable

namespace Diggles.Runtime.Navigation
{
    using System.Collections.Generic;
    using Diggles.Runtime.World;
    using UnityEngine;

    // One path waypoint tagged with whether it sits on a climbable wall (vs a walkable floor),
    // so the movement actions can switch walk ↔ climb.
    public readonly struct NavWaypoint
    {
        public readonly Vector3 Pos;
        public readonly bool IsWall;
        public readonly Vector3 Normal;   // wall facing (zero for floors)
        // Extra outward clearance (native depth nibbles) the climb body must stand off beyond the
        // faithful record, so the rendered/posed body clears the local wall face at a depth step —
        // where the cell's own height rises above the FUN_004238e0 support depth the record rides.
        // 0 on flat faces (the common case) and every floor waypoint. Consumed by ClimbAction's
        // WallStandoff; the record Pos itself stays faithful.
        public readonly float Clearance;
        public readonly NavTransportKind TransportKind;
        public readonly int TransportSourceHandle;
        public bool IsLadder => TransportKind == NavTransportKind.Ladder;
        public bool IsElevator => TransportKind == NavTransportKind.Elevator;
        public NavWaypoint(
            Vector3 pos,
            bool isWall,
            Vector3 normal = default,
            float clearance = 0f,
            NavTransportKind transportKind = NavTransportKind.None,
            int transportSourceHandle = -1)
        {
            Pos = pos;
            IsWall = isWall;
            Normal = normal;
            Clearance = clearance;
            TransportKind = transportKind;
            TransportSourceHandle = transportSourceHandle;
        }
    }

    // Scene host for the pathfinder: Calc1 (tile-level Bellman-Ford on the FUN_005a6350
    // graph) over NavWorld (the +0x3c sub-grid + per-sub-cell depth heights). Lives on the WorldRoot
    // next to DiggableTerrain; replaces the retired mesh-sampled NavGraphService.
    //
    // FindPath takes/returns world-space points (the callers deal in transforms). Internally
    // everything is native: world → local (InverseTransformPoint) → native (LocalToNative) →
    // sub-cells (×8, Z dropped — the float wrapper FUN_0059d440), then Calc1, then the polyline is
    // assembled run-by-run:
    //
    // Floor runs → FloorPathMap (FUN_0041aac0/0041d8a0): densify the run polyline, stamp the
    // per-column standable depth bands, LOS/5-pass-flood, settle — this resolves where in
    // depth the dwarf walks (the grid's bands are empty on wall faces, so it can never route
    // a climb).
    // Wall runs → CAct_Climb's record mirror (FUN_0040bb70, climb-exec.md §1.2): the Calc1
    // sub-cells themselves at ×0.125, each at the FUN_004238e0 support depth (one row up,
    // with the undug-15 overhang fix). No flood, no densify, no [n-2]→[n] z copy (dead branch).
    //
    // Waypoints are classified per the original surface classifier (FUN_00423730): +0x3c floor
    // bit ⇒ floor, else wall. A floor inside a ladder influence keeps only that transport/source
    // tag so the separate FUN_0060bc70 rider can bridge corridor crossings without falsifying Calc1.
    public sealed class NavWorldService : MonoBehaviour
    {
        DiggableTerrain? _terrain;
        Calc1Pathfinder? _pathfinder;
        NavWorld? _builtFor;

        public NavWorld? World
        {
            get
            {
                if (_terrain == null) _terrain = GetComponent<DiggableTerrain>();
                return _terrain != null ? _terrain.Nav : null;
            }
        }

        public bool HasWorld => World != null;

        // Records which layer nulled the last failed FindPath, so the rate-limited
        // "[action walk] no path" warning in DigglesTclEngine can name the failing layer instead
        // of just the outcome. NavTJunctionTests exercises the T-junction geometries this covers.
        // Set on every null return; read-only elsewhere, no behavior change.
        public static string LastNullReason = "";

        // One-plan diagnostic seam consumed by the walk executor trace. FindPath is synchronous on
        // the simulation thread, so these describe the same request as LastNullReason without
        // retaining or mutating any navigation records.
        public static string LastGoalResolution = "";
        public static string LastLayerResolution = "";

        // iOwner: CalcPath passes layer + (owner[+0x7d8]==0 ? 100: 0) — the campaign default lands
        // in the >7 band (cost v1, no owner gate). Pass ≤7 only for the territory-gated policy.
        public List<NavWaypoint>? FindPath(
            Vector3 worldStart,
            Vector3 worldTarget,
            bool canClimb = true,
            int iOwner = 100,
            bool preserveRelaxedGoal = false)
        {
            LastNullReason = "";
            LastGoalResolution = "";
            LastLayerResolution = "";
            var nav = World;
            if (nav == null) { LastNullReason = "no-navworld"; return null; }
            if (_pathfinder == null || !ReferenceEquals(_builtFor, nav))
            {
                _pathfinder = new Calc1Pathfinder(nav);
                _builtFor = nav;
            }

            var root = transform;
            Vector3 sN = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(worldStart));
            Vector3 tN = RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(worldTarget));
            var subs = _pathfinder.FindPath(
                Mathf.RoundToInt(sN.x * 8f), Mathf.RoundToInt(sN.y * 8f),
                Mathf.RoundToInt(tN.x * 8f), Mathf.RoundToInt(tN.y * 8f), canClimb, iOwner);
            if (subs == null || subs.Count == 0)
            {
                LastGoalResolution =
                    $"rawStart=({sN.x:0.000},{sN.y:0.000}) rawGoal=({tN.x:0.000},{tN.y:0.000}) calc1=NULL";
                LastNullReason = $"calc1 start=({sN.x:0.0},{sN.y:0.0}) goal=({tN.x:0.0},{tN.y:0.0}) climb={canClimb}";
                return null;
            }

            var calcStart = subs[0];
            var calcGoal = subs[subs.Count - 1];

            // The original's goal handling lands a descent on the ground line: when the relaxed
            // goal sits on a bare wall cell (the Calc1 relax snaps on the walkable bit, which
            // includes climb cells), settle it down (+y) onto the first floor-bit cell of the band
            // so the dwarf ends standing at the shaft bottom (live dump: gnomes stand exactly on
            // value-3 cells) instead of hanging on the wall above the dig block.
            //
            // The settle applies only when the raw target cell was not itself walkable — i.e. only
            // to goals the relax had to move. The native goal relax runs only under
            // `!Walkable(goal)` (FUN_0059d640:152); a goal placed deliberately on a wall cell (a
            // wall-click, or z_dignwalk's vector_fixdig standoff on a bare wall face) is never
            // moved, and FUN_004163a0 dispatches the trailing type-0 run to CAct_Climb: the dwarf
            // climbs there and hangs (kletterstand, z_procs.tcl:1707). An unconditional settle
            // would turn every such wall standoff into "walk to the floor below", so the digger
            // could never take his wall pose.
            // `action walk -dig 1` bypasses the native pre-snap entirely
            // (FUN_00420840:1901-1919); preserveRelaxedGoal carries that contract through this
            // port-only post-relax settle as well.
            ApplyRelaxedGoalFloorSettle(nav, subs, tN, preserveRelaxedGoal);
            var settledStart = subs[0];
            var settledGoal = subs[subs.Count - 1];
            LastGoalResolution =
                $"rawStart=({Mathf.RoundToInt(sN.x * 8f)},{Mathf.RoundToInt(sN.y * 8f)}) " +
                $"rawGoal=({Mathf.RoundToInt(tN.x * 8f)},{Mathf.RoundToInt(tN.y * 8f)}) " +
                $"calc=({calcStart.sx},{calcStart.sy})->({calcGoal.sx},{calcGoal.sy}) " +
                $"settled=({settledStart.sx},{settledStart.sy})->({settledGoal.sx},{settledGoal.sy}) " +
                $"preserve={preserveRelaxedGoal}";

            // regression: an earlier "trim trailing wall cells" rule inverted FUN_00423730's type
            // codes (type 0 = floor bit clear = wall, type 2 = floor) and deleted every wall-goal
            // climb. FUN_004163a0:69-72 extends the wall (type-0) run by +1 — it is the climb that
            // claims the boundary record, not the floor walk — and a type-0 run reaching the path
            // end is still dispatched to FUN_00415e10 (CAct_Climb): the original climbs to wall
            // goals and hangs there (kletterstand; z_procs.tcl:1707 pins roty=π). Trailing wall
            // cells stay in the path as climb records.

            // Calc1 emits one exit sub-cell per tile, so two consecutive floor polyline cells can
            // hide a vertical ledge between them (a half-dug cave's interior floor line 4+ rows
            // above the tunnel line in the same/adjacent column — intermediate carve depths create
            // these; CaveDigReproTests C1 pins this case). Classified as one "floor" run, the leg
            // is unresolvable by construction: the ledge face's FloorPathMap columns are fully
            // banded out (floorH==ceilH ⇒ empty band, FUN_0041c4b0:135-226) and the 5-pass flood
            // tops out at |Δrow| 2 per hop — disjoint bands ([..,43) vs [45,..)) can never connect,
            // so the whole path nulls and the digger cannot walk. The nav data already says the
            // face is a climb — EFFPASSWALK derives value-1 climb cells on it — the tile-level
            // polyline merely elided them. Splice those actual wall cells back in, letting the
            // existing wall-run assembly (records + boundary claims + dismount re-seed) route the
            // ledge as the climb it physically is.
            for (int i = subs.Count - 2; i >= 0; i--)
            {
                var (ax, ay) = subs[i];
                var (bx, by) = subs[i + 1];
                if (IsWallRecord(nav, ax, ay) || IsWallRecord(nav, bx, by)) continue;
                int dy = by - ay;
                if (Mathf.Abs(dy) < 3 || Mathf.Abs(dy) > 64 || Mathf.Abs(bx - ax) > 2) continue;
                int step = dy > 0 ? 1 : -1;
                // The face column is wherever the climb cells actually are — probe both endpoints'.
                foreach (int col in ax == bx ? new[] { ax } : new[] { ax, bx })
                {
                    var face = new List<(int sx, int sy)>();
                    for (int y = ay + step; y != by; y += step)
                        if (IsWalkableWallCell(nav, col, y)) face.Add((col, y));
                    if (face.Count == 0) continue;
                    subs.InsertRange(i + 1, face);   // collected in travel order
                    break;
                }
            }

            AlignLadderEndpoints(nav, subs);

            // ---- run-by-run assembly (see the class comment) ----
            // The cursor seeds at the relaxed start cell, not the raw body position:
            // FUN_0041aac0 densifies owner[+0x10] — the relaxed Calc1 polyline, whose first cell is
            // the start relax of FUN_0059d640:113-148 — so the first floor run's control polyline
            // (and its StampBands columns) begins on a walkable cell. Seeding from the raw stale
            // (x,y) of a body hanging over its own fresh dig hole put control[0] on a hollowed
            // column whose standable band is empty ([h·4+1, h·4−1) with h≈14 both rows) — SnapHoriz
            // found no start cell within ±16 rows and the whole plan nulled → WalkResult 3 → the
            // Tcl walkfail ladder churned while the gnome hung with no reaction. The depth stays
            // the body's (sN.z): the backtrace origin row rides the live body depth, faithful to
            // FUN_0041d8a0's start-row derivation. The body still enters the world from its stale
            // position — wps[0] keeps worldStart and the mover drives the live body into the route
            // (FUN_00419c60:36-45).
            Vector3 cur = new Vector3(subs[0].sx * 0.125f, subs[0].sy * 0.125f,
                Mathf.Clamp(sN.z, 0f, 15.75f));   // native cursor
            var wps = new List<NavWaypoint>(subs.Count + 8)
            {
                ClassifyWaypoint(nav, worldStart, subs[0].sx, subs[0].sy)
            };
            var layerRuns = new List<string>();
            for (int i = 0; i < subs.Count;)
            {
                bool wallRun = IsWallRecord(nav, subs[i].sx, subs[i].sy);
                int j = i; while (j < subs.Count && IsWallRecord(nav, subs[j].sx, subs[j].sy) == wallRun) j++;
                if (wallRun)
                {
                    layerRuns.Add($"climb[{i}..{j})");
                    // CAct_Climb records: the run's Calc1 cells at the support depth.
                    for (int k = i; k < j; k++)
                    {
                        var (sx, sy) = subs[k];
                        // The record rides the faithful FUN_004238e0 support depth (row above); the
                        // per-cell clearance (own face over support, at a depth step) is a separate
                        // hint the port's ClimbAction standoff consumes so the body clears the local
                        // face — the record itself is untouched (NavWorldRealTerrainTests invariant).
                        int support = SupportDepth(nav, sx, sy);
                        var rec = new Vector3(sx * 0.125f, sy * 0.125f, support);
                        float clearance = ClimbFaceClearance(nav, sx, sy, support);
                        // Wall normal: every climbable face in the data model is an (x,y)-plane
                        // face whose normal is the depth axis (one face depth per heightmap
                        // column — no side-facing climb walls). Outward = toward the open
                        // pocket = the larger depth nibble side: a column of height h holds
                        // rock at z < h and air at z ≥ h (three independent live-dump
                        // confirmations: StampBands' legal band starts at h·4+1, EFFPASSWALK
                        // opens a cell only when h < 14, the floor rule needs h+2 < h(sy+1)).
                        // With world Z = −(z·0.5+7) the air side is world −Z, so outward =
                        // (0,0,−1). (An earlier reading — "smaller nibble/+Z toward the camera" —
                        // was inverted; it rendered the climber's standoff + pose bulge 100 %
                        // inside the rock.)
                        nav.TryGetTransportInfluence(sx, sy, out var transportKind, out int transportSource);
                        wps.Add(new NavWaypoint(
                            ToWorld(root, rec),
                            true,
                            WallOutward,
                            clearance,
                            transportKind,
                            transportSource));
                        cur = rec;
                    }
                    // Boundary claim (FUN_004163a0:69-72): a wall run not at the path end is
                    // extended by +1 — the climb's record array claims the first floor cell of
                    // the next run, and FUN_0040bb70:115-139 copies the last wall record's z onto
                    // that claimed record (the param_2==2 branch; param_2 = the next run's
                    // FUN_00423730 type, 2 = floor). Generic wall climbs therefore land at the
                    // wall base. A typed ladder instead feeds this claimed cell the same floor-line
                    // depth as its following floor run: the ladder transition must finish standing
                    // on its exact exit plane. The dismount remains a ≤1-cell hop, never a warp to
                    // the next floor run's far landing. The dispatch cursor is not
                    // advanced past the claim (FUN_004163a0 leaves +0x20 at the boundary), so the
                    // following floor run starts from the same cell: seed `cur` here.
                    if (j < subs.Count)
                    {
                        var (bx, by) = subs[j];
                        // AirClamp: the claimed record's wall-z copy can sit below the boundary
                        // floor cell's own open depth (a climb off a deeper cave face) — the
                        // lerping executor would sink the body into the local rock for the dismount
                        // hop (CaveDigReproTests C1 pins this). Ride a generic claim at ≥ the local
                        // air depth; a ladder claim starts at the boundary floor's support+1 depth
                        // directly.
                        var (lastWallX, lastWallY) = subs[j - 1];
                        bool ladderExit = nav.TryGetTransportInfluence(
                            lastWallX,
                            lastWallY,
                            out var exitKind,
                            out _)
                            && exitKind == NavTransportKind.Ladder;
                        float claimDepth = ladderExit ? FloorLineDepth(nav, bx, by) : cur.z;
                        var bound = AirClamp(nav, new Vector3(
                            bx * 0.125f,
                            by * 0.125f,
                            claimDepth));
                        wps.Add(ClassifyWaypoint(nav, ToWorld(root, bound), bx, by));
                        // A generic claimed record keeps the wall z (the faithful
                        // FUN_0040bb70:115-139 copy above), while a ladder claim already uses the
                        // exact floor line. In both cases the following floor run starts standing
                        // on the boundary floor cell — the climb dismount lands on the ground line
                        // at its standable depth (support row + 1; live-dump invariant: gnomes
                        // stand at value-3 cells at h+1). Seeding the floor run with the wall
                        // record depth instead made FloorPathMap's backtrace-origin snap
                        // (FUN_0041bbd0, ±16 rows) unbridgeable whenever the climb came off a face
                        // much deeper than the floor band — a half-dug cave pocket (depth 3)
                        // exiting onto the tunnel floor (band rows 45+) nulled the whole path: the
                        // digger could neither take his next standoff nor walk home
                        // (CaveDigReproTests C1 pins this).
                        cur = new Vector3(bound.x, bound.y, FloorLineDepth(nav, bx, by));
                    }
                }
                else
                {
                    // FloorPathMap over the floor run: control polyline = cursor + the run cells.
                    var control = new List<Vector2>(j - i + 1) { new Vector2(cur.x, cur.y) };
                    for (int k = i; k < j; k++)
                        control.Add(new Vector2(subs[k].sx * 0.125f, subs[k].sy * 0.125f));
                    Vector3 runGoal;
                    if (j == subs.Count)
                    {
                        // Final run → land on the relaxed/settled goal floor cell at the stored
                        // target depth. FUN_0041aac0 builds the final leg by densifying owner[+0x10]
                        // (the relaxed Calc1 polyline, whose last cell is a standable floor cell) and
                        // seeding the flood depth from owner[+0x30] (the stored target z) at:97-98 —
                        // it does not walk to the target's raw gravity-plane (x,y).
                        //
                        // The raw target (x,y) cannot be trusted here: the engine's `action walk`
                        // does not vector_fix the target (FUN_00420840 only stores it), so off-floor
                        // targets must be relaxed to the floor by the pathfinder. The gnome AI layer
                        // vector_fixes every walk goal first (z_dignwalk.tcl:596,612,625,…), but the
                        // cutscene `do_action walk` passes parse_pos raw. Those targets are routinely
                        // off-floor: WiggleElfLook* = Elf1Pos+{…} with Elf1Pos.y = Lagerplatz.y−1.5
                        // (the elf flies) ⇒ 1.5 world-units airborne ("walks on air"); and a shaft-rim
                        // target keeps the camp's y while the rim floor sits on a different row ⇒ the
                        // gnome lands off the rim. subs[last] is the Calc1 +y-biased goal relax (then
                        // the floor-settle above), i.e. the exact floor cell the original walks to.
                        var (gx, gy) = subs[subs.Count - 1];
                        // The final-leg goal depth = the stored target z (owner+0x30), clamped only to
                        // the emit grid's range [0, 15.75] (the native 64-row × 0.25 grid, FUN_0041d8a0:182
                        // `ROUND(z·DAT_007374a0)` bounded by the FloorPathMap.cpp grid rows). No seat
                        // pull-down: the native keeps the stored depth and lets the emit band-snap it into
                        // the column's standable band (StampBands / FUN_0041c4b0:135-226) — there is no
                        // universal seat law in the decompile (live dump: meadow gnomes spread z = 6..15 on
                        // the y=30.5 line).
                        //
                        // regression: an earlier "standing-seat cap" (goalZ≥14.4 && 0<h(gy−1)<15 → pull
                        // to h+1) was a workaround for the dig-underself sticky-14.5 idler, but it
                        // collapsed the two urw_3 shaft gnomes: WiggleSchacht1/2Pos share (x,y) and differ
                        // only in depth (z 12.32 vs 16.82); the cap could not tell an authored deep
                        // cutscene depth from get_hmap garbage (both ≥14.4), so it pulled Schacht2
                        // 15.75→h+1≈12 onto Schacht1's 12.25, collapsing both gnomes onto the same point.
                        // Removed: the faithful band-snap already resolves the depth into the legal band
                        // (Schacht2 → band max ≈14.75, separated from Schacht1's 12.25), and the
                        // dig-underself hang is owned by the per-leg floor re-read (HasFloorSupportWorld) +
                        // CActWalk_AutoRetry (RetryWalkAction), the native FUN_0041a470/FUN_00408160 law —
                        // not a depth cap. The walk logic is now identical for cutscene and gameplay targets.
                        float goalZ = Mathf.Clamp(tN.z, 0f, 15.75f);
                        runGoal = new Vector3(gx * 0.125f, gy * 0.125f, goalZ);
                    }
                    else
                    {
                        // Run exits into a wall → the run-goal depth is the wall-boundary rule
                        // of FUN_00415f40:24-57 (the CAct_WalkFloor dispatcher): when the next
                        // run is a climb, the stored exit depth = min(h(pts[end]), h(pts[end+1]))
                        // + 1.0, capped at 15.0 (DAT_00737568) — h read straight at the first
                        // two wall cells (FUN_004093f0 = GetHmapSub(x,y); indices clamped to the
                        // path tail). It flows in as ctor param_8 → +0x44 → FUN_0041a2f0 → the
                        // run-goal z (+0x30) that FUN_0041aac0:97-99 reads on the run's final
                        // leg. face+1.0 native = 0.5 wu on the air side of the face (world −Z),
                        // inside the wall-base standable band — the walker waits at the rim next
                        // to the face, never inside the rock. (Neither the old wall-cell
                        // SupportDepth anchor — on the face plane, torso clipping — nor an
                        // earlier "h(y+2) − 0.5/1.0" reading is what ships: that subtraction is
                        // FUN_0041aac0:101-124's interior-leg anchor for runs ≥ 96 sub-cells of
                        // arc, split into ~32-cell legs by FUN_0041a470, and its window index
                        // min(legEnd+3, runEnd) clamps inside the floor run — when it reaches
                        // runEnd the code takes the final-leg branch (z = +0x30) instead of the
                        // h(y+2) read. The port keeps one leg per run, so only this boundary
                        // rule applies here; long runs lerp where the original re-anchors every
                        // 32 sub-cells — a known, cosmetic-depth divergence.)
                        var (lx, ly) = subs[j - 1];
                        var (w1x, w1y) = subs[j];                // first wall cell of the next run
                        var (w2x, w2y) = subs[Mathf.Min(j + 1, subs.Count - 1)];
                        int hWall = Mathf.Min(nav.GetHmapSub(w1x, w1y), nav.GetHmapSub(w2x, w2y));
                        runGoal = new Vector3(lx * 0.125f, ly * 0.125f, Mathf.Min(15f, hWall + 1f));
                    }
                    // FloorPathMap flags (FUN_0041aac0 → FUN_0041d8a0:139-143,196-201): the flag
                    // byte is 0 on the final leg of a run (the leg whose densify window reaches
                    // the run end, 0041ac29-0041ac37), selecting the ±16 horizontal goal snap
                    // (FUN_0041bbd0) and arming the goal-exact 3-point emit (cVar6 = trailing-
                    // points-or-`-forcetarget`, default 1 — FUN_00420840:362). The port emits
                    // one leg per run, so every Build here is a final leg: both flags true.
                    // (regression: an earlier `firstSegment: first` keying gave every run after
                    // the first the single-column vertical snap, an inverted keying; the vertical
                    // snap belongs to interior legs only.)
                    var route = FloorPathMap.Build(nav, control, cur, runGoal,
                        forceThreePoint: true, finalLeg: true);
                    if (route == null)                       // no depth route ⇒ WS_NOPASSAGE (3)
                    {
                        var controlDump = new System.Text.StringBuilder();
                        for (int k = i; k < j; k++)
                            controlDump.Append($"({subs[k].sx},{subs[k].sy})");
                        LastNullReason = $"floorpathmap run[{i}..{j})/{subs.Count} " +
                            $"cur=({cur.x:0.0},{cur.y:0.0},{cur.z:0.0}) " +
                            $"goal=({runGoal.x:0.0},{runGoal.y:0.0},{runGoal.z:0.0}) " +
                            $"fail[{FloorPathMap.LastBuildFailure}] control={controlDump}";
                        layerRuns.Add($"floorpathmap[{i}..{j})=NULL");
                        LastLayerResolution = string.Join(" ", layerRuns);
                        return null;
                    }
                    var executableRoute = BuildExecutableFloorRoute(route, control);
                    layerRuns.Add(
                        $"floorpathmap[{i}..{j}) depth={route.Count} exec={executableRoute.Count}");
                    // AirClamp per emitted floor waypoint: the LOS landing/mid are emitted at the
                    // raw goal (x,y) while their row may have snapped into a different column's
                    // band (FUN_0041bbd0 searches ±16 columns too) — a depth legal in the snapped
                    // column can be inside rock at the emitted (x,y) (CaveDigReproTests C1 pins
                    // this). Clamp each waypoint to its own column's air depth; the landing keeps
                    // the goal's (x,y) contract.
                    for (int k = 1; k < executableRoute.Count; k++)    // [0] == cur, already emitted
                    {
                        Vector3 emitted = AirClamp(nav, executableRoute[k]);
                        int emittedX = Mathf.RoundToInt(emitted.x * 8f);
                        int emittedY = Mathf.RoundToInt(emitted.y * 8f);
                        wps.Add(ClassifyWaypoint(nav, ToWorld(root, emitted), emittedX, emittedY));
                    }
                    cur = AirClamp(nav, executableRoute[executableRoute.Count - 1]);
                }
                i = j;
            }
            LastLayerResolution = string.Join(" ", layerRuns);
            if (wps.Count <= 1) { LastNullReason = "degenerate-1wp"; return null; }
            return wps;
        }

        // FloorPathMap resolves depth over columns along the Calc1 control polyline. Its lawful LOS
        // fast path returns only start/mid/goal depth anchors; feeding those sparse anchors straight
        // to MetaWalkAction accidentally turns the control polyline into a world-space chord. Since
        // tile+0x12 became the faithful packed (subX,subY) exit pair, those intermediate bends are
        // load-bearing: the TrollQuest stair chord leaves the one-cell floor band at its first tile
        // crossing, and the live-support guard correctly kills it. Insert every skipped Calc1 control
        // vertex between the existing depth anchors, interpolating only z along the FloorPathMap
        // profile. The depth resolver and every navigation record remain unchanged.
        internal static List<Vector3> BuildExecutableFloorRoute(
            IReadOnlyList<Vector3> depthRoute,
            IReadOnlyList<Vector2> control)
        {
            var executable = new List<Vector3>(depthRoute.Count + control.Count);
            if (depthRoute.Count == 0) return executable;
            if (depthRoute.Count == 1 || control.Count < 2)
            {
                executable.AddRange(depthRoute);
                return executable;
            }

            var cumulative = new float[control.Count];
            for (int i = 1; i < control.Count; i++)
                cumulative[i] = cumulative[i - 1] + Vector2.Distance(control[i - 1], control[i]);
            float total = cumulative[cumulative.Length - 1];
            if (total <= 1e-5f)
            {
                executable.AddRange(depthRoute);
                return executable;
            }

            var anchorProgress = new float[depthRoute.Count];
            anchorProgress[0] = 0f;
            for (int i = 1; i + 1 < depthRoute.Count; i++)
                anchorProgress[i] = ProjectProgress(
                    control,
                    cumulative,
                    depthRoute[i],
                    anchorProgress[i - 1]);
            anchorProgress[anchorProgress.Length - 1] = total;

            AddDistinct(executable, depthRoute[0]);
            int controlIndex = 1;
            for (int anchorIndex = 1; anchorIndex < depthRoute.Count; anchorIndex++)
            {
                float fromProgress = anchorProgress[anchorIndex - 1];
                float toProgress = anchorProgress[anchorIndex];
                while (controlIndex + 1 < control.Count && cumulative[controlIndex] <= fromProgress + 1e-5f)
                    controlIndex++;
                while (controlIndex + 1 < control.Count && cumulative[controlIndex] < toProgress - 1e-5f)
                {
                    float t = toProgress > fromProgress + 1e-5f
                        ? (cumulative[controlIndex] - fromProgress) / (toProgress - fromProgress)
                        : 0f;
                    Vector2 point = control[controlIndex];
                    AddDistinct(executable, new Vector3(
                        point.x,
                        point.y,
                        Mathf.Lerp(depthRoute[anchorIndex - 1].z, depthRoute[anchorIndex].z, t)));
                    controlIndex++;
                }
                AddDistinct(executable, depthRoute[anchorIndex]);
            }
            return executable;
        }

        static float ProjectProgress(
            IReadOnlyList<Vector2> control,
            float[] cumulative,
            Vector3 point,
            float minimumProgress)
        {
            Vector2 target = new Vector2(point.x, point.y);
            float bestProgress = minimumProgress;
            float bestDistance = float.MaxValue;
            for (int i = 0; i + 1 < control.Count; i++)
            {
                Vector2 from = control[i];
                Vector2 delta = control[i + 1] - from;
                float length = cumulative[i + 1] - cumulative[i];
                if (length <= 1e-5f || cumulative[i + 1] < minimumProgress) continue;
                float minimumT = Mathf.Clamp01((minimumProgress - cumulative[i]) / length);
                float t = Mathf.Clamp(Vector2.Dot(target - from, delta) / delta.sqrMagnitude, minimumT, 1f);
                Vector2 projected = from + delta * t;
                float distance = (target - projected).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestProgress = cumulative[i] + length * t;
            }
            return bestProgress;
        }

        static void AddDistinct(List<Vector3> points, Vector3 point)
        {
            if (points.Count > 0 && (points[points.Count - 1] - point).sqrMagnitude <= 1e-8f)
            {
                points[points.Count - 1] = point;
                return;
            }
            points.Add(point);
        }

        static void ApplyRelaxedGoalFloorSettle(
            NavWorld nav,
            List<(int sx, int sy)> subs,
            Vector3 nativeTarget,
            bool preserveRelaxedGoal)
        {
            if (preserveRelaxedGoal || subs.Count == 0) return;
            var (goalX, goalY) = subs[subs.Count - 1];
            int rawGoalX = Mathf.RoundToInt(nativeTarget.x * 8f);
            int rawGoalY = Mathf.RoundToInt(nativeTarget.y * 8f);
            bool deliberateWallGoal =
                (nav.SubAt(rawGoalX, rawGoalY) & (NavWorld.SubWalk | NavWorld.SubFloor)) ==
                NavWorld.SubWalk;
            if (deliberateWallGoal ||
                (nav.SubAt(goalX, goalY) & (NavWorld.SubWalk | NavWorld.SubFloor)) !=
                NavWorld.SubWalk)
            {
                return;
            }
            for (int offset = 1; offset <= 16; offset++)
            {
                byte below = nav.SubAt(goalX, goalY + offset);
                if ((below & NavWorld.SubFloor) != 0)
                {
                    subs[subs.Count - 1] = (goalX, goalY + offset);
                    return;
                }
                if ((below & NavWorld.SubWalk) == 0) return;
            }
        }

        // The climbable-face outward normal in world space: air = the larger-depth-nibble side
        // = world −Z (see the wall-run comment in FindPath). Shared with ClimbAction's fallback.
        public static readonly Vector3 WallOutward = new Vector3(0f, 0f, -1f);

        // Live mid-walk floor-support probe — the CAct_WalkFloor per-leg rebuild law seam
        // (FUN_0041a470/FUN_0041aac0: every ~32-sub-cell leg re-reads current terrain and stamps
        // the per-column standable bands from the floor lines, so a carve opening the ground under
        // an in-flight walk empties the next leg's bands → WS_NOPASSAGE, and CActWalk_AutoRetry
        // replans from the current position, FUN_00408160). The port's floor runs are one frozen
        // leg, so MetaWalkAction re-checks this per tick instead (wired by
        // DigglesTclEngine.BuildWalkSegments). True while the world position still has a +0x3c
        // floor bit within the get_gnomeposition probe rows (subY+1/subY/subY−1,
        // FUN_0068d8a0:86-93). Note it must key on the floor bit, not the walkable bit: a carved
        // shaft mouth derives value-1 (climb band) — legal for a climb, but a floor leg crossing
        // it horizontally is an air-glide bug (the native band stamping has nothing to stand on
        // there). Nav gone ⇒ true (never abort on a missing world).
        public bool HasFloorSupportWorld(Vector3 world)
        {
            var nav = World;
            if (nav == null) return true;
            Vector3 n = RuntimeWorldSpawner.LocalToNative(transform.InverseTransformPoint(world));
            int sx = Mathf.RoundToInt(n.x * 8f), sy = Mathf.RoundToInt(n.y * 8f);
            return nav.SubIsFloor(sx, sy + 1) || nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy - 1);
        }

        // Anti-embed emit guard for floor waypoints (gnomes sinking into walls): a body at native
        // (x,y,z) sits in air iff z ≥ h of its own sub-column (with the row-up support read —
        // the standing law z = min(h+1, 14.5) and the FloorPathMap band [h·4+1, …) both live on
        // the air side). Emitted floor waypoints can carry a depth borrowed from another column
        // (the boundary claim's wall-z copy; the LOS landing at the raw goal (x,y) after a
        // cross-column goal snap) — the native's climb pose / per-leg band re-anchors keep the
        // body out of rock there, which our lerping executor cannot, so the port clamps at the
        // emit seam. Wall records are untouched (they faithfully ride the FUN_004238e0 support
        // depth; ClimbFaceClearance owns their face standoff).
        static Vector3 AirClamp(NavWorld nav, Vector3 p)
        {
            int sx = Mathf.RoundToInt(p.x * 8f), sy = Mathf.RoundToInt(p.y * 8f);
            int minH = System.Math.Min(nav.GetHmapSub(sx, sy), nav.GetHmapSub(sx, sy - 1));
            if (p.z < minH) p.z = minH;
            return p;
        }

        Vector3 ToWorld(Transform root, Vector3 native)
            => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native));

        // FUN_004238e0 — the per-record climb depth: the height nibble one row UP (the support row
        // under the path cell); when that reads 15 (undug), the smallest of the four neighbours
        // (sx±1 @ sy-1 horizontal, sy-2/sy vertical) — the bias toward the open face. The decompile
        // picks a neighbour cell then re-reads its height; value-wise that is min of the four.
        public static int SupportDepth(NavWorld nav, int sx, int sy)
        {
            int h = nav.GetHmapSub(sx, sy - 1);
            if (h != 15) return h;
            int hx = System.Math.Min(nav.GetHmapSub(sx - 1, sy - 1), nav.GetHmapSub(sx + 1, sy - 1));
            int hy = System.Math.Min(nav.GetHmapSub(sx, sy - 2), nav.GetHmapSub(sx, sy));
            return System.Math.Min(hx, hy);
        }

        public static float FloorLineDepth(NavWorld nav, int sx, int sy) =>
            Mathf.Min(SupportDepth(nav, sx, sy) + 1f, 14.5f);

        // Extra outward standoff (native depth nibbles) a climb record needs so the body clears the
        // local wall face, on top of the faithful support-depth anchor. ClimbSinkAndDigPaceTests
        // pins this: the native rides record z = support(row above) exactly (FUN_00418db0:123-125 —
        // no standoff), letting the authored kletter pose + per-slot orientation (FUN_00422fe0,
        // unresolved in the port) hang the body outward off the face. Where the face steps in depth
        // (ownH > support — a shaft rim, a natural slope's base) the support row is deeper-open than
        // this cell, so a body parked at support sits inside the current cell's rock (airGap < 0,
        // the body sinking into the wall along +Z; live trace: gap dipped to −0.5..−1.0 native at
        // the eastern-slope base). The port can't reproduce the native's outward pose hang, so
        // ClimbAction stands the body off by this much more at the step, keeping a constant
        // clearance off whichever face is outermost, at every rung. Flat faces (ownH == support,
        // the common case incl. clean vertical shafts) return 0 — faithful, record + body
        // unchanged. ownH == 15 is solid/undug (not a real face) → 0.
        public static float ClimbFaceClearance(NavWorld nav, int sx, int sy, int support)
        {
            int ownH = nav.GetHmapSub(sx, sy);
            return (ownH < 15 && ownH > support) ? ownH - support : 0f;
        }

        // Ladder-only goal normalization: give the existing FloorPathMap/AirClamp machinery the
        // ladder column at both run boundaries. It keeps the established floor-line depth bands
        // intact while removing the port's half-to-one-cell lateral endpoint drift.
        public static void AlignLadderEndpoints(
            NavWorld nav,
            List<(int sx, int sy)> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                var (sx, sy) = cells[i];
                if (!IsWallRecord(nav, sx, sy)
                    || !nav.TryGetTransportInfluence(
                        sx,
                        sy,
                        out var kind,
                        out int sourceHandle)
                    || kind != NavTransportKind.Ladder
                    || !nav.TryGetLadderColumn(sourceHandle, out int ladderX))
                    continue;

                int end = i;
                while (end + 1 < cells.Count)
                {
                    var next = cells[end + 1];
                    if (!IsWallRecord(nav, next.sx, next.sy)
                        || !nav.TryGetTransportInfluence(
                            next.sx,
                            next.sy,
                            out var nextKind,
                            out int nextSource)
                        || nextKind != NavTransportKind.Ladder
                        || nextSource != sourceHandle)
                        break;
                    end++;
                }

                for (int wall = i; wall <= end; wall++)
                    cells[wall] = (ladderX, cells[wall].sy);
                AlignLadderFloorBoundary(nav, cells, i - 1, ladderX);
                AlignLadderFloorBoundary(nav, cells, end + 1, ladderX);
                i = end;
            }
        }

        static void AlignLadderFloorBoundary(
            NavWorld nav,
            List<(int sx, int sy)> cells,
            int index,
            int ladderX)
        {
            if ((uint)index >= (uint)cells.Count) return;
            int floorY = cells[index].sy;
            if (nav.SubIsFloor(ladderX, floorY))
                cells[index] = (ladderX, floorY);
        }

        // Surface classifier (FUN_00423730): floor bit ⇒ floor, walkable-without-floor ⇒ wall.
        // Keep a ladder overlay tag on floor records without changing that classification. A ladder
        // can cross a corridor floor; FUN_0060c3c0 registers one rider span and FUN_0060bc70 keeps
        // moving it continuously, so BuildWalkSegments needs the source tag to bridge that record.
        // Other transport overlays remain wall-only here, preserving the elevator asymmetry.
        /// <summary>
        /// Classifies a path waypoint while retaining ladder provenance on floor crossings.
        /// </summary>
        public static NavWaypoint ClassifyWaypoint(
            NavWorld nav,
            Vector3 worldPos,
            int sx,
            int sy)
        {
            if (nav == null) throw new System.ArgumentNullException(nameof(nav));
            bool wall = IsWallRecord(nav, sx, sy);
            nav.TryGetTransportInfluence(sx, sy, out var transportKind, out int transportSource);
            bool preserveTransport = wall || transportKind == NavTransportKind.Ladder;
            return new NavWaypoint(
                worldPos,
                wall,
                wall ? WallOutward : default,
                0f,
                preserveTransport ? transportKind : NavTransportKind.None,
                preserveTransport ? transportSource : -1);
        }

        // Calc1 decodes node+0x12 as the packed (subX,subY) pair at 0059d640:579-584/:702-707.
        // FUN_00423730 then classifies that exact path record solely by tile+0x3c bit 1:
        // floor bit set => type 2 floor, otherwise type 0 wall. It does not require bit 0.
        static bool IsWallRecord(NavWorld nav, int sx, int sy) =>
            (nav.SubAt(sx, sy) & NavWorld.SubFloor) == 0;

        // Port-only ledge splice scans arbitrary cells that are not already Calc1 records.
        // Keep its stricter real-climb-cell predicate so an empty/solid zero cell is never
        // manufactured into the path merely because FUN_00423730 would classify a record there
        // as type 0 if such a record actually existed.
        static bool IsWalkableWallCell(NavWorld nav, int sx, int sy)
        {
            byte b = nav.SubAt(sx, sy);
            return (b & NavWorld.SubWalk) != 0 && (b & NavWorld.SubFloor) == 0;
        }
    }
}
