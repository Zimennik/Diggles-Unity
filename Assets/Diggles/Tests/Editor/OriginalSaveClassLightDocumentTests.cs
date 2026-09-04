#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveClassLightDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasTypedPreflightedClassLightState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int recordCount = 0;
            int objectBoundCount = 0;
            int staticZeroCount = 0;
            int staticOneCount = 0;
            int maximumSlot = -1;
            var objectClassDistribution = new Dictionary<string, int>(StringComparer.Ordinal);
            var radiusDistribution = new Dictionary<float, int>();
            var trailingDistribution = new Dictionary<float, int>();
            var codec = new OriginalSaveArchiveCodec();

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveClassLightDocument classLights = document.ClassLights
                    ?? throw new AssertionException($"{path} has no typed class-light state.");
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                var objects = document.Objects!.ToDictionary(entity => entity.ObjectId);

                Assert.That(runtime.ClassLights.Records.Count,
                    Is.EqualTo(classLights.Records.Count), path);
                var boundIds = new HashSet<int>();
                for (int index = 0; index < classLights.Records.Count; index++)
                {
                    OriginalSaveClassLightRecord source = classLights.Records[index];
                    OriginalSaveRuntimeClassLight staged = runtime.ClassLights.Records[index];
                    AssertRecordEqual(source, staged, path);
                    Assert.That(source.Radius.AsSingle, Is.GreaterThan(0f), path);
                    Assert.That(source.Red, Is.InRange(0, 31), path);
                    Assert.That(source.Green, Is.InRange(0, 31), path);
                    Assert.That(source.Blue, Is.InRange(0, 31), path);

                    recordCount++;
                    maximumSlot = Math.Max(maximumSlot, source.SlotIndex);
                    Increment(radiusDistribution, source.Radius.AsSingle);
                    Increment(trailingDistribution, source.TrailingValue.AsSingle);
                    if (source.IsObjectBound)
                    {
                        objectBoundCount++;
                        Assert.That(boundIds.Add(source.SourceToken), Is.True, path);
                        Assert.That(objects.ContainsKey(source.SourceToken), Is.True, path);
                        Increment(objectClassDistribution, objects[source.SourceToken].ClassName);
                    }
                    else if (source.SourceToken == 0)
                    {
                        staticZeroCount++;
                    }
                    else
                    {
                        Assert.That(source.SourceToken, Is.EqualTo(1), path);
                        staticOneCount++;
                    }
                }
            }

            TestContext.WriteLine(
                $"files={paths.Length}, records={recordCount}, object-bound={objectBoundCount}, " +
                $"static-0={staticZeroCount}, static-1={staticOneCount}, max-slot={maximumSlot}");
            WriteDistribution("object class", objectClassDistribution);
            WriteDistribution("radius", radiusDistribution);
            WriteDistribution("trailing", trailingDistribution);
        }

        [Test]
        public void PlannerRejectsOutOfRangeClassLightColorBeforeStaging()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            byte[] payload = baseline.CopyPayload();
            int firstRedOffset = checked(
                (int)baseline.Metadata.Control!.ControlStateSectionOffset + sizeof(int) * 4);
            WriteInt32(payload, firstRedOffset, 32);
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("invalid 5-bit color"));
        }

        [Test]
        public void PlannerRejectsDanglingClassLightObjectBeforeStaging()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            byte[] payload = baseline.CopyPayload();
            int firstSourceTokenOffset = checked(
                (int)baseline.Metadata.Control!.ControlStateSectionOffset + sizeof(int) * 8);
            WriteInt32(payload, firstSourceTokenOffset, 999999);
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("references undeclared object 999999"));
        }

        private static void AssertRecordEqual(
            OriginalSaveClassLightRecord source,
            OriginalSaveRuntimeClassLight staged,
            string path)
        {
            Assert.That(staged.SlotIndex, Is.EqualTo(source.SlotIndex), path);
            Assert.That(staged.PositionX.Bits, Is.EqualTo(source.PositionX.Bits), path);
            Assert.That(staged.PositionY.Bits, Is.EqualTo(source.PositionY.Bits), path);
            Assert.That(staged.PositionZ.Bits, Is.EqualTo(source.PositionZ.Bits), path);
            Assert.That(staged.Red, Is.EqualTo(source.Red), path);
            Assert.That(staged.Green, Is.EqualTo(source.Green), path);
            Assert.That(staged.Blue, Is.EqualTo(source.Blue), path);
            Assert.That(staged.Radius.Bits, Is.EqualTo(source.Radius.Bits), path);
            Assert.That(staged.SourceToken, Is.EqualTo(source.SourceToken), path);
            Assert.That(staged.TrailingValue.Bits, Is.EqualTo(source.TrailingValue.Bits), path);
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private static OriginalSaveArchiveDocument ReadPayload(byte[] payload)
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
                gzip.Write(payload, 0, payload.Length);
            compressed.Position = 0;
            return new OriginalSaveArchiveCodec().Read(compressed);
        }

        private static void WriteInt32(byte[] destination, int offset, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, destination, offset, sizeof(int));
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
