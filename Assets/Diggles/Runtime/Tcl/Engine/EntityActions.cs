#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using UnityEngine;

    // The engine's per-object "action" layer (CAction). At most one action runs
    // on an object at a time; it advances over many sim ticks and, when it
    // finishes naturally, the runtime evals its Tcl `finishcode`; if it is
    // replaced/interrupted first, it evals its `breakcode`. This is the
    // completion contract the original scripts rely on:
    // state_disable this; action this anim X {state_enable this}
    // and idle chains like action this anim X {seq_idle}.
    //
    // Verbs implemented here: `anim`, `wait`, `rotate`, and the full `walk` chain
    // (MetaWalkAction / ClimbAction / LadderRiderAction segments under the lifecycle wrappers).
    public interface IEntityAction
    {
        // Advance by dt seconds. Returns true while still running, false when
        // finished. Called once per sim Step on the owning entity.
        bool Tick(double dt);

        // Does this action drive the owning entity's transform (position) each tick? The sim's
        // position-sync phase reads Position back from the transform only for entities whose active
        // action does — so idle/stationary actions (wait, anim) let the sim skip a per-step native
        // transform read + InverseTransformPoint (a big fast-forward win: up to 40 steps/frame).
        // Default true is the safe choice — a real mover that forgets to override just over-syncs
        // (harmless); marking a mover false would leave its Position stale. Movers (MetaWalk/Climb/
        // LadderRider/Walk/Fly) inherit the default; only provably-stationary actions override false.
        bool DrivesTransform => true;
    }

    // Play a single clip (resolved via the controller's state map, or used as a
    // literal "obj.anim") in ONCE mode, and finish when its length elapses
    // (frameCount / 10 s). Mirrors `action this anim <x>`.
    public sealed class AnimAction : ISaveableEntityAction
    {
        private readonly DigglesAnimController? _ctrl;
        private readonly string _clip;
        private bool _started;
        private double _remaining;
        public bool DrivesTransform => false;   // plays a clip only — never moves the transform

        public AnimAction(DigglesAnimController? ctrl, string clip)
        {
            _ctrl = ctrl;
            _clip = clip ?? "";
        }

        public AnimAction(DigglesAnimController? ctrl, AnimActionSaveState state)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));
            _ctrl = ctrl;
            _clip = state.Clip;
            _started = state.Started;
            _remaining = state.RemainingSeconds;
        }

        public RuntimeEntityActionSaveState CaptureSaveState()
        {
            return new AnimActionSaveState(_clip, _started, _remaining);
        }

        public bool Tick(double dt)
        {
            if (!_started)
            {
                _started = true;
                if (_ctrl != null && _clip.Length > 0)
                {
                    var resolved = _ctrl.ResolveState(_clip) ?? _clip;
                    // A clip whose name says "loop" (lookdownloop, knitloop, hackquloop, …) must
                    // loop — playing it Once froze it on its last frame (e.g. a dwarf stuck staring
                    // down the shaft). Everything else plays Once (the action still finishes after
                    // one cycle, so an `action anim` chain advances + fires its finishcode).
                    bool loop = _clip.IndexOf("loop", System.StringComparison.OrdinalIgnoreCase) >= 0
                             || resolved.IndexOf("loop", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    bool played = _ctrl.PlayClip(resolved, loop ? DigglesAnimator.AnimMode.Loop : DigglesAnimator.AnimMode.Once);
                    if (!played)
                        UnityEngine.Debug.LogWarning(
                            $"[ANIMFAIL] clip '{_clip}' → '{resolved}' failed to play on '{_ctrl.name}'");
                    // Duration from the resolved clip only: regression — a silent PlayClip failure
                    // used to read CurrentFrameCount of the previous clip, so the entity visibly
                    // played the wrong thing for the stale clip's length.
                    // Failed resolve → the 0.1 s no-clip fallback, loudly.
                    int n = played && _ctrl.Animator != null ? _ctrl.Animator.CurrentFrameCount : 0;
                    _remaining = n > 0 ? n / 10.0 : 0.1;
                }
                else _remaining = 0.1; // no controller/clip → finish quickly
                return _remaining > 0.0;
            }
            _remaining -= dt;
            return _remaining > 0.0;
        }
    }

    // A mover that can terminate with its own WS result code (the native sub-action's obj+0x08
    // terminal status, walk-lifecycle §5.2). 0 = clean finish (the wrapper decides); 3 =
    // WS_NOPASSAGE (the leg died mid-flight — e.g. a carve removed the floor under the route,
    // the CAct_WalkFloor per-leg rebuild law FUN_0041a470/FUN_0041aac0).
    public interface IWalkResultCarrier
    {
        int ExitResult { get; }
    }

    // Wraps a walk action and writes the owning entity's WalkResult (the engine's obj+0x248
    // pathfind-result enum read by get_walkresult, doc §3.4): 2=WS_NOTFINISHED (walking) on the
    // first tick, then the terminal code when the inner action completes. The unchanged Tcl
    // branches only on 2 (busy) and 4 (arrived); `walkfail` gives up via
    // `4 != [get_walkresult this]` — without this the gnome abandons every job the instant it
    // stops walking. Works for the real MetaWalk/Climb path and the headless degraded WaitAction.
    // Faithful to CActWalk adopting the child's terminal status (FUN_00408160:16 copies the inner
    // MetaWalk's +0x08 up; walk-lifecycle §5.2 mirrors it into gnome+0x248) — a chain that died
    // mid-flight with WS_NOPASSAGE must not read as "arrived".
    public sealed class WalkResultAction : IEntityAction
    {
        readonly IEntityAction _inner;
        readonly DigglesTclEntity _ent;
        readonly int _onFinish;
        bool _started;

        public WalkResultAction(IEntityAction inner, DigglesTclEntity ent, int onFinish)
        { _inner = inner; _ent = ent; _onFinish = onFinish; }

        public bool Tick(double dt)
        {
            if (!_started) { _started = true; _ent.WalkResult = 2; }   // WS_NOTFINISHED = walking
            bool running = _inner.Tick(dt);
            if (!running)
                _ent.WalkResult = _inner is IWalkResultCarrier c && c.ExitResult != 0
                    ? c.ExitResult                                     // child died with its own code
                    : _onFinish;                                       // 4 arrived / 3 no-passage
            return running;
        }
    }

    // The CActWalk wrapper guards (walk-lifecycle.md §5.5 + climb-route.md §0):
    // • -walktimercallback/-walktimercounter — the callback body is eval'd every N native ticks
    // (10 fps) and on finish; returning anything but "1"/empty aborts the walk with
    // WS_WALKEVENT (5). A Tcl error in the body disables further firing but keeps walking
    // (FUN_004075b0:183-222).
    // • -targetdist — finish-early: when the gnome comes within this distance of the target
    // (depth ×0.5 weighted — free in world space), stop with state 5 (FUN_0041aef0:59-66).
    // Sits outside WalkResultAction so an abort's 5 is not overwritten by the inner 4.
    public sealed class WalkGuardAction : IEntityAction
    {
        readonly IEntityAction _inner;
        readonly DigglesTclEntity _ent;
        readonly System.Func<string>? _callback;   // evals the -walktimercallback body, returns its result
        readonly Transform? _tf;
        readonly Vector3 _targetWorld;
        readonly float _targetDist;                // <0 = disabled (the CWalkInfo default −1)
        int _counter;                              // -walktimercounter period, in native ticks
        double _tickAccum;
        int _ticks;
        bool _disabled;

        public WalkGuardAction(IEntityAction inner, DigglesTclEntity ent, Transform? tf,
            System.Func<string>? callback, int counter, Vector3 targetWorld, float targetDist)
        {
            _inner = inner; _ent = ent; _tf = tf;
            _callback = callback; _counter = counter;
            _targetWorld = targetWorld; _targetDist = targetDist;
        }

        public bool Tick(double dt)
        {
            bool running = _inner.Tick(dt);
            if (_targetDist >= 0f && _tf != null && running
                && (_tf.position - _targetWorld).magnitude <= _targetDist)
            {
                _ent.WalkResult = 5;                    // WS_WALKEVENT — finish-early
                return false;
            }
            if (_callback != null && !_disabled && _counter > 0)
            {
                _tickAccum += dt;
                bool fire = false;
                while (_tickAccum >= 0.1) { _tickAccum -= 0.1; if (++_ticks % _counter == 0) fire = true; }
                if (fire || !running)
                {
                    string res;
                    try { res = _callback(); }
                    catch { _disabled = true; res = "1"; }   // callback errored → stop re-firing, keep walking
                    if (res != "1" && res.Length != 0)
                    {
                        _ent.WalkResult = 5;                // WS_WALKEVENT — script aborted the walk
                        // FUN_004075b0:212-221: a non-"0" result string is ref-assigned as the
                        // walk's finish code and runs once the walk stops — genericfight's
                        // pursuit returns `run_pos_obj {…} $attack_item` / `fight_try_attack; …`
                        // this way (genericfight.tcl:893-962). Regression: dropping the string made
                        // combat chasing twitchy and try_attack never fired on closing in.
                        // "0" aborts silently, as in the native.
                        if (res != "0") _ent.ActionFinish = res;
                        return false;
                    }
                }
            }
            return running;
        }
    }

    // CActWalk_AutoRetry (FUN_00408160 + FUN_00407fb0):
    // • WS_NOPASSAGE retry (FUN_00408160:17-19): when the inner MetaWalk dies with 3 — e.g. a
    // carve/growth stamp invalidated the route mid-flight (the per-leg rebuild law) — and the
    // gnome has moved since the last plan (FUN_00407fb0:30-35 compares the stored last-plan
    // position exactly), silently rebuild a fresh MetaWalk from the current position and keep
    // walking. The fresh plan starts from the body's live cell — a digger whose floor was just
    // carved re-enters the world as a CLIMB (FUN_00423730 classifies the start WALL) and
    // mounts instead of hanging/gliding. The moved-gate is the native termination: a walk that
    // made no progress since its plan fails out with 3 for the Tcl walkfail.
    // • Arrive-short retry (walk-lifecycle.md §5.3 re-aim): when the chain arrives (4) but the
    // body is still short of the target, re-plan from the current position (max 2 — the
    // original nudges the target; this stands in for it).
    public sealed class RetryWalkAction : IEntityAction
    {
        const int MaxBlockedRetries = 16;   // safety net only — the native loop is bounded by the
                                            // moved-gate (each retry must have made progress)

        readonly DigglesTclEntity _ent;
        readonly Transform? _tf;
        readonly Vector3 _targetWorld;
        readonly System.Func<IEntityAction?> _replan;   // builds a fresh inner chain from the current pos
        IEntityAction _inner;
        int _retries;          // arrive-short retries (§5.3 stand-in)
        int _blockedRetries;   // WS_NOPASSAGE retries (FUN_00408160)
        Vector3 _lastPlanPos;  // the body position the current plan was built from (+0x14/+0x18/+0x1c)
        bool _hasLastPlanPos;

        public RetryWalkAction(IEntityAction inner, DigglesTclEntity ent, Transform? tf,
            Vector3 targetWorld, System.Func<IEntityAction?> replan)
        {
            _inner = inner; _ent = ent; _tf = tf; _targetWorld = targetWorld; _replan = replan;
            if (tf != null) { _lastPlanPos = tf.position; _hasLastPlanPos = true; }
        }

        public bool Tick(double dt)
        {
            if (_inner.Tick(dt)) return true;
            if (_tf == null) return false;
            if (_ent.WalkResult == 3)
            {
                // FUN_00408160:17-19 — rebuild-from-here iff the body moved since the plan.
                if (_blockedRetries >= MaxBlockedRetries) return false;
                if (_hasLastPlanPos && (_tf.position - _lastPlanPos).sqrMagnitude <= 1e-4f) return false;
                var again = _replan();
                if (again == null) return false;           // no fresh path — keep the 3 (walkfail)
                _blockedRetries++;
                _lastPlanPos = _tf.position; _hasLastPlanPos = true;
                _inner = again;
                return true;
            }
            if (_ent.WalkResult != 4 || _retries >= 2) return false;
            Vector3 d = _tf.position - _targetWorld;
            if (d.magnitude <= 0.45f) return false;        // close enough — done
            var fresh = _replan();
            if (fresh == null) return false;               // can't improve — keep the arrived result
            _retries++;
            _lastPlanPos = _tf.position; _hasLastPlanPos = true;
            _inner = fresh;
            return true;
        }
    }

    // Runs child actions strictly in order — the CAct_MetaWalk segment chain
    // (floor run → climb run → floor run …, FUN_00416540 ticking one sub-action at a time).
    // Result propagation (FUN_00416540:31-45): a child that dies with WS_NOPASSAGE (3) stops the
    // whole chain and the code propagates up — only a clean segment finish (4) advances to the
    // next segment.
    public sealed class SequenceAction : IEntityAction, IWalkResultCarrier
    {
        readonly System.Collections.Generic.List<IEntityAction> _children;
        int _i;
        public int ExitResult { get; private set; }
        public SequenceAction(System.Collections.Generic.List<IEntityAction> children) { _children = children; }
        public bool Tick(double dt)
        {
            while (_i < _children.Count)
            {
                if (_children[_i].Tick(dt)) return true;
                if (_children[_i] is IWalkResultCarrier c && c.ExitResult != 0)
                {
                    ExitResult = c.ExitResult;             // NOPASSAGE kills the chain (FUN_00416540:36-39)
                    _i = _children.Count;
                    return false;
                }
                _i++;
            }
            return false;
        }
    }

    // Idle/delay for N seconds. Mirrors `action this wait <sec>`.
    public sealed class WaitAction : ISaveableEntityAction
    {
        private double _remaining;
        public bool DrivesTransform => false;   // pure countdown — never moves the transform
        public WaitAction(double seconds) { _remaining = seconds; }
        public WaitAction(WaitActionSaveState state)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));
            _remaining = state.RemainingSeconds;
        }
        public RuntimeEntityActionSaveState CaptureSaveState() =>
            new WaitActionSaveState(_remaining);
        public bool Tick(double dt)
        {
            _remaining -= dt;
            return _remaining > 0.0;
        }
    }

    // Walk forward (along the model's current facing) for `distance` world
    // units, with the engine's side-stages: walk_start (ONCE) → walk_loop
    // (LOOP, while moving) → walk_stop (ONCE). Gait clips resolve through the
    // active animset (e.g. STANDARDWALK=0 → gehen_start/loop/end). Speed comes
    // from the loop clip's root motion when available, else a fallback.
    // Turns are separate turn actions (TurnToFaceAction), so this never
    // needs target-facing math — it just moves where the model looks.
    public sealed class WalkAction : IEntityAction
    {
        // Root-motion speed: the body-speed model (model height ×
        // BodySpeedFactor) is retired — speed now comes from the native slot-cruise law,
        // cruise = clip |VecA| × 0.05 wu/s (FUN_00418280:59-62 consumption; scale settled by
        // the live-dump slot records — DigglesAnimController.NativeArcToWorld). BodySpeedFactor
        // is kept only so existing callers/scenes (DigglesWorldRuntime, DigglesTclAnimDemo)
        // still compile; it no longer affects movement.
        public static float BodySpeedFactor = 0.35f;
        // Leg-cycle rate (1 = native 10 FPS). Frame-coupled: body speed scales with it, so the
        // feet stay planted at any cadence.
        public static float LegCadence = 1f;

        // Clip-less fallback (headless tests / rigs without animation data): mann's walk law,
        // 16.2218 (gehen_loop |VecA|) × 0.05 = 0.811087 wu/s — the live-dump slot value.
        public const float FallbackWalkSpeed = 0.8111f;

        readonly DigglesAnimController? _c;
        readonly Transform? _tf;
        readonly float _distance;
        readonly int _animset;
        int _phase;            // 0 start, 1 loop, 2 stop
        bool _entered;
        double _rem, _dur;
        float _travelled;
        float _cruise;         // steady speed = walk_loop root motion

        public WalkAction(DigglesAnimController? c, Transform? tf, float distance, int animset)
        {
            _c = c; _tf = tf; _distance = Mathf.Abs(distance); _animset = animset;
        }

        public bool Tick(double dt)
        {
            if (_c == null || _tf == null) return false;
            switch (_phase)
            {
                case 0: // walk_start — full cruise from the first tick (FUN_00418280 consumes
                        // 0.1 × rm(active slot) during the start clip too; no invented ramp).
                    if (!_entered)
                    {
                        _entered = true;
                        _c.SetActiveAnimset(_animset);
                        if (_c.Animator != null) _c.Animator.PlaybackSpeed = LegCadence;
                        _c.PlayState("walk_start");
                        _dur = _rem = ClipDuration() / Mathf.Max(0.01f, LegCadence);
                        // cruise = clip |VecA| × 0.05 × cadence (the native frame-coupled law;
                        // derivation + calibration: DigglesAnimController.NativeArcToWorld).
                        _cruise = Cruise("walk_start");
                    }
                    MoveForward(StepToward(_cruise * (float)dt));
                    _rem -= dt;
                    if (_travelled >= _distance - 0.001f) { _phase = 2; _entered = false; }
                    else if (_rem <= 0.0) { _phase = 1; _entered = false; }
                    return true;

                case 1: // walk_loop = cruise: constant speed until distance covered
                    if (!_entered) { _entered = true; _c.PlayState("walk_loop"); _cruise = Cruise("walk_loop"); }
                    MoveForward(StepToward(_cruise * (float)dt));
                    if (_travelled >= _distance - 0.001f) { _phase = 2; _entered = false; }
                    return true;

                case 2: // walk_stop plays standing — arrival is distance exhaustion (the native's
                        // arrival is cursor exhaustion; the stop clip is a settle beat).
                    if (!_entered)
                    {
                        _entered = true;
                        if (_c.Animator != null) _c.Animator.PlaybackSpeed = LegCadence;
                        _c.PlayState("walk_stop");
                        _dur = _rem = ClipDuration() / Mathf.Max(0.01f, LegCadence);
                    }
                    _rem -= dt;
                    if (_rem <= 0.0)
                    {
                        if (_c.Animator != null) _c.Animator.PlaybackSpeed = 1f; // back to native cadence
                        _c.PlayState("standstill");
                        return false;
                    }
                    return true;
            }
            return false;
        }

        // Clamp a step to the remaining distance and book it.
        float StepToward(float step)
        {
            if (_travelled + step > _distance) step = _distance - _travelled;
            if (step < 0f) step = 0f;
            _travelled += step;
            return step;
        }

        // The native walk-bank cruise of `state` in the active animset, with the walk_loop and
        // clip-less fallbacks (FUN_00418280:59-62 — rm of the active walk slot).
        float Cruise(string state)
        {
            float v = _c!.NativeLocomotionSpeed(_animset, state, LegCadence);
            if (v <= 0.0001f) v = _c.NativeLocomotionSpeed(_animset, "walk_loop", LegCadence);
            // Cadence floored: an Inspector LegCadence of 0 would zero the fallback → a walk
            // that never advances (an unguarded permanent stall).
            return v > 0.0001f ? v : FallbackWalkSpeed * Mathf.Max(0.01f, LegCadence);
        }

        void MoveForward(float dist)
        {
            _tf!.position += (_tf.rotation * Vector3.forward) * dist;  // local +Z → world -Z = facing
        }

        float ClipDuration()
        {
            int n = _c!.Animator != null ? _c.Animator.CurrentFrameCount : 0;
            return n > 0 ? n / 10f : 0.3f;
        }
    }

    // The single C# movement executor — faithful CAct_MetaWalk in spirit. Walks an
    // entity along a nav path (world-space waypoints): walk_start (ONCE, accel) →
    // walk_loop (LOOP, cruise through the waypoints, turning to face each) →
    // walk_stop (ONCE, decel) → standstill. Clips resolve through the active
    // animset (0=STANDARDWALK, 1=RUN) on the controller — no hardcoded clip names.
    // Movement is toward each waypoint in the WORLD horizontal plane (correct
    // regardless of model-face sign); facing aligns the model's forward to the
    // travel direction (cosmetic). Both Tcl (`action this walk "-target {x y z}..."`)
    // and native C# build this action, so movement is dual-driven onto one executor.
    //
    // v1 = FLOOR only (wall waypoints are walked through as points; climb comes as a
    // ClimbAction this spawns later). Turning is faithful to the FLOOR mover
    // FUN_00418850/FUN_00418730: heading error ≥ π/4 → stop translation and pivot in
    // place looping the animset's turn slot 2-5 clip at its baked VecB.z rad per 0.1 s
    // logic tick, snap exactly, then replay walk_start; error < π/4 → in-stride drift
    // ±0.1 rad/tick with a 0.051 rad snap (FUN_00418280).
    public sealed class MetaWalkAction : IEntityAction, IWalkResultCarrier
    {
        // Faithful walk→run promotion is by time in stride, not by distance — the old
        // "RunDistance travelled" gate was an invention. Decompile ground truth:
        // • FUN_004234a0 (gait select): speedtype 2 ⇒ gait=RUN immediately; otherwise
        // gait=WALK, and only speedtype 0 arms the promotion byte (+0x7be=1,:15-16).
        // • FUN_00418280:107 (per in-stride mover tick): armed && legTicks(+0x20) > 0x14
        // ⇒ FUN_00423470 ⇒ gait=RUN iff the run animset (+0x1ec) ≠ -1 — i.e. a plain
        // speedtype-0 walk breaks into a run after 20 logic ticks (2.0 s) of striding,
        // unless the script forbade running (animsets pass -1: tired / pannier / box /
        // strike, z_dignwalk.tcl walk_action). speedtype 1 never promotes; speedtype 3
        // (autodist — hoverboard/hamster only) decides once at plan time by distance
        // (FUN_0041a910:69-82) and is not modelled here.
        public const float RunPromoteSeconds = 2.0f;   // 0x14 logic ticks × 0.1 s (FUN_00418280:107)

        readonly DigglesAnimController? _c;   // on the inner model GO
        readonly Transform? _outer;           // entity controller GO (move + yaw)
        readonly System.Collections.Generic.List<Vector3> _pts;
        readonly int _walkAnimset, _runAnimset, _speedtype;
        int _activeAnimset;     // current gait (walk or run animset id)
        bool _running;
        bool _runStarting;      // playing the run gait's start clip as the walk→run accel
        double _runStartRem;
        double _strideTime;     // seconds spent in the translating phases (native leg-tick counter +0x20)
        int _i;                 // index of the waypoint we're heading to
        int _phase;             // 0 start, 1 cruise, 2 stop
        bool _entered;
        double _rem, _dur;
        float _cruise;
        const float Arrive = 0.2f;     // waypoint arrival radius (world units)
        // Faithful turn constants — byte-for-byte from the exe, do not retune:
        const float TurnGateRad = 0.7853982f;  // π/4 in-place gate (DAT_007370d0, FUN_00418850:93)
        const float Turn180Rad = 2.3561945f;   // 3π/4 → 180-clip pair (_DAT_00737360, FUN_004185a0:38)
        const float SteerStepRad = 0.1f;     // in-stride drift, rad per 0.1 s tick (DAT_00737034)
        const float SteerSnapRad = 0.051f;   // in-stride snap epsilon (_DAT_00737014, FUN_00418280:31-43)
        bool _started;        // one-time cursor/animset/cruise init done
        bool _turning;        // in the in-place turn phase (native +0x1b "stillTurning")
        float _turnRate;      // rad per 0.1 s logic tick = |turn clip VecB.z| (native +0x34)
        int _turnTicks;       // anti-stall: consumed turn-phase calls (a full π at the slowest
                              // default rate 0.1 rad/tick = 32 logic ticks; the 600-call cap
                              // force-snaps — never an eternal pivot, the troll-cutscene bug class)

        // The per-leg support law (CAct_WalkFloor): the native consumes a floor run in ~32-sub-cell
        // legs, each leg rebuilt from current terrain (FUN_0041a470 arc split + FUN_0041aac0 band
        // stamping read the live tile data) — so when a carve/growth stamp opens the ground under
        // an in-flight walk, the next leg build fails and the sub-action dies with WS_NOPASSAGE
        // (3), which CActWalk_AutoRetry turns into a fresh plan from the current position
        // (FUN_00408160). The port keeps one frozen leg per run, so it re-checks support against
        // live nav instead: `support` (world-space, wired by BuildWalkSegments from
        // NavWorldService.HasFloorSupportWorld) must hold along every committed floor step. Sampling
        // at half-sub-cell spacing prevents a fast hamster gait from skipping a narrow air cell
        // between tick endpoints. Null support (headless/cutscene movers, tests) keeps the old frozen
        // behaviour.
        private const float SupportProbeSpacing = 0.0625f;
        private readonly System.Func<Vector3, bool>? _support;
        private bool _probeArmed;
        private readonly int _walkTraceId;
        private readonly string _walkTraceSegment;
        private bool _moverStartTraced;
        private bool _moverStopTraced;
        public int ExitResult { get; private set; }

        // walkAnimset / runAnimset come from the Tcl `-animsets {walk run …}` list;
        // speedtype from `-speedtype`. Gait starts at walk (or run if forced) and a
        // speedtype-0 walk promotes to the run after RunPromoteSeconds in stride.
        public MetaWalkAction(DigglesAnimController? c, Transform? outer,
            System.Collections.Generic.List<Vector3> pts,
            int walkAnimset, int runAnimset, int speedtype,
            System.Func<Vector3, bool>? support = null,
            int walkTraceId = 0,
            string walkTraceSegment = "")
        {
            _c = c; _outer = outer;
            _pts = pts ?? new System.Collections.Generic.List<Vector3>();
            _walkAnimset = walkAnimset; _runAnimset = runAnimset; _speedtype = speedtype;
            _running = speedtype == 2 && runAnimset >= 0;     // double-click → run now
            _activeAnimset = _running ? runAnimset : walkAnimset;
            _support = support;
            _walkTraceId = walkTraceId;
            _walkTraceSegment = walkTraceSegment;
        }

        // FUN_004234a0:15-16 — only speedtype 0 arms the time promotion (+0x7be); the run
        // animset must exist (FUN_00423470 gates on +0x1ec ≠ -1, i.e. scripts' fast=-1 veto).
        bool CanPromote => !_running && _speedtype == 0 && _runAnimset >= 0;

        // Break into a run mid-walk. Smooth transition: play the run gait's start clip
        // (e.g. laufen_schnell_start) ONCE as the walk→run acceleration, then fall into
        // the run loop — instead of a hard swap straight to the loop.
        void PromoteToRun()
        {
            _running = true;
            _activeAnimset = _runAnimset;
            _c!.SetActiveAnimset(_runAnimset);
            if (_c.Animator != null) _c.Animator.PlaybackSpeed = WalkAction.LegCadence;
            _c.PlayState("walk_start");                       // run's start = accel into run
            _runStartRem = ClipDuration() / Mathf.Max(0.01f, WalkAction.LegCadence);
            _runStarting = _runStartRem > 0.0;
            _cruise = Cruise(_runStarting ? "walk_start" : "walk_loop");
            if (!_runStarting) _c.PlayState("walk_loop");
        }

        public bool Tick(double dt)
        {
            if (_c == null)
            {
                TraceMoverStart($"REJECT {_walkTraceSegment}: missing DigglesAnimController");
                return false;
            }
            if (_outer == null)
            {
                TraceMoverStart($"REJECT {_walkTraceSegment}: missing entity transform");
                return false;
            }
            if (_pts.Count == 0)
            {
                TraceMoverStart($"REJECT {_walkTraceSegment}: empty waypoint list");
                return false;
            }
            if (FloorSupportLost(_outer.position)) return false;
            if (!_started)
            {
                // Resolve the first ahead waypoint before touching the animator: a
                // degenerate re-walk to the spot we already occupy (every waypoint
                // inside the arrival radius) must finish immediately and leave the
                // current anim alone. Regression: issuing walk_start here held
                // laufen_schnell_start's raised-leg last frame for the clip duration
                // while the body had nowhere to go, freezing the leg mid-raise. The
                // original's integrator (FUN_00418280:63-84) likewise completes on
                // cursor exhaustion, with no start-clip side effects for a zero-length path.
                _i = FirstAheadWaypoint();
                if (_i >= _pts.Count)
                {
                    TraceMoverStart(
                        $"COMPLETE {_walkTraceSegment}: every waypoint is already within {Arrive:0.###}u");
                    return false;
                }
                _started = true;
                TraceMoverStart(
                    $"ACCEPT {_walkTraceSegment}: points={_pts.Count} first={_i} " +
                    $"from={_outer.position} to={_pts[_i]} animsets={_walkAnimset}/{_runAnimset} " +
                    $"speedtype={_speedtype}");
                _c.SetActiveAnimset(_activeAnimset);
                _cruise = Cruise("walk_loop");
            }
            // The gate (FUN_00418850:93, disasm 00418a23-00418a35): heading error ≥ π/4
            // toward the current waypoint (or a turn already in flight) → the in-place
            // turn phase consumes the tick instead of any walk phase — translation is
            // frozen while the drehen clip loops. Checked at the very first tick too
            // (walk start = the most visible case) and at sharp waypoint corners.
            if (_phase <= 1 && TurnPhaseTick(dt)) return true;
            switch (_phase)
            {
                case 0: // walk_start clip plays while the body already moves at cruise
                    if (!_entered)
                    {
                        _entered = true;
                        if (_c.Animator != null) _c.Animator.PlaybackSpeed = WalkAction.LegCadence;
                        //play walk_start from frame 0 — including the post-turn re-arm
                        // (FUN_00418850:94-96 re-arms +0x19/+0x20 every turn tick, so the moment
                        // the turn snaps, walk_start replays before walk_loop resumes; the ONCE
                        // SetAnim restart semantics give the frame-0 replay).
                        _c.PlayState("walk_start");
                        _dur = _rem = ClipDuration() / Mathf.Max(0.01f, WalkAction.LegCadence);
                        _cruise = Cruise("walk_start");   // rm of the active clip (FUN_00418280:59)
                    }
                    // Full cruise from the first logic tick: the original consumes the whole
                    // per-tick arc 0.1 × rm(walk_start) during the start clip too
                    // (FUN_00418280:59-62 — slot 6 while +0x19/+0x20 run the start counter; speed
                    // is root-motion coupled, never ramped). The old 0→cruise `t` ramp was
                    // invented and made every walk begin with a glide-in.
                    if (!TryAdvance(_cruise * (float)dt, (float)dt)) return false;
                    _strideTime += dt;                 // the native leg-tick counter (+0x20) runs here too
                    _rem -= dt;
                    // Path exhausted during the start clip (short hop) → decelerate + settle
                    // (phase 2) instead of ending frozen on the start clip's last frame.
                    if (_i >= _pts.Count) { _phase = 2; _entered = false; return true; }
                    if (_rem <= 0.0) { _phase = 1; _entered = false; }
                    return true;

                case 1: // walk_loop = cruise through waypoints (promote to run after 2 s in stride)
                    if (!_entered) { _entered = true; _c.PlayState("walk_loop"); _cruise = Cruise("walk_loop"); }
                    if (!TryAdvance(_cruise * (float)dt, (float)dt)) return false;
                    _strideTime += dt;
                    if (CanPromote && _strideTime >= RunPromoteSeconds) PromoteToRun();
                    else if (_runStarting)   // finish the run-start clip → run loop
                    {
                        _runStartRem -= dt;
                        if (_runStartRem <= 0.0)
                        { _runStarting = false; _c.PlayState("walk_loop"); _cruise = Cruise("walk_loop"); }
                    }
                    if (_i >= _pts.Count) { _phase = 2; _entered = false; }
                    return true;

                case 2: // walk_stop = decelerate cruise → 0, then idle
                    if (!_entered)
                    {
                        _entered = true;
                        if (_c.Animator != null) _c.Animator.PlaybackSpeed = WalkAction.LegCadence;
                        _c.PlayState("walk_stop");
                        _dur = _rem = ClipDuration() / Mathf.Max(0.01f, WalkAction.LegCadence);
                    }
                    _rem -= dt;
                    if (_rem <= 0.0)
                    {
                        if (_c.Animator != null) _c.Animator.PlaybackSpeed = 1f;
                        // Settle to the active animset's own idle ("standard") — for a box carrier
                        // (animset 7) that's mann.kiste_stand (the carry-idle), not the empty-hand
                        // base "standstill" (which left the carrier in an A-pose with the box inside).
                        if (!_c.PlayState("standard", DigglesAnimator.AnimMode.Loop))
                            _c.PlayState("standstill", DigglesAnimator.AnimMode.Loop);
                        return false;
                    }
                    return true;
            }
            return false;
        }

        // The per-leg support re-check (see the ctor note).
        //
        // Armed only after the first supported position: the native per-leg law re-reads terrain
        // under the route (FUN_0041a470 arc split + FUN_0041aac0 band stamping build the next leg
        // from current tiles) — it never tests the body's own support. A walk whose start is stale
        // mid-air (the digger's floor was carved while he stood — the carve never repositions a
        // movable) legitimately begins with the body off support: the mover re-reads the live body
        // position every tick and drives it continuously into record[0] of the relaxed plan
        // (FUN_00419c60:36-45 — no teleport, no abort). Regression: probing the body from tick 1
        // killed that lawful approach after 0.05-0.2 s, and CActWalk_AutoRetry then replanned from
        // the barely-moved position — a 2-tick glide per replan, 16 replans, then WalkResult 3 into
        // the Tcl walkfail ladder and around again, leaving the gnome stuck hovering and crawling
        // through the air. Once the body has been supported once, a later loss is the mid-walk
        // carve and aborts before the unsupported step is committed.
        private bool FloorSupportLost(Vector3 position)
        {
            if (_support == null) return false;
            if (_support(position)) { _probeArmed = true; return false; }
            if (!_probeArmed) return false;            // stale-start approach — lawful off-support
            ExitResult = 3;                            // WS_NOPASSAGE — AutoRetry replans from here
            if (!_moverStopTraced)
            {
                _moverStopTraced = true;
                DigglesTclEngine.TraceWalkExecutor(
                    _walkTraceId,
                    "mover-stop",
                    $"REJECT {_walkTraceSegment}: floor support lost at world={position}");
            }
            return true;
        }

        private void TraceMoverStart(string message)
        {
            if (_moverStartTraced) return;
            _moverStartTraced = true;
            DigglesTclEngine.TraceWalkExecutor(_walkTraceId, "mover-start", message);
        }

        // FUN_00418280:63-84 (the FLOOR step; the climb sibling FUN_00418db0:85-144 shares the
        // structure) — consume `dist` of arc length along the point list: LERP inside a
        // segment, snap to the node + advance the cursor when a whole segment is consumed; arrival
        // = cursor exhaustion (no positional epsilon). In WORLD space the native depth-weighted
        // metric (dx² + dy² + (dz·0.5)²) is the plain Euclidean distance — world Z = native_z·0.5
        // bakes the ×0.5 depth weight in — so all three axes participate at face value. This also
        // replaces the old straight-Y-lerp: each waypoint carries its own ground-line Y / depth,
        // and the integrator interpolates between adjacent on-terrain points only.
        private bool TryAdvance(float dist, float dt)
        {
            if (dist <= 0f || _i >= _pts.Count) return true;
            int guard = 0;
            Vector3 cur = _outer!.position;
            while (dist > 0f && _i < _pts.Count && guard++ < 256)
            {
                Vector3 to = _pts[_i] - cur;
                float seg = to.magnitude;
                if (seg <= 1e-4f) { _i++; continue; }        // degenerate node — skip
                Vector3 dirH = new Vector3(to.x, 0f, to.z);
                if (dirH.sqrMagnitude > 1e-6f) FaceTowards(dirH / dirH.magnitude, dt);
                Vector3 next;
                bool consumesWaypoint = dist >= seg;
                if (!consumesWaypoint)
                {
                    next = cur + to * (dist / seg);
                }
                else
                {
                    next = _pts[_i];
                }
                if (!TryTraverseSupported(ref cur, next))
                {
                    _outer.position = cur;
                    return false;
                }
                if (!consumesWaypoint) dist = 0f;
                else { _i++; dist -= seg; }
            }
            _outer.position = cur;
            return true;
        }

        private bool TryTraverseSupported(ref Vector3 current, Vector3 target)
        {
            if (_support == null)
            {
                current = target;
                return true;
            }

            Vector3 start = current;
            float distance = (target - start).magnitude;
            int probeCount = Mathf.Max(1, Mathf.CeilToInt(distance / SupportProbeSpacing));
            for (int probeIndex = 1; probeIndex <= probeCount; probeIndex++)
            {
                Vector3 candidate = Vector3.Lerp(start, target, (float)probeIndex / probeCount);
                if (FloorSupportLost(candidate)) return false;
                current = candidate;
            }
            return true;
        }

        // In-place turn phase — the π/4 gate (FUN_00418850:93-96) + the stationary
        // integrator (FUN_004185a0 init / FUN_00418730 per-tick). Returns true when the
        // tick was consumed by turning: translation is frozen (the native turn branch
        // never touches position) while the animset's drehen clip loops and yaw advances
        // |clip VecB.z| rad per 0.1 s logic tick (mann: 0.5265 ≈ 302°/s for the 90 pair,
        // 0.7854 = 450°/s for 180 — read from DigglesAnimClip.RotationDelta, the 3db
        // VecB baked rate FUN_004185a0 stores at +0x34). On the snap tick the phase
        // exits with walk_start re-armed: _phase=0/_entered=false → walk_start replays
        // from frame 0, then walk_loop. A sharp corner crossed mid-tick leaks ≤1 tick of
        // translation before the gate sees the new segment (the native checks per
        // budget-loop iteration) — sub-0.1 s, invisible.
        bool TurnPhaseTick(double dt)
        {
            if (_i >= _pts.Count) { _turning = false; return false; }
            Vector3 to = _pts[_i] - _outer!.position;
            Vector3 dirH = new Vector3(to.x, 0f, to.z);
            if (dirH.sqrMagnitude < 1e-6f) { _turning = false; return false; }
            float err = SignedYawTo(dirH / dirH.magnitude) * Mathf.Deg2Rad;
            float abs = Mathf.Abs(err);
            if (float.IsNaN(err))                             // degenerate basis → never spin on NaN
            { _turning = false; return false; }
            if (_turning && ++_turnTicks > 600)               // anti-stall: force the exact snap
                                                              // (600 calls ≫ any real pivot: π at
                                                              // the slowest 0.1 rad/tick = 32 logic
                                                              // ticks; covers slow-mo + frame dt)
            {
                Yaw(err * Mathf.Rad2Deg);
                _turning = false;
                _phase = 0; _entered = false;
                return true;
            }
            if (!_turning)
            {
                if (abs < TurnGateRad) return false;          // < π/4 → in-stride steering
                _turning = true;
                _turnTicks = 0;
                bool big = abs > Turn180Rad;                  // > 3π/4 → 180 pair (+0x38, FUN_004185a0)
                bool right = err > 0f;                        // D6: Unity SignedAngle>0 ↔ native dir=+1 → right
                                                              // slots 3/5 (FUN_00418730: "3 - (dir != 1)").
                                                              // Sign coincidence needs one Play check.
                if (_c!.Animator != null) _c.Animator.PlaybackSpeed = 1f;   // turn clips run at native cadence
                bool played = TurnAnim.Play(_c!, right, big);
                _turnRate = played && _c.Animator != null
                    ? Mathf.Abs(_c.Animator.CurrentRotationDelta.z) : 0f;
                if (_turnRate <= 1e-4f) _turnRate = big ? 0.15f : 0.1f;   // FUN_00422a50:174-185 defaults
            }
            float step = _turnRate * (float)(dt * 10.0);      // rad this tick (rate is per 0.1 s logic tick)
            if (abs <= step + 1e-4f || abs <= _turnRate * 0.05f + 0.001f) // iterStep·0.5+0.001 (DAT_00736fb8)
            {
                Yaw(err * Mathf.Rad2Deg);                     // snap exactly (FUN_00418730:29)
                _turning = false;
                _phase = 0; _entered = false;                 // re-arm → replay walk_start, resume
                return true;                                  // (native walks the leftover budget — sub-tick)
            }
            Yaw(Mathf.Sign(err) * step * Mathf.Rad2Deg);
            return true;
        }

        // Signed yaw delta (degrees) from the model's current horizontal facing to dirH —
        // the same basis the steering always used (the inner model GO's forward, flattened).
        float SignedYawTo(Vector3 dirH)
        {
            Vector3 face = _c!.transform.rotation * Vector3.forward;
            face.y = 0f;
            if (face.sqrMagnitude < 0.0001f) return 0f;
            return Vector3.SignedAngle(face.normalized, dirH, Vector3.up);
        }

        // Yaw the outer transform about world up (the model child rides along). Movement doesn't
        // depend on facing, so a wrong face-sign still walks correctly.
        void Yaw(float deg)
        {
            _outer!.rotation = Quaternion.AngleAxis(deg, Vector3.up) * _outer.rotation;
        }

        // In-stride steering while translating (FUN_00418280:31-43): |delta| ≤ 0.051 rad
        // (_DAT_00737014) → snap to the travel direction; else drift ±0.1 rad per 0.1 s
        // logic tick (DAT_00737034, ≈57°/s) toward it — small path bends are gentle
        // dampened drifts, replacing the invented 540°/s turret yaw. dt is the sim step
        // (as in ClimbAction.FaceDir) — Time.deltaTime here desynced the turn rate from
        // the fixed-step sim.
        void FaceTowards(Vector3 dirH, float dt)
        {
            float yawDeg = SignedYawTo(dirH);
            if (yawDeg == 0f) return;
            if (Mathf.Abs(yawDeg) * Mathf.Deg2Rad <= SteerSnapRad) { Yaw(yawDeg); return; }
            float maxDeg = SteerStepRad * dt * 10f * Mathf.Rad2Deg;
            Yaw(Mathf.Clamp(yawDeg, -maxDeg, maxDeg));
        }

        // Skip waypoints we're already at/under (the path's first point is the start).
        int FirstAheadWaypoint()
        {
            for (int k = 0; k < _pts.Count; k++)
            {
                Vector3 d = _pts[k] - _outer!.position; d.y = 0f;
                if (d.magnitude > Arrive) return k;
            }
            return _pts.Count;
        }

        // Root-motion cruise (retires the model-height body-speed model and the gait-ratio trick):
        // the native floor mover consumes 0.1 × rm(active walk slot) of arc per logic tick
        // (FUN_00418280:59-62; the 0.1 = obj[+0x3c], reset each tick FUN_00419c60:46), where rm is
        // the slot's raw clip |VecA| over a sub-cell-unit point list ⇒ in world units cruise =
        // |VecA| × 0.05 × cadence (full derivation + the live-dump slot verification:
        // DigglesAnimController.NativeArcToWorld). mann: walk 16.2218 → 0.811 wu/s, run 34.2867 →
        // 1.714 wu/s, tired 16.191 → 0.810 wu/s. The animset decides the bank — a cutscene
        // `do_action run` (= `action walk -animsets 1`, default speedtype) cruises at run speed
        // from the first tick; speedtype only selects the promotion behaviour (FUN_004234a0).
        // The earlier mistake this replaces: LocomotionRate (rm / duration) as an absolute speed
        // — wrong normalization (the native never divides by clip length; one frame = one tick).
        float Cruise(string state)
        {
            float v = _c!.NativeLocomotionSpeed(_activeAnimset, state, WalkAction.LegCadence);
            if (v <= 0.0001f) v = _c.NativeLocomotionSpeed(_activeAnimset, "walk_loop", WalkAction.LegCadence);
            // Cadence floored — see WalkAction.Cruise (a 0 cadence must not zero the fallback).
            return v > 0.0001f ? v : WalkAction.FallbackWalkSpeed * Mathf.Max(0.01f, WalkAction.LegCadence);
        }

        float ClipDuration()
        {
            int n = _c!.Animator != null ? _c.Animator.CurrentFrameCount : 0;
            return n > 0 ? n / 10f : 0.3f;
        }
    }

    // Shared turn-clip selection for the walk turn phase + CAct_Rotate: resolve through
    // the active animset's turn slots first — slot names from the engine's slot table
    // FUN_00401010 (2=turn_left_90, 3=turn_right_90, 4=turn_left_180, 5=turn_right_180;
    // filled by z_anims.tcl set_class_animset, so a tired gnome turns with
    // drehen_*_muede, a box carrier with kiste_drehen_*, a hamster rider with
    // hamster_drehen_*) — falling back to the bare class-anim aliases (z_anims.tcl:31-37
    // turnleft/turnright/turn180left/turn180right) when the animset lacks them. Turn
    // slots play looping (FUN_00419c60:91-95 — mode 2 for slots 2-5; only the
    // transition slots get mode 1).
    static class TurnAnim
    {
        public static bool Play(DigglesAnimController c, bool right, bool big)
        {
            string slot = right ? (big ? "turn_right_180" : "turn_right_90")
                                : (big ? "turn_left_180" : "turn_left_90");
            if (c.PlayState(slot, DigglesAnimator.AnimMode.Loop)) return true;
            string alias = right ? (big ? "turn180right" : "turnright")
                                 : (big ? "turn180left" : "turnleft");
            return c.PlayState(alias, DigglesAnimator.AnimMode.Loop);
        }
    }

    // Turn in place to face an absolute target rotation — faithful CAct_Rotate
    // (`action <obj> rotate "<ang> [animsetId]"`, factory FUN_00405e10:221-258): the
    // rest/work/dig facing path (z_dignwalk.tcl rotate_to*) and the cutscene
    // `do_action rotate`. Plan (FUN_00416c00): pair by |delta| ≤ 3π/4 → 90 slots {2,3}
    // span π/2, else 180 slots {4,5} span π; per-tick step = span/clipFrames rad
    // (mann: (π/2)/4 = 225°/s, π/5 = 360°/s); n = round(|delta|/step) ticks — duration
    // proportional to the angle (an earlier revision slerped over one fixed clip length).
    // Tick (FUN_00417010): the drehen clip loops while yaw advances `step` per 0.1 s of
    // sim time; remaining < step·0.5 + 0.001 → exact snap + settle to the `standard`
    // idle loop (:48-50) — never a frozen once-pose. The final facing equals the
    // instant FaceNativeAngle result, so the facing math is unchanged.
    public sealed class TurnToFaceAction : ISaveableEntityAction
    {
        readonly DigglesObject? _obj;
        readonly DigglesAnimController? _c;
        readonly Quaternion _target;
        readonly float _targetNativeYaw;
        readonly int _animset;   // optional 2nd list element of `action rotate` (FUN_00416da0:64-77); -1 = keep current
        readonly int _nativeDirectionSign;
        readonly int _nativeLastAppliedDirectionOrState;
        readonly int _nativeLeftAnimationSlot;
        readonly int _nativeRightAnimationSlot;
        readonly int _nativeTurnClipFrameCount;
        bool _entered;
        float _stepRad;          // per-0.1 s yaw step (+0x208)
        float _dirSign;          // +1 right / -1 left in Unity yaw terms (+0x1c; D6 sign check pending)
        float _remainRad;        // remaining |yaw delta| toward the target
        bool _playedTurnClip;    // we started a looping turn clip → Settle must not leave it looping
        int _calls;              // hard anti-stall cap — see Tick (the troll-cutscene eternal turn)

        public TurnToFaceAction(DigglesObject? obj, DigglesAnimController? c, Quaternion target,
            int animset = -1)
        {
            _obj = obj;
            _c = c;
            _target = target;
            _targetNativeYaw = obj != null ? obj.NativeYawForRotation(target) : 0f;
            _animset = animset;
        }

        public TurnToFaceAction(
            DigglesObject? obj,
            DigglesAnimController? c,
            RotateActionSaveState state)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));
            _obj = obj;
            _c = c;
            _targetNativeYaw = state.AbsoluteTargetYawRadians;
            _target = obj != null
                ? obj.FaceRotationForNativeAngle(_targetNativeYaw)
                : Quaternion.identity;
            _animset = state.AnimationSet;
            _nativeDirectionSign = state.NativeDirectionSign;
            _nativeLastAppliedDirectionOrState = state.NativeLastAppliedDirectionOrState;
            _nativeLeftAnimationSlot = state.NativeLeftAnimationSlot;
            _nativeRightAnimationSlot = state.NativeRightAnimationSlot;
            _nativeTurnClipFrameCount = state.NativeTurnClipFrameCount;
            _entered = state.Started;
            _stepRad = state.AngularStepRadiansPerTick;
            _remainRad = state.RemainingRadians;
            _dirSign = CalculateDirectionSign();
        }

        public RuntimeEntityActionSaveState CaptureSaveState()
        {
            return new RotateActionSaveState(
                _targetNativeYaw,
                System.Math.Max(0f, _stepRad),
                System.Math.Max(0f, _remainRad),
                _entered,
                _animset,
                _nativeDirectionSign,
                _nativeLastAppliedDirectionOrState,
                _nativeLeftAnimationSlot,
                _nativeRightAnimationSlot,
                _nativeTurnClipFrameCount);
        }

        public bool Tick(double dt)
        {
            if (_obj == null) return false;
            // Termination guard: regression — the troll cutscene start_002 showed an eternal
            // turn. The native plan completes in exactly n ≤ ~13 logic ticks (a full π with the
            // 4-frame no-clip preset = round(π/(π/4)·...) ≤ 5); 1200 calls is two orders of
            // magnitude of slack and bounds every ticking regime — zero/NaN dt, per-frame dt,
            // slow-mo `gametime factor`. On expiry: exact snap + settle, never infinite.
            if (++_calls > 1200) { Settle(); return false; }
            if (!_entered)
            {
                _entered = true;
                // Compare the facing directions, not transform.forward — the outer transform
                // carries Q_x(270) so its forward is world +Y (vertical), which flattens to zero
                // and made every turn read as 0°. The visual facing = the per-character
                // SpawnFaceWorld rotated by the rotation's yaw-delta from SpawnRotation.
                float signed = CalculateSignedAngle();
                _dirSign = signed >= 0f ? 1f : -1f;           // +0x1c = (delta ≥ 0) ? +1: −1
                _remainRad = Mathf.Abs(signed) * Mathf.Deg2Rad;

                bool big = _remainRad > 2.3561945f;           // _DAT_00737360 (≤ 3π/4 → 90 pair, FUN_00416c00:22)
                float span = big ? Mathf.PI : Mathf.PI * 0.5f;  // DAT_00737440 / DAT_007371e0
                int frames = 0;
                if (_c != null)
                {
                    if (_animset >= 0) _c.SetActiveAnimset(_animset);   // resolves all slots in that set
                    _playedTurnClip = TurnAnim.Play(_c, signed >= 0f, big);
                    if (_playedTurnClip && _c.Animator != null)
                        frames = _c.Animator.CurrentFrameCount;
                }
                // The no-clip default on all paths — FUN_00416c00 presets +0x214 = 4 (both pairs).
                // Covers: no controller, no animset/alias turn clip (trolls have no drehen slots),
                // a zero-frame clip. The plan below then always yields a finite n.
                if (frames <= 0) frames = 4;
                float nominal = span / frames;                // rad per 0.1 s tick
                float n = Mathf.Floor(_remainRad / nominal + 0.5f);   // (int)(x + 0.5) — FUN_00416c00 round-half-up
                if (!(n >= 1f) || nominal < 0.001f) { Settle(); return false; }   // 0 ticks/NaN → snap now (no spin)
                _stepRad = _remainRad / n;                    // step(+0x208) = |delta| / n (:55-59)
                if (!(_stepRad > 0.0001f)) { Settle(); return false; }   // zero-step/NaN → snap now
            }
            // FUN_00417010: constant angular step per 0.1 s of sim time, then exact snap.
            float step = _stepRad * (float)(dt * 10.0);
            float apply = Mathf.Min(step, _remainRad);        // never overshoot on a coarse dt
            _obj.transform.rotation =
                Quaternion.AngleAxis(_dirSign * apply * Mathf.Rad2Deg, Vector3.up) * _obj.transform.rotation;
            _remainRad -= apply;
            if (!(_remainRad >= _stepRad * 0.5f + 0.001f)) { Settle(); return false; }   //:46-47 (NaN-safe inversion)
            return true;
        }

        float CalculateDirectionSign()
        {
            if (_obj == null) return _nativeDirectionSign < 0 ? -1f : 1f;
            return CalculateSignedAngle() < 0f ? -1f : 1f;
        }

        float CalculateSignedAngle()
        {
            if (_obj == null) return 0f;
            var invSpawn = Quaternion.Inverse(_obj.SpawnRotation);
            Vector3 f0 = (_obj.transform.rotation * invSpawn) * _obj.SpawnFaceWorld;
            Vector3 f1 = (_target * invSpawn) * _obj.SpawnFaceWorld;
            f0.y = 0f;
            f1.y = 0f;
            float signed = f0.sqrMagnitude > 1e-4f && f1.sqrMagnitude > 1e-4f
                ? Vector3.SignedAngle(f0, f1, Vector3.up)
                : 0f;
            return float.IsNaN(signed) ? 0f : signed;
        }

        // Exact final facing + settle to the animset idle `standard` loop — FUN_00417010:48-52
        // (the native applies slot 0 mode 2 then the exact final yaw). Fallback chain for rigs
        // without a `standard` mapping: state `standard` → state `standstill` → bare `standard`
        // clip. If nothing resolves (the troll has no class anim tables and troll.3db has no
        // `standard` clip at all) and a looping turn clip was started, re-issue it once so the
        // drehen loop cannot spin forever — the start_002 eternal-turn-animation symptom. An
        // entity whose previous clip we never touched keeps it (graceful headless degrade).
        void Settle()
        {
            _obj!.transform.rotation = _target;
            if (_c == null) return;
            if (_c.PlayState("standard", DigglesAnimator.AnimMode.Loop)) return;
            if (_c.PlayState("standstill", DigglesAnimator.AnimMode.Loop)) return;
            if (_c.PlayClip("standard", DigglesAnimator.AnimMode.Loop)) return;
            if (_playedTurnClip && _c.Animator != null
                && _c.Animator.CurrentMode == DigglesAnimator.AnimMode.Loop
                && _c.CurrentClip.Length > 0)
                _c.PlayClip(_c.CurrentClip, DigglesAnimator.AnimMode.Once);
        }
    }

    // `action <obj> fly "-range R [-around {x y z}] [-target {x y z}]"` — CAct_Fly. One smooth glide
    // to a destination (the target world point is picked by BuildFlyFromOptions: a random spot within
    // `range` of the around-point, or the entity's current pos, or an explicit -target). The Tcl idle
    // loop re-arms it via the finishcode (state_enable) → perpetual gentle fluttering. The wing-flap
    // is the entity's permanent obj_init loop clip (obw_schmetterling.standard) — this action does not
    // touch the anim. Faithful to the observable behavior; the original's 16-try voxel validation is
    // dropped (we have no flier voxel grid) — the butterfly just glides to the chosen point.
    public sealed class FlyAction : ISaveableEntityAction
    {
        public static float DefaultSpeed = 0.5f;   // world units/sec — gentle flutter (tune by eye)
        public static float DefaultRange = 3f;
        public static float MinClearance = 0.3f;   // keep ≥ this (world units) above the terrain surface

        readonly Transform? _tf;
        readonly Vector3 _target;   // WORLD-space destination
        readonly float _speed;
        Vector3 _velocityOrStepWorld;
        Vector3 _previousPositionWorld;
        readonly float _nativeHeadingOrPhase;
        readonly float _nativeAngularVelocity;
        readonly float _nativeVerticalAccumulator;
        readonly byte _arrivedOrCloseMode;
        readonly byte _swimMode;
        int _tickCount;

        public FlyAction(Transform? tf, Vector3 worldTarget, float speed)
        {
            _tf = tf; _target = worldTarget; _speed = speed > 0f ? speed : DefaultSpeed;
        }

        public FlyAction(Transform? tf, FlyActionSaveState state)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));
            _tf = tf;
            _target = state.TargetWorld;
            _speed = state.Speed;
            _velocityOrStepWorld = state.VelocityOrStepWorld;
            _previousPositionWorld = state.PreviousPositionWorld;
            _nativeHeadingOrPhase = state.NativeHeadingOrPhase;
            _nativeAngularVelocity = state.NativeAngularVelocity;
            _nativeVerticalAccumulator = state.NativeVerticalAccumulator;
            _arrivedOrCloseMode = state.ArrivedOrCloseMode;
            _tickCount = state.TickCount;
            _swimMode = state.SwimMode;
        }

        public RuntimeEntityActionSaveState CaptureSaveState()
        {
            return new FlyActionSaveState(
                _target,
                _velocityOrStepWorld,
                _previousPositionWorld,
                _nativeHeadingOrPhase,
                _nativeAngularVelocity,
                _nativeVerticalAccumulator,
                _arrivedOrCloseMode,
                _tickCount,
                _swimMode,
                _speed);
        }

        public bool Tick(double dt)
        {
            if (_tf == null) return false;
            Vector3 to = _target - _tf.position;
            float d = to.magnitude;
            if (d <= 0.05f) return false;                       // arrived → finishcode re-arms
            Vector3 previous = _tf.position;
            Vector3 delta = to / d * Mathf.Min(_speed * (float)dt, d);
            _tf.position += delta;
            _previousPositionWorld = previous;
            if (dt > 0d) _velocityOrStepWorld = delta / (float)dt;
            _tickCount++;
            return true;
        }
    }
}
