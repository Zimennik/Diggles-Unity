> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

> **Correction** every `layer` label associated with `obj+0x36c` below means
> the player/owner index. The eight nibbles are parallel owner orders, not spatial layers.

> **Port update (`stairs-nav`).** The historical PORT GAP snapshot in §8 is
> superseded. `DiggableTerrain.TryFindDigEdge` now enforces the exact visible shell:
> one-tile inset (`005981d0:84-90`), seed owner-nibble bit 2 (`:92-98`), failure
> `{-1,-1}`, and Tcl Z=14 (`00635180:158-161`). The dropped interior is implemented with
> the explicitly inference-backed §4.4 contract: reusable-array four-neighbour BFS through
> diggable rock, exhaust the reachable frontier, and choose the value-3 floor cell globally
> nearest the dwarf (with distance zero as the only early exit). It never returns the
> seed/rock fallback. The exact interior gate and distance metric remain U1/U2/U4 risks;
> `DigEdgeBfsTests` pins the chosen behavior.
> The paired walk honors `-dig 1` by skipping the port's post-relax floor settle
> (`FUN_00420840:1063-1080,1901-1919`).
> Round-3 reference packing is literal at both callers: Tcl uses
> `ROUND(obj[+0xc/+0x10] + 0.5)` (`FUN_00635180:153-158`), and the animator uses its
> integral scanned tile as both seed and ref (`FUN_0043bdd0:241-245`). Since the port's
> candidate loop addresses an 8x8 sub-cell raster, those tile integers map to the tile
> origin, not to a raw fractional object sub-cell or an invented `+4` tile centre.

# dig-edge — `get_digedge` (FUN_005981d0) and its Tcl wrapper (FUN_00635180)

**Area key:** `dig-edge`
**Status:** Reconstructed. The core search loop of `FUN_005981d0` was **dropped by Ghidra** (≈70 "unreachable block" warnings; body 1510 B / 0x5E6, only ~80 B of prologue/epilogue survive). The contract (inputs, output packing, validity sentinel, the dig-mark write, the layer gate) is **fully pinned** from the two callers and the seed-enqueue that DID survive. The interior frontier-scan body is **reconstructed by INFERENCE** from the surviving seed-enqueue + the byte-for-byte twin search in the fully-decompiled `FUN_00597b20` (`dig_next`) and `FUN_0043bdd0` (the dig-spark animator that also calls `get_digedge`). Every gap is marked.

---

## 1. Overview

`get_digedge $startpos $obj` (Tcl) answers: *"given a dig target tile `$startpos` and a digging dwarf `$obj`, where should the dwarf STAND to begin digging?"* It returns a standoff cell `{x y z}` and the caller validates it with `[vector_unpacky $edgepos] > 0` (the Y component must be strictly positive — both X and Y are −1 on failure).

Caller chain (Tcl → native):
- `dig_starttask` (`z_dignwalk.tcl:144`): `set edgepos [get_digedge $startpos this]`
  → native wrapper **`FUN_00635180`** (`"TCL:CTclGlobalMethods::GetDigEdge"`, string at `00635180:49`)
  → worker **`FUN_005981d0`**.

The same worker is ALSO called from **`FUN_0043bdd0`** (`0043bdd0:245`), the per-frame dig-spark/marker animator, where the call site is *fully* decompiled and nails the return packing.

`get_digedge` is one of four sub-cell dig-geometry primitives. Its siblings: `get_hmap`=`FUN_004093f0`, `dig_next`=`FUN_00597b20`, `vector_fixdig`=`FUN_00594e00`, plus the rim helper `FUN_005968b0` and the dig-precondition `FUN_005976b0`. The actual carve is `FUN_00596b80`.

---

## 2. The world / tile data model (confirmed, used by every function here)

`DAT_0077241c` = the **world/map singleton** (213 references across `_global`). Field offsets, all confirmed by `FUN_00592f10` (the tile lookup) and `FUN_00596b80` (the carve):

| Off | Meaning | Evidence |
|---|---|---|
| +0x14 | tile-array base pointer | `00592f10:29` `iVar2*0x7c + *(p+0x14)` |
| +0x18 | tile count (for bounds trap) | `00592f10:28` |
| +0x44 | row stride (tiles per row) | `00592f10:27` `iVar1 * (p+0x44)` |
| +0x4c | wrap modulus (= W·H) | `00592f10:27` `% (p+0x4c)` |
| +0x84 | **min X** (world-left inclusive) | `00592f10:13`, `005981d0:84` |
| +0x88 | **min Y** | `00592f10:20`, `005981d0:86` |
| +0x8c | **max X** (exclusive-ish, code uses `-1`) | `00592f10:16`, `005981d0:85` |
| +0x90 | **max Y** | `00592f10:23`, `005981d0:87` |
| +0x24 | BDB block-DB object (`rim`/carve read it) | `005968b0:24`, `00596b80:65` |

**Tile record = 0x7c (124) bytes** — matches the live dump `tile_array.bin` 124-B stride. Per-tile fields touched in this area:

| Tile off | Type | Meaning | Reader |
|---|---|---|---|
| +0x00 | i32 | `block_idx` → BDB cell (`*0x59`) | `005968b0:25`, `004093f0:16` |
| **+0x08** | u32 | **8 owner dig nibbles**; owner L bits = `0xf << (L*4)` | `005981d0:98` (write), `0043bdd0:174/240`, `00596b80:49/155/282` |
| +0x0c | u32 | 8 per-owner dig-order ID nibbles | `00596b80:139/232` |
| +0x13 | u8 | dirty/touched bits (`\|=1`, `\|=2`) | `00596b80:123/180` |
| +0x18 | u8 | dig **precondition** (low 2 bits: 0=buried, 3=fully-open, 1/2=exposed=diggable) | `005976b0:36/38`, `00597b20:128` |
| +0x34..0x3b | 8×u8 | per-owner flag array `[0x34+owner]` (the prior doc's "owner gate") | (not read inside get_digedge's surviving code — see §6 gap) |
| +0x3c..0x7b | 64 B | 8×8 sub-cell passability, idx `(y&7)<<3\|(x&7)`; bit1=walk,2=floor,4=water,8=climb | `004093f0` (but `004093f0` here reads the **height** aux, see note) |

**BDB cell = 0x59 (89) bytes**, indexed `block_idx*0x59 + *(BDB+0x10)` (`005968b0:32`, `00596b80:72`). Fields used by the rim helper:
- +0x40/+0x44/+0x48/+0x4c = i32 neighbour/link block indices (corner remap)
- +0x50/+0x51/+0x52/+0x53 = u8 **corner gate / rim heights** (`005968b0:53/84/115/136`)
- the 64-byte aux height/passability table the prior docs call the "sub-grid" lives in this 0x59 block too (`004093f0:25` reads `*((subY&7)<<3 | subX&7) + cell)`).

> NOTE on `FUN_004093f0` (=`get_hmap`): it indexes `((param_3&7)<<3 | param_2&7) + cell` where `cell = block_idx*0x59 + BDB_base` — i.e. it returns the **byte at sub-index of the BDB cell**. Per the project's ground-truth frame this byte is the **slab DEPTH nibble** (0..15), not an elevation. `get_hmap` inputs are `world>>3`? No — `FUN_004093f0(this, sx, sy)` takes the sub-cell coords directly and does `sx>>3, sy>>3` to get the tile then `(sy&7)<<3|(sx&7)` for the sub-index (`004093f0:14,25`).

---

## 3. `FUN_00592f10` — the tile lookup (clamping) — FULLY DECOMPILED

`int FUN_00592f10(world /*this=DAT_0077241c*/, int x, int y) -> tileRecPtr`

```c
x = clamp(x, world[+0x84], world[+0x8c]-1) // 00592f10:13-19 (max via -1)
y = clamp(y, world[+0x88], world[+0x90]-1) // 00592f10:20-26
idx = (y * world[+0x44] + x) % world[+0x4c] // 00592f10:27 row-major, wrapped
if (0 <= idx < world[+0x18]) return idx*0x7c + world[+0x14] // 00592f10:28-30
RaiseException(0xC000008C) // bounds trap; returns world[+0x14] (tile 0):31-32
```

Note: the parameter order in the decompile is `(this, y, x)` named, but the body clamps `param_2` against the X bounds (+0x84/+0x8c) and `param_3` against Y (+0x88/+0x90), so effectively `(this, X, Y)`. **Tiles are addressed row-major as `(Y*stride + X)`.** This is the single primitive every dig function uses to turn an `(x,y)` into a tile record.

---

## 4. `FUN_005981d0` = `get_digedge` worker

### 4.1 Signature & call sites (CONFIRMED)

```c
// Ghidra-decompiled signature (VERBATIM 005981d0:73):
// undefined4 *FUN_005981d0(undefined4 *param_1, int *param_2, undefined4 param_3, byte param_4)
// NB param_3 is typed `undefined4` (scalar) by Ghidra because the worker body that would
// dereference it was dropped; both call sites pass an ADDRESS (&local_3c), so semantically:
undefined4 *FUN_005981d0(int *out /*param_1*/, // -> packed {X,Y} result, returned
                         int *seed /*param_2*/,   // {X, Y} = the DIG TARGET tile
                         int *ref  /*param_3, typed undefined4*/, // &{refX,refY}=DWARF pos (INFERENCE: ptr)
                         byte owner /*param_4*/); // owner/player index 0..7
```

- Wrapper `FUN_00635180:158`: `FUN_005981d0(&local_2c, &local_34, &local_3c, *(iVar4+0x36c))`
  - `local_34 = ROUND(startpos.X)`, `local_30(=&local_34[1]) = ROUND(startpos.Y)` → **seed = startpos** (`635180:155-157`)
  - `local_3c = ROUND(dwarf.y+0.5)`, `local_38 = ROUND(dwarf.z+0.5)` → **ref = dwarf pos** (`635180:153-154`)
  - `*(iVar4+0x36c)` = the dwarf object's **owner/player index** → `owner`
- Animator `FUN_0043bdd0:241-245` (VERBATIM): `local_38=local_8; local_40=local_8; local_3c=local_10; local_44=local_10;` then `FUN_005981d0(local_4c,&local_44,&local_3c,local_1c)`. So **seed = {local_44=local_10, local_40=local_8}** and **ref = {local_3c=local_10, local_38=local_8}** → seed == ref == the scanned tile (local_10=tile X from `+0x307c`, local_8=tile Y from `+0x3080`). `local_1c` = `*(param_1+4)` the player's slot/owner index (`0043bdd0:54`).

### 4.2 Return packing & validity (CONFIRMED from BOTH callers)

`out[0] = resultX (i32)`, `out[1] = resultY (i32)`. Returns `out`.

```c
// FUN_0043bdd0:245-248 (fully decompiled — authoritative)
piVar11 = FUN_005981d0(local_4c, &local_44, &local_3c, local_1c);
rx = piVar11[0]; ry = piVar11[1];
if (rx > 0 && ry > 0) {...use it as a standoff... } // VALIDITY = X>0 && Y>0
```

On failure both words are written `0xFFFFFFFF` (= −1). The surviving epilogue of `005981d0` writes `out[0]=out[1]=0xFFFFFFFF` on **both** visible branches (`005981d0:88-89` and `:99-100`) — i.e. **the success path that writes a positive {X,Y} is entirely inside the blocks Ghidra dropped.** This is consistent with the Tcl test `[vector_unpacky $edgepos] > 0` (`z_dignwalk.tcl:147`): Y>0 ⇔ a valid edge was found.

### 4.3 What survived (the reachable shell) — VERBATIM

```c
undefined4 *FUN_005981d0(int *out, int *seed, undefined4 ref, byte layer) {
  // SEH frame setup (puStack_18=&LAB_00706b0d)... 005981d0:81-83
  if (seed[0] < world[+0x84] + 1 // X <= minX (1-tile inset)
      || seed[0] >= world[+0x8c] - 1            // X >= maxX-1
      || seed[1] <  world[+0x88] + 1            // Y <= minY
      || seed[1] >= world[+0x90] - 1 ) {        // Y >= maxY-1     005981d0:84-87
      out[0] = 0xFFFFFFFF; out[1] = 0xFFFFFFFF; // OUT OF BOUNDS -> invalid   :88-89
  } else {
      local_14 = 0;
      FUN_0041fb80(1);                          // VERBATIM: only arg `1` shown :93
                                                //  (thiscall — array `this`/ECX
                                                //   not resolved by Ghidra)
      RaiseException(0xc000008c,1,0,0);         // VERBATIM at :94 — bounds-check
                                                //  NORETURN model, NOT a real throw
      iRamfffffff8 = *param_2;                  // VERBATIM :95  (see note ‡)
      iRamfffffffc = param_2[1];                // VERBATIM :96  (see note ‡)
      iVar1 = FUN_00592f10(*param_2,param_2[1]);//                              :97
      *(uint*)(iVar1+8) |= 4 << ((param_4&7)<<2);// SET dig-edge nibble bit2     :98
      out[0] = 0xFFFFFFFF; out[1] = 0xFFFFFFFF; // <-- placeholder; real result :99-100
  } // overwritten by dropped body
  return out; //:103
}
```

> **‡ NOTE on:95-96 (CORRECTED).** Ghidra emits `iRamfffffff8 = *param_2;`
> / `iRamfffffffc = param_2[1];` — i.e. writes to the *absolute addresses* `0xfffffff8`
> and `0xfffffffc`. Those are **base-pointer-loss artifacts** (Ghidra lost the FastArray
> element base register after the `Append`), NOT literal writes to −8/−4. The
> *interpretation* that these two writes store `seed.x`/`seed.y` into the freshly-appended
> frontier slot is **INFERENCE** (highly plausible — they immediately follow `Append(1)`
> and read exactly `seed[0]`/`seed[1]`), but it is **not directly readable** from the
> surviving bytes. Do not present "appends one slot and stores seed.x/seed.y" as proven.

Three hard facts from this shell:
1. **`FUN_0041fb80` is `DSFastArray::Append(n)`** — string `"C:\dev\wiggles\CodeLib\StdTools\KStdLib\Include\DSFastArray.h":0x9b` at `0041fb80:17`. It grows the array `param_1` by `param_2` 8-byte elements and zero-inits them (`0041fb80:24-32`). Element size 8 = a packed `{int x, int y}`. So `get_digedge` builds a **FastArray frontier of {x,y} cells**, seeded with the dig target → this is a **BFS/flood-fill**, not a fixed ring scan.
2. **The dig-mark write `tile[+0x08] |= 4<<(owner*4)`** stamps **bit value 4** of the seed tile's owner nibble. The carve `FUN_00596b80` and the animator `FUN_0043bdd0` later read this nibble with `tile[+0x08] >> (owner*4) & 0xf` (`00596b80:49`, `0043bdd0:174`). So `get_digedge` *registers* the target as a pending dig for that owner as a side effect.
3. The bounds gate is a **1-tile inset** of the world rect (`+1` / `-1`), so the result is always ≥1 tile from the world edge.

### 4.4 The dropped search body — RECONSTRUCTION (INFERENCE)

The 70 removed blocks span `0x5982eb`–`0x598785` (≈1.2 KB of code). Their shape is dictated by (a) the FastArray frontier that the shell seeds, (b) the nearest-to-`ref` requirement implied by `param_3`=dwarf pos, and (c) the byte-identical neighbouring search in the fully-decompiled twins. **INFERENCE** (reconstructed, not confirmed):

```
frontier = [ {seed.x, seed.y} ] // a DSFastArray of {x,y}, seeded above
visited = {seed} // (a parallel marked set / the +0x13 bit, cf 00596b80:123)
best = -1; bestDist = +inf
while frontier not empty:
    cur = frontier.pop()                   // BFS order (queue) — appends at tail (0041fb80 grows tail)
    for each 4-neighbour n of cur:         // E/W/N/S — cf the 4 directional reads in 005968b0
        if n out of the 1-tile-inset bounds: continue
        if n already visited: continue
        t = FUN_00592f10(n.x, n.y)
        // GATE: is n a STANDABLE floor cell the dwarf can occupy?
        //   (uses the +0x3c sub passability / the derived floor flag; see §6 gap)
        if n is a walkable FLOOR cell:
            // candidate standoff: keep the one closest to ref (= dwarf pos)
            d = |n.x-ref.x| + |n.y-ref.y|        // (the twins use L1 weighted x0.5 on the depth axis)
            if d < bestDist: bestDist = d; best = n
            // do NOT expand through floor — we want the rim
        else if n is solid/diggable rock (precondition exposed):
            frontier.append(n); visited.add(n)   // keep flooding through rock toward open cells
best ? out = {best.x, best.y}: out = {-1,-1}
```

Confidence notes:
- **Frontier = BFS via DSFastArray**: HIGH (the seed enqueue is in surviving bytes; append-at-tail = queue).
- **4-neighbour expansion**: MEDIUM-HIGH (every sibling here — `005968b0`, `00596b80`, `00597b20` — works in the 4 axis directions and uses the `+0x40..0x4c` corner links; `0043bdd0`'s post-pass uses an L1 distance `<5` / `<8` / `<0x14` test).
- **"nearest standable cell to the dwarf"**: HIGH that the result is a *floor standoff near the dwarf*, because (a) `param_3` is the dwarf position and is otherwise unused, and (b) `0043bdd0:266-277` rejects the returned edge unless it is within L1<0x14 of an existing spark and re-files it — i.e. it treats the result as a reachable floor point, not a wall vertex.
- **The exact gate bits** (does it test `+0x3c & 2` floor, the derived `+0x12 & 0x40` floor-tile flag, or `+0x34+owner`): **UNRESOLVED** — see §6.

### 4.5 SHAFT-BOTTOM dig — does it return a cell ON the dig tile?

For a vertical shaft being deepened straight down, the dig target `$startpos` is a rock voxel directly *below* the dwarf. The dwarf already stands on the shaft floor adjacent to it. The seed is the rock tile; the BFS immediately steps to its neighbours; the **nearest standable floor cell is the dwarf's own tile (or the one beside the rock)** → `get_digedge` returns the **floor cell at the foot of the rock, NOT the rock tile itself and NOT the wall vertex above it.** **INFERENCE (HIGH):** it never returns the solid dig tile — the dig tile fails the "standable floor" gate, so it can only ever be enqueued for expansion, not chosen as `best`. The standoff is therefore always a walkable cell, which is exactly why the Tcl then does `vector_fixdig` (`z_dignwalk.tcl:149`) to refine it 1 unit off the wall and `walk_pos_dig` to that cell.

---

## 5. The Tcl wrapper `FUN_00635180` — input parse & output Z-clamp (FULLY DECOMPILED)

```c
// FUN_00635180 "TCL:CTclGlobalMethods::GetDigEdge"
arg0 = $startpos (Tcl list) -> Tcl_ListObjIndex 0/1/2 -> startpos.X(local_18), startpos.Y(local_14) (635180:19)
obj = Obj_GetObjSafe(param_4, 3, 2) // the $this dwarf object 635180:150
if (obj == 0) -> Tcl result error path, return 1 635180:188-203
seed.X = ROUND(startpos.X) // DAT_00772dd8 = ROUND(local_18); local_34 635180:156-157
seed.Y = ROUND(startpos.Y) // local_30 = ROUND(local_14) 635180:155
ref.X = ROUND(obj.y + 0.5) // local_3c = ROUND(*(obj+0xc)+DAT_0073708c) 635180:154
ref.Y = ROUND(obj.z + 0.5) // local_38 = ROUND(*(obj+0x10)+DAT_0073708c) 635180:153
owner = *(obj + 0x36c) // dwarf owner/player index 635180:158
FUN_005981d0(&out, &seed, &ref, layer);
local_40 = (float)out[0] // resultX (out=&local_2c, so out[0]=local_2c) 635180:159
local_38 = 0x41600000 // = 14.0f -- HARD-CODED Z (3rd component) 635180:160
local_3c = (float)out[1] // resultY (out[1]=local_28) 635180:161
// list built from &local_40 = 3 consecutive floats {local_40, local_3c, local_38}:
list = FUN_006badc0(&{resultX, resultY, 14.0f}) -> Tcl_SetObjResult 635180:162-169
// ^X ^Y ^Z=14.0 (NB: Z=14 is the 3rd slot, NOT the 2nd)
```

**Critical: the wrapper forces the returned Z to the constant `0x41600000` = 14.0f** (`635180:160`). `get_digedge` itself only ever produces an `{X,Y}` pair (§4.2); the engine z is supplied by the wrapper as **14.0** = the top floor-line surface (the project's "ground line"; depth nibble 15→ engine z 14.5, but the standoff floor sits at 14.0). The Tcl then does `set originalz [lindex $edgepos 2]` (`z_dignwalk.tcl:148`) → `originalz = 14.0` and re-applies it after `vector_fixdig` (`lrep walkpos 2 $originalz`, `:150`). So the dwarf walks to the edge at **Z=14 (the floor line)**.

`DAT_0073708c` = **0.5** (the +0.5 round-to-nearest bias; same symbol is the `*0.5` slab-depth multiplier in `0043bdd0:117/139` → `(nibble)*0.5 + 7.0`). INFERENCE (HIGH).

---

## 6. Tcl ⇄ native contract (summary)

| Tcl | Native | In | Out |
|---|---|---|---|
| `get_digedge $startpos $obj` | `FUN_00635180`→`FUN_005981d0` | `$startpos`={x y z} dig target (X,Y rounded); `$obj`=dwarf (→ ref pos & layer `+0x36c`) | `{X Y 14.0}`; `vector_unpacky>0` (Y>0) = valid; `{-1 -1 14.0}`... actually `{-1 -1}` + Z=14 on fail → Y=−1 ⇒ Tcl bails |

Downstream Tcl (`z_dignwalk.tcl:147-160`):
```tcl
if {[vector_unpacky $edgepos] > 0} {
    set originalz [lindex $edgepos 2]            ;# = 14.0 (wrapper constant)
    set walkpos   [vector_fixdig $edgepos [get_pos this]]   ;# 1 unit off wall, snap to walkable sub
    lrep walkpos 2 $originalz                    ;# restore Z=14
    tasklist_add this "walk_pos_dig {$walkpos} {$edgepos}"
    tasklist_add this "dig_continue"
    return true
} else { return false };# no edge -> abort the dig task
```

Side effect: `get_digedge` stamps `tile[+0x08] |= 4<<(owner*4)` on the **target** tile (`005981d0:98`), registering it as a pending dig for that owner; the carve loop `FUN_00596b80` and the spark animator `FUN_0043bdd0` consume that nibble.

---

## 7. Interaction with neighbour systems

- **`FUN_00592f10`** (tile lookup) — used for every `(x,y)→tile`; row-major `(Y*stride+X)`, clamped, wrapped (§3).
- **`FUN_005968b0`** (rim/corner) — returns `max(aux[0x50..0x53])` over the 4 BDB corner gate bytes of the cell at `(x-1,y-1),(x,y-1),(x-1,y),(x,y)`, honouring the `+0x40..0x4c` corner-link remap; returns −1 if no corner present (`005968b0:137-138`). This is the **dig rim height** (used by `dig_next` Z = rim+4). `get_digedge` does **not** appear to call it in its surviving shell, but the dropped body may (gap §8).
- **`FUN_005976b0`** (precondition) — full do/while at `005976b0:48-153`; returns **1** if a reachable wall-vertex within ±0x10 of the target satisfies the open/exposed precondition (`tile[+0x18]&3 ∈ {1,2}`, i.e. `!=0 && !=3`, `005976b0:36-38`) AND the per-cell geometry matches the dwarf row; **0** otherwise. This is the diggability test; the dropped body of `get_digedge` likely uses the same `+0x18 & 3` exposed-rock test to decide which neighbours to flood through.
- **`FUN_00597b20`** (`dig_next`) — the twin: a bounded box scan `[target±0x10]` (`00597b20:62-77`) selecting the min-cost dig node whose `tile[+0x08]&(0xb<<(owner*4))` dig-bits are set (`00597b20:117` `0xb` mask) AND `tile[+0x18]&3 ∈ {1,2}` (`:128-129`) AND rim present (`FUN_005968b0 != -1`, `:130`); cost = squared XY distance + depth·0.5² (`:140-141`); returns `{x, y, rim+4 clamped}` (`:251-262`). Confirms the shared search idiom and the `+0x18&3` gate.
- **`FUN_00596b80`** (carve) — reads the dig nibble, computes the 4 neighbour block remaps via the BDB `+0x40..0x4c` links + `+0x50..0x53` corner heights, writes them back, sets `tile[+0x13]|=1/2`, clears the nibble when done (`00596b80:155/282`), spawns the dig effect. The consumer of `get_digedge`'s mark.
- **`FUN_0043bdd0`** (dig-spark animator) — second caller; iterates the player's marked tiles, calls `get_digedge` per tile, re-files the returned standoff into a {x,y,z,w} 0x10-byte spark FastArray at `player+0x3074` (count `+0x3078`), decays spark life by `DAT_00737138` each frame.

---

## 8. PORT GAP analysis vs current code

Current port: `DigglesTclEngine.cs:800-815` (`get_digedge`) + `TrySnapToSub` (`:2876-2890`) + `FindNearestDiggableEdge` / `GetHmapSub` on the terrain.

```csharp
// DigglesTclEngine.cs:800-815 (current)
var (sx, sy) = T.WorldToTile(ParseVec3(a[1]));
if (!T.FindNearestDiggableEdge(sx, sy, owner, out int ex, out int ey)) return "-1 -1 -1";
const byte FloorBits = SubWalk | SubFloor; // value 3
if (TrySnapToSub(nav, ex*8+4, ey*8+4, 24, FloorBits, out fx, out fy))
    return FormatVec3(new Vector3(fx*0.125f, fy*0.125f, nav.GetHmapSub(fx,fy)));
return FormatVec3(new Vector3(ex, ey, T.HmapToZ(ex, ey)));
```

Gaps vs the RE:

1. **Z must be the constant 14.0, not a per-cell height.** The original wrapper *hard-codes* `resultZ = 14.0f` (`635180:160`) and Tcl preserves it as `originalz`. The port returns `nav.GetHmapSub(fx,fy)` (a per-sub-cell depth nibble) or `HmapToZ`. **Fix:** return Z=14.0 for the standoff (the floor line). The depth refinement happens later in `vector_fixdig`, and even there Tcl restores Z=14 via `lrep walkpos 2 $originalz`. This is the single most load-bearing discrepancy: a wrong Z parks the walk target off the floor line → edge jitter.

2. **`ref` = dwarf position is ignored.** The original picks the floor cell **nearest the DWARF** (`param_3`=dwarf pos). The port's `FindNearestDiggableEdge(sx,sy,...)` searches from the dig target only and never takes the dwarf's position, so on a multi-cell rock face it can pick a far standoff. **Fix:** thread the dwarf pos in and break ties / choose `best` by L1 distance to the dwarf (depth axis weighted ×0.5, cf `00597b20:141`).

3. **The dig-mark side effect is not replicated here.** The original stamps `tile[+0x08] |= 4<<(owner*4)` (`005981d0:98`). The port marks via the separate `dig_mark`/`SetDigMark` path (`:839-846`) on a SHIFT-click, so functionally the pending-dig state exists, but `get_digedge` itself is side-effect-free in the port. Low risk (the mark is set elsewhere), but note the divergence — if a code path relies on `get_digedge` alone to register the dig (as `FUN_0043bdd0` does natively), the port would miss it.

4. **Gate-bit choice is a guess in BOTH the port and this RE.** The port snaps on `SubWalk|SubFloor` (value 3) after a previous bug where bare `SubWalk` matched climb cells and parked the standoff on the wall (`:2872-2875`). The original's actual gate inside the dropped body is **UNRESOLVED** (§6/§9). The port's "floor cell (value 3)" choice is a *reasonable, behaviour-matched* heuristic and currently produces the right result, but is not byte-confirmed.

5. **Validity sentinel:** port returns `"-1 -1 -1"`; original returns `{-1,-1}`+Z=14 → Y=−1. Tcl's `vector_unpacky>0` rejects both identically. OK.

6. **Bounds inset:** original rejects targets within 1 tile of the world edge (`005981d0:84-87`). The port relies on `WorldToTile`/`FindNearestDiggableEdge` clamping; add an explicit 1-tile inset reject to match.

---

## 9. Unresolved (gaps — explicitly marked)

- **U1 — the search body of `FUN_005981d0` (0x5982eb–0x598785) is gone.** BFS-via-FastArray frontier is confirmed by the surviving seed-enqueue; 4-neighbour expansion + nearest-to-dwarf selection is INFERENCE from the twins. Needs a re-decompile / manual disasm of `0x5981d0` to 100%-confirm the loop, the exact neighbour set, and the distance metric.
- **U2 — the per-cell standable gate** inside the dropped body: is it `+0x3c & 2` (floor sub-bit), the derived `+0x12 & 0x40` floor-tile flag, or `tile[+0x34+layer] != 0` (the per-owner array)? The prior doc claimed `+0x34+layer`; **none of these is visible in the surviving bytes.** Unconfirmed.
- **U3 — does the dropped body call `FUN_005968b0` (rim) and/or `FUN_005976b0` (precondition)?** The twins do; `get_digedge`'s shell does not, but they could be in the removed blocks. Their addresses do not appear in the surviving disasm fragment.
- **U4 — exact distance metric / tie-break.** `00597b20:140` uses `dx²+dy²+dz²·0.5²·k`; `0043bdd0` uses L1 `<5/<8/<0x14`. Which one `get_digedge` uses for "nearest to dwarf" is unconfirmed.
- **U5 — `DAT_0073708c = 0.5`** and **`DAT_00737520 = 7.0`** are INFERENCE from `(nibble*0.5+7)` and the round-bias dual-use; not read from a data dump.
- **U6 — `0x41600000 = 14.0f`** decoded by hand (exp 3, mantissa 1.75); the *meaning* (top floor-line surface) is INFERENCE consistent with the depth model (15→14.5, floor line ≈14).

---

## Verification notes (adversarial pass,)

A second agent re-opened every cited primary source and tried to refute the 10 load-bearing
claims. Result: **8 confirmed, 2 corrected, 0 fully unverifiable.** Two factual errors were
fixed in place; several over-statements were softened to INFERENCE. Details:

**CORRECTED — output vector ordering (§5, was line 190).** The spec previously wrote the
result list as `{resultX, 14.0f, resultY}` (Z=14 in the **2nd / Y** slot). That is **wrong.**
`FUN_006badc0(&local_40)` (`00635180:162`) reads 3 consecutive stack floats in declaration
order `local_40, local_3c, local_38` (decls at `00635180:24-26`). The assignments are
`local_40 = out[0]` (resultX, `:159`), `local_38 = 0x41600000` (14.0, `:160`),
`local_3c = out[1]` (resultY, `:161`). So the emitted vector is **`{resultX, resultY, 14.0}`**
— Z=14 is the **3rd component**, resultY is the 2nd. This is also the *only* ordering
consistent with the spec's own claim 2 (the Tcl validity test `[vector_unpacky $edgepos]>0`
reads the **Y** component, which must be resultY, not 14.0). The old §5 line 190 was
internally contradictory. Fixed.

**CORRECTED — §4.3 "stores seed.x/seed.y into the appended slot" presented as proven.** The
surviving bytes at `005981d0:95-96` are `iRamfffffff8 = *param_2;` / `iRamfffffffc = param_2[1];`
— writes to the **absolute addresses 0xfffffff8 / 0xfffffffc**, which are Ghidra
base-pointer-loss artifacts (the FastArray element base was lost after `Append`). The seed
DOES get read (`*param_2`, `param_2[1]`) and this immediately follows `FUN_0041fb80(1)`, so the
"seed enqueued into the new slot" reading is *highly plausible* but is **INFERENCE, not a
direct read**. Claim 3's phrase "appends one slot and stores seed.x/seed.y into it" is
therefore an over-claim; the §4.3 verbatim block and a new note ‡ now state this. (The
`Append`-then-`RaiseException`-then-store sequence is otherwise verbatim and correct.)

**Softened (not errors, but over-precise):**
- Claim 1 signature: `param_3` is typed `undefined4` (scalar) in the decompile, not `int*`.
  Both call sites pass `&local_3c` (an address), so "ref is a pointer to {refX,refY}" is a
  sound INFERENCE — but the worker's own typed signature does not prove it (the deref is in
  the dropped body). Annotated in §4.1.
- §4.1 animator description had the local-var pairing scrambled
  (`local_44=local_3c=tileX`); the verbatim assignments are
  `local_38=local_8; local_40=local_8; local_3c=local_10; local_44=local_10`
  (`0043bdd0:241-244`). Corrected — the *conclusion* (seed==ref==scanned tile) stands.
- Claim 4 reader expressions: the carve `00596b80:49` uses `>>(owner*4) & 0xf`; the animator
  `0043bdd0:174` and `:240` use the equivalent `& (0xf << (owner*4))`. Both are nibble tests;
  the spec conflated the two forms but the substance is correct.

**CONFIRMED verbatim:**
- Claim 1 return packing — `0043bdd0:245-248` exact (`piVar11=...; local_10=*piVar11;
  local_8=piVar11[1]; if((0<local_10)&&(0<(int)local_8))`).
- Claim 2 validity sentinel — `005981d0:88-89` and `:99-100` both write `0xffffffff` to
  out[0] & out[1]; Tcl `z_dignwalk.tcl:147` `[vector_unpacky $edgepos]>0`. Exact.
- Claim 3 (partial) — `FUN_0041fb80` IS DSFastArray::Append: string `DSFastArray.h,0x9b` at
  `0041fb80:17`; 8-byte elements `{int,int}` zero-init at `:30-31` / `:42-43`; signature
  `__thiscall(int*param_1,int param_2)`. The "BFS not ring-scan" conclusion is sound. (The
  one correction is the slot-store provenance, above.)
- Claim 4 dig-mark write — `005981d0:98` exact: `*(uint*)(iVar1+8) |= 4 << ((param_4&7)<<2)`.
- Claim 5 — `0x41600000 == 14.0f`; `635180:160`
  `local_38 = 0x41600000`; Tcl re-applies via `originalz`/`lrep` (`z_dignwalk.tcl:148,150`).
  (The hard-code is real; only its *position* in the output list was mis-stated — see above.)
- Claim 6 — wrapper input packing: seed = {ROUND(startpos.X), ROUND(startpos.Y)}
  (`635180:155-157` via local_18/local_14); ref = {ROUND(obj[+0xc]+0.5), ROUND(obj[+0x10]+0.5)}
  (`635180:153-154`); layer = `*(obj+0x36c)` (`635180:158`). Exact. ("dwarf.y/.z" labels and
  "nearest the dwarf" purpose are frame/behaviour INFERENCE — search body is dropped.)
- Claim 7 — bounds inset gate `005981d0:84-87` exact (`<minX+1`, `>=maxX-1`, `<minY+1`,
  `>=maxY-1`; world fields +0x84/+0x8c/+0x88/+0x90); same fields in `00592f10:13-26`.
- Claim 8 — tile addressing `00592f10:27,29`: `idx=(Y*world[+0x44]+X)%world[+0x4c]`,
  `tile=idx*0x7c+world[+0x14]`. Exact (124-B stride matches the live dump). Note: `FUN_00592f10`
  is `__thiscall(this, X, Y)` with X clamped vs +0x84/+0x8c and Y vs +0x88/+0x90 — the spec's
  "(this, y, x) named but effectively (this, X, Y)" caveat (§3) is borne out.
- Claim 9 (shaft-bottom, INFERENCE HIGH) — cannot be refuted *or* confirmed from bytes (search
  body dropped); the reasoning (solid dig tile fails the standable gate → can only be enqueued,
  never chosen) is internally consistent and the Tcl `vector_fixdig` follow-up
  (`z_dignwalk.tcl:149`) supports it. Left as INFERENCE HIGH. Honest.
- Claim 10 (interior gate UNRESOLVED) — confirmed genuinely unresolved; none of `+0x3c&2`,
  `+0x12&0x40`, `+0x34+layer` appears in the surviving bytes. The prior doc's `+0x34+layer`
  is correctly flagged unconfirmed. Honest.

**Net:** the spec's *contract* (inputs, the X>0&&Y>0 sentinel, the Z=14 hard-code, the
dig-mark side effect, the bounds inset, the tile addressing, BFS-via-FastArray) is sound and
well-cited. The one consequential bug was the **output-vector component order** (Z in the
wrong slot), now fixed — this matters for the port: emit `{X, Y, 14.0}`, not `{X, 14.0, Y}`.
The PORT GAP #1 (return Z=14.0) remains correct; just ensure 14.0 lands in the **3rd**
component.
