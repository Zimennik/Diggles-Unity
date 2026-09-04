> **RE working note — port status is consolidated & kept live in `Docs/GNOME_BEHAVIOR_COMPLETE_RE.md`.** The decompile addresses/offsets/constants below are ground-truth LAW; the *port-gap / "our state" snapshots are the PRE-FIX analysis* — all such gaps were addressed in Phases 0-7 (DONE, see the parent doc §11). Note: `WalkClimbAction.cs` and `DigglesInputController.cs`, cited in older gap notes, were DELETED in Phase 7.

> **Correction** references below to a missing eight-layer roofed-tunnel voxel
> model are invalid. `tile+0x08` contains eight owner-order nibbles, and the native terrain
> also stores one BDB `block_idx` per tile. The target-11 tunnel carve is implemented.

# Port Inventory — movement / dig / work code (gap-analysis base layer)

**AREA_KEY:** `port-inventory`
**Scope:** READ-ONLY map of OUR current Unity port of the original Diggles movement / dig / work
systems. For every file: a `file:line` behavior map, which ORIGINAL native function it CLAIMS to
mirror (per the in-code comments), and known divergences. This is the substrate the other
`re_parts/*` agents diff their ground-truth RE against.

> All native-function attributions below are **as the port's own comments claim** — this doc does NOT
> re-verify them against the decompile. Where the comment cites a `FUN_xxxxxxxx`, treat it as the
> port author's INTENT, to be checked by the corresponding RE agent. Where I flag a likely mismatch I
> mark it **[PORT-GAP]** / **[VERIFY]**.

---

## 0. System map (who calls whom)

```
ClickToMoveService (live, Tcl-driven) DigglesInputController (LEGACY — Destroyd at runtime)
   │ Eval("action this walk -target …") │ selected.SetAction(new WalkClimbAction(path…))
   ▼                                              ▼
DigglesTclEngine.CmdAction "walk" WalkClimbAction: IDigglesAction (orphan path)
   │ BuildMetaWalkFromOptions (ticked by DigglesObject.Update, NOT the sim)
   ▼
NavWorldService.FindPath ── Calc1Pathfinder.FindPath ── NavWorld (sub-grid + FUN_005a6350 graph)
   │ returns List<NavWaypoint> (floor/wall tagged)
   ▼
segmentation loop → MetaWalkAction (floor runs) + ClimbAction (wall runs) [LIVE path]
   │ wrapped in WalkResultAction(…, 4) → SequenceAction
   ▼
DigglesTclEntity.ActiveAction ── ticked by DigglesGameSim.Step phase 2b ── finishcode {seq_idle}

Dig: ClickToMoveService(Shift) → set_event evt_task_dig → z_dignwalk.tcl loop →
       dig_apply/get_digedge/dig_next/get_hmap (DigglesTclEngine) → DiggableTerrain carve → re-mesh → Nav rebuild
Work: ProdDispatcher (native CProdPlanner port) raises evt_autoprod_* onto idle gnomes; ticked by sim 3b
```

**Two parallel movement stacks exist.** The LIVE one is the Tcl-action stack
(`MetaWalkAction`/`ClimbAction` ticked by the sim). The LEGACY one
(`DigglesInputController` → `WalkClimbAction` → `DigglesObject`) is added by the editor/world
builders but **explicitly `Destroy`d** by `DigglesWorldRuntime` at boot
(`DigglesWorldRuntime.cs:724-725`), so it never runs in the campaign. `WalkClimbAction` is the
PROVENANCE of `ClimbAction` (the comment in `ClimbAction.cs:11-12` says it was ported from it).

---

## 1. Navigation data layer

### 1.1 `Runtime/Navigation/NavWorld.cs` — the pathfinding world model
Claims to mirror: **FUN_005a6350** (post-load "floor-flag" derivation), **FUN_004093f0**/**FUN_0062faf0**
(`get_hmap`), surface classifier **FUN_00423730**. Doc: `Docs/ORIGINAL_PATHFINDING_RE.md §1`.

- `NavWorld.cs:29-32` — holds `_blockIdx` (SHARED w/ DiggableTerrain, mutated on dig), `_sub`
  (per-tile tile-major 8×8 sub-cell passability `[tile*64 + (subY&7)*8 + (subX&7)]`), `_bdb`.
- `NavWorld.cs:35-42` — derived per-tile graph + sub-cell bit constants:
  `SubWalk=0x01, SubFloor=0x02, SubWater=0x04, SubClimb=0x08`.
- `NavWorld.cs:54-59` `SubAt` — sub-cell lookup; OOB ⇒ 0 (blocked).
- `NavWorld.cs:68-74` `GetHmapSub` — per-sub-cell depth nibble 0..15 via `_bdb.AuxHeightAt`; OOB or
  `blk>=nBlocks` ⇒ **15** (claims faithful to FUN_0062faf0 OOB return). Comment now correctly labels
  this the **slab-depth-into-screen** axis (`NavWorld.cs:64-67`).
- `NavWorld.cs:90-94` `SubIsClimbWall` — `walk && !floor`.
- `NavWorld.cs:97-135` `DeriveTile` (FUN_005a6350 port): scans all 64 sub-cells, best-walkable score
  `((floor?0x100:0) - |4-row| - |4-col| + 8)`, keeps FIRST max (`bestSub` init `0x24`, `bestScore` init `0`).
  Writes `BaseCost[t]` (`-1` solid / `20` floor-or-climb / `50` plain walkable), `IsFloorTile[t]`
  (`(u&0x100)!=0`), `ExitSub[t]` (best sub idx). `u = hasClimb ? best|0x100: best`.
  - **[PORT-GAP, self-flagged `NavWorld.cs:131-134`]:** the original ALSO (a) forces `-1` when a tile is
    fully enclosed (4-neighbour AND of `+0x18 bit 0x80`) at the map edge with no climb cell, and
    (b) walks the exit sub-cell UP a climb band when `has_climb`. Neither is ported (P0 simplification).
- `NavWorld.cs:138-149` tile queries `TileValid/TileFloor/TileBaseCost`, and `TileDigByte` = BDB
  `aux[0x24]` centre byte (the Calc1 edge-cost diggability) via `_bdb.AuxCenter`.

### 1.2 `Runtime/Navigation/Calc1Pathfinder.cs` — tile-level search
Claims to mirror: **CMapPathfinder::Calc1 (FUN_0059d640)**, float wrapper **FUN_0059d440**.
Doc: `Docs/ORIGINAL_PATHFINDING_RE.md §2`.

- `Calc1Pathfinder.cs:28-30` constants: `Cliff=0x114(276)`, `StepCost=0x10(16)`, `DefaultBudget=200000`.
- `Calc1Pathfinder.cs:50-106` `FindPath(startX,startY,goalX,goalY,canClimb)` — endpoints in SUB-CELL
  coords. Relax start (ring ≤32) + goal (ring ≤12); FIFO Bellman-Ford over tiles (re-enqueue on
  improve, NOT A*); after goal first reached, budget clamps to `min(budget, pops*1.5 + 8)`
  (DAT_007371c0=1.5). Output = sub-cell polyline (relaxed start, interior tiles' `ExitSub`, relaxed goal).
- `Calc1Pathfinder.cs:17` `Step` — gate 1 validity (`BaseCost!=-1`), gate 3 floor for `!canClimb`,
  edge cost variant **v1 (iOwner≤7)**:
  - `curFloor != tFloor`: `base + 276 − 16·min(digCur,digT)`
  - `!curFloor` (both wall): `base + 1 + 16·|digCur−digT|`
  - both floor: `base + 1`; clamp `cost≥1`.
- `Calc1Pathfinder.cs:141-165` `RelaxEndpoint` — square-ring snap onto `SubWalk` bit, **+y (down) first**
  per ring. **[VERIFY]** comment admits "the original's exact probe order is approximated"; the post-snap
  "+y while floor overhang nudge" is TODO (`Calc1Pathfinder.cs:22`).

### 1.3 `Runtime/Navigation/NavWorldService.cs` — scene host + world↔native↔sub plumbing
- `NavWorldService.cs:51-152` `FindPath(worldStart,worldTarget,canClimb)` — world →
  `InverseTransformPoint` → `LocalToNative` → ×8 sub-cells, runs Calc1, then a **lot of post-processing**:
  - `NavWorldService.cs:75-86` goal-settle: if goal landed on a bare-walkable (non-floor) cell, step
    +y up to 16 to find a FLOOR line (claims FUN_0041d8a0:1012-1022). **[SYNTHESIS]**
  - `NavWorldService.cs:88-94, 170-229` `RepathWallRuns` + `BfsWalkable`: replaces each WALL run with a
    4-neighbour plain BFS over walkable sub-cells, thinned to every 2nd cell. Claims to stand in for
    CAct_Climb's FloorPathMap re-path (FUN_0041d8a0). **[SYNTHESIS — costs/5th-pass omitted, self-flagged
    `NavWorldService.cs:199-200`]**
  - `NavWorldService.cs:15` depth (native-z) assignment: WALL cells get `GetHmapSub` (wall-face
    depth); FLOOR runs get piecewise-LERP between depth anchors along (x,y) arc length. Comment admits
    the faithful per-node slope-follow (FUN_00424470) is the **P2 refinement** (`NavWorldService.cs:104-106`).
  - `NavWorldService.cs:157-162` `Tag` (FUN_00423730 classifier): floor bit ⇒ FLOOR, walk-no-floor ⇒
    WALL; wall normal hardcoded **world −Z** `(0,0,-1)` — "refined when the faithful FloorPathMap climb
    lands (P4)". **[PORT-GAP — normal is a placeholder, not derived from geometry.]**

### 1.4 `Runtime/Navigation/NavWorldGizmo.cs` — debug visualiser (not gameplay)
- `NavWorldGizmo.cs:41-92` `Rebuild` — draws CYAN floor strips (value-3 ground lines, depth-spanned) +
  MAGENTA climb cubes (value-1). Reads `GetHmapSub` of own/above/below to choose face row. No effect on
  movement; only diagnostic. Matches the frame-corrected model in `NavWorld.cs:76-94`.

---

## 2. Tcl-action movement executors

### 2.1 `Runtime/Tcl/Engine/EntityActions.cs`
Claims to mirror: **CAct_MetaWalk / CAct_Climb / CAction** (FUN_004163a0, FUN_00416540, FUN_0041a910,
FUN_00420840, FUN_00420520). All implement `IEntityAction.Tick(dt)→bool` (running). Ticked by the sim
(DigglesGameSim 2b), **not** `DigglesObject`.

- `EntityActions.cs:27-64` `AnimAction` — `action <obj> anim <clip>`; plays once (or LOOP if name
  contains "loop"); finishes after `frameCount/10`s.
- `EntityActions.cs:73-90` `WalkResultAction` — wraps any inner action, writes `ent.WalkResult` = 2
  (WS_NOTFINISHED) on first tick, then `onFinish` (4=arrived / 3=no-passage) when inner completes.
  Claims to mirror CAct_MetaWalk stamping engine `obj+0x248`. **THE unblock** for the Tcl `walkfail`
  gate `4 != [get_walkresult this]`.
- `EntityActions.cs:94-108` `SequenceAction` — runs children strictly in order (the floor→climb→floor
  chain). Claims FUN_00416540.
- `EntityActions.cs:111-120` `WaitAction`.
- `EntityActions.cs:129-235` `WalkAction` — DISTANCE form (legacy/demo `action this walk <dist>`):
  walk_start (accel ramp) → walk_loop (cruise) → walk_stop (decel) → standstill, moving FORWARD along
  facing. **Speed is NOT from root motion** — `BodySpeedFactor=0.5` × model height × gait ratio
  (`WalkAction.cs:137-138`, comment: the.3db MotionOffset was too small, abandoned). **[PORT-GAP vs
  the "movement = root motion" memory: speed is eyeballed, not frame-coupled root motion.]**
- `EntityActions.cs:250-443` `MetaWalkAction` — the LIVE floor mover. Walks a `List<Vector3>` world
  waypoints; turns to face each; progressive walk→run promotion by TRAVELLED distance
  (`RunDistance=3`, claims FUN_0041a910:69-82 / FUN_00420840). `speedtype 1`=never run, `2`=run now.
  - `MetaWalkAction.cs:372-394` `Advance` — moves in WORLD horizontal plane, eases height toward
    waypoint Y (`delta.y = to.y * step/dH`). `Arrive=0.2`, `TurnRate=540°/s`.
  - **[PORT-GAP]:** cruise speed again = `ModelHeight*BodySpeedFactor*gait` (`Cruise`:421-429), NOT
    root motion. Climb promotion mid-segment but no actual climbing here (v1 floor only).

### 2.2 `Runtime/Tcl/Engine/ClimbAction.cs` — LIVE climb executor
Claims to mirror: CAct_Climb choreography (ported from the orphan `WalkClimbAction`, `ClimbAction.cs:11-12`).
- `ClimbAction.cs:28` `Kind { Walk, Mount, CrawlLeft, CrawlRight, ClimbUp, ClimbDown, Dismount, Jump }`.
- `ClimbAction.cs:41-49` hardcoded clip-name fallback lists (mann/frau.3db names).
- `ClimbAction.cs:86-136` `Build` — expands tagged path into legs. **This is the SYNTHESIS, not RE:**
  floor→wall = mount+crawl+climbdown; wall→wall = split diagonal into vertical+lateral; wall→floor =
  align-height+crawl+dismount. Uses `Vector3.up`/`Vector3.right` and a `_climbStandoff=0.2` push off the
  wall. **[PORT-GAP — entire mount/crawl/dismount choreography is reconstructed by hand from
  "demonstrated pit routes", NOT from a decompiled function. The axis comment (`ClimbAction.cs:19`
  "Up=+Y, Right=+X, depth=+Z") CONTRADICTS the project's confirmed frame (native +Y = down). [VERIFY]]**
- `ClimbAction.cs:138-191` `Tick` — one-shot legs (mount/dismount) HOLD for clip length; others advance
  on positional arrival (`_arriveEps=0.12`).
- `ClimbAction.cs:264-274` `Calibrate` — speeds from `ModelHeight*BodySpeedFactor`, climb = `×0.47`
  (ClimbFactor). Again **NOT root motion** (comment explicitly rejects LocomotionSpeed/.3db MotionOffset).

### 2.3 `RotateAction` / `TurnToFaceAction` / `FlyAction` (`EntityActions.cs:448-584`)
- `RotateAction` (:448-493) — turn by N degrees, plays turn clip from active animset.
- `TurnToFaceAction` (:501-551) — turn to absolute target rotation; compares FACING dirs via
  `SpawnFaceWorld` (not transform.forward, which is vertical for Y-down meshes). Used by cutscene
  `action <obj> rotate`.
- `FlyAction` (:560-584) — CAct_Fly butterfly glide; **[PORT-GAP self-flagged:558-559]** the original's
  16-try voxel validation is dropped (no flier voxel grid).

---

## 3. Tcl engine — walk + dig natives

### 3.1 `Runtime/Tcl/Engine/DigglesTclEngine.cs` — `CmdAction` dispatch (`:2537-2639`)
- `:2546-2557` `anim` / `wait`.
- `:2558-2583` `walk` — OPTION form (`-target … -animsets … -speedtype …`) → `BuildMetaWalkFromOptions`;
  DISTANCE form → `WalkAction`.
- `:2584-2601` `rotate` — absolute native yaw (radians); `TurnToFaceAction` for characters, reflect+wait
  for props. Claims do_action convention (front=0, left=π/2, back=π, right=3π/2).
- `:2602-2609` `fly`.
- `:2618-2637` break/finish chaining: replacing an in-flight action fires its breakcode in the entity's
  object scope; empty breakcode inherits `ctx.ChainBreak`.

### 3.2 `BuildMetaWalkFromOptions` (`:2765-2868`) — the parse + pathfind + segmentation
Claims: option defaults `canClimb=1` (FUN_00420520), segmentation FUN_004163a0.
- `:2772-2810` parses `-target`(native), `-animsets {walk run}`, `-speedtype`, `-canclimb`,
  `-object`(→ object position), `-dummy`(ignored, offset 0).
- `:2814-2820` no target/nav ⇒ degraded `WalkResultAction(WaitAction(0.05), 4)` (instant arrival — keeps
  headless work-loop LOGIC verifiable).
- `:2822-2827` native→world via `SpawnParent.TransformPoint(NativeToLocal(target))`.
- `:2828-2834` `nav.FindPath`; no path ⇒ `WalkResultAction(…, 3)` (WS_NOPASSAGE).
- `:2836-2867` **segmentation**: group consecutive same-surface waypoints; floor run → `MetaWalkAction`,
  wall run → `ClimbAction` (with bounding floor waypoints so mount/dismount synthesise); chain via
  `SequenceAction`; wrap in `WalkResultAction(…, 4)`.
  - **[PORT-GAP]:** climb runs include the bounding floor waypoints by re-adding `path[si-1]`/`path[sj]`
    (`:2853-2855`), then `ClimbAction.Build` re-synthesises transitions — double-handling vs the
    original's single CAct segment chain. **[VERIFY against FUN_004163a0.]**

### 3.3 Dig + terrain-query natives (`:734-902`)
All NATIVE coords {X=east, Y=depth, Z=height}; tile = (round X, round Y). Registered BEFORE the "0" stubs.
- `:745-749` `vector_unpack{x,y,z}` / `vector_pack` / `vector_packxyz`.
- `:751-763` `get_hmap` (FUN_004093f0) → `nav.GetHmapSub(round(x*8), round(y*8))` (per-sub-cell nibble;
  the old per-tile MAX-corner collapse is gone). Falls back to terrain `GetHmap` if no nav.
- `:764-768` `get_ftype` → terrain byte.
- `:769-783` `get_gnomeposition` (FUN_0068d8a0:84-98) → 0 on floor / 1 on wall; probes FLOOR bit at
  body sub-column rows subY+1, subY, subY−1.
- `:784-793` `get_walkresult` (engine obj+0x248) → `ent.WalkResult`.
- `:794-799` `get_material` → terrain-byte hardness proxy.
- `:800-815` `get_digedge` (FUN_005981d0) → nearest dig-marked solid tile, then snap to nearest FLOOR
  sub-cell (value-3) within 24 rings, Z=`GetHmapSub`. **[PORT-GAP self-flagged:802-804: original returns a
  proper floor-level standoff; ours approximates via tile-find + sub-snap.]**
- `:816-824` `dig_next` (FUN_00597b20) → tryN-th nearest dig-marked solid tile; Z=`clamp(rim+4,1,14)`,
  rim = max corner gate byte.
- `:825-838` `dig_apply` (FUN_006347a0 → FUN_00596b80) → owner-selected, rim-gated
  `terrain.CarveStep(tx,ty,owner)`. The owner comes from gnome+0x36c and selects one nibble in
  `tile+0x08`; it is not a spatial layer.
- `:839-857` `dig_mark` / `is_dig_marked`.
- `:857-879` `vector_fixdig` (FUN_00594e00) — step off the wall toward the dwarf then spiral-snap to a
  walkable sub-cell.
- `:887-902` `dig_anim` math (FUN_00646b10) + `check_ghost_coll` stub + `inc_playerdigmarkid`.

---

## 4. Terrain (dig medium)

### `Runtime/World/DiggableTerrain.cs`
Claims to mirror: the BLOCK-level carve **FUN_00596b80 / FUN_005968b0** via the BDB aux corner-transition
graph. Doc: `Docs/DIGGING_MECHANIC.md`.
- `:56-77` `Init` — shares `BlockIdx`, builds `_tileHeight` (= `AuxMaxCorner`), `_digMark`, `_dug`, and
  **builds `Nav = new NavWorld(...)`** from `r.Voxels` (the static PMP sub-grid) + bdb. Guarded for
  headless (bdb==null).
- `:80-110` coord/query helpers: `WorldToTile` (round X,Y), `HmapToZ`, `GetHmap`, `Solid`(≥14),
  `Diggable`(>0), `GetMaterial`, `RimHeight` (max of 4 corner GATE bytes, for dig_next Z).
- `:113-162` dig marks + `FindNearestDiggableEdge` / `PickDiggableCell` (square-radius scans, NOT ring).
- `:182-224` **the carve**: `StepCornerDown` (one tile's corner down one rim via `AuxCornerNeighbour`),
  `DigNode` (steps the shared corner of all up-to-4 tiles at a grid node), `CarveTileNow`/`ApplyDig`
  (= one DigNode + remesh request).
  - **[CORRECTED / CLOSED]:** one block per tile and shared-corner BDB transitions are the native
    carve model. `tile+0x08` carries eight owner targets. Mode 1 targets 11 and exposes the authored
    tunnel wall profile; there is no second roof surface to model.
- `:229-245` debounced remesh (`DebounceSec=0.4`) → `RemeshFromArrays` + `_navRebuild` (rebuilds Nav).
  - **[PORT-GAP]:** the dig mutates `BlockIdx` (so `GetHmapSub` heights track), but the **+0x3c
    passability sub-grid (`_sub`) is NOT re-carved** — `Init` notes "Dig-side sub-grid carving = P3"
    (`:71-73`). So a freshly dug shaft's WALKABILITY does not update until a full nav rebuild
    re-derives, and even then `_sub` is the static PMP load. **[VERIFY how dig should open walk cells.]**

---

## 5. Work dispatcher

### `Runtime/Engine/Sim/ProdDispatcher.cs`
Claims to mirror: native **CProdPlanner / ProdAllocator** (FUN_00434a80 GnomeIdle, FUN_00434bc0,
FUN_004319b0, FUN_0043a8a0 step→Tcl map, FUN_005f0190/FUN_005ef0b0/FUN_005f1500 demand gates).
Doc: `Docs/WORK_DISPATCHER_NATIVE.md`.
- `:14-43` `PlaceLockStore` — native PlaceFinder reservation list (`{pos,id,expiry}`, horizontal
  proximity). Tcl idiom `placelock_check/set`.
- `:66-68` constants: `Players=8`, `DispatchInterval=0.5` **[assumption — native DAT unknown]**,
  `AssignCooldown=2` ticks.
- `:89-105` `OnGnomeIdle` (gnome_idle hook) — register idle + one-shot nearest-job re-task.
- `:129-168` `Tick`/`Sweep` — amortized one-player-per-tick sweep, jobs sorted highest-need first,
  nearest free gnome per job, reserve+assign.
- `:171-253` job sources: `CollectDigJobs` (single-player dig grid only, `:224`),
  `CollectHarvestJobs` (DEMAND-GATED — only if `HasActiveProduction`, **[PORT-GAP fixed: was
  free-gather]**), `CollectProductionJobs` (invention gate `Bp<Class>`).
- `:280-305` `RaiseAutoprod` — emits `evt_autoprod_dig/harvest/workat/bringprod` (NOT `evt_task_*`).
- `:34` `IsWorkTime` — 12-hour clock phase in `[WorkStart, WorkStart+WorkLen)`.
- **[PORT-GAP]:** Haul collector unimplemented (`:177`); `Match` always true (no tool/skill gating, `:259`);
  dig jobs only for local player (`:224`).

### `Runtime/Engine/Sim/DigglesGameSim.cs` — fixed-step simulator
Claims to mirror: **CGameThread::SrvDoThink (FUN_006b4a30)**, state dispatch **CObj::HandleStates
(FUN_005cac90:27)**.
- `:40-93` `Step(dt)`: (1) advance GameTime; (2) mature timers; (2) dispatch events; **(2b) tick
  ActiveAction**, fire finishcode/breakcode in object scope on completion.
- `:95-113` **(2c) sync `ent.Position` back from the GameObject transform** — only for entities directly
  under the world root (the move executors drive the transform; without this `get_pos` is stale).
- `:117-152` (3) state-think ticks. **[PORT-GAP self-flagged:138-150]:** the engine does NOT gate
  state-think on the active action; ours skips state-think while an action runs ONLY when no tasklist
  work is queued — "a band-aid for commanded walks that don't adopt the state_disable discipline."
- `:154-159` (3b) `ProdDispatcher.DecayCooldowns` + `Tick(dt)`.
- `:163-168` (4) camera/dialogue/audio/particles/screenfx/elf ticks.

---

## 6. Input / click-to-move

### `Runtime/Engine/ClickToMoveService.cs` (LIVE, Tcl-driven, VContainer ITickable)
- `:34-59` LEFT-click only; gated during cutscenes (`SequenceActive`); raycast; dwarf/Frau/Baby ⇒ SELECT.
- `:61-73` **Ctrl+click ⇒ TEST carve** `terrain.CarveTileNow(cx,cy)` (debug, no dwarf).
- `:80-95` Pilz ⇒ `evt_task_harvest`; Feuerstelle ⇒ `prod_cook … Grillpilz`.
- `:101-123` **Shift+terrain ⇒ DIG order** — `SetDigMark` + `set_event this evt_task_dig -pos1 {tx ty}`.
- `:125-137` plain terrain ⇒ WALK — builds `action this walk "-target {x y z} -animsets {0 1}
  -speedtype N" {seq_idle}`; double-click (0.35s/5u) ⇒ speedtype 2 (run).

### `Runtime/Engine/DigglesObject.cs` (CObj mirror)
- `:40-95` `Awake` — captures `SpawnRotation` + `SpawnFaceWorld` (XZ-projected from first non-zero
  MotionOffset clip, else mesh +Z). Used by facing math everywhere.
- `:97-111` `Update` — ticks `CurrentAction` (the LEGACY `IDigglesAction` path) + applies `Velocity`.
  **The live Tcl actions do NOT use this** (they're ticked by the sim and write the transform directly);
  only `WalkClimbAction` (legacy) uses `Velocity`/`SetAction`.
- `:126-156` `FaceNativeAngle` / `FaceRotationForNativeAngle` — native yaw → world heading
  (`wx=-sin, wz=-0.5·cos`, verified ANGERR=0). Used by cutscene rotate.

### `Runtime/Engine/DigglesInputController.cs` + `Runtime/Engine/Actions/WalkClimbAction.cs` — **LEGACY/DEAD**
- `DigglesInputController.cs:46-98` — physics-raycast click→select / `selected.SetAction(new
  WalkClimbAction(...))`. Added by `RuntimeMapBuilder.cs:126-127` and `CampaignBrowserBuilder.cs:132-133`
  but **`Destroy`d at runtime** (`DigglesWorldRuntime.cs:724-725`). Not used in live campaign.
- `WalkClimbAction.cs` — the original synth choreography; speed CALIBRATED from clip root motion
  (`CalibrateSpeed`:180-196) — the ONE place that still reads `.3db MotionOffset`. `ClimbAction`
  (live) was forked from this but switched to `ModelHeight*BodySpeedFactor` instead.

---

## 7. Port-wide divergence summary (the gap base layer)

| # | Divergence | Where | Severity |
|---|---|---|---|
| 1 | Climb mount/crawl/dismount choreography is **hand-synthesised**, not from a decompiled function | `ClimbAction.cs:86-136`, `WalkClimbAction.cs:86-141` | HIGH |
| 2 | `ClimbAction` axis comment says native **+Y = up**; project ground-truth is **+Y = down** | `ClimbAction.cs:19` | HIGH (likely wrong axes) |
| 3 | Movement speed = `ModelHeight*BodySpeedFactor`, **NOT** per-clip root motion (frame-coupled) | `WalkAction.cs:137`, `MetaWalkAction.Cruise`, `ClimbAction.Calibrate` | MED |
| 4 | Wall normal hardcoded world **−Z**, not derived | `NavWorldService.cs:161` | MED |
| 5 | ~~Missing eight-layer roofed-tunnel voxel model~~ — false premise; both use one block per tile and owner-target nibbles | `DiggableTerrain.CarveStep`; `Docs/DIGGING_MECHANIC.md` §7 | CLOSED |
| 6 | Dig does **not re-carve the +0x3c passability sub-grid** (`_sub` is static PMP) | `DiggableTerrain.cs:71-73` | HIGH |
| 7 | `NavWorldService.FindPath` does heavy **synthesised** post-processing (goal-settle, RepathWallRuns BFS, depth LERP) standing in for CAct_Climb's FloorPathMap | `NavWorldService.cs:75-135,170-229` | MED |
| 8 | FUN_005a6350 derivation omits enclosed-tile `-1` forcing + climb-band exit walk | `NavWorld.cs:131-134` | LOW/MED |
| 9 | Calc1 endpoint-relax ring order **approximated**; overhang nudge TODO | `Calc1Pathfinder.cs:22,141-165` | LOW |
| 10 | State-think gate (skip while action runs unless tasklist queued) is a **band-aid**, not faithful | `DigglesGameSim.cs:138-150` | MED |
| 11 | ProdDispatcher `DispatchInterval=0.5` is an **assumption** (native DAT unknown); haul + skill gating unimplemented | `ProdDispatcher.cs:67,177,259` | LOW/MED |
| 12 | Two movement stacks; legacy `WalkClimbAction`/`DigglesInputController` is dead-but-present | `DigglesWorldRuntime.cs:724-725` | LOW (cleanup) |

---

## 8. Unresolved (need the partner RE agents)
- Whether the `ClimbAction`/`NavWorldService` synthesis can be replaced by a faithful CAct_Climb /
  FloorPathMap (FUN_0041d8a0) port — needs that function fully reversed.
- The true native walk/run/climb SPEED source (root motion vs a fixed rate) — needs CAct_MetaWalk's
  per-frame advance (FUN_0041a910 / FUN_00420840) reversed against the `.3db` MotionOffset units.
- How a dig should update +0x3c walkability (open a shaft's walk/floor cells) — needs the carve's
  sub-grid write path (around FUN_00596b80) reversed.
- The native `DispatchInterval` / ProdPlanner cadence DAT.
- Whether segmentation in `BuildMetaWalkFromOptions`
  matches FUN_004163a0's segment boundaries.
