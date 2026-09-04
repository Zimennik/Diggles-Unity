> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# RE: Tcl WORK Layer (`tcl-work-layer`)

Complete reverse-engineering of the dwarf (`Zwerg`) WORK behaviour layer — the
Tcl-side state machine, tasklist queue, and the `evt_task_*` / `evt_autoprod_*`
event handlers that turn a click or an autonomy decision into a sequence of
walk → act → repeat sub-actions.

**Sources (line-cited throughout):**
- `original/data/Scripts/classes/zwerg/zwerg.tcl` — class def; base states `idle`/`task`/`trapped`; obj_init.
- `original/data/Scripts/classes/zwerg/z_work_states.tcl` — the WORK states (`work_auto`, `work_dispatch`, `work_idle`, `work_active`, `work_breakable`, `mad_scientist*`).
- `original/data/Scripts/classes/zwerg/z_events.tcl` — ALL `evt_task_*` / `evt_autoprod_*` handlers (2181 lines).
- `original/data/Scripts/classes/zwerg/z_work_common.tcl` — build/pack/repair/conquer/muetze/divingbell/use-item task generators.
- `original/data/Scripts/classes/zwerg/z_work_prod.tcl` — production-station task helpers (`prod_*`).
- `original/data/Scripts/classes/zwerg/z_dignwalk.tcl` — `walk_pos`/`walk_near_item`/`walkfail`/`note_pathlength`/dig chain.
- `original/data/Scripts/classes/zwerg/z_procs.tcl` — `stop_prod`, `act_when_idle`, `notify_*`, `start_prod`.
- Native command surface is NOT name-mapped in the decompile symbol table
  (`re/decompiled/Diggles.symbols.tsv` has only `FUN_<addr>`); the engine
  commands (`tasklist_*`, `state_*`, `gnome_idle`, `gnome_failed_work`,
  `get_walkresult`, `prod_gnome_state`) are confirmed NATIVE (no `proc` def in
  any `.tcl`) via `re/notes/tcl_engine_commands.tsv`. Their *semantics* are
  inferred from the Tcl call-sites; flagged INFERENCE where the engine internals
  are not byte-verified.
- Port: `unity/Diggles/Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs`,
  `.../Runtime/Engine/Sim/DigglesGameSim.cs`, `.../Runtime/Engine/Sim/ProdDispatcher.cs`.

---

## 1. Overview

A dwarf is a per-object Tcl interpreter (`this` = self) running a **state
machine** + a **task queue** ("tasklist"). The engine ticks each entity's
current-state BODY once per sim step (`SrvDoThink`), but ONLY while the entity is
**state-enabled** (an enable-count ≥ 0). Long sub-actions (walk, anim, wait) are
issued via the native `action`/`walk_action` API which **disables** the state
(`state_disable this`) and re-enables it from a completion callback — so the
state machine pauses while the dwarf physically does something, then resumes.

There are two parallel mechanisms that drive WORK:

1. **State bodies** (`state work_dispatch {...}`, `state idle {...}`, …) — the
   per-tick "what do I do now" logic. They consult the world, set animations,
   and call `state_trigger`/`state_triggerfresh` to move between states.

2. **The tasklist** — a per-object FIFO of Tcl command strings. The dispatching
   states (`work_dispatch`, `work_idle`, `task`, `mad_scientist`) check the
   tasklist FIRST: if non-empty, pop index 0 and `eval` it; otherwise run the
   state's own proc. Each popped command typically issues ONE sub-action (a
   walk, an anim) that disables the state until done — so the tasklist is
   drained one entry per "resume", giving the walk→act→repeat cadence.

Work *enters* the layer in two ways:
- **User command** → `set_event this evt_task_<X>` → `evt_task_<X>_proc`
  (direct, `notify_userevent`, `current_plan="work"`).
- **Autonomy** (native `CProdPlanner`) → `set_event this evt_autoprod_<X>` →
  `evt_autoprod_<X>_proc` (`notify_autoevent`, sets `current_worktask`, routes
  through `work_auto`).

Both converge on `work_dispatch`, which executes a `current_worklist` of
high-level tasks (one task = a full sub-chain pushed onto the tasklist).

---

## 2. The state set (zwerg.tcl + z_work_states.tcl)

`zwerg.tcl:9-16` lists them:
```
// idle - idle
// task - independent task
// work_dispatch - workplace assigned, working a worklist
// work_idle - workplace assigned, no task (waits / strikes / sparetime)
// work_active - workplace assigned, executing task (sparetime-interruptible)
// work_breakable - workplace assigned, executing breakable task
// sparetime - sparetime (separate doc)
```
Plus `work_auto` (event-routing entry), `trapped`, `mad_scientist`,
`mad_scientist_dance`.

Boot: `zwerg.tcl:179-181`:
```
state_reset this;# enable-count = 0
state_trigger this idle;# CurrentState = idle (runs state_enter idle)
state_enable this;# enable-count = 1 → state body now ticks
```

### 2.1 `idle` (zwerg.tcl:269-320) — the home state

`state_enter idle` (273-276): `set idletimeout 0; gnome_idle this 1`.
`state_leave idle` (269-271): `gnome_idle this 0`.

Body (278-320), per tick:
1. `incr idletimeout`.
2. Leave any sparetime place guest slot.
3. If not dead-in-fight:
   - If a weapon/shield is out → `weapon_putin; shield_putin; return`.
   - `if {[act_when_idle]} {return}` — step out of the way of other gnomes / off
     a locked place (see §6.3).
   - **`if {[get_remaining_sparetime this]==0.0} {state_triggerfresh this work_dispatch; return}`**
     ⇒ during WORKTIME the idle dwarf immediately flips to `work_dispatch`. THIS
     is the idle→work edge. (z_procs note: `get_remaining_sparetime`=0 means "no
     sparetime left = it's worktime".)
   - If `idletimeout > 5` and sparetime > 0 → `state_enable this;
     state_triggerfresh this sparetime_dispatch`. Else end sparetime; if on a
     wall and prodautoschedule → `walk_down_from_wall`.
4. Fall-through: `set_idle_anim; state_disable this; action this wait 1 {state_enable this}`
   — play an idle anim, pause the state for 1 s, re-enable. This is the
   **self-pacing** idiom every state uses.

### 2.2 `task` (zwerg.tcl:322-342) — user one-shot driver

Body:
```
if {[tasklist_cnt this]==0} { state_trigger this idle };# queue empty → home
else { set current_occupation "work"
       set command [tasklist_get this 0]; tasklist_rem this 0; eval $command }
```
`state_leave task` (338-342): `unlock_item`.
So a user `walk`/`pickup`/`dig` event fills the tasklist then
`state_triggerfresh this task`; the task state drains one entry per resume and
falls back to `idle` when empty.

### 2.3 `work_auto` (z_work_states.tcl:9-11 + proc 80-151) — autonomy router

`state work_auto {state_work_auto_proc}`.
`state_work_auto_proc` (80-151): reads `current_worktask` (set by an
`evt_autoprod_*` handler) and dispatches:
```
set current_occupation "work"; set idletimeout 0
switch on current_worktask:
  pickup → trigger work_dispatch; tasklist_add "prod_getfromanywhere $current_workclass"
  pickupitem →... tasklist_add "pickup $current_workitem"
  bringprod →... tasklist_add "prod_transporttoprodlist {..} $current_workplace"
  transferprod →... tasklist_add "prod_bringtoprod $current_workitem $current_workplace"
  work →... tasklist_add "prod_autobuild $current_workclass $current_workplace"
  dig →... tasklist_add "dig_starttask {$current_digpos}"
  pack →... tasklist_add "prod_autopack $current_workplace"
  buildup →... tasklist_add "prod_autobuildup $current_workplace"
  unpack →... tasklist_add "prod_autounpack $current_workplace {$current_workpos}"
  repair →... tasklist_add "prod_autorepair $current_workplace"
  (default) → state_trigger this work_idle
```
Each branch does `state_trigger this work_dispatch` THEN `tasklist_add`. Because
`work_dispatch` checks the tasklist first (next tick), the single pushed
`prod_auto*`/`dig_starttask` task runs — and those procs build the
`current_worklist` and re-`state_triggerfresh this work_dispatch` (§3, §5).

### 2.4 `work_dispatch` (z_work_states.tcl:13-22 + proc 154-212) — the worklist engine

State body (13-22):
```
state work_dispatch {
  if {[tasklist_cnt this] > 0} { pop 0; eval; return };# drain sub-actions first
  state_work_dispatch_proc;# else advance the worklist
}
```
`state_work_dispatch_proc` (154-212): the **production worklist driver**.
```
set current_occupation "work"; set idletimeout 0
if {[llength $current_worklist]==0} { set current_workplace 0; state_trigger this work_idle; return }
set current_workdelay 2
set currenttask [lindex $current_worklist 0]
catch { set evalresult [eval $currenttask] } evalerrMsg;# run task 0 of the worklist
if {result not boolean} { log warning }
else if {$evalresult} {;# task SUCCEEDED
    set prod_failurecount 0
    set current_worklist [lreplace $current_worklist 0 0]    ;# pop the worklist
    if {$current_workplace ∉ {0,"dig"}} { call_method $current_workplace prod_progress }
    return
} else {;# task FAILED (returned false)
    if {$current_plan=="sparetime"} { state_triggerfresh this sparetime_dispatch; return }
    incr prod_failurecount
    if {$prod_failurecount < 5} { state_disable this; action this wait $current_workdelay {state_enable this} }
    else { set prod_failurecount 0; pop worklist; state_triggerfresh this work_idle }
}
```
KEY: **two queues.** `current_worklist` = high-level tasks (each one a proc that
pushes a sub-chain onto the **tasklist**). `work_dispatch` evaluates worklist
task 0; that proc PUSHES tasklist entries (the walk/anim sub-steps) and returns
`true` immediately. Next ticks, the state-body's `tasklist_cnt>0` branch drains
those sub-steps. When the sub-chain is exhausted, the body falls to
`state_work_dispatch_proc` again which pops the next worklist task. A failed
task → 5 retries with a 2 s `wait` then gives up to `work_idle`.

### 2.5 `work_idle` (z_work_states.tcl:24-42 + proc 215-301)

Dispatch wrapper (33-42) is the same tasklist-first pattern. `state_enter`
(24-26 → proc 215-227): `stop_prod; current_worktask=""; current_workclass=0;
last_event=""; event_repeat=0; gnome_idle this 1`. `state_leave` (28-31 → 230-235):
`gnome_idle this 0`.

`state_work_idle_proc` (238-301), per tick:
```
prod_gnome_state this idle; incr idletimeout; set current_occupation "idle"
if {idletimeout>3} { if {[act_when_idle]} return }
if {idletimeout>5} { if sparetime>0 → state_enable; state_triggerfresh sparetime_dispatch
                      elseif sparetime_is_on → sparetime_state_end }
if {idletimeout>10} { if work_strike → state_triggerfresh strike
                      if not on a wall && rand<0.25 → state_triggerfresh prodfill_dispatch
                      elseif on wall && prodautoschedule && !walk_down_from_wall → return }
... muetze swap to "fight"/"arbeitslos" hat after idle...
set_idle_anim; state_disable this; action this wait 1 {state_enable this}
```
So `work_idle` is the "assigned but nothing to do" parking state — it offers the
dwarf back to the dispatcher (`gnome_idle 1`), runs idle anims, can drift to
sparetime / strike / prodfill (decorative idle anims).

### 2.6 `work_active` / `work_breakable` (z_work_states.tcl:44-49, procs 304-324)

Thin: just `if {$current_plan=="sparetime"} {state_triggerfresh this sparetime_dispatch}`.
These are entered by production-station scripts that want a sparetime-interruptible
(`work_active`) vs. only-at-safe-point-interruptible (`work_breakable`) section.

### 2.7 `mad_scientist` / `mad_scientist_dance` (z_work_states.tcl:53-74, procs 327-416)

Tasklist-first wrappers that queue random invent/dance anims. Used by the
inventor (`Bp*` invention) station idle. Out of WORK-dispatch scope but listed
for completeness.

### 2.8 `trapped` (zwerg.tcl:27)

3-phase (`trap_mode` 0→1→2) trap/petrify state; on entry `gnome_failed_work this;
tasklist_clear this`. Returns to `idle` on `trap_mode==2`. Pre-empts work.

---

## 3. Tasklist mechanics (native)

Per-object FIFO of command STRINGS. Native commands (no Tcl `proc`); confirmed in
`tcl_engine_commands.tsv`. Surface, as used by the WORK layer:

| Command | Behaviour (INFERENCE from call-sites; engine not byte-verified) |
|---|---|
| `tasklist_add this CMD` | append CMD to tail |
| `tasklist_addfront this CMD` | insert CMD at head (index 0) — used to build a chain in REVERSE so it executes in author order (see `prod_bringtoprod`) |
| `tasklist_get this I` | return entry I ("" if out of range) |
| `tasklist_cnt this` | count |
| `tasklist_rem this I` | remove entry I (note: index-based here; the port removes by VALUE — see PORT GAP) |
| `tasklist_clear this` | empty it |
| `tasklist_find this CMD` | index of first exact match, -1 if none |
| `tasklist_list this` | whole list (for logging) |

**Dispatch loop** (every dispatching state): `if {[tasklist_cnt this]>0} {set c
[tasklist_get this 0]; tasklist_rem this 0; eval $c; return}`. So exactly ONE
entry is popped + evaluated per state tick, and the popped command usually
`state_disable`s until its sub-action completes — that's the throttle.

**`addfront` reverse-build idiom** (z_work_common.tcl:91-94, `prod_bringtoprod`):
```
tasklist_addfront this "prod_transfertoprod...;play_anim bendb";# 4th to run
tasklist_addfront this "play_anim benda";# 3rd
tasklist_addfront this "rotate_towards {$itempos}";# 2nd
tasklist_addfront this "walk_near_item {$itempos} 0.7";# 1st (head)
```
Result order: walk → rotate → benda → (transfer + bendb).

---

## 4. State-transition primitives (native)

| Command | Semantics |
|---|---|
| `state_trigger this S` | run `state_leave OLD` (if any), set CurrentState=S, run `state_enter S` (if any). Body NOT run here — body is ticked by the game loop. `state_trigger this` (no S) clears state. |
| `state_triggerfresh this S` | same + **reset enable-count to a clean enabled value** so the new state ticks immediately regardless of prior `state_disable`s. (Used at every event entry so a half-disabled prior action can't freeze the new task.) |
| `state_enable this` | enable-count += 1 |
| `state_disable this` | enable-count -= 1 |
| `state_reset this` | enable-count = 0 |
| `state_get this` | current state name |
| `state_getenablecnt this` | enable-count |

**Enable-count gate (INFERENCE, matches port + all Tcl idioms):** the engine
ticks the state body only while enable-count ≥ the "enabled" threshold. The Tcl
idiom is always paired: `state_disable this; action this X {state_enable this}` —
disable, issue an engine action whose finish-callback re-enables. So
`state_disable`/`state_enable` are balanced; `triggerfresh` resets the balance to
avoid leaks across a state change. The port models "enabled" as `count >= 0` and
"frozen" as `count < 0` (DigglesGameSim.cs:127). The original's exact threshold
is not byte-verified — `state_reset` setting it to 0 + `state_enable` to 1 right
after at boot (zwerg.tcl:179-181) is consistent with "ticks while count > 0", but
the codebase also disables to negative transiently; **flagged INFERENCE**.

---

## 5. The walk→act→repeat task chain (concrete examples)

### 5.1 Walk — `evt_task_walk_proc` (z_events.tcl:336-362)
```
set evtpos [event_get this -pos1]
gnome_failed_work this; tasklist_clear this; kill_all_ghosts; stop_prod
... event_repeat dedup vs last_event...
notify_userevent
tasklist_add this "walk_pos {$evtpos}"
state_triggerfresh this task
set_objworkicons this; prod_gnome_state this walk "$evtpos"
```
`task` pops `walk_pos`; `walk_pos` (z_dignwalk.tcl:590-604):
```
beam_back; note_pathlength $pos; state_disable this
walk_action "-target {$pos} -waving 1 -forcetarget 0" { walkfail } { /* break */ }
```
`walk_action` is native: it drives the move and on FAIL runs the 1st block
(`walkfail`), on user-break runs the 2nd. The state stays disabled until
walk_action's internal completion re-enables (via the move executor), then `task`
sees the empty tasklist and returns to `idle`.

### 5.2 Dig — `evt_task_dig_proc` (z_events.tcl:1019-1055) → the repeat chain
```
gnome_failed_work this; tasklist_clear this; kill_all_ghosts; stop_prod 0|1
notify_userevent
set current_plan "work"; set current_workplace "dig"; set current_digpos $evtpos
tasklist_add this "dig_starttask {$evtpos}"; prod_change_muetze stone
state_triggerfresh this work_dispatch
```
`work_dispatch` body pops `dig_starttask` (z_dignwalk.tcl:136-164):
```
gnome_announce_dig this $startpos
set edgepos [get_digedge $startpos this];# native: nearest standable floor edge by the rock
if {edge valid} {
  tasklist_add this "walk_pos_dig {$walkpos} {$edgepos}";# walk to the edge
  tasklist_add this "dig_continue";# then start carving
  return true
}
```
`walk_pos_dig` (705-722) walks with `-dig 1 -digpos {..}`. `dig_continue`
(205-...) computes the next dig point, queues `dig_anim` + a follow-up
`dig_continue` (recursion), advancing one carve per resume — the canonical
**walk → act → repeat** loop. It self-terminates when `get_remaining_sparetime
!= 0.0` (sparetime starts) or `current_worktask != "dig"` or the edge runs out.

### 5.3 Production (autonomy) — `evt_autoprod_workat_proc` (z_events.tcl:1127-1168)
```
tasklist_clear this; stop_prod; notify_autoevent
set current_plan "work"; set current_worktask "work"
set current_workplace [-subject1]; set current_workclass [-text1]
... set work icons; prod_change_muetze <category>...
state_triggerfresh this work_auto
```
`work_auto` (`current_worktask=="work"`) → `state_trigger work_dispatch` +
`tasklist_add "prod_autobuild $current_workclass $current_workplace"`.
`prod_autobuild` (z_work_common.tcl:268-278):
```
set current_worklist [call_method $current_workplace get_itemtasklist $itemtype [get_ref this]]
prod_gnome_last_workplace this $current_workplace
if {[llength $current_worklist] > 0} { state_triggerfresh this work_dispatch }
return true
```
So the production STATION supplies the worklist via `get_itemtasklist`
(walk-to-dummy / anim / create-product steps), and `work_dispatch` runs it task
by task, calling `prod_progress` on the station after each success.

### 5.4 Buildup/Pack/Unpack/Repair — `prod_auto{buildup,pack,unpack,repair}`
(z_work_common.tcl:280-342) all follow the same shape: set
`current_workplace`/`current_worklist` from a fixed list of sub-tasks (walk →
changetool → `prod_buildup_rep`/`prod_builddown_rep` recursion → beam_back), then
`state_triggerfresh this work_dispatch`. The `*_rep` procs recurse by re-queuing
themselves with an incremented buildup-step until done (animated multi-step
build), each step gated by an anim that disables the state.

---

## 6. Work-support procs

### 6.1 `gnome_failed_work this` (native)
Called at the TOP of nearly every `evt_task_*` handler and in `walkfail`. Releases
the dwarf's job reservation so the planner can re-offer it. Port:
ProdDispatcher.ReleaseGnome — clears `WorkBusyCooldown` and removes the gnome's
placelock (DigglesTclEngine.cs:1991-1992).

### 6.2 `note_pathlength` / `planned_pathlength` (z_dignwalk.tcl:937-945)
```
proc note_pathlength {pos {lock 1}} {
  if {[lindex $pos 0]<1} {return}
  set pathlength [expr abs([get_posx this]-[lindex $pos 0])+abs([get_posy this]-[lindex $pos 1])];# Manhattan X+Y
  if {$lock} { placelock_set $pos [expr $pathlength*2] $myref };# reserve destination for 2×dist ticks
  if {$pathlength > 5} { fincr planned_pathlength $pathlength };# accumulate "intended travel" stat
}
```
Called by every `walk_*`. The destination placelock prevents two dwarves
converging; the duration scales with distance so a far walk holds its spot
longer. `planned_pathlength` is a per-gnome counter (sparetime/AI tuning).

### 6.3 `walkfail` (z_dignwalk.tcl:910-935)
The walk_action FAIL callback:
```
placelock_rem $myref
if {[get_walkresult this] != 4} {;# 4 = "arrived/ok"; else genuine fail
  if {$walkfail_tasks==""} {
    tasklist_clear this; kill_all_ghosts; stop_prod; gnome_failed_work this
    state_triggerfresh this idle                   ;# give up → home
  } else { foreach e $walkfail_tasks {eval $e}; state_enable this };# retry hook
} else { set walkfail_tasks ""; state_enable this };# actually fine, resume
```
`get_walkresult` codes used across the codebase: **2 = walking/in-progress, 4 =
done-OK** (4 is the "no real failure" sentinel here; std_ai/hamster also branch
on `!=4`). `walkfail_tasks` is an optional retry list (e.g. `walk_down_from_wall`
sets it to wait+clear).

### 6.4 `act_when_idle` (z_procs.tcl:1266-1321)
Idle housekeeping. On a wall: if another `Zwerg/Baby` overlaps and isn't itself
walking (`get_walkresult != 2`), pick one of 4 cardinal neighbours and walk there
(step aside). On the ground: if standing on a placelock, or another non-walking
gnome overlaps, `walk_random`. Returns 1 if it issued a move (caller `return`s).

### 6.5 `stop_prod {toolchange 1}` (z_procs.tcl:1213-1264)
The teardown run at the start of every new task (AFTER `tasklist_clear`). Mood
penalty for the interrupted activity (switch on `state_get`), unlock items, reset
dig id, remove placelocks, end any current production (`stop_prod_tasklist` from
the station), `current_workplace=0`, `current_worklist=""`. NOTE the comment:
"muss *NACH* tasklist_clear aufgerufen werden!" — order matters because
`stop_prod` itself may `tasklist_add` station-teardown steps.

### 6.6 `start_prod` (z_procs.tcl:1197-1207)
`set tsklist [call_method $prodplace start_prod_tasklist [get_ref this]]; foreach
itm {tasklist_add this $itm}`. Used by `evt_task_workprod` to begin a station job.

### 6.7 `notify_userevent` / `notify_autoevent` (z_procs.tcl:1923)
Set `last_eventtype` to `"user"`/`"auto"` and (`user`) stamp
`last_userevent_time=[gettime]`. The 15-second "recent user command" guard in
`evt_task_attack_proc` (z_events.tcl:1591) and `evt_task_defend_proc` (1631) reads
these so AI auto-attacks don't override a fresh player order.

### 6.8 `prod_gnome_state this STATE [arg]` (native, stub in port)
Pure presentation hook (work-icon/announce); does NOT drive the state machine.
Safe as a no-op (port: silent stub, DigglesTclEngine.cs:2186).

---

## 7. evt_task_* and evt_autoprod_* — the full handler table

All `def_event` + `handle_event` wired in z_events.tcl:10-328; procs in the
`else` branch (332+). Common preamble for USER tasks: `gnome_failed_work this;
tasklist_clear this; kill_all_ghosts; stop_prod; notify_userevent;
state_triggerfresh this task|work_dispatch`. Common preamble for AUTO tasks:
`tasklist_clear this; stop_prod; notify_autoevent; set last_event "";
state_triggerfresh this work_auto|work_dispatch`.

### USER (evt_task_*)
| Event | Proc:line | Sets up | Target state |
|---|---|---|---|
| `evt_task_walk` | 336 | `walk_pos {pos}` | task |
| `evt_task_pickup` | 406 | `pickup $item` (immediate) | task |
| `evt_task_convert` | 438 | `convert $item` | task |
| `evt_task_useitem` | 512 | pickup if needed + `use_item $item` | task |
| `evt_task_objaction` | 549 | walk to standoff + `objaction_item` | task |
| `evt_task_kidnap` | 584 | walk_near + rotate + `kidnap` | task |
| `evt_task_bombe` | 617 | `handle_bomb` | task |
| `evt_task_snapitem` | 652 | host `snapping_action` | task |
| `evt_task_open_box` | 702 | `open_box {item}` | task |
| `evt_task_putdown` | 738 | `putdown_tasklist` (+ in-flight cancel logic) | task |
| `evt_task_dropall` | 821 | anim + `beamto_world_all` | task |
| `evt_task_buildup` | 858 | send-out gnomes + `unpack_pos_unfixed` | task |
| `evt_task_harvest` | 900 | `harvest $item` | **work_dispatch** |
| `evt_task_mine` | 942 | `mine {item} 30` | **work_dispatch** |
| `evt_task_switch` | 985 | `switch_item {item}` | task |
| `evt_task_dig` | 1019 | `dig_starttask {pos}` | **work_dispatch** |
| `evt_task_workprod` | 1058 | `start_prod $workplace` (owner-gated) | work_idle |
| `evt_task_prodattack` | 1087 | `fight_startfight` | (fight) |
| `evt_task_attack` | 1547 | 15s-userevent guard; `fight_startfight` | (fight) |
| `evt_task_defend` | 1623 | walkresult/CT guard; `fight_startfight` | (fight) |
| `evt_task_buildingconquer` | 2154 | `conquer $s1` | work_dispatch |

Fight events (`attack`/`defend`/`prodattack`) are deliberately the "ignore work"
path — they pre-empt the WORK layer (and the 15 s guard prevents auto-fight from
stomping a fresh user work order). `evt_task_defend_proc:1631` early-returns if
`get_walkresult this == 2 && get_last_eventtype == "user"` (don't abandon a
user-commanded walk to auto-defend).

### AUTO (evt_autoprod_*) — native CProdPlanner raises these
| Event | Proc:line | current_worktask | → work_auto branch |
|---|---|---|---|
| `evt_autoprod_workat` | 1127 | "work" | prod_autobuild |
| `evt_autoprod_pickup` | 1175 | "pickupitem" | pickup item |
| `evt_autoprod_pickup_store` | 1209 | "pickupitem" | pickup item |
| `evt_autoprod_pack` | 1240 | "pack" | prod_autopack |
| `evt_autoprod_buildup` | 1258 | "buildup" | prod_autobuildup |
| `evt_autoprod_unpack` | 1291 | "unpack"/"repair" (HP-gated) | prod_autounpack / prod_autorepair |
| `evt_autoprod_bringprod` | 1334 | "bringprod" | prod_transporttoprodlist |
| `evt_autoprod_transferprod` | 1361 | "transferprod" | prod_bringtoprod |
| `evt_autoprod_harvest` | 1405 | (direct) — non-Pilz redirects to mine | harvest / mine |
| `evt_autoprod_mine_new` | 1446 | "mine" | `mine {ref} 3` → work_dispatch |
| `evt_autoprod_walk` | 1480 | "walk" | `walk_pos` → work_dispatch |
| `evt_autoprod_dig` | 1514 | "dig" | dig_starttask → work_auto |

Note `evt_autoprod_harvest_proc:1413` redirects to `evt_autoprod_mine_new_proc`
if the target's class isn't `Pilz` — so adding ore classes to the harvest planner
gets mining "for free" (the port relies on this, ProdDispatcher.cs:182-194).

---

## 8. Who wins: idle vs task, and `state_triggerfresh`

- A new user/auto event ALWAYS calls `state_triggerfresh` (not `state_trigger`).
  `fresh` resets the enable-count so a previously `state_disable`d action (e.g. a
  half-finished idle `wait`) cannot keep the new state frozen. This is the
  mechanism by which a click instantly pre-empts whatever the dwarf was doing.
- The event preamble also `tasklist_clear this` + `stop_prod` +
  `gnome_failed_work this`, so the prior task's queued sub-steps and reservations
  are wiped before the new chain is queued. Auto handlers SKIP `gnome_failed_work`
  (they're the planner re-tasking, not a user override) and skip `kill_all_ghosts`
  (commented out) — they're gentler.
- `idle`→`work_dispatch` is driven purely by `get_remaining_sparetime==0`
  (worktime) inside the idle body — there is no event for "start working";
  the dwarf polls. During sparetime the same poll routes to `sparetime_dispatch`.
- `event_repeat` / `last_event`: a repeated identical user event (same target,
  near same pos) sets `event_repeat 1` (e.g. z_events.tcl:352, 421) — read
  downstream to vary anims / skip redundant re-walks (the `putdown` handler at
  763 uses it to amend an in-flight putdown chain rather than restart).

---

## 9. Tcl ⇄ native contract (summary)

| Tcl call | Native role |
|---|---|
| `state_trigger`/`triggerfresh`/`enable`/`disable`/`reset`/`get` | state machine; engine ticks the current-state BODY each SrvDoThink while enabled |
| `tasklist_*` | per-object FIFO; states drain it |
| `action this {wait\|anim\|rotate\|walk}... {finishcode} {breakcode}` | long sub-action; pairs with `state_disable`+`state_enable` in finishcode |
| `walk_action "-target {..}..." {failcode} {breakcode}` | pathed move; runs failcode (→`walkfail`) on fail |
| `gnome_idle this 0\|1` | (de)register the gnome in the planner's idle pool; on `1` the planner may immediately re-task it |
| `gnome_failed_work this` | release job reservation + cooldown |
| `get_walkresult this` | 2=walking, 4=done-OK (others=fail) |
| `placelock_set/check/rem` | destination reservation list (`note_pathlength`, `act_when_idle`) |
| `prod_gnome_state` / `prod_assignworker` / `prod_progress` | station/AI bookkeeping & presentation (no state-machine effect) |
| `set_event this evt_X -...` ← native | how the planner / user injects a task |

The native `CProdPlanner` is the only
producer of `evt_autoprod_*`; the GUI is the only producer of `evt_task_*`. Both
land in the gnome's PendingEvents queue and fire on the next sim step's dispatch
phase.

---

## 10. PORT GAP analysis (vs current C#)

Port files: `DigglesTclEngine.cs`, `DigglesGameSim.cs`, `ProdDispatcher.cs`.

**Faithful / present:**
- All `tasklist_*` and `state_*` commands registered
  (DigglesTclEngine.cs:88-93, 131-161). State bodies ticked per step, gated on
  `StateEnabledCount` (DigglesGameSim.cs:117-152) and `Parked` (cutscene freeze).
- `state_triggerfresh` resets enable-count then runs leave/enter
  (DigglesTclEngine.cs:3701-3727); body ticked from the loop (TickEntityState
  4147-4154) — matches the original split (enter/leave once, body per-tick).
- `action`/`walk_action` finishcode chain run in object scope, with break
  inheritance (DigglesGameSim.cs:64-93) — supports the `state_disable;
  action {state_enable}` idiom.
- `gnome_idle`/`gnome_failed_work` wired to ProdDispatcher
  (1983-1994). `evt_autoprod_*` raised by ProdDispatcher with the EXACT native
  step→Tcl map (ProdDispatcher.cs:280-305), running the UNCHANGED Tcl handlers.
- `get_remaining_sparetime` gates idle→work and the dig loop (532-542).
- `get_walkresult` returns the entity's WalkResult (789-793).

**Gaps / deviations (ordered by impact):**

1. **`tasklist_rem` removes by VALUE, not by INDEX.**
   Port: `TaskList.Remove(a[2])` (DigglesTclEngine.cs:136). Original `tasklist_rem
   this 0` means "remove entry at INDEX 0". `List.Remove("0")` removes the first
   entry whose STRING == "0" (almost never present) → **the dispatch pop is a
   no-op; the same task re-evaluates forever.** Every dispatching state does
   `tasklist_get this 0; tasklist_rem this 0; eval`. This is the single most
   load-bearing bug in the layer. FIX: `tasklist_rem` must parse `a[2]` as an int
   index and `RemoveAt`. (The `putdown` cancel logic at z_events.tcl:783 also
   passes an index.) — VERIFY against original engine, but every call-site passes
   a numeric index, never a command string.

2. **State-think skipped while an action runs (band-aid).**
   DigglesGameSim.cs:150 `if (ent.ActiveAction != null && ent.TaskList.Count == 0)
   continue;`. The code's own comment admits the fully-faithful model is "no
   action-gate; states pace themselves via state_disable". With gap #1 fixed the
   tasklist drains, so this gate mostly matters for commanded click-walks that
   don't yet adopt `state_disable`. Track for removal.

3. **Enable-count threshold modeled as `< 0 = frozen`, `>= 0 = enabled`.**
   DigglesGameSim.cs:127. The original boot is `reset(0)+enable(→1)` and disables
   to negative transiently. If the true engine threshold is `> 0` (not `>= 0`), a
   state left at exactly 0 would tick in the port but not the original. Low risk
   (every real pause goes negative), but **INFERENCE** — worth a Play check that a
   freshly `state_reset`+un-`enable`d dwarf doesn't think.

4. **`prod_gnome_state`, `prod_assignworker`, `prod_progress` are no-op stubs**
   (2181-2188). Correct for behaviour (presentation/AI bookkeeping), but
   `prod_assignworker` returning constant `"1"` (951) means
   `evt_zwerg_workannounce_proc`'s confirm path always "succeeds" — fine while
   the station model is thin, but when stations gain real worker slots this must
   reflect actual assignment.

5. **`get_itemtasklist` / `start_prod_tasklist` / `stop_prod_tasklist`** (the
   station-supplied worklists) depend on the production-station classes being
   ported. Until then `prod_autobuild` gets an empty `current_worklist` →
   `work_dispatch` falls straight to `work_idle`. Not a work-LAYER bug; a
   station-content gap. (CollectProductionJobs gates on `Bp<Class>` owner attrib +
   `ProdSlots`, ProdDispatcher.cs:236-253.)

6. **`act_when_idle` / `walk_down_from_wall` / `set_idle_anim`**: confirm the port
   provides these procs (they're Tcl, loaded from z_procs/z_dignwalk) — if a Tcl
   file isn't loaded for runtime gnomes the idle body throws. (Not verified here;
   the engine loads class files at init per reference-tcl-init-loading.)

---

## 11. Unresolved / needs further RE

- **Exact `state_enable`/`state_disable` threshold** and whether
  `state_triggerfresh` sets the count to 0 or 1 — needs the native
  `CObj::HandleStates` / state-trigger functions byte-verified (Ghidra; not
  name-mapped yet). Current port `>=0` is an assumption.
- **`get_walkresult` full code set** — 2 (walking) and 4 (done-OK) are confirmed
  by Tcl branches; values 0/1/3 (idle/blocked/aborted?) are inferred, not RE'd
  from the native move state machine.
- **`tasklist_rem` index-vs-value** — needs one decompile confirmation that the
  native pops by index (all 30+ call-sites pass an integer, so behaviourally
  certain, but cite the engine to be safe before changing the port).
- **`prod_progress` semantics** — called after each successful worklist task
  (work_dispatch:191); whether it advances a station counter that gates job
  completion is a STATION concern (genericprod.tcl) outside this layer.
- **`start_prod_tasklist` / `get_itemtasklist` station contract** — the exact
  list shape each production class returns (walk-dummy / anim / create-product
  ordering) is per-class and lives in the station scripts, not RE'd here.
- **`work_strike` / `prodfill_dispatch` / `strike` states** — referenced by
  work_idle (z_work_states.tcl:267-275) but defined in z_work_strike.tcl /
  z_work_prodfill.tcl; only the entry edges are covered here.
- **`event_repeat` downstream consumers** — set widely; full list of readers (anim
  variation, putdown amend) only partially traced.
