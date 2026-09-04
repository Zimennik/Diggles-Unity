#nullable enable

namespace Diggles.Runtime.Diagnostics
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    // Buffered file logger for debugging the live sim without flooding the Unity
    // console (Debug.Log is slow enough at volume to freeze the editor). Lines
    // accumulate in memory and flush to a file in bulk. Targeted to a small set of
    // tracked entity handles so we can trace one gnome's state/anim flow.
    public static class DigglesDebugLog
    {
        public static bool Enabled;
        public static readonly HashSet<int> Tracked = new();

        // Hard cap on total lines so a per-command trace can't fill the disk / produce
        // an unreadable file. The first N lines capture plenty of the repeating pattern.
        public static int MaxLines = 6000;

        static readonly StringBuilder _buf = new();
        static string _path = "diggles_debug.log";
        static int _pending;
        static int _lines;
        static bool _capped;

        public static bool IsTracked(int handle) => Tracked.Contains(handle);

        public static void Begin(string path)
        {
            _path = path;
            _buf.Clear();
            _pending = 0;
            _lines = 0;
            _capped = false;
            Tracked.Clear();
            try { File.WriteAllText(_path, $"=== diggles debug log {DateTime.Now:HH:mm:ss} ===\n"); }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[DigglesDebugLog] open failed: {e.Message}"); }
            Enabled = true;
        }

        public static void Log(string msg)
        {
            if (!Enabled) return;
            if (_lines >= MaxLines)
            {
                if (!_capped) { _capped = true; _buf.Append($"=== capped at {MaxLines} lines ===\n"); Flush(); }
                return;
            }
            _lines++;
            _buf.Append(msg).Append('\n');
            if (++_pending >= 400) Flush();
        }

        public static void Flush()
        {
            if (!Enabled || _buf.Length == 0) return;
            try { File.AppendAllText(_path, _buf.ToString()); }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[DigglesDebugLog] flush failed: {e.Message}"); }
            _buf.Clear();
            _pending = 0;
        }
    }
}
