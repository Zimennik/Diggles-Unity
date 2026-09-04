#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveWorldInitializationTests
    {
        [Test]
        public void InitializeFromSaveBuildsNavigationAfterInstallingExactState()
        {
            const int width = 2;
            const int height = 2;
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
            var state = new DiggableTerrain.SaveState
            {
                Width = width,
                Height = height,
                PlayerDigMarkId = 7,
                BlockIdx = new uint[] { 0, 0, 0, 0 },
                Zone = new byte[] { 1, 2, 3, 4 },
                Terrain = new byte[] { 2, 3, 4, 5 },
                CellId = new ushort[] { 11, 12, 13, 14 },
                NativeTerrainFlags = new ushort[] { 2, 1, 0x80, 3 },
                NavigationState = new byte[] { 0x14, 0x32, 0xFF, 0x1E },
                FloorAndExitState = new byte[] { 0x64, 0x24, 0, 0xC2 },
                OriginalBlock = new uint[] { 0, 0, 0, 0 },
                Dug = new bool[] { false, true, false, true },
                DigState = new uint[] { 0, 0xB, 0, 0 },
                DigId = new uint[] { 0, 3, 0, 0 },
                MarkX0 = new int[8],
                MarkY0 = new int[8],
                MarkX1 = new int[] { -1, -1, -1, -1, -1, -1, -1, -1 },
                MarkY1 = new int[] { -1, -1, -1, -1, -1, -1, -1, -1 },
                Voxels = new byte[tileCount * PmpMap.SubGridLength],
                HasTemplate = new bool[] { true, true, false, true },
                RenderXMin = 0,
                RenderYMin = 0,
                RenderXMax = width - 1,
                RenderYMax = height - 1,
            };
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = 1 },
                PerBlockAux = CreateSolidAux(),
            };
            var gameObject = new GameObject("OriginalSaveWorldInitializationTest");
            try
            {
                DiggableTerrain terrain = gameObject.AddComponent<DiggableTerrain>();

                terrain.InitializeFromSave(result, bdb, string.Empty, () => { }, state);

                Assert.That(terrain.PlayerDigMarkId, Is.EqualTo(7));
                Assert.That(terrain.Nav, Is.Not.Null);
                Assert.That(terrain.Nav!.Version, Is.EqualTo(1));
                Assert.That(result.CellId, Is.EqualTo(state.CellId));
                Assert.That(result.NativeTerrainFlags, Is.EqualTo(state.NativeTerrainFlags));
                Assert.That(result.DugFloor, Is.EqualTo(state.Dug));
                Assert.That(terrain.Nav.BaseCost, Is.EqualTo(new sbyte[] { 20, 50, -1, 30 }));
                Assert.That(terrain.Nav.ExitPairAndFlags, Is.EqualTo(state.FloorAndExitState),
                    "tile+0x12 must retain packed X/Y plus flags verbatim");
                Assert.That(terrain.Nav.ExitSubX(0), Is.EqualTo(4));
                Assert.That(terrain.Nav.ExitSubY(0), Is.EqualTo(4));
                Assert.That(terrain.Nav.ExitSubX(3), Is.EqualTo(2));
                Assert.That(terrain.Nav.ExitSubY(3), Is.Zero);
                DiggableTerrain.SaveState captured = terrain.CaptureSaveState();
                Assert.That(captured.NavigationState, Is.EqualTo(state.NavigationState));
                Assert.That(captured.FloorAndExitState, Is.EqualTo(state.FloorAndExitState));

                var mapBuilder = new RuntimeMapBuilder(string.Empty, string.Empty);
                mapBuilder.AttachLiveWorld(result, terrain);
                Assert.That(mapBuilder.LiveMode, Is.True);
                Assert.That(mapBuilder.WorldResult, Is.SameAs(result));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void InitializeFromSaveRejectsIncompleteNativeTerrainFlags()
        {
            var result = new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = 1,
                Height = 1,
                BlockIdx = new uint[1],
                Zone = new byte[1],
                Terrain = new byte[1],
                CellId = new ushort[1],
                NativeTerrainFlags = Array.Empty<ushort>(),
                HasTpl = new BitArray(1),
                Voxels = new byte[PmpMap.SubGridLength],
            };
            var gameObject = new GameObject("OriginalSaveWorldValidationTest");
            try
            {
                DiggableTerrain terrain = gameObject.AddComponent<DiggableTerrain>();
                Assert.Throws<ArgumentException>(() => terrain.InitializeFromSave(
                    result,
                    new BdbModel(),
                    string.Empty,
                    () => { },
                    new DiggableTerrain.SaveState()));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static byte[] CreateSolidAux()
        {
            var result = new byte[89];
            for (int index = 0; index < 64; index++) result[index] = 15;
            return result;
        }
    }
}
