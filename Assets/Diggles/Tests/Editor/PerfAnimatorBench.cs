#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Diagnostics;
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    // Headless allocation + timing micro-benchmark for the per-entity animation path.
    //
    // Allocations are platform-independent — this measures the same GC pressure the Play
    // profiler attributes to DigglesAnimator.Update -> GC.Alloc, without a GPU. Prints
    // bytes-allocated-per-animator-per-frame and microseconds-per-animator-per-frame for the
    // morph (VAT) path (Interpolate=true) and the swap path (Interpolate=false), in Loop and
    // Still modes, so a perf fix can be measured before/after (grep the log for [PERFBENCH]).
    //
    // [Explicit] — not part of the pass/fail suite; run by name filter:
    // Unity.exe -batchmode -nographics -projectPath <p> -runTests -testPlatform EditMode
    // -testFilter "Diggles.Tests.PerfAnimatorBench" -testResults <xml> -logFile <log>
    public sealed class PerfAnimatorBench
    {
        const int N = 200;   // animators — a "several caves dug" crowd
        const int M = 300;   // ticks (render frames)

        readonly System.Collections.Generic.List<UnityEngine.Object> _trash = new();

        [TearDown]
        public void TearDown()
        {
            DigglesAnimator.PublishTimeScale(1.0);
            foreach (var o in _trash) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _trash.Clear();
        }

        Mesh NewMesh(string name)
        {
            var m = new Mesh { name = name };
            m.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            m.triangles = new[] { 0, 1, 2 };
            m.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            _trash.Add(m);
            return m;
        }

        DigglesAnimClip MakeClip(string name, int frames, bool morph)
        {
            var meshes = new Mesh[frames];
            for (int i = 0; i < frames; i++) meshes[i] = NewMesh($"{name}_f{i}");
            var clip = new DigglesAnimClip { Name = name, Frames = meshes };
            if (morph)
            {
                clip.BaseMesh = NewMesh($"{name}_base");
                var vat = new Texture2D(frames, frames, TextureFormat.RGBAFloat, false);
                _trash.Add(vat);
                clip.Vat = vat;
                clip.VatFrameCount = frames;
            }
            return clip;
        }

        DigglesAnimator NewAnimator(bool morph, string clipName, DigglesAnimator.AnimMode mode, int startFrame)
        {
            var go = new GameObject("perf_anim");
            _trash.Add(go);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            var anim = go.AddComponent<DigglesAnimator>();
            anim.Interpolate = morph;
            anim.AddClip(MakeClip(clipName, 8, morph));
            anim.SetAnim(clipName, startFrame, mode);
            return anim;
        }

        void Bench(string label, bool morph, DigglesAnimator.AnimMode mode, int startFrame)
        {
            var anims = new DigglesAnimator[N];
            for (int i = 0; i < N; i++) anims[i] = NewAnimator(morph, "clip", mode, startFrame);

            // Warm up (JIT + first-frame setup) then settle GC so the sample is steady-state.
            for (int f = 0; f < 10; f++) for (int i = 0; i < N; i++) anims[i].TickClock(1f / 60f);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            // GC.GetTotalAllocatedBytes isn't in this Mono runtime. Robust proxy: gen0 collection
            // count delta (exact, noise-free — 0 collections over 60k ticks means the path is
            // alloc-free; >0 means it allocates) + the GetTotalMemory heap delta as a byte
            // estimate (exact when no collection fired mid-run, else a lower bound).
            int gc0 = GC.CollectionCount(0);
            long m0 = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < M; f++)
                for (int i = 0; i < N; i++)
                    anims[i].TickClock(1f / 60f);
            sw.Stop();
            long m1 = GC.GetTotalMemory(false);
            int gen0 = GC.CollectionCount(0) - gc0;

            long ticks = M * (long)N;
            long heapDelta = m1 - m0;
            double bytesPerTick = gen0 == 0 ? (double)heapDelta / ticks : double.NaN;   // exact only if no GC fired
            double usPerTick = sw.Elapsed.TotalMilliseconds * 1000.0 / ticks;
            string bpt = gen0 == 0 ? $"{bytesPerTick,7:F2} B/anim/frame" : $"~{(double)heapDelta / ticks,6:F1}+ B (GC fired {gen0}x)";
            Debug.Log($"[PERFBENCH] {label,-11} N={N} M={M} : gen0={gen0,3} | {bpt} | {usPerTick,7:F4} us/anim/frame");
        }

        [Test, Explicit("perf")] public void Animator_MorphLoop()  => Bench("MorphLoop",  true,  DigglesAnimator.AnimMode.Loop,  0);
        [Test, Explicit("perf")] public void Animator_MorphStill() => Bench("MorphStill", true,  DigglesAnimator.AnimMode.Still, 3);
        [Test, Explicit("perf")] public void Animator_SwapLoop()   => Bench("SwapLoop",   false, DigglesAnimator.AnimMode.Loop,  0);
        [Test, Explicit("perf")] public void Animator_SwapStill()  => Bench("SwapStill",  false, DigglesAnimator.AnimMode.Still, 3);
    }
}
