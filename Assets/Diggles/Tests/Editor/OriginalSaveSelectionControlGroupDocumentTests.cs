#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveSelectionControlGroupDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasRepeatedStableSelectionControlGroups()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var groupMemberCounts = new Dictionary<string, int>();
            var memberClasses = new Dictionary<string, int>();
            int nonEmptySaveCount = 0;
            int totalMembers = 0;
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveSelectionControlGroupDocument groups =
                    document.SelectionControlGroups ?? throw new AssertionException(
                        $"{path} has no typed selection control groups.");
                OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(document);
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(plan);
                Assert.That(groups.Groups, Has.Count.EqualTo(
                    OriginalSaveSelectionControlGroupDocument.GroupCount));
                Assert.That(runtime.SelectionControlGroups.Groups, Has.Count.EqualTo(
                    OriginalSaveSelectionControlGroupDocument.GroupCount));

                bool nonEmpty = false;
                for (int group = 0; group < groups.Groups.Count; group++)
                {
                    IReadOnlyList<int> expected = groups.Groups[group];
                    Assert.That(runtime.SelectionControlGroups.Groups[group], Is.EqualTo(expected));
                    for (int copy = 1;
                        copy < OriginalSaveSelectionControlGroupDocument.SerializedCopyCount;
                        copy++)
                    {
                        Assert.That(groups.GetSerializedGroup(copy, group), Is.EqualTo(expected));
                    }

                    Increment(groupMemberCounts, $"{group + 1}:{expected.Count}");
                    if (expected.Count != 0) nonEmpty = true;
                    totalMembers += expected.Count;
                    foreach (int objectId in expected)
                    {
                        OriginalSaveObjectDocument member = plan.ObjectsById[objectId];
                        Increment(memberClasses, member.ClassName);
                    }
                    Assert.That(expected.Distinct().Count(), Is.EqualTo(expected.Count));
                }
                if (nonEmpty) nonEmptySaveCount++;
            }

            Print("selectionGroupMemberCount", groupMemberCounts);
            Print("selectionGroupMemberClass", memberClasses);
            TestContext.Out.WriteLine(
                $"selectionGroupNonEmptySaves={nonEmptySaveCount} totalMembers={totalMembers}");
            Assert.That(paths.Length, Is.GreaterThanOrEqualTo(98));
            Assert.That(nonEmptySaveCount, Is.GreaterThan(0));
            Assert.That(totalMembers, Is.GreaterThan(0));
        }

        private static void Increment<TKey>(Dictionary<TKey, int> values, TKey key)
            where TKey : notnull
        {
            values.TryGetValue(key, out int count);
            values[key] = count + 1;
        }

        private static void Print<TKey>(string label, Dictionary<TKey, int> values)
            where TKey : notnull
        {
            foreach (KeyValuePair<TKey, int> pair in
                values.OrderBy(pair => pair.Key.ToString(), System.StringComparer.Ordinal))
            {
                TestContext.Out.WriteLine($"{label}='{pair.Key}' count={pair.Value}");
            }
        }
    }
}
