#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveNewsTickerDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasValidTypedNewsTickerState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int activeCount = 0;
            int historyCount = 0;
            int displayQueueCount = 0;
            int danglingQueueCount = 0;
            int activeHistoryMismatchCount = 0;
            var activeCountDistribution = new Dictionary<int, int>();
            var historyCountDistribution = new Dictionary<int, int>();
            var queueCountDistribution = new Dictionary<int, int>();
            int invalidActiveCategoryCount = 0;
            int invalidHistoryCategoryCount = 0;
            var activeCategoryDistribution = new Dictionary<int, int>();
            var historyCategoryDistribution = new Dictionary<int, int>();
            var maskDistribution = new Dictionary<int, int>();
            var flagDistribution = new Dictionary<string, int>(StringComparer.Ordinal);
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveNewsTickerDocument ticker = document.NewsTicker
                    ?? throw new AssertionException($"{path} has no typed news-ticker state.");
                OriginalSaveLoadPlan plan;
                try
                {
                    plan = new OriginalSaveLoadPlanner().Prepare(document);
                }
                catch (Exception exception)
                {
                    string first = ticker.Active.Count == 0
                        ? "no active records"
                        : $"first active: id={ticker.Active[0].Id}, " +
                          $"color=0x{unchecked((uint)ticker.Active[0].PackedColor):X8}, " +
                          $"category={ticker.Active[0].NativeCategory}, " +
                          $"metrics={ticker.Active[0].TextMetricX}/{ticker.Active[0].TextMetricY}, " +
                          $"priority={ticker.Active[0].Priority}, " +
                          $"effective={ticker.Active[0].EffectivePriority}, " +
                          $"time={ticker.Active[0].RemainingTime}";
                    throw new InvalidDataException($"{path}: {first}", exception);
                }
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(plan);
                Assert.That(runtime.NewsTicker.Active.Count, Is.EqualTo(ticker.Active.Count));
                Assert.That(runtime.NewsTicker.History.Count, Is.EqualTo(ticker.History.Count));
                Assert.That(runtime.NewsTicker.DisplayQueue.Count,
                    Is.EqualTo(ticker.DisplayQueue.Count));
                Assert.That(runtime.NewsTicker.EnabledCategoryMask,
                    Is.EqualTo(ticker.EnabledCategoryMask));
                var canonical = runtime.NewsTicker.CreateSaveState();
                for (int index = 0; index < ticker.Active.Count; index++)
                {
                    OriginalSaveNewsTickerActiveRecord source = ticker.Active[index];
                    Assert.That(runtime.NewsTicker.Active[index].NativeCategory,
                        Is.EqualTo(source.NativeCategory));
                    Assert.That(runtime.NewsTicker.Active[index].Category,
                        Is.InRange(0, 5));
                    Assert.That(canonical.Active[index].PackedColorWord,
                        Is.EqualTo(source.PackedColor));
                    Assert.That(canonical.Active[index].NativeCategoryWord,
                        Is.EqualTo(source.NativeCategory));
                    Assert.That(canonical.Active[index].TextMetricX,
                        Is.EqualTo(source.TextMetricX));
                    Assert.That(canonical.Active[index].TextMetricY,
                        Is.EqualTo(source.TextMetricY));
                    Assert.That(BitConverter.SingleToInt32Bits(canonical.Active[index].Priority),
                        Is.EqualTo(source.Priority.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(
                            canonical.Active[index].EffectivePriority),
                        Is.EqualTo(source.EffectivePriority.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(
                            canonical.Active[index].RemainingTime),
                        Is.EqualTo(source.RemainingTime.Bits));
                    Assert.That(canonical.Active[index].PresentationFlag0,
                        Is.EqualTo(source.PresentationFlag0));
                    Assert.That(canonical.Active[index].PresentationFlag1,
                        Is.EqualTo(source.PresentationFlag1));
                    Assert.That(BitConverter.DoubleToInt64Bits(canonical.Active[index].Timestamp),
                        Is.EqualTo(BitConverter.DoubleToInt64Bits(source.Timestamp)));
                    if ((uint)source.NativeCategory >= 6)
                    {
                        Assert.That(runtime.NewsTicker.Active[index].Category,
                            Is.EqualTo(0));
                    }
                }
                for (int index = 0; index < ticker.History.Count; index++)
                {
                    OriginalSaveNewsTickerHistoryRecord source = ticker.History[index];
                    Assert.That(runtime.NewsTicker.History[index].NativeCategory,
                        Is.EqualTo(source.NativeCategory));
                    Assert.That(runtime.NewsTicker.History[index].Category,
                        Is.InRange(0, 5));
                    Assert.That(canonical.History[index].PackedColorWord,
                        Is.EqualTo(source.PackedColor));
                    Assert.That(canonical.History[index].NativeCategoryWord,
                        Is.EqualTo(source.NativeCategory));
                    if ((uint)source.NativeCategory >= 6)
                    {
                        Assert.That(runtime.NewsTicker.History[index].Category,
                            Is.EqualTo(0));
                    }
                }

                Increment(activeCountDistribution, ticker.Active.Count);
                Increment(historyCountDistribution, ticker.History.Count);
                Increment(queueCountDistribution, ticker.DisplayQueue.Count);
                Increment(maskDistribution, ticker.EnabledCategoryMask);
                var activeIds = new HashSet<int>();
                foreach (OriginalSaveNewsTickerActiveRecord entry in ticker.Active)
                {
                    activeIds.Add(entry.Id);
                    Increment(activeCategoryDistribution, entry.NativeCategory);
                    if ((uint)entry.NativeCategory >= 6) invalidActiveCategoryCount++;
                    Increment(flagDistribution,
                        $"{entry.PresentationFlag0}/{entry.PresentationFlag1}/" +
                        $"{entry.AppearsInHistory}");
                    activeCount++;

                    if (entry.AppearsInHistory == 0) continue;
                    OriginalSaveNewsTickerHistoryRecord? history = ticker.History.FirstOrDefault(
                        candidate => candidate.Id == entry.Id);
                    if (history == null || history.Text.Text != entry.Text.Text ||
                        history.PackedColor != entry.PackedColor ||
                        history.ClickScript.Text != entry.ClickScript.Text ||
                        history.NativeCategory != entry.NativeCategory)
                    {
                        activeHistoryMismatchCount++;
                    }
                }
                historyCount += ticker.History.Count;
                foreach (OriginalSaveNewsTickerHistoryRecord entry in ticker.History)
                {
                    Increment(historyCategoryDistribution, entry.NativeCategory);
                    if ((uint)entry.NativeCategory >= 6) invalidHistoryCategoryCount++;
                }
                foreach (OriginalSaveNewsTickerDisplayQueueRecord entry in ticker.DisplayQueue)
                {
                    if (!activeIds.Contains(entry.Id)) danglingQueueCount++;
                    displayQueueCount++;
                }
                for (int index = 0; index < ticker.DisplayQueue.Count; index++)
                {
                    Assert.That(canonical.DisplayQueue[index].Id,
                        Is.EqualTo(ticker.DisplayQueue[index].Id));
                    Assert.That(BitConverter.SingleToInt32Bits(
                            canonical.DisplayQueue[index].Position),
                        Is.EqualTo(ticker.DisplayQueue[index].Position.Bits));
                }
            }

            TestContext.WriteLine(
                $"files={paths.Length}, active={activeCount}, history={historyCount}, " +
                $"displayQueue={displayQueueCount}, danglingQueue={danglingQueueCount}, " +
                $"activeHistoryMismatch={activeHistoryMismatchCount}, " +
                $"invalidActiveCategory={invalidActiveCategoryCount}, " +
                $"invalidHistoryCategory={invalidHistoryCategoryCount}");
            WriteDistribution("active count", activeCountDistribution);
            WriteDistribution("history count", historyCountDistribution);
            WriteDistribution("display queue count", queueCountDistribution);
            WriteDistribution("active category", activeCategoryDistribution);
            WriteDistribution("history category", historyCategoryDistribution);
            WriteDistribution("mask", maskDistribution);
            WriteDistribution("flags", flagDistribution);
        }

        private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }

        private static void WriteDistribution<TKey>(string label, Dictionary<TKey, int> values)
            where TKey : notnull
        {
            foreach (KeyValuePair<TKey, int> pair in values)
                TestContext.WriteLine($"{label} {pair.Key}: {pair.Value}");
        }
    }
}
