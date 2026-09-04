#nullable enable

namespace Diggles.Tests
{
    using System;
    using Diggles.Runtime;
    using Diggles.Runtime.UI;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class RuntimeGameSessionTests
    {
        [Test]
        public void RuntimeDefinitionsAreDeclarationOnlyAndIdempotent()
        {
            var root = new GameObject("RuntimeDefinitionsTest");
            try
            {
                DigglesEngine engine = new DigglesEngineFactory().Create(
                    OriginalDataTestFixture.DataRoot,
                    root.transform,
                    "BlockDB2.BDB");

                engine.LoadRuntimeDefinitions();
                int classCount = engine.Ctx.Classes.Count;

                Assert.That(engine.RuntimeDefinitionsLoaded, Is.True);
                Assert.That(classCount, Is.GreaterThan(100));
                Assert.That(engine.Ctx.Classes.ContainsKey("Zwerg"), Is.True);
                Assert.That(engine.Ctx.Entities, Is.Empty,
                    "definition loading must not create save-candidate entities");

                engine.LoadRuntimeDefinitions();
                Assert.That(engine.Ctx.Classes.Count, Is.EqualTo(classCount));
                Assert.That(engine.Ctx.Entities, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WorldRuntimeResolvesTheBoundSessionInsteadOfKeepingTheBootContext()
        {
            var stableRoot = new GameObject("WorldRuntimeBindingTest");
            var initialRoot = new GameObject("WorldContent.Initial");
            initialRoot.transform.SetParent(stableRoot.transform, false);
            var runtimeObject = new GameObject("WorldRuntime");
            RuntimeGameSession? initial = null;
            RuntimeGameSession? candidate = null;
            try
            {
                var factory = new DigglesEngineFactory();
                initial = RuntimeGameSession.CreateInitial(
                    factory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                var active = new ActiveGameSession(initial);
                var host = new RuntimeWorldSessionHost(
                    factory,
                    active,
                    new GameMutationGate(),
                    new RecordingBinder());
                candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                var runtime = runtimeObject.AddComponent<DigglesWorldRuntime>();

                runtime.BindSession(initial);
                Assert.That(runtime.Context, Is.SameAs(initial.Engine.Ctx));

                runtime.BindSession(candidate);
                Assert.That(runtime.Context, Is.SameAs(candidate.Engine.Ctx));
                Assert.That(runtime.Context, Is.Not.SameAs(initial.Engine.Ctx));
            }
            finally
            {
                candidate?.Dispose();
                initial?.Dispose();
                UnityEngine.Object.DestroyImmediate(runtimeObject);
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }

        [Test]
        public void CandidateActivationFailureRestoresOldRootFacadeAndPresentation()
        {
            var stableRoot = new GameObject("RuntimeSessionHostTest");
            var initialRoot = new GameObject("WorldContent.Initial");
            initialRoot.transform.SetParent(stableRoot.transform, false);
            RuntimeGameSession? candidate = null;
            RuntimeGameSession? initial = null;
            try
            {
                var factory = new DigglesEngineFactory();
                DigglesEngine initialEngine = factory.Create(
                    OriginalDataTestFixture.DataRoot,
                    initialRoot.transform,
                    "BlockDB2.BDB");
                initial = RuntimeGameSession.CreateInitial(initialEngine, initialRoot);
                var active = new ActiveGameSession(initial);
                var binder = new RecordingBinder();
                var gate = new GameMutationGate();
                var host = new RuntimeWorldSessionHost(
                    factory,
                    active,
                    gate,
                    binder);
                candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                candidate.MarkValidated();
                binder.FailFor = candidate;

                Assert.Throws<InvalidOperationException>(() => host.ActivateCandidate(candidate));

                Assert.That(active.Current, Is.SameAs(initial));
                Assert.That(initial.State, Is.EqualTo(RuntimeGameSessionState.Active));
                Assert.That(initial.ContentRoot.activeSelf, Is.True);
                Assert.That(candidate.State, Is.EqualTo(RuntimeGameSessionState.Validated));
                Assert.That(candidate.ContentRoot.activeSelf, Is.False);
                Assert.That(binder.BoundSession, Is.SameAs(initial));
                Assert.That(gate.IsMutationInProgress, Is.False);
                Assert.That(initial.ContentRoot, Is.Not.Null,
                    "rollback must not destroy the old session");
            }
            finally
            {
                candidate?.Dispose();
                initial?.Dispose();
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }

        [Test]
        public void ValidatedCandidateSwapRetiresButDoesNotDestroyPreviousSession()
        {
            var stableRoot = new GameObject("RuntimeSessionSwapTest");
            var initialRoot = new GameObject("WorldContent.Initial");
            initialRoot.transform.SetParent(stableRoot.transform, false);
            RuntimeGameSession? candidate = null;
            RuntimeGameSession? initial = null;
            try
            {
                var factory = new DigglesEngineFactory();
                initial = RuntimeGameSession.CreateInitial(
                    factory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                var active = new ActiveGameSession(initial);
                var binder = new RecordingBinder();
                var host = new RuntimeWorldSessionHost(
                    factory,
                    active,
                    new GameMutationGate(),
                    binder);
                candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                Assert.That(candidate.ContentRoot.activeSelf, Is.False);
                Assert.That(candidate.Engine.Ctx, Is.Not.SameAs(initial.Engine.Ctx));
                candidate.MarkValidated();

                RuntimeGameSession retired = host.ActivateCandidate(candidate);

                Assert.That(retired, Is.SameAs(initial));
                Assert.That(active.Current, Is.SameAs(candidate));
                Assert.That(candidate.State, Is.EqualTo(RuntimeGameSessionState.Active));
                Assert.That(candidate.ContentRoot.activeSelf, Is.True);
                Assert.That(initial.State, Is.EqualTo(RuntimeGameSessionState.Retired));
                Assert.That(initial.ContentRoot.activeSelf, Is.False);
                Assert.That(initial.ContentRoot, Is.Not.Null,
                    "the caller owns retired-session disposal after successful activation");
                Assert.That(binder.BoundSession, Is.SameAs(candidate));
            }
            finally
            {
                candidate?.Dispose();
                initial?.Dispose();
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }

        [Test]
        public void EntryPointServicesResolveTheNewContextAfterFacadeSwap()
        {
            var stableRoot = new GameObject("RuntimeSessionEntryPointTest");
            var initialRoot = new GameObject("WorldContent.Initial");
            initialRoot.transform.SetParent(stableRoot.transform, false);
            RuntimeGameSession? candidate = null;
            RuntimeGameSession? initial = null;
            try
            {
                var factory = new DigglesEngineFactory();
                initial = RuntimeGameSession.CreateInitial(
                    factory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                initial.Engine.Ctx.IsStoryMgr = false;
                var active = new ActiveGameSession(initial);
                var host = new RuntimeWorldSessionHost(
                    factory,
                    active,
                    new GameMutationGate(),
                    new RecordingBinder());
                var bottomMenu = new BottomMenuController(active);
                var timeControl = new TimeControlService(active);
                var click = new ClickToMoveService(active);

                TickEntryPoint(bottomMenu);
                click.FireDropAll();
                Assert.That(bottomMenu.Model.Mode, Is.EqualTo(BottomMenuMode.None));
                Assert.That(timeControl.Active, Is.False);

                ClickToMoveService.DigToolArmed = true;
                candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                candidate.Engine.Ctx.IsStoryMgr = true;
                candidate.MarkValidated();
                host.ActivateCandidate(candidate);

                TickEntryPoint(bottomMenu);
                click.FireDropAll();
                Assert.That(bottomMenu.Model.Mode, Is.EqualTo(BottomMenuMode.Dig));
                Assert.That(timeControl.Active, Is.True);
                Assert.That(ClickToMoveService.DigToolArmed, Is.False,
                    "an input mode from the retired context must not leak into the loaded session");
            }
            finally
            {
                ClickToMoveService.DigToolArmed = false;
                candidate?.Dispose();
                initial?.Dispose();
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }

        [Test]
        public void CompositePresentationBinderRebindsEveryParticipantDuringRollback()
        {
            var stableRoot = new GameObject("RuntimePresentationRollbackTest");
            var initialRoot = new GameObject("WorldContent.Initial");
            initialRoot.transform.SetParent(stableRoot.transform, false);
            RuntimeGameSession? candidate = null;
            RuntimeGameSession? initial = null;
            try
            {
                var factory = new DigglesEngineFactory();
                initial = RuntimeGameSession.CreateInitial(
                    factory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                var active = new ActiveGameSession(initial);
                var first = new RecordingParticipant();
                var second = new RecordingParticipant();
                var binder = new RuntimePresentationBinder(
                    new IRuntimeSessionPresentationParticipant[] { first, second });
                var host = new RuntimeWorldSessionHost(
                    factory,
                    active,
                    new GameMutationGate(),
                    binder);
                candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                candidate.MarkValidated();
                second.FailFor = candidate;

                Assert.Throws<InvalidOperationException>(() => host.ActivateCandidate(candidate));

                CollectionAssert.AreEqual(
                    new[] { candidate, initial },
                    first.Bindings);
                CollectionAssert.AreEqual(
                    new[] { candidate, initial },
                    second.Bindings);
                Assert.That(active.Current, Is.SameAs(initial));
                Assert.That(initial.ContentRoot.activeSelf, Is.True);
                Assert.That(candidate.ContentRoot.activeSelf, Is.False);
            }
            finally
            {
                candidate?.Dispose();
                initial?.Dispose();
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }

        private sealed class RecordingBinder : IRuntimePresentationBinder
        {
            public RuntimeGameSession? FailFor { get; set; }
            public RuntimeGameSession? BoundSession { get; private set; }

            public void BindSession(RuntimeGameSession session)
            {
                BoundSession = session;
                if (ReferenceEquals(session, FailFor))
                    throw new InvalidOperationException("Injected presentation activation failure.");
            }
        }

        private sealed class RecordingParticipant : IRuntimeSessionPresentationParticipant
        {
            public RuntimeGameSession? FailFor { get; set; }
            public System.Collections.Generic.List<RuntimeGameSession> Bindings { get; } = new();

            public void BindSession(RuntimeGameSession session)
            {
                Bindings.Add(session);
                if (ReferenceEquals(session, FailFor))
                {
                    FailFor = null;
                    throw new InvalidOperationException("Injected participant binding failure.");
                }
            }
        }

        private static void TickEntryPoint(object service)
        {
            System.Reflection.MethodInfo? tick = service.GetType().GetMethod(
                "VContainer.Unity.ITickable.Tick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(tick, Is.Not.Null);
            tick!.Invoke(service, null);
        }
    }
}
