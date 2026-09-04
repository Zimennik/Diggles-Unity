#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Text;

    public enum OriginalSaveActionType
    {
        Null = 0,
        Wait = 1,
        LegacySlide = 2,
        Anim = 3,
        Crawl = 4,
        Rotate = 5,
        Attack = 6,
        AttackBallistic = 7,
        Fly = 8,
        Lore = 9,
        Walk = 10,
        Move = 11,
    }

    public readonly struct OriginalSaveRaw32 : IEquatable<OriginalSaveRaw32>
    {
        public OriginalSaveRaw32(int bits)
        {
            Bits = bits;
        }

        public int Bits { get; }
        public float AsSingle => BitConverter.Int32BitsToSingle(Bits);

        public bool Equals(OriginalSaveRaw32 other) => Bits == other.Bits;
        public override bool Equals(object? obj) =>
            obj is OriginalSaveRaw32 other && Equals(other);
        public override int GetHashCode() => Bits;
        public override string ToString() => $"0x{unchecked((uint)Bits):X8}";
    }

    public sealed class OriginalSaveNativeString
    {
        private static readonly Encoding Windows1252 = Encoding.GetEncoding(1252);
        private readonly byte[] _bytesIncludingTerminator;

        internal OriginalSaveNativeString(byte[] bytesIncludingTerminator)
        {
            if (bytesIncludingTerminator == null)
                throw new ArgumentNullException(nameof(bytesIncludingTerminator));
            if (bytesIncludingTerminator.Length == 0 || bytesIncludingTerminator[^1] != 0)
                throw new ArgumentException(
                    "A native string must include its terminating NUL byte.",
                    nameof(bytesIncludingTerminator));

            _bytesIncludingTerminator = (byte[])bytesIncludingTerminator.Clone();
            Text = Windows1252.GetString(
                _bytesIncludingTerminator,
                0,
                _bytesIncludingTerminator.Length - 1);
        }

        public int DeclaredLength => _bytesIncludingTerminator.Length;
        public string Text { get; }

        public byte[] CopyBytesIncludingTerminator() =>
            (byte[])_bytesIncludingTerminator.Clone();
    }

    public sealed class OriginalSaveActionCommonState
    {
        internal OriginalSaveActionCommonState(
            OriginalSaveNativeString argument,
            int ownerObjectId,
            OriginalSaveNativeString finishCode,
            OriginalSaveNativeString breakCode,
            int statusOrOpaque)
        {
            Argument = argument ?? throw new ArgumentNullException(nameof(argument));
            OwnerObjectId = ownerObjectId;
            FinishCode = finishCode ?? throw new ArgumentNullException(nameof(finishCode));
            BreakCode = breakCode ?? throw new ArgumentNullException(nameof(breakCode));
            StatusOrOpaque = statusOrOpaque;
        }

        public OriginalSaveNativeString Argument { get; }
        public int OwnerObjectId { get; }
        public OriginalSaveNativeString FinishCode { get; }
        public OriginalSaveNativeString BreakCode { get; }
        public int StatusOrOpaque { get; }
    }

    public abstract class OriginalSaveActionPayload
    {
        protected OriginalSaveActionPayload(
            OriginalSaveActionType type,
            OriginalSaveActionCommonState? common)
        {
            Type = type;
            Common = common;
        }

        public OriginalSaveActionType Type { get; }
        public OriginalSaveActionCommonState? Common { get; }
    }

    public sealed class OriginalSaveNullActionPayload : OriginalSaveActionPayload
    {
        internal OriginalSaveNullActionPayload()
            : base(OriginalSaveActionType.Null, null) { }
    }

    public sealed class OriginalSaveLegacySlideActionPayload : OriginalSaveActionPayload
    {
        internal OriginalSaveLegacySlideActionPayload()
            : base(OriginalSaveActionType.LegacySlide, null) { }
    }

    public sealed class OriginalSaveWaitActionPayload : OriginalSaveActionPayload
    {
        internal OriginalSaveWaitActionPayload(
            OriginalSaveActionCommonState common,
            double absoluteDeadline)
            : base(OriginalSaveActionType.Wait, common)
        {
            AbsoluteDeadline = absoluteDeadline;
        }

        public double AbsoluteDeadline { get; }
    }

    public sealed class OriginalSaveAnimActionPayload : OriginalSaveActionPayload
    {
        internal OriginalSaveAnimActionPayload(
            OriginalSaveActionCommonState common,
            double absoluteDeadline)
            : base(OriginalSaveActionType.Anim, common)
        {
            AbsoluteDeadline = absoluteDeadline;
        }

        public double AbsoluteDeadline { get; }
    }

    public sealed class OriginalSaveCrawlActionPayload : OriginalSaveActionPayload
    {
        internal OriginalSaveCrawlActionPayload(
            OriginalSaveActionCommonState common,
            int remainingTicks,
            OriginalSaveRaw32 randomYawOrPhase)
            : base(OriginalSaveActionType.Crawl, common)
        {
            RemainingTicks = remainingTicks;
            RandomYawOrPhase = randomYawOrPhase;
        }

        public int RemainingTicks { get; }
        public OriginalSaveRaw32 RandomYawOrPhase { get; }
    }

    public sealed class OriginalSaveRotateActionPayload : OriginalSaveActionPayload
    {
        internal OriginalSaveRotateActionPayload(
            OriginalSaveActionCommonState common,
            int directionSign,
            int lastAppliedDirectionOrState,
            OriginalSaveRaw32 angularStep,
            int leftAnimationSlot,
            int rightAnimationSlot,
            int turnClipFrameCount,
            OriginalSaveRaw32 absoluteTargetYaw)
            : base(OriginalSaveActionType.Rotate, common)
        {
            DirectionSign = directionSign;
            LastAppliedDirectionOrState = lastAppliedDirectionOrState;
            AngularStep = angularStep;
            LeftAnimationSlot = leftAnimationSlot;
            RightAnimationSlot = rightAnimationSlot;
            TurnClipFrameCount = turnClipFrameCount;
            AbsoluteTargetYaw = absoluteTargetYaw;
        }

        public int DirectionSign { get; }
        public int LastAppliedDirectionOrState { get; }
        public OriginalSaveRaw32 AngularStep { get; }
        public int LeftAnimationSlot { get; }
        public int RightAnimationSlot { get; }
        public int TurnClipFrameCount { get; }
        public OriginalSaveRaw32 AbsoluteTargetYaw { get; }
    }

    public sealed class OriginalSaveAttackActionPayload : OriginalSaveActionPayload
    {
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _leadingRawValues;
        private readonly ReadOnlyCollection<byte> _deadlineFlags;
        private readonly ReadOnlyCollection<byte> _frameFlags;
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _impactAndTrailingRawValues;

        internal OriginalSaveAttackActionPayload(
            OriginalSaveActionCommonState common,
            OriginalSaveNativeString chainedActionVerb,
            OriginalSaveNativeString chainedActionArguments,
            OriginalSaveRaw32[] leadingRawValues,
            double absoluteDeadline,
            byte[] deadlineFlags,
            int currentFrameOrTick,
            byte[] frameFlags,
            OriginalSaveRaw32[] impactAndTrailingRawValues,
            byte trailingFlag)
            : base(OriginalSaveActionType.Attack, common)
        {
            ChainedActionVerb = chainedActionVerb ??
                throw new ArgumentNullException(nameof(chainedActionVerb));
            ChainedActionArguments = chainedActionArguments ??
                throw new ArgumentNullException(nameof(chainedActionArguments));
            _leadingRawValues = Array.AsReadOnly(leadingRawValues ??
                throw new ArgumentNullException(nameof(leadingRawValues)));
            AbsoluteDeadline = absoluteDeadline;
            _deadlineFlags = Array.AsReadOnly(deadlineFlags ??
                throw new ArgumentNullException(nameof(deadlineFlags)));
            CurrentFrameOrTick = currentFrameOrTick;
            _frameFlags = Array.AsReadOnly(frameFlags ??
                throw new ArgumentNullException(nameof(frameFlags)));
            _impactAndTrailingRawValues = Array.AsReadOnly(impactAndTrailingRawValues ??
                throw new ArgumentNullException(nameof(impactAndTrailingRawValues)));
            TrailingFlag = trailingFlag;
        }

        public OriginalSaveNativeString ChainedActionVerb { get; }
        public OriginalSaveNativeString ChainedActionArguments { get; }
        public IReadOnlyList<OriginalSaveRaw32> LeadingRawValues => _leadingRawValues;
        public double AbsoluteDeadline { get; }
        public IReadOnlyList<byte> DeadlineFlags => _deadlineFlags;
        public int CurrentFrameOrTick { get; }
        public IReadOnlyList<byte> FrameFlags => _frameFlags;
        public IReadOnlyList<OriginalSaveRaw32> ImpactAndTrailingRawValues =>
            _impactAndTrailingRawValues;
        public byte TrailingFlag { get; }
    }

    public sealed class OriginalSaveAttackBallisticActionPayload : OriginalSaveActionPayload
    {
        private readonly ReadOnlyCollection<int> _scheduleValues;
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _firstRawValues;
        private readonly ReadOnlyCollection<int> _transientHandles;
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _secondRawValues;
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _lastTarget;

        internal OriginalSaveAttackBallisticActionPayload(
            OriginalSaveActionCommonState common,
            int targetObjectId,
            int mode,
            int[] scheduleValues,
            OriginalSaveRaw32[] firstRawValues,
            int[] transientHandles,
            OriginalSaveRaw32[] secondRawValues,
            byte primaryFlag,
            OriginalSaveRaw32[] lastTarget,
            byte secondaryFlag,
            byte tertiaryFlag,
            OriginalSaveRaw32 targetVerticalOffset,
            double absoluteDeadline)
            : base(OriginalSaveActionType.AttackBallistic, common)
        {
            TargetObjectId = targetObjectId;
            Mode = mode;
            _scheduleValues = Array.AsReadOnly(scheduleValues ??
                throw new ArgumentNullException(nameof(scheduleValues)));
            _firstRawValues = Array.AsReadOnly(firstRawValues ??
                throw new ArgumentNullException(nameof(firstRawValues)));
            _transientHandles = Array.AsReadOnly(transientHandles ??
                throw new ArgumentNullException(nameof(transientHandles)));
            _secondRawValues = Array.AsReadOnly(secondRawValues ??
                throw new ArgumentNullException(nameof(secondRawValues)));
            PrimaryFlag = primaryFlag;
            _lastTarget = Array.AsReadOnly(lastTarget ??
                throw new ArgumentNullException(nameof(lastTarget)));
            SecondaryFlag = secondaryFlag;
            TertiaryFlag = tertiaryFlag;
            TargetVerticalOffset = targetVerticalOffset;
            AbsoluteDeadline = absoluteDeadline;
        }

        public int TargetObjectId { get; }
        public int Mode { get; }
        public IReadOnlyList<int> ScheduleValues => _scheduleValues;
        public IReadOnlyList<OriginalSaveRaw32> FirstRawValues => _firstRawValues;
        public IReadOnlyList<int> TransientHandles => _transientHandles;
        public IReadOnlyList<OriginalSaveRaw32> SecondRawValues => _secondRawValues;
        public byte PrimaryFlag { get; }
        public IReadOnlyList<OriginalSaveRaw32> LastTarget => _lastTarget;
        public byte SecondaryFlag { get; }
        public byte TertiaryFlag { get; }
        public OriginalSaveRaw32 TargetVerticalOffset { get; }
        public double AbsoluteDeadline { get; }

        public int ShootAnimationDatabaseId => _firstRawValues[0].Bits;
        public int ShootAnimationIndex => _firstRawValues[1].Bits;
        public int TickCounter => _secondRawValues[0].Bits;
        public float Damage => _secondRawValues[1].AsSingle;
        public byte HitReactionFlag => PrimaryFlag;
        public float LastTargetX => _lastTarget[0].AsSingle;
        public float LastTargetY => _lastTarget[1].AsSingle;
        public float LastTargetZ => _lastTarget[2].AsSingle;
        public byte TrailCompletedFlag => SecondaryFlag;
        public byte IgnoreTerrainCollisionFlag => TertiaryFlag;
        public float TargetVerticalOffsetValue => TargetVerticalOffset.AsSingle;
    }

    public sealed class OriginalSaveFlyActionPayload : OriginalSaveActionPayload
    {
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _motionValues;

        internal OriginalSaveFlyActionPayload(
            OriginalSaveActionCommonState common,
            OriginalSaveRaw32[] motionValues,
            byte arrivedOrCloseMode,
            int tickCount,
            byte swimMode)
            : base(OriginalSaveActionType.Fly, common)
        {
            _motionValues = Array.AsReadOnly(motionValues ??
                throw new ArgumentNullException(nameof(motionValues)));
            ArrivedOrCloseMode = arrivedOrCloseMode;
            TickCount = tickCount;
            SwimMode = swimMode;
        }

        public IReadOnlyList<OriginalSaveRaw32> MotionValues => _motionValues;
        public byte ArrivedOrCloseMode { get; }
        public int TickCount { get; }
        public byte SwimMode { get; }
    }

    public sealed class OriginalSaveLoreTarget
    {
        internal OriginalSaveLoreTarget(int objectId, OriginalSaveRaw32 offsetOrDistance)
        {
            ObjectId = objectId;
            OffsetOrDistance = offsetOrDistance;
        }

        public int ObjectId { get; }
        public OriginalSaveRaw32 OffsetOrDistance { get; }
        public float DistanceBehindHead => OffsetOrDistance.AsSingle;
    }

    public sealed class OriginalSaveLoreWaypoint
    {
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _values;

        internal OriginalSaveLoreWaypoint(OriginalSaveRaw32[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Length != 8)
                throw new ArgumentException("A native Lore waypoint has eight values.", nameof(values));
            _values = Array.AsReadOnly(values);
        }

        public IReadOnlyList<OriginalSaveRaw32> Values => _values;
        public OriginalSaveRaw32 PositionX => _values[0];
        public OriginalSaveRaw32 PositionY => _values[1];
        public OriginalSaveRaw32 PositionZ => _values[2];
        public OriginalSaveRaw32 RotationX => _values[3];
        public OriginalSaveRaw32 RotationY => _values[4];
        public OriginalSaveRaw32 RotationZ => _values[5];
        public OriginalSaveRaw32 MinimumSpeed => _values[6];
        public OriginalSaveRaw32 MaximumSpeed => _values[7];
    }

    public sealed class OriginalSaveLoreActionPayload : OriginalSaveActionPayload
    {
        private readonly ReadOnlyCollection<OriginalSaveLoreTarget> _targets;
        private readonly ReadOnlyCollection<OriginalSaveLoreWaypoint> _waypoints;

        internal OriginalSaveLoreActionPayload(
            OriginalSaveActionCommonState common,
            OriginalSaveRaw32 gravity,
            OriginalSaveRaw32 friction,
            OriginalSaveLoreTarget[] targets,
            OriginalSaveLoreWaypoint[] waypoints)
            : base(OriginalSaveActionType.Lore, common)
        {
            Gravity = gravity;
            Friction = friction;
            _targets = Array.AsReadOnly(targets ??
                throw new ArgumentNullException(nameof(targets)));
            _waypoints = Array.AsReadOnly(waypoints ??
                throw new ArgumentNullException(nameof(waypoints)));
        }

        public OriginalSaveRaw32 Gravity { get; }
        public OriginalSaveRaw32 Friction { get; }
        public IReadOnlyList<OriginalSaveLoreTarget> Targets => _targets;
        public IReadOnlyList<OriginalSaveLoreWaypoint> Waypoints => _waypoints;
        public float GravityValue => Gravity.AsSingle;
        public float FrictionValue => Friction.AsSingle;
    }

    public sealed class OriginalSaveWalkAnimationBank
    {
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _slotValues;

        internal OriginalSaveWalkAnimationBank(
            int bankStateOrAnimationSet,
            OriginalSaveRaw32[] slotValues)
        {
            if (slotValues == null) throw new ArgumentNullException(nameof(slotValues));
            if (slotValues.Length != 24 * 5)
                throw new ArgumentException(
                    "A native Walk animation bank has 24 five-value slots.",
                    nameof(slotValues));
            BankStateOrAnimationSet = bankStateOrAnimationSet;
            _slotValues = Array.AsReadOnly(slotValues);
        }

        public int BankStateOrAnimationSet { get; }
        public IReadOnlyList<OriginalSaveRaw32> SlotValues => _slotValues;
    }

    public sealed class OriginalSaveWalkActionPayload : OriginalSaveActionPayload
    {
        private readonly ReadOnlyCollection<OriginalSaveWalkAnimationBank> _animationBanks;
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _targetAndDistanceValues;
        private readonly ReadOnlyCollection<byte> _moveFlags;

        internal OriginalSaveWalkActionPayload(
            OriginalSaveActionCommonState common,
            OriginalSaveWalkAnimationBank[] animationBanks,
            int moveNeededOrApproach,
            byte waving,
            byte dontStop,
            OriginalSaveRaw32[] targetAndDistanceValues,
            byte canClimb,
            byte useObjects,
            int randomPath,
            int randomZ,
            byte[] moveFlags,
            int objectBreakClassId,
            OriginalSaveRaw32 objectBreakDistance,
            OriginalSaveRaw32 targetDistance,
            OriginalSaveRaw32 climbRotationX,
            OriginalSaveRaw32 climbRotationY,
            OriginalSaveRaw32 climbRotationZ,
            byte diagonalSqueezeFlag,
            OriginalSaveRaw32 randomPathWallDistance,
            int walkTimerCounter,
            int tickCounter,
            int speedType,
            OriginalSaveNativeString walkTimerCallback,
            OriginalSaveNativeString walkCollisionCallback,
            OriginalSaveNativeString objectUseCallback)
            : base(OriginalSaveActionType.Walk, common)
        {
            _animationBanks = Array.AsReadOnly(animationBanks ??
                throw new ArgumentNullException(nameof(animationBanks)));
            MoveNeededOrApproach = moveNeededOrApproach;
            Waving = waving;
            DontStop = dontStop;
            _targetAndDistanceValues = Array.AsReadOnly(targetAndDistanceValues ??
                throw new ArgumentNullException(nameof(targetAndDistanceValues)));
            CanClimb = canClimb;
            UseObjects = useObjects;
            RandomPath = randomPath;
            RandomZ = randomZ;
            _moveFlags = Array.AsReadOnly(moveFlags ??
                throw new ArgumentNullException(nameof(moveFlags)));
            ObjectBreakClassId = objectBreakClassId;
            ObjectBreakDistance = objectBreakDistance;
            TargetDistance = targetDistance;
            ClimbRotationX = climbRotationX;
            ClimbRotationY = climbRotationY;
            ClimbRotationZ = climbRotationZ;
            DiagonalSqueezeFlag = diagonalSqueezeFlag;
            RandomPathWallDistance = randomPathWallDistance;
            WalkTimerCounter = walkTimerCounter;
            TickCounter = tickCounter;
            SpeedType = speedType;
            WalkTimerCallback = walkTimerCallback ??
                throw new ArgumentNullException(nameof(walkTimerCallback));
            WalkCollisionCallback = walkCollisionCallback ??
                throw new ArgumentNullException(nameof(walkCollisionCallback));
            ObjectUseCallback = objectUseCallback ??
                throw new ArgumentNullException(nameof(objectUseCallback));
        }

        public IReadOnlyList<OriginalSaveWalkAnimationBank> AnimationBanks => _animationBanks;
        public int MoveNeededOrApproach { get; }
        public byte Waving { get; }
        public byte DontStop { get; }
        public IReadOnlyList<OriginalSaveRaw32> TargetAndDistanceValues => _targetAndDistanceValues;
        public byte CanClimb { get; }
        public byte UseObjects { get; }
        public int RandomPath { get; }
        public int RandomZ { get; }
        public IReadOnlyList<byte> MoveFlags => _moveFlags;
        public int ObjectBreakClassId { get; }
        public OriginalSaveRaw32 ObjectBreakDistance { get; }
        public OriginalSaveRaw32 TargetDistance { get; }
        public OriginalSaveRaw32 ClimbRotationX { get; }
        public OriginalSaveRaw32 ClimbRotationY { get; }
        public OriginalSaveRaw32 ClimbRotationZ { get; }
        public byte DiagonalSqueezeFlag { get; }
        public OriginalSaveRaw32 RandomPathWallDistance { get; }
        public int WalkTimerCounter { get; }
        public int TickCounter { get; }
        public int SpeedType { get; }
        public OriginalSaveNativeString WalkTimerCallback { get; }
        public OriginalSaveNativeString WalkCollisionCallback { get; }
        public OriginalSaveNativeString ObjectUseCallback { get; }
    }

    public sealed class OriginalSaveMoveActionPayload : OriginalSaveActionPayload
    {
        private readonly ReadOnlyCollection<OriginalSaveRaw32> _vectors;

        internal OriginalSaveMoveActionPayload(
            OriginalSaveActionCommonState common,
            OriginalSaveRaw32[] vectors,
            int remainingNativeTicks)
            : base(OriginalSaveActionType.Move, common)
        {
            _vectors = Array.AsReadOnly(vectors ??
                throw new ArgumentNullException(nameof(vectors)));
            RemainingNativeTicks = remainingNativeTicks;
        }

        public IReadOnlyList<OriginalSaveRaw32> Vectors => _vectors;
        public int RemainingNativeTicks { get; }
    }

    public sealed class OriginalSaveActionRecord
    {
        private readonly byte[] _sourcePayload;

        internal OriginalSaveActionRecord(
            byte[] sourcePayload,
            int ownerObjectId,
            int payloadOffset,
            int payloadLength,
            OriginalSaveActionPayload payload)
        {
            _sourcePayload = sourcePayload ?? throw new ArgumentNullException(nameof(sourcePayload));
            OwnerObjectId = ownerObjectId;
            PayloadOffset = payloadOffset;
            PayloadLength = payloadLength;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public int OwnerObjectId { get; }
        public OriginalSaveActionType Type => Payload.Type;
        public int PayloadOffset { get; }
        public int PayloadLength { get; }
        public OriginalSaveActionPayload Payload { get; }

        public byte[] CopyRawPayload()
        {
            var result = new byte[PayloadLength];
            Buffer.BlockCopy(_sourcePayload, PayloadOffset, result, 0, result.Length);
            return result;
        }
    }

    public sealed class OriginalSaveActionAuxiliaryVectorRecord
    {
        private readonly ReadOnlyCollection<int> _values;

        internal OriginalSaveActionAuxiliaryVectorRecord(int[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Length != 8)
                throw new ArgumentException(
                    "A native action auxiliary vector record has eight values.",
                    nameof(values));
            _values = Array.AsReadOnly(values);
        }

        public IReadOnlyList<int> Values => _values;
        public OriginalSaveRaw32 PositionX => new(_values[0]);
        public OriginalSaveRaw32 PositionY => new(_values[1]);
        public OriginalSaveRaw32 PositionZ => new(_values[2]);
        public OriginalSaveRaw32 RotationX => new(_values[3]);
        public OriginalSaveRaw32 RotationY => new(_values[4]);
        public OriginalSaveRaw32 RotationZ => new(_values[5]);
        public OriginalSaveRaw32 MinimumSpeed => new(_values[6]);
        public OriginalSaveRaw32 MaximumSpeed => new(_values[7]);
    }

    public sealed class OriginalSaveActionAuxiliaryRecord
    {
        private readonly ReadOnlyCollection<OriginalSaveActionAuxiliaryVectorRecord> _vectors;
        private readonly ReadOnlyCollection<int> _trailingValues;

        internal OriginalSaveActionAuxiliaryRecord(
            int value,
            OriginalSaveActionAuxiliaryVectorRecord[] vectors,
            int[] trailingValues)
        {
            Value = value;
            _vectors = Array.AsReadOnly(vectors ?? throw new ArgumentNullException(nameof(vectors)));
            if (trailingValues == null) throw new ArgumentNullException(nameof(trailingValues));
            if (trailingValues.Length != 3)
                throw new ArgumentException(
                    "A native action auxiliary record has three trailing values.",
                    nameof(trailingValues));
            _trailingValues = Array.AsReadOnly(trailingValues);
        }

        public int Value { get; }
        public IReadOnlyList<OriginalSaveActionAuxiliaryVectorRecord> Vectors => _vectors;
        public IReadOnlyList<int> TrailingValues => _trailingValues;
        public int OwnerObjectId => Value;
        public IReadOnlyList<OriginalSaveActionAuxiliaryVectorRecord> Waypoints => _vectors;
        public OriginalSaveRaw32 Progress => new(_trailingValues[0]);
        public OriginalSaveRaw32 TotalLength => new(_trailingValues[1]);
        public OriginalSaveRaw32 Speed => new(_trailingValues[2]);
    }

    public sealed class OriginalSaveActionTableDocument
    {
        private const int MaximumObjectId = 0x1FFF;
        private const int MaximumStringLength = 1024 * 1024;
        private const int MaximumCollectionCount = 4 * 1024 * 1024;

        private readonly ReadOnlyCollection<OriginalSaveActionRecord> _records;
        private readonly ReadOnlyCollection<OriginalSaveActionAuxiliaryRecord> _auxiliaryRecords;

        internal OriginalSaveActionTableDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            var cursor = new Cursor(sourcePayload, metadata.ActionSectionOffset);
            var records = new List<OriginalSaveActionRecord>(metadata.ActionCount);
            var owners = new HashSet<int>();
            int ownerObjectId = cursor.ReadInt32();
            while (ownerObjectId != -1)
            {
                if (ownerObjectId < 0 || ownerObjectId > MaximumObjectId || !owners.Add(ownerObjectId))
                    throw new InvalidDataException($"Invalid action owner object ID {ownerObjectId}.");

                int rawType = cursor.ReadInt32();
                if (rawType < (int)OriginalSaveActionType.Null ||
                    rawType > (int)OriginalSaveActionType.Move)
                {
                    throw new InvalidDataException(
                        $"Invalid action type {rawType} for object ID {ownerObjectId}.");
                }

                int payloadOffset = cursor.Position;
                OriginalSaveActionPayload payload = ReadPayload(
                    cursor,
                    ownerObjectId,
                    (OriginalSaveActionType)rawType);
                records.Add(new OriginalSaveActionRecord(
                    sourcePayload,
                    ownerObjectId,
                    payloadOffset,
                    checked(cursor.Position - payloadOffset),
                    payload));
                ownerObjectId = cursor.ReadInt32();
            }

            cursor.ExpectInt32(0x626C6133, "action table sentinel");
            int auxiliaryCount = cursor.ReadCount("action auxiliary record");
            var auxiliaryRecords = new OriginalSaveActionAuxiliaryRecord[auxiliaryCount];
            int auxiliaryVectorCount = 0;
            for (int record = 0; record < auxiliaryRecords.Length; record++)
            {
                int value = cursor.ReadInt32();
                int vectorCount = cursor.ReadCount("action auxiliary vector record");
                auxiliaryVectorCount = checked(auxiliaryVectorCount + vectorCount);
                var vectors = new OriginalSaveActionAuxiliaryVectorRecord[vectorCount];
                for (int vector = 0; vector < vectors.Length; vector++)
                    vectors[vector] = new OriginalSaveActionAuxiliaryVectorRecord(cursor.ReadInt32s(8));
                auxiliaryRecords[record] = new OriginalSaveActionAuxiliaryRecord(
                    value,
                    vectors,
                    cursor.ReadInt32s(3));
            }
            cursor.ExpectInt32(0x626C6134, "action auxiliary table sentinel");

            if (cursor.Position != metadata.Marker13Offset)
            {
                throw new InvalidDataException(
                    $"Typed action parsing ended at {cursor.Position}, expected marker 13 at {metadata.Marker13Offset}.");
            }
            if (records.Count != metadata.ActionCount ||
                auxiliaryRecords.Length != metadata.AuxiliaryRecordCount ||
                auxiliaryVectorCount != metadata.AuxiliaryVectorRecordCount)
            {
                throw new InvalidDataException(
                    "Typed action parsing disagrees with the structurally validated action metadata.");
            }

            _records = new ReadOnlyCollection<OriginalSaveActionRecord>(records);
            _auxiliaryRecords = Array.AsReadOnly(auxiliaryRecords);
        }

        public IReadOnlyList<OriginalSaveActionRecord> Records => _records;
        public IReadOnlyList<OriginalSaveActionAuxiliaryRecord> AuxiliaryRecords => _auxiliaryRecords;

        private static OriginalSaveActionPayload ReadPayload(
            Cursor cursor,
            int tableOwnerObjectId,
            OriginalSaveActionType type)
        {
            switch (type)
            {
                case OriginalSaveActionType.Null:
                    return new OriginalSaveNullActionPayload();
                case OriginalSaveActionType.Wait:
                    return new OriginalSaveWaitActionPayload(
                        ReadCommon(cursor, tableOwnerObjectId),
                        cursor.ReadDouble());
                case OriginalSaveActionType.LegacySlide:
                    return new OriginalSaveLegacySlideActionPayload();
                case OriginalSaveActionType.Anim:
                    return new OriginalSaveAnimActionPayload(
                        ReadCommon(cursor, tableOwnerObjectId),
                        cursor.ReadDouble());
                case OriginalSaveActionType.Crawl:
                    return new OriginalSaveCrawlActionPayload(
                        ReadCommon(cursor, tableOwnerObjectId),
                        cursor.ReadInt32(),
                        cursor.ReadRaw32());
                case OriginalSaveActionType.Rotate:
                    return new OriginalSaveRotateActionPayload(
                        ReadCommon(cursor, tableOwnerObjectId),
                        cursor.ReadInt32(),
                        cursor.ReadInt32(),
                        cursor.ReadRaw32(),
                        cursor.ReadInt32(),
                        cursor.ReadInt32(),
                        cursor.ReadInt32(),
                        cursor.ReadRaw32());
                case OriginalSaveActionType.Attack:
                    return ReadAttack(cursor, tableOwnerObjectId);
                case OriginalSaveActionType.AttackBallistic:
                    return ReadAttackBallistic(cursor, tableOwnerObjectId);
                case OriginalSaveActionType.Fly:
                    return new OriginalSaveFlyActionPayload(
                        ReadCommon(cursor, tableOwnerObjectId),
                        cursor.ReadRaw32s(12),
                        cursor.ReadByte(),
                        cursor.ReadInt32(),
                        cursor.ReadByte());
                case OriginalSaveActionType.Lore:
                    return ReadLore(cursor, tableOwnerObjectId);
                case OriginalSaveActionType.Walk:
                    return ReadWalk(cursor, tableOwnerObjectId);
                case OriginalSaveActionType.Move:
                    return new OriginalSaveMoveActionPayload(
                        ReadCommon(cursor, tableOwnerObjectId),
                        cursor.ReadRaw32s(9),
                        cursor.ReadInt32());
                default:
                    throw new InvalidDataException($"Unsupported current action type {(int)type}.");
            }
        }

        private static OriginalSaveAttackActionPayload ReadAttack(
            Cursor cursor,
            int ownerObjectId)
        {
            OriginalSaveNativeString verb = cursor.ReadNativeString(MaximumStringLength);
            OriginalSaveNativeString arguments = cursor.ReadNativeString(MaximumStringLength);
            OriginalSaveRaw32[] leading = cursor.ReadRaw32s(5);
            double deadline = cursor.ReadDouble();
            byte[] deadlineFlags = cursor.ReadBytes(3);
            int frame = cursor.ReadInt32();
            byte[] frameFlags = cursor.ReadBytes(4);
            OriginalSaveRaw32[] impactAndTrailing = cursor.ReadRaw32s(5);
            byte trailingFlag = cursor.ReadByte();
            OriginalSaveActionCommonState common = ReadCommon(cursor, ownerObjectId);
            return new OriginalSaveAttackActionPayload(
                common,
                verb,
                arguments,
                leading,
                deadline,
                deadlineFlags,
                frame,
                frameFlags,
                impactAndTrailing,
                trailingFlag);
        }

        private static OriginalSaveAttackBallisticActionPayload ReadAttackBallistic(
            Cursor cursor,
            int ownerObjectId)
        {
            int targetObjectId = cursor.ReadInt32();
            int mode = cursor.ReadInt32();
            int[] schedule = cursor.ReadInt32s(cursor.ReadCount("ballistic action schedule value"));
            OriginalSaveRaw32[] firstRaw = cursor.ReadRaw32s(2);
            int[] handles = cursor.ReadInt32s(cursor.ReadCount("ballistic action transient handle"));
            OriginalSaveRaw32[] secondRaw = cursor.ReadRaw32s(2);
            byte primaryFlag = cursor.ReadByte();
            OriginalSaveRaw32[] lastTarget = cursor.ReadRaw32s(3);
            byte secondaryFlag = cursor.ReadByte();
            byte tertiaryFlag = cursor.ReadByte();
            OriginalSaveRaw32 verticalOffset = cursor.ReadRaw32();
            double deadline = cursor.ReadDouble();
            OriginalSaveActionCommonState common = ReadCommon(cursor, ownerObjectId);
            return new OriginalSaveAttackBallisticActionPayload(
                common,
                targetObjectId,
                mode,
                schedule,
                firstRaw,
                handles,
                secondRaw,
                primaryFlag,
                lastTarget,
                secondaryFlag,
                tertiaryFlag,
                verticalOffset,
                deadline);
        }

        private static OriginalSaveLoreActionPayload ReadLore(Cursor cursor, int ownerObjectId)
        {
            OriginalSaveActionCommonState common = ReadCommon(cursor, ownerObjectId);
            OriginalSaveRaw32 gravity = cursor.ReadRaw32();
            OriginalSaveRaw32 friction = cursor.ReadRaw32();
            int targetCount = cursor.ReadCount("lore action target");
            var targets = new OriginalSaveLoreTarget[targetCount];
            for (int index = 0; index < targets.Length; index++)
                targets[index] = new OriginalSaveLoreTarget(cursor.ReadInt32(), cursor.ReadRaw32());

            int waypointCount = cursor.ReadCount("lore action waypoint");
            var waypoints = new OriginalSaveLoreWaypoint[waypointCount];
            for (int index = 0; index < waypoints.Length; index++)
                waypoints[index] = new OriginalSaveLoreWaypoint(cursor.ReadRaw32s(8));
            return new OriginalSaveLoreActionPayload(common, gravity, friction, targets, waypoints);
        }

        private static OriginalSaveWalkActionPayload ReadWalk(Cursor cursor, int ownerObjectId)
        {
            OriginalSaveActionCommonState common = ReadCommon(cursor, ownerObjectId);
            var banks = new OriginalSaveWalkAnimationBank[4];
            for (int bank = 0; bank < banks.Length; bank++)
            {
                banks[bank] = new OriginalSaveWalkAnimationBank(
                    cursor.ReadInt32(),
                    cursor.ReadRaw32s(24 * 5));
            }

            int moveNeededOrApproach = cursor.ReadInt32();
            byte waving = cursor.ReadByte();
            byte dontStop = cursor.ReadByte();
            OriginalSaveRaw32[] targetAndDistanceValues = cursor.ReadRaw32s(4);
            byte canClimb = cursor.ReadByte();
            byte useObjects = cursor.ReadByte();
            int randomPath = cursor.ReadInt32();
            int randomZ = cursor.ReadInt32();
            byte[] moveFlags = cursor.ReadBytes(4);
            int objectBreakClassId = cursor.ReadInt32();
            OriginalSaveRaw32 objectBreakDistance = cursor.ReadRaw32();
            OriginalSaveRaw32 targetDistance = cursor.ReadRaw32();
            OriginalSaveRaw32 climbRotationX = cursor.ReadRaw32();
            OriginalSaveRaw32 climbRotationY = cursor.ReadRaw32();
            OriginalSaveRaw32 climbRotationZ = cursor.ReadRaw32();
            byte diagonalSqueezeFlag = cursor.ReadByte();
            OriginalSaveRaw32 randomPathWallDistance = cursor.ReadRaw32();
            int walkTimerCounter = cursor.ReadInt32();
            int tickCounter = cursor.ReadInt32();
            int speedType = cursor.ReadInt32();
            OriginalSaveNativeString walkTimerCallback = cursor.ReadNativeString(MaximumStringLength);
            OriginalSaveNativeString walkCollisionCallback = cursor.ReadNativeString(MaximumStringLength);
            OriginalSaveNativeString objectUseCallback = cursor.ReadNativeString(MaximumStringLength);
            return new OriginalSaveWalkActionPayload(
                common,
                banks,
                moveNeededOrApproach,
                waving,
                dontStop,
                targetAndDistanceValues,
                canClimb,
                useObjects,
                randomPath,
                randomZ,
                moveFlags,
                objectBreakClassId,
                objectBreakDistance,
                targetDistance,
                climbRotationX,
                climbRotationY,
                climbRotationZ,
                diagonalSqueezeFlag,
                randomPathWallDistance,
                walkTimerCounter,
                tickCounter,
                speedType,
                walkTimerCallback,
                walkCollisionCallback,
                objectUseCallback);
        }

        private static OriginalSaveActionCommonState ReadCommon(
            Cursor cursor,
            int tableOwnerObjectId)
        {
            OriginalSaveNativeString argument = cursor.ReadNativeString(MaximumStringLength);
            int commonOwnerObjectId = cursor.ReadInt32();
            if (commonOwnerObjectId != tableOwnerObjectId)
            {
                throw new InvalidDataException(
                    $"Action table owner {tableOwnerObjectId} does not match common owner {commonOwnerObjectId}.");
            }

            return new OriginalSaveActionCommonState(
                argument,
                commonOwnerObjectId,
                cursor.ReadNativeString(MaximumStringLength),
                cursor.ReadNativeString(MaximumStringLength),
                cursor.ReadInt32());
        }

        private sealed class Cursor
        {
            private readonly byte[] _source;

            internal Cursor(byte[] source, long offset)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                if (offset < 0 || offset > source.Length)
                    throw new ArgumentOutOfRangeException(nameof(offset));
                Position = checked((int)offset);
            }

            internal int Position { get; private set; }

            internal byte ReadByte()
            {
                EnsureAvailable(1);
                return _source[Position++];
            }

            internal byte[] ReadBytes(int count)
            {
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                EnsureAvailable(count);
                var result = new byte[count];
                Buffer.BlockCopy(_source, Position, result, 0, count);
                Position = checked(Position + count);
                return result;
            }

            internal int ReadInt32()
            {
                EnsureAvailable(4);
                int value = _source[Position] |
                    _source[Position + 1] << 8 |
                    _source[Position + 2] << 16 |
                    _source[Position + 3] << 24;
                Position += 4;
                return value;
            }

            internal double ReadDouble()
            {
                EnsureAvailable(8);
                ulong bits = 0;
                for (int index = 0; index < 8; index++)
                    bits |= (ulong)_source[Position + index] << (index * 8);
                Position += 8;
                return BitConverter.Int64BitsToDouble(unchecked((long)bits));
            }

            internal OriginalSaveRaw32 ReadRaw32() => new OriginalSaveRaw32(ReadInt32());

            internal int[] ReadInt32s(int count)
            {
                if (count < 0 || count > MaximumCollectionCount)
                    throw new InvalidDataException($"Invalid native value count {count}.");
                var result = new int[count];
                for (int index = 0; index < result.Length; index++)
                    result[index] = ReadInt32();
                return result;
            }

            internal OriginalSaveRaw32[] ReadRaw32s(int count)
            {
                if (count < 0 || count > MaximumCollectionCount)
                    throw new InvalidDataException($"Invalid native raw-value count {count}.");
                var result = new OriginalSaveRaw32[count];
                for (int index = 0; index < result.Length; index++)
                    result[index] = ReadRaw32();
                return result;
            }

            internal int ReadCount(string label)
            {
                int count = ReadInt32();
                if (count < 0 || count > MaximumCollectionCount)
                    throw new InvalidDataException($"Invalid {label} count {count}.");
                return count;
            }

            internal OriginalSaveNativeString ReadNativeString(int maximumLength)
            {
                int length = ReadInt32();
                if (length <= 0 || length > maximumLength)
                    throw new InvalidDataException($"Invalid native string length {length}.");
                byte[] bytes = ReadBytes(length);
                if (bytes[^1] != 0)
                    throw new InvalidDataException("Original save string is not NUL-terminated.");
                return new OriginalSaveNativeString(bytes);
            }

            internal void ExpectInt32(int expected, string label)
            {
                int actual = ReadInt32();
                if (actual != expected)
                {
                    throw new InvalidDataException(
                        $"Invalid {label}: expected 0x{expected:X8}, got 0x{actual:X8}.");
                }
            }

            private void EnsureAvailable(int count)
            {
                if (count < 0 || Position > _source.Length - count)
                    throw new EndOfStreamException("Unexpected end of original save action table.");
            }
        }
    }
}
