# Walk executor stair handoff (`walk-executor`,)

## Verdict

The TrollQuest stair graph was sound. Two defects hid behind the red PlayMode repro:

1. The repro invoked `action <obj> walk {x y z} "" ""`. The engine reserves a non-option
   fourth argument for the legacy scalar-distance mover, so the vector failed `float.TryParse`,
   silently became distance `0`, and never entered `BuildMetaWalkFromOptions`. This explains the
   unchanged position and complete absence of an `[action walk]` path log. The native scripts use
   one option-list argument followed by finish/break callbacks:
   `action <obj> walk "-target {x y z}..." <finish> <break>`.
2. Once that test error is removed, the execution handoff still cuts the stair. Calc1 now decodes
   tile `+0x12` as the packed exit pair and returns the twelve proven bends. FloorPathMap lawfully
   resolves depth over that control polyline, but its LOS fast path emits only two or three depth
   anchors. `NavWorldService` passed those sparse anchors directly to `MetaWalkAction`, which treats
   them as complete world-space waypoints and linearly walks the chord from start to goal. The live
   support guard then correctly reports `WS_NOPASSAGE` at the first place that chord leaves the
   one-subcell stair band.

No Calc1, NavWorld derive, tile record, exit-pair, endpoint-relaxation, or FloorPathMap law changed.
The execution adapter now reinserts skipped Calc1 control vertices between the existing FloorPathMap
depth anchors and interpolates only their depth. Existing depth anchors remain byte-law outputs; the
mover merely consumes the complete `(subX,subY)` route instead of a stale scalar-style chord.

## Dump proof

Using `NavProbe/nav_probe.jsonl` probe 1 and the exact mover support predicate
(`FLOOR` at `sy+1`, `sy`, or `sy-1`):

- straight `(1718,1108) -> (1787,1124)`: 42 unsupported sampled subcells; the first is
  `(1725,1110)`;
- the twelve packed-exit segments: zero unsupported sampled subcells.

This is the characteristic “moves toward / dies at the first tile crossing” executor failure. The
original dishonest command stopped even earlier because it selected the zero-distance legacy mover.

## Permanent diagnostic seam

`DigglesTclEngine.WalkExecutorDiagnostics` is opt-in and defaults off. Each traced request gets an id
and emits one concise line for:

- `action-precondition`: option/scalar shape, current state, prior action, and installed action type;
- `goal-relax`: raw, Calc1-relaxed, and floor-settled endpoint subcells;
- `layer-selection`: Calc1 waypoint count, FloorPathMap depth-anchor count, expanded executable count,
  and the precise null layer on rejection;
- `mover-start` / `mover-stop`: the first real mover tick or its concrete missing dependency/support
  rejection.

The PlayMode repro enables this seam, drains the gnome's prior idle action, disables its autonomous
state machine, selects it as the click path does, derives start/goal depth from live `get_hmap`, uses
the canonical `-target` command, installs `walk_ok`/`walk_fail` callbacks, asserts a
`RestoredWalkAction` plus synchronous `WS_NOTFINISHED`, and logs position/action/state/result once per
simulation second.

## Expected gated run

The first request should report an accepted option walk, the unchanged probe endpoints after goal
resolution, a floor layer similar to `floorpathmap[0..12) depth=2 exec=12`, and an accepted
`mover-start`. The per-second lines should advance east/down the stair without `mover-stop`, then show
`action=-`, `walkResult=4`, `walk_ok=1`, `walk_fail=0` near `(223.39,140.5)`.
