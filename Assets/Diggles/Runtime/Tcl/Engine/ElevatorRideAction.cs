#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using UnityEngine;

    public sealed class ElevatorRideAction : IEntityAction, IWalkResultCarrier
    {
        private const float ArrivalTolerance = 0.05f;
        private const float TransferDwellSeconds = 1f;

        // FUN_00607b10 authors these first eight of the sixteen zero-constructed vectors.
        private static readonly Vector3[] CabinOffsets =
        {
            new(-0.5f, 0f, 0f),
            new(0.5f, 0f, 0f),
            new(0.5f, 0f, 1.5f),
            new(-0.5f, 0f, 1.5f),
            new(-0.5f, 0f, 0f),
            new(0.5f, 0f, 0f),
            Vector3.zero,
            new(0f, 0f, 1.5f),
        };

        private readonly DigglesTclContext _ctx;
        private readonly DigglesTclEntity _rider;
        private readonly int _driveHandle;
        private readonly float _pickupY;
        private readonly float _destinationY;
        private readonly Vector3 _exitNative;
        private bool _registered;

        public int ExitResult { get; private set; }

        public ElevatorRideAction(
            DigglesTclContext ctx,
            DigglesTclEntity rider,
            int driveHandle,
            float pickupY,
            float destinationY,
            Vector3 exitNative)
        {
            _ctx = ctx;
            _rider = rider;
            _driveHandle = driveHandle;
            _pickupY = pickupY;
            _destinationY = destinationY;
            _exitNative = exitNative;
        }

        public bool Tick(double dt)
        {
            if (!_ctx.Entities.TryGetValue(_driveHandle, out var drive)
                || drive.TransportLogic?.Elevator is not { } elevator)
                return Fail(null);

            if (!_registered)
            {
                DigglesTclEngine.RegisterElevatorRide(_ctx, _rider.Handle, _driveHandle);
                elevator.Riders[_rider.Handle] = new ElevatorRiderRequest
                {
                    RiderHandle = _rider.Handle,
                    PickupY = _pickupY,
                    DestinationY = _destinationY,
                    Phase = ElevatorRidePhase.Calling,
                };
                _registered = true;
            }

            if (!elevator.Riders.TryGetValue(_rider.Handle, out var request))
                return Fail(elevator);
            request.LeaseSeconds = 0.0;

            if (!elevator.Initialized) return true;
            if (elevator.ProjectedSpan <= 0.5f) return Fail(elevator);
            if (!IsWithinTrack(elevator, _pickupY)
                || !IsWithinTrack(elevator, _destinationY))
                return Fail(elevator);

            switch (request.Phase)
            {
                case ElevatorRidePhase.Calling:
                    if (!IsStoppedAt(elevator, request.PickupY)
                        || !CanBoardCurrentSweep(elevator, request))
                        return true;
                    request.Phase = ElevatorRidePhase.Boarding;
                    elevator.MotionState = ElevatorMotionState.StoppedForTransfer;
                    elevator.Velocity = 0f;
                    return true;

                case ElevatorRidePhase.Boarding:
                    if (!TryAssignCabinSlot(elevator, request)) return true;
                    request.Phase = ElevatorRidePhase.Riding;
                    elevator.TransferDwellSeconds = Mathf.Max(
                        elevator.TransferDwellSeconds,
                        TransferDwellSeconds);
                    elevator.MotionState = ElevatorMotionState.StoppedForTransfer;
                    return true;

                case ElevatorRidePhase.Riding:
                    if (!IsStoppedAt(elevator, request.DestinationY)) return true;
                    request.Phase = ElevatorRidePhase.Exiting;
                    request.CabinSlot = -1;
                    elevator.MotionState = ElevatorMotionState.StoppedForTransfer;
                    elevator.Velocity = 0f;
                    return true;

                case ElevatorRidePhase.Exiting:
                    request.Phase = ElevatorRidePhase.Completed;
                    elevator.Riders.Remove(_rider.Handle);
                    DigglesTclEngine.UnregisterElevatorRide(
                        _ctx,
                        _rider.Handle,
                        _driveHandle);
                    DigglesTclEngine.ResetElevatorWhenUnserviced(elevator);
                    ApplyPosition(_exitNative);
                    return false;

                case ElevatorRidePhase.Completed:
                    return false;

                default:
                    return Fail(elevator);
            }
        }

        private static bool IsStoppedAt(ElevatorLogicState elevator, float stopY) =>
            Mathf.Abs(elevator.CurrentCabinY - stopY) <= ArrivalTolerance
            && Mathf.Abs(elevator.Velocity) <= ArrivalTolerance;

        private static bool IsWithinTrack(ElevatorLogicState elevator, float stopY) =>
            stopY >= elevator.TrackTopY - ArrivalTolerance
            && stopY <= elevator.TrackBottomY + ArrivalTolerance;

        private static int CountFixedRiders(ElevatorLogicState elevator)
        {
            int count = 0;
            foreach (ElevatorRiderRequest request in elevator.Riders.Values)
                if (request.Phase is ElevatorRidePhase.Boarding
                    or ElevatorRidePhase.Riding
                    or ElevatorRidePhase.Exiting)
                    count++;
            return count;
        }

        // FUN_00608140:61-100: even at a stopped pickup with spare capacity, a new call
        // cannot board if its requested direction would reverse any fixed passenger's
        // remaining trip. Equal pickup/destination keeps the native default +1 direction.
        private static bool CanBoardCurrentSweep(
            ElevatorLogicState elevator,
            ElevatorRiderRequest candidate)
        {
            if (CountFixedRiders(elevator) >= elevator.RiderCapacity) return false;
            float callDirection = candidate.DestinationY < candidate.PickupY ? -1f : 1f;
            foreach (ElevatorRiderRequest other in elevator.Riders.Values)
            {
                if (object.ReferenceEquals(other, candidate)
                    || other.Phase is not (ElevatorRidePhase.Boarding
                        or ElevatorRidePhase.Riding
                        or ElevatorRidePhase.Exiting))
                    continue;
                if (callDirection * (other.DestinationY - elevator.CurrentCabinY) < 0f)
                    return false;
            }
            return true;
        }

        private static bool TryAssignCabinSlot(
            ElevatorLogicState elevator,
            ElevatorRiderRequest request)
        {
            int capacity = Mathf.Clamp(
                elevator.RiderCapacity,
                0,
                ElevatorLogicState.MaxRiders);
            for (int slot = 0; slot < capacity; slot++)
            {
                bool occupied = false;
                foreach (ElevatorRiderRequest other in elevator.Riders.Values)
                {
                    if (object.ReferenceEquals(other, request) || other.CabinSlot != slot) continue;
                    if (other.Phase is ElevatorRidePhase.Boarding
                        or ElevatorRidePhase.Riding
                        or ElevatorRidePhase.Exiting)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied) continue;
                request.CabinSlot = slot;
                request.CabinOffset = capacity <= 1
                    ? Vector3.zero
                    : slot < CabinOffsets.Length ? CabinOffsets[slot] : Vector3.zero;
                return true;
            }
            return false;
        }

        private bool Fail(ElevatorLogicState? elevator)
        {
            if (elevator != null
                && elevator.Riders.TryGetValue(_rider.Handle, out ElevatorRiderRequest request))
            {
                request.Phase = ElevatorRidePhase.Interrupted;
                request.CabinSlot = -1;
                elevator.Riders.Remove(_rider.Handle);
                DigglesTclEngine.ResetElevatorWhenUnserviced(elevator);
            }
            DigglesTclEngine.UnregisterElevatorRide(_ctx, _rider.Handle, _driveHandle);
            ExitResult = 3;
            return false;
        }

        private void ApplyPosition(Vector3 nativePosition)
        {
            _rider.Position = nativePosition;
            if (_rider.GameObject != null && _ctx.EntitySpawner != null)
                _ctx.EntitySpawner.ApplyPosition(_rider.GameObject, nativePosition);
        }
    }
}
