#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSavePlayerFacingWiringTests
    {
        private const string FixtureName = "save_tutorial.sav";

        [TestCase("slot", "slot.sav")]
        [TestCase("slot.sav", "slot.sav")]
        [TestCase("slot.profile", "slot.profile.sav")]
        public void BackendAppendsSavAndWritesInsideGamesave(
            string name,
            string expectedFileName)
        {
            using var fixture = new BackendFixture();

            string path = fixture.Backend.Save(name);

            Assert.That(path, Is.EqualTo(Path.GetFullPath(Path.Combine(
                fixture.DataRoot,
                "Gamesave",
                expectedFileName))));
            Assert.That(fixture.ArchiveService.AssembleWriteCount, Is.EqualTo(1));
            Assert.That(fixture.ArchiveService.LastPath, Is.EqualTo(path));
        }

        [TestCase("..")]
        [TestCase("../escape")]
        [TestCase("folder/escape")]
        [TestCase(@"folder\escape")]
        public void BackendRejectsUnsafeSaveNames(string name)
        {
            using var fixture = new BackendFixture();

            Assert.Throws<ArgumentException>(() => fixture.Backend.Save(name));
            Assert.That(fixture.ArchiveService.TotalWriteCount, Is.Zero);
        }

        [Test]
        public void BackendRejectsAbsoluteSavePaths()
        {
            using var fixture = new BackendFixture();
            string absolutePath = Path.GetFullPath(Path.Combine(
                fixture.DataRoot,
                "outside.sav"));

            Assert.Throws<ArgumentException>(() => fixture.Backend.Save(absolutePath));
            Assert.That(fixture.ArchiveService.TotalWriteCount, Is.Zero);
        }

        [Test]
        public void BackendSelectsBaselinePatchOrNoBaselineAssembly()
        {
            OriginalSaveRuntimeSnapshot snapshot = ReadFixtureSnapshot();
            using var fixture = new BackendFixture();
            fixture.Store.SetBaseline(fixture.Session, snapshot);

            fixture.Backend.Save("baseline");

            Assert.That(fixture.ArchiveService.BaselineWriteCount, Is.EqualTo(1));
            Assert.That(fixture.ArchiveService.AssembleWriteCount, Is.Zero);
            Assert.That(fixture.ArchiveService.LastBaseline, Is.SameAs(snapshot));
            fixture.Store.Clear();

            fixture.Backend.Save("assembled");

            Assert.That(fixture.ArchiveService.BaselineWriteCount, Is.EqualTo(1));
            Assert.That(fixture.ArchiveService.AssembleWriteCount, Is.EqualTo(1));
            Assert.That(fixture.ArchiveService.LastCamera, Is.SameAs(fixture.Camera));
            Assert.That(fixture.Store.Baseline, Is.Null);
        }

        [Test]
        public void FixedEngineStateReaderRoundTripsSynthesizedCameraBits()
        {
            var cameraObject = new GameObject(nameof(
                FixedEngineStateReaderRoundTripsSynthesizedCameraBits));
            try
            {
                DigglesCameraController camera =
                    cameraObject.AddComponent<DigglesCameraController>();
                camera.FocusX = 101.25f;
                camera.FocusY = -31.5f;
                camera.Zoom = 2.75f;
                camera.Dx = -0.375f;
                camera.Dy = 1.125f;
                const float viewBegin = 17.25f;
                byte[] block = new OriginalSaveFixedEngineStateSynthesizer().Synthesize(
                    912.5d,
                    camera,
                    viewBegin,
                    CreateEmptyFogState());

                OriginalSaveFixedEngineState state =
                    OriginalSaveFixedEngineStateReader.Read(block);

                AssertSameBits(camera.FocusX, state.FocusX);
                AssertSameBits(camera.FocusY, state.FocusY);
                AssertSameBits(camera.Zoom, state.Zoom);
                AssertSameBits(camera.Dx, state.Dx);
                AssertSameBits(camera.Dy, state.Dy);
                AssertSameBits(viewBegin, state.ViewBegin);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void QuickSaveNameHasOriginalShape()
        {
            string name = QuickSaveHotkeyService.CreateQuickSaveName(
                new DateTime(2026, 7, 17, 1, 35, 0));

            Assert.That(name, Does.Match(
                @"^quick_Quicksave_[0-9]{8}_[0-9]{4}$"));
            Assert.That(name, Is.EqualTo("quick_Quicksave_20260717_0135"));
        }

        private static OriginalSaveRuntimeSnapshot ReadFixtureSnapshot()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                FixtureName);
            Assume.That(File.Exists(path),
                $"Original save fixture was not found at '{path}'.");
            OriginalSaveArchiveDocument document =
                new OriginalSaveArchiveCodec().Read(path);
            return new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
        }

        private static FogOfWarState.SaveState CreateEmptyFogState()
        {
            var exploredRect = new int[FogOfWarState.Players * 4];
            for (int player = 0; player < FogOfWarState.Players; player++)
            {
                int offset = player * 4;
                exploredRect[offset] = 0x8000;
                exploredRect[offset + 1] = 0x8000;
                exploredRect[offset + 2] = -0x8000;
                exploredRect[offset + 3] = -0x8000;
            }
            return new FogOfWarState.SaveState
            {
                ExploredRect = exploredRect,
            };
        }

        private static void AssertSameBits(float expected, float actual)
        {
            Assert.That(
                BitConverter.SingleToInt32Bits(actual),
                Is.EqualTo(BitConverter.SingleToInt32Bits(expected)));
        }

        private sealed class BackendFixture : IDisposable
        {
            private readonly GameObject _cameraObject;

            internal BackendFixture()
            {
                DataRoot = Path.Combine(
                    Path.GetTempPath(),
                    nameof(OriginalSavePlayerFacingWiringTests),
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DataRoot);
                var root = new GameObject("WorldContent.BackendTest");
                Session = RuntimeGameSession.CreateInitial(
                    new DigglesEngineFactory().Create(
                        DataRoot,
                        root.transform,
                        "unused.bdb"),
                    root);
                var activeSession = new ActiveGameSession(Session);
                _cameraObject = new GameObject("BackendTestCamera");
                Camera = _cameraObject.AddComponent<DigglesCameraController>();
                ArchiveService = new RecordingArchiveService();
                Store = new OriginalSaveBaselineSessionStore();
                Backend = new OriginalSaveGameBackend(
                    ArchiveService,
                    new ThrowingLoadService(),
                    Store,
                    activeSession,
                    () => Camera);
            }

            internal string DataRoot { get; }
            internal RuntimeGameSession Session { get; }
            internal DigglesCameraController Camera { get; }
            internal RecordingArchiveService ArchiveService { get; }
            internal OriginalSaveBaselineSessionStore Store { get; }
            internal OriginalSaveGameBackend Backend { get; }

            public void Dispose()
            {
                Session.Dispose();
                UnityEngine.Object.DestroyImmediate(_cameraObject);
                if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, true);
            }
        }

        private sealed class RecordingArchiveService :
            IOriginalSaveRuntimeArchiveService
        {
            internal int BaselineWriteCount { get; private set; }
            internal int AssembleWriteCount { get; private set; }
            internal int TotalWriteCount => BaselineWriteCount + AssembleWriteCount;
            internal string? LastPath { get; private set; }
            internal OriginalSaveRuntimeSnapshot? LastBaseline { get; private set; }
            internal DigglesCameraController? LastCamera { get; private set; }

            public void CaptureAndWriteAtomic(
                string path,
                OriginalSaveRuntimeSnapshot baseline,
                DigglesTclContext context)
            {
                BaselineWriteCount++;
                LastPath = path;
                LastBaseline = baseline;
            }

            public void CaptureAndWriteAtomic(
                string path,
                DigglesTclContext context,
                DigglesCameraController camera,
                OriginalSaveViewBeginSessionStore viewBeginStore)
            {
                AssembleWriteCount++;
                LastPath = path;
                LastCamera = camera;
            }

            public void CaptureAndWriteAtomic(
                string path,
                DigglesTclContext context,
                DigglesCameraController camera,
                float viewBegin)
            {
                AssembleWriteCount++;
                LastPath = path;
                LastCamera = camera;
            }

            public bool WriteInFlight => false;

            public System.Threading.Tasks.Task<string> CaptureAndWriteAtomicAsync(
                string path,
                OriginalSaveRuntimeSnapshot baseline,
                DigglesTclContext context)
            {
                CaptureAndWriteAtomic(path, baseline, context);
                return System.Threading.Tasks.Task.FromResult(path);
            }

            public System.Threading.Tasks.Task<string> CaptureAndWriteAtomicAsync(
                string path,
                DigglesTclContext context,
                DigglesCameraController camera,
                OriginalSaveViewBeginSessionStore viewBeginStore)
            {
                CaptureAndWriteAtomic(path, context, camera, viewBeginStore);
                return System.Threading.Tasks.Task.FromResult(path);
            }
        }

        private sealed class ThrowingLoadService : IOriginalSaveGameLoadService
        {
            public OriginalSaveGameLoadResult LoadGame(
                string savePathOrName,
                bool raiseGamestart = true)
            {
                throw new InvalidOperationException("Load was not expected in this test.");
            }
        }
    }
}
