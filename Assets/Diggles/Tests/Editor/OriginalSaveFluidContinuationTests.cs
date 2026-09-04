#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveFluidContinuationTests
    {
        // Floors, not pins: the shared Gamesave directory legitimately grows as new
        // native-written reference saves are added, so these corpus counts only rise.
        private const int ExpectedCorpusSaveCount = 103;
        private const int ExpectedActiveSetSaveCount = 19;
        private const int ExpectedDivergentBufferSaveCount = 83;

        [Test]
        [Explicit("Pins exact counts of a developer-local save corpus")]
        public void EveryCorpusFluidStateStages()
        {
            string[] paths = FindCorpusPaths();
            var codec = new OriginalSaveArchiveCodec();
            var planner = new OriginalSaveLoadPlanner();
            var stager = new OriginalSaveRuntimeStager();
            int activeSetSaveCount = 0;
            int divergentBufferSaveCount = 0;

            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                OriginalSaveRuntimeSnapshot snapshot = stager.Stage(planner.Prepare(document));
                FluidSystem.SaveState fluid = snapshot.RuntimeWorld.CreateFluidState();
                Assert.DoesNotThrow(
                    () => FluidSystem.ValidateSaveState(
                        fluid,
                        document.World.Width,
                        document.World.Height,
                        snapshot.EntitiesById.ContainsKey),
                    path);

                if (document.World.Fluid.SparseSets[1].Count != 0 ||
                    document.World.Fluid.SparseSets[3].Count != 0)
                {
                    activeSetSaveCount++;
                }
                if (HasDivergentFluidBuffers(document.World)) divergentBufferSaveCount++;
            }

            Assert.That(activeSetSaveCount, Is.GreaterThanOrEqualTo(ExpectedActiveSetSaveCount));
            Assert.That(divergentBufferSaveCount, Is.GreaterThanOrEqualTo(ExpectedDivergentBufferSaveCount));
        }

        [Test]
        public void BufferOneIsAuthoritative()
        {
            OriginalSaveArchiveDocument document = ReadFixture("save_test.sav");
            int drainedTrailIndex = -1;
            for (int index = 0; index < document.World.Terrain.Count; index++)
            {
                OriginalSaveTerrainTile tile = document.World.Terrain[index];
                if (tile.FluidTypeForBuffer(0) != 0 &&
                    tile.FluidTypeForBuffer(1) == 0 &&
                    tile.FluidFillForBuffer(1) == 0f)
                {
                    drainedTrailIndex = index;
                    break;
                }
            }
            Assert.That(drainedTrailIndex, Is.GreaterThanOrEqualTo(0),
                "save_test.sav has no drained trail that distinguishes the two native buffers.");

            OriginalSaveTerrainTile drainedTrail = document.World.Terrain[drainedTrailIndex];
            OriginalSaveRuntimeSnapshot snapshot = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(document));
            FluidSystem.SaveState stagedFluid = snapshot.RuntimeWorld.CreateFluidState();
            int x = drainedTrailIndex % document.World.Width;
            int y = drainedTrailIndex / document.World.Width;
            Assert.That(stagedFluid.Cells.Any(cell => cell.X == x && cell.Y == y), Is.False);

            var context = new DigglesTclContext(new TclInterpreter());
            context.Fluids.Restore(stagedFluid);
            Assert.That(drainedTrail.FluidTypeForBuffer(0), Is.Not.Zero);
            Assert.That(drainedTrail.FluidTypeForBuffer(1), Is.Zero);
            Assert.That(context.Fluids.GetFill(x, y), Is.Zero);
            Assert.That(context.Fluids.GetFluidType(x, y), Is.Zero);
        }

        [Test]
        [Explicit("Pins exact counts of a developer-local save corpus")]
        public void SettledFluidRoundTripsLossless()
        {
            OriginalSaveArchiveDocument original = ReadCompatibilityFixture();
            OriginalSaveRuntimeSnapshot baseline = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(original));
            using var runtime = new RuntimeWorldHarness(baseline);
            var reader = new OriginalSaveArchiveReader();

            OriginalSaveCurrentArchivePatch patch =
                new OriginalSaveRuntimePatchMapper().Capture(baseline, runtime.Context);
            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);

            AssertCompatibilityShadowMatches(
                baseline.RuntimeWorld.FluidCompatibility,
                original.World);
            AssertNativeFluidStateMatches(original.World, rebuilt.World);
            CollectionAssert.AreEqual(original.CopyPayload(), rebuilt.CopyPayload());
        }

        [Test]
        public void MutatedFluidSynthesizesSettledForm()
        {
            OriginalSaveArchiveDocument original = ReadFixture("save_tutorial.sav");
            OriginalSaveRuntimeSnapshot baseline = new OriginalSaveRuntimeStager().Stage(
                new OriginalSaveLoadPlanner().Prepare(original));
            using var runtime = new RuntimeWorldHarness(baseline);
            FluidSystem.SaveState baselineFluid = baseline.RuntimeWorld.CreateFluidState();
            int sourceHandle = baseline.Entities[0].ObjectId;
            const int liveX = 12;
            const int liveY = 34;
            var mutatedFluid = new FluidSystem.SaveState
            {
                Enabled = true,
                Cells = new[]
                {
                    new FluidSystem.CellSaveState
                    {
                        X = liveX,
                        Y = liveY,
                        Fill = 1.5f,
                        TypeMask = (int)FluidSystem.FluidKind.Water,
                    },
                },
                Sources = new[]
                {
                    new FluidSystem.SourceSaveState
                    {
                        Handle = sourceHandle,
                        PositionX = 1.25f,
                        PositionY = -0.5f,
                        PositionZ = 2.5f,
                        Type = "water",
                        TypeMask = (int)FluidSystem.FluidKind.Water,
                        ConfiguredVolume = 8d,
                        Volume = 3d,
                        Vpf = 0.5d,
                        HasVolume = true,
                        HasVpf = true,
                        Vented = true,
                    },
                },
                Stoppers = baselineFluid.Stoppers,
            };
            runtime.Context.Fluids.Restore(mutatedFluid);
            var reader = new OriginalSaveArchiveReader();

            OriginalSaveCurrentArchivePatch patch =
                new OriginalSaveRuntimePatchMapper().Capture(baseline, runtime.Context);
            OriginalSaveArchiveDocument rebuilt =
                new OriginalSaveCurrentArchiveWriter(reader).Rebuild(original, patch);

            int liveIndex = liveY * rebuilt.World.Width + liveX;
            for (int index = 0; index < rebuilt.World.Terrain.Count; index++)
            {
                OriginalSaveTerrainTile tile = rebuilt.World.Terrain[index];
                int expectedType = index == liveIndex ? (int)FluidSystem.FluidKind.Water : 0;
                float expectedFill = index == liveIndex ? 1.5f : 0f;
                if (tile.FluidTypeForBuffer(0) != expectedType ||
                    tile.FluidTypeForBuffer(1) != expectedType ||
                    tile.FluidFillForBuffer(0) != expectedFill ||
                    tile.FluidFillForBuffer(1) != expectedFill ||
                    tile.Unknown24 != 0 || tile.Unknown28 != 0 ||
                    tile.Unknown2C != 0 || tile.Unknown30 != 0 ||
                    (tile.TemplateFlags & 4) != 0)
                {
                    Assert.Fail($"Synthesized fluid tile {index} is not in native settled form.");
                }
            }

            Assert.That(rebuilt.World.Fluid.CurrentBufferIndex, Is.Zero);
            Assert.That(rebuilt.World.Fluid.PreviousBufferIndex, Is.EqualTo(1));
            Assert.That(rebuilt.World.Fluid.SparseSets.All(set => set.Count == 0), Is.True,
                "Phase C intentionally leaves flowing regions dormant until a source or dig touches them.");
            Assert.That(rebuilt.World.Fluid.DebugEnabled, Is.False);
            AssertMutatedSourceMatches(rebuilt.World.Fluid.Sources, sourceHandle);
            AssertStoppersMatch(mutatedFluid.Stoppers, rebuilt.World.Fluid.Stoppers);

            byte[] rebuiltPayload = rebuilt.CopyPayload();
            using var compressed = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(
                compressed, System.IO.Compression.CompressionLevel.Fastest, true))
            {
                gzip.Write(rebuiltPayload, 0, rebuiltPayload.Length);
            }
            compressed.Position = 0;
            OriginalSaveArchiveMetadata reparsed = reader.ReadMetadata(compressed);
            Assert.That(reparsed.Version, Is.EqualTo(OriginalSaveArchiveReader.CurrentVersion));
            Assert.DoesNotThrow(() => new OriginalSaveLoadPlanner().Prepare(rebuilt));
        }

        private static bool HasDivergentFluidBuffers(OriginalSaveWorldDocument world)
        {
            for (int index = 0; index < world.Terrain.Count; index++)
            {
                OriginalSaveTerrainTile tile = world.Terrain[index];
                if (tile.FluidTypeForBuffer(0) != tile.FluidTypeForBuffer(1) ||
                    tile.FluidFillForBuffer(0) != tile.FluidFillForBuffer(1))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasNonzeroFluidFlow(OriginalSaveWorldDocument world)
        {
            for (int index = 0; index < world.Terrain.Count; index++)
            {
                OriginalSaveTerrainTile tile = world.Terrain[index];
                if (tile.Unknown24 != 0 || tile.Unknown28 != 0 ||
                    tile.Unknown2C != 0 || tile.Unknown30 != 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static void AssertCompatibilityShadowMatches(
            OriginalFluidCompatibilityState compatibility,
            OriginalSaveWorldDocument world)
        {
            Assert.That(compatibility.TileCount, Is.EqualTo(world.Terrain.Count));
            Assert.That(compatibility.CurrentBufferIndex,
                Is.EqualTo(world.Fluid.CurrentBufferIndex));
            Assert.That(compatibility.PreviousBufferIndex,
                Is.EqualTo(world.Fluid.PreviousBufferIndex));
            Assert.That(compatibility.DebugEnabled, Is.EqualTo(world.Fluid.DebugEnabled));
            Assert.That(compatibility.SparseSets, Has.Count.EqualTo(world.Fluid.SparseSets.Count));

            for (int index = 0; index < compatibility.TileCount; index++)
            {
                OriginalFluidCompatibilityTileState exact = compatibility.GetTile(index);
                OriginalSaveTerrainTile tile = world.Terrain[index];
                if (exact.TypeBuffer0 != tile.FluidTypeForBuffer(0) ||
                    exact.TypeBuffer1 != tile.FluidTypeForBuffer(1) ||
                    exact.FillBuffer0Bits != tile.Unknown1C ||
                    exact.FillBuffer1Bits != tile.Unknown20 ||
                    exact.CacheWord0 != tile.Unknown24 || exact.CacheWord1 != tile.Unknown28 ||
                    exact.CacheWord2 != tile.Unknown2C || exact.CacheWord3 != tile.Unknown30)
                {
                    Assert.Fail($"Compatibility fluid tile {index} differs from its native bytes.");
                }
            }

            for (int set = 0; set < compatibility.SparseSets.Count; set++)
            {
                Assert.That(compatibility.SparseSets[set],
                    Has.Count.EqualTo(world.Fluid.SparseSets[set].Count));
                for (int index = 0; index < compatibility.SparseSets[set].Count; index++)
                {
                    Assert.That(compatibility.SparseSets[set][index].First,
                        Is.EqualTo(world.Fluid.SparseSets[set][index].First));
                    Assert.That(compatibility.SparseSets[set][index].Second,
                        Is.EqualTo(world.Fluid.SparseSets[set][index].Second));
                }
            }
        }

        private static void AssertNativeFluidStateMatches(
            OriginalSaveWorldDocument expected,
            OriginalSaveWorldDocument actual)
        {
            Assert.That(actual.Fluid.CurrentBufferIndex,
                Is.EqualTo(expected.Fluid.CurrentBufferIndex));
            Assert.That(actual.Fluid.PreviousBufferIndex,
                Is.EqualTo(expected.Fluid.PreviousBufferIndex));
            Assert.That(actual.Fluid.DebugEnabled, Is.EqualTo(expected.Fluid.DebugEnabled));
            for (int index = 0; index < expected.Terrain.Count; index++)
            {
                OriginalSaveTerrainTile left = expected.Terrain[index];
                OriginalSaveTerrainTile right = actual.Terrain[index];
                if (left.FluidTypeForBuffer(0) != right.FluidTypeForBuffer(0) ||
                    left.FluidTypeForBuffer(1) != right.FluidTypeForBuffer(1) ||
                    left.Unknown1C != right.Unknown1C || left.Unknown20 != right.Unknown20 ||
                    left.Unknown24 != right.Unknown24 || left.Unknown28 != right.Unknown28 ||
                    left.Unknown2C != right.Unknown2C || left.Unknown30 != right.Unknown30)
                {
                    Assert.Fail($"Round-tripped fluid tile {index} differs from its native bytes.");
                }
            }

            for (int set = 0; set < expected.Fluid.SparseSets.Count; set++)
            {
                Assert.That(actual.Fluid.SparseSets[set],
                    Has.Count.EqualTo(expected.Fluid.SparseSets[set].Count));
                for (int index = 0; index < expected.Fluid.SparseSets[set].Count; index++)
                {
                    Assert.That(actual.Fluid.SparseSets[set][index].First,
                        Is.EqualTo(expected.Fluid.SparseSets[set][index].First));
                    Assert.That(actual.Fluid.SparseSets[set][index].Second,
                        Is.EqualTo(expected.Fluid.SparseSets[set][index].Second));
                }
            }
        }

        private static void AssertMutatedSourceMatches(
            OriginalSaveFluidSourceCollection sources,
            int sourceHandle)
        {
            Assert.That(sources, Has.Count.EqualTo(1));
            OriginalSaveFluidSourceRecord source = sources[0];
            Assert.That(source.ObjectId, Is.EqualTo(sourceHandle));
            Assert.That(source.PositionX, Is.EqualTo(1.25f));
            Assert.That(source.PositionY, Is.EqualTo(-0.5f));
            Assert.That(source.PositionZ, Is.EqualTo(2.5f));
            Assert.That(source.FluidType, Is.EqualTo((int)FluidSystem.FluidKind.Water));
            Assert.That(source.ConfiguredVolumeUnits, Is.EqualTo(1024f));
            Assert.That(source.RemainingVolumeUnits, Is.EqualTo(384f));
            Assert.That(source.HeightLimit, Is.EqualTo(OriginalSaveFluidSourceRecord.DisabledValue));
            Assert.That(source.VolumePerFrameUnits, Is.EqualTo(64f));
            Assert.That(source.Vented, Is.True);
        }

        private static void AssertStoppersMatch(
            IReadOnlyList<FluidSystem.StopperSaveState> expected,
            OriginalSaveFluidStopperCollection actual)
        {
            FluidSystem.StopperSaveState[] ordered = expected.ToArray();
            Array.Sort(ordered, (left, right) => left.Handle.CompareTo(right.Handle));
            Assert.That(actual, Has.Count.EqualTo(ordered.Length));
            for (int index = 0; index < ordered.Length; index++)
            {
                FluidSystem.StopperSaveState source = ordered[index];
                OriginalSaveFluidStopperRecord stopper = actual[index];
                Assert.That(stopper.ObjectId, Is.EqualTo(source.Handle));
                Assert.That(stopper.MinX, Is.EqualTo((int)Math.Min(source.StartX, source.EndX)));
                Assert.That(stopper.MinY, Is.EqualTo((int)Math.Min(source.StartY, source.EndY)));
                Assert.That(stopper.MaxX, Is.EqualTo((int)Math.Max(source.StartX, source.EndX)));
                Assert.That(stopper.MaxY, Is.EqualTo((int)Math.Max(source.StartY, source.EndY)));
                Assert.That(stopper.PermeabilityX, Is.EqualTo(source.Permeability));
                Assert.That(stopper.PermeabilityY, Is.EqualTo(source.Permeability));
                Assert.That(stopper.Dirty, Is.True);
            }
        }

        private static string[] FindCorpusPaths()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            // The port's own quicksaves land in this shared directory and are out of scope for
            // the native-corpus assertions.
            paths = Array.FindAll(paths, p => !OriginalDataTestFixture.IsPortWrittenSaveName(Path.GetFileName(p)));
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            Assert.That(paths, Has.Length.GreaterThanOrEqualTo(ExpectedCorpusSaveCount));
            return paths;
        }

        private static OriginalSaveArchiveDocument ReadFixture(string fileName)
        {
            string path = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave", fileName);
            Assume.That(File.Exists(path), $"Original save fixture was not found at '{path}'.");
            return new OriginalSaveArchiveCodec().Read(path);
        }

        private static OriginalSaveArchiveDocument ReadCompatibilityFixture()
        {
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in FindCorpusPaths())
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                if ((document.World.Fluid.SparseSets[1].Count != 0 ||
                     document.World.Fluid.SparseSets[3].Count != 0) &&
                    HasDivergentFluidBuffers(document.World) &&
                    HasNonzeroFluidFlow(document.World))
                {
                    return document;
                }
            }
            throw new AssertionException(
                "The save corpus has no fluid fixture with native buffers, flow words, and active work sets.");
        }

        private sealed class RuntimeWorldHarness : IDisposable
        {
            private readonly GameObject _terrainObject;

            internal RuntimeWorldHarness(OriginalSaveRuntimeSnapshot baseline)
            {
                Context = BuildIdentityStableRuntime(baseline);
                Context.LightBeginRow = baseline.RuntimeWorld.LightBegin;
                BdbPmpWorldBuilder.TclWorldResult world =
                    baseline.RuntimeWorld.CreateWorldResult();
                _terrainObject = new GameObject("OriginalSaveFluidContinuationTerrain");
                DiggableTerrain terrain = _terrainObject.AddComponent<DiggableTerrain>();
                terrain.Init(world, null!, string.Empty, () => { });
                terrain.RestoreSaveState(
                    baseline.RuntimeWorld.CreateTerrainState(world.Voxels));
                Context.Terrain = terrain;

                var fog = new FogOfWarState(
                    baseline.RuntimeWorld.Width,
                    baseline.RuntimeWorld.Height,
                    (_, _) => 0);
                fog.RestoreSaveState(baseline.RuntimeWorld.CreateFogState());
                Context.FogOfWar = fog;
                Context.Fluids.Restore(baseline.RuntimeWorld.CreateFluidState());
            }

            internal DigglesTclContext Context { get; }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_terrainObject);
            }

            private static DigglesTclContext BuildIdentityStableRuntime(
                OriginalSaveRuntimeSnapshot baseline)
            {
                var context = new DigglesTclContext(new TclInterpreter());
                var classes = new Dictionary<string, DigglesClass>(StringComparer.Ordinal);
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
                        // Identity fields the real session stager restores; a minimal identity
                        // builder must too, or the no-op diff invents patches.
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
                _ = new DigglesGameSim(context) { GameTime = baseline.GameTime };
                return context;
            }
        }
    }
}
