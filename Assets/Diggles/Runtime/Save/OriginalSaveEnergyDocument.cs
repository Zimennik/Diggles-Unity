#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using Diggles.Runtime.Tcl.Engine;

    public readonly struct OriginalSaveEnergyLeaseRecord
    {
        internal OriginalSaveEnergyLeaseRecord(
            int consumerObjectId,
            OriginalSaveRaw32 amount,
            int remainingTicks)
        {
            ConsumerObjectId = consumerObjectId;
            Amount = amount;
            RemainingTicks = remainingTicks;
        }

        public int ConsumerObjectId { get; }
        public OriginalSaveRaw32 Amount { get; }
        public int RemainingTicks { get; }
    }

    public sealed class OriginalSaveEnergySourceRecord
    {
        private readonly ReadOnlyCollection<OriginalSaveEnergyLeaseRecord> _leases;

        internal OriginalSaveEnergySourceRecord(
            int sourceObjectId,
            int energyClass,
            OriginalSaveRaw32 range,
            OriginalSaveEnergyLeaseRecord[] leases)
        {
            SourceObjectId = sourceObjectId;
            EnergyClass = energyClass;
            Range = range;
            _leases = Array.AsReadOnly(leases ?? throw new ArgumentNullException(nameof(leases)));
        }

        public int SourceObjectId { get; }
        public int EnergyClass { get; }
        public OriginalSaveRaw32 Range { get; }
        public IReadOnlyList<OriginalSaveEnergyLeaseRecord> Leases => _leases;
    }

    public sealed class OriginalSaveEnergyDocument
    {
        private readonly ReadOnlyCollection<OriginalSaveEnergySourceRecord> _sources;

        internal OriginalSaveEnergyDocument(
            byte[] sourcePayload,
            OriginalSaveControlMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.ControlRecordSectionOffset);
            int end = checked((int)metadata.Marker50Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            int offset = start;
            int sourceCount = ReadInt32(sourcePayload, ref offset);
            if (sourceCount != metadata.ControlRecordCount || sourceCount < 0)
                throw new InvalidDataException("Native EnergyMgr source count disagrees with structural metadata.");

            var sources = new OriginalSaveEnergySourceRecord[sourceCount];
            int leaseCount = 0;
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                int sourceObjectId = ReadInt32(sourcePayload, ref offset);
                int energyClass = ReadInt32(sourcePayload, ref offset);
                var range = new OriginalSaveRaw32(ReadInt32(sourcePayload, ref offset));
                int sourceLeaseCount = ReadInt32(sourcePayload, ref offset);
                if (sourceLeaseCount < 0)
                    throw new InvalidDataException(
                        $"Native EnergyMgr source {sourceObjectId} has a negative lease count.");
                var leases = new OriginalSaveEnergyLeaseRecord[sourceLeaseCount];
                for (int leaseIndex = 0; leaseIndex < leases.Length; leaseIndex++)
                {
                    leases[leaseIndex] = new OriginalSaveEnergyLeaseRecord(
                        ReadInt32(sourcePayload, ref offset),
                        new OriginalSaveRaw32(ReadInt32(sourcePayload, ref offset)),
                        ReadInt32(sourcePayload, ref offset));
                }
                leaseCount = checked(leaseCount + sourceLeaseCount);
                sources[sourceIndex] = new OriginalSaveEnergySourceRecord(
                    sourceObjectId,
                    energyClass,
                    range,
                    leases);
            }

            if (leaseCount != metadata.NestedControlRecordCount)
                throw new InvalidDataException("Native EnergyMgr lease count disagrees with structural metadata.");
            if (offset != end)
                throw new InvalidDataException("Typed EnergyMgr parsing did not consume its section.");
            _sources = Array.AsReadOnly(sources);
        }

        public IReadOnlyList<OriginalSaveEnergySourceRecord> Sources => _sources;

        private static int ReadInt32(byte[] source, ref int offset)
        {
            int value = OriginalSaveWorldDocument.ReadInt32(source, offset);
            offset += sizeof(int);
            return value;
        }
    }

    public sealed class OriginalSaveRuntimeEnergySnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveRuntimeEnergySourceSnapshot> _sources;

        internal OriginalSaveRuntimeEnergySnapshot(OriginalSaveEnergyDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var sources = new OriginalSaveRuntimeEnergySourceSnapshot[source.Sources.Count];
            for (int index = 0; index < sources.Length; index++)
                sources[index] = new OriginalSaveRuntimeEnergySourceSnapshot(source.Sources[index]);
            _sources = Array.AsReadOnly(sources);
        }

        public IReadOnlyList<OriginalSaveRuntimeEnergySourceSnapshot> Sources => _sources;

        public EnergySystem.SaveState CreateSaveState()
        {
            var sources = new EnergySystem.SourceSaveState[Sources.Count];
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                OriginalSaveRuntimeEnergySourceSnapshot source = Sources[sourceIndex];
                var leases = new EnergySystem.LeaseSaveState[source.Leases.Count];
                for (int leaseIndex = 0; leaseIndex < leases.Length; leaseIndex++)
                {
                    OriginalSaveEnergyLeaseRecord lease = source.Leases[leaseIndex];
                    leases[leaseIndex] = new EnergySystem.LeaseSaveState
                    {
                        ConsumerHandle = lease.ConsumerObjectId,
                        Amount = lease.Amount.AsSingle,
                        TicksLeft = lease.RemainingTicks,
                    };
                }
                sources[sourceIndex] = new EnergySystem.SourceSaveState
                {
                    SourceHandle = source.SourceObjectId,
                    EnergyClass = source.EnergyClass,
                    Range = source.Range,
                    Leases = leases,
                };
            }
            return new EnergySystem.SaveState { Sources = sources };
        }
    }

    public sealed class OriginalSaveRuntimeEnergySourceSnapshot
    {
        private readonly ReadOnlyCollection<OriginalSaveEnergyLeaseRecord> _leases;

        internal OriginalSaveRuntimeEnergySourceSnapshot(OriginalSaveEnergySourceRecord source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SourceObjectId = source.SourceObjectId;
            EnergyClass = source.EnergyClass;
            Range = source.Range.AsSingle;
            var leases = new OriginalSaveEnergyLeaseRecord[source.Leases.Count];
            for (int index = 0; index < leases.Length; index++)
                leases[index] = source.Leases[index];
            _leases = Array.AsReadOnly(leases);
        }

        public int SourceObjectId { get; }
        public int EnergyClass { get; }
        public float Range { get; }
        public IReadOnlyList<OriginalSaveEnergyLeaseRecord> Leases => _leases;
    }
}
