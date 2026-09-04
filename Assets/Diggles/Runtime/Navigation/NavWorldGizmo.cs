#nullable enable

namespace Diggles.Runtime.Navigation
{
    using System.Collections.Generic;
    using Diggles.Runtime.World;
    using UnityEngine;

    // Debug visualiser for the pathfinding data layer (NavWorld). Draws the original's +0x3c
    // walkability sub-grid in the side-view "ant farm" frame: native +y = down/deeper, height
    // nibble = slab depth into the screen.
    //
    // Cyan floor strips — a +0x3c value-3 cell marks a ground line (the boundary row between an open
    // pocket above (−y) and supporting material below (+y)). The standable floor face extends
    // into the screen across the open depth range, so each marker renders as a thin slab
    // spanning depth z = min(h_above, h_own).. max(h_own, h_below) at the support boundary.
    // On the meadow that is the full 8-block depth; in the h=11 corridors a 2-block strip.
    // Magenta climb cubes — +0x3c value-1 climbable wall cells (vertical faces along tile-Y): a cube
    // at the cell's own depth on the relief.
    //
    // Auto-added next to DiggableTerrain by RuntimeMapBuilder. Gizmos draw in the Scene view (and in the
    // Game view if its toolbar "Gizmos" toggle is on). Right-click the component → Rebuild after a dig.
    public sealed class NavWorldGizmo : MonoBehaviour
    {
        public bool drawFloors = true;
        // Off by default: a restored world carries the native +0x3c band, which marks every
        // climbable wall face (~92,000 cells on urw_3 vs ~900 in a port-built fresh world), and
        // drawing those per editor frame costs ~30 FPS. Turn on when you need the climb overlay.
        public bool drawWalls = false;
        public float cubeSize = 0.08f;       // climb-wall cube size
        public float heightLift = 0.3f;      // climb-wall lift along world up (game up)
        public float floorLift = 0.04f;      // floor-strip lift along game up (local +Z), avoids z-fighting
        public int maxMarkers = 120000;      // safety cap (a full multi-template world can be large)
        public int maxWallMarkers = 4000;    // climb cubes drawn per frame even when drawWalls is on
        public Color floorColor = new Color(0.10f, 0.85f, 1f, 0.95f);    // cyan — ground-line floor strips
        public Color wallColor  = new Color(1f, 0.15f, 0.85f, 0.95f);    // magenta — climbable wall faces

        // Floors are slabs in the WorldRoot local frame (drawn via Gizmos.matrix); walls are world cubes.
        readonly List<(Vector3 c, Vector3 s)> _floorSlabs = new();
        readonly List<Vector3> _walls = new();
        NavWorld? _builtFrom;
        int _builtVersion = -1;             // NavWorld.Version at the last Rebuild
        float _nextAutoRebuild;             // realtime debounce for carve-driven rebuilds

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            _floorSlabs.Clear(); _walls.Clear();
            var nav = GetComponent<DiggableTerrain>()?.Nav;
            _builtFrom = nav;
            _builtVersion = nav?.Version ?? -1;
            if (nav == null) return;
            var tf = transform;     // the WorldRoot (Q_x(-90)); local frame = (nativeX, depth_z, -nativeY)
            int count = 0;
            // Outer loop over tiles, skipping ones with no walkable +0x3c sub-cell (TileValid == false ⇒
            // solid) so a sparse world costs ~one check per tile, not 64.
            for (int ty = 0; ty < nav.Height && count < maxMarkers; ty++)
            for (int tx = 0; tx < nav.Width && count < maxMarkers; tx++)
            {
                if (!nav.TileValid(tx, ty)) continue;        // no +0x3c walkable sub-cell here
                for (int sy = 0; sy < 8 && count < maxMarkers; sy++)
                for (int sx = 0; sx < 8 && count < maxMarkers; sx++)
                {
                    int subX = tx * 8 + sx, subY = ty * 8 + sy;
                    if (!nav.SubWalkable(subX, subY)) continue;       // +0x3c bit 0x01
                    if (nav.SubIsFloor(subX, subY))
                    {
                        // Ground line: support boundary + the open depth range it spans.
                        int hO = nav.GetHmapSub(subX, subY);
                        int hA = nav.GetHmapSub(subX, subY - 1);      // above = −y (game up); OOB ⇒ 15
                        int hB = nav.GetHmapSub(subX, subY + 1);
                        int lo = Mathf.Min(hA, hO), hi = Mathf.Max(hO, hB);
                        if (hi <= lo) hi = lo + 1;                    // keep a visible sliver
                        // The floor face sits at the top edge of the support row: this row if it is the
                        // support (hO > hA), else the row below (last-open-row marker over h(below) solid).
                        float faceY = (hO > hA ? subY : subY + 1) * 0.125f;
                        var c = new Vector3(
                            subX * 0.125f + 0.0625f,                  // east, cell centre
                            ((lo + hi) * 0.5f) * 0.5f + 7f,           // depth-strip centre (nibble → z)
                            -faceY + floorLift);                      // local +Z = native −y = game up
                        var s = new Vector3(0.125f, (hi - lo) * 0.5f, 0.02f);
                        _floorSlabs.Add((c, s));
                    }
                    else
                    {
                        // Climbable wall (value 1) — placed at the cell's own depth on the relief.
                        int h = nav.GetHmapSub(subX, subY);
                        var local = new Vector3(subX * 0.125f, h * 0.5f + 7f, -(subY * 0.125f));
                        _walls.Add(tf.TransformPoint(local) + Vector3.up * heightLift);
                    }
                    count++;
                }
            }
            if (count >= maxMarkers)
                Debug.LogWarning($"[NavWorldGizmo] capped at {maxMarkers} markers — raise maxMarkers to show all.");
            else
                Debug.Log($"[NavWorldGizmo +0x3c] {_floorSlabs.Count} floor strips (cyan) + {_walls.Count} climb cells (magenta).");
        }

        void OnDrawGizmos()
        {
            // Lazilybuild when the world's NavWorld first appears or is replaced (new world /
            // rebake), and when its contents changed (Version bumps on every RevalidateRect, i.e.
            // every carve/growth stamp). An identity-only check would freeze the overlay, since digs
            // mutate the same NavWorld in place. Debounced — a continuous dig re-pulls ≤2×/s.
            var nav = GetComponent<DiggableTerrain>()?.Nav;
            if (!ReferenceEquals(nav, _builtFrom)) Rebuild();
            else if (nav != null && nav.Version != _builtVersion
                     && Time.realtimeSinceStartup >= _nextAutoRebuild)
            {
                _nextAutoRebuild = Time.realtimeSinceStartup + 0.5f;
                Rebuild();
            }
            if (drawFloors && _floorSlabs.Count > 0)
            {
                var prev = Gizmos.matrix;
                Gizmos.matrix = transform.localToWorldMatrix;     // draw slabs in the WorldRoot local frame
                Gizmos.color = floorColor;
                for (int i = 0; i < _floorSlabs.Count; i++) Gizmos.DrawCube(_floorSlabs[i].c, _floorSlabs[i].s);
                Gizmos.matrix = prev;
            }
            if (drawWalls)
            {
                var s = Vector3.one * cubeSize;
                Gizmos.color = wallColor;
                int wallCount = Mathf.Min(_walls.Count, maxWallMarkers);
                for (int i = 0; i < wallCount; i++) Gizmos.DrawCube(_walls[i], s);
            }
        }
    }
}
