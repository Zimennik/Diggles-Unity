#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Tcl;
    using UnityEngine;

    // Deterministic port of the native CFluid session state. The original stores one normalized
    // fill value per world cell and converts source units through the block's 10-bit capacity.
    // Sources, barriers, queries, and flow all live here; FluidPresenter only observes snapshots.
    public sealed class FluidSystem
    {
        public const float SourceUnitScale = 128f;
        // Diggles.exe.rdata constants used by 00577b20..005789b0 and 005791a0.
        public const float NativeFillCutoff = 0.001f;
        public const float NativeActivityCutoff = 0.004f;
        public const float NativeSourceWakeMomentum = 0.05f;
        public const float NativeWaterRelaxation = 1.5f;
        public const float NativeSulfurRelaxation = 1.4f;
        public const float NativeLavaRelaxation = 1.3f;
        public const float NativeWaterMomentum = 0.5f;
        public const float NativeSulfurMomentum = 0.3f;
        public const float NativeLavaMomentum = 0.125f;
        public const int NativeFluidMinimumCapacity = 32;
        public const int NativeLavaMinimumCapacity = 128;
        public const int NativeFluidMinimumAperture = 8;
        public const int NativeLavaMinimumAperture = 16;
        public const int NativeStopperQuantization = 15;
        const float EmptyEpsilon = 0.00001f;
        const float MaxValidatedFill = 1_000_000f;
        const int MaxSavedCells = 1_000_000;
        const int MaxSavedObjects = 100_000;
        const float NativeSubCellScale = 0.125f;

        public enum FluidKind
        {
            None = 0,
            Water = 1,
            Sulfur = 2,
            Lava = 3,
            LavaWater = 4,
        }

        public sealed class FluidSource
        {
            public Vector3 PositionOffset;
            public string Type = "water";
            public int TypeMask = (int)FluidKind.Water;
            public double Vpf;
            public double ConfiguredVolume;
            public double Volume;
            public double Height;
            public bool HasVpf;
            public bool HasVolume;
            public bool HasHeight;
            public bool Vented;
        }

        public sealed class FluidStopper
        {
            public Vector2 Start;
            public Vector2 End;
            public float Permeability;
        }

        public readonly struct CellSnapshot
        {
            public readonly int X;
            public readonly int Y;
            public readonly float Fill;
            public readonly int TypeMask;

            public CellSnapshot(int x, int y, float fill, int typeMask)
            {
                X = x;
                Y = y;
                Fill = fill;
                TypeMask = typeMask;
            }
        }

        sealed class Cell
        {
            public float Fill;
            public int TypeMask;
        }

        sealed class FlowState
        {
            public float SnapshotFill;
            public int SnapshotTypeMask;
            public float PreviousX;
            public float CurrentX;
            public float PreviousY;
            public float CurrentY;
        }

        readonly struct FlowShape
        {
            public int Capacity { get; }
            public int Lower { get; }
            public int Upper { get; }
            public int HorizontalAperture { get; }
            public int VerticalAperture { get; }

            public FlowShape(
                int capacity,
                int lower,
                int upper,
                int horizontalAperture,
                int verticalAperture)
            {
                Capacity = capacity;
                Lower = lower;
                Upper = upper;
                HorizontalAperture = horizontalAperture;
                VerticalAperture = verticalAperture;
            }
        }

        [Serializable]
        public sealed class SaveState
        {
            public bool Enabled;
            public CellSaveState[] Cells = Array.Empty<CellSaveState>();
            public SourceSaveState[] Sources = Array.Empty<SourceSaveState>();
            public StopperSaveState[] Stoppers = Array.Empty<StopperSaveState>();
        }

        [Serializable]
        public struct CellSaveState
        {
            public int X, Y, TypeMask;
            public float Fill;
        }

        [Serializable]
        public struct SourceSaveState
        {
            public int Handle, TypeMask;
            public float PositionX, PositionY, PositionZ;
            public string Type;
            public double Vpf, ConfiguredVolume, Volume, Height;
            public bool HasVpf, HasVolume, HasHeight, Vented;
        }

        [Serializable]
        public struct StopperSaveState
        {
            public int Handle;
            public float StartX, StartY, EndX, EndY, Permeability;
        }

        readonly DigglesTclContext _ctx;
        readonly Dictionary<long, Cell> _cells = new();
        readonly Dictionary<long, FlowState> _flowStates = new();
        readonly Dictionary<int, FluidSource> _sources = new();
        readonly Dictionary<int, FluidStopper> _stoppers = new();
        // Native keeps sources/stoppers in a creation-ordered list and the archive
        // serializes that order; the dictionaries alone can't guarantee it once a
        // handle is removed, so the order is tracked explicitly.
        readonly List<int> _sourceOrder = new();
        readonly List<int> _stopperOrder = new();
        readonly List<(int x, int y)> _activeCoordinates = new();
        readonly HashSet<long> _activeKeys = new();
        readonly List<(int x, int y)> _nextActiveCoordinates = new();
        readonly HashSet<long> _nextActiveKeys = new();
        readonly HashSet<long> _sourceDirtyKeys = new();
        private readonly HashSet<long> _navigationDirtyKeys = new();
        private readonly Func<int, int, bool> _isNavigationSubCellUnderwater;

        public bool Enabled { get; private set; }
        public IReadOnlyDictionary<int, FluidSource> Sources => _sources;
        public IReadOnlyDictionary<int, FluidStopper> Stoppers => _stoppers;
        public int CellCount => _cells.Count;
        public event Action? Changed;

        public FluidSystem(DigglesTclContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
            _isNavigationSubCellUnderwater = IsNavigationSubCellUnderwater;
        }

        public static void RegisterCommands(DigglesTclContext context, TclInterpreter interp)
        {
            var fluids = context.Fluids;
            interp.RegisterCommand("set_water", (_, args) =>
            {
                RequireArity(args, 2);
                fluids.SetEnabled(ParseBoolean(args[1]));
                return "";
            });
            interp.RegisterCommand("set_fsource", (_, args) =>
            {
                if (args.Length < 2 || (args.Length & 1) != 0)
                    throw new TclException("wrong # args: should be \"set_fsource object ?option value ...?\"");
                var entity = RequireEntity(context, args[1]);
                fluids.SetSource(entity.Handle, args);
                return "";
            });
            interp.RegisterCommand("get_fsource", (_, args) =>
            {
                RequireArity(args, 3);
                var entity = RequireEntity(context, args[1]);
                return fluids.GetSourceValue(entity.Handle, args[2]);
            });
            interp.RegisterCommand("rem_fsource", (_, args) =>
            {
                RequireArity(args, 2);
                fluids.RemoveSource(RequireEntity(context, args[1]).Handle);
                return "";
            });
            interp.RegisterCommand("set_fstopper", (_, args) =>
            {
                RequireArity(args, 5);
                var entity = RequireEntity(context, args[1]);
                fluids.SetStopper(entity.Handle, ParseVector2(args[2]), ParseVector2(args[3]),
                    Mathf.Clamp01(Number(args[4])));
                return "";
            });
            interp.RegisterCommand("rem_fstopper", (_, args) =>
            {
                RequireArity(args, 2);
                fluids.RemoveStopper(RequireEntity(context, args[1]).Handle);
                return "";
            });
            interp.RegisterCommand("get_fill", (_, args) =>
            {
                RequireArity(args, 3);
                int x = NativeRound(Number(args[1]));
                int y = NativeRound(Number(args[2]));
                return Format(fluids.GetFill(x, y));
            });
            interp.RegisterCommand("isunderwater", (_, args) =>
            {
                RequireArity(args, 2);
                return fluids.IsUnderwater(ParseVector3(args[1])) ? "1" : "0";
            });
            interp.RegisterCommand("get_waterheight", (_, args) =>
            {
                RequireArity(args, 2);
                return FormatVector3(fluids.GetWaterHeight(ParseVector3(args[1])));
            });
        }

        public void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            if (enabled && _activeCoordinates.Count == 0) PrimeAllFluidCells();
            SynchronizeNavigationWater();
            Changed?.Invoke();
        }

        public float GetFill(int x, int y)
        {
            return _cells.TryGetValue(Key(x, y), out var cell) ? cell.Fill : 0f;
        }

        public static Vector2Int ResolveSourceCell(Vector3 ownerPosition, Vector3 positionOffset)
        {
            return new Vector2Int(
                (int)(ownerPosition.x + positionOffset.x),
                (int)(ownerPosition.y + positionOffset.y));
        }

        public static float CalculateNativeTransfer(
            FluidKind kind,
            float currentSurface,
            float neighborSurface,
            int aperture,
            float permeability,
            float currentAmount,
            float neighborAmount,
            float momentum)
        {
            if ((int)kind < (int)FluidKind.Water || (int)kind > (int)FluidKind.LavaWater ||
                aperture <= 0 || permeability <= 0f)
                return 0f;

            bool lava = kind == FluidKind.Lava;
            int effectiveAperture = (int)(aperture * Mathf.Clamp01(permeability));
            if (effectiveAperture == 0) return 0f;
            effectiveAperture = Mathf.Max(
                effectiveAperture,
                lava ? NativeLavaMinimumAperture : NativeFluidMinimumAperture);

            float relaxation = kind switch
            {
                FluidKind.Sulfur => NativeSulfurRelaxation,
                FluidKind.Lava => NativeLavaRelaxation,
                _ => NativeWaterRelaxation,
            };
            float momentumCoefficient = kind switch
            {
                FluidKind.Sulfur => NativeSulfurMomentum,
                FluidKind.Lava => NativeLavaMomentum,
                _ => NativeWaterMomentum,
            };
            // Every kernel computes neighbor -> current from the snapshot pressure, then clamps
            // against the two live amounts (00577b20:71-98; vertical twin 00577e00:71-98).
            float transfer = (PressureSurface(neighborSurface) - PressureSurface(currentSurface)) *
                NativeSubCellScale * NativeSubCellScale * effectiveAperture * relaxation +
                momentum * momentumCoefficient;
            return Mathf.Clamp(transfer, -Mathf.Max(currentAmount, 0f), Mathf.Max(neighborAmount, 0f));
        }

        public int GetFluidType(int x, int y)
        {
            if (!Enabled) return 0;
            return _cells.TryGetValue(Key(x, y), out var cell) && cell.Fill > EmptyEpsilon
                ? cell.TypeMask & 7
                : 0;
        }

        public bool IsUnderwater(Vector3 nativePosition)
        {
            if (!Enabled) return false;
            int x = Mathf.FloorToInt(nativePosition.x);
            int y = Mathf.FloorToInt(nativePosition.y);
            float fill = GetFill(x, y);
            if (fill <= EmptyEpsilon || !TryGetShape(x, y, out _, out int lower, out int upper))
                return false;

            float fractionY = nativePosition.y - y;
            float sampleFromBottom = (1f - fractionY) * 8f;
            float filledSpan = (upper - lower) * fill + (8f - upper);
            return sampleFromBottom < filledSpan;
        }

        public Vector3 GetWaterHeight(Vector3 nativePosition)
        {
            if (!Enabled) return nativePosition;
            int x = Mathf.FloorToInt(nativePosition.x);
            int y = Mathf.FloorToInt(nativePosition.y);

            int scan = y;
            for (int i = 0; GetFill(x, scan) >= 1f; i++)
            {
                if (i == 8) return nativePosition;
                scan--;
            }
            for (int i = 0; GetFill(x, scan) <= EmptyEpsilon; i++)
            {
                if (i == 8) return nativePosition;
                scan++;
            }

            float fill = Mathf.Min(1f, GetFill(x, scan));
            if (!TryGetShape(x, scan, out _, out int lower, out int upper)) return nativePosition;
            float surfaceY = scan + 1f - ((upper - lower) * fill + (8f - upper)) * 0.125f;
            return new Vector3(nativePosition.x, surfaceY, nativePosition.z);
        }

        public CellSnapshot[] CaptureCells()
        {
            var result = new CellSnapshot[_cells.Count];
            int index = 0;
            foreach (var pair in _cells)
            {
                DecodeKey(pair.Key, out int x, out int y);
                result[index++] = new CellSnapshot(x, y, pair.Value.Fill, pair.Value.TypeMask);
            }
            Array.Sort(result, (left, right) => left.Y != right.Y
                ? left.Y.CompareTo(right.Y)
                : left.X.CompareTo(right.X));
            return result;
        }

        public void Tick()
        {
            if (!Enabled) return;
            bool changed = RemoveInvalidCells();
            changed |= ApplySources();
            changed |= FlowActiveCells();
            changed |= RemoveEmptyCells();
            RebuildActiveCells();
            if (changed)
            {
                SynchronizeDirtyNavigationWater();
                Changed?.Invoke();
            }
        }

        public void OnEntityRemoved(int handle)
        {
            if (_sources.Remove(handle))
                _sourceOrder.Remove(handle);
            if (_stoppers.Remove(handle))
                _stopperOrder.Remove(handle);
        }

        public void Reset()
        {
            bool hadCells = _cells.Count > 0 || Enabled;
            Enabled = false;
            _cells.Clear();
            _flowStates.Clear();
            _sources.Clear();
            _stoppers.Clear();
            _sourceOrder.Clear();
            _stopperOrder.Clear();
            _activeCoordinates.Clear();
            _activeKeys.Clear();
            _nextActiveCoordinates.Clear();
            _nextActiveKeys.Clear();
            _sourceDirtyKeys.Clear();
            SynchronizeNavigationWater();
            if (hadCells) Changed?.Invoke();
        }

        public SaveState Capture()
        {
            var cells = CaptureCells();
            var savedCells = new CellSaveState[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                savedCells[i] = new CellSaveState
                {
                    X = cells[i].X,
                    Y = cells[i].Y,
                    Fill = cells[i].Fill,
                    TypeMask = cells[i].TypeMask,
                };

            var sources = new SourceSaveState[_sourceOrder.Count];
            for (int i = 0; i < _sourceOrder.Count; i++)
            {
                int handle = _sourceOrder[i];
                var source = _sources[handle];
                sources[i] = new SourceSaveState
                {
                    Handle = handle,
                    PositionX = source.PositionOffset.x,
                    PositionY = source.PositionOffset.y,
                    PositionZ = source.PositionOffset.z,
                    Type = source.Type,
                    TypeMask = source.TypeMask,
                    Vpf = source.Vpf,
                    ConfiguredVolume = source.ConfiguredVolume,
                    Volume = source.Volume,
                    Height = source.Height,
                    HasVpf = source.HasVpf,
                    HasVolume = source.HasVolume,
                    HasHeight = source.HasHeight,
                    Vented = source.Vented,
                };
            }

            var stoppers = new StopperSaveState[_stopperOrder.Count];
            for (int i = 0; i < _stopperOrder.Count; i++)
            {
                int handle = _stopperOrder[i];
                var stopper = _stoppers[handle];
                stoppers[i] = new StopperSaveState
                {
                    Handle = handle,
                    StartX = stopper.Start.x,
                    StartY = stopper.Start.y,
                    EndX = stopper.End.x,
                    EndY = stopper.End.y,
                    Permeability = stopper.Permeability,
                };
            }
            return new SaveState { Enabled = Enabled, Cells = savedCells, Sources = sources, Stoppers = stoppers };
        }

        public void Restore(SaveState state)
        {
            ValidateSaveState(state, _ctx.Terrain?.Width ?? 0, _ctx.Terrain?.Height ?? 0, null);
            _cells.Clear();
            _flowStates.Clear();
            _sources.Clear();
            _stoppers.Clear();
            _sourceOrder.Clear();
            _stopperOrder.Clear();
            _activeCoordinates.Clear();
            _activeKeys.Clear();
            _nextActiveCoordinates.Clear();
            _nextActiveKeys.Clear();
            _sourceDirtyKeys.Clear();
            Enabled = state.Enabled;
            foreach (var saved in state.Cells)
            {
                long key = Key(saved.X, saved.Y);
                int typeMask = saved.TypeMask & 7;
                _cells[key] = new Cell { Fill = saved.Fill, TypeMask = typeMask };
                _flowStates[key] = new FlowState
                {
                    SnapshotFill = saved.Fill,
                    SnapshotTypeMask = typeMask,
                };
            }
            foreach (var saved in state.Sources)
            {
                if (!_sources.ContainsKey(saved.Handle))
                    _sourceOrder.Add(saved.Handle);
                _sources[saved.Handle] = new FluidSource
                {
                    PositionOffset = new Vector3(saved.PositionX, saved.PositionY, saved.PositionZ),
                    Type = saved.Type ?? "water",
                    TypeMask = saved.TypeMask & 7,
                    Vpf = saved.Vpf,
                    ConfiguredVolume = saved.ConfiguredVolume,
                    Volume = saved.Volume,
                    Height = saved.Height,
                    HasVpf = saved.HasVpf,
                    HasVolume = saved.HasVolume,
                    HasHeight = saved.HasHeight,
                    Vented = saved.Vented,
                };
            }
            foreach (var saved in state.Stoppers)
            {
                if (!_stoppers.ContainsKey(saved.Handle))
                    _stopperOrder.Add(saved.Handle);
                _stoppers[saved.Handle] = new FluidStopper
                {
                    Start = new Vector2(saved.StartX, saved.StartY),
                    End = new Vector2(saved.EndX, saved.EndY),
                    Permeability = saved.Permeability,
                };
            }
            if (Enabled) PrimeAllFluidCells();
            SynchronizeNavigationWater();
            Changed?.Invoke();
        }

        private void SynchronizeNavigationWater()
        {
            var nav = _ctx.Terrain?.Nav;
            if (nav == null) return;
            nav.RebuildWaterFlags(_isNavigationSubCellUnderwater);
            _navigationDirtyKeys.Clear();
        }

        private void SynchronizeDirtyNavigationWater()
        {
            var nav = _ctx.Terrain?.Nav;
            if (nav == null) return;
            foreach (long key in _navigationDirtyKeys)
            {
                DecodeKey(key, out int x, out int y);
                nav.RebuildWaterFlagsForTile(x, y, _isNavigationSubCellUnderwater);
            }
            _navigationDirtyKeys.Clear();
        }

        private bool IsNavigationSubCellUnderwater(int subX, int subY)
        {
            if (!Enabled) return false;
            int tileX = subX >> 3;
            int tileY = subY >> 3;
            float fill = GetFill(tileX, tileY);
            if (fill <= EmptyEpsilon ||
                !TryGetShape(tileX, tileY, out _, out int lower, out int upper))
            {
                return false;
            }

            float sampleFromBottom = 7.5f - (subY & 7);
            float filledSpan = (upper - lower) * fill + (8f - upper);
            return sampleFromBottom < filledSpan;
        }

        public static void ValidateSaveState(
            SaveState? state,
            int worldWidth,
            int worldHeight,
            Predicate<int>? validHandle)
        {
            if (state == null || state.Cells == null || state.Sources == null || state.Stoppers == null)
                throw new ArgumentException("Fluid save tables are missing.", nameof(state));
            if (state.Cells.Length > MaxSavedCells || state.Sources.Length > MaxSavedObjects ||
                state.Stoppers.Length > MaxSavedObjects)
                throw new ArgumentException("Fluid save tables are too large.", nameof(state));

            var cellKeys = new HashSet<long>();
            foreach (var cell in state.Cells)
            {
                if (!Finite(cell.Fill) || cell.Fill <= 0f || cell.Fill > MaxValidatedFill ||
                    cell.TypeMask <= 0 || cell.TypeMask > 7 || !cellKeys.Add(Key(cell.X, cell.Y)))
                    throw new ArgumentException("Fluid cell data is invalid.", nameof(state));
                if (worldWidth > 0 && worldHeight > 0 &&
                    ((uint)cell.X >= (uint)worldWidth || (uint)cell.Y >= (uint)worldHeight))
                    throw new ArgumentException("Fluid cell lies outside the current world.", nameof(state));
            }

            var sourceHandles = new HashSet<int>();
            foreach (var source in state.Sources)
            {
                if (source.Handle <= 0 || !sourceHandles.Add(source.Handle) ||
                    (validHandle != null && !validHandle(source.Handle)) ||
                    !Finite(source.PositionX) || !Finite(source.PositionY) || !Finite(source.PositionZ) ||
                    !Finite(source.Vpf) || !Finite(source.ConfiguredVolume) ||
                    !Finite(source.Volume) || !Finite(source.Height) ||
                    source.TypeMask <= 0 || source.TypeMask > 7)
                    throw new ArgumentException("Fluid source data is invalid.", nameof(state));
            }

            var stopperHandles = new HashSet<int>();
            foreach (var stopper in state.Stoppers)
            {
                if (stopper.Handle <= 0 || !stopperHandles.Add(stopper.Handle) ||
                    (validHandle != null && !validHandle(stopper.Handle)) ||
                    !Finite(stopper.StartX) || !Finite(stopper.StartY) ||
                    !Finite(stopper.EndX) || !Finite(stopper.EndY) ||
                    !Finite(stopper.Permeability) || stopper.Permeability < 0f || stopper.Permeability > 1f)
                    throw new ArgumentException("Fluid stopper data is invalid.", nameof(state));
            }
        }

        void SetSource(int handle, string[] args)
        {
            if (!_sources.TryGetValue(handle, out var source))
            {
                source = new FluidSource();
                _sources.Add(handle, source);
                _sourceOrder.Add(handle);
            }

            bool setHeight = false;
            bool setVpf = false;
            for (int i = 2; i + 1 < args.Length; i += 2)
            {
                string option = args[i];
                string value = args[i + 1];
                switch (option)
                {
                    case "-pos":
                        source.PositionOffset = ParseVector3(value);
                        break;
                    case "-type":
                        source.Type = NormalizeType(value, out int typeMask);
                        source.TypeMask = typeMask;
                        break;
                    case "-volume":
                    {
                        double parsed = NumberDouble(value);
                        // FUN_0057b2a0 uses exactly -10000 as its no-update sentinel. It does
                        // not clear an already configured finite budget.
                        if (parsed != -10000.0)
                        {
                            source.ConfiguredVolume = parsed;
                            source.Volume = parsed;
                            source.HasVolume = true;
                        }
                        break;
                    }
                    case "-height":
                    {
                        double parsed = NumberDouble(value);
                        if (parsed != -10000.0)
                        {
                            source.Height = parsed;
                            setHeight = true;
                        }
                        break;
                    }
                    case "-vpf":
                    {
                        double parsed = NumberDouble(value);
                        if (parsed != -10000.0)
                        {
                            source.Vpf = parsed;
                            setVpf = true;
                        }
                        break;
                    }
                    case "-ventil":
                        source.Vented = ParseBoolean(value);
                        break;
                }
            }

            // FUN_0057b2a0 stores height and VPF in mutually exclusive fields. If both options
            // occur in one command, VPF is applied last by the native helper and therefore wins.
            if (setVpf)
            {
                source.HasVpf = true;
                source.HasHeight = false;
            }
            else if (setHeight)
            {
                source.HasHeight = true;
                source.HasVpf = false;
            }
        }

        string GetSourceValue(int handle, string option)
        {
            _sources.TryGetValue(handle, out var source);
            return option switch
            {
                "-pos" => FormatVector3(source?.PositionOffset ?? Vector3.zero),
                "-type" => source?.Type ?? "water",
                "-volume" => Format(source != null && source.HasVolume ? source.Volume : 0.0),
                "-height" => Format(source != null && source.HasHeight ? source.Height : 0.0),
                "-vpf" => Format(source != null && source.HasVpf ? source.Vpf : 0.0),
                "-ventil" => source != null && source.Vented ? "1" : "0",
                _ => "0",
            };
        }

        void RemoveSource(int handle)
        {
            if (_sources.Remove(handle))
                _sourceOrder.Remove(handle);
        }

        void SetStopper(int handle, Vector2 start, Vector2 end, float permeability)
        {
            if (!_stoppers.ContainsKey(handle))
                _stopperOrder.Add(handle);
            _stoppers[handle] = new FluidStopper
            {
                Start = start,
                End = end,
                Permeability = permeability,
            };
        }

        void RemoveStopper(int handle)
        {
            if (_stoppers.Remove(handle))
                _stopperOrder.Remove(handle);
        }

        private bool ApplySources()
        {
            if (_sources.Count == 0) return false;
            bool changed = false;
            foreach (int handle in _sourceOrder)
            {
                if (!_ctx.Entities.TryGetValue(handle, out var entity)) continue;
                FluidSource source = _sources[handle];
                Vector3 sourcePosition = entity.Position + source.PositionOffset;
                Vector2Int sourceCell = ResolveSourceCell(entity.Position, source.PositionOffset);
                int x = sourceCell.x;
                int y = sourceCell.y;
                if (!TryGetShape(x, y, out int capacity, out _, out _) ||
                    (source.HasVolume && source.Volume <= 0.0))
                {
                    continue;
                }

                float oldAmount = GetFill(x, y) * capacity;
                float newAmount = oldAmount;
                if (source.HasHeight)
                {
                    newAmount = Mathf.Max((float)source.Height, 0f) * capacity;
                    newAmount = LimitSourceAmount(oldAmount, newAmount, source);
                }
                else if (source.HasVpf)
                {
                    newAmount = Mathf.Max(oldAmount + (float)(source.Vpf * SourceUnitScale), 0f);
                    newAmount = LimitSourceAmount(oldAmount, newAmount, source);
                }

                if (source.Vented)
                {
                    // 0057a1c0:168-208: add the bounded surface/source-Y error in volume units.
                    float provisionalFill = newAmount / capacity;
                    float surfaceY = FindFluidSurfaceY(sourcePosition, x, y, provisionalFill);
                    float ventedAmount = newAmount + (surfaceY - sourcePosition.y) * capacity;
                    newAmount = LimitSourceAmount(oldAmount, Mathf.Max(ventedAmount, 0f), source);
                }

                float actualDelta = newAmount - oldAmount;
                int typeMask = newAmount > EmptyEpsilon ? source.TypeMask & 7 : 0;
                bool cellChanged = SetCell(x, y, newAmount / capacity, typeMask);
                if (!cellChanged) continue;

                if (source.HasVolume)
                    source.Volume = Math.Max(0.0, source.Volume - Math.Abs(actualDelta) / SourceUnitScale);
                long key = Key(x, y);
                _sourceDirtyKeys.Add(key);
                GetOrCreateFlowState(key).CurrentY = NativeSourceWakeMomentum;
                changed = true;
            }
            return changed;
        }

        private static float LimitSourceAmount(float oldAmount, float requestedAmount, FluidSource source)
        {
            if (!source.HasVolume) return requestedAmount;
            float delta = requestedAmount - oldAmount;
            float limit = (float)(source.Volume * SourceUnitScale);
            return oldAmount + Mathf.Sign(delta) * Mathf.Min(Mathf.Abs(delta), limit);
        }

        private float FindFluidSurfaceY(
            Vector3 sourcePosition,
            int overrideX,
            int overrideY,
            float overrideFill)
        {
            int x = (int)sourcePosition.x;
            int y = (int)sourcePosition.y;
            int scan = y;
            for (int count = 0; FillForVent(x, scan, overrideX, overrideY, overrideFill) >= 1f; count++)
            {
                if (count == 8) return scan;
                scan--;
            }
            for (int count = 0; FillForVent(x, scan, overrideX, overrideY, overrideFill) == 0f; count++)
            {
                if (count == 8) return scan;
                scan++;
            }

            float fill = Mathf.Min(FillForVent(x, scan, overrideX, overrideY, overrideFill), 1f);
            if (!TryGetShape(x, scan, out _, out int lower, out int upper)) return scan;
            return scan + 1f - ((upper - lower) * fill + (8f - upper)) * NativeSubCellScale;
        }

        private float FillForVent(int x, int y, int overrideX, int overrideY, float overrideFill)
        {
            return x == overrideX && y == overrideY ? overrideFill : GetFill(x, y);
        }

        private bool FlowActiveCells()
        {
            if (_activeCoordinates.Count == 0) return false;
            _activeCoordinates.Sort((left, right) => left.y != right.y
                ? left.y.CompareTo(right.y)
                : left.x.CompareTo(right.x));

            foreach ((int x, int y) in _activeCoordinates)
            {
                long key = Key(x, y);
                FlowState state = GetOrCreateFlowState(key);
                state.SnapshotFill = GetFill(x, y);
                state.SnapshotTypeMask = GetFluidTypeRaw(x, y);
                state.PreviousX = state.CurrentX;
                state.PreviousY = state.CurrentY;
                state.CurrentX = 0f;
            }

            bool changed = false;
            foreach ((int x, int y) in _activeCoordinates)
            {
                FlowState state = GetOrCreateFlowState(Key(x, y));
                float momentumX = state.PreviousX + PreviousXAt(x - 1, y);
                float momentumY = state.PreviousY + PreviousYAt(x, y - 1);
                int typeMask = state.SnapshotTypeMask & 7;
                if (typeMask == 0)
                {
                    typeMask = SnapshotTypeAt(x, y - 1);
                    if (typeMask == 0) typeMask = SnapshotTypeAt(x - 1, y);
                    state.SnapshotTypeMask = typeMask;
                }

                FluidKind kind = (FluidKind)typeMask;
                if ((int)kind >= (int)FluidKind.Water && (int)kind <= (int)FluidKind.LavaWater)
                {
                    changed |= TransferNativeBoundary(x, y, x, y - 1, true, kind, momentumY);
                    changed |= TransferNativeBoundary(x, y, x - 1, y, false, kind, momentumX);
                }
                state.CurrentX = momentumX;
                state.CurrentY = momentumY;
            }
            return changed;
        }

        private bool TransferNativeBoundary(
            int currentX,
            int currentY,
            int neighborX,
            int neighborY,
            bool vertical,
            FluidKind kind,
            float momentum)
        {
            if (!TryGetFlowShape(currentX, currentY, out FlowShape currentShape) ||
                !TryGetFlowShape(neighborX, neighborY, out FlowShape neighborShape))
            {
                return false;
            }

            int neighborType = SnapshotTypeAt(neighborX, neighborY);
            if (neighborType != 0 && neighborType != (int)kind) return false;

            bool lava = kind == FluidKind.Lava;
            int minimumCapacity = lava ? NativeLavaMinimumCapacity : NativeFluidMinimumCapacity;
            int currentCapacity = Mathf.Max(currentShape.Capacity, minimumCapacity);
            int neighborCapacity = Mathf.Max(neighborShape.Capacity, minimumCapacity);
            float currentAmount = GetFill(currentX, currentY) * currentCapacity;
            float neighborAmount = GetFill(neighborX, neighborY) * neighborCapacity;
            float currentSurface = SurfaceHeight(currentShape, SnapshotFillAt(currentX, currentY), 0f);
            float neighborSurface = SurfaceHeight(
                neighborShape,
                SnapshotFillAt(neighborX, neighborY),
                vertical ? 8f : 0f);
            int aperture = vertical ? currentShape.VerticalAperture : currentShape.HorizontalAperture;
            float permeability = PermeabilityBetween(currentX, currentY, neighborX, neighborY);
            float transfer = CalculateNativeTransfer(
                kind,
                currentSurface,
                neighborSurface,
                aperture,
                permeability,
                currentAmount,
                neighborAmount,
                momentum);
            if (Mathf.Abs(transfer) <= EmptyEpsilon) return false;

            float newCurrentFill = (currentAmount + transfer) / currentCapacity;
            float newNeighborFill = (neighborAmount - transfer) / neighborCapacity;
            if (newCurrentFill < NativeFillCutoff) newCurrentFill = 0f;
            if (newNeighborFill < NativeFillCutoff) newNeighborFill = 0f;
            int outputType = (int)kind;
            bool currentChanged = SetCell(
                currentX,
                currentY,
                newCurrentFill,
                newCurrentFill > 0f ? outputType : 0);
            bool neighborChanged = SetCell(
                neighborX,
                neighborY,
                newNeighborFill,
                newNeighborFill > 0f ? outputType : 0);
            return currentChanged || neighborChanged;
        }

        private void RebuildActiveCells()
        {
            _nextActiveCoordinates.Clear();
            _nextActiveKeys.Clear();
            foreach ((int x, int y) in _activeCoordinates)
            {
                FlowState state = GetOrCreateFlowState(Key(x, y));
                bool verticalActive = Mathf.Abs(state.CurrentY) > NativeActivityCutoff &&
                    (GetFill(x, y) != 0f || GetFill(x, y - 1) != 0f);
                bool horizontalActive = Mathf.Abs(state.CurrentX) > NativeActivityCutoff &&
                    (GetFill(x, y) != 0f || GetFill(x - 1, y) != 0f);
                if (verticalActive || horizontalActive)
                {
                    AddNextActive(x, y);
                    AddNextActive(x + 1, y);
                    AddNextActive(x, y + 1);
                }
                if (horizontalActive) AddNextActive(x - 1, y);
                if (verticalActive) AddNextActive(x, y - 1);
            }

            foreach (long key in _sourceDirtyKeys)
            {
                DecodeKey(key, out int x, out int y);
                AddNextActive(x, y);
                AddNextActive(x + 1, y);
                AddNextActive(x, y + 1);
            }
            _sourceDirtyKeys.Clear();

            _activeCoordinates.Clear();
            _activeKeys.Clear();
            foreach ((int x, int y) in _nextActiveCoordinates)
            {
                _activeCoordinates.Add((x, y));
                _activeKeys.Add(Key(x, y));
            }
        }

        private void PrimeAllFluidCells()
        {
            foreach (long key in _cells.Keys)
            {
                DecodeKey(key, out int x, out int y);
                GetOrCreateFlowState(key).CurrentY = NativeSourceWakeMomentum;
                AddActive(x, y);
                AddActive(x + 1, y);
                AddActive(x, y + 1);
            }
        }

        private void AddActive(int x, int y)
        {
            if (!InWorld(x, y)) return;
            long key = Key(x, y);
            if (_activeKeys.Add(key)) _activeCoordinates.Add((x, y));
        }

        private void AddNextActive(int x, int y)
        {
            if (!InWorld(x, y)) return;
            long key = Key(x, y);
            if (_nextActiveKeys.Add(key)) _nextActiveCoordinates.Add((x, y));
        }

        private bool InWorld(int x, int y)
        {
            var terrain = _ctx.Terrain;
            return terrain != null && (uint)x < (uint)terrain.Width && (uint)y < (uint)terrain.Height;
        }

        private FlowState GetOrCreateFlowState(long key)
        {
            if (_flowStates.TryGetValue(key, out FlowState state)) return state;
            state = new FlowState();
            _flowStates.Add(key, state);
            return state;
        }

        private float PreviousXAt(int x, int y)
        {
            return _flowStates.TryGetValue(Key(x, y), out FlowState state) ? state.PreviousX : 0f;
        }

        private float PreviousYAt(int x, int y)
        {
            return _flowStates.TryGetValue(Key(x, y), out FlowState state) ? state.PreviousY : 0f;
        }

        private float SnapshotFillAt(int x, int y)
        {
            return _flowStates.TryGetValue(Key(x, y), out FlowState state) ? state.SnapshotFill : 0f;
        }

        private int SnapshotTypeAt(int x, int y)
        {
            return _flowStates.TryGetValue(Key(x, y), out FlowState state)
                ? state.SnapshotTypeMask & 7
                : 0;
        }

        private bool TryGetFlowShape(int x, int y, out FlowShape shape)
        {
            var terrain = _ctx.Terrain;
            var bdb = terrain?.Bdb;
            if (terrain == null || bdb == null || (uint)x >= (uint)terrain.Width ||
                (uint)y >= (uint)terrain.Height)
            {
                shape = default;
                return false;
            }

            int index = y * terrain.Width + x;
            if ((uint)index >= (uint)terrain.BlockIdx.Length ||
                terrain.BlockIdx[index] >= bdb.Header.NBlocks)
            {
                shape = default;
                return false;
            }

            // The current south/east cell owns its north/west boundary. Capacity is not an
            // openness test: 00577b20/00577e00 return when the applicable seven-bit rate is zero.
            uint descriptor = bdb.AuxFluidDescriptor((int)terrain.BlockIdx[index]);
            int capacity = (int)(descriptor & 0x3ffu);
            int upper = (int)((descriptor >> 10) & 0xfu);
            int lower = (int)((descriptor >> 14) & 0xfu);
            if (capacity == 0 || lower > upper || upper > 8)
            {
                shape = default;
                return false;
            }

            shape = new FlowShape(
                capacity,
                lower,
                upper,
                (int)(descriptor >> 25),
                (int)((descriptor >> 18) & 0x7fu));
            return true;
        }

        private static float SurfaceHeight(FlowShape shape, float fill, float rowOffset)
        {
            return (shape.Upper - shape.Lower) * fill + (8f + rowOffset - shape.Upper);
        }

        private static float PressureSurface(float surface)
        {
            return surface > 8f ? (surface - 8f) * 8f + 8f : surface;
        }

        private float PermeabilityBetween(int x0, int y0, int x1, int y1)
        {
            float result = 1f;
            foreach (int handle in _stopperOrder)
            {
                if (!_stoppers.TryGetValue(handle, out FluidStopper stopper) ||
                    !_ctx.Entities.TryGetValue(handle, out var entity))
                {
                    continue;
                }
                int baseX = Mathf.FloorToInt(entity.Position.x + 0.5f);
                int baseY = Mathf.FloorToInt(entity.Position.y);
                float ax = baseX + stopper.Start.x;
                float ay = baseY + stopper.Start.y;
                float bx = baseX + stopper.End.x;
                float by = baseY + stopper.End.y;
                float width = Mathf.Abs(bx - ax);
                float height = Mathf.Abs(by - ay);
                bool horizontalBarrier = width > height;
                bool verticalMove = x0 == x1;
                if (horizontalBarrier != verticalMove) continue;

                if (verticalMove)
                {
                    float boundary = Mathf.Max(y0, y1);
                    float sampleX = x0 + 0.5f;
                    if (sampleX < Mathf.Min(ax, bx) || sampleX > Mathf.Max(ax, bx) ||
                        boundary < Mathf.Min(ay, by) || boundary > Mathf.Max(ay, by))
                        continue;
                }
                else
                {
                    float boundary = Mathf.Max(x0, x1);
                    float sampleY = y0 + 0.5f;
                    if (sampleY < Mathf.Min(ay, by) || sampleY > Mathf.Max(ay, by) ||
                        boundary < Mathf.Min(ax, bx) || boundary > Mathf.Max(ax, bx))
                        continue;
                }
                result *= stopper.Permeability;
                int obstruction = (int)((1f - result) * NativeStopperQuantization);
                result = 1f - obstruction / (float)NativeStopperQuantization;
                if (result <= 0f) return 0f;
            }
            return result;
        }

        bool RemoveInvalidCells()
        {
            if (_cells.Count == 0) return false;
            var remove = new List<long>();
            foreach (long key in _cells.Keys)
            {
                DecodeKey(key, out int x, out int y);
                if (!TryGetShape(x, y, out _, out _, out _)) remove.Add(key);
            }
            foreach (long key in remove)
            {
                _cells.Remove(key);
                _navigationDirtyKeys.Add(key);
            }
            return remove.Count > 0;
        }

        bool RemoveEmptyCells()
        {
            if (_cells.Count == 0) return false;
            var remove = new List<long>();
            foreach (var pair in _cells)
                if (pair.Value.Fill <= EmptyEpsilon || pair.Value.TypeMask == 0) remove.Add(pair.Key);
            foreach (long key in remove)
            {
                _cells.Remove(key);
                _navigationDirtyKeys.Add(key);
            }
            return remove.Count > 0;
        }

        private bool SetCell(int x, int y, float fill, int typeMask)
        {
            long key = Key(x, y);
            if (fill <= EmptyEpsilon || typeMask == 0)
            {
                if (_cells.Remove(key))
                {
                    _navigationDirtyKeys.Add(key);
                    return true;
                }
                return false;
            }
            float updatedFill = Mathf.Max(fill, 0f);
            int updatedType = typeMask & 7;
            if (!_cells.TryGetValue(key, out var cell))
            {
                cell = new Cell();
                _cells.Add(key, cell);
            }
            else if (cell.Fill == updatedFill && cell.TypeMask == updatedType)
            {
                return false;
            }
            cell.Fill = updatedFill;
            cell.TypeMask = updatedType;
            _navigationDirtyKeys.Add(key);
            return true;
        }

        int GetFluidTypeRaw(int x, int y)
        {
            return _cells.TryGetValue(Key(x, y), out var cell) ? cell.TypeMask & 7 : 0;
        }

        bool TryGetShape(int x, int y, out int capacity, out int lower, out int upper)
        {
            var terrain = _ctx.Terrain;
            if (terrain != null && terrain.TryGetFluidShape(x, y, out capacity, out lower, out upper))
                return capacity > 0;
            capacity = 0;
            lower = 0;
            upper = 8;
            return false;
        }

        static DigglesTclEntity RequireEntity(DigglesTclContext context, string value)
        {
            return context.ResolveEntity(value);
        }

        static void RequireArity(string[] args, int count)
        {
            if (args.Length != count) throw new TclException("wrong # args");
        }

        static float Number(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) || !Finite(result))
                throw new TclException("expected floating-point number");
            return result;
        }

        static double NumberDouble(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ||
                double.IsNaN(result) || double.IsInfinity(result))
                throw new TclException("expected floating-point number");
            return result;
        }

        static bool ParseBoolean(string value)
        {
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
                return numeric != 0;
            throw new TclException("expected boolean value");
        }

        static Vector2 ParseVector2(string value)
        {
            var parts = TclList.Parse(value);
            if (parts.Count < 2) throw new TclException("expected two-component vector");
            return new Vector2(Number(parts[0]), Number(parts[1]));
        }

        static Vector3 ParseVector3(string value)
        {
            var parts = TclList.Parse(value);
            if (parts.Count < 2) throw new TclException("expected vector");
            return new Vector3(Number(parts[0]), Number(parts[1]), parts.Count > 2 ? Number(parts[2]) : 0f);
        }

        static string NormalizeType(string value, out int typeMask)
        {
            switch (value.ToLowerInvariant())
            {
                case "water": typeMask = (int)FluidKind.Water; return "water";
                case "sulfur": typeMask = (int)FluidKind.Sulfur; return "sulfur";
                case "lava": typeMask = (int)FluidKind.Lava; return "lava";
                case "lavawater": typeMask = (int)FluidKind.LavaWater; return "lavawater";
                default: typeMask = (int)FluidKind.Water; return "water";
            }
        }

        static int NativeRound(float value)
        {
            return value >= 0f ? Mathf.FloorToInt(value + 0.5f) : Mathf.CeilToInt(value - 0.5f);
        }

        static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string FormatVector3(Vector3 value) =>
            Format(value.x) + " " + Format(value.y) + " " + Format(value.z);

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        static long Key(int x, int y) => ((long)x << 32) | (uint)y;

        static void DecodeKey(long key, out int x, out int y)
        {
            x = (int)(key >> 32);
            y = (int)key;
        }
    }
}
