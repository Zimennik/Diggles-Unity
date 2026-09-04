# Original Pathfinding & Movement — Complete Reverse-Engineering

> Native nav/stamp/get_digedge law re-verified against the cited decompile/disassembly
> ranges. Historical implementation sections were corrected where the former scalar
> `+0x12`, carry, or rock-flood model leaked into them.

> Ground-truth RE of how Diggles (2001) pathfinds, walks, climbs, and positions for digging — the
> authoritative spec for porting the ORIGINAL pathfinding into the Unity port (replacing the custom
> mesh nav-graph). Sourced from a decompile sweep over
> `re/decompiled/Diggles/_global/*.c` + `Diggles.exe` `.rdata` constant extraction + a LIVE running-game
> memory dump (`dumps/extracted/{tile_array.bin, cmap_full.bin, start_area_runtime.json}`). Every claim is
> cited (`FUN_addr:line`, struct offset, constant hex+dec). Cross-validated; the few residual UNRESOLVED
> items are listed in §10 and are non-blocking.

> ✅ **PORT STATUS — this plan is IMPLEMENTED and LIVE.** The custom
> mesh nav-graph was deleted and this faithful pipeline is now the
> shipping one:
> - **P0 data layer** → `NavWorld.cs` (PMP 8×8 seed blit + `FUN_005a6b40`
> EFFPASSWALK derive + `FUN_005a6350` tile-record derivation + per-subcell `GetHmapSub`).
> - **P1 Calc1** → `Calc1Pathfinder.cs` (tile Bellman-Ford, +0x12&0x40 gate,
> cost-by-owner, endpoint relax).
> - **P2 MetaWalk + WalkFloor** → `NavWorldService.FindPath` + the CAct_MetaWalk
> segmentation in `DigglesTclEngine.BuildWalkSegments` (FLOOR run → `MetaWalkAction`
> in `Runtime/Tcl/Engine/EntityActions.cs`; WalkResult enum wired via
> `WalkResultAction`; + `RetryWalkAction` auto-retry layer).
> - **P4 Climb** → `ClimbAction.cs` + `FloorPathMap.cs` (WALL runs).
> - Gizmo = `NavWorldGizmo.cs`. Tests: `NavWorldTests`, `NavWorldRealTerrainTests`,
> `MoveSpeedTests`, `TurnAnimTests`, dig-repro suites.
>
> The §8 "to build" and §11 phased plan below are kept as the historical record
> of HOW it was built; treat their "restore/build" verbs as DONE. Remaining
> polish items (dig standoff sub-cell accuracy, climb clip-name table) are tracked
> elsewhere, not here.

> **Native-law closure.** An earlier carry-based reconstruction of the passability
> byte was based on a false data-gap premise and has been removed:
>
> - The PMP trailer RLE is the authored navigation-plane **seed**. `FUN_005992b0` expands each
> word as `value = word & 3`, `run = word >> 2` (`0` means `0x4000`) at
> `005992b0_FUN_005992b0.c:538-544`, then blits that buffer into
> `tile+0x3c..+0x7b` through `FUN_00596550` at `:563-567`. Values are exactly
> `0/1/3` (`bit0=walk`, `bit1=floor`). Boot and live stamps copy every clipped
> subcell byte verbatim **before validation**; there is no endpoint carry or gap bridge.
> - The executable exposes two scheduling levels that must not be conflated. The direct
> stamp call queues composite `0x40000` (`FUN_005992b0:570-592`), which expands to
> EFFOBJPF+EFFBLOCK+RENDERJOBS+MINIMAP, but not EFFPASSWALK
> (`FUN_005a3fd0:36-41`). The later full-world `0x5f` validation includes bit0 and
> executes EFFPASSWALK before EFFOBJPF and EFFBLOCK
> (`FUN_005931f0:246-252`; runner `FUN_005a46d0:112-266`). A later data probe
> corrects the earlier seam inference: both `gng_023.pmp` and `hol_020.pmp` carry zero
> RLE bytes across y=122..126, and their selected BDB detail records are solid there.
> Native therefore has **no initial route** across that join; it is a designed dig-through.
> Direct live-stamp order is **RLE seed → EFFOBJPF → EFFBLOCK**. Boot's later full
> validation may run **EFFPASSWALK → EFFOBJPF → EFFBLOCK**, while digging queues
> the same three nav passes on its expanded dirty rectangle. The established object-influence
> seam remains separate, avoiding a duplicate `FUN_005a6890` delta.
> - EFFPASSWALK (`FUN_005a6b40`) writes 0/1/3 from the selected BDB 0x59-record detail
> bytes. It requires all 31 exact gnome-capsule probes to be `<0x0e`, gates through
> `tile((sx+4)>>3,(sy+4)>>3)+0x18 bit7`, and classifies floor by the native detail-jump
> decision (`:105-523`; exact offsets and formula in §1.5). A carve's `0x10000`
> composite also schedules bits0|1|2 on its expanded dirty rectangle
> (`FUN_005a3fd0:42-48`). The gather resolves node `+0x00` on every pass
> (`FUN_00595e40:69-77`), after digging has replaced that live block id through the
> four transition slots (`FUN_00596b80:64-134`). It never reuses a pristine PMP id.
> On the TrollQuest seam, one dug node column opens only eight subcells; the native
> capsule spans `dx=-7..+7`, so the designed traversal needs the adjacent two-node
> carve. The exact BDB/PMP probe yields walk cells only for that 16-subcell opening.
> - `+0x12` is one packed byte, never a scalar `ExitSub`: bits 0-2 are subX,
> bits 3-5 are subY, bit 6 is the floor/object-passage flag. EFFBLOCK settles the
> packed pair **down** through consecutive plane-bit1 cells only in the all-walkable
> branch (`FUN_005a6350:256-278`); the partially-walkable branch stores the best pair
> without settling (`:281-288`). No walk cell or the solid-window gate selects `-1`
> (`:236-245`); floor/object passage selects `20`, and genuinely walkable cells with
> neither category select `50` (`:279/:287/:290`).
> - Original saves still carry `tile+0x06` at record `+4` and the complete raw
> `tile+0x12` byte at record `+13`. They round-trip verbatim after the native
> full-`0x5f` sub-grid validation equivalent (`FUN_005931f0:246-249`); the authored
> plane itself is not in that save stream.
> - Per-object `+0x06` deltas remain a separate EFFBLOCK tail
> (`FUN_005a6890:76-95`). The port keeps its existing object-influence seam rather
> than applying those deltas twice.
>
> Regression locks: `NavWorldTests`, `StampedTemplateNavigationTests`,
> `OriginalSaveWorldInitializationTests`, `DigEdgeBfsTests`, and the optional real-data
> a live `apply_level` fixture.

---

## ⚠️ FRAME CORRECTION (FINAL) — read this first

**The world is a SIDE-VIEW "ant farm" and §0–§2 were RIGHT all along: the `+0x3c` sub-grid IS the walkable
map.** The 512×640 tile grid is the SCREEN FACE — native X = horizontal (east), native **Y = the GAMEPLAY
VERTICAL** (+y = down/deeper, gravity, the climb axis), and the BDB height nibble = **SLAB DEPTH INTO THE
SCREEN** (the dig axis, ≤8 blocks of 1.0; 15=undug, 0=dug-through). A walkable floor is therefore a
1-sub-cell-thick **GROUND LINE** in the (x,y) plane — the boundary row between an open pocket (above, −y) and
supporting material (below, +y) — extending into the screen along the depth axis. That is *why* the sub-grid
is sparse: floors are lines in a side view, not sheets.

Live-dump verification (`tile_array.bin` + BDB aux + `find_gnomes.py` on `diggles_meadow.dmp`):
- **1590/1590 value-3 cells sit on ground lines** — h(own)>h(above) (first solid row under the pocket, 1092)
  or h(below)>h(own) (last open row over h=15 support, 498). Zero exceptions.
- **Gnome positions cluster AT the lines**: all meadow gnomes at y∈[29.54..30.50] around the y=30.5 line
  (7 of 28 exactly ON value-3 cells), while their z spreads 6.16..15.18 — the "roam freely" axis is the DEPTH.
- `get_gnomeposition`'s ±Y probe = reading the floor bit at the support row under the body anchor.

> **An earlier "MAJOR CORRECTION" that stood here ("walkability is GEOMETRY-based, gnomes walk 2-D meadows,
> `+0x3c` is only a rim marker") was itself the misread and is REVERTED.** It compared gnome BODY-ANCHOR cells
> (which sit ~1.0 above the feet) against marker rows in a frame that treated (x,y) as a top-down plane and the
> height nibble as elevation. The "rebuild NavWorld to the geometry model" directive is rescinded — NavWorld's
> walkability source is the `+0x3c` bits (value 3 = floor line, value 1 = walkable non-floor/wall), with `GetHmapSub`
> used ONLY for depth placement. Dump gotcha kept: the game relocates (live base ≠ 0x400000) — read the CMap
> singleton (`DAT_0077241c`) via the ACTUAL module base from the minidump module list.

### Addendum — port data verified + coordinate frame proven + gizmo status

- **The port's `NavWorld` data is byte-for-byte identical to the original.** Headless test
  `NavWorldRealTerrainTests.DumpStartMeadow` builds the real `urw_unq_start` terrain
  (`DigglesEngine.Build` → `map create 512 640` + `call templates/tcl/urw_unq_start.tcl` → `Map.EmitTerrain`)
  and its block ids / `GetHmapSub` heights / `+0x3c` bits match `tools/verify_walkable.py` on the live
  minidump: tile (249,30) = block 71 (front height 0 / back height 15), open meadow = block 59 (height 0).
  So loading is correct; any remaining issue is in **classification/visualisation**, not data.
- **WORLD axis mapping is PROVEN** (`NavWorldTests.WorldRoot_Qx_minus90_AxisMapping`, Unity's real
  quaternion): under `WorldRoot = Euler(270,0,0)` with `NativeToLocal=(x,z*0.5+7,−y)`, native **east→world+X,
  native-Y→world−Y, height-nibble→world−Z**. ⚠️ Labeling correction: **GAME UP = world +Y**
  (= native −y); the height-nibble axis (world −Z) is the slab DEPTH into the screen, NOT "up". Floors are
  faces looking at world +Y and span the world X/Z plane. A gizmo overlaying the terrain MUST still use
  `WorldRoot.TransformPoint(local)` / `Gizmos.matrix = localToWorld` (NOT a raw Y-up frame).
- **Floor/wall split is RESOLVED.** The walk floor = the `+0x3c` value-3 GROUND LINES (each
  rendering as a depth strip z = min(h_above,h_own)..max(h_own,h_below) at the support boundary); the climb
  walls = value-1 cells (vertical faces along tile-Y). The "gradient rule" idea and the "full geometry fill"
  are both retired — the height nibble never was the gameplay vertical. `NavWorldGizmo` draws exactly this.

---

## 0. Executive summary — the strategic insight

**The custom nav-graph pivot was founded on a MISREADING of the data, not a real data gap.**
Prior RE saw that the raw PMP navigation RLE has only ~108 non-zero bytes / 16384 and concluded "the pathfinder must
read something denser; the PMP voxels are unusable." A dump of the **live running game** disproves this:

- **0.5% of sub-cells are walkable in the LIVE game too** — identical sparsity to the raw PMP
  (`{0:509548, 1:862, 3:1590}` live vs `{0:16276, 3:108}` per PMP chunk).
- Live walkable tile `(227,30)` sub-grid = one row `03 03 03 03 03 03 03 03`, rest `00` — **byte-identical
  to the raw PMP tile.**
- **Gnomes do NOT walk over open ground.** They walk along **thin, 1-sub-cell-wide carved shelves and
  corridors** (live: horizontal shelves at y=30/42/54/66 joined by single-tile vertical corridors).

So the sparse `+0x3c` sub-grid **is** the walkable map. PMP loads its authored seed verbatim
(`FUN_005992b0:538-544`, `:563-567`/`FUN_00596550`), and EFFPASSWALK may then replace a validated rectangle
from BDB detail (`FUN_005a6b40`). **A faithful port preserves that order and must not invent a bridge, carry,
or densification heuristic.** A direct live stamp does not queue EFFPASSWALK. The TrollQuest seam is the
decisive distinction: its authored seed and derived solid geometry both reject the initial crossing; only the
post-dig `0x10000` validation derives the opened walk lattice.
The original Calc1 port did not die for lack of data; it died because the team expected dense data, saw
sparse, assumed broken, and pivoted. The data was right all along.

**The complete pipeline (each stage cited in its section):**

```
PMP file ──FUN_005992b0/00596550──▶ tile+0x3c (8×8 authored seed, verbatim) §1
                                         │
CURRENT node+0 block id ──gather BDB aux──EFFPASSWALK──▶ tile+0x3c (derived 0/1/3 plane) §1.5
                                         │
BDB block aux (block_idx*0x59) ──────────┼──▶ 8×8 per-sub-cell HEIGHT (Z = nibble*0.5 + 7.0) §1
                                         ▼
FUN_005a6350 (EFFBLOCK derivation pass) ─────▶ tile+0x06 (valid/cost) + tile+0x12 (0x40 flag + §1.4
                                                  packed {subX,subY} pair)
                                         ▼
CMapPathfinder::Calc1 (FUN_0059d640) TILE-level Bellman-Ford, gates on +0x06 & +0x12&0x40 §2
   • sub-cells only for endpoint snapping + output pair decode (+0x12 x=bits0..2,y=bits3..5) + bLines smoothing
                                         ▼
CAct_MetaWalk (00415d10 ctor / 004158a0 CalcPath / 00416540 tick / 004163a0 segment) §3
   • classify each waypoint FLOOR/WALL/WATER (00423730) → group → dispatch sub-action
   • writes WalkResult enum to gnome+0x248 (Tcl get_walkresult) §3,§7
                                         ▼
   FLOOR → CAct_WalkFloor (00415f40) root-motion-coupled mover, floor-height follow §4
   WALL → CAct_Climb (00415e10) FloorPathMap BFS over height-steps + interpolating exec §5
   WATER → CAct_WalkObject(00416120) swim (out of scope until water gameplay)
```

The Tcl scripts run UNCHANGED on top; they only need the native command contract in §7
(`action walk`, `get_walkresult`=2/4, `get_gnomeposition`, the dig primitives) to be faithful.

---

## 1. The data model — what the pathfinder reads, and where it comes from

### 1.1 The tile struct (124 = 0x7C bytes)

Indexed by `FUN_00592f10(x,y)` = `tileBase + ((clampY*stride + clampX) % count) * 0x7c`, where (from the live
CMap singleton `DAT_0077241c`): `[+0x14]`=tileBase, `[+0x18]`=count(=327680=512×640), `[+0x44]`=stride(512),
`[+0x4c]`=modulus(327680), `[+0x84..+0x90]`=tile bounds (minX,minY,maxX,maxY), `[+0x94..+0xa0]`=**sub-cell**
bounds (=8× tile bounds), `[+0x24]`=BDB-aux descriptor `{[+0x10]=base, [+0x14]=count}`.

| Offset | Sz | Origin | Meaning | Read by |
|---|---|---|---|---|
| **+0x00** | u32 | PMP colour/block stream `005992b0:415` | **block_idx** → BDB (height + geometry) | get_hmap, cost `dig`, renderer |
| **+0x06** | i8 | **DERIVED by FUN_005a6350:290** | **base cost / validity**: −1(0xFF)=solid/blocked; 20(0x14)=floor tile; 50(0x32)=walkable-non-floor | **Calc1 gate + seed cost** |
| +0x07 | u8 | PMP zone stream `005992b0:453` | zone byte (4=default, 20=template) | fog/gameplay |
| +0x08 | u32 | dig system | 8 owners × 4-bit target/order nibbles | dig, get_digedge |
| **+0x12** | u8 | **DERIVED by FUN_005a6350:278/286; saved raw** | **packed pair + flags**: bits 0-2=subX, bits 3-5=subY, bit 0x40=floor/object-passage-bearing exit, bit 0x80=independent preserved flag. The all-walkable branch may settle Y down through bit1 cells. | **Calc1 gate + output polyline; save round-trip** |
| +0x13 | u8 | `005992b0:521` `\|=1` | "touched by template" + dirty bits | renderer |
| +0x14 | u16 | L2M cellId `005987c0:44` | corner-pattern/BlockType (BDB block-template select) | renderer only |
| +0x18 | u16 | PMP terrain switch `005992b0:342/377` | **dig precondition** (low 2 bits: 0=buried, 3=open, 1/2=exposed=diggable) | dig (FUN_005976b0) |
| +0x24 | … | dig | diggability/centre | dig |
| +0x34..+0x3b | 8×u8 | gameplay | per-owner flag array (owner gate reads `[0x34+iOwner]`) | Calc1 owner gate, get_digedge |
| **+0x3c..+0x7b** | 64 B | PMP RLE seed `005992b0:538-544`, then EFFPASSWALK derive `005a6b40:105-523` | **8×8 sub-cell NAV PLANE**, idx `(y&7)<<3\|(x&7)`. bit **0x01**=walkable, **0x02**=floor; EFFOBJPF can inject bits 2/3 for object passage. | classifier, relax, fixdig, get_gnomeposition, EFFBLOCK |

> **Doc correction:** `re/formats/WORLD_PIPELINE.md §2` labels `+0x3c..+0x7b` as "8×8 heightmap" — **WRONG**, it is
> passability. Heights come from the BDB aux (§1.3). An earlier pass also mis-called `+0x06` a zone byte;
> it is the DERIVED cost/validity (live 0x14=20 = a floor tile, exactly matching the FUN_005a6350 formula).

### 1.2 Sub-cell passability bits (`tile+0x3c[64]`)

| Bit | Mask | Meaning | Primary reader |
|---|---|---|---|
| walkable | 0x01 | a unit may occupy this sub-cell | endpoint relax (Calc1), vector_fixdig, FUN_005a6350 |
| floor | 0x02 | the sub-cell is a floor surface (not a wall face) | surface classifier `00423730`, get_gnomeposition |
| object bit 2 | 0x04 | object-supplied plane state (not present in the PMP low-two-bit RLE) | EFFOBJPF / object movement |
| object passage | 0x08 | object-supplied passage; makes EFFBLOCK's floor/passage flag and bypasses the 2×2 solid-material gate | EFFOBJPF, FUN_005a6350 |

PMP authored-nav values are exactly `0`=blocked, `1`=walkable non-floor, `3`=walkable floor. The loader can
only emit the low two bits because its RLE word uses `word&3` (`FUN_005992b0:538-544`). EFFPASSWALK can later
overwrite the complete byte with a freshly derived 0/1/3 value (`FUN_005a6b40:105-523`); EFFOBJPF then
read-modify-writes object bits 2/3 (`FUN_005a5da0:116-164`).

### 1.3 Heights — the BDB block aux (NOT in the tile struct)

`get_hmap` leaf `FUN_004093f0:14-25`:
```
tile = FUN_00592f10(subX>>3, subY>>3)
aux = block_idx(=tile[+0]) * 0x59 + CMap[+0x24][+0x10] // BDB aux base, stride 89
height = aux[ (subY&7)<<3 | (subX&7) ] // 8×8 per-sub-cell nibble 0..15
```
World Z = `height * 0.5 + 7.0` (DAT_0073708c=0.5, DAT_00737520=7.0). Range 7.0..14.5. **Per-SUB-CELL** — a
single tile holds 64 independent heights (a cliff edge can run *within* one tile). The aux 89-byte record:
`[0x00..0x3F]`=8×8 height grid (corners 0x00/0x07/0x38/0x3F, centre 0x24); `[0x40..0x4f]`=4×u32 neighbour-block
transition indices (the carve graph, see DIGGING_MECHANIC.md); `[0x50..0x53]`=cached corner gate heights;
`[0x54]` low nibble=elevation variant. **The port already loads this** (`BdbData.PerBlockAux`, `AuxStride=89`,
`AuxAt(blk,off)`) — it just needs a per-sub-cell accessor (see §6/§8).

### 1.4 EFFBLOCK — exact `+0x06/+0x12` derivation (`FUN_005a6350`)

A post-load/stamp/carve pass converts the **current** `+0x3c` plane into per-tile pathfinding fields
`+0x06`/`+0x12`. The direct stamp composite schedules EFFBLOCK as bit2
(`FUN_005992b0:580`, `FUN_005a3fd0:36-41`); when a full validation also includes bit0, the runner executes
EFFPASSWALK and EFFOBJPF before it (`FUN_005a46d0:112-266`). A dig schedules bits0|1|2 through its
`0x10000` composite (`FUN_005a3fd0:42-48`). EFFBLOCK clamps its half-open subcell rectangle to
`[minX+0x12,maxX-0x13) × [minY+0x0e,maxY-0x15)` before converting it to intersecting tiles
(`FUN_005a6350:40-69`). For each such tile (`:99-129`):
```
best_score = 0; best_pair = 0x24; no_walkable = true; all_walkable = true; any_object_passage = false
for sub in 0..63:
    b = tile[0x3c + sub]
    any_object_passage |= b & 0x08
    if (b & 0x01) == 0: all_walkable = false
    else:
        no_walkable = false
        // bit 0x02 contributes 0x100; otherwise prefer L1-nearest to (4,4); strict max, first wins
        score = ((b & 0x02)*0x80) - |4-(sub>>3)| - |4-(sub&7)| + 8
        if score > best_score: best_score = score; best_pair = sub
```
Then writes (`FUN_005a6350:132-290`):

- If there is no walkable subcell, write `+0x06=-1` and leave `+0x12` stale (`:237-238`, `:290`).
- AND `tile+0x18 bit7` over the exact 2×2 `{self,right,down,diagonal}` window. If all four are set and
  there is no bit3 object passage, also write `+0x06=-1` and leave `+0x12` stale (`:132-245`).
- If all 64 subcells are walkable and neither floor nor object passage is present, store `+0x12=0x24`.
  Otherwise, in this all-walkable branch only, settle `best_pair` downward while the next row carries bit1,
  then store `0x40|settled_pair` (`:246-278`).
- If only some subcells are walkable, do **not** settle: store `((score>>2)&0x40)|best_pair` (`:281-288`).
- For either valid branch, store `+0x06=20` when floor/object passage contributed the high score, otherwise
  `+0x06=50` (`:279/:287/:290`). The tail `FUN_005a6890:76-95` applies per-object cost deltas separately.

The `+0x12` low six bits are a packed coordinate pair: `subX = value&7`, `subY = (value>>3)&7`.
Calc1 may ignore the 0x40 gate for a climber, but every consumer must still decode both axes from this one byte.

### 1.5 Dispatcher and EFFPASSWALK (`FUN_005a46d0` / `FUN_005a6b40`)

The runner consumes queued dirty rectangles in ascending bit order. Its profiler strings and worker calls give
the complete mask table:

| bit / mask | pass | worker / citation |
|---|---|---|
| 0 / `0x001` | EFFPASSWALK | `FUN_005a6b40` (`005a46d0:112/156`) |
| 1 / `0x002` | EFFOBJPF | `FUN_005a5da0` (`:167/211`) |
| 2 / `0x004` | EFFBLOCK | `FUN_005a6350` (`:222/266`) |
| 3 / `0x008` | BASELIGHT | `FUN_005906a0` (`:277/321`) |
| 4 / `0x010` | RENDERJOBS | runner block `:333-401` |
| 5 / `0x020` | FLUIDOBJS | runner block `:414-466` |
| 6 / `0x040` | MINIMAP | `FUN_005a9090` (`:477/526`) |
| 7 / `0x080` | IDIOBJ | `FUN_005d5570` (`:538/582`) |
| 8 / `0x100` | OBJPHYSIC | runner block `:593-735` |
| 9 / `0x200` | DUMMYDES | profiler block beginning `:628` |
| 10 / `0x400` | FLUIDALIST | runner block `:736-790` |

The stamp site first blits RLE at `FUN_005992b0:538-568`, then queues `0x40000` over the stamp, `0x400` over
the one-subcell ring, and `0x008` over its clipped lower band (`:570-607`). `FUN_005a3fd0:36-41` expands the
composite to `0x006`, `0x010`, and `0x040`: direct stamp work is EFFOBJPF, EFFBLOCK, RENDERJOBS, MINIMAP,
plus the explicitly queued FLUIDALIST/BASELIGHT bands. It does **not** directly add bit0. In contrast,
the full-world validator queues `0x5f` then calls the runner with `0x7ff` (`FUN_005931f0:246-252`), which
orders EFFPASSWALK → EFFOBJPF → EFFBLOCK. Direct live stamps retain the authored plane and run EFFBLOCK;
they do not borrow the full-load path. `FUN_005a3fd0:42-49` separately expands a dig's `0x10000` composite
to bits0|1|2 over `{x-8,y-2,x+8,y+0x10}`, which is when a newly opened seam is re-derived before EFFBLOCK.
The TrollQuest join is intentionally blocked until that carve path runs.

EFFPASSWALK takes a subcell rectangle and clamps it to the native half-open interior
`[minX+0x18,maxX-0x18) × [minY+0x10,maxY-0x18)` (`FUN_005a6b40:55-74`). For each `(sx,sy)`:

The input is live: `FUN_00595e40:69-77` reads the tile's current node `+0x00` block id and copies that
block's first 64 bytes from the BDB 0x59 record. `FUN_00596b80:64-134` writes the four transitioned block
ids before queuing the dig validation. Consequently pristine tiles reproduce their authored BDB detail,
while carved tiles immediately select the dug variants; `tile+0x3c` is the output plane, not the detail input.

1. Gate on `tile((sx+4)>>3,(sy+4)>>3)+0x18 bit7 == 0` (`:15`).
2. Require detail `<0x0e` at all 31 exact `(dy,dx)` offsets (`:116-419`):
   `(-1,0)`, `(-2,±2)`, `(-3,±4)`, `(-4,0)`, `(-5,±5)`, `(-5,±7)`, `(-6,±2)`,
   `(-8,0)`, `(-8,±4)`, `(-8,±7)`, `(-10,±2)`, `(-11,±5)`, `(-12,0)`, `(-12,±7)`,
   `(-13,±3)`, `(-15,0)`, `(-15,±4)`, `(-15,±7)`.
3. With `h=detail(sx,sy)`, emit floor `3` when
   `detail(y-1)+2<h && detail(y-11)+2<h`; otherwise, when `h+2<detail(y+1)`, emit `3` iff
   `detail(y-10)+2<detail(y+1)`, else emit walk `1`; all other clear cells emit `1`
   (`:419-474`). The fallback is **y-10**: executable VA `0x5a7381` constructs that row and
   `0x5a76a8` passes it to the accessor; the older y-4 transcription was wrong.
4. Blit the complete 0/1/3 result byte into `+0x3c` via `FUN_00596550` (`:519-523`).

### 1.6 Load/stamp pipeline (Tcl `map_template`)

```
map create W H → alloc W*H*0x7c, set CMap extents/stride
map_template <pmp> X Y → FUN_005992b0:
    PMP = [u32 W][u32 H][W*H u8 terrain→+0x18][W*H u32 colour→+0x00 block_idx]
          [u32 rleCount][rle u16 authored seed → +0x3c..0x7b via 00596550][W*H u8 zone→+0x07]
    (direct 0x40000)         → EFFOBJPF → EFFBLOCK (+0x06/+0x12 from authored plane)
map_setlayer2 X Y <l2m> → FUN_005987c0: cellId → +0x14
(BlockDB?.BDB) → per-block 89-byte aux = detail heights, indexed by block_idx
(full validation) → EFFPASSWALK → EFFOBJPF → EFFBLOCK
                             plane derive       object bits    +0x06/+0x12
(dig 0x10000) → expanded EFFPASSWALK → EFFOBJPF → EFFBLOCK
```

Everything is row-major `y*512 + x`. Verified vs `tile_array.bin` (512·640·124 = 40,632,320 B).

---

## 2. CALC1 — the tile pathfinder (`FUN_0059d640`, 9702 B)

Entry: `CMapPathfinder::Calc1(start, end, iOwner, bLines, bCanClimb, maxNodes)`. Float wrapper `FUN_0059d440`
converts CVec3D→integer **sub-cells** via `× DAT_0073752c (=8.0)` + round, dropping Z (2-D in the tile plane),
then calls the int entry. `start/end` are sub-cell ints; `tile = sub>>3`, `subInTile = (subY&7)<<3|(subX&7)`.

### 2.1 Resolution — TILE search, sub-cell endpoints (the reconciliation)
- **Core Dijkstra runs at TILE resolution.** Distance field `DAT_00b9bf18` = dense `int[tileW*tileH]`, slot
  `width*tileY+tileX`, ∞=0x7FFFFFFF (alloc `FUN_0059a320`). Neighbours = **4-cardinal ±1 in TILE units** (no
  diagonals, no Z axis). It is a **FIFO Bellman-Ford**, queue cap 0x400=1024 grow
  +1024 (`FUN_0059a560`), NOT a heap-Dijkstra.
- **Sub-cells enter only at the edges:** start/goal passability snapping (`+0x3c & 1`), the output polyline
  (each tile's packed `{subX,subY}` from `+0x12` low 6 bits), and bLines smoothing.

### 2.2 The per-neighbour gate
For candidate neighbour tile T at (tx',ty'):
1. **Validity:** `T[+0x06] != -1` (`0059ab90:101`).
2. **Cheaper:** `dist[cur] + edgeCost(cur,T) < dist[T]`.
3. **Climb gate (only `!bCanClimb`):** `(T[+0x12] & 0x40) != 0` — must be a FLOOR tile (`0059ab90:119,204,289,375`).
   `bCanClimb` **drops** this gate (variants C/D have ZERO `& 0x12` tests) → climbers may step onto wall tiles.
   
4. **Owner gate (only `iOwner ≤ 7`):** `T[0x34 + iOwner] != 0` (`0059bf90:108`).

Dispatch (`0059d640:417-433`): `iOwner≤7` selects cost-v2 + owner gate; `bCanClimb` selects no-0x40-gate. Four
variants A/B/C/D = `0059ab90 / 0059bf90 / 0059b670 / 0059ca30`. **Original bug to replicate:** variant B's
x−1 (left) neighbour tests mask `0xA4` not `0x40` (`0059bf90:328`, disasm-verified). Under the corrected
packed-pair law, `0x20` is subY bit2 and `0x04` is subX bit2; `0x80` is the extra flag. This is an active
directional copy-paste defect, not a mask over unknown floor flags.

### 2.3 Cost (`FUN_0059a740` v1 / `FUN_0059a8b0` v2 — identical but cliff const)
`cliff(t) = t[+0x12] & 0x40`; `base = T[+0x06]`; `dig(t) = BDB.block[t.block_idx].byte[+0x24]` (block stride 0x59).
```
if cliff(cur) XOR cliff(T): return base + CLIFF − 16*min(dig(cur),dig(T))
elif !cliff(cur) && !cliff(T): return base + 1 + 16*|dig(cur)−dig(T)|
else: return base + 1
```
CLIFF = v1 **0x114=276** / v2 **0x10a=266**; elevation step **0x10=16**. v1 for iOwner>7, v2 for iOwner≤7.
After the goal is first reached, the node budget is clamped to `min(budget, round(nodesExpanded × 1.5))`
(DAT_007371c0=1.5) — "explore a little more, then stop."

### 2.4 Relaxation (snap impassable clicks) — sub-cell, `+0x3c & 1`
- OOB margin 0x28=40 sub-cells inside `CMap[+0x94..+0xa0]` → null if outside.
- **Start:** square-ring r=1..32 (probe +y,+x,−x,−y), first walkable sub-cell wins.
- **Goal:** fan r=1..12 biased toward +y; null if none.
- Post-snap "+y while `+0x3c & 2`" nudge to clear overhang sub-cells.

### 2.5 Output + bLines
Returns a ref-counted `CPath` `{waypoints: int[2] sub-cell pairs, count, start, goal, totalCost}`, **start→goal
order**, built by greedy backtrace over the dist field (`FUN_0059aad0`/`aa20` = predecessor filters). Each
waypoint = the tile's **packed exit pair** from `+0x12` (`x=value&7`, `y=(value>>3)&7`). **bLines (NOT vestigial):** when set, snaps each
vertex to a valid floor sub-cell in its column (`FUN_0059a5b0`), densifies wall spans at step 2.0, then removes
collinear interior points. MetaWalk calls Calc1 with **bLines=0** (raw per-tile crossings); bLines is for other
callers. **UNRESOLVED:** alt pathfinder Calc2 (`FUN_005a22f0`, gated by owner[+0x7b4]) — campaign uses Calc1; defer.

---

## 3. METAWALK — maintainer (`CAct_MetaWalk`)

Function roles (corrected vs the brief, via source-attribution): ctor `00415d10`; **CalcPath `004158a0`** (runs
Calc1, stores CPath); **per-tick `00416540`**; **MakeNextSegment `004163a0`**; surface classifier `00423730`.

### 3.1 CalcPath (`004158a0`) → the Calc1 call (disasm-verified push order)
Branch on `owner[+0x7b4]`: ==0 → **Calc1** (`0059d440`); !=0 → Calc2 (`005a22f0`, out of scope).
```
Calc1(v1 = movable[+0x0c/+0x10/+0x14] (current pos),
       v2 = owner[+0x7a4/+0x7a8/+0x7ac] (target pos),
       iOwner = movable[+0x36c] + (owner[+0x7d8]==0 ? 100 : 0),   // +100 = no-owner-gate variant
       bLines = 0,
       bCanClimb = owner[+0x7b0] )                                // per-dwarf, default 1
```
Target Z is snapped to the destination tile height (clamped 14.5); the path's last waypoint is patched onto the
exact target.

### 3.2 Per-tick (`00416540`) + segment loop (`004163a0`)
`00416540`: tick the current sub-action (`this+0x14`); when it returns "done", read its result — **4** ⇒ build
the next segment (MakeNextSegment); **3/5/0** ⇒ propagate up and stop. `004163a0`: classify the cursor waypoint
(`00423730`), **group consecutive same-surface (and same water-body) waypoints**, advance the cursor, and spawn
the sub-action for that segment: surface **2=FLOOR→WalkFloor (00415f40)**, **1=WATER→WalkObject (00416120)**,
**0=WALL→Climb (00415e10)**. A wall segment with `bCanClimb==0` ⇒ result 3 (NOPASSAGE).

### 3.3 Surface classifier `00423730` (exact bits)
```
(sx,sy) = path[index]; tile = FUN_00592f10(sx>>3, sy>>3); b = tile[0x3c + ((sy&7)<<3|(sx&7))]
if bCanSwim(owner[+0x7b1]) && (b & 0x04) && waterBody.active: return 1 // WATER
if (b & 0x02) == 0: return 0 // WALL/solid
else: return 2 // FLOOR
```

### 3.4 WalkResult enum @ gnome+0x248 (Tcl `get_walkresult`) — names from `004075b0:155-176`
| Val | Name | Meaning | Written |
|---|---|---|---|
| 0 | WS_ERROR | bad surface/internal | `004163a0` default |
| 1 | WS_WALKABORTED | externally broken | `00407b30` (Break) |
| **2** | **WS_NOTFINISHED** | **walking (in progress)** | start `00407430:64`, init `00415d10:79` |
| **3** | **WS_NOPASSAGE** | **no path / blocked / wall-no-climb** | `004163a0` (null path / wall), `004075b0:50` (OOB) |
| **4** | **WS_TARGETREACHED** | **arrived (all segments consumed)** | `004163a0` (cursor exhausted) |
| 5 | WS_WALKEVENT | walk-timer callback fired | `004075b0:215` |

**Re-pathing** lives one layer up in `CActWalk_AutoRetry` (`004075b0`): on a stuck/failed child it nudges the
target and rebuilds a fresh MetaWalk, and re-fires a periodic Tcl callback. Plain MetaWalk
never re-paths.

---

## 4. WALKFLOOR — the floor mover (`CAct_WalkFloor`, `00415f40`)

### 4.1 Walk verb (`action <obj> walk "<opts>" <fin> <brk>`) — dispatcher `FUN_00407430`, parser `FUN_00420840`
Defaults set in `FUN_00420520`: **`-canclimb`=1 (ON)**, `-useobjects`=1, `-waving`=1, `-stopat`=2.0,
`-targetdist`=−1 (→ arrival eps). Key options (field on the CWalkInfo block at owner+~0x79c): `-target{xyz}`,
`-object`+`-dummy` (walk to obj's link-point), `-dig <bool>` (bypass floor-snap — keep exact dig-edge target),
`-canclimb <bool>` (0x7b0; gates the WALL branch), `-speedtype <int>` (0/1=walk,2=run,3=autodist),
`-animsets {slow fast dive std}`, `-forcetarget`, `-targetdist`, `-walktimercallback/-counter`. Unknown options
are silently skipped.

### 4.2 Movement is FRAME-COUPLED to animation root motion (NOT a constant, NOT time-based)
Per-frame displacement (`FUN_00418db0:74-83`):
`disp = (rootmotion[9]+[10]+[11]+[12]) * DAT_00737058(=0.25)`, consumed as arc-length along the baked floor
points (piecewise-linear). The gait (`gnome+0x798`) is chosen by speedtype (`FUN_004234a0`: 2→run else walk);
walk vs run speed differs **entirely because the run clip (`laufen_schnell_loop`) has larger per-frame root
translation**. At 10fps anim, per-tick world advance = clip frame delta × 0.25. **Port: drive speed from
per-clip root motion, not a hand-tuned constant.**

### 4.3 Arrival, facing, transitions
- **Arrival threshold = 0.09 = 0.3²** (DAT_0073702c), with the **Z/height axis weighted ×0.5** (DAT_0073708c) in
  every distance metric — match this so vertical gaps don't false-trigger arrival.
- **Turn clips** when heading change ≥ **π/4 (45°)** (DAT_007370d0): play an in-place turn (turn_l/r_90/180 =
  `drehen_links/rechts/ganz_links/ganz`) before walking; smaller changes blend in-stride.
- **Start/stop chain:** walk_start → walk_loop → walk_stop (NOT a bare loop). Clip names come from the 24-slot
  animset table (`set_class_animset`, `z_anims.tcl:1278+`); slots: 0 standard, 1 walk_start, 2 walk_loop,
  4 walk_stop, 5-8 turns, 9-13 climb (standard/up/down/right/left), 14-18 ladder/ground↔wall.

### 4.4 Floor-height following (the port's straight-Y-lerp bug)
Every baked intermediate point carries its **own heightmap Z** (sampled per node via `FUN_00424470`: the
per-sub-cell height table, `(x>>3,(y+2)>>3)` cell, sub `((y+2)&7)<<3|(x&7)`), and the integrator interpolates Y
**between adjacent on-terrain points** — never start→goal directly. **Port fix:** generate sub-cell-spaced
waypoints each carrying their sampled floor Y; advance by arc-length; interpolate Y between neighbours; weight Z
×0.5 in distance.

---

## 5. CLIMB — wall navigation (FloorPathMap BFS + interpolating executor)

**Reframe:** "climbing" is split. The **route** over a vertical face is found by `CAct_WalkFloor`'s grid BFS
(`0041aac0` builds a `FloorPathMap`, `0041d8a0` floods it); the **per-segment execution** is `CAct_Climb`
(`0040bb70` ctor / `0040be90` step / `0040bf80` tick), dispatched for WALL segments (`00415e10`).

### 5.1 FloorPathMap (`0041d8a0`) — grid, "wall" cell, BFS
- Flat `uint32[rows × 64]` (**column stride 0x40=64**, NOT 8×8). Cell bits: **[31:24]=blocked/visit mask**
  (0x10000000=too-low, 0x08000000=too-high, 0x80000000=chosen mount, 0xff000000=impassable); **[23:0]=cost**.
- **A "wall" cell = terrain-height nibble step > 2 vs its neighbour** (`0041c4b0`): a cliff/step too tall to
  floor-walk → must be climbed. Heights from the same BDB aux (`FUN_00595e40`/`00424470`).
- Cell↔world: col=round(x·8), row=round(y·8), Z uses round(z·4) (DAT_007374a0=4); flat idx = col·64+row.
- BFS = Dijkstra from the goal cell (FIFO frontier `map[8]`, grows +0x1000), **4 weighted cardinal neighbours
  + 1 long-reach 5th pass**; costs = `{5.0,2.0,1.0,4.0} × 256` = `{1280,512,256,1024}`. Mount/dismount cell =
  nearest free cell via `0041bbd0` (**horizontal ±0x10=16**) / `0041bde0` (**vertical ±0x100=256** — a pit
  spans many rows but stays in ~1 column, hence anisotropic). Output = 16-byte `{x,y,z,flag}` waypoints, world
  = cellInt·0.125, **flag uniformly 2** (NOT move-type codes), lifted `y += 0.125` while still a wall cell.

### 5.2 CAct_Climb executor
`0040bb70` mirrors the segment's waypoints into 16-byte records (world = int·0.125, flag 0). `0040bf80` tick:
fail (state 3) if `obj[+0x7b0]==0`; else spawn a per-waypoint `CAct_WalkAnim` (root-motion mover) toward each
record via `0040be90`. **Arrival eps = 0.09 (0.30 world)** (DAT_0073702c). Climb-end Z offsets: **up→+14.5**
(DAT_00737564), **down→+12.5** (DAT_00737548), chosen by the sign of the vertical delta. **Per-leg speed =
CAct_WalkAnim root motion** (no fixed climb speed, no 0.47 factor). Clip selection (up/down/lateral/mount) is
inside CAct_WalkAnim from grid-step axis (row=climb, col=lateral) + vertical sign — clip-NAME table UNRESOLVED
(lives in WalkAnim/animset, not the 8 climb funcs).

### 5.3 The current `ClimbAction.cs` diverges (must rewrite)
Invented mount→crawl→climb choreography; fabricated `Kind{Mount,CrawlLeft,…}` codes (original flag is uniformly
2; the 0/3/4/5 are the CSubAction *state* enum); Z stacking via wall-normal instead of `y+=0.125`+14.5/12.5;
hand-tuned `height*0.47` speed instead of root motion; arrival 0.12 vs 0.30; faces wall-normal instead of the
height gradient.

---

## 6. DIG-TARGETING & STANDOFF — tile selection followed by sub-cell refinement

Native workers (wrapper → worker): `get_hmap`→**FUN_004093f0**; `get_digedge`→**FUN_005981d0**;
`dig_next`→**FUN_00597b20**; `vector_fixdig`→**FUN_00594e00**; `get_gnomeposition`→**FUN_0068d8a0**; rim helper
**FUN_005968b0**; precondition **FUN_005976b0**.

- **get_hmap (FUN_004093f0)** = PER-SUB-CELL height: `aux[(subY&7)<<3|(subX&7)]` of `block_idx*0x59`. Inputs are
  `round(world·8)` sub-cells. The port collapses to **per-tile MAX corner** (`AuxMaxCorner`) → a wall reads 15
  everywhere → `dig_check`'s `get_hmap<13` count = 0 → fails all 9 tries → dwarf never digs. **Fix:**
  `GetHmapSub(sx,sy) = AuxAt(BlockIdx[(sy>>3)*W+(sx>>3)], (sy&7)<<3|(sx&7))`; pass `round(x·8),round(y·8)`.
- **get_digedge (FUN_005981d0)** = TILE BFS from the marker, expanding only from tiles with the player's proper
  dig-mark bits in `tile+0x08`; nibble bit2 is visited scratch and is cleared on exit (`0x598424-0x59860d`). A
  dequeued tile is a candidate when template record byte0 `<0x0c` and `tile[0x34+player]!=0`; rank by squared
  tile distance to the dwarf with strict `<` (`0x59836f-0x598408`). It returns a tile, not a subcell, and never
  reads the nav plane or `+0x12`. `vector_fixdig`/Calc1 perform the later subcell refinement.
- **vector_fixdig (FUN_00594e00)** = step 1 unit off the wall toward the dwarf → spiral-snap to nearest WALKABLE
  sub-cell (`+0x3c & 1`) → Z = sub-cell heightmap+1 (≤14.5). Result is sub-cell-accurate (`·0.125`).
- **dig_next (FUN_00597b20)** = nearest dig-marked, precondition-passing voxel; Z = **rim+4** (rim from
  FUN_005968b0 = max of 4 neighbour gate bytes aux[0x50..0x53]); clamp [1,14].
- **precondition (FUN_005976b0)** = `tile[+0x18] & 3 ∈ {1,2}` (partially exposed) + a valid rim + visibility.
- **get_gnomeposition (FUN_0068d8a0)** = 0 on floor / 1 on wall, from `+0x3c & 2` at the dwarf sub-cell ±Y. Port
  hardcodes "0" (floor-dig only; wall-dig anims never play).

**Root cause one-liner:** the carve/fixdig gates need the **8×8 per-sub-cell BDB heightmap + authored nav plane**,
while get_digedge needs the marked-tile graph, BDB record byte0, and per-player FoW. The old port conflated those
layers and substituted a per-tile max plus an unbounded rock flood. The 8×8 aux was already loaded
(`BdbData.PerBlockAux`); the fixed pipeline keeps each native field in its own consumer.

---

## 7. TCL ↔ ENGINE MOVEMENT CONTRACT (what the unchanged scripts require)

| Tcl cmd | Native | Returns / contract |
|---|---|---|
| `action <o> walk "<opts>" <fin> <brk>` | `00405e10`→`00407430`/`00420840` | builds the walk; sets gnome+0x248=2; finishcode fires on STOP (arrived or failed) — script disambiguates via get_walkresult |
| `get_walkresult <o>` | `FUN_0068fb80` | int gnome+0x248 (see §3.4). **Tcl branches ONLY on 4 (arrived) and 2 (walking).** |
| `get_gnomeposition <o>` | `FUN_0068d8a0` | 0=floor, 1=wall |
| `get_pathlength <o>` | `FUN_0068d710` | **disabled** in the original (raises an error) — scripts use the Tcl `planned_pathlength` global + `note_pathlength` proc instead |
| `get_digedge / dig_next / vector_fixdig / get_hmap` | §6 | dig geometry |

Tcl wrapper procs (in `z_dignwalk.tcl`, supplied by the scripts — the port does NOT implement them): `walk_action`
(injects `-animsets`/`-speedtype`), `walk_pos`, `walk_pos_dig` (`-dig 1 -digpos`), `walk_dummy` (object+linkpos
standoff), `walkfail` (`if {4 != [get_walkresult this]} {tasklist_clear; gnome_failed_work; state idle}`).
`-canclimb 0` only for box-carriers (`:613,837`). `-target` is **native world float** (port already converts via
`NativeToLocal`).

**Port bugs to fix:** (1) `WalkResultAction` uses **1** for walking — must be **2** (scripts do
`==2` busy-checks); (2) no-path returns **2** — must be **3**; (3) `-dig` not honored (must bypass floor-snap);
(4) `-dummy` standoff approximate (needs link-point offset); (5) `get_gnomeposition` stub; (6) `-canclimb`
ignored.

---

## 8. PORT GAP ANALYSIS — what exists vs what to build

**Already in the port (reuse):**
- BDB per-sub-cell heightmap is loaded: `BdbData.PerBlockAux` + `AuxAt(blk,off)`, `AuxStride=89`. Just needs a
  `GetSubCellHeight(sx,sy)` accessor mirroring FUN_004093f0.
- The Tcl event/state/tasklist substrate, `action walk` plumbing (`BuildMetaWalkFromOptions`), `WalkResultAction`,
  the native↔world coordinate conversion (`RuntimeWorldSpawner.NativeToLocal`).
- The old `Pathfinding/` Calc1 C# (deleted, in git history `fa191cc~`) — the 4-variant Dijkstra + relaxation +
  cost is mostly correct as an algorithm; resurrect and CORRECT it per §2 (tile resolution, +0x12&0x40 gate
  direction, cost-by-owner, FUN_005a6350 derivation).

**To build / restore (the data layer is the foundation):**
1. **Sub-cell passability** — load the PMP authored-navigation RLE verbatim into an 8×8-per-tile `byte[]`
   (sparse is correct). Runtime name: `PmpData.AuthoredNavigationPlane`; `SolidityVoxels` is a compatibility alias.
2. **EFFBLOCK derivation** — port FUN_005a6350: per tile compute `BaseCost (-1/20/50)` and preserve
   `ExitPairAndFlags` as packed `{subX bits0-2, subY bits3-5, flags bits6-7}`.
3. **Per-sub-cell height accessor** — `GetHmapSub`; fix `get_hmap`/`vector_fixdig`/`dig_next`/
   `get_gnomeposition` to be sub-cell-accurate (§6). `get_digedge` itself returns a tile and does not read the plane.
4. **Calc1 (corrected)** — tile Bellman-Ford on the derived graph; sub-cell endpoint snapping; output polyline
   via each tile's packed exit pair.
5. **MetaWalk** — segment classify/group/dispatch; the §3.4 WalkResult enum (fix 1→2, 2→3).
6. **WalkFloor** — root-motion-coupled speed; floor-height-follow (kill the straight Y-lerp); turn/start/stop
   clips.
7. **Climb** — rewrite `ClimbAction.cs` as the FloorPathMap BFS + interpolating executor (§5); drop the invented
   choreography.
8. **Dig standoff** — native marked-tile get_digedge plus sub-cell fixdig (§6) so the dwarf reaches the local mouth.

---

## 9. Constants
| Const | Value | Role |
|---|---|---|
| DAT_0073752c | 8.0 | world→sub-cell scale |
| DAT_0073703c | 0.125 (1/8) | sub-cell→world |
| DAT_007374a0 | 4.0 | world→climb-grid Z |
| DAT_0073708c | 0.5 | **Z/height weight in all dist metrics; Z=nibble·0.5+7** |
| DAT_00737520 | 7.0 | Z base |
| DAT_00737058 | 0.25 | anim-rootmotion→world speed scale |
| DAT_0073702c | 0.09 (0.3²) | arrival threshold² |
| DAT_007370d0 | π/4 (0.785) | turn-clip threshold (45°) |
| DAT_007371c0 | 1.5 | budget clamp / axis-dominance K |
| cliff penalty v1/v2 | 0x114=276 / 0x10a=266 | edge cost |
| elevation step | 0x10=16 | edge cost |
| tile stride | 0x7c=124 | tile array |
| BDB aux stride | 0x59=89 | height/geometry |
| climb costs | {5,2,1,4}×256 = {1280,512,256,1024} | FloorPathMap BFS |
| climb mount radii | ±16 horiz / ±256 vert | mount/dismount search |
| climb-end Z | 14.5 up / 12.5 down | DAT_00737564/48 |
| start/goal relax | 33 / 13 rings | Calc1 endpoint snap |
| queue cap | 0x400=1024 (+1024) | Calc1 FIFO |

---

## 10. UNRESOLVED (non-blocking; confirm during impl)
1. **Calc2 (`FUN_005a22f0`)** — the alt pathfinder gated by `owner[+0x7b4]`; campaign uses Calc1 (`0x7b4==0`).
   Reverse only if a case sets it. Next: structurally diff vs `0059d640`.
2. **FloorPathMap BFS exact 5-pass (Δcol,Δrow,cost) tuples** — the *set* is {(±1,0),(0,±1),(±2 half-steps)} with
   costs {256,512,1024,1280}; the exact per-pass mapping is obscured by Ghidra pointer-arith. Next: hand-trace
   `0041d8a0:468-636` or single-step.
3. **Climb clip-NAME table** — direction→clip lives in `CAct_WalkAnim` (`00417ba0`), outside the climb funcs.
4. **+0x12 bit 0x80 writer/meaning** — EFFBLOCK does not set it, but saves preserve it verbatim. Bits 0x04/0x20
   are now resolved as packed subX/subY coordinate bits, including in Variant-B's native `0xA4` left mask.
5. **EFFOBJPF object bit 0x04 semantics** — `FUN_005a5da0` replaces plane bits2/3 from object data; bit3 is
   EFFBLOCK passage, while the distinct runtime meaning of bit2 still needs the relevant object reader.

---

## 11. Implementation plan (phased — each phase compiles + is verifiable)

**P0 — Data layer (the foundation; nothing works without it).**
Thread PMP `AuthoredNavigationPlane` → runtime 8×8 sub-cell passability; add the exact FUN_005a6350 derivation
(`BaseCost` plus packed `ExitPairAndFlags` per tile); add `GetHmapSub`. *Verify:* a debug probe prints, for the
urw_unq_start area, the same sparse walkable shelves the live dump shows; `get_hmap` returns per-sub-cell 0..15.

**P1 — Calc1 (corrected) + endpoint snapping.**
Resurrect/correct the deleted Calc1 (tile Bellman-Ford, +0x12&0x40 gate, cost-by-owner, relax). *Verify:* EditMode
tests — a path along a known shelf matches the live route shape; a click on rock snaps to the nearest shelf.

**P2 — MetaWalk + WalkFloor (floor walking end-to-end).**
Segment dispatch + the §3.4 WalkResult enum (fix 1→2, 2→3) + root-motion speed + floor-height follow + turn/
start/stop clips. *Verify:* click-to-walk hugs the terrain on cross-Y moves (no clipping); walk/run speeds
read right; `get_walkresult` gates the Tcl loops correctly.

**P3 — Dig standoff (unblock digging).**
Marked-tile get_digedge plus sub-cell vector_fixdig/dig_next/get_gnomeposition (§6). *Verify:* a dwarf walks to the foot of
a cliff and digs (no edge jitter); `dig_check.freeneighbours ≥ 2`.

**P4 — Climb (rewrite).**
FloorPathMap BFS + CAct_Climb interpolating executor (§5); retire the synthesized choreography. *Verify:*
descend a pit / climb a cave wall along the original route shape; mount/dismount at the nearest free cell.

**P5 — Polish + the `-dig`/`-dummy`/`-canclimb` honoring + auto-retry layer.**
*Verify:* box-carriers don't climb; harvest `walk_dummy` reaches the standoff; stuck walks re-path.

> ✅ DONE: the CUSTOM mesh nav-graph (`NavGraph*.cs` / `WalkClimbAction.cs`) was RETIRED and DELETED.
> `Runtime/Navigation/` now holds this faithful pipeline (`NavWorld` / `Calc1Pathfinder` /
> `FloorPathMap` / `NavWorldService` / `NavWorldGizmo`); the movement actions live in `Runtime/Tcl/Engine/`.
