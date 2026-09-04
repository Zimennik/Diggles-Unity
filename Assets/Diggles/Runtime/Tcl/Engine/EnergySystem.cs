#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Tcl;

    // Port of the native EnergyMgr used by production places and energy sources. A successful
    // acquisition consumes one energy portion immediately and records a timed source load; repeated
    // acquisitions by the same consumer reuse that record until it is released or expires.
    public sealed class EnergySystem
    {
        const int LeaseTicks = 6000;

        sealed class Lease
        {
            public int SourceHandle;
            public int ConsumerHandle;
            public float Amount;
            public int TicksLeft;
        }

        readonly DigglesTclContext _ctx;
        readonly List<Lease> _leases = new();
        readonly List<int> _sourceOrder = new();

        public EnergySystem(DigglesTclContext context)
        {
            _ctx = context;
        }

        public static void RegisterCommands(DigglesTclContext context, TclInterpreter interp)
        {
            var energy = context.Energy;
            interp.RegisterCommand("set_energyrange", (_, args) =>
            {
                RequireArity(args, 3);
                DigglesTclEntity entity = RequireEntity(context, args[1]);
                entity.EnergyRange = Number(args[2]);
                energy.RegisterSource(entity);
                return "";
            });
            interp.RegisterCommand("set_energyclass", (_, args) =>
            {
                RequireArity(args, 3);
                DigglesTclEntity entity = RequireEntity(context, args[1]);
                entity.EnergyClass = Integer(args[2]);
                energy.RegisterSource(entity);
                return "";
            });
            interp.RegisterCommand("set_energyconsumption", (_, args) =>
            {
                RequireArity(args, 3);
                RequireEntity(context, args[1]).EnergyConsumption = Number(args[2]);
                return "";
            });
            interp.RegisterCommand("set_energystore", (_, args) =>
            {
                RequireArity(args, 3);
                RequireEntity(context, args[1]).EnergyStore = Number(args[2]);
                return "";
            });
            interp.RegisterCommand("get_energystore", (_, args) =>
            {
                RequireArity(args, 2);
                return Format(RequireEntity(context, args[1]).EnergyStore);
            });
            interp.RegisterCommand("set_energymaxstore", (_, args) =>
            {
                RequireArity(args, 3);
                RequireEntity(context, args[1]).EnergyMaxStore = Number(args[2]);
                return "";
            });
            interp.RegisterCommand("get_energysourceload", (_, args) =>
            {
                RequireArity(args, 2);
                return Format(energy.GetSourceLoad(RequireEntity(context, args[1])));
            });
            interp.RegisterCommand("check_energy", (_, args) =>
            {
                RequireArity(args, 2);
                return energy.Check(RequireEntity(context, args[1])) ? "1" : "0";
            });
            interp.RegisterCommand("get_energy", (_, args) =>
            {
                RequireArity(args, 2);
                return energy.Acquire(RequireEntity(context, args[1])) ? "1" : "0";
            });
            interp.RegisterCommand("release_energy", (_, args) =>
            {
                RequireArity(args, 2);
                energy.Release(RequireEntity(context, args[1]));
                return "";
            });
        }

        // FUN_005edcd0 -> FUN_005b93d0: zero-cost consumers are always ready. Otherwise a source
        // must belong to the same owner, have an equal-or-higher energy class, be enabled and built,
        // contain one full consumption portion, and lie strictly inside its horizontal range.
        public bool Check(DigglesTclEntity consumer)
        {
            if (consumer.EnergyConsumption <= 0f) return true;
            return FindBestSource(consumer) != null;
        }

        // FUN_005edd80 -> FUN_005b9510: existing leases are idempotent. A new lease selects the
        // qualifying source with the greatest current store, deducts one portion, and lasts for
        // 6000 native logic ticks unless release_energy ends it first.
        public bool Acquire(DigglesTclEntity consumer)
        {
            if (consumer.EnergyConsumption <= 0f) return true;
            PruneDeadLeases();
            for (int i = 0; i < _leases.Count; i++)
                if (_leases[i].ConsumerHandle == consumer.Handle)
                    return true;

            var source = FindBestSource(consumer);
            if (source == null) return false;
            source.EnergyStore -= consumer.EnergyConsumption;
            _leases.Add(new Lease
            {
                SourceHandle = source.Handle,
                ConsumerHandle = consumer.Handle,
                Amount = consumer.EnergyConsumption,
                TicksLeft = LeaseTicks,
            });
            return true;
        }

        public void Release(DigglesTclEntity consumer)
        {
            for (int i = _leases.Count - 1; i >= 0; i--)
                if (_leases[i].ConsumerHandle == consumer.Handle)
                    _leases.RemoveAt(i);
        }

        public float GetSourceLoad(DigglesTclEntity source)
        {
            PruneDeadLeases();
            float result = 0f;
            for (int i = 0; i < _leases.Count; i++)
                if (_leases[i].SourceHandle == source.Handle)
                    result += _leases[i].Amount;
            return result;
        }

        // Called once per 10 Hz game-thread logic tick, matching the native lease countdown in
        // CGameThread::SrvDoThink. Expiry is a safety net for consumers that disappear or fail to
        // execute their normal release path; consumed energy is never refunded.
        public void Tick()
        {
            for (int i = _leases.Count - 1; i >= 0; i--)
            {
                var lease = _leases[i];
                if (!_ctx.Entities.ContainsKey(lease.SourceHandle)
                    || !_ctx.Entities.ContainsKey(lease.ConsumerHandle)
                    || --lease.TicksLeft == 0)
                    _leases.RemoveAt(i);
            }
        }

        public void OnEntityRemoved(int handle)
        {
            for (int i = _leases.Count - 1; i >= 0; i--)
                if (_leases[i].SourceHandle == handle || _leases[i].ConsumerHandle == handle)
                    _leases.RemoveAt(i);
            _sourceOrder.Remove(handle);
        }

        // Clears leases only for an explicit runtime reset. Original.sav loading must use Restore:
        // active native leases and their remaining countdowns are serialized continuation state.
        public void ResetTransientState()
        {
            _leases.Clear();
        }

        public SaveState Capture()
        {
            List<int> sourceHandles = CollectSourceHandles();
            var sources = new SourceSaveState[sourceHandles.Count];
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                int sourceHandle = sourceHandles[sourceIndex];
                DigglesTclEntity source = _ctx.Entities[sourceHandle];
                var leases = new List<LeaseSaveState>();
                for (int leaseIndex = 0; leaseIndex < _leases.Count; leaseIndex++)
                {
                    Lease lease = _leases[leaseIndex];
                    if (lease.SourceHandle != sourceHandle) continue;
                    leases.Add(new LeaseSaveState
                    {
                        ConsumerHandle = lease.ConsumerHandle,
                        Amount = lease.Amount,
                        TicksLeft = lease.TicksLeft,
                    });
                }
                sources[sourceIndex] = new SourceSaveState
                {
                    SourceHandle = sourceHandle,
                    EnergyClass = source.EnergyClass,
                    Range = source.EnergyRange,
                    Leases = leases.ToArray(),
                };
            }
            return new SaveState { Sources = sources };
        }

        public void Restore(SaveState state)
        {
            ValidateSaveState(state, _ctx.Entities);

            _sourceOrder.Clear();
            _leases.Clear();
            foreach (SourceSaveState savedSource in state.Sources)
            {
                DigglesTclEntity source = _ctx.Entities[savedSource.SourceHandle];
                source.EnergyClass = savedSource.EnergyClass;
                source.EnergyRange = savedSource.Range;
                _sourceOrder.Add(savedSource.SourceHandle);
                foreach (LeaseSaveState savedLease in savedSource.Leases)
                {
                    _leases.Add(new Lease
                    {
                        SourceHandle = savedSource.SourceHandle,
                        ConsumerHandle = savedLease.ConsumerHandle,
                        Amount = savedLease.Amount,
                        TicksLeft = savedLease.TicksLeft,
                    });
                }
            }
        }

        public static void ValidateSaveState(
            SaveState? state,
            IReadOnlyDictionary<int, DigglesTclEntity> entities)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            if (state.Sources == null)
                throw new ArgumentException("Energy source state is missing.", nameof(state));

            var sourceHandles = new HashSet<int>();
            foreach (SourceSaveState? source in state.Sources)
            {
                if (source == null)
                    throw new ArgumentException("Energy source state contains a null record.", nameof(state));
                if (!sourceHandles.Add(source.SourceHandle))
                    throw new ArgumentException(
                        $"Energy source {source.SourceHandle} is duplicated.", nameof(state));
                if (!entities.TryGetValue(source.SourceHandle, out DigglesTclEntity? entity))
                    throw new ArgumentException(
                        $"Energy source {source.SourceHandle} does not exist.", nameof(state));
                if (!IsSource(entity))
                    throw new ArgumentException(
                        $"Energy source {source.SourceHandle} is not an energy object.", nameof(state));
                if (!Finite(source.Range))
                    throw new ArgumentException(
                        $"Energy source {source.SourceHandle} has a non-finite range.", nameof(state));
                if (source.Leases == null)
                    throw new ArgumentException(
                        $"Energy source {source.SourceHandle} has no lease collection.", nameof(state));
                foreach (LeaseSaveState? lease in source.Leases)
                {
                    if (lease == null)
                        throw new ArgumentException(
                            $"Energy source {source.SourceHandle} contains a null lease.", nameof(state));
                    if (!entities.ContainsKey(lease.ConsumerHandle))
                        throw new ArgumentException(
                            $"Energy lease consumer {lease.ConsumerHandle} does not exist.", nameof(state));
                    if (!Finite(lease.Amount))
                        throw new ArgumentException(
                            $"Energy lease consumer {lease.ConsumerHandle} has a non-finite amount.", nameof(state));
                }
            }
        }

        DigglesTclEntity? FindBestSource(DigglesTclEntity consumer)
        {
            DigglesTclEntity? best = null;
            float bestStore = float.NegativeInfinity;
            foreach (int sourceHandle in CollectSourceHandles())
            {
                DigglesTclEntity source = _ctx.Entities[sourceHandle];
                if (!IsSource(source) || source.Owner != consumer.Owner) continue;
                if (source.EnergyClass < consumer.EnergyClass) continue;
                if (!source.BuildupComplete || !IsEnabled(source)) continue;
                if (source.EnergyStore < consumer.EnergyConsumption) continue;
                if (source.EnergyStore <= 0f) continue;
                float dx = source.Position.x - consumer.Position.x;
                float dy = source.Position.y - consumer.Position.y;
                if (dx * dx + dy * dy >= source.EnergyRange * source.EnergyRange) continue;
                if (source.EnergyStore <= bestStore) continue;
                best = source;
                bestStore = source.EnergyStore;
            }
            return best;
        }

        static bool IsSource(DigglesTclEntity entity) => entity.Class?.Kind == "energy";

        static bool IsEnabled(DigglesTclEntity entity) =>
            !entity.VarsScope.Vars.TryGetValue("_prod_enabled", out string value) || value != "0";

        void RegisterSource(DigglesTclEntity entity)
        {
            if (!IsSource(entity) || _sourceOrder.Contains(entity.Handle)) return;
            _sourceOrder.Add(entity.Handle);
        }

        List<int> CollectSourceHandles()
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            foreach (int handle in _sourceOrder)
            {
                if (!_ctx.Entities.TryGetValue(handle, out DigglesTclEntity? entity) ||
                    !IsSource(entity) || !seen.Add(handle))
                    continue;
                result.Add(handle);
            }
            foreach (KeyValuePair<int, DigglesTclEntity> pair in _ctx.Entities)
            {
                if (!IsSource(pair.Value) || !seen.Add(pair.Key)) continue;
                result.Add(pair.Key);
            }
            return result;
        }

        void PruneDeadLeases()
        {
            for (int i = _leases.Count - 1; i >= 0; i--)
                if (!_ctx.Entities.ContainsKey(_leases[i].SourceHandle)
                    || !_ctx.Entities.ContainsKey(_leases[i].ConsumerHandle))
                    _leases.RemoveAt(i);
        }

        static DigglesTclEntity RequireEntity(DigglesTclContext context, string value)
        {
            var entity = value == "this" ? context.This : context.GetEntity(value);
            return entity ?? throw new TclException("invalid object reference");
        }

        static void RequireArity(string[] args, int count)
        {
            if (args.Length != count) throw new TclException("wrong # args");
        }

        static float Number(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                throw new TclException("expected floating-point number");
            return result;
        }

        static int Integer(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                throw new TclException("expected integer");
            return result;
        }

        static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public sealed class SaveState
        {
            public SourceSaveState[] Sources = Array.Empty<SourceSaveState>();
        }

        public sealed class SourceSaveState
        {
            public int SourceHandle;
            public int EnergyClass;
            public float Range;
            public LeaseSaveState[] Leases = Array.Empty<LeaseSaveState>();
        }

        public sealed class LeaseSaveState
        {
            public int ConsumerHandle;
            public float Amount;
            public int TicksLeft;
        }
    }
}
