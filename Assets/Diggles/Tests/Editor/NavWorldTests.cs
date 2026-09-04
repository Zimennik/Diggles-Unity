#nullable enable

namespace Diggles.Tests
{
    using System.Collections;
    using Diggles.Runtime;
    using Diggles.Runtime.Navigation;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // P0 (original-pathfinding port) — the pathfinding data layer. Verifies, with NO Play mode / no real
    // data files, the two pieces the previous Calc1 port got wrong:
    // 1. The PMP authored navigation plane is a SUB-CELL RASTER; de-rastering it into the per-tile
    // sub-grid must keep a horizontal shelf on ONE tile sub-row (the old tile-major Array.Copy scrambled it).
    // 2. The FUN_005a6350 floor-flag derivation + the per-sub-cell GetHmapSub (the port collapsed the 8×8
    // heightmap to a single per-tile MAX corner → cliffs read "all 15 / all solid").
    // Ground truth: Docs/ORIGINAL_PATHFINDING_RE.md §1.
    public sealed class NavWorldTests
    {
        // --- 1. De-raster: a horizontal shelf in the RASTER source lands on the right tile sub-row ---
        [Test]
        public void BlitPmp_DeRastersShelf_ToCorrectTileSubRow()
        {
            const int W = 2, H = 1, subW = W * 8;   // 16 sub-cols × 8 sub-rows
            var raster = new byte[W * H * 64];
            // A shelf along sub-row 4 (raster row 4 = all 16 sub-cols walkable+floor = 0x03).
            for (int gx = 0; gx < subW; gx++) raster[4 * subW + gx] = 0x03;

            // Non-zero terrain corners make the record writes non-transparent. The authored
            // navigation plane itself is copied regardless (FUN_005992b0:553-568); this test also
            // exercises the block/terrain side of the ordinary non-transparent blit.
            var terrain = new byte[W * H];
            for (int i = 0; i < terrain.Length; i++) terrain[i] = 1;
            var pmp = new PmpMap
            {
                Width = W, Height = H, AuthoredNavigationPlane = raster,
                TerrainType = terrain, ColorOrLighting = new uint[W * H], HeightMap = new byte[W * H],
            };
            var worldVox = new byte[W * H * 64];
            BdbPmpWorldBuilder.BlitPmpIntoWorld(
                pmp, 0, 0, W, H,
                new uint[W * H], new byte[W * H], new byte[W * H], new BitArray(W * H), worldVox);

            // Both tiles: ONLY sub-row 4 is 0x03; every other sub-cell is 0. (The old tile-major copy would
            // have smeared raster rows 4..7 into tile 1's 64 cells.)
            for (int tile = 0; tile < W * H; tile++)
                for (int s = 0; s < 64; s++)
                {
                    byte expected = (s >> 3) == 4 ? (byte)0x03 : (byte)0x00;
                    Assert.AreEqual(expected, worldVox[tile * 64 + s],
                        $"tile {tile} sub {s} (row {s >> 3}, col {s & 7})");
                }
        }

        [Test]
        public void PmpParser_ExposesTrailerAsAuthoredNavigationPlane()
        {
            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(
                       stream,
                       System.Text.Encoding.UTF8,
                       leaveOpen: true))
            {
                writer.Write(1u);
                writer.Write(1u);
                writer.Write((byte)1);
                writer.Write(0u);
                writer.Write(3u);
                writer.Write((ushort)(1 << 2));
                writer.Write((ushort)((2 << 2) | 1));
                writer.Write((ushort)((61 << 2) | 3));
                writer.Write((byte)20);
            }
            stream.Position = 0;

            PmpMap map = PmpParser.Parse(stream, stream.Length);

            Assert.That(map.AuthoredNavigationPlane.Length, Is.EqualTo(PmpMap.SubGridLength));
            Assert.That(map.AuthoredNavigationPlane[0], Is.Zero);
            Assert.That(map.AuthoredNavigationPlane[1], Is.EqualTo(1));
            Assert.That(map.AuthoredNavigationPlane[2], Is.EqualTo(1));
            Assert.That(map.AuthoredNavigationPlane[3], Is.EqualTo(3));
            Assert.That(map.AuthoredNavigationPlane[63], Is.EqualTo(3));
            Assert.That(map.NavigationAt(0, 0, 3, 0), Is.EqualTo(3),
                "FUN_005992b0:538-544 decodes the trailer directly into the blitted nav raster");
        }

        [Test]
        public void PmpParser_ZeroRunExpandsToNativeFourteenBitMaximum()
        {
            const int width = 16;
            const int height = 16;
            const int tileCount = width * height;
            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(
                       stream,
                       System.Text.Encoding.UTF8,
                       leaveOpen: true))
            {
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write(new byte[tileCount]);
                for (int tile = 0; tile < tileCount; tile++) writer.Write(0u);
                writer.Write(1u);
                writer.Write((ushort)(NavWorld.SubWalk | NavWorld.SubFloor));
                writer.Write(new byte[tileCount]);
            }
            stream.Position = 0;

            PmpMap map = PmpParser.Parse(stream, stream.Length);

            Assert.That(map.AuthoredNavigationPlane.Length, Is.EqualTo(0x4000));
            Assert.That(
                map.AuthoredNavigationPlane,
                Has.All.EqualTo((byte)(NavWorld.SubWalk | NavWorld.SubFloor)),
                "FUN_005992b0:541-544 interprets a zero 14-bit run as 0x4000 bytes");
        }

        static NavWorld Nav1(byte[] sub64, BdbModel? bdb = null)
            => new NavWorld(1, 1, new uint[] { 0 }, sub64, bdb ?? new BdbModel());

        // --- 2. FUN_005a6350 derivation ---
        [Test]
        public void Derive_FloorShelf_WritesCostAndPackedExitPair()
        {
            var sub = new byte[64];
            for (int col = 0; col < 8; col++) sub[4 * 8 + col] = 0x03;   // floor shelf on row 4
            var nav = Nav1(sub);
            Assert.AreEqual((sbyte)20, nav.BaseCost[0], "floor tile base cost = 20");
            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0x64),
                "+0x12 packs subX=4, subY=4 beneath flag 0x40");
            Assert.That(nav.ExitSubX(0), Is.EqualTo(4));
            Assert.That(nav.ExitSubY(0), Is.EqualTo(4));
            Assert.IsTrue(nav.HasFloorOrPassageFlag(0));
            Assert.AreEqual(8, nav.WalkableSubCellCount());
        }

        [Test]
        public void Derive_SolidTile_IsBlocked()
        {
            var nav = Nav1(new byte[64]);                 // no walkable sub-cell
            Assert.AreEqual((sbyte)(-1), nav.BaseCost[0], "no walkable sub-cell ⇒ solid (-1)");
            Assert.IsFalse(nav.HasFloorOrPassageFlag(0));
            Assert.IsFalse(nav.TileValid(0, 0));
        }

        [Test]
        public void Derive_WalkableNonFloor_Cost50_NotFloor()
        {
            var sub = new byte[64]; sub[36] = 0x01;       // walkable only (no floor bit), central
            var nav = Nav1(sub);
            Assert.AreEqual((sbyte)50, nav.BaseCost[0], "walkable-non-floor base cost = 50");
            Assert.IsFalse(nav.HasFloorOrPassageFlag(0));
            Assert.AreEqual(0x24, nav.ExitPairAndFlags[0]);
        }

        [Test]
        public void Derive_ObjectPassageBit_SetsPassageFlagAndCost20()
        {
            var sub = new byte[64];
            sub[36] = NavWorld.SubWalk | NavWorld.SubObjectPassage;
            var nav = Nav1(sub);
            Assert.IsTrue(nav.HasFloorOrPassageFlag(0),
                "EFFBLOCK ORs plane bit3 into the +0x12 bit6 passage category");
            Assert.AreEqual((sbyte)20, nav.BaseCost[0]);
            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0x64));
        }

        [Test]
        public void Derive_AllWalkableFloorBand_SettlesPackedPairDown()
        {
            var sub = new byte[64];
            for (int index = 0; index < sub.Length; index++) sub[index] = NavWorld.SubWalk;
            sub[4 * 8 + 4] |= NavWorld.SubFloor;
            sub[5 * 8 + 4] |= NavWorld.SubFloor;

            var nav = Nav1(sub);

            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0x6C),
                "FUN_005a6350:256-278 settles (4,4) down to floor-bearing (4,5)");
            Assert.That(nav.ExitSubX(0), Is.EqualTo(4));
            Assert.That(nav.ExitSubY(0), Is.EqualTo(5));
            Assert.That(nav.BaseCost[0], Is.EqualTo(20));
        }

        [Test]
        public void Derive_AllWalkableWithoutFloor_UsesNativeCenterPair()
        {
            var sub = new byte[64];
            for (int index = 0; index < sub.Length; index++) sub[index] = NavWorld.SubWalk;

            var nav = Nav1(sub);

            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0x24),
                "FUN_005a6350:252-253 writes packed center (4,4) for an all-walkable plain tile");
            Assert.That(nav.BaseCost[0], Is.EqualTo(50));
        }

        [Test]
        public void Derive_PartiallyWalkableFloorBand_DoesNotSettlePair()
        {
            var sub = new byte[64];
            sub[4 * 8 + 4] = NavWorld.SubWalk | NavWorld.SubFloor;
            sub[5 * 8 + 4] = NavWorld.SubWalk | NavWorld.SubFloor;

            var nav = Nav1(sub);

            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0x64),
                "FUN_005a6350:281-286 writes the strict-max pair directly for a partial tile");
        }

        [Test]
        public void Derive_EqualScorePairKeepsFirstRasterCell()
        {
            var sub = new byte[64];
            sub[4 * 8 + 3] = NavWorld.SubWalk | NavWorld.SubFloor;
            sub[4 * 8 + 5] = NavWorld.SubWalk | NavWorld.SubFloor;

            var nav = Nav1(sub);

            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0x63),
                "FUN_005a6350:121 keeps the first raster cell because replacement is strict >");
        }

        [Test]
        public void Derive_BlockedTileLeavesPackedExitByteStale()
        {
            var sub = new byte[64];
            var nav = new NavWorld(
                1,
                1,
                new uint[] { 0 },
                sub,
                new BdbModel(),
                deriveImmediately: false);
            nav.RestoreTileGraph(new byte[] { 0x14 }, new byte[] { 0xE4 });

            nav.DeriveTile(0, 0);

            Assert.That(nav.BaseCost[0], Is.EqualTo(-1));
            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0xE4),
                "FUN_005a6350:237-238 writes only +0x06 when no plane cell is walkable");
        }

        [Test]
        public void Derive_AllSolidTwoByTwoWindowBlocksWalkUnlessObjectPassageExists()
        {
            const int width = 2;
            const int height = 2;
            var sub = new byte[width * height * PmpMap.SubGridLength];
            sub[0x24] = NavWorld.SubWalk;
            var flags = new ushort[] { 0x80, 0x80, 0x80, 0x80 };
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                sub,
                new BdbModel(),
                new BitArray(width * height),
                flags,
                deriveImmediately: false);
            nav.RestoreTileGraph(
                new byte[] { 0x14, 0xFF, 0xFF, 0xFF },
                new byte[] { 0xA4, 0, 0, 0 });

            nav.DeriveTile(0, 0);

            Assert.That(nav.BaseCost[0], Is.EqualTo(-1));
            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0xA4),
                "FUN_005a6350:241-245 blocks the 2x2 bit7 window and leaves +0x12 stale");

            sub[0x24] |= NavWorld.SubObjectPassage;
            nav.DeriveTile(0, 0);

            Assert.That(nav.BaseCost[0], Is.EqualTo(20));
            Assert.That(nav.ExitPairAndFlags[0], Is.EqualTo(0x64),
                "plane bit3 bypasses the solid-window gate and sets the passage category");
        }

        // --- 3. GetHmapSub is PER-SUB-CELL (a cliff within one tile), not the per-tile MAX corner ---
        [Test]
        public void GetHmapSub_ReadsPerSubCellHeight_NotCollapsed()
        {
            var aux = new byte[89];
            // 8×8 heightmap (aux 0x00..0x3F): left half (col<4) solid 15, right half open 0 — a cliff IN one tile.
            for (int s = 0; s < 64; s++) aux[s] = (byte)((s & 7) < 4 ? 15 : 0);
            var bdb = new BdbModel { Header = new BdbHeader { NBlocks = 1 }, PerBlockAux = aux };
            var nav = new NavWorld(1, 1, new uint[] { 0 }, new byte[64], bdb);

            Assert.AreEqual(15, nav.GetHmapSub(0, 0), "col 0 = solid");
            Assert.AreEqual(15, nav.GetHmapSub(3, 0), "col 3 = solid");
            Assert.AreEqual(0,  nav.GetHmapSub(4, 0), "col 4 = open (the cliff edge inside the tile)");
            Assert.AreEqual(0,  nav.GetHmapSub(7, 5), "col 7 = open");
            Assert.AreEqual(15, nav.GetHmapSub(100, 0), "out of bounds = solid (15)");
        }

        [Test]
        public void EffPassWalk_AfterDigReadsCurrentBlockVariant_NotPristineTemplateBlock()
        {
            const int width = 16;
            const int height = 16;
            const int targetX = 64;
            const int targetY = 80;
            int tileCount = width * height;
            var blocks = new uint[tileCount];
            var aux = new byte[2 * BdbModel.AuxStride];
            for (int sub = 0; sub < PmpMap.SubGridLength; sub++) aux[sub] = 15;
            var nav = new NavWorld(
                width,
                height,
                blocks,
                new byte[tileCount * PmpMap.SubGridLength],
                new BdbModel
                {
                    Header = new BdbHeader { NBlocks = 2 },
                    PerBlockAux = aux,
                },
                deriveImmediately: false);

            Assert.That(nav.GetHmapSub(targetX, targetY), Is.EqualTo(15));
            Assert.That(nav.DeriveSubCell(targetX, targetY), Is.Zero);

            // Native FUN_00596b80:64-134 replaces node+0 with a dug block variant. EFFPASSWALK's
            // gather then follows that current id (FUN_00595e40:69-77), not the PMP's original id.
            for (int tile = 0; tile < blocks.Length; tile++) blocks[tile] = 1;
            nav.RevalidateRect(targetX, targetY, targetX, targetY);

            Assert.That(nav.GetHmapSub(targetX, targetY), Is.Zero);
            Assert.That(nav.BaseSubAt(targetX, targetY), Is.EqualTo(NavWorld.SubWalk),
                "post-dig EFFPASSWALK must derive from the live block variant's aux bytes");
        }

        [Test]
        public void EffPassWalk_RequiresEveryExactNativeCapsuleProbe()
        {
            const int width = 16;
            const int height = 16;
            const int targetX = 64;
            const int targetY = 80;
            int tileCount = width * height;
            var blocks = new uint[tileCount];
            var aux = new byte[tileCount * BdbModel.AuxStride];
            for (int tile = 0; tile < tileCount; tile++) blocks[tile] = (uint)tile;
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = (uint)tileCount },
                PerBlockAux = aux,
            };
            var nav = new NavWorld(
                width,
                height,
                blocks,
                new byte[tileCount * PmpMap.SubGridLength],
                bdb,
                deriveImmediately: false);
            void SetDetail(int subX, int subY, byte value)
            {
                int tile = (subY >> 3) * width + (subX >> 3);
                int local = ((subY & 7) << 3) + (subX & 7);
                aux[checked((int)blocks[tile] * BdbModel.AuxStride + local)] = value;
            }

            // FUN_005a6b40:116-419, read in exact nested-branch call order. The scratch address is
            // (sy+dy)*stride+(sx+dx): dx is the unscaled column add and dy multiplies the row stride.
            (int dx, int dy)[] probes =
            {
                (0, -8), (-7, -15), (7, -15), (0, -15), (0, -1), (-7, -8), (7, -8),
                (-4, -3), (4, -3), (-7, -5), (7, -5), (-7, -12), (7, -12),
                (-4, -15), (4, -15), (0, -12), (0, -4), (-4, -8), (4, -8),
                (-3, -13), (3, -13), (-5, -11), (5, -11), (-2, -10), (2, -10),
                (-2, -6), (2, -6), (-5, -5), (5, -5), (-2, -2), (2, -2),
            };

            Assert.That(nav.DeriveSubCell(targetX, targetY), Is.EqualTo(NavWorld.SubWalk));
            Assert.That(probes, Has.Length.EqualTo(31));
            foreach ((int dx, int dy) in probes)
            {
                SetDetail(targetX + dx, targetY + dy, 0x0E);
                Assert.That(
                    nav.TryGetEffPassWalkBlockingProbe(
                        targetX,
                        targetY,
                        out int blockingDx,
                        out int blockingDy,
                        out int blockingDetail),
                    Is.True);
                Assert.That((blockingDx, blockingDy, blockingDetail), Is.EqualTo((dx, dy, 0x0E)),
                    $"probe-table mismatch at native offset ({dx},{dy})");
                Assert.That(
                    nav.DeriveSubCell(targetX, targetY),
                    Is.Zero,
                    $"native capsule probe ({dx},{dy}) must block at detail 0x0e");
                SetDetail(targetX + dx, targetY + dy, 0);
            }

            foreach ((int dx, int dy) in probes)
                SetDetail(targetX + dx, targetY + dy, 0x0E);
            for (int index = 0; index < probes.Length; index++)
            {
                (int dx, int dy) = probes[index];
                Assert.That(
                    nav.TryGetEffPassWalkBlockingProbe(
                        targetX,
                        targetY,
                        out int blockingDx,
                        out int blockingDy,
                        out _),
                    Is.True);
                Assert.That((blockingDx, blockingDy), Is.EqualTo((dx, dy)),
                    $"probe {index + 1} must retain FUN_005a6b40's short-circuit order");
                SetDetail(targetX + dx, targetY + dy, 0);
            }
            Assert.That(
                nav.TryGetEffPassWalkBlockingProbe(targetX, targetY, out _, out _, out _),
                Is.False);

            SetDetail(targetX, targetY - 8, 0x0D);
            Assert.That(nav.DeriveSubCell(targetX, targetY), Is.EqualTo(NavWorld.SubWalk),
                "detail 0x0d must remain below the native threshold");
            SetDetail(targetX, targetY - 8, 0x8D);
            Assert.That(nav.DeriveSubCell(targetX, targetY), Is.Zero,
                "FUN_005a6b40 compares the raw detail byte, not its low nibble");
            SetDetail(targetX, targetY - 8, 0);

            SetDetail(targetX - 1, targetY - 1, 0x0E);
            Assert.That(nav.DeriveSubCell(targetX, targetY), Is.EqualTo(NavWorld.SubWalk),
                "a nearby cell absent from the 31-probe table must not widen the capsule");
        }

        [Test]
        public void EffPassWalk_LastOpenFloorConfirmReadsYMinusTen()
        {
            const int width = 16;
            const int height = 16;
            const int targetX = 64;
            const int targetY = 80;
            int tileCount = width * height;
            var blocks = new uint[tileCount];
            var aux = new byte[tileCount * BdbModel.AuxStride];
            for (int tile = 0; tile < tileCount; tile++) blocks[tile] = (uint)tile;
            var nav = new NavWorld(
                width,
                height,
                blocks,
                new byte[tileCount * PmpMap.SubGridLength],
                new BdbModel
                {
                    Header = new BdbHeader { NBlocks = (uint)tileCount },
                    PerBlockAux = aux,
                },
                deriveImmediately: false);
            void SetDetail(int subX, int subY, byte value)
            {
                int tile = (subY >> 3) * width + (subX >> 3);
                int local = ((subY & 7) << 3) + (subX & 7);
                aux[checked((int)blocks[tile] * BdbModel.AuxStride + local)] = value;
            }

            SetDetail(targetX, targetY + 1, 15);
            Assert.That(nav.DeriveSubCell(targetX, targetY), Is.EqualTo(3),
                "an open cell above a confirmed detail jump is a native floor");

            SetDetail(targetX, targetY - 10, 13);
            Assert.That(nav.DeriveSubCell(targetX, targetY), Is.EqualTo(1),
                "VA 0x5a7381/0x5a76a8 reads y-10 for the fallback confirm; y-4 is not the executable law");
        }

        [Test]
        public void EffPassWalk_ReDeriveUsesNativeHalfOpenWorldInsets()
        {
            const int width = 16;
            const int height = 16;
            var subGrid = new byte[width * height * PmpMap.SubGridLength];
            void Seed(int subX, int subY)
            {
                int tile = (subY >> 3) * width + (subX >> 3);
                int local = ((subY & 7) << 3) + (subX & 7);
                subGrid[tile * PmpMap.SubGridLength + local] =
                    NavWorld.SubWalk | NavWorld.SubFloor;
            }
            (int x, int y)[] outside = { (23, 80), (64, 15), (104, 80), (64, 104) };
            (int x, int y)[] inside = { (24, 80), (64, 16), (103, 80), (64, 103) };
            foreach ((int x, int y) in outside) Seed(x, y);
            foreach ((int x, int y) in inside) Seed(x, y);
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                subGrid,
                new BdbModel
                {
                    Header = new BdbHeader { NBlocks = 1 },
                    PerBlockAux = new byte[BdbModel.AuxStride],
                },
                deriveImmediately: false);

            nav.ReDeriveSubGrid(0, 0, width * 8 - 1, height * 8 - 1);

            foreach ((int x, int y) in outside)
                Assert.That(nav.BaseSubAt(x, y), Is.EqualTo(3),
                    $"outside native EFFPASSWALK inset ({x},{y}) must retain the RLE seed");
            foreach ((int x, int y) in inside)
                Assert.That(nav.BaseSubAt(x, y), Is.EqualTo(1),
                    $"inside native EFFPASSWALK inset ({x},{y}) must be derived from flat detail");
        }

        [Test]
        public void EffBlock_ValidatorUsesItsNativeWorldInsets()
        {
            const int width = 16;
            const int height = 16;
            var subGrid = new byte[width * height * PmpMap.SubGridLength];
            for (int sub = 0; sub < subGrid.Length; sub++) subGrid[sub] = NavWorld.SubWalk;
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                subGrid,
                new BdbModel
                {
                    Header = new BdbHeader { NBlocks = 1 },
                    PerBlockAux = new byte[BdbModel.AuxStride],
                },
                deriveImmediately: false);

            nav.RevalidateRect(0, 0, width * 8 - 1, height * 8 - 1);

            int Cost(int tileX, int tileY) => nav.BaseCost[tileY * width + tileX];
            Assert.That(Cost(2, 1), Is.EqualTo(50));
            Assert.That(Cost(13, 13), Is.EqualTo(50));
            Assert.That(Cost(1, 1), Is.EqualTo(-1),
                "FUN_005a6350:40-69 starts at worldMinX+0x12, which first intersects tile 2");
            Assert.That(Cost(14, 13), Is.EqualTo(-1));
            Assert.That(Cost(2, 14), Is.EqualTo(-1));
        }

        [Test]
        public void GravitySettle_ProbesRowBelowRoundedAnchor()
        {
            const int anchorRow = 4;
            const int supportRow = 7;
            const float depth = 8f;
            var aux = new byte[BdbModel.AuxStride];
            for (int col = 0; col < 8; col++)
            {
                aux[col] = 15;
                aux[8 + col] = 15;
                aux[anchorRow * 8 + col] = 9;
                aux[supportRow * 8 + col] = 15;
            }
            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = 1 },
                PerBlockAux = aux,
            };
            var nav = new NavWorld(1, 1, new uint[] { 0 }, new byte[64], bdb);
            var start = new Vector3(0.5f, anchorRow * 0.125f, depth);
            int subX = Mathf.RoundToInt(start.x * 8f);

            Assert.IsFalse(nav.SubWalkable(subX, anchorRow),
                "the rounded anchor is an air row, not a floor/support cell");
            Assert.Greater(nav.GetHmapSub(subX, anchorRow), depth,
                "the anchor row deliberately contains a false support height");
            Assert.LessOrEqual(nav.GetHmapSub(subX, anchorRow + 1), depth,
                "ROUND(y*8)+1 is the native support probe and is air in this regression column");

            Vector3 settled = RuntimeWorldSpawner.GravitySettle(nav, start);

            Assert.AreEqual(supportRow * 0.125f, settled.y, 1e-5f,
                "gravity must ignore the anchor-row false positive and reach the support below");
            Assert.AreEqual(start.x, settled.x, 1e-5f, "point gravity must not move x");
            Assert.AreEqual(start.z, settled.z, 1e-5f, "point gravity must not move depth");

            var oneRowDrop = new Vector3(start.x, (supportRow - 1) * 0.125f, depth);
            Vector3 oneRowSettled = RuntimeWorldSpawner.GravitySettle(nav, oneRowDrop);
            Assert.AreEqual(supportRow * 0.125f, oneRowSettled.y, 1e-5f,
                "an air anchor with support immediately below must land on that support row");

            var authoredInSupport = new Vector3(start.x, 0f, depth);
            Vector3 authoredSettled = RuntimeWorldSpawner.GravitySettle(nav, authoredInSupport);
            Assert.AreEqual(authoredInSupport, authoredSettled,
                "an authored point supported in both its anchor and below rows must stay exact");
        }

        [Test]
        public void NativeTerrainFlags_GraphDerivationPreservesAuthoredForceWallBit()
        {
            var hasTemplate = new BitArray(1, false);
            var nativeTerrainFlags = new ushort[] { 0x82 };
            var nav = new NavWorld(
                1,
                1,
                new uint[] { 0 },
                new byte[64],
                new BdbModel(),
                hasTemplate,
                nativeTerrainFlags);

            nav.RevalidateRect(0, 0, 7, 7);

            Assert.That(hasTemplate[0], Is.False,
                "EFFPASSWALK has no template-presence gate (FUN_005a6b40:109-115)");
            Assert.That(nativeTerrainFlags[0], Is.EqualTo(0x82),
                "native terrain updates preserve authored high flags (FUN_005fc9d0:115-122)");
        }

        [Test]
        public void RebuildWaterFlagsForTile_OnlyTouchesRequestedTile()
        {
            var subGrid = new byte[2 * PmpMap.SubGridLength];
            for (int index = 0; index < subGrid.Length; index++)
                subGrid[index] = NavWorld.SubWalk | NavWorld.SubFloor;
            var nav = new NavWorld(2, 1, new uint[] { 0, 0 }, subGrid, new BdbModel());
            nav.RebuildWaterFlags((_, _) => true);

            int samples = 0;
            nav.RebuildWaterFlagsForTile(1, 0, (_, _) =>
            {
                samples++;
                return false;
            });

            Assert.That(samples, Is.EqualTo(PmpMap.SubGridLength));
            Assert.That(nav.SubIsWater(0, 0), Is.True);
            Assert.That(nav.SubIsWater(8, 0), Is.False);
            Assert.That(nav.SubWalkable(8, 0), Is.True);
        }

        // --- 4. Calc1 endpoint margin (0059d640:102-105 start + FUN_0040b8a0 goal): BOTH endpoints
        // must lie ≥ 0x28=40 sub-cells INSIDE the sub-bounds; near-edge endpoints are REJECTED even
        // when the cell itself is walkable. (Regression: the port's first reading tolerated 40
        // sub-cells OUTSIDE the bounds — inverted.) ---
        [Test]
        public void Calc1_EndpointMargin_RejectsNearEdgeEndpoints()
        {
            const int W = 12, H = 12;                  // 96×96 sub-cells ⇒ interior = [40..56)
            var sub = new byte[W * H * 64];
            // One continuous floor line across the whole map at subY 44 (tile row 5, sub-row 4).
            for (int tx = 0; tx < W; tx++)
                for (int col = 0; col < 8; col++)
                    sub[(5 * W + tx) * 64 + 4 * 8 + col] = 0x03;
            var nav = new NavWorld(W, H, new uint[W * H], sub, new BdbModel());
            var pf = new Calc1Pathfinder(nav);

            var ok = pf.FindPath(44, 44, 52, 44, canClimb: true);
            Assert.IsNotNull(ok, "interior endpoints (≥40 inside) must route");
            Assert.AreEqual((52, 44), ok![ok.Count - 1], "path must end at the goal cell");

            Assert.IsNull(pf.FindPath(20, 44, 52, 44, canClimb: true),
                "a START closer than 40 sub-cells to the bounds must be rejected (0059d640:102-105)");
            Assert.IsNull(pf.FindPath(44, 44, 70, 44, canClimb: true),
                "a GOAL closer than 40 sub-cells to the bounds must be rejected (FUN_0040b8a0)");
        }

        [Test]
        public void Calc1_VariantBLeftMaskReadsPackedCoordinateBits()
        {
            const int width = 16;
            const int height = 16;
            const int tileY = 6;
            const int leftTileX = 6;
            const int rightTileX = 7;
            var sub = new byte[width * height * PmpMap.SubGridLength];
            void SetSub(int subX, int subY, byte value)
            {
                int tile = (subY >> 3) * width + (subX >> 3);
                sub[tile * PmpMap.SubGridLength + ((subY & 7) << 3) + (subX & 7)] = value;
            }
            SetSub(leftTileX * 8 + 4, tileY * 8 + 4, NavWorld.SubWalk);
            SetSub(
                rightTileX * 8 + 4,
                tileY * 8 + 4,
                NavWorld.SubWalk | NavWorld.SubFloor);
            var nav = new NavWorld(width, height, new uint[width * height], sub, new BdbModel());
            var pathfinder = new Calc1Pathfinder(nav);

            Assert.That(nav.ExitPairAndFlags[tileY * width + leftTileX], Is.EqualTo(0x24),
                "fixture: plain walk tile stores packed pair x=4,y=4 without flag 0x40");
            Assert.That(pathfinder.FindPath(
                    rightTileX * 8 + 4,
                    tileY * 8 + 4,
                    leftTileX * 8 + 4,
                    tileY * 8 + 4,
                    canClimb: false,
                    iOwner: 0),
                Is.Not.Null,
                "0059bf90:328 tests 0xA4 on -x; packed pair 0x24 satisfies its coordinate bits");
            Assert.That(pathfinder.FindPath(
                    rightTileX * 8 + 4,
                    tileY * 8 + 4,
                    leftTileX * 8 + 4,
                    tileY * 8 + 4,
                    canClimb: false,
                    iOwner: 100),
                Is.Null,
                "variant A uses the ordinary +0x12&0x40 floor/passage gate in every direction");
        }

        [Test]
        public void InfluenceOverlay_HardBlockDetours_AndRemovalRestoresRoute()
        {
            const int width = 16;
            const int height = 16;
            var sub = new byte[width * height * 64];
            for (int tile = 0; tile < width * height; tile++)
                sub[tile * 64 + 36] = NavWorld.SubWalk | NavWorld.SubFloor;
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                sub,
                new BdbModel());
            var pathfinder = new Calc1Pathfinder(nav);

            var direct = pathfinder.FindPath(52, 52, 84, 52, canClimb: true);
            Assert.NotNull(direct);

            nav.SetInfluence(100, 64, 48, 71, 55, int.MaxValue, addsClimbSurface: false);
            var detour = pathfinder.FindPath(52, 52, 84, 52, canClimb: true);

            Assert.NotNull(detour);
            Assert.IsFalse(detour!.Exists(point => (point.sx >> 3) == 8 && (point.sy >> 3) == 6));
            Assert.Greater(detour.Count, direct!.Count);

            nav.RemoveInfluence(100);
            var restored = pathfinder.FindPath(52, 52, 84, 52, canClimb: true);
            Assert.NotNull(restored);
            Assert.AreEqual(direct.Count, restored!.Count);
        }

        [Test]
        public void InfluenceOverlay_DoorCloseOpenFlipBlocksOnlyCorridorAndInvalidatesWorld()
        {
            const int width = 16;
            const int height = 16;
            var sub = new byte[width * height * 64];
            for (int tileX = 0; tileX < width; tileX++)
                sub[(6 * width + tileX) * 64 + 36] = NavWorld.SubWalk | NavWorld.SubFloor;
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                sub,
                new BdbModel());
            var pathfinder = new Calc1Pathfinder(nav);

            Assert.NotNull(pathfinder.FindPath(52, 52, 84, 52, canClimb: true));
            int initialVersion = nav.Version;

            nav.SetInfluence(
                100,
                64,
                48,
                71,
                55,
                int.MaxValue,
                addsClimbSurface: false,
                transportKind: NavTransportKind.Door);
            int closedVersion = nav.Version;

            Assert.Greater(closedVersion, initialVersion);
            Assert.AreEqual(-1, nav.EffectiveTileBaseCost(8, 6));
            Assert.IsNull(pathfinder.FindPath(52, 52, 84, 52, canClimb: true));

            nav.SetInfluence(
                100,
                64,
                48,
                71,
                55,
                0,
                addsClimbSurface: false,
                transportKind: NavTransportKind.Door);
            int openVersion = nav.Version;

            Assert.Greater(openVersion, closedVersion);
            Assert.AreEqual(20, nav.EffectiveTileBaseCost(8, 6));
            Assert.NotNull(pathfinder.FindPath(52, 52, 84, 52, canClimb: true));

            nav.SetInfluence(
                100,
                64,
                48,
                71,
                55,
                int.MaxValue,
                addsClimbSurface: false,
                transportKind: NavTransportKind.Door);

            Assert.Greater(nav.Version, openVersion);
            Assert.IsNull(pathfinder.FindPath(52, 52, 84, 52, canClimb: true));
        }

        [Test]
        public void InfluenceOverlay_OverlapsRemainUntilEverySourceIsRemoved()
        {
            const int width = 16;
            const int height = 16;
            var sub = new byte[width * height * 64];
            for (int tile = 0; tile < width * height; tile++)
                sub[tile * 64 + 36] = NavWorld.SubWalk | NavWorld.SubFloor;
            var nav = new NavWorld(
                width,
                height,
                new uint[width * height],
                sub,
                new BdbModel());

            nav.SetInfluence(100, 64, 48, 71, 55, int.MaxValue, addsClimbSurface: false);
            nav.SetInfluence(101, 64, 48, 71, 55, int.MaxValue, addsClimbSurface: false);
            Assert.AreEqual(-1, nav.EffectiveTileBaseCost(8, 6));

            nav.RemoveInfluence(100);
            Assert.AreEqual(-1, nav.EffectiveTileBaseCost(8, 6));

            nav.RemoveInfluence(101);
            Assert.AreEqual(20, nav.EffectiveTileBaseCost(8, 6));
        }

        [Test]
        public void LadderEndpoints_UseLadderColumnAndThatFloorsDepthBand()
        {
            const int width = 16;
            const int height = 16;
            const int ladderX = 52;
            var blocks = new uint[width * height];
            var aux = new byte[width * height * BdbModel.AuxStride];
            for (int tile = 0; tile < blocks.Length; tile++)
            {
                blocks[tile] = (uint)tile;
                for (int sub = 0; sub < 64; sub++)
                    aux[tile * BdbModel.AuxStride + sub] = (byte)((sub & 7) == 0 ? 5 : 11);
            }

            var subGrid = new byte[width * height * 64];
            void SetFloor(int sx, int sy)
            {
                int tile = (sy >> 3) * width + (sx >> 3);
                int sub = tile * 64 + ((sy & 7) << 3) + (sx & 7);
                subGrid[sub] = NavWorld.SubWalk | NavWorld.SubFloor;
            }
            SetFloor(48, 47);
            SetFloor(ladderX, 47);
            SetFloor(48, 65);
            SetFloor(ladderX, 65);

            var bdb = new BdbModel
            {
                Header = new BdbHeader { NBlocks = (uint)blocks.Length },
                PerBlockAux = aux,
            };
            var nav = new NavWorld(width, height, blocks, subGrid, bdb);
            nav.SetInfluence(
                101,
                40,
                48,
                64,
                64,
                -20,
                addsClimbSurface: true,
                transportKind: NavTransportKind.Ladder,
                climbX0: ladderX,
                climbY0: 48,
                climbX1: ladderX,
                climbY1: 64);
            var cells = new System.Collections.Generic.List<(int sx, int sy)>
            {
                (48, 47),
                (ladderX, 48),
                (ladderX, 56),
                (ladderX, 64),
                (48, 65),
            };

            NavWorldService.AlignLadderEndpoints(nav, cells);

            int ladderTile = (56 >> 3) * width + (ladderX >> 3);
            Assert.AreEqual(ladderX & 7, nav.EffectivePackedExitPair(ladderTile) & 7,
                "Calc1 must receive the overlay's ladder column, not the terrain tile's old exit");
            Assert.AreEqual(ladderX, cells[0].sx, "the mount floor goal is the ladder column");
            Assert.AreEqual(ladderX, cells[cells.Count - 1].sx,
                "the dismount floor goal is the ladder column");
            Assert.IsTrue(nav.SubIsFloor(cells[0].sx, cells[0].sy));
            Assert.IsTrue(nav.SubIsFloor(cells[cells.Count - 1].sx, cells[cells.Count - 1].sy));
            float topDepth = NavWorldService.FloorLineDepth(nav, cells[0].sx, cells[0].sy);
            float bottomDepth = NavWorldService.FloorLineDepth(
                nav,
                cells[cells.Count - 1].sx,
                cells[cells.Count - 1].sy);
            Assert.AreEqual(12f, topDepth, 1e-6f);
            Assert.AreEqual(12f, bottomDepth, 1e-6f,
                "the aligned dismount feeds the existing floor-line support+1 depth law");
        }

        // FINDING-2 regression: the native ladder
        // writer is COST-ONLY — FUN_0060b600:88-98 stamps obj+0x264 = −20 / obj+0x268 = 4 into
        // the influence layers and never touches walkability or the FUN_005a6350 exit sub
        // (tile+0x12). Port law: ladder tags are strictly ADDITIVE over the natural nav state —
        // a wall climbable without the ladder stays climbable with one a column away, a corridor
        // floor tile under the band keeps its terrain floor crossing, and removal restores the
        // exact pre-ladder derived state.
        [Test]
        public void LadderInfluence_IsStrictlyAdditive_AndRemovalRestoresPreLadderState()
        {
            const int width = 16;
            const int height = 16;
            var subGrid = new byte[width * height * 64];
            void Set(int sx, int sy, byte value)
            {
                int tile = (sy >> 3) * width + (sx >> 3);
                subGrid[tile * 64 + ((sy & 7) << 3) + (sx & 7)] = value;
            }
            // A natural climbable wall column at x=51 (value 1: walkable, no floor bit)…
            for (int sy = 48; sy <= 64; sy++) Set(51, sy, NavWorld.SubWalk);
            // …and a corridor ground line running through the ladder-base tile (row 62).
            for (int sx = 40; sx <= 64; sx++)
                Set(sx, 62, (byte)(NavWorld.SubWalk | NavWorld.SubFloor));

            var nav = new NavWorld(
                width, height, new uint[width * height], subGrid, new BdbModel());
            int baseTile = (62 >> 3) * width + (52 >> 3);
            byte preExit = nav.EffectivePackedExitPair(baseTile);
            int preCost = nav.EffectiveTileBaseCost(baseTile);
            bool preFloor = nav.EffectiveTileHasFloorOrPassage(baseTile);
            Assert.IsTrue(nav.SubIsClimbWall(51, 50), "fixture: the wall is climbable pre-ladder");

            // The ladder: ±12-wide cost rect, climb surface on the exact column x=52 —
            // one column right of the natural wall.
            nav.SetInfluence(200, 40, 40, 64, 62, -20, addsClimbSurface: true,
                transportKind: NavTransportKind.Ladder,
                climbX0: 52, climbY0: 44, climbX1: 52, climbY1: 62);

            Assert.IsTrue(nav.SubIsClimbWall(51, 50),
                "natural wall climbability must survive a ladder a column away");
            Assert.AreEqual(preExit, nav.EffectivePackedExitPair(baseTile),
                "a floor tile under the band keeps its terrain floor crossing (cost-only law)");
            var pathfinder = new Calc1Pathfinder(nav);
            var through = pathfinder.FindPath(42, 62, 62, 62, canClimb: false);
            Assert.NotNull(through, "the corridor stays routable with the ladder present");
            foreach (var (sx, sy) in through!)
                Assert.IsTrue(nav.SubIsFloor(sx, sy),
                    $"corridor waypoint ({sx},{sy}) must stay on the ground line — a WALL-plane " +
                    "crossing here is the 2d48217 hijack that trapped the gnome");

            nav.RemoveInfluence(200);
            Assert.AreEqual(preExit, nav.EffectivePackedExitPair(baseTile));
            Assert.AreEqual(preCost, nav.EffectiveTileBaseCost(baseTile));
            Assert.AreEqual(preFloor, nav.EffectiveTileHasFloorOrPassage(baseTile));
            Assert.IsFalse(nav.SubHasInfluenceClimb(52, 50),
                "removal restores the exact pre-ladder state");
            Assert.IsTrue(nav.SubIsClimbWall(51, 50));
        }

        // --- 5. EMPIRICAL: where does the WorldRoot's Q_x(-90) put native elevation/depth/east in WORLD? ---
        // The terrain mesh AND the gizmo cubes both live at LOCAL = (nativeX, nativeZ*0.5+7, -nativeY) under a
        // transform whose localRotation = Euler(270,0,0). This test uses Unity's REAL quaternion to pin which
        // WORLD axis is "up" (elevation) vs the ant-farm DEPTH — the whole gizmo-orientation question. Asserts
        // the mapping so the result is unambiguous in the test log.
        [Test]
        public void WorldRoot_Qx_minus90_AxisMapping()
        {
            var root = new UnityEngine.GameObject("axisRoot");
            root.transform.localRotation = UnityEngine.Quaternion.Euler(270f, 0f, 0f);
            var child = new UnityEngine.GameObject("axisChild");
            child.transform.SetParent(root.transform, false);

            child.transform.localPosition = new UnityEngine.Vector3(0f, 10f, 0f);   // LOCAL +Y = native ELEVATION
            var elev = child.transform.position;
            child.transform.localPosition = new UnityEngine.Vector3(0f, 0f, -10f);  // LOCAL -Z = native +DEPTH
            var depth = child.transform.position;
            child.transform.localPosition = new UnityEngine.Vector3(10f, 0f, 0f);   // LOCAL +X = native EAST
            var east = child.transform.position;

            UnityEngine.Debug.Log($"[AXES] elevation(local +Y) -> world {elev}   (|y|={elev.y:0.0} |z|={elev.z:0.0})");
            UnityEngine.Debug.Log($"[AXES] depth   (local -Z) -> world {depth}");
            UnityEngine.Debug.Log($"[AXES] east    (local +X) -> world {east}");
            UnityEngine.Object.DestroyImmediate(root);

            // Confirm the hand-derived mapping: elevation falls on world Z (NOT world Y), depth on world Y.
            Assert.Less(UnityEngine.Mathf.Abs(elev.y), 0.01f, "elevation has NO world-Y component");
            Assert.Greater(UnityEngine.Mathf.Abs(elev.z), 9.9f, "elevation lands on world Z");
            Assert.Greater(UnityEngine.Mathf.Abs(depth.y), 9.9f, "depth lands on world Y");
        }
    }
}
