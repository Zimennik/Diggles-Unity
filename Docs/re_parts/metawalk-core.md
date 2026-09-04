> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# metawalk-core — `CAct_MetaWalk` complete RE

> Authoritative line-by-line RE of the **MetaWalk maintainer**: the action that takes a freshly
> computed `CPath` polyline and drives a gnome along it by spawning one *sub-action* per
> contiguous same-surface run (FLOOR→WalkFloor, WALL→Climb, WATER→WalkObject), advancing a cursor,
> and propagating the sub-action result up to the Tcl `get_walkresult` enum.
>
> Sources: `re/decompiled/Diggles/_global/*.c`. Every claim cites `FUN_addr:line`. Where Ghidra
> mangled a function or lost a register-passed argument, it is called out and marked **INFERENCE**.
>
> Frame note (project ground truth): the 512×640 tile grid is the SIDE VIEW. Native X = horizontal
> walk axis; native Y (tile rows) = the gameplay VERTICAL (gravity / climb axis, +y = down). The BDB
> height nibble = SLAB DEPTH into the screen (dig axis), engine z = `nibble*0.5+7`. Sub-grid `+0x3c`
> bit-meanings: bit0(`&1`)=walkable, bit1(`&2`)=floor(standable), bit2(`&4`)=water. Distance metrics
> weight the depth (z) axis ×0.5 (`DAT_0073708c`).

---

## 0. Overview

`CAct_MetaWalk` is a `CSubAction` subclass. It is the per-gnome top-level walk action. Its job:

1. **Ctor `FUN_00415d10`** — store (movable, owner, path-cursor bounds, animset ids, speedtype).
   Sets `WalkResult = 2` (WS_NOTFINISHED). It does **not** compute the path.
2. **CalcPath `FUN_004158a0`** — call the pathfinder (`CMapPathfinder::Calc1` `FUN_0059d440`, or Calc2
   `FUN_005a22f0` when `owner[+0x7b4]!=0`), store the resulting `CPath` on `this`, then **patch the last
   waypoint** onto the exact (snapped) target and write the snapped target back to the owner.
3. **Per-tick `FUN_00416540`** — tick the current sub-action; when it finishes, read its result code
   (0..5) and either build the next segment (code 4) or propagate up and stop (3/5/0).
4. **MakeNextSegment `FUN_004163a0`** — classify the waypoint at the cursor (`FUN_00423730`), **group**
   the maximal run of consecutive waypoints that share the same surface class (and, for water, the same
   water body), advance the cursor past that run, then construct the matching sub-action:
   FLOOR→`FUN_00415f40`, WALL→`FUN_00415e10`, WATER→`FUN_00416120`. A WALL run with `bCanClimb==0` ⇒
   result 3 (WS_NOPASSAGE).

The CPath stored on `this` is a `CPath` object: `+0x08`=point array base, `+0x0c`=count, points are
`int[2]` sub-cell (x,y) pairs at stride 8 (`int x; int y;`). `this+0x10` = CPath ptr (ref-counted),
`this+0x1c` = cursor END (count), `this+0x20` = cursor CURRENT, `this+0x14` = current sub-action ptr,
`this+0x0c` = movable, `this+0x04` = ? (movable-related handle), `this+0x18`/`+0x24` = speedtype/animset
payload copied to factories (see §6). `this+0x08` = WalkResult (the value the Tcl `get_walkresult` reads,
written 2/3/4/5 etc.).

---

## 1. CSubAction base contract (what a sub-action returns each tick; who frees it)

A `CSubAction` is a ref-counted object (`obj[0]`=vftable, `obj[1]`=refcount). Layout used by MetaWalk:

| Off | Field |
|---|---|
| `+0x00` | vftable |
| `+0x04` | refcount (`AddRef`/`Release`; `Release==0` → `(**vftable)(1)` = destructor) |
| `+0x08` | **state/result code** (0..5) — the enum below |
| `+0x0c` | movable (the entity being moved) |
| `+0x10` | CPath ptr (ref-counted) |
| `+0x14` | child sub-action ptr (the segment currently executing) |
| `+0x18`,`+0x1c` | cursor base / cursor END (path index bounds) |
| `+0x20` | cursor CURRENT (next unconsumed waypoint index) |
| `+0x24` | animset / speedtype payload |

**Per-tick virtual** = vftable slot `+0x0c` (`(**(piVar3+0xc))` at `FUN_00416540:26`). It returns a
`char`: **'\0' = the sub-action is DONE this tick**, non-zero = still running. When done, the sub-action's
**own** `obj[2]` (= `+0x08`, the result code) holds the outcome, which the parent reads and maps.

**State/result enum** (`obj+0x08`, also the value the gnome's WalkResult takes):

| Code | Name | Meaning | Where set |
|---|---|---|---|
| 0 | WS_ERROR | bad surface / internal failure | `FUN_004163a0:82` (surface class 3 fallthrough), `FUN_00416540` default |
| 1 | WS_WALKABORTED | externally broken (Break verb) | (outside MetaWalk) |
| **2** | WS_NOTFINISHED | walking, in progress | ctor `FUN_00415d10:79` |
| **3** | WS_NOPASSAGE | no path / blocked / WALL with no-climb | `FUN_004163a0:23` (no path), `:42` (solid & cannot dig), `FUN_00416540` case 3 |
| **4** | WS_TARGETREACHED | arrived (cursor exhausted) | `FUN_004163a0:28` (cursor≥end) |
| 5 | WS_WALKEVENT | walk-timer callback fired | propagated, `FUN_00416540` case 5 |

**Ownership / freeing:** field stores elsewhere use the AddRef-new/Release-old idiom
(`if(--ref==0) (**vftable)(1)`). NOTE (verifier): in `FUN_00416540` the child is freed by a **direct
deleting-destructor virtual call** `(**(code**)*child)(1)` — i.e. vftable slot 0 with arg 1 — at
`:47-49` (after a 4, before building the next segment) and at `:58-59` (after a 3/5/0), NOT by a
ref-count decrement. Semantically a final Release, but it is the destructor call, not `--refcount`.
After a 3/5/0 the slot is also nulled (`this[0x14]=0`, `:61`).

---

## 2. Ctor — `FUN_00415d10`

```
// __thiscall FUN_00415d10(this, movable, h04, CPath* path, animsetPayload p5, p6, p7)
// p2=movable, p3=h04, p4=CPath, p5/p6/p7 = animset/speedtype/cursor payload
AddRef(path); AddRef(movable); //:16-25
this[0x0c] = movable; AddRef(movable); //:26-32 (store movable)
Release(old this[0x0c]); //:33-41
this[0x04] = h04; //:42
this[0x10] = path; AddRef(path); Release(old); //:43-58 (store CPath)
Release(movable); Release(path); // balance the temp AddRefs:59-76
this[0x18] = p5; this[0x20] = p5; //:77-78 cursor base AND current = p5
this[0x08] = 2; //:79 WalkResult = WS_NOTFINISHED
this[0x1c] = p6; //:80 cursor END
this[0x24] = p7; //:81 animset payload
Release(movable); Release(path); //:82-100 final balance
return 1;
```

Key facts: `this+0x18` (cursor base) and `this+0x20` (cursor current) both init to `p5`; `this+0x1c`
(cursor end) = `p6`. `this+0x08`=2 establishes the walking state immediately. The path is **already
computed** when passed in — the ctor only stores it.

**Field map** confirmed: `+0x0c`=movable, `+0x10`=CPath, `+0x08`=result, `+0x18`/`+0x20`/`+0x1c`=cursor
base/cur/end, `+0x24`=animset, `+0x04`=h04. (These match the offsets used by `FUN_004163a0` and
`FUN_00416540`.)

---

## 3. CalcPath — `FUN_004158a0`

`__thiscall FUN_004158a0(this=param_1, out CPathPtr* param_2)` — runs the pathfinder and stamps the
result CPath into `*param_2`, then patches the final waypoint. `param_1[1]`=movable, `param_1[3]`=owner
(the CWalk owner/context), `param_1[3]+0x7xx` = owner walk-context fields.

### 3.1 The Calc1 / Calc2 branch (`:33`)

```
if (owner[+0x7b4] == 0) → Calc1 path (CMapPathfinder_Calc1, FUN_0059d440):33-67
else → Calc2 path (FUN_005a22f0):68-144
```

**Calc1 branch** (`:33-67`):
```
FUN_0059a4b0(&local_18); // ensure the global pathfinder scratch (DAT_00772420) exists
local_8 = (flags) with bCanClimb-ish byte; //:35-38
piVar5 = CMapPathfinder_Calc1(&local_18); //:39 returns &CPathPtr
result = *piVar5; //:40-42 store into *param_2 (AddRef new / Release old)
```
The arg block `local_18` packs the Calc1 parameters (see §3.3). The `CMapPathfinder_Calc1` 5-arg
wrapper at `FUN_0059d440` rounds the CVec3D inputs to integer sub-cells (`×DAT_0073752c=8.0`, ROUND)
and forwards to the real `FUN_0059d640` (the A* core).

**Calc2 branch** (`:68-144`) — `owner[+0x7b4]!=0`:
```
fVar10 = movable[+0x0c +0x0c]...; // current-pos x
DAT_00772dd8 = ROUND(fVar10 * DAT_0073752c); //:75 world→sub-cell
FUN_005a22f0(...); //:76 Calc2 (out of MetaWalk scope; spec'd in §3.5)
*param_2 = local_14 (AddRef/Release); //:77-102
// then copy the movable's current pos into owner[+0x7a4/7a8/7ac] (target seed):103-107
```

### 3.2 Target-Z handling + last-waypoint patch (`:13`)

> ⚠️ **CORRECTION (verifier):** the target-Z computation and the owner-target write-back
> (`owner+0x7a4/8/ac`, lines `:103-144`) are **INSIDE THE Calc2 `else` BRANCH ONLY** — `else {` opens
> at `004158a0:68` and its closing `}` is at `:145`. The **Calc1** branch (`:33-67`) does **NOT** run any
> of this; it only does the Calc1 call + ref-mgmt and then falls straight through to the shared
> last-waypoint-patch block at `:146-174`. So in the Calc1 (campaign) path, `owner+0x7a4/8/ac` keep
> whatever the `walk` verb parser wrote (the original `-target`), and the empty-path synthesis in §3.3
> uses *that* target, not a freshly-snapped one. The block below is the **Calc2** form.

After Calc2 returns a CPath:

```
if (*param_2 == 0) return; //:12 no path (caller maps to WS_NOPASSAGE)
n = path[+0x0c]; //:113 waypoint count
if (n > 0) { //:114
    last = path.points[n-1];           // :115-120  (x,y) of final waypoint
    iVar4 = last.x;  iVar3 = last.y;
    owner_ctx = local_18[3];           // owner
    bdbByte = FUN_004093f0(...);       // :122  read BDB AUX height byte (stride 0x59), NOT +0x3c
    // ---- target Z (depth) clamp ----
    fVar8 = (float)owner[+0x7b8];                      // :124  half-extent / body radius
    h     = movable[+0x14];                            // :125  movable current z (depth)
    lo    = h - fVar8;                                 // :126  band lower
    base  = (float)(int)(owner[+0x7dc] + ((bdbByte&0xff)+1)); // :127 floor depth + (BDB height byte+1)
    if (lo <= base) lo = base;                         // :128-130  clamp lower to floor depth
    if (lo <= DAT_00737564) clampLo = lo else DAT_00737564; // :131-134  cap at 14.5
    k = FUN_006d46ea();                                // :135  interp factor (0..1?)
    hi = h + fVar8;                                    // :136
    if (DAT_00737564 <= hi) hi = DAT_00737564;         // :137-139  cap hi at 14.5
    owner[+0x7ac] = k*DAT_00736f94*(hi-clampLo)+clampLo; // :140  TARGET Z (depth), lerp in band
    owner[+0x7a4] = (float)iVar4 * DAT_0073703c;       // :142  TARGET X = last.x * 0.125
    owner[+0x7a8] = (float)iVar3 * DAT_0073703c;       // :143  TARGET Y = last.y * 0.125
}
```

So (in the **Calc2 branch only**) the **final target X/Y come from the last computed waypoint** (snapped
onto a walkable sub-cell), and the **target Z (depth) is clamped into a band**
`[max(h-bodyR, floorDepth + bdbHeightByte + 1), min(h+bodyR, 14.5)]` and lerped by `k*DAT_00736f94`.
`DAT_00737564`=**14.5** is the universal depth ceiling. (`bdbHeightByte` = `FUN_004093f0` BDB aux byte,
NOT the `+0x3c` passability bits — `FUN_004093f0` reads the stride-`0x59` BDB array, see §5.)

### 3.3 Last-waypoint coordinate patch into the CPath (`:146-174`)

```
if (*param_2 != 0) {
  if (path[+0x0c] < 1) { // EMPTY path (single point / same-cell)
     idx = FUN_00416630(path+...);       // :148  append slot
     fx = owner[+0x7a4] * DAT_0073752c;  // :150  target X back to sub-cell (×8)
     fy = owner[+0x7a8] * DAT_0073752c;  // :151
     path.points[idx] = (int)fx, (int)fy; // :155-157  write the SINGLE waypoint = target
  } else { // NON-EMPTY path
     idx = FUN_00416630(path+...);       // :160  index of last
     // duplicate the last point (patch the tail) :162-172
     path.points[idx] = path.points[idx-1]; // copies (x,y) tail
  }
}
```

**INTERPRETATION**: when the path is empty (start==goal cell), MetaWalk synthesises a one-point path AT
the snapped world target (×8 back to sub-cell). When non-empty, the existing tail is re-written so the
last index holds the final target (consistent with the polyline already ending at goal). `FUN_00416630`
= "append/index into a 64-aligned DSFastArray, growing by `FUN_006baad0` when crossing a 0x40 boundary".

### 3.4 Calc1 arg block (`local_18`) — derived from `FUN_0059d440`/`FUN_0059d640` signatures

The decompiled `CMapPathfinder::Calc1` callstat strings (`FUN_0059d440:7`, `FUN_0059d640:9`) give the
true signature:
```
CPathPtr CMapPathfinder::Calc1(CVec3D v1, CVec3D v2, int iOwner, bool bLines, bool bCanClimb)
   v1 = movable current pos (CVec3D x,y,z)
   v2 = owner target pos (owner[+0x7a4/+0x7a8/+0x7ac])
   iOwner = movable owner id (+ owner-gate offset)
   bLines = polyline-smoothing flag (0 for MetaWalk; smoothing happens via bLines in Calc1)
   bCanClimb= owner[+0x7b0] (per-dwarf climb permission, default 1)
```
`FUN_0059d440` ROUNDs each component ×`DAT_0073752c`(8.0) to integer sub-cells before calling the integer
core `FUN_0059d640`.

### 3.5 Calc1 core (`FUN_0059d640`) — contract only (A* internals out of scope)

`FUN_0059d640` is the integer-sub-cell pathfinder (mangled, "Type propagation not settling"). Verified
structure:
- **Endpoint snapping**: if start (`param_3,param_4`) or goal (`param_5,param_6`) sub-cell is not walkable
  (`+0x3c & 1`), spiral-search outward for a walkable cell — **start: up to 33 rings** (`iVar15 < 0x21`,
  `:116-148`); **goal: up to 13 rings** (`iVar15 < 0xd`, `:154-235`) with a finer fractional offset
  pattern. If no walkable goal cell found ⇒ `*param_2 = 0` (no path).
- **Floor-bit gating** on the *tile* level via `+0x3c & 2` (`:270`,`:304`): the two `while(true)` loops
  nudge start/goal upward (`param_4+1`, `param_6+1`) while the sub-cell is NOT a floor (bit1 clear) —
  i.e. snap the endpoints DOWN onto a standable floor line.
- **Trivial path**: if snapped start == snapped goal ⇒ allocate a `CPath` (`0x37c` bytes, `CPath::vftable`)
  with the two identical points and return (`:311-348`).
- **A* / Bellman-Ford** over the per-tile cost grid `DAT_00b9bf18` (cost array, stride `DAT_00b9bf20`,
  modulus `DAT_00b9bf28`): neighbour relaxers `FUN_0059aad0` (no-climb) / `FUN_0059aa20` (climb) probe the
  4 cardinals (+`y±1`, `x±1`) and write back-pointers; `FUN_0059ab90`/`0059b670`/`0059bf90`/`0059ca30`
  are the four init variants chosen by `(bLines, iOwner<8)`.
- **Output build** (`:831`): walk the back-pointer chain into the CPath point array; with `bLines`
  set, **collinear runs are collapsed and long diagonals are subdivided** (the `DAT_007371c0`=1.5
  axis-dominance test + `DAT_00737330` step loop at `:925-984`). The result `CPath` is returned via
  `param_2`. `bLines==0` (MetaWalk) keeps the full per-cell polyline.

This is enough for the port: **MetaWalk treats Calc1 as a black box that returns a CPath of sub-cell
(x,y) waypoints from snapped-start to snapped-goal.** Our port already has `Calc1Pathfinder.cs`.

---

## 4. MakeNextSegment — `FUN_004163a0` (the grouping rule, EXACT)

`__fastcall FUN_004163a0(this=param_1 in ECX)`. Builds the sub-action for the next contiguous segment and
returns it (the caller stores it at `this+0x14`). **This is the heart of the test runner.**

```
path = this[0x10]; //:20 CPath
if (path == 0) { this[0x08] = 3; return 0; } //:21-24 NO PATH → WS_NOPASSAGE

cur = this[0x20]; //:26 cursor CURRENT
if (this[0x1c] <= cur) { this[0x08] = 4; return 0; } //:27-30 CURSOR EXHAUSTED → WS_TARGETREACHED

AddRef(path); //:31-33
surf0 = FUN_00423730(path, /*bCanSwim*/, cur); //:34 classify the FIRST waypoint of the run
                                                //      (Ghidra shows FUN_00423730(iVar2); real args §5)
base = 0;
if (surf0 == 1) { //:36 WATER
    if (owner[+0x7b1] != 0) {                   // :37  bCanSwim
        FUN_0040c060(this[0x20]);               // :38  bounds-check the cursor in the array
        base = FUN_004237e0();                  // :39  WATER-BODY id of the first water waypoint
    }
}
else if (surf0 == 0 && owner[+0x7b0]==0) //:42 WALL and cannot-climb (+0x7b0==0)
    goto LAB_00416527 (this[0x08]=3; return 0); //      → WS_NOPASSAGE

cur = ++this[0x20]; //:43-44 consume the first waypoint
surfRun = surf0;
if (cur < this[0x1c]) { //:46 more waypoints in range
  do {
    p = this[0x10];  AddRef(p);                 // :48-53
    surfN = FUN_00423730(p, /*…*/, cur);        // :54  classify the NEXT waypoint
    if (surfN != surf0) break;                  // :56  ── SURFACE CHANGED → end the run
    if (surf0 == 1) {                           // :57  for WATER also require same body
        bounds-check cur < path.count;          // :58-60
        bodyN = FUN_004237e0();                 // :61  water-body of this waypoint
        if (bodyN != base) break;               // :63  ── DIFFERENT WATER BODY → end the run
    }
    cur = ++this[0x20];                         // :65-66  consume
  } while (cur < this[0x1c]); //:67
}

// start = this[0x20] captured at FUNCTION ENTRY (:26, iVar1), BEFORE any ++ → the run START index
end = this[0x20]; //:69 one-past-the-run cursor (iVar4)
// nextSurf = iVar3 = surface that ENDED the run (== surf0 if run hit the path end / single wp)
if (end < this[0x1c] && surf0 == 0) //:70 WALL run that did NOT reach the end
    end = end + 1;                               // :71  (overlap one waypoint into the next surface)
else if (surf0 != 0) { //:73
    if (surf0 == 1) return FUN_00416120(this, start, end);          // :74-76  WATER → WalkObject (ECX=this)
    if (surf0 == 2) return FUN_00415f40(this, start, end, nextSurf);// :78-80  FLOOR → WalkFloor (ECX=this)
    this[0x08] = 0; return 0;                     // :82-83  unknown surface → WS_ERROR
}
return FUN_00415e10(this, start, end, nextSurf); //:85-86 WALL (surf0==0) → Climb (ECX=this)
```

> ⚠️ **CORRECTION (verifier): factory argument order.** Ghidra renders the call sites as
> `FUN_004xxxxx(iVar1, iVar4, iVar3)` (3 args). Because these are `__thiscall`, `this`=ECX=the MetaWalk
> object (`param_1` of `FUN_004163a0`) is **implicit** and is NOT one of those three. The three explicit
> args are **`iVar1` = run START index** (`*(this+0x20)` captured at `:26`, before consumption),
> **`iVar4` = run END index**, **`iVar3` = the surface class that ended the run** (climb dismount hint /
> WalkFloor "next-is-wall" gate). The factory bodies confirm this: each reads MetaWalk fields off its own
> ECX `param_1` (`FUN_00415f40:24,26,76,82,83`; `FUN_00415e10:36,43,44`; `FUN_00416120:53,57,64,65`) and
> uses the two/three *stack* params as path indices. So the segment slice is **`[start, end)`** —
> WalkObject even takes the midpoint `(start+end)/2` for its water-body probe (`FUN_00416120:53`).
> The earlier "(`this, end, surfRun`)" rendering elsewhere in this doc is the same correction.

### 4.1 The grouping rule, stated precisely

> **A segment is the maximal run of consecutive path waypoints, starting at the cursor, that all share
> the SAME surface class `FUN_00423730` value — AND, when that class is WATER (1), the SAME water-body id
> `FUN_004237e0`.** FLOOR and WALL runs group on surface class alone. The run is `[start, end)` where the
> cursor is left at `end`.

Surface dispatch (`surf0`):
- **0 = WALL** → `FUN_00415e10` (Climb factory). Special: if `bCanClimb(+0x7b0)==0` ⇒ WS_NOPASSAGE (3)
  *before* grouping (`:42`). A WALL run that does not reach the path end has its `end` bumped by +1
  (`:70-71`) so the climb segment overlaps the first waypoint of the following surface (gives the climb
  a dismount target on the next floor).
- **1 = WATER** → `FUN_00416120` (WalkObject/swim factory), grouped also by water-body.
- **2 = FLOOR** → `FUN_00415f40` (WalkFloor factory).
- anything else ⇒ WS_ERROR (0).

### 4.2 Cursor mechanics

- `this+0x20` = cursor CURRENT, `this+0x1c` = cursor END (= waypoint count, set in ctor).
- Each consumed waypoint does `++this[0x20]`. The run's start index is the cursor value on entry; the run
  end index is the cursor on exit (one-past-last-consumed). The factories receive `this`(ECX) +
  `(startIdx, endIdx, endingSurfaceClass)` — the WalkFloor/Climb/WalkObject sub-actions read the path
  slice `[startIdx, endIdx)` off `this[0x10]`. (See the §4 correction box for the exact arg order.)
- When `cursor >= end` on entry ⇒ TARGETREACHED (4). MetaWalk's per-tick (`FUN_00416540`) builds the
  next segment after each child finishes, so segments are produced lazily, one at a time.

`FUN_0040c060(idx)` (`:38`) = `CFastArray::operator[]` bounds-check returning `*base + idx*8` (the
(x,y) record address). `FUN_004237e0` (water-body id) is §5.2.

---

## 5. Surface classifier — `FUN_00423730` (exact bits)

Ghidra signature: `__fastcall FUN_00423730(uint param_1, char param_2, CPath* param_3)` where
- `param_1` (ECX) = **waypoint index** into the path,
- `param_2` (DL) = **bCanSwim** flag (the owner's `+0x7b1`),
- `param_3` (stack) = the **CPath** (point array holder).

> NOTE — be precise about what is inferred. **The 3-arg *signature itself is NOT inferred*** — Ghidra's
> own function header is `FUN_00423730(uint param_1, char param_2, undefined4 *param_3)` (`00423730:7`),
> three params, explicit. What IS inferred is the **call-site argument binding** in `FUN_004163a0`: there
> Ghidra rendered the calls as `FUN_00423730(iVar2)`/`(iVar6)` (one arg) and even put the *CPath* (not the
> index) in ECX, because it lost the EDX (`bCanSwim`) and stack (`path`) operands and mis-assigned the
> AddRef'd CPath register. The reconstructed binding `(idx=ECX, bCanSwim=DL=owner[+0x7b1], path=stack)` is
> the inference (high confidence: the body bounds-checks `param_3[3]` as the count, indexes `param_3[2]`
> by `param_1*8`, and the trailing Release at `:37-44` pairs with `FUN_004163a0`'s `:31`/`:48` AddRef of
> the CPath).

```
undefined4 FUN_00423730(uint idx, char bCanSwim, CPath* path) {
  if (path[3] /*count*/ <= idx) RaiseException(BOUNDS); //:16-18
  sx = path[2]/*pts*/[idx*8 + 0]; //:19 waypoint sub-cell x
  sy = path[2] [idx*8 + 4]; //:20 waypoint sub-cell y
  tile = FUN_00592f10(sx>>3, sy>>3); //:21 tile struct for (col,row)
  b = tile[0x3c + ((sy&7)<<3 | (sx&7))]; //:22 sub-cell passability byte

  if (bCanSwim && (b & 4)) { //:23 WATER bit
      wb = FUN_004237e0();                                       // :24  water-body context
      if (*(wb + 0xdc) != 0) { result = 1; goto rel; }          // :25-28  active water body → WATER
  }
  if ((b & 2) == 0) result = 0; //:30-31 NOT floor → WALL (solid / climbable)
  else result = 2; //:33-34 floor bit set → FLOOR
rel:
  Release(path); //:37-44 paired with FUN_004163a0's AddRef
  return result; // 0=WALL, 1=WATER, 2=FLOOR
}
```

So the classifier is **purely the `+0x3c` sub-grid bits**:
- `b & 4` (water) AND `bCanSwim` AND active water-body → **1 WATER**
- else `b & 2` clear → **0 WALL** (must be climbed, or NOPASSAGE if cannot climb)
- else → **2 FLOOR**

`FUN_00592f10` = `CMap::GetTile(col,row)`: clamps (col,row) into the map bounds (`+0x84..+0x90`),
folds `(row * +0x44 + col) % +0x4c`, bounds-checks against `+0x18`, returns `idx*0x7c + tileBase(+0x14)`.
`FUN_004093f0` (used by §3.2 and the WalkFloor factory) = read a **height/geometry byte** from the BDB
aux: `cell = GetTile(x>>3,y>>3); base = cell[0]*0x59 + bdbBase(+0x10); return base[((y&7)<<3|(x&7))]`.

### 5.2 Water-body id — `FUN_004237e0`

`__fastcall FUN_004237e0(int* pXY)` — returns the **water/region object** for a world sub-cell:
```
col = (pXY[0] + sign-correct)>>3; row = (pXY[1] + sign-correct)>>3; //:16-17
tile = GetTile(col,row); id = (ushort)tile[0x16]; //:18-19 region id
if (id==0) probe 4 neighbours (col±(row-col)±1, row±1) for a non-zero id; //:20-37
if (all 0) return regionTable[0]; //:33
return regionTable[id] // regionTable = DAT_0077244c + 0x488, capped 0x2000 //:39-44
```
i.e. tile field `+0x16` is the region/water-body index; the classifier groups water runs by this id.

---

## 6. Per-tick — `FUN_00416540` (sub-action result codes 0..5 + propagation)

`__fastcall FUN_00416540(this=param_1)` — called every frame while MetaWalk is the active action.

```
uVar6 = 1; // default "still running"
child = this[0x14]; //:18 current sub-action
if (child == 0) { //:19 no segment yet → build the first one
    child = FUN_004163a0(this);         // :20  MakeNextSegment
    this[0x14] = child;
    if (child == 0) return 0;           // :22-24  MakeNextSegment failed (e.g. set result 3/4) → stop
}
done = (**(child+0x0c)); //:26 TICK the sub-action (its per-tick virtual)
if (done == '\0') { //:27 sub-action FINISHED this tick
    r = child[2];                       // :30  read child result (obj+0x08, 0..5)
    uVar6 = 1;
    switch (r) {
      case 0: uVar6 = 0; /*fall*/        // :31-32  ERROR → this stops
      default: this[0x08] = 0; break;    // :33-35  (default & 0) set WalkResult 0, stop next tick
      case 3: this[0x08]=3; uVar6=0; break; // :36-39  NOPASSAGE → propagate up, stop
      case 4: break;                     // :40-41  TARGETREACHED-of-segment → CONTINUE to next segment
      case 5: this[0x08]=5; uVar6=0; break; // :42-45  WALKEVENT → propagate up, stop
    }
    if (child[2] == 4) {                 // :46  segment done, more path to walk
        Release(child);                  // :47-49
        this[0x14] = FUN_004163a0(this); // :50-51  build NEXT segment
        return (this[0x14]!=0) ? uVar6 : 0; // :52-56
    }
    // result 3/5/0: free the child, clear the slot, stop
    Release(child);                      // :58-60
    this[0x14] = 0;                      // :61
}
return uVar6; //:63 non-zero = MetaWalk still running; 0 = finished
```

### 6.1 Result-code propagation table (sub-action → MetaWalk)

| Child result `child[0x08]` | MetaWalk action | `this[0x08]` (WalkResult) | MetaWalk keeps running? |
|---|---|---|---|
| **4** WS_TARGETREACHED (of this segment) | free child, **build next segment** | unchanged (still 2) | yes if next segment exists, else next tick MakeNextSegment returns 4 |
| **3** WS_NOPASSAGE | free child, stop | **3** | no (returns 0) |
| **5** WS_WALKEVENT | free child, stop | **5** | no |
| **0** WS_ERROR | free child, stop | **0** | no |
| (running, returns nonzero from tick) | nothing | unchanged | yes |

So **4 is the only "continue" code**: each segment reports 4 when its slice is consumed, and MetaWalk
immediately spins up the next surface segment. When `FUN_004163a0` finds the cursor exhausted it returns
0 but sets `this[0x08]=4` first (`:27-30`) — that is how the *whole* walk reports WS_TARGETREACHED. (The
child's per-segment "4" and MetaWalk's terminal "4" are the same enum value but different scopes.)

---

## 7. The three sub-action factories

> ⚠️ **CORRECTION (verifier):** all three are `__thiscall` whose ECX (`this`) = **MetaWalk** and whose
> explicit args are **`(startIdx, endIdx, endingSurfaceClass)`** — NOT `(end, surfRun)`. The Ghidra
> headers prove the arity: `FUN_00415e10(int,undefined4,undefined4,undefined4)` (this+3),
> `FUN_00415f40(int,undefined4,uint,int)` (this+3), `FUN_00416120(int,undefined4*,int)` (this+2).
> Each body reads movable/path/cursor off its own ECX `this` (`param_1`). They configure the new
> sub-action for the path slice **`[startIdx, endIdx)`**.

### 7.1 WALL → `FUN_00415e10` (CAct_Climb)

```
// __thiscall(this=MetaWalk, param_2=startIdx, param_3=endIdx, param_4=endingSurfaceClass)
obj = new(0x9f); obj.vftable = CAct_Climb::vftable; obj[3,4,7,8]=0; //:25-35
path = this[0x10]; h04 = this[0x04]; movable = this[0x0c]; //:36-44
FUN_0041fe30(movable, h04, path); //:61 base sub-action init (store movable+path, ref-mgmt)
obj[5] = param_2 (startIdx); //:62 segment START index
obj[6] = param_3 (endIdx); //:63 segment END index
obj[2] = 2; //:64 state = 2 (running)
obj[7] = FUN_0040bb70(param_4); //:65-66 build Climb point list from the ENDING SURFACE CLASS
return obj;
```
**CORRECTION:** `obj[5]` = **param_2 = startIdx** and `obj[6]` = **param_3 = endIdx** (the earlier
"`obj[5]=end; obj[6]=surfRun`" was wrong). The `FUN_0040bb70` arg is **param_4 = the ending surface
class** (iVar3), not an "animsetPayload". `FUN_0040bb70` (the Climb ctor helper) mirrors the path slice
into 16-byte `{x,y,z,flag}` records (world = sub-cell `* DAT_0073703c` = ×0.125), see §8.

### 7.2 FLOOR → `FUN_00415f40` (CAct_WalkFloor)

```
// __thiscall(this=MetaWalk, param_2=startIdx, param_3=endIdx, param_4=endingSurfaceClass)
local_c = this[0x24]; //:24 the walk-verb target-depth payload (sentinel DAT_0073773c if unset)
// --- depth pre-pass: ONLY when the floor run is IMMEDIATELY FOLLOWED BY A WALL (param_4==0) ---
if (param_4==0 && local_c==DAT_0073773c && (int)param_3 < this[0x1c]) { //:25
    // read BDB AUX height bytes (FUN_004093f0) at the END waypoint (b@endIdx) and the one
    // AFTER it (a@endIdx+1), take the SMALLER (MIN) byte, clamp into [.., DAT_00737568].
    a = path[endIdx+1 clamped];  b = path[endIdx clamped];           // :28-46
    h_b = FUN_004093f0(b);  h_a = FUN_004093f0(a);                   // :47-48
    chosen = (h_a < h_b) ? a : b;       // :49-52  pick the LOWER BDB byte (min depth nibble)
    hMin = FUN_004093f0(chosen);                                     // :53
    local_c = min(hMin + DAT_00737138, DAT_00737568);                // :54-57  clamp target depth
}
obj = new(0xc6); obj.vftable = CAct_WalkFloor::vftable; init many fields 0; //:59-75
movable=this[0x0c]; h04=this[0x04]; path=this[0x10]; //:76-88
FUN_0041a910(movable, h04, path, startIdx, endIdx, (param_4==0), local_c); //:89
return obj;
```
> ⚠️ **CORRECTIONS (verifier):**
> 1. **`param_4==0` is NOT a "dig/floor-snap toggle".** `param_4` = `iVar3` = the **surface class that
> ended the run**; `param_4==0` is true exactly when **the next surface after this floor run is a WALL
> (class 0)**. So the depth pre-pass only fires when the gnome is about to transition floor→climb; it
> sets the floor target from the **LOWER (min)** of the two boundary cells' BDB height bytes
> (`bVar6<bVar5 ? next: end`, `00415f40:49-52`) plus `DAT_00737138`, capped at `DAT_00737568`. The boolean
> `(param_4==0)` IS forwarded to `FUN_0041a910` as `param_7` → `obj+0x48` (a "next-is-wall" flag), but
> framing it as a dig toggle is unsupported.
> 2. **`FUN_0041a910` stores the cursor indices, not "end/surfRun".** It sets `obj[0x20]=param_5=startIdx`
> (`0041a910:57`) and `obj[0x24]=param_6=endIdx` (`:58`) — the earlier "`obj[0x20]=end, obj[0x24]=surfRun`"
> was wrong (offsets correct, *values* swapped/mislabelled). It also sets `obj[0x08]=2` (`:59`),
> `obj[0x44]=param_8=local_c` (`:60`), `obj[0x48]=param_7` (`:68`).
> 3. **The dig-standoff gate is `owner[+0x7e8]==3 && owner[+0x798]!=2`** (`0041a910:69`), then
> squared-distance `< DAT_007374cc` (depth axis ×`DAT_0073708c`) ⇒ `FUN_004234a0` (`:76-77`); else if
> `owner[+0x798]==0 && owner[+0x1ec]!=-1` ⇒ set `owner[+0x798]=1` (`:79-80`).

`FUN_0041a910` = WalkFloor base init: stores (movable,h04,path), the cursor `[startIdx,endIdx)`, target
depth, dig-standoff bring-up, and the floor point list (`FUN_0041a2f0`/`FUN_0041a470`).

`DAT_00737568` = depth cap (≈ another 14.5-ish ceiling for the floor target), `DAT_00737138` = small
depth bias added to the BDB height. `DAT_0073773c` = the sentinel "unset target depth" the pre-pass
recognises.

### 7.3 WATER → `FUN_00416120` (CAct_WalkObject / swim)

```
// __thiscall(this=MetaWalk, param_2=startIdx, param_3=endIdx) — only 2 explicit args
obj = new(0xd4); obj.vftable = CAct_WalkObject::vftable; init fields 0; //:32-52
mid = ((int)param_2 + param_3)/2; bounds-check mid < path.count; //:53-55
iVar9 = FUN_004237e0; //:56 water-body object for the MID waypoint
path=this[0x10]; h04=this[0x04]; ctx=this[0x0c]; //:57-71
obj[3]=ctx (ref-mgmt); obj[1]=h04; obj[4]=path; //:77-134
obj[0xb]=param_2 (startIdx); obj[0xc]=param_3 (endIdx); obj[10]=iVar9; //:135-138
// link obj into a global callback list (obj[5]=list node, obj[7]=list head):145-171
obj[8] = FUN_0041b170; //:172 the WalkObject per-tick callback
return obj;
```
WalkObject swims toward the water-body `iVar9` between the segment's two endpoints. **Out of scope until
water gameplay** (per the brief); the contract is: a WATER run yields one WalkObject sub-action whose
result feeds the same 0..5 propagation.

---

## 8. Tcl ↔ native contract

MetaWalk itself is **not** directly Tcl-callable; it is the implementation behind the `action <obj> walk`
verb (dispatcher `FUN_00407430`, parser `FUN_00420840`). The Tcl-visible surface is:

| Tcl | Native | Contract |
|---|---|---|
| `action $o walk "<opts>" <fin> <brk>` | builds CWalkInfo on owner `+0x79c`, then a MetaWalk via CalcPath | opts set `-canclimb`(+0x7b0, def 1), `-target{xyz}`(+0x7a4/8/c), `-dig`(floor-snap bypass), `-speedtype`, `-animsets`, … |
| `get_walkresult $o` | reads gnome `+0x248` (the value MetaWalk stamps via the enum) | 2=walking, 3=no-passage, 4=arrived, 5=event, 0/1=error/abort |
| `get_gnomeposition` | movable `+0x0c/+0x10/+0x14` (x,y,z=depth) | the pos MetaWalk advances; Z is the depth axis |

The `<fin>` script fires when MetaWalk leaves the running set (result≠2). The auto-retry layer
`CActWalk_AutoRetry` (`FUN_004075b0`) wraps MetaWalk and re-paths on stuck/3 by nudging the target and
rebuilding — **plain MetaWalk never re-paths**.

---

## 9. Constants used by metawalk-core

| Const | Value (hex / dec) | Role | Cite |
|---|---|---|---|
| `DAT_0073752c` | 8.0 | world→sub-cell scale (×8, ROUND) | `0059d440:58`, `004158a0:150` |
| `DAT_0073703c` | 0.125 (1/8) | sub-cell→world (target X/Y) | `004158a0:142-143` |
| `DAT_0073708c` | 0.5 | Z/depth weight in dist metrics | (WalkFloor/Climb) |
| `DAT_00737564` | 14.5 | depth ceiling (target-Z clamp) | `004158a0:131-139` |
| `DAT_00737568` | ~14.5 | floor-target depth cap (WalkFloor) | `00415f40:54-55` |
| `DAT_00737138` | small bias | depth bias added to BDB height | `00415f40:55` |
| `DAT_0073773c` | sentinel | "unset target depth" marker | `00415f40:25` |
| `DAT_00736f94` | lerp factor | target-Z interpolation weight | `004158a0:140` |
| `DAT_007374cc` | dig-near² | dig-standoff proximity threshold | `0041a910:76` |
| tile stride | 0x7c = 124 | tile array record | `00592f10:29` |
| BDB aux stride | 0x59 = 89 | per-block height/geometry | `004093f0:22` |
| region table base | `DAT_0077244c+0x488`, cap 0x2000 | water-body lookup | `004237e0:33-44` |
| new sizes | Climb 0x9f / WalkFloor 0xc6 / WalkObject 0xd4 | sub-action object sizes | `00415e10:25`,`00415f40:59`,`00416120:32` |

Sub-grid `+0x3c` bit meanings (classifier `00423730:23-34`): `&1`=walkable, `&2`=floor(standable),
`&4`=water. Owner walk-context fields: `+0x7a4/8/c`=target xyz, `+0x7b0`=bCanClimb, `+0x7b1`=bCanSwim,
`+0x7b4`=use-Calc2 flag, `+0x7b8`=body radius, `+0x7dc`=floor depth base, `+0x7e8`=dig-mode (==3),
`+0x798`=approach state.

---

## 10. How it interacts with neighbour systems

- **Calc1/Calc2 (pathfinder)** — supplies the CPath. MetaWalk only reads `+0x08`(count) and the (x,y)
  point array; it patches the tail to the snapped target (§3.2-3.3).
- **WalkFloor (`00415f40`/CAct_WalkFloor)** — FLOOR sub-action; root-motion-coupled mover that follows
  per-sub-cell floor depth. Reports 4 on slice-arrival → MetaWalk continues. (spec area: walkfloor)
- **Climb (`00415e10`/CAct_Climb)** — WALL sub-action; FloorPathMap BFS over height-steps + interpolating
  executor. Gated by `+0x7b0`. (spec area: climb)
- **WalkObject (`00416120`)** — WATER sub-action (swim). Out of scope until water gameplay.
- **Dig standoff** — WalkFloor init checks `owner[+0x7e8]==3` and the proximity (`< DAT_007374cc`) to fire
  `FUN_004234a0` / set approach state — the bridge from "walk to dig edge" to "start digging".
- **Tcl work loop** — reads `get_walkresult` to branch (2 keep waiting, 4 do next task step, 3 reroute).

---

## 11. PORT GAP analysis (vs `Assets/Diggles/Runtime/Tcl/Engine/EntityActions.cs` `MetaWalkAction`)

Our `MetaWalkAction` (EntityActions.cs:250) is a **single FLOOR mover** that walks a precomputed
world-space waypoint list with a walk_start→loop→stop anim chain and progressive run-promotion. It is NOT
the faithful maintainer. Gaps:

1. **No surface classification / segmentation.** The original groups the path into FLOOR/WALL/WATER runs
   (`FUN_004163a0` + `FUN_00423730`) and dispatches a *different* sub-action per run. Our port treats the
   whole path as one floor walk (header comment admits "v1 = FLOOR only; climb comes as a ClimbAction this
   spawns later"). **Gap:** implement `MakeNextSegment` — read `NavWorld`'s `+0x3c` bits per waypoint,
   group consecutive same-class (and same water-body) waypoints, and emit a `MetaWalkAction` (FLOOR),
   `ClimbAction` (WALL), or water action per run, chained via the existing `SequenceAction`.

2. **No WALL→Climb dispatch and no `bCanClimb` gate.** The original yields WS_NOPASSAGE (3) for a WALL run
   when `+0x7b0==0`. Our port has a separate `ClimbAction` (ClimbAction.cs) but MetaWalkAction never
   spawns it nor checks climb permission. **Gap:** wire the WALL branch + the no-climb→result-3 path.

3. **No result-code propagation (0..5).** The original's per-tick reads the child result and only on **4**
   continues to the next segment; 3/5/0 propagate up and stop. Our `WalkResultAction` (EntityActions.cs:73)
   only stamps a single onFinish (4 or 3) when its single inner finishes — it cannot express
   per-segment continuation or the WS_WALKEVENT(5) path. **Gap:** the segment chain must map each segment's
   terminal result through the 4-continues / {3,5,0}-stops table (§6.1); only the LAST segment's success
   should write WalkResult 4, an early WALL-no-climb writes 3 immediately.

4. **Target-Z (depth) handling absent.** The original snaps the final waypoint onto a walkable cell and
   clamps/lerps the **depth** into a band capped at 14.5 (§3.2). Our `Advance` (EntityActions.cs:388-389)
   just lerps `to.y` linearly toward the waypoint Y with no per-cell floor-depth follow and no 14.5 clamp.
   **Gap:** sample per-sub-cell floor depth at each waypoint (`GetHmapSub`) and follow it; weight the depth
   axis ×0.5 in arrival (§9), don't straight-lerp Y start→goal. (This is the WalkFloor floor-follow bug
   already noted in the main doc §4.4; metawalk just supplies the snapped target.)

5. **Last-waypoint patch / single-point path.** The original synthesises a one-point path at the target
   when start==goal cell (§3.3). Our port has no equivalent — an empty/degenerate Calc1 result likely
   produces no movement and no WalkResult=4. **Gap:** when the pathfinder returns 0 or 1 points, still
   stamp WalkResult and (if same-cell) treat as immediately arrived (4).

6. **WalkResult on the gnome is binary (2→onFinish).** `WalkResultAction` sets 2 at start and `_onFinish`
   at end. The faithful enum has 5 states and is read mid-walk by the Tcl work loop. **Gap:** expose the
   full enum on `DigglesTclEntity.WalkResult` and write 3 (no-passage) when MakeNextSegment fails, 4 only
   on full-cursor-exhaustion.

7. **Arrival radius / metric mismatch.** Port uses `Arrive=0.2` horizontal (EntityActions.cs:275), original
   uses 0.09 = 0.3² squared-distance with the depth axis weighted ×0.5. Minor, but affects whether vertical
   gaps false-trigger arrival on wall↔floor transitions. **Gap:** match the squared metric + 0.5 depth weight.

---

## 12. Unresolved

1. **Calc2 (`FUN_005a22f0`)** — fully read here (structure mirrors Calc1: endpoint snap, A* over
   `DAT_00b9bf18`, `bLines` smoothing, output CPath). Still gated by `owner[+0x7b4]!=0`; campaign uses
   Calc1. The two differ in the init variant set (`FUN_0059fda0`/`005a0f00`/`005a1920`/`005a0650` vs Calc1's
   `0059ab90`/…) — the *functional* difference (Calc2 appears to be the bLines-always / object-aware
   variant) is not yet pinned. Non-blocking for the campaign port.
2. **`FUN_00423730` call-site arg passing** — the 3-arg signature `(idx, bCanSwim, path)` is EXPLICIT in
   Ghidra's header (`00423730:7`); what is INFERENCE is the **call-site binding** in `FUN_004163a0`
   (rendered as a 1-arg call with the CPath mis-placed in ECX). High confidence from the body
   (count=`path[3]`, points=`path[2]`, index=`param_1`) but confirm by disasm of the call site (which
   register holds the path / DL/`bCanSwim`).
3. **`FUN_006d46ea` / `DAT_00736f94`** in the target-Z lerp (`004158a0:135,140`) — `FUN_006d46ea` returns
   an int used as a lerp factor; its exact semantics (RNG? frame counter? interp k) are not resolved. The
   net effect is a clamp of target depth into `[floorDepth + bdbHeightByte + 1 … 14.5]` (the added byte is
   the `FUN_004093f0` BDB aux value, NOT `+0x3c` passability bits); the lerp weight is cosmetic for
   placement. Confirm during WalkFloor impl. (This block is Calc2-only — see §3.2 correction.)
4. **`local_c == DAT_0073773c` sentinel** in `00415f40:25` — assumed "target depth unset". Verify the
   default written by the walk-verb parser (`FUN_00420520`) equals `DAT_0073773c`.
5. **`this+0x04` (h04)** — passed through every factory but its exact role (a secondary movable handle /
   the CWalk owner) is not nailed; tracked as an opaque pass-through.

---

## Verification notes (adversarial re-check)

All 12 load-bearing claims were re-checked against the primary decompile. **Confirmed:** the ctor field
map (Claim 1), the `owner[+0x7b4]` Calc1/Calc2 branch (Claim 2), target X/Y = last waypoint ×0.125 and
14.5 depth ceiling (Claim 3), the empty-path ×8 synthesis / tail patch (Claim 4), the per-tick child
dispatch with "only 4 continues" (Claim 5), the three MakeNextSegment terminal results (Claim 6), the
maximal same-surface(+water-body) grouping (Claim 7), the non-terminal WALL `end+1` overlap (Claim 8),
the surface-classifier bit logic 1/0/2 over `+0x3c` (Claim 9 body), and the CSubAction contract — result
at `+0x08`, per-tick virtual at vftable `+0x0c` returning char-0-when-done (Claim 12). Corrections made:

1. **§3.2 target-Z is Calc2-only, NOT shared.** The `else {` at `004158a0:68` closes at `:145`, so the
   whole target-Z computation and the `owner+0x7a4/8/ac` write-back (`:103-144`) run **only in the Calc2
   branch**. The Calc1 (campaign) branch never executes them; it falls straight to the shared
   last-waypoint patch (`:146-174`). Claim 3 is therefore true *of the Calc2 path*; for Calc1 the owner
   target is whatever the `walk` verb parser wrote. Added a correction box.

2. **Factory signatures were wrong — biggest fix.** The spec said all three factories are
   `__thiscall(this=MetaWalk, end, surfRun)`. The Ghidra headers show `this`+3 (`FUN_00415e10`,
   `FUN_00415f40`) and `this`+2 (`FUN_00416120`). The MetaWalk object is the **implicit ECX `this`**;
   the explicit args are **`(startIdx, endIdx, endingSurfaceClass)`**. The caller passes
   `iVar1 = *(this+0x20)` = the run **START** cursor (captured at `004163a0:26`), `iVar4` = the **END**,
   `iVar3` = the surface class that ended the run. The segment slice is `[start, end)`. Corrected §4
   pseudocode, §4.2, §7 header, §7.1, §7.2, §7.3.

3. **§7.1 Climb field stores corrected.** `obj[5]=param_2=startIdx` and `obj[6]=param_3=endIdx`
   (`00415e10:62-63`), not `obj[5]=end; obj[6]=surfRun`. `FUN_0040bb70`'s arg is `param_4` = the ending
   surface class (`:65`), not an "animsetPayload".

4. **§7.2 WalkFloor:** (a) `param_4==0` is **"next surface is a WALL"**, not a generic "-dig/floor-snap
   toggle" — `param_4` is the ending surface class (`iVar3`). (b) `FUN_0041a910` stores
   `obj[0x20]=startIdx` (`0041a910:57`) and `obj[0x24]=endIdx` (`:58`) — the spec's "`obj[0x20]=end,
   obj[0x24]=surfRun`" had the offsets right but the values mislabelled. (c) The depth pre-pass picks the
   **LOWER (min)** of the two boundary cells' BDB bytes (`if(bVar6<bVar5) chosen=next`, `00415f40:49-52`),
   not the "higher"/"higher-floor" cell.

5. **`FUN_004093f0` reads the BDB AUX byte, not `+0x3c`.** In §3.2 the function was annotated as reading
   "the +0x3c passability byte" and its value called "passbits". `FUN_004093f0` (`004093f0:14-25`) indexes
   the **stride-`0x59` BDB array** (`blockIdx*0x59 + bdbBase(+0x10)`) — a height/geometry byte — and is a
   *different* read from the classifier's `+0x3c` (`00423730:22`). Relabelled `floorByte`→`bdbByte`.

6. **§5 INFERENCE wording sharpened.** Claim 9 says "the three argument signature is an inference." Strictly,
   the **3-arg signature is explicit in Ghidra's header** (`00423730:7`); only the **call-site argument
   binding** in `FUN_004163a0` (which Ghidra rendered as a 1-arg call with the CPath mis-placed in ECX) is
   the inference. Note rewritten to separate the two.

7. **§1 freeing mechanism.** The child is freed in `FUN_00416540` by a **direct deleting-destructor call**
   `(**(code**)*child)(1)` (`:47-49`, `:58-59`), i.e. vftable[0] with arg 1, not a `--refcount` Release
   (semantically equivalent, but the doc's "frees by Release" was imprecise). Slot nulled at `:61`.

8. **Claim 11 enum — partial confirmation.** Values **2 (NOTFINISHED), 3 (NOPASSAGE), 4 (TARGETREACHED),
   5 (WALKEVENT)** are hard-backed by the decompile (`00415d10:79`, `004163a0:23/28/42`, `00416540` cases)
   and Tcl (`genericfight.tcl:878` annotates `==2` as `WS_NOTFINISHED`; arrival checks use `==4` / `!=4`
   everywhere). **0 = WS_ERROR** is backed by the `case 0`/default → `+0x08=0` paths. The split **0=ERROR
   vs 1=WALKABORTED** is an inference: the binary's string table lists the six `WS_*` names *alphabetically*
   (`Diggles.interesting.txt:604-609`), so it does NOT fix the numeric order; WS_WALKABORTED=1 is plausible
   (set outside MetaWalk by the Break verb) but unverified from these sources. Marked accordingly.

No other load-bearing claim required correction; the grouping rule, cursor mechanics, propagation table,
and constants table check out against the cited lines.
