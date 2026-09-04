#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Stats;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine;
    using UnityEngine.Scripting;

    public interface IOriginalSaveRuntimePatchMapper
    {
        OriginalSaveCurrentArchivePatch Capture(
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context);

        OriginalSaveCanonicalCapture CaptureCanonical(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            DigglesTclContext context);
    }

    public sealed class OriginalSaveRuntimePatchMapper : IOriginalSaveRuntimePatchMapper
    {
        private const int NativeAttributePageSize = 512;
        private const int NativeDigEnableFirstSlot = 6;
        private const int NativeDigEnableCount = 5;

        private static readonly HashSet<string> _empiricalProductionItems = new(
            new[]
            {
                "Abfluss", "Aufzug", "Bar", "Barbetrieb", "Bier", "Bordell",
                "Bowlingbahn", "Brauerei", "Dampfaufzug", "Dampfhammer", "Dampfmaschine",
                "Disco", "Dojo", "Dreherei", "Eisen", "Eisenerz", "Eisenerz_", "Energie",
                "Energie__", "Farm", "Feuerstelle", "Fitnessstudio", "Golderz", "Grabstein",
                "Grenzstein", "Grillhamster", "Grillhamster_", "Grillhamster__", "Grillpilz",
                "Grillpilz_", "Grillpilz__", "Grosse_Holzkiepe", "Hamster", "Hauklotz",
                "Heiltrank", "Hochofen", "Holzkiepe", "Holztuer", "Hoverboard", "Industriebad",
                "Industriekueche", "Industrieschlafzimmer", "Industriewohnzimmer", "Kettensaege",
                "Keule", "Kleiner_Heiltrank", "Kohle", "Krankenhaus", "Kristallerz",
                "Kristallschmiede", "Labor", "Lager", "Laufrad", "Leiter", "Leiter_Metall",
                "Luxusbad", "Luxuskueche", "Luxusschlafzimmer", "Luxuswohnzimmer", "Metalltuer",
                "Mittelalterbad", "Mittelalterkueche", "Mittelalterschlafzimmer",
                "Mittelalterwohnzimmer", "Pilz", "Pilzbrot__", "Plattmachfalle", "Powerpotion",
                "Presslufthammer", "Raupe", "Raupenschleimkuchen__", "Raupensuppe_",
                "Raupensuppe__", "Reithamster", "Saegewerk", "Schild", "Schleiferei", "Schmelze",
                "Schmiede", "Schreinerei", "Schubkarren", "Schule", "Steinmetz",
                "Steinschleuder", "Steintuer", "Streitaxt", "Taucherglocke", "Tempel", "Theater",
                "Tischlerei", "Wachhaus", "Waffenschmiede", "Wasserrad", "Zelt", "_Automatisch",
                "_Bewachen_mittel", "_Bewachen_nah", "_Bewachen_weit", "_Buecher_einlagern",
                "_Eisen_einlagern", "_Gold_einlagern", "_Kisten_einlagern", "_Kohle_einlagern",
                "_Kristall_einlagern", "_Kungfu", "_Nahrung_einlagern", "_Offen",
                "_Pilzhut_einlagern", "_Pilzstamm_einlagern", "_Ringe_einlagern",
                "_Rohmineralien_einlagern", "_Schusswaffen", "_Schwertkampf",
                "_Stein_einlagern", "_Traenke_einlagern", "_Verschlossen", "_Verteidigung",
                "_Waffen_einlagern", "_Werkzeuge_einlagern", "_Zweihandkampf",
            },
            StringComparer.Ordinal);

        private static readonly HashSet<string> _warnedUnmappedProductionItems =
            new(StringComparer.Ordinal);

        [Preserve]
        public OriginalSaveRuntimePatchMapper() { }

        // Capture profiling: per-section totals in milliseconds, off during play. Public because the
        // Editor test assembly has no InternalsVisibleTo into Runtime.
        public static bool ProfileCapture;
        public static readonly Dictionary<string, long> ProfileMs = new();

        private static System.Diagnostics.Stopwatch? ProfStart() =>
            ProfileCapture ? System.Diagnostics.Stopwatch.StartNew() : null;

        private static void ProfAdd(string label, System.Diagnostics.Stopwatch? clock)
        {
            if (clock == null) return;
            ProfileMs.TryGetValue(label, out long total);
            ProfileMs[label] = total + clock.ElapsedMilliseconds;
        }

        public OriginalSaveCurrentArchivePatch Capture(
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (baseline.SourceDocument.Metadata.Version !=
                OriginalSaveArchiveReader.CurrentVersion)
            {
                throw new NotSupportedException(
                    $"Runtime writing is not implemented for original save version 0x{baseline.SourceDocument.Metadata.Version:X}.");
            }
            var canonicalClock = ProfStart();
            OriginalSaveCanonicalCapture canonical = CaptureCanonical(baseline, context);
            ProfAdd("capture.canonical", canonicalClock);
            bool objectGraphChanged = ObjectGraphChanged(baseline.Entities, canonical.Entities);

            var archivePatch = new OriginalSaveCurrentArchivePatch
            {
                GameTime = canonical.GameTime,
                CanonicalCapture = canonical,
                RebuildStructureSections = objectGraphChanged,
            };
            bool hasTerrain = canonical.Terrain != null;
            bool hasFog = canonical.FogOfWar != null;
            if (hasTerrain != hasFog)
                throw new InvalidOperationException(
                    "The live world must expose terrain and fog together at a native save boundary.");
            if (hasTerrain)
            {
                var worldClock = ProfStart();
                archivePatch.World = CaptureWorldPatch(
                    baseline.World,
                    baseline.RuntimeWorld,
                    canonical.Terrain!,
                    canonical.FogOfWar!,
                    canonical.Fluid,
                    context);
                ProfAdd("capture.worldPatch", worldClock);
            }
            if (!objectGraphChanged)
            {
                var patchClock = ProfStart();
                for (int index = 0; index < baseline.Entities.Count; index++)
                {
                    OriginalSaveObjectCorePatch? entityPatch = CaptureEntityPatch(
                        baseline.Entities[index],
                        canonical.Entities[index]);
                    if (entityPatch != null) archivePatch.SetObject(entityPatch);
                }
                ProfAdd("capture.entityPatches", patchClock);
            }
            return archivePatch;
        }

        private static bool ObjectGraphChanged(
            IReadOnlyList<OriginalSaveRuntimeEntitySnapshot> retained,
            IReadOnlyList<OriginalSaveCanonicalEntityState> live)
        {
            if (retained.Count != live.Count) return true;
            for (int index = 0; index < retained.Count; index++)
                if (retained[index].ObjectId != live[index].ObjectId) return true;
            return false;
        }

        private static OriginalSaveObjectCorePatch? CaptureEntityPatch(
            OriginalSaveRuntimeEntitySnapshot source,
            OriginalSaveCanonicalEntityState live)
        {
            var patch = new OriginalSaveObjectCorePatch(source.ObjectId);
            bool changed = false;
            if (!SameVector(source.Position, live.Position))
            {
                patch.Position = NativeVector(live.Position);
                changed = true;
            }
            if (!SameVector(source.Velocity, live.Velocity))
            {
                patch.Velocity = NativeVector(live.Velocity);
                changed = true;
            }
            if (!SameVector(source.Rotation, live.Rotation))
            {
                patch.Rotation = NativeVector(live.Rotation);
                changed = true;
            }
            if (source.WorkStart != live.WorkStart)
            {
                patch.WorkStart = live.WorkStart;
                changed = true;
            }
            if (source.WorkLength != live.WorkLength)
            {
                patch.WorkLength = live.WorkLength;
                changed = true;
            }
            if (!string.Equals(source.InstanceName, live.InstanceName, StringComparison.Ordinal))
            {
                patch.InstanceName = live.InstanceName;
                changed = true;
            }
            if (!string.Equals(source.DisplayState, live.InfoString, StringComparison.Ordinal))
            {
                patch.DisplayState = live.InfoString;
                changed = true;
            }
            if (source.StateLiveWord != live.StateLiveIndex)
            {
                patch.StateLiveWord = live.StateLiveIndex;
                changed = true;
            }
            if (source.StateTargetWord != live.StateTargetIndex)
            {
                patch.StateTargetWord = live.StateTargetIndex;
                changed = true;
            }
            if (source.StateEnabledWord != live.StateEnabledWord)
            {
                patch.StateEnabledWord = live.StateEnabledWord;
                changed = true;
            }
            if (source.FogOfWarSizeX != live.FogOfWarSizeX)
            {
                patch.FogOfWarSizeX = live.FogOfWarSizeX;
                changed = true;
            }
            if (source.FogOfWarSizeY != live.FogOfWarSizeY)
            {
                patch.FogOfWarSizeY = live.FogOfWarSizeY;
                changed = true;
            }
            if (source.FogOfWarLastX != live.FogOfWarLastX)
            {
                patch.FogOfWarLastX = live.FogOfWarLastX;
                changed = true;
            }
            if (source.FogOfWarLastY != live.FogOfWarLastY)
            {
                patch.FogOfWarLastY = live.FogOfWarLastY;
                changed = true;
            }
            if (source.Owner != live.Owner)
            {
                patch.Owner = live.Owner;
                changed = true;
            }

            if (source.LinkedObjectId != live.LinkedObjectId)
            {
                patch.LinkedObjectId = live.LinkedObjectId;
                changed = true;
            }
            if (source.LinkedSlot != live.LinkedSlot)
            {
                patch.LinkedSlot = live.LinkedSlot;
                changed = true;
            }
            if (source.PlaceSnapMode != live.PlaceSnapMode)
            {
                patch.PlaceSnapMode = live.PlaceSnapMode;
                changed = true;
            }
            if (source.DigOrderId != live.DigOrderId)
            {
                patch.DigOrderId = live.DigOrderId;
                changed = true;
            }
            return changed ? patch : null;
        }

        private static bool SameVector(OriginalSaveVector3 source, Vector3 live)
        {
            return source.X == live.x && source.Y == live.y && source.Z == live.z;
        }

        static OriginalSaveWorldPatch CaptureWorldPatch(
            OriginalSaveWorldDocument baseline,
            OriginalSaveRuntimeWorldSnapshot baselineRuntimeWorld,
            DiggableTerrain.SaveState terrainState,
            FogOfWarState.SaveState fogState,
            FluidSystem.SaveState fluidState,
            DigglesTclContext context)
        {
            if (terrainState.Width != baseline.Width || terrainState.Height != baseline.Height ||
                fogState.Width != baseline.Width || fogState.Height != baseline.Height)
                throw new InvalidDataException("Runtime world dimensions do not match the native save.");

            int tileCount = checked(baseline.Width * baseline.Height);
            if (terrainState.BlockIdx.Length != tileCount || terrainState.Zone.Length != tileCount ||
                terrainState.Terrain.Length != tileCount || terrainState.CellId.Length != tileCount ||
                terrainState.NativeTerrainFlags.Length != tileCount ||
                terrainState.NavigationState.Length != tileCount ||
                terrainState.FloorAndExitState.Length != tileCount ||
                terrainState.DigState.Length != tileCount || terrainState.DigId.Length != tileCount ||
                terrainState.HasTemplate.Length != tileCount)
                throw new InvalidDataException("Runtime terrain state is incomplete.");

            ValidateFogState(fogState, baseline.Width, baseline.Height, tileCount);

            FluidSystem.ValidateSaveState(
                fluidState,
                baseline.Width,
                baseline.Height,
                handle => context.Entities.ContainsKey(handle));
            bool synthesizeSettledFluid =
                !SameFluidState(fluidState, baselineRuntimeWorld.CreateFluidState());
            var fluidCellsByIndex = new Dictionary<int, FluidSystem.CellSaveState>(
                synthesizeSettledFluid ? fluidState.Cells.Length : 0);
            if (synthesizeSettledFluid)
            {
                foreach (FluidSystem.CellSaveState cell in fluidState.Cells)
                {
                    int index = checked(cell.Y * baseline.Width + cell.X);
                    fluidCellsByIndex.Add(index, cell);
                }
            }
            OriginalSaveFluidPatch? fluidPatch = synthesizeSettledFluid
                ? CaptureSettledFluidPatch(fluidState, context)
                : null;
            // Fabricated terrain may have no NavWorld. Its CaptureSaveState fallback contains
            // neutral 0xff/0 graph bytes, not authoritative runtime state; preserve the archive's
            // +0x06/+0x12 in that case. Real loaded/generated sessions always carry Nav.
            bool captureNavigationGraph = context.Terrain?.Nav != null;

            var tilePatches = new Dictionary<int, OriginalSaveTerrainTilePatch>(
                synthesizeSettledFluid ? tileCount : 0);
            for (int index = 0; index < tileCount; index++)
            {
                OriginalSaveTerrainTile source = baseline.Terrain[index];
                int material = terrainState.NativeTerrainFlags[index] & 3;
                byte templateFlags = terrainState.HasTemplate[index]
                    ? (byte)(source.TemplateFlags | 1)
                    : (byte)(source.TemplateFlags & ~1);
                // Native tile+0x18 bit 7 is authored by the PMP stamp
                // (FUN_005992b0:315,339-377) and consumed by EFFPASSWALK
                // (FUN_005a6b40:15). A real live NavWorld makes the word authoritative:
                // stamps may set bit 7, while native material updates preserve every high flag
                // (`& 0xfffc | material`, FUN_005fc9d0:115-122). Nav-less terrain synthesizes
                // flags, so retain the archive's high bits and patch material only.
                ushort nativeTerrainFlags = captureNavigationGraph
                    ? terrainState.NativeTerrainFlags[index]
                    : unchecked((ushort)(source.NativeTerrainFlags & ~3 | material));
                if ((uint)material > 3)
                    throw new InvalidDataException($"Runtime terrain tile {index} has invalid material {material}.");

                OriginalSaveTerrainTilePatch? patch = null;
                if (terrainState.BlockIdx[index] != source.BlockIndex ||
                    (captureNavigationGraph &&
                        (terrainState.NavigationState[index] != source.NavigationState ||
                         terrainState.FloorAndExitState[index] != source.FloorAndExitState)) ||
                    terrainState.DigState[index] != source.DigState ||
                    terrainState.DigId[index] != source.DigOrderIds ||
                    material != source.MaterialType ||
                    nativeTerrainFlags != source.NativeTerrainFlags ||
                    terrainState.CellId[index] != source.CellId ||
                    templateFlags != source.TemplateFlags)
                {
                    patch = GetTilePatch(tilePatches, index);
                    patch.BlockIndex = terrainState.BlockIdx[index];
                    if (captureNavigationGraph)
                    {
                        patch.NavigationState = terrainState.NavigationState[index];
                        patch.FloorAndExitState = terrainState.FloorAndExitState[index];
                    }
                    patch.DigState = terrainState.DigState[index];
                    patch.DigOrderIds = terrainState.DigId[index];
                    patch.TerrainState = source.TerrainState & 0xFFFF0000u | nativeTerrainFlags;
                    patch.CellId = terrainState.CellId[index];
                    patch.TemplateFlags = templateFlags;
                }

                ulong fogBytes = 0;
                int fogOffset = index * FogOfWarState.Players;
                for (int player = 0; player < FogOfWarState.Players; player++)
                    fogBytes |= (ulong)fogState.Fog[fogOffset + player] << (player * 8);
                if (fogBytes != source.FogState)
                {
                    patch ??= GetTilePatch(tilePatches, index);
                    patch.FogState = fogBytes;
                }

                if (synthesizeSettledFluid)
                {
                    byte fluidType = 0;
                    float fluidFill = 0f;
                    if (fluidCellsByIndex.TryGetValue(index, out FluidSystem.CellSaveState cell))
                    {
                        fluidType = checked((byte)cell.TypeMask);
                        fluidFill = cell.Fill;
                    }

                    patch ??= GetTilePatch(tilePatches, index);
                    patch.TemplateFlags = (byte)(templateFlags & ~4);
                    patch.FluidTypeBuffer0 = fluidType;
                    patch.FluidTypeBuffer1 = fluidType;
                    patch.FluidFillBuffer0 = fluidFill;
                    patch.FluidFillBuffer1 = fluidFill;
                }
            }

            var worldPatch = new OriginalSaveWorldPatch
            {
                // -10000 is the context's "never set" sentinel: a session that did not seed the
                // rows falls back to the archived trailer words instead of writing the sentinel.
                LightBegin = context.LightBeginRow == -10000 ? null : context.LightBeginRow,
                GroundBegin = context.GroundBeginRow == -10000 ? null : context.GroundBeginRow,
                Fluid = fluidPatch,
                FogBeginRows = Array.AsReadOnly(fogState.FowBegin),
                FogFadeTilesByPlayer = CaptureFadeTiles(fogState, baseline.Width, baseline.Height),
            };
            foreach (OriginalSaveTerrainTilePatch patch in tilePatches.Values)
                worldPatch.SetTerrainTile(patch);
            return worldPatch;
        }

        private static OriginalSaveFluidPatch CaptureSettledFluidPatch(
            FluidSystem.SaveState state,
            DigglesTclContext context)
        {
            // Empty work sets are the settled form: fluid that was actively flowing freezes after
            // load until a source or a dig marks its region dirty.
            return new OriginalSaveFluidPatch
            {
                Sources = CaptureFluidSources(state.Sources),
                Stoppers = CaptureFluidStoppers(state.Stoppers, context),
                CurrentBufferIndex = 0,
                PreviousBufferIndex = 1,
                SparseSets = CreateEmptyFluidSparseSets(),
                DebugEnabled = false,
            };
        }

        private static IReadOnlyList<OriginalSaveFluidSourceRecord> CaptureFluidSources(
            IReadOnlyList<FluidSystem.SourceSaveState> sources)
        {
            var result = new OriginalSaveFluidSourceRecord[sources.Count];
            for (int index = 0; index < result.Length; index++)
            {
                FluidSystem.SourceSaveState source = sources[index];
                if (source.HasHeight && source.HasVpf)
                {
                    throw new InvalidDataException(
                        $"Runtime fluid source {source.Handle} has both height and volume-per-frame enabled.");
                }
                if (source.TypeMask > (int)FluidSystem.FluidKind.LavaWater)
                {
                    throw new InvalidDataException(
                        $"Runtime fluid source {source.Handle} has invalid native type {source.TypeMask}.");
                }

                float configuredVolume = source.HasVolume
                    ? NativeFluidFloat(
                        source.ConfiguredVolume * OriginalSaveFluidSourceRecord.UnitScale,
                        source.Handle,
                        nameof(source.ConfiguredVolume))
                    : OriginalSaveFluidSourceRecord.DisabledValue;
                float remainingVolume = source.HasVolume
                    ? NativeFluidFloat(
                        source.Volume * OriginalSaveFluidSourceRecord.UnitScale,
                        source.Handle,
                        nameof(source.Volume))
                    : OriginalSaveFluidSourceRecord.DisabledValue;
                double encodedHeight = source.Height >= 1d ? source.Height + 2d : source.Height;
                float height = source.HasHeight
                    ? NativeFluidFloat(
                        encodedHeight,
                        source.Handle,
                        nameof(source.Height))
                    : OriginalSaveFluidSourceRecord.DisabledValue;
                float volumePerFrame = source.HasVpf
                    ? NativeFluidFloat(
                        source.Vpf * OriginalSaveFluidSourceRecord.UnitScale,
                        source.Handle,
                        nameof(source.Vpf))
                    : OriginalSaveFluidSourceRecord.DisabledValue;

                result[index] = new OriginalSaveFluidSourceRecord(
                    source.Handle,
                    source.PositionX,
                    source.PositionY,
                    source.PositionZ,
                    source.TypeMask,
                    configuredVolume,
                    remainingVolume,
                    height,
                    volumePerFrame,
                    source.Vented);
            }
            return Array.AsReadOnly(result);
        }

        private static IReadOnlyList<OriginalSaveFluidStopperRecord> CaptureFluidStoppers(
            IReadOnlyList<FluidSystem.StopperSaveState> stoppers,
            DigglesTclContext context)
        {
            var result = new OriginalSaveFluidStopperRecord[stoppers.Count];
            for (int index = 0; index < result.Length; index++)
            {
                FluidSystem.StopperSaveState stopper = stoppers[index];
                if (!context.Entities.TryGetValue(stopper.Handle, out DigglesTclEntity? owner))
                {
                    throw new InvalidDataException(
                        $"Runtime fluid stopper {stopper.Handle} has no native owner.");
                }

                int startX = NativeStopperCoordinate(
                    stopper.StartX,
                    stopper.Handle,
                    nameof(stopper.StartX));
                int startY = NativeStopperCoordinate(
                    stopper.StartY,
                    stopper.Handle,
                    nameof(stopper.StartY));
                int endX = NativeStopperCoordinate(
                    stopper.EndX,
                    stopper.Handle,
                    nameof(stopper.EndX));
                int endY = NativeStopperCoordinate(
                    stopper.EndY,
                    stopper.Handle,
                    nameof(stopper.EndY));
                result[index] = new OriginalSaveFluidStopperRecord(
                    stopper.Handle,
                    owner.Position.x,
                    owner.Position.y,
                    owner.Position.z,
                    true,
                    Math.Min(startX, endX),
                    Math.Min(startY, endY),
                    Math.Max(startX, endX),
                    Math.Max(startY, endY),
                    stopper.Permeability,
                    stopper.Permeability);
            }
            return Array.AsReadOnly(result);
        }

        private static IReadOnlyList<IReadOnlyList<OriginalSaveIntPair>> CreateEmptyFluidSparseSets()
        {
            var result = new IReadOnlyList<OriginalSaveIntPair>[4];
            for (int index = 0; index < result.Length; index++)
                result[index] = Array.Empty<OriginalSaveIntPair>();
            return Array.AsReadOnly(result);
        }

        private static float NativeFluidFloat(double value, int handle, string field)
        {
            if (!Finite(value) || value < -float.MaxValue || value > float.MaxValue)
            {
                throw new InvalidDataException(
                    $"Runtime fluid source {handle} {field} cannot be represented by the native format.");
            }
            return (float)value;
        }

        private static int NativeStopperCoordinate(float value, int handle, string field)
        {
            double exactValue = value;
            if (exactValue < int.MinValue || exactValue > int.MaxValue || value != (int)value)
            {
                throw new InvalidDataException(
                    $"Runtime fluid stopper {handle} {field} is not an exact native coordinate.");
            }
            return (int)value;
        }

        private static bool SameFluidState(FluidSystem.SaveState live, FluidSystem.SaveState baseline)
        {
            if (live.Enabled != baseline.Enabled ||
                live.Cells.Length != baseline.Cells.Length ||
                live.Sources.Length != baseline.Sources.Length ||
                live.Stoppers.Length != baseline.Stoppers.Length)
                return false;

            var baselineCells = new Dictionary<long, FluidSystem.CellSaveState>(baseline.Cells.Length);
            foreach (FluidSystem.CellSaveState cell in baseline.Cells)
                baselineCells.Add(CellKey(cell.X, cell.Y), cell);
            foreach (FluidSystem.CellSaveState cell in live.Cells)
            {
                if (!baselineCells.TryGetValue(CellKey(cell.X, cell.Y), out var source) ||
                    cell.TypeMask != source.TypeMask || cell.Fill != source.Fill)
                    return false;
            }

            var baselineSources = new Dictionary<int, FluidSystem.SourceSaveState>(baseline.Sources.Length);
            foreach (FluidSystem.SourceSaveState source in baseline.Sources)
                baselineSources.Add(source.Handle, source);
            foreach (FluidSystem.SourceSaveState source in live.Sources)
            {
                if (!baselineSources.TryGetValue(source.Handle, out var expected) ||
                    !SameFluidSource(source, expected))
                    return false;
            }

            var baselineStoppers = new Dictionary<int, FluidSystem.StopperSaveState>(baseline.Stoppers.Length);
            foreach (FluidSystem.StopperSaveState stopper in baseline.Stoppers)
                baselineStoppers.Add(stopper.Handle, stopper);
            foreach (FluidSystem.StopperSaveState stopper in live.Stoppers)
            {
                if (!baselineStoppers.TryGetValue(stopper.Handle, out var expected) ||
                    stopper.StartX != expected.StartX || stopper.StartY != expected.StartY ||
                    stopper.EndX != expected.EndX || stopper.EndY != expected.EndY ||
                    stopper.Permeability != expected.Permeability)
                    return false;
            }
            return true;
        }

        private static bool SameFluidSource(
            FluidSystem.SourceSaveState live,
            FluidSystem.SourceSaveState baseline)
        {
            // Bit-pattern comparison, not ==: real saves carry NaN residue in source volume words,
            // and NaN == NaN is false, which would flag every such unchanged source as mutated.
            return live.TypeMask == baseline.TypeMask &&
                SameDoubleBits(live.PositionX, baseline.PositionX) &&
                SameDoubleBits(live.PositionY, baseline.PositionY) &&
                SameDoubleBits(live.PositionZ, baseline.PositionZ) &&
                live.HasVpf == baseline.HasVpf &&
                live.HasVolume == baseline.HasVolume && live.HasHeight == baseline.HasHeight &&
                live.Vented == baseline.Vented &&
                (!live.HasVpf || SameDoubleBits(live.Vpf, baseline.Vpf)) &&
                (!live.HasVolume ||
                 SameDoubleBits(live.ConfiguredVolume, baseline.ConfiguredVolume)) &&
                (!live.HasVolume || SameDoubleBits(live.Volume, baseline.Volume)) &&
                (!live.HasHeight || SameDoubleBits(live.Height, baseline.Height));
        }

        private static bool SameDoubleBits(double left, double right) =>
            BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

        private static long CellKey(int x, int y)
        {
            return (long)x << 32 | (uint)y;
        }

        static IReadOnlyList<IReadOnlyList<OriginalSaveIntPair>> CaptureFadeTiles(
            FogOfWarState.SaveState state,
            int width,
            int height)
        {
            var result = new IReadOnlyList<OriginalSaveIntPair>[FogOfWarState.Players];
            for (int player = 0; player < result.Length; player++)
            {
                int start = state.FadeOffsets[player];
                int end = state.FadeOffsets[player + 1];
                var pairs = new OriginalSaveIntPair[end - start];
                for (int index = start; index < end; index++)
                {
                    int packed = state.FadeItems[index];
                    int x = packed % width;
                    int y = packed / width;
                    if (packed < 0 || (uint)x >= (uint)width || (uint)y >= (uint)height)
                        throw new InvalidDataException(
                            $"Runtime fog fade tile {packed} is outside the world.");
                    pairs[index - start] = new OriginalSaveIntPair(x, y);
                }
                result[player] = pairs;
            }
            return result;
        }

        static void ValidateFogState(
            FogOfWarState.SaveState state,
            int width,
            int height,
            int tileCount)
        {
            if (state.Fog == null || state.FowBegin == null || state.FadeOffsets == null ||
                state.FadeItems == null || state.ExploredRect == null ||
                state.Width != width || state.Height != height ||
                state.Fog.Length != tileCount * FogOfWarState.Players ||
                state.FowBegin.Length != FogOfWarState.Players ||
                state.FadeOffsets.Length != FogOfWarState.Players + 1 ||
                state.ExploredRect.Length != FogOfWarState.Players * 4 ||
                (uint)state.LocalPlayer >= FogOfWarState.Players ||
                state.RoundRobin is < 0 or > 5 || state.FadeOffsets[0] != 0 ||
                state.FadeOffsets[FogOfWarState.Players] != state.FadeItems.Length)
                throw new InvalidDataException("Runtime fog state is incomplete.");
            for (int player = 0; player < FogOfWarState.Players; player++)
            {
                if (state.FowBegin[player] < -1 || state.FowBegin[player] >= height ||
                    state.FadeOffsets[player] < 0 ||
                    state.FadeOffsets[player] > state.FadeOffsets[player + 1] ||
                    state.FadeOffsets[player + 1] > state.FadeItems.Length)
                    throw new InvalidDataException("Runtime fog state contains invalid native bounds.");
            }
            foreach (int packed in state.FadeItems)
            {
                int x = packed % width;
                int y = packed / width;
                if (packed < 0 || (uint)x >= (uint)width || (uint)y >= (uint)height)
                    throw new InvalidDataException("Runtime fog state contains an invalid fade tile.");
            }
        }

        static OriginalSaveTerrainTilePatch GetTilePatch(
            Dictionary<int, OriginalSaveTerrainTilePatch> patches,
            int index)
        {
            if (!patches.TryGetValue(index, out OriginalSaveTerrainTilePatch? patch))
            {
                patch = new OriginalSaveTerrainTilePatch(index);
                patches.Add(index, patch);
            }
            return patch;
        }

        public OriginalSaveCanonicalCapture CaptureCanonical(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            DigglesTclContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Sim == null)
                throw new InvalidOperationException("The live world has no simulation clock.");
            if (context.SequenceActive || context.This != null || context.CurrentEvent != null)
                throw new InvalidOperationException("The live world is not at a safe native save boundary.");

            var preambleClock = ProfStart();
            TimerRegistry.Entry[] timerRegistry = context.Timers.CaptureEntries();
            TimerRegistry.ValidateEntries(timerRegistry);
            TimerScheduler.Entry[] scheduledTimers = context.Sim.Scheduler.CaptureEntries();
            TimerScheduler.ValidateEntries(scheduledTimers);

            EnergySystem.SaveState energy = context.Energy.Capture();
            EnergySystem.ValidateSaveState(energy, context.Entities);
            ParticleSystemState.SaveState particles = context.Particles?.Capture() ?? new();
            particles.Sources = FilterOrphanParticleSources(particles.Sources, context);
            ParticleSystemState.Validate(particles);
            AssignCanonicalParticleHandles(particles);
            NewsTickerSystem.SaveState ticker = context.NewsTicker.Capture();
            NewsTickerSystem.ValidateSaveState(ticker);
            int[] productionSlotOverrides = context.ProdSlotOverrides.Capture();
            ProductionSlotOverrides.Validate(productionSlotOverrides);
            LoreTrackRecord[] loreTracks = context.LoreTracks.CaptureRecords();
            LoreTrackSystem.ValidateRecords(loreTracks);
            ProfAdd("canonical.preamble", preambleClock);
            var animClock = ProfStart();
            OriginalSaveCanonicalAnimationState[] animationControllers =
                CaptureAnimationControllers(retainedRaw, context);
            ProfAdd("canonical.animControllers", animClock);
            // Icons before entities: each emitted global icon slot must be linked from its owner's
            // icon holder (obj+0x2B8 lanes). An active slot with a -1 holder is an orphan the
            // original can never expire, leaving permanent HP bars over every dwarf.
            OriginalSaveObjectIconRecord[] objectIcons = CaptureObjectIcons(context);
            var iconSlotsByObject = new Dictionary<int, List<int>>();
            foreach (OriginalSaveObjectIconRecord iconRecord in objectIcons)
            {
                if (!iconRecord.IsActive) continue;
                if (!iconSlotsByObject.TryGetValue(iconRecord.ObjectId, out List<int>? slots))
                {
                    slots = new List<int>(2);
                    iconSlotsByObject.Add(iconRecord.ObjectId, slots);
                }
                slots.Add(iconRecord.Slot);
            }

            var entitiesClock = ProfStart();
            OriginalSaveCanonicalEntityState[] entities = CaptureCanonicalEntities(
                retainedRaw,
                context,
                scheduledTimers,
                animationControllers,
                particles,
                iconSlotsByObject);
            ProfAdd("canonical.entities", entitiesClock);
            var actionsClock = ProfStart();
            RuntimeEntityActionSaveStateEntry[] actions = AlignActionsWithRetained(
                CaptureActions(context, entities),
                retainedRaw);
            ProfAdd("canonical.actions", actionsClock);
            var tailClock = ProfStart();
            var ownerAttribsClock = ProfStart();
            OriginalSaveCanonicalOwnerAttributeBlock[] ownerAttributes =
                CaptureOwnerAttributes(retainedRaw, context);
            ProfAdd("tail.ownerAttribs", ownerAttribsClock);
            var playersClock = ProfStart();
            var players = CapturePlayerState(context, productionSlotOverrides);
            ProfAdd("tail.players", playersClock);
            var terrainClock = ProfStart();
            DiggableTerrain.SaveState? terrainState = context.Terrain?.CaptureSaveState();
            ProfAdd("tail.terrain", terrainClock);
            var fogClock = ProfStart();
            FogOfWarState.SaveState? fogState = context.FogOfWar?.CaptureSaveState();
            ProfAdd("tail.fog", fogClock);
            var fluidClock = ProfStart();
            FluidSystem.SaveState fluidState = context.Fluids.Capture();
            ProfAdd("tail.fluid", fluidClock);
            var globalTimersClock = ProfStart();
            OriginalSaveTimerRecord[] globalTimers =
                CaptureGlobalTimers(retainedRaw, context, timerRegistry, scheduledTimers);
            ProfAdd("tail.globalTimers", globalTimersClock);
            var triggersClock = ProfStart();
            TriggerRecord?[] triggers =
                context.TriggerService?.CaptureRecords() ?? Array.Empty<TriggerRecord?>();
            ProfAdd("tail.triggers", triggersClock);
            var aiClock = ProfStart();
            var aiState = AlignAiWithRetained(
                context.Ai?.CaptureSaveState() ?? CreateEmptyAiState(),
                retainedRaw);
            ProfAdd("tail.ai", aiClock);
            var storyClock = ProfStart();
            var storyState = CaptureStoryState(context);
            ProfAdd("tail.story", storyClock);
            var selectionClock = ProfStart();
            var selectionGroups = CaptureSelectionControlGroups(context);
            ProfAdd("tail.selection", selectionClock);
            var caveClock = ProfStart();
            DiggableTerrain.CaveRecord[] caveRecords =
                context.Terrain?.CaptureCaveRecords() ?? Array.Empty<DiggableTerrain.CaveRecord>();
            ProfAdd("tail.caves", caveClock);
            var lightsClock = ProfStart();
            OriginalSaveCanonicalClassLightState[] classLights =
                CaptureClassLights(retainedRaw, context);
            ProfAdd("tail.classLights", lightsClock);
            var elfClock = ProfStart();
            var elfState = context.Elf?.CaptureNativeSaveState();
            ProfAdd("tail.elf", elfClock);

            var result = new OriginalSaveCanonicalCapture
            {
                GameTime = context.Sim.GameTime,
                GroundBeginRow = context.GroundBeginRow,
                Entities = entities,
                OwnerAttributes = ownerAttributes,
                TimerRegistryEntries = timerRegistry,
                ScheduledTimers = scheduledTimers,
                GlobalTimers = globalTimers,
                Triggers = triggers,
                Ai = aiState,
                Story = storyState,
                Players = players,
                SelectionControlGroups = selectionGroups,
                Sparetime = context.SpareRegistry.ToArray(),
                Horizon = context.HorizonRegions.ToArray(),
                AdaptiveSoundMarkers = context.Audio?.CaptureMusicMarkers() ??
                    Array.Empty<AudioState.MusicMarker>(),
                NewsTicker = ticker,
                CaveRecords = caveRecords,
                Energy = energy,
                Particles = particles,
                LoreTracks = loreTracks,
                ClassLights = classLights,
                AnimationControllers = animationControllers,
                ObjectIcons = objectIcons,
                Actions = actions,
                DecoSeeds = CaptureDecoSeeds(context),
                ProductionSlotOverrides = productionSlotOverrides,
                Elf = elfState,
                Terrain = terrainState,
                FogOfWar = fogState,
                Fluid = fluidState,
            };

            ProfAdd("canonical.tail", tailClock);
            var validateClock = ProfStart();
            TriggerService.ValidateRecords(result.Triggers);
            AiSystem.ValidateSaveState(result.Ai);
            AudioState.ValidateMusicMarkers(result.AdaptiveSoundMarkers);
            DiggableTerrain.ValidateCaveRecords(result.CaveRecords);
            ValidateCanonicalCapture(result, context);
            ProfAdd("canonical.validate", validateClock);
            return result;
        }

        // Surviving marker-7 seeds in archive order: MapDecoSeeds preserves the source document's
        // read order and carve retirement only removes. Seeds materialized without a source record
        // are skipped, they have no archive bytes to re-emit.
        private static OriginalSaveSpatialSeed[] CaptureDecoSeeds(DigglesTclContext context)
        {
            var seeds = context.MapDecoSeeds;
            if (seeds == null || seeds.Count == 0)
                return Array.Empty<OriginalSaveSpatialSeed>();
            var result = new List<OriginalSaveSpatialSeed>(seeds.Count);
            for (int index = 0; index < seeds.Count; index++)
            {
                var entity = seeds[index];
                if (entity?.MapDecoSeedRecord is OriginalSaveSpatialSeed record && !entity.MapDecoRetiring)
                    result.Add(record);
            }
            return result.ToArray();
        }

        private static OriginalSaveCanonicalClassLightState[] CaptureClassLights(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            DigglesTclContext context)
        {
            RuntimeWorldSpawner.ClassLightSaveState[] live =
                context.EntitySpawner is RuntimeWorldSpawner spawner
                    ? spawner.CaptureClassLights()
                    : Array.Empty<RuntimeWorldSpawner.ClassLightSaveState>();
            RuntimeWorldSpawner.ValidateClassLights(live);

            IReadOnlyList<OriginalSaveRuntimeClassLight>? retained =
                retainedRaw?.ClassLights.Records;
            if (retained == null || retained.Count == 0)
                return SynthesizeClassLights(live, null);

            var liveStatic = new Queue<RuntimeWorldSpawner.ClassLightSaveState>();
            var liveByOwner = new Dictionary<int, RuntimeWorldSpawner.ClassLightSaveState>();
            foreach (RuntimeWorldSpawner.ClassLightSaveState light in live)
            {
                if (!light.IsObjectBound)
                {
                    liveStatic.Enqueue(light);
                    continue;
                }
                if (!liveByOwner.TryAdd(light.ObjectId, light))
                    throw new InvalidDataException(
                        $"Live object {light.ObjectId} owns more than one class light.");
            }

            var result = new List<OriginalSaveCanonicalClassLightState>(
                Math.Max(retained.Count, live.Length));
            var occupiedSlots = new HashSet<int>();
            foreach (OriginalSaveRuntimeClassLight raw in retained)
            {
                occupiedSlots.Add(raw.SlotIndex);
                if (raw.IsObjectBound)
                {
                    // Object-bound lights are rebuilt from immutable class data. Their native sparse
                    // slot and trailing words have no live analogue, so retain the whole record
                    // while its live owner still exists. A destroyed owner drops its stale record.
                    if (!context.Entities.ContainsKey(raw.SourceToken)) continue;
                    result.Add(CloneClassLight(raw, isRetainedObjectBound: true));
                    liveByOwner.Remove(raw.SourceToken);
                    continue;
                }

                if (liveStatic.Count == 0)
                    continue;
                RuntimeWorldSpawner.ClassLightSaveState current = liveStatic.Dequeue();
                result.Add(ClassLightMatches(raw, current)
                    ? CloneClassLight(raw, isRetainedObjectBound: false)
                    : CreateClassLight(
                        raw.SlotIndex,
                        raw.SourceToken,
                        raw.TrailingValue,
                        current,
                        isRetainedObjectBound: false));
            }

            var additions = new List<RuntimeWorldSpawner.ClassLightSaveState>(
                liveStatic.Count + liveByOwner.Count);
            while (liveStatic.Count != 0) additions.Add(liveStatic.Dequeue());
            foreach (KeyValuePair<int, RuntimeWorldSpawner.ClassLightSaveState> pair in
                     liveByOwner.OrderBy(pair => pair.Key))
                additions.Add(pair.Value);
            if (additions.Count != 0)
                result.AddRange(SynthesizeClassLights(additions, occupiedSlots));

            result.Sort((left, right) => left.SlotIndex.CompareTo(right.SlotIndex));
            return result.ToArray();
        }

        private static OriginalSaveCanonicalClassLightState[] SynthesizeClassLights(
            IReadOnlyList<RuntimeWorldSpawner.ClassLightSaveState> live,
            HashSet<int>? occupiedSlots)
        {
            occupiedSlots ??= new HashSet<int>();
            var result = new OriginalSaveCanonicalClassLightState[live.Count];
            int slot = 0;
            for (int index = 0; index < result.Length; index++)
            {
                while (occupiedSlots.Contains(slot)) slot++;
                if (slot >= 2048)
                    throw new InvalidDataException("The live class-light table exceeds 2048 slots.");
                RuntimeWorldSpawner.ClassLightSaveState light = live[index];
                result[index] = CreateClassLight(
                    slot,
                    light.IsObjectBound ? light.ObjectId : 0,
                    new OriginalSaveRaw32(0),
                    light,
                    isRetainedObjectBound: false);
                occupiedSlots.Add(slot++);
            }
            return result;
        }

        private static OriginalSaveCanonicalClassLightState CloneClassLight(
            OriginalSaveRuntimeClassLight source,
            bool isRetainedObjectBound) =>
            new()
            {
                SlotIndex = source.SlotIndex,
                PositionX = source.PositionX,
                PositionY = source.PositionY,
                PositionZ = source.PositionZ,
                Red = source.Red,
                Green = source.Green,
                Blue = source.Blue,
                Radius = source.Radius,
                SourceToken = source.SourceToken,
                TrailingCompatibilityValue = source.TrailingValue,
                IsRetainedObjectBound = isRetainedObjectBound,
            };

        private static OriginalSaveCanonicalClassLightState CreateClassLight(
            int slotIndex,
            int sourceToken,
            OriginalSaveRaw32 trailingValue,
            RuntimeWorldSpawner.ClassLightSaveState source,
            bool isRetainedObjectBound) =>
            new()
            {
                SlotIndex = slotIndex,
                PositionX = RawSingle(source.NativePosition.x),
                PositionY = RawSingle(source.NativePosition.y),
                PositionZ = RawSingle(source.NativePosition.z),
                Red = NativeLightColor(source.Color.r),
                Green = NativeLightColor(source.Color.g),
                Blue = NativeLightColor(source.Color.b),
                Radius = RawSingle(source.Range),
                SourceToken = sourceToken,
                TrailingCompatibilityValue = trailingValue,
                IsRetainedObjectBound = isRetainedObjectBound,
            };

        private static bool ClassLightMatches(
            OriginalSaveRuntimeClassLight retained,
            RuntimeWorldSpawner.ClassLightSaveState live) =>
            retained.PositionX.AsSingle == live.NativePosition.x &&
            retained.PositionY.AsSingle == live.NativePosition.y &&
            retained.PositionZ.AsSingle == live.NativePosition.z &&
            retained.Red == NativeLightColor(live.Color.r) &&
            retained.Green == NativeLightColor(live.Color.g) &&
            retained.Blue == NativeLightColor(live.Color.b) &&
            retained.Radius.AsSingle == live.Range;

        private static OriginalSaveRaw32 RawSingle(float value) =>
            new(BitConverter.SingleToInt32Bits(value));

        private static int NativeLightColor(float value)
        {
            if (!Finite(value) || value < 0f || value > 1f)
                throw new InvalidDataException("A live class-light color is outside 0..1.");
            return Mathf.RoundToInt(value * 31f);
        }

        private static AiSystem.SaveState CreateEmptyAiState()
        {
            var owners = new AiSystem.OwnerSaveState[8];
            for (int index = 0; index < owners.Length; index++)
                owners[index] = new AiSystem.OwnerSaveState();
            return new AiSystem.SaveState
            {
                Owners = owners,
                Notices = Array.Empty<AiSystem.DiplomacyNotice>(),
            };
        }

        private static int[][] CaptureSelectionControlGroups(DigglesTclContext context)
        {
            var result = new int[context.SelectionControlGroups.Length][];
            for (int index = 0; index < result.Length; index++)
                result[index] = context.SelectionControlGroups[index].ToArray();
            return result;
        }

        private static OriginalSaveCanonicalPlayerState CapturePlayerState(
            DigglesTclContext context,
            int[] productionSlotOverrides)
        {
            var slots = new OriginalSaveCanonicalPlayerSlot[context.PlayerSlots.Length];
            for (int index = 0; index < slots.Length; index++)
            {
                DigglesTclContext.PlayerSlotState source = context.PlayerSlots[index];
                slots[index] = new OriginalSaveCanonicalPlayerSlot
                {
                    PlayerName = source.PlayerName,
                    SlotState = source.SlotState,
                    PlayerOwnerId = source.PlayerOwnerId,
                    ClanOwnerId = source.ClanOwnerId,
                    SecondaryName = source.SecondaryName,
                };
            }
            return new OriginalSaveCanonicalPlayerState
            {
                LocalPlayer = context.LocalPlayer,
                ReservedFraming = (int[])productionSlotOverrides.Clone(),
                OwnerRace = (int[])context.OwnerRace.Clone(),
                Slots = slots,
                Diplomacy = (int[,])context.Diplomacy.Clone(),
            };
        }

        private static OriginalSaveCanonicalStoryState CaptureStoryState(
            DigglesTclContext context)
        {
            var triggers = new OriginalSaveCanonicalStoryTrigger[
                context.StoryEventTriggers.Count];
            int triggerIndex = 0;
            foreach (KeyValuePair<string, (string Trigger, string Extra)> pair in
                context.StoryEventTriggers)
            {
                triggers[triggerIndex++] = new OriginalSaveCanonicalStoryTrigger(
                    pair.Key,
                    pair.Value.Trigger,
                    pair.Value.Extra);
            }

            var map = new OriginalSaveCanonicalStoryMapEntry[context.StoryMap.Count];
            int mapIndex = 0;
            foreach (KeyValuePair<(int, int), int> pair in context.StoryMap)
            {
                map[mapIndex++] = new OriginalSaveCanonicalStoryMapEntry(
                    pair.Key.Item1,
                    pair.Key.Item2,
                    pair.Value);
            }

            var zones = new OriginalSaveCanonicalStoryZone[context.StoryZones.Count];
            for (int index = 0; index < zones.Length; index++)
            {
                DigglesTclContext.StoryZone zone = context.StoryZones[index];
                zones[index] = new OriginalSaveCanonicalStoryZone(zone.Name, zone.Bound);
            }

            var temps = new OriginalSaveCanonicalStoryTemp[context.StoryTemps.Count];
            for (int index = 0; index < temps.Length; index++)
            {
                DigglesTclContext.StoryTemp source = context.StoryTemps[index];
                var seenChildren = new string[source.SeenChildren.Count];
                source.SeenChildren.CopyTo(seenChildren);
                Array.Sort(seenChildren, StringComparer.Ordinal);
                temps[index] = new OriginalSaveCanonicalStoryTemp
                {
                    Name = source.Name,
                    Low = source.Lo,
                    High = source.Hi,
                    Zone = source.Zone,
                    Exclusive = source.Exclusive,
                    ZoneBorder = source.ZoneBorder,
                    StoneIndex = source.StoneIdx,
                    MinimumDistanceReference = source.MinDistRef,
                    MinimumDistance = source.MinDistVal,
                    MiddleDistance = source.MidDist,
                    NonRelevant = source.NonRelevant,
                    DigConnect = source.DigConnect,
                    Connect = source.Connect,
                    CheckAlign = source.CheckAlign,
                    Placed = source.Placed,
                    PlacedX = source.PlacedX,
                    PlacedY = source.PlacedY,
                    EventNames = source.EventNames.ToArray(),
                    SeenChildren = seenChildren,
                };
            }

            var placedTemps = new (string Name, int X, int Y)[context.StoryPlacedTemps.Count];
            for (int index = 0; index < placedTemps.Length; index++)
                placedTemps[index] = context.StoryPlacedTemps[index];
            return new OriginalSaveCanonicalStoryState
            {
                Events = CopyPairs(context.StoryEvents),
                EventOrder = context.StoryEventOrder.ToArray(),
                EventTriggers = triggers,
                Map = map,
                Callbacks = CopyPairs(context.StoryCallbacks),
                Zones = zones,
                Temps = temps,
                TempGroups = CopyPairs(context.StoryTempGroups),
                PlacedTemps = placedTemps,
                DigWeight = context.StoryDigWeight,
                DigThreshold = context.StoryDigThreshold,
                DigMessage = context.StoryDigMessage,
                LatchDirection = context.StoryLatchDir,
                LatchX = context.StoryLatchX,
                LatchY = context.StoryLatchY,
                LatchConsumed = context.StoryLatchConsumed,
                MapWidth = context.StoryMapW,
                MapHeight = context.StoryMapH,
                RectX = context.StoryRectX,
                RectY = context.StoryRectY,
                RectWidth = context.StoryRectW,
                RectHeight = context.StoryRectH,
                UniqueTemp = context.StoryUtemp,
                RawRectX = context.StoryRawRectX,
                RawRectY = context.StoryRawRectY,
                RawRectWidth = context.StoryRawRectW,
                RawRectHeight = context.StoryRawRectH,
                ScanX = context.StoryScanX,
                ScanY = context.StoryScanY,
                UniqueTempPlaced = context.StoryUtempPlaced,
                UniqueTempPlacedName = context.StoryUtempPlacedName,
                UniqueTempWorldX = context.StoryUtempWorldX,
                UniqueTempWorldY = context.StoryUtempWorldY,
                UniqueTempExtentWidth = context.StoryUtempExtW,
                UniqueTempExtentHeight = context.StoryUtempExtH,
                ZoneFloor = context.StoryZoneFloor,
            };
        }

        private static KeyValuePair<TKey, TValue>[] CopyPairs<TKey, TValue>(
            IReadOnlyDictionary<TKey, TValue> source)
            where TKey : notnull
        {
            var result = new KeyValuePair<TKey, TValue>[source.Count];
            int index = 0;
            foreach (KeyValuePair<TKey, TValue> pair in source) result[index++] = pair;
            return result;
        }

        private static OriginalSaveCanonicalOwnerAttributeBlock[] CaptureOwnerAttributes(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            DigglesTclContext context)
        {
            IReadOnlyList<string> names = context.Stats.Registry.DefinedInOrder;
            if (names.Count > 512)
                throw new InvalidDataException("The live attribute registry exceeds 512 native slots.");

            bool freshCapture = retainedRaw == null;
            var result = new OriginalSaveCanonicalOwnerAttributeBlock[9];
            for (int blockIndex = 0; blockIndex < result.Length; blockIndex++)
            {
                int owner = blockIndex - 1;
                var primary = new int[NativeAttributePageSize];
                var secondary = new int[NativeAttributePageSize];
                OriginalSaveRuntimeOwnerAttributeBlockSnapshot? residue =
                    FindOwnerAttributeResidue(retainedRaw, owner);
                if (residue != null)
                {
                    CopyExactPage(residue.PrimaryValueBits, primary, "owner primary attribute");
                    CopyExactPage(
                        residue.SecondaryValueBits,
                        secondary,
                        "owner secondary attribute");
                }

                context.OwnerAttribs.TryGetValue(
                    owner,
                    out Dictionary<string, double>? liveValues);
                Dictionary<string, int> productionKnowledgeSlots =
                    CollectProductionKnowledgeSlots(context, owner);
                foreach (string name in names)
                {
                    if (!productionKnowledgeSlots.TryGetValue(name, out int slot) &&
                        !TryNativeAttributeSlot(name, out slot))
                    {
                        continue;
                    }
                    double defaultValue = context.Stats.Registry.DefaultOf(name);
                    if (freshCapture)
                        secondary[slot] = FreshOwnerAttributeBits(defaultValue);
                    double value = freshCapture && IsEmpiricalProductionOwnerCounter(name)
                        ? FreshProductionOwnerCounterValue(context, owner, name, defaultValue)
                        : liveValues != null && liveValues.TryGetValue(name, out double live)
                            ? live
                            : defaultValue;
                    primary[slot] = freshCapture
                        ? FreshOwnerAttributeBits(value)
                        : AttributeBits(
                        value,
                        true,
                        primary[slot]);
                }
                CaptureDigEnablePages(primary, secondary, liveValues, freshCapture);
                result[blockIndex] = new OriginalSaveCanonicalOwnerAttributeBlock
                {
                    Owner = owner,
                    PrimaryValueBits = primary,
                    SecondaryCompatibilityBits = secondary,
                };
            }
            return result;
        }

        private static Dictionary<string, int> CollectProductionKnowledgeSlots(
            DigglesTclContext context,
            int owner)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DigglesTclEntity entity in context.Entities.Values)
            {
                if (entity.Owner != owner || entity.NativeProductionSlots == null) continue;
                foreach (DigglesTclEntity.NativeProductionSlotState slot in
                    entity.NativeProductionSlots)
                {
                    if (slot.RegistryValue2 < 0 || slot.RegistryValue2 >= NativeAttributePageSize)
                        continue;
                    string attributeName = "Bp" + slot.ClassName;
                    if (result.TryGetValue(attributeName, out int existingSlot) &&
                        existingSlot != slot.RegistryValue2)
                    {
                        throw new InvalidDataException(
                            $"Production knowledge attribute '{attributeName}' maps to native " +
                            $"slots {existingSlot} and {slot.RegistryValue2}.");
                    }
                    result[attributeName] = slot.RegistryValue2;
                }
            }
            return result;
        }

        private static void CaptureDigEnablePages(
            int[] primary,
            int[] secondary,
            IReadOnlyDictionary<string, double>? liveValues,
            bool freshCapture)
        {
            if (!freshCapture) return;
            int enabledBits = BitConverter.SingleToInt32Bits(1f);
            for (int index = 0; index < NativeDigEnableCount; index++)
            {
                string name = $"digenable{index}";
                double value = liveValues != null && liveValues.TryGetValue(name, out double live)
                    ? live
                    : 1d;
                primary[NativeDigEnableFirstSlot + index] = FreshOwnerAttributeBits(value);
                secondary[NativeDigEnableFirstSlot + index] = enabledBits;
            }
        }

        private static double FreshProductionOwnerCounterValue(
            DigglesTclContext context,
            int owner,
            string name,
            double defaultValue)
        {
            if (owner != -1) return defaultValue;
            bool found = false;
            double total = 0d;
            foreach (KeyValuePair<int, Dictionary<string, double>> ownerValues in context.OwnerAttribs)
            {
                if (!ownerValues.Value.TryGetValue(name, out double value)) continue;
                found = true;
                total += value;
            }
            return found ? total : defaultValue;
        }

        private static bool IsEmpiricalProductionOwnerCounter(string name) =>
            _empiricalProductionItems.Contains(name) &&
            OriginalSaveNativeAttributeSlots.SlotByName.ContainsKey(name);

        private static OriginalSaveRuntimeOwnerAttributeBlockSnapshot?
            FindOwnerAttributeResidue(OriginalSaveRuntimeSnapshot? retainedRaw, int owner)
        {
            if (retainedRaw == null) return null;
            foreach (OriginalSaveRuntimeOwnerAttributeBlockSnapshot block in
                retainedRaw.OwnerAttributes.Blocks)
            {
                if (block.Owner == owner) return block;
            }
            return null;
        }

        private static OriginalSaveCanonicalEntityState[] CaptureCanonicalEntities(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            DigglesTclContext context,
            IReadOnlyList<TimerScheduler.Entry> scheduledTimers,
            IReadOnlyList<OriginalSaveCanonicalAnimationState> animationControllers,
            ParticleSystemState.SaveState particles,
            IReadOnlyDictionary<int, List<int>> iconSlotsByObject)
        {
            DigglesTclEntity[] liveEntities = context.Entities.Values
                .OrderBy(entity => entity.Handle)
                .ToArray();
            var result = new OriginalSaveCanonicalEntityState[liveEntities.Length];
            var animationsByObjectId = new Dictionary<int, OriginalSaveCanonicalAnimationState>(
                animationControllers.Count);
            foreach (OriginalSaveCanonicalAnimationState animation in animationControllers)
                animationsByObjectId.Add(animation.ObjectId, animation);
            Dictionary<int, int[]> particleWordsByObject = CaptureObjectParticleWords(particles);
            var memberDefaultsByClass =
                new Dictionary<DigglesClass, Dictionary<string, string>>();
            for (int index = 0; index < liveEntities.Length; index++)
            {
                DigglesTclEntity live = liveEntities[index];
                OriginalSaveRuntimeEntitySnapshot? residue = null;
                retainedRaw?.EntitiesById.TryGetValue(live.Handle, out residue);
                ValidateEntity(live, context, residue);
                if (residue != null &&
                    !string.Equals(residue.ClassName, live.Class.Name, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Retained residue for object {live.Handle} belongs to class " +
                        $"'{residue.ClassName}', not live class '{live.Class.Name}'.");
                }
                var tclVarsClock = ProfStart();
                OriginalSaveTclVariable[] tclVariables =
                    CaptureTclVariables(
                        residue,
                        live,
                        context,
                        memberDefaultsByClass,
                        out bool retainedVariables);
                ProfAdd("entities.tclVars", tclVarsClock);

                (int stateLive, int stateTarget, int stateEnabled) =
                    CaptureStateWords(residue, live);
                if (!animationsByObjectId.TryGetValue(
                        live.Handle,
                        out OriginalSaveCanonicalAnimationState? animation))
                {
                    throw new InvalidDataException(
                        $"Object {live.Handle} has no canonical animation controller.");
                }
                int animationFrameValue = animation.FrameValues.Length == 0
                    ? -1
                    : animation.FrameValues[Mathf.Clamp(
                        animation.CurrentFrame,
                        0,
                        animation.FrameValues.Length - 1)];
                var coreBitsClock = ProfStart();
                int[] coreStateBits = CaptureCoreStateBits(live);
                ProfAdd("entities.coreBits", coreBitsClock);
                var texAnimClock = ProfStart();
                OriginalSaveCanonicalTextureAnimationState[] textureAnimations =
                    CaptureTextureAnimations(live, residue, out int[] currentPartFrames);
                ProfAdd("entities.textureAnims", texAnimClock);
                (int initialModelSlot, int initialAnimationState, int initialStartFrame,
                    int initialMode) = CaptureInitialAnimation(live);
                var entity = new OriginalSaveCanonicalEntityState
                {
                    ObjectId = live.Handle,
                    ClassName = live.Class.Name,
                    ObjectType = live.NativeObjectType,
                    InstanceName = live.InstanceName,
                    InfoString = live.InfoString,
                    StateLiveIndex = stateLive,
                    StateTargetIndex = stateTarget,
                    StateEnabledWord = stateEnabled,
                    Position = live.Position,
                    Velocity = live.Velocity,
                    Rotation = live.Rotation,
                    CoreStateBits = coreStateBits,
                    Owner = live.Owner,
                    CreationTimeHours = live.CreationTimeHours,
                    WorkStart = live.WorkStart,
                    WorkLength = live.WorkLen,
                    TaskList = live.RestoredTaskListResidue != null && !live.TaskListDirty
                        ? live.RestoredTaskListResidue
                        : live.TaskList.ToArray(),
                    FogBrightnessCache = CaptureFogBrightnessCache(context, live),
                    IconHolderSlots = CaptureIconHolderSlots(iconSlotsByObject, live),
                    RenderDirtyLatch = live.RenderDirtyLatch,
                    FogOfWarSizeX = live.FowSizeX,
                    FogOfWarSizeY = live.FowSizeY,
                    FogOfWarLastX = live.FowLastX,
                    FogOfWarLastY = live.FowLastY,
                    FogOfWarLastZ = live.FowLastZ,
                    ParticleStateWords = particleWordsByObject.TryGetValue(
                        live.Handle,
                        out int[]? particleWords)
                            ? particleWords
                            : Array.Empty<int>(),
                    WalkingActionActive = live.ActiveAction is RestoredWalkAction or WalkAction
                        ? (byte)1
                        : (byte)0,
                    WorkTimeState = IsWithinWorkTime(
                        context.Sim!.GameTime,
                        live.WorkStart,
                        live.WorkLen)
                            ? (byte)1
                            : (byte)0,
                    LightOffset = live.LightOffset,
                    LightRange = live.LightRange,
                    LightColor = live.LightColor,
                    NativeMutationFlags = live.NativeMutationFlags,
                    // The native virgin walk result is 1 (the CObj constructor value) while the
                    // port keeps its virgin value at 0 (its sparetime pipeline keys the first-walk
                    // flow on it), so translate at the boundary. Retained entities carry the
                    // archived word.
                    WalkResult = residue == null && live.WalkResult == 0 ? 1 : live.WalkResult,
                    WeaponClassId = live.WeaponClassId,
                    ShieldClassId = live.ShieldClassId,
                    PickTargetMode = live.PickTargetMode,
                    AttackInProgress = live.AttackInProgress,
                    BallisticResult = live.BallisticResult,
                    DiedInFight = live.DiedInFight,
                    ClimbAbility = live.ClimbAbility,
                    ObjectFlags = DigglesTclEngine.CaptureNativeObjectFlags(
                        live,
                        residue?.ObjectFlags ?? 0x01100101),
                    OwnsTclState = residue != null
                        ? residue.HasTclState || live.ActiveAction != null
                        : ClassOwnsTclState(live.Class) || tclVariables.Length != 0 ||
                            live.ActiveAction != null,
                    HasActionRow = residue != null
                        ? residue.StagedAction != null || live.ActiveAction != null
                        : ClassOwnsTclState(live.Class) || live.ActiveAction != null,
                    TclVariables = tclVariables,
                    IsTclVariableCompatibilityPassthrough = retainedVariables,
                    AnimationModelDatabaseSlot = animation.ModelDatabaseSlot,
                    AnimationFrameValue = animationFrameValue,
                    AnimationState = animation.AnimationState,
                    AnimationMode = animation.Mode,
                    InitialAnimationModelDatabaseSlot = initialModelSlot,
                    InitialAnimationState = initialAnimationState,
                    InitialAnimationStartFrame = initialStartFrame,
                    InitialAnimationMode = initialMode,
                    CurrentPartFrames = currentPartFrames,
                    TextureAnimations = textureAnimations,
                    InventoryCapacity = DigglesTclEngine.EffectiveInvCapacity(context, live),
                    Inventory = live.Inventory.ToArray(),
                    ContainedInObjectId = live.ContainedIn,
                    LinkedObjectId = NativeLinkedObjectId(residue, live, context),
                    LinkedSlot = NativeLinkedSlot(residue, live, context),
                    PlaceSnapMode = live.PlaceSnapMode,
                    DigOrderId = live.DigSetId,
                    AttributePageBits = CaptureEntityAttributePage(residue, live, context),
                    // The native attribute page is lazy: no page for object types 0/12 or for
                    // stat-untouched items (Stein/Bier/Keule), a page only for stat-carrying
                    // objects (all of type 1/5, plus creatures of type 3/4). The port's StatValues
                    // store is equally lazy, so presence means any materialized value; retained
                    // bodies reproduce the archived presence.
                    HasNativeAttributePage = residue != null
                        ? residue.AttributePageBits.Count == 512
                        : live.Stats.Raw.Count != 0 ||
                            live.NativeObjectType is 1 or 5 or 11,
                    EventQueue = CaptureEntityEventQueue(
                        residue,
                        live,
                        context,
                        scheduledTimers),
                    Family = CaptureFamilyState(live),
                    TalkIssues = CaptureTalkIssueState(live),
                    Planner = CapturePlannerState(live, context),
                    Production = CaptureProductionState(live, residue?.Production, context),
                };
                result[index] = entity;
            }
            return result;
        }

        // The CObj icon holder (obj+0x2B8): each lane holds the global icon-table slot this object
        // owns, in allocation order; -1 marks an empty lane. The holder countdown is what expires
        // an idle icon, so every slot the capture emits has to be linked here.
        private static int[] CaptureIconHolderSlots(
            IReadOnlyDictionary<int, List<int>> iconSlotsByObject,
            DigglesTclEntity live)
        {
            var result = new int[16];
            Array.Fill(result, -1);
            if (iconSlotsByObject.TryGetValue(live.Handle, out List<int>? slots))
            {
                for (int lane = 0; lane < slots.Count && lane < result.Length; lane++)
                    result[lane] = slots[lane];
            }
            return result;
        }

        private static int[] CaptureFogBrightnessCache(
            DigglesTclContext context,
            DigglesTclEntity live)
        {
            var result = new int[16];
            Array.Fill(result, -1);
            if (context.FogOfWar == null || live.FowCache == null ||
                live.FowCacheVersion != context.FogOfWar.CacheVersion)
            {
                return result;
            }

            int count = Math.Min(8, live.FowCache.Length);
            Array.Copy(live.FowCache, result, count);
            return result;
        }

        internal static bool IsWithinWorkTime(
            double gameTime,
            float workStart,
            float workLength)
        {
            double phase = gameTime / ProdDispatcher.WorktimeHourSeconds %
                ProdDispatcher.DayHours;
            double relative = phase - workStart;
            if (relative < 0.0) relative += ProdDispatcher.DayHours;
            return workLength > 0f && relative < workLength;
        }

        private static Dictionary<int, int[]> CaptureObjectParticleWords(
            ParticleSystemState.SaveState particles)
        {
            var result = new Dictionary<int, int[]>();
            foreach (ParticleSystemState.SourceEntry entry in particles.Sources)
            {
                if (entry.SourceIndex is < 0 or >= 32)
                {
                    throw new InvalidDataException(
                        $"Object {entry.ObjectId} particle source index {entry.SourceIndex} " +
                        "is outside the native 32-slot table.");
                }

                if (!result.TryGetValue(entry.ObjectId, out int[]? words))
                {
                    words = new int[160];
                    for (int index = 0; index < 32; index++)
                    {
                        words[index] = -1;
                        words[128 + index] = -1;
                    }
                    result.Add(entry.ObjectId, words);
                }

                int sourceIndex = entry.SourceIndex;
                ParticleSystemState.Source state = entry.State;
                words[sourceIndex] = state.NativeParticleHandle;
                int positionBase = 32 + sourceIndex * 3;
                words[positionBase] = BitConverter.SingleToInt32Bits(state.RelPos.x);
                words[positionBase + 1] = BitConverter.SingleToInt32Bits(state.RelPos.y);
                words[positionBase + 2] = BitConverter.SingleToInt32Bits(state.RelPos.z);
                words[128 + sourceIndex] = state.LinkId;
            }
            return result;
        }

        private static void AssignCanonicalParticleHandles(
            ParticleSystemState.SaveState particles)
        {
            var usedSlots = new HashSet<int>();
            int nextSlot = 0;
            foreach (ParticleSystemState.SourceEntry entry in particles.Sources)
            {
                entry.State.NativeParticleHandle = ReserveCanonicalParticleHandle(
                    entry.State.NativeParticleHandle,
                    entry.State.HasNativeState,
                    usedSlots,
                    ref nextSlot);
            }
            foreach (ParticleSystemState.TempSource temp in particles.Temps)
            {
                temp.NativeParticleHandle = ReserveCanonicalParticleHandle(
                    temp.NativeParticleHandle,
                    temp.NativeParticleHandle >= 0,
                    usedSlots,
                    ref nextSlot);
            }
        }

        private static int ReserveCanonicalParticleHandle(
            int preferredSlot,
            bool usePreferredSlot,
            HashSet<int> usedSlots,
            ref int nextSlot)
        {
            if (usePreferredSlot && preferredSlot is >= 0 and < 0x1000 &&
                usedSlots.Add(preferredSlot))
            {
                return preferredSlot;
            }
            while (nextSlot < 0x1000 && !usedSlots.Add(nextSlot)) nextSlot++;
            if (nextSlot >= 0x1000)
                throw new InvalidDataException("The canonical particle table exceeds 4096 slots.");
            return nextSlot++;
        }

        private static int[] CaptureCoreStateBits(DigglesTclEntity live)
        {
            if (!live.NativeCoreStateInitialized) live.InitializeNativeCoreState();
            return new[]
            {
                BitConverter.SingleToInt32Bits(live.NativeMotionSpeed),
                BitConverter.SingleToInt32Bits(live.Velocity.x),
                BitConverter.SingleToInt32Bits(live.Velocity.y),
                BitConverter.SingleToInt32Bits(live.Velocity.z),
                BitConverter.SingleToInt32Bits(live.NativeMotionDelta.x),
                BitConverter.SingleToInt32Bits(live.NativeMotionDelta.y),
                BitConverter.SingleToInt32Bits(live.NativeMotionDelta.z),
                BitConverter.SingleToInt32Bits(live.Rotation.x),
                BitConverter.SingleToInt32Bits(live.Rotation.y),
                BitConverter.SingleToInt32Bits(live.Rotation.z),
                BitConverter.SingleToInt32Bits(live.NativePreviousRotation.x),
                BitConverter.SingleToInt32Bits(live.NativePreviousRotation.y),
                BitConverter.SingleToInt32Bits(live.NativePreviousRotation.z),
            };
        }

        private static OriginalSaveCanonicalTextureAnimationState[] CaptureTextureAnimations(
            DigglesTclEntity live,
            OriginalSaveRuntimeEntitySnapshot? residue,
            out int[] currentPartFrames)
        {
            DigglesTextureAnimator? animator = live.GameObject == null
                ? null
                : live.GameObject.GetComponentInChildren<DigglesTextureAnimator>(true);
            // Until the live table really mutates after staging (install or playback advance) the
            // archived table is the truth: the port arms extra channels during staging (face
            // animation) that the native save legitimately lacks, and headless placeholders have
            // no animator at all.
            bool liveTableIsAuthoritative =
                animator != null && animator.MutatedSinceNativeBaseline;
            DigglesTextureAnimator.NativeChannelState[] channels =
                liveTableIsAuthoritative || residue == null
                    ? animator?.CaptureNativeChannels() ??
                        Array.Empty<DigglesTextureAnimator.NativeChannelState>()
                    : Array.Empty<DigglesTextureAnimator.NativeChannelState>();
            if (channels.Length == 0)
            {
                // A retained entity whose presentation neverinstalled the channels — a
                // headless/placeholder model has no DigglesTextureAnimator — round-trips the
                // archived table and N16 snapshot verbatim instead of dropping them.
                if (residue != null && residue.TextureAnimations.Count != 0)
                {
                    var retained = new OriginalSaveCanonicalTextureAnimationState[
                        residue.TextureAnimations.Count];
                    for (int index = 0; index < retained.Length; index++)
                    {
                        OriginalSaveRuntimeTextureAnimationSnapshot source =
                            residue.TextureAnimations[index];
                        int[] frames = new int[source.FrameValues.Count];
                        for (int frame = 0; frame < frames.Length; frame++)
                            frames[frame] = source.FrameValues[frame];
                        retained[index] = new OriginalSaveCanonicalTextureAnimationState
                        {
                            FrameValues = frames,
                            Cursor = source.Cursor,
                            PlaybackMode = source.PlaybackMode,
                        };
                    }
                    currentPartFrames = new int[residue.CurrentPartFrames.Count];
                    for (int index = 0; index < currentPartFrames.Length; index++)
                        currentPartFrames[index] = residue.CurrentPartFrames[index];
                    return retained;
                }
                currentPartFrames = Array.Empty<int>();
                return Array.Empty<OriginalSaveCanonicalTextureAnimationState>();
            }

            int partCount = checked(channels[^1].Submesh + 1);
            int retainedPartCount = residue?.CurrentPartFrames.Count ?? 0;
            currentPartFrames = new int[Math.Max(partCount, retainedPartCount)];
            if (residue != null)
            {
                for (int part = 0; part < retainedPartCount; part++)
                    currentPartFrames[part] = residue.CurrentPartFrames[part];
            }
            Array.Clear(currentPartFrames, 0, partCount);
            var result = new OriginalSaveCanonicalTextureAnimationState[partCount];
            for (int part = 0; part < result.Length; part++)
                result[part] = new OriginalSaveCanonicalTextureAnimationState();
            foreach (DigglesTextureAnimator.NativeChannelState channel in channels)
            {
                int[] frameValues = (int[])channel.FrameIds.Clone();
                result[channel.Submesh] = new OriginalSaveCanonicalTextureAnimationState
                {
                    FrameValues = frameValues,
                    Cursor = channel.Cursor,
                    PlaybackMode = channel.AnimType,
                };
                currentPartFrames[channel.Submesh] =
                    channel.Cursor >= 0 && channel.Cursor < frameValues.Length
                        ? frameValues[channel.Cursor]
                        : 0;
            }
            // Native CObj_Tick trims trailing empty controller records (FUN_005CC600:190-237),
            // but CObj+0x23C keeps its high-water length: FUN_005F5EF0 only grows +0x240 and
            // overwrites the surviving controller prefix (lines 218-250). Trim the controller
            // table alone so N16 retains the native current-part vector shape.
            int trimmed = result.Length;
            while (trimmed > 0 && result[trimmed - 1].FrameValues.Length == 0) trimmed--;
            if (trimmed != result.Length)
                System.Array.Resize(ref result, trimmed);
            return result;
        }

        private static (int modelSlot, int animationState, int startFrame, int mode)
            CaptureInitialAnimation(DigglesTclEntity live)
        {
            if (live.NativeInitialAnimationUsesRawIdentity)
            {
                return (
                    live.NativeInitialAnimationModelDatabaseSlot,
                    live.NativeInitialAnimationState,
                    live.NativeInitialAnimationStartFrame,
                    live.NativeInitialAnimationMode);
            }
            if (!live.NativeInitialAnimationWasSeeded)
            {
                // No CObj writer initializes +0x3BC..+0x3C8 before the first resolved
                // animation. Preserve the port's existing zero output instead of inventing
                // native sentinels for an unobserved state.
                return (0, 0, 0, 0);
            }

            // A save must not abort on an unresolvable identity: port-side helper models (e.g.
            // trigger_fahne markers) have no native.3db database slot, and the native itself
            // never latched +0x3BC for objects its writer could not resolve. Fall back to the
            // not-seeded zero tuple instead of throwing.
            DigglesAnimClip? clip = live.NativeInitialAnimationClip;
            if (clip?.SourceModel == null) return (0, 0, 0, 0);
            int animationState = ResolveNativeAnimationState(clip);
            if (animationState < 0) return (0, 0, 0, 0);
            int modelSlot = OriginalSaveRuntimeSessionStager.NativeModelSlot(clip.SourceModel);
            if (modelSlot < 0) return (0, 0, 0, 0);
            return (
                modelSlot,
                animationState,
                live.NativeInitialAnimationStartFrame,
                live.NativeInitialAnimationMode);
        }

        private static bool ClassOwnsTclState(DigglesClass runtimeClass) =>
            !runtimeClass.DisableScripting &&
            (!string.IsNullOrEmpty(runtimeClass.InitCode) ||
             !string.IsNullOrEmpty(runtimeClass.ExitCode) ||
             runtimeClass.MemberVars.Count != 0 ||
             runtimeClass.EventHandlers.Count != 0 ||
             runtimeClass.States.Count != 0 ||
             runtimeClass.Methods.Count != 0);

        // Native obj+0x200/+0x204/+0x208 capture law (CObj::HandleStates 005cac90). While the
        // restored machine is untouched the archived words round-trip verbatim, so a port state
        // registry that resolves an index differently cannot perturb byte identity. Any state
        // mutation (state_trigger/enable/disable/reset, the think commit) switches to the live
        // derivation: target = the class-table index of CurrentState, live = the committed index
        // (equal to target once the state went live), enable = port count + 1 (state_enable and
        // state_disable are absolute writes: native 1/0 ↔ port 0/-1).
        private static (int liveWord, int targetWord, int enabledWord) CaptureStateWords(
            OriginalSaveRuntimeEntitySnapshot? retainedRaw,
            DigglesTclEntity live)
        {
            if (retainedRaw != null && !live.StateWordsDirty)
            {
                return (retainedRaw.StateLiveWord,
                    retainedRaw.StateTargetWord,
                    retainedRaw.StateEnabledWord);
            }
            int targetWord = live.Class?.StateIndexOf(live.CurrentState) ?? -1;
            int liveWord = live.StateWentLive ? targetWord : live.LiveStateIndex;
            return (liveWord, targetWord, live.StateEnabledCount + 1);
        }

        private static int NativeLinkedObjectId(
            OriginalSaveRuntimeEntitySnapshot? retainedRaw,
            DigglesTclEntity live,
            DigglesTclContext context)
        {
            if (live.LinkedToHandle > 0) return live.LinkedToHandle;
            return retainedRaw != null && retainedRaw.LinkedObjectId > 0 &&
                !context.Entities.ContainsKey(retainedRaw.LinkedObjectId)
                    ? retainedRaw.LinkedObjectId
                    : -1;
        }

        private static int NativeLinkedSlot(
            OriginalSaveRuntimeEntitySnapshot? retainedRaw,
            DigglesTclEntity live,
            DigglesTclContext context)
        {
            // The native slot word is serialized independently of the link id: dangling links and
            // even unlinked objects carry stale slot residue (e.g. {id -1, slot 5}). Whenever the
            // live link state is unchanged relative to the archive the slot word round-trips
            // verbatim; only a genuinely re-linked entity emits its live slot.
            if (retainedRaw != null)
            {
                int retainedId = retainedRaw.LinkedObjectId;
                bool retainedResolved = retainedId > 0 &&
                    context.Entities.ContainsKey(retainedId);
                int expectedLiveHandle = retainedResolved ? retainedId : 0;
                if (live.LinkedToHandle == expectedLiveHandle) return retainedRaw.LinkedSlot;
            }
            return live.LinkedSlot;
        }

        private static OriginalSaveTclVariable[] CaptureTclVariables(
            OriginalSaveRuntimeEntitySnapshot? retainedRaw,
            DigglesTclEntity live,
            DigglesTclContext context,
            Dictionary<DigglesClass, Dictionary<string, string>> memberDefaultsByClass,
            out bool compatibilityPassthrough)
        {
            compatibilityPassthrough = retainedRaw != null &&
                TclVariablesMatch(
                    retainedRaw.TclVariables,
                    live,
                    context,
                    memberDefaultsByClass);
            if (compatibilityPassthrough)
            {
                var exact = new OriginalSaveTclVariable[retainedRaw!.TclVariables.Count];
                for (int index = 0; index < exact.Length; index++)
                    exact[index] = retainedRaw.TclVariables[index];
                return exact;
            }

            var result = new List<OriginalSaveTclVariable>(live.VarsScope.Vars.Count + 5);
            foreach (KeyValuePair<string, string> pair in live.VarsScope.Vars)
            {
                if (pair.Key == null || pair.Value == null || pair.Key.IndexOf('\0') >= 0 ||
                    pair.Value.IndexOf('\0') >= 0)
                {
                    throw new InvalidDataException(
                        $"Object {live.Handle} has invalid native Tcl variable state.");
                }
                if (retainedRaw == null && ExcludeFreshTclVariable(pair.Key)) continue;
                result.Add(new OriginalSaveTclVariable(pair.Key, pair.Value));
            }

            if (retainedRaw == null)
            {
                // The native decides per object whether an interp exists at all. An owner's dump
                // carries the Tcl 8.3.2 builtins plus the init-chain root-global copies (ANIM_*
                // alongside the genuine writes); a non-owner (idiobj deco) has nothing. A set
                // consisting only of engine-reconstructible values (root-global copies or
                // member-default mirrors) marks a non-owner and returns empty; one genuine write,
                // an active action, or a scripted class shape keeps the whole set plus builtins.
                Dictionary<string, string>? memberDefaults = null;
                if (live.Class != null)
                    memberDefaultsByClass.TryGetValue(live.Class, out memberDefaults);
                bool genuineWriteExists = result.Any(variable =>
                    !((context.Interp.GlobalScope.Vars.TryGetValue(
                           variable.Name, out string rootValue) &&
                       string.Equals(rootValue, variable.Value, StringComparison.Ordinal)) ||
                      (memberDefaults != null &&
                       memberDefaults.TryGetValue(variable.Name, out string memberValue) &&
                       string.Equals(memberValue, variable.Value, StringComparison.Ordinal))));
                bool ownsInterp = genuineWriteExists ||
                    live.ActiveAction != null ||
                    ClassOwnsTclState(live.Class);
                if (!ownsInterp)
                {
                    result.Clear();
                }
                else
                {
                    int insertAt = 0;
                    foreach ((string name, string value) in NativeTclInterpBuiltins)
                    {
                        if (!live.VarsScope.Vars.ContainsKey(name))
                            result.Insert(insertAt++, new OriginalSaveTclVariable(name, value));
                    }
                }
            }
            return result.ToArray();
        }

        // The Tcl 8.3.2 interpreter-environment constants every native object interp is born with
        // (a Tcl_CreateInterp side effect, byte-identical across all native saves).
        private static readonly (string Name, string Value)[] NativeTclInterpBuiltins =
        {
            ("env", ""),
            ("tclDefaultLibrary", ""),
            ("tcl_patchLevel", "8.3.2"),
            ("tcl_platform", ""),
            ("tcl_version", "8.3"),
        };

        private static bool ExcludeFreshTclVariable(string name)
        {
            // These entity properties are retained: unlike the other bookkeeping variables, the
            // restore path has no faithful producer for them. _autolight is not among them because
            // it is +0x3d0 bit 0x20 (FUN_004f09f0 property registry), captured into N37 and
            // re-seeded by RestoreNativeObjectFlags.
            if (name is
                "_invulnerable" or "_undeletable" or "_objectcollision")
            {
                return false;
            }
            return name.StartsWith("_", StringComparison.Ordinal) ||
                name is "this" or "outobj" or "wt";
        }

        private static bool TclVariablesMatch(
            IReadOnlyList<OriginalSaveTclVariable> retainedRaw,
            DigglesTclEntity live,
            DigglesTclContext context,
            Dictionary<DigglesClass, Dictionary<string, string>> memberDefaultsByClass)
        {
            var retainedValues = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < retainedRaw.Count; index++)
                retainedValues[retainedRaw[index].Name] = retainedRaw[index].Value;

            IReadOnlyDictionary<string, string> liveValues = live.VarsScope.Vars;
            foreach (KeyValuePair<string, string> pair in retainedValues)
            {
                if (!liveValues.TryGetValue(pair.Key, out string? value) ||
                    !string.Equals(value, pair.Value, StringComparison.Ordinal))
                {
                    LastPassthroughBreaker =
                        $"#{live.Handle} {live.Class?.Name}: retained '{pair.Key}'='{pair.Value}' " +
                        $"vs live '{value ?? "<absent>"}'";
                    return false;
                }
            }

            if (!memberDefaultsByClass.TryGetValue(
                    live.Class,
                    out Dictionary<string, string>? memberDefaults))
            {
                HashSet<string> memberVariables = context.CollectMemberVars(live.Class);
                memberDefaults = new Dictionary<string, string>(
                    memberVariables.Count,
                    StringComparer.Ordinal);
                foreach (string member in memberVariables)
                    memberDefaults.Add(member, context.MemberDefaultFor(live.Class, member));
                memberDefaultsByClass.Add(live.Class, memberDefaults);
            }
            foreach (KeyValuePair<string, string> pair in liveValues)
            {
                if (retainedValues.ContainsKey(pair.Key)) continue;
                if (IsReconstructedTclVariable(pair, live, memberDefaults, context)) continue;
                LastPassthroughBreaker =
                    $"#{live.Handle} {live.Class?.Name}: extra '{pair.Key}'='{pair.Value}'";
                return false;
            }
            return true;
        }

        // The most recent variable that broke a compatibility-passthrough match, i.e. the
        // init-created or diverged value that forced live re-serialization.
        public static string LastPassthroughBreaker = "";

        private static bool IsReconstructedTclVariable(
            KeyValuePair<string, string> variable,
            DigglesTclEntity live,
            IReadOnlyDictionary<string, string> memberDefaults,
            DigglesTclContext context)
        {
            if (string.Equals(variable.Key, "this", StringComparison.Ordinal))
            {
                return string.Equals(
                    variable.Value,
                    live.Handle.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
            }
            // Every "_"-prefixed variable is port engine bookkeeping (the set_* command mirror
            // convention: _selectable/_boxed/_materialneed/_partner_*). Archives written by the
            // original game carry a handful of them and matched as retained above; any extra one
            // is a port-side reconstruction, since every class init re-seeds its own set.
            if (variable.Key.StartsWith("_", StringComparison.Ordinal)) return true;
            if (memberDefaults.TryGetValue(variable.Key, out string? memberDefault))
            {
                return string.Equals(
                    variable.Value,
                    memberDefault,
                    StringComparison.Ordinal);
            }
            // Root-global copies are engine artifacts, not object state: init-chain scripts
            // (`global ANIM_STILL …` in animclassinit.tcl and friends) alias root globals into the
            // object scope under the per-object `global` law. Native object dumps never carry
            // them, so a copy whose value still equals the root global is reconstruction, not a
            // lazy write; a diverged value still breaks the passthrough and is captured.
            if (context.Interp.GlobalScope.Vars.TryGetValue(
                    variable.Key, out string rootGlobal))
            {
                return string.Equals(variable.Value, rootGlobal, StringComparison.Ordinal);
            }
            return false;
        }

        private static int[] CaptureEntityAttributePage(
            OriginalSaveRuntimeEntitySnapshot? residue,
            DigglesTclEntity live,
            DigglesTclContext context)
        {
            var result = new int[512];
            // A retained page with 0 slots means the native optional grid was absent for this
            // object, which is legal: start from zeroed defaults instead of failing.
            if (residue != null && residue.AttributePageBits.Count != 0)
                CopyExactPage(residue.AttributePageBits, result, "object attribute");
            // Slot placement follows OriginalSaveNativeAttributeSlots, not the port registry order:
            // the two are different permutations (native atr_Mood=29, port registry 28), so writing
            // in registry order makes the original read the wrong attributes. Names outside the
            // table keep their retained raw slot.
            foreach (string name in context.Stats.Registry.DefinedInOrder)
            {
                if (!TryNativeAttributeSlot(name, out int slot)) continue;
                result[slot] = AttributeBits(
                    live.Stats.Get(name, context.Stats.Registry),
                    residue != null,
                    result[slot]);
            }
            return result;
        }

        private static readonly HashSet<string> _warnedUnmappedAttributes = new(StringComparer.Ordinal);

        private static bool TryNativeAttributeSlot(string name, out int slot)
        {
            if (OriginalSaveNativeAttributeSlots.SlotByName.TryGetValue(name, out slot))
                return true;
            if (_warnedUnmappedAttributes.Add(name))
            {
                UnityEngine.Debug.LogWarning(
                    $"[OriginalSave] attribute '{name}' has no native slot mapping — its value " +
                    "is not serialized (regenerate the sentinel table if this is a new vanilla attribute).");
            }
            return false;
        }

        private static void CopyExactPage(
            IReadOnlyList<int> source,
            int[] destination,
            string description)
        {
            if (source.Count != destination.Length)
            {
                throw new InvalidDataException(
                    $"Retained {description} page has {source.Count} slots; expected {destination.Length}.");
            }
            for (int index = 0; index < destination.Length; index++)
                destination[index] = source[index];
        }

        private static int AttributeBits(
            double value,
            bool hasRetainedBits,
            int retainedBits)
        {
            float single = (float)value;
            if (hasRetainedBits && double.IsNaN(value) &&
                float.IsNaN(BitConverter.Int32BitsToSingle(retainedBits)))
            {
                // A double cannot reliably distinguish every Single NaN payload. Retain the
                // exact loaded bits while the live slot remains NaN.
                return retainedBits;
            }
            // Deliberately no finiteness check or StatDef clamp. Native save emits the raw
            // Single slot, including infinities, denormals, signed zero, and NaN payloads.
            return BitConverter.SingleToInt32Bits(single);
        }

        private static int FreshOwnerAttributeBits(double value)
        {
            float rounded = (float)value;
            if (float.IsNaN(rounded) || float.IsNegativeInfinity(rounded) ||
                (double)rounded <= value)
            {
                return BitConverter.SingleToInt32Bits(rounded);
            }

            int bits = BitConverter.SingleToInt32Bits(rounded);
            float down = BitConverter.Int32BitsToSingle(rounded > 0f ? bits - 1 : bits + 1);
            return BitConverter.SingleToInt32Bits(down);
        }

        private static OriginalSaveCanonicalEventQueueState CaptureEntityEventQueue(
            OriginalSaveRuntimeEntitySnapshot? residue,
            DigglesTclEntity live,
            DigglesTclContext context,
            IReadOnlyList<TimerScheduler.Entry> scheduledTimers)
        {
            const int capacity = OriginalSaveObjectEventQueueDocument.Capacity;
            var slots = new OriginalSaveTimerEventPayloadDocument?[capacity];
            int readCursor = 0;
            int writeCursor = 0;
            var retainedPending = new List<OriginalSaveRuntimeEntityEventSnapshot>(capacity - 1);
            if (residue == null)
            {
                readCursor = live.NativeEventRingReadCursor;
                writeCursor = live.NativeEventRingWriteCursor;
                ValidateRingCursor(readCursor, nameof(live.NativeEventRingReadCursor));
                ValidateRingCursor(writeCursor, nameof(live.NativeEventRingWriteCursor));
                if (live.NativeEventRingSlots.Count != capacity)
                {
                    throw new InvalidDataException(
                        $"Object {live.Handle} has an invalid live native event-ring capacity.");
                }
                for (int slot = 0; slot < slots.Length; slot++)
                    slots[slot] = live.NativeEventRingSlots[slot];
            }
            else
            {
                readCursor = residue.EventQueueReadCursor;
                writeCursor = residue.EventQueueWriteCursor;
                ValidateRingCursor(readCursor, nameof(residue.EventQueueReadCursor));
                ValidateRingCursor(writeCursor, nameof(residue.EventQueueWriteCursor));
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    OriginalSaveRuntimeEntityEventSnapshot? source = residue.EventQueueSlots[slot];
                    if (source != null) slots[slot] = CloneEventPayload(source);
                }
                if (live.NativeEventRingRevision != 0)
                {
                    readCursor = live.NativeEventRingReadCursor;
                    writeCursor = live.NativeEventRingWriteCursor;
                    ValidateRingCursor(readCursor, nameof(live.NativeEventRingReadCursor));
                    ValidateRingCursor(writeCursor, nameof(live.NativeEventRingWriteCursor));
                    for (int slot = 0; slot < slots.Length; slot++)
                        slots[slot] = live.NativeEventRingSlots[slot];
                }
                else
                {
                    int pendingCount = (writeCursor - readCursor + capacity) & (capacity - 1);
                    for (int pending = 0; pending < pendingCount; pending++)
                    {
                        int slot = (readCursor + pending) & (capacity - 1);
                        retainedPending.Add(residue.EventQueueSlots[slot] ??
                            throw new InvalidDataException(
                                $"Retained object {live.Handle} event slot {slot} is empty inside its pending window."));
                    }

                    EntityEvent[] pendingLive = live.PendingEvents.ToArray();
                    int consumed = FindConsumedPrefix(retainedPending, pendingLive, live.Handle);
                    int retainedRemaining = retainedPending.Count - consumed;
                    readCursor = (readCursor + consumed) & (capacity - 1);
                    if (retainedRemaining > pendingLive.Length)
                    {
                        // A live queue cannot remove from its tail. Rebuild the complete pending window
                        // when a caller replaced the inbox by means other than the native pop operation.
                        readCursor = writeCursor;
                        retainedRemaining = 0;
                    }

                    for (int index = retainedRemaining; index < pendingLive.Length; index++)
                    {
                        slots[writeCursor] = CreateEventPayload(
                            pendingLive[index],
                            live.Handle,
                            context.Sim!.GameTime);
                        writeCursor = (writeCursor + 1) & (capacity - 1);
                    }
                }
            }

            OriginalSaveTimerEventPayloadDocument[] delayed = CaptureDelayedEvents(
                residue,
                live.Handle,
                scheduledTimers);
            return new OriginalSaveCanonicalEventQueueState
            {
                ReadCursor = readCursor,
                WriteCursor = writeCursor,
                Slots = slots,
                DelayedRecords = delayed,
            };
        }

        private static int FindConsumedPrefix(
            IReadOnlyList<OriginalSaveRuntimeEntityEventSnapshot> retainedPending,
            IReadOnlyList<EntityEvent> livePending,
            int ownerObjectId)
        {
            for (int consumed = 0; consumed <= retainedPending.Count; consumed++)
            {
                int remaining = retainedPending.Count - consumed;
                if (livePending.Count < remaining) continue;
                bool matches = true;
                for (int index = 0; index < remaining; index++)
                {
                    if (EventMatches(
                            retainedPending[consumed + index],
                            livePending[index],
                            ownerObjectId))
                        continue;
                    matches = false;
                    break;
                }
                if (matches) return consumed;
            }
            return retainedPending.Count;
        }

        private static OriginalSaveTimerEventPayloadDocument[] CaptureDelayedEvents(
            OriginalSaveRuntimeEntitySnapshot? residue,
            int ownerObjectId,
            IReadOnlyList<TimerScheduler.Entry> scheduledTimers)
        {
            var delayedEntries = new List<TimerScheduler.Entry>();
            foreach (TimerScheduler.Entry scheduled in scheduledTimers)
            {
                if (scheduled.Handle == ownerObjectId &&
                    !TryGetTimerSerial(scheduled.Payload, out _))
                    delayedEntries.Add(scheduled);
            }
            var result = new OriginalSaveTimerEventPayloadDocument[delayedEntries.Count];
            var retainedUsed = residue == null
                ? Array.Empty<bool>()
                : new bool[residue.DelayedEvents.Count];
            for (int index = 0; index < delayedEntries.Count; index++)
            {
                TimerScheduler.Entry live = delayedEntries[index];
                OriginalSaveRuntimeEntityEventSnapshot? exact = null;
                if (residue != null)
                {
                    for (int retainedIndex = 0;
                         retainedIndex < residue.DelayedEvents.Count;
                         retainedIndex++)
                    {
                        if (retainedUsed[retainedIndex]) continue;
                        OriginalSaveRuntimeEntityEventSnapshot candidate =
                            residue.DelayedEvents[retainedIndex];
                        if (candidate.DueTime.Bits != BitConverter.DoubleToInt64Bits(live.NextFire) ||
                            !EventMatches(candidate, live, ownerObjectId))
                            continue;
                        retainedUsed[retainedIndex] = true;
                        exact = candidate;
                        break;
                    }
                }
                result[index] = exact != null
                    ? CloneEventPayload(exact)
                    : CreateEventPayload(live, ownerObjectId, live.NextFire);
            }
            return result;
        }

        private static void ValidateRingCursor(int value, string description)
        {
            if ((uint)value >= OriginalSaveObjectEventQueueDocument.Capacity)
                throw new InvalidDataException($"Retained event queue has invalid {description} {value}.");
        }

        private static bool EventMatches(
            OriginalSaveRuntimeEntityEventSnapshot source,
            EntityEvent live,
            int ownerObjectId) =>
            string.Equals(source.EventName, live.Name, StringComparison.Ordinal) &&
            source.OwnerObjectId == ownerObjectId &&
            PayloadMatches(
                source.Subject1ObjectId,
                source.Subject2ObjectId,
                source.Subject3ObjectId,
                source.Arguments,
                source.Text1.Text,
                source.Text2.Text,
                source.Word1,
                source.Word2,
                source.Word3,
                live.Payload);

        private static bool EventMatches(
            OriginalSaveRuntimeEntityEventSnapshot source,
            TimerScheduler.Entry live,
            int ownerObjectId) =>
            string.Equals(source.EventName, live.Event, StringComparison.Ordinal) &&
            source.OwnerObjectId == ownerObjectId &&
            PayloadMatches(
                source.Subject1ObjectId,
                source.Subject2ObjectId,
                source.Subject3ObjectId,
                source.Arguments,
                source.Text1.Text,
                source.Text2.Text,
                source.Word1,
                source.Word2,
                source.Word3,
                live.Payload);

        private static bool PayloadMatches(
            int subject1,
            int subject2,
            int subject3,
            IReadOnlyList<OriginalSaveRaw32> positions,
            string text1,
            string text2,
            OriginalSaveRaw32 number1,
            OriginalSaveRaw32 number2,
            OriginalSaveRaw32 number3,
            IReadOnlyDictionary<string, string>? payload)
        {
            if (PayloadInt(payload, "-subject1", -1) != subject1 ||
                PayloadInt(payload, "-subject2", -1) != subject2 ||
                PayloadInt(payload, "-subject3", -1) != subject3 ||
                !string.Equals(PayloadString(payload, "-text1"), text1, StringComparison.Ordinal) ||
                !string.Equals(PayloadString(payload, "-text2"), text2, StringComparison.Ordinal) ||
                PayloadFloatBits(payload, "-num1") != number1.Bits ||
                PayloadFloatBits(payload, "-num2") != number2.Bits ||
                PayloadFloatBits(payload, "-num3") != number3.Bits)
                return false;
            for (int vector = 0; vector < 3; vector++)
            {
                int[] bits = PayloadVectorBits(payload, "-pos" + (vector + 1));
                for (int component = 0; component < 3; component++)
                {
                    if (bits[component] != positions[vector * 3 + component].Bits) return false;
                }
            }
            return true;
        }

        private static OriginalSaveTimerEventPayloadDocument CloneEventPayload(
            OriginalSaveRuntimeEntityEventSnapshot source) =>
            source.CreatePayloadDocument();

        private static OriginalSaveTimerEventPayloadDocument CreateEventPayload(
            EntityEvent source,
            int ownerObjectId,
            double creationStamp) =>
            CreateEventPayload(source.Name, source.Payload, ownerObjectId, creationStamp);

        private static OriginalSaveTimerEventPayloadDocument CreateEventPayload(
            TimerScheduler.Entry source,
            int ownerObjectId,
            double creationStamp) =>
            CreateEventPayload(source.Event, source.Payload, ownerObjectId, creationStamp);

        private static OriginalSaveTimerEventPayloadDocument CreateEventPayload(
            string eventName,
            IReadOnlyDictionary<string, string>? payload,
            int ownerObjectId,
            double creationStamp) =>
            OriginalSaveTimerEventPayloadDocument.CreateRuntimePayload(
                eventName,
                payload,
                ownerObjectId,
                creationStamp);

        private static int PayloadInt(
            IReadOnlyDictionary<string, string>? payload,
            string key,
            int defaultValue)
        {
            if (payload == null || !payload.TryGetValue(key, out string? value)) return defaultValue;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                throw new InvalidDataException($"Live event payload '{key}' is not an Int32.");
            return result;
        }

        private static string PayloadString(
            IReadOnlyDictionary<string, string>? payload,
            string key) =>
            payload != null && payload.TryGetValue(key, out string? value) ? value : "";

        private static int PayloadFloatBits(
            IReadOnlyDictionary<string, string>? payload,
            string key)
        {
            if (payload == null || !payload.TryGetValue(key, out string? value)) return 0;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ||
                !Finite(result))
                throw new InvalidDataException($"Live event payload '{key}' is not a finite native float.");
            return BitConverter.SingleToInt32Bits(result);
        }

        private static int[] PayloadVectorBits(
            IReadOnlyDictionary<string, string>? payload,
            string key)
        {
            var result = new int[3];
            if (payload == null || !payload.TryGetValue(key, out string? value)) return result;
            IReadOnlyList<string> values = TclList.Parse(value);
            if (values.Count != 3)
                throw new InvalidDataException($"Live event payload '{key}' is not a three-float vector.");
            for (int index = 0; index < result.Length; index++)
            {
                if (!float.TryParse(
                        values[index],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float component) || !Finite(component))
                    throw new InvalidDataException($"Live event payload '{key}' contains a non-finite float.");
                result[index] = BitConverter.SingleToInt32Bits(component);
            }
            return result;
        }

        private static OriginalSaveTimerRecord[] CaptureGlobalTimers(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            DigglesTclContext context,
            IReadOnlyList<TimerRegistry.Entry> registryEntries,
            IReadOnlyList<TimerScheduler.Entry> scheduledTimers)
        {
            var result = new List<OriginalSaveTimerRecord>(registryEntries.Count);
            var schedulerSerials = new HashSet<long>();
            var registrySerials = new HashSet<long>();
            // Index the scheduler once (serial to entry). Per-registry linear scans are O(N²) over
            // the timer population and dominate capture time on large sessions.
            var scheduledBySerial = new Dictionary<long, TimerScheduler.Entry>(scheduledTimers.Count);
            var duplicateSerials = new HashSet<long>();
            foreach (TimerScheduler.Entry scheduled in scheduledTimers)
            {
                if (!TryGetTimerSerial(scheduled.Payload, out long serial)) continue;
                if (scheduledBySerial.ContainsKey(serial)) duplicateSerials.Add(serial);
                else scheduledBySerial.Add(serial, scheduled);
            }
            foreach (TimerRegistry.Entry registry in registryEntries)
            {
                registrySerials.Add(registry.Serial);
                if (registry.Cancelled) continue;
                // A fired one-shot lingers in the Tcl registry until the lazy reap while its
                // scheduler entry is already consumed — the native equivalent slot is freed at
                // that point, so there is nothing to serialize. Anything else without a
                // scheduled counterpart is real incoherence and still throws (below).
                if (registry.RemainingFires == 0 &&
                    !scheduledBySerial.ContainsKey(registry.Serial))
                {
                    continue;
                }
                TimerScheduler.Entry scheduled = FindScheduledTimer(
                    registry,
                    scheduledBySerial,
                    duplicateSerials,
                    schedulerSerials);
                OriginalSaveRuntimeTimer? exact = FindRetainedTimer(retainedRaw, registry.Serial);
                OriginalSaveTimerEventPayloadDocument payload = exact != null &&
                    TimerPayloadMatches(exact, registry, scheduled)
                    ? CloneTimerPayload(exact)
                    : CreateEventPayload(
                        scheduled.Event,
                        scheduled.Payload,
                        scheduled.Handle,
                        context.Sim!.GameTime);
                result.Add(new OriginalSaveTimerRecord(
                    registry.UserId,
                    new OriginalSaveRaw64(BitConverter.DoubleToInt64Bits(scheduled.NextFire)),
                    new OriginalSaveRaw64(BitConverter.DoubleToInt64Bits(scheduled.Interval)),
                    scheduled.RepeatsLeft,
                    scheduled.Handle,
                    payload));
            }
            foreach (TimerScheduler.Entry scheduled in scheduledTimers)
            {
                if (TryGetTimerSerial(scheduled.Payload, out long serial) &&
                    !registrySerials.Contains(serial))
                {
                    throw new InvalidDataException(
                        $"Scheduled timer serial {serial} has no live registry entry.");
                }
            }
            return result.ToArray();
        }

        private static TimerScheduler.Entry FindScheduledTimer(
            TimerRegistry.Entry registry,
            IReadOnlyDictionary<long, TimerScheduler.Entry> scheduledBySerial,
            HashSet<long> duplicateSerials,
            HashSet<long> usedSerials)
        {
            if (duplicateSerials.Contains(registry.Serial))
                throw new InvalidDataException(
                    $"Timer serial {registry.Serial} has multiple scheduled entries.");
            if (!scheduledBySerial.TryGetValue(registry.Serial, out TimerScheduler.Entry match))
                throw new InvalidDataException($"Live timer serial {registry.Serial} has no scheduled entry.");
            if (!usedSerials.Add(registry.Serial) || match.Handle != registry.Handle ||
                !string.Equals(match.Event, registry.Event, StringComparison.Ordinal) ||
                match.RepeatsLeft != registry.RemainingFires)
            {
                throw new InvalidDataException(
                    $"Live timer serial {registry.Serial} registry and scheduler state disagree.");
            }
            int payloadUserId = PayloadInt(
                match.Payload,
                TimerRegistry.UserIdKey,
                registry.UserId);
            if (payloadUserId != registry.UserId)
                throw new InvalidDataException($"Live timer serial {registry.Serial} has a mismatched user id.");
            return match;
        }

        private static bool TryGetTimerSerial(
            IReadOnlyDictionary<string, string>? payload,
            out long serial)
        {
            serial = 0;
            return payload != null &&
                payload.TryGetValue(TimerRegistry.SerialKey, out string? value) &&
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out serial) &&
                serial > 0;
        }

        private static OriginalSaveRuntimeTimer? FindRetainedTimer(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            long serial)
        {
            if (retainedRaw == null || serial <= 0 || serial > int.MaxValue) return null;
            int index = (int)serial - 1;
            return (uint)index < (uint)retainedRaw.Timers.Records.Count
                ? retainedRaw.Timers.Records[index]
                : null;
        }

        private static bool TimerPayloadMatches(
            OriginalSaveRuntimeTimer source,
            TimerRegistry.Entry registry,
            TimerScheduler.Entry scheduled)
        {
            if (source.UserId != registry.UserId || source.OwnerObjectId != registry.Handle ||
                source.EventOwnerObjectId != registry.Handle ||
                !string.Equals(source.EventName, registry.Event, StringComparison.Ordinal))
                return false;
            var positions = new[]
            {
                source.Position1X, source.Position1Y, source.Position1Z,
                source.Position2X, source.Position2Y, source.Position2Z,
                source.Position3X, source.Position3Y, source.Position3Z,
            };
            return PayloadMatches(
                source.Subject1ObjectId,
                source.Subject2ObjectId,
                source.Subject3ObjectId,
                positions,
                source.Text1.Text,
                source.Text2.Text,
                source.Number1,
                source.Number2,
                source.Number3,
                scheduled.Payload);
        }

        private static OriginalSaveTimerEventPayloadDocument CloneTimerPayload(
            OriginalSaveRuntimeTimer source) =>
            new(
                source.CopyFixedEventName(),
                source.EventOwnerObjectId,
                source.Subject1ObjectId,
                source.Subject2ObjectId,
                source.Subject3ObjectId,
                new[]
                {
                    source.Position1X, source.Position1Y, source.Position1Z,
                    source.Position2X, source.Position2Y, source.Position2Z,
                    source.Position3X, source.Position3Y, source.Position3Z,
                },
                new OriginalSaveNativeString(source.Text1.CopyBytesIncludingTerminator()),
                new OriginalSaveNativeString(source.Text2.CopyBytesIncludingTerminator()),
                source.Number1,
                source.Number2,
                source.Number3,
                source.CreatedAt);

        private static OriginalSaveCanonicalFamilyState? CaptureFamilyState(
            DigglesTclEntity live)
        {
            bool hasVars = live.VarsScope.Vars.ContainsKey("_partner_partner") ||
                live.VarsScope.Vars.ContainsKey("_partner_mother") ||
                live.VarsScope.Vars.ContainsKey("_partner_father") ||
                live.VarsScope.Vars.ContainsKey("_partner_children") ||
                live.VarsScope.Vars.ContainsKey("_partner_preg");
            if (!live.HasFamilyState && !hasVars && live.NativeObjectType is not (1 or 11))
                return null;

            CaptureFamilyLink(
                live,
                "partner",
                live.FamilyPartnerId,
                live.FamilyPartnerState,
                live.FamilyPartnerReferenceObjectId,
                out int partnerId,
                out int partnerState,
                out int partnerReference);
            CaptureFamilyLink(
                live,
                "mother",
                live.FamilyMotherId,
                live.FamilyMotherState,
                live.FamilyMotherReferenceObjectId,
                out int motherId,
                out int motherState,
                out int motherReference);
            CaptureFamilyLink(
                live,
                "father",
                live.FamilyFatherId,
                live.FamilyFatherState,
                live.FamilyFatherReferenceObjectId,
                out int fatherId,
                out int fatherState,
                out int fatherReference);

            string[] children = live.FamilyChildrenNames.ToArray();
            if (live.VarsScope.Vars.TryGetValue("_partner_children", out string? childList))
                children = TclList.Parse(childList).ToArray();
            bool pregnancy = live.FamilyPregnancyFlag;
            if (live.VarsScope.Vars.TryGetValue("_partner_preg", out string? pregnancyValue))
                pregnancy = TclExpr.ToBool(pregnancyValue);
            return new OriginalSaveCanonicalFamilyState
            {
                PartnerId = partnerId,
                PartnerState = partnerState,
                PartnerReferenceObjectId = partnerReference,
                MotherId = motherId,
                MotherState = motherState,
                MotherReferenceObjectId = motherReference,
                FatherId = fatherId,
                FatherState = fatherState,
                FatherReferenceObjectId = fatherReference,
                ChildrenNames = children,
                PregnancyFlag = pregnancy,
            };
        }

        private static void CaptureFamilyLink(
            DigglesTclEntity live,
            string role,
            int retainedId,
            int retainedState,
            int retainedReference,
            out int id,
            out int state,
            out int reference)
        {
            id = retainedId;
            state = retainedState;
            reference = retainedReference;
            if (!live.VarsScope.Vars.TryGetValue("_partner_" + role, out string? value)) return;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                id = 0;
                state = 0;
                reference = -1;
                return;
            }
            if (state != 1 || id != retainedId) reference = -1;
            state = 1;
        }

        private static OriginalSaveCanonicalTalkIssueState? CaptureTalkIssueState(
            DigglesTclEntity live)
        {
            if (live.TalkIssues == null && live.NativeObjectType != 1) return null;
            var entries = live.TalkIssues == null
                ? Array.Empty<KeyValuePair<string, double>>()
                : CopyPairs(live.TalkIssues);
            return new OriginalSaveCanonicalTalkIssueState
            {
                Entries = entries,
                StoredSum = live.TalkIssuesSum,
            };
        }

        private static OriginalSaveCanonicalPlannerState? CapturePlannerState(
            DigglesTclEntity live,
            DigglesTclContext context)
        {
            if (live.NativeObjectType != 1 && live.PlannerIdleAnnounceFlag == 0 &&
                live.PlannerCurrentWorkplaceId < 0 && live.PreferredWorkplaceHandle < 0)
                return null;
            int idleFlag = context.ProdDispatcher != null
                ? (context.ProdDispatcher.IsPooled(live.Handle, live.Owner) ? 1 : 0)
                : live.PlannerIdleAnnounceFlag;
            return new OriginalSaveCanonicalPlannerState
            {
                IdleAnnounceFlag = idleFlag,
                IdleSince = live.PlannerIdleSince,
                CurrentWorkplaceObjectId = live.PlannerCurrentWorkplaceId,
                AssignedWorkplaceObjectId = live.PreferredWorkplaceHandle,
            };
        }

        private static OriginalSaveCanonicalProductionState? CaptureProductionState(
            DigglesTclEntity live,
            OriginalSaveRuntimeEntityProductionSnapshot? retainedProduction,
            DigglesTclContext context)
        {
            if (live.NativeObjectType is not (5 or 6 or 7 or 10) &&
                live.NativeProductionSlots == null && live.ProdSlots.Count == 0)
                return null;

            bool freshProductionBody = live.NativeProductionSlots == null;
            string[] freshSlotNames = freshProductionBody
                ? OriginalSaveRuntimeSessionStager.ReadProductionSlotNames(context, live.Class)
                : Array.Empty<string>();
            var slots = new List<OriginalSaveCanonicalProductionSlotState>(Math.Max(
                Math.Max(live.NativeProductionSlots?.Count ?? 0, live.ProdSlots.Count),
                freshSlotNames.Length));
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            if (live.NativeProductionSlots != null)
            {
                foreach (DigglesTclEntity.NativeProductionSlotState source in
                    live.NativeProductionSlots)
                {
                    slots.Add(CaptureProductionSlot(live, source));
                    emitted.Add(source.ClassName);
                }
            }
            foreach (string slotName in freshSlotNames)
            {
                live.ProdSlots.TryGetValue(slotName, out double count);
                slots.Add(CreateFreshProductionSlot(slotName, count));
                emitted.Add(slotName);
            }
            foreach (KeyValuePair<string, double> pair in live.ProdSlots)
            {
                if (!emitted.Add(pair.Key)) continue;
                // Catalog-seeded zero-count slots absent from the archive are inert: the native
                // rebuilds its slot array from the class tech-tree on load (save-re-reports/B2),
                // and emitting them would grow the archived slot set on every no-op round-trip.
                // Only slots carrying real state (a nonzero order count) are appended.
                if (pair.Value == 0) continue;
                slots.Add(CreateFreshProductionSlot(pair.Key, pair.Value));
            }

            var guests = new OriginalSaveCanonicalGuestSeatState[DigglesTclEntity.GuestSeatCount];
            ValidateGuestArray(live.GuestLinks, nameof(live.GuestLinks));
            ValidateGuestArray(live.Guests, nameof(live.Guests));
            ValidateGuestArray(live.GuestOrders, nameof(live.GuestOrders));
            for (int index = 0; index < guests.Length; index++)
            {
                int link = live.GuestLinks?[index] ?? -1;
                int guest = live.Guests?[index] ?? 0;
                int orders = live.GuestOrders == null
                    ? 0
                    : NativeInteger(live.GuestOrders[index], $"guest order {index}");
                // The native serializes both -1 (constructor) and 0 (cleared) as "no guest", while
                // the port collapses them to 0 live. An unchanged seat round-trips its archived
                // raw words verbatim; only genuinely mutated seats emit live state.
                if (retainedProduction != null &&
                    index < retainedProduction.GuestSeats.Count)
                {
                    OriginalSaveRuntimeEntityProductionGuestSeatSnapshot seat =
                        retainedProduction.GuestSeats[index];
                    int projectedOccupant = seat.GuestObjectId > 0 &&
                        context.Entities.ContainsKey(seat.GuestObjectId)
                            ? seat.GuestObjectId
                            : 0;
                    if (link == seat.SeatLinkId && guest == projectedOccupant &&
                        orders == seat.OrderCount)
                    {
                        guests[index] = new OriginalSaveCanonicalGuestSeatState(
                            seat.SeatLinkId,
                            seat.GuestObjectId,
                            seat.OrderCount);
                        continue;
                    }
                }
                guests[index] = new OriginalSaveCanonicalGuestSeatState(link, guest, orders);
            }

            byte unpack = live.UnpackRequested ? (byte)1 : (byte)0;
            return new OriginalSaveCanonicalProductionState
            {
                Slots = slots.ToArray(),
                WorkState = ProductionWorkState(live.ProdState),
                AssignedWorkerObjectId = live.CurrentWorkerHandle,
                RequestBytes = new[]
                {
                    unpack,
                    live.PackRequested ? (byte)1 : (byte)0,
                    live.BuildupRequested ? (byte)1 : (byte)0,
                    live.NativeProductionUnpackFlag,
                    live.NativeProductionPackFlag,
                    live.NativeProductionResidue2D,
                    unpack,
                },
                PendingPositionBits = new[]
                {
                    BitConverter.SingleToInt32Bits(live.OrderPos.x),
                    BitConverter.SingleToInt32Bits(live.OrderPos.y),
                    BitConverter.SingleToInt32Bits(live.OrderPos.z),
                },
                FlowCompatibilityBytes = CaptureProductionFlowBytes(live),
                ProductionValues = new[]
                {
                    live.NativeProductionCurrentProductStamp,
                    live.ProdProgressCur,
                    live.ProdProgressMax,
                    live.NativeProductionInventionCandidateSlot,
                },
                ProductionEnabled = CaptureProductionEnabled(live),
                EnergyValues = new[]
                {
                    live.EnergyClass,
                    BitConverter.SingleToInt32Bits(live.EnergyConsumption),
                    BitConverter.SingleToInt32Bits(live.EnergyStore),
                    BitConverter.SingleToInt32Bits(live.EnergyMaxStore),
                    BitConverter.SingleToInt32Bits(live.EnergyCurrentLoad),
                },
                GuestSeats = guests,
                State3C = live.NativeProductionState3C,
                State140 = live.NativeProductionState140,
            };
        }

        private static byte[] CaptureProductionFlowBytes(DigglesTclEntity live)
        {
            if (live.NativeProductionFlowBytes.Length != 8)
                throw new InvalidDataException("A native production flow block requires eight bytes.");
            byte[] result = (byte[])live.NativeProductionFlowBytes.Clone();
            result[2] = live.ProdSwitchMode ? (byte)1 : (byte)0;
            result[4] = live.ProdExclusiveMode ? (byte)1 : (byte)0;
            return result;
        }

        private static OriginalSaveCanonicalProductionSlotState CaptureProductionSlot(
            DigglesTclEntity live,
            DigglesTclEntity.NativeProductionSlotState source)
        {
            live.ProdSlots.TryGetValue(source.ClassName, out double count);
            return new OriginalSaveCanonicalProductionSlotState
            {
                RegistryValue1 = source.RegistryValue1,
                RegistryValue2 = source.RegistryValue2,
                ClassName = source.ClassName,
                ResourceName = source.ResourceName,
                OrderCount = NativeInteger(count, $"production slot '{source.ClassName}'"),
                DemandBits = source.DemandBits,
                Invented = source.Invented,
                Buildable = source.Buildable,
            };
        }

        private static OriginalSaveCanonicalProductionSlotState CreateFreshProductionSlot(
            string itemName,
            double orderCount)
        {
            return new OriginalSaveCanonicalProductionSlotState
            {
                RegistryValue1 = FreshProductionOwnerCounterSlot(itemName),
                RegistryValue2 = FreshProductionBlueprintSlot(itemName),
                ClassName = itemName,
                ResourceName = itemName,
                OrderCount = NativeInteger(orderCount, $"production slot '{itemName}'"),
                DemandBits = 0,
                Invented = 0,
                Buildable = 0,
            };
        }

        private static int FreshProductionOwnerCounterSlot(string itemName)
        {
            if (!_empiricalProductionItems.Contains(itemName))
            {
                WarnUnmappedProductionItem(itemName, "owner counter");
                return -1;
            }
            return OriginalSaveNativeAttributeSlots.SlotByName.TryGetValue(itemName, out int slot)
                ? slot
                : -1;
        }

        private static int FreshProductionBlueprintSlot(string itemName)
        {
            string blueprintName = $"Bp{itemName}";
            if (OriginalSaveNativeAttributeSlots.SlotByName.TryGetValue(
                    blueprintName,
                    out int slot))
            {
                return slot;
            }
            WarnUnmappedProductionItem(itemName, $"blueprint attribute '{blueprintName}'");
            return -1;
        }

        private static void WarnUnmappedProductionItem(string itemName, string field)
        {
            if (!_warnedUnmappedProductionItems.Add(itemName)) return;
            UnityEngine.Debug.LogWarning(
                $"[OriginalSave] production item '{itemName}' was not mapped for its native " +
                $"{field}; emitting -1. Add a corpus observation before assigning a slot.");
        }

        private static int NativeInteger(double value, string description)
        {
            if (!Finite(value) || value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
                throw new InvalidDataException($"Live {description} is not a native Int32 value.");
            return (int)value;
        }

        private static void ValidateGuestArray<T>(T[]? values, string description)
        {
            if (values != null && values.Length != DigglesTclEntity.GuestSeatCount)
                throw new InvalidDataException($"Live {description} does not contain 16 guest records.");
        }

        private static int ProductionWorkState(string value) => value switch
        {
            "" or "idle" => 0,
            "research" => 1,
            "produce" => 2,
            "bring" => 3,
            "pack" => 4,
            "buildup" => 5,
            "unpack" => 6,
            _ => throw new InvalidDataException($"Live production work-state '{value}' is not serializable."),
        };

        private static byte CaptureProductionEnabled(DigglesTclEntity live)
        {
            if (!live.VarsScope.Vars.TryGetValue("_prod_enabled", out string? value)) return 1;
            return string.IsNullOrEmpty(value) || value == "0" ? (byte)0 : (byte)1;
        }

        private static OriginalSaveCanonicalAnimationState[] CaptureAnimationControllers(
            OriginalSaveRuntimeSnapshot? retainedRaw,
            DigglesTclContext context)
        {
            var result = new List<OriginalSaveCanonicalAnimationState>(context.Entities.Count);
            var liveObjectIds = new HashSet<int>();
            foreach (DigglesTclEntity entity in context.Entities.Values.OrderBy(value => value.Handle))
            {
                liveObjectIds.Add(entity.Handle);
                DigglesAnimator? animator = entity.GameObject == null
                    ? null
                    : entity.GameObject.GetComponentInChildren<DigglesAnimator>(true);
                DigglesAnimClip? clip = animator?.CurrentClip;
                int modelSlot = -1;
                int animationState = -1;
                if (clip?.SourceModel != null)
                {
                    modelSlot = OriginalSaveRuntimeSessionStager.NativeModelSlot(clip.SourceModel);
                    animationState = ResolveNativeAnimationState(clip);
                }
                int mode = animator != null
                    ? (int)animator.CurrentMode
                    : entity.CurrentAnimMode is >= 0 and <= 2 ? entity.CurrentAnimMode : 0;
                result.Add(new OriginalSaveCanonicalAnimationState
                {
                    ObjectId = entity.Handle,
                    ClipName = animator?.CurrentAnimName ?? entity.CurrentAnim ?? "",
                    Mode = mode,
                    CurrentFrame = animator?.CurrentFrame ?? 0,
                    FrameCount = animator?.CurrentFrameCount ?? 0,
                    FrameValues = clip?.SourceMeshIndices?.ToArray() ?? Array.Empty<int>(),
                    StartTime = context.Sim!.GameTime,
                    ModelDatabaseSlot = modelSlot,
                    AnimationState = animationState,
                });
            }

            // A native controller can outlive its object. Keeping these records is explicitly
            // compatibility retention; no live entity reads them and new campaigns emit none.
            if (retainedRaw != null)
            {
                foreach (OriginalSaveRuntimeAnimationController orphan in
                    retainedRaw.AnimationControllers.Records)
                {
                    if (liveObjectIds.Contains(orphan.ControllerIndex)) continue;
                    result.Add(new OriginalSaveCanonicalAnimationState
                    {
                        ObjectId = orphan.ControllerIndex,
                        Mode = orphan.RawControlA,
                        CurrentFrame = orphan.CurrentOrCompatibilityValue,
                        FrameCount = orphan.DeclaredValueCount,
                        FrameValues = orphan.Values.ToArray(),
                        StartTime = orphan.NativeTimeState.AsDouble,
                        ModelDatabaseSlot = orphan.ModelDatabaseSlot,
                        AnimationState = orphan.AnimationState,
                        IsRetainedOrphan = true,
                        RawStateByte = orphan.RawStateByte,
                        RawTimeBits = orphan.NativeTimeState.Bits,
                        RepeatedControllerIndex = orphan.RepeatedControllerIndex,
                        RawControlB = orphan.RawControlB,
                    });
                }
            }
            result.Sort((left, right) => left.ObjectId.CompareTo(right.ObjectId));
            return result.ToArray();
        }

        private static int ResolveNativeAnimationState(DigglesAnimClip clip)
        {
            ThreeDbModel? model = clip.SourceModel;
            if (model == null) return -1;
            ThreeDbObject? owner = null;
            foreach (ThreeDbObject candidate in model.Objects)
            {
                if (!string.Equals(candidate.Name, clip.SourceObjName, StringComparison.Ordinal))
                    continue;
                owner = candidate;
                break;
            }
            if (owner == null) return -1;
            int match = -1;
            foreach (uint index in owner.Indices)
            {
                if (index >= (uint)model.Anims.Count ||
                    !string.Equals(model.Anims[(int)index].Name1, clip.Name, StringComparison.Ordinal))
                    continue;
                if (match >= 0) return -1;
                match = (int)index;
            }
            return match;
        }

        private static OriginalSaveObjectIconRecord[] CaptureObjectIcons(
            DigglesTclContext context)
        {
            var active = new List<(int ObjectId, int Value, int Type, float OffsetY)>();
            bool hasHitpoints = context.Stats.Registry.TryGet("atr_Hitpoints", out StatDef hp);
            foreach (DigglesTclEntity entity in context.Entities.Values.OrderBy(value => value.Handle))
            {
                if (hasHitpoints && hp.Max > 0d && HasPermanentHitpointIcon(context, entity))
                {
                    double fraction = entity.Stats.Get("atr_Hitpoints", context.Stats.Registry) / hp.Max;
                    if (!Finite(fraction))
                        throw new InvalidDataException($"Object {entity.Handle} has invalid hitpoint state.");
                    // The native creates a type-0 hitpoint icon only when it is worth showing
                    // (CObj_Tick:426-433: hpFrac <= 0.5 or a changed value; a full-HP idle bar is
                    // expired by the holder countdown FUN_005dd100). An unconditional slot for
                    // every dwarf leaves orphan records the holder can never expire, so the
                    // original draws permanent health bars over everyone.
                    if (fraction < 1.0d)
                    {
                        active.Add((
                            entity.Handle,
                            Mathf.RoundToInt(15f * Mathf.Clamp01((float)fraction)),
                            0,
                            -0.3f));
                    }
                }
                if (entity.NativeObjectType == 1 &&
                    entity.Owner == context.LocalPlayer &&
                    !string.IsNullOrEmpty(entity.CurrentProdItem))
                {
                    active.Add((
                        entity.Handle,
                        WorkIconIndex(entity),
                        5,
                        -0.8f));
                }
            }
            if (active.Count > OriginalSaveObjectIconDocument.SlotCount)
                throw new InvalidDataException("The live object-icon set exceeds 128 native slots.");

            var result = new OriginalSaveObjectIconRecord[OriginalSaveObjectIconDocument.SlotCount];
            for (int slot = 0; slot < result.Length; slot++)
            {
                if (slot < active.Count)
                {
                    (int objectId, int value, int type, float offsetY) = active[slot];
                    result[slot] = new OriginalSaveObjectIconRecord(
                        slot,
                        objectId,
                        value,
                        -1,
                        type,
                        new OriginalSaveRaw32(0),
                        new OriginalSaveRaw32(BitConverter.SingleToInt32Bits(offsetY)),
                        new OriginalSaveRaw32(0));
                }
                else
                {
                    result[slot] = new OriginalSaveObjectIconRecord(
                        slot,
                        -1,
                        0,
                        -1,
                        -1,
                        new OriginalSaveRaw32(0),
                        new OriginalSaveRaw32(0),
                        new OriginalSaveRaw32(0));
                }
            }
            return result;
        }

        private static bool HasPermanentHitpointIcon(
            DigglesTclContext context,
            DigglesTclEntity entity)
        {
            if (entity.Owner != context.LocalPlayer || entity.NativeObjectType is not (1 or 11))
                return false;
            return !entity.VarsScope.Vars.TryGetValue("_selectable", out string selectable) ||
                selectable is "1" or "true";
        }

        private static int WorkIconIndex(DigglesTclEntity entity)
        {
            if (entity.NativeProductionSlots == null) return 0;
            for (int index = 0; index < entity.NativeProductionSlots.Count; index++)
            {
                if (string.Equals(
                        entity.NativeProductionSlots[index].ClassName,
                        entity.CurrentProdItem,
                        StringComparison.Ordinal))
                    return index;
            }
            return 0;
        }

        private static RuntimeEntityActionSaveStateEntry[] CaptureActions(
            DigglesTclContext context,
            IReadOnlyList<OriginalSaveCanonicalEntityState> entities)
        {
            var result = new List<RuntimeEntityActionSaveStateEntry>(entities.Count);
            foreach (OriginalSaveCanonicalEntityState canonical in entities)
            {
                if (!canonical.HasActionRow) continue;
                DigglesTclEntity entity = context.Entities[canonical.ObjectId];
                if (entity.ActiveAction == null)
                {
                    result.Add(new RuntimeEntityActionSaveStateEntry(entity.Handle));
                    continue;
                }
                if (entity.ActiveAction is not ISaveableEntityAction saveable)
                {
                    throw new NotSupportedException(
                        $"Active action '{entity.ActiveAction.GetType().Name}' on object " +
                        $"{entity.Handle} has no side-effect-free save capture.");
                }
                RuntimeEntityActionSaveState state = saveable.CaptureSaveState() ??
                    throw new InvalidDataException(
                        $"Active action on object {entity.Handle} returned no save state.");
                result.Add(new RuntimeEntityActionSaveStateEntry(
                    entity.Handle,
                    state,
                    entity.ActionFinish,
                    entity.ActionBreak));
            }
            return result.ToArray();
        }

        // Belt over the delete-time cleanup: an emitter whose owner is gone must never reach
        // the archive (the original never persists one, and its loader crashes on the dangling
        // reference). Losing a smoke puff is recoverable; losing the player's save is not —
        // filter with a loud warning instead of failing the save.
        private static ParticleSystemState.SourceEntry[] FilterOrphanParticleSources(
            ParticleSystemState.SourceEntry[] sources,
            DigglesTclContext context)
        {
            List<ParticleSystemState.SourceEntry>? kept = null;
            for (int index = 0; index < sources.Length; index++)
            {
                ParticleSystemState.SourceEntry entry = sources[index];
                bool orphan = !context.Entities.ContainsKey(entry.ObjectId);
                if (orphan)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[OriginalSave] dropping particle source {entry.SourceIndex} of dead " +
                        $"object {entry.ObjectId} from the capture (delete-time cleanup " +
                        "missed it — investigate).");
                    if (kept == null)
                    {
                        kept = new List<ParticleSystemState.SourceEntry>(sources.Length);
                        for (int copy = 0; copy < index; copy++) kept.Add(sources[copy]);
                    }
                    continue;
                }
                kept?.Add(entry);
            }
            return kept?.ToArray() ?? sources;
        }

        private static RuntimeEntityActionSaveStateEntry[] AlignActionsWithRetained(
            RuntimeEntityActionSaveStateEntry[] live,
            OriginalSaveRuntimeSnapshot? retainedRaw)
        {
            // The archive keeps action rows in its own native list order (not sorted by
            // owner). Owners that already appear in the retained sequence keep their archived
            // positions; rows born in the port session append after them in handle order.
            if (retainedRaw == null || live.Length == 0) return live;
            var liveByOwner = new Dictionary<int, RuntimeEntityActionSaveStateEntry>(live.Length);
            foreach (RuntimeEntityActionSaveStateEntry entry in live)
                liveByOwner.Add(entry.ObjectId, entry);
            var result = new List<RuntimeEntityActionSaveStateEntry>(live.Length);
            foreach (OriginalSaveRuntimeActionSnapshot retained in retainedRaw.Actions)
            {
                if (liveByOwner.Remove(retained.OwnerObjectId,
                        out RuntimeEntityActionSaveStateEntry entry))
                    result.Add(entry);
            }
            foreach (RuntimeEntityActionSaveStateEntry entry in live)
            {
                if (liveByOwner.ContainsKey(entry.ObjectId))
                    result.Add(entry);
            }
            return result.ToArray();
        }

        private static void ValidateCanonicalCapture(
            OriginalSaveCanonicalCapture capture,
            DigglesTclContext context)
        {
            if (!Finite(capture.GameTime))
                throw new InvalidDataException("The live game clock is non-finite.");
            if (capture.OwnerAttributes.Length != 9)
                throw new InvalidDataException("Canonical owner attributes require nine blocks.");
            for (int index = 0; index < capture.OwnerAttributes.Length; index++)
            {
                OriginalSaveCanonicalOwnerAttributeBlock owner = capture.OwnerAttributes[index];
                if (owner.Owner != index - 1)
                    throw new InvalidDataException("Canonical owner-attribute blocks are out of order.");
                if (owner.PrimaryValueBits.Length != 512 ||
                    owner.SecondaryCompatibilityBits.Length != 512)
                    throw new InvalidDataException("A canonical owner-attribute page is incomplete.");
            }
            if (capture.Entities.Length > 8192)
                throw new InvalidDataException("The live object graph exceeds 8192 native slots.");
            var objectIds = new HashSet<int>();
            int previousObjectId = -1;
            foreach (OriginalSaveCanonicalEntityState entity in capture.Entities)
            {
                if (entity.ObjectId < 2 || entity.ObjectId > 0x1FFF ||
                    entity.ObjectId <= previousObjectId || !objectIds.Add(entity.ObjectId) ||
                    string.IsNullOrEmpty(entity.ClassName) || entity.InstanceName == null ||
                    entity.AttributePageBits.Length != 512 ||
                    entity.EventQueue.Slots.Length != OriginalSaveObjectEventQueueDocument.Capacity ||
                    entity.TclVariables == null || entity.Inventory == null ||
                    entity.InventoryCapacity < 0 || entity.ContainedInObjectId == entity.ObjectId)
                    throw new InvalidDataException("A canonical object record is structurally invalid.");
                previousObjectId = entity.ObjectId;
                if (entity.ContainedInObjectId >= 0 &&
                    !context.Entities.ContainsKey(entity.ContainedInObjectId))
                {
                    throw new InvalidDataException(
                        $"Canonical object {entity.ObjectId} has a missing inventory owner.");
                }
                foreach (int inventoryObjectId in entity.Inventory)
                {
                    if (!context.Entities.ContainsKey(inventoryObjectId))
                        throw new InvalidDataException(
                            $"Canonical object {entity.ObjectId} contains a missing inventory object.");
                }
                foreach (OriginalSaveTclVariable variable in entity.TclVariables)
                {
                    if (variable.Name == null || variable.Value == null ||
                        variable.Name.IndexOf('\0') >= 0 || variable.Value.IndexOf('\0') >= 0)
                        throw new InvalidDataException(
                            $"Canonical object {entity.ObjectId} has an invalid Tcl variable.");
                }
                ValidateFamilyCapture(entity.ObjectId, entity.Family);
                ValidateTalkIssueCapture(entity.ObjectId, entity.TalkIssues);
                ValidatePlannerCapture(entity.ObjectId, entity.Planner);
                ValidateRingCursor(entity.EventQueue.ReadCursor, "read cursor");
                ValidateRingCursor(entity.EventQueue.WriteCursor, "write cursor");
                int pending = (entity.EventQueue.WriteCursor - entity.EventQueue.ReadCursor +
                    OriginalSaveObjectEventQueueDocument.Capacity) &
                    (OriginalSaveObjectEventQueueDocument.Capacity - 1);
                for (int index = 0; index < pending; index++)
                {
                    int slot = (entity.EventQueue.ReadCursor + index) &
                        (OriginalSaveObjectEventQueueDocument.Capacity - 1);
                    OriginalSaveTimerEventPayloadDocument? payload =
                        entity.EventQueue.Slots[slot];
                    if (payload == null)
                        throw new InvalidDataException(
                            $"Canonical object {entity.ObjectId} has an empty pending event slot.");
                    ValidateEventPayload(payload, entity.ObjectId, context);
                }
                foreach (OriginalSaveTimerEventPayloadDocument delayed in
                         entity.EventQueue.DelayedRecords)
                    ValidateEventPayload(delayed, entity.ObjectId, context);
                ValidateProductionCapture(entity.ObjectId, entity.Production);
            }
            if (capture.GlobalTimers.Length > 0x3000)
                throw new InvalidDataException("The live global timer queue exceeds 12,288 records.");
            foreach (OriginalSaveTimerRecord timer in capture.GlobalTimers)
            {
                // RepeatsLeft == 0 is legal (natively retained exhausted timers, see the
                // registry/scheduler validators).
                if (!Finite(timer.NextFire.AsDouble) || !Finite(timer.Interval.AsDouble) ||
                    timer.Interval.AsDouble < 0d ||
                    !context.Entities.ContainsKey(timer.OwnerObjectId))
                    throw new InvalidDataException("A canonical global timer is invalid.");
                ValidateEventPayload(timer.EventPayload, timer.OwnerObjectId, context);
            }
            if (capture.ObjectIcons.Length != OriginalSaveObjectIconDocument.SlotCount)
                throw new InvalidDataException("Canonical object icons require exactly 128 slots.");
            for (int slot = 0; slot < capture.ObjectIcons.Length; slot++)
            {
                OriginalSaveObjectIconRecord icon = capture.ObjectIcons[slot];
                if (icon.Slot != slot || !Finite(icon.OffsetX.AsSingle) ||
                    !Finite(icon.OffsetY.AsSingle) || !Finite(icon.OffsetZ.AsSingle) ||
                    (icon.IsActive && !context.Entities.ContainsKey(icon.ObjectId)) ||
                    icon.IconType is not (-1 or 0 or 5))
                {
                    throw new InvalidDataException("A canonical object-icon record is invalid.");
                }
            }
            ValidatePlayerCapture(capture.Players);
            ValidateStoryCapture(capture.Story, context);
            ValidateSelectionCapture(capture.SelectionControlGroups, context);
            ValidateSparetimeCapture(capture.Sparetime);
            ValidateHorizonCapture(capture.Horizon);
            ValidateAnimationCapture(capture.AnimationControllers, context);
            ValidateActionCapture(capture.Actions, capture.Entities, context);
            int previousClassLightSlot = -1;
            var classLightOwners = new HashSet<int>();
            foreach (OriginalSaveCanonicalClassLightState light in capture.ClassLights)
            {
                if (light.SlotIndex <= previousClassLightSlot || light.SlotIndex >= 2048 ||
                    !Finite(light.PositionX.AsSingle) || !Finite(light.PositionY.AsSingle) ||
                    !Finite(light.PositionZ.AsSingle) || !Finite(light.Radius.AsSingle) ||
                    light.Radius.AsSingle <= 0f || light.Red is < 0 or > 31 ||
                    light.Green is < 0 or > 31 || light.Blue is < 0 or > 31 ||
                    light.SourceToken < 0 ||
                    (light.IsObjectBound && !context.Entities.ContainsKey(light.SourceToken)) ||
                    (light.IsObjectBound && !classLightOwners.Add(light.SourceToken)))
                {
                    throw new InvalidDataException("A canonical class-light record is invalid.");
                }
                previousClassLightSlot = light.SlotIndex;
            }
            ProductionSlotOverrides.Validate(capture.ProductionSlotOverrides);
            EnergySystem.ValidateSaveState(capture.Energy, context.Entities);
            ParticleSystemState.Validate(capture.Particles);
            NewsTickerSystem.ValidateSaveState(capture.NewsTicker);
            LoreTrackSystem.ValidateRecords(capture.LoreTracks);
            ValidateWorldCapture(capture.Terrain, capture.FogOfWar);
            int worldWidth = capture.Terrain?.Width ?? 0;
            int worldHeight = capture.Terrain?.Height ?? 0;
            FluidSystem.ValidateSaveState(
                capture.Fluid,
                worldWidth,
                worldHeight,
                handle => context.Entities.ContainsKey(handle));
        }

        private static void ValidateWorldCapture(
            DiggableTerrain.SaveState? terrain,
            FogOfWarState.SaveState? fog)
        {
            if ((terrain == null) != (fog == null))
                throw new InvalidDataException(
                    "Canonical terrain and fog state must either both be present or both be absent.");
            if (terrain == null || fog == null) return;
            if (terrain.Width <= 0 || terrain.Height <= 0)
                throw new InvalidDataException("Canonical terrain dimensions are invalid.");
            int tileCount;
            try
            {
                tileCount = checked(terrain.Width * terrain.Height);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("Canonical terrain dimensions overflow.", exception);
            }
            if (terrain.BlockIdx == null || terrain.Zone == null || terrain.Terrain == null ||
                terrain.CellId == null || terrain.NativeTerrainFlags == null ||
                terrain.NavigationState == null || terrain.FloorAndExitState == null ||
                terrain.OriginalBlock == null || terrain.Dug == null ||
                terrain.DigState == null || terrain.DigId == null ||
                terrain.MarkX0 == null || terrain.MarkY0 == null ||
                terrain.MarkX1 == null || terrain.MarkY1 == null ||
                terrain.Voxels == null || terrain.HasTemplate == null ||
                terrain.BlockIdx.Length != tileCount || terrain.Zone.Length != tileCount ||
                terrain.Terrain.Length != tileCount || terrain.CellId.Length != tileCount ||
                terrain.NativeTerrainFlags.Length != tileCount ||
                terrain.NavigationState.Length != tileCount ||
                terrain.FloorAndExitState.Length != tileCount ||
                terrain.OriginalBlock.Length != tileCount || terrain.Dug.Length != tileCount ||
                terrain.DigState.Length != tileCount || terrain.DigId.Length != tileCount ||
                terrain.HasTemplate.Length != tileCount || terrain.MarkX0.Length != 8 ||
                terrain.MarkY0.Length != 8 || terrain.MarkX1.Length != 8 ||
                terrain.MarkY1.Length != 8)
            {
                throw new InvalidDataException("Canonical terrain state is incomplete.");
            }
            ValidateFogState(fog, terrain.Width, terrain.Height, tileCount);
        }

        private static void ValidatePlayerCapture(OriginalSaveCanonicalPlayerState players)
        {
            if ((uint)players.LocalPlayer >= 8 || players.ReservedFraming.Length != 32 ||
                players.OwnerRace.Length != 8 || players.Slots.Length != 8 ||
                players.Diplomacy.GetLength(0) != 8 || players.Diplomacy.GetLength(1) != 8)
            {
                throw new InvalidDataException("Canonical player state has invalid fixed dimensions.");
            }
            for (int owner = 0; owner < players.Slots.Length; owner++)
            {
                OriginalSaveCanonicalPlayerSlot slot = players.Slots[owner];
                if (slot == null || slot.PlayerName == null || slot.SecondaryName == null ||
                    slot.SlotState is < 0 or > 2 || (uint)slot.PlayerOwnerId >= 8 ||
                    (uint)slot.ClanOwnerId >= 8)
                {
                    throw new InvalidDataException($"Canonical player slot {owner} is invalid.");
                }
                for (int target = 0; target < 8; target++)
                {
                    int relation = players.Diplomacy[owner, target];
                    if (relation is < 0 or > 4 || (owner == target && relation != 0))
                    {
                        throw new InvalidDataException(
                            $"Canonical diplomacy relation {owner}->{target} is invalid.");
                    }
                }
            }
        }

        private static void ValidateStoryCapture(
            OriginalSaveCanonicalStoryState story,
            DigglesTclContext context)
        {
            if (story.MapWidth < 0 || story.MapHeight < 0 || story.DigWeight < 0 ||
                story.DigThreshold < 0 || story.ZoneFloor < 0 ||
                story.ZoneFloor > story.Zones.Length ||
                story.LatchDirection is not (0 or 1 or 2 or 4 or 8))
            {
                throw new InvalidDataException("Canonical story state has invalid native bounds.");
            }
            var eventNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> storyEvent in story.Events)
            {
                if (string.IsNullOrEmpty(storyEvent.Key) || storyEvent.Value == null ||
                    !eventNames.Add(storyEvent.Key))
                    throw new InvalidDataException("Canonical story events are invalid or duplicated.");
            }
            if (story.EventOrder.Length != eventNames.Count ||
                story.EventTriggers.Length != eventNames.Count)
                throw new InvalidDataException("Canonical story event tables disagree.");
            foreach (string eventName in story.EventOrder)
                if (!eventNames.Contains(eventName))
                    throw new InvalidDataException("Canonical story event order is invalid.");
            foreach (OriginalSaveCanonicalStoryTrigger trigger in story.EventTriggers)
            {
                if (!eventNames.Contains(trigger.Name) || trigger.Trigger == null ||
                    trigger.Extra == null)
                    throw new InvalidDataException("Canonical story trigger state is invalid.");
            }
            foreach (OriginalSaveCanonicalStoryMapEntry entry in story.Map)
            {
                if ((uint)entry.X >= (uint)story.MapWidth ||
                    (uint)entry.Y >= (uint)story.MapHeight || entry.Value is < 0 or > 2)
                    throw new InvalidDataException("Canonical story map contains an invalid cell.");
            }
            var zoneNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (OriginalSaveCanonicalStoryZone zone in story.Zones)
            {
                if (string.IsNullOrEmpty(zone.Name) || !zoneNames.Add(zone.Name))
                    throw new InvalidDataException("Canonical story zones are invalid or duplicated.");
            }
            var tempNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (OriginalSaveCanonicalStoryTemp temp in story.Temps)
            {
                if (temp == null || string.IsNullOrEmpty(temp.Name) ||
                    !tempNames.Add(temp.Name) || temp.Zone < -1 || temp.Zone >= story.Zones.Length ||
                    !Finite(temp.MinimumDistance) || !Finite(temp.MiddleDistance) ||
                    !Finite(temp.NonRelevant) || temp.EventNames == null || temp.SeenChildren == null)
                {
                    throw new InvalidDataException(
                        "Canonical story templates are invalid or duplicated.");
                }
                foreach (string eventName in temp.EventNames)
                    if (!eventNames.Contains(eventName))
                        throw new InvalidDataException(
                            $"Canonical story template '{temp.Name}' references an unknown event.");
            }
            foreach (KeyValuePair<int, string> callback in story.Callbacks)
            {
                if (callback.Value == null ||
                    (callback.Key >= 0 && !context.Entities.ContainsKey(callback.Key)))
                    throw new InvalidDataException("Canonical story callback state is invalid.");
            }
        }

        private static void ValidateSelectionCapture(
            IReadOnlyList<int[]> groups,
            DigglesTclContext context)
        {
            if (groups.Count != OriginalSaveSelectionControlGroupDocument.GroupCount)
                throw new InvalidDataException("Canonical selection state requires nine groups.");
            for (int group = 0; group < groups.Count; group++)
            {
                int[] members = groups[group] ??
                    throw new InvalidDataException("A canonical selection group is missing.");
                var unique = new HashSet<int>();
                foreach (int objectId in members)
                {
                    if (!context.Entities.ContainsKey(objectId) || !unique.Add(objectId))
                        throw new InvalidDataException(
                            $"Canonical selection group {group + 1} is invalid.");
                }
            }
        }

        private static void ValidateSparetimeCapture(
            IReadOnlyList<SparetimeRegistration> registrations)
        {
            var objects = new HashSet<int>();
            foreach (SparetimeRegistration registration in registrations)
            {
                if ((uint)registration.Category > (uint)SparetimeCategory.Ill ||
                    !objects.Add(registration.ObjectId))
                {
                    throw new InvalidDataException(
                        "Canonical sparetime registrations are invalid or duplicated.");
                }
            }
        }

        private static void ValidateHorizonCapture(IReadOnlyList<HorizonRegionState> regions)
        {
            foreach (HorizonRegionState region in regions)
            {
                if (!Finite(region.Position) || !Finite(region.Depth) || !Finite(region.Size) ||
                    (uint)region.Red > byte.MaxValue || (uint)region.Green > byte.MaxValue ||
                    (uint)region.Blue > byte.MaxValue)
                {
                    throw new InvalidDataException("A canonical horizon region is invalid.");
                }
            }
        }

        private static void ValidateAnimationCapture(
            IReadOnlyList<OriginalSaveCanonicalAnimationState> animations,
            DigglesTclContext context)
        {
            int previousObjectId = int.MinValue;
            foreach (OriginalSaveCanonicalAnimationState animation in animations)
            {
                if (animation == null || animation.ObjectId <= previousObjectId ||
                    animation.ClipName == null || animation.FrameCount < 0 ||
                    animation.ModelDatabaseSlot is < -1 or > 255 ||
                    (!animation.IsRetainedOrphan &&
                     (!context.Entities.ContainsKey(animation.ObjectId) ||
                      animation.Mode is < 0 or > 2 || animation.CurrentFrame < 0 ||
                      !Finite(animation.StartTime))))
                {
                    throw new InvalidDataException(
                        "A canonical animation-controller record is invalid.");
                }
                previousObjectId = animation.ObjectId;
            }
        }

        private static void ValidateActionCapture(
            IReadOnlyList<RuntimeEntityActionSaveStateEntry> actions,
            IReadOnlyList<OriginalSaveCanonicalEntityState> entities,
            DigglesTclContext context)
        {
            var rowOwners = new HashSet<int>();
            var tclOwners = new HashSet<int>();
            foreach (OriginalSaveCanonicalEntityState entity in entities)
            {
                if (entity.HasActionRow) rowOwners.Add(entity.ObjectId);
                if (entity.OwnsTclState) tclOwners.Add(entity.ObjectId);
            }
            if (!rowOwners.IsSubsetOf(tclOwners))
                throw new InvalidDataException(
                    "Action-row owners must be a subset of the Tcl-state owners (native law).");
            var owners = new HashSet<int>();
            foreach (RuntimeEntityActionSaveStateEntry action in actions)
            {
                if (!context.Entities.ContainsKey(action.ObjectId) ||
                    !rowOwners.Contains(action.ObjectId) || !owners.Add(action.ObjectId))
                    throw new InvalidDataException("A canonical action record is invalid.");
            }
            if (!owners.SetEquals(rowOwners))
                throw new InvalidDataException(
                    "Canonical action rows do not match the action-participant set.");
        }

        private static AiSystem.SaveState AlignAiWithRetained(
            AiSystem.SaveState live,
            OriginalSaveRuntimeSnapshot? retainedRaw)
        {
            // The archive serializes each AI owner's Tcl dictionary in a fixed sequence, while the
            // live scope enumerates in insertion order (policy defaults first, saved overlays
            // after). An unchanged owner scope emits the archived sequence verbatim.
            if (retainedRaw == null) return live;
            for (int owner = 0; owner < live.Owners.Length &&
                 owner < retainedRaw.Ai.Owners.Count; owner++)
            {
                AiSystem.OwnerSaveState liveOwner = live.Owners[owner];
                OriginalSaveRuntimeAiOwnerSnapshot retained = retainedRaw.Ai.Owners[owner];
                if (liveOwner.VariableNames.Length != retained.Variables.Count) continue;
                var livePairs = new Dictionary<string, string>(
                    liveOwner.VariableNames.Length, StringComparer.Ordinal);
                bool duplicate = false;
                for (int i = 0; i < liveOwner.VariableNames.Length; i++)
                {
                    if (!livePairs.TryAdd(liveOwner.VariableNames[i], liveOwner.VariableValues[i]))
                    { duplicate = true; break; }
                }
                if (duplicate) continue;
                bool unchanged = true;
                foreach (OriginalSaveTclVariable variable in retained.Variables)
                {
                    if (!livePairs.TryGetValue(variable.Name, out string? value) ||
                        !string.Equals(value, variable.Value, StringComparison.Ordinal))
                    { unchanged = false; break; }
                }
                if (!unchanged) continue;
                var names = new string[retained.Variables.Count];
                var values = new string[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = retained.Variables[i].Name;
                    values[i] = retained.Variables[i].Value;
                }
                liveOwner.VariableNames = names;
                liveOwner.VariableValues = values;
            }
            return live;
        }

        private static void ValidateEventPayload(
            OriginalSaveTimerEventPayloadDocument payload,
            int ownerObjectId,
            DigglesTclContext context)
        {
            // Subjects are emitted verbatim; real saves carry non -1 subjects, e.g. the
            // Recycling-mod event 'evt_task_recyclebuilding'. Event names outside the vanilla boot
            // registry are tolerated: modded saves carry mod def_event names, and the unmodded
            // original keeps such events inert (no handler) rather than rejecting the archive.
            if (payload.OwnerObjectId != ownerObjectId)
            {
                throw new InvalidDataException(
                    $"Canonical event '{payload.EventName}' violates its owner contract.");
            }
            if (!context.RegisteredEventNames.Contains(payload.EventName))
            {
                UnityEngine.Debug.LogWarning(
                    $"Canonical event '{payload.EventName}' is not in the vanilla def_event " +
                    "registry — likely a mod-defined event. Emitting verbatim; the event stays " +
                    "inert without a handler, matching the unmodded original.");
            }
        }

        private static void ValidateProductionCapture(
            int objectId,
            OriginalSaveCanonicalProductionState? production)
        {
            if (production == null) return;
            if (production.WorkState is < 0 or > 6 || production.RequestBytes.Length != 7 ||
                production.RequestBytes[0] != production.RequestBytes[6] ||
                production.PendingPositionBits.Length != 3 ||
                production.FlowCompatibilityBytes.Length != 8 ||
                production.ProductionValues.Length != 4 || production.EnergyValues.Length != 5 ||
                production.GuestSeats.Length != DigglesTclEntity.GuestSeatCount)
            {
                throw new InvalidDataException(
                    $"Canonical production record for object {objectId} is structurally invalid.");
            }
            if (production.ProductionEnabled > 1)
                throw new InvalidDataException($"Object {objectId} has an invalid production-enabled byte.");
            if (production.RequestBytes[0] > 1 || production.RequestBytes[1] > 1 ||
                production.RequestBytes[2] > 1)
            {
                throw new InvalidDataException(
                    $"Object {objectId} has an invalid production request byte.");
            }
            foreach (OriginalSaveCanonicalProductionSlotState slot in production.Slots)
            {
                if (slot == null || string.IsNullOrEmpty(slot.ClassName) ||
                    slot.ResourceName == null || slot.OrderCount is < 0 or > 10)
                {
                    throw new InvalidDataException(
                        $"Object {objectId} has an invalid production slot.");
                }
            }
            for (int index = 0; index < production.PendingPositionBits.Length; index++)
            {
                if (!Finite(BitConverter.Int32BitsToSingle(production.PendingPositionBits[index])))
                    throw new InvalidDataException($"Object {objectId} has a non-finite order position.");
            }
            for (int index = 1; index < production.EnergyValues.Length; index++)
            {
                if (!Finite(BitConverter.Int32BitsToSingle(production.EnergyValues[index])))
                    throw new InvalidDataException($"Object {objectId} has non-finite energy state.");
            }
        }

        private static void ValidateFamilyCapture(
            int objectId,
            OriginalSaveCanonicalFamilyState? family)
        {
            if (family == null) return;
            if (family.PartnerState is < 0 or > 2 || family.MotherState is < 0 or > 2 ||
                family.FatherState is < 0 or > 2 || family.PartnerReferenceObjectId < -1 ||
                family.MotherReferenceObjectId < -1 || family.FatherReferenceObjectId < -1)
            {
                throw new InvalidDataException($"Object {objectId} has invalid family state.");
            }
            foreach (string child in family.ChildrenNames)
            {
                if (child == null || child.IndexOf('\0') >= 0)
                    throw new InvalidDataException(
                        $"Object {objectId} has an invalid family child name.");
            }
        }

        private static void ValidateTalkIssueCapture(
            int objectId,
            OriginalSaveCanonicalTalkIssueState? talkIssues)
        {
            if (talkIssues == null) return;
            if (!Finite(talkIssues.StoredSum))
                throw new InvalidDataException($"Object {objectId} has a non-finite talk-issue sum.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> issue in talkIssues.Entries)
            {
                if (string.IsNullOrEmpty(issue.Key) || !Finite(issue.Value) ||
                    !names.Add(issue.Key))
                {
                    throw new InvalidDataException(
                        $"Object {objectId} has invalid or duplicate talk-issue state.");
                }
            }
        }

        private static void ValidatePlannerCapture(
            int objectId,
            OriginalSaveCanonicalPlannerState? planner)
        {
            if (planner == null) return;
            if (planner.IdleAnnounceFlag is not (0 or 1) || !Finite(planner.IdleSince) ||
                planner.CurrentWorkplaceObjectId < -1 || planner.AssignedWorkplaceObjectId < -1)
            {
                throw new InvalidDataException($"Object {objectId} has invalid planner state.");
            }
        }

        static void ValidateEntity(
            DigglesTclEntity entity,
            DigglesTclContext context,
            OriginalSaveRuntimeEntitySnapshot? retainedRaw)
        {
            bool registeredClass = entity.Class != null &&
                !string.IsNullOrEmpty(entity.Class.Name) &&
                context.Classes.TryGetValue(entity.Class.Name, out DigglesClass? registered) &&
                ReferenceEquals(registered, entity.Class);
            bool headlessRetainedClass = context.Classes.Count == 0 && entity.Class != null &&
                retainedRaw != null &&
                string.Equals(retainedRaw.ClassName, entity.Class.Name, StringComparison.Ordinal);
            if (!registeredClass && !headlessRetainedClass)
            {
                throw new InvalidDataException(
                    $"Runtime entity {entity.Handle} does not use a boot-registered, placeholder, " +
                    "or retained baseline class.");
            }
            if (!Finite(entity.Position) || !Finite(entity.Velocity) || !Finite(entity.Rotation) ||
                !Finite(entity.WorkStart) || !Finite(entity.WorkLen) ||
                !Finite(entity.FowLastX) || !Finite(entity.FowLastY))
                throw new InvalidDataException($"Runtime entity {entity.Handle} contains non-finite native state.");
            if (entity.NativeObjectType is < -1 or > 12)
                throw new InvalidDataException(
                    $"Runtime entity {entity.Handle} has invalid native type {entity.NativeObjectType}.");
            if (entity.Owner < -1 || entity.Owner > 7)
                throw new InvalidDataException(
                    $"Runtime entity {entity.Handle} has invalid player owner {entity.Owner}.");
            if (entity.WalkResult is < 0 or > 5 || entity.WeaponClassId < 0 ||
                entity.ShieldClassId < 0 || entity.PickTargetMode is < 0 or > 2 ||
                DigglesTclEngine.EffectiveInvCapacity(context, entity) < 0)
            {
                throw new InvalidDataException(
                    $"Runtime entity {entity.Handle} has invalid core native scalar state.");
            }
            if (entity.LinkedToHandle > 0 && !context.Entities.ContainsKey(entity.LinkedToHandle))
                throw new InvalidDataException(
                    $"Runtime entity {entity.Handle} links to missing entity {entity.LinkedToHandle}.");
            if (entity.InstanceName == null)
                throw new InvalidDataException($"Runtime entity {entity.Handle} has no instance name.");
        }

        static OriginalSaveVector3 NativeVector(Vector3 value)
        {
            return new OriginalSaveVector3(value.x, value.y, value.z);
        }

        static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
