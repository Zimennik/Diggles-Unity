#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Hard rectangular black edges at the fog boundary in tunnels/caves.
    //
    // Root cause: the black→revealed gradient is drawn by the terrain shader (fow.g bilinear
    // multiply), so it needs a terrain surface to land on. Tiles without renderable terrain —
    // load-time block-59 cave air ("open air above the bowl" skip in BuildTileBuckets:1187),
    // oob blocks, tiles outside the render bbox — have no gradient carrier. When such a tile
    // sits at the black boundary (mixed fog corners: not AllBlack, so the cover retreats, but
    // not fully revealed either) the overlay used to emit nothing over it: the covers/walls of
    // the all-black tiles beyond showed through as razor-edged rectangles. The native never has
    // this hole: open tiles render the mat-21 "cave backdrop" plane, which at fog borders is
    // promoted 21→22 and alpha-fades per-vertex (the native background-gate byte) — the
    // gradient carrier over open space. The port has no backdrop, so FogOfWarOverlay emits
    // gradient patches (black quads at the tile fill depth, alpha = 1−fow.g in
    // Diggles/FowGradient) over meshless tiles that still have a black corner.
    public sealed class FogOfWarCaveBoundaryTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        [Test]
        public void RealCave_BlackBoundary_HasNoGradientHolesOverMeshlessTiles()
        {
            var rootGo = new GameObject("WorldRoot");
            rootGo.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            try
            {
                var engine = new DigglesEngine();
                engine.Build(DataRoot, rootGo.transform, "BlockDB2.BDB");
                engine.Interp.Eval("map create 512 640");
                engine.Interp.Eval("set temppos {208 0}");
                try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
                catch (System.Exception e) { TestContext.WriteLine($"[template] {e.Message}"); }
                // A real underground cave stamped at BOOT (the worldgen story-cave path:
                // map_template before EmitTerrain → BuildFromTclDriven blit, NO SyncStampedTile
                // — its block-59 open interior (108/240 tiles) stays un-dug ⇒ MESHLESS, unlike
                // runtime growth stamps whose 59s get DugFloor).
                engine.Interp.Eval("map_template caves_3.pmp 228 44");
                engine.Map.EmitTerrain(rootGo);
                var terr = rootGo.GetComponent<DiggableTerrain>();
                Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");
                var wr = engine.Map.WorldResult;
                Assert.IsNotNull(wr, "no WorldResult after EmitTerrain");

                // ---- survey: meshless (no-render) tiles inside the render bbox ----
                bool Renders(int tx, int ty)
                {
                    if (tx < wr!.RenderXMin || tx > wr.RenderXMax ||
                        ty < wr.RenderYMin || ty > wr.RenderYMax) return false;
                    int i = ty * wr.Width + tx;
                    if (wr.BlockIdx[i] == 59)
                        return wr.DugFloor != null && wr.DugFloor[i];
                    return true;
                }
                int airAbove = 0, airBelow = 0;   // rough sky/underground split at row 32
                var below = new List<(int x, int y)>();
                for (int ty = wr!.RenderYMin; ty <= wr.RenderYMax; ty++)
                    for (int tx = wr.RenderXMin; tx <= wr.RenderXMax; tx++)
                        if (!Renders(tx, ty))
                        {
                            if (ty < 32) airAbove++;
                            else { airBelow++; below.Add((tx, ty)); }
                        }
                TestContext.WriteLine($"render bbox X {wr.RenderXMin}..{wr.RenderXMax} " +
                                      $"Y {wr.RenderYMin}..{wr.RenderYMax}; meshless tiles: " +
                                      $"{airAbove} above row 32 (sky), {airBelow} below (caves)");
                Assert.Greater(airBelow, 0,
                    "expected load-time block-59 (meshless) tiles inside underground caves — " +
                    "if this fails the meshless-boundary hypothesis is wrong");

                // Pick a cave pocket: an underground meshless tile with >= 3 meshless
                // 8-neighbors (interior of an open cave volume, not a stray).
                (int cx, int cy) = (-1, -1);
                foreach (var (tx, ty) in below)
                {
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if ((dx | dy) != 0 && !Renders(tx + dx, ty + dy)) n++;
                    if (n >= 3) { cx = tx; cy = ty; break; }
                }
                Assert.GreaterOrEqual(cx, 0, "no cave pocket found");
                TestContext.WriteLine($"cave probe at ({cx},{cy})");

                // ---- fog: radial reveal INSIDE the cave (the dwarf-broke-in moment) ----
                var fog = new FogOfWarState(terr!.Width, terr.Height, terr.FogFillAt);
                fog.BeginUpdate(0);
                // Fine units (tile*8); radius 4 tiles — small enough that the black boundary
                // crosses the open cave air around the probe.
                fog.RemoveFog(cx * 8 + 4, cy * 8 + 4, 4 * 8, 4 * 8, 0, 0f, 1f);
                fog.EndUpdate();

                // ---- the law: every tile with a black corner must have a gradient carrier ----
                // • renders terrain → the ground shader draws the gradient;
                // • AllBlack → opaque cover (gradient is the neighbors' job);
                // • meshless + mixed corners → GRADIENT PATCH quad in the overlay.
                List<(int x, int y)> Holes(FogOfWarOverlay o, out int patched)
                {
                    var quads = Quads(o);
                    var list = new List<(int x, int y)>();
                    patched = 0;
                    for (int ty = cy - 10; ty <= cy + 10; ty++)
                    {
                        for (int tx = cx - 10; tx <= cx + 10; tx++)
                        {
                            bool b00 = fog.FogAt(tx, ty, 0) == 0, b10 = fog.FogAt(tx + 1, ty, 0) == 0;
                            bool b01 = fog.FogAt(tx, ty + 1, 0) == 0, b11 = fog.FogAt(tx + 1, ty + 1, 0) == 0;
                            bool anyBlack = b00 || b10 || b01 || b11;
                            bool allBlack = b00 && b10 && b01 && b11;
                            if (!anyBlack || allBlack || Renders(tx, ty)) continue;
                            // mixed-corner meshless tile: needs a horizontal overlay quad over it
                            if (HasHorizontalQuadOver(quads, tx, ty)) patched++;
                            else list.Add((tx, ty));
                        }
                    }
                    return list;
                }

                var ov = rootGo.AddComponent<FogOfWarOverlay>();

                // Root-cause proof — the pre-fix wiring (no rendersAt): the cover retreats off
                // the mixed-corner cave-air tiles and nothing is emitted over them, producing
                // the hard rectangular black-edge hole.
                ov.Init(fog, terr.GetHmap);
                ov.Pump();
                var legacyHoles = Holes(ov, out _);
                TestContext.WriteLine($"legacy wiring: {legacyHoles.Count} gradient holes " +
                                      $"{string.Join(" ", legacyHoles)}");
                Assert.Greater(legacyHoles.Count, 0,
                    "the pre-fix wiring must exhibit the meshless-boundary hole (bug repro)");

                // The fix: a rendersAt-aware overlay closes every hole with a gradient patch.
                ov.Init(fog, terr.GetHmap, terr.RendersTerrain);
                ov.Pump();

                // Cross-check the terrain's own predicate against the bucket-law mirror.
                for (int ty = cy - 8; ty <= cy + 8; ty++)
                    for (int tx = cx - 8; tx <= cx + 8; tx++)
                        Assert.AreEqual(Renders(tx, ty), terr.RendersTerrain(tx, ty),
                            $"RendersTerrain mismatch at ({tx},{ty})");

                var holes = Holes(ov, out int patches);
                TestContext.WriteLine($"fixed wiring: {patches} patched, {holes.Count} holes");
                DumpArea(fog, Renders, Quads(ov), cx - 10, cy - 10, cx + 10, cy + 10);
                Assert.Greater(patches, 0, "the boundary must actually cross meshless cave air");
                Assert.IsEmpty(holes,
                    "meshless black-boundary tiles without a gradient carrier (hard rectangular " +
                    $"fog edge): {string.Join(" ", holes)}");

                // ---- Patches follow the display field on real cave data (the stair-step
                // dissolve). Root cause: patches used to leave on raw AnyBlackCorner while the
                // presenter's smoothed display (= the patch shader's alpha, 1−fow.g of the same
                // texture) was still dark — the void behind a meshless cave tile popped in at
                // full contrast, tile by tile, at the raw reveal front. With the presenter
                // wired, a patch may leave only once all 4 corner display values reach 255
                // (alpha ≡ 0 — invisible).
                var pres = rootGo.AddComponent<FogOfWarPresenter>();
                pres.Init(fog);                                   // snap: reveal #1 displayed
                ov.Init(fog, terr.GetHmap, terr.RendersTerrain, pres);
                ov.Pump();
                // The gnome's next step: grow the reveal — raw bytes flip instantly…
                fog.BeginUpdate(0);
                fog.RemoveFog(cx * 8 + 4, cy * 8 + 4, 6 * 8, 6 * 8, 0, 0f, 1f);
                fog.EndUpdate();
                pres.Pump(0.1f);                                  // …display mid-chase (g ≈ 36)
                ov.Pump();
                var chaseQuads = Quads(ov);
                int chasing = 0;
                var naked = new List<(int x, int y)>();
                for (int ty = cy - 10; ty <= cy + 10; ty++)
                    for (int tx = cx - 10; tx <= cx + 10; tx++)
                    {
                        if (Renders(tx, ty)) continue;            // meshed: terrain shader fades
                        bool b00 = fog.FogAt(tx, ty, 0) == 0, b10 = fog.FogAt(tx + 1, ty, 0) == 0;
                        bool b01 = fog.FogAt(tx, ty + 1, 0) == 0, b11 = fog.FogAt(tx + 1, ty + 1, 0) == 0;
                        if (b00 && b10 && b01 && b11) continue;   // opaque cover closes it
                        bool caughtUp = pres.DisplayAt(tx, ty).g == 255
                                     && pres.DisplayAt(tx + 1, ty).g == 255
                                     && pres.DisplayAt(tx, ty + 1).g == 255
                                     && pres.DisplayAt(tx + 1, ty + 1).g == 255;
                        if (caughtUp) continue;                   // alpha ≡ 0 → no carrier needed
                        chasing++;
                        if (!HasHorizontalQuadOver(chaseQuads, tx, ty)) naked.Add((tx, ty));
                    }
                TestContext.WriteLine($"mid-chase meshless tiles: {chasing}, naked: {naked.Count}");
                Assert.Greater(chasing, 0, "the grown reveal must cross meshless cave air mid-chase");
                Assert.IsEmpty(naked,
                    "meshless mid-chase tiles whose patch left before the display caught up " +
                    $"(per-tile pop = the stair-step dissolve): {string.Join(" ", naked)}");
            }
            finally
            {
                Object.DestroyImmediate(rootGo);
            }
        }

        // ---- helpers ---------------------------------------------------------------------

        // All overlay geometry as quads (AddQuad emits vertices in groups of 4).
        static List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d)> Quads(FogOfWarOverlay ov)
        {
            var list = new List<(Vector3, Vector3, Vector3, Vector3)>();
            for (int cy = 0; cy < ov.ChunksY; cy++)
                for (int cx = 0; cx < ov.ChunksX; cx++)
                {
                    var m = ov.ChunkMesh(cx, cy);
                    if (m == null || m.vertexCount == 0) continue;
                    var v = m.vertices;
                    for (int i = 0; i + 3 < v.Length; i += 4)
                        list.Add((v[i], v[i + 1], v[i + 2], v[i + 3]));
                }
            return list;
        }

        // A horizontal (constant-y) quad whose rect contains the tile center — a front cover
        // or a gradient patch; either is a legitimate carrier over a meshless tile.
        static bool HasHorizontalQuadOver(
            List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d)> quads, int tx, int ty)
        {
            float px = tx + 0.5f, pz = -(ty + 0.5f);
            foreach (var (a, b, c, d) in quads)
            {
                if (Mathf.Abs(a.y - b.y) > 1e-3f || Mathf.Abs(a.y - c.y) > 1e-3f) continue;
                float x0 = Mathf.Min(a.x, Mathf.Min(b.x, Mathf.Min(c.x, d.x)));
                float x1 = Mathf.Max(a.x, Mathf.Max(b.x, Mathf.Max(c.x, d.x)));
                float z0 = Mathf.Min(a.z, Mathf.Min(b.z, Mathf.Min(c.z, d.z)));
                float z1 = Mathf.Max(a.z, Mathf.Max(b.z, Mathf.Max(c.z, d.z)));
                if (px >= x0 && px <= x1 && pz >= z0 && pz <= z1) return true;
            }
            return false;
        }

        static void DumpArea(FogOfWarState fog, System.Func<int, int, bool> renders,
                             List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d)> quads,
                             int x0, int y0, int x1, int y1)
        {
            var sb = new StringBuilder();
            sb.AppendLine("map: '#'=meshed-black '='=meshed-lit '.'=air-lit " +
                          "'C'=cover 'P'=patched-air 'H'=HOLE(air, mixed corners, no carrier)");
            for (int ty = y0; ty <= y1; ty++)
            {
                sb.Append($"{ty,3} ");
                for (int tx = x0; tx <= x1; tx++)
                {
                    bool b00 = fog.FogAt(tx, ty, 0) == 0, b10 = fog.FogAt(tx + 1, ty, 0) == 0;
                    bool b01 = fog.FogAt(tx, ty + 1, 0) == 0, b11 = fog.FogAt(tx + 1, ty + 1, 0) == 0;
                    bool any = b00 || b10 || b01 || b11, all = b00 && b10 && b01 && b11;
                    char ch;
                    if (renders(tx, ty)) ch = any ? '#' : '=';
                    else if (all) ch = 'C';
                    else if (!any) ch = '.';
                    else ch = HasHorizontalQuadOver(quads, tx, ty) ? 'P' : 'H';
                    sb.Append(ch);
                }
                sb.AppendLine();
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
