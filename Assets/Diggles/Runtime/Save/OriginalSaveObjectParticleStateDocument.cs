#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;

    public readonly struct OriginalSaveObjectParticleSourceRecord
    {
        internal OriginalSaveObjectParticleSourceRecord(
            int sourceIndex,
            int particleHandle,
            OriginalSaveRaw32 relativePositionX,
            OriginalSaveRaw32 relativePositionY,
            OriginalSaveRaw32 relativePositionZ,
            int rawLinkId)
        {
            SourceIndex = sourceIndex;
            ParticleHandle = particleHandle;
            RelativePositionX = relativePositionX;
            RelativePositionY = relativePositionY;
            RelativePositionZ = relativePositionZ;
            RawLinkId = rawLinkId;
        }

        public int SourceIndex { get; }
        public int ParticleHandle { get; }
        public OriginalSaveRaw32 RelativePositionX { get; }
        public OriginalSaveRaw32 RelativePositionY { get; }
        public OriginalSaveRaw32 RelativePositionZ { get; }
        public int RawLinkId { get; }
        public bool HasParticleHandle => ParticleHandle >= 0;
    }

    public sealed class OriginalSaveObjectParticleStateDocument
    {
        private const int SourceCount = 32;
        private const int SerializedValueCount = SourceCount * 5;

        private readonly ReadOnlyCollection<int> _rawValues;
        private readonly ReadOnlyCollection<OriginalSaveObjectParticleSourceRecord> _sources;

        internal OriginalSaveObjectParticleStateDocument(int[] rawValues)
        {
            if (rawValues == null) throw new ArgumentNullException(nameof(rawValues));
            if (rawValues.Length != 0 && rawValues.Length != SerializedValueCount)
            {
                throw new ArgumentException(
                    $"Native object particle state must contain 0 or {SerializedValueCount} values.",
                    nameof(rawValues));
            }

            var values = (int[])rawValues.Clone();
            _rawValues = Array.AsReadOnly(values);
            if (values.Length == 0)
            {
                _sources = Array.AsReadOnly(Array.Empty<OriginalSaveObjectParticleSourceRecord>());
                return;
            }

            var sources = new OriginalSaveObjectParticleSourceRecord[SourceCount];
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                int vectorOffset = SourceCount + sourceIndex * 3;
                sources[sourceIndex] = new OriginalSaveObjectParticleSourceRecord(
                    sourceIndex,
                    values[sourceIndex],
                    new OriginalSaveRaw32(values[vectorOffset]),
                    new OriginalSaveRaw32(values[vectorOffset + 1]),
                    new OriginalSaveRaw32(values[vectorOffset + 2]),
                    values[SourceCount * 4 + sourceIndex]);
            }
            _sources = Array.AsReadOnly(sources);
        }

        public bool IsPresent => _rawValues.Count != 0;
        public IReadOnlyList<int> RawValues => _rawValues;
        public IReadOnlyList<OriginalSaveObjectParticleSourceRecord> Sources => _sources;
    }

    public sealed class OriginalSaveRuntimeObjectParticleState
    {
        private readonly ReadOnlyCollection<OriginalSaveObjectParticleSourceRecord> _sources;

        internal OriginalSaveRuntimeObjectParticleState(
            OriginalSaveObjectParticleStateDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            IsPresent = source.IsPresent;
            var sources = new OriginalSaveObjectParticleSourceRecord[source.Sources.Count];
            for (int index = 0; index < sources.Length; index++)
                sources[index] = source.Sources[index];
            _sources = Array.AsReadOnly(sources);
        }

        public bool IsPresent { get; }
        public IReadOnlyList<OriginalSaveObjectParticleSourceRecord> Sources => _sources;
    }
}
