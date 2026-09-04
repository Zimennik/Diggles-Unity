#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime;
    using NUnit.Framework;

    // the dig-order tool's constraints (DigMarquee), pinned against the RE'd
    // original (decompile):
    // • rect = [min, max+2) per axis (FUN_00566550:26-67) → minimum 2×2, you cannot mark a
    // 1-wide tunnel in either orientation;
    // • the PICK SNAP + CROSS-AXIS CLAMP (FUN_005680b0, applied by FUN_005300f0:146-167 to
    // every pick BEFORE it is stored): mouse-down anchor → the 4-tile lattice (tile 4k+1,
    // always a crossroads block), drag corner → the 2-tile lattice (tile 2j+1) with the
    // cross axis clamped to the anchor (dominant drag axis wins, ties → vertical, re-decided
    // every frame) — a tunnel is EXACTLY 2 across, only its length extends (user ground
    // truth from the original game, confirmed in the decompile);
    // • the dig LATTICE: per-tile gate (((y-1)/2) & ((x-1)/2) & 1) == 0 (FUN_00566870:23,
    // duplicated in the commit loop FUN_00566550:117) — odd/odd 2×2 blocks are permanent rock
    // pillars, so continuous tunnels only exist at tile coords ≡ 1,2 (mod 4) and parallel
    // tunnels always keep the 2-tile pillar band between them;
    // • the FULL per-cell validity FUN_00566870 (CellValid): rim bounds + pillar + not
    // open-marked (+0x08 bit 8, own & below) + exposure stand-in + the support row/flanks
    // still solid (neighbour blocks' aux[+0x00] > 1);
    // • parity with the original campaign data: tourn_2100.tcl:266/328 dig columns
    // {205,206}/{197,198} sit exactly on even lattice block columns 102/98, and the AI
    // (std_ai.tcl:2646/2712) only proposes tunnels at coords ≡ 1 (mod 4);
    // • the cave-stencil anchor snap (FUN_005300f0:186-198): x on a 2-tile, y on a 4-tile lattice.
    public sealed class DigToolTests
    {
        // ---- minimum size (FUN_00566550:60-67 — the "+2" exclusive ends) ----

        [Test]
        public void SnapRect_Click_IsAlways2x2()
        {
            // zero drag (anchor == corner) → the original still marks [x, x+2) × [y, y+2)
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(205, 31, 205, 31);
            Assert.AreEqual((205, 31, 206, 32), (x0, y0, x1, y1));
            Assert.AreEqual(2, x1 - x0 + 1);
            Assert.AreEqual(2, y1 - y0 + 1);
        }

        [Test]
        public void SnapRect_HorizontalDrag_IsAtLeast2WideAndExactly2Tall()
        {
            // horizontal drag 10→15, no y movement: each side = dragged span + 2
            // (FUN_00566550's [min, max+2)) → 7 wide × 2 tall
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(10, 8, 15, 8);
            Assert.AreEqual(10, x0);
            Assert.AreEqual(16, x1);           // max(10,15)+1 inclusive == [10, 17) exclusive
            Assert.AreEqual(2, y1 - y0 + 1);   // the short side is 2, never 1
            Assert.GreaterOrEqual(x1 - x0 + 1, 2);
        }

        [Test]
        public void SnapRect_VerticalDrag_IsExactly2WideAndAtLeast2Tall()
        {
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(40, 12, 40, 30);
            Assert.AreEqual(2, x1 - x0 + 1);
            Assert.AreEqual((12, 31), (y0, y1));
        }

        [Test]
        public void SnapRect_CornerOrderIsIrrelevant()
        {
            Assert.AreEqual(DigMarquee.SnapRect(3, 9, 7, 4), DigMarquee.SnapRect(7, 4, 3, 9));
        }

        // ---- the lattice (FUN_00566870:23) ----

        [Test]
        public void Lattice_PillarIffBothBlockIndicesOdd()
        {
            for (int ty = 1; ty < 64; ty++)
                for (int tx = 1; tx < 64; tx++)
                {
                    bool expected = (((tx - 1) / 2) % 2 == 1) && (((ty - 1) / 2) % 2 == 1);
                    Assert.AreEqual(expected, DigMarquee.IsPillar(tx, ty), $"tile ({tx},{ty})");
                }
        }

        [Test]
        public void Lattice_PillarTilesAre3and4Mod4_BothAxes()
        {
            // pillar blocks cover tiles {4j+3, 4j+4} on both axes (block b = (t-1)/2 odd)
            Assert.IsTrue(DigMarquee.IsPillar(3, 3));
            Assert.IsTrue(DigMarquee.IsPillar(4, 4));
            Assert.IsTrue(DigMarquee.IsPillar(3, 4));
            Assert.IsTrue(DigMarquee.IsPillar(7, 11));   // (7-1)/2=3 odd, (11-1)/2=5 odd
            // any even block index on either axis → markable
            Assert.IsFalse(DigMarquee.IsPillar(1, 3));   // bx=0 even
            Assert.IsFalse(DigMarquee.IsPillar(3, 1));   // by=0 even
            Assert.IsFalse(DigMarquee.IsPillar(5, 5));   // bx=by=2 even
        }

        [Test]
        public void Lattice_ContinuousVerticalTunnel_OnlyOnEvenBlockColumns()
        {
            // a 2-wide tunnel on block column b covers tiles {2b+1, 2b+2}; it is continuous down
            // the map iff b is even — on odd b every odd block ROW is a pillar (player-visible
            // "tunnels snap to a grid")
            for (int b = 0; b < 16; b++)
            {
                bool broken = false;
                for (int ty = 1; ty < 40; ty++)
                    if (DigMarquee.IsPillar(2 * b + 1, ty) || DigMarquee.IsPillar(2 * b + 2, ty))
                        broken = true;
                Assert.AreEqual(b % 2 == 1, broken, $"block column {b}");
            }
        }

        [Test]
        public void Lattice_ContinuousHorizontalTunnel_OnlyOnEvenBlockRows()
        {
            for (int b = 0; b < 16; b++)
            {
                bool broken = false;
                for (int tx = 1; tx < 40; tx++)
                    if (DigMarquee.IsPillar(tx, 2 * b + 1) || DigMarquee.IsPillar(tx, 2 * b + 2))
                        broken = true;
                Assert.AreEqual(b % 2 == 1, broken, $"block row {b}");
            }
        }

        [Test]
        public void Lattice_ParallelTunnels_Keep2RockTilesBetween()
        {
            // consecutive tunnel-capable column pairs: {4j+1, 4j+2} and {4j+5, 4j+6} — exactly the
            // 2-tile band {4j+3, 4j+4} between them, and that band IS pillar rock at odd block rows
            for (int j = 0; j < 8; j++)
            {
                int gap0 = 4 * j + 3, gap1 = 4 * j + 4;
                Assert.AreEqual(2, (4 * j + 5) - (4 * j + 2) - 1, "tile gap between parallel tunnels");
                Assert.IsTrue(DigMarquee.IsPillar(gap0, 3) && DigMarquee.IsPillar(gap1, 3),
                    $"the {gap0}/{gap1} band must contain undiggable pillars");
            }
        }

        [Test]
        public void Lattice_OnlyGatesTheStandardTunnel()
        {
            // mode-0 depth marks (FUN_005671a0) have NO lattice gate in the original
            Assert.IsFalse(DigMarquee.CellMarkable(3, 3, standardTunnel: true));
            Assert.IsTrue(DigMarquee.CellMarkable(3, 3, standardTunnel: false));
            Assert.IsTrue(DigMarquee.CellMarkable(5, 5, standardTunnel: true));
        }

        // ---- parity with the original campaign data ----

        [Test]
        public void Tourn2100_TunnelColumns_SitOnTheLattice()
        {
            // tourn_2100.tcl:266 for {set i 205} {$i < 207}... {set j 31} {$j < 42} → {205,206}×{31..41}
            // tourn_2100.tcl:328 for {set i 197} {$i < 199}... {set j 31} {$j < 42} → {197,198}×{31..41}
            foreach (int x in new[] { 205, 206, 197, 198 })
                for (int y = 31; y < 42; y++)
                    Assert.IsFalse(DigMarquee.IsPillar(x, y), $"script tile ({x},{y}) must be markable");
            // both 2-wide shafts are exactly ONE even lattice block column (102 and 98)
            Assert.AreEqual((205 - 1) / 2, (206 - 1) / 2);
            Assert.AreEqual(0, ((205 - 1) / 2) % 2);
            Assert.AreEqual((197 - 1) / 2, (198 - 1) / 2);
            Assert.AreEqual(0, ((197 - 1) / 2) % 2);
            // shifted off the lattice by one, the same shaft WOULD hit pillars (col 207 = block 103, odd)
            bool offLatticeBroken = false;
            for (int y = 31; y < 42; y++) offLatticeBroken |= DigMarquee.IsPillar(207, y);
            Assert.IsTrue(offLatticeBroken);
        }

        [Test]
        public void Tourn2100_DragReproducesTheScriptRect()
        {
            // dragging the marquee from (205,31) to (205,40) yields exactly the script's
            // {205,206}×{31..41} thanks to the +2 ends — and no cell of it is lattice-skipped
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(205, 31, 205, 40);
            Assert.AreEqual((205, 31, 206, 41), (x0, y0, x1, y1));
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                    Assert.IsTrue(DigMarquee.CellMarkable(tx, ty, standardTunnel: true), $"({tx},{ty})");
        }

        [Test]
        public void StdAi_LatticeRule_CoordMod4Eq1_IsPillarFreeColumn()
        {
            // std_ai.tcl:2646/2712 only proposes tunnels where the tile coord ≡ 1 (mod 4) —
            // exactly the columns/rows our predicate leaves continuous
            for (int t = 1; t < 64; t += 4)
            {
                for (int o = 1; o < 40; o++)
                {
                    Assert.IsFalse(DigMarquee.IsPillar(t, o), $"AI column {t}");
                    Assert.IsFalse(DigMarquee.IsPillar(t + 1, o), $"AI column {t + 1}");
                    Assert.IsFalse(DigMarquee.IsPillar(o, t), $"AI row {t}");
                    Assert.IsFalse(DigMarquee.IsPillar(o, t + 1), $"AI row {t + 1}");
                }
            }
        }

        // ---- the pick snap + cross-axis clamp (FUN_005680b0) ----

        [Test]
        public void Anchor_SnapsBothAxesTo4LatticeTile4kPlus1()
        {
            // x' = (int)(4*floor((x+0.5)/4) + 1.5) = 4k+1 (FUN_005680b0:19-24, mode 0)
            Assert.AreEqual((205, 29), DigMarquee.SnapAnchor(205f, 31f));
            // the whole 4-tile band {4k..4k+3} maps to the same anchor 4k+1
            foreach (float px in new[] { 204f, 205f, 206f, 207f, 204.6f, 207.4f })
                Assert.AreEqual(205, DigMarquee.SnapAnchor(px, 9f).ax, $"pick x={px}");
            Assert.AreEqual(209, DigMarquee.SnapAnchor(208f, 9f).ax);   // next band
        }

        [Test]
        public void Anchor_IsAlwaysACrossroadsBlock_NeverAPillar()
        {
            // anchor tile 4k+1 → block 2k even on BOTH axes → the 2×2 it spans is pillar-free
            for (float px = 1f; px < 60f; px += 1.7f)
                for (float py = 1f; py < 60f; py += 2.3f)
                {
                    var (ax, ay) = DigMarquee.SnapAnchor(px, py);
                    Assert.AreEqual(0, (ax - 1) % 4, $"ax={ax} not 4k+1");
                    Assert.AreEqual(0, (ay - 1) % 4, $"ay={ay} not 4k+1");
                    Assert.AreEqual(0, ((ax - 1) / 2) % 2, $"anchor block x odd at pick ({px},{py})");
                    Assert.AreEqual(0, ((ay - 1) / 2) % 2, $"anchor block y odd at pick ({px},{py})");
                    Assert.IsFalse(DigMarquee.IsPillar(ax, ay) || DigMarquee.IsPillar(ax + 1, ay) ||
                                   DigMarquee.IsPillar(ax, ay + 1) || DigMarquee.IsPillar(ax + 1, ay + 1));
                }
        }

        [Test]
        public void HorizontalDrag_ClampsHeightTo2_RegardlessOfVerticalTravel()
        {
            // anchor (9,9) is a crossroads block; the pick wanders 6 tiles down but 10 right —
            // x dominates → the corner's Y is CLAMPED to the anchor row (FUN_005680b0:54-59)
            var (bx, by) = DigMarquee.SnapDragCorner(9, 9, 20f, 15f);
            Assert.AreEqual((19, 9), (bx, by));            // x 2-snapped to 2j+1, y = anchor
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(9, 9, bx, by);
            Assert.AreEqual(2, y1 - y0 + 1, "horizontal tunnel must be exactly 2 tall");
            Assert.AreEqual(12, x1 - x0 + 1);              // length free (2-step granular)
        }

        [Test]
        public void VerticalDrag_ClampsWidthTo2_RegardlessOfHorizontalTravel()
        {
            var (bx, by) = DigMarquee.SnapDragCorner(9, 9, 12f, 30f);
            Assert.AreEqual((9, 29), (bx, by));            // y 2-snapped, x = anchor
            var (x0, y0, x1, y1) = DigMarquee.SnapRect(9, 9, bx, by);
            Assert.AreEqual(2, x1 - x0 + 1, "vertical tunnel must be exactly 2 wide");
            Assert.AreEqual(22, y1 - y0 + 1);
        }

        [Test]
        public void DominantAxis_SwitchesMidDrag_AndTiesGoVertical()
        {
            // the clamp is re-derived from the LIVE pick each frame (FUN_005300f0 calls
            // FUN_005680b0 on every drag update) — wandering the mouse flips the orientation
            Assert.AreEqual((19, 9), DigMarquee.SnapDragCorner(9, 9, 20f, 12f));   // x dominates
            Assert.AreEqual((9, 19), DigMarquee.SnapDragCorner(9, 9, 12f, 20f));   // now y does
            // exact tie |Δx| == |Δy| → the <= branch → VERTICAL (FUN_005680b0:55-59)
            Assert.AreEqual((9, 15), DigMarquee.SnapDragCorner(9, 9, 15f, 15f));
            // zero drag → vertical with zero extent → the 2×2 crossroads block on commit
            Assert.AreEqual((9, 9), DigMarquee.SnapDragCorner(9, 9, 9f, 9f));
        }

        [Test]
        public void CorridorAnchors_AreDirectionLocked()
        {
            // unreachable through SnapAnchor (kept faithful to FUN_005680b0:61-63): an anchor in
            // a vertical-corridor block (block x even, y odd) extends ONLY vertically even when
            // the drag is horizontal-dominant — and vice versa. The LOCK pins the corner's cross
            // axis to the ANCHOR's coordinate: FUN_005680b0:61 `(uVar6 & 1) == 0` (block x even)
            // → goto with fVar3 = anchorX (vertical, x stays ax); else:61-63 sets fVar3 = snapX
            // and, block y even, falls through to fVar8 = fVar4 = anchorY (horizontal, y stays ay).
            Assert.AreEqual(5, DigMarquee.SnapDragCorner(5, 7, 30f, 8f).bx, "x must stay locked to ax");
            Assert.AreEqual(5, DigMarquee.SnapDragCorner(7, 5, 8f, 30f).by, "y must stay locked to ay");
        }

        [Test]
        public void Pipeline_CrossAxisIsAlwaysExactly2_AndRectIsPillarFree()
        {
            // full UI pipeline over a spread of picks: anchor snap → drag snap/clamp → grown
            // rect. The cross axis is exactly 2 and a standard rect can never contain a pillar
            // (its cross block index is even).
            for (float px = 2f; px < 40f; px += 3.1f)
                for (float py = 2f; py < 40f; py += 2.7f)
                    for (float qx = 2f; qx < 40f; qx += 5.3f)
                        for (float qy = 2f; qy < 40f; qy += 4.9f)
                        {
                            var (ax, ay) = DigMarquee.SnapAnchor(px, py);
                            var (bx, by) = DigMarquee.SnapDragCorner(ax, ay, qx, qy);
                            var (x0, y0, x1, y1) = DigMarquee.SnapRect(ax, ay, bx, by);
                            int w = x1 - x0 + 1, h = y1 - y0 + 1;
                            Assert.AreEqual(2, System.Math.Min(w, h),
                                $"cross axis must be 2 (pick ({px},{py})→({qx},{qy}) rect {w}×{h})");
                            Assert.IsTrue(w == 2 || h == 2);
                            Assert.AreEqual(0, (w - 2) % 2, "length grows in 2-tile steps");
                            Assert.AreEqual(0, (h - 2) % 2, "length grows in 2-tile steps");
                            bool pillar = false;
                            for (int ty = y0; ty <= y1 && !pillar; ty++)
                                for (int tx = x0; tx <= x1 && !pillar; tx++)
                                    pillar = DigMarquee.IsPillar(tx, ty);
                            Assert.IsFalse(pillar,
                                $"snapped standard rect ({x0},{y0})..({x1},{y1}) contains a pillar");
                        }
        }

        // ---- the full per-cell validity (FUN_00566870 → CellValid) ----

        static bool Valid(int tx, int ty, int w = 64, int h = 64,
            System.Func<int, int, bool>? solid = null,
            System.Func<int, int, bool>? open = null,
            System.Func<int, int, bool>? exposed = null)
            => DigMarquee.CellValid(tx, ty, w, h,
                solid ?? ((x, y) => true), open ?? ((x, y) => false), exposed ?? ((x, y) => true));

        [Test]
        public void CellValid_SolidWorld_NonPillarInteriorCellIsValid()
        {
            Assert.IsTrue(Valid(5, 5));
            Assert.IsTrue(Valid(1, 1));          // first interior cell
            Assert.IsTrue(Valid(62, 62));        // last interior cell (< max-1 with max=64)
        }

        [Test]
        public void CellValid_RimAndPillarAreInvalid()
        {
            // world-rim bounds [min+1, max-1) (FUN_00566870:19-22)
            Assert.IsFalse(Valid(0, 5));
            Assert.IsFalse(Valid(5, 0));
            Assert.IsFalse(Valid(63, 5));
            Assert.IsFalse(Valid(5, 63));
            // the lattice pillar (:23)
            Assert.IsFalse(Valid(3, 3));
            Assert.IsFalse(Valid(7, 11));
        }

        [Test]
        public void CellValid_OpenMarkBlocksOwnCellAndSupportRow()
        {
            // the player's +0x08 nibble bit 8 on the cell itself (:25) or the cell BELOW (:33-34)
            Assert.IsFalse(Valid(5, 5, open: (x, y) => x == 5 && y == 5));
            Assert.IsFalse(Valid(5, 5, open: (x, y) => x == 5 && y == 6));
            Assert.IsTrue(Valid(5, 5, open: (x, y) => x == 5 && y == 4));   // ABOVE doesn't gate
        }

        [Test]
        public void CellValid_CarvedNeighboursBlockTheMark()
        {
            // support row + flanks must still be solid (aux[+0x00] > 1,:35-50): below,
            // right, left, below-right, below-left — ant-farm y+1 = down
            foreach (var (nx, ny) in new[] { (5, 6), (6, 5), (4, 5), (6, 6), (4, 6) })
                Assert.IsFalse(Valid(5, 5, solid: (x, y) => !(x == nx && y == ny)),
                    $"carved neighbour ({nx},{ny}) must invalidate (5,5)");
            // the row ABOVE is irrelevant to validity
            foreach (var (nx, ny) in new[] { (5, 4), (4, 4), (6, 4) })
                Assert.IsTrue(Valid(5, 5, solid: (x, y) => !(x == nx && y == ny)),
                    $"carved cell ({nx},{ny}) above must NOT invalidate (5,5)");
        }

        [Test]
        public void CellValid_ExposureStandInGates()
        {
            Assert.IsFalse(Valid(5, 5, exposed: (x, y) => false));
        }

        [Test]
        public void Tourn2100_AnchorPipeline_LandsOnTheScriptColumns()
        {
            // the script's vertical shafts use columns {205,206} — exactly the pair a vertical
            // UI drag anchored at x=205 (the 4-lattice tile of block column 102) produces
            var (ax, _) = DigMarquee.SnapAnchor(205.3f, 31f);
            Assert.AreEqual(205, ax);
            var (bx, by) = DigMarquee.SnapDragCorner(205, 29, 205.7f, 41.2f);
            Assert.AreEqual(205, bx, "vertical shaft keeps the anchor column");
            var (x0, _, x1, _) = DigMarquee.SnapRect(205, 29, bx, by);
            Assert.AreEqual((205, 206), (x0, x1));
        }

        // ---- the cave-stencil anchor snap (FUN_005300f0:186-198) ----

        [Test]
        public void StencilAnchor_SnapsXToStep2_YToStep4()
        {
            // x' = 2*floor((x-0.5)/2) + 2.5; y' = 4*floor((y+0.5)/4) + 3.5
            var (sx, sy) = DigMarquee.SnapStencilAnchor(10f, 20f);
            Assert.AreEqual(10.5f, sx, 1e-4f);   // 2*floor(9.5/2)+2.5 = 2*4+2.5
            Assert.AreEqual(23.5f, sy, 1e-4f);   // 4*floor(20.5/4)+3.5 = 4*5+3.5
            // invariant inside a step (x ∈ [8.5,10.5) shares floor((x-0.5)/2) = 4),
            // jumps exactly one step across the boundary
            Assert.AreEqual(sx, DigMarquee.SnapStencilAnchor(10.4f, 20f).x, 1e-4f);
            Assert.AreEqual(sx + 2f, DigMarquee.SnapStencilAnchor(10.6f, 20f).x, 1e-4f);
            Assert.AreEqual(sy, DigMarquee.SnapStencilAnchor(10f, 22.9f).y, 1e-4f);
            Assert.AreEqual(sy + 4f, DigMarquee.SnapStencilAnchor(10f, 23.6f).y, 1e-4f);
        }
    }
}
