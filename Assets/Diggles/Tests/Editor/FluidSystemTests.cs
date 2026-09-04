#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class FluidSystemTests
    {
        GameObject? _terrainObject;

        [TearDown]
        public void TearDown()
        {
            if (_terrainObject != null) UnityEngine.Object.DestroyImmediate(_terrainObject);
        }

        (TclInterpreter Interp, DigglesTclContext Context, DiggableTerrain Terrain) Host(
            int width, int height, int capacity = 128, int lower = 0, int upper = 8)
        {
            var interp = new TclInterpreter();
            var context = DigglesTclEngine.Register(interp);
            var aux = new byte[BdbModel.AuxStride];
            uint descriptor = (uint)(capacity | (upper << 10) | (lower << 14)) |
                (120u << 18) | (120u << 25);
            for (int i = 0; i < 4; i++) aux[BdbModel.AuxOffFluid + i] = (byte)(descriptor >> (i * 8));
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = 1 },
                PerBlockAux = aux,
            };
            int count = width * height;
            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = width,
                Height = height,
                BlockIdx = new uint[count],
                Terrain = new byte[count],
                Zone = new byte[count],
                CellId = new ushort[count],
                HasTpl = new BitArray(count),
            };
            _terrainObject = new GameObject("FluidSystemTestsTerrain");
            var terrain = _terrainObject.AddComponent<DiggableTerrain>();
            terrain.Init(result, bdb, "", () => { });
            context.Terrain = terrain;
            return (interp, context, terrain);
        }

        static DigglesTclEntity Spawn(DigglesTclContext context, string name, Vector3 position)
        {
            var cls = new DigglesClass { Name = name, Kind = "dummy" };
            context.Classes[name] = cls;
            var entity = new DigglesTclEntity { Class = cls, Position = position };
            context.RegisterEntity(entity);
            return entity;
        }

        static FluidSystem.SaveState State(params FluidSystem.CellSaveState[] cells) => new()
        {
            Enabled = true,
            Cells = cells,
        };

        [Test]
        public void BdbFluidDescriptor_ExposesCapacityAndBoundaries()
        {
            var (_, _, terrain) = Host(1, 1, 302, 2, 6);
            Assert.That(terrain.TryGetFluidShape(0, 0, out int capacity, out int lower, out int upper), Is.True);
            Assert.That(capacity, Is.EqualTo(302));
            Assert.That(lower, Is.EqualTo(2));
            Assert.That(upper, Is.EqualTo(6));
        }

        [Test]
        public void NativeFlowLaw_TablePinsWallDropLevelingAndMediumCoefficients()
        {
            Assert.That(FluidSystem.NativeWaterRelaxation, Is.EqualTo(1.5f));
            Assert.That(FluidSystem.NativeSulfurRelaxation, Is.EqualTo(1.4f));
            Assert.That(FluidSystem.NativeLavaRelaxation, Is.EqualTo(1.3f));
            Assert.That(FluidSystem.NativeWaterMomentum, Is.EqualTo(0.5f));
            Assert.That(FluidSystem.NativeSulfurMomentum, Is.EqualTo(0.3f));
            Assert.That(FluidSystem.NativeLavaMomentum, Is.EqualTo(0.125f));
            Assert.That(FluidSystem.NativeFillCutoff, Is.EqualTo(0.001f));
            Assert.That(FluidSystem.NativeActivityCutoff, Is.EqualTo(0.004f));
            Assert.That(FluidSystem.NativeSourceWakeMomentum, Is.EqualTo(0.05f));
            Assert.That(FluidSystem.NativeFluidMinimumCapacity, Is.EqualTo(32));
            Assert.That(FluidSystem.NativeLavaMinimumCapacity, Is.EqualTo(128));
            Assert.That(FluidSystem.NativeFluidMinimumAperture, Is.EqualTo(8));
            Assert.That(FluidSystem.NativeLavaMinimumAperture, Is.EqualTo(16));
            Assert.That(FluidSystem.NativeStopperQuantization, Is.EqualTo(15));

            float wall = FluidSystem.CalculateNativeTransfer(
                FluidSystem.FluidKind.Water, 0f, 16f, 0, 1f, 1000f, 1000f, 0f);
            float drop = FluidSystem.CalculateNativeTransfer(
                FluidSystem.FluidKind.Water, 0f, 16f, 120, 1f, 1000f, 1000f, 0f);
            float waterLevel = FluidSystem.CalculateNativeTransfer(
                FluidSystem.FluidKind.Water, 0f, 8f, 120, 1f, 1000f, 1000f, 0f);
            float sulfurLevel = FluidSystem.CalculateNativeTransfer(
                FluidSystem.FluidKind.Sulfur, 0f, 8f, 120, 1f, 1000f, 1000f, 0f);
            float lavaLevel = FluidSystem.CalculateNativeTransfer(
                FluidSystem.FluidKind.Lava, 0f, 8f, 120, 1f, 1000f, 1000f, 0f);
            float lavaWaterLevel = FluidSystem.CalculateNativeTransfer(
                FluidSystem.FluidKind.LavaWater, 0f, 8f, 120, 1f, 1000f, 1000f, 0f);
            float minimumAperture = FluidSystem.CalculateNativeTransfer(
                FluidSystem.FluidKind.Water, 0f, 8f, 1, 1f, 1000f, 1000f, 0f);

            Assert.That(wall, Is.Zero, "directional BDB aperture 0 is an absolute wall");
            Assert.That(drop, Is.EqualTo(202.5f).Within(0.0001f),
                "pressure(16)=72; 72 * 0.125^2 * 120 * 1.5");
            Assert.That(waterLevel, Is.EqualTo(22.5f).Within(0.0001f));
            Assert.That(sulfurLevel, Is.EqualTo(21f).Within(0.0001f));
            Assert.That(lavaLevel, Is.EqualTo(19.5f).Within(0.0001f));
            Assert.That(lavaWaterLevel, Is.EqualTo(22.5f).Within(0.0001f),
                "lava-water dispatches through the native water kernels");
            Assert.That(minimumAperture, Is.EqualTo(1.5f).Within(0.0001f),
                "a nonzero water aperture is floored to 8");
        }

        [Test]
        public void Presenter_RequiresItsRenderComponentsOnWorldRoot()
        {
            var (_, context, terrain) = Host(1, 1);
            var presenter = terrain.gameObject.AddComponent<FluidPresenter>();
            presenter.Init(context.Fluids, terrain, Path.GetTempPath());

            Assert.That(terrain.TryGetComponent(out MeshFilter _), Is.True);
            Assert.That(terrain.TryGetComponent(out MeshRenderer _), Is.True);
        }

        [Test]
        public void SourceCommands_MergeFieldsAndConsumeFiniteVolume()
        {
            var (interp, context, _) = Host(1, 1);
            var source = Spawn(context, "FluidSourceProbe", Vector3.zero);
            interp.Eval("set_water 1");
            interp.Eval($"set_fsource {source.Handle} -pos {{0 0 0}} -type water -vpf 0.5 -volume 0.5");
            interp.Eval($"set_fsource {source.Handle} -ventil 1");

            context.Fluids.Tick();

            Assert.That(context.Fluids.GetFill(0, 0), Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(interp.Eval($"get_fsource {source.Handle} -volume"), Is.EqualTo("0"));
            Assert.That(interp.Eval($"get_fsource {source.Handle} -vpf"), Is.EqualTo("0.5"));
            Assert.That(interp.Eval($"get_fsource {source.Handle} -ventil"), Is.EqualTo("1"));

            interp.Eval($"set_fsource {source.Handle} -vpf -0.25 -volume 0.25 -ventil 0");
            context.Fluids.Tick();
            Assert.That(context.Fluids.GetFill(0, 0), Is.EqualTo(0.25f).Within(0.0001f),
                "negative VPF must drain a fluid cell");
            Assert.That(interp.Eval($"get_fsource {source.Handle} -volume"), Is.EqualTo("0"));

            interp.Eval($"set_fsource {source.Handle} -height 1.5 -vpf -10000 -volume -10000");
            context.Fluids.Tick();
            Assert.That(context.Fluids.GetFill(0, 0), Is.EqualTo(0.25f).Within(0.0001f),
                "the native no-update sentinel must not erase an exhausted finite budget");

            var unlimited = Spawn(context, "UnlimitedFluidSourceProbe", Vector3.zero);
            interp.Eval($"set_fsource {unlimited.Handle} -height 1.5 -vpf -10000 -volume -10000");
            context.Fluids.Tick();
            Assert.That(context.Fluids.GetFill(0, 0), Is.EqualTo(1.5f).Within(0.0001f),
                "a new source with omitted volume must remain unlimited");
            Assert.That(interp.Eval($"get_fsource {unlimited.Handle} -height"), Is.EqualTo("1.5"));
            Assert.That(interp.Eval($"get_fsource {unlimited.Handle} -vpf"), Is.EqualTo("0"));

            interp.Eval($"set_fsource {unlimited.Handle} -vpf -9999");
            Assert.That(interp.Eval($"get_fsource {unlimited.Handle} -vpf"), Is.EqualTo("-9999"),
                "negative non-sentinel VPF is a native drain, not an absent option");
        }

        [Test]
        public void VentedSource_PinsTheFluidSurfaceToItsAuthoredY()
        {
            var (interp, context, _) = Host(1, 1);
            var source = Spawn(context, "VentedFluidSourceProbe", new Vector3(0f, 0.25f, 0f));
            interp.Eval("set_water 1");
            interp.Eval($"set_fsource {source.Handle} -type water -vpf 0.25 -ventil 1");

            context.Fluids.Tick();

            Assert.That(context.Fluids.GetFill(0, 0), Is.EqualTo(0.75f).Within(0.0001f),
                "open-cell surface y=1-fill must be pinned to source y=0.25");
            Assert.That(context.Fluids.GetWaterHeight(new Vector3(0f, 0.25f, 0f)).y,
                Is.EqualTo(0.25f).Within(0.0001f));
        }

        [Test]
        public void Flow_FallsAndEqualizesUnlessStopperBlocksBoundary()
        {
            var (interp, context, _) = Host(1, 2);
            context.Fluids.Restore(State(new FluidSystem.CellSaveState
                { X = 0, Y = 0, Fill = 1f, TypeMask = (int)FluidSystem.FluidKind.Water }));
            context.Fluids.Tick();
            Assert.That(context.Fluids.GetFill(0, 1), Is.GreaterThan(0f), "water must fall to the next map row");

            context.Fluids.Restore(State(new FluidSystem.CellSaveState
                { X = 0, Y = 0, Fill = 1f, TypeMask = (int)FluidSystem.FluidKind.Water }));
            var stopper = Spawn(context, "FluidStopperProbe", Vector3.zero);
            interp.Eval($"set_fstopper {stopper.Handle} {{-1 1}} {{1 1}} 0");
            context.Fluids.Tick();
            Assert.That(context.Fluids.GetFill(0, 1), Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void Flow_ZeroVerticalApertureBlocksACapacityBearingWall()
        {
            var (_, context, terrain) = Host(1, 2, capacity: 960);
            uint descriptor = 960u | (8u << 10) | (120u << 25);
            for (int index = 0; index < 4; index++)
                terrain.Bdb!.PerBlockAux[BdbModel.AuxOffFluid + index] =
                    (byte)(descriptor >> (index * 8));
            context.Fluids.Restore(State(new FluidSystem.CellSaveState
                { X = 0, Y = 0, Fill = 1f, TypeMask = (int)FluidSystem.FluidKind.Water }));

            context.Fluids.Tick();

            Assert.That(context.Fluids.GetFill(0, 0), Is.EqualTo(1f).Within(0.0001f));
            Assert.That(context.Fluids.GetFill(0, 1), Is.Zero,
                "capacity without the vertical aperture is native SOLID, not a fluid destination");
        }

        [Test]
        public void Queries_UseNativeSurfaceFormulaAndFluidType()
        {
            var (interp, context, _) = Host(3, 1);
            context.Fluids.Restore(State(new FluidSystem.CellSaveState
                { X = 1, Y = 0, Fill = 0.5f, TypeMask = (int)FluidSystem.FluidKind.Sulfur }));
            Assert.That(context.Fluids.IsUnderwater(new Vector3(1.5f, 0.75f, 9f)), Is.True);
            Assert.That(context.Fluids.IsUnderwater(new Vector3(1.5f, 0.25f, 9f)), Is.False);
            Assert.That(context.Fluids.GetWaterHeight(new Vector3(1.5f, 0.75f, 9f)).y,
                Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(interp.Eval("get_ftype 1 0"), Is.EqualTo("2"));

            var self = Spawn(context, "FluidQuerySelf", new Vector3(0.5f, 0.25f, 9f));
            var wet = Spawn(context, "FluidQueryWet", new Vector3(1.5f, 0.75f, 9f));
            Assert.That(interp.Eval($"obj_query {self.Handle} -class FluidQueryWet -water 1"),
                Is.EqualTo(wet.Handle.ToString()));
            Assert.That(interp.Eval($"obj_query {self.Handle} -class FluidQueryWet -water 0"), Is.EqualTo("0"));
        }

        [Test]
        public void Restore_RebuildsNavigationWaterFlagsFromFluidDepth()
        {
            var (_, context, terrain) = Host(1, 1);
            var subGrid = new byte[PmpMap.SubGridLength];
            for (int index = 0; index < subGrid.Length; index++)
                subGrid[index] = NavWorld.SubWalk | NavWorld.SubFloor;
            terrain.Nav = new NavWorld(
                1,
                1,
                new uint[] { 0 },
                subGrid,
                terrain.Bdb!,
                deriveImmediately: false);

            context.Fluids.Restore(State(new FluidSystem.CellSaveState
            {
                X = 0,
                Y = 0,
                Fill = 0.5f,
                TypeMask = (int)FluidSystem.FluidKind.Water,
            }));

            Assert.That(terrain.Nav.SubIsWater(4, 3), Is.False);
            Assert.That(terrain.Nav.SubIsWater(4, 4), Is.True);
            Assert.That(terrain.Nav.SubIsWater(4, 7), Is.True);
            Assert.That(terrain.Nav.SubWalkable(4, 4), Is.True);

            context.Fluids.SetEnabled(false);

            Assert.That(terrain.Nav.SubIsWater(4, 4), Is.False);
            Assert.That(terrain.Nav.SubWalkable(4, 4), Is.True);
        }

        [Test]
        public void SparetimeQueries_RejectUnderwaterPlaces()
        {
            var (interp, context, _) = Host(3, 1);
            var asker = Spawn(context, "FluidSpareAsker", new Vector3(0.5f, 0.25f, 9f));
            var place = Spawn(context, "FluidSparePlace", new Vector3(1.5f, 0.75f, 9f));
            place.BuildupComplete = true;
            context.Fluids.Restore(State(new FluidSystem.CellSaveState
            {
                X = 1,
                Y = 0,
                Fill = 0.5f,
                TypeMask = (int)FluidSystem.FluidKind.Water,
            }));
            interp.Eval($"sparetime {place.Handle} announce eat");

            Assert.That(interp.Eval(
                    $"sparetime {asker.Handle} queryrect eat -160 -80 160 80"),
                Is.Empty,
                "FUN_005f4d00 must not offer an underwater eat/sleep/leisure place");
        }

        [Test]
        public void SaveState_RestoresCellsSourcesAndStoppers()
        {
            var (interp, context, _) = Host(2, 1);
            _ = new DigglesGameSim(context);
            var source = Spawn(context, "SavedFluidObject", Vector3.zero);
            interp.Eval("set_water 1");
            interp.Eval($"set_fsource {source.Handle} -type lava -vpf 0 -volume 85");
            interp.Eval($"set_fstopper {source.Handle} {{0 0}} {{1 0}} 0.25");
            context.Fluids.Restore(new FluidSystem.SaveState
            {
                Enabled = true,
                Cells = new[] { new FluidSystem.CellSaveState
                    { X = 1, Y = 0, Fill = 0.75f, TypeMask = (int)FluidSystem.FluidKind.Lava } },
                Sources = context.Fluids.Capture().Sources,
                Stoppers = context.Fluids.Capture().Stoppers,
            });
            FluidSystem.SaveState snapshot = context.Fluids.Capture();

            context.Fluids.Reset();
            context.Fluids.Restore(snapshot);

            Assert.That(context.Fluids.Enabled, Is.True);
            Assert.That(context.Fluids.GetFill(1, 0), Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(context.Fluids.GetFluidType(1, 0), Is.EqualTo((int)FluidSystem.FluidKind.Lava));
            Assert.That(interp.Eval($"get_fsource {source.Handle} -volume"), Is.EqualTo("85"));
            Assert.That(snapshot.Sources[0].ConfiguredVolume, Is.EqualTo(85d));
            Assert.That(snapshot.Sources[0].Volume, Is.EqualTo(85d));
            Assert.That(context.Fluids.Stoppers.ContainsKey(source.Handle), Is.True);
        }
    }
}
