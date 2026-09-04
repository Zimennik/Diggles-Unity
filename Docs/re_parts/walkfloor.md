> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# WalkFloor — `CAct_WalkFloor` complete reverse-engineering (AREA_KEY: walkfloor)

> Ground-truth RE of the floor mover that produces the dwarf "gliding"/anim-coupling. Every claim cites
> `re/decompiled/Diggles/_global/<addr>_FUN_<addr>.c:line`, a struct offset, or a constant extracted directly
> from `original/Diggles.exe.rdata` (image base 0x400000; values re-extracted byte-for-byte, see §7).
>
> **Frame convention:** the 512×640 tile grid is the SIDE VIEW. native X = horizontal east
> (the walk axis); native **Y (tile rows) = the gameplay VERTICAL** (+y = down/deeper, gravity, climb axis); the
> BDB per-sub-cell height nibble = SLAB DEPTH INTO THE SCREEN (the dig axis; engine z = nibble·0.5+7, range
> 7..14.5). Distance metrics weight the depth axis ×0.5 (`DAT_0073708c = 0.5`). Where the decompile says
> "height" for the per-node z, read **DEPTH**.

---

## 0. The class stack (who ticks whom)

`CAct_WalkFloor` is NOT a flat mover. It is a 3-level nested action stack, all verified from the vftables in
`.rdata` and the dispatch sites:

```
MetaWalk dispatch FUN_004163a0:79 surface==2 (FLOOR) → FUN_00415f40 (WalkFloor FACTORY)
                                        │
CAct_WalkFloor alloc 0xc6=198 B, vftable 0x71d394 (set FUN_00415f40:66 / FUN_0041a120:25)
  • dtor slot0 = FUN_0041a120 (frees FloorPathMap@+0x14, child@+0x18, seg-array@+0x38)
  • Tick slot3 = FUN_0041aef0 ← the per-frame entry
  • Break slot2 = FUN_0041b000
  • (slot1 FUN_0041b020 = forward-tick passthrough to child@+0x18)
        │  per SEGMENT:
        ▼
  FUN_0041aac0 "MakeNextSegment": build a FloorPathMap (FUN_0041d8a0) over the next path span,
                sample the per-node target z (FUN_00424470 at (x,y+2)), then SPAWN the inner mover
                via FUN_00419130 into this+0x18.
        │
        ▼
  CAct_WalkAnim ctor FUN_00417ba0, vftable 0x71d328
  • Tick slot3 = FUN_00419c60 ← the per-frame root-motion integrator entry
        │
        ▼
  FUN_00418db0 (walk root-motion advance) / FUN_00418850 (climb-step variant, out of scope here)
```

So "gliding" = `FUN_00418db0` advancing the dwarf along a baked piecewise-linear point list by an **arc length
= 0.1 × 0.25 × (sum of the 4 directional clips' per-frame root motion)** each tick, with the per-point z
sampled from the terrain. (The `0.1` is a fixed per-tick fraction reset every tick at `00419c60:46`, NOT a
carried residual — see Verification notes.)

The two sibling structs and their key offsets (this+N), reconstructed from the decompile:

| Struct | Offset | Meaning |
|---|---|---|
| **CAct_WalkFloor** | +0x04 | CMovable (the gnome body; pos @+0xc/+0x10/+0x14, facing @+0x40/+0x44/+0x48) `0041aac0:126` |
| | +0x08 | CSubAction state enum (0/1/3/4/5) `0041aef0:37` |
| | +0x0c | COwner/CObject (gait, target, flags @+0x798/+0x7a4..) `0041aac0:159` |
| | +0x10 | CPath (Calc1 output; `[2]`=count, sub-cell int pairs ×8) `0041aac0:86,135` |
| | +0x14 | FloorPathMap (built by FUN_0041bb10/FUN_0041bff0) `0041aac0:45,92` |
| | +0x18 | child CAct_WalkAnim `0041aef0:21` |
| | +0x1c | child CAct_Climb (alternate, FUN_0041d8a0 result) `0041aac0:174` |
| | +0x20/+0x24 | segment span start/end sub-cell `0041aac0:63,77` |
| | +0x38 | baked seg-index array (`{startIdx,endIdx}` int pairs ×8) `0041aac0:134,144` |
| | +0x3c | seg count `0041aac0:40` |
| | +0x40 | seg cursor `0041aac0:39,207` |
| | +0x48 | bForward / first-segment flag `0041aac0:175` |
| **CAct_WalkAnim** | +0x04 | CMovable body `00418db0:35` |
| | +0x0c | COwner/CObject `00418db0:52`, gait @+0x798 |
| | +0x10 | the baked point list ("CPath"; `[1]`=cap,`[2]`=count, 16-B records x@0/y@4/z@8/flag@0xc) `00418db0:88` |
| | +0x14 | the start/loop/stop arc-segment table (built by FUN_0041a470) `0041a470:80` |
| | +0x1c | cursor index into the point list `00418db0:86,140` |
| | +0x20 | seg index (start) `0041a470:32` |
| | +0x24 | seg index (end / point count) `0041a470:31` |
| | +0x2c | current clip id (0xffffffff/0x10/0x15/9..0xc) `00418db0:41,58` `00419c60:38` |
| | +0x34 | total arc length of the segment `0041a470:72` |
| | +0x38/+0x3c | start/loop/stop sub-segment array (8-B `{startPt,endPt}`) + count `0041a470:85,87` |
| | +0x3c | (CAct_WalkAnim) **root-motion residual scale**, init 0.1 (0x3dcccccd) `00419c60:46` |
| | +0x40/+0x44/+0x48 | current interpolated pos x/y/z `00418db0:70-72` |
| | +0x4c/+0x50/+0x54 | facing/heading vector (from FUN_005e6060) `00418db0:154-156` |
| | +0x58 | start/stop frame counter `00418db0:44,48` |

> NOTE the offset collision: `+0x3c` is the seg-count in the WalkFloor struct but the root-motion residual in
> the WalkAnim struct. They are different objects.

---

## 1. The WalkFloor factory — `FUN_00415f40(metaWalk, segIdx, segMode)`

Called by `FUN_004163a0:79` with `(metaWalk, segEndIdx, surfaceCode)`. Allocates the 0xc6-byte CAct_WalkFloor,
but its main job (before alloc) is to compute the **destination z-cap** that the inner mover clamps the final
target height to.

```
local_c = metaWalk[+0x24] // current cached z-cap (float):24
if segMode==0 && local_c == -1 (DAT_0073773c) && segIdx < metaWalk[+0x1c]: // last segment:25
    path  = metaWalk[+0x10]                                                       // the CPath      :26
    n     = path.count (path[+0xc]) - 1                                           //                :28
    i1    = min(segIdx+1, n)                                                      //                :29-32
    i0    = min(segIdx,   n)                                                      //                :33-35
    // sample the per-sub-cell DEPTH nibble at the two endpoint sub-cells
    hA    = FUN_004093f0(path.sub[i0].x, path.sub[i0].y)   // get_hmap (PER-SUB-CELL)               :45-47
    hB    = FUN_004093f0(path.sub[i1].x, path.sub[i1].y)   //                                       :40-41,48
    h     = (hB < hA) ? hB : hA                            // take the MIN of the two              :49-53
    local_c = min( (float)h + 1.0 (DAT_00737138), 15.0 (DAT_00737568) )   // cap +1, clamp 15      :54-57
puVar7 = alloc(0xc6); set vftable=CAct_WalkFloor; zero fields // the object:59-75
... refcount the path/owner... //:76-88
FUN_0041a910(owner, movable, path, metaWalk, segIdx, segMode==0, local_c) // init+spawn:89
return puVar7
```

**Port contract:** when building the FINAL floor segment, the destination z (depth) is **clamped to
min(h_nibble + 1, 15)** where `h_nibble` = the smaller of the two endpoint sub-cell depth nibbles. This is the
"don't sink the feet into the slab" cap. `-1` is the sentinel "not yet computed / not the last segment".

---

## 2. The WalkAnim init — `FUN_0041a910(...)` + `FUN_0041a470` (baking the point list)

`FUN_0041a910` (424 B) stores refs and seeds the inner mover, then calls **`FUN_0041a470`** which BAKES the
start/loop/stop sub-segment table from arc length:

```
FUN_0041a910:
  obj[+0x10] = path; obj[+0x0c] = owner-ish; obj[+0x04] = movable:40-56
  obj[+0x20] = segStartIdx (param_5):57
  obj[+0x24] = segEndIdx (param_6):58
  obj[+0x08] = 2 (state NOTFINISHED):59
  obj[+0x44] = z-cap (param_8 = local_c from §1):60
  obj[+0x28..+0x30] = FUN_0041a2f0(...) // initial facing vector (see §5):61-64
  FUN_0041a470 // BAKE the seg table:65
  obj[+0x40] = 0 // root-motion accumulator start:67
  obj[+0x48] = bForward (param_7):68
  // dig-standoff hook (owner[+0x7e8]==3 && owner[+0x798]!=2): if within DAT_007374cc(=5.0)² of
  // the target → FUN_004234a0 (set gait/dig flags); else arm the dig-rotate state:69-83
```

### 2.1 `FUN_0041a470` — start/loop/stop arc segmentation
Computes the **total arc length** of the point span and splits it into 3 phases keyed off two constants:

```
total = Σ over span [ |pt[i+1]-pt[i]| ] (XY only; sqrt via FUN_0040b7e0):46-61
obj[+0x34] = total:72
if total >= 96.0 (DAT_00737608)::73
    // long walk → emit start..loop..stop segments
    accum = 0; cursor = segStart
    repeat:                                                                         :79-120
        segArray[k].start = cursor                                                  :92
        accum += 32.0 (DAT_007375b0)            // each emitted sub-segment ≈ 32 u   :91
        advance cursor until arc-walked >= accum (or end)                           :94-114
        segArray[k].end = cursor                                                    :119
    while (accum + 96.0 <= total)                                                   :118-120
    // then a TAIL "stop" sub-segment counted BACKWARD 32 u from the end            :134-156
    walk backward from (count-1) accumulating |Δ| until >= 32.0 → uVar8             :136-156
    segArray[last] = { uVar8 , count }                                              :160-176
else:
    // short walk → ONE sub-segment {segStart, segEnd}                              :179-194
    segArray[0] = { obj[+0x20] , obj[+0x24] }
```

**Interpretation:** `DAT_00737608 = 96.0` is the threshold above which a walk uses the full
**walk_start → walk_loop → … → walk_stop** chain; `DAT_007375b0 = 32.0` is the arc-length granularity of each
loop sub-segment and the length of the terminal "stop" sub-segment. The unit is **sub-cell ints** (the point
list stores `round(world·8)`), so 32 ints = 4.0 world units; 96 ints = 12.0 world units. Short hops (< 12 world
units) skip the start/stop framing and play a single segment.

> This is the "walk_start/loop/stop chaining" the brief asks for: it is an ARC-LENGTH partition of the route,
> not a fixed clip schedule. The clip CHOICE per sub-segment happens later (§4).

---

## 3. The per-tick root-motion advance — `FUN_00418db0` (THE core)

This is the heart of the gliding. Entry from `FUN_00419c60:79` (the CAct_WalkAnim tick). It runs a small state
machine on `movable[+0x234]` (the start/loop/stop phase): **1 = walk_start, 0 = walk_loop, 2 = walk_stop/move**.

### 3.1 start/stop framing (clip-id selection by phase) `00418db0:35-68`
```
if movable[+0x234] == 1: // START phase
    if FUN_004230d0(0x15) == -1:  movable[+0x234] = 0    // no walk_start clip → go straight to loop
    else:
        obj[+0x2c] = 0x15                       // clip id 21 = walk_start
        if obj[+0x58] < FUN_00423180(0x15):     // advance start-frame counter up to clip length
            obj[+0x58]++
        else: movable[+0x234] = 0; obj[+0x58]=0 // start done → loop
if movable[+0x234] == 0: // LOOP phase
    if FUN_00422ee0(0x10) == -1: movable[+0x234] = 2
    else:
        obj[+0x2c] = 0x10                        // clip id 16 = walk_loop
        if obj[+0x58] < FUN_00423180(0x10): obj[+0x58]++
        else: movable[+0x234] = 2; obj[+0x58]=0  // loop done → move/stop
if movable[+0x234] == 2: // MOVE phase → integrate position
    ... §3.2 ...
```
Clip ids: **0x15 (21) = walk_start, 0x10 (16) = walk_loop.** `FUN_004230d0`/`FUN_00422ee0` return the clip's
"next" link (or -1 if absent); `FUN_00423180(id)` returns the clip's frame-length (record[+0x10]).

### 3.2 the displacement formula (disasm-confirmed, `00418ead-00418eff`)
```
local_34 = obj[+0x48] (cur z saved)
rm = FUN_00423270(10) + FUN_00423270(9) + FUN_00423270(0xb) + FUN_00423270(0xc) // 4 clip motions
local_18 = rm * 0.25 (DAT_00737058) // full-clip arc length this tick:82
local_8 = obj[+0x3c] * local_18 // arc to consume = residual_scale × full arc:83
```
- `FUN_00423270(clipId)` returns (XMM0) the **per-frame root-motion arc scalar** of that clip:
  `record = gaitBase + 4 + clipId·0x14` where `gaitBase = obj+8 + gait·0x1e4`, `gait = owner[+0x798]` (0..3);
  value read = `record[+8]`. If `record[+4] == -1` it falls back to `obj+0x5b8 + clipId·0x14 + 8`
  (`FUN_00423270:0042327e..00423363`). So **speed is entirely the clip's baked root motion × 0.25**, summed
  over clip ids **9,10,11,12** (the four-direction motion slots of the active gait).
- `obj[+0x3c]` is a per-tick **advance fraction**, **RESET to 0.1** (0x3dcccccd) at the TOP of every
  `FUN_00419c60` tick, unconditionally (`00419c60:46`, disasm `00419ca0 mov dword ptr [esi+0x3c],0x3dcccccd`,
  before any branch). So the per-tick arc consumed is ALWAYS `0.1 · fullArc`. The "unconsumed-remainder"
  write at `00418db0:149` (§3.4) is **dead state** — overwritten by line 46 before it can be read on the next
  tick. ⚠️ **CORRECTION (was wrong in v1):** the 0.1 is NOT a residual carried frame-to-frame; it is a fixed
  per-tick fraction. There is no cross-frame sub-stepping smoothing via this field. (Cursor `obj+0x1c` IS
  persistent, and `obj+0x40..` cur-pos is re-seeded from the body each tick at `00419c60:39-41`, so progress
  is tracked by cursor + body position, not by a carried residual.) See §Verification notes.

### 3.3 consuming arc length along the point list `00418db0:85-144`
While `local_8 > 0` and cursor `i = obj[+0x1c]` not at the end:
```
p = pointList[i] // 16-B record: x@0,y@4,z@8
d2 = (p.x - cur.x)² + (p.y - cur.y)² + ((p.z - cur.z)·0.5)² // z weighted ×0.5 (DAT_0073708c):102-104
seg = sqrt(d2) (FUN_0040b7e0):105
if local_8 < seg: // we stop INSIDE this segment
    t = local_8 / seg                                                                              :119
    cur.x += (p.x-cur.x)·t ;  cur.y += (p.y-cur.y)·t ;  cur.z += (p.z-cur.z)·t   // LERP            :120-125
    local_8 = 0; break                                                                             :126
else: // consume the whole segment, snap to p
    local_8 -= seg                                                                                 :129
    cur = p                                      // cur.x/y/z = pointList[i]                        :137-139
    i++ ;  obj[+0x1c] = i                                                                           :140-141
```
So the mover **interpolates between adjacent on-terrain points by arc length** — never start→goal directly. The
per-point z is the terrain depth baked at build time (§4.2). This is the floor-following.

### 3.4 leftover write (DEAD) + facing update `00418db0:145-158`
```
obj[+0x3c] = local_8 / local_18 // leftover arc as fraction → DEAD: reset to 0.1 next tick:149
facing = FUN_005e6060(...) // recompute heading (model orientation matrices):153
obj[+0x4c..+0x54] = facing:154-156
FUN_00418c70(&segStart, obj+0x40) // pick the TURN/CLIMB clip from the heading delta (§5):157
```
> ⚠️ **CORRECTION (v1 said this was a carried residual):** the `obj[+0x3c]` write at line 149 is overwritten
> by the unconditional reset at `00419c60:46` before the next tick reads it (§3.2). It does nothing. The
> per-tick advance is therefore a **fixed 0.1·fullArc**, not a smoothed sub-step.

**Port contract (the exact gliding loop):**
```
fullArc = (rm9 + rm10 + rm11 + rm12) * 0.25 // rmK = active gait's clip-K root-motion scalar
advance = 0.1 * fullArc // FIXED per-tick fraction (NOT a carried residual)
cur = body // re-seed from body each tick (00419c60:39-41)
while advance > 0 and cursor < pts.Count:
    seg = sqrtWeighted(pts[cursor] - cur)            // (dx² + dy² + (dz*0.5)²) then sqrt
    if advance < seg: cur = lerp(cur, pts[cursor], advance/seg); advance = 0; break
    advance -= seg; cur = pts[cursor]; cursor++
body = cur // committed via the collision sweep FUN_00419840
// (obj[+0x3c] leftover write exists in the binary but is dead — do NOT carry it)
```
This is FRAME-COUPLED, not time-based: at 10 fps the per-tick advance = (summed clip frame translation) ×
0.25 × 0.1.

---

## 4. The point list & per-node depth — `FUN_0041aac0` + `FUN_00424470`

### 4.1 What `FUN_0041aac0` produces
For each segment it builds a FloorPathMap (`FUN_0041bff0` fills a box `[startX..endX]×[startY..endY]` expanded by
±3 sub-cells, `0041aac0:64-84`), classifies wall vs walkable cells (`FUN_0041c4b0`), then runs the BFS
(`FUN_0041d8a0:173`) to produce the actual move waypoints, and spawns the CAct_WalkAnim mover
(`FUN_00419130:206`). The waypoints are 16-byte `{x,y,z,flag}` in world int·8 (sub-cell) coordinates.

### 4.2 The per-node DEPTH (z) sample — `FUN_00424470` (the (x, y+2) support-cell read)
`FUN_0041aac0:115/122` samples the terrain depth for the node and offsets it:
```
h = FUN_00424470(x, y) // per-sub-cell depth nibble at the SUPPORT cell (x, y+2)
if (going up / iVar5>=0): z_node = h - 0.5 (DAT_0073708c):122-123
else (going down / iVar5<0): z_node = h - 1.0 (DAT_00737138):115-116
```
**`FUN_00424470(x,y)` reads the height nibble at sub-cell `(x>>3, (y+2)>>3)`, sub-index `((y+2)&7)<<3 | (x&7)`**
(`00424470:19,29`). The **`+2` in Y is the key**: in the side view +y = down, so `y+2` is 2 sub-cells *below the
node* — i.e. the cell that physically SUPPORTS the feet. The mover places its node depth at
`support_depth − {0.5 | 1.0}` so the body sits just in front of (above, in depth) the supporting slab. The
0.5 vs 1.0 bias is chosen by the vertical direction of travel (`FUN_00423be0` sign of the path Δ, `0041aac0:109`).

> This is exactly the "floor-height following" the brief flags as the port's straight-Y-lerp bug: each node
> carries its OWN sampled support depth; the integrator (§3.3) lerps z between neighbours by arc length and
> weights it ×0.5 in the distance metric. **Never lerp z from start→goal.**

`FUN_00424470` is the same sampler family as `get_hmap` (`FUN_004093f0`) but hard-wired to read `(y+2)` and the
CMap singleton `DAT_0077241c`. The block aux stride is `0x59=89`, sub-index `((subY&7)<<3 | subX&7)`,
height table `aux[0..63]`.

---

## 5. Facing math + gait + turn-clip selection

### 5.1 Initial + per-frame facing — `FUN_0041a2f0` / `FUN_005e6060`
`FUN_0041a2f0` (called in `FUN_0041a910:61`) sets the initial heading. If the segment is degenerate
(`obj[+0x24]==count`) it copies the owner's stored target dir `owner[+0x7a4..+0x7ac]` (`0041a2f0:72-76`). Else it
derives a direction from the last point pair scaled ×0.125 (DAT_0073703c, `0041a2f0:39-40`); the z is filled by
the surface class (`FUN_00423730`): **floor → z=0 via FUN_004238e0; wall (1/2) → z = up 14.5 / down 12.5**
(DAT_00737564 / DAT_00737548 by sign of FUN_00423be0, `0041a2f0:42-66`). `FUN_005e6060` (per-tick, `00418db0:153`)
recomputes the model orientation from the view/model matrices and returns the yaw triple written to `obj+0x4c`.

### 5.2 Turn / climb clip choice from heading — `FUN_00418c70` (the 4-sector rule)
`FUN_00418c70(prevPos, curPos)` picks the clip id (`obj[+0x2c]`) from the **angle** between the previous and
current node:
```
// disasm 00418c86-00418cb4: ALL THREE deltas are computed and passed to FUN_0040b700:
// d.x = curPos.x - prevPos.x; d.y = curPos.y - prevPos.y; d.z = curPos.z - prevPos.z
ang = FUN_0040b700(d) → FUN_006f52b0 // atan2 over the FULL delta vector:13-16
if ang < 0: ang += 2π (DAT_007374f8 = 6.28319):18-19
ang = (ang / 2π) · 4.0 (DAT_007374a0) // quantise heading into 4 sectors:21
                                                     // thresholds 1.0 / 2.0 / 3.0
if 0 <= ang < 1.0: clip = 0xb (11):35-36
elif 1.0 <= ang < 2.0: clip = 10:31-33
elif 2.0 <= ang < 3.0: clip = 0xc (12):27-29
else (3.0..4.0): clip = 9:24-25
if obj.cursor (+0x1c) >= pointList.count (+0x14 →[8]): clip = 1 // arrived → standard/idle clip:38-40
```
Constants confirmed: the quantiser uses `DAT_007374a0 = 4.0` and the `1.0/2.0/3.0` sector splits
(DAT_00737138/00737330/00737428). The 4 quantised headings map to the **four directional motion clips
9,10,11,12** that §3.2 sums — the gait carries a per-direction root-motion magnitude, and the heading picks
which one drives this leg. A heading change crossing a sector boundary swaps the directional clip
(the in-place "turn" feel).

> ⚠️ **CORRECTION (v1 errors):** (a) v1 said the heading is `atan2(curPos.z − prevPos.z)`; the disasm
> (`00418c86-00418cb4`) shows **all three deltas** (x@0, y@+4, z@+8) are written to a temp and passed to
> `FUN_0040b700` — the heading is over the full Δ vector, not the z-delta alone. (b) v1 attributed
> `DAT_007370d0 = π/4` to this floor turn-clip selector "as the per-RE turn-threshold". **`DAT_007370d0` is
> NOT referenced in `FUN_00418c70` at all** — it is used at `FUN_00418850:93` (the CLIMB-step variant, out of
> scope for walkfloor) as an angle threshold. The floor turn selector uses only 4.0 and the 1/2/3 splits;
> there is no π/4 in it. (Grep over `_global/`: `DAT_007370d0` appears in `00418850` but not `00418c70`.)

### 5.3 Gait selection — `FUN_004234a0`
`owner[+0x798]` is the gait index (0..3) that selects which `0x1e4`-byte motion block §3.2 reads. It is set by
`FUN_004234a0(owner)` (called from `FUN_0041a910:77` and `FUN_00419c60:55`):
```
if owner[+0x7e8] == 2: owner[+0x798] = 1; return // (a "type-2" mover → gait 1):10-12
owner[+0x798] = 0 // default gait 0:14
if owner[+0x7e8] == 0: owner[+0x7be] = 1 // arm a flag (idle/anim trigger):15-17
```
So in plain floor walking the gait is **0 (walk) by default**, **1 for the type-2 case**. The brief's
"speedtype 2 → run" maps upstream (the walk verb sets `owner[+0x7e8]`); run vs walk differs because the run
gait's clip records carry larger `record[+8]` root-motion scalars — *the integrator constant 0.25 is the same*.

---

## 6. Arrival, walk-events, state propagation — `FUN_0041aef0` / `FUN_00419c60`

### 6.1 Inner-mover arrival & next-segment — `FUN_00419c60`
The CAct_WalkAnim tick (`FUN_00419c60`) calls `FUN_00418db0` (line 79, when the current waypoint flag@+0xc==0,
i.e. a walk node) or `FUN_00418850` (flag==2, climb node, line 83). After integrating it runs the inter-unit
collision sweep (`FUN_00419840`) and a Tcl walk-timer-callback distance check:
```
// walk-event: if a -walktimercallback is armed and we are within (scaled) range of its node:154-175
range² = owner[+0x7c4]; if iVar7==0 (current waypoint flag@+0xc == 0, i.e. a WALK node — the FUN_00418db0
                                     branch): range² *= _DAT_007370a8 (= 0.65)                          :162-164
d² = (node - cur) with z×0.5 (DAT_0073708c); if d² <= range²: state = 5 (WALKEVENT):166-173
```
> ⚠️ **CORRECTION (v1 said `if gait==0`):** the `* 0.65` scale at `00419c60:163-164` is gated on `iVar7 == 0`,
> where `iVar7 = local_c = *(waypoint + 0xc)` (the per-node flag captured at `00419c60:76-77,96`): **0 = walk
> node** (routed to `FUN_00418db0`), **2 = climb node** (routed to `FUN_00418850`). It is the WAYPOINT-FLAG,
> not the gait field (`owner+0x798`). For ordinary floor walking the two coincide in effect (a floor leg has
> flag-0 nodes), but a faithful port must key the 0.65 walk-event-range scale on the node being a walk node,
> not on gait==0.
On `FUN_00419840` returning 0 (route blocked) it sets state 0/3/4 per the switch (`00419c60:133-151`):
**case 0 → reset pos to body, state 3; case 2 → reset pos+facing; state 3.** On a clean finish it sets
state 4 (`00419c60:207`).

### 6.2 Outer arrival — `FUN_0041aef0` (CAct_WalkFloor::Tick)
```
if child@+0x18 == 0: child = FUN_0041aac0 // first/continue segment:21-25
result = child.tick:29
if result == 0 (child done):
    switch child.state (+0x08):
      case 4: rebuild next segment (FUN_0041aac0)    // TARGETREACHED for this segment, more to go    :43,52
      case 3: this.state = 3 (NOPASSAGE); stop                                                        :39-41
      case 5: this.state = 5 (WALKEVENT)                                                              :45-47
      default/0: this.state = 0                                                                       :34-37
// final-arrival proximity (only owner[+0x7b4]==0)::59-66
if owner[+0x7c8] valid and d²(body, owner[+0x7a4..]) < owner[+0x7c8]: this.state = 5
    where d² weights z by 0.5 (DAT_0073708c)
```
When the FloorPathMap for the next span is empty, `FUN_0041aac0:41` sets state 4 (the whole route is consumed).

### 6.3 The arrival epsilon (brief's 0.09)
`DAT_0073702c = 0.09 = 0.30²` is the climb/standoff arrival eps used by sibling actions; in the floor mover the
"arrival" is **arc-length exhaustion** (cursor reaches the point count, §3.3) rather than an epsilon test. The
dig-standoff hook (`FUN_0041a910:69-78`) uses `DAT_007374cc = 5.0`: at `0041a910:74-76` it computes the
depth-weighted squared distance, takes its **sqrt** (`FUN_0040b7e0` at:75, in-place), then tests
`sqrt(d²) < 5.0` — i.e. straight-line distance < 5.0 (equivalently d² < 25.0). v1's shorthand "5.0²" is
loose: the *constant* is 5.0 and the compare is against the sqrt'd distance, not the squared. The depth axis is
×0.5-weighted in EVERY distance metric here
(`00418db0:103`, `0041aef0:62`, `00419c60:168`, `0041a910:73`) — replicate this exactly so a depth gap never
false-triggers arrival/events.

---

## 7. Constants

| DAT | VA | float | u32 | Role in WalkFloor |
|---|---|---|---|---|
| DAT_00737058 | 0x737058 | **0.25** | 0x3e800000 | **root-motion → arc scale** (§3.2) |
| DAT_0073708c | 0x73708c | **0.5** | 0x3f000000 | **depth-axis weight in ALL dist metrics; z=nibble·0.5+7**; node z bias (up) |
| DAT_00737138 | 0x737138 | **1.0** | 0x3f800000 | node z bias (down, §4.2); z-cap +1 (§1); sector split |
| DAT_00737568 | 0x737568 | 15.0 | 0x41700000 | z-cap clamp (§1) |
| DAT_00737564 | 0x737564 | 14.5 | 0x41680000 | climb-up facing z (§5.1) |
| DAT_00737548 | 0x737548 | 12.5 | 0x41480000 | climb-down facing z (§5.1) |
| DAT_007375b0 | 0x7375b0 | **32.0** | 0x42000000 | walk-loop sub-segment arc granularity (§2.1) |
| DAT_00737608 | 0x737608 | **96.0** | 0x42c00000 | start/loop/stop threshold arc (§2.1) |
| DAT_007370d0 | 0x7370d0 | 0.785398 | 0x3f490fdb | π/4 — **NOT used by walkfloor** (climb-step `FUN_00418850:93` only; see Verification notes) |
| DAT_007374f8 | 0x7374f8 | 6.28319 | 0x40c90fdb | 2π (heading wrap, §5.2) |
| DAT_007374a0 | 0x7374a0 | 4.0 | 0x40800000 | heading → 4 sectors (§5.2) |
| DAT_00737330 | 0x737330 | 2.0 | 0x40000000 | sector split (§5.2) |
| DAT_00737428 | 0x737428 | 3.0 | 0x40400000 | sector split (§5.2) |
| DAT_0073703c | 0x73703c | 0.125 | 0x3e000000 | sub-cell int → world (1/8) |
| DAT_0073752c | 0x73752c | 8.0 | 0x41000000 | world → sub-cell |
| DAT_0073773c | 0x73773c | **−1.0** | 0xbf800000 | z-cap "unset" sentinel (§1); also facing-z "unset" guard in `FUN_0041a2f0:42` (NOT a residual guard — v1 mislabel) |
| DAT_007377e0 | 0x7377e0 | −1000.0 | 0xc47a0000 | "no node-height" sentinel (FUN_004192a0/00419c60) |
| DAT_0073702c | 0x73702c | 0.09 (0.3²) | 0x3db851ec | arrival eps² (siblings/standoff) |
| DAT_007374cc | 0x7374cc | 5.0 | 0x40a00000 | dig-rotate proximity: test `sqrt(d²) < 5.0` (`0041a910:75-76`) — constant is 5.0, NOT 5.0² |
| _DAT_007370a8 | 0x7370a8 | 0.65 | 0x3f266666 | walk-node walk-event range scale (§6.1; gated on waypoint flag==0, not gait) |
| DAT_00736fcc | 0x736fcc | 0.005 | – | mesh-bbox scale (collision sweep, FUN_00419350) |
| per-tick advance frac | – | 0.1 | 0x3dcccccd | **fixed** per-tick arc fraction, RESET each tick (`00419c60:46`) — NOT a carried residual |
| gait block stride | – | 0x1e4=484 | – | per-gait motion block (FUN_00423270) |
| clip record stride| – | 0x14=20 | – | per-clip motion record |
| clip ids | – | 9,10,11,12 / 16 / 21 | – | dir motions / walk_loop / walk_start |

---

## 8. Tcl ↔ native contract (movement)

- The walk is built by `action <obj> walk "<opts>" <fin> <brk>` → `FUN_00407430`/`FUN_00420840`; defaults
  `-canclimb 1 -useobjects 1 -waving 1 -stopat 2.0 -targetdist -1` (`FUN_00420520`). `-speedtype 2` → run
  (sets the upstream owner field consumed by `FUN_004234a0` gait selection, §5.3).
- Per-frame the engine ticks the action stack (§0); the only Tcl callouts are the inter-unit collision method
  (`FUN_004194d0` → `call_method this …`) inside the collision sweep, and the optional
  `-walktimercallback` proximity fire (§6.1) which sets state 5 (`get_walkresult` = 5 = WS_WALKEVENT).
- `get_walkresult` reads `gnome+0x248` (the propagated CSubAction state): **2** = walking, **3** = no-passage,
  **4** = arrived, **5** = walk-event. Scripts branch ONLY on 4 (arrived) and 2 (walking).
- The point list is sub-cell ints (world·8); world = int·0.125 (DAT_0073703c). Depth/z uses the BDB aux nibble
  (z = nibble·0.5 + 7.0).

---

## 9. PORT GAP ANALYSIS vs current code

Current port: `Assets/Diggles/Runtime/Engine/Actions/WalkClimbAction.cs` (and `ClickToMoveService.cs` issuing
the walk verb). It is a **single flat per-leg mover** that does NOT reproduce the native integration:

1. **Speed is NOT root-motion-coupled the native way.** The port computes
   `speed = |MotionOffset| / (frames/10)` then applies a hand-tuned `GroundSpeedScale = 1/1.3` fudge
   (`WalkClimbAction.cs:183-190`). Native: per-tick advance = `0.1 · (rm9+rm10+rm11+rm12)·0.25` consumed as
   ARC LENGTH along the point list (§3.2-3.4). The `0.1` is a **fixed per-tick fraction reset every tick**
   (`00419c60:46`), NOT a carried residual — see Verification notes. **Fix:** drive by arc-length consumption
   of `0.1 × 0.25 × Σ(directional-clip root motion)`; no 1.3 fudge, no frame-to-frame residual carry.

2. **No start/loop/stop arc framing.** Native splits the route at 96-unit arc into walk_start (clip 21) →
   walk_loop (clip 16) sub-segments of 32-unit arc + a 32-unit terminal stop (§2.1, §3.1). The port plays a bare
   `gehen_loop` with an ad-hoc `_runDelay` auto-promotion (`:255-268`). **Fix:** port `FUN_0041a470` arc split
   and the `FUN_00418db0:35-68` phase machine.

3. **Per-node depth-follow is missing / wrong axis.** Native samples the SUPPORT cell `(x, y+2)` depth nibble
   per node and sets node z = `support − {0.5|1.0}` (§4.2), then lerps z between neighbours weighted ×0.5. The
   port treats Up=+Y and lerps straight to leg targets (`:239-253`) with the WRONG frame (`.x` lateral, `.y`
   vertical) — the brief's straight-Y-lerp bug. The port DOES already have the correct per-sub-cell sampler
   (`NavWorld.GetHmapSub`, `NavWorld.cs:68`) but the WalkFloor leg builder does not call it per node with the
   `(y+2)` support offset. **Fix:** generate sub-cell-spaced points each carrying `GetHmapSub(x, y+2)`-derived z;
   lerp z by arc length; weight depth ×0.5 in every distance.

4. **Arrival uses a positional epsilon (0.12), not arc-length exhaustion.** `WalkClimbAction.cs:74,244` uses
   `arriveEps = 0.12`; native advances the cursor by arc consumption and arrives when the cursor reaches the
   point count (§3.3). The native depth-weight is 0.5 and the standoff eps is 0.09 (0.3²), not 0.12. **Fix:**
   arc-length cursor + 0.09² where an eps is needed, with ×0.5 depth weight.

5. **Turn/heading clip selection is absent.** Native picks the directional clip (9/10/11/12) by quantising the
   node-to-node heading into 4 sectors (π/4 snap) via `FUN_00418c70` (§5.2). The port hard-codes
   `gehen_loop`/`laufen_schnell_loop` and a wall-normal facing for climb. **Fix:** port the 4-sector heading
   quantiser to choose the directional motion clip and the in-place turn feel.

6. **The whole climb choreography is synthesized** (`Kind{Mount,CrawlLeft,…}`, `:27`) — out of scope for
   walkfloor (covered by the climb spec) but note: the native FloorPathMap (`FUN_0041d8a0`) that produces the
   climb waypoints is built INSIDE `FUN_0041aac0` (the WalkFloor segment builder), so a faithful port shares one
   segment-builder for floor + wall, not two disjoint actions.

7. **Facing uses transform.forward / wall-normal**, not the native `FUN_005e6060` model-matrix heading +
   `FUN_0041a2f0` direction. The port's `FaceHorizontal` (`:325`) is a reasonable approximation but uses the
   nav-basis (+Y up) frame; the native heading is computed in the model/view matrices and quantised. Lower
   priority once §1-§5 land.

**Already correct / reusable in the port:** `NavWorld.GetHmapSub` (the per-sub-cell depth sampler, mirrors
`FUN_004093f0`/`FUN_00424470`); the native↔world coordinate conversion; `Calc1Pathfinder` for the route; the
clip table on `DigglesAnimator`. The constants in §7 (especially **0.25, 0.5, 32.0, 96.0, per-tick frac 0.1**)
must be hard-wired to the extracted values, NOT re-tuned.

---

## 10. Unresolved / to confirm during impl

1. **Exact gait→clip-id table.** Clip ids 9/10/11/12 are "the active gait's 4 directional motion slots";
   16=walk_loop, 21=walk_start. The mapping of these engine clip-ids to the named animset entries
   (`gehen_loop`, `laufen_schnell_loop`, `drehen_*`) lives in `set_class_animset` (`z_anims.tcl:1278+`), not in
   the WalkFloor funcs. Confirm by dumping a gnome's gait block (`owner+8 + gait·0x1e4`) at runtime.
2. **walk_stop clip id.** §2.1 bakes a terminal 32-unit "stop" sub-segment, but the phase machine
   (`00418db0:35-68`) only sets walk_start(21)/walk_loop(16) explicitly; the stop clip appears to be selected by
   `FUN_00418c70` when the cursor reaches the end (clip 1 = "standard/idle", `00418c70:38-40`). Confirm whether
   a dedicated walk_stop clip exists or the framing just decelerates into idle.
3. **`FUN_005e6060` heading math** (model/view matrix product) is reconstructed only structurally (Ghidra
   collapses the SSE matrix multiplies); the precise output basis vs our `SpawnFaceWorld` convention needs a
   runtime cross-check. Non-blocking — the 4-sector quantiser (§5.2) is the load-bearing part.
4. **`FUN_00418850`** (the climb-step sibling of `FUN_00418db0`, `00419c60:83`) is NOT decompiled here (flag==2
   path); it belongs to the climb spec but shares the residual/arc machinery.
5. **Per-segment FloorPathMap box expansion (±3) and the `FUN_0041c4b0` wall classifier** (`0041aac0:93`) are
   referenced but the wall-step threshold (height nibble step > 2) is the climb spec's domain; confirm it reads
   the same `FUN_00424470` sampler.

---

## Verification notes (adversarial pass,)

A second agent re-opened every cited primary source (decompiled C **and** raw `Diggles.exe` disasm via
capstone) to refute the 11 load-bearing claims. Constants were re-extracted from the PE byte-for-byte and all
21 match §7. Results:

**CONFIRMED (unchanged):** claims 2, 3, 4, 5, 6, 8, 9 — verified against decompile and, where Ghidra mangled
the function, against raw disasm:
- **Claim 2** (`FUN_00423270` returns `record[+8]`, fallback `owner+0x5b8+clipId·0x14+8`, discriminator
  `record[+4]==-1`): Ghidra models this as returning a *pointer* and drops the final load. The disasm
  (`00423316 movss xmm0,[eax+esi+8]` after `0042330b add eax,4`, esi=clipId·0x14, eax=owner+8+gait·0x1e4;
  fallback `0042332a add eax,0x5b8` then `00423337 movss xmm0,[eax+esi+8]`) confirms the offsets EXACTLY. The
  v1 author reconstructed correctly.
- **Claim 8** cap = `min(min(hA,hB)+1.0, 15.0)`: confirmed `00415f40:47-57`. (The §1 gate comment "last
  segment" is loose — the actual gate is `param_4==0 && cap==−1 && segIdx < count`; the formula is right.)
- **Claim 9** vftables: dumped from `.rdata` — WalkFloor 0x71d394 slots {0x41a120,0x41b020,0x41b000,0x41aef0}
  and WalkAnim 0x71d328 slot3=0x419c60 — all match.

**CORRECTED:**
- **Claim 1 — the "residual carried frame-to-frame" is FALSE.** `obj[+0x3c]` is RESET to 0.1 (0x3dcccccd)
  unconditionally at the top of every `FUN_00419c60` tick (`00419c60:46`; disasm `00419ca0 mov dword ptr
  [esi+0x3c],0x3dcccccd`, in the prologue with no branch over it). The leftover-fraction write at
  `00418db0:149` is therefore **dead state** — overwritten before the next tick can read it. Cur-pos
  (`obj+0x40..`) is likewise re-seeded from the body each tick (`00419c60:39-41`). Consequently the per-tick
  advance is a **fixed `0.1 · fullArc`** arc length, where `fullArc = 0.25·Σ rm{9,10,11,12}`. There is no
  cross-frame sub-stepping/smoothing via this field. Fixed in §0, §3.2, §3.4, §7 table, §9.1. (The rest of
  claim 1 — `fullArc = 0.25·(rm9+rm10+rm11+rm12)`, the per-clip sum, consumed as arc length — is confirmed.)
- **Claim 7 — two errors.** (a) The heading is computed from the **full Δ vector** (x@0, y@+4, z@+8), not
  `atan2(Δz)`. Disasm `00418c86-00418cb4` writes all three deltas to a temp and passes the pointer to
  `FUN_0040b700`; Ghidra surfaced only the z-delta (last in XMM0). (b) `DAT_007370d0 = π/4` is **NOT used by
  `FUN_00418c70`** — grep over `_global/` shows it is referenced only by `FUN_00418850:93` (the climb-step
  variant, out of scope). The floor turn-clip selector uses solely `4.0` and the `1.0/2.0/3.0` splits. The
  v1 hand-wave "π/4 baked into the 4-sector split" was removed. The 4-sector→clip {11,10,12,9} mapping and the
  cursor≥count→clip 1 fallback ARE correct. Fixed in §5.2 and §7 table.
- **Claim 10 — the `×0.65` gate is the WAYPOINT FLAG, not the gait.** `00419c60:163` tests `iVar7 == 0` where
  `iVar7 = *(waypoint+0xc)` (0=walk node → `FUN_00418db0`; 2=climb node → `FUN_00418850`), captured at
  `:76-77,96`. It is not `owner+0x798` (gait). For ordinary floor legs the two coincide, but the port must key
  the scale on the node flag. Also, `DAT_007374cc=5.0` is compared against `sqrt(d²)` (a sqrt at `0041a910:75`
  precedes the `< 5.0` test), so it is the constant **5.0**, not "5.0²"; the test is `dist < 5.0` ⟺ `d² < 25`.
  Fixed in §6.1, §6.3, §7 table. (The "arrival = arc-length exhaustion, no epsilon" part is confirmed.)

**MARKED INFERENCE (not refuted, but not literally in the bytes):**
- **Claim 11** "run vs walk differs ONLY via larger baked `record[+8]` root motion." `FUN_004234a0` (gait=1 if
  `owner[+0x7e8]==2`, else 0) and the `gait·0x1e4` block selection are confirmed in code/disasm. The
  "differs only via root motion, integrator 0.25 unchanged" is a sound inference (0.25 is a fixed `.rdata`
  constant independent of gait) but was not separately proven by inspecting a run-gait's clip records; keep it
  labelled INFERENCE pending a runtime gait-block dump (already listed in §10.1).
- **Claim 4** the "up/down" labels on the ±0.5/−1.0 node-z bias are interpretive: `FUN_00423be0` returns the
  sign of the **ΔX** between adjacent path points (`00423be0:35`, offset 0), not a depth delta. The mechanic
  (`iVar5<0 → h−1.0`, else `h−0.5`, `0041aac0:111-123`) and the `(x,y+2)` support sample (`00424470:19,29`)
  are confirmed; only the "going up/going down" gloss is a label, not a proven vertical direction.
