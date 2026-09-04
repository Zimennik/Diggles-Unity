# Navigation & Movement — the shipped architecture

> This is the *as-built* doc for the live pathfinding + movement action layer. The RE
> **laws** live in `ORIGINAL_PATHFINDING_RE.md` (Calc1/MetaWalk/WalkFloor/Climb disasm) and
> `GNOME_BEHAVIOR_COMPLETE_RE.md`; this file maps them to code. An earlier mesh nav-graph
> approach was deleted in favor of the faithful tile-based planner described below.

## Pipeline at a glance

```
Tcl `action <h> walk -target {x y z} …` (or C# callers: ClickToMove, dispatcher)
  └─ DigglesTclEngine.CmdAction (walk verb, ~:5700)
       ├─ target: native coords → world via SpawnParent.TransformPoint(NativeToLocal(t))
       ├─ NavWorldService.FindPath(worldStart, worldTarget, canClimb)   ── Runtime/Navigation/
       │    ├─ world → local → native (LocalToNative) → sub-cells (×8, Z dropped)
       │    ├─ Calc1Pathfinder.FindPath — faithful tile-level Bellman-Ford over the
       │    │    FUN_005a6350-derived graph (BaseCost/IsFloorTile/ExitSub) on NavWorld
       │    ├─ goal settle: relaxed bare-wall goals drop onto the floor band BELOW —
       │    │    but ONLY when the raw goal was itself unwalkable (deliberate wall
       │    │    goals, e.g. the wall-dig standoff, stay on the wall → the dwarf climbs + hangs)
       │    └─ polyline assembly per surface run:
       │         FLOOR runs → FloorPathMap (FUN_0041aac0/0041d8a0) — resolves WHERE IN
       │           DEPTH the dwarf walks (densify, depth bands, LOS/flood, settle)
       │         WALL  runs → CAct_Climb record mirror (FUN_0040bb70): the Calc1 sub-cells
       │           ×0.125 at the FUN_004238e0 support depth
       │       ⇒ List<NavWaypoint> (Pos world, IsWall, Normal, Clearance,
       │          TransportKind/TransportSourceHandle)
       └─ BuildWalkSegments (DigglesTclEngine.cs:~7905, faithful FUN_004163a0):
            group CONSECUTIVE same-surface waypoints —
              FLOOR run → MetaWalkAction
              generic WALL run → ClimbAction (± bounding floor wps for mount/dismount)
              LADDER wall + same-source tagged floor crossings → LadderRiderAction
              ELEVATOR wall → ElevatorRideAction
            — chained by SequenceAction, then wrapped:
            WalkGuardAction( RetryWalkAction( WalkResultAction( chain ) ) )
```

Coordinate refresher (see `COORDINATE_SYSTEM.md` + memory `reference_antfarm_side_view`):
the tile grid is a SIDE-VIEW ant farm (512 wide × 640 tall); game UP = world +Y = native
−tileY; the height nibble is slab depth INTO the screen (the dig axis).

## Runtime/Navigation/ — the world model + planner

| File | Role |
|---|---|
| `NavWorld.cs` | The faithful nav data: per-tile 8×8 sub-cell bytes (`SubWalk 0x01 / SubFloor 0x02 / SubWater 0x04 / SubClimb 0x08`, tile-major `[tile*64+…]`) derived from PMP solidity + BDB aux heights; the FUN_005a6350 tile graph (`BaseCost` −1 solid / 20 floor / 50 walkable, `IsFloorTile`, `ExitSub`); authored tile+0x18 bit 7 is the EFFPASSWALK force-wall input and is never a template-presence proxy; `Version` bumps on graph/revalidation passes (digs mutate the SHARED `_blockIdx`). The sub-grid is **sparse in the original too** — do not densify. |
| `Calc1Pathfinder.cs` | The tile-level search (`CMapPathfinder::Calc1` port): Bellman-Ford-style relax over BaseCost/IsFloorTile; endpoint snap via sub-cells; climb gate for `canClimb=false`; `iOwner` cost band (campaign default = the >7 no-owner-gate band). |
| `NavWorldService.cs` | Scene host (MonoBehaviour on WorldRoot next to `DiggableTerrain`): world↔native conversion, goal settle (above), run-by-run polyline assembly → `List<NavWaypoint>`. **Warning in-code:** a "trim trailing wall cells" rule was tried and reverted as a regression (it inverted FUN_00423730's type codes and deleted wall-goal climbs) — don't reintroduce. |
| `FloorPathMap.cs` | The FLOOR **depth resolver** (not the climb route — a corrected early misattribution). |
| `NavWorldGizmo.cs` | Editor overlay: draws the +0x3c ground lines as depth strips; refreshes off `NavWorld.Version`. |

## Runtime/Tcl/Engine/ — the action layer (CAction)

`EntityActions.cs` + `ClimbAction.cs` + `LadderRiderAction.cs`. Contract (`IEntityAction`): at most ONE action per
object; `Tick(dt)` per sim step returns running/finished; natural finish evals the Tcl
`finishcode`, replacement evals `breakcode` (the `state_disable this; action … {state_enable this}`
idiom depends on this). `DrivesTransform` (default true) lets the sim skip the per-step
transform read-back for stationary actions — override to `false` ONLY for provably
stationary ones (Wait/Anim do), never for movers.

| Action | Mirrors | Notes |
|---|---|---|
| `AnimAction` | `action anim X` | Resolves via the controller state map; clips named `*loop*` LOOP, else Once; finishes after frameCount/10 s. |
| `WaitAction` | `action wait N` | Pure countdown. Also the headless/degraded walk fallback. |
| `MetaWalkAction` | FLOOR mover FUN_00418850/00418730/00418280 | Faithful turning: heading error ≥ π/4 → stop + pivot in place (turn clip's baked VecB.z rad / 0.1 s tick), snap, replay walk_start; < π/4 → in-stride drift ±0.1 rad/tick, 0.051 snap. Walk→run promotion by TIME IN STRIDE (2.0 s, speedtype 0 only; speedtype 2 = run now, 1 = never). Cruise speed = clip \|VecA\| × 0.05 wu/s (native slot law). |
| `ClimbAction` (own file) | CAct_Climb | Generic wall runs only, including mount/dismount transitions; `WallStandoff` consumes `NavWaypoint.Clearance`. |
| `LadderRiderAction` (own file) | CTransportLogic rider `FUN_00607000:48` → `FUN_0060bc70` | One continuous same-source vertical ride. UP = `rm(slot 0x0e) × 0.1` per 0.1 s tick; DOWN = `v × 0.1`, then `v += 0.25`, cap 2.5. Hard-snap `(ladder.x, rider.y, ladder.z+1)` and native yaw π every tick. Same-source floor waypoints retain only the ladder tag, not wall classification, so interior corridor crossings do not remount; the final floor remains the following MetaWalk exit. |
| `WalkAction` | straight-line walk | Walks along current facing for a distance with start/loop/stop gait stages; turns are `TurnToFaceAction`'s job. |
| `TurnToFaceAction` / `FlyAction` | rotate verb / flyers | |
| `SequenceAction` | — | Chains the per-surface segments. |
| `WalkResultAction` | obj+0x248 stamp | Writes `ent.WalkResult`: 2 = WS_NOTFINISHED while walking, then 4 = TARGETREACHED / 3 = NOPASSAGE. The unmodified Tcl branches ONLY on 2 and 4 (`walkfail` gives up via `4 != [get_walkresult]`). |
| `RetryWalkAction` | FUN_004075b0/00407fb0 AutoRetry | Arrived short of target → re-plan from the body position (max 2). |
| `WalkGuardAction` | CActWalk guards | `-walktimercallback/-walktimercounter` (non-"1" return aborts with WS_WALKEVENT=5) + `-targetdist` finish-early. Sits OUTSIDE the result writer so the 5 sticks. |

Failure pricing: no-path / non-climber-on-wall returns `WalkResultAction(WaitAction(0.2), 3)` —
the 0.2 s matches the native ≥1-logic-tick failure latency (0.05 s under-priced it 4–12×
and fueled dispatcher churn).

Walk-goal contract (memory `reference_walk_goal_contract`): `action walk` does NOT
vector_fix; the engine relaxes off-floor goals to the floor cell. `-randompath` synthesizes
the wander goal on the floor grid (wildlife); box-carriers pass `-canclimb 0` and must get
WS_NOPASSAGE on any wall segment (FUN_004163a0 law, enforced pre-segmentation).

## Who dispatches walks

- **Tcl** — every gameplay walk (work loop, dig approach, flee, wander) via `action … walk`.
- **ClickToMoveService** (`Runtime/Engine/`) — player right-click movement + dig-tool clicks;
  goes through the same engine path (world→native conversion — beware the historical
  double-transform bug noted in `CUTSCENE_SYSTEM.md`).
- **ProdDispatcher** (`Runtime/Engine/Sim/`) — autonomous job step plans (see
  `WORK_DISPATCHER_NATIVE.md`).

Digging interlock: carves mutate `NavWorld` in place (`DiggableTerrain.CarveTileNow` →
`RevalidateRect` over the dirty dependency margins; authored tile+0x18 high flags stay intact);
spiral-snap helpers
(`TrySnapToSub*`, DigglesTclEngine) implement the get_digedge / vector_fixdig cell selection
(`re_parts/dig-edge.md`).

## Testing

Headless EditMode covers Calc1 paths, the WalkResult contract, work loops, and the dedicated
ladder integrator. `LadderRiderTests` pins factual rung reassert, the exact mann ladder:wall
speed ratio, native down acceleration, same-source floor-crossing continuity, and repeat-cycle
stability. Full scene presentation remains **Play-only** to verify visually.
