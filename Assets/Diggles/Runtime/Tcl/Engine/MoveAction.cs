#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using UnityEngine;

    // CAct_Move — the engine's straight-line glide (`action <obj> move "{x y z} speed"`),
    // used by the sequencer's `sq_object move` (sequencer.tcl:939-945; start_002's Hand of
    // God descent). Faithful to the decompile:
    //
    // • ctor FUN_00416680 zeroes the state; Init FUN_00416720 parses the 2-element Tcl list
    // {targetVec speed}, captures the object's current position as the start (+0x1c..0x24),
    // the target (+0x28..0x30), clamps the speed, then computes
    // steps = round(dist / speed + 0.5) (stored at +0x40)
    // stepVec = delta / steps (+0x34..0x3c)
    // • Tick FUN_00416a30 decrements the step counter and adds stepVec to the object's
    // position (via CObj::SetPos FUN_005cd8e0) until the counter runs out.
    //
    // One native step = one SrvDoThink logic tick = 0.1 s of game time (FUN_006b4a30 paces on
    // `nextThink += DAT_007371a8`, DAT_007371a8 = 0.1 — clock-worktime-re.md §1.1). So `speed`
    // is native units per 0.1 s tick, and the whole glide lasts steps × 0.1 game-seconds. The
    // port runs it as a continuous lerp over that same duration (the per-tick quantization is
    // invisible at 40 Hz sim steps) — and since the sim feeds actions the factor-scaled dt,
    // `gametime factor` slow-mo slows the glide exactly like the original.
    //
    // Headless-safe: the native Position always advances; a bound GameObject is posed through
    // the spawner (same axis conversion as set_pos).
    public sealed class MoveAction : ISaveableEntityAction
    {
        const double TickSeconds = 0.1;     // SrvDoThink logic tick (DAT_007371a8)
        const float MinSpeed = 1e-3f;       // Init clamps speed to a positive floor (DAT_00736fe0)

        private readonly DigglesTclContext _ctx;
        private readonly DigglesTclEntity _ent;
        private readonly Vector3 _target;
        private readonly float _speed;

        private bool _started;
        private Vector3 _start;
        private double _duration;                   // steps × 0.1 s of game time
        private double _elapsed;

        public MoveAction(DigglesTclContext ctx, DigglesTclEntity ent, Vector3 target, float speed)
        {
            _ctx = ctx ?? throw new System.ArgumentNullException(nameof(ctx));
            _ent = ent ?? throw new System.ArgumentNullException(nameof(ent));
            _target = target;
            _speed = speed;
        }

        public MoveAction(
            DigglesTclContext ctx,
            DigglesTclEntity ent,
            MoveActionSaveState state)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));
            _ctx = ctx ?? throw new System.ArgumentNullException(nameof(ctx));
            _ent = ent ?? throw new System.ArgumentNullException(nameof(ent));
            _target = state.Target;
            _speed = MinSpeed;
            _started = state.Started;
            _start = state.Start;
            _duration = state.DurationSeconds;
            _elapsed = state.ElapsedSeconds;
        }

        public RuntimeEntityActionSaveState CaptureSaveState()
        {
            return new MoveActionSaveState(
                _start,
                _target,
                _duration,
                _elapsed,
                _started);
        }

        public bool Tick(double dt)
        {
            if (!_started)
            {
                _started = true;
                _start = _ent.Position;                     // Init reads obj+0xc..0x14 at creation
                float dist = (_target - _start).magnitude;
                float speed = Mathf.Max(_speed, MinSpeed);
                int steps = Mathf.Max(1, Mathf.RoundToInt(dist / speed + 0.5f));
                _duration = steps * TickSeconds;
            }
            _elapsed += dt;
            float t = _duration > 0.0 ? Mathf.Clamp01((float)(_elapsed / _duration)) : 1f;
            Apply(Vector3.Lerp(_start, _target, t));
            return _elapsed < _duration;
        }

        void Apply(Vector3 nativePos)
        {
            _ent.Position = nativePos;
            if (_ent.GameObject != null && _ctx.EntitySpawner != null)
                _ctx.EntitySpawner.ApplyPosition(_ent.GameObject, nativePos);
        }
    }
}
