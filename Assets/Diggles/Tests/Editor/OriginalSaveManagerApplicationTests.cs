#nullable enable

namespace Diggles.Tests
{
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveManagerApplicationTests
    {
        private GameObject? _stableRoot;
        private RuntimeGameSession? _initial;
        private RuntimeGameSession? _candidate;
        private OriginalSaveRuntimeSnapshot? _snapshot;
        private OriginalSaveRuntimeSessionState? _staged;

        private RuntimeGameSession Candidate => _candidate ??
            throw new AssertionException("The tutorial candidate was not staged.");

        private OriginalSaveRuntimeSnapshot Snapshot => _snapshot ??
            throw new AssertionException("The tutorial snapshot was not staged.");

        private OriginalSaveRuntimeSessionState Staged => _staged ??
            throw new AssertionException("The tutorial session state was not staged.");

        [OneTimeSetUp]
        public void StageTutorialFixture()
        {
            string savePath = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                "save_tutorial.sav");
            Assume.That(File.Exists(savePath),
                $"Original save fixture was not found at '{savePath}'.");

            try
            {
                OriginalSaveArchiveDocument document =
                    new OriginalSaveArchiveCodec().Read(savePath);
                OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                _snapshot = snapshot;

                _stableRoot = new GameObject("OriginalSaveManagerApplicationTest");
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                var engineFactory = new DigglesEngineFactory();
                RuntimeGameSession initial = RuntimeGameSession.CreateInitial(
                    engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                _initial = initial;
                var host = new RuntimeWorldSessionHost(
                    engineFactory,
                    new ActiveGameSession(initial),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                RuntimeGameSession candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                _candidate = candidate;

                var actionFactory = new OriginalSaveActionFactory(
                    new RuntimeSaveableActionFactory());
                var sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    new OriginalSaveActionInstaller());
                _staged = sessionStager.StageCandidate(
                    candidate,
                    snapshot,
                    "BlockDB2.BDB");
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
        public void LiveObjectResumesAtSavedFrame()
        {
            OriginalSaveRuntimeAnimationController saved =
                Snapshot.AnimationControllers.Records.Single(
                    record => record.ControllerIndex == 1786);
            DigglesTclEntity gnome = Staged.Entities[saved.ControllerIndex];
            DigglesAnimator animator = gnome.GameObject!
                .GetComponentInChildren<DigglesAnimator>(true)!;

            Assert.That(gnome.Class.Kind, Is.EqualTo("gnome"));
            Assert.That(saved.CurrentOrCompatibilityValue, Is.GreaterThan(0));
            Assert.That(saved.CurrentOrCompatibilityValue,
                Is.LessThan(saved.DeclaredValueCount - 1));
            Assert.That(animator, Is.Not.Null);
            Assert.That(animator.CurrentFrame,
                Is.EqualTo(saved.CurrentOrCompatibilityValue));
            Assert.That(animator.CurrentMode,
                Is.EqualTo((DigglesAnimator.AnimMode)saved.RawControlA));
            Assert.That(animator.CurrentFrameCount,
                Is.EqualTo(saved.DeclaredValueCount));
        }

        [Test]
        public void OrphanRecordsAreOmitted()
        {
            int orphanCount = 0;
            int nonRenderableCount = 0;
            int renderableCount = 0;
            int skippedOrAmbiguousCount = 0;
            int liveLogicOnlyCount = 0;
            var skippedClassNames = new System.Collections.Generic.List<string>();

            foreach (OriginalSaveRuntimeAnimationController record in
                     Snapshot.AnimationControllers.Records)
            {
                if (!Staged.Entities.TryGetValue(
                        record.ControllerIndex,
                        out DigglesTclEntity entity))
                {
                    orphanCount++;
                    continue;
                }

                DigglesAnimator? animator = entity.GameObject?
                    .GetComponentInChildren<DigglesAnimator>(true);
                if (animator == null)
                {
                    nonRenderableCount++;
                    if (record.ModelDatabaseSlot < 0) liveLogicOnlyCount++;
                    continue;
                }

                renderableCount++;
                if (!ContinuationMatches(record, animator))
                {
                    skippedOrAmbiguousCount++;
                    bool boxed = entity.VarsScope.Vars.TryGetValue("_boxed", out string bx) &&
                        (bx == "1" ||
                            string.Equals(bx, "true", System.StringComparison.OrdinalIgnoreCase));
                    skippedClassNames.Add(boxed ? "<boxed>" : entity.Class?.Name ?? "<null>");
                }
            }

            Assert.That(Snapshot.AnimationControllers.Records, Has.Count.EqualTo(1792));
            Assert.That(Staged.Entities, Has.Count.EqualTo(484));
            Assert.That(orphanCount, Is.EqualTo(1308));
            // The archived-latch spawn law: the 9 Tuer_kaserne and 6 Schalter_hebel controllers
            // find a real animator (their obj_init-computed models spawn from the archived N36
            // slot instead of a meshless placeholder), so their continuations apply instead of
            // retaining.
            Assert.That(nonRenderableCount, Is.EqualTo(119));
            Assert.That(renderableCount, Is.EqualTo(365));
            Assert.That(liveLogicOnlyCount, Is.EqualTo(4));
            // The clip-less cutscene stand-in (PseudoZwerg) has no resolvable model slot on its
            // live animator; those records are retained-not-applied with a logged warning, which
            // is the lawful outcome. Boxed entities also lawfully retain: the boxed presentation
            // law (kiste.standard) refuses a contradictory marker-9 continuation — the tutorial
            // carries one boxed prop. Every other renderable tutorial controller must resolve.
            Assert.That(skippedOrAmbiguousCount, Is.EqualTo(5),
                "only the four PseudoZwerg records and the boxed prop may be retained-not-applied");
            Assert.That(
                skippedClassNames.FindAll(name => name != "PseudoZwerg" && name != "<boxed>"),
                Is.Empty,
                "no class other than the cutscene stand-in or a boxed prop may fail clip resolution");
        }

        [Test]
        public void OnlyNonObjectBoundLightsRestored()
        {
            int expectedStaticCount = Snapshot.ClassLights.Records.Count(
                light => !light.IsObjectBound &&
                    (light.SourceToken == 0 || light.SourceToken == 1));
            int objectBoundCount = Snapshot.ClassLights.Records.Count(
                light => light.IsObjectBound);
            int staticPresentationCount = 0;
            for (int index = 0; index < Candidate.ContentRoot.transform.childCount; index++)
            {
                if (Candidate.ContentRoot.transform.GetChild(index).name == "StaticLight")
                    staticPresentationCount++;
            }

            Assert.That(expectedStaticCount, Is.EqualTo(52));
            Assert.That(objectBoundCount, Is.EqualTo(1));
            Assert.That(staticPresentationCount, Is.EqualTo(expectedStaticCount));
            // Object-bound lights are spawn-derived: the entity's class_light seeds them when the
            // Play spawner materializes the object. EditMode staging runs no spawner, so exactly
            // the static token-0/1 set may be buffered — the object-bound record must not be
            // statically restored (the non-duplication invariant this test guards).
            Assert.That(GetClassLightBufferCount(), Is.EqualTo(expectedStaticCount),
                "only non-object-bound lights may reach the static buffer during staging");
        }

        [Test]
        public void IconLoadDoesNotCoupleLifecycle()
        {
            Assert.That(Snapshot.ObjectIcons.Slots,
                Has.Count.EqualTo(OriginalSaveObjectIconDocument.SlotCount));
            Assert.That(Snapshot.ObjectIcons.Slots.Count(icon => icon.IsActive),
                Is.GreaterThan(0),
                "the fixture must contain active loaded slots for this boundary assertion");
            Assert.That(Staged.Source.ObjectIcons, Is.SameAs(Snapshot.ObjectIcons));

            SpriteRenderer[] iconPresentations =
                Candidate.ContentRoot.GetComponentsInChildren<SpriteRenderer>(true);
            Assert.That(iconPresentations.All(
                    renderer => renderer.gameObject.name == "BoxContentIcon"),
                Is.True,
                "loaded icon slots must not create presentation; only live boxed-state producers may do so");
        }

        private static bool ContinuationMatches(
            OriginalSaveRuntimeAnimationController saved,
            DigglesAnimator animator)
        {
            int savedFrame = saved.CurrentOrCompatibilityValue == -1
                ? 0
                : saved.CurrentOrCompatibilityValue;
            DigglesAnimator.AnimMode expectedMode =
                (DigglesAnimator.AnimMode)saved.RawControlA;
            if (expectedMode == DigglesAnimator.AnimMode.Once &&
                savedFrame == saved.DeclaredValueCount - 1)
            {
                expectedMode = DigglesAnimator.AnimMode.Still;
            }

            ThreeDbModel[] sourceModels = animator.Clips
                .Where(clip => clip.SourceModel != null)
                .Select(clip => clip.SourceModel!)
                .Distinct()
                .ToArray();
            if (sourceModels.Length != 1 ||
                (uint)saved.AnimationState >= (uint)sourceModels[0].Anims.Count)
            {
                return false;
            }

            return animator.CurrentAnimName == sourceModels[0].Anims[saved.AnimationState].Name1 &&
                animator.CurrentFrameCount == saved.DeclaredValueCount &&
                animator.CurrentFrame == savedFrame &&
                animator.CurrentMode == expectedMode;
        }

        private int GetClassLightBufferCount()
        {
            RuntimeWorldSpawner spawner = Candidate.Engine.Ctx.EntitySpawner as RuntimeWorldSpawner ??
                throw new AssertionException("The candidate does not use RuntimeWorldSpawner.");
            FieldInfo field = typeof(RuntimeWorldSpawner).GetField(
                "_classLights",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new AssertionException("RuntimeWorldSpawner has no class-light buffer.");
            object buffer = field.GetValue(spawner) ??
                throw new AssertionException("RuntimeWorldSpawner class-light buffer is null.");
            PropertyInfo count = buffer.GetType().GetProperty("Count") ??
                throw new AssertionException("RuntimeWorldSpawner class-light buffer has no Count.");
            return (int)(count.GetValue(buffer) ?? 0);
        }

        private void DisposeFixture()
        {
            _candidate?.Dispose();
            _candidate = null;
            _initial?.Dispose();
            _initial = null;
            if (_stableRoot != null) UnityEngine.Object.DestroyImmediate(_stableRoot);
            _stableRoot = null;
            _snapshot = null;
            _staged = null;
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }
    }
}
