#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System.Collections.Generic;
    using Diggles.Runtime.Navigation;
    using UnityEngine;

    // Wall-climb executor — CAct_Climb + per-leg CAct_WalkAnim (Docs/re_parts/climb-exec.md).
    // Vertical CTransportLogic ladders never enter this class: DigglesTclEngine dispatches them
    // to LadderRiderAction, matching FUN_00607000:48 and FUN_0060bc70. Elevator-wall asymmetry
    // remains deliberately outside this lane.
    //
    // Original mechanics reproduced:
    // • Records are uniform waypoints (FUN_0040bb70 sets flag 0 on every record; direction is
    // never pre-tagged). The route depths were already baked by NavWorldService (per-record
    // support height, FUN_004238e0) — this executor only consumes positions.
    // • The 3-phase movable[+0x234] machine (FUN_00418db0): a held one-shot mount transition
    // (slot 0x10 ground_to_wall = stand_zu_kletterstand) entering the wall, the root-motion
    // advance, and the held dismount (slot 0x11 wall_to_ground = kletterstand_zu_stand) on
    // the wall→floor handoff. In the original the dismount actually plays as the first phase
    // of the following floor segment (the phase value persists on the movable); chaining it
    // at the climb's end is the same instant in the segment chain. Transitions whose state
    // doesn't resolve are skipped — the engine's "slot == -1 ⇒ advance phase" (FUN_00418850).
    // • Clip selection per leg = the heading quadrant (FUN_00418c70 slots 9/0xa/0xb/0xc(+0xd)),
    // resolved through the animset states (climb_up/climb_down/climb_right/climb_left/
    // climb_standard, z_anims.tcl set_class_animset) — never hardcoded clip names, so the
    // gender remap and the run bank (kletter_*_schnell) come for free. The original bins on
    // an atan2 the decompile half-mangles (climb-exec.md §9.4, unresolved); we bin on the
    // dominant axis of (lateral X, vertical Y), which reproduces the observed clips
    // (kletter_hoch up / runter down / links↔rechts lateral).
    // • Advance is arc-length consumption per the native budget law (FUN_00418db0:74-82):
    // per logic tick = 0.1 × 0.25 × Σ rm(slots 9..12 = climb_up/down/right/left, the
    // FUN_00401010 slot registration order; DAT_00737058 = 0.25), rm = the raw clip |VecA|
    // cached at the live slot-table scale 0.05 ⇒ world cruise = 0.25·Σ|VecA|·0.05
    // ≈ 0.4155 wu/s for
    // mann — uniform for every leg (the native sums all four slots every tick regardless of
    // heading). Unit derivation + start_001 calibration: DigglesAnimController.NativeArcToWorld.
    // • -canclimb 0 ⇒ WS_NOPASSAGE happens at dispatch (BuildMetaWalkFromOptions) —
    // the FUN_0040bf80:16 gate.
    public sealed class ClimbAction : IEntityAction
    {
        enum Phase { Enter, Mount, Advance, Dismount, Done }

        const float TurnRate = 540f;   // deg/s facing alignment (cosmetic, as MetaWalkAction)
        // Port-only fingertip compensation off the wall face along its outward normal (toward
        // the open pocket / world −Z). The original has no standoff: FUN_00418db0:123-125 lerps
        // x, y and z straight to each record, so the root rides exactly on the face plane and
        // the body clears the rock purely through the authored kletter pose offset (mann.3db
        // frame-0 mass ≈ +0.19 wu on the model-front side) + the net render orientation. Our
        // pose chain reproduces the bulge but not the per-slot orientation polish
        // (FUN_00422fe0, unresolved), so ≤0.25 wu keeps fingertips/toes out of the face.
        const float WallStandoff = 0.25f;

        readonly DigglesAnimController? _c;   // inner model GO (clips + root motion)
        readonly Transform? _outer;           // entity controller GO (the moving transform)
        readonly List<Vector3> _pts = new();  // the wall records (world space, standoff applied)
        readonly Vector3 _faceDir;            // climb facing (into the wall)
        readonly bool _mount;                 // entering from a floor waypoint
        readonly bool _dismount;              // exiting onto a floor waypoint
        readonly Vector3 _floorEnd;           // the bounding floor wp the dismount lands on
        readonly int _animset;

        Phase _phase = Phase.Enter;
        double _holdRem;        // remaining hold of the current one-shot transition
        int _i;                 // record cursor
        string _state = "";     // active climb state
        float _cruise;

        // Diagnostic ([CLIMBTRC]) — armed from DigglesWorldRuntime's Debug section.
        public static bool TraceClimb = false;
        double _traceAccum;

        public ClimbAction(DigglesAnimController? c, Transform? outer, List<NavWaypoint> path,
                           int walkAnimset, int runAnimset, int speedtype)
        {
            _c = c; _outer = outer;
            // Gait bank (FUN_004234a0): the run animset only on the forced-run walk; the
            // standard bank otherwise (its climb slots are the kletter_*_loop clips).
            _animset = (speedtype == 2 && runAnimset >= 0) ? runAnimset : walkAnimset;
            if (path == null || path.Count == 0) { _phase = Phase.Done; _mount = _dismount = false; return; }
            _mount = !path[0].IsWall;                       // bounding floor wp before the run
            _dismount = !path[path.Count - 1].IsWall;       // bounding floor wp after the run
            if (_dismount) _floorEnd = path[path.Count - 1].Pos;
            Vector3 n = default;
            foreach (var wp in path)
                if (wp.IsWall && n == default) { n = wp.Normal; break; }
            // Outward = toward the open pocket = world −Z (air = the larger depth nibble side;
            // NavWorldService.WallOutward, wall-embedding audit).
            if (n == default) n = NavWorldService.WallOutward;
            // Standoff = the base fingertip compensation plus the per-record face clearance at a depth
            // step (NavWaypoint.Clearance, native nibbles): where the cell's own face rises above the
            // faithful support depth the record rides, park the body that much further air-ward so it
            // clears the local face instead of sinking into the current cell's rock (the
            // sinks-into-the-wall step-embed). Native depth → world units = ×0.5 (world Z = native_z·0.5),
            // so a 1-nibble step adds 0.5 wu of outward standoff. Flat faces carry Clearance 0 ⇒ the
            // faithful uniform 0.25 wu, unchanged.
            foreach (var wp in path)
                if (wp.IsWall) _pts.Add(wp.Pos + n * (WallStandoff + wp.Clearance * 0.5f));
            // The bounding floor wp is the claimed boundary record (FUN_004163a0:69-72 extends a
            // wall run by +1; FUN_0040bb70 mirrors it into the climb's record array with the last
            // wall record's z) — it is part of the climb's advance: the final leg walks onto the
            // wall-adjacent floor cell, and the dismount transition then plays stationary there.
            // Without this the body stayed at the last wall record and the dismount snap warped it
            // to wherever the next floor run's first emitted waypoint was (its far landing, when
            // FloorPathMap's LOS collapse dropped the intermediate points) — the teleport bug.
            if (_dismount && _pts.Count > 0) _pts.Add(_floorEnd);
            // Yaw target for the climb: face into the wall (−outward = world +Z) — the climber
            // hangs with the back to the camera, the real-game silhouette.
            // • The Tcl wall-dig pose pins roty = π (z_procs.tcl:1707), and the verified
            // named-angle map (DigglesObject.FaceRotationForNativeAngle, cutscene-facing
            // work) sends π → world +Z = into the screen = into the wall. The C# climber
            // now agrees with the Tcl wall pose instead of fighting it.
            // • An earlier revision aimed the front at the air (−Z) off the wall-embedding
            // audit's pose-centroid argument, which was the wrong-facing regression — the
            // centroid bulge says where the mass hangs, not where the face looks.
            // • The mass still clears the rock: WallStandoff (0.25 outward) puts the root
            // 0.25 wu on the air side, so the frame-0 mass centroid (+0.19 wu along model
            // front) lands ≈ on/just outside the face plane while fingers/toes (+0.45 max)
            // reach into the face — a grip, not a burial.
            // The original composes roty=π with the per-slot climb orientation
            // (FUN_00419c60:87-93,126 → FUN_00422fe0 — the slot table's per-slot orientation
            // anim, still unresolved); facing the wall is the observable net result.
            _faceDir = -n;
        }

        public bool Tick(double dt)
        {
            if (_c == null || _outer == null || _phase == Phase.Done) return false;
            switch (_phase)
            {
                case Phase.Enter:
                    if (_pts.Count == 0) { _phase = Phase.Done; return false; }
                    _c.SetActiveAnimset(_animset);
                    _cruise = ClimbCruise();
                    // The mount phase follows the FUN_00418db0 phase machine (movable[+0x234]):
                    // before any advance, phase 0 plays slot 0x10 = ground_to_wall
                    // (stand_zu_kletterstand) held to its full frame count, stationary at the
                    // current body position — the wall-base floor cell the preceding floor
                    // segment delivered (its exit anchor is the FUN_00415f40 minH+1 boundary
                    // rule, on the floor at the face). The clip's VecA = 0 (mann.3db probe) —
                    // the native cannot translate during the mount even in its integrator. An
                    // earlier port synthesized an "Approach" leg that lerped the body 3-D
                    // straight to the first wall record at walk speed, so gnomes mounted from
                    // mid-air over the pit center; that leg was removed. The climb now starts
                    // grounded, and the first wall record is consumed by the climb integrator
                    // at climb speed in the climb pose (FUN_00419c60:39-41 seeds the arc cursor
                    // from the body, so a ≤1-cell floor→wall hop is part of the climb).
                    if (_mount && _c.PlayState("ground_to_wall", DigglesAnimator.AnimMode.Once))
                    {
                        _phase = Phase.Mount;
                        _holdRem = ClipDuration();
                        return true;
                    }
                    // No mount needed (run starts on the wall) or the slot doesn't resolve
                    // (slot == -1 ⇒ advance phase, FUN_00418db0:36-38).
                    _phase = Phase.Advance;
                    return true;

                case Phase.Mount:
                    Face((float)dt);             // yaw into the wall during the transition
                    _holdRem -= dt;
                    if (_holdRem <= 0.0) _phase = Phase.Advance;
                    return true;

                case Phase.Advance:
                    Face((float)dt);
                    SelectClip();
                    Advance(_cruise * (float)dt);
                    // Diagnostic ([CLIMBTRC], ~2 Hz): body vs current-record positions during a climb.
                    // Discriminator for a sinks-into-the-wall report: if the body tracks the record
                    // (delta stays ≈ the 0.25 standoff) while the mesh visually sinks, the bug is in
                    // the pose/clip layer (clip binning), not here; if the delta itself drifts with
                    // time, something external (Tcl set_vel? another writer) is pushing the body.
                    if (TraceClimb && _outer != null && _i < _pts.Count)
                    {
                        _traceAccum += dt;
                        if (_traceAccum >= 0.5)
                        {
                            _traceAccum = 0.0;
                            Vector3 d = _outer.position - _pts[_i];
                            UnityEngine.Debug.LogWarning($"[CLIMBTRC] {_c!.name} rec {_i}/{_pts.Count} " +
                                $"body={_outer.position:F3} rec={_pts[_i]:F3} delta={d:F3} |d|={d.magnitude:F3}");
                        }
                    }
                    if (_i < _pts.Count) return true;
                    // wall→floor handoff (climb-exec.md §5): the held wall_to_ground one-shot,
                    // stationary at the last record (≈ floor level — the record's support depth
                    // already equals the floor cell's, so no translation during the jump-off).
                    if (_dismount)
                    {
                        if (_c.PlayState("wall_to_ground", DigglesAnimator.AnimMode.Once))
                        {
                            _phase = Phase.Dismount;
                            _holdRem = ClipDuration();
                            return true;
                        }
                        _outer.position = _floorEnd;   // clip missing (slot -1) → land immediately
                        _phase = Phase.Done;
                        return false;
                    }
                    SettleOnWall();
                    _phase = Phase.Done;
                    return false;

                case Phase.Dismount:
                    _holdRem -= dt;
                    if (_holdRem > 0.0) return true;
                    // Re-snap to the boundary floor record on leg completion (FUN_00419c60:188-200).
                    // The advance already walked onto it (it is the last entry of _pts), so this only
                    // strips residue (the wall standoff of the leg before) — without the record in
                    // _pts the body stayed at the last wall record and get_gnomeposition kept
                    // reading 1 (wall) → the dwarf dug in the wall pose while hanging (symptom 3).
                    _outer.position = _floorEnd;
                    _phase = Phase.Done;
                    return false;
            }
            return false;
        }

        // FUN_00418c70 — the heading → climb-clip quadrant, evaluated for the current leg in the
        // gameplay (lateral, vertical) plane. Vertical dominant: down ⇒ climb_down, up ⇒ climb_up;
        // lateral dominant ⇒ climb_right/climb_left; degenerate ⇒ climb_standard (kletterstand).
        //
        // Lateral sign: the kletter_links/rechts
        // clips are character-relative (links' root motion = +X mesh, rechts = −X — mann.3db
        // vecA probe; with forward = −Y mesh per gehen_loop's vecA that makes mesh space
        // right-handed and character left = +X mesh). Facing into the wall (world +Z, back to
        // camera), the character's anatomic right = world +X (LookRotation(+Z)·right) and,
        // facing away from the viewer, his right is also the screen's right — so a leg moving
        // world +X (screen right) ⇒ climb_right, −X ⇒ climb_left. FUN_00418c70's own atan2
        // binning is still half-mangled in the RE (climb-exec.md §9.4); if laterals still read
        // mirrored, swap only this pair — the wall facing above is independently pinned by roty=π.
        void SelectClip()
        {
            if (_i >= _pts.Count) return;
            Vector3 d = _pts[_i] - _outer!.position;
            string s;
            float dx = d.x, dyDown = -d.y;                  // world +Y = game up ⇒ native +y = -Y
            if (d.sqrMagnitude < 1e-6f) s = "climb_standard";
            else if (Mathf.Abs(dyDown) >= Mathf.Abs(dx)) s = dyDown > 0f ? "climb_down" : "climb_up";
            else s = dx > 0f ? "climb_right" : "climb_left";
            if (s == _state) return;
            // Push only on change (FUN_005cdae0 is called only when slot/orientation changed).
            // The clip choice does not change the speed — the native consumes the same
            // 0.25·Σ rm fullArc every tick whichever directional clip is showing.
            if (_c!.PlayState(s, DigglesAnimator.AnimMode.Loop)) _state = s;
            else if (_state.Length == 0 && _c.PlayState("climb_standard", DigglesAnimator.AnimMode.Loop))
                _state = "climb_standard";
        }

        // Root-motion climb speed (retires the walk-base × budget-ratio chain): the native
        // per-tick climb advance is
        // 0.1 × 0.25 × Σ rm(slots 9,0xa,0xb,0xc) (FUN_00418db0:74-82, DAT_00737058)
        // with slots 9..12 = climb_up / climb_down / climb_right / climb_left (FUN_00401010's
        // registration order — the ids are pinned, not inferred) and rm = the slot's raw clip
        // |VecA| (FUN_00423270 → record[+8]) cached at the live slot scale 0.05 ⇒
        // cruise = 0.25 × Σ|VecA| × 0.05 × cadence wu/s
        // mann set 0: 0.25 × (8.3571+8.2914+8.2883+8.3065) × 0.05 = 0.4155 wu/s, uniform across
        // legs (the native sums all four slots every tick — direction only picks the clip). A
        // forced-run climb (animset 1) reads the kletter_*_schnell bank's own VecA. Unit
        // derivation + the start_001 calibration: DigglesAnimController.NativeArcToWorld.
        // Preserve the pre-existing clip-less fallback for degraded rigs; loaded runtime banks use
        // NativeClimbSpeed above and therefore the verified 0.4155 wu/s mann law.
        const float FallbackClimbSpeed = 1.0389f;
        float ClimbCruise()
        {
            float cad = _c!.Animator != null ? Mathf.Max(0.01f, _c.Animator.PlaybackSpeed) : 1f;
            float v = _c.NativeClimbSpeed(_animset, cad);
            return v > 0.0001f ? v : FallbackClimbSpeed * cad;
        }

        // Arc-length consumption over the records — the same integrator as the floor mover
        // (FUN_00418db0:85-144): lerp inside a segment, snap + cursor advance on full consumption,
        // arrival = cursor exhaustion. World space bakes the ×0.5 depth weight into the Z scale.
        void Advance(float dist)
        {
            if (dist <= 0f || _i >= _pts.Count) return;
            int guard = 0;
            Vector3 cur = _outer!.position;
            while (dist > 0f && _i < _pts.Count && guard++ < 256)
            {
                Vector3 to = _pts[_i] - cur;
                float seg = to.magnitude;
                if (seg <= 1e-4f) { _i++; continue; }
                if (dist < seg)
                {
                    cur += to * (dist / seg);
                    dist = 0f;
                }
                else
                {
                    cur = _pts[_i]; _i++;
                    dist -= seg;
                }
            }
            _outer.position = cur;
        }

        // The run ends on the wall (no bounding floor — e.g. a wall-click) → hold kletterstand.
        void SettleOnWall()
        {
            _c!.PlayState("climb_standard", DigglesAnimator.AnimMode.Loop);
        }

        // Yaw the outer so the model faces the wall. The original drives facing through the clip
        // orientation pair + the turn math (FUN_004185a0/FUN_00418730); facing the wall face is
        // the net result for climb legs (the kletter clips are authored against the wall plane).
        void Face(float dt) => FaceDir(_faceDir, dt);

        void FaceDir(Vector3 dirH, float dt)
        {
            if (_c == null || _outer == null) return;
            Vector3 d = new Vector3(dirH.x, 0f, dirH.z);
            if (d.sqrMagnitude < 1e-6f) return;
            d.Normalize();
            Vector3 face = _c.transform.rotation * Vector3.forward;
            face.y = 0f;
            if (face.sqrMagnitude < 1e-4f) return;
            float yaw = Vector3.SignedAngle(face.normalized, d, Vector3.up);
            float step = Mathf.Clamp(yaw, -TurnRate * dt, TurnRate * dt);
            _outer.rotation = Quaternion.AngleAxis(step, Vector3.up) * _outer.rotation;
        }

        double ClipDuration()
        {
            int n = _c != null && _c.Animator != null ? _c.Animator.CurrentFrameCount : 0;
            return n > 0 ? n / 10.0 : 0.0;
        }
    }
}
