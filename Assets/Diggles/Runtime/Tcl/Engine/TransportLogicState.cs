#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System.Collections.Generic;
    using Diggles.Runtime.Navigation;
    using UnityEngine;

    public enum DoorAccessMode
    {
        Open = 0,
        Closed = 1,
        OpenForPlayer = 2,
        OpenForFriends = 3,
        OpenForClasses = 4,
        OpenForAll = 5,
    }

    public sealed class PathInfluenceState
    {
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
        public int Cost;
        public int Type;
        public bool AddsClimbSurface;
        public bool HasClimbBounds;
        public int ClimbX0;
        public int ClimbY0;
        public int ClimbX1;
        public int ClimbY1;
        public NavTransportKind TransportKind;
    }

    public sealed class DoorLogicState
    {
        public DoorAccessMode Mode = DoorAccessMode.Closed;
        public readonly HashSet<string> AllowedClasses = new(System.StringComparer.Ordinal);
        public bool IsTemporarilyOpen;
        public bool IsVisuallyOpen;
        public bool OpensLeft;
    }

    public sealed class LadderLogicState
    {
        public float Range = 1_000_000f;
        public float ProjectedSpan;
        public string CurrentPresentation = "";
        public string CurrentPresentationClip = "";
        public int LastStampCenterX = int.MinValue;
        public int LastStampCenterY = int.MinValue;
        public bool LastStampActive;
    }

    public enum ElevatorMotionState
    {
        Idle = 0,
        StoppedForTransfer = 1,
        Moving = 2,
    }

    public enum ElevatorRidePhase
    {
        Calling,
        Boarding,
        Riding,
        Exiting,
        Completed,
        Interrupted,
    }

    public sealed class ElevatorLogicState
    {
        public const int MaxRiders = 16;
        public const int DefaultRiderCapacity = 4;

        public int CabinHandle;
        public bool UsesRegisteredTrack;
        public float MaxRange = 1_000_000f;
        public int AccelerationMode = 1;
        public float Speed = 0.2f;
        public float BrakeAcceleration = 0.4f;
        public int RiderCapacity = DefaultRiderCapacity;
        public int Flags
        {
            get => RiderCapacity;
            set => RiderCapacity = System.Math.Clamp(value, 0, MaxRiders);
        }
        public bool Initialized;
        public Vector3 LastDrivePosition;
        public bool HasLastDrivePosition;
        public bool CabinVisible;
        public bool HasCabinVisibility;
        public float ProjectedSpan;
        public float TrackTopY;
        public float TrackBottomY;
        public float CurrentCabinY;
        public float TargetY;
        public float Velocity;
        public float TravelDirection;
        public float TransferDwellSeconds;
        public ElevatorMotionState MotionState;
        public string CurrentPresentation = "";
        public int LastStampCenterX = int.MinValue;
        public int LastStampCenterY = int.MinValue;
        public bool LastStampActive;
        public readonly Dictionary<int, ElevatorRiderRequest> Riders = new();
    }

    public sealed class ElevatorRiderRequest
    {
        public int RiderHandle;
        public float PickupY;
        public float DestinationY;
        public ElevatorRidePhase Phase;
        public int CabinSlot = -1;
        public Vector3 CabinOffset;
        public double LeaseSeconds;
    }

    public sealed class TransportLogicState
    {
        public DoorLogicState? Door;
        public LadderLogicState? Ladder;
        public ElevatorLogicState? Elevator;
    }
}
