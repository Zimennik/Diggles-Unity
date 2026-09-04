#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using Diggles.Runtime.Sim;
    using UnityEngine;

    // Crawl has no shipped producer or corpus record, so its native-tick countdown remains the
    // compatibility baseline. Lore and AttackBallistic use bound constructors during original-save
    // restoration; the state-only constructors remain side-effect-free round-trip fallbacks.
    public sealed class RestoredNativeCrawlAction : ISaveableEntityAction
    {
        private const double NativeTickSeconds = 0.1d;

        private readonly int _randomYawOrPhaseBits;
        private int _remainingTicks;
        private double _accumulator;

        public RestoredNativeCrawlAction(NativeCrawlActionSaveState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            _remainingTicks = state.RemainingTicks;
            _randomYawOrPhaseBits = state.RandomYawOrPhaseBits;
        }

        public bool DrivesTransform => false;

        public bool Tick(double dt)
        {
            if (_remainingTicks <= 0) return false;
            _accumulator += Math.Max(0d, dt);
            while (_remainingTicks > 0 && _accumulator >= NativeTickSeconds)
            {
                _accumulator -= NativeTickSeconds;
                _remainingTicks--;
            }
            return _remainingTicks > 0;
        }

        public RuntimeEntityActionSaveState CaptureSaveState() =>
            new NativeCrawlActionSaveState(_remainingTicks, _randomYawOrPhaseBits);
    }

    public sealed class RestoredNativeAttackBallisticAction : ISaveableEntityAction
    {
        private const double NewShotDelaySeconds = 0.1d;
        private const float HitDistanceSquared = 0.25f;

        private readonly NativeAttackBallisticActionSaveState _source;
        private readonly DigglesTclEntity? _owner;
        private readonly DigglesTclContext? _context;
        private readonly int[]? _scheduleValues;
        private readonly int[]? _firstRawBits;
        private readonly int[]? _secondRawBits;
        private readonly int[]? _lastTargetBits;
        private readonly List<BallisticShot>? _shots;
        private readonly float _damage;
        private double _remainingSeconds;
        private byte _trailCompletedFlag;
        private Vector3 _lastTarget;
        private bool _started;

        public RestoredNativeAttackBallisticAction(NativeAttackBallisticActionSaveState state)
        {
            _source = state ?? throw new ArgumentNullException(nameof(state));
            _remainingSeconds = state.RemainingSeconds;
        }

        public RestoredNativeAttackBallisticAction(
            NativeAttackBallisticActionSaveState state,
            DigglesTclEntity owner,
            DigglesTclContext context)
            : this(state)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (owner.Handle <= 0 || !context.Entities.TryGetValue(owner.Handle, out DigglesTclEntity? liveOwner) ||
                !ReferenceEquals(owner, liveOwner))
            {
                throw new InvalidOperationException(
                    "A restored AttackBallistic owner must be registered in its candidate context.");
            }
            if (state.Mode is not (0 or 1))
                throw new ArgumentOutOfRangeException(nameof(state), "AttackBallistic mode must be 0 or 1.");

            _scheduleValues = state.CopyScheduleValues();
            for (int index = 0; index < _scheduleValues.Length; index++)
            {
                if (_scheduleValues[index] < -1)
                    throw new ArgumentOutOfRangeException(nameof(state), "AttackBallistic fire stamps cannot be below -1.");
            }
            _firstRawBits = state.CopyFirstRawBits();
            _secondRawBits = state.CopySecondRawBits();
            _lastTargetBits = state.CopyLastTargetBits();
            _damage = BitConverter.Int32BitsToSingle(_secondRawBits[1]);
            if (!Finite(_damage) || _damage < 0f)
                throw new ArgumentOutOfRangeException(nameof(state), "AttackBallistic damage must be finite and non-negative.");

            _lastTarget = new Vector3(
                BitConverter.Int32BitsToSingle(_lastTargetBits[0]),
                BitConverter.Int32BitsToSingle(_lastTargetBits[1]),
                BitConverter.Int32BitsToSingle(_lastTargetBits[2]));
            if (!Finite(_lastTarget))
                throw new ArgumentOutOfRangeException(nameof(state), "AttackBallistic last-target position must be finite.");
            float verticalOffset = BitConverter.Int32BitsToSingle(state.TargetVerticalOffsetBits);
            if (!Finite(verticalOffset))
                throw new ArgumentOutOfRangeException(nameof(state), "AttackBallistic vertical offset must be finite.");

            _trailCompletedFlag = state.SecondaryFlag;
            int[] handles = state.CopyTransientHandles();
            _shots = new List<BallisticShot>(handles.Length);
            for (int index = 0; index < handles.Length; index++)
            {
                _shots.Add(new BallisticShot(
                    handles[index],
                    _lastTarget,
                    _trailCompletedFlag != 0,
                    0d));
            }
        }

        public bool DrivesTransform => false;

        public bool Tick(double dt)
        {
            if (_context != null) return TickContinuation(dt);
            if (_remainingSeconds <= 0d) return false;
            _remainingSeconds = Math.Max(0d, _remainingSeconds - Math.Max(0d, dt));
            return _remainingSeconds > 0d;
        }

        public RuntimeEntityActionSaveState CaptureSaveState()
        {
            if (_context == null)
            {
                return new NativeAttackBallisticActionSaveState(
                    _source.TargetObjectId,
                    _source.Mode,
                    _source.CopyScheduleValues(),
                    _source.CopyFirstRawBits(),
                    _source.CopyTransientHandles(),
                    _source.CopySecondRawBits(),
                    _source.PrimaryFlag,
                    _source.CopyLastTargetBits(),
                    _source.SecondaryFlag,
                    _source.TertiaryFlag,
                    _source.TargetVerticalOffsetBits,
                    _remainingSeconds);
            }

            List<BallisticShot> shots = _shots ??
                throw new InvalidOperationException("Bound AttackBallistic shot state is missing.");
            var transientHandles = new int[shots.Count];
            for (int index = 0; index < transientHandles.Length; index++)
                transientHandles[index] = shots[index].Handle;
            return new NativeAttackBallisticActionSaveState(
                _source.TargetObjectId,
                _source.Mode,
                (int[])(_scheduleValues ?? throw new InvalidOperationException()).Clone(),
                (int[])(_firstRawBits ?? throw new InvalidOperationException()).Clone(),
                transientHandles,
                (int[])(_secondRawBits ?? throw new InvalidOperationException()).Clone(),
                _source.PrimaryFlag,
                (int[])(_lastTargetBits ?? throw new InvalidOperationException()).Clone(),
                _trailCompletedFlag,
                _source.TertiaryFlag,
                _source.TargetVerticalOffsetBits,
                _remainingSeconds);
        }

        private bool TickContinuation(double dt)
        {
            DigglesTclContext context = _context ??
                throw new InvalidOperationException("Bound AttackBallistic context is missing.");
            DigglesTclEntity owner = _owner ??
                throw new InvalidOperationException("Bound AttackBallistic owner is missing.");
            List<BallisticShot> shots = _shots ??
                throw new InvalidOperationException("Bound AttackBallistic shot state is missing.");
            int[] scheduleValues = _scheduleValues ??
                throw new InvalidOperationException("Bound AttackBallistic schedule is missing.");
            int[] secondRawBits = _secondRawBits ??
                throw new InvalidOperationException("Bound AttackBallistic tick state is missing.");

            if (!_started)
            {
                _started = true;
                owner.BallisticResult = 0;
            }
            if (!context.Entities.TryGetValue(
                    _source.TargetObjectId,
                    out DigglesTclEntity? target))
            {
                shots.Clear();
                _remainingSeconds = 0d;
                return false;
            }
            if (_remainingSeconds <= 0d && shots.Count == 0) return false;

            secondRawBits[0] = unchecked(secondRawBits[0] + 1);
            ResolveReadyShots(Math.Max(0d, dt), owner, target, context, shots);
            FireScheduledShots(context, target, scheduleValues, shots);
            _remainingSeconds = Math.Max(0d, _remainingSeconds - Math.Max(0d, dt));
            return _remainingSeconds > 0d || shots.Count > 0;
        }

        private void ResolveReadyShots(
            double dt,
            DigglesTclEntity owner,
            DigglesTclEntity target,
            DigglesTclContext context,
            List<BallisticShot> shots)
        {
            for (int index = shots.Count - 1; index >= 0; index--)
            {
                BallisticShot shot = shots[index];
                shot.RemainingDelay = Math.Max(0d, shot.RemainingDelay - dt);
                if (shot.RemainingDelay > 0d)
                {
                    shots[index] = shot;
                    continue;
                }

                shots.RemoveAt(index);
                if (!shot.TrailCompleted ||
                    (target.Position - shot.LastTarget).sqrMagnitude >= HitDistanceSquared)
                {
                    continue;
                }

                var impact = new PendingCombatImpactState(
                    owner.Handle,
                    target.Handle,
                    _damage,
                    string.Empty,
                    0f,
                    0d);
                var timedImpact = new TimedImpactAction(new WaitAction(0d), context, impact);
                timedImpact.Tick(0d);
                owner.BallisticResult = 1;
            }
        }

        private void FireScheduledShots(
            DigglesTclContext context,
            DigglesTclEntity target,
            int[] scheduleValues,
            List<BallisticShot> shots)
        {
            double absoluteFireStamp = (context.Sim?.GameTime ?? 0d) * 10d;
            for (int index = 0; index < scheduleValues.Length; index++)
            {
                int fireStamp = scheduleValues[index];
                if (fireStamp <= 0 || fireStamp > absoluteFireStamp) continue;

                scheduleValues[index] = -1;
                _lastTarget = target.Position;
                int[] lastTargetBits = _lastTargetBits ??
                    throw new InvalidOperationException("Bound AttackBallistic target state is missing.");
                lastTargetBits[0] = BitConverter.SingleToInt32Bits(_lastTarget.x);
                lastTargetBits[1] = BitConverter.SingleToInt32Bits(_lastTarget.y);
                lastTargetBits[2] = BitConverter.SingleToInt32Bits(_lastTarget.z);
                _trailCompletedFlag = 1;
                shots.Add(new BallisticShot(0, _lastTarget, true, NewShotDelaySeconds));
            }
        }

        private static bool Finite(Vector3 value) =>
            Finite(value.x) && Finite(value.y) && Finite(value.z);

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private struct BallisticShot
        {
            internal BallisticShot(
                int handle,
                Vector3 lastTarget,
                bool trailCompleted,
                double remainingDelay)
            {
                Handle = handle;
                LastTarget = lastTarget;
                TrailCompleted = trailCompleted;
                RemainingDelay = remainingDelay;
            }

            internal int Handle { get; }
            internal Vector3 LastTarget { get; }
            internal bool TrailCompleted { get; }
            internal double RemainingDelay { get; set; }
        }
    }

    public sealed class RestoredNativeLoreAction : ISaveableEntityAction
    {
        private readonly NativeLoreActionSaveState _state;
        private readonly DigglesTclEntity? _owner;
        private readonly LoreTrackSystem? _tracks;
        private readonly LoreTrackTrailer[]? _trailers;
        private readonly float _gravity;
        private readonly float _friction;
        private double _accumulator;
        private bool _completed;

        public RestoredNativeLoreAction(NativeLoreActionSaveState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public RestoredNativeLoreAction(
            NativeLoreActionSaveState state,
            DigglesTclEntity owner,
            DigglesTclContext context)
            : this(state)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (owner.Handle <= 0 || !context.Entities.TryGetValue(owner.Handle, out DigglesTclEntity? liveOwner) ||
                !ReferenceEquals(owner, liveOwner))
            {
                throw new InvalidOperationException(
                    "A restored Lore owner must be registered in its candidate context.");
            }

            _tracks = context.LoreTracks;
            _gravity = BitConverter.Int32BitsToSingle(state.GravityBits);
            _friction = BitConverter.Int32BitsToSingle(state.FrictionBits);
            if (!Finite(_gravity))
                throw new ArgumentOutOfRangeException(nameof(state), "Lore gravity must be finite.");
            if (!Finite(_friction))
                throw new ArgumentOutOfRangeException(nameof(state), "Lore friction must be finite.");

            NativeLoreTargetSaveState[] targets = state.CopyTargets();
            _trailers = new LoreTrackTrailer[targets.Length];
            for (int index = 0; index < targets.Length; index++)
            {
                NativeLoreTargetSaveState target = targets[index];
                if (!context.Entities.ContainsKey(target.ObjectId))
                {
                    throw new InvalidOperationException(
                        $"Lore trailer entity {target.ObjectId} for owner {owner.Handle} is missing.");
                }
                _trailers[index] = new LoreTrackTrailer(
                    target.ObjectId,
                    BitConverter.Int32BitsToSingle(target.OffsetOrDistanceBits));
            }
        }

        public bool DrivesTransform => _tracks != null;

        public bool Tick(double dt)
        {
            if (_tracks == null) return true;
            if (_completed) return false;

            DigglesTclEntity owner = _owner ??
                throw new InvalidOperationException("Bound Lore owner is missing.");
            LoreTrackTrailer[] trailers = _trailers ??
                throw new InvalidOperationException("Bound Lore trailer state is missing.");
            if (!_tracks.TryGetRecord(owner.Handle, out LoreTrackRecord record))
                throw new InvalidOperationException($"Lore track owner {owner.Handle} has no registry record.");
            if (record.Progress >= record.TotalLength)
                return Complete(owner.Handle, trailers);

            _accumulator += Math.Max(0d, dt);
            while (_accumulator >= LoreTrackSystem.NativeTickSeconds)
            {
                _accumulator -= LoreTrackSystem.NativeTickSeconds;
                if (!_tracks.Tick(owner.Handle, _gravity, _friction, trailers))
                    return Complete(owner.Handle, trailers);
            }
            return true;
        }

        public RuntimeEntityActionSaveState CaptureSaveState() =>
            new NativeLoreActionSaveState(
                _state.GravityBits,
                _state.FrictionBits,
                _state.CopyTargets(),
                _state.CopyWaypointBits());

        private bool Complete(int ownerId, IReadOnlyList<LoreTrackTrailer> trailers)
        {
            if (!_completed)
            {
                _completed = true;
                _tracks?.Complete(ownerId, trailers);
            }
            return false;
        }

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
