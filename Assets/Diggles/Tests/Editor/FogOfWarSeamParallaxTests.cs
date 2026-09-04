#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Bug: black cover edges cut across the lit back wall of deep caves — the fog gradient
    // showed on near-front surfaces (solid tops at 14.5) but was absent at cover seams over
    // deep cave interiors.
    //
    // Mechanism (parallax occlusion, not a shading/UV bug):
    // • fog UV sampling is exact: a fragment on lattice line x=i samples texel-i center =
    // the decoded byte of tile i (uv=(nat+0.5)/W vs texel centers (i+0.5)/W), identical
    // to the native per-corner bilerp (FUN_004cc040 over FUN_004ca830 corners);
    // • with black tile rows ≤ t−1 over an open cave and revealed rows ≥ t, the only
    // gradient carrier is row t−1's surface at its fill depth y≈7, while covers (rows
    // ≤ t−2, needing a 2×2 black corner block) used to sit on the front plane 14.53:
    // their bottom silhouette (lattice t−1) parallax-overhangs the y≈7 plane by
    // (14.53−7)·tan(pitch) ≈ 1.9 tiles at the default tilt (Dx=0.25 rad, camera law
    // eye=at+R(0,sin dx,cos dx)) … 5.3 tiles at 35° — the whole 1-tile gradient band
    // [t−1..t] lay hidden in the cover's shadow and the screen jumped cover-black →
    // fully-lit wall in one step;
    // • Fix (FogOfWarOverlay): covers sit at each tile's own fill surface
    // (nibble·0.5+7+CoverLift) — the native's own carrier position for open tiles
    // (FUN_004ca080 emits the mat-21/22 backdrop as per-tile template geometry at the
    // tile's own depths, fog-shaded per vertex). Shadow width collapses to
    // CoverLift·tan(pitch) ≈ 0.02 tile.
    //
    // This test builds a deep meshless cave with a black upper half and revealed lower half,
    // and marches an analytic 35°-pitch view-ray fan across the seam in screen space,
    // intersecting the actual overlay geometry and evaluating each hit's darkness (opaque
    // cover = 1; gradient quad = 1−G_bilinear of the fog field, the exact FowGradient shader
    // law; no hit over revealed void = 0). The law under test: the darkness profile across
    // the seam is a monotone gradient (many intermediate samples, no hard step) — pre-fix the
    // same fan read 1,1,…,1,0,0 (single-sample cliff).
    public sealed class FogOfWarSeamParallaxTests
    {
        const int W = 48, H = 64;
        const float Pitch = 0.6107f;              // 35° orbit tilt
        static readonly float K = Mathf.Tan(Pitch);

        GameObject? _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        [Test]
        public void DeepCaveSeam_ScreenSpaceDarkness_IsAGradient_NotACliff()
        {
            // World: solid rock (nibble 15) with a deep open cave (nibble 0, meshless)
            // at x∈[4..43], rows 20..40.
            var heights = new int[W * H];
            bool InCave(int x, int y) => x >= 4 && x <= 43 && y >= 20 && y <= 40;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    heights[y * W + x] = InCave(x, y) ? 0 : 15;
            var fog = new FogOfWarState(W, H, (x, y) => 15);
            _go = new GameObject("FowSeamHost");
            var ov = _go.AddComponent<FogOfWarOverlay>();
            ov.Init(fog,
                (x, y) => (uint)x < W && (uint)y < H ? heights[y * W + x] : 15,
                rendersAt: (x, y) => !InCave(x, y));

            // Reveal the cave's lower half: tiles rows 30..40 lit ⇒ lattice rows ≤29 black,
            // ≥30 lit. Boundary carrier = tile row 29 (mixed corners, meshless → patch@7);
            // covers over AllBlack cave rows 20..28.
            fog.RemoveBlackFog(0, 4, 30, 43, 40);
            ov.Pump();

            // ---- collect overlay quads (terrain-local frame) -------------------------------
            var opaque = QuadsOf(ov, 0);
            var grad = QuadsOf(ov, 1);

            // Anti-overhang invariant: every opaque horizontal cover sits at some tile's own
            // fill surface — over the cave strictly below 7.1, never on the front plane.
            foreach (var q in opaque)
            {
                if (Mathf.Abs(q.a.y - q.c.y) > 1e-4f) continue;              // wall, not cover
                float cxq = (Mathf.Min(q.a.x, q.c.x) + Mathf.Max(q.a.x, q.c.x)) * 0.5f;
                float cyq = -(Mathf.Min(q.a.z, q.c.z) + Mathf.Max(q.a.z, q.c.z)) * 0.5f;
                if (InCave((int)cxq, (int)cyq))
                    Assert.Less(q.a.y, 7.1f,
                        $"cave cover at ({cxq},{cyq}) must sit at the cave's own fill depth");
            }
            // Old law: full-depth side walls [7..14.53] closed every black cave edge facing
            // lit tiles. Flush open-to-open seams must emit none.
            Assert.IsFalse(opaque.Exists(q =>
                    Mathf.Abs(q.a.y - q.c.y) > 1f && InCave((int)q.a.x, (int)(-q.a.z))),
                "no full-depth black walls inside the open cave (flush seam)");

            // ---- the screen-space darkness profile across the seam -------------------------
            // View ray in the terrain-local frame at 35° pitch: per unit of depth (−y) the
            // ray advances +K in tileY (−K in local z). Entry point on the front plane
            // y=14.53 at (x=24.5, tileY=t_e). First-hit darkness law:
            // opaque quad → 1; gradient quad → 1−G_bilin(hit) (the FowGradient shader);
            // no hit → 0 (revealed meshless void).
            var profile = new List<float>();
            for (float te = 22.5f; te <= 26.0f; te += 0.05f)
                profile.Add(FirstHitDarkness(opaque, grad, fog, 24.5f, te));

            // Expected: covers over rows ≤28 now sit at y=7.03, the patch row 29 at y=7.0 —
            // a ray entering at t_e lands on the y≈7 plane at tileY ≈ t_e + (14.53−7)·K ≈
            // t_e+5.27: covers are hit while t_e+5.27 < 29 (te ≲ 23.7), the patch's gradient
            // band G∈(0,1) is swept while the landing crosses [29,30] (te ∈ ~[23.7,24.7]) —
            // a full tile of screen-space fade — and beyond it the revealed void reads 0.
            // Pre-fix the cover plane at 14.53 pushed its silhouette to the same landing row
            // 29+5.27 = 34.3 > 30: the fan jumped 1→0 in one sample.
            Assert.AreEqual(1f, profile[0], 1e-3f, "fan must start fully black (over covers/patch root)");
            Assert.AreEqual(0f, profile[profile.Count - 1], 1e-3f, "fan must end fully lit");
            int intermediates = profile.FindAll(v => v > 0.05f && v < 0.95f).Count;
            Assert.GreaterOrEqual(intermediates, 5,
                $"the seam must show a screen-space GRADIENT (≥5 intermediate samples), got {intermediates} " +
                $"[{string.Join(" ", profile.ConvertAll(v => v.ToString("0.00")))}]");
            float maxStep = 0f;
            for (int i = 1; i < profile.Count; i++)
            {
                Assert.LessOrEqual(profile[i], profile[i - 1] + 1e-3f,
                    "darkness must fall monotonically across the seam");
                maxStep = Mathf.Max(maxStep, profile[i - 1] - profile[i]);
            }
            Assert.Less(maxStep, 0.35f,
                $"no hard edge: max per-sample darkness step {maxStep:0.00} (pre-fix: 1.00)");
        }

        // First-hit darkness for the ray entering the front plane at (x, y=14.53, z=−te)
        // with direction (0, −1, −K) (unnormalized parameter s = depth travelled).
        static float FirstHitDarkness(
            List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d)> opaque,
            List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d)> grad,
            FogOfWarState fog, float x, float te)
        {
            const float Y0 = 14.53f;
            float bestS = float.MaxValue, bestDark = 0f;

            void Consider(List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d)> quads, bool isOpaque)
            {
                foreach (var (a, b, c, d) in quads)
                {
                    float x0 = Mathf.Min(a.x, Mathf.Min(b.x, Mathf.Min(c.x, d.x)));
                    float x1 = Mathf.Max(a.x, Mathf.Max(b.x, Mathf.Max(c.x, d.x)));
                    float y0 = Mathf.Min(a.y, Mathf.Min(b.y, Mathf.Min(c.y, d.y)));
                    float y1 = Mathf.Max(a.y, Mathf.Max(b.y, Mathf.Max(c.y, d.y)));
                    float z0 = Mathf.Min(a.z, Mathf.Min(b.z, Mathf.Min(c.z, d.z)));
                    float z1 = Mathf.Max(a.z, Mathf.Max(b.z, Mathf.Max(c.z, d.z)));
                    if (x < x0 || x > x1) continue;
                    float s, hz, hy;
                    if (y1 - y0 < 1e-4f)                       // horizontal (cover/patch)
                    {
                        s = Y0 - y0;
                        if (s <= 0f) continue;
                        hz = -te - K * s;
                        hy = y0;
                        if (hz < z0 - 1e-4f || hz > z1 + 1e-4f) continue;
                    }
                    else if (z1 - z0 < 1e-4f)                  // vertical, z-normal (wall/skirt)
                    {
                        s = (-te - z0) / K;
                        if (s <= 0f) continue;
                        hy = Y0 - s;
                        hz = z0;
                        if (hy < y0 - 1e-4f || hy > y1 + 1e-4f) continue;
                    }
                    else continue;                             // x-normal: ray runs parallel
                    if (s >= bestS) continue;
                    bestS = s;
                    bestDark = isOpaque ? 1f : 1f - BilinearG(fog, x, -hz);
                }
            }

            Consider(opaque, true);
            Consider(grad, false);
            return bestDark;
        }

        // The display texture's G channel under bilinear sampling: lattice point (i,j)
        // carries the black indicator of tile (i,j) (byte==0 → 0, else 1); fragments at
        // (nat.x, tileY) interpolate the 4 surrounding lattice points — exactly the shader's
        // (nat+0.5)/size sample of the corner-anchored RG16 texels.
        static float BilinearG(FogOfWarState fog, float natX, float tileY)
        {
            int i = Mathf.FloorToInt(natX), j = Mathf.FloorToInt(tileY);
            float fx = natX - i, fy = tileY - j;
            float g00 = fog.FogAt(i, j, 0) == 0 ? 0f : 1f;
            float g10 = fog.FogAt(i + 1, j, 0) == 0 ? 0f : 1f;
            float g01 = fog.FogAt(i, j + 1, 0) == 0 ? 0f : 1f;
            float g11 = fog.FogAt(i + 1, j + 1, 0) == 0 ? 0f : 1f;
            return (g00 * (1 - fx) + g10 * fx) * (1 - fy) + (g01 * (1 - fx) + g11 * fx) * fy;
        }

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
                    for (int k = 0; k + 5 < t.Length; k += 6)
                        list.Add((v[t[k]], v[t[k + 1]], v[t[k + 2]], v[t[k + 5]]));
                }
            return list;
        }
    }
}
