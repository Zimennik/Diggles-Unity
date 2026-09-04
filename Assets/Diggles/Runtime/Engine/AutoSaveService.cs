#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Non-freezing autosaves on top of the async save path: live state is captured on the
    // frame the timer fires, serialization/gzip/disk run in the background. A cycle is
    // skipped (and retried a minute later) while another save is in flight or no world is
    // active — an autosave must never contend with the player's own F2.
    public sealed class AutoSaveService : ITickable
    {
        private const string AutoSavePrefix = "auto_Autosave_";
        private const float IntervalSeconds = 300f;
        private const float RetrySeconds = 60f;
        private const int KeepNewest = 3;

        private readonly IActiveGameSession _sessions;
        private float _secondsUntilNext = IntervalSeconds;

        [Preserve]
        [Inject]
        public AutoSaveService(IActiveGameSession sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public static string CreateAutoSaveName(DateTime timestamp)
        {
            return AutoSavePrefix + timestamp.ToString(
                "yyyyMMdd_HHmm",
                CultureInfo.InvariantCulture);
        }

        void ITickable.Tick()
        {
            _secondsUntilNext -= Time.unscaledDeltaTime;
            if (_secondsUntilNext > 0f) return;

            DigglesTclContext context = _sessions.Current.Engine.Ctx;
            if (context.Terrain == null ||
                !context.IsStoryMgr ||
                context.SaveGames is not OriginalSaveGameBackend backend ||
                backend.WriteInFlight)
            {
                _secondsUntilNext = RetrySeconds;
                return;
            }

            _secondsUntilNext = IntervalSeconds;
            RunAutoSave(backend, context.DataRoot);
        }

        private static async void RunAutoSave(OriginalSaveGameBackend backend, string dataRoot)
        {
            try
            {
                string path = await backend.SaveAsync(CreateAutoSaveName(DateTime.Now));
                Debug.Log($"[OriginalSave] autosave written to '{path}'.");
                PruneOldAutoSaves(dataRoot);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[OriginalSave] autosave failed: {exception.Message}");
            }
        }

        private static void PruneOldAutoSaves(string dataRoot)
        {
            string directory = OriginalSavePathResolver.GetSaveDirectory(dataRoot);
            if (!Directory.Exists(directory)) return;
            FileInfo[] stale = new DirectoryInfo(directory)
                .GetFiles(AutoSavePrefix + "*.sav", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTime)
                .Skip(KeepNewest)
                .ToArray();
            foreach (FileInfo file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException exception)
                {
                    Debug.LogWarning(
                        $"[OriginalSave] could not prune autosave '{file.Name}': {exception.Message}");
                }
            }
        }
    }
}
