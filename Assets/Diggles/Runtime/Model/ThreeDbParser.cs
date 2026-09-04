#nullable enable

namespace Diggles.Runtime
{
    using System.IO;
    using System.Text;
    using UnityEngine;

    public static class ThreeDbParser
    {
        private const string ExpectedMagic = "3DDB 1.0";

        public static ThreeDbModel Parse(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Parse(stream, stream.Length);
        }

        public static ThreeDbModel Parse(Stream stream, long fileSize)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var magic = ReadKString(reader);
            if (magic != ExpectedMagic)
            {
                throw new InvalidDataException(
                    $"Unexpected 3DB magic: '{magic}' (expected '{ExpectedMagic}')");
            }

            var modelName = ReadKString(reader);

            var materialCount = reader.ReadUInt16();
            var materials = new ThreeDbMaterial[materialCount];
            for (var i = 0; i < materialCount; i++)
            {
                materials[i] = ReadMaterial(reader);
            }

            var meshCount = reader.ReadUInt32();
            var meshes = new ThreeDbMesh[meshCount];
            for (var i = 0; i < meshCount; i++)
            {
                meshes[i] = ReadMesh(reader);
            }

            var objectCount = reader.ReadUInt16();
            var objects = new ThreeDbObject[objectCount];
            for (var i = 0; i < objectCount; i++)
            {
                objects[i] = ReadObject(reader);
            }

            var animCount = reader.ReadUInt16();
            var anims = new ThreeDbAnim[animCount];
            for (var i = 0; i < animCount; i++)
            {
                anims[i] = ReadAnim(reader);
            }

            var shadowCount = reader.ReadUInt16();
            var shadows = new ThreeDbShadow[shadowCount];
            for (var i = 0; i < shadowCount; i++)
            {
                shadows[i] = new ThreeDbShadow(reader.ReadBytes(0x400));
            }

            var cmapCount = reader.ReadUInt16();
            var cmaps = new ThreeDbCMap[cmapCount];
            for (var i = 0; i < cmapCount; i++)
            {
                cmaps[i] = ReadCMap(reader);
            }

            var idxListCount = reader.ReadUInt16();
            var uvListCount = reader.ReadUInt16();
            var vListCount = reader.ReadUInt16();
            var nListCount = reader.ReadUInt16();
            var triangleCount = reader.ReadUInt32();

            var idxListSizes = ReadUInt16Array(reader, idxListCount);
            var uvListSizes = ReadUInt16Array(reader, uvListCount);
            var vListSizes = ReadUInt16Array(reader, vListCount);
            var nListSizes = ReadUInt16Array(reader, nListCount);

            var triangles = new ThreeDbTriangle[triangleCount];
            for (var i = 0; i < triangleCount; i++)
            {
                triangles[i] = new ThreeDbTriangle(reader.ReadBytes(20));
            }

            var indexLists = new ushort[idxListCount][];
            for (var i = 0; i < idxListCount; i++)
            {
                indexLists[i] = ReadUInt16Array(reader, idxListSizes[i]);
            }

            var uvLists = new Vector2[uvListCount][];
            for (var i = 0; i < uvListCount; i++)
            {
                uvLists[i] = ReadUvList(reader, uvListSizes[i]);
            }

            var vLists = new ThreeDbQVert[vListCount][];
            for (var i = 0; i < vListCount; i++)
            {
                vLists[i] = ReadQuantizedVertexList(reader, vListSizes[i]);
            }

            var nLists = new byte[nListCount][];
            for (var i = 0; i < nListCount; i++)
            {
                nLists[i] = reader.ReadBytes(nListSizes[i]);
            }

            return new ThreeDbModel
            {
                Magic = magic,
                ModelName = modelName,
                Materials = materials,
                Meshes = meshes,
                Objects = objects,
                Anims = anims,
                Shadows = shadows,
                CMaps = cmaps,
                Triangles = triangles,
                IndexLists = indexLists,
                UvLists = uvLists,
                VLists = vLists,
                NLists = nLists,
                BytesConsumed = stream.Position,
                FileSize = fileSize,
            };
        }

        private static ThreeDbMaterial ReadMaterial(BinaryReader reader)
        {
            var name = ReadKString(reader);
            var src = ReadKString(reader);
            var flag = reader.ReadUInt32();
            return new ThreeDbMaterial(name, src, flag);
        }

        private static ThreeDbMesh ReadMesh(BinaryReader reader)
        {
            var subCount = reader.ReadUInt16();
            var subMeshes = new ThreeDbSubMesh[subCount];
            for (var i = 0; i < subCount; i++)
            {
                subMeshes[i] = ReadSubMesh(reader);
            }

            var aX = reader.ReadSingle();
            var aY = reader.ReadSingle();
            var aZ = reader.ReadSingle();
            var bX = reader.ReadSingle();
            var bY = reader.ReadSingle();
            var bZ = reader.ReadSingle();
            var block128 = reader.ReadBytes(128);
            var field2 = reader.ReadUInt16();
            var block48 = reader.ReadBytes(48);
            var field0 = reader.ReadUInt16();

            return new ThreeDbMesh
            {
                SubMeshes = subMeshes,
                BBoxMin = new Vector3(aX, aY, aZ),
                BBoxMax = new Vector3(bX, bY, bZ),
                Block128 = block128,
                Block48 = block48,
                Field0 = field0,
                Field2 = field2,
            };
        }

        private static ThreeDbSubMesh ReadSubMesh(BinaryReader reader)
        {
            var mat = reader.ReadInt16();
            var flags = reader.ReadInt16();
            var idxList = reader.ReadUInt16();
            var uvList = reader.ReadUInt16();
            var vList = reader.ReadUInt16();
            var nList = reader.ReadUInt16();
            return new ThreeDbSubMesh(mat, (ushort)flags, idxList, uvList, vList, nList);
        }

        private static ThreeDbObject ReadObject(BinaryReader reader)
        {
            var name = ReadKString(reader);
            var count = reader.ReadUInt16();
            var indices = new uint[count];
            for (var i = 0; i < count; i++)
            {
                indices[i] = reader.ReadUInt32();
            }
            return new ThreeDbObject(name, indices);
        }

        private static ThreeDbAnim ReadAnim(BinaryReader reader)
        {
            var name1 = ReadKString(reader);
            var count = reader.ReadUInt16();
            var indices = new uint[count];
            for (var i = 0; i < count; i++)
            {
                indices[i] = reader.ReadUInt32();
            }
            var field4 = reader.ReadUInt16();
            _ = reader.ReadBytes(4);
            var name2 = ReadKString(reader);
            var aX = reader.ReadSingle();
            var aY = reader.ReadSingle();
            var aZ = reader.ReadSingle();
            var bX = reader.ReadSingle();
            var bY = reader.ReadSingle();
            var bZ = reader.ReadSingle();
            return new ThreeDbAnim(
                name1, name2, indices, field4,
                new Vector3(aX, aY, aZ),
                new Vector3(bX, bY, bZ));
        }

        private static ThreeDbCMap ReadCMap(BinaryReader reader)
        {
            var w = reader.ReadUInt16();
            var h = reader.ReadUInt16();
            var f4 = reader.ReadUInt16();
            var f5 = reader.ReadUInt16();
            var data = reader.ReadBytes(w * h);
            return new ThreeDbCMap(w, h, f4, f5, data);
        }

        private static string ReadKString(BinaryReader reader)
        {
            var length = reader.ReadUInt32();
            if (length > 4096u)
            {
                throw new InvalidDataException(
                    $"KStr length {length} exceeds sanity limit at offset 0x{reader.BaseStream.Position - 4:X}");
            }
            var bytes = reader.ReadBytes((int)length);
            return DecodeLatin1(bytes);
        }

        private static string DecodeLatin1(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i] = (char)bytes[i];
            }
            return new string(chars);
        }

        private static ushort[] ReadUInt16Array(BinaryReader reader, int count)
        {
            var array = new ushort[count];
            for (var i = 0; i < count; i++)
            {
                array[i] = reader.ReadUInt16();
            }
            return array;
        }

        private static Vector2[] ReadUvList(BinaryReader reader, int count)
        {
            var array = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var u = reader.ReadSingle();
                var v = reader.ReadSingle();
                array[i] = new Vector2(u, v);
            }
            return array;
        }

        private static ThreeDbQVert[] ReadQuantizedVertexList(BinaryReader reader, int count)
        {
            var array = new ThreeDbQVert[count];
            for (var i = 0; i < count; i++)
            {
                short x = reader.ReadInt16();
                short y = reader.ReadInt16();
                short z = reader.ReadInt16();
                array[i] = new ThreeDbQVert(x, y, z);
            }
            return array;
        }
    }
}
