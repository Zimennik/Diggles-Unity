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

    public sealed class QuickSaveHotkeyService : ITickable
    {
        private const string QuickSavePrefix = "quick_Quicksave_";

        private readonly IActiveGameSession _sessions;

        [Preserve]
        [Inject]
        public QuickSaveHotkeyService(IActiveGameSession sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public static string CreateQuickSaveName(DateTime timestamp)
        {
            return QuickSavePrefix + timestamp.ToString(
                "yyyyMMdd_HHmm",
                CultureInfo.InvariantCulture);
        }

        void ITickable.Tick()
        {
            DigglesTclContext context = _sessions.Current.Engine.Ctx;
            IGameSaveBackend? backend = context.SaveGames;
            if (context.Terrain == null || backend == null) return;

            if (Input.GetKeyDown(KeyCode.F2))
            {
                // The menu diorama is a live world too — quicksaving it would produce a "game"
                // that restores to the menu backdrop. The original cannot save at the menu either.
                if (!context.IsStoryMgr)
                {
                    Debug.LogWarning("[OriginalSave] quicksave ignored — no campaign is running.");
                    return;
                }
                QuickSave(backend);
            }
            else if (Input.GetKeyDown(KeyCode.F5)) QuickLoad(context, backend);
        }

        private static async void QuickSave(IGameSaveBackend backend)
        {
            // Capture happens synchronously inside SaveAsync; the await only spans the
            // background serialization + disk write, so the game never stalls.
            try
            {
                if (backend is OriginalSaveGameBackend original)
                {
                    if (original.WriteInFlight)
                    {
                        Debug.LogWarning("[OriginalSave] quicksave skipped — a save is already in flight.");
                        return;
                    }
                    string path = await original.SaveAsync(CreateQuickSaveName(DateTime.Now));
                    Debug.Log($"[OriginalSave] quicksave written to '{path}'.");
                    return;
                }

                string syncPath = backend.Save(CreateQuickSaveName(DateTime.Now));
                Debug.Log($"[OriginalSave] quicksave written to '{syncPath}'.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[OriginalSave] quicksave failed: {exception.Message}");
            }
        }

        private static void QuickLoad(
            DigglesTclContext context,
            IGameSaveBackend backend)
        {
            string saveDirectory = OriginalSavePathResolver.GetSaveDirectory(
                context.DataRoot);
            FileInfo? newest = Directory.Exists(saveDirectory)
                ? new DirectoryInfo(saveDirectory)
                    .GetFiles("*.sav", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTime)
                    .FirstOrDefault()
                : null;
            if (newest == null)
            {
                Debug.LogWarning(
                    $"[OriginalSave] quickload found no .sav files in '{saveDirectory}'.");
                return;
            }

            Debug.Log($"[OriginalSave] quickloading '{newest.FullName}'.");
            backend.Load(newest.Name);
        }
    }
}
