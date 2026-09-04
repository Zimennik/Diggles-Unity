#nullable enable

namespace Diggles.Runtime.Sim
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEngine;

    public sealed class LoreTrackWaypoint
    {
        public LoreTrackWaypoint(
            Vector3 position,
            Vector3 rotation,
            float minimumSpeed,
            float maximumSpeed)
        {
            if (!Finite(position)) throw new ArgumentOutOfRangeException(nameof(position));
            if (!Finite(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
            if (!Finite(minimumSpeed)) throw new ArgumentOutOfRangeException(nameof(minimumSpeed));
            if (!Finite(maximumSpeed)) throw new ArgumentOutOfRangeException(nameof(maximumSpeed));

            Position = position;
            Rotation = rotation;
            MinimumSpeed = minimumSpeed;
            MaximumSpeed = maximumSpeed;
        }

        public Vector3 Position { get; }
        public Vector3 Rotation { get; }
        public float MinimumSpeed { get; }
        public float MaximumSpeed { get; }

        internal LoreTrackWaypoint WithSpeeds(float minimumSpeed, float maximumSpeed) =>
            new(Position, Rotation, minimumSpeed, maximumSpeed);

        private static bool Finite(Vector3 value) =>
            Finite(value.x) && Finite(value.y) && Finite(value.z);

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public sealed class LoreTrackRecord
    {
        private const float DefaultMinimumSpeed = 0.5f;
        private const float DefaultMaximumSpeed = 10f;

        private readonly List<LoreTrackWaypoint> _waypoints;
        private readonly ReadOnlyCollection<LoreTrackWaypoint> _waypointsView;
        private float[] _segmentLengths;
        private float _calculatedLength;

        public LoreTrackRecord(
            int ownerId,
            IEnumerable<LoreTrackWaypoint> waypoints,
            float progress,
            float totalLength,
            float speed)
        {
            if (ownerId < 0) throw new ArgumentOutOfRangeException(nameof(ownerId));
            if (waypoints == null) throw new ArgumentNullException(nameof(waypoints));
            if (!Finite(progress) || progress < 0f)
                throw new ArgumentOutOfRangeException(nameof(progress));
            if (!Finite(totalLength) || totalLength < 0f)
                throw new ArgumentOutOfRangeException(nameof(totalLength));
            if (!Finite(speed)) throw new ArgumentOutOfRangeException(nameof(speed));

            OwnerId = ownerId;
            _waypoints = NormalizeWaypoints(waypoints);
            _waypointsView = _waypoints.AsReadOnly();
            _segmentLengths = BuildSegmentLengths(_waypoints);
            _calculatedLength = Sum(_segmentLengths);
            if (!Finite(_calculatedLength))
                throw new ArgumentException("A Lore track length must be finite.", nameof(waypoints));
            if (progress < totalLength && CalculatedLength <= 0f)
            {
                throw new ArgumentException(
                    "An unfinished Lore track must contain a non-zero segment.",
                    nameof(waypoints));
            }
            Progress = progress;
            TotalLength = totalLength;
            Speed = speed;
        }

        public int OwnerId { get; }
        public IReadOnlyList<LoreTrackWaypoint> Waypoints => _waypointsView;
        public float Progress { get; private set; }
        public float TotalLength { get; private set; }
        public float Speed { get; private set; }

        public float CalculatedLength => _calculatedLength;

        internal LoreTrackRecord Copy() =>
            new(OwnerId, _waypoints, Progress, TotalLength, Speed);

        internal bool TrySample(float distance, out LoreTrackSample sample)
        {
            if (_waypoints.Count == 0)
            {
                sample = default;
                return false;
            }

            if (_waypoints.Count == 1)
            {
                LoreTrackWaypoint only = _waypoints[0];
                sample = new LoreTrackSample(
                    only.Position,
                    only.Rotation,
                    only.MinimumSpeed,
                    only.MaximumSpeed,
                    0f,
                    0f);
                return true;
            }

            float clampedDistance = Mathf.Clamp(distance, 0f, TotalLength);
            float traversed = 0f;
            for (int index = 0; index < _segmentLengths.Length; index++)
            {
                float segmentLength = _segmentLengths[index];
                if (segmentLength <= 0f) continue;
                if (clampedDistance >= traversed + segmentLength &&
                    index < _segmentLengths.Length - 1)
                {
                    traversed += segmentLength;
                    continue;
                }

                LoreTrackWaypoint start = _waypoints[index];
                LoreTrackWaypoint end = _waypoints[index + 1];
                float t = Mathf.Clamp01((clampedDistance - traversed) / segmentLength);
                // Native Y supplies the signed downhill term. Native Z is scaled only when
                // measuring the track segment's arc length.
                sample = new LoreTrackSample(
                    Vector3.LerpUnclamped(start.Position, end.Position, t),
                    LerpRotation(start.Rotation, end.Rotation, t),
                    Mathf.LerpUnclamped(start.MinimumSpeed, end.MinimumSpeed, t),
                    Mathf.LerpUnclamped(start.MaximumSpeed, end.MaximumSpeed, t),
                    segmentLength,
                    end.Position.y - start.Position.y);
                return true;
            }

            LoreTrackWaypoint last = _waypoints[^1];
            sample = new LoreTrackSample(
                last.Position,
                last.Rotation,
                last.MinimumSpeed,
                last.MaximumSpeed,
                0f,
                0f);
            return true;
        }

        internal void SetMotion(float speed, float progress)
        {
            Speed = speed;
            Progress = progress;
        }

        internal void TrimBehind(float maximumTrailerOffset)
        {
            if (!Finite(maximumTrailerOffset) || maximumTrailerOffset < 0f)
                throw new ArgumentOutOfRangeException(nameof(maximumTrailerOffset));
            if (_waypoints.Count < 2) return;

            float trimDistance = Mathf.Max(0f, Progress - maximumTrailerOffset);
            float removedDistance = 0f;
            int removeCount = 0;
            while (removeCount < _waypoints.Count - 1 &&
                   removedDistance + _segmentLengths[removeCount] <= trimDistance)
            {
                removedDistance += _segmentLengths[removeCount];
                removeCount++;
            }

            if (removeCount == 0) return;
            _waypoints.RemoveRange(0, removeCount);
            _segmentLengths = BuildSegmentLengths(_waypoints);
            _calculatedLength = Sum(_segmentLengths);
            Progress = Mathf.Max(0f, Progress - removedDistance);
            TotalLength = Mathf.Max(0f, TotalLength - removedDistance);
        }

        private static List<LoreTrackWaypoint> NormalizeWaypoints(
            IEnumerable<LoreTrackWaypoint> waypoints)
        {
            var result = new List<LoreTrackWaypoint>();
            float inheritedMinimum = DefaultMinimumSpeed;
            float inheritedMaximum = DefaultMaximumSpeed;
            foreach (LoreTrackWaypoint waypoint in waypoints)
            {
                if (waypoint == null)
                    throw new ArgumentException("A Lore track waypoint cannot be null.", nameof(waypoints));

                float minimum = waypoint.MinimumSpeed;
                float maximum = waypoint.MaximumSpeed;
                if (minimum == -1f)
                {
                    minimum = inheritedMinimum;
                    maximum = inheritedMaximum;
                }
                else
                {
                    inheritedMinimum = minimum;
                    inheritedMaximum = maximum;
                }
                result.Add(waypoint.WithSpeeds(minimum, maximum));
            }
            return result;
        }

        private static float[] BuildSegmentLengths(IReadOnlyList<LoreTrackWaypoint> waypoints)
        {
            var result = new float[Math.Max(0, waypoints.Count - 1)];
            for (int index = 0; index < result.Length; index++)
            {
                float length = LoreTrackSystem.CalculateSegmentLength(
                    waypoints[index].Position,
                    waypoints[index + 1].Position);
                if (!Finite(length))
                    throw new ArgumentException("A Lore track segment length must be finite.");
                result[index] = length;
            }
            return result;
        }

        private static float Sum(IReadOnlyList<float> values)
        {
            float result = 0f;
            for (int index = 0; index < values.Count; index++) result += values[index];
            return result;
        }

        private static Vector3 LerpRotation(Vector3 start, Vector3 end, float t) =>
            new(
                LerpAngleRadians(start.x, end.x, t),
                LerpAngleRadians(start.y, end.y, t),
                LerpAngleRadians(start.z, end.z, t));

        private static float LerpAngleRadians(float start, float end, float t) =>
            start + Mathf.DeltaAngle(start * Mathf.Rad2Deg, end * Mathf.Rad2Deg) *
            Mathf.Deg2Rad * t;

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public readonly struct LoreTrackTrailer
    {
        public LoreTrackTrailer(int objectId, float offset)
        {
            if (objectId <= 0) throw new ArgumentOutOfRangeException(nameof(objectId));
            if (float.IsNaN(offset) || float.IsInfinity(offset) || offset < 0f)
                throw new ArgumentOutOfRangeException(nameof(offset));
            ObjectId = objectId;
            Offset = offset;
        }

        public int ObjectId { get; }
        public float Offset { get; }
    }

    internal readonly struct LoreTrackSample
    {
        internal LoreTrackSample(
            Vector3 position,
            Vector3 rotation,
            float minimumSpeed,
            float maximumSpeed,
            float segmentLength,
            float verticalDrop)
        {
            Position = position;
            Rotation = rotation;
            MinimumSpeed = minimumSpeed;
            MaximumSpeed = maximumSpeed;
            SegmentLength = segmentLength;
            VerticalDrop = verticalDrop;
        }

        internal Vector3 Position { get; }
        internal Vector3 Rotation { get; }
        internal float MinimumSpeed { get; }
        internal float MaximumSpeed { get; }
        internal float SegmentLength { get; }
        internal float VerticalDrop { get; }
    }

    public sealed class LoreTrackSystem
    {
        public const double NativeTickSeconds = 0.1d;
        private const float NativeTickScale = 0.1f;
        private const float NativeVerticalDistanceScale = 0.5f;

        private readonly DigglesTclContext _context;
        private readonly Dictionary<int, LoreTrackRecord> _records = new();
        private readonly List<int> _recordOrder = new();

        public LoreTrackSystem(DigglesTclContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public int Count => _records.Count;

        public bool Contains(int ownerId) => _records.ContainsKey(ownerId);

        public bool TryGetRecord(int ownerId, out LoreTrackRecord record)
        {
            if (_records.TryGetValue(ownerId, out LoreTrackRecord? found))
            {
                record = found;
                return true;
            }
            record = null!;
            return false;
        }

        public LoreTrackRecord[] CaptureRecords()
        {
            var result = new LoreTrackRecord[_records.Count];
            for (int index = 0; index < _recordOrder.Count; index++)
                result[index] = _records[_recordOrder[index]].Copy();
            return result;
        }

        public void RestoreRecords(IEnumerable<LoreTrackRecord> records)
        {
            ValidateRecords(records);
            var restored = new Dictionary<int, LoreTrackRecord>();
            var restoredOrder = new List<int>();
            foreach (LoreTrackRecord record in records)
            {
                if (record == null)
                    throw new ArgumentException("A Lore track record cannot be null.", nameof(records));
                if (!restored.TryAdd(record.OwnerId, record.Copy()))
                {
                    throw new InvalidOperationException(
                        $"Lore track owner {record.OwnerId} appears more than once.");
                }
                restoredOrder.Add(record.OwnerId);
            }

            _records.Clear();
            _recordOrder.Clear();
            for (int index = 0; index < restoredOrder.Count; index++)
            {
                int ownerId = restoredOrder[index];
                _records.Add(ownerId, restored[ownerId]);
                _recordOrder.Add(ownerId);
            }
        }

        public static void ValidateRecords(IEnumerable<LoreTrackRecord>? records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var owners = new HashSet<int>();
            foreach (LoreTrackRecord? record in records)
            {
                if (record == null)
                    throw new ArgumentException("A Lore track record cannot be null.", nameof(records));
                if (!owners.Add(record.OwnerId))
                    throw new ArgumentException("A Lore track owner appears more than once.", nameof(records));
                _ = record.Copy();
            }
        }

        public bool Tick(
            int ownerId,
            float gravity,
            float friction,
            IReadOnlyList<LoreTrackTrailer> trailers)
        {
            if (!Finite(gravity)) throw new ArgumentOutOfRangeException(nameof(gravity));
            if (!Finite(friction)) throw new ArgumentOutOfRangeException(nameof(friction));
            if (trailers == null) throw new ArgumentNullException(nameof(trailers));
            if (!_records.TryGetValue(ownerId, out LoreTrackRecord? record))
                throw new InvalidOperationException($"Lore track owner {ownerId} has no registry record.");
            if (!_context.Entities.TryGetValue(ownerId, out DigglesTclEntity? owner))
                throw new InvalidOperationException($"Lore track owner entity {ownerId} is missing.");
            if (record.Progress >= record.TotalLength) return false;
            if (!record.TrySample(record.Progress, out LoreTrackSample head))
                throw new InvalidOperationException($"Lore track owner {ownerId} has no usable waypoints.");

            float speed = record.Speed;
            if (head.SegmentLength > 0f)
            {
                speed += head.VerticalDrop / head.SegmentLength * gravity * NativeTickScale /
                    (1 + trailers.Count);
            }

            ApplyTransform(owner, head.Position, head.Rotation);
            for (int index = 0; index < trailers.Count; index++)
            {
                LoreTrackTrailer trailer = trailers[index];
                if (!_context.Entities.TryGetValue(trailer.ObjectId, out DigglesTclEntity? entity))
                {
                    throw new InvalidOperationException(
                        $"Lore trailer entity {trailer.ObjectId} for owner {ownerId} is missing.");
                }
                if (!record.TrySample(record.Progress - trailer.Offset, out LoreTrackSample sample))
                {
                    throw new InvalidOperationException(
                        $"Lore trailer {trailer.ObjectId} for owner {ownerId} has no usable track position.");
                }
                ApplyTransform(entity, sample.Position, SanitizeRotation(sample.Rotation));
            }

            speed -= friction * NativeTickScale;
            speed = Mathf.Clamp(speed, head.MinimumSpeed, head.MaximumSpeed);
            record.SetMotion(speed, record.Progress + speed * NativeTickScale);
            return record.Progress < record.TotalLength;
        }

        public void Complete(int ownerId, IReadOnlyList<LoreTrackTrailer> trailers)
        {
            if (trailers == null) throw new ArgumentNullException(nameof(trailers));
            if (!_records.TryGetValue(ownerId, out LoreTrackRecord? record))
                throw new InvalidOperationException($"Lore track owner {ownerId} has no registry record.");

            float maximumOffset = 0f;
            for (int index = 0; index < trailers.Count; index++)
                maximumOffset = Mathf.Max(maximumOffset, trailers[index].Offset);
            record.TrimBehind(maximumOffset);
        }

        public static float CalculateSegmentLength(Vector3 start, Vector3 end)
        {
            Vector3 difference = end - start;
            difference.z *= NativeVerticalDistanceScale;
            return difference.magnitude;
        }

        private void ApplyTransform(DigglesTclEntity entity, Vector3 position, Vector3 rotation)
        {
            entity.Position = position;
            entity.Rotation = rotation;
            if (entity.GameObject == null || _context.EntitySpawner == null) return;
            _context.EntitySpawner.ApplyPosition(entity.GameObject, position);
            _context.EntitySpawner.ApplyRotation(entity.GameObject, rotation);
        }

        private static Vector3 SanitizeRotation(Vector3 value) =>
            new(
                Mathf.Abs(value.x) >= 10000f ? 0f : value.x,
                Mathf.Abs(value.y) >= 10000f ? 0f : value.y,
                Mathf.Abs(value.z) >= 10000f ? 0f : value.z);

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
