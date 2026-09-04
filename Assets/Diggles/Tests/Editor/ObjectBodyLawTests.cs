#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // The OBJECT-BODY law cluster:
    //
    // • The native engine NEVER blocks walking through object bodies: set_collision /
    // rec+0xb8 gate PLACEMENT only (FUN_005e7000's sole caller region is the placement
    // tick FUN_0046b450; Calc1 FUN_0059d640 gates on tile+0x06 alone, patched only by
    // set_pf_influence — doors/story barriers, FUN_0068f430 → FUN_005a6890). The gnome-vs-
    // body law is REACTIVE and lives in Tcl: check_ghost_coll (FUN_0067b090, the order-ghost
    // registry query) feeds walk_out_of / send_gnomes_outofplacement / prod_buildup_check
    // (z_dignwalk.tcl:1049-1074, z_work_common.tcl:345-401).
    // • Placement validity = settle + depth budget + THREE native tests: FUN_005e76a0
    // (terrain fit: 12-tile border DAT_00737540, no-embed probes, base-corner boundary law
    // ±1.0 DAT_00737138), FUN_005e7000 (AABB vs collidable bodies — the baked CMesh bbox
    // +0x44..0x58 ×0.005/0.005/0.01), FUN_005e7400 (AABB vs standing order ghosts).
    // • The pick body is the CURRENT display mesh's baked bbox (OBB in the model transform) —
    // NativePickBody keeps the BoxCollider synced to the live sharedMesh.
    // • has_cmap (FUN_00659280: frame mesh+0x00 cmap index != -1) is the collision DEFAULT
    // for every dummy class (animinit.tcl:249-251/:327).
    public sealed class ObjectBodyLawTests
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

        // invariant-culture float parse — the engine emits dot decimals regardless of locale
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        DigglesTclEntity ConfirmedOrder(TclInterpreter interp, DigglesTclContext ctx, string spot)
        {
            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.OrderBuild(crate.Handle));
            var p = TclList.Parse(spot);
            pc.UpdateCursor(new Vector3(F(p[0]), F(p[1]), F(p[2])));
            Assert.IsTrue(pc.Valid, "the order spot must validate");
            Assert.IsTrue(pc.Confirm());
            return crate;
        }

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

        // ------------------------------------------------------------------
        // 1. check_ghost_coll — the REACTIVE body law (FUN_0067b090)
        // ------------------------------------------------------------------

        [Test]
        public void CheckGhostColl_Bbox_ReturnsTheStandingGhostBody()
        {
            var (interp, ctx) = Host();
            var crate = ConfirmedOrder(interp, ctx, "28 20 8");

            // no ghost → the native no-record "0" (the Tcl `!= 0` guard, z_dignwalk.tcl:1050)
            var other = Spawn(interp, ctx, "B", "40 20 8");
            Assert.AreEqual("0", interp.Eval($"check_ghost_coll bbox {other.Handle} {other.Handle}").Trim());

            string bbox = interp.Eval($"check_ghost_coll bbox {crate.Handle} {crate.Handle}").Trim();
            Assert.AreNotEqual("0", bbox, "a standing order ghost must yield its body bbox");
            var corners = TclList.Parse(bbox);
            Assert.AreEqual(2, corners.Count, "bbox = {min} {max} — walk_out_of lindex 0/1");
            var mn = TclList.Parse(corners[0]);
            var mx = TclList.Parse(corners[1]);
            Assert.AreEqual(3, mn.Count);
            Assert.AreEqual(3, mx.Count);
            // the box is anchored at the ghost pos (28 20 8) and CONTAINS it
            Assert.Less(F(mn[0]), 28f); Assert.Greater(F(mx[0]), 28f);
            Assert.LessOrEqual(F(mn[1]), 20f); Assert.GreaterOrEqual(F(mx[1]), 20f);
            Assert.Less(F(mn[2]), 8f); Assert.Greater(F(mx[2]), 8f);

            // vector_inbox agrees — the exact walk_out_of predicate (z_dignwalk.tcl:1055)
            Assert.AreEqual("1", interp.Eval(
                $"vector_inbox {{28 20 8}} {{{corners[0]}}} {{{corners[1]}}}").Trim());
        }

        [Test]
        public void CheckGhostColl_Gnomes_ListsBodiesInside_ExcludesTheSubject()
        {
            var (interp, ctx) = Host();
            var crate = ConfirmedOrder(interp, ctx, "28 20 8");
            var inside = Spawn(interp, ctx, "G", "28.2 20 8");
            var outside = Spawn(interp, ctx, "G", "35 20 8");

            string list = interp.Eval($"check_ghost_coll gnomes {outside.Handle} {crate.Handle}").Trim();
            Assert.AreNotEqual("0", list, "a gnome inside the ghost body must be reported");
            var refs = TclList.Parse(list);
            CollectionAssert.Contains(refs, inside.Handle.ToString());
            CollectionAssert.DoesNotContain(refs, outside.Handle.ToString(), "outside gnome not listed");

            // the SUBJECT is excluded — `check_ghost_coll gnomes this $item` never lists this
            Assert.AreEqual("0", interp.Eval($"check_ghost_coll gnomes {inside.Handle} {crate.Handle}").Trim(),
                "the asking gnome itself is not shooed (z_work_common.tcl:374 passes `this`)");
        }

        [Test]
        public void CheckGhostColl_Objects_IsTheBuildupRecheck()
        {
            var (interp, ctx) = Host();
            var crate = ConfirmedOrder(interp, ctx, "28 20 8");
            Assert.AreEqual("0", interp.Eval($"check_ghost_coll objects {crate.Handle} {crate.Handle}").Trim(),
                "clear ghost spot → no collision (prod_buildup_check proceeds)");

            var blocker = Spawn(interp, ctx, "B", "28.3 20 8");   // kind production ⇒ collidable
            blocker.Owner = 0;
            Assert.AreEqual("1", interp.Eval($"check_ghost_coll objects {crate.Handle} {crate.Handle}").Trim(),
                "a collidable body on the ghost spot → collision (prod_buildup_check cancels, z_work_common.tcl:347)");
        }

        // ------------------------------------------------------------------
        // 2. ORDER-GHOST overlap — FUN_005e7400 (no stacking pending orders)
        // ------------------------------------------------------------------

        [Test]
        public void Placement_RejectsASpotOnAStandingOrderGhost()
        {
            var (interp, ctx) = Host();
            var first = ConfirmedOrder(interp, ctx, "28 20 8");
            Assert.IsTrue(ctx.Placement!.OrderGhosts.ContainsKey(first.Handle));

            var second = Spawn(interp, ctx, "B", "20.5 22 8");
            second.Owner = 0;
            interp.Eval($"set_boxed {second.Handle} 1");
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.OrderBuild(second.Handle));

            pc.UpdateCursor(new Vector3(28f, 20f, 8f));
            Assert.IsFalse(pc.Valid, "a pending order ghost vetoes the spot (FUN_005e7400)");
            pc.UpdateCursor(new Vector3(35f, 20f, 8f));
            Assert.IsTrue(pc.Valid, "clear ground away from the ghost stays valid");

            // the crate's OWN standing ghost never vetoes its own re-placement (the param_4
            // exclusion — the re-click order flow rewrites the record, FUN_005ee250)
            Assert.IsTrue(pc.Confirm());
            Assert.IsTrue(pc.OrderBuild(second.Handle));
            pc.UpdateCursor(new Vector3(35f, 20f, 8f));
            Assert.IsTrue(pc.Valid, "own ghost is the FUN_005e7400 param_4 exclusion");
        }

        // ------------------------------------------------------------------
        // 3. TERRAIN FIT — FUN_005e76a0's base-corner boundary law
        // ------------------------------------------------------------------

        [Test]
        public void Placement_RejectsAFootprintHangingOverAShaft()
        {
            var (interp, ctx) = Host();
            ctx.LocalPlayer = 0;
            // the synthetic meadow stripe (rows 21..22 solid) with an open SHAFT at x=30..31
            var solid = new List<(int, int)>();
            for (int x = 16; x <= 48; x++)
                if (x < 30 || x > 31) { solid.Add((x, 21)); solid.Add((x, 22)); }
            ctx.Terrain = MakeOpenTerrain(64, 64, solid.ToArray());

            var crate = Spawn(interp, ctx, "B", "20.5 20 8");
            crate.Owner = 0;
            interp.Eval($"set_boxed {crate.Handle} 1");
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.OrderBuild(crate.Handle));

            // wholly on the stripe → the corner sits at the rock/air boundary → valid
            pc.UpdateCursor(new Vector3(24.2f, 20f, 8f));     // snap → 24.5; corners 23.5/25.5
            Assert.IsTrue(pc.Valid, "a flat stripe spot is valid (boundary corners escape)");

            // east corner over the shaft → corner + agreeing neighbourhood deep in AIR
            // (h ≤ z−1.0, FUN_005e76a0's air branch) → invalid
            pc.UpdateCursor(new Vector3(29.2f, 20f, 8f));     // snap → 29.5; corner 30.5 = shaft
            Assert.IsFalse(pc.Valid, "a footprint corner hanging over a shaft fails the corner law");

            // the 12-tile border margin (DAT_00737540)
            pc.UpdateCursor(new Vector3(24.5f, 20f, 8f));
            Assert.IsTrue(pc.Valid);
        }

        // ------------------------------------------------------------------
        // 4. PICK BODY — the collider tracks the CURRENT display mesh
        // ------------------------------------------------------------------

        [Test]
        public void NativePickBody_SyncsTheBoxToTheLiveMesh()
        {
            var go = new GameObject("Model");
            _gos.Add(go);
            var mf = go.AddComponent<MeshFilter>();
            Mesh Box(float sx, float sy, float sz)
            {
                var m = new Mesh
                {
                    vertices = new[]
                    {
                        new Vector3(-sx, -sy, -sz), new Vector3(sx, sy, sz),
                        new Vector3(sx, -sy, sz),
                    },
                    triangles = new[] { 0, 1, 2 },
                };
                m.RecalculateBounds();
                return m;
            }
            var small = Box(1f, 2f, 3f);
            var big = Box(10f, 20f, 30f);
            mf.sharedMesh = small;
            var bc = go.AddComponent<BoxCollider>();
            bc.center = small.bounds.center;
            bc.size = small.bounds.size;
            var sync = go.AddComponent<NativePickBody>();
            sync.Bind(mf, bc);

            // the unpack/set_boxed model swap: the pick body must follow the displayed mesh
            mf.sharedMesh = big;
            sync.SyncNow();
            Assert.AreEqual(big.bounds.size, bc.size, "the box resizes to the swapped mesh");
            Assert.AreEqual(big.bounds.center, bc.center);

            Object.DestroyImmediate(small);
            Object.DestroyImmediate(big);
        }

        // ------------------------------------------------------------------
        // 4b. PICK TOLERANCE — the near-miss ring rescues thin silhouettes
        // (user UX contract the exact triangle law made gnome
        // limbs/small pickups fiddly; a few screen px of forgiveness for
        // characters+pickups, buildings stay exact-only)
        // ------------------------------------------------------------------

        [Test]
        public void PickTolerance_NearMissRescued_FarMissStaysMiss()
        {
            var camGo = new GameObject("cam");
            _gos.Add(camGo);
            var cam = camGo.AddComponent<Camera>();
            camGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); // looking +Z
            // Deterministic pixel space in batchmode (pixelRect otherwise depends on the
            // game-view size, which headless runs don't have).
            var rt = new RenderTexture(1024, 768, 0);
            cam.targetTexture = rt;

            // A thin vertical "limb": 0.1 wu wide quad at z=10, inside a fat click box.
            var go = new GameObject("limb");
            _gos.Add(go);
            go.transform.position = new Vector3(0f, 0f, 10f);
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(-0.05f, -1f, 0f), new Vector3(0.05f, -1f, 0f),
                    new Vector3(0.05f, 1f, 0f), new Vector3(-0.05f, 1f, 0f),
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            mesh.RecalculateBounds();
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var bc = go.AddComponent<BoxCollider>();
            bc.size = new Vector3(1f, 2.5f, 0.1f);
            Physics.SyncTransforms();

            // Aim ~2 px OUTSIDE the silhouette's right edge (still well inside the box).
            var screenEdge = cam.WorldToScreenPoint(new Vector3(0.05f, 0f, 10f));
            var nearMiss = screenEdge + new Vector3(2f, 0f, 0f);
            var ray = cam.ScreenPointToRay(nearMiss);
            Assert.IsTrue(Physics.Raycast(ray, out var boxHit, 100f), "the fat box catches the near-miss ray");

            // Exact law rejects (this IS the crate-vs-building guarantee)...
            Assert.IsFalse(ClickToMoveService.TryRefineMeshHit(ray, boxHit, out _),
                "2 px off the silhouette = exact miss");
            //...the tolerance ring rescues, at roughly the model's distance.
            Assert.IsTrue(ClickToMoveService.TryRefineMeshHitTolerant(cam, nearMiss, boxHit, out var dist),
                "the near-miss ring must rescue the thin silhouette");
            Assert.AreEqual(10f, dist, 0.5f);

            // A clearly-far miss (4× the ring radius) stays a miss even when the box catches it.
            var farMiss = screenEdge + new Vector3(4f * ClickToMoveService.PickTolerancePx, 0f, 0f);
            var farRay = cam.ScreenPointToRay(farMiss);
            if (Physics.Raycast(farRay, out var farBoxHit, 100f))
            {
                Assert.IsFalse(ClickToMoveService.TryRefineMeshHit(farRay, farBoxHit, out _));
                Assert.IsFalse(ClickToMoveService.TryRefineMeshHitTolerant(cam, farMiss, farBoxHit, out _),
                    "a miss beyond the ring must NOT be rescued");
            }
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(mesh);
        }

        // ------------------------------------------------------------------
        // 5. has_cmap — the collision default (real data)
        // ------------------------------------------------------------------

        [Test]
        public void HasCmap_ReadsTheRealMeshCmapIndex()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/3db/produktionsstaetten.3db"),
                "original data dir not present — has_cmap data test skipped");
            Assume.That(System.IO.File.Exists(
                System.IO.Path.Combine(Application.streamingAssetsPath, "obj_db_map.tsv")),
                "StreamingAssets obj map not present — has_cmap data test skipped");

            var (interp, ctx) = Host();
            ctx.ModelLibrary = new DigglesModelLibrary(DataRoot);

            // built building = cmap (collidable); its bau_a stage mesh = none (ground truth
            // read straight from produktionsstaetten.3db,)
            Assert.AreEqual("1", interp.Eval("has_cmap feuerstelle.standard").Trim());
            Assert.AreEqual("0", interp.Eval("has_cmap feuerstelle.bau_a").Trim());
            // deco mushrooms: small = walk-through (no cmap), big = collidable — the exact
            // native split animinit.tcl bakes into class_collision
            Assert.AreEqual("0", interp.Eval("has_cmap pilze_00_a.standard").Trim());
            Assert.AreEqual("1", interp.Eval("has_cmap pilze_04_a.standard").Trim());
            // the kiste box carries a cmap too
            Assert.AreEqual("1", interp.Eval("has_cmap kiste.standard").Trim());

            // the placement/pick bbox surface reads the same baked CMesh record (+0x44..0x58)
            Assert.IsTrue(ctx.ModelLibrary.TryGetMeshBBox("feuerstelle", "standard", 0,
                out var mn, out var mx));
            Assert.AreEqual(-241.4f, mn.x, 0.1f);
            Assert.AreEqual(-154.7f, mn.y, 0.1f);
            Assert.AreEqual(241.3f, mx.x, 0.1f);
            Assert.AreEqual(1.5f, mx.y, 0.1f);
            Assert.AreEqual(241.3f, mx.z, 0.1f);
        }
    }
}
