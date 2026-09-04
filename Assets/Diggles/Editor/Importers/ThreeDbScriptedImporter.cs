#nullable enable

namespace Diggles.Editor.Importers
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using UnityEditor;
    using UnityEditor.AssetImporters;
    using UnityEngine;

    [ScriptedImporter(version: 2, ext: "3db")]
    public sealed class ThreeDbScriptedImporter : ScriptedImporter
    {
        [Tooltip("Index of the first source mesh to import. Each mesh is one frame " +
                 "of an animation in Diggles; use this to preview specific frames.")]
        [SerializeField] private int startMeshIndex;

        [Tooltip("How many source meshes to import (starting from Start Mesh Index). " +
                 "Diggles characters store every animation frame as a separate mesh " +
                 "(10000+); start with 1 (single pose).")]
        [SerializeField] private int maxMeshesToImport = 1;

        [Tooltip("Absolute path to Diggles original/data folder. Textures are loaded " +
                 "from <root>/Texture/<material-name>.tga at import time. Leave blank " +
                 "to use the data root from diggles_config.json.")]
        [SerializeField] private string originalDataRoot = "";

        [Tooltip("Flip triangle winding (swap second and third vertex). Diggles winding " +
                 "is already correct for Unity left-handed Y-up, so default is OFF.")]
        [SerializeField] private bool flipWinding;

        [Tooltip("Flip the V axis of UVs (DirectX top-left vs OpenGL bottom-left).")]
        [SerializeField] private bool flipUvV = true;

        [Tooltip("Negate normal vectors. Only effective if Use Provided Normals is ON. " +
                 "sphere.dat values already point outward correctly, so default is OFF.")]
        [SerializeField] private bool flipNormals;

        [Tooltip("Use sphere.dat-decoded normals from the .3db file. When OFF, Unity " +
                 "computes normals from triangle geometry (RecalculateNormals).")]
        [SerializeField] private bool useProvidedNormals = true;

        [Tooltip("Apply the same axis transform to normals as to vertices. sphere.dat " +
                 "normals are in the engine's render coordinate system (loaded ONCE " +
                 "globally, not per-model), NOT in model-space — so default is OFF.")]
        [SerializeField] private bool transformNormalsWithVertices;

        [Tooltip("Extra rotation applied to the root GameObject after import (degrees " +
                 "around Y). If the model faces the wrong way as if rotated 180° around " +
                 "vertical axis, set this to 180.")]
        [SerializeField] private float extraYRotationDegrees;

        [Tooltip("Coordinate system conversion. Diggles is DX-style left-handed Y-up. " +
                 "If the model is upside-down and faces the wrong way, use RotateX180 " +
                 "(negate Y and Z) — the typical default for these models.")]
        [SerializeField] private AxisConvention axisConvention = AxisConvention.RotateX180;

        [Tooltip("Set material _Cull=Off. Only effective with shaders that respect _Cull " +
                 "(Standard does NOT — its passes are hard-coded Cull Back). Keep OFF " +
                 "unless using a custom double-sided shader.")]
        [SerializeField] private bool doubleSided;

        public enum AxisConvention
        {
            AsIs,
            NegateY,
            NegateZ,
            RotateX180,
            SwapYZ,
            SwapYZNegateZ,
        }

        [Tooltip("Vertex dequantization mode. UnitCubeCentered matches the old " +
                 "Diggles3db-Converter — known to produce recognizable shapes. " +
                 "AabbScaling uses per-mesh vec3_a/vec3_b; turns out those are NOT " +
                 "AABB and produce garbage — kept here for future investigation.")]
        [SerializeField] private DequantizeMode dequantizeMode = DequantizeMode.UnitCubeCentered;

        [Tooltip("Texture LOD preference: 'max' searches m256→m128→m064→m032→Gray. " +
                 "Match the engine's perfoptions.")]
        [SerializeField] private TextureLodPreference textureLod = TextureLodPreference.Max;

        public enum DequantizeMode { AabbScaling, UnitCubeCentered }
        public enum TextureLodPreference { Max, High, Medium, Low, Minimal }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var model = ThreeDbParser.Parse(ctx.assetPath);

            if (model.BytesRemaining != 0)
            {
                ctx.LogImportWarning(
                    $"3DB parser left {model.BytesRemaining} bytes unread at end of file");
            }

            var configuredRoot = string.IsNullOrEmpty(this.originalDataRoot)
                ? Diggles.Runtime.DigglesConfig.ConfiguredDataRoot
                : this.originalDataRoot;
            var dataRoot = string.IsNullOrEmpty(configuredRoot) ? null : configuredRoot;
            var sphereDat = TryLoadSphereDat(dataRoot, ctx.assetPath);
            var textureCache = new Dictionary<string, Texture2D>(
                System.StringComparer.OrdinalIgnoreCase);

            var materials = BuildMaterials(
                model, dataRoot, textureCache, ctx, this.textureLod, this.doubleSided);

            var rootName = string.IsNullOrEmpty(model.ModelName) ? "ThreeDB" : model.ModelName;
            var root = new GameObject(rootName);
            if (System.Math.Abs(this.extraYRotationDegrees) > 0.001f)
            {
                root.transform.rotation = Quaternion.Euler(0f, this.extraYRotationDegrees, 0f);
            }

            var start = System.Math.Clamp(
                this.startMeshIndex, 0, System.Math.Max(0, model.Meshes.Count - 1));
            var end = System.Math.Min(model.Meshes.Count, start + System.Math.Max(1, this.maxMeshesToImport));
            for (var meshIndex = start; meshIndex < end; meshIndex++)
            {
                var meshGo = BuildMeshGameObject(
                    model, meshIndex, materials,
                    this.useProvidedNormals ? sphereDat : null,
                    ctx,
                    this.flipWinding, this.flipUvV, this.flipNormals,
                    this.dequantizeMode, this.axisConvention, this.doubleSided,
                    this.transformNormalsWithVertices);
                if (meshGo == null) continue;
                meshGo.transform.SetParent(root.transform, worldPositionStays: false);
            }
            var importedCount = end - start;

            ctx.AddObjectToAsset("RootPrefab", root);
            ctx.SetMainObject(root);

            ctx.LogImportWarning(
                $"3DB '{model.ModelName}': imported {importedCount}/{model.Meshes.Count} meshes, " +
                $"{model.Materials.Count} materials, " +
                $"{textureCache.Count} textures, " +
                $"sphere.dat={(sphereDat != null ? "OK" : "MISSING")}, " +
                $"data root='{dataRoot ?? "<none>"}'");
        }

        private static GameObject? BuildMeshGameObject(
            ThreeDbModel model,
            int meshIndex,
            Material[] materials,
            Vector3[]? sphereDat,
            AssetImportContext ctx,
            bool flipWinding,
            bool flipUvV,
            bool flipNormals,
            DequantizeMode dequantizeMode,
            AxisConvention axisConvention,
            bool doubleSided,
            bool transformNormalsWithVertices)
        {
            var srcMesh = model.Meshes[meshIndex];
            if (srcMesh.SubMeshes.Count == 0) return null;

            var combinedVertices = new List<Vector3>();
            var combinedUvs = new List<Vector2>();
            var combinedNormals = new List<Vector3>();
            var perSubmeshTriangles = new List<int[]>();
            var perSubmeshMaterial = new List<Material>();

            foreach (var sub in srcMesh.SubMeshes)
            {
                if (sub.VListIndex >= model.VLists.Count) continue;
                if (sub.IdxListIndex >= model.IndexLists.Count) continue;

                var vList = model.VLists[sub.VListIndex];
                var idxList = model.IndexLists[sub.IdxListIndex];
                var uvList = sub.UvListIndex < model.UvLists.Count
                    ? model.UvLists[sub.UvListIndex]
                    : System.Array.Empty<Vector2>();
                var nList = sub.NListIndex < model.NLists.Count
                    ? model.NLists[sub.NListIndex]
                    : System.Array.Empty<byte>();

                var vertexBase = combinedVertices.Count;
                foreach (var q in vList)
                {
                    var v = dequantizeMode == DequantizeMode.AabbScaling
                        ? srcMesh.DequantizeVertex((short)q.x, (short)q.y, (short)q.z)
                        : DequantizeUnitCube((short)q.x, (short)q.y, (short)q.z);
                    combinedVertices.Add(ApplyAxisConvention(v, axisConvention));
                }

                AppendUvs(combinedUvs, uvList, vList.Length, flipUvV);
                AppendNormals(
                    combinedNormals, nList, sphereDat, vList.Length, flipNormals,
                    transformNormalsWithVertices ? axisConvention : AxisConvention.AsIs);

                var triangles = BuildTriangles(
                    idxList, vertexBase, vList.Length, flipWinding);
                perSubmeshTriangles.Add(triangles);

                var material = sub.MaterialIndex >= 0
                    && sub.MaterialIndex < materials.Length
                    ? materials[sub.MaterialIndex]
                    : materials.Length > 0 ? materials[0] : DefaultMaterial();
                perSubmeshMaterial.Add(material);
            }

            if (combinedVertices.Count == 0) return null;

            var mesh = new Mesh { name = $"mesh_{meshIndex}" };
            mesh.indexFormat = combinedVertices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(combinedVertices);
            if (combinedUvs.Count == combinedVertices.Count)
            {
                mesh.SetUVs(0, combinedUvs);
            }
            if (combinedNormals.Count == combinedVertices.Count)
            {
                mesh.SetNormals(combinedNormals);
            }
            else
            {
                mesh.RecalculateNormals();
            }

            mesh.subMeshCount = perSubmeshTriangles.Count;
            for (var i = 0; i < perSubmeshTriangles.Count; i++)
            {
                mesh.SetTriangles(perSubmeshTriangles[i], i);
            }
            mesh.RecalculateBounds();

            ctx.AddObjectToAsset($"Mesh{meshIndex}", mesh);

            var go = new GameObject($"Mesh_{meshIndex}");
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = perSubmeshMaterial.ToArray();
            return go;
        }

        private static Vector3 DequantizeUnitCube(short xQ, short yQ, short zQ)
        {
            const float Range = 65535f;
            const float Scale = 100f;
            var ux = (ushort)xQ;
            var uy = (ushort)yQ;
            var uz = (ushort)zQ;
            return new Vector3(
                (ux / Range - 0.5f) * Scale,
                (uy / Range - 0.5f) * Scale,
                (uz / Range - 0.5f) * Scale);
        }

        private static Vector3 ApplyAxisConvention(Vector3 v, AxisConvention conv) => conv switch
        {
            AxisConvention.AsIs => v,
            AxisConvention.NegateY => new Vector3(v.x, -v.y, v.z),
            AxisConvention.NegateZ => new Vector3(v.x, v.y, -v.z),
            AxisConvention.RotateX180 => new Vector3(v.x, -v.y, -v.z),
            AxisConvention.SwapYZ => new Vector3(v.x, v.z, v.y),
            AxisConvention.SwapYZNegateZ => new Vector3(v.x, v.z, -v.y),
            _ => v,
        };

        private static void AppendUvs(
            List<Vector2> dst, Vector2[] src, int expectedCount, bool flipV)
        {
            if (src.Length == expectedCount)
            {
                foreach (var uv in src)
                {
                    dst.Add(new Vector2(uv.x, flipV ? 1f - uv.y : uv.y));
                }
                return;
            }
            for (var i = 0; i < expectedCount; i++)
            {
                dst.Add(Vector2.zero);
            }
        }

        private static void AppendNormals(
            List<Vector3> dst, byte[] src, Vector3[]? sphereDat, int expectedCount,
            bool flipNormals, AxisConvention axisConvention)
        {
            if (sphereDat == null || src.Length != expectedCount)
            {
                for (var i = 0; i < expectedCount; i++)
                {
                    dst.Add(Vector3.up);
                }
                return;
            }
            var sign = flipNormals ? -1f : 1f;
            foreach (var idx in src)
            {
                var n = sphereDat[idx] * sign;
                dst.Add(ApplyAxisConvention(n, axisConvention));
            }
        }

        private static int[] BuildTriangles(
            ushort[] indices, int vertexBase, int vListLength, bool flipWinding)
        {
            var triCount = indices.Length / 3;
            var triangles = new int[triCount * 3];
            for (var i = 0; i < triCount; i++)
            {
                var i0 = indices[i * 3 + 0];
                var i1 = indices[i * 3 + 1];
                var i2 = indices[i * 3 + 2];
                if (i0 >= vListLength || i1 >= vListLength || i2 >= vListLength)
                {
                    continue;
                }
                triangles[i * 3 + 0] = vertexBase + i0;
                triangles[i * 3 + 1] = vertexBase + (flipWinding ? i2 : i1);
                triangles[i * 3 + 2] = vertexBase + (flipWinding ? i1 : i2);
            }
            return triangles;
        }

        private static Material[] BuildMaterials(
            ThreeDbModel model,
            string? dataRoot,
            Dictionary<string, Texture2D> textureCache,
            AssetImportContext ctx,
            TextureLodPreference lodPreference,
            bool doubleSided)
        {
            var shader = PickShader();
            var result = new Material[model.Materials.Count];
            var alphaSeen = new Dictionary<string, bool>(
                System.StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < model.Materials.Count; i++)
            {
                var src = model.Materials[i];
                var mat = new Material(shader) { name = src.Name };
                if (dataRoot != null)
                {
                    var loaded = LoadTextureFromDisk(
                        dataRoot, src.Name, textureCache, alphaSeen, lodPreference);
                    if (loaded != null)
                    {
                        mat.mainTexture = loaded;
                        var hasAlpha = alphaSeen.TryGetValue(src.Name, out var a) && a;
                        if (hasAlpha)
                        {
                            ConfigureStandardCutout(mat);
                        }
                        ctx.AddObjectToAsset($"Texture_{i}_{Sanitize(src.Name)}", loaded);
                    }
                    else
                    {
                        ctx.LogImportWarning(
                            $"Texture '{src.Name}' not found under '{dataRoot}/Texture/'");
                    }
                }
                if (doubleSided)
                {
                    mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                }
                ctx.AddObjectToAsset($"Material_{i}_{Sanitize(src.Name)}", mat);
                result[i] = mat;
            }
            return result;
        }

        private static void ConfigureStandardCutout(Material mat)
        {
            mat.SetFloat("_Mode", 1f);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private static Material DefaultMaterial() => new Material(PickShader()) { name = "Default" };

        private static Shader PickShader() =>
            Shader.Find("Standard")
            ?? Shader.Find("Universal Render Pipeline/Lit")
            ?? Shader.Find("Hidden/InternalErrorShader")
            ?? throw new System.InvalidOperationException("No usable shader found");

        private static readonly string[] LodFoldersMax = { "m256", "m128", "m064", "m032", "Gray", "Misc", "ClassIcons" };
        private static readonly string[] LodFoldersHigh = { "m128", "m256", "m064", "m032", "Gray", "Misc", "ClassIcons" };
        private static readonly string[] LodFoldersMedium = { "m064", "m128", "m032", "m256", "Gray", "Misc", "ClassIcons" };
        private static readonly string[] LodFoldersLow = { "m032", "m064", "m016", "m128", "m256", "Gray", "Misc", "ClassIcons" };
        private static readonly string[] LodFoldersMinimal = { "m016", "m032", "m064", "m128", "m256", "Gray", "Misc", "ClassIcons" };

        private static Texture2D? LoadTextureFromDisk(
            string dataRoot,
            string materialName,
            Dictionary<string, Texture2D> cache,
            Dictionary<string, bool> alphaCache,
            TextureLodPreference lodPreference)
        {
            if (cache.TryGetValue(materialName, out var cached)) return cached;

            var textureDir = Path.Combine(dataRoot, "Texture");
            if (!Directory.Exists(textureDir)) return null;

            var folders = lodPreference switch
            {
                TextureLodPreference.Max => LodFoldersMax,
                TextureLodPreference.High => LodFoldersHigh,
                TextureLodPreference.Medium => LodFoldersMedium,
                TextureLodPreference.Low => LodFoldersLow,
                TextureLodPreference.Minimal => LodFoldersMinimal,
                _ => LodFoldersMax,
            };

            foreach (var folder in folders)
            {
                var subDir = Path.Combine(textureDir, folder);
                if (!Directory.Exists(subDir)) continue;
                var candidate = Path.Combine(subDir, materialName + ".tga");
                if (!File.Exists(candidate))
                {
                    var matches = Directory.GetFiles(subDir, materialName + ".*")
                        .FirstOrDefault();
                    if (matches == null) continue;
                    candidate = matches;
                }
                try
                {
                    var result = TgaLoader.LoadFromFile(candidate);
                    if (result != null)
                    {
                        cache[materialName] = result.Texture;
                        alphaCache[materialName] = result.HasAlpha;
                        return result.Texture;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"TGA load failed for '{candidate}': {e.Message}");
                }
            }
            return null;
        }

        private static Vector3[]? TryLoadSphereDat(string? dataRoot, string assetPath)
        {
            if (dataRoot != null)
            {
                var p = Path.Combine(dataRoot, "sphere.dat");
                if (File.Exists(p)) return ReadSphereDat(p);
            }

            var assetDir = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(assetDir)) return null;
            var current = assetDir;
            for (var i = 0; i < 4; i++)
            {
                var candidate = Path.Combine(current!, "sphere.dat");
                if (File.Exists(candidate)) return ReadSphereDat(candidate);
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) break;
            }
            return null;
        }

        private static Vector3[] ReadSphereDat(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != 3072)
            {
                throw new InvalidDataException(
                    $"sphere.dat: expected 3072 bytes, got {bytes.Length}");
            }
            var result = new Vector3[256];
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            for (var i = 0; i < 256; i++)
            {
                var x = reader.ReadSingle();
                var y = reader.ReadSingle();
                var z = reader.ReadSingle();
                result[i] = new Vector3(x, y, z);
            }
            return result;
        }

        private static string Sanitize(string name) =>
            string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
    }
}
