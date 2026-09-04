#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using NUnit.Framework;

    public sealed class OriginalSaveElfDocumentTests
    {
        [Test]
        public void ReadsEveryAvailableCurrentSaveIntoTypedElfState()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var modelSetIds = new Dictionary<int, int>();
            var modelAnimationIds = new Dictionary<int, int>();
            var state64Values = new Dictionary<int, int>();
            var modes = new Dictionary<int, int>();
            var currentAnimations = new Dictionary<int, int>();
            var requestedAnimations = new Dictionary<int, int>();
            var particlePairs = new Dictionary<string, int>();
            var lookAtObjectIds = new Dictionary<int, int>();
            var resolvedMeshes = new Dictionary<string, int>();
            int visibleAnimationStates = 0;
            int lookAtReferences = 0;
            var codec = new OriginalSaveArchiveCodec();
            var models = new DigglesModelLibrary(OriginalDataTestFixture.DataRoot);
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveElfDocument elf = document.Elf ??
                    throw new AssertionException($"{path} has no typed CElf document.");
                Assert.That(document.Metadata.Logic.ElfStateEndOffset -
                    document.Metadata.Logic.ElfStateOffset, Is.EqualTo(78), path);
                AssertFinite(elf.FrameAlpha.AsSingle, "frame alpha", path);
                AssertFinite(elf.Position.X, "position X", path);
                AssertFinite(elf.Position.Y, "position Y", path);
                AssertFinite(elf.Position.Z, "position Z", path);
                AssertFinite(elf.LeanA.AsSingle, "lean A", path);
                AssertFinite(elf.LeanB.AsSingle, "lean B", path);
                AssertFinite(elf.NativeState64.AsSingle, "native +0x64", path);
                AssertFinite(elf.Target.X, "target X", path);
                AssertFinite(elf.Target.Y, "target Y", path);
                AssertFinite(elf.Target.Z, "target Z", path);

                Increment(modelSetIds, elf.NativeModelDatabaseId);
                Increment(modelAnimationIds, elf.NativeMeshId);
                Increment(state64Values, elf.NativeState64.Bits);
                Increment(modes, elf.Mode);
                Increment(currentAnimations, elf.CurrentRegisteredAnimation);
                Increment(requestedAnimations, elf.RequestedRegisteredAnimation);
                Increment(particlePairs,
                    $"{elf.FirstParticleSourceHandle},{elf.SecondParticleSourceHandle}");
                if (elf.CurrentRegisteredAnimation >= 0) visibleAnimationStates++;
                if (elf.LookAtObjectId >= 0)
                {
                    lookAtReferences++;
                    Increment(lookAtObjectIds, elf.LookAtObjectId);
                }
                if (elf.NativeMeshId >= 0)
                {
                    Assert.That(models.TryResolveNativeMesh(
                        "elfe",
                        elf.NativeMeshId,
                        out string clip,
                        out int frame), Is.True, path);
                    Increment(resolvedMeshes, $"{elf.NativeMeshId}:{clip}[{frame}]");
                }
            }

            Print("modelSet", modelSetIds);
            Print("modelAnim", modelAnimationIds);
            Print("state64", state64Values, value => $"0x{unchecked((uint)value):X8}");
            Print("mode", modes);
            Print("currentAnim", currentAnimations);
            Print("requestedAnim", requestedAnimations);
            Print("particles", particlePairs);
            Print("lookAt", lookAtObjectIds);
            Print("mesh", resolvedMeshes);
            TestContext.Out.WriteLine(
                $"activeAnimations={visibleAnimationStates}; lookAtReferences={lookAtReferences}");
            Assert.That(paths.Length, Is.GreaterThanOrEqualTo(98));
        }

        private static void AssertFinite(float value, string label, string path) =>
            Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False,
                $"{path}: non-finite CElf {label}");

        private static void Increment<TKey>(Dictionary<TKey, int> values, TKey key)
            where TKey : notnull
        {
            values.TryGetValue(key, out int count);
            values[key] = count + 1;
        }

        private static void Print(
            string label,
            Dictionary<int, int> values,
            Func<int, string>? format = null)
        {
            foreach (KeyValuePair<int, int> pair in values.OrderBy(pair => pair.Key))
                TestContext.Out.WriteLine(
                    $"{label}={format?.Invoke(pair.Key) ?? pair.Key.ToString()} count={pair.Value}");
        }

        private static void Print(
            string label,
            Dictionary<string, int> values)
        {
            foreach (KeyValuePair<string, int> pair in values.OrderBy(pair => pair.Key))
                TestContext.Out.WriteLine($"{label}={pair.Key} count={pair.Value}");
        }
    }
}
