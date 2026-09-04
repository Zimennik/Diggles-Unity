#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using UnityEngine;

    // Play-mode RIM PROBE: hold F8 and
    // LEFT-CLICK any terrain polygon — the rim band, the blue floor, anything that looks
    // wrong. The tool highlights the picked triangle for 6 seconds and appends one full
    // diagnostic record to <project>/RimProbe/rim_probe.jsonl (+ a one-line Debug.Log
    // summary). Multiple clicks append; the file is the hand-off artifact.
    //
    // Per click it captures everything the rim analysis needs: the hit tile and its 2×2
    // corner NODES (native tile+0x18 flag word + low2 state, block idx, terrain byte,
    // cellId, dug/dugFloor, per-player fog byte, base light), the picked TRIANGLE's three
    // vertices (root-local position, vertex COLOR — 3-mat weights + the dissolve ALPHA —
    // and both UV channels), the renderer/material identity, and the frame's global state
    // (horizon zone fog, FoW params, base-light params, camera clear, ambient, sun).
    // Read-only: no gameplay, sim or render state is touched.
    [DisallowMultipleComponent]
    public sealed class RimProbeTool : MonoBehaviour
    {
        DiggableTerrain? _terrain;
        FogOfWarState? _fog;          // null in the menu / fogless worlds
        BaseLightMap? _baseLight;     // null on legacy worlds
        Transform? _root;
        int _clicks;

        public static string OutPath => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "RimProbe", "rim_probe.jsonl"));

        public void Init(DiggableTerrain terrain, FogOfWarState? fog, BaseLightMap? baseLight, Transform root)
        {
            _terrain = terrain; _fog = fog; _baseLight = baseLight; _root = root;
        }

        void Update()
        {
            if (!Input.GetKey(KeyCode.F8) || !Input.GetMouseButtonDown(0)) return;
            var cam = Camera.main;
            if (cam == null) return;
            ProbeScreenPoint(cam, Input.mousePosition);
        }

        // Probe one screen point (the F8-click path and headless capture diagnostics both
        // land here). Returns true when something was hit and a record was appended.
        // Convex model hulls LIE about what is visible (a big prop's convex collider blankets
        // whole rooms and eats rays) — walk ALL hits along the ray and dump the full chain;
        // the tri/tile detail comes from the first NON-convex hit (terrain has real meshes).
        public bool ProbeScreenPoint(Camera cam, Vector3 screenPos)
        {
            if (_terrain == null || _root == null) return false;
            var ray = cam.ScreenPointToRay(screenPos);
            var hits = Physics.RaycastAll(ray, 1000f);
            if (hits.Length == 0)
            {
                Debug.Log($"[RimProbe] probe ({screenPos.x:F0},{screenPos.y:F0}): no collider hit");
                return false;
            }
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            int pick = -1;
            for (int i = 0; i < hits.Length; i++)
                if (hits[i].triangleIndex >= 0) { pick = i; break; }
            if (pick < 0) pick = 0;
            try { Dump(cam, hits[pick], screenPos, hits); }
            catch (Exception e) { Debug.LogError($"[RimProbe] dump failed: {e}"); }
            return true;
        }

        static string HitLabel(in RaycastHit h)
        {
            var go = h.collider != null ? h.collider.gameObject : null;
            if (go == null) return "?";
            string name = go.name;
            var p = go.transform.parent;
            for (int i = 0; i < 3 && p != null; i++, p = p.parent) name = p.name + "/" + name;
            return name;
        }

        void Dump(Camera cam, RaycastHit hit, Vector3 screenPos, RaycastHit[]? chain = null)
        {
            var t = _terrain!;
            var r = t.WorldResult;
            var root = _root!;
            var sb = new StringBuilder(4096);
            var ci = CultureInfo.InvariantCulture;

            Vector3 lp = root.InverseTransformPoint(hit.point);
            // Root-local → native frame: x = tileX, −z = tileY, y = render z (metres).
            float nx = lp.x, ny = -lp.z, nz = lp.y;
            int tx = Mathf.FloorToInt(nx), ty = Mathf.FloorToInt(ny);

            sb.Append('{');
            sb.AppendFormat(ci, "\"click\":{0},\"time\":\"{1:HH:mm:ss}\",", ++_clicks, DateTime.Now);
            sb.AppendFormat(ci, "\"screen\":[{0:F0},{1:F0}],", screenPos.x, screenPos.y);
            sb.AppendFormat(ci, "\"world\":[{0:F3},{1:F3},{2:F3}],", hit.point.x, hit.point.y, hit.point.z);
            sb.AppendFormat(ci, "\"native\":[{0:F3},{1:F3},{2:F3}],", nx, ny, nz);
            sb.AppendFormat(ci, "\"tile\":[{0},{1}],", tx, ty);
            sb.AppendFormat(ci, "\"go\":\"{0}\",", hit.collider != null ? hit.collider.gameObject.name : "?");
            if (chain != null && chain.Length > 0)
            {
                sb.Append("\"chain\":[");
                int n = Math.Min(chain.Length, 6);
                for (int i = 0; i < n; i++)
                {
                    var h = chain[i];
                    sb.Append(i > 0 ? ",{" : "{");
                    sb.AppendFormat(ci, "\"d\":{0:F1},\"tri\":{1},\"go\":\"{2}\"",
                        h.distance, h.triangleIndex, HitLabel(h));
                    var hr = h.collider != null ? h.collider.GetComponentInParent<MeshRenderer>() : null;
                    if (hr != null && hr.sharedMaterial != null)
                        sb.AppendFormat(ci, ",\"mat\":\"{0}\"", hr.sharedMaterial.name);
                    sb.Append('}');
                }
                sb.Append("],");
            }

            // The 2×2 corner nodes of the hit cell (self / E / S / SE — the native sampling).
            sb.Append("\"nodes\":[");
            for (int i = 0; i < 4; i++)
            {
                int cx = tx + (i & 1), cy = ty + (i >> 1);
                sb.Append(i > 0 ? ",{" : "{");
                sb.AppendFormat(ci, "\"n\":[{0},{1}]", cx, cy);
                if (r != null && cx >= 0 && cy >= 0 && cx < t.Width && cy < t.Height)
                {
                    int idx = cy * t.Width + cx;
                    ushort flags = idx < r.NativeTerrainFlags.Length ? r.NativeTerrainFlags[idx] : (ushort)0;
                    sb.AppendFormat(ci, ",\"flags\":\"0x{0:X4}\",\"state\":{1}", flags, flags & 3);
                    sb.AppendFormat(ci, ",\"block\":{0}", idx < r.BlockIdx.Length ? r.BlockIdx[idx] : 0);
                    sb.AppendFormat(ci, ",\"terrain\":\"0x{0:X2}\"", idx < r.Terrain.Length ? r.Terrain[idx] : 0);
                    sb.AppendFormat(ci, ",\"cellId\":\"0x{0:X4}\"", idx < r.CellId.Length ? r.CellId[idx] : 0);
                    if (r.DugFloor != null && idx < r.DugFloor.Length)
                        sb.AppendFormat(ci, ",\"dugFloor\":{0}", r.DugFloor[idx] ? "true" : "false");
                    sb.AppendFormat(ci, ",\"hmap\":{0}", t.GetHmap(cx, cy));
                    if (_fog != null)
                        sb.AppendFormat(ci, ",\"fogP0\":\"0x{0:X2}\"", _fog.FogAt(cx, cy, 0));
                    if (_baseLight != null)
                        sb.AppendFormat(ci, ",\"baseLight\":{0}", _baseLight.At(cx, cy));
                }
                sb.Append('}');
            }
            sb.Append("],");

            // The picked triangle: root-local positions + vertex colors (the 3-mat weights
            // in rgb, the DISSOLVE ALPHA in a) + both UV channels.
            var mc = hit.collider as MeshCollider;
            var mesh = mc != null ? mc.sharedMesh : null;
            if (mesh != null && hit.triangleIndex >= 0)
            {
                var tris = mesh.triangles;
                var verts = mesh.vertices;
                var cols = mesh.colors;
                var uv0 = mesh.uv;
                var uv1 = mesh.uv2;
                int b = hit.triangleIndex * 3;
                sb.Append("\"tri\":[");
                var tr = hit.collider!.transform;
                for (int k = 0; k < 3 && b + k < tris.Length; k++)
                {
                    int vi = tris[b + k];
                    var v = verts[vi];
                    var wv = tr.TransformPoint(v);
                    var lv = root.InverseTransformPoint(wv);
                    sb.Append(k > 0 ? ",{" : "{");
                    sb.AppendFormat(ci, "\"local\":[{0:F3},{1:F3},{2:F3}]", lv.x, lv.y, lv.z);
                    if (cols != null && vi < cols.Length)
                        sb.AppendFormat(ci, ",\"color\":[{0:F3},{1:F3},{2:F3},{3:F3}]",
                            cols[vi].r, cols[vi].g, cols[vi].b, cols[vi].a);
                    if (uv0 != null && vi < uv0.Length)
                        sb.AppendFormat(ci, ",\"uv\":[{0:F3},{1:F3}]", uv0[vi].x, uv0[vi].y);
                    if (uv1 != null && vi < uv1.Length)
                        sb.AppendFormat(ci, ",\"uv1\":[{0:F3},{1:F3}]", uv1[vi].x, uv1[vi].y);
                    sb.Append('}');
                    // Highlight the picked triangle in the scene for 6 seconds.
                    int vj = tris[b + (k + 1) % 3];
                    Debug.DrawLine(wv, tr.TransformPoint(verts[vj]), Color.magenta, 6f);
                }
                sb.Append("],");
            }

            // Renderer / material identity.
            var rend = hit.collider != null ? hit.collider.GetComponent<MeshRenderer>() : null;
            var mat = rend != null ? rend.sharedMaterial : null;
            if (mat != null)
            {
                sb.AppendFormat(ci, "\"material\":\"{0}\",\"shader\":\"{1}\",", mat.name, mat.shader.name);
                if (mat.HasProperty("_Ambient"))
                {
                    try
                    {
                        var a = mat.GetVector("_Ambient");
                        sb.AppendFormat(ci, "\"matAmbient\":[{0:F3},{1:F3},{2:F3}],", a.x, a.y, a.z);
                    }
                    catch { /* property type varies per shader — diagnostics only */ }
                }
            }

            // Frame globals: zone fog, FoW, base light, clear, ambient, sun.
            var hz = Shader.GetGlobalVector("_HorizonFog");
            var fow = Shader.GetGlobalVector("_FowParams");
            var blp = Shader.GetGlobalVector("_BaseLightParams");
            sb.AppendFormat(ci, "\"horizonFog\":[{0:F3},{1:F3},{2:F3},{3:F1}],", hz.x, hz.y, hz.z, hz.w);
            sb.AppendFormat(ci, "\"fowParams\":[{0:F0},{1:F0},{2:F0}],", fow.x, fow.y, fow.z);
            sb.AppendFormat(ci, "\"baseLightParams\":[{0:F0},{1:F0},{2:F0},{3:F2}],", blp.x, blp.y, blp.z, blp.w);
            var cc = cam.backgroundColor;
            sb.AppendFormat(ci, "\"camClear\":[{0:F3},{1:F3},{2:F3}],\"camFlags\":\"{3}\",", cc.r, cc.g, cc.b, cam.clearFlags);
            var amb = RenderSettings.ambientLight;
            sb.AppendFormat(ci, "\"ambient\":[{0:F3},{1:F3},{2:F3}],\"ambientMode\":\"{3}\",",
                amb.r, amb.g, amb.b, RenderSettings.ambientMode);
            var sun = RenderSettings.sun;
            if (sun != null)
                sb.AppendFormat(ci, "\"sun\":{{\"color\":[{0:F3},{1:F3},{2:F3}],\"intensity\":{3:F2},\"dir\":[{4:F2},{5:F2},{6:F2}]}},",
                    sun.color.r, sun.color.g, sun.color.b, sun.intensity,
                    sun.transform.forward.x, sun.transform.forward.y, sun.transform.forward.z);
            var cp = cam.transform.position;
            sb.AppendFormat(ci, "\"cam\":[{0:F2},{1:F2},{2:F2}]", cp.x, cp.y, cp.z);
            sb.Append('}');

            Directory.CreateDirectory(Path.GetDirectoryName(OutPath)!);
            File.AppendAllText(OutPath, sb.ToString() + Environment.NewLine);

            // Compact on-screen summary: tile, corner states, the tri's alphas.
            string states = "?", alphas = "?";
            if (r != null)
            {
                int W = t.Width, H = t.Height;
                int S(int cx, int cy)
                {
                    if (cx < 0) cx = 0; else if (cx >= W) cx = W - 1;
                    if (cy < 0) cy = 0; else if (cy >= H) cy = H - 1;
                    int idx = cy * W + cx;
                    return idx < r.NativeTerrainFlags.Length ? r.NativeTerrainFlags[idx] & 3 : -1;
                }
                states = $"{S(tx, ty)}{S(tx + 1, ty)}{S(tx, ty + 1)}{S(tx + 1, ty + 1)}";
            }
            if (mesh != null && hit.triangleIndex >= 0)
            {
                var cols = mesh.colors; var tris = mesh.triangles;
                int b = hit.triangleIndex * 3;
                if (cols != null && cols.Length > 0 && b + 2 < tris.Length)
                    alphas = string.Format(ci, "{0:F2}/{1:F2}/{2:F2}",
                        cols[tris[b]].a, cols[tris[b + 1]].a, cols[tris[b + 2]].a);
            }
            Debug.Log($"[RimProbe] #{_clicks} tile ({tx},{ty}) states[NW,NE,SW,SE]={states} triAlpha={alphas} → {OutPath}");
        }
    }
}
