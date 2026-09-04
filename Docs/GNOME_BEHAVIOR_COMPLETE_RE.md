# GNOME BEHAVIOR — COMPLETE RE (the authoritative 1:1 re-implementation spec)

> This is an RE ground-truth doc: the decompile addresses / struct offsets / constants are LAW and were
> NOT re-audited here. Only the PORT-STATUS notes were re-checked against the current tree:
> - **All 30 gaps in §10 have been addressed — Phases 0-7 are DONE; the per-phase DONE
> notes in §11 are the live status.** Confirmed against code: `NavWorld.ReDeriveSubGrid` (EFFPASSWALK,
> gap #1) exists (`NavWorld.cs:200`); `tasklist_rem` now removes BY INDEX (gap #5, `DigglesTclEngine.cs:192`);
> `FloorPathMap.cs` exists (Phase 4).
> - The §10 gap table is the **ORIGINAL pre-fix analysis**; its "Our state (port)" column and file:line
> anchors describe the code as it was BEFORE the fixes and are frozen for provenance — read §11 for
> what actually shipped. Line numbers throughout have drifted with the tree.
> - **Deleted since (Phase 7 cleanup):** `WalkClimbAction.cs` and `DigglesInputController.cs` — referenced
> in §10 rows #4/#7/#9/#30 as if live; they no longer exist (ClickToMoveService owns input). All other
> referenced port files still exist (paths moved under `Runtime/{Navigation,World,Tcl/Engine,Engine/Sim}`).

> The single source of truth for re-implementing the Diggles/Wiggles (2001) gnome
> **movement / dig / work / idle** stack faithfully. It consolidates the 16 area RE specs in
> `Docs/re_parts/*.md` (6 of which carry adversarial "## Verification notes"), deduplicates them,
> resolves the contradictions found between areas (by re-checking the cited decompile), and keeps
> every `FUN_addr:line` / struct-offset / constant citation. Where a primary source is Ghidra-mangled
> or a claim is reconstructed, it is marked **INFERENCE** / **UNRESOLVED**.
>
> Decompile root: `the decompile<addr>_FUN_<addr>.c`.
> Tcl: `the decompile`. Live dumps: `the decompile`.
> Per-area sources are cited inline as `[area-key]` and resolve to `Docs/re_parts/<area-key>.md`.

---

## 0. Executive summary + reading map

A gnome's behaviour is **four cooperating layers**, all clocked by one server tick
`CGameThread::SrvDoThink` (`FUN_006b4a30`):

1. **Decision (Tcl)** — a per-object state machine + tasklist FIFO (`idle`/`task`/`work_*`/`sparetime`),
   driven by user clicks (`evt_task_*`) and the native autonomy planner (`evt_autoprod_*`). It issues
   high-level verbs (`walk_action`, `dig_starttask`) and gates every step on `get_walkresult == 4`.
   [tcl-work-layer, sparetime-idle, dispatcher-reconcile, tcl-dig-complete]

2. **Action stack (native)** — `action <obj> walk` builds `CActWalk` → `CActWalk_AutoRetry` →
   `CAct_MetaWalk`, which segments a `Calc1` path into FLOOR/WALL/WATER runs and dispatches one
   sub-action per run: `CAct_WalkFloor` (floor), `CAct_Climb` (wall), `CAct_WalkObject` (water). The
   leaf mover for every run is `CAct_WalkAnim`, a root-motion integrator.
   [walk-lifecycle, metawalk-core, walkfloor, climb-route, climb-exec, calc1-confirm, animsets]

3. **World mutation (dig)** — the Tcl dig loop bottoms out in dig natives (`get_digedge`, `dig_next`,
   `vector_fixdig`, `dig_apply`), the block carve `FUN_00596b80`, and the **MapValidator** that
   re-derives the per-sub-cell passability `+0x3c` from the heightmap (EFFPASSWALK) so freshly-dug floor
   becomes walkable. [dig-edge, dig-natives, carve-validator, tcl-dig-complete]

4. **Entity state** — the CObj anchor (feet-on-floor-line), the single per-tick position writer, gravity,
   and facing. [entity-anchor]

### Reading map (which section answers which question)

| You want to… | Read |
|---|---|
| Understand the data the whole stack reads | §1 world/walkability model |
| Wire `action walk` → result codes | §2 action stack + WS enum |
| Make floor walking glide at the right speed | §3 floor walking |
| Build a faithful climb | §4 climbing (route + executor + clips) |
| Carve terrain and reopen walkability | §5 digging + EFFPASSWALK |
| Drive autonomous + commanded work | §6 work layer |
| Make idle gnomes wander/rest | §7 sparetime + idle |
| Couple movement speed to animation | §8 animation coupling |
| Place/face the gnome correctly | §9 entity anchor |
| See every port gap in one table | §10 consolidated gap table |
| Build it in the right order | §11 implementation plan |
| Know what's still unknown | §12 unresolved |

### Headline corrections vs the port (full list in §10; top 10 in the return summary)

The port's biggest faithfulness failures are: (a) **EFFPASSWALK is not ported** — dug floor never
becomes walkable; (b) **climb is hand-synthesised** (`Kind{Mount,CrawlLeft,…}`) on the wrong axis;
(c) **movement speed is a height-fraction heuristic**, not per-clip root-motion arc consumption;
(d) **`tasklist_rem` removes by value** so every dispatch loop spins forever; (e) Calc1 uses the **wrong
cost variant** for campaign gnomes (276 vs 266) and lacks the owner gate. Each is detailed below with the
exact native behaviour and the port `file:line`.

---

## 1. The world / walkability data model (frame-corrected)

### 1.1 The frame (ground truth — older notes mislabel axes)

The 512×640 tile grid is the **SIDE VIEW** (ant farm). In the CObj struct (all floats) and in tile space:

- **native X** (`CObj+0x0c`) = horizontal EAST = the **walk** axis.
- **native Y** (`CObj+0x10`, tile rows) = the **gameplay VERTICAL**: `+Y = down/deeper`, gravity and the
  climb axis act on it. [entity-anchor §0, metawalk-core, walkfloor, all areas]
- **native Z** (`CObj+0x14`) = **DEPTH INTO THE SCREEN** = the **dig** axis. `world z = nibble*0.5 + 7`,
  range **7.0..14.5** (`DAT_0073708c=0.5`, `DAT_00737520=7.0`). Distance metrics weight this axis **×0.5**.
  [dig-edge §5, calc1-confirm §9]

When older notes say "height"/"elevation" for the BDB nibble, read **DEPTH**. The gameplay vertical is
tile-Y. The port's `RuntimeWorldSpawner.NativeToLocal(n) = (n.x, n.z*0.5+7, -n.y)` is the exact image
(native X→localX, native Z→localY height, native Y→local −Z). [entity-anchor §0]

### 1.2 Tile + sub-grid + BDB layout

- **Tile record = 0x7c (124) bytes**, addressed row-major: `idx = (Y*stride + X) % modulus`,
  `tile = idx*0x7c + base` via `FUN_00592f10(CMap, X, Y)` with clamp to `+0x84..+0x90` bounds.
  [dig-edge §3, calc1-confirm §3] Per-tile fields used by the stack:

| Tile off | Meaning |
|---|---|
| +0x00 | `block_idx` → BDB cell (`*0x59`) |
| +0x06 | derived BaseCost gate byte (−1 solid / 20 floor-or-climb / 50 plain walkable); `-1` ⇒ invalid |
| +0x08 | **Per-owner dig-state word** (8 owners × 4 bits, shift `owner*4`) |
| +0x0c | parallel per-owner dig-order ID word (the selected ID is cached at gnome `+0x250`) |
| +0x12 | derived graph word: **bit 0x40 = floor-tile flag**, low bits = **exit sub-cell** `(subX in 0-2) | (subY in 3-5)` |
| +0x13 | dirty bits (`|=1` block changed, `|=2` re-render) |
| +0x18 | dig precondition (low 2 bits: 0 buried, 3 fully open, 1/2 = exposed/diggable; **high bit 0x80 = fully enclosed**) |
| +0x34..0x3b | per-owner flag array `[0x34+owner]` (the Calc1 owner gate) |
| +0x3c..0x7b | **8×8 sub-cell passability**, idx `((subY&7)<<3 | (subX&7))`; bit1=walk, bit2=floor(standable), bit4=water, bit8=climb |

- **`+0x3c` sub-grid bit semantics** (the classifier source, [metawalk-core §5]):
  `&1`=walkable, `&2`=floor (standable ground line), `&4`=water, `&8`=climb. **value 3 (walk|floor)** =
  the GROUND-LINE floor cells gnomes stand on; **value 1 (walk only)** = climbable wall band.

- **BDB cell = 0x59 (89) bytes**, `block_idx*0x59 + CMap[+0x24][+0x10]`. Within it:
  - `+0x00..+0x3f` = 8×8 **height nibbles** (the depth slab; `get_hmap`/`FUN_004093f0` reads
    `aux[((subY&7)<<3 | (subX&7))]`). [dig-edge §2, carve-validator §2.4]
  - `+0x40/+0x44/+0x48/+0x4c` = the 4 **corner-transition** block indices (the carve graph).
  - `+0x50/+0x51/+0x52/+0x53` = the 4 **corner gate / rim height** bytes.

- **`get_hmap` (`FUN_004093f0`)**: `FUN_004093f0(sx, sy)` → `tile(sx>>3, sy>>3)` then
  `aux[((sy&7)<<3 | (sx&7))]` = the per-sub-cell depth nibble 0..15; **OOB ⇒ 15**. [dig-natives §0, tcl-dig-complete §3]

### 1.3 The derived pathfinding graph (EFFBLOCK = `FUN_005a6350`)

The per-tile `+0x06`/`+0x12` are **derived** from the `+0x3c` sub-grid by `FUN_005a6350` (the port's
`NavWorld.DeriveTile`): scan 64 sub-cells, score `((floor?0x100:0) - |4-row| - |4-col| + 8)`, keep first
max → `ExitSub`, `IsFloorTile = (best&0x100)!=0`, `BaseCost = -1 solid / 20 floor-or-climb / 50 walkable`.
The port omits two refinements (enclosed-tile −1 forcing, climb-band exit walk) — LOW/MED gap.
[port-inventory §1.1, carve-validator §0]

### 1.4 The two derivation passes, and the dig contract (forward reference)

Walkability is **GEOMETRY-derived and recomputed after every dig**, in a fixed order by the MapValidator:
**EFFPASSWALK (`FUN_005a6b40`, re-derives `+0x3c` from the heightmap) runs BEFORE EFFBLOCK
(`FUN_005a6350`, re-derives `+0x06`/`+0x12` from `+0x3c`).** A dig that lowers geometry but skips
EFFPASSWALK leaves stale walkability — the gnome cannot path onto the new floor. Full detail in §5.4.
[carve-validator §0, §3]

---

## 2. The action stack — lifecycle + result codes

### 2.1 Object graph (per active walk)

```
action <obj> walk "<opts>" [finishcode] [breakcode]
  → FUN_00405e10 name→action factory: "walk" = RTTI 0x24 → CActWalk
  → FUN_00407430 CActWalk::init: stash gnome+codes, build+parse CWalkInfo,
                   SEED gnome+0x248 = 2 (WS_NOTFINISHED), gnome+0x2b4 = 1 (is-walking), action+0x24 = 2
  ── per frame the action-stack ticks CActWalk::Tick = FUN_004075b0 ──
  → CActWalk_AutoRetry (RTTI 0x6d): off-map AABB reject → WS_NOPASSAGE; lazily new + tick the inner
                   CAct_MetaWalk (RTTI 0x23, rebuilt whenever the gnome moved); re-aim "nudge"; fire
                   -walktimercallback every N ticks; on child finish mirror status → action+0x24 AND gnome+0x248
      → FUN_00407fb0   (re)build the inner CAct_MetaWalk (CalcPath → CPath)
  → FUN_00407b30 CActWalk::Break: action+0x24=1, gnome+0x2b4=0, gnome+0x248=1 (WS_WALKABORTED), destroy child
```
[walk-lifecycle §1-§6]

### 2.2 The WalkResult enum (THE Tcl contract) — `gnome+0x248`, returned by `get_walkresult`

| value | symbol | meaning | who sets it |
|---|---|---|---|
| 0 | WS_ERROR | bad surface / internal | metawalk `FUN_004163a0:82` |
| 1 | WS_WALKABORTED | Break verb interrupt | `FUN_00407b30:12` |
| **2** | WS_NOTFINISHED | walking, in progress (the SEED) | `FUN_00407430:64`, `FUN_00415d10:79` |
| **3** | WS_NOPASSAGE | no path / off-map / no-climb wall | `FUN_004075b0:51`, `FUN_004163a0:23/42` |
| **4** | WS_TARGETREACHED | arrived (cursor exhausted) | `FUN_004163a0:28` |
| **5** | WS_WALKEVENT | walk-timer callback aborted | `FUN_004075b0:216` |

> **Reconciliation:** `dispatcher-reconcile` and `metawalk-core` confirm the numeric set {2,3,4,5}
> from the decompile/Tcl; the 0=ERROR vs 1=WALKABORTED split is INFERENCE (the string table lists the
> WS_* names alphabetically and does not fix the order). The port's {2,3,4} usage is CORRECT.
> [walk-lifecycle §5.4, metawalk-core §1, dispatcher-reconcile §5]

**Tcl idiom:** `if {[get_walkresult $o]==2}` = still walking; `if {4 != [get_walkresult $o]}` = failed
(→ `walkfail` abandons the job). Seeded to 2 at issue; set to the terminal WS_* on stop; **only `==4`
advances the work loop.** This is THE movement↔work contract; a `get_walkresult` that doesn't return 4 on
arrival makes every walk "fail" (the historical "0-stub broke everything" bug). [tcl-dig-complete §4, dispatcher-reconcile §5]

### 2.3 The walk-option parser `FUN_00420840` (CWalkInfo) — 22 options

`CWalkInfo` (CActWalk+0x1c, built by `FUN_00420520` then parsed by `FUN_00420840`). The cascade (precedence):
`-target → -digpos(stub) → -object → -dummy → -dontstop → -canclimb → -stopat → -useobjects → -speedtype →
-randompath → -waving → -randomz → (no-nudge bool) → -forcetarget → -targetdist → -randompathwalldist →
-walktimercallback → -walktimercounter → -walkcollisioncallback → -objbreak → -animsets → -climbrot`.
**There is NO `-dig`, `-canswim`, or `-counter`** (those were prompt-list errors). [walk-lifecycle §4]

Key fields + defaults (CWalkInfo byte offsets): `+0x7a4/8/c` target xyz (default `-1000,-1000,14.0`),
`+0x7b0` -canclimb (def via parser, treated 1), `+0x7a0` -stopat (def **2.0**), `+0x7c8` -targetdist
(def **−1**=off), `+0x79c` -waving (def **1**), `+0x7e8` -speedtype (def 0=auto), `+0x7e0`
-walktimercounter, `+0x7ec` -walktimercallback body, `+0x08/+0x1ec/+0x3d0/+0x5b4` = 4 animset ids
(walk/run/3rd/4th). [walk-lifecycle §3, §4]

Post-parse fixups: `-dummy`+`-object` resolves the object's work-dummy link (`FUN_004059a0`) →
the actual work-spot target; the speedtype "needs-to-move" flag is derived from dist²>threshold; a
diagonal-corner passability pre-check sets `+0x7d8`. [walk-lifecycle §4.4]

### 2.4 The AutoRetry tick `FUN_004075b0` (per frame) — COMPLETE

1. **First tick:** if target is outside the playable AABB (and not random-path) → `WS_NOPASSAGE`,
   stop. Else new the AutoRetry child + build the inner CAct_MetaWalk.
2. **`FUN_00407fb0` re-plan:** whenever the gnome's pos changed, rebuild the CAct_MetaWalk toward the
   (possibly moving) target. **Plain MetaWalk never re-paths; the AutoRetry layer does.**
3. **Tick the child:** when it finishes (`tick`→0), mirror `child+0x08` → `action+0x24` AND `gnome+0x248`.
4. **Re-aim nudge** (only on a finished-but-short walk, result not 3/5, no-nudge flag clear,
   `-forcetarget`/`-object` set): if the target sub-cell is blocked, rotate the target by π and re-plan —
   "edge in toward the reachable spot instead of failing outright."
5. **Walk-timer callback:** every `-walktimercounter` ticks (and on finish), `eval` the
   `-walktimercallback` body; a non-`"1"` return aborts with `WS_WALKEVENT (5)`.
[walk-lifecycle §5]

### 2.5 CAct_MetaWalk — the segmenting maintainer `FUN_00415d10/004158a0/00416540/004163a0`

- **Ctor** stores the CPath (already computed), cursor `[base, end)`, animset payload; sets state 2.
- **CalcPath `FUN_004158a0`:** branch `owner[+0x7b4]==0 → Calc1` (campaign) else `Calc2`. The target-Z
  clamp + owner-target write-back runs **ONLY in the Calc2 branch** (`else` closes at `:145`); the Calc1
  branch keeps the parser's `-target`. Patches the final waypoint; synthesises a 1-point path when
  start==goal cell. [metawalk-core §3, Verification notes]
- **MakeNextSegment `FUN_004163a0` (the grouping rule):** classify the waypoint at the cursor via
  `FUN_00423730`, then **group the maximal run of consecutive waypoints sharing the SAME surface class**
  (and, for water, the same water-body id). Dispatch:
  - **2 = FLOOR** → `FUN_00415f40` (WalkFloor factory)
  - **0 = WALL** → `FUN_00415e10` (Climb factory); if `-canclimb (+0x7b0)==0` ⇒ **WS_NOPASSAGE (3)**
    before grouping; a non-terminal WALL run bumps `end+1` to overlap the next surface (dismount target).
  - **1 = WATER** → `FUN_00416120` (WalkObject/swim) [out of scope until water]
  - else ⇒ WS_ERROR.
  The factory ABI is `__thiscall(this=MetaWalk, startIdx, endIdx, endingSurfaceClass)`; the slice is
  `[start, end)`. [metawalk-core §4, Verification notes]
- **Surface classifier `FUN_00423730`** = pure `+0x3c` bits: `bCanSwim && (b&4) && active-water-body → 1`;
  else `(b&2)==0 → 0 WALL`; else `2 FLOOR`. [metawalk-core §5]
- **Per-tick `FUN_00416540` propagation:** tick the child; **only result 4 continues** (free child, build
  next segment); 3/5/0 propagate up and stop. Cursor exhausted on entry ⇒ MetaWalk writes 4 (whole-walk
  TARGETREACHED). [metawalk-core §6]

---

## 3. Floor walking — `CAct_WalkFloor` (3-level nested action)

`CAct_WalkFloor` is **not a flat mover**. It is a 3-level stack: WalkFloor (per segment) →
`FUN_0041aac0` (MakeNextSegment: build a FloorPathMap, sample per-node depth, spawn) → `CAct_WalkAnim`
(per-tick root-motion integrator). [walkfloor §0]

### 3.1 The destination depth cap (factory `FUN_00415f40`)

When building the FINAL floor segment that is immediately followed by a WALL (`endingSurfaceClass==0`),
the destination depth = `min(min(h_nibbleA, h_nibbleB) + 1.0, 15.0)` where `h_*` are the two endpoint
sub-cells' BDB depth nibbles (take the **MIN**). `-1` (`DAT_0073773c`) is the "not computed" sentinel.
This is the "don't sink the feet into the slab" cap. [walkfloor §1, Verification notes]

### 3.2 The arc-length segmentation (start/loop/stop) `FUN_0041a470`

Compute the total XY arc length of the point span. If `total ≥ 96.0` (`DAT_00737608`): emit
`walk_start → walk_loop …` sub-segments of **32.0-unit** arc (`DAT_007375b0`) + a terminal 32-unit
"stop" sub-segment counted backward from the end. Else (short hop): one sub-segment. Units are sub-cell
ints (world·8), so 32 ints = 4.0 world units, 96 ints = 12.0 world units. **This is an arc-length
partition of the route, not a fixed clip schedule.** [walkfloor §2.1]

### 3.3 The root-motion integrator `FUN_00418db0` (THE gliding) — COMPLETE

Phase machine on `movable[+0x234]`: **1 = walk_start (clip 0x15=21), 0 = walk_loop (clip 0x10=16),
2 = MOVE**. In the MOVE phase, per tick:

```
fullArc = (rootmotion(9) + rootmotion(10) + rootmotion(11) + rootmotion(12)) * 0.25 // DAT_00737058
advance = 0.1 * fullArc // FIXED per-tick fraction, RESET to 0.1 every tick (00419c60:46) — NOT a carried residual
cur = body // re-seeded from the body each tick (00419c60:39-41)
while advance > 0 and cursor < pts.Count:
    seg = sqrt( dx² + dy² + (dz*0.5)² )       // depth (z) weighted ×0.5 (DAT_0073708c)
    if advance < seg: cur = lerp(cur, pts[cursor], advance/seg); advance = 0; break
    advance -= seg; cur = pts[cursor]; cursor++
body = cur // committed via the collision sweep FUN_00419840
```

- `rootmotion(clip) = FUN_00423270(clip)` reads `gaitBase + 4 + clip*0x14`'s `[+8]` field (the clip's
  baked per-frame root-motion scalar), with fallback to the base bank `obj+0x5b8`. **Speed is ENTIRELY the
  clip's baked root motion × 0.25 — there is NO hand-tuned speed constant.** Walk vs run differs only
  because `laufen_schnell_loop` has ~2.11× the root translation of `gehen_loop`. [walkfloor §3.2, animsets §5.1]
- **CRITICAL (corrected in walkfloor Verification notes):** the per-tick advance is a **fixed `0.1·fullArc`**.
  The `obj[+0x3c]` "leftover fraction" write at `00418db0:149` is **dead state** — overwritten by the
  unconditional reset at `00419c60:46` before the next tick reads it. **Do NOT carry a frame-to-frame
  residual.** (animsets §5.2's "phase carry" wording is superseded by this correction.)
- This is **FRAME-COUPLED**, not time-based: at 10 fps the per-tick advance = `0.1 × 0.25 × Σ clip root motion`.

### 3.4 Per-node floor-height following `FUN_0041aac0` + `FUN_00424470`

Each densified node carries its OWN sampled depth: `h = FUN_00424470(x, y)` reads the height nibble at
the **SUPPORT cell `(x, y+2)`** (sub-cell `(x>>3, (y+2)>>3)`, sub-idx `((y+2)&7)<<3 | (x&7)`), then
`node z = h - {0.5 | 1.0}` (the 0.5/1.0 bias chosen by the sign of ΔX between adjacent points). The
integrator (§3.3) lerps z between neighbours by arc length, weighted ×0.5. **Never straight-lerp z from
start→goal.** The port already has the sampler (`NavWorld.GetHmapSub`) but does not call it per node with
the `(y+2)` support offset. [walkfloor §4, entity-anchor §3.3]

### 3.5 Directional clip selection `FUN_00418c70` (the 4-sector rule)

Heading between previous and current node, over the **full Δ vector** (x@0, y@+4, z@+8), via atan2,
wrapped to [0,2π), scaled `×4.0/2π` into 4 sectors:
`[0,1)→clip 0xb`, `[1,2)→clip 0xa`, `[2,3)→clip 0xc`, `[3,4)→clip 9`; cursor≥count ⇒ clip 1 (idle/settle).
**Corrected (walkfloor V.notes):** the heading is the full Δ vector (not Δz alone), and `DAT_007370d0=π/4`
is **NOT used here** (it belongs to the climb-step `FUN_00418850`). [walkfloor §5.2]

### 3.6 Gait + arrival + walk-events

- **Gait `FUN_004234a0`:** `owner[+0x798] = 1` (run) if `owner[+0x7e8]==2`, else `0` (walk). The gait
  index selects the `0x1e4`-byte motion bank `FUN_00423270` reads. [walkfloor §5.3]
- **Arrival = arc-length cursor exhaustion**, NOT a positional epsilon. (The 0.09=0.3² eps is for
  climb/standoff siblings.) The dig-standoff hook fires when `sqrt(depth-weighted d²) < 5.0`
  (`DAT_007374cc`, compared against the sqrt, so it's `dist<5.0`). [walkfloor §6.3]
- **Walk-events** fire at owner range `×0.65` (`_DAT_007370a8`) for **walk nodes** (waypoint flag==0,
  not gait), with z weighted ×0.5. [walkfloor §6.1, Verification notes]

---

## 4. Climbing — route builder + executor + clips

A WALL segment is built by `CAct_WalkFloor`'s segment builder into a **FloorPathMap** (the route), then
executed by `CAct_Climb` → per-leg `CAct_WalkAnim` (which chooses the climb clip per leg by geometry).
[climb-route §0, climb-exec §0]

> **ATTRIBUTION CORRECTION (, ground-truth probe `tools/probe_climb_band.py` on the live
> dump):** the FloorPathMap grid (§4.1) is **NOT the climb route**. Its per-column legal band
> `[h(sy−1)·4+1, h(sy+1)·4−1)` is **EMPTY on every mid-wall cell of a real cliff** (h is constant
> along a wall face — e.g. 11/11/11 → band `[45,43)` — so every column of a sheer wall is fully
> masked and the flood can never descend it; probed on the live cliff x=2059, y 242..272). The bands
> are non-empty exactly ON floor lines (hA < hB), i.e. **FloorPathMap resolves WHERE IN DEPTH the
> dwarf walks along a FLOOR segment** (col = arc index along the floor polyline, row = depth·4) —
> the lip-snap and the staircase columns in the probe confirm it. The climb route proper is §4.2:
> `CAct_Climb`'s record mirror of the Calc1 sub-cell polyline at `FUN_004238e0` support depths —
> no flood, no densify. climb-route.md §7's "vertical shaft descent" walkthrough is the RE doc's
> misread (its own §3 band formula contradicts it on real wall data). Also corrected from the
> decompile: a SOLID tile (gate byte +6 == −1) leaves the defaults `floorH=1000/ceilH=0` → the
> column **fully masked**, not "left fully open" (`FUN_0041c4b0:155`).

### 4.1 The FloorPathMap route builder `FUN_0041aac0`/`FUN_0041d8a0`

Grid is **column-major (stride 0x40=64)**: **column = densified arc-length index along the segment
centreline; row = `round(world.z·4)` = the depth axis.** Pipeline:

1. **Densify `FUN_0041bff0`:** one point per 1.0 world unit along the segment polyline; each point's
   `(x,y) = lerp·0.125`, `z = 15.0` (full slab); grid = `uint32[n·64]`. [climb-route §2]
2. **Stamp blocked bands `FUN_0041c4b0`:** per column the legal climbable band is
   `[floorH·4+1, ceilH·4-1)`; below → `0x10000000` (too-low), above → `0x08000000` (too-high). A
   height step `> 2` between vertically adjacent cells snaps the point onto the cliff lip. [climb-route §3]
3. **Goal depth derivation:** `h = FUN_00424470(goal sub-cell, +2 row bias)`; final-segment
   `dx < 0 → goal.z = h − 1.0`, `dx ≥ 0 → goal.z = h − 0.5` (which side of the wall you approach from).
   [climb-route §6]
4. **Seed/goal cells:** seed = nearest point to GOAL `(col, round(goalZ·4))`; backtrace origin = nearest
   point to START `(col, round(startZ·4))`. Flood runs goal→start, backtrace start→goal. [climb-route §4.1]
5. **Straight-LOS short-circuit:** march the line seed→goal (TRUNCATED lerp idx `col·0x40 + row`); if every
   cell is passable (`(cell & 0xff000000)==0`) emit a 2- or 3-point straight path, no flood. [climb-route §4.2]
6. **Else 5-pass weighted Dijkstra FIFO flood** with the EXACT cost table `{1280,512,256,1280,1024}` =
   `{5,2,1,5,4}×256` over **20 offsets** (pass 2 = cardinals cheapest 256; pass 1 = diagonals 512;
   pass 4 = 2-step cardinals 1024; passes 0+3 = knight moves 1280), frontier grows `+0x1000`. [climb-route §4.3]
7. **Steepest-cost backtrace** over the same 20 neighbours (move to the max-cost neighbour), emitting
   `{x=pts[col].x, y=pts[col].y, z=row·0.25, flag=2}` waypoints. [climb-route §4.4]
8. **Mount/dismount free-cell snap:** mount `FUN_0041bbd0` = **±16 horizontal**; dismount `FUN_0041bde0` =
   **±256 row in the single column `col−1`** (corrected: not ±1 col). Free = `(cell&0xff000000)==0 &&
   (cell&0xffffff)==0`. [climb-route §5, Verification notes]
9. **Floor settle (flood path only):** for interior waypoints, while the `+0x3c` sub-cell has bit 2
   (floor), `wp.y += 0.125` (slide deeper) until off the floor line. **Corrected:** this runs on the
   FLOOD/backtrace path, NOT the LOS short-circuit. [climb-route §4.5, Verification notes]

Net: a clean shaft = a 2-point straight descent + floor settle; a non-trivial shaft = a cardinal-cheapest
Dijkstra column of flag-2 waypoints. Either way the route ends ON the floor line. [climb-route §7]

### 4.2 The climb executor `CAct_Climb` (`FUN_00415e10`/`FUN_0040bb70`/`FUN_0040bf80`)

- **Record build `FUN_0040bb70`:** mirror the segment's sub-cell waypoints into **0x10-byte records**
  `{float x = sub·0.125, float y = sub·0.125, float z = FUN_004238e0 support depth, int flag = 0}`.
  **CORRECTED (climb-exec V.notes):** the ctor is called as `FUN_0040bb70(this=CAct_Climb, param4=path_ptr)`,
  so the `param_2==2` z-copy branch (copy `[n-2].z → [n].z`) is **DEAD on the climb path** — do NOT copy it.
  **Every climb record has flag 0.** [climb-exec §1.1-§1.3]
- **`FUN_004238e0`** = per-sub-cell support depth (nibble one row up, `subY-1`, with an overhang fix when
  it reads 15/undug → pick the lower neighbour). This is the depth each rung sits at. [climb-exec §1.4]
- **Tick `FUN_0040bf80`:** if `owner[+0x7b0]==0` (-canclimb off) → **state 3 (WS_NOPASSAGE)** immediately
  (box carriers walk `-canclimb 0`); else spawn a `CAct_WalkAnim` toward `record[idx]` (arrival eps²
  **0.09 = 0.3²**); on the leg reporting state 4, advance to the next leg; done when no record left.
  [climb-exec §2]

### 4.3 The per-leg mover `CAct_WalkAnim::tick FUN_00419c60` + clip selection

> **NEW STRUCTURAL FINDING (climb-exec V.notes):** because every climb record has flag 0, the dwarf climb
> actually executes through the **flag-0 step `FUN_00418db0`** (the same floor-style integrator as §3.3),
> NOT the flag-2 `FUN_00418850`. `FUN_00418db0` itself runs the `movable[+0x234]` mount/climb/dismount
> phase machine and calls the quadrant clip selector `FUN_00418c70`. So floor and climb share one mover;
> the port should drive climb legs through the flag-0 path, not a separate "wall step."

- **Speed = root motion** (`Σ clip 9..12 × 0.25`), depth ×0.5; no fixed climb speed, no 0.47 factor.
- **Clip chosen per leg by geometry** via `FUN_00418c70`: heading in the (lateral-X, depth) plane →
  4 quadrants → animset slot **9 climb_standard, 10 climb_up, 11 climb_down, 12 climb_right**, with
  **13 climb_left** on the lateral branch; mount = slot **16 ground_to_wall**, dismount = slot **17
  wall_to_ground**, held for the clip's frame count by the 3-phase `movable[+0x234]` machine
  (2=mount, 0=climb, 1=dismount). [climb-exec §3]
- **Slot→clip** resolves through the per-gait animset bank `obj+8 + gait*0x1e4 + slot*0x14` (gait from
  `FUN_004234a0`), `-1` falls back to `obj+0x5b8`. The clip NAMES (`kletter_hoch_loop`,
  `stand_zu_kletterstand`, …) are authored in `set_class_animset` (`z_anims.tcl:1280+`). Full slot table
  in §8.2. [climb-exec §4, animsets §2.1]
- **Wall→floor handoff** is implicit: the last climb record's z is already the floor support depth, the
  dismount clip plays, the WalkAnim writes state 4, and MetaWalk re-classifies the next segment as FLOOR.
  [climb-exec §5]

---

## 5. Digging — Tcl chain + natives + carve + EFFPASSWALK revalidation

### 5.1 The Tcl dig loop `z_dignwalk.tcl`

```
evt_task_dig / evt_autoprod_dig → dig_starttask {pos}
  ├ get_digedge $pos this → nearest reachable floor edge to stand at (sentinel {-1 -1 14} on fail)
  ├ vector_fixdig $edge [get_pos] → reachable standoff (1 unit off the wall, snapped to walkable)
  ├ lrep walkpos 2 $originalz → THE originalz patch: restore the dig depth into slot 2
  └ tasklist_add "walk_pos_dig {walkpos} {edge}"; "dig_continue"
dig_continue {try}: (runs while sparetime==0 AND current_worktask=="dig")
  ├ digpoint = dig_next $cpos this $try + {0 0 -1} (next voxel)
  ├ dig_check $digpoint (≥2 of 3 cross-neighbours have get_hmap < 13)
  ├ (Kristallstrahl re-aim if laser)
  ├ nonewwalk radii: Spitzhacke 0.5 / Presslufthammer 1.1 / Kristallstrahl 10.0
  ├ if walkdist > nonewwalk: queue walk_pos_dig (reposition) + walkfail_tasks retry ladder
  ├ change_tool (Spitzhacke/Presslufthammer/Kristallstrahl)
  └ tasklist_add "dig_anim {digpoint}" → anim finish-callback runs dig_execute; dig_continue
dig_anim {digpos}: (pose/tool anim, branches on get_gnomeposition floor/wall + tool)
dig_execute {digpoint}: (inside the anim finish-callback)
  └ while in-reach: if rand < digcount*get_material*0.7 (or laser): dig_apply $digpoint this (THE carve)
                    grant tttgain_dig exp + chance-spawn ore; step to next voxel
```
The carve is **paced by animation length** (`animcnt` from `exp_Stein`). [tcl-dig-complete §2]

### 5.2 The dig natives (contract)

| native | worker | returns |
|---|---|---|
| `get_digedge $pos $obj` | `FUN_00635180`→`FUN_005981d0` | `{X Y 14.0}` nearest reachable floor edge by the rock; **Z hard-coded 14.0** (the floor line); `{-1 -1 14}` on fail (validity `vector_unpacky>0`). Side effect: `tile[+0x08] |= 4<<(owner*4)`. **The search body is Ghidra-mangled (~70 blocks gone)** — BFS-via-FastArray confirmed, nearest-to-dwarf selection is INFERENCE. [dig-edge] |
| `dig_next $pos $g [$n] [$id]` | `FUN_00597b20` | n-th nearest dig-marked, precondition-passing node; window x±16 / y(−17..+15); gate `tile+8 & (0xB<<owner*4)` + `+0x18&3 ∈ {1,2}` + valid rim; cost `dx² + (dy)² + dz²·0.25` with anchor offsets `dy=subY−(gy−0.75)`, `dz=(rim+4)−(gz+6.0)`, `+8.0` non-id penalty; **output Z = clamp(rim+4+2.0, 1.0, 14.0) = clamp(rim+6,1,14)** (NOT rim+4). [dig-natives §2] |
| `vector_fixdig $edge $g` | `FUN_00594e00` | step `±(target−p)/d2` toward the dwarf (gated d2≥1), spiral-snap to nearest walkable (`+0x3c&1`), +y climb-band nudge to a floor cell, `z = supportNibble(subY−5)+1` clamped **≤14.5**. [dig-natives §3] |
| `vector_fix $pos` | `FUN_00595770` | plain landscape snap (no step-off-wall): nearest walkable sub-cell, z=`min(supportNibble(subY−1)+1, 14.5)`; **pass-through if OOB** (0x30=48 margin). [dig-natives §4] |
| `get_gnomeposition $obj` | `FUN_0068d8a0` | **0 = floor, 1 = wall** (probe `+0x3c & 2` at rows subY+1/subY/subY−1; OOB→1). [dig-natives §5] |
| `dig_apply $pos $g` | `FUN_006347a0`→`FUN_00596b80` | bool (1 = a node changed this step); reads owner `+0x36c`; one BDB rim step per call. [dig-natives §6] |
| `dig_mark $owner $x $y $mode [$cnt]` | `FUN_006586c0` | mode 0 paint (clamp cnt [0,3]) / 1 paint-dummy / 2 clear (`tile+8 &= ~(0xf<<owner*4)`). [dig-natives §7] |
| `get_hmap $x $y` | `FUN_004093f0` | per-sub-cell depth nibble 0..15; **15 if OOB**. |
| `get_material $pos` | UNRESOLVED native | small float/int hardness (rock=0/3); swing-probability only. |
| `is_dig_marked x1 y1 x2 y2` | DigMark.cpp | bool any-marked in rect. |

### 5.3 The carve `FUN_00596b80` (block level)

1. Precondition gate (bounds +1 margin + `FUN_005976b0` exposed test).
2. Decode the dig-state nibble `tile+0x08 >> (owner*4)` → an open-depth **threshold ∈ {0,3,7,11,15}**.
3. Rim gate: `rim = FUN_005968b0` (max of 4 neighbour corner gate bytes, follow transitions, −1 if
   self-contained); proceed only if `threshold ≤ rim`.
4. Re-resolve `block_idx` for the centre + 4 corner neighbours via the aux corner-transition graph
   `aux[0x40..0x4c]`, accepted iff the candidate's gate byte `aux[0x50..0x53] == rim`; write back; set
   `tile+0x13 |= 1`.
5. Clear carved-through layers; set re-render dirty `tile+0x13 |= 2`; write the `get_gnomeposition`
   source `gnome+0x250 = (tile+0x0c) >> shift`; **enqueue the dirty rect via `FUN_005ad040`** (the carve
   does NOT itself rewrite `+0x3c`). [carve-validator §1]

### 5.4 EFFPASSWALK — the `+0x3c` re-derivation `FUN_005a6b40` ★ CORNERSTONE

The MapValidator `FUN_005a46d0` runs the dirty-rect stages in a fixed order; **EFFPASSWALK
(`FUN_005a6b40`) runs BEFORE EFFBLOCK (`FUN_005a6350`).** EFFPASSWALK re-derives the 8×8 `+0x3c`
passability for every sub-cell in the rect purely from the heightmap (`get_hmap`). Per sub-cell:

1. **Precondition:** sample the tile at `((sx+4)>>3, (sy+4)>>3)`; if its `+0x18` high bit (0x80,
   "fully enclosed") is set → emit **0**.
2. **Open-band test:** ALL **31** relative cells (drow ≤ 0, a ±7 column fan narrowing upward, out to
   15 rows above) must have `get_hmap < 0xE (14)` (open). If any is ≥ 14 → emit **0** (buried).
   (carve-validator corrected the band from 30→31 cells.)
3. **Floor-vs-wall step:** default **1** (climbable wall); promote to **3** (walk|floor ground line) iff a
   `> 2`-nibble depth step crosses the y-boundary:
   - PRIMARY: `h(sy-1)+2 < h(sy)` AND `h(sy-11)+2 < h(sy)` → 3 (first solid row under the pocket)
   - ALTERNATE: if `h(sy)+2 < h(sy+1)` → `(h(sy-10)+2 < h(sy+1)) ? 3: 1`
   - else → 1.
4. `FUN_00596550` copies the 0/1/3 window back into `tile+0x3c` (the only `+0x3c` writer).

**VERIFIED against the live minidump: 1672/1672 = 100%** of value-3 ground-line cells reproduced.
[carve-validator §2, §3, Verification notes]

**The dig contract the port must honour:** (1) lower the geometry/heightmap, (2) mark the rect dirty,
(3) re-run EFFPASSWALK (rebuild `+0x3c` from the NEW heightmap) THEN EFFBLOCK (rebuild `+0x06`/`+0x12`
from `+0x3c`). Skipping (3) leaves stale walkability — the gnome cannot path onto the dug floor.
[carve-validator §5]

---

## 6. The work layer — dispatcher → events → tasklists → states

### 6.1 Two queues, one tick

A dwarf is a per-object Tcl interp running a **state machine** + a **tasklist FIFO** (command strings).
The engine ticks the current-state BODY once per `SrvDoThink` **only while state-enabled** (enable-count).
Long sub-actions (`action … walk/anim/wait`) **`state_disable`** and re-enable from a finish-callback —
the state pauses while the dwarf physically acts, then resumes (walk→act→repeat). [tcl-work-layer §1]

- **`current_worklist`** = high-level tasks (each pushes a sub-chain onto the tasklist).
- **tasklist** = the FIFO of sub-action command strings; dispatching states drain ONE entry per tick:
  `set c [tasklist_get this 0]; tasklist_rem this 0; eval $c`.

### 6.2 The states

| state | role |
|---|---|
| `idle` | home; polls `get_remaining_sparetime`: ==0 → `work_dispatch`, >0 → `sparetime_dispatch` |
| `task` | user one-shot driver; drains tasklist, falls back to `idle` when empty |
| `work_auto` | autonomy router: switches on `current_worktask`, pushes a `prod_auto*`/`dig_starttask` task |
| `work_dispatch` | the worklist engine: drains tasklist first, else evaluates `current_worklist[0]`; on success pop + `prod_progress`; on fail 5 retries with a 2s wait then `work_idle` |
| `work_idle` | "assigned, nothing to do" parking; offers the gnome to the dispatcher (`gnome_idle 1`); drifts to sparetime/strike/prodfill |
| `work_active`/`work_breakable` | thin; switch to sparetime when `current_plan=="sparetime"` |
| `trapped`/`mad_scientist` | pre-empt / inventor idle |
[tcl-work-layer §2]

### 6.3 Event entry

- **User:** GUI raises `evt_task_<X>` → handler does `gnome_failed_work; tasklist_clear; kill_all_ghosts;
  stop_prod; notify_userevent`, queues the task, `state_triggerfresh task|work_dispatch`.
- **Autonomy:** the native CProdPlanner raises `evt_autoprod_<X>` → handler does `tasklist_clear; stop_prod;
  notify_autoevent`, sets `current_worktask`, `state_triggerfresh work_auto|work_dispatch`.
- **`state_triggerfresh`** resets the enable-count so a half-disabled prior action can't freeze the new
  task — this is how a click instantly pre-empts whatever the dwarf was doing. [tcl-work-layer §4, §8]

### 6.4 The native dispatcher (CProdPlanner / WorkScheduler)

Two layers in one `SrvDoThink`: **(A)** `CProdPlanner::MaintenanceTick` (dig/harvest/produce/research/
pickup) and **(B)** `FUN_005eed70` (transport: pack/unpack/bringprod/mine/…). Both format
`set_event %s evt_autoprod_* …` strings (`FUN_0043a8a0` / `FUN_005f3a50`) and queue them into the gnome's
Tcl interp; the unchanged handlers run. [dispatcher-reconcile §1, §3]

- **Player amortization:** one player per qualifying tick via `DAT_00c3c968 = (cursor+1) & 0x80000007`
  (≈ `&7`). Layer B runs all 8 players every fire (a port amortization gap). [dispatcher-reconcile §2]
- **Gnome match cost `FUN_004317a0`:** `dx² + (dy·wY)² + (dz·wZ)²` (3-D, weighted; dig/depth axis ×~0.5)
  over a reachability-filtered floor-cell scan, rejected when `cost > radius²`
  (`radius² = need²·K + base`). **The port's `dx²+dy²` (horizontal-only) is wrong.** [dispatcher-reconcile §4]
- **Dig event payload** is **3 world floats** `{%f %f %f}`, not 2 tile ints. [dispatcher-reconcile §3]
- **The loop closes** on `get_walkresult == 4` before the dig/harvest task carves. [dispatcher-reconcile §5]

### 6.5 Support procs

`gnome_failed_work` (release reservation), `note_pathlength` (placelock the destination for 2×dist),
`walkfail` (the universal finish/fail gate: `if get_walkresult != 4` → retry `walkfail_tasks` or give up
to idle), `act_when_idle` (collision-avoidance shuffle + wander), `stop_prod` (teardown, called AFTER
tasklist_clear), `prod_gnome_state` (engine work-disposition flag, presentation). [tcl-work-layer §6]

---

## 7. Sparetime + idle

### 7.1 The worktime clock (native)

`gettime` = seconds; `gethours = gettime / DAT_00737630` (seconds-per-logic-hour). The HUD clock is
**12 hours, one hand, NO day/night**: clock-hour = 240 logic-hours (`0xf0`), full revolution = 2880
(`0xb40`). Per-gnome work window via `set_worktime`/`get_worktime`/`auto_choose_workingtime` (parity
stagger so the colony doesn't all rest at once). Observed shifts: start 0.0/6.0, duration **6.0**.
[sparetime-idle §2]

`get_remaining_sparetime` (`FUN_006955d0`, Ghidra-mangled): `==0.0` ⇒ worktime (→ work_dispatch),
`>0.0` ⇒ sparetime (→ sparetime_dispatch). **INFERENCE:** `remaining = (dayPhase ∈ window) ? 0:
hour-distance to next window start`, `dayPhase = gethours % 12`. The native formula lives in the
un-decompiled `Sparetime.cpp`. [sparetime-idle §2.4]

### 7.2 The idle loop (`zwerg.tcl` `state idle` + `work_idle`)

Each idle tick: `incr idletimeout`; vacate a sparetime seat; `act_when_idle` (wander/avoid); poll
`get_remaining_sparetime` (→ work or sparetime); `set_idle_anim`; self-pace via
`state_disable; action this wait 1 {state_enable}`. `work_idle` adds `prod_gnome_state idle`, the
`prodfill_dispatch` idle showcase (25% at idletimeout>10), strike, and the jobless-hat swap.
[sparetime-idle §3]

`act_when_idle`: on a wall, step aside if another gnome overlaps; on the floor, `walk_random [irandom 2 4]`
if standing on someone's placelock or overlapped, else pop one `idle_action_list` entry. `walk_random`
finds a `get_place -center … -circle plength -mindist 0.7 -walldist 1` target. `set_idle_anim` cycles 4
floor stand variants (`mann.stand_anim_a..d`) or the climb-stand on a wall (facing π). [sparetime-idle §3.3-3.5]

### 7.3 Sparetime dispatch

Entered from idle when `remaining>0`. Builds a plan (`sparetime_future` of eat/slp/fun/bth scaled by
remaining hours), arbitrates `current_wish_occupation` from attributes, runs start/loop/end of the chosen
occupation; always hands back to `work_dispatch` (never directly to idle). The deep needs economy
(~5000 lines of `z_spare_*.tcl`) is gated entirely behind `get_remaining_sparetime>0`. [sparetime-idle §4]

---

## 8. Animation coupling

### 8.1 The three class-def tables + two runtime structures

- `class_defaultanim` (spawn idle), `set_class_anim` (state→clip-name map, 1000+ entries, the dig/turn/
  work clips), `set_class_animset` (id→24-slot gait table, the walk/run/swim/box gaits). [animsets §1]
- **Class animset table** (load-time): per-id 0x100-byte row of 32 slots indexed by a name hash
  `Σ(h*9+c) & 0x1f`. **Per-object gait block** (runtime): `obj+8`, 4 gaits × 0x1e4 bytes, each 24 slots ×
  0x14 bytes (`+0x04` anim idx or −1, `+0x10` frame count, root-motion scalars). Unset slot (`+4==-1`)
  falls back to the base bank `obj+0x5b8` = STANDARDWALK(0). [animsets §1, §2]
- `-animsets {a b}`: a = walk gait id, b = run gait id; `speedtype 2 ⇒ gait 1 (run)` via `FUN_004234a0`.

### 8.2 The 24 slots (canonical order; clip names for the dwarf, gait 0 / gait 1)

| idx | slot | walk clip | run clip |
|---|---|---|---|
| 0 | standard | `mann.standard` | `mann.standard` |
| 1 | walk_start | `mann.gehen_start` | `mann.laufen_schnell_start` |
| 2 | walk_loop | `mann.gehen_loop` | `mann.laufen_schnell_loop` |
| 3 | walk_loop_wave | `mann.gehen_gruessen` | — |
| 4 | walk_stop | `mann.gehen_end` | `mann.laufen_schnell_end` |
| 5 | turn_left_90 | `mann.drehen_links` | — |
| 6 | turn_right_90 | `mann.drehen_rechts` | — |
| 7 | turn_left_180 | `mann.drehen_ganz_links` | — |
| 8 | turn_right_180 | `mann.drehen_ganz` | — |
| 9 | climb_standard | `mann.kletterstand` | `mann.kletterstand` |
| 10 | climb_up | `mann.kletter_hoch_loop` | `mann.kletter_hoch_schnell` |
| 11 | climb_down | `mann.kletter_runter_loop` | `mann.kletter_runter_schnell` |
| 12 | climb_right | `mann.kletter_rechts_loop` | `mann.kletter_rechts_schnell` |
| 13 | climb_left | `mann.kletter_links_loop` | `mann.kletter_links_schnell` |
| 14 | ladder_climb_up | `mann.leiter_hoch_loop` | — |
| 15 | ladder_climb_down | `mann.leiter_runter_loop` | — |
| 16 | ground_to_wall (MOUNT) | `mann.stand_zu_kletterstand` | — |
| 17 | wall_to_ground (DISMOUNT) | `mann.kletterstand_zu_stand` | — |
| 18 | ladder_to_ground | `mann.leiter_zu_boden` | — |

[animsets §2.1, climb-exec §4]

### 8.3 The 10 fps clock + root-motion coupling (THE speed answer)

- Clips play as **whole-mesh swaps at 10 FPS** (`DAT_007373f0=10.0`, `+0.05` bias), STILL/ONCE/LOOP; the
  engine NEVER auto-chains start→loop→stop (the caller schedules it via the slot triad). [animsets §4]
- Each `.3db` clip carries a `vec3 motionOffset` (per-loop translation): `gehen_loop=(0,−16.22,0)`,
  `laufen_schnell_loop=(0,−34.29,0)` (≈2.11×). [animsets §4.1]
- **World displacement = `Σ(clip root-motion 9..12) × 0.25`, consumed as arc length along the floor
  polyline**, advanced a fixed `0.1·fullArc` per tick (§3.3). Walk vs run differs ONLY because the clips
  carry different baked translation — **no hand-tuned speed constant.** The dig clips come from the
  `set_class_anim` map (`buddeln_*`/`kletter_hack_*`), not the gait animset. [animsets §5, §8]

---

## 9. Entity anchor / position semantics

- **Anchor = feet on the floor line** (CObj `+0x0c/+0x10/+0x14` = X/Y/Z). The live dump proves an alive
  gnome's anchor sits EXACTLY on the `+0x3c` floor sub-cell row — there is **NO +1.0 body-centre lift** in
  the stored anchor (the old "~1.0 above feet" note was wrong; the y≈29 cluster is carried/neutral objects
  with a real carrier slot, not gnomes raised 1.0). [entity-anchor §1]
- **Carrier-height offset:** `get_pos` adds `carrierRec[+0x54]*0.005` to Y when mounted; `set_posbottom`
  subtracts it (inverse pair). **Identity for free-standing gnomes** — only bites mounted/linked entities.
  [entity-anchor §2]
- **Single per-tick writer `FUN_005cd8e0`:** every position change funnels here; it stores the anchor and
  re-buckets the entity in the CMap (X,Y) spatial hash (Z/depth excluded). **There is no separate physics
  step** — the action integrates pos directly. The walk mover has a root-motion mode
  (`newPos = start + rm*0.2 + head*0.8`) and a velocity mode (damped accumulator). [entity-anchor §3]
- **Gravity** is a SEPARATE action `FUN_0040f440` that adds a fall delta to **Y only** (proving +y=down),
  gated by a support test `FUN_004097b0` (compares the entity Z vs the per-cell depth byte). No continuous
  gravity integrator. [entity-anchor §4]
- **Facing:** rotY at `+0x44` (radians CCW); set via `FUN_005cd840` (snaps |comp|≥10000 to 0). `rotate_to*`
  are per-class Tcl procs wrapping `action this rotate <angle>` (front=0, left=π/2, back=π, right=3π/2).
  `get_anglexz(p1,p2) = atan2(p1.x−p2.x, p2.z−p1.z)` (X-Z plane, dx negated; empirically verified, exact
  disasm not recovered). The port's `-0.5*cos` facing math is the verified consequence of the `z*0.5`
  height scale — **do NOT "simplify" `-0.5*cos` to `-cos`.** [entity-anchor §5]

---

## 10. CONSOLIDATED PORT GAP ANALYSIS

Severity: **C**=critical (blocks faithful behaviour), **H**=high, **M**=medium, **L**=low.
Port roots: `Assets/Diggles/Runtime/...`. Sources cited as `[area-key]`.

| # | Subsystem | Original behaviour | Our state (port) | file:line | Sev |
|---|---|---|---|---|---|
| 1 | **Dig walkability** (EFFPASSWALK) | `+0x3c` re-derived from the heightmap after every dig, BEFORE EFFBLOCK | `NavWorld._sub` is the static PMP load, never re-derived; dug floor never walkable | `NavWorld.cs:54-135`; `DiggableTerrain.cs:71-73` | **C** [carve-validator G1] |
| 2 | **Climb route** | FloorPathMap: densify→stamp bands→5-pass Dijkstra `{1280,512,256,1280,1024}`→backtrace→settle | No FloorPathMap; `Build` hand-synthesises mount/crawl/dismount from 2 waypoints | `ClimbAction.cs:86-136` | **C** [climb-route G1] |
| 3 | **Climb axis/codes** | uniform flag-2 records, clip chosen per leg by heading quadrant; grid col=arc, row=depth | invented `Kind{Mount,CrawlLeft,…}`; axis comment claims +Y=up (project frame is +Y=down) | `ClimbAction.cs:19,28,89` | **H** [climb-route G2/G3, port-inventory #2] |
| 4 | **Movement speed** | `Σ clip root-motion 9..12 × 0.25`, consumed as arc length, fixed 0.1/tick fraction | `ModelHeight·BodySpeedFactor(0.5)·gaitRatio`; climb `×0.47`; `WalkClimbAction` adds a 1/1.3 fudge | `EntityActions.cs:137,421-429`; `ClimbAction.cs:264-274`; `WalkClimbAction.cs:184-191` | **H** [walkfloor G1, animsets G1, climb-exec §8.2] |
| 5 | **`tasklist_rem` by value** | removes by **INDEX** (`tasklist_rem this 0` = pop index 0) | `List.Remove("0")` removes by value → dispatch pop is a no-op, tasks re-eval forever | `DigglesTclEngine.cs:136` | **C** [tcl-work-layer G1] |
| 6 | **Calc1 cost variant** | campaign gnomes (iOwner≤7) use CLIFF **0x10a=266** + the owner gate `tile[0x34+iOwner]` | hardcodes CLIFF **0x114=276**, no owner gate, no iOwner input | `Calc1Pathfinder.cs:28-137` | **H** [calc1-confirm G1/G2] |
| 7 | **Per-node floor-Y follow** | each node z = support cell `(x,y+2)` nibble − {0.5\|1.0}; lerp z by arc, ×0.5 weight | straight-lerps Y to leg targets; treats Up=+Y | `WalkClimbAction.cs:239-253`; `MetaWalkAction.Advance` `EntityActions.cs:372-394` | **H** [walkfloor G3, entity-anchor §7] |
| 8 | **MetaWalk segmentation/result** | maximal same-surface grouping → WalkFloor/Climb/Water; only result 4 continues; full 5-state enum | single floor mover; binary WalkResult (2→4/3); no WALL→climb dispatch, no `-canclimb` gate to WS_NOPASSAGE | `EntityActions.cs:250-443,73-90`; `DigglesTclEngine.cs:2836-2867` | **H** [metawalk-core G1-G6] |
| 9 | **start/loop/stop framing** | 96-unit arc threshold + 32-unit sub-segments + phase machine; turn clip by 4-sector heading | bare `gehen_loop` + ad-hoc `_runDelay`; hardcoded clip names; no turn quantiser | `WalkClimbAction.cs:255-268`; `MetaWalkAction` | **M** [walkfloor G2/G5] |
| 10 | **`get_digedge` Z** | wrapper hard-codes **Z=14.0** (floor line); Tcl preserves as originalz; picks floor cell nearest the dwarf | returns `GetHmapSub` per-cell depth; ignores dwarf pos | `DigglesTclEngine.cs:800-815` | **H** [dig-edge G1/G2] |
| 11 | **`dig_next` Z + picker** | `clamp(rim+6, 1, 14)`; window x±16/y(−17..+15); 0.25 depth weight; anchor offsets 0.75/6.0; +8 id penalty | `clamp(rim+4,1,14)`; simple distance, no offsets/weight/penalty | `DigglesTclEngine.cs:816-824` | **M** [dig-natives G1/G2] |
| 12 | **`vector_fix` identity** | snap to nearest walkable sub-cell, z=`min(supportNibble+1,14.5)`, OOB pass-through | identity passthrough → un-snapped walk targets | `DigglesTclEngine.cs:947` | **M** [dig-natives G4] |
| 13 | **`dig_mark` arg mismap** | `owner,x,y,MODE,cnt` (mode 0/1/2) | reads `cnt=a[1]`(=owner!), keys clear off `a[5]==2`, ignores mode `a[4]` | `DigglesTclEngine.cs:839-846` | **M** [dig-natives G6] |
| 14 | **Dig resolution** | One `block_idx` per tile; shared-node BDB transition; `tile+0x08` stores eight owner targets | implemented with the same block/corner law and target-11 tunnel profile | `DiggableTerrain.cs` | **DONE** [ correction] |
| 15 | **Dispatcher match metric** | 3-D weighted `dx²+(dy·wY)²+(dz·wZ)²`, radius reject `cost>need²·K+base` | `dx²+dy²` horizontal-only, no radius reject | `ProdDispatcher.cs:316-320` | **M** [dispatcher-reconcile G1/G3] |
| 16 | **Dig event payload** | 3 world floats `{x y z}` | 2 tile ints `{tx ty}`, `Pos=(tx,ty,0)` tile-space | `ProdDispatcher.cs:230,290` | **M** [dispatcher-reconcile G2] |
| 17 | **walk-option parser** | 22 options incl. `-stopat`/`-targetdist`/`-walktimercallback`/`-randompath`/`-dummy` link | only 6 (`-target`,`-animsets`,`-speedtype`,`-canclimb`,`-object`,`-dummy`-noop); arrival hardcoded 0.2 | `DigglesTclEngine.cs:2777-2808`; `EntityActions.cs:275` | **M** [walk-lifecycle gaps] |
| 18 | **AutoRetry/re-plan** | retry adjacent cell on block; rebuild MetaWalk when gnome moves (chase moving target) | one path, no retry, no mid-walk replan | `DigglesTclEngine.cs:2828` | **M** [walk-lifecycle 5/6] |
| 19 | **State-think gate** | engine does NOT gate state-think on the active action (states self-pace via state_disable) | skips state-think while an action runs unless tasklist queued (band-aid) | `DigglesGameSim.cs:138-150` | **M** [tcl-work-layer G2] |
| 20 | **`get_place` stub** | real PlaceFinder (circle/random/walldist) | hardcoded `"-1 0 0"` → idle gnomes never wander/avoid | `DigglesTclEngine.cs:950` | **M** [sparetime-idle G1] |
| 21 | **worktime ratio** | shift duration ~6.0 (6:6) | `WorkLen=9` (9:3) → gnomes rest far less | `DigglesTclEntity.cs:117`; `DigglesTclEngine.cs:571` | **M** [sparetime-idle G4] |
| 22 | **`prod_gnome_state`** stub | engine work-disposition flag (`gnome+0xf9+1`) | bulk-stubbed "0" | `DigglesTclEngine.cs:2186` | **M** [sparetime-idle G2, tcl-work-layer 4] |
| 23 | **clock edge events** | `evt_time_startwork`/`startsparetime` toggle `current_plan` | not raised → `work_active/breakable` won't abandon a long task at shift end | (clock tick) | **M** [sparetime-idle G6] |
| 24 | **wall normal** | derived from geometry | hardcoded world −Z | `NavWorldService.cs:161` | **M** [port-inventory #4] |
| 25 | **vector_fixdig** | `±(target−p)/d2` step, +y floor scan, subY−5 support, z≤14.5 | unit-normalize step, no +y scan, z≤14 | `DigglesTclEngine.cs:857-878` | **L** [dig-natives G3] |
| 26 | **Calc1 endpoint relax** | start 4-axis r≤32, goal 8-probe +y-biased fan r≤12, +y overhang nudge | symmetric ring, no overhang nudge; budget clamp on pops +8 (should be enqueued ×1.5) | `Calc1Pathfinder.cs:141-165,22` | **L** [calc1-confirm G5-G9] |
| 27 | **carrier-height / set_posbottom** | get_pos +carrierH, set_posbottom −carrierH (inverse pair) | both identity (raw) | `DigglesTclEngine.cs:330-334,935-945` | **L** [entity-anchor §2] |
| 28 | **Break → WS_WALKABORTED** | sets WalkResult=1 on interrupt | fires breakcode but doesn't set 1 | `DigglesTclEngine.cs:2629-2634` | **L** [walk-lifecycle 7] |
| 29 | **rotation NaN guard** | snap |comp|≥10000 to 0 | stores raw | `DigglesTclEngine.cs:340-351` | **L** [entity-anchor §5] |
| 30 | **dead legacy stack** | — | `WalkClimbAction`/`DigglesInputController` added then `Destroy`d | `DigglesWorldRuntime.cs:724-725` | **L** cleanup [port-inventory #12] |

**Already CORRECT / keep (do not regress):** `get_gnomeposition` (exact), `get_angle_num4` (exact),
`get_walkresult` {2,3,4} value set, the `-0.5*cos` facing math, anchor=feet placement (no +1.0 lift),
Calc1 FIFO Bellman-Ford + cost-v1 three-branch formula, `set_class_animset`/`set_class_anim` parsing, the
10fps morph clock, `gnome_idle`→ProdDispatcher wiring, the EFFBLOCK floor/solid common case.

---

## 11. Implementation plan (dependency order + verification per phase)

> Build bottom-up: the data layer first, then the leaf mover, then segmentation, then the world-mutation
> revalidation, then the decision layer fixes. Each phase has a concrete verification.

### Phase 0 — Quick correctness fixes (independent, do first)
- **P0.1** `tasklist_rem` → parse the arg as an int index and `RemoveAt` (gap #5). **Verify:** a
  headless work-loop test shows a dispatching state drains its tasklist instead of looping forever.
- **P0.2** Calc1 cost variant: add the iOwner input; campaign (iOwner≤7) uses CLIFF **266** + the owner
  gate `tile[0x34+iOwner]` (gaps #6). **Verify:** path cost on a cliff edge = `base+266−16·min(dig)`.
- **P0.3** `get_digedge` Z=14.0 + thread dwarf pos; `dig_next` Z=`clamp(rim+6,1,14)`; `vector_fix` real
  snap; `dig_mark` arg fix (gaps #10/#11/#12/#13). **Verify:** dig-edge standoff lands on the floor line;
  `dig_next` aims at the right depth.
*Rationale:* these are localized, unblock other phases, and need no architecture change.

### Phase 1 — Data layer: EFFPASSWALK port (gap #1, the cornerstone)
- Add `NavWorld.ReDeriveSubGrid(rect)` implementing §5.4: precondition `+0x18>>7` of the `(+4,+4)` tile;
  the 31-cell open-band `<14` test; the `>2`-step floor(3)/wall(1) split (PRIMARY `h(sy-1)`+`h(sy-11)`,
  ALTERNATE `h(sy)`/`h(sy-10)`). Then `DeriveTilesIn(rect)` (EFFBLOCK) in that order.
- **Verify:** replay the live-dump probe — `ReDeriveSubGrid` reproduces value-3 ground-line cells at 100%
  on `diggles_meadow.dmp`; an in-Play dig opens the shaft floor as walkable (a gnome can path onto it).

### Phase 2 — Leaf mover: root-motion integrator (gaps #4, #7, #9)
- Implement the `FUN_00418db0` loop: `advance = 0.1 · 0.25 · Σ(directional clip root motion)`, consumed
  as arc length along a per-node point list; depth axis ×0.5; per-node z from `GetHmapSub(x, y+2) − {0.5|1.0}`;
  arrival = cursor exhaustion. Add `FUN_0041a470` arc framing (96/32) + the start/loop/stop phase machine
  + the 4-sector directional clip quantiser (`FUN_00418c70`). Remove `BodySpeedFactor`/`GroundSpeedScale`/
  `ClimbFactor`. Calibrate the one `.3db motionOffset → world` scale constant (unresolved #3).
- **Verify:** a flat walk's foot-plant matches the morph clock (no skating) at 10 fps; run/walk speed ratio
  ≈ 2.11 with no per-mode constant; height follows the terrain per node, not a straight Y-lerp.

### Phase 3 — Segmentation: faithful MetaWalk (gap #8)
- Port `FUN_004163a0`: classify each Calc1 waypoint via the `+0x3c` bits, group maximal same-surface runs,
  dispatch WalkFloor (floor) / Climb (wall) / (water deferred); `-canclimb 0` WALL run ⇒ immediate
  WS_NOPASSAGE (3); only result 4 continues to the next segment; whole-cursor exhaustion writes 4. Expose
  the full 5-state WalkResult enum read mid-walk by the Tcl loop.
- **Verify:** a path crossing floor→wall→floor produces 3 sub-actions; a no-climb box carrier on a wall run
  reports `get_walkresult==3`; a normal arrival reports 4 only at the end.

### Phase 4 — Climb route + executor (gaps #2, #3) — *re-scoped per the §4 attribution correction*
- **FLOOR runs** → build the FloorPathMap (§4.1) as the DEPTH resolver: densify → stamp bands
  (`FUN_0041c4b0`) → seed/goal cells → LOS short-circuit or 5-pass Dijkstra `{1280,512,256,1280,1024}`
  over the 20 offsets → steepest-cost backtrace → mount ±16 / dismount ±256-col−1 snap → floor settle
  on the flood path; goal depth `h−0.5/−1.0` by approach dx (§4.1 step 3) at wall-entry boundaries.
- **WALL runs** → `CAct_Climb` records (§4.2): the Calc1 sub-cells themselves (x,y=sub·0.125,
  z=`FUN_004238e0` support depth), uniform/un-tagged, no z-copy. Executor (§4.3): 3-phase machine
  (held ground_to_wall mount → root-motion advance with the heading-quadrant climb states →
  held wall_to_ground dismount on the wall→floor handoff), `-canclimb` enforced at dispatch.
- Reuse: BDB heightmap (`BdbData.PerBlockAux`), the `+0x3c` sub-grid for the settle floor bit.
- **Verify:** an open ground line LOS-short-circuits (2/3-pt, no flood); a cliff descent's WALL records
  all sit at their `FUN_004238e0` support depth and the path ends on the bottom floor cell; the dwarf
  plays kletter_runter/hoch by quadrant and dismounts standing.
- **DONE** — `FloorPathMap.cs` + `NavWorldService` run-by-run assembly + `ClimbAction`
  rewrite; EditMode: `FloorPathMap_MeadowLine_LosShortCircuit`, `FindPath_CliffDescent_ClimbRecordsOnWallFace`.

### Phase 5 — Per-owner target carve (gap #14, corrected)
- Decode and clear the owner's nibble in `tile+0x08`; apply the rim-gated `gate==rim` BDB
  transition; mark the rect dirty and run EFFPASSWALK→EFFBLOCK.
- **DONE; premise corrected.** The alleged spatial layers are actually
  **8 OWNER nibbles** (owner = gnome+0x36c; `FUN_005671a0` writes `(cnt|8)<<owner·4`, mode 1 writes 3
  via `FUN_005672d0`, the nibble encodes the open-depth TARGET {0,3,7,11,15} the carve stops at).
  Implemented: `DiggableTerrain._digState/_digId` (tile+0x08/+0x0c) + `RimAtNode` (faithful
  `FUN_005968b0` 4-corner transition-follow + −1 sentinel, gap G5) + `CarveStep` (faithful
  `FUN_00596b80`: target decode → rim gate w/ clear-on-done → gate==rim corner accept → per-owner
  completion sweep → dig-id cache to gnome+0x250); dig_next gains the id +8 penalty / matchId /
  rim −1 gates; dig_mark paints faithfully; gnome_announce_dig no longer resurrects cleared marks.
  Carve geometry stays block-per-tile, which is also the original's resolution; in-tile detail
  comes from the selected block's three co-rendered BDB submeshes. The standard mode-1 target 11
  retains front rock and exposes its authored wall profile, so no separate roof renderer is missing.
  EditMode: `CarveStep_DigTargets_StopAndSelfClear` (full dig 4 steps → self-clear; target-11 dig
  stops after 1 step at rim 7).

### Phase 6 — Decision layer fidelity (gaps #15-#23)
- Dispatcher: 3-D weighted match metric + radius reject; 3-float dig event payload; transport amortization.
  Clock: shift duration 6.0; raise `evt_time_startwork`/`startsparetime` edges; `prod_gnome_state` real.
  Idle: real `get_place` PlaceFinder so idle gnomes wander/avoid. AutoRetry/re-plan; full walk-option parser.
- **Verify:** the right gnome (nearest in 3-D) takes a deep dig; idle gnomes mill and step aside; a gnome
  abandons a long task when its shift ends.
- **DONE** — rodata extracted (`.rdata` raw 0x313e00): match weights `DAT_007374cc`=**5.0**
  (vertical) / `DAT_00737058`=**0.25** (depth) → `Dist2 = dx²+(5dy)²+(0.25dz)²`; the need-radius gate is
  **dead code in the shipped exe** (`DAT_00736f78`=0.0 threshold always passes → radius:=FLT_MAX,
  00436d40:141-143) so no distance reject is ported (faithful). Dig jobs carry a real 3-float pos
  (tile + rim+6 depth) and emit `-pos1 {x y z}`. Clock: `DAT_00737630`=**150** extracted (native
  hour-unit; port cadence knob stays `ProdDispatcher.HourSec`); WorkLen default 6.0 + the
  baby.tcl:411-424 crowd-balancing `auto_choose_workingtime`; `get_remaining_sparetime` returns
  HOURS (was seconds — every sparetime plan read as ≥5 h); `get_worktime` end/nextstart/laststart/
  nextend/lastend; per-gnome `evt_time_startwork`/`startsparetime` edges raised by the dispatcher.
  `prod_gnome_state`/`prod_gnomeidle` de-stubbed (entity ProdState + the idle pool). `get_place`
  REAL (annulus sample on ground lines, walldist, placelock, -nearpos) — un-breaks walk_random /
  act_when_idle wandering. Walk lifecycle: `-walktimercallback`/`-walktimercounter` (non-"1" ⇒
  WS_WALKEVENT 5), `-targetdist` finish-early (5), light AutoRetry.
  NOT ported (noted): transport layer-B per-fire all-players amortization (single-player focus).

### Phase 7 — Cleanup (gaps #24-#30)
- Derive the wall normal; carrier-height inverse pair; rotation NaN guard; Break→WS_WALKABORTED; Calc1
  endpoint-relax shape + overhang nudge + budget clamp; delete the dead `WalkClimbAction`/
  `DigglesInputController` stack.
- **Verify:** regression suite green; no behaviour change in the common case.
- **DONE** — wall normal: the constant world −Z IS the derivation (every climbable face in
  the data model fronts the depth axis; no side-facing climb walls exist — comment finalized).
  Carrier-height pair: identity is the faithful FREE-entity contract (entity-anchor.md §2.3); linked
  entities resolve through the transform hierarchy — documented at get_posbottom. Rotation NaN/garbage
  guard (|comp| ≥ 10000 → 0, `FUN_005cd840`) in ReflectRotation. Action-replace now stamps
  WalkResult=1 (WS_WALKABORTED, `FUN_00407b30:12`) on an in-flight walk. The Calc1 endpoint-relax /
  overhang-nudge / budget-clamp items were already landed in P0 (this list predated it). Deleted:
  `WalkClimbAction.cs`, `DigglesInputController.cs` + their AddComponent sites (ClickToMoveService
  owns all live input); dig-id field consolidated (P6). Full suite: 149 passed / 3 pre-existing
  failures (cutscene audio window + set_anim prefix — present at the pre-P4 parent, tracked separately).

---

## 12. Consolidated unresolved list

Carried forward from the area specs (deduplicated). None block the campaign port; most are fidelity-tuning.

1. **rodata constant VALUES** for the dispatcher match weights and radius (`DAT_007374cc`, `DAT_00737058`,
   `DAT_00737680/00737578/00736f78/00737700`, `DAT_007372a8`) — referenced but not in the decompile text;
   need a `.rdata` dump or live read. The prompt says the dig/depth axis weight ≈ 0.5. [dispatcher-reconcile §10]
2. **`get_remaining_sparetime` exact formula + unit** (12-position clock-hour vs 240-unit fine hour) and the
   value of `DAT_00737630` (seconds-per-logic-hour, assumed 1800). `FUN_006955d0`/`Sparetime.cpp` is
   Ghidra-mangled. [sparetime-idle §9]
3. **`get_digedge` inner search loop** (`0x5982eb–0x598785`, ~70 blocks dropped): BFS-via-FastArray
   confirmed; 4-neighbour expansion, nearest-to-dwarf selection, distance metric, and the standable gate
   (`+0x3c&2` vs `+0x12&0x40` vs `+0x34+layer`) are INFERENCE. Needs a manual disasm / live probe. [dig-edge §9]
4. **`.3db motionOffset → world` scale constant** — the single calibration that makes root-motion speed
   faithful and removes both fudge factors. Hints: `DAT_0073703c=0.125`, `DAT_0073752c=8.0`. [animsets §11]
5. **`FUN_00423270(9..12)` component semantics** — sum×0.25 = displacement is VERIFIED; whether 9-12 are the
   4 floats of the active clip's motion vector or the strides of slots 9-12 is INFERENCE (port to the
   observable `|motionOffset|·0.25`). [animsets §11, walkfloor §2]
6. **`movable[+0x234]` set-to-2 entry point** (where a fresh WALL segment arms the mount phase) lives in the
   MetaWalk dispatcher, not in the read climb functions — confirm by reading writes to `+0x234`. [climb-exec §9]
7. **EFFPASSWALK secondary step-confirm column fan** (the ~3.4% value-1 residual on rows −10..−2) — the
   value-3 split is exact; ~3 column samples need a disasm single-step to 100%-pin. [carve-validator §7]
8. **Calc1 `owner[+0x7d8]` provenance** (the territory toggle that picks cost v1 vs v2 per gnome) and the
   `+0x12` bits 0x20/0x80 semantics behind the variant-B 0xA4 left-mask bug. [calc1-confirm §12]
9. **Native dispatch cadence DAT** (`DispatchInterval`, layer-A interval `timer[1]`, layer-B
   `DAT_007372a8`) — port uses 0.5 as a guess. [dispatcher-reconcile §10, port-inventory §8]
10. **`enable_count` exact threshold** and whether `state_triggerfresh` sets it to 0 or 1; the full
    `get_walkresult` 0/1 codes from the native move state machine. [tcl-work-layer §11]
11. **`get_anglexz` exact native disasm** (formula `atan2(p1.x−p2.x, p2.z−p1.z)` empirically verified,
    not line-cited) and the `CAct_Rotate` per-frame slew rate. [entity-anchor §9]
12. **`get_material` worker FUN** (no profiler label, command-table-bound) and its cellId→hardness table;
    `is_dig_marked` native; `Calc2` functional difference from `Calc1` (campaign uses Calc1, non-blocking).
    [dig-natives §11, metawalk-core §12]
13. **`std_ai` AI dig-mark origin** — `std_ai.tcl` has no `dig_mark`; where AI dig orders originate
    (StoryMgr/lg_* growth engine?) is unresolved. [dispatcher-reconcile §10]
14. **water/swim (`CAct_WalkObject`)** exact locomotion internals remain structurally read only.
    The live fluid point queries, Tcl swim/dive branches, `obj_query -water`, and fish target
    constraint are now implemented; remaining work is executable-backed locomotion verification.
    [metawalk-core §7, `FLUID_SYSTEM_RE.md`]

---

## Appendix A — Master constants table

| Const | Value | Role |
|---|---|---|
| `DAT_00737058` | **0.25** | root-motion → arc-length scale (the displacement constant) |
| `DAT_0073708c` | **0.5** | depth (z) axis weight in EVERY distance metric; `z = nibble·0.5 + 7` |
| `DAT_00737520` | **7.0** | z base |
| `DAT_0073703c` | **0.125** (1/8) | sub-cell int → world; settle step `y += 0.125` |
| `DAT_0073752c` | **8.0** | world → sub-cell (`round(x·8)`) |
| per-tick advance | **0.1** | fixed per-tick arc fraction, RESET each tick (NOT a residual) |
| `DAT_007375b0` | **32.0** | walk-loop sub-segment arc granularity |
| `DAT_00737608` | **96.0** | start/loop/stop threshold arc |
| `DAT_007374a0` | **4.0** | heading → 4 sectors; world.z → climb-grid row (`round(z·4)`) |
| `DAT_007374f8` | **6.28319** (2π) | heading wrap |
| `DAT_007370d0` | **0.7854** (π/4) | climb-step turn threshold (`FUN_00418850` only) |
| `DAT_0073702c` | **0.09** (0.3²) | arrival eps² (climb/standoff) |
| `DAT_0073701c` | **0.0625** (0.25²) | lateral climb-step threshold² |
| `DAT_007374cc` | **5.0** | dig-rotate proximity (`dist < 5.0`); also climb-flood cost-0/3 weight (×256) |
| `DAT_00737330` | **2.0** | climb cost-1 weight (×256); `dig_next` Z post-add +2.0; EFFPASSWALK step `+2` |
| `DAT_00737138` | **1.0** | climb cost-2 weight; densifier spacing; goal-depth `h−1.0`; `dig_next` Z low clamp |
| `DAT_00737658` | **256.0** | climb-flood cost multiplier |
| 5-pass costs | **{1280,512,256,1280,1024}** = {5,2,1,5,4}×256 | climb Dijkstra |
| mount / dismount window | **±16 horiz** / **±256 row, col−1** | `FUN_0041bbd0` / `FUN_0041bde0` |
| `DAT_00737564` | **14.5** | depth ceiling (target-Z / vector_fix(dig) clamp / climb-up endpoint) |
| `DAT_00737548` | **12.5** | climb-down endpoint depth |
| `DAT_00737550` | **14.0** | `dig_next` Z high clamp; `get_digedge` Z (`0x41600000`) |
| `DAT_007370c0` | **0.75** | `dig_next` Y anchor offset |
| `DAT_007374e0` | **6.0** | `dig_next` Z anchor offset |
| `DAT_0073752c`(reuse) | **8.0** | `dig_next` +8 non-id penalty |
| CLIFF v1 / v2 | **0x114=276** (iOwner>7) / **0x10a=266** (iOwner≤7) | Calc1 cliff-edge cost |
| step cost | **0x10=16** | Calc1 dig-nibble step |
| `DAT_007371c0` | **1.5** | Calc1 budget clamp ×; bLines axis dominance |
| `DAT_00736f78` | **0.0** | bLines length Z term (2-D only) |
| `DAT_00737054` / `DAT_007370d4` | **0.2** / **0.8** | walk-mover Mode-A root-motion / heading weights |
| `DAT_00736fcc` | **0.005** | carrier-height term `rec[+0x54]·0.005`; clip root-translation scale |
| `DAT_007373f0` / `DAT_00737190` | **10.0** / **0.05** | animation FPS / frame-index round bias |
| `_DAT_007370a8` | **0.65** | walk-node walk-event range scale |
| open threshold | **0xE=14** | EFFPASSWALK `h<14` ⇔ open |
| dig-state targets | **{0,3,7,11,15}** | `tile+0x08` nibble decode |
| tile stride / BDB aux stride | **0x7c=124** / **0x59=89** | tile record / per-block aux |
| gait block / slot stride | **0x1e4=484** / **0x14=20** | per-gait bank / per-slot record |
| AssignCooldown (`gnome+1000`) | **2** | dispatcher per-gnome busy cooldown |
| clock | **240 / 2880** logic-hours | clock-hour / 12h revolution (no day/night) |

(Dispatcher match weights `DAT_007374cc`/`DAT_00737058`, radius K-constants, and the worktime
seconds-per-hour are UNRESOLVED rodata — see §12.)

---

## Appendix B — Vertical ladder transport rider (lane C,)

Vertical ladders are not wall climbs. Their transport object and their riders have two independent
per-tick laws.

### B.1 Ladder-object rung reassert

`FUN_00607000:38-46` refreshes the projected ladder/span. After moving riders at `:48`, the transport
selects the `leiter_a..i` presentation through `FUN_0060b560` and calls `FUN_005cdae0` every tick at
`:49-54`. The call is therefore gated by the factual current animation/frame, not by a remembered
presentation choice. The port mirrors that by caching the desired qualified clip to avoid allocation,
comparing it with `DigglesTclEntity.CurrentAnim` plus mode, and reapplying only when factual state
diverges. This applies only while the projected transport span is active; construction stages are not
reasserted.

### B.2 Dedicated rider mover

Registration is `FUN_0060c3c0`: remove an old rider entry, append a new 0x14-byte entry, copy current
and target Y, and reset entry `+0x0c` (down velocity) to zero. Removal is `FUN_0060c2b0`. The transport
ticks the rider table with `FUN_0060bc70` (`FUN_00607000:48`):

- UP (`targetY <= currentY`): `deltaY/tick = rm(slot 0x0e, ladder_climb_up) ×
  DAT_00737034`, where `DAT_00737034 = 0.1`; clamp at the target.
- DOWN: first `currentY += velocity × 0.1`, then `velocity += DAT_00737058` where
  `DAT_00737058 = 0.25`, capped by `DAT_007373d8 = 2.5`; clamp at the target.
- Every tick hard-snaps the rider to `(ladder.x, currentY, ladder.z + DAT_00737138)` with
  `DAT_00737138 = 1.0`, and fixes rotation to `(0, pi, 0)` (`FUN_005cd8e0:222`).
- Every tick calls `FUN_0060bb30`, which selects slot 0x0e UP / 0x0f DOWN against the factual
  current model/frame. Matching loop reasserts do not restart the loop.

The live slot-table scale remains `raw |VecA| × 0.05`. Shipped `mann.3db` values are
`|leiter_hoch_loop| = 28.2918701171875` and wall-climb values
`{8.357107162475586, 8.29141902923584, 8.288270950317383, 8.306541442871094}`. Thus:

```
ladderUp = 28.2918701171875 × 0.05 = 1.414593505859375 wu/s
wallClimb = 0.25 × sum(wall slots) × 0.05 = 0.415541732311249 wu/s
ratio = 3.40421525893774
```

`leiter_runter_loop` has raw magnitude `7.066395282745361`, but it does not set descent speed;
the stepped velocity entry above does. A ladder crossing a floor corridor remains one rider span:
the floor waypoint keeps `IsWall=false` and only carries the same ladder source tag. Interior crossings
stay in the rider; the final floor remains the separate `FUN_00411870`/`FUN_00411c90`-style MetaWalk
exit from the ladder plane. Generic floors, generic walls, and elevators keep their existing dispatch
laws. In particular, the known elevator-shaft
free-climb asymmetry is not changed here.

### B.3 L5 combat follow-up boundary

The lane-C L5 card concerns combat ownership, not movement ownership. Current
`FightSystem.FindAttackPosition` asks `get_place` for an annulus and validates reachability only after a
single candidate; `FightResolver.ResolveAttack` independently uses melee reach
`max(class_fightdist) + 0.2`, reapproaches through `reach + 2`, and applies the verified 15-degree facing
gate. The required follow-up is to Play-log TrollQuest first, then make the floor-mounted
`FUN_005c3880` path choose the nearest reachable floor candidate toward the attacker within the same
shared reach radius. Candidate ordering, reach, and the resolver must share one law; tests must cover a
full-fighter plant, no backoff/idle loop, and stable attack after a ladder dismount. No combat code is
changed by this movement lane.
