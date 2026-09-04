#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // The Diggles camera, reproduced from the original engine (RE'd from Diggles.exe
    // FUN_00475ee0 eye/at build + FUN_0044dee0 projection + FUN_0044dc80 LookAt).
    //
    // The original is a perspective camera that orbits a ground look-at point; zoom sets the
    // distance, dx/dy tilt & rotate it, projection has half-FOV(horizontal) = 0.3*aspect rad.
    //
    // Coordinate convention (matches how the rendered world + the proven cutscene camera work):
    // the focus is mapped to world via RuntimeWorldSpawner.NativeToLocal (x, z*0.5+7, -y) under
    // the WorldRoot (Euler 270,0,0), and the camera orbits in world space with up = world +Y.
    // (Verified against the working cutscene-camera logs: focus (231.5,29.7,12.8) → world
    // (231.5,-29.5,-13.4), camPos = focusWorld + dir*dist, euler (35,0,0).) Building the eye in
    // native space + a root-mapped up gave a "camera from below" bug — do not do that.
    //
    // This is one controller for both worlds: gameplay user input and scripted/cutscene moves
    // write the same state (FocusX/FocusY/FocusZ/Zoom/Dx/Dy). Set Mode = Scripted to let an
    // external driver (the cutscene presenter) own the state; Gameplay reads user input.
    [DisallowMultipleComponent]
    public sealed class DigglesCameraController : MonoBehaviour
    {
        public enum CameraMode { Gameplay, Scripted }

        [Header("Wiring")]
        public Camera? TargetCamera;
        [Tooltip("The campaign WorldRoot (Euler 270,0,0). Camera poses are computed in its local space.")]
        public Transform? WorldRoot;

        [Header("Mode")]
        public CameraMode Mode = CameraMode.Gameplay;
        // When this returns true (e.g. a cutscene is driving the camera through the
        // presenter), the controller yields: it neither reads input nor poses the camera.
        // Lets the gameplay controller coexist with the cutscene presenter until the
        // presenter is migrated onto this controller too.
        public System.Func<bool>? Suppress;

        // ---- Live camera state (the raw set_view params) -----------------------------
        [Header("Camera state (native ground target + zoom + angle)")]
        public float FocusX = 246.34f;      // ground target X (native, east)
        public float FocusY = 29.12f;       // ground target Y (native, depth)
        [Range(MinZoom, MaxZoom)] public float Zoom = 1.35f;
        [Tooltip("Polar angle from VERTICAL (radians); camera is dx off straight-down. Normal ~ -0.2..-0.25.")]
        public float Dx = -0.25f;
        [Tooltip("Azimuth (radians); rotates the camera around the focus.")]
        public float Dy = 0f;

        // Extra eye offset (world space) for screenvibe/shake, set by the cutscene presenter.
        [System.NonSerialized] public Vector3 ShakeOffset;

        // ---- Engine constants ----
        const float AtHeight = 14f;         // look-at native height (LookAt Z const 0x41600000)
        const float NearPlane = 0.5f;       // zn (DAT_0073708c)
        const float FarPlane = 180f;        // zf (DAT_0073763c) — M[10]=zf/(zf-zn)=1.00279 confirms 180
        const float HalfFovPerAspect = 0.3f; // horizontal half-FOV (rad) = 0.3 * aspect
        // The camera-update caller (FUN_006b6900) clamps the requested zoom to [1.0, 2.5] — verified
        // by disasm and by the camcalib live dump (min 1.0, max 2.5). So R = zoom*16-10 ∈ [6, 30] > 0.
        public const float MinZoom = 1.0f;
        public const float MaxZoom = 2.5f;
        // set_view's own clamp (FUN_00629fe0) — the wider range cutscenes use (gameplay caps tighter).
        public const float CutsceneMinZoom = 0.5f;
        public const float CutsceneMaxZoom = 6.0f;

        [Header("Distance (RE: R = zoom*16 - 10, confirmed by RV_VIEW)")]
        public float DistPerZoom = 16f;
        public float DistBias = -10f;

        // ---- Gameplay input (constants from ViWinRoot::Tick FUN_00529c10) -------------
        [Header("Gameplay input")]
        public bool EnableInput = true;
        [Tooltip("Base pan units per second (DAT_00737054 = 0.2), scaled by PanScale and the zoom factor.")]
        public float PanSpeed = 0.2f;
        public float PanTurboSpeed = 1.0f;     // DAT_00737138
        public float PanScale = 50f;           // DAT_007375cc
        [Tooltip("Pan gets FASTER as zoom (distance) grows: factor = 1 + (zoom-MinZoom)*PanZoomGain. "
               + "Original gain = (zoom-1)*0.2*k+1 (k≈1) → 0.2.")]
        public float PanZoomGain = 0.2f;
        [Tooltip("Wheel zoom-in multiplier (DAT_007370d4 = 0.8); zoom-out is its reciprocal 1.125.")]
        public float ZoomInStep = 0.8f;
        public float ZoomOutStep = 1.125f;
        [Tooltip("Rotation radians per drag-pixel (DAT_00736fcc = 0.005).")]
        public float RotateStep = 0.005f;
        [Tooltip("Flip vertical drag → pitch (dx) direction if it feels inverted.")]
        public bool InvertRotatePitch = false;
        [Tooltip("Flip horizontal drag → yaw (dy) direction if it feels inverted.")]
        public bool InvertRotateYaw = false;
        [Tooltip("Max combined rotation magnitude: dx^2 + dy^2 <= RotMagMax (DAT_00737268 = 1.8).")]
        public float RotMagMax = 1.8f;
        [Tooltip("Screen-edge margin (px) that triggers edge-scroll. 0 = disabled.")]
        public float EdgeScrollMargin = 8f;

        [Header("Zoom smoothing")]
        [Tooltip("Exponential smoothing time for gameplay wheel zoom. 0 = instant.")]
        public float ZoomSmoothTime = 0.10f;
        [System.NonSerialized] private float _zoomTarget;

        [Header("Map bounds (focus clamp; 0/0 size = unbounded)")]
        public Vector2 FocusMin = Vector2.zero;
        public Vector2 FocusMax = Vector2.zero;
        [Tooltip("Focus inset at minimum gameplay zoom, in native world units.")]
        public float MarginBase = 6f;
        [Tooltip("Additional focus inset per zoom unit above the gameplay minimum.")]
        public float MarginPerZoom = 8f;

        void Awake()
        {
            if (TargetCamera == null) TargetCamera = GetComponent<Camera>();
            if (TargetCamera == null) TargetCamera = Camera.main;
            _zoomTarget = Zoom;
        }

        void LateUpdate()
        {
            if (Suppress != null && Suppress()) return;   // external driver owns the camera
            if (Mode == CameraMode.Gameplay && EnableInput) HandleInput(Time.deltaTime);
            ApplyToCamera();
        }

        // ---- Public API (the cutscene presenter / scripted driver calls these) -------

        public void SetState(float focusX, float focusY, float zoom, float dx, float dy)
        {
            FocusX = focusX; FocusY = focusY;
            // Scripted/cutscene zoom uses set_view's wider clamp [0.5,6.0] (DAT_0073708c..DAT_007374e0),
            // not the gameplay caller's [1.0,2.5] — else a cutscene zoom like urw_6a's 0.7 clamps to 1.0
            // and R=zoom*16-10 jumps from 1.2 to 6 (camera 5x too far). Gameplay input still clamps [1,2.5].
            Zoom = Mathf.Clamp(zoom, CutsceneMinZoom, CutsceneMaxZoom);
            _zoomTarget = Zoom;
            Dx = dx; Dy = dy;
        }

        public void SetWorldBounds(Vector2 min, Vector2 max)
        {
            FocusMin = min;
            FocusMax = max;
            ClampFocus();
        }

        // Cutscene presenter entry: set state + shake and pose the camera in one call (Scripted mode).
        public void DriveScripted(float focusX, float focusY, float zoom, float dx, float dy, Vector3 shake)
        {
            Mode = CameraMode.Scripted;
            SetState(focusX, focusY, zoom, dx, dy);
            ShakeOffset = shake;
            ApplyToCamera();
        }

        // ---- Core: pose the Unity camera from the state ------------------------------
        // Exact reproduction of the original's CPU-built view matrix:
        // at = (FocusX, FocusY, 14) [native: x=east, y=depth, z=height]
        // R = zoom*16 - 10
        // The camera is near-straight-down (offset ≈ +Z), nudged by two small tilts:
        // dx = tilt about the X (east) axis → camera leans in the depth-height plane (N/S)
        // dy = tilt about the Y (depth) axis → camera leans in the east-height plane (E/W)
        // (Not an azimuth about the vertical — that would spin the view about the near-aligned view axis, i.e. roll.)
        // eye = at + R*(cos(dx)*sin(dy), sin(dx), cos(dx)*cos(dy))
        // up = (sin(dx)*sin(dy), -cos(dx), sin(dx)*cos(dy)) (orthonormal for all dx,dy)
        // Native -> WorldRoot-local by the axis swap (x, z, -y) (camera z is already render height, so no
        // nibble *0.5+7), then WorldRoot.TransformPoint. The root Euler(270,0,0) maps native Y (the dy tilt
        // axis) to world +Y (the camera up), so dy rotates without roll.
        public void ApplyToCamera()
        {
            var cam = TargetCamera;
            if (cam == null) return;

            float R = Mathf.Max(0.5f, Zoom * DistPerZoom + DistBias);
            float cdx = Mathf.Cos(Dx), sdx = Mathf.Sin(Dx);
            // dy rotates about native Y; the original's sign is opposite ours (rotate by -dy) — verified
            // visually (was turning left when it should turn right). cos is even so only sin flips.
            float cdy = Mathf.Cos(Dy), sdy = -Mathf.Sin(Dy);

            Vector3 atN = new Vector3(FocusX, FocusY, AtHeight);
            Vector3 eyeN = atN + new Vector3(R * cdx * sdy, R * sdx, R * cdx * cdy);
            Vector3 upN = new Vector3(sdx * sdy, -cdx, sdx * cdy);

            Vector3 eyeW = NativeToWorldPoint(eyeN) + ShakeOffset;
            Vector3 atW = NativeToWorldPoint(atN);
            Vector3 upW = NativeToWorldDir(upN);

            cam.transform.position = eyeW;
            cam.transform.rotation = Quaternion.LookRotation((atW - eyeW).normalized, upW);

            cam.orthographic = false;
            cam.nearClipPlane = NearPlane;
            cam.farClipPlane = FarPlane;
            cam.fieldOfView = ComputeVerticalFovDeg(cam.aspect);
        }

        // World distance from the camera eye to its ground look-at. A movescreen overlay elf is placed
        // at a fraction of this (along the screen ray) so it always floats in front of the ground at any
        // zoom — a fixed world depth sank into the terrain when R (= zoom*16-10) shrank at low zoom.
        public float FocusDistanceWorld()
        {
            float R = Mathf.Max(0.5f, Zoom * DistPerZoom + DistBias);
            float cdx = Mathf.Cos(Dx), sdx = Mathf.Sin(Dx);
            float cdy = Mathf.Cos(Dy), sdy = -Mathf.Sin(Dy);
            Vector3 atN = new Vector3(FocusX, FocusY, AtHeight);
            Vector3 eyeN = atN + new Vector3(R * cdx * sdy, R * sdx, R * cdx * cdy);
            return Vector3.Distance(NativeToWorldPoint(eyeN), NativeToWorldPoint(atN));
        }

        // Native render coords (x=east, y=depth, z=height, all metres) -> Unity world.
        // Axis swap (x, z, -y) into WorldRoot-local, then the root transform.
        Vector3 NativeToWorldPoint(Vector3 n)
        {
            Vector3 local = new Vector3(n.x, n.z, -n.y);
            return WorldRoot != null ? WorldRoot.TransformPoint(local) : local;
        }

        Vector3 NativeToWorldDir(Vector3 n)
        {
            Vector3 local = new Vector3(n.x, n.z, -n.y);
            return WorldRoot != null ? WorldRoot.TransformDirection(local) : local;
        }

        // Original projection (FUN_0044dee0) is a standard D3D LH perspective: M[0]=cot(hFOV/2),
        // M[5]=cot(vFOV/2)=M[0]*aspect. The half-FOV the engine bakes is horizontal = 0.3*aspect rad
        // (hFOV = 0.6*aspect; at 4:3 → 45.8°). Unity's fieldOfView is vertical and it re-derives the
        // horizontal from the runtime aspect, so we must hand it the matching vertical:
        // tan(vFOV/2) = tan(hFOV/2) / aspect (vertical is narrower than horizontal for aspect>1).
        // (A prior version multiplied by aspect instead of dividing, giving vFOV ≈ 58.8° instead of ≈ 35.3°.)
        float ComputeVerticalFovDeg(float aspect)
        {
            if (aspect <= 0f) aspect = 4f / 3f;
            float halfHFov = HalfFovPerAspect * aspect;       // horizontal half-FOV (rad) = 0.3*aspect
            float tanV = Mathf.Tan(halfHFov) / aspect;        // tan(vFOV/2)
            return Mathf.Clamp(2f * Mathf.Atan(tanV) * Mathf.Rad2Deg, 1f, 179f);
        }

        // ---- Gameplay input ----------------------------------------------------------

        // Exponential approach; snaps exactly to target once within epsilon so it settles.
        public static float StepZoomSmoothing(float current, float target, float smoothTime, float dt)
        {
            if (smoothTime <= 0f || dt <= 0f) return target;

            float t = 1f - Mathf.Exp(-dt / smoothTime);
            float next = Mathf.Lerp(current, target, t);
            return Mathf.Abs(next - target) < 1e-4f ? target : next;
        }

        void HandleInput(float dt)
        {
            if (_zoomTarget == 0f) _zoomTarget = Zoom;

            // --- Pan: global native X/Y (not rotated by the view), faster as zoom (distance) grows.
            // Original (FUN_00529c10): arrow delta goes straight into the live pan fields; up = -native Y,
            // down = +Y, left = -X, right = +X.
            Vector2 move = Vector2.zero;
            if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) move.x -= 1f;
            if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) move.x += 1f;
            if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) move.y -= 1f;
            if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) move.y += 1f;

            if (EdgeScrollMargin > 0f && Application.isFocused)
            {
                Vector3 mp = Input.mousePosition;
                if (mp.x >= 0 && mp.x <= Screen.width && mp.y >= 0 && mp.y <= Screen.height)
                {
                    if (mp.x < EdgeScrollMargin) move.x -= 1f;
                    else if (mp.x > Screen.width - EdgeScrollMargin) move.x += 1f;
                    if (mp.y > Screen.height - EdgeScrollMargin) move.y -= 1f;   // top edge = up = -Y
                    else if (mp.y < EdgeScrollMargin) move.y += 1f;
                }
            }

            if (move != Vector2.zero)
            {
                bool turbo = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                float zoomFactor = 1f + Mathf.Max(0f, Zoom - MinZoom) * PanZoomGain;   // farther = faster
                float step = (turbo ? PanTurboSpeed : PanSpeed) * PanScale * zoomFactor * dt;
                FocusX += move.x * step;       // global: no view-yaw rotation
                FocusY += move.y * step;
                ClampFocus();
            }

            // --- Zoom (mouse wheel), multiplicative -----------------------------------
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.0001f)
            {
                _zoomTarget *= wheel > 0f ? ZoomInStep : ZoomOutStep;
                _zoomTarget = Mathf.Clamp(_zoomTarget, MinZoom, MaxZoom);
            }

            Zoom = Time.timeScale == 0f || ZoomSmoothTime <= 0f
                ? _zoomTarget
                : StepZoomSmoothing(Zoom, _zoomTarget, ZoomSmoothTime, dt);
            ClampFocus();

            // --- Rotate (orbit around the focus on both tilt axes), bounded ------------
            // Right/middle drag: mouse X -> dy (E/W tilt), mouse Y -> dx (N/S tilt). Bounded by the
            // magnitude clamp dx^2+dy^2 <= RotMagMax so it's an orbit-with-limits, not a free spin.
            float ds = RotateStep * 30f;
            if (Input.GetMouseButton(1) || Input.GetMouseButton(2))
            {
                // Original (Tick 1461-1463): cam[0x59] += dragX*0.005 (dy); cam[0x58] -= dragY*0.005 (dx).
                // The Invert* toggles let you flip either axis live in the Inspector to match feel.
                float dyDelta = Input.GetAxis("Mouse X") * ds * (InvertRotateYaw ? -1f : 1f);
                // Verified by feel: mouse down → camera up (matches the original). +MouseY achieves this.
                float dxDelta = Input.GetAxis("Mouse Y") * ds * (InvertRotatePitch ? -1f : 1f);
                Dy += dyDelta;
                Dx += dxDelta;
                ClampRotation();
            }
            float keyStep = ds * dt * 60f;
            if (Input.GetKey(KeyCode.Q)) { Dy -= keyStep; ClampRotation(); }
            if (Input.GetKey(KeyCode.E)) { Dy += keyStep; ClampRotation(); }
        }

        // Clamp a focus (orbit target) to [min,max] inset by a zoom-scaled margin.
        // Port deviation: native pan gates candidates against per-player fog rectangles
        // (_global/00529c10_ViWinRoot_Tick.c:1334-1456; _global/00529570_FUN_00529570.c:53-195),
        // not a zoom-scaled map-AABB inset.
        public static Vector2 ClampFocusToBounds(
            Vector2 focus,
            Vector2 min,
            Vector2 max,
            float zoom,
            float minZoom,
            float marginBase,
            float marginPerZoom)
        {
            float margin = marginBase + marginPerZoom * Mathf.Max(0f, zoom - minZoom);
            return new Vector2(
                ClampFocusAxis(focus.x, min.x, max.x, margin),
                ClampFocusAxis(focus.y, min.y, max.y, margin));
        }

        private static float ClampFocusAxis(float value, float min, float max, float margin)
        {
            if (max <= min) return value;

            float lo = min + margin;
            float hi = max - margin;
            return lo > hi ? 0.5f * (min + max) : Mathf.Clamp(value, lo, hi);
        }

        void ClampFocus()
        {
            Vector2 focus = ClampFocusToBounds(
                new Vector2(FocusX, FocusY),
                FocusMin,
                FocusMax,
                Zoom,
                MinZoom,
                MarginBase,
                MarginPerZoom);
            FocusX = focus.x;
            FocusY = focus.y;
        }

        // Keep (Dx,Dy) within the original's combined rotation bound dx^2 + dy^2 <= RotMagMax.
        void ClampRotation()
        {
            if (RotMagMax <= 0f) return;
            float mag2 = Dx * Dx + Dy * Dy;
            if (mag2 > RotMagMax)
            {
                float s = Mathf.Sqrt(RotMagMax / mag2);
                Dx *= s; Dy *= s;
            }
        }
    }
}
