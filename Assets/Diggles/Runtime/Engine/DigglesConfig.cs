#nullable enable

namespace Diggles.Runtime
{
    using System.IO;
    using UnityEngine;

    /// <summary>
    /// Ships-with-the-build, user-editable configuration for the original-game data
    /// path. The standalone build has no Inspector, so the serialized
    /// <see cref="DigglesWorldRuntime.dataRoot"/> default (a dev machine path) is useless
    /// to players — this loader lets them point the build at their own <c>data/</c> folder
    /// via a plain-text JSON file, with no recompile.
    ///
    /// Search order (first hit wins; a missing/invalid file is skipped, not fatal):
    /// 1. diggles_config.json next to the executable
    /// (Path.GetDirectoryName(Application.dataPath)) — lets a user drop a config
    /// beside the.exe that overrides the shipped one without touching the build.
    /// 2. StreamingAssets/diggles_config.json — the file that ships verbatim inside
    /// &lt;Build&gt;_Data/StreamingAssets/ (the default we author in the repo).
    /// 3. The serialized fallback passed in (Inspector value) — Editor + any build
    /// with no config keep working unchanged.
    ///
    /// Parsing is JsonUtility (no new deps); unknown fields like "_comment" are ignored.
    /// </summary>
    public static class DigglesConfig
    {
        [System.Serializable]
        private sealed class ConfigData
        {
            public string? _comment; // free-form; preserved across SaveTuning round-trips
            public string? dataRoot;
            public string? locale;   // optional; "en" (default) / "de" / "ru" / … — see ResolveLocale
            // Optional; underground darkness floor 0..1 (BaseLightPresenter.MinFactor —
            // Docs/TUNNEL_TORCH_LIGHTING_RE.md §8.5): 0 = native pitch-dark law, 1 = darkness
            // off. -1 (default) = "not set" → the Inspector/serialized value stands.
            public float baseLightMin = -1f;
            // Optional; the accumulated class-light (torch) brightness ceiling, 0..4
            // (RuntimeWorldSpawner.ClassLightCap): 1.0 = the native saturation law, >1 =
            // brighter-than-native stacking. -1 (default) = "not set" → Inspector value.
            public float classLightCap = -1f;
            // Optional; TCP port for the dev remote console bound to 127.0.0.1;
            // absent or <= 0 disables the service.
            public int remoteConsolePort;
        }

        const string FileName = "diggles_config.json";

        /// <summary>
        /// The data root from the config files (same search order as <see cref="ResolveDataRoot"/>),
        /// without boot logging. Empty when no config sets one. Used as the default for editor tools.
        /// </summary>
        public static string ConfiguredDataRoot
        {
            get
            {
                string? exeDir = null;
                try { exeDir = Path.GetDirectoryName(Application.dataPath); }
                catch { }
                if (!string.IsNullOrEmpty(exeDir) && TryLoad(Path.Combine(exeDir!, FileName), out var beside))
                    return beside;
                return TryLoad(Path.Combine(Application.streamingAssetsPath, FileName), out var shipped) ? shipped : "";
            }
        }

        /// <summary>
        /// Resolve the original-game data root, honouring an external config file if one
        /// exists. Never throws: on any error it logs and returns <paramref name="fallback"/>.
        /// Logs which source won so a mis-pointed build is diagnosable from the player log.
        /// </summary>
        public static string ResolveDataRoot(string fallback)
        {
            // 1) Beside the executable (build) / project root (editor) — highest priority.
            string? exeDir = null;
            try { exeDir = Path.GetDirectoryName(Application.dataPath); }
            catch { /* dataPath unavailable in some contexts — skip */ }
            if (!string.IsNullOrEmpty(exeDir))
            {
                var besideExe = Path.Combine(exeDir!, FileName);
                if (TryLoad(besideExe, out var v1))
                {
                    Debug.Log($"[DigglesConfig] dataRoot = \"{v1}\" (from config beside executable: {besideExe})");
                    return v1;
                }
            }

            // 2) StreamingAssets — the config that ships with the build.
            var streaming = Path.Combine(Application.streamingAssetsPath, FileName);
            if (TryLoad(streaming, out var v2))
            {
                Debug.Log($"[DigglesConfig] dataRoot = \"{v2}\" (from StreamingAssets config: {streaming})");
                return v2;
            }

            // 3) Serialized fallback.
            Debug.Log($"[DigglesConfig] dataRoot = \"{fallback}\" (no config file found; using serialized default)");
            return fallback;
        }

        /// <summary>
        /// Resolve the text locale ("en", "de", "ru", …) for the original data, from the same config
        /// file(s) as <see cref="ResolveDataRoot"/> (same search order). Optional field: absent →
        /// returns <paramref name="fallback"/>. It selects the "_[locale]" dialogue/subtitle text
        /// files and their byte encoding (ru = Windows-1251, else Latin-1). Never throws.
        /// </summary>
        public static string ResolveLocale(string fallback)
        {
            string? exeDir = null;
            try { exeDir = Path.GetDirectoryName(Application.dataPath); }
            catch { /* dataPath unavailable in some contexts — skip */ }
            if (!string.IsNullOrEmpty(exeDir))
            {
                var besideExe = Path.Combine(exeDir!, FileName);
                if (TryLoadLocale(besideExe, out var v1))
                {
                    Debug.Log($"[DigglesConfig] locale = \"{v1}\" (from config beside executable: {besideExe})");
                    return v1;
                }
            }

            var streaming = Path.Combine(Application.streamingAssetsPath, FileName);
            if (TryLoadLocale(streaming, out var v2))
            {
                Debug.Log($"[DigglesConfig] locale = \"{v2}\" (from StreamingAssets config: {streaming})");
                return v2;
            }

            Debug.Log($"[DigglesConfig] locale = \"{fallback}\" (no locale in config; using default)");
            return fallback;
        }

        /// <summary>
        /// Resolve the underground darkness floor (0..1) from the same config file(s) — the
        /// user-facing tuning knob for cave darkness. Optional:
        /// absent/invalid → <paramref name="fallback"/> (the Inspector value on the world root).
        /// </summary>
        public static float ResolveBaseLightMin(float fallback)
        {
            string? exeDir = null;
            try { exeDir = Path.GetDirectoryName(Application.dataPath); }
            catch { /* dataPath unavailable in some contexts — skip */ }
            if (!string.IsNullOrEmpty(exeDir)
                && TryLoadBaseLightMin(Path.Combine(exeDir!, FileName), out var v1))
            {
                Debug.Log($"[DigglesConfig] baseLightMin = {v1} (from config beside executable)");
                return v1;
            }
            if (TryLoadBaseLightMin(Path.Combine(Application.streamingAssetsPath, FileName), out var v2))
            {
                Debug.Log($"[DigglesConfig] baseLightMin = {v2} (from StreamingAssets config)");
                return v2;
            }
            return fallback;
        }

        static bool TryLoadBaseLightMin(string path, out float value)
        {
            value = -1f;
            try
            {
                if (!File.Exists(path)) return false;
                var data = JsonUtility.FromJson<ConfigData>(File.ReadAllText(path));
                if (data == null || data.baseLightMin < 0f) return false;   // optional
                value = Mathf.Clamp01(data.baseLightMin);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DigglesConfig] failed to read {path}: {e.Message} — ignoring.");
                return false;
            }
        }

        /// <summary>
        /// Resolve the loopback-only dev remote-console port from the same config file(s).
        /// The optional field is disabled when absent or non-positive, in which case
        /// <paramref name="fallback"/> is returned. Never throws.
        /// </summary>
        public static int ResolveRemoteConsolePort(int fallback)
        {
            string? exeDir = null;
            try { exeDir = Path.GetDirectoryName(Application.dataPath); }
            catch { /* dataPath unavailable in some contexts — skip */ }
            if (!string.IsNullOrEmpty(exeDir)
                && TryLoadRemoteConsolePort(Path.Combine(exeDir!, FileName), out var v1))
            {
                Debug.Log($"[DigglesConfig] remoteConsolePort = {v1} (from config beside executable)");
                return v1;
            }
            if (TryLoadRemoteConsolePort(Path.Combine(Application.streamingAssetsPath, FileName), out var v2))
            {
                Debug.Log($"[DigglesConfig] remoteConsolePort = {v2} (from StreamingAssets config)");
                return v2;
            }
            return fallback;
        }

        static bool TryLoadRemoteConsolePort(string path, out int value)
        {
            value = 0;
            try
            {
                if (!File.Exists(path)) return false;
                var data = JsonUtility.FromJson<ConfigData>(File.ReadAllText(path));
                if (data == null || data.remoteConsolePort <= 0) return false;
                value = data.remoteConsolePort;
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DigglesConfig] failed to read {path}: {e.Message} — ignoring.");
                return false;
            }
        }

        /// <summary>
        /// Resolve the class-light (torch) brightness ceiling from the config file(s) — the
        /// user-facing knob for the multi-torch plateau. 1.0 = the native saturation law;
        /// optional: absent/invalid → <paramref name="fallback"/> (the Inspector value on the spawner).
        /// </summary>
        public static float ResolveClassLightCap(float fallback)
        {
            string? exeDir = null;
            try { exeDir = Path.GetDirectoryName(Application.dataPath); }
            catch { /* dataPath unavailable in some contexts — skip */ }
            if (!string.IsNullOrEmpty(exeDir)
                && TryLoadClassLightCap(Path.Combine(exeDir!, FileName), out var v1))
            {
                Debug.Log($"[DigglesConfig] classLightCap = {v1} (from config beside executable)");
                return v1;
            }
            if (TryLoadClassLightCap(Path.Combine(Application.streamingAssetsPath, FileName), out var v2))
            {
                Debug.Log($"[DigglesConfig] classLightCap = {v2} (from StreamingAssets config)");
                return v2;
            }
            return fallback;
        }

        static bool TryLoadClassLightCap(string path, out float value)
        {
            value = -1f;
            try
            {
                if (!File.Exists(path)) return false;
                var data = JsonUtility.FromJson<ConfigData>(File.ReadAllText(path));
                if (data == null || data.classLightCap < 0f) return false;   // optional
                value = Mathf.Clamp(data.classLightCap, 0f, 4f);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DigglesConfig] failed to read {path}: {e.Message} — ignoring.");
                return false;
            }
        }

        /// <summary>
        /// Write the live-tuning knobs back into diggles_config.json — the same file/keys the
        /// boot resolve reads, so saved values persist across runs instead of requiring
        /// hand-editing the json. Target = the beside-exe copy if it exists, else the
        /// StreamingAssets copy, else create one (editor → StreamingAssets, player → beside
        /// exe). Existing dataRoot/locale/remoteConsolePort/_comment round-trip untouched.
        /// Values &lt; 0 are skipped (knob not available). Returns the written path, or ""
        /// on failure.
        /// </summary>
        public static string SaveTuning(float baseLightMin, float classLightCap)
        {
            try
            {
                string? exeDir = null;
                try { exeDir = Path.GetDirectoryName(Application.dataPath); }
                catch { /* dataPath unavailable in some contexts — skip */ }
                string beside = !string.IsNullOrEmpty(exeDir) ? Path.Combine(exeDir!, FileName) : "";
                string streaming = Path.Combine(Application.streamingAssetsPath, FileName);
                string target =
                    beside.Length > 0 && File.Exists(beside) ? beside
                    : File.Exists(streaming) ? streaming
                    : Application.isEditor ? streaming
                    : beside.Length > 0 ? beside : streaming;

                var data = new ConfigData();
                if (File.Exists(target))
                    data = JsonUtility.FromJson<ConfigData>(File.ReadAllText(target)) ?? new ConfigData();
                if (baseLightMin >= 0f) data.baseLightMin = Mathf.Clamp01(baseLightMin);
                if (classLightCap >= 0f) data.classLightCap = Mathf.Clamp(classLightCap, 0f, 4f);
                File.WriteAllText(target, JsonUtility.ToJson(data, true));
                Debug.Log($"[DigglesConfig] tuning saved → {target}");
                return target;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DigglesConfig] SaveTuning failed: {e.Message}");
                return "";
            }
        }

        static bool TryLoadLocale(string path, out string value)
        {
            value = "";
            try
            {
                if (!File.Exists(path)) return false;
                var data = JsonUtility.FromJson<ConfigData>(File.ReadAllText(path));
                if (data == null || string.IsNullOrWhiteSpace(data.locale)) return false; // optional
                value = data.locale!.Trim();
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DigglesConfig] failed to read {path}: {e.Message} — ignoring.");
                return false;
            }
        }

        /// <summary>
        /// Try to read a non-empty dataRoot from a JSON file. Returns false (and never
        /// throws) if the file is absent, unreadable, unparseable, or has no dataRoot.
        /// </summary>
        static bool TryLoad(string path, out string value)
        {
            value = "";
            try
            {
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<ConfigData>(json);
                if (data == null || string.IsNullOrWhiteSpace(data.dataRoot))
                {
                    Debug.LogWarning($"[DigglesConfig] {path} has no valid \"dataRoot\" — ignoring.");
                    return false;
                }
                value = data.dataRoot!.Trim();
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DigglesConfig] failed to read {path}: {e.Message} — ignoring.");
                return false;
            }
        }
    }
}
