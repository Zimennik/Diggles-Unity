#nullable enable

namespace Diggles.Tests
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class DigEdgeBfsTests
    {
        [Test]
        public void GetDigEdge_FloodsMarkedColumn_AndRanksOpenExploredFrontierNearestGnome()
        {
            const int width = 20;
            const int height = 20;
            const int seedX = 10;
            const int seedY = 4;
            const int gnomeX = 10;
            const int gnomeY = 12;
            var root = new GameObject(
                nameof(GetDigEdge_FloodsMarkedColumn_AndRanksOpenExploredFrontierNearestGnome));
            try
            {
                DiggableTerrain terrain = BuildTerrain(root, width, height);
                for (int tileY = seedY; tileY < gnomeY; tileY++)
                    terrain.SetDigMark(seedX, tileY, 0, dummy: true);
                SetOpenTile(terrain, seedX + 1, seedY);
                SetOpenTile(terrain, gnomeX, gnomeY);
                FogOfWarState fog = BuildFog(
                    width,
                    height,
                    0,
                    (seedX + 1, seedY),
                    (gnomeX, gnomeY));

                Assert.That(terrain.TryFindDigEdge(
                        seedX,
                        seedY,
                        0,
                        gnomeX,
                        gnomeY,
                        out int edgeTileX,
                        out int edgeTileY,
                        fog),
                    Is.True);
                Assert.That((edgeTileX, edgeTileY), Is.EqualTo((gnomeX, gnomeY)),
                    "0x5983e1-0x598408 ranks tile distance to the gnome, not distance to the marker");
                Assert.That(terrain.Nav!.BaseCost[gnomeY * width + gnomeX], Is.EqualTo(-1),
                    "get_digedge candidate selection must not consult the navigation plane");
                for (int tileY = seedY; tileY < gnomeY; tileY++)
                    Assert.That(terrain.RawDigNibble(seedX, tileY, 0), Is.EqualTo(3),
                        "scratch bit2 must be cleared without changing the authored dig nibble");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GetDigEdge_StopsAtUnmarkedFrontier_AndRequiresExploredCandidate()
        {
            const int width = 16;
            const int height = 16;
            const int seedX = 8;
            const int seedY = 8;
            var root = new GameObject(
                nameof(GetDigEdge_StopsAtUnmarkedFrontier_AndRequiresExploredCandidate));
            try
            {
                DiggableTerrain terrain = BuildTerrain(root, width, height);
                terrain.SetDigMark(seedX, seedY, 0, dummy: true);
                SetOpenTile(terrain, seedX + 1, seedY);
                SetOpenTile(terrain, seedX + 2, seedY);
                FogOfWarState onlyDistantExplored = BuildFog(
                    width,
                    height,
                    0,
                    (seedX + 2, seedY));

                Assert.That(terrain.TryFindDigEdge(
                        seedX,
                        seedY,
                        0,
                        seedX + 2,
                        seedY,
                        out int edgeX,
                        out int edgeY,
                        onlyDistantExplored),
                    Is.False);
                Assert.That((edgeX, edgeY), Is.EqualTo((-1, -1)),
                    "the unmarked frontier tile cannot spread the flood to a farther open tile");

                FogOfWarState frontierExplored = BuildFog(
                    width,
                    height,
                    0,
                    (seedX + 1, seedY),
                    (seedX + 2, seedY));
                Assert.That(terrain.TryFindDigEdge(
                        seedX,
                        seedY,
                        0,
                        seedX + 2,
                        seedY,
                        out edgeX,
                        out edgeY,
                        frontierExplored),
                    Is.True);
                Assert.That((edgeX, edgeY), Is.EqualTo((seedX + 1, seedY)),
                    "an explored open frontier tile is a candidate but remains a flood terminus");
                Assert.That(terrain.RawDigNibble(seedX, seedY, 0), Is.EqualTo(3));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GetDigEdge_StrictDistanceTieKeepsEastFirstBfsCandidate()
        {
            const int width = 16;
            const int height = 16;
            const int seedX = 8;
            const int seedY = 8;
            var root = new GameObject(nameof(GetDigEdge_StrictDistanceTieKeepsEastFirstBfsCandidate));
            try
            {
                DiggableTerrain terrain = BuildTerrain(root, width, height);
                terrain.SetDigMark(seedX, seedY, 0, dummy: true);
                SetOpenTile(terrain, seedX + 1, seedY);
                SetOpenTile(terrain, seedX - 1, seedY);
                FogOfWarState fog = BuildFog(
                    width,
                    height,
                    0,
                    (seedX + 1, seedY),
                    (seedX - 1, seedY));

                Assert.That(terrain.TryFindDigEdge(
                        seedX,
                        seedY,
                        0,
                        seedX,
                        seedY,
                        out int edgeX,
                        out int edgeY,
                        fog),
                    Is.True);
                Assert.That((edgeX, edgeY), Is.EqualTo((seedX + 1, seedY)),
                    "native neighbour order is east, west, down, up and equal distance never replaces best");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GetDigEdge_FailureAndInsetUseExactSentinel()
        {
            const int width = 16;
            const int height = 16;
            var root = new GameObject(nameof(GetDigEdge_FailureAndInsetUseExactSentinel));
            try
            {
                DiggableTerrain terrain = BuildTerrain(root, width, height);
                Assert.That(terrain.TryFindDigEdge(8, 8, 0, 8, 8, out int tileX, out int tileY),
                    Is.False);
                Assert.That((tileX, tileY), Is.EqualTo((-1, -1)));

                var interpreter = new TclInterpreter();
                DigglesTclContext context = DigglesTclEngine.Register(interpreter);
                context.Terrain = terrain;
                Assert.That(interpreter.Eval("get_digedge {0 8} 0").Trim(), Is.EqualTo("-1 -1 14"),
                    "world-edge rejection keeps the wrapper's hard-coded Z=14 sentinel");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DigWalkOption_SkipsRelaxedGoalFloorSettle()
        {
            MethodInfo? parseOption = typeof(DigglesTclEngine).GetMethod(
                "NativeWalkOptionEnabled",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(parseOption, Is.Not.Null);
            Assert.That(parseOption!.Invoke(
                    null,
                    new object[] { "-digpos {11 10 14} -dig 1 -canclimb 1", "-dig" }),
                Is.EqualTo(true), "-dig is stored independently of native no-op -digpos");

            const int width = 16;
            const int height = 16;
            var subGrid = new byte[width * height * PmpMap.SubGridLength];
            SetWorldSub(subGrid, width, 60, 51, NavWorld.SubWalk);
            SetWorldSub(subGrid, width, 60, 52, NavWorld.SubWalk);
            SetWorldSub(subGrid, width, 60, 53, NavWorld.SubWalk | NavWorld.SubFloor);
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                subGrid,
                new BdbModel(),
                deriveImmediately: false);
            MethodInfo? settleGoal = typeof(NavWorldService).GetMethod(
                "ApplyRelaxedGoalFloorSettle",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(settleGoal, Is.Not.Null);
            var regularWalk = new List<(int sx, int sy)> { (60, 51) };
            var digWalk = new List<(int sx, int sy)> { (60, 51) };
            var nativeTarget = new Vector3(60 * 0.125f, 50 * 0.125f, 14f);

            settleGoal!.Invoke(null, new object[] { nav, regularWalk, nativeTarget, false });
            settleGoal.Invoke(null, new object[] { nav, digWalk, nativeTarget, true });

            Assert.That(regularWalk[0], Is.EqualTo((60, 53)),
                "ordinary walks retain the port's relaxed-wall-to-floor settle");
            Assert.That(digWalk[0], Is.EqualTo((60, 51)),
                "-dig 1 preserves the unsnapped wall standoff (FUN_00420840:1901-1919)");
        }

        static DiggableTerrain BuildTerrain(GameObject root, int width, int height)
        {
            int tileCount = width * height;
            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = width,
                Height = height,
                MinX = 0,
                MinY = 0,
                MaxXExclusive = width,
                MaxYExclusive = height,
                BlockIdx = new uint[tileCount],
                Zone = new byte[tileCount],
                Terrain = new byte[tileCount],
                CellId = new ushort[tileCount],
                NativeTerrainFlags = new ushort[tileCount],
                HasTpl = new BitArray(tileCount),
                Voxels = new byte[tileCount * PmpMap.SubGridLength],
            };
            for (int index = 0; index < tileCount; index++)
            {
                result.Terrain[index] = 2;
                result.NativeTerrainFlags[index] = 2;
            }
            var aux = new byte[2 * BdbModel.AuxStride];
            for (int sub = 0; sub < PmpMap.SubGridLength; sub++)
            {
                aux[sub] = 15;
                aux[BdbModel.AuxStride + sub] = 11;
            }
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = 2 },
                PerBlockAux = aux,
            };
            var terrain = root.AddComponent<DiggableTerrain>();
            terrain.Init(result, bdb, string.Empty, () => { });
            return terrain;
        }

        static void SetOpenTile(DiggableTerrain terrain, int tileX, int tileY) =>
            terrain.BlockIdx[tileY * terrain.Width + tileX] = 1;

        static FogOfWarState BuildFog(
            int width,
            int height,
            int owner,
            params (int x, int y)[] explored)
        {
            var fog = new FogOfWarState(width, height, (_, _) => 0);
            FogOfWarState.SaveState state = fog.CaptureSaveState();
            foreach (var (x, y) in explored)
                state.Fog[(y * width + x) * FogOfWarState.Players + owner] = FogOfWarState.GrayFloor;
            fog.RestoreSaveState(state);
            return fog;
        }

        static void SetWorldSub(byte[] subGrid, int width, int subX, int subY, byte value)
        {
            int tile = (subY >> 3) * width + (subX >> 3);
            int local = ((subY & 7) << 3) + (subX & 7);
            subGrid[tile * PmpMap.SubGridLength + local] = value;
        }
    }
}
