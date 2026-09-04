#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Fog-of-war black-volume overlay (FogOfWarOverlay — the port of the native front covers
    // FUN_004cca20 + side walls FUN_004c6b20; Docs/FOG_OF_WAR_RE.md §4). Laws under test:
    // • a virgin (all byte-0) solid map is fully closed by opaque quads on the front plane
    // z = 14.5 (nibble 15 → 15·0.5+7), and carries no side walls (nothing explored);
    // • covers sit at each tile's own fill surface (nibble·0.5+7+CoverLift): a front-plane
    // cover over a deep open tile overhangs the fade carrier at depth by
    // (14.5−7)·tan(pitch) ≈ 2..5 tiles and its silhouette cuts a hard edge across the lit
    // cave wall;
    // • revealing a rect removes the covers there (all-black = all 4 lattice corners 0)
    // and raises side walls only where the black tile stands proud of a revealed
    // neighbour (black rock beside a dug pit: fill surface → nibble·0.5+7); flush
    // open-to-open seams emit no walls (the depth cover is the closure);
    // • depth steps between covered/patched tiles with a meshless side get skirts;
    // • fogActive == false (cutscene / show_fogofwar 0 / -nofow) hides the whole volume
    // while the data survives for re-show.
    // Pattern: GameObject + component, Pump drives it in EditMode.
    public sealed class FogOfWarOverlayTests
    {
        const int W = 48, H = 40;
        int[]? _heights;
        GameObject? _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        (FogOfWarState fog, FogOfWarOverlay ov) Make(System.Func<int, int, bool>? rendersAt = null)
        {
            _heights = new int[W * H];
            for (int i = 0; i < _heights.Length; i++) _heights[i] = 15;   // solid rock world
            var fog = new FogOfWarState(W, H, (x, y) => 15);
            _go = new GameObject("FowOverlayHost");
            var ov = _go.AddComponent<FogOfWarOverlay>();
            ov.Init(fog,
                (x, y) => (uint)x < W && (uint)y < H ? _heights![y * W + x] : 15,
                rendersAt);
            return (fog, ov);
        }

        static List<Vector3> Verts(FogOfWarOverlay ov)
        {
            var list = new List<Vector3>();
            for (int cy = 0; cy < ov.ChunksY; cy++)
                for (int cx = 0; cx < ov.ChunksX; cx++)
                {
                    var m = ov.ChunkMesh(cx, cy);
                    if (m != null && m.vertexCount > 0) list.AddRange(m.vertices);
                }
            return list;
        }

        // Quads of one submesh (0 = opaque covers/walls, 1 = alpha gradient patches).
        // AddQuad emits 6 indices per quad: (i,i+1,i+2)(i,i+2,i+3).
        static List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d)> QuadsOf(FogOfWarOverlay ov, int sub)
        {
            var list = new List<(Vector3, Vector3, Vector3, Vector3)>();
            for (int cy = 0; cy < ov.ChunksY; cy++)
                for (int cx = 0; cx < ov.ChunksX; cx++)
                {
                    var m = ov.ChunkMesh(cx, cy);
                    if (m == null || m.vertexCount == 0 || m.subMeshCount <= sub) continue;
                    var v = m.vertices;
                    var t = m.GetTriangles(sub);
                    for (int j = 0; j + 5 < t.Length; j += 6)
                        list.Add((v[t[j]], v[t[j + 1]], v[t[j + 2]], v[t[j + 5]]));
                }
            return list;
        }

        [Test]
        public void VirginMap_FullyCoveredOnTheFrontPlane_NoWalls()
        {
            var (_, ov) = Make();
            var verts = Verts(ov);
            Assert.Greater(verts.Count, 0, "an unexplored map must emit black covers");
            Assert.IsTrue(verts.All(v => Mathf.Abs(v.y - (FogOfWarOverlay.FrontPlaneY + FogOfWarOverlay.CoverLift)) < 1e-3f),
                "with nothing explored there are covers only — every vertex on z=14.5");
            // The merged cover strips must span the whole map (±0.01 rect growth).
            Assert.Less(verts.Min(v => v.x), 0.1f, "covers reach the left map edge");
            Assert.Greater(verts.Max(v => v.x), W - 0.1f, "covers reach the right map edge");
            Assert.Less(verts.Min(v => v.z), -(H - 0.1f), "covers reach the bottom map edge");
            Assert.Greater(verts.Max(v => v.z), -0.1f, "covers reach the top map edge");
            Assert.IsEmpty(QuadsOf(ov, 1), "no boundary ring on a virgin map → no gradient geometry");
        }

        [Test]
        public void Reveal_CoversFollowTileFill_WallsOnlyAtProudRock()
        {
            var (fog, ov) = Make();
            // An open cavern (dug to the floor, nibble 0) at [10..20]², plus a dug corridor
            // heading east into solid rock at [21..26]×[14..16]…
            for (int y = 10; y <= 20; y++)
                for (int x = 10; x <= 20; x++)
                    _heights![y * W + x] = 0;
            for (int y = 14; y <= 16; y++)
                for (int x = 21; x <= 26; x++)
                    _heights![y * W + x] = 0;
            // …explored: the cavern middle + the corridor up to lattice 24 (tiles ≤ 23 lit).
            fog.RemoveBlackFog(0, 12, 12, 18, 18);
            fog.RemoveBlackFog(0, 19, 14, 24, 16);
            ov.Pump();

            var verts = Verts(ov);
            float depthCover = 0f * 0.5f + 7f + FogOfWarOverlay.CoverLift;      // 7.03
            float frontCover = FogOfWarOverlay.FrontPlaneY + FogOfWarOverlay.CoverLift;
            var covers = QuadsOf(ov, 0).FindAll(q =>
                Mathf.Abs(q.a.y - q.b.y) < 1e-4f && Mathf.Abs(q.a.y - q.c.y) < 1e-4f);
            bool CoverOver(int tx, int ty, float y) => covers.Exists(q =>
                Mathf.Abs(q.a.y - y) < 1e-3f
                && tx + 0.5f > Mathf.Min(q.a.x, q.c.x) && tx + 0.5f < Mathf.Max(q.a.x, q.c.x)
                && -(ty + 0.5f) > Mathf.Min(q.a.z, q.c.z) && -(ty + 0.5f) < Mathf.Max(q.a.z, q.c.z));

            // No cover may intrude into the revealed rect (a tile keeps its cover only while
            // all 4 lattice corners are byte 0 — the FUN_004ca830:284-289 sentinel).
            Assert.IsFalse(covers.Exists(q =>
                    Mathf.Min(q.a.x, q.c.x) > 12.2f && Mathf.Max(q.a.x, q.c.x) < 18.8f
                    && Mathf.Min(-q.a.z, -q.c.z) > 12.2f && Mathf.Max(-q.a.z, -q.c.z) < 18.8f),
                "no black cover over explored tiles");
            // Covers follow the tile fill: black cavern tiles at the open depth 7+lift,
            // black solid tiles at the front plane (nibble 15 → 14.5+lift).
            Assert.IsTrue(CoverOver(10, 10, depthCover),
                "black open-cavern tile must be covered at its own fill depth (7+lift)");
            Assert.IsTrue(CoverOver(25, 15, depthCover),
                "black corridor continuation must be covered at the corridor depth");
            Assert.IsTrue(CoverOver(30, 30, frontCover),
                "black solid tile keeps the native front-plane cover");
            // Flush open-to-open seams raise no walls (regression: they previously emitted
            // full-depth black rectangles [7..14.53] across the whole cave cross-section).
            Assert.IsFalse(verts.Any(v => Mathf.Abs(v.x - 19.005f) < 1e-3f && v.y > 7.1f),
                "no full-depth wall on the open cavern flank (flush seam)");
            Assert.IsFalse(verts.Any(v => Mathf.Abs(v.x - 10.995f) < 1e-3f && v.y > 7.1f),
                "no full-depth wall on the open cavern flank (flush seam)");
            // (rows 14..16 only — the solid rock above the corridor legitimately walls its
            // own W edge at row 13, native probe law 004c6b20:41-67; its quad's deepest
            // vertices sit exactly at −z=14, so the corridor-row filter starts past it)
            Assert.IsFalse(verts.Any(v => Mathf.Abs(v.x - 25.005f) < 1e-3f && v.y > 7.1f
                                          && -v.z > 14.5f && -v.z <= 17f),
                "no wall at the corridor's black continuation (flush open-to-open)");
            // Proud rock does wall: the solid black tile (22,12) above the revealed corridor
            // (S edge at lattice 13, probes row 14 = lit) → wall [7.. 14.5+lift] at
            // z = −(13 − inset).
            Assert.IsTrue(verts.Any(v => Mathf.Abs(v.z - (-12.995f)) < 1e-3f && Mathf.Abs(v.y - 7f) < 1e-3f)
                       && verts.Any(v => Mathf.Abs(v.z - (-12.995f)) < 1e-3f && Mathf.Abs(v.y - frontCover) < 1e-3f),
                "solid black rock proud above the revealed corridor keeps its side wall");
        }

        // Gradient patches over meshless tiles: a tile that renders no terrain (load-time
        // block-59 cave air) cannot carry the shader's black-corner gradient — when its cover
        // retreats (not AllBlack any more) the overlay must lay an alpha-gradient quad at the
        // tile's fill depth (nibble·0.5+7) instead of leaving a hole onto the covers/walls
        // behind (the native's mat-21→22 cave-backdrop alpha fade, 02-rendering.md §4c).
        [Test]
        public void MeshlessBlackBoundary_GetsGradientPatches_NotHoles()
        {
            // An open meshless cave pocket [10..20]² in solid rock…
            bool InPocket(int x, int y) => x >= 10 && x <= 20 && y >= 10 && y <= 20;
            var (fog, ov) = Make(rendersAt: (x, y) => !InPocket(x, y));
            for (int y = 10; y <= 20; y++)
                for (int x = 10; x <= 20; x++)
                    _heights![y * W + x] = 0;   // fully open air
            // …revealed over [12..22]×[12..18] — the rect leaves the pocket on the east, so the
            // boundary crosses both meshless (W/N/S flanks) and meshed (E flank) tiles.
            fog.RemoveBlackFog(0, 12, 12, 22, 18);
            ov.Pump();

            // Patch = horizontal quad of the gradient submesh (1); pocket air (nibble 0) → y = 7.
            // (Submesh 1 also carries vertical step skirts — the horizontal filter drops them.)
            List<(float x0, float x1, float z0, float z1)> patches = new();
            foreach (var (a, b, c, d) in QuadsOf(ov, 1))
            {
                if (Mathf.Abs(a.y - b.y) > 1e-3f || Mathf.Abs(a.y - c.y) > 1e-3f) continue;
                Assert.AreEqual(7f, a.y, 1e-3f, "pocket patches sit at the open fill depth 7");
                patches.Add((Mathf.Min(a.x, c.x), Mathf.Max(a.x, c.x),
                             Mathf.Min(a.z, c.z), Mathf.Max(a.z, c.z)));
            }
            bool Patched(int tx, int ty) => patches.Exists(p =>
                tx + 0.5f > p.x0 && tx + 0.5f < p.x1 && -(ty + 0.5f) > p.z0 && -(ty + 0.5f) < p.z1);

            // Mixed-corner meshless tiles must be patched: west flank (11,y), north (x,11),
            // south (x,18) — corners straddle the reveal rect edge.
            Assert.IsTrue(Patched(11, 14), "west-flank meshless boundary tile must carry a patch");
            Assert.IsTrue(Patched(15, 11), "north-flank meshless boundary tile must carry a patch");
            Assert.IsTrue(Patched(15, 18), "south-flank meshless boundary tile must carry a patch");
            // Meshed mixed-corner tiles carry the gradient in the terrain shader — no patch.
            Assert.IsFalse(Patched(22, 14), "meshed boundary tile must NOT get a patch");
            // Fully-revealed pocket interior: nothing.
            Assert.IsFalse(Patched(15, 15), "lit meshless interior must NOT get a patch");
            // AllBlack pocket tiles keep their opaque cover and get no redundant patch.
            Assert.IsFalse(Patched(10, 10) || Patched(19, 10), "covered AllBlack tiles need no patch");
            // The AllBlack pocket cover sits at the pocket's own fill depth (7+lift), not the
            // front plane (opaque submesh 0).
            Assert.IsTrue(QuadsOf(ov, 0).Exists(q =>
                    Mathf.Abs(q.a.y - (7f + FogOfWarOverlay.CoverLift)) < 1e-3f
                    && q.a.y == q.b.y && 10.5f > Mathf.Min(q.a.x, q.c.x) && 10.5f < Mathf.Max(q.a.x, q.c.x)
                    && -10.5f > Mathf.Min(q.a.z, q.c.z) && -10.5f < Mathf.Max(q.a.z, q.c.z)),
                "AllBlack pocket tile (10,10) is covered at its own fill depth");
            // The pocket rim's depth step (meshless cover 7+lift ↔ meshed solid cover
            // 14.5+lift) is closed by a vertical skirt in the gradient submesh: tile (9,10)
            // (meshed, AllBlack, 14.53) owns its E edge to pocket tile (10,10) → skirt quad
            // on lattice x=10 spanning the two cover depths.
            Assert.IsTrue(QuadsOf(ov, 1).Exists(q =>
                    Mathf.Abs(q.a.x - 10f) < 1e-4f && Mathf.Abs(q.c.x - 10f) < 1e-4f
                    && Mathf.Max(q.a.y, q.c.y) > 14.5f && Mathf.Min(q.a.y, q.c.y) < 7.1f
                    && -10.5f > Mathf.Min(q.a.z, q.c.z) && -10.5f < Mathf.Max(q.a.z, q.c.z)),
                "pocket rim depth step must be closed by a gradient skirt at lattice x=10");
        }

        // Patches follow the display field: sim bytes flip instantly, but the presenter chases
        // the displayed fog over SmoothSeconds, and the patch shader's alpha (1−fow.g) samples
        // that display texture. Under the old raw law a meshless tile lost its patch the frame
        // its last corner byte flipped — a near-opaque black quad vanished mid-chase and the
        // void behind popped in at full contrast, tile by tile, at the raw front.
        // Law under test: with a presenter wired, a patch may be absent only where all 4
        // corner display values are 255 — the bilinear alpha is then identically 0 over the
        // quad, so geometry removal is invisible by construction (seamless dissolve).
        [Test]
        public void MeshlessChaseBand_KeepsPatches_UntilDisplayCatchesUp()
        {
            bool InPocket(int x, int y) => x >= 10 && x <= 20 && y >= 10 && y <= 20;
            System.Func<int, int, bool> rendersAt = (x, y) => !InPocket(x, y);
            var (fog, ov) = Make(rendersAt);
            for (int y = 10; y <= 20; y++)
                for (int x = 10; x <= 20; x++)
                    _heights![y * W + x] = 0;                    // open cave air
            var pres = _go!.AddComponent<FogOfWarPresenter>();
            pres.Init(fog);                                      // snap: all-black display
            ov.Init(fog,
                (x, y) => (uint)x < W && (uint)y < H ? _heights![y * W + x] : 15,
                rendersAt, pres);                                // re-Init with the presenter

            bool AllRaw(int tx, int ty) =>
                fog.FogAt(tx, ty, 0) == 0 && fog.FogAt(tx + 1, ty, 0) == 0 &&
                fog.FogAt(tx, ty + 1, 0) == 0 && fog.FogAt(tx + 1, ty + 1, 0) == 0;
            bool AnyRaw(int tx, int ty) =>
                fog.FogAt(tx, ty, 0) == 0 || fog.FogAt(tx + 1, ty, 0) == 0 ||
                fog.FogAt(tx, ty + 1, 0) == 0 || fog.FogAt(tx + 1, ty + 1, 0) == 0;
            bool CaughtUp(int tx, int ty) =>
                pres.DisplayAt(tx, ty).g == 255 && pres.DisplayAt(tx + 1, ty).g == 255 &&
                pres.DisplayAt(tx, ty + 1).g == 255 && pres.DisplayAt(tx + 1, ty + 1).g == 255;
            bool PatchAt(int tx, int ty) => QuadsOf(ov, 1).Exists(q =>
                Mathf.Abs(q.a.y - 7f) < 1e-3f &&                 // fill depth (nibble 0)
                tx + 0.5f > Mathf.Min(q.a.x, q.c.x) && tx + 0.5f < Mathf.Max(q.a.x, q.c.x) &&
                -(ty + 0.5f) > Mathf.Min(q.a.z, q.c.z) && -(ty + 0.5f) < Mathf.Max(q.a.z, q.c.z));

            fog.RemoveBlackFog(0, 12, 12, 18, 18);               // raw bytes flip instantly
            pres.Pump(0.1f);                                     // display mid-chase (g ≈ 36)
            ov.Pump();

            Assert.Less((int)pres.DisplayAt(15, 15).g, 255, "display must still be chasing");
            // Lattice 12..18 lit ⇒ tiles [12..17]² carry no raw black corner — the old law
            // dropped their patches right here, mid-chase (the visible pop). New law: any
            // corner display < 255 ⇒ the patch stays (its alpha is still > 0 somewhere).
            for (int ty = 10; ty <= 20; ty++)
                for (int tx = 10; tx <= 20; tx++)
                {
                    if (AllRaw(tx, ty)) continue;                // opaque cover closes it
                    Assert.IsTrue(PatchAt(tx, ty),
                        $"mid-chase meshless tile ({tx},{ty}) must keep its gradient patch");
                }

            pres.Pump(1.0f);                                     // chase completes (snap to 255)
            ov.Pump();                                           // rebuilt via DisplayChanged alone
            // Caught up: the patch set retracts to the permanent raw boundary ring. A patch
            // is absent ⇔ all 4 corner displays are 255 (alpha ≡ 0 — invisible removal).
            for (int ty = 10; ty <= 20; ty++)
                for (int tx = 10; tx <= 20; tx++)
                {
                    if (AllRaw(tx, ty)) continue;
                    Assert.AreEqual(AnyRaw(tx, ty), PatchAt(tx, ty),
                        $"settled patch set must equal the raw ring at ({tx},{ty})");
                    if (!PatchAt(tx, ty))
                        Assert.IsTrue(CaughtUp(tx, ty),
                            $"patch left ({tx},{ty}) although its display alpha was not yet 0");
                }
        }

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

        [Test]
        public void FogInactive_HidesTheVolume_AndReshows()
        {
            var (fog, ov) = Make();
            var holder = _go!.transform.Find("FowBlack");
            Assert.IsNotNull(holder, "the black-volume holder must exist");
            Assert.IsTrue(holder!.gameObject.activeSelf, "fog active by default → volume shown");

            // Fog stays active through cutscenes — only show_fogofwar / -nofow hide the volume.
            fog.ShowFog = false;                    // Tcl show_fogofwar 0 (live in the port)
            ov.Pump();
            Assert.IsFalse(holder.gameObject.activeSelf, "show_fogofwar 0 hides the volume");

            fog.ShowFog = true;
            ov.Pump();
            Assert.IsTrue(holder.gameObject.activeSelf, "show_fogofwar 1 re-shows the volume");
            Assert.Greater(Verts(ov).Count, 0, "the mesh data survives the hide/show cycle");
        }
    }
}
