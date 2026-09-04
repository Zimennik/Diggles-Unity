> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

> **Correction** the decompiler-local `layer` below is the owner/player index
> read from gnome+0x36c. It selects one of eight parallel order nibbles; it is not geometry.

# dig-natives — complete RE of the remaining dig native commands

> Ground-truth reverse-engineering of the dig-targeting / standoff / mark / apply native commands that
> the unchanged Tcl dig loop (`z_dignwalk.tcl`, `std_ai.tcl`) bottoms out in. Sources read line-by-line:
> `re/decompiled/Diggles/_global/*.c`, the float/double constants extracted **directly from
> `original/Diggles.exe` `.rdata`** (PE section-mapped, image base 0x400000), the engine-command notes
> `re/notes/tcl_engine_commands.tsv`, and the Tcl callers in `original/data/Scripts`. Every claim is cited
> `FUN_addr:line` / offset / constant (hex+dec). Frame convention per the project brief: native **X** =
> horizontal east (walk axis); native **Y** (tile rows) = the gameplay VERTICAL (+y = down/deeper, gravity,
> the climb axis); the BDB per-sub-cell height nibble = **SLAB DEPTH into the screen** (the dig axis;
> engine z = nibble·0.5 + 7, range 7..14.5). When the older docs say "height/elevation" for the nibble, read
> **DEPTH**. This doc focuses on the dig natives; pathfinding lives in `ORIGINAL_PATHFINDING_RE.md`, the carve
> in `DIGGING_MECHANIC.md`.

---

## 0. Command → native map (verified via the profiler-label strings `"TCL:CTcl…Methods::Name"`)

| Tcl command | binding (CTcl…Methods wrapper) | worker | source |
|---|---|---|---|
| `dig_next $pos $gnome [$n] [$matchid]` | `FUN_00634b50` (DigNext) | **`FUN_00597b20`** | `00634b50:43,119` |
| `vector_fixdig $wall $gnome` | `FUN_00635610` (VectorFixDig) | **`FUN_00594e00`** | `00635610:41,120` |
| `vector_fix $pos` | `FUN_00635940` (VectorFix) | **`FUN_00595770`** | `00635940:40,88` |
| `get_digedge $pos $gnome` | `FUN_00635180` (GetDigEdge) | `FUN_005981d0` | `00635180:49,158` |
| `get_gnomeposition $obj` | **`FUN_0068d8a0`** (GetGnomePosition) | inline | `0068d8a0:35` |
| `dig_apply $pos $gnome` | **`FUN_006347a0`** (DigApply) | carve `FUN_00596b80` | `006347a0:42,119` |
| `get_hmap $x $y` | `FUN_0062faf0` (GetHMap) | `FUN_004093f0` | `0062faf0:41,129` |
| `dig_mark $owner $x $y $mode [$cnt]` | **`FUN_006586c0`** (DigMark) | `FUN_00566460/005671a0/5672d0/2f10` | `006586c0:47` |
| `is_dig_marked x1 y1 x2 y2` | (DigMark.cpp family) | UNRESOLVED native | `source_attribution:DigMark.cpp` |
| `get_material $pos` | **no profiler label** — UNRESOLVED native | reads cellId (`+0x14`) | §8 |

Helpers reused by several: tile lookup `FUN_00592f10`; rim helper `FUN_005968b0`; sub-cell read
`FUN_00407e70`; precondition `FUN_005976b0`; per-sub-cell height leaf `FUN_004093f0`.

---

## 1. Constants — exact values read from `Diggles.exe` `.rdata`

All values below were read **from the binary** (not inferred). `float` unless noted.

| DAT addr | value | role in the dig natives |
|---|---|---|
| `DAT_0073752c` | **8.0** | world→sub-cell scale (×8, round); ALSO the `dig_next` "+8 non-id penalty" |
| `DAT_0073703c` | **0.125** (1/8) | sub-cell→world output scale |
| `DAT_0073708c` | **0.5** | half-cell rounding bias (`+0.5` then ROUND) in dig_apply / get_digedge |
| `DAT_00737520` | **7.0** | Z base (`z = nibble·0.5 + 7`) |
| `DAT_007370c0` | **0.75** | `dig_next` Y anchor offset: `dy = subY − (gnomeY − 0.75)` |
| `DAT_007374e0` | **6.0** | `dig_next` Z/depth anchor offset: `dz = (rim+4) − (gnomeZ + 6.0)` |
| `DAT_00737058` | **0.25** | `dig_next` DEPTH-axis weight in the squared distance (×0.25) |
| `DAT_00737330` | **2.0** | `dig_next` output Z post-add (`z = (rim+4) + 2.0`, then clamp) |
| `DAT_00737138` | **1.0** | low clamp bound for `dig_next` Z |
| `DAT_00737550` | **14.0** | high clamp bound for `dig_next` Z |
| `DAT_00737564` | **14.5** | high clamp for vector_fixdig / vector_fix Z (`z ≤ 14.5`) |
| `DAT_00737348` | **2.1** (double) | spiral-snap "cost" rounding bias: `(int)(2·sqrt(d²) + 2.1)` |
| `DAT_0073702c` | 0.09 (0.3²) | arrival eps² (movement; not used by these natives) |
| `DAT_007370d0` | 0.7853982 (π/4) | turn-clip threshold (movement) |
| `DAT_007371c0` | 1.5 | budget clamp (Calc1) |
| `DAT_00736fcc` | 0.005 | precondition object-extent scale (`FUN_005976b0`) |
| `DAT_00736fe0` | 0.01 | precondition object-extent scale (`FUN_005976b0`) |

> **Correction to `ORIGINAL_PATHFINDING_RE.md` §6 / §9:** `dig_next`'s output Z is **`rim + 4 + 2.0`** (= rim+6),
> clamped **[1.0, 14.0]** (`DAT_00737330`=2.0, `DAT_00737138`=1.0, `DAT_00737550`=14.0) — NOT "rim+4". And its
> depth-axis weight is **0.25** (`DAT_00737058`), NOT 0.5. The 0.5 weight (`DAT_0073708c`) is the rounding bias,
> used elsewhere. The anchor offsets (Y −0.75, Z +6.0) were entirely missing from prior docs.

---

## 2. `dig_next` — `FUN_00597b20(out vec3, float* pos, int gnome, int n, char matchId)`

Picks the **n-th nearest dig-marked, precondition-passing voxel** to `pos`, weighting the depth axis lightly,
penalising nodes whose per-owner order id differs from the gnome's current dig-id. Returns the node as world `{x,y,z}`; if no
candidate (`param_4==0` short-circuit at the top, or none found), returns the sentinel `{-1,-1,-1}`
(`0xbf800000`=−1.0 each, `00597b20:276-279`).

### 2.1 Setup (`00597b20:54-117`)
```
CMap = DAT_0077241c
owner = *(int*)(gnome + 0x36c) // owner/player index (0..7):57
owner4 = owner * 4 // local_44:116
ownerMask = 0xB << owner4 // local_40 (0xB = 1011b):117
gnomeId = *(uint*)(gnome + 0x250) // local_30 (current dig id):115
// scan window (sub-cells), clamped to sub-cell bounds [+0x84..+0x90] with ±2 margins:
x0 = max(CMap[+0x84]+2, pos.x − 0x10) // local_3c (−16.. +16 in X):60,62-65
y0 = max(CMap[+0x88]+2, pos.y − 0x11) // local_14 (−17 in Y):59,66-69
x1 = min(CMap[+0x8c]−2, pos.x + 0x10) // local_34:61,70-73
y1 = min(CMap[+0x90]−2, pos.y + 0xf) // local_38 (+15 in Y):58,74-77
// the candidate output array (param_4 slots) is pre-filled with cost +inf (0x4b189680) and
// position −1 (0xbf800000); it is kept sorted ascending by cost (insertion via FUN_006d1b90 memmove).
```
The window is **asymmetric in Y** (−0x11..+0xf = −17..+15), i.e. biased to scan slightly more **above** (−y, toward
the open pocket) the gnome than below — consistent with digging the wall the dwarf faces.

### 2.2 Per-cell gate (`00597b20:126-133`) — the precondition
For each `(x,y)` in the window (`tile = FUN_00592f10(x,y)`):
1. `(*(uint*)(tile+8) & ownerMask) != 0` — this owner's dig nibble is set. `:127`
2. `(*(byte*)(tile+0x18) & 3) != 0` — precondition: not fully buried. `:128`
3. `(*(byte*)(tile+0x18) & 3) != 3` — precondition: not fully open. `:129` → together {1,2} = **diggable**.
4. `FUN_005968b0(&{x,y}) != -1` — a valid rim exists (the neighbour-block gate graph resolves). `:130`
5. `id = (*(uint*)(tile+0xc) >> owner4) & 0xf` — the per-owner 4-bit id at `tile+0x0c`. `:131-132`
6. **`matchId=='\0' || id == gnomeId`** — when the 5th arg is set, only same-id voxels qualify. `:133`

### 2.3 Weighted distance + id penalty (`00597b20:136-146`)
```
rim = FUN_005968b0(&{x,y}) //:136
dx = x − pos.x
dy = y − (pos.y − 0.75) // DAT_007370c0 = 0.75:137
dz = (rim + 4) − (pos.z + 6.0) // DAT_007374e0 = 6.0:138
cost = dx*dx + dy*dy + dz*dz * 0.25 // DAT_00737058 = 0.25:139-141
if (gnomeId != id) cost += 8.0 // DAT_0073752c = 8.0:144-145
// insertion-sort cost into the param_4-slot arrays (cost[], pos[3]); keep the cheapest param_4.:148-231
// stored candidate position = (x, y, rim+4):229-231
```
The depth axis (`dz`) is **down-weighted ×0.25**, so the picker prefers the voxel nearest in the (x,y) ground
plane and treats slab-depth difference as minor. The `+8` penalty pushes non-current-id voxels to the back so a
dwarf finishes its current vein before switching.

### 2.4 Output (`00597b20:245-262`)
Takes slot `param_4 − 1` (the array is cheapest-first and `n+1` slots are requested by the wrapper, see §2.5 —
so for the usual `n=0` the single slot 0 is the nearest):
```
out.x = stored.x //:249,260
out.y = stored.y //:250,261
z = stored.z (= rim+4) + 2.0 // DAT_00737330 = 2.0:251
if (z <= 1.0) z = 1.0 // DAT_00737138:253-254
else if (z >= 14.0) z = 14.0 // DAT_00737550:253-254
out.z = z //:262
```
**Net output Z = clamp(rim + 6.0, 1.0, 14.0).**

### 2.5 Wrapper `FUN_00634b50` (DigNext) — Tcl arg shape
- Accepts arg-count 3,4,5 (`(int)param_3 − 3U < 3`, `00634b50:66`).
- arg1 (`local_34`) = `$pos` parsed to a vec3 (`FUN_006bafe0`). `:87`
- arg2 = `$gnome` → `Obj_GetObjSafe(param_4,0,2)` movable. `:88`
- If argc==4: arg3 → int `n` (≥0, default 0 if negative). `:90-102` (n+1 slots requested: `FUN_00597b20(…, n+1, …)` `:119`)
- If argc==5: arg4 → bool `matchId`. `:16`
- Calls `FUN_00597b20(out, pos, gnome, n+1, matchId)`. `:119` Returns the vec3 (or `{-1,-1,-1}`).
- The Tcl validity test is `vector_unpacky > 0` / `[lindex $dp 0] > 0` (sentinel −1). (`single_Minimal.tcl`, `z_dignwalk.tcl:208`.)

---

## 3. `vector_fixdig` — `FUN_00594e00(out vec3, float* gnomePos, float* wallPos)`

Computes the **standoff** the dwarf walks to before digging a wall voxel: step ~1 unit off the wall toward the
dwarf, spiral-snap to the nearest WALKABLE sub-cell (`+0x3c & 1`), then push along +y while still on a wall
column, and finally set Z to the support-cell depth nibble +1.

> NOTE on arg order: the Tcl call is `vector_fixdig $edgepos [get_pos this]` (`z_dignwalk.tcl:198`), and the
> wrapper `FUN_00635610` passes **arg1=`$edgepos`→param_2, arg2=`get_pos`→param_3** (`00635610:120`
> `FUN_00594e00(out, &local_2c=arg1, local_38=arg2)`). Inside, `param_2` is the point being fixed (the edge/wall),
> `param_3` is the reference (the gnome). The "step toward the dwarf" uses `param_3` as the pull target.

### 3.1 Step off the wall (`00594e00:38-54`)
```
fx = param_2.x; fy = param_2.y // the wall/edge point
d2 = (fy − param_3.y)² + (fx − param_3.x)² // sq dist edge→gnome:41-42
if (d2 >= 1.0) { fx += (param_3.x − fx)/d2; fy += (param_3.y − fy)/d2 } // step ~1/√d2 toward gnome:44-46
else { fx = param_3.x; fy = param_3.y } // too close: use gnome:48-50
subX = round(fx * 8.0) // local_34:52
subY = round(fy * 8.0) → DAT_00772dd8 // local_44/uVar9:53-54
```
(`DAT_00737138`=1.0 is the `d2 >= 1.0` gate at `:44`.)

### 3.2 Spiral-snap to a walkable sub-cell (`00594e00:55-298`)
If the start sub-cell is **not** walkable (`(tile+0x3c[idx] & 1)==0`, `:65`), do an expanding square-ring spiral
(`local_3c = 1, 3, 5 … < 0x32=50`, `:55-295`), 4 arms per ring (the +x, +y, −x, −y passes, each tracking the
running offsets), and keep the **nearest** walkable cell by squared distance `local_40` (`:104,158,217,271`). The
"cost" tie-break uses `iVar5 = (int)(2·sqrt(d²) + 2.1)` (`DAT_00737348`=2.1, `:18` etc.) clamped down to the
running min — this biases toward the geometrically closest ring. `bVar1` = "found one". If nothing found,
`goto LAB_00595590` with the original cell (`:297`). The clamps use the tile/sub bounds `[+0x84..+0x90]`,
modulus `[+0x4c]`, count `[+0x18]` exactly as `FUN_00592f10`.

### 3.3 Climb-band nudge (`00594e00:299-331`)
From the snapped `(local_38, local_44)`, while the **floor bit** is *not* set yet, push **+y (down)** up to 8
cells looking for a `(tile+0x3c[idx] & 2) != 0` cell (`:327`). This walks down the wall column to the ground line.
Loop bound `(int)local_24 < 8` (`:331`).

### 3.4 Z from the support cell, +1, clamp ≤14.5 (`00595590:332-413`)
```
col = subX>>3; row = (snappedY − 5)>>3 // sample 5 sub-cells "up":334-335
blk = tile[col,row].block_idx; aux = blk*0x59 + BDB.base //:360-368
h = aux[ (snappedY−5 &7)<<3 | (subX&7) ] // per-sub-cell nibble:369
z = param_3.z (the requested z)
if (z <= h + 1) z = (other support row's nibble) + 1 //:371-405
out.x = local_38 * 0.125 // DAT_0073703c:407
out.y = local_44 * 0.125 //:408
out.z = min(z, 14.5) // DAT_00737564:409-413
```
So **out = (snappedSubX·0.125, snappedSubY·0.125, min(supportNibble+1, 14.5))** — a sub-cell-accurate floor point
one depth-unit in front of the wall.

The wrapper `FUN_00635610` returns this vec3 (or, if argc<2, sets a 1-element fallback `FUN_006bc8f0(1)` `:148`).

---

## 4. `vector_fix` — `FUN_00595770(out vec3, ?, float* pos, byte* okOut)` (worker of VectorFix `FUN_00635940`)

The **plain landscape snap** the Tcl `walk_pos`/`walk_pos_dig`/`walk_run`/`walk_near_item` wrap every target in
(`z_dignwalk.tcl:596,612,625,709,764`). It is `vector_fixdig` WITHOUT the step-off-wall math: it simply snaps
`pos` to the nearest WALKABLE sub-cell and sets Z to that cell's floor depth.

### 4.1 Body (`00595770`)
```
subX = round(pos.x * 8); subY = round(pos.y * 8) → DAT_00772dd8 //:33-35
*okOut = 0 //:36
// out-of-bounds guard: must be inside [+0x94..+0xa0] with a 0x30=48 sub-cell margin, else PASS THROUGH:
if (NOT (subX in [CMap+0x94+0x30.. CMap+0x9c−0x30] && subY in [+0x98+0x30.. +0xa0−0x30])):
        out = pos ; return                                                    // :39-42,282-285
if (subX <= 9 || subY <= 9): out = {0,0,0}; return // hard reject:43,277-280
if (start sub-cell walkable, +0x3c & 1): //:47
        *okOut = 1 ; out = {subX*0.125, subY*0.125, pos.z}                    // keep z         :270-275
else: // identical 4-arm spiral as vector_fixdig (rings 1,3,5 … < 0x30=48): //:48-254
        find nearest walkable sub-cell (local_28, local_2c), *okOut = 1
        h = FUN_004093f0(local_28, local_2c − 1) & 0xff                       // support nibble :256
        z = max(pos.z, h + 1)                                                 // :257-259
        z = min(z, 14.5)                                                      // DAT_00737564   :261-263
        out = {local_28*0.125, local_2c*0.125, z}                            // :265-267
```
Key differences vs `vector_fixdig`: (a) NO step-off-wall vector; (b) margin is **0x30=48** sub-cells (vs the
fixdig path's tighter clamp); (c) if the click is already walkable it KEEPS the original z; (d) the support
sample is at `subY − 1` (`FUN_004093f0(x, y−1)`). The spiral cost bias is the same `2·sqrt(d²)+2.1`
(`DAT_00737348`). The wrapper `FUN_00635940` ignores `okOut` and returns the vec3 (or `FUN_006bc8f0(1)` when
argc<2 — i.e. needs at least the pos arg, `:118`).

---

## 5. `get_gnomeposition` — `FUN_0068d8a0(this, interp, argc, objv)` → 0/1

Returns **0 = standing on the ground (floor-dig), 1 = on a wall (wall-dig)**; the Tcl uses it to pick the dig
ANIMATION and the dig direction (`z_dignwalk.tcl:710`, `drachenb.tcl`, `spinne.tcl`).

### 5.1 Body (`0068d8a0:58-98`)
```
movable = Obj_GetObjSafe(objv, 2, 1); if null → result 1 (treat as wall):58-61
subX = round(movable[+0x10] * 8.0) // pos.x → sub-cell (local_14? uVar3):63
subY = round(movable[+0x0c] * 8.0) → DAT_00772dd8:64-66
// NOTE the field order: +0x10 is X, +0x0c is Y (the movable stores y at +0x0c, x at +0x10).
// bounds: subY must be in (CMap+0x94.. CMap+0x9c) and subX in (CMap+0x98.. CMap+0xa0), strict ±1, else result 1:67-70
sx = subX & 7
// probe the FLOOR bit (+0x3c & 2) at THREE rows, in order subY+1, subY, subY−1:
if (tile(subX>>3, (subY+1)>>3)[+0x3c | ((subY+1&7)<<3 | sx)] & 2) → goto FLOOR-PATH (result via:92):86-88
if (tile(subX>>3, subY >>3)[+0x3c | ((subY &7)<<3 | sx)] & 2) → goto FLOOR-PATH:89-91
if (FUN_00407e70(subX, subY−1) & 2) → goto FLOOR-PATH:92-93
else: result = 0 → on a WALL... (see sign convention note):94-98
```

### 5.2 Sign convention — IMPORTANT (the worker returns the INVERSE of the Tcl value)
Read carefully: when any of the three `+0x3c & 2` (floor) probes **hits**, control goes to `LAB_0068da93`
(`:71-83`), which builds **`Tcl_NewIntObj(0)`** → the command result is **0**. When **none** hits (the `else`
branch `:84-98`), it does the extra cardinal checks (subY+1, subY, subY−1 against bit 2, and `FUN_00407e70(subX,
subY−1) & 2`, `:86-93`) and on the final fall-through builds **`Tcl_NewIntObj(1)`** (`:94-98`) → result **1**.

So: **floor bit present at the body sub-column ⇒ result 0 (floor); absent ⇒ result 1 (wall)** — matching the
Tcl contract (`if {[get_gnomeposition this]}` = "on a wall"). The three rows are **subY+1, subY, subY−1** (the
support row just below the anchor, the anchor row, the row above), probing bit **0x02 (floor surface)**.

> **Port note:** the current port (`DigglesTclEngine.cs:773-783`) probes the SAME three rows and the SAME bit,
> returning "0" when a floor bit is found — which is **correct**. The only fidelity gaps are (a) it reads the
> entity position from `e.Position.x/.y` rather than the movable's `+0x10`/`+0x0c` (equivalent if the port's
> Position maps x→x, y→y), and (b) it skips the strict in-bounds ±1 guard (returns "1"=wall when OOB in the
> original — the port returns "0" by default; minor).

---

## 6. `dig_apply` — `FUN_006347a0(this, interp, argc, objv)` → bool; carve `FUN_00596b80`

(Already covered in `DIGGING_MECHANIC.md` §2; reconfirmed and detailed for the rounding + layer + cosmetic
branch here.)

### 6.1 Wrapper (`006347a0:67-174`)
```
if (argc < 2): FUN_006bc8f0(1); return 1 (usage error):67-72
digObj = objv[1] (the $digpoint vector):84-92
FUN_006bafe0(&local_38) // parse digObj → (local_38=x?, local_34=y?):94
x = round(local_34 + 0.5) // DAT_0073708c = 0.5 → local_1c:95
y = round(local_38 + 0.5) → DAT_00772dd8 → local_20:96-98
gnome = Obj_GetObjSafe(objv, 0, 2) // the $gnome arg:99
if (gnome == 0): return 1:159-166
FUN_005f8080(y, x); FUN_005fd060(y, x) // lazy-init the dig-mark/dirty systems:103-104
// LOCAL-PLAYER cosmetic branch (only if the digging player == the local human)::18
cur = FUN_005b28d0; localOwner = DAT_00772409 ? *(cur+0x18): 0
if (*(int*)(DAT_00772400+100) == localOwner):
        layer = *(int*)(gnome + 0x36c)                                         :113
        FUN_0058a820(y, x, layer)                  // lazy CMapBeautifier        :116
        CMapBeautifier_ScanAtPos(y, x, layer)      // texture re-skin            :117
result = FUN_00596b80(&{y, x}, gnome) // THE CARVE:119
// SECOND cosmetic pass after the carve::120-133
if (localOwner-match): FUN_0058a820(...); FUN_0058ba00(...):127-132
return Tcl_NewBooleanObj(result):134-158
```
`FUN_005f8080`/`FUN_0058a820` are lazy singleton getters (`005f8080:19-27`, `0058a820:19-27`) — they construct
the dig-mark / beautifier systems on first use, NOT per-call work. **The carve + the boolean are the only
non-cosmetic effects; a headless sim can skip the `localOwner` branches.**

### 6.2 Carve `FUN_00596b80(int* pos, int gnome)` (recap of `DIGGING_MECHANIC.md` §2, offsets reconfirmed)
1. Bounds (+1 margin) + precondition `FUN_005976b0` (`:42-47`).
2. Center dig height from `tile[+8] >> (owner*4)`: nibble==0→15, bit8 clear→11(marked), else `(n&3)*4−1` (`:48-61`).
3. `rim = FUN_005968b0` (`:62`); only proceed if `centerHeight <= rim` (`:63`).
4. Re-resolve `block_idx` for center + the 4 directional neighbours via aux transition graph
   `aux[0x40/0x44/0x48/0x4c]`, gated by `aux[0x50/0x51/0x52/0x53] == rim` (`:64-119`); set `tile[+0x13] |= 1`.
5. Clear every owner's completed dig nibble below the rim (`:137-158`).
6. `tile[+0x250 on gnome] = (tile+0xc >> (owner*4))` (cache the dug-cell id back to the gnome) (`:232`); effects,
   regional re-mesh `FUN_005a3dd0/5fd0`, recompute hmap `FUN_004093f0` (`:159-256`).

Each call = **one elevation step**; the Tcl `dig_execute` loops it (`z_dignwalk.tcl:316`).

---

## 7. `dig_mark` / `is_dig_marked` — `FUN_006586c0` (DigMark)

Paints (or clears) the per-owner dig-order layer. **Tcl signature: `dig_mark $owner $x $y $mode [$cnt]`**
(`std_ai.tcl`: `dig_mark $owner $x $y 1`, `dig_mark $owner $x $y 0 $digh`, `dig_mark $owner $x $y 2`).

### 7.1 Arg parse (`006586c0:70-237`)
```
if (argc < 5): FUN_006bc8f0(4); return 1 (usage):70-74
arg1 = $owner → int local_14 (used as a count/owner; gate "local_14 < 7"):89-101
arg2 = $x → int local_18:103-135
arg3 = $y → int local_20:136-170
arg4 = $mode → int piVar7 (0 / 1 / 2; the branch selector):184-237
arg5 = $cnt → int uVar6 (optional; clamped [0,3] in mode 0):198-223
```
> NOTE the decompiler's variable reuse makes the *exact* mapping of arg1 (`local_14`) ambiguous between "owner"
> and "count", but the **branch selector is unambiguously arg4** (`piVar7`, the value compared to 0/1/2 at
> `:244,259,267`), and the spatial args are arg2/arg3. The Tcl always passes owner,x,y,mode[,cnt].

### 7.2 Bounds gate + dispatch (`006586c0:238-281`)
Requires `x in (CMap+0x84+1.. +0x8c−1)`, `y in (CMap+0x88+1.. +0x90−1)`, **and `local_14 < 7`** (`:240-243`),
then on `piVar7` (mode):
- **mode 0 (paint, default):** `cnt = clamp(arg5, 0, 3)`; `FUN_00566460(x,y,cnt,owner)` + `FUN_005671a0(x,y,cnt,owner)`
  — set the dig-order marker with a clamped strength. (`:244-258`)
- **mode 1 (paint dummy):** `FUN_00566460(x,y,x,owner)` + `FUN_005672d0(…)` — a variant paint (the AI's
  "mark this column" `dig_mark … 1`). (`:259-265`)
- **mode 2 (clear):** `FUN_00566460`; then **clear the dig nibble** for the owner's layer:
  `tile(x,y)[+8] &= ~(0xf << (owner<<2))` (`:266-278`). This is the `dig_mark … 2` "un-mark".
- Returns 0 on success, 1 on usage/oob. (`:280,297`)

The `FUN_00566460/005671a0/5672d0` painters live in `Map/DigMark.cpp` (`source_attribution`), and write a
separate per-owner order layer (not the tile's live dig nibble, except mode 2 which clears `tile+8`).

### 7.3 `is_dig_marked x1 y1 x2 y2`
Native UNRESOLVED here (not profiler-labeled; lives in the `DigMark.cpp` family — `FUN_005661c0` /
`FUN_00566460` / `FUN_00568410` are the candidates). From the **caller** (`Trigger_Schwefel.tcl`:
`is_dig_marked [xn-4] [yn-4] [xp+4] [yp+4]`) it is a **rectangle query → bool** "any cell in [x1,y1)..[x2,y2) is
dig-marked for the (implicit local) owner." The port already implements this shape.

---

## 8. `get_material` — UNRESOLVED native (contract from callers)

No `CTcl…Methods::GetMaterial` profiler label exists, and the command-name string is not an inline literal in
the decompile (the command table is a data array), so the exact worker address is **UNRESOLVED**. The contract
is fully constrained by the callers and the dig flow:

- `z_dignwalk.tcl:315`: `if {$laser || rand < $digcount * [get_material $digpoint] * 0.7}` — a **per-swing
  carve-probability multiplier**; larger = digs faster. So it returns a small **float ≥ 0**.
- `std_ai.tcl`: `set m [get_material $p]; if {$m==0 || $m==3} { set rock 1 }` — also used as a small **integer
  material class** (0 and 3 = rock). And `set cm [get_material $cp]` paired with `get_hmap` to map the world.

**INFERENCE (labeled):** `get_material` reads the tile's **L2M cellId** at `+0x14` (the scape/material field;
bits 5..7 = urw/swf/kris/lava per `DIGGING_MECHANIC.md` §2) and/or the BDB block material slot, mapping it to a
small class id / hardness factor. It is **read-only** and never gates legality (only swing probability). Exact
table UNRESOLVED — needs the command-table walk (find the registrar that binds the `"get_material"` string) or a
live probe. The port's stub (`GetMaterial(tx,ty)` returning a float) is contract-compatible; only the value table
is approximate.

---

## 9. Tcl ↔ native contract (what the unchanged dig scripts require)

| Tcl | native | returns / contract |
|---|---|---|
| `dig_next $pos $g [$n] [$id]` | `FUN_00597b20` | world `{x,y, clamp(rim+6,1,14)}`; `{-1,-1,-1}`=none; valid iff `lindex 0 > 0` |
| `vector_fixdig $edge $g` | `FUN_00594e00` | sub-cell-accurate standoff `{sx·.125, sy·.125, min(supportNibble+1,14.5)}` |
| `vector_fix $pos` | `FUN_00595770` | nearest-walkable snap (keeps z if already walkable; else `min(supportNibble+1,14.5)`); **pass-through if OOB** |
| `get_digedge $pos $g` | `FUN_005981d0` | nearest reachable floor sub-cell adjacent to diggable rock (the foot-of-cliff) |
| `get_gnomeposition $g` | `FUN_0068d8a0` | **0=floor, 1=wall** (floor bit `+0x3c & 2` at subY+1/subY/subY−1) |
| `dig_apply $pos $g` | `FUN_006347a0`/`00596b80` | bool: 1 = a voxel was carved this step; loops to excavate |
| `get_hmap $x $y` | `FUN_0062faf0`/`004093f0` | per-sub-cell nibble 0..15 (`round(x·8),round(y·8)`); **15 if OOB** |
| `dig_mark $owner $x $y $mode [$cnt]` | `FUN_006586c0` | mode 0 paint(clamp cnt [0,3]) / 1 paint-dummy / 2 clear; 0=ok |
| `is_dig_marked x1 y1 x2 y2` | DigMark.cpp | bool any-marked in rect |
| `get_material $pos` | UNRESOLVED | small float/int hardness/class (rock=0/3); swing-prob only |

---

## 10. PORT GAP analysis (vs `Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs`, current state)

| # | Native | Port state | Gap / fix |
|---|---|---|---|
| G1 | **`dig_next` Z** | `DigglesTclEngine.cs:823` returns `clamp(RimHeight+4, 1, 14)` | **Off by the +2.0**: original is `clamp(rim + 4 + 2.0, 1.0, 14.0)` = clamp(rim+6,1,14) (`00597b20:251`, `DAT_00737330`=2.0). Fix the constant. |
| G2 | **`dig_next` picker** | `PickDiggableCell(cx,cy,tryN,…)` (terrain-side; uses simple distance, no anchor offsets / weight / id penalty) | Reproduce: window x±16 / y(−17..+15), gate `tile+8 & (0xB<<(owner*4))` + precondition `+0x18&3∈{1,2}` + valid rim; cost `dx²+dy²+0.25·dz²` with `dy=subY−(gy−0.75)`, `dz=(rim+4)−(gz+6.0)`; `+8.0` if id≠gnomeId; keep n-th nearest. Anchor offsets (0.75/6.0) and the 0.25 weight are currently absent. |
| G3 | **`vector_fixdig`** | `:857-878` steps `+dir` (1 world unit) then `TrySnapToSub(…16, SubWalk)`, Z = `min(GetHmapSub+1,14)` | Close. Two fidelity gaps: (a) original step is `±(target−p)/d2` (scaled by `1/d2`, only when `d2≥1`), not a unit normalize; (b) missing the **+y climb-band nudge** (`00594e00:299-331`) and the **subY−5 support sample**; (c) clamp is **14.5** not 14 (`DAT_00737564`). |
| G4 | **`vector_fix`** | `:947` **identity passthrough** (`a[1]` unchanged) | Should snap to the nearest walkable sub-cell with the OOB/margin (0x30) and z=`min(supportNibble+1,14.5)` rules (`FUN_00595770`). The identity stub means `walk_pos`/`walk_near_item` targets are NOT landscape-snapped → off-mesh walk targets. Implement the real snap. |
| G5 | **`get_gnomeposition`** | `:773-783` probes subY+1/subY/subY−1 for `SubFloor`, returns 0 if any | **Correct** (matches `0068d8a0` row order + bit). Minor: add the OOB→"1" guard; source x/y from the movable fields. |
| G6 | **`dig_mark` args** | `:839-846` reads `cnt=a[1]` (= **owner!**), `x=a[2]`, `y=a[3]`, `id=a[5]`, and branches on `id==2` | **Arg mismap.** Original: a[1]=owner, a[2]=x, a[3]=y, **a[4]=mode** (0/1/2), a[5]=cnt. The port treats a[1] as count and a[4] (the real mode) is ignored; it keys "clear" off a[5]==2. Fix: read owner=a[1], mode=a[4], cnt=a[5] (clamp [0,3] in mode 0). |
| G7 | **`dig_apply` rounding** | `:836` `WorldToTile(ParseVec3)` | Original rounds **`x=round(py+0.5)`, `y=round(px+0.5)`** with the X/Y swap (`006347a0:95-98` reads digObj.y→x, digObj.x→y) and the +0.5 bias. Verify `WorldToTile` applies the +0.5 and the axis order. |
| G8 | **`get_digedge` Z** | `:813` `GetHmapSub(fx,fy)` for the snapped floor cell | OK in spirit; confirm the worker `FUN_005981d0` standoff matches (it is itself partly Ghidra-mangled — see `ORIGINAL_PATHFINDING_RE.md` §6 / unresolved). |
| G9 | **`get_material`** | `:794-799` `GetMaterial(tx,ty)` float | Contract-compatible stub; value table approximate (native UNRESOLVED). Low priority (swing-prob only). |
| G10 | **`is_dig_marked`** | `:848-855` rect scan over `IsDigMarked` | Shape correct. |

**Highest-impact fixes:** G4 (vector_fix identity = un-snapped walk targets), G6 (dig_mark arg mismap = AI
dig-order paint wrong), G1/G2 (dig_next Z + picker = the dwarf aims at the wrong depth / wrong voxel order).

---

## 11. UNRESOLVED

1. **`get_material` native** — no profiler label; command-table-bound. Find the registrar that maps the
   `"get_material"` string to its FUN, or live-probe. The value table (cellId/material → hardness) is
   INFERENCE only. (§8)
2. **`is_dig_marked` native** — in `Map/DigMark.cpp` (`FUN_005661c0`/`00566460`/`00568410`); not individually
   decompiled here. The rect-query contract is from the caller. (§7.3)
3. **`get_digedge` worker `FUN_005981d0`** — Ghidra collapsed ~70 blocks (per `ORIGINAL_PATHFINDING_RE.md`
   §10.6); the BFS / `tile[0x34+layer]` gate is reconstructed, not 100%-byte-confirmed. Re-decompile to verify.
4. **`dig_mark` arg1 (`local_14`) exact role** — owner vs a secondary count is ambiguous in the decompile
   (variable reuse); the mode selector (arg4) and spatial args (arg2/arg3) are unambiguous. The `local_14 < 7`
   gate suggests it is the OWNER index (≤7 players). (§7.1)
5. **`dig_next` slot semantics for `n>0`** — output reads slot `param_4−1` of an ascending-cost array of size
   `n+1`; for `n=0` this is the nearest. For `n>0` it returns the n-th nearest (the array holds the n+1 cheapest,
   slot n is the worst-of-best). Confirmed structurally; no caller in the campaign uses `n>1`.
6. **`FUN_005f8080` / `FUN_0058a820` payloads** — confirmed as lazy-singleton getters (dig-mark system /
   CMapBeautifier); their internals are cosmetic/order-layer and out of scope for a headless carve.
