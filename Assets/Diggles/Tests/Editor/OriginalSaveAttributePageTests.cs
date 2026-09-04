#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveAttributePageTests
    {
        private const int AttributeSlotCount = 512;
        // Floor, not a pin: the shared Gamesave directory legitimately grows (quicksaves land
        // there); new valid saves only add pages, so the count can only increase.
        private const int MinimumCorpusAttributePageCount = 30000;
        // Native page slot (sentinel probe + decompile FUN_00558770/genattribs order): the
        // original's atr_Hitpoints is slot 26, atr_Mood 29. The port's own registry index (25)
        // is a different permutation; reading pages by it scrambled every stat.
        private const int HitpointsSlot = 26;
        private const string HitpointsName = "atr_Hitpoints";

        [Test]
        public void EveryObjectPageRoundTripsRawBits()
        {
            string[] paths = FindCorpusPaths();
            int pageCount = 0;
            var codec = new OriginalSaveArchiveCodec();
            var planner = new OriginalSaveLoadPlanner();
            var stager = new OriginalSaveRuntimeStager();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveObjectCollection objects = document.Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                OriginalSaveRuntimeSnapshot snapshot = stager.Stage(planner.Prepare(document));
                foreach (OriginalSaveObjectDocument source in objects)
                {
                    IReadOnlyList<int> parsed = source.CommonState.GridState;
                    IReadOnlyList<int> staged = snapshot.EntitiesById[source.ObjectId].AttributePageBits;
                    if (parsed.Count == 0)
                    {
                        Assert.That(staged, Is.Empty, $"{path}, object {source.ObjectId}");
                        continue;
                    }

                    Assert.That(parsed, Has.Count.EqualTo(AttributeSlotCount));
                    Assert.That(staged, Has.Count.EqualTo(AttributeSlotCount));
                    Assert.That(staged, Is.Not.SameAs(parsed));
                    CollectionAssert.AreEqual(parsed, staged, $"{path}, object {source.ObjectId}");
                    pageCount++;
                }
            }

            Assert.That(pageCount, Is.GreaterThanOrEqualTo(MinimumCorpusAttributePageCount));
            TestContext.WriteLine($"files={paths.Length}, attribute-pages={pageCount}");
        }

        [Test]
        public void RegisteredHitpointsRestoreOntoEntity()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_test.sav");
            OriginalSaveObjectCollection objects = document.Objects ??
                throw new AssertionException("save_test.sav has no typed object collection.");
            OriginalSaveObjectDocument damagedGnome = objects.FirstOrDefault(IsDamagedGnome) ??
                throw new AssertionException("save_test.sav contains no damaged gnome attribute page.");
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));

            using var session = new SessionHarness();
            OriginalSaveRuntimeSessionState staged = session.Stage(snapshot);
            DigglesTclContext context = session.Candidate.Engine.Ctx;
            double restored = context.Stats.Get(
                staged.Entities[damagedGnome.ObjectId].Stats,
                HitpointsName);

            AssertFloatToDoubleExact(
                damagedGnome.CommonState.GridState[HitpointsSlot],
                restored,
                $"object {damagedGnome.ObjectId}");
        }

        [Test]
        public void OwnerAttribTablesRestore()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_test.sav");
            OriginalSaveObjectManifest manifest = document.Metadata.Objects ??
                throw new AssertionException("save_test.sav has no object manifest.");
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));

            Assert.That(manifest.OwnerAttributeTables.Blocks, Has.Count.EqualTo(9));
            Assert.That(snapshot.OwnerAttributes.Blocks, Has.Count.EqualTo(9));
            bool independentSecondaryState = false;
            for (int block = 0; block < snapshot.OwnerAttributes.Blocks.Count; block++)
            {
                OriginalSaveOwnerAttributeBlockDocument parsed =
                    manifest.OwnerAttributeTables.Blocks[block];
                OriginalSaveRuntimeOwnerAttributeBlockSnapshot staged =
                    snapshot.OwnerAttributes.Blocks[block];
                Assert.That(staged.Owner, Is.EqualTo(block - 1));
                Assert.That(staged.PrimaryValueBits, Has.Count.EqualTo(AttributeSlotCount));
                Assert.That(staged.SecondaryValueBits, Has.Count.EqualTo(AttributeSlotCount));
                Assert.That(staged.PrimaryValueBits, Is.Not.SameAs(parsed.PrimaryValueBits));
                Assert.That(staged.SecondaryValueBits, Is.Not.SameAs(parsed.SecondaryValueBits));
                CollectionAssert.AreEqual(parsed.PrimaryValueBits, staged.PrimaryValueBits);
                CollectionAssert.AreEqual(parsed.SecondaryValueBits, staged.SecondaryValueBits);
                independentSecondaryState |= staged.PrimaryValueBits.Where(
                    (bits, slot) => bits != staged.SecondaryValueBits[slot]).Any();
            }
            Assert.That(independentSecondaryState, Is.True,
                "save_test.sav should carry distinct primary and secondary owner-attribute state");

            using var session = new SessionHarness();
            session.Stage(snapshot);
            DigglesTclContext context = session.Candidate.Engine.Ctx;
            int owner = context.LocalPlayer;
            OriginalSaveRuntimeOwnerAttributeBlockSnapshot ownerState =
                snapshot.OwnerAttributes.Blocks[owner + 1];
            Dictionary<string, double> restored = context.OwnerAttribs[owner];
            IReadOnlyDictionary<string, int> nativeSlots =
                OriginalSaveNativeAttributeSlots.SlotByName;
            // Restore follows the archive's own CProd Bp<Class> anchors, which lawfully exceed
            // the sentinel table on mod-era saves (save_test carries mod-class Bp names). Every
            // native-table name must restore; extras must all be Bp anchors.
            Assert.That(restored.Keys, Is.SupersetOf(nativeSlots.Keys));
            foreach (string extra in restored.Keys.Except(nativeSlots.Keys))
                Assert.That(extra, Does.StartWith("Bp"),
                    $"non-anchor extra owner attribute restored: {extra}");

            int valuesDifferentFromDefinitionDefaults = 0;
            foreach (KeyValuePair<string, int> entry in nativeSlots)
            {
                double expected = BitConverter.Int32BitsToSingle(
                    ownerState.PrimaryValueBits[entry.Value]);
                Assert.That(restored.TryGetValue(entry.Key, out double actual), Is.True, entry.Key);
                // Bp<Class> production-knowledge words follow the archive's own CProd anchors,
                // which lawfully drift from the sentinel table (BpKleiner_Heiltrank is 79 here
                // vs 77 in the sentinel). Exact-bits identity is asserted only for the stable
                // non-anchor names.
                if (!entry.Key.StartsWith("Bp", StringComparison.Ordinal))
                    AssertFloatToDoubleExact(ownerState.PrimaryValueBits[entry.Value], actual, entry.Key);
                if (expected != context.Stats.Registry.DefaultOf(entry.Key))
                    valuesDifferentFromDefinitionDefaults++;
            }
            Assert.That(valuesDifferentFromDefinitionDefaults, Is.GreaterThan(0),
                "save_test.sav should carry mutated named owner attributes");
        }

        // Ground-truth anchors for the sentinel-probed native slot table: the four vitals per
        // the decompile derivation (genattribs order after the 11 FUN_00558770 built-ins) and
        // two built-ins the port also names. If the table regenerates differently, the sentinel
        // probe or the game data changed.
        [Test]
        public void NativeSlotTableCarriesTheProvenAnchors()
        {
            var slots = OriginalSaveNativeAttributeSlots.SlotByName;
            Assert.That(slots["atr_Hitpoints"], Is.EqualTo(26));
            Assert.That(slots["atr_Nutrition"], Is.EqualTo(27));
            Assert.That(slots["atr_Alertness"], Is.EqualTo(28));
            Assert.That(slots["atr_Mood"], Is.EqualTo(29));
            Assert.That(slots["hitpoints"], Is.EqualTo(2), "FUN_00558770 built-in");
            Assert.That(slots["weight"], Is.EqualTo(5), "FUN_00558770 built-in");
        }

        // Any gnome with a real page works — the test pins the slot mapping (native slot 26 ->
        // atr_Hitpoints), not combat damage. The old predicate searched the port's registry
        // index 25 (natively exp_F_Kungfu) and was self-fulfilling.
        private static bool IsDamagedGnome(OriginalSaveObjectDocument candidate)
        {
            bool isGnome = candidate.ClassName is "Zwerg" or "Frau" or "PseudoZwerg";
            if (candidate.CommonState.GridState.Count != AttributeSlotCount ||
                !isGnome)
            {
                return false;
            }

            float hitpoints = BitConverter.Int32BitsToSingle(
                candidate.CommonState.GridState[HitpointsSlot]);
            return hitpoints > 0.0f && hitpoints <= 1.0f;
        }

        private static void AssertFloatToDoubleExact(int bits, double actual, string message)
        {
            double expected = BitConverter.Int32BitsToSingle(bits);
            Assert.That(
                BitConverter.DoubleToInt64Bits(actual),
                Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)),
                message);
        }

        private static string[] FindCorpusPaths()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths.Length, Is.GreaterThanOrEqualTo(98));
            return paths;
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private sealed class SessionHarness : IDisposable
        {
            private readonly GameObject _stableRoot;
            private readonly RuntimeGameSession _initial;

            internal SessionHarness()
            {
                _stableRoot = new GameObject("OriginalSaveAttributePageTests");
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
