#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using UnityEngine.Scripting;

    public sealed class OriginalSaveGameBackend : IGameSaveBackend
    {
        private readonly IOriginalSaveRuntimeArchiveService _archiveService;
        private readonly IOriginalSaveGameLoadService _loadService;
        private readonly OriginalSaveBaselineSessionStore _baselineStore;
        private readonly ActiveGameSession _activeSession;
        private readonly Func<DigglesCameraController?> _cameraProvider;

        [Preserve]
        public OriginalSaveGameBackend(
            IOriginalSaveRuntimeArchiveService archiveService,
            IOriginalSaveGameLoadService loadService,
            OriginalSaveBaselineSessionStore baselineStore,
            ActiveGameSession activeSession,
            Func<DigglesCameraController?> cameraProvider)
        {
            _archiveService = archiveService ??
                throw new ArgumentNullException(nameof(archiveService));
            _loadService = loadService ?? throw new ArgumentNullException(nameof(loadService));
            _baselineStore = baselineStore ??
                throw new ArgumentNullException(nameof(baselineStore));
            _activeSession = activeSession ??
                throw new ArgumentNullException(nameof(activeSession));
            _cameraProvider = cameraProvider ??
                throw new ArgumentNullException(nameof(cameraProvider));
        }

        public string Save(string name)
        {
            (string path, RuntimeGameSession session, DigglesTclContext context,
                OriginalSaveRuntimeSnapshot? baseline) = PrepareSave(name);
            if (baseline != null)
            {
                _archiveService.CaptureAndWriteAtomic(path, baseline, context);
                return path;
            }

            _archiveService.CaptureAndWriteAtomic(
                path,
                context,
                RequireCamera(),
                _baselineStore.GetOrCreateViewBeginStore(session));
            return path;
        }

        /// <summary>
        /// The non-blocking save (F2 / autosave): live state is captured synchronously on the
        /// calling frame, serialization + gzip + disk IO run on a background thread. Throws
        /// when another background save is still in flight.
        /// </summary>
        public System.Threading.Tasks.Task<string> SaveAsync(string name)
        {
            (string path, RuntimeGameSession session, DigglesTclContext context,
                OriginalSaveRuntimeSnapshot? baseline) = PrepareSave(name);
            if (baseline != null)
                return _archiveService.CaptureAndWriteAtomicAsync(path, baseline, context);

            return _archiveService.CaptureAndWriteAtomicAsync(
                path,
                context,
                RequireCamera(),
                _baselineStore.GetOrCreateViewBeginStore(session));
        }

        public bool WriteInFlight => _archiveService.WriteInFlight;

        private (string path, RuntimeGameSession session, DigglesTclContext context,
            OriginalSaveRuntimeSnapshot? baseline) PrepareSave(string name)
        {
            RuntimeGameSession session = _activeSession.Current;
            DigglesTclContext context = session.Engine.Ctx;
            string path = OriginalSavePathResolver.ResolvePlayerSavePath(
                context.DataRoot,
                name);
            Directory.CreateDirectory(OriginalSavePathResolver.GetSaveDirectory(
                context.DataRoot));

            OriginalSaveRuntimeSnapshot? baseline = _baselineStore.Baseline;
            if (baseline != null && ReferenceEquals(_baselineStore.Owner, session))
            {
                if (baseline.SourceDocument.World.HasOffset)
                    throw new NotSupportedException("Writing a save from an offset snapshot is not supported yet.");
                return (path, session, context, baseline);
            }

            if (baseline != null) _baselineStore.Clear();
            return (path, session, context, null);
        }

        private DigglesCameraController RequireCamera() =>
            _cameraProvider() ??
            throw new InvalidOperationException(
                "Saving a new campaign requires the live Diggles camera controller; " +
                "headless no-baseline saves are not supported.");

        public string Load(string name)
        {
            try
            {
                string normalizedName =
                    OriginalSavePathResolver.NormalizePlayerSaveFileName(name);
                return _loadService.LoadGame(
                    normalizedName,
                    raiseGamestart: true).LoadedPath;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[OriginalSave] failed to load '{name}': {exception.Message}");
                return string.Empty;
            }
        }
    }

    public sealed class OriginalSaveGameBackendParticipant :
        IRuntimeSessionPresentationParticipant
    {
        private readonly Func<OriginalSaveGameBackend> _backendProvider;
        private readonly OriginalSaveBaselineSessionStore _baselineStore;

        [Preserve]
        public OriginalSaveGameBackendParticipant(
            Func<OriginalSaveGameBackend> backendProvider,
            OriginalSaveBaselineSessionStore baselineStore)
        {
            _backendProvider = backendProvider ??
                throw new ArgumentNullException(nameof(backendProvider));
            _baselineStore = baselineStore ??
                throw new ArgumentNullException(nameof(baselineStore));
        }

        public void BindSession(RuntimeGameSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (!ReferenceEquals(_baselineStore.Owner, session)) _baselineStore.Clear();
            session.Engine.Ctx.SaveGames = _backendProvider();
        }
    }

    internal static class OriginalSavePathResolver
    {
        private const string SaveDirectoryName = "Gamesave";
        private const string SaveExtension = ".sav";

        internal static string GetSaveDirectory(string dataRoot)
        {
            if (string.IsNullOrWhiteSpace(dataRoot))
                throw new ArgumentException("An original data root is required.", nameof(dataRoot));
            return Path.GetFullPath(Path.Combine(dataRoot, SaveDirectoryName));
        }

        internal static string NormalizePlayerSaveFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A save name is required.", nameof(name));

            string trimmed = name.Trim();
            if (Path.IsPathRooted(trimmed) ||
                trimmed.IndexOf('/') >= 0 ||
                trimmed.IndexOf('\\') >= 0 ||
                trimmed.IndexOf("..", StringComparison.Ordinal) >= 0 ||
                trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "A player save name must be a single file name and cannot contain '..'.",
                    nameof(name));
            }

            return trimmed.EndsWith(SaveExtension, StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + SaveExtension;
        }

        internal static string ResolvePlayerSavePath(string dataRoot, string name)
        {
            return ResolveInsideSaveDirectory(
                dataRoot,
                NormalizePlayerSaveFileName(name),
                nameof(name));
        }

        internal static string ResolveLoadPath(string dataRoot, string savePathOrName)
        {
            if (string.IsNullOrWhiteSpace(savePathOrName))
            {
                throw new ArgumentException(
                    "A save path or name is required.",
                    nameof(savePathOrName));
            }

            string supplied = savePathOrName.Trim();
            if (Path.IsPathRooted(supplied) || File.Exists(supplied))
                return Path.GetFullPath(supplied);

            string fileName = Path.HasExtension(supplied)
                ? supplied
                : supplied + SaveExtension;
            return ResolveInsideSaveDirectory(
                dataRoot,
                fileName,
                nameof(savePathOrName));
        }

        private static string ResolveInsideSaveDirectory(
            string dataRoot,
            string fileName,
            string parameterName)
        {
            string saveRoot = GetSaveDirectory(dataRoot);
            string resolved = Path.GetFullPath(Path.Combine(saveRoot, fileName));
            string rootPrefix = saveRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "A save name cannot escape the original Gamesave directory.",
                    parameterName);
            }
            return resolved;
        }
    }
}
