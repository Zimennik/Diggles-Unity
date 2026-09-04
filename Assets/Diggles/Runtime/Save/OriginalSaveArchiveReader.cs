#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using UnityEngine.Scripting;

    public sealed class OriginalSaveArchiveReader
    {
        public const int MinimumRecognizedVersion = 0x86;
        public const int MaximumRecognizedVersion = 0xA5;
        public const int CurrentVersion = 0xA5;

        private const int FixedEngineStateSize = 205;
        private const int TerrainRecordSize = 53;
        private const int MaximumStringLength = 1024 * 1024;
        private const int MaximumTerrainRecords = 4 * 1024 * 1024;
        private const int MaximumSparseRecords = 4 * 1024 * 1024;
        private const int MaximumObjectId = 0x1FFF;
        private const int MaximumObjectDeclarations = MaximumObjectId - 1;
        private const int AttributeSlotCount = 512;
        private const int OwnerAttributeBlockCount = 9;

        [Preserve]
        public OriginalSaveArchiveReader() { }

        public OriginalSaveArchiveMetadata ReadMetadata(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadMetadata(file);
        }

        public OriginalSaveArchiveMetadata ReadMetadata(Stream compressedStream, bool leaveOpen = false)
        {
            if (compressedStream == null) throw new ArgumentNullException(nameof(compressedStream));
            using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen);
            return ReadDecompressedMetadata(gzip);
        }

        internal OriginalSaveArchiveMetadata ReadDecompressedMetadata(Stream decompressedStream)
        {
            if (decompressedStream == null) throw new ArgumentNullException(nameof(decompressedStream));
            var archive = new PackedArchiveReader(decompressedStream);

            int version = archive.ReadInt32();
            if (version < MinimumRecognizedVersion || version > MaximumRecognizedVersion)
            {
                throw new InvalidDataException(
                    $"Invalid original save version 0x{version:X}. The original loader recognizes 0x{MinimumRecognizedVersion:X}..0x{MaximumRecognizedVersion:X}.");
            }
            if (version != CurrentVersion)
                throw new NotSupportedException(
                    $"Original save version 0x{version:X} is recognized but its version-specific layout is not implemented. Supported version: 0x{CurrentVersion:X}.");

            string headerText = archive.ReadNativeString(MaximumStringLength);
            double gameTime = archive.ReadDouble();
            ExpectMarker(archive, 1);

            long fixedEngineStateOffset = archive.Position;
            archive.SkipExactly(FixedEngineStateSize);
            ExpectMarker(archive, 2);
            ExpectMarker(archive, 3);

            long worldSectionOffset = archive.Position;
            int width = archive.ReadInt32();
            int height = archive.ReadInt32();
            int minX = archive.ReadInt32();
            int minY = archive.ReadInt32();
            int maxXExclusive = archive.ReadInt32();
            int maxYExclusive = archive.ReadInt32();

            int terrainRecordCount = CheckedProduct(
                width, height, MaximumTerrainRecords, "terrain record");
            archive.SkipExactly(checked((long)terrainRecordCount * TerrainRecordSize));

            archive.SkipExactly(40);

            int fluidPrimaryCount = ReadCount(archive, "primary fluid record");
            archive.SkipExactly(checked((long)fluidPrimaryCount * 37));
            int fluidSecondaryCount = ReadCount(archive, "secondary fluid record");
            archive.SkipExactly(checked((long)fluidSecondaryCount * 41));
            archive.SkipExactly(8);

            var fluidSparseCounts = new int[4];
            for (int i = 0; i < fluidSparseCounts.Length; i++)
            {
                fluidSparseCounts[i] = ReadCount(archive, "fluid sparse record");
                archive.SkipExactly(checked((long)fluidSparseCounts[i] * 8));
            }
            archive.ReadByte();

            var fogSparseCounts = new int[8];
            for (int i = 0; i < fogSparseCounts.Length; i++)
            {
                fogSparseCounts[i] = ReadCount(archive, "fog-of-war sparse record");
                archive.SkipExactly(checked((long)fogSparseCounts[i] * 8));
            }

            long marker4Offset = archive.Position;
            ExpectMarker(archive, 4);

            OriginalSaveObjectManifest objects = ReadCurrentObjectManifest(archive, width, height);
            OriginalSaveControlMetadata control = ReadCurrentControlMetadata(archive);
            OriginalSaveGraphicsMetadata graphics = ReadCurrentGraphicsMetadata(archive);
            OriginalSaveLogicMetadata logic = ReadCurrentLogicMetadata(archive);

            var world = new OriginalSaveWorldMetadata(
                worldSectionOffset,
                marker4Offset,
                width,
                height,
                minX,
                minY,
                maxXExclusive,
                maxYExclusive,
                terrainRecordCount,
                fluidPrimaryCount,
                fluidSecondaryCount,
                fluidSparseCounts,
                fogSparseCounts);
            return new OriginalSaveArchiveMetadata(
                version,
                headerText,
                gameTime,
                fixedEngineStateOffset,
                world,
                objects,
                control,
                graphics,
                logic);
        }

        static OriginalSaveObjectManifest ReadCurrentObjectManifest(
            PackedArchiveReader archive, int worldWidth, int worldHeight)
        {
            ExpectMarker(archive, 5);
            ExpectMarker(archive, 6);

            long declarationsOffset = archive.Position;
            var declarations = new List<OriginalSaveObjectDeclaration>();
            var declaredIds = new HashSet<int>();
            var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int previousId = 1;
            long declarationTerminatorOffset;

            while (true)
            {
                long idOffset = archive.Position;
                int objectId = archive.ReadInt32();
                if (objectId == -1)
                {
                    declarationTerminatorOffset = idOffset;
                    break;
                }

                if (objectId < 2 || objectId > MaximumObjectId)
                    throw new InvalidDataException($"Invalid original object ID {objectId} at decompressed offset {idOffset}.");
                if (objectId <= previousId || !declaredIds.Add(objectId))
                    throw new InvalidDataException($"Original object declarations are not strictly ordered at ID {objectId}.");
                if (declarations.Count >= MaximumObjectDeclarations)
                    throw new InvalidDataException($"The save contains more than {MaximumObjectDeclarations} object declarations.");

                string className = archive.ReadNativeString(MaximumStringLength);
                if (className.Length == 0)
                    throw new InvalidDataException($"Original object {objectId} has an empty class name.");

                declarations.Add(new OriginalSaveObjectDeclaration(objectId, className));
                classCounts.TryGetValue(className, out int classCount);
                classCounts[className] = classCount + 1;
                previousId = objectId;
            }

            long separatorOffset = archive.Position;
            string separatorText = archive.ReadNativeString(MaximumStringLength);

            long supportSectionOffset = archive.Position;
            int supportRecordCount = ReadCount(
                archive, "object support record", MaximumSparseRecords);
            for (int i = 0; i < supportRecordCount; i++)
            {
                archive.SkipExactly(28);
                archive.SkipExactly(32);
                archive.SkipExactly(16);
                archive.SkipExactly(36);
                archive.ReadNativeString(MaximumStringLength);
                archive.ReadNativeString(MaximumStringLength);
                archive.SkipExactly(20);
            }

            long bodySectionOffset = archive.Position;
            int firstBodyObjectId = archive.ReadInt32();
            if (firstBodyObjectId != -1 && !declaredIds.Contains(firstBodyObjectId))
            {
                throw new InvalidDataException(
                    $"The first original object body references undeclared ID {firstBodyObjectId} at decompressed offset {bodySectionOffset}.");
            }

            var bodies = ReadCurrentObjectBodies(
                archive, declarations, firstBodyObjectId);
            long bodyStreamEndOffset = archive.Position;

            OriginalSaveOwnerAttributeTableDocument ownerAttributeTables =
                ReadCurrentObjectManagerTail(archive);
            ReadCurrentClassTable(
                archive,
                worldWidth,
                worldHeight,
                out int classTableEntryCount,
                out int spatialBucketCount,
                out int spatialRecordCount,
                out long spatialIndexOffset);
            long marker7Offset = archive.Position;
            ExpectMarker(archive, 7);

            return new OriginalSaveObjectManifest(
                declarationsOffset,
                declarationTerminatorOffset,
                separatorOffset,
                separatorText,
                supportSectionOffset,
                supportRecordCount,
                bodySectionOffset,
                firstBodyObjectId,
                bodyStreamEndOffset,
                classTableEntryCount,
                spatialBucketCount,
                spatialRecordCount,
                spatialIndexOffset,
                marker7Offset,
                declarations,
                bodies,
                classCounts,
                ownerAttributeTables);
        }

        static OriginalSaveControlMetadata ReadCurrentControlMetadata(PackedArchiveReader archive)
        {
            long controlStateSectionOffset = archive.Position;
            int controlStateEntryCount = 0;
            int previousIndex = -1;
            int index = archive.ReadInt32();
            while (index != -1)
            {
                if (index <= previousIndex || index >= 0x800)
                    throw new InvalidDataException($"Invalid ordered control-state index {index}.");

                SkipInt32(archive, 9);
                previousIndex = index;
                controlStateEntryCount++;
                index = archive.ReadInt32();
            }

            long marker8Offset = archive.Position;
            ExpectMarker(archive, 8);
            long controlRecordSectionOffset = archive.Position;
            int controlRecordCount = ReadCount(archive, "control record");
            int nestedControlRecordCount = 0;
            for (int record = 0; record < controlRecordCount; record++)
            {
                SkipInt32(archive, 3);
                int nestedCount = ReadCount(archive, "nested control record");
                nestedControlRecordCount = checked(nestedControlRecordCount + nestedCount);
                SkipInt32(archive, checked(nestedCount * 3));
            }

            long marker50Offset = archive.Position;
            ExpectMarker(archive, 0x50);
            return new OriginalSaveControlMetadata(
                controlStateSectionOffset,
                controlStateEntryCount,
                marker8Offset,
                controlRecordSectionOffset,
                controlRecordCount,
                nestedControlRecordCount,
                marker50Offset);
        }

        static OriginalSaveGraphicsMetadata ReadCurrentGraphicsMetadata(PackedArchiveReader archive)
        {
            long animationControllerSectionOffset = archive.Position;
            int animationControllerCount = 0;
            int animationVectorValueCount = 0;
            int previousIndex = -1;
            int index = archive.ReadInt32();
            while (index != -1)
            {
                if (index <= previousIndex || index > MaximumObjectId)
                    throw new InvalidDataException($"Invalid ordered animation-controller index {index}.");

                archive.SkipExactly(1);
                archive.SkipExactly(8);
                SkipInt32(archive, 2);
                int valueCount = ReadCount(archive, "animation controller value");
                animationVectorValueCount = checked(animationVectorValueCount + valueCount);
                SkipInt32(archive, valueCount);
                SkipInt32(archive, 5);
                previousIndex = index;
                animationControllerCount++;
                index = archive.ReadInt32();
            }

            long marker9Offset = archive.Position;
            ExpectMarker(archive, 9);
            long graphicsEntrySectionOffset = archive.Position;
            int graphicsEntryCount = 0;
            previousIndex = -1;
            index = archive.ReadInt32();
            while (index != -1)
            {
                if (index <= previousIndex || index >= 0x1000)
                    throw new InvalidDataException($"Invalid ordered graphics-entry index {index}.");

                SkipInt32(archive, 9);
                archive.SkipExactly(1);
                SkipInt32(archive, 6);
                previousIndex = index;
                graphicsEntryCount++;
                index = archive.ReadInt32();
            }

            long marker10Offset = archive.Position;
            ExpectMarker(archive, 10);
            return new OriginalSaveGraphicsMetadata(
                animationControllerSectionOffset,
                animationControllerCount,
                animationVectorValueCount,
                marker9Offset,
                graphicsEntrySectionOffset,
                graphicsEntryCount,
                marker10Offset);
        }

        static OriginalSaveLogicMetadata ReadCurrentLogicMetadata(PackedArchiveReader archive)
        {
            long firstManagerSectionOffset = archive.Position;
            int stringRecordCount = ReadCount(archive, "logic string record");
            for (int record = 0; record < stringRecordCount; record++)
            {
                archive.ReadNativeString(MaximumStringLength);
                archive.ReadNativeString(MaximumStringLength);
                archive.ReadNativeString(MaximumStringLength);
                archive.SkipExactly(1);
            }

            int descriptorCount = SkipLogicDescriptorTable(archive);
            int nestedRecordCount = ReadCount(archive, "logic nested record");
            for (int record = 0; record < nestedRecordCount; record++)
                SkipLogicNestedRecord(archive);

            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 11);

            int pairRecordCount = ReadCount(archive, "logic string-pair record");
            for (int record = 0; record < pairRecordCount; record++)
            {
                archive.ReadNativeString(MaximumStringLength);
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
            }

            SkipInt32(archive, ReadCount(archive, "logic raw value"));
            SkipInt32(archive, 2);

            int namedValueCount = ReadCount(archive, "logic named value");
            for (int value = 0; value < namedValueCount; value++)
            {
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
            }

            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 5);
            archive.SkipExactly(1);
            SkipInt32(archive, 1);
            archive.SkipExactly(1);
            SkipInt32(archive, 1);

            long marker11Offset = archive.Position;
            ExpectMarker(archive, 11);
            long matrixSectionOffset = archive.Position;
            int matrixValueCount = 0;
            for (int cell = 0; cell < 9 * 9; cell++)
            {
                int valueCount = ReadCount(archive, "logic matrix vector value");
                matrixValueCount = checked(matrixValueCount + valueCount);
                SkipInt32(archive, valueCount);
            }

            long marker12Offset = archive.Position;
            ExpectMarker(archive, 12);
            long actionSectionOffset = archive.Position;
            ReadActionTable(
                archive,
                out int actionCount,
                out Dictionary<int, int> actionTypeCounts,
                out int auxiliaryRecordCount,
                out int auxiliaryVectorRecordCount);
            long marker13Offset = archive.Position;
            ExpectMarker(archive, 13);

            int globalPairRecordCount = ReadCount(archive, "logic global pair record");
            SkipInt32(archive, checked(globalPairRecordCount * 2));
            long marker14Offset = archive.Position;
            ExpectMarker(archive, 14);

            ReadSchedulerTable(
                archive,
                out int schedulerSlotCount,
                out int schedulerRecordCount);
            long marker15Offset = archive.Position;
            ExpectMarker(archive, 15);

            int eightValueRecordCount = ReadCount(archive, "logic eight-value record");
            SkipInt32(archive, checked(eightValueRecordCount * 8));
            long marker16Offset = archive.Position;
            ExpectMarker(archive, 16);

            int namedRecordCount = ReadCount(archive, "logic named record");
            for (int record = 0; record < namedRecordCount; record++)
            {
                SkipInt32(archive, 4);
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
            }
            long marker17Offset = archive.Position;
            ExpectMarker(archive, 17);

            ReadNewsTickerState(
                archive,
                out int newsTickerActiveCount,
                out int newsTickerHistoryCount,
                out int newsTickerDisplayQueueCount);
            long newsTickerStateEndOffset = archive.Position;
            long objectIconStateOffset = archive.Position;
            SkipInt32(archive, 128 * 7);
            long objectIconStateEndOffset = archive.Position;
            long caveRecordStateOffset = archive.Position;
            int lateFiveValueRecordCount = ReadCount(archive, "late five-value record");
            SkipInt32(archive, checked(lateFiveValueRecordCount * 5));
            long caveRecordStateEndOffset = archive.Position;
            long aiOwnerStateOffset = archive.Position;
            for (int owner = 0; owner < 8; owner++)
                SkipAiOwnerState(archive);
            long aiOwnerStateEndOffset = archive.Position;

            long elfStateOffset = archive.Position;
            SkipInt32(archive, 17);
            archive.SkipExactly(2);
            SkipInt32(archive, 2);
            long elfStateEndOffset = archive.Position;
            long reservedPlayerFramingOffset = archive.Position;
            SkipInt32(archive, 32);
            long reservedPlayerFramingEndOffset = archive.Position;
            long playerStateOffset = archive.Position;
            for (int owner = 0; owner < 8; owner++)
                SkipLateOwnerState(archive);
            long playerStateEndOffset = archive.Position;

            long marker18Offset = archive.Position;
            ExpectMarker(archive, 18);
            long finalBlockOffset = archive.Position;
            archive.SkipExactly(0x1000);
            archive.ExpectEndOfStream();

            return new OriginalSaveLogicMetadata(
                firstManagerSectionOffset,
                stringRecordCount,
                descriptorCount,
                nestedRecordCount,
                marker11Offset,
                matrixSectionOffset,
                matrixValueCount,
                marker12Offset,
                actionSectionOffset,
                actionCount,
                actionTypeCounts,
                auxiliaryRecordCount,
                auxiliaryVectorRecordCount,
                marker13Offset,
                globalPairRecordCount,
                marker14Offset,
                schedulerSlotCount,
                schedulerRecordCount,
                marker15Offset,
                eightValueRecordCount,
                marker16Offset,
                namedRecordCount,
                marker17Offset,
                newsTickerActiveCount,
                newsTickerHistoryCount,
                newsTickerDisplayQueueCount,
                newsTickerStateEndOffset,
                objectIconStateOffset,
                objectIconStateEndOffset,
                lateFiveValueRecordCount,
                caveRecordStateOffset,
                caveRecordStateEndOffset,
                aiOwnerStateOffset,
                aiOwnerStateEndOffset,
                elfStateOffset,
                elfStateEndOffset,
                reservedPlayerFramingOffset,
                reservedPlayerFramingEndOffset,
                playerStateOffset,
                playerStateEndOffset,
                marker18Offset,
                finalBlockOffset,
                archive.Position);
        }

        static void ReadSchedulerTable(
            PackedArchiveReader archive,
            out int slotCount,
            out int recordCount)
        {
            slotCount = ReadCount(archive, "scheduler slot");
            recordCount = 0;
            for (int slot = 0; slot < slotCount; slot++)
            {
                int presence = archive.ReadInt32();
                if (presence == 0)
                    continue;

                SkipInt32(archive, 4);
                archive.ReadNativeString(MaximumStringLength);
                archive.SkipExactly(8 * 4);
                archive.SkipExactly(1);
                SkipInt32(archive, 1);
                archive.SkipExactly(8 * 2);
                SkipInt32(archive, 1);
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
                archive.ReadNativeString(MaximumStringLength);
                archive.ReadNativeString(MaximumStringLength);
                recordCount++;
            }
        }

        static void ReadNewsTickerState(
            PackedArchiveReader archive,
            out int activeCount,
            out int historyCount,
            out int displayQueueCount)
        {
            activeCount = ReadCount(archive, "news-ticker active record");
            for (int record = 0; record < activeCount; record++)
            {
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 8);
                archive.SkipExactly(2);
                archive.ReadNativeString(MaximumStringLength);
                archive.SkipExactly(8);
                archive.SkipExactly(1);
            }

            historyCount = ReadCount(archive, "news-ticker history record");
            for (int record = 0; record < historyCount; record++)
            {
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 2);
            }

            SkipInt32(archive, 1);
            displayQueueCount = ReadCount(archive, "news-ticker display-queue record");
            SkipInt32(archive, checked(displayQueueCount * 2));
        }

        static void SkipAiOwnerState(PackedArchiveReader archive)
        {
            archive.SkipExactly(1);
            SkipInt32(archive, 1);
            archive.ReadNativeString(MaximumStringLength);
            SkipTclVariables(archive);
        }

        static void SkipLateOwnerState(PackedArchiveReader archive)
        {
            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 3);
            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 8);
        }

        static int SkipLogicDescriptorTable(PackedArchiveReader archive)
        {
            int descriptorCount = ReadCount(archive, "logic descriptor");
            for (int descriptor = 0; descriptor < descriptorCount; descriptor++)
            {
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 2);
                archive.SkipExactly(1);
                SkipInt32(archive, ReadCount(archive, "logic descriptor vector value"));
                SkipInt32(archive, 8);
                archive.SkipExactly(1);
                SkipInt32(archive, 6);
                archive.SkipExactly(1);
                SkipInt32(archive, 3);
            }
            return descriptorCount;
        }

        static void SkipLogicNestedRecord(PackedArchiveReader archive)
        {
            archive.ReadNativeString(MaximumStringLength);
            int valueCount = ReadCount(archive, "logic nested value");
            for (int value = 0; value < valueCount; value++)
            {
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 4);
                archive.SkipExactly(1);
            }

            SkipInt32(archive, 3);
            SkipInt32(archive, checked(ReadCount(archive, "logic nested four-value record") * 4));
            SkipInt32(archive, checked(ReadCount(archive, "logic nested five-value record") * 5));
        }

        static void ReadActionTable(
            PackedArchiveReader archive,
            out int actionCount,
            out Dictionary<int, int> actionTypeCounts,
            out int auxiliaryRecordCount,
            out int auxiliaryVectorRecordCount)
        {
            actionCount = 0;
            actionTypeCounts = new Dictionary<int, int>();
            var ownerObjectIds = new HashSet<int>();
            int ownerObjectId = archive.ReadInt32();
            while (ownerObjectId != -1)
            {
                if (ownerObjectId < 0 || ownerObjectId > MaximumObjectId || !ownerObjectIds.Add(ownerObjectId))
                    throw new InvalidDataException($"Invalid action owner object ID {ownerObjectId}.");

                int actionType = archive.ReadInt32();
                if (actionType < 0 || actionType > 11)
                    throw new InvalidDataException($"Invalid action type {actionType} for object ID {ownerObjectId}.");

                SkipActionPayload(archive, actionType);
                actionTypeCounts.TryGetValue(actionType, out int typeCount);
                actionTypeCounts[actionType] = typeCount + 1;
                actionCount++;
                ownerObjectId = archive.ReadInt32();
            }

            ExpectValue(archive, 0x626C6133, "action table sentinel");
            auxiliaryRecordCount = ReadCount(archive, "action auxiliary record");
            auxiliaryVectorRecordCount = 0;
            for (int record = 0; record < auxiliaryRecordCount; record++)
            {
                SkipInt32(archive, 1);
                int vectorRecordCount = ReadCount(archive, "action auxiliary vector record");
                auxiliaryVectorRecordCount = checked(auxiliaryVectorRecordCount + vectorRecordCount);
                SkipInt32(archive, checked(vectorRecordCount * 8));
                SkipInt32(archive, 3);
            }
            ExpectValue(archive, 0x626C6134, "action auxiliary table sentinel");
        }

        static void SkipActionCommonState(PackedArchiveReader archive)
        {
            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 1);
            archive.ReadNativeString(MaximumStringLength);
            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 1);
        }

        static void SkipActionPayload(PackedArchiveReader archive, int actionType)
        {
            switch (actionType)
            {
                case 0:
                case 2:
                    return;
                case 1:
                case 3:
                    SkipActionCommonState(archive);
                    archive.SkipExactly(8);
                    return;
                case 4:
                    SkipActionCommonState(archive);
                    SkipInt32(archive, 2);
                    return;
                case 5:
                    SkipActionCommonState(archive);
                    SkipInt32(archive, 7);
                    return;
                case 6:
                    archive.ReadNativeString(MaximumStringLength);
                    archive.ReadNativeString(MaximumStringLength);
                    SkipInt32(archive, 5);
                    archive.SkipExactly(8 + 3);
                    SkipInt32(archive, 1);
                    archive.SkipExactly(4);
                    SkipInt32(archive, 5);
                    archive.SkipExactly(1);
                    SkipActionCommonState(archive);
                    return;
                case 7:
                    SkipInt32(archive, 2);
                    SkipInt32(archive, ReadCount(archive, "ballistic action first vector value"));
                    SkipInt32(archive, 2);
                    SkipInt32(archive, ReadCount(archive, "ballistic action second vector value"));
                    SkipInt32(archive, 2);
                    archive.SkipExactly(1);
                    SkipInt32(archive, 3);
                    archive.SkipExactly(2);
                    SkipInt32(archive, 1);
                    archive.SkipExactly(8);
                    SkipActionCommonState(archive);
                    return;
                case 8:
                    SkipActionCommonState(archive);
                    SkipInt32(archive, 12);
                    archive.SkipExactly(1);
                    SkipInt32(archive, 1);
                    archive.SkipExactly(1);
                    return;
                case 9:
                    SkipActionCommonState(archive);
                    SkipInt32(archive, 2);
                    SkipInt32(archive, checked(ReadCount(archive, "lore action target") * 2));
                    SkipInt32(archive, checked(ReadCount(archive, "lore action table record") * 8));
                    return;
                case 10:
                    SkipActionCommonState(archive);
                    for (int group = 0; group < 4; group++)
                        SkipInt32(archive, 1 + 24 * 5);
                    SkipInt32(archive, 1);
                    archive.SkipExactly(2);
                    SkipInt32(archive, 4);
                    archive.SkipExactly(2);
                    SkipInt32(archive, 2);
                    archive.SkipExactly(4);
                    SkipInt32(archive, 6);
                    archive.SkipExactly(1);
                    SkipInt32(archive, 4);
                    archive.ReadNativeString(MaximumStringLength);
                    archive.ReadNativeString(MaximumStringLength);
                    archive.ReadNativeString(MaximumStringLength);
                    return;
                case 11:
                    SkipActionCommonState(archive);
                    SkipInt32(archive, 10);
                    return;
                default:
                    throw new InvalidDataException($"Unsupported current action type {actionType}.");
            }
        }

        static OriginalSaveOwnerAttributeTableDocument ReadCurrentObjectManagerTail(
            PackedArchiveReader archive)
        {
            long tailOffset = archive.Position;
            try
            {
                var blocks = new OriginalSaveOwnerAttributeBlockDocument[OwnerAttributeBlockCount];
                for (int block = 0; block < blocks.Length; block++)
                {
                    int owner = block - 1;
                    long primaryOffset = archive.Position;
                    int[] primaryValueBits = ReadInt32Values(archive, AttributeSlotCount);
                    long secondaryOffset = archive.Position;
                    int[] secondaryValueBits = ReadInt32Values(archive, AttributeSlotCount);
                    blocks[block] = new OriginalSaveOwnerAttributeBlockDocument(
                        owner,
                        primaryOffset,
                        secondaryOffset,
                        primaryValueBits,
                        secondaryValueBits);
                }

                var tables = new OriginalSaveOwnerAttributeTableDocument(tailOffset, blocks);
                SkipInt32(archive, 2);
                for (int group = 0; group < 3; group++)
                {
                    SkipObjectManagerArray(archive);
                    SkipObjectManagerArray(archive);
                    int valueCount = ReadCount(archive, "object manager value");
                    SkipInt32(archive, valueCount);
                    archive.SkipExactly(2);
                }

                int actionRecordCount = ReadCount(archive, "object manager action record");
                if (actionRecordCount != 0)
                {
                    throw new InvalidDataException(
                        $"The save contains {actionRecordCount} class-specific object-manager action records, which are not mapped yet.");
                }

                return tables;
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException(
                    $"The original object-manager tail at decompressed offset {tailOffset} " +
                    $"exceeded the payload at offset {archive.Position}.",
                    exception);
            }
        }

        static int[] ReadInt32Values(PackedArchiveReader archive, int count)
        {
            if (count < 0)
                throw new InvalidDataException($"Invalid Int32 value count {count}.");
            var values = new int[count];
            for (int index = 0; index < values.Length; index++)
                values[index] = archive.ReadInt32();
            return values;
        }

        static void SkipObjectManagerArray(PackedArchiveReader archive)
        {
            int count = ReadCount(archive, "object manager array");
            for (int i = 0; i < count; i++)
            {
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 7);
                archive.SkipExactly(3);
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
            }
        }

        static void ReadCurrentClassTable(
            PackedArchiveReader archive,
            int worldWidth,
            int worldHeight,
            out int entryCount,
            out int spatialBucketCount,
            out int spatialRecordCount,
            out long spatialIndexOffset)
        {
            SkipInt32(archive, 1);
            SkipInt32(archive, 0x3000);
            SkipInt32(archive, 1);
            archive.SkipExactly(1);

            int freeSlotCount = ReadCount(archive, "class table free slot");
            SkipInt32(archive, freeSlotCount);
            int occupiedSlotCount = ReadCount(archive, "class table occupied slot");
            SkipInt32(archive, occupiedSlotCount);

            entryCount = 0;
            int previousIndex = -1;
            int index = archive.ReadInt32();
            while (index != -1)
            {
                if (index <= previousIndex || index >= 0x3000)
                    throw new InvalidDataException($"Invalid ordered class-table entry index {index}.");
                SkipClassTableEntry(archive);
                previousIndex = index;
                entryCount++;
                index = archive.ReadInt32();
            }

            spatialBucketCount = CheckedProduct(
                checked((worldWidth + 7) / 8),
                checked((worldHeight + 7) / 8),
                MaximumSparseRecords,
                "spatial bucket");
            // First byte of the spatial-index buckets (marker-7 precursor); B3 §3 identifies
            // these as the dormant scenery-seed records (FUN_005c6cf0). OriginalSaveSpatialSeedDocument
            // decodes them from here for load-time materialization.
            spatialIndexOffset = archive.Position;
            spatialRecordCount = 0;
            for (int bucket = 0; bucket < spatialBucketCount; bucket++)
            {
                int recordCount = ReadCount(archive, "spatial index record");
                spatialRecordCount = checked(spatialRecordCount + recordCount);
                for (int record = 0; record < recordCount; record++)
                    SkipSpatialIndexRecord(archive);
            }
        }

        static void SkipClassTableEntry(PackedArchiveReader archive)
        {
            SkipInt32(archive, 16);
            SkipInt32(archive, 4);
            int routeCount = ReadCount(archive, "class table route");
            SkipInt32(archive, routeCount);
            SkipInt32(archive, 2);
            archive.ReadNativeString(MaximumStringLength);
            archive.SkipExactly(1);
            SkipInt32(archive, 4);
            archive.SkipExactly(2);
            SkipInt32(archive, 10);
            archive.ReadNativeString(MaximumStringLength);
            archive.SkipExactly(3);
            SkipInt32(archive, 1);
            SkipInt32(archive, 6);
            archive.SkipExactly(1);
        }

        static void SkipSpatialIndexRecord(PackedArchiveReader archive)
        {
            SkipInt32(archive, 6);
            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 4);
            archive.SkipExactly(2);
        }

        static List<OriginalSaveObjectBodySummary> ReadCurrentObjectBodies(
            PackedArchiveReader archive,
            List<OriginalSaveObjectDeclaration> declarations,
            int firstBodyObjectId)
        {
            var bodies = new List<OriginalSaveObjectBodySummary>(declarations.Count);
            int bodyObjectId = firstBodyObjectId;
            int declarationIndex = 0;

            while (bodyObjectId != -1)
            {
                if (declarationIndex >= declarations.Count)
                    throw new InvalidDataException($"Unexpected extra original object body ID {bodyObjectId}.");

                OriginalSaveObjectDeclaration declaration = declarations[declarationIndex];
                if (bodyObjectId != declaration.ObjectId)
                {
                    throw new InvalidDataException(
                        $"Expected original object body ID {declaration.ObjectId}, found {bodyObjectId} at decompressed offset {archive.Position - 4}.");
                }

                long bodyOffset = archive.Position;
                string bodyClassName = archive.ReadNativeString(MaximumStringLength);
                if (!string.Equals(bodyClassName, declaration.ClassName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Original object {bodyObjectId} body class '{bodyClassName}' does not match declaration '{declaration.ClassName}'.");
                }

                int objectType = archive.ReadInt32();
                if (objectType < -1 || objectType > 12)
                    throw new InvalidDataException($"Invalid original object type {objectType} for ID {bodyObjectId}.");

                SkipCurrentObjectBody(archive, objectType);
                bodies.Add(new OriginalSaveObjectBodySummary(
                    bodyObjectId,
                    bodyClassName,
                    objectType,
                    bodyOffset,
                    archive.Position - bodyOffset));

                declarationIndex++;
                bodyObjectId = archive.ReadInt32();
            }

            if (declarationIndex != declarations.Count)
            {
                throw new InvalidDataException(
                    $"Original object body stream ended before declared ID {declarations[declarationIndex].ObjectId}.");
            }

            return bodies;
        }

        static void SkipCurrentObjectBody(PackedArchiveReader archive, int objectType)
        {
            SkipInt32(archive, 6);
            SkipInt32(archive, 3);
            SkipInt32(archive, 13);

            int childReferenceCount = ReadCount(archive, "object child reference");
            SkipInt32(archive, childReferenceCount);
            SkipStringVector(archive, "object string");
            SkipInt32(archive, 16);
            SkipTclVariables(archive);
            SkipInt32(archive, 1);
            archive.ReadNativeString(MaximumStringLength);

            SkipInt32(archive, 4);
            SkipInt32(archive, 4);
            SkipRouteVector(archive);

            if (ReadPresenceInt32(archive, "object grid") != 0)
                SkipInt32(archive, 512);

            SkipInt32(archive, 2);
            SkipInt32(archive, 4);
            SkipInt32(archive, 10);
            SkipInt32(archive, 3);

            if (ReadPresenceByte(archive, "object navigation state") != 0)
                SkipInt32(archive, 160);

            archive.SkipExactly(3);
            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 4);
            SkipInt32(archive, 8);
            SkipInt32(archive, 5);
            archive.SkipExactly(1);
            SkipInt32(archive, 4);
            SkipInt32(archive, 6);
            SkipInt32(archive, 1);

            if (objectType == 1 || objectType == 11)
                SkipActorLinkGroup(archive);
            if (objectType == 1)
                SkipNamedIndex(archive);
            if (objectType == 5 || objectType == 6 || objectType == 7 || objectType == 10)
                SkipMobileState(archive);
            if (objectType == 1)
                SkipInt32(archive, 4);

            if (ReadPresenceByte(archive, "attachment history") != 0)
                SkipAttachmentHistory(archive);

            SkipInt32(archive, 16);
            archive.SkipExactly(16);
            SkipInt32(archive, 16);
            archive.SkipExactly(1);
            SkipInt32(archive, 1);
            archive.SkipExactly(1);

            if (ReadPresenceByte(archive, "action variant") != 0)
                SkipActionVariant(archive);

            SkipExtendedSupportTable(archive);
            SkipInt32(archive, 1);
            archive.SkipExactly(1);
        }

        static void SkipTclVariables(PackedArchiveReader archive)
        {
            int present = ReadPresenceInt32(archive, "Tcl interpreter");
            if (present == 0) return;

            int variableCount = ReadCount(archive, "Tcl variable");
            for (int i = 0; i < variableCount; i++)
            {
                archive.ReadNativeString(MaximumStringLength);
                archive.ReadNativeString(MaximumStringLength);
            }
        }

        static void SkipRouteVector(PackedArchiveReader archive)
        {
            int nodeCount = ReadCount(archive, "route node");
            SkipInt32(archive, nodeCount);
            SkipInt32(archive, 2);
        }

        static void SkipStringVector(PackedArchiveReader archive, string label)
        {
            int count = ReadCount(archive, label);
            for (int i = 0; i < count; i++)
                archive.ReadNativeString(MaximumStringLength);
        }

        static void SkipActorLinkGroup(PackedArchiveReader archive)
        {
            SkipInt32(archive, 9);
            SkipStringVector(archive, "actor link string");
            ReadPresenceByte(archive, "actor link flag");
        }

        static void SkipNamedIndex(PackedArchiveReader archive)
        {
            SkipInt32(archive, 1);
            int sentinel = archive.ReadInt32();
            while (sentinel != -1)
            {
                if (sentinel != 1)
                    throw new InvalidDataException($"Invalid named-index sentinel {sentinel} at decompressed offset {archive.Position - 4}.");
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 1);
                sentinel = archive.ReadInt32();
            }
        }

        static void SkipMobileState(PackedArchiveReader archive)
        {
            int entryCount = ReadCount(archive, "mobile state entry");
            for (int i = 0; i < entryCount; i++)
            {
                SkipInt32(archive, 2);
                archive.ReadNativeString(MaximumStringLength);
                archive.ReadNativeString(MaximumStringLength);
                SkipInt32(archive, 2);
                archive.SkipExactly(2);
            }

            SkipInt32(archive, 2);
            archive.SkipExactly(7);
            SkipInt32(archive, 3);
            archive.SkipExactly(8);
            SkipInt32(archive, 4);
            archive.SkipExactly(1);
            SkipInt32(archive, 5);
            SkipInt32(archive, 16 * 3);
            archive.SkipExactly(2);
        }

        static void SkipAttachmentHistory(PackedArchiveReader archive)
        {
            int historyCount = ReadCount(archive, "attachment history");
            for (int i = 0; i < historyCount; i++)
                SkipRouteVector(archive);
        }

        static void SkipActionVariant(PackedArchiveReader archive)
        {
            int variant = archive.ReadInt32();
            switch (variant)
            {
                case 0:
                    SkipInt32(archive, 8);
                    int entryCount = ReadCount(archive, "action variant zero entry");
                    SkipInt32(archive, checked(entryCount * 10));
                    SkipInt32(archive, 3);
                    SkipInt32(archive, 16 * 3);
                    SkipInt32(archive, 4);
                    SkipInt32(archive, 3);
                    SkipInt32(archive, 8);
                    ReadPresenceByte(archive, "action variant zero flag");
                    break;

                case 1:
                    SkipInt32(archive, 3);
                    SkipInt32(archive, 2);
                    SkipInt32(archive, 2);
                    ReadPresenceByte(archive, "action variant one flag");
                    SkipInt32(archive, 2);
                    break;

                case 2:
                    SkipInt32(archive, 3);
                    SkipInt32(archive, 3);
                    SkipInt32(archive, 1);
                    ReadPresenceByte(archive, "action variant two flag");
                    SkipInt32(archive, 1);
                    int valueCount = ReadCount(archive, "action variant two value");
                    SkipInt32(archive, valueCount);
                    SkipStringVector(archive, "action variant two string");
                    SkipInt32(archive, 1);
                    break;

                default:
                    throw new InvalidDataException($"Invalid action variant {variant} at decompressed offset {archive.Position - 4}.");
            }
        }

        static void SkipExtendedSupportTable(PackedArchiveReader archive)
        {
            SkipInt32(archive, 2);
            for (int i = 0; i < 64; i++)
            {
                if (ReadPresenceByte(archive, "extended support record") != 0)
                    SkipSupportPayload(archive);
            }

            int recordCount = ReadCount(archive, "extended support record");
            for (int i = 0; i < recordCount; i++)
                SkipSupportPayload(archive);
        }

        static void SkipSupportPayload(PackedArchiveReader archive)
        {
            archive.SkipExactly(32);
            SkipInt32(archive, 4);
            SkipInt32(archive, 9);
            archive.ReadNativeString(MaximumStringLength);
            archive.ReadNativeString(MaximumStringLength);
            SkipInt32(archive, 3);
            archive.SkipExactly(8);
        }

        static int ReadPresenceInt32(PackedArchiveReader archive, string label)
        {
            int value = archive.ReadInt32();
            if (value != 0 && value != 1)
                throw new InvalidDataException($"Invalid {label} presence value {value} at decompressed offset {archive.Position - 4}.");
            return value;
        }

        static byte ReadPresenceByte(PackedArchiveReader archive, string label)
        {
            byte value = archive.ReadByte();
            if (value != 0 && value != 1)
                throw new InvalidDataException($"Invalid {label} presence byte {value} at decompressed offset {archive.Position - 1}.");
            return value;
        }

        static void SkipInt32(PackedArchiveReader archive, int count)
        {
            if (count < 0)
                throw new InvalidDataException($"Invalid Int32 skip count {count}.");
            archive.SkipExactly(checked((long)count * 4));
        }

        static int CheckedProduct(int left, int right, int maximum, string label)
        {
            if (left <= 0 || right <= 0)
                throw new InvalidDataException($"Invalid {label} dimensions {left}x{right}.");
            long value = checked((long)left * right);
            if (value > maximum)
                throw new InvalidDataException($"The save declares {value} {label}s; the limit is {maximum}.");
            return (int)value;
        }

        static int ReadCount(PackedArchiveReader archive, string label)
        {
            return ReadCount(archive, label, MaximumSparseRecords);
        }

        static int ReadCount(PackedArchiveReader archive, string label, int maximum)
        {
            int count = archive.ReadInt32();
            if (count < 0 || count > maximum)
                throw new InvalidDataException($"Invalid {label} count {count}.");
            return count;
        }

        static void ExpectMarker(PackedArchiveReader archive, int expected)
        {
            long offset = archive.Position;
            int actual = archive.ReadInt32();
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"Invalid original save marker at decompressed offset {offset}: expected {expected}, found {actual}.");
            }
        }

        static void ExpectValue(PackedArchiveReader archive, int expected, string label)
        {
            long offset = archive.Position;
            int actual = archive.ReadInt32();
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"Invalid {label} at decompressed offset {offset}: expected 0x{expected:X8}, found 0x{actual:X8}.");
            }
        }

        sealed class PackedArchiveReader
        {
            static readonly Encoding Windows1252 = Encoding.GetEncoding(1252);

            // Decompression cap for the streaming metadata walk (the codec's buffered read has
            // its own): a crafted gzip bomb must fail fast instead of walking terabytes.
            const long MaximumDecompressedPosition =
                OriginalSaveArchiveCodecOptions.DefaultMaximumDecompressedPayloadLength;

            readonly BinaryReader _reader;
            readonly byte[] _skipBuffer = new byte[8192];

            internal PackedArchiveReader(Stream stream)
            {
                _reader = new BinaryReader(stream ?? throw new ArgumentNullException(nameof(stream)));
            }

            internal long Position { get; private set; }

            private void Advance(long byteCount)
            {
                Position += byteCount;
                if (Position > MaximumDecompressedPosition)
                {
                    throw new InvalidDataException(
                        $"Original save payload exceeds the decompressed limit of " +
                        $"{MaximumDecompressedPosition} bytes.");
                }
            }

            internal byte ReadByte()
            {
                byte value = _reader.ReadByte();
                Advance(1);
                return value;
            }

            internal int ReadInt32()
            {
                int value = _reader.ReadInt32();
                Advance(4);
                return value;
            }

            internal double ReadDouble()
            {
                double value = _reader.ReadDouble();
                Advance(8);
                return value;
            }

            internal string ReadNativeString(int maximumLength)
            {
                int length = ReadInt32();
                if (length <= 0 || length > maximumLength)
                    throw new InvalidDataException($"Invalid native string length {length}.");

                byte[] bytes = _reader.ReadBytes(length);
                Advance(bytes.Length);
                if (bytes.Length != length)
                    throw new EndOfStreamException("Unexpected end of original save string.");
                if (bytes[length - 1] != 0)
                    throw new InvalidDataException("Original save string is not NUL-terminated.");

                return Windows1252.GetString(bytes, 0, length - 1);
            }

            internal void SkipExactly(long byteCount)
            {
                if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
                while (byteCount > 0)
                {
                    int requested = (int)Math.Min(byteCount, _skipBuffer.Length);
                    int read = _reader.Read(_skipBuffer, 0, requested);
                    if (read <= 0)
                        throw new EndOfStreamException("Unexpected end of original save payload.");
                    Advance(read);
                    byteCount -= read;
                }
            }

            internal void ExpectEndOfStream()
            {
                if (_reader.BaseStream.ReadByte() < 0)
                    return;

                Position++;
                throw new InvalidDataException(
                    $"Original save contains trailing data after decompressed offset {Position - 1}.");
            }
        }
    }

    public sealed class OriginalSaveArchiveMetadata
    {
        internal OriginalSaveArchiveMetadata(
            int version,
            string headerText,
            double gameTime,
            long fixedEngineStateOffset,
            OriginalSaveWorldMetadata world,
            OriginalSaveObjectManifest? objects,
            OriginalSaveControlMetadata? control,
            OriginalSaveGraphicsMetadata? graphics,
            OriginalSaveLogicMetadata? logic)
        {
            Version = version;
            HeaderText = headerText;
            GameTime = gameTime;
            FixedEngineStateOffset = fixedEngineStateOffset;
            World = world;
            Objects = objects;
            Control = control;
            Graphics = graphics;
            Logic = logic;
        }

        public int Version { get; }
        public string HeaderText { get; }
        public double GameTime { get; }
        public long FixedEngineStateOffset { get; }
        public OriginalSaveWorldMetadata World { get; }
        public OriginalSaveObjectManifest? Objects { get; }
        public OriginalSaveControlMetadata? Control { get; }
        public OriginalSaveGraphicsMetadata? Graphics { get; }
        public OriginalSaveLogicMetadata? Logic { get; }
    }

    public sealed class OriginalSaveControlMetadata
    {
        internal OriginalSaveControlMetadata(
            long controlStateSectionOffset,
            int controlStateEntryCount,
            long marker8Offset,
            long controlRecordSectionOffset,
            int controlRecordCount,
            int nestedControlRecordCount,
            long marker50Offset)
        {
            ControlStateSectionOffset = controlStateSectionOffset;
            ControlStateEntryCount = controlStateEntryCount;
            Marker8Offset = marker8Offset;
            ControlRecordSectionOffset = controlRecordSectionOffset;
            ControlRecordCount = controlRecordCount;
            NestedControlRecordCount = nestedControlRecordCount;
            Marker50Offset = marker50Offset;
        }

        public long ControlStateSectionOffset { get; }
        public int ControlStateEntryCount { get; }
        public long Marker8Offset { get; }
        public long ControlRecordSectionOffset { get; }
        public int ControlRecordCount { get; }
        public int NestedControlRecordCount { get; }
        public long Marker50Offset { get; }
    }

    public sealed class OriginalSaveGraphicsMetadata
    {
        internal OriginalSaveGraphicsMetadata(
            long animationControllerSectionOffset,
            int animationControllerCount,
            int animationVectorValueCount,
            long marker9Offset,
            long graphicsEntrySectionOffset,
            int graphicsEntryCount,
            long marker10Offset)
        {
            AnimationControllerSectionOffset = animationControllerSectionOffset;
            AnimationControllerCount = animationControllerCount;
            AnimationVectorValueCount = animationVectorValueCount;
            Marker9Offset = marker9Offset;
            GraphicsEntrySectionOffset = graphicsEntrySectionOffset;
            GraphicsEntryCount = graphicsEntryCount;
            Marker10Offset = marker10Offset;
        }

        public long AnimationControllerSectionOffset { get; }
        public int AnimationControllerCount { get; }
        public int AnimationVectorValueCount { get; }
        public long Marker9Offset { get; }
        public long GraphicsEntrySectionOffset { get; }
        public int GraphicsEntryCount { get; }
        public long Marker10Offset { get; }
    }

    public sealed class OriginalSaveLogicMetadata
    {
        readonly ReadOnlyDictionary<int, int> _actionTypeCounts;

        internal OriginalSaveLogicMetadata(
            long firstManagerSectionOffset,
            int stringRecordCount,
            int descriptorCount,
            int nestedRecordCount,
            long marker11Offset,
            long matrixSectionOffset,
            int matrixValueCount,
            long marker12Offset,
            long actionSectionOffset,
            int actionCount,
            Dictionary<int, int> actionTypeCounts,
            int auxiliaryRecordCount,
            int auxiliaryVectorRecordCount,
            long marker13Offset,
            int pairRecordCount,
            long marker14Offset,
            int schedulerSlotCount,
            int schedulerRecordCount,
            long marker15Offset,
            int eightValueRecordCount,
            long marker16Offset,
            int namedRecordCount,
            long marker17Offset,
            int newsTickerActiveCount,
            int newsTickerHistoryCount,
            int newsTickerDisplayQueueCount,
            long newsTickerStateEndOffset,
            long objectIconStateOffset,
            long objectIconStateEndOffset,
            int lateFiveValueRecordCount,
            long caveRecordStateOffset,
            long caveRecordStateEndOffset,
            long aiOwnerStateOffset,
            long aiOwnerStateEndOffset,
            long elfStateOffset,
            long elfStateEndOffset,
            long reservedPlayerFramingOffset,
            long reservedPlayerFramingEndOffset,
            long playerStateOffset,
            long playerStateEndOffset,
            long marker18Offset,
            long finalBlockOffset,
            long payloadLength)
        {
            FirstManagerSectionOffset = firstManagerSectionOffset;
            StringRecordCount = stringRecordCount;
            DescriptorCount = descriptorCount;
            NestedRecordCount = nestedRecordCount;
            Marker11Offset = marker11Offset;
            MatrixSectionOffset = matrixSectionOffset;
            MatrixValueCount = matrixValueCount;
            Marker12Offset = marker12Offset;
            ActionSectionOffset = actionSectionOffset;
            ActionCount = actionCount;
            _actionTypeCounts = new ReadOnlyDictionary<int, int>(actionTypeCounts);
            AuxiliaryRecordCount = auxiliaryRecordCount;
            AuxiliaryVectorRecordCount = auxiliaryVectorRecordCount;
            Marker13Offset = marker13Offset;
            PairRecordCount = pairRecordCount;
            Marker14Offset = marker14Offset;
            SchedulerSlotCount = schedulerSlotCount;
            SchedulerRecordCount = schedulerRecordCount;
            Marker15Offset = marker15Offset;
            EightValueRecordCount = eightValueRecordCount;
            Marker16Offset = marker16Offset;
            NamedRecordCount = namedRecordCount;
            Marker17Offset = marker17Offset;
            NewsTickerActiveCount = newsTickerActiveCount;
            NewsTickerHistoryCount = newsTickerHistoryCount;
            NewsTickerDisplayQueueCount = newsTickerDisplayQueueCount;
            NewsTickerStateEndOffset = newsTickerStateEndOffset;
            ObjectIconStateOffset = objectIconStateOffset;
            ObjectIconStateEndOffset = objectIconStateEndOffset;
            LateFiveValueRecordCount = lateFiveValueRecordCount;
            CaveRecordStateOffset = caveRecordStateOffset;
            CaveRecordStateEndOffset = caveRecordStateEndOffset;
            AiOwnerStateOffset = aiOwnerStateOffset;
            AiOwnerStateEndOffset = aiOwnerStateEndOffset;
            ElfStateOffset = elfStateOffset;
            ElfStateEndOffset = elfStateEndOffset;
            ReservedPlayerFramingOffset = reservedPlayerFramingOffset;
            ReservedPlayerFramingEndOffset = reservedPlayerFramingEndOffset;
            PlayerStateOffset = playerStateOffset;
            PlayerStateEndOffset = playerStateEndOffset;
            Marker18Offset = marker18Offset;
            FinalBlockOffset = finalBlockOffset;
            PayloadLength = payloadLength;
        }

        public long FirstManagerSectionOffset { get; }
        public int StringRecordCount { get; }
        public int DescriptorCount { get; }
        public int NestedRecordCount { get; }
        public long Marker11Offset { get; }
        public long MatrixSectionOffset { get; }
        public int MatrixValueCount { get; }
        public long Marker12Offset { get; }
        public long ActionSectionOffset { get; }
        public int ActionCount { get; }
        public IReadOnlyDictionary<int, int> ActionTypeCounts => _actionTypeCounts;
        public int AuxiliaryRecordCount { get; }
        public int AuxiliaryVectorRecordCount { get; }
        public long Marker13Offset { get; }
        public int PairRecordCount { get; }
        public long Marker14Offset { get; }
        public int SchedulerSlotCount { get; }
        public int SchedulerRecordCount { get; }
        public long Marker15Offset { get; }
        public int EightValueRecordCount { get; }
        public long Marker16Offset { get; }
        public int NamedRecordCount { get; }
        public long Marker17Offset { get; }
        public int NewsTickerActiveCount { get; }
        public int NewsTickerHistoryCount { get; }
        public int NewsTickerDisplayQueueCount { get; }
        public long NewsTickerStateOffset => Marker17Offset + sizeof(int);
        public long NewsTickerStateEndOffset { get; }
        public long ObjectIconStateOffset { get; }
        public long ObjectIconStateEndOffset { get; }
        public int LateFiveValueRecordCount { get; }
        public long CaveRecordStateOffset { get; }
        public long CaveRecordStateEndOffset { get; }
        public long AiOwnerStateOffset { get; }
        public long AiOwnerStateEndOffset { get; }
        public long ElfStateOffset { get; }
        public long ElfStateEndOffset { get; }
        public long ReservedPlayerFramingOffset { get; }
        public long ReservedPlayerFramingEndOffset { get; }
        public long PlayerStateOffset { get; }
        public long PlayerStateEndOffset { get; }
        public long Marker18Offset { get; }
        public long FinalBlockOffset { get; }
        public long PayloadLength { get; }
    }

    public sealed class OriginalSaveObjectManifest
    {
        readonly ReadOnlyCollection<OriginalSaveObjectDeclaration> _declarations;
        readonly ReadOnlyCollection<OriginalSaveObjectBodySummary> _bodies;
        readonly ReadOnlyDictionary<string, int> _classCounts;

        internal OriginalSaveObjectManifest(
            long declarationsOffset,
            long declarationTerminatorOffset,
            long separatorOffset,
            string separatorText,
            long supportSectionOffset,
            int supportRecordCount,
            long bodySectionOffset,
            int firstBodyObjectId,
            long bodyStreamEndOffset,
            int classTableEntryCount,
            int spatialBucketCount,
            int spatialRecordCount,
            long spatialIndexOffset,
            long marker7Offset,
            List<OriginalSaveObjectDeclaration> declarations,
            List<OriginalSaveObjectBodySummary> bodies,
            Dictionary<string, int> classCounts,
            OriginalSaveOwnerAttributeTableDocument ownerAttributeTables)
        {
            DeclarationsOffset = declarationsOffset;
            DeclarationTerminatorOffset = declarationTerminatorOffset;
            SeparatorOffset = separatorOffset;
            SeparatorText = separatorText;
            SupportSectionOffset = supportSectionOffset;
            SupportRecordCount = supportRecordCount;
            BodySectionOffset = bodySectionOffset;
            FirstBodyObjectId = firstBodyObjectId;
            BodyStreamEndOffset = bodyStreamEndOffset;
            ClassTableEntryCount = classTableEntryCount;
            SpatialBucketCount = spatialBucketCount;
            SpatialRecordCount = spatialRecordCount;
            SpatialIndexOffset = spatialIndexOffset;
            Marker7Offset = marker7Offset;
            _declarations = declarations.AsReadOnly();
            _bodies = bodies.AsReadOnly();
            _classCounts = new ReadOnlyDictionary<string, int>(classCounts);
            OwnerAttributeTables = ownerAttributeTables ??
                throw new ArgumentNullException(nameof(ownerAttributeTables));
        }

        public long DeclarationsOffset { get; }
        public long DeclarationTerminatorOffset { get; }
        public long SeparatorOffset { get; }
        public string SeparatorText { get; }
        public long SupportSectionOffset { get; }
        public int SupportRecordCount { get; }
        public long BodySectionOffset { get; }
        public int FirstBodyObjectId { get; }
        public long BodyStreamEndOffset { get; }
        public int ClassTableEntryCount { get; }
        public int SpatialBucketCount { get; }
        public int SpatialRecordCount { get; }

        // Decompressed byte offset of the first spatial-index bucket count (marker-7 precursor).
        // Feeds OriginalSaveSpatialSeedDocument's typed decode of the dormant scenery seeds.
        public long SpatialIndexOffset { get; }
        public long Marker7Offset { get; }
        public IReadOnlyList<OriginalSaveObjectDeclaration> Declarations => _declarations;
        public IReadOnlyList<OriginalSaveObjectBodySummary> Bodies => _bodies;
        public IReadOnlyDictionary<string, int> ClassCounts => _classCounts;
        public OriginalSaveOwnerAttributeTableDocument OwnerAttributeTables { get; }
    }

    public sealed class OriginalSaveOwnerAttributeTableDocument
    {
        private readonly ReadOnlyCollection<OriginalSaveOwnerAttributeBlockDocument> _blocks;

        internal OriginalSaveOwnerAttributeTableDocument(
            long offset,
            OriginalSaveOwnerAttributeBlockDocument[] blocks)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            if (blocks.Length != 9)
            {
                throw new ArgumentException(
                    "An owner-attribute table must contain nine owner blocks.",
                    nameof(blocks));
            }

            Offset = offset;
            _blocks = Array.AsReadOnly(blocks);
        }

        public long Offset { get; }
        public IReadOnlyList<OriginalSaveOwnerAttributeBlockDocument> Blocks => _blocks;
    }

    public sealed class OriginalSaveOwnerAttributeBlockDocument
    {
        private readonly ReadOnlyCollection<int> _primaryValueBits;
        private readonly ReadOnlyCollection<int> _secondaryValueBits;

        internal OriginalSaveOwnerAttributeBlockDocument(
            int owner,
            long primaryOffset,
            long secondaryOffset,
            int[] primaryValueBits,
            int[] secondaryValueBits)
        {
            if (owner is < -1 or > 7) throw new ArgumentOutOfRangeException(nameof(owner));
            if (primaryValueBits == null) throw new ArgumentNullException(nameof(primaryValueBits));
            if (secondaryValueBits == null) throw new ArgumentNullException(nameof(secondaryValueBits));
            if (primaryValueBits.Length != 512)
            {
                throw new ArgumentException(
                    "A primary owner-attribute table must contain 512 values.",
                    nameof(primaryValueBits));
            }
            if (secondaryValueBits.Length != 512)
            {
                throw new ArgumentException(
                    "A secondary owner-attribute table must contain 512 values.",
                    nameof(secondaryValueBits));
            }

            Owner = owner;
            PrimaryOffset = primaryOffset;
            SecondaryOffset = secondaryOffset;
            _primaryValueBits = Array.AsReadOnly(primaryValueBits);
            _secondaryValueBits = Array.AsReadOnly(secondaryValueBits);
        }

        public int Owner { get; }
        public long PrimaryOffset { get; }
        public long SecondaryOffset { get; }
        public IReadOnlyList<int> PrimaryValueBits => _primaryValueBits;

        // B3 identifies this as an independent owner-attribute table carrying
        // accumulator/timestamp-like state; retained here raw.
        public IReadOnlyList<int> SecondaryValueBits => _secondaryValueBits;
    }

    public sealed class OriginalSaveObjectDeclaration
    {
        internal OriginalSaveObjectDeclaration(int objectId, string className)
        {
            ObjectId = objectId;
            ClassName = className;
        }

        public int ObjectId { get; }
        public string ClassName { get; }
    }

    public sealed class OriginalSaveObjectBodySummary
    {
        internal OriginalSaveObjectBodySummary(
            int objectId,
            string className,
            int objectType,
            long offset,
            long size)
        {
            ObjectId = objectId;
            ClassName = className;
            ObjectType = objectType;
            Offset = offset;
            Size = size;
        }

        public int ObjectId { get; }
        public string ClassName { get; }
        public int ObjectType { get; }
        public long Offset { get; }
        public long Size { get; }
    }

    public sealed class OriginalSaveWorldMetadata
    {
        private readonly ReadOnlyCollection<int> _fluidSparseCounts;
        private readonly ReadOnlyCollection<int> _fogSparseCounts;

        internal OriginalSaveWorldMetadata(
            long sectionOffset,
            long marker4Offset,
            int width,
            int height,
            int minX,
            int minY,
            int maxXExclusive,
            int maxYExclusive,
            int terrainRecordCount,
            int fluidPrimaryCount,
            int fluidSecondaryCount,
            int[] fluidSparseCounts,
            int[] fogSparseCounts)
        {
            SectionOffset = sectionOffset;
            Marker4Offset = marker4Offset;
            Width = width;
            Height = height;
            MinX = minX;
            MinY = minY;
            MaxXExclusive = maxXExclusive;
            MaxYExclusive = maxYExclusive;
            TerrainRecordCount = terrainRecordCount;
            FluidPrimaryCount = fluidPrimaryCount;
            FluidSecondaryCount = fluidSecondaryCount;
            _fluidSparseCounts = Array.AsReadOnly(fluidSparseCounts);
            _fogSparseCounts = Array.AsReadOnly(fogSparseCounts);
        }

        public long SectionOffset { get; }
        public long Marker4Offset { get; }
        public int Width { get; }
        public int Height { get; }
        public int MinX { get; }
        public int MinY { get; }
        public int MaxXExclusive { get; }
        public int MaxYExclusive { get; }

        // Compatibility aliases retained while callers migrate to the verified CMap bound names.
        public int OriginX => MinX;
        public int OriginY => MinY;
        public int LogicalWidth => MaxXExclusive;
        public int LogicalHeight => MaxYExclusive;
        public int TerrainRecordCount { get; }
        public int FluidPrimaryCount { get; }
        public int FluidSecondaryCount { get; }
        public IReadOnlyList<int> FluidSparseCounts => _fluidSparseCounts;
        public IReadOnlyList<int> FogSparseCounts => _fogSparseCounts;
    }
}
