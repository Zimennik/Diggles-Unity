#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // DIG-chain tests on the REAL start-meadow terrain — the gap the ShiftDig Play repro
    // exposed: every prior dig test ran on FABRICATED terrain (WorkLoopTests) or drove the carve
    // directly (NavWorldRealTerrainTests); none exercised the mark→get_digedge→dig_next→dig_apply
    // contract against the real BDB heights and the per-owner mark slots at once.
    //
    // Root-cause locks:
    // • tile+0x08 = 8 per-player nibble slots (FUN_00596b80:44-46); the human player = owner 0.
    // Marking with the wrong "owner" (the Play test passed PlayerDigMarkId = the per-order vein
    // id, FUN_00567cb0:229-236) leaves every owner-0 lookup blind → dig_starttask returns false
    // → work_idle wander. Locked by Mark_OwnerLayers_AreNotInterchangeable.
    // • get_hmap = the faithful FUN_004093f0 per-sub-cell BDB read; it knows NOTHING about marks
    // (the "marked reads 15 / unmarked 0" contract was the reverted 527905a shim). Locked by
    // GetHmap_IsMarkIndependent_AndCarveLowersTheNodeCell.
    // • dig_apply node = trunc(coord + 0.5) (FUN_006347a0:95-98 under the chop-FPU reading; the
    // nearest-even reading would collapse tourn_2100's 205/206 two-column shaft onto one node
    // line). Locked by DigApply_NodeMapping_TruncPlusHalf.
    // • CarveStep stall edge (shaft-floor-rootcause.md §4): a node whose rim parks at the mark's
    // target with no gate==rim transition must SELF-CLEAR (stand-in for the unmodeled
    // FUN_005976b0 exposure clear), or dig_next re-serves it forever. Locked by
    // CarveStep_Loop_AlwaysEndsWithMarkCleared.
    [Explicit("Loads real data + BDB (DIGGLES_DATA_ROOT); run on demand")]
    public sealed class DigChainTests
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
            engine.Ctx.Terrain = terr;                          // DigglesWorldRuntime normally wires this
            engine.Ctx.NavService = rootGo.AddComponent<NavWorldService>();
            return (rootGo, engine, terr, terr.Nav!);
        }

        // A solid below-the-meadow-line tile with an open approach row above it (the same pick the
        // PlayMode ShiftDig test makes): node sub-cell reads 15, rim valid, ≥2 free probes above.
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

        // Owner slots are per-player, not interchangeable with the vein id: a mark painted for owner
        // 1 must be invisible to owner-0 dig_next/CarveStep lookups. get_digedge is deliberately not
        // used as the negative probe: FUN_005981d0:92-98 ORs bit 2 into the requested owner's SEED.
        [Test]
        public void Mark_OwnerLayers_AreNotInterchangeable()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            var interp = engine.Interp;
            var (tx, ty) = FindSolidDigTile(terr, nav);
            Assert.GreaterOrEqual(tx, 0, "no solid dig tile found under the meadow");
            TestContext.WriteLine($"dig tile = ({tx},{ty}), rim={terr.RimAtNode(tx, ty)}");

            // Wrong layer (1 = what PlayerDigMarkId evaluated to in the broken Play test).
            terr.SetDigMark(tx, ty, 1, 0);
            Assert.IsFalse(terr.IsDigMarked(tx, ty, 0), "an owner-1 mark must not read on layer 0");
            Assert.AreEqual("-1 -1 -1", interp.Eval($"dig_next {{{tx} {ty} 8}} 0").Trim(),
                "owner-0 dig_next must NOT see an owner-1 mark");
            terr.ClearDigMark(tx, ty, 1);

            // Right layer (0 = the human player = the default entity Owner).
            terr.SetDigMark(tx, ty, 0, 0);
            Assert.IsTrue(terr.IsDigMarked(tx, ty, 0));
            string edge = interp.Eval($"get_digedge {{{tx} {ty}}} 0").Trim();
            TestContext.WriteLine($"get_digedge → '{edge}'");
            Assert.AreNotEqual("-1 -1 14", edge, "owner-0 get_digedge must find the owner-0 mark");
            string next = interp.Eval($"dig_next {{{tx} {ty} 8}} 0").Trim();
            TestContext.WriteLine($"dig_next → '{next}'");
            Assert.AreNotEqual("-1 -1 -1", next, "owner-0 dig_next must find the owner-0 mark");
            Object.DestroyImmediate(rootGo);
        }

        // get_hmap is the faithful FUN_004093f0 read: identical before/after marking (no 527905a-style
        // mark masking), 15 on solid rock, and it DROPS at the node's sub-cell once dig_apply carves
        // the node — exactly what the PlayMode ShiftDig test measures.
        [Test]
        public void GetHmap_IsMarkIndependent_AndCarveLowersTheNodeCell()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            var interp = engine.Interp;
            var (tx, ty) = FindSolidDigTile(terr, nav);
            Assert.GreaterOrEqual(tx, 0, "no solid dig tile found under the meadow");

            int hBefore = int.Parse(interp.Eval($"get_hmap {tx} {ty}").Trim());
            terr.SetDigMark(tx, ty, 0, 0);
            int hMarked = int.Parse(interp.Eval($"get_hmap {tx} {ty}").Trim());
            Assert.AreEqual(hBefore, hMarked, "marking must NOT change get_hmap (faithful FUN_004093f0)");
            Assert.AreEqual(15, hMarked, "the picked rock tile reads solid");

            // One faithful carve step at the node (what one dig_execute swing does).
            string ok = interp.Eval($"dig_apply {{{tx} {ty} 8}} 0").Trim();
            Assert.AreEqual("1", ok, "dig_apply must carve the marked node");
            int hAfter = int.Parse(interp.Eval($"get_hmap {tx} {ty}").Trim());
            TestContext.WriteLine($"get_hmap {hMarked} → {hAfter} after one carve step");
            Assert.Less(hAfter, hMarked, "one carve step lowers the node's sub-cell height (15→≤11)");
            Object.DestroyImmediate(rootGo);
        }

        // FUN_006347a0:95-98 node mapping = trunc(coord+0.5): integral digpoints carve their OWN node
        // (parity with the marked tile), x.5 rounds half-UP to the next node — never banker's-even.
        [Test]
        public void DigApply_NodeMapping_TruncPlusHalf()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            var interp = engine.Interp;
            var (tx, ty) = FindSolidDigTile(terr, nav);
            Assert.GreaterOrEqual(tx, 0, "no solid dig tile found under the meadow");

            // Integral input → the same node (tx,ty): the rim at THAT node must drop.
            terr.SetDigMark(tx, ty, 0, 0);
            int rim0 = terr.RimAtNode(tx, ty);
            Assert.AreEqual("1", interp.Eval($"dig_apply {{{tx} {ty} 8}} 0").Trim());
            int rim1 = terr.RimAtNode(tx, ty);
            TestContext.WriteLine($"integral dig_apply at ({tx},{ty}): rim {rim0} → {rim1}");
            Assert.Less(rim1, rim0, "an integral digpoint must carve its own node (chop-FPU parity)");

            // Fractional x−0.4 (→ trunc(x−0.4+0.5) = x): still the same node. Use a second,
            // untouched solid tile for the fractional probe.
            int bx = -1, by = ty;
            for (int sx = tx + 2; sx < tx + 14; sx++)
                if (nav.GetHmapSub(sx * 8, by * 8) == 15 && terr.RimAtNode(sx, by) >= 0 && !terr.IsDigMarked(sx, by, 0))
                { bx = sx; break; }
            if (bx < 0) { Object.DestroyImmediate(rootGo); Assert.Inconclusive("no second untouched solid tile in the row"); return; }
            terr.SetDigMark(bx, by, 0, 0);
            int rimB0 = terr.RimAtNode(bx, by);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Assert.AreEqual("1", interp.Eval($"dig_apply {{{(bx - 0.4f).ToString(inv)} {by} 8}} 0").Trim(),
                "x−0.4 maps to node x (trunc(x+0.1) = x)");
            int rimB1 = terr.RimAtNode(bx, by);
            TestContext.WriteLine($"fractional dig_apply at ({bx - 0.4f},{by}): rim {rimB0} → {rimB1}");
            Assert.Less(rimB1, rimB0, "x−0.4 must carve node x");
            Object.DestroyImmediate(rootGo);
        }

        // Regression C (Play strike #3,, corrected by the real-BDB
        // experiment): the standard mode-1 tunnel nibble 3 decodes to native target 11. It lowers
        // the shared-node rim once; the two-node marquee width, not extra depth, creates the
        // standable floor and climb ribbon. The raw nibble remains 3 so DigNotify's growth gate
        // ((n&8)==0 && n!=0, FUN_005fd060:75-77) still observes a standard tunnel order.
        [Test]
        public void StandardDummyDig_CarvesToNativeTarget11_AndFeedsDigNotify()
        {
            var (rootGo, _, terr, nav) = BuildStartMeadow();
            var (tx, ty) = FindSolidDigTile(terr, nav);
            Assert.GreaterOrEqual(tx, 0, "no solid dig tile found under the meadow");

            terr.SetDigMark(tx, ty, 0, 0, dummy: true);
            Assert.AreEqual(3, terr.RawDigNibble(tx, ty, 0),
                "the mode-1 dummy nibble must stay 3 — DigNotify's growth gate reads it raw");
            Assert.AreEqual(11, terr.DigTarget(tx, ty, 0),
                "the standard tunnel must retain the native target-11 profile");

            int rim0 = terr.RimAtNode(tx, ty);
            int steps = 0;
            while (terr.CarveStep(tx, ty, 0, out _) && steps < 64) steps++;
            int rim1 = terr.RimAtNode(tx, ty);
            TestContext.WriteLine($"dummy carve at ({tx},{ty}): rim {rim0} → {rim1} in {steps} steps");
            Assert.GreaterOrEqual(steps, 1,
                "a standard tunnel must lower the shared-node rim");
            Assert.Less(rim1, rim0, "the target-11 profile must lower the node");
            Assert.IsFalse(terr.IsDigMarked(tx, ty, 0),
                "the dummy mark must self-clear when the carve bottoms out");
            Object.DestroyImmediate(rootGo);
        }

        // The CarveStep drive loop must ALWAYS terminate with the mark cleared — full digs end via
        // the completion sweep / rim collapse, and the STALL edge (rim parked at the target with no
        // gate==rim transition) now self-clears instead of leaving a
        // mark dig_next re-serves forever (shaft-floor-rootcause.md §4).
        [Test]
        public void CarveStep_Loop_AlwaysEndsWithMarkCleared()
        {
            var (rootGo, _, terr, nav) = BuildStartMeadow();
            int tested = 0, stalls = 0;
            for (int ty = 30; ty < 35; ty++)
                for (int tx = 240; tx < 254; tx++)
                {
                    if (!terr.HasGeometry(tx, ty) || terr.RimAtNode(tx, ty) < 0) continue;
                    // Alternate target-11 standard tunnels and mode-0 full-depth orders.
                    bool dummy = ((tx + ty) & 1) == 0;
                    terr.SetDigMark(tx, ty, 0, 0, dummy: dummy);
                    if (!terr.IsDigMarked(tx, ty, 0)) continue;
                    tested++;
                    int steps = 0;
                    while (terr.CarveStep(tx, ty, 0, out _) && steps < 64) steps++;
                    if (steps == 0) stalls++;
                    Assert.IsFalse(terr.IsDigMarked(tx, ty, 0),
                        $"node ({tx},{ty}) (dummy={dummy}, steps={steps}, rim={terr.RimAtNode(tx, ty)}): " +
                        "the CarveStep loop ended but the mark survived — dig_next would grind here forever");
                }
            TestContext.WriteLine($"nodes tested: {tested} (immediate-refuse/stall-cleared: {stalls})");
            Assert.Greater(tested, 8, "too few markable nodes — widen the scan window");
            Object.DestroyImmediate(rootGo);
        }

        // END-TO-END on real terrain: real Zwerg classes + a real owner-0 mark + the faithful
        // evt_task_dig → work_dispatch → dig_starttask chain, stepped headlessly. Asserts the chain
        // ENGAGES (dig tasks queued / work state entered) and — the real-terrain first — that the
        // marked node actually carves (rim drops) with the mark self-clearing. Mirrors
        // WorkLoopTests.Dig_DriveLoop but on the REAL BDB heights the Play repro runs on.
        [Test]
        public void Dig_DriveLoop_RealTerrain()
        {
            var (rootGo, engine, terr, nav) = BuildStartMeadow();
            var interp = engine.Interp; var ctx = engine.Ctx;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "Zwerg class not loaded");
            var sim = engine.Sim;

            var (tx, ty) = FindSolidDigTile(terr, nav);
            Assert.GreaterOrEqual(tx, 0, "no solid dig tile found under the meadow");
            // Stand the gnome right above the dig tile (native z = body depth ≈ 8).
            string h = interp.Eval($"new Zwerg \"\" \"{tx} {ty - 1} 8\" \"0 0 0\"").Trim();
            var g = ctx.GetEntity(h);
            Assert.IsNotNull(g, "no gnome entity");
            Assert.AreEqual(0, g!.Owner, "a fresh player gnome must be owner 0 (the human player)");
            // Drop the model: EditMode can't run real movement (DigglesObject.Update is Play-only),
            // so force the proven headless degrade (walks → brief WaitActions, WorkLoopTests mode).
            // The SUBJECT here is the real-terrain DATA path (marks/heights/rims), not motion —
            // motion is the PlayMode ShiftDig test's job.
            if (g.GameObject != null) { Object.DestroyImmediate(g.GameObject); g.GameObject = null; }
            interp.Eval($"set_worktime {g.Handle} 0 12");
            for (int i = 0; i < 10; i++) sim.Step(0.1);

            terr.SetDigMark(tx, ty, 0, 0);
            terr.IncPlayerDigMarkId();
            int rim0 = terr.RimAtNode(tx, ty);
            ctx.This = g;
            interp.Eval($"set_event this evt_task_dig -target {g.Handle} -pos1 {{{tx} {ty}}}");
            ctx.This = null;

            bool sawWork = false, sawDigTask = false;
            for (int i = 0; i < 120; i++)
            {
                sim.Step(0.1);
                if (g.CurrentState == "work_dispatch" || g.CurrentState == "task") sawWork = true;
                foreach (var task in g.TaskList) if (task.Contains("dig")) { sawDigTask = true; break; }
                if (i % 10 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} wr={g.WalkResult} " +
                        $"rim={terr.RimAtNode(tx, ty)} marked={terr.IsDigMarked(tx, ty, 0)} " +
                        $"tasks={g.TaskList.Count} action={(g.ActiveAction != null)}");
            }
            int rim1 = terr.RimAtNode(tx, ty);
            TestContext.WriteLine($"RESULT: sawWork={sawWork} sawDigTask={sawDigTask} rim {rim0}→{rim1} " +
                $"marked={terr.IsDigMarked(tx, ty, 0)} finalState={g.CurrentState}");
            Assert.IsTrue(sawWork || sawDigTask,
                "gnome never entered the dig work flow after evt_task_dig on REAL terrain");
            Assert.Less(rim1, rim0, $"the dig never carved the marked node on real terrain (rim {rim0}→{rim1})");
            Object.DestroyImmediate(rootGo);
        }
    }

    public sealed class DigWorkPointSelectionTests
    {
        private const int Width = 16;
        private const int Height = 16;
        private const int TargetX = 8;
        private const int TargetY = 8;
        [Test]
        public void HorizontalMouthAtShaftBase_StrictTieKeepsEastFirstTile()
        {
            AssertSelectedDigEdge(
                new[] { new Vector2Int(TargetX, TargetY) },
                new Vector2Int(TargetX - 1, TargetY),
                new Vector2Int(TargetX + 1, TargetY),
                "0x5983fa-0x5983fd keeps the first equal-distance candidate; " +
                "0x598424-0x5985a4 enqueues east before west");
        }

        [Test]
        public void PlainHorizontalTunnel_RanksEastTileNearestGnome()
        {
            AssertSelectedDigEdge(
                new[] { new Vector2Int(TargetX, TargetY) },
                new Vector2Int(TargetX + 1, TargetY),
                new Vector2Int(TargetX + 1, TargetY),
                "get_digedge returns an integral tile, never a fractional nav work point");
        }

        [Test]
        public void PlainVerticalShaft_FloodsMarkedColumnToBottomFrontier()
        {
            AssertSelectedDigEdge(
                new[]
                {
                    new Vector2Int(TargetX, TargetY),
                    new Vector2Int(TargetX, TargetY + 1),
                    new Vector2Int(TargetX, TargetY + 2),
                },
                new Vector2Int(TargetX, TargetY + 3),
                new Vector2Int(TargetX, TargetY + 3),
                "only the connected marked column spreads the flood to the shaft-bottom frontier");
        }

        [Test]
        public void TclWrapper_ReferenceUsesRoundAfterHalfBias()
        {
            // FUN_00635180:153-158 packs ref as ROUND(obj coordinate + 0.5), not
            // ROUND(obj coordinate * 8). At 8.49 the native reference is tile 9.
            var root = new GameObject(nameof(TclWrapper_ReferenceUsesRoundAfterHalfBias));
            try
            {
                var terrain = BuildTerrain(root, new[] { new Vector2Int(TargetX, TargetY) });
                var interp = new TclInterpreter();
                var context = DigglesTclEngine.Register(interp);
                context.Terrain = terrain;
                context.This = new DigglesTclEntity
                {
                    Owner = 0,
                    Position = new Vector3(8.49f, 8.49f, 8f),
                };

                string actual = interp.Eval($"get_digedge {{{TargetX} {TargetY}}} this").Trim();

                Assert.AreEqual("9 8 14", actual,
                    "rounded reference (9,9) ties east/down; strict < keeps east-first BFS candidate");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void AssertSelectedDigEdge(
            Vector2Int[] markedRockTiles,
            Vector2Int gnomeTile,
            Vector2Int expectedTile,
            string message)
        {
            var root = new GameObject(nameof(DigWorkPointSelectionTests));
            try
            {
                var terrain = BuildTerrain(root, markedRockTiles);
                var interp = new TclInterpreter();
                var context = DigglesTclEngine.Register(interp);
                context.Terrain = terrain;
                context.This = new DigglesTclEntity
                {
                    Owner = 0,
                    Position = new Vector3(gnomeTile.x, gnomeTile.y, 8f),
                };

                string actual = interp.Eval($"get_digedge {{{TargetX} {TargetY}}} this").Trim();
                string expected = $"{expectedTile.x} {expectedTile.y} 14";

                Assert.AreEqual(expected, actual, message);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static DiggableTerrain BuildTerrain(GameObject root, Vector2Int[] markedRockTiles)
        {
            int tileCount = Width * Height;
            var blockIndices = new uint[tileCount];
            for (int index = 0; index < blockIndices.Length; index++)
                blockIndices[index] = 1;
            foreach (Vector2Int tile in markedRockTiles)
                blockIndices[tile.y * Width + tile.x] = 0;

            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = Width,
                Height = Height,
                BlockIdx = blockIndices,
                Terrain = new byte[tileCount],
                Voxels = new byte[tileCount * PmpMap.SubGridLength],
            };
            var terrain = root.AddComponent<DiggableTerrain>();
            terrain.Init(result, BuildBlockDatabase(), "", () => { });
            foreach (Vector2Int tile in markedRockTiles)
                terrain.SetDigMark(tile.x, tile.y, 0);
            return terrain;
        }

        private static BdbModel BuildBlockDatabase()
        {
            var auxiliaryData = new byte[2 * BdbModel.AuxStride];
            for (int index = 0; index < 64; index++)
                auxiliaryData[index] = 15;

            return new BdbModel
            {
                Header = new BdbHeader
                {
                    NBlocks = 2,
                },
                PerBlockAux = auxiliaryData,
            };
        }

    }
}
