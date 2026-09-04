#nullable enable

namespace Diggles.Editor.Diagnostics
{
    using Diggles.Runtime.World;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using Diggles.Editor.Importers;
    using Diggles.Runtime;
    using UnityEditor;
    using UnityEngine;

    // Editor window for debugging spawn / world orientation issues. Spawns
    // isolated test entities along the X axis, plus terrain tests (single
    // blocks, multi-block chunks, full PMP template) to pinpoint where axis
    // conventions diverge between models and world.
    public sealed class DigglesSpawnDiagnostics : EditorWindow
    {
        // ----- Config -----
        [SerializeField] string dataRoot = "";
        [SerializeField] string testBdbPath = "";
        [SerializeField] string testPmpPath = "";
        [SerializeField] string testDwarfClass = "Zwerg";
        [SerializeField] string testFrauClass = "Zwerg";   // Frau uses same class as Zwerg
        [SerializeField] string testHamsterClass = "Hamster";
        [SerializeField] string testTrollClass = "Troll";
        [SerializeField] string testDracheClass = "Drache";
        [SerializeField] string testStaticClass = "Pilz";  // mushroom (no Dummy_ prefix)
        [SerializeField] int testBlockIdx = 0;  // 0 = default solid earth
        [SerializeField] int chunkSize = 4;

        // ----- Per-entity observation -----
        [System.Serializable]
        sealed class EntityObservation
        {
            public string Name = "";
            public bool Spawned;
            public string Orientation = "";
            public string FacingOrAxis = "";
            public string RotateY = "";
            public string Notes = "";
            // Auto-filled by spawn:
            public string AutoInfo = "";
        }

        readonly List<EntityObservation> all = new();

        EntityObservation Make(string name)
        {
            var o = new EntityObservation { Name = name };
            all.Add(o);
            return o;
        }

        // ----- entities -----
        EntityObservation? axes, rawMesh, dwarf, frau, hamster, troll, drache;
        EntityObservation? staticMarker;
        EntityObservation? terrainBlock, terrainBlock2, terrainBlock3;
        EntityObservation? caveBlock, mountainBlock;
        EntityObservation? chunk4x4, chunk8x8;
        EntityObservation? fullPmp;

        Vector3 nextSpawnPos = Vector3.zero;
        const float SpawnSpacing = 6f;

        Vector2 scroll;
        string report = "";

        [MenuItem("Diggles/Spawn Diagnostics")]
        public static void Open()
        {
            var w = GetWindow<DigglesSpawnDiagnostics>("Diggles Diagnostics");
            w.minSize = new Vector2(560, 700);
            w.InitEntities();
        }

        void InitEntities()
        {
            all.Clear();
            axes          = Make("A. Reference axes (red=X green=Y blue=Z)");
            rawMesh       = Make("B. Raw 3DB mesh (mann frame 0, NO axis conv, NO rotation)");
            dwarf         = Make("C1. Animated Zwerg (mann.3db)");
            frau          = Make("C2. Animated Frau (frau.3db)");
            hamster       = Make("C3. Animated Hamster (hamster.3db)");
            troll         = Make("C4. Animated Troll (troll.3db)");
            drache        = Make("C5. Animated Drache (drache.3db)");
            staticMarker  = Make("D. Static marker via Resolve");
            terrainBlock  = Make("E1. Terrain — single block (default idx)");
            terrainBlock2 = Make("E2. Terrain — block idx 200 (other)");
            terrainBlock3 = Make("E3. Terrain — block idx 500 (other)");
            caveBlock     = Make("E4. Terrain — first 'cave-like' block (cell 2 non-empty)");
            mountainBlock = Make("E5. Terrain — first 'mountain-like' block (max Z high)");
            chunk4x4      = Make("F1. Terrain chunk 4x4 (idx 100-103 across two rows)");
            chunk8x8      = Make("F2. Terrain chunk 8x8");
            fullPmp       = Make("G. Full PMP template (urw_unq_start)");
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(dataRoot)) dataRoot = Diggles.Runtime.DigglesConfig.ConfiguredDataRoot;
            if (string.IsNullOrEmpty(testBdbPath)) testBdbPath = Path.Combine(dataRoot, "BlockDB1.bdb");
            if (string.IsNullOrEmpty(testPmpPath)) testPmpPath = Path.Combine(dataRoot, "Templates", "caves_1.pmp");
        }

        void OnGUI()
        {
            if (all.Count == 0) InitEntities();
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("Config", EditorStyles.boldLabel);
            dataRoot      = EditorGUILayout.TextField("Data root", dataRoot);
            testBdbPath   = EditorGUILayout.TextField("BDB path",  testBdbPath);
            testPmpPath   = EditorGUILayout.TextField("PMP path",  testPmpPath);
            testDwarfClass   = EditorGUILayout.TextField("Dwarf class",   testDwarfClass);
            testFrauClass    = EditorGUILayout.TextField("Frau class",    testFrauClass);
            testHamsterClass = EditorGUILayout.TextField("Hamster class", testHamsterClass);
            testTrollClass   = EditorGUILayout.TextField("Troll class",   testTrollClass);
            testDracheClass  = EditorGUILayout.TextField("Drache class",  testDracheClass);
            testStaticClass  = EditorGUILayout.TextField("Static class",  testStaticClass);
            testBlockIdx     = EditorGUILayout.IntField("Default block idx", testBlockIdx);
            chunkSize        = EditorGUILayout.IntSlider("Chunk size (4x4 used)", chunkSize, 2, 16);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("RESET position counter")) nextSpawnPos = Vector3.zero;
            if (GUILayout.Button("REMOVE all [TEST] objects")) RemoveAllTestSpawns();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Loaded campaign scene is required for MarkerModelResolver TSV maps.\n" +
                "Each Spawn lands at next X offset. Inspect in scene view + inspector.\n" +
                "Try rotating transform Y/X/Z by +90 to observe behavior.",
                MessageType.Info);

            DrawSection("A. Reference",       new[] { axes! });
            DrawSection("B. Raw model data",  new[] { rawMesh! });
            DrawSection("C. Animated chars",  new[] { dwarf!, frau!, hamster!, troll!, drache! });
            DrawSection("D. Static markers",  new[] { staticMarker! });
            DrawSection("E. Single blocks",   new[] { terrainBlock!, terrainBlock2!, terrainBlock3!,
                                                       caveBlock!, mountainBlock! });
            DrawSection("F. Multi-block chunks", new[] { chunk4x4!, chunk8x8! });
            DrawSection("G. Full template",   new[] { fullPmp! });

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Generate report")) report = GenerateReport();
            if (GUILayout.Button("Copy to clipboard"))
            {
                GUIUtility.systemCopyBuffer = report;
                Debug.Log("Diagnostics report copied to clipboard.");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.TextArea(report, GUILayout.MinHeight(200));

            EditorGUILayout.EndScrollView();
        }

        void DrawSection(string title, EntityObservation[] items)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            foreach (var obs in items) DrawEntity(obs);
        }

        void DrawEntity(EntityObservation obs)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(obs.Name, EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Spawn", GUILayout.MaxWidth(80)))
            {
                try
                {
                    obs.AutoInfo = "";
                    DoSpawn(obs);
                    obs.Spawned = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Spawn failed for {obs.Name}: {e}");
                    obs.AutoInfo = $"SPAWN FAILED: {e.Message}";
                    obs.Spawned = false;
                }
            }
            obs.Spawned = EditorGUILayout.Toggle("Spawned ok?", obs.Spawned);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(obs.AutoInfo))
                EditorGUILayout.HelpBox(obs.AutoInfo, MessageType.None);

            obs.Orientation   = EditorGUILayout.TextField("Orientation", obs.Orientation);
            obs.FacingOrAxis  = EditorGUILayout.TextField("Facing / dominant axis", obs.FacingOrAxis);
            obs.RotateY       = EditorGUILayout.TextField("Rotate Y +90 →", obs.RotateY);
            obs.Notes         = EditorGUILayout.TextField("Notes", obs.Notes);
            EditorGUILayout.EndVertical();
        }

        // ----- dispatch -----

        void DoSpawn(EntityObservation obs)
        {
            if (obs == axes)         SpawnAxes(obs);
            else if (obs == rawMesh) SpawnRawMesh(obs);
            else if (obs == dwarf)   SpawnAnimated(obs, testDwarfClass);
            else if (obs == frau)    SpawnAnimated(obs, testFrauClass);
            else if (obs == hamster) SpawnAnimated(obs, testHamsterClass);
            else if (obs == troll)   SpawnAnimated(obs, testTrollClass);
            else if (obs == drache)  SpawnAnimated(obs, testDracheClass);
            else if (obs == staticMarker) SpawnStaticMarker(obs);
            else if (obs == terrainBlock)  SpawnTerrainSingleBlock(obs, testBlockIdx);
            else if (obs == terrainBlock2) SpawnTerrainSingleBlock(obs, 200);
            else if (obs == terrainBlock3) SpawnTerrainSingleBlock(obs, 500);
            else if (obs == caveBlock)     SpawnTerrainCaveLike(obs);
            else if (obs == mountainBlock) SpawnTerrainMountainLike(obs);
            else if (obs == chunk4x4)      SpawnTerrainChunk(obs, 4);
            else if (obs == chunk8x8)      SpawnTerrainChunk(obs, 8);
            else if (obs == fullPmp)       SpawnFullPmp(obs);
        }

        // ----- spawn helpers -----

        Vector3 NextPos()
        {
            var p = nextSpawnPos;
            nextSpawnPos.x += SpawnSpacing;
            return p;
        }

        GameObject NewTestGO(string name)
        {
            var go = new GameObject($"[TEST] {name}");
            go.transform.position = NextPos();
            return go;
        }

        void SpawnAxes(EntityObservation obs)
        {
            var root = NewTestGO("Axes");
            CreateArrow(root.transform, Vector3.right,   Color.red,   "X+");
            CreateArrow(root.transform, Vector3.up,      Color.green, "Y+");
            CreateArrow(root.transform, Vector3.forward, Color.blue,  "Z+");
            var origin = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            origin.name = "origin";
            origin.transform.SetParent(root.transform, false);
            origin.transform.localScale = Vector3.one * 0.3f;
            origin.GetComponent<MeshRenderer>().sharedMaterial = MakeColorMat(Color.white);
            Selection.activeGameObject = root;
            obs.AutoInfo = $"Spawned at {root.transform.position}";
        }

        static void CreateArrow(Transform parent, Vector3 dir, Color color, string label)
        {
            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shaft.name = label;
            shaft.transform.SetParent(parent, false);
            shaft.transform.localPosition = dir * 1f;
            shaft.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir);
            shaft.transform.localScale = new Vector3(0.15f, 2f, 0.15f);
            shaft.GetComponent<MeshRenderer>().sharedMaterial = MakeColorMat(color);
        }

        static Material MakeColorMat(Color c)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(sh);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            mat.color = c;
            return mat;
        }

        void SpawnRawMesh(EntityObservation obs)
        {
            var path = Path.Combine(dataRoot, "3db", "mann.3db");
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            var model = ThreeDbParser.Parse(path);
            if (model.Meshes.Count == 0) throw new System.Exception("mann.3db has no meshes");

            var src = model.Meshes[0];
            var mesh = new Mesh { name = "RawMann_frame0" };
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var sub in src.SubMeshes)
            {
                if (sub.VListIndex >= model.VLists.Count) continue;
                if (sub.IdxListIndex >= model.IndexLists.Count) continue;
                var vl = model.VLists[sub.VListIndex];
                var il = model.IndexLists[sub.IdxListIndex];
                int baseIdx = verts.Count;
                foreach (var q in vl)
                {
                    const float Range = 65535f;
                    const float Scale = 100f;
                    var ux = (ushort)(short)q.x;
                    var uy = (ushort)(short)q.y;
                    var uz = (ushort)(short)q.z;
                    verts.Add(new Vector3(
                        (ux / Range - 0.5f) * Scale,
                        (uy / Range - 0.5f) * Scale,
                        (uz / Range - 0.5f) * Scale));
                }
                foreach (var i in il) if (i < vl.Length) tris.Add(baseIdx + i);
            }
            mesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = NewTestGO("Raw mann mesh");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MakeColorMat(new Color(0.7f, 0.7f, 0.9f));

            obs.AutoInfo = $"Pos={go.transform.position}; bounds={mesh.bounds.min}..{mesh.bounds.max} size={mesh.bounds.size}; verts={verts.Count}";
            Selection.activeGameObject = go;
        }

        void SpawnAnimated(EntityObservation obs, string className)
        {
            var dyn = MarkerModelResolver.ResolveDynamic(className, dataRoot, ctx: null);
            if (dyn == null || dyn.Clips.Length == 0 || dyn.Clips[0].Frames.Length == 0 || dyn.Clips[0].Frames[0] == null)
                throw new System.Exception($"ResolveDynamic('{className}') returned null/empty.");
            var go = NewTestGO($"Anim {className}");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = dyn.Clips[0].Frames[0];
            var mr = go.AddComponent<MeshRenderer>();
            if (dyn.Materials != null && dyn.Materials.Length > 0)
                mr.sharedMaterials = dyn.Materials;
            obs.AutoInfo = $"Pos={go.transform.position}; mesh bounds={mf.sharedMesh.bounds.min}..{mf.sharedMesh.bounds.max} size={mf.sharedMesh.bounds.size}; default clip='{dyn.DefaultClipName}' frames={dyn.Clips[0].Frames.Length}; label={dyn.DebugLabel}";
            Selection.activeGameObject = go;
        }

        void SpawnStaticMarker(EntityObservation obs)
        {
            var r = MarkerModelResolver.Resolve(testStaticClass, dataRoot, ctx: null);
            if (r?.Mesh == null) throw new System.Exception($"Resolve('{testStaticClass}') returned null");
            var go = NewTestGO($"Static {testStaticClass}");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = r.Mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (r.Materials != null && r.Materials.Length > 0)
                mr.sharedMaterials = r.Materials;
            obs.AutoInfo = $"Pos={go.transform.position}; bounds={r.Mesh.bounds.min}..{r.Mesh.bounds.max} size={r.Mesh.bounds.size}";
            Selection.activeGameObject = go;
        }

        // ----- terrain helpers -----

        BdbModel? cachedBdb;
        BdbModel LoadBdb()
        {
            if (cachedBdb != null) return cachedBdb;
            if (!File.Exists(testBdbPath)) throw new FileNotFoundException(testBdbPath);
            cachedBdb = BdbParser.Parse(testBdbPath);
            return cachedBdb;
        }

        // Build a mesh from a single block at (tx, ty) world position, using the
        // same vertex formula as BdbPmpWorldBuilder so we exercise that code path.
        static Mesh BuildBlockMesh(BdbModel bdb, int blockIdx, int tx, int ty, string name)
        {
            var mesh = new Mesh { name = name };
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var dedupe = new Dictionary<long, int>();

            const int MaxV = 4096;
            var palBuf = new ushort[MaxV];
            var nrmBuf = new byte[MaxV];

            for (int sub = 0; sub < 3; sub++)
            {
                var (triStart, triCount) = bdb.CellTriangleRange(blockIdx, sub);
                if (triCount == 0) continue;
                var cell = bdb.Blocks[blockIdx].Cells[sub];
                if (cell.VertCount == 0 || cell.VertCount > MaxV) continue;
                bdb.CopyCellPaletteIndices(blockIdx, sub, palBuf, nrmBuf);
                var idxPool = bdb.IndexPool;
                for (int t = 0; t < triCount; t += 3)
                {
                    int a = EmitV(idxPool[triStart + t], palBuf, cell.VertCount, bdb, tx, ty, verts, uvs, dedupe);
                    int b = EmitV(idxPool[triStart + t + 1], palBuf, cell.VertCount, bdb, tx, ty, verts, uvs, dedupe);
                    int c = EmitV(idxPool[triStart + t + 2], palBuf, cell.VertCount, bdb, tx, ty, verts, uvs, dedupe);
                    if (a < 0 || b < 0 || c < 0) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                }
            }

            mesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static int EmitV(ushort poolIdx, ushort[] palBuf, int vc, BdbModel bdb, int tx, int ty,
            List<Vector3> verts, List<Vector2> uvs, Dictionary<long, int> dedupe)
        {
            if (poolIdx >= vc) return -1;
            int palIdx = palBuf[poolIdx];
            if (palIdx >= bdb.Palette.Length) return -1;
            long key = ((long)palIdx << 32) | ((long)(uint)(ty * 65536 + tx));
            if (dedupe.TryGetValue(key, out var existing)) return existing;
            var p = bdb.Palette[palIdx];
            // Same axis convention as BdbPmpWorldBuilder.EmitVertex:
            // Unity X = Diggles X (horizontal)
            // Unity Y = Diggles Z (height)
            // Unity Z = -Diggles Y (depth)
            var pos = new Vector3(p.Position.x + tx, p.Position.z, -(p.Position.y + ty));
            int idx = verts.Count;
            verts.Add(pos);
            uvs.Add(new Vector2(p.Uv.x, 1f - p.Uv.y));
            dedupe[key] = idx;
            return idx;
        }

        void SpawnTerrainSingleBlock(EntityObservation obs, int blockIdx)
        {
            var bdb = LoadBdb();
            if (blockIdx < 0 || blockIdx >= bdb.Header.NBlocks)
                throw new System.Exception($"Block idx {blockIdx} out of range [0, {bdb.Header.NBlocks})");
            var mesh = BuildBlockMesh(bdb, blockIdx, 0, 0, $"Block_{blockIdx}");
            var go = NewTestGO($"Terrain block {blockIdx}");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MakeColorMat(new Color(0.6f, 0.5f, 0.3f));

            // Inspect cells
            var cellInfo = "";
            for (int s = 0; s < 3; s++)
            {
                var c = bdb.Blocks[blockIdx].Cells[s];
                cellInfo += $"cell{s}={c.VertCount}v ";
            }
            obs.AutoInfo = $"Pos={go.transform.position}; bounds={mesh.bounds.min}..{mesh.bounds.max} size={mesh.bounds.size}; {cellInfo}";
            Selection.activeGameObject = go;
        }

        void SpawnTerrainCaveLike(EntityObservation obs)
        {
            var bdb = LoadBdb();
            // Find first block where cell 2 (cave / bottom face) has verts
            int idx = -1;
            for (int i = 1; i < bdb.Header.NBlocks; i++)
            {
                var c2 = bdb.Blocks[i].Cells[2];
                if (c2.VertCount > 0) { idx = i; break; }
            }
            if (idx < 0) throw new System.Exception("No cave-like block found");
            SpawnTerrainSingleBlock(obs, idx);
            obs.AutoInfo = $"CAVE-LIKE block #{idx}: " + obs.AutoInfo;
        }

        void SpawnTerrainMountainLike(EntityObservation obs)
        {
            var bdb = LoadBdb();
            // Find a block with highest cell 0 vertex Z value (steep top face).
            int bestIdx = -1;
            float bestZ = float.MinValue;
            for (int i = 1; i < bdb.Header.NBlocks; i++)
            {
                var c0 = bdb.Blocks[i].Cells[0];
                if (c0.VertCount == 0) continue;
                var palBuf = new ushort[c0.VertCount];
                var nrmBuf = new byte[c0.VertCount];
                bdb.CopyCellPaletteIndices(i, 0, palBuf, nrmBuf);
                float maxZ = float.MinValue;
                for (int v = 0; v < c0.VertCount; v++)
                {
                    int palIdx = palBuf[v];
                    if (palIdx >= bdb.Palette.Length) continue;
                    var z = bdb.Palette[palIdx].Position.z;
                    if (z > maxZ) maxZ = z;
                }
                if (maxZ > bestZ) { bestZ = maxZ; bestIdx = i; }
                if (bestZ > 7f) break; // good enough
            }
            if (bestIdx < 0) throw new System.Exception("No mountain-like block found");
            SpawnTerrainSingleBlock(obs, bestIdx);
            obs.AutoInfo = $"MOUNTAIN-LIKE block #{bestIdx} (maxZ={bestZ:F2}): " + obs.AutoInfo;
        }

        void SpawnTerrainChunk(EntityObservation obs, int size)
        {
            var bdb = LoadBdb();
            int idx = testBlockIdx;
            if (idx < 0 || idx >= bdb.Header.NBlocks) idx = 100;

            var rootGO = NewTestGO($"Chunk {size}x{size} (block #{idx} grid)");
            for (int ty = 0; ty < size; ty++)
            {
                for (int tx = 0; tx < size; tx++)
                {
                    var mesh = BuildBlockMesh(bdb, idx, tx, ty, $"Block_{idx}_at_{tx}_{ty}");
                    var child = new GameObject($"Tile_{tx}_{ty}");
                    child.transform.SetParent(rootGO.transform, false);
                    child.transform.localPosition = Vector3.zero;
                    var mf = child.AddComponent<MeshFilter>();
                    mf.sharedMesh = mesh;
                    var mr = child.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = MakeColorMat(
                        new Color(0.5f + tx * 0.05f, 0.5f, 0.3f + ty * 0.04f));
                }
            }
            obs.AutoInfo = $"Chunk root pos={rootGO.transform.position}; {size}x{size} tiles using block #{idx}";
            Selection.activeGameObject = rootGO;
        }

        void SpawnFullPmp(EntityObservation obs)
        {
            if (!File.Exists(testPmpPath)) throw new FileNotFoundException(testPmpPath);
            var rootGO = NewTestGO($"FullPMP {Path.GetFileNameWithoutExtension(testPmpPath)}");
            BdbPmpWorldBuilder.BuildWorld(rootGO, null, new BdbPmpWorldBuilder.BuildArgs
            {
                BdbPath = testBdbPath,
                PmpPath = testPmpPath,
                OffsetX = 0,
                OffsetY = 0,
            });
            // Compute bounds
            var renderers = rootGO.GetComponentsInChildren<MeshRenderer>();
            Bounds total = renderers.Length > 0 ? renderers[0].bounds : new Bounds();
            foreach (var mr in renderers) total.Encapsulate(mr.bounds);
            obs.AutoInfo = $"Root pos={rootGO.transform.position}; total bounds={total.min}..{total.max} size={total.size}; renderers={renderers.Length}";
            Selection.activeGameObject = rootGO;
        }

        // ----- cleanup -----
        void RemoveAllTestSpawns()
        {
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            int count = 0;
            foreach (var go in roots)
                if (go.name.StartsWith("[TEST]")) { DestroyImmediate(go); count++; }
            Debug.Log($"Removed {count} [TEST] objects.");
            nextSpawnPos = Vector3.zero;
        }

        // ----- report -----
        string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Diggles Spawn Diagnostics Report v2");
            sb.AppendLine();
            sb.AppendLine($"Generated: {System.DateTime.Now}");
            sb.AppendLine($"Unity: {Application.unityVersion}, Pipeline: " +
                          (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.name ?? "Built-in"));

            var sv = SceneView.lastActiveSceneView;
            if (sv != null && sv.camera != null)
            {
                var c = sv.camera;
                sb.AppendLine($"SceneView: pos={c.transform.position} eul={c.transform.rotation.eulerAngles}");
            }
            foreach (var rootGO in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (rootGO.name.StartsWith("Diggles__"))
                {
                    sb.AppendLine($"Campaign root '{rootGO.name}': localEul={rootGO.transform.localEulerAngles} worldEul={rootGO.transform.eulerAngles}");
                    break;
                }
            }
            sb.AppendLine();

            foreach (var obs in all)
            {
                sb.AppendLine($"## {obs.Name}");
                sb.AppendLine($"  - Spawned ok: {obs.Spawned}");
                if (!string.IsNullOrWhiteSpace(obs.AutoInfo))
                    sb.AppendLine($"  - Info: {obs.AutoInfo}");
                if (!string.IsNullOrWhiteSpace(obs.Orientation))
                    sb.AppendLine($"  - Orientation: {obs.Orientation}");
                if (!string.IsNullOrWhiteSpace(obs.FacingOrAxis))
                    sb.AppendLine($"  - Facing/axis: {obs.FacingOrAxis}");
                if (!string.IsNullOrWhiteSpace(obs.RotateY))
                    sb.AppendLine($"  - Rotate Y +90 → {obs.RotateY}");
                if (!string.IsNullOrWhiteSpace(obs.Notes))
                    sb.AppendLine($"  - Notes: {obs.Notes}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
