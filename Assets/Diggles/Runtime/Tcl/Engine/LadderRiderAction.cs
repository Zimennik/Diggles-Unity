#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using Diggles.Runtime.Navigation;
    using UnityEngine;

    public enum LadderRiderDirection
    {
        None = 0,
        Up = -1,
        Down = 1,
    }

    /// <summary>
    /// Scalar native vertical-ladder integrator used by <see cref="LadderRiderAction"/>.
    /// </summary>
    public sealed class LadderRiderMover
    {
        public const float NativeTickSeconds = 0.1f;
        public const float DownVelocityStep = 0.25f;
        public const float DownVelocityCap = 2.5f;

        const float CoordinateEpsilon = 1e-5f;

        readonly float _upSpeed;
        float _downVelocity;
        float _downTickRemaining = NativeTickSeconds;

        public float CurrentCoordinate { get; private set; }
        public float TargetCoordinate { get; }
        public float DownVelocity => _downVelocity;

        public LadderRiderDirection Direction
        {
            get
            {
                if (CurrentCoordinate > TargetCoordinate + CoordinateEpsilon)
                    return LadderRiderDirection.Up;
                if (CurrentCoordinate < TargetCoordinate - CoordinateEpsilon)
                    return LadderRiderDirection.Down;
                return LadderRiderDirection.None;
            }
        }

        public LadderRiderMover(
            float currentCoordinate,
            float targetCoordinate,
            float upSpeed)
        {
            if (!float.IsFinite(currentCoordinate))
                throw new ArgumentOutOfRangeException(nameof(currentCoordinate));
            if (!float.IsFinite(targetCoordinate))
                throw new ArgumentOutOfRangeException(nameof(targetCoordinate));
            if (!float.IsFinite(upSpeed) || upSpeed <= 0f)
                throw new ArgumentOutOfRangeException(nameof(upSpeed));

            CurrentCoordinate = currentCoordinate;
            TargetCoordinate = targetCoordinate;
            _upSpeed = upSpeed;
            if (Mathf.Abs(CurrentCoordinate - TargetCoordinate) <= CoordinateEpsilon)
                CurrentCoordinate = TargetCoordinate;
        }

        /// <summary>
        /// Advances by elapsed seconds while preserving the native 0.1-second down-velocity steps.
        /// </summary>
        public bool Tick(float deltaTime)
        {
            float remaining = Mathf.Max(0f, deltaTime);
            LadderRiderDirection direction = Direction;
            if (direction == LadderRiderDirection.None) return false;

            if (direction == LadderRiderDirection.Up)
            {
                CurrentCoordinate = Mathf.Max(
                    TargetCoordinate,
                    CurrentCoordinate - _upSpeed * remaining);
                return Direction != LadderRiderDirection.None;
            }

            while (remaining > CoordinateEpsilon && Direction == LadderRiderDirection.Down)
            {
                if (_downTickRemaining <= CoordinateEpsilon)
                    AdvanceDownVelocityTick();

                float slice = Mathf.Min(remaining, _downTickRemaining);
                CurrentCoordinate = Mathf.Min(
                    TargetCoordinate,
                    CurrentCoordinate + _downVelocity * slice);
                remaining -= slice;
                _downTickRemaining -= slice;
                if (_downTickRemaining <= CoordinateEpsilon)
                    AdvanceDownVelocityTick();
            }
            return Direction != LadderRiderDirection.None;
        }

        void AdvanceDownVelocityTick()
        {
            // FUN_0060bc70: down first advances by entry[+0xc] × 0.1, then adds
            // DAT_00737058 = 0.25 and caps at DAT_007373d8 = 2.5. FUN_0060c3c0
            // initializes +0xc to zero for every new ride, preventing cycle-to-cycle drift.
            _downVelocity = Mathf.Min(DownVelocityCap, _downVelocity + DownVelocityStep);
            _downTickRemaining = NativeTickSeconds;
        }
    }

    /// <summary>
    /// Dedicated CTransportLogic vertical-ladder rider; wall climbing remains in ClimbAction.
    /// </summary>
    public sealed class LadderRiderAction : IEntityAction
    {
        public const float MannFallbackUpSpeed = 1.4145935f;

        readonly DigglesAnimController? _controller;
        readonly Transform? _outer;
        readonly Vector3 _anchor;
        readonly Vector3 _downAxis;
        readonly Vector3 _target;
        readonly Quaternion _facing;
        readonly int _animset;

        LadderRiderMover? _mover;

        public LadderRiderAction(
            DigglesAnimController? controller,
            Transform? outer,
            Vector3 anchor,
            Vector3 downAxis,
            Vector3 target,
            Quaternion facing,
            int animset)
        {
            if (!float.IsFinite(anchor.x) || !float.IsFinite(anchor.y) || !float.IsFinite(anchor.z))
                throw new ArgumentOutOfRangeException(nameof(anchor));
            if (!float.IsFinite(target.x) || !float.IsFinite(target.y) || !float.IsFinite(target.z))
                throw new ArgumentOutOfRangeException(nameof(target));
            if (downAxis.sqrMagnitude <= 1e-6f)
                throw new ArgumentException("A non-zero ladder down axis is required.", nameof(downAxis));

            _controller = controller;
            _outer = outer;
            _anchor = anchor;
            _downAxis = downAxis.normalized;
            _target = target;
            _facing = facing;
            _animset = animset;
        }

        public bool Tick(double dt)
        {
            if (_outer == null) return false;
            if (_mover == null) InitializeMover();

            LadderRiderDirection direction = _mover!.Direction;
            if (direction == LadderRiderDirection.None)
            {
                SnapToLadder();
                return false;
            }

            ReassertRiderAnimation(direction);
            bool running = _mover.Tick((float)Math.Max(0.0, dt));
            SnapToLadder();
            return running;
        }

        /// <summary>
        /// Returns the first waypoint after one same-source ladder ride, including tagged floors.
        /// </summary>
        public static int FindContinuousRideEnd(
            IReadOnlyList<NavWaypoint> path,
            int startIndex)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if ((uint)startIndex >= (uint)path.Count)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            NavWaypoint first = path[startIndex];
            if (!first.IsWall || !first.IsLadder || first.TransportSourceHandle < 0)
                throw new ArgumentException("The ride must start on a sourced ladder wall.", nameof(startIndex));

            int end = startIndex + 1;
            while (end < path.Count
                && path[end].IsLadder
                && path[end].TransportSourceHandle == first.TransportSourceHandle)
            {
                end++;
            }
            return end;
        }

        void InitializeMover()
        {
            _controller?.SetActiveAnimset(_animset);
            if (_controller?.Animator != null)
                _controller.Animator.PlaybackSpeed = 1f;

            float currentCoordinate = Vector3.Dot(_outer!.position - _anchor, _downAxis);
            float targetCoordinate = Vector3.Dot(_target - _anchor, _downAxis);
            float upSpeed = _controller?.NativeLadderUpSpeed(_animset) ?? 0f;
            if (upSpeed <= 0.0001f && _animset != 0)
                upSpeed = _controller?.NativeLadderUpSpeed(0) ?? 0f;
            if (upSpeed <= 0.0001f) upSpeed = MannFallbackUpSpeed;
            _mover = new LadderRiderMover(currentCoordinate, targetCoordinate, upSpeed);
        }

        void ReassertRiderAnimation(LadderRiderDirection direction)
        {
            // FUN_0060bc70 calls FUN_0060bb30 every rider tick. That helper resolves the factual
            // current model/frame and selects slot 0x0e (up) or 0x0f (down); DigglesAnimator's
            // same-loop guard prevents a matching reassert from rewinding the loop.
            string state = direction == LadderRiderDirection.Up
                ? "ladder_climb_up"
                : "ladder_climb_down";
            _controller?.PlayState(state, DigglesAnimator.AnimMode.Loop);
        }

        void SnapToLadder()
        {
            // FUN_0060bc70 hard-snaps every rider tick to (ladder.x, rider.y, ladder.z + 1).
            // FUN_005cd8e0:222 supplies the fixed native rotation (0, pi, 0).
            _outer!.position = _anchor + _downAxis * _mover!.CurrentCoordinate;
            _outer.rotation = _facing;
        }
    }
}
