#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Play-mirror repros for the findings (first LIVE world growth):
    // 1. lateral growth works but the mesh at the carve↔stamp SEAM has holes;
    // 2. digging DOWN from the start bowl generated nothing.
    // Both reproduce the EXACT Play chain headless: real campaign boot (single_CampaignX
    // wrapper), the real marquee laws (DigMarquee snap + FUN_00566870 validity — the same
    // paint loop ClickToMoveService.CommitDigMarquee runs), real dig_apply swings, and the
    // StoryMgr think loop driving generate_next/load_next into RuntimeMapBuilder live mode.
    public sealed class WorldGrowthPlayReproTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _spawnedGos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawnedGos) if (go != null) Object.DestroyImmediate(go);
            _spawnedGos.Clear();
        }

        static (GameObject root, DigglesEngine engine) BootCampaign(List<GameObject> own)
        {
            var rootGo = new GameObject("WorldRoot");
            own.Add(rootGo);
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl",
                                      "scripts/init/classinit.tcl", "scripts/init/lginit.tcl" })
                try { engine.Interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(engine.Ctx.Classes.ContainsKey("StoryMgr"), "classinit must register StoryMgr");
            engine.Interp.Eval("obj_clear\nsel /obj\nset sm [new StoryMgr]\ncall_method $sm load_level Campaign");
            engine.Map.EmitTerrain(rootGo);
            engine.Ctx.Terrain = rootGo.GetComponent<DiggableTerrain>();
            return (rootGo, engine);
        }

        static DigglesTclEntity? FindByClass(DigglesTclContext ctx, string cls)
        {
            foreach (var e in ctx.Entities.Values) if (e.Class?.Name == cls) return e;
            return null;
        }

        // The exact CommitDigMarquee standard-tunnel paint loop (ClickToMoveService:672-712 —
        // reproduced here because the service needs a scene/camera): FUN_00566870 validity per
        // cell, mode-1 dummy nibble on owner 0.
        static int PaintStandardMarquee(DiggableTerrain t, int x0, int y0, int x1, int y1)
        {
            bool Solid(int x, int y) => t.NwCornerHeight(x, y) > 1;
            bool Open(int x, int y) => (t.RawDigNibble(x, y, 0) & 8) != 0;
            // FUN_00566870:26-28 — the material-class gate (rejects stone 0 / open 3), exactly
            // ClickToMoveService's _vExposed wiring (:809).
            bool Exposed(int x, int y) => t.TileMaterialDiggable(x, y);
            int marked = 0;
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!DigMarquee.CellValid(tx, ty, t.Width, t.Height, Solid, Open, Exposed)) continue;
                    t.SetDigMark(tx, ty, 0, 0, dummy: true);
                    marked++;
                }
            t.IncPlayerDigMarkId();
            return marked;
        }

        static int CountLive(DigglesEngine engine)
        {
            int n = 0;
            foreach (var d in engine.Map.Diagnostics) if (d.Contains("LIVE pmp")) n++;
            return n;
        }

        // ===== finding 2 — the DOWN shaft from the start bowl =====
        //
        // Mirror of the player's Play session: pick the bowl floor under a spawned dwarf, order a
        // standard-tunnel marquee straight DOWN (SnapAnchor/SnapDragCorner/SnapRect — exactly 2
        // wide), dig it out with dig_apply swings row by row while the StoryMgr think loop runs,
        // and require the growth chain to stamp templates below the bowl. The original grows
        // DOWNWARD by design: DigNotify's +y probe carries the LARGEST weight (+4,
        // FUN_005fd060:113 vs +2 lateral / +1 up) and the Urwald pool has real shaft pieces
        // (gng_027/028/029/030/032 — 43 top/bottom doors in the.ent data).
        [Test, Explicit("Boots the full real campaign + drives the growth loop — run on demand")]
        public void DownShaft_FromStartBowl_GrowsWorld()
        {
            var (root, engine) = BootCampaign(_spawnedGos);
            var ctx = engine.Ctx;
            var interp = engine.Interp;
            var terr = ctx.Terrain!;
            Assert.IsTrue(engine.Map.LiveMode, "post-EmitTerrain the map builder must be in live mode");

            // settle StartCnt so the idle loop reaches sm_scan
            for (int i = 0; i < 70; i++) engine.Sim.Step(0.1);

            // ---- locate the bowl floor: first solid tile under a dwarf ----
            var dwarf = FindByClass(ctx, "Zwerg");
            Assert.IsNotNull(dwarf, "campaign must spawn dwarves");
            int zx = Mathf.RoundToInt(dwarf!.Position.x), zy = Mathf.RoundToInt(dwarf.Position.y);
            int floorY = zy;
            while (floorY < terr.Height - 1 && !terr.Solid(zx, floorY)) floorY++;
            TestContext.WriteLine($"dwarf at ({zx},{zy}); bowl floor at y={floorY}");

            // ---- story-map occupancy around the bowl (which cells the boot marked grown) ----
            int cellX = (zx + ((zx >> 31) & 3)) >> 2;
            var sb = new System.Text.StringBuilder("story map rows (cells, x " + (cellX - 8) + ".." + (cellX + 8) + "):\n");
            int lastGrownCy = 0;
            for (int cy = 0; cy < 60; cy++)
            {
                sb.Append($"cy={cy,3}: ");
                for (int cx = cellX - 8; cx <= cellX + 8; cx++)
                {
                    int v = interp.Eval($"sm_map_get {cx} {cy}").Trim() == "1" ? 1 : 0;
                    sb.Append(v == 1 ? '#' : '.');
                    if (v == 1 && (cx == cellX || cx == cellX + 1)) lastGrownCy = Mathf.Max(lastGrownCy, cy);
                }
                sb.Append('\n');
            }
            TestContext.WriteLine(sb.ToString());
            TestContext.WriteLine($"deepest grown cell in the shaft columns: cy={lastGrownCy} (tileY {lastGrownCy * 4 + 3})");

            // ---- the marquee: click the bowl floor, drag straight down (the real snap laws) ----
            var (ax, ay) = DigMarquee.SnapAnchor(zx, floorY + 1);
            // deep enough to exit the boot-marked story cells + 3 more cells of margin
            int wantBottom = Mathf.Min(terr.Height - 12, System.Math.Max((lastGrownCy + 4) * 4, floorY + 24));
            var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, wantBottom);
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(ax, ay, bx, by);
            int marked = PaintStandardMarquee(terr, x0, y0, x1, y1);
            TestContext.WriteLine($"marquee rect ({x0},{y0})..({x1},{y1}) marked={marked}");
            Assert.Greater(marked, 0, "the shaft marquee must mark tiles");

            // ---- dig the shaft top→bottom until a breakthrough latches BELOW the pre-grown start
            // caves, then STOP and let the concurrent StoryMgr think loop consume it ----
            //
            // Faithful to Play + the passing Growth_DigToGeneration reference: DigNotify clears the
            // latch on EVERY dig_apply (FUN_005fd060:66-70) and only re-sets it when weight>digcount
            // (:90/:114). So a live latch survives ONLY between digs — never after a "dig the whole
            // shaft then tick" burst. The reliable pattern is: dig until the latch fires, then STOP
            // (leaving it live) and drain the sim so the StoryMgr's idle sm_scan consumes it.
            //
            // The DOWN shaft twist: the bowl's start caves are PRE-GROWN (cy 9..17 above). A latch
            // pointing INTO them (a shallow, bowl-adjacent breakthrough) gives a free rect boxed by
            // those grown cells → generate_next's "too small" abort → no growth. Only a latch whose
            // cell is BELOW the caves (cy > lastGrownCy) has room. So keep digging the shaft down
            // (each dig re-sets a fresh down-latch; the intermediate shallow ones are simply cleared
            // by the next dig) until the current latch points deep, then stop + drain.
            int before = CountLive(engine);
            int digs = 0;
            int StoryCellOf(int v) => (v + ((v >> 31) & 3)) >> 2;

            // ---- dig until a DEEP down-latch, drain, and RETRY on generation failure ----
            // The faithful generator may legitimately fail a run (story failcount law,
            // story.tcl:212-222); a FAILED (empty) generation does not reset the weight counter
            // (the reset lives in the placement-emitting lg_get_level path, FUN_00652d10:280),
            // so in Play the very next dig re-latches and the StoryMgr retries. Mirror that:
            // keep digging the shaft further down and re-draining until templates stamp (or the
            // shaft runs out). After a SUCCESS the weight re-arms to 0 — further digs latch only
            // once a fresh sm_set_digcount worth of pressure accumulates.
            int steps = 0, tyCursor = y0;
            for (int attempt = 0; attempt < 8 && CountLive(engine) == before && tyCursor <= y1; attempt++)
            {
                bool deepLatch = false;
                for (; tyCursor <= y1 && !deepLatch; tyCursor++)
                {
                    if (!terr.IsDigMarked(x0, tyCursor, 0)) continue;   // dig the leftmost shaft column
                    interp.Eval($"dig_apply {{{x0} {tyCursor} 8}}");
                    digs++;
                    deepLatch = ctx.StoryLatchDir == 2 && !ctx.StoryLatchConsumed
                                && StoryCellOf(ctx.StoryLatchY) > lastGrownCy;
                }
                TestContext.WriteLine($"attempt {attempt}: {digs} digs so far, weight={ctx.StoryDigWeight} " +
                                      $"deepLatch={deepLatch} latch dir={ctx.StoryLatchDir} at " +
                                      $"({ctx.StoryLatchX},{ctx.StoryLatchY}) cellY={StoryCellOf(ctx.StoryLatchY)}");
                if (!deepLatch) break;
                // drain: idle loop consumes the latch → generate_next → lg_start → load_next
                for (int s = 0; s < 600 && CountLive(engine) == before; s++, steps++) engine.Sim.Step(0.1);
            }
            for (int i = 0; i < 120; i++) engine.Sim.Step(0.1);   // let more templates stream
            int lastLive = -1;
            for (int round = 0; round < 20 && CountLive(engine) != lastLive; round++)
            {
                lastLive = CountLive(engine);
                for (int i = 0; i < 60; i++) engine.Sim.Step(0.1);
            }
            int live = CountLive(engine) - before;
            TestContext.WriteLine($"drained {steps} steps to first stamp");

            var smEnt = FindByClass(ctx, "StoryMgr")!;
            string SmVar(string n) => smEnt.VarsScope.Vars.TryGetValue(n, out var v) ? v : "<unset>";
            TestContext.WriteLine($"sm state: gencount={SmVar("gencount")} failcount={SmVar("failcount")} " +
                                  $"temp_buffer='{SmVar("temp_buffer")}' utemp='{ctx.StoryUtemp}' " +
                                  $"rect=({ctx.StoryRectX},{ctx.StoryRectY} {ctx.StoryRectW}x{ctx.StoryRectH})");
            foreach (var d in engine.Map.Diagnostics) if (d.Contains("LIVE")) TestContext.WriteLine(d);
            TestContext.WriteLine($"{live} live stamps");

            Assert.Greater(live, 0, "digging DOWN out of the start bowl must grow the world (stamps below the bowl)");

            // the growth must land BELOW the bowl (not just anywhere)
            var rx = new System.Text.RegularExpressions.Regex(@"LIVE pmp '[^']+' @\((-?\d+),(-?\d+)\)");
            bool anyBelow = false;
            foreach (var d in engine.Map.Diagnostics)
            {
                var m = rx.Match(d);
                if (m.Success && int.Parse(m.Groups[2].Value) >= floorY) anyBelow = true;
            }
            Assert.IsTrue(anyBelow, "at least one stamp must land below the bowl floor");
        }

        // ===== finding 1 — seam integrity: carve↔stamp interface must be watertight =====
        //
        // THE JUNCTION LAW: the growth stamp NEVER overlaps the
        // player's tunnel — the anchor walk PRE-STEPS one 4-tile cell along the dig direction
        // (FUN_00650f90:182 = FUN_005f7a90 on &latchDir), so the dig-direction start piece's
        // top-left = latchCell + 1 cell; the blit is destination-blind template-wins
        // (FUN_005992b0 — only source transparency skips,:292) and preserves the dig-mark
        // nibbles (+0x08 untouched); NO seam reconciliation exists natively (the effect passes
        // re-derive DERIVED data only). This test drives the REAL chain (campaign boot →
        // marquee → dig_apply swings → StoryMgr tick → growth stamp) and asserts:
        // 1. the start piece landed at the pre-stepped anchor, one cell beyond the latch;
        // 2. its template data is VERBATIM in the world (no half-buried mutation);
        // 3. dig marks inside the stamp rect survived the stamp;
        // 4. no invisible stamped tiles; 5. the rendered mesh stays watertight.
        [Test, Explicit("Boots the full real campaign + drives the growth loop — run on demand")]
        public void GrowthStamp_NextToCarvedTunnel_SeamIsWatertight()
        {
            var (root, engine) = BootCampaign(_spawnedGos);
            var ctx = engine.Ctx;
            var interp = engine.Interp;
            var terr = ctx.Terrain!;

            for (int i = 0; i < 70; i++) engine.Sim.Step(0.1);

            // BASELINE: the pristine authored world (no dig, no growth) must already be watertight.
            // If it is NOT, the invariant is flagging authored geometry, not the carve↔stamp seam.
            terr.FlushRemesh();
            var baseBad = SeamMismatches(engine, terr);
            TestContext.WriteLine($"BASELINE pristine-world seam mismatches: {baseBad.Count}");
            for (int bi = 0; bi < System.Math.Min(6, baseBad.Count); bi++) TestContext.WriteLine("  base " + baseBad[bi]);

            // ---- find a lateral dig site like the money test (virgin solid, un-grown,
            // DIGGABLE — the sm_draw_stone -border top band makes rows ≤ ~30 stone class 0 and
            // the side bands mark their story cells grown, so start below/inside the interior) ----
            int dx = -1, dy0 = -1;
            for (int y0s = 40; y0s < 200 && dx < 0; y0s += 4)
                for (int x = 40; x < terr.Width - 48; x += 4)
                {
                    bool ok = true;
                    for (int k = 0; k <= 20 && ok; k++)
                        for (int ox = 0; ox < 4 && ok; ox++)
                            ok = terr.HasGeometry(x + ox, y0s + k) && terr.TileMaterialDiggable(x + ox, y0s + k);
                    for (int k = 0; k <= 6 && ok; k++)
                        ok = interp.Eval($"sm_map_get {x / 4} {y0s / 4 + k}").Trim() == "0";
                    if (ok) { dx = x; dy0 = y0s; break; }
                }
            Assert.GreaterOrEqual(dx, 0, "no virgin solid dig site found");
            TestContext.WriteLine($"dig site: column x={dx}..{dx + 3}, y from {dy0}");

            // ---- a real downward tunnel: marquee + swings (carves the tiles as Play does) ----
            var (ax, ay) = DigMarquee.SnapAnchor(dx + 1, dy0);
            var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, ax, dy0 + 20);
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(ax, ay, bx, by);
            int marked = PaintStandardMarquee(terr, x0, y0, x1, y1);
            Assert.Greater(marked, 0, "tunnel marquee must mark tiles");

            // Growth trigger (same reliable pattern as the shaft test + the passing
            // Growth_DigToGeneration reference): dig the column DOWN one swing per tile until a
            // downward breakthrough latches, then STOP (leaving the latch live) and drain so the
            // concurrent StoryMgr idle loop consumes it (DigNotify clears the latch on every dig,
            // FUN_005fd060:66-70, so it only survives BETWEEN digs). This virgin site is all
            // un-grown, so the first latch already has a big free rect — no "too small" abort.
            int before = CountLive(engine);
            bool latched = false;
            int steps = 0, tyCur = y0;
            int latX = 0, latY = 0;   // the breakthrough the story tick consumes (captured before
                                      // later digs overwrite the latch fields)
            // dig → latch → drain, retrying on generation failure (story failcount law — the
            // faithful generator may legitimately fail a run; the next dig re-latches).
            for (int attempt = 0; attempt < 8 && CountLive(engine) == before && tyCur <= y1; attempt++)
            {
                latched = false;
                for (; tyCur <= y1 && !latched; tyCur++)
                {
                    if (!terr.IsDigMarked(x0, tyCur, 0)) continue;
                    interp.Eval($"dig_apply {{{x0} {tyCur} 8}}");
                    latched = ctx.StoryLatchDir == 2 && !ctx.StoryLatchConsumed;
                }
                if (!latched) break;
                latX = ctx.StoryLatchX; latY = ctx.StoryLatchY;
                for (int s = 0; s < 600 && CountLive(engine) == before; s++, steps++) engine.Sim.Step(0.1);
            }
            for (int i = 0; i < 120; i++) engine.Sim.Step(0.1);
            // Stream the WHOLE generation in (one piece per 0.5 s tick) BEFORE the junction
            // checks — the start piece may be anywhere in the distance-sorted temp_buffer.
            int lastLive = -1;
            for (int round = 0; round < 20 && CountLive(engine) != lastLive; round++)
            {
                lastLive = CountLive(engine);
                for (int i = 0; i < 60; i++) engine.Sim.Step(0.1);
            }
            int live = CountLive(engine) - before;
            TestContext.WriteLine($"{live} live stamps after the tunnel (latched={latched}, drained {steps} steps)");
            foreach (var d in engine.Map.Diagnostics) if (d.Contains("LIVE")) TestContext.WriteLine(d);
            Assert.Greater(live, 0, "the tunnel must trigger growth (precondition for the seam check)");

            // ---- THE JUNCTION LAW (byte-) ----
            // Down-dig breakthrough at latch (latX,latY): the anchor walk PRE-STEPS one cell
            // (FUN_00650f90:182), so the dig-direction start piece (a 4×4 {left,right}
            // through-corridor, FUN_00585d80:58-110) lands its top-left at
            // (latX&~3, (latY&~3)+4) — one full cell of rock past the latch cell, never on the
            // tunnel. This virgin site has no HasTpl overlap → walk iteration 0 must have won.
            {
                int poseX = latX & ~3, poseY = (latY & ~3) + 4;
                var lg = ctx.LevelGen!;
                string startTcl = "";
                var all = new System.Text.StringBuilder();
                foreach (var (tcl, px, py, pw, ph) in lg.LastLevelRects())
                {
                    all.Append($"{tcl}@({px},{py}) ");
                    if (px == poseX && py == poseY && pw == 4 && ph == 4) startTcl = tcl;
                }
                TestContext.WriteLine($"placements: {all}");
                Assert.IsNotEmpty(startTcl,
                    $"the start piece must sit at the pre-stepped anchor ({poseX},{poseY}) — placements: {all}");

                // VERBATIM TEMPLATE DATA at the junction: outside the player's tunnel columns
                // (x0..x1 — the only tiles a later carve may legally change), every stamped tile
                // of the start piece must hold the authored PMP block — the exact assertion the
                // Play defect violated
                // half the crossing corridor held non-authored earth).
                string? pmpBase = lg.GetPmpBase(startTcl);
                Assert.IsNotNull(pmpBase, $"PMP base of '{startTcl}' must resolve");
                var startPmp = PmpParser.Parse(System.IO.Path.Combine(
                    DataRoot, "Templates/pmp", pmpBase + ".pmp"));
                var res = engine.Map.WorldResult!;
                int buried = 0;
                for (int py2 = 0; py2 < 4; py2++)
                    for (int px2 = 0; px2 < 4; px2++)
                    {
                        int wx2 = poseX + px2, wy2 = poseY + py2;
                        if (wx2 >= x0 && wx2 <= x1) continue;              // tunnel columns: carve may differ
                        if (BdbPmpWorldBuilder.PmpTileTransparent(startPmp, px2, py2)) continue;
                        uint want = startPmp.BlockIdxAt(px2, py2);
                        uint got = res.BlockIdx[wy2 * res.Width + wx2];
                        if (got != want)
                        {
                            buried++;
                            TestContext.WriteLine($"MUTATED template tile ({wx2},{wy2}): block {got} != authored {want}");
                        }
                    }
                Assert.AreEqual(0, buried,
                    "the stamped start piece must be VERBATIM outside the tunnel columns (no half-buried corridor)");

                // DIG MARKS SURVIVE THE STAMP (+0x08 never touched by the blit): the marquee
                // rows inside the stamp rect were marked but not yet dug when the stamp landed —
                // they must still carry the order (this is what lets the dwarf dig through the
                // template mouth; the full-carve loop below exercises exactly that).
                int markedInStamp = 0;
                for (int wy2 = poseY; wy2 < poseY + 4; wy2++)
                    if (wy2 >= y0 && wy2 <= y1 && terr.IsDigMarked(x0, wy2, 0)) markedInStamp++;
                TestContext.WriteLine($"marks surviving inside the stamp rect: {markedInStamp}");
                Assert.Greater(markedInStamp, 0,
                    "the tunnel order's marks inside the stamp rect must survive the stamp (native +0x08 law)");

                // Now DIG THROUGH THE TEMPLATE MOUTH: fully carve the whole marked tunnel — the
                // surviving marks must let the carve proceed straight through the stamped piece
                // (the end-to-end junction the Play defect broke). More growth may trigger
                // mid-carve (the weight re-armed after the success) — that's the native flow.
                for (int ty = y0; ty <= y1; ty++)
                    for (int tx = x0; tx <= x1; tx++)
                    {
                        if (!terr.IsDigMarked(tx, ty, 0)) continue;
                        for (int swing = 0; swing < 8; swing++)
                            if (interp.Eval($"dig_apply {{{tx} {ty} 8}}").Trim() != "1") break;
                    }
                // the carve must have progressed INTO the stamp rect on the tunnel columns —
                // at least one tile there deviates from the authored block now (the carve's
                // block-transition swap) or is fully dug.
                int carvedInStamp = 0;
                for (int wy2 = poseY; wy2 < poseY + 4; wy2++)
                    for (int wx2 = x0; wx2 <= x1; wx2++)
                    {
                        if (wy2 < y0 || wy2 > y1) continue;
                        int ii = wy2 * res.Width + wx2;
                        int lx2 = wx2 - poseX, ly2 = wy2 - poseY;
                        if (lx2 < 0 || lx2 > 3) continue;
                        uint authored = startPmp.BlockIdxAt(lx2, ly2);
                        if (res.BlockIdx[ii] != authored) carvedInStamp++;
                    }
                TestContext.WriteLine($"carve-through progressed on {carvedInStamp} stamp-rect tunnel tiles");
                Assert.Greater(carvedInStamp, 0,
                    "the dwarf must be able to dig THROUGH the stamped template mouth (junction end-to-end)");
            }

            // let any mid-carve generations finish streaming before the mesh checks
            {
                int ll = -1;
                for (int round = 0; round < 20 && CountLive(engine) != ll; round++)
                {
                    ll = CountLive(engine);
                    for (int i = 0; i < 60; i++) engine.Sim.Step(0.1);
                }
            }

            // flush the debounced remesh so chunk meshes reflect the final data
            terr.FlushRemesh();

            // ---- NO INVISIBLE STAMPED TILES (the metric's old blind spot) ----
            // The seam metric below only compares two RENDERED neighbours — a stamped tile that
            // renders NOTHING (block 59 with the DugFloor mask unset — the renderer's skip in
            // RemeshFromArrays) was never flagged. Native law: template block 59 = an authored
            // PRE-CUT open cell (hol_023_cut vs hol_023) that must render its dug floor. Scan
            // every LIVE stamp rect (from the diagnostics) for such tiles.
            {
                var res = engine.Map.WorldResult!;
                var rxDims = new System.Text.RegularExpressions.Regex(
                    @"LIVE pmp '[^']+' @\((-?\d+),(-?\d+)\) (\d+)x(\d+)");
                var invisible = new List<string>();
                foreach (var d in engine.Map.Diagnostics)
                {
                    var m = rxDims.Match(d);
                    if (!m.Success) continue;
                    int sx0 = int.Parse(m.Groups[1].Value), sy0 = int.Parse(m.Groups[2].Value);
                    int sw = int.Parse(m.Groups[3].Value), sh = int.Parse(m.Groups[4].Value);
                    for (int ty = sy0; ty < sy0 + sh && invisible.Count < 50; ty++)
                        for (int tx = sx0; tx < sx0 + sw; tx++)
                        {
                            if (tx < 0 || ty < 0 || tx >= res.Width || ty >= res.Height) continue;
                            int ii = ty * res.Width + tx;
                            if (res.BlockIdx[ii] == 59 && (res.DugFloor == null || !res.DugFloor[ii]))
                                invisible.Add($"({tx},{ty}) in {d.Trim()}");
                        }
                }
                foreach (var s in invisible) TestContext.WriteLine("INVISIBLE STAMPED TILE " + s);
                Assert.AreEqual(0, invisible.Count,
                    "a stamped block-59 (pre-cut) tile must render its dug floor — invisible tiles are see-through holes");
            }

            // ---- the seam invariant: no NEW mesh gap beyond the pristine authored baseline ----
            // (baseSet almost always empty — the render-edge metric passes the authored world; the
            // subtraction just guards against a fragile authored corner being pre-counted.)
            var baseSet = new HashSet<string>(baseBad);
            var bad = SeamMismatches(engine, terr);
            var added = new List<string>();
            foreach (var m in bad) if (!baseSet.Contains(m)) added.Add(m);
            foreach (var m in added) TestContext.WriteLine("NEW HOLE " + m);
            TestContext.WriteLine($"{bad.Count} total mesh gaps, {added.Count} NEW beyond the {baseBad.Count} pristine baseline");
            Assert.AreEqual(0, added.Count,
                "the carve↔stamp seam must add no NEW mesh gap (rendered edge z-intervals must meet)");
        }

        // ===== the WORLD-BORDER stone band (sm_draw_stone -border, FUN_0065e090:930-941) =====
        //
        // The original stops border digging with a 32-tile probabilistic band of stone class 0
        // painted at boot (Campaign.tcl:3) over the top/left/right edges — solid for the outer
        // ~16 tiles (see Docs/LEVEL_GENERATOR.md §WORLD-BORDER). A gnome teleported to the
        // right border and digging right must be stopped by the band (
        // "dug to the world edge").
        [Test, Explicit("Boots the full real campaign — run on demand")]
        public void WorldBorder_StoneBand_StopsDiggingAtTheEdge()
        {
            var (root, engine) = BootCampaign(_spawnedGos);
            var ctx = engine.Ctx; var terr = ctx.Terrain!;

            // band presence: the outer halves are SOLID class-0 stone (p = 1 region)
            int stoneR = 0, stoneL = 0, n = 0;
            for (int y = 64; y < 576; y += 3)
            { n++; if (terr.TileMaterialType(500, y) == 0) stoneR++; if (terr.TileMaterialType(5, y) == 0) stoneL++; }
            TestContext.WriteLine($"right x=500: {stoneR}/{n} stone; left x=5: {stoneL}/{n}");
            Assert.AreEqual(n, stoneR, "right border outer half must be solid stone class 0");
            Assert.AreEqual(n, stoneL, "left border outer half must be solid stone class 0");
            Assert.IsTrue(terr.TileMaterialType(0x100, 320) != 0, "map interior stays diggable");

            // the marker validity gate rejects the whole marquee inside the band
            var (ax, ay) = DigMarquee.SnapAnchor(500, 300);
            var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, 508, 300);
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(ax, ay, bx, by);
            int marked = PaintStandardMarquee(terr, x0, y0, x1, y1);
            Assert.AreEqual(0, marked, "the stone band must reject dig markers (FUN_00566870:26-28)");

            // the carve precondition refuses too, even with a force-injected mark
            terr.SetDigMark(501, 301, 0, 0, dummy: true);
            Assert.IsFalse(terr.CarveStep(501, 301, 0, out _),
                "the carve must refuse class-0 stone (FUN_005976b0:35-38) and clear the mark");
            Assert.IsFalse(terr.IsDigMarked(501, 301, 0), "the refused mark is cleared (FUN_00596b80:281-282)");

            // the band story-marks its rects (FUN_005fc9d0:139-141 + the:943 top-9-rows mark)
            Assert.AreEqual("1", engine.Interp.Eval("sm_map_get 126 40").Trim(), "right band cells grown");
            Assert.AreEqual("1", engine.Interp.Eval("sm_map_get 1 40").Trim(), "left band cells grown");
            Assert.AreEqual("1", engine.Interp.Eval("sm_map_get 64 4").Trim(), "top 9 cell rows grown");
        }

        // ===== finding 3 — a dug-out stone FALLS when the ground under it is carved away =====
        //
        // Loose items (a dug Stein, a Steinbrocken boulder, ore chunks) are def_class … material with
        // set_physic 1 (rohstoffe.tcl) — the original runs continuous item physics so they drop onto
        // the new floor when the ground under them is removed. The port has no item physics, so the
        // terrain reports every carve (DiggableTerrain.TilesChanged) and RuntimeWorldSpawner
        //.ResettleLooseItems re-runs the drop settle for items whose support column dropped. Here we
        // rest a Steinbrocken on the bowl floor, dig a pit straight under it, and require it to fall.
        [Test, Explicit("Boots the full real campaign — run on demand")]
        public void DugStone_SettlesWhenSupportCarvedAway()
        {
            var (root, engine) = BootCampaign(_spawnedGos);
            var ctx = engine.Ctx; var interp = engine.Interp; var terr = ctx.Terrain!;
            var spawner = (Diggles.Runtime.RuntimeWorldSpawner)ctx.EntitySpawner!;
            for (int i = 0; i < 70; i++) engine.Sim.Step(0.1);

            // wire the drop-on-dig hook exactly as DigglesWorldRuntime does in Play
            terr.TilesChanged += (a, b, c, d) => Diggles.Runtime.RuntimeWorldSpawner.ResettleLooseItems(
                terr.Nav, ctx.Entities.Values, a, b, c, d,
                e => spawner.ApplyPosition(e.GameObject, e.Position));

            // bowl floor under a dwarf
            var dwarf = FindByClass(ctx, "Zwerg");
            Assert.IsNotNull(dwarf, "campaign must spawn dwarves");
            int zx = Mathf.RoundToInt(dwarf!.Position.x), zy = Mathf.RoundToInt(dwarf.Position.y);
            int floorY = zy;
            while (floorY < terr.Height - 3 && !terr.Solid(zx, floorY)) floorY++;
            Assert.Less(floorY, terr.Height - 3, "need a solid floor under the dwarf");

            // spawn a Steinbrocken and rest it on that floor (the drop-settle we re-run on carve)
            interp.Eval("sel /obj");
            var stone = ctx.GetEntity(interp.Eval($"new Steinbrocken \"\" \"{zx} {floorY - 1} 9\" \"0 0 0\"").Trim());
            Assert.IsNotNull(stone, "Steinbrocken must spawn");
            Assert.AreEqual("material", stone!.Class?.Kind, "Steinbrocken is a material item");
            stone.Position = Diggles.Runtime.RuntimeWorldSpawner.SettleDropPosition(terr.Nav, stone.Position);
            spawner.ApplyPosition(stone.GameObject, stone.Position);
            float y0 = stone.Position.y;
            TestContext.WriteLine($"dwarf ({zx},{zy}); floor y={floorY}; stone rests at native {stone.Position}");

            // dig a pit straight DOWN under the stone — repeated node carves (the atomic dig_apply
            // unit). Each carve fires TilesChanged → ResettleLooseItems → the stone re-settles.
            int carves = 0;
            for (int pass = 0; pass < 16; pass++)
                for (int ny = floorY; ny <= floorY + 8; ny++)
                    for (int nx = zx - 1; nx <= zx + 2; nx++)
                        if (terr.CarveTileNow(nx, ny)) carves++;
            float y1 = stone.Position.y;
            TestContext.WriteLine($"{carves} node carves; stone native y {y0:F2} → {y1:F2} (world falls as native y grows)");

            Assert.Greater(carves, 0, "the pit must actually carve (else the test proves nothing)");
            // The drop-on-dig HOOK is what finding 3 fixes: any downward re-settle proves it fired
            // (the exact depth depends on how far the bowl-floor structure opens under the stone).
            Assert.Greater(y1, y0 + 0.25f,
                "a dug-out stone must FALL (native y grows = deeper) when the ground under it is carved away");
        }

        // WATERTIGHT invariant on the ACTUAL RENDERED MESH (not the 8×8 aux heightmap).
        //
        // Why not aux corners: the block's 8×8 heightmap corner (aux 0x00/07/38/3F) of two adjacent
        // tiles samples DIFFERENT sub-positions and is a coarse nav/collision field — it is NOT the
        // shared render edge. The pristine authored campaign world "mismatches" that metric ~201×
        // yet ships perfectly watertight. What the mesh actually stitches is each block's CELL
        // VERTICES: tile (tx,ty) emits vertices at world x = p.x+tx, so its EAST edge (palette x≈1)
        // and the east neighbour's WEST edge (palette x≈0) sit on the same world line x=tx+1. A HOLE
        // is a vertical GAP there — the two edges' z-extents are DISJOINT (e.g. a carved floor at
        // z≈7 abutting a stamped SOLID top at z≈14.5 with no cliff skirt between them). So per block
        // we take the [zmin,zmax] the mesh covers on each of the 4 palette edges and flag a seam
        // where the shared edges' intervals leave a gap > tol. Authored slopes (edge tops coincide)
        // and one-sided cliff skirts (one interval contains the other) both pass.
        static List<string> SeamMismatches(DigglesEngine engine, DiggableTerrain terr)
        {
            // A real see-through hole is many aux levels (the un-fixed bug was 2.0u = 14.5 vs 12.5).
            // Tolerate ≤1.0u residual ledges: the block-per-tile aux transition graph steps by
            // authored levels (…15→13→11…) and can get stuck one level (0.5u) short of a stamp floor,
            // leaving a minor step — not a see-through gap. 1.1 catches ≥1.5u holes, passes ≤1.0u.
            const float EPS = 0.02f, GapTol = 1.1f;
            var r = engine.Map.WorldResult!;
            var bdb = BdbParser.Parse(System.IO.Path.Combine(DataRoot, "BlockDB2.BDB"));
            bool Rendered(int tx, int ty)
            {
                uint b = r.BlockIdx[ty * r.Width + tx];
                if (b >= bdb.Header.NBlocks) return false;
                if (b == 59) return r.DugFloor != null && r.DugFloor[ty * r.Width + tx];
                return true;
            }
            // Per-block edge z-extents: side 0=W(x≈0) 1=E(x≈1) 2=N(y≈0) 3=S(y≈1); (min,max) or empty.
            var cache = new Dictionary<uint, (float min, float max, bool has)[]>();
            var palBuf = new ushort[8192]; var normBuf = new byte[8192];
            (float min, float max, bool has)[] Edges(uint b)
            {
                if (cache.TryGetValue(b, out var got)) return got;
                var e = new (float min, float max, bool has)[4];
                for (int s = 0; s < 4; s++) e[s] = (float.MaxValue, float.MinValue, false);
                for (int sub = 0; sub < 3; sub++)
                {
                    var cell = bdb.Blocks[(int)b].Cells[sub];
                    if (cell.VertCount == 0 || cell.VertCount > palBuf.Length) continue;
                    bdb.CopyCellPaletteIndices((int)b, sub, palBuf, normBuf);
                    for (int vi = 0; vi < cell.VertCount; vi++)
                    {
                        int pi = palBuf[vi];
                        if (pi >= bdb.Palette.Length) continue;
                        var p = bdb.Palette[pi].Position;   // (x,y in [0,1], z world height)
                        void Hit(int s) { if (p.z < e[s].min) e[s].min = p.z; if (p.z > e[s].max) e[s].max = p.z; e[s].has = true; }
                        if (p.x <= EPS) Hit(0);
                        if (p.x >= 1f - EPS) Hit(1);
                        if (p.y <= EPS) Hit(2);
                        if (p.y >= 1f - EPS) Hit(3);
                    }
                }
                cache[b] = e;
                return e;
            }
            // A gap exists when the two edge intervals are disjoint by more than tol.
            bool Gap((float min, float max, bool has) a, (float min, float max, bool has) b)
                => a.has && b.has && (a.max < b.min - GapTol || b.max < a.min - GapTol);

            var bad = new List<string>();
            int rx0 = System.Math.Max(1, r.RenderXMin), ry0 = System.Math.Max(1, r.RenderYMin);
            int rx1 = System.Math.Min(r.Width - 2, r.RenderXMax), ry1 = System.Math.Min(r.Height - 2, r.RenderYMax);
            for (int ty = ry0; ty <= ry1 && bad.Count < 200; ty++)
                for (int tx = rx0; tx <= rx1 && bad.Count < 200; tx++)
                {
                    if (!Rendered(tx, ty)) continue;
                    var me = Edges(r.BlockIdx[ty * r.Width + tx]);
                    if (Rendered(tx + 1, ty))
                    {
                        var nb = Edges(r.BlockIdx[ty * r.Width + tx + 1]);
                        if (Gap(me[1], nb[0]))
                            bad.Add($"E ({tx},{ty})blk{r.BlockIdx[ty * r.Width + tx]} z[{me[1].min:F1},{me[1].max:F1}] | ({tx + 1},{ty})blk{r.BlockIdx[ty * r.Width + tx + 1]} z[{nb[0].min:F1},{nb[0].max:F1}]");
                    }
                    if (Rendered(tx, ty + 1))
                    {
                        var nb = Edges(r.BlockIdx[(ty + 1) * r.Width + tx]);
                        if (Gap(me[3], nb[2]))
                            bad.Add($"S ({tx},{ty})blk{r.BlockIdx[ty * r.Width + tx]} z[{me[3].min:F1},{me[3].max:F1}] | ({tx},{ty + 1})blk{r.BlockIdx[(ty + 1) * r.Width + tx]} z[{nb[2].min:F1},{nb[2].max:F1}]");
                    }
                }
            return bad;
        }
    }
}
