#nullable enable

namespace Diggles.Runtime.Navigation
{
    using System.Collections.Generic;
    using UnityEngine;

    // Floor-segment depth-profile resolver — port of the original's FloorPathMap
    // (FUN_0041bff0 densifier + FUN_0041c4b0 band stamping + FUN_0041d8a0 LOS/5-pass-flood/backtrace
    // + FUN_0041bbd0/bde0 free-cell snaps). See Docs/re_parts/climb-route.md.
    //
    // This grid does not route wall climbs. Its per-column legal band
    // [h(sy-1)*4+1, h(sy+1)*4-1) is empty on every mid-wall cell of a real cliff (h is constant
    // along a wall face, e.g. 11/11/11 -> band [45,43) = empty -> fully masked), so the flood can
    // never descend a sheer wall. The bands are non-empty exactly on floor lines (hA < hB): the
    // grid resolves where in depth the dwarf walks along a floor polyline (col = arc index along
    // the segment, row = depth*4). The climb route proper is CAct_Climb's record mirror of the
    // Calc1 sub-cell polyline with FUN_004238e0 support depths (climb-exec.md §1.2/§1.4) — see
    // NavWorldService. The "climb" naming in climb-route.md §7 is a misread in that doc.
    //
    // All coordinates here are native: x/y in tile units (sub-cell = 0.125), z = depth nibble scale
    // (0..15.75; grid row = round(z*4), 64 rows).
    public static class FloorPathMap
    {
        const uint MaskTooLow  = 0x10000000;   // below the column's floor surface (0041c4b0:212)
        const uint MaskTooHigh = 0x08000000;   // past the column's ceiling (0041c4b0:218)
        const uint MaskBits    = 0xff000000;
        const uint CostBits    = 0x00ffffff;
        // The flood seeds the goal cell with a large 24-bit cost and relaxes downward (neighbour =
        // popped - passCost, keep-max); the backtrace ascends the gradient to this maximum. The
        // relative costs are disasm-verified ({1280,512,256,1280,1024}); the seed's absolute value
        // is not (mangled tail, climb-route.md §11.1) — any value far above pathLen*1280 behaves
        // identically. 0xfffff0 keeps headroom inside the 24-bit cost field.
        const uint SeedCost = 0x00fffff0;

        // 5-pass neighbour table (climb-route.md §4.3): pass2 = cardinals (256),
        // pass1 = diagonals (512), pass4 = 2-step cardinals (1024), passes 0+3 = knight moves (1280).
        static readonly int[] PassCost = { 1280, 512, 256, 1280, 1024 };
        static readonly sbyte[,,] PassOff =
        {
            { {-1,-2}, {+2,-1}, {+1,+2}, {-2,+1} },
            { {-1,-1}, {+1,-1}, {+1,+1}, {-1,+1} },
            { {-1, 0}, { 0,-1}, {+1, 0}, { 0,+1} },
            { {-1,+2}, {-2,-1}, {+1,-2}, {+2,+1} },
            { { 0,+2}, {-2, 0}, { 0,-2}, {+2, 0} },
        };

        // Failure-path diagnostic (set on every null return, cleared per Build): which exit fired
        // and with what indices. Consumed by NavWorldService's LastNullReason so a "no path" report
        // names the failing law instead of just the layer. Strings only materialize on failure.
        public static string LastBuildFailure = "";

        // Builds the depth-resolved route for one floor leg. control = the leg's (x,y) control
        // polyline (the sparse Calc1 cells); start/goal carry their depths in.z.
        //
        // Flag keying (FUN_0041aac0 0041ac29-0041ae14 → FUN_0041d8a0:139-143,196-201): the caller's
        // flag byte is 0 on the final leg of a run (the leg whose densify window reaches the run
        // end) and 1 on interior legs of a long (≥96-sub-cell) run.
        // forceThreePoint mirrors cVar6 = (flag==0 ? trailing-points-or-`-forcetarget`: 0)
        // (not "canclimb"; owner+0x7bf = `action walk -forcetarget`, default 1 —
        // FUN_00420840:362,1147-1154): true -> the LOS short-circuit emits {start, mid, goal}
        // and the post-flood re-seed retry is disarmed.
        // finalLeg selects the goal-cell snap (FUN_0041d8a0:196-201 on the flag byte):
        // true (flag 0) = ±16 horizontal FUN_0041bbd0, false = ±256-row vertical FUN_0041bde0.
        // Returns native waypoints (start..goal inclusive) or null = no route (WS_NOPASSAGE,
        // FUN_0041aac0:211).
        public static List<Vector3>? Build(NavWorld nav, IReadOnlyList<Vector2> control,
            Vector3 start, Vector3 goal, bool forceThreePoint, bool finalLeg)
        {
            LastBuildFailure = "";
            // No early-out for a 1-point polyline (a run whose only cell is the claimed climb
            // boundary, or start==goal): FUN_0041bff0 emits the single densified point and
            // FUN_0041d8a0 runs the same band/LOS/snap machinery on the 1-column grid — the goal
            // depth must still come out band-resolved (row·0.25), never the raw target z.
            var pts = Densify(control, goal);
            LipSnap(nav, pts);

            int n = pts.Count;
            var grid = new uint[n * 64];
            StampBands(nav, pts, grid);

            // Seed/goal cell derivation (§4.1): col = nearest point, row = round(z*4). The flood
            // seeds at the goal-nearest cell and the backtrace starts at the start-nearest cell.
            int startCol = NearestPoint(pts, start), goalColRaw = NearestPoint(pts, goal);
            int startRow = Mathf.Clamp(Mathf.RoundToInt(start.z * 4f), 0, 63);
            int goalRowRaw = Mathf.Clamp(Mathf.RoundToInt(goal.z * 4f), 0, 63);

            // Backtrace-origin snap (§5): always the ±16 search (FUN_0041d8a0:189).
            if (!SnapHoriz(grid, n, ref startCol, ref startRow))
            {
                LastBuildFailure =
                    $"start-snap col={startCol}/{n} row={startRow} band={DescribeColumnBand(grid, startCol)}";
                return null;
            }

            // Seed snap → LOS → flood, with one re-seed retry (FUN_0041d8a0:659-665, gated on
            // cVar6==0 = interior legs only): when the backtrace origin got no cost, the seed is
            // re-derived from the unsnapped goal col + the raw goal row and the whole
            // snap/LOS/flood pass runs again (the original's do-while loops back through:196) —
            // the first snap may have slid the seed into a side pocket the start can't reach.
            // Effectively dead from NavWorldService (one leg per run ⇒ forceThreePoint always
            // true), kept faithful for direct callers and a future interior-leg split.
            int goalCol = goalColRaw, goalRow = goalRowRaw;
            for (int attempt = 0; ; attempt++)
            {
                bool seedOk = finalLeg
                    ? SnapHoriz(grid, n, ref goalCol, ref goalRow)
                    : SnapVert(grid, n, ref goalCol, ref goalRow);
                // A goal-snap failure is not a path failure (T-junction case, NavTJunctionTests
                // T10): the wall-boundary run-goal depth (min(h,h)+1, FUN_00415f40:24-57)
                // sits far outside the rim cell's standable band whenever the adjoining wall face
                // is deeper-open than the rim floor — e.g. a full-depth pit (h=0 ⇒ z=1 ⇒ row 4) met
                // from a tunnel floor whose band is [45,59): the ±16-row FUN_0041bbd0 window can
                // never reach the band, and returning null here would kill the whole walk.
                // The native does not abort: FUN_0041bbd0's not-found branch (0041bbd0:95-110)
                // force-pins the requested cell (grid |= 0x80000000, the chosen-mount mask) and
                // returns with the leg alive — the walk waits at the rim and the adjoining climb
                // (which owns the junction via the FUN_004163a0:70-71 claim) does the descent.
                // Port equivalent (interpretation — the exact downstream of the pinned cell is in
                // FUN_0041d8a0's mangled tail): pin the goal column and take its nearest band-legal
                // row to the requested one — the rim wait depth. A fully-masked goal column (no
                // legal depth at all) is still a true failure.
                if (!seedOk)
                {
                    if (!finalLeg)
                    {
                        LastBuildFailure = $"goal-snap-vert col={goalCol}/{n} row={goalRow}";
                        return null;
                    }
                    goalCol = Mathf.Clamp(goalColRaw, 0, n - 1);
                    goalRow = goalRowRaw;
                    if (!PinNearestLegalRow(grid, goalCol, ref goalRow))
                    {
                        LastBuildFailure =
                            $"goal-pin col={goalCol}/{n} row={goalRow} band={DescribeColumnBand(grid, goalCol)}";
                        return null;
                    }
                }

                // Straight-LOS short-circuit (§4.2, truncated lerp): every cell on the line passable
                // -> emit the 2/3-point straight path, no flood and no settle.
                // The mid point is the goal cell at the goal depth: FUN_0041d8a0:235-240 indexes the
                // densified array with local_90 == the goal-nearest col (local_58), not the start col —
                // mid = {pts[goalCol].x, pts[goalCol].y, goalRow*0.25}, point 2 = the exact goal.
                // Using pts[startCol] instead would make start->mid a pure-depth move in place at
                // the start, diving to the goal depth before walking.
                if (LosClear(grid, n, goalCol, goalRow, startCol, startRow))
                {
                    // The final standing point sits at the band-resolved depth (goalRow, snapped into
                    // the standable band above), not the raw target z. FUN_0041d8a0:240-296 emits the
                    // goal-end point's z as (snapped row · 0.25), never the raw owner[+0x30] float.
                    // A raw goal.z would leave a trailing pure-depth micro-segment, making
                    // MetaWalkAction.FaceTowards turn the dwarf along the depth axis at the very end
                    // instead of along its lateral (+X) travel.
                    var landing = new Vector3(goal.x, goal.y, goalRow * 0.25f);
                    var los = new List<Vector3> { start };
                    var mid = new Vector3(pts[goalCol].x, pts[goalCol].y, goalRow * 0.25f);
                    if (forceThreePoint && (mid - start).sqrMagnitude > 1e-6f && (mid - landing).sqrMagnitude > 1e-6f)
                        los.Add(mid);
                    los.Add(landing);
                    return los;
                }

                // 5-pass Dijkstra flood from the seed (§4.3).
                Flood(grid, n, goalCol, goalRow);
                if ((grid[startCol * 64 + startRow] & CostBits) != 0) break;
                if (forceThreePoint || attempt > 0)
                {
                    var ptsDump = new System.Text.StringBuilder();
                    for (int k = 0; k < n; k++)
                        ptsDump.Append(
                            $"{k}:({pts[k].x:0.00},{pts[k].y:0.00})h{nav.GetHmapSub((int)(pts[k].x * 8f), (int)(pts[k].y * 8f))} ");
                    LastBuildFailure =
                        $"flood-unreached start=({startCol},{startRow}) seed=({goalCol},{goalRow})/{n} " +
                        $"bands=[{DescribeBands(grid, n)}] pts=[{ptsDump.ToString().TrimEnd()}]";
                    return null;
                }
                goalCol = goalColRaw;          // FUN_0041d8a0:661-665 — fresh, unsnapped seed
                goalRow = goalRowRaw;
            }

            // Steepest-cost backtrace start -> seed (§4.4): same 20 offsets, keep the max cost.
            var cells = Backtrace(grid, n, startCol, startRow, goalCol, goalRow);
            if (cells == null)
            {
                LastBuildFailure =
                    $"backtrace start=({startCol},{startRow}) seed=({goalCol},{goalRow})/{n}";
                return null;
            }

            var wps = new List<Vector3>(cells.Count + 2) { start };
            foreach (var (c, r) in cells)
                wps.Add(new Vector3(pts[c].x, pts[c].y, r * 0.25f));
            // Final point at the band-resolved depth (goalRow·0.25), not the raw target z — see the
            // LOS note above: a raw-depth tail segment makes the walker face the depth axis.
            wps.Add(new Vector3(goal.x, goal.y, goalRow * 0.25f));

            // Interior settle (§4.5, flood path only): slide each interior waypoint along +y
            // (deeper) one sub-cell at a time while the row below it carries the floor bit
            // (+0x3c & 2) — the probe row is subY+1, not the waypoint's own row
            // (FUN_0041d8a0:985 `local_38 = (int)ROUND(y·8) + 1`, the &2 test:1013,
            // `y += 0.125`:1022). Terminal state = the waypoint rests on the last floor row of
            // the stack. Probing the own row instead overshoots one row past the ground line onto
            // the solid row below, where the AirClamp emit guard then raises z to 15.
            for (int i = 1; i < wps.Count - 1; i++)
            {
                var w = wps[i];
                int sx = Mathf.RoundToInt(w.x * 8f), guard = 0;
                while ((nav.SubAt(sx, Mathf.RoundToInt(w.y * 8f) + 1) & NavWorld.SubFloor) != 0 && guard++ < 64)
                    w.y += 0.125f;
                wps[i] = w;
            }
            return wps;
        }

        // FUN_0041bff0: one point per 1.0 sub-cell of polyline length (remainder carried between
        // pairs), z preset to 15.0 (the full/undug slab nibble); final endpoint appended (the
        // native appends the last control cell — our callers' goal (x,y) is exactly that cell).
        //
        // The segment length is the sqrt of the integer-subcell d² — FUN_0040b7e0 is an
        // XMM0-in/XMM0-out sqrt helper Ghidra renders as a bodyless void call, so the decompile
        // silently reads the pre-sqrt d². One point per world unit would make every depth-grid
        // column a full tile: a single riser-boundary column whose ROUND(x·8) crossed into the next
        // terrace masks a whole tile of profile, and three such in a row outrun the flood's
        // |Δcol|≤2 hops and null the whole ascent. At the native sub-cell pitch those mismatch
        // columns are isolated and the flood hops them (asm: 0041bff0 call 0x40b7e0 on d² at the
        // loop head).
        static List<Vector3> Densify(IReadOnlyList<Vector2> control, Vector3 goal)
        {
            var pts = new List<Vector3>();
            const float subCellStep = 0.125f;   // 1.0 native subcell in world units
            float rem = 0f;
            for (int i = 0; i + 1 < control.Count; i++)
            {
                Vector2 p0 = control[i], p1 = control[i + 1];
                float len = Vector2.Distance(p0, p1);
                if (len <= 1e-5f) continue;
                float t = rem;
                while (t < len)
                {
                    Vector2 p = Vector2.Lerp(p0, p1, t / len);
                    pts.Add(new Vector3(p.x, p.y, 15f));
                    t += subCellStep;
                }
                rem = t - len;
            }
            pts.Add(new Vector3(goal.x, goal.y, 15f));
            return pts;
        }

        // FUN_0041c4b0:74-129 — nudge each point's y onto the nearest floor lip: search d=0,1,2
        // down (and d>>1 up) for the first row whose height steps >2 over the row above (the same
        // predicate as the EFFPASSWALK value-3 floor line) and snap y to that row.
        static void LipSnap(NavWorld nav, List<Vector3> pts)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                int sx = (int)(p.x * 8f), sy = (int)(p.y * 8f);   // truncate, as the original
                for (int d = 0; d < 3; d++)
                {
                    if (nav.GetHmapSub(sx, sy + d) - nav.GetHmapSub(sx, sy + d - 1) > 2)
                    { p.y = (sy + d) * 0.125f; pts[i] = p; break; }
                    int up = sy - (d >> 1);
                    if (nav.GetHmapSub(sx, up) - nav.GetHmapSub(sx, up - 1) > 2)
                    { p.y = up * 0.125f; pts[i] = p; break; }
                }
            }
        }

        // FUN_0041c4b0:135-226 — per column, the legal depth band is [floorH*4+1, ceilH*4-1) with
        // floorH = h(sx, sy-1) (one row up = the support row) and ceilH = h(sx, sy+1). A solid tile
        // (gate byte +6 == -1, our BaseCost -1) leaves the defaults floorH=1000/ceilH=0 -> the whole
        // column masked. (climb-route.md claims solid tiles are "left fully open"; the decompile's
        //:155 `local_38 = 1000` default proves the opposite.)
        // Failure-dump helpers: a cell is band-legal when no mask bit is set (StampBands leaves
        // it clear inside the column's standable band). Strings only build on the failure path.
        static string DescribeColumnBand(uint[] grid, int col)
        {
            int first = -1, last = -1;
            for (int row = 0; row < 64; row++)
            {
                if ((grid[col * 64 + row] & MaskBits) != 0) continue;
                if (first < 0) first = row;
                last = row;
            }
            return first < 0 ? "empty" : $"{first}..{last}";
        }

        static string DescribeBands(uint[] grid, int n)
        {
            var bands = new System.Text.StringBuilder();
            for (int col = 0; col < n; col++)
                bands.Append($"{col}:{DescribeColumnBand(grid, col)} ");
            return bands.ToString().TrimEnd();
        }

        static void StampBands(NavWorld nav, List<Vector3> pts, uint[] grid)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                int sx = Mathf.RoundToInt(pts[i].x * 8f);
                int syM1 = Mathf.RoundToInt(pts[i].y * 8f) - 1;
                int floorH = 1000, ceilH = 0;
                if (nav.TileValid(sx >> 3, syM1 >> 3))
                {
                    floorH = nav.GetHmapSub(sx, syM1);
                    ceilH = nav.GetHmapSub(sx, syM1 + 2);
                }
                int b = i * 64;
                for (int r = 0; r < 64; r++)
                {
                    if (r < floorH * 4 + 1) grid[b + r] |= MaskTooLow;
                    else if (r >= ceilH * 4 - 1) grid[b + r] |= MaskTooHigh;
                }
            }
        }

        static int NearestPoint(List<Vector3> pts, Vector3 p)
        {
            int best = 0; float bestD = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].x - p.x, dy = pts[i].y - p.y;
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; if (d < 0.01f) break; }   // 0.01 early-out (§4.1)
            }
            return best;
        }

        // FUN_0041bbd0 — ±16 col × ±16 row window, nearest free cell by squared cell distance.
        // "Free" before the flood = unmasked (mask==0 && cost==0).
        static bool SnapHoriz(uint[] grid, int n, ref int col, ref int row)
            => SnapWindow(grid, n, ref col, ref row, col - 16, col + 16, row - 16, row + 16);

        // FUN_0041bde0 — ±256-row band in the single column col-1 (§V.1).
        static bool SnapVert(uint[] grid, int n, ref int col, ref int row)
            => SnapWindow(grid, n, ref col, ref row, col - 1, col, row - 256, row + 256);

        // Diagnostic counter: how many times the goal-pin fallback below actually engaged. The pin
        // is a T-junction law (a run-goal depth outside the rim band); ordinary open-floor goals
        // are always band-snappable, so NavMeadowDepthSweepTests asserts this stays 0. Reset by
        // tests; never read by runtime code.
        public static int PinFallbackFires;

        // The goal-pin fallback (see the failure branch in Build): nearest free row to `row`
        // within the single column `col` over the full 0..63 span. Mirrors FUN_0041bbd0's
        // "never fail — pin and proceed" contract: the raw pinned cell is masked and a masked seed
        // would drown the flood, so the nearest legal row of the same column is the usable pin.
        static bool PinNearestLegalRow(uint[] grid, int col, ref int row)
        {
            PinFallbackFires++;
            int best = -1, bestD = int.MaxValue;
            for (int r = 0; r < 64; r++)
            {
                uint cell = grid[col * 64 + r];
                if ((cell & MaskBits) != 0 || (cell & CostBits) != 0) continue;
                int d = (r - row) * (r - row);
                if (d < bestD) { bestD = d; best = r; }
            }
            if (best < 0) return false;
            row = best;
            return true;
        }

        static bool SnapWindow(uint[] grid, int n, ref int col, ref int row,
            int c0, int c1, int r0, int r1)
        {
            int cc = Mathf.Clamp(col, 0, n - 1), cr = Mathf.Clamp(row, 0, 63);
            if ((grid[cc * 64 + cr] & MaskBits) == 0 && (grid[cc * 64 + cr] & CostBits) == 0)
            { col = cc; row = cr; return true; }
            c0 = Mathf.Max(0, c0); c1 = Mathf.Min(n - 1, c1);
            r0 = Mathf.Max(0, r0); r1 = Mathf.Min(63, r1);
            int bc = -1, br = -1, bestD = int.MaxValue;
            for (int c = c0; c <= c1; c++)
                for (int r = r0; r <= r1; r++)
                {
                    uint cell = grid[c * 64 + r];
                    if ((cell & MaskBits) != 0 || (cell & CostBits) != 0) continue;
                    int d = (c - col) * (c - col) + (r - row) * (r - row);
                    if (d < bestD) { bestD = d; bc = c; br = r; }
                }
            if (bc < 0) return false;
            col = bc; row = br;
            return true;
        }

        // §4.2 — march floor(dist)+1 steps along the (col,row) line, truncating each lerped
        // component (cvttss2si, §V.3); blocked when any cell carries a mask bit.
        static bool LosClear(uint[] grid, int n, int c0, int r0, int c1, int r1)
        {
            float dist = Mathf.Sqrt((c1 - c0) * (c1 - c0) + (r1 - r0) * (r1 - r0));
            int steps = (int)dist + 1;
            for (int i = 0; i <= steps; i++)
            {
                float t = steps > 0 ? (float)i / steps : 0f;
                int c = (int)Mathf.Lerp(c0, c1, t), r = (int)Mathf.Lerp(r0, r1, t);
                if (c < 0 || c >= n || r < 0 || r > 63) return false;
                if ((grid[c * 64 + r] & MaskBits) != 0) return false;
            }
            return true;
        }

        static void Flood(uint[] grid, int n, int seedCol, int seedRow)
        {
            seedCol = Mathf.Clamp(seedCol, 0, n - 1); seedRow = Mathf.Clamp(seedRow, 0, 63);
            grid[seedCol * 64 + seedRow] =
                (grid[seedCol * 64 + seedRow] & MaskBits) | SeedCost;
            var fifo = new Queue<(int c, int r)>();
            fifo.Enqueue((seedCol, seedRow));
            int guard = n * 64 * 8;
            while (fifo.Count > 0 && guard-- > 0)
            {
                var (c, r) = fifo.Dequeue();
                uint cost = grid[c * 64 + r] & CostBits;
                for (int pass = 0; pass < 5; pass++)
                {
                    long nc = (long)cost - PassCost[pass];
                    if (nc <= 0) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        int cc = c + PassOff[pass, k, 0], rr = r + PassOff[pass, k, 1];
                        if (cc < 0 || cc >= n || rr < 0 || rr > 63) continue;
                        uint cell = grid[cc * 64 + rr];
                        if ((cell & MaskBits) != 0) continue;          // banded-out (0041d8a0:388)
                        if ((uint)nc <= (cell & CostBits)) continue;   // keep-max relax (:495)
                        grid[cc * 64 + rr] = (cell & MaskBits) | (uint)nc;
                        fifo.Enqueue((cc, rr));
                    }
                }
            }
        }

        static List<(int c, int r)>? Backtrace(uint[] grid, int n,
            int col, int row, int seedCol, int seedRow)
        {
            var cells = new List<(int, int)>();
            int guard = n * 64;
            while (guard-- > 0)
            {
                cells.Add((col, row));
                if (col == seedCol && row == seedRow) return cells;
                uint cur = grid[col * 64 + row] & CostBits;
                int bc = -1, br = -1; uint best = cur;
                for (int pass = 0; pass < 5; pass++)
                    for (int k = 0; k < 4; k++)
                    {
                        int cc = col + PassOff[pass, k, 0], rr = row + PassOff[pass, k, 1];
                        if (cc < 0 || cc >= n || rr < 0 || rr > 63) continue;
                        uint cost = grid[cc * 64 + rr] & CostBits;
                        if (cost > best) { best = cost; bc = cc; br = rr; }
                    }
                if (bc < 0) return null;   // gradient stalled — no route to the seed
                col = bc; row = br;
            }
            return null;
        }
    }
}
