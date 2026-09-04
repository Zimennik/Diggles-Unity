#nullable enable

namespace Diggles.Tests
{
    using System.IO;
    using Diggles.Runtime.Save;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveObjectBodyFieldLawTests
    {
        [Test]
        public void BeginningDwarvesCarryCurrentPartTextureVariants()
        {
            OriginalSaveObjectCollection objects = ReadBeginningObjects();
            int[][] expected =
            {
                new[] { 2, 2, 4, 0, 12 },
                new[] { 0, 2, 2, 0, 12 },
                new[] { 1, 1, 2, 0, 9 },
                new[] { 0, 1, 3, 0, 9 },
            };

            for (int index = 0; index < expected.Length; index++)
            {
                OriginalSaveObjectDocument dwarf = objects.FindById(index + 4) ??
                    throw new AssertionException($"Beginning dwarf {index + 4} is missing.");
                Assert.That(
                    dwarf.CommonState.RouteValues,
                    Is.EqualTo(expected[index]),
                    $"object {dwarf.ObjectId} N16");
            }
        }

        [Test]
        public void BeginningDwarvesCarryPerPlayerRenderProxyHandles()
        {
            OriginalSaveObjectCollection objects = ReadBeginningObjects();
            for (int index = 0; index < 4; index++)
            {
                OriginalSaveObjectDocument dwarf = objects.FindById(index + 4) ??
                    throw new AssertionException($"Beginning dwarf {index + 4} is missing.");
                Assert.That(
                    dwarf.CommonState.State37C,
                    Is.EqualTo(new[] { index, -1, -1, -1, -1, -1, -1, -1 }),
                    $"object {dwarf.ObjectId} N33");
            }
        }

        private static OriginalSaveObjectCollection ReadBeginningObjects()
        {
            string path = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "..",
                "..",
                "savework",
                "save_begining_save.sav"));
            Assume.That(File.Exists(path), $"Beginning-save fixture was not found at '{path}'.");
            OriginalSaveArchiveDocument document = new OriginalSaveArchiveCodec().Read(path);
            return document.Objects ??
                throw new AssertionException("The beginning save has no typed objects.");
        }
    }
}
