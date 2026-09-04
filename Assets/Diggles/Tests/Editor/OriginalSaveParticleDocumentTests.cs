#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveParticleDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasTypedPreflightedParticleState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int recordCount = 0;
            int maximumSlot = -1;
            int objectLinkedCount = 0;
            int staticZeroCount = 0;
            var kindDistribution = new Dictionary<int, int>();
            var modeDistribution = new Dictionary<int, int>();
            var flagDistribution = new Dictionary<byte, int>();
            var codec = new OriginalSaveArchiveCodec();

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveParticleDocument particles = document.Particles
                    ?? throw new AssertionException($"{path} has no typed particle state.");
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                var objectIds = document.Objects!.Select(entity => entity.ObjectId).ToHashSet();

                Assert.That(runtime.Particles.Records.Count,
                    Is.EqualTo(particles.Records.Count), path);
                for (int index = 0; index < particles.Records.Count; index++)
                {
                    OriginalSaveParticleRecord source = particles.Records[index];
                    OriginalSaveRuntimeParticle staged = runtime.Particles.Records[index];
                    AssertRecordEqual(source, staged, path);
                    Assert.That(source.ParticleCount, Is.GreaterThanOrEqualTo(0), path);
                    Assert.That(source.RawFlag64, Is.InRange((byte)0, (byte)1), path);
                    Assert.That(AllFloatsAreFinite(source), Is.True, path);

                    recordCount++;
                    maximumSlot = Math.Max(maximumSlot, source.SlotIndex);
                    Increment(kindDistribution, source.ParticleKind);
                    Increment(modeDistribution, source.Mode);
                    Increment(flagDistribution, source.RawFlag64);
                    if (source.IsObjectLinked)
                    {
                        objectLinkedCount++;
                        Assert.That(objectIds.Contains(source.LinkedObjectToken), Is.True, path);
                    }
                    else if (source.LinkedObjectToken == 0)
                    {
                        staticZeroCount++;
                    }
                    else
                    {
                        Assert.That(source.LinkedObjectToken, Is.EqualTo(-1), path);
                    }
                }
            }

            TestContext.WriteLine(
                $"files={paths.Length}, records={recordCount}, max-slot={maximumSlot}, " +
                $"object-linked={objectLinkedCount}, static-zero={staticZeroCount}");
            WriteDistribution("kind", kindDistribution);
            WriteDistribution("mode", modeDistribution);
            WriteDistribution("flag", flagDistribution);
        }

        [Test]
        public void PlannerRejectsNonFiniteParticlePositionBeforeStaging()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            byte[] payload = baseline.CopyPayload();
            int firstPositionXOffset = checked(
                (int)baseline.Metadata.Graphics!.GraphicsEntrySectionOffset + sizeof(int) * 3);
            WriteInt32(payload, firstPositionXOffset, unchecked((int)0x7FC00000));
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("non-finite floating value"));
        }

        [Test]
        public void PlannerRejectsDanglingParticleObjectLinkBeforeStaging()
        {
            OriginalSaveArchiveDocument baseline = ReadFixture("save_tutorial.sav");
            byte[] payload = baseline.CopyPayload();
            int firstLinkedObjectOffset = checked(
                (int)baseline.Metadata.Graphics!.GraphicsEntrySectionOffset + 53);
            WriteInt32(payload, firstLinkedObjectOffset, 999999);
            OriginalSaveArchiveDocument corrupted = ReadPayload(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveLoadPlanner().Prepare(corrupted))!;
            Assert.That(exception.Message, Does.Contain("references undeclared object 999999"));
        }

        [Test]
        public void NativeTerminalModesAdvanceOnFirstOrdinaryParticleTick()
        {
            var particles = new ParticleSystemState();
            ParticleSystemState.Source inactive = particles.Configure(
                10, 0, 1, default, default, 10, 1, false, -1, -1);
            inactive.HasNativeState = true;
            inactive.NativeMode = 5;
            inactive.Active = true;
            ParticleSystemState.Source expired = particles.Configure(
                11, 0, 1, default, default, 10, 1, false, -1, -1);
            expired.HasNativeState = true;
            expired.NativeMode = 6;
            expired.Active = true;

            particles.Tick(0.01);

            Assert.That(particles.Sources.ContainsKey((10, 0)), Is.True);
            Assert.That(inactive.NativeMode, Is.Zero);
            Assert.That(inactive.Active, Is.False);
            Assert.That(particles.Sources.ContainsKey((11, 0)), Is.False);
        }

        private static bool AllFloatsAreFinite(OriginalSaveParticleRecord particle) =>
            Finite(particle.PositionX.AsSingle) &&
            Finite(particle.PositionY.AsSingle) &&
            Finite(particle.PositionZ.AsSingle) &&
            Finite(particle.DirectionX.AsSingle) &&
            Finite(particle.DirectionY.AsSingle) &&
            Finite(particle.DirectionZ.AsSingle) &&
            Finite(particle.RawValue60.AsSingle) &&
            Finite(particle.RawValue68.AsSingle);

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static void AssertRecordEqual(
            OriginalSaveParticleRecord source,
            OriginalSaveRuntimeParticle staged,
            string path)
        {
            Assert.That(staged.SlotIndex, Is.EqualTo(source.SlotIndex), path);
            Assert.That(staged.ParticleKind, Is.EqualTo(source.ParticleKind), path);
            Assert.That(staged.ParticleCount, Is.EqualTo(source.ParticleCount), path);
            Assert.That(staged.PositionX.Bits, Is.EqualTo(source.PositionX.Bits), path);
            Assert.That(staged.PositionY.Bits, Is.EqualTo(source.PositionY.Bits), path);
            Assert.That(staged.PositionZ.Bits, Is.EqualTo(source.PositionZ.Bits), path);
            Assert.That(staged.DirectionX.Bits, Is.EqualTo(source.DirectionX.Bits), path);
            Assert.That(staged.DirectionY.Bits, Is.EqualTo(source.DirectionY.Bits), path);
            Assert.That(staged.DirectionZ.Bits, Is.EqualTo(source.DirectionZ.Bits), path);
            Assert.That(staged.RawValue50, Is.EqualTo(source.RawValue50), path);
            Assert.That(staged.RawFlag64, Is.EqualTo(source.RawFlag64), path);
            Assert.That(staged.RawValue60.Bits, Is.EqualTo(source.RawValue60.Bits), path);
            Assert.That(staged.Mode, Is.EqualTo(source.Mode), path);
            Assert.That(staged.RawValue68.Bits, Is.EqualTo(source.RawValue68.Bits), path);
            Assert.That(staged.LinkedObjectToken, Is.EqualTo(source.LinkedObjectToken), path);
            Assert.That(staged.RawValue74, Is.EqualTo(source.RawValue74), path);
            Assert.That(staged.RawValue80, Is.EqualTo(source.RawValue80), path);
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
