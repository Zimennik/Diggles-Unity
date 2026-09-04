#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Dig-mark renderer DATA layer — the faithful DigMark plane meshes (DigMarkOverlay +
    // DigMarkMesher, the separate-object port of Wiggles\Map\DigMark.cpp):
    // • DigMarkMesher must reproduce FUN_0056ae30's field split (corner tiles A/B/C/D,
    // nibble bit 3 = the depth pass) and FUN_00568410's per-count tessellation
    // (pie / band / notch / quad, outline vs fill index counts);
    // • DigMarkOverlay must rebuild the persistent mesh from DiggableTerrain's dig-state
    // nibbles (tile+0x08 analogue) on DigMarkChanged, and the hover mesh from the
    // ClickToMoveService hover set, on the front plane y = 15 with the native palettes;
    // • other owners' layers stay invisible; identical hover republish keeps the version;
    // out-of-bounds hover cells are ignored.
    // No real BDB/data needed — DiggableTerrain.Init accepts bdb=null (the WorkLoopTests pattern).
    public sealed class DigMarkOverlayTests
    {
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            DigMarkOverlay.ClearHover();   // static hover state must not leak between tests
            foreach (var go in _gos) if (go != null) Object.DestroyImmediate(go);
            _gos.Clear();
        }

        (DiggableTerrain terr, DigMarkOverlay overlay) MakeTerrain(int w, int h)
        {
            var go = new GameObject("DigMarkOverlayHost");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h,
                BlockIdx = new uint[w * h],   // all block 0 = solid (bdb=null ⇒ height 15)
                Terrain = new byte[w * h],
            }, null!, "", () => { });
            var o = go.AddComponent<DigMarkOverlay>();
            o.Init(t);
            return (t, o);
        }

        // ---------------- DigMarkMesher: the FUN_0056ae30/FUN_00568410 tessellation ----------------

        [Test]
        public void Mesher_SingleStdCorner_QuarterPieInTheOwnQuadrant()
        {
            var md = new DigMarkMeshData();
            // Cell (0,0), only corner A (tile 0,0) marked with the standard-tunnel dummy 3.
            DigMarkMesher.EmitCell(0, 0, 3, 0, 0, 0, outline: false, md);
            // One pass, 4 arc segments: center + 2·5 ring verts, 3 tris per segment.
            Assert.AreEqual(11, md.VertexCount, "pie = center + (N+1)*2 ring verts, N=4");
            Assert.AreEqual(4 * 3 * 3, md.Tris.Count, "pie fill = 9 indices per segment");
            Assert.IsTrue(md.Sel.All(s => s == 0), "nibble 3 (bit 3 clear) must be the std pass");
            // Corner A's quadrant: x∈[0,0.49], y∈[0,0.49] (angle −π/2..0, FUN_00568410:236-260).
            foreach (var p in md.Pos)
            {
                Assert.That(p.x, Is.InRange(-0.001f, 0.491f), "pie must stay in the corner quadrant (x)");
                Assert.That(p.y, Is.InRange(-0.001f, 0.491f), "pie must stay in the corner quadrant (y)");
            }
        }

        [Test]
        public void Mesher_DepthNibble_RoutesToTheDepthPass()
        {
            var md = new DigMarkMeshData();
            DigMarkMesher.EmitCell(0, 0, 8 | 2, 0, 0, 0, outline: false, md);   // (cnt|8) depth mark
            Assert.Greater(md.VertexCount, 0);
            Assert.IsTrue(md.Sel.All(s => s == 1), "nibble bit 3 set must route to the depth pass");
        }

        [Test]
        public void Mesher_MixedCell_SplitsIntoBothPassesWithRoundFlag()
        {
            var md = new DigMarkMeshData();
            // A = std (3), B = depth (8): adjacent combined pair → roundFlag → 2-segment pies.
            DigMarkMesher.EmitCell(0, 0, 3, 8, 0, 0, outline: false, md);
            Assert.AreEqual(14, md.VertexCount, "two 2-segment pies (7 verts each: center + 3·2)");
            Assert.AreEqual(2 * 2 * 3 * 3, md.Tris.Count);
            Assert.IsTrue(md.Sel.Take(7).All(s => s == 1), "the depth pass renders first (FUN_0056ae30:64)");
            Assert.IsTrue(md.Sel.Skip(7).All(s => s == 0));
        }

        [Test]
        public void Mesher_FullCell_QuadFilled_NothingInOutlineMode()
        {
            var md = new DigMarkMeshData();
            DigMarkMesher.EmitCell(0, 0, 3, 3, 3, 3, outline: false, md);
            Assert.AreEqual(4, md.VertexCount, "count==4 = one unit quad");
            Assert.AreEqual(6, md.Tris.Count);
            md.Clear();
            DigMarkMesher.EmitCell(0, 0, 3, 3, 3, 3, outline: true, md);
            Assert.AreEqual(0, md.Tris.Count, "outline mode skips full quads (FUN_00568410:67-69)");
        }

        [Test]
        public void Mesher_AdjacentPair_HalfCellBand()
        {
            var md = new DigMarkMeshData();
            DigMarkMesher.EmitCell(0, 0, 3, 3, 0, 0, outline: false, md);   // A&B = the top edge
            Assert.AreEqual(6, md.VertexCount, "band = 6 verts (rim/contour/edge lines)");
            Assert.AreEqual(12, md.Tris.Count, "fill = rim band + strip (the band overlaps = 2× alpha)");
            // The strip spans the marked edge (y=0) down to the 0.49 contour only — never past
            // the cell midline (the other half belongs to the neighbour cell's mirrored band).
            Assert.IsTrue(md.Pos.All(p => p.y <= 0.491f), "band must not cross the contour line");
            md.Clear();
            DigMarkMesher.EmitCell(0, 0, 3, 3, 0, 0, outline: true, md);
            Assert.AreEqual(6, md.Tris.Count, "outline = the rim band only");
        }

        [Test]
        public void Mesher_ThreeCorners_NotchAroundTheEmptyOne()
        {
            var md = new DigMarkMeshData();
            DigMarkMesher.EmitCell(0, 0, 3, 3, 3, 0, outline: false, md);   // D empty
            Assert.AreEqual(9, md.VertexCount, "notch = 3 arc positions × 3 rings");
            Assert.AreEqual(24, md.Tris.Count, "notch fill = 8 tris (FUN_00568410:975-1192)");
            // The √2 midpoint of the outer ring reaches the far corner (0,0) exactly.
            Assert.IsTrue(md.Pos.Any(p => Vector2.Distance(p, new Vector2(0f, 0f)) < 0.01f),
                "the notch's far ring midpoint must reach the opposite cell corner");
            md.Clear();
            DigMarkMesher.EmitCell(0, 0, 3, 3, 3, 0, outline: true, md);
            Assert.AreEqual(12, md.Tris.Count, "notch outline = the 4 rim-band tris");
        }

        [Test]
        public void Mesher_DiagonalPair_TwoIndependentPies()
        {
            var md = new DigMarkMeshData();
            DigMarkMesher.EmitCell(0, 0, 3, 0, 0, 3, outline: false, md);   // A & D (diagonal)
            // No roundFlag (the diagonal case): two smooth 4-segment pies.
            Assert.AreEqual(22, md.VertexCount, "two 11-vert pies (FUN_00568410:819-822 recursion)");
            Assert.AreEqual(2 * 4 * 9, md.Tris.Count);
        }

        // ---------------- DigMarkOverlay: terrain → persistent mesh ----------------

        [Test]
        public void MarkSetAndClear_RebuildsThePersistentMesh()
        {
            const int W = 16, H = 12;
            var (terr, overlay) = MakeTerrain(W, H);
            Assert.IsNotNull(overlay.MarksMesh);
            Assert.AreEqual(0, overlay.MarksMesh!.vertexCount, "virgin terrain must render no marks");

            // Standard-tunnel dummy (mode 1, nibble 3 — bit 3 clear) → the GREEN palette.
            terr.SetDigMark(5, 6, owner: 0, cnt: 0, dummy: true);
            overlay.Pump();
            var mesh = overlay.MarksMesh!;
            Assert.Greater(mesh.vertexCount, 0, "a committed mark must tessellate");
            Assert.IsTrue(mesh.colors32.All(c => c.Equals(DigMarkOverlay.CommittedStdColor)),
                "a dummy mark must use the native green pass palette");
            // The blob sits around native (5,6) on the front plane: local (5, 15, −6) ± 0.5.
            foreach (var v in mesh.vertices)
            {
                Assert.AreEqual(DigMarkOverlay.PlaneY, v.y, 0.0001f, "all mark verts live on the plane y=15");
                Assert.That(v.x, Is.InRange(4.4f, 5.6f));
                Assert.That(v.z, Is.InRange(-6.6f, -5.4f));
            }

            // Depth tool (mode 0, cnt|8 — bit 3 set) → the STEEL-BLUE palette.
            terr.SetDigMark(9, 3, owner: 0, cnt: 2, dummy: false);
            overlay.Pump();
            mesh = overlay.MarksMesh!;
            Assert.IsTrue(mesh.colors32.Any(c => c.Equals(DigMarkOverlay.CommittedDepthColor)),
                "a (cnt|8) mark must use the native steel-blue pass palette");
            Assert.IsTrue(mesh.colors32.Any(c => c.Equals(DigMarkOverlay.CommittedStdColor)),
                "the std mark must coexist");

            terr.ClearDigMark(5, 6, 0);
            overlay.Pump();
            mesh = overlay.MarksMesh!;
            Assert.Greater(mesh.vertexCount, 0, "the depth mark must survive the other clear");
            Assert.IsTrue(mesh.colors32.All(c => c.Equals(DigMarkOverlay.CommittedDepthColor)),
                "the cleared std mark must leave no green geometry");

            terr.ClearDigMark(9, 3, 0);
            overlay.Pump();
            Assert.AreEqual(0, overlay.MarksMesh!.vertexCount, "clearing every mark must empty the mesh");
        }

        [Test]
        public void OtherOwnersMarks_StayInvisible()
        {
            const int W = 8, H = 8;
            var (terr, overlay) = MakeTerrain(W, H);
            terr.SetDigMark(3, 3, owner: 1, cnt: 0, dummy: true);   // an AI player's order
            overlay.Pump();
            Assert.AreEqual(0, overlay.MarksMesh!.vertexCount,
                "owner-1 marks must not show on the human player's (owner 0) overlay");
            terr.SetDigMark(3, 3, owner: 0, cnt: 0, dummy: true);
            overlay.Pump();
            Assert.Greater(overlay.MarksMesh!.vertexCount, 0);
        }

        // ---------------- DigMarkOverlay: hover/marquee mesh ----------------

        [Test]
        public void Hover_BuildsMovesAndClears()
        {
            const int W = 8, H = 8;
            var (_, overlay) = MakeTerrain(W, H);
            Assert.AreEqual(0, overlay.HoverMesh!.vertexCount);

            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (2, 2, true), (3, 2, false) });
            overlay.Pump();
            var mesh = overlay.HoverMesh!;
            Assert.Greater(mesh.vertexCount, 0, "published hover cells must tessellate");
            Assert.IsTrue(mesh.colors32.Any(c => c.Equals(DigMarkOverlay.HoverValidColor)),
                "the valid cell renders the green marquee palette");
            Assert.IsTrue(mesh.colors32.Any(c => c.Equals(DigMarkOverlay.HoverInvalidColor)),
                "the invalid cell renders the red-orange palette (value|8 → the second pass)");

            // Hover moves → the mesh follows (old cells vanish).
            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (6, 6, true) });
            overlay.Pump();
            mesh = overlay.HoverMesh!;
            Assert.Greater(mesh.vertexCount, 0);
            Assert.IsTrue(mesh.vertices.All(v => v.x > 4.5f && v.z < -4.5f),
                "geometry must move with the hover set");
            Assert.IsTrue(mesh.colors32.All(c => c.Equals(DigMarkOverlay.HoverValidColor)));

            DigMarkOverlay.ClearHover();
            overlay.Pump();
            Assert.AreEqual(0, overlay.HoverMesh!.vertexCount, "cleared hover must leave no residue");
        }

        [Test]
        public void HoverOutline_UsesTheBrighterRingPalette()
        {
            const int W = 8, H = 8;
            var (_, overlay) = MakeTerrain(W, H);
            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (2, 2, true) }, outline: true);
            overlay.Pump();
            var mesh = overlay.HoverMesh!;
            Assert.Greater(mesh.vertexCount, 0);
            // Outline job factor 0x50 vs the fill 0x32 (FUN_0056d0b0:252-261) → ×1.6 brightness.
            var expected = new Color32(
                (byte)Mathf.Min(255f, DigMarkOverlay.HoverValidColor.r * 1.6f),
                (byte)Mathf.Min(255f, DigMarkOverlay.HoverValidColor.g * 1.6f),
                (byte)Mathf.Min(255f, DigMarkOverlay.HoverValidColor.b * 1.6f),
                DigMarkOverlay.HoverValidColor.a);
            Assert.IsTrue(mesh.colors32.All(c => c.Equals(expected)), "outline hover must brighten ×1.6");
            // Outline switch alone (same cells) must rebuild: back to fill = the base palette.
            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (2, 2, true) }, outline: false);
            overlay.Pump();
            Assert.IsTrue(overlay.HoverMesh!.colors32.All(c => c.Equals(DigMarkOverlay.HoverValidColor)),
                "an outline-flag change must bump the version and re-palette");
        }

        // The eraser (dig:undo) cursor: a flat opaque-gray brush, ignoring the per-cell valid flag
        // (there is no "invalid" for a delete brush) and never brightened by the outline factor.
        [Test]
        public void HoverErase_UsesTheFlatGrayBrush()
        {
            const int W = 8, H = 8;
            var (_, overlay) = MakeTerrain(W, H);
            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (2, 2, true), (3, 2, false) },
                outline: false, DigMarkOverlay.HoverStyle.Erase);
            overlay.Pump();
            var mesh = overlay.HoverMesh!;
            Assert.Greater(mesh.vertexCount, 0, "the erase brush must tessellate");
            Assert.IsTrue(mesh.colors32.All(c => c.Equals(DigMarkOverlay.HoverEraseColor)),
                "the eraser renders flat gray for every cell, even the valid=false one");
            // Switching back to a Dig hover over the same cells must re-palette (style bumps the version).
            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (2, 2, true), (3, 2, false) });
            overlay.Pump();
            Assert.IsTrue(overlay.HoverMesh!.colors32.Any(c => c.Equals(DigMarkOverlay.HoverInvalidColor)),
                "a style change back to Dig must recolor the invalid cell red-orange");
        }

        [Test]
        public void IdenticalHoverRepublish_KeepsVersion()
        {
            var cells = new List<(int, int, bool)> { (1, 1, true), (2, 1, true) };
            DigMarkOverlay.PublishHover(cells);
            int v = DigMarkOverlay.HoverVersion;
            DigMarkOverlay.PublishHover(new List<(int, int, bool)>(cells));
            Assert.AreEqual(v, DigMarkOverlay.HoverVersion, "identical republish must not bump the hover version");
            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (1, 1, false), (2, 1, true) });
            Assert.AreNotEqual(v, DigMarkOverlay.HoverVersion, "a changed verdict must bump the hover version");
        }

        // Out-of-bounds hover cells (a marquee dragged over the world rim) must be ignored, not throw.
        [Test]
        public void OutOfBoundsHover_IsIgnored()
        {
            const int W = 8, H = 8;
            var (_, overlay) = MakeTerrain(W, H);
            DigMarkOverlay.PublishHover(new List<(int, int, bool)> { (-1, 2, true), (2, H + 3, true), (4, 4, true) });
            overlay.Pump();
            var mesh = overlay.HoverMesh!;
            Assert.Greater(mesh.vertexCount, 0, "the in-bounds cell must still render");
            foreach (var v in mesh.vertices)
            {
                Assert.That(v.x, Is.InRange(3.4f, 5.6f), "only the in-bounds cell (4,4) may tessellate");
                Assert.That(v.z, Is.InRange(-5.6f, -3.4f));
            }
        }

        // The master toggle must flip the child renderers, not destroy the data.
        [Test]
        public void GlobalToggle_DisablesTheRenderers()
        {
            const int W = 8, H = 8;
            var (terr, overlay) = MakeTerrain(W, H);
            terr.SetDigMark(3, 3, owner: 0, cnt: 0, dummy: true);
            overlay.Pump();
            var marksGo = overlay.transform.Find("DigMarks");
            Assert.IsNotNull(marksGo);
            Assert.IsTrue(marksGo!.gameObject.activeSelf);
            try
            {
                DigMarkOverlay.GlobalEnabled = false;
                overlay.Pump();
                Assert.IsFalse(marksGo.gameObject.activeSelf, "showDigMarks=false must hide the mark object");
                Assert.Greater(overlay.MarksMesh!.vertexCount, 0, "the mesh data must survive the toggle");
            }
            finally { DigMarkOverlay.GlobalEnabled = true; }
        }
    }
}
