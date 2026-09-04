#nullable enable

namespace Diggles.Runtime.World
{
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Rendering;

    // Dig-tool feedback — a faithful port of the original's DigMark renderer as a SEPARATE
    // world-space object (the Play review rejected the terrain-shader overlay: it
    // projected the marks through every depth layer, and 
    // — which is exactly what the native does):
    // • PERSISTENT dig marks ("DigMark 2", FUN_0056af90) — every tile whose player dig nibble
    // (tile+0x08 analogue, DiggableTerrain._digState) is set contributes a corner to a
    // marching-squares field; nibble bit 3 splits the palette: (cnt|8) depth marks → the
    // STEEL-BLUE pass (A=0xB3), the standard-tunnel dummy (3) → the FAINT GREEN pass (A=0x33);
    // • HOVER/MARQUEE ("DigMark 4", FUN_0056d0b0) — the armed-tool pre-click hover (native:
    // outline-only rings, job factor 0x50 vs the fill 0x32 → ×1.6 brightness) and the drag
    // marquee (filled); the marquee grid holds 1 per rect tile, and cells failing
    // FUN_00566870 get |8 → they route into the second pass = strong RED-ORANGE (A=0xAF).
    //
    // NATIVE ground truth (class DigMark, Wiggles\Map\DigMark.cpp leaked at FUN_00568410:56):
    // all dig feedback is a WORLD-SPACE translucent vertex-colored triangle mesh regenerated
    // per frame — marching squares over the tile nibbles with rounded corners (FUN_0056ae30 →
    // FUN_00568410, see DigMarkMesher for the full tessellation RE) — on the fixed render plane
    // z = 15.0 (FUN_0056af90:757 / FUN_0056d0b0:544; half a render unit in front of the tallest
    // rock face, top_z = nibble·0.5+7 ≤ 14.5 — FUN_004c6b20), alpha-blended FVF 0x1C2 with a sin
    // position wobble and a sin(3x+3y+0.25·frame) brightness pulse. The terrain draw
    // (FUN_004ca830) never reads the nibbles — the terrain is NOT tinted.
    //
    // PORT: two child MeshRenderers under the terrain root (whose local space IS the native
    // render frame: x = native x, z = −native y, y = native render z — BdbPmpWorldBuilder
    //.EmitVertexW), meshes rebuilt only when the marks / hover change (not per frame); the
    // per-frame wobble + pulse run in the Diggles/DigMark vertex shader. The plane y = 15 floats
    // in front of every face (faces top out at 14.5), and ZTest LEqual keeps terrain that is
    // actually between the camera and the plane occluding — marks are visible ONLY at the front
    // layer, never projected down the dig axis.
    //
    // Palettes (Diggles.exe.rdata, f = (sin·7 + offset)/64 per vertex, channels clamp at 255):
    // std/valid GREEN A=0x33, offset 135 (_DAT_0073761c): rgb = f·(56.064, 87.04, 43.008)
    // (_DAT_007375d0/_DAT_007375fc/_DAT_007375c0) → mid (118, 184, 91);
    // depth STEEL-BLUE A=0xB3, offset 45 (_DAT_007375c4): rgb = f·(140.288, 144.384, 260.096)
    // (_DAT_00737628/_DAT_0073762c/_DAT_0073765c) → mid (99, 102, 183);
    // invalid RED-ORANGE A=0xAF, offset 135: rgb = f·(122.88, 26.112, 12.032)
    // (_DAT_00737610/_DAT_00737590/_DAT_00737544) → mid (255⌈259⌉, 55, 25).
    //
    // Data flow (unchanged from the mark-map version):
    // DiggableTerrain.DigMarkChanged → persistent mesh rebuild (committed marks, Owner)
    // ClickToMoveService → PublishHover/ClearHover (static) → hover mesh rebuild
    // Update (or Pump in EditMode tests) rebuilds dirty meshes + applies GlobalEnabled.
    [DisallowMultipleComponent]
    public sealed class DigMarkOverlay : MonoBehaviour
    {
        // The native render plane z (FUN_0056af90:757) — terrain-local Y in the port.
        public const float PlaneY = 15f;

        // ---- palettes at mid-pulse (public for tests; derivation in the header) ----
        public static readonly Color32 CommittedStdColor = new(118, 184, 91, 0x33);
        public static readonly Color32 CommittedDepthColor = new(99, 102, 183, 0xB3);
        public static readonly Color32 HoverValidColor = new(118, 184, 91, 0x33);
        public static readonly Color32 HoverInvalidColor = new(255, 55, 25, 0xAF);
        // The ERASER cursor (dig:undo tool) — the native opaque-gray paint brush (FUN_00567410,
        // mgr state 2). The RE gives no exact.rdata bytes for the erase palette (the ghost is a
        // plain gray brush, not a pulsating dig mark), so this is an inferred neutral opaque gray
        // — distinct from the translucent green/blue dig marks so the player reads "delete", not
        // "order". Rendered flat (no ×1.6 outline brighten, no valid/invalid split).
        public static readonly Color32 HoverEraseColor = new(150, 150, 150, 0xC8);
        const float StdPulseAmp = 7f / 135f;    // pulse scale DAT_00737520=7 / palette offset
        const float DepthPulseAmp = 7f / 45f;
        const float RingJobFactor = 80f / 50f;  // outline hover job factor 0x50 vs fill 0x32
        const float MarkWobbleAmp = 0.03f;      // _DAT_00736ff8 (persistent job)
        const float HoverWobbleAmp = 0.06f;     // _DAT_00737018 (marquee/hover job)
        static readonly int WobbleAmpId = Shader.PropertyToID("_WobbleAmp");

        // Master toggle — pushed by DigglesWorldRuntime.showDigMarks (default ON).
        public static bool GlobalEnabled = true;

        // Which owner layer to visualize. 0 = the human player (the native UI shows the local
        // player's own dig orders only — FUN_0056af90 reads this+0x11c = the local player index).
        public int Owner;

        // ---- hover contract (rewritten by ClickToMoveService whenever the hover set changes) ----
        // outline=true = the native pre-drag hover (ring only, brighter — FUN_0056d0b0 single-cell
        // mode, local_48=1); outline=false = the drag marquee / cave stamp / eraser (filled cells).
        // The cell set is ARBITRARY — a 2×2 anchor block, a grown tunnel rect, the eraser's 2×2
        // window, OR the whole carve-cell MASK of a cave template (each with its per-cell verdict).
        // style = Dig (green valid / red-orange invalid) or Erase (the opaque-gray delete brush).
        public enum HoverStyle { Dig, Erase }
        static readonly List<(int tx, int ty, bool valid)> s_hover = new();
        static bool s_hoverOutline;
        static HoverStyle s_hoverStyle;
        static int s_hoverVersion;

        public static int HoverVersion => s_hoverVersion;

        // Publish the current hover cell set. No-ops (keeps the version) when the set + outline +
        // style are all unchanged, so per-frame republishing is free while the mouse rests.
        public static void PublishHover(List<(int tx, int ty, bool valid)> cells,
            bool outline = false, HoverStyle style = HoverStyle.Dig)
        {
            if (outline == s_hoverOutline && style == s_hoverStyle && SameHover(cells)) return;
            s_hover.Clear();
            s_hover.AddRange(cells);
            s_hoverOutline = outline;
            s_hoverStyle = style;
            s_hoverVersion++;
        }

        public static void ClearHover()
        {
            if (s_hover.Count == 0) return;
            s_hover.Clear();
            s_hoverVersion++;
        }

        static bool SameHover(List<(int tx, int ty, bool valid)> cells)
        {
            if (cells.Count != s_hover.Count) return false;
            for (int i = 0; i < cells.Count; i++)
                if (cells[i] != s_hover[i]) return false;
            return true;
        }

        // ---- instance state ----
        DiggableTerrain? _terrain;
        readonly HashSet<int> _marked = new();      // tile indices with a nonzero Owner nibble
        readonly HashSet<long> _cellKeys = new();   // rebuild scratch: visited marching cells
        readonly Dictionary<(int, int), int> _hoverField = new();
        readonly DigMarkMeshData _md = new();
        readonly List<Vector3> _vtx = new();
        readonly List<Color32> _col = new();
        readonly List<Vector2> _uv = new();
        bool _dirtyMarks;
        int _seenHover = -1;

        GameObject? _marksGo, _hoverGo;
        Mesh? _marksMesh, _hoverMesh;
        Material? _marksMat, _hoverMat;

        public Mesh? MarksMesh => _marksMesh;   // test/debug access
        public Mesh? HoverMesh => _hoverMesh;

        public void Init(DiggableTerrain terrain)
        {
            if (_terrain != null) _terrain.DigMarkChanged -= OnMarkChanged;
            _terrain = terrain;
            terrain.DigMarkChanged += OnMarkChanged;
            EnsureRenderers();
            RebuildAll();
            _seenHover = -1;
            Pump();   // build now (don't wait a frame)
        }

        void OnDestroy()
        {
            if (_terrain != null) _terrain.DigMarkChanged -= OnMarkChanged;
            if (_marksMesh != null) DestroyImmediate(_marksMesh);
            if (_hoverMesh != null) DestroyImmediate(_hoverMesh);
            if (_marksMat != null) DestroyImmediate(_marksMat);
            if (_hoverMat != null) DestroyImmediate(_hoverMat);
        }

        void EnsureRenderers()
        {
            if (_marksGo != null) return;
            var shader = Shader.Find("Diggles/DigMark");
            _marksMesh = new Mesh { name = "DigMarks" };
            _marksMesh.MarkDynamic();
            _hoverMesh = new Mesh { name = "DigMarkHover" };
            _hoverMesh.MarkDynamic();
            _marksGo = MakeChild("DigMarks", _marksMesh, shader, MarkWobbleAmp, out _marksMat);
            _hoverGo = MakeChild("DigMarkHover", _hoverMesh, shader, HoverWobbleAmp, out _hoverMat);
        }

        GameObject MakeChild(string name, Mesh mesh, Shader? shader, float wobbleAmp, out Material? mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);   // terrain-local = the native render frame
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mat = null;
            if (shader != null)   // headless EditMode may lack the pipeline; mesh data still builds
            {
                mat = new Material(shader) { name = name + "Mat" };
                mat.SetFloat(WobbleAmpId, wobbleAmp);
                mr.sharedMaterial = mat;
            }
            return go;
        }

        void OnMarkChanged(int tx, int ty)
        {
            if (_terrain == null || (uint)tx >= (uint)_terrain.Width || (uint)ty >= (uint)_terrain.Height) return;
            int idx = ty * _terrain.Width + tx;
            if (_terrain.RawDigNibble(tx, ty, Owner) != 0) _marked.Add(idx);
            else _marked.Remove(idx);
            _dirtyMarks = true;
        }

        // Full rescan of the persistent layer (init / owner switch / bulk load).
        public void RebuildAll()
        {
            if (_terrain == null) return;
            _marked.Clear();
            int w = _terrain.Width, h = _terrain.Height;
            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                    if (_terrain.RawDigNibble(tx, ty, Owner) != 0)
                        _marked.Add(ty * w + tx);
            _dirtyMarks = true;
        }

        void Update() => Pump();

        // The per-frame pump, public so EditMode tests (no player loop) can drive it.
        // Rebuilds the meshes only when the underlying data changed; the wobble/pulse
        // animation is entirely in the Diggles/DigMark vertex shader.
        public void Pump()
        {
            if (_terrain == null || _marksGo == null || _hoverGo == null) return;
            bool on = GlobalEnabled;
            if (_marksGo.activeSelf != on) _marksGo.SetActive(on);
            if (_hoverGo.activeSelf != on) _hoverGo.SetActive(on);
            if (_dirtyMarks) { _dirtyMarks = false; RebuildMarks(); }
            if (_seenHover != s_hoverVersion) { _seenHover = s_hoverVersion; RebuildHover(); }
        }

        // ---- persistent marks: FUN_0056af90 — cells over every marked tile's 4 corners ----
        void RebuildMarks()
        {
            _md.Clear();
            _cellKeys.Clear();
            int w = _terrain!.Width;
            foreach (int t in _marked)
            {
                int tx = t % w, ty = t / w;
                for (int dy = -1; dy <= 0; dy++)
                    for (int dx = -1; dx <= 0; dx++)
                        EmitMarkCell(tx + dx, ty + dy);
            }
            Upload(_marksMesh!, hover: false, outline: false);
        }

        void EmitMarkCell(int cx, int cy)
        {
            long key = ((long)cy << 32) | (uint)cx;
            if (!_cellKeys.Add(key)) return;
            // RawDigNibble is OOB-safe (returns 0) — edge cells sample the void as unmarked.
            DigMarkMesher.EmitCell(cx, cy,
                _terrain!.RawDigNibble(cx, cy, Owner),
                _terrain.RawDigNibble(cx + 1, cy, Owner),
                _terrain.RawDigNibble(cx, cy + 1, Owner),
                _terrain.RawDigNibble(cx + 1, cy + 1, Owner),
                outline: false, _md);
        }

        // ---- hover/marquee: FUN_0056d0b0 — grid of 1s over the rect, invalid tiles |8 ----
        void RebuildHover()
        {
            _md.Clear();
            _cellKeys.Clear();
            _hoverField.Clear();
            int w = _terrain!.Width, h = _terrain.Height;
            foreach (var (tx, ty, valid) in s_hover)
            {
                if ((uint)tx >= (uint)w || (uint)ty >= (uint)h) continue;   // OOB drag cells: ignore
                _hoverField[(tx, ty)] = valid ? 1 : 9;
            }
            foreach (var kv in _hoverField)
            {
                var (tx, ty) = kv.Key;
                for (int dy = -1; dy <= 0; dy++)
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        int cx = tx + dx, cy = ty + dy;
                        long key = ((long)cy << 32) | (uint)cx;
                        if (!_cellKeys.Add(key)) continue;
                        DigMarkMesher.EmitCell(cx, cy,
                            HoverField(cx, cy), HoverField(cx + 1, cy),
                            HoverField(cx, cy + 1), HoverField(cx + 1, cy + 1),
                            s_hoverOutline, _md);
                    }
            }
            Upload(_hoverMesh!, hover: true, outline: s_hoverOutline, style: s_hoverStyle);
        }

        int HoverField(int tx, int ty) => _hoverField.TryGetValue((tx, ty), out int v) ? v : 0;

        // Native tile frame → terrain-local mesh on the plane y = 15, palette by pass selector.
        void Upload(Mesh mesh, bool hover, bool outline, HoverStyle style = HoverStyle.Dig)
        {
            _vtx.Clear(); _col.Clear(); _uv.Clear();
            for (int i = 0; i < _md.Pos.Count; i++)
            {
                var p = _md.Pos[i];
                _vtx.Add(new Vector3(p.x, PlaneY, -p.y));
                Color32 col;
                float pulse;
                if (hover)
                {
                    // Erase brush = flat opaque gray; dig hover = green valid / red-orange invalid.
                    col = style == HoverStyle.Erase ? HoverEraseColor
                        : _md.Sel[i] == 0 ? HoverValidColor : HoverInvalidColor;
                    pulse = StdPulseAmp;   // both marquee palettes pulse on offset 135 (FUN_0056d0b0:397)
                    if (outline && style != HoverStyle.Erase) col = Brighten(col, RingJobFactor);
                }
                else if (_md.Sel[i] == 0) { col = CommittedStdColor; pulse = StdPulseAmp; }
                else { col = CommittedDepthColor; pulse = DepthPulseAmp; }
                _col.Add(col);
                _uv.Add(new Vector2(pulse, 0f));
            }
            mesh.Clear();
            mesh.indexFormat = _vtx.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vtx);
            mesh.SetColors(_col);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_md.Tris, 0, true);
        }

        static Color32 Brighten(Color32 c, float k) => new(
            (byte)Mathf.Min(255f, c.r * k), (byte)Mathf.Min(255f, c.g * k),
            (byte)Mathf.Min(255f, c.b * k), c.a);
    }
}
