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

    public sealed class OriginalSaveObjectParticleStateTests
    {
        [Test]
        public void EveryCurrentSaveHasTypedPreflightedObjectParticleState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int objectStateCount = 0;
            int activeAttachmentCount = 0;
            int staleHandleCount = 0;
            int duplicateHandleCount = 0;
            int orphanParticleCount = 0;
            int maximumSourceIndex = -1;
            var handleCountDistribution = new Dictionary<int, int>();
            var classDistribution = new Dictionary<string, int>(StringComparer.Ordinal);
            var codec = new OriginalSaveArchiveCodec();

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                var particles = document.Particles!.Records.ToDictionary(record => record.SlotIndex);
                var attachedHandles = new HashSet<int>();

                foreach (OriginalSaveObjectDocument obj in document.Objects!)
                {
                    OriginalSaveObjectParticleStateDocument source = obj.CommonState.ParticleState;
                    OriginalSaveRuntimeObjectParticleState staged =
                        runtime.EntitiesById[obj.ObjectId].ParticleState;
                    Assert.That(staged.IsPresent, Is.EqualTo(source.IsPresent), path);
                    Assert.That(staged.Sources.Count, Is.EqualTo(source.Sources.Count), path);
                    if (!source.IsPresent) continue;

                    objectStateCount++;
                    Increment(classDistribution, obj.ClassName);
                    int activeForObject = 0;
                    for (int index = 0; index < source.Sources.Count; index++)
                    {
                        OriginalSaveObjectParticleSourceRecord expected = source.Sources[index];
                        OriginalSaveObjectParticleSourceRecord actual = staged.Sources[index];
                        AssertRecordEqual(expected, actual, path);
                        Assert.That(AllFloatsAreFinite(expected), Is.True, path);
                        Assert.That(expected.ParticleHandle, Is.InRange(-1, 4095), path);
                        if (!expected.HasParticleHandle) continue;

                        activeForObject++;
                        activeAttachmentCount++;
                        maximumSourceIndex = Math.Max(maximumSourceIndex, expected.SourceIndex);
                        if (!particles.ContainsKey(expected.ParticleHandle)) staleHandleCount++;
                        if (!attachedHandles.Add(expected.ParticleHandle)) duplicateHandleCount++;
                    }
                    Increment(handleCountDistribution, activeForObject);
                }

                orphanParticleCount += particles.Keys.Count(handle => !attachedHandles.Contains(handle));
            }

            TestContext.WriteLine(
                $"files={paths.Length}, object-states={objectStateCount}, " +
                $"active-attachments={activeAttachmentCount}, stale={staleHandleCount}, " +
                $"duplicates={duplicateHandleCount}, orphan-particles={orphanParticleCount}, " +
                $"max-source-index={maximumSourceIndex}");
            WriteDistribution("handles per object", handleCountDistribution);
            WriteDistribution("class", classDistribution);
        }

        [Test]
        public void PlannerRejectsOutOfRangeObjectParticleHandleBeforeStaging()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            OriginalSaveObjectDocument gnome = baseline.Objects!.FindById(51)
                ?? throw new AssertionException("Tutorial gnome 51 was not found.");
            byte[] payload = baseline.CopyPayload();
            int stateOffset = FindParticleStateOffset(payload, gnome);
            WriteInt32(payload, stateOffset, 4096);
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("invalid native handle 4096"));
        }

        [Test]
        public void PlannerRejectsNonFiniteObjectParticlePositionBeforeStaging()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            OriginalSaveObjectDocument gnome = baseline.Objects!.FindById(51)
                ?? throw new AssertionException("Tutorial gnome 51 was not found.");
            byte[] payload = baseline.CopyPayload();
            int stateOffset = FindParticleStateOffset(payload, gnome);
            WriteInt32(payload, stateOffset + 32 * sizeof(int), unchecked((int)0x7FC00000));
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("non-finite relative position"));
        }

        private static int FindParticleStateOffset(
            byte[] payload,
            OriginalSaveObjectDocument obj)
        {
            IReadOnlyList<int> values = obj.CommonState.ParticleState.RawValues;
            Assert.That(values, Has.Count.EqualTo(160));
            var pattern = new byte[values.Count * sizeof(int)];
            for (int index = 0; index < values.Count; index++)
            {
                byte[] bytes = BitConverter.GetBytes(values[index]);
                Buffer.BlockCopy(bytes, 0, pattern, index * sizeof(int), sizeof(int));
            }

            int start = checked((int)obj.Offset);
            int end = checked((int)obj.OpaqueTailOffset);
            int found = -1;
            for (int offset = start; offset <= end - pattern.Length; offset++)
            {
                int index = 0;
                while (index < pattern.Length && payload[offset + index] == pattern[index]) index++;
                if (index != pattern.Length) continue;
                Assert.That(found, Is.EqualTo(-1), "Object particle state pattern is not unique.");
                found = offset;
            }
            Assert.That(found, Is.GreaterThanOrEqualTo(0), "Object particle state pattern was not found.");
            return found;
        }

        private static bool AllFloatsAreFinite(OriginalSaveObjectParticleSourceRecord source) =>
            Finite(source.RelativePositionX.AsSingle) &&
            Finite(source.RelativePositionY.AsSingle) &&
            Finite(source.RelativePositionZ.AsSingle);

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static void AssertRecordEqual(
            OriginalSaveObjectParticleSourceRecord expected,
            OriginalSaveObjectParticleSourceRecord actual,
            string path)
        {
            Assert.That(actual.SourceIndex, Is.EqualTo(expected.SourceIndex), path);
            Assert.That(actual.ParticleHandle, Is.EqualTo(expected.ParticleHandle), path);
            Assert.That(actual.RelativePositionX.Bits,
                Is.EqualTo(expected.RelativePositionX.Bits), path);
            Assert.That(actual.RelativePositionY.Bits,
                Is.EqualTo(expected.RelativePositionY.Bits), path);
            Assert.That(actual.RelativePositionZ.Bits,
                Is.EqualTo(expected.RelativePositionZ.Bits), path);
            Assert.That(actual.RawLinkId, Is.EqualTo(expected.RawLinkId), path);
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

        private static void WriteDistribution<TKey>(
            string label,
            Dictionary<TKey, int> distribution)
            where TKey : notnull
        {
            TestContext.WriteLine(
                $"{label}: " + string.Join(", ", distribution.OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}={pair.Value}")));
        }
    }
}
