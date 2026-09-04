#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // Unity's bundled NUnit predates [NonParallelizable]; EditMode tests run serially anyway.
    public sealed class OriginalSaveActivationTests
    {
        private const string BdbRelativePath = "BlockDB2.BDB";

        [Test]
        [Timeout(3600000)] // ~100 full-world stagings + periodic native-asset unloads exceed NUnit's 180 s default.
        public void EveryCorpusFixtureActivatesWithNoBlockers()
        {
            string[] savePaths = FindCorpusPaths();
            savePaths = AssertCorpusIsCurated(savePaths);

            using var harness = new SessionHarness();
            int stagedCount = 0;
            foreach (string savePath in savePaths)
            {
                OriginalSaveRuntimeSnapshot snapshot = harness.Preparation.Prepare(savePath);
                RuntimeGameSession candidate = harness.CreateCandidate();
                try
                {
                    OriginalSaveRuntimeSessionState staged = harness.SessionStager.StageCandidate(
                        candidate,
                        snapshot,
                        BdbRelativePath);
                    Assert.That(
                        staged.ComputeActivationBlockers(),
                        Is.Empty,
                        Path.GetFileName(savePath));
                    Assert.That(
                        staged.IsActivationReady,
                        Is.True,
                        Path.GetFileName(savePath));
                }
                finally
                {
                    candidate.Dispose();
                    // DestroyImmediate on the session root releases the GameObjects but NOT the
                    // procedural chunk meshes/textures still registered as native assets — 103
                    // staged full worlds otherwise exhaust process memory and the batch runner
                    // dies mid-test (observed only runs containing this test were
                    // killed). Reclaim unreferenced native assets between fixtures.
                    if (++stagedCount % 4 == 0)
                    {
                        UnityEditor.EditorUtility.UnloadUnusedAssetsImmediate();
                        GC.Collect();
                    }
                }
            }
        }

        [Test]
        [Timeout(1800000)]
        public void RichFixtureRoundTripsByteExactly()
        {
            // D4 anchor: the richest campaign quicksave must survive
            // stage → capture → assemble → byte-compare → reparse → re-stage.
            using var harness = new SessionHarness();
            using var camera = new ScratchCamera();
            AssertFixtureRoundTrips(
                harness,
                FixturePath("quick_Quicksave_20201220_2057.sav"),
                camera.Controller);
        }

        [Test]
        [Timeout(7200000)] // Three full-world stagings per fixture.
        public void EveryCorpusFixtureRoundTripsByteExactly()
        {
            string[] savePaths = FindCorpusPaths();
            savePaths = AssertCorpusIsCurated(savePaths);

            // 309 staged worlds do not fit one editor process (two corpus runs died of native
            // memory exhaustion on despite per-fixture unloads), so the test runner
            // shards the corpus across fresh processes: DIGGLES_CORPUS_SLICE="i/n" selects
            // every n-th fixture starting at i. Unset runs the whole corpus (CI-of-the-future).
            string? slice = Environment.GetEnvironmentVariable("DIGGLES_CORPUS_SLICE");
            if (!string.IsNullOrEmpty(slice))
            {
                string[] parts = slice.Split('/');
                int index = int.Parse(parts[0]);
                int total = int.Parse(parts[1]);
                savePaths = savePaths.Where((_, i) => i % total == index).ToArray();
            }
            // Name-targeted run (artifact generation for the E2 acceptance harness): sorted
            // indices are unreliable — the shared directory churns under an external sync.
            string? only = Environment.GetEnvironmentVariable("DIGGLES_CORPUS_ONLY");
            if (!string.IsNullOrEmpty(only))
            {
                savePaths = savePaths
                    .Where(path => string.Equals(
                        Path.GetFileName(path), only, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.That(savePaths, Is.Not.Empty,
                    $"corpus fixture '{only}' was not found");
            }

            using var harness = new SessionHarness();
            using var camera = new ScratchCamera();
            foreach (string savePath in savePaths)
            {
                Debug.Log($"[D4] round-tripping {Path.GetFileName(savePath)}");
                AssertFixtureRoundTrips(harness, savePath, camera.Controller);
                UnityEditor.EditorUtility.UnloadUnusedAssetsImmediate();
                GC.Collect();
            }
        }

        // D4: the round-trip path a LOADED original save takes through an in-game save — the
        // baseline patch writer (capture patch vs the retained source document, Rebuild). An
        // unchanged staged session must reproduce the source archive byte-for-byte; the rebuilt
        // archive must also reparse and re-stage with zero blockers. (The no-baseline Assemble
        // writer is the NEW-CAMPAIGN path — D5/E2 territory — and is exercised separately as a
        // reparse/re-stage gate without a byte comparison.)
        private static void AssertFixtureRoundTrips(
            SessionHarness harness,
            string savePath,
            DigglesCameraController camera)
        {
            string label = Path.GetFileName(savePath);
            var codec = new OriginalSaveArchiveCodec();
            OriginalSaveRuntimeSnapshot snapshot = harness.Preparation.Prepare(savePath);

            // Both writers capture from ONE staged session, which is disposed before any
            // re-staging: at most one staged full world is alive at a time (three concurrently
            // resident worlds exhausted native memory on the corpus run).
            OriginalSaveArchiveDocument rebuilt;
            OriginalSaveArchiveDocument assembled;
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                harness.StageForCapture(candidate, snapshot);
                DigglesTclContext context = candidate.Engine.Ctx;
                var mapper = new OriginalSaveRuntimePatchMapper();
                OriginalSaveCurrentArchivePatch patch = mapper.Capture(snapshot, context);
                var writer = new OriginalSaveCurrentArchiveWriter(new OriginalSaveArchiveReader());
                rebuilt = writer.Rebuild(snapshot.SourceDocument, patch);

                // DIGGLES_D4_NORETAIN=1: build the assembled leg from a PURE live capture
                // (no retained residue) — the exact shape the no-baseline service writes for
                // fresh campaigns; used with DIGGLES_D4_KEEP to generate acceptance artifacts.
                OriginalSaveCanonicalCapture capture = mapper.CaptureCanonical(
                    Environment.GetEnvironmentVariable("DIGGLES_D4_NORETAIN") == "1"
                        ? null
                        : snapshot,
                    context);
                var fogOfWar = capture.FogOfWar ??
                    throw new AssertionException($"{label}: the capture has no fog state.");
                byte[] fixedEngineState =
                    new OriginalSaveFixedEngineStateSynthesizer().Synthesize(
                        capture.GameTime,
                        camera,
                        new OriginalSaveViewBeginSessionStore(context.IsStoryMgr).Value,
                        fogOfWar);
                assembled = writer.Assemble(capture, fixedEngineState, context.LightBeginRow);
            }
            finally
            {
                candidate.Dispose();
            }

            // The rebuilt archive is written BEFORE the byte gate and kept when the gate
            // fails, so mismatches can be diffed offline (scratch savprobe tool) without
            // re-running a staging session.
            // DIGGLES_D4_KEEP=1 preserves BOTH artifacts on success too — the E2 acceptance
            // harness feeds them to the original executable.
            bool keepForAcceptance =
                Environment.GetEnvironmentVariable("DIGGLES_D4_KEEP") == "1";
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"diggles-d4-{Path.GetFileNameWithoutExtension(savePath)}.sav");
            bool keepArtifact = true;
            try
            {
                codec.Write(tempPath, rebuilt);
                AssertPayloadsBytesEqual(snapshot.SourceDocument, rebuilt, tempPath, label);
                AssertArchiveRestages(harness, tempPath, snapshot.Entities.Count, label);
                keepArtifact = keepForAcceptance;
            }
            finally
            {
                if (!keepArtifact) File.Delete(tempPath);
            }

            string assembledPath = Path.Combine(
                Path.GetTempPath(),
                keepForAcceptance
                    ? $"diggles-d5-{Path.GetFileNameWithoutExtension(savePath)}.sav"
                    : $"diggles-d5-{Guid.NewGuid():N}.sav");
            try
            {
                codec.Write(assembledPath, assembled);
                AssertArchiveRestages(
                    harness,
                    assembledPath,
                    snapshot.Entities.Count,
                    $"{label} (assembled)");
            }
            finally
            {
                if (!keepForAcceptance) File.Delete(assembledPath);
            }
        }

        private static void AssertArchiveRestages(
            SessionHarness harness,
            string archivePath,
            int expectedEntityCount,
            string label)
        {
            OriginalSaveRuntimeSnapshot reparsed = harness.Preparation.Prepare(archivePath);
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                OriginalSaveRuntimeSessionState restaged = harness.SessionStager.StageCandidate(
                    candidate,
                    reparsed,
                    BdbRelativePath);
                Assert.That(restaged.ComputeActivationBlockers(), Is.Empty, label);
                Assert.That(restaged.IsActivationReady, Is.True, label);
                Assert.That(restaged.Entities, Has.Count.EqualTo(expectedEntityCount), label);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        // The baseline patch writer must reproduce an unchanged session's source archive
        // byte-for-byte — the whole payload, no excluded regions (Rebuild carries every
        // untouched byte verbatim and the restored session's live values equal the archived
        // ones bit-exactly).
        private static void AssertPayloadsBytesEqual(
            OriginalSaveArchiveDocument original,
            OriginalSaveArchiveDocument rebuilt,
            string rebuiltArtifactPath,
            string label)
        {
            byte[] a = original.CopyPayload();
            byte[] b = rebuilt.CopyPayload();
            Assert.That(b.Length, Is.EqualTo(a.Length),
                $"{label}: payload length (rebuilt kept at {rebuiltArtifactPath})");
            for (int index = 0; index < a.Length; index++)
            {
                if (a[index] == b[index]) continue;
                Assert.Fail(
                    $"{label}: byte mismatch at payload offset 0x{index:X}: " +
                    $"0x{a[index]:X2} vs 0x{b[index]:X2} " +
                    $"(rebuilt kept at {rebuiltArtifactPath})");
            }
        }

        [Test]
        public void RichCampaignQuicksaveStagesFully()
        {
            // A late-campaign quicksave with a RUNNING economy: 5 Laufrad + 4 Dampfmaschine
            // declared, populated CStoryMgr, and the modded-era Holzkiepe_ entities — the widest
            // single-fixture campaign coverage in the corpus (probed).
            const string fileName = "quick_Quicksave_20201220_2057.sav";
            string savePath = FixturePath(fileName);
            using var harness = new SessionHarness(traceCandidateObjectTcl: true);
            OriginalSaveRuntimeSnapshot snapshot = harness.Preparation.Prepare(savePath);
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                OriginalSaveRuntimeSessionState staged = harness.SessionStager.StageCandidate(
                    candidate,
                    snapshot,
                    BdbRelativePath);

                Assert.That(snapshot.Entities, Has.Count.GreaterThan(400),
                    "the anchor must stay a populated late-campaign save");
                Assert.That(staged.Entities, Has.Count.EqualTo(snapshot.Entities.Count));
                Assert.That(snapshot.Story.Events, Is.Not.Empty);
                Assert.That(candidate.Engine.Ctx.StoryEventOrder,
                    Has.Count.EqualTo(snapshot.Story.Events.Count));

                Assert.That(staged.PendingEntityEvents,
                    Has.Count.EqualTo(snapshot.Entities.Count));
                int expectedPendingCount = 0;
                int stagedPendingCount = 0;
                foreach (OriginalSaveRuntimeEntitySnapshot entity in snapshot.Entities)
                {
                    IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot> stagedEvents =
                        staged.PendingEntityEvents[entity.ObjectId];
                    Assert.That(stagedEvents, Is.SameAs(entity.PendingEvents));
                    expectedPendingCount += entity.PendingEvents.Count;
                    stagedPendingCount += stagedEvents.Count;
                }
                Assert.That(stagedPendingCount, Is.EqualTo(expectedPendingCount));

                OriginalSaveRuntimeEntitySnapshot[] productionEntities = snapshot.Entities
                    .Where(entity => entity.Production != null)
                    .ToArray();
                Assert.That(productionEntities, Is.Not.Empty);
                Assert.That(productionEntities.All(entity =>
                    !string.IsNullOrEmpty(staged.Entities[entity.ObjectId].ProdState)), Is.True);
                Assert.That(snapshot.Energy.Sources, Is.Not.Empty);
                Assert.That(candidate.Engine.Ctx.Energy.Capture().Sources,
                    Has.Length.EqualTo(snapshot.Energy.Sources.Count));
                Assert.That(staged.ComputeActivationBlockers(), Is.Empty);
                Assert.That(harness.TracingFactory.ObjectTclBeforeActivation, Is.Zero,
                harness.TracingFactory.OffenderSummary);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void DegenerateModClassShellPinned()
        {
            // Entity 321 is class 'Holzkiepe_' (BetterStorage mod) in this fixture — corpus-probed
            //; the 2022 quicksaves carry the class under different entity ids.
            const string fileName = "quick_Quicksave_20201220_2007.sav";
            using var harness = new SessionHarness();
            OriginalSaveRuntimeSnapshot snapshot = harness.Preparation.Prepare(FixturePath(fileName));
            RuntimeGameSession candidate = harness.CreateCandidate();
            try
            {
                OriginalSaveRuntimeSessionState staged = harness.SessionStager.StageCandidate(
                    candidate,
                    snapshot,
                    BdbRelativePath);
                OriginalSaveRuntimeEntitySnapshot saved = snapshot.EntitiesById[321];
                DigglesTclEntity shell = staged.Entities[321];
                DigglesClass shellClass = shell.Class ??
                    throw new AssertionException("The degenerate entity has no placeholder class.");

                Assert.That(saved.ClassName, Is.EqualTo("Holzkiepe_"));
                Assert.That(shellClass.Name, Is.EqualTo(saved.ClassName));
                Assert.That(shellClass.DisableScripting, Is.True);
                Assert.That(shellClass.Kind, Is.Empty);
                Assert.That(
                    candidate.Engine.Ctx.Entities.Values
                        .Where(entity => entity.Class?.Kind == "tool"),
                    Has.No.Member(shell));
                Assert.That(staged.ComputeActivationBlockers(), Is.Empty);
                Assert.That(staged.ComputeActivationBlockers().Any(blocker =>
                    blocker.Contains(saved.ClassName, StringComparison.Ordinal)), Is.False);
                Assert.That(staged.Warnings.Any(warning =>
                    warning.Contains(saved.ClassName, StringComparison.Ordinal)), Is.True);
            }
            finally
            {
                candidate.Dispose();
            }
        }

        [Test]
        public void MalformedArchiveNeverMutatesLiveSession()
        {
            using var harness = new SessionHarness();
            string corruptPath = CreatePreflightCorruptArchive();
            RuntimeGameSession liveSession = harness.ActiveSession.Current;
            DigglesTclContext liveContext = liveSession.Engine.Ctx;
            GameObject liveRoot = liveSession.ContentRoot;
            try
            {
                Assert.Throws<InvalidDataException>(() =>
                    harness.LoadService.LoadGame(corruptPath, raiseGamestart: false));

                Assert.That(harness.ActiveSession.Current, Is.SameAs(liveSession));
                Assert.That(harness.ActiveSession.Current.Engine.Ctx, Is.SameAs(liveContext));
                Assert.That(harness.ActiveSession.Current.ContentRoot, Is.SameAs(liveRoot));
                Assert.That(liveSession.State, Is.EqualTo(RuntimeGameSessionState.Active));
                Assert.That(liveRoot.activeSelf, Is.True);
            }
            finally
            {
                if (File.Exists(corruptPath)) File.Delete(corruptPath);
            }
        }

        [Test]
        public void LoadServiceActivatesTutorialEndToEnd()
        {
            using var harness = new SessionHarness(traceCandidateObjectTcl: true);
            RuntimeGameSession oldSession = harness.InitialSession;
            DigglesTclContext oldContext = oldSession.Engine.Ctx;

            OriginalSaveGameLoadResult result = harness.LoadService.LoadGame(
                "save_tutorial.sav",
                raiseGamestart: true);

            RuntimeGameSession loaded = harness.ActiveSession.Current;
            OriginalSaveRuntimeSessionState staged = loaded.OriginalSaveState ??
                throw new AssertionException("The active tutorial session has no staged save state.");
            Assert.That(loaded, Is.Not.SameAs(oldSession));
            Assert.That(loaded.State, Is.EqualTo(RuntimeGameSessionState.Active));
            // contract: the replaced session is DISPOSED after commit (its full world
            // previously leaked into the scene on every in-game load) and its root destroyed.
            Assert.That(oldSession.State, Is.EqualTo(RuntimeGameSessionState.Disposed));
            Assert.That(loaded.ContentRoot.activeSelf, Is.True);
            Assert.That(oldSession.ContentRoot == null, Is.True,
                "the retired world's content root must be destroyed");
            Assert.That(loaded.Engine.Ctx, Is.Not.SameAs(oldContext));
            Assert.That(loaded.Engine.Ctx.Entities, Has.Count.EqualTo(staged.Entities.Count));
            Assert.That(loaded.Engine.Ctx.Terrain, Is.SameAs(staged.Terrain));
            Assert.That(loaded.Engine.Map.LiveMode, Is.True);
            Assert.That(oldContext.Terrain, Is.Not.SameAs(staged.Terrain));
            Assert.That(result.LoadedName, Is.EqualTo("save_tutorial"));
            Assert.That(result.EntityCount, Is.EqualTo(staged.Entities.Count));
            Assert.That(result.Warnings, Is.EqualTo(staged.Warnings));
            Assert.That(staged.PendingEntityEventsInstalled, Is.True);
            Assert.That(staged.PublishedSystemEventRecipients,
                Has.Count.EqualTo(1));
            Assert.That(staged.PublishedSystemEventRecipients.TryGetValue(
                OriginalSaveGameLoadService.GamestartEventName,
                out int recipientCount), Is.True);
            Assert.That(recipientCount, Is.GreaterThan(0));
            Assert.That(CountQueuedEvent(
                loaded.Engine.Ctx,
                OriginalSaveGameLoadService.GamestartEventName),
                Is.EqualTo(recipientCount));
            Assert.That(harness.TracingFactory.ObjectTclBeforeActivation, Is.Zero,
                harness.TracingFactory.OffenderSummary);

            using var pendingEventHarness = new SessionHarness(
                traceCandidateObjectTcl: true);
            pendingEventHarness.LoadService.LoadGame(
                "save_tutorial3.sav",
                raiseGamestart: false);
            RuntimeGameSession pendingEventSession =
                pendingEventHarness.ActiveSession.Current;
            OriginalSaveRuntimeSessionState pendingEventState =
                pendingEventSession.OriginalSaveState ??
                throw new AssertionException(
                    "The active tutorial 3 session has no staged save state.");
            Assert.That(pendingEventState.PendingEntityEvents[1827],
                Has.Count.EqualTo(51));
            Assert.That(pendingEventState.PendingEntityEvents[1828],
                Has.Count.EqualTo(51));
            Assert.That(pendingEventState.Entities[1827].PendingEvents,
                Has.Count.EqualTo(51));
            Assert.That(pendingEventState.Entities[1828].PendingEvents,
                Has.Count.EqualTo(51));
            Assert.That(pendingEventHarness.TracingFactory.ObjectTclBeforeActivation,
                Is.Zero);
        }

        [Test]
        public void GamestartNotRaisedOnTclPath()
        {
            using var harness = new SessionHarness(traceCandidateObjectTcl: true);

            harness.LoadService.LoadGame(
                "save_tutorial.sav",
                raiseGamestart: false);

            RuntimeGameSession loaded = harness.ActiveSession.Current;
            OriginalSaveRuntimeSessionState staged = loaded.OriginalSaveState ??
                throw new AssertionException("The active tutorial session has no staged save state.");
            Assert.That(staged.PendingEntityEventsInstalled, Is.True);
            Assert.That(staged.PublishedSystemEventRecipients, Is.Empty);
            Assert.That(CountQueuedEvent(
                loaded.Engine.Ctx,
                OriginalSaveGameLoadService.GamestartEventName), Is.Zero);
            Assert.That(harness.TracingFactory.ObjectTclBeforeActivation, Is.Zero,
                harness.TracingFactory.OffenderSummary);
        }

        private static int CountQueuedEvent(DigglesTclContext context, string eventName) =>
            context.Entities.Values.Sum(entity =>
                entity.PendingEvents.Count(pending =>
                    string.Equals(pending.Name, eventName, StringComparison.Ordinal)));

        // The corpus dir is the LIVE game's Gamesave (resolved through the same config as the
        // game — the Steam install today): the player's own port-era saves (quick_/auto_ 2026
        // stamps, d5_ test names) legitimately coexist there. They are NOT original fixtures —
        // filter them out of the corpus instead of failing, but keep a floor on the fixture
        // count so a truncated collection still trips.
        private static string[] AssertCorpusIsCurated(string[] savePaths)
        {
            string[] fixtures = savePaths
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    return !name.StartsWith("d5_", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("auto_Autosave_20", StringComparison.Ordinal) &&
                        !name.StartsWith("quick_Quicksave_202607", StringComparison.Ordinal) &&
                        !name.StartsWith("quick_Quicksave_2026", StringComparison.Ordinal);
                })
                .ToArray();
            if (fixtures.Length != savePaths.Length)
            {
                Debug.Log($"[corpus] skipping {savePaths.Length - fixtures.Length} " +
                    "port-era save(s) in the shared Gamesave directory");
            }
            Assert.That(fixtures.Length, Is.GreaterThanOrEqualTo(50),
                "the original save corpus looks truncated — check the Gamesave directory");
            return fixtures;
        }

        // TEMPORARY diagnostic (every restored entity spawns as a
        // placeholder) — probes the candidate's model library directly and traces PlayClip.
        [Test]
        public void DiagnoseRestoredPresentation()
        {
            DigglesAnimController.TracePlayClip = true;
            try
            {
                using var harness = new SessionHarness();
                string diagSave = Environment.GetEnvironmentVariable("DIGGLES_DIAG_SAVE") ?? "";
                OriginalSaveRuntimeSnapshot snapshot = harness.Preparation.Prepare(
                    diagSave.Length != 0 ? diagSave : FixturePath("save_tutorial.sav"));
                RuntimeGameSession candidate = harness.CreateCandidate();
                try
                {
                    harness.SessionStager.StageCandidate(candidate, snapshot, BdbRelativePath);
                    DigglesModelLibrary lib = candidate.Engine.Lib;
                    Debug.Log($"[DIAG] ResolveClass Zwerg -> '{lib.ResolveClass("Zwerg")?.Obj ?? "NULL"}'");
                    Debug.Log($"[DIAG] ResolveClass Dummy_Obw_rasen_y -> '{lib.ResolveClass("Dummy_Obw_rasen_y")?.Obj ?? "NULL"}'");
                    Debug.Log($"[DIAG] GetObjDb obw_rasen_y -> '{lib.GetObjDb("obw_rasen_y") ?? "NULL"}'");
                    Debug.Log($"[DIAG] GetClip obw_rasen_y.standard -> {(lib.GetClip("obw_rasen_y", "standard") != null)}");
                    Debug.Log($"[DIAG] GetClip mann.standard -> {(lib.GetClip("mann", "st_stehen_myst") != null)}");
                    int placeholders = 0, models = 0;
                    foreach (DigglesTclEntity entity in candidate.Engine.Ctx.Entities.Values)
                    {
                        GameObject? gameObject = entity.GameObject;
                        if (gameObject == null) continue;
                        bool hasMesh = false;
                        foreach (MeshFilter filter in
                                 gameObject.GetComponentsInChildren<MeshFilter>(true))
                            if (filter.sharedMesh != null) { hasMesh = true; break; }
                        if (hasMesh) models++; else placeholders++;
                    }
                    Debug.Log($"[DIAG] staged presentation: models={models} placeholders/empty={placeholders}");
                }
                finally
                {
                    candidate.Dispose();
                }
            }
            finally
            {
                DigglesAnimController.TracePlayClip = false;
            }
        }

        private static string[] FindCorpusPaths()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static string FixturePath(string fileName)
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                fileName);
            Assume.That(File.Exists(path),
                $"Original save fixture was not found at '{path}'.");
            return path;
        }

        private static string CreatePreflightCorruptArchive()
        {
            OriginalSaveArchiveDocument baseline =
                new OriginalSaveArchiveCodec().Read(FixturePath("save_tutorial3.sav"));
            OriginalSaveObjectCollection objects = baseline.Objects ??
                throw new AssertionException("Tutorial 3 has no typed object collection.");
            OriginalSaveObjectDocument pendingOwner = objects.FindById(1827) ??
                throw new AssertionException("Tutorial 3 object 1827 was not found.");
            int slotIndex = pendingOwner.EventQueueDocument.GetPendingSlotIndex(0);
            int eventNameOffset = checked((int)
                pendingOwner.EventQueueDocument.GetSlotPayloadOffset(slotIndex));
            byte[] payload = baseline.CopyPayload();
            byte[] replacement = Encoding.ASCII.GetBytes("c6_missing_event");
            Array.Clear(payload, eventNameOffset, 32);
            Buffer.BlockCopy(replacement, 0, payload, eventNameOffset, replacement.Length);

            string path = Path.Combine(
                Path.GetTempPath(),
                $"diggles-c6-corrupt-{Guid.NewGuid():N}.sav");
            using var file = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest);
            gzip.Write(payload, 0, payload.Length);
            return path;
        }

        private sealed class SessionHarness : IDisposable
        {
            private readonly GameObject _stableRoot;

            internal SessionHarness(bool traceCandidateObjectTcl = false)
            {
                _stableRoot = new GameObject(nameof(OriginalSaveActivationTests));
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                TracingFactory = new TracingEngineFactory(traceCandidateObjectTcl);
                InitialSession = RuntimeGameSession.CreateInitial(
                    TracingFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        BdbRelativePath),
                    initialRoot);
                ActiveSession = new ActiveGameSession(InitialSession);
                TracingFactory.Bind(ActiveSession);

                var host = new RuntimeWorldSessionHost(
                    TracingFactory,
                    ActiveSession,
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                var actionFactory = new OriginalSaveActionFactory(
                    new RuntimeSaveableActionFactory());
                _actionInstaller = new OriginalSaveActionInstaller();
                SessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    _actionInstaller);
                var codec = new OriginalSaveArchiveCodec();
                Preparation = new OriginalSaveLoadPreparationService(
                    new OriginalSaveRepository(codec),
                    new OriginalSaveLoadPlanner(),
                    new OriginalSaveRuntimeStager());
                SessionHost = host;
                LoadService = new OriginalSaveGameLoadService(
                    Preparation,
                    host,
                    SessionStager,
                    ActiveSession,
                    new OriginalSaveGameLoadConfiguration(BdbRelativePath),
                    new OriginalSaveBaselineSessionStore(),
                    () => null);
            }

            internal TracingEngineFactory TracingFactory { get; }
            internal RuntimeGameSession InitialSession { get; }
            internal ActiveGameSession ActiveSession { get; }
            internal IRuntimeWorldSessionHost SessionHost { get; }
            internal IOriginalSaveRuntimeSessionStager SessionStager { get; }
            internal IOriginalSaveLoadPreparationService Preparation { get; }
            internal OriginalSaveGameLoadService LoadService { get; }

            internal RuntimeGameSession CreateCandidate() => SessionHost.CreateCandidate(
                OriginalDataTestFixture.DataRoot,
                BdbRelativePath);

            internal OriginalSaveRuntimeSessionState StageForCapture(
                RuntimeGameSession candidate,
                OriginalSaveRuntimeSnapshot snapshot)
            {
                OriginalSaveRuntimeSessionState state =
                    SessionStager.StageCandidate(candidate, snapshot, BdbRelativePath);
                // StageCandidate already installs prepared actions internally; pending entity
                // events are installed at commit, so a capture-grade session adds them here.
                _actionInstaller.InstallPendingEntityEvents(
                    state.PendingEntityEvents,
                    state.Entities,
                    candidate.Engine.Ctx,
                    snapshot.GameTime);
                return state;
            }

            private readonly OriginalSaveActionInstaller _actionInstaller;

            public void Dispose()
            {
                RuntimeGameSession current = ActiveSession.Current;
                if (!ReferenceEquals(current, InitialSession)) current.Dispose();
                InitialSession.Dispose();
                UnityEngine.Object.DestroyImmediate(_stableRoot);
            }
        }

        private sealed class ScratchCamera : IDisposable
        {
            private readonly GameObject _gameObject;

            internal ScratchCamera()
            {
                _gameObject = new GameObject(nameof(ScratchCamera));
                Controller = _gameObject.AddComponent<DigglesCameraController>();
            }

            internal DigglesCameraController Controller { get; }

            public void Dispose() => UnityEngine.Object.DestroyImmediate(_gameObject);
        }

        private sealed class TracingEngineFactory : IDigglesEngineFactory
        {
            private readonly bool _traceCandidateObjectTcl;
            private ActiveGameSession? _activeSession;

            internal TracingEngineFactory(bool traceCandidateObjectTcl)
            {
                _traceCandidateObjectTcl = traceCandidateObjectTcl;
            }

            private readonly System.Collections.Generic.List<string> _offenderSamples = new();

            internal int ObjectTclBeforeActivation { get; private set; }
            internal string OffenderSummary => _offenderSamples.Count == 0
                ? "<none>"
                : string.Join("; ", _offenderSamples);

            internal void Bind(ActiveGameSession activeSession) =>
                _activeSession = activeSession;

            public DigglesEngine Create(
                string dataRoot,
                Transform spawnParent,
                string bdbRelativePath)
            {
                var engine = new DigglesEngine();
                engine.Build(dataRoot, spawnParent, bdbRelativePath);
                ActiveGameSession? activeSession = _activeSession;
                if (!_traceCandidateObjectTcl || activeSession == null) return engine;

                engine.Interp.CommandTrace = (command, _, _) =>
                {
                    // The native loader RUNS obj_init per restored shell (the creation callback,
                    // load-shell-interp-re.md) — the stager marks that window and its object-scope
                    // Tcl is lawful constructor code, not a staging leak.
                    if (engine.Ctx.RestoredShellConstructionActive) return;
                    bool candidateIsInactive =
                        !ReferenceEquals(activeSession.Current.Engine, engine);
                    // The established staging law (green since the C-era stager tests): no entity
                    // initialization, no event dispatch, and no command evaluated in a LIVE
                    // ENTITY's object scope before activation. Trusted AI-policy initialization
                    // runs in per-owner scopes and is explicitly lawful — a blanket This!=null
                    // check would flag it.
                    bool thisIsLiveEntity = engine.Ctx.This is DigglesTclEntity thisEntity &&
                        engine.Ctx.Entities.ContainsKey(thisEntity.Handle);
                    // `obj_init {... }` inside a def_class body is the REGISTRATION command for
                    // the per-instance init script (this=null during the declaration boot; 789
                    // such registrations across the shipped classes). Only an obj_init evaluated
                    // in a live entity's scope is instance initialization — the forbidden kind.
                    bool isObjectTcl = thisIsLiveEntity ||
                        engine.Ctx.CurrentEvent != null;
                    if (candidateIsInactive && isObjectTcl)
                    {
                        ObjectTclBeforeActivation++;
                        if (_offenderSamples.Count < 10)
                        {
                            _offenderSamples.Add(
                                $"{command} (this={(engine.Ctx.This as DigglesTclEntity)?.Handle.ToString() ?? "null"}, " +
                                $"evt={(engine.Ctx.CurrentEvent != null ? "set" : "null")})");
                        }
                    }
                };
                return engine;
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }
    }
}
