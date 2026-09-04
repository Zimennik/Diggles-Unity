#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveFixedEngineSynthesisTests
    {
        private const string BdbRelativePath = "BlockDB2.BDB";
        private const string FixtureName = "save_tutorial.sav";

        [Test]
        public void BlockIs205BytesAndReparses()
        {
            using var fixture = new NoBaselineFixture();
            byte[] fixedEngineState = fixture.SynthesizeFixedEngineState();
            OriginalSaveArchiveDocument document = fixture.Assemble(fixedEngineState);
            byte[] payload = document.CopyPayload();
            int fixedOffset = checked((int)document.Metadata.FixedEngineStateOffset);

            Assert.That(fixedEngineState, Has.Length.EqualTo(
                OriginalSaveFixedEngineStateSynthesizer.BlockLength));
            Assert.That(document.Metadata.Version, Is.EqualTo(0xA5));
            Assert.That(document.Metadata.HeaderText, Is.Empty);
            Assert.That(BitConverter.ToInt32(payload, sizeof(int)), Is.EqualTo(1));
            Assert.That(payload[sizeof(int) * 2], Is.Zero);
            Assert.That(BitConverter.ToDouble(payload, sizeof(int) * 2 + sizeof(byte)),
                Is.EqualTo(fixture.Capture.GameTime));
            Assert.That(BitConverter.ToInt32(payload, fixedOffset - sizeof(int)), Is.EqualTo(1));
            Assert.That(BitConverter.ToInt32(payload, fixedOffset + fixedEngineState.Length),
                Is.EqualTo(2));
            Assert.That(BitConverter.ToInt32(
                payload,
                fixedOffset + fixedEngineState.Length + sizeof(int)), Is.EqualTo(3));
            Assert.That(document.Metadata.World.SectionOffset,
                Is.EqualTo(fixedOffset + fixedEngineState.Length + sizeof(int) * 2));
        }

        [Test]
        public void CameraAndFogRoundTrip()
        {
            var cameraObject = new GameObject(nameof(CameraAndFogRoundTrip));
            try
            {
                DigglesCameraController camera =
                    cameraObject.AddComponent<DigglesCameraController>();
                camera.FocusX = 101.25f;
                camera.FocusY = -31.5f;
                camera.Zoom = 2.75f;
                camera.Dx = -0.375f;
                camera.Dy = 1.125f;
                FogOfWarState.SaveState fogOfWar = CreateEmptyFogState();
                const double gameTime = 912.5d;
                const float viewBegin = 17.25f;
                var viewBeginStore = new OriginalSaveViewBeginSessionStore(false);
                viewBeginStore.SetViewBegin(viewBegin);

                byte[] block = new OriginalSaveFixedEngineStateSynthesizer().Synthesize(
                    gameTime,
                    camera,
                    viewBeginStore.Value,
                    fogOfWar);

                Assert.That(BitConverter.ToDouble(block, 0), Is.EqualTo(gameTime + 0.0001d));
                Assert.That(BitConverter.ToDouble(block, 8), Is.Zero);
                Assert.That(BitConverter.ToDouble(block, 16), Is.Zero);
                Assert.That(BitConverter.ToSingle(block, 24), Is.EqualTo(-1000f));
                Assert.That(BitConverter.ToSingle(block, 28), Is.EqualTo(-1000f));
                Assert.That(BitConverter.ToSingle(block, 32), Is.EqualTo(camera.Zoom));
                Assert.That(block[36], Is.Zero);
                Assert.That(BitConverter.ToSingle(block, 37), Is.EqualTo(camera.Dx));
                Assert.That(BitConverter.ToSingle(block, 41), Is.EqualTo(camera.Dy));
                Assert.That(BitConverter.ToSingle(block, 45), Is.EqualTo(camera.FocusX));
                Assert.That(BitConverter.ToSingle(block, 49), Is.EqualTo(camera.FocusY));
                Assert.That(BitConverter.ToSingle(block, 53), Is.EqualTo(camera.Zoom));
                Assert.That(BitConverter.ToSingle(block, 57), Is.EqualTo(camera.Dx));
                Assert.That(BitConverter.ToSingle(block, 61), Is.EqualTo(camera.Dy));
                Assert.That(BitConverter.ToInt32(block, 65), Is.Zero);
                Assert.That(BitConverter.ToInt32(block, 69), Is.Zero);
                Assert.That(BitConverter.ToSingle(block, 73), Is.EqualTo(viewBegin));
                Assert.That(new OriginalSaveViewBeginSessionStore(true).Value,
                    Is.EqualTo(OriginalSaveFixedEngineStateSynthesizer.CampaignViewBegin));
                for (int player = 0; player < FogOfWarState.Players; player++)
                {
                    int rectangleOffset = 77 + player * 4 * sizeof(int);
                    Assert.That(BitConverter.ToInt32(block, rectangleOffset),
                        Is.EqualTo(0x8000));
                    Assert.That(BitConverter.ToInt32(block, rectangleOffset + sizeof(int)),
                        Is.EqualTo(0x8000));
                    Assert.That(BitConverter.ToInt32(block, rectangleOffset + sizeof(int) * 2),
                        Is.EqualTo(-1));
                    Assert.That(BitConverter.ToInt32(block, rectangleOffset + sizeof(int) * 3),
                        Is.EqualTo(-1));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void FullDocumentReparsesAndPreflights()
        {
            using var fixture = new NoBaselineFixture();
            OriginalSaveArchiveDocument document = fixture.Assemble(
                fixture.SynthesizeFixedEngineState());

            OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(document);
            OriginalSaveRuntimeSnapshot restaged = new OriginalSaveRuntimeStager().Stage(plan);
            OriginalSaveAnimationControllerDocument animations =
                document.AnimationControllers ??
                throw new AssertionException("The assembled document has no animation table.");
            OriginalSaveTrailingPaddingDocument trailingPadding = document.TrailingPadding ??
                throw new AssertionException("The assembled document has no trailing padding.");

            Assert.That(document.World.FogBeginRows, Is.All.EqualTo(-1));
            Assert.That(document.World.GroundBegin, Is.EqualTo(fixture.Capture.GroundBeginRow));
            Assert.That(restaged.Entities, Has.Count.EqualTo(fixture.Capture.Entities.Length));
            Assert.That(animations.Records, Has.Count.EqualTo(
                fixture.Capture.AnimationControllers.Length));
            for (int index = 0; index < fixture.Capture.AnimationControllers.Length; index++)
            {
                Assert.That(animations.Records[index].Values, Is.EqualTo(
                    fixture.Capture.AnimationControllers[index].FrameValues));
            }
            Assert.That(trailingPadding.Length, Is.EqualTo(0x1000));
            Assert.That(trailingPadding.IsCanonicalZeroPadding, Is.True);
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

        private static OriginalSaveRuntimeSnapshot ReadFixture()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                FixtureName);
            Assume.That(File.Exists(path),
                $"Original save fixture was not found at '{path}'.");
            OriginalSaveArchiveDocument document = new OriginalSaveArchiveCodec().Read(path);
            return new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
        }

        private sealed class NoBaselineFixture : IDisposable
        {
            private readonly SessionHarness _harness;
            private readonly RuntimeGameSession _candidate;
            private readonly GameObject _cameraObject;

            internal NoBaselineFixture()
            {
                OriginalSaveRuntimeSnapshot snapshot = ReadFixture();
                _harness = new SessionHarness();
                _candidate = _harness.CreateCandidate();
                _harness.StageForCapture(_candidate, snapshot);
                Context = _candidate.Engine.Ctx;
                Capture = new OriginalSaveRuntimePatchMapper().CaptureCanonical(null, Context);
                _cameraObject = new GameObject(nameof(OriginalSaveFixedEngineSynthesisTests));
                Camera = _cameraObject.AddComponent<DigglesCameraController>();
                Camera.FocusX = 181.5f;
                Camera.FocusY = 44.25f;
                Camera.Zoom = 1.85f;
                Camera.Dx = -0.2f;
                Camera.Dy = 0.4f;
                ViewBeginStore = new OriginalSaveViewBeginSessionStore(Context.IsStoryMgr);
            }

            internal DigglesTclContext Context { get; }
            internal OriginalSaveCanonicalCapture Capture { get; }
            internal DigglesCameraController Camera { get; }
            internal OriginalSaveViewBeginSessionStore ViewBeginStore { get; }

            internal byte[] SynthesizeFixedEngineState()
            {
                FogOfWarState.SaveState fogOfWar = Capture.FogOfWar ??
                    throw new AssertionException("The no-baseline capture has no fog state.");
                return new OriginalSaveFixedEngineStateSynthesizer().Synthesize(
                    Capture.GameTime,
                    Camera,
                    ViewBeginStore.Value,
                    fogOfWar);
            }

            internal OriginalSaveArchiveDocument Assemble(byte[] fixedEngineState)
            {
                return new OriginalSaveCurrentArchiveWriter(new OriginalSaveArchiveReader())
                    .Assemble(Capture, fixedEngineState, Context.LightBeginRow);
            }

            public void Dispose()
            {
                _candidate.Dispose();
                UnityEngine.Object.DestroyImmediate(_cameraObject);
                _harness.Dispose();
            }
        }

        private sealed class SessionHarness : IDisposable
        {
            private readonly GameObject _stableRoot;
            private readonly IDigglesEngineFactory _engineFactory;
            private readonly RuntimeGameSession _initialSession;
            private readonly IRuntimeWorldSessionHost _sessionHost;
            private readonly IOriginalSaveRuntimeSessionStager _sessionStager;
            private readonly IOriginalSaveActionInstaller _actionInstaller;

            internal SessionHarness()
            {
                _stableRoot = new GameObject(nameof(OriginalSaveFixedEngineSynthesisTests));
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                _engineFactory = new DigglesEngineFactory();
                _initialSession = RuntimeGameSession.CreateInitial(
                    _engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        BdbRelativePath),
                    initialRoot);
                _sessionHost = new RuntimeWorldSessionHost(
                    _engineFactory,
                    new ActiveGameSession(_initialSession),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                var actionFactory = new OriginalSaveActionFactory(
                    new RuntimeSaveableActionFactory());
                _actionInstaller = new OriginalSaveActionInstaller();
                _sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    _actionInstaller);
            }

            internal RuntimeGameSession CreateCandidate()
            {
                return _sessionHost.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    BdbRelativePath);
            }

            internal void StageForCapture(
                RuntimeGameSession candidate,
                OriginalSaveRuntimeSnapshot snapshot)
            {
                OriginalSaveRuntimeSessionState state =
                    _sessionStager.StageCandidate(candidate, snapshot, BdbRelativePath);
                // StageCandidate already installs prepared actions internally;
                // a second Install would trip the already-has-action guard.
                _actionInstaller.InstallPendingEntityEvents(
                    state.PendingEntityEvents,
                    state.Entities,
                    candidate.Engine.Ctx,
                    snapshot.GameTime);
            }

            public void Dispose()
            {
                _initialSession.Dispose();
                UnityEngine.Object.DestroyImmediate(_stableRoot);
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }
    }
}
