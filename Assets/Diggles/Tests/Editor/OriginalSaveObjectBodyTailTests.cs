#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveObjectBodyTailTests
    {
        // Total object bodies across the native save corpus.
        private const int ExpectedCorpusBodyCount = 146018;

        [Test]
        [Explicit("Pins exact counts of a developer-local save corpus")]
        public void EveryBodyParsesToExactSize()
        {
            string[] paths = FindCorpusPaths();
            int bodyCount = 0;
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveObjectCollection objects = codec.Read(path).Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    Assert.That(
                        body.TailParseEndOffset,
                        Is.EqualTo(body.Offset + body.Size),
                        $"{path}, object {body.ObjectId}");
                    bodyCount++;
                }
            }

            Assert.That(bodyCount, Is.EqualTo(ExpectedCorpusBodyCount));
            TestContext.WriteLine($"files={paths.Length}, bodies={bodyCount}");
        }

        [Test]
        [Explicit("Pins exact counts of a developer-local save corpus")]
        public void PickTargetModeDomainIsZeroOneTwo()
        {
            string[] paths = FindCorpusPaths();
            int bodyCount = 0;
            var histogram = new Dictionary<int, int>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveObjectCollection objects = codec.Read(path).Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    Assert.That(
                        body.PickTargetMode is 0 or 1 or 2,
                        Is.True,
                        $"{path}, object {body.ObjectId}, pick mode {body.PickTargetMode}");
                    Increment(histogram, body.PickTargetMode);
                    bodyCount++;
                }
            }

            Assert.That(bodyCount, Is.EqualTo(ExpectedCorpusBodyCount));
            TestContext.WriteLine(
                $"files={paths.Length}, bodies={bodyCount}, pick-modes={FormatHistogram(histogram)}");
        }

        [Test]
        [Explicit("Pins exact counts of a developer-local save corpus")]
        public void WalkResultDomainMatchesCorpus()
        {
            string[] paths = FindCorpusPaths();
            int bodyCount = 0;
            var histogram = new Dictionary<int, int>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveObjectCollection objects = codec.Read(path).Objects ??
                    throw new AssertionException($"{path} has no typed object collection.");
                for (int index = 0; index < objects.Count; index++)
                {
                    OriginalSaveObjectDocument body = objects[index];
                    int walkResult = body.CommonState.RouteState;
                    Assert.That(
                        walkResult is 1 or 2 or 3 or 4 or 5,
                        Is.True,
                        $"{path}, object {body.ObjectId}, walk result {walkResult}");
                    Increment(histogram, walkResult);
                    bodyCount++;
                }
            }

            Assert.That(bodyCount, Is.EqualTo(ExpectedCorpusBodyCount));
            TestContext.WriteLine(
                $"files={paths.Length}, bodies={bodyCount}, walk-results={FormatHistogram(histogram)}");
        }

        [Test]
        public void RepresentativeGnomeCoreScalarsReachRuntimeSnapshot()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_tutorial.sav");
            OriginalSaveObjectCollection objects = document.Objects ??
                throw new AssertionException("The tutorial save has no typed object collection.");
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            OriginalSaveObjectDocument gnome = objects.FindById(51) ??
                throw new AssertionException("Tutorial gnome 51 was not found.");
            OriginalSaveRuntimeEntitySnapshot staged = snapshot.EntitiesById[51];

            Assert.That(staged.WalkResult, Is.EqualTo(gnome.CommonState.RouteState));
            Assert.That(staged.WeaponClassId, Is.EqualTo(gnome.CommonState.State220[2]));
            Assert.That(staged.ShieldClassId, Is.EqualTo(gnome.CommonState.State220[3]));
            Assert.That(staged.PickTargetMode, Is.EqualTo(gnome.PickTargetMode));
        }

        private static string[] FindCorpusPaths()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            // The port's own quicksaves land in this shared directory and are out of scope for
            // the native-corpus assertions.
            paths = System.Array.FindAll(paths, p => !OriginalDataTestFixture.IsPortWrittenSaveName(Path.GetFileName(p)));
            Assume.That(paths.Length, Is.GreaterThanOrEqualTo(98));
            return paths;
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private static void Increment(IDictionary<int, int> histogram, int value)
        {
            histogram.TryGetValue(value, out int count);
            histogram[value] = count + 1;
        }

        private static string FormatHistogram(IReadOnlyDictionary<int, int> histogram) =>
            string.Join(",", histogram.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}:{pair.Value}"));
    }
}
