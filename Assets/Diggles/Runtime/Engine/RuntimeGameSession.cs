#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using Diggles.Runtime.Save;
    using UnityEngine;
    using UnityEngine.Scripting;

    public interface IDigglesEngineFactory
    {
        DigglesEngine Create(string dataRoot, Transform spawnParent, string bdbRelativePath);
    }

    public sealed class DigglesEngineFactory : IDigglesEngineFactory
    {
        [Preserve]
        public DigglesEngineFactory() { }

        public DigglesEngine Create(
            string dataRoot,
            Transform spawnParent,
            string bdbRelativePath)
        {
            if (spawnParent == null) throw new ArgumentNullException(nameof(spawnParent));
            var engine = new DigglesEngine();
            engine.Build(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)),
                spawnParent,
                bdbRelativePath ?? throw new ArgumentNullException(nameof(bdbRelativePath)));
            return engine;
        }
    }

    public interface IGameMutationGate
    {
        bool IsMutationInProgress { get; }
        IDisposable EnterExclusiveMutation();
    }

    public sealed class GameMutationGate : IGameMutationGate
    {
        private bool _entered;

        [Preserve]
        public GameMutationGate() { }

        public bool IsMutationInProgress => _entered;

        public IDisposable EnterExclusiveMutation()
        {
            if (_entered)
                throw new InvalidOperationException("A game-session mutation is already in progress.");
            _entered = true;
            return new Lease(this);
        }

        private sealed class Lease : IDisposable
        {
            private GameMutationGate? _owner;

            internal Lease(GameMutationGate owner) => _owner = owner;

            public void Dispose()
            {
                if (_owner == null) return;
                _owner._entered = false;
                _owner = null;
            }
        }
    }

    public enum RuntimeGameSessionState
    {
        Candidate,
        Validated,
        Active,
        Retired,
        Disposed,
    }

    public sealed class RuntimeGameSession : IDisposable
    {
        internal RuntimeGameSession(
            DigglesEngine engine,
            GameObject contentRoot,
            RuntimeGameSessionState state)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            ContentRoot = contentRoot ?? throw new ArgumentNullException(nameof(contentRoot));
            State = state;
        }

        public DigglesEngine Engine { get; }
        public GameObject ContentRoot { get; }
        public RuntimeGameSessionState State { get; private set; }
        public OriginalSaveRuntimeSessionState? OriginalSaveState { get; private set; }

        internal void AttachOriginalSaveState(OriginalSaveRuntimeSessionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (State != RuntimeGameSessionState.Candidate)
                throw new InvalidOperationException("Original save state can only be attached to a candidate session.");
            if (OriginalSaveState != null)
                throw new InvalidOperationException("The candidate already has staged original save state.");
            OriginalSaveState = state;
        }

        public static RuntimeGameSession CreateInitial(
            DigglesEngine engine,
            GameObject contentRoot)
        {
            if (contentRoot == null) throw new ArgumentNullException(nameof(contentRoot));
            contentRoot.SetActive(true);
            return new RuntimeGameSession(engine, contentRoot, RuntimeGameSessionState.Active);
        }

        public void MarkValidated()
        {
            if (State != RuntimeGameSessionState.Candidate)
                throw new InvalidOperationException($"Only a candidate session can be validated; current state is {State}.");
            if (OriginalSaveState != null)
            {
                IReadOnlyList<string> blockers =
                    OriginalSaveState.ComputeActivationBlockers();
                if (blockers.Count != 0)
                {
                    throw new InvalidOperationException(
                        "The original-save candidate has unmet activation conditions: " +
                        string.Join("; ", blockers));
                }
            }
            State = RuntimeGameSessionState.Validated;
        }

        internal void InstallCommitState()
        {
            if (State != RuntimeGameSessionState.Validated)
                throw new InvalidOperationException("Commit state requires a validated candidate session.");
            OriginalSaveState?.InstallPendingEntityEvents(Engine.Ctx);
        }

        internal void MarkActive()
        {
            if (State != RuntimeGameSessionState.Validated)
                throw new InvalidOperationException($"Only a validated session can become active; current state is {State}.");
            State = RuntimeGameSessionState.Active;
        }

        internal void MarkRetired()
        {
            if (State != RuntimeGameSessionState.Active)
                throw new InvalidOperationException($"Only an active session can be retired; current state is {State}.");
            State = RuntimeGameSessionState.Retired;
        }

        public void Dispose()
        {
            if (State == RuntimeGameSessionState.Disposed) return;
            State = RuntimeGameSessionState.Disposed;
            if (ContentRoot == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(ContentRoot);
            else UnityEngine.Object.DestroyImmediate(ContentRoot);
        }
    }

    public interface IActiveGameSession
    {
        RuntimeGameSession Current { get; }
    }

    public sealed class ActiveGameSession : IActiveGameSession
    {
        private RuntimeGameSession _current;

        public ActiveGameSession(RuntimeGameSession initialSession)
        {
            _current = initialSession ?? throw new ArgumentNullException(nameof(initialSession));
            if (_current.State != RuntimeGameSessionState.Active)
                throw new ArgumentException("The initial game session must be active.", nameof(initialSession));
        }

        public RuntimeGameSession Current => _current;

        internal void SetCurrent(RuntimeGameSession session) =>
            _current = session ?? throw new ArgumentNullException(nameof(session));
    }

    public interface IRuntimePresentationBinder
    {
        void BindSession(RuntimeGameSession session);
    }

    // Scene-level presenters implement this narrow binding seam. The composite invokes every
    // participant for candidate activation and invokes the same path with the prior session during
    // rollback, so no presenter has to discover a context through a global or service locator.
    public interface IRuntimeSessionPresentationParticipant
    {
        void BindSession(RuntimeGameSession session);
    }

    public sealed class RuntimePresentationBinder : IRuntimePresentationBinder
    {
        private readonly List<IRuntimeSessionPresentationParticipant> _participants = new();

        [Preserve]
        public RuntimePresentationBinder(
            IEnumerable<IRuntimeSessionPresentationParticipant> participants)
        {
            if (participants == null) throw new ArgumentNullException(nameof(participants));
            _participants.AddRange(participants);
        }

        public void BindSession(RuntimeGameSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            foreach (IRuntimeSessionPresentationParticipant participant in _participants)
                participant.BindSession(session);
            if (Application.isPlaying) TclDebugConsole.Attach(session.Engine.Ctx);
            if (Application.isPlaying) RemoteConsoleService.Attach();
        }
    }

    public interface IRuntimeWorldSessionHost
    {
        RuntimeGameSession CreateCandidate(
            string dataRoot,
            string bdbRelativePath,
            string name = "WorldContent.Candidate");

        RuntimeGameSession ActivateCandidate(RuntimeGameSession candidate);
    }

    public sealed class RuntimeWorldSessionHost : IRuntimeWorldSessionHost
    {
        private readonly Transform _stableWorldRoot;
        private readonly IDigglesEngineFactory _engineFactory;
        private readonly ActiveGameSession _activeSession;
        private readonly IGameMutationGate _mutationGate;
        private readonly IRuntimePresentationBinder _presentationBinder;

        public RuntimeWorldSessionHost(
            IDigglesEngineFactory engineFactory,
            ActiveGameSession activeSession,
            IGameMutationGate mutationGate,
            IRuntimePresentationBinder presentationBinder)
        {
            _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
            _activeSession = activeSession ?? throw new ArgumentNullException(nameof(activeSession));
            _mutationGate = mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));
            _presentationBinder = presentationBinder ?? throw new ArgumentNullException(nameof(presentationBinder));
            _stableWorldRoot = _activeSession.Current.ContentRoot.transform.parent
                ?? throw new ArgumentException(
                    "The active session content root must have a stable parent.",
                    nameof(activeSession));
        }

        public RuntimeGameSession CreateCandidate(
            string dataRoot,
            string bdbRelativePath,
            string name = "WorldContent.Candidate")
        {
            var root = new GameObject(string.IsNullOrWhiteSpace(name) ? "WorldContent.Candidate" : name);
            root.transform.SetParent(_stableWorldRoot, false);
            root.SetActive(false);
            try
            {
                DigglesEngine engine = _engineFactory.Create(dataRoot, root.transform, bdbRelativePath);
                return new RuntimeGameSession(engine, root, RuntimeGameSessionState.Candidate);
            }
            catch
            {
                DestroySafe(root);
                throw;
            }
        }

        public RuntimeGameSession ActivateCandidate(RuntimeGameSession candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (candidate.State != RuntimeGameSessionState.Validated)
                throw new InvalidOperationException("A candidate session must be fully validated before activation.");
            if (candidate.ContentRoot.transform.parent != _stableWorldRoot)
                throw new InvalidOperationException("The candidate content root does not belong to this session host.");

            using IDisposable lease = _mutationGate.EnterExclusiveMutation();
            RuntimeGameSession previous = _activeSession.Current;
            previous.ContentRoot.SetActive(false);
            candidate.ContentRoot.SetActive(true);
            try
            {
                _presentationBinder.BindSession(candidate);
                candidate.InstallCommitState();
                candidate.MarkActive();
                _activeSession.SetCurrent(candidate);
                previous.MarkRetired();
                return previous;
            }
            catch (Exception activationError)
            {
                candidate.ContentRoot.SetActive(false);
                previous.ContentRoot.SetActive(true);
                try
                {
                    _presentationBinder.BindSession(previous);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "Candidate session activation failed and presentation rollback also failed.",
                        activationError,
                        rollbackError);
                }
                throw;
            }
        }

        private static void DestroySafe(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
