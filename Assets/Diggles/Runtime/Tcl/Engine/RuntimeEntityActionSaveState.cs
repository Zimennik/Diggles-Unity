#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using UnityEngine;

    public interface ISaveableEntityAction : IEntityAction
    {
        /// <summary>
        /// Captures side-effect-free state that can recreate the action without advancing it.
        /// </summary>
        RuntimeEntityActionSaveState CaptureSaveState();
    }

    [Serializable]
    public abstract class RuntimeEntityActionSaveState
    {
    }

    [Serializable]
    public sealed class AnimActionSaveState : RuntimeEntityActionSaveState
    {
        public AnimActionSaveState(string clip, bool started, double remainingSeconds)
        {
            Clip = clip ?? throw new ArgumentNullException(nameof(clip));
            if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds))
                throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
            Started = started;
            RemainingSeconds = remainingSeconds;
        }

        public string Clip { get; }
        public bool Started { get; }
        public double RemainingSeconds { get; }
    }

    [Serializable]
    public sealed class WaitActionSaveState : RuntimeEntityActionSaveState
    {
        public WaitActionSaveState(double remainingSeconds)
        {
            if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds))
                throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
            RemainingSeconds = remainingSeconds;
        }

        public double RemainingSeconds { get; }
    }

    [Serializable]
    public sealed class MoveActionSaveState : RuntimeEntityActionSaveState
    {
        public MoveActionSaveState(
            Vector3 start,
            Vector3 target,
            double durationSeconds,
            double elapsedSeconds,
            bool started)
        {
            if (!Finite(start) || !Finite(target))
                throw new ArgumentOutOfRangeException(nameof(target));
            if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) ||
                durationSeconds < 0d)
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) ||
                elapsedSeconds < 0d)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            Start = start;
            Target = target;
            DurationSeconds = durationSeconds;
            ElapsedSeconds = elapsedSeconds;
            Started = started;
        }

        public Vector3 Start { get; }
        public Vector3 Target { get; }
        public double DurationSeconds { get; }
        public double ElapsedSeconds { get; }
        public bool Started { get; }
        public int RemainingNativeTicks =>
            Math.Max(0, (int)Math.Ceiling(
                Math.Max(0d, DurationSeconds - ElapsedSeconds) / 0.1d));

        private static bool Finite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }

    [Serializable]
    public sealed class RotateActionSaveState : RuntimeEntityActionSaveState
    {
        public RotateActionSaveState(
            float absoluteTargetYawRadians,
            float angularStepRadiansPerTick,
            float remainingRadians,
            bool started,
            int animationSet = -1,
            int nativeDirectionSign = 0,
            int nativeLastAppliedDirectionOrState = 0,
            int nativeLeftAnimationSlot = 0,
            int nativeRightAnimationSlot = 0,
            int nativeTurnClipFrameCount = 0)
        {
            if (!Finite(absoluteTargetYawRadians))
                throw new ArgumentOutOfRangeException(nameof(absoluteTargetYawRadians));
            if (!Finite(angularStepRadiansPerTick) || angularStepRadiansPerTick < 0f)
                throw new ArgumentOutOfRangeException(nameof(angularStepRadiansPerTick));
            if (!Finite(remainingRadians) || remainingRadians < 0f)
                throw new ArgumentOutOfRangeException(nameof(remainingRadians));

            AbsoluteTargetYawRadians = absoluteTargetYawRadians;
            AngularStepRadiansPerTick = angularStepRadiansPerTick;
            RemainingRadians = remainingRadians;
            Started = started;
            AnimationSet = animationSet;
            NativeDirectionSign = nativeDirectionSign;
            NativeLastAppliedDirectionOrState = nativeLastAppliedDirectionOrState;
            NativeLeftAnimationSlot = nativeLeftAnimationSlot;
            NativeRightAnimationSlot = nativeRightAnimationSlot;
            NativeTurnClipFrameCount = nativeTurnClipFrameCount;
        }

        public float AbsoluteTargetYawRadians { get; }
        public float AngularStepRadiansPerTick { get; }
        public float RemainingRadians { get; }
        public bool Started { get; }
        public int AnimationSet { get; }
        public int NativeDirectionSign { get; }
        public int NativeLastAppliedDirectionOrState { get; }
        public int NativeLeftAnimationSlot { get; }
        public int NativeRightAnimationSlot { get; }
        public int NativeTurnClipFrameCount { get; }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [Serializable]
    public sealed class FlyActionSaveState : RuntimeEntityActionSaveState
    {
        public FlyActionSaveState(
            Vector3 targetWorld,
            Vector3 velocityOrStepWorld,
            Vector3 previousPositionWorld,
            float nativeHeadingOrPhase,
            float nativeAngularVelocity,
            float nativeVerticalAccumulator,
            byte arrivedOrCloseMode,
            int tickCount,
            byte swimMode,
            float speed)
        {
            if (!Finite(targetWorld) || !Finite(velocityOrStepWorld) ||
                !Finite(previousPositionWorld))
                throw new ArgumentOutOfRangeException(nameof(targetWorld));
            if (!Finite(nativeHeadingOrPhase) || !Finite(nativeAngularVelocity) ||
                !Finite(nativeVerticalAccumulator))
                throw new ArgumentOutOfRangeException(nameof(nativeHeadingOrPhase));
            if (!Finite(speed) || speed <= 0f)
                throw new ArgumentOutOfRangeException(nameof(speed));

            TargetWorld = targetWorld;
            VelocityOrStepWorld = velocityOrStepWorld;
            PreviousPositionWorld = previousPositionWorld;
            NativeHeadingOrPhase = nativeHeadingOrPhase;
            NativeAngularVelocity = nativeAngularVelocity;
            NativeVerticalAccumulator = nativeVerticalAccumulator;
            ArrivedOrCloseMode = arrivedOrCloseMode;
            TickCount = tickCount;
            SwimMode = swimMode;
            Speed = speed;
        }

        public Vector3 TargetWorld { get; }
        public Vector3 VelocityOrStepWorld { get; }
        public Vector3 PreviousPositionWorld { get; }
        public float NativeHeadingOrPhase { get; }
        public float NativeAngularVelocity { get; }
        public float NativeVerticalAccumulator { get; }
        public byte ArrivedOrCloseMode { get; }
        public int TickCount { get; }
        public byte SwimMode { get; }
        public float Speed { get; }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [Serializable]
    public sealed class NativeAttackActionSaveState : RuntimeEntityActionSaveState
    {
        private readonly System.Collections.ObjectModel.ReadOnlyCollection<int> _leadingRawBits;
        private readonly System.Collections.ObjectModel.ReadOnlyCollection<byte> _deadlineFlags;
        private readonly System.Collections.ObjectModel.ReadOnlyCollection<byte> _frameFlags;
        private readonly System.Collections.ObjectModel.ReadOnlyCollection<int> _impactAndTrailingRawBits;

        public NativeAttackActionSaveState(
            string chainedActionVerb,
            string chainedActionArguments,
            int[] leadingRawBits,
            double remainingSeconds,
            byte[] deadlineFlags,
            int currentFrameOrTick,
            byte[] frameFlags,
            int[] impactAndTrailingRawBits,
            byte trailingFlag)
        {
            if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds))
                throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
            if (leadingRawBits == null || leadingRawBits.Length != 5)
                throw new ArgumentException("A native Attack action has five leading values.",
                    nameof(leadingRawBits));
            if (deadlineFlags == null || deadlineFlags.Length != 3)
                throw new ArgumentException("A native Attack action has three deadline flags.",
                    nameof(deadlineFlags));
            if (frameFlags == null || frameFlags.Length != 4)
                throw new ArgumentException("A native Attack action has four frame flags.",
                    nameof(frameFlags));
            if (impactAndTrailingRawBits == null || impactAndTrailingRawBits.Length != 5)
                throw new ArgumentException("A native Attack action has five impact/trailing values.",
                    nameof(impactAndTrailingRawBits));

            ChainedActionVerb = chainedActionVerb ??
                throw new ArgumentNullException(nameof(chainedActionVerb));
            ChainedActionArguments = chainedActionArguments ??
                throw new ArgumentNullException(nameof(chainedActionArguments));
            _leadingRawBits = Array.AsReadOnly((int[])leadingRawBits.Clone());
            RemainingSeconds = Math.Max(0d, remainingSeconds);
            _deadlineFlags = Array.AsReadOnly((byte[])deadlineFlags.Clone());
            CurrentFrameOrTick = currentFrameOrTick;
            _frameFlags = Array.AsReadOnly((byte[])frameFlags.Clone());
            _impactAndTrailingRawBits =
                Array.AsReadOnly((int[])impactAndTrailingRawBits.Clone());
            TrailingFlag = trailingFlag;
        }

        public string ChainedActionVerb { get; }
        public string ChainedActionArguments { get; }
        public System.Collections.Generic.IReadOnlyList<int> LeadingRawBits => _leadingRawBits;
        public double RemainingSeconds { get; }
        public System.Collections.Generic.IReadOnlyList<byte> DeadlineFlags => _deadlineFlags;
        public int CurrentFrameOrTick { get; }
        public System.Collections.Generic.IReadOnlyList<byte> FrameFlags => _frameFlags;
        public System.Collections.Generic.IReadOnlyList<int> ImpactAndTrailingRawBits =>
            _impactAndTrailingRawBits;
        public byte TrailingFlag { get; }
    }

    [Serializable]
    public sealed class NativeCrawlActionSaveState : RuntimeEntityActionSaveState
    {
        public NativeCrawlActionSaveState(int remainingTicks, int randomYawOrPhaseBits)
        {
            if (remainingTicks < 0) throw new ArgumentOutOfRangeException(nameof(remainingTicks));
            RemainingTicks = remainingTicks;
            RandomYawOrPhaseBits = randomYawOrPhaseBits;
        }

        public int RemainingTicks { get; }
        public int RandomYawOrPhaseBits { get; }
    }

    [Serializable]
    public sealed class NativeAttackBallisticActionSaveState : RuntimeEntityActionSaveState
    {
        private readonly int[] _scheduleValues;
        private readonly int[] _firstRawBits;
        private readonly int[] _transientHandles;
        private readonly int[] _secondRawBits;
        private readonly int[] _lastTargetBits;

        public NativeAttackBallisticActionSaveState(
            int targetObjectId,
            int mode,
            int[] scheduleValues,
            int[] firstRawBits,
            int[] transientHandles,
            int[] secondRawBits,
            byte primaryFlag,
            int[] lastTargetBits,
            byte secondaryFlag,
            byte tertiaryFlag,
            int targetVerticalOffsetBits,
            double remainingSeconds)
        {
            if (targetObjectId < -1) throw new ArgumentOutOfRangeException(nameof(targetObjectId));
            if (scheduleValues == null) throw new ArgumentNullException(nameof(scheduleValues));
            if (firstRawBits == null || firstRawBits.Length != 2)
                throw new ArgumentException("A native AttackBallistic action has two first raw values.",
                    nameof(firstRawBits));
            if (transientHandles == null) throw new ArgumentNullException(nameof(transientHandles));
            if (secondRawBits == null || secondRawBits.Length != 2)
                throw new ArgumentException("A native AttackBallistic action has two second raw values.",
                    nameof(secondRawBits));
            if (lastTargetBits == null || lastTargetBits.Length != 3)
                throw new ArgumentException("A native AttackBallistic action has three last-target values.",
                    nameof(lastTargetBits));
            if (double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds))
                throw new ArgumentOutOfRangeException(nameof(remainingSeconds));

            TargetObjectId = targetObjectId;
            Mode = mode;
            _scheduleValues = (int[])scheduleValues.Clone();
            _firstRawBits = (int[])firstRawBits.Clone();
            _transientHandles = (int[])transientHandles.Clone();
            _secondRawBits = (int[])secondRawBits.Clone();
            PrimaryFlag = primaryFlag;
            _lastTargetBits = (int[])lastTargetBits.Clone();
            SecondaryFlag = secondaryFlag;
            TertiaryFlag = tertiaryFlag;
            TargetVerticalOffsetBits = targetVerticalOffsetBits;
            RemainingSeconds = Math.Max(0d, remainingSeconds);
        }

        public int TargetObjectId { get; }
        public int Mode { get; }
        public int[] CopyScheduleValues() => (int[])_scheduleValues.Clone();
        public int[] CopyFirstRawBits() => (int[])_firstRawBits.Clone();
        public int[] CopyTransientHandles() => (int[])_transientHandles.Clone();
        public int[] CopySecondRawBits() => (int[])_secondRawBits.Clone();
        public byte PrimaryFlag { get; }
        public int[] CopyLastTargetBits() => (int[])_lastTargetBits.Clone();
        public byte SecondaryFlag { get; }
        public byte TertiaryFlag { get; }
        public int TargetVerticalOffsetBits { get; }
        public double RemainingSeconds { get; }
    }

    [Serializable]
    public sealed class NativeLoreTargetSaveState
    {
        public NativeLoreTargetSaveState(int objectId, int offsetOrDistanceBits)
        {
            if (objectId < -1) throw new ArgumentOutOfRangeException(nameof(objectId));
            ObjectId = objectId;
            OffsetOrDistanceBits = offsetOrDistanceBits;
        }

        public int ObjectId { get; }
        public int OffsetOrDistanceBits { get; }
    }

    [Serializable]
    public sealed class NativeLoreActionSaveState : RuntimeEntityActionSaveState
    {
        private readonly NativeLoreTargetSaveState[] _targets;
        private readonly int[] _waypointBits;

        public NativeLoreActionSaveState(
            int gravityBits,
            int frictionBits,
            NativeLoreTargetSaveState[] targets,
            int[] waypointBits)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            if (Array.Exists(targets, target => target == null))
                throw new ArgumentException("A native Lore target cannot be null.", nameof(targets));
            if (waypointBits == null) throw new ArgumentNullException(nameof(waypointBits));
            if (waypointBits.Length % 8 != 0)
                throw new ArgumentException("Each native Lore waypoint has eight values.",
                    nameof(waypointBits));

            GravityBits = gravityBits;
            FrictionBits = frictionBits;
            _targets = (NativeLoreTargetSaveState[])targets.Clone();
            _waypointBits = (int[])waypointBits.Clone();
        }

        public int GravityBits { get; }
        public int FrictionBits { get; }
        public NativeLoreTargetSaveState[] CopyTargets() =>
            (NativeLoreTargetSaveState[])_targets.Clone();
        public int[] CopyWaypointBits() => (int[])_waypointBits.Clone();
    }

    [Serializable]
    public sealed class WalkActionSaveState : RuntimeEntityActionSaveState
    {
        private readonly int[] _nativeAnimationBankStates;
        private readonly int[] _nativeAnimationSlotBits;
        private readonly byte[] _moveFlags;

        public WalkActionSaveState(
            Vector3 targetNative,
            bool hasTarget,
            int[] nativeAnimationBankStates,
            int[] nativeAnimationSlotBits,
            int moveNeededOrApproach,
            byte waving,
            byte dontStop,
            float stopAt,
            byte canClimb,
            byte useObjects,
            int randomPath,
            int randomZ,
            byte[] moveFlags,
            int objectBreakClassId,
            float objectBreakDistance,
            float targetDistance,
            Vector3 climbRotation,
            byte diagonalSqueezeFlag,
            float randomPathWallDistance,
            int walkTimerCounter,
            int tickCounter,
            int speedType,
            string walkTimerCallback,
            string walkCollisionCallback,
            string objectUseCallback,
            string nativeArgument = "",
            int statusOrOpaque = 0)
        {
            if (!Finite(targetNative) || !Finite(stopAt) || !Finite(objectBreakDistance) ||
                !Finite(targetDistance) || !Finite(climbRotation) ||
                !Finite(randomPathWallDistance))
                throw new ArgumentOutOfRangeException(nameof(targetNative));
            if (nativeAnimationBankStates == null || nativeAnimationBankStates.Length != 4)
                throw new ArgumentException("A native Walk action has four animation banks.",
                    nameof(nativeAnimationBankStates));
            if (nativeAnimationSlotBits == null || nativeAnimationSlotBits.Length != 4 * 24 * 5)
                throw new ArgumentException("A native Walk action has 480 animation-slot values.",
                    nameof(nativeAnimationSlotBits));
            if (moveFlags == null || moveFlags.Length != 4)
                throw new ArgumentException("A native Walk action has four move flags.",
                    nameof(moveFlags));

            TargetNative = targetNative;
            HasTarget = hasTarget;
            _nativeAnimationBankStates = (int[])nativeAnimationBankStates.Clone();
            _nativeAnimationSlotBits = (int[])nativeAnimationSlotBits.Clone();
            MoveNeededOrApproach = moveNeededOrApproach;
            Waving = waving;
            DontStop = dontStop;
            StopAt = stopAt;
            CanClimb = canClimb;
            UseObjects = useObjects;
            RandomPath = randomPath;
            RandomZ = randomZ;
            _moveFlags = (byte[])moveFlags.Clone();
            ObjectBreakClassId = objectBreakClassId;
            ObjectBreakDistance = objectBreakDistance;
            TargetDistance = targetDistance;
            ClimbRotation = climbRotation;
            DiagonalSqueezeFlag = diagonalSqueezeFlag;
            RandomPathWallDistance = randomPathWallDistance;
            WalkTimerCounter = walkTimerCounter;
            TickCounter = tickCounter;
            SpeedType = speedType;
            WalkTimerCallback = walkTimerCallback ?? throw new ArgumentNullException(nameof(walkTimerCallback));
            WalkCollisionCallback = walkCollisionCallback ?? throw new ArgumentNullException(nameof(walkCollisionCallback));
            ObjectUseCallback = objectUseCallback ?? throw new ArgumentNullException(nameof(objectUseCallback));
            NativeArgument = nativeArgument ?? throw new ArgumentNullException(nameof(nativeArgument));
            StatusOrOpaque = statusOrOpaque;
        }

        public Vector3 TargetNative { get; }
        public bool HasTarget { get; }
        public int[] CopyNativeAnimationBankStates() => (int[])_nativeAnimationBankStates.Clone();
        public int[] CopyNativeAnimationSlotBits() => (int[])_nativeAnimationSlotBits.Clone();
        public int WalkAnimationSet => _nativeAnimationBankStates[0];
        public int RunAnimationSet => _nativeAnimationBankStates[1];
        public int WaterAnimationSet => _nativeAnimationBankStates[2];
        public int MoveNeededOrApproach { get; }
        public byte Waving { get; }
        public byte DontStop { get; }
        public float StopAt { get; }
        public byte CanClimb { get; }
        public byte UseObjects { get; }
        public int RandomPath { get; }
        public int RandomZ { get; }
        public byte[] CopyMoveFlags() => (byte[])_moveFlags.Clone();
        public int ObjectBreakClassId { get; }
        public float ObjectBreakDistance { get; }
        public float TargetDistance { get; }
        public Vector3 ClimbRotation { get; }
        public byte DiagonalSqueezeFlag { get; }
        public float RandomPathWallDistance { get; }
        public int WalkTimerCounter { get; }
        public int TickCounter { get; }
        public int SpeedType { get; }
        public string WalkTimerCallback { get; }
        public string WalkCollisionCallback { get; }
        public string ObjectUseCallback { get; }
        public string NativeArgument { get; }
        public int StatusOrOpaque { get; }

        public WalkActionSaveState WithTickCounter(int tickCounter)
        {
            return new WalkActionSaveState(
                TargetNative,
                HasTarget,
                _nativeAnimationBankStates,
                _nativeAnimationSlotBits,
                MoveNeededOrApproach,
                Waving,
                DontStop,
                StopAt,
                CanClimb,
                UseObjects,
                RandomPath,
                RandomZ,
                _moveFlags,
                ObjectBreakClassId,
                ObjectBreakDistance,
                TargetDistance,
                ClimbRotation,
                DiagonalSqueezeFlag,
                RandomPathWallDistance,
                WalkTimerCounter,
                tickCounter,
                SpeedType,
                WalkTimerCallback,
                WalkCollisionCallback,
                ObjectUseCallback,
                NativeArgument,
                StatusOrOpaque);
        }

        private static bool Finite(Vector3 value) =>
            Finite(value.x) && Finite(value.y) && Finite(value.z);

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [Serializable]
    public sealed class PendingCombatImpactState
    {
        public PendingCombatImpactState(
            int attackerId,
            int defenderId,
            float damage,
            string trainingSkill,
            float trainingAmount,
            double remainingDelay,
            bool applied = false)
        {
            if (attackerId <= 0) throw new ArgumentOutOfRangeException(nameof(attackerId));
            if (defenderId <= 0) throw new ArgumentOutOfRangeException(nameof(defenderId));
            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage < 0f)
                throw new ArgumentOutOfRangeException(nameof(damage));
            if (float.IsNaN(trainingAmount) || float.IsInfinity(trainingAmount) || trainingAmount < 0f)
                throw new ArgumentOutOfRangeException(nameof(trainingAmount));
            if (double.IsNaN(remainingDelay) || double.IsInfinity(remainingDelay))
                throw new ArgumentOutOfRangeException(nameof(remainingDelay));

            AttackerId = attackerId;
            DefenderId = defenderId;
            Damage = damage;
            TrainingSkill = trainingSkill ?? throw new ArgumentNullException(nameof(trainingSkill));
            TrainingAmount = trainingAmount;
            RemainingDelay = Math.Max(0d, remainingDelay);
            Applied = applied;
        }

        public int AttackerId { get; }
        public int DefenderId { get; }
        public float Damage { get; }
        public string TrainingSkill { get; }
        public float TrainingAmount { get; }
        public double RemainingDelay { get; private set; }
        public bool Applied { get; private set; }

        internal PendingCombatImpactState Copy()
        {
            return new PendingCombatImpactState(
                AttackerId,
                DefenderId,
                Damage,
                TrainingSkill,
                TrainingAmount,
                RemainingDelay,
                Applied);
        }

        internal bool AdvanceAndClaim(double deltaSeconds)
        {
            if (Applied) return false;
            RemainingDelay = Math.Max(0d, RemainingDelay - Math.Max(0d, deltaSeconds));
            if (RemainingDelay > 0d) return false;
            Applied = true;
            return true;
        }
    }

    [Serializable]
    public sealed class TimedImpactActionSaveState : RuntimeEntityActionSaveState
    {
        public TimedImpactActionSaveState(
            RuntimeEntityActionSaveState innerAction,
            PendingCombatImpactState pendingImpact)
        {
            InnerAction = innerAction ?? throw new ArgumentNullException(nameof(innerAction));
            PendingImpact = pendingImpact?.Copy() ??
                throw new ArgumentNullException(nameof(pendingImpact));
        }

        public RuntimeEntityActionSaveState InnerAction { get; }
        public PendingCombatImpactState PendingImpact { get; }
    }
}
