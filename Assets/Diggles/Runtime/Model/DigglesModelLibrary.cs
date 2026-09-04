#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using UnityEngine;

    // Runtime-side loader for Diggles.3db character/building/prop models.
    //
    // This is the standalone counterpart to the editor MarkerModelResolver: it
    // parses.3db files, builds frame meshes + materials, and serves animation
    // clips by (objectName, clipName) — LAZILY, so listing the 1023 clips of
    // `mann` is cheap and only the clips actually played get their meshes built.
    //
    // It reads the same data the editor pipeline uses:
    // * StreamingAssets/class_model_map_v2.tsv (class -> obj, anim, visible)
    // * StreamingAssets/obj_db_map.tsv (obj -> db)
    // * <dataRoot>/3db/<db>.3db (model)
    // * <dataRoot>/Texture/<lod>/<name>.tga (textures)
    // * <dataRoot>/sphere.dat (normal table)
    //
    // Vertices use the correct per-mesh bbox dequantisation
    // (ThreeDbMesh.DequantizeVertex) rather than the importer's legacy unit-cube
    // path, so model proportions/scale are faithful. Orientation is left to the
    // caller (the preview holder applies the world rotation).
    public sealed class DigglesModelLibrary
    {
        // Canonical model→world scale, shared by every spawn path so the world
        // (MarkerModelResolver) and the runtime library (this class via
        // RuntimeLibrarySpawner / DigglesTclAnimDemo) render characters at the
        // same size. Both use the identical bbox vertex decode; only this uniform
        // factor differs between paths, so keeping it shared keeps them consistent.
        //
        // 0.005 = the engine's faithful model→world scale (1/200, DAT_00736fcc).
        // Confirmed against the real game: on our 1-tile = 1-unit
        // world a dwarf ≈ 1.05 units (≈ tile-height), which matches in-game
        // proportions. This is ~0.82× the old unit-cube world size (the bbox-vs-
        // unit-cube ratio is a near-constant 0.82 across all objects), so adopting
        // it shrinks world characters by ~0.82× vs the earlier build. (The old
        // absolute WalkClimbAction offsets this note flagged are gone — the climb
        // is record-driven now; the nav data layer is terrain-derived and unaffected.)
        public const float ModelToWorldScale = 0.005f;

        public string DataRoot { get; }

        /// Asset-path indirection (mods/ → data/). With no mod roots registered this is a
        /// pass-through to DataRoot, so behaviour is unchanged. See DigglesAssetResolver.
        public DigglesAssetResolver Assets { get; }

        public DigglesModelLibrary(string dataRoot)
        {
            DataRoot = dataRoot ?? "";
            Assets = new DigglesAssetResolver(DataRoot);
        }

        // ----- public API ---------------------------------------------------

        public readonly struct ClassResolve
        {
            public readonly string Obj;
            public readonly string DefaultAnim;
            public readonly bool Visible;
            public ClassResolve(string obj, string anim, bool visible)
            { Obj = obj; DefaultAnim = anim; Visible = visible; }
        }

        public bool MapsLoaded => _classMap != null && _objDb != null;

        // All class names known to the class->model map (sorted).
        public IReadOnlyList<string> ClassNames
        {
            get
            {
                EnsureMaps();
                if (_classNamesSorted == null)
                {
                    _classNamesSorted = _classMap!.Keys.ToList();
                    _classNamesSorted.Sort(System.StringComparer.OrdinalIgnoreCase);
                }
                return _classNamesSorted;
            }
        }

        // All object names known to the obj→db map (sorted). The natural picker
        // for an anim tester (one object = one model with its own clip set).
        public IReadOnlyList<string> ObjectNames
        {
            get
            {
                EnsureMaps();
                if (_objNamesSorted == null)
                {
                    _objNamesSorted = new List<string>(_objDb!.Keys);
                    _objNamesSorted.Sort(System.StringComparer.OrdinalIgnoreCase);
                }
                return _objNamesSorted;
            }
        }

        public ClassResolve? ResolveClass(string className)
        {
            EnsureMaps();
            if (_classMap == null) return null;
            if (!_classMap.TryGetValue(className, out var e)) return null;
            if (string.IsNullOrEmpty(e.obj)) return null;
            return new ClassResolve(e.obj!, e.anim ?? "", e.visible);
        }

        // The TSV knows this class is deliberately modelless (visible=0, no obj) — a marker /
        // helper class. ResolveClass returns null for those (no obj), losing the flag; the
        // spawner uses this to keep them out of the pink-placeholder fallback.
        public bool IsClassInvisible(string className)
        {
            EnsureMaps();
            return _classMap != null
                && _classMap.TryGetValue(className, out var e)
                && !e.visible && string.IsNullOrEmpty(e.obj);
        }

        public string? GetObjDb(string objName)
        {
            EnsureMaps();
            if (_objDb != null && _objDb.TryGetValue(objName, out var db)) return db;
            // Fallback: an object that isn't in the generated registry but ships as its own
            // <obj>.3db (e.g. the cutscene narrator 'elfe' — present at 3db/elfe.3db but absent
            // from obj_db_map.tsv). Without this it spawns as the pink wireframe placeholder.
            if (_objDb != null && !string.IsNullOrEmpty(objName))
            {
                foreach (var cand in new[] { objName, objName.ToLowerInvariant() })
                {
                    if (File.Exists(Path.Combine(DataRoot, "3db", cand + ".3db")))
                    {
                        _objDb[objName] = cand;   // cache the discovery
                        return cand;
                    }
                }
            }
            return null;
        }

        // Names of every animation defined on an object, in.3db order. Cheap —
        // no mesh building. Returns empty if the object/db can't be found.
        public IReadOnlyList<string> GetClipNames(string objName)
        {
            var loc = FindObject(objName);
            if (loc == null) return System.Array.Empty<string>();
            var (db, obj, _) = loc.Value;
            var names = new List<string>(obj.Indices.Length);
            foreach (var animIdx in obj.Indices)
            {
                if (animIdx >= db.Anims.Count) continue;
                names.Add(db.Anims[(int)animIdx].Name1);
            }
            return names;
        }

        public int GetClipFrameCount(string objName, string clipName)
        {
            var loc = FindObject(objName);
            if (loc == null) return 0;
            var (db, obj, _) = loc.Value;
            var anim = FindAnim(db, obj, clipName);
            return anim?.Indices.Length ?? 0;
        }

        // Resolves the native CMesh index stored by systems such as CElf back to the authored
        // animation clip and frame that reference it. Several clips may share a mesh; iteration in
        // the object's native animation-table order provides the same stable first match.
        public bool TryResolveNativeMesh(
            string objName,
            int nativeMeshIndex,
            out string clipName,
            out int frameIndex)
        {
            clipName = "";
            frameIndex = -1;
            if (nativeMeshIndex < 0) return false;
            var loc = FindObject(objName);
            if (loc == null) return false;
            var (db, obj, _) = loc.Value;
            foreach (uint animationIndex in obj.Indices)
            {
                if (animationIndex >= db.Anims.Count) continue;
                ThreeDbAnim animation = db.Anims[(int)animationIndex];
                for (int frame = 0; frame < animation.Indices.Length; frame++)
                {
                    if (animation.Indices[frame] != nativeMeshIndex) continue;
                    clipName = animation.Name1;
                    frameIndex = frame;
                    return true;
                }
            }
            return false;
        }

        // has_cmap <obj.anim> [frame] — whether the anim's frame mesh carries a collision map.
        // Native law (TCL:CTclGlobalMethods::HasCMap FUN_00659280:140-159): resolve the anim →
        // the frame array at animRec+0x08 → mesh id → CMesh via the 0xE8-stride table
        // (FUN_004059a0) → true iff mesh+0x00 (the last u16 of CMesh::Persist FUN_00402640,
        // sign-extended = the cmap index) != -1. The port's ThreeDbMesh.Field0 is that exact
        // field. animinit.tcl keys every dummy class's collision default on this
        // (`if {[has_cmap $anim.standard]} {class_collision 1}`,:249-251/:327) — the stubbed
        // "0" left every mushroom/tree non-collidable.
        public bool HasCMap(string objName, string clipName, int frame = 0)
        {
            var loc = FindObject(objName);
            if (loc == null) return false;
            var (db, obj, _) = loc.Value;
            var anim = FindAnim(db, obj, clipName);
            if (anim == null || anim.Indices.Length == 0) return false;
            if (frame < 0 || frame >= anim.Indices.Length) frame = 0;
            int meshIdx = (int)anim.Indices[frame];
            if (meshIdx < 0 || meshIdx >= db.Meshes.Count) return false;
            return db.Meshes[meshIdx].Field0 != 0xFFFF;   // cmap index != -1
        }

        // The baked per-mesh bbox of a clip frame in raw model units (CMesh rec+0x44..0x58 —
        // the exact box the native scales by 0.005 (x/y) / 0.01 (z) for body overlap
        // (FUN_005e7000:117-140) and the mouse pick (FUN_004796d0:124-158)). Cheap: header
        // data only, no mesh build.
        public bool TryGetMeshBBox(string objName, string clipName, int frame,
            out Vector3 min, out Vector3 max)
        {
            min = max = default;
            var loc = FindObject(objName);
            if (loc == null) return false;
            var (db, obj, _) = loc.Value;
            var anim = FindAnim(db, obj, clipName);
            if (anim == null || anim.Indices.Length == 0) return false;
            if (frame < 0 || frame >= anim.Indices.Length) frame = 0;
            int meshIdx = (int)anim.Indices[frame];
            if (meshIdx < 0 || meshIdx >= db.Meshes.Count) return false;
            var m = db.Meshes[meshIdx];
            min = m.BBoxMin;
            max = m.BBoxMax;
            return true;
        }

        // Build (or return cached) a clip with all its frame meshes + materials.
        public DigglesAnimClip? GetClip(string objName, string clipName)
        {
            var key = objName + "|" + clipName;
            if (_clipCache.TryGetValue(key, out var cached)) return cached;
            // Cache miss = a synchronous model build (frame meshes + VAT bake + material/texture
            // loads) on the main thread — a known hitch candidate. Named for the freeze watchdog.
            using var _perf = Diggles.Runtime.Diagnostics.PerfTrace.Section("model.buildclip", 100);

            var loc = FindObject(objName);
            if (loc == null)
            {
                // Resolution can succeed while clips fail silently; name the failing step
                // (db load/parse warn separately).
                Debug.LogWarning($"[3db] GetClip({objName}.{clipName}): object not found in db " +
                    $"'{GetObjDb(objName) ?? "<unmapped>"}' (dbCache={_dbCache.Count} entries)");
                return null;
            }
            var (db, obj, dbName) = loc.Value;
            var anim = FindAnim(db, obj, clipName);
            if (anim == null || anim.Indices.Length == 0)
            {
                Debug.LogWarning($"[3db] GetClip({objName}.{clipName}): anim " +
                    $"{(anim == null ? "not found" : "has no frames")} on object in '{dbName}' " +
                    $"(object has {obj.Indices.Length} anim refs, db has {db.Anims.Count} anims)");
                return null;
            }

            var mats = GetMaterials(dbName, db);
            var frames = new Mesh[anim.Indices.Length];
            var srcMeshIdx = new int[anim.Indices.Length];
            var linkA = new Vector3[anim.Indices.Length];
            var linkS = new Vector3[anim.Indices.Length];
            Material[]? clipMats = null;
            for (int f = 0; f < anim.Indices.Length; f++)
            {
                int meshIdx = (int)anim.Indices[f];
                srcMeshIdx[f] = meshIdx;
                frames[f] = GetFrameMesh(dbName, db, meshIdx);   // builds + caches the affine
                if (TryGetMeshAffine(dbName, meshIdx, out var a, out var s)) { linkA[f] = a; linkS[f] = s; }
                if (f == 0) clipMats = BuildSubmeshMaterials(db, meshIdx, mats);
            }

            var clip = new DigglesAnimClip
            {
                Name = anim.Name1,
                SourceObjName = objName,    // anim-frame SFX key = "<obj>.<anim>" (soundinit)
                DefaultMode = DigglesAnimator.AnimMode.Loop,
                MotionOffset = anim.VecA,
                RotationDelta = anim.VecB,
                Frames = frames,
                Materials = clipMats,
                SourceModel = db,           // for link/attachment-point lookup
                SourceMeshIndices = srcMeshIdx,
                FrameLinkA = linkA,
                FrameLinkS = linkS,
            };
            BuildVat(clip);   // GPU interpolation data (multi-frame, consistent topology)
            _clipCache[key] = clip;
            return clip;
        }

        // Pack a clip's per-frame vertex positions into a Vertex Animation Texture
        // + a static base mesh (frame-0 topology with each vertex's VAT column in
        // UV1.x). The Diggles/Model shader lerps frame f0→f1 on the GPU. Skipped
        // (Vat stays null → animator mesh-swaps) for single-frame or topology-
        // mismatched clips.
        private void BuildVat(DigglesAnimClip clip)
        {
            var frames = clip.Frames;
            if (frames == null || frames.Length < 2 || frames[0] == null) return;
            int fc = frames.Length;

            // Dominant topology (the mode of vertexCount):.3db clips occasionally carry stray
            // frames a few vertices off (tonne.ani = [576, 574, 582×6]) — the old hard bail on
            // any mismatch dropped those clips to the 10 FPS mesh-swap (visible on the barrel's
            // animation and the Raupe wiggle). Mismatched
            // frames are resampled onto the dominant topology by nearest-vertex against a
            // reference frame — a few-vertex divergence is visually invisible under the lerp.
            int vc = 0, bestCnt = 0;
            for (int f = 0; f < fc; f++)
            {
                if (frames[f] == null) return;
                int c = frames[f].vertexCount, n = 0;
                for (int g = 0; g < fc; g++) if (frames[g].vertexCount == c) n++;
                if (n > bestCnt || (n == bestCnt && c > vc)) { bestCnt = n; vc = c; }
            }
            if (vc == 0) return;
            int refIdx = 0;
            for (int f = 0; f < fc; f++) if (frames[f].vertexCount == vc) { refIdx = f; break; }
            var refVerts = frames[refIdx].vertices;

            var vat = new Texture2D(vc, fc, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "VAT_" + clip.Name,
            };
            var pixels = new Color[vc * fc];
            for (int f = 0; f < fc; f++)
            {
                var verts = frames[f].vertices;            // one-time per clip
                int row = f * vc;
                if (verts.Length == vc)
                {
                    for (int v = 0; v < vc; v++)
                    {
                        var p = verts[v];
                        pixels[row + v] = new Color(p.x, p.y, p.z, 0f);
                    }
                }
                else
                {
                    // Nearest-vertex resample to the reference topology (one-time, small meshes).
                    for (int v = 0; v < vc; v++)
                    {
                        var want = refVerts[v];
                        float bd = float.MaxValue; Vector3 bp = want;
                        for (int u = 0; u < verts.Length; u++)
                        {
                            float d = (verts[u] - want).sqrMagnitude;
                            if (d < bd) { bd = d; bp = verts[u]; }
                        }
                        pixels[row + v] = new Color(bp.x, bp.y, bp.z, 0f);
                    }
                }
            }
            vat.SetPixels(pixels);
            vat.Apply(false, true);

            // Base mesh = the reference frame's topology + UV1.x = vertex column (v+0.5)/vc.
            var baseMesh = Object.Instantiate(frames[refIdx]);
            baseMesh.name = "Base_" + clip.Name;
            var cols = new List<Vector2>(vc);
            for (int v = 0; v < vc; v++) cols.Add(new Vector2((v + 0.5f) / vc, 0f));
            baseMesh.SetUVs(1, cols);
            baseMesh.bounds = frames[refIdx].bounds;

            clip.Vat = vat;
            clip.BaseMesh = baseMesh;
            clip.VatFrameCount = fc;
        }

        public void ClearCaches()
        {
            foreach (var m in _meshCache.Values) if (m != null) Object.Destroy(m);
            _meshCache.Clear();
            foreach (var arr in _dbMaterials.Values)
                if (arr != null) foreach (var mat in arr) if (mat != null) Object.Destroy(mat);
            _dbMaterials.Clear();
            foreach (var t in _textureCache.Values) if (t != null) Object.Destroy(t);
            _textureCache.Clear();
            _alphaKindCache.Clear();
            foreach (var c in _clipCache.Values)
            {
                if (c == null) continue;
                if (c.Vat != null) Object.Destroy(c.Vat);
                if (c.BaseMesh != null) Object.Destroy(c.BaseMesh);
            }
            _clipCache.Clear();
            // _dbCache is deliberately not cleared: it is the process-wide parsed-.3db cache
            // (pure data, no Unity objects) that makes repeat loads cheap. The data root is
            // fixed for the process lifetime.
            _objLocCache.Clear();
        }

        // ----- maps ---------------------------------------------------------

        private struct MapEntry { public string? obj; public string? anim; public bool visible; }
        private Dictionary<string, MapEntry>? _classMap;
        private Dictionary<string, string>? _objDb;
        private List<string>? _classNamesSorted;
        private List<string>? _objNamesSorted;

        private void EnsureMaps()
        {
            if (_classMap != null && _objDb != null) return;
            var classPath = Path.Combine(Application.streamingAssetsPath, "class_model_map_v2.tsv");
            var objDbPath = Path.Combine(Application.streamingAssetsPath, "obj_db_map.tsv");
            _classMap = new Dictionary<string, MapEntry>(4096);
            _objDb = new Dictionary<string, string>(4096);
            if (!File.Exists(classPath) || !File.Exists(objDbPath))
            {
                Debug.LogError($"DigglesModelLibrary: missing TSV ({classPath} or {objDbPath})");
                return;
            }
            foreach (var line in File.ReadAllLines(classPath))
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 4) continue;
                _classMap[p[0]] = new MapEntry
                {
                    obj = string.IsNullOrEmpty(p[1]) ? null : p[1],
                    anim = string.IsNullOrEmpty(p[2]) ? null : p[2],
                    visible = p[3] == "1",
                };
            }
            foreach (var line in File.ReadAllLines(objDbPath))
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                _objDb[p[0]] = p[1];
            }
        }

        // ----- db + object resolution --------------------------------------

        // Static on purpose: a fresh candidate library re-parsed every
        //.3db database on each in-game load. ThreeDbModel is pure parsed data (arrays/strings,
        // no Unity objects), keyed by database name — identical files parse identically, so
        // sharing across library instances is safe. Built clips (Unity meshes) stay
        // per-instance: session disposal + UnloadUnusedAssets may reclaim those.
        private static readonly Dictionary<string, ThreeDbModel> _dbCache = new();
        private readonly Dictionary<string, DigglesAnimClip> _clipCache = new();
        private Vector3[]? _sphereDat;

        private readonly Dictionary<string, (ThreeDbModel db, ThreeDbObject obj, string dbName)> _objLocCache = new();

        private (ThreeDbModel db, ThreeDbObject obj, string dbName)? FindObject(string objName)
        {
            if (_objLocCache.TryGetValue(objName, out var hit)) return hit;
            var dbName = GetObjDb(objName);
            if (dbName == null) return null;
            var db = LoadDb(dbName);
            if (db == null) return null;
            for (int i = 0; i < db.Objects.Count; i++)
            {
                if (db.Objects[i].Name == objName)
                {
                    var loc = (db, db.Objects[i], dbName);
                    _objLocCache[objName] = loc;
                    return loc;
                }
            }
            return null;
        }

        private static ThreeDbAnim? FindAnim(ThreeDbModel db, ThreeDbObject obj, string clipName)
        {
            foreach (var animIdx in obj.Indices)
            {
                if (animIdx >= db.Anims.Count) continue;
                var a = db.Anims[(int)animIdx];
                if (a.Name1 == clipName) return a;
            }
            return null;
        }

        // Save restoration resolves a database by its native slot name (the animinit order)
        // even when the live animator holds no clip from it yet — e.g. the crate pose of a
        // boxed building lives in 'transport', which a building animator never preloads.
        public ThreeDbModel? GetDatabaseModel(string dbName) => LoadDb(dbName);

        private ThreeDbModel? LoadDb(string dbName)
        {
            EnsureSphereDat();
            if (_dbCache.TryGetValue(dbName, out var cached)) return cached;
            var path = Assets.Resolve(Path.Combine("3db", dbName + ".3db"));
            if (!File.Exists(path))
            {
                path = Assets.Resolve(Path.Combine("3db", dbName.ToLower() + ".3db"));
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"DigglesModelLibrary: missing 3db file '{dbName}'");
                    return null;
                }
            }
            try
            {
                var model = ThreeDbParser.Parse(path);
                _dbCache[dbName] = model;
                return model;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"DigglesModelLibrary: parse failed for {path}: {e.Message}");
                return null;
            }
        }

        private void EnsureSphereDat()
        {
            if (_sphereDat != null) return;
            var sp = Assets.Resolve("sphere.dat");
            if (!File.Exists(sp)) return;
            var bytes = File.ReadAllBytes(sp);
            if (bytes.Length != 3072) return;
            _sphereDat = new Vector3[256];
            using var s = new MemoryStream(bytes);
            using var br = new BinaryReader(s);
            for (int i = 0; i < 256; i++)
                _sphereDat[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }

        // ----- mesh build ---------------------------------------------------

        // Decode vertices with the engine's absolute map (u16/4-8192) instead of the
        // per-mesh window→bbox normalize. Needed so attachment link points + the attached
        // model's own verts share the engine's space (hats sat offset otherwise). The
        // gnome's window ≈ absolute so it's unaffected. Toggle off to revert.
        public static bool UseAbsoluteDecode = true;

        private readonly Dictionary<string, Mesh> _meshCache = new();

        // The exact affine each mesh decodes vertices with: world = A + u16*S (per axis).
        // Link points are quantized the same way, so applying this affine (not the engine's
        // absolute u/4-8192) places a link exactly in the mesh's vertex space.
        private readonly Dictionary<string, (Vector3 A, Vector3 S)> _meshAffine = new();
        public bool TryGetMeshAffine(string dbName, int meshIdx, out Vector3 a, out Vector3 s)
        {
            if (_meshAffine.TryGetValue(dbName + "|m" + meshIdx, out var aff)) { a = aff.A; s = aff.S; return true; }
            a = Vector3.zero; s = Vector3.zero; return false;
        }

        private Mesh GetFrameMesh(string dbName, ThreeDbModel db, int meshIdx)
        {
            var key = dbName + "|m" + meshIdx;
            if (_meshCache.TryGetValue(key, out var cached)) return cached;
            var mesh = BuildMesh(db, meshIdx, key);
            if (mesh != null) _meshCache[key] = mesh;
            return mesh!;
        }

        private Mesh? BuildMesh(ThreeDbModel db, int meshIdx, string nameHint)
        {
            if (meshIdx < 0 || meshIdx >= db.Meshes.Count) return null;
            var srcMesh = db.Meshes[meshIdx];
            if (srcMesh.SubMeshes.Count == 0) return null;

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var perSubmeshTris = new List<int[]>();

            // Vertex decode:
            // each component is a 16-bit value in offset-binary clustered tightly
            // around 0x8000 — the meaningful coordinate occupies only a small
            // window (~hundreds) of the range, and the per-mesh BBox is the exact
            // AABB of those values. So decode = normalise each axis's actual
            // unsigned-value range across the whole mesh into the bbox. A naive
            // full-range linear map collapses everything to the bbox corners
            // (renders as a cube); the old unit-cube map collapsed it ~200× small.
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
            if (uMaxX < uMinX) return null; // no verts
            var bMin = srcMesh.BBoxMin;
            var bMax = srcMesh.BBoxMax;
            // Absolute decode (the engine's true vertex/link map: world = u16/4 - 8192).
            // Overriding bMin/bMax to the absolute dequant of this mesh's u16 range makes
            // the window formula below collapse to absolute (S→0.25, A→-8192). The gnome's
            // window already ≈ absolute (so unchanged); models whose bbox ≠ their dequant
            // range (e.g. hats — verts were offset from origin → floated) get corrected.
            if (UseAbsoluteDecode)
            {
                bMin = new Vector3(uMinX / 4f - 8192f, uMinY / 4f - 8192f, uMinZ / 4f - 8192f);
                bMax = new Vector3(uMaxX / 4f - 8192f, uMaxY / 4f - 8192f, uMaxZ / 4f - 8192f);
            }
            float sx = uMaxX > uMinX ? (bMax.x - bMin.x) / (uMaxX - uMinX) : 0f;
            float sy = uMaxY > uMinY ? (bMax.y - bMin.y) / (uMaxY - uMinY) : 0f;
            float sz = uMaxZ > uMinZ ? (bMax.z - bMin.z) / (uMaxZ - uMinZ) : 0f;
            // Cache the decode affine (world = A + u16*S) so link points map into this
            // exact vertex space. nameHint == the mesh cache key.
            _meshAffine[nameHint] = (
                new Vector3(bMin.x - uMinX * sx, bMin.y - uMinY * sy, bMin.z - uMinZ * sz),
                new Vector3(sx, sy, sz));

            foreach (var sub in srcMesh.SubMeshes)
            {
                if (sub.VListIndex >= db.VLists.Count) continue;
                if (sub.IdxListIndex >= db.IndexLists.Count) continue;

                var vList = db.VLists[sub.VListIndex];
                var idxList = db.IndexLists[sub.IdxListIndex];
                var uvList = sub.UvListIndex < db.UvLists.Count
                    ? db.UvLists[sub.UvListIndex] : System.Array.Empty<Vector2>();
                var nList = sub.NListIndex < db.NLists.Count
                    ? db.NLists[sub.NListIndex] : System.Array.Empty<byte>();

                int vertexBase = verts.Count;
                foreach (var q in vList)
                {
                    int ux = (ushort)q.x, uy = (ushort)q.y, uz = (ushort)q.z;
                    verts.Add(new Vector3(
                        bMin.x + (ux - uMinX) * sx,
                        bMin.y + (uy - uMinY) * sy,
                        bMin.z + (uz - uMinZ) * sz));
                }

                AppendUvs(uvs, uvList, vList.Length);
                AppendNormals(normals, nList, vList.Length);
                perSubmeshTris.Add(BuildTriangles(idxList, vertexBase, vList.Length));
            }

            if (verts.Count == 0) return null;

            var mesh = new Mesh { name = nameHint };
            mesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            if (uvs.Count == verts.Count) mesh.SetUVs(0, uvs);
            if (normals.Count == verts.Count) mesh.SetNormals(normals);
            else mesh.RecalculateNormals();
            mesh.subMeshCount = perSubmeshTris.Count;
            for (int i = 0; i < perSubmeshTris.Count; i++)
                mesh.SetTriangles(perSubmeshTris[i], i);
            mesh.RecalculateBounds();
            return mesh;
        }

        private void AppendUvs(List<Vector2> dst, Vector2[] src, int expectedCount)
        {
            if (src.Length == expectedCount)
            {
                foreach (var uv in src) dst.Add(new Vector2(uv.x, 1f - uv.y));
                return;
            }
            for (int i = 0; i < expectedCount; i++) dst.Add(Vector2.zero);
        }

        private void AppendNormals(List<Vector3> dst, byte[] src, int expectedCount)
        {
            if (_sphereDat == null || src.Length != expectedCount)
            {
                for (int i = 0; i < expectedCount; i++) dst.Add(Vector3.up);
                return;
            }
            foreach (var idx in src) dst.Add(_sphereDat[idx]);
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

        // ----- materials ----------------------------------------------------

        private readonly Dictionary<string, Material[]> _dbMaterials = new();
        private readonly Dictionary<string, Texture2D> _textureCache =
            new(System.StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TgaAlphaKind> _alphaKindCache =
            new(System.StringComparer.OrdinalIgnoreCase);
        private static readonly string[] LodFolders =
            { "m256", "m128", "m064", "m032", "Gray", "Misc", "ClassIcons" };

        // ----- texture variants (Tcl `def_texture`) -------------------------
        // Base material name → ordered variant TGA filenames (index 0 = the base/normal).
        // `set_textureanimation <obj> <submesh> <index…>` picks a variant by index. Keyed by
        // base material NAME, so it's gender-aware for free (ZMann_* vs ZFrau_* are separate
        // entries) and general (clothing/hair/hat color, elves, trolls, … all use it).
        private readonly Dictionary<string, string[]> _textureVariants =
            new(System.StringComparer.OrdinalIgnoreCase);

        // def_texture <base> <var0.tga> <var1.tga> … — register the variant list for a material.
        public void RegisterTextureVariants(string baseMaterial, string[] variantFiles)
        {
            if (string.IsNullOrEmpty(baseMaterial) || variantFiles == null || variantFiles.Length == 0)
                return;
            _textureVariants[baseMaterial] = variantFiles;
        }

        // How many base materials have registered variant lists (diagnostics).
        public int VariantRegistryCount => _textureVariants.Count;

        // Number of registered variants for a base material (0 if none registered).
        public int GetVariantCount(string baseMaterial)
            => !string.IsNullOrEmpty(baseMaterial) && _textureVariants.TryGetValue(baseMaterial, out var v) ? v.Length : 0;

        // Texture for variant `index` of `baseMaterial`. With variants registered, index maps
        // into the def_texture list (out-of-range → 0). With none, index 0 = the base material's
        // own texture. Returns null for "NULL"/missing (caller keeps the current texture).
        public Texture2D? GetVariantTexture(string baseMaterial, int index)
        {
            if (string.IsNullOrEmpty(baseMaterial)) return null;
            if (_textureVariants.TryGetValue(baseMaterial, out var vars) && vars.Length > 0)
            {
                if (index < 0 || index >= vars.Length) index = 0;
                var file = vars[index];
                if (string.IsNullOrEmpty(file) || file.Equals("NULL", System.StringComparison.OrdinalIgnoreCase))
                    return null;
                var name = file.EndsWith(".tga", System.StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(0, file.Length - 4) : file;
                return LoadTexture(name);
            }
            return index == 0 ? LoadTexture(baseMaterial) : null;
        }

        private Material[] GetMaterials(string dbName, ThreeDbModel db)
        {
            if (_dbMaterials.TryGetValue(dbName, out var cached)) return cached;
            var shader = DigglesShaders.Model;
            var mats = new Material[db.Materials.Count];
            for (int i = 0; i < db.Materials.Count; i++)
            {
                var src = db.Materials[i];
                var mat = new Material(shader) { name = $"{dbName}_{src.Name}" };
                var tex = LoadTexture(src.Name);
                if (tex != null)
                {
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                    else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                }
                var kind = _alphaKindCache.TryGetValue(src.Name, out var k) ? k : TgaAlphaKind.Opaque;
                ConfigureMaterialForAlphaKind(mat, kind);
                // GPU-instancing prototype (default off). When enabled, the Diggles/Model instancing
                // variant draws same-clip morph characters together with _F0/_F1/_Blend per-instance.
                // Off (default) leaves enableInstancing=false → faithful non-instanced MPB path.
                // See DigglesAnimator.UseInstancing. Materials are cached per-db, so this reads the
                // flag once at first build — set it before the world spawns.
                mat.enableInstancing = DigglesAnimator.UseInstancing;
                mats[i] = mat;
            }
            _dbMaterials[dbName] = mats;
            return mats;
        }

        private static Material[] BuildSubmeshMaterials(ThreeDbModel db, int meshIdx, Material[] mats)
        {
            if (meshIdx < 0 || meshIdx >= db.Meshes.Count || mats.Length == 0)
                return System.Array.Empty<Material>();
            var mesh = db.Meshes[meshIdx];
            var arr = new Material[mesh.SubMeshes.Count];
            for (int i = 0; i < mesh.SubMeshes.Count; i++)
            {
                int idx = mesh.SubMeshes[i].MaterialIndex;
                arr[i] = idx >= 0 && idx < mats.Length ? mats[idx] : mats[0];
            }
            return arr;
        }

        private Texture2D? LoadTexture(string materialName)
        {
            if (_textureCache.TryGetValue(materialName, out var cached)) return cached;
            // Search mod roots first, then DataRoot (asset indirection); within each root walk
            // the LOD folders. With no mod roots this is the original DataRoot/Texture search.
            foreach (var root in Assets.Roots)
            {
                var textureDir = Path.Combine(root, "Texture");
                if (!Directory.Exists(textureDir)) continue;
                foreach (var folder in LodFolders)
                {
                    var subDir = Path.Combine(textureDir, folder);
                    if (!Directory.Exists(subDir)) continue;
                    var candidate = Path.Combine(subDir, materialName + ".tga");
                    if (!File.Exists(candidate))
                    {
                        var match = Directory.GetFiles(subDir, materialName + ".*").FirstOrDefault();
                        if (match == null) continue;
                        candidate = match;
                    }
                    try
                    {
                        var r = TgaLoader.LoadFromFile(candidate);
                        if (r != null)
                        {
                            _textureCache[materialName] = r.Texture;
                            _alphaKindCache[materialName] = r.AlphaKind;
                            return r.Texture;
                        }
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"DigglesModelLibrary: TGA load failed '{candidate}': {e.Message}");
                    }
                }
            }
            return null;
        }

        private static void ConfigureMaterialForAlphaKind(Material mat, TgaAlphaKind kind)
        {
            float BlendSrcAlpha = (float)UnityEngine.Rendering.BlendMode.SrcAlpha;
            float BlendOneMinusSrcAlpha = (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
            // CullMode: Back (2) = single-sided. Solid models (Opaque/Cutout) cull
            // backfaces so interior faces (e.g. a mushroom cap's underside) don't
            // show through. Transparent FX stay double-sided (Off).
            const float CullBack = 2f, CullOff = 0f;
            switch (kind)
            {
                case TgaAlphaKind.Opaque:
                    SetF(mat, "_SrcBlend", 1f); SetF(mat, "_DstBlend", 0f);
                    SetF(mat, "_ZWrite", 1f); SetF(mat, "_AlphaToMask", 0f); SetF(mat, "_Cutoff", 0f);
                    SetF(mat, "_Cull", CullBack);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                    break;
                case TgaAlphaKind.Cutout:
                    SetF(mat, "_SrcBlend", 1f); SetF(mat, "_DstBlend", 0f);
                    SetF(mat, "_ZWrite", 1f); SetF(mat, "_AlphaToMask", 1f); SetF(mat, "_Cutoff", 0.05f);
                    SetF(mat, "_Cull", CullBack);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                    break;
                case TgaAlphaKind.Transparent:
                    SetF(mat, "_SrcBlend", BlendSrcAlpha); SetF(mat, "_DstBlend", BlendOneMinusSrcAlpha);
                    SetF(mat, "_ZWrite", 0f); SetF(mat, "_AlphaToMask", 0f); SetF(mat, "_Cutoff", 0f);
                    SetF(mat, "_Cull", CullOff);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    break;
            }
        }

        private static void SetF(Material mat, string prop, float v)
        {
            if (mat.HasProperty(prop)) mat.SetFloat(prop, v);
        }
    }
}
