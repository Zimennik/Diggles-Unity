#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class AudioStateAdaptiveSoundMarkerTests
    {
        [Test]
        public void ArmThemeFromMarkers_ArmsNearestRestoredMarkerInsideGroundPlaneRadius()
        {
            var audio = new AudioState();
            audio.RestoreMusicMarkers(new[]
            {
                new AudioState.MusicMarker
                {
                    ObjectId = -1,
                    Theme = "outside",
                    Pos = new Vector3(280.5f, 14f, 15f),
                    Radius = 0.25f,
                },
                new AudioState.MusicMarker
                {
                    ObjectId = -1,
                    Theme = "metall",
                    Pos = new Vector3(300f, 14f, -500f),
                    Radius = 1000f,
                },
                new AudioState.MusicMarker
                {
                    ObjectId = -1,
                    Theme = "cave",
                    Pos = new Vector3(282f, 14f, 500f),
                    Radius = 1000f,
                },
            });

            bool armed = audio.ArmThemeFromMarkers(new Vector3(280f, 14f, -1000f));

            Assert.That(armed, Is.True);
            Assert.That(audio.MusicPlaying, Is.True);
            Assert.That(audio.CurrentTheme, Is.EqualTo("cave"));
            Assert.That(audio.PrimaryTheme, Is.EqualTo("cave"));
            Assert.That(audio.MusicMarkers, Has.Count.EqualTo(3));
        }

        [Test]
        public void ArmThemeFromMarkers_WhenCalledTwice_ArmsOnceWithoutDuplicatingState()
        {
            var audio = new AudioState();
            audio.AddMarker("cave", new Vector3(280f, 14f, 15f), 1000f);

            bool firstArm = audio.ArmThemeFromMarkers(new Vector3(246.7f, 28.8f, 0f));
            bool secondArm = audio.ArmThemeFromMarkers(new Vector3(246.7f, 28.8f, 999f));

            Assert.That(firstArm, Is.True);
            Assert.That(secondArm, Is.False);
            Assert.That(audio.MusicPlaying, Is.True);
            Assert.That(audio.CurrentTheme, Is.EqualTo("cave"));
            Assert.That(audio.PrimaryTheme, Is.EqualTo("cave"));
            Assert.That(audio.MusicMarkers, Has.Count.EqualTo(1));
        }
    }
}
