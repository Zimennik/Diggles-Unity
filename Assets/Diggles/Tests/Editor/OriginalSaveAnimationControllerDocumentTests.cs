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

    public sealed class OriginalSaveAnimationControllerDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasTypedPreflightedAnimationControllerState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int controllerCount = 0;
            int dynamicControllerCount = 0;
            int staticControllerCount = 0;
            int maximumIndex = -1;
            int rawDfByteCount = 0;
            int rawDfWordCount = 0;
            var modelSlotDistribution = new Dictionary<int, int>();
            var valueCountDistribution = new Dictionary<int, int>();
            var codec = new OriginalSaveArchiveCodec();

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveAnimationControllerDocument controllers =
                    document.AnimationControllers ?? throw new AssertionException(
                        $"{path} has no typed animation-controller state.");
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                var objectIds = document.Objects!.Select(entity => entity.ObjectId).ToHashSet();
                var controllerIds = new HashSet<int>();

                Assert.That(runtime.AnimationControllers.Records.Count,
                    Is.EqualTo(controllers.Records.Count), path);
                for (int index = 0; index < controllers.Records.Count; index++)
                {
                    OriginalSaveAnimationControllerRecord source = controllers.Records[index];
                    OriginalSaveRuntimeAnimationController staged =
                        runtime.AnimationControllers.Records[index];
                    AssertRecordEqual(source, staged, path);
                    Assert.That(controllerIds.Add(source.ControllerIndex), Is.True, path);
                    Assert.That(source.DeclaredValueCount, Is.EqualTo(source.Values.Count), path);
                    Assert.That(source.RepeatedControllerIndex,
                        Is.EqualTo(source.ControllerIndex), path);

                    controllerCount++;
                    maximumIndex = Math.Max(maximumIndex, source.ControllerIndex);
                    if (objectIds.Contains(source.ControllerIndex)) dynamicControllerCount++;
                    else staticControllerCount++;
                    if (source.RawStateByte == 0xDF) rawDfByteCount++;
                    if (source.RawControlA == unchecked((int)0xDFDFDFDF)) rawDfWordCount++;
                    if (source.RawControlB == unchecked((int)0xDFDFDFDF)) rawDfWordCount++;
                    Increment(modelSlotDistribution, source.ModelDatabaseSlot);
                    Increment(valueCountDistribution, source.Values.Count);
                }

                Assert.That(objectIds.IsSubsetOf(controllerIds), Is.True, path);
            }

            TestContext.WriteLine(
                $"files={paths.Length}, controllers={controllerCount}, dynamic={dynamicControllerCount}, " +
                $"static={staticControllerCount}, max-index={maximumIndex}, raw-byte-df={rawDfByteCount}, " +
                $"raw-word-df={rawDfWordCount}");
            TestContext.WriteLine(
                $"model-slot min={modelSlotDistribution.Keys.Min()}, " +
                $"max={modelSlotDistribution.Keys.Max()}, distinct={modelSlotDistribution.Count}");
            TestContext.WriteLine(
                $"value-count min={valueCountDistribution.Keys.Min()}, " +
                $"max={valueCountDistribution.Keys.Max()}, distinct={valueCountDistribution.Count}");
        }

        [Test]
        public void PlannerRejectsAnimationControllerWithMismatchedRepeatedIndex()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            OriginalSaveAnimationControllerRecord first = baseline.AnimationControllers!.Records[0];
            byte[] payload = baseline.CopyPayload();
            int repeatedIndexOffset = checked(
                (int)baseline.Metadata.Graphics!.AnimationControllerSectionOffset +
                sizeof(int) + sizeof(byte) + sizeof(long) + sizeof(int) * 3 +
                first.Values.Count * sizeof(int));
            WriteInt32(payload, repeatedIndexOffset, first.ControllerIndex + 1);
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("repeats index"));
        }

        [Test]
        public void PlannerRejectsAnimationControllerWithNonFiniteTimeState()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            byte[] payload = baseline.CopyPayload();
            // Finiteness is only enforced for renderable object-backed records (declared object
            // index + model slot 0..255) — orphan/logic-only records legally carry allocator
            // garbage (B8 Claim 4b). Corrupt the FIRST renderable record: walk the fixed-shape
            // records (45 bytes + 4 per vector value; time/state at record offset +5).
            var declaredIds = new HashSet<int>(
                baseline.Objects!.Select(obj => obj.ObjectId));
            long recordOffset = baseline.Metadata.Graphics!.AnimationControllerSectionOffset;
            int timeStateOffset = -1;
            foreach (OriginalSaveAnimationControllerRecord record in
                     baseline.AnimationControllers!.Records)
            {
                if (declaredIds.Contains(record.ControllerIndex) &&
                    record.ModelDatabaseSlot is >= 0 and <= 255)
                {
                    timeStateOffset = checked((int)recordOffset + sizeof(int) + sizeof(byte));
                    break;
                }
                recordOffset += 45L + 4L * record.Values.Count;
            }
            Assert.That(timeStateOffset, Is.GreaterThan(0),
                "the tutorial fixture must contain a renderable object-backed controller");
            WriteInt64(payload, timeStateOffset, unchecked((long)0x7FF8000000000000));
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("non-finite time/state"));
        }

        private static void AssertRecordEqual(
            OriginalSaveAnimationControllerRecord source,
            OriginalSaveRuntimeAnimationController staged,
            string path)
        {
            Assert.That(staged.ControllerIndex, Is.EqualTo(source.ControllerIndex), path);
            Assert.That(staged.RawStateByte, Is.EqualTo(source.RawStateByte), path);
            Assert.That(staged.NativeTimeState.Bits, Is.EqualTo(source.NativeTimeState.Bits), path);
            Assert.That(staged.DeclaredValueCount, Is.EqualTo(source.DeclaredValueCount), path);
            Assert.That(staged.CurrentOrCompatibilityValue,
                Is.EqualTo(source.CurrentOrCompatibilityValue), path);
            CollectionAssert.AreEqual(source.Values, staged.Values, path);
            Assert.That(staged.RepeatedControllerIndex,
                Is.EqualTo(source.RepeatedControllerIndex), path);
            Assert.That(staged.RawControlA, Is.EqualTo(source.RawControlA), path);
            Assert.That(staged.RawControlB, Is.EqualTo(source.RawControlB), path);
            Assert.That(staged.ModelDatabaseSlot, Is.EqualTo(source.ModelDatabaseSlot), path);
            Assert.That(staged.AnimationState, Is.EqualTo(source.AnimationState), path);
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

        private static void WriteInt64(byte[] destination, int offset, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, destination, offset, sizeof(long));
        }

        private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }
    }
}
