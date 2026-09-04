#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveAdaptiveSoundMarkerDocumentTests
    {
        [Test]
        public void EveryCurrentSaveHasValidTypedAdaptiveSoundMarkers()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int totalMarkers = 0;
            int positionalMarkers = 0;
            int objectMarkers = 0;
            int danglingObjectMarkers = 0;
            var themes = new Dictionary<string, int>(StringComparer.Ordinal);
            var ranges = new Dictionary<int, int>();
            var countDistribution = new Dictionary<int, int>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveAdaptiveSoundMarkerDocument markers =
                    document.AdaptiveSoundMarkers ?? throw new AssertionException(
                        $"{path} has no typed adaptive-sound marker registry.");
                OriginalSaveLoadPlan plan = new OriginalSaveLoadPlanner().Prepare(document);
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(plan);
                Assert.That(runtime.AdaptiveSoundMarkers.Markers.Count,
                    Is.EqualTo(markers.Markers.Count));
                Increment(countDistribution, markers.Markers.Count);

                for (int index = 0; index < markers.Markers.Count; index++)
                {
                    OriginalSaveAdaptiveSoundMarkerRecord expected = markers.Markers[index];
                    OriginalSaveRuntimeAdaptiveSoundMarker actual =
                        runtime.AdaptiveSoundMarkers.Markers[index];
                    Assert.That(actual.ObjectId, Is.EqualTo(expected.ObjectId));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.PositionX),
                        Is.EqualTo(expected.PositionX.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.PositionY),
                        Is.EqualTo(expected.PositionY.Bits));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.PositionZ),
                        Is.EqualTo(expected.PositionZ.Bits));
                    Assert.That(actual.Theme, Is.EqualTo(expected.Theme.Text));
                    Assert.That(BitConverter.SingleToInt32Bits(actual.Range),
                        Is.EqualTo(expected.Range.Bits));

                    Increment(themes, expected.Theme.Text);
                    Increment(ranges, expected.Range.Bits);
                    if (expected.IsObjectBound)
                    {
                        objectMarkers++;
                        if (document.Objects == null ||
                            !document.Objects.Any(obj => obj.ObjectId == expected.ObjectId))
                        {
                            danglingObjectMarkers++;
                        }
                    }
                    else
                    {
                        positionalMarkers++;
                    }
                    totalMarkers++;
                }
            }

            TestContext.WriteLine(
                $"files={paths.Length}, markers={totalMarkers}, positional={positionalMarkers}, " +
                $"object={objectMarkers}, danglingObject={danglingObjectMarkers}");
            foreach (KeyValuePair<int, int> pair in countDistribution)
                TestContext.WriteLine($"marker count {pair.Key}: {pair.Value}");
            foreach (KeyValuePair<string, int> pair in themes)
                TestContext.WriteLine($"theme '{pair.Key}': {pair.Value}");
            foreach (KeyValuePair<int, int> pair in ranges)
            {
                TestContext.WriteLine(
                    $"range {BitConverter.Int32BitsToSingle(pair.Key):R} " +
                    $"(0x{unchecked((uint)pair.Key):X8}): {pair.Value}");
            }
        }

        [Test]
        public void AdaptiveSoundCommandsKeepPositionalAndObjectMarkerIdentitySeparate()
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            var markerClass = new DigglesClass { Name = "MarkerObject", Kind = "dummy" };
            context.Classes.Add(markerClass.Name, markerClass);
            var first = new DigglesTclEntity { Class = markerClass };
            var second = new DigglesTclEntity { Class = markerClass };
            context.RegisterEntity(first);
            context.RegisterEntity(second);

            interpreter.Eval("adaptive_sound marker cave {10 20 30} 75");
            interpreter.Eval($"adaptive_sound markerobj fight {first.Handle}");
            interpreter.Eval($"adaptive_sound markerobj danger {first.Handle}");
            interpreter.Eval($"adaptive_sound markerobj other {second.Handle}");

            Assert.That(context.Audio, Is.Not.Null);
            Assert.That(context.Audio!.MusicMarkers, Has.Count.EqualTo(4));
            AudioState.MusicMarker positional = context.Audio.MusicMarkers[0];
            Assert.That(positional.ObjectId, Is.EqualTo(-1));
            Assert.That(positional.Theme, Is.EqualTo("cave"));
            Assert.That(positional.Pos, Is.EqualTo(new Vector3(10f, 20f, 30f)));
            Assert.That(positional.Radius, Is.EqualTo(75f));

            AudioState.MusicMarker objectMarker = context.Audio.MusicMarkers[1];
            Assert.That(objectMarker.ObjectId, Is.EqualTo(first.Handle));
            Assert.That(objectMarker.Theme, Is.EqualTo("fight"));
            Assert.That(objectMarker.Pos, Is.EqualTo(Vector3.zero));
            Assert.That(objectMarker.Radius, Is.EqualTo(50f));

            interpreter.Eval($"adaptive_sound delmarkerobj {first.Handle}");
            Assert.That(context.Audio.MusicMarkers, Has.Count.EqualTo(2));
            Assert.That(context.Audio.MusicMarkers[0].ObjectId, Is.EqualTo(-1));
            Assert.That(context.Audio.MusicMarkers[1].ObjectId, Is.EqualTo(second.Handle));
        }

        private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }
    }
}
