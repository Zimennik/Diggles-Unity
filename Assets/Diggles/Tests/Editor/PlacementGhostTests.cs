#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The building-placement ghost laws (mode-2 RE; mode 8 is the wall-trap snapmode,
    // fallen.tcl:245; production buildings use snapmode 0 = FUN_0046b450 tick mode 2,
    // genericprod.tcl:450):
    //
    // • FUN_00530e80 loads two models into the placement singleton: +0xb8/+0xbc = the
    // unpacked building's display model from the target class's anim tables (:26-68,
    // stored:93/:109/:168/:174) and +0xc4/+0xc8 = the global "kiste"."standard" box
    // (FUN_00460460:310-337) — the ghost is the building, not the crate's current box.
    // • The mode-2 tick (FUN_0046b450:795-920): pick (x half-tile snap FUN_0046aec0:71,
    // y 1/8 sub-row, z = the whole slab where the mouse ray enters the heightfield —
    // CGameThread::Draw FUN_006b5d90:613-744) → the vertical settle (FUN_0046b190: out of
    // solid ≤16 rows up, fall to the first h>z support row ≤16 down) → the depth-budget
    // gate `pickZ + classBboxMaxZ·0.01 < 13.0` (:828-829) decides building vs box. z and x
    // are never overwritten; overlap failure does not fall back to the box (:915).
    // • +0xcd (asbox) is computed per frame from the branch (:1970); box-only is a valid
    // (green) spot; an invalid un-forced spot displays the building model (:1963-1967).
    // • The epilogue publishes {placing, green, asbox} → get_place_info (FUN_0064c1e0)
    // returns the triple the tutorial gates on ("1 0 0"/"1 1 0"/"1 1 1",
    // Trigger_Tutorial.tcl:1320-1322).
    // • Confirm's -asprod/-asbox txtpar follows the computed +0xcd (FUN_00531310:91-97/:229-265).
    public sealed class PlacementGhostTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        static BdbModel? s_bdb;
        static BdbModel Bdb() => s_bdb ??= BdbParser.Parse(DataRoot + "/BlockDB2.BDB");

        private const int DoorPlacementWidth = 48;
        private const int DoorPlacementHeight = 48;
        // The corrected mode-5 seat law (FUN_0046b450:1875-1882, floor-consumed — see
        // PlacementController.DoorSeat): cursor (19.5,20) → snap (20,20) → seat x =
        // floor(20+0.5) = 20 (INTEGER, = tueren.tcl:51 hf2i(x+0.5)), y = 2·floor((20−0.5)·0.5)
        // + 2.5 = 20.5 (…+0.5, = tueren.tcl:52 hf2i(y)+0.5), z = 15. The tunnel aperture is
        // built around that seat: centerX = 160, centerY = 164 sub-cells.
        private const int DoorCenterSubX = 160;
        private const int DoorCenterSubY = 164;

        private enum DoorTerrainShape
        {
            HorizontalTunnel,
            OpenCave,
            Meadow,
            VerticalShaft,
        }

        public enum LadderTerrainShape
        {
            ShortShaft,
            LongShaft,
            HorizontalCorridor,
            // Named false-red geometries: the invented ±8 "frame" probes rejected both
            // although the native four-probe law accepts them.
            WideShaft,
            ShaftBesideGallery,
        }

        static (TclInterpreter interp, DigglesTclContext ctx) Host()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var _ = new DigglesGameSim(ctx);
            interp.Eval("def_class G none gnome 0 {} {}");
            interp.Eval("def_class B none production 0 {} {}");
            return (interp, ctx);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
            => ctx.GetEntity(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim())!;

        DigglesTclEntity BoxedCrate(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
        {
            var crate = Spawn(interp, ctx, cls, pos);
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            return crate;
        }

        // An all-open fabricated terrain (block 59 = air ⇒ tile height 0) with optional solid
        // tiles (block 0 ⇒ height 15) — the WorkLoopTests harness pattern.
        DiggableTerrain MakeOpenTerrain(int w, int h, params (int x, int y)[] solid)
        {
            var go = new GameObject("DiggableTerrain");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            var block = new uint[w * h];
            for (int k = 0; k < block.Length; k++) block[k] = 59;
            foreach (var (x, y) in solid) if ((uint)x < (uint)w && (uint)y < (uint)h) block[y * w + x] = 0;
            t.Init(new BdbPmpWorldBuilder.TclWorldResult { Width = w, Height = h, BlockIdx = block, Terrain = new byte[w * h] }, Bdb(), "", () => { });
            return t;
        }

        private static NavWorld MakeDoorPlacementWorld(DoorTerrainShape shape)
        {
            int tileCount = DoorPlacementWidth * DoorPlacementHeight;
            var blocks = new uint[tileCount];
            var aux = new byte[tileCount * BdbModel.AuxStride];
            for (int tileY = 0; tileY < DoorPlacementHeight; tileY++)
            for (int tileX = 0; tileX < DoorPlacementWidth; tileX++)
            {
                int tile = tileY * DoorPlacementWidth + tileX;
                blocks[tile] = (uint)tile;
                for (int localY = 0; localY < 8; localY++)
                for (int localX = 0; localX < 8; localX++)
                {
                    int subX = tileX * 8 + localX;
                    int subY = tileY * 8 + localY;
                    byte height = shape switch
                    {
                        DoorTerrainShape.HorizontalTunnel =>
                            subX > DoorCenterSubX - 8 && subX < DoorCenterSubX + 8
                            && subY > DoorCenterSubY - 20 && subY < DoorCenterSubY
                                ? (byte)11
                                : (byte)15,
                        DoorTerrainShape.OpenCave => 11,
                        DoorTerrainShape.Meadow => 0,
                        DoorTerrainShape.VerticalShaft =>
                            Mathf.Abs(subX - DoorCenterSubX) <= 3 ? (byte)11 : (byte)15,
                        _ => 15,
                    };
                    aux[tile * BdbModel.AuxStride + localY * 8 + localX] = height;
                }
            }

            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = (uint)tileCount },
                PerBlockAux = aux,
            };
            return new NavWorld(
                DoorPlacementWidth,
                DoorPlacementHeight,
                blocks,
                new byte[tileCount * 64],
                bdb);
        }

        private static NavWorld MakeLadderPlacementWorld(LadderTerrainShape shape)
        {
            const int width = 48;
            const int height = 48;
            // 172 subs = x 21.5: on the mode-4 transport lattice (x_c = 1.5 + 4k,
            // FUN_0046b450's floor((x_c+0.5)·0.25)·4 + 1.5 gate) — the old 156 (19.5) column
            // sat on the rejected half of the lattice and would now scan nothing.
            const int centerX = 172;
            int bottomY = shape == LadderTerrainShape.LongShaft ? 324 : 148;
            float shaftSpan = shape switch
            {
                LadderTerrainShape.LongShaft => 30f,
                // A genuinely low tunnel: interior ≈ 2.5 world tall — its open column can never
                // reach slot 1 of the rung table, which is the native law that keeps a
                // corridor red (no invented ±8 "frame" probes).
                LadderTerrainShape.HorizontalCorridor => 2.5f,
                // ≥ the native minimum span 8.296 (leiter_b threshold − 1, FUN_0060b560).
                _ => 10f,
            };
            int shaftTop = bottomY - Mathf.RoundToInt(shaftSpan * 8f) + 4;
            int halfWidth = shape switch
            {
                LadderTerrainShape.HorizontalCorridor => 40,
                LadderTerrainShape.WideShaft => 8,   // 2-tile shaft: open at the old ±8 probes
                _ => 4,
            };
            int tileCount = width * height;
            var blocks = new uint[tileCount];
            var aux = new byte[tileCount * BdbModel.AuxStride];
            for (int tileY = 0; tileY < height; tileY++)
            for (int tileX = 0; tileX < width; tileX++)
            {
                int tile = tileY * width + tileX;
                blocks[tile] = (uint)tile;
                for (int localY = 0; localY < 8; localY++)
                for (int localX = 0; localX < 8; localX++)
                {
                    int subX = tileX * 8 + localX;
                    int subY = tileY * 8 + localY;
                    bool inside = Mathf.Abs(subX - centerX) <= halfWidth
                        && subY >= shaftTop
                        && subY < bottomY;
                    // A side gallery opening onto the shaft's +8 column (the exact cell the
                    // removed frame probe sampled) — natively irrelevant to mode 4.
                    if (shape == LadderTerrainShape.ShaftBesideGallery
                        && subX >= centerX + 8 && subX <= centerX + 48
                        && subY >= bottomY - 28 && subY < bottomY)
                        inside = true;
                    aux[tile * BdbModel.AuxStride + localY * 8 + localX] = inside
                        ? (byte)11
                        : (byte)15;
                }
            }

            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = (uint)tileCount },
                PerBlockAux = aux,
            };
            return new NavWorld(
                width,
                height,
                blocks,
                new byte[tileCount * 64],
                bdb);
        }

        // ------------------------------------------------------------------
        // 1. Ghost model — the class's unboxed standard model + the kiste box pair
        // (FUN_00530e80:26-68/:93/:109/:168/:174 + FUN_00460460:310-337)
        // ------------------------------------------------------------------

        [Test]
        public void CursorGhost_IsTheUnboxedBuildingModel_WithAKistePair()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/3db/produktionsstaetten.3db"),
                "original data dir not present — model-resolution test skipped");
            Assume.That(System.IO.File.Exists(
                System.IO.Path.Combine(Application.streamingAssetsPath, "class_model_map_v2.tsv")),
                "StreamingAssets model maps not present — model-resolution test skipped");

            var (interp, ctx) = Host();
            interp.Eval("def_class Feuerstelle none production 0 {} {}");
            var lib = new DigglesModelLibrary(DataRoot);
            ctx.ModelLibrary = lib;
            var rootGo = new GameObject("WorldRoot");
            _gos.Add(rootGo);
            ctx.SpawnParent = rootGo.transform;

            var crate = BoxedCrate(interp, ctx, "Feuerstelle", "20.5 20 8");
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.OrderBuild(crate.Handle));

            Assert.IsNotNull(pc.CursorGhost, "placement must raise a cursor ghost");
            Assert.IsNotNull(pc.GhostBuildingGo, "the ghost must carry the building model child");
            Assert.IsNotNull(pc.GhostBoxGo, "the ghost must carry the kiste box model child");

            // The building child shows the class's unboxed standard model — the same visual
            // unpackfrombox produces (TSV: Feuerstelle → feuerstelle.brennt), resolved through
            // the spawner's model path. Not a clone of the crate's current boxed GameObject.
            var buildingMesh = pc.GhostBuildingGo!.GetComponent<MeshFilter>()?.sharedMesh;
            var kisteMesh = pc.GhostBoxGo!.GetComponent<MeshFilter>()?.sharedMesh;
            Assert.IsNotNull(buildingMesh, "building ghost child must have a mesh");
            Assert.IsNotNull(kisteMesh, "kiste ghost child must have a mesh");
            var expected = lib.GetClip("feuerstelle", "brennt");
            Assert.IsNotNull(expected, "the library must resolve feuerstelle.brennt");
            Assert.AreSame(expected!.Frames[0], buildingMesh,
                "the ghost building mesh must be the class standard clip's frame-0 mesh");
            var kisteClip = lib.GetClip("kiste", "standard");
            Assert.IsNotNull(kisteClip, "the library must resolve the global kiste.standard box");
            Assert.AreSame(kisteClip!.Frames[0], kisteMesh,
                "the box child must be the global kiste model (FUN_00460460:310-337)");
            Assert.AreNotSame(kisteMesh, buildingMesh,
                "the ghost must NOT be the crate's boxed kiste model (the 'ghost is a crate' Play bug)");

            // A building spot (asbox = 0) shows the building child, not the box. z = 4 sits well
            // inside the depth budget for any sane model extent (4 + ext < 13 ⇔ ext < 9 slabs).
            pc.UpdateCursor(new Vector3(28f, 20f, 4f));
            Assert.IsTrue(pc.Valid);
            Assert.IsFalse(pc.AsBox);
            Assert.IsTrue(pc.GhostBuildingGo!.activeSelf, "building spot → the building model is up");
            Assert.IsFalse(pc.GhostBoxGo!.activeSelf, "building spot → the kiste model is down");

            pc.Cancel();
            Assert.IsNull(pc.CursorGhost, "cancel drops the cursor ghost");
        }

        [Test]
        public void CursorGhost_FrontBandSpot_SwapsToTheKisteModel()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/3db/produktionsstaetten.3db"),
                "original data dir not present — model-swap test skipped");
            Assume.That(System.IO.File.Exists(
                System.IO.Path.Combine(Application.streamingAssetsPath, "class_model_map_v2.tsv")),
                "StreamingAssets model maps not present — model-swap test skipped");

            var (interp, ctx) = Host();
            interp.Eval("def_class Feuerstelle none production 0 {} {}");
            ctx.ModelLibrary = new DigglesModelLibrary(DataRoot);
            var rootGo = new GameObject("WorldRoot");
            _gos.Add(rootGo);
            ctx.SpawnParent = rootGo.transform;

            var crate = BoxedCrate(interp, ctx, "Feuerstelle", "20.5 20 8");
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);

            // The front band (mode-2 depth budget, FUN_0046b450:828-829): at z = 13 the gate
            // z + extent < 13 fails for any extent ≥ 0 → the box branch — asbox = 1, the kiste
            // model is up, and the spot stays valid (green): the confirm delivers the crate
            // (putdown) instead of building. The verified Steam law: box only when
            // pointing at the front Z block.
            pc.UpdateCursor(new Vector3(25.3f, 20f, 13f));
            Assert.IsTrue(pc.Valid, "a front-band box spot is VALID (green)");
            Assert.IsTrue(pc.AsBox, "front-band spot computes asbox = 1");
            Assert.IsFalse(pc.GhostBuildingGo!.activeSelf, "box spot → the building model is down");
            Assert.IsTrue(pc.GhostBoxGo!.activeSelf, "box spot → the kiste model is up");

            // Back over a deep spot: the model swaps back per frame.
            pc.UpdateCursor(new Vector3(30f, 20f, 4f));
            Assert.IsFalse(pc.AsBox);
            Assert.IsTrue(pc.GhostBuildingGo!.activeSelf);
            Assert.IsFalse(pc.GhostBoxGo!.activeSelf);
        }

        // ------------------------------------------------------------------
        // 2. Cursor seat — the mode-2 vertical settle onto the support row
        // (FUN_0046aec0:71-77 + FUN_0046b190 + FUN_0046b450:795-920)
        // ------------------------------------------------------------------

        [Test]
        public void CursorSeat_SettlesOntoTheSupportRow_KeepsThePickedDepth()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/BlockDB2.BDB"),
                "original data dir not present — floor-seat test skipped");
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            // An open band (rows y<21) over a SOLID GROUND stripe (y=21..22) — the synthetic
            // meadow: the settle must fall from a y=20 pick onto the y=21 support row. A lone
            // solid column at (40, 18..19) gives an on-top-of-a-block support.
            var solid = new List<(int, int)> { (40, 18), (40, 19) };
            for (int x = 16; x <= 48; x++) { solid.Add((x, 21)); solid.Add((x, 22)); }
            ctx.Terrain = MakeOpenTerrain(64, 64, solid.ToArray());

            var crate = BoxedCrate(interp, ctx, "B", "20.5 20 8");
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);

            // The faithful mode-2 seat: x snaps by the native law ROUND(2x+0.5)·0.5 under
            // round-half-to-even (28.2 → 28.5); the VERTICAL settles onto the first h>z support
            // row BELOW the pick (FUN_0046b190); the DEPTH stays the pick's — mode 2 never
            // overwrites z (FUN_0046b450:1971-1973 store the pick's x/z with the settled y).
            pc.UpdateCursor(new Vector3(28.2f, 20f, 8f));
            Assert.AreEqual(28.5f, pc.GhostPos.x, 1e-5f, "x → the native half-tile snap (28.2 → 28.5)");
            Assert.AreEqual(8f, pc.GhostPos.z, 1e-5f, "the ghost keeps the PICKED depth (raw z)");
            Assert.AreEqual(21f, pc.GhostPos.y, 1e-5f, "the vertical settles onto the ground stripe's support row");
            Assert.IsTrue(pc.Valid, "a settled open-floor spot is valid");
            Assert.IsFalse(pc.AsBox, "deep pick (8 + ext < 13) → the building branch");

            // A pick INSIDE the solid column climbs OUT of the rock (FUN_0046b190 loop 1) and
            // rests on TOP of the block — the SEAT is legit (settled, y=18) but the SPOT is
            // NOT: the lone 1-tile pillar leaves every footprint base corner hanging over the
            // open columns beside it, which the FUN_005e76a0 corner-boundary law rejects
            // (corner + agreeing neighbourhood in air, h ≤ z−1.0 — the placement-too-permissive
            // fix,; the pre-terrain-fit port asserted Valid here).
            pc.UpdateCursor(new Vector3(40f, 18.5f, 8f));
            Assert.AreEqual(18f, pc.GhostPos.y, 1e-5f, "seated on the block's top row");
            Assert.IsFalse(pc.Valid, "a footprint hanging over the void fails the corner law");

            // A pick outside the world's 0..15 depth slabs never finds support (h ≤ z always).
            pc.UpdateCursor(new Vector3(28f, 20f, 17f));
            Assert.IsFalse(pc.Valid, "outside the depth slab range the settle exhausts ⇒ +0xcc = 0");

            // The world border: OOB tiles read solid at every depth — the upward march exhausts
            // (the native pick's border rejection, FUN_0046aec0:21-24, has the same effect).
            pc.UpdateCursor(new Vector3(200f, 20f, 8f));
            Assert.IsFalse(pc.Valid, "outside the map — no seat");

            // A pick hanging over a 16-row-deep void (x=28, y=10: nothing solid within the
            // fall budget) exhausts the down-march ⇒ invalid (FUN_0046b190:113-120).
            pc.UpdateCursor(new Vector3(28f, 10f, 8f));
            Assert.IsFalse(pc.Valid, "no support within 16 rows below ⇒ settle failure");

            // The confirmed order carries the SETTLED position (support vertical + picked depth).
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Confirm());
            Assert.AreEqual(8f, crate.OrderPos.z, 1e-5f, "the deferred order stores the settled pos (picked depth)");
            Assert.AreEqual(21f, crate.OrderPos.y, 1e-5f, "…with the settled vertical");
        }

        // ------------------------------------------------------------------
        // 3. ASBOX — computed per frame from the depth-budget branch (+0xcd,
        // FUN_0046b450:828-829/:1970; overlap failure does NOT fall to the box,:915)
        // ------------------------------------------------------------------

        [Test]
        public void AsBox_ComputedPerFrame_FromTheDepthBudget()
        {
            var (interp, ctx) = Host();
            var crate = BoxedCrate(interp, ctx, "B", "20.5 20 8");
            var blocker = Spawn(interp, ctx, "B", "25 20 8");   // kind production ⇒ collidable
            blocker.Owner = 0;
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);

            // Deep clear ground: building spot → asbox 0, valid.
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Valid);
            Assert.IsFalse(pc.AsBox, "deep spot (z + ext < 13) → asbox = 0");

            // The FRONT BAND (z=13: 13 + ext ≥ 13 for any extent): the box branch — asbox 1,
            // STILL valid (the tutorial's '1 1 1').
            pc.UpdateCursor(new Vector3(28f, 20f, 13f));
            Assert.IsTrue(pc.Valid, "a front-band box spot is VALID");
            Assert.IsTrue(pc.AsBox, "front band → asbox = 1");

            // Overlapping a collidable building with the budget PASSING: invalid BUILDING —
            // the native exits without the box retry (FUN_0046b450:915), asbox stays 0.
            pc.UpdateCursor(new Vector3(25f, 20f, 8f));
            Assert.IsFalse(pc.Valid, "blocked building spot is invalid");
            Assert.IsFalse(pc.AsBox, "…and does NOT become a box (mode 2 has no overlap fallback)");

            // The kiste-pair exemption (FUN_005e7000:80): a BOXED crate lying around never
            // blocks — placing right at it stays a clean building spot.
            var box2 = BoxedCrate(interp, ctx, "B", "31 20 8");
            pc.UpdateCursor(new Vector3(31f, 20f, 8f));
            Assert.IsTrue(pc.Valid, "boxed crates do not block placement (the kiste exemption)");
            Assert.IsFalse(pc.AsBox);
        }

        // The collision gate inside the overlap approximation (FUN_005e7000:73-77 — the
        // rec+0xb8 flag): deco/dummy entities (grass tufts, info markers, ground pickups)
        // never block a spot. Blocking on them X-striped the whole meadow after the deco
        // revival (34b8467;).
        [Test]
        public void SpotClear_DecoAndMarkersDoNotBlock()
        {
            var (interp, ctx) = Host();
            interp.Eval("def_class DecoGras none dummy 0 {} {}");
            interp.Eval("def_class InfoMark none info 0 {} {}");
            interp.Eval("def_class PilzItem none material 0 {} {}");
            var crate = BoxedCrate(interp, ctx, "B", "20.5 20 8");
            Spawn(interp, ctx, "DecoGras", "28 20 8");
            Spawn(interp, ctx, "InfoMark", "28.2 20 8");
            Spawn(interp, ctx, "PilzItem", "27.8 20 8");
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);

            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Valid, "deco/info/material entities must not block placement");
            Assert.IsFalse(pc.AsBox);

            // …but an explicit set_collision 1 deco (dummy_trollhausen.tcl:49 class_collision 1
            // analog) DOES block.
            var hut = Spawn(interp, ctx, "DecoGras", "34 20 8");
            interp.Eval($"set_collision {hut.Handle} 1");
            pc.UpdateCursor(new Vector3(34f, 20f, 8f));
            Assert.IsFalse(pc.Valid, "a collision-flagged deco blocks like the native rec+0xb8 gate");

            // …and a GROWING plant blocks even though its class kind is "material": the standing
            // Pilz's obj_init runs `set_collision this 1` (pilz.tcl:42), so its rec+0xb8 is SET —
            // the native overlap FUN_005e7000:73-77 vetoes building over it. Only the HARVESTED
            // drops (Pilzhut & co, no set_collision) stay transparent.
            var growing = Spawn(interp, ctx, "PilzItem", "40 20 8");
            interp.Eval($"set_collision {growing.Handle} 1");
            pc.UpdateCursor(new Vector3(40f, 20f, 8f));
            Assert.IsFalse(pc.Valid, "a growing plant (material + set_collision 1) blocks placement");
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsTrue(pc.Valid, "the loose material without the flag still never blocks");
        }

        [Test]
        public void ToggleAsBoxShim_SurvivesThePerFrameRecompute()
        {
            // The DEPRECATED test/UI shim (the native has no toggle — +0xcd is computed): a
            // forced asbox must hold through UpdateCursor's recompute on a building-valid spot,
            // or the pre-existing BuildupCarrier asbox tests (toggle THEN cursor) would break.
            var (interp, ctx) = Host();
            var crate = BoxedCrate(interp, ctx, "B", "20.5 20 8");
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);
            pc.ToggleAsBox();
            Assert.IsTrue(pc.AsBox);
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));       // clear ground — building would fit
            Assert.IsTrue(pc.AsBox, "the forced asbox survives the per-frame recompute");
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(crate.OrderAsBox, "the confirm follows the (forced) asbox");
        }

        // ------------------------------------------------------------------
        // 4. get_place_info — the published triple (FUN_0064c1e0; the tutorial gates)
        // ------------------------------------------------------------------

        [Test]
        public void GetPlaceInfo_PublishesPlacingGreenAsbox()
        {
            var (interp, ctx) = Host();
            Assert.AreEqual("0 0 0", interp.Eval("get_place_info").Trim(), "not placing → 0 0 0");

            var crate = BoxedCrate(interp, ctx, "B", "20.5 20 8");
            var blocker = Spawn(interp, ctx, "B", "25 20 8");
            blocker.Owner = 0;
            var pc = ctx.Placement!;
            pc.OrderBuild(crate.Handle);

            // Ghost on the mouse, no validated spot yet (the +0xcc start state) — the
            // tutorial's "fireplaceonmouse" gate (Trigger_Tutorial.tcl:1320).
            Assert.AreEqual("1 0 0", interp.Eval("get_place_info").Trim());

            // Green building spot — "fireplacegreen" (:1321).
            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.AreEqual("1 1 0", interp.Eval("get_place_info").Trim());

            // Front-band box spot — "fireplacebox" (:1322): green AND asbox (the mode-2 depth
            // budget picks the box branch at z = 13, FUN_0046b450:828-829).
            pc.UpdateCursor(new Vector3(28f, 20f, 13f));
            Assert.AreEqual("1 1 1", interp.Eval("get_place_info").Trim());

            pc.Cancel();
            Assert.AreEqual("0 0 0", interp.Eval("get_place_info").Trim(), "cancel → not placing");
        }

        // ------------------------------------------------------------------
        // 5. CONFIRM — the payload follows the COMPUTED asbox (FUN_00531310:229-265)
        // ------------------------------------------------------------------

        [Test]
        public void Confirm_FollowsTheComputedAsBox()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            var pc = ctx.Placement!;

            // Front-band box spot → the deferred order goes out AS BOX (+0x2d = 1, the putdown
            // route; the mode-2 depth budget picks the box at z = 13).
            var crate1 = BoxedCrate(interp, ctx, "B", "20.5 20 8");
            pc.OrderBuild(crate1.Handle);
            pc.UpdateCursor(new Vector3(25.3f, 20f, 13f));
            Assert.IsTrue(pc.AsBox);
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(crate1.OrderAsBox, "computed asbox = 1 must flow into the order (+0x2d)");

            // Deep building spot → asprod (+0x2d = 0).
            var crate2 = BoxedCrate(interp, ctx, "B", "21.5 24 8");
            pc.OrderBuild(crate2.Handle);
            pc.UpdateCursor(new Vector3(30f, 24f, 8f));
            Assert.IsFalse(pc.AsBox);
            Assert.IsTrue(pc.Confirm());
            Assert.IsFalse(crate2.OrderAsBox, "computed asbox = 0 must flow into the order");
        }

        // ------------------------------------------------------------------
        // 6. SPECIAL ATTACHMENT MODES — wall/ceiling traps (tick modes 8/9)
        // ------------------------------------------------------------------

        [Test]
        public void PlaceSnapMode_CommandsRoundTripOnTheObjectRecord()
        {
            var (interp, ctx) = Host();
            var trap = Spawn(interp, ctx, "B", "20 20 8");
            Assert.AreEqual("0", interp.Eval($"get_placesnapmode {trap.Handle}"));
            interp.Eval($"set_placesnapmode {trap.Handle} 1");
            Assert.AreEqual("1", interp.Eval($"get_placesnapmode {trap.Handle}"));
            Assert.AreEqual(1, trap.PlaceSnapMode);
            interp.Eval($"set_placesnapmode {trap.Handle} 2");
            Assert.AreEqual("2", interp.Eval($"get_placesnapmode {trap.Handle}"));
        }

        [Test]
        public void WallSnapMode_SeatsOnlyAgainstAFlatFaceInsideDepthTolerance()
        {
            var cursor = new Vector3(10f, 10f, 9f);
            var bbMin = new Vector3(-200f, -200f, -100f);
            var bbMax = new Vector3(200f, 0f, 100f);
            byte Sub(int _, int sy) => sy == 82 || sy == 83 ? (byte)2 : (byte)0;
            int FlatHmap(int _, int __) => 10;

            Assert.IsTrue(PlacementController.TryComputeWallMount(
                cursor, bbMin, bbMax, Sub, FlatHmap, out var mounted));
            Assert.AreEqual(9.575f, mounted.y, 1e-5f,
                "the 2..15 sub-row scan and DAT_007370d4=0.8 offset define the wall seat");
            Assert.AreEqual(11f, mounted.z, 1e-5f,
                "the model back face is moved onto the common hmap depth");

            int BrokenHmap(int sx, int _) => sx > 80 ? 9 : 10;
            Assert.IsFalse(PlacementController.TryComputeWallMount(
                cursor, bbMin, bbMax, Sub, BrokenHmap, out _),
                "all four side-silhouette corners must report the same wall depth");

            Assert.IsFalse(PlacementController.TryComputeWallMount(
                new Vector3(10f, 10f, 14f), bbMin, bbMax, Sub, FlatHmap, out _),
                "|picked depth - wall depth| must remain strictly below DAT_00737398=4");
        }

        [Test]
        public void CeilingSnapMode_RequiresUpperCornersInRock_AndBaseOnBoundary()
        {
            var pos = new Vector3(10f, 10f, 13f);
            var bbMin = new Vector3(-200f, -400f, -100f);
            var bbMax = new Vector3(200f, 0f, 100f);
            int CeilingAndFloor(int _, int sy) => sy < 75 ? 15 : 13;
            Assert.IsTrue(PlacementController.ComputeCeilingTerrainFit(
                pos, bbMin, bbMax, CeilingAndFloor),
                "upper corners are inside the ceiling and lower corners straddle the floor boundary");

            int MissingCeiling(int _, int __) => 13;
            Assert.IsFalse(PlacementController.ComputeCeilingTerrainFit(
                pos, bbMin, bbMax, MissingCeiling),
                "an upper corner in front of the ceiling relief rejects the hanging trap");

            int BuriedBase(int _, int __) => 15;
            Assert.IsFalse(PlacementController.ComputeCeilingTerrainFit(
                pos, bbMin, bbMax, BuriedBase),
                "lower corners buried more than one depth unit into agreeing rock reject the seat");
        }

        [Test]
        public void DoorPlacement_Mode5AcceptsOnlyHorizontalTunnelCells()
        {
            var terrainObject = new GameObject("DoorPlacementTerrain");
            _gos.Add(terrainObject);
            var terrain = terrainObject.AddComponent<DiggableTerrain>();
            var (interp, ctx) = Host();
            ctx.Terrain = terrain;
            interp.Eval("def_class Door none transport 0 {} {}");
            var door = BoxedCrate(interp, ctx, "Door", "10 10 3");
            interp.Eval($"create_doorlogic {door.Handle}");

            var placement = ctx.Placement!;
            Assert.IsTrue(placement.OrderBuild(door.Handle));
            var cases = new[]
            {
                (shape: DoorTerrainShape.HorizontalTunnel, expected: true),
                (shape: DoorTerrainShape.OpenCave, expected: false),
                (shape: DoorTerrainShape.Meadow, expected: false),
                (shape: DoorTerrainShape.VerticalShaft, expected: false),
            };

            foreach (var placementCase in cases)
            {
                terrain.Nav = MakeDoorPlacementWorld(placementCase.shape);
                placement.UpdateCursor(new Vector3(19.5f, 20f, 4f));

                Assert.AreEqual(
                    placementCase.expected,
                    placement.Valid,
                    $"{placementCase.shape}: {placement.LastReject}");
                Assert.IsFalse(placement.AsBox, $"{placementCase.shape} must remain a door body");
                // Integer x / half y / z 15 — the tueren.tcl:48-52 grid parity; the built
                // door's evt_timer0 re-snap is a NO-OP on this seat.
                Assert.AreEqual(20f, placement.GhostPos.x, 1e-6f);
                Assert.AreEqual(20.5f, placement.GhostPos.y, 1e-6f);
                Assert.AreEqual(15f, placement.GhostPos.z, 1e-6f);
            }
        }

        [TestCase(LadderTerrainShape.ShortShaft, 22f, 7f, true, 10f)]
        [TestCase(LadderTerrainShape.LongShaft, 22f, 7f, true, 22f)]
        [TestCase(LadderTerrainShape.HorizontalCorridor, 22f, 7f, false, 0f)]
        [TestCase(LadderTerrainShape.ShortShaft, 22f, 15f, true, 10f)]
        [TestCase(LadderTerrainShape.WideShaft, 22f, 7f, true, 10f)]
        [TestCase(LadderTerrainShape.ShaftBesideGallery, 22f, 7f, true, 10f)]
        public void LadderPlacement_PreviewLengthTable(
            LadderTerrainShape shape,
            float maximumLength,
            float cursorDepth,
            bool expectedValid,
            float expectedLength)
        {
            var nav = MakeLadderPlacementWorld(shape);

            bool valid = PlacementController.TryComputeLadderMount(
                nav,
                new Vector3(21.6f, 12.2f, cursorDepth),
                maximumLength,
                out Vector3 mounted,
                out float previewLength);

            Assert.AreEqual(expectedValid, valid, shape.ToString());
            Assert.AreEqual(expectedLength, previewLength, 1e-6f, shape.ToString());
            if (!expectedValid) return;
            Assert.AreEqual(21.5f, mounted.x, 1e-6f);
            Assert.AreEqual(shape == LadderTerrainShape.LongShaft ? 40.5f : 18.5f,
                mounted.y,
                1e-6f,
                "mode 4 scans down to the shaft bottom");
            Assert.AreEqual(11f, mounted.z, 1e-6f,
                "front-layer picks still publish the native ladder plane");
        }

        [Test]
        public void LadderPlacement_FrontLayerCrateIsGreen_HorizontalCorridorIsRed()
        {
            var terrainObject = new GameObject("LadderPlacementTerrain");
            _gos.Add(terrainObject);
            var terrain = terrainObject.AddComponent<DiggableTerrain>();
            terrain.Width = 48;
            terrain.Height = 48;
            terrain.Nav = MakeLadderPlacementWorld(LadderTerrainShape.ShortShaft);
            var (interp, ctx) = Host();
            ctx.Terrain = terrain;
            interp.Eval("def_class Ladder none transport 0 {} {}");
            var ladder = BoxedCrate(interp, ctx, "Ladder", "9.5 8 3");
            interp.Eval($"create_ladderlogic {ladder.Handle}");
            interp.Eval($"set_ladderrange {ladder.Handle} 22");
            interp.Eval($"set_snaptowall {ladder.Handle} 1");
            var placement = ctx.Placement!;
            Assert.IsTrue(placement.OrderBuild(ladder.Handle));

            placement.UpdateCursor(new Vector3(21.6f, 12.2f, 15f));

            Assert.IsTrue(placement.Valid, placement.LastReject);
            Assert.IsFalse(placement.AsBox);
            Assert.AreEqual(10f, placement.LadderPreviewLength, 1e-6f);
            Assert.AreEqual(11f, placement.GhostPos.z, 1e-6f);

            terrain.Nav = MakeLadderPlacementWorld(LadderTerrainShape.HorizontalCorridor);
            placement.UpdateCursor(new Vector3(21.6f, 17.2f, 13f));

            Assert.IsTrue(placement.Valid, placement.LastReject);
            Assert.IsTrue(placement.AsBox,
                "the front depth band keeps the native crate-putdown fallback");
            Assert.AreEqual(0f, placement.LadderPreviewLength, 1e-6f);

            placement.UpdateCursor(new Vector3(21.6f, 17.2f, 7f));

            Assert.IsFalse(placement.Valid, placement.LastReject);
            Assert.IsFalse(placement.AsBox, "corridors must not fall through to a placeable box");
            Assert.AreEqual(0f, placement.LadderPreviewLength, 1e-6f);
        }

        [Test]
        public void LadderPlacement_GhostModelTracksScannedPreviewLength()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/3db/transport.3db"),
                "original transport model db not present");
            var terrainObject = new GameObject("LadderPreviewTerrain");
            var worldRoot = new GameObject("LadderPreviewRoot");
            _gos.Add(terrainObject);
            _gos.Add(worldRoot);
            var terrain = terrainObject.AddComponent<DiggableTerrain>();
            terrain.Nav = MakeLadderPlacementWorld(LadderTerrainShape.ShortShaft);
            var (interpreter, context) = Host();
            context.Terrain = terrain;
            var library = new DigglesModelLibrary(DataRoot);
            context.ModelLibrary = library;
            context.SpawnParent = worldRoot.transform;
            interpreter.Eval("def_class Leiter none transport 0 {} {}");
            var ladder = BoxedCrate(interpreter, context, "Leiter", "9.5 8 3");
            interpreter.Eval($"create_ladderlogic {ladder.Handle}");
            interpreter.Eval($"set_ladderrange {ladder.Handle} 22");
            var placement = context.Placement!;
            Assert.IsTrue(placement.OrderBuild(ladder.Handle));

            placement.UpdateCursor(new Vector3(21.6f, 12.2f, 7f));

            // span 10 → span+1 = 11 fits slot 1 (leiter_b threshold 9.296) but not slot 2
            // (leiter_c 13.264) — the shortest PLACEABLE ladder is leiter_b, never leiter_a
            // (slot 0 is the mode-4 reject, FUN_0060b560).
            var shortMesh = library.GetClip("leiter_b", "standard")!.Frames[0];
            Assert.AreSame(shortMesh,
                placement.GhostBuildingGo!.GetComponent<MeshFilter>().sharedMesh);

            terrain.Nav = MakeLadderPlacementWorld(LadderTerrainShape.LongShaft);
            placement.UpdateCursor(new Vector3(21.6f, 12.2f, 7f));

            var cappedMesh = library.GetClip("leiter_e", "standard")!.Frames[0];
            Assert.AreSame(cappedMesh,
                placement.GhostBuildingGo!.GetComponent<MeshFilter>().sharedMesh,
                "the 22-unit cap selects the longest registered rung model that fits span+1");
        }
    }
}
