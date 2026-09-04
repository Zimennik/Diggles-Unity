#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.World;
    using NUnit.Framework;
    using UnityEngine;

    // Cave dig tools (Tiny/Small/Medium/Huge = TEMPLATE STAMPS) + the eraser (dig:undo), wired
    // into ClickToMoveService/BottomMenuController. Ground truth: BOTTOM_MENU_RE.md §3/§5,
    // decompile FUN_005661c0 (template load) / FUN_00566a60 (per-cell validity) / FUN_00567cb0 (stamp
    // commit) / FUN_00567410 (eraser). This pins the DATA layer (CaveTemplate/CaveStamp + the
    // DiggableTerrain cave/eraser APIs) that the click layer drives.
    //
    // The stamp validity (FUN_00566a60) needs the BDB aux (NW-corner heights + open sub-cells), so
    // the stamp/validity tests run over a SYNTHETIC 2-block BdbModel: block 0 = solid rock (aux 15),
    // block 1 = an already-open FACE (aux 11 — exposed, < 12 so it can host a mouth, ≥ the cnt-3 lip
    // target 11). A cave region painted onto block-1 tiles inside a solid block-0 field satisfies
    // every FUN_00566a60 rule. The
    // eraser + cave-flood + record APIs operate on the dig nibbles directly, so those run bare
    // (bdb=null, the DigMarkOverlayTests pattern).
    public sealed class CaveStampTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;
        const int SIZE = CaveTemplate.Size;   // 32
        const int SKIP = CaveTemplate.Skip;   // 4

        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _gos.Clear();
        }

        // ---------------- template decode (FUN_005661c0 / CaveTemplate.Decode) ----------------

        [Test]
        public void Decode_SyntheticTga_BlueChannel_And_BottomUpOrientation()
        {
            // 32×32, 32bpp, uncompressed (type 2), bottom-up (desc 0x08 — like the real caveN.tga).
            // Marker: blue 0 (→ cell 0 = carve) at STORED row 0 col 0; everything else blue 0xE0
            // (→ (0xE0+0x20)>>6 = 4 = skip). Stored row 0 is the FILE-FIRST row = the bottom for a
            // bottom-up TGA, so it must land at DECODED row 31 (the mouth), not row 0.
            byte[] tga = BuildTga32((col, storedRow) => (storedRow == 0 && col == 0) ? (byte)0x00 : (byte)0xE0);
            var tpl = CaveTemplate.Decode(tga, 7);

            Assert.AreEqual(7, tpl.Index);
            Assert.AreEqual(SKIP, tpl.Cell(5, 5), "blue 0xE0 must decode to skip (4)");
            Assert.AreEqual(0, tpl.Cell(0, 31), "the stored bottom row maps to decoded row 31 (mouth)");
            Assert.AreEqual(SKIP, tpl.Cell(0, 0), "the decoded TOP row is not the file's first row (orientation flip)");
            Assert.AreEqual(SKIP, tpl.Cell(-1, 0), "out-of-stencil reads = skip");
            Assert.AreEqual(SKIP, tpl.Cell(0, 32), "out-of-stencil reads = skip");
        }

        // The real caveN.tga carve shapes: BODY (cnt 0..2, excl. the cnt-3 door lips) = Tiny 8×5 /
        // Small 12×5 / Medium 16×9 / Huge 12×12; full carve (incl. lips) 10/14/18/14 wide; the two
        // mouth rows (30, 31) carry cnt-3 lips at the outer edges (BOTTOM_MENU_RE.md §3).
        [Test]
        public void Decode_RealTemplates_MatchDocumentedShapes()
        {
            var tpls = LoadRealTemplatesOrIgnore();
            (int bodyW, int height, int fullW)[] exp = { (8, 5, 10), (12, 5, 14), (16, 9, 18), (12, 12, 14) };
            for (int i = 0; i < 4; i++)
            {
                var tpl = tpls[i];
                Assert.IsNotNull(tpl, $"cave{i}.tga failed to decode");
                var (c0, r0, c1, r1) = tpl!.CarveBounds();
                Assert.AreEqual(exp[i].fullW, c1 - c0 + 1, $"cave{i} full carve width (incl. door lips)");
                Assert.AreEqual(exp[i].height, r1 - r0 + 1, $"cave{i} carve height");

                int bc0 = SIZE, bc1 = -1;
                for (int r = 0; r < SIZE; r++)
                    for (int c = 0; c < SIZE; c++)
                    {
                        int v = tpl.Cell(c, r);
                        if (v != SKIP && v != 3) { if (c < bc0) bc0 = c; if (c > bc1) bc1 = c; }
                    }
                Assert.AreEqual(exp[i].bodyW, bc1 - bc0 + 1, $"cave{i} BODY width (excl. cnt-3 door lips)");

                Assert.AreEqual(3, tpl.Cell(c0, 31), $"cave{i} row 31 left edge must be a cnt-3 door lip");
                Assert.AreEqual(3, tpl.Cell(c1, 31), $"cave{i} row 31 right edge must be a cnt-3 door lip");
                Assert.AreEqual(SKIP, tpl.Cell(c0, r0 - 1 < 0 ? 0 : r0 - 1), $"cave{i} just above the carve is skip");
            }
        }

        // ---------------- stamp geometry (CaveStamp.Origin / SnapAnchor) ----------------

        [Test]
        public void Geometry_Origin_SnapAnchor_MouthOnAnchorRow()
        {
            Assert.AreEqual((64, 68), CaveStamp.Origin(80, 100), "origin = (ax-16, ay-32)");

            // SnapAnchor = the int cast of DigMarquee.SnapStencilAnchor (FUN_00567cb0:66/70).
            var (fx, fy) = DigMarquee.SnapStencilAnchor(83.2f, 97.6f);
            var (ax, ay) = CaveStamp.SnapAnchor(83.2f, 97.6f);
            Assert.AreEqual((int)fx, ax);
            Assert.AreEqual((int)fy, ay);

            // Template mouth row 31 maps to world tile ty = ay-1 (mouth sits on the anchor row).
            var (_, y0) = CaveStamp.Origin(ax, ay);
            Assert.AreEqual(ay - 1, y0 + 31, "buffer row 31 → the anchor row (mouth)");
        }

        // ---------------- stamp commit over the synthetic open-face BDB ----------------

        [Test]
        public void Commit_ValidSite_PaintsCaveNibbles_Record_And_BumpsId()
        {
            var tpl = LoadRealOrIgnore(0);   // cave0 (Tiny, 38 carve cells)
            const int ax = 80, ay = 100;
            var t = MakeCaveTerrain(tpl, ax, ay, carveBlock: 1);   // block 1 = open face

            Assert.IsTrue(CaveStamp.Validate(tpl, ax, ay, t, 0), "an open-face site must validate");
            int id0 = t.PlayerDigMarkId;

            int painted = CaveStamp.Commit(tpl, ax, ay, t, 0, out int mouthX, out int mouthY);
            Assert.AreEqual(38, painted, "cave0 stamps its 38 carve cells");
            Assert.AreEqual(id0 + 1, t.PlayerDigMarkId, "one mark id per stamp (FUN_00567cb0:229-236)");
            Assert.AreEqual(ay - 1, mouthY, "the dig seed sits on the mouth row");

            var (x0, y0) = CaveStamp.Origin(ax, ay);
            int checkedCells = 0;
            for (int row = 0; row < SIZE; row++)
                for (int col = 0; col < SIZE; col++)
                {
                    int v = tpl.Cell(col, row);
                    if (v == SKIP) continue;
                    int tx = x0 + col, ty = y0 + row, nib = t.RawDigNibble(tx, ty, 0);
                    Assert.AreEqual((v & 3) | 8, nib, $"carve cell ({col},{row}) → mode-0 nibble (cnt|8)");
                    Assert.AreNotEqual(0, nib & 8, "every cave cell carries the OPEN bit 8");
                    Assert.AreEqual(id0 & 0xF, t.DigMarkIdAt(tx, ty, 0), "all cells share the stamp's id");
                    Assert.IsTrue(t.IsDigMarked(tx, ty, 0), "cave nibbles feed the dispatcher dig funnel");
                    checkedCells++;
                }
            Assert.AreEqual(38, checkedCells);

            Assert.AreEqual(1, t.CaveRecords.Count, "one CaveRecord appended");
            var rec = t.CaveRecords[0];
            Assert.AreEqual(0, rec.TemplateIdx);
            Assert.AreEqual(id0 & 0xF, rec.MarkId, "record id = the cells' stroke id");
            var (bc0, br0, bc1, br1) = tpl.CarveBounds();
            Assert.AreEqual((x0 + bc0, y0 + br0, x0 + bc1, y0 + br1), (rec.X0, rec.Y0, rec.X1, rec.Y1),
                "record bbox = the painted carve bounds in world tiles");
        }

        [Test]
        public void Commit_InvalidCell_AbortsWholeStamp_AndPreviewShowsIt()
        {
            var tpl = LoadRealOrIgnore(0);
            const int ax = 80, ay = 100;
            var t = MakeCaveTerrain(tpl, ax, ay, carveBlock: 1);
            var (x0, y0) = CaveStamp.Origin(ax, ay);

            // Punch ONE mouth-row (row 31) carve cell back to solid rock (block 0, NW height 15) —
            // rule 3 (the mouth must sit on an exposed face, height < 12) then fails for it.
            int badCol = -1;
            for (int c = 0; c < SIZE; c++) if (tpl.Cell(c, 31) != SKIP) { badCol = c; break; }
            Assert.Greater(badCol, -1);
            int btx = x0 + badCol, bty = y0 + 31;
            t.BlockIdx[bty * t.Width + btx] = 0;   // solid granite in the mouth

            Assert.IsFalse(CaveStamp.Validate(tpl, ax, ay, t, 0), "the punched mouth cell invalidates the site");
            int id0 = t.PlayerDigMarkId;
            int painted = CaveStamp.Commit(tpl, ax, ay, t, 0, out _, out _);
            Assert.AreEqual(0, painted, "one invalid carve cell aborts the WHOLE stamp (all-or-nothing)");
            Assert.AreEqual(id0, t.PlayerDigMarkId, "an aborted stamp must not bump the id");
            Assert.AreEqual(0, t.CaveRecords.Count, "an aborted stamp appends no record");
            for (int row = 0; row < SIZE; row++)
                for (int col = 0; col < SIZE; col++)
                    if (tpl.Cell(col, row) != SKIP)
                        Assert.AreEqual(0, t.RawDigNibble(x0 + col, y0 + row, 0), "no cell may be painted on abort");

            // The PREVIEW is the same Validate with the per-cell verdict list — the bad cell shows
            // invalid while its neighbours stay valid (a per-cell verdict, not a global red).
            var cells = new List<(int tx, int ty, bool valid)>();
            CaveStamp.Validate(tpl, ax, ay, t, 0, cells);
            Assert.IsTrue(cells.Exists(c => c.tx == btx && c.ty == bty && !c.valid), "preview marks the punched cell invalid");
            Assert.IsTrue(cells.Exists(c => c.valid), "preview keeps the other cells valid");
        }

        [Test]
        public void Validate_RejectsBuriedGranite_AndAir()
        {
            var tpl = LoadRealOrIgnore(0);
            const int ax = 80, ay = 100;

            // Virgin rock everywhere (block 0, aux 15): the MOUTH rows fail rule 3 (aux ≥ 0xc —
            // no exposed face to start the dig from) → the whole stamp aborts.
            var granite = MakeCaveTerrain(tpl, ax, ay, carveBlock: 0);
            Assert.IsFalse(CaveStamp.Validate(tpl, ax, ay, granite, 0), "a mouth on unbroken rock rejects the stamp");
            Assert.AreEqual(0, CaveStamp.Commit(tpl, ax, ay, granite, 0, out _, out _));

            // AIR blocks (the flat-floor/open block 59, not in the synthetic BDB → aux reads 15):
            // the mouth rows fail rule 3 the same way.
            var air = MakeCaveTerrain(tpl, ax, ay, carveBlock: 59);
            Assert.IsFalse(CaveStamp.Validate(tpl, ax, ay, air, 0), "empty air rejects the stamp");
            Assert.AreEqual(0, CaveStamp.Commit(tpl, ax, ay, air, 0, out _, out _));
        }

        // M4 regression: the cave BODY buried in virgin rock is VALID —
        // FUN_00566a60 has NO exposure test on body cells (only the material type:132 and
        // aux ≥ target:150-164); the exposed-face demand (:137-148) applies to the two MOUTH
        // rows alone. The old TileExposed stand-in failed every buried body cell → the upper
        // part of the stamp ghost was ALWAYS red and no cave could be placed.
        [Test]
        public void Validate_AcceptsBuriedBody_WhenMouthSitsOnExposedFace()
        {
            var tpl = LoadRealOrIgnore(0);
            const int ax = 80, ay = 100;
            // Mouth rows (30, 31 → world ay−2, ay−1) = the open FACE block 1 (aux 11 < 0xc);
            // everything above = untouched virgin rock block 0 (aux 15).
            var t = MakeCaveTerrain(tpl, ax, ay, carveBlock: 0);
            var (x0, y0) = CaveStamp.Origin(ax, ay);
            for (int row = 30; row < SIZE; row++)
                for (int col = 0; col < SIZE; col++)
                    if (tpl.Cell(col, row) != SKIP)
                        t.BlockIdx[(y0 + row) * t.Width + (x0 + col)] = 1;

            var cells = new List<(int tx, int ty, bool valid)>();
            bool ok = CaveStamp.Validate(tpl, ax, ay, t, 0, cells);
            foreach (var c in cells)
                Assert.IsTrue(c.valid, $"carve cell ({c.tx},{c.ty}) must be valid — buried EARTH body cells pass FUN_00566a60");
            Assert.IsTrue(ok, "mouth on an exposed face + buried earth body = a placeable cave");
            Assert.Greater(CaveStamp.Commit(tpl, ax, ay, t, 0, out _, out int mouthY), 0);
            Assert.AreEqual(ay - 1, mouthY);
        }

        // M2 (cave side): the per-cell material law FUN_00566a60:132 — tile+0x18 & 3 ∈ {1,2}.
        // PMP terrain byte 4 = STONE (type 0) and 5 = fully OPEN (type 3) reject the cell (and
        // with it the whole all-or-nothing stamp); 3 = SAND (type 1) stays valid.
        [Test]
        public void Validate_MaterialType_RejectsStoneAndOpen_ByTerrainByte()
        {
            var tpl = LoadRealOrIgnore(0);
            const int ax = 80, ay = 100;

            // Baseline: the M4 fixture (exposed mouth + buried body, terrain bytes all 0 = earth).
            DiggableTerrain Fresh()
            {
                var t = MakeCaveTerrain(tpl, ax, ay, carveBlock: 0);
                var (bx0, by0) = CaveStamp.Origin(ax, ay);
                for (int row = 30; row < SIZE; row++)
                    for (int col = 0; col < SIZE; col++)
                        if (tpl.Cell(col, row) != SKIP)
                            t.BlockIdx[(by0 + row) * t.Width + (bx0 + col)] = 1;
                return t;
            }
            var (x0, y0) = CaveStamp.Origin(ax, ay);
            // Any body carve cell:
            int bcol = -1, brow = -1;
            for (int row = 0; row < SIZE && bcol < 0; row++)
                for (int col = 0; col < SIZE; col++)
                    if (tpl.Cell(col, row) != SKIP) { bcol = col; brow = row; break; }
            int btx = x0 + bcol, bty = y0 + brow;

            foreach (byte fileByte in new byte[] { 4, 5, 132, 133 })   // stone / open (+0x80 flag variants)
            {
                var t = Fresh();
                t.Terrain[bty * t.Width + btx] = fileByte;
                t.SyncStampedTile(btx, bty);
                Assert.IsFalse(t.TileMaterialDiggable(btx, bty), $"terrain byte {fileByte} must not be diggable");
                var cells = new List<(int tx, int ty, bool valid)>();
                Assert.IsFalse(CaveStamp.Validate(tpl, ax, ay, t, 0, cells), $"terrain byte {fileByte} must abort the stamp");
                Assert.IsTrue(cells.Exists(c => c.tx == btx && c.ty == bty && !c.valid), "the ghost marks exactly the bad cell");
                Assert.AreEqual(0, CaveStamp.Commit(tpl, ax, ay, t, 0, out _, out _), "all-or-nothing abort");
            }

            // SAND (file 3 → type 1) is inside the {1,2} band — still placeable.
            var sand = Fresh();
            sand.Terrain[bty * sand.Width + btx] = 3;
            sand.SyncStampedTile(btx, bty);
            Assert.IsTrue(CaveStamp.Validate(tpl, ax, ay, sand, 0), "sand (type 1) is diggable");
        }

        // ---------------- eraser: stroke window + cave flood (bare terrain) ----------------

        [Test]
        public void EraseStrokeWindow_ClearsPlainTunnel_KeepsCaves()
        {
            var t = MakeBareTerrain(20, 20);
            t.SetDigMark(5, 5, 0, 0, dummy: true);   // plain tunnel nibble 3
            t.SetDigMark(6, 5, 0, 0, dummy: true);
            t.SetDigMark(5, 6, 0, 0, dummy: true);
            t.SetDigMark(6, 6, 0, 0, dummy: false);  // a cave nibble (bit 8) inside the same window

            int cleared = t.EraseStrokeWindow(5, 5, 0);
            Assert.AreEqual(3, cleared, "the 2×2 window clears the 3 plain marks; the cave cell is skipped");
            Assert.AreEqual(0, t.RawDigNibble(5, 5, 0));
            Assert.AreEqual(0, t.RawDigNibble(6, 5, 0));
            Assert.AreEqual(0, t.RawDigNibble(5, 6, 0));
            Assert.AreEqual(8, t.RawDigNibble(6, 6, 0), "a bit-8 cave cell survives the stroke erase");
        }

        [Test]
        public void EraseCaveAt_FloodsSameIdCaveAndRecord_RespectsIdScoping()
        {
            var t = MakeBareTerrain(30, 10);

            // Cave A (mark id 0): tiles (10,5)(11,5)(12,5); its record.
            Assert.AreEqual(0, t.PlayerDigMarkId);
            for (int x = 10; x <= 12; x++) t.SetDigMark(x, 5, 0, 0, dummy: false);   // nibble 8, id 0
            t.AddCaveRecord(10, 5, 12, 5, templateIdx: 0, markId: t.PlayerDigMarkId & 0xF, owner: 0);
            t.IncPlayerDigMarkId();   // → 1

            // Cave B (mark id 1): tiles (13,5)(14,5) — 4-connected to A but a DIFFERENT stroke id.
            for (int x = 13; x <= 14; x++) t.SetDigMark(x, 5, 0, 0, dummy: false);   // nibble 8, id 1
            t.AddCaveRecord(13, 5, 14, 5, templateIdx: 1, markId: t.PlayerDigMarkId & 0xF, owner: 0);
            Assert.AreEqual(2, t.CaveRecords.Count);

            // Stroke-erase must NOT touch caves (bit-8 cells), and a non-cave click returns false.
            Assert.AreEqual(0, t.EraseStrokeWindow(10, 5, 0), "the eraser stroke skips cave (bit-8) cells");
            Assert.AreEqual(8, t.RawDigNibble(10, 5, 0));
            Assert.IsFalse(t.EraseCaveAt(0, 0, 0), "a click off any cave returns false (caller falls to stroke)");

            // A click on cave A floods away exactly cave A (same id) and drops its record; cave B stays.
            Assert.IsTrue(t.EraseCaveAt(11, 5, 0));
            for (int x = 10; x <= 12; x++) Assert.AreEqual(0, t.RawDigNibble(x, 5, 0), $"cave A cell {x} cleared");
            for (int x = 13; x <= 14; x++) Assert.AreEqual(8, t.RawDigNibble(x, 5, 0), $"cave B cell {x} (id 2) survives the id-scoped flood");
            Assert.AreEqual(1, t.CaveRecords.Count, "only cave A's record is removed");
            Assert.AreEqual(1, t.CaveRecords[0].TemplateIdx, "the surviving record is cave B (template 1)");
        }

        // ==================== fixtures ====================

        // A synthetic BDB: block 0 = solid rock (all aux 15), block 1 = an open FACE (all aux 11 —
        // NW corner 11 < 12 can host a mouth and ≥ the cnt-3 lip target 11), block 2 = the shared
        // "next level" TRANSITION TARGET. Blocks 0/1 carry real corner transitions → block 2
        // (gate bytes 11), so RimAtNode reads rim 11 on every placed tile — the painters' rim
        // re-mark gate (MarkRimGate, FUN_005671a0:32-45) then accepts all cave targets {0,3,7,11}
        // like virgin real-BDB rock. (The old byte-fill fixture left garbage transition indices —
        // rim reads were undefined.) Block 59 (AirBlock) is intentionally absent so it renders
        // "no geometry".
        static BdbModel MakeBdb()
        {
            var aux = new byte[3 * BdbModel.AuxStride];
            for (int i = 0; i < BdbModel.AuxStride; i++)
            {
                aux[i] = 15;                                  // block 0: solid
                aux[BdbModel.AuxStride + i] = 11;             // block 1: open face
                aux[2 * BdbModel.AuxStride + i] = 7;          // block 2: the next level down
            }
            for (int blk = 0; blk < 3; blk++)
            {
                int b = blk * BdbModel.AuxStride;
                for (int c = 0; c < 4; c++)
                {
                    // +0x40..0x4F: 4×u32 neighbour-transition indices (LE); blocks 0/1 step to
                    // block 2, block 2 is terminal (0xFFFFFFFF).
                    uint cand = blk == 2 ? 0xFFFFFFFFu : 2u;
                    for (int k = 0; k < 4; k++)
                        aux[b + 0x40 + c * 4 + k] = (byte)(cand >> (k * 8));
                    // +0x50..0x53: per-corner gate bytes — block 2's read back as the RIM (11).
                    aux[b + 0x50 + c] = 11;
                }
            }
            return new BdbModel { Header = new BdbHeader { NBlocks = 3 }, PerBlockAux = aux };
        }

        // A terrain whose cave-template carve tiles are set to `carveBlock` (1 = open face → valid,
        // 0 = solid granite, 59 = air) inside an otherwise solid (block 0) field.
        DiggableTerrain MakeCaveTerrain(CaveTemplate tpl, int ax, int ay, uint carveBlock)
        {
            const int w = 160, h = 160;
            var blockIdx = new uint[w * h];   // all 0 = solid
            var (x0, y0) = CaveStamp.Origin(ax, ay);
            for (int row = 0; row < SIZE; row++)
                for (int col = 0; col < SIZE; col++)
                    if (tpl.Cell(col, row) != SKIP)
                    {
                        int tx = x0 + col, ty = y0 + row;
                        if ((uint)tx < w && (uint)ty < h) blockIdx[ty * w + tx] = carveBlock;
                    }
            return MakeTerrain(w, h, blockIdx, MakeBdb());
        }

        DiggableTerrain MakeBareTerrain(int w, int h) => MakeTerrain(w, h, new uint[w * h], null);

        DiggableTerrain MakeTerrain(int w, int h, uint[] blockIdx, BdbModel? bdb)
        {
            var go = new GameObject("CaveStampHost");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h, BlockIdx = blockIdx, Terrain = new byte[w * h],
                HasTpl = new System.Collections.BitArray(w * h),
            }, bdb!, "", () => { });
            return t;
        }

        // A 32×32 32-bpp uncompressed bottom-up (desc 0x08) TGA; blueAt(col, storedRow) → the blue byte.
        static byte[] BuildTga32(Func<int, int, byte> blueAt)
        {
            var d = new byte[18 + SIZE * SIZE * 4];
            d[2] = 2;                       // uncompressed true-color
            d[12] = SIZE & 0xFF; d[13] = (byte)(SIZE >> 8);
            d[14] = SIZE & 0xFF; d[15] = (byte)(SIZE >> 8);
            d[16] = 32; d[17] = 0x08;       // 32 bpp, 8 alpha bits, bottom-up
            int p = 18;
            for (int r = 0; r < SIZE; r++)
                for (int c = 0; c < SIZE; c++) { d[p] = blueAt(c, r); d[p + 1] = 0; d[p + 2] = 0; d[p + 3] = 0xFF; p += 4; }
            return d;
        }

        static CaveTemplate?[] LoadRealTemplatesOrIgnore()
        {
            if (!Directory.Exists(Path.Combine(DataRoot, "templates")))
                Assert.Ignore($"real templates not present at {DataRoot}/templates — decode test needs them");
            return CaveTemplates.Load(DataRoot);
        }

        static CaveTemplate LoadRealOrIgnore(int idx)
        {
            string path = Path.Combine(DataRoot, "templates", $"cave{idx}.tga");
            if (!File.Exists(path)) Assert.Ignore($"cave{idx}.tga not present at {path}");
            return CaveTemplate.Decode(File.ReadAllBytes(path), idx);
        }
    }

    // Dig-marker validity + click laws — the pure seams:
    // • DiggableTerrain.TileMaterialType — the native tile+0x18 & 3 model (PMP loader
    // FUN_005992b0:150-216 rewrite +:242-377 probe/store; init FUN_00598f10:154);
    // • DigMarquee.CellValid with the material band as `exposed` (FUN_00566870:26-28);
    // • ClickToMoveService.TryFrontPlanePoint — the native mark pick un-projects the cursor
    // at the FRONT plane z = 15 (FUN_004796d0:177-181), NOT at the terrain surface.
    public sealed class DigMarkValidityTests
    {
        readonly List<GameObject> _gos = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gos) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _gos.Clear();
        }

        DiggableTerrain MakeBare(int w, int h)
        {
            var go = new GameObject("DigMarkValidityHost");
            _gos.Add(go);
            var t = go.AddComponent<DiggableTerrain>();
            t.Init(new BdbPmpWorldBuilder.TclWorldResult
            {
                Width = w, Height = h, BlockIdx = new uint[w * h], Terrain = new byte[w * h],
            }, null!, "", () => { });
            return t;
        }

        // FILE byte → type: 0/1 keep (map-create default 2), 2→2 earth, 3→1 sand, 4→0 STONE,
        // 5→3 OPEN; bit 0x80 is a flag and must not disturb the type (132/133 = 4/5 flagged).
        [Test]
        public void TileMaterialType_FileByteMapping()
        {
            var t = MakeBare(16, 16);
            // Terrain-byte writes after Init must re-derive the authoritative flag words
            // (native tile+0x18 is painted at load; the port mirrors that as a load-time
            // snapshot) — the same seam growth stamps use.
            void Set(int x, int y, byte v)
            {
                t.Terrain[y * t.Width + x] = v;
                t.SyncNativeMaterialBand(x, y, x, y);
            }
            // Isolated single-byte islands (spaced so the 2×2 probe can't cross-contaminate).
            (byte file, int type)[] cases = { (0, 2), (1, 2), (2, 2), (3, 1), (4, 0), (5, 3), (130, 2), (132, 0), (133, 3) };
            for (int i = 0; i < cases.Length; i++)
            {
                int x = 2 + (i % 4) * 3, y = 2 + (i / 4) * 3;
                Set(x, y, cases[i].file);
                Assert.AreEqual(cases[i].type, t.TileMaterialType(x, y),
                    $"file terrain byte {cases[i].file} → type {cases[i].type}");
            }
            Assert.AreEqual(0, t.TileMaterialType(-1, 0), "OOB reads stone-like");
            // The loader probe: a 0-byte tile borrows the first nonzero of below/right/below-right
            // (FUN_005992b0:293-304) — a hole LEFT of granite reads granite.
            var p = MakeBare(8, 8);
            p.Terrain[3 * 8 + 4] = 4;             // (4,3) stone
            p.SyncNativeMaterialBand(4, 3, 4, 3);
            Assert.AreEqual(0, p.TileMaterialType(3, 3), "0-byte tile probes its right neighbour");
            Assert.IsFalse(p.TileMaterialDiggable(3, 3));
            p.Terrain[6 * 8 + 3] = 3;             // below = sand
            p.Terrain[5 * 8 + 4] = 4;             // right = stone
            p.SyncNativeMaterialBand(3, 5, 4, 6);
            Assert.AreEqual(1, p.TileMaterialType(3, 5),
                "the native probe gives below precedence over right");
            Assert.IsTrue(p.TileMaterialDiggable(6, 6), "far 0-byte tile = default earth");
        }

        // M2/M3 (tunnel side): the FULL FUN_00566870 validity with the material band wired as
        // `exposed` — granite (file 4) and open/air (file 5) cells fail; earth passes. The
        // commit loop (CommitDigMarquee → CellAllowed per cell) runs the SAME predicate, so
        // failing cells are silently dropped on mouse-up like the native painter
        // (FUN_00566550:112-123 → FUN_005672d0:41-44 skips them, no abort).
        [Test]
        public void TunnelCellValid_MaterialBand_RejectsGraniteAndAir()
        {
            var t = MakeBare(32, 32);
            bool Solid(int x, int y) => t.NwCornerHeight(x, y) > 1;            // bdb-less → 15
            bool Open(int x, int y) => (t.RawDigNibble(x, y, 0) & 8) != 0;
            bool Exposed(int x, int y) => t.TileMaterialDiggable(x, y);

            // (5,5) is off the pillar lattice ((4/2)&(4/2)&1 == 0) and mid-world.
            Assert.IsTrue(DigMarquee.CellValid(5, 5, 32, 32, Solid, Open, Exposed), "earth cell valid");

            t.Terrain[5 * 32 + 5] = 4;   // granite
            t.SyncNativeMaterialBand(5, 5, 5, 5);
            Assert.IsFalse(DigMarquee.CellValid(5, 5, 32, 32, Solid, Open, Exposed), "granite cell invalid (M2)");

            t.Terrain[5 * 32 + 5] = 5;   // fully open / air
            t.SyncNativeMaterialBand(5, 5, 5, 5);
            Assert.IsFalse(DigMarquee.CellValid(5, 5, 32, 32, Solid, Open, Exposed), "air cell invalid (M3 — dropped at commit)");

            t.Terrain[5 * 32 + 5] = 3;   // sand
            t.SyncNativeMaterialBand(5, 5, 5, 5);
            Assert.IsTrue(DigMarquee.CellValid(5, 5, 32, 32, Solid, Open, Exposed), "sand cell valid");
        }

        // M1: the mark pick plane. The native un-projects the cursor at native z = 15
        // (FUN_004796d0:177-179) — the same plane the DigMark mesh renders on
        // (FUN_0056d0b0:544 / DigMarkOverlay.PlaneY) — so a click ON the visible marker
        // resolves to the marker's own tile, not the parallax-shifted terrain tile behind it.
        [Test]
        public void FrontPlanePoint_SolvesNativeZ15()
        {
            Assert.AreEqual(15f, ClickToMoveService.MarkPlaneZ, "pick plane = the DigMark render plane");

            var a = new Vector3(10f, 20f, 25f);
            var b = new Vector3(12f, 24f, 5f);     // z crosses 15 at t = 0.5
            Assert.IsTrue(ClickToMoveService.TryFrontPlanePoint(a, b, out var at));
            Assert.AreEqual(11f, at.x, 1e-4f);
            Assert.AreEqual(22f, at.y, 1e-4f);
            Assert.AreEqual(15f, at.z, 1e-4f);

            // Extrapolation beyond the segment is fine (the ray is a line).
            Assert.IsTrue(ClickToMoveService.TryFrontPlanePoint(new Vector3(0, 0, 16f), new Vector3(1, 0, 15.5f), out var far));
            Assert.AreEqual(2f, far.x, 1e-4f);

            // Parallel to the plane → no solution.
            Assert.IsFalse(ClickToMoveService.TryFrontPlanePoint(new Vector3(0, 0, 14f), new Vector3(1, 1, 14f), out _));
        }
    }
}
