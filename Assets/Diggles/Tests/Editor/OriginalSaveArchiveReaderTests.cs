#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using Vector3 = UnityEngine.Vector3;

    public sealed class OriginalSaveArchiveReaderTests
    {
        [Test]
        public void ReadsOriginalTutorialSaveThroughWorldBoundary()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var metadata = new OriginalSaveArchiveReader().ReadMetadata(path);

            Assert.That(metadata.Version, Is.EqualTo(0xA5));
            Assert.That(metadata.HeaderText, Is.Empty);
            Assert.That(metadata.GameTime, Is.EqualTo(441.428134966944).Within(1e-9));
            Assert.That(metadata.FixedEngineStateOffset, Is.EqualTo(21));
            Assert.That(metadata.World.SectionOffset, Is.EqualTo(234));
            Assert.That(metadata.World.Width, Is.EqualTo(500));
            Assert.That(metadata.World.Height, Is.EqualTo(200));
            Assert.That(metadata.World.MinX, Is.Zero);
            Assert.That(metadata.World.MinY, Is.Zero);
            Assert.That(metadata.World.MaxXExclusive, Is.EqualTo(500));
            Assert.That(metadata.World.MaxYExclusive, Is.EqualTo(200));
            Assert.That(metadata.World.TerrainRecordCount, Is.EqualTo(100000));
            Assert.That(metadata.World.FluidPrimaryCount, Is.Zero);
            Assert.That(metadata.World.FluidSecondaryCount, Is.EqualTo(9));
            Assert.That(metadata.World.FluidSparseCounts, Is.EqualTo(new[] { 0, 0, 0, 0 }));
            Assert.That(metadata.World.FogSparseCounts,
                Is.EqualTo(new[] { 11415, 228, 227, 0, 0, 0, 0, 0 }));
            Assert.That(metadata.World.Marker4Offset, Is.EqualTo(5395692));

            Assert.That(metadata.Objects, Is.Not.Null);
            var objects = metadata.Objects!;
            Assert.That(objects.DeclarationsOffset, Is.EqualTo(5395704));
            Assert.That(objects.DeclarationTerminatorOffset, Is.EqualTo(5407369));
            Assert.That(objects.SeparatorOffset, Is.EqualTo(5407373));
            Assert.That(objects.SeparatorText, Is.Empty);
            Assert.That(objects.SupportSectionOffset, Is.EqualTo(5407378));
            Assert.That(objects.SupportRecordCount, Is.EqualTo(24));
            Assert.That(objects.BodySectionOffset, Is.EqualTo(5410790));
            Assert.That(objects.FirstBodyObjectId, Is.EqualTo(31));
            Assert.That(objects.BodyStreamEndOffset, Is.EqualTo(6061013));
            Assert.That(objects.ClassTableEntryCount, Is.EqualTo(345));
            Assert.That(objects.SpatialBucketCount, Is.EqualTo(1575));
            Assert.That(objects.SpatialRecordCount, Is.EqualTo(1242));
            Assert.That(objects.Marker7Offset, Is.EqualTo(6317049));
            Assert.That(objects.Declarations, Has.Count.EqualTo(484));
            Assert.That(objects.Bodies, Has.Count.EqualTo(484));
            Assert.That(objects.ClassCounts, Has.Count.EqualTo(129));
            Assert.That(objects.Declarations[0].ObjectId, Is.EqualTo(31));
            Assert.That(objects.Declarations[0].ClassName, Is.EqualTo("Dummy_Wand_so_verlies_c"));
            Assert.That(objects.Declarations[^1].ObjectId, Is.EqualTo(1793));
            Assert.That(objects.Declarations[^1].ClassName, Is.EqualTo("Dummy_Muetze_b"));
            Assert.That(objects.ClassCounts["Info_Coll_b"], Is.EqualTo(58));
            Assert.That(objects.Bodies[1].ObjectId, Is.EqualTo(50));
            Assert.That(objects.Bodies[1].ObjectType, Is.EqualTo(3));
            Assert.That(objects.Bodies[2].ObjectId, Is.EqualTo(51));
            Assert.That(objects.Bodies[2].ObjectType, Is.EqualTo(1));
            Assert.That(objects.Bodies[2].Size, Is.EqualTo(26998));

            Assert.That(metadata.Control, Is.Not.Null);
            var control = metadata.Control!;
            Assert.That(control.ControlStateSectionOffset, Is.EqualTo(6317053));
            Assert.That(control.ControlStateEntryCount, Is.EqualTo(53));
            Assert.That(control.Marker8Offset, Is.EqualTo(6319177));
            Assert.That(control.ControlRecordSectionOffset, Is.EqualTo(6319181));
            Assert.That(control.ControlRecordCount, Is.Zero);
            Assert.That(control.NestedControlRecordCount, Is.Zero);
            Assert.That(control.Marker50Offset, Is.EqualTo(6319185));

            Assert.That(metadata.Graphics, Is.Not.Null);
            var graphics = metadata.Graphics!;
            Assert.That(graphics.AnimationControllerSectionOffset, Is.EqualTo(6319189));
            Assert.That(graphics.AnimationControllerCount, Is.EqualTo(1792));
            Assert.That(graphics.AnimationVectorValueCount, Is.EqualTo(4241));
            Assert.That(graphics.Marker9Offset, Is.EqualTo(6416797));
            Assert.That(graphics.GraphicsEntrySectionOffset, Is.EqualTo(6416801));
            Assert.That(graphics.GraphicsEntryCount, Is.EqualTo(37));
            Assert.That(graphics.Marker10Offset, Is.EqualTo(6419210));

            Assert.That(metadata.Logic, Is.Not.Null);
            var logic = metadata.Logic!;
            Assert.That(logic.FirstManagerSectionOffset, Is.EqualTo(6419214));
            Assert.That(logic.StringRecordCount, Is.Zero);
            Assert.That(logic.DescriptorCount, Is.Zero);
            Assert.That(logic.NestedRecordCount, Is.Zero);
            Assert.That(logic.Marker11Offset, Is.EqualTo(6419330));
            Assert.That(logic.MatrixSectionOffset, Is.EqualTo(6419334));
            Assert.That(logic.MatrixValueCount, Is.Zero);
            Assert.That(logic.Marker12Offset, Is.EqualTo(6419658));
            Assert.That(logic.ActionSectionOffset, Is.EqualTo(6419662));
            Assert.That(logic.ActionCount, Is.EqualTo(327));
            Assert.That(logic.ActionTypeCounts,
                Is.EquivalentTo(new[]
                {
                    new System.Collections.Generic.KeyValuePair<int, int>(0, 309),
                    new System.Collections.Generic.KeyValuePair<int, int>(1, 1),
                    new System.Collections.Generic.KeyValuePair<int, int>(3, 16),
                    new System.Collections.Generic.KeyValuePair<int, int>(10, 1),
                }));
            Assert.That(logic.AuxiliaryRecordCount, Is.Zero);
            Assert.That(logic.AuxiliaryVectorRecordCount, Is.Zero);
            Assert.That(logic.Marker13Offset, Is.EqualTo(6425457));
            Assert.That(logic.PairRecordCount, Is.EqualTo(2));
            Assert.That(logic.Marker14Offset, Is.EqualTo(6425481));
            Assert.That(logic.SchedulerSlotCount, Is.EqualTo(2));
            Assert.That(logic.SchedulerRecordCount, Is.EqualTo(2));
            Assert.That(logic.Marker15Offset, Is.EqualTo(6425749));
            Assert.That(logic.EightValueRecordCount, Is.EqualTo(11));
            Assert.That(logic.Marker16Offset, Is.EqualTo(6426109));
            Assert.That(logic.NamedRecordCount, Is.Zero);
            Assert.That(logic.Marker17Offset, Is.EqualTo(6426117));
            Assert.That(logic.NewsTickerActiveCount, Is.Zero);
            Assert.That(logic.NewsTickerHistoryCount, Is.EqualTo(11));
            Assert.That(logic.NewsTickerDisplayQueueCount, Is.Zero);
            Assert.That(logic.LateFiveValueRecordCount, Is.Zero);
            Assert.That(logic.Marker18Offset, Is.EqualTo(6437305));
            Assert.That(logic.FinalBlockOffset, Is.EqualTo(6437309));
            Assert.That(logic.PayloadLength, Is.EqualTo(6441405));
        }

        [Test]
        public void ReadsLargeOriginalSupportTableAndObjectActionState()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_test.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var metadata = new OriginalSaveArchiveReader().ReadMetadata(path);

            Assert.That(metadata.Objects, Is.Not.Null);
            Assert.That(metadata.Objects!.SupportRecordCount, Is.EqualTo(9143));
            Assert.That(metadata.Objects.Bodies, Has.Count.EqualTo(1579));
            Assert.That(metadata.Objects.BodyStreamEndOffset, Is.EqualTo(23448801));
            Assert.That(metadata.Objects.ClassTableEntryCount, Is.EqualTo(920));
            Assert.That(metadata.Objects.SpatialBucketCount, Is.EqualTo(5120));
            Assert.That(metadata.Objects.SpatialRecordCount, Is.EqualTo(1867));
            Assert.That(metadata.Objects.Marker7Offset, Is.EqualTo(23881679));
            Assert.That(metadata.Objects.Bodies.Any(body => body.ObjectType == 2), Is.True);
            Assert.That(metadata.Objects.Bodies.Any(body => body.ObjectType == 6), Is.True);
            Assert.That(metadata.Control, Is.Not.Null);
            Assert.That(metadata.Control!.ControlStateSectionOffset, Is.EqualTo(23881683));
            Assert.That(metadata.Control.ControlStateEntryCount, Is.EqualTo(369));
            Assert.That(metadata.Control.Marker8Offset, Is.EqualTo(23896447));
            Assert.That(metadata.Control.ControlRecordSectionOffset, Is.EqualTo(23896451));
            Assert.That(metadata.Control.ControlRecordCount, Is.EqualTo(2));
            Assert.That(metadata.Control.NestedControlRecordCount, Is.Zero);
            Assert.That(metadata.Control.Marker50Offset, Is.EqualTo(23896487));
            Assert.That(metadata.Graphics, Is.Not.Null);
            Assert.That(metadata.Graphics!.AnimationControllerSectionOffset, Is.EqualTo(23896491));
            Assert.That(metadata.Graphics.AnimationControllerCount, Is.EqualTo(8177));
            Assert.That(metadata.Graphics.AnimationVectorValueCount, Is.EqualTo(19809));
            Assert.That(metadata.Graphics.Marker9Offset, Is.EqualTo(24343696));
            Assert.That(metadata.Graphics.GraphicsEntrySectionOffset, Is.EqualTo(24343700));
            Assert.That(metadata.Graphics.GraphicsEntryCount, Is.EqualTo(424));
            Assert.That(metadata.Graphics.Marker10Offset, Is.EqualTo(24371264));
            Assert.That(metadata.Logic, Is.Not.Null);
            Assert.That(metadata.Logic!.FirstManagerSectionOffset, Is.EqualTo(24371268));
            Assert.That(metadata.Logic.StringRecordCount, Is.EqualTo(60));
            Assert.That(metadata.Logic.DescriptorCount, Is.EqualTo(27));
            Assert.That(metadata.Logic.NestedRecordCount, Is.EqualTo(23));
            Assert.That(metadata.Logic.Marker11Offset, Is.EqualTo(24476925));
            Assert.That(metadata.Logic.MatrixSectionOffset, Is.EqualTo(24476929));
            Assert.That(metadata.Logic.MatrixValueCount, Is.EqualTo(18));
            Assert.That(metadata.Logic.Marker12Offset, Is.EqualTo(24477325));
            Assert.That(metadata.Logic.ActionSectionOffset, Is.EqualTo(24477329));
            Assert.That(metadata.Logic.ActionCount, Is.EqualTo(1316));
            Assert.That(metadata.Logic.ActionTypeCounts[0], Is.EqualTo(1215));
            Assert.That(metadata.Logic.ActionTypeCounts[1], Is.EqualTo(8));
            Assert.That(metadata.Logic.ActionTypeCounts[3], Is.EqualTo(54));
            Assert.That(metadata.Logic.ActionTypeCounts[5], Is.EqualTo(2));
            Assert.That(metadata.Logic.ActionTypeCounts[8], Is.EqualTo(8));
            Assert.That(metadata.Logic.ActionTypeCounts[10], Is.EqualTo(21));
            Assert.That(metadata.Logic.ActionTypeCounts[11], Is.EqualTo(8));
            Assert.That(metadata.Logic.AuxiliaryRecordCount, Is.Zero);
            Assert.That(metadata.Logic.AuxiliaryVectorRecordCount, Is.Zero);
            Assert.That(metadata.Logic.Marker13Offset, Is.EqualTo(24539300));
            Assert.That(metadata.Logic.PairRecordCount, Is.EqualTo(17));
            Assert.That(metadata.Logic.Marker14Offset, Is.EqualTo(24539444));
            Assert.That(metadata.Logic.SchedulerSlotCount, Is.EqualTo(9));
            Assert.That(metadata.Logic.SchedulerRecordCount, Is.EqualTo(9));
            Assert.That(metadata.Logic.Marker15Offset, Is.EqualTo(24540687));
            Assert.That(metadata.Logic.EightValueRecordCount, Is.EqualTo(3));
            Assert.That(metadata.Logic.Marker16Offset, Is.EqualTo(24540791));
            Assert.That(metadata.Logic.NamedRecordCount, Is.EqualTo(2));
            Assert.That(metadata.Logic.Marker17Offset, Is.EqualTo(24540859));
            Assert.That(metadata.Logic.NewsTickerActiveCount, Is.EqualTo(1));
            Assert.That(metadata.Logic.NewsTickerHistoryCount, Is.EqualTo(51));
            Assert.That(metadata.Logic.NewsTickerDisplayQueueCount, Is.EqualTo(1));
            Assert.That(metadata.Logic.LateFiveValueRecordCount, Is.EqualTo(1));
            Assert.That(metadata.Logic.Marker18Offset, Is.EqualTo(24595975));
            Assert.That(metadata.Logic.FinalBlockOffset, Is.EqualTo(24595979));
            Assert.That(metadata.Logic.PayloadLength, Is.EqualTo(24600075));
        }

        [Test]
        public void ReadsEveryAvailableCurrentOriginalSaveThroughEndOfPayload()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root), $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var reader = new OriginalSaveArchiveReader();
            foreach (string path in paths)
            {
                OriginalSaveArchiveMetadata metadata = reader.ReadMetadata(path);
                Assert.That(metadata.Version, Is.EqualTo(0xA5), path);
                Assert.That(metadata.Logic, Is.Not.Null, path);
                Assert.That(metadata.Logic!.PayloadLength,
                    Is.EqualTo(metadata.Logic.FinalBlockOffset + 0x1000), path);
            }
        }

        [Test]
        public void PreparesEveryAvailableCurrentSaveIdentityPlan()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            var codec = new OriginalSaveArchiveCodec();
            var planner = new OriginalSaveLoadPlanner();
            var stager = new OriginalSaveRuntimeStager();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveLoadPlan plan = planner.Prepare(document);
                OriginalSaveRuntimeSnapshot snapshot = stager.Stage(plan);
                Assert.That(plan.ObjectsById.Count, Is.EqualTo(document.Objects!.Count), path);
                Assert.That(snapshot.Entities, Has.Count.EqualTo(document.Objects.Count), path);
            }
        }

        [Test]
        public void LosslessCodecRoundTripsValidatedCurrentPayload()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var codec = new OriginalSaveArchiveCodec();
            OriginalSaveArchiveDocument original = codec.Read(path);
            using var compressed = new MemoryStream();
            codec.Write(compressed, original, true);
            compressed.Position = 0;
            OriginalSaveArchiveDocument roundTrip = codec.Read(compressed, true);

            Assert.That(roundTrip.Metadata.Version, Is.EqualTo(0xA5));
            Assert.That(roundTrip.Metadata.Logic, Is.Not.Null);
            Assert.That(roundTrip.Metadata.Logic!.PayloadLength, Is.EqualTo(6441405));
            Assert.That(roundTrip.PayloadLength, Is.EqualTo(original.PayloadLength));
            CollectionAssert.AreEqual(original.CopyPayload(), roundTrip.CopyPayload());
        }

        [Test]
        public void TypedCurrentWriterPreservesCommonObjectStateAndOpaqueTailsByteForByte()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            var codec = new OriginalSaveArchiveCodec(reader);
            OriginalSaveArchiveDocument original = codec.Read(path);

            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original);

            Assert.That(rebuilt.Metadata.Version, Is.EqualTo(0xA5));
            Assert.That(rebuilt.Objects, Has.Count.EqualTo(original.Objects!.Count));
            CollectionAssert.AreEqual(original.CopyPayload(), rebuilt.CopyPayload());
        }

        [Test]
        public void TypedCurrentWriterRebuildsNativeWorldByteForByte()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            OriginalSaveArchiveDocument original = new OriginalSaveArchiveCodec(reader).Read(path);
            var patch = new OriginalSaveCurrentArchivePatch
            {
                World = new OriginalSaveWorldPatch(),
            };

            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);

            CollectionAssert.AreEqual(original.CopyPayload(), rebuilt.CopyPayload());
        }

        [Test]
        public void TypedCurrentWriterAppliesVariableLengthSemanticWorldPatch()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            OriginalSaveArchiveDocument original = new OriginalSaveArchiveCodec(reader).Read(path);
            var sparseSets = new IReadOnlyList<OriginalSaveIntPair>[]
            {
                new[] { new OriginalSaveIntPair(12, 34) },
                Array.Empty<OriginalSaveIntPair>(),
                Array.Empty<OriginalSaveIntPair>(),
                Array.Empty<OriginalSaveIntPair>(),
            };
            var fogLists = new IReadOnlyList<OriginalSaveIntPair>[8];
            for (int player = 0; player < fogLists.Length; player++)
                fogLists[player] = Array.Empty<OriginalSaveIntPair>();

            var worldPatch = new OriginalSaveWorldPatch
            {
                Fluid = new OriginalSaveFluidPatch
                {
                    CurrentBufferIndex = 0,
                    PreviousBufferIndex = 1,
                    SparseSets = sparseSets,
                },
                FogFadeTilesByPlayer = fogLists,
            };
            worldPatch.SetTerrainTile(new OriginalSaveTerrainTilePatch(100)
            {
                BlockIndex = 59,
                DigState = 0xAu,
                DigOrderIds = 2u,
                TerrainState = 2u,
                FluidTypeBuffer0 = 1,
                FluidTypeBuffer1 = 1,
                FluidFillBuffer0 = 1.25f,
                FluidFillBuffer1 = 1.25f,
                FogState = 0x0102030405060708UL,
            });
            var patch = new OriginalSaveCurrentArchivePatch { World = worldPatch };

            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);
            OriginalSaveTerrainTile changed = rebuilt.World.Terrain[100];

            Assert.That(changed.BlockIndex, Is.EqualTo(59));
            Assert.That(changed.DigState, Is.EqualTo(0xAu));
            Assert.That(changed.DigOrderIds, Is.EqualTo(2u));
            Assert.That(changed.MaterialType, Is.EqualTo(2));
            Assert.That(changed.FluidTypeForBuffer(0), Is.EqualTo(1));
            Assert.That(changed.FluidTypeForBuffer(1), Is.EqualTo(1));
            Assert.That(changed.FluidFillForBuffer(0), Is.EqualTo(1.25f));
            Assert.That(changed.FluidFillForBuffer(1), Is.EqualTo(1.25f));
            Assert.That(changed.FogState, Is.EqualTo(0x0102030405060708UL));
            Assert.That(rebuilt.World.Fluid.CurrentBufferIndex, Is.Zero);
            Assert.That(rebuilt.World.Fluid.PreviousBufferIndex, Is.EqualTo(1));
            Assert.That(rebuilt.World.Fluid.SparseSets[0], Has.Count.EqualTo(1));
            Assert.That(rebuilt.World.Fluid.SparseSets[0][0].First, Is.EqualTo(12));
            Assert.That(rebuilt.World.Fog.FadeTilesByPlayer.All(set => set.Count == 0), Is.True);
            Assert.That(rebuilt.Objects, Has.Count.EqualTo(original.Objects!.Count));
            Assert.DoesNotThrow(() => new OriginalSaveLoadPlanner().Prepare(rebuilt));
        }

        [Test]
        public void TypedCurrentWriterAppliesVariableLengthSemanticObjectPatch()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            OriginalSaveArchiveDocument original = new OriginalSaveArchiveCodec(reader).Read(path);
            var patch = new OriginalSaveCurrentArchivePatch { GameTime = 500.25d };
            patch.SetObject(new OriginalSaveObjectCorePatch(51)
            {
                Position = new OriginalSaveVector3(100.5f, 30.25f, 4.75f),
                Velocity = new OriginalSaveVector3(1f, 2f, 3f),
                Rotation = new OriginalSaveVector3(0.1f, 0.2f, 0.3f),
                WorkStart = 2.5f,
                WorkLength = 7.5f,
                ChildObjectIds = new[] { 50 },
                TclVariables = new[] { new OriginalSaveTclVariable("writer_probe", "valid") },
                StateObjectId = 50,
                InstanceName = "Charly Reloaded",
                DisplayState = "EinZwergWriterProbe",
                FogOfWarSizeX = 80,
                FogOfWarSizeY = 48,
                FogOfWarLastX = 100.5f,
                FogOfWarLastY = 30.25f,
                Owner = 2,
                LinkedObjectId = 50,
                LinkedSlot = 4,
                PlaceSnapMode = 1,
                ObjectFlags = 1234567,
                DirtyFlags = 7,
                DigOrderId = 3,
            });

            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);
            OriginalSaveObjectDocument changed = rebuilt.Objects!.FindById(51)!;

            Assert.That(rebuilt.Metadata.GameTime, Is.EqualTo(500.25d));
            Assert.That(changed.Position.X, Is.EqualTo(100.5f));
            Assert.That(changed.Velocity.Z, Is.EqualTo(3f));
            Assert.That(changed.Rotation.Y, Is.EqualTo(0.2f));
            Assert.That(changed.WorkStart, Is.EqualTo(2.5f));
            Assert.That(changed.WorkLength, Is.EqualTo(7.5f));
            Assert.That(changed.ChildObjectIds, Is.EqualTo(new[] { 50 }));
            Assert.That(changed.TryGetTclVariable("writer_probe", out string value), Is.True);
            Assert.That(value, Is.EqualTo("valid"));
            Assert.That(changed.StateObjectId, Is.EqualTo(50));
            Assert.That(changed.InstanceName, Is.EqualTo("Charly Reloaded"));
            Assert.That(changed.DisplayState, Is.EqualTo("EinZwergWriterProbe"));
            Assert.That(changed.CommonState.FogOfWarSizeX, Is.EqualTo(80));
            Assert.That(changed.CommonState.FogOfWarSizeY, Is.EqualTo(48));
            Assert.That(changed.Owner, Is.EqualTo(2));
            Assert.That(changed.CommonState.LinkedObjectId, Is.EqualTo(50));
            Assert.That(changed.CommonState.LinkedSlot, Is.EqualTo(4));
            Assert.That(changed.CommonState.PlaceSnapMode, Is.EqualTo(1));
            Assert.That(changed.CommonState.ObjectFlags, Is.EqualTo(1234567));
            Assert.That(changed.CommonState.DirtyFlags, Is.EqualTo(7));
            Assert.That(changed.CommonState.DigOrderId, Is.EqualTo(3));
            Assert.DoesNotThrow(() => new OriginalSaveLoadPlanner().Prepare(rebuilt));
        }

        [Test]
        public void RuntimePatchMapperCapturesSafeIdentityStableNativeState()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            OriginalSaveArchiveDocument original = new OriginalSaveArchiveCodec(reader).Read(path);
            OriginalSaveRuntimeSnapshot baseline = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(original));
            DigglesTclContext context = BuildIdentityStableRuntime(baseline, 700.75d);
            DigglesTclEntity charly = context.Entities[51];
            charly.Position = new Vector3(101.5f, 31.25f, 5.75f);
            charly.Owner = 3;
            charly.WorkStart = 4f;
            charly.WorkLen = 8f;
            charly.LinkedToHandle = 50;
            charly.LinkedSlot = 5;

            OriginalSaveCurrentArchivePatch patch =
                new OriginalSaveRuntimePatchMapper().Capture(baseline, context);
            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);
            OriginalSaveObjectDocument changed = rebuilt.Objects!.FindById(51)!;

            Assert.That(rebuilt.Metadata.GameTime, Is.EqualTo(700.75d));
            Assert.That(changed.Position.X, Is.EqualTo(101.5f));
            Assert.That(changed.Position.Y, Is.EqualTo(31.25f));
            Assert.That(changed.Position.Z, Is.EqualTo(5.75f));
            Assert.That(changed.Owner, Is.EqualTo(3));
            Assert.That(changed.WorkStart, Is.EqualTo(4f));
            Assert.That(changed.WorkLength, Is.EqualTo(8f));
            Assert.That(changed.CommonState.LinkedObjectId, Is.EqualTo(50));
            Assert.That(changed.CommonState.LinkedSlot, Is.EqualTo(5));
            Assert.DoesNotThrow(() => new OriginalSaveLoadPlanner().Prepare(rebuilt));
        }

        [Test]
        public void RuntimePatchMapperNoOpPreservesNativePayloadByteForByte()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            OriginalSaveArchiveDocument original = new OriginalSaveArchiveCodec(reader).Read(path);
            OriginalSaveRuntimeSnapshot baseline = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(original));
            DigglesTclContext context = BuildIdentityStableRuntime(baseline, baseline.GameTime);

            OriginalSaveCurrentArchivePatch patch =
                new OriginalSaveRuntimePatchMapper().Capture(baseline, context);
            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);

            OriginalFluidCompatibilityState compatibility = baseline.RuntimeWorld.FluidCompatibility;
            Assert.That(compatibility.TileCount, Is.EqualTo(original.World.Terrain.Count));
            Assert.That(compatibility.CurrentBufferIndex,
                Is.EqualTo(original.World.Fluid.CurrentBufferIndex));
            Assert.That(compatibility.PreviousBufferIndex,
                Is.EqualTo(original.World.Fluid.PreviousBufferIndex));
            Assert.That(compatibility.Sources, Has.Count.EqualTo(original.World.Fluid.Sources.Count));
            Assert.That(compatibility.Stoppers, Has.Count.EqualTo(original.World.Fluid.Stoppers.Count));
            Assert.That(compatibility.SparseSets.Select(set => set.Count),
                Is.EqualTo(original.World.Fluid.SparseSets.Select(set => set.Count)));
            for (int index = 0; index < compatibility.TileCount; index++)
            {
                OriginalFluidCompatibilityTileState exact = compatibility.GetTile(index);
                OriginalSaveTerrainTile source = original.World.Terrain[index];
                if (exact.TypeBuffer0 != source.FluidTypeForBuffer(0) ||
                    exact.TypeBuffer1 != source.FluidTypeForBuffer(1) ||
                    exact.FillBuffer0Bits != source.Unknown1C ||
                    exact.FillBuffer1Bits != source.Unknown20 ||
                    exact.CacheWord0 != source.Unknown24 || exact.CacheWord1 != source.Unknown28 ||
                    exact.CacheWord2 != source.Unknown2C || exact.CacheWord3 != source.Unknown30)
                {
                    Assert.Fail($"Native fluid compatibility tile {index} was not retained exactly.");
                }
            }
            CollectionAssert.AreEqual(original.CopyPayload(), rebuilt.CopyPayload());
        }

        [Test]
        public void RuntimePatchMapperCapturesTerrainFogAndSynthesizesMutatedFluid()
        {
            string path = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");

            var reader = new OriginalSaveArchiveReader();
            OriginalSaveArchiveDocument original = new OriginalSaveArchiveCodec(reader).Read(path);
            OriginalSaveRuntimeSnapshot baseline = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(original));
            DigglesTclContext context = BuildIdentityStableRuntime(baseline, 800.25d);

            var terrainObject = new UnityEngine.GameObject("OriginalSaveTerrainCaptureTest");
            try
            {
                int count = original.World.Terrain.Count;
                var result = new BdbPmpWorldBuilder.TclWorldResult
                {
                    Width = original.World.Width,
                    Height = original.World.Height,
                    BlockIdx = new uint[count],
                    Terrain = new byte[count],
                    Zone = new byte[count],
                    CellId = new ushort[count],
                    HasTpl = new System.Collections.BitArray(count),
                };
                for (int index = 0; index < count; index++)
                {
                    OriginalSaveTerrainTile tile = original.World.Terrain[index];
                    result.BlockIdx[index] = tile.BlockIndex;
                    result.Zone[index] = 4;
                    result.Terrain[index] = NativeTerrainByte(tile.MaterialType);
                    result.CellId[index] = tile.CellId;
                    result.HasTpl[index] = (tile.TemplateFlags & 1) != 0;
                }

                DiggableTerrain terrain = terrainObject.AddComponent<DiggableTerrain>();
                terrain.Init(result, null!, string.Empty, () => { });
                DiggableTerrain.SaveState terrainState = terrain.CaptureSaveState();
                terrainState.BlockIdx[100] = 59;
                terrainState.DigState[100] = 0xBu;
                terrainState.DigId[100] = 3u;
                terrainState.Terrain[100] = 2;
                terrainState.NativeTerrainFlags[100] = unchecked((ushort)(
                    terrainState.NativeTerrainFlags[100] & ~3 | 2));
                terrainState.CellId[100] = ushort.MaxValue;
                bool expectedTemplate = (original.World.Terrain[100].TemplateFlags & 1) == 0;
                terrainState.HasTemplate[100] = expectedTemplate;
                terrain.RestoreSaveState(terrainState);
                context.Terrain = terrain;

                FogOfWarState.SaveState fogState = CaptureNativeFog(original.World);
                fogState.Fog[100 * FogOfWarState.Players] = 0xEE;
                var fog = new FogOfWarState(original.World.Width, original.World.Height, (_, _) => 0);
                fog.RestoreSaveState(fogState);
                context.FogOfWar = fog;

                context.Fluids.Restore(baseline.RuntimeWorld.CreateFluidState());

                OriginalSaveCurrentArchivePatch patch =
                    new OriginalSaveRuntimePatchMapper().Capture(baseline, context);
                OriginalSaveArchiveDocument rebuilt =
                    new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);
                OriginalSaveTerrainTile changed = rebuilt.World.Terrain[100];

                Assert.That(changed.BlockIndex, Is.EqualTo(59));
                Assert.That(changed.DigState, Is.EqualTo(0xBu));
                Assert.That(changed.DigOrderIds, Is.EqualTo(3u));
                Assert.That(changed.MaterialType, Is.EqualTo(2));
                Assert.That(changed.NavigationState,
                    Is.EqualTo(original.World.Terrain[100].NavigationState),
                    "a Nav-less diagnostic fixture must not replace archived tile+0x06 with 0xff");
                Assert.That(changed.FloorAndExitState,
                    Is.EqualTo(original.World.Terrain[100].FloorAndExitState),
                    "a Nav-less diagnostic fixture must not replace archived tile+0x12 with zero");
                Assert.That(changed.CellId, Is.EqualTo(ushort.MaxValue));
                Assert.That((changed.TemplateFlags & 1) != 0, Is.EqualTo(expectedTemplate));
                Assert.That(changed.FogForPlayer(0), Is.EqualTo(0xEE));
                Assert.That(changed.FluidTypeForBuffer(0),
                    Is.EqualTo(original.World.Terrain[100].FluidTypeForBuffer(0)));
                Assert.That(changed.FluidTypeForBuffer(1),
                    Is.EqualTo(original.World.Terrain[100].FluidTypeForBuffer(1)));
                Assert.That(changed.Unknown1C, Is.EqualTo(original.World.Terrain[100].Unknown1C));
                Assert.That(changed.Unknown20, Is.EqualTo(original.World.Terrain[100].Unknown20));
                Assert.That(changed.Unknown24, Is.EqualTo(original.World.Terrain[100].Unknown24));
                Assert.That(changed.Unknown28, Is.EqualTo(original.World.Terrain[100].Unknown28));
                Assert.That(changed.Unknown2C, Is.EqualTo(original.World.Terrain[100].Unknown2C));
                Assert.That(changed.Unknown30, Is.EqualTo(original.World.Terrain[100].Unknown30));
                Assert.That(rebuilt.World.Fluid.Sources.Count,
                    Is.EqualTo(original.World.Fluid.Sources.Count));
                Assert.That(rebuilt.World.Fluid.Stoppers.Count,
                    Is.EqualTo(original.World.Fluid.Stoppers.Count));
                Assert.That(rebuilt.World.Fluid.SparseSets.Select(set => set.Count),
                    Is.EqualTo(original.World.Fluid.SparseSets.Select(set => set.Count)));
                Assert.DoesNotThrow(() => new OriginalSaveLoadPlanner().Prepare(rebuilt));

                context.Fluids.Restore(new FluidSystem.SaveState
                {
                    Enabled = true,
                    Cells = new[]
                    {
                        new FluidSystem.CellSaveState
                        {
                            X = 100,
                            Y = 0,
                            Fill = 1.5f,
                            TypeMask = (int)FluidSystem.FluidKind.Water,
                        },
                    },
                    Sources = new[]
                    {
                        new FluidSystem.SourceSaveState
                        {
                            Handle = baseline.Entities[0].ObjectId,
                            Type = "water",
                            TypeMask = (int)FluidSystem.FluidKind.Water,
                            ConfiguredVolume = 8d,
                            Volume = 3d,
                            Vpf = 0.5d,
                            HasVolume = true,
                            HasVpf = true,
                        },
                    },
                });
                OriginalSaveCurrentArchivePatch mutatedPatch =
                    new OriginalSaveRuntimePatchMapper().Capture(baseline, context);
                OriginalSaveArchiveDocument mutated =
                    new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, mutatedPatch);
                OriginalSaveTerrainTile fluidTile = mutated.World.Terrain[100];

                Assert.That(fluidTile.FluidTypeForBuffer(0), Is.EqualTo(1));
                Assert.That(fluidTile.FluidTypeForBuffer(1), Is.EqualTo(1));
                Assert.That(fluidTile.FluidFillForBuffer(0), Is.EqualTo(1.5f));
                Assert.That(fluidTile.FluidFillForBuffer(1), Is.EqualTo(1.5f));
                Assert.That(fluidTile.Unknown24, Is.Zero);
                Assert.That(fluidTile.Unknown28, Is.Zero);
                Assert.That(fluidTile.Unknown2C, Is.Zero);
                Assert.That(fluidTile.Unknown30, Is.Zero);
                Assert.That(fluidTile.TemplateFlags & 4, Is.Zero);
                Assert.That(mutated.World.Fluid.CurrentBufferIndex, Is.Zero);
                Assert.That(mutated.World.Fluid.PreviousBufferIndex, Is.EqualTo(1));
                Assert.That(mutated.World.Fluid.SparseSets.All(set => set.Count == 0), Is.True);
                Assert.That(mutated.World.Fluid.DebugEnabled, Is.False);
                Assert.That(mutated.World.Fluid.Sources, Has.Count.EqualTo(1));
                Assert.That(mutated.World.Fluid.Sources[0].ConfiguredVolumeUnits, Is.EqualTo(1024f));
                Assert.That(mutated.World.Fluid.Sources[0].RemainingVolumeUnits, Is.EqualTo(384f));
                Assert.That(mutated.World.Fluid.Sources[0].VolumePerFrameUnits, Is.EqualTo(64f));
                Assert.That(mutated.World.Fluid.Stoppers, Is.Empty);
                Assert.DoesNotThrow(() => new OriginalSaveLoadPlanner().Prepare(mutated));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void RuntimeArchiveServiceValidatesAndAtomicallyWritesCapturedNativeState()
        {
            string fixturePath = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(fixturePath),
                $"Original save fixture was not found at '{fixturePath}'.");

            var reader = new OriginalSaveArchiveReader();
            var codec = new OriginalSaveArchiveCodec(reader);
            OriginalSaveArchiveDocument original = codec.Read(fixturePath);
            OriginalSaveRuntimeSnapshot baseline = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(original));
            DigglesTclContext context = BuildIdentityStableRuntime(baseline, 900.5d);
            context.Entities[51].Position = new Vector3(110f, 32f, 6f);

            string directory = Path.Combine(
                Path.GetTempPath(), "DigglesOriginalRuntimeArchiveTests", Guid.NewGuid().ToString("N"));
            string destination = Path.Combine(directory, "captured.sav");
            try
            {
                var repository = new OriginalSaveRepository(codec);
                var service = new OriginalSaveRuntimeArchiveService(
                    new OriginalSaveRuntimePatchMapper(),
                    new OriginalSaveCurrentArchiveWriter(reader),
                    new OriginalSaveLoadPlanner(),
                    repository);

                service.CaptureAndWriteAtomic(destination, baseline, context);

                OriginalSaveArchiveDocument written = repository.Read(destination);
                Assert.That(written.Metadata.GameTime, Is.EqualTo(900.5d));
                Assert.That(written.Objects!.FindById(51)!.Position.X, Is.EqualTo(110f));
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void CodecExposesTypedTerrainAndFogState()
        {
            string tutorialPath = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            string testPath = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_test.sav");
            Assume.That(File.Exists(tutorialPath),
                $"Original save fixture was not found at '{tutorialPath}'.");
            Assume.That(File.Exists(testPath),
                $"Original save fixture was not found at '{testPath}'.");

            var codec = new OriginalSaveArchiveCodec();
            OriginalSaveArchiveDocument tutorial = codec.Read(tutorialPath);

            Assert.That(tutorial.World.Width, Is.EqualTo(500));
            Assert.That(tutorial.World.Height, Is.EqualTo(200));
            Assert.That(tutorial.World.Terrain, Has.Count.EqualTo(100000));
            Assert.That(tutorial.World.FogBeginRows, Is.EqualTo(Enumerable.Repeat(-1, 8)));
            Assert.That(tutorial.World.LightBegin, Is.EqualTo(33));
            Assert.That(tutorial.World.GroundBegin, Is.EqualTo(33));
            Assert.That(tutorial.World.UnknownGameState, Is.EqualTo(33));

            OriginalSaveTerrainTile first = tutorial.World.Terrain[0];
            Assert.That(first.BlockIndex, Is.Zero);
            Assert.That(first.NavigationState, Is.EqualTo(0xFF));
            Assert.That(first.DigState, Is.Zero);
            Assert.That(first.DigOrderIds, Is.Zero);
            Assert.That(first.TerrainState, Is.EqualTo(0x4B02));
            Assert.That(first.MaterialType, Is.EqualTo(2));
            Assert.That(first.FluidTypeForBuffer(0), Is.Zero);
            Assert.That(first.FluidTypeForBuffer(1), Is.Zero);
            Assert.That(first.FluidFillForBuffer(0), Is.Zero);
            Assert.That(first.FluidFillForBuffer(1), Is.Zero);
            Assert.That(first.FogState, Is.Zero);

            OriginalSaveTerrainTile templateEdge = tutorial.World.Terrain[32];
            Assert.That(templateEdge.TemplateFlags, Is.EqualTo(1));
            Assert.That(templateEdge.TerrainState, Is.EqualTo(0x80));
            Assert.That(templateEdge.MaterialType, Is.Zero);

            Assert.That(tutorial.World.Fluid.Sources, Is.Empty);
            Assert.That(tutorial.World.Fluid.Stoppers, Has.Count.EqualTo(9));
            Assert.That(tutorial.World.Fluid.CurrentBufferIndex, Is.EqualTo(1));
            Assert.That(tutorial.World.Fluid.PreviousBufferIndex, Is.Zero);
            Assert.That(tutorial.World.Fluid.SparseSets.All(set => set.Count == 0), Is.True);
            Assert.That(tutorial.World.Fluid.DebugEnabled, Is.False);
            OriginalSaveFluidStopperRecord stopper = tutorial.World.Fluid.Stoppers[0];
            Assert.That(stopper.ObjectId, Is.EqualTo(172));
            Assert.That(stopper.CachedPositionX, Is.EqualTo(77.2617f).Within(0.0001f));
            Assert.That(stopper.CachedPositionY, Is.EqualTo(30.5f));
            Assert.That(stopper.CachedPositionZ, Is.EqualTo(15f));
            Assert.That(stopper.Dirty, Is.False);
            Assert.That(
                new[] { stopper.MinX, stopper.MinY, stopper.MaxX, stopper.MaxY },
                Is.EqualTo(new[] { 0, -2, 1, 1 }));
            Assert.That(stopper.PermeabilityX, Is.Zero);
            Assert.That(stopper.PermeabilityY, Is.EqualTo(1f));

            Assert.That(tutorial.World.Fog.FadeTilesByPlayer[0], Has.Count.EqualTo(11415));
            Assert.That(tutorial.World.Fog.FadeTilesByPlayer[1], Has.Count.EqualTo(228));
            Assert.That(tutorial.World.Fog.FadeTilesByPlayer[2], Has.Count.EqualTo(227));
            Assert.That(tutorial.World.Fog.FadeTilesByPlayer.Skip(3).All(set => set.Count == 0),
                Is.True);
            OriginalSaveIntPair firstFadeTile = tutorial.World.Fog.FadeTilesByPlayer[0][0];
            Assert.That(firstFadeTile.First, Is.EqualTo(325));
            Assert.That(firstFadeTile.Second, Is.EqualTo(63));
            Assert.That(tutorial.World.Fog.EndOffset, Is.EqualTo(5395692));

            OriginalSaveArchiveDocument test = codec.Read(testPath);
            OriginalSaveTerrainTile marked = test.World.Terrain[19706];
            Assert.That(marked.BlockIndex, Is.EqualTo(1225));
            Assert.That(marked.DigState, Is.Zero);
            Assert.That(marked.DigOrderIds, Is.EqualTo(1));
            Assert.That(marked.DigOrderIdForPlayer(0), Is.EqualTo(1));
            Assert.That(marked.DigOrderIdForPlayer(1), Is.Zero);
            Assert.That(marked.TemplateFlags, Is.EqualTo(3));
            Assert.That(marked.MaterialType, Is.EqualTo(2));
            Assert.That(marked.FogForPlayer(0), Is.EqualTo(1));
            Assert.That(marked.FogForPlayer(1), Is.EqualTo(1));
            Assert.That(marked.FogForPlayer(2), Is.Zero);

            Assert.That(test.World.Fluid.Sources, Has.Count.EqualTo(7));
            OriginalSaveFluidSourceRecord source = test.World.Fluid.Sources[0];
            Assert.That(source.ObjectId, Is.EqualTo(1670));
            Assert.That(source.PositionX, Is.Zero);
            Assert.That(source.PositionY, Is.EqualTo(-0.2f));
            Assert.That(source.PositionZ, Is.Zero);
            Assert.That(source.FluidType, Is.EqualTo(1));
            Assert.That(source.ConfiguredVolumeUnits, Is.EqualTo(2.9388178e29f));
            Assert.That(source.RemainingVolumeUnits, Is.EqualTo(-1f));
            Assert.That(source.HeightLimit, Is.EqualTo(-1f));
            Assert.That(source.VolumePerFrameUnits, Is.EqualTo(-1279872f));
            Assert.That(source.HasVolume, Is.False);
            Assert.That(source.HasHeightLimit, Is.False);
            Assert.That(source.HasVolumePerFrame, Is.True);
            Assert.That(source.Vented, Is.False);

            OriginalSaveFluidSourceRecord activeSource = test.World.Fluid.Sources[1];
            Assert.That(activeSource.ConfiguredVolumeUnits, Is.EqualTo(64000f));
            Assert.That(activeSource.RemainingVolumeUnits, Is.EqualTo(0f));
            Assert.That(activeSource.VolumePerFrameUnits, Is.EqualTo(384f));
            Assert.That(activeSource.HasVolume, Is.True);
            Assert.That(activeSource.HasVolumePerFrame, Is.True);

            Assert.That(tutorial.Objects, Is.Not.Null);
            Assert.That(tutorial.Objects!, Has.Count.EqualTo(484));
            OriginalSaveObjectDocument firstObject = tutorial.Objects![0];
            Assert.That(firstObject.ObjectId, Is.EqualTo(31));
            Assert.That(firstObject.ClassName, Is.EqualTo("Dummy_Wand_so_verlies_c"));
            Assert.That(firstObject.ObjectType, Is.Zero);
            Assert.That(firstObject.Position.X, Is.EqualTo(70.49f).Within(0.01f));
            Assert.That(firstObject.Position.Y, Is.EqualTo(40.5f).Within(0.01f));
            Assert.That(firstObject.Position.Z, Is.EqualTo(3.02f).Within(0.01f));
            Assert.That(firstObject.InstanceName, Is.EqualTo("Dummy_Wand_so_verlies_c_1"));
            Assert.That(firstObject.StateObjectId, Is.EqualTo(31));
            Assert.That(firstObject.Owner, Is.EqualTo(-1));
            Assert.That(firstObject.DisplayState, Is.Empty);
            Assert.That(firstObject.HasParticleState, Is.False);
            Assert.That(firstObject.ChildObjectIds, Is.Empty);
            Assert.That(firstObject.TclVariables, Is.Empty);

            OriginalSaveObjectDocument? gnome = tutorial.Objects.FindById(51);
            Assert.That(gnome, Is.Not.Null);
            Assert.That(gnome!.ClassName, Is.EqualTo("Zwerg"));
            Assert.That(gnome.ObjectType, Is.EqualTo(1));
            Assert.That(gnome.InstanceName, Is.EqualTo("Charly"));
            Assert.That(gnome.StateObjectId, Is.EqualTo(-1));
            Assert.That(gnome.Owner, Is.Zero);
            Assert.That(gnome.DisplayState, Is.EqualTo("EinZwerg"));
            Assert.That(gnome.HasParticleState, Is.True);
            Assert.That(gnome.CommonState.ParticleState.RawValues, Has.Count.EqualTo(160));
            Assert.That(gnome.CommonState.ParticleState.Sources, Has.Count.EqualTo(32));
            Assert.That(gnome.CommonState.GridState, Has.Count.EqualTo(512));
            Assert.That(gnome.CommonState.FogOfWarSizeX, Is.EqualTo(112));
            Assert.That(gnome.CommonState.FogOfWarSizeY, Is.EqualTo(64));
            Assert.That(gnome.CommonState.FogOfWarLastX, Is.EqualTo(92.38629f).Within(0.0001f));
            Assert.That(gnome.CommonState.FogOfWarLastY, Is.EqualTo(30.49998f).Within(0.0001f));
            Assert.That(gnome.CommonState.LinkedObjectId, Is.EqualTo(-1));
            Assert.That(gnome.CommonState.LinkedSlot, Is.EqualTo(-1));
            Assert.That(gnome.CommonState.PlaceSnapMode, Is.Zero);
            Assert.That(gnome.CommonState.ObjectFlags, Is.EqualTo(18384673));
            Assert.That(gnome.CommonState.DirtyFlags, Is.EqualTo(15));
            Assert.That(gnome.CommonState.TriggerRecordId, Is.EqualTo(-1));
            Assert.That(gnome.ChildObjectIds, Is.EqualTo(new[] { 50, 1787 }));
            Assert.That(gnome.TryGetTclVariable("myglasses", out string myGlasses), Is.True);
            Assert.That(myGlasses, Is.EqualTo("0"));
            Assert.That(tutorial.Objects.FindById(50), Is.Not.Null);
            Assert.That(tutorial.Objects.FindById(1787), Is.Not.Null);

            OriginalSaveLoadPlan loadPlan = new OriginalSaveLoadPlanner().Prepare(tutorial);
            Assert.That(loadPlan.ObjectsById, Has.Count.EqualTo(484));
            Assert.That(loadPlan.ObjectsById[51], Is.SameAs(gnome));
            Assert.That(loadPlan.ObjectsById.ContainsKey(50), Is.True);
            Assert.That(loadPlan.ObjectsById.ContainsKey(1787), Is.True);

            OriginalSaveRuntimeSnapshot staged = new OriginalSaveRuntimeStager().Stage(loadPlan);
            Assert.That(staged.Entities, Has.Count.EqualTo(484));
            Assert.That(staged.GameTime, Is.EqualTo(441.428134966944).Within(1e-9));
            Assert.That(staged.EntitiesById[51].InstanceName, Is.EqualTo("Charly"));
            Assert.That(staged.EntitiesById[51].FogOfWarSizeX, Is.EqualTo(112));
            Assert.That(staged.ReferrersByObjectId[50], Does.Contain(51));
            Assert.That(staged.ReferrersByObjectId[1787], Does.Contain(51));

            DiggableTerrain.SaveState stagedTerrain =
                staged.RuntimeWorld.CreateTerrainState(Array.Empty<byte>());
            FogOfWarState.SaveState stagedFog = staged.RuntimeWorld.CreateFogState();
            FluidSystem.SaveState stagedFluid = staged.RuntimeWorld.CreateFluidState();
            Assert.That(stagedTerrain.Width, Is.EqualTo(tutorial.World.Width));
            Assert.That(stagedTerrain.PlayerDigMarkId, Is.Zero,
                "The native transient DigMark counter is reinitialized rather than serialized.");
            Assert.That(stagedTerrain.BlockIdx[0], Is.EqualTo(tutorial.World.Terrain[0].BlockIndex));
            Assert.That(stagedTerrain.Terrain[0], Is.EqualTo(2));
            Assert.That(stagedTerrain.CellId[0], Is.EqualTo(tutorial.World.Terrain[0].CellId));
            Assert.That(stagedTerrain.Zone[0], Is.Zero);
            Assert.That(
                stagedTerrain.NativeTerrainFlags[0],
                Is.EqualTo(tutorial.World.Terrain[0].NativeTerrainFlags));
            Assert.That(stagedTerrain.HasTemplate[32], Is.True);
            Assert.That(stagedTerrain.RenderXMin, Is.Zero);
            Assert.That(stagedTerrain.RenderYMin, Is.Zero);
            Assert.That(stagedTerrain.RenderXMax, Is.EqualTo(tutorial.World.Width - 1));
            Assert.That(stagedTerrain.RenderYMax, Is.EqualTo(tutorial.World.Height - 1));
            Assert.That(staged.RuntimeWorld.MinX, Is.EqualTo(tutorial.World.MinX));
            Assert.That(staged.RuntimeWorld.MinY, Is.EqualTo(tutorial.World.MinY));
            Assert.That(staged.RuntimeWorld.MaxXExclusive, Is.EqualTo(tutorial.World.MaxXExclusive));
            Assert.That(staged.RuntimeWorld.MaxYExclusive, Is.EqualTo(tutorial.World.MaxYExclusive));
            Assert.That(staged.RuntimeWorld.LightBegin, Is.EqualTo(tutorial.World.LightBegin));
            Assert.That(staged.RuntimeWorld.GroundBegin, Is.EqualTo(tutorial.World.GroundBegin));
            BdbPmpWorldBuilder.TclWorldResult stagedWorld = staged.RuntimeWorld.CreateWorldResult();
            Assert.That(stagedWorld.NativeTerrainFlags[0],
                Is.EqualTo(tutorial.World.Terrain[0].NativeTerrainFlags));
            Assert.That(stagedWorld.MinX, Is.EqualTo(tutorial.World.MinX));
            Assert.That(stagedWorld.MaxXExclusive, Is.EqualTo(tutorial.World.MaxXExclusive));
            Assert.That(stagedFog.Fog[0], Is.EqualTo(tutorial.World.Terrain[0].FogForPlayer(0)));
            Assert.That(stagedFog.FadeItems, Has.Length.EqualTo(11870));
            Assert.That(stagedFluid.Sources, Is.Empty);
            Assert.That(stagedFluid.Stoppers, Has.Length.EqualTo(9));
            Assert.DoesNotThrow(() => FluidSystem.ValidateSaveState(
                stagedFluid,
                tutorial.World.Width,
                tutorial.World.Height,
                staged.EntitiesById.ContainsKey));

            var candidateContext = new DigglesTclContext(new TclInterpreter());
            OriginalSaveRuntimeLightingState lighting =
                new OriginalSaveRuntimeLightingInitializer().InitializeCandidate(
                    staged.RuntimeWorld,
                    candidateContext,
                    (_, _) => 0);
            Assert.That(candidateContext.LightBeginRow, Is.EqualTo(tutorial.World.LightBegin));
            Assert.That(candidateContext.GroundBeginRow, Is.EqualTo(tutorial.World.GroundBegin));
            Assert.That(candidateContext.BaseLight, Is.SameAs(lighting.BaseLight));
            Assert.That(candidateContext.TunnelTorches, Is.SameAs(lighting.TunnelTorches));
            Assert.That(lighting.BaseLight.LightBegin, Is.EqualTo(tutorial.World.LightBegin));
            Assert.That(lighting.BaseLight.At(0, 0), Is.EqualTo(BaseLightMap.Daylight));
            Assert.That(lighting.TunnelTorches.LightBegin, Is.EqualTo(tutorial.World.LightBegin));
            Assert.That(lighting.TunnelTorches.SkyLitAt, Is.Not.Null);
            Assert.That(lighting.TunnelTorches.SkyLitAt!(0, 0), Is.True);
        }

        [Test]
        public void RepositoryValidatesAndAtomicallyReplacesNativeSave()
        {
            string fixturePath = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(fixturePath),
                $"Original save fixture was not found at '{fixturePath}'.");

            string directory = Path.Combine(
                Path.GetTempPath(), "DigglesOriginalSaveRepositoryTests", Guid.NewGuid().ToString("N"));
            string destination = Path.Combine(directory, "atomic_test.sav");
            try
            {
                var codec = new OriginalSaveArchiveCodec();
                var repository = new OriginalSaveRepository(codec);
                OriginalSaveArchiveDocument document = repository.Read(fixturePath);

                Directory.CreateDirectory(directory);
                File.WriteAllBytes(destination, new byte[] { 1, 2, 3, 4 });
                repository.WriteAtomic(destination, document);

                OriginalSaveArchiveDocument written = repository.Read(destination);
                Assert.That(written.Metadata.Version, Is.EqualTo(0xA5));
                Assert.That(written.Metadata.World.Width, Is.EqualTo(500));
                CollectionAssert.AreEqual(document.CopyPayload(), written.CopyPayload());
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void LoadPreparationServiceReturnsValidatedSnapshotWithoutRuntimeMutation()
        {
            string fixturePath = Path.Combine(
                OriginalDataTestFixture.DataRoot, "Gamesave", "save_tutorial.sav");
            Assume.That(File.Exists(fixturePath),
                $"Original save fixture was not found at '{fixturePath}'.");

            var codec = new OriginalSaveArchiveCodec();
            var service = new OriginalSaveLoadPreparationService(
                new OriginalSaveRepository(codec),
                new OriginalSaveLoadPlanner(),
                new OriginalSaveRuntimeStager());

            OriginalSaveRuntimeSnapshot snapshot = service.Prepare(fixturePath);

            Assert.That(snapshot.SourceDocument.Metadata.Version, Is.EqualTo(0xA5));
            Assert.That(snapshot.Entities, Has.Count.EqualTo(484));
            Assert.That(snapshot.EntitiesById[51].InstanceName, Is.EqualTo("Charly"));
        }

        [Test]
        public void RejectsVersionOutsideOriginalLoaderRange()
        {
            using var save = BuildMinimalSave(0xA6, 4);
            var exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveArchiveReader().ReadMetadata(save));
            Assert.That(exception!.Message, Does.Contain("0xA6"));
        }

        [Test]
        public void RejectsRecognizedLegacyVersionUntilItsLayoutIsImplemented()
        {
            using var save = BuildMinimalSave(0xA4, 4);
            var exception = Assert.Throws<NotSupportedException>(
                () => new OriginalSaveArchiveReader().ReadMetadata(save));
            Assert.That(exception!.Message, Does.Contain("0xA4"));
            Assert.That(exception.Message, Does.Contain("recognized"));
        }

        [Test]
        public void CodecRejectsPayloadAboveConfiguredDecompressionLimit()
        {
            using var save = BuildCompressedPayload(new byte[1025]);
            var codec = new OriginalSaveArchiveCodec(
                new OriginalSaveArchiveReader(),
                new OriginalSaveArchiveCodecOptions(1024));

            var exception = Assert.Throws<InvalidDataException>(() => codec.Read(save));
            Assert.That(exception!.Message, Does.Contain("1024"));
        }

        [Test]
        public void RejectsIncorrectCalculatedWorldMarker()
        {
            using var save = BuildMinimalSave(0xA5, 99);
            var exception = Assert.Throws<InvalidDataException>(
                () => new OriginalSaveArchiveReader().ReadMetadata(save));
            Assert.That(exception!.Message, Does.Contain("expected 4, found 99"));
        }

        static DigglesTclContext BuildIdentityStableRuntime(
            OriginalSaveRuntimeSnapshot baseline,
            double gameTime)
        {
            var context = new DigglesTclContext(new TclInterpreter());
            var classes = new System.Collections.Generic.Dictionary<string, DigglesClass>(
                StringComparer.Ordinal);
            foreach (OriginalSaveRuntimeEntitySnapshot source in baseline.Entities)
            {
                if (!classes.TryGetValue(source.ClassName, out DigglesClass? objectClass))
                {
                    objectClass = new DigglesClass { Name = source.ClassName };
                    classes.Add(source.ClassName, objectClass);
                }
                context.RegisterEntityAtHandle(new DigglesTclEntity
                {
                    Class = objectClass!,
                    InstanceName = source.InstanceName,
                    // Identity fields the real session stager restores; this minimal builder
                    // must too, or the no-op diff invents patches (an empty InfoString patch
                    // costs the tutorial gnomes their "EinZwerg" string).
                    InfoString = source.DisplayState,
                    RestoredStateLive = source.StateLiveWord,
                    RestoredStateTarget = source.StateTargetWord,
                    RestoredStateEnabled = source.StateEnabledWord,
                    LiveStateIndex = source.StateLiveWord,
                    StateEnabledCount = source.StateEnabledWord - 1,
                    Position = new Vector3(source.Position.X, source.Position.Y, source.Position.Z),
                    Velocity = new Vector3(source.Velocity.X, source.Velocity.Y, source.Velocity.Z),
                    Rotation = new Vector3(source.Rotation.X, source.Rotation.Y, source.Rotation.Z),
                    Owner = source.Owner,
                    WorkStart = source.WorkStart,
                    WorkLen = source.WorkLength,
                    FowSizeX = source.FogOfWarSizeX,
                    FowSizeY = source.FogOfWarSizeY,
                    FowLastX = source.FogOfWarLastX,
                    FowLastY = source.FogOfWarLastY,
                    LinkedToHandle = source.LinkedObjectId > 0 &&
                        baseline.EntitiesById.ContainsKey(source.LinkedObjectId)
                            ? source.LinkedObjectId
                            : 0,
                    LinkedSlot = source.LinkedSlot,
                    PlaceSnapMode = source.PlaceSnapMode,
                    DigSetId = source.DigOrderId,
                }, source.ObjectId);
            }
            _ = new DigglesGameSim(context) { GameTime = gameTime };
            return context;
        }

        static FogOfWarState.SaveState CaptureNativeFog(OriginalSaveWorldDocument world)
        {
            int tileCount = world.Terrain.Count;
            var fog = new byte[tileCount * FogOfWarState.Players];
            for (int tile = 0; tile < tileCount; tile++)
                for (int player = 0; player < FogOfWarState.Players; player++)
                    fog[tile * FogOfWarState.Players + player] =
                        world.Terrain[tile].FogForPlayer(player);

            var offsets = new int[FogOfWarState.Players + 1];
            int fadeCount = 0;
            for (int player = 0; player < FogOfWarState.Players; player++)
            {
                offsets[player] = fadeCount;
                fadeCount += world.Fog.FadeTilesByPlayer[player].Count;
            }
            offsets[FogOfWarState.Players] = fadeCount;
            var fadeItems = new int[fadeCount];
            int cursor = 0;
            for (int player = 0; player < FogOfWarState.Players; player++)
                foreach (OriginalSaveIntPair pair in world.Fog.FadeTilesByPlayer[player])
                    fadeItems[cursor++] = pair.Second * world.Width + pair.First;

            return new FogOfWarState.SaveState
            {
                Width = world.Width,
                Height = world.Height,
                Fog = fog,
                FowBegin = world.FogBeginRows.ToArray(),
                FadeOffsets = offsets,
                FadeItems = fadeItems,
                ExploredRect = new int[FogOfWarState.Players * 4],
                Enabled = true,
                ShowFog = true,
            };
        }

        static byte NativeTerrainByte(int material)
        {
            return material switch
            {
                0 => 4,
                1 => 3,
                2 => 2,
                3 => 5,
                _ => throw new ArgumentOutOfRangeException(nameof(material)),
            };
        }

        static MemoryStream BuildMinimalSave(int version, int marker4)
        {
            var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
            using (var writer = new BinaryWriter(gzip))
            {
                writer.Write(version);
                writer.Write(1);
                writer.Write((byte)0);
                writer.Write(12.5d);
                writer.Write(1);
                writer.Write(new byte[205]);
                writer.Write(2);
                writer.Write(3);
                writer.Write(1);
                writer.Write(1);
                writer.Write(0);
                writer.Write(0);
                writer.Write(1);
                writer.Write(1);
                writer.Write(new byte[53]);
                writer.Write(new byte[40]);
                writer.Write(0);
                writer.Write(0);
                writer.Write(new byte[8]);
                for (int i = 0; i < 4; i++) writer.Write(0);
                writer.Write((byte)0);
                for (int i = 0; i < 8; i++) writer.Write(0);
                writer.Write(marker4);
            }
            output.Position = 0;
            return output;
        }

        static MemoryStream BuildCompressedPayload(byte[] payload)
        {
            var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                gzip.Write(payload, 0, payload.Length);
            output.Position = 0;
            return output;
        }
    }
}
