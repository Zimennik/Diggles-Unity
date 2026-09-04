# Standard roofed tunnels — corrected native contract

> against `Diggles.exe`, `BlockDB2.BDB`, the current carve, and the
> current terrain emitter. This note supersedes every older claim that `tile+0x08` is an
> eight-level spatial voxel stack.

## Conclusion

The port does not need a stacked-block or multi-surface terrain model for standard tunnels.
The original also stores one `block_idx` per tile. A standard tunnel is a shallow BDB corner
transition selected by the mode-1 target `11`; the selected block retains the front rock and
contains the wall geometry that makes the opening read as a roofed tunnel.

The eight nibbles in `tile+0x08` are eight owners' independent dig orders. They do not describe
eight positions in the camera-depth axis.

## Executable proof

`FUN_00596b80` is decisive:

1. It reads `owner = *(gnome+0x36c)` and computes `shift = owner*4`.
2. It decodes `(tile+0x08 >> shift) & 0xf`. Raw nibble `3` has bit 8 clear and
   therefore decodes to target `11`.
3. It obtains the shared-node rim through `FUN_005968b0` and follows the four BDB
   transition indices at aux `+0x40..+0x4c`, accepting a candidate only when its gate
   byte at `+0x50..+0x53` equals the rim.
4. It writes one resolved `block_idx` into each of the four tiles around the node.
5. Its completion loop advances the nibble shift by four from 0 through 28, clearing
   every owner's completed order. It never indexes a spatial depth layer.

`FUN_004ca830` then reads the tile's single `block_idx` and loops over all three cell
records for that block. Those records are co-rendered geometry/material submeshes. They
are not alternate terrain states and are not selected through `tile+0x08`.

## Original-data proof

The first target-11 transition from solid block 0 in `BlockDB2.BDB` selects one of
blocks 22, 32, 44, or 54, depending on the shared corner. The real database contains:

| Block | lowered corner/gate | submesh triangle counts |
|---|---|---|
| 22 | SE, gate 11 | 6 / 8 / 8 |
| 32 | SW, gate 11 | 7 / 3 / 4 |
| 44 | NE, gate 11 | 8 / 7 / 4 |
| 54 | NW, gate 11 | 6 / 8 / 5 |

Solid block 0 has only its two-triangle front surface. Every target-11 successor retains
solid corners and adds populated wall submeshes. This is the authored tunnel edge/roof
profile the renderer must emit, and `BuildTileBuckets` emits all three records.

## Port contract

- `DiggableTerrain.DecodeTarget(3)` returns 11.
- `CarveStep` uses the owner nibble, shared-node rim, BDB transition gate, completion sweep,
  and per-owner dig ID.
- The standard marquee is two nodes wide, which is what produces a valid EFFPASSWALK floor
  strip and climb ribbon at target 11. The explicit real-BDB descent tests cover this law.
- `BdbPmpWorldBuilder.BuildTileBuckets` emits all three submeshes of the selected block.

This contract was already functional after an earlier commit`; the checkpoint removes
the contradictory tests, comments, status rows, and backlog entries that incorrectly kept the
feature open.

## Separate rendering work

`CBlockHole`/`GetBlockHole` clips authored strong-cliff geometry around scape-hole corners, and
cave landscape backdrops add scenery behind those cutouts. The current port does not yet reproduce
that path exactly. It is a cave/scenery presentation item, not a missing standard-tunnel roof or
a `tile+0x08` data-model requirement.
