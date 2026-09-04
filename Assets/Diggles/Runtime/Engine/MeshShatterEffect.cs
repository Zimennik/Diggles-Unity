#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    // ================== THE MESH SHATTER — faithful MeshDestructor port (rounds 6-7) ==================
    //
    // — confirmed by RE: the native MeshDestructor
    // (Engine/MeshDestructor.cpp) does NOT emit point sprites. FUN_005eae80 → FUN_004845a0
    // (current-frame resolve FUN_004059a0 + frustum gate + shatter sound FUN_00453f00) →
    // FUN_004830a0: decode the frame's 16-bit vertex buffer and world-transform it with a small
    // random jitter (:289-303), walk the INDEX BUFFER per triangle (:32), adaptively
    // subdivide large triangles (FUN_00482950 — recursive split while the edges exceed the
    // DAT_00736fe0 threshold) and emit REAL TRIANGLE SHARDS that keep the model's
    // material/texture (model+0x80), UVs and per-vertex palette colours (:314-327); per-shard
    // random symmetric velocity (:167-172 — GENTLE: player-confirmed native look is 
    //,, gravity-dominant, not an
    // explosion; the raw magnitudes hide behind model-scale factors like DAT_00737034, so the
    // defaults here are tuned to that description), gravity DAT_00737058, ground clamp +
    // bounce, freed when settled (FUN_005ea850 — physics, not a timer).
    //
    // Port: MeshShatterBuilder partitions the source triangles into fragments
    // (per-triangle; midpoint-4-split of oversized ones — area-preserving; UNIFORM stride
    // subset when the mesh is denser than FragmentCap so creatures shatter across the whole
    // body, not the first N index-order triangles); alpha-cutout foliage drops its fully-
    // transparent shards (texture alpha at the fragment's UV centroid — moss breaks into its
    // visible sprigs, fixed). MeshShatterEffect renders ALL fragments
    // as ONE dynamic mesh with the model's own sharedMaterial (one draw call) and integrates
    // them on the CPU at a fixed step driven by GAME time (DigglesAnimator.GlobalTimeScale —
    // the same scaled clock the animators consume: x10 fast-forward flies 10×, pause freezes
    // shards mid-air). Ground is PER-FRAGMENT: one raycast at spawn caches each shard's own
    // ground Y (uneven terrain — no hovering, no sinking); a shard with NO ground beneath
    // keeps falling and shrinks away after a short flight (pit law). Invoked from
    // DigglesTclEngine.ShatterEntity (deco removal now; the same public seam is the
    // creature-death hook — combat NOT wired by design). All magnitudes = Play-tunable statics.

    public struct ShatterFragment
    {
        public Vector3 A, B, C;        // world-space corners
        public Vector2 Ua, Ub, Uc;     // ORIGINAL model UVs (the texture pieces fly apart)
        public int SourceTri;          // index-buffer triangle this fragment came from
    }

    public static class MeshShatterBuilder
    {
        // Partition the triangle list into shards: one fragment per triangle, oversized
        // triangles midpoint-4-split until every edge fits the threshold (or depth/cap runs
        // out) — the FUN_00482950 law with a simplified split selector. When the source has
        // more triangles than `cap`, a UNIFORM stride subset is taken (dense creature meshes
        // must shatter across the whole body) and splitting is skipped (those triangles are
        // already small).
        public static List<ShatterFragment> Build(Vector3[] verts, int[] tris, Vector2[]? uvs,
            float splitThreshold, int maxDepth, int cap)
        {
            var frags = new List<ShatterFragment>();
            int triCount = tris.Length / 3;
            bool dense = triCount > cap;
            float stride = dense ? triCount / (float)cap : 1f;
            for (float ft = 0f; ft < triCount && frags.Count < cap; ft += stride)
            {
                int t = (int)ft;
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                Vector2 u0 = uvs != null && i0 < uvs.Length ? uvs[i0] : Vector2.zero;
                Vector2 u1 = uvs != null && i1 < uvs.Length ? uvs[i1] : Vector2.zero;
                Vector2 u2 = uvs != null && i2 < uvs.Length ? uvs[i2] : Vector2.zero;
                Split(frags, verts[i0], verts[i1], verts[i2], u0, u1, u2, t,
                    dense ? 0 : maxDepth, splitThreshold * splitThreshold, cap);
            }
            return frags;
        }

        static void Split(List<ShatterFragment> frags, Vector3 a, Vector3 b, Vector3 c,
            Vector2 ua, Vector2 ub, Vector2 uc, int srcTri, int depth, float thrSq, int cap)
        {
            if (frags.Count >= cap) return;
            if (depth > 0)
            {
                float e0 = (a - b).sqrMagnitude, e1 = (b - c).sqrMagnitude, e2 = (c - a).sqrMagnitude;
                if (Mathf.Max(e0, Mathf.Max(e1, e2)) > thrSq)
                {
                    Vector3 ab = (a + b) * 0.5f, bc = (b + c) * 0.5f, ca = (c + a) * 0.5f;
                    Vector2 uab = (ua + ub) * 0.5f, ubc = (ub + uc) * 0.5f, uca = (uc + ua) * 0.5f;
                    Split(frags, a, ab, ca, ua, uab, uca, srcTri, depth - 1, thrSq, cap);
                    Split(frags, ab, b, bc, uab, ub, ubc, srcTri, depth - 1, thrSq, cap);
                    Split(frags, ca, bc, c, uca, ubc, uc, srcTri, depth - 1, thrSq, cap);
                    Split(frags, ab, bc, ca, uab, ubc, uca, srcTri, depth - 1, thrSq, cap);
                    return;
                }
            }
            frags.Add(new ShatterFragment { A = a, B = b, C = c, Ua = ua, Ub = ub, Uc = uc, SourceTri = srcTri });
        }

        // ROUND 7 — alpha culling for cutout foliage a shard whose
        // texture alpha at its UV centroid is near zero is invisible junk — drop it, so moss
        // shatters into its VISIBLE sprigs only. Non-readable textures skip the cull; a
        // fully-culled result keeps the originals (degenerate UVs guard).
        public static List<ShatterFragment> CullTransparent(List<ShatterFragment> frags,
            Texture2D? tex, float alphaThreshold)
        {
            if (tex == null) return frags;
            try
            {
                var kept = new List<ShatterFragment>(frags.Count);
                foreach (var f in frags)
                {
                    Vector2 uv = (f.Ua + f.Ub + f.Uc) / 3f;
                    if (tex.GetPixelBilinear(uv.x, uv.y).a >= alphaThreshold) kept.Add(f);
                }
                return kept.Count > 0 ? kept : frags;
            }
            catch (UnityException) { return frags; }   // non-readable texture
        }
    }

    public sealed class MeshShatterEffect : MonoBehaviour
    {
        // ---- Play-tunable law constants (shapes cited in the header) ----
        public static float SplitThreshold = 1.0f;   // shard edge scale ≈ DAT_00736fe0
        public static int MaxSplitDepth = 3;
        public static int FragmentCap = 600;         // native = uncapped; safety for big meshes
        public static float StepHz = 30f;            // fixed integration step (GAME-time driven)
        // ROUND-7 retune to the native look (,
        // — gravity does the work; the round-6 spread
        // read as an explosion — the classic model-scale overshoot on the:167-172 constants).
        public static float LaunchSpread = 0.012f;   // per-step symmetric random
        public static float LaunchUpBias = 0.008f;   // barely-there arc
        public static float GravityPerStep = 0.006f; // DAT_00737058 analog
        public static float SpinDegPerStep = 2.5f;   // per-shard tumble (presentation)
        public static float GroundDrop = 1.2f;       // FALLBACK ground plane (headless/no hit range)
        public static float GroundRayRange = 40f;    // spawn-time per-fragment ground raycast
        public static LayerMask GroundMask = ~0;     // terrain chunk colliders (deco has none)
        public static float AlphaCullThreshold = 0.2f;
        public static float FlightShrinkDelay = 1.0f;   // no-ground shard: seconds of fall, then…
        public static float FlightShrinkSeconds = 0.6f; // …shrink to nothing (pit/air law)
        public static float RestLinger = 0.7f;       // all finished → shards stay a beat
        public static float FadeSeconds = 0.5f;      // then shrink away
        public static float MaxLifetime = 8f;        // hard kill (native frees at rest)

        // Test seams (statics — reset to null in test teardown):
        // • GroundProbe: world pos → the ground Y beneath, or null = NO ground (pit).
        // Defaults: physics raycast in Play; the flat fallback plane headless.
        // • SpeedSource: the game-speed factor. Defaults to DigglesAnimator.GlobalTimeScale —
        // the SAME scaled clock the animators consume (TimeControlService x1..x10 folds
        // into it; cutscenes force x1; pause drives it to 0 → shards freeze mid-air).
        public static System.Func<Vector3, float?>? GroundProbe;
        public static System.Func<float>? SpeedSource;

        struct F
        {
            public Vector3 Cen, Vel;
            public Vector3 OffA, OffB, OffC;   // corners relative to the centroid
            public Vector3 Axis;               // tumble axis
            public float Ang, Spin;            // current angle / per-step rate (deg)
            public float GroundY;              // this shard's own cached ground line
            public bool HasGround;             // false = pit/air beneath → flight-shrink law
            public float FlightSteps;
            public float Scale;                // per-shard shrink (flight death)
            public bool Done;                  // resting or shrunk away
        }

        F[] _f = System.Array.Empty<F>();
        Mesh? _mesh;
        Vector3[] _vbuf = System.Array.Empty<Vector3>();
        float _acc, _life, _restAt = -1f;
        int _doneCount, _stepsTaken;
        float _fallbackGroundY, _fade = 1f;
        static System.Random _rng = new(0x5EAE80);

        // The private, self-contained material this effect renders with. Either a snapshot COPY of
        // the source model's material (owned → destroyed with the effect) or the shared dirt
        // fallback (static → outlives the effect). See ResolveRenderMaterial (the anti-PINK law).
        Material? _ownedMat;
        static Material? s_dirtFallback;
        static readonly int UseVatId = Shader.PropertyToID("_UseVat");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public int FragmentCount => _f.Length;
        public bool AllSettled => _f.Length > 0 && _doneCount >= _f.Length;
        public int StepsTaken => _stepsTaken;                     // test surface
        public Vector3 FragmentCenter(int i) => _f[i].Cen;        // test surface
        public bool FragmentDone(int i) => _f[i].Done;            // test surface
        public bool FragmentHasGround(int i) => _f[i].HasGround;  // test surface

        // Build the effect: one GameObject + one dynamic mesh + the MODEL'S OWN material.
        // `texture` (optional) = the model's readable texture for the foliage alpha cull.
        public static MeshShatterEffect? Spawn(Mesh source, Transform sourceTf, Material? material,
            float fallbackGroundY, Texture2D? texture = null)
        {
            if (source == null || sourceTf == null || source.vertexCount == 0) return null;
            var tris = source.triangles;                 // all submeshes concatenated
            if (tris.Length < 3) return null;
            var verts = source.vertices;
            var wv = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) wv[i] = sourceTf.TransformPoint(verts[i]);
            var frags = MeshShatterBuilder.Build(wv, tris, source.uv, SplitThreshold, MaxSplitDepth, FragmentCap);
            frags = MeshShatterBuilder.CullTransparent(frags, texture, AlphaCullThreshold);
            if (frags.Count == 0) return null;

            var go = new GameObject("MeshShatter");
            var fx = go.AddComponent<MeshShatterEffect>();
            fx.Init(frags, material, sourceTf, fallbackGroundY);
            return fx;
        }

        void Init(List<ShatterFragment> frags, Material? material, Transform? sourceTf, float fallbackGroundY)
        {
            _fallbackGroundY = fallbackGroundY;
            _f = new F[frags.Count];
            _vbuf = new Vector3[frags.Count * 3];
            var uv = new Vector2[frags.Count * 3];
            var idx = new int[frags.Count * 3];
            for (int i = 0; i < frags.Count; i++)
            {
                var fr = frags[i];
                Vector3 cen = (fr.A + fr.B + fr.C) / 3f;
                float? gy = ProbeGround(cen);
                _f[i] = new F
                {
                    Cen = cen, OffA = fr.A - cen, OffB = fr.B - cen, OffC = fr.C - cen,
                    Vel = new Vector3(Sym(LaunchSpread), Sym(LaunchSpread) + LaunchUpBias, Sym(LaunchSpread)),
                    Axis = Random.onUnitSphere,
                    Ang = 0f,
                    Spin = Sym(SpinDegPerStep) + Mathf.Sign(Sym(1f)) * SpinDegPerStep * 0.5f,
                    GroundY = gy ?? float.NegativeInfinity,
                    HasGround = gy.HasValue,
                    Scale = 1f,
                };
                uv[i * 3] = fr.Ua; uv[i * 3 + 1] = fr.Ub; uv[i * 3 + 2] = fr.Uc;
                idx[i * 3] = i * 3; idx[i * 3 + 1] = i * 3 + 1; idx[i * 3 + 2] = i * 3 + 2;
            }
            _mesh = new Mesh { name = "MeshShatterFrags" };
            _mesh.MarkDynamic();
            WriteVerts();
            _mesh.SetVertices(_vbuf);
            _mesh.SetUVs(0, uv);
            _mesh.SetTriangles(idx, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            var mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = _mesh;
            var mr = gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ResolveRenderMaterial(material, sourceTf);   // model's own look, or dirt — NEVER pink
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // Per-fragment ground line, cached ONCE at spawn (≤ FragmentCap raycasts): uneven
        // terrain gets true contact heights — no shards hovering mid-air or sinking into the
        // ground mesh fixed). Null = nothing beneath within
        // range → the flight-shrink pit law.
        float? ProbeGround(Vector3 pos)
        {
            if (GroundProbe != null) return GroundProbe(pos);
            if (Application.isPlaying)
            {
                if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out var hit,
                        GroundRayRange, GroundMask, QueryTriggerInteraction.Ignore))
                    return hit.point.y;
                return null;                              // pit/air — falls and shrinks away
            }
            return _fallbackGroundY;                      // headless: flat fallback plane
        }

        static float Sym(float amp) => (float)(_rng.NextDouble() * 2.0 - 1.0) * amp;

        // Advance by SCALED game seconds (Update feeds dt × the game-speed factor; tests feed
        // dt directly). The fixed-step accumulator keeps integration deterministic per speed.
        public void Advance(float scaledDt)
        {
            _life += scaledDt;
            _acc += scaledDt;
            float step = 1f / Mathf.Max(1f, StepHz);
            int guard = 0;
            // The 1e-5 epsilon eats float accumulation error so 1.0 s = exactly StepHz steps.
            while (_acc + 1e-5f >= step && guard++ < 64) { _acc -= step; StepOnce(); }
        }

        // One fixed integration step — public for headless law tests.
        public void StepOnce()
        {
            _stepsTaken++;
            for (int i = 0; i < _f.Length; i++)
            {
                ref F f = ref _f[i];
                if (f.Done) continue;
                f.Cen += f.Vel;
                f.Vel.y -= GravityPerStep;
                f.Ang += f.Spin;
                if (f.HasGround)
                {
                    if (f.Cen.y <= f.GroundY && f.Vel.y < 0f)
                    {
                        f.Cen.y = f.GroundY;
                        f.Vel.y = -f.Vel.y * 0.3f;        // bounce (FUN_005ea850 settle spirit)
                        f.Vel.x *= 0.5f; f.Vel.z *= 0.5f;
                        f.Spin *= 0.5f;
                        if (f.Vel.y < GravityPerStep * 2f)  // too weak to rise again → at rest
                        {
                            f.Vel = Vector3.zero; f.Spin = 0f; f.Done = true;
                            _doneCount++;
                        }
                    }
                }
                else
                {
                    // No ground beneath (pit/open air): keep falling; after a short flight
                    // shrink to nothing (plus the MaxLifetime hard-kill upstream).
                    f.FlightSteps++;
                    if (f.FlightSteps > FlightShrinkDelay * StepHz)
                    {
                        f.Scale -= 1f / Mathf.Max(1f, FlightShrinkSeconds * StepHz);
                        if (f.Scale <= 0f)
                        {
                            f.Scale = 0f; f.Done = true;
                            _doneCount++;
                        }
                    }
                }
            }
        }

        void Update()
        {
            if (_mesh == null || _f.Length == 0) { Destroy(gameObject); return; }
            // GAME time: the same scaled clock the animators run on (x1..x10 fast-forward,
            // cutscene-forced x1; pause → 0 → shards freeze mid-air).
            float speed = SpeedSource?.Invoke() ?? (float)DigglesAnimator.GlobalTimeScale;
            Advance(Time.deltaTime * speed);

            if (AllSettled && _restAt < 0f) _restAt = _life;
            if (_restAt >= 0f && _life > _restAt + RestLinger)
                _fade = Mathf.Max(0f, 1f - (_life - _restAt - RestLinger) / Mathf.Max(0.01f, FadeSeconds));
            if (_fade <= 0f || _life > MaxLifetime) { Destroy(gameObject); return; }

            WriteVerts();
            _mesh.SetVertices(_vbuf);
            _mesh.RecalculateBounds();
        }

        void WriteVerts()
        {
            for (int i = 0; i < _f.Length; i++)
            {
                ref F f = ref _f[i];
                var rot = Quaternion.AngleAxis(f.Ang, f.Axis);
                float s = _fade * f.Scale;
                _vbuf[i * 3] = f.Cen + rot * (f.OffA * s);
                _vbuf[i * 3 + 1] = f.Cen + rot * (f.OffB * s);
                _vbuf[i * 3 + 2] = f.Cen + rot * (f.OffC * s);
            }
        }

        // ---- material resolve (the anti-PINK law) ----------------------------------------------
        // The shards must wear the dying model's OWN material so they read as a shattered copy of
        // it. The old path assigned the captured reference RAW, so any of three real failures
        // rendered Unity's magenta "missing material":
        // 1) NULL at capture — ShatterEntity walks GetComponentInChildren<Renderer> and can land
        // on a material-less child (a hat/shadow placeholder), handing us null;
        // 2) a DEAD reference — for animated gnomes the renderer's material slots are per-instance
        // clones (DigglesTextureAnimator) and the whole array is reassigned every SetAnim; both
        // death seams tear the source down the SAME tick (MapDecoRetire `if(ShatterEntity){Del}`,
        // CmdDestruct `ShatterEntity; hide`), so the captured Material can be a destroyed
        // UnityEngine.Object by the time we render next frame — `== null` → magenta;
        // 3) an UNSUPPORTED shader on the fragment pass.
        // Fix: prefer the passed material, else re-resolve from the mesh's OWN renderer (sourceTf —
        // for the morph path that is the animator's MeshRenderer, the exact material of the mesh we
        // shatter), then SNAPSHOT a private copy NOW (immune to the source dying this tick) forced to
        // render statically — the shards are pre-baked WORLD geometry; the model's per-frame VAT
        // morph is fed by a per-renderer MaterialPropertyBlock we do not carry, so a stray _UseVat=1
        // would sample a black VAT and collapse every shard onto the model origin. No usable
        // material → the shared dirt-toned fallback: a standalone shatter is dirt, never pink.
        Material? ResolveRenderMaterial(Material? passed, Transform? sourceTf)
        {
            var real = IsRenderable(passed) ? passed : FindRendererMaterial(sourceTf);
            if (IsRenderable(real))
            {
                _ownedMat = new Material(real!) { name = real!.name + " (shatter)" };
                if (_ownedMat.HasProperty(UseVatId)) _ownedMat.SetFloat(UseVatId, 0f);
                _ownedMat.enableInstancing = false;
                return _ownedMat;
            }
            return DirtFallback();
        }

        // A material Unity can actually draw with. A destroyed UnityEngine.Object compares == null
        // (Unity's overloaded ==), so a dead capture short-circuits before we touch.shader; a
        // material already sitting on the internal error shader is the other detectable magenta
        // source. (`shader.isSupported` is deliberately NOT consulted: it is GPU-query-dependent and
        // returns false under a null/headless graphics device, which would wrongly reject a perfectly
        // good material — and a shader the live model was rendering with is supported by construction.)
        static bool IsRenderable(Material? m)
            => m != null && m.shader != null
               && m.shader.name != "Hidden/InternalErrorShader";

        static Material? FindRendererMaterial(Transform? tf)
        {
            if (tf == null) return null;
            var r = tf.GetComponent<Renderer>() ?? tf.GetComponentInChildren<Renderer>();
            return r != null ? r.sharedMaterial : null;
        }

        // Shared dirt-toned fallback (built once) — the standalone stand-in for the set-43 particle
        // dirt burst when the model material can't be resolved. Uses the Diggles model shader so the
        // shards still light/fog like the world; a solid brown base colour over the default white map.
        static Material? DirtFallback()
        {
            if (s_dirtFallback != null) return s_dirtFallback;
            var sh = DigglesShaders.Model;
            if (sh == null) return null;   // no shader DB (headless tooling) → nothing draws; still no pink
            var m = new Material(sh) { name = "MeshShatter_DirtFallback" };
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, new Color(0.42f, 0.32f, 0.22f, 1f));
            if (m.HasProperty(UseVatId)) m.SetFloat(UseVatId, 0f);
            s_dirtFallback = m;
            return m;
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_ownedMat != null) Destroy(_ownedMat);   // owned copy only; the dirt fallback is a shared static
        }
    }
}
