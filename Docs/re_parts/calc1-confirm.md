> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# Calc1 — confirmation & extension RE (`calc1-confirm`)

> Line-by-line RE of `CMapPathfinder::Calc1` and all of its helpers, cross-checked against the fresh
> port `Assets/Diggles/Runtime/Navigation/Calc1Pathfinder.cs`. Every constant below was extracted
> **directly from `original/Diggles.exe` `.rdata`** (PE imgbase 0x400000) — values are ground truth,
> not inferred. Every behavioural claim cites `FUN_addr:line` in `re/decompiled/Diggles/_global/`.
>
> **Headline corrections to `Docs/ORIGINAL_PATHFINDING_RE.md` §2 found this pass:**
> 1. The 4-variant **dispatch is INVERTED** in the old doc. The owner-gate variants (B/D) are chosen
> for `iOwner ≤ 7`, NOT `iOwner > 7`. The no-owner variants (A/C) are chosen for `iOwner > 7`.
> (`0059d640:417-433`, confirmed by the `param_6 < 8` guard inside B/D.)
> 2. The **goal relaxation is NOT a symmetric square ring** — it is an 8-probe directional fan heavily
> biased toward +y (down), with up-probes scaled to r/4 and r/5 (`0059d640:154-235`). The port uses a
> symmetric ring → DEVIATION.
> 3. The **start relaxation probes ONLY the 4 axis points** (+y,+x,−x,−y) per ring, not the full ring
> perimeter (`0059d640:116-148`). The port walks the whole perimeter → DEVIATION (harmless-ish, but
> picks a different tie-break cell).
> 4. The **post-snap "+y while +0x3c&2" nudge is now fully decoded** (`0059d640:241-307`): independently
> for start and goal, `while (subcell(x, y+1) & 0x02) y += 1`. The port does NOT implement it → DEVIATION.
> 5. The **budget clamp counter is the FRONTIER-WRITE index `param_1[4]` (nodes enqueued), not pops**, and
> the clamp is `round(enqueued × 1.5)` taken at the moment the goal is *written* — applied as the new
> `maxNodes` ceiling (`0059ab90:152-156` etc.). The port clamps on `pops` and adds a stray `+8`.
> 6. **bLines densify step = 2.0** and the densify distance is **purely 2-D** (the Z term
> `DAT_00736f78 = 0.0`) — `0059d640:925-964`. MetaWalk passes bLines=0, so this never runs for gnomes,
> but the port's claim "step 2.0 (units?)" is now resolved: sub-cell units, no Z.

---

## 1. Overview & call graph

```
CalcPath (FUN_004158a0) // CAct_MetaWalk
  └─ owner[+0x7b4]==0 → CMapPathfinder::Calc1 (float wrapper, FUN_0059d440)
        └─ scales x,y ×8, DROPS z, ROUND → int entry CMapPathfinder::Calc1 (FUN_0059d640)
              ├─ FUN_0059d600     start-already-walkable test
              ├─ FUN_0059a320     (lazy) alloc/clear dist field DAT_00b9bf18 (tile-major int[])   [seen earlier in init]
              ├─ FUN_0059a560     (re)alloc frontier queue, cap 0x400, grow +0x400
              ├─ one of 4 search variants (dispatch on iOwner, bCanClimb):
              │     A FUN_0059ab90  iOwner>7 , !climb : 0x40 floor gate, NO owner gate, cost v1
              │     B FUN_0059bf90  iOwner≤7 , !climb : 0x40 floor gate (LEFT uses 0xA4 bug), owner gate, cost v2
              │     C FUN_0059b670  iOwner>7 ,  climb : NO floor gate, NO owner gate, cost v1
              │     D FUN_0059ca30  iOwner≤7 ,  climb : NO floor gate, owner gate, cost v2
              │        cost v1 = FUN_0059a740 (CLIFF 0x114)   cost v2 = FUN_0059a8b0 (CLIFF 0x10a)
              │        improvement predicate wrappers: FUN_0059aad0 (v1) / FUN_0059aa20 (v2)
              ├─ greedy backtrace over dist field → CPath waypoints (exit sub-cell per tile)
              └─ if bLines: FUN_0059a5b0 snap each vertex to a floor sub-cell, densify wall spans @2.0,
                            drop collinear interior points
```

The dist field and frontier are **module-global singletons** (`DAT_00b9bf18` dist, `DAT_00b9bf2c/30/20/28`
the active sub-rect origin/stride/modulus). One pathfind at a time; the field is cleared lazily at the end
by re-stamping every visited frontier cell back to `0x7FFFFFFF` (`0059d640:766-796`, and the abort path
`0059e8cf:713-744`).

---

## 2. The float wrapper `FUN_0059d440` — which component is dropped

Signature `CPathPtr Calc1(CVec3D v1, CVec3D v2, int iOwner, bool bLines, bool bCanClimb)`.
Ghidra params: `param_2,3,4 = v1.{x,y,z}`, `param_5,6,7 = v2.{x,y,z}`, `param_8=iOwner`, `param_9=bLines`,
`param_10=bCanClimb`, `param_11=maxNodes`.

`0059d440:56-59`:
```
DAT_00772dd8 = ROUND(param_5 * 8.0); // = goal.x in sub-cells
Calc1_int(param_1,
          ROUND(param_2 * 8.0),   // start.x sub  (v1.x)
          ROUND(param_3 * 8.0),   // start.y sub  (v1.y)
          DAT_00772dd8,           // goal.x  sub  (v2.x)
          ROUND(param_6 * 8.0),   // goal.y  sub  (v2.y)
          param_8, param_9, param_10, param_11);                  // iOwner,bLines,bCanClimb,maxNodes
```
`DAT_0073752c = 8.0`. **The DROPPED component is the Z of both endpoints**
(`param_4 = v1.z`, `param_7 = v2.z`). In the proven world frame Z is the **BDB slab-depth-into-screen /
native-z axis**, so Calc1 is a strictly 2-D search in the (native-x, native-y) screen plane. ✅ The port's
comment ("drops Z") and 2-D assumption are correct.

---

## 3. Tile addressing `FUN_00592f10(thisCMap, x, y)` (`00592f10:7-33`)
```
cx = clamp(x, CMap[+0x84], CMap[+0x8c]-1) // tile bounds minX..maxX
cy = clamp(y, CMap[+0x88], CMap[+0x90]-1) // minY..maxY
idx = (cy * CMap[+0x44] + cx) % CMap[+0x4c] // stride=+0x44 (512), modulus=+0x4c (327680)
if 0<=idx<CMap[+0x18(count)]: return CMap[+0x14(tileBase)] + idx*0x7c
else raise, return tileBase
```
The search variants do this clamp inline against `+0x84..+0x90`; the relax loops call `FUN_00592f10`
with `x>>3, y>>3` (sub→tile). Tile stride `0x7c=124`.

---

## 4. Int entry `FUN_0059d640` — endpoint relaxation (THE part to fix)

### 4.1 Start relaxation (`0059d640:113-148`) — 4-axis ring, r=1..32
Runs only if `FUN_0059d600(start)==0` (start not already a valid+walkable sub-cell, `0059d600:7-16`).
For `r = 1..0x20 (32)`, in this exact order, first hit wins (and writes back into start):
```
if walkable(x, y+r): y += r; break // +y (DOWN) first
if walkable(x+r, y): x += r; break // +x
if walkable(x-r, y): x -= r; break // -x
if walkable(x, y-r): y -= r; break // -y last
```
`walkable(sx,sy):= FUN_00592f10(sx>>3,sy>>3)[+6] != -1 && (tile[+0x3c + ((sy&7)<<3|(sx&7))] & 1) != 0`.
**Only the 4 axis cells per ring are probed — NOT the diagonal perimeter.** No success after r=32 ⇒ start
stays put (NOT a null; the goal-relax below or the final validity check decides null).

### 4.2 Goal relaxation (`0059d640:152-239`) — 8-probe directional fan, r=1..12
Runs only if the goal sub-cell isn't already valid+walkable (`:152-153`). For `r = 1..0xC (12)`, probe in
this order; first hit writes back and breaks (`/` = C integer division, truncated toward 0):
```
1 (x, y+r) // straight DOWN, full r
2 (x, y-(r/4)) // UP, quarter r local_3c = (r+(r>>31&3))>>2
3 (x+(r/2), y) // RIGHT, half r local_28 = r>>1
4 (x-(r/2), y) // LEFT, half r
5 (x+(r/3), y+(r/2)) // down-right
6 (x-(r/3), y+(r/2)) // down-left
7 (x+(r/3), y-(r/5)) // up-right, fifth r local_30 = r/5
8 (x-(r/3), y-(r/5)) // up-left, fifth r
```
Same `walkable` predicate. After the loop, a final validity re-check (`:236-239`); if the goal is still not
walkable ⇒ `*param_2 = 0` (NULL PATH, `LAB_0059fba5`). The fan is **strongly +y-biased** (down probes use
r and r/2; up probes only r/4 and r/5) — it prefers snapping the click DOWN onto a floor line, matching the
side-view "fall onto the shelf below the cursor" intuition.

### 4.3 Post-snap "+y while floor" overhang nudge (`0059d640:241-307`)
Two independent `while(true)` loops, START then GOAL. Each reads the sub-cell **one row DOWN** and tests the
FLOOR bit (0x02); while set, step the point down:
```
// START:
while (tile(x, y+1)[+0x3c + (((y+1)&7)<<3 | x&7)] & 0x02) y += 1; // param_4
// GOAL:
while (tile(gx, gy+1)[+0x3c + (((gy+1)&7)<<3 | gx&7)] & 0x02) gy += 1; // param_6
```
The tile fetch is the full inline clamp+mod against `CMap[+0x21..+0x24]` (the *sub*-cell bounds, note: it
uses `>>3` on x,y then `+0x21=minSubX/8`… i.e. tile coords) — equivalent to `FUN_00592f10(x, y+1)`.
**Effect:** slide the endpoint down through a vertical stack of floor sub-cells until the cell below is no
longer floor — i.e. land on the *bottom* floor sub-cell of an overhang/ledge. This is `+y` = DOWN (deeper),
consistent with gnomes standing on the lowest face of a shelf. The port omits this entirely.

> ⚠️ Subtlety: this nudge keys on bit **0x02 (floor)**, whereas the relax keys on bit **0x01 (walkable)**.
> A cell can be walkable-non-floor (a wall, value 1) without being floor.

### 4.4 Trivial / degenerate cases
- OOB / margin reject (`0059d640:102-105`): if `CMap[+0x29]==0` (no map) OR start.x/.y outside
  `CMap[+0x25..+0x28] ± 0x28(40)` OR `FUN_0040b8a0(&goal, 0x28)` (goal-near-edge test) → NULL.
- `start == goal` after relax (`:311`): emit a 1-point CPath {start} and return.

---

## 5. The search core (variants A–D) — exact gate & relax-step

All four share the identical skeleton (FIFO Bellman-Ford over a tile dist field):

```
queue = [start_tile]; dist[start] = selfcost(start); enqueued(param_1[4]) = 1
loop:
  cur = queue[head(param_1[3])]; head++
  if head >= cap OR enqueued >= cap: return found // (B/D check this at:75; A/C at:418-419)
  if cur not within active sub-rect margin 2: skip neighbours // DAT_00b9bf2c+2.. +stride-2 etc.
  for nb in [ (cx, cy+1), (cx, cy-1), (cx+1, cy), (cx-1, cy) ]: // +y, -y, +x, -x (THIS ORDER)
      T = tile(nb)
      if T[+6] == -1: continue                                  // GATE 1 validity
      if dist[cur] + cost(cur,nb) >= dist[nb]: continue         // GATE 2 improvement (strict <)
      if !climb && (T[+0x12] & MASK) == 0: continue             // GATE 3 floor gate (A/B only)
      if ownerGate && T[0x34 + iOwner] == 0: continue           // GATE 4 owner gate (B/D only)
      dist[nb] = dist[cur] + cost(cur,nb)
      frontier[enqueued++] = nb
      if nb == goal && !found:
          found = true
          maxNodes = min(maxNodes, round(enqueued * 1.5))       // BUDGET CLAMP, DAT_007371c0=1.5
  if enqueued == head OR maxNodes <= head: return found
  if cap < enqueued+4: grow cap += 0x400
```

### 5.1 The 4-variant dispatch (`0059d640:417-433`) — **CORRECTED**
`param_7 = iOwner`, `(char)param_9 = bCanClimb`. `&DAT_00000007 < param_7` means `iOwner > 7`.
```
if (bCanClimb == 0):
    if (iOwner > 7):  A = FUN_0059ab90(...)              // floor gate, NO owner gate, cost v1
    else:             B = FUN_0059bf90(..., iOwner, ...)  // floor gate + owner gate, cost v2
else: // bCanClimb != 0
    if (iOwner > 7):  C = FUN_0059b670(...)               // NO floor gate, NO owner gate, cost v1
    else:             D = FUN_0059ca30(..., iOwner, ...)   // NO floor gate, owner gate, cost v2
```
So **owner-gated variants (B,D) require `iOwner ≤ 7`** and read `tile[0x34 + iOwner]` after the inline
`if (iOwner < 8) idx = tile + iOwner; else raise;` guard (`0059bf90:102-108`, `0059ca30:100-106`).
**iOwner > 7 selects the NO-owner-gate variants (A,C)** and uses cost **v1** (CLIFF 0x114). iOwner ≤ 7
uses cost **v2** (CLIFF 0x10a). *(The old doc had both the owner-gate direction and the cost-by-owner
direction backwards.)*

### 5.2 Per-variant gate masks
| Variant | Addr | climb? | floor-gate mask per neighbour (+y / −y / +x / −x) | owner gate | cost |
|---|---|---|---|---|---|
| A | 0059ab90 | no | 0x40 / 0x40 / 0x40 / 0x40 (`:119,204,289,375`) | no | v1 (0059a740) |
| B | 0059bf90 | no | 0x40 / 0x40 / 0x40 / **0xA4** (`:101,178,252,328`) | yes `[0x34+iOwner]` | v2 (0059a8b0) |
| C | 0059b670 | yes | — none — | no | v1 |
| D | 0059ca30 | yes | — none — | yes `[0x34+iOwner]` | v2 |

**Variant-B left-neighbour `0xA4` is REAL** (`0059bf90:328`). 0xA4 = 0x80|0x20|0x04. Where the other three
directions test only the floor bit 0x40, the −x direction tests "0x80|0x20|0x04". Bit semantics of 0x80/0x20
at +0x12 are unknown (see Unresolved). This is a copy-paste defect; replicate verbatim for fidelity. It only
affects campaign workers (iOwner ≤ 7, non-climbers) moving leftward.

### 5.3 Self-cost of the start (`0059ab90:44` etc.)
`dist[start] = cost(start, start)`. For start==start, cliff(cur)==cliff(T) and dig equal, so all three cost
branches collapse to `base + 1`. So the start seed
distance = `tile[+6] + 1` (= 21 for a floor tile, 51 for walkable-non-floor). The port seeds `dist=0` at the
start — DEVIATION (uniform offset, harmless for shortest-path ordering but changes the budget-clamp timing &
any absolute-distance comparison; see §8).

### 5.4 Improvement predicate wrappers `FUN_0059aad0` (v1) / `FUN_0059aa20` (v2)
`0059aad0:14-31`: `return cost_v1(to, from) <= dist[from] - dist[to]` i.e.
`dist[from] + cost_v1(to,from) <= dist[to]`. Note the cost is computed **(to→from)** — i.e. `base` is taken
from the *current* node `from`, not the candidate `to`. **But the inline code in variants A/C calls
`FUN_0059a740(iVar5,iVar1, iVar5,iVar1±1)` = (from, to)** for the actual relaxation, and the wrapper only for
the `<` test inside the diagonal-skip backtrace. The two argument orders differ; in the search loop the cost
is always `cost(from_x,from_y, to_x,to_y)` and `base` = `to[+6]` (`0059a740:44` reads `piVar4 = to`'s +6).
The relaxation predicate in the loop is the literal `*piVar4 + iVar9 < *local_1c` where piVar4=dist[from],
local_1c=dist[to] (`0059ab90:119`). So: **improve iff `dist[from] + cost(from,to) < dist[to]` (STRICT <).**
Port uses `nd >= dist → skip`, i.e. strict `<` to improve — ✅ matches.

---

## 6. Cost functions `FUN_0059a740` (v1) / `FUN_0059a8b0` (v2) — EXACT

Both take `(fromX,fromY, toX,toY)`. `cliff(t) = tile(t)[+0x12] & 0x40`. `base = tile(to)[+6]`.
`dig(t) = BDB.aux[ block_idx(tile(t)) * 0x59 + 0x24 ]` (the block-CENTRE height nibble; raises if block id
OOB, `0059a740:23-29`).

```
if (cliff(from) XOR cliff(to)): // exactly one is a floor tile (a cliff edge)
    return base + CLIFF - 0x10 * min(dig(from), dig(to))      // 0059a740:44 / 0059a8b0:44
elif (!cliff(from) && !cliff(to)): // both non-floor (wall↔wall)
    return base + 1 + 0x10 * abs(dig(from) - dig(to))         // 0059a740:66-68
else: // both floor
    return base + 1                                          // 0059a740:70
```
- v1 (FUN_0059a740): `CLIFF = 0x114 = 276`. v2 (FUN_0059a8b0): `CLIFF = 0x10a = 266`.
- step `0x10 = 16`. `abs` is the classic `(v^sign)-sign` (`0059a740:67-68`).
- **The cost can be ≥1 always** (base ≥ 20 for valid tiles, and the cliff branch `276 - 16*min(dig)`; dig
  nibble ≤ 15 → `16*15=240`, so cliff cost ≥ `base + 36` > 0). The port's `if (cost < 1) cost = 1;` clamp
  is therefore dead code in practice but harmless.

✅ The port's three-branch cost formula and constants (Cliff=0x114, StepCost=0x10) are **correct for v1**.
❌ The port has NO v2 path (CLIFF 0x10a / iOwner≤7) — see §8.

---

## 7. Output polyline + bLines smoothing

### 7.1 Raw polyline (no bLines) — `0059d640:565-585` (and the climb mirror at:687-707)
Backtrace from goal over the dist field, greedily stepping to the cheaper 4-neighbour
(`FUN_0059aad0`/`aa20` choose which predecessor). For each crossed tile, the waypoint stored is the tile's
**exit sub-cell** packed in `+0x12`:
```
wp.x = (tile[+0x12] & 7) + tileX*8 // 0059d640:579 (low 3 bits = subX 0..7)
wp.y = ((tile[+0x12] >> 3) & 7) + tileY*8 // 0059d640:583 (bits 3..5 = subY 0..7)
```
Then the FIRST waypoint is forced to the exact relaxed `start` (`:451-452`) and the LAST to the exact relaxed
`goal` (`:760-762`). Order is **start→goal**. ✅ The port reproduces this (uses `ExitSub`, replaces last with
goal, prepends start) — but note the port masks ExitSub low-6 as `(e&7, e>>3)` which is correct since
ExitSub already only holds 6 bits.

> Important detail the port currently gets *structurally* right but for a subtly different reason: the
> original packs `exit` as `(subX in bits0-2) | (subY in bits3-5)` — a **0..7 sub within the tile**, so the
> world sub-cell is `tileX*8 + subX`. The port's `ExitSub` from `DeriveTile` is the raw 0..63 best-sub index
> `(row<<3)|col`, and it unpacks `(e&7=col, e>>3=row)` → same thing. ✅ consistent.

### 7.2 bLines smoothing (MetaWalk passes bLines=0 — never runs for gnomes) `0059d640:837-1413`
Only when `bLines != 0`. Two stages:

**(a) per-vertex floor snap + wall-span densify (`:837-988`).** For each interior vertex pair (v[i],v[i+1]):
- snap each via `FUN_0059a5b0` (§7.3) to a nearby floor sub-cell;
- if both snapped cells have `(sub & 10) != 0` (bit 0x08 climb | 0x02 floor) (`:893,921`):
  - compute `dx=Δx, dy=Δy`; pick the **dominant axis** via `DAT_007371c0 (=1.5)`:
    `if 1.5*|dy| <= |dx|` (x dominates) → densify: walk from the vertex along the unit (dx,dy)/len in
    steps of `DAT_00737330 (=2.0)` sub-cells, snapping each step with FUN_0059a5b0 and appending
    (`:936-964`). `len = sqrt(dx²+dy² + DAT_00736f78)` and **DAT_00736f78 = 0.0** → the length is purely 2-D.
  - else (y dominates) → append a single midpoint `((x0+x1)/2, (y0+y1)/2)` (`:967-983`).

**(b) collinear interior-point removal (`:1198-1413`).** For each interior triple, compute the 2-D cross
product `cross = (Bx-Ax)*(Cy-Ay) - (By-Ay)*(Cx-Ax)` (integer) and the parametric projection; if the point is
collinear (cross test with the `DAT_00737970 = 0x80000000` sign-flip allowing ± direction, `:1275-1277`),
drop it (`:1289-1411` rewrites the array in place, decrementing count). Endpoints are always kept.

### 7.3 `FUN_0059a5b0` — single-vertex floor snap (`0059a5b0:7-77`)
Given a sub-cell `*param_2 = (x,y)`:
- margin guard: if within 8 sub-cells of the sub-cell bounds `CMap[+0x94..+0xa0]` → copy through unchanged.
- read `b = tile(x>>3,y>>3)[+0x3c + ((y&7)<<3|x&7)]`.
- **if `(b & 0x0F) == 0` (empty sub-cell):** if also `(b & 0x0E)==0`, scan DOWN then UP for a sub-cell whose
  bit-AND with a running flag is nonzero — effectively search ±a few rows for the nearest occupied cell
  (`:29-50`, the `iVar2=3` then `iVar2=5` two-pass with `bVar1` toggling up/down). Writes the found (x,y).
- **else (occupied):** if not already floor (`b & 2 == 0`), step DOWN (`y+1`) up to 3 times looking for a
  cell with the FLOOR bit, stopping early if a climb bit (0x08) is hit (`:54-72`). Writes the floor cell or
  the original if none.

This is a "pull the smoothed point onto the nearest floor surface" helper, used **only by bLines**.

---

## 8. PORT GAP ANALYSIS — `Calc1Pathfinder.cs` vs the decompile

| # | Area | Original | Port today | Verdict |
|---|---|---|---|---|
| G1 | **Variant dispatch / owner gate** | 4 variants on (iOwner≤7?, climb?); owner-gate variants need iOwner≤7 and gate `tile[0x34+iOwner]` | single code path; **no owner gate at all**, no iOwner input | **MISSING.** Campaign gnomes pass iOwner∈[0..7] → should use cost **v2** + owner gate. Port silently uses v1 and no owner gate. |
| G2 | **Cost variant (v1 vs v2)** | iOwner≤7 → CLIFF **0x10a=266**; iOwner>7 → 0x114=276 | hardcodes **0x114** only | **WRONG for campaign.** Campaign workers (iOwner≤7) should use 266, port uses 276. Off by 10 on every cliff edge — changes which path wins on ties. |
| G3 | **Floor-gate direction** | non-climbers gate on `+0x12 & 0x40`; climbers drop it | gates `!canClimb && !tFloor` ✅ | **CORRECT.** |
| G4 | **Variant-B 0xA4 left-mask bug** | −x neighbour tests 0xA4 not 0x40 | uniform 0x40 | **MISSING** (fidelity-only; affects leftward moves of non-climber campaign workers). |
| G5 | **Goal relaxation shape** | 8-probe directional fan, +y-biased, r≤12 (§4.2) | symmetric square ring r≤12 | **DEVIATION.** Different snap cell when the click is off-mesh; the original prefers DOWN. |
| G6 | **Start relaxation shape** | 4 axis points/ring, r≤32, +y first (§4.1) | full ring perimeter, r≤32, +y first | **DEVIATION** (tie-break only; perimeter can pick a diagonal the original never would). |
| G7 | **Post-snap +y overhang nudge** | both endpoints slid down while `subcell(x,y+1)&0x02` (§4.3) | not implemented | **MISSING.** Endpoints can sit on an upper floor row instead of the bottom of the ledge. |
| G8 | **Budget-clamp counter & formula** | clamp `min(maxNodes, round(enqueued × 1.5))`, taken when goal is *written*, counter = frontier-write index | `min(budget, (int)(pops*1.5f) + 8)` on **pops** with a stray **+8** | **DEVIATION.** Wrong counter (pops≈head, not enqueued) and the `+8` is fabricated — remove it; use the enqueued count. |
| G9 | **Start seed distance** | `dist[start] = base+1` (21/51) | `dist[start]=0` | **DEVIATION** (uniform offset; harmless to ordering, but skews the absolute distances if any caller reads totalCost, and shifts when the 1.5 clamp first fires). Match it for faithfulness. |
| G10 | **maxNodes default** | passed in by CalcPath; queue grows 0x400 at a time, no inherent 200000 cap | `DefaultBudget = 200000` | **APPROX.** The real default comes from `-stopat`/caller; 200000 is a safe over-estimate (the search self-limits via the 1.5 clamp once the goal is hit). Acceptable, document it. |
| G11 | **Neighbour order** | +y, −y, +x, −x (§5) | +x, −x, +y, −y | **DEVIATION** (FIFO order changes which equal-cost path is returned; cosmetic for cost, visible for exact route). Reorder to match. |
| G12 | **Search style** | FIFO Bellman-Ford, re-enqueue on improve, frontier array | Queue<int> FIFO Bellman-Ford ✅ | **CORRECT** in spirit. |
| G13 | **Self-loop / start==goal** | emits 1-point path | port returns `[start, goal]` (2 pts) when chain len 1 | **MINOR.** Behaviourally fine for MetaWalk. |
| G14 | **bLines** | full smoother (step 2.0, 2-D, collinear drop) | not implemented; comment says MetaWalk uses bLines=0 ✅ | **CORRECT to omit** for gnomes. Note for other callers it's missing. |
| G15 | **Endpoint OOB margin** | reject within 0x28=40 of map edge (`:102-105`) + `FUN_0040b8a0(goal,0x28)` | only the `BaseCost==-1` check | **MISSING** edge-margin reject (rarely matters; the relax usually fails first). |

**Net:** the port's *core search + cost-v1 + floor-gate* are faithful, but it is effectively the
**iOwner>7, non-climber** branch only. For the actual campaign (iOwner 0..7) it should switch to **cost v2
(CLIFF 266) + the owner gate**, and the endpoint relaxation differs in shape (G5/G6/G7). The budget clamp
(G8) and start-seed (G9) are small numeric deviations.

---

## 9. Constants table (ALL re-extracted from `original/Diggles.exe` `.rdata`, imgbase 0x400000)

| Symbol | Addr | Bytes | Value | Role |
|---|---|---|---|---|
| DAT_0073752c | 0x73752c | 41000000 | **8.0** | world→sub-cell scale (wrapper) |
| DAT_0073703c | 0x73703c | 3e000000 | **0.125** | sub-cell→world |
| DAT_007371c0 | 0x7371c0 | 3fc00000 | **1.5** | budget clamp ×, AND bLines axis-dominance K |
| DAT_00737330 | 0x737330 | 40000000 | **2.0** | bLines densify step (sub-cell units) |
| DAT_00736f78 | 0x736f78 | 00000000 | **0.0** | Z term in bLines length² → length is 2-D only |
| DAT_00737970 | 0x737970 | 80000000 | **−0.0** | sign-flip mask for collinear ± test |
| DAT_00736f94 | 0x736f94 | 38000000 | **2^-15 (3.05e-5)** | CalcPath target-Z frame fraction (not in search) |
| DAT_0073708c | 0x73708c | 3f000000 | **0.5** | Z=nibble·0.5+7; Z weight in MetaWalk dist metrics |
| DAT_00737520 | 0x737520 | 40e00000 | **7.0** | Z base |
| DAT_0073702c | 0x73702c | 3db851ec | **0.09 (0.3²)** | arrival threshold² (MetaWalk/Climb) |
| DAT_007370d0 | 0x7370d0 | 3f490fdb | **π/4 (0.7853982)** | turn-clip threshold |
| DAT_007374a0 | 0x7374a0 | 40800000 | **4.0** | world→climb-grid Z |
| DAT_00737564 | 0x737564 | 41680000 | **14.5** | climb-end Z up / Z clamp max |
| DAT_00737548 | 0x737548 | 41480000 | **12.5** | climb-end Z down |
| CLIFF v1 | — | — | **0x114 = 276** | cost edge, FUN_0059a740 (iOwner>7) |
| CLIFF v2 | — | — | **0x10a = 266** | cost edge, FUN_0059a8b0 (iOwner≤7) |
| step | — | — | **0x10 = 16** | dig-nibble step in cost |
| tile stride | — | — | **0x7c = 124** | tile array record |
| BDB aux stride | — | — | **0x59 = 89** | per-block height/geometry |
| dig nibble offset | — | — | **+0x24** | block-centre height read by cost |
| queue cap / grow | — | — | **0x400 = 1024 / +1024** | frontier (FUN_0059a560) |
| start relax rings | — | — | **32 (r=1..0x20)** | 4-axis |
| goal relax rings | — | — | **12 (r=1..0xC)** | 8-probe fan |
| edge margin | — | — | **0x28 = 40** | endpoint OOB reject |

---

## 10. Tcl ↔ native contract (unchanged from §7 of the master doc)
`action <o> walk` → builds CWalkInfo (default `-canclimb 1`); CAct_MetaWalk::CalcPath (`004158a0:33`)
branches `owner[+0x7b4]==0 → Calc1` (campaign) / else Calc2 (`005a22f0`, out of scope). Calc1 args from
CalcPath: `v1 = movable[+0x0c/10/14]`, `v2 = owner[+0x7a4/7a8/7ac]`, `iOwner = movable[+0x36c] +
(owner[+0x7d8]==0 ? 100: 0)`, `bLines = 0`, `bCanClimb = owner[+0x7b0]`. **NOTE the +100**: when
`owner[+0x7d8]==0`, iOwner gets +100 → pushes it into the `>7` band → selects the **no-owner-gate** variant
(A/C) + cost **v1**. When `owner[+0x7d8]!=0`, iOwner stays small (≤7) → owner-gate variant (B/D) + cost v2.
So `+0x7d8` is effectively the "respect per-player territory gates" switch, and it also picks the cliff
constant. *(This finally explains why two cost variants exist: same world, two policies.)*

---

## 11. Interaction with neighbour systems
- **NavWorld / FUN_005a6350** supplies `tile[+6]` (BaseCost −1/20/50) and `tile[+0x12]` (0x40 floor flag +
  exit sub-cell). Calc1 only ever READS these; the derivation pass is the contract boundary. The port's
  `NavWorld.BaseCost/IsFloorTile/ExitSub` map 1:1. ✅
- **BDB aux** supplies `dig(t)` = aux[blk*0x59 + 0x24]. Port `TileDigByte` reads `AuxCenter` (= +0x24). ✅
- **MetaWalk** consumes the CPath (start→goal exit sub-cells), classifies each waypoint
  FLOOR/WALL/WATER, groups, and dispatches WalkFloor/Climb/WalkObject. Calc1's job ends at the polyline.
- **Calc2 (FUN_005a22f0)** is the alternate pathfinder for `owner[+0x7b4]!=0`; campaign never sets it.

---

## 12. Unresolved (non-blocking)
1. **+0x12 bits 0x20 / 0x80 semantics** — the variant-B `0xA4` left-mask references them; their write-site
   in FUN_005a6350 is the climb-band path (`005a6350:256-277`). Decode whether 0x20/0x80 ever get set in
   practice on the campaign maps (if never, the 0xA4 bug is inert). Next: dump `+0x12` histogram from
   `tile_array.bin`.
2. **`FUN_0040b8a0(goal, 0x28)`** — the goal "near-edge / blocked-region" pre-reject (`0059d640:105`); body
   not yet read. Likely a bounds/region test; low impact (relax usually fails first).
3. **`FUN_0059a320` clear-field exact trigger** — the dist field is a lazilysized global; confirmed
   tile-major `width*y+x % modulus` but the resize predicate (`DAT_00b9bf20/24/28` vs CMap extents) is only
   skimmed. Port uses generation-stamps instead (equivalent, O(1) reset) — fine.
4. **CalcPath `+0x7d8` provenance** — what gameplay flag sets owner[+0x7d8] (territory toggle). Needed to
   know, per gnome, whether campaign uses cost v1 or v2. Next: grep writers of `+0x7d8`.
5. **`FUN_006d46ea` in CalcPath target-Z lerp** (`004158a0:135`) — a frame/random fraction used with
   DAT_00736f94; out of Calc1 scope but affects the patched goal Z.
