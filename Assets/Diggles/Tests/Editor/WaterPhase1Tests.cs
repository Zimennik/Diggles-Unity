#nullable enable

namespace Diggles.Tests
{
    using System.Collections;
    using System.IO;
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class WaterPhase1Tests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        private GameObject? _terrainObject;

        [TearDown]
        public void TearDown()
        {
            if (_terrainObject != null) UnityEngine.Object.DestroyImmediate(_terrainObject);
        }

        [Test]
        public void RealTemplate_InitialWaterOccupiesAuthoredSourceCell()
        {
            string waterClass = Path.Combine(DataRoot, "Scripts", "classes", "items", "wasser.tcl");
            string template = Path.Combine(DataRoot, "Templates", "urw_gng_050_a.tcl");
            string layer2 = Path.Combine(DataRoot, "Templates", "urw_gng_050_a.l2m");
            Assume.That(File.Exists(waterClass), $"original water class is missing at {waterClass}");
            Assume.That(File.Exists(template), $"original template is missing at {template}");
            Assume.That(File.Exists(layer2), $"original layer-2 map is missing at {layer2}");

            L2mMap l2m = L2mParser.Parse(layer2);
            ushort sourceTerrain = l2m.CellAt(12, 11);
            Assert.That(sourceTerrain, Is.EqualTo(0x5400));
            Assert.That(L2mMap.DecodeCorners(sourceTerrain), Is.EqualTo((1, 1, 1, 0)));
            Assert.That(L2mMap.BlockType(sourceTerrain), Is.Zero);
            Assert.That(L2mMap.AnimFrame(sourceTerrain), Is.Zero);

            var (interpreter, context, _) = Host(64, 64);
            context.DataRoot = DataRoot;
            context.TolerateMissingFiles = true;
            context.TolerateMissingClass = true;
            context.TolerateMissingEntities = true;
            var mapBuilder = new RecordingMapBuilder();
            context.MapBuilder = mapBuilder;
            interpreter.TolerateExprErrors = true;
            interpreter.TolerateMissingVars = true;
            interpreter.UnknownCommandHandler = (_, _) => "0";
            interpreter.RegisterCommand("puts", (_, _) => "");
            var sim = new DigglesGameSim(context);

            interpreter.Eval("call scripts/classes/items/wasser.tcl");
            interpreter.Eval("call templates/urw_gng_050_a.tcl");
            interpreter.Eval("MapTemplateSet 20 20");
            sim.Step(1.0);

            Assert.That(mapBuilder.Pmp, Is.EqualTo(("data/templates/gng_050.pmp", 20, 20)));
            Assert.That(
                mapBuilder.L2m,
                Is.EqualTo(("data/templates/urw_gng_050_a.l2m", 20, 20)));
            FluidSystem.CellSnapshot[] cells = context.Fluids.CaptureCells();
            Assert.That(cells, Has.Length.EqualTo(1));
            Assert.That(cells[0].X, Is.EqualTo(32));
            Assert.That(cells[0].Y, Is.EqualTo(31));
            Assert.That(cells[0].Fill, Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(cells[0].TypeMask, Is.EqualTo((int)FluidSystem.FluidKind.Water));
        }

        [Test]
        public void SourceCell_UsesNativeTruncationAfterTemplateOffset()
        {
            Assert.That(
                FluidSystem.ResolveSourceCell(
                    new Vector3(32.64f, 32.375f, 13.01f),
                    new Vector3(0f, -1f, 0f)),
                Is.EqualTo(new Vector2Int(32, 31)));
            Assert.That(
                FluidSystem.ResolveSourceCell(
                    new Vector3(-0.25f, -0.25f, 0f),
                    Vector3.zero),
                Is.EqualTo(Vector2Int.zero));
        }

        [Test]
        public void Presenter_MapsFillToNativeFrontWallAndDepthStrips()
        {
            var (_, context, terrain) = Host(1, 1, capacity: 302, lower: 2, upper: 6, variant: 0);
            var presenter = terrain.gameObject.AddComponent<FluidPresenter>();
            presenter.Init(context.Fluids, terrain, DataRoot);
            context.Fluids.Restore(State(new FluidSystem.CellSaveState
            {
                X = 0,
                Y = 0,
                Fill = 0.5f,
                TypeMask = (int)FluidSystem.FluidKind.Water,
            }));

            presenter.PresentIfDirty();

            Mesh mesh = presenter.Mesh!;
            Vector3[] vertices = mesh.vertices;
            Assert.That(vertices, Has.Length.EqualTo(22));
            Assert.That(vertices[0].y, Is.EqualTo(14.2f).Within(0.0001f));
            Assert.That(vertices[0].z, Is.EqualTo(-0.5f).Within(0.0001f));
            Assert.That(vertices[2].z, Is.EqualTo(-0.75f).Within(0.0001f));
            Assert.That(vertices[2].y, Is.EqualTo(vertices[0].y).Within(0.0001f),
                "the front volume wall stays at native render depth 14.4");
            Assert.That(vertices[4].z, Is.EqualTo(vertices[20].z).Within(0.0001f),
                "the exposed top stays at the simulated fill surface");
            Assert.That(vertices[21].y, Is.EqualTo(10.2f).Within(0.0001f),
                "raw render depth 6.4 uses LocalFromRender: localY = depth/2 + 7");
            Assert.That(mesh.GetTriangles(0), Has.Length.EqualTo(54));
            Assert.That(mesh.colors[0].a, Is.EqualTo(64f / 255f).Within(0.0001f));

            Material[] materials = terrain.GetComponent<MeshRenderer>().sharedMaterials;
            Assert.That(materials[0].mainTexture!.name, Is.EqualTo("wasser_env"));
            Assert.That(materials[1].mainTexture!.name, Is.EqualTo("schwef_nahtlos"));
            Assert.That(materials[2].mainTexture!.name, Is.EqualTo("lava_nahtlos"));
            Assert.That(materials[0].GetTexture("_AlphaTex")!.name, Is.EqualTo("alpha"));
        }

        [Test]
        public void Presenter_CellVolumeUsesEighthCellBoundsAndClampsVisibleOverfill()
        {
            FluidPresenter.CellVolume empty = FluidPresenter.CalculateCellVolume(10, 0f, 2, 6);
            FluidPresenter.CellVolume half = FluidPresenter.CalculateCellVolume(10, 0.5f, 2, 6);
            FluidPresenter.CellVolume full = FluidPresenter.CalculateCellVolume(10, 1f, 2, 6);
            FluidPresenter.CellVolume pressurized = FluidPresenter.CalculateCellVolume(10, 3f, 2, 6);

            Assert.That(empty.BottomY, Is.EqualTo(10.75f).Within(0.0001f));
            Assert.That(empty.SurfaceY, Is.EqualTo(10.75f).Within(0.0001f));
            Assert.That(half.SurfaceY, Is.EqualTo(10.5f).Within(0.0001f));
            Assert.That(full.SurfaceY, Is.EqualTo(10.25f).Within(0.0001f));
            Assert.That(pressurized.SurfaceY, Is.EqualTo(full.SurfaceY).Within(0.0001f));
        }

        [Test]
        public void Presenter_UsesNativeMaterialOpacityLaw()
        {
            Assert.That(
                FluidPresenter.CalculateNativeOpacity(0.5f, (int)FluidSystem.FluidKind.Water),
                Is.EqualTo(64f / 255f).Within(0.0001f));
            Assert.That(
                FluidPresenter.CalculateNativeOpacity(3f, (int)FluidSystem.FluidKind.Water),
                Is.EqualTo(128f / 255f).Within(0.0001f));
            Assert.That(
                FluidPresenter.CalculateNativeOpacity(3f, (int)FluidSystem.FluidKind.Sulfur),
                Is.EqualTo(64f / 255f).Within(0.0001f));
        }

        [Test]
        public void Presenter_FogGateHidesBlackAndUsesTheTerrainGrayVeilLaw()
        {
            Assert.That(FluidPresenter.NativeFogCellVisible(0, 0, 0, 0), Is.False);
            Assert.That(FluidPresenter.NativeFogCellVisible(0, 0, 1, 0), Is.True);

            var source = new Color(1f, 0f, 0f, 0.5f);
            Color hidden = FluidPresenter.ApplyNativeFog(source, 1f, 0f, 7f);
            Color veiled = FluidPresenter.ApplyNativeFog(source, 1f, 1f, 7f);
            Color frontVeiled = FluidPresenter.ApplyNativeFog(source, 1f, 1f, 14.5f);
            Assert.That(hidden.r, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(hidden.g, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(hidden.b, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(hidden.a, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(veiled.r, Is.EqualTo(170f / 255f).Within(0.0001f));
            Assert.That(veiled.g, Is.EqualTo(170f / 255f).Within(0.0001f));
            Assert.That(veiled.b, Is.EqualTo(155f / 255f).Within(0.0001f));
            Assert.That(veiled.a, Is.EqualTo(0.5f).Within(0.0001f));
            const float nativeFrontGrayFactor = 0.04375f;
            Assert.That(frontVeiled.r,
                Is.EqualTo(Mathf.Lerp(source.r, FluidPresenter.FogVeilColor.r, nativeFrontGrayFactor))
                    .Within(0.0001f),
                "(1 - (14.5 - 7) * 0.125 * 1.02) pins the native depth bias");
            Assert.That(frontVeiled.a, Is.EqualTo(source.a).Within(0.0001f));

            string shaderPath = Path.Combine(
                Application.dataPath,
                "Diggles",
                "Runtime",
                "Shaders",
                "Resources",
                "DigglesFluid.shader");
            string shaderSource = File.ReadAllText(shaderPath);
            StringAssert.Contains("TEXTURE2D(_FowTex)", shaderSource);
            StringAssert.Contains("finalAlpha *= fow.g", shaderSource);
            StringAssert.Contains("float3(170.0, 170.0, 155.0) / 255.0", shaderSource);
        }

        [Test]
        public void GnomeWaterState_UsesFeetAndMouthThresholdsAndClearsOnExit()
        {
            var (_, context, _) = Host(1, 1);
            context.Fluids.Restore(State(new FluidSystem.CellSaveState
            {
                X = 0,
                Y = 0,
                Fill = 0.5f,
                TypeMask = (int)FluidSystem.FluidKind.Water,
            }));

            GnomeWaterState boundary = GnomeWaterReaction.Evaluate(
                context.Fluids,
                new Vector3(0.5f, 0.6f, 0f),
                new Vector3(0f, -0.1f, 0f));
            GnomeWaterState submerged = GnomeWaterReaction.Evaluate(
                context.Fluids,
                new Vector3(0.5f, 0.6001f, 0f),
                new Vector3(0f, -0.1f, 0f));
            GnomeWaterState shallow = GnomeWaterReaction.Evaluate(
                context.Fluids,
                new Vector3(0.5f, 0.75f, 0f),
                new Vector3(0f, -0.5f, 0f));

            Assert.That(boundary.FeetUnderwater, Is.False);
            Assert.That(boundary.MouthUnderwater, Is.False);
            Assert.That(submerged.FeetUnderwater, Is.True);
            Assert.That(submerged.MouthUnderwater, Is.True);
            Assert.That(shallow.FeetUnderwater, Is.True);
            Assert.That(shallow.MouthUnderwater, Is.False);

            context.Fluids.Restore(State());
            GnomeWaterState dry = GnomeWaterReaction.Evaluate(
                context.Fluids,
                new Vector3(0.5f, 0.75f, 0f),
                Vector3.zero);
            Assert.That(dry.FeetUnderwater, Is.False);
            Assert.That(dry.MouthUnderwater, Is.False);
        }

        [Test]
        public void GnomeWaterState_SynchronizesDrowningFlagAndMovementBankSeam()
        {
            var (_, context, _) = Host(1, 1);
            var gnomeClass = new DigglesClass { Name = "WaterProbeGnome", Kind = "gnome" };
            var action = new WaterWalkProbe();
            var gnome = new DigglesTclEntity
            {
                Class = gnomeClass,
                Position = new Vector3(0.5f, 0.75f, 0f),
                ActiveAction = action,
            };
            context.RegisterEntity(gnome);
            context.Fluids.Restore(State(new FluidSystem.CellSaveState
            {
                X = 0,
                Y = 0,
                Fill = 0.5f,
                TypeMask = (int)FluidSystem.FluidKind.Water,
            }));

            GnomeWaterReaction.Synchronize(context);

            Assert.That(gnome.VarsScope.Vars["is_underwater"], Is.EqualTo("1"));
            Assert.That(action.WaterMovement, Is.True);

            context.Fluids.Restore(State());
            GnomeWaterReaction.Synchronize(context);

            Assert.That(gnome.VarsScope.Vars["is_underwater"], Is.EqualTo("0"));
            Assert.That(action.WaterMovement, Is.False);
        }

        [Test]
        public void GnomeWaterMovement_ConsumesSubWaterFlagOnWalkableSubcell()
        {
            var subGrid = new byte[64];
            subGrid[(4 << 3) + 4] = NavWorld.SubWalk;
            var navigation = new NavWorld(
                1,
                1,
                new uint[1],
                subGrid,
                new BdbModel { Header = new BdbHeader { NBlocks = 1 } },
                deriveImmediately: false);
            navigation.RebuildWaterFlags((x, y) => x == 4 && y == 4);

            Assert.That(
                GnomeWaterReaction.IsMovementWater(
                    navigation,
                    new Vector3(0.5f, 0.5f, 0f),
                    volumeFallback: false),
                Is.True);

            navigation.RebuildWaterFlags((_, _) => false);
            Assert.That(
                GnomeWaterReaction.IsMovementWater(
                    navigation,
                    new Vector3(0.5f, 0.5f, 0f),
                    volumeFallback: true),
                Is.False);
        }

        [Test]
        public void RestoredWalk_EntersAndLeavesAuthoredWaterAnimationBank()
        {
            var (_, context, terrain) = Host(1, 1);
            var owner = new DigglesTclEntity
            {
                Class = new DigglesClass { Name = "WaterAnimationGnome", Kind = "gnome" },
            };
            var ownerObject = new GameObject("WaterAnimationGnome");
            ownerObject.transform.SetParent(terrain.transform, false);
            owner.GameObject = ownerObject;
            var controller = ownerObject.AddComponent<Diggles.Runtime.DigglesAnimController>();
            var state = new WalkActionSaveState(
                Vector3.zero,
                true,
                new[] { 0, 1, 3, 0 },
                new int[4 * 24 * 5],
                1,
                1,
                0,
                2f,
                1,
                1,
                0,
                0,
                new byte[] { 1, 0, 0, 0 },
                -1,
                -1f,
                -1f,
                Vector3.zero,
                0,
                0f,
                -1,
                0,
                0,
                string.Empty,
                "walk_collision_callback",
                "obj_use_callback");
            var action = new RestoredWalkAction(
                context,
                owner,
                state,
                new AnimationBankChangingWalk(controller, 1));

            action.SetWaterMovement(true);
            Assert.That(controller.ActiveAnimset, Is.EqualTo(3));

            action.Tick(0.025);
            Assert.That(controller.ActiveAnimset, Is.EqualTo(3));

            action.SetWaterMovement(false);
            Assert.That(controller.ActiveAnimset, Is.EqualTo(1));
        }

        private (TclInterpreter Interpreter, DigglesTclContext Context, DiggableTerrain Terrain) Host(
            int width,
            int height,
            int capacity = 128,
            int lower = 0,
            int upper = 8,
            byte variant = 0)
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            var aux = new byte[BdbModel.AuxStride];
            aux[BdbModel.AuxOffVariant] = variant;
            uint descriptor = (uint)(capacity | (upper << 10) | (lower << 14)) |
                (120u << 18) | (120u << 25);
            for (int index = 0; index < 4; index++)
                aux[BdbModel.AuxOffFluid + index] = (byte)(descriptor >> (index * 8));
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = 1 },
                PerBlockAux = aux,
            };
            int cellCount = width * height;
            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = width,
                Height = height,
                BlockIdx = new uint[cellCount],
                Terrain = new byte[cellCount],
                Zone = new byte[cellCount],
                CellId = new ushort[cellCount],
                HasTpl = new BitArray(cellCount),
            };
            _terrainObject = new GameObject("WaterPhase1TestsTerrain");
            var terrain = _terrainObject.AddComponent<DiggableTerrain>();
            terrain.Init(result, bdb, string.Empty, () => { });
            context.Terrain = terrain;
            return (interpreter, context, terrain);
        }

        private static FluidSystem.SaveState State(params FluidSystem.CellSaveState[] cells) => new()
        {
            Enabled = true,
            Cells = cells,
        };

        private sealed class WaterWalkProbe : IEntityAction, IWaterReactiveWalk
        {
            public bool WaterMovement { get; private set; }

            public bool Tick(double dt) => true;

            public void SetWaterMovement(bool active) => WaterMovement = active;
        }

        private sealed class AnimationBankChangingWalk : IEntityAction
        {
            private readonly Diggles.Runtime.DigglesAnimController _controller;
            private readonly int _animationBank;

            public AnimationBankChangingWalk(
                Diggles.Runtime.DigglesAnimController controller,
                int animationBank)
            {
                _controller = controller;
                _animationBank = animationBank;
            }

            public bool Tick(double dt)
            {
                _controller.SetActiveAnimset(_animationBank);
                return true;
            }
        }

        private sealed class RecordingMapBuilder : IDigglesMapBuilder
        {
            public (string Path, int X, int Y)? Pmp { get; private set; }
            public (string Path, int X, int Y)? L2m { get; private set; }

            public void CreateMap(int width, int height) { }

            public void LoadPmp(string path, int x, int y) => Pmp = (path, x, y);

            public void LoadL2m(string path, int x, int y) => L2m = (path, x, y);
        }
    }
}
