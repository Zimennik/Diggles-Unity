#nullable enable

namespace Diggles.Runtime.Diagnostics
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using UnityEngine;

    // Lightweight opt-in timing for the heavy synchronous ops that cause multi-second Play hitches
    // (worldgen generation, the cutscene-skip sim drain, terrain remesh). Logs `[perf] <label> <ms>`
    // only when an op exceeds its threshold, so a Play capture labels each freeze without a profiler
    // trace. Uses Stopwatch.GetTimestamp (a static long read — no allocation), so it's safe to call
    // on the per-step hot path: cost is two timestamp reads + a compare when the op is fast.
    public static class PerfTrace
    {
        public static bool Enabled = true;   // cheap; on by default to catch hitches in Play
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        public static long Now() => Stopwatch.GetTimestamp();
        public static double MsSince(long start) => (Stopwatch.GetTimestamp() - start) * MsPerTick;

        // Log a warning if the time since `start` exceeds thresholdMs.
        public static void LogIfSlow(string label, long start, double thresholdMs, string extra = "")
        {
            if (!Enabled) return;
            double ms = MsSince(start);
            if (ms >= thresholdMs)
                UnityEngine.Debug.LogWarning($"[perf] {label} {ms:F0} ms{(extra.Length > 0 ? " | " + extra : "")}");
        }

        // ---- named sections + freeze attribution ---------------------------------------------
        //
        // A Section marks a potentially-heavy synchronous main-thread op with a constant label
        // (no allocation). Two consumers:
        // * the freeze watchdog (PerfHud's background thread) reads the active label while the
        // main thread is stuck, so a multi-second freeze is named live even if it never
        // returns to the frame loop;
        // * every completed section that exceeded its threshold logs `[perf] <label> <ms>`,
        // and the slowest section of each frame is published for the slow-frame logger.
        // Nesting is supported (the scope restores the outer label on Dispose). The label/start
        // pair is read cross-thread without a lock — a torn read can only mis-time a diagnostic
        // message, never affect the game.
        static string? s_secLabel;
        static long s_secStart;
        static string? s_frameMaxLabel;
        static double s_frameMaxMs;
        static long s_heartbeat = Stopwatch.GetTimestamp();

        public static string? ActiveSection => s_secLabel;
        public static double ActiveSectionMs { get { var l = s_secLabel; return l != null ? MsSince(s_secStart) : 0.0; } }

        // The slowest completed section since the last ResetFrame (PerfHud prints it on slow frames).
        public static string? FrameMaxSection => s_frameMaxLabel;
        public static double FrameMaxSectionMs => s_frameMaxMs;

        // Called once per frame by PerfHud.Update: stamps the main-thread heartbeat the watchdog
        // polls, and resets the per-frame slowest-section slot.
        public static void Heartbeat()
        {
            s_heartbeat = Stopwatch.GetTimestamp();
            s_frameMaxLabel = null;
            s_frameMaxMs = 0.0;
        }

        public static double MsSinceHeartbeat() => (Stopwatch.GetTimestamp() - s_heartbeat) * MsPerTick;

        public readonly struct SectionScope : System.IDisposable
        {
            readonly string _label;
            readonly string? _prevLabel;
            readonly long _prevStart;
            readonly long _start;
            readonly double _threshold;

            internal SectionScope(string label, double thresholdMs, string? prevLabel, long prevStart, long start)
            {
                _label = label; _threshold = thresholdMs;
                _prevLabel = prevLabel; _prevStart = prevStart; _start = start;
            }

            public void Dispose()
            {
                s_secLabel = _prevLabel;
                s_secStart = _prevStart;
                double ms = MsSince(_start);
                if (ms > s_frameMaxMs) { s_frameMaxMs = ms; s_frameMaxLabel = _label; }
                if (Enabled && ms >= _threshold)
                    UnityEngine.Debug.LogWarning($"[perf] {_label} {ms:F0} ms");
            }
        }

        // `using var _ = PerfTrace.Section("model.buildclip");` — label MUST be a constant string
        // (no interpolation) so the hot path stays allocation-free.
        public static SectionScope Section(string label, double logThresholdMs = 50)
        {
            long now = Stopwatch.GetTimestamp();
            var scope = new SectionScope(label, logThresholdMs, s_secLabel, s_secStart, now);
            s_secLabel = label;
            s_secStart = now;
            return scope;
        }
    }

    public sealed class HitchFamilyTimingSample
    {
        public long Calls { get; internal set; }
        public long Units { get; internal set; }
        public double TotalMs { get; internal set; }
        public double MaxMs { get; internal set; }
    }

    public static class HitchFamilyTimingDiag
    {
        static readonly Dictionary<string, HitchFamilyTimingSample> s_samples = new();

        public static bool CaptureTimingDiag;
        public static IReadOnlyDictionary<string, HitchFamilyTimingSample> Samples => s_samples;

        public static long Start() => CaptureTimingDiag ? PerfTrace.Now() : 0;

        public static void Record(string phase, long start, long units = 0)
        {
            if (!CaptureTimingDiag) return;
            double elapsedMs = PerfTrace.MsSince(start);
            if (!s_samples.TryGetValue(phase, out HitchFamilyTimingSample? sample))
            {
                sample = new HitchFamilyTimingSample();
                s_samples.Add(phase, sample);
            }
            sample.Calls++;
            sample.Units += units;
            sample.TotalMs += elapsedMs;
            if (elapsedMs > sample.MaxMs) sample.MaxMs = elapsedMs;
        }

        public static void Count(string phase, long units = 1)
        {
            if (!CaptureTimingDiag) return;
            if (!s_samples.TryGetValue(phase, out HitchFamilyTimingSample? sample))
            {
                sample = new HitchFamilyTimingSample();
                s_samples.Add(phase, sample);
            }
            sample.Calls++;
            sample.Units += units;
        }

        public static void ResetTimingDiag() => s_samples.Clear();
    }
}
