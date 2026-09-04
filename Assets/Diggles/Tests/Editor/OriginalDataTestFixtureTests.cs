#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.IO;
    using NUnit.Framework;

    public sealed class OriginalDataTestFixtureTests
    {
        [Test]
        public void ResolveDataRoot_WithoutConfiguration_ThrowsActionableError()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => OriginalDataTestFixture.ResolveDataRoot(null));

            StringAssert.Contains(OriginalDataTestFixture.EnvironmentVariableName, exception!.Message);
        }

        [Test]
        public void ResolveDataRoot_WithMissingDirectory_ThrowsDirectoryNotFound()
        {
            var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            Assert.Throws<DirectoryNotFoundException>(
                () => OriginalDataTestFixture.ResolveDataRoot(missingPath));
        }

        [Test]
        public void ResolveDataRoot_WithRequiredFiles_ReturnsNormalizedAbsolutePath()
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), $"diggles-data-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(dataRoot, "Scripts"));
            File.WriteAllBytes(Path.Combine(dataRoot, "BlockDB2.BDB"), Array.Empty<byte>());

            try
            {
                var resolved = OriginalDataTestFixture.ResolveDataRoot(dataRoot);

                Assert.AreEqual(Path.GetFullPath(dataRoot).Replace('\\', '/'), resolved);
            }
            finally
            {
                Directory.Delete(dataRoot, true);
            }
        }
    }
}
