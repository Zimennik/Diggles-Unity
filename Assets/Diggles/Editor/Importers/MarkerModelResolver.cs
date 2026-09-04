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

    /// <summary>
    /// Resolves a Tcl marker class name into a real.3db mesh + materials.
    /// Mirrors ThreeDbScriptedImporter.BuildMeshGameObject (which is known to
    /// produce visually-correct models) but uses URP/Lit shader for materials.
    /// </summary>
    public static class MarkerModelResolver
    {
        public sealed class ResolvedModel
        {
            public Mesh? Mesh;
            public Material[]? Materials;
            public string DebugLabel = "";
        }

        public sealed class ResolvedDynamicModel
        {
            public DigglesAnimClip[] Clips = System.Array.Empty<DigglesAnimClip>();
            public Material[]? Materials;
            public string DefaultClipName = "";
            public DigglesAnimator.AnimMode DefaultMode = DigglesAnimator.AnimMode.Loop;
            public int DefaultStartFrame;
            public string DebugLabel = "";
        }

        public static ResolvedModel? Resolve(
            string className, string dataRoot, AssetImportContext? ctx)
        {
            EnsureMapLoaded();
            if (s_classMap == null) return null;
            if (!s_classMap.TryGetValue(className, out var entry)) return null;
            if (!entry.visible) return null;
            if (string.IsNullOrEmpty(entry.obj) || string.IsNullOrEmpty(entry.anim)) return null;

            // Find which.3db contains this object (case-sensitive name lookup).
            if (s_objDb == null || !s_objDb.TryGetValue(entry.obj!, out var dbName))
            {
                Debug.LogWarning($"MarkerModelResolver: obj '{entry.obj}' not found in any 3db (class={className})");
                return null;
            }

            var cacheKey = $"{dbName}|{entry.obj}|{entry.anim}";
            if (s_modelCache.TryGetValue(cacheKey, out var cached)) return cached;

            var dbModel = LoadDb(dbName, dataRoot);
            if (dbModel == null) return null;

            // Find Object by case-sensitive name (engine uses no-tolower comparison)
            int objIdx = -1;
            for (int i = 0; i < dbModel.Objects.Count; ++i)
            {
                if (dbModel.Objects[i].Name == entry.obj)
                {
                    objIdx = i;
                    break;
                }
            }
            if (objIdx < 0)
            {
                Debug.LogWarning($"MarkerModelResolver: obj '{entry.obj}' not in {dbName}.3db (class={className})");
                return null;
            }
            var obj = dbModel.Objects[objIdx];

            // Find the Anim where Name1 == entry.anim inside this Object's indices
            // list. Object.Indices is a list of global anim indices; the engine
            // scans them and matches Anim.Name1 (case-sensitive).
            int meshIdx = -1;
            foreach (var animIdx in obj.Indices)
            {
                if (animIdx >= dbModel.Anims.Count) continue;
                var anim = dbModel.Anims[(int)animIdx];
                if (anim.Name1 != entry.anim) continue;
                if (anim.Indices.Length == 0) continue;
                // Frame 0 of the matched anim = static-display mesh.
                meshIdx = (int)anim.Indices[0];
                break;
            }
            if (meshIdx < 0)
            {
                Debug.LogWarning($"MarkerModelResolver: anim '{entry.anim}' not found for obj '{entry.obj}' in {dbName}.3db (class={className})");
                return null;
            }
            if (meshIdx >= dbModel.Meshes.Count) return null;

            // Build materials once per db (cached).
            var mats = GetOrBuildMaterials(dbName, dbModel, dataRoot, ctx);

            var (mesh, perSubmeshMat) = BuildMesh(dbModel, meshIdx, mats, cacheKey);
            if (mesh == null) return null;

            if (ctx != null) ctx.AddObjectToAsset($"Mesh_{cacheKey}", mesh);

            var result = new ResolvedModel
            {
                Mesh = mesh,
                Materials = perSubmeshMat,
                DebugLabel = cacheKey,
            };
            s_modelCache[cacheKey] = result;
            return result;
        }

        public static void ClearCache()
        {
            foreach (var m in s_modelCache.Values)
                if (m.Mesh != null) Object.DestroyImmediate(m.Mesh);
            s_modelCache.Clear();
            foreach (var m in s_meshCache.Values)
                if (m != null) Object.DestroyImmediate(m);
            s_meshCache.Clear();
            s_dynClipCache.Clear();
            foreach (var arr in s_dbMaterials.Values)
                if (arr != null)
                    foreach (var mat in arr) if (mat != null) Object.DestroyImmediate(mat);
            s_dbMaterials.Clear();
            s_dbCache.Clear();
            s_textureCache.Clear();
            s_classMap = null;
            s_objDb = null;
        }

        // Variant of Resolve that returns animation clip data for a multi-frame
        // anim. Returns null if the class resolves to a single-frame anim, in which
        // case the caller falls back to the static Resolve path.
        public static ResolvedDynamicModel? ResolveDynamic(
            string className, string dataRoot, AssetImportContext? ctx)
        {
            EnsureMapLoaded();
            if (s_classMap == null) return null;
            if (!s_classMap.TryGetValue(className, out var entry)) return null;
            if (!entry.visible) return null;
            if (string.IsNullOrEmpty(entry.obj) || string.IsNullOrEmpty(entry.anim)) return null;
            if (s_objDb == null || !s_objDb.TryGetValue(entry.obj!, out var dbName)) return null;

            var dbModel = LoadDb(dbName, dataRoot);
            if (dbModel == null) return null;

            int objIdx = -1;
            for (int i = 0; i < dbModel.Objects.Count; ++i)
            {
                if (dbModel.Objects[i].Name == entry.obj) { objIdx = i; break; }
            }
            if (objIdx < 0) return null;
            var obj = dbModel.Objects[objIdx];

            // Find the matching anim within this object's anim list.
            ThreeDbAnim? matchedAnim = null;
            foreach (var animIdx in obj.Indices)
            {
                if (animIdx >= dbModel.Anims.Count) continue;
                var a = dbModel.Anims[(int)animIdx];
                if (a.Name1 == entry.anim) { matchedAnim = a; break; }
            }
            if (matchedAnim == null) return null;

            // If the class's declared default anim is single-frame (e.g.
            // mann.standard = 1-frame idle pose), find the first multi-frame
            // anim in this object to play instead. This is what gives dwarves
            // breathing/idle motion. Preference order:
            // 1. stand_anim_a (canonical idle variant)
            // 2. any *_loop (locomotion/work idle)
            // 3. any anim with >1 frame
            string chosenAnimName = matchedAnim.Name1;
            ThreeDbAnim chosenAnim = matchedAnim;
            if (matchedAnim.Indices.Length <= 1)
            {
                ThreeDbAnim? fallback = null;
                ThreeDbAnim? loopFallback = null;
                ThreeDbAnim? anyMultiFrame = null;
                foreach (var animIdx in obj.Indices)
                {
                    if (animIdx >= dbModel.Anims.Count) continue;
                    var a = dbModel.Anims[(int)animIdx];
                    if (a.Indices.Length <= 1) continue;
                    anyMultiFrame ??= a;
                    if (a.Name1.EndsWith("_loop", System.StringComparison.Ordinal))
                        loopFallback ??= a;
                    if (a.Name1 == "stand_anim_a" || a.Name1 == "stand_a")
                    {
                        fallback = a;
                        break;
                    }
                }
                var picked = fallback ?? loopFallback ?? anyMultiFrame;
                if (picked == null) return null;  // truly static object — caller falls back
                chosenAnim = picked;
                chosenAnimName = picked.Name1;
            }
            matchedAnim = chosenAnim;

            var mats = GetOrBuildMaterials(dbName, dbModel, dataRoot, ctx);

            var clipsList = new List<DigglesAnimClip>(8);
            var loadedNames = new HashSet<string>();
            DigglesAnimClip? defaultClip = LoadClip(dbName, entry.obj!, dbModel, mats, matchedAnim, ctx);
            if (defaultClip == null) return null;
            clipsList.Add(defaultClip);
            loadedNames.Add(defaultClip.Name);

            foreach (var name in CommonClipPreloadNames)
            {
                if (loadedNames.Contains(name)) continue;
                var candidate = FindAnimInObject(dbModel, obj, name);
                if (candidate == null || candidate.Indices.Length <= 1) continue;
                var built = LoadClip(dbName, entry.obj!, dbModel, mats, candidate, ctx);
                if (built == null) continue;
                clipsList.Add(built);
                loadedNames.Add(name);
            }

            // Build per-submesh materials for the default clip's first frame
            // (used to set renderer.sharedMaterials).
            var perSubmeshMat = BuildMaterialArrayForMeshIdx(
                dbModel, (int)matchedAnim.Indices[0], mats);

            return new ResolvedDynamicModel
            {
                Clips = clipsList.ToArray(),
                Materials = perSubmeshMat,
                DefaultClipName = defaultClip.Name,
                DefaultMode = DigglesAnimator.AnimMode.Loop,
                DefaultStartFrame = 0,
                DebugLabel = $"{dbName}|{entry.obj}|{defaultClip.Name}+{clipsList.Count - 1}",
            };
        }

        // Anim names eagerly loaded for every animated character/object on
        // top of its default clip — so actions (Walk, Idle variants, future
        // Mine/Chop/etc.) can SetAnim into them without an extra editor
        // round-trip. Anims that don't exist on the object are skipped.
        private static readonly string[] CommonClipPreloadNames =
        {
            "standard",
            "stand_a", "stand_b", "stand_c", "stand_d",
            "stand_anim_a", "stand_anim_b",
            "gehen_start", "gehen_loop", "gehen_end",
            "laufen_start", "laufen_loop", "laufen_end",
            "laufen_schnell_start", "laufen_schnell_loop", "laufen_schnell_end",
            "galopp_loop", "fliegen_loop",
            "drehen_links", "drehen_rechts",
            // Climb anims (z_anims.tcl: climbup/down/left/right →
            // mann.kletter_*_loop, *_schnell for fast variants).
            "kletter_hoch_loop", "kletter_runter_loop",
            "kletter_links_loop", "kletter_rechts_loop",
            "kletter_hoch_schnell", "kletter_runter_schnell",
            "kletterstand", "stand_zu_kletterstand", "kletterstand_zu_stand",
            // Ladder anims (for ladders / vertical mine shafts).
            "leiter_hoch_start", "leiter_hoch_loop",
            "leiter_runter_start", "leiter_runter_loop", "leiter_runter_end",
            "leiter_stand", "leiter_zu_boden",
        };

        // Per-object alignment rotation. Bakes Diggles model frame (Z-up
        // + author-specific forward direction from motionOffset) onto Unity
        // world frame (Y-up + forward = +Z).
        //
        // Result:
        // bakeRotation * (0, 0, 1)_diggles = (0, 1, 0)_world [up axis]
        // bakeRotation * motionOffset = (0, 0, +)_world [forward]
        //
        // After bake, a model placed at identity rotation stands upright and
        // walks toward Unity +Z. WalkAction can then yaw outer.rotation
        // around Vector3.up with the universal formula yaw = atan2(x, z).
        //
        // For objects with no motion-offset reference (static decorations),
        // we still apply a pure axis pitch (Z-up → Y-up) so they stand.
        private static Quaternion ComputeBakeRotation(ThreeDbModel db, ThreeDbObject obj)
        {
            Vector3 forward = Vector3.zero;
            foreach (var animIdx in obj.Indices)
            {
                if (animIdx >= db.Anims.Count) continue;
                var a = db.Anims[(int)animIdx];
                if (a.VecA.sqrMagnitude < 0.01f) continue;
                if (a.Name1.EndsWith("_loop", System.StringComparison.Ordinal))
                {
                    forward = a.VecA;
                    if (a.Name1.Contains("gehen") || a.Name1.Contains("laufen") ||
                        a.Name1.Contains("walk")  || a.Name1.Contains("galopp"))
                        break;
                }
            }
            if (forward.sqrMagnitude < 0.01f)
            {
                // Static fallback: axis pitch Z-up → Y-up.
                return Quaternion.Euler(-90f, 0f, 0f);
            }
            // Diggles models authored Z-up (head at +Z in vert data). Bake
            // model→world via Inverse(LookRotation(forward, +Z up)).
            return Quaternion.Inverse(
                Quaternion.LookRotation(forward.normalized, Vector3.forward));
        }

        private static ThreeDbAnim? FindAnimInObject(
            ThreeDbModel db, ThreeDbObject obj, string animName)
        {
            foreach (var animIdx in obj.Indices)
            {
                if (animIdx >= db.Anims.Count) continue;
                var a = db.Anims[(int)animIdx];
                if (a.Name1 == animName) return a;
            }
            return null;
        }

        // Build (or return cached) DigglesAnimClip with all frame meshes for
        // one anim. Each frame mesh is cached globally per (db, meshIdx) so
        // multiple anims referring to the same mesh share it.
        //
        // Cache key includes objName so different objects with the same anim
        // name (e.g. multiple objects each named 'standard') get distinct
        // clips. Mesh-level cache (s_meshCache) is keyed by (db, meshIdx)
        // which is already globally unique, so frame meshes are shared
        // correctly across objects that happen to reference the same mesh.
        private static DigglesAnimClip? LoadClip(
            string dbName, string objName,
            ThreeDbModel db, Material[] mats,
            ThreeDbAnim anim, AssetImportContext? ctx)
        {
            if (anim.Indices.Length == 0) return null;
            var clipKey = $"{dbName}|{objName}|{anim.Name1}";
            if (s_dynClipCache.TryGetValue(clipKey, out var cached)) return cached;

            var frames = new Mesh[anim.Indices.Length];
            for (int f = 0; f < anim.Indices.Length; ++f)
            {
                int meshIdx = (int)anim.Indices[f];
                if (meshIdx >= db.Meshes.Count) { frames[f] = null!; continue; }
                var meshKey = $"{dbName}|m{meshIdx}";
                if (!s_meshCache.TryGetValue(meshKey, out var mesh))
                {
                    var (built, _) = BuildMesh(db, meshIdx, mats, meshKey);
                    if (built != null)
                    {
                        s_meshCache[meshKey] = built;
                        if (ctx != null) ctx.AddObjectToAsset($"Mesh_{meshKey}", built);
                        mesh = built;
                    }
                }
                frames[f] = mesh!;
            }
            var clip = new DigglesAnimClip
            {
                Name = anim.Name1,
                SourceObjName = objName,    // anim-frame SFX key = "<obj>.<anim>" (soundinit)
                DefaultMode = DigglesAnimator.AnimMode.Loop,
                MotionOffset = anim.VecA,
                RotationDelta = anim.VecB,
                Frames = frames,
            };
            s_dynClipCache[clipKey] = clip;
            return clip;
        }

        // Builds the materials array (one per submesh) that matches a given mesh's
        // SubMeshes — needed because BuildMesh emits one Unity submesh per source
        // ThreeDbSubMesh and Unity expects renderer.sharedMaterials.Length == subMeshCount.
        private static Material[] BuildMaterialArrayForMeshIdx(
            ThreeDbModel db, int meshIdx, Material[] mats)
        {
            if (meshIdx < 0 || meshIdx >= db.Meshes.Count || mats.Length == 0)
                return System.Array.Empty<Material>();
            var mesh = db.Meshes[meshIdx];
            var arr = new Material[mesh.SubMeshes.Count];
            for (int i = 0; i < mesh.SubMeshes.Count; ++i)
            {
                int idx = mesh.SubMeshes[i].MaterialIndex;
                arr[i] = idx >= 0 && idx < mats.Length ? mats[idx] : mats[0];
            }
            return arr;
        }

        // ----- internals ----------------------------------------------------

        [System.Serializable] private struct MapEntry { public string? obj; public string? anim; public bool visible; }
        private static Dictionary<string, MapEntry>? s_classMap;
        private static Dictionary<string, string>? s_objDb;       // objName → dbName
        private static readonly Dictionary<string, ThreeDbModel> s_dbCache = new();
        private static readonly Dictionary<string, Material[]> s_dbMaterials = new();
        private static readonly Dictionary<string, ResolvedModel> s_modelCache = new();
        private static readonly Dictionary<string, Mesh> s_meshCache = new();
        private static readonly Dictionary<string, DigglesAnimClip> s_dynClipCache = new();
        private static readonly Dictionary<string, Texture2D> s_textureCache =
            new(System.StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TgaAlphaKind> s_alphaKindCache =
            new(System.StringComparer.OrdinalIgnoreCase);
        private static Vector3[]? s_sphereDat;

        // ----- mesh build (mirrors ThreeDbScriptedImporter exactly) --------

        private static (Mesh?, Material[]?) BuildMesh(
            ThreeDbModel db, int meshIdx, Material[] materials, string nameHint)
        {
            var srcMesh = db.Meshes[meshIdx];
            if (srcMesh.SubMeshes.Count == 0) return (null, null);

            var combinedVerts = new List<Vector3>();
            var combinedUvs = new List<Vector2>();
            var combinedNormals = new List<Vector3>();
            var perSubmeshTris = new List<int[]>();
            var perSubmeshMat  = new List<Material>();

            // Vertex decode, shared with Diggles.Runtime.DigglesModelLibrary.BuildMesh.
            // Each component is a 16-bit value in offset-binary clustered near 0x8000;
            // the meaningful coordinate occupies only a small window of the range, and
            // the per-mesh BBox is the exact AABB of those values. So decode = normalise
            // each axis's actual unsigned range across the whole mesh into the bbox, then
            // bake the shared ModelToWorldScale so the result renders at the same size
            // every other spawn path produces.
            const float S = Diggles.Runtime.DigglesModelLibrary.ModelToWorldScale;
            int uMinX = int.MaxValue, uMinY = int.MaxValue, uMinZ = int.MaxValue;
            int uMaxX = int.MinValue, uMaxY = int.MinValue, uMaxZ = int.MinValue;
            foreach (var sub in srcMesh.SubMeshes)
            {
                if (sub.VListIndex >= db.VLists.Count) continue;
                if (sub.IdxListIndex >= db.IndexLists.Count) continue;
                foreach (var q in db.VLists[sub.VListIndex])
                {
                    int ux = (ushort)q.x, uy = (ushort)q.y, uz = (ushort)q.z;
                    if (ux < uMinX) uMinX = ux; if (ux > uMaxX) uMaxX = ux;
                    if (uy < uMinY) uMinY = uy; if (uy > uMaxY) uMaxY = uy;
                    if (uz < uMinZ) uMinZ = uz; if (uz > uMaxZ) uMaxZ = uz;
                }
            }
            if (uMaxX < uMinX) return (null, null); // no verts
            var bMin = srcMesh.BBoxMin;
            var bMax = srcMesh.BBoxMax;
            float sx = uMaxX > uMinX ? (bMax.x - bMin.x) / (uMaxX - uMinX) : 0f;
            float sy = uMaxY > uMinY ? (bMax.y - bMin.y) / (uMaxY - uMinY) : 0f;
            float sz = uMaxZ > uMinZ ? (bMax.z - bMin.z) / (uMaxZ - uMinZ) : 0f;

            foreach (var sub in srcMesh.SubMeshes)
            {
                if (sub.VListIndex >= db.VLists.Count) continue;
                if (sub.IdxListIndex >= db.IndexLists.Count) continue;

                var vList = db.VLists[sub.VListIndex];
                var idxList = db.IndexLists[sub.IdxListIndex];
                var uvList = sub.UvListIndex < db.UvLists.Count
                    ? db.UvLists[sub.UvListIndex]
                    : System.Array.Empty<Vector2>();
                var nList = sub.NListIndex < db.NLists.Count
                    ? db.NLists[sub.NListIndex]
                    : System.Array.Empty<byte>();

                int vertexBase = combinedVerts.Count;
                foreach (var q in vList)
                {
                    int ux = (ushort)q.x, uy = (ushort)q.y, uz = (ushort)q.z;
                    var v = new Vector3(
                        (bMin.x + (ux - uMinX) * sx) * S,
                        (bMin.y + (uy - uMinY) * sy) * S,
                        (bMin.z + (uz - uMinZ) * sz) * S);
                    combinedVerts.Add(ApplyAxisConvention(v));
                }

                AppendUvs(combinedUvs, uvList, vList.Length);
                AppendNormals(combinedNormals, nList, vList.Length);

                var tris = BuildTriangles(idxList, vertexBase, vList.Length);
                perSubmeshTris.Add(tris);

                var material = sub.MaterialIndex >= 0 && sub.MaterialIndex < materials.Length
                    ? materials[sub.MaterialIndex]
                    : (materials.Length > 0 ? materials[0] : null!);
                perSubmeshMat.Add(material);
            }

            if (combinedVerts.Count == 0) return (null, null);

            var mesh = new Mesh { name = nameHint };
            mesh.indexFormat = combinedVerts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(combinedVerts);
            if (combinedUvs.Count == combinedVerts.Count)
                mesh.SetUVs(0, combinedUvs);
            if (combinedNormals.Count == combinedVerts.Count)
                mesh.SetNormals(combinedNormals);
            else
                mesh.RecalculateNormals();

            mesh.subMeshCount = perSubmeshTris.Count;
            for (int i = 0; i < perSubmeshTris.Count; i++)
                mesh.SetTriangles(perSubmeshTris[i], i);
            mesh.RecalculateBounds();

            return (mesh, perSubmeshMat.ToArray());
        }

        // Keep verts in the Diggles native frame (head at -Y, face at +Z). The
        // marker spawn rotation (Q_pitch * markerRot) plus root.rotation (-90 X)
        // together fold these into the world view.
        private static Vector3 ApplyAxisConvention(Vector3 v) => v;

        private static void AppendUvs(List<Vector2> dst, Vector2[] src, int expectedCount)
        {
            if (src.Length == expectedCount)
            {
                foreach (var uv in src)
                    dst.Add(new Vector2(uv.x, 1f - uv.y));  // flip V
                return;
            }
            for (int i = 0; i < expectedCount; i++) dst.Add(Vector2.zero);
        }

        private static void AppendNormals(List<Vector3> dst, byte[] src, int expectedCount)
        {
            if (s_sphereDat == null || src.Length != expectedCount)
            {
                for (int i = 0; i < expectedCount; i++) dst.Add(Vector3.up);
                return;
            }
            // sphere.dat normals in Diggles render-space — used as-is to
            // match raw verts.
            foreach (var idx in src) dst.Add(s_sphereDat[idx]);
        }

        private static int[] BuildTriangles(ushort[] indices, int vertexBase, int vListLength)
        {
            int triCount = indices.Length / 3;
            var tris = new int[triCount * 3];
            for (int i = 0; i < triCount; i++)
            {
                ushort i0 = indices[i * 3 + 0];
                ushort i1 = indices[i * 3 + 1];
                ushort i2 = indices[i * 3 + 2];
                if (i0 >= vListLength || i1 >= vListLength || i2 >= vListLength) continue;
                tris[i * 3 + 0] = vertexBase + i0;
                tris[i * 3 + 1] = vertexBase + i1;
                tris[i * 3 + 2] = vertexBase + i2;
            }
            return tris;
        }

        // ----- materials (Diggles/Model shader — emulates DX7 fixed-function) -----

        private static Material[] GetOrBuildMaterials(
            string dbName, ThreeDbModel db, string dataRoot, AssetImportContext? ctx)
        {
            if (s_dbMaterials.TryGetValue(dbName, out var cached)) return cached;
            // Diggles/Model: custom shader with ZWrite always on + alpha-blend +
            // alpha-test cutoff. Bypasses URP/Lit's refusal to honor ZWrite=1 in
            // Transparent surface mode, which causes Z-fighting flicker.
            var shader = Shader.Find("Diggles/Model")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No Diggles/Model, URP/Lit or Standard shader");
            var mats = new Material[db.Materials.Count];
            for (int i = 0; i < db.Materials.Count; i++)
            {
                var src = db.Materials[i];
                var mat = new Material(shader) { name = $"{dbName}_{src.Name}" };
                var tex = LoadTextureFromDisk(dataRoot, src.Name);
                if (tex != null)
                {
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                    else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                }
                // Per-material render state, picked from the texture's alpha
                // classification (set when the TGA was loaded).
                var kind = s_alphaKindCache.TryGetValue(src.Name, out var k)
                    ? k : TgaAlphaKind.Opaque;
                ConfigureMaterialForAlphaKind(mat, kind);
                if (ctx != null) ctx.AddObjectToAsset(
                    $"Mat_{dbName}_{i}_{Sanitize(src.Name)}", mat);
                mats[i] = mat;
            }
            s_dbMaterials[dbName] = mats;
            return mats;
        }

        // Translates the texture's TgaAlphaKind into per-material render-state
        // properties consumed by Diggles/Model shader's [_SrcBlend][_DstBlend][
        // _ZWrite][_AlphaToMask][_Cutoff] directives.
        //
        // Opaque — solid; no blend, no clip
        // Cutout — alpha-test + A2C (foliage edges via MSAA coverage)
        // Transparent — alpha-blend + ZWrite Off (FX lights, glows)
        private static void ConfigureMaterialForAlphaKind(Material mat, TgaAlphaKind kind)
        {
            const float BlendOne = 1f;
            const float BlendZero = 0f;
            float BlendSrcAlpha = (float)UnityEngine.Rendering.BlendMode.SrcAlpha;
            float BlendOneMinusSrcAlpha = (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
            switch (kind)
            {
                case TgaAlphaKind.Opaque:
                    if (mat.HasProperty("_SrcBlend"))   mat.SetFloat("_SrcBlend",   BlendOne);
                    if (mat.HasProperty("_DstBlend"))   mat.SetFloat("_DstBlend",   BlendZero);
                    if (mat.HasProperty("_ZWrite"))     mat.SetFloat("_ZWrite",     1f);
                    if (mat.HasProperty("_AlphaToMask")) mat.SetFloat("_AlphaToMask", 0f);
                    if (mat.HasProperty("_Cutoff"))     mat.SetFloat("_Cutoff",     0f);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                    break;
                case TgaAlphaKind.Cutout:
                    if (mat.HasProperty("_SrcBlend"))   mat.SetFloat("_SrcBlend",   BlendOne);
                    if (mat.HasProperty("_DstBlend"))   mat.SetFloat("_DstBlend",   BlendZero);
                    if (mat.HasProperty("_ZWrite"))     mat.SetFloat("_ZWrite",     1f);
                    if (mat.HasProperty("_AlphaToMask")) mat.SetFloat("_AlphaToMask", 1f);
                    if (mat.HasProperty("_Cutoff"))     mat.SetFloat("_Cutoff",     0.05f);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                    break;
                case TgaAlphaKind.Transparent:
                    if (mat.HasProperty("_SrcBlend"))   mat.SetFloat("_SrcBlend",   BlendSrcAlpha);
                    if (mat.HasProperty("_DstBlend"))   mat.SetFloat("_DstBlend",   BlendOneMinusSrcAlpha);
                    if (mat.HasProperty("_ZWrite"))     mat.SetFloat("_ZWrite",     0f);
                    if (mat.HasProperty("_AlphaToMask")) mat.SetFloat("_AlphaToMask", 0f);
                    if (mat.HasProperty("_Cutoff"))     mat.SetFloat("_Cutoff",     0f);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    break;
            }
        }


        private static readonly string[] LodFolders = { "m256", "m128", "m064", "m032", "Gray", "Misc", "ClassIcons" };

        private static Texture2D? LoadTextureFromDisk(string dataRoot, string materialName)
        {
            if (s_textureCache.TryGetValue(materialName, out var cached)) return cached;
            var textureDir = Path.Combine(dataRoot, "Texture");
            if (!Directory.Exists(textureDir)) return null;
            foreach (var folder in LodFolders)
            {
                var subDir = Path.Combine(textureDir, folder);
                if (!Directory.Exists(subDir)) continue;
                var candidate = Path.Combine(subDir, materialName + ".tga");
                if (!File.Exists(candidate))
                {
                    var matches = Directory.GetFiles(subDir, materialName + ".*").FirstOrDefault();
                    if (matches == null) continue;
                    candidate = matches;
                }
                try
                {
                    var r = TgaLoader.LoadFromFile(candidate);
                    if (r != null)
                    {
                        s_textureCache[materialName] = r.Texture;
                        s_alphaKindCache[materialName] = r.AlphaKind;
                        return r.Texture;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"TGA load failed for '{candidate}': {e.Message}");
                }
            }
            return null;
        }

        // ----- DB loading + sphere.dat --------------------------------------

        private static ThreeDbModel? LoadDb(string dbName, string dataRoot)
        {
            if (s_sphereDat == null)
            {
                var sp = Path.Combine(dataRoot, "sphere.dat");
                if (File.Exists(sp))
                {
                    var bytes = File.ReadAllBytes(sp);
                    if (bytes.Length == 3072)
                    {
                        s_sphereDat = new Vector3[256];
                        using var s = new MemoryStream(bytes);
                        using var br = new BinaryReader(s);
                        for (int i = 0; i < 256; i++)
                            s_sphereDat[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    }
                }
            }
            if (s_dbCache.TryGetValue(dbName, out var cached)) return cached;
            var path = Path.Combine(dataRoot, "3db", dbName + ".3db");
            if (!File.Exists(path))
            {
                path = Path.Combine(dataRoot, "3db", dbName.ToLower() + ".3db");
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"MarkerModelResolver: missing 3db file {dbName}");
                    return null;
                }
            }
            try
            {
                var model = ThreeDbParser.Parse(path);
                s_dbCache[dbName] = model;
                return model;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"MarkerModelResolver: parse failed for {path}: {e.Message}");
                return null;
            }
        }

        // ----- class→model tables -------------------------------------------

        private static void EnsureMapLoaded()
        {
            if (s_classMap != null && s_objDb != null) return;
            // class_model_map_v2.tsv: <class>\t<obj>\t<anim>\t<visible>\n
            // obj_db_map.tsv: <obj>\t<db>\n
            // Built by tools/build_class_map.py (parses all Tcl class definitions
            // + scans every.3db for Object names).
            var classPath = Path.Combine(Application.streamingAssetsPath, "class_model_map_v2.tsv");
            var objDbPath = Path.Combine(Application.streamingAssetsPath, "obj_db_map.tsv");
            if (!File.Exists(classPath) || !File.Exists(objDbPath))
            {
                Debug.LogError($"MarkerModelResolver: missing TSV ({classPath} or {objDbPath})");
                s_classMap = new Dictionary<string, MapEntry>();
                s_objDb = new Dictionary<string, string>();
                return;
            }
            s_classMap = new Dictionary<string, MapEntry>(4096);
            foreach (var line in File.ReadAllLines(classPath))
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 4) continue;
                s_classMap[p[0]] = new MapEntry
                {
                    obj = string.IsNullOrEmpty(p[1]) ? null : p[1],
                    anim = string.IsNullOrEmpty(p[2]) ? null : p[2],
                    visible = p[3] == "1",
                };
            }
            s_objDb = new Dictionary<string, string>(4096);
            foreach (var line in File.ReadAllLines(objDbPath))
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                s_objDb[p[0]] = p[1];
            }
            Debug.Log($"MarkerModelResolver: loaded {s_classMap.Count} classes, {s_objDb.Count} obj→db");
        }

        private static string Sanitize(string name) =>
            string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
    }
}
