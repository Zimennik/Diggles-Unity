#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Headless-testable cinematic-camera model driven by the sequencer's native camera
    // commands (set_view / set_viewpos / set_camerafollow / camera_set). Pure state + logic:
    // the sim calls Tick(dt) to interpolate moves; a Play-mode component reads Focus/Zoom/
    // Yaw/Pitch to pose the real Unity Camera. Tests assert this state directly.
    //
    // Diggles' camera looks at a focus point on (or above) the map and orbits it; `zoom` is
    // the view distance, `dx`/`dy` are yaw/pitch offsets from the default isometric angle,
    // `move` animates vs snaps, `speed` scales the glide. See Docs/CUTSCENE_SYSTEM.md §3.
    public sealed class CutsceneCameraState
    {
        // Current (interpolated) view.
        public Vector3 Focus;
        public float Zoom = 1f;
        public float Dx, Dy;          // yaw / pitch offsets (script units)

        // Target view while animating.
        Vector3 _tFocus;
        float _tZoom = 1f, _tDx, _tDy;
        float _speed = 1f;
        bool _animating;

        // Start-of-move snapshot + elapsed time-fraction, for camset-eased glides.
        Vector3 _sFocus;
        float _sZoom = 1f, _sDx, _sDy;
        float _moveTau;         // 0→1 elapsed time fraction of the current move
        float _moveDuration;    // seconds the current move takes (base / speed)
        bool _useCamset;
        CameraSet? _camset;     // active easing profile set by camera_set

        // Base move duration in seconds at speed 1 (divided by the speed arg).
        const float MoveBaseDuration = 2.0f;

        public bool Frozen;
        public bool ViewLocked;       // viewlock 1 → ignore further view commands
        // False until the cutscene issues its FIRST explicit view (set_view/set_viewpos/follow).
        // The presenter leaves the gameplay camera alone until then — the original doesn't snap the
        // camera the instant a cutscene starts (urw_3 waits `do_wait time 4` before sq_camera).
        public bool HasView;

        // Follow mode: track an entity's position as the focus.
        public int FollowHandle = -1; // -1 = not following
        public float FollowZoom = 1f;
        System.Func<int, Vector3?>? _resolvePos;

        // Host wires this so follow / get_camerafollowdist can read entity positions.
        public void SetPositionResolver(System.Func<int, Vector3?> f) => _resolvePos = f;

        // get_cameramoving — true while a non-follow glide is in flight.
        public bool Moving => _animating;

        public void SetView(Vector3 focus, float zoom, float dx, float dy, bool move, bool freeze, float speed, bool useCamset = false)
        {
            if (ViewLocked) return;
            HasView = true;
            FollowHandle = -1;                 // an explicit view cancels follow
            _tFocus = focus; _tZoom = zoom; _tDx = dx; _tDy = dy;
            _speed = speed > 0f ? speed : 1f;
            Frozen = freeze;
            if (move)
            {
                // Snapshot the start view + reset elapsed time so a camset profile can ease start→target.
                _sFocus = Focus; _sZoom = Zoom; _sDx = Dx; _sDy = Dy;
                _moveTau = 0f;
                _moveDuration = MoveBaseDuration / _speed;
                _useCamset = useCamset && _camset != null;
                _animating = true;
            }
            else { Focus = focus; Zoom = zoom; Dx = dx; Dy = dy; _animating = false; }
        }

        // Re-seed the CURRENT pose at a SEQUENCE START, syncing this cutscene camera to the live
        // gameplay camera. No target, no animation, no HasView change — it only replaces the
        // interpolation's "from". Native keeps ONE camera object: set_sequence (FUN_00649080) never
        // resets it, and a `move` op interpolates FROM the live view (FUN_00629fe0:478-525 copies the
        // live fields +0x154.. into the move's FROM slots +0xf4..). The port split gameplay/cutscene
        // into two objects, so without this the first `sq_camera move` of a 2nd sequence would glide
        // in from the PRIOR sequence's leftover Focus (the "2nd-cutscene fly-in").
        public void SeedPose(float focusX, float focusY, float zoom, float dx, float dy)
        {
            Focus = new Vector3(focusX, focusY, 0f); Zoom = zoom; Dx = dx; Dy = dy;
            _tFocus = Focus; _tZoom = zoom; _tDx = dx; _tDy = dy;
            _animating = false;
        }

        // camera_set <startspeed> <waypoints> — install the easing profile applied to the next move.
        public void SetCamset(float startSpeed, (float a, float b)[] points)
            => _camset = new CameraSet { StartSpeed = startSpeed, Points = points };

        public void Follow(int handle, float zoom)
        {
            if (ViewLocked) return;
            HasView = true;
            FollowHandle = handle;
            FollowZoom = zoom > 0f ? zoom : 1f;
            _tZoom = FollowZoom;
            _animating = true;
        }

        public void StopFollow() => FollowHandle = -1;

        public void Tick(double dt)
        {
            if (FollowHandle >= 0 && _resolvePos != null)
            {
                var p = _resolvePos(FollowHandle);
                if (p.HasValue) _tFocus = p.Value;
                _tZoom = FollowZoom;
            }
            if (Frozen) { _animating = false; return; }

            // Follow: track the object's position continuously (not a "move").
            if (FollowHandle >= 0)
            {
                float kf = Mathf.Clamp01((float)dt * _speed * 4f);
                Focus = Vector3.Lerp(Focus, _tFocus, kf);
                Zoom = Mathf.Lerp(Zoom, _tZoom, kf);
                Dx = Mathf.Lerp(Dx, _tDx, kf);
                Dy = Mathf.Lerp(Dy, _tDy, kf);
                _animating = false;
                return;
            }

            if (!_animating) return;

            if (_useCamset && _camset != null && _moveDuration > 0f)
            {
                // Camset-eased glide: time advances linearly; the velocity-vs-time curve is integrated
                // into a normalized progress (0→1) that shapes the easing (inout = accel from rest then
                // decel; standard = start fast then decel). Lerp the view start→target by that progress.
                _moveTau += (float)dt / _moveDuration;
                float p = _camset.ProgressAt(_moveTau);
                Focus = Vector3.Lerp(_sFocus, _tFocus, p);
                Zoom = Mathf.Lerp(_sZoom, _tZoom, p);
                Dx = Mathf.Lerp(_sDx, _tDx, p);
                Dy = Mathf.Lerp(_sDy, _tDy, p);
                if (_moveTau >= 1f) { _animating = false; Focus = _tFocus; Zoom = _tZoom; Dx = _tDx; Dy = _tDy; }
                return;
            }

            // Default glide (no camset): exponential ease toward the target.
            float k = Mathf.Clamp01((float)dt * _speed * 4f);
            Focus = Vector3.Lerp(Focus, _tFocus, k);
            Zoom = Mathf.Lerp(Zoom, _tZoom, k);
            Dx = Mathf.Lerp(Dx, _tDx, k);
            Dy = Mathf.Lerp(Dy, _tDy, k);
            bool arrived = (Focus - _tFocus).sqrMagnitude < 1e-4f && Mathf.Abs(Zoom - _tZoom) < 1e-3f;
            _animating = !arrived;
            if (arrived) { Focus = _tFocus; Zoom = _tZoom; Dx = _tDx; Dy = _tDy; }
        }

        // get_camerafollowdist — distance from the current focus to the followed object
        // (the do_wait-camera gate holds while this > 4.0 or Moving).
        public float FollowDist()
        {
            if (FollowHandle >= 0 && _resolvePos != null)
            {
                var p = _resolvePos(FollowHandle);
                if (p.HasValue) return Vector3.Distance(Focus, p.Value);
            }
            return 0f;
        }

        // get_view 1 selects the pending target fields when a scripted target is valid. Native
        // GetView branches from live +0x154..+0x164 to target +0x134..+0x150 for that argument
        // (FUN_00629b80, 0x00629d08..0x00629ecc). The existing trailing zero makes the restored
        // set_view explicitly non-moving, matching the native default.
        public string ViewString(bool predicted)
        {
            bool useTarget = predicted && HasView && _tFocus.x >= 0f;
            Vector3 focus = useTarget ? _tFocus : Focus;
            float zoom = useTarget ? _tZoom : Zoom;
            float dx = useTarget ? _tDx : Dx;
            float dy = useTarget ? _tDy : Dy;
            return $"{Ftos(focus.x)} {Ftos(focus.y)} {Ftos(zoom)} {Ftos(dx)} {Ftos(dy)} 0";
        }

        private static string Ftos(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    // A camset (camera_set) — a velocity-vs-TIME easing profile. StartSpeed is the velocity at time 0;
    // Points are (timeFraction, velocity) keys (last fraction = 1, usually velocity 0). The piecewise-
    // linear velocity curve is integrated over time → a normalized progress (ProgressAt), so the move
    // eases per the curve's SHAPE: inout {0 {{0.3 0.7}{1.0 0.0}}} = accelerate from rest then decelerate;
    // standard {1.0 {{0.5 0.3}{1.0 0.0}}} = start fast then decelerate. (Integrating velocity-vs-progress
    // instead would stall when StartSpeed=0 — inout never leaves p=0; hence the velocity-vs-time model.)
    public sealed class CameraSet
    {
        public float StartSpeed;
        public (float a, float b)[] Points = System.Array.Empty<(float, float)>();

        // Normalized eased progress (0→1) at elapsed time-fraction tau (0→1).
        public float ProgressAt(float tau)
        {
            tau = Mathf.Clamp01(tau);
            float total = Area(1f);
            return total > 1e-6f ? Mathf.Clamp01(Area(tau) / total) : tau;
        }

        // ∫0^upto of the piecewise-linear velocity curve (trapezoidal). Keys: (0,StartSpeed) → Points.
        float Area(float upto)
        {
            float area = 0f, prevA = 0f, prevB = StartSpeed;
            foreach (var (a, b) in Points)
            {
                float segEnd = Mathf.Min(a, upto);
                if (segEnd > prevA && a > prevA)
                {
                    float vEnd = Mathf.Lerp(prevB, b, (segEnd - prevA) / (a - prevA));
                    area += (prevB + vEnd) * 0.5f * (segEnd - prevA);
                }
                if (upto <= a) return area;
                prevA = a; prevB = b;
            }
            if (upto > prevA) area += prevB * (upto - prevA);   // past last key: hold last velocity
            return area;
        }
    }
}
