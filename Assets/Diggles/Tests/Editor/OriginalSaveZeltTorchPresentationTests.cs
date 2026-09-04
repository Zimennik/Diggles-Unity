#nullable enable

namespace Diggles.Tests
{
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveZeltTorchPresentationTests
    {
        private const string BdbRelativePath = "BlockDB2.BDB";
        private GameObject? _stableRoot;
        private RuntimeGameSession? _initial;
        private RuntimeGameSession? _candidate;
        private OriginalSaveArchiveDocument? _document;
        private OriginalSaveRuntimeSnapshot? _snapshot;
        private OriginalSaveRuntimeSessionState? _staged;

        private OriginalSaveArchiveDocument Document => _document ??
            throw new AssertionException("The late-game archive was not loaded.");

        private OriginalSaveRuntimeSnapshot Snapshot => _snapshot ??
            throw new AssertionException("The late-game snapshot was not staged.");

        private OriginalSaveRuntimeSessionState Staged => _staged ??
            throw new AssertionException("The late-game session was not staged.");

        [OneTimeSetUp]
        public void StageLateGameFixture()
        {
            string savePath = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                "save_test.sav");
            Assume.That(File.Exists(savePath),
                $"Late-game save fixture was not found at '{savePath}'.");

            try
            {
                _document = new OriginalSaveArchiveCodec().Read(savePath);
                _snapshot = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(_document));

                _stableRoot = new GameObject(nameof(OriginalSaveZeltTorchPresentationTests));
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                var engineFactory = new DigglesEngineFactory();
                _initial = RuntimeGameSession.CreateInitial(
                    engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        BdbRelativePath),
                    initialRoot);
                var host = new RuntimeWorldSessionHost(
                    engineFactory,
                    new ActiveGameSession(_initial),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                _candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    BdbRelativePath);

                var actionFactory = new OriginalSaveActionFactory(
                    new RuntimeSaveableActionFactory());
                var sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    new OriginalSaveActionInstaller());
                _staged = sessionStager.StageCandidate(
                    _candidate,
                    _snapshot,
                    BdbRelativePath);
            }
            catch
            {
                DisposeFixture();
                throw;
            }
        }

        [OneTimeTearDown]
        public void TearDownFixture() => DisposeFixture();

        [Test]
        public void BoxedTentUsesTransportCratePresentation()
        {
            OriginalSaveRuntimeEntitySnapshot boxedTent = Snapshot.Entities.First(entity =>
                entity.ClassName == "Zelt" &&
                (entity.ObjectFlags & 0x00004000) != 0);
            OriginalSaveObjectDocument archivedTent = Document.Objects?.FindById(boxedTent.ObjectId) ??
                throw new AssertionException($"Zelt object {boxedTent.ObjectId} is missing.");
            DigglesTclEntity stagedTent = Staged.Entities[boxedTent.ObjectId];
            DigglesAnimator animator = stagedTent.GameObject?
                .GetComponentInChildren<DigglesAnimator>(true) ??
                throw new AssertionException($"Zelt object {boxedTent.ObjectId} has no animator.");

            Assert.That(archivedTent.CommonState.State39C[0], Is.EqualTo(12), "N34 model slot");
            Assert.That(boxedTent.InitialAnimationModelDatabaseSlot, Is.EqualTo(6), "N36 model slot");
            Assert.That(animator.CurrentClip?.SourceObjName, Is.EqualTo("kiste"));
            Assert.That(animator.CurrentClip?.Name, Is.EqualTo("standard"));
        }

        [Test]
        public void WallTorchUsesArchivedRotationThroughNativeConversion()
        {
            OriginalSaveRuntimeEntitySnapshot savedTorch = Snapshot.EntitiesById[30];
            Assert.That(savedTorch.ClassName, Is.EqualTo("Misc_Light"));
            Assert.That(savedTorch.LinkedObjectId, Is.EqualTo(-1));
            DigglesTclEntity stagedTorch = Staged.Entities[savedTorch.ObjectId];
            Transform model = stagedTorch.GameObject?.transform.Find("Model") ??
                throw new AssertionException($"Torch object {savedTorch.ObjectId} has no model.");

            Vector3 archivedRotation = new(
                savedTorch.Rotation.X,
                savedTorch.Rotation.Y,
                savedTorch.Rotation.Z);
            Quaternion expected = Quaternion.Euler(-90f, 0f, 0f) * Quaternion.Euler(
                archivedRotation.x * Mathf.Rad2Deg,
                -archivedRotation.y * Mathf.Rad2Deg,
                archivedRotation.z * Mathf.Rad2Deg);

            Assert.That(stagedTorch.Rotation, Is.EqualTo(archivedRotation));
            Assert.That(Quaternion.Angle(model.localRotation, expected), Is.LessThan(0.001f));
        }

        private void DisposeFixture()
        {
            _candidate?.Dispose();
            _candidate = null;
            _initial?.Dispose();
            _initial = null;
            if (_stableRoot != null) Object.DestroyImmediate(_stableRoot);
            _stableRoot = null;
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }
    }
}
