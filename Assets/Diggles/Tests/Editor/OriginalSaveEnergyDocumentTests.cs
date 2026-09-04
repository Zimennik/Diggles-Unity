#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveEnergyDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasTypedPreflightedEnergyState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int sourceCount = 0;
            int leaseCount = 0;
            var sourceClassDistribution = new Dictionary<string, int>(StringComparer.Ordinal);
            var energyClassDistribution = new Dictionary<int, int>();
            var rangeDistribution = new Dictionary<float, int>();
            var remainingTickDistribution = new Dictionary<int, int>();
            var codec = new OriginalSaveArchiveCodec();

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveEnergyDocument energy = document.Energy
                    ?? throw new AssertionException($"{path} has no typed EnergyMgr state.");
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                var objects = document.Objects!.ToDictionary(entity => entity.ObjectId);

                Assert.That(runtime.Energy.Sources.Count, Is.EqualTo(energy.Sources.Count));
                for (int sourceIndex = 0; sourceIndex < energy.Sources.Count; sourceIndex++)
                {
                    OriginalSaveEnergySourceRecord source = energy.Sources[sourceIndex];
                    OriginalSaveRuntimeEnergySourceSnapshot staged = runtime.Energy.Sources[sourceIndex];
                    Assert.That(staged.SourceObjectId, Is.EqualTo(source.SourceObjectId), path);
                    Assert.That(staged.EnergyClass, Is.EqualTo(source.EnergyClass), path);
                    Assert.That(BitConverter.SingleToInt32Bits(staged.Range),
                        Is.EqualTo(source.Range.Bits), path);
                    Assert.That(staged.Leases.Count, Is.EqualTo(source.Leases.Count), path);

                    sourceCount++;
                    Increment(sourceClassDistribution, objects[source.SourceObjectId].ClassName);
                    Increment(energyClassDistribution, source.EnergyClass);
                    Increment(rangeDistribution, source.Range.AsSingle);
                    foreach (OriginalSaveEnergyLeaseRecord lease in source.Leases)
                    {
                        leaseCount++;
                        Assert.That(objects.ContainsKey(lease.ConsumerObjectId), Is.True, path);
                        Assert.That(float.IsNaN(lease.Amount.AsSingle) ||
                            float.IsInfinity(lease.Amount.AsSingle), Is.False, path);
                        Increment(remainingTickDistribution, lease.RemainingTicks);
                    }
                }
            }

            TestContext.WriteLine(
                $"files={paths.Length}, sources={sourceCount}, leases={leaseCount}");
            WriteDistribution("source class", sourceClassDistribution);
            WriteDistribution("energy class", energyClassDistribution);
            WriteDistribution("range", rangeDistribution);
            WriteDistribution("remaining ticks", remainingTickDistribution);
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
            foreach (KeyValuePair<TKey, int> pair in values.OrderBy(pair => pair.Key))
                TestContext.WriteLine($"{label} {pair.Key}: {pair.Value}");
        }
    }
}
