#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveSparetimeDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasValidTypedSparetimeRegistrations()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var categoryCounts = new Dictionary<int, int>();
            var classCounts = new Dictionary<string, int>();
            int totalRegistrations = 0;
            int nonEmptySaveCount = 0;
            int danglingRegistrationCount = 0;
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveSparetimeDocument sparetime = document.Sparetime
                    ?? throw new AssertionException($"{path} has no typed sparetime registry.");
                OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(document);
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(plan);
                Assert.That(runtime.Sparetime.Registrations.Count, Is.EqualTo(sparetime.Records.Count));

                if (sparetime.Records.Count != 0) nonEmptySaveCount++;
                for (int index = 0; index < sparetime.Records.Count; index++)
                {
                    OriginalSaveSparetimeRecord expected = sparetime.Records[index];
                    OriginalSaveRuntimeSparetimeRegistration actual =
                        runtime.Sparetime.Registrations[index];
                    Assert.That(actual.Category, Is.EqualTo(expected.Category));
                    Assert.That(actual.ObjectId, Is.EqualTo(expected.ObjectId));
                    Increment(categoryCounts, expected.Category);
                    if (runtime.EntitiesById.TryGetValue(
                        expected.ObjectId,
                        out OriginalSaveRuntimeEntitySnapshot? obj))
                    {
                        Increment(classCounts, obj.ClassName);
                    }
                    else
                    {
                        danglingRegistrationCount++;
                        Increment(classCounts, "<dangling>");
                    }
                    totalRegistrations++;
                }
            }

            TestContext.WriteLine(
                $"files={paths.Length}, nonEmpty={nonEmptySaveCount}, " +
                $"registrations={totalRegistrations}, dangling={danglingRegistrationCount}");
            foreach (KeyValuePair<int, int> pair in categoryCounts)
                TestContext.WriteLine($"category {pair.Key}: {pair.Value}");
            foreach (KeyValuePair<string, int> pair in classCounts)
                TestContext.WriteLine($"class {pair.Key}: {pair.Value}");
        }

        private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }
    }
}
