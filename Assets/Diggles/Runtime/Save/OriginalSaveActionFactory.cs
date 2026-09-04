#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;
    using UnityEngine.Scripting;

    public interface IOriginalSaveActionFactory
    {
        /// <summary>
        /// Creates a candidate action without installing or advancing it and without evaluating Tcl.
        /// </summary>
        OriginalSavePreparedAction CreateCandidate(
            OriginalSaveRuntimeActionSnapshot source,
            DigglesTclEntity owner,
            DigglesTclContext candidateContext,
            double restoredGameTime);
    }

    public sealed class OriginalSaveActionFactory : IOriginalSaveActionFactory
    {
        private readonly IRuntimeSaveableActionFactory _runtimeFactory;

        [Preserve]
        public OriginalSaveActionFactory(IRuntimeSaveableActionFactory runtimeFactory)
        {
            _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        }

        public OriginalSavePreparedAction CreateCandidate(
            OriginalSaveRuntimeActionSnapshot source,
            DigglesTclEntity owner,
            DigglesTclContext candidateContext,
            double restoredGameTime)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (candidateContext == null) throw new ArgumentNullException(nameof(candidateContext));
            if (source.OwnerObjectId != owner.Handle)
            {
                throw new InvalidOperationException(
                    $"Native action owner {source.OwnerObjectId} does not match candidate entity {owner.Handle}.");
            }

            if (!source.HasActiveAction)
            {
                return new OriginalSavePreparedAction(
                    source.Type,
                    owner.Handle,
                    null,
                    "",
                    "",
                    0,
                    null,
                    null);
            }

            OriginalSaveActionCommonState common = source.Common ??
                throw new InvalidOperationException(
                    $"Native action {source.Type} for owner {owner.Handle} has no common state.");
            RuntimeEntityActionSaveState state = source.Payload switch
            {
                OriginalSaveWaitActionPayload =>
                    new WaitActionSaveState(source.RemainingSecondsAt(restoredGameTime)),
                OriginalSaveAnimActionPayload =>
                    new AnimActionSaveState(
                        common.Argument.Text,
                        true,
                        source.RemainingSecondsAt(restoredGameTime)),
                OriginalSaveMoveActionPayload move => CreateMoveState(move, owner),
                OriginalSaveRotateActionPayload rotate => CreateRotateState(rotate, owner),
                OriginalSaveFlyActionPayload fly => CreateFlyState(fly, candidateContext, owner),
                OriginalSaveWalkActionPayload walk => CreateWalkState(walk, owner),
                OriginalSaveAttackActionPayload attack => CreateAttackState(
                    attack,
                    source.RemainingSecondsAt(restoredGameTime)),
                OriginalSaveCrawlActionPayload crawl => CreateCrawlState(crawl, owner),
                OriginalSaveAttackBallisticActionPayload ballistic =>
                    CreateAttackBallisticState(
                        ballistic,
                        source.RemainingSecondsAt(restoredGameTime),
                        owner),
                OriginalSaveLoreActionPayload lore => CreateLoreState(lore),
                _ => throw new NotSupportedException(
                    $"Runtime reconstruction of native action type {source.Type} is not implemented."),
            };
            IEntityAction action = state switch
            {
                NativeAttackBallisticActionSaveState ballistic =>
                    new RestoredNativeAttackBallisticAction(ballistic, owner, candidateContext),
                NativeLoreActionSaveState lore =>
                    new RestoredNativeLoreAction(lore, owner, candidateContext),
                _ => _runtimeFactory.Create(state, owner, candidateContext),
            };
            return new OriginalSavePreparedAction(
                source.Type,
                owner.Handle,
                action,
                common.FinishCode.Text,
                common.BreakCode.Text,
                common.StatusOrOpaque,
                source.Type == OriginalSaveActionType.Walk ? 2 : null,
                source.Type == OriginalSaveActionType.AttackBallistic ? 0 : null);
        }

        private static MoveActionSaveState CreateMoveState(
            OriginalSaveMoveActionPayload source,
            DigglesTclEntity owner)
        {
            if (source.RemainingNativeTicks < 0)
                throw new InvalidOperationException(
                    $"Native Move action for owner {owner.Handle} has a negative tick count.");
            Vector3 target = ReadVector(source.Vectors, 3, "Move target", owner.Handle);
            return new MoveActionSaveState(
                owner.Position,
                target,
                source.RemainingNativeTicks * 0.1d,
                0d,
                true);
        }

        private static RotateActionSaveState CreateRotateState(
            OriginalSaveRotateActionPayload source,
            DigglesTclEntity owner)
        {
            float targetYaw = source.AbsoluteTargetYaw.AsSingle;
            float step = Math.Abs(source.AngularStep.AsSingle);
            ValidateFinite(targetYaw, "Rotate target yaw", owner.Handle);
            ValidateFinite(step, "Rotate angular step", owner.Handle);
            float remaining = Math.Abs(Mathf.DeltaAngle(
                owner.Rotation.y * Mathf.Rad2Deg,
                targetYaw * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
            return new RotateActionSaveState(
                targetYaw,
                step,
                remaining,
                true,
                -1,
                source.DirectionSign,
                source.LastAppliedDirectionOrState,
                source.LeftAnimationSlot,
                source.RightAnimationSlot,
                source.TurnClipFrameCount);
        }

        private static FlyActionSaveState CreateFlyState(
            OriginalSaveFlyActionPayload source,
            DigglesTclContext context,
            DigglesTclEntity owner)
        {
            Vector3 nativeTarget = ReadVector(source.MotionValues, 0, "Fly target", owner.Handle);
            Vector3 targetWorld = context.SpawnParent != null
                ? context.SpawnParent.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nativeTarget))
                : nativeTarget;
            Vector3 nativeVelocity = ReadVector(
                source.MotionValues,
                3,
                "Fly velocity/step",
                owner.Handle);
            Vector3 nativePrevious = ReadVector(
                source.MotionValues,
                6,
                "Fly previous position",
                owner.Handle);
            Vector3 velocityWorld = context.SpawnParent != null
                ? context.SpawnParent.TransformVector(RuntimeWorldSpawner.NativeToLocal(nativeVelocity))
                : nativeVelocity;
            Vector3 previousWorld = context.SpawnParent != null
                ? context.SpawnParent.TransformPoint(RuntimeWorldSpawner.NativeToLocal(nativePrevious))
                : nativePrevious;
            float heading = source.MotionValues[9].AsSingle;
            float angularVelocity = source.MotionValues[10].AsSingle;
            float verticalAccumulator = source.MotionValues[11].AsSingle;
            ValidateFinite(heading, "Fly heading/phase", owner.Handle);
            ValidateFinite(angularVelocity, "Fly angular velocity", owner.Handle);
            ValidateFinite(verticalAccumulator, "Fly vertical accumulator", owner.Handle);
            return new FlyActionSaveState(
                targetWorld,
                velocityWorld,
                previousWorld,
                heading,
                angularVelocity,
                verticalAccumulator,
                source.ArrivedOrCloseMode,
                source.TickCount,
                source.SwimMode,
                FlyAction.DefaultSpeed);
        }

        private static WalkActionSaveState CreateWalkState(
            OriginalSaveWalkActionPayload source,
            DigglesTclEntity owner)
        {
            var bankStates = new int[source.AnimationBanks.Count];
            var slotBits = new int[source.AnimationBanks.Count * 24 * 5];
            int slotOffset = 0;
            for (int bank = 0; bank < source.AnimationBanks.Count; bank++)
            {
                OriginalSaveWalkAnimationBank sourceBank = source.AnimationBanks[bank];
                bankStates[bank] = sourceBank.BankStateOrAnimationSet;
                for (int value = 0; value < sourceBank.SlotValues.Count; value++)
                    slotBits[slotOffset++] = sourceBank.SlotValues[value].Bits;
            }

            Vector3 target = ReadVector(
                source.TargetAndDistanceValues,
                1,
                "Walk target",
                owner.Handle);
            float stopAt = source.TargetAndDistanceValues[0].AsSingle;
            float objectBreakDistance = source.ObjectBreakDistance.AsSingle;
            float targetDistance = source.TargetDistance.AsSingle;
            Vector3 climbRotation = new(
                source.ClimbRotationX.AsSingle,
                source.ClimbRotationY.AsSingle,
                source.ClimbRotationZ.AsSingle);
            float randomPathWallDistance = source.RandomPathWallDistance.AsSingle;
            ValidateFinite(stopAt, "Walk stop-at value", owner.Handle);
            ValidateFinite(objectBreakDistance, "Walk object-break distance", owner.Handle);
            ValidateFinite(targetDistance, "Walk target distance", owner.Handle);
            ValidateFinite(climbRotation.x, "Walk climb rotation X", owner.Handle);
            ValidateFinite(climbRotation.y, "Walk climb rotation Y", owner.Handle);
            ValidateFinite(climbRotation.z, "Walk climb rotation Z", owner.Handle);
            ValidateFinite(randomPathWallDistance, "Walk random-path wall distance", owner.Handle);

            var moveFlags = new byte[source.MoveFlags.Count];
            for (int index = 0; index < moveFlags.Length; index++)
                moveFlags[index] = source.MoveFlags[index];
            return new WalkActionSaveState(
                target,
                true,
                bankStates,
                slotBits,
                source.MoveNeededOrApproach,
                source.Waving,
                source.DontStop,
                stopAt,
                source.CanClimb,
                source.UseObjects,
                source.RandomPath,
                source.RandomZ,
                moveFlags,
                source.ObjectBreakClassId,
                objectBreakDistance,
                targetDistance,
                climbRotation,
                source.DiagonalSqueezeFlag,
                randomPathWallDistance,
                source.WalkTimerCounter,
                source.TickCounter,
                source.SpeedType,
                source.WalkTimerCallback.Text,
                source.WalkCollisionCallback.Text,
                source.ObjectUseCallback.Text);
        }

        private static NativeAttackActionSaveState CreateAttackState(
            OriginalSaveAttackActionPayload source,
            double remainingSeconds)
        {
            var leadingBits = new int[source.LeadingRawValues.Count];
            for (int index = 0; index < leadingBits.Length; index++)
                leadingBits[index] = source.LeadingRawValues[index].Bits;
            var deadlineFlags = new byte[source.DeadlineFlags.Count];
            for (int index = 0; index < deadlineFlags.Length; index++)
                deadlineFlags[index] = source.DeadlineFlags[index];
            var frameFlags = new byte[source.FrameFlags.Count];
            for (int index = 0; index < frameFlags.Length; index++)
                frameFlags[index] = source.FrameFlags[index];
            var impactBits = new int[source.ImpactAndTrailingRawValues.Count];
            for (int index = 0; index < impactBits.Length; index++)
                impactBits[index] = source.ImpactAndTrailingRawValues[index].Bits;
            return new NativeAttackActionSaveState(
                source.ChainedActionVerb.Text,
                source.ChainedActionArguments.Text,
                leadingBits,
                remainingSeconds,
                deadlineFlags,
                source.CurrentFrameOrTick,
                frameFlags,
                impactBits,
                source.TrailingFlag);
        }

        private static NativeCrawlActionSaveState CreateCrawlState(
            OriginalSaveCrawlActionPayload source,
            DigglesTclEntity owner)
        {
            if (source.RemainingTicks < 0)
                throw new InvalidOperationException(
                    $"Native Crawl action for owner {owner.Handle} has a negative tick count.");
            return new NativeCrawlActionSaveState(
                source.RemainingTicks,
                source.RandomYawOrPhase.Bits);
        }

        private static NativeAttackBallisticActionSaveState CreateAttackBallisticState(
            OriginalSaveAttackBallisticActionPayload source,
            double remainingSeconds,
            DigglesTclEntity owner)
        {
            if (source.Mode is not (0 or 1))
            {
                throw new InvalidOperationException(
                    $"Native AttackBallistic mode {source.Mode} for owner {owner.Handle} is not 0 or 1.");
            }
            for (int index = 0; index < source.ScheduleValues.Count; index++)
            {
                if (source.ScheduleValues[index] < -1)
                {
                    throw new InvalidOperationException(
                        $"Native AttackBallistic schedule value {source.ScheduleValues[index]} for owner {owner.Handle} is invalid.");
                }
            }
            ValidateFinite(source.Damage, "AttackBallistic damage", owner.Handle);
            if (source.Damage < 0f)
            {
                throw new InvalidOperationException(
                    $"Native AttackBallistic damage for owner {owner.Handle} is negative.");
            }
            ValidateFinite(source.LastTargetX, "AttackBallistic last-target X", owner.Handle);
            ValidateFinite(source.LastTargetY, "AttackBallistic last-target Y", owner.Handle);
            ValidateFinite(source.LastTargetZ, "AttackBallistic last-target Z", owner.Handle);
            ValidateFinite(
                source.TargetVerticalOffsetValue,
                "AttackBallistic target vertical offset",
                owner.Handle);
            return new NativeAttackBallisticActionSaveState(
                source.TargetObjectId,
                source.Mode,
                Copy(source.ScheduleValues),
                CopyBits(source.FirstRawValues),
                Copy(source.TransientHandles),
                CopyBits(source.SecondRawValues),
                source.HitReactionFlag,
                CopyBits(source.LastTarget),
                source.TrailCompletedFlag,
                source.IgnoreTerrainCollisionFlag,
                source.TargetVerticalOffset.Bits,
                remainingSeconds);
        }

        private static NativeLoreActionSaveState CreateLoreState(
            OriginalSaveLoreActionPayload source)
        {
            var targets = new NativeLoreTargetSaveState[source.Targets.Count];
            for (int index = 0; index < targets.Length; index++)
            {
                OriginalSaveLoreTarget target = source.Targets[index];
                targets[index] = new NativeLoreTargetSaveState(
                    target.ObjectId,
                    target.OffsetOrDistance.Bits);
            }

            var waypointBits = new int[source.Waypoints.Count * 8];
            int offset = 0;
            foreach (OriginalSaveLoreWaypoint waypoint in source.Waypoints)
            {
                foreach (OriginalSaveRaw32 value in waypoint.Values)
                    waypointBits[offset++] = value.Bits;
            }
            return new NativeLoreActionSaveState(
                source.Gravity.Bits,
                source.Friction.Bits,
                targets,
                waypointBits);
        }

        private static int[] Copy(System.Collections.Generic.IReadOnlyList<int> source)
        {
            var result = new int[source.Count];
            for (int index = 0; index < result.Length; index++) result[index] = source[index];
            return result;
        }

        private static int[] CopyBits(
            System.Collections.Generic.IReadOnlyList<OriginalSaveRaw32> source)
        {
            var result = new int[source.Count];
            for (int index = 0; index < result.Length; index++) result[index] = source[index].Bits;
            return result;
        }

        private static Vector3 ReadVector(
            System.Collections.Generic.IReadOnlyList<OriginalSaveRaw32> values,
            int offset,
            string label,
            int ownerObjectId)
        {
            var value = new Vector3(
                values[offset].AsSingle,
                values[offset + 1].AsSingle,
                values[offset + 2].AsSingle);
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
                float.IsNaN(value.y) || float.IsInfinity(value.y) ||
                float.IsNaN(value.z) || float.IsInfinity(value.z))
            {
                throw new InvalidOperationException(
                    $"Native {label} for owner {ownerObjectId} is not finite.");
            }
            return value;
        }

        private static void ValidateFinite(float value, string label, int ownerObjectId)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException(
                    $"Native {label} for owner {ownerObjectId} is not finite.");
        }
    }

    public sealed class OriginalSavePreparedAction
    {
        internal OriginalSavePreparedAction(
            OriginalSaveActionType sourceType,
            int ownerObjectId,
            IEntityAction? action,
            string finishCode,
            string breakCode,
            int statusOrOpaque,
            int? initialWalkResult,
            int? initialBallisticResult)
        {
            SourceType = sourceType;
            OwnerObjectId = ownerObjectId;
            Action = action;
            FinishCode = finishCode ?? throw new ArgumentNullException(nameof(finishCode));
            BreakCode = breakCode ?? throw new ArgumentNullException(nameof(breakCode));
            StatusOrOpaque = statusOrOpaque;
            InitialWalkResult = initialWalkResult;
            InitialBallisticResult = initialBallisticResult;
        }

        public OriginalSaveActionType SourceType { get; }
        public int OwnerObjectId { get; }
        public IEntityAction? Action { get; }
        public string FinishCode { get; }
        public string BreakCode { get; }
        public int StatusOrOpaque { get; }
        public int? InitialWalkResult { get; }
        public int? InitialBallisticResult { get; }
    }
}
