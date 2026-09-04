#nullable enable

namespace Diggles.Runtime.World
{
    using System.Collections.Generic;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    // The native panorama/backdrop laws — pure math, no scene objects (Docs/BACKDROP_PARALLAX_RE.md).
    //
    // Ground truth (Diggles.exe decompile):
    // FUN_0047edc0 — panorama constructor: loads Panorama_layer_0..6.tga and fills the per-layer
    // depth/scale/offset constant tables (float pool DAT_00737xxx, values extracted
    // from the exe's.rdata — see the RE doc §3).
    // FUN_00480ee0 — per-frame dispatcher: unprojects a 9×9 screen grid into rays (screen→world
    // matrix at DAT_0076a338+0x2794), stores per-ray slope/near tables.
    // FUN_004802c0 — per-layer UV grid: uCore = Ku*(slopeX*(D-1) - nearX) + 5.0,
    // vCore = -Kv*(slopeY*(D-1) - nearY) + 5.0 (the parallax law).
    // FUN_0047f440 — layer mesh emit: screen-space TLVERTs (z=0.02, rhw=50, diffuse white),
    // uv = layerScale * core + layerOffset; drawn back-to-front, sort key layer+1.
    // FUN_00480720 — "GroundRenderer2" meadow ground carpet: world-space 20-unit chunks around
    // the camera focus x, rows at render z {-60,-37.5,-15,+7.5}, y = ground-2.5.
    // FUN_004750b0 — horizon-zone scan: nearest `horizon_set` region within its size of the
    // camera focus becomes the active backdrop colour/fog for the frame.
    // FUN_00484cd0:82-104 / FUN_004856e0:162-199 — the rear fade: vertices with render z < 0
    // (7 m behind the deepest floor; no BDB terrain vertex reaches there —
    // palette z ∈ [7,14.5] measured) lerp to the active zone colour over
    // `depth` units; live targets = authored backdrop models.
    //
    // Coordinates: the "render frame" is the frame the original
    // RV_VIEW/RV_PROJ (CDisplay+0x26d4/+0x2714) consume and the screen→world inverse
    // (+0x2794) produces — x = east, y = tile rows (growing down), z = height in metres:
    // deepest dug floor z = 7 (nibble 0), undug front plane z = 14.5 (nibble 15), camera
    // look-at height = the const 14 (DigglesCameraController.AtHeight). That is exactly the
    // port's world-root local frame up to the row sign — the controller's NativeToWorldPoint
    // is the same plain swap (x, z, -y), verified against captured RV_VIEW matrices.
    // Decompile evidence that the panorama/carpet/fade share it:
    // * the horizon clip line projects the carpet plane at z = +14.5 (DAT_00737564) and
    // z = -37.5 (_DAT_007377c8) — 14.5 is the front plane in this frame;
    // * the carpet's near row sits at z = +7.5 (DAT_00737528), hugging the deepest plane 7;
    // * the vertex-brightness knee is 7.0 (DAT_00737520, FUN_00484cd0:10) — the floor;
    // * FUN_00488980:131-148 hands FUN_00484cd0 the z it just produced with the model→world
    // matrix and then renders through the same view — one frame, no nibble rescale.
    // Root-local (lx, ly, lz) ↔ render frame (lx, -lz, ly) — a pure axis swap.
    public static class BackdropLaws
    {
        // ---- root-local ↔ native render frame (row-sign axis swap, no rescale) ----
        public static Vector3 RenderFromLocal(Vector3 local) =>
            new Vector3(local.x, -local.z, local.y);

        public static Vector3 LocalFromRender(Vector3 render) =>
            new Vector3(render.x, render.z, -render.y);

        // ---- per-layer constants (FUN_0047edc0:257-288; float-pool values from Diggles.exe) ----
        // Layer 0 = farthest (drawn first), layer 5 = nearest. Texture i = Panorama_layer_i.tga.
        public static readonly float[] LayerDepth = { 21100f, 20500f, 11300f, 1000f, 200f, 100f };
        public static readonly float[] LayerScale = { 0.53f, 0.04f, 0.11f, 1.12f, 6.68f, 18.0f };
        public static readonly float[] LayerOffU = { 0f, 0f, 0f, 0f, 0.7f, 1.0f };
        public static readonly float[] LayerOffV = { 0.75f, 1.0f, 0.5f, -4.9f, -33.0f, -90.25f };
        public const int LayerCount = 6;

        public const float Ku = 0.00075f;        // _DAT_007370c8 (u axis scale, FUN_004802c0:38)
        public const float Kv = 0.0015f;         // _DAT_007370f0 (v axis scale, FUN_004802c0:39)
        public const float UvBias = 5.0f;        // DAT_007373a8 (added to both cores)
        public const float DepthBias = 1.0f;     // DAT_007372a8 (d' = D - 1, FUN_004802c0:23)

        // ---- surface gate (FUN_00480ee0:38-39) ----
        // Panorama draws only while cameraFocusRow - 20 <= groundRow (the level's surface line,
        // panorama+0x1560). Rows grow DOWNWARD, so this is "focus within 20 rows of the surface".
        public const float SurfaceGateMargin = 20f;   // DAT_00737578

        public static bool SurfaceVisible(float focusRow, float groundRow)
            => focusRow - SurfaceGateMargin <= groundRow;

        // ---- layer UV law (FUN_00480ee0 grid + FUN_004802c0 + FUN_0047f440:139/166) ----
        // nearR/farR = the pixel ray's near-plane and far-plane points in the render frame
        // (screen z=0 and z=1 unprojections — the native uses the inverse view-proj rows the
        // same way). Returns the layer's texture UV in the native (v grows down) convention.
        public static Vector2 LayerUv(int layer, Vector3 nearR, Vector3 farR)
        {
            float dz = farR.z - nearR.z;
            if (Mathf.Abs(dz) < 1e-6f) dz = dz >= 0f ? 1e-6f : -1e-6f;
            float slopeX = (farR.x - nearR.x) / dz;                 // [0x50] = Δx/Δz
            float slopeY = (farR.y - nearR.y) / dz;                 // [0x2d8] = Δy/Δz
            float dp = LayerDepth[layer] - DepthBias;
            float uCore = Ku * (slopeX * dp - nearR.x) + UvBias;    // 0xa70 grid
            float vCore = -Kv * (slopeY * dp - nearR.y) + UvBias;   // 0xcf8 grid (sign-flipped)
            float s = LayerScale[layer];
            return new Vector2(
                s * uCore + LayerOffU[layer],
                s * vCore + LayerOffV[layer]);
        }

        // ---- meadow ground carpet (FUN_00480720, "GroundRenderer2", texture Panorama_layer_6) ----
        // 20-unit chunks around the focus x; each chunk = 4×4 verts (3×3 quads). Rows sit at render
        // z {-60, -37.5, -15, +7.5} (row z = (row*7.5 - (3-row)*60)/3,:158) with y = groundRow-2.5
        // plus the far-edge lifts {+0.75, +0.25} (:163-170, DAT_007370c0/DAT_00737058). UVs step
        // 0.33 per cell (:240/:255-261, _DAT_00737068).
        public const float CarpetChunk = 20f;         // DAT_00737578
        public const float CarpetHalfSpan = 140f;     // DAT_00737624
        public const float CarpetYLift = 2.5f;        // DAT_007373d8
        public const float CarpetUvStep = 0.33f;      // _DAT_00737068
        public static readonly float[] CarpetRowZ = { -60f, -37.5f, -15f, 7.5f };
        public static readonly float[] CarpetRowYOff = { 0.75f, 0.25f, 0f, 0f };

        public static float CarpetSnap(float focusX)
            => Mathf.Round(focusX / CarpetChunk) * CarpetChunk;   // FUN_00480720:51-54 (round helper FUN_004295a0)

        // Carpet vertex in the RENDER frame for chunk origin chunkX, cell column c (0..3), row r (0..3).
        public static Vector3 CarpetVertex(float chunkX, int c, int r, float groundRow)
        {
            float x = chunkX + (c * CarpetChunk) / 3f;                       //:162
            float y = groundRow - CarpetYLift + CarpetRowYOff[r];            //:55 +:163-170
            float z = CarpetRowZ[r];                                         //:158
            return new Vector3(x, y, z);
        }

        // ---- horizon-zone pick (FUN_004750b0) ----
        // The nearest `horizon_set` region whose XY distance to the camera focus is < its size wins;
        // none in range → no active zone (index -1). Distance is the plain 2D euclidean (:118-127).
        public static int PickZone(IReadOnlyList<HorizonRegionState> zones, float focusX, float focusY)
        {
            int best = -1;
            float bestDist = float.MaxValue;                                  // DAT_00737700 = FLT_MAX
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                float dx = z.Position.x - focusX;
                float dy = z.Position.y - focusY;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist < z.Size && dist < bestDist) { best = i; bestDist = dist; }
            }
            return best;
        }

        // ---- rear fade (FUN_00484cd0:82-104, model twin FUN_004856e0:162-199) ----
        // Only geometry with render z < 0 fades — seven metres behind the deepest dug floor
        // plane (z = 7), i.e. root-local y < 0; full zone colour at z <= -depth. Terrain can
        // never qualify: the BDB global vertex palette spans z ∈ [7.0, 14.5] exactly (measured
        // over all 118688 BlockDB2 blocks). The law's live native targets are authored backdrop
        // models (hinterlegung/flusslandschaft-class deco placed behind the slab) via the model
        // twin. depth == 0 (no active zone) disables the law entirely (:83).
        public static float HorizonFade(float renderZ, float depth)
        {
            if (depth <= 0f || renderZ >= 0f) return 0f;
            return Mathf.Clamp01(-renderZ / depth);
        }

        // ---- ground row derivation (port-side; native source of panorama+0x1560 not located) ----
        // Scan: the deepest row that still contains load-time open air (block 59 above the
        // bowl); the surface line = that row + SurfaceLineFromDeepestAir. The offset pins the
        // carpet plane (groundRow − 2.5) half a row below the deepest meadow-floor face (the
        // face sits at deepestAir+1): with the raw air row the carpet floated ~4.5 rows above
        // the floor, above the eye row, and smeared across the sky. Native semantics of
        // +0x1560 remain unrecovered (writer not in the decompile); BackdropPresenter.GroundRowBias
        // stays the fine-tuning lever.
        // Worlds without any sky bowl (pure cave maps) return int.MinValue → the surface gate
        // never opens (matches the native: no panorama underground).
        public const uint AirBlock = 59;
        public const int SurfaceLineFromDeepestAir = 4;

        public static int ComputeGroundRow(uint[] blockIdx, int width, int height)
        {
            int ground = int.MinValue;
            for (int ty = height - 1; ty >= 0; ty--)
            {
                int row = ty * width;
                for (int tx = 0; tx < width; tx++)
                {
                    if (blockIdx[row + tx] == AirBlock) { ground = ty; break; }
                }
                if (ground != int.MinValue) break;
            }
            return ground == int.MinValue ? ground : ground + SurfaceLineFromDeepestAir;
        }
    }
}
