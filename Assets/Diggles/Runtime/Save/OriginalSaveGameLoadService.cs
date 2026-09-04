#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using UnityEngine.Scripting;

    public interface IOriginalSaveGameLoadService
    {
        OriginalSaveGameLoadResult LoadGame(
            string savePathOrName,
            bool raiseGamestart = true);
    }

    public sealed class OriginalSaveGameLoadResult
    {
        private readonly ReadOnlyCollection<string> _warnings;

        internal OriginalSaveGameLoadResult(
            string loadedName,
            string loadedPath,
            int entityCount,
            IReadOnlyList<string> warnings)
        {
            LoadedName = loadedName ?? throw new ArgumentNullException(nameof(loadedName));
            LoadedPath = loadedPath ?? throw new ArgumentNullException(nameof(loadedPath));
            EntityCount = entityCount;
            if (warnings == null) throw new ArgumentNullException(nameof(warnings));
            var warningCopy = new string[warnings.Count];
            for (int index = 0; index < warningCopy.Length; index++)
                warningCopy[index] = warnings[index];
            _warnings = Array.AsReadOnly(warningCopy);
        }

        public string LoadedName { get; }
        public string LoadedPath { get; }
        public int EntityCount { get; }
        public IReadOnlyList<string> Warnings => _warnings;
    }

    public sealed class OriginalSaveActivationException : InvalidOperationException
    {
        private readonly ReadOnlyCollection<string> _activationBlockers;

        internal OriginalSaveActivationException(IReadOnlyList<string> activationBlockers)
            : base(CreateMessage(activationBlockers))
        {
            if (activationBlockers == null)
                throw new ArgumentNullException(nameof(activationBlockers));
            var blockerCopy = new string[activationBlockers.Count];
            for (int index = 0; index < blockerCopy.Length; index++)
                blockerCopy[index] = activationBlockers[index];
            _activationBlockers = Array.AsReadOnly(blockerCopy);
        }

        public IReadOnlyList<string> ActivationBlockers => _activationBlockers;

        private static string CreateMessage(IReadOnlyList<string> activationBlockers)
        {
            if (activationBlockers == null)
                throw new ArgumentNullException(nameof(activationBlockers));
            return "The staged original save cannot be activated:" +
                Environment.NewLine + "- " +
                string.Join(Environment.NewLine + "- ", activationBlockers);
        }
    }

    public sealed class OriginalSaveGameLoadConfiguration
    {
        public OriginalSaveGameLoadConfiguration(string bdbRelativePath)
        {
            if (string.IsNullOrWhiteSpace(bdbRelativePath))
            {
                throw new ArgumentException(
                    "A candidate BDB path is required.",
                    nameof(bdbRelativePath));
            }
            BdbRelativePath = bdbRelativePath;
        }

        public string BdbRelativePath { get; }
    }

    public sealed class OriginalSaveGameLoadService : IOriginalSaveGameLoadService
    {
        public const string GamestartEventName = "evt_system_gamestart";

        private readonly IOriginalSaveLoadPreparationService _preparation;
        private readonly IRuntimeWorldSessionHost _sessionHost;
        private readonly IOriginalSaveRuntimeSessionStager _sessionStager;
        private readonly IActiveGameSession _activeSession;
        private readonly OriginalSaveGameLoadConfiguration _configuration;
        private readonly OriginalSaveBaselineSessionStore _baselineStore;
        private readonly Func<DigglesCameraController?> _cameraProvider;

        [Preserve]
        public OriginalSaveGameLoadService(
            IOriginalSaveLoadPreparationService preparation,
            IRuntimeWorldSessionHost sessionHost,
            IOriginalSaveRuntimeSessionStager sessionStager,
            IActiveGameSession activeSession,
            OriginalSaveGameLoadConfiguration configuration,
            OriginalSaveBaselineSessionStore baselineStore,
            Func<DigglesCameraController?> cameraProvider)
        {
            _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
            _sessionHost = sessionHost ?? throw new ArgumentNullException(nameof(sessionHost));
            _sessionStager = sessionStager ?? throw new ArgumentNullException(nameof(sessionStager));
            _activeSession = activeSession ?? throw new ArgumentNullException(nameof(activeSession));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _baselineStore = baselineStore ??
                throw new ArgumentNullException(nameof(baselineStore));
            _cameraProvider = cameraProvider ??
                throw new ArgumentNullException(nameof(cameraProvider));
        }

        public OriginalSaveGameLoadResult LoadGame(
            string savePathOrName,
            bool raiseGamestart = true)
        {
            RuntimeGameSession? candidate = null;
            bool committed = false;
            var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
            long prepareMs, stageMs, activateMs;
            try
            {
                string savePath = ResolveSavePath(savePathOrName);
                string loadedName = Path.GetFileNameWithoutExtension(savePath);
                OriginalSaveRuntimeSnapshot snapshot = _preparation.Prepare(savePath);
                OriginalSaveFixedEngineState fixedEngineState =
                    OriginalSaveFixedEngineStateReader.Read(snapshot.SourceDocument);
                prepareMs = phaseWatch.ElapsedMilliseconds;
                RuntimeGameSession current = _activeSession.Current;
                candidate = _sessionHost.CreateCandidate(
                    current.Engine.Ctx.DataRoot,
                    _configuration.BdbRelativePath,
                    $"WorldContent.{loadedName}.Candidate");
                OriginalSaveRuntimeSessionState staged = _sessionStager.StageCandidate(
                    candidate,
                    snapshot,
                    _configuration.BdbRelativePath);
                IReadOnlyList<string> blockers = staged.ComputeActivationBlockers();
                if (blockers.Count != 0)
                    throw new OriginalSaveActivationException(blockers);
                stageMs = phaseWatch.ElapsedMilliseconds - prepareMs;

                candidate.MarkValidated();
                CarryAudioPlaybackAcrossSwap(current, candidate);
                RuntimeGameSession retired = _sessionHost.ActivateCandidate(candidate);
                committed = true;
                // The replaced session (a full world: terrain chunks, entities, nav) was
                // previously retired but never released — every in-game load leaked it into
                // the scene (headless batch runs died of it, and an interactive session paid
                // the memory cost after every reload). Rollback can no longer need it once
                // activation committed.
                retired.Dispose();

                _baselineStore.SetBaseline(candidate, snapshot);
                _baselineStore.GetOrCreateViewBeginStore(candidate)
                    .SetViewBegin(fixedEngineState.ViewBegin);
                DigglesCameraController? camera = _cameraProvider();
                if (camera != null)
                {
                    camera.FocusX = fixedEngineState.FocusX;
                    camera.FocusY = fixedEngineState.FocusY;
                    camera.Zoom = fixedEngineState.Zoom;
                    camera.Dx = fixedEngineState.Dx;
                    camera.Dy = fixedEngineState.Dy;
                }

                // Native load restores the marker array (FUN_00556570:407-428;
                // FUN_004d18f0:15-33), then the draw tick feeds the restored view position to
                // the marker selector (FUN_006b5d90:366-376; FUN_004d1570:104-133). The port's
                // continuous proximity tick is pending, so mirror that first post-load selection
                // once, after the candidate and its restored camera state are active.
                candidate.Engine.Ctx.Audio?.ArmThemeFromMarkers(
                    new UnityEngine.Vector3(
                        fixedEngineState.FocusX,
                        fixedEngineState.FocusY,
                        0f));

                // Clan flags are live-producer presentation (native: first CObj::Tick after
                // gameload recreates the CScapeIcon); staging on the inactive root skips them,
                // so re-run the producer once now that the candidate hierarchy is active.
                Diggles.Runtime.Tcl.Engine.DigglesTclEngine.RefreshAllBuildingClanFlags(
                    candidate.Engine.Ctx);

                if (raiseGamestart)
                {
                    staged.PublishSystemEvent(
                        GamestartEventName,
                        candidate.Engine.Ctx);
                }

                activateMs = phaseWatch.ElapsedMilliseconds - prepareMs - stageMs;
                UnityEngine.Debug.Log(
                    $"[OriginalSave] load '{loadedName}': total {phaseWatch.ElapsedMilliseconds} ms " +
                    $"(prepare {prepareMs} + stage {stageMs} + activate {activateMs})");

                return new OriginalSaveGameLoadResult(
                    loadedName,
                    savePath,
                    staged.Entities.Count,
                    staged.Warnings);
            }
            catch
            {
                // Only a baseline claimed by this attempt is discarded — a failed load (bad
                // file, staging blocker) must not strip the still-active session's baseline,
                // or its next save would silently fall to the no-baseline writer.
                if (candidate != null && ReferenceEquals(_baselineStore.Owner, candidate))
                    _baselineStore.Clear();
                if (!committed && candidate != null) candidate.Dispose();
                throw;
            }
        }

        private string ResolveSavePath(string savePathOrName)
        {
            return OriginalSavePathResolver.ResolveLoadPath(
                _activeSession.Current.Engine.Ctx.DataRoot,
                savePathOrName);
        }

        // The native adaptive manager is a lazy singleton (FUN_004cfea0:19-28), while the save's
        // adaptive-sound block carries only marker count/records (FUN_004d18f0:15-33). Preserve
        // campaign playback across the port's whole-session swap, but never import the outgoing menu theme:
        // native marker processing reselects from the loaded marker set after view restoration
        // (FUN_006b5d90:366-376; FUN_004d1570:104-133), which the activation tail mirrors above.
        private static void CarryAudioPlaybackAcrossSwap(
            RuntimeGameSession current,
            RuntimeGameSession candidate)
        {
            AudioState? from = current.Engine.Ctx.Audio;
            AudioState? to = candidate.Engine.Ctx.Audio;
            if (from == null || to == null) return;

            to.Mode = from.Mode;
            to.MusicBaseVolume = from.MusicBaseVolume;

            bool sourceIsCampaign = current.OriginalSaveState != null ||
                current.Engine.Ctx.IsStoryMgr;
            if (!sourceIsCampaign) return;

            to.CurrentTheme = from.CurrentTheme;
            to.PrimaryTheme = from.PrimaryTheme;
            to.AutoFightTheme = from.AutoFightTheme;
            to.MusicPlaying = from.MusicPlaying;
            to.MarkersEnabled = from.MarkersEnabled;
        }
    }
}
