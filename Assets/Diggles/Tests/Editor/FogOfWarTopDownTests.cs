#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // From a top-down orbit, black fog renders as hard tile-staircase rectangles: the revealed
    // floor is fully lit right up to the black edges, with no gradient at all — while the same
    // partially revealed cave looks fine from the side (35°).
    //
    // Root cause (the state dumps in RealCave_NoStaleState_SideFanSmooth refute the
    // stale-cover and stale-display hypotheses): fog is a 2D field over (tileX,
    // tileY) — a vertical surface (overlay wall/skirt, or a black tile's port-rendered cliff
    // face) has its bilinear fog pinned to its own lattice line, one flat value over its
    // whole 7.5-unit depth extent. At orbit pitch p such a black sheet fills
    // ~tan(p)·(depth extent) tile-rows of screen landing — 28 rows at 75° — roofing the
    // revealed floor behind it in flat black with a razor edge at its silhouette (the
    // staircase = per-tile sheet/cover silhouettes). At 35° the same roof is ~5 rows hugging
    // the black mass — reads as its silhouette, "fine".
    //
    // A fix attempt was rejected in Play and reverted:
    // • DigglesGround clip(fow.g − 0.002) opened holes to the camera clear color wherever
    // an all-black tile still had meshed terrain;
    // • the view-ray-landing sheets produced heavy artifacts and hard angular seams
    // between the gradient band and the black mass.
    // The analytic ray-fan model below diverges from the real render (it composites ideal
    // quads; the real pipeline has depth-write ordering, alpha sorting, the clear-color
    // backdrop and meshed-vs-meshless coverage differences the model does not capture).
    // The render is back to the previous baseline (depth-positioned opaque
    // covers/walls/skirts): fine from the side, blocky from above — a known open issue.
    // Any next attempt must be verified with in-Play screenshots, not analytic fans —
    // hence the 75° fan law test is [Ignore]d, kept as documentation of the geometry.
    public sealed class FogOfWarTopDownTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        const float SidePitch = 0.6107f;    // 35° — the "looks fine" angle
        const float TopPitch = 1.31f;       // ~75° — near the native orbit clamp (√1.8≈1.34)

        GameObject? _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        // ================================================================================
        // An unexplored solid-rock mesa (rows 34..37) stands on a revealed cave floor.
        // Pre-fix its pinned-black wall+cliff roofed the revealed floor behind it (rows
        // 38..45) for ~28 landing-rows at 75°; post-fix the fan must show: lit floor →
        // 1-tile band → the mesa's own black footprint → the lit valley behind it —
        // with no hard step anywhere in the window.
        // ================================================================================
        [Test]
        [Ignore("Round-4 top-down fix REJECTED in Play (holes through the mesh, angular gradient seams) and reverted "
              + "to the 48ca314 render state. This analytic fan models the rejected sheets/discard laws and diverges "
              + "from the real render; re-enable only together with an in-Play screenshot verification method.")]
        public void BlackMesa_TopDownFan_ShowsTheLitFloorBehind_NoRoof()
        {
            const int W = 48, H = 64;
            var heights = new int[W * H];
            bool Mesa(int y) => y >= 34 && y <= 37;
            bool Meshless(int x, int y) => y <= 29 && !Mesa(y);   // cavern roofspace air
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    heights[y * W + x] = (y >= 46 || Mesa(y)) ? 15 : 0;

            var fog = new FogOfWarState(W, H, (x, y) => 15);
            _root = new GameObject("FowMesaHost");
            var pres = _root.AddComponent<FogOfWarPresenter>();
            pres.Init(fog);
            var ov = _root.AddComponent<FogOfWarOverlay>();
            ov.Init(fog,
                (x, y) => (uint)x < W && (uint)y < H ? heights[y * W + x] : 15,
                rendersAt: (x, y) => (uint)x < W && (uint)y < H && !Meshless(x, y),
                presenter: pres);

            // Reveal the floor around the mesa; the mesa itself stays unexplored.
            fog.RemoveBlackFog(0, 0, 30, W - 1, 33);
            fog.RemoveBlackFog(0, 0, 38, W - 1, 45);
            for (int i = 0; i < 12; i++) pres.Pump(0.1f);
            ov.Pump();

            var scene = CollectOverlay(ov);
            AddTerrain(scene, (x, y) => (uint)x < W && (uint)y < H && !Meshless(x, y),
                       (x, y) => (uint)x < W && (uint)y < H ? heights[y * W + x] : 15,
                       0, 0, W - 1, H - 1);
            float G(float nx, float ty) => BilinearG(pres, nx, ty);

            // The mesa wall must be a view-projected sheet (uv mode written by the overlay).
            Assert.IsTrue(scene.Exists(q => q.Kind == 2 && Mathf.Abs(q.Min.z - (-34.005f)) < 0.01f),
                "the mesa's north wall must be emitted as a view-projected sheet");

            float k = Mathf.Tan(TopPitch);
            var profile = new List<float>();
            var kinds = new StringBuilder();
            float te0 = 3.0f, te1 = 30.0f;
            for (float te = te0; te <= te1; te += 0.05f)
            {
                profile.Add(RayDarkness(scene, G, 24.5f, te, k, out char kc));
                kinds.Append(kc);
            }
            Dump("top-down 75°", te0, profile, kinds);

            float ValMin(float a, float b) => Sub(profile, te0, a, b).Min();
            float ValMax(float a, float b) => Sub(profile, te0, a, b).Max();
            Assert.Less(profile[0], 0.15f, "fan starts on lit floor");
            Assert.Greater(ValMax(6.0f, 9.0f), 0.85f, "the mesa's own footprint reads black");
            // The revealed floor behind the mesa must stay visible (pre-fix: the pinned-black
            // wall/cliff roofed it at 1.00 for 28 landing-rows — the staircase).
            Assert.Less(ValMin(10.5f, 16.0f), 0.15f,
                "the lit valley behind the mesa must show through (pre-fix: flat black roof)");
            // Both fog boundaries must be smooth gradients. The mesa's south-rim silhouette
            // (te ≈ 9.0..10.0 — through-rock landings against the floor 28 rows behind) is a
            // crisp occlusion edge, native-identical (the FUN_004c6b20 box edge), and is
            // deliberately excluded: fog cannot grade content 28 rows apart.
            float stepNear = MaxStep(Sub(profile, te0, 3.0f, 9.0f));
            float stepFar = MaxStep(Sub(profile, te0, 10.0f, 30.0f));
            Assert.Less(stepNear, 0.35f,
                $"near frontier must be a gradient: max step {stepNear:0.00} (pre-fix 1.00)");
            Assert.Less(stepFar, 0.35f,
                $"valley + far frontier must be gradients: max step {stepFar:0.00} (pre-fix 1.00)");
            int inter = profile.FindAll(v => v > 0.1f && v < 0.9f).Count;
            Assert.GreaterOrEqual(inter, 8, $"boundaries must be GRADIENTS, got {inter} intermediates");

            // Side pitch regression: the same scene stays smooth at 35° (window ends inside
            // the mesa footprint — its south-rim silhouette is the excluded occlusion edge).
            float k2 = Mathf.Tan(SidePitch);
            var prof2 = new List<float>();
            var kinds2 = new StringBuilder();
            float s0 = 26.5f, s1 = 32.5f;
            for (float te = s0; te <= s1; te += 0.05f)
            {
                prof2.Add(RayDarkness(scene, G, 24.5f, te, k2, out char kc));
                kinds2.Append(kc);
            }
            Dump("side 35°", s0, prof2, kinds2);
            Assert.Less(prof2[0], 0.15f, "side fan starts on lit floor");
            Assert.Greater(ValMax2(prof2, s0, 29.5f, 32.0f), 0.85f, "side: mesa footprint black");
            float maxStep2 = MaxStep(prof2);
            Assert.Less(maxStep2, 0.35f, $"side fan stays a gradient: max step {maxStep2:0.00}");

            float ValMax2(List<float> p, float t0, float a, float b) => Sub(p, t0, a, b).Max();
        }

        // ================================================================================
        // Real cave (caves_3.pmp): state dumps refute hypotheses A/B, and the 35° side fan
        // across the pocket frontier stays a smooth gradient.
        // ================================================================================
        [Test]
        public void RealCave_NoStaleState_SideFanSmooth()
        {
            _root = new GameObject("WorldRoot");
            _root.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
            var engine = new DigglesEngine();
            engine.Build(DataRoot, _root.transform, "BlockDB2.BDB");
            engine.Interp.Eval("map create 512 640");
            engine.Interp.Eval("set temppos {208 0}");
            try { engine.Interp.Eval("call templates/tcl/urw_unq_start.tcl"); }
            catch (System.Exception e) { TestContext.WriteLine($"[template] {e.Message}"); }
            engine.Interp.Eval("map_template caves_3.pmp 228 44");
            engine.Map.EmitTerrain(_root);
            var terr = _root.GetComponent<DiggableTerrain>();
            Assert.IsNotNull(terr, "no DiggableTerrain after EmitTerrain");

            var wr = engine.Map.WorldResult;
            Assert.IsNotNull(wr, "no WorldResult after EmitTerrain");
            bool CaveAir(int tx, int ty) =>
                tx >= wr!.RenderXMin && tx <= wr.RenderXMax &&
                ty >= wr.RenderYMin && ty <= wr.RenderYMax &&
                wr.BlockIdx[ty * wr.Width + tx] == 59 &&
                (wr.DugFloor == null || !wr.DugFloor[ty * wr.Width + tx]);
            (int cx, int cy) = (-1, -1);
            for (int ty = System.Math.Max(32, wr!.RenderYMin); ty <= wr.RenderYMax && cx < 0; ty++)
                for (int tx = wr.RenderXMin; tx <= wr.RenderXMax && cx < 0; tx++)
                {
                    if (!CaveAir(tx, ty)) continue;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if ((dx | dy) != 0 && CaveAir(tx + dx, ty + dy)) n++;
                    if (n >= 3) { cx = tx; cy = ty; }
                }
            Assert.GreaterOrEqual(cx, 0, "no cave pocket found");
            TestContext.WriteLine($"cave probe at ({cx},{cy})");

            var fog = new FogOfWarState(terr!.Width, terr.Height, terr.FogFillAt);
            var pres = _root.AddComponent<FogOfWarPresenter>();
            pres.Init(fog);
            var ov = _root.AddComponent<FogOfWarOverlay>();
            ov.Init(fog, terr.GetHmap, terr.RendersTerrain, pres);

            fog.BeginUpdate(0);
            fog.RemoveFog(cx * 8 + 4, cy * 8 + 4, 5 * 8, 5 * 8, 0, 0f, 1f);
            fog.EndUpdate();
            for (int i = 0; i < 12; i++) pres.Pump(0.1f);
            ov.Pump();

            // ---- hypothesis A/B invariants (the state-dump proof) --------------------------
            var scene = CollectOverlay(ov);
            var staleCovers = new List<(int x, int y)>();
            var staleTexels = new List<(int x, int y)>();
            for (int ty = cy - 12; ty <= cy + 12; ty++)
                for (int tx = cx - 12; tx <= cx + 12; tx++)
                {
                    bool allBlack = fog.FogAt(tx, ty, 0) == 0 && fog.FogAt(tx + 1, ty, 0) == 0
                                 && fog.FogAt(tx, ty + 1, 0) == 0 && fog.FogAt(tx + 1, ty + 1, 0) == 0;
                    if (!allBlack && HasCoverOver(scene, tx, ty)) staleCovers.Add((tx, ty));
                    if ((uint)tx < terr.Width && (uint)ty < terr.Height)
                    {
                        byte b = fog.FogAt(tx, ty, 0);
                        byte expG = b == 0 ? (byte)0 : (byte)255;
                        if (pres.DisplayAt(tx, ty).g != expG || pres.TargetAt(tx, ty).g != expG)
                            staleTexels.Add((tx, ty));
                    }
                }
            TestContext.WriteLine($"stale covers: {staleCovers.Count} {string.Join(" ", staleCovers)}");
            TestContext.WriteLine($"stale texels: {staleTexels.Count} {string.Join(" ", staleTexels)}");
            Assert.IsEmpty(staleCovers, "hypothesis A: covers over not-all-black tiles");
            Assert.IsEmpty(staleTexels, "hypothesis B: display/target G disagree with the sim byte");

            // ---- 35° side fan across the pocket's frontier ---------------------------------
            AddTerrain(scene, terr.RendersTerrain, terr.GetHmap, cx - 14, cy - 26, cx + 14, cy + 26);
            float G(float nx, float ty) => BilinearG(pres, nx, ty);
            float k = Mathf.Tan(SidePitch);
            var prof = new List<float>();
            var kinds = new StringBuilder();
            // End on the pocket's lit rim tops (rows cy−3..cy−1, penetration-revealed rock):
            // rays entering over the pocket air itself overshoot at 35° onto the far black
            // covers — an undug block-59 interior has no floor mesh, correctly dark.
            float t0 = cy - 10f, t1 = cy - 1f;
            for (float te = t0; te <= t1; te += 0.05f)
            {
                prof.Add(RayDarkness(scene, G, cx + 0.5f, te, k, out char kc));
                kinds.Append(kc);
            }
            Dump("real side 35°", t0, prof, kinds);
            Assert.AreEqual(1f, prof[0], 0.02f, "side fan starts black (unexplored rock)");
            Assert.Less(prof[prof.Count - 1], 0.35f, "side fan ends substantially lit");
            float maxStep = MaxStep(prof);
            Assert.Less(maxStep, 0.35f, $"side fan stays a gradient (max step {maxStep:0.00})");
        }

        // ================= analytic scene ==================================================

        // Axis-aligned shadeable quad mirroring the runtime shader laws:
        // kind 0 cover — opaque black;
        // kind 1 patch — alpha = 1−G(own hit position);
        // kind 2 sheet — alpha = 1−G(view-ray landing on zBase, offset clamped to 2 tiles);
        // kind 3 top — terrain: discarded when G(own)≈0 (the native no-mesh law), else
        // opaque, darkness = 1−G(own);
        // kind 4 cliff — terrain face on a lattice line: discarded when G(x, lattice)≈0,
        // else opaque, darkness = 1−G(x, lattice).
        struct Q
        {
            public Vector3 Min, Max;
            public int Kind;
            public float ZBase;     // kind 2
            public float Lattice;   // kind 4
        }

        static void AddTerrain(List<Q> scene, System.Func<int, int, bool> renders,
                               System.Func<int, int, int> hmap, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!renders(x, y)) continue;
                    float top = hmap(x, y) * 0.5f + 7f;
                    scene.Add(new Q
                    {
                        Min = new Vector3(x, top, -(y + 1)),
                        Max = new Vector3(x + 1, top, -y),
                        Kind = 3,
                    });
                    float sn = renders(x, y + 1) ? hmap(x, y + 1) * 0.5f + 7f : 7f;
                    if (top > sn + 0.01f)
                        scene.Add(new Q
                        {
                            Min = new Vector3(x, sn, -(y + 1)),
                            Max = new Vector3(x + 1, top, -(y + 1)),
                            Kind = 4, Lattice = y + 1,
                        });
                    float nn = renders(x, y - 1) ? hmap(x, y - 1) * 0.5f + 7f : 7f;
                    if (top > nn + 0.01f)
                        scene.Add(new Q
                        {
                            Min = new Vector3(x, nn, -y),
                            Max = new Vector3(x + 1, top, -y),
                            Kind = 4, Lattice = y,
                        });
                }
        }

        // Overlay meshes → quads; the shader mode is read back from the mesh UVs (certifies
        // the overlay actually wrote the sheet markers).
        static List<Q> CollectOverlay(FogOfWarOverlay ov)
        {
            var list = new List<Q>();
            for (int cy = 0; cy < ov.ChunksY; cy++)
                for (int cx = 0; cx < ov.ChunksX; cx++)
                {
                    var m = ov.ChunkMesh(cx, cy);
                    if (m == null || m.vertexCount == 0) continue;
                    var v = m.vertices;
                    var uv = m.uv;
                    for (int sub = 0; sub < m.subMeshCount; sub++)
                    {
                        var t = m.GetTriangles(sub);
                        for (int i = 0; i + 5 < t.Length; i += 6)
                        {
                            var a = v[t[i]]; var b = v[t[i + 1]]; var c = v[t[i + 2]]; var d = v[t[i + 5]];
                            var q = new Q
                            {
                                Min = Vector3.Min(Vector3.Min(a, b), Vector3.Min(c, d)),
                                Max = Vector3.Max(Vector3.Max(a, b), Vector3.Max(c, d)),
                            };
                            if (sub == 0) q.Kind = 0;
                            else if (uv.Length > t[i] && uv[t[i]].x >= 0.5f)
                            {
                                q.Kind = 2;
                                q.ZBase = uv[t[i]].y;
                            }
                            else q.Kind = 1;
                            list.Add(q);
                        }
                    }
                }
            return list;
        }

        // March the ray entering the plane y=14.6 at (x, tileY=te), direction (0,−1,−K) per
        // unit depth; composite alpha-black front-to-back; opaque surfaces end the ray; no
        // hit at all = black (the cave background).
        static float RayDarkness(List<Q> scene, System.Func<float, float, float> G,
                                 float x, float te, float k, out char kind)
        {
            const float Y0 = 14.6f;
            kind = 'b';
            var hits = new List<(float s, float dark, bool opaque, char kc)>();
            foreach (var q in scene)
            {
                if (x < q.Min.x - 1e-4f || x > q.Max.x + 1e-4f) continue;
                float s, hy, hz;
                if (q.Max.y - q.Min.y < 1e-4f)            // horizontal
                {
                    s = Y0 - q.Min.y;
                    if (s <= 1e-4f) continue;
                    hy = q.Min.y; hz = -te - k * s;
                    if (hz < q.Min.z - 1e-4f || hz > q.Max.z + 1e-4f) continue;
                }
                else if (q.Max.z - q.Min.z < 1e-4f)       // vertical, z-normal
                {
                    s = (-te - q.Min.z) / k;
                    if (s <= 1e-4f) continue;
                    hy = Y0 - s; hz = q.Min.z;
                    if (hy < q.Min.y - 1e-4f || hy > q.Max.y + 1e-4f) continue;
                }
                else continue;
                float dark; char kc; bool opaque;
                switch (q.Kind)
                {
                    case 0: dark = 1f; kc = 'C'; opaque = true; break;
                    case 1: dark = 1f - G(x, -hz); kc = 'P'; opaque = false; break;
                    case 2:
                    {
                        float dy = Mathf.Min(k * (hy - q.ZBase), 64f);  // ray landing (degenerate guard)
                        dark = 1f - G(x, -hz + dy); kc = 'S'; opaque = false; break;
                    }
                    case 3:
                    {
                        float g = G(x, -hz);
                        if (g < 0.002f) continue;                       // native no-mesh discard
                        dark = 1f - g; kc = 'T'; opaque = true; break;
                    }
                    default:
                    {
                        float g = G(x, q.Lattice);
                        if (g < 0.002f) continue;                       // native no-mesh discard
                        dark = 1f - g; kc = 'F'; opaque = true; break;
                    }
                }
                hits.Add((s, dark, opaque, kc));
            }
            hits.Sort((a, b) => a.s.CompareTo(b.s));
            float transmit = 1f, total = 0f;
            foreach (var (s, dark, opq, kc) in hits)
            {
                if (opq) { total += transmit * dark; kind = kc; return total; }
                total += transmit * dark;
                transmit *= 1f - dark;
                if (transmit < 1e-3f) { kind = kc; return total; }
            }
            return total + transmit * 1f;
        }

        // Display-texture G under bilinear sampling (the exact shader law).
        static float BilinearG(FogOfWarPresenter pres, float natX, float tileY)
        {
            int i = Mathf.FloorToInt(natX), j = Mathf.FloorToInt(tileY);
            float fx = natX - i, fy = tileY - j;
            float G(int a, int b) => pres.DisplayAt(a, b).g / 255f;
            return (G(i, j) * (1 - fx) + G(i + 1, j) * fx) * (1 - fy)
                 + (G(i, j + 1) * (1 - fx) + G(i + 1, j + 1) * fx) * fy;
        }

        static bool HasCoverOver(List<Q> scene, int tx, int ty)
        {
            float px = tx + 0.5f, pz = -(ty + 0.5f);
            foreach (var q in scene)
            {
                if (q.Kind != 0 || q.Max.y - q.Min.y > 1e-3f) continue;
                if (px >= q.Min.x && px <= q.Max.x && pz >= q.Min.z && pz <= q.Max.z) return true;
            }
            return false;
        }

        static List<float> Sub(List<float> profile, float te0, float a, float b)
        {
            int i0 = Mathf.Clamp(Mathf.RoundToInt((a - te0) / 0.05f), 0, profile.Count - 1);
            int i1 = Mathf.Clamp(Mathf.RoundToInt((b - te0) / 0.05f), 0, profile.Count - 1);
            return profile.GetRange(i0, i1 - i0 + 1);
        }

        static float MaxStep(List<float> profile)
        {
            float m = 0f;
            for (int i = 1; i < profile.Count; i++)
                m = Mathf.Max(m, Mathf.Abs(profile[i] - profile[i - 1]));
            return m;
        }

        static void Dump(string label, float te0, List<float> profile, StringBuilder kinds)
        {
            var sb = new StringBuilder($"[{label}] profile:\n");
            for (int i = 0; i < profile.Count; i += 5)
                sb.Append($"{te0 + i * 0.05f:0.0}:{profile[i]:0.00}{kinds[i]} ");
            TestContext.WriteLine(sb.ToString());
        }
    }
}
