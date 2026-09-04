#nullable enable

namespace Diggles.Editor.Importers
{
    using Diggles.Runtime.World;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using UnityEditor;
    using UnityEditor.AssetImporters;
    using UnityEngine;

    [ScriptedImporter(version: 17, ext: "l2m")]
    public sealed class L2mScriptedImporter : ScriptedImporter
    {
        [Tooltip("Absolute path to Diggles original/data folder. Used to find " +
                 "the BlockDB?.BDB catalog and the paired .pmp heightmap.")]
        [SerializeField] private string originalDataRoot = "";

        private string DataRoot => string.IsNullOrEmpty(this.originalDataRoot)
            ? Diggles.Runtime.DigglesConfig.ConfiguredDataRoot
            : this.originalDataRoot;

        [Tooltip("World size of each map tile in Unity units.")]
        [SerializeField] private float tileSize = 1f;

        [Tooltip("Vertical size of one heightmap step. PMP heightmap range is " +
                 "0..8 (plus L2M corner refinements 0..2). Default 2 gives a " +
                 "visible bowl depth (~20 units) vs 60-tile map width.")]
        [SerializeField] private float tileHeight = 2f;

        [Tooltip("OberweltSideView (default, validated via runtime memory dump):\n" +
                 "  Runtime confirmed: heightmap=20 means OBERWELT surface (open plain).\n" +
                 "  heightmap=4 means default solid stone (underground mass).\n" +
                 "  cellId!=0 marks template-placed blocks (cave walls, special features).\n" +
                 "  8x8 subgrid value 3 = scapehole (dwarf-walkable carved area).\n" +
                 "Other modes: legacy debug views.")]
        [SerializeField] private RenderStyle renderStyle = RenderStyle.OberweltSideView;

        [Tooltip("Floor (default): X-Z plane, height on Y (top-down isometric — fits Diggles' camera).\n" +
                 "Wall: X-Y plane, height on Z (legacy debug).")]
        [SerializeField] private Orientation orientation = Orientation.Floor;

        [Tooltip("PlaceholderCubes only: stack a full column from 0 to height.")]
        [SerializeField] private bool stackColumns = true;

        [Tooltip("PlaceholderCubes only: also draw cells where L2M = 0.")]
        [SerializeField] private bool renderEmptyCells;

        [Tooltip("If true: parse the paired .tcl file and place a labeled " +
                 "marker sphere for each `new <Class>` object instance " +
                 "(dwarves, mushrooms, mountain, trees, etc.). Stage 1 of " +
                 "object visualization — markers only, real .3db meshes come later.")]
        [SerializeField] private bool placeObjectMarkers = true;

        [Tooltip("Marker sphere radius in world units (in tileSize).")]
        [SerializeField] private float markerRadius = 0.3f;

        public enum Orientation { Floor, Wall }
        public enum RenderStyle { OberweltSideView, SideViewBlocks, StoneVoxels, Heightfield, HeightField8x8, CornerQuads, PlaceholderCubes }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var map = L2mParser.Parse(ctx.assetPath);
            if (map.FileSize == 0)
            {
                ctx.LogImportWarning("L2M file is empty");
                var emptyRoot = new GameObject("EmptyMap");
                ctx.AddObjectToAsset("RootPrefab", emptyRoot);
                ctx.SetMainObject(emptyRoot);
                return;
            }
            if (map.BytesRemaining != 0)
            {
                ctx.LogImportWarning(
                    $"L2M parser left {map.BytesRemaining} bytes unread");
            }

            var bestBdbPath = FindMatchingBdb(map, this.DataRoot);
            var bdbName = bestBdbPath != null ? Path.GetFileName(bestBdbPath) : "<none>";

            var pmp = TryLoadPairedPmp(ctx.assetPath, map, this.DataRoot,
                out var pmpSource);

            var root = new GameObject(Path.GetFileNameWithoutExtension(ctx.assetPath));

            string summary = this.renderStyle switch
            {
                RenderStyle.OberweltSideView => BuildOberweltSideView(
                    root, map, pmp, ctx, this.tileSize, this.tileHeight, this.orientation),
                RenderStyle.SideViewBlocks => BuildSideViewBlocks(
                    root, map, pmp, ctx, this.tileSize, this.tileHeight, this.orientation),
                RenderStyle.StoneVoxels => BuildStoneVoxels(
                    root, map, pmp, ctx, this.tileSize, this.tileHeight, this.orientation),
                RenderStyle.Heightfield => BuildHeightfieldByteMap(
                    root, map, pmp, ctx, this.tileSize, this.tileHeight, this.orientation),
                RenderStyle.HeightField8x8 => BuildHeightField8x8(
                    root, map, pmp, ctx, this.tileSize, this.tileHeight, this.orientation),
                RenderStyle.CornerQuads => BuildCornerQuads(
                    root, map, pmp, ctx, this.tileSize, this.tileHeight, this.orientation),
                _ => BuildPlaceholderCubesSummary(
                    root, map, pmp, ctx, this.tileSize, this.tileHeight,
                    this.stackColumns, this.orientation, this.renderEmptyCells),
            };

            var objCount = 0;
            string objNote = "";
            if (this.placeObjectMarkers)
            {
                (objCount, objNote) = PlaceObjectMarkers(
                    root, ctx, ctx.assetPath,
                    this.tileSize, this.tileHeight,
                    this.markerRadius, this.orientation);
            }

            ctx.AddObjectToAsset("RootPrefab", root);
            ctx.SetMainObject(root);

            var pmpNote = pmp != null
                ? $", pmp={pmpSource} ({pmp.Width}x{pmp.Height}, " +
                  $"h={pmp.HeightMap.Min()}..{pmp.HeightMap.Max()}, " +
                  $"voxels-air={pmp.SolidityVoxels.Count(b => b != 0)})"
                : ", pmp=<none — flat>";
            var objSummary = this.placeObjectMarkers
                ? $", objects={objCount} ({objNote})"
                : "";
            ctx.LogImportWarning(
                $"L2M '{Path.GetFileNameWithoutExtension(ctx.assetPath)}': " +
                $"{map.Width}x{map.Height} grid, {summary}, " +
                $"bdb={bdbName}{pmpNote}{objSummary}");
        }

        private static (int count, string summary) PlaceObjectMarkers(
            GameObject root, AssetImportContext ctx, string l2mAssetPath,
            float tileSize, float tileHeight, float markerRadius,
            Orientation orientation)
        {
            var tclPath = Path.ChangeExtension(l2mAssetPath, ".tcl");
            if (!File.Exists(tclPath))
            {
                return (0, "no sibling .tcl");
            }

            var objects = TclObjectParser.Parse(tclPath);
            if (objects.Count == 0)
            {
                return (0, "no `new` calls in .tcl");
            }

            var markerShader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");
            Mesh? sphereMesh = null;
            var matCache = new Dictionary<string, Material>();
            var bucket = new GameObject("Objects (.tcl markers)");
            bucket.transform.SetParent(root.transform, false);

            //.3db models are loaded from the original data, not from the Unity project.
            var dataRoot = Diggles.Runtime.DigglesConfig.ConfiguredDataRoot;

            var byClass = new Dictionary<string, int>();
            foreach (var o in objects)
            {
                var go = new GameObject($"{o.ClassName}_L{o.LineNumber}");
                go.transform.SetParent(bucket.transform, false);
                go.transform.localPosition = MakeObjectPosition(
                    o.Position, tileSize, tileHeight, orientation);
                go.transform.localRotation = Quaternion.Euler(
                    o.Rotation.x * Mathf.Rad2Deg,
                    o.Rotation.z * Mathf.Rad2Deg,
                    -o.Rotation.y * Mathf.Rad2Deg);

                var resolved = MarkerModelResolver.Resolve(o.ClassName, dataRoot, ctx);
                if (resolved?.Mesh != null)
                {
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = resolved.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    if (resolved.Materials != null && resolved.Materials.Length > 0)
                        mr.sharedMaterials = resolved.Materials;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    mr.receiveShadows = true;
                }
                else if (!(o.ClassName.StartsWith("Trigger_") ||
                           o.ClassName.StartsWith("Sequence") ||
                           o.ClassName.StartsWith("Info_") ||
                           o.ClassName.StartsWith("Fake_")))
                {
                    if (sphereMesh == null)
                    {
                        sphereMesh = CreateMarkerSphereMesh();
                        ctx.AddObjectToAsset("MarkerSphere", sphereMesh);
                    }
                    go.transform.localScale = Vector3.one * markerRadius;
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = sphereMesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = GetOrCreateMarkerMaterial(
                        o.ClassName, matCache, markerShader, ctx);
                }

                byClass[o.ClassName] = byClass.GetValueOrDefault(o.ClassName) + 1;
            }

            var topClasses = byClass
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => $"{kv.Value}x{kv.Key}")
                .ToArray();
            var summary = $"{byClass.Count} classes; top: {string.Join(", ", topClasses)}";
            return (objects.Count, summary);
        }

        private static Vector3 MakeObjectPosition(
            Vector3 tclPos, float tileSize, float tileHeight, Orientation orientation)
        {
            var x = tclPos.x * tileSize;
            var y = -tclPos.y * tileSize;
            var height = tclPos.z * tileHeight;
            return orientation == Orientation.Floor
                ? new Vector3(x, height, y)
                : new Vector3(x, y, height);
        }

        private static Material GetOrCreateMarkerMaterial(
            string className, Dictionary<string, Material> cache,
            Shader shader, AssetImportContext ctx)
        {
            if (cache.TryGetValue(className, out var existing)) return existing;
            unchecked
            {
                var hashSeed = 0u;
                foreach (var ch in className) hashSeed = hashSeed * 31u + ch;
                var h = hashSeed * 2654435761u;
                var r = ((h >> 16) & 0xFFu) / 255f;
                var g = ((h >> 8) & 0xFFu) / 255f;
                var b = (h & 0xFFu) / 255f;
                var color = new Color(
                    r * 0.6f + 0.3f,
                    g * 0.6f + 0.3f,
                    b * 0.6f + 0.3f);
                var mat = new Material(shader)
                {
                    name = $"marker_{className}",
                    color = color,
                };
                cache[className] = mat;
                ctx.AddObjectToAsset($"MarkerMat_{className}", mat);
                return mat;
            }
        }

        private static Mesh CreateMarkerSphereMesh()
        {
            var mesh = new Mesh { name = "ObjectMarkerOctahedron" };
            var verts = new[]
            {
                new Vector3( 1,  0,  0),
                new Vector3(-1,  0,  0),
                new Vector3( 0,  1,  0),
                new Vector3( 0, -1,  0),
                new Vector3( 0,  0,  1),
                new Vector3( 0,  0, -1),
            };
            var tris = new[]
            {
                0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4,
                2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5,
            };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private const int WorldBufferTiles = 25;
        private const byte PmpDefaultTerrainShape = 4;

        private string BuildOberweltSideView(
            GameObject root, L2mMap map, PmpMap? pmp, AssetImportContext ctx,
            float tileSize, float tileHeight, Orientation orientation)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");

            var w = (int)map.Width;
            var h = (int)map.Height;
            var usePmp = pmp != null && pmp.Width == map.Width && pmp.Height == map.Height;

            float TileBaseZ(int lx, int ly)
            {
                if (lx < 0 || lx >= w || ly < 0 || ly >= h)
                    return PmpDefaultTerrainShape * tileHeight;
                var v = usePmp ? pmp!.HeightAt(lx, ly) : PmpDefaultTerrainShape;
                return v * tileHeight;
            }

            float TileColorBit(int lx, int ly)
            {
                if (lx < 0 || lx >= w || ly < 0 || ly >= h) return 0f;
                if (!usePmp) return 0f;
                return (pmp!.TerrainAt(lx, ly) & 0x80) != 0 ? 1f : 0f;
            }

            float CornerZ(int cx, int cy)
            {
                var sum = 0f;
                var count = 0;
                for (var dy = -1; dy <= 0; dy++)
                {
                    for (var dx = -1; dx <= 0; dx++)
                    {
                        sum += TileBaseZ(cx + dx, cy + dy);
                        count++;
                    }
                }
                return sum / count;
            }

            var x0Ext = -WorldBufferTiles;
            var x1Ext = w + WorldBufferTiles;
            var y0Ext = -WorldBufferTiles;
            var y1Ext = h + WorldBufferTiles;

            var grassVerts = new List<Vector3>();
            var grassTris = new List<int>();
            var stoneVerts = new List<Vector3>();
            var stoneTris = new List<int>();
            var bufferVerts = new List<Vector3>();
            var bufferTris = new List<int>();
            var earthVerts = new List<Vector3>();
            var earthTris = new List<int>();

            Vector3 V(float x, float y, float z) => orientation == Orientation.Floor
                ? new Vector3(x, z, y)
                : new Vector3(x, y, z);

            void EmitQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                          List<Vector3> verts, List<int> tris)
            {
                var i = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i + 2); tris.Add(i + 1); tris.Add(i + 3);
            }

            var l2mTileCount = 0;
            for (var ly = y0Ext; ly < y1Ext; ly++)
            {
                for (var lx = x0Ext; lx < x1Ext; lx++)
                {
                    var x0 = lx * tileSize;
                    var x1 = x0 + tileSize;
                    var yA = -(ly * tileSize);
                    var yB = yA - tileSize;
                    var zNW = CornerZ(lx,     ly);
                    var zNE = CornerZ(lx + 1, ly);
                    var zSW = CornerZ(lx,     ly + 1);
                    var zSE = CornerZ(lx + 1, ly + 1);

                    var isInL2m = lx >= 0 && lx < w && ly >= 0 && ly < h;
                    List<Vector3> verts;
                    List<int> tris;
                    if (!isInL2m)
                    {
                        verts = bufferVerts; tris = bufferTris;
                    }
                    else
                    {
                        l2mTileCount++;
                        var isGrass = TileColorBit(lx, ly) > 0f;
                        verts = isGrass ? grassVerts : stoneVerts;
                        tris = isGrass ? grassTris : stoneTris;
                    }

                    EmitQuad(
                        V(x0, yA, zNW), V(x1, yA, zNE),
                        V(x0, yB, zSW), V(x1, yB, zSE),
                        verts, tris);
                }
            }

            var floorZ = -2f * tileHeight;
            var xWest = x0Ext * tileSize;
            var xEast = x1Ext * tileSize;
            var yNorthEdge = -y0Ext * tileSize;
            var ySouthEdge = -y1Ext * tileSize;
            EmitQuad(
                V(xWest, yNorthEdge, floorZ), V(xEast, yNorthEdge, floorZ),
                V(xWest, ySouthEdge, floorZ), V(xEast, ySouthEdge, floorZ),
                earthVerts, earthTris);

            BuildSubmesh(root, ctx, shader, "Oberwelt Grass (terrain_type & 0x80)",
                new Color(0.42f, 0.62f, 0.25f),
                grassVerts, grassTris, null, "GrassMesh", "GrassMat");
            BuildSubmesh(root, ctx, shader, "L2M Stone Surface",
                new Color(0.55f, 0.50f, 0.45f),
                stoneVerts, stoneTris, null, "StoneMesh", "StoneMat");
            BuildSubmesh(root, ctx, shader, "Surrounding World (default plateau)",
                new Color(0.35f, 0.42f, 0.20f),
                bufferVerts, bufferTris, null, "BufferMesh", "BufferMat");
            BuildSubmesh(root, ctx, shader, "World Floor (visual base)",
                new Color(0.20f, 0.16f, 0.13f),
                earthVerts, earthTris, null, "EarthMesh", "EarthMat");

            var pmpNote = usePmp
                ? $"pmp h-range {pmp!.HeightMap.Min()}..{pmp.HeightMap.Max()}"
                : "no PMP (flat at h=4)";
            return $"L2M={l2mTileCount} tiles + buffer {WorldBufferTiles} on each side; " +
                $"per-tile Z from PMP terminal byte ({pmpNote})";
        }

        private static void EmitFlatTile(
            List<Vector3> verts, List<int> tris,
            float x0, float x1, float y0, float y1, float z,
            Orientation orientation)
        {
            Vector3 V(float x, float y) => orientation == Orientation.Floor
                ? new Vector3(x, z, y)
                : new Vector3(x, y, z);
            var i0 = verts.Count;
            verts.Add(V(x0, y0));
            verts.Add(V(x1, y0));
            verts.Add(V(x0, y1));
            verts.Add(V(x1, y1));
            tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            tris.Add(i0 + 2); tris.Add(i0 + 1); tris.Add(i0 + 3);
        }

        private static void EmitBlock(
            List<Vector3> verts, List<int> tris,
            float x0, float x1, float y0, float y1, float z0, float z1,
            Orientation orientation)
        {
            for (var dir = 0; dir < 6; dir++)
            {
                EmitFace(dir, x0, x1, y0, y1, z0, z1, orientation, verts, tris);
            }
        }

        private string BuildSideViewBlocks(
            GameObject root, L2mMap map, PmpMap? pmp, AssetImportContext ctx,
            float tileSize, float tileHeight, Orientation orientation)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");

            var w = (int)map.Width;
            var h = (int)map.Height;
            var useHeight = pmp != null && pmp.Width == map.Width && pmp.Height == map.Height;

            var bucketVerts = new Dictionary<int, List<Vector3>>();
            var bucketTris = new Dictionary<int, List<int>>();
            for (var bt = 0; bt < 8; bt++)
            {
                bucketVerts[bt] = new List<Vector3>();
                bucketTris[bt] = new List<int>();
            }

            var solidCount = 0;
            for (var ty = 0; ty < h; ty++)
            {
                for (var tx = 0; tx < w; tx++)
                {
                    var cell = map.CellAt(tx, ty);
                    if (cell == 0) continue;
                    solidCount++;

                    var bt = L2mMap.BlockType(cell);
                    var depth = useHeight ? pmp!.HeightAt(tx, ty) : (byte)0;

                    var x0 = tx * tileSize;
                    var x1 = x0 + tileSize;
                    var screenY0 = -(ty * tileSize);
                    var screenY1 = screenY0 - tileSize;
                    var z0 = depth * tileHeight;
                    var z1 = z0 + tileHeight;

                    var verts = bucketVerts[bt];
                    var tris = bucketTris[bt];
                    for (var dir = 0; dir < 6; dir++)
                    {
                        EmitFace(dir, x0, x1, screenY0, screenY1, z0, z1,
                            orientation, verts, tris);
                    }
                }
            }

            for (var bt = 0; bt < 8; bt++)
            {
                BuildSubmesh(root, ctx, shader, $"Solid BlockType {bt}",
                    BlockTypeColor(bt),
                    bucketVerts[bt], bucketTris[bt], null,
                    $"SolidBT{bt}Mesh", $"SolidBT{bt}Mat");
            }

            return $"{solidCount}/{w * h} solid cells ({100.0 * solidCount / (w * h):F1}%), " +
                $"{w * h - solidCount} empty (air/plain)";
        }

        private string BuildStoneVoxels(
            GameObject root, L2mMap map, PmpMap? pmp, AssetImportContext ctx,
            float tileSize, float tileHeight, Orientation orientation)
        {
            if (pmp == null || pmp.Width != map.Width || pmp.Height != map.Height)
            {
                return "no PMP — voxel mode requires PMP";
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");

            var w = (int)map.Width;
            var h = (int)map.Height;
            var stoneVerts = new List<Vector3>();
            var stoneTris = new List<int>();
            var airVerts = new List<Vector3>();
            var airTris = new List<int>();

            byte HeightOf(int x, int y)
            {
                if (x < 0 || x >= w || y < 0 || y >= h) return 0;
                return pmp!.HeightAt(x, y);
            }

            for (var ty = 0; ty < h; ty++)
            {
                for (var tx = 0; tx < w; tx++)
                {
                    var hHere = HeightOf(tx, ty);
                    if (hHere == 0) continue;

                    var x0 = tx * tileSize;
                    var x1 = x0 + tileSize;
                    var y0 = -(ty * tileSize);
                    var y1 = y0 - tileSize;
                    var z0 = 0f;
                    var z1 = hHere * tileHeight;

                    var nbrW = HeightOf(tx - 1, ty);
                    var nbrE = HeightOf(tx + 1, ty);
                    var nbrN = HeightOf(tx, ty - 1);
                    var nbrS = HeightOf(tx, ty + 1);

                    EmitFace(5, x0, x1, y0, y1, z0, z1, orientation, stoneVerts, stoneTris);
                    EmitFace(4, x0, x1, y0, y1, z0, z1, orientation, stoneVerts, stoneTris);
                    if (nbrW < hHere) EmitColumnFace(0, x0, x1, y0, y1, nbrW * tileHeight, z1,
                        orientation, stoneVerts, stoneTris);
                    if (nbrE < hHere) EmitColumnFace(1, x0, x1, y0, y1, nbrE * tileHeight, z1,
                        orientation, stoneVerts, stoneTris);
                    if (nbrN < hHere) EmitColumnFace(2, x0, x1, y0, y1, nbrN * tileHeight, z1,
                        orientation, stoneVerts, stoneTris);
                    if (nbrS < hHere) EmitColumnFace(3, x0, x1, y0, y1, nbrS * tileHeight, z1,
                        orientation, stoneVerts, stoneTris);
                }
            }

            const int Side = 8;
            var subStep = tileSize / Side;
            var airCount = 0;
            for (var ty = 0; ty < h; ty++)
            {
                for (var tx = 0; tx < w; tx++)
                {
                    for (var z = 0; z < Side; z++)
                    {
                        for (var sx = 0; sx < Side; sx++)
                        {
                            if (pmp.SolidityAt(tx, ty, sx, z) == 0) continue;
                            airCount++;
                            EmitFullCube(tx, ty, sx, z, tileSize, subStep, tileHeight,
                                orientation, airVerts, airTris);
                        }
                    }
                }
            }

            BuildSubmesh(root, ctx, shader, "Terrain (stone columns)",
                new Color(0.45f, 0.42f, 0.38f),
                stoneVerts, stoneTris, null, "StoneMesh", "StoneMat");
            BuildSubmesh(root, ctx, shader, "Air voxels (carved)",
                new Color(0.85f, 0.55f, 0.25f),
                airVerts, airTris, null, "AirMesh", "AirMat");

            return $"{w * h} tiles, stone-faces={stoneTris.Count / 3}, " +
                $"air-voxels={airCount} ({airTris.Count / 3} faces)";
        }

        private static void EmitColumnFace(
            int dir, float x0, float x1, float y0, float y1, float zBottom, float zTop,
            Orientation orientation, List<Vector3> verts, List<int> tris)
        {
            EmitFace(dir, x0, x1, y0, y1, zBottom, zTop, orientation, verts, tris);
        }

        private static void EmitFullCube(
            int tx, int ty, int sx, int z,
            float tileSize, float subStep, float heightStep,
            Orientation orientation, List<Vector3> verts, List<int> tris)
        {
            var x0 = tx * tileSize + sx * subStep;
            var x1 = x0 + subStep;
            var y0 = -(ty * tileSize);
            var y1 = y0 - tileSize;
            var z0 = z * heightStep;
            var z1 = z0 + heightStep;
            for (var dir = 0; dir < 6; dir++)
            {
                EmitFace(dir, x0, x1, y0, y1, z0, z1, orientation, verts, tris);
            }
        }

        private static void EmitFace(
            int dir, float x0, float x1, float y0, float y1, float z0, float z1,
            Orientation orientation, List<Vector3> verts, List<int> tris)
        {
            Vector3 V(float x, float y, float z) => orientation == Orientation.Floor
                ? new Vector3(x, z, y)
                : new Vector3(x, y, z);

            Vector3 a, b, c, d;
            switch (dir)
            {
                case 0:
                    a = V(x0, y0, z0); b = V(x0, y1, z0); c = V(x0, y0, z1); d = V(x0, y1, z1);
                    break;
                case 1:
                    a = V(x1, y0, z0); b = V(x1, y0, z1); c = V(x1, y1, z0); d = V(x1, y1, z1);
                    break;
                case 2:
                    a = V(x0, y0, z0); b = V(x0, y0, z1); c = V(x1, y0, z0); d = V(x1, y0, z1);
                    break;
                case 3:
                    a = V(x0, y1, z0); b = V(x1, y1, z0); c = V(x0, y1, z1); d = V(x1, y1, z1);
                    break;
                case 4:
                    a = V(x0, y0, z0); b = V(x1, y0, z0); c = V(x0, y1, z0); d = V(x1, y1, z0);
                    break;
                default:
                    a = V(x0, y0, z1); b = V(x0, y1, z1); c = V(x1, y0, z1); d = V(x1, y1, z1);
                    break;
            }

            var i0 = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            tris.Add(i0 + 0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            tris.Add(i0 + 2); tris.Add(i0 + 1); tris.Add(i0 + 3);
        }

        private string BuildHeightfieldByteMap(
            GameObject root, L2mMap map, PmpMap? pmp, AssetImportContext ctx,
            float tileSize, float tileHeight, Orientation orientation)
        {
            if (pmp == null || pmp.Width != map.Width || pmp.Height != map.Height)
            {
                return "no PMP — heightfield needs PMP";
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");

            var w = (int)map.Width;
            var h = (int)map.Height;
            var verts = new List<Vector3>((w + 1) * (h + 1));
            var tris = new List<int>(w * h * 6);

            for (var y = 0; y <= h; y++)
            {
                for (var x = 0; x <= w; x++)
                {
                    var cx = System.Math.Min(x, w - 1);
                    var cy = System.Math.Min(y, h - 1);
                    var hv = pmp.HeightAt(cx, cy);
                    verts.Add(MakeVertex(
                        x * tileSize, -(y * tileSize), hv * tileHeight, orientation));
                }
            }
            var stride = w + 1;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i0 = y * stride + x;
                    var i1 = i0 + 1;
                    var i2 = i0 + stride;
                    var i3 = i2 + 1;
                    tris.Add(i0); tris.Add(i1); tris.Add(i2);
                    tris.Add(i2); tris.Add(i1); tris.Add(i3);
                }
            }
            BuildSubmesh(root, ctx, shader, "Heightfield (per-tile byte)",
                new Color(0.55f, 0.50f, 0.42f),
                verts, tris, null, "HeightByteMesh", "HeightByteMat");

            var minH = pmp.HeightMap.Min();
            var maxH = pmp.HeightMap.Max();
            return $"{w}x{h} tiles, ({w + 1}x{h + 1})={verts.Count} verts, " +
                $"per-tile height={minH}..{maxH}";
        }

        private string BuildHeightField8x8(
            GameObject root, L2mMap map, PmpMap? pmp, AssetImportContext ctx,
            float tileSize, float tileHeight, Orientation orientation)
        {
            if (pmp == null || pmp.Width != map.Width || pmp.Height != map.Height)
            {
                return BuildCornerQuads(
                    root, map, pmp, ctx, tileSize, tileHeight, orientation) +
                    " (fallback: no PMP, used CornerQuads)";
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");

            var w = (int)map.Width;
            var h = (int)map.Height;
            const int Side = 8;
            var subStep = tileSize / Side;

            var verts = new List<Vector3>(w * h * Side * Side);
            var tris = new List<int>(w * h * (Side - 1) * (Side - 1) * 6);
            var uvs = new List<Vector2>(w * h * Side * Side);
            var distinctHeights = new HashSet<byte>();

            for (var ty = 0; ty < h; ty++)
            {
                for (var tx = 0; tx < w; tx++)
                {
                    var tileStartIdx = verts.Count;
                    for (var sy = 0; sy < Side; sy++)
                    {
                        for (var sx = 0; sx < Side; sx++)
                        {
                            var hv = pmp.HeightAt(tx, ty);
                            distinctHeights.Add(hv);
                            var worldX = tx * tileSize + sx * subStep;
                            var worldY = -(ty * tileSize + sy * subStep);
                            var worldH = hv * tileHeight;
                            verts.Add(MakeVertex(worldX, worldY, worldH, orientation));
                            uvs.Add(new Vector2(sx / (float)(Side - 1), sy / (float)(Side - 1)));
                        }
                    }
                    for (var sy = 0; sy < Side - 1; sy++)
                    {
                        for (var sx = 0; sx < Side - 1; sx++)
                        {
                            var i0 = tileStartIdx + sy * Side + sx;
                            var i1 = i0 + 1;
                            var i2 = i0 + Side;
                            var i3 = i2 + 1;
                            tris.Add(i0); tris.Add(i1); tris.Add(i2);
                            tris.Add(i2); tris.Add(i1); tris.Add(i3);
                        }
                    }
                }
            }

            BuildSubmesh(root, ctx, shader, "HeightField 8x8",
                new Color(0.55f, 0.50f, 0.42f),
                verts, tris, uvs, "TerrainHF8Mesh", "TerrainHF8Mat");

            return $"{w * h} tiles x 64 sub-verts = {verts.Count} verts, " +
                $"{tris.Count / 3} tris, distinct heights={distinctHeights.Count}";
        }

        private string BuildCornerQuads(
            GameObject root, L2mMap map, PmpMap? pmp, AssetImportContext ctx,
            float tileSize, float tileHeight, Orientation orientation)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");

            var w = (int)map.Width;
            var h = (int)map.Height;
            var useHeights = pmp != null && pmp.Width == map.Width && pmp.Height == map.Height;

            byte BaseAt(int x, int y) => useHeights ? pmp!.HeightAt(x, y) : (byte)0;

            const int EmptyBucket = -1;
            var matVerts = new Dictionary<int, List<Vector3>>();
            var matTris = new Dictionary<int, List<int>>();
            var matUvs = new Dictionary<int, List<Vector2>>();
            for (var bt = -1; bt < 8; bt++)
            {
                matVerts[bt] = new List<Vector3>();
                matTris[bt] = new List<int>();
                matUvs[bt] = new List<Vector2>();
            }
            var nonEmpty = 0;

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var cell = map.CellAt(x, y);
                    var (nw, ne, sw, se) = cell == 0
                        ? (0, 0, 0, 0)
                        : L2mMap.DecodeCorners(cell);
                    if (cell != 0) nonEmpty++;
                    var bucket = cell == 0 ? EmptyBucket : L2mMap.BlockType(cell);

                    var b = BaseAt(x, y);
                    var hNw = (b + nw) * tileHeight;
                    var hNe = (b + ne) * tileHeight;
                    var hSw = (b + sw) * tileHeight;
                    var hSe = (b + se) * tileHeight;

                    var x0 = x * tileSize;
                    var x1 = (x + 1) * tileSize;
                    var y0 = -y * tileSize;
                    var y1 = -(y + 1) * tileSize;

                    var vNw = MakeVertex(x0, y0, hNw, orientation);
                    var vNe = MakeVertex(x1, y0, hNe, orientation);
                    var vSw = MakeVertex(x0, y1, hSw, orientation);
                    var vSe = MakeVertex(x1, y1, hSe, orientation);

                    var verts = matVerts[bucket];
                    var tris = matTris[bucket];
                    var uvs = matUvs[bucket];

                    var i0 = verts.Count;
                    verts.Add(vNw); verts.Add(vNe); verts.Add(vSw); verts.Add(vSe);
                    uvs.Add(new Vector2(0, 1));
                    uvs.Add(new Vector2(1, 1));
                    uvs.Add(new Vector2(0, 0));
                    uvs.Add(new Vector2(1, 0));

                    tris.Add(i0 + 0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                    tris.Add(i0 + 2); tris.Add(i0 + 1); tris.Add(i0 + 3);
                }
            }

            BuildSubmesh(root, ctx, shader, "Ground (empty)",
                new Color(0.45f, 0.55f, 0.32f),
                matVerts[EmptyBucket], matTris[EmptyBucket], matUvs[EmptyBucket],
                "GroundMesh", "GroundMat");
            for (var bt = 0; bt < 8; bt++)
            {
                BuildSubmesh(root, ctx, shader, $"BlockType {bt}",
                    BlockTypeColor(bt),
                    matVerts[bt], matTris[bt], matUvs[bt],
                    $"BlockType{bt}Mesh", $"BlockType{bt}Mat");
            }

            var totalVerts = matVerts.Values.Sum(v => v.Count);
            var totalTris = matTris.Values.Sum(t => t.Count) / 3;
            return $"{nonEmpty}/{w * h} non-empty cells, {totalVerts} verts, {totalTris} tris";
        }

        private static Color BlockTypeColor(int bt) => bt switch
        {
            0 => new Color(0.78f, 0.65f, 0.45f),
            1 => new Color(0.70f, 0.40f, 0.30f),
            2 => new Color(0.50f, 0.70f, 0.85f),
            3 => new Color(0.85f, 0.85f, 0.45f),
            4 => new Color(0.80f, 0.40f, 0.80f),
            5 => new Color(0.50f, 0.85f, 0.55f),
            6 => new Color(0.45f, 0.45f, 0.45f),
            7 => new Color(0.95f, 0.55f, 0.20f),
            _ => Color.magenta,
        };

        private static void BuildSubmesh(
            GameObject parent, AssetImportContext ctx, Shader shader,
            string childName, Color color,
            List<Vector3> verts, List<int> tris, List<Vector2>? uvs,
            string meshKey, string matKey)
        {
            if (verts.Count == 0) return;

            var go = new GameObject(childName);
            go.transform.SetParent(parent.transform, false);

            var mesh = new Mesh
            {
                name = meshKey,
                indexFormat = verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            if (uvs != null && uvs.Count == verts.Count)
            {
                mesh.SetUVs(0, uvs);
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            ctx.AddObjectToAsset(meshKey, mesh);

            var mat = new Material(shader) { name = matKey };
            SetMaterialColor(mat, color);
            ctx.AddObjectToAsset(matKey, mat);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }

        private static void SetMaterialColor(Material mat, Color color)
        {
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;
        }

        private static Vector3 MakeVertex(float x, float y, float height, Orientation o)
            => o == Orientation.Floor
                ? new Vector3(x, height, y)
                : new Vector3(x, y, height);

        private string BuildPlaceholderCubesSummary(
            GameObject root, L2mMap map, PmpMap? pmp, AssetImportContext ctx,
            float tileSize, float tileHeight,
            bool stackColumns, Orientation orientation, bool renderEmptyCells)
        {
            var cubeMesh = CreatePlaceholderCubeMesh();
            ctx.AddObjectToAsset("CubeMesh", cubeMesh);
            var matCache = new Dictionary<int, Material>();
            var (placed, columnTotal) = BuildPlaceholderColumns(
                root, map, pmp, cubeMesh, matCache, ctx,
                tileSize, tileHeight, stackColumns,
                orientation, renderEmptyCells);
            foreach (var kv in matCache)
            {
                ctx.AddObjectToAsset($"Material_id_{kv.Key}", kv.Value);
            }
            return $"{placed} surface tiles ({columnTotal} cubes total)";
        }

        private static (int surface, int total) BuildPlaceholderColumns(
            GameObject root,
            L2mMap map,
            PmpMap? pmp,
            Mesh sharedMesh,
            Dictionary<int, Material> materialCache,
            AssetImportContext ctx,
            float tileSize,
            float tileHeight,
            bool stackColumns,
            Orientation orientation,
            bool renderEmptyCells)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? throw new System.InvalidOperationException("No usable shader");

            var surface = 0;
            var total = 0;
            var useHeights = pmp != null
                && pmp.Width == map.Width
                && pmp.Height == map.Height;

            for (var y = 0; y < map.Height; y++)
            {
                for (var x = 0; x < map.Width; x++)
                {
                    var cellId = map.CellAt(x, y);
                    if (cellId == 0 && !renderEmptyCells) continue;
                    surface++;

                    var h = useHeights ? pmp!.HeightAt(x, y) : (byte)0;
                    var topLevel = (int)h;
                    var bottomLevel = stackColumns ? 0 : topLevel;

                    for (var level = bottomLevel; level <= topLevel; level++)
                    {
                        var cellGo = new GameObject(
                            $"c_{x}_{y}_h{level}_id{cellId}");
                        cellGo.transform.SetParent(root.transform, false);
                        cellGo.transform.localPosition = ComputePosition(
                            x, y, level, tileSize, tileHeight, orientation);
                        cellGo.transform.localScale = ComputeScale(
                            tileSize, tileHeight, orientation);

                        var mf = cellGo.AddComponent<MeshFilter>();
                        mf.sharedMesh = sharedMesh;
                        var mr = cellGo.AddComponent<MeshRenderer>();

                        var isTop = level == topLevel;
                        var isEmpty = cellId == 0;
                        var matKey = isEmpty ? -2 : (isTop ? cellId : -1);
                        var mat = GetOrCreateMaterial(
                            matKey, materialCache, shader, isTop, isEmpty);
                        mr.sharedMaterial = mat;
                        total++;
                    }
                }
            }
            return (surface, total);
        }

        private static Vector3 ComputePosition(
            int x, int y, int level, float tileSize, float tileHeight,
            Orientation orientation) => orientation switch
        {
            Orientation.Floor => new Vector3(
                x * tileSize, level * tileHeight, -y * tileSize),
            Orientation.Wall => new Vector3(
                x * tileSize, -y * tileSize, level * tileHeight),
            _ => throw new System.ArgumentOutOfRangeException(nameof(orientation)),
        };

        private static Vector3 ComputeScale(
            float tileSize, float tileHeight, Orientation orientation) => orientation switch
        {
            Orientation.Floor => new Vector3(tileSize, tileHeight, tileSize),
            Orientation.Wall => new Vector3(tileSize, tileSize, tileHeight),
            _ => throw new System.ArgumentOutOfRangeException(nameof(orientation)),
        };

        private static Material GetOrCreateMaterial(
            int key, Dictionary<int, Material> cache, Shader shader,
            bool isTop, bool isEmpty)
        {
            if (cache.TryGetValue(key, out var existing)) return existing;
            Color color;
            string name;
            if (isEmpty)
            {
                color = new Color(0.85f, 0.85f, 0.85f, 0.3f);
                name = "empty_cell";
            }
            else if (isTop)
            {
                color = ColorFromId((ushort)key);
                name = $"tile_id_{key}";
            }
            else
            {
                color = new Color(0.35f, 0.28f, 0.22f);
                name = "subsurface_fill";
            }
            var mat = new Material(shader)
            {
                name = name,
                color = color,
            };
            cache[key] = mat;
            return mat;
        }

        private static Color ColorFromId(ushort id)
        {
            unchecked
            {
                var h = (id * 2654435761u) & 0x00FFFFFFu;
                var r = ((h >> 16) & 0xFFu) / 255f;
                var g = ((h >> 8) & 0xFFu) / 255f;
                var b = (h & 0xFFu) / 255f;
                return new Color(r * 0.7f + 0.2f, g * 0.7f + 0.2f, b * 0.7f + 0.2f);
            }
        }

        private static Mesh CreatePlaceholderCubeMesh()
        {
            var mesh = new Mesh { name = "PlaceholderTileCube" };
            var verts = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(1, 0, 1), new Vector3(0, 0, 1),
                new Vector3(0, 1, 0), new Vector3(1, 1, 0),
                new Vector3(1, 1, 1), new Vector3(0, 1, 1),
            };
            var tris = new[]
            {
                4, 6, 5, 4, 7, 6,
                0, 1, 2, 0, 2, 3,
                0, 5, 1, 0, 4, 5,
                3, 2, 6, 3, 6, 7,
                0, 7, 4, 0, 3, 7,
                1, 6, 2, 1, 5, 6,
            };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static string? FindMatchingBdb(L2mMap map, string dataRoot)
        {
            var bdbFiles = new[] { "BlockDB0.BDB", "BlockDB1.BDB", "BlockDB2.BDB" }
                .Select(n => Path.Combine(dataRoot, n))
                .Where(File.Exists)
                .ToArray();

            if (bdbFiles.Length == 0) return null;

            var maxCellId = map.Cells.Where(c => c != 0).DefaultIfEmpty((ushort)0).Max();
            if (maxCellId == 0) return null;

            foreach (var path in bdbFiles)
            {
                try
                {
                    var bdb = BdbParser.Parse(path);
                    if (maxCellId < bdb.Header.NDescriptors)
                    {
                        return path;
                    }
                }
                catch
                {
                    continue;
                }
            }
            return null;
        }

        private static PmpMap? TryLoadPairedPmp(
            string l2mAssetPath, L2mMap map, string dataRoot, out string source)
        {
            source = "";

            var pmpFromTcl = FindPmpFromSiblingTcl(l2mAssetPath, dataRoot);
            if (pmpFromTcl != null && TryParse(pmpFromTcl, out var byTcl))
            {
                source = $"tcl→{Path.GetFileName(pmpFromTcl)}";
                return byTcl;
            }

            var sibling = Path.ChangeExtension(l2mAssetPath, ".pmp");
            if (sibling != null && File.Exists(sibling) && TryParse(sibling, out var bySibling))
            {
                source = $"sibling→{Path.GetFileName(sibling)}";
                return bySibling;
            }

            var byDims = FindPmpByMatchingDimensions(map, dataRoot);
            if (byDims != null)
            {
                source = $"dims→{Path.GetFileName(byDims.Path)}";
                return byDims.Map;
            }

            return null;
        }

        private static bool TryParse(string path, out PmpMap pmp)
        {
            try
            {
                pmp = PmpParser.Parse(path);
                return true;
            }
            catch
            {
                pmp = null!;
                return false;
            }
        }

        private static readonly Regex MapTemplateRegex = new(
            @"map_template\s+(\S+\.pmp)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string? FindPmpFromSiblingTcl(string l2mAssetPath, string dataRoot)
        {
            var basenameNoExt = Path.GetFileNameWithoutExtension(l2mAssetPath);
            var candidateTcls = new[]
            {
                Path.ChangeExtension(l2mAssetPath, ".tcl"),
                Path.Combine(dataRoot, "Templates", basenameNoExt + ".tcl"),
            };

            foreach (var tcl in candidateTcls)
            {
                if (tcl == null || !File.Exists(tcl)) continue;
                var text = File.ReadAllText(tcl);
                var m = MapTemplateRegex.Match(text);
                if (!m.Success) continue;
                var rel = m.Groups[1].Value.Replace('\\', '/');
                var idx = rel.IndexOf("data/", System.StringComparison.OrdinalIgnoreCase);
                var tail = idx >= 0 ? rel.Substring(idx + "data/".Length) : rel;
                var absolute = Path.Combine(dataRoot, tail);
                if (File.Exists(absolute)) return absolute;
            }
            return null;
        }

        private sealed class PmpHit
        {
            public string Path { get; init; } = "";
            public PmpMap Map { get; init; } = new();
        }

        private static PmpHit? FindPmpByMatchingDimensions(L2mMap map, string dataRoot)
        {
            var templates = Path.Combine(dataRoot, "Templates");
            if (!Directory.Exists(templates)) return null;
            foreach (var path in Directory.EnumerateFiles(templates, "*.pmp"))
            {
                try
                {
                    var pmp = PmpParser.Parse(path);
                    if (pmp.Width == map.Width && pmp.Height == map.Height)
                    {
                        return new PmpHit { Path = path, Map = pmp };
                    }
                }
                catch
                {
                    continue;
                }
            }
            return null;
        }
    }
}
