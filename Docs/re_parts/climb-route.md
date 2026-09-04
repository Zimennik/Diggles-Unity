> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# climb-route — The Climb ROUTE BUILDER (FloorPathMap), Reverse-Engineered

> **⚠️ ATTRIBUTION CORRECTION:** the
> FloorPathMap documented here is **NOT the climb route builder** — it is the **FLOOR-segment
> depth-profile resolver**. Proof: this doc's own §3 band formula `[h(sy−1)·4+1, h(sy+1)·4−1)` is
> **EMPTY on every mid-wall cell of a real cliff** (h is constant along a wall face — probed
> x=2059 y 242..272: h ≡ 11 → band `[45,43)` → all 64 rows masked), so the §4 flood can never
> descend a sheer wall and §7's "straight vertical shaft descent" walkthrough is internally
> inconsistent with §3 on real data. The bands are non-empty exactly ON floor lines (hA < hB):
> the grid picks WHERE IN DEPTH the dwarf walks along a floor polyline (the §3 lip-snap and the
> staircase columns in the probe confirm it). The actual climb route is `CAct_Climb`'s record
> mirror of the path's own sub-cells at `FUN_004238e0` support depths — see climb-exec.md §1.2
> (whose verifier already concluded the climb consumes flag-0 records built straight from the
> path source, with no flood involved). The §2-§6 ALGORITHM below is disasm-verified and stands;
> only the "climb"/"wall" framing of §0/§7 is wrong.
>
> Also corrected from the decompile (`FUN_0041c4b0:155`): a tile whose gate byte `+6 == −1`
> (a SOLID tile, BaseCost −1) keeps the DEFAULTS `floorH=1000 / ceilH=0` → the whole column is
> masked too-low — **fully blocked**, not "left fully open" as §3 claims.

> Ground-truth RE of how Diggles (2001) builds the *route* over a wall/cliff for one MetaWalk
> segment: the `CAct_WalkFloor` route builder `FUN_0041aac0`, the cost-grid initialiser
> `FUN_0041c4b0`, the point-list densifier `FUN_0041bff0`, the **5-pass Dijkstra flood + steepest
> descent backtrace** in `FUN_0041d8a0`, and the mount/dismount free-cell searches `FUN_0041bbd0`
> (±16 horizontal) / `FUN_0041bde0` (±256 vertical). The *execution* of the resulting waypoints is in
> [`climb-exec.md`](climb-exec.md) (`CAct_Climb`/`CAct_WalkAnim`); this doc is the BUILD side only.
>
> Every claim cites `FUN_addr:line` from `re/decompiled/Diggles/_global/*.c`. All float `DAT_*`
> constants were extracted directly from `original/Diggles.exe` (imgbase 0x400000, `.rdata` VA
> 0x715000 / raw 0x313e00) and the 5-pass neighbour offsets / cost-table indices were verified by
> Capstone disassembly of `FUN_0041d8a0` (`.text` raw 0x400). Numbers below are not inferred from the
> Ghidra C alone — the pointer-arithmetic was confirmed against the real machine code (see §4.3).
>
> **Frame note (project ground truth):** native X = horizontal/east (lateral); native Y = the
> gameplay vertical (+y deeper, the climbing axis); the BDB height nibble = SLAB DEPTH into the screen
> (the dig axis, weighted ×0.5; engine z = nibble·0.5+7, 7..14.5). The FloorPathMap grid does NOT use
> raw tile X/Y for its two axes — see §3 (col = point-list arc index along the segment centreline;
> row = `round(world.z·4)` = the depth axis). The waypoint world coords come from the point list
> (which is in the tile plane), so the grid's "row" is the slab/depth dimension.

---

## 0. Overview & call graph

`CAct_WalkFloor` (the floor+wall route action) lazily builds a route ONE SEGMENT AT A TIME. Its tick
`FUN_0041aef0` calls the builder `FUN_0041aac0`, which:

1. Clamps the segment endpoints into the segment's own bbox (±3 cells of margin) (`0041aac0:62-85`).
2. Densifies the segment's polyline into a **FloorPathMap point list** via `FUN_0041bff0` (one point
   per ~1.0 world unit; `param_1[2]` array of 12-byte `{x,y,z=15.0}` records) (`:92`).
3. Builds the **cost grid** (`uint32[points·64]`) and stamps the per-column too-low/too-high blocked
   bands from the BDB heightmap via `FUN_0041c4b0` (`:93`).
4. Derives the **goal depth** on the wall: `h - 1.0` if the segment's final dx<0, else `h - 0.5`
   (`:97-124`; `h` from `FUN_00424470`, dx-sign from `FUN_00423be0`).
5. Runs the flood+backtrace `FUN_0041d8a0` to emit a `CPath` of `{x,y,z,flag=2}` waypoints into
   `owner[+0x1c]` (`:173-174`).
6. Advances the segment index `owner[+0x40]++` and returns a fresh `CSubAction` (`:29`).

```
CAct_WalkFloor::tick = FUN_0041aef0
   └─ FUN_0041aac0 (build route for segment [owner+0x40])
        ├─ FUN_0041bff0   densify segment polyline → FloorPathMap point list  (param_1[2], n=param_1[3])
        ├─ FUN_0041c4b0   alloc uint32[n·64] cost grid + stamp blocked bands from BDB heights
        ├─ FUN_00424470   BDB height nibble at goal sub-cell (row+2 bias)          ─┐ goal-depth
        ├─ FUN_00423be0   final-segment dx (sign only)                              ─┘ (h-0.5 / h-1.0)
        └─ FUN_0041d8a0   THE flood + backtrace  ───────────────────────────────────────────────┐
              ├─ seed/goal point-index search (nearest point to start / to goal)                 │
              ├─ FUN_0041bbd0  mount cell search   (±16 horizontal free-cell)                     │
              ├─ FUN_0041bde0  dismount cell search (±256 vertical free-cell)                     │
              ├─ 5-pass Dijkstra flood FROM the seed cell (FIFO frontier owner[+8], grows +0x1000)│
              ├─ steepest-cost backtrace FROM the goal cell                                       │
              └─ emit CPath waypoints {x,y,z, flag=2}; flood-path settle-loop (y+=0.125)          │
                                                                                                  │
   result CPath ─▶ owner[+0x1c] ──▶ consumed by CAct_Climb (see climb-exec.md) ◀─────────────────┘
```

`FUN_0041aef0:59-66` also has the "near final target → finish early (state 5)" check: if
`owner[+0x7b4]==0` (Calc1 mode) and the squared distance from the current sub-action target to the
owner's stored goal (`owner+0x7a4/7a8/7ac`, depth weighted ×`DAT_0073708c`=0.5) is below
`owner+0x7c8`, set state 5 and stop. (`DAT_00736f78` gate at `:59`.)

---

## 1. The FloorPathMap struct (this = param_1 of FUN_0041d8a0 / FUN_0041c4b0)

Reconstructed from `FUN_0041bb10` (ctor), `FUN_0041bff0` (point/grid build), and all index sites in
`FUN_0041d8a0`/`FUN_0041c4b0`. Word offsets (×4 bytes):

| Field | Meaning | Evidence |
|---|---|---|
| `[0]` | `uint32* grid` (cost+flags, `n·64` entries, **col stride 0x40=64**) | `0041bff0:200`, idx `col*0x40+row` `0041d8a0:375,453,...` |
| `[1]` | grid length = `n << 6` | `0041bff0:201`; bounds-check `0041d8a0:377,454` |
| `[2]` | `Point* pts` (12-byte records `{float x, float y, float z}`) | `0041bff0:159-161,194-196`; read `0041d8a0:158-161` |
| `[3]` | `n` = point count | `0041bff0:98,151,175`; loops `0041d8a0:149,204` |
| `[4..7]` | bbox `{minCol, minRow, maxCol, maxRow}` (col=x·8, row=y·8) | `0041bff0:84-139`; used by `0041c4b0:38-44` |
| `[8]` | `Cell* frontier` (FIFO, 8-byte `{int col, int row}`) | `0041bb10:32-33`; `0041d8a0:429-432,500` |
| `[9]` | frontier capacity (starts 0x1000) | `0041bb10:34`; grow `0041d8a0:637-643` |
| `[10]` | frontier grow chunk / cap2 (0x1000) | `0041bb10:31`; `0041d8a0:637-639` |
| `[0xb]` (byte) | "invalid/clamped" flag (1 ⇒ bail, no route) | set `0041c4b0:43,50,160`; gate `0041d8a0:139` |

`FUN_0041bb10` zeroes everything, allocates the 0x1000-entry frontier, and sets `[0xb]=1`
(`0041bb10:35`). `FUN_0041c4b0:43` clears `[0xb]=0` and re-sets it to 1 if the bbox spills off the
loaded world or a sampled tile is out of range — that is the "give up, segment too close to the world
edge" path.

**Cell word layout** (the `uint32` at `grid[col*64+row]`): `[31:24]` = blocked/visit mask, `[23:0]` =
Dijkstra cost.
- `0x10000000` = too-low band (below the floor surface for that column), `FUN_0041c4b0:212`.
- `0x08000000` = too-high band (above the column's ceiling), `FUN_0041c4b0:218`.
- `0x80000000` = chosen mount cell (set by `FUN_0041bbd0`/`bde0`), `0041bbd0:107`, `0041bde0:100`.
- `(cell & 0xff000000) != 0` ⇒ cell impassable for the flood (`0041d8a0:388`, `0041bbd0:30,69`).
- `(cell & 0xffffff)` ⇒ the accumulated cost (backtrace reads only these low 24 bits, `0041d8a0:691`).

---

## 2. Point-list densifier — FUN_0041bff0

Called as `FUN_0041bff0(this, srcPath, segStartIdx, segEndIdx, /*stride*/0x20, /*flag*/0)` from
`0041aac0:92`. `param_5` (the trailing "already a CPath?" byte) is 0, so it first slices the segment
out of the source path via `FUN_0041f320(&tmp, srcPath, segStart, segEnd, 0x20, this)` →
`CPath::vftable` object (`0041f320:42`), then walks that sliced path.

- Resets `this[3]=0` (`:75`), copies endpoint into bbox seeds `this[4..7]` (`:84-88`).
- For each pair of consecutive control points `(p0,p1)`: tracks the bbox (`:119-139`), then **emits
  one point every `DAT_00737138`=1.0 world unit** along the segment (`:143-162`): interpolated
  `x = lerp·DAT_0073703c`, `y = lerp·DAT_0073703c` (the `·0.125`/8 here is the world↔point scale used
  consistently), and **z:= `0x41700000` = 15.0** for EVERY emitted point (`:159,194`). 15.0 is the
  full/undug slab nibble — the route assumes the wall face is at full depth and the flood will pull it
  in. The remainder accumulator `param_5` carries fractional arc-length between pairs (`:163`).
- After the last pair, appends the final endpoint (`:168-196`), z again 15.0.
- Allocates `grid = calloc(n·256 bytes)` i.e. `uint32[n·64]` and sets `this[1]=n<<6`,
  `memset(grid,0,n<<8)` (`:197-202`). The grid is **column-major: column = point index, 64 rows per
  column**.

So the **column axis of the FloorPathMap = the densified arc-length index along the segment**, NOT a
spatial X. Each column owns the `(x,y)` of one point; the only thing the BFS chooses per column is the
ROW (the z/depth slot).

---

## 3. Cost-grid init & blocked bands — FUN_0041c4b0

`FUN_0041c4b0` fills the grid's `[31:24]` blocked mask from the BDB heightmap so the flood can only
travel through legally-standable depth slots per column.

- Computes a padded bbox (`min-8.. max+8`, `0041c4b0:38-44`) and pulls a **local height tile**
  `_Dst[(row-y0)*W + (col-x0)]` from the BDB via `FUN_00595e40` (`:57`), masking each byte to its low
  nibble (`& 0xf`, `:61`) = the per-sub-cell slab depth. If the bbox leaves the loaded world it stamps
  `0x0f0f0f0f` everywhere and sets `[0xb]=1` (give up) (`:47-51`).
- **Per-point "step" classification (`:66-134`):** for each point it walks the local height map
  vertically (the `iVar14` row stride) and, where the height delta between a cell and the one above is
  `> 2` (`2 < (*here - *above)`, `:94,119`), records that the point's wall starts there — it nudges
  the point's `y` (`pts[i].y = (row+δ)·0.125`, `:99,123`) so the densified point sits exactly at the
  cliff lip. `δ` is searched within `[0,3)` (`:79-129`). This is the "snap the point onto the wall
  edge" step. (Heights from `FUN_00595e40`; same source as `get_hmap`.)
- **Per-point blocked-band stamping (`:135-226`):** for each point/column it samples the world tile at
  `(round(x·8)>>3, (round(y·8)-1)>>3)` (`:151-152`, the `-1` looks one row up), reads two heightmap
  bytes — `local_38` (the floor height for this column, `:198`) and `local_40` (the height two rows
  down, `:205`) — then for every row `r in [0,64)` of the column (`:22`):
  - if `r < local_38·4 + 1` → set `0x10000000` (too-low / inside the floor) (`:211-215`),
  - else if `r >= local_40·4 - 1` → set `0x08000000` (too-high / past the ceiling) (`:217-219`).

  i.e. the legal climbable band per column is `[floorHeight·4+1, ceilHeight·4-1)`. The `·4` is the
  same `DAT_007374a0`=4.0 row scale used by the flood (§4.1). Tiles whose gate byte `tile[+6]==-1`
  (`:189`) are skipped (left fully open). The big tile struct is the L2M cell array `DAT_0077241c`
  (stride 0x7c=124; sub-grid at +0x3c; heightmap region pointer at +0x10/+0x14, count +0x18, etc).

---

## 4. THE flood + backtrace — FUN_0041d8a0

Ghidra mangled the tail of this function badly (≈70 "unreachable block" warnings, `0041d8a0:7-69`);
the reachable C plus the verified disassembly are enough to reconstruct it fully. ABI is
`__thiscall`: **this = the FloorPathMap** (ECX), explicit args pushed at `0041aac0:0x41ae09-ae14`:
`param_2 = &start.xyz`, `param_3 = &goal.xyz`, `param_4 = climb-flag (uVar8)`, `param_5 = startFlag`,
plus a trailing literal `0x10` Ghidra folded away. The body uses `param_5` as the **mount-search
selector** (0 ⇒ horizontal `FUN_0041bbd0`, else vertical `FUN_0041bde0`) and `cVar6 = (param_5==0) ?
param_4: 0` as the **"force a 3-point straight path" flag** (`:140-143`).

### 4.1 Seed / goal cell derivation (`:144-188`)

- Loop over all `n` points; `local_48` = index of the point **nearest the START** (`param_2`),
  `local_58` = index of the point **nearest the GOAL** (`param_3=local_88`), both by squared 3-D
  distance, with `local_34/local_40` init `DAT_007376fc = 1e10` (infinity). Early-out once both are
  within `DAT_00736fe0 = 0.01` (`:163-164,174-176`).
- **Cell = (column, row)** where `column = point index` and `row = round(world.z · DAT_007374a0=4.0)`:
  - `local_80 = round(start.z · 4)` → **goal-cell row**, paired with `local_84 = local_48` (col).
  - `local_8c = round(goal.z · 4)` → **seed-cell row**, paired with `local_90 = local_58` (col).
- So: **BFS seed (flood origin) = (col=`local_90`=nearestGoalPt, row=`local_8c`=round(goalZ·4))**;
  **BFS goal (backtrace origin) = (col=`local_84`=nearestStartPt, row=`local_80`=round(startZ·4))**.
  The flood runs goal→start and the backtrace start→goal; the emitted path is therefore start→goal.

The seed and goal cells are then snapped to a free cell by the mount searches (§5): `FUN_0041bbd0`
on the goal cell (`local_84`, `:189`), and per-iteration `FUN_0041bbd0`/`bde0` on the seed cell
(`local_90`, `:196-201`).

### 4.2 Frontier seed & straight-line LOS short-circuit (`:203-431`)

Before flooding, `:203-391` does a **straight-line passability test** between seed and goal cells:
march `iVar9+1 = floor(dist)+1` steps along the line `(col,row) = lerp(start,goal)`, computing flat
idx `= trunc(lerpCol)·0x40 + trunc(lerpRow)` (`:375-376`; the lerp components are int-cast/`cvttss2si`
= TRUNCATED, not rounded — disasm `0x41dc96/0x41dca5` — contrast the `ROUND` used for the z·4 row
conversions), and if every cell on the line is passable
(`(cell & 0xff000000) == 0`, `:388`) it jumps to `LAB_0041f026` (`:248`) and emits a **2-point path**
(`cVar6==0` or endpoints coincide) or **3-point path** (else) — NO flood. The 2-pt path =
`{start, {local_48,local_7c,local_40}}` (`:259-300`); the 3-pt path inserts both endpoints and the
goal (`:302-360`). `local_40` (the emitted z) `= local_8c · _DAT_00736fec(1/64) · DAT_0073756c(16) =
local_8c · 0.25 = row/4` — the inverse of the `z·4` row conversion.
> **⚠ CORRECTION:** at the emit, `{local_48, local_7c}` is the
> **GOAL cell's (x,y)**, NOT the start's: `local_48` is REUSED — it is the start-nearest index in
> the §4.1 search loop, then **reassigned** at `:235-239` (`piVar20 = local_90; local_48 =
> pts[piVar20].x; local_7c = pts[piVar20].y` with `local_90 = local_58` = the GOAL-nearest col).
> So the LOS mid = `{pts[goalCol].x, pts[goalCol].y, goalRow·0.25}` — the depth change happens
> GRADUALLY across the lateral start→mid traverse. Reading the old "local_48 (start)" label
> literally made the port build the mid at the START's (x,y) → a pure in-place depth dive at walk
> start (gnomes "flew to the far wall"). This is the "no climb needed, walk straight" case.

If the LOS is blocked, `:392 LAB_0041dcfb` builds the **cost table** and runs the flood.

### 4.3 The 5-pass Dijkstra flood (`:392-666`) — EXACT offsets & costs

**Cost table** (`local_28[]`, built `0041d8a0:393-415`; verified by disasm — the table base is
`ebp-0x24`, indexed `[ebp + pass*4 - 0x24]` at `0x41df4a`):

| pass | cost | source const |
|---|---|---|
| 0 | **1280** | `DAT_007374cc`·256 = 5.0·256 |
| 1 | **512** | `DAT_00737330`·256 = 2.0·256 |
| 2 | **256** | `DAT_00737138`·256 = 1.0·256 |
| 3 | **1280** | (5.0·256 reused) |
| 4 | **1024** | `DAT_007374a0`·256 = 4.0·256 |

(`DAT_00737658 = 256.0`. The C `local_28[4]` reads the adjacent stack slot `ebp-0x14`; disasm shows it
holds the 4.0·256=1024 store at `0x41ddb0/0x41dde4`. The pass-3 value is the SAME 5.0·256 that was
spilled to `ebp-0x18` — `local_28[3]`.)

**Per-pop neighbour set.** The flood is a FIFO (`local_40` read head, `iVar9` write head,
`0041d8a0:434-647`). For each popped cell `(C,R)` it does 5 passes × 4 directional checks = **20
candidate neighbours**. Each check: `newCost = poppedCost - cost[pass]` (`:485`, `sub eax,
[ebp+pass*4-0x24]` at `0x41df4a`); if `newCost > grid[neighbour]` (Ghidra's `grid[nb] < newCost`,
`:495`) push the neighbour and store `grid[nb]=newCost`. Hand-traced (with the Ghidra `int*`
"+/-0x10" rendered as ±0x40 bytes, confirmed by `sub [ebp-0x5c],0x40` at `0x41df15`), the 20 `(Δcol,
Δrow)` offsets per pass are:

| pass | cost | check A | check B | check C | check D |
|---|---|---|---|---|---|
| 0 | 1280 | (−1,−2) | (+2,−1) | (+1,+2) | (−2,+1) |
| 1 | 512  | (−1,−1) | (+1,−1) | (+1,+1) | (−1,+1) |
| 2 | 256  | (−1, 0) | ( 0,−1) | (+1, 0) | ( 0,+1) |
| 3 | 1280 | (−1,+2) | (−2,−1) | (+1,−2) | (+2,+1) |
| 4 | 1024 | ( 0,+2) | (−2, 0) | ( 0,−2) | (+2, 0) |

Reading it as a neighbourhood: **pass 2 = the 4 cardinals (cost 256)**, **pass 1 = the 4 diagonals
(cost 512)**, **pass 4 = the 4 two-step cardinals (cost 1024)**, **passes 0+3 = the 8 knight moves
(cost 1280)**. Bounds: each check is gated `0 ≤ row < 0x40` and `0 ≤ col < n` before indexing
(`:486,521,556,591`). Goal-reach bookkeeping: when a pushed neighbour equals the GOAL cell
`(local_84,local_80)` it records the hop count into `local_4c` (`:515-518`, etc); the outer loop
stops once the read head passes the write head or the goal was reached (`:647`). Frontier grows
`+0x1000` when `writeHead+0x10 ≥ cap` (`:637-643`,
`"C:\dev\wiggles\Wiggles\Wiggles\Actions\FloorPathMap.cpp":0x32f`).

**Re-seed for descent (`:648-666`):** after the flood, if the GOAL cell got no cost
(`grid[goalCell] & 0xffffff == 0`) AND it is not a forced-climb (`cVar6==0`) AND not already retried
(`local_75==0`), it re-seeds from `local_58` at `row = round(goal.z·4)` and floods again (`:661-665`).
This is the "couldn't reach by climbing up; try from the other depth" retry. Otherwise it falls
through to `LAB_0041e385` (the backtrace).

### 4.4 The steepest-cost backtrace (`:670-836`)

`LAB_0041e3ef` walks from the GOAL cell `(local_44=col, local_94=row)` **up the cost gradient** to the
seed: it inspects the SAME 20 neighbours (hand-traced identical to §4.3 — verified all-OK) and moves to
the neighbour with the **largest** stored cost (`uVar12 < grid[nb]&0xffffff` keeps the max,
`:708,733,757,781`), since the flood made the seed the global maximum. Each accepted step appends the
cell to the output. The loop terminates when `(local_90,local_8c) == (local_44,local_94)` (reached the
seed, `:849`) → builds the climb `CPath`; the per-step waypoint world is `{x=pts[col].x, y=pts[col].y,
z=row·0.25}`, **flag = 2** uniformly (the WALL flag CAct_Climb keys on). The Ghidra-collapsed
`LAB_0041e3d2` emit block (`:851-958`) is the path materialiser: a `CPath` (`FUN_006ba810(0x3d8)`,
vftable `:856-858`) whose interior records are `pts[idx]` with `z = round(...)·1/64·16` (=row/4) and
flag 2; the first record is the start (`param_2`), the last is the goal (`local_88`).

### 4.5 The y += 0.125 settle loop (`:960-1027`) — EXACTLY which waypoints, and termination

**[CORRECTED — see Verification notes §V.2.]** This loop runs on the **flood/backtrace-materialised
path at `LAB_0041e3d2`** (`:848-1028`), which is reached ONLY after the 5-pass flood ran
(`LAB_0041dcfb`→`:659 goto LAB_0041e385`→`LAB_0041e3d2`). It does **NOT** run on the
straight-LOS short-circuit path: that path materialises its 2/3-point `CPath` at `LAB_0041f026`
(`:248-360`) and `break`s out of the outer loop (`:299,358,363`) to the function exit
(`:669 goto LAB_0041f2f7`) WITHOUT reaching the settle. So the settle operates on the
flood-derived (Dijkstra) waypoint list, over its **interior waypoints** only: `iVar9 = 1.. count-2`
(`:960-961,1026` loop bounds exclude waypoint[0] and the last). For each interior waypoint:
- Convert its world to sub-cells: `subX = round(wp.x·8)`, `subY = round(wp.y·8)` (`DAT_0073752c=8`,
  `:973,984`).
- Index the L2M tile `(subX>>3, subY>>3)` (clamped to map bounds via `DAT_0077241c[0x21..0x24]`,
  `:987-1003`; stride 0x7c, modulo `[0x13]`), and read the `+0x3c` sub-grid byte at
  `((subY&7)<<3 | (subX&7))` (`:1013`).
- **While `(subgridByte & 2) != 0`** — i.e. while the waypoint sits on a FLOOR sub-cell (bit 1 = the
  walk|floor "ground-line" bit) — do `wp.y += DAT_0073703c (0.125)` and re-test (`:1013-1022`). One
  0.125 step = one sub-cell along +Y (deeper). **Termination:** the first sub-cell whose bit 2 is
  clear (`break`, `:1013-1014`).

In words: it **slides each interior waypoint of the materialised flood path along +Y (deeper) one
sub-cell at a time until it is no longer standing on the solid floor line** — settling a waypoint off a
floor edge so the dwarf can descend cleanly. It never touches the endpoints (waypoint 0 and the last,
which carry the exact `param_2`/`local_88` coords). There is no count limit other than the grid; in
practice it stops within a few sub-cells because the column transitions off the floor bit quickly.

---

## 5. Mount / dismount free-cell searches

Both take `param_1 = &grid-base-ptr-pair` (so `param_1[1]`=grid length, `param_1[3]`=n) and
`param_2 = {int col, int row}` (mutated in place to the chosen free cell). "Free" = passable AND
zero-cost: `(cell & 0xff000000) == 0` AND `(cell & 0xffffff) == 0` (`0041bbd0:30-31`,
`0041bde0:30-31`). Both use `FUN_00405920` = bounds-checked flat accessor `grid + idx*4`, idx =
`col*0x40 + row` (`00405920:10-14`).

### 5.1 FUN_0041bbd0 — HORIZONTAL mount search (±16)

- If the input cell is already free, return true (`:29-33`).
- Else scan a window **row ∈ [row−0x10, row+0x10)** clamped `[0,0x40)` (`:34-39`) ×
  **col ∈ [col−0x10, col+0x10)** clamped `[0, n)` (`:41-54`; the lower clamp is `if((int)uVar2<1)
  uVar2=0` ⇒ floor at 0, **not** 1 — corrected from an earlier "[1,n)"), keep the free cell with the
  smallest squared distance `(Δcol²+Δrow²)` to the input (`:78-84`), write it back into `param_2`
  (`:92-93`).
- If NO free cell was found in the window (`local_20 == 0x7fffffff`): mark the (clamped) cell with
  `0x80000000` (chosen-mount bit) and return **false** (`:95-112`) — i.e. "no room, give up". When a
  cell WAS found it returns true.

The `±0x10=16` window is the **lateral/horizontal mount reach** (16 point-columns ≈ 2 world units).

### 5.2 FUN_0041bde0 — VERTICAL dismount search (±256)

Structurally identical, but anisotropic: the row window is **row ∈ [row−0x100, row+0x100)** clamped
`[0,0x40)` (`:34-38`). **[CORRECTED — see Verification notes §V.1.]** The column window is NOT
`[col−1, col+1)`; it is a **single column `[col−1, col)` = only `col−1`**. The lower col bound is
`iVar8 = col−1` (clamped ≥0, `:43-46`) and the UPPER col bound is `iVar4 = col` itself (clamped ≤ n,
`:47-49`; there is no `+1`, unlike `bbd0`'s `*param_2+0x10`). The inner loop `iVar9 = iVar8 = col−1;
while (iVar9 < iVar4 = col)` therefore executes for `iVar9 = col−1` only (disasm `0x41be88 lea
edi,[edx-1]`; `0x41be92 cmovge edx,[ebx+0xc]` i.e. col clamp with no `+1`; `0x41bed2 cmp esi,edx; jge`
exits after one column). So it searches a tall ±256-row band but in the single column to the LEFT of
the input (col−1). Same free test, same nearest-by-distance pick, same `0x80000000`-and-false on
failure (`:95-104`).

`FUN_0041d8a0` uses `bbd0` (horizontal) when `param_5==0` and `bde0` (vertical) when `param_5!=0`
(`:196-201`). The seed cell `local_84` is always snapped with `bbd0` first (`:189`).

---

## 6. Goal-depth derivation — `h-0.5` / `h-1.0` (FUN_0041aac0 + 00424470 + 00423be0)

The wall-face target depth is set in `FUN_0041aac0:97-124` and fed as `goal.z = local_14`:
- If the current cell still equals the segment's start cell (`uVar9 == owner[+0x24]`, `:97`), the goal
  z is just `owner[+0x30]` (the stored target) (`:98`).
- Otherwise `h = FUN_00424470(...)` = the BDB height nibble at the goal sub-cell with a **+2 row bias**
  (`00424470:19,29`: samples sub-cell `(x&7, (y+2)&7)` of block `(x>>3,(y+2)>>3)`), and:
  - `dx = FUN_00423be0(idx, segmentList)` = `lastX − prevX` of the final segment (the horizontal
    approach direction, sign only) (`00423be0:35`).
  - **dx < 0 → `goal.z = h − DAT_00737138 (1.0)`** (`0041aac0:111-116`); approaching the wall from the
    +X side, stand one full slab back.
  - **dx ≥ 0 → `goal.z = h − DAT_0073708c (0.5)`** (`0041aac0:118-123`); approaching from −X, stand a
    half slab back.

So the dwarf's wall-mount depth is the local slab height minus a half/whole step, chosen by which side
of the wall it is coming from. (`h` is the raw nibble; the half-vs-whole offset is the only dx-sign
dependence.)

---

## 7. The route for a STRAIGHT VERTICAL DESCENT into a shaft ending on a floor line

Putting §1–§6 together for the canonical "drop straight down a shaft, land on the floor" case
(start = floor lip at the top, goal = floor sub-cell at the bottom, same X column):

1. `FUN_0041bff0` densifies the segment's vertical centreline into ~`Δdepth` point-columns, each at
   the shaft's `(x,y)` with z=15 (full slab).
2. `FUN_0041c4b0` stamps each column's legal band: too-low band `[0, floorH·4]` and too-high band
   `[ceilH·4, 64)`, leaving the open shaft rows passable.
3. Seed cell = (col nearest the bottom goal, row=`round(goalZ·4)`); goal cell = (col nearest the top
   start, row=`round(startZ·4)`).
4. The straight-LOS test (§4.2) marches the line top→bottom; in a clean open shaft every cell is
   passable, so it **short-circuits to a 2-point straight path** `{start, bottomGoal}` (NO flood). NOTE
   the y+=0.125 settle loop (§4.5) does NOT run on this LOS path — it runs only on the flood/backtrace
   path materialised at `LAB_0041e3d2` (corrected; see §V.2). The 2-point LOS path is emitted verbatim.
   - If the shaft is NOT a clean LOS (a jog, an overhang, a partial dig), the 5-pass flood runs:
     because the cost weights make **cardinal moves cheapest (256) and 2-steps/diagonals dearer
     (512–1280)**, the cheapest flood path hugs the open column straight down, descending one
     depth-row (the `(0,±1)` cardinal, cost 256) per step. The backtrace then walks that column
     bottom→top and emits `{x, y, z=row·0.25, flag=2}` waypoints — i.e. a stack of WALL waypoints down
     the shaft.
5. Mount at the top lip: `FUN_0041bbd0` (±16 horizontal) snaps the entry onto the nearest free shaft
   column. Dismount at the bottom: `FUN_0041bde0` (±256 vertical) snaps the exit onto the deepest free
   row that lands on the floor line.
6. The emitted `CPath` (flag-2 waypoints, descending z=row·0.25) is handed to `CAct_Climb`
   (climb-exec.md), which spawns per-leg `CAct_WalkAnim` movers choosing the climb-down clip from each
   leg's row-decreasing geometry, and at the last (floor) waypoint applies the wall→floor handoff.

**Net:** a clean shaft = a 2-point straight descent with a floor settle; a non-trivial shaft = a
cardinal-cheapest Dijkstra column of flag-2 waypoints. Either way the route ends ON the floor line
(the bottom point sits at the floor sub-cell because the dismount search lands it there and the floor
settle keeps walk waypoints off the lip).

---

## 8. Constants

| Const | VA | Value | Role |
|---|---|---|---|
| `DAT_0073703c` | 0x73703c | 0.125 (1/8) | point↔world scale; settle step (`y+=0.125`); cost-grid lip nudge |
| `DAT_0073752c` | 0x73752c | 8.0 | world→sub-cell (`round(x·8)`) in the settle loop & 0041c4b0 |
| `DAT_007374a0` | 0x7374a0 | 4.0 | world.z→grid ROW (`round(z·4)`); also pass-4 cost weight |
| `DAT_00736fec` | 0x736fec | 0.015625 (1/64) | row→world z reconstruction (`row·(1/64)·16`=row/4) |
| `DAT_0073756c` | 0x73756c | 16.0 | paired with 1/64 above (and = the ±16 horiz literal as float) |
| `DAT_00737138` | 0x737138 | 1.0 | densifier spacing; goal-depth `h−1.0` (dx<0); cost-2 weight |
| `DAT_0073708c` | 0x73708c | 0.5 | goal-depth `h−0.5` (dx≥0); depth weight in finish-early dist |
| `DAT_00737330` | 0x737330 | 2.0 | cost-1 weight |
| `DAT_007374cc` | 0x7374cc | 5.0 | cost-0 / cost-3 weight |
| `DAT_00737658` | 0x737658 | 256.0 | cost multiplier (all costs ×256) |
| `DAT_007376fc` | 0x7376fc | 1e10 | distance-search "infinity" init |
| `DAT_00736fe0` | 0x736fe0 | 0.01 | seed/goal "close enough" epsilon (squared) |
| point z init | — | 15.0 (`0x41700000`) | full/undug slab nibble for densified points |
| **5-pass costs** | — | **{1280,512,256,1280,1024}** = {5,2,1,5,4}×256 | flood per-pass |
| col stride | — | 0x40 = 64 | grid `col*64+row` |
| mount window | — | ±0x10 = ±16 (horiz) | `FUN_0041bbd0` |
| dismount window | — | ±0x100 = ±256 row / **single col `col−1`** (vert) | `FUN_0041bde0` (corrected: col window is `[col−1,col)`, not ±1) |
| floor bit | — | `& 2` of `+0x3c` sub-grid | settle-loop floor test |
| frontier grow | — | +0x1000 = +4096 | `FUN_0041d8a0:637`; `FloorPathMap.cpp:0x32f` |

---

## 9. Tcl ↔ native contract

The route builder is entirely below the Tcl line. Tcl `action <o> walk "<opts>"` builds a
`CAct_MetaWalk` whose floor/wall segments are routed by `CAct_WalkFloor` (this doc) and executed by
`CAct_Climb` (climb-exec.md). The only Tcl-visible outputs are:
- `get_walkresult <o>` (`FUN_0068fb80`) reads the resulting sub-action state (3 = NOPASSAGE / no route
  could be built; 4 = arrived; 2 = walking; 5 = finished-early). `FUN_0041aac0:211` sets state 3 when
  `FUN_0041d8a0` produced no `CPath` (`owner[+0x1c]==0`); state 4 when the segment index ran out
  (`:41`).
- `-canclimb 0` (box carriers): gates `CAct_Climb` at execution time (owner[+0x7b0]), NOT the route
  build — the route is still computed, but climbing is refused at exec, surfacing as state 3.

No script reads waypoints; they are consumed natively. See `tcl-work-layer.md` / `metawalk-core.md`
for the surrounding contract.

---

## 10. PORT GAP ANALYSIS vs current code

Current port: `Assets/Diggles/Runtime/Tcl/Engine/ClimbAction.cs` (+ orphaned
`Runtime/Engine/Actions/WalkClimbAction.cs`). It is the **invented choreography** already flagged in
`Docs/ORIGINAL_PATHFINDING_RE.md §5.3`. Concrete divergences from this RE:

1. **No FloorPathMap at all.** The port has no densified point list, no `uint32[n·64]` cost grid, no
   blocked-band stamping, no Dijkstra flood, no backtrace. `ClimbAction.Build` (`ClimbAction.cs:86`)
   hand-synthesises mount→crawl→climb→dismount legs from a couple of nav waypoints. The route must be
   rebuilt as: densify segment (§2) → stamp bands (§3) → seed/goal cells (§4.1) → LOS short-circuit or
   5-pass flood (§4.2-4.3) → steepest-cost backtrace (§4.4) → settle (§4.5).
2. **Wrong grid axes.** Port uses world `Up=+Y, Right=+X, depth=+Z` (`ClimbAction.cs:20,89`). The
   original grid is `col = densified point index along the segment`, `row = round(world.z·4)` (the
   depth axis); waypoint world comes from the point list, z=row/4. The port's "up=+Y climb axis" has
   no analogue in the builder.
3. **Fabricated move codes.** `enum Kind{Mount,CrawlLeft,CrawlRight,ClimbUp,ClimbDown,Dismount,Jump}`
   (`ClimbAction.cs:28`) — the original waypoint flag is **uniformly 2**; direction is derived per-leg
   inside `CAct_WalkAnim` (climb-exec.md), not encoded in the route.
4. **Wrong costs / neighbourhood.** Port has no weighted neighbour passes; the original is the exact
   5-pass `{1280,512,256,1280,1024}` table over the 20 offsets in §4.3 (cardinals cheapest).
5. **Missing mount/dismount searches.** No ±16 horizontal / ±256 vertical free-cell snap; the port
   instead offsets by a hand-tuned `_climbStandoff=0.2` along the wall normal (`ClimbAction.cs:101`).
6. **Missing goal-depth derivation.** No `h−0.5`/`h−1.0`-by-dx-sign mount depth (§6); the port stands
   off by a fixed normal offset.
7. **Missing floor settle.** No `y+=0.125`-until-clear-of-floor-bit for interior waypoints of the
   flood/backtrace path (§4.5; corrected — applies to the flood path, not the LOS path).
8. **Speed/arrival wrong (executor side, but rooted here).** `height·0.47` climb speed
   (`ClimbAction.cs:268-273`) and arrival 0.12 (`:75`) instead of root-motion + the real flag-driven
   legs — see climb-exec.md.

**Reuse:** the BDB heightmap is already loaded (`BdbData.PerBlockAux`, `AuxAt`, stride 89) — that is
the `FUN_00595e40`/`00424470` source for §3/§6. The L2M `+0x3c` sub-grid is the settle-loop floor-bit
source (§4.5). The densifier/flood/backtrace are pure algorithm and can be implemented directly from
§2-§5 against those existing data layers.

---

## 11. UNRESOLVED / gaps

1. **`FUN_0041d8a0` mangled tail (`:7-69`, ~70 unreachable blocks).** The reachable C + the verified
   disasm cover the flood, the cost table, the 20 neighbour offsets, the backtrace neighbour set, and
   the settle loop. NOT re-verified byte-for-byte: the exact bookkeeping of `local_4c` (goal-hop
   counter) and the precise loop-exit predicate interplay at `:647` (the C `(iVar9 <= local_4c) &&
   (readHead < writeHead)` reads sane but the mangled blocks may hide an extra early-out). INFERENCE:
   it is a standard "stop when goal cost settled or frontier drained".
2. **`param_5`/`param_4`/trailing `0x10` arg shuffle.** RESOLVED by disasm (call `0x41ae14`, callee
   prologue `0x41d8ce-0x41d8f5`): `this`(ECX) = the FloorPathMap `[esi+0x14]`; the 5 stack args are
   `[ebp+8]=param_2=&start`, `[ebp+0xc]=param_3/local_88=&goal`, `[ebp+0x10]=param_4=climb/canclimb
   flag (ebx)`, `[ebp+0x14]=param_5=local_18 (start-segment flag, 0 on the first cell)`, and
   `[ebp+0x18]=0x10` (the trailing literal — NOT read in the reachable body; confirmed unused).
   Ghidra mis-rendered `this` as `param_1=&local_40`. `param_5`=mount selector, `cVar6 =
   (param_5==0)?param_4:0` — both confirmed.
3. **`FUN_0041c4b0` height "step > 2" classification (`:94,119`).** The `2 < (here-above)` cliff
   threshold and the `δ∈[0,3)` lip search are read straight from the C; the exact world tile struct
   fields `DAT_0077241c[0x84..0xa0]` (bbox/strides) are named by offset only, not fully typed.
4. **Re-seed-for-descent retry (`:648-666`).** Confirmed it re-floods from `local_58` at the goal
   depth when the first flood missed the goal; the `local_75` one-shot guard prevents an infinite
   retry. Not stress-tested against a multi-jog shaft.
5. **2-pt vs 3-pt straight emit (`:259-360`).** Both branches read clearly; which one fires depends on
   `cVar6` and whether `local_48` (start point) coincides with `local_88` (goal). The exact float
   equality at `:259-261` is brittle in a port — use an epsilon.
6. **`FUN_0041f320` (CPath slice).** Only its head (`:1-60`) was read in full; it creates a `CPath`
   and sub-samples with stride 0x20. Its interior densification details are deferred to the densifier
   contract in §2 (`FUN_0041bff0`), which is what actually populates the point list.

---

## Verification notes (adversarial re-check,)

A second pass re-disassembled `Diggles.exe` (`.text` raw 0x400, imgbase 0x400000) with Capstone and
re-read every cited C/Tcl line, attempting to REFUTE each load-bearing claim. Results:

**CONFIRMED by independent disasm/symbolic trace (no change):**
- **Grid layout** `col*0x40+row`, `row=round(z·4)`, cell `[31:24]`=mask `[23:0]`=cost. Seed flat-idx
  `0x41ddd0: shl ecx,6; add ecx,[..]` = col·64+row. ✓
- **5-pass cost table** `{1280,512,256,1280,1024}={5,2,1,5,4}×256`. Disasm `0x41dcfb-0x41dde4`:
  stores to `ebp-0x24/-0x20/-0x1c/-0x18/-0x14`; pass-3 (`ebp-0x18`) reuses the 5.0·256 saved at
  `ebp-0x44`; indexed `0x41df4a sub eax,[ebp+ecx*4-0x24]`. ✓ EXACT.
- **The 20 neighbour offsets per pass.** Re-derived by symbolic execution of the machine-code state
  updates (`0x41de7a` base + `0x41df15`/`0x41df33` pass prologues + `0x41e28e` post-body). Output
  reproduced the table in §4.3 byte-for-byte: pass0=(-1,-2)(2,-1)(1,2)(-2,1); pass1=4 diagonals;
  pass2=4 cardinals; pass3=(-1,2)(-2,-1)(1,-2)(2,1); pass4=2-step cardinals. ✓ EXACT.
- **Seed/goal labelling.** `local_48`=nearest to `param_2` (gnome current pos = START),
  `local_58`=nearest to `local_88` (mount-depth target = GOAL); flood seeds from the GOAL-nearest cell
  `(local_90=local_58, local_8c=round(goalZ·4))`, backtrace from the START-nearest cell
  `(local_84=local_48, local_80=round(startZ·4))`. ✓
- **Mount FUN_0041bbd0** ±16×±16, free=`(&0xff000000)==0 && (&0xffffff)==0`, mark `0x80000000`+false.
- **Cost-grid bands FUN_0041c4b0**: `0x10000000` too-low (`r < floorH·4+1`), `0x08000000` too-high
  (`r ≥ ceilH·4−1`), spill→`0x0f0f0f0f`+`[0xb]=1`, step>2 lip search, gate byte `==−1` skip, stride
  0x7c. ✓
- **Goal-depth** `dx<0 → h−1.0`, `dx≥0 → h−0.5` (disasm `0x41ac5f jns` → `0x41ac81 subss …,[0x737138]`
  on the dx<0 fall-through). `h` from `FUN_00424470` with `+2` row bias on BOTH block-select and
  sub-cell (`:19,29`; block stride `0x59`=89). `dx`=`pt[idx].x−pt[idx-1].x` (`00423be0:35`). ✓
- **Densifier** point z=`0x41700000`=15.0, one pt per 1.0 unit, x/y·0.125, grid `this[1]=n<<6`,
  `memset n<<8`, slices via `FUN_0041f320` stride 0x20 when `param_5==0`. ✓
- **Arg mapping / `param_5` selector / `cVar6`** — see §11 item 2 (now RESOLVED via disasm). ✓
- **Driver FUN_0041aef0** rebuilds the sub-action per tick, advances `owner+0x40` in `FUN_0041aac0`
  (`:207`), state 3 on no-CPath (`:211`), state 4 on segIdx exhausted (`:41`). CPath stored at
  `owner+0x1c`; the per-segment CSubAction (return value) stored at `owner+0x18`. ✓ (The "consumed by
  `CAct_Climb`" naming is NOT verifiable from these files — `FUN_0041aac0:206` builds the sub-action
  via `FUN_00419130` from the CPath, but the concrete class is out of scope here; treat the
  `CAct_Climb` label as UNVERIFIED in this doc.)

**CORRECTED:**
- **§V.1 — Dismount `FUN_0041bde0` column window.** The earlier claim "col ∈ [col−1, col+1)" /
  "±1 col" is WRONG. The upper col bound is `*param_2` (= col, clamped to ≤ n) with NO `+1` (disasm
  `0x41be92 cmp edx,[ebx+0xc]; cmovge edx,[ebx+0xc]`), the lower is `col−1` (`0x41be88 lea
  edi,[edx-1]`), and the inner loop `iVar9=col−1; while(iVar9<col)` runs for `col−1` only. So the
  dismount scans a ±256-row band in the **single column `col−1`** (`[col−1, col)`). Fixed in §5.2,
  §8 constants table.
- **§V.2 — y+=0.125 settle loop applies to the FLOOD path, not the LOS path.** The earlier claim that
  the settle runs "ONLY on the trivial/straight path" is WRONG. The settle (`:960-1027`) lives inside
  the `LAB_0041e3d2` materialiser (`:848-1028`), which is reached ONLY after the 5-pass flood
  (`LAB_0041dcfb`→`:659 goto LAB_0041e385`→`LAB_0041e3d2`). The straight-LOS short-circuit
  materialises its 2/3-point path at the SEPARATE `LAB_0041f026` (`:248-360`) and `break`s straight to
  the function exit (`:299,358,363` → `:669`), never touching the settle. So the settle operates on
  the Dijkstra/backtrace waypoints. Fixed in §0, §4.5, §7-step4, §10-gap7.
- **§V.3 — LOS lerp uses TRUNCATION, not round.** §4.2 said "round(lerpCol)·0x40 + round(lerpRow)";
  the lerp components are int-cast via `cvttss2si` (`0x41dc96`, `0x41dca5`) = TRUNCATED. (Contrast the
  explicit `ROUND` used for the `z·4` row conversions.) The `·0x40` on the col term and
  `floor(dist)+1` step count are correct. Fixed in §4.2.
- **§V.4 — Mount `FUN_0041bbd0` lower col clamp is 0, not 1.** §5.1 said col clamped "[1,n)"; the C is
  `if((int)uVar2<1) uVar2=0` (`:43-45`) ⇒ lower bound 0. Fixed in §5.1.

**Net:** the algorithmic core (grid layout, cost table, 20-offset neighbourhood, seed/goal direction,
goal-depth rule, densifier, mount search, cost-band stamping, arg mapping) is SOUND and now
disasm-verified. The four corrections above are localized (dismount col span, settle-loop attribution,
LOS truncation, mount col clamp) and do not alter the overall pipeline.
