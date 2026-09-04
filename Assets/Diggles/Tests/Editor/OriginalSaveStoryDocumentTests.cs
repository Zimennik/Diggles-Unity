#nullable enable

namespace Diggles.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveStoryDocumentTests
    {
        [Test]
        public void ReadsEveryAvailableCurrentSaveIntoTypedStoryState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var eventCounts = new Dictionary<int, int>();
            var completedEventCounts = new Dictionary<int, int>();
            var tempCounts = new Dictionary<int, int>();
            var groupCounts = new Dictionary<int, int>();
            var pairCounts = new Dictionary<int, int>();
            var mapDimensions = new Dictionary<string, int>();
            var mapValues = new Dictionary<int, int>();
            var zoneCounts = new Dictionary<int, int>();
            var callbackIds = new Dictionary<int, int>();
            var latchDirections = new Dictionary<int, int>();
            var digThresholds = new Dictionary<int, int>();
            var tempLayouts = new Dictionary<string, int>();
            var groupLayouts = new Dictionary<string, int>();
            var groupEntryLayouts = new Dictionary<string, int>();
            var selectedTemps = new Dictionary<string, int>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveStoryDocument story = document.Story ??
                    throw new AssertionException($"{path} has no typed story document.");
                _ = new OriginalSaveLoadPlanner().Prepare(document);
                Increment(eventCounts, story.Events.Count);
                Increment(completedEventCounts, story.Events.Count(value => value.Completed));
                Increment(tempCounts, story.Temps.Count);
                Increment(groupCounts, story.Groups.Count);
                Increment(pairCounts, story.PairRecords.Count);
                Increment(mapDimensions,
                    $"{story.MapWidth}x{story.MapHeight}:{story.MapValues.Count}");
                foreach (int value in story.MapValues) Increment(mapValues, value);
                Increment(zoneCounts, story.Zones.Count);
                Increment(callbackIds, story.CallbackObjectId);
                Increment(latchDirections, story.LatchDirection);
                Increment(digThresholds, story.DigThreshold);
                Increment(selectedTemps,
                    $"0x{unchecked((uint)story.NativeSelectedTempValue):X8}:" +
                    story.SelectedTempName.Text);

                foreach (OriginalSaveStoryEventDocument storyEvent in story.Events)
                {
                    Assert.That(storyEvent.Name.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                    Assert.That(storyEvent.Trigger.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                    Assert.That(storyEvent.Extra.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                }
                foreach (OriginalSaveStoryTempDocument temp in story.Temps)
                {
                    Assert.That(temp.Name.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                    Increment(tempLayouts,
                        $"{temp.Name.Text}:ref={temp.MinimumDistanceReferenceIndex}:" +
                        $"group={temp.TemplateGroupIndex}:zone={temp.ZoneIndex}:" +
                        $"border={temp.ZoneBorderIndex}:stone={temp.StoneIndex}:" +
                        $"align={(temp.CheckAlign ? 1 : 0)}:dig={temp.DigConnectValue}:" +
                        $"events={temp.EventIndices.Count}:placed={(temp.Placed ? 1 : 0)}");
                }
                foreach (OriginalSaveStoryGroupDocument group in story.Groups)
                {
                    Assert.That(group.Name.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                    Increment(groupLayouts,
                        $"{group.Name.Text}:entries={group.Entries.Count}:" +
                        $"four={group.FourValueRecords.Count}:five={group.FiveValueRecords.Count}:" +
                        $"header={string.Join(',', group.HeaderValues)}");
                    foreach (OriginalSaveStoryGroupEntryDocument entry in group.Entries)
                    {
                        Increment(groupEntryLayouts,
                            $"{group.Name.Text}/{entry.Name.Text}:" +
                            $"{string.Join(',', entry.Values)}:{(entry.Completed ? 1 : 0)}");
                    }
                }
                foreach (OriginalSaveStoryZoneDocument zone in story.Zones)
                    Assert.That(zone.Name.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
            }

            Print("eventCount", eventCounts);
            Print("completedEventCount", completedEventCounts);
            Print("tempCount", tempCounts);
            Print("groupCount", groupCounts);
            Print("pairCount", pairCounts);
            Print("mapDimensions", mapDimensions);
            Print("mapValue", mapValues);
            Print("zoneCount", zoneCounts);
            Print("callbackId", callbackIds);
            Print("latchDirection", latchDirections);
            Print("digThreshold", digThresholds);
            Print("selectedTemp", selectedTemps);
            Print("tempLayout", tempLayouts);
            Print("groupLayout", groupLayouts);
            Print("groupEntryLayout", groupEntryLayouts);
            Assert.That(paths.Length, Is.GreaterThanOrEqualTo(98));
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
