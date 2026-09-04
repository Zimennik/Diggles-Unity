> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# climb-exec — The Climb Executor (CAct_Climb + CAct_WalkAnim), Reverse-Engineered

> Ground-truth RE of how a single WALL segment is *executed* in Diggles (2001): the `CAct_Climb`
> sub-action that MetaWalk dispatches for a wall span, and the per-leg `CAct_WalkAnim` mover it
> spawns — including **how the climb clip is chosen per leg** (direction → animset slot), the
> root-motion-coupled speed, facing while climbing, and the wall→floor handoff at the end of a
> descent. Every claim cites `FUN_addr:line` from `re/decompiled/Diggles/_global/*.c`, with struct
> offsets and `.rdata` float constants extracted directly from `original/Diggles.exe` (imgbase
> 0x400000, `.rdata` VA 0x715000 / raw 0x313e00).
>
> **Frame note (project ground truth):** native X = horizontal/east (the LATERAL climb axis); native
> Y = the gameplay VERTICAL (the up/down climb axis, +y deeper); the BDB height nibble = SLAB DEPTH
> into the screen (the depth axis, weighted ×0.5 in every distance metric). When this doc says
> "vertical"/"up/down" it means tile-Y; "lateral"/"sideways" means tile-X; "depth" means the nibble axis.

---

## 0. Overview & call graph

A WALL span of the MetaWalk path is executed by **`CAct_Climb`** (the doc's §5.2). MetaWalk's segment
loop dispatches it through **`FUN_00415e10`** (the "WALL → Climb" factory). `CAct_Climb` does NOT move
the dwarf itself: it mirrors the segment's waypoints into a flat array of **0x10-byte records** and then,
per waypoint, spawns a **`CAct_WalkAnim`** (the actual root-motion mover) that walks the body from the
current position to that record. When a `CAct_WalkAnim` reports "arrived", `CAct_Climb` spawns the next
one. `CAct_WalkAnim` is what selects the clip (kletter_hoch/runter/links/rechts, the
stand↔kletterstand transitions, etc.) **from the geometry of the leg**, advances the body by clip root
motion, and turns the body to face the leg.

```
MetaWalk segment loop (004163a0)
   └─ WALL segment ─▶ FUN_00415e10 (CAct_Climb factory)
                         ├─ alloc CAct_Climb (vftable CAct_Climb::vftable)
                         ├─ store owner/movable refs        [+5]=param_2 [+6]=param_3 [+2]=state=2
                         └─ FUN_0040bb70(srcPath, param_2)   ─▶ [+7] = waypoint-record array
                                                                  (0x10 B/record: x,y,z, flag@+0xc)

CAct_Climb::tick = FUN_0040bf80
   ├─ if owner[+0x7b0]==0 (canclimb off) → state=3 (NOPASSAGE), return
   ├─ if no current child → FUN_0040be90 spawns CAct_WalkAnim toward record[idx]
   ├─ tick the child (vftable+0xc); if child not done → return "busy"
   └─ child done → copy child result (0/3/4/5) up, then FUN_0040be90 → next record

CAct_WalkAnim::tick = FUN_00419c60 (the per-leg mover)
   ├─ FUN_004192a0 leg-END predicate (cursor>=count + flag/phase check; NOT an eps test)
   ├─ record[idx].flag == 0 → FUN_00418db0 (FLOOR-style step: walk_start/loop/stop + turns)
   ├─ record[idx].flag == 2 → FUN_00418850 (WALL-style step: mount/dismount + climb dirs)
   ├─ FUN_00419840 root-motion integrator (advances body by clip translation)
   └─ writes its own state to (this+8): 0/3/4/5

   clip selection happens INSIDE the step functions, into (this+0x2c) = animset slot:
      FUN_00418c70  climb direction → slot {9, 0xa, 0xb, 0xc}  (standard/up/down/right)
      FUN_00418280  floor turn/loop → slot {6,7,8, 0xd, ...}
      FUN_00418850  mount/dismount  → slot {0x10, 0x11, 0x14, 0x15}
   slot → clip resolved via FUN_00422ee0(obj, slot) against the per-gait animset bank.
```

---

## 1. CAct_Climb construction — `FUN_00415e10` (the WALL factory) + `FUN_0040bb70` (the records)

### 1.1 `FUN_00415e10` — build a CAct_Climb for one wall segment
`undefined4* __thiscall FUN_00415e10(int self, undefined4 param_2, undefined4 param_3, undefined4 param_4)`
(`00415e10`). `self` is the MetaWalk; `param_2`/`param_3` are the segment endpoints (passed straight to
`[+5]`/`[+6]`); `param_4` is the **CPath / waypoint source** for `FUN_0040bb70`.

```
puVar5 = alloc(0x9f) // 00415e10:25 (CAct_Climb object)
puVar5[0] = CAct_Climb::vftable //:32
puVar5[3] = 0; puVar5[4] = 0 //:30-31 (CSubAction base fields)
puVar5[7] = 0; puVar5[8] = 0 //:33-34
FUN_0041fe30(owner, movable,...) //:61 CSubAction base ctor (refs the owner/movable)
puVar5[5] = param_2 //:62 segment-start endpoint
puVar5[6] = param_3 //:63 segment-end endpoint
puVar5[2] = 2 //:64 *** state = 2 == WS_NOTFINISHED ***
puVar5[7] = FUN_0040bb70(this=puVar5, arg=param_4) //:65-66 *** records (this=CAct_Climb, arg=path src) ***
```
> **The constant `2` at:64 is the CSubAction state field** = the same WalkResult enum (2 = walking).
>
> **CORRECTION (verifier,):** the Ghidra decompile renders this call as `FUN_0040bb70(param_4)`
> (one argument) at `00415e10:65`, and the raw `.text` at `0x415ed2..0x415ee7` confirms the real ABI:
> `mov ecx, esi` (ECX/`this` = `puVar5`, the **CAct_Climb object itself**) then `push [ebp+0x10]`
> (the stack arg = `param_4`, the **path source pointer**). So `FUN_0040bb70`'s `param_2` is the
> **path-source pointer, NOT the integer `2`.** The original claim "`FUN_0040bb70(path, 2)`" is wrong on
> both counts: `this` is the CAct_Climb (not the path), and the second arg is a pointer (not 2). The
> consequence is in §1.3 — the `param_2 == 2` z-copy branch is **never taken** on this call path (a heap
> pointer is never == 2). Verified by disassembly; see ## Verification notes.

CAct_Climb field layout (word indices into `puVar5`):
| word | byte | content |
|---|---|---|
| [0] | +0x00 | vftable (`CAct_Climb::vftable`) |
| [1] | +0x04 | refcount (CSubAction base) |
| [2] | +0x08 | **state** (WalkResult enum: 0/2/3/4/5) |
| [3] | +0x0c | owner ptr (gnome) — set by `FUN_0041fe30` |
| [4] | +0x10 | movable ptr — set by `FUN_0041fe30` |
| [5] | +0x14 | segment endpoint param_2 |
| [6] | +0x18 | segment endpoint param_3 |
| [7] | +0x1c | **waypoint-record array** (from `FUN_0040bb70`) |
| [8] | +0x20 | **current child `CAct_WalkAnim`** (filled by tick) |

### 1.2 `FUN_0040bb70` — mirror the path into 0x10-byte records (the ctor's record builder)
`int* __thiscall FUN_0040bb70(int self, int param_2)` (`0040bb70`, 789 B). **`self` IS the CAct_Climb
object** (`puVar5` from §1.1, confirmed by disassembly — see §1.1 CORRECTION), used here as the
record-build context: `self[+0x10]`=src array base (= the movable, set by `FUN_0041fe30`),
`self[+0x14]`=startIndex (= `param_2` endpoint), `self[+0x18]`=endIndex (= `param_3` endpoint),
`self[+0x1c]`=our output record array (= `puVar5[7]`, freed+rebuilt). `param_2` here = the **path-source
pointer** that was pushed at the call site (NOT the literal 2 — see §1.1/§1.3). The source records are
**8-byte int pairs** (`{ix, iy}` sub-cell ints); the output records are **0x10 bytes** of
`{float x, float y, float z, int flag}`.

```
if self[+0x1c]!=0: FUN_0040baf0(...); self[+0x1c]=0 //:34-36 free old records
hdr = alloc(0x2b) //:38 small header object
  hdr[0]=0; hdr[1]=0; hdr[3]=0xc47a0000 (= -1000.0f) //:43-45 sentinel z (was mis-stated -250000)
count = self[+0x18] - self[+0x14] //:48 number of waypoints
arr = alloc(0x2e); arr[0]=count; eh_vector_ctor(arr+1, stride 0x10, count, FUN_00405b50) //:50-58
hdr[0]=arr; hdr[1]=count; hdr[2]=count //:60-63
self[+0x1c] = hdr

for i = startIndex.. endIndex-1: //:65-113
    srcX = src[i*8 + 0]                                      // :82  int sub-cell X
    srcY = src[i*8 + 4]                                      // :83  int sub-cell Y
    fx   = (float)srcX * DAT_0073703c                        // :82  *** ·0.125  (sub-cell → world X) ***
    fy   = (float)srcY * DAT_0073703c                        // :83  *** ·0.125  (sub-cell → world Y) ***
    fz   = fy   (temp)                                       // :84  (placeholder; FUN_004238e0 overwrites)
    FUN_004238e0(&srcX)                                      // :85  *** compute the per-sub-cell DEPTH/z ***
    rec = arr_data + i*0x10
    rec.x = fx; rec.y = fy; rec.z = fz                       // :101-103
    rec.flag (rec+0xc) = 0                                   // :111  *** flag default 0 ***
```
> **Key:** each record's world (x,y) = sub-cell·**0.125** (`DAT_0073703c`), and `rec.flag` is **always
> set to 0** here (`:111` writes 0 unconditionally — there is NO path in `FUN_0040bb70` that writes flag
> 2). **CONSEQUENCE (verifier):** since `FUN_00419c60` dispatches flag-0 → `FUN_00418db0` and flag-2 →
> `FUN_00418850` (`:78-83`), and every climb record has flag 0, **the climb actually executes through
> `FUN_00418db0` (the §3.3 "floor-style" step), NOT `FUN_00418850` (§3.2).** This is not a contradiction:
> `FUN_00418db0` itself runs the same 3-phase `movable[+0x234]` mount/climb/dismount machine (§3.3) AND
> calls the quadrant climb-clip selector `FUN_00418c70` (`00418db0:157`). So the up/down/left/right climb
> loops (slots 0xa-0xd) and the in-leg mount slot 0x10 are all reached from the **flag-0** path. The
> flag-2 step `FUN_00418850` is for records that some OTHER builder tags with flag 2 (not the climb ctor);
> for the dwarf climb it is effectively unused. A port should drive climb legs through the flag-0/floor
> step logic, not a separate "wall step".

### 1.3 The `param_2 == 2` z-copy branch (`:115-140`) — DEAD on the climb-ctor path
The function body contains a trailing branch:
```
if param_2 == 2: //:115
    n = arr.count                                           // :117
    if n < 2: return arr                                    // :118-121  (nothing to fix)
    src_rec  = arr_data + (n-2)*0x10                        // :122-128  second-to-last record
    dst_rec  = arr_data + (n-1)*0x10                        // :130-137  last record
    dst_rec.z = src_rec.z      // *(dst+8) = *(src+8)        // :139  *** copy z of [n-2] onto [n] ***
```
> **CORRECTION (verifier,): this branch is NOT taken when `FUN_0040bb70` is called from the
> CAct_Climb ctor.** As shown in §1.1, the second argument passed by `FUN_00415e10` is `param_4` — a
> **heap pointer to the path source**, not the integer `2`. `param_2 == 2` therefore compares a pointer
> against 2 and is (essentially always) false, so the z-copy never runs for a climb segment built here.
> The branch must exist for OTHER callers of `FUN_0040bb70` that genuinely pass a move-type code of 2;
> for the climb executor it is dead code. **A faithful port of the climb ctor must NOT copy `[n-2].z`
> onto `[n].z`.** (If the original "depth stabilisation at the climb endpoint" behaviour is observed in
> game, it comes from `FUN_004238e0`'s overhang fix per-rung — §1.4 — not from this branch.)
>
> *Original intent (for the move-type-2 callers):* the LAST waypoint's **depth (z)** is overwritten with
> the previous waypoint's z. Records' z is the **depth axis** (computed by `FUN_004238e0`, §1.4), NOT
> elevation — elevation/up-down is tile-Y (`rec.y`).

### 1.4 `FUN_004238e0` — the per-sub-cell depth-with-overhang-fix used for `rec.z`
`char __fastcall FUN_004238e0(uint* p)` where `p = {subX, subY}` (`004238e0`). Returns the BDB height
nibble for the cell **one row up** (`subY-1`, i.e. the support row under a floor line), with an
overhang correction when that cell reads 15 (undug):
```
tile = FUN_00592f10(subX>>3, (subY-1)>>3) //:23
aux = tile.block_idx * 0x59 + CMap[+0x24][+0x10] //:26-31 (BDB aux, stride 0x59=89)
h = aux[((subY-1)&7)<<3 | (subX&7)] //:33 the nibble at the support sub-cell
if h == 0x0f: //:34 *** undug → pick the lower neighbour ***
    pick (subX±1, subY-1) whichever of get_hmap is smaller, // :35-41
    and (subX, subY-2 vs subY) whichever is smaller,        // :42-67  bias toward the open face
    h = FUN_004093f0(pickedX, pickedY)                      // :69  re-read the chosen neighbour
return h
```
> Uses `FUN_004093f0` = `get_hmap` leaf = the **per-sub-cell height** accessor the port must mirror
> (`GetHmapSub`). This is the depth the climb body is placed at for each rung.

---

## 2. CAct_Climb tick & step — `FUN_0040bf80` + `FUN_0040be90`

### 2.1 `FUN_0040bf80` — CAct_Climb::tick (per-waypoint child spawner)
`undefined1 __fastcall FUN_0040bf80(int self)` (`0040bf80`, 148 B).
```
if owner[+0x7b0] == 0: //:16 *** canclimb flag OFF ***
    self[+8] = 3;  return 0                                 // :17  *** state = 3 (WS_NOPASSAGE) ***
child = self[+0x20] //:20 current CAct_WalkAnim
if child == 0:
    child = FUN_0040be90(self)                              // :23  spawn first leg
    self[+0x20] = child
    if child == 0: return 0                                  // (no more legs)
cVar2 = child->vftable[+0xc] //:29 *** tick child (its CSubAction tick) ***
if cVar2 != 0: return 1 //:30 child returned "busy" (nonzero) → return 1
                                                             //      (CORRECTED: was "if done==0: return 1")
switch (child[+2]) { // child state //:34
    case 0: self[+8]=0; break                                // :35  WS_ERROR
    case 3: self[+8]=3; break                                // :40  WS_NOPASSAGE
    case 4: self[+8]=4; goto keep                            // :43  WS_TARGETREACHED (this leg)
    case 5: self[+8]=5; break                                // :46  WS_WALKEVENT
    default: goto keep                                       // :38
}
keep:
child->vftable[0](1) // delete child //:51-53
self[+0x20] = FUN_0040be90(self) //:54 *** spawn NEXT leg ***
if next == 0: return 0 // all legs consumed → done //:56-59
return uVar5 // 0 if done else 1
```
> The **fail-on-canclimb=0** gate (:16) is exactly the doc's "fail (state 3) if `obj[+0x7b0]==0`". The
> per-waypoint child is the `CAct_WalkAnim`. Result 4 ("arrived") on a leg does NOT terminate the whole
> climb — it just advances to the next leg; the climb finishes when `FUN_0040be90` returns 0 (no record
> left).

### 2.2 `FUN_0040be90` — CAct_Climb::makeNextLeg (spawn a WalkAnim toward record[next])
`undefined4 __fastcall FUN_0040be90(int self)` (`0040be90`, 231 B). It reads `recs = self[+0x1c]`;
`idx = recs[2] - 1` (the header's "remaining" cursor; `recs[2]` is decremented elsewhere); the target
record `rec = recs.data + idx*0x10`.
```
recs = self[+0x1c] //:20
idx = recs[2] - 1 //:21 current cursor
rec = recs.data + idx*0x10 //:27
movable = self[+4] //:29 (current body anchor)
dz = rec.z - body.z (movable+0x10) //:30
dx = rec.x - body.x (movable+0x0c) //:31
dy = rec.y - body.y (movable+0x14) //:32
if (dz*dz + dx*dx + dy*dy) >= DAT_0073702c: //:33 *** arrival eps² = 0.09 (0.30 world) ***
    if state ∉ {3,4,5}:                                     // :35  not already finished
        mem = alloc(0x51)                                   // :36  CAct_WalkAnim object (0x51 BYTES = 81)
        wa  = FUN_00417ba0(mem)                             // :41  *** CAct_WalkAnim ctor (mem kept in edi) ***
        FUN_00419130(this=wa, movable=self[+4],             // :51  *** init the WalkAnim leg ***
                     recs=self[+0x1c], 0, 1)                //      (Ghidra shows this=self[+0xc]; the raw
                                                            //       .text `mov ecx,edi` at 0x40bf68 proves
                                                            //       the real `this` is the new WalkAnim `wa`)
        return wa
else:
    self[+8] = 4                                            // :56  *** within eps → leg done (state 4) ***
return 0
```
> **Arrival metric** uses raw squared distance against `DAT_0073702c = 0.09` (= 0.3²). NOTE: this raw
> form weights all three axes equally; the ×0.5 depth weight (`DAT_0073708c`) is applied in the
> WalkAnim's own metrics (`FUN_00418db0`/`FUN_00418850`), not here. The WalkAnim is initialised by
> `FUN_00419130` with the **whole record array** (`self[+0x1c]`) and `param_4=0, param_5=1` (the "1" =
> the "is the last/standalone" flag → see `FUN_00419130:67-104`).

### 2.3 `FUN_00419130` — CAct_WalkAnim leg init
`FUN_00419130(self, movable, recs, param_4, param_5, param_6)` (`00419130`, 360 B). Sets:
```
self[+0x0c] = movable (refcounted) //:28-29
self[+0x04] = recs (the path array) //:44
self[+0x14] = param_4 (=0) //:67 start cursor
self[+0x18] = param_5 (=1) //:68 byte "useStart/standalone" flag
self[+0x1a] = param_6 //:70 byte "useEnd" flag
self[+0x08] = 2 //:71 *** state = 2 (WS_NOTFINISHED) ***
self[+0x1c] = 0; self[+0x1b]=0; self[+0x20]=0; self[+0x24]=0; self[+0x28]=0 //:72-76 counters
// orientation seed: compare current model facing-angle vs the standard pose
animIdx = movable[+0xdc] //:77 animation-frame index
slot0 = animset[movable][slot 0].angleStart ([+0x38]) //:87
slot0e = animset[movable][slot 0].angleEnd ([+0x3c]) //:95
self[+0x19] = (FUN_00422ee0(7)==slot0 && FUN_00422fe0(7)==slot0e) ? 0: 1 //:96-104 "needs turn" flag
```
> `self[+0x19]` is the "turn allowed" byte that gates the in-place turn clips later (`FUN_00418280:44`,
> `FUN_00418730`). `FUN_00422ee0(7)`/`FUN_00422fe0(7)` read slot 7 (turn_left_180) angle pair as the
> orientation reference.

---

## 3. CAct_WalkAnim — the per-leg mover (`FUN_00419c60`) and clip selection

`FUN_00417ba0` is just the **ctor** (`00417ba0`): zeroes fields, sets `*p = CAct_WalkAnim::vftable`,
clears `p[3..4]` and `p[0x10..0x16]`. The behaviour is in the **tick** `FUN_00419c60`.

### 3.1 `FUN_00419c60` — CAct_WalkAnim::tick
`undefined4 __fastcall FUN_00419c60(int self)` (`00419c60`, 1067 B). Body state layout (byte offsets):
| off | field |
|---|---|
| +0x04 | movable (body anchor) |
| +0x08 | state (0/3/4/5) |
| +0x0c | owner (gnome) |
| +0x14 | recs (path array) |
| +0x1c | cursor (current record index) |
| +0x20 | walk-step frame counter |
| +0x2c | **current animset slot (clip id)**, init 0xffffffff |
| +0x3c | per-frame arc-length budget remaining (init 0x3dcccccd = **0.1f**) |
| +0x40..0x48 | **current body position (x,y,z)** |
| +0x4c..0x54 | **current body forward/facing vector** |
| +0x58 | mount/dismount sub-frame counter |

```
pos = self+0x40 //:36
pos.xyz = movable[+0x0c/0x10/0x14] //:39-41 snap to body
fwd = movable[+0x40/0x44/0x48] //:43-45 snap facing
self[+0x3c] = 0x3dcccccd (=0.1f) //:46 per-frame budget
if FUN_004192a0(self) == 0: // *** NOT yet at the leg end *** //:47
    if (DAT_00772400[+0x6c] % 0x14 == 0x13) || (self[+0x20]==0):   // :49  periodic gait re-eval
        ... FUN_004234a0(owner)  // re-pick gait bank (0x798) by tiredness   // :55
    rec   = recs.data[min(cursor, count-1)]                // :63-74
    flag  = rec.flag (rec+0xc)                              // :76
    if flag == 0: FUN_00418db0(self)                        // :78-79  *** FLOOR-style step ***
    if flag == 2: FUN_00418850(self)                        // :82-83  *** WALL-style step ***
    // clip → animator: if slot changed, play it
    if self[+0x2c] != -1:                                   // :86
        a0 = FUN_00422ee0(slot)  ; a1 = FUN_00422fe0(slot)  // (clip db idx + orient)
        orientMode = (slot ∈ {6,8,0xd,0x10,0x11,0x14,0x15}) ? 1 : 2   // :92-95
        if (clip or orient changed): FUN_005cdae0(a0,a1,0,orientMode)  // :126  *** SET the clip ***
    res = FUN_00419840(pos, ...)   // *** advance body by clip root motion ***   // :132
    switch(res){ 0: stop/snap; 1/3: state=3; 2: re-snap pos+fwd, state=3 }       // :133-151
    if FUN_004192a0(self)==0:   // still moving
        ... walk-timer callback check (owner[+0x7c0]) at distance owner[+0x7c4]   // :154-176
            (·_DAT_007370a8=0.65 on floor) → if within, state=5 (WS_WALKEVENT)
        return 1   // busy
    else: snap to last waypoint                             // :179-185
else: // *** at the leg end *** //:187
    snap pos to last record                                 // :188-200
if last.z != sentinel: set facing from movable[+0x40/0x48] //:202-206
self[+8] = 4 // *** WS_TARGETREACHED *** //:207
return 0
```
> So: the WalkAnim re-snaps to the body each tick, runs a **step function** that (a) writes the animset
> **slot** into `self[+0x2c]` and (b) advances the cursor, then `FUN_005cdae0` actually plays that clip
> and `FUN_00419840` moves the body by the clip's **root-motion translation**. The leg ends (state 4)
> when `FUN_004192a0` says we're at the end.

### 3.2 `FUN_00418850` — the WALL-style step (mount / climb-lateral / dismount clip selection)
`void __fastcall FUN_00418850(int self)` (`00418850`, 1050 B). Driven by a 3-phase state machine on
**`movable[+0x234]`** (the "wall-walk phase": 2 = mounting, 0 = climbing, 1 = dismounting):
```
if movable[+0x234] == 2: // *** MOUNT phase *** //:26
    if FUN_00422ee0(0x11) == -1: movable[+0x234] = 0         // :28  no wall_to_ground clip → skip
    else:
        self[+0x2c] = 0x11                                   // :32  *** slot 0x11 = wall_to_ground ***  (see note)
        if self[+0x58] < FUN_00423180(0x11): self[+0x58]++   // :34  hold for the clip's frame count
        else: movable[+0x234] = 0; self[+0x58] = 0           // :38  advance to climb phase
if movable[+0x234] == 0: // *** CLIMB phase *** //:43
    if FUN_004230d0(0x14) == -1: movable[+0x234] = 1         // :44  no slot-0x14 clip → skip
    else:
        self[+0x2c] = 0x14                                   // :49  *** slot 0x14 ***
        if self[+0x58] < FUN_00423180(0x14): self[+0x58]++   // :51  hold
        else: movable[+0x234] = 1; self[+0x58] = 0           // :55  advance to dismount phase
if movable[+0x234] == 1: // *** DISMOUNT / move phase *** //:60
    budget = self[+0x3c]                                     // :61
    while budget > 0:                                        // :63
        rec = recs.data[cursor]                              // :64-74
        d   = rec - pos                                      // :79-81
        if |d|² >= DAT_0073701c (=0.0625) or last-rec:       // :82  *** lateral step threshold 0.0625 ***
            heading = atan2-ish of (rec.depth - pos.depth)   // :84-92  via FUN_00410940 / FUN_00423a90
            if |Δheading| >= DAT_007370d0 (=π/4) or self[+0x1b]: // :93  *** 45° turn gate ***
                self[+0x19]=1; FUN_00418730(&rec)            // :96  do an in-place turn step
            else: FUN_00418280(&rec)                         // :99  blend / advance step
        else: cursor++                                       // :103
    ... at end-of-path, possibly play slot 0x15 (dismount tail) // :148-166
```
> The **mount** clip is slot **0x10 = ground_to_wall (`stand_zu_kletterstand`)**, the **dismount**
> slot **0x11 = wall_to_ground (`kletterstand_zu_stand`)**. *(In `FUN_00418850` the phase-2 branch plays
> slot 0x11 and the phase-1 tail plays slot 0x15 — these are the symmetrical transition slots; the exact
> 0x14/0x15 names are the ladder/extra transition slots. The clip TABLE order is fixed by name-hash in
> `set_class_animset`, see §4.)* The actual UP/DOWN/LEFT/RIGHT climb clips are chosen by **direction**
> inside `FUN_00418c70` (§3.4), reached via the turn/blend step functions.

### 3.3 `FUN_00418db0` — the FLOOR-style step (walk_start/loop/stop + root-motion advance)
`void __fastcall FUN_00418db0(int self)` (`00418db0`, 883 B). Same 3-phase `movable[+0x234]` machine,
but with the WALK transition slots:
```
if movable[+0x234]==1: play slot 0x15, hold, →0 //:35-50
if movable[+0x234]==0: play slot 0x10, hold, →2 //:52-67
if movable[+0x234]==2: // *** the actual root-motion advance *** //:69
    base = body.xyz                                        // :70-73
    // sum the 4 root-motion translation slots 9,10,11,12 and scale
    sum = rootmotion(slot 9)+slot10+slot11+slot12          // :74-81  FUN_00423270(9..12)
    arc = (sum) * DAT_00737058 (=0.25)                     // :82  *** per-frame world advance ***
    budget = self[+0x3c] * arc                             // :83
    while budget > 0:                                       // :85
        rec = recs.data[cursor]; segLen = |rec - pos|       // :88-105 (depth ×0.5 weighted, :103)
        if budget < segLen:                                 // :107  partial step inside this segment
            t = budget/segLen; pos = lerp(pos, rec, t)      // :119-125
            break
        budget -= segLen; pos = rec; cursor++               // :129-141
    self[+0x3c] = budget / arc                              // :149  leftover budget
    fwd = FUN_005e6060(...)                                 // :153  recompute forward from body bones
    FUN_00418c70(&base, self+0x40)                          // :157  *** pick the FLOOR/CLIMB dir clip ***
```
> **Speed is purely root-motion:** `arc = (rootmotion slots 9+10+11+12) × 0.25` (`DAT_00737058`). The
> depth axis is weighted ×0.5 (`DAT_0073708c`,:103) in the segment-length metric. **No fixed climb
> speed, no 0.47 factor.** The clip for this advance is chosen by `FUN_00418c70` from the *heading to the
> next waypoint*.

### 3.4 `FUN_00418c70` — THE DIRECTION → CLIMB-CLIP SELECTOR (the answer to "how the clip is chosen")
`void __thiscall FUN_00418c70(int self, int from, int to)` (`00418c70`, 308 B). It computes the heading
of the leg in the **(x, depth) plane** and bins it into 4 quadrants → an animset slot:
```
dz = to[+8] - from[+8] // depth-axis delta (rec.z is the DEPTH component here):13
ang = atan2(dz, dx) via FUN_0040b700 + FUN_006f52b0:14-17
if ang < 0: ang += 2π (DAT_007374f8=6.2831855):18-19
q = (ang / 2π) * 4.0 (DAT_007374a0=4.0) // map full turn → 0..4 quadrants:21
if 0.0 <= q < 1.0 (DAT_00737138): self[+0x2c] = 0xb // *** slot 11 = climb_DOWN (kletter_runter) ***:22→32-33→36 (else path)
   -- exact branch tree (:22-37):
      if q in [0,1):           slot = 0xb  (climb_down)        // :35-36
      elif q in [1,2):         slot = 0xa  (climb_up)          // :31-32
      elif q in [2,3):         slot = 0xc  (climb_right)       // :27-28
      else (q in [3,4)):       slot = 9    (climb_standard)    // :24-25
if cursor >= recs.count: self[+0x2c] = 1 // past end → slot 1 (walk_start, settle):38-40
```
> **The climb clip is the 4-way quadrant of the leg heading in the (lateral-X, depth) plane.** Slots:
> **9 = climb_standard (`kletterstand`), 10 = climb_up (`kletter_hoch_loop`), 11 = climb_down
> (`kletter_runter_loop`), 12 = climb_right (`kletter_rechts_loop`)**, with **13 = climb_left
> (`kletter_links_loop`)** selected on the lateral branch of the *floor* step (`FUN_00418280:102-104`).
> The boundaries are the cardinal axes (q=0,1,2,3 ↔ 0°,90°,180°,270°). (Note: the *physical* up/down axis
> is tile-Y, but the clip selector here bins on the (x, **depth**) heading because the model's climb anims
> are authored against the wall face whose normal is the depth axis — the body climbs "into/out of" the
> wall depth. This matches the demonstrated routes: kletter_hoch when moving up the face, kletter_runter
> when descending.)

### 3.5 `FUN_00418280` / `FUN_00418730` — turn & lateral-climb clip blending
`FUN_00418280` (the blend step, `00418280:792B`) selects the turn/loop slots:
```
if self[+0x19] (turn allowed) and frame < FUN_00423180(6): slot 6 (turn_right_90):86-91
elif self[+0x18] (useStart) and arcLeft <= FUN_00423180(8): slot 8 (turn_right_180):93-100
elif self[+0x24]>0 and frame < count(0xd)+self[+0x24]: slot 0xd (climb_LEFT):101-106
else: slot 7 (turn_left_180):110
   then if FUN_00417de0 (greet check) fires: slot 0xd (climb_left):111-116
```
`FUN_00418730` (the in-place-turn step, `00418730:282B`) sets slot **3 or 5** (`3-(dir!=1)` → 2/3 =
turn_left_90/right_90; `5-(dir!=1)` → 4/5) and advances facing by `±(turnSpeed)` with
`self[+0x50]` (yaw) clamped by `DAT_00736fb8 (0.001)`/`DAT_00737034 (0.1)`. Turn direction sign is in
`self[+0x30] (±1)` computed by `FUN_004185a0` (`±1 = sign of the heading delta`,:35), and the
half/full-turn flag in `self[+0x38]` (`>π·threshold` ⇒ 180-turn).

### 3.6 `FUN_00419840` — the root-motion integrator (per-frame translation)
`int __thiscall FUN_00419840(int self, float* posInOut)` (`00419840`, 1055 B). Reads the clip's
root-motion offset via `FUN_00419350` and applies it (depth axis ×0.5 = `DAT_0073708c`,:49). It then
sweeps the OTHER movables in the scene for collision/overlap (`FUN_004194d0` per neighbour,:108) and
returns `0/1/2/3` (clear / blocked-stop / blocked-reposition / collide). `FUN_00419350`
(`00419350:381B`) extracts the clip translation: `motion = clip[+0x44..+0x58] × DAT_00736fcc (=0.005)`
from the active anim frame (`movable+0x39c/0x3a0`). **This is the sole source of per-frame displacement —
walk and climb differ only because the clips have different root translation.**

### 3.7 `FUN_004234a0` — gait bank selection (which animset row)
`void __fastcall FUN_004234a0(int owner)` (`004234a0`):
```
if owner[+0x7e8] == 2: owner[+0x798] = 1 // *** RUN bank ***
else: owner[+0x798] = 0 // *** WALK/STANDARD bank ***
                       if owner[+0x7e8]==0: owner[+0x7be]=1
```
`owner[+0x798]` is the gait index used by every slot resolver to pick the animset bank.

---

## 4. The animset slot table — slot index → clip name (the "clip table")

The slot resolvers all index `obj+8 + gait*0x1e4 + slot*0x14` (each bank 0x1e4=484 B, 0x18=24 slots ×
0x14=20 B), with a fallback default bank at `obj+0x5b8` when the per-gait entry's `[+4]==-1`:

| resolver | returns | offset |
|---|---|---|
| `FUN_00422ee0(slot)` | **clip db-index** (or default-bank fallback) | slot+4 / 0x5b8+slot·0x14 |
| `FUN_00422fe0(slot)` | clip **orientation/angle** | slot+8 |
| `FUN_004230d0(slot)` | clip db-index (no fallback) | slot+4 |
| `FUN_00423180(slot)` | clip **frame count** | slot+0x10 |
| `FUN_00423270(slot)` | **pointer** to slot root-motion fields | slot (base) |

The slot→clip binding is authored in Tcl (`set_class_animset` → native `FUN_00616d30`). The standard and
run banks for the dwarf (`z_anims.tcl:1280` / `:1312`) define exactly the slots the executor uses:

| slot (dec / hex) | animset name | STANDARD clip (gait 0) | RUN clip (gait 1) |
|---|---|---|---|
| 0 / 0x00 | standard | mann.standard | mann.standard |
| 1 / 0x01 | walk_start | mann.gehen_start | mann.laufen_schnell_start |
| 2 / 0x02 | walk_loop | mann.gehen_loop | mann.laufen_schnell_loop |
| 3 / 0x03 | walk_loop_wave | mann.gehen_gruessen | mann.laufen_schnell_loop |
| 4 / 0x04 | walk_stop | mann.gehen_end | mann.laufen_schnell_end |
| 5 / 0x05 | turn_left_90 | mann.drehen_links | mann.drehen_links |
| 6 / 0x06 | turn_right_90 | mann.drehen_rechts | mann.drehen_rechts |
| 7 / 0x07 | turn_left_180 | mann.drehen_ganz_links | mann.drehen_ganz_links |
| 8 / 0x08 | turn_right_180 | mann.drehen_ganz | mann.drehen_ganz |
| 9 / 0x09 | **climb_standard** | **mann.kletterstand** | mann.kletterstand |
| 10 / 0x0a | **climb_up** | **mann.kletter_hoch_loop** | mann.kletter_hoch_schnell |
| 11 / 0x0b | **climb_down** | **mann.kletter_runter_loop** | mann.kletter_runter_schnell |
| 12 / 0x0c | **climb_right** | **mann.kletter_rechts_loop** | mann.kletter_rechts_schnell |
| 13 / 0x0d | **climb_left** | **mann.kletter_links_loop** | mann.kletter_links_schnell |
| 14 / 0x0e | ladder_climb_up | mann.leiter_hoch_loop | mann.leiter_hoch_loop |
| 15 / 0x0f | ladder_climb_down | mann.leiter_runter_loop | mann.leiter_runter_loop |
| 16 / 0x10 | **ground_to_wall (MOUNT)** | **mann.stand_zu_kletterstand** | mann.stand_zu_kletterstand |
| 17 / 0x11 | **wall_to_ground (DISMOUNT)** | **mann.kletterstand_zu_stand** | mann.kletterstand_zu_stand |
| 18 / 0x12 | ladder_to_ground | mann.leiter_zu_boden | mann.leiter_zu_boden |

> The animset list is ordered exactly by these slot indices in `z_anims.tcl` (climb block at lines
> 1294-1303). Slots **0x14 (20) / 0x15 (21)** referenced in `FUN_00418850` are beyond the dwarf's
> 19-entry list, so they resolve to **-1 → the default-bank fallback** (`obj+0x5b8`) and the phase simply
> skips (the `== -1` branch advances `movable[+0x234]`). In practice the dwarf's mount/dismount run on
> slots **0x10 / 0x11** (ground_to_wall / wall_to_ground) and the up/down/left/right climb loops on
> **0x0a–0x0d**, with **0x09 (kletterstand)** as the in-between idle/standard climb pose.

---

## 5. The wall→floor handoff at the end of a descent

There is **no separate "place the dwarf on the floor cell" code** — the handoff is implicit and falls out
of three mechanisms:

1. **Depth stabilisation (ctor):** `FUN_0040bb70`'s `param_2==2` branch (§1.3) copies the second-to-last
   record's depth (z) onto the final record, so the last rung sits at a consistent depth (no snap).
2. **Per-rung depth (ctor):** every record's z = `FUN_004238e0` (§1.4) = the per-sub-cell support height
   one row up, with the overhang correction — so the bottom rung's depth already equals the floor cell's
   support depth.
3. **Dismount clip + state (executor):** the LAST leg's WalkAnim, running the WALL step `FUN_00418850`,
   reaches its dismount phase (`movable[+0x234]==1`) and plays the **wall_to_ground / dismount slot**;
   when the WalkAnim's `FUN_004192a0` arrival predicate fires (within eps 0.09), it writes **state 4
   (WS_TARGETREACHED)** and re-snaps the body to the last record (`FUN_00419c60:188-200`,202-206). Control
   returns to MetaWalk's segment loop, which classifies the *next* (floor) segment as FLOOR and dispatches
   a fresh `CAct_WalkFloor` whose first waypoint already lies on the floor line.

> So the body lands on the floor at the record (x,y) with z = the support-cell depth, the dismount clip
> plays, and the next FLOOR sub-action takes over. The climb executor itself never explicitly "lifts the
> dwarf onto the floor" — the floor cell's depth is baked into the last climb record, and the FLOOR mover
> resumes from there. The doc's §5.2 "climb-end Z up→+14.5 (DAT_00737564) / down→+12.5 (DAT_00737548)" are
> the **depth clamps** applied to the climb endpoint elsewhere in the climb-grid builder (those two floats
> ARE 14.5 / 12.5, verified); they bound the support-depth so the dismount lands in the valid 7..14.5 range.

---

## 6. Constants (extracted from `original/Diggles.exe`.rdata, hex VA + float)

| DAT (VA) | value | role in climb-exec |
|---|---|---|
| DAT_0073703c | **0.125** (1/8) | sub-cell int → world (record x,y in `FUN_0040bb70`) |
| DAT_0073702c | **0.09** (=0.3²) | arrival eps² (`FUN_0040be90:33`, `FUN_00417de0:138`) — NOT used by `FUN_004192a0` (that is a cursor>=count predicate) |
| DAT_0073701c | **0.0625** (=0.25²) | lateral-step advance threshold² (`FUN_00418850:82`) |
| DAT_00737058 | **0.25** | root-motion → per-frame world advance (`FUN_00418db0:82`) |
| DAT_00736fcc | **0.005** | clip root-translation scale (`FUN_00419350:42-44,62-64`) |
| DAT_0073708c | **0.5** | depth-axis weight in distance metrics (`FUN_00418db0:103`, `FUN_00419840:49`) |
| DAT_007370d0 | **0.7853982** (π/4) | turn-clip threshold 45° (`FUN_00418850:93`) |
| DAT_007371e0 | **1.5707964** (π/2) | facing offset 90° (`FUN_00418730:19`, `FUN_00418280:26`) |
| DAT_007374a0 | **4.0** | quadrant scale: full turn → 0..4 (`FUN_00418c70:21`) |
| DAT_007374f8 | **6.2831855** (2π) | full turn (`FUN_00418c70:19`) |
| DAT_00737138 | **1.0** | quadrant boundary q<1 (`FUN_00418c70:22`) |
| DAT_00737330 | **2.0** | quadrant boundary q<2 (`FUN_00418c70:23`) |
| DAT_00737428 | **3.0** | quadrant boundary q<3 (`FUN_00418c70:24`) |
| DAT_00737790 | **-3.1415927** (−π) | angle-normalise lower bound (`FUN_004185a0:25,43`) |
| DAT_00737440 | **3.1415927** (π) | angle-normalise upper bound (`FUN_004185a0:27,45`) |
| DAT_00737034 | **0.1** | per-frame turn rate (`FUN_00418730`, `FUN_00417c50:72`) |
| DAT_00737014 | **0.051** | facing-clamp small angle (`FUN_00418280:31`) |
| DAT_00736fe0 | **0.01** | turn-end snap threshold (`FUN_00418850:114,144`) |
| DAT_00736fb8 | **0.001** | yaw min-step (`FUN_00418730:28`, `FUN_00417c50:67`) |
| DAT_00736fe4 | **0.01** | budget decrement on turn (`FUN_00418730:23`) |
| _DAT_007370a8 | **0.65** | floor walk-timer callback distance scale (`FUN_00419c60:164`) |
| DAT_00737564 | **14.5** | climb-end depth clamp (up) — climb-grid endpoint |
| DAT_00737548 | **12.5** | climb-end depth clamp (down) — climb-grid endpoint |
| (init) 0x3dcccccd | **0.1** | per-frame arc budget seed (`FUN_00419c60:46`) |
| (init) 0xc47a0000 | **-1000.0** | record "no-z" sentinel (`FUN_0040bb70:45`) |

> `DAT_00737310 = 3.376e12` and `DAT_00737360 = 2.38e38` are float→int rounding "magic add" sentinels
> (NaN/truncation tricks), not physical parameters.

---

## 7. Tcl ↔ native contract (climb-relevant)

- **`-canclimb <bool>`** (`action … walk`) → `owner[+0x7b0]`. `CAct_Climb::tick` (`FUN_0040bf80:16`)
  hard-fails a wall segment with **state 3 (WS_NOPASSAGE)** when this is 0. Box-carriers walk with
  `-canclimb 0` (`z_dignwalk.tcl:613,837`), so they NEVER climb — they re-path or report no-passage.
- **`get_walkresult`** reads the propagated CSubAction state (gnome+0x248): a climb in progress = 2, a
  no-climb wall = 3, arrival = 4 — Tcl branches only on `==2` (busy) and `==4` (arrived).
- **`get_gnomeposition`** = 1 while on a wall sub-cell (the wall-walk phase `movable[+0x234]` active);
  Tcl `rotate_towards`/`walk_dummy` gate on it (`w_procs.tcl`, `z_dignwalk.tcl`).
- **gait** (`owner[+0x798]` via `FUN_004234a0`) selects the STANDARD vs RUN animset bank → the climb_up
  clip is `kletter_hoch_loop` (walk) or `kletter_hoch_schnell` (run); speed differs only via root motion.
- The clip NAMES are entirely supplied by the unchanged scripts (`z_anims.tcl set_class_animset`); the
  engine references **slot indices**, never names, at runtime.

---

## 8. PORT GAP ANALYSIS — current `ClimbAction.cs` / `WalkClimbAction.cs` vs the original

Both `Assets/Diggles/Runtime/Tcl/Engine/ClimbAction.cs` and `Assets/Diggles/Runtime/Engine/Actions/
WalkClimbAction.cs` implement the **same invented choreography** and diverge from the original on every
axis:

1. **Invented move-kind enum.** `enum Kind { Walk, Mount, CrawlLeft, CrawlRight, ClimbUp, ClimbDown,
   Dismount, Jump }` with a hand-built `Build` that synthesises mount→crawl→descend→dismount legs
   (`ClimbAction.cs:28,86-136`). **Original:** records are uniform (`FUN_0040bb70` sets flag 0); the clip
   is chosen *per tick* by the **heading quadrant** (`FUN_00418c70`) and the **wall-walk phase machine**
   (`movable[+0x234]`), not by pre-tagged leg kinds. There is no "Jump" and no synthesised lateral crawl.
2. **Hardcoded speed `height*BodySpeedFactor*0.47`** (`ClimbAction.cs:264-274`, `ClimbFactor=0.47`).
   **Original:** speed is `(rootmotion slots 9+10+11+12) × 0.25` (`FUN_00418db0:82`) — purely per-clip
   root motion; run vs walk differs only because the clips differ. The 0.47 is fabricated.
3. **Arrival eps 0.12** (`ClimbAction.cs:75`). **Original:** **0.09** (= 0.3²), `DAT_0073702c`
   (`FUN_0040be90:33`). The lateral-step threshold is a separate **0.0625** (`FUN_00418850:82`).
4. **Faces the wall normal** (`ClimbAction.cs:184,188 Face(-leg.WallNormal)`). **Original:** facing is
   driven by the heading/turn math (`FUN_004185a0`/`FUN_00418730`) and the clip orientation pair
   (`FUN_00422ee0/fe0`), with a π/4 turn gate (`DAT_007370d0`) — not the geometric wall normal.
5. **Z stacking via `mountRise`/wall-normal standoff** (`ClimbAction.cs:99-133`). **Original:** the depth
   (z) of each rung is the **per-sub-cell support height** (`FUN_004238e0`/`get_hmap`), and the last
   record's z is copied from the previous (`FUN_0040bb70:139`). No `mountRise`, no `_climbStandoff`.
6. **Clip set by leg-kind, looped continuously** (`PlayLeg`/`PlayFirst`). **Original:** the clip is the
   animset **slot** written into `self[+0x2c]` (`FUN_00418c70`/`FUN_00418850`) and pushed by
   `FUN_005cdae0` only when slot/orientation changes; mount/dismount are **frame-count-held**
   (`FUN_00423180`), and the up/down/left/right loops are slots 0x0a-0x0d by quadrant.
7. **`-canclimb` not enforced as state 3.** The port's climb routing doesn't fail-with-NOPASSAGE when
   `canclimb==0` — it should mirror `FUN_0040bf80:16-17`.

**To build (faithful executor):**
- A `CAct_Climb` equivalent: take the wall-segment waypoints (already sub-cell), build records with
  world (x,y)=sub·0.125 and z=`GetHmapSub(supportRow)` with the overhang fix, copy [n-2].z→[n].z.
- A `CAct_WalkAnim` equivalent that: per tick re-snaps to the body, runs the phase machine
  (`movable[+0x234]`: mount→climb→dismount), picks the slot by heading quadrant (`FUN_00418c70`),
  advances by clip root motion × 0.25, weights depth ×0.5, and ends the leg within eps 0.09.
- Reuse `DigglesAnimController.PlayClip` keyed by the **slot→clip name** table (§4) resolved through the
  gait bank, and `LocomotionSpeed`/root-motion (the project's per-clip root-motion plumbing) instead of
  `height*0.47`.
- Enforce `-canclimb 0` → immediate WS_NOPASSAGE for box-carriers.

---

## 9. UNRESOLVED

1. **`movable[+0x234]` exact value semantics.** Reconstructed as a 3-phase wall-walk state (2=mount,
   0=climb-advance, 1=dismount/tail) from `FUN_00418850`/`FUN_00418db0`. The exact entry point that sets
   it to 2 on a fresh wall segment (vs 1 for plain floor) is not in the read functions — likely the
   surface classifier / segment dispatch in MetaWalk sets it when a WALL segment begins. Confirm by
   reading the MetaWalk dispatcher writes to `+0x234`.
2. **Slots 0x14 / 0x15 in `FUN_00418850`.** For the dwarf these fall past the 19-entry animset list and
   resolve to -1 (skip). For models with a longer list (other characters) they'd bind to extra ladder/
   transition clips. Harmless for dwarves; note for non-dwarf climbers.
3. **`FUN_005cdae0` exact signature** (the clip-play call at `FUN_00419c60:126`) — read as
   `(clipDbIdx, orient, 0, orientMode)`; the precise meaning of `orientMode` 1 vs 2 (mirror/blend) is
   inferred from the slot set `{6,8,0xd,0x10,0x11,0x14,0x15}` (turn/transition clips get mode 1). Confirm
   by reading `FUN_005cdae0`.
4. **The depth-vs-elevation framing of `FUN_00418c70`.** The quadrant is binned on (x, rec.z=depth);
   physically the climb up/down is tile-Y. The clips (kletter_hoch/runter) are authored to read correct
   either way, but a port must verify the heading axis maps to the visible up/down on real pit routes.
5. **`FUN_00417de0` greet interaction** inside the walk step (`FUN_00418280:111`) raises `evt_zwerg_greet`
   when two dwarves pass — cosmetic, not climb-core, but it can hijack the clip to slot 0xd mid-leg.

---

## Verification notes (adversarial pass,)

All 12 load-bearing claims were re-checked against the primary decompile and, where Ghidra collapsed the
ABI, against the raw `.text` bytes of `original/Diggles.exe` (imgbase 0x400000). All 23 float constants in
§6 were re-extracted from the PE (`.rdata` raw 0x313e00) and **match exactly** (0.125, 0.09, 0.0625, 0.25,
0.005, 0.5, π/4, π/2, 4.0, 2π, 1.0/2.0/3.0, ±π, 0.1, 0.051, 0.01, 0.001, 0.65, 14.5, 12.5). Findings:

**CONFIRMED (unchanged):** claims 2 (record build: x,y=sub·0.125, flag default 0), 3 (`FUN_004238e0`
support-row nibble + overhang fix, `004238e0:23-69`), 4 (`FUN_0040bf80` canclimb→state 3 at `:16-17`,
child-state-4 advance), 5 (`FUN_0040be90` eps²=0.09 spawn/`FUN_00419130` init), 6 (`FUN_00419c60`
flag-dispatch `:78-83`, `FUN_005cdae0` play `:126`, `FUN_00419840` advance `:132`; root motion = slots
9+10+11+12 ×0.25 at `00418db0:74-82`, depth ×0.5 at `:103` and `00419840:49`), 7 (no fixed climb speed),
**8 (the quadrant clip selector — verified branch-exact: q∈[0,1)→0xb, [1,2)→0xa, [2,3)→0xc, [3,4)→9 at
`00418c70:22-37`, scale ×4.0/2π)**, 9 (3-phase `movable[+0x234]` machine; `FUN_00418850` plays 0x11/0x14/
0x15 held for `FUN_00423180` counts), 10 (resolver math `obj+8 + gait·0x1e4 + slot·0x14`, +4/+8/+0x10
fields, 0x5b8 fallback; `FUN_004234a0` gait from `+0x7e8`), 11 (Tcl slot table — verified line-by-line
against `z_anims.tcl:1280-1306` standard bank and `:1312-1336` run bank), 12 (port-gap citations in
`ClimbAction.cs`: enum `:28`, `arriveEps=0.12f` `:75`, `ClimbFactor=0.47f` `:267-273`, `Face(-WallNormal)`
`:184/188`, `mountRise`/`_climbStandoff` `:99-133` — all present as stated).

**CORRECTED:**
1. **Claim 1 — the `FUN_0040bb70` arguments (the most important correction).** The original claimed the
   ctor calls `FUN_0040bb70(path, 2)`, enabling the `param_2==2` z-copy branch. **Wrong.** Ghidra renders
   `FUN_0040bb70(param_4)` (one arg) at `00415e10:65`; the raw `.text` at `0x415ed2..0x415ee7`
   (`mov ecx,esi` / `push [ebp+0x10]` / `call 0x40bb70`) shows `this` = **the CAct_Climb object** and the
   single stack arg = **`param_4` (the path-source pointer)**. Therefore:
   - `FUN_0040bb70`'s `self` is the CAct_Climb (§1.2 relabeled accordingly), and its `param_2` is a heap
     pointer, **not the integer 2**.
   - The **`param_2 == 2` z-copy branch (`:115-139`) is dead on the climb-ctor path** (§1.3 rewritten). A
     faithful port must **NOT** copy `[n-2].z → [n].z` for climb segments. (The branch is live only for
     other callers that genuinely pass move-type 2.)
2. **§1.2 sentinel value.** `hdr[3] = 0xc47a0000` was annotated "-250000.0f"; the actual IEEE-754 value is
   **-1000.0** (the §6 table already had -1000.0; §1.2 is now consistent).
3. **§2.1 child-done check inverted.** Was "`if done==0: return 1`". Actual `0040bf80:30` is
   `if (cVar2 != 0) return 1;` — i.e. return "busy" (1) when the child tick returns **nonzero**. Fixed.
4. **`DAT_0073702c` users.** §6 listed `FUN_004192a0` as a 0.09 consumer. It is not — `FUN_004192a0` is a
   pure **cursor≥count** leg-END predicate with no float-distance test (verified: grep shows `DAT_0073702c`
   only in `FUN_0040be90:33` and `FUN_00417de0:138`). The call-graph line and the constants row are fixed.
5. **§2.2 `FUN_00419130` `this`.** Ghidra showed `this=self[+0xc]`; the raw `.text` `mov ecx,edi` at
   `0x40bf68` proves the real `this` is the freshly-allocated `CAct_WalkAnim` (edi). Noted inline. Also
   `alloc(0x51)` is **0x51 BYTES (81)**, not "0x51 words".

**NEW STRUCTURAL FINDING (added to §1.2 "Key"):** because every climb record has flag 0 (`0040bb70:111`)
and `FUN_00419c60` routes flag 0 → `FUN_00418db0` (`:78-79`), **the dwarf climb executes through the
flag-0 "floor-style" step `FUN_00418db0`, which itself runs the `movable[+0x234]` mount/climb/dismount
machine and calls the quadrant clip selector `FUN_00418c70` (`00418db0:157`).** The flag-2 step
`FUN_00418850` (§3.2) is therefore NOT exercised by climb records from this ctor — it serves records that
some other builder tags with flag 2. The clip-selection claims (8/9) still hold (both step functions share
the phase machine + selector), but the port should drive climb legs through the flag-0 path rather than a
separate "wall step".

**Ghidra-mangling notes:** `FUN_00418c70` loses the x-delta (it is formed inside `FUN_0040b700` from a
packed {dx,dz} vector; only the z-delta `param_3+8 - param_2+8` is visible at `00418c70:13`), but the
binning tree is fully intact and verified. `FUN_0040b700`/`FUN_006f52b0` are the unit-vector→atan2 helpers;
their internals were not needed to confirm the quadrant mapping.
