#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Pins the native panorama/backdrop laws (Docs/BACKDROP_PARALLAX_RE.md) — the constant
    // tables extracted from Diggles.exe's float pool, the FUN_004802c0 parallax UV law, the
    // FUN_00480ee0:38 surface gate, the FUN_00480720 ground-carpet grid, the FUN_004750b0
    // horizon-zone pick and the FUN_00484cd0:82-104 rear-skirt fade. These tests cover the
    // math only, not the meshes and shaders that present it.
    public sealed class BackdropLawsTests
    {
        // ---- constant tables (FUN_0047edc0:257-288; exe.rdata values, RE doc §3) ----

        [Test]
        public void LayerTables_MatchTheNativeConstantPool()
        {
            Assert.AreEqual(6, BackdropLaws.LayerCount);
            CollectionAssert.AreEqual(
                new[] { 21100f, 20500f, 11300f, 1000f, 200f, 100f }, BackdropLaws.LayerDepth,
                "layer depths (DAT_00737510/508/4f0/4c0/478/458)");
            CollectionAssert.AreEqual(
                new[] { 0.53f, 0.04f, 0.11f, 1.12f, 6.68f, 18.0f }, BackdropLaws.LayerScale,
                "layer scales (DAT_00737248/178/1b8/2b8/3c8/410)");
            CollectionAssert.AreEqual(
                new[] { 0f, 0f, 0f, 0f, 0.7f, 1.0f }, BackdropLaws.LayerOffU,
                "layer U offsets (0x14c8 table)");
            CollectionAssert.AreEqual(
                new[] { 0.75f, 1.0f, 0.5f, -4.9f, -33.0f, -90.25f }, BackdropLaws.LayerOffV,
                "layer V offsets (0x14f8 table)");
            Assert.AreEqual(0.00075f, BackdropLaws.Ku, 0f, "_DAT_007370c8");
            Assert.AreEqual(0.0015f, BackdropLaws.Kv, 0f, "_DAT_007370f0");
            Assert.AreEqual(5.0f, BackdropLaws.UvBias, 0f, "DAT_007373a8");
            Assert.AreEqual(1.0f, BackdropLaws.DepthBias, 0f, "DAT_007372a8");
        }

        // ---- parallax UV law (FUN_004802c0 + FUN_0047f440:139/166) ----

        [Test]
        public void LayerUv_MatchesTheNativeFormula()
        {
            // Synthetic ray in the render frame: near (100, 200, 30) → far (110, 190, -150).
            var near = new Vector3(100f, 200f, 30f);
            var far = new Vector3(110f, 190f, -150f);
            const int layer = 5;   // D=100, S=18, offU=1.0, offV=-90.25

            float dz = far.z - near.z;                       // -180
            float slopeX = (far.x - near.x) / dz;            // 10 / -180
            float slopeY = (far.y - near.y) / dz;            // -10 / -180
            float dp = 100f - 1f;                            // d' = D - 1
            float uCore = 0.00075f * (slopeX * dp - near.x) + 5f;
            float vCore = -0.0015f * (slopeY * dp - near.y) + 5f;
            var expected = new Vector2(18f * uCore + 1.0f, 18f * vCore - 90.25f);

            Vector2 uv = BackdropLaws.LayerUv(layer, near, far);
            Assert.AreEqual(expected.x, uv.x, 1e-4f, "u = S*(Ku*(slopeX*(D-1) - nearX) + 5) + offU");
            Assert.AreEqual(expected.y, uv.y, 1e-4f, "v = S*(-Kv*(slopeY*(D-1) - nearY) + 5) + offV");
        }

        [Test]
        public void LayerUv_DistantLayersParallaxLessThanNearOnes()
        {
            // Pan the camera +10 east (both ray points shift together, direction unchanged):
            // the texture-space shift is S*Ku*10 — near layer 5 (S=18) must move far more than
            // far layer 0 (S=0.53). This is the parallax: du/dpan = -S*Ku (nearX enters with -).
            var nearA = new Vector3(100f, 200f, 30f);
            var farA = new Vector3(110f, 190f, -150f);
            var nearB = nearA + new Vector3(10f, 0f, 0f);
            var farB = farA + new Vector3(10f, 0f, 0f);

            float duFar = Mathf.Abs(BackdropLaws.LayerUv(0, nearB, farB).x - BackdropLaws.LayerUv(0, nearA, farA).x);
            float duNear = Mathf.Abs(BackdropLaws.LayerUv(5, nearB, farB).x - BackdropLaws.LayerUv(5, nearA, farA).x);
            Assert.AreEqual(0.53f * 0.00075f * 10f, duFar, 1e-5f);
            Assert.AreEqual(18f * 0.00075f * 10f, duNear, 1e-5f);
            Assert.Greater(duNear, duFar * 20f, "near layer must scroll much faster than the far layer");
        }

        [Test]
        public void LayerUv_CounterPan_OppositeSign_Near34xFar()
        {
            // The counter-pan law (FUN_004802c0: uCore = Ku*(slopeX*d' - x_near) + 5 — x_near
            // enters negated): panning the camera +p east shifts every layer's u by exactly
            // -S*Ku*p (signed), so near layer 5 / far layer 0 = S5/S0 = 18/0.53 ≈ 33.96 ("34x").
            var near = new Vector3(100f, 200f, 30f);
            var far = new Vector3(110f, 190f, -150f);
            const float pan = 10f;
            var pankit = new Vector3(pan, 0f, 0f);

            float du0 = BackdropLaws.LayerUv(0, near + pankit, far + pankit).x
                      - BackdropLaws.LayerUv(0, near, far).x;
            float du5 = BackdropLaws.LayerUv(5, near + pankit, far + pankit).x
                      - BackdropLaws.LayerUv(5, near, far).x;
            Assert.AreEqual(-0.53f * 0.00075f * pan, du0, 1e-6f, "du = -S*Ku*pan (far layer)");
            Assert.AreEqual(-18f * 0.00075f * pan, du5, 5e-5f, "du = -S*Ku*pan (near layer)");
            Assert.AreEqual(18f / 0.53f, du5 / du0, 0.05f, "near/far ratio ≈ 33.96 (same sign)");

            // Screen direction: for the downward-looking camera (render Δz < 0), u decreases
            // toward the +x (east) side of the screen — an east-diverging ray has more-negative
            // slopeX. u decreasing across screen-x plus du<0 under a +x pan ⇒ a fixed texture
            // feature moves toward smaller screen x: the backdrop slides opposite to the pan.
            var nearC = new Vector3(100f, 200f, 30f);          // center column ray
            var farC = new Vector3(100f, 190f, -150f);
            var nearE = new Vector3(100.2f, 200f, 30f);        // east-of-center column ray
            var farE = new Vector3(120f, 190f, -150f);
            float uC = BackdropLaws.LayerUv(5, nearC, farC).x;
            float uE = BackdropLaws.LayerUv(5, nearE, farE).x;
            Assert.Less(uE, uC, "u decreases toward the east side of the screen");
        }

        // ---- root-local ↔ native render frame (RuntimeWorldSpawner.NativeToLocal inverse) ----

        [Test]
        public void RenderFrame_IsTheRootLocalFrame_TileHeightsCompressInto()
        {
            // Tile/entity z is a nibble (0..15); NativeToLocal compresses it into local y
            // (z*0.5+7 → terrain 7..14.5). The render frame the view matrices (and therefore
            // the panorama rays, the carpet and the rear fade) consume is local itself up to
            // the row sign — RenderFromLocal is a pure axis swap, no rescale. Evidence: the
            // horizon clip line uses front plane z = +14.5 (DAT_00737564), the carpet's near
            // row +7.5 hugs the deepest plane 7, the brightness knee is 7.0 (DAT_00737520),
            // and DigglesCameraController.NativeToWorldPoint is the same plain swap.
            var nibble = new Vector3(123.4f, 56.7f, 9.5f);   // (tileX, depthRow, height nibble)
            Vector3 local = Diggles.Runtime.RuntimeWorldSpawner.NativeToLocal(nibble);
            Vector3 render = BackdropLaws.RenderFromLocal(local);
            Assert.AreEqual(nibble.x, render.x, 1e-4f, "render x = native tileX");
            Assert.AreEqual(nibble.y, render.y, 1e-4f, "render y = native depth row");
            Assert.AreEqual(nibble.z * 0.5f + 7f, render.z, 1e-4f,
                "render z = the COMPRESSED height in metres (nibble*0.5+7), not the raw nibble");

            Vector3 back = BackdropLaws.LocalFromRender(render);
            Assert.AreEqual(local.x, back.x, 1e-4f);
            Assert.AreEqual(local.y, back.y, 1e-4f);
            Assert.AreEqual(local.z, back.z, 1e-4f);

            // Landmarks of the frame: deepest dug floor (nibble 0) → render z 7; undug front
            // plane (nibble 15) → 14.5; the camera at-height 14 is the affine's fixed point.
            Assert.AreEqual(7f, BackdropLaws.RenderFromLocal(new Vector3(0f, 7f, 0f)).z, 1e-5f);
            Assert.AreEqual(14.5f, BackdropLaws.RenderFromLocal(new Vector3(0f, 14.5f, 0f)).z, 1e-5f);
            Assert.AreEqual(14f, Diggles.Runtime.RuntimeWorldSpawner.NativeToLocal(new Vector3(0f, 0f, 14f)).y, 1e-5f);
        }

        [Test]
        public void HorizonFade_ThresholdSitsSevenMetresBehindTheDeepestFloor()
        {
            // FUN_00484cd0:82 fades only render z < 0 — local y < 0, i.e. seven metres behind
            // the deepest dug floor plane (7). Terrain never fades (the BDB vertex palette
            // spans z ∈ [7, 14.5] exactly); the law's live targets are authored backdrop
            // models via the FUN_004856e0 twin.
            const float depth = 32f;   // the Info_Fog_* scripts all pass 32
            Assert.AreEqual(0f, BackdropLaws.HorizonFade(7f, depth), "deepest floor plane — untouched");
            Assert.AreEqual(0f, BackdropLaws.HorizonFade(0.5f, depth), "just above the threshold");
            Assert.AreEqual(0.5f, BackdropLaws.HorizonFade(-16f, depth), 1e-5f, "half depth");
            Assert.AreEqual(1f, BackdropLaws.HorizonFade(-32f, depth), 1e-5f, "full zone colour at -depth");
            Assert.AreEqual(0f, BackdropLaws.HorizonFade(14.5f, depth), "front plane untouched");
        }

        // ---- surface gate (FUN_00480ee0:38-39, DAT_00737578 = 20) ----

        [Test]
        public void SurfaceGate_OpensWithinTwentyRowsOfTheSurface()
        {
            Assert.IsTrue(BackdropLaws.SurfaceVisible(30f, 30f), "at the surface line");
            Assert.IsTrue(BackdropLaws.SurfaceVisible(50f, 30f), "exactly 20 below (<=)");
            Assert.IsFalse(BackdropLaws.SurfaceVisible(50.01f, 30f), "deeper than 20 rows");
            Assert.IsTrue(BackdropLaws.SurfaceVisible(5f, 30f), "above the surface");
        }

        // ---- ground carpet (FUN_00480720) ----

        [Test]
        public void Carpet_RowDepthsAndLiftsMatchTheNativeGrid()
        {
            // row z = (row*7.5 - (3-row)*60)/3 (:158); y lifts +0.75/+0.25 on the far rows (:163-170).
            CollectionAssert.AreEqual(new[] { -60f, -37.5f, -15f, 7.5f }, BackdropLaws.CarpetRowZ);
            CollectionAssert.AreEqual(new[] { 0.75f, 0.25f, 0f, 0f }, BackdropLaws.CarpetRowYOff);

            const float ground = 40f;
            var v = BackdropLaws.CarpetVertex(chunkX: 200f, c: 3, r: 0, groundRow: ground);
            Assert.AreEqual(200f + 20f, v.x, 1e-4f, "chunk spans 20 units over 3 cells");
            Assert.AreEqual(ground - 2.5f + 0.75f, v.y, 1e-4f, "y = ground - 2.5 + far-row lift");
            Assert.AreEqual(-60f, v.z, 1e-4f, "far row depth");
        }

        [Test]
        public void Carpet_SnapsToTwentyUnitChunksAroundTheFocus()
        {
            Assert.AreEqual(240f, BackdropLaws.CarpetSnap(246.34f), 1e-4f);
            // half-to-even (Mathf.Round == the x87 default rounding the native helper runs under)
            Assert.AreEqual(240f, BackdropLaws.CarpetSnap(250f), 1e-4f);
            Assert.AreEqual(0f, BackdropLaws.CarpetSnap(9.99f), 1e-4f);
        }

        // ---- horizon-zone pick (FUN_004750b0:118-127) ----

        static HorizonRegionState Zone(float x, float y, float size, int r = 10, int g = 20, int b = 30)
            => new(new Vector3(x, y, 13f), r, g, b, 32f, size);

        [Test]
        public void PickZone_NearestInRangeWins_RadiusIsExclusive()
        {
            var zones = new List<HorizonRegionState>
            {
                Zone(0f, 0f, 40f),      // dist 50 from focus → out (size 40)
                Zone(100f, 40f, 70f),   // dist 50 → in
                Zone(45f, 30f, 70f),    // dist 5 → in, nearest
            };
            Assert.AreEqual(2, BackdropLaws.PickZone(zones, 40f, 30f));
            Assert.AreEqual(-1, BackdropLaws.PickZone(new List<HorizonRegionState>(), 40f, 30f));
            // dist == size is not inside (native `dist < size`, strict)
            var edge = new List<HorizonRegionState> { Zone(40f, 0f, 40f) };
            Assert.AreEqual(-1, BackdropLaws.PickZone(edge, 0f, 0f));
            Assert.AreEqual(0, BackdropLaws.PickZone(new List<HorizonRegionState> { Zone(39.9f, 0f, 40f) }, 0f, 0f));
        }

        // ---- rear-skirt fade (FUN_00484cd0:82-104) ----

        [Test]
        public void HorizonFade_RampsOnlyBehindTheDeepestPlane()
        {
            const float depth = 32f;   // the Info_Fog_* scripts all pass 32
            Assert.AreEqual(0f, BackdropLaws.HorizonFade(5f, depth), "in front of the plane — no fade");
            Assert.AreEqual(0f, BackdropLaws.HorizonFade(0f, depth), "on the plane — no fade");
            Assert.AreEqual(0.5f, BackdropLaws.HorizonFade(-16f, depth), 1e-5f, "half depth");
            Assert.AreEqual(1f, BackdropLaws.HorizonFade(-32f, depth), 1e-5f, "full colour at -depth");
            Assert.AreEqual(1f, BackdropLaws.HorizonFade(-64f, depth), 1e-5f, "clamped past -depth");
            Assert.AreEqual(0f, BackdropLaws.HorizonFade(-64f, 0f), "no active zone → law off (FUN_00484cd0:83)");
        }

        // ---- air-corner luminance (the "earth frame" law, RE doc §4b) ----
        // Native: FUN_004ca830 zeroes the luminance byte [0x4d2..0x4d5] of every corner node
        // with tile state 3 (backdrop hole), and FUN_004cc040:55-57 blends the four bytes
        // bilinearly into each vertex — the terrain fades dark over its last half-tile toward
        // every rear-cutout window. The mesher bakes that bilinear into vertex COLOR.a.

        [Test]
        public void CutoutAlpha_BakesTheStencilRampTowardStateThreeNodes()
        {
            // The corner byte is 0 at a tile+0x18 state-3 node and 255 otherwise; the mesher
            // bakes the bilinear into COLOR.a and DigglesGround alpha-tests at the 126.5
            // isoline (no blend, no luminance). The open predicate is the state word — the
            // authored terrain byte 5 — never blockIdx==59 (the cutout ring lies on solid).
            string dataRoot = OriginalDataTestFixture.DataRoot;
            var bdb = BdbParser.ParseCached(System.IO.Path.Combine(dataRoot, "BlockDB2.BDB"));
            const int W = 6, H = 6;
            var r = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = W,
                Height = H,
                BlockIdx = new uint[W * H],          // all block 0 = full solid block
                Terrain = new byte[W * H],
                CellId = new ushort[W * H],
                RenderXMin = 0, RenderYMin = 0, RenderXMax = W - 1, RenderYMax = H - 1,
            };
            for (int i = 0; i < W * H; i++) r.Terrain[i] = 4;   // solid rock class → state 0
            r.BlockIdx[3 * W + 3] = BackdropLaws.AirBlock;   // one backdrop-hole node at (3,3)
            r.Terrain[3 * W + 3] = 5;                        // authored FILE byte 5 → state 3

            var root = new GameObject("RimLawTestRoot");
            try
            {
                var grid = new BdbPmpWorldBuilder.TerrainChunkGrid(root, bdb, r, dataRoot);
                r.Chunks = grid;
                grid.MarkAllDirty();
                Assert.Greater(grid.RebuildDirty(), 0, "the synthetic world must emit chunks");

                float AlphaAt(float wx, float wz)
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                    {
                        var mesh = mf.sharedMesh;
                        if (mesh == null) continue;
                        var vs = mesh.vertices;
                        var cs = mesh.colors;
                        if (cs == null || cs.Length != vs.Length) continue;
                        for (int i = 0; i < vs.Length; i++)
                            if (Mathf.Abs(vs[i].x - wx) < 1e-3f && Mathf.Abs(vs[i].z - wz) < 1e-3f)
                                return cs[i].a;
                    }
                    return float.NaN;
                }

                // Cell (2,2): its SE corner is the hole node (3,3) → luminance 0 there,
                // full light at its own solid NW corner — the native half-tile dark ramp.
                Assert.AreEqual(1f, AlphaAt(2f, -2f), 1e-3f, "solid corner node stays lit");
                Assert.AreEqual(0f, AlphaAt(3f, -3f), 1e-3f,
                    "hole corner node → luminance 0 (native [0x4d2..0x4d5]=0)");
                Assert.AreEqual(1f, AlphaAt(0f, 0f), 1e-3f, "terrain away from the hole untouched");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---- ground-row derivation (port-side surface line) ----

        [Test]
        public void ComputeGroundRow_DeepestAirPlusSurfaceOffset_OrMinValueWithoutSky()
        {
            const int w = 4, h = 9;
            var blocks = new uint[w * h];           // all solid (block 0)
            Assert.AreEqual(int.MinValue, BackdropLaws.ComputeGroundRow(blocks, w, h), "no sky bowl");

            blocks[1 * w + 2] = BackdropLaws.AirBlock;
            blocks[3 * w + 0] = BackdropLaws.AirBlock;   // deepest air row = 3
            Assert.AreEqual(4, BackdropLaws.SurfaceLineFromDeepestAir,
                "carpet (ground-2.5) = deepestAir+1.5 → half a row under the floor face at deepestAir+1");
            Assert.AreEqual(3 + 4, BackdropLaws.ComputeGroundRow(blocks, w, h),
                "surface line = deepest air row + the empirical offset");
        }
    }
}
