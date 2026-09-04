#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // FogOfWarPresenter temporal smoothing (a deliberate port deviation): display texels chase
    // the decoded sim targets over SmoothSeconds instead of snapping — black reveals fade in,
    // the gray veil fades continuously. Init must snap (no world fade-in from black at load).
    public sealed class FogOfWarPresenterTests
    {
        GameObject? _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        (FogOfWarState fog, FogOfWarPresenter pr) Make()
        {
            var fog = new FogOfWarState(16, 16, (x, y) => 0);
            _go = new GameObject("FowPresenterHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);
            return (fog, pr);
        }

        [Test]
        public void Init_SnapsToCurrentState_NoFadeIn()
        {
            var fog = new FogOfWarState(16, 16, (x, y) => 0);
            fog.RemoveFogBoxNoCollision(8 * 8, 8 * 8, 2 * 8, 2 * 8, 0);   // pre-revealed pocket
            _go = new GameObject("FowPresenterHost");
            var pr = _go.AddComponent<FogOfWarPresenter>();
            pr.Init(fog);
            Assert.That(pr.DisplayAt(8, 8).g, Is.EqualTo(255), "revealed BEFORE Init → snapped visible");
            Assert.That(pr.DisplayAt(2, 2).g, Is.EqualTo(0), "unseen stays black");
        }

        [Test]
        public void Reveal_FadesInOverSmoothSeconds()
        {
            var (fog, pr) = Make();
            Assert.That(pr.DisplayAt(8, 8).g, Is.EqualTo(0));

            fog.RemoveFogBoxNoCollision(8 * 8, 8 * 8, 2 * 8, 2 * 8, 0);   // instant sim reveal
            pr.Pump(FogOfWarPresenter.SmoothSeconds * 0.5f);
            var mid = pr.DisplayAt(8, 8);
            Assert.That(mid.g, Is.GreaterThan(0), "fade started");
            Assert.That(mid.g, Is.LessThan(255), "not snapped — mid-transition");

            pr.Pump(FogOfWarPresenter.SmoothSeconds);                     // finish the swing
            Assert.That(pr.DisplayAt(8, 8).g, Is.EqualTo(255), "fully revealed after the swing");
            // R = the gray-veil strength (decoded fogFactor): 0 when fully visible (byte ≥
            // 0xE3), 255 at the resting gray floor.
            Assert.That(pr.DisplayAt(8, 8).r, Is.EqualTo(0), "no gray veil while fully visible");
        }

        [Test]
        public void FadeToGray_DisplayFollowsContinuously()
        {
            var (fog, pr) = Make();
            fog.RemoveFogBoxNoCollision(8 * 8, 8 * 8, 8, 8, 0);
            pr.Pump(10f);                                                  // settle the reveal
            Assert.That(pr.DisplayAt(8, 8).r, Is.EqualTo(0), "fully visible — no veil");

            // Decay the byte into the visible ramp (0xFF − 19·4 = 0xB3): the decoded veil
            // target becomes 255 − 2·⌊(0xB3−0x80)·1.3⌋ = 123; the display chases it smoothly.
            for (int i = 0; i < 19; i++) { fog.BeginUpdate(0); fog.EndUpdate(); }
            pr.Pump(0.05f);
            var mid = pr.DisplayAt(8, 8);
            Assert.That(mid.r, Is.GreaterThan(0).And.LessThan(123), "veil growing smoothly");
            pr.Pump(10f);
            Assert.That(pr.DisplayAt(8, 8).r, Is.EqualTo(123), "settled on the exact ramp value");
            Assert.That(pr.DisplayAt(8, 8).g, Is.EqualTo(255), "explored — not black");
        }
    }
}
