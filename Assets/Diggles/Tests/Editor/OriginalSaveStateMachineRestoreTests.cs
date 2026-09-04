#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // The native Tcl-state-machine words obj+0x200/+0x204/+0x208 (CObj::HandleStates 005cac90 —
    // live/target per-class state-table indices + the think-enable count) must restore onto
    // the live entity and round-trip through capture. Regression: left unrestored, every
    // object of a loaded world goes think-dead, and set_objname/set_objinfo silently no-op,
    // leaving the name (+0xD8) and info string (+0x360) empty.
    public sealed class OriginalSaveStateMachineRestoreTests
    {
        private const int TutorialGnomeId = 51;

        // save_tutorial.sav gnome #51 ground truth (verified against the raw archive with
        // Tools/SaveFormatResearch/dump_a5_details.py): live 14, target 14, enabled 0,
        // name "Charly", info string "EinZwerg".
        private const int TutorialGnomeStateIndex = 14;

        [Test]
        public void TutorialSnapshotCarriesTheStateWords()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial.sav");
            OriginalSaveObjectCollection objects = document.Objects ??
                throw new AssertionException("save_tutorial.sav has no typed object collection.");
            OriginalSaveObjectDocument gnome = objects.FindById(TutorialGnomeId) ??
                throw new AssertionException($"tutorial object {TutorialGnomeId} was not found.");
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            OriginalSaveRuntimeEntitySnapshot staged = snapshot.EntitiesById[TutorialGnomeId];

            Assert.That(staged.StateLiveWord, Is.EqualTo(gnome.CommonState.State200[0]));
            Assert.That(staged.StateTargetWord, Is.EqualTo(gnome.CommonState.State200[1]));
            Assert.That(staged.StateEnabledWord, Is.EqualTo(gnome.CommonState.State200[2]));
            Assert.That(staged.StateLiveWord, Is.EqualTo(TutorialGnomeStateIndex),
                "fixture ground truth: the tutorial gnome saved in live state 14");
            Assert.That(staged.StateTargetWord, Is.EqualTo(TutorialGnomeStateIndex));
            Assert.That(staged.StateEnabledWord, Is.EqualTo(0),
                "the tutorial gnome saved state-disabled (mid idle-wait discipline)");
        }

        [Test]
        public void TutorialGnomeStateMachineRestoresOntoTheEntity()
        {
            OriginalSaveRuntimeSnapshot snapshot = PrepareSnapshot("save_tutorial.sav");
            using var session = new SessionHarness();
            OriginalSaveRuntimeSessionState staged = session.Stage(snapshot);
            DigglesTclEntity gnome = staged.Entities[TutorialGnomeId];

            Assert.That(gnome.Class.StateOrder.Count,
                Is.GreaterThan(TutorialGnomeStateIndex),
                "the Zwerg class must register its native state table in script order");
            Assert.That(gnome.CurrentState, Is.Not.Null,
                "the archived target state index must resolve through the class state table");
            TestContext.WriteLine(
                $"tutorial gnome restored state = '{gnome.CurrentState}' " +
                $"(index {TutorialGnomeStateIndex} of {gnome.Class.StateOrder.Count})");
            Assert.That(gnome.StateWentLive, Is.True,
                "live == target in the archive: the enter body must NOT re-run");
            Assert.That(gnome.LiveStateIndex, Is.EqualTo(TutorialGnomeStateIndex));
            Assert.That(gnome.StateEnabledCount, Is.EqualTo(-1),
                "native enable word 0 = disabled = port count -1 (state_enable revives to 0)");
            Assert.That(gnome.StateWordsDirty, Is.False,
                "staging must not count as a state mutation");

            Assert.That(gnome.InstanceName, Is.EqualTo("Charly"));
            Assert.That(gnome.InfoString, Is.EqualTo("EinZwerg"),
                "obj+0x360 (set_objinfo) must restore onto the entity");
        }

        [Test]
        public void CleanStagedCaptureRoundTripsTheWordsVerbatim()
        {
            OriginalSaveRuntimeSnapshot snapshot = PrepareSnapshot("save_tutorial.sav");
            using var session = new SessionHarness();
            session.Stage(snapshot);
            DigglesTclContext context = session.Candidate.Engine.Ctx;

            OriginalSaveCanonicalCapture capture =
                new OriginalSaveRuntimePatchMapper().CaptureCanonical(snapshot, context);
            OriginalSaveCanonicalEntityState gnome = FindEntity(capture, TutorialGnomeId);

            Assert.That(gnome.StateLiveIndex, Is.EqualTo(TutorialGnomeStateIndex));
            Assert.That(gnome.StateTargetIndex, Is.EqualTo(TutorialGnomeStateIndex));
            Assert.That(gnome.StateEnabledWord, Is.EqualTo(0));
            Assert.That(gnome.InfoString, Is.EqualTo("EinZwerg"));
        }

        [Test]
        public void MutatedStateMachineCapturesTheLiveDerivation()
        {
            OriginalSaveRuntimeSnapshot snapshot = PrepareSnapshot("save_tutorial.sav");
            using var session = new SessionHarness();
            OriginalSaveRuntimeSessionState staged = session.Stage(snapshot);
            DigglesTclContext context = session.Candidate.Engine.Ctx;
            DigglesTclEntity gnome = staged.Entities[TutorialGnomeId];

            // A played session: the state machine moved on after the restore.
            string? restoredState = gnome.CurrentState;
            Assert.That(restoredState, Is.Not.Null);
            gnome.StateEnabledCount = 0;             // state_enable
            gnome.StateWordsDirty = true;
            OriginalSaveCanonicalCapture capture =
                new OriginalSaveRuntimePatchMapper().CaptureCanonical(snapshot, context);
            OriginalSaveCanonicalEntityState captured = FindEntity(capture, TutorialGnomeId);

            Assert.That(captured.StateTargetIndex,
                Is.EqualTo(gnome.Class.StateIndexOf(restoredState)),
                "a dirty machine derives the target word from CurrentState");
            Assert.That(captured.StateLiveIndex, Is.EqualTo(captured.StateTargetIndex),
                "the state had gone live, so live == target");
            Assert.That(captured.StateEnabledWord, Is.EqualTo(1),
                "port count 0 (enabled) = native enable word 1");
        }

        [Test]
        public void SetObjnameAndSetObjinfoAreRealCommands()
        {
            using var session = new SessionHarness();
            DigglesTclContext context = session.Candidate.Engine.Ctx;
            context.Interp.Eval("new Zwerg wave3_name_probe");
            string handle = context.Interp.Eval("obj_query -class Zwerg -name wave3_name_probe");
            if (string.IsNullOrEmpty(handle) || handle == "0")
            {
                // Resolve through the freshest entity instead when the query form differs.
                DigglesTclEntity? probe = null;
                foreach (var entry in context.Entities)
                    if (entry.Value.InstanceName == "wave3_name_probe") probe = entry.Value;
                Assert.That(probe, Is.Not.Null, "the probe gnome must exist");
                handle = probe!.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            context.Interp.Eval($"set_objname {handle} Bofur");
            Assert.That(context.Interp.Eval($"get_objname {handle}"), Is.EqualTo("Bofur"));

            context.Interp.Eval($"set_objinfo {handle} EinZwerg");
            Assert.That(context.Interp.Eval($"get_objinfo {handle}"), Is.EqualTo("EinZwerg"));

            context.Interp.Eval($"set_objname {handle} auto male");
            string autoName = context.Interp.Eval($"get_objname {handle}");
            Assert.That(autoName, Is.Not.Empty,
                "set_objname auto must draw a name from Scripts/classes/zwerg/names.txt");
        }

        // Dumps the port's def_attrib registration order, one name per line, for comparison
        // against the original's slot order (established empirically by setting sentinel
        // values and reading the saved page back) to build the exact slot correction table.
        [Test]
        [Explicit("Dumps the attribute registry order to the temp folder")]
        public void DumpAttributeRegistryOrder()
        {
            using var session = new SessionHarness();
            // def_attrib registration happens with the runtime definitions (class scripts), the
            // same call StageCandidate makes — a bare engine boot has an empty registry.
            session.Candidate.Engine.LoadRuntimeDefinitions();
            var names = session.Candidate.Engine.Ctx.Stats.Registry.DefinedInOrder;
            string path = Path.Combine(
                System.IO.Path.GetTempPath(), "port-attrib-order.txt");
            File.WriteAllLines(path, names);
            TestContext.WriteLine($"port def_attrib registry: {names.Count} names → {path}");
            Assert.That(names.Count, Is.GreaterThan(0));
        }

        private static OriginalSaveRuntimeSnapshot PrepareSnapshot(string fileName)
        {
            OriginalSaveArchiveDocument document = ReadFixture(fileName);
            return new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
        }

        private static OriginalSaveCanonicalEntityState FindEntity(
            OriginalSaveCanonicalCapture capture,
            int objectId)
        {
            foreach (OriginalSaveCanonicalEntityState entity in capture.Entities)
                if (entity.ObjectId == objectId) return entity;
            throw new AssertionException($"captured entity {objectId} was not found.");
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                fileName);
            Assume.That(File.Exists(path),
                $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private sealed class SessionHarness : IDisposable
        {
            private readonly GameObject _stableRoot;
            private readonly RuntimeGameSession _initial;

            internal SessionHarness()
            {
                _stableRoot = new GameObject(nameof(OriginalSaveStateMachineRestoreTests));
                var initialRoot = new GameObject("WorldContent.Initial");
                initialRoot.transform.SetParent(_stableRoot.transform, false);
                var engineFactory = new DigglesEngineFactory();
                _initial = RuntimeGameSession.CreateInitial(
                    engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                var host = new RuntimeWorldSessionHost(
                    engineFactory,
                    new ActiveGameSession(_initial),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                Candidate = host.CreateCandidate(
                    OriginalDataTestFixture.DataRoot,
                    "BlockDB2.BDB");
                Candidate.Engine.Ctx.EntitySpawner = new NullSpawner();
            }

            internal RuntimeGameSession Candidate { get; }

            internal OriginalSaveRuntimeSessionState Stage(OriginalSaveRuntimeSnapshot snapshot)
            {
                var saveableActionFactory = new RuntimeSaveableActionFactory();
                var actionFactory = new OriginalSaveActionFactory(saveableActionFactory);
                var actionStager = new OriginalSaveActionCandidateStager(actionFactory);
                var sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    actionStager,
                    new OriginalSaveActionInstaller());
                return sessionStager.StageCandidate(Candidate, snapshot, "BlockDB2.BDB");
            }

            public void Dispose()
            {
                Candidate.Dispose();
                _initial.Dispose();
                UnityEngine.Object.DestroyImmediate(_stableRoot);
            }
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }

        private sealed class NullSpawner : IDigglesEntitySpawner
        {
            public GameObject? Spawn(DigglesTclEntity ent, Transform parent) => null;
            public void Destroy(GameObject go) => UnityEngine.Object.DestroyImmediate(go);
            public void ApplyPosition(GameObject go, Vector3 nativePos) { }
            public void ApplyRotation(GameObject go, Vector3 nativeRotRadians) { }
            public void ApplyAnim(
                GameObject go,
                string animName,
                int startFrame,
                DigglesAnimator.AnimMode mode) { }
        }
    }
}
