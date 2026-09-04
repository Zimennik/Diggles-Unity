# Fluid system reverse engineering and port contract

> Status: native data/query laws, single-pass relaxation, source venting, block-volume
> presentation, corrected render frame, and fog contract implemented.
> Detailed citations and decoded coefficient tables: `Docs/WATER_PHASE1_RE.md`.

## Runtime ownership

`FluidSystem` is ordinary session state owned by `DigglesTclContext`. It contains the sparse
fluid-cell table, sources, stoppers, enable flag, deterministic 10 Hz update, and runtime snapshot
model.
It has no `MonoBehaviour` dependency. `FluidPresenter` observes cell changes and owns only the
Unity mesh, materials, and runtime-loaded original textures. The presenter requires and caches its
`MeshFilter`/`MeshRenderer` pair and repairs Unity fake-null component references before rebuilding,
so a world rebuild cannot dereference a missing renderer on `WorldRoot`.

`DigglesGameSim` calls `FluidSystem.Tick` from the same 10 Hz think pass as the native
`CGameThread::SrvDoThink` call to `CFluid::Tick`. This keeps flow independent of frame rate and
time-scale rendering.

## Exact recovered data laws

The last four bytes of every 89-byte BDB block auxiliary record are a little-endian fluid
descriptor, not generic flags:

| Bits | Meaning |
|---|---|
| 0..9 | fluid capacity in native volume units |
| 10..13 | upper fluid boundary in eighth-cell units |
| 14..17 | lower fluid boundary in eighth-cell units |
| 18..24 | vertical boundary aperture |
| 25..31 | horizontal boundary aperture |

The three shipped BlockDB variants contain the same descriptor tables. Most open blocks use
upper 8/lower 0; sloped and partially open blocks use 2/4/6 boundaries. The invalid sentinel
records use 15/15 and are rejected by `DiggableTerrain.TryGetFluidShape`. Capacity alone does
not make a boundary traversable: the owning south/east cell's applicable aperture must also be
nonzero after stopper multiplication.

The native source unit scale is 128. Tcl `-vpf` and finite `-volume` values are converted through
that scale before they touch per-block capacity. In the persisted source record, `+0x14` is the
configured volume, `+0x18` its remaining budget, `+0x1C` the height target, and `+0x20` VPF. The
internal disabled sentinel is exactly `-1.0`; the script no-update sentinel is `-10000`. A negative
non-sentinel VPF is a real drain.

The native terrain record contains two complete fluid type/fill buffers at tile offsets
`+0x1A/+0x1C` and `+0x1B/+0x20`. The four sparse coordinate arrays saved by `CFluid` are transient
active/work sets, not liquid volume; valid original saves commonly have populated buffers and four
empty sets.

Fluid type values are:

- `1`: water;
- `2`: sulfur;
- `3`: lava;
- `4`: lava-water.

Mixing preserves the native low-bit combination behavior. `get_ftype` now reads fluid cells,
not the terrain material byte.

For a point `(x, y, z)`, `isunderwater` uses the cell at `floor(x), floor(y)` and the executable's
eighth-cell surface test:

```text
sample = (1 - frac(y)) * 8
filledSpan = (upper - lower) * fill + (8 - upper)
underwater = sample < filledSpan
```

`get_waterheight` performs the native bounded scan of at most eight full/empty neighboring
cells and returns:

```text
surfaceY = cellY + 1 - ((upper - lower) * min(fill, 1) + (8 - upper)) / 8
```

`get_fill` and `get_ftype` use the executable's half-away-from-zero coordinate rounding.

## Tcl surface

The following commands are real and are removed from the fallback stub inventory:

- `set_water`;
- `set_fsource`, `get_fsource`, `rem_fsource`;
- `set_fstopper`, `rem_fstopper`;
- `get_fill`, `get_ftype`;
- `isunderwater`, `get_waterheight`.

`set_fsource` merges partial updates. Height and VPF are mutually exclusive, with a real VPF
winning when both appear in one call. Finite source budgets decrease by the amount actually
added or removed. Horizontal stoppers block vertical flow; vertical stoppers block horizontal
flow. Native obstruction is stored in directional nibbles: effective permeability is
`1-nibble/15`, intersecting stopper factors multiply in creation order, and each result is
re-quantized. A permeability-zero script wall therefore makes the directional aperture exactly
zero. `-ventil` performs the native bounded eight-cell surface lookup and adjusts the source cell
so its fluid surface is pinned to the source's actual Y, subject to its finite budget.

The same point query now backs `obj_query -water 0/1`. `action fly -swim 1` samples destinations
inside the live fluid volume, so the unchanged fish Tcl no longer uses the above-ground fly
validator. Native spare-time place searches also reject flooded eat, sleep, and leisure objects.
Existing drowning, air, diving-bell, lava/sulfur damage, water-wheel, hamster, and water-extinguish
branches become live through their unchanged Tcl calls.

## Flow contract

Each 10 Hz tick swaps the sparse work selectors, refreshes stoppers, applies sources, performs
one snapshot-driven boundary pass, and rebuilds the next active set. A current south/east cell
processes its north boundary first and west boundary second. Live amounts are mutated during the
pass, while pressure surfaces come from the pre-pass snapshot; this matches the executable's dual
fill buffers and prevents four-pass, whole-shaft teleportation.

For fill `F` and descriptor bounds `L/U`, the eighth-cell height is
`H=(U-L)*F+(8-U)`; the north side of a vertical pair adds eight. Pressure is `H` through eight
and `(H-8)*8+8` above it. Signed neighbor-to-current transfer is
`deltaPressure * 0.125² * effectiveAperture * relaxation + work * momentum`, clamped to the two
live amounts. The coefficient table is:

| Medium | relaxation | momentum | capacity floor | aperture floor |
|---|---:|---:|---:|---:|
| water / lava-water | 1.5 | 0.5 | 32 | 8 |
| sulfur | 1.4 | 0.3 | 32 | 8 |
| lava | 1.3 | 0.125 | 128 | 16 |

Fills below `0.001` are zeroed. Directional work remains active only above `0.004`, and a source
mutation seeds vertical work at `0.05`. Full derivation, kernel addresses, wall gate, examples,
and source-vent law are in `Docs/WATER_PHASE1_RE.md` §4.

## Presentation and fog contract

The presenter projects every simulated cell as the native block volume: a front wall at native
render depth `14.4`, plus an exposed top surface containing
`(16-(variant&15))/2` one-depth-unit strips at the exact eighth-cell fill level. Render-frame
coordinates use `BackdropLaws.LocalFromRender`; raw render depth is not port-local height.

Materials load original `alpha.tga`, `wasser_env.tga`, `schwef_nahtlos.tga`, and
`lava_nahtlos.tga` through `TgaLoader`. Water/lava opacity is fill-driven while sulfur uses the
native fixed alpha. The fluid shader consumes the same fog globals and depth-graded gray/black
law as terrain; black visibility also gates transparent alpha so hidden water cannot shine or
darken through fog. See `Docs/WATER_PHASE1_RE.md` §2 for renderer/fog citations.

## Persistence and verification

The runtime snapshot stores enable state, every non-empty cell, full source records, and stopper
records. The native `.sav` mapper writes live cells into both original buffers, serializes sources
and directional stoppers, and clears only the transient work sets. Load validation rejects
duplicate/out-of-world cells, non-finite values, invalid permeability, oversized tables, and
references to absent entities before any live-world commit.

`FluidSystemTests` covers descriptor decoding, the native coefficient/drop/leveling table,
capacity-bearing zero-aperture walls, source merge and finite emission/drain, vented surface
pinning, impermeable stoppers, exact point/surface queries, fluid type, water-aware object queries,
underwater spare-place filtering, and runtime snapshot restoration. `WaterPhase1Tests` pins the
volume mesh/render frame and fluid fog law. The save continuation fixture remains unchanged.
Full EditMode and PlayMode gates remain required when the test runner permits them.
