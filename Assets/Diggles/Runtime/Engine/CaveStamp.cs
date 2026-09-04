#nullable enable

namespace Diggles.Runtime
{
    using System.Collections.Generic;
    using Diggles.Runtime.World;

    // The cave dig tools' template stamp — the faithful FUN_00566a60 (per-cell validity) +
    // FUN_00567cb0 (mouse-up commit) pair over DiggableTerrain (RE summary
    // Docs/BOTTOM_MENU_RE.md §3).
    //
    // Geometry: the anchor comes through DigMarquee.SnapStencilAnchor (x on the 2-tile, y on the
    // 4-tile lattice, FUN_005300f0:186-198); the stamp covers cols [ax−16, ax+16) × rows
    // [ay−32, ay) (FUN_00567cb0:70-72) — the mouth sits at the anchor row and the cave extends
    // up (ant-farm: smaller y). Template cell (col, row) → world tile (ax−16+col, ay−32+row).
    //
    // Per-cell validity (FUN_00566a60; cell value v ∈ 0..3 carves with the open-depth target
    // T = (v&3)!=0 ? (v&3)*4−1: 0 — {0,3,7,11}; v == 4 skips):
    // 1. world-rim bounds [min+1, max−1) per axis (:76-79);
    // 2. tile material type tile+0x18 & 3 ∈ {1,2} (:132) — DiggableTerrain.TileMaterialDiggable
    // (the faithful PMP terrain-byte model; rejects stone/granite 0 and fully-open 3). The
    // old "borders open space" (TileExposed) stand-in was wrong — it failed every carve
    // cell buried in virgin rock, so any cave body taller than its exposed mouth face
    // ghosted always-red. The native accepts fully buried earth body cells; only the mouth
    // rows demand an already-exposed face (rule 3);
    // 3. mouth rows (the two rows nearest the anchor, y ≥ ay−2,:137): the tile's block aux
    // byte +0x00 (NW sub-cell height, FUN_004093b0) must be < 0xc — the mouth must sit on
    // an already-exposed face — and the per-player explored flag set (:139-146; the port
    // has no fog-of-war → explored = true);
    // 4. current height: aux[+0x00] ≥ T (:150-164) — can't stamp where the ground is already
    // opened below the template's carve target;
    // 5. existing mark: the owner nibble's decoded target (FUN_00597430 inline,:166-178)
    // must be ≥ T — an existing deeper order blocks the stamp;
    // 6. anchor row only (y == ay−1,:186-211): the two tiles under the mouth cell,
    // (x−1, y+1) and (x, y+1), must have aux[+0x00] > 10 — solid ground to stand on;
    // 7. border (skipped for the cnt-3 door-lip cells, v == 3 →:214): wherever the adjacent
    // template cell is a skip (4), the world tile(s) on that side must be virgin solid —
    // aux[+0x00] == 0xf and the owner nibble == 0:
    // above skip → (x, y−1) (:215-239)
    // left skip → (x−1, y−1) + (x−1, y) (:240-281)
    // right skip → (x+1, y−1) + (x+1, y) (:283-324).
    // Any invalid carve cell aborts the whole stamp (all-or-nothing, FUN_00567cb0:77-82 —
    // error sound); the failure mask feeds the ghost preview (FUN_0056c530: mask 1 = carve
    // cell, |8 = failed → the red-orange DigMark pass).
    //
    // Commit (FUN_00567cb0): validate → abort on any failure; paint every carve cell through
    // the mode-0 painter (nibble (v|8) + the current stroke-id nibble, FUN_005671a0); append a
    // {painted bbox, template idx} cave record (:204-214) — the eraser's cave-click removal and
    // the future cave-skin pass consume it; per-owner mark-id counter ++ (:229-236).
    public static class CaveStamp
    {
        // The stamp's world origin: template cell (col,row) → tile (x0+col, y0+row).
        public static (int x0, int y0) Origin(int ax, int ay) => (ax - 16, ay - 32);

        // Snap a picked native position to the stamp anchor ints (the (int) casts of
        // FUN_00567cb0:66/70 over the SnapStencilAnchor floats).
        public static (int ax, int ay) SnapAnchor(float pickX, float pickY)
        {
            var (sx, sy) = DigMarquee.SnapStencilAnchor(pickX, pickY);
            return ((int)sx, (int)sy);
        }

        // FUN_00566a60 over the whole stamp. Returns true iff every carve cell passes; `cells`
        // (optional) receives every carve cell with its pass/fail result — the ghost preview's
        // field (mask 1 = valid, |8 = failed → red-orange).
        public static bool Validate(CaveTemplate tpl, int ax, int ay, DiggableTerrain t, int owner,
            List<(int tx, int ty, bool valid)>? cells = null)
        {
            var (x0, y0) = Origin(ax, ay);
            bool all = true;
            for (int row = 0; row < CaveTemplate.Size; row++)
                for (int col = 0; col < CaveTemplate.Size; col++)
                {
                    int v = tpl.Cell(col, row);
                    if (v == CaveTemplate.Skip) continue;
                    int tx = x0 + col, ty = y0 + row;
                    bool ok = CellValid(tpl, col, row, v, tx, ty, ay, t, owner);
                    all &= ok;
                    cells?.Add((tx, ty, ok));
                }
            return all;
        }

        static bool CellValid(CaveTemplate tpl, int col, int row, int v, int tx, int ty, int ay,
            DiggableTerrain t, int owner)
        {
            int target = (v & 3) != 0 ? (v & 3) * 4 - 1 : 0;
            // 1. world-rim bounds (:76-79).
            if (tx < 1 || tx >= t.Width - 1 || ty < 1 || ty >= t.Height - 1) return false;
            // 2. material type tile+0x18 & 3 ∈ {1,2} (:132) — not stone, not fully open.
            if (!t.TileMaterialDiggable(tx, ty)) return false;
            // 3. mouth rows must sit on an exposed face (:137-148).
            if (ty >= ay - 2 && t.NwCornerHeight(tx, ty) >= 0xc) return false;
            // 4. ground not already opened below the carve target (:150-164).
            if (t.NwCornerHeight(tx, ty) < target) return false;
            // 5. an existing deeper mark blocks the stamp (:166-178).
            if (DiggableTerrain.DecodeMarkTarget(t.RawDigNibble(tx, ty, owner)) < target) return false;
            // 6. the anchor row needs solid floor under the mouth (:186-211).
            if (ty == ay - 1 &&
                (t.NwCornerHeight(tx - 1, ty + 1) <= 10 || t.NwCornerHeight(tx, ty + 1) <= 10))
                return false;
            // 7. the cave border must be virgin solid rock (:214-324; lips v==3 skip this).
            if (v != 3)
            {
                if (tpl.Cell(col, row - 1) == CaveTemplate.Skip && !VirginSolid(t, tx, ty - 1, owner))
                    return false;
                if (tpl.Cell(col - 1, row) == CaveTemplate.Skip &&
                    (!VirginSolid(t, tx - 1, ty - 1, owner) || !VirginSolid(t, tx - 1, ty, owner)))
                    return false;
                if (tpl.Cell(col + 1, row) == CaveTemplate.Skip &&
                    (!VirginSolid(t, tx + 1, ty - 1, owner) || !VirginSolid(t, tx + 1, ty, owner)))
                    return false;
            }
            return true;
        }

        // aux[+0x00] == 0xf and no mark in the owner's nibble (FUN_00566a60:215-324).
        static bool VirginSolid(DiggableTerrain t, int tx, int ty, int owner)
            => t.NwCornerHeight(tx, ty) == 0xf && t.RawDigNibble(tx, ty, owner) == 0;

        // FUN_00567cb0's mode-3 commit: validate (all-or-nothing), paint (cnt|8) nibbles + the
        // stroke id, append the cave record, bump the mark-id counter. Returns the painted cell
        // count (0 = aborted) and the mouth-row cell nearest the anchor for the dig-task seed.
        public static int Commit(CaveTemplate tpl, int ax, int ay, DiggableTerrain t, int owner,
            out int mouthX, out int mouthY)
        {
            mouthX = ax; mouthY = ay - 1;
            if (!Validate(tpl, ax, ay, t, owner)) return 0;
            var (x0, y0) = Origin(ax, ay);
            int painted = 0, bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
            int bestMouth = int.MaxValue;
            for (int row = 0; row < CaveTemplate.Size; row++)
                for (int col = 0; col < CaveTemplate.Size; col++)
                {
                    int v = tpl.Cell(col, row);
                    if (v == CaveTemplate.Skip) continue;
                    int tx = x0 + col, ty = y0 + row;
                    // The record rect grows over every non-skip cell — whether or not the paint
                    // below lands. Native law: FUN_00567cb0:164-198 updates the min/max bbox
                    // unconditionally after calling the painter; the FUN_005671a0 rim gate only
                    // suppresses the nibble. The rect is the full template footprint (using a
                    // painted-only bbox instead: a cave stamped over already-dug space drops
                    // cells at the rim gate, the rect shrinks, and the half-open completion scan
                    // [X0,X1)×[Y0,Y1) of CaveSkinDigCheck can collapse onto — or entirely miss —
                    // the surviving marks, firing "complete" and growing the skins on the first
                    // swing).
                    if (tx < bx0) bx0 = tx;
                    if (ty < by0) by0 = ty;
                    if (tx > bx1) bx1 = tx;
                    if (ty > by1) by1 = ty;
                    // The mode-0 painter's rim re-mark gate (FUN_005671a0:32-45 — painter-side,
                    // not part of the FUN_00566a60 validity, so the drop is per-cell silent and
                    // never aborts the stamp): a cell whose node rim already sits below the
                    // painted nibble's target — e.g. a cnt-3 door lip (target 11) over an
                    // already-carved tunnel (rim ≤ 7) — simply doesn't paint; it is already open
                    // there, and the rest of the cave stamps around it. The deep body/mouth cells
                    // (targets 0/3/7) still paint over a carved corridor and deepen it.
                    // Law + case table: DiggableTerrain.MarkRimGate.
                    if (!t.MarkRimGate(tx, ty, (v & 3) | 8)) continue;
                    t.SetDigMark(tx, ty, owner, v);   // mode-0: nibble (v|8) + the current id
                    painted++;
                    // The dig seed: a mouth-row cell nearest the anchor column — the exposed
                    // front, so the progressive carve starts where the gnome can reach.
                    if (ty == ay - 1)
                    {
                        int d = (tx - ax) * (tx - ax);
                        if (d < bestMouth) { bestMouth = d; mouthX = tx; mouthY = ty; }
                    }
                }
            if (painted == 0) return 0;
            t.AddCaveRecord(bx0, by0, bx1, by1, tpl.Index, t.PlayerDigMarkId & 0xF, owner);
            t.IncPlayerDigMarkId();   // one id per stamp (FUN_00567cb0:229-236)
            return painted;
        }
    }
}
