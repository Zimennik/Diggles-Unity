#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Item placement / putdown / product-drop seat + packing laws:
    // (1) put-down items end up sunk/buried behind the ground relief (invisible);
    // (2) production crates land off to the side (native crates hug the building);
    // (3) dropped items/food spread too far (native packs the 2-D ground plane tight).
    // Law + citations: Docs/ITEM_PLACEMENT_RE.md. Frame (ant-farm): native.x = east,
    // native.y = screen-vertical (floor lines are y rows), native.z = depth into the scene
    // (0 = deepest, 14.5 = the front glass). get_hmap(x,y) = how far the rock relief extends
    // toward the viewer at that cell; an item is hidden when its z < the relief around it.
    // The standing/seat law: z = hmap(x, y-1) + 1 — one nibble in front of the back-wall
    // relief (vector_fix, FUN_00595770:256-262; cap 14.5 = DAT_00737564).
    //
    // The native CPlaceFinder scans depth as a free coordinate: FindPlace sweeps (x-column, z)
    // pairs over the band — rect: z in [center.z+z0, center.z+z1], circle: [center.z-2R,
    // center.z+2R] (FUN_005a9700:149-152/:163-164) — in 0.5 steps (:249-313, local_44 += 4 with
    // z = local_44*0.125), scoring each against cost = |cand-ideal| + 2*|cand-center| (both
    // depth-weighted x0.5) + randW*rand01*3.0 (FUN_005a9270:65-66/:57, DAT_00737428=3.0).
    // The old port pinned one z per column (the analytic optimum), so (a) a column could never
    // satisfy -mindist through depth alone (native crates land 1..3 in front of the building at
    // dx~0 - the z_work_prod.tcl:217 band [cz+min(1,11-cz), 13-cz]) and (b) one lying item's
    // veto killed its whole column (native slots drops in front/behind an item at the same x),
    // spreading drops linearly along east instead of packing the (x,z) plane.
    [Explicit]   // loads real data + BDB (start meadow); run on demand
    public sealed class ItemPlacementTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        (DigglesEngine engine, NavWorld nav) BuildMeadow()
        {
            var root = new GameObject("WorldRoot");
            _gos.Add(root);
            root.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);   // the Q_x(-90) hack
            var engine = new DigglesEngine();
            engine.Build(DataRoot, root.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
            engine.Map.EmitTerrain(root);
            var terr = root.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            engine.Ctx.Terrain = terr;   // get_place / vector_fix read ctx.Terrain.Nav
            Assert.IsNotNull(terr!.Nav, "no NavWorld");
            return (engine, terr.Nav!);
        }

        // A floor sub-cell in the open meadow band with floor room east/west of it.
        static bool FindFloor(NavWorld nav, out int sx, out int sy)
        {
            for (int ty = 26; ty <= 32; ty++)
            for (int tx = 236; tx <= 256; tx++)
            for (int yy = 0; yy < 8; yy++)
            for (int xx = 0; xx < 8; xx++)
            {
                int gx = tx * 8 + xx, gy = ty * 8 + yy;
                if (nav.SubIsFloor(gx, gy) && nav.SubIsFloor(gx - 8, gy) && nav.SubIsFloor(gx + 8, gy))
                { sx = gx; sy = gy; return true; }
            }
            sx = sy = 0; return false;
        }

        static Vector3 V(string s)
        {
            var t = s.Trim().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            return new Vector3(
                float.Parse(t[0], CultureInfo.InvariantCulture),
                float.Parse(t[1], CultureInfo.InvariantCulture),
                float.Parse(t[2], CultureInfo.InvariantCulture));
        }

        static string RS(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        private static DigglesTclEntity Spawn(
            DigglesEngine engine,
            string className,
            Vector3 position)
        {
            string handle = engine.Interp.Eval(
                $"new {className} \"\" \"{RS(position.x)} {RS(position.y)} {RS(position.z)}\" \"0 0 0\"").Trim();
            DigglesTclEntity? entity = engine.Ctx.GetEntity(handle);
            Assert.IsNotNull(entity, $"failed to spawn {className}");
            return entity!;
        }

        private static bool FindIntegerMeadowFloor(NavWorld nav, out int sx, out int sy)
        {
            for (int ty = 26; ty <= 32; ty++)
                for (int tx = 236; tx <= 256; tx++)
                {
                    int candidateX = tx * 8;
                    for (int yy = 0; yy < 8; yy++)
                    {
                        int candidateY = ty * 8 + yy;
                        if (nav.SubIsFloor(candidateX, candidateY)
                            && nav.SubIsFloor(candidateX - 8, candidateY)
                            && nav.SubIsFloor(candidateX + 8, candidateY)
                            && nav.GetHmapSub(candidateX, candidateY - 1) <= 13)
                        {
                            sx = candidateX;
                            sy = candidateY;
                            return true;
                        }
                    }
                }
            sx = sy = 0;
            return false;
        }

        private static bool DigTunnelFloor(
            DiggableTerrain terrain,
            NavWorld nav,
            out int sx,
            out int sy)
        {
            int digX = -1;
            int surfaceY = -1;
            int surfaceTileY = -1;
            for (int tx = 236; tx <= 256 && digX < 0; tx++)
            {
                int candidateX = tx * 8;
                for (int candidateY = 26 * 8; candidateY < 33 * 8; candidateY++)
                {
                    if (!nav.SubIsFloor(candidateX, candidateY)) continue;
                    int tileY = candidateY >> 3;
                    bool diggable = true;
                    for (int ny = tileY + 1; ny <= tileY + 2 && diggable; ny++)
                        for (int nx = tx; nx <= tx + 2; nx++)
                            if (!terrain.TileMaterialDiggable(nx, ny))
                            {
                                diggable = false;
                                break;
                            }
                    if (!diggable) continue;
                    digX = tx;
                    surfaceY = candidateY;
                    surfaceTileY = tileY;
                    break;
                }
            }

            if (digX < 0)
            {
                sx = sy = 0;
                return false;
            }

            for (int round = 0; round < 64; round++)
            {
                bool changed = false;
                for (int ny = surfaceTileY + 1; ny <= surfaceTileY + 5; ny++)
                    for (int nx = digX; nx <= digX + 2; nx++)
                        changed |= terrain.CarveTileNow(nx, ny);
                if (!changed) break;
            }
            nav.RevalidateRect(
                (digX - 2) * 8,
                (surfaceTileY - 2) * 8,
                (digX + 4) * 8 + 7,
                (surfaceTileY + 7) * 8 + 7);

            for (int candidateY = (surfaceTileY + 7) * 8; candidateY > surfaceY + 4; candidateY--)
                for (int candidateX = digX * 8; candidateX <= (digX + 2) * 8; candidateX += 8)
                    if (nav.SubIsFloor(candidateX, candidateY)
                        && nav.GetHmapSub(candidateX, candidateY - 1) <= 13)
                    {
                        sx = candidateX;
                        sy = candidateY;
                        return true;
                    }

            sx = sy = 0;
            return false;
        }

        private static void AssertRealRingPutdownSeat(
            DigglesEngine engine,
            NavWorld nav,
            int targetSubX,
            int targetSubY,
            string site)
        {
            var interp = engine.Interp;
            var ctx = engine.Ctx;
            interp.Eval("def_class ItemDropSeatGnome none gnome 0 {} {}");
            interp.Eval("call scripts/classes/story/Ringe.tcl");
            interp.Eval("call scripts/init/eventgen.tcl");
            interp.Eval("call scripts/classes/zwerg/z_procs.tcl");
            Assert.IsTrue(ctx.Classes.ContainsKey("Ring_Des_Lebens"), "real ring class was not loaded");

            float requestedDepth = nav.GetHmapSub(targetSubX, targetSubY - 1);
            var requestedBottom = new Vector3(
                targetSubX * 0.125f,
                targetSubY * 0.125f,
                requestedDepth);
            var carrier = Spawn(engine, "ItemDropSeatGnome", new Vector3(
                requestedBottom.x,
                requestedBottom.y,
                requestedDepth + 1f));
            var ring = Spawn(engine, "Ring_Des_Lebens", carrier.Position);
            carrier.Owner = ring.Owner = ctx.LocalPlayer;
            interp.Eval($"inv_add {carrier.Handle} {ring.Handle}");
            Assert.AreEqual(carrier.Handle, ring.ContainedIn, $"{site}: ring was not put in inventory");

            PlacementController placement = ctx.Placement!;
            Assert.IsTrue(placement.ClickInventoryCrate(carrier.Handle, ring.Handle),
                $"{site}: inventory click did not enter item putdown mode");
            Assert.AreEqual(PlacementController.PlacementMode.ItemPutdown, placement.Mode);
            placement.UpdateCursor(requestedBottom);
            Assert.IsTrue(placement.Valid, $"{site}: ring preview rejected: {placement.LastReject}");

            int settledSubX = Mathf.RoundToInt(placement.GhostPos.x * 8f);
            int settledSubY = Mathf.RoundToInt(placement.GhostPos.y * 8f);
            Assert.AreEqual(targetSubX, settledSubX, $"{site}: x left the selected floor cell");
            Assert.AreEqual(targetSubY, settledSubY, $"{site}: y left the selected floor cell");
            float supportLine = nav.GetHmapSub(settledSubX, settledSubY - 1) + 1f;
            float expectedDepth = Mathf.Min(Mathf.Max(requestedDepth, supportLine), 14.5f);
            Assert.AreEqual(expectedDepth, placement.GhostPos.z, 1e-5f,
                $"{site}: preview depth is not min(max(z,hmap(x,y-1)+1),14.5)");
            Assert.GreaterOrEqual(placement.GhostPos.z, supportLine - 1e-5f,
                $"{site}: preview ring is behind the floor relief");

            Assert.IsNotNull(ring.CurrentAnim, $"{site}: ring has no current display animation");
            string displayAnimation = ring.CurrentAnim!;
            int separator = displayAnimation.IndexOf('.');
            Assert.Greater(separator, 0, $"{site}: ring has no resolved display animation");
            Assert.IsTrue(engine.Lib.TryGetMeshBBox(
                displayAnimation.Substring(0, separator),
                displayAnimation.Substring(separator + 1),
                0,
                out _,
                out Vector3 meshMax), $"{site}: ring display bbox did not resolve");
            float bottomOffset = meshMax.y * DigglesModelLibrary.ModelToWorldScale;
            Assert.Greater(Mathf.Abs(bottomOffset), 1e-5f,
                $"{site}: real ring bbox must exercise the set_posbottom offset");

            Assert.IsNotNull(placement.CursorGhost, $"{site}: real ring preview has no visual");
            Vector3 previewAnchor = RuntimeWorldSpawner.LocalToNative(
                ctx.SpawnParent!.InverseTransformPoint(placement.CursorGhost!.transform.position));
            Assert.AreEqual(placement.GhostPos.y - bottomOffset, previewAnchor.y, 1e-5f,
                $"{site}: preview did not subtract bbox.maxY*0.005 exactly once");
            Assert.AreEqual(expectedDepth, previewAnchor.z, 1e-5f,
                $"{site}: preview visual depth differs from the logical seat");

            Vector3 confirmedBottom = placement.GhostPos;
            Assert.IsTrue(placement.Confirm(), $"{site}: valid ring placement did not confirm");
            Assert.Less(
                Vector3.Distance(previewAnchor, placement.OrderGhosts[ring.Handle].Pos),
                1e-4f,
                $"{site}: confirmed preview ghost moved off its displayed anchor");

            // Execute the exact final positioned-drop endpoint: beam_from_inv_to_pos removes the
            // item then calls set_posbottom with the confirmed point (z_procs.tcl:292-323).
            interp.Eval("set current_weapon_item 0; set current_weapon_out 0; " +
                "set current_shield_item 0; set current_shield_out 0; set current_lock_obj 0");
            ctx.This = carrier;
            try
            {
                interp.Eval($"beam_from_inv_to_pos {ring.Handle} " +
                    $"{{{RS(confirmedBottom.x)} {RS(confirmedBottom.y)} {RS(confirmedBottom.z)}}}");
            }
            finally
            {
                ctx.This = null;
            }

            Assert.AreEqual(-1, ring.ContainedIn, $"{site}: final ring never left inventory");
            Assert.AreEqual(expectedDepth, ring.Position.z, 1e-5f,
                $"{site}: final ring depth differs from the floor law");
            Assert.GreaterOrEqual(ring.Position.z, supportLine - 1e-5f,
                $"{site}: final ring is behind the floor relief");
            Assert.AreEqual(confirmedBottom.y - bottomOffset, ring.Position.y, 1e-5f,
                $"{site}: final set_posbottom did not subtract bbox.maxY*0.005 exactly once");
            Assert.Less(Vector3.Distance(previewAnchor, ring.Position), 1e-4f,
                $"{site}: preview and final raw anchors do not match");
            Assert.IsNotNull(ring.GameObject, $"{site}: final real ring has no visual");
            Vector3 finalVisualAnchor = RuntimeWorldSpawner.LocalToNative(
                ctx.SpawnParent!.InverseTransformPoint(ring.GameObject!.transform.position));
            Assert.Less(Vector3.Distance(previewAnchor, finalVisualAnchor), 1e-4f,
                $"{site}: preview and final visuals do not share the same seat");
        }

        // Positioned inventory drops do not pass through vector_fix: the original endpoint is
        // beam_from_inv_to_pos -> set_posbottom (z_procs.tcl:292-323). The port must therefore
        // apply vector_fix's depth law (FUN_00595770:255-267) in the item-only cursor route.
        [Test]
        public void InventoryRingDrop_Meadow_PreviewAndFinalLandOnFloorLine()
        {
            var (engine, nav) = BuildMeadow();
            Assert.IsTrue(FindIntegerMeadowFloor(nav, out int sx, out int sy),
                "no integer-lattice meadow floor cell with an uncapped support line");
            AssertRealRingPutdownSeat(engine, nav, sx, sy, "meadow");
        }

        [Test]
        public void InventoryRingDrop_DugTunnel_PreviewAndFinalLandOnFloorLine()
        {
            var (engine, nav) = BuildMeadow();
            DiggableTerrain terrain = engine.Ctx.Terrain!;
            Assert.IsTrue(DigTunnelFloor(terrain, nav, out int sx, out int sy),
                "failed to carve a tunnel with an integer-lattice floor cell");
            AssertRealRingPutdownSeat(engine, nav, sx, sy, "dug tunnel");
        }

        // ---- Bug 1: the seat law — a get_place result rests in front of the relief ------------
        //
        // The native candidate legality comes from the FindPlace grid (FUN_0041f7a0 build); the
        // cited walk-target contract is the vector_fix standing law z = hmap(x, y-1)+1
        // (FUN_00595770:256-262). A workplace whose own z is low ("buildings slightly sunk",
        // e.g. z = standing-0.7) must not drag the product seat below the standing depth: the
        // old port clamped the result z to min(hA,hO)+0.5 — half a nibble under the standing
        // line (and arbitrarily deep when the center z was low).
        [Test]
        public void SeatLaw_ProductSeat_NotBelowStandingDepth()
        {
            var (engine, nav) = BuildMeadow();
            Assert.IsTrue(FindFloor(nav, out int sx, out int sy), "no meadow floor cell found");
            float wx = sx * 0.125f, wy = sy * 0.125f;
            int hA = nav.GetHmapSub(sx, sy - 1);
            float standZ = hA + 1f;                          // FUN_00595770:256 standing depth
            float sunkZ = Mathf.Max(0.1f, standZ - 0.7f);    // a slightly-sunk workplace z
            TestContext.WriteLine($"floor sub ({sx},{sy}) world({wx:0.###},{wy:0.###}) hA={hA} standZ={standZ} sunk-center z={sunkZ}");

            var res = V(engine.Interp.Eval(
                $"get_place -center {{{RS(wx)} {RS(wy)} {RS(sunkZ)}}} -circle 4 -mindist 1.2 -random 0 -nearpos {{{RS(wx + 2f)} {RS(wy)} {RS(sunkZ)}}}"));
            Assert.AreNotEqual(-1f, res.x, "get_place found nothing for the product seat");
            int rx = Mathf.RoundToInt(res.x * 8f), ry = Mathf.RoundToInt(res.y * 8f);
            float standAtRes = nav.GetHmapSub(rx, ry - 1) + 1f;
            TestContext.WriteLine($"result {res} standing-depth@result={standAtRes}");
            Assert.GreaterOrEqual(res.z, standAtRes - 0.001f,
                "the seat sits BELOW the standing depth hmap(y-1)+1 (FUN_00595770:256) — the item " +
                "renders sunk into the ground relief (playtest: buried/invisible drops)");
        }

        // ---- Bug 2: a littered yard — the crate still lands at the building ---------------------
        //
        // prod_createproduct_box: get_place -center {workplace} -rect -10 z0 10 z1 -mindist 1.5
        // -random 2 -nearpos {gnome} with z0 = hmin(1, 11-cz), z1 = 13-cz (z_work_prod.tcl:217) —
        // the depth band starts 1 in front of the building, so the native can satisfy the whole
        // mindist through depth at dx~0 (the crate hugs the building) even when lying materials
        // block the side ring. The old port pinned z per column: the side ring blocked -> pushed
        // columns further east/west -> "crate lands off to the side".
        [Test]
        public void CrateAnchor_LitteredYard_CrateHugsBuilding()
        {
            var (engine, nav) = BuildMeadow();
            var interp = engine.Interp; var ctx = engine.Ctx;
            Assert.IsTrue(FindFloor(nav, out int sx, out int sy), "no meadow floor cell found");
            float cx = sx * 0.125f, cy = sy * 0.125f;
            float cz = nav.GetHmapSub(sx, sy - 1) + 1f;      // properly seated building
            interp.Eval("def_class TestMat none material 0 {} {}");

            // litter the mindist side-ring east and west with delivered materials, densely in
            // both ground axes (a yard after a materials haul) — the side ring is solidly
            // occupied, so only tight frontal/depth spots remain near the building.
            var litter = new List<Vector3>();
            foreach (float dx in new[] { -2.6f, -2.25f, -1.9f, -1.55f, -1.2f, 1.2f, 1.55f, 1.9f, 2.25f, 2.6f })
                foreach (float dz in new[] { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3.5f })
                    litter.Add(new Vector3(cx + dx, cy, cz + dz));
            foreach (var p in litter)
            {
                string h = interp.Eval($"new TestMat \"\" \"{RS(p.x)} {RS(p.y)} {RS(p.z)}\" \"0 0 0\"").Trim();
                var e = ctx.GetEntity(h); if (e != null) e.Position = p;
            }

            float z0 = Mathf.Min(1f, 11f - cz), z1 = 13f - cz;   // hmin = MIN (z_work_prod.tcl:217)
            string call = $"get_place -center {{{RS(cx)} {RS(cy)} {RS(cz)}}} -rect -10 {RS(z0)} 10 {RS(z1)} -mindist 1.5 -random 0 -nearpos {{{RS(cx + 2f)} {RS(cy)} {RS(cz)}}}";
            var res = V(interp.Eval(call));
            Assert.AreNotEqual(-1f, res.x, "crate get_place found nothing");
            float horiz = Mathf.Sqrt((res.x - cx) * (res.x - cx) + (res.y - cy) * (res.y - cy));
            float dcw = Mathf.Sqrt(horiz * horiz + (res.z - cz) * 0.5f * (res.z - cz) * 0.5f);
            TestContext.WriteLine($"building=({cx:0.##},{cy:0.##},{cz:0.##}) crate={res} horiz={horiz:0.###} weightedDc={dcw:0.###}");
            // The law under test: the crate stays on the tight mindist ring at the building —
            // paying the standoff through depth when the sides are littered (the native scans
            // (x,z), FUN_005a9700:249-313; cost 2*dc pulls to the ring, FUN_005a9270:65) —
            // instead of walking columns east/west past the litter (pre-fix: horiz 2.63, dc
            // 2.67, "crate beside the building").
            Assert.LessOrEqual(dcw, 2.2f,
                $"crate left the tight ring (weighted dc {dcw:0.##} vs mindist 1.5) — flung off the littered building");
            Assert.LessOrEqual(horiz, 2.0f,
                $"crate pushed {horiz:0.##} east/west — the native pays the standoff through depth, not by fleeing sideways");
        }

        // ---- Bug 3: sequential drops pack the 2-D ground plane, not a 1-D east line -------------
        //
        // The native materials veto is a per-grid-cell body stamp (FUN_0041cac0 via 005a9700:226,
        // reject:264) on the 64-cell (x,z) scan grid — cell ~0.31 units — so drops fill the
        // ground plane: the same east-column stays usable at another depth. The old port vetoed
        // +-0.4 east x +-0.9 depth around every item with a single pinned z per column — each drop
        // consumed its whole column and the pile smeared east/west.
        // Shape = the real product/food drop: prod_createproduct_* -rect (z_work_prod.tcl:217/256)
        // — the -rect family keeps the free-depth scan.
        [Test]
        public void PackingDensity_TenDrops_StayCompact()
        {
            var (engine, nav) = BuildMeadow();
            var interp = engine.Interp; var ctx = engine.Ctx;
            Assert.IsTrue(FindFloor(nav, out int sx, out int sy), "no meadow floor cell found");
            float cx = sx * 0.125f, cy = sy * 0.125f;
            float cz = nav.GetHmapSub(sx, sy - 1) + 1f;
            interp.Eval("def_class TestFood none material 0 {} {}");
            float z0 = Mathf.Min(1f, 11f - cz), z1 = 13f - cz;   // the createproduct depth band

            var placed = new List<Vector3>();
            for (int i = 0; i < 10; i++)
            {
                string call = $"get_place -center {{{RS(cx)} {RS(cy)} {RS(cz)}}} -rect -10 {RS(z0)} 10 {RS(z1)} -mindist 1.5 -random 0 -materials true -nearpos {{{RS(cx + 1.5f)} {RS(cy)} {RS(cz)}}}";
                var res = V(interp.Eval(call));
                if (res.x < 0f)   // ring full under the veto -> the scripts' -materials false fallback
                    res = V(interp.Eval(call.Replace("-materials true", "-materials false")));
                if (res.x < 0f) { TestContext.WriteLine($"  drop {i}: no place"); continue; }
                string h = interp.Eval($"new TestFood \"\" \"{RS(res.x)} {RS(res.y)} {RS(res.z)}\" \"0 0 0\"").Trim();
                var e = ctx.GetEntity(h); if (e != null) e.Position = res;
                placed.Add(res);
                TestContext.WriteLine($"  drop {i}: {res}");
            }
            Assert.GreaterOrEqual(placed.Count, 8, "too few drops placed to judge packing");

            // (a) the pile is compact: max depth-weighted distance from the anchor stays small —
            // 10 items at native grid density (~0.31 cells) fit well inside r~2.
            float maxDc = 0f;
            foreach (var p in placed)
            {
                float dx = p.x - cx, dy = p.y - cy, dz = (p.z - cz) * 0.5f;
                maxDc = Mathf.Max(maxDc, Mathf.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            // (b) neighbours stay adjacent (~an item footprint), not spread.
            var nn = new List<float>();
            for (int i = 0; i < placed.Count; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < placed.Count; j++)
                {
                    if (i == j) continue;
                    float dx = placed[i].x - placed[j].x, dy = placed[i].y - placed[j].y;
                    float dz = (placed[i].z - placed[j].z) * 0.5f;
                    best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dy * dy + dz * dz));
                }
                nn.Add(best);
            }
            nn.Sort();
            float median = nn[nn.Count / 2];
            TestContext.WriteLine($"pile: n={placed.Count} maxDistFromAnchor={maxDc:0.###} medianNeighbour={median:0.###}");
            Assert.Less(median, 1.0f, $"drops over-spread (median neighbour {median:0.##}) — the original packs adjacent");
            Assert.Less(maxDc, 2.2f,
                $"the pile smeared to r={maxDc:0.##} — the native packs the (x,z) ground plane " +
                "(same column reusable at another depth), not a 1-D east line");
        }

        // ---- Wander/side-step circle shapes keep the caller's depth -----------------------------
        //
        // act_when_idle's crowd dispersal = walk_random [irandom 2 4] (z_procs.tcl:1298/:1305) =
        // `get_place -center (gnome) -circle L -mindist 0.7 -random L -walldist 1`
        // (z_dignwalk.tcl:725). Scanning the whole standing pocket in depth for this shape made
        // the jittered winners scatter along z — gnomes "spread" invisibly in depth and stacked
        // on screen (post-cutscene crowd piles at one point). The circle-shape law: winners stay
        // at the caller's depth (the pinned z-cost optimum), spreading east/row instead.
        [Test]
        public void WanderSideStep_CircleShape_KeepsCallerDepth_SpreadsAcrossGround()
        {
            var (engine, nav) = BuildMeadow();
            var interp = engine.Interp; var ctx = engine.Ctx;
            Assert.IsTrue(FindFloor(nav, out int sx, out int sy), "no meadow floor cell found");
            float cx = sx * 0.125f, cy = sy * 0.125f;
            float gz = Mathf.Min(nav.GetHmapSub(sx, sy - 1) + 1f + 8f, 13f);   // a mid-pocket camp depth
            interp.Eval("def_class TestGnome none gnome 0 {} {}");

            // the post-cutscene formation: 5 gnomes 0.3 apart (all inside each other's crowd box)
            var gnomes = new List<DigglesTclEntity>();
            for (int k = 0; k < 5; k++)
            {
                string h = interp.Eval($"new TestGnome \"\" \"{RS(cx + 0.3f * k)} {RS(cy)} {RS(gz)}\" \"0 0 0\"").Trim();
                var e = ctx.GetEntity(h);
                Assert.IsNotNull(e, "gnome spawn failed");
                e!.Position = new Vector3(cx + 0.3f * k, cy, gz);
                gnomes.Add(e);
            }

            var winners = new List<Vector3>();
            try
            {
                foreach (var g in gnomes)
                {
                    ctx.This = g;
                    for (int r = 0; r < 3; r++)   // a few side-step rounds per gnome
                    {
                        float plen = 2f + r;      // irandom 2 4
                        string call = $"get_place -center {{{RS(g.Position.x)} {RS(g.Position.y)} {RS(g.Position.z)}}} -circle {RS(plen)} -mindist 0.7 -random {RS(plen)} -walldist 1";
                        var res = V(interp.Eval(call));
                        if (res.x < 0f) { TestContext.WriteLine($"  gnome#{g.Handle} r{r}: -1 (no place)"); continue; }
                        winners.Add(res);
                        TestContext.WriteLine($"  gnome#{g.Handle} r{r}: {res}  dz={res.z - g.Position.z:+0.##;-0.##}");
                        Assert.LessOrEqual(Mathf.Abs(res.z - g.Position.z), 1.01f,
                            "a side-step winner left the caller's depth — gnomes disperse into the SCREEN " +
                            "instead of across it (r13 A: the crowd looks stacked at one point)");
                    }
                }
            }
            finally { ctx.This = null; }
            Assert.GreaterOrEqual(winners.Count, 10, "side-step get_place kept failing (-1) — no dispersal at all");

            // the dispersal must be visible: several distinct ground spots (east/row spread)
            int distinct = 0;
            for (int i = 0; i < winners.Count; i++)
            {
                bool dup = false;
                for (int j = 0; j < i && !dup; j++)
                    if (Mathf.Abs(winners[i].x - winners[j].x) < 0.25f
                        && Mathf.Abs(winners[i].y - winners[j].y) < 0.25f) dup = true;
                if (!dup) distinct++;
            }
            TestContext.WriteLine($"winners={winners.Count} distinct-ground-spots={distinct}");
            Assert.GreaterOrEqual(distinct, 3, "side-step winners collapse onto one spot — no on-screen dispersal");
        }

        // ---- The item-approach ring spot honours the interaction gate ---------------------------
        //
        // open_box → walk_near_item item 0.6 → `get_place -center {item} -nearpos {gnome}
        // -mindist 0.6 -circle 2.6 -except this` (z_dignwalk.tcl:764) → walk → check_switcher_dist:
        // 3-D dist > 1 ⇒ tasklist_clear = the open silently dies (z_procs.tcl:161-166,:953-956).
        // With free depth the ring spot for a depth-offset approach lands |dz| up to 2·radius from
        // the item (weighted metric hides half of it) and the gate kills the interaction (barrels
        // won't open). The circle pin keeps the spot within the gate.
        [Test]
        public void BarrelApproach_RingSpot_WithinInteractionGate()
        {
            var (engine, nav) = BuildMeadow();
            var interp = engine.Interp; var ctx = engine.Ctx;
            Assert.IsTrue(FindFloor(nav, out int sx, out int sy), "no meadow floor cell found");
            float bx = sx * 0.125f, by = sy * 0.125f;
            float pocketLo = nav.GetHmapSub(sx, sy - 1) + 1f;
            float bz = Mathf.Min(pocketLo + 9f, 13f);            // barrel mid-pocket
            float gz = Mathf.Max(pocketLo, bz - 8f);             // the gnome approaches from a drifted depth
            interp.Eval("def_class TestTonne none item 0 {} {}");
            interp.Eval("def_class TestGnome none gnome 0 {} {}");
            string bh = interp.Eval($"new TestTonne \"\" \"{RS(bx)} {RS(by)} {RS(bz)}\" \"0 0 0\"").Trim();
            var barrel = ctx.GetEntity(bh); Assert.IsNotNull(barrel, "barrel spawn failed");
            barrel!.Position = new Vector3(bx, by, bz);
            string gh = interp.Eval($"new TestGnome \"\" \"{RS(bx + 0.5f)} {RS(by)} {RS(gz)}\" \"0 0 0\"").Trim();
            var gnome = ctx.GetEntity(gh); Assert.IsNotNull(gnome, "gnome spawn failed");
            gnome!.Position = new Vector3(bx + 0.5f, by, gz);

            Vector3 res;
            try
            {
                ctx.This = gnome;
                res = V(interp.Eval(
                    $"get_place -center {{{RS(bx)} {RS(by)} {RS(bz)}}} -nearpos {{{RS(gnome.Position.x)} {RS(gnome.Position.y)} {RS(gnome.Position.z)}}} -mindist 0.6 -circle 2.6 -except {gh}"));
            }
            finally { ctx.This = null; }
            Assert.AreNotEqual(-1f, res.x, "approach get_place found nothing");
            float d3 = Vector3.Distance(res, barrel.Position);   // the RAW 3-D gate metric
            TestContext.WriteLine($"barrel=({bx:0.##},{by:0.##},{bz:0.##}) gnomeZ={gz:0.##} spot={res} dist3d={d3:0.###}");
            Assert.LessOrEqual(d3, 1.0f,
                $"the approach spot is {d3:0.##} from the item — check_switcher_dist (>1 ⇒ tasklist_clear, " +
                "z_procs.tcl:161-166) aborts the interaction (r13 B: barrels won't open)");
        }

        [Test]
        public void RearWallBarrel_ApproachSeat_IsReachableAndWithinInteractionGate()
        {
            var (engine, nav) = BuildMeadow();
            var interp = engine.Interp;
            var ctx = engine.Ctx;
            var barrelPosition = new Vector3(244.7326f, 30.5f, 3.11999f);
            var gnomePosition = new Vector3(248.84f, 30.5f, 9.82f);
            int barrelSubX = Mathf.RoundToInt(barrelPosition.x * 8f);
            int barrelSubY = Mathf.RoundToInt(barrelPosition.y * 8f);

            Assert.AreEqual(1958, barrelSubX, "rear Schatztonne must exercise its authored meadow column");
            Assert.IsTrue(nav.SubIsFloor(barrelSubX, barrelSubY),
                "the authored rear barrel stands on the meadow floor row");
            Assert.GreaterOrEqual(barrelPosition.z, nav.GetHmapSub(barrelSubX, barrelSubY - 1) + 1f,
                "the rear barrel is not buried behind the meadow relief");

            interp.Eval("def_class RearTestTonne none item 0 {} {}");
            interp.Eval("def_class RearTestGnome none gnome 0 {} {}");
            DigglesTclEntity barrel = Spawn(engine, "RearTestTonne", barrelPosition);
            DigglesTclEntity gnome = Spawn(engine, "RearTestGnome", gnomePosition);
            interp.Eval($"set_physic {barrel.Handle} 0");

            Vector3 rawSeat;
            try
            {
                ctx.This = gnome;
                rawSeat = V(interp.Eval(
                    $"get_place -center {{{RS(barrelPosition.x)} {RS(barrelPosition.y)} {RS(barrelPosition.z)}}} " +
                    $"-nearpos {{{RS(gnomePosition.x)} {RS(gnomePosition.y)} {RS(gnomePosition.z)}}} " +
                    "-mindist 0.6 -circle 2.6 -except this"));
            }
            finally
            {
                ctx.This = null;
            }

            Assert.AreNotEqual(-1f, rawSeat.x, "walk_near_item must find a seat for the rear barrel");
            Vector3 seat = V(interp.Eval(
                $"vector_fix {{{RS(rawSeat.x)} {RS(rawSeat.y)} {RS(rawSeat.z)}}}"));
            int seatSubX = Mathf.RoundToInt(seat.x * 8f);
            int seatSubY = Mathf.RoundToInt(seat.y * 8f);
            Assert.IsTrue(nav.SubIsFloor(seatSubX, seatSubY),
                "the post-vector_fix approach seat must be a meadow floor cell");
            Assert.LessOrEqual(Vector3.Distance(seat, barrel.Position), 1f,
                "open_box clears the task when the final 3-D distance exceeds one unit");

            DiggableTerrain terrain = ctx.Terrain!;
            var navService = terrain.gameObject.GetComponent<NavWorldService>()
                ?? terrain.gameObject.AddComponent<NavWorldService>();
            ctx.NavService = navService;
            Transform root = terrain.transform;
            Vector3 World(Vector3 native) =>
                root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native));
            var path = navService.FindPath(World(gnome.Position), World(seat));

            Assert.IsNotNull(path,
                $"the rear-barrel seat must route from the meadow ({NavWorldService.LastNullReason})");
            var route = path!;
            Assert.IsNotEmpty(route);
            Vector3 landing = RuntimeWorldSpawner.LocalToNative(
                root.InverseTransformPoint(route[route.Count - 1].Pos));
            Assert.LessOrEqual(Vector3.Distance(landing, barrel.Position), 1f,
                "the path resolver's final depth must also survive open_box's 3-D distance gate");
        }

        // ---- Putdown-in-place never buries the item ----------------------------------------------
        //
        // Drop-in-place (double-click / putdown pos=0) seats the item at gnome pos ± abst through
        // beamto_world (z_procs.tcl:379-395: abst = 0.8 boxed / 0.5, npos = pos + (−sin·a, 0,
        // cos·a), set_posbottom [vector_fix npos]). A gnome standing at the pocket floor (hA+1)
        // next to a relief bulge/step hands vector_fix a below-relief z at the neighbour column —
        // the native walkable branch passes it through (FUN_00595770:270-275) and the ring/pannier
        // renders inside the ground. The port deviation seats it by the spiral branch's own law:
        // z = min(max(z, hmap(y−1)+1), 14.5) (:256-262).
        [Test]
        public void InventoryDrop_InPlace_SeatNotBuried()
        {
            var (engine, nav) = BuildMeadow();
            var interp = engine.Interp;

            // find a walkable pair of nearby columns whose relief steps up ≥ 2 nibbles (a back-wall
            // bulge / cave edge) — the burial geometry.
            int fx = -1, fy = -1, tx = -1; int bestJump = 0;
            for (int ty = 22; ty <= 38 && bestJump < 2; ty++)
            for (int gx = 224 * 8; gx < 264 * 8 && bestJump < 2; gx++)
            {
                int gy = ty * 8 + 4;
                for (int yy = 0; yy < 8; yy++)
                {
                    int cyy = ty * 8 + yy;
                    if (!nav.SubWalkable(gx, cyy) || !nav.SubWalkable(gx + 6, cyy)) continue;
                    int a = nav.GetHmapSub(gx, cyy - 1), b = nav.GetHmapSub(gx + 6, cyy - 1);
                    if (b - a >= 2 && a < 13)
                    { fx = gx; fy = cyy; tx = gx + 6; bestJump = b - a; break; }
                }
            }
            if (fx < 0)
            {
                Assert.Ignore("no ≥2-nibble relief step found in the scan window — geometry not present on this map");
                return;
            }
            float gnomeX = fx * 0.125f, rowY = fy * 0.125f;
            int hLow = nav.GetHmapSub(fx, fy - 1), hHigh = nav.GetHmapSub(tx, fy - 1);
            float gnomeZ = hLow + 1f;                       // the pocket-floor minimum stand
            // beamto_world toward the bulge, boxed pannier: npos = gnome + (0.75 east, 0, −0.28)
            // (roty ≈ −1.2: sin≈−0.93, cos≈0.36 → abst 0.8 → east +0.75, depth +0.29; use the
            // worst depth case: straight back-facing cos = −1 ⇒ z − 0.8, east toward the bulge)
            var npos = new Vector3(tx * 0.125f, rowY, gnomeZ - 0.8f);
            TestContext.WriteLine($"step: low hA={hLow}@x={gnomeX:0.##} high hA={hHigh}@x={tx * 0.125f:0.##} row y={rowY:0.##} gnomeZ={gnomeZ} npos.z={npos.z}");

            var seat = V(interp.Eval($"vector_fix {{{RS(npos.x)} {RS(npos.y)} {RS(npos.z)}}}"));
            int sxr = Mathf.RoundToInt(seat.x * 8f), syr = Mathf.RoundToInt(seat.y * 8f);
            float standAtSeat = nav.GetHmapSub(sxr, syr - 1) + 1f;
            TestContext.WriteLine($"seat={seat} standing-depth@seat={standAtSeat}");
            Assert.GreaterOrEqual(seat.z, standAtSeat - 0.001f,
                "the drop seat sits below the relief line of its own column — the item renders " +
                "inside the ground (r13 D: 'rings/panniers sink into the ground')");
        }
    }
}
