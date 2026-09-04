#nullable enable

namespace Diggles.Tests
{
    using System.IO;
    using System.IO.Compression;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveTrailingPaddingDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasCanonicalZeroTrailingPadding()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveTrailingPaddingDocument padding = codec.Read(path).TrailingPadding
                    ?? throw new AssertionException($"{path} has no typed trailing padding.");
                Assert.That(padding.Length,
                    Is.EqualTo(OriginalSaveTrailingPaddingDocument.SerializedLength), path);
                Assert.That(padding.IsCanonicalZeroPadding, Is.True, path);
                Assert.That(padding.CopyBytes(), Is.All.Zero, path);
            }

            TestContext.WriteLine($"files={paths.Length}, canonicalZeroTails={paths.Length}");
        }

        [Test]
        public void ReaderRetainsNonCanonicalPaddingBecauseNativeLoaderIgnoresIt()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot,
                "Gamesave",
                "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var codec = new OriginalSaveArchiveCodec();
            OriginalSaveArchiveDocument baseline = codec.Read(path);
            byte[] payload = baseline.CopyPayload();
            int start = checked((int)baseline.Metadata.Logic!.FinalBlockOffset);
            payload[start] = 0x5A;
            payload[start + OriginalSaveTrailingPaddingDocument.SerializedLength - 1] = 0xA5;

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
                gzip.Write(payload, 0, payload.Length);
            compressed.Position = 0;

            OriginalSaveTrailingPaddingDocument padding = codec.Read(compressed).TrailingPadding!;
            byte[] retained = padding.CopyBytes();
            Assert.That(padding.IsCanonicalZeroPadding, Is.False);
            Assert.That(retained[0], Is.EqualTo(0x5A));
            Assert.That(retained[^1], Is.EqualTo(0xA5));
        }

        [Test]
        public void CanonicalPaddingFactoryProducesExactZeroBlock()
        {
            byte[] padding = OriginalSaveTrailingPaddingDocument.CreateCanonicalBytes();
            Assert.That(padding.Length,
                Is.EqualTo(OriginalSaveTrailingPaddingDocument.SerializedLength));
            Assert.That(padding, Is.All.Zero);
        }
    }
}
