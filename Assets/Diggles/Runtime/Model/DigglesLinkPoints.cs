#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Computes a model's link / attachment point offset for a given animation frame.
    // Link points (hats=head 4, hands 0/1/7, mouth 10, …) are baked markers in each
    // morph keyframe; attached objects ride them. See Docs/HAT_LINK_SYSTEM.md.
    //
    // Chain:
    // meshIndex = anim.Indices[frame] (frame → morph mesh keyframe)
    // slot = u32(Meshes[meshIndex].Block128, linkId*4) (32-slot link table)
    // rec = Triangles[slot] (20-byte record; first 3 u16 = pos)
    // offset = (u16/4 - 8192) per axis (dequant)
    // The returned offset is in the SAME space as our decoded mesh vertices (each mesh's
    // bbox == the dequant range of its u16 window), so feeding it through the inner
    // "Model" transform (Q_x(-90) + ModelToWorldScale) lands it like a vertex.
    public static class DigglesLinkPoints
    {
        public const int Head = 4, RightHand = 0, LeftHand = 1, CarryHand = 7, Mouth = 10;

        // Decodes the link point with the same per-mesh affine (world = A + u16*S) the
        // mesh used for its vertices — so the link lands exactly in the mesh's vertex
        // space (not the engine's absolute u/4-8192, which is a different linear map and
        // left the hat slightly off). A/S come from DigglesModelLibrary.TryGetMeshAffine.
        public static bool TryGetLocalOffset(ThreeDbModel? db, int meshIndex, int linkId,
            Vector3 affineA, Vector3 affineS, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (db == null || linkId < 0 || linkId > 31) return false;
            if (meshIndex < 0 || meshIndex >= db.Meshes.Count) return false;

            var block = db.Meshes[meshIndex].Block128;          // 32 × u32 link table
            int b = linkId * 4;
            if (block == null || block.Length < b + 4) return false;
            uint slot = (uint)(block[b] | (block[b + 1] << 8) | (block[b + 2] << 16) | (block[b + 3] << 24));
            if (slot >= (uint)db.Triangles.Count) return false;

            var raw = db.Triangles[(int)slot].Raw;              // 20-byte record
            if (raw == null || raw.Length < 6) return false;
            int qx = raw[0] | (raw[1] << 8);
            int qy = raw[2] | (raw[3] << 8);
            int qz = raw[4] | (raw[5] << 8);

            offset = new Vector3(affineA.x + qx * affineS.x, affineA.y + qy * affineS.y, affineA.z + qz * affineS.z);
            return true;
        }

        // The engine's own dequant of the same record — offset = u16/4 − 8192 per axis
        // (FUN_00408a10: (q|0x43800000)·k − c ≡ u/4 − 8192), in raw model axes. This is the
        // map the native uses for walk -dummy goals (FUN_00420840:1929-1938 scales it by
        // x,y×0.005, z×0.01 — no rotation); the per-mesh vertex affine above is a different
        // linear map (kept for the hat seat, which is vertex-space by design). Feeding the
        // affine decode into the walk goal displaced harvest/chop stands on the depth axis
        // (the ×2 height unit amplifies the decode delta).
        public static bool TryGetEngineOffset(ThreeDbModel? db, int meshIndex, int linkId, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (db == null || linkId < 0 || linkId > 31) return false;
            if (meshIndex < 0 || meshIndex >= db.Meshes.Count) return false;
            var block = db.Meshes[meshIndex].Block128;
            int b = linkId * 4;
            if (block == null || block.Length < b + 4) return false;
            uint slot = (uint)(block[b] | (block[b + 1] << 8) | (block[b + 2] << 16) | (block[b + 3] << 24));
            if (slot >= (uint)db.Triangles.Count) return false;
            var raw = db.Triangles[(int)slot].Raw;
            if (raw == null || raw.Length < 6) return false;
            int qx = raw[0] | (raw[1] << 8);
            int qy = raw[2] | (raw[3] << 8);
            int qz = raw[4] | (raw[5] << 8);
            offset = new Vector3(qx / 4f - 8192f, qy / 4f - 8192f, qz / 4f - 8192f);
            return true;
        }

        // Per-frame link orientation (get_linkrot): the same 20-byte record stores, at
        // bytes +0x08/+0x0c/+0x10, an intrinsic Euler (X, Y, Z) in radians (Y = yaw),
        // in native model space — plain float32, no dequant. Lets a hat tilt/turn with
        // the head as it animates.
        public static bool TryGetLocalEuler(ThreeDbModel? db, int meshIndex, int linkId, out Vector3 eulerRad)
        {
            eulerRad = Vector3.zero;
            if (db == null || linkId < 0 || linkId > 31) return false;
            if (meshIndex < 0 || meshIndex >= db.Meshes.Count) return false;
            var block = db.Meshes[meshIndex].Block128;
            int b = linkId * 4;
            if (block == null || block.Length < b + 4) return false;
            uint slot = (uint)(block[b] | (block[b + 1] << 8) | (block[b + 2] << 16) | (block[b + 3] << 24));
            if (slot >= (uint)db.Triangles.Count) return false;
            var raw = db.Triangles[(int)slot].Raw;
            if (raw == null || raw.Length < 20) return false;
            eulerRad = new Vector3(
                System.BitConverter.ToSingle(raw, 0x08),
                System.BitConverter.ToSingle(raw, 0x0c),
                System.BitConverter.ToSingle(raw, 0x10));
            return true;
        }
    }
}
