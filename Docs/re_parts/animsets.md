> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# AREA: animsets — the animation-set / gait layer for movement, climb & dig

**Scope:** `z_anims.tcl` `set_class_anim` + `set_class_animset` tables, the 24-slot gait
animset record, the clip-name families (gehen/laufen/drehen/kletter/leiter/buddeln/kiste/kiepe…),
how `-animsets {a b}` maps to gait slots, the 10 fps vertex-morph timing, the per-clip `.3db`
`motionOffset` root-motion field, and how the ORIGINAL couples per-frame root motion to world
displacement (`DAT_00737058 = 0.25` consumption in `CAct_WalkFloor`). Cross-ref to the port's
`DigglesAnimController` / `WalkAction` / `MetaWalkAction` / `WalkClimbAction` and a gap list.

Frame note (per task ground-truth): native X = horizontal walk axis; native Y (tile rows) =
gameplay vertical (gravity / climb); the BDB height nibble = slab DEPTH into the screen. The
animset layer itself is axis-agnostic — it only stores clip references and per-clip root-motion
scalars; the axis interpretation happens in WalkFloor/Climb (covered by ORIGINAL_PATHFINDING_RE.md).

---

## 1. OVERVIEW — three Tcl→engine tables, two runtime structures

There are **three** class-definition Tcl commands that feed animation, all registered in
`CTclClassDefMethods::Register` (`0060fc70_CTclClassDefMethods_Register.c`):

| Tcl command | Slot in Register | Native handler | Builds |
|---|---|---|---|
| `class_defaultanim <obj>.<anim> <mode>` | 0xf | `FUN_00613520` | class default (idle at spawn) |
| `set_class_anim <state> <obj>.<anim> [txt]` | 0xc | `FUN_00615650` | per-class **state→clip name map** |
| `set_class_animset <id> { {slot <obj>.<anim>} … }` | 0x1b | `FUN_00616d30` | per-class **gait animset table** (id→24 slots) |

Citations: `set_class_anim` registration `0060fc70:291-299`; `set_class_animset` registration
`0060fc70:675-687` (`"TCL:CTclClassDefMethods::SetClassAnimSet"` string at `00616d30:57`);
`class_defaultanim` `0060fc70:366-378`.

Two runtime structures result:

1. **Class-level animset table** — built by `FUN_00616d30` at class+`0xcdc` (pointer) / class+`0xce0`
   (count). Each animset id is a 0x100-byte (256) row of **32 slots × 8 bytes**, indexed by a slot-name
   hash mod 32. Each 8-byte slot = the resolved `(db, obj_in_db, anim_in_obj)` packed triple +
   animset-id tag. (`00616d30:226,232-233`).
2. **Per-OBJECT gait block** — at object+8, **4 gaits × 0x1e4 bytes (484)** (`FUN_00423270:15-16`,
   `FUN_004230d0:64-66`). Within a gait: **24 slots × 0x14 bytes (20)**: `+0x04` = resolved global
   anim idx (`-1` = unset), `+0x10` = frame count, plus the per-slot root-motion scalars consumed by
   the floor mover. When a gait's slot is unset (`+4 == -1`) the engine falls back to the object's
   **base animset block at object+0x5b8** (`FUN_00422ee0:30-39`, `FUN_004230d0` etc.). This base
   block = ANIMSET_STANDARDWALK(0).

The class table (struct 1) is the load-time data; it is instantiated into the per-object gait block
(struct 2) when an object is spawned / when `-animsets`/`-speedtype` choose a gait.

---

## 2. `set_class_animset` PARSER — `FUN_00616d30` (1305 B)

Signature (from Register call site, args are Tcl objv): `int FUN_00616d30(interp, clientData, objc, objv)`.
`param_3 = objc`, `param_4 = objv`. Pseudocode:

```
require a class is in scope: if (*(class_mgr+4) == 0) → error "no class defined." (line 82)
require objc == 3: else if (objc != 3) → wrong-args error (line 259)

// arg[1] = animset id (integer)
id = Tcl_GetIntFromObj(objv[1]) // local_30 / uVar10 (lines 100-110)
if (id >= class.animset_count(+0xce0)) FUN_00603df0(id+1) // grow the table (line 116)

// arg[2] = the list of {slot anim} pairs
list = objv[2] // local_54 (line 132)
n = Tcl_ListObjLength(list) // (line 144)
for (i = 0; i < n; ++i):
    pair = Tcl_ListObjIndex(list, i)        // (line 152)
    if (Tcl_ListObjLength(pair) != 2) continue          // pairs only (line 164)

    animStr = Tcl_ListObjIndex(pair, 1)     // the "<obj>.<anim>" string (line 165)
    FUN_006b8fb0(animStr)                                // wrap as engine string
    FUN_00460fc0(&triple, ...)              // resolve "<obj>.<anim>" → (db,obj,anim)  (line 185)
    triple.tag = id                         // local_64 = id (line 193)

    slotStr = Tcl_ListObjIndex(pair, 0)     // the slot NAME (line 194)
    // --- slot-name HASH (lines 218-225) ---
    h = 0
    for (char c : slotStr)  h = h*9 + c
    slotIdx = h & 0x1f                       // (line 226: (uVar10 & 0x1f) * 8)

    row  = class.animset_table(+0xcdc) + id*0x100        // (line 226: local_14*0x100)
    slot = row + slotIdx*8                                // 8-byte slot
    rowArray = *slot                         // FUN_00603a60 grows it (line 227)
    rowArray[entryIdx] = { triple(8B) at +4, tag=id at +0xc }   // stride 0x10 (lines 232-233)
```

Key constants & offsets (all from `00616d30`):
- class animset-table ptr = **class+0xcdc**, count = **class+0xce0** (lines 226, 215 / cf. `00601500:185-188`).
- animset row stride = **0x100 = 256** (line 226).
- **slot count per row = 32** (slot index masked `& 0x1f`, line 226).
- slot stride in the class row = **8** (line 226).
- slot-name hash = **`Σ (h*9 + c)`** over the ASCII bytes, then `& 0x1f` (lines 221-226).
- inner entry stride = **0x10 = 16** (`+4` triple, `+0xc` tag) (lines 232-233).

> **INFERENCE (Ghidra-mangled inner store):** the inner `rowArray[entryIdx]` (lines 226-233) is a
> growable sub-array per slot; the surfaced behaviour is "slot name → resolved clip triple". The
> exact multiplicity (whether a slot can hold >1 clip, e.g. random variants) is not provable from
> this function alone — the read side (`FUN_004230d0`) takes index 0, so treat it as **1 clip per
> slot** for the port. Marked INFERENCE.

### 2.1 The 24 named slots (from `z_anims.tcl`, the only caller that names them)

`set_class_animset` rows are addressed by name, but **every animset in `z_anims.tcl` lists the slots
in a fixed canonical order** (`z_anims.tcl:1280-1306` for set 0). Mapping name → ordinal index (the
ordinal is what `CAct_WalkFloor` uses via `FUN_004230d0(idx)`):

| idx | slot name | set-0 clip (`z_anims.tcl`) | role |
|---|---|---|---|
| 0 | `standard` | `mann.standard` | gait idle pose |
| 1 | `walk_start` | `mann.gehen_start` | ONCE accel |
| 2 | `walk_loop` | `mann.gehen_loop` | LOOP cruise (root-motion source) |
| 3 | `walk_loop_wave` | `mann.gehen_gruessen` | LOOP greet variant |
| 4 | `walk_stop` | `mann.gehen_end` | ONCE decel |
| 5 | `turn_left_90` | `mann.drehen_links` | in-place turn |
| 6 | `turn_right_90` | `mann.drehen_rechts` | in-place turn |
| 7 | `turn_left_180` | `mann.drehen_ganz_links` | in-place 180 |
| 8 | `turn_right_180` | `mann.drehen_ganz` | in-place 180 |
| 9 | `climb_standard` | `mann.kletterstand` | wall idle |
| 10 | `climb_up` | `mann.kletter_hoch_loop` | climb up LOOP |
| 11 | `climb_down` | `mann.kletter_runter_loop` | climb down LOOP |
| 12 | `climb_right` | `mann.kletter_rechts_loop` | crawl right LOOP |
| 13 | `climb_left` | `mann.kletter_links_loop` | crawl left LOOP |
| 14 | `ladder_climb_up` | `mann.leiter_hoch_loop` | ladder up |
| 15 | `ladder_climb_down` | `mann.leiter_runter_loop` | ladder down |
| 16 | `ground_to_wall` | `mann.stand_zu_kletterstand` | mount transition |
| 17 | `wall_to_ground` | `mann.kletterstand_zu_stand` | dismount transition |
| 18 | `ladder_to_ground` | `mann.leiter_zu_boden` | ladder dismount |
| (opt) | `walk_begin` | (set-5/6 only) | one-shot enter (mount hamster/board) |
| (opt) | `walk_end` | (set-5/6 only) | one-shot exit |

> The ordinal column is the **canonical authoring order**, used for the port slot enum. The native
> engine does NOT use the ordinal — it uses the `& 0x1f` name hash (§2). Both produce a stable lookup;
> the port may use either (string key is simplest and is what we already do). `walk_begin`/`walk_end`
> are extra slots present only in mounted sets (`z_anims.tcl:1439-1440,1472-1473`).

A partial animset (e.g. ANIMSET_SNEAK=8, only `walk_start/loop/stop` — `z_anims.tcl:1530-1534`) leaves
all other slots `-1`, so the engine falls back to the base block (object+0x5b8 = STANDARDWALK) for turns,
climb, idle, etc. **This fallback is the whole point of partial sets.**

---

## 3. THE ANIMSET TABLE (full, from `z_anims.tcl:1276-1662`)

`set ANIMSET_x N` then `member ANIMSET_x` then `set_class_animset $ANIMSET_x { … }`. The `member`
command (Register slot 5, `FUN_006131f0`) just declares the variable a class member; the integer is
the animset id passed to `-animsets`.

| id | const | meaning | distinguishing clips |
|---|---|---|---|
| 0 | ANIMSET_STANDARDWALK | base walk (the fallback) | `gehen_*` |
| 1 | ANIMSET_RUN | run | `laufen_schnell_*`, climb `*_schnell` |
| 2 | ANIMSET_WALKTIRED | tired | `gehen_muede_*`, `drehen_*_muede` |
| 3 | ANIMSET_SWIM | swim | `schwimmen_*`; turns = loop; climb = `schwimmen_hoch/runter` |
| 4 | ANIMSET_WALKWITHPANNIER | carrying Kiepe | `gehen_kiepe_*` (climb = normal) |
| 5 | ANIMSET_HAMSTERRIDE | hamster mount | `hamster_*` + `walk_begin/end` = auf/absteigen |
| 6 | ANIMSET_HOVERBOARD | hoverboard | `board_*` + `walk_begin/end` |
| 7 | ANIMSET_WALKWITHBOX | carrying Kiste | `gehen_kiste_*`, `kiste_drehen_*` (turns), climb=normal |
| 8 | ANIMSET_SNEAK | sneak | `schleichen_*` (start/loop/stop ONLY) |
| 9 | ANIMSET_FLEE | flee | `fliehen_*` (3 slots) |
| 10 | ANIMSET_ZOMBIE | zombie | `gehen_zombie_*` (3 slots) |
| 11 | ANIMSET_DIVER | diving | `tauchen_*` (3 slots) |
| 12 | ANIMSET_SKIPPWALK | skip/hop | `skipp_*` (+standard) |
| 13 | ANIMSET_DRUNKENWALK | drunk | `gehen_betrunken_*` (+standard) |
| 14 | ANIMSET_WALKFIT | fit walk | `gehen_fit_*` (3 slots) |
| 15 | ANIMSET_BARRELWALK | rolling barrel | `rollen_fass_*` (3 slots) |
| 16 | ANIMSET_WALKSTRIKE | strike | `gehen_streiken_*` + `drehen_streiken` turns |
| 256 | ANIMSET_FIGHTKUNGFU | fight (kungfu) | `laufen_kungfu_*` + kungfu turns |
| 257 | ANIMSET_FIGHTSWORD | fight (sword) | `laufen_schwert_*` |
| 258 | ANIMSET_FIGHTTWOHAND | fight (2-hand) | `laufen_zweihand_*` |

**`-animsets {a b}` semantics:** the **first** id = the walk (slow) gait, the **second** = the run
(fast) gait. From the Tcl call sites the common form is `-animsets {0 1}` (STANDARDWALK + RUN). Other
forms seen in scripts: `-animsets {slow fast dive std}` (4-list, ORIGINAL_PATHFINDING_RE.md:311).
`speedtype` picks among them: `FUN_004234a0` (the gait selector) — **speedtype 2 ⇒ gait index 1 (run);
else gait index 0 (walk)**; speedtype 0 also sets a flag at obj+0x7be (`FUN_004234a0:1-11`).

---

## 4. 10 FPS VERTEX-MORPH TIMING (the playback clock)

All clips play as **whole-mesh swaps at a fixed 10 FPS** (no bones, no interpolation). From
`CAnimCtrl::GetFrameIdx` (`FUN_00462040`, summarised in re/notes/ANIMATION_SYSTEM.md §1):

```
delta = round((now - start_time) * DAT_007373f0(=10.0) + DAT_00737190(=0.05))
frame = (mode==STILL) ? start_frame
      : (mode==ONCE)  ? min(start_frame+delta, count-1)   // freeze last, auto-flip to STILL
      : (start_frame+delta) % count                        // LOOP
```

Constants: `DAT_007373f0 = 10.0` (FPS), `DAT_00737190 = 0.05` (round bias). Modes (`animinit.tcl`):
STILL=0, ONCE=1, LOOP=2. The engine NEVER auto-chains start→loop→stop — the caller schedules it
(this is why the slot triad exists). The port replicates this exactly in `DigglesAnimator`
(`Mathf.RoundToInt((t-start)*10 + 0.05)`) and chains in `DigglesAnimController.PlayStartLoop`
(`DigglesAnimController.cs:180-188`).

### 4.1 `.3db` root-motion fields (the displacement source)

Each anim record carries `vec3 motionOffset` (per-loop world translation in model frame) and
`vec3 rotationDelta` (per-loop rotation, radians) after the frame index table
. Examples (mann.3db):
`gehen_loop = (0,−16.22,0)`, `laufen_schnell_loop = (0,−34.29,0)` (≈2.11× faster), `standard = 0`,
`drehen_rechts` translation 0 but rotZ≈−0.52. The two scratch fields `field4` + `reserved4` between
the index table and the second name are **uninitialised garbage — ignore**.

---

## 5. ROOT-MOTION → WORLD DISPLACEMENT — `CAct_WalkFloor` step `FUN_00418db0` (883 B)

This is the heart of the task: **how the original couples per-frame root motion to displacement.**
The floor-walk action `CAct_WalkFloor` runs a small start/loop/stop state machine on
`obj.walkstate (+0x234)`:

```
state 0x234 == 1 (START): play slot 0x15, advance until clip ends → set 0 (00418db0:33-49)
state 0x234 == 0 (LOOP): play slot 0x10, advance until clip ends → set 2 (00418db0:50-67)
state 0x234 == 2 (MOVE): consume root motion along the floor polyline (00418db0:68-159)
```

(0x15, 0x10 are the engine's internal anim-slot constants requested via `FUN_004230d0(slot)`; the
chosen clip's frame count via `FUN_00423180(slot)`; the slot record base via `FUN_00423270(slot)`.)

### 5.1 The per-step displacement (THE formula) — `00418db0:68-87`

```c
local_18 = (FUN_00423270(10) // slot/component 10
           + FUN_00423270(9)               // slot/component 9
           + FUN_00423270(0xb)             // 11
           + FUN_00423270(0xc) )           // 12
           * DAT_00737058;                 //  = 0.25
local_8 = obj.phase(+0x3c) * local_18; // (+0x3c) = fractional-clip progress 0..1
```

So **the world arc-length advanced this tick = (sum of the active clip's 4 root-motion components) ×
0.25 × (clip phase delta)**. `DAT_00737058 = 0.25` is the rootmotion→world scale
(ORIGINAL_PATHFINDING_RE.md:316,465). At 10 fps this is `clipFrameDelta × 0.25`. Walk-vs-run speed
differs **entirely** because `laufen_schnell_loop` has ~2× the per-frame root translation of
`gehen_loop` — there is **no hand-tuned speed constant** in the engine.

### 5.2 Consuming the displacement along the baked floor polyline — `00418db0:87-145`

`local_8` (the world distance to advance) is then walked along the **piecewise-linear floor points**
baked by Calc1 (`obj+0x14` = point array, `obj+0x1c` = current index, `obj+0x40..0x48` = current xyz):

```
while (local_8 > 0): // (00418db0:87-143)
    p = points[curIdx]
    segLen = dist2(curPos, p)  with Z weighted ×DAT_0073708c (=0.5)   // (lines 111-114)
    if (local_8 < segLen):                            // partial segment
        t = local_8 / segLen
        curPos = lerp(curPos, p, t)                   // (lines 119-125) ← Y interpolated too
        break
    local_8 -= segLen                                 // (line 129) consume whole segment
    curPos = p ; curIdx++                             // (lines 137-141) advance to next point
phase(+0x3c) = leftover/local_18 // (line 148) carry fractional progress
```

Two faithful details the port must keep:
- **Height follows the polyline**: Y is lerped between adjacent baked points (`00418db0:119-125`),
  each carrying its own sampled terrain Y — never a straight start→goal Y-lerp.
- **Z weighted ×0.5** in every distance metric (`DAT_0073708c`, lines 113, and arrival in
  ORIGINAL_PATHFINDING_RE.md:323).

> **INFERENCE (component indices 9–12):** `FUN_00423270(9..12)` returns a float per call and the four
> are summed. The function indexes `gaitBlock + param_2*0x14` (same stride as the 24-slot table) and
> returns `*(slotRecord)` (the slot record's first field). Whether 9–12 are literally the climb slots'
> stored stride OR (more likely) the 4 floats of the active clip's per-frame motion vector packed into
> consecutive 0x14 records is **not provable from these bytes** (Ghidra surfaces a pointer return that
> the caller reads as float). The **verified, load-bearing fact** is: *displacement = (4-component
> root-motion sum) × 0.25 × clip-phase, consumed as arc-length along the floor polyline.* Port to the
> per-clip `|motionOffset|` magnitude (already exposed) × 0.25-equivalent, NOT a tuned constant.

---

## 6. `set_class_anim` PARSER — `FUN_00615650` (Register slot 0xc)

`set_class_anim <state> <obj>.<anim> [{txt …}]` builds the **state→clip name map** (NOT gait slots).
Used by Tcl `state_enter` / `play state <name>` and by engine actions for non-gait clips
(dig, work, talk, idle variants). 1000+ entries in `z_anims.tcl:14-1271`. The optional 3rd arg is a
per-frame **txt-anim track** (`{eyes c l l …}` / `{face 11 15 …}`) used by the facial system
(`z_anims.tcl:544,694,783,1032-1035`). The walk/dig families relevant here:

- **walk:** `walkstart/walkloop/walkstop/walkback/walkwaveloop` → `gehen_*`; fast `walkfast*` →
  `laufen_schnell_*`; `walkfit*`, `walktired*`, `walkdrunk*`, `walkunderwater*`, `walkzombie*`,
  `walkbox*`, `walkpannier*` variants (`z_anims.tcl:25-208`).
- **turn:** `turn180right/left`, `turnright/left`, `rotateright/left` → `drehen_*` (`:31-37`).
- **climb:** `climbup/down/right/left` → `kletter_*_loop`; fast `*fast`; `digclimb*` →
  `kletter_hack_*`; ladder `ladderclimb*`/`ladder*` → `leiter_*` (`:39-96, 332-335`).
- **dig:** `digup/digfront/digdown/digleft/digright` (+ `_wood/_stone/_metal` + `_accident`) →
  `buddeln_*` (`:35`). Airhammer `airhamm*` → `pressluft_*`; laser `laserdrill*`;
  pickaxe `hack*` → `hacken_*`; saw `saw*`/`foxtail*` (`:337-475`).
- **box/Kiste:** `takebox*/putbox*` → `kiste_nehmen/ablegen_*`; carry `walkbox*` (`:178-198,258-261`).

The port stores this as `DigglesClass.AnimMap` (state→"obj.anim"), resolved by
`DigglesAnimController.ResolveState` with **animset override first, base map second**
(`DigglesAnimController.cs:125-132`) — faithful to "gait slot wins over base state map".

---

## 7. TCL ↔ NATIVE CONTRACT (what the unchanged scripts require)

| Tcl | Native | Contract |
|---|---|---|
| `class_defaultanim <o>.<a> <mode>` | `FUN_00613520` | sets spawn idle (class+0x10/0x18/0x20) |
| `set_class_anim <state> <o>.<a> [txt]` | `FUN_00615650` | state→clip map; optional txt-anim track |
| `set_class_animset <id> { … }` | `FUN_00616d30` | id→32-slot row (hash `Σh*9+c & 0x1f`); fallback to set 0 |
| `member <var>` | `FUN_006131f0` | declares the `ANIMSET_*` integer a class member |
| `action <o> walk "-animsets {a b} -speedtype N …"` | walk dispatcher | a=walk gait id, b=run gait id; speedtype 2⇒run |

Tcl wrapper `walk_action` (in `z_dignwalk.tcl`, supplied by the scripts) injects the right
`-animsets`/`-speedtype` per task; `-canclimb 0` only for box carriers
(ORIGINAL_PATHFINDING_RE.md:415-418). The port does NOT implement the Tcl wrappers — it parses the
`-animsets`/`-speedtype` option string directly (`DigglesTclEngine.cs:2760-2862`).

---

## 8. NEIGHBOUR-SYSTEM INTERACTIONS

- **WalkFloor / MetaWalk (ORIGINAL_PATHFINDING_RE.md §3-4):** consumes the animset's `walk_start`(1)
  → `walk_loop`(2) → `walk_stop`(4) and the `turn_*`(5-8) slots; speed is `walk_loop` root motion ×0.25.
  Turn clips fire when heading change ≥ π/4 (`DAT_007370d0`).
- **Climb (ORIGINAL_PATHFINDING_RE.md §5):** the per-segment executor `CAct_WalkAnim` picks
  `climb_up`(10)/`climb_down`(11)/`climb_right`(12)/`climb_left`(13) by grid-step axis + vertical sign,
  and `ground_to_wall`(16)/`wall_to_ground`(17) for mount/dismount; speed = that clip's root motion
  (no fixed climb speed). The clip-NAME table for climb lives **here in the animset**, which is why the
  pathfinding RE marked it "UNRESOLVED (lives in WalkAnim/animset)".
- **Dig (DIGGING_MECHANIC.md):** dig clips come from the **`set_class_anim` map** (`buddeln_*`,
  `kletter_hack_*`), NOT the gait animset; selected by face direction + material + `get_gnomeposition`
  (0=floor `buddeln`, 1=wall `kletter_hack`).
- **Gender (gender_clothing_frau memory):** `set_alternateanimdb mann frau` remaps the resolving obj so
  the SAME slot/state names resolve from `frau.3db`; the port mirrors this in
  `DigglesAnimController.SetAlternate`/`SplitObjAnim` (`.cs:76-91,272-290`).
- **Face anim:** the optional `set_class_anim` txt-anim 3rd arg (`{eyes …}`/`{face …}`) drives the
  facial texture-swap system (separate area).

---

## 9. PORT GAP ANALYSIS

**What the port already does faithfully:**
- `set_class_animset` parser (`DigglesTclEngine.cs:2512-2530`) builds `DigglesClass.Animsets[id] =
  {slot→"obj.anim"}` — functionally equal to the native 32-slot row (string key replaces the name
  hash; acceptable since both are stable name→clip). ✅
- `set_class_anim` map + animset-override-first resolution (`DigglesAnimController.ResolveState`). ✅
- 10 fps morph clock + STILL/ONCE/LOOP + start→loop chaining (`DigglesAnimator`,
  `DigglesAnimController.PlayStartLoop`). ✅
- `-animsets {a b}` parse: a=walk, b=run; `-speedtype` (`DigglesTclEngine.cs:2787-2791`,
  `EntityActions.cs:283-292`). ✅
- The faithful gait RATIO from root motion: `LocomotionRate(run)/LocomotionRate(walk) ≈ 2.11`
  (`DigglesAnimController.cs:156-167`, `EntityActions.cs:171-174`). ✅

**GAPS (the speed model is NOT faithful):**

1. **Displacement is a height-fraction heuristic, not root-motion arc-length.**
   `MetaWalkAction.Cruise` = `ModelHeight * BodySpeedFactor(0.5) * gaitRatio`
   (`EntityActions.cs:137,174,421-427`); `WalkAction` same (`:174`). The original is
   `|motionOffset| × 0.25 × phase` consumed as polyline arc-length (§5.1). **Fix:** drive cruise speed
   from `LocomotionSpeed("walk_loop")` (already exists, `DigglesAnimController.cs:141-150`) — i.e.
   `|motionOffset| × modelScale / (frames/10)`; drop `BodySpeedFactor`/`ModelHeight`. The comment at
   `EntityActions.cs:131-136` explicitly says they abandoned root motion because "16.22 was smaller
   than the visual foot sweep" — that's the **modelScale calibration** problem (the `.3db` units →
   world), not a reason to abandon the model. Solve the one-time scale (DAT_0073703c=0.125 sub-cell
   world unit is the native scale; calibrate `|motionOffset|·k` once), then run/climb fall out free.

2. **`WalkClimbAction.cs` carries a separate, also-unfaithful speed model** — calibrates
   `_modelUnitsToWorld` from a pinned `_walkSpeed` constant and then applies a hand-tuned
   `GroundSpeedScale = 1f/1.3f` fudge (`.cs:184-191`). Two divergent speed systems in the port
   (`EntityActions.WalkAction/MetaWalkAction` and `WalkClimbAction`) should be **unified onto the one
   root-motion×0.25 model**.

3. **Climb clip selection is in the invented `ClimbAction.cs`, not the animset.** Per
   ORIGINAL_PATHFINDING_RE.md §5.3 the port fabricated `Kind{Mount,CrawlLeft,…}` codes and a
   `height*0.47` speed. The faithful design picks `climb_up/down/right/left` + `ground_to_wall/
   wall_to_ground` from the **active animset slots 10-13/16-17** and paces each leg by that clip's root
   motion. The animset already has these slots resolved — wire them in.

4. **Per-frame root-motion CONSUMPTION (the `phase(+0x3c)` carry) is absent.** The port advances by
   `cruise * dt` (`EntityActions.cs:336`), decoupling movement from the morph clock. Faithful behaviour
   advances exactly the clip's authored stride per played frame (no foot-skating). For visual parity
   this matters most at low frame rates / fast clips. **Fix (optional, higher fidelity):** advance by
   `Σcomponents × 0.25 × framesAdvancedThisTick` instead of `cruise × dt`.

5. **`walk_loop_wave`(3) / `walk_begin`/`walk_end` (mount sets) unused.** The greet-wave variant and
   the mount/dismount one-shots for hamster (5) / hoverboard (6) are parsed but never played. Low
   priority (cosmetic / mounts unimplemented).

6. **Slot-name hash not replicated.** The port keys slots by exact string; the native uses `Σh*9+c &
   0x1f`. Equivalent in practice **unless** two slot names collide mod 32 in the original (they don't —
   authored names are distinct). No action needed; documented for completeness.

---

## 10. CONSTANTS TABLE

| Const / offset | Value | Role | Citation |
|---|---|---|---|
| `DAT_00737058` | **0.25** | root-motion→world displacement scale | `00418db0:82`; ORIG_PATH:465 |
| `DAT_0073708c` | **0.5** | Z/height weight in walk distance metric | `00418db0:113`; ORIG_PATH:463 |
| `DAT_007373f0` | **10.0** | animation FPS (global) | re/notes/ANIMATION_SYSTEM.md §1 |
| `DAT_00737190` | **0.05** | frame-index round bias | re/notes/ANIMATION_SYSTEM.md §1 |
| `DAT_0073702c` | **0.09 (0.3²)** | walk arrival threshold² | ORIG_PATH:466 |
| `DAT_007370d0` | **π/4 (0.785)** | turn-clip heading threshold (45°) | ORIG_PATH:467 |
| class+`0xcdc` / `0xce0` | ptr / count | class animset-table base / length | `00616d30:226`; `00601500:185-188` |
| animset row stride | **0x100 = 256** | bytes per animset id row | `00616d30:226` |
| slots per row | **32** (`& 0x1f`) | slot index mask | `00616d30:226` |
| class slot stride | **8** | bytes per class-table slot | `00616d30:226` |
| inner entry stride | **0x10 = 16** | `+4` triple, `+0xc` tag | `00616d30:232-233` |
| slot-name hash | `Σ h*9 + c` | name → slot index | `00616d30:221-226` |
| obj gait block base | **obj+8** | first gait | `FUN_00423270:15` |
| gait block stride | **0x1e4 = 484** | bytes per gait (4 gaits) | `FUN_00423270:16` |
| gait slot stride | **0x14 = 20** | bytes per runtime slot | `FUN_00423270:21` |
| gait slot count | **24** (`< 0x18`) | slots per gait | `FUN_00423270:20` |
| slot `+0x04` | i32 | resolved anim idx (-1 = unset) | `FUN_004230d0:64`; `FUN_00422ee0:30` |
| slot `+0x10` | i32 | frame count | `FUN_00423180:25` |
| base animset block | **obj+0x5b8** | STANDARDWALK fallback | `FUN_00422ee0:36`; `FUN_00423270:30` |
| gait index field | **obj+0x798** | 0=walk,1=run (≤3 valid) | `FUN_00423270:15`; `FUN_004234a0` |
| speedtype field | **obj+0x7e8** | 2⇒run gait | `FUN_004234a0:1-3` |
| walkstate field | **obj+0x234** | 1=start,0=loop,2=move | `00418db0:33,50,68` |
| walk-slot consts | 0x15 (start), 0x10 (loop) | engine internal slot ids | `00418db0:35,52` |

---

## 11. UNRESOLVED / INFERENCE LIST

1. **`FUN_00423270(9..12)` component semantics** — verified: their sum ×0.25 = displacement. NOT
   verified: whether 9-12 are the 4 floats of the active clip's per-frame motion vector or the stored
   strides of slots 9-12. Ghidra returns a pointer the caller reads as float. **Port to per-clip
   `|motionOffset|·0.25`** (the observable behaviour); mark the field decode as INFERENCE.
2. **Inner per-slot sub-array multiplicity** (`00616d30:226-233`) — read side takes index 0, so the
   port treats it as 1 clip/slot. Whether the engine supports per-slot random variants is unproven.
3. **`.3db` motionOffset → world unit scale.** The numeric `16.22` (gehen_loop) needs a single
   model→world calibration constant the port hasn't pinned (the abandoned-root-motion comment at
   `EntityActions.cs:131`). Native scale hints: `DAT_0073703c=0.125` (sub-cell→world),
   `DAT_0073752c=8.0` (world→sub-cell). Resolving this kills both fudge factors
   (`BodySpeedFactor=0.5`, `GroundSpeedScale=1/1.3`).
4. **`walk_loop_wave`(3) trigger condition** — the engine plays the greet variant on some social
   proximity event; the exact gate (which `evt_*`) is not traced here.
5. **txt-anim 3rd arg of `set_class_anim`** (`{eyes …}`/`{face …}`) — parsed by `FUN_00615650`; the
   exact runtime track format belongs to the facial-animation area, not traced here.
6. **Fight animsets 256-258** — used only in (stubbed) combat; gait selection among them not traced.
