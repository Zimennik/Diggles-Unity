#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using UnityEngine;

    public sealed record ThreeDbMaterial(string Name, string SourcePath, uint Flag);

    public sealed record ThreeDbSubMesh(
        int MaterialIndex,
        ushort Flags,
        ushort IdxListIndex,
        ushort UvListIndex,
        ushort VListIndex,
        ushort NListIndex);

    public sealed class ThreeDbMesh
    {
        public IReadOnlyList<ThreeDbSubMesh> SubMeshes { get; init; } = System.Array.Empty<ThreeDbSubMesh>();
        public Vector3 BBoxMin { get; init; }
        public Vector3 BBoxMax { get; init; }
        public byte[] Block128 { get; init; } = System.Array.Empty<byte>();
        public byte[] Block48 { get; init; } = System.Array.Empty<byte>();
        public ushort Field0 { get; init; }
        public ushort Field2 { get; init; }

        public Vector3 DequantizeVertex(short xQ, short yQ, short zQ)
        {
            const float UInt16Range = 65536f;
            const float Bias = 32768f;
            var size = this.BBoxMax - this.BBoxMin;
            return new Vector3(
                (xQ + Bias) / UInt16Range * size.x + this.BBoxMin.x,
                (yQ + Bias) / UInt16Range * size.y + this.BBoxMin.y,
                (zQ + Bias) / UInt16Range * size.z + this.BBoxMin.z);
        }
    }

    public sealed record ThreeDbObject(string Name, uint[] Indices);

    public sealed record ThreeDbAnim(
        string Name1,
        string Name2,
        uint[] Indices,
        ushort Field4,
        Vector3 VecA,
        Vector3 VecB);

    public sealed record ThreeDbShadow(byte[] Data);

    public sealed record ThreeDbCMap(
        ushort Width,
        ushort Height,
        ushort Field4,
        ushort Field5,
        byte[] Data);

    public sealed record ThreeDbTriangle(byte[] Raw);

    // Packed quantized vertex — the 3× int16 exactly as stored in the.3db (6 bytes), replacing the
    // old Vector3Int[] (3× int32 = 12 bytes). This halves the single largest retained chunk of a
    // parsed character model (frau.3db: ~44 MB of vertex data → ~22 MB), the dominant sim-step
    // allocation when a dwarf's init loads the frau/mann db. x/y/z return the
    // same sign-extended int values Vector3Int exposed, so every consumer's (ushort)/(short) cast is
    // byte-for-byte identical.
    public readonly struct ThreeDbQVert
    {
        public readonly short X, Y, Z;
        public ThreeDbQVert(short x, short y, short z) { X = x; Y = y; Z = z; }
        public int x => X;
        public int y => Y;
        public int z => Z;
    }

    public sealed class ThreeDbModel
    {
        public string Magic { get; init; } = "";
        public string ModelName { get; init; } = "";
        public IReadOnlyList<ThreeDbMaterial> Materials { get; init; } = System.Array.Empty<ThreeDbMaterial>();
        public IReadOnlyList<ThreeDbMesh> Meshes { get; init; } = System.Array.Empty<ThreeDbMesh>();
        public IReadOnlyList<ThreeDbObject> Objects { get; init; } = System.Array.Empty<ThreeDbObject>();
        public IReadOnlyList<ThreeDbAnim> Anims { get; init; } = System.Array.Empty<ThreeDbAnim>();
        public IReadOnlyList<ThreeDbShadow> Shadows { get; init; } = System.Array.Empty<ThreeDbShadow>();
        public IReadOnlyList<ThreeDbCMap> CMaps { get; init; } = System.Array.Empty<ThreeDbCMap>();
        public IReadOnlyList<ThreeDbTriangle> Triangles { get; init; } = System.Array.Empty<ThreeDbTriangle>();
        public IReadOnlyList<ushort[]> IndexLists { get; init; } = System.Array.Empty<ushort[]>();
        public IReadOnlyList<Vector2[]> UvLists { get; init; } = System.Array.Empty<Vector2[]>();
        public IReadOnlyList<ThreeDbQVert[]> VLists { get; init; } = System.Array.Empty<ThreeDbQVert[]>();
        public IReadOnlyList<byte[]> NLists { get; init; } = System.Array.Empty<byte[]>();
        public long BytesConsumed { get; init; }
        public long FileSize { get; init; }

        public long BytesRemaining => this.FileSize - this.BytesConsumed;
    }
}
