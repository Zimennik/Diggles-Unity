#nullable enable

namespace Diggles.Editor.Importers
{
    using Diggles.Runtime.World;
    using System.Collections.Generic;
    using System.IO;
    using UnityEditor.AssetImporters;
    using UnityEngine;

    public sealed class CampaignBuildResult
    {
        public byte[] Heightmap { get; init; } = System.Array.Empty<byte>();
        public byte[] TerrainType { get; init; } = System.Array.Empty<byte>();
        public ushort[] L2mCellIds { get; init; } = System.Array.Empty<ushort>();
        public byte[] SolidityTopSolidZ { get; init; } = System.Array.Empty<byte>();
        public int Width { get; init; }
        public int Height { get; init; }
        public int ChunksLoaded { get; init; }
        public int ChunksAttempted { get; init; }
        public List<string> Diagnostics { get; init; } = new();
        public List<MarkerInstance> Objects { get; init; } = new();
    }

    public readonly struct MarkerInstance
    {
        public MarkerInstance(string className, Vector3 position, Vector3 rotation, int lineNumber)
        {
            this.ClassName = className;
            this.Position = position;
            this.Rotation = rotation;
            this.LineNumber = lineNumber;
        }
        public string ClassName { get; }
        public Vector3 Position { get; }    // World-space, in tile units (not yet scaled by tileSize)
        public Vector3 Rotation { get; }    // Diggles rX, rY, rZ in radians
        public int LineNumber { get; }
    }

    public static class CampaignWorldBuilder
    {
        public const byte DefaultZ = 4;
        public const float DefaultZf = 4f;
        public const float BowlFloorZ = -4f;
        public const float RimZ = 2f;
        public const float SkirtFloorZ = -6f;

        public static CampaignBuildResult BuildWorld(
            CampaignWorld world, string dataRoot)
        {
            var w = world.WorldWidth;
            var h = world.WorldHeight;
            var z = new byte[w * h];
            var tt = new byte[w * h];
            var cell = new ushort[w * h];
            var topSolid = new byte[w * h];
            for (var i = 0; i < z.Length; i++)
            {
                z[i] = DefaultZ;
                topSolid[i] = (byte)(PmpMap.SubGridSide - 1);
            }

            var diag = new List<string>(world.Diagnostics);

            var chunksAttempted = 0;
            var chunksLoaded = 0;
            var objects = new List<MarkerInstance>();
            foreach (var tpl in world.Templates)
            {
                foreach (var chunk in EnumerateLeaves(tpl))
                {
                    chunksAttempted++;
                    var loaded = false;
                    if (chunk.PmpPath != null && File.Exists(chunk.PmpPath))
                    {
                        try
                        {
                            var pmp = PmpParser.Parse(chunk.PmpPath);
                            BlitPmp(pmp, chunk.WorldX, chunk.WorldY, z, tt, topSolid, w, h);
                            loaded = true;
                        }
                        catch (System.Exception e)
                        {
                            diag.Add($"  PMP parse failed for {chunk.Name}: {e.Message}");
                        }
                    }
                    if (chunk.L2mPath != null && File.Exists(chunk.L2mPath))
                    {
                        try
                        {
                            var l2m = L2mParser.Parse(chunk.L2mPath);
                            BlitL2m(l2m, chunk.WorldX, chunk.WorldY, cell, w, h);
                            loaded = true;
                        }
                        catch (System.Exception e)
                        {
                            diag.Add($"  L2M parse failed for {chunk.Name}: {e.Message}");
                        }
                    }
                    if (loaded) chunksLoaded++;
                }

                var objTcl = ResolveObjectTcl(tpl, dataRoot);
                if (objTcl != null)
                {
                    var spawned = TclObjectParser.Parse(objTcl);
                    foreach (var o in spawned)
                    {
                        objects.Add(new MarkerInstance(
                            o.ClassName,
                            new Vector3(o.Position.x + tpl.WorldX,
                                        o.Position.z,
                                        -(o.Position.y + tpl.WorldY)),
                            o.Rotation,
                            o.LineNumber));
                    }
                    if (spawned.Count > 0)
                        diag.Add($"  spawned {spawned.Count} objects from " +
                                 $"{Path.GetFileName(objTcl)}");
                }
            }

            return new CampaignBuildResult
            {
                Heightmap = z,
                TerrainType = tt,
                L2mCellIds = cell,
                SolidityTopSolidZ = topSolid,
                Width = w,
                Height = h,
                ChunksLoaded = chunksLoaded,
                ChunksAttempted = chunksAttempted,
                Diagnostics = diag,
                Objects = objects,
            };
        }

        public static (int xMin, int yMin, int xMax, int yMax)? FindBbox(
            CampaignBuildResult build, int margin)
        {
            var xMin = build.Width; var yMin = build.Height; var xMax = -1; var yMax = -1;
            for (var y = 0; y < build.Height; y++)
            {
                for (var x = 0; x < build.Width; x++)
                {
                    var i = y * build.Width + x;
                    if (build.Heightmap[i] != DefaultZ ||
                        build.TerrainType[i] != 0 ||
                        build.L2mCellIds[i] != 0)
                    {
                        if (x < xMin) xMin = x;
                        if (y < yMin) yMin = y;
                        if (x > xMax) xMax = x;
                        if (y > yMax) yMax = y;
                    }
                }
            }
            if (xMax < 0) return null;
            xMin = System.Math.Max(0, xMin - margin);
            yMin = System.Math.Max(0, yMin - margin);
            xMax = System.Math.Min(build.Width - 1, xMax + margin);
            yMax = System.Math.Min(build.Height - 1, yMax + margin);
            return (xMin, yMin, xMax, yMax);
        }

        public static void EmitMeshAndObjects(
            GameObject root, CampaignBuildResult build, AssetImportContext? ctx,
            float tileSize, float tileHeight,
            int xMin, int yMin, int xMax, int yMax,
            bool spawnObjectMarkers, float markerRadius,
            string dataRoot = "")
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No URP or Standard shader");

            var grassVerts = new List<Vector3>(); var grassTris = new List<int>();
            var stoneVerts = new List<Vector3>(); var stoneTris = new List<int>();
            var dirtVerts = new List<Vector3>(); var dirtTris = new List<int>();
            var w = build.Width; var h = build.Height;
            var pmpZ = build.Heightmap;
            var tt = build.TerrainType;
            var cells = build.L2mCellIds;
            var topSolid = build.SolidityTopSolidZ;
            var skirtFloor = SkirtFloorZ * tileHeight;

            float TileLogicalZ(int tx, int ty)
            {
                if (tx < 0 || tx >= w || ty < 0 || ty >= h) return DefaultZf;
                var i = ty * w + tx;
                var ttype = tt[i];
                var c = cells[i];
                var terrainZ = DefaultZf;
                if (ttype == 0x85) terrainZ = -4f;
                else if (ttype == 0x84) terrainZ = c != 0 ? DefaultZf : 0f;
                else if (ttype == 0x02) terrainZ = pmpZ[i] >= DefaultZ ? DefaultZf : -2f;
                var solidZ = (topSolid[i] - 7f) * 2f + DefaultZf;
                return System.Math.Min(terrainZ, solidZ);
            }

            float TileTopHeight(int tx, int ty) => TileLogicalZ(tx, ty) * tileHeight;

            (List<Vector3> Verts, List<int> Tris) ClassifyMaterial(int i)
            {
                var ttype = tt[i];
                var c = cells[i];
                if (ttype == 0x85) return (grassVerts, grassTris);
                if (ttype == 0x84)
                {
                    return c == 0 ? (dirtVerts, dirtTris) : (stoneVerts, stoneTris);
                }
                if (ttype == 0x02)
                {
                    return pmpZ[i] < DefaultZ
                        ? (dirtVerts, dirtTris)
                        : (stoneVerts, stoneTris);
                }
                return (dirtVerts, dirtTris);
            }

            for (var ty = yMin; ty <= yMax; ty++)
            {
                for (var tx = xMin; tx <= xMax; tx++)
                {
                    var i = ty * w + tx;
                    var (verts, tris) = ClassifyMaterial(i);

                    var z = TileTopHeight(tx, ty);
                    var x0 = tx * tileSize;
                    var x1 = x0 + tileSize;
                    var yA = -(ty * tileSize);
                    var yB = yA - tileSize;

                    var idx = verts.Count;
                    verts.Add(new Vector3(x0, yA, z));
                    verts.Add(new Vector3(x1, yA, z));
                    verts.Add(new Vector3(x0, yB, z));
                    verts.Add(new Vector3(x1, yB, z));
                    tris.Add(idx); tris.Add(idx + 2); tris.Add(idx + 1);
                    tris.Add(idx + 2); tris.Add(idx + 3); tris.Add(idx + 1);

                    EmitVoxelSide(stoneVerts, stoneTris,
                        new Vector3(x0, yA, z), new Vector3(x1, yA, z),
                        TileTopHeight(tx, ty - 1), skirtFloor, ty - 1 < yMin);
                    EmitVoxelSide(stoneVerts, stoneTris,
                        new Vector3(x1, yB, z), new Vector3(x0, yB, z),
                        TileTopHeight(tx, ty + 1), skirtFloor, ty + 1 > yMax);
                    EmitVoxelSide(stoneVerts, stoneTris,
                        new Vector3(x1, yA, z), new Vector3(x1, yB, z),
                        TileTopHeight(tx + 1, ty), skirtFloor, tx + 1 > xMax);
                    EmitVoxelSide(stoneVerts, stoneTris,
                        new Vector3(x0, yB, z), new Vector3(x0, yA, z),
                        TileTopHeight(tx - 1, ty), skirtFloor, tx - 1 < xMin);
                }
            }

            BuildOneSubmesh(root, ctx, shader, "Grass (meadow)",
                new Color(0.42f, 0.62f, 0.28f),
                grassVerts, grassTris, "GrassMesh", "GrassMat");
            BuildOneSubmesh(root, ctx, shader, "Dirt (earth)",
                new Color(0.52f, 0.40f, 0.26f),
                dirtVerts, dirtTris, "DirtMesh", "DirtMat");
            BuildOneSubmesh(root, ctx, shader, "Stone (walls + barriers + cliffs)",
                new Color(0.48f, 0.45f, 0.42f),
                stoneVerts, stoneTris, "StoneMesh", "StoneMat");

            if (spawnObjectMarkers && build.Objects.Count > 0)
                SpawnObjectMarkers(root, ctx, shader, build, tileSize, tileHeight,
                    markerRadius, dataRoot);
        }

        private static void EmitVoxelSide(
            List<Vector3> verts, List<int> tris,
            Vector3 topA, Vector3 topB,
            float neighborTopZ, float skirtFloor, bool isBboxEdge)
        {
            var targetZ = isBboxEdge ? skirtFloor : neighborTopZ;
            if (targetZ >= topA.z - 0.001f) return;

            var idx = verts.Count;
            var botA = new Vector3(topA.x, topA.y, targetZ);
            var botB = new Vector3(topB.x, topB.y, targetZ);
            verts.Add(topA);
            verts.Add(topB);
            verts.Add(botA);
            verts.Add(botB);
            tris.Add(idx); tris.Add(idx + 1); tris.Add(idx + 2);
            tris.Add(idx + 2); tris.Add(idx + 1); tris.Add(idx + 3);
        }

        private static void SpawnObjectMarkers(
            GameObject root, AssetImportContext? ctx, Shader shader,
            CampaignBuildResult build, float tileSize, float tileHeight, float markerRadius,
            string dataRoot)
        {
            Mesh? fallbackMesh = null;   // lazily-created octahedron for unresolved markers
            var matCache = new Dictionary<string, Material>();
            var bucket = new GameObject("Objects (.tcl markers)");
            bucket.transform.SetParent(root.transform, false);

            int placed = 0, fallback = 0, hidden = 0;
            foreach (var marker in build.Objects)
            {
                var className = marker.ClassName;
                var pos = marker.Position;
                int line = marker.LineNumber;

                var go = new GameObject($"{className}_L{line}");
                go.transform.SetParent(bucket.transform, false);
                go.transform.localPosition = new Vector3(
                    pos.x * tileSize, pos.z * tileSize, pos.y * tileHeight);

                // Rotation: marker.Rotation = (rX, rY, rZ) in radians.
                // Convert axes (Diggles X horiz / Y depth / Z up → Unity X / up / depth)
                // and convert radians to degrees.
                var rot = marker.Rotation;
                go.transform.localRotation = Quaternion.Euler(
                    rot.x * Mathf.Rad2Deg,
                    rot.z * Mathf.Rad2Deg,   // Diggles Z-axis rotation = Unity Y-axis (yaw)
                    -rot.y * Mathf.Rad2Deg);

                var resolved = MarkerModelResolver.Resolve(className, dataRoot, ctx);
                if (resolved?.Mesh != null)
                {
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = resolved.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    if (resolved.Materials != null && resolved.Materials.Length > 0)
                        mr.sharedMaterials = resolved.Materials;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    mr.receiveShadows = true;
                    placed++;
                }
                else if (className.StartsWith("Trigger_") ||
                         className.StartsWith("Sequence") ||
                         className.StartsWith("Info_") ||
                         className.StartsWith("Fake_"))
                {
                    // Invisible logical marker — just leave empty GameObject.
                    hidden++;
                }
                else
                {
                    // Unresolved real class → pink octahedron so it stands out.
                    if (fallbackMesh == null)
                    {
                        fallbackMesh = CreateOctahedronMesh();
                        if (ctx != null) ctx.AddObjectToAsset("MarkerSphere", fallbackMesh);
                    }
                    go.transform.localScale = Vector3.one * markerRadius;
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = fallbackMesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = GetOrCreateColoredMaterial(
                        className, matCache, shader, ctx);
                    fallback++;
                }
            }
            Debug.Log($"[CampaignWorldBuilder] markers placed={placed} unresolved={fallback} hidden={hidden}");
        }

        private static IEnumerable<CampaignTemplate> EnumerateLeaves(CampaignTemplate t)
        {
            if (t.SubChunks.Count == 0) { yield return t; yield break; }
            foreach (var s in t.SubChunks)
                foreach (var leaf in EnumerateLeaves(s))
                    yield return leaf;
        }

        private static string? ResolveObjectTcl(CampaignTemplate t, string dataRoot)
        {
            var candidates = new[]
            {
                Path.Combine(dataRoot, "Templates", t.Name + ".tcl"),
                Path.ChangeExtension(t.TclPath, ".tcl"),
            };
            foreach (var c in candidates)
                if (c != null && File.Exists(c) &&
                    !c.Replace('\\', '/').Contains("/tcl/")) return c;
            return null;
        }

        private static void BlitPmp(PmpMap pmp, int wx, int wy,
            byte[] dstZ, byte[] dstType, byte[] dstTopSolid, int dstW, int dstH)
        {
            for (var sy = 0; sy < pmp.Height; sy++)
            {
                var dy = wy + sy;
                if (dy < 0 || dy >= dstH) continue;
                for (var sx = 0; sx < pmp.Width; sx++)
                {
                    var dx = wx + sx;
                    if (dx < 0 || dx >= dstW) continue;
                    var terrain = pmp.TerrainAt(sx, sy);
                    if (terrain == 0) continue;
                    var di = dy * dstW + dx;
                    dstZ[di] = pmp.HeightAt(sx, sy);
                    dstType[di] = terrain;
                    dstTopSolid[di] = ComputeTopSolidZ(pmp, sx, sy);
                }
            }
        }

        private static byte ComputeTopSolidZ(PmpMap pmp, int sx, int sy)
        {
            var maxTop = -1;
            for (var subX = 0; subX < PmpMap.SubGridSide; subX++)
            {
                for (var sz = PmpMap.SubGridSide - 1; sz >= 0; sz--)
                {
                    if (pmp.SolidityAt(sx, sy, subX, sz) != 3)
                    {
                        if (sz > maxTop) maxTop = sz;
                        break;
                    }
                }
            }
            return maxTop < 0 ? (byte)0 : (byte)maxTop;
        }

        private static void BlitL2m(L2mMap l2m, int wx, int wy,
            ushort[] dst, int dstW, int dstH)
        {
            for (var sy = 0; sy < l2m.Height; sy++)
            {
                var dy = wy + sy;
                if (dy < 0 || dy >= dstH) continue;
                for (var sx = 0; sx < l2m.Width; sx++)
                {
                    var dx = wx + sx;
                    if (dx < 0 || dx >= dstW) continue;
                    var c = l2m.CellAt(sx, sy);
                    if (c != 0) dst[dy * dstW + dx] = c;
                }
            }
        }

        private static void BuildOneSubmesh(
            GameObject root, AssetImportContext? ctx, Shader shader,
            string childName, Color color,
            List<Vector3> verts, List<int> tris,
            string meshKey, string matKey)
        {
            if (verts.Count == 0) return;
            var go = new GameObject(childName);
            go.transform.SetParent(root.transform, false);
            var mesh = new Mesh
            {
                name = meshKey,
                indexFormat = verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (ctx != null) ctx.AddObjectToAsset(meshKey, mesh);
            var mat = new Material(shader) { name = matKey };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;
            if (ctx != null) ctx.AddObjectToAsset(matKey, mat);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }

        private static Mesh CreateOctahedronMesh()
        {
            var mesh = new Mesh { name = "Octahedron" };
            mesh.vertices = new[]
            {
                new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(0, -1, 0),
                new Vector3(0, 0, 1), new Vector3(0, 0, -1),
            };
            mesh.triangles = new[]
            {
                0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4,
                2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material GetOrCreateColoredMaterial(
            string className, Dictionary<string, Material> cache,
            Shader shader, AssetImportContext? ctx)
        {
            if (cache.TryGetValue(className, out var m)) return m;
            unchecked
            {
                var hash = 0u;
                foreach (var ch in className) hash = hash * 31u + ch;
                hash *= 2654435761u;
                var color = new Color(
                    (((hash >> 16) & 0xFFu) / 255f) * 0.6f + 0.3f,
                    (((hash >> 8) & 0xFFu) / 255f) * 0.6f + 0.3f,
                    ((hash & 0xFFu) / 255f) * 0.6f + 0.3f);
                var mat = new Material(shader) { name = $"marker_{className}" };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                mat.color = color;
                cache[className] = mat;
                if (ctx != null) ctx.AddObjectToAsset($"MarkerMat_{className}", mat);
                return mat;
            }
        }
    }
}
