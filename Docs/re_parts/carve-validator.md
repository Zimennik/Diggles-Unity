> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# Carve + Walkability Revalidation — Complete RE (`carve-validator`)

> Ground-truth reverse-engineering of the **post-dig carve** (`FUN_00596b80`), the **rim helper**
> (`FUN_005968b0`), the **block-aux writer** (`FUN_00596550` / `FUN_00595e40`), the **MapValidator**
> region-iteration driver (`FUN_005a46d0`), and the **EFFPASSWALK** pass (`FUN_005a6b40`) that
> RE-DERIVES the per-sub-cell passability `tile+0x3c` from the per-sub-cell heightmap. This is the
> cornerstone of **post-dig walkability**: after a dig mutates terrain, EFFPASSWALK regenerates the
> `+0x3c` floor/wall lines, then `FUN_005a6350` (the BLOCK pass, already RE'd) re-derives the per-tile
> graph fields `+0x06`/`+0x12` from those.
>
> Every claim cites `FUN_addr:line` / struct offset / constant (hex+dec). The EFFPASSWALK decision tree
> was VERIFIED against the live minidump (`dumps/diggles_meadow.dmp`) by a Python probe
> (`tools/probe_effpasswalk_verify.py`, the corrected EXACT model): it reproduces **1672/1672 = 100%** of the
> live `value-3` ground-line cells (zero exceptions) and removes 8 spurious `value-1` cells vs the earlier
> 30-cell band. NOTE the open band is **31** cells (not 30) and the floor/wall split has two
> nested confirms (`h(sy-11)` primary, `h(sy-10)` alternate) — see §2.2 CORRECTED notes + Verification notes.
>
> **Frame**: the 512×640 tile grid is the SIDE VIEW. Native X = horizontal east
> (walk axis); native Y (tile rows) = gameplay VERTICAL (+y = down/deeper/gravity, the climb axis); the
> BDB per-sub-cell height nibble (0..15) = SLAB DEPTH INTO THE SCREEN (the dig axis). When older notes
> say "height"/"elevation" for the nibble, read **DEPTH**. The gameplay vertical is tile-Y.

---

## 0. Overview — the post-dig pipeline

```
USER/AI dig
   │
   ▼
FUN_00596b80 (the CARVE) §1
   • precondition gate (bounds + FUN_005976b0 exposed test)
   • decode the owner's dig-state nibble in tile+0x08 (8 owners × 4 bits) → an open-depth threshold
   • re-resolve block_idx via the aux corner-transition graph aux[0x40..0x4c]
     gated by the corner gate bytes aux[0x50..0x53] vs the rim (FUN_005968b0)
   • set dirty bits tile+0x13 |= 1 (block changed) / |= 2
   • spawn the dug object, write get_gnomeposition source tile+0x250
   │
   ▼
FUN_005ad040 / MapValidator queues the dirty rect into one of the region lists
   │
   ▼
FUN_005a46d0 (the MapValidator) iterate the dirty rects, run the stage passes IN ORDER: §3
   EFFPASSWALK → EFFOBJPF → EFFBLOCK → BASELIGHT → [RENDERJOBS] → FLUIDOBJS → MINIMAP
   → IDIOBJ → OBJPHYSIC → DUMMYDES → FLUIDALIST
   │
   ├─ EFFPASSWALK = FUN_005a6b40 → RE-DERIVE tile+0x3c (8×8 passability) FROM the heightmap §2
   │ • build a local height window (FUN_00595e40), run the open-band decision tree per
   │ sub-cell, emit 0/1/3, then FUN_00596550 copies the window back into tile+0x3c
   │
   └─ EFFBLOCK = FUN_005a6350 → RE-DERIVE the per-tile graph +0x06 / +0x12 FROM tile+0x3c §1.4
```

So a dig is a **two-phase** mutation: (1) `FUN_00596b80` lowers the *block geometry* (heightmap) and marks
the rect dirty; (2) the MapValidator's `EFFPASSWALK`→`EFFBLOCK` regenerate the *passability lines* and the
*tile graph* from the new geometry. **Walkability is GEOMETRY-derived, recomputed after every dig** — it is
not edited in place by the dig code.

---

## 1. THE CARVE — `FUN_00596b80` (2218 bytes)

Signature (reconstructed): `void FUN_00596b80(CMap *cmap /*param_1*/, int *subPt /*param_2 = {subX,subY}*/, CObject *gnome /*param_3*/)`.

### 1.1 Precondition gate (`00596b80:42-47`)
```
if !(cmap[0x21]+1 <= subX < cmap[0x23]-1 && cmap[0x22]+1 <= subY < cmap[0x24]-1) return; // in-bounds, 1-cell margin
owner = gnome[+0x36c] // owner index 0..7 (:44)
c1 = FUN_005976b0(...) // EXPOSED precondition (see §1.6) (:45)
shift = (char)owner * 4 // bVar10 = owner<<2: nibble shift (:46)
if c1 == 0 → goto CLEAR (just clears the dig nibble; no carve)
```
`FUN_005976b0` (§1.6) returns nonzero only when the target tile is partially exposed
(`tile+0x18 & 3 ∈ {1,2}`) AND a fluid/visibility check passes. If it fails, the dig is rejected and the
code falls through to `00596b80:281-282` which clears this owner's nibble: `tile+0x08 &= ~(0xF << shift)`.

### 1.2 Dig-state nibble decode `tile+0x08` (`00596b80:48-61`) — eight owner orders
The word at `tile+0x08` is a `u32` holding **8 owners × 4 bits** (`owner*4` shift). It is
parallel order state, not a spatial voxel column. For the current owner:
```
v = (tile+0x08) >> shift // this owner's 4-bit nibble
if (v & 0xF) == 0: target = 0xF (15) // no order for this owner
elif (v & 8) == 0: target = 0xB (11) // bit3 clear → partly open, mid level
else:
    target = 0
    if (v & 3) != 0:      target = (v & 3)*4 - 1     // low 2 bits → {1:3, 2:7, 3:11}
```
So the nibble encodes an **open-depth threshold** `target ∈ {0,3,7,11,15}`:
- bit `0x8` (=open flag) set + low bits `0x3` (=sub-fill 0..3) → threshold `lo*4-1` (3/7/11).
- bit `0x8` clear → 11; nibble zero → 15 (fully undug).

This `target` (called `local_20`) is the depth level the dig is trying to reach.

### 1.3 The rim gate (`00596b80:62-63`)
```
rim = FUN_005968b0(cmap, subPt) // §1.5 — max of the 4 neighbour corner gate bytes
if !(target <= rim): → fall through to CLEAR // can't carve past the rim — clear the nibble
```
The dig only proceeds when the requested open-depth `target` is **≤ the rim height** of the surrounding
corner gates. Otherwise it bails and clears.

### 1.4 Block re-resolution via the corner-transition graph (`00596b80:64-119`)
Four corner neighbour blocks are resolved (NW/NE/SW/SE), each gated by its corner gate byte vs the rim
`uVar12` (= the value returned by FUN_005968b0, reused). For each corner k:
```
blk = tile+0x00 (block_idx at the corner tile via FUN_00592f10)
auxRec = blk * 0x59 + cmap[9][+0x10] // BDB aux base, stride 0x59=89
candBlk = auxRec[ 0x4c - 4*k ] // NW→+0x4c local_20 / NE→+0x48 local_2c / SW→+0x44 local_30 / SE→+0x40 local_18
if candBlk != -1 && FUN_004093b0(...)[ 0x53 - k ] == rim: // the cand's gate byte equals the rim → accept
    use candBlk
```
- `+0x4c/+0x48/+0x44/+0x40` (`local_20/2c/30/18` at lines 74/88/102/116) = the 4 **neighbour-block
  transition indices** (the carve graph: "the block this corner becomes when stepped down one level").
  *Confirmed:* live aux block 71 `[0x40..0x4c]` = `{71, 71, 8913, 37301}` (SW/SE step to deeper blocks, NW/NE
  stay — block 71 is a half-step floor open on top, solid below).
- `+0x53/+0x52/+0x51/+0x50` (lines 75/89/103/117 via `FUN_004093b0`) = the candidate's **corner gate bytes**;
  the transition is only taken when the candidate's gate byte == the rim height. This keeps the dig seamless.

Then writes the 4 resolved blocks back into the 4 corner tiles and marks each dirty
(`00596b80:120-136`): `*tilePtr = resolvedBlk; tile+0x13 |= 1`.

### 1.5 Nibble cleanup pass (`00596b80:137-158`)
After block re-resolution, loop all 8 owners of the centre tile's `tile+0x08`:
```
for owner4 = 0; owner4 < 0x20; owner4 += 4:
    decode this owner's nibble → lvl (same {0,3,7,11,15} decode as §1.2)
    if FUN_005968b0(...) < lvl:                       // rim now below this order's target
        tile+0x08 &= ~(0xF << owner4)                 // this owner's order is complete
```
Every owner's order whose target now exceeds the lowered rim is complete and gets cleared.

### 1.6 Spawn the dug object + write `get_gnomeposition` source (`00596b80:159-241`)
```
tile+0x13 |= 2 // re-render dirty bit (:180)
h = FUN_004093f0(...) // get_hmap per-sub-cell height at the dig point (:181)
Zdrop = (float)h + DAT_00737330 // drop-Z = height-nibble + const (:182)
... allocate/seat a CObject (the rubble/dug marker) at (subX, subY, Zdrop) via FUN_00495940 (:211)
gnome[+0x250] = (tile+0x0c) >> shift // SOURCE byte for get_gnomeposition (floor/wall) (:232)
FUN_005ad040 // ENQUEUE the dirty rect into the MapValidator (:242)
```
- `tile+0x0c` is a parallel per-owner dig-order ID word; `>>shift` extracts this owner's value into the gnome's
  `+0x250` (read by `get_gnomeposition` to choose the floor-dig vs wall-dig animation).
- `FUN_005ad040` is what hands the dirty rect to the validator — the carve does NOT itself rewrite `+0x3c`.

### 1.7 CLEAR fall-through (`00596b80:281-282`)
If the precondition (§1.1) or the rim gate (§1.3) fails:
```
tile+0x08 &= ~(0xF << shift) // clear this owner's dig nibble; no geometry change
```

---

## 1.5 RIM HELPER — `FUN_005968b0` (708 bytes)

`uint FUN_005968b0(CMap *cmap /*param_1*/, int *subPt /*param_2={subX,subY}*/)` — returns the **rim height**
= the max of 4 neighbour-corner gate bytes, used as the dig depth cap and the transition-accept test.

For each of 4 cells around the node — `(subX-1,subY-1)`, `(subX,subY-1)`, `(subX-1,subY)`, `(subX,subY)` —
it (`005968b0:23-151`):
```
blk = tile_block_idx
aux = blk*0x59 + cmap[+0x24][+0x10]
cand = aux[ 0x4c / 0x48 / 0x44 / 0x40 ] // the matching corner transition index
if cand == -1 || cand == blk: byte = aux[0x53/0x52/0x51/0x50] // self → own gate byte
else: byte = auxOf(cand)[same gate offset]; bVar5 = true // follow the transition
```
- bVar1 = NW gate (`+0x53`), bVar2 = NE (`+0x52`), bVar3 = SW (`+0x51`), bVar4 = SE (`+0x50`).
- If NONE of the 4 followed a transition (`bVar5 == false`) AND the SE branch took the self path → returns
  `0xFFFFFFFF` (−1) (`005968b0:137-139`) = "no rim / undefined".
- Else returns `max(bVar1, bVar2, bVar3, bVar4)` (`005968b0:152-164`).

**Net:** rim = highest of the 4 shared-corner gate heights, or −1 if the node is fully self-contained. The
gate bytes (`aux[0x50..0x53]`) are the cached per-corner step heights; for block 71 they are `{0,0,15,15}`
(NW/NE open at depth 0, SW/SE solid at 15), for block 81 `{11,11,15,15}`.

---

## 2. EFFPASSWALK — `FUN_005a6b40` (3153 bytes) — THE `+0x3c` RE-DERIVATION ★ CORNERSTONE

`void FUN_005a6b40(int *rect /*param_1 = {subX0, subY0, subX1, subY1}*/)`. Called once per dirty rect by the
MapValidator (`005a46d0:156`). It rebuilds the 8×8 per-sub-cell passability `tile+0x3c` for every sub-cell in
the (margin-expanded) rect, purely from the per-sub-cell **heightmap** (get_hmap).

### 2.1 Window setup (`005a6b40:55-97`)
```
// clamp the rect into the sub-cell bounds with asymmetric margins (sub-cells):
x0 = max(cmap[+0x94]+0x18, rect[0]); y0 = max(cmap[+0x98]+0x10, rect[1])
x1 = min(cmap[+0x9c]-0x18, rect[2]); y1 = min(cmap[+0xa0]-0x18, rect[3])
if !(x0 < x1 && y0 < y1) return;
// allocate two flat buffers over an EXPANDED window:
local_4c = x0 - 8; local_44 = x1 + 8 // window cols [x0-8.. x1+8)
local_48 = y0 - 0x10; local_40 = y1 + 2 // window rows [y0-16.. y1+2)
stride = local_44 - local_4c // iVar5 = window column stride
pbVar6 = alloc(...) // HEIGHT window
FUN_00595e40(pbVar6, &local_4c) // FILL it: pbVar6[(r)*stride + c] = get_hmap(x,y) §2.4
puVar7 = alloc(...) // OUTPUT (0/1/3) window, size local_2c = (x1-x0)*(y1-y0)
```
Indexing: a cell at world sub `(sx, sy)` maps to window `pbVar6[(sy - (y0-16))*stride + (sx - (x0-8))]`. The
inner loop var `iVar8` runs the window row (so `iVar8` ↔ `sy`), `iVar20` the window col (`↔ sx`). Therefore
**`pbVar6 + iVar20 + (iVar8 + K)*stride + C` == `get_hmap(sx + C, sy + K)`** — the per-sub-cell height at
relative offset (drow=K, dcol=C). Verified by the probe reproducing the live data.

### 2.2 The per-sub-cell decision (`005a6b40:105-517`)
For each output sub-cell `(sx, sy)`:

**(a) Precondition** (`005a6b40:13`): sampled once per 8-cell run (cached on `local_c1`):
```
preTile = FUN_00592f10((sx + 4) >> 3, (sy + 4) >> 3) // tile half-offset by +4 sub-cells in BOTH axes
local_c1 = (~(preTile[+0x18] >> 7)) & 1 // = NOT the +0x18 high bit
if local_c1 == 0: emit 0 (blocked, skip the geometry test)
```
`tile+0x18` is the PMP terrain switch (low 2 bits = dig precondition; **high bit 0x80 = "fully enclosed /
not exposed"**). When set, the cell can never be passable.

**(b) Open-band test** (`005a6b40:116-418`): a deeply-nested chain — **ALL** of these **31** relative cells must
have `get_hmap < 0xE (14)` (i.e. be open / not near-solid) for the cell to be passable. EXACT offsets (drow, dcol)
re-traced (each `(iVar8+K)*iVar5` row term + its `±C` column literal), in source order from the decompile
(`tools/probe_effpasswalk_verify.py:OPEN`):
```
(-8,0) (-15,-7) (-15,7) (-15,0) (-1,0) (-8,-7) (-8,7) (-3,-4) (-3,4) (-5,-7) (-5,7)
(-12,-7) (-12,7) (-15,-4) (-15,4) (-12,0) (-4,0) (-8,-4) (-8,4) (-13,-3) (-13,3)
(-11,-5) (-11,5) (-10,-2) (-10,2) (-6,-2) (-6,2) (-5,-5) (-5,5) (-2,-2) (-2,2)
```
> CORRECTED (verification): the earlier list had 30 cells; the actual decompile has **31**. Three
> errors were fixed: `(-10,0)→(-10,-2)` (line 338: `iVar19+-2`), `(-2,0)→(-2,-2)` (line 403: `iVar12+-2`), and
> the final `(-2,+2)` cell (line 415: `iVar12+2`) was **missing entirely**. The corrected band reproduces
> value-3 at 100% AND cuts the value-1 false-positives (8 cells the old band wrongly passed; see §6/Verification).
All offsets have **drow ≤ 0** — the band is **ABOVE** the cell (toward −y, the open pocket), out to 15 rows
up with a fanning column spread (±7 near the top, narrowing). This is the geometric statement *"there is a
tall open pocket directly above this cell"* — i.e. the cell is the floor/wall of an accessible cavity. If ANY
of these is `≥ 0xE`, emit **0** (the band is blocked by solid → the cell is buried, not walkable).
- `0xE = 14` is the **open threshold** (`< 0xE` ⇔ open). Heights 14/15 = solid slab into the screen.

**(c) Floor-vs-wall step test** (`005a6b40:419-474`): if the open band passed, `local_b8` is set to **1** (default
wall, line 419), then two nested step tests can promote it to **3** (floor):
```
here = get_hmap(sx, sy) // iVar17 = iVar8*stride → pbVar14
above1 = get_hmap(sx, sy-1) // iVar11 = (iVar8-1)*stride → pbVar15 @420-429
below1 = get_hmap(sx, sy+1) // iVar9 = (iVar8+1)*stride+iVar20 @462
// PRIMARY (439 + 457): BOTH the immediate row above AND the row 11 up are >2 lower than here → FLOOR
if above1 + 2 < here AND get_hmap(sx, sy-11) + 2 < here: emit 3 (first solid row under the pocket)
// ALTERNATE (466 + 471): here is >2 lower than the row below; floor iff the row 10 up is also >2 lower
elif here + 2 < below1: emit (get_hmap(sx, sy-10) + 2 < below1 ? 3: 1)
else: emit 1 (open but no step → a climbable wall band)
```
> CORRECTED (verification): two confirm offsets were wrong/missing. (1) The PRIMARY (line 439 `*pbVar15+2<*pbVar14`)
> has a **second nested confirm** at line 457 (`*pbVar15+2<*pbVar14` with `pbVar15=pbVar6+iVar20+iVar10`, and `iVar10`
> was last set to `(iVar8-0xb)*iVar5` at line 307 ⇒ row **sy-11**, `pbVar14=here`). The earlier text emitted 3 on
> `above1+2<here` alone. (2) The ALTERNATE confirm reads `iVar13` (line 467), last set at line 330 to `(iVar8-10)*iVar5`
> ⇒ row **sy-10**, not sy-15. Both `FUN_0041fad0(...)` calls index the SAME height window (`pbVar6`), not a fresh
> get_hmap. The corrected conditions reproduce the live value-3 split at 100% (probe_effpasswalk_verify.py).
The **">2-nibble step" rule** is the heart of it: `value-3` (walk|floor) marks a sub-cell where the depth
nibble JUMPS by >2 across the y-boundary — the GROUND LINE between an open pocket and supporting material.
`value-1` (walk only) is an open band with no such step = a vertical climbable wall face.

**(d) Default** = 0. The constants: the comparison literal is `+2` (the >2 step, since `a+2 < b` ⇔ `b-a > 2`).

### 2.3 Output write-back (`005a6b40:507-523`)
```
*puVar16 = local_b8 // 0/1/3 into the OUTPUT window at (sy-y0)*(x1-x0) + (sx-x0)
... after the double loop:
FUN_00596550(puVar7, &local_20) // copy the OUTPUT window into tile+0x3c (the de-window pass) §2.5
free(puVar7); free(pbVar6)
```

### 2.4 Height-window fill — `FUN_00595e40` (888 bytes)
`void FUN_00595e40(byte *dst, uint *rect)` — fills `dst` (the `pbVar6` window) with the **per-sub-cell
heightmap** over the rect. For each tile in the window it reads the tile's block aux record
(`blk*0x59 + cmap[+0x24][+0x10]`) and copies the 8×8 nibbles into the window at the matching sub-cell
positions (`00595e40:81-135`). When a tile is fully inside (8×8 aligned) it does a fast 8-row copy
(`puVar13[0..0xf]` = the aux 8×8); else a clamped per-cell copy. **This is exactly `get_hmap` (FUN_004093f0)
materialised over a region** — `dst[(y-y0)*stride + (x-x0)] = aux[blk(x,y)*0x59 + (y&7)*8+(x&7)]`.

### 2.5 De-window writer — `FUN_00596550` (861 bytes)
`void FUN_00596550(byte *src, uint *rect)` — the inverse of FUN_00595e40 for the OUTPUT buffer: copies the
0/1/3 values from `src` (the puVar7 window) BACK into each tile's `tile+0x3c` 8×8 grid. For each tile
(`00596550:72-106` fast path / `:16` clamped path):
```
tilePtr = FUN_00592f10(tx, ty) // resolve the tile struct
for sub in 0..63: tilePtr[0x3c + sub] = src[ windowIndex(tx*8+col, ty*8+row) ]
```
The fast path (`:72-106`) writes 8 rows of 8 bytes (`tilePtr+0x3c.. +0x78`) when the tile is fully covered;
note the source index uses `((row - rect[1]) * (rect[2]-rect[0]) - rect[0]) + col` (row-major window). **This
is the function that actually mutates `+0x3c`** — the only writer of passability in the validate path.

### 2.6 VERIFICATION (live dump, `tools/probe_effpasswalk_verify.py`)
Running the **EXACT** §2.2 tree (corrected 31-cell band + the two nested confirms) against
`dumps/diggles_meadow.dmp` (read CMap via the relocated module base; per-sub-cell heights via aux):
- **value-3 reproduction: 1672/1672 = 100.0%** over the full map (all tiles with a non-empty `+0x3c`).
  CONFIRMED by re-verification — the corrected primary/alternate confirms do NOT change the
  value-3 result (the band test already forces the confirmed rows open), so the headline 100% stands.
- value-1: the corrected 31-cell band is strictly MORE faithful than the earlier 30-cell list — in the
  test window (tx 200-300, ty 26-60) it cuts the value-1 false-positives from `(0→1): 10` to `(0→1): 2`
  (8 cells the old band wrongly passed). Residual 2 is at the dirty-rect boundary (window edge), expected.
- The remaining false-positives (predicting 1 where live is 0) are tiles OUTSIDE the validated dirty rect —
  the validator only runs EFFPASSWALK over `param_1[0x27]` dirty lists, so un-validated tiles keep the raw
  PMP-load `+0x3c` (usually 0). The probe over-applies the rule to the whole map; this is expected and
  confirms the validator is region-scoped, not global.
- Concrete check: tile (249,30) = block 71 (aux rows 0-3 height 0,
  rows 4-7 height 15 — confirmed from the live aux). The rule emits **3** at subY=4 (first solid row:
  `h(sy-1)=0 +2 < h(sy)=15` AND `h(sy-11) +2 < 15`, after the 31-cell band test) — exactly the live
  `+0x3c` row-4 `03 03 03 03 03 03 03 03`, `+0x12=0x64` (floor bit 0x40 | exit-sub 0x24). ✔

---

## 3. THE MAPVALIDATOR — `FUN_005a46d0` (5839 bytes)

`void FUN_005a46d0(int *self /*param_1*/)` — the dirty-region revalidation driver. `self` holds parallel
**dirty-rect lists**, one per stage, each `{base ptr, count}` at a fixed field. It runs the stages **in this
fixed order**, each iterating its own list of `{subX0,subY0,subX1,subY1}` int-quads (16 bytes each), clamping
each to the sub-cell bounds (`cmap+0x94..+0xa0`), accumulating an area counter (`self[0x68..0x72]`), and
calling the stage worker per rect:

| # | local_8 | Stage name (`.rdata`) | List field | Worker | Role |
|---|---|---|---|---|---|
| 0 | 1 | (pre: timer rects) `self[0x27]` | +0x9c | FUN_006c2340 | tick timers |
| 1 | 2 | **MapValidator:EFFPASSWALK** | `self[0]` (+0x00/+0x01) | **FUN_005a6b40** §2 | re-derive `+0x3c` passability |
| 2 | 3 | MapValidator:EFFOBJPF | `self[3]/[4]` | FUN_005a5da0 | object pathfind fields |
| 3 | 4 | **MapValidator:EFFBLOCK** | `self[6]/[7]` | **FUN_005a6350** §1.4 | re-derive per-tile `+0x06`/`+0x12` |
| 4 | 5 | MapValidator:BASELIGHT | `self[9]/[10]` | FUN_005906a0 | base lighting |
| 5 | 6 | MapValidator:RENDERJOBS (if `DAT_0076a338`) | `self[0xc]` | FUN_005b8930 | mesh re-render jobs |
| 6 | 7 | MapValidator:FLUIDOBJS | `self[0xf]` | FUN_0057d980+0057b990 | fluids |
| 7 | 8 | MapValidator:MINIMAP | `self[0x12]` | FUN_005a9090 | minimap |
| 8 | 9 | MapValidator:IDIOBJ | `self[0x15]` | FUN_005d5570 | id-objects |
| 9 | 10 | MapValidator:OBJPHYSIC | `self[0x18]` | FUN_005eb7b0+00592840 | object physics |
| - | 0x10 | MapValidator:DUMMYDES | `self[0x1b]` | FUN_005eb7b0 | dummy destinations |
| - | 0x14 | MapValidator:FLUIDALIST | `self[0x1e]` | FUN_0057d980+0057cd50 | fluid alist |

**Stage-order contract (THE key dependency):** `EFFPASSWALK` (stage 1, re-derives `+0x3c` from heightmap)
runs **BEFORE** `EFFBLOCK` (stage 3, re-derives `+0x06`/`+0x12` from `+0x3c`). The pathfinding tile graph is
therefore always consistent with the freshly regenerated passability lines after a dig. `EFFOBJPF` (stage 2)
sits between them. Each stage's profiling block (`MapValidator:<NAME>`) is the `DAT_007368c`/`DAT_00773650`
timer registration (`005a46d0:10` etc.) — ignore for porting.

Per-rect clamp (every stage, e.g. `005a46d0:140-154`):
```
x0 = max(rect[0], cmap[+0x94]); y0 = max(rect[1], cmap[+0x98])
x1 = min(rect[2], cmap[+0x9c]); y1 = min(rect[3], cmap[+0xa0])
self[areaField] += (x1 - x0) * (y1 - y0) // area accounting
worker(&clampedRect)
```
At the very end (`005a46d0:797-798`): `GetTickCount` + `*(DAT_00772400 + 0x97) = 0` (clear the "validation
pending" flag). `DAT_00772400` = the global map/world singleton.

---

## 4. CONSTANTS

| Const | Value (hex / dec) | Role | Cite |
|---|---|---|---|
| owner nibble shift | `owner * 4` | 8 owners × 4 bits in `tile+0x08`/`+0x0c` | 00596b80:44-49 (`owner = gnome+0x36c`) |
| dig-state targets | `{0,3,7,11,15}` from `(v&3)*4-1`, 0xB, 0xF | open-depth threshold decode | 00596b80:50-60 |
| open threshold | `0xE = 14` (`h < 0xE` ⇔ open) | EFFPASSWALK band/step test | 005a6b40:125 etc. |
| step delta | `+2` (`a+2 < b` ⇔ step > 2 nibbles) | floor(3) vs wall(1) split (primary 439+457, alternate 466+471) | 005a6b40:439,457,466,471 |
| open-band span | drow −15..0, dcol ±7 fan (**31** cells) | "open pocket above" geometry | 005a6b40:116-418 (parsed) |
| primary floor confirm | `get_hmap(sx,sy-11)` (iVar10@307) | 2nd nested compare after `above1+2<here` | 005a6b40:457 |
| alternate floor confirm | `get_hmap(sx,sy-10)` (iVar13@330) | compare after `here+2<below1` | 005a6b40:467,471 |
| precondition bit | `tile+0x18 >> 7` (high bit) inverted | "not fully enclosed" gate | 005a6b40:110 |
| precondition tile | `((sx+4)>>3, (sy+4)>>3)` (+4 sub-cell offset) | which tile's +0x18 is sampled | 005a6b40:109 |
| tile stride | `0x7c = 124` | tile array | 00592f10:29 |
| BDB aux stride | `0x59 = 89` | per-block aux record | 00596b80:72, 004093f0:22 |
| aux corner-transition | `+0x40/+0x44/+0x48/+0x4c` (SE/SW/NE/NW) | carve graph neighbour blocks | 00596b80:74-116 |
| aux corner-gate | `+0x50/+0x51/+0x52/+0x53` (SE/SW/NE/NW) | rim gate bytes | 00596b80:75-117, 005968b0:53-150 |
| aux height grid | `+0x00..+0x3f` (8×8 nibbles) | get_hmap source | 004093f0:24 |
| drop-Z offset | `DAT_00737330` (≈ a small +offset) | dug-object placement Z = h + this | 00596b80:182 |
| `+0x13` bit 1 | block-changed dirty | renderer/validator | 00596b80:123 etc. |
| `+0x13` bit 2 | re-render dirty | renderer | 00596b80:180 |
| `+0x250` | gnome get_gnomeposition source = `(tile+0x0c)>>shift` | floor/wall dig anim | 00596b80:232 |
| `EFFPASSWALK` before `EFFBLOCK` | stages 1 then 3 | graph consistency | 005a46d0:156,266 |

---

## 5. TCL ↔ NATIVE CONTRACT

The carve+validate path has NO direct Tcl entry — it is downstream of the dig primitives:
- `dig_next` / `dig_execute` (Tcl `z_dignwalk.tcl`) → native dig dispatcher → `FUN_00596b80` (the carve).
- After the carve, `FUN_005ad040` (00596b80:242) enqueues the dirty rect; the engine's frame loop later runs
  `FUN_005a46d0` (the MapValidator), regenerating `+0x3c` (EFFPASSWALK) and the tile graph (EFFBLOCK).
- `get_gnomeposition` (`FUN_0068d8a0`) reads the `+0x250` source the carve wrote, OR re-derives 0/1 from
  `+0x3c & 2` at the dwarf's sub-cell — both are kept in sync by EFFPASSWALK.
- `get_hmap` (`FUN_004093f0`) returns the per-sub-cell height that EFFPASSWALK consumes — so after a dig the
  same heightmap that the carve lowered is what the passability is rebuilt from. Self-consistent.

**Contract the port must honour:** a dig must (1) lower the geometry/heightmap, (2) mark the rect dirty,
(3) on the next validate tick re-run EFFPASSWALK (rebuild `+0x3c` from the new heightmap) THEN EFFBLOCK
(rebuild `+0x06`/`+0x12` from `+0x3c`). Skipping (3) leaves stale walkability and the dwarf cannot path onto
the freshly-dug floor.

---

## 6. PORT GAP ANALYSIS (vs current Unity code)

Current port files: `Runtime/World/DiggableTerrain.cs` (the carve), `Runtime/Navigation/NavWorld.cs`
(the data layer + FUN_005a6350 port).

| # | Gap | Severity | Detail |
|---|---|---|---|
| G1 | **EFFPASSWALK is NOT ported** — `+0x3c` is loaded VERBATIM from the static PMP and never re-derived. | **CRITICAL** | `NavWorld._sub` is the PMP load; there is NO `FUN_005a6b40` equivalent. After a dig, the passability lines do NOT regenerate → the dwarf can't walk onto carved floor. `NavWorld.cs:76-94` only exposes the static sub-grid; the geometry re-derivation (§2.2) is absent. |
| G2 | ~~Missing spatial voxel carve~~ **CLOSED / false premise.** | **DONE** | The executable carves the same one-block-per-tile BDB corner graph. `tile+0x08` is eight owner targets, not geometry. `DiggableTerrain.CarveStep` decodes and clears each owner, applies the rim gate and `gate==rim` transitions, and mode 1 retains native target 11. |
| G3 | **No MapValidator stage-ordering** — the port re-bakes nav directly after a dig via `_navRebuild` (a remesh + a full `NavWorld.DeriveAll`). | **MEDIUM** | `DiggableTerrain.cs:243-244` calls `RemeshFromArrays` + `_navRebuild` but `NavWorld.DeriveAll` (NavWorld.cs:97) only re-runs `FUN_005a6350` (EFFBLOCK) over `_sub` — and `_sub` is the STALE static PMP grid (never regenerated by an EFFPASSWALK port). So EFFBLOCK derives from un-updated passability. The §3 contract (EFFPASSWALK→EFFBLOCK from the NEW heightmap) is broken at the first half. |
| G4 | **`DeriveTile` skips the climb-band / enclosed-AND refinements** of FUN_005a6350 (acknowledged in NavWorld.cs:131-134). | LOW | Doesn't affect the common floor/solid case; revisit with wall/dig integration. |
| G5 | **Rim helper** (`FUN_005968b0`) is approximated as `RimHeight = max(4 corner gate bytes)` of the SINGLE tile (`DiggableTerrain.cs:102-110`), not the 4-neighbour shared-corner walk with the transition-follow + the `-1` self-contained sentinel (§1.5). | LOW | Adequate for dig-Z but not for the carve's rim gate. |

### Recommended fix (faithful, P0-of-post-dig-walkability)
Add an **EFFPASSWALK port** to `NavWorld`: `ReDeriveSubGrid(rect)` that, over the dirty rect, recomputes
`_sub[tile*64 + sub]` from `GetHmapSub` using the §2.2 tree (precondition `+0x18>>7` of the +4-offset tile;
the 30-cell open-band `< 14` test; the `>2`-step floor/wall split). Then `DiggableTerrain.ApplyDig` should,
after lowering the block geometry, call `Nav.ReDeriveSubGrid(dirtyRect)` (EFFPASSWALK) **then**
`Nav.DeriveTilesIn(dirtyRect)` (EFFBLOCK) — in that order — instead of relying on the static PMP grid.
This makes carved floors immediately walkable, faithfully.

---

## 7. UNRESOLVED / INFERENCE

1. **Exact open-band column fan (RESOLVED).** The band is **31** cells (not 30); all (drow,dcol)
   offsets are now traced directly from the `(iVar8+K)*iVar5 + iVar20 + C` arithmetic (no INFERENCE). The
   earlier 30-cell list had three errors (see §2.2(b) CORRECTED note). The corrected band reproduces value-3
   at 100% and removes 8 spurious value-1 cells. No disasm single-step needed.
2. **Secondary step-confirm reads (RESOLVED).** The primary's second confirm is
   `h(sy-11)+2 < h(sy)` (iVar10 last set at line 307 to `(iVar8-0xb)*iVar5`); the alternate's confirm is
   `h(sy-10)+2 < h(sy+1)` (iVar13 last set at line 330 to `(iVar8-10)*iVar5`). The earlier `h(sy-15)`/`h(sy-8)`
   guesses were wrong. Both reads index the same `pbVar6` height window (via `FUN_0041fad0`, a bounds-checked
   `base+idx`). Verified: the EXACT conditions reproduce the live value-3 split at 100%.
3. **`DAT_00737330`** (carve drop-Z offset, 00596b80:182) — raw `.rdata` float, value not extracted here
   (no symbol entry). Likely `0.5` or `1.0`; confirm from `.rdata` during impl. Non-blocking (cosmetic
   rubble placement).
4. **`tile+0x0c`** (the parallel per-owner order-ID word feeding `+0x250`) — its full layout vs
   `tile+0x08` is not separately RE'd here; treated as a parallel 8×4-bit word. Confirm if wall-dig anims
   misbehave.
5. **EFFOBJPF (`FUN_005a5da0`)** — stage 2, between EFFPASSWALK and EFFBLOCK; not reversed (object-pathfind
   fields, likely not needed for terrain walkability). Out of scope.

---

## Verification notes (adversarial re-check)

Each load-bearing claim was re-checked against the PRIMARY decompile (`re/decompiled/.../<addr>_FUN_<addr>.c`)
and, for EFFPASSWALK, against the live dump via a corrected probe (`tools/probe_effpasswalk_verify.py`).

**CONFIRMED (verbatim against the decompile):**
- CARVE `FUN_00596b80`: the dig-state nibble decode `tile+0x08>>shift → {0,3,7,11,15}` (00596b80:49-60);
  the rim gate `if (local_20 <= rim)` wrapping the carve, else CLEAR `tile+0x08 &= ~(0xf<<shift)`
  (00596b80:63 / 281-282); block re-resolution via aux `+0x4c/+0x48/+0x44/+0x40` accepted iff
  `FUN_004093b0[+0x53/+0x52/+0x51/+0x50] == rim` (00596b80:74-118); dirty bits `+0x13|=1` (123) / `|=2`
  (180); `gnome+0x250 = (tile+0x0c)>>shift` (232); `FUN_005ad040` enqueue (242); no `+0x3c` write. ✔
- RIM `FUN_005968b0`: 4 cells `(sx-1,sy-1)/(sx,sy-1)/(sx-1,sy)/(sx,sy)`, transition `+0x4c/+0x48/+0x44/+0x40`,
  gate `+0x53/+0x52/+0x51/+0x50`, follow-transition iff `cand!=-1 && cand!=self`, `-1` returned only when
  the SE (4th) cell self-paths AND `bVar5==false` (137-139), else `max` of the 4 gate bytes (152-164). ✔
- MAPVALIDATOR `FUN_005a46d0`: EFFPASSWALK (`FUN_005a6b40`, line **156**) runs BEFORE EFFBLOCK
  (`FUN_005a6350`, line **266**), with EFFOBJPF (`FUN_005a5da0`, line 211) between. ✔
- EFFPASSWALK precondition: `local_c1 = ~(*(byte*)(tile+0x18)>>7)&1` of tile `((sx+4)>>3,(sy+4)>>3)`;
  `c1==0 → emit 0` (005a6b40:15). ✔ (window/stride setup §2.1, index formula §2.1 all confirmed.)
- WRITER `FUN_00596550` is the only `+0x3c` writer (writes `tile+0x3c..+0x78`, tile stride `0x7c`, 00596550:67/77-106);
  FILLER `FUN_00595e40` materialises get_hmap from aux `blk*0x59 + cmap[+0x24][+0x10]` into the height window
  (00595e40:76,86-115). `FUN_004093f0`=get_hmap (aux stride 0x59, index `(y&7)<<3|(x&7)`). ✔
- VERIFICATION: value-3 reproduction **1672/1672 = 100%** over the full map — re-confirmed with the EXACT
  (corrected) model. tile(249,30)=block71, +0x3c row4=`03×8`, +0x12=`0x64` — all reproduced. ✔
- PORT GAPS G1-G5: re-checked against `NavWorld.cs` (no EFFPASSWALK port; `_sub` never re-derived from
  `GetHmapSub`; `DeriveAll`/`DeriveTile` = FUN_005a6350 only) and `DiggableTerrain.cs` (`StepCornerDown`/`DigNode`
  block-level carve; `Update`→`RemeshFromArrays`+`_navRebuild`; `RimHeight`=single-tile max). All accurate. ✔

**CORRECTED:**
- **Open band: 30 → 31 cells, two column errors.** Re-traced every `(iVar8+K)*iVar5 + iVar20 + C`: the cell
  `(-10,0)` is actually `(-10,-2)` (005a6b40:338), `(-2,0)` is `(-2,-2)` (line 403), and a 31st cell `(-2,+2)`
  (line 415) was missing. Fixed in §2.2(b) + constants. The corrected band still gives value-3 100% and cuts
  value-1 false-positives (8 cells). The original probe `tools/probe_effpasswalk.py` carried the same 30-cell
  error; superseded by `probe_effpasswalk_verify.py`.
- **Floor/wall split: missing nested confirms.** PRIMARY (line 439) is followed by a SECOND nested compare
  (line 457) `h(sy-11)+2 < h(sy)` — emit 3 only if BOTH hold (the earlier text emitted 3 on `above1+2<here`
  alone). ALTERNATE confirm is `h(sy-10)` (iVar13@330), not `h(sy-15)`. Fixed in §2.2(c) + constants + §7.
- **Minor probe bug noted:** `probe_effpasswalk.py:54` read `t[0x19]` for the `+0x18` precondition; the
  decompile reads the byte at `+0x18` (00596b80-class `*(byte*)(p+0x18)>>7`). `probe_effpasswalk_verify.py`
  uses `t[0x18]`. (Did not change the value-3 result on this dump.)

**LABELING CAVEAT (not a substantive error):** §1.4/claim-2 label `+0x40/+0x44/+0x48/+0x4c` as `SE/SW/NE/NW`,
while the port (`DiggableTerrain.DigNode`) calls the `+0x40`-paired cell `(sx,sy)` "NW". The offset↔cell↔gate
pairing is correct either way (`+0x40↔(sx,sy)↔+0x50`, …); only the human corner-name convention differs.

**UNVERIFIABLE / out of scope:** `DAT_00737330` float value (no symbol in extract); `tile+0x0c` full layout;
`FUN_005a5da0` (EFFOBJPF) internals — all already flagged in §7 and unchanged.
