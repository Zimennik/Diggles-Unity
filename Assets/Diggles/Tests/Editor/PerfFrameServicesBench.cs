#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using Diggles.Runtime;
    using Diggles.Runtime.Particles;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    // Headless bench for the per-frame Play services. Play data: steady 33-44 ms CPU-bound
    // frames with sim at 0.3 ms and 0.1-1 MB gcAlloc every frame — the cost lives in the
    // per-frame main-thread services outside the sim. Each test pumps one service's real
    // per-frame entry point N times over ~700 real entities (booted through the actual
    // class scripts) and prints [PERFBENCH-FRAME] µs/call + GC.CollectionCount(0) delta.
    //
    // Private entry points are reached via bound delegates, so this file compiles unchanged
    // against both the pre-fix and post-fix trees — baseline vs fixed = run it on each.
    //
    // [Explicit] — run on demand:
    // Unity.exe -batchmode -nographics -projectPath <p> -runTests -testPlatform EditMode
    // -testFilter "Diggles.Tests.PerfFrameServicesBench" -testResults <xml> -logFile <log> (no -quit)
    [Explicit("perf")]
    public sealed class PerfFrameServicesBench
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        const int Gnomes = 20, Items = 680;   // ≈700 total — a representative Play population

        TclInterpreter _interp = null!;
        DigglesTclContext _ctx = null!;
        GameObject _worldRoot = null!;
        Mesh _entityMesh = null!;
        readonly List<GameObject> _cleanup = new();

        // ------------------------------------------------------------------ fixture

        [OneTimeSetUp]
        public void SpawnWorld()
        {
            _interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            _interp.UnknownCommandHandler = (i, a) => "0";
            _ctx = DigglesTclEngine.Register(_interp);
            _ctx.DataRoot = DataRoot;
            _ctx.TolerateMissingEntities = true;
            _ctx.TolerateMissingClass = true;
            _ctx.TolerateMissingFiles = true;
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { _interp.Eval("call " + s); }
                catch (Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }

            for (int i = 0; i < Gnomes; i++)
                _interp.Eval($"new Zwerg \"\" \"{20 + (i % 10)} {30 + (i / 10)} 8\" \"0 0 0\"");
            for (int i = 0; i < Items; i++)
                _interp.Eval($"new Stein \"\" \"{20 + (i % 40)} {28 + ((i / 40) % 16)} 8\" \"0 0 0\"");

            // Headless boot has no EntitySpawner — attach the outer/Model GameObject pair the
            // Play spawner would create (root + child MeshFilter), which is what every audited
            // per-frame consumer (aura hull, particle objverts, hover pick) actually touches.
            _worldRoot = new GameObject("BenchWorldRoot");
            _entityMesh = MakeVertexCloud(2400);   // dwarf-frame-sized vertex count
            foreach (var e in _ctx.Entities.Values)
            {
                var go = new GameObject("ent" + e.Handle);
                go.transform.SetParent(_worldRoot.transform, false);
                var model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                model.AddComponent<MeshFilter>().sharedMesh = _entityMesh;
                e.GameObject = go;
            }
            _ctx.SpawnParent = _worldRoot.transform;
            _ctx.Particles ??= new ParticleSystemState();
            TestContext.WriteLine($"[PERFBENCH-FRAME] world: {_ctx.Entities.Count} real entities");
        }

        [OneTimeTearDown]
        public void Teardown()
        {
            foreach (var go in _cleanup) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            if (_worldRoot != null) UnityEngine.Object.DestroyImmediate(_worldRoot);
            var particles = GameObject.Find("CpuParticles");   // the presenter's lazily-created holder
            if (particles != null) UnityEngine.Object.DestroyImmediate(particles);
        }

        GameObject Track(GameObject go) { _cleanup.Add(go); return go; }

        static Mesh MakeVertexCloud(int n)
        {
            var v = new Vector3[n];
            var rng = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                var d = new Vector3((float)rng.NextDouble() * 2f - 1f,
                                    (float)rng.NextDouble() * 2f - 1f,
                                    (float)rng.NextDouble() * 2f - 1f);
                v[i] = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.up;
            }
            var m = new Mesh { name = "BenchCloud", vertices = v };
            m.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0);
            return m;
        }

        // Alloc metric: bytes allocated on this thread if the runtime exposes it (Unity Mono
        // does), else GC.GetTotalMemory — the post-classinit Boehm heap is big enough that
        // gen0 collection counts stay 0 even under MB/frame churn, so bytes are the signal.
        static readonly Func<long>? AllocBytes = CreateAllocBytes();
        static Func<long>? CreateAllocBytes()
        {
            var mi = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",
                BindingFlags.Static | BindingFlags.Public);
            if (mi == null) return null;
            var f = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), mi);
            // Probe: Unity Mono ships the API but returns a constant on some backends — verify
            // it actually moves across a known allocation, else fall back to GetTotalMemory.
            long a = f();
            var junk = new byte[64 * 1024];
            long b = f();
            GC.KeepAlive(junk);
            return b > a ? f : null;
        }

        static (double us, int gen0, double bytesPerCall) Measure(int iters, Action body)
        {
            body();   // warm-up (lazy caches, JIT)
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int gc0 = GC.CollectionCount(0);
            long b0 = AllocBytes?.Invoke() ?? GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++) body();
            sw.Stop();
            long b1 = AllocBytes?.Invoke() ?? GC.GetTotalMemory(false);
            return (sw.Elapsed.TotalMilliseconds * 1000.0 / iters,
                    GC.CollectionCount(0) - gc0,
                    Math.Max(0, b1 - b0) / (double)iters);
        }

        // ------------------------------------------------------------------ 1. aura band

        // AuraBandRenderer rebuilds the selection+hover hull band per camera render per frame.
        // Pre-fix: 2× GetComponentInChildren + Mesh.vertices (a fresh Vector3[] copy) per slot
        // per call — the measured 0.1-1 MB/frame Play allocator.
        [Test]
        public void AuraBand_BuildBands()
        {
            var camGo = Track(new GameObject("benchCam"));
            var cam = camGo.AddComponent<Camera>();
            camGo.transform.position = new Vector3(0f, 0f, -10f);   // both roots sit at origin

            var sel = FirstEntityGo(0);
            var hov = FirstEntityGo(1);
            var abGo = Track(new GameObject("benchAura"));
            var ab = abGo.AddComponent<AuraBandRenderer>();
            ab.SetSlotDirect(0, sel, new Color(1f, 1f, 1f, 0.85f));
            ab.SetSlotDirect(1, hov, new Color(0f, 1f, 0f, 0.75f));

            int tris = ab.BuildBands(cam);
            Assert.Greater(tris, 0, "band geometry must actually build (else the bench measures nothing)");

            var (us, gen0, bytes) = Measure(2000, () => ab.BuildBands(cam));
            Debug.Log($"[PERFBENCH-FRAME] auraband 2 slots @{_entityMesh.vertexCount} verts : {us:F1} us/build | {bytes / 1024.0:F1} KB/build | gen0={gen0} (2000 builds)");
        }

        GameObject FirstEntityGo(int skip)
        {
            foreach (var e in _ctx.Entities.Values)
            {
                if (skip-- > 0) continue;
                return e.GameObject!;
            }
            throw new InvalidOperationException("no entities");
        }

        // ------------------------------------------------------------------ 2. hover pick lookup

        // SelectionAuraService resolves collider→entity per raycast hit per frame. Pre-fix: a
        // linear O(N) scan of ctx.Entities per call. Worst case = the LAST registered entity.
        [Test]
        public void SelectionAura_FindEntityByGameObject()
        {
            var svc = new SelectionAuraService(_ctx);
            var mi = typeof(SelectionAuraService).GetMethod("FindEntityByGameObject",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(mi, "FindEntityByGameObject not found (renamed?)");
            var find = (Func<GameObject, DigglesTclEntity?>)Delegate.CreateDelegate(
                typeof(Func<GameObject, DigglesTclEntity?>), svc, mi!);

            GameObject? last = null;
            foreach (var e in _ctx.Entities.Values) last = e.GameObject;
            Assert.IsNotNull(find(last!), "lookup must resolve the entity");

            var (us, gen0, bytes) = Measure(20000, () => find(last!));
            Debug.Log($"[PERFBENCH-FRAME] hoverlookup ents={_ctx.Entities.Count} : {us:F2} us/call | {bytes:F0} B/call | gen0={gen0} (20000 calls)");
        }

        // ------------------------------------------------------------------ 3. objverts spawn

        // CpuParticleEmitter MeshVerts (objverts) spawning samples the owner mesh per spawned
        // particle (MeshVertOffset). Pre-fix: Mesh.vertices allocated a fresh ~28 KB Vector3[]
        // copy per particle. Note: the shipped type table reaches this only through the legacy
        // template path (setId 6's faithful Step6 point-spawns), so the bench builds a legacy
        // MeshVerts type explicitly — the exact code path the fix touches.
        [Test]
        public void Emitter_MeshVertsSpawn()
        {
            var emGo = Track(new GameObject("benchEmitter"));
            var em = emGo.AddComponent<CpuParticleEmitter>();
            var owner = FirstEntityGo(2);
            var type = new ParticleType
            {
                SetId = 41, Tex = ParticleTex.A128d, RenderMode = 0x0f, Alpha = 255, AtlasGrid = 16,
                Shape = SpawnShape.MeshVerts, CountMode = SpawnCount.Single, StartSize = 2f, AgeCap = 16,
            };
            var src = new ParticleSystemState.Source { Type = 41, MaxParticles = 128, NumPerFrame = 1, Active = true };
            em.Configure(type, src, owner.transform, _entityMesh, null);
            em.SetWorldPos(Vector3.zero);
            em.SetActive(true);

            var (us, gen0, bytes) = Measure(3000, () => em.StepFrames(1));
            Debug.Log($"[PERFBENCH-FRAME] objverts-spawn @{_entityMesh.vertexCount}-vert owner : {us:F2} us/step | {bytes / 1024.0:F1} KB/step | gen0={gen0} (3000 steps)");
        }

        // ------------------------------------------------------------------ 4. particle presenter

        // ParticlePresenter's per-source per-frame resolve path (dt = 0 ⇒ no integrator steps —
        // isolates exactly the work that runs for every source every frame regardless of
        // simulation). Pre-fix: entity dict probes + GetComponentInChildren (MeshVerts types) +
        // a material SetFloat per source per frame.
        [Test]
        public void Presenter_PerSourceResolve()
        {
            var presGo = Track(new GameObject("benchParticles"));
            var pres = presGo.AddComponent<ParticlePresenter>();
            pres.Construct(_ctx);
            var camGo = Track(new GameObject("benchPCam"));
            camGo.AddComponent<Camera>();   // Cam fallback: Camera.allCameras[0]

            int k = 0;
            foreach (var e in _ctx.Entities.Values)
            {
                if (k >= 24) break;
                var s = _ctx.Particles!.Configure(e.Handle, 0, (k % 3 == 0) ? 6 : 0,
                    Vector3.zero, Vector3.up, 32, 1, true, -1, -1);
                s.Active = true;
                k++;
            }

            // Post-fix seam = public Pump(float); pre-fix tree = private LateUpdate.
            Action<float> drive;
            var pump = typeof(ParticlePresenter).GetMethod("Pump", BindingFlags.Instance | BindingFlags.Public);
            if (pump != null)
            {
                drive = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), pres, pump);
            }
            else
            {
                var lu = typeof(ParticlePresenter).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(lu, "neither Pump nor LateUpdate found");
                var d = (Action)Delegate.CreateDelegate(typeof(Action), pres, lu!);
                drive = _ => d();   // EditMode Time.deltaTime ≈ 0 ⇒ steps=0, same as Pump(0)
            }

            // Edit mode never calls MonoBehaviour.Awake on AddComponent, so each emitter the
            // presenter lazily creates has an uninitialized mesh/renderer and its first Render
            // throws (aborting the pump after creating one emitter). Pump-and-catch until all
            // sources have emitters, reflectively Awake-ing each new one — no-op on a tree
            // whose emitters tolerate headless creation.
            var awake = typeof(CpuParticleEmitter).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            var meshField = typeof(CpuParticleEmitter).GetField("_mesh", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 30; i++)
            {
                try { drive(0f); }
                catch (Exception) { /* headless pre-Awake emitter — initialized below */ }
                foreach (var e2 in UnityEngine.Object.FindObjectsByType<CpuParticleEmitter>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (meshField?.GetValue(e2) == null) awake?.Invoke(e2, null);
            }

            var (us, gen0, bytes) = Measure(2000, () => drive(0f));
            Debug.Log($"[PERFBENCH-FRAME] presenter 24 sources : {us:F1} us/pump | {bytes:F0} B/pump | gen0={gen0} (2000 pumps)");
        }

        // ------------------------------------------------------------------ 5. subtitles

        // SubtitleView.Render runs every frame from ScreenManager while speech is up. Pre-fix:
        // full rich-text rebuild (2 StringBuilders + hex ToStrings) + a TMP text re-assign per
        // frame with no change gate. (Headless: the TMP re-layout half doesn't run — the
        // measured delta is the string-build half only; Play saves more.)
        [Test]
        public void Subtitle_RenderWhileSpeaking()
        {
            var go = Track(new GameObject("benchSubtitle"));
            var view = go.AddComponent<SubtitleView>();
            var labelGo = Track(new GameObject("benchLabel"));
            var label = labelGo.AddComponent<TMPro.TextMeshPro>();
            typeof(SubtitleView).GetField("label", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(view, label);

            var d = new DialogueState();
            d.SetSpeechOut(3600f, new List<(Color32, string)>
            {
                (new Color32(255, 200, 100, 255), "Something is stirring under the meadow tonight..."),
                (new Color32(120, 220, 255, 255), "Keep digging - the vein cannot be far now."),
            }, false);

            var (us, gen0, bytes) = Measure(5000, () => view.Render(d));
            Debug.Log($"[PERFBENCH-FRAME] subtitle render (speech up) : {us:F2} us/frame | {bytes:F0} B/frame | gen0={gen0} (5000 frames)");
        }
    }
}
