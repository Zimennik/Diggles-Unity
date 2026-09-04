> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

# dispatcher-reconcile — Native Work Dispatcher (CProdPlanner / WorkScheduler) RECONCILED

Reconciliation + extension of `Docs/WORK_DISPATCHER_NATIVE.md` against the decompile, with the
complete pipeline order (planning → `evt_autoprod_*` raise → Tcl work chains → movement) and the
dispatcher↔movement interface contract. Every native claim below is cited file:line against
`the decompile<addr>_*.c`. The port under review is
`Assets/Diggles/Runtime/Engine/Sim/ProdDispatcher.cs` (note: the old doc's path
`Runtime/Sim/ProdDispatcher.cs` is WRONG — actual is `Runtime/Engine/Sim/ProdDispatcher.cs`).

---

## 0. VERDICT (TL;DR)

- The old doc's **two-layer** model is CONFIRMED and both layers run in the SAME server tick:
  `CGameThread::SrvDoThink` (006b4a30) → **(A)** `FUN_00424680` (CAiCentral tick) →
  `CProdPlannerMgr::Tick` (00432720) → `CProdPlanner::MaintenanceTick` (00433a70) [dig/harvest/
  produce/research/pickup, emit via `FUN_0043a8a0`], and **(B)** `FUN_005eed70` (WorkScheduler/
  transport tick) → `FUN_005f3600` (greedy assign) → `FUN_005f3a50` (emit) [pack/unpack/harvest/
  dig/research/produce/bringprod/buildup/mine]. SrvDoThink calls BOTH (006b4a30 calls 00424680 and
  005eed70; see the SrvDoThink callee set).
- The two emit paths use **different type numbers** but the **same `evt_autoprod_*` names** —
  reconciled in §3 with exact per-type tables read from the byte-level switch.
- The old doc has **THREE concrete errors** now corrected by the decompile:
  1. **WalkResult values**: doc said "1=walking, 2=blocked". NATIVE writes obj+0x248 = **2=walking
     (not-finished), 3=no-passage/fail, 4=arrived, 5=fail2** (004075b0/00407430/00417de0). The PORT
     (`2/3/4`) is CORRECT; the doc was wrong.
  2. **dig event payload**: doc said `evt_autoprod_dig -pos1 {tx ty}` (2 ints). NATIVE emits
     `-pos1 {%f %f %f}` — **3 floats** from the job's world position (0043a8a0:101, 005f3a50:96).
  3. **handler→state**: doc said all `evt_autoprod_*` go "→ work_auto". ACTUAL is MIXED:
     dig & workat → `state_triggerfresh this work_auto` (z_events.tcl:1537, :1167); harvest, mine,
     walk → `work_dispatch` (z_events.tcl:1439, :1472, :1506).
- The gnome-match cost is **NOT a navmesh path-cost** (as the port's `Dist2` implies). NATIVE
  (`FUN_004317a0`) uses a **weighted squared distance** `dx² + (dy·wY)² + (dz·wZ)²` over a
  reachability-filtered floor-cell scan, where wY=`DAT_007374cc`, wZ=`DAT_00737058` (004317a0:75-78).
  The port's match metric drops the Z (depth) term and both weights — a PORT GAP (§7).
- AssignCooldown=**2** is CONFIRMED from the decompile (set at 00434bc0:18, 00433a70:278/290/370/440;
  field obj+0x3e8 = `+1000` decimal). The port's `AssignCooldown=2` is correct.
- The need-scaled radius formula is CONFIRMED: `radius = need² · DAT_00737680 + DAT_00737578`,
  clamped to `DAT_00737700` when `≥ DAT_00736f78` (00436d40:141-143). The port's
  `need*need*K + base` is structurally faithful; the K constants' numeric values are rodata and
  NOT recoverable from the decompile text (UNRESOLVED).

---

## 1. PIPELINE ORDER (production planning → event raise → Tcl chain → movement)

```
CGameThread::SrvDoThink (FUN_006b4a30) // server think, once per server tick
 ├─ FUN_00424680 (CAiCentral tick)
 │ ├─ per-player loop i=0..7, gate (local_14 + i*6 + owner_base) % 0x32 == 0 [00424680:30]
 │ │ → FUN_0042a650 / FUN_00555b60 (per-player AI think, staggered 1/50)
 │ ├─ FUN_004313c0 + a 0x96(=150)-cell sweep of DAT_00c3c964 (dig-mark grid GC) [00424680:56-74]
 │ └─ CProdPlannerMgr::Tick (FUN_00432720) [00424680:76]
 │ ├─ build the per-player gnome lists: for every CObj with +0x36c!=-1 &&
 │ │ +0x3e0!=0, push into planner[owner].gnomeList [00432720:44-61]
 │ ├─ if FUN_00565a10 (time-accumulator gate) → CProdPlanner::MaintenanceTick(planner)
 │ │ then DAT_00c3c968 = (DAT_00c3c968+1) & 0x80000007 (player cursor &7) [00432720:75-82]
 │ └─ FUN_0043fd30
 └─ FUN_005eed70 (WorkScheduler / transport tick)
      ├─ gate: DAT_0076837a flag set AND elapsed ≥ DAT_007372a8 (the interval)        [005eed70:40]
      ├─ for player p=0..7:                                                           [005eed70:47-125]
      │    FUN_005f0070 + FUN_005f0190   (build demand grid: production needs)
      │    FUN_005f2380(...)             (score gnome×job transport cost matrix)
      │    FUN_005f2040
      │    FUN_005f3600(planner_p)       (greedy max-score assign → FUN_005f3a50 emit) [005eed70:122]
      └─ adaptive throttle: if this tick took >20/40/60/80/100 ms, push next-fire time
           forward by escalating DAT_* (load-shedding)                                [005eed70:128-141]
```

### CProdPlanner::MaintenanceTick (FUN_00433a70) — the dig/produce planner, phase by phase
Param = `planner` (the per-player CProdPlanner, owner at `planner+4`).

1. **Collect idle gnomes** (00433a70:71-135). Walk all live CObj (slot table at `DAT_0077244c+0x488`,
   id list `+0x84b1`/count `+0x84b5`). For each obj whose `+0xdb == planner.owner` (00433a70:89):
   - if `obj[0xf8]!=0` (is-a-worker flag) → append into `planner+0x1c[planner+0x20++]` (the idle pool)
     (00433a70:90-108).
   - if `obj[0xf9]!=0` (has a work struct): try a job: `FUN_0043a050(obj)` (00433a70:115). If a job
     index is found and `FUN_0043a290` validates the step, commit via `FUN_0043d4b0 + FUN_0043a0c0 +
     FUN_0043a7b0` (00433a70:124-131).
2. **Run the job collectors** in fixed order, each followed by `FUN_00433780` (a flush): [00433a70:169-178]
   `FUN_00434e60` (ProdAllocator pass-1: score building 'places' by demand) →
   `FUN_00436440` → `FUN_00435f50` → `FUN_00436750` → **`FUN_00436d40` (DIG allocator)** →
   `FUN_004375c0`.
3. **Decay job records** at `planner+0x5c` (stride 0x18): `rec[0x14]--`; drop the record when its
   counter `< 1` by `memmove`-compacting the array (00433a70:180-216). This is the per-job TTL.
4. **Second object pass** (LAB_00433f87, 00433a70:250-353): re-walk all owner objects, advance each
   gnome's work-struct step cursor (`obj[0xf9]` fields), set busy cooldowns `obj+0x3e8 (=+1000) = 2`
   (00433a70:278,:290), and (for type-2 step `obj[0xf9]+0xc==2`) push the gnome into the final
   assignment list `planner+0x2c[planner+0x30++]` (00433a70:331-351).
5. **Final assign loop** over `planner+0x2c` (00433a70:355-407): for each gnome
   `j = FUN_0043a050(gnome)`; clear `gnome.workstruct+0x48 = 0`; if `j==-1` → release
   (`FUN_0043a0c0`, set give-up flag) else → `gnome+1000 = 2; gnome+0x35e = 1;
   FUN_0043a460(gnome, j)` — **`FUN_0043a460` is the ASSIGN that emits the event**.
6. **Tail** (00433a70:409-451): for any building 'place' with `place.f[+0x18] > 0.0` (unmet need),
   pull one more gnome via `FUN_0043a050`/`FUN_0043a460` (the "hungry building grabs a worker").

### The ASSIGN → EMIT chain (the dispatcher↔Tcl bridge)
```
FUN_0043a460(gnome, jobIdx) [0043a460]
 ├─ rec = planner.jobRecs[jobIdx*0x10 + planner+0x6c]; rec[+4]++ (advance step cursor)
 ├─ if step in range and FUN_0043a290(step) validates:
 │ scan the job's step-list (rec[+8], stride 0x34) for a type-10 (dig) step → if found,
 │ FUN_0043bc90(gnome, x,y,z) (stash the dig target on the gnome) [0043a460:55-73]
 │ FUN_0043a630(gnome, rec[+8] + stepCursor*0x34) // emit THIS step [0043a460:79]
 └─ else → FUN_0043d4b0 / FUN_0043a0c0 / set gnome.workstruct+4 = 1 (give up)
FUN_0043a630(gnome, step) [0043a630]
 ├─ register the job target in planner+0x34 list (dedup) [0043a630:26-53]
 ├─ FUN_0043a8a0(&buf, step) → formats "set_event %s evt_autoprod_*..." into buf [0043a630:54]
 └─ if buf non-empty → FUN_0054ea20(buf) // QUEUE the command string into the Tcl interp [0043a630:70]
```
`FUN_0054ea20` is the Tcl-command enqueue (the native equivalent of our `set_event` →
`PendingEvents`). The string is run later in the gnome's object interp, invoking the unchanged
`evt_autoprod_*_proc`.

---

## 2. THE GATE & AMORTIZATION (FUN_00565a10, the player cursor)

`FUN_00565a10(timer)` is a **time-accumulator gate**, NOT a player counter: [00565a10]
```
if (g_world != 0) {
  acc = timer[0]; cap = *(double*)(g_world + 0xa088); // the world's "now" clock
  if (acc < 0) { FUN_005655a0(timer); acc = ticks*timer[1]*DAT_00737090 + (cap - timer[1]); timer[0]=acc; }
  if (acc <= cap) {
    if (timer[2]==0) acc = cap;                            // first-fire snap
    timer[0] = acc + timer[1];                             // advance by the interval timer[1]
    return 1;                                              // FIRE
  }
}
return 0;
```
So `MaintenanceTick` fires whenever `acc <= now`, then advances `acc` by the per-call interval
`timer[1]`. After each fire, `CProdPlannerMgr::Tick` bumps the **player cursor**
`DAT_00c3c968 = (DAT_00c3c968 + 1) & 0x80000007` (00432720:78) — the `& 7` (with the high-bit /
two's-complement guard) means **one player advances per qualifying tick**, exactly the port's
`_cursor = (_cursor + 1) & (Players - 1)`. The interval `timer[1]` and the world-clock units are
rodata/world-state — numeric values UNRESOLVED.

The transport layer (B) is NOT amortized per-player: `FUN_005eed70` loops **all 8 players every
fire** (005eed70:47-125), gated only by the `DAT_007372a8` interval + adaptive throttle. The port
amortizes BOTH into one `& 7` cursor, which is faithful to layer A but coarser than layer B (a minor
PORT GAP — transport will be re-evaluated 1/8 as often as the original; flagged §7).

---

## 3. STEP-TYPE → EVENT MAP (the reconciliation, byte-exact)

### Path A — `FUN_0043a8a0(&buf, step)` (MaintenanceTick emit). Type = `step[0]` (`*param_1`).
| `step[0]` | emitted `set_event` string | extra fields | cite |
|---|---|---|---|
| 1 | `evt_change_muetze -text1 %s` (hat swap) | step[0xb]+0xc | 0043a8a0:44 |
| 2 | `evt_autoprod_walk -pos1 {%f %f %f}` | step[4],[5],[6] floats | 0043a8a0:38 |
| 3 | `evt_autoprod_pickup` / `evt_autoprod_pickup_store -subject1 -text1` | flag obj+0x3d0 & 0x4000000 | 0043a8a0:49 |
| 5 | `evt_autoprod_transferprod -subject1 -subject2 -text1` | | 0043a8a0:62 |
| 6 | `evt_autoprod_pack -subject1 -text1` | | 0043a8a0:106 |
| 7 | `evt_autoprod_unpack -subject1 -text1 -pos1 {%f %f %f} -num1 %d` | step[4..6],step[10] | 0043a8a0:113 |
| 8 | `evt_autoprod_workat -text1 Bp%s -subject1 %s` (RESEARCH/invent) | also sets obj+0x3e0+0x1c=1 | 0043a8a0:88 |
| 9 | `evt_autoprod_workat -text1 %s -subject1 %s` (PRODUCE) | also sets obj+0x3e0+0x1c=2 | 0043a8a0:76 |
| 10 | `evt_autoprod_dig -pos1 {%f %f %f}` | step[4],[5],[6] floats | 0043a8a0:100 |
| 0xc(12) | `evt_autoprod_harvest -subject1 -text1` | | 0043a8a0:70 |

### Path B — `FUN_005f3a50(job, gnome)` (transport emit). Type = `job[+8]` (`*(param_1+8)`).
| `job+8` | emitted `set_event` string | obj+0x3e0+0x1c | cite |
|---|---|---|---|
| 0 | `evt_autoprod_pack -subject1 %s` | 4 | 005f3a50:61 |
| 1 | `evt_autoprod_unpack -subject1 -pos1 {%f %f %f} -num1 %d` | 6 | 005f3a50:75 |
| 2 | `evt_autoprod_harvest -text1 %s` | — | 005f3a50:91 |
| 3 | `evt_autoprod_dig -pos1 {%f %f %f}` | — | 005f3a50:95 |
| 4 | `evt_autoprod_workat -text1 Bp%s -subject1 %s` (RESEARCH) | 1 | 005f3a50:100 |
| 5 | `evt_autoprod_workat -text1 %s -subject1 %s` (PRODUCE) | 2 | 005f3a50:110 |
| 6 | `evt_autoprod_bringprod -text1 {%s} -subject1 %s` | 3 | 005f3a50:120 |
| 7 | `evt_autoprod_buildup -subject1 %s` | 5 | 005f3a50:130 |
| 8 | `evt_autoprod_mine -text1 %s` | — | 005f3a50:145 |

**Reconciliation**: the old doc said path A uses `dig=10/harvest=12`, path B uses `dig=3/harvest=2`.
Both are now byte-confirmed above. Both produce the identical `evt_autoprod_*` names, so a single C#
`JobKind` enum keyed to the canonical names is correct. Note `evt_autoprod_bringprod` exists ONLY in
path B (type 6) — the transport layer is the source of haul, never path A. Note the native
`obj+0x3e0+0x1c` state code (1=research,2=produce,3=bring,4=pack,5=buildup,6=unpack) is the engine's
internal work-state selector; in the port this role is filled by the Tcl `current_worktask` set in
each handler — no port field is needed.

---

## 4. THE NEAREST-GNOME MATCH (FUN_004319b0 + FUN_004317a0) — corrects the port

`FUN_00436d40` (DIG allocator, the clearest example) does, per dig job (after a bubble-sort of the job
index array by the float priority at job+0xc, 00436d40:62-115):
```
radius² = job.need[+0xc] * job.need[+0xc] * DAT_00737680 + DAT_00737578; [00436d40:141]
if (radius² >= DAT_00736f78) radius² = DAT_00737700; [00436d40:142] // clamp
ok = FUN_004319b0(job.cellSet, planner.owner[+4], job.x[+0xc], job.y[+0x10], job.z[+0x14], radius²); [00436d40:161]
if (ok) → reserve the cell + queue a delayed dig confirm (FUN_00437370 / FUN_0043de60) [00436d40:248-276]
else → fall back to a per-candidate scan (FUN_0043aef0 / FUN_00432690 / FUN_0043ad40) [00436d40:160-245]
```
`FUN_004319b0(cellSet, owner, x, y, z, radius²)`: [004319b0]
- For each candidate cell in the set: `bestGnomeCell = FUN_004317a0(cell, owner, ownerMask, x, y, z, &cost)`
  (004319b0:63). **If `cost > radius²` → reject (set -1)** (004319b0:64). Then set the cell's reserve
  bits: `cellByte & 7 == 1` (a free floor-cell) → `cellByte |= 2` (claim) and store the gnome id at
  `cell+6` (004319b0:42-45,:78-81). This is the anti-collision reservation.

`FUN_004317a0(cell, owner, ownerMask, x, y, z, &cost)` — the per-gnome cost scorer: [004317a0]
- Scans all live floor cells (`cell[+4]==1`, 004317a0:46) whose owner-reachability bit
  `(cellFlags & (1<<ownerMask))` is set (004317a0:55), that are unreserved (`cell+2 == -1`,
  004317a0:62), holding a valid worker obj of `owner` (004317a0:71-74), with class-id gate
  `FUN_005cb3a0(class) > 0x8c` and flag checks.
- **cost = `dx² + (dy·DAT_007374cc)² + (dz·DAT_00737058)²`** where dx=gnome.x−x, dy=gnome.y−y,
  dz=gnome.z−z (004317a0:75-78). Returns the cell index of the minimum-cost gnome.

In the PROVEN world frame (native east→+X, depth-into-screen→Z dig axis ×0.5, climb axis→Y): the
match is a **3-D weighted squared distance**, with the dig/depth axis down-weighted (the prompt's
"distance metrics weight this axis x0.5" — `DAT_00737058` is that ½ weight). **The port's `Dist2`
(`dx*dx + dy*dy`, horizontal only) is WRONG on two counts: it omits the Z term and both weights.**
(PORT GAP, §7.)

---

## 5. get_walkresult — THE DISPATCHER↔MOVEMENT CONTRACT

`get_walkresult` = `TCL:CTclObjMethods::GetWalkResult` (FUN_0068fb80). Body: `Tcl_NewIntObj(
*(int*)(obj + 0x248))` — returns the gnome's cached pathfind-result enum at **obj+0x248**
(0068fb80:58). CONFIRMED.

Writers of obj+0x248 (the move state machine, FUN_004075b0 / FUN_00407430 / FUN_00417de0):
| value | meaning | cite |
|---|---|---|
| **2** | walking / not-finished (set on walk start) | 00407430:64, 00417de0:65 |
| **3** | no-passage / path failed | 004075b0:51 |
| **4** | arrived (the success completion code, `uVar2` from the action result obj+8) | 004075b0:109 |
| **5** | secondary fail | 004075b0:216 |

The Tcl work loop gates on `4 != [get_walkresult this]` (z_dignwalk.tcl:916, baby.tcl:199,
actors.tcl:880) and `== 2` (still-walking, hamster.tcl:183, z_events.tcl:1631). So the contract is:
**the move executor MUST set obj+0x248 = 2 while walking, 4 on arrival, 3 on no-path** — and the work
chain only advances when it reads 4.

PORT (CORRECT, doc was wrong here): `WalkResultAction` (Assets/.../Tcl/Engine/EntityActions.cs:73)
sets `_ent.WalkResult = 2` on start, `= onFinish` (4 arrived / 3 no-passage) on completion; the walk
chain factory wraps every walk in it (DigglesTclEngine.cs:2818/2834/2867). `get_walkresult`
(DigglesTclEngine.cs:789) returns `e.WalkResult`. The headless degrade returns 4 (instant arrival,
DigglesTclEngine.cs:2820). This matches the native value set {2,3,4} exactly.

`planned_pathlength` / `get_walklength` (the doc's "planned_pathlength" in dispatch decisions): the
native dispatch decision does NOT call a Tcl `planned_pathlength` — the path *length* used for
job-matching is the C++-internal `cost` from `FUN_004317a0` (§4), never surfaced to Tcl. The Tcl
side only reads the *result* (`get_walkresult`), not the length. (So there is no Tcl
`planned_pathlength` contract to port; the matching length lives entirely in the C# dispatcher.)

---

## 6. Tcl HANDLER CONTRACT (what each emitted event does)

All handlers live in `original/data/Scripts/classes/zwerg/z_events.tcl`. Each clears the tasklist,
`stop_prod`, `notify_autoevent`, sets `current_plan="work"` + `current_worktask` + work vars, then
triggers a state. The **trigger target differs** (corrects the old doc):

| event | sets | trigger | cite |
|---|---|---|---|
| `evt_autoprod_dig` | `current_worktask=dig`, `current_workplace=dig`, `current_digpos=$pos`, mütze=stone | **work_auto** | z_events.tcl:1514,:1537 |
| `evt_autoprod_workat` | `current_worktask=work`, `current_workplace=$subj1`, `current_workclass=$text1`; `Bp`-prefix ⇒ "erfinden" (research) | **work_auto** | z_events.tcl:1127,:1167 |
| `evt_autoprod_harvest` | non-Pilz ⇒ redirect to `evt_autoprod_mine_new_proc`; else `current_worktask=harvest`, `harvest $objref` | **work_dispatch** | z_events.tcl:1405,:1439 |
| `evt_autoprod_mine_new` | `current_worktask=mine`, `tasklist_add "mine {$obj} 3"` | **work_dispatch** | z_events.tcl:1446,:1472 |
| `evt_autoprod_walk` | `current_worktask=walk`, `tasklist_add "walk_pos {$pos}"` | **work_dispatch** | z_events.tcl:1480,:1506 |
| `evt_autoprod_bringprod` | `current_worktask=bringprod`, transport-to-prodlist | (see handler) | z_events.tcl:1334 |
| `evt_autoprod_pickup/_store/pack/buildup/unpack/transferprod` | per-type | (see handlers) | z_events.tcl:1175-1404 |

NAME MISMATCH (flagged): native path-B type 8 emits `evt_autoprod_mine` (005f3a50:145), but the
harvest handler redirects to `evt_autoprod_mine_new_proc` (z_events.tcl:1414). There IS a
`evt_autoprod_mine_new_proc` (z_events.tcl:1446) and the redirect path is what runs in practice;
whether a bare `evt_autoprod_mine_proc` also exists is UNRESOLVED (the port should route mine via the
harvest non-Pilz redirect, which is what the Tcl actually does).

These set Tcl globals that the worktime gate (`get_remaining_sparetime`) and the work-state machine
(`work_auto` → `work_dispatch` → `walk_pos_dig` / `harvest` tasks) consume. The walk task then ends
with a finishcode that `state_enable`s the gnome and writes `WalkResult`; the next state-think pops
the next task and gates on `get_walkresult == 4` before digging/harvesting (the §5 contract closes the
loop).

---

## 7. PORT GAP ANALYSIS (`Assets/Diggles/Runtime/Engine/Sim/ProdDispatcher.cs`)

What the port already implements correctly:
- ✅ Two-layer collapse into one `Tick(dt)` with `_cursor &7` amortization (faithful to layer A; 00432720:78).
- ✅ `AssignCooldown = 2` (matches `gnome+1000 = 2`, 00434bc0:18 / 00433a70:278).
- ✅ `gnome_idle` → `OnGnomeIdle` with the **immediate one-shot re-task** when `WorkBusyCooldown <= 0`
  — faithful to `CProdPlanner::GnomeIdle` (00434a80:50 `if (gnome+1000 < 1) FUN_00434bc0`).
- ✅ Need-scaled radius shape `need*need*K + base` (matches 00436d40:141), and the dig/harvest/produce
  job-source split, demand-gated harvest (`HasActiveProduction`, faithful to FUN_005f0190 demand gate).
- ✅ `RaiseAutoprod` emits the canonical `evt_autoprod_*` via `set_event` in the gnome's object scope,
  queued for next-Step dispatch (faithful one-tick latency, SrvDoThink dispatch-then-handle order).
- ✅ Reserve via `PlaceLockStore` (anti-collision), `ReleaseGnome` on failure, worktime gate
  `IsWorkTime` (dayPhase = (GameTime/1800)%12 vs [WorkStart, +WorkLen)).
- ✅ `get_walkresult`/`WalkResult` value set {2,3,4} is CORRECT (doc was wrong; §5).
- ✅ Dispatcher actually wired: `DigglesGameSim.cs:158-159` calls `DecayCooldowns` + `Tick(dt)` at
  the END of Step (after state bodies), `DigglesTclContext.cs:149` holds it.

Gaps / divergences from the native (in rough priority):
1. **MATCH METRIC (highest-value gap).** Port `Dist2 = dx²+dy²` (horizontal only). Native
   `FUN_004317a0` = `dx² + (dy·DAT_007374cc)² + (dz·DAT_00737058)²` — 3-D, weighted, with the
   dig/depth axis (Z) down-weighted ≈½. The port both **drops the Z term** and **uses no weights**.
   On flat ground this is harmless, but for digs at depth / climbs the wrong gnome will be chosen.
   Fix: weight Y and Z per the native (the exact weights are rodata, UNRESOLVED — start with
   wY=1, wZ=0.5 per the prompt's "x0.5" and tune).
2. **DIG EVENT PAYLOAD.** Port emits `evt_autoprod_dig -pos1 {tx ty}` (`job.Tx + " " + job.Ty`,
   ProdDispatcher.cs:290) — 2 integer tile coords. Native emits a **3-float world pos**
   `{%f %f %f}` (0043a8a0:101). `evt_autoprod_dig_proc` does `set current_digpos [event_get this -pos1]`
   and feeds it to the dig task; downstream `vector_fixdig`/dig logic expects a world position, not raw
   tile indices. Port `job.Pos` is `new Vector3(tx, ty, 0f)` (ProdDispatcher.cs:230) — tile-space, not
   world-space, and z=0. Fix: build the dig job Pos from the real dig-edge WORLD coords (the same
   `get_digedge`/`FindNearestDiggableEdge` already used elsewhere) and emit `-pos1 {x y z}`.
3. **RADIUS NOT ENFORCED IN MATCH.** Native rejects a gnome when `cost > radius²` (004319b0:64). The
   port computes no radius and never rejects on distance — it just takes the nearest pooled gnome
   regardless of how far. Add the `need²·K + base` radius gate to the match loop (`Sweep`/`OnGnomeIdle`).
4. **TRANSPORT LAYER AMORTIZATION.** Native runs the transport tick (layer B) for **all 8 players
   every fire** (005eed70:47), not 1/8. The port folds everything into the `&7` cursor, so haul/produce
   re-evaluation for non-cursor players is starved. Minor for single-player; flag for AI economies.
5. **HAUL (`bringprod`) NOT COLLECTED.** `BuildJobs` calls dig/harvest/production only
   (ProdDispatcher.cs:174-177); `CollectHaulJobs` is a TODO. Native emits `bringprod` from path B
   type 6 (005f3a50:120). No recipe-input model yet (the doc's known gap stands).
6. **CARRIED-CARGO DISCOUNT MISSING.** Native `FUN_005f2380` strongly prefers a gnome already
   carrying an acceptable item for a haul job; port `Match` returns `true` for all and applies no
   discount. Needed once haul lands.
7. **NEED PRIORITY IS COSMETIC.** Port hard-codes Need = mark-count (dig), 0.5 (harvest), count+2
   (produce). Native derives need from `demand − owner-stock` per material (FUN_00434e60 /
   FUN_005ef0b0) and bubble-sorts (00436d40:62). The relative ordering (produce > dig > harvest) is a
   reasonable approximation but not the native demand math.
8. **CELL RESERVE vs PLACELOCK.** Native reserves the *dig cell* via a bit in the cell array
   (004319b0:44 `cellByte |= 2` + gnome id at cell+6), independent of the `placelock` system the Tcl
   uses for walk spots. The port reuses `PlaceLockStore` for both, which conflates two reservation
   namespaces; acceptable but not byte-faithful (two gnomes digging adjacent cells could spuriously
   block via the 0.8 radius).
9. **MINE NAMING.** Port has no mine path; native mine arrives via the harvest non-Pilz redirect
   (§6). When ore is added to `CollectHarvestJobs`, the unchanged `evt_autoprod_harvest_proc` redirect
   handles it for free — but confirm the redirect target name.

---

## 8. CONSTANTS

| symbol | role | value | source |
|---|---|---|---|
| obj+0x248 | walkresult enum (2=walk,3=nopath,4=arrived,5=fail) | — | 0068fb80:58, 004075b0, 00407430 |
| obj+0x3e8 (=+1000 dec) | per-gnome busy/re-task cooldown | set to **2** on assign | 00434bc0:18, 00433a70:278 |
| obj+0x35e | "has a current job" flag | set 1 on assign | 00433a70:371, 0043a460 |
| obj+0xf8 | is-a-worker flag (idle-pool eligibility) | nonzero ⇒ poolable | 00433a70:90 |
| obj+0xf9 | gnome work-struct ptr | — | 00433a70:110 |
| obj+0xdb | owner id (planner.owner compare) | — | 00433a70:89 |
| obj+0x3e0+0x1c | engine work-state code (1 res,2 prod,3 bring,4 pack,5 buildup,6 unpack) | — | 0043a8a0:78, 005f3a50:62.. |
| DAT_00737680 | radius² need² coefficient (K_score2) | rodata float (UNRESOLVED) | 00436d40:141 |
| DAT_00737578 | radius² base term (K_base) | rodata float (UNRESOLVED) | 00436d40:141 |
| DAT_00736f78 | radius² clamp threshold | rodata float (UNRESOLVED; used 129×) | 00436d40:142 |
| DAT_00737700 | radius² clamp value | rodata float (UNRESOLVED) | 00436d40:143 |
| DAT_007374cc | match-cost Y-axis weight (climb axis) | rodata float (UNRESOLVED) | 004317a0:76 |
| DAT_00737058 | match-cost Z-axis weight (dig/depth axis ≈0.5) | rodata float (UNRESOLVED) | 004317a0:77 |
| DAT_007372a8 | transport-tick interval | rodata double (UNRESOLVED) | 005eed70:39 |
| DAT_00c3c968 | planner player cursor `& 0x80000007` (≈ &7) | runtime counter | 00432720:78 |
| DAT_00c3c964 | dig-mark grid GC cursor (0..0x1fff) | runtime counter | 00424680:59-71 |
| 0x32 (50) | CAiCentral per-player think stagger modulus | — | 00424680:30 |
| 0x96 (150) | dig-mark cells GC'd per CAiCentral tick | — | 00424680:57 |
| step stride 0x34 | job step-list record size (path A) | 52 bytes | 0043a460:54 |
| job stride 0x18 | planner job-record size (TTL list +0x5c) | 24 bytes | 00433a70:147 |
| 0x2c | transport job record stride (path B) | 44 bytes | 005f3600:59 |
| hour-unit | worktime day = 12 units; shifts 0.0/6.0 ×6h | GameTime/1800 (port; native day-len UNRESOLVED) | z_methods.tcl:274, baby.tcl:422 |

---

## 9. TCL ↔ NATIVE CONTRACT (summary)

- **Native → Tcl (the only outbound channel):** the dispatcher raises exactly the `evt_autoprod_*`
  family by formatting a `set_event` string (FUN_0043a8a0 / FUN_005f3a50) and queuing it
  (FUN_0054ea20). It NEVER raises `evt_task_*` (those are user-click / std_ai combat orders;
  std_ai.tcl raises only `evt_task_walk`/`evt_task_attack`, never dig — confirmed ai/std_ai.tcl:488,
  490, 945). For the human player there is NO Tcl that raises `evt_autoprod_*`, so the native
  dispatcher is the SOLE source of autonomous human work (the port's `ProdDispatcher` is exactly that).
- **Tcl → native (what the dispatcher reads back):** `gnome_idle this 1|0` (pool membership;
  CProdPlanner::GnomeIdle), `get_walkresult this` (movement result, the loop gate), `get_remaining_
  sparetime` (worktime gate), `placelock_*` (walk-spot reservations), `dig_mark`/`gnome_announce_dig`
  (the dig-mark grid the dig allocator consumes), `gnome_failed_work`/`gnome_stopped_work` (release).
- **The loop:** raise `evt_autoprod_dig` → `evt_autoprod_dig_proc` sets `current_digpos` + triggers
  `work_auto` → `work_dispatch` walks to the dig edge (writing `WalkResult`) → on `get_walkresult==4`
  the dig task carves → `gnome_idle this 1` on completion re-enters the pool → next sweep re-tasks.

---

## 10. UNRESOLVED

1. **rodata constant VALUES** (`DAT_00737680/00737578/00736f78/00737700/007374cc/00737058/007372a8`):
   referenced but never assigned in the decompile text (they live in `.rdata`). Cannot read the float
   literals from the C; need a data-section dump or a live read. Port K-constants are therefore
   guesses; tune in Play.
2. **Match-cost axis weights**: the *fact* of Y/Z weighting is proven (004317a0:76-77) but the numeric
   weights (DAT_007374cc, DAT_00737058) are unresolved (rodata). Prompt says dig axis ×0.5.
3. **Worktime day length in native seconds**: Tcl uses 12 hour-units, shifts 0.0/6.0×6h
   (z_methods.tcl:274). The native→seconds mapping (Sparetime.cpp `FUN_005f4a70` not yet read) is
   unconfirmed; port assumes hour-unit = 1800s.
4. **`evt_autoprod_mine` vs `evt_autoprod_mine_new`**: path-B type 8 emits `evt_autoprod_mine`
   (005f3a50:145) but the harvest handler redirects to `evt_autoprod_mine_new_proc`
   (z_events.tcl:1414). Whether a bare `evt_autoprod_mine_proc` exists / how the engine routes
   `evt_autoprod_mine` is unconfirmed.
5. **`std_ai` dig-marking for AI players**: the old doc claims AI paints dig-marks via std_ai
   `dig_mark`. grep of `ai/std_ai.tcl` shows NO `dig_mark` call (only `set_event evt_task_*`). Where
   AI dig-marks originate (the lg_*/StoryMgr growth engine? a different AI script?) is UNRESOLVED —
   INFERENCE that AI dig comes from elsewhere, not std_ai.
6. **FUN_00434e60 (ProdAllocator pass-1) demand math**: 4237-byte function not fully read; the exact
   `demand − stock` need formula and the top-2 wanted-slot selection are summarized from structure,
   not line-by-line. The port's Need heuristic is an approximation.
7. **FUN_005f2380 (transport scorer, 4735 bytes)**: read only at the call-site level; the carried-cargo
   discount factor and acceptable-input-class computation (FUN_005f1cc0) are not byte-verified.
