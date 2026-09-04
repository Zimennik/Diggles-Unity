#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // World-edge fog law: a tunnel reveal near the map edge exposed the raw void past the
    // last tiles. RE ground truth:
    // • the native never clamps the reveal at the border — RemoveFog bounds each ray step
    // by the full world tile rect (0057e120:137-140), so border bytes flip/fade in sim;
    // • what it clamps is the render: CScapeRenderer::RenderScape draws a tile (mesh and
    // black front covers) only when minX+2 < tx < maxX−2 and minY+2 < ty < maxY−2
    // (004c4de0:569-578; record-less tiles skipped identically:579). The outer band is
    // never drawn — it reads as permanent blackness no matter what its fog bytes say,
    // and the raw world edge is unseeable by construction.
    // Port projection under test (FogOfWarState.IsRenderedTile + FogOfWarOverlay.PinnedBlack
    // + FogOfWarPresenter.SetPinnedBlack):
    // • sim untouched: reveals still write border-band bytes;
    // • overlay: pinned tiles keep their opaque covers after any reveal — (a) the native
    // inset band, (b) the port stand-in for solid tiles the renderer cannot draw
    // (RendersTerrain false, fill ≥ 14 — the native draws default solid earth there);
    // • open cave air (fill < 14) is never pinned (keeps the gradient-patch law);
    // • above the fow-begin sky line nothing is pinned (native shows backdrop, not black);
    // • the presenter's texture targets agree with the pin (no lit terrain beside a cover
    // that will never drop), and unpinning on Clear restores raw-byte targets.
    public sealed class FogOfWarWorldEdgeTests
    {
        const int W = 48, H = 40;
        int[]? _heights;
        GameObject? _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
            _heights = null;   // NUnit reuses the fixture instance across tests
        }

        (FogOfWarState fog, FogOfWarOverlay ov) Make(System.Func<int, int, bool>? rendersAt = null,
                                                     FogOfWarPresenter? presenter = null,
                                                     FogOfWarState? fogIn = null)
        {
            _heights ??= NewHeights();
            var fog = fogIn ?? new FogOfWarState(W, H, (x, y) => 15);
            _go ??= new GameObject("FowWorldEdgeHost");
            var ov = _go.AddComponent<FogOfWarOverlay>();
            ov.Init(fog,
                (x, y) => (uint)x < W && (uint)y < H ? _heights![y * W + x] : 15,
                rendersAt, presenter);
            return (fog, ov);
        }

        int[] NewHeights()
        {
            var h = new int[W * H];
            for (int i = 0; i < h.Length; i++) h[i] = 15;   // solid rock world
            return h;
        }

        static float FrontCover => FogOfWarOverlay.FrontPlaneY + FogOfWarOverlay.CoverLift;

        // Is tile (tx,ty) under a horizontal opaque cover quad (submesh 0) at depth y?
        static bool CoverOver(FogOfWarOverlay ov, int tx, int ty, float y)
        {
            for (int cy = 0; cy < ov.ChunksY; cy++)
                for (int cx = 0; cx < ov.ChunksX; cx++)
                {
                    var m = ov.ChunkMesh(cx, cy);
                    if (m == null || m.vertexCount == 0 || m.subMeshCount < 1) continue;
                    var v = m.vertices;
                    var t = m.GetTriangles(0);
                    for (int j = 0; j + 5 < t.Length; j += 6)
                    {
                        Vector3 a = v[t[j]], c = v[t[j + 2]];
                        if (Mathf.Abs(a.y - c.y) > 1e-3f || Mathf.Abs(a.y - y) > 1e-3f) continue;
                        if (tx + 0.5f > Mathf.Min(a.x, c.x) && tx + 0.5f < Mathf.Max(a.x, c.x)
                            && -(ty + 0.5f) > Mathf.Min(a.z, c.z) && -(ty + 0.5f) < Mathf.Max(a.z, c.z))
                            return true;
                    }
                }
            return false;
        }

        // ---- sim side: the predicate boundaries + no reveal clamp --------------------------

        [Test]
        public void RenderBand_PredicateBoundaries_AndRevealHasNoSimClamp()
        {
            var fog = new FogOfWarState(W, H, (x, y) => 15);

            // 004c4de0:577-578 — min+2 < tx < max−2 (rendered = [3.. W−3]).
            Assert.IsFalse(fog.IsRenderedTile(2, 20), "col 2 is inside the undrawn band");
            Assert.IsTrue(fog.IsRenderedTile(3, 20), "col 3 is the first drawn column");
            Assert.IsTrue(fog.IsRenderedTile(W - 3, 20), "col W-3 is the last drawn column");
            Assert.IsFalse(fog.IsRenderedTile(W - 2, 20), "col W-2 is inside the undrawn band");
            // 004c4de0:569-570 — same for rows.
            Assert.IsFalse(fog.IsRenderedTile(20, 2));
            Assert.IsTrue(fog.IsRenderedTile(20, 3));
            Assert.IsTrue(fog.IsRenderedTile(20, H - 3));
            Assert.IsFalse(fog.IsRenderedTile(20, H - 2));

            // The reveal side has no border clamp (0057e120:137-140): band bytes still flip.
            fog.RemoveBlackFog(0, 0, 18, 8, 22);
            Assert.AreEqual(1, fog.FogAt(0, 18, 0), "corner band byte revealed in sim");
            Assert.AreEqual(1, fog.FogAt(1, 20, 0), "band byte revealed in sim");
        }

        // ---- overlay law (a): the native inset band stays covered through reveals ----------

        [Test]
        public void RevealedBorderBand_KeepsOpaqueCovers()
        {
            var (fog, ov) = Make();                       // all solid, everything renders
            fog.RemoveBlackFog(0, 0, 18, 8, 22);          // reveal into the west band (cols 0..2)
            ov.Pump();

            Assert.IsTrue(CoverOver(ov, 1, 20, FrontCover),
                "revealed band tile keeps its cover — the native never draws the inset band " +
                "(004c4de0:569-578), so it must read as permanent black, not as the world edge");
            Assert.IsFalse(CoverOver(ov, 5, 20, FrontCover),
                "a revealed tile inside the drawn region drops its cover as before");
        }

        // ---- overlay law (b): solid meshless tiles stay covered; cave air is exempt --------

        [Test]
        public void RevealedSolidMeshlessTiles_StayCovered_CaveAirDoesNot()
        {
            // "render bbox": tiles x >= 30 draw nothing; one open cave-air tile at (32,20).
            _heights = NewHeights();
            _heights[20 * W + 32] = 0;
            var (fog, ov) = Make(rendersAt: (x, y) => x < 30);
            fog.RemoveBlackFog(0, 24, 16, 36, 24);        // reveal across the bbox seam
            ov.Pump();

            Assert.IsTrue(CoverOver(ov, 34, 20, FrontCover),
                "revealed SOLID meshless tile stays covered — the native renders default solid " +
                "earth there; the port has no mesh, so black is the closure (no raw void)");
            Assert.IsFalse(CoverOver(ov, 26, 20, FrontCover),
                "revealed meshed tile drops its cover");
            Assert.IsFalse(CoverOver(ov, 32, 20, 7f + FogOfWarOverlay.CoverLift),
                "open cave air (fill < 14) is NOT pinned — the r5 gradient-patch law stands");
        }

        // ---- the fow-begin sky gate --------------------------------------------------------

        [Test]
        public void SkyBandAboveFowBegin_IsNotPinned_UndergroundBandIs()
        {
            var (fog, ov) = Make();
            fog.SetFowBegin(20, 0);                       // sky rows 5..19 revealed full width
            ov.Pump();
            Assert.IsFalse(CoverOver(ov, 1, 10, FrontCover),
                "band tiles ABOVE the fow line are not pinned — the native's undrawn band " +
                "shows the sky backdrop there, not blackness");

            fog.RemoveBlackFog(0, 0, 24, 8, 28);          // reveal into the band underground
            ov.Pump();
            Assert.IsTrue(CoverOver(ov, 1, 26, FrontCover),
                "band tiles BELOW the fow line stay covered after a reveal");
        }

        // ---- presenter texture agrees with the pin -----------------------------------------

        [Test]
        public void PresenterTargets_FollowThePin_AndUnpinOnClear()
        {
            _heights = NewHeights();
            var fog = new FogOfWarState(W, H, (x, y) => 15);
            _go = new GameObject("FowWorldEdgeHost");
            var pres = _go.AddComponent<FogOfWarPresenter>();
            pres.Init(fog);
            var (_, ov) = Make(rendersAt: null, presenter: pres, fogIn: fog);

            fog.RemoveBlackFog(0, 0, 18, 8, 22);          // band + interior revealed in sim
            pres.Pump(10f);

            Assert.AreEqual((0, 0), ((int, int))pres.TargetAt(1, 20),
                "pinned band texel reads permanent black despite the revealed sim byte");
            Assert.AreEqual((255, 255), ((int, int))pres.TargetAt(5, 20),
                "drawn-region texel decodes the raw byte as before");

            ov.Clear();                                   // un-pins (predicate closes over ov state)
            Assert.AreEqual((255, 255), ((int, int))pres.TargetAt(1, 20),
                "Clear() must un-pin and restore raw-byte targets (no stale predicate)");
        }
    }
}
