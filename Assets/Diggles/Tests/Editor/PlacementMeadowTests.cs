#nullable enable

namespace Diggles.Tests
{
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // REAL-terrain placement verification on the start meadow (urw_unq_start @ {208 0}) — the
    // manual test triad, now ASSERTED against the mode-2 RE (FUN_0046b450:795-920;
    // snapmode 0 = every production crate, genericprod.tcl:450):
    // 1. NO X-STRIPING: every flat-meadow pick (x 239..250.5, all half-tile columns) gives the
    // same law verdict — BUILDING wherever pickZ + extent < 13, regardless of x;
    // 2. BOX ONLY ON THE FRONT BAND: z ≥ 13 − extent ⇒ the kiste branch (the player's Steam law
    //; the front cross-section (z = 15) never seats;
    // 3. THE CRATE SETTLES: set_pos/set_boxed land the campfire crate ON the meadow floor line
    // (y = 30.5) — including the intro-cutscene order (summon → set_pos UNBOXED → packtobox,
    // urw_3.tcl:29) and a sky-high drop (the regression).
    // Builds the REAL meadow through the full DigglesEngine graph and raycasts the terrain like
    // ClickToMoveService.TryPickTerrain does.
    [Explicit("Slow: loads real data + BDB, builds the start-meadow terrain; run on demand")]
    public sealed class PlacementMeadowTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [Test]
        public void Meadow_Mode2Placement_NoStriping_FrontBandBox_CrateSettles()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/BlockDB2.BDB"), "original data dir not present");

            // ---- the real meadow world through the full engine graph ----
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            var nav = terr!.Nav;
            Assert.IsNotNull(nav, "no NavWorld");
            var ctx = engine.Ctx;
            ctx.Terrain = terr;
            ctx.LocalPlayer = 0;
            if (ctx.EntitySpawner is RuntimeWorldSpawner rws) rws.Terrain = terr;
            var root = rootGo.transform;
            Physics.SyncTransforms();

            Vector3 W(Vector3 native) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native));
            Vector3 N(Vector3 world) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(world));

            // ---- terrain ground truth (diagnostic print) ----
            TestContext.WriteLine("[A] SubIsFloor line rows per half-tile column, x 239..251:");
            for (float x = 239f; x <= 251f; x += 0.5f)
            {
                int subX = Mathf.RoundToInt(x * 8f);
                var rows = new StringBuilder();
                for (int sy = 232; sy <= 256; sy++)
                    if (nav!.SubIsFloor(subX, sy)) rows.Append($"{sy} ");
                TestContext.WriteLine($"  x={x,6:0.0} (subX={subX}): floor subY = {rows}");
            }

            // ---- the crate + placement mode (the Play BUILD stand-in path) ----
            // The minimal test class bypasses genericprod.tcl's object initializer. Real
            // Feuerstelle instances execute `set_physic this 1` there (:446), which is required
            // by the native FUN_005eb470 point-physics enqueue gate.
            engine.Interp.Eval("def_class Feuerstelle none production 0 {} {}");
            string h = engine.Interp.Eval("new Feuerstelle \"\" \"250 28 8\" \"0 0 0\"").Trim();
            var crate = ctx.GetEntity(h);
            Assert.IsNotNull(crate, "crate entity not created");
            crate!.Owner = 0;
            engine.Interp.Eval($"set_physic {h} 1");
            engine.Interp.Eval($"set_boxed {h} 1");
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.OrderBuild(crate.Handle), "OrderBuild must enter placement mode");
            float ext = pc.BuildingDepthExtent;
            TestContext.WriteLine($"\n[B] class depth extent = {ext:0.00} slabs (budget 13.0) — " +
                                  $"box band starts at z = {13f - ext:0.0}");

            // ---- [C] the flat-meadow sweep: NO striping; the branch follows ONLY the depth law ----
            // x stops at 248.5: the native x-snap (ROUND(2x+0.5)·0.5) moves a pick up to +0.5
            // east, the footprint spans ±1.2 tiles beyond that, and columns ≥ 251 are the
            // meadow's EASTERN SLOPE — the base-corner boundary law (FUN_005e76a0) faithfully
            // rejects footprints whose corner overhangs the slope (x=249+ picks), which is
            // strictness, not striping.
            int picks = 0, buildings = 0, boxes = 0;
            var failures = new StringBuilder();
            for (float x = 239f; x <= 248.5f; x += 0.5f)
                foreach (float z in new[] { 2f, 5f, 8f, 11f, 13f, 14f })
                {
                    var origin = W(new Vector3(x, 26f, z));   // native y=26 ≈ 4.5 tiles above the line
                    if (!PickTerrain(new Ray(origin, Vector3.down), out var hitPoint))
                    { failures.AppendLine($"  x={x} z={z}: NO TERRAIN HIT"); continue; }
                    Vector3 nat = N(hitPoint);
                    pc.UpdateCursor(nat);
                    picks++;

                    // the mode-2 expectation from the pick alone (FUN_0046b450:828-829)
                    float pickZ = Mathf.Round(nat.z);
                    bool expectBox = pickZ + ext >= PlacementController.BuildingDepthBudget;
                    // The base-corner boundary law (FUN_005e76a0, ported 3ef6d52): the footprint
                    // extends ~ext slabs BEHIND the anchor, and a back corner buried ≥1 slab in
                    // the meadow's back rock rejects — picks that deep are faithfully INVALID
                    // (the old sweep pinned the permissive pre-corner-law behaviour at z=2).
                    bool expectBackReject = !expectBox && (pickZ - ext) < 0.6f;
                    if (!pc.Valid)
                    {
                        if (!expectBackReject)
                            failures.AppendLine($"  x={x} z={z}: INVALID (pick {nat}) ghost={pc.GhostPos}");
                        continue;
                    }
                    if (expectBackReject)
                    { failures.AppendLine($"  x={x} z={z}: VALID but the back corner embeds (pickZ={pickZ}, ext={ext:0.00})"); continue; }
                    if (pc.AsBox != expectBox)
                        failures.AppendLine($"  x={x} z={z}: AsBox={pc.AsBox} expected={expectBox} (pickZ={pickZ})");
                    if (pc.Valid && pc.AsBox) boxes++;
                    if (pc.Valid && !pc.AsBox) buildings++;
                    // the ghost keeps the picked depth and settles the vertical onto the line row
                    if (pc.Valid)
                    {
                        if (Mathf.Abs(pc.GhostPos.z - pickZ) > 1e-3f)
                            failures.AppendLine($"  x={x} z={z}: ghost z {pc.GhostPos.z} != pick z {pickZ}");
                        if (Mathf.Abs(pc.GhostPos.y - 30.5f) > 1e-3f)
                            failures.AppendLine($"  x={x} z={z}: ghost y {pc.GhostPos.y} != the meadow line 30.5");
                    }
                }
            TestContext.WriteLine($"[C] sweep: picks={picks} buildings={buildings} boxes={boxes}");
            Assert.IsTrue(failures.Length == 0, "mode-2 law violations:\n" + failures);
            Assert.Greater(picks, 100, "sweep must cover the flat meadow");
            Assert.Greater(buildings, 0, "the deep meadow must take buildings");
            Assert.Greater(boxes, 0, "the front band must exist (z=13/14 picks)");

            // ---- [D] the front cross-section (z = 15) never seats: the settle exhausts ----
            pc.UpdateCursor(new Vector3(245f, 30.5f, 15f));
            Assert.IsFalse(pc.Valid, "z=15 (the ant-farm front glass) must not validate (FUN_0046b190 exhaustion)");

            // ---- [E] crate settle pins (symptom 1) ----
            // E1: the SandboxUrw order (set_boxed FIRST, then set_pos — SandboxUrw.tcl:25-28):
            // the set_pos settle hook lands the crate ON the line.
            string h1 = engine.Interp.Eval("new Feuerstelle \"\" \"0 0 0\" \"0 0 0\"").Trim();
            var fs1 = ctx.GetEntity(h1)!;
            engine.Interp.Eval($"set_owner {h1} 0");
            engine.Interp.Eval($"set_physic {h1} 1");
            engine.Interp.Eval($"set_boxed {h1} 1");
            engine.Interp.Eval($"set_pos {h1} {{246.8 30.5 9.8}}");
            AssertOnMeadowFloor("E1 SandboxUrw order", fs1);

            // E2: the INTRO-CUTSCENE order (urw_3.tcl:29 — summon → set_pos while UNBOXED
            // at DropPos, y half a unit ABOVE the line, then packtobox): boxing settles the
            // LOGICAL position onto the line (the transform-only SnapToGround left it floating —
            // the regression pin).
            string h2 = engine.Interp.Eval("new Feuerstelle \"\" \"0 0 0\" \"0 0 0\"").Trim();
            var fs2 = ctx.GetEntity(h2)!;
            engine.Interp.Eval($"set_owner {h2} 0");
            engine.Interp.Eval($"set_physic {h2} 1");
            engine.Interp.Eval($"set_pos {h2} {{246.84 30.0 9.82}}");
            Assert.AreEqual(30.0f, fs2.Position.y, 1e-3f, "an unboxed set_pos must NOT settle");
            int e2SubX = Mathf.RoundToInt(fs2.Position.x * 8f);
            int e2ProbeRow = Mathf.RoundToInt(fs2.Position.y * 8f) + 1;
            NavWorld e2Nav = nav!;
            Assert.AreEqual(1975, e2SubX, "E2 must pin the reported meadow column");
            Assert.LessOrEqual(e2Nav.GetHmapSub(e2SubX, e2ProbeRow), fs2.Position.z,
                "the native ROUND(y*8)+1 support probe begins in air");
            int e2SupportRow = e2ProbeRow;
            while (e2SupportRow < e2Nav.SubH
                && e2Nav.GetHmapSub(e2SubX, e2SupportRow) <= fs2.Position.z)
                e2SupportRow++;
            Assert.AreEqual(244, e2SupportRow, "the E2 column's first support is the y=30.5 row");
            engine.Interp.Eval($"set_boxed {h2} 1");
            AssertOnMeadowFloor("E2 cutscene order (summon→set_pos→packtobox)", fs2);

            // E3: a SKY-HIGH drop (native y 14 = 16.5 units above the meadow): the boxing settle
            // falls the whole column to the support row — must land.
            string h3 = engine.Interp.Eval("new Feuerstelle \"\" \"0 0 0\" \"0 0 0\"").Trim();
            var fs3 = ctx.GetEntity(h3)!;
            engine.Interp.Eval($"set_owner {h3} 0");
            engine.Interp.Eval($"set_physic {h3} 1");
            engine.Interp.Eval($"set_pos {h3} {{246.8 14 9.8}}");
            engine.Interp.Eval($"set_boxed {h3} 1");
            AssertOnMeadowFloor("E3 sky drop", fs3);

            Object.DestroyImmediate(rootGo);

            void AssertOnMeadowFloor(string label, DigglesTclEntity e)
            {
                TestContext.WriteLine($"  [{label}] native=({e.Position.x:0.00},{e.Position.y:0.00},{e.Position.z:0.00})");
                Assert.AreEqual(30.5f, e.Position.y, 1e-3f, $"{label}: the crate must rest on the meadow line (y=30.5)");
                Assert.AreEqual(9.8f, e.Position.z, 0.11f, $"{label}: the depth stays the ordered one");
                if (e.GameObject != null)
                {
                    Vector3 natGo = N(e.GameObject.transform.position);
                    Assert.AreEqual(30.5f, natGo.y, 1e-2f, $"{label}: the RENDER transform must match the settled position");
                }
            }
        }

        // ================= PLAY-PARITY repro (regression) =================
        // After 3ef6d52 the player's Play ghost was RED across the whole meadow while this suite
        // stayed green — the two Play deltas the sweep above lacks are (a) the REAL class corpus
        // (animinit's has_cmap → class_collision on big plants) and (b) the REAL deco population
        // (the split-template `new Dummy_*` spawns). This test builds the meadow with BOTH, then
        // sweeps: buildings must be VALID on open grass and INVALID only for cause (a standing
        // collidable body / the slope / the back wall) — the player's Steam law (
        [Test]
        public void Meadow_PlayParity_RealClassesAndDeco_OpenGrassTakesBuildings()
        {
            Assume.That(System.IO.File.Exists(DataRoot + "/BlockDB2.BDB"), "original data dir not present");

            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            // Play delta (a): the real class corpus BEFORE the template, so the deco spawns as
            // REAL classes with the has_cmap collision defaults (animinit.tcl:249-251/:327).
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { engine.Interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            Assert.IsTrue(engine.Ctx.Classes.ContainsKey("Zwerg"), "real class corpus did not load");
            // Play delta (b): the template now spawns its deco through those classes.
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[call] {e.Message}"); }
            engine.Map.EmitTerrain(rootGo);
            var terr = rootGo.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
            var ctx = engine.Ctx;
            ctx.Terrain = terr;
            ctx.LocalPlayer = 0;
            if (ctx.EntitySpawner is RuntimeWorldSpawner rws) rws.Terrain = terr;
            var root = rootGo.transform;
            Physics.SyncTransforms();
            Vector3 W(Vector3 native) => root.TransformPoint(RuntimeWorldSpawner.NativeToLocal(native));
            Vector3 N(Vector3 world) => RuntimeWorldSpawner.LocalToNative(root.InverseTransformPoint(world));

            // ---- [F] the collidable-body census on the meadow (what the overlap gate sees) ----
            TestContext.WriteLine("[F] entities on the meadow band x 230..260 (collision gate inputs):");
            foreach (var e in ctx.Entities.Values)
            {
                if (e.Class == null || e.ContainedIn >= 0) continue;
                if (e.Position.x < 230f || e.Position.x > 260f || e.Position.y > 34f) continue;
                string col = e.VarsScope.Vars.TryGetValue("_collision", out var cv) ? $"var:{cv}"
                    : !string.IsNullOrEmpty(e.Class.Collision) ? $"cls:{e.Class.Collision}" : "-";
                string bb = "";
                var da = e.Class.DefaultAnim;
                if (!string.IsNullOrEmpty(da) && da!.IndexOf('.') > 0 && ctx.ModelLibrary != null)
                {
                    int dot = da.IndexOf('.');
                    if (ctx.ModelLibrary.TryGetMeshBBox(da.Substring(0, dot), da.Substring(dot + 1), 0,
                            out var mn, out var mx))
                        bb = $" bb=({mn.x:0},{mn.y:0},{mn.z:0})..({mx.x:0},{mx.y:0},{mx.z:0})";
                }
                TestContext.WriteLine($"  {e.Class.Name}#{e.Handle} kind={e.Class.Kind} col={col} " +
                    $"@({e.Position.x:0.##},{e.Position.y:0.##},{e.Position.z:0.##}) anim={da}{bb}");
            }

            // ---- the crate through the REAL Feuerstelle class ----
            string h = engine.Interp.Eval("new Feuerstelle \"\" \"250 28 8\" \"0 0 0\"").Trim();
            var crate = ctx.GetEntity(h);
            Assert.IsNotNull(crate, "crate entity not created");
            crate!.Owner = 0;
            engine.Interp.Eval($"set_boxed {h} 1");
            var pc = ctx.Placement!;
            Assert.IsTrue(pc.OrderBuild(crate.Handle), "OrderBuild must enter placement mode");
            float ext = pc.BuildingDepthExtent;
            TestContext.WriteLine($"\n[G] real-class depth extent = {ext:0.00} slabs");

            // ---- [H] the sweep with the reject census ----
            int picks = 0, buildings = 0, boxes = 0, rejects = 0;
            var rejectCensus = new System.Collections.Generic.Dictionary<string, int>();
            var rejectSamples = new StringBuilder();
            for (float x = 239f; x <= 248.5f; x += 0.5f)
                foreach (float z in new[] { 4f, 6f, 8f, 10f })
                {
                    var origin = W(new Vector3(x, 26f, z));
                    if (!PickTerrain(new Ray(origin, Vector3.down), out var hitPoint)) continue;
                    pc.UpdateCursor(N(hitPoint));
                    picks++;
                    if (pc.Valid && !pc.AsBox) buildings++;
                    else if (pc.Valid) boxes++;
                    else
                    {
                        rejects++;
                        string key = pc.LastReject ?? "?";
                        int cut = key.IndexOf('@');            // aggregate by gate+subject
                        if (cut > 0) key = key.Substring(0, cut).TrimEnd();
                        rejectCensus[key] = rejectCensus.TryGetValue(key, out var n) ? n + 1 : 1;
                        if (rejectSamples.Length < 4000)
                            rejectSamples.AppendLine($"  x={x} z={z}: {pc.LastReject}");
                    }
                }
            TestContext.WriteLine($"[H] sweep: picks={picks} buildings={buildings} boxes={boxes} rejects={rejects}");
            foreach (var kv in rejectCensus) TestContext.WriteLine($"  reject[{kv.Value}] {kv.Key}");
            TestContext.WriteLine(rejectSamples.ToString());

            Assert.Greater(picks, 60, "sweep must cover the meadow");
            // CONTRACT CHANGE (was: buildings > picks/2): the sweep band is dotted
            // with STANDING collidable bodies that the native placement genuinely vetoes —
            // growing Pilz plants (`set_collision this 1`, pilz.tcl:42) and Schatztonne barrels
            // (`set_collision this 1`, misc_items.tcl obj_init) both set rec+0xb8, and
            // FUN_005e7000:73-77 rejects every candidate overlapping such a body. Blocking on
            // them is the B8 fix, so a
            // >50% ratio over THIS band is not the law. The law that must hold: OPEN GRASS still
            // takes buildings (a healthy absolute count), and every reject is FOR CAUSE (a named
            // body / terrain gate) — never an anonymous red.
            Assert.Greater(buildings, 15,
                "open meadow must still accept buildings on open grass (regression: red everywhere)\n" + rejectSamples);
            foreach (var kv in rejectCensus)
                Assert.IsTrue(kv.Key.StartsWith("bld body") || kv.Key.StartsWith("box body")
                    || kv.Key.StartsWith("bld body-r") || kv.Key.StartsWith("box body-r")
                    || kv.Key.Contains("terrain") || kv.Key.Contains("settle")
                    || kv.Key.Contains("order-ghost"),
                    $"unattributed reject class: '{kv.Key}'");

            // ---- [I] a pick ON a standing collidable body must stay INVALID (the faithful veto):
            // Dummy_Pilze_04_b — the big mushroom at x≈243.63 (splt_urw_unq_start_tCB.tcl), a
            // cmap-carrying mesh (ObjectBodyLawTests pins pilze_04 = cmap ⇒ class_collision 1).
            var pilz = System.Linq.Enumerable.FirstOrDefault(ctx.Entities.Values,
                en => en.Class?.Name == "Dummy_Pilze_04_b");
            if (pilz != null)
            {
                pc.UpdateCursor(new Vector3(pilz.Position.x, pilz.Position.y, pilz.Position.z));
                Assert.IsFalse(pc.Valid && !pc.AsBox,
                    $"a building on top of the big mushroom must reject (got valid, reject={pc.LastReject})");
                TestContext.WriteLine($"[I] on-mushroom pick reject = {pc.LastReject}");
            }
            else TestContext.WriteLine("[I] Dummy_Pilze_04_b not spawned — body-veto pin skipped");

            Object.DestroyImmediate(rootGo);
        }

        // ClickToMoveService.TryPickTerrain replica (terrain-only pick: MeshCollider + DiggableTerrain parent).
        static bool PickTerrain(Ray ray, out Vector3 point)
        {
            point = default;
            var hits = Physics.RaycastAll(ray, 10000f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var hh in hits)
            {
                if (hh.distance >= best) continue;
                if (!(hh.collider is MeshCollider)) continue;
                if (hh.collider.GetComponentInParent<DiggableTerrain>() == null) continue;
                best = hh.distance;
                point = hh.point;
            }
            return best < float.MaxValue;
        }
    }
}
