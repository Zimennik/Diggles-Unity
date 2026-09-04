> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# Sparetime + Idle Behavior — RE Spec (`sparetime-idle`)

Reverse-engineered from the original Tcl (`data/Scripts/classes/zwerg/*.tcl`), the
decompiled native (`re/decompiled/Diggles/_global/*.c`), and `Docs/ENGINE_TCL_API.txt`.
Cross-checked against the current port (`Assets/Diggles/Runtime/...`).

> AXIS NOTE: this area is logic-only (clock + AI state machine); no world-frame axes
> are load-bearing here except `get_gnomeposition` (floor vs wall — handled elsewhere).

---

## 1. Overview

A dwarf's autonomy is a Tcl **state machine** layered on a native **worktime clock**.
Two orthogonal axes decide what a gnome does each tick:

1. **`current_plan`** — `"work"` vs `"sparetime"`, derived from the clock window via
   `get_remaining_sparetime` (and toggled by `evt_time_startwork` /
   `evt_time_startsparetime`, `zwerg.tcl:344-354`).
2. **`current_occupation`** — `"work"`, `"idle"`, `"spare"`/eat/slp/fun/bth/ill, set by the
   active state handler (`z_spare_main.tcl:2`).

The **idle** state (`zwerg.tcl:278`, and the richer `work_idle` in
`z_work_states.tcl:238`) is the "nothing to do right now" loop. Each idle tick it:
- calls `act_when_idle` (collision-avoidance shuffle + small wander + per-class idle action),
- checks `get_remaining_sparetime` to decide work-dispatch vs sparetime-dispatch,
- and plays a stand idle animation (`set_idle_anim`), self-pacing via
  `state_disable; action wait 1 {state_enable}`.

The **sparetime** subsystem (`z_spare_*.tcl`, ~5000 lines) is a full needs simulation
(eat/sleep/fun/bath/reproduce/socialize) that only runs when the gnome is OFF the clock.
For the scope of *idle behavior* the load-bearing entry/exit is the
idle⇄sparetime⇄work hand-off; the deep sparetime economy is documented at a contract level.

---

## 2. The worktime clock (native)

### 2.1 Time base — `gettime`, `gethours`, `gametime`

| Tcl cmd | Native | Returns |
|---|---|---|
| `gettime` | reads `*(double*)(DAT_00772400+0xa088)` | game time in **seconds** |
| `gethours` | `(float)gettime / DAT_00737630` | game time in **logic hour-units** |
| `gametime` | clock control (start/stop/reset/factor) | — |

`DAT_00737630` is the **seconds-per-logic-hour** divisor (a runtime float; not in the
decompile's data dump). It appears identically across the engine wherever "current hour"
is needed: `FUN_006955d0:59` (get_remaining_sparetime), `FUN_0050ab10:765-769` (clock
display), `FUN_005c83f0:176`, `FUN_0068b790:110`, `FUN_00645410:254`, `FUN_00551370:57`,
`FUN_0050cae0:49`. (`ENGINE_TCL_API.txt:669-672`.)

### 2.2 The 12-hour one-hand clock — `FUN_0050ab10:765-769`

The HUD clock derives display fields from `hours = round(gettime/DAT_00737630)`:
```c
DAT_00772dd8 = round(gettime / DAT_00737630); // total logic hours
hourOfDay = (round(gettime/DAT_00737630) / 0xf0) % 0xc + 1; // 0xf0=240, 0xc=12
day = (DAT_00772dd8 / 0xc) % 0x14 + 1; // 0xc=12, 0x14=20
yearOrMonth = round(gettime/DAT_00737630) / 0xb40 + 1; // 0xb40=2880
```
So the gameplay clock is **12 hours, one hand**: one clock-hour = **240 logic
hour-units** (`0xf0`), a full 12-hour revolution = **2880** logic-hour-units (`0xb40`).
There is **NO day/night** — just the single 12-position dial (matches the project memo
"NO day/night — single 12-hour one-hand clock").

### 2.3 Per-gnome work window — `get_worktime` / `set_worktime` / `auto_choose_workingtime`

(`ENGINE_TCL_API.txt:660-668, 1414-1417`; all three are **native** — `auto_choose_workingtime`
has NO Tcl proc, only the native command.)

- `set_worktime <obj> <start> <duration>` — start + duration in hour-units.
- `get_worktime <obj> [start|duration|end|nextstart|laststart|nextend|lastend]` — the
  window edges, and the **next/last edge timestamps** (so the gnome can compute when its
  next shift begins/ends).
- `auto_choose_workingtime <obj>` — native; staggers each gnome's shift start by
  population parity so the colony does not all rest at once. Called at gnome init
  (`zwerg.tcl:147`) and on baby→gnome (`baby.tcl:134`).

Observed values in the Tcl: starts of `0.0`/`6.0`, durations/`set_worktime` of `6.0`
(`baby.tcl:422-424`, `z_methods.tcl:274-278`). `baby.tcl:411-424` shows the migration rule:
count gnomes whose `get_worktime start` is the favourite slot, then assign the baby
`start=6.0` (if the favourite is crowded) or `0.0`, both `duration=6.0`.

### 2.4 `get_remaining_sparetime` — the work/rest gate

`get_remaining_sparetime <objref>` → **hours of sparetime left** (`ENGINE_TCL_API.txt:603-604`).
- `== 0.0` ⇒ it is currently **worktime** ⇒ idle states route to `work_dispatch`.
- `> 0.0` ⇒ it is **sparetime** ⇒ idle states route to `sparetime_dispatch` / idle anims.

Native `FUN_006955d0` (`CTclObjMethods::GetRemainingSpareTime`, name at line 31) is
Ghidra-MANGLED — the body collapses to TLS/exception boilerplate (lines 22-57, 86-97) and
the only surviving compute is `local_14 = (float)gettime / DAT_00737630` (line 59), i.e. it
reads the current logic-hour. The actual subtraction of the current hour against the
gnome's window (the part that yields the remaining hours) is in the inlined sparetime
singleton and was NOT reconstructed by Ghidra. **INFERENCE (from the Tcl contract +
clock model):** remaining = if `dayPhase∈window` then `0.0` else the hour-distance to the
next window start, where `dayPhase = (gethours) % 12`. This is consistent with all Tcl
callers (`==0.0` / `>0.0` / `hceil` / `<0.2`).

> UNRESOLVED: the exact native formula (and whether the unit is the 12-position
> clock-hour or the 240-unit fine hour) lives in `Sparetime.cpp` (`FUN_005f4a70` family),
> which Ghidra did not cleanly decompile. The port's `(GameTime/1800)%12` model is an
> assumption flagged for verification.

### 2.5 Sparetime PLACES — `sparetime` command — `FUN_006aadd0`

`FUN_006aadd0` = `CTclGlobalMethods::Sparetime` (name at line 51). This is the
**leisure-place registry**, not the clock. Subcommands (string-compared at lines
105/184/217/267):
- `sparetime <obj> announce <category>` (line 105) — registers `<obj>` as a sparetime
  place of `<category>`; reads the place's world pos from `obj+0xdc` (lines 143-144) and
  inserts into a category-indexed `DSFastArray` (`FUN_005f4ad0`/`FUN_005f4f80`).
- `sparetime <obj> disannounce` (line 184) — removes it (`FUN_005f4ad0` lookup,
  `FUN_005f4f80`).
- `sparetime <obj> queryrange <category> <rng>` (line 217) — nearest places of category
  within radius (`FUN_005f4b20`, line 243).
- `sparetime <obj> queryrect <category> <xn> <yn> <xp> <yp>` (line 267) — box query
  (`FUN_005f4d00`, line 316).
(`ENGINE_TCL_API.txt:1532-1536`.) The sparetime AI uses these to find pubs/beds/baths.

### 2.6 `prod_gnome_state` / `gnome_idle` — the engine "what is this gnome doing" flag

`prod_gnome_state <obj> <state> [...]` = `FUN_006a7230`
(`CTclObjMethods::ProdGnomeState`, name at line 45). It writes a one-byte state +
parameters onto the gnome's **prod-planner struct** `gnome+0xf9` (`puVar5`, line 75) so
the native work dispatcher (CProdPlanner) knows the gnome's disposition. String-compared
subcases (lines 96-411):
- `"idle"` → `*(byte*)(puVar5+1) = 1` (line 109) — marks the gnome **available**.
- `"sparetime"` → `FUN_005ebe50` (line 118) — marks off-clock.
- `"unknown"` → `FUN_005ebdf0` (line 100).
- `"walk"/"guard"/"fight"/"build"/"invent"/"pickup"/"putdown"/"pack"/"unpack"/`
  `"transport"/"harvest"/"dig"/"explore"` → write a typed work-target record into
  `puVar5[3..0xc]` (`puVar5[3]` = a type enum 4..0xf; `puVar5[7..9]` = target pos;
  `puVar5[0xa]` = block index). Mirrors the state→string switch in `FUN_005ec410`
  (`prod_gnome_state... ` read-back; case 1 = `"sparetime"`, case 0xe/0xf =
  default/`"explore"`).

`gnome_idle <obj> [true|false]` (`ENGINE_TCL_API.txt:679-680`) — "notify gnome is idle".
`prod_gnomeidle <obj> [true|false]` (`:1077-1078`) — "announce worker is idle". Both feed
the CProdPlanner idle pool. The Tcl raises these on entering/leaving `work_idle`
(`z_work_states.tcl:226,234`) and `prodfill_dispatch` (`z_work_prodfill.tcl:6,32`).

---

## 3. The idle state machine (Tcl)

### 3.1 The base `state idle` (`zwerg.tcl:273-320`)

Registered at gnome init (`state_trigger this idle`, `zwerg.tcl:180`). Each tick:
```tcl
state_enter idle { set idletimeout 0; gnome_idle this 1 } #:273
state idle {
    incr idletimeout                                            # :283
    # vacate a sparetime seat if we still hold one
    if {$sparetime_current_place_ref>0} { prod_guest guestremove ...; ... }   # :284-288
    if {!$died_in_fight} {
        if {$current_weapon_out||$current_shield_out} { weapon_putin; shield_putin; return }  # :291
        if {[act_when_idle]} { return }                         # :298  ← wander / avoid / idle-action
        if {[get_remaining_sparetime this]==0.0} {              # :299  ← WORKTIME
            state_triggerfresh this work_dispatch; return
        }
        if {$idletimeout>5} {                                   # :304
            if {[get_remaining_sparetime this]>0.0} {           # :306  ← SPARETIME
                state_enable this; state_triggerfresh this sparetime_dispatch; return
            } else {
                if {$sparetime_is_on} {sparetime_state_end}     # :312
                if {[get_gnomeposition this]&&[get_prodautoschedule this]} {walk_down_from_wall}
            }
        }
    }
    set_idle_anim                                               # :317  ← stand idle anim
    state_disable this; action this wait 1 {state_enable this}  # :318-319 ← SELF-PACING
}
```
`state_leave idle { gnome_idle this 0 }` (`:270`).

### 3.2 `work_idle` (`z_work_states.tcl:238-301`) — the richer idle used by the work plan

When the work-dispatch loop runs dry it routes to `work_idle` (`state_work_dispatch_proc`,
`z_work_states.tcl:166`). `state_work_idle_proc` extends the base idle:
```tcl
prod_gnome_state this idle #:247 ← engine flag
incr idletimeout; set current_occupation "idle" #:249-251
if {$idletimeout>3} { if {[act_when_idle]} {return} } #:252-255
if {$idletimeout>5} {
    if {[get_remaining_sparetime this]>0.0} { ...sparetime_dispatch; return }   # :256-261
    elseif {$sparetime_is_on} { sparetime_state_end }          # :262-264
}
if {$idletimeout>10} {
    if {$work_strike} { state_triggerfresh this strike }       # :267-270
    if {[get_gnomeposition this]==0} {
        if {rand()<0.25} { state_triggerfresh this prodfill_dispatch }   # :271-275 ← idle SHOWCASE
    } else { if {...&&![walk_down_from_wall]} {return} }       # :276-278
}
# hat (Muetze) swap to "fight"/"arbeitslos" after a counter #:281-296
set_idle_anim; state_disable this; action this wait 1 {state_enable this} #:298-300
```
Key extra behaviors vs base idle:
- **`prodfill_dispatch`** (25% chance once `idletimeout>10`, only on floor): the idle
  **showcase** animation state (`z_work_prodfill.tcl`). It picks a workplace-flavoured or
  default idle activity and plays a short anim sequence (e.g. `standsleep stretch carve
  knit handstand smokepipe`, `:86`), then returns to `work_idle`. Sets `gnome_idle 1` on
  enter, `0` on leave.
- **Idle hat swap** (`:281-296`): after `muetzen_counter` decrements below 0, an
  unemployed gnome swaps to the `arbeitslos` ("jobless") hat, or `fight` hat if
  autoscheduling is off.

### 3.3 `act_when_idle` (`z_procs.tcl:1266-1321`) — the wander / avoid micro-behavior

This is the per-tick "should I move out of the way / wander a little / do a little
action" routine. Returns 1 if it took an action (so the caller `return`s), 0 otherwise.

**On a wall** (`get_gnomeposition this` ≠ 0, `:1268-1294`): look for other Zwerg/Baby in a
bounding box `{-0.7 -1.0 -0.5 0.7 1.0 0.5}`; if any is not still-pathing
(`get_walkresult != 2`), try to step to one of the 4 neighbour cells
`{0 1.3 0}{0 -1.3 0}{-1.3 0 0}{1.3 0 0}` (height-fixed via `get_hmap`) that is unoccupied
and unlocked → `walk_pos`. Else return 0.

**On the floor** (`:1295-1320`):
- If this exact spot is **placelocked by someone else** (`placelock_check pos 0.8 myref`),
  leave it: `walk_random [irandom 2 4]` (`:1296-1299`).
- Else if a Zwerg/Baby occupies the close box `{-0.6 -0.3 -1.2 0.6 0.3 1.2}` and is not
  still-pathing, also `walk_random [irandom 2 4]` (`:1301-1309`).
- Else (truly idle) pop ONE entry off **`idle_action_list`** and `eval` it (`:1311-1318`).
  `idle_action_list` is a per-gnome queue of deferred one-off idle reactions, e.g.
  `bear_child <n>` (reproduction, `z_spare_reprod.tcl:110`) or `try_getoutof_water`
  (`z_spare_procs.tcl:946-947`). Initialized empty (`zwerg.tcl:98`). If empty → return 0.
- Sets `current_occupation "idle"`, returns 1.

### 3.4 `walk_random` / `walk_around` — the wander walk

```tcl
proc walk_random {plength} { # z_dignwalk.tcl:724-729
    set place [get_place -center [get_pos this] -circle $plength \
                         -mindist 0.7 -random $plength -walldist 1]
    if {[lindex $place 0]<1} {return false}                    # no spot found
    walk_pos $place; return true
}
proc walk_around {} { # z_dignwalk.tcl:884-887
    tasklist_add this "walk_random [irandom 2 4]"
    tasklist_add this "adjust_gnome_rotation"
}
```
So the **wander radius** is a random `get_place` target within a circle of
`plength = irandom 2..4` world units around the gnome, at least `0.7` from the centre and
`1` from walls. `walk_around` (used by `prodfill` & sparetime, e.g.
`sparetime_activities` `:326`) is the same walk plus a final rotation jitter.
`irandom 2 4` ⇒ plength ∈ {2,3} (`irandom min max` = `[min,max)`).

### 3.5 `set_idle_anim` (`z_procs.tcl:1695-1710`) — the stand idle pose

```tcl
proc set_idle_anim {} {
    if {[get_diedinfight this]} {return}
    if {[get_gnomeposition this]==0} {                         # on floor
        switch [hf2i [random 4]] {                             # uniform 0..3
            0 {set_anim this mann.stand_anim_a 0 2}
            1 {set_anim this mann.stand_anim_b 0 2}
            2 {set_anim this mann.stand_anim_c 0 2}
            3 {set_anim this mann.stand_anim_d 0 2}
        }
    } else {                                                   # on wall
        if {abs([get_roty this]-3.14)>0.1} {set_roty this 3.14}
        set_anim this mann.kletterstand_anim 0 2               # climb-stand
    }
}
```
`set_anim... 0 2` = play once, blend mode 2. So an idle floor gnome cycles randomly
through 4 stand variants; a wall-hanging gnome faces back (roty=π) and uses the
climb-stand. This is called EVERY idle tick that did not branch away, so the gnome subtly
re-poses while waiting.

### 3.6 `sparetime_activities` — the idle "fidget" pool (init, `z_spare_main.tcl:310-326`)

Built at obj_init as a list of one-shot fidgets the sparetime/fun code draws from:
`standloopa..d`, `jumpa`, `scratch`, `breathe`, `teeter_t`, `wait`, `scout`, `wipenose`,
`cough`, `teeter_w`, `kneebend`, `prod_waittime 2`, `walk_around`. (English clip names →
the German `mann.*` clips via the animset.)

### 3.7 `seq_idle` — extras' idle in cutscenes (`misc/seq_idle.tcl`)

A SEPARATE, weighted idle picker for **sequence statist roles** (dwarves standing around
in cutscenes). Weights live in `seq_idle_anims` (`zwerg.tcl:118-138`): `{10 {stand_anim_a}}`
… `{w1 {kletterstand_anim}}` (the `w` prefix = wall-only). `seq_idle` builds a weighted
table, filters by `get_gnomeposition` (floor vs wall), picks `irandom`, and chains the
anim sequence via `action this anim mann.<clip> {seq_idle}` (self-re-arming). Not used by
the live-AI idle path; relevant only to cutscene presentation.

---

## 4. The sparetime dispatch (entered FROM idle)

When idle decides it is sparetime (`get_remaining_sparetime>0`), it
`state_triggerfresh this sparetime_dispatch` (`zwerg.tcl:309`, `z_work_states.tcl:259`).
`sparetime_dispatch` (`z_spare_main.tcl:5-149`):
- On first entry (`sparetime_initialize`, `:27-75`) it resets ~40 sparetime vars, computes
  `sparetime_rest = hceil(get_remaining_sparetime)`, and seeds a **plan** `sparetime_future`
  — a list of activities for the rest of the period, length scaled by remaining hours:
  `0h→{}`, `1h→one of {eat slp}`, `2h→{eat slp eat}`-slice, `3h→{"" eat fun slp eat}`-slice,
  `4h→{eat fun slp eat}`, `≥5h→{eat fun slp bth eat}` (`:55-62`).
- Each loop: if `get_remaining_sparetime==0.0` → `sparetime_state_end` (back to work,
  `:82-86`); else `prod_gnome_state this sparetime` (`:87`); then `sparetime_decide_occup`
  (`:108`, the big needs-arbitration `proc` at `:378-710`) picks
  `current_wish_occupation` from attributes atr_Nutrition/Alertness/Mood/Hitpoints and the
  emergency logic; then it `start`/`loop`/`end`s the chosen occupation
  (`sparetime_${occup}_start/_loop/_end`, `:127-146`).
- `act_when_idle` is also called inside sparetime (`:135`) so the wander/avoid still runs.

`sparetime_state_end` (`:711-744`): closes the current occupation, sets `current_plan
"work"`, `sparetime_is_on 0`, decays talk issues, and `state_triggerfresh this
work_dispatch`. So sparetime always hands back to **work_dispatch**, never directly to
idle.

The deep economy (eat/slp/fun/bth/reproduce/socialize sub-states, the `funloss_*` mood
accounting, `event_sparewish_update` periodic recompute at `:745-847`, talk/reproduction)
is OUT OF SCOPE for the idle gap-list but is fully present in `z_spare_*.tcl` and gated
entirely behind `get_remaining_sparetime>0`.

---

## 5. Constants table

| Constant | Hex | Dec | Meaning | Source |
|---|---|---|---|---|
| Seconds per logic-hour | `DAT_00737630` | (1800 assumed) | `gethours = gettime/this` | FUN_006955d0:59, FUN_0050ab10:765 |
| Logic-hours per clock-hour | `0xf0` | 240 | `(hours/240)%12` | FUN_0050ab10:767 |
| Clock positions (one-hand) | `0xc` | 12 | `%12+1` hour-of-dial | FUN_0050ab10:767 |
| Logic-hours per 12h revolution | `0xb40` | 2880 | day/period rollover | FUN_0050ab10:769 |
| Day count modulus | `0x14` | 20 | `(hours/12)%20+1` | FUN_0050ab10:768 |
| Work window default start/dur | — | 0.0 / 6.0 | `set_worktime` observed | baby.tcl:422-424 |
| Idle→work_dispatch gate | — | `remaining==0.0` | worktime | zwerg.tcl:299 |
| Idle→sparetime gate | — | `remaining>0.0` && `idletimeout>5` | sparetime | zwerg.tcl:304-306 |
| Idle→prodfill chance | — | `rand<0.25` @ `idletimeout>10` | idle showcase | z_work_states.tcl:272 |
| Wander radius (plength) | — | `irandom 2 4` ⇒ {2,3} | walk_random circle | z_dignwalk.tcl:724, z_procs.tcl:1298 |
| Wander mindist / walldist | — | 0.7 / 1 | `get_place` constraints | z_dignwalk.tcl:725 |
| Avoid box (floor) | — | `{-0.6 -0.3 -1.2 0.6 0.3 1.2}` | neighbour check | z_procs.tcl:1301 |
| Avoid box (wall) | — | `{-0.7 -1.0 -0.5 0.7 1.0 0.5}` | neighbour check | z_procs.tcl:1269 |
| Placelock self radius | — | 0.8 (floor) / 0.7 (wall) | `placelock_check` | z_procs.tcl:1296,1286 |
| Idle anim variants (floor) | — | 4 (`stand_anim_a..d`) | `random 4` | z_procs.tcl:1700 |
| Wall idle facing | — | `roty=3.14` (π) | climb-stand | z_procs.tcl:1708 |
| Muetzen counter start | — | 7 | jobless-hat delay | zwerg.tcl:110 |
| prod_gnome_state idle flag offset | `+1` of `gnome+0xf9` | — | byte=1 | FUN_006a7230:109 |

---

## 6. Tcl ↔ native contract

| Tcl command | Native fn | Port status | Notes |
|---|---|---|---|
| `gettime` | reads gametime double | OK (`GameTime`) | seconds |
| `gethours` | `/DAT_00737630` | OK (`/1800`) | hour-units |
| `get_remaining_sparetime` | FUN_006955d0 (mangled) | **real but model-assumed** | `(GameTime/1800)%12` vs window |
| `set_worktime`/`get_worktime` | native | real | start/len only; **next/last edge variants NOT impl** |
| `auto_choose_workingtime` | native (no Tcl) | real | parity stagger; len=9 (orig used 6) |
| `sparetime announce/disannounce/queryrange/queryrect` | FUN_006aadd0 | real | place registry |
| `prod_gnome_state <obj> <state>` | FUN_006a7230 | **STUBBED "0"** | engine work-disposition flag |
| `gnome_idle <obj> 1/0` | CProdPlanner::GnomeIdle | real | feeds ProdDispatcher idle pool |
| `prod_gnomeidle <obj> 1/0` | native | **STUBBED "0"** | second idle announce |
| `get_gnomeposition` | FUN_0068d8a0 | real | floor(0)/wall(1) |
| `get_walkresult` | obj+0x248 | real | 2 walking / 4 reached |
| `get_place... -circle -random` | native PlaceFinder | **STUBBED "-1 0 0"** | ← BREAKS all wandering |
| `placelock_set/check/rem` | native | real | reservation list |
| `random`/`irandom` | native RNG | real | idle anim / wander pick |
| `act_when_idle`/`set_idle_anim`/`walk_random`/`walk_around` | (Tcl procs) | run if zwerg.tcl loaded | depend on `get_place`/anim |

---

## 7. How idle interacts with neighbour systems

- **Work dispatcher (native CProdPlanner / port ProdDispatcher):** idle registers the
  gnome via `gnome_idle this 1` (and natively `prod_gnome_state this idle`). The dispatcher
  picks idle gnomes for open jobs and raises `evt_autoprod_*` on them, which the work
  states convert into `current_worklist` tasks → leaves idle for `work_dispatch`.
  Idle ↔ work is therefore the dispatcher's input/output boundary. (Spec:
  `Docs/WORK_DISPATCHER_NATIVE.md`.)
- **Sparetime economy:** entered only from idle when `remaining>0`; exits back to
  `work_dispatch`. `act_when_idle` is shared (called from both idle and sparetime).
- **Animation controller:** `set_idle_anim`/`prodfill`/`seq_idle` drive `set_anim`/
  `action this anim`. The clip names are English (`stand_anim_a`, `kletterstand_anim`,
  `standsleep`…) resolved through the animset (`set_class_animset`).
- **Navigation:** `walk_random`→`walk_pos`→the MetaWalk executor; `get_gnomeposition`
  (floor/wall) decides floor vs climb idle pose and which avoid-box/anim to use.
- **Hats (Muetzen):** idle swaps to the `arbeitslos`/`fight` hat after a delay
  (`z_work_states.tcl:281-296`); sparetime swaps to the `sparetime` hat
  (`z_spare_main.tcl:8-11`).
- **placelock:** idle gnomes vacate a spot another gnome reserved, preventing pile-ups.

---

## 8. PORT GAP analysis (vs current code)

State of the port (`Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs`,
`DigglesGameSim.cs`, `DigglesTclEntity.cs`):

1. **`get_place` is a hardcoded stub `"-1 0 0"`** (`DigglesTclEngine.cs:950`). This is the
   single biggest idle gap: `walk_random` (`z_dignwalk.tcl:726`) bails on `[lindex
   $place 0]<1`, so **idle gnomes never wander, never vacate occupied/locked spots, and
   `act_when_idle` only ever pops `idle_action_list`**. The visible "idle gnomes mill
   around / step out of the way" behavior is entirely absent. Needs a real PlaceFinder
   (`get_place -center -circle -mindist -random -walldist -nearpos` against NavWorld
   floor cells + placelock + obj occupancy). HIGH priority for faithful idle.

2. **`prod_gnome_state` is bulk-stubbed to "0"** (`DigglesTclEngine.cs:2186`). The Tcl
   calls `prod_gnome_state this idle` (`z_work_states.tcl:247`) and `... sparetime`
   (`z_spare_main.tcl:87`) every tick. Stubbing it means the **native idle/work
   disposition flag** (`gnome+0xf9 +1`) is never set, so a port that later reads it (or a
   dispatcher keyed on it) can't tell idle from sparetime from working. Currently the port
   routes idle eligibility through `gnome_idle` (which IS real), so this is latent, not
   yet fatal — but it is a correctness gap. Implement as a per-entity `ProdState` enum +
   target record (mirror the `FUN_006a7230` cases). MEDIUM.

3. **`prod_gnomeidle` bulk-stubbed "0"** (`:2186`) — second idle-announce; redundant with
   `gnome_idle` for the pool but the Tcl may branch on it. LOW.

4. **`get_remaining_sparetime` clock model is ASSUMED, not RE-confirmed**
   (`DigglesTclEngine.cs:532-542`): `(GameTime/1800)%12` vs `[WorkStart,WorkStart+WorkLen)`.
   The native formula (`Sparetime.cpp`/`FUN_005f4a70`) is un-decompiled. Also the port
   uses `WorkLen=9` default (`DigglesTclEntity.cs:117`) and `auto_choose_workingtime` sets
   len=9 (`DigglesTclEngine.cs:571`), but the original observed `set_worktime` durations
   are **6.0** (`baby.tcl:422-424`). This makes the port's work/rest ratio (9:3) differ
   from the original (6:6) — gnomes rest far less than intended. MEDIUM (gameplay-feel).

5. **`get_worktime` sub-queries `nextstart/laststart/nextend/lastend/end/duration`**
   (`ENGINE_TCL_API.txt:663-668`) are only partially implemented — the port handles
   `start` and `len`/`duration`-prefixed (`DigglesTclEngine.cs:555-559`) but **not the
   next/last edge timestamps**, which `z_spare_reprod.tcl:144-145,179-182` reads to match
   couples' shifts. Reproduction-timing logic will misbehave. LOW (sparetime depth).

6. **`evt_time_startwork` / `evt_time_startsparetime` edge events are not raised by any
   clock tick.** The original toggles `current_plan` on these events
   (`zwerg.tcl:344-354`); the port derives plan only via `get_remaining_sparetime`. Since
   the idle/work states re-poll `get_remaining_sparetime` directly each tick this is mostly
   covered, BUT mid-task states (`work_active`/`work_breakable`, `z_work_states.tcl:308,319`)
   switch to sparetime by testing `current_plan=="sparetime"`, which is ONLY set by those
   events. Without them a gnome will not abandon a long task when its shift ends.
   `WORK_DISPATCHER_NATIVE.md:133` already specs raising these on the clock edge. MEDIUM.

7. **`prodfill_dispatch` (idle showcase anims) — verify it's reachable.** The port loads
   `z_work_prodfill.tcl` (`zwerg.tcl:170`), and `work_idle` triggers it at 25%/`idletimeout>10`
   (`z_work_states.tcl:272`). This depends on `rand`/`get_prodautoschedule`/anim playback
   working — likely fine, but no test covers it. The richer per-workplace fidgets
   (`z_work_prodfill.tcl:49-86`) need the workplace `prod_*` plumbing. LOW.

8. **`set_idle_anim` / idle-anim cadence works only if the animset clip names resolve.**
   The Tcl uses `mann.stand_anim_a..d`, `mann.kletterstand_anim`. Confirm these map in the
   port's animset (the anim-controller memo). If a clip is missing the gnome T-poses while
   idle. LOW-VERIFY.

9. **State-think gate band-aid** (`DigglesGameSim.cs:150`): the sim skips state-think while
   an `ActiveAction` runs IF the tasklist is empty. Idle self-paces with `action wait 1
   {state_enable}`, so an idle gnome with an active wait-action and empty tasklist will not
   re-think until the wait finishes — which matches the original (idle's own pacing). But
   the comment flags this as a band-aid for commanded walks; if idle ever needs to react
   FASTER than its 1-second wait (e.g. an urgent avoid), it can't. LOW (faithful for now).

---

## 9. Unresolved

- **Exact `get_remaining_sparetime` native formula and its unit** (12-position clock-hour
  vs 240-unit fine hour). `FUN_006955d0` is Ghidra-mangled; the compute is inlined from
  `Sparetime.cpp`/`FUN_005f4a70` which did not decompile cleanly. Port model assumed.
- **Value of `DAT_00737630`** (seconds-per-logic-hour). Assumed 1800 from the docs; not in
  the decompile's data dump. If it differs, the entire clock cadence (and `gethours`,
  `auto_choose_workingtime`, the HUD dial) shifts.
- **Original worktime duration** — observed `6.0` in Tcl, but whether the live game keeps a
  fixed 6h shift or `auto_choose_workingtime` natively varies it is not confirmed (the
  native `auto_choose_workingtime` body was not RE'd; only its call sites).
- **`prod_gnome_state` work-target record layout** (`puVar5[3..0xc]`): reconstructed from
  `FUN_006a7230` writes (type enum + pos + block index) but the consuming side
  (CProdPlanner read of `gnome+0xf9`) was not traced, so field semantics beyond pos/type
  are INFERENCE.
- **`idle_action_list` full population set** — found `bear_child` and `try_getoutof_water`;
  other deferred idle reactions may be appended elsewhere (not exhaustively grepped).
- Whether the port currently reaches `state idle` at all in a live colony (vs sitting in
  `work_idle`) — needs a Play/headless trace; the two share most behavior but differ on
  `prodfill`/hat-swap.
