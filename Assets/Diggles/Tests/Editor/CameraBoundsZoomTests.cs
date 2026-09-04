#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class CameraBoundsZoomTests
    {
        [Test]
        public void ClampFocusToBounds_InteriorFocus_PassesThroughUnchanged()
        {
            var focus = new Vector2(50f, 40f);
            Vector2 result = DigglesCameraController.ClampFocusToBounds(
                focus,
                Vector2.zero,
                new Vector2(100f, 80f),
                2f,
                DigglesCameraController.MinZoom,
                2f,
                3f);

            Assert.That(result.x, Is.EqualTo(focus.x).Within(1e-4f));
            Assert.That(result.y, Is.EqualTo(focus.y).Within(1e-4f));
        }

        [TestCase(-100f, -100f)]
        [TestCase(-100f, 100f)]
        [TestCase(100f, -100f)]
        [TestCase(100f, 100f)]
        public void ClampFocusToBounds_OutOfBoundsCorner_EndsInsideInset(float focusX, float focusY)
        {
            var min = Vector2.zero;
            var max = new Vector2(20f, 12f);
            const float margin = 3f;
            Vector2 result = DigglesCameraController.ClampFocusToBounds(
                new Vector2(focusX, focusY),
                min,
                max,
                2f,
                DigglesCameraController.MinZoom,
                2f,
                1f);

            Assert.That(result.x, Is.GreaterThan(min.x));
            Assert.That(result.x, Is.LessThan(max.x));
            Assert.That(result.y, Is.GreaterThan(min.y));
            Assert.That(result.y, Is.LessThan(max.y));
            Assert.That(result.x, Is.InRange(min.x + margin, max.x - margin));
            Assert.That(result.y, Is.InRange(min.y + margin, max.y - margin));
        }

        [Test]
        public void ClampFocusToBounds_DegenerateAxes_PassThroughUnchanged()
        {
            var focus = new Vector2(123f, -456f);
            Vector2 result = DigglesCameraController.ClampFocusToBounds(
                focus,
                new Vector2(5f, 10f),
                new Vector2(5f, 0f),
                2.5f,
                DigglesCameraController.MinZoom,
                1f,
                1f);

            Assert.That(result.x, Is.EqualTo(focus.x).Within(1e-4f));
            Assert.That(result.y, Is.EqualTo(focus.y).Within(1e-4f));
        }

        [Test]
        public void ClampFocusToBounds_CollapsedInset_ReturnsAxisMidpoints()
        {
            Vector2 result = DigglesCameraController.ClampFocusToBounds(
                new Vector2(-100f, 100f),
                new Vector2(0f, -10f),
                new Vector2(10f, 10f),
                DigglesCameraController.MinZoom,
                DigglesCameraController.MinZoom,
                11f,
                0f);

            Assert.That(result.x, Is.EqualTo(5f).Within(1e-4f));
            Assert.That(result.y, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void StepZoomSmoothing_RepeatedSteps_SettleExactlyAtTarget()
        {
            const float target = 1f;
            float current = 2.5f;

            for (int i = 0; i < 240; i++)
                current = DigglesCameraController.StepZoomSmoothing(current, target, 0.1f, 1f / 60f);

            Assert.That(current, Is.EqualTo(target));
        }

        [Test]
        public void StepZoomSmoothing_NonPositiveSmoothTimeOrDeltaTime_IsInstant()
        {
            const float target = 1.75f;

            Assert.That(
                DigglesCameraController.StepZoomSmoothing(1f, target, 0f, 1f / 60f),
                Is.EqualTo(target));
            Assert.That(
                DigglesCameraController.StepZoomSmoothing(1f, target, -0.1f, 1f / 60f),
                Is.EqualTo(target));
            Assert.That(
                DigglesCameraController.StepZoomSmoothing(1f, target, 0.1f, 0f),
                Is.EqualTo(target));
            Assert.That(
                DigglesCameraController.StepZoomSmoothing(1f, target, 0.1f, -1f / 60f),
                Is.EqualTo(target));
        }
    }
}
