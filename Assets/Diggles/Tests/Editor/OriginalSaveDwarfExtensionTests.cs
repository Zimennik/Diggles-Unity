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

    // Type-1/11 family, talkissue, and planner sub-state restore. Tests 1-3 read the corpus
    // through the parser + the immutable runtime snapshot; test 4 exercises the full session
    // stager (partner pseudo-vars, AssignedWorkplaceId, idle-pool membership) on the tutorial
    // save.
    public sealed class OriginalSaveDwarfExtensionTests
    {
        [Test]
        [Explicit("Pins exact counts of a developer-local save corpus")]
        public void FamilyStatesRestore()
        {
            string[] paths = FindCorpusPaths();
            var histogram = new Dictionary<(int Partner, int Mother, int Father), int>();
            int familyBlocks = 0;
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveObjectCollection objects = codec.Read(path).Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    OriginalSaveObjectFamilyExtensionDocument? family = body.FamilyExtension;
                    if (family == null) continue;

                    // Only types 1 (gnome) and 11 (baby) carry the family block.
                    Assert.That(body.ObjectType is 1 or 11, Is.True,
                        $"{path}, object {body.ObjectId} carries a family block for type {body.ObjectType}");
                    var triple = (
                        family.PartnerLink.State,
                        family.MotherLink.State,
                        family.FatherLink.State);
                    histogram.TryGetValue(triple, out int count);
                    histogram[triple] = count + 1;
                    familyBlocks++;
                }
            }

            // B2 §2 state-triple counts (partner, mother, father). These four are asserted exactly;
            // other triples are legal and left uncounted here. Dangling refs (a live record whose
            // ref word is -1, or an undeclared id) are tolerated implicitly: the parse above never
            // threw, and the state histogram is independent of ref resolution.
            AssertTriple(histogram, (1, 1, 1), 1030);
            AssertTriple(histogram, (1, 0, 0), 704);
            AssertTriple(histogram, (0, 1, 1), 289);
            AssertTriple(histogram, (1, 2, 2), 249);

            TestContext.WriteLine(
                $"files={paths.Length}, family blocks={familyBlocks}, triples={histogram.Count}");
        }

        [Test]
        public void TalkIssueSumIsPersistedNotDerived()
        {
            string[] paths = FindCorpusPaths();
            int nonDerivableGnomes = 0;
            bool asserted = false;
            var stager = new OriginalSaveRuntimeStager();
            var planner = new OriginalSaveLoadPlanner();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveObjectCollection objects = document.Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");

                OriginalSaveRuntimeSnapshot? snapshot = null;
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    OriginalSaveObjectTalkIssueExtensionDocument? talk = body.TalkIssueExtension;
                    if (talk == null || talk.Entries.Count == 0) continue;

                    double sumOfWeights = 0.0;
                    foreach (OriginalSaveObjectTalkIssueEntryDocument entry in talk.Entries)
                        sumOfWeights += entry.Weight.AsSingle;
                    float storedSum = talk.TalkDesireSum.AsSingle;

                    // A clearly non-derivable case: the running sum has drifted from Σweights by more
                    // than a rounding epsilon (native `reduce` subtracts without deleting, B2 §3).
                    if (Math.Abs(sumOfWeights - storedSum) <= 0.5) continue;
                    nonDerivableGnomes++;

                    snapshot ??= stager.Stage(planner.Prepare(document));
                    OriginalSaveRuntimeEntityTalkIssueSnapshot carried =
                        snapshot.EntitiesById[body.ObjectId].TalkIssues ??
                        throw new AssertionException(
                            $"{path}, gnome {body.ObjectId} lost its talkissue snapshot.");

                    // The restored sum is the stored native +0x18 verbatim, NOT Σweights.
                    Assert.That(carried.SumBits, Is.EqualTo(talk.TalkDesireSum.Bits),
                        $"{path}, gnome {body.ObjectId}");
                    Assert.That(carried.SumBits,
                        Is.Not.EqualTo(BitConverter.SingleToInt32Bits((float)sumOfWeights)),
                        $"{path}, gnome {body.ObjectId}");
                    if (!asserted)
                    {
                        asserted = true;
                        TestContext.WriteLine(
                            $"non-derivable example: {Path.GetFileName(path)} gnome " +
                            $"{body.ObjectId} stored={storedSum} sum-of-weights={sumOfWeights}");
                    }
                }
            }

            Assert.That(nonDerivableGnomes, Is.GreaterThan(0),
                "no corpus gnome had a talkissue running sum differing from the sum of its weights");
            TestContext.WriteLine($"non-derivable gnomes={nonDerivableGnomes}");
        }

        [Test]
        public void PlannerWorkplaceIdsTolerateDangling()
        {
            string[] paths = FindCorpusPaths();
            int danglingCurrent = 0;
            int danglingAssigned = 0;
            int plannerBlocks = 0;
            var stager = new OriginalSaveRuntimeStager();
            var loadPlanner = new OriginalSaveLoadPlanner();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveObjectCollection objects = document.Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                var declaredIds = new HashSet<int>();
                for (int index = 0; index < objects.Count; index++)
                    declaredIds.Add(objects[index].ObjectId);

                // Staging must not throw on a dangling planner id — the port never resolves it.
                OriginalSaveRuntimeSnapshot snapshot = stager.Stage(loadPlanner.Prepare(document));
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    OriginalSaveObjectPlannerExtensionDocument? planner = body.PlannerExtension;
                    if (planner == null) continue;
                    plannerBlocks++;

                    OriginalSaveRuntimeEntityPlannerSnapshot carried =
                        snapshot.EntitiesById[body.ObjectId].Planner ??
                        throw new AssertionException(
                            $"{path}, gnome {body.ObjectId} lost its planner snapshot.");
                    Assert.That(carried.CurrentWorkplaceObjectId,
                        Is.EqualTo(planner.CurrentWorkplaceObjectId), $"{path}, gnome {body.ObjectId}");
                    Assert.That(carried.AssignedWorkplaceObjectId,
                        Is.EqualTo(planner.AssignedWorkplaceObjectId), $"{path}, gnome {body.ObjectId}");

                    if (IsDangling(planner.CurrentWorkplaceObjectId, declaredIds)) danglingCurrent++;
                    if (IsDangling(planner.AssignedWorkplaceObjectId, declaredIds)) danglingAssigned++;
                }
            }

            // Corpus has 904 dangling +0x3C and 51 dangling +0x40 (B2 §4) — proving the tolerance
            // path is exercised, not merely reachable.
            Assert.That(plannerBlocks, Is.GreaterThan(0));
            Assert.That(danglingCurrent, Is.GreaterThan(0),
                "expected dangling planner current-workplace ids in the corpus");
            Assert.That(danglingAssigned, Is.GreaterThan(0),
                "expected dangling planner assigned-workplace ids in the corpus");
            TestContext.WriteLine(
                $"planner blocks={plannerBlocks}, dangling current={danglingCurrent}, " +
                $"dangling assigned={danglingAssigned}");
        }

        [Test]
        public void StagedGnomeExposesFamilyAndPlannerState()
        {
            string savePath = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(savePath), $"Original save fixture was not found at '{savePath}'.");

            OriginalSaveArchiveDocument document = new OriginalSaveArchiveCodec().Read(savePath);
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));

            var stableRoot = new GameObject("OriginalSaveDwarfExtensionTest");
            var initialRoot = new GameObject("WorldContent.Initial");
            initialRoot.transform.SetParent(stableRoot.transform, false);
            RuntimeGameSession? initial = null;
            RuntimeGameSession? candidate = null;
            try
            {
                var engineFactory = new DigglesEngineFactory();
                initial = RuntimeGameSession.CreateInitial(
                    engineFactory.Create(
                        OriginalDataTestFixture.DataRoot,
                        initialRoot.transform,
                        "BlockDB2.BDB"),
                    initialRoot);
                var host = new RuntimeWorldSessionHost(
                    engineFactory,
                    new ActiveGameSession(initial),
                    new GameMutationGate(),
                    new NoopPresentationBinder());
                candidate = host.CreateCandidate(OriginalDataTestFixture.DataRoot, "BlockDB2.BDB");

                candidate.Engine.LoadRuntimeDefinitions();
                candidate.Engine.Ctx.EntitySpawner = new RecordingSpawner();
                bool objectInitializationObserved = false;
                candidate.Engine.Interp.CommandTrace = (command, _, __) =>
                {
                    if (command == "obj_init") objectInitializationObserved = true;
                };

                var actionFactory = new OriginalSaveActionFactory(new RuntimeSaveableActionFactory());
                var sessionStager = new OriginalSaveRuntimeSessionStager(
                    new OriginalSaveRuntimeLightingInitializer(),
                    new OriginalSaveActionCandidateStager(actionFactory),
                    new OriginalSaveActionInstaller());

                OriginalSaveRuntimeSessionState staged = sessionStager.StageCandidate(
                    candidate, snapshot, "BlockDB2.BDB");

                DigglesTclContext context = candidate.Engine.Ctx;
                Diggles.Runtime.Sim.ProdDispatcher dispatcher = context.ProdDispatcher ??
                    throw new AssertionException("The candidate has no work dispatcher.");

                int familyGnomes = 0;
                int plannerGnomes = 0;
                int pooledGnomes = 0;
                foreach (OriginalSaveRuntimeEntitySnapshot saved in snapshot.Entities)
                {
                    DigglesTclEntity entity = staged.Entities[saved.ObjectId];

                    if (saved.Family is OriginalSaveRuntimeEntityFamilySnapshot family)
                    {
                        familyGnomes++;
                        Assert.That(entity.VarsScope.Vars.TryGetValue("_partner_preg", out string preg),
                            Is.True, $"gnome {saved.ObjectId} exposes no _partner_preg");
                        Assert.That(preg, Is.EqualTo(family.PregnancyFlag ? "1" : "0"),
                            $"gnome {saved.ObjectId} pregnancy flag");
                        AssertPartnerVar(entity, "partner", family.Partner);
                        AssertPartnerVar(entity, "mother", family.Mother);
                        AssertPartnerVar(entity, "father", family.Father);
                    }

                    if (saved.TalkIssues is OriginalSaveRuntimeEntityTalkIssueSnapshot talk)
                    {
                        Assert.That(BitConverter.SingleToInt32Bits((float)entity.TalkIssuesSum),
                            Is.EqualTo(talk.SumBits), $"gnome {saved.ObjectId} talkissue sum");
                    }

                    if (saved.Planner is OriginalSaveRuntimeEntityPlannerSnapshot planner)
                    {
                        plannerGnomes++;
                        Assert.That(entity.AssignedWorkplaceId,
                            Is.EqualTo(planner.AssignedWorkplaceObjectId),
                            $"gnome {saved.ObjectId} assigned workplace");
                        bool pooled = dispatcher.IsPooled(entity.Handle, entity.Owner);
                        Assert.That(pooled, Is.EqualTo(planner.IdleAnnounceFlag == 1),
                            $"gnome {saved.ObjectId} idle-pool membership");
                        if (pooled) pooledGnomes++;
                    }
                }

                Assert.That(objectInitializationObserved, Is.False,
                    "entity reconstruction must not evaluate obj_init Tcl");
                Assert.That(plannerGnomes, Is.GreaterThan(0),
                    "the tutorial save exposed no planner gnomes to verify");
                Assert.That(familyGnomes, Is.GreaterThan(0),
                    "the tutorial save exposed no family gnomes to verify");
                TestContext.WriteLine(
                    $"family gnomes={familyGnomes}, planner gnomes={plannerGnomes}, " +
                    $"idle-pooled={pooledGnomes}");
            }
            finally
            {
                candidate?.Dispose();
                initial?.Dispose();
                if (stableRoot != null) UnityEngine.Object.DestroyImmediate(stableRoot);
            }
        }

        private static void AssertPartnerVar(
            DigglesTclEntity entity,
            string role,
            OriginalSaveRuntimeEntityFamilyLink link)
        {
            bool present = entity.VarsScope.Vars.TryGetValue("_partner_" + role, out string value);
            if (link.State == 1)
            {
                // A linked record materializes the resolved-entity handle var.
                Assert.That(present, Is.True, $"gnome exposes no _partner_{role} for a live link");
                Assert.That(value, Is.EqualTo(
                    link.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    $"_partner_{role} handle");
            }
            else
            {
                // Never-set (0) / deceased (2) links leave no var (staging runs no other Tcl).
                Assert.That(present, Is.False,
                    $"_partner_{role} must be absent for a {(link.State == 2 ? "deceased" : "never-set")} link");
            }
        }

        private static bool IsDangling(int workplaceId, HashSet<int> declaredIds) =>
            workplaceId >= 0 && !declaredIds.Contains(workplaceId);

        private static void AssertTriple(
            IReadOnlyDictionary<(int, int, int), int> histogram,
            (int, int, int) triple,
            int expected)
        {
            histogram.TryGetValue(triple, out int actual);
            Assert.That(actual, Is.EqualTo(expected),
                $"family state triple {triple} count");
        }

        private static string[] FindCorpusPaths()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            // The port's own quicksaves land in this shared directory and are out of scope for
            // the native-corpus assertions.
            paths = Array.FindAll(paths, p => !OriginalDataTestFixture.IsPortWrittenSaveName(Path.GetFileName(p)));
            Assume.That(paths.Length, Is.GreaterThanOrEqualTo(98));
            return paths;
        }

        private sealed class NoopPresentationBinder : IRuntimePresentationBinder
        {
            public void BindSession(RuntimeGameSession session) { }
        }

        private sealed class RecordingSpawner : IDigglesEntitySpawner
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
