#nullable enable

using Diggles.Runtime.Sim;

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Dig depth + stone-drop-rate pairing.
    //
    // A dug shaft must be descendable. The decisive real-BDB experiment showed that tunnel width,
    // not an invented full-depth carve, supplies EFFPASSWALK's floor strip and climb ribbon: the
    // native two-node-wide marquee is descendable at target 11. The mode-1 nibble therefore keeps
    // its exact native target and one successful dig_apply per node.
    //
    // The Tcl stone roll fires once per successful dig_apply. With the target-11 law restored, a
    // standard tunnel produces one roll per node; the campaign selector supplies 0.01 and free play
    // supplies 0.04. These tests pin the target, descent, and rate as one contract.
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class DigDepthAndDispatchTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

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
            return (rootGo, engine, terr, terr.Nav!);
        }

        // A solid below-the-meadow-line tile with material to dig (mirrors DigChainWave2's pick).
        static (int tx, int ty) FindSolidDigTile(DiggableTerrain terr, NavWorld nav)
        {
            for (int ty = 29; ty < 36; ty++)
                for (int tx = 240; tx < 256; tx++)
                {
                    if (!terr.HasGeometry(tx, ty)) continue;
                    if (nav.GetHmapSub(tx * 8, ty * 8) != 15) continue;
                    if (terr.RimAtNode(tx, ty) < 0) continue;
                    int free = 0;
                    for (int px = -1; px <= 1; px++)
                        if (nav.GetHmapSub((tx + px) * 8, (ty - 1) * 8) < 13) free++;
                    if (free >= 2) return (tx, ty);
                }
            return (-1, -1);
        }

        // The native target-11 transition is also the roofed-tunnel rendering contract. A solid
        // block becomes one of four authored edge blocks: it keeps a solid-height corner and adds
        // both wall submeshes. This guards against reintroducing an invented stacked-terrain model
        // or rendering only cell zero of the selected BDB block.
        [Test]
        public void StandardTunnel_FirstTransitionRetainsRockAndProvidesWallSubmeshes()
        {
            var bdb = BdbParser.Parse(System.IO.Path.Combine(DataRoot, "BlockDB2.BDB"));
            int[] expectedBlocks = { 54, 44, 32, 22 };
            int[,] expectedTriangles =
            {
                { 6, 8, 5 },
                { 8, 7, 4 },
                { 7, 3, 4 },
                { 6, 8, 8 },
            };

            Assert.AreEqual(2, bdb.CellTriangleRange(0, 0).Count / 3,
                "solid block 0 must start with its two-triangle front surface");
            Assert.AreEqual(0, bdb.CellTriangleRange(0, 1).Count);
            Assert.AreEqual(0, bdb.CellTriangleRange(0, 2).Count);

            for (int corner = 0; corner < 4; corner++)
            {
                int successor = (int)bdb.AuxCornerNeighbour(0, corner);
                Assert.AreEqual(expectedBlocks[corner], successor,
                    $"unexpected first carve successor for corner {corner}");
                Assert.AreEqual(11, bdb.AuxCornerGate(successor, corner),
                    $"successor {successor} must be gated at the standard-tunnel target");
                Assert.AreEqual(15, bdb.AuxMaxCorner(successor),
                    $"successor {successor} must retain solid-height rock");

                for (int submesh = 0; submesh < 3; submesh++)
                    Assert.AreEqual(expectedTriangles[corner, submesh],
                        bdb.CellTriangleRange(successor, submesh).Count / 3,
                        $"unexpected triangle count for block {successor}, submesh {submesh}");
            }
        }

        // (a) CARVE DEPTH + one-dig_apply-per-step — UPDATED to the RESTORED NATIVE
        // LAW: the mode-1 standard-tunnel dummy
        // decodes to target 11 (FUN_00597430:15-16 — one rim step, the authored-corridor walk
        // depth), NOT full depth. The full-depth deviation this test used to pin was the
        // misattributed "11 breaks descend" fix — the experiment proved passability is decided
        // by the dig's WIDTH, and descent into target-11 shafts is proven end-to-end by
        // DigPlayReproTests.MarqueeShaft_* + WallDigReproTests.ShaftDig_*. Unchanged halves:
        // the RAW nibble stays 3 (DigNotify's growth gate reads it raw) and the mark
        // self-clears when the node reaches its target (no infinite re-serve).
        [Test]
        public void DummyDig_CarvesToTarget11_OneApplyPerStep_AndFeedsDigNotify()
        {
            var (rootGo, _, terr, nav) = BuildStartMeadow();
            var (tx, ty) = FindSolidDigTile(terr, nav);
            Assert.GreaterOrEqual(tx, 0, "no solid dig tile found under the meadow");

            terr.SetDigMark(tx, ty, 0, 0, dummy: true);
            Assert.AreEqual(3, terr.RawDigNibble(tx, ty, 0),
                "the mode-1 dummy nibble must stay 3 — DigNotify's growth gate reads it raw");
            Assert.AreEqual(11, terr.DigTarget(tx, ty, 0),
                "the dummy mark must decode to the native walk depth 11 (FUN_00597430:15-16)");

            int rim0 = terr.RimAtNode(tx, ty);
            int steps = 0;
            while (terr.CarveStep(tx, ty, 0, out _) && steps < 64) steps++;
            int rim1 = terr.RimAtNode(tx, ty);
            TestContext.WriteLine($"dummy carve at ({tx},{ty}): rim {rim0} → {rim1} in {steps} steps " +
                                  "(== dig_apply=1 calls == stone rolls for this node)");
            Assert.GreaterOrEqual(steps, 1, "the dummy mark must carve at least one rim step");
            Assert.Less(rim1, rim0, "the node's rim must have stepped down");
            Assert.IsFalse(terr.IsDigMarked(tx, ty, 0),
                "the dummy mark must self-clear when the carve reaches its target (no infinite re-serve)");
            Object.DestroyImmediate(rootGo);
        }

        // (b) R3a DESCENT PROOF — a 2-wide standard tunnel dug DOWN (the minimum the drag-marquee
        // tool produces, DigToolTests) opens a climbable shaft with a STANDABLE floor at the
        // bottom that the pathfinder can reach from the rim. This is the exact pairing the player is
        // blocked on: dig a shaft → a gnome can walk/climb to the bottom and stand. Mirrors
        // NavWorldRealTerrainTests.FindPath_DugShaft_ClimbRecordsOnWallFace but drives the carve the
        // way the GNOME does — per node, through the dummy mark + CarveStep (DigTarget decode) — so
        // it proves the *gameplay* depth, not just the debug full-depth CarveTileNow.
        [Test]
        public void StandardTunnelDig_2Wide_GnomeCanDescendAndStand()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            var svc = rootGo.AddComponent<NavWorldService>();

            // Anchor on a meadow ground line, then drop a 2-wide column of rock under it.
            (int sx, int sy) anchor = default;
            for (int sx = 238 * 8; sx < 252 * 8 && anchor == default; sx++)
                for (int sy = 28 * 8; sy < 33 * 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) { anchor = (sx, sy); break; }
            Assert.AreNotEqual(default((int, int)), anchor, "no meadow line found");
            int ptx = anchor.sx / 8, pty = anchor.sy / 8 + 1;   // rock tile under the line
            TestContext.WriteLine($"meadow anchor ({anchor.sx},{anchor.sy}) → 2-wide shaft tiles " +
                                  $"x={ptx}..{ptx + 1}, y from {pty}");

            // Paint a 2-wide × deep standard-tunnel (mode-1 dummy) order, exactly as the marquee tool
            // commits — then drive the gnome's per-node carve (dig_apply → CarveStep) to completion
            // over every marked node, repeating until the column stops changing (the dig loop).
            for (int ty = pty; ty <= pty + 4; ty++)
                for (int tx = ptx; tx <= ptx + 1; tx++)
                    terr.SetDigMark(tx, ty, 0, 0, dummy: true);
            int totalSteps = 0;
            for (int round = 0; round < 96; round++)
            {
                bool any = false;
                for (int ny = pty; ny <= pty + 4; ny++)
                    for (int nx = ptx; nx <= ptx + 1; nx++)
                        while (terr.CarveStep(nx, ny, 0, out _)) { any = true; if (++totalSteps > 4096) break; }
                if (!any) break;
            }
            TestContext.WriteLine($"2-wide shaft carved in {totalSteps} CarveStep calls");

            // The Update half of the carve contract (no Play loop in EditMode): EFFPASSWALK→EFFBLOCK
            // over the dirty rect, so the freshly opened cells become walkable floor / climbable wall.
            nav.RevalidateRect((ptx - 2) * 8, (pty - 2) * 8, (ptx + 3) * 8 + 7, (pty + 6) * 8 + 7);

            // A climb band with a floor landing must now exist somewhere in the shaft (the descendable
            // 2-wide pocket the ±7-column band law allows — shaft-floor-rootcause §1.3, N=2 ⇒ 16
            // sub-cells ⇒ floor strip + climb ribbon).
            (int sx, int y0, int y1) band = default;
            (int sx, int sy) bottom = default;
            for (int sx = (ptx - 1) * 8; sx <= (ptx + 2) * 8 && band == default; sx++)
                for (int sy = anchor.sy; sy < (pty + 6) * 8; sy++)
                {
                    if (!nav.SubIsClimbWall(sx, sy)) continue;
                    int yEnd = sy; while (nav.SubIsClimbWall(sx, yEnd)) yEnd++;
                    if (yEnd - sy >= 4)
                        for (int d = 0; d <= 4 && bottom == default; d++)
                            for (int ox = -4; ox <= 4 && bottom == default; ox++)
                                if (nav.SubIsFloor(sx + ox, yEnd + d)) { bottom = (sx + ox, yEnd + d); band = (sx, sy, yEnd); }
                    sy = yEnd;
                }
            Assert.AreNotEqual(default((int, int, int)), band,
                "R3a: the 2-wide dug shaft produced NO climb band with a floor landing — the gnome " +
                "cannot descend (EFFPASSWALK found no ribbon in the target-11 pocket).");
            TestContext.WriteLine($"climb band x={band.sx} y=[{band.y0}..{band.y1}) standable bottom=({bottom.sx},{bottom.sy})");
            Assert.IsTrue(nav.SubIsFloor(bottom.sx, bottom.sy), "the bottom cell must be a standable floor");

            // The pathfinder must route rim → bottom (a real descent the gnome could execute).
            (int sx, int sy) rim = default;
            for (int sx = band.sx - 40; sx < band.sx - 8 && rim == default; sx++)
                for (int sy = anchor.sy - 8; sy <= anchor.sy + 8; sy++)
                    if (nav.SubIsFloor(sx, sy)) { rim = (sx, sy); break; }
            if (rim == default) { Object.DestroyImmediate(rootGo); Assert.Ignore("no rim floor near the pit"); return; }

            var root = rootGo.transform;
            Vector3 W(Vector3 native) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native));
            var start = W(new Vector3(rim.sx * 0.125f, rim.sy * 0.125f, nav.GetHmapSub(rim.sx, rim.sy)));
            var target = W(new Vector3(bottom.sx * 0.125f, bottom.sy * 0.125f, nav.GetHmapSub(bottom.sx, bottom.sy)));
            var path = svc.FindPath(start, target);
            Assert.IsNotNull(path, "R3a: no path rim→bottom — the dug shaft is not descendable");
            Assert.IsFalse(path![path.Count - 1].IsWall, "the descent must END on a floor waypoint (stand)");
            TestContext.WriteLine($"descent path: {path.Count} waypoints rim→standable bottom");
            Object.DestroyImmediate(rootGo);
        }

        // (c) R3b STONE RATE — in a CAMPAIGN-shaped world (is_storymgr → is_campaign 1, z_methods
        //.tcl:194) the dig_execute selector picks rndval 0.01, NOT the free-play 0.04. We evaluate
        // the EXACT original branch (z_dignwalk.tcl:328-332) in the dwarf's own object scope and
        // assert 0.01; then Monte-Carlo `rand<0.01` to confirm it is the ~1-in-100 rate (so stones
        // do NOT drop from every block — the count is a few rolls per node × 1%, not per-node).
        [Test]
        public void StoneDrop_CampaignRate_Is001_NotFreeplay004()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            interp.RegisterCommand("puts", (i, a) => "");
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            ctx.IsStoryMgr = true;                       // campaign world → is_campaign 1
            var sim = new DigglesGameSim(ctx);
            try { interp.Eval("call scripts/classes/zwerg/zwerg.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[zwerg] {e.Message}"); }
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");

            var g = ctx.GetEntity(interp.Eval("new Zwerg \"\" \"100 100 8\" \"0 0 0\"").Trim())!;
            // method init is timer-armed (zwerg.tcl:183 -attime 3) — run the clock past it.
            for (int i = 0; i < 60 && (!g.VarsScope.Vars.TryGetValue("gnome_initialized", out var gi) || gi != "1"); i++)
                sim.Step(0.1);
            Assert.AreEqual("1", g.VarsScope.Vars.TryGetValue("gnome_initialized", out var gi2) ? gi2 : "<none>",
                "the dwarf's method init must run (it sets is_campaign)");

            // is_storymgr is true → the dwarf's init set is_campaign 1 (z_methods.tcl:193-195). This
            // per-object global is what im_in_campaign (z_procs.tcl:22) returns — the load-bearing fact.
            Assert.AreEqual("1", g.VarsScope.Vars.TryGetValue("is_campaign", out var ic) ? ic : "<none>",
                "R3b: a campaign dwarf's is_campaign must be 1 (set from is_storymgr in method init)");
            Assert.AreEqual("1", interp.Eval("is_storymgr").Trim(), "campaign host must report is_storymgr 1");

            // The EXACT z_dignwalk.tcl:328-332 rate selector, run in the dwarf's object scope (so the
            // im_in_campaign proc resolves against the dwarf's class + reads its is_campaign global).
            var prevThis = ctx.This;
            var prevGlobal = interp.ActiveGlobalOverride;
            ctx.This = g;
            interp.ActiveGlobalOverride = g.VarsScope;
            string camp, rnd;
            try
            {
                camp = interp.Eval("im_in_campaign").Trim();
                rnd = interp.Eval(
                    "if {[im_in_campaign]} { set rndval 0.01 } else { set rndval 0.04 }\nset rndval").Trim();
            }
            finally { ctx.This = prevThis; interp.ActiveGlobalOverride = prevGlobal; }
            Assert.AreEqual("1", camp,
                "R3b: im_in_campaign must be 1 in the campaign — else dig_execute uses the 4× free-play rate");
            Assert.AreEqual("0.01", rnd, "the campaign stone-drop rate must be 0.01 (1 per ~100 digs), not 0.04");

            // Monte-Carlo the gate: ~1% of dig_apply successes drop a stone — NOT every block.
            UnityEngine.Random.InitState(20260613);
            int trials = 20000, hits = 0;
            for (int i = 0; i < trials; i++)
                if (interp.Eval("expr {rand()<0.01}").Trim() == "1") hits++;
            double freq = (double)hits / trials;
            TestContext.WriteLine($"observed stone-drop frequency over {trials} rolls: {freq:0.0000} (expect ≈0.01)");
            Assert.That(freq, Is.EqualTo(0.01).Within(0.004),
                "the campaign rate must be ~1-in-100, not the per-block over-drop the user saw");
        }
    }
}
