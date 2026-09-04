#nullable enable

namespace Diggles.Runtime
{
    using System;

    // The original drag dig tool's constraint math, extracted pure so EditMode tests
    // can pin it without a scene (consumed by ClickToMoveService's marquee).
    //
    // The standard-tunnel (tool 0) rule set, from the decompile:
    //
    // • Every pick is pre-adjusted by FUN_005680b0 before it reaches the marquee state —
    // FUN_005300f0:146-167 routes mouse-down/drag/up through it; FUN_005664f0 (down) and
    // FUN_00566830 (drag) then store the adjusted floats into +0xe4/+0xe8 (anchor) and
    // +0xf0/+0xf4 (corner).
    //
    // • Anchor snap (FUN_005680b0:19-24, tool mode +0xe0 == 0 at mouse-down): both axes go to
    // the 4-tile lattice, x' = 4*floor((x+0.5)/4) + 1.5 (consts 0.5 @0073708c, 0.25 @00737058,
    // 4.0 @007374a0, 1.5 @007371c0) — tile 4k+1, so the anchor block (t-1)/2 = 2k is always
    // even/even, a free crossroads block of the dig lattice. (:25-43 holds a pillar-rescue
    // re-snap of one axis to the 2-lattice; unreachable for 4k+1 anchors — not ported.)
    //
    // • Drag snap + cross-axis clamp (FUN_005680b0:45-64, mode != 0): the moving corner first
    // snaps per-axis to the 2-tile lattice x' = 2*floor((x-0.5)/2) + 1.5 (tile 2j+1), then
    // the cross axis is clamped to the anchor by the anchor block's parity:
    // – even/even (always true after the anchor snap): the dominant axis wins — vertical
    // when |snapX−anchorX| <= |snapY−anchorY| → corner = (anchorX, snapY), else
    // horizontal → corner = (snapX, anchorY) (:54-59). Re-derived from the live pick
    // every frame, so the orientation flips mid-drag when the other axis takes over.
    // – block x even / y odd (vertical corridor anchor): always vertical; x odd / y even:
    // always horizontal; odd/odd (pillar anchor): unclamped (:61-63 — defensive paths,
    // unreachable through the anchor snap).
    // Consequently a horizontal tunnel is exactly 2 tiles tall and a vertical one exactly
    // 2 tiles wide — only the drag axis extends, in 2-tile steps. Fat rectangles cannot be
    // ordered; the lattice gates below are belt-and-braces.
    //
    // • Commit rect (FUN_00566550): corners → ints, rect [minX, maxX+2) × [minY, maxY+2)
    // (min/max at:26-43, the "+2" exclusive ends at:60-67), clamped to [world.min,
    // world.max-1) (:44-75). A zero-drag click therefore marks the anchor's 2×2 crossroads
    // block. The preview FUN_0056d0b0:95-96 builds the identical grown rect while dragging.
    //
    // • The dig lattice — the commit loop (FUN_00566550:117), the validity check
    // (FUN_00566870:23) and the preview greying all gate per tile on
    // (((y-1)/2) & ((x-1)/2) & 1) == 0
    // i.e. 2×2 blocks b=(t-1)/2 whose x and y indices are both odd are permanent rock
    // pillars. Parallel tunnels therefore live on the 4-tile lattice with exactly a 2-tile
    // rock band between them. Cross-checked: tourn_2100.tcl:266/328 digs columns {205,206}
    // and {197,198} = even block columns 102/98; std_ai.tcl:2646/2712 only proposes tunnels
    // at tile coords ≡ 1 (mod 4). Under the anchor/corner snaps a standard rect can never
    // contain a pillar (its cross block index is even) — the gate matters for the
    // eraser/stencil paths and as defense.
    //
    // • Per-cell validity (FUN_00566870 — called by the painter FUN_005672d0:56 and per cell
    // by the preview FUN_0056d0b0:25, which greys failures) — CellValid below:
    // 1. world-rim bounds [min+1, max-1) per axis (:19-22);
    // 2. not a lattice pillar (:23);
    // 3. the player's tile+0x08 nibble must not carry the open bit 8 (:25);
    // 4. material type: tile+0x18 & 3 ∈ {1,2} (not stone/granite 0, not fully open 3)
    // or the per-player fog flag is off (:26-28) — the port wires
    // DiggableTerrain.TileMaterialDiggable (the faithful PMP terrain-byte model, see
    // TileMaterialType);
    // 5. the cell below (y+1 — ant-farm: +y = down) must not be open-marked (:33-34), and
    // the block aux byte +0x00 (the 8×8 heightmap's NW corner; FUN_004093b0 = base +
    // idx*0x59) of below / right / left / below-right / below-left must be > 1 — still
    // solid (:35-50; (x,y+1) is checked twice,:33 and:42). This forbids floorless
    // tunnels (support row already carved) and marking into existing hollows.
    // (:30-31's early-true when y+1 >= world.max is unreachable under rule 1.)
    // The painter FUN_005672d0:40-53 additionally gates (when the per-player tile+0x34 byte
    // is set) on material ∈ {1,2} and on the FUN_005968b0 node rim >= the FUN_00597430
    // nibble-decoded threshold — modelled as DiggableTerrain.MarkRimGate (drops re-marks
    // over already-carved cells; wired in ClickToMoveService.CellAllowed and the
    // CaveStamp.Commit paint loop).
    //
    // • The depth tools' mode-0 painter FUN_005671a0 has no lattice/neighbour validity (bounds
    //:20-23 + the same +0x34 exposure/rim block:32-45 only) — in the original those tools
    // stamp the 32×32 cave stencils, whose placement is snapped instead (SnapStencilAnchor),
    // and tool 5 is the mark eraser (FUN_005664f0(param_3=1) → mode 2 → FUN_00567410).
    //
    // • World clamps not modelled here (the caller's bounds/validity checks stand in):
    // FUN_00566550:44-75 clamps the rect to [world.min, world.max-1) and FUN_005672d0:22-31
    // rejects the 1-tile world rim.
    //
    // Coordinate convention: the original's float world truncates to tiles (tile t spans
    // [t, t+1)); our native coords round (tile t spans [t-0.5, t+0.5)). The formulas are
    // applied verbatim to our native floats — the tile→lattice mapping stays aligned (picks in
    // the 4-tile band {4k..4k+3} anchor at 4k+1; picks in the 2-block {2j+1, 2j+2} corner at
    // 2j+1), differing from the original only at half-tile pick edges.
    public static class DigMarquee
    {
        // The marquee commit rect (FUN_00566550:26-67): both corner tiles in any order →
        // inclusive [x0..x1] × [y0..y1] = the original's [min, max+2) exclusive ends.
        // Each side = dragged span + 2, so the minimum is 2×2. Fed snapped corners
        // (SnapAnchor/SnapDragCorner) the cross axis is exactly 2 and the drag axis grows
        // in 2-tile steps — the original's only-tunnels-no-rects behavior.
        public static (int x0, int y0, int x1, int y1) SnapRect(int ax, int ay, int bx, int by)
            => (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx) + 1, Math.Max(ay, by) + 1);

        // The mouse-down anchor snap (FUN_005680b0:19-24, mode 0 — FUN_005300f0 adjusts the
        // pick through this before FUN_005664f0 stores it): both axes to the 4-tile lattice,
        // tile = (int)(4*floor((p+0.5)/4) + 1.5) = 4k+1. The anchor block ((t-1)/2 = 2k even)
        // is always a free crossroads of the dig lattice.
        public static (int ax, int ay) SnapAnchor(float pickX, float pickY)
            => (4 * (int)Math.Floor((pickX + 0.5f) * 0.25f) + 1,
                4 * (int)Math.Floor((pickY + 0.5f) * 0.25f) + 1);

        // The per-frame drag corner snap + cross-axis clamp (FUN_005680b0:45-64, mode != 0):
        // snap each axis to the 2-tile lattice (tile 2j+1), then clamp the cross axis to the
        // anchor by the anchor block's parity. Crossroads anchor (the only kind SnapAnchor
        // produces): dominant drag axis wins, ties → vertical (:54-59); corridor anchors are
        // direction-locked; the odd/odd case passes through unclamped (:61-63, defensive).
        // Call every frame with the live pick — mid-drag orientation switching is original.
        public static (int bx, int by) SnapDragCorner(int ax, int ay, float pickX, float pickY)
        {
            int sx = 2 * (int)Math.Floor((pickX - 0.5f) * 0.5f) + 1;
            int sy = 2 * (int)Math.Floor((pickY - 0.5f) * 0.5f) + 1;
            // MSVC truncating '/' == C# '/' (in-game coords are >= 1; the rim is rejected upstream).
            bool blkXOdd = (((ax - 1) / 2) & 1) != 0;
            bool blkYOdd = (((ay - 1) / 2) & 1) != 0;
            if (!blkXOdd && !blkYOdd)   // crossroads: |Δx| <= |Δy| → vertical (the float compare
                                        // is exact in ints: lattice centers differ by integers)
                return Math.Abs(sx - ax) <= Math.Abs(sy - ay) ? (ax, sy) : (sx, ay);
            if (!blkXOdd) return (ax, sy);   // vertical corridor anchor → x locked
            if (!blkYOdd) return (sx, ay);   // horizontal corridor anchor → y locked
            return (sx, sy);                 // pillar anchor — original leaves it unclamped
        }

        // The lattice pillar predicate (FUN_00566870:23 == FUN_00566550:117):
        // tile (tx,ty) lies in a 2×2 rock pillar block iff both block indices (t-1)/2 are odd.
        // C# `/` truncates toward zero exactly like the original's MSVC `/` (in-game coords are
        // ≥1 anyway — the world rim is rejected upstream).
        public static bool IsPillar(int tx, int ty)
            => ((((tx - 1) / 2) & ((ty - 1) / 2)) & 1) != 0;

        // Per-cell markability: the standard tunnel (mode-1, FUN_005672d0 via FUN_00566870)
        // never marks pillar cells; the depth tools' mode-0 marks (FUN_005671a0) have no
        // lattice gate in the original.
        public static bool CellMarkable(int tx, int ty, bool standardTunnel)
            => !standardTunnel || !IsPillar(tx, ty);

        // The full standard-tunnel per-cell validity, FUN_00566870 (see the header rule list;
        // the preview greys cells failing this, FUN_0056d0b0:25, and the painter
        // FUN_005672d0:56 refuses them). Pure: world data comes in as delegates so EditMode
        // tests can fabricate terrain.
        // neighbourSolid(x,y) — block aux[+0x00] (8×8-heightmap NW corner) > 1 (:35-50);
        // the port approximates with GetHmap (max corner) until
        // DiggableTerrain grows the NW-corner accessor.
        // openMarked(x,y) — the player's tile+0x08 nibble has bit 8 (:25/:34).
        // exposed(x,y) — the material type tile+0x18 & 3 ∈ {1,2} or fog off (:26-28);
        // wired to DiggableTerrain.TileMaterialDiggable (rejects
        // granite 0 and fully-open 3).
        public static bool CellValid(
            int tx, int ty, int width, int height,
            Func<int, int, bool> neighbourSolid,
            Func<int, int, bool> openMarked,
            Func<int, int, bool> exposed)
        {
            // 1. world-rim bounds (:19-22): [min+1, max-1) per axis.
            if (tx < 1 || tx >= width - 1 || ty < 1 || ty >= height - 1) return false;
            // 2. the lattice pillar (:23).
            if (IsPillar(tx, ty)) return false;
            // 3. not already open/being-dug for this player (:25).
            if (openMarked(tx, ty)) return false;
            // 4. exposure (:26-28) — stand-in.
            if (!exposed(tx, ty)) return false;
            // (:30-31 early-true for y+1 >= world.max is unreachable: rule 1 caps ty at max-2.)
            // 5. the support row + flanks (:33-50). y+1 = below (ant-farm).
            if (openMarked(tx, ty + 1)) return false;       //:33-34
            if (!neighbourSolid(tx, ty + 1)) return false;  //:35 below
            if (!neighbourSolid(tx + 1, ty)) return false;  //:36-38 right
            if (!neighbourSolid(tx - 1, ty)) return false;  //:39-41 left
            // (:42-44 re-checks (tx, ty+1) — redundant, skipped)
            if (!neighbourSolid(tx + 1, ty + 1)) return false;  //:45-47 below-right
            if (!neighbourSolid(tx - 1, ty + 1)) return false;  //:48-50 below-left
            return true;
        }

        // The cave-stencil anchor snap (tools 1..4, FUN_005300f0:186-198 — press & drag both):
        // x' = 2*floor((x - 0.5)/2) + 2.5 (consts 0.5 @0073708c, 1.5 @007371c0, 1.0 @00737138)
        // y' = 4*floor((y + 0.5)/4) + 3.5 (consts 0.25 @00737058, 4.0 @007374a0, 2.0 @00737330)
        // i.e. the 32×32 stencil centre rides a 2-tile lattice in X and a 4-tile lattice in Y;
        // FUN_00567cb0:66-72 then stamps cells over [(int)x'-16, (int)x'+16) × [(int)y'-32, (int)y').
        // Unused by the rect marquee — kept for the stencil stamp port.
        public static (float x, float y) SnapStencilAnchor(float pickX, float pickY)
            => (2f * (float)Math.Floor((pickX - 0.5f) * 0.5f) + 2.5f,
                4f * (float)Math.Floor((pickY + 0.5f) * 0.25f) + 3.5f);
    }
}
