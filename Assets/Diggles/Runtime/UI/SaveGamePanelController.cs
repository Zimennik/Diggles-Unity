#nullable enable

namespace Diggles.Runtime.UI
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using VContainer;
    using VContainer.Unity;

    // The F3 load/save panel seam (UI_ARCHITECTURE.md three-layer pattern): this file is the
    // POCO model + DI controller; the visual layout is authored in the Unity editor and reads
    // the model via a thin view. Player-facing text stays out of the seam.
    public sealed class SaveGamePanelModel
    {
        public enum EntryKind { Manual, Quick, Auto }

        public readonly struct Entry
        {
            public Entry(string fileName, DateTime lastWriteTime, long sizeBytes, EntryKind kind)
            {
                FileName = fileName;
                LastWriteTime = lastWriteTime;
                SizeBytes = sizeBytes;
                Kind = kind;
            }

            /// <summary>File name including the.sav extension — the backend load key.</summary>
            public string FileName { get; }
            public DateTime LastWriteTime { get; }
            public long SizeBytes { get; }
            public EntryKind Kind { get; }
            public string DisplayName => Path.GetFileNameWithoutExtension(FileName);
        }

        public readonly List<Entry> Entries = new();
        /// <summary>The view toggles this; the controller only scans while the panel shows.</summary>
        public bool Visible;
        /// <summary>True while a background save write is still running (disable Save/Delete).</summary>
        public bool WriteInFlight;
        /// <summary>True when a gameplay world is active (Load/Save allowed at all).</summary>
        public bool WorldActive;
        public int Version;
    }

    public sealed class SaveGamePanelController : ITickable
    {
        private const float RefreshIntervalSeconds = 1f;

        private readonly Func<DigglesTclContext> _contextAccessor;
        private float _nextRefresh;
        private bool _forceRefresh;

        [UnityEngine.Scripting.Preserve]
        [Inject]
        public SaveGamePanelController(IActiveGameSession sessions)
        {
            _contextAccessor = () => sessions.Current.Engine.Ctx;
        }

        // Headless tests provide a fixed context directly.
        public SaveGamePanelController(DigglesTclContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            _contextAccessor = () => context;
        }

        public readonly SaveGamePanelModel Model = new();

        private DigglesTclContext Context => _contextAccessor();

        void ITickable.Tick()
        {
            if (!Model.Visible) return;
            _nextRefresh -= UnityEngine.Time.unscaledDeltaTime;
            if (_nextRefresh > 0f && !_forceRefresh) return;
            Refresh();
        }

        /// <summary>Rescans the Gamesave directory into the model (newest first).</summary>
        public void Refresh()
        {
            _nextRefresh = RefreshIntervalSeconds;
            _forceRefresh = false;
            DigglesTclContext context = Context;
            Model.Entries.Clear();
            Model.WorldActive = context.Terrain != null && context.SaveGames != null;
            Model.WriteInFlight =
                context.SaveGames is OriginalSaveGameBackend backend && backend.WriteInFlight;

            ScanInto(Model, SaveDirectory(context));
            Model.Version++;
        }

        /// <summary>Pure directory scan (newest first) — headless-testable.</summary>
        public static void ScanInto(SaveGamePanelModel model, string directory)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (!Directory.Exists(directory)) return;
            var files = new DirectoryInfo(directory)
                .GetFiles("*.sav", SearchOption.TopDirectoryOnly);
            Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            foreach (FileInfo file in files)
            {
                model.Entries.Add(new SaveGamePanelModel.Entry(
                    file.Name,
                    file.LastWriteTime,
                    file.Length,
                    ClassifyEntry(file.Name)));
            }
        }

        public static SaveGamePanelModel.EntryKind ClassifyEntry(string fileName)
        {
            if (fileName.StartsWith("quick_", StringComparison.OrdinalIgnoreCase))
                return SaveGamePanelModel.EntryKind.Quick;
            if (fileName.StartsWith("auto_", StringComparison.OrdinalIgnoreCase))
                return SaveGamePanelModel.EntryKind.Auto;
            return SaveGamePanelModel.EntryKind.Manual;
        }

        /// <summary>Loads the selected entry through the session-bound backend.</summary>
        public bool Load(SaveGamePanelModel.Entry entry)
        {
            IGameSaveBackend? backend = Context.SaveGames;
            if (backend == null) return false;
            return !string.IsNullOrEmpty(backend.Load(entry.FileName));
        }

        /// <summary>
        /// Saves under the given player name (non-blocking when the backend supports it) and
        /// refreshes the list when the write completes.
        /// </summary>
        public async void Save(string name)
        {
            DigglesTclContext context = Context;
            IGameSaveBackend? backend = context.SaveGames;
            if (backend == null) return;
            try
            {
                if (backend is OriginalSaveGameBackend original)
                {
                    if (original.WriteInFlight) return;
                    await original.SaveAsync(name);
                }
                else
                {
                    backend.Save(name);
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError($"[OriginalSave] panel save failed: {exception.Message}");
            }
            _forceRefresh = true;
        }

        /// <summary>Deletes the selected save file and refreshes the list.</summary>
        public bool Delete(SaveGamePanelModel.Entry entry)
        {
            string path = Path.Combine(SaveDirectory(Context), entry.FileName);
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (IOException exception)
            {
                UnityEngine.Debug.LogWarning(
                    $"[OriginalSave] could not delete '{entry.FileName}': {exception.Message}");
                return false;
            }
            finally
            {
                _forceRefresh = true;
            }
        }

        private static string SaveDirectory(DigglesTclContext context) =>
            Path.Combine(context.DataRoot, "Gamesave");
    }
}
