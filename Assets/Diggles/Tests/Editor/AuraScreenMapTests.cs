#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;

    // AuraBandRenderer.ScreenAtDepthMap must match cam.ScreenToWorldPoint exactly per vertex,
    // just with the camera state cached per draw. Regression: an affine basis built by
    // differencing two unprojections 1 px apart lost float precision against large world
    // coordinates and displaced the band. The camera sits far from the origin here so any
    // cancellation-style error is visible.
    public sealed class AuraScreenMapTests
    {
        GameObject? _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _go = null;
        }

        Camera MakeCamera(bool ortho)
        {
            _go = new GameObject("AuraMapCam");
            var cam = _go.AddComponent<Camera>();
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            cam.fieldOfView = 27.5f;                          // Diggles-scale narrow FOV
            cam.orthographic = ortho;
            if (ortho) cam.orthographicSize = 12f;
            // Far from the origin + tilted — the regression only shows at large world coords.
            cam.transform.position = new Vector3(512.7f, 331.9f, -407.3f);
            cam.transform.rotation = Quaternion.Euler(38f, 217f, 0f);
            return cam;
        }

        static void AssertParity(Camera cam, float tol)
        {
            float depth = cam.nearClipPlane + 0.05f;         // the renderer's emit depth
            var map = new AuraBandRenderer.ScreenAtDepthMap(cam, depth);
            float w = cam.pixelWidth, h = cam.pixelHeight;
            (float x, float y)[] pts =
            {
                (0f, 0f), (w, 0f), (0f, h), (w, h), (w * 0.5f, h * 0.5f),
                (w * 0.31f, h * 0.73f), (w * 0.97f, h * 0.05f), (1f, 1f),
            };
            foreach (var (x, y) in pts)
            {
                Vector3 expect = cam.ScreenToWorldPoint(new Vector3(x, y, depth));
                Vector3 got = map.Map(x, y);
                Assert.That((got - expect).magnitude, Is.LessThan(tol),
                    $"screen ({x},{y}): map {got:F6} != ScreenToWorldPoint {expect:F6} — " +
                    "the per-vertex screen→world math must be exact");
            }
        }

        [Test]
        public void PerspectiveCamera_MapMatchesScreenToWorldPoint()
        {
            var cam = MakeCamera(ortho: false);
            // 2e-3 world units at a ~700-unit camera position = pure float-rounding parity;
            // the affine-differencing bug erred ~25× more than this at screen edges.
            AssertParity(cam, 2e-3f);
        }

        [Test]
        public void OrthographicCamera_FallsBackToTheNativeCall()
        {
            var cam = MakeCamera(ortho: true);
            AssertParity(cam, 1e-4f);                         // same native call — exact
        }
    }
}
