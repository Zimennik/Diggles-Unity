#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;

    public sealed class OriginalSaveHorizonDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasValidTypedHorizonRegions()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var countDistribution = new Dictionary<int, int>();
            var colorDistribution = new Dictionary<string, int>();
            int totalRegions = 0;
            float minimumDepth = float.PositiveInfinity;
            float maximumDepth = float.NegativeInfinity;
            float minimumSize = float.PositiveInfinity;
            float maximumSize = float.NegativeInfinity;
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveHorizonDocument horizon = document.Horizon
                    ?? throw new AssertionException($"{path} has no typed horizon registry.");
                OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(document);
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(plan);
                Assert.That(runtime.Horizon.Regions.Count, Is.EqualTo(horizon.Regions.Count));
                Increment(countDistribution, horizon.Regions.Count);

                for (int index = 0; index < horizon.Regions.Count; index++)
                {
                    OriginalSaveHorizonRegionDocument expected = horizon.Regions[index];
                    OriginalSaveRuntimeHorizonRegion actual = runtime.Horizon.Regions[index];
                    Assert.That(BitConverter.SingleToInt32Bits(actual.PositionX),
                        Is.EqualTo(expected.PositionX.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.PositionY),
                        Is.EqualTo(expected.PositionY.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.PositionZ),
                        Is.EqualTo(expected.PositionZ.Bits));
                    Assert.That(actual.Red, Is.EqualTo(expected.Red));
                    Assert.That(actual.Green, Is.EqualTo(expected.Green));
                    Assert.That(actual.Blue, Is.EqualTo(expected.Blue));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.Depth),
                        Is.EqualTo(expected.Depth.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.Size),
                        Is.EqualTo(expected.Size.Bits));
                    Increment(colorDistribution,
                        $"{expected.Red},{expected.Green},{expected.Blue}");
                    minimumDepth = Math.Min(minimumDepth, actual.Depth);
                    maximumDepth = Math.Max(maximumDepth, actual.Depth);
                    minimumSize = Math.Min(minimumSize, actual.Size);
                    maximumSize = Math.Max(maximumSize, actual.Size);
                    totalRegions++;
                }
            }

            TestContext.WriteLine($"files={paths.Length}, regions={totalRegions}");
            foreach (KeyValuePair<int, int> pair in countDistribution)
                TestContext.WriteLine($"region count {pair.Key}: {pair.Value}");
            foreach (KeyValuePair<string, int> pair in colorDistribution)
                TestContext.WriteLine($"color {pair.Key}: {pair.Value}");
            TestContext.WriteLine(
                $"depth={minimumDepth:R}..{maximumDepth:R}, size={minimumSize:R}..{maximumSize:R}");
        }

        [Test]
        public void HorizonCommandsUseNativeOrderedReplaceRemoveAndClearSemantics()
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);

            interpreter.Eval("horizon_set {10 20 30} 0.5 0.25 1.5 32 40");
            Assert.That(context.HorizonRegions, Has.Count.EqualTo(1));
            HorizonRegionState first = context.HorizonRegions[0];
            Assert.That(first.Red, Is.EqualTo(128));
            Assert.That(first.Green, Is.EqualTo(64));
            Assert.That(first.Blue, Is.EqualTo(255));
            Assert.That(first.Depth, Is.EqualTo(32f));
            Assert.That(first.Size, Is.EqualTo(40f));

            interpreter.Eval("horizon_set {10.5 20 99} 0 0.1 0.2 12 15");
            Assert.That(context.HorizonRegions, Has.Count.EqualTo(1));
            HorizonRegionState replacement = context.HorizonRegions[0];
            Assert.That(replacement.Position.x, Is.EqualTo(10.5f));
            Assert.That(replacement.Position.z, Is.EqualTo(99f));
            Assert.That(replacement.Green, Is.EqualTo(26));
            Assert.That(replacement.Blue, Is.EqualTo(51));

            interpreter.Eval("horizon_set {15 20 30} 1 1 1 8 10");
            Assert.That(context.HorizonRegions, Has.Count.EqualTo(2));
            interpreter.Eval("horizon_rem {10.75 20 -100}");
            Assert.That(context.HorizonRegions, Has.Count.EqualTo(1));
            Assert.That(context.HorizonRegions[0].Position.x, Is.EqualTo(15f));
            interpreter.Eval("horizon_clear");
            Assert.That(context.HorizonRegions, Is.Empty);
        }

        private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }
    }
}
