# Digging — full RE of the original mechanic + the faithful port

> Native nav/get_digedge portion for `nav-native-law`; other sections were last
> verified against code. All port-side symbols cited
> below exist and match: `DiggableTerrain.{TileMaterialType, CarveStep, CarveTileNow, ApplyDig,
> MarkRimGate, RimAtNode}`, `BdbData.{AuxTrueCornerNW/NE/SW/SE, AuxCornerNeighbour, CornerPatternBlock}`,
> `ClickToMoveService.{Ctrl-click CarveTileNow, CellAllowed}`, `CaveStamp.Commit`,
> `DigglesTclEngine` `dig_apply`→`CarveStep`, and the repro test
> `CaveDigReproTests.CaveStampDig_DigNextOrder_DiggerStaysPathable_AndCompletes`.
>
> Ground-truth reverse-engineering of how Diggles (2001) digs out a terrain block, and how the Unity
> port reproduces it. Sources: the decompile, symbols
> `Diggles.symbols.tsv`, original Tcl `original/data/Scripts/classes/zwerg/*`, and a byte-level probe of
> `original/data/BlockDB2.BDB` (`tools/probe_dig_graph.py`, `tools/probe_patterns.py`). Every claim below is
> cited or measured. **This supersedes `REGRESSION_PROBLEMS_.md` Problem 4's premise** that the
> `block_idx` resolver `FUN_0058a8b0` is "un-reversed" — it is now reversed, and that function is *not* the
> resolver (it is cosmetic). See §5.

> **Native-law closu (`nav-native-law`).** The stamp trailer and dig-edge
> selector are now instruction-backed, not inferred:
>
> - PMP RLE words expand directly to `value=word&3`, `run=word>>2` (`0` means
> `0x4000`) and are blitted verbatim into `tile+0x3c` as the initial seed
> (`FUN_005992b0:538-544`, `:563-567`; `FUN_00596550`). The direct stamp
> `0x40000` composite schedules EFFOBJPF+EFFBLOCK+render/minimap, not bit0
> (`FUN_005a3fd0:36-41`), while the later full `0x5f` validation includes bit0 and
> the runner orders EFFPASSWALK→EFFOBJPF→EFFBLOCK
> (`FUN_005931f0:246-252`; `FUN_005a46d0:112-266`). Both TrollQuest seam PMPs have
> zero authored bytes at y=122..126, and the selected BDB detail there is solid.
> The seam has no initial native route: direct live-stamp EFFBLOCK writes `-1` from
> that empty plane (`FUN_005a6350:236-245/:290`). It is a designed dig-through;
> `dig_apply` changes the BDB block ids and the dig composite then derives the opened
> plane. No carry, endpoint bridge, or duplicated object delta participates.
> - EFFPASSWALK (`FUN_005a6b40:105-523`) requires its exact 31-probe gnome capsule to
> have detail `<0x0e`, gates on the `((sx+4)>>3,(sy+4)>>3)` tile's `+0x18 bit7`,
> then emits 0/1/3 from the y-1/y-11 and y+1/y-10 detail-jump law. A carve's
> `0x10000` composite schedules bits0|1|2 on the expanded dirty rectangle
> (`FUN_00596b80:74-118`, `FUN_005a3fd0:42-48`). Its gather follows the current
> node `+0x00` block id (`FUN_00595e40:69-77`) after the carve swaps that id through
> the BDB transition graph (`FUN_00596b80:64-134`); there is no cached pristine-detail
> source. Full dispatcher table and exact
> offsets are in `ORIGINAL_PATHFINDING_RE.md §1.5`.
> - EFFBLOCK writes `+0x06=50` only for a valid walk cell with neither floor nor object
> passage; either category writes `20`, while no walk cell or the solid-window gate
> writes `-1` and leaves `+0x12` stale (`FUN_005a6350:236-290`).
> - `get_digedge` (`FUN_005981d0`, capstone range `0x5981d0-0x5987b3`) is a
> four-neighbour BFS from the marker. It spreads only through this player's non-scratch
> dig-mark bits in `tile+0x08`; bit2 of the nibble is visited scratch and is cleared from
> every queued tile before return (`0x598424-0x5985a4`, cleanup `0x5985b4-0x59860d`).
> - A visited tile is a candidate only when its template record byte0 is `<0x0c`
> (`0x59836f-0x5983a4`) and its per-player FoW byte is nonzero
> (`0x5983b2-0x5983df`). Candidates are ranked by squared Euclidean distance to the
> gnome, with strict `<` so BFS order wins ties (`0x5983e1-0x598408`). The function
> reads neither the nav plane nor `+0x12`.
> - The Tcl wrapper passes the marker as seed and rounded gnome tile as the ranking
> reference (`FUN_00635180:153-158`); the work-point caller uses marker for both
> (`FUN_0043bdd0:241-245`). Output is a tile (or `{-1,-1}`), with wrapper Z=14.
> Equal-distance ties keep the first BFS candidate: native enqueue order is east,
> west, down, up (`0x598424-0x5985a4`).
>
> Covered by `StampedTemplateNavigationTests`, `DigEdgeBfsTests`, and the optional live
> a mod-campaign boot fixture.

---

## 0. One-paragraph summary

A dig is a flag/tasklist flow in Tcl that bottoms out in the native command **`dig_apply $digpoint $gnome`**.
`dig_apply` (`FUN_006347a0`) rounds the point to a tile and calls the carve **`FUN_00596b80`**, which mutates the
world tile array: it **re-resolves the `block_idx`** of the dug tile and its neighbours by walking a **pre-baked
block-transition graph stored in each BlockDB block's 89-byte aux** (`aux[0x40..0x4c]` = 4 neighbour-block
indices), and clears completed owners' dig nibbles. The same per-tile mesh builder then re-runs over the dirty
tiles; the dug tile becomes a **flat-floor block (family of 59, corners 0 → z 7.0)** and the bordering solid tiles
become **cliff-edge blocks (55/56/57/58/60/74…)** whose authored cell geometry *is* the vertical wall. No special
"pit object", no procedural wall synthesis, no hardcoded constant — the carve is a table-walk over the block
catalogue, and the renderer draws whatever blocks result.

---

## 1. The Tcl flow (order → tasklist → carve)

Files: `classes/zwerg/z_events.tcl`, `z_dignwalk.tcl`, `z_procs.tcl`, `z_work_states.tcl`.

1. **Order.** `set_event <z> evt_task_dig -pos1 {tx ty}` → `evt_task_dig_proc` (`z_events.tcl:1019`): clears the
   tasklist, sets `current_workplace="dig"`, pushes **one** task `dig_starttask {clickpos}`, then
   `state_triggerfresh this work_dispatch`.
2. **Dispatch loop.** `state work_dispatch` (`z_work_states.tcl:13-22`): each tick pops task 0 and `eval`s it.
3. **Start.** `dig_starttask` (`z_dignwalk.tcl:136`): `get_digedge` finds the wall edge (validity = packed-vec
   `vector_unpacky > 0`), pushes `walk_pos_dig {walkpos} {edgepos}` + `dig_continue`.
4. **Walk + iterate.** `walk_pos_dig` (`:705`) runs `action this walk … -dig 1`. On arrival `dig_continue` (`:205`)
   picks the next voxel `dig_next` (sentinel `-1 -1 1` = none), gates it with `dig_check` (>1 neighbour cell with
   `get_hmap < 13`), and queues `dig_anim {digpoint}`.
5. **Carve.** `dig_anim` queues an animation proc (`play_anim`/`wait_time_dig`, `z_procs.tcl:180-212`) whose action
   finish-callback runs **`dig_execute {digpos}; dig_continue`**. `dig_execute` (`z_dignwalk.tcl:295`) loops
   `dig_apply $digpoint this` while in range; on success it grants exp (`add_attrib exp_Stein`, tunes
   `misc/techtreetunes.tcl:1356`) and ~1–4 % spawns ore (`new {Stein|Eisenerz|Kohle|Golderz|Kristallerz}`,
   `:328-372`). **`dig_apply` is the only world-mutating call; the Tcl only adds the reward layer.**

### Engine-command contract (which natives must be real)

| Command | Native? | Mutates / Cosmetic | Note |
|---|---|---|---|
| `dig_apply $pt $gnome` | native `FUN_006347a0` | **MUTATES** | THE carve. Returns 1 if a voxel was removed. |
| `dig_next`, `get_digedge` | native | read-only | `dig_next` returns a candidate voxel; `get_digedge` returns the selected tile as `{x y 14}`; `vector_unpacky>0` = valid |
| `get_hmap x y` | native `FUN_0062faf0`→`FUN_004093f0` | read-only | sub-cell height 0..15 from the block's intrinsic 8×8 aux; `<12/<13` = open |
| `get_material $pt` | native `FUN_00630bb0` (mislabeled "GroundPos" profiler tag; name↔handler pairing at exe VA 0x61b610/0x61b624) | read-only | returns `tile+0x18 & 3` as a Tcl INT (material type, see §2). THE per-swing pace multiplier: `rand()<digcount*mat*0.7` (z_dignwalk.tcl:315) — earth(2) digs 2× faster than sand(1); fresh dwarf: digcount 0.5 ⇒ P=0.7 on earth. The port's pre-07-06 float (0.6/0.85/1.0) under-drove the pace ~2.4× (playtest 07-05 D1) |
| `get_gnomeposition` | native | read-only | 0 = digging on the ground, ≠0 = on a wall |
| `get_walkresult this` | native | read-only | 4 = arrived (the `walkfail` gate); **must not be a "0" stub** |
| `dig_mark` | native | MUTATES (order layer) | AI/trigger paint; the *user* path doesn't use it |
| `prod_gnome_state`, `gnome_announce_dig` | native | COSMETIC | HUD/status; stub-safe |

Diggability is decided by (a) `get_digedge`/`dig_next` returning a valid vector, (b) `dig_check`'s
`get_hmap<13` neighbour count, (c) `get_material>0`, and ultimately (d) `dig_apply` returning 1. There is **no
`get_diggable` command** (absent from the engine + proc tables and the whole script tree).

### 1.1 Native `get_digedge` law (`FUN_005981d0`)

Inputs are tile coordinates: marker seed, ranking reference (the gnome for Tcl), and player channel. The seed
must lie in the one-tile inset (`0x598222-0x598258`), otherwise the result is `{-1,-1}`. Native then:

1. Sets visited scratch `4 << (player*4)` in the seed's `tile+0x08` nibble and performs a four-neighbour BFS.
2. Scores every dequeued in-inset tile whose template record byte0 is `<0x0c` and whose
   `tile[0x34+player]` FoW byte is nonzero (`0x598310-0x59840d`). A candidate need not itself retain a proper
   mark: this is how the open frontier adjacent to the marked region participates.
3. Expands neighbours **only** from tiles carrying a proper player mark (`0x0b << (player*4)`), setting the
   scratch bit once when each neighbour is queued (`0x598424-0x5985a4`). It never floods through arbitrary
   diggable rock.
4. Keeps the smallest `(tileX-gnomeX)^2 + (tileY-gnomeY)^2`. The compare is strict `<`, so equal-distance
   candidates retain BFS order (`0x5983e1-0x598408`).
5. Clears the scratch bit on every queued tile before returning (`0x5985b4-0x59860d`).

The only world fields read are template index→record byte0, the player's dig nibble, and that player's FoW
byte. Plane bits and the EFFBLOCK `+0x12` packed pair do not participate (`FUN_005981d0` full disassembly).
This marked-flood plus gnome-distance ranking is the vertical-shaft rule: the solid marked column conducts the
search, while the already-open shaft mouth nearest the gnome wins instead of sending the gnome away first.

---

## 2. The native carve `FUN_00596b80` (called by `dig_apply` = `FUN_006347a0`)

`dig_apply` (`_global/006347a0…`): parses the digpoint vector, `x=round(px+0.5) y=round(py+0.5)`, reads the gnome's
layer `gnome+0x36c`, calls `FUN_00596b80(&pos, gnome)`, returns its boolean. **The cosmetic re-skin
(`CMapBeautifier::ScanAtPos`) + dig-marker re-mesh run only for the local human player** (`:112,132`) — a headless
sim can skip them.

**Tile struct = 124 bytes (`0x7c`)** (`FUN_00592f10` lookup `base + ((y*stride+x)%count)*0x7c`):
- `+0x00 u32 block_idx` — index into BlockDB cells. THE render/mesh key.
- `+0x04 u16` — mesh cache handle (`0xffff` = rebuild).
- `+0x08 u32` — **live DIG orders: 8 owners × 4-bit nibble** (bit `0x8` selects
  the cave-depth encoding; low bits select the target; zero means no order for that owner).
- `+0x13 u8` — dirty bits (`|1` geometry, `|2` relight).
- `+0x14 u16 cellId` — L2M material/scape (bits 5..7 = urw/swf/kris/lava).
- `+0x18 u16` — **low 2 bits = the tile MATERIAL TYPE** (`&3`: 0 = STONE/granite, 1 = sand, 2 = earth,
  3 = fully open/scapehole; official API `ENGINE_TCL_API.txt:494`). CORRECTED — the old
  "exposure" reading (0 = fully buried) was wrong: the field is PAINTED at load (map-create default 2,
  `FUN_00598f10:154`; PMP terrain byte 2/3/4/5 → type 2/1/0/3 via the loader's rewrite+probe,
  `FUN_005992b0:150-216/242-377`; lg stone paint `FUN_005fc9d0:115-122`) and never rewritten by the
  carve. It is the dig PRECONDITION gate (`FUN_005976b0:36-38` requires ∈{1,2}), `get_material`'s
  return value (`FUN_00630bb0:93-94`), and the marker validity band (tunnel `FUN_00566870:26-28`,
  painters `FUN_005672d0:41-44`/`FUN_005671a0:33-38`, cave stamp `FUN_00566a60:132`).
  Port model: `DiggableTerrain.TileMaterialType`.
- `+0x3c..0x7b` — current per-tile 8×8 navigation plane (PMP 0/1/3 seed, then EFFPASSWALK derive;
  bit0 walk, bit1 floor).
  Per-subcell dig heights are in the selected BDB template's 89-byte auxiliary record, not this field.

**`FUN_00596b80`:**
1. Bounds + precondition `FUN_005976b0` (`:42-47`).
2. Decode the center's current dig height from `tile[0x08]>>(owner*4)` (`:48-61`): solid→15, marked→11, open→sub.
3. Compare against the **rim height** `FUN_005968b0` (max of neighbours' resolved rim bytes) (`:62`).
4. **Re-resolve `block_idx`** for the tile + its directional neighbours (`:64-119`): for each, read its current
   block's aux at offset `0x4c/0x48/0x44/0x40` (the 4 neighbour-block indices); if `≠ -1` and the per-direction
   gate `aux[0x53/0x52/0x51/0x50] == rim_layer`, adopt it. Write the results + set `tile[0x13] |= 1` (`:120-136`).
5. Clear the dig nibbles of upper layers below the rim (`:137-158`).
6. Effects + regional re-mesh (`FUN_005a3dd0/5fd0`) + recompute hmap (`:159-256`).

Each `dig_apply` is **one step** (drops a block by ~one elevation level via the graph); the gnome's `dig_execute`
loop repeats it to fully excavate. The terminal state of a fully-dug column is the flat-floor block; its bordering
solids become cliff blocks.

EFFPASSWALK consumes those changed ids directly: `FUN_00595e40:69-77` gathers the first 64 detail bytes of the
current block's 0x59 record for every touched tile. The nav plane is then overwritten from that live detail. In
the real TrollQuest seam, one node column creates an eight-subcell slot, narrower than the native probe span
`dx=-7..+7`; two adjacent node columns create the 16-subcell opening that the gnome capsule can traverse. This
is why the acceptance dig marks/carves both columns while still asserting no route before the carve.

---

## 3. The pit MESH (engine vs our port)

The engine has two emit paths in the per-tile renderer `FUN_004ca830`:
- **GetBlock** (normal): builds 3 sub-cell meshes from `block.cells[3]`; each vertex is fetched from the global
  palette `DAT_00b836a4` (5 floats x,y,z,u,v; **z pre-baked in [7.0..14.5]**, empirically confirmed in BDB2);
  `world.xyz = palette.xyz + (tile_x, tile_y, 0)`.
- **GetBlockHole** (`FUN_004ca080`): the cliff path, triggered when `|aux_corner − aux_center| > 4`. It clips the
  block's **pre-authored side faces** against the 4 neighbour planes (`FUN_004c9010`, clip plane at 0.5) and emits
  the vertical wall. Elevation `z = (nibble)*0.5 + 7.0` (`DAT_0073708c=0.5`, `DAT_00737520=7.0`).

**Our port does NOT need GetBlockHole.** It meshes each block's 3 cells directly (the GetBlock path); the cliff-edge
blocks (55/56/…/74) carry their wall side-faces *in their own cells* (e.g. block 56 cell0 = 8 verts = the wall), so
re-tiling a neighbour to a cliff block makes the existing renderer draw the wall. Walls are darker in the original
because of per-vertex corner-luminance + height fog (`FUN_004cc040`); the floor/walls use the **same ground
multi-texture** (no separate pit material) — scape atlas keyed by `cellId` bits 5..7.

---

## 4. BlockDB ground-truth (measured from `BlockDB2.BDB`, 118 688 blocks)

89-byte aux per block: `+0x00..0x3F` = 8×8 heightmap (0..15); corners NW/NE/SW/SE = `+0x00/+0x07/+0x38/+0x3F`,
centre `+0x24`; `+0x40..0x4F` = **4×u32 neighbour-block transition indices** (`0xFFFFFFFF` = none — verified: every
slot of every block is `-1` or a valid index); `+0x50..0x53` = 4 corner/gate bytes; `+0x54` low nibble = variant.
The aux table is **byte-identical across BlockDB0/1/2** (same topology, 3 mesh-detail tiers; **DB2 is richest**).
Elevation `z = nibble*0.5 + 7.0`.

| block | role | corners NW/NE/SW/SE | nb `aux[40/44/48/4c]` |
|---|---|---|---|
| 0 | solid earth | 15/15/15/15 | 54,44,32,22 |
| 59 | **flat floor** (dug) | 0/0/0/0 | 59,59,59,59 |
| 56 | straight wall (S open) | 15/15/0/0 | 94760,95169,56,56 |
| 74 | straight wall (E open) | 15/0/15/0 | 89414,74,5446,74 |
| 55 | inside corner | 15/15/15/0 | … |
| 58 | outside corner | 15/0/0/0 | … |

Block 0's transitions drop **one corner by one level** (→ 54 = 11/15/15/15 etc.): the carve is incremental. Block
59 is **not empty** — it has the same 4-vertex floor quad as block 0, just at z 7.0.

### Open-corner-pattern → block (the table our port builds, `tools/probe_patterns.py`)

Bit NW=8 NE=4 SW=2 SE=1; set = corner OPEN (height 0), clear = SOLID (15). Lowest-index match:

```
0→0(keep) 1→55 2→57 3→56 4→5412 5→74 6→100287 7→58 8→5410 9→(none) 10→75 11→60 12→71 13→5385 14→5384 15→59
```

15 of 16 patterns have a pure-0/15 block; only `0b1001` (two diagonal corners) has none, which a contiguous dig
never produces.

---

## 5. Correction to the existing docs/memory

`REGRESSION_PROBLEMS_.md` Problem 4 and the `voxel-world-model` memory said the `block_idx` resolver is
`CMapBeautifier::ScanAtPos` (`FUN_0058a8b0`) and is "not yet reversed", proposing a `block_idx=59` + synthesized-quad
workaround. Ground truth:
- The real resolver is the **aux neighbour-transition graph walked inside the carve `FUN_00596b80`** — fully
  reversed and confirmed against the BDB bytes.
- `FUN_0058a8b0`/`FUN_0058b130` (`ScanAtPos`) is **cosmetic only** — the map-beautifier DECO pass, not the geometry
  resolver. (correction: its templates are the LEVEL-GEN template shapes on the beautifier's `+0x1a4`
  table, NOT `cave0..3.tga` — those TGAs are the cave STAMP masks loaded by `FUN_005661c0`. Full law in §9.)
- "set block_idx=59 + synthesize walls" is therefore **not** the faithful path; the faithful path is the
  block-transition (floor + cliff blocks from the catalogue), which is what we implemented (§6).

---

## 6. The port implementation (faithful carve)

**Mechanism:** a tile's rendered block is chosen by its 4 corner heights; the world is a grid whose corners match
across edges. Digging = recompute the dug tile + the tiles sharing a corner with it, selecting the game's OWN BDB
block for each tile's open-corner configuration (the terminal effect of the aux graph). The same renderer re-runs.

**Files changed:**
- `Runtime/World/BdbData.cs` — `AuxTrueCornerNW/NE/SW/SE` (true 8×8 corners `0x00/07/38/3F`) + `CornerPatternBlock(openMask)`
  building the 16-entry pattern→block table once from the BDB.
- `Runtime/World/DiggableTerrain.cs` — `CarveOut`/`CarveTileNow`/`ApplyDig` now do the faithful transition: dug
  tile → flat floor (59, marked `_dug`), each bordering solid tile → its cliff-edge block via `CornerPatternBlock`
  of the tile's open-corner mask (`OpenMaskFor`/`NodeOpen`/`IsOpenColumn`). `_wasOpenAtLoad` protects source-open
  space. Shares `_dug` to `TclWorldResult.DugFloor`.
- `Runtime/World/BdbPmpWorldBuilder.cs` — `RenderWorldArrays` takes `bool[]? dugFloor`; block 59 is skipped ONLY
  when it's load-time air (not a dug-floor tile). `RemeshFromArrays` threads `r.DugFloor`.
- `Runtime/Engine/ClickToMoveService.cs` — **Ctrl + left-click** on diggable terrain → `CarveTileNow` (test dig, no
  dwarf needed). (Shift+click still issues the real dwarf dig order via `evt_task_dig`.)
- `Runtime/Tcl/Engine/DigglesTclEngine.cs` — `dig_apply` now reads its `$gnome` arg (no longer silently dropped)
  and routes to the faithful `ApplyDig`, so the gnome dig loop also produces real depressions.

> **REVISION: single-step, not terminal.** The first implementation jumped a
> dug tile straight to floor (block 59) via a corner-pattern table → it "dug full depth instead of one block" and
> ignored existing open neighbours. Replaced by the faithful **single-step aux-graph carve**: each dig lowers the
> surface by ONE rim level (15→11→7→3→0) via `BdbData.AuxCornerNeighbour(blk,corner)` (the real `aux[0x40..0x4c]`
> graph, verified: blk0 NW chain 0→54→34511→39259→47449(0)). The carve is per grid NODE — `DigNode` steps the
> shared corner of the ≤4 tiles meeting there, so the mesh stays seamless and the surroundings slope/cliff into the
> dig. **One click = ONE node** (`CarveTileNow(nx,ny)` = the atomic `dig_apply` unit); repeat to deepen to floor 59.
>
> **Footprint = minimum 2×2 tiles; true 1×1 is geometrically impossible here.** A grid corner is shared by 4 tiles,
> so the smallest dig (one node) touches a 2×2 of tiles (one corner each). The original digs 1×1 only because it
> works at 8×8 SUB-CELL resolution per tile — which our tile-resolution port dropped (the nav pivot discarded the
> PMP 8×8 sub-grid). Finer-than-2×2 digging would need that sub-grid restored.

**How to test:** Play `RuntimeWorldGeneration.unity`, get into the world, **Ctrl + left-click a solid wall** →
the nearest grid node lowers one level (a small dimple over the 2×2 of tiles around it); click repeatedly to deepen.

---

## 7. Cave vs standard tunnel: target depth, not stacked voxels

There is one terrain-carve primitive. The visible difference between a cave-depth order and a
standard roofed tunnel comes from the mark's target and the authored BDB transition shape:

- mode 1 writes raw nibble `3`, which decodes to target `11` and lowers the
  shared-node rim by one transition;
- mode 0 writes `(count|8)`, which selects targets `{0,3,7,11}` for the cave-depth tools;
- `tile+0x08` contains eight owner nibbles. It does not contain eight spatial layers.

The executable and the port both store one `block_idx` per map tile. The three BDB cell records
for that block are co-rendered geometry/material submeshes, not alternate vertical terrain
states. `FUN_004ca830` reads the one block index and emits those three records; the port's
`BuildTileBuckets` does the same. A target-11 transition retains the front-rock lip and exposes
the authored wall submeshes, producing the standard roofed-tunnel profile without a second
surface. The port has used this native law since `8c51dc2`.

`GetBlockHole`/`CBlockHole` is a separate authored-scape cliff clipping path. Its exact clipping
and the cave-backdrop/cutout presentation may still need rendering work, but they are not a
stacked tunnel data model and must not be tracked as a `tile+0x08` voxel feature.

## 7b. cave-dig corrections

Re-verified line-by-line against the decompile while fixing the stuck-cave-digger cluster:

- **`dig_next` (FUN_00597b20) slot semantics**: the native keeps a top-N array (N = try+1)
  initialised to `{-1,-1,-1}` / cost 1e7 (:85-114) and returns slot N−1 (:245-262) — fewer than
  N candidates ⇒ the **sentinel**, which ends the Tcl loop (`unpacky>0` / `try<9`,
  z_dignwalk.tcl:236/:287). The port used to CLAMP to the last candidate → dig_continue's try
  escalation re-served the same dig_check-rejected voxel forever (the grinding cave gnome).
- **`dig_next` has NO exposure gate**: gate 2 (:128-129) is the STATIC material band
  `tile+0x18 & 3 ∈ {1,2}`. The progressive dig front comes from the **Tcl side**: `dig_check`
  (≥2 of 3 gnome-facing probes `get_hmap < 13`, z_dignwalk.tcl:163-181), the try escalation,
  and `dig_execute`'s range tether `dist(digpoint, pos+{0,-0.8}) < 3.0 + exp_Stein·2` (:313).
  The old port's `TileExposed` proxy in dig_next was invented and is removed.
- **Carve precondition** (FUN_005976b0 via FUN_00596b80:45-47): material ∈ {1,2} (:36-38) else
  the carve refuses AND clears the owner's nibble (:281-282); rim ≠ −1 (:41-45). Ported into
  `CarveStep`. (Its third leg — the object-standing scan — is still an open item.)
- **The painters' rim RE-MARK gate** (mode-1 FUN_005672d0:40-53, mode-0 FUN_005671a0:32-45):
  under the per-player explored flag `tile[0x34+player]` (no-fog port = always explored), a
  marker cell paints only while the node rim FUN_005968b0 (port `RimAtNode` = the level the
  NEXT carve step lands at: virgin 11, once-carved 7, …, floor −1) ≥ the FUN_00597430-decoded
  target of the nibble BEING PAINTED. Completed tunnel + new tunnel mark (target 11 vs rim 7)
  ⇒ silently dropped at commit — the "marks over already-dug tunnels" manual test symptom. Cave
  cells (targets 0/3/7) still paint over a carved corridor mouth. Painter-side per-cell drop,
  NOT a validity abort. Port: `DiggableTerrain.MarkRimGate` + `ClickToMoveService.CellAllowed`
  + `CaveStamp.Commit`.
- **Nav during partial digs**: the port re-syncs EFFPASSWALK→EFFBLOCK synchronously on every
  carve step (unchanged); the stuck-gnome nav loss was in the PATH ASSEMBLY over
  intermediate-depth terrain — (a) a climb→floor boundary claim fed the WALL record depth into
  the following floor run's FloorPathMap seed (unbridgeable vs the floor band; the dismount
  actually lands ON the ground line at support+1), and (b) Calc1's packed-pair-per-tile
  polyline hid vertical terrace ledges (with value-1 walkable non-floor cells on the face) inside a
  single "floor" run, whose banded columns the 5-pass flood can never join. Both fixed in
  NavWorldService (dismount re-seed + ledge splice). Repro:
  `CaveDigReproTests.CaveStampDig_DigNextOrder_DiggerStaysPathable_AndCompletes`.

## 8. Known simplifications / follow-ups

- ~~No rim/precondition gate yet~~ — DONE: `CarveStep` runs the FUN_005968b0 rim gate, the
  gate==rim transition accept, the completion sweep, and the FUN_005976b0
  material precondition. Still open from FUN_005976b0: the object-standing scan (deco/beam
  interaction — objects standing on a tile natively block/react to its carve).
- ~~Eight-layer roofed-tunnel voxel model~~ — **closed as a false premise**; see §7. The
  eight nibbles are owners, and the native/port target-11 BDB profile is the tunnel law.
- **Full-bbox re-mesh per dig** (debounced 0.4 s). Chunked re-mesh is the perf follow-up (roadmap M3).
- **Verify wall orientation in Play.** The node→corner mapping (`DigNode`: (nx-1,ny-1)=SE … (nx,ny)=NW, aux corners
  `0x00/07/38/3F`, gates `0x50..0x53`) is internally consistent; if a slope/wall faces the wrong way it's a one-line
  swap of the corner ids in `DigNode`/`StepCornerDown`.

---

## 9. The map beautifier — cave-skin prop swap + dig beams

One singleton (`DAT_00772414`, lazy accessor `FUN_0058a820`, ctor `FUN_00581470`) owns BOTH cosmetic dig-deco
systems. `DigApply` (`FUN_006347a0`) runs them around the carve, local-view only (`:112-118` before /
`:127-133` after), with the DIGGER's owner (`gnome+0x36c`).

**A. Cave-skin swap on cave COMPLETION — PORTED (branch fogofwar-1to1, this session).**
- Rect sources (one list, beautifier`+0x30`, stride 0x14 `{x1,y1,x2,y2,size}` + `FUN_0058eb30`
  overlap-drop on append): the cave STAMP commit (`FUN_00567cb0:204-214`, size = tool template idx `+0xdc`)
  and Tcl `cave_skin add <size> <x1> <y1> <x2> <y2>` / `rem <x> <y>` (`FUN_0066a9a0`; AI std_ai.tcl:3020/2527,
  Trigger_Schwefel.tcl:798). Port: `DiggableTerrain.CaveRecords`.
- Skin registry: `add_cave_skin_class <set> <detail> <size>` (`FUN_00617570`, runs inside `def_class`) sets the
  class flag `+0xcc2` (its ONLY setter; ctor default 0, `FUN_00601250:88`) + appends a rule `{set,size,detail,cls}`
  (beautifier`+0x28`). Data: `SetCaveSkinClasses` (animinit.tcl:382-432) over dummy_auskleidung.tcl —
  `{Urw,Met,Kris,Golden}_caves_{1..4}_{variant a..c}_{detail a..c}`; set Urw/Met/Kris/Golden = 0..3, size
  caves_1..4 = 0..3, detail a/b/c = 2/1/0.
- Completion pass `FUN_0058ba00` (after-carve): first rect CONTAINING the dig node (inclusive `:72-95`) whose
  owner nibbles are ALL 0 over `[x1,x2)×[y1,y2)` (`:102-158`) →
  (1) remove every LIVE object of a `+0xcc2` class inside the rect (block sweep `:159-446`, inclusive float
  bounds `:397-406`) + purge its pending deferred spawns; (2) spawn detail slots 2,1,0 (`:232-249`) of the
  matching `{set = zone band at dig y (FUN_005fcdf0:211-228, <0→0), size}` rules at
  `((x1+x2)/2+1, (y1+y2)/2, z=15)` with ONE shared random variant pick (`FUN_0058e9c0` `&local_34`; detail
  gated vs the object-detail setting `FUN_0058c450:73-89` — port = max = all 3); (3) retire the rect
  (`FUN_0058f2a0`) and stop (one rect per dig).
- Port: `DigglesTclEngine.CaveSkinDigCheck`/`PickCaveSkin` (called from `dig_apply` after `CarveStep`),
  `DigglesClass.CaveSkin`, `DigglesTclContext.CaveSkinRules`, `DiggableTerrain.AddCaveRecord`
  (overlap-drop)/`RemoveCaveRecord(At)`. Tests: `CaveSkinSwapTests` (4).
- Native quirk NOT reproduced: `cave_skin add` stores size as FLOAT BITS into the int-matched field
  (`FUN_0066a9a0:220` vs `FUN_0058e9c0`'s int compare) — AI rects with size 1/2 never matched a rule in the
  shipped game. We store the int (obvious intent; the human stamp path is int in both).
- Original-save continuation: `FUN_0058ecd0` persists the same `+0x30` vector immediately after the
  fixed object-icon table. The typed `OriginalSaveCaveRecordDocument` retains all five words and the
  inactive candidate restores the ordered records into `DiggableTerrain.CaveRecords`. Native does
  not serialize the port-only owner/mark helpers; they are derived from the restored per-owner dig
  nibbles and only fall back to the candidate local player when a rectangle has no active cave mark.

**B. During-dig BEAM/deco spawning — PORTED (branch fogofwar-1to1,).**
This is the "tunnel loot" system: beams/supports AND treasure/resources/critters that appear behind diggers
in STANDARD tunnels. Full RE (every step verified in the decompile):

- **The template table `+0x1a4` (stride 0x54) IS the level-gen template registry** — the same table the
  `lg_*` commands populate (proved by `LGDecTemplateCount` `FUN_00654fa0:95-106` decrementing `tmpl+0x1c`
  through the same accessor `FUN_0058a820`+`0x1a4`). The unused Tcl `template_set` and static exe data are
  NOT the source. Registration `FUN_00583080`: name → `_hol_` sets `+0x41` (excluded) + `+0x48=-2` (no deco);
  zone `+0x4c` from the name prefix (`.rdata` 0x72a898 `"urw_"`=0, 0x72a8a0 `"swf_"`=1, `"kris_"`=2,
  `"lava_"`=3 — bytes read from `original/diggles.exe`). Metadata `FUN_005826d0`: PMP header w/h → `+0x8/+0xc`,
  `.ent` `map_entry` doors; the `.tcl` parse `FUN_00581d30`: `split_template` header → `+0x40=1`+`+0x48=-2`;
  `loading_objdetail` tracks h/m/l/o(ff) → 2/1/0/0; each `new <Class> "" "[expr {X+$xoffset}] [expr
  {Y+$yoffset}] Z" "rx ry rz"` line (class exists + `class+0xca0==0`) lazily creates the template's DECO SET
  (`+0x48` → beautifier`+0x20` vector, entries stride 0x2c = {classIdx, x,y,z, rx,ry,rz, …, detail}) with
  offsets RELATIVE to the template origin (the strtod'd leading numerals; separator `" "` = 0x72a864).
  (`class+0xca0` = a class {4 ints + Tcl obj} block at `+0xc90` — setter not in the decompile dump, sibling
  `+0xca8` block feeds the delete-sweep effect `FUN_00605c10:94-115`; a NO-OP for every class the shipped
  tunnel templates spawn, so the port gates on class-exists at spawn time instead.)
- **Template SHAPE** (`FUN_00581b40` → cache beautifier`+0x18` by PMP name → rasterizer `FUN_00599fc0`):
  cell = 1 ⇔ the authored block's aux 8×8-hmap byte `0x1b` (sub-cell 3,3) nibble == **11 — the TUNNEL-FLOOR
  height**. Byte-verified on BDB2 + `gng_001.pmp` (blocks 76/77/78/79 → aux1b 12/13/12/12 = 0; 80,81 → 11 = 1;
  0 → 15 = 0): gng_001 = the 2-wide corridor through the middle of the 4×4. Zone variants share one PMP shape.
- **`ScanAtPos`** (`FUN_0058b130`, from DigApply:112-118 BEFORE each carve, local view, digger-owner layer):
  full exit when the node is inside an existing beam-rect (`+0x10`, stride 0x18, inclusive); else if the node's
  nibble is in the **dummy band** (`(n&8)==0 && (n&0xF)!=0` — i.e. the MODE-1 dummy marks (3) painted by the
  STANDARD tunnel order `FUN_005672d0` and the worldgen growth; depth-tool/cave mode-0 marks (`cnt|8`) never
  match), build the ±16 window grid (clamped 4 tiles inside the map, exclusive upper) of dummy-band cells and
  match via `FUN_0058a8b0`: eligible templates = not `+0x41`/`+0x40`, `+0x48 ≥ 0`, `+0x4c == FUN_005fcdf0(y)`
  (<0→0), `W*H ≤ 16` (the 4×4 gng tunnels); EXACT 0/1 equality over every covering placement (ox ∈
  [relX−W+1, min(gw−W, relX+W−1)), oy likewise; first hit per template); identical-shape templates reuse one
  scan; candidates recently chosen are dropped newest→oldest while >1 remain (the ≤4 ring, `+0x38`);
  winner = `rand % candidates` (the shared MT19937). Match → rect {(x0w+ox+2)&~3, (y0w+oy+2)&~3,
  (x0w+ox+W+2)&~3, (y0w+oy+H+2)&~3, tmplIdx, layer} + `FUN_0058c920` queues every deco entry as pending
  (beautifier`+0x8`, stride 0x8c): pos = rect origin + authored offset, rot, detail, layer, and the 8 corners
  of the class's 3DB anim bbox (rotated) as floor probes. Then ONE random existing rect (`frame % count`) is
  retired when its ±4 area holds no nibble at its layer (:300-388) — the rect GC.
- **The pending tick `FUN_0058d370`** — called once per `CGameThread::SrvDoThink` (`FUN_005d4250:87`); ONE
  entry per call (`frame % count`): out-of-map pos (±16 inset) parks forever; ANY dig nibble in the probe-box
  ±2 area keeps it pending (completed carves clear their nibbles — deco appears BEHIND the diggers); otherwise
  ONE fit evaluation and the entry leaves the queue either way: spawn ⇔ 3 ≤ #(probe hmap ≤ round(z)) ≤ 7
  (partially embedded — not floating, not buried) AND every probe hmap > 10 (**the tunnel band**: floor 11+;
  a deeper cave carve kills the beam). hmap = the tile block's aux 8×8 at the ×8 (`DAT_0073752c`) sub-cell =
  exactly `get_hmap`. Detail-culled entries defer into the per-block deferred list (`FUN_005d7450`) instead.
  Spawn = direct object construction + variation picks + the standard init.
- **Removal law (CORRECTED):** inside the BEAUTIFIER nothing removes live objects except the cave-skin
  completion sweep (A, `+0xcc2` classes only; its pending purge `FUN_0058ba00:281-388` targets the per-block
  DEFERRED-detail lists, NOT the beam queue). But the earlier conclusion "tunnel deco persists once spawned"
  was **WRONG** — the ENGINE removes dummy deco per-carve via the **MapValidator DUMMYDES phase**
  (`FUN_005a46d0` label 0x10 → `FUN_005eb7b0`): every carve dirty rect is tested against live objects with
  `(obj+0x3d0 & 0x12) == 0x10` (physic flag on, not dying) and `obj+0x278 == −1` (unlinked); an
  objtype-dummy class (`class+0x54 == 0` → cached verdict `class+0x68 = 3`) whose ANIM BBOX (obj pos ± frame
  bbox `FUN_004059a0+0x44..0x54`) overlaps the rect is destroyed through `FUN_005eae80`. **ROUND-5
  CORRECTION:** `FUN_005eae80` is NOT a silent free — its first act is the **MESH SHATTER**
  (`FUN_004845a0`: current-frame resolve + frustum gate + shatter SOUND `FUN_00453f00` → the MeshDestructor
  core `FUN_004830a0`: per-vertex 16-bit decode + world transform `:289-303`, adaptive triangle
  tessellation `FUN_00482950`, per-vertex palette colours `:314-327`, symmetric random velocity `:167-172`,
  gravity `DAT_00737058` + physics-until-settled `FUN_005ea850`), THEN releases render/light handles
  (`FUN_005ca8b0`) and queues the frame's deferred-destroy list guarded by `obj+0x404`
  (drainer `FUN_00605c10`). In the original, tunnel boards/beams/moss BURST INTO TEXTURED FRAGMENTS one by
  one as a cave carve front reaches them — the same MeshDestructor that fires on creature death
  (death modes `classdef+0x68` ∈ {1,3}, and 2 via `FUN_005e9700`).
  In shipped data the small-tunnel deco sets contain NO torch (`Fackel`) classes; light-bearing deco does
  exist (e.g. `Dummy_Kris_*` with `class_light`, deco/dummy_kristall.tcl) and lights up through the normal
  per-instance `class_light` spawn path.

**Port** (mirrors A's wiring): `Runtime/World/MapDecoBeautifier.cs` (rects+pending+ring+shape cache; ScanAtPos/
Tick/matcher), `LevelGenerator` = the shared registry (`Piece.Zone/Hol/Split/Deco` + `Registered`/`EnsureMeta`/
`FindTemplatePmp`; deco parsed in `LoadMeta`), `DigglesTclEngine.MapDecoOf/MapDecoTick/MapDecoSpawn` (lazy
singleton like `FUN_0058a820`; spawn = `CmdNew` → obj_init + scene spawner + class_light + particles, then
owner −1 like authored template deco + `_selectable`/`_hoverable` = 0 — native deco classes never opt into
the default-off pick flags (animinit.tcl dummy generators, `set_selectable` = `FUN_00681220` bit 0x2), while
the port's pick model defaults ON, so spawned deco must opt out or it hover-auras —),
`dig_apply` calls `ScanAtPos` before `CarveStep`, `DigglesGameSim`
ticks `MapDecoTick` on the 10 Hz think (= SrvDoThink cadence). Port deviations (documented in the class
header): the AREA-CLEAR gate box = the owning BEAM RECT and the floor probes = fixed half-extents
(±0.5 xy, −5/+0.5 z), splitting the two consumers of the native 3DB anim bbox (no per-class bbox index in
the port; the first version's centre±0.5 area gate fired mid-dig — supports popped in beside the digger,
); model/anim variation rides `new`; class gate at spawn not parse; max object detail (no
deferred lists); **cave-over-tunnel purge**: `CaveSkinDigCheck` also sweeps `MapDecoSpawned` entities inside
a completed cave rect (the native leaves spawned tunnel deco forever — beams floated mid-air in finished
caves).

** fixes (deco-lifecycle track):**
- **BUG A — per-carve deco sweep.** The cave-over-tunnel purge only
  fired on cave-RECORD completion (`CaveSkinDigCheck`), so it missed freehand caves with no `cave_skin`
  record, the Ctrl+LMB test carve, and back-wall / mid-tunnel deco whose tunnel was re-carved into a cave —
  the deco floated in the opened space (deco on the back wall / in the middle of the cave was not removed). Round 1 shipped the per-carve sweep with an RE note claiming the native has no
  delete-on-carve — **that claim was WRONG** (see the corrected Removal law above: the MapValidator
  DUMMYDES phase IS the native per-carve deco killer; `FUN_005976b0` is only the dig-guard that blocks
  carving under standing objects, `FUN_00596b80:45/281-282`). The port sweep is therefore the DUMMYDES
  analog, not a deviation-in-kind: `DiggableTerrain.OnCarveNode` fires after every successful carve
  (`CarveStep` + `CarveTileNow`) → `DigglesTclEngine.MapDecoCarveSweep`. (
  "beams on the walls and decorations still do not disappear when a cave is dug above the tunnel")
  extended the kill conditions to the player's deco taxonomy, band-gated instead of the native raw
  bbox-overlap (documented deviation — predictable, spares pieces from unrelated adjacent digs):
    • FLOOR law (boards/moss on the BACK wall + floor deco): the piece's own floor sample left the tunnel
      band (`MapDecoBeautifier.FloorStranded` = `hmap ≤ 10`; window match by the SAMPLE's tile — RoundToInt
      of the position missed the sampled tile for `.5`-coordinate pieces);
    • WALL-ANCHOR law (front-plane beams/supports): anchor wall tiles recorded at spawn
      (`MapDecoBeautifier.WallAnchorsFor` — nearest solid tiles ≤ 2 away in each cardinal direction →
      `DigglesTclEntity.MapDecoAnchors`); the piece dies when a carve window covers an anchor whose face is
      now cave-band/open (`AnchorLost ≤ 10`) — a cave dug ABOVE/BESIDE the tunnel removes the wall a beam
      braces while its own floor stays hmap 11.
  Removal runs through `MapDecoRetire` — the native `FUN_005eae80` seam: `MapDecoRetiring` = the `obj+0x404`
  double-queue guard; headless deletes instantly (deterministic law tests).
- **ROUND 3 — the LITERAL bbox kill law; anchors removed.** Boards/moss on a wall were still not removed when the block in front of them was dug — the anchor-tile gate missed the case the native
  bbox law catches: a wall board's model protrudes into the open space in front of its wall face. Grounding
  completed: **every authored template deco line runs `set_physic $objhandle true`**
  (Templates/urw_gng_001_a.tcl:8/14/22/28 — back-wall boards at z=11.02, front supports at 15.04, moss) →
  `FUN_0067fba0:141-146` sets `obj+0x3d0 |= 0x10` and creates the piece's dummy record (`FUN_005ebc90` +
  `FUN_005eb470`); DUMMYDES (`FUN_005eb7b0:83`) then kills any recorded objtype-dummy whose ANIM BBOX
  overlaps a carve dirty rect (strict float compare). Cave-skin classes are `class_physic 0` and never
  `set_physic` (SetCaveSkinClasses animinit.tcl:419-429) → never enrolled → only the completion pass swaps
  them (the port sweep now EXCLUDES `CaveSkin` accordingly). Port law in `MapDecoCarveSweep`: piece bbox =
  pos ± (`FowExtX/Y` renderer union when computed, else `MapDecoBeautifier.DecoExt` = 1.5) vs the carve
  node's window `[nx−1.5, nx+0.5]×[ny−1.5, ny+0.5]`, strict inequalities; PLUS a port-only CAVE-BAND GUARD
  (some window tile `hmap ≤ 10`) compensating for the fat isotropic default box — tunnel-band re-digs
  beside a piece (fresh carves at 11) don't over-kill. `WallAnchorsFor`/`AnchorLost`/
  `MapDecoAnchors` bookkeeping was deleted (subsumed). Repro:
  `CarveSweep_FrontBlockCarved_RemovesWallBoard_BboxLaw` (fail-first); guards:
  `CarveSweep_UnrelatedWallCarve_KeepsWallAnchoredDeco` (intact-band carve + no-overlap carve keep it).
- **ROUND 3 — emerge timing: piece-local area gate.** Beams appeared slightly late: the
  whole-BEAM-RECT ±2 gate made every piece wait for the entire template section. Native `FUN_0058d370:62-101`
  gates on the ENTRY's own bbox min/max ±2 — piece-local, so deco matures as the dig front passes it. Port:
  gate box = pos ± (`DecoExt` + 2), inclusive tiles (`MapDecoBeautifier.Tick`); `PendingDeco.AreaX0..Y1`
  removed. Repro: `Tick_PieceLocalAreaGate_SpawnsWhileFarEndStillMarked` (fail-first);
  `Tick_AreaGate_KeepsPendingWhileLocalMarksRemain` still holds the mid-dig no-spawn line.
- **ROUND 4 — TEMPLATE deco joins the sweep: the set_physic enrolment.**
  wandboden_Holz_Standard still hung in the air: round 3 gated the sweep on `MapDecoSpawned` (beautifier spawns
  only), but the player's hanging pieces are AUTHORED template deco — enrolled natively by their own init
  lines `set_physic $objhandle true` (Templates/urw_gng_001_a.tcl:8/14/22/28) → `FUN_0067fba0:141-146`
  (obj+0x3d0 |= 0x10 + dummy record `FUN_005eb470`). The port already stores set_physic as the `_physic`
  entity prop (RegisterEntityProp) — eligibility is now `MapDecoKillable`: `MapDecoSpawned` OR
  (objtype-dummy class [`Kind == "dummy"`, incl. def_idiobjclass] + `_physic` truthy + not link-attached
  [`LinkedToHandle == 0` ⇔ obj+0x278 == −1]); cave skins stay excluded (class_physic 0, never set_physic).
  Same criterion backs the completion purge. Characters/items that call set_physic (e.g.
  characters/brut.tcl) are exempt via the objtype verdict (class+0x54 != 0 → keep), as are the templates'
  `Info_Coll_*` markers. Digging under start-meadow trees/bushes (def_idiobjclass dummies, set_physic'd by
  their templates) now crumbles them — native DUMMYDES behavior. Repro:
  `CarveSweep_TemplateDeco_SetPhysicTrue_IsSwept_GuardsHold` (enrolled piece crumbles; un-enrolled /
  link-attached / non-dummy pieces survive).
- **ROUND 4 — beams-not-visible-while-digging (BACKLOG finding, timeboxed).** The spawn pipeline is clean
  for beams: `Dummy_Multi_Holzstuetze_a` parses from the gng deco list (detail 2 —
  `RealData_UrwGng001_TclDecoParse`), classinit globs `scripts/classes/deco/*.tcl` so the class exists in
  Play, and the floor probes accept the support position exactly like boards. Remaining suspect =
  RENDER-side: the Multi_* pieces live in `JoinedObjs.3db` with capital-case clip names
  (`Multi_Holzstuetze_a.standard`, SetFrontDummyClassesNoPhys animlist) — verify in Play that the spawned
  GO resolves that clip (watch for `[map_deco] new Dummy_Multi_…`, then check the anim registry's case
  handling / renderer presence). Moss "appearing with delay" was the round-3 rect-gate lateness (now
  piece-local).
- **ROUND 3 — removal presentation = CRUMBLE — SUPERSEDED BY ROUND 5; its RE verdict was WRONG.** Round 3
  claimed the native destroy has no VFX (mis-read `FUN_005eae80` as handles+queue only and missed that its
  `FUN_004845a0` call IS the shatter builder). Kept for history: the dirt burst (set 20) +
  `MapDecoSlide.BeginCrumble` drop now serve only as the NO-MESH fallback of the shatter path below;
  slide-back retreat still selectable via `MapDecoSlide.UseSlideRetreat`.
- **ROUND 5 — removal = the DEATH SHATTER (the object bursts into pieces like a dying creature; confirmed by RE).** Native law (MeshDestructor, `Engine/MeshDestructor.cpp`
  attribution): `FUN_005eae80` → `FUN_004845a0` (resolve the CURRENT anim frame `FUN_004059a0`, frustum
  gate `FUN_00472e80`, shatter SOUND `FUN_00453f00`) → **`FUN_004830a0`**: decode the frame's 16-bit
  vertices and world-transform them (`:289-303`, includes a small random jitter), walk the index buffer per
  triangle, adaptively tessellate big triangles (`FUN_00482950`, threshold `DAT_00736fe0`), emit textured
  shards carrying per-vertex palette colours (`:314-327`, palette `DAT_00b9bed0`) and the model's material
  (`+0x80`); per-shard random velocity (`:167-172`), gravity `DAT_00737058`, ground clamp + settle
  (`FUN_005ea850`; freed when at rest — no timer). The ONLY Tcl entry is `CTclObjMethods::Destruct`
  (`FUN_0067bcf0`, `<obj>` only — the port's `obj_to_particle` stub name maps here; the "type" is the
  object's death mode `classdef+0x68`, not an argument). Callers: DUMMYDES `FUN_005eb7b0`, the cave-skin
  purge `FUN_0058ba00:422-433`, death modes {1,3} (`FUN_005ea850:273-277`) and 2 (`FUN_005e9700:601-603`).
  **Port (ROUND 6 — the round-5 sprite rendition was REJECTED in Play: every object turned into a few tiny particles, whereas the original literally shatters the mesh):**
  `DigglesTclEngine.ShatterEntity` (PUBLIC — the creature-death seam too; combat NOT wired) now spawns
  **`MeshShatterEffect`** (Runtime/Engine/MeshShatterEffect.cs) — TRUE GEOMETRY fragments:
  `MeshShatterBuilder` partitions the CURRENT-frame mesh (`DigglesAnimator.CurrentFrameMesh`, else first
  `MeshFilter`) per index-buffer triangle and midpoint-4-splits oversized triangles until every edge fits
  the `SplitThreshold` (the `FUN_00482950`/`DAT_00736fe0` law; simplified split selector, area-preserving);
  every fragment keeps its ORIGINAL UVs and renders with the model's OWN `sharedMaterial` — the texture
  pieces fly apart. One GameObject + one dynamic Mesh = one draw call; CPU fixed-step integration
  (30 Hz): symmetric launch + up-arc (`:167-172` shape), gravity, ground bounce + settle
  (`FUN_005ea850` spirit), rest → linger → shrink-fade → destroy (native frees at rest). Fragment count:
  per-triangle, native-uncapped for typical deco; `FragmentCap` = 600 safety for big meshes. Tunables:
  `SplitThreshold/MaxSplitDepth/FragmentCap/StepHz/LaunchSpread/LaunchUpBias/GravityPerStep/
  SpinDegPerStep/GroundDrop/RestLinger/FadeSeconds/MaxLifetime`. Particle set 43 + `CreateTempShatter`
  remain DORMANT sprite-burst infra (documented in `ParticleTypeTable`; not wired). Tests:
  `DecoShatterTests` — builder coverage (every triangle exactly once, UVs preserved), tessellation
  area-preservation + edge-threshold, integration settle, ShatterEntity routes mesh→geometry effect /
  no-mesh→false (retire falls back to dirt+crumble), headless guard; set-43 infra tests retained.
  **ROUND 7 polish (mechanism approved in Play):**
  (1) GAME-time scaling — the effect advances by dt × `DigglesAnimator.GlobalTimeScale` (the animators'
  scaled clock; TimeControlService x1..x10 folds in, cutscenes force x1, pause freezes shards mid-air);
  test seam `SpeedSource`. (2) Per-fragment GROUND CACHE — one raycast at spawn per shard
  (`GroundMask`/`GroundRayRange`; test seam `GroundProbe`) → each shard settles at ITS OWN ground Y on
  uneven terrain (hovering/sinking fixed); a shard with NOTHING beneath falls on and shrinks away
  after `FlightShrinkDelay` (pit law); flat `GroundDrop` = headless/no-hit fallback only. (3) ALPHA CULL
  for cutout foliage — `MeshShatterBuilder.CullTransparent` drops shards whose texture alpha at the UV
  centroid < `AlphaCullThreshold` (moss breaks into its visible sprigs; moss shards fixed);
  dense meshes over `FragmentCap` take a UNIFORM stride subset (creatures shatter across the whole body,
  not the first N index-order triangles). (4) GENTLE COLLAPSE retune — an exact copy of the model made of shards, falling apart with slight acceleration: `LaunchSpread` 0.012 / `LaunchUpBias` 0.008 / `SpinDegPerStep` 2.5
  (the round-6 spread was the classic model-scale overshoot on the:167-172 constants). Round-7 tests:
  scaled-clock accumulator, ground-cache solid-vs-pit law, alpha-cull law, uniform-cap law, gentle-default
  bounds guard.
- **BUG B — cave-skin lining props opt out of the pick model.** `CaveSkinDigCheck` step 2 spawned the fresh
  skin props via `CmdNew` WITHOUT the opt-out flags, so they hover-aura'd and ate clicks.
  Native cave-skin classes are `def_class … dummy` with `class_collision 0` and NO obj_init, and never call
  `set_hoverable`/`set_selectable` (animinit.tcl:419-429) → default-off native flags leave them
  non-interactive. Fixed: the step-2 spawn now applies `_selectable`/`_hoverable` = 0, the same seam
  `MapDecoSpawn` uses for tunnel deco.
- **BUG B — supports appeared immediately, before the dig completed: the cave record rect must
  be the FULL template footprint.** `CaveStamp.Commit` recorded the bbox of the cells that actually PAINTED —
  but the mode-0 painter's rim gate (`FUN_005671a0:32-45`, `DiggableTerrain.MarkRimGate`) silently drops
  every cell already open (validity rule 4 reads the STATIC corner height, the painter reads the NEXT-step
  RIM — a once-carved corridor cell is *valid* for an 11-target lip yet *drops* at rim 7). Re-stamping a cave
  over mostly-dug ground (extending an existing cave — the Play case) shrank the record, in the extreme to a
  single row/col, whose half-open completion scan `[x1,x2)×[y1,y2)` is EMPTY → the FIRST swing "completed"
  the cave, spawned the three skin props and retired the record. Native law (`FUN_00567cb0:164-198`): the
  bbox update runs UNCONDITIONALLY for every non-skip template cell, after the painter call and regardless
  of its outcome — the record is always the full footprint (also feeds the `FUN_0058eb30` overlap-drop and
  the completion centre with native geometry). Port fixed to match; repro
  `CaveSkinCompletionCarveTests.StampOverDugGround_RecordIsFullFootprint_AndPropsWaitForTheRealDig`.
- **POLISH C — deco "emerge from the back wall" — IMPLEMENTED.** The
  native does NOT animate deco: `FUN_0058d370` constructs the object directly at its final rest position
  (rect origin + the authored z offset); the "coming out of the back wall" look in the original is a
  byproduct of the 8 depth-layer carve progressively revealing the final-position mesh. The port models a
  single carve per tile, so per the player's clarification (appearance: they slide out of the wall; removal: not an instant delete) the presentation is a spawner-side tween: `MapDecoSlide`
  (Runtime/Engine) slides the piece along native z — out of the wall on spawn (`BeginEmerge`, 1.0 s,
  wired in `MapDecoSpawn` and the `CaveSkinDigCheck` skin spawn: supports grow out), back into the wall
  on removal (`BeginRetreat`, 0.6 s, then the deferred entity delete — matching the native DUMMYDES
  deferred-destroy timing, which likewise never frees in-place). Play-mode only; headless spawns/deletes
  stay instant.

Tests: `MapDecoBeamTests` (21 EditMode: match/rect/queue laws, inside-rect skip + corridor tiling, zone +
mode-0 negatives, pending gate → spawn (owner −1, non-selectable) → tunnel-band reject, piece-local area
gate (mid-dig no-spawn + far-end-still-marked spawn), cave-completion purge of tunnel deco, unknown-class
drop, rect GC + re-match, class_light deco, full dig_apply pipeline, real-data `.tcl` deco parse + PMP/BDB
shape law, ** per-carve cave-re-carve strands deco → swept, beam over intact floor kept, cave-skin
props not hover/select; round 3: front-block carve kills the wall board (bbox law), cave above the
tunnel kills the wall-mounted beam, unrelated/intact-band carves keep it**) +
`CaveSkinCompletionCarveTests` (2 EditMode, real BDB: a progressive 192-swing dig_apply cave carve grows
the skins only at true completion (swing 188/192, one excluded-boundary residue cell); the over-dug
re-stamp records the FULL template footprint and holds the props until the surviving strip is actually
dug).
