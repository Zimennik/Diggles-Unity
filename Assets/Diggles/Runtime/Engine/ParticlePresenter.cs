#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Particles;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Drives the engine's particle sources (ParticleSystemState, fed by the Tcl change/create/blow/
    // free_particlesource commands) through a faithful CPU particle engine (CpuParticleEmitter), with
    // Unity used ONLY as an additive billboard renderer. Each persistent Source gets one emitter that
    // simulates in world space and rebuilds its billboard mesh each frame; temps (create_particlesource
    // / obj_to_particle) become one-shot bursts. Stepped at a fixed frame cadence (fpsRef) so the
    // per-frame motion/colour/lifetime constants from the original integrate exactly. Runs in BOTH
    // gameplay and cutscenes. Scene component, injected with the context, registered via the installer.
    public sealed class ParticlePresenter : MonoBehaviour, IStartable, IRuntimeSessionPresentationParticipant
    {
        [Header("Calibration (tune in Play)")]
        [Tooltip("Frames/second the CPU integrator is stepped at. The original ran per render-frame; its " +
                 "velocity/gravity/lifetime constants are per-frame, so this scales them to wall-clock.")]
        [SerializeField] float fpsRef = 12f;
        [Tooltip("Global billboard-size scale: native particle size (0.4..8.0) → world units.")]
        [SerializeField] float sizeScale = 0.25f;
        [Tooltip("Particle additive brightness (atlases are dim by design).")]
        [SerializeField] float intensity = 1.4f;
        [Tooltip("Flip atlas V if sprites read from the wrong row (texture orientation).")]
        [SerializeField] bool atlasVFlip;
        [Tooltip("Atlas cell sampling width (fraction of the cell). Original = 0.875 but it crops full-cell " +
                 "sprites; 1.0 = full cell (no crop).")]
        [SerializeField] float atlasInset = 1.0f;
        [SerializeField] bool debugLog;

        DigglesTclContext? _ctx;

        [UnityEngine.Scripting.Preserve]
        [Inject]
        public void Construct(IActiveGameSession sessions) => BindSession(sessions.Current);

        // Headless particle tools and benchmarks own a fixed context without a scene DI scope.
        public void Construct(DigglesTclContext context) => BindContext(context);

        // Per-source render view: the transform/mesh/material/cell-map used to
        // be re-resolved for EVERY source EVERY frame (incl. a GetComponentInChildren walk and a
        // material SetFloat). Cached here; re-resolved only when the source object, its type, or
        // the owner GameObject actually changes.
        sealed class SourceView
        {
            public CpuParticleEmitter Em = null!;
            public ParticleSystemState.Source? Src;   // last-seen source (ref identity)
            public int Type = -1;                     // last-seen src.Type
            public GameObject? EntityGo;              // last-resolved owner GO (respawn detection)
            public Transform? EntityTf;
            public MeshFilter? EntityMf;              // only resolved for MeshVerts-shaped types
            public bool[]? CellMap;
            public Material? Mat;
        }

        readonly Dictionary<(int obj, int idx), SourceView> _views = new();
        readonly Dictionary<ParticleTex, Texture2D?> _textures = new();
        readonly Dictionary<(ParticleTex, int), Material> _materials = new();
        readonly Dictionary<(ParticleTex, int), bool[]> _cellMaps = new();
        readonly List<(CpuParticleEmitter c, float die)> _temps = new();
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        Transform? _root;
        Camera? _cam;
        float _accum, _logAccum, _appliedIntensity = float.NaN;

        public void BindSession(RuntimeGameSession session)
            => BindContext(session.Engine.Ctx);

        void BindContext(DigglesTclContext context)
        {
            ClearRuntimeEmitters();
            _ctx = context ?? throw new System.ArgumentNullException(nameof(context));
            _cam = null;
            _accum = 0f;
            _logAccum = 0f;
        }

        void ClearRuntimeEmitters()
        {
            foreach (SourceView view in _views.Values)
                DestroyPresentationObject(view.Em != null ? view.Em.gameObject : null);
            _views.Clear();
            foreach (var temp in _temps)
                DestroyPresentationObject(temp.c != null ? temp.c.gameObject : null);
            _temps.Clear();
        }

        static void DestroyPresentationObject(GameObject? value)
        {
            if (value == null) return;
            value.SetActive(false);
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        void IStartable.Start() { }

        Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("CpuParticles");
                    _root = go.transform;            // world-identity holder; emitters build world-space meshes
                }
                return _root;
            }
        }

        Camera? Cam
        {
            get
            {
                if (_cam == null) _cam = Camera.main ?? (Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null);
                return _cam;
            }
        }

        void LateUpdate() => Pump(Time.deltaTime);

        // Public so the headless perf bench / EditMode tests can drive the presenter without a
        // player loop (same seam pattern as FogOfWarPresenter.Pump).
        public void Pump(float dt)
        {
            if (_ctx?.Particles == null) return;
            var ps = _ctx.Particles;

            // Fixed-step the integrator at fpsRef; clamp to avoid spikes after a stall.
            _accum += dt;
            int steps = Mathf.Clamp(Mathf.FloorToInt(_accum * fpsRef), 0, 4);
            _accum -= steps / Mathf.Max(1f, fpsRef);
            // Fraction of the way to the next sim step → render-time interpolation (smooths the 12fps sim).
            float interp = Mathf.Clamp01(_accum * fpsRef);

            var cam = Cam;

            // Live inspector tweak of the brightness — pushed to the shared materials only when
            // the value changes (this was a per-source per-frame SetFloat inside MaterialFor).
            if (_appliedIntensity != intensity)
            {
                _appliedIntensity = intensity;
                foreach (var m in _materials.Values)
                    if (m != null) m.SetFloat(IntensityId, intensity);
            }

            // Persistent per-object sources.
            foreach (var kv in ps.Sources)
            {
                var src = kv.Value;
                var type = ParticleTypeTable.Get(src.Type);
                if (type.Dispatch == DispatchKind.Inert) continue;

                if (!_views.TryGetValue(kv.Key, out var view) || view.Em == null)
                {
                    view = new SourceView { Em = NewEmitter($"p_{kv.Key.obj}_{kv.Key.idx}_set{src.Type}") };
                    _views[kv.Key] = view;
                }
                var em = view.Em;

                // Owner GameObject — probed each frame (it can die/respawn independently).
                GameObject? go = null;
                if (_ctx != null && _ctx.Entities.TryGetValue(kv.Key.obj, out var ent) && ent.GameObject != null)
                    go = ent.GameObject;

                // Re-resolve the cached view only on a real change (source replaced, type
                // changed, owner GO changed, or a MeshVerts source whose MeshFilter died).
                if (!ReferenceEquals(view.Src, src) || view.Type != src.Type
                    || !ReferenceEquals(view.EntityGo, go)
                    || (type.Shape == SpawnShape.MeshVerts && view.EntityMf == null && go != null))
                {
                    view.Src = src;
                    view.Type = src.Type;
                    view.EntityGo = go;
                    view.EntityTf = go != null ? go.transform : null;
                    view.EntityMf = type.Shape == SpawnShape.MeshVerts && go != null
                        ? go.GetComponentInChildren<MeshFilter>() : null;
                    view.CellMap = CellMapFor(type.Tex, type.AtlasGrid);
                    view.Mat = MaterialFor(type.Tex, type.RenderMode);
                }

                // relpos is in NATIVE coords (relative to the object), NOT world. Map it through the same
                // native→world transform as positions (native z = UP, the WorldRoot Q_x(-90) hack) by
                // round-tripping the entity's world pos to native, adding relpos there, and re-mapping.
                // Adding it raw put e.g. a torch's vertical {0,0,h} into +Z ("in the wall") and the elf's
                // {0,-6,0} 6 units underground (→ invisible).
                // WorldAnchored sources (elf hand sparkles) carry an ABSOLUTE native position in RelPos
                // (their owner is not in ctx.Entities and re-aims RelPos every frame) — use it directly.
                Vector3 entityWorldPos = view.EntityTf != null ? view.EntityTf.position : WorldRoot.position;
                Vector3 srcNative = src.WorldAnchored
                    ? src.RelPos
                    : RuntimeWorldSpawner.LocalToNative(WorldRoot.InverseTransformPoint(entityWorldPos)) + src.RelPos;
                em.SetWorldPos(NativeWorldPos(srcNative));
                // The LIVE display mesh (frame meshes swap under the same MeshFilter at 10 FPS;
                // the emitter ref-compares and re-copies its vertex cache only on a real swap).
                Mesh? liveMesh = view.EntityMf != null ? view.EntityMf.sharedMesh : null;
                em.Configure(type, src, view.EntityTf, liveMesh, view.CellMap);
                // FOG GATE (native per-type emitter prologue FUN_004a1c50:48-77 + twins): an
                // emitter with is_vis_in_fog == 0 (change_particlesource a[8] / native emitter
                // byte+0x64) skips SPAWNING unless the fog around it is ≥ 0x81 on the local
                // lane; already-alive particles keep integrating (SetActive only gates spawn).
                // PORT SIMPLIFICATION (documented): the native checks the 4 tiles around the
                // position — we sample the single owner tile. Runs through cutscenes too
                // (faithful —); only show_fogofwar 0 / -nofow lift the gate.
                bool emit = src.Active;
                if (emit && !src.VisibleInFog && _ctx != null)
                {
                    var fog = _ctx.FogOfWar;
                    if (fog != null && fog.Enabled && fog.ShowFog
                        && fog.FogAt(Mathf.RoundToInt(srcNative.x), Mathf.RoundToInt(srcNative.y),
                                     fog.LocalPlayer) < 0x81)
                        emit = false;
                }
                em.SetActive(emit);
                // blow_particlesource fold-and-zero (FUN_00498590:303-308): the accumulated wind
                // goes into the emitter's DECAYING drift once, then clears — a gust that fades,
                // not a permanent re-aim (the old Burst+Direction overwrite skewed fires forever).
                if (src.BlowImpulse != Vector3.zero) { em.AddImpulse(src.BlowImpulse); src.BlowImpulse = Vector3.zero; }
                em.StepFrames(steps);
                em.Render(cam, view.Mat, sizeScale, atlasVFlip, atlasInset, interp);
            }

            if (_views.Count > ps.Sources.Count) PruneEmitters(ps);

            // Temporary world-space sources (create_particlesource / obj_to_particle) → one-shot bursts.
            foreach (var t in ps.Temps)
            {
                if (t.Spawned) continue;
                t.Spawned = true;
                var type = ParticleTypeTable.Get(t.Type);
                if (type.Dispatch == DispatchKind.Inert) continue;
                var em = NewEmitter($"temp_set{t.Type}");
                em.SetWorldPos(NativeWorldPos(t.Pos));
                var fake = new ParticleSystemState.Source
                {
                    Type = t.Type, Direction = t.Direction,
                    MaxParticles = t.Num > 0 ? t.Num * 2 : 128, NumPerFrame = 0, Active = false,
                };
                em.Configure(type, fake, null, null, CellMapFor(type.Tex, type.AtlasGrid));
                // The SHATTER seam (CreateTempShatter): precomputed world-space model vertices
                // + tints — set 43 spawns its fragments ON them (CpuParticleEmitter.SpawnShatter).
                if (t.ShatterVerts != null) em.SetShatterSource(t.ShatterVerts, t.ShatterCols);
                em.Burst(t.Num > 0 ? t.Num : 16);
                _temps.Add((em, Time.time + Mathf.Max(0.5f, t.Remaining) + type.AgeCap / Mathf.Max(1f, fpsRef) + 1f));
            }
            for (int i = _temps.Count - 1; i >= 0; i--)
            {
                if (_temps[i].c == null) { _temps.RemoveAt(i); continue; }
                if (Time.time >= _temps[i].die) { Destroy(_temps[i].c.gameObject); _temps.RemoveAt(i); }
                else { var tt = ParticleTypeTable.Get(_temps[i].c.SetId); _temps[i].c.StepFrames(steps); _temps[i].c.Render(cam, MaterialFor(tt.Tex, tt.RenderMode), sizeScale, atlasVFlip, atlasInset, interp); }
            }

            if (debugLog && (_logAccum += dt) >= 1f)
            {
                _logAccum = 0f;
                int live = 0; foreach (var v in _views.Values) if (v.Em != null) live += v.Em.AliveCount;
                Debug.Log($"[Particles] sources={ps.Sources.Count} emitters={_views.Count} temps={_temps.Count} live={live} steps/f={steps}");
            }
        }

        CpuParticleEmitter NewEmitter(string emitterName)
        {
            var go = new GameObject(emitterName);
            go.transform.SetParent(Root, false);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            return go.AddComponent<CpuParticleEmitter>();
        }

        void PruneEmitters(ParticleSystemState ps)
        {
            var dead = new List<(int, int)>();
            foreach (var kv in _views) if (!ps.Sources.ContainsKey(kv.Key)) dead.Add(kv.Key);
            foreach (var k in dead) { if (_views[k].Em != null) Destroy(_views[k].Em.gameObject); _views.Remove(k); }
        }

        // ---- entity lookups -----------------------------------------------------------------------

        Transform WorldRoot => _ctx?.SpawnParent != null ? _ctx.SpawnParent! : transform;

        Vector3 NativeWorldPos(Vector3 nativePos) =>
            WorldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nativePos));

        // ---- materials / textures -----------------------------------------------------------------

        // One material per (atlas, renderMode). renderMode 0x10 = additive glow (fire/sparks/magic);
        // 0x0f = premultiplied-alpha (smoke/dust/debris) so overlapping particles composite instead of
        // blowing out to white — the cause of the "overbright smoke" feedback.
        Material? MaterialFor(ParticleTex tex, int renderMode)
        {
            var key = (tex, renderMode);
            if (_materials.TryGetValue(key, out var m) && m != null) return m;   // intensity re-push lives in Pump (on change only)
            var shader = DigglesShaders.Particle;
            m = new Material(shader!) { name = $"Particle_{tex}_{renderMode:x}" };
            var t = TextureFor(tex);
            if (t != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
            }
            m.SetFloat(IntensityId, intensity);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlend", (float)(renderMode == 0x10
                ? UnityEngine.Rendering.BlendMode.One
                : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _materials[key] = m;
            return m;
        }

        // Per-(texture,grid) map of which cells hold a sprite (vs blank gutter). Lets RandomCell skip
        // empty cells. v is indexed from the image TOP to match the render's cell convention. If the
        // atlas isn't CPU-readable, returns all-true (band targeting alone still avoids the digit rows).
        bool[]? CellMapFor(ParticleTex tex, int grid)
        {
            grid = Mathf.Max(1, grid);
            var key = (tex, grid);
            if (_cellMaps.TryGetValue(key, out var m)) return m;
            m = ComputeCellMap(TextureFor(tex), grid);
            _cellMaps[key] = m;
            return m;
        }

        static bool[] ComputeCellMap(Texture2D? t, int grid)
        {
            var arr = new bool[grid * grid];
            if (t == null || !t.isReadable)
            {
                for (int i = 0; i < arr.Length; i++) arr[i] = true;
                return arr;
            }
            var px = t.GetPixels32();
            int w = t.width, h = t.height, cw = Mathf.Max(1, w / grid), ch = Mathf.Max(1, h / grid);
            for (int v = 0; v < grid; v++)
                for (int u = 0; u < grid; u++)
                {
                    int y0 = (grid - 1 - v) * ch, x0 = u * cw;   // image-top row v → Unity y near top
                    int cover = 0;
                    for (int y = y0; y < y0 + ch && y < h; y++)
                        for (int x = x0; x < x0 + cw && x < w; x++)
                        {
                            var p = px[y * w + x];
                            if (p.a > 16 || p.r > 24 || p.g > 24 || p.b > 24) cover++;
                        }
                    arr[v * grid + u] = cover > cw * ch / 40;     // >2.5% covered = a real sprite
                }
            return arr;
        }

        Texture2D? TextureFor(ParticleTex tex)
        {
            if (_textures.TryGetValue(tex, out var t)) return t;
            t = LoadTexture(tex);
            _textures[tex] = t;
            return t;
        }

        Texture2D? LoadTexture(ParticleTex tex)
        {
            var root = _ctx?.DataRoot;
            if (string.IsNullOrEmpty(root)) return null;
            var path = Path.Combine(root, "Texture", "Misc", ParticleTexFiles.FileFor(tex));
            if (!File.Exists(path)) { Debug.LogWarning($"[Particles] missing texture {path}"); return null; }
            try { return TgaLoader.LoadFromFile(path)?.Texture; }
            catch (System.Exception e) { Debug.LogWarning($"[Particles] {ParticleTexFiles.FileFor(tex)}: {e.Message}"); return null; }
        }

        void OnDestroy()
        {
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
