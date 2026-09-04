#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using VContainer;
    using VContainer.Unity;

    // Drives the camera during cutscenes from DigglesTclContext.Camera (+ ScreenFx shake): poses the
    // RE'd DigglesCameraController (DriveScripted) while a cutscene runs, and hands the camera back to
    // gameplay input when it ends. Scene-placed (on DigglesControllers), injected with the context.
    // Extracted verbatim from the old DigglesCutscenePresenter (camera+elf split for SRP).
    //
    // EXECUTION ORDER (100): the cutscene camera is posed synchronously here (DriveScripted →
    // ApplyToCamera). The ElfPresenter (order 200) reads the posed camera AFTER this, so the elf
    // faces/screen-tracks the SAME-frame camera pose — restoring the old presenter's atomic
    // camera→elf ordering (split into two LateUpdates would otherwise let the elf read a 1-frame-stale
    // pose and visibly lag/jitter while the camera moves).
    [DefaultExecutionOrder(100)]
    public sealed class CutsceneCameraDriver : MonoBehaviour, IStartable, IRuntimeSessionPresentationParticipant
    {
        [Tooltip("Route the cutscene camera through the exact RE'd controller (DriveScripted) — gets the " +
                 "real orbit incl. dy rotation + the wider cutscene zoom clamp. Off: the fixed-iso fallback.")]
        [SerializeField] bool routeThroughController = true;

        [Header("Fallback framing (only used when no controller is wired)")]
        [SerializeField] float basePitchDeg = 35f;
        [SerializeField] float baseYawDeg = 180f;
        [SerializeField] float zoomToDistance = 6f;
        [SerializeField] float dxToPitchRad = 1f;
        [SerializeField] float dyToYawRad = 1f;
        [SerializeField] float cameraLerp = 10f;

        DigglesTclContext? _ctx;

        [UnityEngine.Scripting.Preserve]
        [Inject]
        public void Construct(IActiveGameSession sessions) => BindSession(sessions.Current);

        Camera? _camera;
        DigglesCameraController? _cameraController;
        Transform? _worldRoot;
        bool _camResolved;
        bool _prevSeqActive;

        public void BindSession(RuntimeGameSession session)
        {
            _ctx = session.Engine.Ctx;
            _worldRoot = _ctx.SpawnParent;
            _prevSeqActive = _ctx.SequenceActive;
            _camera = null;
            _cameraController = null;
            _camResolved = false;
        }

        void IStartable.Start()
        {
            if (_ctx != null) _worldRoot = _ctx.SpawnParent;
        }

        void LateUpdate()
        {
            if (_ctx == null) return;
            ResolveCamera();
            if (_ctx.SequenceActive)
            {
                // Sequence just began (false→true edge): re-seed the cutscene camera's CURRENT pose
                // from the LIVE gameplay camera, so the first `sq_camera move` glides from the on-
                // screen view — not the PRIOR sequence's leftover Focus (the "2nd-cutscene fly-in").
                // Native shares ONE camera object; set_sequence (FUN_00649080) never resets it and a
                // move interpolates FROM the live view (FUN_00629fe0:478-525). We re-unify our split
                // cutscene/gameplay cameras here. HasView stays false so the real camera still HOLDS
                // the gameplay pose until this sequence's first explicit view.
                if (!_prevSeqActive && _ctx.Camera != null && _cameraController != null)
                    _ctx.Camera.SeedPose(_cameraController.FocusX, _cameraController.FocusY,
                        _cameraController.Zoom, _cameraController.Dx, _cameraController.Dy);
                UpdateCamera();
            }
            else
            {
                // Hand the camera back to gameplay input when the cutscene ends.
                if (_cameraController != null && _cameraController.Mode == DigglesCameraController.CameraMode.Scripted)
                    _cameraController.Mode = DigglesCameraController.CameraMode.Gameplay;
                UpdateGameplayCamera();
            }
            _prevSeqActive = _ctx.SequenceActive;
        }

        // The camera controller is created by DigglesWorldRuntime after the scope builds, so resolve
        // it lazily (cached) rather than in Start.
        void ResolveCamera()
        {
            if (_camResolved) return;
            _camera = Camera.main;
            _cameraController = _camera != null ? _camera.GetComponent<DigglesCameraController>() : null;
            if (_camera != null) _camResolved = true;
        }

        Vector3 FocusToWorld(Vector3 nativeFocus)
            => _worldRoot != null ? _worldRoot.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nativeFocus)) : nativeFocus;

        // Outside a sequence, a gameplay-side set_view (e.g. a news-ticker click action zooming to
        // a newborn — native CTclGlobalMethods::SetView FUN_006284e0 writes the SHARED camera) must
        // reach the gameplay controller too: consume HasView once and drive the orbit with it.
        //
        // GAMEPLAY ZOOM CONSUMPTION LAW (news-click zoom-0 fix): the canonical news-click actions
        // pass zoom LITERAL 0 (baby.tcl:436, z_events.tcl:2004, z_work_common.tcl:630,
        // Skirmish.tcl:254 — always `set_view $x [expr $y -1] 0 -0.35 0`). Native SetView
        // (FUN_006284e0:159-173) stores that 0 RAW into the target field +0x13c (only SetViewPos
        // FUN_00629fe0:297-307 clamps its own zoom arg). The consumer is CGameThread::Draw
        // FUN_006b5d90 (debug string:93 "CGameThread::Draw if(m_fViewMoveToX>-1000.0f)"):
        // *:128-135 — pending target + moving flag false → SNAP: live x/y/zoom/dx/dy = targets
        // VERBATIM (zoom 0 included), targets consumed (reset to the -1000 sentinel);
        // *:552-562 — the SAME frame's per-frame live-zoom clamp then rewrites
        // m_fZoom = min(max(m_fZoom, DAT_00737138=1.0), DAT_007373d8=2.5) on the gameplay
        // path (game+0x598 sequence flag clear; the:579-587 cutscene path uses [0.5, 6.0]).
        // Net effect: gameplay set_view zoom 0 lands at the gameplay MINIMUM zoom 1.0 (R = 6),
        // NOT the cutscene floor 0.5 (R = 0.5 — an in-your-face close-up) that DriveScripted's
        // cutscene clamp would produce. So clamp to the GAMEPLAY window here, before driving.
        public static float GameplayZoomFor(float setViewZoom)
            => Mathf.Clamp(setViewZoom,
                DigglesCameraController.MinZoom, DigglesCameraController.MaxZoom);

        void UpdateGameplayCamera()
        {
            var cam = _ctx!.Camera;
            if (cam == null || _cameraController == null || !cam.HasView) return;

            _cameraController.DriveScripted(
                cam.Focus.x, cam.Focus.y, GameplayZoomFor(cam.Zoom), cam.Dx, cam.Dy, Vector3.zero);
            _cameraController.Mode = DigglesCameraController.CameraMode.Gameplay;
            if (!cam.Moving) cam.HasView = false;
        }

        void UpdateCamera()
        {
            var cam = _ctx!.Camera;
            if (cam == null || _camera == null) return;
            // Leave the gameplay camera untouched until the cutscene sets its first view — the
            // original doesn't snap/move the camera the instant the cutscene begins.
            if (!cam.HasView) return;

            // Screenvibe shake offset (along the current camera right/up — 1-frame lag is fine).
            Vector3 shake = Vector3.zero;
            if (_ctx.ScreenFx is { ShakeActive: true } fx)
            {
                var o = fx.ShakeOffset(_ctx.Sim?.GameTime ?? 0.0);
                shake = _camera.transform.right * o.x + _camera.transform.up * o.y;
            }

            // Drive the ONE camera controller (exact RE model). cam.Focus.x/y = native ground target;
            // cam.Dx = pitch-from-vertical, cam.Dy = azimuth; cam.Zoom = zoom.
            if (routeThroughController && _cameraController != null)
            {
                _cameraController.DriveScripted(cam.Focus.x, cam.Focus.y, cam.Zoom, cam.Dx, cam.Dy, shake);
                if (CutsceneTrace.Enabled && Time.frameCount % 30 == 0)
                    CutsceneTrace.Log($"camera(ctrl): focus=({cam.Focus.x:F2},{cam.Focus.y:F2}) zoom={cam.Zoom:F2} " +
                        $"dx={cam.Dx:F3} dy={cam.Dy:F3} camPos={_camera.transform.position} " +
                        $"euler={_camera.transform.rotation.eulerAngles} fov={_camera.fieldOfView:F1}");
                return;
            }

            // Fallback (no controller wired): simple fixed-iso orbit.
            Vector3 focusWorld = FocusToWorld(cam.Focus);
            float pitch = basePitchDeg * Mathf.Deg2Rad + cam.Dx * dxToPitchRad;
            float yaw = baseYawDeg * Mathf.Deg2Rad + cam.Dy * dyToYawRad;
            var dir = new Vector3(Mathf.Cos(pitch) * Mathf.Sin(yaw), Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Cos(yaw));
            float dist = Mathf.Max(1f, cam.Zoom * zoomToDistance);
            Vector3 want = focusWorld + dir * dist + shake;
            float k = 1f - Mathf.Exp(-cameraLerp * Time.deltaTime);
            _camera.transform.position = Vector3.Lerp(_camera.transform.position, want, k);
            _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation,
                Quaternion.LookRotation((focusWorld - want).normalized, Vector3.up), k);
        }
    }
}
