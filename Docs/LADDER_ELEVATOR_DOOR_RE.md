# Ladders, Elevators & Doors — Native RE + Port Implementation Spec

> **Status: E1 ELEVATOR REPAIR IMPLEMENTED (updated).** Reverse-engineering of the ladder /
> elevator / door / troll-door family in Diggles (2001), plus the current mapping onto the port's
> nav/behaviour stack. Door, ladder, and elevator phases, including ladder placement mode 4, are
> implemented and covered by automated tests. Special non-transport attachment objects remain a
> separate work item.
>
> Ground truth: `re/decompiled/Diggles/_global/*.c` (Ghidra of `wiggles.exe`, image base 0x00400000)
> cross-referenced with `re/decompiled/Diggles.source_attribution.tsv` (original C++ filenames) and
> the original Tcl under `original/data/Scripts/**`. Every law cites `FUN_xxxxxxxx:line` or
> `file.tcl:line`. Nav mapping grounded in `Docs/ORIGINAL_PATHFINDING_RE.md`,
> `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`, `Docs/DIGGING_MECHANIC.md`, `Docs/ITEM_PLACEMENT_RE.md`.
>
> **Source C++ units** (from source_attribution.tsv): `Objects/TransportLogic.cpp`,
> `Actions/Act_Ladder.cpp`, `Actions/Act_Elevator.cpp`, `Actions/Act_Door.cpp`,
> `Tcl/TclObjMethods_AG.cpp` + `_GetSet.cpp` (the command handlers).

---

## 0. Executive summary — the one unifying mechanism

All three families are **one native subsystem** — a per-object *TransportLogic* record plus a shared
*pathfinding-influence rectangle* stamped into the CMap nav grid. There is **no bespoke climb, no
bespoke door collision** — they all steer the **existing** Calc1 / MetaWalk / Climb pipeline by
writing cost into the map:

- **A ladder** writes a **negative-cost "climb attractor" band** up a wall (range-limited) → the
  normal pathfinder routes gnomes onto it and the normal Climb executor climbs it.
- **A closed door** writes a **+INT_MAX cost blocker rectangle** → the normal pathfinder detours
  around it; opening clears the rectangle.
- **An elevator** is a moving platform object (cabin) driven by a logic record; passengers ride via
  position interpolation (`set_forceipol`).

The Tcl→native command surface and its current port status are summarized in §9:

| Tcl command | Native handler | C++ name | args | Writes |
|---|---|---|---|---|
| `create_ladderlogic this` | **FUN_006798f0** | `CTclObjMethods::CreateLadderLogic` | 2 | LadderLogic → obj+0x3ec[+8] |
| `set_ladderrange this N` | **FUN_00679b40** | `SetLadderRange` | 3 | LadderLogic+0x28 = N; re-stamp |
| `create_elevatorlogic this $cab F` | **FUN_006792c0** | `CreateElevatorLogic` | 4 | ElevatorLogic → obj+0x3ec[+4] |
| `set_elevatorproperties this …` | **FUN_00695a60** | `SetElevatorProperties` | 5–7 | ElevatorLogic+0x218…+0x228 |
| `create_doorlogic this` | **FUN_00679d30** | `CreateDoorLogic` | 2 | DoorLogic → obj+0x3ec[+0xc] |
| `set_doorproperties this MODE …` | **FUN_00679f80** | `SetDoorProperties` | 3–4 | DoorLogic+0x18 = mode enum |
| `set_pf_influence this X0 Y0 X1 Y1 V T` | **FUN_0068f430** | `SetPFInfluenceRect` | 8 | obj+0x254…+0x268 rect |
| `delete_transportlogic this` | **FUN_006796e0** | `DeleteTransportLogic` | 2 | frees obj+0x3ec, clears +0x268 |
| `set_snaptowall this B` | **FUN_0067d4b0** | `SetSnapToWall` | 3 | obj+0x3d0 bit 0x20000 |
| `set_fstopper` / `rem_fstopper` | (fluid) | — | — | **liquid** stopper, NOT pathfinding |

**The TransportLogic holder** (`obj+0x3ec`, 16 bytes, alloc'd lazily by any create_*logic):

```
holder[0x00] = last type written (0=elevator, 1=ladder, 2=door)
holder[0x04] = ElevatorLogic* (FUN_006792c0:146)
holder[0x08] = LadderLogic* (FUN_006798f0:87)
holder[0x0c] = DoorLogic* (FUN_00679d30:84)
```
`delete_transportlogic` frees the whole holder and zeroes it (FUN_006796e0:71-76).

**The shared influence rectangle** — the 7 fields *every* influencer (ladder, door, `set_pf_influence`)
writes onto its OWN object (identical layout in FUN_0060b600, FUN_0060ceb0, FUN_0068f430):

```
obj+0x254 = box X0 (sub-cell offset, relative to obj sub-cell pos)
obj+0x258 = box Y0 (=decimal 600 in the decompile)
obj+0x25c = box X1
obj+0x260 = box Y1
obj+0x264 = COST added to every covered cell (door-closed = 0x7FFFFFFF; ladder = 0xffffffec = -20)
obj+0x268 = TYPE / category (door = 4, ladder = 4, set_pf_influence = the T arg)
```
Written into the map by the pair **FUN_005a3dd0** (get-or-create the 13-layer influence singleton
`DAT_00772428`) + **FUN_005a3fd0** (rectangle stamp, layer-mask **7** = layers 0|1|2; clamps to CMap
sub-cell bounds `DAT_0077241c+0x94..+0xa0`) → **FUN_006c2340** (per-layer dirty-rect accumulator).

---

## 1. LADDERS

### 1.1 Classes & build chain (`original/data/Scripts/classes/items/transport.tcl`)

Three producible ladder classes, all `metal elevator 2` (the "elevator" *type-group* is shared with
lifts — see `get_objtype` gating in AI, §7.4):

| Class | `set_ladderrange` | texture variant | transport.tcl |
|---|---|---|---|
| `Leiter` | **22** | `set_textureanimation this 0 {0} 0 0` |:277-323 |
| `Leiter_Metall` | **32** | `{1}` |:325-367 |
| `Leiter_Kristall` | **40** | `{2}` |:369-414 |

`obj_init` (transport.tcl:300-315, identical across the three):
```tcl
set_physic this 1
set_snaptowall this 1;# seat against the wall face (§1.5)
set_textureanimation this 0 {N} 0 0;# N = 0/1/2 skin
set_fogofwar this 8 8
timer_event this evt_timer0 -repeat 1 -interval 1 -userid 0 -attime 3;# fire at t=3
```
`handle_event evt_timer0` (transport.tcl:317-321) — the actual wiring, fired ~3 ticks after spawn
(after the build-up completes):
```tcl
create_ladderlogic this
set_ladderrange this 22;# 22 | 32 | 40
set_fogofwar this 8 8
```
`method destroy` overrides genericprod to call `delete_transportlogic this` first (transport.tcl:294-298).
`class_defaultanim leiter.unten_b`; the ladder's own frames are `leiter_a` … `leiter_i` (**9 rungs** —
see §1.3). Buildup stages via `build_dummys`/`buidup_anis` (genericprod, standard).

Also referenced in build-placement logic: `z_work_common.tcl` special-cases
`lsearch {Leiter Leiter_Kristall Leiter_Metall}` (a ladder is a legal build target on a wall, unlike
most objects).

### 1.2 `create_ladderlogic` — FUN_006798f0 (2 args)

```
this = Obj_GetObjSafe(argv,2,1) //:54
holder = *(this+0x3ec); if null → alloc 16 B, zero [1][2][3] //:59-70
if (holder[2 /*+0x08*/] == 0): //:72 (not already a ladder)
    holder[0]   = 1                                   // type = ladder  :73
    L = alloc(0x561)                                  // LadderLogic record, 1377 B  :74
    L = FUN_0060ad40(L)                               // ctor + one-time subsystem init  :80
    L->[0x1c] = this                                  // owner obj (== record field param_1[7])  :83
    L->[0x20] = this->[0xdc]                          // obj id / CMap handle  :84
    FUN_0060bac0(L)                                   // settle/stamp once  :85
    L->[0x18] = this->[0x3d0]                          // cached obj flags  :86
    holder[2] = L                                     // :87
```

### 1.3 LadderLogic ctor + one-time subsystem init — FUN_0060ad40

Runs the *transport subsystem* init **once** (`if DAT_00b9bf44==0`,:40): it registers the `transport`
object class (`s_transport_00721a0c`,:67-72) and iterates the ladder standard frames
**`leiter_a` … `leiter_i`** (loop `local_1c` from `'a'`=0x61 while `< 'j'`,:90-168) — i.e. **9 rung
frames** — recording per-rung heights into the global transport grid `DAT_00b9bf40`
(entry stride 0x10; height field `((float)aux[0x15] - (float)aux[0x12]) * DAT_00736fcc - DAT_0073708c`,
:163-164). Per-instance it zeroes `L[0..3]`, sets `L[0xa /*+0x28*/]=range` default `0x49742400`
(=1.0e6f, later overwritten by `set_ladderrange`) and `L[9]=0` (a bool). (FUN_0060ad40:177-182.)

**LadderLogic record layout:**
```
+0x00..0x08 cached position x,y,z (param_1[0..2])
+0x0c settle step counter (caps at 9) (param_1[3])
+0x18 cached obj flags (obj+0x3d0) (param_1[6])
+0x1c owner obj* (param_1[7])
+0x20 obj id (obj+0xdc)
+0x24 bool "descending" (param_1[9])
+0x28 RANGE (set by set_ladderrange) (param_1[0xa])
+0x2c computed base height (param_1[0xb])
```

### 1.4 `set_ladderrange` — FUN_00679b40 (3 args)

```
this = Obj_GetObjSafe(argv,2,1)
if holder && holder[+0x08] (LadderLogic): //:61
    range = float(argv[2])                    // FUN_006baf80  :68
    *(LadderLogic + 0x28) = range             // :69
    FUN_0060b600(LadderLogic)                 // RE-STAMP the influence band  :70
```
So `set_ladderrange` sets `+0x28` and immediately re-projects the band.

### 1.5 The vertical projection — FUN_0060b600 (influence writer) + FUN_0060b1b0 (wall scan)

**FUN_0060b600** (`param_1` = LadderLogic; `param_1[7]` = owner obj):
1. `FUN_0060b1b0` computes the base height / band extent (the up-scan, below) → `param_1[0xb]`.
2. Reads a transport-grid entry (`FUN_0060b560` index into `DAT_00b9bf40`) → base height (:32-35).
3. `*(bool*)(param_1+9)` = descending? (compare heights,:38).
4. **Gate:** `if ((obj->[0x3d0] & 0x404001) != 0x400001)` the band is **zeroed** (:39-43). Cross-checking
   the flag word with the command handlers shows that the required bits are **visible (`0x1`) and
   fully built (`0x400000`)**, while **boxed (`0x4000`) must be clear**. This gate does not test the
   object-level `set_snaptowall` bit (`0x20000`).
5. Compute the sub-cell rectangle: vertical extent scaled by 8 (`DAT_0073752c=8.0`); offsets
   `iVar9=-0xc, iVar2=rangeSub-4, iVar7=6, iVar8=0xc` (:52-56) → stamps into obj+0x254..+0x260.
6. Sets **obj+0x264 = 0xffffffec (-20)** (negative cost = **attractor**,:89) and **obj+0x268 = 4**
   (:88), then stamps via `FUN_005a3dd0`/`FUN_005a3fd0(...,7)` (:76-77, 97-98).

**FUN_0060b1b0 — the "how far up does the ladder reach" scan** (this is the geometry the port must
reproduce; `in_XMM2_Da` = the RANGE):
- Convert ladder (x,y) → sub-cells (`×8`).
- From the base, **walk UP the wall** (loop: `iVar8 -= 4, uVar13 -= 4, uVar14 -= 4; local_8++`,
:112-115) while ALL hold:
  - wall face **continuous** — BDB aux height at each step differs from the previous by **≤1 nibble**
    (`iVar4 = (baseH&0xff) - aux[sub]; if (1 < iVar4) return; if (iVar4 < 0) return;` :88-93,105-111);
  - not enclosed / not foreign zone — `tile+0x18 & 0x80` clear (:47,61) and L2M `tile+0x16` matches
    the owner class (:54-55,71-72);
  - accumulated height `fVar16 = local_8*0.5 - 0.5 < range` (:116-123);
  - still inside CMap bounds (:124-126).
- The number of successful steps × the rung height = the band's vertical span.

**Historical root cause of "ladder sticks to the tunnel bottom":** the functional ladder is this
**projected climb band**, not a tall mesh. Before the implementation,
`create_ladderlogic`, `set_ladderrange`, and object-level `set_snaptowall` did no useful work, so no
band was projected and the ladder remained an inert prop at its drop point. The port now projects
the band, performs the wall-depth settle, and routes ladder segments through the dedicated ladder
mount/climb/dismount animation aliases.

### 1.6 Ladder placement — FUN_00530e80 mode selection + FUN_0046b450 mode 4

**(Redone from the decompile — the earlier port carried two inventions: ±8 "frame"
probes that reddened real shafts, and a missing early-stop.)** A boxed ladder still owns its
TransportLogic holder. `FUN_00530e80:112-166` reads holder type 1 and selects placement tick mode 4
rather than ordinary building mode 2. The mode-4 arm of `FUN_0046b450` (the `iVar5 == 4` branch):

1. **Cursor snap + seat**: x → `ROUND(2x + 0.5)·0.5` (`DAT_00737330`/`DAT_0073708c`), y → sub-row;
   seat `x_c = 2·floor((x − 0.5)·0.5) + DAT_007371c0 = 1.5` (the dropped-floor `FUN_004295a0`
   pattern, Play-verified as `TransportShaftCenterX`); scan start row = `floor(y) − 1.5`
   (`piStack_d0 = fVar19 − DAT_007371c0`).
2. **Lattice gate** (right before the scan do-loop): the scan runs only when
   `x_c == floor((x_c + 0.5)·DAT_00737058=0.25)·DAT_007374a0=4.0 + 1.5` — i.e. the seat is on the
   **4-unit transport lattice** `x_c ∈ {1.5, 5.5, 9.5, …}`. Off-lattice columns skip straight to
   the box fallback. (Also gated on `param_1+0xf0 == 0`, not force-boxed.)
3. **Down-scan**, +1.0/row (`DAT_00737138`), ≤ 0x40 rows. A row is a shaft bottom when exactly
   FOUR probes hold: `hmap(subX∓4, subY−6) == 11` (`cVar1 == '\v'`) and `hmap(subX∓4, subY+6) > 12`
   (`0xc < bVar2`) — open shaft column above, solid support under both flanks. **There are no ±8
   probes in the native.**
4. **Zone gate**: at least one of the 2×2 seat tiles must have a nonzero per-player `tile+0x34`
   byte (all four zero → the row is skipped). Port: knowingly unmodelled (same hook as Calc1's
   owner gate 4).
5. **Early-stop** (`LAB_0046e598`, from step ≥ 2): both flanks `hmap(subX∓4, subY−2) == 15` ⇒ the
   shaft has closed — stop the scan (box fallback), never tunnel to a lower cavity. The scan also
   breaks past the map bottom (`DAT_0077241c[0x24] − 4`).
6. **Commit**: the FIRST row passing probes+zone is THE candidate — after the building/order-ghost
   overlap tests (`FUN_005e7000`/`FUN_005e7400`) and the reach scan the branch `break`s
   unconditionally; failures fall to the box fallback, never to a deeper row.
7. **Acceptance**: `FUN_0060b1b0` (reach scan) then **`FUN_0060b560 != 0`** — the rung-table
   selector must resolve at least slot 1: `span + 1 ≥ height(leiter_b)`. Measured from the shipped
   `transport.3db`: leiter_a..i standard bboxY × `DAT_00736fcc = 0.005` − 0.5 = thresholds
   4.931 / **9.296** / 13.264 / 17.231 / 21.199 / 25.166 / 29.134 / 33.102 / 37.070 → the minimum
   legal span is **8.296**. This single term is what reds a LOW corridor (its open column is too
   short) — no frame probes needed. The selected slot is also the ghost model (`leiter_a..i`), so
   the hover ghost stretches with the scanned span.
8. The accepted base publishes `(x_c, row, z = 11)`; otherwise ordinary settled-box placement.

The port implements this in `PlacementController.TryComputeLadderMount` (lattice gate, four-probe
row law, early-stop, first-row commit, selector threshold via
`DigglesTclEngine.TryGetLadderMinimumSpan` from the live library) and drives the hover ghost model
through `TrySelectLadderPresentation`/`ApplyLadderPreview`. This placement law is separate from the
later runtime `set_snaptowall` depth settle.

### 1.7 How a gnome USES a ladder — CAct_Ladder (Act_Ladder.cpp, FUN_004111b0)

The ladder is *used* by the ordinary walk/climb pipeline. `CAct_Ladder::tick` (FUN_004111b0):
- If done flag set → result 2 and return (:31-33).
- Computes an **approach point**: `x ± DAT_0073709c` (mount offset toward the wall,:38-43),
  `z + DAT_00737138` (:44).
- Allocates a **`CAct_MetaWalk`** (`CAct_MetaWalk::vftable`,:56) and a **`CPath`** (`CPath::vftable`,
:97) with 2 waypoints (current pos, ladder mount,:118-126), sets the MetaWalk target
  (`obj+0x7a4..+0x7ac` = `puVar6[0x1e9..0x1eb]`,:128-130), and calls
  **`FUN_00415d10(metawalk, target, path, 0, 2, -1.0f)`** = the CAct_MetaWalk ctor (:138) — i.e. it
  **delegates to the normal MetaWalk** (`Docs/ORIGINAL_PATHFINDING_RE.md §3`). MetaWalk then classifies
  the ladder band's sub-cells as WALL and dispatches **CAct_Climb** — the climb runs on the existing
  FloorPathMap executor (§10 mapping). Sets state = walking (`param_1+0x50 = 1`,:139).

**Dedicated ladder anim slots** (distinct from free-wall climb): the animset table exposes
`ground_to_ladder`, `ladder_climb_up`, `ladder_climb_down`, `ladder_to_ground`
(`classes/characters/rh_globals.tcl`, `hamster.tcl`, `riesenelfe.tcl`; slots 14-18 per
`ORIGINAL_PATHFINDING_RE.md §4.3`). A ladder climb should play the `ladder_*` clips, a free cliff the
`climb_*` clips.

---

## 2. ELEVATORS

### 2.1 Classes & build chain (transport.tcl)

Each lift is a **drive object** (`metal elevator 2`) that spawns a **cabin dummy**
(`metal dummy 2`, `set_physic 0`, `set_forceipol 1` = interpolate position) in `evt_timer0`:

| Drive | Cabin | `create_elevatorlogic … F` | `set_elevatorproperties this …` | transport.tcl |
|---|---|---|---|---|
| `Aufzug` | `Aufzugkabine` | `$k 1` | `1000 0 2.0` (linear, speed 2.0) |:3-72 |
| `Dampfaufzug` | `Dampfaufzugkabine` | `$k 1` | `1000 1 0.2 0.4` (quadratic, accel 0.2 / brake 0.4) |:74-147 |
| `Kristallaufzug` | `Kristallaufzugkabine` | `$k 0` | `1000 0 100.0 100.0 1` |:150-218 |

`handle_event evt_timer0` (e.g. transport.tcl:48-54):
```tcl
set k [new Aufzugkabine]
create_elevatorlogic this $k 1
set_elevatorproperties this 1000 0 2.0;# MAXRANGE, ACCMODE, speed[, brake][, flag]
```
Drive `class_anim`s: `lift_still` / `lift_up` / `lift_down` (:25-27). Cabin has its own
`aufzugfahrkorb.standard`.

### 2.2 `create_elevatorlogic this $cabin flag` — FUN_006792c0 (4 args)

```
drive = Obj_GetObjSafe(argv,3,1) //:59
cabin = Obj_GetObjSafe(argv,3,2) //:61
flag = bool(argv[3]) // FUN_004295e0(3)+FUN_006baf10 → param_3._3_1_
holder = *(drive+0x3ec) (alloc if null)
if holder[1 /*+0x04*/] == 0: //:89
    holder[0] = 0                                     // type = elevator  :90
    E = alloc(0x122) → FUN_00607b10 (ctor)            // ElevatorLogic, 290 B  :91-97
    E->[0x20c] = drive                                // owner  :100
    E->[0x210] = drive->[0xdc]                         // :101
    E->[0x214] = cabin                                // :102
    E->[0x204] = flag                                 // :104
    E->[0x1fc]=E->[0x200] = drive->[0x3d0]            // :144-145
    if flag==0: E->[0x208] = FUN_006074b0()           // :107-109
    else:       register waypoints in global DAT_0076a338 grid  // :111-143
    holder[1] = E                                     // :146
```
(The `flag` arg selects whether the lift registers a fixed track (`DAT_0076a338`, stride 0x20) or a
computed one.)

### 2.3 `set_elevatorproperties` — FUN_00695a60 (5–7 args)

```
E = *(*(this+0x3ec)+4) // must exist, else "no elevator logic"
E->[0x224] = double(argv[2]) // MAXRANGE (1000):83-103
E->[0x218] = int (argv[3]) // ACCMODE (0=linear, 1=accelerated):104-122
E->[0x21c] = double(argv[4]) // speed / acceleration:123-143
if argc>5: E->[0x220] = float(argv[5]) // braking:144-153
if argc==7: E->[0x228] = int (argv[6]) // active rider capacity:154-162
```

`FUN_00607b10:21-43` supplies the defaults: max range is effectively unbounded, ACCMODE is 1,
speed/acceleration is 0.2, braking is 0.4, and `+0x228` is **4**. The constructor zero-constructs
sixteen cabin-offset vectors and explicitly authors the first eight, while live rider loops stop at
`+0x228` and the load repair clears all sixteen physical slots (`FUN_00607b10:12-96`;
`FUN_006075b0:93-125`). The last property is therefore the usable rider capacity, clamped by the
16-slot backing table, not an opaque feature flag. The crystal lift deliberately reduces it to one
(`transport.tcl:195-200`).

### 2.4 Elevator state I/O — FUN_006075b0 (fresh RE)

The function named in the E1 brief is **state I/O, not the logic tick**. Its only direct caller in
the recovered program is the type-0 arm of the shared TransportLogic loader
(`FUN_006071f0:26-44`; source attribution maps that caller to `Objects/TransportLogic.cpp`).
`FUN_006075b0` serializes the scalar elevator fields and a variable-length array of 0x28-byte call
records (`FUN_006075b0:22-84`). On load it:

1. reads the version/state bit (`FUN_006075b0:85-92`);
2. clears all 16 fixed rider slots (`FUN_006075b0:93-125`);
3. restores the registered-track row when this is a registered lift (`FUN_006075b0:126-179`);
4. resets the update cooldown to 10000 and re-caches the drive flags
   (`FUN_006075b0:182-185`).

This explains why treating `FUN_006075b0` as a frame update produced a plausible but incomplete
port: its call records survive a save, but active passenger links intentionally do not. The live
dispatch is `FUN_00607000:20-22`, which invokes `FUN_0060a860` for holder slot `+0x04`.

### 2.5 Live logic tick — calls, stops, motion, and passenger linkage

`FUN_0060a860` applies the following order every update:

1. clear the prior influence and normalize restored transient state 3
   (`FUN_0060a860:33-53`);
2. age/remove the dynamic call leases (`FUN_0060a860:54-90`);
3. choose the next stop with `FUN_00608b50`, then move with `FUN_00609050`
   (`FUN_0060a860:91-92`);
4. rewrite the positions of only fixed rider slots whose attached byte `+0x0e` is set
   (`FUN_0060a860:93-150`);
5. refresh the track registration through `FUN_0060a380` (`FUN_0060a860:151`).

There are two distinct record sets. The dynamic call array at logic `+0x20` has 0x28-byte entries:
actor, 16-tick lease, pickup XYZ, destination XYZ, phase, and direction. `FUN_00608140:21-57`
rejects an inactive/unbuilt/boxed drive, de-duplicates the actor, and appends this record.
An existing call is merely found; if its lease ages out, the still-running action reissues it on a
later tick rather than mutating a fixed passenger slot (`FUN_00608070:14-30`; `FUN_0060a860:54-90`).
Boarding is offered only when cabin Y equals pickup Y and cabin velocity is zero; direction and
current occupancy also gate the offer (`FUN_00608140:58-105`).

The fixed passenger table starts at `+0xf4`, has sixteen 0x10-byte slots, and is limited by the
configured rider capacity. A slot holds actor, cabin-offset index, destination Y, the board-ready,
exit-ready, and attached bytes, plus the actor's saved object flag. `FUN_00608580:18-65` allocates a
slot, chooses a unique cabin offset, and switches the actor to the carried-object flag.
`FUN_00608750:12-34` promotes a waiting call into a fixed rider (or marks an existing rider ready to
exit), while `FUN_00608940:17-76` removes it, restores its saved flag, and returns the lift to a
moving/idle state when the ready set is empty. `FUN_0060abd0:14-45` is the common interruption
cleanup: it removes both the fixed passenger and any outstanding call.

Stop selection is a directional sweep. With no calls or riders the target is current cabin Y
(`FUN_00608b50:26-29`). Fixed passengers determine direction and have priority
(`FUN_00608b50:30-159`); compatible waiting pickups are added only afterward
(`FUN_00608b50:160-216`). The selected target is stored at `+0x1f4` (`FUN_00608b50:218`). State 1
holds the platform still for boarding/exiting, including its dwell counter (`FUN_00609050:40-71`).
In state 2, ACCMODE 0 advances by configured speed times the global tick and clamps at the stop
(`FUN_00609050:73-111`); ACCMODE 1 accelerates, looks ahead to brake, and also prevents overshoot
(`FUN_00609050:113-178`). Presentation selects `lift_still`, `lift_up`, or `lift_down`
(`FUN_00609050:225-244`), and `FUN_00608ae0:13-21` copies the computed position to the cabin object.

The track is vertical and bounded by shaft geometry. `FUN_006096d0:28-115` scans from drive
`centerY+3`, samples the two walls at `centerX±4`, requires target height 11, starts the span at
0.375, and grows it by 0.125 until a boundary, zone, foreign obstruction, or configured max range.
`FUN_0060a210:17-35` initializes the cabin at `(drive.x, drive.y+2.0, drive.z)`. Both action
endpoints must lie from `drive.y` through `drive.y + span + 0.5`
(`FUN_0060acd0:13-21`); this half-unit-inclusive range is the authoritative stop law.
The refresh also mirrors the drive's visible+built+not-boxed gate onto the cabin visibility bit and
reinitializes cabin position when the drive moves (`FUN_0060a2c0:12-35`). Thus a produced boxed
elevator may retain its holder/cabin for placement mode selection, but only the crate is rendered.

For an attached rider, `FUN_00609da0:19-83` returns the cabin linkage position plus the rider's
assigned offset (a single-rider lift has no lateral offset). The tick then writes that exact XYZ,
so the rider follows the car, including its depth plane, rather than independently interpolating
between floor waypoints (`FUN_0060a860:93-150`). `set_forceipol 1` on the normal and steam cabins
controls cabin rendering only (`transport.tcl:57-70,130-143`).

### 2.6 CAct_Elevator state machine and interruption law

`FUN_0040e700` is the action tick. Its recovered states are:

| State | Native transition |
|---|---|
| 0→1 | construct/wait for the ordinary approach walk (`FUN_0040e700:113-131`, helper `FUN_0040d970`) |
| 2 | reissue/de-duplicate the call; when the stopped cabin reaches pickup, build the boarding walk (`FUN_0040e700:132-138`, helper `FUN_0040dc60`) |
| 3→4 | finish boarding, set board-ready, begin lift motion after a 10-tick dwell, then set attached (`FUN_0040e700:152-180`) |
| 4→5 | ride until destination equals current cabin Y and velocity is zero; clear attached and build the exit walk (`FUN_0040e700:227-246`, helper `FUN_0040df10`) |
| 5→6 | finish cabin exit, unregister the fixed rider, and build the final floor walk (`FUN_0040e700:184-198`, helper `FUN_0040e1f0`) |
| 6→7 | finish the floor walk, clean the request, and complete (`FUN_0040e700:199-217`) |

Destroyed, boxed, unbuilt, invalid-range, or unreachable conditions go through cleanup rather than
leaving a passenger linked (`FUN_0040e700:27-93,222`). The destructor and explicit cancellation
paths also call `FUN_0060abd0` (`FUN_0040d4a0:28-31`, `FUN_0040ed10`, `FUN_0040ed60`). A repaired
port must consequently leave an interrupted actor at its last lawful cabin position, clear its
attachment/request, and allow the lift to become idle or service another call.

### 2.7 Elevator placement — FUN_00530e80 mode 3 + FUN_0046b450

The shared holder selects **mode 3** when `holder[0] == 0`; ladder and door select modes 4 and 5
respectively (`FUN_00530e80:112-166`). In mode 3, `FUN_0046b450:1001-1237` snaps to the transport
lattice and searches at most 64 vertical candidates. A valid shaft head has target-15 samples at
`x±4` on the closed side and target-11 wall samples immediately into the shaft
(`FUN_0046b450:1059-1083`), passes occupancy/overlap gates (`FUN_0046b450:1084-1167`), and has at
least the minimum span returned by the same runtime scan (`FUN_0046b450:1168-1172`). The accepted
row starts at `floor(cursorY)+1.5` and descends in one-unit steps; the minimum span is exactly
`DAT_00737428 = 3.0`, and the mount uses depth `z=15`
(`FUN_0046b450:1011-1015,1050-1053,1168-1172,1199-1202`). Unlike ladder mode 4, this branch has no
ordinary boxed-ground fallback: an elevator ghost remains invalid/red away from a vertical shaft.

### 2.8 Script creation and boxed-product law

All three drive classes create their cabins and elevator logic from timer 0
(`transport.tcl:3-54,74-126,150-201`); `FUN_006792c0:58-146` is the corresponding
`create_elevatorlogic` handler and `FUN_00695a60:59-162` applies the property vector above.
Production uses the normal building crate law: `genericprod.tcl:6` includes **`elevator`** in
`BOXED_CLASSES`, and the workshop paths call `prod_createproduct_inv_boxed`/`packtobox`, whose
`set_boxed this 1` is at `genericprod.tcl:191`. The two port obligations are distinct:
`get_class_type <class-name>` must return `elevator` so that script branch runs, and a fresh elevator
must carry native object type 10 for its type-dependent body/save metadata. The 2b11057 tree already
had the class-name query fix, but not the latter kind-derived type assignment. More importantly for
the manual visual, it also omitted `FUN_0060a2c0`'s cabin-visibility coupling: the product could be
boxed while its full-size cabin rendered over the crate. In addition, the port represents the
native boxed-render flag by playing `kiste.standard`; its inactive elevator tick then played
`lift_still` over that crate. The repair does not run lift presentation while inactive. A produced
drive is the boxed product; its holder/cabin may exist for placement, but that cabin remains hidden
until the drive is built.

The oversized-marker path is separate and now has a concrete explanation: `set_boxed` calls
`BoxContentIcon.Attach`, whose one-shot `BoxContentIconReseat.LateUpdate` scales the sprite to 80%
of the **currently displayed mesh** (`RuntimeWorldSpawner.cs`, `BoxContentIcon:848-955`). Under
2b11057, the intervening inactive `lift_still` replay made that re-seat measure the elevator mesh,
not `kiste.standard`, so the icon became building-sized. This repair preserves the crate through the
re-seat; no icon-size constant was changed. If fresh manual S22 still reproduces an oversized icon,
that `Attach → Reseat → Seat/TryGetModelBounds` path is the isolated follow-up surface.

### 2.9 E1 repair delta against 2b11057

The fresh RE changes the implementation in these concrete ways:

| 2b11057 behavior | Recovered law / repair |
|---|---|
| No elevator branch in placement | Holder type 0 selects mode 3; scan the shaft head, require the runtime span, publish `z=15`, and remain invalid elsewhere (`FUN_00530e80:112-166`; `FUN_0046b450:1001-1237`). |
| Track clamped to `drive.y+2 .. drive.y+span` | Calls and destinations are legal over `drive.y .. drive.y+span+0.5`; only the initial car position is `drive.y+2` (`FUN_0060acd0:13-21`; `FUN_0060a210:17-35`). |
| `+0x228` named `Flags`; every lift admitted 16 riders | It is rider capacity, default 4, with sixteen backing slots; crystal configures 1 (`FUN_00607b10:43-96`; `transport.tcl:195-200`). |
| One `Boarded` bool and instantaneous board/exit | Explicit call, board, ride, exit, complete, and interrupted phases; the boarding transfer holds the car for the native ten-tick dwell (`FUN_0040e700:132-246`; `FUN_00609050:40-71`). |
| Rider written to `(drive.x, car.y, cabin.z)` | Rider follows the cabin linkage plus its unique slot offset; the floor exit waypoint restores the floor depth plane (`FUN_00609da0:19-83`; `FUN_0060a860:93-150`). |
| Lease expiry was the only interruption cleanup | Replacing the rider action removes the call/fixed rider immediately; only the waiting-call lease remains as a safety net (`FUN_0040d4a0:28-31`; `FUN_0060abd0:14-45`). |
| Cabin stayed visible, retained its old origin, and the inactive tick could replay `lift_still` over `kiste.standard` | Drive active flags show/hide the cabin, a moved drive reinitializes cabin XYZ/target, and inactive transport never replaces the crate presentation (`FUN_0060a2c0:12-35`; `FUN_0060a210:17-35`). |
| Fresh elevator products had no native kind-derived body type in 2b11057 | The current kind map assigns `elevator → 10`, boxing reasserts that metadata, and the acceptance test executes the `BOXED_CLASSES` decision. Cabin hiding separately removes the full-size visual that obscured the crate. |

Repair files: `Assets/Diggles/Runtime/Engine/PlacementController.cs`,
`Assets/Diggles/Runtime/Tcl/Engine/TransportLogicState.cs`, `ElevatorRideAction.cs`, and
`DigglesTclEngine.cs`; `Assets/Diggles/Tests/Editor/ElevatorTransportRepairTests.cs` plus its Unity
`.meta`; and this report. The acceptance fixture covers the three-class placement/product tables,
the call/board/move/exit/interruption state table, opposite-direction boarding admission, and the
passenger-to-cabin position invariant.

---

## 3. DOORS — two mechanisms

The game has **two independent door systems**. Both ultimately block/allow the pathfinder, but via
different primitives.

### 3.1 Mechanism A — native auto-door (`create_doorlogic` + `set_doorproperties`)

Used by **producible doors** and **troll doors/gates**. The engine auto-opens for qualifying gnomes
by proximity (the DoorLogic is ticked; `set_doorproperties` sets who qualifies).

**Producible doors** (`classes/items/tueren.tcl` → `calls/tueren.tcl`): `Holztuer`, `Steintuer`,
`Metalltuer`, `Kristalltuer` (all `metal protection 2`). `calls/tueren.tcl` `evt_timer0`
(:45-59) snaps the door onto the grid (`set_posz 15.0`, half-cell x/y) then:
```tcl
create_doorlogic this
set_doorproperties this openforfriends
```
`evt_btn_on` (:9-41) is the player toggle: `_Offen`→`open`, `_Verschlossen`→`closed`,
`_Automatisch`→`openforfriends`; packing a door forces `open` (tueren.tcl calls:14-18). `method init`
(:83-90) resets to `openforfriends`.

**Troll doors/gates** (`classes/items/tuerundtor.tcl`): `Trolltor_links`, `Trolltor_rechts`,
`Trolltuer` (all `stone protection 0`). `evt_timer0` (:141-144,:175-178,:215-218):
```tcl
create_doorlogic this
set_doorproperties this openforclasses { Troll }
```
`set_collision this 1` in obj_init. → **Only `Troll`-class actors open it** (story-gated barrier).

#### `create_doorlogic` — FUN_00679d30 (2 args)
```
this = Obj_GetObjSafe(argv,2,1)
holder = *(this+0x3ec) (alloc if null)
if holder[3 /*+0x0c*/] == 0:
    holder[0] = 2                              // type = door  :73
    D = alloc(0x765) → FUN_0060c520 (ctor)     // DoorLogic, 1893 B  :74-79
    D->[8] = this                              // owner  :81
    FUN_0060ceb0(D)                            // stamp influence  :82
    D->[0x20] = this->[0x3d0]                    // cached flags  :83
    holder[3] = D                              // :84
```
**DoorLogic ctor FUN_0060c520** sets initial state: `+4 = 3`, **`+0x18 = 1` (mode = CLOSED)**,
`+0x34 = 3` (:18-26) — **doors are born closed and open only when `set_doorproperties` relaxes them.**

#### `set_doorproperties this MODE [classlist]` — FUN_00679f80 (3–4 args)
String→enum (FUN_00679f80:101-207), written to **DoorLogic+0x18**:

| MODE string | enum | meaning |
|---|---|---|
| `open` (default / no match) | **0** | always passable |
| `closed` | **1** | blocked (writes INT_MAX cost — §3.3) |
| `openforplayer` | **2** | owner's own player |
| `openforfriends`| **3** | owner + those the **DOOR OWNER'S diplomacy row** marks ally(1)/friend(2) — FUN_0060d220:41-56: both lookups are `FUN_004c4130(row=door owner ×0x34)` → `FUN_0045f830(col=mover ×4)`; first expects 2, retry expects 1; the mover's own row is never read |
| `openforclasses`| **4** | only listed classes (e.g. `{Troll}`) → stored at DoorLogic+0x30 (:246-269) |
| `openforall` | **5** | everyone |

A transition **into or out of `closed`(1)** re-runs `FUN_0060ceb0` (:215-222) to re-stamp the map.
For `openforclasses` (argc==4) the class list is iterated and each name interned into `DoorLogic+0x30`
(:224-282).

#### DoorLogic influence writer — FUN_0060ceb0 (`param_1` = DoorLogic)
```
obj = *(DoorLogic+8) // owner
flags = obj->[0x3d0]
active = (flags & 0x404001) == 0x400001 // visible+built, not boxed (as for ladders)
box = seated ? [X0=-8, Y0=-20, X1=8, Y1=4] // obj+0x254=-8,+0x258=-20,+0x25c=8,+0x260=4:78-81
             : [0,0,0,0]                                // not seated ⇒ no influence  :31-45
obj+0x264 = (DoorLogic->[0x18] == 1) ? 0x7FFFFFFF: 0 // CLOSED ⇒ INT_MAX cost:88-91
obj+0x268 = 4 // type:86
stamp old box (clear) then new box via FUN_005a3dd0/FUN_005a3fd0(...,7):70-100
```
So a **native door blocks by writing INT_MAX cost** over a **±8 X (16 wide), −20..+4 Y (24 tall)**
sub-cell rectangle around its opening, and clears it when the mode is not closed. (In the ant-farm
frame, `reference_antfarm_side_view`: native Y = the gameplay vertical; the tall −20..+4 Y span seals
the doorway opening at any floor height; the ±8 X span covers the leaf + jambs. The scripted door
(§3.2) uses a tighter ±1 X.)

#### How a gnome uses it — CAct_Door (Act_Door.cpp, FUN_0040cf60 build / FUN_0040d230 tick)
Same shape as CAct_Ladder: build a `CAct_MetaWalk` toward the door (`target` from `param_1+0x38/0x3c`),
ctor `FUN_00415d10(metawalk, obj, path, …, 13.5f)` (:82; `0x41580000` = 13.5).

**⚠ (corrected, ENGINE review lane) `13.5` is NOT a distance.** Ghidra's
`FUN_00415d10` signature is arg-shifted: the float lands at **CAct_MetaWalk+0x24** — `+0x1c` is the
path END INDEX, an *int* (`FUN_004163a0:27,:46,:67`; `FUN_00407fb0:97-100` direct-fills
`[7]=+0x1c` from `CPath+0xc` and `[9]=+0x24 = -1.0f`). `+0x24` flows `FUN_00415f40:24` →
`FUN_0041a910` last param → **CAct_WalkFloor+0x44** → `FUN_0041a2f0:41-42`, where a non-−1 value
is used verbatim as the **destination Z (depth) of the final waypoint** — 13.5 sits between
climb-up z=14.5 (`DAT_00737564`) and climb-down z=12.5 (`DAT_00737548`): "stand at the door"
depth. Every other CAct passes the −1.0 sentinel there.

**The door does NOT auto-open on a proximity radius.** The law is *membership*:
`FUN_0040d230` state 1 calls the DoorLogic pass-check `FUN_0060d220` (:69); a granted mover is
registered in the DoorLogic **DSFastArray at +0x24/+0x28** (`FUN_0060d220:80-103`, which also
picks the swing direction from mover-vs-door X at:11 and returns 2 = "wait, opening");
`FUN_0060d470` deregisters on act end/abort (`FUN_0040d230:29,:96`) and flips the door state to
3 (close) **when the array empties** (:43-44). Port stand-in (no per-mover CAct_Door): a
qualifying, actively-walking mover inside the door's own influence rect [-8,-20,+8,+4]
(FUN_0060ceb0:78-81) holds it open — see `DigglesTclEngine.DoorInfluence*`.

#### Door placement seat — FUN_0046b450 mode 5 (holder type 2, FUN_00530e80:144-166)
After the mode-5 cursor snap (`:1828-1837`: `FUN_0055a600(&pick, 0xf)` z=15 plane pick, x →
`ROUND(2x+0.5)·0.5` half-grid, y → `ROUND(8y)/8` sub-row; border test vs the world rect ±
`DAT_007374a0` = 4.0 at `:1839-1842`), the SEAT is computed at `:1875-1882`:

```
x' = floor(x + 0.5) //:1875-1876 local_74 = DAT_0073708c = 0.5
y' = 2·floor((y − 0.5)·0.5) + 2.5 //:1877-1882 DAT_007373d8 = 2.5
z' = 15.0 //:1880 local_cc = 0x41700000
```

`FUN_004295a0` is the CRT **floor** wrapper (→ `FUN_006f6da0`); Ghidra drops its x87 return, so
the raw decompile reads as bare `+0.5 / +2` — the identical dropped-floor pattern at
`:1284-1290` is the LADDER seat whose floor-consumed reading is already Play-verified in the
port (`PlacementController.TransportShaftCenterX`). Cross-check: the built door's one-shot grid
snap `tueren.tcl:48-52` (`x = hf2i(x+0.5)`, `y = hf2i(y)+0.5`, `z=15`) is **idempotent** on this
seat (integer x, y ≡ *.5) — under the floor-less reading it would displace the built door from
the validated ghost. The aperture predicate `FUN_0060c590` (13×h11 column above the seat, floor
15 at x±0/±8, roof at y−20, 24×16 SubWater|SubClimb scan) is gated at `:1956` on the seat point.
Port: `PlacementController.DoorSeat` + `TryComputeDoorMount`.

### 3.2 Mechanism B — scripted / switch door (`set_pf_influence` + switches)

Used by **level-placed, switch-operated doors and gates** (`classes/items/tuerundtor.tcl`:
`Zauntor_a`, `Tuer_kaserne`, `Tuer_metall`, `Tuer_verlies`, `Tuer_kristall`, `Gefaengnis_gitter_a/b`),
all wired through **`classes/items/calls/doors.tcl`**. These do **not** use DoorLogic — they patch the
influence rect directly from Tcl and are triggered by `Schalter` switches (`calls/switcher.tcl`).

`calls/doors.tcl` (the load-bearing lines):
```tcl
# obj build (else-branch,:110-130):
set influence_pf... # per-class: 1 for solid doors, 0 for grates (Gefaengnis_gitter)
if {$influence_pf} {set_pf_influence this -1 -20 +1 +4 INT_MAX 0} #:128 CLOSED = block
set_collision this 1

# oeffnen (open):4-22:
set_pf_influence this 0 0 0 0 0 0 #:11 clear the block
rem_fstopper this #:21 (fluid)

# schliessen (close):23-33:
if {$influence_pf} {set_pf_influence this -1 -20 +1 +4 INT_MAX 0} #:27 re-block
set_fstopper this {0 -2} {0 1} 0 #:32 (fluid)

# obj_exit:19:
set_pf_influence this 0 0 0 0 0 0 # always clear on destroy
```
`walk_collision_callback` returns `Schaltstatus` (1 closed → collide, 0 open → pass,:42-51).
Switch wiring: `evt_timer0` finds nearby `Schalter*` objects and binds their press/release to
`schalten`/`schliessen` (:80-106). Switch behaviour + `set_actiononpress` in `calls/switcher.tcl`.

#### `set_pf_influence this X0 Y0 X1 Y1 VALUE TYPE` — FUN_0068f430 (8 args)
```
this = Obj_GetObjSafe(argv,8,1)
X0,Y0,X1,Y1 = int(argv[2..5]) // rect corners (sub-cell offsets)
VALUE = argv[6]: "INT_MAX"→0x7FFFFFFF, "INT_MIN"→0x80000000, else int //:238-298
TYPE = int(argv[7])
obj+0x254 = X… //:344-350 (the same 7 fields as door/ladder)
obj+0x258 = Y0
obj+0x25c = X1
obj+0x260 = Y1
obj+0x264 = VALUE
obj+0x268 = TYPE
clear-old-box then stamp-new-box via FUN_005a3dd0/FUN_005a3fd0(...,7) //:332-359
```
`-1 -20 +1 +4 INT_MAX 0` = a narrow (±1 X), tall (−20..+4 Y) INT_MAX blocker, TYPE 0. Cleared with all
zeros. **This is the identical `obj+0x254..+0x268` mechanism the native door and ladder use** — the
scripted door is just the Tcl-authored version of the same influence rect.

### 3.3 What INT_MAX actually does to the pathfinder (the nav-coupling law)

The influence is **not** a direct write of INT_MAX into `tile+0x06` — that byte is a signed i8
(`ORIGINAL_PATHFINDING_RE.md §1.1`; port `NavWorld.BaseCost` is `sbyte[]`, `NavWorld.cs:35`) and cannot
hold INT_MAX. Instead the rect is registered on the object (`obj+0x264` = cost) and stamped into a
**13-layer influence manager** (`DAT_00772428`, FUN_005a3dd0/FUN_005a3fd0), which marks the covered
sub-cell rects dirty (FUN_006c2340 = a DSFastArray rect accumulator) and periodically refreshes
(every 200 stamps, FUN_005a3fd0:140-187). The net, observable effect that the port must reproduce:

- **Closed door / `set_pf_influence … INT_MAX`** → covered tiles become **impassable** to Calc1
  (equivalent to `BaseCost = -1` over the rect). Calc1 already gates on `BaseCost != -1`
  (`Calc1Pathfinder.cs:110,171`), so the port models this as an overlay that forces `-1` (or adds a
  huge cost) over the rect.
- **Open** → rect cleared, tiles restored to their derived cost.
- **Ladder (−20 cost)** → covered wall tiles become **cheaper** and marked climb-passable (an
  attractor), so the router prefers the ladder column.

> ⚠️ The exact fold of `obj+0x264` cost into Calc1's per-edge cost (whether the dirty rects trigger a
> localized re-run of the `FUN_005a6350` derivation that rewrites `tile+0x06`, or Calc1 queries the
> influence layers live) is **INFERRED, not byte-traced** — the DSFastArray consumer wasn't followed
> to its Calc1 read site. The *port design* (a dedicated influence overlay consulted in Calc1, §10) is
> correct regardless of which the native does internally.

### 3.4 Traps

**No evidence** that traps ride the TransportLogic / influence machinery. `Schalter_hebel_lore_*`
switches (`Schalter.tcl:59-150`) drive `Dampflore` (steam mine-cart) via `obj_query … activate`, not
doors. Trap-like barriers use `set_collision` / `set_fstopper` (fluid) rather than
`create_doorlogic`/`set_pf_influence`. **Recommendation:** treat traps as out of scope for this
subsystem; revisit only if a trap class is found calling `create_*logic` or `set_pf_influence`.

---

## 4. `set_snaptowall` & `set_fstopper` (adjacent primitives)

- **`set_snaptowall this B`** — FUN_0067d4b0 (`SetSnapToWall`): `B` true ⇒ `obj+0x3d0 |= 0x20000`;
  false ⇒ `&= ~0x20000` (:74-88). `get_snaptowall` = FUN_0067d6e0 reads that bit. This is the
  **object-level** command (used by ladders, transport.tcl:308). It is distinct from **class-level**
  `class_snaptowall` = FUN_00614740 (classdef+0x2c bit 8), which the port already implements
  (`DigglesTclEngine.cs:61-62`). Per `Docs/TUNNEL_TORCH_LIGHTING_RE.md §5a`, bit 0x20000 drives the
  live **z-settle** (every 16th render tick the object's z snaps to `min(fill over ±¼-tile cross)+0.01`;
  x/y never move). Ladders need the same settle to seat on the wall and satisfy the `0x400001` gate in
  the influence writers (§1.5, §3.1). The native physics gate also requires boxed bit `0x4000` to
  be clear (`FUN_005cbf70`); therefore produced/contained ladder crates must retain the carrier's
  putdown depth until they are actually unboxed.
- **`set_fstopper` / `rem_fstopper`** — a **fluid/liquid** stopper (blocks water flow across the
  door footprint), **not** a pathfinding primitive. Doors set both (`set_fstopper this {0 -2} {0 1} 0`).
  Independent of the influence system; implemented by `FluidSystem` as a directional permeability
  boundary. See `FLUID_SYSTEM_RE.md`.

---

## 5. Port mapping — concrete integration seams

Verified port files (all exist):

| Native mechanism | Port file / seam | Implemented mapping |
|---|---|---|
| Tcl command surface | `Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs` | Real handlers for all door/ladder/elevator commands and object-level snap/interpolation flags. |
| TransportLogic record | `TransportLogicState.cs` + `DigglesTclEntity` | Door, ladder, elevator, influence, and rider state keyed by entity handle. |
| Influence rect → nav | `NavWorld.cs` + `Calc1Pathfinder.cs` | Overlap-safe hard-block, cost, climb, typed transport, and source-handle overlay consulted by Calc1. |
| Ladder climb band | `Calc1Pathfinder.cs` + `NavWorldService.cs` (`FindPath`) + `FloorPathMap.cs` (climb BFS) | Ladder band sets sub-cell **climb** bit (0x08) + negative cost so MetaWalk classifies WALL → Climb routes it. |
| Ladder up-scan (FUN_0060b1b0) | new helper reading `NavWorld.GetHmapSub` (`DIGGING_MECHANIC.md`, `ORIGINAL_PATHFINDING_RE.md §1.3`) | Walk up from base while wall continuous (±1 nibble) & height < range. |
| Ladder placement mode 4 | `PlacementController.TryComputeLadderMount` | Scan down to a target-11 shaft bottom, publish `z=11`, and retain native box fallback. |
| Elevator placement mode 3 | `PlacementController.TryComputeElevatorMount` | Scan upward to a target-15/target-11 shaft head, validate the runtime span, publish `z=15`, and reject every non-shaft spot. |
| CAct_Ladder/Door/Elevator | `Assets/Diggles/Runtime/Tcl/Engine/EntityActions.cs` (`MetaWalkAction`:416), `ClimbAction.cs` | Approach = `MetaWalkAction` to the object's mount point (the actions already delegate to MetaWalk natively). |
| snaptowall z-settle | `TunnelTorchTick` (per `TUNNEL_TORCH_LIGHTING_RE.md §5a`) | Reuse for ladders; drives the seat + `0x400001` gate. |
| Door/ladder spawn wiring | `Assets/Diggles/Runtime/Engine/RuntimeWorldSpawner.cs` (`NativeToLocal`:343) | The `evt_timer0` handlers already call the (now-real) commands; ensure entities get their holder on spawn. |
| Door open/close anims | animset slots (`open_l/r`, `close_l/r`, `opening_*`, `closing_*` from tuerundtor.tcl / tueren.tcl) | Play on mode transition. |
| Ladder climb anims | animset slots 14-18 (`ground_to_ladder`, `ladder_climb_up/down`, `ladder_to_ground`) | Play instead of free-wall `climb_*`. |
| Elevator product/cabin | `DigglesTclEngine.set_boxed` + elevator refresh | Preserve native type 10, render the drive as a crate, and hide its cabin while boxed/unbuilt. |
| Elevator call/ride | `ElevatorRideAction.cs` + `DigglesTclEngine.TickTransportLogic` | Typed route segment, directional call sweep, transfer dwell, capacity-limited rider slots, cabin-relative carry/dismount, and interruption cleanup. |

**Key seam decision:** because `BaseCost` is `sbyte`, model the influence as a **parallel per-tile
overlay** (an `int[] costBias` initialised to 0, plus a `bool[] hardBlock`), owned by `NavWorld`, with
`AddInfluenceRect(x0,y0,x1,y1,cost,type)` / `RemoveInfluenceRect(objHandle)` mirroring the object's
`obj+0x254..+0x268` fields. `Calc1Pathfinder.AddNeighbor` consults it: `hardBlock ⇒ skip`;
`edgeCost += costBias` (clamped). Ladders additionally OR the climb bit into the sub-grid over the band
so `MetaWalk`'s surface classifier (`ORIGINAL_PATHFINDING_RE.md §3.3`, bit checks on `tile+0x3c`) yields
WALL and dispatches Climb.

---

## 6. Phased implementation plan (ordered by user value)

Each phase compiles and is headless-testable (repo patterns: `WorkLoopTests` RealHost,
`NavWorldRealTerrainTests` real terrain, `Calc1`/`NavWorld` unit tests). During the full-plan pass,
each independently revertible feature slice receives its own checkpoint commit after automated
verification; final gameplay acceptance still requires the next manual Play run.

**Phase D1 — Doors block pathfinding — IMPLEMENTED.**
Implement the influence overlay in `NavWorld` + `Calc1Pathfinder`; implement `set_pf_influence`,
`create_doorlogic`, `set_doorproperties` (mode enum → cost: closed⇒hardBlock, else clear),
`delete_transportlogic`. Register `set_snaptowall` (sets the entity flag; z-settle can come later —
gate the influence on visible+built+not-boxed as native does). *Verify:* a
`Calc1Pathfinder` test where a path must detour around a `closed` door and passes straight through when
`open`; scripted `Zauntor`/`Tuer_*` and produced `Holztuer` both block. **Size: S.**

**Phase D2 — Door open/close semantics + anims + troll gating — IMPLEMENTED.**
`openforfriends` (diplomacy via existing `get_diplomacy`), `openforplayer`, `openforall`,
`openforclasses {Troll}` (class filter → only Troll routes through; others blocked). Switch wiring
(`Schalter` press → `schalten`/`schliessen`) already runs in Tcl once `set_pf_influence` is real. Door
open/close animation on mode transition; `CAct_Door` proximity auto-open via `MetaWalkAction`. *Verify:*
a friendly gnome opens & passes; an enemy is blocked; a Troll opens a `Trolltor`, a dwarf cannot.
**Size: M.**

**Phase L1 — Ladders usable (fixes "sticks to bottom") — IMPLEMENTED.**
Implement `create_ladderlogic` + `set_ladderrange` → the FUN_0060b1b0 up-scan (bounded by range 22/32/40
and wall continuity) → stamp the climb band (climb bit + negative cost) into `NavWorld`. Implement the
`set_snaptowall` z-settle (reuse `TunnelTorchTick`) so the ladder seats and the `0x400001` gate passes.
*Verify:* `NavWorldRealTerrainTests`-style test — a ladder in a vertical shaft produces a climb band
spanning ≈range height; `NavWorldService.FindPath` routes a gnome from shaft-bottom to shaft-top via the
band; `ClimbAction` executes it. **Size: M–L.**

**Phase L2 — Ladder mount/dismount + dedicated anims — IMPLEMENTED.**
`ground_to_ladder` / `ladder_climb_up|down` / `ladder_to_ground` clips (slots 14-18) instead of the free
`climb_*`; mount offset `±DAT_0073709c` / `+DAT_00737138` (CAct_Ladder). *Verify:* the climb reads
as a ladder, mounts at the base, dismounts at the top. **Size: S–M.**

**Phase E1 — Elevators — REPAIRED.**
`create_elevatorlogic` + cabin dummy + `set_elevatorproperties`; holder-type-0 shaft placement;
boxed production and cabin visibility; platform movement (ACCMODE/acceleration/braking); and the
call/board/dwell/ride/exit/interruption action recovered in §§2.4-2.9. The rider table has sixteen
backing slots but native capacity 4 (crystal: 1), and carries from the cabin linkage rather than a
synthetic drive position. EditMode coverage is in `ElevatorTransportRepairTests`; the test runner
gate intentionally owns execution, followed by fresh manual S22 verification.

---

## 7. Cross-references worth knowing

1. **AI door control** (`ai/std_ai.tcl:42,138,345,2358-2360`): `bOpenDoors` gates whether an AI clan
   auto-opens its doors; the AI calls `set_doorproperties $pitem openforall|openforfriends`. Doors are
   enumerated as `-type {production elevator …}` objects. Confirms doors participate in the normal
   object-query/AI layer.
2. **`opendoor`** (`story/Trigger_Crystal.tcl:297`, `sequences/Kristall/kri_137.tcl`) is a **story
   proc** for the crystal-world cutscene gates (`+opendoor left/right`), **not** a general engine
   command. Registered as a plus-variant in the port (`DigglesTclEngine.cs:5882`).
3. **`elevator` is a `get_objtype`** value (`std_ai.tcl:227`, `z_work_common.tcl`), shared by lifts AND
   ladders (both `metal elevator 2`). Ladders are excluded from some AI/production loops via the
   explicit `lsearch {Leiter Leiter_Kristall Leiter_Metall}`.
4. **Tech-tree**: all classes are in `gameplay/gen_tt.tcl` (buildable) — no special-casing needed for
   placement beyond the wall-target rule.

---

## 8. Confidence map

**SOLID (decompile-verified, byte-level):**
- Every Tcl→native handler mapping in §0 (each handler's own `CallStat` string confirms its name).
- `obj+0x3ec` TransportLogic holder + the 3 logic slots/types.
- The shared `obj+0x254..+0x268` influence rect (identical in ladder/door/`set_pf_influence` writers).
- Door mode enum 0-5 and default-closed ctor; `closed ⇒ obj+0x264 = 0x7FFFFFFF`.
- `set_ladderrange → LadderLogic+0x28`; `set_snaptowall → obj+0x3d0 bit 0x20000`; `set_elevatorproperties`
  field layout.
- Ladders/doors delegate gnome usage to `CAct_MetaWalk` (CAct_Ladder/CAct_Door build it).
- Elevator state I/O, live dispatch/tick, directional stop selection, both motion modes, the
  fixed/dynamic rider record layouts, cabin-relative linkage, action states, and cancellation paths
  (§§2.4-2.6).
- Elevator placement mode 3 and its target-15/target-11 shaft-head probes (§2.7).
- The former port stub status and the "sticks to bottom" root cause; the affected door/ladder
  commands and wall-depth settle are now implemented.

**INFERRED (structure clear, not fully byte-traced):**
- Exactly how `obj+0x264` cost reaches Calc1's edge cost (13-layer stamp + dirty-rect re-derivation vs.
  live query) — §3.3. Port design is correct either way.
- FUN_0060b1b0 exact stop conditions (wall continuity ±1 nibble, zone match, range) — logic read but
  not single-stepped.
- The door influence box corners are `(X0,Y0,X1,Y1) = (-8,-20,8,4)` (obj+0x254/+0x258/+0x25c/+0x260,
  FUN_0060ceb0:78-81) — resolved. What remains open is only the world-axis reading of X vs Y (ant-farm
  frame) when converting to the port's sub-grid; verify against a live door footprint when coding.
- The port mapping from the native elevator track registry to sparse navigation transport segments.

**OPEN:**
- Confirm in Play that all three authored lift variants align the cabin mesh and rider feet at the
  top and bottom stops. The port intentionally represents both native registry variants by a typed
  sparse source-handle overlay; no gameplay behavior currently requires the original global array.
- Whether ladder/troll-door meshes are authored to *span* visually or are single props relying on the
  band (affects only visuals, not gameplay).
- Trap ↔ influence connection (no evidence found; assumed none).
- `DAT_007377b0` exact value (the Y world→sub-cell scale used in FUN_0068f430 / FUN_0060b1b0;
  `DAT_0073752c = 8.0` is the X scale).

---

## 9. Port status snapshot

Implemented command surface:
- `create_doorlogic`, `set_doorproperties`, `create_ladderlogic`, `set_ladderrange`,
  `delete_transportlogic`, and `set_pf_influence` are real handlers.
- Object-level `set_snaptowall` / `get_snaptowall` and `set_forceipol` / `get_forceipol` are real
  handlers. `set_snaptowall` also performs the native wall-depth settle used by unboxed ladders;
  boxed and contained products are excluded.
- `create_elevatorlogic` and `set_elevatorproperties` create the typed shaft transport, retain the
  registered/computed-track selector, and drive functional call/ride/movement integration.
- `set_fstopper` and `rem_fstopper` are implemented by the fluid simulation; they remain independent
  of pathfinding.

`NavWorld` now owns an overlap-safe sparse influence overlay with hard-block counts, additive cost
bias, and temporary climb-surface counts. `Calc1Pathfinder` reads the effective overlay values. Door
path planning evaluates per-mover access (owner, diplomacy, or class), and the runtime transport tick
opens/closes qualifying doors by proximity with the native animation aliases. Ladder logic scans the
wall, projects its bounded `-20` climb band, and tags ladder path segments so `ClimbAction` uses the
dedicated ladder mount, climb, and dismount aliases. Automated coverage exists for overlap/removal,
door detours and access modes, conditional opening, ladder placement/projection, boxed depth
preservation, wall-depth settle, elevator three-variant shaft placement/product boxing, route
tagging, call/board/ride/exit/interruption phases, cabin-relative passenger carry, linear motion,
accelerated braking, and transport-length fog-of-war shaft reveal.

**Strictly-additive influence law:** the native writer stamps only
COST (FUN_0060b600:88-98) — no influence may overwrite the natural nav state. The port's ladder
climb-column overlay is additive counts, and `NavWorld.EffectiveExitSub` substitutes the ladder
column only where the terrain offers no FLOOR crossing (wall tiles / overlay-only tiles); a
corridor floor tile under the band keeps its terrain ground-line crossing, and influence removal
restores the exact pre-ladder derived state (regression:
`NavWorldTests.LadderInfluence_IsStrictlyAdditive_AndRemovalRestoresPreLadderState`).

---

## 10. The exact addresses (for the implementer)

```
Tcl handlers (Tcl/TclObjMethods_*.cpp):
  create_ladderlogic FUN_006798f0 CreateLadderLogic (2 args)
  create_elevatorlogic FUN_006792c0 CreateElevatorLogic (4 args)
  create_doorlogic FUN_00679d30 CreateDoorLogic (2 args)
  delete_transportlogic FUN_006796e0 DeleteTransportLogic (2 args)
  set_ladderrange FUN_00679b40 SetLadderRange (3 args) → LadderLogic+0x28
  set_doorproperties FUN_00679f80 SetDoorProperties (3-4) → DoorLogic+0x18 / +0x30
  set_elevatorproperties FUN_00695a60 SetElevatorProperties (5-7) → ElevatorLogic+0x218..+0x228
  set_pf_influence FUN_0068f430 SetPFInfluenceRect (8) → obj+0x254..+0x268
  set_snaptowall FUN_0067d4b0 SetSnapToWall (3) → obj+0x3d0 |0x20000
  get_snaptowall FUN_0067d6e0 GetSnapToWall
  class_snaptowall FUN_00614740 (class, classdef+0x2c bit 8)

Logic records (Objects/TransportLogic.cpp):
  holder obj+0x3ec {type, +4 Elevator*, +8 Ladder*, +0xc Door*}
  ElevatorLogic ctor FUN_00607b10 (0x122 B) fields +0x1fc/+0x200/+0x204/+0x208/+0x20c/+0x210/+0x214/+0x218/+0x21c/+0x220/+0x224/+0x228
  LadderLogic ctor FUN_0060ad40 (0x561 B) settle FUN_0060bac0 (cnt→9) writer FUN_0060b600 up-scan FUN_0060b1b0
  DoorLogic ctor FUN_0060c520 (0x765 B) writer FUN_0060ceb0 default mode +0x18=1 (closed)
  TransportLogic update FUN_00607000 (elevator tick → FUN_0060a860)
  Elevator state I/O FUN_006075b0 (serialize / restore; not movement)
  Elevator target select FUN_00608b50
  Elevator cabin movement FUN_00609050
  Elevator rider update FUN_0060a860

Influence stamp:
  FUN_005a3dd0 get/create 13-layer influence singleton DAT_00772428
  FUN_005a3fd0 rectangle stamp (layer-mask 7 = layers 0|1|2; clamp to DAT_0077241c+0x94..+0xa0)
  FUN_006c2340 per-layer dirty-rect accumulator (DSFastArray)

Gnome-side actions (Actions/Act_*.cpp):
  CAct_Ladder FUN_004111b0 → CAct_MetaWalk to mount (±DAT_0073709c x, +DAT_00737138 z), ctor FUN_00415d10 dist -1.0
  CAct_Door FUN_0040cf60 build / FUN_0040d230 tick → CAct_MetaWalk to door, ctor FUN_00415d10
                 with 13.5 (0x41580000) = the metawalk's DESTINATION DEPTH z (MetaWalk+0x24 →
                 WalkFloor+0x44 → FUN_0041a2f0:41-42), NOT an arrival distance (§3.1 correction);
                 open/close = DoorLogic membership FUN_0060d220:80-103 / FUN_0060d470:43-44
  CAct_Elevator FUN_0040e700 tick; FUN_0040d970 approach; FUN_0040dc60 board;
                 FUN_0040df10 exit; FUN_0040e1f0 dismount; FUN_0040d4a0/FUN_0040ed10/FUN_0040ed60 cancel
```

---

## 11. Tcl reference index (call sites)

- Ladders/elevators: `classes/items/transport.tcl` (Leiter/Leiter_Metall/Leiter_Kristall,
  Aufzug/Dampfaufzug/Kristallaufzug + cabins).
- Native doors: `classes/items/tueren.tcl` (Holz/Stein/Metall/Kristalltuer) → `calls/tueren.tcl`
  (create_doorlogic + set_doorproperties openforfriends + evt_btn_on toggle).
- Troll doors + scripted doors: `classes/items/tuerundtor.tcl` (Trolltor_*/Trolltuer via
  create_doorlogic; Tuer_kaserne/metall/verlies/kristall + Gefaengnis_gitter + Zauntor_a via
  `calls/doors.tcl`).
- Scripted-door influence + switches: `classes/items/calls/doors.tcl` (set_pf_influence),
  `classes/items/Schalter.tcl` + `classes/items/calls/switcher.tcl`.
- AI: `ai/std_ai.tcl` (bOpenDoors, set_doorproperties openforall/openforfriends).
- Anim slots: `classes/characters/rh_globals.tcl`, `hamster.tcl`, `riesenelfe.tcl`, `drachenb.tcl`
  (ground_to_ladder / ladder_climb_up|down / ladder_to_ground).

---

## 12. Contradictions with existing docs/memory (flag before implementing)

1. **`set_pf_influence` "patches tile+0x06 to INT_MAX"** — the port comment
   (`DigglesTclEngine.cs:1642`, and the memory note `reference_engine_command_gap`) is **imprecise**.
   `tile+0x06` is a signed byte (`NavWorld.BaseCost` is `sbyte[]`, `NavWorld.cs:35`) and **cannot store
   INT_MAX**. RE shows `set_pf_influence` writes a **per-object influence rectangle**
   (`obj+0x254..+0x268`, cost in `+0x264`) stamped into a separate 13-layer influence system
   (FUN_0068f430 / FUN_005a3fd0), not a direct `+0x06` poke. The *effect* (covered tiles become
   impassable) is consistent, but the port must implement a **dedicated overlay**, not write INT_MAX
   into `BaseCost`. (§3.3, §5.)
2. **`set_snaptowall` "already RE'd for torches"** — the memory note (`reference_dig_underfoot_mount_law`
   / `reference_gui`… lighting) describes **`class_snaptowall`** (FUN_00614740, classdef+0x2c bit 8).
   The **object-level `set_snaptowall`** (FUN_0067d4b0) that ladders call (transport.tcl:308) sets the
   same runtime flag (obj+0x3d0 bit 0x20000) but is a **different command and is currently unregistered**
   in the port. Not a conflict — a clarification: ladders need the object command wired, then the
   existing torch z-settle reused.
3. **No conflict** with `ORIGINAL_PATHFINDING_RE.md` — it never claimed to cover the influence system;
   this doc extends it. The frame (native Y = gameplay vertical; ant-farm side view) is consistent with
   the tall-Y door/ladder rectangles here.
```

