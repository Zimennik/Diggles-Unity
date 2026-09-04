#nullable enable

namespace Diggles.Runtime.Tools
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // Play-mode coordinate diagnostic. Helps find why TCL-positioned things (cutscene
    // beam/summon/elf-move, camera focus) appear shifted vs where they should be.
    //
    // It cross-checks the THREE coordinate spaces that must agree:
    // * native — Diggles/TCL coords (entity.Position, pen values, camera focus);
    // * local — under WorldRoot (Q_x(-90)); localPos = NativeToLocal(native);
    // * world — Unity world (terrain verts, transform.position, the click raycast).
    //
    // For every live entity it shows native, the ACTUAL GameObject world pos, the
    // EXPECTED world pos = WorldRoot.TransformPoint(NativeToLocal(native)), and the
    // DELTA between them. A non-zero delta means that object's transform diverged from
    // its native coords (the bug). A zero delta everywhere ⇒ the mapping is fine and any
    // perceived shift is the CAMERA (compare the magenta focus marker to the actors).
    //
    // Controls:
    // * RIGHT-click terrain → drops a green marker at the hit and logs world↔native
    // (left-click stays free for walk).
    // * "Place at native" button → drops a cyan marker at the inspector `testNative`
    // coord via NativeToLocal, so you can see where a given TCL coord renders vs the
    // terrain tile of the same index.
    // * magenta sphere = the cutscene camera's focus point (where it's looking).
    //
    // Add it by hand or via DigglesWorldRuntime.showCoordProbe.
    public sealed class DigglesCoordProbe : MonoBehaviour
    {
        public DigglesWorldRuntime? Runtime;
        public Camera? Cam;
        public bool showEntityLabels = true;
        [Tooltip("Native (TCL) coord to drop a test marker at via the 'Place at native' button.")]
        public Vector3 testNative = new Vector3(231f, 30f, 13f);

        readonly List<GameObject> _markers = new();
        GameObject? _focusMarker;
        Material? _mat;
        GUIStyle? _style;
        string _nativeInput = "226.54 30.44 14.78";   // a failed cutscene walk target (edit freely)

        DigglesTclContext? Ctx => Runtime != null ? Runtime.Context : null;
        Transform? WorldRoot => Ctx != null ? Ctx.SpawnParent : null;

        void Awake()
        {
            if (Runtime == null) Runtime = FindObjectOfType<DigglesWorldRuntime>();
            if (Cam == null) Cam = Camera.main;
        }

        Material Mat()
        {
            if (_mat != null) return _mat;
            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            _mat = new Material(sh) { name = "CoordProbeMat" };
            return _mat;
        }

        GameObject MakeMarker(Vector3 worldPos, Color color, float size, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "CoordProbe_" + name;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);                 // never block the walk raycast
            go.transform.position = worldPos;
            go.transform.localScale = Vector3.one * size;
            var mr = go.GetComponent<MeshRenderer>();
            var m = new Material(Mat()) { name = "CoordProbeMat_" + name };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            mr.sharedMaterial = m;
            return go;
        }

        void DropMarker(Vector3 worldPos, Color color, string name)
        {
            _markers.Add(MakeMarker(worldPos, color, 0.5f, name));
            if (_markers.Count > 40) { Destroy(_markers[0]); _markers.RemoveAt(0); }
        }

        void Update()
        {
            if (Cam == null) Cam = Camera.main;
            if (Cam == null) return;

            // Magenta marker tracks the cutscene camera focus (where the camera looks).
            var cam = Ctx?.Camera;
            if (cam != null && WorldRoot != null)
            {
                Vector3 fw = WorldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(cam.Focus));
                if (_focusMarker == null) _focusMarker = MakeMarker(fw, Color.magenta, 0.7f, "Focus");
                _focusMarker.transform.position = fw;
            }

            // RIGHT-click → probe the terrain point (left-click is walk).
            if (Input.GetMouseButtonDown(1) &&
                Physics.Raycast(Cam.ScreenPointToRay(Input.mousePosition), out var hit, 10000f))
            {
                Vector3 nat = WorldRoot != null
                    ? RuntimeWorldSpawner.LocalToNative(WorldRoot.InverseTransformPoint(hit.point))
                    : hit.point;
                DropMarker(hit.point, Color.green, "Click");
                Debug.Log($"[CoordProbe] RIGHT-click  world={Fmt(hit.point)}  native={Fmt(nat)}  collider={hit.collider.name}");
            }
        }

        void OnGUI()
        {
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };

            // World-space labels for entities (native + delta).
            if (showEntityLabels && Ctx != null && Cam != null && WorldRoot != null)
            {
                foreach (var e in Ctx.Entities.Values)
                {
                    if (e.GameObject == null) continue;
                    Vector3 wp = e.GameObject.transform.position;
                    var sp = Cam.WorldToScreenPoint(wp);
                    if (sp.z <= 0f) continue;
                    Vector3 expected = WorldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(e.Position));
                    float d = (wp - expected).magnitude;
                    string col = d > 0.5f ? "red" : "lime";
                    GUI.Label(new Rect(sp.x - 60f, Screen.height - sp.y - 8f, 220f, 16f),
                        $"<color={col}>{e.Class?.Name}#{e.Handle} n={Fmt(e.Position)} Δ{d:F2}</color>", _style);
                }
            }

            // Control panel.
            GUILayout.BeginArea(new Rect(8, 8, 430, 230), GUI.skin.box);
            GUILayout.Label("<b>Diggles Coord Probe</b>  (RIGHT-click terrain to probe)", _style);
            var cam = Ctx?.Camera;
            if (cam != null && WorldRoot != null)
            {
                Vector3 fw = WorldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(cam.Focus));
                GUILayout.Label($"Camera focus  native={Fmt(cam.Focus)}  world={Fmt(fw)}  zoom={cam.Zoom:F2}", _style);
                if (Cam != null) GUILayout.Label($"Main cam pos={Fmt(Cam.transform.position)}  seq={(Ctx!.SequenceActive ? "ON" : "off")}", _style);
            }
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Native:", _style, GUILayout.Width(45));
            _nativeInput = GUILayout.TextField(_nativeInput, GUILayout.Width(160));
            if (GUILayout.Button("Drop CYAN here") && WorldRoot != null)
            {
                var p = _nativeInput.Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3
                    && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nx)
                    && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ny)
                    && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nz))
                {
                    var nat = new Vector3(nx, ny, nz);
                    Vector3 w = WorldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nat));
                    DropMarker(w, Color.cyan, "Native");
                    Debug.Log($"[CoordProbe] native={Fmt(nat)} → world={Fmt(w)}");
                }
            }
            GUILayout.EndHorizontal();
            // Quick delta summary for the first few entities with a GameObject.
            if (Ctx != null && WorldRoot != null)
            {
                int shown = 0;
                foreach (var e in Ctx.Entities.Values)
                {
                    if (e.GameObject == null || e.Class == null) continue;
                    Vector3 wp = e.GameObject.transform.position;
                    Vector3 expected = WorldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(e.Position));
                    float d = (wp - expected).magnitude;
                    GUILayout.Label($"{e.Class.Name}#{e.Handle}: n={Fmt(e.Position)} actual={Fmt(wp)} Δ{d:F2}", _style);
                    if (++shown >= 5) break;
                }
            }
            if (GUILayout.Button("Clear markers"))
            {
                foreach (var m in _markers) if (m != null) Destroy(m);
                _markers.Clear();
            }
            GUILayout.EndArea();
        }

        static string Fmt(Vector3 v) =>
            $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
    }
}
