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

    public sealed class OriginalSaveTimerDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasExactTypedPreflightedTimers()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths.Length, Is.GreaterThanOrEqualTo(98));

            int timerCount = 0;
            int maximumPerFile = 0;
            var eventNames = new HashSet<string>(StringComparer.Ordinal);
            var userIds = new HashSet<int>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveTimerDocument timers = document.Timers
                    ?? throw new AssertionException($"{path} has no typed timer state.");
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                Assert.That(runtime.Timers.Records.Count, Is.EqualTo(timers.Records.Count), path);
                maximumPerFile = Math.Max(maximumPerFile, timers.Records.Count);

                for (int index = 0; index < timers.Records.Count; index++)
                {
                    OriginalSaveTimerRecord source = timers.Records[index];
                    OriginalSaveRuntimeTimer staged = runtime.Timers.Records[index];
                    AssertTimerEqual(source, staged, path);
                    Assert.That(source.OwnerObjectId,
                        Is.EqualTo(source.EventPayload.OwnerObjectId), path);
                    Assert.That(Finite(source.NextFire.AsDouble), Is.True, path);
                    Assert.That(Finite(source.Interval.AsDouble), Is.True, path);
                    Assert.That(Finite(source.EventPayload.CreatedAt.AsDouble), Is.True, path);
                    Assert.That(EventValuesAreFinite(source.EventPayload), Is.True, path);
                    timerCount++;
                    eventNames.Add(source.EventPayload.EventName);
                    userIds.Add(source.UserId);
                }
            }

            Assert.That(timerCount, Is.GreaterThan(200000));
            TestContext.WriteLine(
                $"files={paths.Length}, timers={timerCount}, max-per-file={maximumPerFile}, " +
                $"event-names={eventNames.Count}, user-ids={string.Join(",", userIds.OrderBy(id => id))}");
        }

        [Test]
        public void PlannerRejectsTimerWhoseEmbeddedOwnerDoesNotMatchSchedulerOwner()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            byte[] payload = baseline.CopyPayload();
            int eventOwnerOffset = checked(
                (int)baseline.Metadata.Objects!.SupportSectionOffset + 64);
            WriteInt32(payload, eventOwnerOffset, 999999);
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("does not match event owner 999999"));
        }

        [Test]
        public void PlannerRejectsNonFiniteTimerFireTime()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            byte[] payload = baseline.CopyPayload();
            int nextFireOffset = checked(
                (int)baseline.Metadata.Objects!.SupportSectionOffset + 8);
            WriteInt64(payload, nextFireOffset, unchecked((long)0x7FF8000000000000));
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("non-finite time value"));
        }

        private static void AssertTimerEqual(
            OriginalSaveTimerRecord source,
            OriginalSaveRuntimeTimer staged,
            string path)
        {
            OriginalSaveTimerEventPayloadDocument payload = source.EventPayload;
            Assert.That(staged.UserId, Is.EqualTo(source.UserId), path);
            Assert.That(staged.NextFire.Bits, Is.EqualTo(source.NextFire.Bits), path);
            Assert.That(staged.Interval.Bits, Is.EqualTo(source.Interval.Bits), path);
            Assert.That(staged.RepeatsLeft, Is.EqualTo(source.RepeatsLeft), path);
            Assert.That(staged.OwnerObjectId, Is.EqualTo(source.OwnerObjectId), path);
            Assert.That(staged.EventName, Is.EqualTo(payload.EventName), path);
            CollectionAssert.AreEqual(payload.CopyFixedEventName(), staged.CopyFixedEventName(), path);
            Assert.That(staged.EventOwnerObjectId, Is.EqualTo(payload.OwnerObjectId), path);
            Assert.That(staged.Subject1ObjectId, Is.EqualTo(payload.Subject1ObjectId), path);
            Assert.That(staged.Subject2ObjectId, Is.EqualTo(payload.Subject2ObjectId), path);
            Assert.That(staged.Subject3ObjectId, Is.EqualTo(payload.Subject3ObjectId), path);
            Assert.That(staged.Position1X.Bits, Is.EqualTo(payload.Position1X.Bits), path);
            Assert.That(staged.Position1Y.Bits, Is.EqualTo(payload.Position1Y.Bits), path);
            Assert.That(staged.Position1Z.Bits, Is.EqualTo(payload.Position1Z.Bits), path);
            Assert.That(staged.Position2X.Bits, Is.EqualTo(payload.Position2X.Bits), path);
            Assert.That(staged.Position2Y.Bits, Is.EqualTo(payload.Position2Y.Bits), path);
            Assert.That(staged.Position2Z.Bits, Is.EqualTo(payload.Position2Z.Bits), path);
            Assert.That(staged.Position3X.Bits, Is.EqualTo(payload.Position3X.Bits), path);
            Assert.That(staged.Position3Y.Bits, Is.EqualTo(payload.Position3Y.Bits), path);
            Assert.That(staged.Position3Z.Bits, Is.EqualTo(payload.Position3Z.Bits), path);
            CollectionAssert.AreEqual(
                payload.Text1.CopyBytesIncludingTerminator(),
                staged.Text1.CopyBytesIncludingTerminator(),
                path);
            CollectionAssert.AreEqual(
                payload.Text2.CopyBytesIncludingTerminator(),
                staged.Text2.CopyBytesIncludingTerminator(),
                path);
            Assert.That(staged.Number1.Bits, Is.EqualTo(payload.Number1.Bits), path);
            Assert.That(staged.Number2.Bits, Is.EqualTo(payload.Number2.Bits), path);
            Assert.That(staged.Number3.Bits, Is.EqualTo(payload.Number3.Bits), path);
            Assert.That(staged.CreatedAt.Bits, Is.EqualTo(payload.CreatedAt.Bits), path);
        }

        private static bool EventValuesAreFinite(OriginalSaveTimerEventPayloadDocument value) =>
            Finite(value.Position1X.AsSingle) &&
            Finite(value.Position1Y.AsSingle) &&
            Finite(value.Position1Z.AsSingle) &&
            Finite(value.Position2X.AsSingle) &&
            Finite(value.Position2Y.AsSingle) &&
            Finite(value.Position2Z.AsSingle) &&
            Finite(value.Position3X.AsSingle) &&
            Finite(value.Position3Y.AsSingle) &&
            Finite(value.Position3Z.AsSingle) &&
            Finite(value.Number1.AsSingle) &&
            Finite(value.Number2.AsSingle) &&
            Finite(value.Number3.AsSingle);

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

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
    }
}
