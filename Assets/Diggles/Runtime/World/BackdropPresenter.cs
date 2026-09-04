#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Panorama backdrop + horizon-zone presenter — the render half of the native background
    // subsystem (laws + citations in BackdropLaws / Docs/BACKDROP_PARALLAX_RE.md).
    //
    // Three native features, one seam:
    // 1. Panorama (surface landscape): 6 alpha-blended layers (Panorama_layer_0..5.tga) whose
    // UVs follow the per-pixel-ray parallax law of FUN_004802c0, plus the meadow ground
    // carpet (Panorama_layer_6.tga, FUN_00480720). Gated to "camera focus within 20 rows of
    // the surface" (FUN_00480ee0:38) and to a live gameplay session (the CDisplay_Draw
    // +0x38f0 byte — see LateUpdate). The native draws the layers as screen-space TLVERTs in
    // a z-test-less pre-pass under the terrain; the port does the same (static viewport-space
    // quads emitted straight to clip space by the _ScreenAnchored shader path, ZWrite Off,
    // Background queue) — terrain draws after and covers them identically.
    // 2. Horizon zones (`horizon_set`, the Info_Fog_* map objects): the nearest region within
    // its size of the camera focus (FUN_004750b0) supplies the frame clear colour
    // (CGameThread::Draw fill, FUN_006ad9a0:128-140) — the authored cave backdrop colour.
    // 3. Rear fade: vertices with render z < 0 (= root-local y < 0, seven metres behind the
    // deepest dug floor plane 7) lerp to the active zone colour over `depth` units
    // (FUN_00484cd0:82-104, FUN_004856e0:162-199) — uploaded here as the _HorizonFog shader
    // globals consumed by DigglesGround/DigglesModel. Terrain can never reach z < 0 (the
    // BDB palette spans [7, 14.5] exactly); the law's live native targets are authored
    // backdrop models placed behind the slab, which melt into the matching clear colour with
    // depth.
    //
    // Port notes (deviations, each deliberate):
    // * The native culls panorama triangles above/below a per-column horizon line (overdraw
    // optimization for its z-test-less pre-pass). The port's depth test against the terrain
    // achieves the same final image, so the clip is not ported.
    // * FUN_004750b0 also derives a fog-of-war fade byte for the zone (+0x2864) whose consumer
    // was not located in the decompile; it is not ported. Zones activate regardless of FoW.
    // * The native multitexture pair path (FUN_0047fad0, "RenderM") is a caps optimization that
    // draws layer pairs in one call — visually identical to the sequential path we port.
    // Execution order after DigglesCameraController (order 0): the controller poses the camera
    // in its LateUpdate — sampling the camera matrices earlier in the same phase would read the
    // previous frame's pose (undefined order), so the UV counter-pan would lag the camera by one
    // frame.
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public sealed class BackdropPresenter : MonoBehaviour
    {
        const int GridN = 9;                       // 9×9 screen grid (0x51 = 81 verts, FUN_00480ee0)
        const int CarpetChunks = 14;               // snap-140.. snap+140 step 20 (FUN_00480720:53-57)

        static readonly int HorizonFogId = Shader.PropertyToID("_HorizonFog");
        static readonly int HorizonWorldToNativeId = Shader.PropertyToID("_HorizonWorldToNative");

        [Tooltip("Extra rows added to the derived ground line (Play tuning; native panorama+0x1560 writer not located).")]
        public float GroundRowBias = 0f;
        [Tooltip("Native texture space has v growing down; TgaLoader flips rows to Unity's bottom-up → sample 1-v.")]
        public bool FlipLayerV = true;

        Transform? _root;
        int _groundRow = int.MinValue;
        Func<IReadOnlyList<HorizonRegionState>>? _zones;
        Func<bool>? _gameplay;   // native +0x38f0 session gate provider (null = always live)

        DigglesCameraController? _camCtl;
        Camera? _cam;

        GameObject? _container;
        readonly Mesh?[] _layerMeshes = new Mesh?[BackdropLaws.LayerCount];
        readonly GameObject?[] _layerGos = new GameObject?[BackdropLaws.LayerCount];
        Mesh? _carpetMesh;
        GameObject? _carpetGo;
        Texture2D?[] _layerTex = new Texture2D?[BackdropLaws.LayerCount + 1];

        // scratch (no per-frame garbage)
        readonly Vector3[] _gridNearR = new Vector3[GridN * GridN];
        readonly Vector3[] _gridFarR = new Vector3[GridN * GridN];
        readonly List<Vector2> _uvScratch = new(GridN * GridN);
        float _carpetSnap = float.NaN;
        bool _visible;

        // camera clear restore state
        Camera? _clearOwner;
        bool _clearSaved;
        CameraClearFlags _savedFlags;
        Color _savedColor;
        bool _zoneActive;

        public bool PanoramaVisible => _visible;    // test/debug
        public int GroundRow => _groundRow;

        public void Init(
            string dataRoot,
            Transform worldRoot,
            int groundRow,
            Func<IReadOnlyList<HorizonRegionState>>? zones,
            Func<bool>? gameplayActive = null)
        {
            ClearScene();                           // rebuild-safe (TearDownWorld destroyed our children)
            _root = worldRoot;
            _groundRow = groundRow;
            _zones = zones;
            _gameplay = gameplayActive;

            for (int i = 0; i <= BackdropLaws.LayerCount; i++)
            {
                string p = Path.Combine(dataRoot, "Texture", "Misc", $"Panorama_layer_{i}.tga");
                _layerTex[i] = File.Exists(p) ? TgaLoader.LoadFromFile(p)?.Texture : null;
                if (_layerTex[i] == null)
                    Debug.LogWarning($"[BackdropPresenter] missing panorama texture: {p}");
                // Per-axis addressing:
                // * U = Repeat everywhere — the strips tile horizontally by design (layer_0's
                // L/R edge columns are bit-identical); Clamp would freeze the U counter-pan
                // once u left [0,1], visibly detaching the backdrop from the camera.
                // * V: layer 0 Repeat (fully opaque seamless sky — its handful of vertical
                // repeats read as clouds), strip layers 1..5 + carpet Clamp. The V law
                // divides by the ray's render Δz, and camera orbits (dy) shrink Δz toward 0
                // — slopeY grows without bound, v sweeps tens of tiles across the screen, and
                // V-wrap turns every strip into a stack of diagonal stripes. The strips are
                // authored transparent-top / opaque-bottom, so V-clamp degrades cleanly:
                // above the band the stretched edge row is fully transparent sky, below it is
                // solid ground colour that carpet/terrain over-paint — no pose can produce
                // stripes.
                else
                {
                    _layerTex[i]!.wrapModeU = TextureWrapMode.Repeat;
                    _layerTex[i]!.wrapModeV = i == 0 ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                }
            }

            _container = new GameObject("Backdrop");
            _container.transform.SetParent(worldRoot, false);   // meshes live in ROOT-LOCAL space

            var shader = Shader.Find("Diggles/Backdrop") ?? Shader.Find("Sprites/Default");
            for (int l = 0; l < BackdropLaws.LayerCount; l++)
            {
                var go = new GameObject($"PanoramaLayer_{l}");
                go.transform.SetParent(_container.transform, false);
                var mesh = new Mesh { name = $"PanoramaLayer_{l}" };
                mesh.MarkDynamic();
                BuildGridTopology(mesh);
                var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(shader!) { name = $"PanoramaMat_{l}" };
                if (_layerTex[l] != null) mat.SetTexture("_MainTex", _layerTex[l]);
                // Layers are SCREEN-SPACE quads (the native TLVERT law, FUN_0047f440:114-171):
                // vertex xy = viewport 0..1, emitted straight to clip space by the shader —
                // glued to the screen by construction; ONLY the UVs counter-pan.
                mat.SetFloat("_ScreenAnchored", 1f);
                mat.renderQueue = 1000 + l;         // native sort key layer+1, back-to-front
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _layerGos[l] = go; _layerMeshes[l] = mesh;
            }

            // Ground carpet (layer-6 texture, "GroundRenderer2" — native sort key 7).
            _carpetGo = new GameObject("PanoramaGroundCarpet");
            _carpetGo.transform.SetParent(_container.transform, false);
            _carpetMesh = new Mesh { name = "PanoramaGroundCarpet" };
            _carpetMesh.MarkDynamic();
            var cmf = _carpetGo.AddComponent<MeshFilter>(); cmf.sharedMesh = _carpetMesh;
            var cmr = _carpetGo.AddComponent<MeshRenderer>();
            var cmat = new Material(shader!) { name = "PanoramaMat_carpet" };
            if (_layerTex[6] != null) cmat.SetTexture("_MainTex", _layerTex[6]);
            cmat.renderQueue = 1000 + BackdropLaws.LayerCount;
            cmr.sharedMaterial = cmat;
            cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cmr.receiveShadows = false;

            _carpetSnap = float.NaN;
            _carpetGround = float.NaN;
            SetVisible(false);
        }

        // Drop scene objects + shader/clear side effects (world teardown or menu swap).
        public void Clear() => ClearScene();

        void ClearScene()
        {
            Shader.SetGlobalVector(HorizonFogId, Vector4.zero);
            RestoreClear();
            _zoneActive = false;
            if (_container != null) DestroyImmediate(_container);
            _container = null; _carpetGo = null; _carpetMesh = null;
            for (int i = 0; i < _layerGos.Length; i++) { _layerGos[i] = null; _layerMeshes[i] = null; }
            _visible = false;
        }

        void OnDisable() { Shader.SetGlobalVector(HorizonFogId, Vector4.zero); RestoreClear(); _zoneActive = false; }
        void OnDestroy() => ClearScene();

        void LateUpdate()
        {
            if (_root == null || _container == null) return;
            if (_camCtl == null) _camCtl = FindFirstObjectByType<DigglesCameraController>();
            _cam = _camCtl != null && _camCtl.TargetCamera != null ? _camCtl.TargetCamera : Camera.main;
            if (_cam == null) { SetVisible(false); return; }

            float focusX = _camCtl != null ? _camCtl.FocusX : 0f;
            float focusY = _camCtl != null ? _camCtl.FocusY : 0f;

            // Only the panorama/carpet obey the campaign gate; the horizon-zone scan is ungated.
            // Native: CDisplay::Draw runs the horizon-zone scan FUN_004750b0 unconditionally
            // every frame (004781b0_CDisplay_Draw.c:210 — outside the panorama if), while the
            // panorama dispatcher alone sits under `byte+0x38f0 && StoryMgr+0x84 == 0`
            // (:203-206; the scan re-checks the byte only for the active flag,
            // FUN_004750b0:141-143). The +0x38f0 byte is the display-live/world-attached flag,
            // not a campaign flag: the menu diorama (obw_szene.tcl:1650-1669) authors
            // Info_Fog_Urw_a/b and the native menu shows no raw earth rim, so zone activation
            // must be live over the menu too. Any world this presenter runs in has the byte's
            // port-equivalent up by definition (a world is attached and drawing), so the zone
            // path is ungated here; the campaign gate stays on the panorama/carpet only.
            bool gameplay = _gameplay == null || _gameplay();
            _dbgGameplay = gameplay;
            bool show = gameplay
                && _groundRow != int.MinValue
                && BackdropLaws.SurfaceVisible(focusY, _groundRow + GroundRowBias);
            UpdateHorizonZone(focusX, focusY, show);
            SetVisible(show);
            if (!show) return;

            UpdatePanoramaLayers(_cam);
            UpdateCarpet(focusX);
        }

        // ---- horizon zone → clear colour + rear-skirt fog globals (FUN_004750b0 / 006ad9a0) ----
        // The frame clear is owned whenever a zone is active (zone colour — FUN_006ad9a0:128-140
        // packs (r<<16|g<<8|b)) or the panorama is visible (plain black fill; also keeps a scene
        // Skybox from painting over the depth-less backdrop). Note: the native no-zone
        // else-branch actually fills 0xffb8b2aa, not black; not ported pending full
        // original-render research.
        void UpdateHorizonZone(float focusX, float focusY, bool panoramaVisible)
        {
            // Matrix uploaded every frame, not only under an active pick: `horizon_tint`
            // forces the fade region with no zone active (it needs a live matrix to show the
            // true region), and this removes the stale-matrix window on the first active
            // frame after a world rebuild moved the root.
            Shader.SetGlobalMatrix(HorizonWorldToNativeId, _root!.worldToLocalMatrix);
            var zones = _zones?.Invoke();
            int pick = zones != null ? BackdropLaws.PickZone(zones, focusX, focusY) : -1;
            _dbgPick = pick; _dbgFocusX = focusX; _dbgFocusY = focusY;
            if (pick >= 0)
            {
                var z = zones![pick];
                var col = new Color(z.Red / 255f, z.Green / 255f, z.Blue / 255f, 1f);
                // Rear-skirt fade globals (FUN_00484cd0:82-104). depth is in render-z metres
                // as stored by horizon_set (Info_Fog_* pass 32). Matrix maps world →
                // root-local; local y is the native render z (BackdropLaws.RenderFromLocal —
                // plain axis swap), so the shaders apply the z<0 law to local y directly.
                Shader.SetGlobalVector(HorizonFogId, new Vector4(col.r, col.g, col.b, z.Depth));
                OverrideClear(col);
                _zoneActive = true;
            }
            else
            {
                if (_zoneActive)
                {
                    Shader.SetGlobalVector(HorizonFogId, Vector4.zero);
                    _zoneActive = false;
                }
                if (panoramaVisible) OverrideClear(Color.black);
                else RestoreClear();
            }
        }

        void OverrideClear(Color col)
        {
            if (_cam == null) return;
            if (!_clearSaved || _clearOwner != _cam)
            {
                RestoreClear();
                _clearOwner = _cam; _savedFlags = _cam.clearFlags;
                _savedColor = _cam.backgroundColor; _clearSaved = true;
            }
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = col;
        }

        void RestoreClear()
        {
            if (_clearSaved && _clearOwner != null)
            {
                _clearOwner.clearFlags = _savedFlags;
                _clearOwner.backgroundColor = _savedColor;
            }
            _clearSaved = false; _clearOwner = null;
        }

        // ---- panorama layers: 9×9 screen grid, per-vertex ray UVs (the parallax law) ----
        // Vertices are STATIC screen-space quads (the native TLVERT law) — only the UVs stream.
        void UpdatePanoramaLayers(Camera cam)
        {
            float near = cam.nearClipPlane;
            float far = cam.farClipPlane;
            var rootInv = _root!;

            int i = 0;
            for (int row = 0; row < GridN; row++)
            {
                for (int col = 0; col < GridN; col++, i++)
                {
                    var vp = new Vector3(col / (float)(GridN - 1), row / (float)(GridN - 1), 0f);
                    vp.z = near;
                    Vector3 nWorld = cam.ViewportToWorldPoint(vp);
                    vp.z = far;
                    Vector3 fWorld = cam.ViewportToWorldPoint(vp);
                    // Root-local → native render frame (plain row-sign axis swap — local y is
                    // the render height; see BackdropLaws.RenderFromLocal).
                    _gridNearR[i] = BackdropLaws.RenderFromLocal(rootInv.InverseTransformPoint(nWorld));
                    _gridFarR[i] = BackdropLaws.RenderFromLocal(rootInv.InverseTransformPoint(fWorld));
                }
            }

            for (int l = 0; l < BackdropLaws.LayerCount; l++)
            {
                var mesh = _layerMeshes[l];
                if (mesh == null) continue;
                _uvScratch.Clear();
                for (int v = 0; v < _gridNearR.Length; v++)
                {
                    Vector2 uv = BackdropLaws.LayerUv(l, _gridNearR[v], _gridFarR[v]);
                    if (FlipLayerV) uv.y = 1f - uv.y;   // native v grows down; TGA rows are bottom-up in Unity
                    _uvScratch.Add(uv);
                }
                mesh.SetUVs(0, _uvScratch);
            }
        }

        static void BuildGridTopology(Mesh mesh)
        {
            // Static screen-space vertices: xy = viewport 0..1 (col/8, row/8) — the native
            // TLVERT grid (x = col*w/8, y = row*h/8, FUN_0047f440:114-171). The Backdrop
            // shader's _ScreenAnchored path emits them straight to clip space, so the quads
            // stay glued to the screen and only the UVs move (the native counter-pan).
            var verts = new Vector3[GridN * GridN];
            var uvs = new Vector2[GridN * GridN];
            for (int row = 0; row < GridN; row++)
                for (int col = 0; col < GridN; col++)
                    verts[row * GridN + col] = new Vector3(
                        col / (float)(GridN - 1), row / (float)(GridN - 1), 0f);
            mesh.SetVertices(verts);
            mesh.SetUVs(0, new List<Vector2>(uvs));
            var tris = new int[(GridN - 1) * (GridN - 1) * 6];
            int t = 0;
            for (int r = 0; r < GridN - 1; r++)
            {
                for (int c = 0; c < GridN - 1; c++)
                {
                    int a = r * GridN + c;
                    tris[t++] = a; tris[t++] = a + 1; tris[t++] = a + GridN;
                    tris[t++] = a + 1; tris[t++] = a + GridN + 1; tris[t++] = a + GridN;
                }
            }
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e5f, 1e5f, 1e5f));
        }

        // ---- meadow ground carpet (FUN_00480720) ----
        float _carpetGround = float.NaN;

        void UpdateCarpet(float focusX)
        {
            if (_carpetMesh == null) return;
            float snap = BackdropLaws.CarpetSnap(focusX);
            float ground = _groundRow + GroundRowBias;
            if (snap == _carpetSnap && ground == _carpetGround) return;
            _carpetSnap = snap;
            _carpetGround = ground;
            var verts = new List<Vector3>(CarpetChunks * 16);
            var uvs = new List<Vector2>(CarpetChunks * 16);
            var tris = new List<int>(CarpetChunks * 54);
            for (int chunk = 0; chunk < CarpetChunks; chunk++)
            {
                float chunkX = snap - BackdropLaws.CarpetHalfSpan + chunk * BackdropLaws.CarpetChunk;
                int baseV = verts.Count;
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        Vector3 render = BackdropLaws.CarpetVertex(chunkX, c, r, ground);
                        // Native render frame → root-local = plain axis swap: the carpet rows
                        // sit at render z (= local y) {-60, -37.5, -15, +7.5} — the near row
                        // hugs the deepest floor plane (7) and the ribbon runs 67 metres back
                        // toward the horizon.
                        verts.Add(BackdropLaws.LocalFromRender(render));
                        float v = r * BackdropLaws.CarpetUvStep;
                        if (FlipLayerV) v = 1f - v;
                        uvs.Add(new Vector2(c * BackdropLaws.CarpetUvStep, v));
                    }
                }
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int a = baseV + r * 4 + c;
                        tris.Add(a); tris.Add(a + 1); tris.Add(a + 4);
                        tris.Add(a + 1); tris.Add(a + 5); tris.Add(a + 4);
                    }
                }
            }
            _carpetMesh.Clear();
            _carpetMesh.SetVertices(verts);
            _carpetMesh.SetUVs(0, uvs);
            _carpetMesh.SetTriangles(tris, 0);
            _carpetMesh.bounds = new Bounds(Vector3.zero, new Vector3(1e5f, 1e5f, 1e5f));
        }

        void SetVisible(bool on)
        {
            // Unconditional: freshly created GOs start active — SetActive(same) is a no-op in Unity.
            _visible = on;
            for (int l = 0; l < _layerGos.Length; l++)
                if (_layerGos[l] != null) _layerGos[l]!.SetActive(on);
            if (_carpetGo != null) _carpetGo.SetActive(on);
        }

        // ---- debug introspection (TclDebugConsole: `horizon_debug` / `horizon_tint`) ----
        // Flag-gated, zero cost while off: the tint is a shader global the fade block reads;
        // the report is built only on explicit console request.
        static readonly int HorizonDebugTintId = Shader.PropertyToID("_HorizonDebugTint");
        int _dbgPick = -1;
        float _dbgFocusX, _dbgFocusY;
        bool _dbgGameplay;

        // horizon_tint 1|0 — hot-pinks the rear-fade region in DigglesGround/DigglesModel
        // regardless of zone state (the shaders force depth 32 while the flag is up), so a
        // 2-second Play look answers "are the rim triangles inside the z<0 region at all".
        public static void SetDebugTint(bool on)
            => Shader.SetGlobalFloat(HorizonDebugTintId, on ? 1f : 0f);

        // Full zone→shader chain state for the `horizon_debug` console command.
        public string DebugReport()
        {
            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine(
                $"presenter: init={_root != null} groundRow={_groundRow} " +
                $"panoramaVisible={_visible} campaignGate(panorama-only)={_dbgGameplay}");
            sb.AppendLine(
                $"focus=({_dbgFocusX:F1}, {_dbgFocusY:F1})  zoneActive={_zoneActive}  pick={_dbgPick}");
            Vector4 fog = Shader.GetGlobalVector(HorizonFogId);
            sb.AppendLine(
                $"_HorizonFog global: rgb=({fog.x:F3},{fog.y:F3},{fog.z:F3}) depth={fog.w:F1}  " +
                $"tint={Shader.GetGlobalFloat(HorizonDebugTintId)}");
            var zones = _zones?.Invoke();
            if (zones == null) sb.AppendLine("zones: provider NOT WIRED");
            else
            {
                sb.AppendLine($"zones registered: {zones.Count}" +
                    (zones.Count == 0 ? "  (no horizon_set ran — Info_Fog chain dead or none placed here)" : ""));
                for (int i = 0; i < zones.Count; i++)
                {
                    var z = zones[i];
                    float dx = z.Position.x - _dbgFocusX, dy = z.Position.y - _dbgFocusY;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    sb.AppendLine(
                        $"  [{i}] pos=({z.Position.x:F1},{z.Position.y:F1},{z.Position.z:F1}) " +
                        $"rgb=({z.Red},{z.Green},{z.Blue}) depth={z.Depth} size={z.Size} " +
                        $"dist={dist:F1} {(dist < z.Size ? "IN-RANGE" : "out")}" +
                        (i == _dbgPick ? "  <== PICKED" : ""));
                }
            }
            sb.Append("fade region: root-local y < 0 (== native render z < 0, seven metres behind the deepest floor 7); full colour at y <= -depth");
            return sb.ToString();
        }
    }
}
