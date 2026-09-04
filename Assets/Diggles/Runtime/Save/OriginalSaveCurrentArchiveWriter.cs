#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine.Scripting;

    public interface IOriginalSaveCurrentArchiveWriter
    {
        OriginalSaveArchiveDocument Rebuild(OriginalSaveArchiveDocument source);
        OriginalSaveArchiveDocument Rebuild(
            OriginalSaveArchiveDocument source,
            OriginalSaveCurrentArchivePatch patch);
        OriginalSaveArchiveDocument Assemble(
            OriginalSaveCanonicalCapture capture,
            byte[] fixedEngineState,
            int lightBegin);
    }

    public sealed class OriginalSaveCurrentArchiveWriter : IOriginalSaveCurrentArchiveWriter
    {
        private static readonly Encoding Windows1252 = Encoding.GetEncoding(1252);

        private readonly OriginalSaveArchiveReader _reader;
        private readonly OriginalSaveStructureBuilder _structureBuilder;

        [Preserve]
        public OriginalSaveCurrentArchiveWriter(OriginalSaveArchiveReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _structureBuilder = new OriginalSaveStructureBuilder();
        }

        public OriginalSaveArchiveDocument Rebuild(OriginalSaveArchiveDocument source)
        {
            return Rebuild(source, new OriginalSaveCurrentArchivePatch());
        }

        public OriginalSaveArchiveDocument Rebuild(
            OriginalSaveArchiveDocument source,
            OriginalSaveCurrentArchivePatch patch)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (patch == null) throw new ArgumentNullException(nameof(patch));
            if (source.Metadata.Version != OriginalSaveArchiveReader.CurrentVersion)
            {
                throw new NotSupportedException(
                    $"Typed archive writing is not implemented for original save version 0x{source.Metadata.Version:X}.");
            }

            OriginalSaveObjectManifest manifest = source.Metadata.Objects
                ?? throw new InvalidOperationException("The current save has no object manifest.");
            OriginalSaveObjectCollection objects = source.Objects
                ?? throw new InvalidOperationException("The current save has no typed object collection.");
            byte[] sourcePayload = source.CopyPayload();
            ApplyHeaderPatch(
                sourcePayload,
                source,
                patch.GameTime ??
                (patch.RebuildStructureSections ? patch.CanonicalCapture?.GameTime : null));
            foreach (int objectId in patch.ObjectIds)
            {
                if (objects.FindById(objectId) == null)
                    throw new InvalidDataException($"Original object patch references undeclared ID {objectId}.");
            }

            if (patch.RebuildStructureSections)
            {
                OriginalSaveCanonicalCapture capture = patch.CanonicalCapture ??
                    throw new InvalidOperationException(
                        "Canonical structure rebuilding requires a D1 capture.");
                return RebuildStructureSections(
                    source,
                    sourcePayload,
                    manifest,
                    objects,
                    patch,
                    capture);
            }

            using var output = new MemoryStream(sourcePayload.Length);
            using (var writer = new BinaryWriter(output, Windows1252, true))
            {
                WritePrefix(writer, sourcePayload, source, manifest, patch.World);
                foreach (OriginalSaveObjectDocument obj in objects)
                {
                    writer.Write(obj.ObjectId);
                    if (!patch.TryGetObject(obj.ObjectId, out OriginalSaveObjectCorePatch? objectPatch))
                    {
                        int bodyOffset = checked((int)obj.Offset);
                        output.Write(sourcePayload, bodyOffset, checked((int)obj.Size));
                        continue;
                    }
                    WriteCommonObjectBody(writer, obj, objectPatch);

                    int tailOffset = checked((int)obj.OpaqueTailOffset);
                    int bodyEnd = checked((int)(obj.Offset + obj.Size));
                    output.Write(sourcePayload, tailOffset, checked(bodyEnd - tailOffset));
                }
                writer.Write(-1);
            }

            int suffixOffset = checked((int)manifest.BodyStreamEndOffset);
            output.Write(sourcePayload, suffixOffset, sourcePayload.Length - suffixOffset);
            byte[] rebuiltPayload = output.ToArray();

            using var validationStream = new MemoryStream(rebuiltPayload, false);
            OriginalSaveArchiveMetadata metadata = _reader.ReadDecompressedMetadata(validationStream);
            return new OriginalSaveArchiveDocument(rebuiltPayload, metadata);
        }

        public OriginalSaveArchiveDocument Assemble(
            OriginalSaveCanonicalCapture capture,
            byte[] fixedEngineState,
            int lightBegin)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            if (fixedEngineState == null)
                throw new ArgumentNullException(nameof(fixedEngineState));
            if (fixedEngineState.Length != OriginalSaveFixedEngineStateSynthesizer.BlockLength)
            {
                throw new InvalidDataException(
                    $"The fixed-engine block is {fixedEngineState.Length} bytes; expected " +
                    $"{OriginalSaveFixedEngineStateSynthesizer.BlockLength}.");
            }
            if (!Finite(capture.GameTime) || capture.GameTime < 0d)
                throw new InvalidDataException("The native game clock is invalid.");

            DiggableTerrain.SaveState terrain = capture.Terrain ??
                throw new InvalidDataException("The canonical capture has no terrain state.");
            FogOfWarState.SaveState fogOfWar = capture.FogOfWar ??
                throw new InvalidDataException("The canonical capture has no fog-of-war state.");
            ValidateCanonicalWorld(terrain, fogOfWar, capture.Fluid);

            using var output = new MemoryStream();
            using (var writer = new BinaryWriter(output, Windows1252, true))
            {
                writer.Write(OriginalSaveArchiveReader.CurrentVersion);
                WriteNativeString(writer, string.Empty);
                writer.Write(capture.GameTime);
                writer.Write(1);
                writer.Write(fixedEngineState);
                writer.Write(2);
                writer.Write(3);

                WriteCanonicalWorld(writer, capture, terrain, fogOfWar, lightBegin);
                writer.Write(4);
                writer.Write(5);
                writer.Write(6);
                _structureBuilder.WriteDeclarations(writer, null, capture.Entities);
                _structureBuilder.WriteSupportRecords(writer, capture.GlobalTimers);
                WriteCanonicalObjectBodies(writer, capture);
                _structureBuilder.WriteObjectManagerTail(
                    writer,
                    capture.OwnerAttributes,
                    capture.Entities);
                _structureBuilder.WriteEmptyClassTable(writer);
                _structureBuilder.WriteEmptySpatialIndex(writer, terrain.Width, terrain.Height);
                writer.Write(7);

                WriteClassLights(writer, capture.ClassLights);
                writer.Write(-1);
                writer.Write(8);
                WriteEnergy(writer, capture.Energy);
                writer.Write(0x50);

                WriteAnimationControllers(writer, capture);
                writer.Write(-1);
                writer.Write(9);
                WriteParticles(writer, capture);
                writer.Write(-1);
                writer.Write(10);

                WriteStory(writer, capture.Story);
                writer.Write(11);
                WriteSelectionControlGroups(writer, capture.SelectionControlGroups);
                writer.Write(12);
                _structureBuilder.WriteActionTable(writer, null, capture);
                writer.Write(13);
                WriteSparetime(writer, capture.Sparetime);
                writer.Write(14);
                WriteTriggers(writer, capture.Triggers);
                writer.Write(15);
                WriteHorizon(writer, capture.Horizon);
                writer.Write(16);
                WriteAdaptiveSoundMarkers(writer, capture.AdaptiveSoundMarkers);
                writer.Write(17);
                WriteNewsTicker(writer, capture.NewsTicker);
                WriteObjectIcons(writer, capture.ObjectIcons);
                WriteCaveRecords(writer, capture.CaveRecords);
                WriteAi(writer, capture.Ai);
                WriteElf(writer, capture.Elf);
                WriteProductionSlotOverrides(writer, capture);
                WritePlayers(writer, capture.Players);
                writer.Write(18);
                writer.Write(new byte[0x1000]);
            }

            byte[] payload = output.ToArray();
            using var validationStream = new MemoryStream(payload, false);
            OriginalSaveArchiveMetadata metadata = _reader.ReadDecompressedMetadata(validationStream);
            return new OriginalSaveArchiveDocument(payload, metadata);
        }

        private OriginalSaveArchiveDocument RebuildStructureSections(
            OriginalSaveArchiveDocument source,
            byte[] sourcePayload,
            OriginalSaveObjectManifest manifest,
            OriginalSaveObjectCollection objects,
            OriginalSaveCurrentArchivePatch patch,
            OriginalSaveCanonicalCapture capture)
        {
            OriginalSaveLogicMetadata logic = source.Metadata.Logic ??
                throw new InvalidOperationException(
                    "The current save has no action-table metadata.");
            OriginalSaveActionTableDocument actions = source.Actions ??
                throw new InvalidOperationException("The current save has no typed action table.");
            bool objectGraphChanged = ObjectGraphChanged(objects, capture.Entities);

            using var output = new MemoryStream(sourcePayload.Length);
            using (var writer = new BinaryWriter(output, Windows1252, true))
            {
                WritePrefixToDeclarations(
                    writer,
                    sourcePayload,
                    source,
                    manifest,
                    patch.World);
                _structureBuilder.WriteDeclarations(writer, manifest, capture.Entities);
                _structureBuilder.WriteSupportRecords(writer, capture.GlobalTimers);

                for (int index = 0; index < capture.Entities.Length; index++)
                {
                    OriginalSaveCanonicalEntityState live = capture.Entities[index] ??
                        throw new InvalidDataException("A canonical object registry slot is null.");
                    patch.TryGetObject(live.ObjectId, out OriginalSaveObjectCorePatch? objectPatch);
                    OriginalSaveObjectDocument? sourceObject = objects.FindById(live.ObjectId);
                    byte[] rebuiltBody = sourceObject != null
                        ? _structureBuilder.BuildObjectBody(sourceObject, live, objectPatch)
                        : _structureBuilder.BuildObjectBody(live, capture.GameTime);

                    writer.Write(live.ObjectId);
                    if (sourceObject != null)
                    {
                        int sourceBodyOffset = checked((int)sourceObject.Offset);
                        int sourceBodyLength = checked((int)sourceObject.Size);
                        if (MatchesSourceBody(
                            sourcePayload,
                            sourceBodyOffset,
                            sourceBodyLength,
                            rebuiltBody))
                        {
                            output.Write(sourcePayload, sourceBodyOffset, sourceBodyLength);
                            continue;
                        }
                    }
                    writer.Write(rebuiltBody);
                }
                writer.Write(-1);

                _structureBuilder.WriteObjectManagerTail(
                    writer,
                    capture.OwnerAttributes,
                    capture.Entities);
                int retainedActionOffset = checked((int)logic.ActionSectionOffset);
                if (!objectGraphChanged)
                {
                    OriginalSaveRetainedStructureSections retainedStructures =
                        _structureBuilder.CaptureRetainedStructures(source);
                    writer.Write(retainedStructures.ClassTable);
                    writer.Write(retainedStructures.SpatialIndex);
                }
                else
                {
                    // Class table: empty self-heals — the native LOD pass rebuilds the render
                    // proxies from the live objects we do serialize. The spatial index does not
                    // self-heal (dormant seeds have no live counterpart; the only native producer
                    // FUN_005cd1d0 converts live display instances), so re-emit the surviving
                    // seeds instead of dropping the deco.
                    _structureBuilder.WriteEmptyClassTable(writer);
                    _structureBuilder.WriteSpatialIndexFromSeeds(
                        writer,
                        capture.DecoSeeds,
                        source.World.Width,
                        source.World.Height);
                }
                int marker7Offset = checked((int)manifest.Marker7Offset);
                output.Write(
                    sourcePayload,
                    marker7Offset,
                    checked(retainedActionOffset - marker7Offset));
                _structureBuilder.WriteActionTable(writer, actions, capture);
            }

            int suffixOffset = checked((int)logic.Marker13Offset);
            output.Write(sourcePayload, suffixOffset, sourcePayload.Length - suffixOffset);
            byte[] rebuiltPayload = output.ToArray();
            using var validationStream = new MemoryStream(rebuiltPayload, false);
            OriginalSaveArchiveMetadata metadata = _reader.ReadDecompressedMetadata(validationStream);
            return new OriginalSaveArchiveDocument(rebuiltPayload, metadata);
        }

        private static void ValidateCanonicalWorld(
            DiggableTerrain.SaveState terrain,
            FogOfWarState.SaveState fogOfWar,
            FluidSystem.SaveState fluid)
        {
            if (fluid == null) throw new InvalidDataException("The canonical fluid state is missing.");
            if (terrain.Width <= 0 || terrain.Height <= 0 ||
                fogOfWar.Width != terrain.Width || fogOfWar.Height != terrain.Height)
            {
                throw new InvalidDataException("Canonical world dimensions are invalid.");
            }

            int tileCount;
            try
            {
                tileCount = checked(terrain.Width * terrain.Height);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("Canonical world dimensions overflow.", exception);
            }

            ValidateCount(terrain.BlockIdx, tileCount, "terrain block page");
            ValidateCount(terrain.Zone, tileCount, "terrain navigation page");
            ValidateCount(terrain.Terrain, tileCount, "terrain floor page");
            ValidateCount(terrain.CellId, tileCount, "terrain cell-id page");
            ValidateCount(terrain.NativeTerrainFlags, tileCount, "terrain flag page");
            ValidateCount(terrain.NavigationState, tileCount, "terrain navigation-state page");
            ValidateCount(terrain.FloorAndExitState, tileCount, "terrain packed exit-pair/flags page");
            ValidateCount(terrain.DigState, tileCount, "terrain dig-state page");
            ValidateCount(terrain.DigId, tileCount, "terrain dig-id page");
            ValidateCount(terrain.HasTemplate, tileCount, "terrain template page");
            ValidateCount(
                fogOfWar.Fog,
                checked(tileCount * FogOfWarState.Players),
                "fog tile page");
            ValidateCount(fogOfWar.FowBegin, FogOfWarState.Players, "fog begin rows");
            ValidateCount(
                fogOfWar.FadeOffsets,
                FogOfWarState.Players + 1,
                "fog fade offsets");
            ValidateCount(
                fogOfWar.ExploredRect,
                FogOfWarState.Players * 4,
                "fog explored rectangles");
            if (fogOfWar.FadeItems == null || fogOfWar.FadeOffsets[0] != 0 ||
                fogOfWar.FadeOffsets[^1] != fogOfWar.FadeItems.Length ||
                fluid.Cells == null || fluid.Sources == null || fluid.Stoppers == null)
            {
                throw new InvalidDataException("Canonical world sparse state is invalid.");
            }
        }

        private static void WriteCanonicalWorld(
            BinaryWriter writer,
            OriginalSaveCanonicalCapture capture,
            DiggableTerrain.SaveState terrain,
            FogOfWarState.SaveState fogOfWar,
            int lightBegin)
        {
            int tileCount = checked(terrain.Width * terrain.Height);
            var fluidCells = new Dictionary<int, FluidSystem.CellSaveState>(
                capture.Fluid.Cells.Length);
            foreach (FluidSystem.CellSaveState cell in capture.Fluid.Cells)
            {
                if ((uint)cell.X >= (uint)terrain.Width ||
                    (uint)cell.Y >= (uint)terrain.Height ||
                    cell.TypeMask is < 0 or > 7 || !Finite(cell.Fill) || cell.Fill < 0f)
                {
                    throw new InvalidDataException("A canonical fluid cell is invalid.");
                }
                int tileIndex = checked(cell.Y * terrain.Width + cell.X);
                if (!fluidCells.TryAdd(tileIndex, cell))
                    throw new InvalidDataException("A canonical fluid cell is duplicated.");
            }

            writer.Write(terrain.Width);
            writer.Write(terrain.Height);
            writer.Write(0);
            writer.Write(0);
            writer.Write(terrain.Width);
            writer.Write(terrain.Height);
            for (int tileIndex = 0; tileIndex < tileCount; tileIndex++)
            {
                byte fluidType = 0;
                float fluidFill = 0f;
                if (fluidCells.TryGetValue(tileIndex, out FluidSystem.CellSaveState cell))
                {
                    fluidType = checked((byte)cell.TypeMask);
                    fluidFill = cell.Fill;
                }

                writer.Write(terrain.BlockIdx[tileIndex]);
                // Native terrain record laws (writer FUN_00592f90, loader
                // FUN_005931f0:246-249): +4 carries tile+0x06, +13 carries raw tile+0x12
                // (packed subX bits0-2, subY bits3-5, flags bits6-7), and
                // +17 low16 carries material, authored force-wall bit 7, and appearance noise.
                // The +0x12 byte is written verbatim; splitting it into a scalar exit loses subY.
                writer.Write(terrain.NavigationState[tileIndex]);
                writer.Write(terrain.DigState[tileIndex]);
                writer.Write(terrain.DigId[tileIndex]);
                writer.Write(terrain.FloorAndExitState[tileIndex]);
                byte templateFlags = terrain.HasTemplate[tileIndex] ? (byte)1 : (byte)0;
                writer.Write((byte)(templateFlags & ~4));
                writer.Write(terrain.CellId[tileIndex]);
                uint terrainState = terrain.NativeTerrainFlags[tileIndex];
                terrainState |= (uint)fluidType << 16;
                terrainState |= (uint)fluidType << 24;
                writer.Write(terrainState);
                writer.Write(fluidFill);
                writer.Write(fluidFill);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write(
                    fogOfWar.Fog,
                    tileIndex * FogOfWarState.Players,
                    FogOfWarState.Players);
            }

            for (int player = 0; player < FogOfWarState.Players; player++)
            {
                int row = fogOfWar.FowBegin[player];
                if (row < -1 || row >= terrain.Height)
                {
                    throw new InvalidDataException(
                        $"Canonical fog begin row {row} is invalid for player {player}.");
                }
                writer.Write(row);
            }
            writer.Write(lightBegin);
            writer.Write(capture.GroundBeginRow);

            WriteCanonicalFluid(writer, capture);
            for (int player = 0; player < FogOfWarState.Players; player++)
            {
                int start = fogOfWar.FadeOffsets[player];
                int end = fogOfWar.FadeOffsets[player + 1];
                if (start < 0 || end < start || end > fogOfWar.FadeItems.Length)
                    throw new InvalidDataException("Canonical fog fade offsets are invalid.");
                writer.Write(end - start);
                for (int itemIndex = start; itemIndex < end; itemIndex++)
                {
                    int packedTile = fogOfWar.FadeItems[itemIndex];
                    if ((uint)packedTile >= (uint)tileCount)
                        throw new InvalidDataException("A canonical fog fade tile is invalid.");
                    writer.Write(packedTile % terrain.Width);
                    writer.Write(packedTile / terrain.Width);
                }
            }
        }

        private static void WriteCanonicalFluid(
            BinaryWriter writer,
            OriginalSaveCanonicalCapture capture)
        {
            FluidSystem.SaveState fluid = capture.Fluid;
            writer.Write(fluid.Sources.Length);
            foreach (FluidSystem.SourceSaveState source in fluid.Sources)
            {
                if (source.Handle < 2 || source.Handle > 0x1FFF ||
                    source.TypeMask is < 1 or > 4 ||
                    source.HasHeight && source.HasVpf)
                {
                    throw new InvalidDataException(
                        $"Canonical fluid source {source.Handle} is invalid.");
                }

                writer.Write(source.Handle);
                writer.Write(source.PositionX);
                writer.Write(source.PositionY);
                writer.Write(source.PositionZ);
                writer.Write(source.TypeMask);
                writer.Write(source.HasVolume
                    ? NativeFluidFloat(
                        source.ConfiguredVolume * OriginalSaveFluidSourceRecord.UnitScale,
                        source.Handle,
                        nameof(source.ConfiguredVolume))
                    : OriginalSaveFluidSourceRecord.DisabledValue);
                writer.Write(source.HasVolume
                    ? NativeFluidFloat(
                        source.Volume * OriginalSaveFluidSourceRecord.UnitScale,
                        source.Handle,
                        nameof(source.Volume))
                    : OriginalSaveFluidSourceRecord.DisabledValue);
                double encodedHeight = source.Height >= 1d ? source.Height + 2d : source.Height;
                writer.Write(source.HasHeight
                    ? NativeFluidFloat(encodedHeight, source.Handle, nameof(source.Height))
                    : OriginalSaveFluidSourceRecord.DisabledValue);
                writer.Write(source.HasVpf
                    ? NativeFluidFloat(
                        source.Vpf * OriginalSaveFluidSourceRecord.UnitScale,
                        source.Handle,
                        nameof(source.Vpf))
                    : OriginalSaveFluidSourceRecord.DisabledValue);
                writer.Write(source.Vented ? (byte)1 : (byte)0);
            }

            var entities = new Dictionary<int, OriginalSaveCanonicalEntityState>(
                capture.Entities.Length);
            foreach (OriginalSaveCanonicalEntityState entity in capture.Entities)
                entities.Add(entity.ObjectId, entity);

            writer.Write(fluid.Stoppers.Length);
            foreach (FluidSystem.StopperSaveState stopper in fluid.Stoppers)
            {
                if (!entities.TryGetValue(
                        stopper.Handle,
                        out OriginalSaveCanonicalEntityState? owner) ||
                    !Finite(stopper.Permeability) ||
                    stopper.Permeability is < 0f or > 1f)
                {
                    throw new InvalidDataException(
                        $"Canonical fluid stopper {stopper.Handle} is invalid.");
                }
                int startX = NativeStopperCoordinate(stopper.StartX, stopper.Handle);
                int startY = NativeStopperCoordinate(stopper.StartY, stopper.Handle);
                int endX = NativeStopperCoordinate(stopper.EndX, stopper.Handle);
                int endY = NativeStopperCoordinate(stopper.EndY, stopper.Handle);
                writer.Write(stopper.Handle);
                writer.Write(owner.Position.x);
                writer.Write(owner.Position.y);
                writer.Write(owner.Position.z);
                writer.Write((byte)1);
                writer.Write(Math.Min(startX, endX));
                writer.Write(Math.Min(startY, endY));
                writer.Write(Math.Max(startX, endX));
                writer.Write(Math.Max(startY, endY));
                writer.Write(stopper.Permeability);
                writer.Write(stopper.Permeability);
            }

            writer.Write(0);
            writer.Write(1);
            for (int set = 0; set < 4; set++) writer.Write(0);
            writer.Write((byte)0);
        }

        private static float NativeFluidFloat(double value, int handle, string field)
        {
            if (!Finite(value) || value < -float.MaxValue || value > float.MaxValue)
            {
                throw new InvalidDataException(
                    $"Fluid source {handle} field {field} cannot be represented natively.");
            }
            return (float)value;
        }

        private static int NativeStopperCoordinate(float value, int handle)
        {
            if (!Finite(value) || value < int.MinValue || value > int.MaxValue || value != (int)value)
            {
                throw new InvalidDataException(
                    $"Fluid stopper {handle} has a non-integral native coordinate.");
            }
            return (int)value;
        }

        private void WriteCanonicalObjectBodies(
            BinaryWriter writer,
            OriginalSaveCanonicalCapture capture)
        {
            Dictionary<int, int> triggerSlots = BuildTriggerSlotsByOwner(capture.Triggers);
            for (int index = 0; index < capture.Entities.Length; index++)
            {
                OriginalSaveCanonicalEntityState entity = capture.Entities[index] ??
                    throw new InvalidDataException("A canonical object registry slot is null.");
                int triggerSlot = triggerSlots.TryGetValue(entity.ObjectId, out int value)
                    ? value
                    : -1;
                writer.Write(entity.ObjectId);
                writer.Write(_structureBuilder.BuildObjectBody(
                    entity,
                    capture.GameTime,
                    triggerSlot));
            }
            writer.Write(-1);
        }

        private static Dictionary<int, int> BuildTriggerSlotsByOwner(
            IReadOnlyList<TriggerRecord?> triggers)
        {
            var result = new Dictionary<int, int>();
            for (int slot = 0; slot < triggers.Count; slot++)
            {
                TriggerRecord? trigger = triggers[slot];
                if (trigger == null || trigger.Kind == TriggerKind.Dead) continue;
                if (trigger.OwnerHandle < 2 || !result.TryAdd(trigger.OwnerHandle, slot))
                {
                    throw new InvalidDataException(
                        "Canonical trigger ownership cannot be represented natively.");
                }
            }
            return result;
        }

        private static void WriteClassLights(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveCanonicalClassLightState> lights)
        {
            int previousSlot = -1;
            foreach (OriginalSaveCanonicalClassLightState light in lights)
            {
                if (light == null || light.SlotIndex <= previousSlot || light.SlotIndex >= 0x800)
                    throw new InvalidDataException("Canonical class lights are not strictly ordered.");
                writer.Write(light.SlotIndex);
                writer.Write(light.PositionX.Bits);
                writer.Write(light.PositionY.Bits);
                writer.Write(light.PositionZ.Bits);
                writer.Write(light.Red);
                writer.Write(light.Green);
                writer.Write(light.Blue);
                writer.Write(light.Radius.Bits);
                writer.Write(light.SourceToken);
                writer.Write(light.TrailingCompatibilityValue.Bits);
                previousSlot = light.SlotIndex;
            }
        }

        private static void WriteEnergy(BinaryWriter writer, EnergySystem.SaveState energy)
        {
            if (energy == null || energy.Sources == null)
                throw new InvalidDataException("The canonical energy state is missing.");
            writer.Write(energy.Sources.Length);
            foreach (EnergySystem.SourceSaveState source in energy.Sources)
            {
                if (source == null || source.Leases == null || !Finite(source.Range))
                    throw new InvalidDataException("A canonical energy source is invalid.");
                writer.Write(source.SourceHandle);
                writer.Write(source.EnergyClass);
                writer.Write(source.Range);
                writer.Write(source.Leases.Length);
                foreach (EnergySystem.LeaseSaveState lease in source.Leases)
                {
                    if (lease == null || !Finite(lease.Amount))
                        throw new InvalidDataException("A canonical energy lease is invalid.");
                    writer.Write(lease.ConsumerHandle);
                    writer.Write(lease.Amount);
                    writer.Write(lease.TicksLeft);
                }
            }
        }

        private static void WriteAnimationControllers(
            BinaryWriter writer,
            OriginalSaveCanonicalCapture capture)
        {
            int previousObjectId = -1;
            foreach (OriginalSaveCanonicalAnimationState animation in
                     capture.AnimationControllers)
            {
                // The table's index order is a hard disk law (the parser rejects any other);
                // the semantic checks apply to live records only — a controller that outlived
                // its object carries allocator residue (corpus mode words like 0xDFDFDFDF) and
                // round-trips verbatim.
                if (animation == null || animation.ObjectId <= previousObjectId ||
                    animation.ObjectId > 0x1FFF || animation.FrameCount < 0 ||
                    animation.FrameValues == null ||
                    animation.FrameValues.Length != animation.FrameCount ||
                    (!animation.IsRetainedOrphan &&
                     (animation.ModelDatabaseSlot is < -1 or > 255 ||
                      animation.Mode is < 0 or > 2)))
                {
                    throw new InvalidDataException(
                        animation == null
                            ? "A canonical animation controller is null."
                            : "Canonical animation controller " +
                              $"{animation.ObjectId} (after {previousObjectId}, " +
                              $"orphan={animation.IsRetainedOrphan}) is invalid: " +
                              $"frameCount={animation.FrameCount}, " +
                              $"values={animation.FrameValues?.Length.ToString() ?? "null"}, " +
                              $"modelSlot={animation.ModelDatabaseSlot}, " +
                              $"mode={animation.Mode}.");
                }

                if (animation.IsRetainedOrphan)
                {
                    writer.Write(animation.ObjectId);
                    writer.Write(animation.RawStateByte);
                    writer.Write(animation.RawTimeBits);
                    writer.Write(animation.FrameCount);
                    writer.Write(animation.CurrentFrame);
                    writer.Write(animation.FrameValues.Length);
                    for (int frame = 0; frame < animation.FrameValues.Length; frame++)
                        writer.Write(animation.FrameValues[frame]);
                    writer.Write(animation.RepeatedControllerIndex);
                    writer.Write(animation.Mode);
                    writer.Write(animation.RawControlB);
                    writer.Write(animation.ModelDatabaseSlot);
                    writer.Write(animation.AnimationState);
                    previousObjectId = animation.ObjectId;
                    continue;
                }

                int currentFrame = animation.FrameCount == 0
                    ? -1
                    : Math.Max(0, Math.Min(animation.CurrentFrame, animation.FrameCount - 1));
                writer.Write(animation.ObjectId);
                writer.Write((byte)0);
                writer.Write(capture.GameTime);
                writer.Write(animation.FrameCount);
                writer.Write(currentFrame);
                writer.Write(animation.FrameCount);
                for (int frame = 0; frame < animation.FrameValues.Length; frame++)
                    writer.Write(animation.FrameValues[frame]);
                writer.Write(animation.ObjectId);
                writer.Write(animation.Mode);
                writer.Write(currentFrame);
                writer.Write(animation.ModelDatabaseSlot);
                writer.Write(animation.AnimationState);
                previousObjectId = animation.ObjectId;
            }
        }

        private static void WriteParticles(
            BinaryWriter writer,
            OriginalSaveCanonicalCapture capture)
        {
            ParticleSystemState.SaveState particles = capture.Particles;
            if (particles == null || particles.Sources == null || particles.Temps == null)
                throw new InvalidDataException("The canonical particle state is missing.");

            var records = new List<CanonicalParticleRecord>(
                checked(particles.Sources.Length + particles.Temps.Length));
            var usedSlots = new HashSet<int>();
            int nextSlot = 0;
            foreach (ParticleSystemState.SourceEntry entry in particles.Sources)
            {
                if (entry == null || entry.State == null)
                    throw new InvalidDataException("A canonical particle source is missing.");
                ParticleSystemState.Source state = entry.State;
                int slot = ReserveParticleSlot(
                    state.NativeParticleHandle,
                    state.NativeParticleHandle >= 0,
                    usedSlots,
                    ref nextSlot);
                records.Add(new CanonicalParticleRecord
                {
                    Slot = slot,
                    Kind = state.Type,
                    ParticleCount = checked(state.MaxParticles + 1),
                    PositionX = state.RelPos.x,
                    PositionY = state.RelPos.y,
                    PositionZ = state.RelPos.z,
                    DirectionX = state.Direction.x,
                    DirectionY = state.Direction.y,
                    DirectionZ = state.Direction.z,
                    RawValue50 = state.NumPerFrame,
                    Flag = state.VisibleInFog ? (byte)1 : (byte)0,
                    RawValue60 = state.MaxRangeSquared,
                    Mode = state.HasNativeState ? state.NativeMode : state.Active ? 1 : 0,
                    RawValue68 = 0f,
                    LinkedObjectToken = entry.ObjectId >= 2 ? entry.ObjectId : -1,
                    RawValue74 = state.HasNativeState ? state.NativeRawValue74 : state.SubMesh,
                    RawValue80 = state.HasNativeState ? state.NativeRawValue80 : 0,
                });
            }

            foreach (ParticleSystemState.TempSource temp in particles.Temps)
            {
                if (temp == null)
                    throw new InvalidDataException("A canonical temporary particle source is missing.");
                int slot = ReserveParticleSlot(
                    temp.NativeParticleHandle,
                    temp.NativeParticleHandle >= 0,
                    usedSlots,
                    ref nextSlot);
                records.Add(new CanonicalParticleRecord
                {
                    Slot = slot,
                    Kind = temp.Type,
                    ParticleCount = checked(temp.Num + 1),
                    PositionX = temp.Pos.x,
                    PositionY = temp.Pos.y,
                    PositionZ = temp.Pos.z,
                    DirectionX = temp.Direction.x,
                    DirectionY = temp.Direction.y,
                    DirectionZ = temp.Direction.z,
                    RawValue50 = temp.Num,
                    Flag = 0,
                    RawValue60 = 0f,
                    Mode = 1,
                    RawValue68 = checked((float)(capture.GameTime + temp.Remaining)),
                    LinkedObjectToken = -1,
                    RawValue74 = 0,
                    RawValue80 = 0,
                });
            }

            records.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            foreach (CanonicalParticleRecord record in records)
            {
                writer.Write(record.Slot);
                writer.Write(record.Kind);
                writer.Write(record.ParticleCount);
                writer.Write(record.PositionX);
                writer.Write(record.PositionY);
                writer.Write(record.PositionZ);
                writer.Write(record.DirectionX);
                writer.Write(record.DirectionY);
                writer.Write(record.DirectionZ);
                writer.Write(record.RawValue50);
                writer.Write(record.Flag);
                writer.Write(record.RawValue60);
                writer.Write(record.Mode);
                writer.Write(record.RawValue68);
                writer.Write(record.LinkedObjectToken);
                writer.Write(record.RawValue74);
                writer.Write(record.RawValue80);
            }
        }

        private static int ReserveParticleSlot(
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

        private sealed class CanonicalParticleRecord
        {
            internal int Slot;
            internal int Kind;
            internal int ParticleCount;
            internal float PositionX;
            internal float PositionY;
            internal float PositionZ;
            internal float DirectionX;
            internal float DirectionY;
            internal float DirectionZ;
            internal int RawValue50;
            internal byte Flag;
            internal float RawValue60;
            internal int Mode;
            internal float RawValue68;
            internal int LinkedObjectToken;
            internal int RawValue74;
            internal int RawValue80;
        }

        private static void WriteStory(
            BinaryWriter writer,
            OriginalSaveCanonicalStoryState story)
        {
            if (story == null) throw new InvalidDataException("The canonical story state is missing.");

            var eventValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in story.Events)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null ||
                    !eventValues.TryAdd(pair.Key, pair.Value))
                {
                    throw new InvalidDataException("Canonical story events are invalid or duplicated.");
                }
            }
            var eventTriggers = new Dictionary<string, OriginalSaveCanonicalStoryTrigger>(
                StringComparer.Ordinal);
            foreach (OriginalSaveCanonicalStoryTrigger trigger in story.EventTriggers)
            {
                if (!eventTriggers.TryAdd(trigger.Name, trigger))
                    throw new InvalidDataException("Canonical story triggers are duplicated.");
            }
            if (story.EventOrder.Length != eventValues.Count)
                throw new InvalidDataException("Canonical story event order is incomplete.");

            var eventIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            writer.Write(story.EventOrder.Length);
            for (int index = 0; index < story.EventOrder.Length; index++)
            {
                string name = story.EventOrder[index];
                if (!eventValues.TryGetValue(name, out string? value) ||
                    !eventTriggers.TryGetValue(
                        name,
                        out OriginalSaveCanonicalStoryTrigger trigger) ||
                    !eventIndices.TryAdd(name, index))
                {
                    throw new InvalidDataException("Canonical story event tables disagree.");
                }
                WriteNativeString(writer, name);
                WriteNativeString(writer, trigger.Trigger);
                WriteNativeString(writer, trigger.Extra);
                writer.Write(NativeBoolean(value));
            }

            var tempIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < story.Temps.Length; index++)
            {
                OriginalSaveCanonicalStoryTemp temp = story.Temps[index] ??
                    throw new InvalidDataException("A canonical story template is missing.");
                if (string.IsNullOrEmpty(temp.Name) || !tempIndices.TryAdd(temp.Name, index))
                    throw new InvalidDataException("Canonical story templates are invalid or duplicated.");
            }
            var groupIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < story.TempGroups.Length; index++)
            {
                string name = story.TempGroups[index].Key;
                if (string.IsNullOrEmpty(name) || !groupIndices.TryAdd(name, index))
                    throw new InvalidDataException("Canonical story groups are invalid or duplicated.");
            }
            var zoneIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < story.Zones.Length; index++)
            {
                if (string.IsNullOrEmpty(story.Zones[index].Name) ||
                    !zoneIndices.TryAdd(story.Zones[index].Name, index))
                {
                    throw new InvalidDataException("Canonical story zones are invalid or duplicated.");
                }
            }

            writer.Write(story.Temps.Length);
            for (int index = 0; index < story.Temps.Length; index++)
            {
                OriginalSaveCanonicalStoryTemp temp = story.Temps[index];
                WriteNativeString(writer, temp.Name);
                writer.Write(index);
                writer.Write(index);
                writer.Write(temp.Placed ? (byte)1 : (byte)0);
                writer.Write(temp.EventNames.Length);
                foreach (string eventName in temp.EventNames)
                {
                    if (!eventIndices.TryGetValue(eventName, out int eventIndex))
                    {
                        throw new InvalidDataException(
                            $"Story template '{temp.Name}' references unknown event '{eventName}'.");
                    }
                    writer.Write(eventIndex);
                }
                writer.Write(temp.Low);
                writer.Write(temp.High);
                writer.Write(temp.MinimumDistance);
                writer.Write(FindOptionalIndex(tempIndices, temp.MinimumDistanceReference));
                bool isPlacedUnique = string.Equals(
                    temp.Name,
                    story.UniqueTempPlacedName,
                    StringComparison.Ordinal);
                writer.Write(isPlacedUnique ? Math.Max(0, story.UniqueTempExtentWidth) : 0);
                writer.Write(isPlacedUnique ? Math.Max(0, story.UniqueTempExtentHeight) : 0);
                int[] digConnect = ParseNativeIntegers(temp.DigConnect, 2);
                writer.Write(digConnect[1]);
                writer.Write(groupIndices.TryGetValue(temp.Name, out int groupIndex)
                    ? groupIndex
                    : -1);
                writer.Write(temp.Exclusive ? (byte)1 : (byte)0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(temp.Zone);
                writer.Write(FindOptionalIndex(zoneIndices, temp.ZoneBorder));
                writer.Write(temp.PlacedX);
                writer.Write(temp.PlacedY);
                writer.Write(temp.CheckAlign > 0 ? (byte)1 : (byte)0);
                writer.Write(temp.StoneIndex);
                writer.Write(temp.MiddleDistance);
                writer.Write(temp.NonRelevant);
            }

            writer.Write(story.TempGroups.Length);
            for (int index = 0; index < story.TempGroups.Length; index++)
            {
                KeyValuePair<string, string> group = story.TempGroups[index];
                OriginalSaveCanonicalStoryTemp? temp = FindStoryTemp(story.Temps, group.Key);
                WriteStoryGroup(writer, group.Key, group.Value, temp);
            }

            WriteNativeString(writer, story.UniqueTemp);
            writer.Write(story.UniqueTempPlaced ? 1 : 0);
            writer.Write(story.RectWidth);
            writer.Write(story.RectHeight);
            writer.Write(story.RectX);
            writer.Write(story.RectY);
            writer.Write(story.ScanX);
            writer.Write(story.ScanY);
            writer.Write(story.RawRectY);
            writer.Write(story.RawRectX);
            writer.Write(story.RawRectWidth);
            writer.Write(story.RawRectHeight);
            writer.Write(0);

            int mapCount = checked(story.MapWidth * story.MapHeight);
            var mapValues = new int[mapCount];
            foreach (OriginalSaveCanonicalStoryMapEntry entry in story.Map)
            {
                if ((uint)entry.X >= (uint)story.MapWidth ||
                    (uint)entry.Y >= (uint)story.MapHeight || entry.Value is < 0 or > 2)
                {
                    throw new InvalidDataException("A canonical story map entry is invalid.");
                }
                mapValues[checked(entry.Y * story.MapWidth + entry.X)] = entry.Value;
            }
            writer.Write(mapValues.Length);
            foreach (int value in mapValues) writer.Write(value);
            writer.Write(story.MapWidth);
            writer.Write(story.MapHeight);

            writer.Write(story.Zones.Length);
            foreach (OriginalSaveCanonicalStoryZone zone in story.Zones)
            {
                WriteNativeString(writer, zone.Name);
                writer.Write(zone.Bound);
            }

            KeyValuePair<int, string>? callback = SelectStoryCallback(story.Callbacks);
            WriteNativeString(writer, callback?.Value ?? string.Empty);
            writer.Write(callback?.Key ?? -1);
            writer.Write(story.ZoneFloor);
            writer.Write(story.LatchDirection);
            writer.Write(story.LatchX);
            writer.Write(story.LatchY);
            writer.Write(story.LatchConsumed ? (byte)1 : (byte)0);
            writer.Write(story.DigWeight);
            writer.Write(story.DigMessage ? (byte)1 : (byte)0);
            writer.Write(story.DigThreshold);
        }

        private static void WriteStoryGroup(
            BinaryWriter writer,
            string name,
            string specification,
            OriginalSaveCanonicalStoryTemp? temp)
        {
            if (specification == null)
                throw new InvalidDataException($"Story group '{name}' has no specification.");
            List<string> rows;
            try
            {
                rows = TclList.Parse(specification);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    $"Story group '{name}' has an invalid Tcl specification.",
                    exception);
            }

            var entries = new List<StoryGroupEntry>();
            var materialRecords = new List<int[]>();
            foreach (string row in rows)
            {
                List<string> values = TclList.Parse(row);
                if (values.Count == 0) continue;
                if (string.Equals(values[0], "mat", StringComparison.Ordinal))
                {
                    for (int index = 1; index < values.Count; index++)
                    {
                        List<string> material = TclList.Parse(values[index]);
                        if (material.Count < 5)
                            throw new InvalidDataException($"Story group '{name}' has an invalid mat row.");
                        int type = ParseNativeInteger(material[0]);
                        int x = ParseNativeInteger(material[1]);
                        int y = ParseNativeInteger(material[2]);
                        int width = ParseNativeInteger(material[3]);
                        int height = ParseNativeInteger(material[4]);
                        materialRecords.Add(new[]
                        {
                            x,
                            y,
                            checked(x + width),
                            checked(y + height),
                            type,
                        });
                    }
                    continue;
                }
                if (values.Count < 3)
                    throw new InvalidDataException($"Story group '{name}' has an invalid entry row.");
                entries.Add(new StoryGroupEntry(
                    values[2],
                    ParseNativeInteger(values[0]),
                    ParseNativeInteger(values[1]),
                    StoryChildSeen(temp, values[2])));
            }

            WriteNativeString(writer, name);
            writer.Write(entries.Count);
            foreach (StoryGroupEntry entry in entries)
            {
                WriteNativeString(writer, entry.Name);
                writer.Write(entry.X);
                writer.Write(entry.Y);
                writer.Write(0);
                writer.Write(0);
                writer.Write(entry.Completed ? (byte)1 : (byte)0);
            }

            int[] digConnect = ParseNativeIntegers(temp?.DigConnect ?? string.Empty, 2);
            writer.Write(0);
            writer.Write(0);
            writer.Write(digConnect[0]);
            IReadOnlyList<int[]> connectionRecords = ParseStoryConnectionRecords(
                temp?.Connect ?? string.Empty);
            writer.Write(connectionRecords.Count);
            foreach (int[] record in connectionRecords)
                for (int value = 0; value < 4; value++) writer.Write(record[value]);
            writer.Write(materialRecords.Count);
            foreach (int[] record in materialRecords)
                for (int value = 0; value < 5; value++) writer.Write(record[value]);
        }

        private static IReadOnlyList<int[]> ParseStoryConnectionRecords(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int[]>();
            List<string> outer = TclList.Parse(value);
            if (outer.Count == 4 && AllNativeIntegers(outer))
            {
                return new[]
                {
                    new[]
                    {
                        ParseNativeInteger(outer[0]),
                        ParseNativeInteger(outer[1]),
                        ParseNativeInteger(outer[2]),
                        ParseNativeInteger(outer[3]),
                    },
                };
            }

            var result = new List<int[]>(outer.Count);
            foreach (string row in outer)
            {
                List<string> values = TclList.Parse(row);
                if (values.Count != 4 || !AllNativeIntegers(values))
                    throw new InvalidDataException("A story connection record is invalid.");
                result.Add(new[]
                {
                    ParseNativeInteger(values[0]),
                    ParseNativeInteger(values[1]),
                    ParseNativeInteger(values[2]),
                    ParseNativeInteger(values[3]),
                });
            }
            return result;
        }

        private static bool StoryChildSeen(
            OriginalSaveCanonicalStoryTemp? temp,
            string childName)
        {
            if (temp == null) return false;
            foreach (string seen in temp.SeenChildren)
            {
                if (string.Equals(seen, childName, StringComparison.Ordinal) ||
                    string.Equals(
                        RemoveStoryExtension(seen),
                        RemoveStoryExtension(childName),
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string RemoveStoryExtension(string value)
        {
            return value.EndsWith(".tcl", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - 4)
                : value;
        }

        private static OriginalSaveCanonicalStoryTemp? FindStoryTemp(
            IReadOnlyList<OriginalSaveCanonicalStoryTemp> temps,
            string name)
        {
            foreach (OriginalSaveCanonicalStoryTemp temp in temps)
                if (string.Equals(temp.Name, name, StringComparison.Ordinal)) return temp;
            return null;
        }

        private static KeyValuePair<int, string>? SelectStoryCallback(
            IReadOnlyList<KeyValuePair<int, string>> callbacks)
        {
            if (callbacks.Count == 0) return null;
            KeyValuePair<int, string> selected = callbacks[0];
            for (int index = 1; index < callbacks.Count; index++)
                if (callbacks[index].Key < selected.Key) selected = callbacks[index];
            return selected;
        }

        private static int FindOptionalIndex(
            IReadOnlyDictionary<string, int> indices,
            string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (indices.TryGetValue(name, out int index)) return index;
            throw new InvalidDataException($"Story reference '{name}' is unresolved.");
        }

        private static int[] ParseNativeIntegers(string value, int count)
        {
            var result = new int[count];
            if (string.IsNullOrWhiteSpace(value)) return result;
            List<string> values = TclList.Parse(value);
            for (int index = 0; index < values.Count && index < result.Length; index++)
                result[index] = ParseNativeInteger(values[index]);
            return result;
        }

        private static bool AllNativeIntegers(IReadOnlyList<string> values)
        {
            foreach (string value in values)
            {
                if (!int.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out _))
                {
                    return false;
                }
            }
            return true;
        }

        private static int ParseNativeInteger(string value)
        {
            if (!int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int result))
            {
                throw new InvalidDataException($"'{value}' is not a native Int32 value.");
            }
            return result;
        }

        private static byte NativeBoolean(string value)
        {
            return string.IsNullOrEmpty(value) ||
                string.Equals(value, "0", StringComparison.Ordinal) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                ? (byte)0
                : (byte)1;
        }

        private readonly struct StoryGroupEntry
        {
            internal StoryGroupEntry(string name, int x, int y, bool completed)
            {
                Name = name;
                X = x;
                Y = y;
                Completed = completed;
            }

            internal string Name { get; }
            internal int X { get; }
            internal int Y { get; }
            internal bool Completed { get; }
        }

        private static void WriteSelectionControlGroups(
            BinaryWriter writer,
            IReadOnlyList<int[]> groups)
        {
            if (groups.Count != OriginalSaveSelectionControlGroupDocument.GroupCount)
                throw new InvalidDataException("Canonical selection state requires nine groups.");
            for (int copy = 0;
                 copy < OriginalSaveSelectionControlGroupDocument.SerializedCopyCount;
                 copy++)
            {
                for (int group = 0; group < groups.Count; group++)
                {
                    int[] members = groups[group] ??
                        throw new InvalidDataException("A canonical selection group is missing.");
                    writer.Write(members.Length);
                    foreach (int objectId in members) writer.Write(objectId);
                }
            }
        }

        private static void WriteSparetime(
            BinaryWriter writer,
            IReadOnlyList<SparetimeRegistration> registrations)
        {
            writer.Write(registrations.Count);
            foreach (SparetimeRegistration registration in registrations)
            {
                writer.Write((int)registration.Category);
                writer.Write(registration.ObjectId);
            }
        }

        private static readonly string[] TriggerTargetTypeNames =
        {
            "dummy", "gnome", "monster", "material", "tool", "production", "energy",
            "protection", "store", "transport", "elevator", "baby", "info",
        };

        private static void WriteTriggers(
            BinaryWriter writer,
            IReadOnlyList<TriggerRecord?> triggers)
        {
            writer.Write(triggers.Count);
            for (int slot = 0; slot < triggers.Count; slot++)
            {
                TriggerRecord? trigger = triggers[slot];
                if (trigger == null || trigger.Kind == TriggerKind.Dead)
                {
                    writer.Write(0);
                    continue;
                }

                writer.Write(slot + 1);
                writer.Write(slot);
                writer.Write((int)trigger.Kind);
                writer.Write(trigger.Execution);
                writer.Write(trigger.OwnerHandle);
                WriteNativeString(writer, trigger.FireScript);
                writer.Write(trigger.LastCheck);
                writer.Write(trigger.CheckTimer);
                writer.Write(trigger.CheckIdle);
                writer.Write(trigger.CheckNear);
                writer.Write(trigger.Active ? (byte)1 : (byte)0);
                writer.Write(trigger.TargetRange);
                writer.Write(trigger.FireTime);
                writer.Write(trigger.Interval);
                writer.Write(trigger.TargetRef);
                WriteNativeString(writer, trigger.TargetClass ?? "?");
                writer.Write(TriggerTargetTypeMask(trigger.TargetTypes));
                WriteNativeString(writer, trigger.TargetOwnerSpec ?? string.Empty);
                writer.Write(trigger.TargetCount);
                WriteNativeString(writer, trigger.CallbackScript);
                WriteNativeString(writer, TclList.Format(trigger.Actuators));
            }
        }

        private static int TriggerTargetTypeMask(IReadOnlyList<string>? targetTypes)
        {
            if (targetTypes == null) return 0;
            int result = 0;
            foreach (string targetType in targetTypes)
            {
                int bit = Array.FindIndex(
                    TriggerTargetTypeNames,
                    value => string.Equals(value, targetType, StringComparison.Ordinal));
                if (bit < 0)
                    throw new InvalidDataException($"Unsupported trigger target type '{targetType}'.");
                result |= 1 << bit;
            }
            return result;
        }

        private static void WriteHorizon(
            BinaryWriter writer,
            IReadOnlyList<HorizonRegionState> regions)
        {
            writer.Write(regions.Count);
            foreach (HorizonRegionState region in regions)
            {
                writer.Write(region.Position.x);
                writer.Write(region.Position.y);
                writer.Write(region.Position.z);
                writer.Write(region.Red);
                writer.Write(region.Green);
                writer.Write(region.Blue);
                writer.Write(region.Depth);
                writer.Write(region.Size);
            }
        }

        private static void WriteAdaptiveSoundMarkers(
            BinaryWriter writer,
            IReadOnlyList<AudioState.MusicMarker> markers)
        {
            writer.Write(markers.Count);
            foreach (AudioState.MusicMarker marker in markers)
            {
                writer.Write(marker.ObjectId);
                writer.Write(marker.Pos.x);
                writer.Write(marker.Pos.y);
                writer.Write(marker.Pos.z);
                WriteNativeString(writer, marker.Theme);
                writer.Write(marker.Radius);
            }
        }

        private static void WriteNewsTicker(
            BinaryWriter writer,
            NewsTickerSystem.SaveState newsTicker)
        {
            if (newsTicker == null || newsTicker.Active == null || newsTicker.History == null ||
                newsTicker.DisplayQueue == null)
            {
                throw new InvalidDataException("The canonical news-ticker state is missing.");
            }

            writer.Write(newsTicker.Active.Length);
            foreach (NewsTickerSystem.EntrySaveState entry in newsTicker.Active)
            {
                if (entry == null)
                    throw new InvalidDataException("A canonical news-ticker entry is missing.");
                WriteNativeString(writer, entry.Text);
                writer.Write(entry.PackedColorWord);
                writer.Write(entry.NativeCategoryWord);
                writer.Write(entry.TextMetricX);
                writer.Write(entry.TextMetricY);
                writer.Write(entry.Priority);
                writer.Write(entry.EffectivePriority);
                writer.Write(entry.RemainingTime);
                writer.Write(entry.Id);
                writer.Write(entry.PresentationFlag0);
                writer.Write(entry.PresentationFlag1);
                WriteNativeString(writer, entry.ClickScript);
                long timestampBits = BitConverter.DoubleToInt64Bits(entry.Timestamp);
                writer.Write(unchecked((int)timestampBits));
                writer.Write(unchecked((int)(timestampBits >> 32)));
                writer.Write(entry.AppearsInHistory ? (byte)1 : (byte)0);
            }

            writer.Write(newsTicker.History.Length);
            foreach (NewsTickerSystem.EntrySaveState entry in newsTicker.History)
            {
                if (entry == null)
                    throw new InvalidDataException("A canonical news-ticker history entry is missing.");
                WriteNativeString(writer, entry.Text);
                writer.Write(entry.PackedColorWord);
                WriteNativeString(writer, entry.ClickScript);
                writer.Write(entry.NativeCategoryWord);
                writer.Write(entry.Id);
            }

            writer.Write(newsTicker.EnabledMask);
            writer.Write(newsTicker.DisplayQueue.Length);
            foreach (NewsTickerSystem.DisplayQueueSaveState entry in newsTicker.DisplayQueue)
            {
                if (entry == null)
                    throw new InvalidDataException("A news-ticker display-queue entry is missing.");
                writer.Write(entry.Id);
                writer.Write(entry.Position);
            }
        }

        private static void WriteObjectIcons(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveObjectIconRecord> icons)
        {
            if (icons.Count != OriginalSaveObjectIconDocument.SlotCount)
                throw new InvalidDataException("Canonical object icons require 128 slots.");
            for (int slot = 0; slot < icons.Count; slot++)
            {
                OriginalSaveObjectIconRecord icon = icons[slot] ??
                    throw new InvalidDataException("A canonical object-icon slot is missing.");
                if (icon.Slot != slot)
                    throw new InvalidDataException("Canonical object-icon slots are out of order.");
                writer.Write(icon.ObjectId);
                writer.Write(icon.Value);
                writer.Write(icon.OpaqueRuntimeWord);
                writer.Write(icon.IconType);
                writer.Write(icon.OffsetX.Bits);
                writer.Write(icon.OffsetY.Bits);
                writer.Write(icon.OffsetZ.Bits);
            }
        }

        private static void WriteCaveRecords(
            BinaryWriter writer,
            IReadOnlyList<DiggableTerrain.CaveRecord> records)
        {
            writer.Write(records.Count);
            foreach (DiggableTerrain.CaveRecord record in records)
            {
                writer.Write(record.X0);
                writer.Write(record.Y0);
                writer.Write(record.X1);
                writer.Write(record.Y1);
                writer.Write(record.TemplateIdx);
            }
        }

        private static void WriteAi(BinaryWriter writer, AiSystem.SaveState ai)
        {
            if (ai == null || ai.Owners == null || ai.Owners.Length != 8)
                throw new InvalidDataException("Canonical AI state requires eight owner slots.");
            for (int owner = 0; owner < ai.Owners.Length; owner++)
            {
                AiSystem.OwnerSaveState state = ai.Owners[owner] ??
                    throw new InvalidDataException($"Canonical AI owner {owner} is missing.");
                if (state.VariableNames == null || state.VariableValues == null ||
                    state.VariableNames.Length != state.VariableValues.Length)
                {
                    throw new InvalidDataException($"Canonical AI owner {owner} has invalid variables.");
                }
                if (!state.Initialized)
                {
                    if (state.Enabled || state.VariableNames.Length != 0)
                    {
                        throw new InvalidDataException(
                            $"Uninitialized canonical AI owner {owner} contains interpreter state.");
                    }
                    writer.Write((byte)0);
                    writer.Write(owner);
                    WriteNativeString(writer, string.Empty);
                    writer.Write(0);
                    continue;
                }
                writer.Write(state.Enabled ? (byte)1 : (byte)0);
                writer.Write(owner);
                WriteNativeString(writer, OriginalSaveAiDocument.NativeStandardScriptPath);
                writer.Write(1);
                writer.Write(state.VariableNames.Length);
                for (int index = 0; index < state.VariableNames.Length; index++)
                {
                    WriteNativeString(writer, state.VariableNames[index]);
                    WriteNativeString(writer, state.VariableValues[index]);
                }
            }
        }

        private static void WriteElf(BinaryWriter writer, ElfState.NativeSaveState? elf)
        {
            if (elf == null)
            {
                // The cold CElf must use the native idle shape, not port -1 sentinels: across
                // the whole native corpus the model database is 254 and current/requested
                // registered animations are 0 (Docs/ORIGINAL_SAVE_FORMAT_RE.md §CElf). The
                // original's 200 Hz narrator tick (FUN_00427e30) consumed our -1 database as a
                // model lookup and dereferenced null+0x50, crashing during the herbalist
                // cutscene.
                writer.Write(0);      // texture variant
                writer.Write(0);      // current registered animation (native idle = 0)
                writer.Write(0);      // requested registered animation (native idle = 0)
                writer.Write(254);    // model database id (native constant)
                writer.Write(-1);     // mesh id: no live mesh
                writer.Write(0f);
                writer.Write(-1);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write(-1);
                writer.Write(-1);
                return;
            }

            writer.Write(elf.TextureVariant);
            // Port-internal -1 sentinels never reach the archive: the native corpus is
            // db=254 / registered anims >= 0 everywhere, and the narrator tick dereferences
            // the database id (the herbalist crash). The port's live ElfState only fills
            // _nativeModelDatabaseId when restoring from a native save, so a fresh campaign
            // would otherwise serialize -1s.
            writer.Write(Math.Max(0, elf.CurrentRegisteredAnimation));
            writer.Write(Math.Max(0, elf.RequestedRegisteredAnimation));
            writer.Write(elf.NativeModelDatabaseId < 0 ? 254 : elf.NativeModelDatabaseId);
            writer.Write(elf.NativeMeshId);
            writer.Write(elf.FrameAlpha);
            writer.Write(elf.LookAtObjectId);
            writer.Write(elf.Position.x);
            writer.Write(elf.Position.y);
            writer.Write(elf.Position.z);
            writer.Write(elf.LeanA);
            writer.Write(elf.LeanB);
            writer.Write(elf.NativeState64Bits);
            writer.Write(elf.Target.x);
            writer.Write(elf.Target.y);
            writer.Write(elf.Target.z);
            writer.Write(elf.Mode);
            writer.Write(elf.DrawOverUi ? (byte)1 : (byte)0);
            writer.Write(elf.AnimationHasMoreFrames ? (byte)1 : (byte)0);
            writer.Write(elf.FirstParticleSourceHandle);
            writer.Write(elf.SecondParticleSourceHandle);
        }

        private static void WriteProductionSlotOverrides(
            BinaryWriter writer,
            OriginalSaveCanonicalCapture capture)
        {
            IReadOnlyList<int> values = capture.ProductionSlotOverrides;
            if (values.Count != 32)
                throw new InvalidDataException("Canonical production overrides require 32 values.");
            foreach (int value in values)
            {
                if (value is < 0 or > 2)
                    throw new InvalidDataException(
                        "A canonical production override is outside the 0/1/2 domain.");
                writer.Write(value);
            }
        }

        private static void WritePlayers(
            BinaryWriter writer,
            OriginalSaveCanonicalPlayerState players)
        {
            if (players == null || players.Slots == null || players.Slots.Length != 8 ||
                players.Diplomacy == null || players.Diplomacy.GetLength(0) != 8 ||
                players.Diplomacy.GetLength(1) != 8)
            {
                throw new InvalidDataException("Canonical player state requires eight owner slots.");
            }
            for (int owner = 0; owner < players.Slots.Length; owner++)
            {
                OriginalSaveCanonicalPlayerSlot slot = players.Slots[owner] ??
                    throw new InvalidDataException($"Canonical player slot {owner} is missing.");
                WriteNativeString(writer, slot.PlayerName);
                writer.Write(slot.SlotState);
                writer.Write(slot.PlayerOwnerId);
                writer.Write(slot.ClanOwnerId);
                WriteNativeString(writer, slot.SecondaryName);
                for (int target = 0; target < 8; target++)
                    writer.Write(players.Diplomacy[owner, target]);
            }
        }

        private static bool ObjectGraphChanged(
            OriginalSaveObjectCollection retained,
            IReadOnlyList<OriginalSaveCanonicalEntityState> live)
        {
            if (retained.Count != live.Count) return true;
            for (int index = 0; index < retained.Count; index++)
            {
                if (live[index] == null || retained[index].ObjectId != live[index].ObjectId)
                    return true;
            }
            return false;
        }

        private static bool MatchesSourceBody(
            byte[] sourcePayload,
            int sourceOffset,
            int sourceLength,
            byte[] rebuiltBody)
        {
            if (sourceLength != rebuiltBody.Length) return false;
            for (int index = 0; index < sourceLength; index++)
            {
                if (sourcePayload[sourceOffset + index] != rebuiltBody[index]) return false;
            }
            return true;
        }

        private static void WritePrefixToDeclarations(
            BinaryWriter writer,
            byte[] sourcePayload,
            OriginalSaveArchiveDocument source,
            OriginalSaveObjectManifest manifest,
            OriginalSaveWorldPatch? patch)
        {
            int declarationOffset = checked((int)manifest.DeclarationsOffset);
            if (patch == null)
            {
                writer.Write(sourcePayload, 0, declarationOffset);
                return;
            }

            OriginalSaveWorldMetadata metadata = source.Metadata.World;
            int worldOffset = checked((int)metadata.SectionOffset);
            int marker4Offset = checked((int)metadata.Marker4Offset);
            writer.Write(sourcePayload, 0, worldOffset);
            WriteWorld(writer, source.World, patch);
            writer.Write(
                sourcePayload,
                marker4Offset,
                checked(declarationOffset - marker4Offset));
        }

        static void WritePrefix(
            BinaryWriter writer,
            byte[] sourcePayload,
            OriginalSaveArchiveDocument source,
            OriginalSaveObjectManifest manifest,
            OriginalSaveWorldPatch? patch)
        {
            if (patch == null)
            {
                writer.Write(sourcePayload, 0, checked((int)manifest.BodySectionOffset));
                return;
            }

            OriginalSaveWorldMetadata metadata = source.Metadata.World;
            int worldOffset = checked((int)metadata.SectionOffset);
            int marker4Offset = checked((int)metadata.Marker4Offset);
            int bodyOffset = checked((int)manifest.BodySectionOffset);
            writer.Write(sourcePayload, 0, worldOffset);
            WriteWorld(writer, source.World, patch);
            writer.Write(sourcePayload, marker4Offset, checked(bodyOffset - marker4Offset));
        }

        static void WriteWorld(
            BinaryWriter writer,
            OriginalSaveWorldDocument world,
            OriginalSaveWorldPatch patch)
        {
            foreach (int index in patch.TerrainIndices)
            {
                if ((uint)index >= (uint)world.Terrain.Count)
                    throw new InvalidDataException(
                        $"Original terrain patch references tile {index} outside the world.");
            }

            writer.Write(world.Width);
            writer.Write(world.Height);
            writer.Write(world.MinX);
            writer.Write(world.MinY);
            writer.Write(world.MaxXExclusive);
            writer.Write(world.MaxYExclusive);

            bool synthesizeSettledFluid = IsSettledFluidSynthesis(patch.Fluid);
            for (int index = 0; index < world.Terrain.Count; index++)
            {
                patch.TryGetTerrainTile(index, out OriginalSaveTerrainTilePatch? tilePatch);
                WriteTerrainTile(
                    writer,
                    world.Terrain[index],
                    tilePatch,
                    synthesizeSettledFluid);
            }

            IReadOnlyList<int> fogBeginRows = patch.FogBeginRows ?? world.FogBeginRows;
            ValidateCount(fogBeginRows, 8, "fog begin rows");
            for (int player = 0; player < fogBeginRows.Count; player++)
            {
                int row = fogBeginRows[player];
                if (row < -1 || row >= world.Height)
                    throw new InvalidDataException(
                        $"Original fog begin row {row} is outside the world for player {player}.");
                writer.Write(row);
            }
            writer.Write(patch.LightBegin ?? world.LightBegin);
            writer.Write(patch.GroundBegin ?? world.GroundBegin);

            WriteFluid(writer, world, patch.Fluid);
            WriteFogFadeTiles(writer, world, patch.FogFadeTilesByPlayer);
        }

        static void WriteTerrainTile(
            BinaryWriter writer,
            OriginalSaveTerrainTile tile,
            OriginalSaveTerrainTilePatch? patch,
            bool synthesizeSettledFluid)
        {
            if (synthesizeSettledFluid &&
                (patch == null ||
                 !patch.FluidTypeBuffer0.HasValue ||
                 !patch.FluidTypeBuffer1.HasValue ||
                 !patch.FluidFillBuffer0.HasValue ||
                 !patch.FluidFillBuffer1.HasValue))
            {
                throw new InvalidDataException(
                    "Settled fluid synthesis requires a live fluid projection for every terrain tile.");
            }

            writer.Write(patch?.BlockIndex ?? tile.BlockIndex);
            writer.Write(patch?.NavigationState ?? tile.NavigationState);
            writer.Write(patch?.DigState ?? tile.DigState);
            writer.Write(patch?.DigOrderIds ?? tile.DigOrderIds);
            writer.Write(patch?.FloorAndExitState ?? tile.FloorAndExitState);
            byte templateFlags = patch?.TemplateFlags ?? tile.TemplateFlags;
            if (synthesizeSettledFluid) templateFlags = (byte)(templateFlags & ~4);
            writer.Write(templateFlags);
            writer.Write(patch?.CellId ?? tile.CellId);

            uint terrainState = patch?.TerrainState ?? tile.TerrainState;
            if (patch?.FluidTypeBuffer0 is byte type0)
            {
                ValidateFluidType(type0);
                terrainState = terrainState & 0xFF00FFFFu | (uint)type0 << 16;
            }
            if (patch?.FluidTypeBuffer1 is byte type1)
            {
                ValidateFluidType(type1);
                terrainState = terrainState & 0x00FFFFFFu | (uint)type1 << 24;
            }
            writer.Write(terrainState);
            WriteFluidFill(writer, patch?.FluidFillBuffer0, tile.Unknown1C);
            WriteFluidFill(writer, patch?.FluidFillBuffer1, tile.Unknown20);
            writer.Write(synthesizeSettledFluid ? 0u : tile.Unknown24);
            writer.Write(synthesizeSettledFluid ? 0u : tile.Unknown28);
            writer.Write(synthesizeSettledFluid ? 0u : tile.Unknown2C);
            writer.Write(synthesizeSettledFluid ? 0u : tile.Unknown30);
            writer.Write(patch?.FogState ?? tile.FogState);
        }

        private static bool IsSettledFluidSynthesis(OriginalSaveFluidPatch? patch)
        {
            if (patch == null || patch.Sources == null || patch.Stoppers == null ||
                patch.CurrentBufferIndex != 0 || patch.PreviousBufferIndex != 1 ||
                patch.SparseSets == null || patch.SparseSets.Count != 4 ||
                patch.DebugEnabled != false)
            {
                return false;
            }

            foreach (IReadOnlyList<OriginalSaveIntPair> set in patch.SparseSets)
                if (set == null || set.Count != 0) return false;
            return true;
        }

        static void WriteFluidFill(BinaryWriter writer, float? replacement, uint originalBits)
        {
            if (!replacement.HasValue)
            {
                writer.Write(originalBits);
                return;
            }
            if (!Finite(replacement.Value) || replacement.Value < 0f)
                throw new InvalidDataException("Original fluid fill is invalid.");
            writer.Write(replacement.Value);
        }

        static void WriteFluid(
            BinaryWriter writer,
            OriginalSaveWorldDocument world,
            OriginalSaveFluidPatch? patch)
        {
            IReadOnlyList<OriginalSaveFluidSourceRecord> sources =
                patch?.Sources ?? world.Fluid.Sources;
            writer.Write(sources.Count);
            foreach (OriginalSaveFluidSourceRecord source in sources)
            {
                ValidateFluidSource(source);
                writer.Write(source.ObjectId);
                writer.Write(source.PositionX);
                writer.Write(source.PositionY);
                writer.Write(source.PositionZ);
                writer.Write(source.FluidType);
                writer.Write(source.State14);
                writer.Write(source.State18);
                writer.Write(source.State1C);
                writer.Write(source.State20);
                writer.Write(source.Vented ? (byte)1 : (byte)0);
            }

            IReadOnlyList<OriginalSaveFluidStopperRecord> stoppers =
                patch?.Stoppers ?? world.Fluid.Stoppers;
            writer.Write(stoppers.Count);
            foreach (OriginalSaveFluidStopperRecord stopper in stoppers)
            {
                ValidateFluidStopper(stopper);
                writer.Write(stopper.ObjectId);
                writer.Write(stopper.CachedPositionX);
                writer.Write(stopper.CachedPositionY);
                writer.Write(stopper.CachedPositionZ);
                writer.Write(stopper.Dirty ? (byte)1 : (byte)0);
                writer.Write(stopper.MinX);
                writer.Write(stopper.MinY);
                writer.Write(stopper.MaxX);
                writer.Write(stopper.MaxY);
                writer.Write(stopper.PermeabilityX);
                writer.Write(stopper.PermeabilityY);
            }

            int currentBuffer = patch?.CurrentBufferIndex ?? world.Fluid.CurrentBufferIndex;
            int previousBuffer = patch?.PreviousBufferIndex ?? world.Fluid.PreviousBufferIndex;
            if ((uint)currentBuffer > 1 || (uint)previousBuffer > 1 || currentBuffer == previousBuffer)
                throw new InvalidDataException("Original fluid buffer indices are invalid.");
            writer.Write(currentBuffer);
            writer.Write(previousBuffer);

            if (patch?.SparseSets == null)
            {
                for (int set = 0; set < world.Fluid.SparseSets.Count; set++)
                    WritePairs(writer, world.Fluid.SparseSets[set], world, "fluid sparse tile");
            }
            else
            {
                ValidateCount(patch.SparseSets, 4, "fluid sparse sets");
                foreach (IReadOnlyList<OriginalSaveIntPair> set in patch.SparseSets)
                    WritePairs(writer, set, world, "fluid sparse tile");
            }
            writer.Write((patch?.DebugEnabled ?? world.Fluid.DebugEnabled) ? (byte)1 : (byte)0);
        }

        static void WriteFogFadeTiles(
            BinaryWriter writer,
            OriginalSaveWorldDocument world,
            IReadOnlyList<IReadOnlyList<OriginalSaveIntPair>>? replacement)
        {
            if (replacement == null)
            {
                for (int player = 0; player < world.Fog.FadeTilesByPlayer.Count; player++)
                    WritePairs(writer, world.Fog.FadeTilesByPlayer[player], world, "fog fade tile");
                return;
            }

            ValidateCount(replacement, 8, "fog fade tile lists");
            foreach (IReadOnlyList<OriginalSaveIntPair> set in replacement)
                WritePairs(writer, set, world, "fog fade tile");
        }

        static void WritePairs(
            BinaryWriter writer,
            IReadOnlyList<OriginalSaveIntPair> pairs,
            OriginalSaveWorldDocument world,
            string label)
        {
            if (pairs == null) throw new InvalidDataException($"Original {label} list is null.");
            writer.Write(pairs.Count);
            foreach (OriginalSaveIntPair pair in pairs)
            {
                if ((uint)pair.First >= (uint)world.Width || (uint)pair.Second >= (uint)world.Height)
                    throw new InvalidDataException(
                        $"Original {label} ({pair.First}, {pair.Second}) is outside the world.");
                writer.Write(pair.First);
                writer.Write(pair.Second);
            }
        }

        static void ValidateFluidSource(OriginalSaveFluidSourceRecord source)
        {
            // The four volume/height state words are not Finite-gated: 22 corpus saves carry
            // NaN residue in the configured-volume word (+0x14) and the original loads them —
            // the words round-trip verbatim.
            if (source.ObjectId < 2 || source.ObjectId > 0x1FFF ||
                source.FluidType < 0 || source.FluidType > 4 ||
                !Finite(source.PositionX) || !Finite(source.PositionY) || !Finite(source.PositionZ))
                throw new InvalidDataException(
                    $"Original fluid source record (object {source.ObjectId}, " +
                    $"type {source.FluidType}) is invalid.");
        }

        static void ValidateFluidStopper(OriginalSaveFluidStopperRecord stopper)
        {
            if (stopper.ObjectId < 2 || stopper.ObjectId > 0x1FFF ||
                !Finite(stopper.CachedPositionX) || !Finite(stopper.CachedPositionY) ||
                !Finite(stopper.CachedPositionZ) ||
                !Finite(stopper.PermeabilityX) || !Finite(stopper.PermeabilityY) ||
                stopper.PermeabilityX < 0f || stopper.PermeabilityX > 1f ||
                stopper.PermeabilityY < 0f || stopper.PermeabilityY > 1f ||
                stopper.MinX > stopper.MaxX || stopper.MinY > stopper.MaxY)
                throw new InvalidDataException("Original fluid stopper record is invalid.");
        }

        static void ValidateFluidType(byte type)
        {
            if (type > 7) throw new InvalidDataException($"Original fluid type {type} is invalid.");
        }

        private static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        static void ApplyHeaderPatch(
            byte[] payload,
            OriginalSaveArchiveDocument source,
            double? replacementGameTime)
        {
            if (!replacementGameTime.HasValue) return;
            double gameTime = replacementGameTime.Value;
            if (double.IsNaN(gameTime) || double.IsInfinity(gameTime) || gameTime < 0d)
                throw new InvalidDataException("Original game time is invalid.");

            int gameTimeOffset = checked((int)source.Metadata.FixedEngineStateOffset - 12);
            byte[] bytes = BitConverter.GetBytes(gameTime);
            Buffer.BlockCopy(bytes, 0, payload, gameTimeOffset, bytes.Length);
        }

        static void WriteCommonObjectBody(
            BinaryWriter writer,
            OriginalSaveObjectDocument obj,
            OriginalSaveObjectCorePatch? patch)
        {
            WriteNativeString(writer, obj.ClassName);
            writer.Write(obj.ObjectType);
            WriteVector(writer, patch?.Position ?? obj.Position);
            WriteVector(writer, obj.TransientPosition);
            WriteState350(writer, obj.State350, patch);
            WriteCoreState(writer, obj.CoreState, patch);
            WriteVector(writer, patch?.ChildObjectIds ?? obj.ChildObjectIds);
            WriteStringVector(writer, obj.Strings);
            WriteValues(writer, obj.StateAfterStrings, 16, nameof(obj.StateAfterStrings));

            IReadOnlyList<OriginalSaveTclVariable> tclVariables =
                patch?.TclVariables ?? obj.TclVariables;
            bool hasTclState = patch?.TclVariables != null || obj.HasTclState;
            writer.Write(hasTclState ? 1 : 0);
            if (hasTclState)
            {
                writer.Write(tclVariables.Count);
                foreach (OriginalSaveTclVariable variable in tclVariables)
                {
                    WriteNativeString(writer, variable.Name);
                    WriteNativeString(writer, variable.Value);
                }
            }

            writer.Write(patch?.StateObjectId ?? obj.StateObjectId);
            WriteNativeString(writer, patch?.InstanceName ?? obj.InstanceName);

            OriginalSaveObjectCommonStateDocument common = obj.CommonState;
            // +0x200/+0x204/+0x208 live/target/enable state words are patchable (a played
            // session's machines move on); the +0x20C dead word stays source-retained.
            writer.Write(patch?.StateLiveWord ?? common.State200[0]);
            writer.Write(patch?.StateTargetWord ?? common.State200[1]);
            writer.Write(patch?.StateEnabledWord ?? common.State200[2]);
            writer.Write(common.State200[3]);
            WriteState220(writer, common.State220, patch);
            WriteVector(writer, common.RouteValues);
            writer.Write(common.RouteState);
            writer.Write(common.RouteTimeBits);

            writer.Write(common.GridState.Count == 0 ? 0 : 1);
            if (common.GridState.Count != 0)
                WriteValues(writer, common.GridState, 512, nameof(common.GridState));

            WriteValues(writer, common.State264, 2, nameof(common.State264));
            WriteValues(writer, common.State254, 4, nameof(common.State254));
            WriteState26C(writer, common.State26C, patch);
            WriteValues(writer, common.State298, 3, nameof(common.State298));

            writer.Write(common.ParticleState.IsPresent ? (byte)1 : (byte)0);
            if (common.ParticleState.IsPresent)
            {
                WriteValues(
                    writer,
                    common.ParticleState.RawValues,
                    160,
                    nameof(common.ParticleState));
            }

            writer.Write(common.State2B4);
            writer.Write(common.State35C);
            writer.Write(common.State35D);
            WriteNativeString(writer, patch?.DisplayState ?? common.DisplayState);
            writer.Write(patch?.FogOfWarSizeX ?? common.FogOfWarSizeX);
            writer.Write(patch?.FogOfWarSizeY ?? common.FogOfWarSizeY);
            writer.Write(patch?.Owner ?? common.Owner);
            writer.Write(common.State378);
            WriteValues(writer, common.State37C, 8, nameof(common.State37C));
            WriteValues(writer, common.State39C, 5, nameof(common.State39C));
            writer.Write(common.State3B4);
            WriteValues(writer, common.State3BC, 4, nameof(common.State3BC));
            WriteLateCoreState(writer, common.LateCoreState, patch);
            writer.Write(common.TriggerRecordId);
        }

        static void WriteState350(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 3, "State350");
            writer.Write(values[0]);
            writer.Write(patch?.WorkStart.HasValue == true
                ? BitConverter.SingleToInt32Bits(patch.WorkStart.Value)
                : values[1]);
            writer.Write(patch?.WorkLength.HasValue == true
                ? BitConverter.SingleToInt32Bits(patch.WorkLength.Value)
                : values[2]);
        }

        static void WriteCoreState(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 13, "CoreState");
            for (int index = 0; index < values.Count; index++)
            {
                // CoreState word layout: word 0 = motion magnitude, words 1..3 = requested
                // velocity, 4..6 = last-tick delta, 7..9 = current rotation, 10..12 = previous
                // rotation.
                float? replacement = index switch
                {
                    1 => patch?.Velocity?.X,
                    2 => patch?.Velocity?.Y,
                    3 => patch?.Velocity?.Z,
                    7 => patch?.Rotation?.X,
                    8 => patch?.Rotation?.Y,
                    9 => patch?.Rotation?.Z,
                    _ => null,
                };
                writer.Write(replacement.HasValue
                    ? BitConverter.SingleToInt32Bits(replacement.Value)
                    : values[index]);
            }
        }

        static void WriteState220(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 4, "State220");
            for (int index = 0; index < values.Count; index++)
                writer.Write(index == 1 && patch?.PlaceSnapMode.HasValue == true
                    ? patch.PlaceSnapMode.Value
                    : values[index]);
        }

        static void WriteState26C(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 10, "State26C");
            for (int index = 0; index < values.Count; index++)
            {
                int value = index switch
                {
                    0 when patch?.FogOfWarLastX.HasValue == true =>
                        BitConverter.SingleToInt32Bits(patch.FogOfWarLastX.Value),
                    1 when patch?.FogOfWarLastY.HasValue == true =>
                        BitConverter.SingleToInt32Bits(patch.FogOfWarLastY.Value),
                    3 when patch?.LinkedObjectId.HasValue == true => patch.LinkedObjectId.Value,
                    4 when patch?.LinkedSlot.HasValue == true => patch.LinkedSlot.Value,
                    _ => values[index],
                };
                writer.Write(value);
            }
        }

        static void WriteLateCoreState(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            OriginalSaveObjectCorePatch? patch)
        {
            ValidateCount(values, 6, "LateCoreState");
            for (int index = 0; index < values.Count; index++)
            {
                int value = index switch
                {
                    0 when patch?.ObjectFlags.HasValue == true => patch.ObjectFlags.Value,
                    2 when patch?.DirtyFlags.HasValue == true => patch.DirtyFlags.Value,
                    4 when patch?.DigOrderId.HasValue == true => patch.DigOrderId.Value,
                    _ => values[index],
                };
                writer.Write(value);
            }
        }

        static void WriteVector(BinaryWriter writer, OriginalSaveVector3 value)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }

        static void WriteVector(BinaryWriter writer, IReadOnlyList<int> values)
        {
            writer.Write(values.Count);
            WriteValues(writer, values, values.Count, "vector");
        }

        static void WriteStringVector(BinaryWriter writer, IReadOnlyList<string> values)
        {
            writer.Write(values.Count);
            foreach (string value in values) WriteNativeString(writer, value);
        }

        static void WriteValues(
            BinaryWriter writer,
            IReadOnlyList<int> values,
            int expectedCount,
            string label)
        {
            ValidateCount(values, expectedCount, label);
            for (int index = 0; index < values.Count; index++) writer.Write(values[index]);
        }

        static void ValidateCount<T>(IReadOnlyCollection<T> values, int expectedCount, string label)
        {
            if (values.Count != expectedCount)
                throw new InvalidDataException(
                    $"Original object {label} contains {values.Count} values; expected {expectedCount}.");
        }

        static void WriteNativeString(BinaryWriter writer, string value)
        {
            if (value == null) throw new InvalidDataException("Original native string is null.");
            if (value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Original native string contains an embedded NUL.");
            byte[] bytes = Windows1252.GetBytes(value);
            writer.Write(checked(bytes.Length + 1));
            writer.Write(bytes);
            writer.Write((byte)0);
        }
    }

    public sealed class OriginalSaveCurrentArchivePatch
    {
        private readonly Dictionary<int, OriginalSaveObjectCorePatch> _objects = new();

        public double? GameTime { get; set; }
        public OriginalSaveWorldPatch? World { get; set; }
        public OriginalSaveCanonicalCapture? CanonicalCapture { get; set; }

        /// <summary>
        /// Rebuilds the variable-length sections from <see cref="CanonicalCapture"/>.
        /// </summary>
        public bool RebuildStructureSections { get; set; }
        internal IEnumerable<int> ObjectIds => _objects.Keys;

        public void SetObject(OriginalSaveObjectCorePatch patch)
        {
            if (patch == null) throw new ArgumentNullException(nameof(patch));
            if (patch.ObjectId < 2 || patch.ObjectId > 0x1FFF)
                throw new ArgumentOutOfRangeException(nameof(patch));
            _objects[patch.ObjectId] = patch;
        }

        internal bool TryGetObject(int objectId, out OriginalSaveObjectCorePatch? patch)
        {
            return _objects.TryGetValue(objectId, out patch);
        }
    }

    public sealed class OriginalSaveObjectCorePatch
    {
        public OriginalSaveObjectCorePatch(int objectId)
        {
            ObjectId = objectId;
        }

        public int ObjectId { get; }
        public OriginalSaveVector3? Position { get; set; }
        public OriginalSaveVector3? Velocity { get; set; }
        public OriginalSaveVector3? Rotation { get; set; }
        public float? WorkStart { get; set; }
        public float? WorkLength { get; set; }
        public IReadOnlyList<int>? ChildObjectIds { get; set; }
        public IReadOnlyList<OriginalSaveTclVariable>? TclVariables { get; set; }
        public int? StateObjectId { get; set; }
        public string? InstanceName { get; set; }
        public string? DisplayState { get; set; }
        // Native +0x200/+0x204/+0x208 overrides (live/target state-table indices + enable word);
        // +0x20C stays source-retained.
        public int? StateLiveWord { get; set; }
        public int? StateTargetWord { get; set; }
        public int? StateEnabledWord { get; set; }
        public int? FogOfWarSizeX { get; set; }
        public int? FogOfWarSizeY { get; set; }
        public float? FogOfWarLastX { get; set; }
        public float? FogOfWarLastY { get; set; }
        public int? Owner { get; set; }
        public int? LinkedObjectId { get; set; }
        public int? LinkedSlot { get; set; }
        public int? PlaceSnapMode { get; set; }
        public int? ObjectFlags { get; set; }
        public int? DirtyFlags { get; set; }
        public int? DigOrderId { get; set; }
    }
}
