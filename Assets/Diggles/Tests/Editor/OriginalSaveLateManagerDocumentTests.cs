#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveLateManagerDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasTypedObjectIconsAndCaveRecords()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int activeIconCount = 0;
            int danglingActiveIconCount = 0;
            int caveRecordCount = 0;
            int outOfWorldCaveRecordCount = 0;
            var iconTypeDistribution = new Dictionary<int, int>();
            var iconOpaqueDistribution = new Dictionary<int, int>();
            var iconOffsetDistribution = new Dictionary<string, int>(StringComparer.Ordinal);
            var caveCountDistribution = new Dictionary<int, int>();
            var caveTemplateDistribution = new Dictionary<int, int>();
            var codec = new OriginalSaveArchiveCodec();

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveObjectIconDocument icons = document.ObjectIcons
                    ?? throw new AssertionException($"{path} has no typed object-icon state.");
                OriginalSaveCaveRecordDocument caves = document.CaveRecords
                    ?? throw new AssertionException($"{path} has no typed cave-record state.");
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));

                Assert.That(icons.Slots, Has.Count.EqualTo(OriginalSaveObjectIconDocument.SlotCount));
                Assert.That(runtime.ObjectIcons.Slots.Count, Is.EqualTo(icons.Slots.Count));
                Assert.That(runtime.CaveRecords.Records.Count, Is.EqualTo(caves.Records.Count));
                var objectIds = document.Objects!.Select(entity => entity.ObjectId).ToHashSet();
                foreach (OriginalSaveObjectIconRecord icon in icons.Slots)
                {
                    OriginalSaveRuntimeObjectIconEntry staged = runtime.ObjectIcons.Slots[icon.Slot];
                    Assert.That(staged.ObjectId, Is.EqualTo(icon.ObjectId));
                    Assert.That(staged.Value, Is.EqualTo(icon.Value));
                    Assert.That(staged.OpaqueRuntimeWord, Is.EqualTo(icon.OpaqueRuntimeWord));
                    Assert.That(staged.IconType, Is.EqualTo(icon.IconType));
                    Assert.That(BitConverter.SingleToInt32Bits(staged.OffsetX),
                        Is.EqualTo(icon.OffsetX.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(staged.OffsetY),
                        Is.EqualTo(icon.OffsetY.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(staged.OffsetZ),
                        Is.EqualTo(icon.OffsetZ.Bits));
                    if (!icon.IsActive) continue;
                    activeIconCount++;
                    Increment(iconTypeDistribution, icon.IconType);
                    Increment(iconOpaqueDistribution, icon.OpaqueRuntimeWord);
                    Increment(iconOffsetDistribution,
                        $"0x{unchecked((uint)icon.OffsetX.Bits):X8}/" +
                        $"0x{unchecked((uint)icon.OffsetY.Bits):X8}/" +
                        $"0x{unchecked((uint)icon.OffsetZ.Bits):X8}");
                    if (!objectIds.Contains(icon.ObjectId)) danglingActiveIconCount++;
                }

                Increment(caveCountDistribution, caves.Records.Count);
                foreach (OriginalSaveCaveRecord cave in caves.Records)
                {
                    caveRecordCount++;
                    Increment(caveTemplateDistribution, cave.TemplateIndex);
                    if (cave.X0 < document.World.MinX || cave.Y0 < document.World.MinY ||
                        cave.X1 >= document.World.MaxXExclusive ||
                        cave.Y1 >= document.World.MaxYExclusive)
                    {
                        outOfWorldCaveRecordCount++;
                    }
                }
            }

            TestContext.WriteLine(
                $"files={paths.Length}, activeIcons={activeIconCount}, " +
                $"danglingActiveIcons={danglingActiveIconCount}, caves={caveRecordCount}, " +
                $"outOfWorldCaves={outOfWorldCaveRecordCount}");
            WriteDistribution("icon type", iconTypeDistribution);
            WriteDistribution("icon opaque", iconOpaqueDistribution);
            WriteDistribution("icon offset", iconOffsetDistribution);
            WriteDistribution("cave count", caveCountDistribution);
            WriteDistribution("cave template", caveTemplateDistribution);
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
