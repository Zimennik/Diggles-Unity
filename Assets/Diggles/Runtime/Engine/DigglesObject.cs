#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Mirror of the original engine's CObj — the base entity for everything
    // in the world that has a position, can move, can animate, and can be
    // assigned an action. Attached to every spawned marker that resolves to
    // a real.3db model.
    //
    // Per-frame:
    // 1. Tick the current action (if any).
    // 2. Advance position by velocity (matches CObj::Tick `pos += vel * dt`).
    //
    // The animator field lets actions trigger SetAnim (walkstart → walkloop
    // → walkstop chains, etc.). It's optional — purely static decoration
    // markers don't have one.
    [DisallowMultipleComponent]
    public sealed class DigglesObject : MonoBehaviour
    {
        // World-space velocity in Unity coordinates. Applied directly to
        // transform.position each frame.
        public Vector3 Velocity;

        [SerializeField] DigglesAnimator? animator;

        public DigglesAnimator? Animator => animator;
        public IDigglesAction? CurrentAction { get; private set; }
        public string ClassName = "";

        // Captured at spawn so WalkAction can derive correct per-character
        // yaw. Different characters spawn facing different world directions
        // (mann markerRot has TCL rY=270° → faces +X, hamster markerRot=
        // identity → faces -Z, etc.). Without these, walk yaw would apply
        // identical math to all characters and they'd end up rotated wrong.
        public Quaternion SpawnRotation { get; private set; }
        public Vector3 SpawnFaceWorld { get; private set; }  // XZ-projected unit vector

        void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<DigglesAnimator>();
            SpawnRotation = transform.rotation;

            // Spawn face direction = direction the model walks toward in
            // world at default rotation. Different.3db models author
            // different "forward" axes:
            // * mann/hamster/troll: motion offset is along mesh Y axis
            // and ends up vertical in world after spawn rotation chain
            // — these models' visual face is mesh +Z direction.
            // * raupe (worm): motion offset is along mesh X axis and
            // ends up horizontal in world — face direction IS the
            // motion direction.
            // Strategy: take loop anim's motionOffset as candidate face
            // direction in mesh. Transform to world. If horizontal, use
            // it. Otherwise fall back to mesh +Z direction.
            var modelChild = transform.Find("Model");
            Quaternion modelRot = modelChild != null
                ? modelChild.rotation
                : Quaternion.identity;

            Vector3 faceMesh = Vector3.zero;
            if (animator != null)
            {
                foreach (var clip in animator.Clips)
                {
                    if (clip == null || clip.MotionOffset.sqrMagnitude < 0.01f) continue;
                    // First clip with non-zero motion offset wins. Don't
                    // filter by name — different chars use different
                    // suffixes (mann.gehen_loop, raupe01.kriechen, etc.).
                    faceMesh = clip.MotionOffset.normalized;
                    break;
                }
            }

            Vector3 face;
            if (faceMesh.sqrMagnitude > 0.001f)
            {
                face = modelRot * faceMesh;
                face.y = 0f;
            }
            else
            {
                face = Vector3.zero;
            }
            // Vertical motion direction (motion offset projects to ~0 in
            // world XZ) — fall back to mesh +Z.
            if (face.sqrMagnitude < 0.01f)
            {
                face = modelRot * Vector3.forward;
                face.y = 0f;
            }
            float mag = face.magnitude;
            SpawnFaceWorld = mag > 0.001f ? face / mag : Vector3.forward;
        }

        // No per-frame Update. The former Update ticked CurrentAction + integrated Velocity at
        // render rate, but both are dead in Play — SetAction is never called (grep: only the definition
        // + an IDigglesAction.cs comment) so CurrentAction stays null, and the MonoBehaviour Velocity
        // field is never assigned (the live velocity lives on the Tcl entity, ent.Velocity, integrated
        // by the fixed-step sim in DigglesGameSim.Step phase 2b2). Keeping an empty Update only cost a
        // wasted native->managed dispatch per character every frame. If a C# action path is ever wanted,
        // drive it from the sim (the single fixed-step integrator), not a second render-rate one.
        public void SetAction(IDigglesAction? newAction)
        {
            CurrentAction?.End(this);
            CurrentAction = newAction;
            newAction?.Start(this);
        }

        // Face an absolute native yaw (radians, from get_anglexz / set_roty: front=0, left=π/2,
        // back=π, right=3π/2) — used by the cutscene `action <obj> rotate <ang>`. Drives the outer
        // transform with the same SpawnFaceWorld-relative math WalkAction uses, so a scripted turn
        // and a walk agree (they used to fight: walk set the outer, rotate set the inner Model →
        // dwarves in dialogue faced the wrong way). native ground dir (sin,cos in x,z) → world XZ
        // (native.x→world.X; native.z→world.Z scaled -0.5), faced about world up.
        public void FaceNativeAngle(float angRad) => transform.rotation = FaceRotationForNativeAngle(angRad);

        // Face a world direction (XZ) using the per-character SpawnFaceWorld basis — same math the
        // walk uses. For the cutscene elf (and any object the presenter steers directly).
        public void FaceWorldDirection(Vector3 worldDir)
        {
            Vector3 flat = new Vector3(worldDir.x, 0f, worldDir.z);
            if (flat.sqrMagnitude < 1e-5f) return;
            flat.Normalize();
            float targetYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            Vector3 face = SpawnFaceWorld;
            float spawnYaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.AngleAxis(targetYaw - spawnYaw, Vector3.up) * SpawnRotation;
        }

        // The live native yaw — the exact inverse of FaceRotationForNativeAngle. This is what
        // get_roty must report: the native has one yaw field which every mover writes and every
        // script reads; the port's movement executors rotate the transform directly, so the
        // entity-side Rotation.y went stale the moment a gnome walked (dig scripts branching on
        // `get_roty − digdir` decided snap-vs-turn on the pre-walk yaw, causing gnomes to flip
        // facing mid-dig). Current world facing f = Δq · SpawnFaceWorld with
        // Δq = rotation · SpawnRotation⁻¹; f ∝ (−sin a, ·, −0.5·cos a) ⇒ a = atan2(−f.x, −2·f.z).
        public float CurrentNativeYaw()
        {
            return NativeYawForRotation(transform.rotation);
        }

        public float NativeYawForRotation(Quaternion rotation)
        {
            Quaternion dq = rotation * Quaternion.Inverse(SpawnRotation);
            Vector3 f = dq * SpawnFaceWorld;
            float a = Mathf.Atan2(-f.x, -2f * f.z);
            return a < 0f ? a + 2f * Mathf.PI : a;   // scripts store roty in [0, 2π)
        }

        // The world rotation that faces an absolute native yaw (does not apply it) — so a turn
        // action can animate from the current rotation to this one over a turn clip.
        //
        // Wall poses agree with this shared named-angle map: the Tcl wall pose `set_roty 3.14`
        // (kletterstand wall-dig idle, z_procs.tcl:1707) wants π = "back" = world +Z = into the wall,
        // and ClimbAction's climb facing converges to the same +Z — the climber hangs back-to-camera
        // (the confirmed silhouette). An earlier air-facing (−Z) special case, derived from a
        // pose-centroid argument, produced a wrong-facing regression and was removed; the pose's
        // front bulge is kept off the rock by ClimbAction.WallStandoff instead.
        public Quaternion FaceRotationForNativeAngle(float angRad)
        {
            // native yaw → world heading. The -0.5 z-scale comes from NativeToLocal's z*0.5 + the
            // WorldRoot rotation; do not "simplify" to -cos. The wx sign is
            // negated to match the negated dx in get_anglexz/NativeAngleXz — together they keep
            // position-derived facing identical (the two negations cancel) while making the named
            // directions (front/back/left/right) land on the correct screen side (`rotate left` ⇒ left).
            float wx = -Mathf.Sin(angRad);
            float wz = -0.5f * Mathf.Cos(angRad);
            float targetYaw = Mathf.Atan2(wx, wz) * Mathf.Rad2Deg;
            Vector3 face = SpawnFaceWorld;
            float spawnYaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
            return Quaternion.AngleAxis(targetYaw - spawnYaw, Vector3.up) * SpawnRotation;
        }

        // Editor-time setup (called from BdbPmpWorldBuilder during import).
        public void Initialize(DigglesAnimator? attachedAnimator, string className)
        {
            animator = attachedAnimator;
            ClassName = className;
        }
    }
}
