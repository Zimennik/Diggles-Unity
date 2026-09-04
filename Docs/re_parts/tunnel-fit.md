# Tunnel fit and TrollQuest debris regression (`tunnel-fit`,)

## EFFPASSWALK 31-probe verdict

The axis-transposition hypothesis is falsified. `FUN_005a6b40` stores the detail raster row-major:
the Y offset is multiplied by the scratch-buffer width and the X offset is the unscaled column add.
`NavWorld` used those axes correctly. Its old table was merely sorted and represented entries as
`{dy,dx}`; it is now written as named `(Dx,Dy)` entries in the native short-circuit order.

The exact calls from `005a6b40_FUN_005a6b40.c` are:

| # | dx | dy | decompile lines | old port slot | current port entry |
|---:|---:|---:|---:|---:|---|
| 1 | 0 | -8 | 116-125 | 13 | `(0,-8)` |
| 2 | -7 | -15 | 126-135 | 30 | `(-7,-15)` |
| 3 | 7 | -15 | 136-143 | 31 | `(7,-15)` |
| 4 | 0 | -15 | 144-151 | 27 | `(0,-15)` |
| 5 | 0 | -1 | 152-160 | 1 | `(0,-1)` |
| 6 | -7 | -8 | 161-168 | 16 | `(-7,-8)` |
| 7 | 7 | -8 | 169-177 | 17 | `(7,-8)` |
| 8 | -4 | -3 | 178-187 | 4 | `(-4,-3)` |
| 9 | 4 | -3 | 188-196 | 5 | `(4,-3)` |
| 10 | -7 | -5 | 197-206 | 9 | `(-7,-5)` |
| 11 | 7 | -5 | 207-214 | 10 | `(7,-5)` |
| 12 | -7 | -12 | 215-224 | 23 | `(-7,-12)` |
| 13 | 7 | -12 | 225-232 | 24 | `(7,-12)` |
| 14 | -4 | -15 | 233-240 | 28 | `(-4,-15)` |
| 15 | 4 | -15 | 241-249 | 29 | `(4,-15)` |
| 16 | 0 | -12 | 250-258 | 22 | `(0,-12)` |
| 17 | 0 | -4 | 259-268 | 6 | `(0,-4)` |
| 18 | -4 | -8 | 269-276 | 14 | `(-4,-8)` |
| 19 | 4 | -8 | 277-285 | 15 | `(4,-8)` |
| 20 | -3 | -13 | 286-296 | 25 | `(-3,-13)` |
| 21 | 3 | -13 | 297-306 | 26 | `(3,-13)` |
| 22 | -5 | -11 | 307-318 | 20 | `(-5,-11)` |
| 23 | 5 | -11 | 319-328 | 21 | `(5,-11)` |
| 24 | -2 | -10 | 329-341 | 18 | `(-2,-10)` |
| 25 | 2 | -10 | 342-351 | 19 | `(2,-10)` |
| 26 | -2 | -6 | 352-362 | 11 | `(-2,-6)` |
| 27 | 2 | -6 | 363-372 | 12 | `(2,-6)` |
| 28 | -5 | -5 | 373-382 | 7 | `(-5,-5)` |
| 29 | 5 | -5 | 383-393 | 8 | `(5,-5)` |
| 30 | -2 | -2 | 394-406 | 2 | `(-2,-2)` |
| 31 | 2 | -2 | 407-418 | 3 | `(2,-2)` |

“Old port slot” is the one-based index in the former `{dy,dx}` array after applying its actual
lookup (`x += [i,1]`, `y += [i,0]`). All 31 semantic coordinates were therefore already present;
only native short-circuit order and explicit axis names were absent.

Axis proof:

- `005a6b40:76-83` expands `{x0-8,y0-16,x1+8,y1+2}` and fills the detail buffer.
- `005a6b40:100-106` multiplies the outer Y cursor by `iVar5` (row width); the inner X
  cursor is the unscaled add. Every table address therefore has the form
  `(sy + dy) * rowWidth + sx + dx`.
- `00595e40:82-115` copies one 8-byte source row at a time into successive destination rows;
  its partial path starts at `record + localY*8 + localX` (`:118`) and writes to
  `(globalY-y0)*rectWidth + globalX-x0` (`:125-128`).
- Every gate compares the raw BDB detail byte operand `*pbVar15 < 0x0e`; there is no nibble mask
  (`005a6b40:125,135,...,418`). Detail 14 and 15 reject; 0 through 13 pass.

The target-11 solid-world reconstruction also distinguishes the orientations:

| carved shape | native axes: walk/floor cells | swapped axes: walk/floor cells |
|---|---:|---:|
| horizontal cell strip bounded by two node rows | 184 / 184 | 188 / 0 |
| vertical cell strip bounded by two node columns | 188 / 4 | 184 / 0 |

Thus swapping the axes would create the reported floor failure rather than fix it. The EditMode
falsifier carves both complete 2xN shapes through `DiggableTerrain.CarveStep`, asserts every changed
horizontal tile has walk+floor and every changed shaft tile has walk, and prints the first rejecting
native `(dx,dy)` plus detail byte on failure.

## the live carve-shape falsifier

The red live assertion did **not** expose a stamped/special-block branch in `CarveStep`.
`dig_apply` maps the native point with `floor(coord+0.5)` and calls the same
`DiggableTerrain.CarveStep` used by the synthetic target-11 test (`DigglesTclEngine.cs:1626-1662`).
There is no primitive `ApplyDig` call on this path.

The divergence was in the fixture driver. It painted both rows but manually completed only the
lower node at the near face before asking a four-block *node footprint* to be walkable. Native
`FUN_00596b80` owns exactly one shared grid node per call: it reads the mark target (`:48-61`), gets
the shared rim (`:62-63`), conditionally follows the four corner transitions at `+0x4c/+0x48/+0x44/+0x40`
(`:64-119`), and writes those four surrounding block IDs (`:120-136`). Those four tiles include
ceiling/wall boundary blocks; the routine never promises that every block it changes is itself a
complete walkable tunnel tile. The standard painter owns the complete two-row stroke
(`FUN_00566550:26-67`; `FUN_005672d0`), and repeated `dig_next`/`dig_apply` calls consume its nodes.

The exact authored `hol_020` data reproduces the failure without Unity:

| operation | block `(180,126)` | native rim | EFFPASSWALK result |
|---|---:|---:|---|
| authored | 3075 | 11 | no clear foot; `(0,-8)=15` |
| only node `(180,126)` | 36379 | 7 | no clear foot; `(0,-8)=15` |
| plus paired row node `(180,125)` | 36379 | 7 | 8 clear feet, but no floor yet (drag-axis boundary incomplete) |

The first transition is exactly the BDB successor selected by `FUN_005968b0`/`FUN_00596b80`; it is
therefore positive evidence that stamped block 3075 follows the native target-11/shared-rim law.
Forcing that one node's four changed blocks to become walkable, or making `CarveStep` implicitly
carve neighbouring nodes, would change the native atomic operation.

The owning test seam now builds a native-shaped 8x2 mode-1 order, completes both rows over the six
reachable mouth columns through the real `dig_apply`, and leaves two continuation columns pending.
Its face finder requires `TemplateRecordWrites`, not `HasTpl`, so native-transparent template
padding cannot masquerade as authored rubble.
Its strengthened acceptance asserts every completed foot-plane tile gains a walk bit, at least one
gains a new walk+floor cell, and Calc1 can reach the next real `get_digedge` point. The diagnostic
still prints the first failing `(dx,dy)` and raw detail byte. `CarveStep`, EFFPASSWALK, and EFFBLOCK
remain unchanged by because the RE/data falsifier cleared all three.

The live test already proves Calc1 can route from the gnome's standing cell to the next rubble work
point after that synchronous refresh. Consequently, if Play still stops after these nav assertions
pass, the remaining failure is downstream of EFFPASSWALK: capture the existing walk-executor seam
for the actual `evt_task_dig` continuation and pin whether `goal-relax`, `layer-selection`, or
`mover-stop` rejects. No executor law is changed in this patch.

Executor patch plan (deliberately not applied here):

1. Enable `DigglesTclEngine.WalkExecutorDiagnostics` around the real queued `evt_task_dig`, then
   advance the unmodified task until it either enters the second rubble order or returns a walk
   result. Preserve the four existing trace stages from `walk-executor.md`.
2. If `goal-relax` or `layer-selection` rejects, pin the raw dig-edge goal, relaxed Calc1 cell,
   FloorPathMap layer, and `LastNullReason` in the live fixture before changing the owning adapter.
3. If `mover-stop` rejects, pin the first rejected world/subcell coordinate and its
   `SubWalk|SubFloor` neighbourhood. Only then change the executor/support seam responsible for that
   rejection; do not widen EFFPASSWALK or rewrite stamped navigation data.

## Round 3: authored rubble-face fixture

Restoring the visual data exposed a test-identity bug before the carve: the old finder tried
to rediscover an authored rubble face by scanning mutable live material/terrain classifications.
Those predicates had been calibrated while the delayed matlist was repainting template records, so
they were not a stable identity after the source bytes were restored.

The fixture now parses both relevant authored PMPs and pins source bytes against the live world:

- `hol_005.pmp`, stamped by `troll_quest_start.tcl:3` at `(196,132)`, contains debris records in
  both terrain `0x04`/`0x84` (material class 0) and terrain `0x02` (class 2). Representative block,
  terrain, and material triples are asserted before all non-transparent records are compared.
- `hol_020.pmp`, stamped by `troll_quest_001.tcl:3` at `(172,124)`, authors the live carve face in
  local columns 8..18, rows 1..2. All twenty-two terrain bytes are `0x02` (class 2); the upper block
  row is `[3071,3072,3073,0,0,0,0,0,0,0,0]` and the lower row is
  `[3075,80,3076,0,0,0,0,0,0,0,0]`. In world coordinates that is
  `(180..190,125..126)`. These are non-transparent PMP records: block ID zero does not imply
  transparency; native `FUN_005992b0:292` tests the raw 2x2 terrain-byte window.

Those twenty-two source/live records are pinned before the fixture's vertical seam dig. That dig
legitimately follows the BDB transition graph for world node lines 181-182, changing the decorative
mouth blocks in columns 180-182 and leaving the authored suffix at columns 183-190 untouched. The
post-seam finder therefore uses local columns 11..18 as the 8x2 mode-1 stroke, after proving their
live block IDs remain zero, terrain remains `0x02`, material remains class 2, record-write bits are
set, and both node rows retain rim 11. The real standing floor is local column 9, column 10 is the
transitioned mouth, and column 11 starts the still-solid order.

The 8x2 order, six-column completion, pending continuation, post-carve walk/floor checks, and
Calc1/dig-edge acceptance from are unchanged. This round changes only the fixture and this
evidence note; there is no runtime carve, probe, derive, or stamping change.

## Round 4: continuation ranking from the real mouth

The final red was another fixture-state defect. Before the first `get_digedge`, the fixture put the
gnome on the authored floor in `hol_020` local column 9. It then manually completed six shared-node
columns but never executed the walk leg that follows a real dig action. Consequently the second
`get_digedge` ranked its advanced frontier against that stale pre-carve position; the observed
6.65-tile distance measured the length of the newly opened mouth, not a remote-candidate defect.

The native operand is unambiguous. `FUN_00635180:153-158` builds the reference as
`ROUND(gnome.x + 0.5), ROUND(gnome.y + 0.5)`. The `FUN_005981d0` disassembly at
`0x5983e1-0x598408` computes `(candidate.x-reference.x)^2 +
(candidate.y-reference.y)^2` and replaces the winner only on strict `<`. Marker position controls
the BFS seed; it does not replace the gnome reference used for ranking.

After the six-column carve, the fixture now finds a `SubWalk|SubFloor` cell in the leading complete
tunnel tile, proves Calc1 can walk there from the authored floor, and uses the real Tcl `set_pos`
seam to place the gnome on that settled mouth before issuing the second `get_digedge`. The existing
local-distance acceptance and the final Calc1 route are then evaluated from the gnome's actual
post-carve mouth. This round changes only the live fixture and this evidence note; the picker,
carve, probe, derive, and stamping runtime laws remain untouched.

## Round 5: remaining-marker seed and fresh-face exploration

The next failure was downstream of the synchronous nav refresh, not evidence that the partial
order lost its flood seed. The native seed law is explicit in the capstone disassembly:

- `FUN_00635180:155-158` rounds the Tcl marker argument and passes it as `param_2`;
- `0x598299-0x5982db` enqueues that exact tile and sets its owner-channel visited bit;
- `0x598418-0x59841e` expands from a queued tile only while its `0xb << owner*4` mark bits remain;
- `0x598424-0x5985a4` visits east, west, down, then up, and `0x5985b4-0x59860d` clears the
  scratch bit from every queued tile before returning.

The fixture's second call passes the first untouched lower node, `(continuationX, nearY)`, rather
than the consumed first node. For the pinned `hol_020` stroke this is world `(189,126)`; its paired
upper node is `(189,125)`. Both must still carry the mode-1 nibble `3` after the six completed
columns and after fog reconciliation. The test now asserts those exact post-reveal seed nibbles and
cross-checks the Tcl result against a direct `TryFindDigEdge` call with the same seed, owner, rounded
gnome reference, and fog state. No runtime seeding change is warranted.

The fixture did omit the other native input. It moved the gnome to the new mouth and immediately
issued `get_digedge`, before the gnome's presence could reveal the newly exposed frontier.
Candidate test B at `0x5983b6-0x5983df` rejects `node+0x34+owner == 0` even when candidate test A
already proved `record[0] < 0x0c`. `CFogOfWar::UpdateFoW` processes revealers on its six-way stripe
(`FUN_0057f5c0:134-198`), and the game calls it every fourth think
(`CGameThread::SrvDoThink`, `0x006b4a30:393-421`). A few immediate ticks therefore do not model the
fresh face becoming explored.

After `set_pos`, the fixture now advances 30 × 0.1 seconds: at least seven FoW updates, enough to
cover a complete six-slot revealer stripe. It temporarily uses sequence mode only to keep the
fixture's already-asserted `evt_task_dig` event queued; fog continues during sequences by the same
native game-thread law. The acceptance message permanently records the second seed, owner, both
remaining mark nibbles, gnome/reference coordinates, reveal sizes, FoW tick range, and every nearby
cell's mark nibble, FoW byte, block ID, and BDB record byte 0 before and after the reveal. Thus any
future red distinguishes an empty marked flood (`m0`), black frontier (`f00`), solid candidate
(`r>=12`), wrong ranking, or a genuinely disconnected Calc1 goal without another diagnostic round.

Round 5 changes the live fixture and this evidence note only. `TryFindDigEdge`, fog reveal,
`CarveStep`, EFFPASSWALK/EFFBLOCK, stamping, and the walk executor remain unchanged.

## TrollQuest debris regression

History comparison:

- Pre-epic `04519d3` (`ee013b1^`) tested source transparency before all per-tile writes. Therefore
  transparent records carried no block, zone, or terrain byte and also did not enter `HasTpl`.
  Live `DrawStone` passed `HasTpl` as its skip mask, preserving authored records while painting
  transparent padding.
- `ee013b1` correctly moved the independent authored navigation-plane copy and template-coverage
  flag ahead of the transparency `continue`, but also changed live `DrawStone` from `r.HasTpl` to
  `null`. That unrelated change let the later TrollQuest matlist overwrite authored debris terrain.
- `6c953d4` changed navigation-plane naming/semantics, not the block/terrain write gate. Current
  `BlitPmpIntoWorld` still writes block, zone, and terrain only below the transparency check.

Native `FUN_005992b0` agrees with that split. The all-zero 2x2 terrain-window condition is at
`:292`; only its true branch writes tile+0x18/material (`:317-377`), block ID (`:379-415`), and the
record byte at node+7 (`:416-453`). The false/transparent path carries none of them. The later
tile+0x13 coverage pass (`:461-529`) and authored navigation-plane blit (`:553-568`) are independent
and cover transparent padding too.

Using `HasTpl` directly is no longer sufficient because it now correctly includes transparent
padding. The repair records actual non-transparent writes during every live PMP blit and supplies
that `TemplateRecordWrites` mask to the delayed live material painter. This reproduces the pre-epic
visual result without undoing the native navigation/coverage fix.

The TrollQuest fixture parses `hol_005.pmp` directly, pins representative authored debris block IDs
including 1250-1317, checks every non-transparent live block and terrain byte against the PMP, and
requires `Dummy_Multi_Holzgeruest_001`, `Dummy_Traeger_h`, and `Dummy_Traeger_b` to have active
GameObjects at their authored positions inside the 16x8 start tunnel. Those positions come directly
from `Templates/tcl/troll_quest_start.tcl:18,66,72`: `(205.5,138.5,9.01)`,
`(207.5,138.5,9.01)`, and `(199.51999,138.5,9.01)` after the `(196,132)` preset offset.
