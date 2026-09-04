#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class StampedTemplateNavigationTests
    {
        const int StairWorldX = 5;
        const int StairWorldY = 5;

        [Test]
        public void LiveStamp_BlitsAuthoredRleThenRunsEffBlockWithoutEffPassWalk()
        {
            const int width = 16;
            const int height = 16;
            var root = new GameObject(nameof(LiveStamp_BlitsAuthoredRleThenRunsEffBlockWithoutEffPassWalk));
            try
            {
                BdbModel bdb = BuildDigBdb();
                BdbPmpWorldBuilder.TclWorldResult result = BuildWorld(width, height, defaultBlock: 2);
                var terrain = root.AddComponent<DiggableTerrain>();
                terrain.Init(result, bdb, string.Empty, () => { });

                PmpMap stamp = BuildStamp(width: 6, height: 6, block: 2);
                const int seededTileX = 1;
                const int seededTileY = 1;
                SetStampSub(
                    stamp,
                    seededTileX,
                    seededTileY,
                    4,
                    4,
                    NavWorld.SubWalk | NavWorld.SubFloor);
                // Block 1 has an open body and a solid final detail row. A carve/full-load
                // EFFPASSWALK pass would derive a floor here, but direct stamp 0x40000 does not.
                const int floorTileX = 2;
                const int floorTileY = 3;
                stamp.ColorOrLighting[floorTileY * 6 + floorTileX] = 1;

                BdbPmpWorldBuilder.BlitPmpIntoWorld(
                    stamp,
                    StairWorldX,
                    StairWorldY,
                    width,
                    height,
                    result.BlockIdx,
                    result.Zone,
                    result.Terrain,
                    result.HasTpl,
                    result.Voxels,
                    terrain.SyncStampedTile);
                int seededSubX = (StairWorldX + seededTileX) * 8 + 4;
                int seededSubY = (StairWorldY + seededTileY) * 8 + 4;
                Assert.That(
                    terrain.Nav!.BaseSubAt(seededSubX, seededSubY),
                    Is.EqualTo(NavWorld.SubWalk | NavWorld.SubFloor),
                    "FUN_005992b0:538-568 blits the authored RLE before any validator pass");
                terrain.SyncNativeMaterialBand(
                    StairWorldX,
                    StairWorldY,
                    StairWorldX + 5,
                    StairWorldY + 5);

                bool graphReadyInsideNotification = false;
                terrain.TilesChanged += (_, _, _, _) =>
                {
                    int seededTile = (StairWorldY + seededTileY) * width + StairWorldX + seededTileX;
                    graphReadyInsideNotification = terrain.Nav!.BaseCost[seededTile] == 20
                        && terrain.Nav.HasFloorOrPassageFlag(seededTile);
                };
                terrain.RefreshStampedRect(
                    StairWorldX,
                    StairWorldY,
                    StairWorldX + 5,
                    StairWorldY + 5);

                Assert.That(graphReadyInsideNotification, Is.True,
                    "TilesChanged must observe EFFBLOCK over the authored plane");
                Assert.That(
                    terrain.Nav.BaseSubAt(seededSubX, seededSubY),
                    Is.EqualTo(NavWorld.SubWalk | NavWorld.SubFloor),
                    "FUN_005a3fd0:36-40 queues no EFFPASSWALK for direct stamp 0x40000");
                int seededTile = (StairWorldY + seededTileY) * width + StairWorldX + seededTileX;
                Assert.That(terrain.Nav.ExitPairAndFlags[seededTile], Is.EqualTo(0x64),
                    "FUN_005a6350:281-290 derives the packed pair and cost from the authored cell");
                int floorSubX = (StairWorldX + floorTileX) * 8 + 4;
                int floorSubY = (StairWorldY + floorTileY) * 8 + 6;
                Assert.That(
                    terrain.Nav.BaseSubAt(floorSubX, floorSubY),
                    Is.Zero,
                    "the unstamped geometry floor is not derived until a bit-0 validator pass");
                int geometryTile = (StairWorldY + floorTileY) * width + StairWorldX + floorTileX;
                Assert.That(terrain.Nav.BaseCost[geometryTile], Is.EqualTo(-1),
                    "FUN_005a6350:236-238 assigns -1 when the authored plane has no walk cell");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LiveStamp_TransparentTilesKeepVerbatimRleThroughEffBlock()
        {
            const int width = 16;
            const int height = 16;
            const int stampX = 7;
            const int stampY = 5;
            var root = new GameObject(nameof(LiveStamp_TransparentTilesKeepVerbatimRleThroughEffBlock));
            try
            {
                BdbModel bdb = BuildDigBdb();
                BdbPmpWorldBuilder.TclWorldResult result = BuildWorld(width, height, defaultBlock: 2);
                var terrain = root.AddComponent<DiggableTerrain>();
                terrain.Init(result, bdb, string.Empty, () => { });

                PmpMap stamp = BuildStamp(width: 1, height: 4, block: 2, terrainByte: 0);
                for (int localY = 0; localY < 4; localY++)
                {
                    byte authored = localY == 0 || localY == 3
                        ? (byte)(NavWorld.SubWalk | NavWorld.SubFloor)
                        : NavWorld.SubWalk;
                    SetStampSub(stamp, 0, localY, 4, 4, authored);
                }

                BdbPmpWorldBuilder.BlitPmpIntoWorld(
                    stamp,
                    stampX,
                    stampY,
                    width,
                    height,
                    result.BlockIdx,
                    result.Zone,
                    result.Terrain,
                    result.HasTpl,
                    result.Voxels,
                    terrain.SyncStampedTile);
                for (int localY = 0; localY < 4; localY++)
                {
                    byte expectedSeed = localY == 0 || localY == 3
                        ? (byte)(NavWorld.SubWalk | NavWorld.SubFloor)
                        : NavWorld.SubWalk;
                    Assert.That(
                        terrain.Nav!.BaseSubAt(stampX * 8 + 4, (stampY + localY) * 8 + 4),
                        Is.EqualTo(expectedSeed),
                        "the independent FUN_00596550 plane copy includes transparent tile records");
                }
                terrain.RefreshStampedRect(stampX, stampY, stampX, stampY + 3);

                for (int localY = 0; localY < 4; localY++)
                {
                    int tile = (stampY + localY) * width + stampX;
                    int subX = stampX * 8 + 4;
                    int subY = (stampY + localY) * 8 + 4;
                    Assert.That(result.HasTpl[tile], Is.True,
                        "FUN_005992b0:461-529 marks the complete clipped stamp rectangle");
                    Assert.That(result.BlockIdx[tile], Is.EqualTo(2),
                        "transparent tile records must retain their destination block");
                    byte expectedPlane = localY == 0 || localY == 3
                        ? (byte)(NavWorld.SubWalk | NavWorld.SubFloor)
                        : NavWorld.SubWalk;
                    Assert.That(terrain.Nav!.BaseSubAt(subX, subY), Is.EqualTo(expectedPlane),
                        "direct stamp validation must not overwrite transparent authored RLE bytes");
                    Assert.That(
                        terrain.Nav.BaseCost[tile],
                        Is.EqualTo(localY == 0 || localY == 3 ? 20 : 50),
                        "FUN_005a6350:279/:287 selects 50 only without floor or object passage");
                }

                var pathfinder = new Calc1Pathfinder(terrain.Nav!);
                Assert.That(pathfinder.FindPath(
                        stampX * 8 + 4,
                        stampY * 8 + 4,
                        stampX * 8 + 4,
                        (stampY + 3) * 8 + 4,
                        canClimb: true),
                    Is.Not.Null,
                    "the authored plain-walk lattice remains connected through the transparent stamp rect");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LiveStamp_SolidGeometryDoesNotBridgeAuthoredWallEndpoints()
        {
            // The retired R1-R3 heuristic copied compatible endpoint records across up to four
            // solid stamped tiles. Direct stamp EFFBLOCK FUN_005a6350:237-290 stays local to the
            // authored plane; a later dig/full-load EFFPASSWALK decides cells from detail bytes.
            const int width = 16;
            const int height = 16;
            const int tileX = 8;
            const int upperY = 5;
            const int lowerY = 10;
            var subGrid = new byte[width * height * PmpMap.SubGridLength];
            var hasTemplate = new BitArray(width * height);
            for (int tileY = upperY; tileY <= lowerY; tileY++)
                hasTemplate[tileY * width + tileX] = true;
            SetWorldSub(subGrid, width, tileX * 8 + 4, upperY * 8 + 4, NavWorld.SubWalk);
            SetWorldSub(subGrid, width, tileX * 8 + 4, lowerY * 8 + 4, NavWorld.SubWalk);

            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                subGrid,
                BuildDigBdb(),
                hasTemplate,
                new ushort[width * height],
                deriveImmediately: false);
            nav.DeriveAll();

            for (int tileY = upperY + 1; tileY < lowerY; tileY++)
            {
                int tile = tileY * width + tileX;
                Assert.That(nav.BaseSubAt(tileX * 8 + 4, tileY * 8 + 4), Is.Zero,
                    "the fixture deliberately leaves the authored plane empty");
                Assert.That(nav.BaseCost[tile], Is.EqualTo(-1),
                    "EFFBLOCK must not carry endpoint +0x06 through missing authored bytes");
                Assert.That(nav.ExitPairAndFlags[tile], Is.Zero,
                    "EFFBLOCK's blocked branch leaves the fresh +0x12 byte untouched");
            }

            var pathfinder = new Calc1Pathfinder(nav);
            int startX = tileX * 8 + 4;
            int startY = upperY * 8 + 4;
            int goalY = lowerY * 8 + 4;
            Assert.That(pathfinder.FindPath(startX, startY, startX, goalY, canClimb: true),
                Is.Null, "compatible endpoint pairs cannot manufacture a through-earth route");

            nav.RevalidateStampRect(tileX, upperY, tileX, lowerY);

            Assert.That(pathfinder.FindPath(startX, startY, startX, goalY, canClimb: true),
                Is.Null,
                "direct-stamp EFFBLOCK cannot manufacture a bridge between authored endpoints");
            Assert.That(nav.BaseCost[upperY * width + tileX], Is.EqualTo(50));
            Assert.That(nav.BaseCost[lowerY * width + tileX], Is.EqualTo(50));

            nav.RevalidateRect(
                tileX * PmpMap.SubGridSide,
                upperY * PmpMap.SubGridSide,
                tileX * PmpMap.SubGridSide + PmpMap.SubGridSide - 1,
                lowerY * PmpMap.SubGridSide + PmpMap.SubGridSide - 1);

            Assert.That(pathfinder.FindPath(startX, startY, startX, goalY, canClimb: true),
                Is.Null,
                "dig/full-load EFFPASSWALK clears solid-detail endpoints before EFFBLOCK");
            Assert.That(nav.BaseCost[upperY * width + tileX], Is.EqualTo(-1));
            Assert.That(nav.BaseCost[lowerY * width + tileX], Is.EqualTo(-1));
        }

        [Test]
        public void EffPassWalk_UsesAuthoredBit7_NotTemplatePresence()
        {
            const int width = 16;
            const int height = 16;
            var blocks = new uint[width * height];
            for (int index = 0; index < blocks.Length; index++) blocks[index] = 2;
            blocks[9 * width + 6] = 1;
            var hasTemplate = new BitArray(width * height, false);
            var nativeFlags = new ushort[width * height];
            var nav = new NavWorld(
                width,
                height,
                blocks,
                new byte[width * height * PmpMap.SubGridLength],
                BuildDigBdb(),
                hasTemplate,
                nativeFlags,
                deriveImmediately: false);
            const int floorSubX = 6 * 8 + 2;
            const int floorSubY = 9 * 8 + 6;
            int preconditionTile = 10 * width + 6;

            nativeFlags[preconditionTile] = 0x82;
            Assert.That(nav.DeriveSubCell(floorSubX, floorSubY), Is.Zero,
                "authored tile+0x18 bit 7 is the force-wall precondition");

            nativeFlags[preconditionTile] = 2;

            Assert.That(hasTemplate[preconditionTile], Is.False,
                "EFFPASSWALK must not fabricate or require template coverage");
            Assert.That(nav.DeriveSubCell(floorSubX, floorSubY), Is.EqualTo(3),
                "clear-bit terrain must enter EFFPASSWALK even outside HasTpl");
        }

        [Test]
        public void TerrainInit_CarriesRawPmpBit7IntoNativeFlags()
        {
            const int width = 2;
            const int height = 2;
            var root = new GameObject(nameof(TerrainInit_CarriesRawPmpBit7IntoNativeFlags));
            try
            {
                BdbPmpWorldBuilder.TclWorldResult result = BuildWorld(width, height, defaultBlock: 0);
                result.NativeTerrainFlags = Array.Empty<ushort>();
                result.Terrain[1] = 0x82;
                var terrain = root.AddComponent<DiggableTerrain>();

                terrain.Init(result, BuildDigBdb(), string.Empty, () => { });

                Assert.That(result.NativeTerrainFlags[1] & 0x80, Is.EqualTo(0x80),
                    "boot synthesis must copy PMP terrain bit 7 (FUN_005992b0:315,339-377)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TerrainInit_BlitsRleSeedThenRunsFullWorldEffPassWalk()
        {
            const int width = 16;
            const int height = 16;
            const int subX = 64;
            const int subY = 64;
            var root = new GameObject(nameof(TerrainInit_BlitsRleSeedThenRunsFullWorldEffPassWalk));
            try
            {
                BdbPmpWorldBuilder.TclWorldResult result = BuildWorld(width, height, defaultBlock: 2);
                SetWorldSub(
                    result.Voxels,
                    width,
                    subX,
                    subY,
                    NavWorld.SubWalk | NavWorld.SubFloor);
                var terrain = root.AddComponent<DiggableTerrain>();

                terrain.Init(result, BuildDigBdb(), string.Empty, () => { });

                int tile = (subY >> 3) * width + (subX >> 3);
                Assert.That(terrain.Nav!.BaseSubAt(subX, subY), Is.EqualTo(NavWorld.SubWalk),
                    "full 0x5f validation runs EFFPASSWALK after the boot RLE seed (FUN_005931f0:246-252)");
                Assert.That(terrain.Nav.BaseCost[tile], Is.EqualTo(50),
                    "EFFBLOCK must consume the boot-derived plane, not the stale authored floor seed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SavedTileGraph_SurvivesRevalidationOutsideDirtyMargins()
        {
            const int width = 32;
            const int height = 32;
            int tileCount = width * height;
            var navigationState = new byte[tileCount];
            var floorAndExitState = new byte[tileCount];
            for (int tile = 0; tile < tileCount; tile++) navigationState[tile] = 0xFF;
            const int authoredX = 24;
            const int authoredY = 24;
            int authoredTile = authoredY * width + authoredX;
            navigationState[authoredTile] = 0x14;
            floorAndExitState[authoredTile] = 0xE4;
            var nav = new NavWorld(
                width,
                height,
                new uint[tileCount],
                new byte[tileCount * PmpMap.SubGridLength],
                BuildDigBdb(),
                deriveImmediately: false);
            nav.RestoreTileGraph(navigationState, floorAndExitState);

            nav.RevalidateRect(8 * 8, 8 * 8, 8 * 8 + 7, 8 * 8 + 7);

            Assert.That(nav.BaseCost[authoredTile], Is.EqualTo(20));
            Assert.That(nav.HasFloorOrPassageFlag(authoredTile), Is.True);
            Assert.That(nav.PackedExitPair(authoredTile), Is.EqualTo(0x24));
            Assert.That(nav.CaptureExitPairAndFlagsState()[authoredTile], Is.EqualTo(0xE4),
                "saved +0x12 must survive a carve validator whose dirty margins do not reach it");
        }

        [Test]
        public void StampedRubbleCarve_ExtendsNav_AndNextDigFaceResolves()
        {
            const int width = 20;
            const int height = 20;
            const int floorSubY = 9 * 8 + 6;
            var root = new GameObject(nameof(StampedRubbleCarve_ExtendsNav_AndNextDigFaceResolves));
            try
            {
                BdbModel bdb = BuildDigBdb();
                BdbPmpWorldBuilder.TclWorldResult result = BuildWorld(width, height, defaultBlock: 2);
                for (int tileX = 6; tileX <= 8; tileX++)
                {
                    result.BlockIdx[9 * width + tileX] = 1;
                    SetWorldSub(result.Voxels, width, tileX * 8 + 4, floorSubY,
                        NavWorld.SubWalk | NavWorld.SubFloor);
                }
                var terrain = root.AddComponent<DiggableTerrain>();
                terrain.Init(result, bdb, string.Empty, () => { });
                var navService = root.AddComponent<NavWorldService>();
                NavWorld serviceWorld = navService.World!;
                var pathfinder = new Calc1Pathfinder(serviceWorld);

                PmpMap rubble = BuildStamp(width: 4, height: 2, block: 0, terrainByte: 2);
                BdbPmpWorldBuilder.BlitPmpIntoWorld(
                    rubble,
                    9,
                    9,
                    width,
                    height,
                    result.BlockIdx,
                    result.Zone,
                    result.Terrain,
                    result.HasTpl,
                    result.Voxels,
                    terrain.SyncStampedTile);
                terrain.SyncNativeMaterialBand(9, 9, 12, 10);
                terrain.RefreshStampedRect(9, 9, 12, 10);
                Assert.That(result.NativeTerrainFlags[9 * width + 10] & 0x80, Is.Zero,
                    "diggable rubble is not authored as a force-wall tile");
                Assert.That(terrain.Nav!.BaseCost[9 * width + 10], Is.EqualTo(-1),
                    "zero authored RLE takes FUN_005a6350:236-238 and remains blocked until digging");

                terrain.SetDigMark(10, 9, 0);
                terrain.SetDigMark(10, 10, 0);
                terrain.SetDigMark(11, 9, 0);
                terrain.SetDigMark(11, 10, 0);
                bool navReadyInsideNotification = false;
                terrain.TilesChanged += (_, _, _, _) =>
                {
                    int openedTile = 9 * width + 10;
                    navReadyInsideNotification = ReferenceEquals(navService.World, serviceWorld)
                        && navService.World!.BaseCost[openedTile] != -1
                        && navService.World.HasFloorOrPassageFlag(openedTile);
                };

                Assert.That(terrain.CarveStep(10, 10, 0, out _), Is.True,
                    "the near stamped-rubble layer must carve");
                Assert.That(navReadyInsideNotification, Is.True,
                    "TilesChanged/NavWorldService must see the stamped floor already re-derived");
                Assert.That(FindFloorInTile(terrain.Nav!, 10, 9, out int openedSubX, out int openedSubY),
                    Is.True, "the dug stamped tile must gain a real floor cell");
                terrain.ClearDigMark(10, 9, 0);

                Assert.That(pathfinder.FindPath(6 * 8 + 4, floorSubY, openedSubX, openedSubY, canClimb: true),
                    Is.Not.Null, "navigation must extend through the newly dug stamp layer");

                Assert.That(terrain.TryFindDigEdge(
                        11,
                        10,
                        0,
                        openedSubX >> 3,
                        openedSubY >> 3,
                        out int edgeTileX,
                        out int edgeTileY),
                    Is.True, "the next rubble layer must resolve to the new floor mouth");
                Assert.That(edgeTileX, Is.EqualTo(10));
                Assert.That(edgeTileY, Is.InRange(9, 10));
                Assert.That(terrain.NwCornerHeight(edgeTileX, edgeTileY), Is.LessThan(0x0C),
                    "get_digedge candidates use BDB record byte0, not the nav plane");

                var interpreter = new TclInterpreter();
                DigglesTclContext context = DigglesTclEngine.Register(interpreter);
                context.Terrain = terrain;
                string nextDigFace = interpreter.Eval("dig_next {11 10 8} 0").Trim();
                Assert.That(nextDigFace, Is.Not.EqualTo("-1 -1 -1"),
                    "the remaining authored-rubble order must expose an honest next face");
                StringAssert.StartsWith("11 ", nextDigFace,
                    "the resolved order must belong to the untouched next rubble layer");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Calc1_NonClimberRejectsLadderOnlyTile()
        {
            const int width = 16;
            const int height = 16;
            var subGrid = new byte[width * height * PmpMap.SubGridLength];
            SetWorldSub(subGrid, width, 52, 52, NavWorld.SubWalk | NavWorld.SubFloor);
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                subGrid,
                new BdbModel { Header = new BdbHeader { NBlocks = 1 }, PerBlockAux = new byte[BdbModel.AuxStride] });
            nav.SetInfluence(
                7,
                60,
                52,
                60,
                52,
                -20,
                addsClimbSurface: true,
                transportKind: NavTransportKind.Ladder,
                climbX0: 60,
                climbY0: 52,
                climbX1: 60,
                climbY1: 52);
            int ladderTile = 6 * width + 7;
            var pathfinder = new Calc1Pathfinder(nav);

            Assert.That(nav.HasFloorOrPassageFlag(ladderTile), Is.False);
            Assert.That(nav.EffectiveTileHasFloorOrPassage(ladderTile), Is.True,
                "ladder influence affects transition cost/type but not raw tile+0x12");
            Assert.That(pathfinder.FindPath(52, 52, 60, 52, canClimb: false), Is.Null,
                "Calc1 gate 3 must use raw +0x12&0x40 for non-climbers");
            Assert.That(pathfinder.FindPath(52, 52, 60, 52, canClimb: true), Is.Not.Null,
                "climbers may enter the same ladder-only tile");
        }

        static BdbPmpWorldBuilder.TclWorldResult BuildWorld(int width, int height, uint defaultBlock)
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
                RenderXMin = 0,
                RenderYMin = 0,
                RenderXMax = width - 1,
                RenderYMax = height - 1,
            };
            for (int index = 0; index < tileCount; index++)
            {
                result.BlockIdx[index] = defaultBlock;
                result.Zone[index] = 4;
                result.Terrain[index] = 2;
                result.NativeTerrainFlags[index] = 2;
            }
            return result;
        }

        static PmpMap BuildStamp(int width, int height, uint block, byte terrainByte = 2)
        {
            int tileCount = width * height;
            var terrain = new byte[tileCount];
            var blocks = new uint[tileCount];
            var zones = new byte[tileCount];
            for (int index = 0; index < tileCount; index++)
            {
                terrain[index] = terrainByte;
                blocks[index] = block;
                zones[index] = 20;
            }
            return new PmpMap
            {
                Width = (uint)width,
                Height = (uint)height,
                TerrainType = terrain,
                ColorOrLighting = blocks,
                AuthoredNavigationPlane = new byte[tileCount * PmpMap.SubGridLength],
                HeightMap = zones,
            };
        }

        static BdbModel BuildDigBdb()
        {
            var aux = new byte[3 * BdbModel.AuxStride];
            for (int sub = 0; sub < PmpMap.SubGridLength; sub++)
            {
                aux[sub] = 15;
                aux[BdbModel.AuxStride + sub] = (byte)((sub >> 3) == 7 ? 15 : 0);
                aux[2 * BdbModel.AuxStride + sub] = 0;
            }
            for (int block = 0; block < 3; block++)
            {
                int blockBase = block * BdbModel.AuxStride;
                for (int corner = 0; corner < 4; corner++)
                {
                    uint neighbour = block == 0 ? 1u : 0xFFFFFFFFu;
                    for (int byteIndex = 0; byteIndex < 4; byteIndex++)
                        aux[blockBase + 0x40 + corner * 4 + byteIndex] =
                            (byte)(neighbour >> (byteIndex * 8));
                    aux[blockBase + 0x50 + corner] = 11;
                }
            }
            return new BdbModel
            {
                Header = new BdbHeader { NBlocks = 3 },
                PerBlockAux = aux,
            };
        }

        static void SetStampSub(PmpMap stamp, int tileX, int tileY, int localX, int localY, byte value)
        {
            int rasterWidth = checked((int)stamp.Width * PmpMap.SubGridSide);
            int subX = tileX * PmpMap.SubGridSide + localX;
            int subY = tileY * PmpMap.SubGridSide + localY;
            stamp.AuthoredNavigationPlane[subY * rasterWidth + subX] = value;
        }

        static void SetWorldSub(byte[] subGrid, int width, int subX, int subY, byte value)
        {
            int tile = (subY >> 3) * width + (subX >> 3);
            int local = ((subY & 7) << 3) + (subX & 7);
            subGrid[tile * PmpMap.SubGridLength + local] = value;
        }

        static bool FindFloorInTile(NavWorld nav, int tileX, int tileY, out int subX, out int subY)
        {
            for (int localY = 0; localY < PmpMap.SubGridSide; localY++)
            for (int localX = 0; localX < PmpMap.SubGridSide; localX++)
            {
                int candidateX = tileX * PmpMap.SubGridSide + localX;
                int candidateY = tileY * PmpMap.SubGridSide + localY;
                if ((nav.BaseSubAt(candidateX, candidateY) &
                     (NavWorld.SubWalk | NavWorld.SubFloor)) !=
                    (NavWorld.SubWalk | NavWorld.SubFloor))
                {
                    continue;
                }
                subX = candidateX;
                subY = candidateY;
                return true;
            }
            subX = subY = -1;
            return false;
        }
    }
}
