#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    // The faithful selection/hover aura — the native "SelHoverGlow" pass reconstructed
    // (CDisplay_DrawObjects FUN_00473cc0:669-714):
    // 1. project the current anim frame's vertices to screen space (FUN_00490a70/00490cc0);
    // 2. 2D convex hull of the points (gift wrap in the native, monotone chain here —
    // same hull, FUN_00490fc0);
    // 3. a soft band around the hull: the hull ring carries the aura colour, an outer ring
    // (+20 px) and an inner ring (−10 px, native W_out = 2·W_in, W_in = 10 px pushed at
    // 0x4719b8) fade to alpha 0 (FUN_004914b0 quad strips both sides of the line);
    // 4. additive, no Z test, over everything (mod 0x1f interface_glow: srcalpha/one,
    // zwrite off), no pulse; widths scale with the screen height (native constants are
    // 800×600-era pixels).
    // One hidden singleton draws every registered band. Slots: 0 = the primary selection,
    // 1 = hover, 2+ = the rest of the multi-selection (the native draws the glow for every
    // selected unit — the SelHoverGlow pass walks the whole selection array).
    //
    // Draw path: under URP, drawing via OnRenderObject was unreliable for the game camera
    // (invoked mid-pipeline, the immediate GL either misses the camera's final target or is
    // overwritten by the post stack; the Camera.main fallback didn't help — the band only
    // showed in the Scene view). The band now draws in RenderPipelineManager.endCameraRendering
    // — after URP finishes the camera, straight onto its final target — with the camera's
    // matrices loaded explicitly (nothing at that point guarantees the GL matrix state, unlike
    // inside OnRenderObject). OnRenderObject remains only as the built-in-pipeline fallback
    // (double-draw guarded by the SRP check).
    public sealed class AuraBandRenderer : MonoBehaviour
    {
        struct Entry
        {
            public GameObject? Root;         // the entity's outer GO (null slot = off)
            public Color Color;
            // Per-root component cache: GetComponentInChildren ran twice per slot per camera per
            // frame. Resolved lazily on first use, re-resolved only if the cached components die
            // (model reskin) — worst case degrades to the old per-frame walk.
            public DigglesAnimator? Anim;
            public MeshFilter? Mf;
            public bool Resolved;
        }

        static AuraBandRenderer? s_instance;
        // Growable slot list (multi-select needs one band per selected unit); MaxSlots caps a
        // runaway caller. Index semantics documented on the class header.
        public const int MaxSlots = 65;
        readonly System.Collections.Generic.List<Entry> _slots = new();

        void EnsureSlots(int n)
        {
            while (_slots.Count < n) _slots.Add(default);
        }

        static Material? s_mat;

        // Native band widths in 600-line-era pixels (FUN_004716f0 pushes 10.0; W_out = 2·W_in).
        const float InnerPx600 = 10f;
        const float OuterPx600 = 20f;
        const int MaxHullPoints = 512;   // vertex subsample cap (hull input)

        public static void Set(int slot, GameObject? entityRoot, Color color)
        {
            var inst = Instance();
            if (inst == null || (uint)slot >= MaxSlots) return;
            inst.EnsureSlots(slot + 1);
            inst._slots[slot] = new Entry { Root = entityRoot, Color = color };
        }

        public static void Clear(int slot)
        {
            if (s_instance == null || (uint)slot >= (uint)s_instance._slots.Count) return;
            s_instance._slots[slot] = default;
        }

        static AuraBandRenderer? Instance()
        {
            if (s_instance != null) return s_instance;
            if (!Application.isPlaying) return null;
            // DontSave like the marquee GO (HideAndDontSave also hides — keep the object findable
            // and its render callbacks unambiguous).
            var go = new GameObject("AuraBandRenderer") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<AuraBandRenderer>();
            return s_instance;
        }

        static Material Mat()
        {
            if (s_mat == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                s_mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                s_mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                s_mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);   // additive glow
                s_mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                s_mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                s_mat.SetInt("_ZWrite", 0);
            }
            return s_mat;
        }

        // scratch buffers (reused per frame)
        readonly List<Vector2> _pts = new(MaxHullPoints + 8);
        readonly List<Vector2> _hull = new(64);

        // Mesh.vertices allocates a fresh Vector3[] copy on every access — hulling did that per
        // slot (×2 meshes when the morph blend is live) per camera per frame: a steady
        // 0.1-1 MB/frame GC churn. Frame meshes are immutable library assets, so cache the copy
        // per mesh (weak table — entries die with the mesh).
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, Vector3[]> s_vertCache = new();
        static Vector3[] VertsOf(Mesh m) => s_vertCache.GetValue(m, static mm => mm.vertices);

        void OnEnable() => UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        void OnDisable() => UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

        // SRP path (URP): after the pipeline finished this camera, the active target is the
        // camera's final image — an immediate GL overlay lands on screen reliably (the same
        // hook the URP GL-overlay idiom uses). Game + Scene view cameras only; a Game camera
        // rendering into a RenderTexture (previews/minimaps) gets no UI band.
        void OnEndCameraRendering(UnityEngine.Rendering.ScriptableRenderContext _, Camera cam)
        {
            if (cam.cameraType == CameraType.SceneView ||
                (cam.cameraType == CameraType.Game && cam.targetTexture == null))
                DrawBands(cam);
        }

        // Built-in pipeline fallback only (no SRP → endCameraRendering never fires). The SRP
        // guard doubles as the double-draw guard.
        void OnRenderObject()
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null) return;
            var cam = Camera.current != null ? Camera.current : Camera.main;
            if (cam != null) DrawBands(cam);
        }

        // Rebuild the band geometry for this camera (screen-space, per-camera projection).
        // Geometry is collected first — exceptions can't corrupt an open GL block. Returns the
        // triangle count (the headless smoke test's data-level surface).
        public int BuildBands(Camera cam)
        {
            _tris.Clear();
            for (int s = 0; s < _slots.Count; s++)
            {
                var e = _slots[s];                        // copy out (List<T> can't hand out a ref)
                if (e.Root == null) continue;             // Unity-null covers destroyed entities
                try { CollectBandFor(cam, ref e); }
                catch (System.Exception ex)
                {
                    if (!_loggedError) { _loggedError = true; Debug.LogWarning($"[aura] band build failed: {ex.Message}"); }
                }
                _slots[s] = e;                            // write the lazily-resolved caches back
            }
            return _tris.Count / 3;
        }

        // Headless/test seam: drive a slot directly on this instance (Set/Clear go through the
        // Play-mode singleton, which EditMode tests can't reach).
        public void SetSlotDirect(int slot, GameObject? entityRoot, Color color)
        {
            if ((uint)slot >= MaxSlots) return;
            EnsureSlots(slot + 1);
            _slots[slot] = new Entry { Root = entityRoot, Color = color };
        }

        void DrawBands(Camera cam)
        {
            if (BuildBands(cam) == 0) return;

            // Emit in world space near the camera's near plane, with the camera's matrices
            // loaded explicitly — at endCameraRendering time nothing has set them up for us
            // (inside OnRenderObject they happen to be current; here they must be loaded).
            float depth = cam.nearClipPlane + 0.05f;
            // Exact per-vertex screen→world (what cam.ScreenToWorldPoint computes), with the
            // camera state cached once per draw so the native call doesn't run per GL vertex
            // (~1.5k/frame). An earlier attempt — an affine basis from 3 unprojections 1 px
            // apart — was numerically wrong: differencing near-plane world points ~1e-4 units
            // apart against O(100) world coordinates cancels most float mantissa bits, and that
            // %-level basis error scaled by up to screen-width pixels displaced the band (the
            // aura appeared to fly away from the model). The analytic form in ScreenAtDepthMap
            // loses no precision (AuraScreenMapTests pins it against ScreenToWorldPoint per
            // vertex).
            var map = new ScreenAtDepthMap(cam, depth);
            Mat().SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;
            GL.Begin(GL.TRIANGLES);
            for (int i = 0; i < _tris.Count; i++)
            {
                var (p, col) = _tris[i];
                GL.Color(col);
                GL.Vertex(map.Map(p.x, p.y));
            }
            GL.End();
            GL.PopMatrix();
            if (!_loggedFirstDraw)
            {
                _loggedFirstDraw = true;
                Debug.Log($"[aura] first band drawn: {_tris.Count / 3} tris, cam={cam.name} ({cam.cameraType}), " +
                          $"path={(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? "endCameraRendering" : "OnRenderObject")}");
            }
        }

        readonly List<(Vector2 p, Color c)> _tris = new(2048);
        bool _loggedFirstDraw, _loggedError;

        // Screen→world at a fixed view depth — the exact math of cam.ScreenToWorldPoint
        // (perspective: the ray through the pixel scaled so the view-space depth equals
        // `depth`), evaluated from camera state cached at construction. For a standard
        // (possibly off-center) perspective projection, NDC → view is
        // view.x = depth·(ndc.x + m02)/m00, view.y = depth·(ndc.y + m12)/m11,
        // view.z = −depth (clip.w = −view.z = depth),
        // then cameraToWorld. Every coefficient is derived analytically — no catastrophic
        // cancellation, unlike differencing two 1-px-apart unprojections (see above).
        // Non-standard matrices (ortho scene cameras, oblique) fall back to the native call
        // per vertex — still exact, just not cached.
        public readonly struct ScreenAtDepthMap
        {
            readonly Camera _cam;
            readonly Matrix4x4 _c2w;
            readonly float _depth, _kx, _bx, _ky, _by;
            readonly bool _analytic;

            public ScreenAtDepthMap(Camera cam, float depth)
            {
                _cam = cam;
                _depth = depth;
                _c2w = cam.cameraToWorldMatrix;
                var pr = cam.projectionMatrix;
                _analytic = !cam.orthographic
                            && pr.m01 == 0f && pr.m03 == 0f && pr.m10 == 0f && pr.m13 == 0f
                            && pr.m30 == 0f && pr.m31 == 0f && pr.m32 == -1f && pr.m33 == 0f;
                if (_analytic)
                {
                    // ndc.x = 2·sx/pw − 1 folded into view.x = sx·kx + bx (same for y).
                    float pw = cam.pixelWidth, ph = cam.pixelHeight;
                    _kx = 2f * depth / (pw * pr.m00);
                    _bx = depth * (pr.m02 - 1f) / pr.m00;
                    _ky = 2f * depth / (ph * pr.m11);
                    _by = depth * (pr.m12 - 1f) / pr.m11;
                }
                else { _kx = _bx = _ky = _by = 0f; }
            }

            public Vector3 Map(float sx, float sy)
                => _analytic
                    ? _c2w.MultiplyPoint3x4(new Vector3(sx * _kx + _bx, sy * _ky + _by, -_depth))
                    : _cam.ScreenToWorldPoint(new Vector3(sx, sy, _depth));
        }

        void CollectBandFor(Camera cam, ref Entry e)
        {
            // The renderer lives on the inner "Model" node; its animator serves the frame mesh.
            // Interpolated state (CurrentFrameMorph): the visual mesh is GPU-lerped f0→f1 by
            // _Blend — hulling the discrete f0 alone made the band lag/jump at the 10 FPS key
            // rate while the model glided. Reconstruct the same lerp on the CPU for the hull
            // input.
            var root = e.Root!;
            var color = e.Color;
            if (!e.Resolved || (e.Anim == null && e.Mf == null))   // lazily resolve; re-resolve if the cache died
            {
                e.Anim = root.GetComponentInChildren<DigglesAnimator>();
                e.Mf = e.Anim == null ? root.GetComponentInChildren<MeshFilter>() : null;
                e.Resolved = true;
            }
            var anim = e.Anim;
            Transform modelTf;
            Mesh? mesh = null, meshNext = null;
            float blend = 0f;
            if (anim != null) { (mesh, meshNext, blend) = anim.CurrentFrameMorph(); modelTf = anim.transform; }
            else
            {
                var mf = e.Mf;
                if (mf == null) return;
                mesh = mf.sharedMesh; modelTf = mf.transform;
            }
            if (mesh == null) return;
            if (!mesh.isReadable)
            {
                if (!_loggedError) { _loggedError = true; Debug.LogWarning($"[aura] mesh '{mesh.name}' not readable — no band"); }
                return;
            }

            // 1. project a subsample of the frame's vertices to screen pixels (lerped f0→f1
            // with the shader's own blend when the morph is live; ≤ MaxHullPoints kept).
            var verts = VertsOf(mesh);                    // cached CPU copy (see s_vertCache)
            int n = verts.Length;
            if (n < 3) return;
            Vector3[]? vertsNext = null;                  // frame f1's vertices for the morph lerp
            if (meshNext != null && blend > 0f && meshNext.isReadable && meshNext.vertexCount == n)
                vertsNext = VertsOf(meshNext);
            int step = Mathf.Max(1, n / MaxHullPoints);
            var l2w = modelTf.localToWorldMatrix;
            _pts.Clear();
            for (int i = 0; i < n; i += step)
            {
                Vector3 v = vertsNext != null ? Vector3.Lerp(verts[i], vertsNext[i], blend) : verts[i];
                Vector3 sp = cam.WorldToScreenPoint(l2w.MultiplyPoint3x4(v));
                if (sp.z <= 0f) return;                   // behind the camera — skip the whole band
                _pts.Add(new Vector2(sp.x, sp.y));
            }

            // 2. convex hull (monotone chain).
            MonotoneHull(_pts, _hull);
            int h = _hull.Count;
            if (h < 3) return;

            // 3. centroid + the three rings (FUN_00490fc0:128-174).
            Vector2 c = Vector2.zero;
            for (int i = 0; i < h; i++) c += _hull[i];
            c /= h;
            // Absolute pixels, like the native constants — scaling them by resolution fattened
            // the band into a smear at high resolution; a constant 10/20 px band stays a tight,
            // distance-independent outline.
            const float wIn = InnerPx600, wOut = OuterPx600;

            // 4. quad strips hull↔outer and inner↔hull, vertex colours peak at the hull
            // (alpha forced |0xff000000 in the native — colour.a is the port's intensity).
            Color peak = new(color.r, color.g, color.b, color.a);
            Color edge = new(color.r, color.g, color.b, 0f);
            for (int i = 0; i < h; i++)
            {
                int j = (i + 1) % h;
                Vector2 hi = _hull[i], hj = _hull[j];
                Vector2 oi = Ring(c, hi, wOut), oj = Ring(c, hj, wOut);
                Vector2 ii = Ring(c, hi, -wIn), ij = Ring(c, hj, -wIn);
                // hull → outer
                Tri(hi, peak, hj, peak, oj, edge);
                Tri(hi, peak, oj, edge, oi, edge);
                // inner → hull
                Tri(ii, edge, ij, edge, hj, peak);
                Tri(ii, edge, hj, peak, hi, peak);
            }
        }

        void Tri(Vector2 a, Color ca, Vector2 b, Color cb, Vector2 d, Color cd)
        {
            _tris.Add((a, ca));
            _tris.Add((b, cb));
            _tris.Add((d, cd));
        }

        static Vector2 Ring(Vector2 c, Vector2 p, float w)
        {
            Vector2 d = p - c;
            float dist = d.magnitude;
            if (dist < 1e-3f) return p;
            float k = Mathf.Max(0f, (dist + w) / dist);   // negative w shrinks, clamped ≥ 0 (:170)
            return c + d * k;
        }

        // Andrew's monotone chain — same hull as the native's gift wrap (FUN_00490fc0).
        sealed class XyComparer : System.Collections.Generic.IComparer<Vector2>
        {
            public static readonly XyComparer Instance = new();
            public int Compare(Vector2 a, Vector2 b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y);
        }

        static void MonotoneHull(List<Vector2> pts, List<Vector2> outHull)
        {
            outHull.Clear();
            int n = pts.Count;
            if (n < 3) { outHull.AddRange(pts); return; }
            pts.Sort(XyComparer.Instance);   // IComparer, not a Comparison lambda — List.Sort(Comparison) wraps+allocates per call
            // lower
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                while (outHull.Count >= 2 && Cross(outHull[^2], outHull[^1], p) <= 0f)
                    outHull.RemoveAt(outHull.Count - 1);
                outHull.Add(p);
            }
            // upper
            int lower = outHull.Count + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                var p = pts[i];
                while (outHull.Count >= lower && Cross(outHull[^2], outHull[^1], p) <= 0f)
                    outHull.RemoveAt(outHull.Count - 1);
                outHull.Add(p);
            }
            outHull.RemoveAt(outHull.Count - 1);   // last point == first
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b)
            => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }
}
