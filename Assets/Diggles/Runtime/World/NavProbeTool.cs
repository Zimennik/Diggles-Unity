#nullable enable

namespace Diggles.Runtime.World
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using Diggles.Runtime.Navigation;
    using UnityEngine;

    // NAV PROBE: F9+click terrain TWICE in Play —
    // first click sets the route START, second the GOAL — and the tool runs Calc1 between them
    // (both canClimb variants) and appends a COMPLETE reproduction record to
    // <project>/NavProbe/nav_probe.jsonl: endpoints, the settle trace, both route results, and
    // for the bounding corridor every tile's BaseCost (+0x06), raw exit byte (+0x12) and the
    // full 8×8 sub-cell plane render ('.'=none, 'w'=walk, 'F'=walk+floor, '#'=other bits).
    // The record is self-sufficient to rebuild the exact failure headlessly in a test.
    // Read-only; wired next to RimProbeTool for every world.
    public sealed class NavProbeTool : MonoBehaviour
    {
        DiggableTerrain? _terrain;
        Transform? _root;
        int _probes;
        Vector2? _pendingStart;   // native (nx, ny), sub-cell resolution

        public static string OutPath => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "NavProbe", "nav_probe.jsonl"));

        public void Init(DiggableTerrain terrain, Transform root)
        {
            _terrain = terrain;
            _root = root;
        }

        void Update()
        {
            if (!Input.GetKey(KeyCode.F9) || !Input.GetMouseButtonDown(0)) return;
            var cam = Camera.main;
            if (cam == null || _terrain == null || _root == null) return;

            var ray = cam.ScreenPointToRay(Input.mousePosition);
            var hits = Physics.RaycastAll(ray, 1000f);
            if (hits.Length == 0) { Debug.Log("[NavProbe] no collider hit"); return; }
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            int pick = -1;
            for (int i = 0; i < hits.Length; i++)
                if (hits[i].triangleIndex >= 0) { pick = i; break; }
            if (pick < 0) pick = 0;

            Vector3 lp = _root.InverseTransformPoint(hits[pick].point);
            // Root-local → native frame (the RimProbeTool law): x = tileX, −z = tileY.
            var native = new Vector2(lp.x, -lp.z);

            if (_pendingStart == null)
            {
                _pendingStart = native;
                Debug.Log($"[NavProbe] START set at native ({native.x:F2},{native.y:F2}) — F9+click the GOAL");
                return;
            }

            var start = _pendingStart.Value;
            _pendingStart = null;
            try { DumpRoute(start, native); }
            catch (Exception e) { Debug.LogError($"[NavProbe] dump failed: {e}"); }
        }

        // Public for headless/console use: probe an explicit native start/goal pair.
        public void DumpRoute(Vector2 startNative, Vector2 goalNative)
        {
            var t = _terrain ?? throw new InvalidOperationException("NavProbeTool not initialized");
            var nav = t.Nav ?? throw new InvalidOperationException("terrain has no NavWorld");
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(16384);

            int sx = Mathf.RoundToInt(startNative.x * 8f), sy = Mathf.RoundToInt(startNative.y * 8f);
            int gx = Mathf.RoundToInt(goalNative.x * 8f), gy = Mathf.RoundToInt(goalNative.y * 8f);

            sb.Append('{');
            sb.AppendFormat(ci, "\"probe\":{0},\"time\":\"{1:HH:mm:ss}\",", ++_probes, DateTime.Now);
            sb.AppendFormat(ci, "\"startNative\":[{0:F3},{1:F3}],\"goalNative\":[{2:F3},{3:F3}],",
                startNative.x, startNative.y, goalNative.x, goalNative.y);
            sb.AppendFormat(ci, "\"startSub\":[{0},{1}],\"goalSub\":[{2},{3}],", sx, sy, gx, gy);

            // The Calc1 endpoint settle (native &2 slide) — recorded so a divergence is visible.
            int ssx = sx, ssy = sy, sgx = gx, sgy = gy;
            while ((nav.SubAt(ssx, ssy + 1) & NavWorld.SubFloor) != 0) ssy++;
            while ((nav.SubAt(sgx, sgy + 1) & NavWorld.SubFloor) != 0) sgy++;
            sb.AppendFormat(ci, "\"settledStart\":[{0},{1}],\"settledGoal\":[{2},{3}],", ssx, ssy, sgx, sgy);

            foreach (bool climb in new[] { true, false })
            {
                var pf = new Calc1Pathfinder(nav);
                var path = pf.FindPath(sx, sy, gx, gy, canClimb: climb);
                sb.AppendFormat(ci, "\"route_climb_{0}\":", climb ? 1 : 0);
                if (path == null) sb.Append("null,");
                else
                {
                    sb.Append('[');
                    for (int i = 0; i < path.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.AppendFormat(ci, "[{0},{1}]", path[i].sx, path[i].sy);
                    }
                    sb.Append("],");
                }
            }

            // The bounding corridor, padded: every tile's record + plane.
            int tx0 = Math.Min(sx, gx) / 8 - 1, tx1 = Math.Max(sx, gx) / 8 + 1;
            int ty0 = Math.Min(sy, gy) / 8 - 1, ty1 = Math.Max(sy, gy) / 8 + 1;
            tx0 = Math.Max(0, tx0); ty0 = Math.Max(0, ty0);
            tx1 = Math.Min(nav.Width - 1, tx1); ty1 = Math.Min(nav.Height - 1, ty1);
            sb.AppendFormat(ci, "\"rect\":[{0},{1},{2},{3}],\"tiles\":[", tx0, ty0, tx1, ty1);
            bool firstTile = true;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    if (!firstTile) sb.Append(',');
                    firstTile = false;
                    int tile = ty * nav.Width + tx;
                    sb.Append('{');
                    sb.AppendFormat(ci, "\"t\":[{0},{1}],\"cost\":{2},\"exit\":{3},\"plane\":[",
                        tx, ty, nav.BaseCost[tile], nav.ExitPairAndFlags[tile]);
                    for (int row = 0; row < 8; row++)
                    {
                        if (row > 0) sb.Append(',');
                        sb.Append('"');
                        for (int col = 0; col < 8; col++)
                        {
                            byte b = nav.SubAt(tx * 8 + col, ty * 8 + row);
                            bool walk = (b & NavWorld.SubWalk) != 0;
                            bool floor = (b & NavWorld.SubFloor) != 0;
                            sb.Append(walk && floor ? 'F' : walk ? 'w'
                                : b == 0 ? '.' : '#');
                        }
                        sb.Append('"');
                    }
                    sb.Append("]}");
                }
            sb.Append("]}");

            Directory.CreateDirectory(Path.GetDirectoryName(OutPath)!);
            File.AppendAllText(OutPath, sb.ToString() + Environment.NewLine);
            Debug.Log($"[NavProbe] route dumped: climb={(sb.ToString().Contains("\"route_climb_1\":null") ? "NULL" : "found")} → {OutPath}");
        }
    }
}
