#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;

    // Authored-deco rotation mapping law.
    //
    // Oracle: the original renders the Fuchsschwanz (handsaw) of the herbalist camp
    // (urw_unq_vodo_001_a.tcl:407, rot "5.01099 2.69998 1.11144") stuck in the front support
    // beam (Dummy_Multi_Holzstuetze_f, same template:413, native x=29.163).
    //
    // Native law (decompile FUN_004d2420 link→world→screen; FUN_005cbf70 offset→world — both
    // consume the +0x40/44/48 rotation fields identically):
    // M = Ry(ry) · Rx(rx) · Rz(rz), Z innermost, each axis matrix the transpose of
    // Unity's numeric same-axis matrix ⇒ exactly Quaternion.Euler(−rx, −ry, −rz).
    // These tests pin MarkerRotFromNative to that law via a verbatim reimplementation of
    // the decompiled rotation blocks.
    public sealed class AuthoredRotationLawTests
    {
        // The saw's authored bits — the anchor for the general law.
        static readonly Vector3 SawRot = new Vector3(5.01099f, 2.69998f, 1.11144f);
        const float SawNativeX = 30.355f;   // urw_unq_vodo_001_a.tcl:407
        const float BeamNativeX = 29.163f;  // Dummy_Multi_Holzstuetze_f,:413

        // FUN_004d2420 rotation blocks, verbatim (native frame; v = (x, y, z) native):
        // rz: fVar28 = x·cos+y·sin; fVar34 = y·cos−x·sin
        // rx: fVar25 = z·cos−y'·sin; y'' = y'·cos+z·sin
        // ry: xOut = x'·cos−z''·sin; zOut = z''·cos+x'·sin
        static Vector3 NativeRotate(Vector3 rot, Vector3 v)
        {
            float sz = Mathf.Sin(rot.z), cz = Mathf.Cos(rot.z);
            float x1 = v.x * cz + v.y * sz;
            float y1 = v.y * cz - v.x * sz;
            float sx = Mathf.Sin(rot.x), cx = Mathf.Cos(rot.x);
            float z2 = v.z * cx - y1 * sx;
            float y2 = y1 * cx + v.z * sx;
            float sy = Mathf.Sin(rot.y), cy = Mathf.Cos(rot.y);
            float xOut = x1 * cy - z2 * sy;
            float zOut = z2 * cy + x1 * sy;
            return new Vector3(xOut, y2, zOut);
        }

        static void AssertMatchesNativeLaw(Vector3 rotRad, float tol = 1e-4f)
        {
            Quaternion q = RuntimeWorldSpawner.MarkerRotFromNative(rotRad);
            foreach (var basis in new[] { Vector3.right, Vector3.up, Vector3.forward })
            {
                Vector3 expected = NativeRotate(rotRad, basis);
                Vector3 actual = q * basis;   // numeric action == native-frame action
                Assert.That(actual.x, Is.EqualTo(expected.x).Within(tol),
                    $"rot={rotRad} basis={basis} x");
                Assert.That(actual.y, Is.EqualTo(expected.y).Within(tol),
                    $"rot={rotRad} basis={basis} y");
                Assert.That(actual.z, Is.EqualTo(expected.z).Within(tol),
                    $"rot={rotRad} basis={basis} z");
            }
        }

        [Test]
        public void MarkerRot_MatchesDecompiledLaw_ForSawAuthoredBits()
            => AssertMatchesNativeLaw(SawRot);

        // General mapping-law table: yaw quarters, single-axis pitch/roll, and the other
        // full-euler props of the herbalist camp template (urw_unq_vodo_001_a.tcl).
        [TestCase(0f, 0f, 0f)]
        [TestCase(0f, 1.5708f, 0f)]
        [TestCase(0f, 3.14159f, 0f)]
        [TestCase(0f, 4.71239f, 0f)]
        [TestCase(1.5708f, 0f, 0f)]           // pure pitch (Dummy_Wand_laub_cc = 4.71239 0 0)
        [TestCase(0f, 0f, 1.5708f)]           // pure roll
        [TestCase(0f, 0f, 5.72319f)]          // Dummy_Wurzel_05_b:552
        [TestCase(0f, 0.319999f, 5.72319f)]   // Dummy_Balken_e:196 (leaning beam)
        [TestCase(0.959995f, 5.56319f, 0f)]   // Dummy_Seule_o_e:268
        [TestCase(1.27602f, 1.82102f, 5.03242f)] // Dummy_Seule_m_g:522
        [TestCase(0f, 5.60319f, 4.68319f)]    // Dummy_Strohballen_c:383
        public void MarkerRot_MatchesDecompiledLaw_Table(float rx, float ry, float rz)
            => AssertMatchesNativeLaw(new Vector3(rx, ry, rz));

        // Yaw-only rotations must keep the movers/facing convention bit-for-bit:
        // Euler(0, −ry, 0). Characters and most props are yaw-only, so the law must be a
        // strict no-op for them.
        [TestCase(0f)]
        [TestCase(1.5708f)]
        [TestCase(3.14159f)]
        [TestCase(4.71239f)]
        public void MarkerRot_YawOnly_UnchangedFromValidatedConvention(float ry)
        {
            Quaternion q = RuntimeWorldSpawner.MarkerRotFromNative(new Vector3(0f, ry, 0f));
            Quaternion validated = Quaternion.Euler(0f, -ry * Mathf.Rad2Deg, 0f);
            Assert.That(Quaternion.Angle(q, validated), Is.LessThan(1e-3f));
        }

        // The anchor scenario end-to-end: compose the inner-Model rotation the spawner builds
        // (Q_x(−90)·markerRot) and check the saw's blade axis. Meshes are Y-down model space,
        // the fuchsschwanz blade extends along model local Y (~1 world unit at ModelScale).
        // The correct law points the blade at native −X: from the authored pivot x=30.355
        // into the support-beam column at x=29.163. The old law (negate yaw only) pointed it
        // at native +X, about a metre to the other side of the beam.
        [Test]
        public void Saw_BladeAxis_PointsIntoSupportBeamColumn()
        {
            Quaternion inner = Quaternion.Euler(-90f, 0f, 0f)
 * RuntimeWorldSpawner.MarkerRotFromNative(SawRot);
            // WorldRoot-local direction of the model's local +Y (blade axis).
            Vector3 bladeLocal = inner * Vector3.up;
            // native x == WorldRoot-local x (NativeToLocal is x-identity).
            Assert.That(bladeLocal.x, Is.LessThan(-0.9f),
                "blade must point toward native −X (screen left, into the beam)");
            float tipX = SawNativeX + bladeLocal.x * 1.0f;   // ~1 wu blade length
            Assert.That(tipX, Is.EqualTo(BeamNativeX).Within(0.45f),
                "blade tip must land within the support-beam column");

            // The pre-fix law for contrast: yaw-only negation swings the blade to +X.
            Quaternion old = Quaternion.Euler(-90f, 0f, 0f)
 * Quaternion.Euler(SawRot.x * Mathf.Rad2Deg,
                                  -SawRot.y * Mathf.Rad2Deg,
                                   SawRot.z * Mathf.Rad2Deg);
            Assert.That((old * Vector3.up).x, Is.GreaterThan(0.9f));
        }

        // π-terms are sign-invariant (Rz(π) == Rz(−π)): the kaserne walls authored with
        // rot "0 0 3.14159" never looked wrong under the old law and must not move now.
        [Test]
        public void PiRoll_IsSignInvariant_NoVisibleChange()
        {
            Quaternion fixedLaw = RuntimeWorldSpawner.MarkerRotFromNative(new Vector3(0f, 0f, Mathf.PI));
            Quaternion oldLaw = Quaternion.Euler(0f, 0f, Mathf.PI * Mathf.Rad2Deg);
            Assert.That(Quaternion.Angle(fixedLaw, oldLaw), Is.LessThan(1e-3f));
        }
    }
}
