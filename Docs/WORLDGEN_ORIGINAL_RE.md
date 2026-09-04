# Diggles Worldgen — Original 100% RE + 1:1 Port-Parity Plan

> The **1:1 rebuild has landed** (the -exlimit granite band is disabled). The RE ground-truth below is
> current. **PORT-STATUS UPDATE:** the per-item `[NOT FIXED]` tags and tree-verified line numbers in
> **PART B are PRE-REBUILD** — Phase 0-6 (B0-B10, BA-BC) were all executed by the rebuild. See the PART B
> banner for the current per-item state. `LevelGenerator.cs` is now 1583 lines; the selection code is
> `DigglesTclEngine.cs:CmdSmGetLevelInfo` (~:4300+), NOT the old `:4106/:4127` refs.

_Consolidated synthesis of a 6-agent max-effort RE workflow. Evidence rank: live-dump > decompile > Tcl > prior-artifact._

---

## ✅ IMPLEMENTATION STATUS — ALL PHASES LANDED + HEADLESS-VERIFIED

Every ranked divergence (B0–B10, BA–BC) is implemented, no stubs. Full EditMode suite: **713 tests,
8 pre-existing baseline failures, ZERO worldgen regressions**. Real-chain diag `Diag_UtempGeneration_
RealChain` (all 4 dig dirs, `sm_set_temp` used-marking, `corrSkip`): story caves place one-per-type
(einsiedler→feen/troll→voodoo→normal), every one `conns=1 corrSkip=0 AWAY-OK` (connected corridor, correct
side) — confirmed on BOTH System.Random and the real MT19937 stream.

| Phase | Divergence | What landed |
|---|---|---|
| 0 | **B0** composite doors | split-template sub-pieces now load their merged `.ent` doors (were 0 → buried seam → whole-gen fail). Composites place. |
| 1 | **B1+B2+B6** selection | removed the invented high-depth cutoff; added the `FUN_005fa9a0` free-rect FIT test + fall-through; split `-mindist`(ref-placed)/`-middist`(centre-exclusion). Story caves stop over-spawning; NORMAL caves when none fit. |
| — | **B5→** position | `FUN_005fc0a0` re-places the rect around the dig cursor (exact utDir X-hint + watermark clamps) → utemps land AWAY in the dig dir, connect sub-piece at the cursor (short corridor). |
| 3 | **B3** corridor | removed the port-invented superset fallback (native indexes `general[mask]` directly) → every corridor piece carries exactly the required doors → mates/connects. Real variation consts `0.25`/`32768` from `.rdata`. Unroutable → cap-and-continue (never vanishes). |
| 5 | **B8/B9/B10** cosmetic/material | B8 `mat` rects = occupancy reservations (courtyards, grid=1); B9 `sm_draw_stone -funnel/-exlimit/-utmat`; the campaign `-matlist` consumer follows `FUN_0065e090:190-206` and publishes the painter's EFFBLOCK band; B10 `FUN_00585390` dead-end prune (atomic revert-on-fail). |
| 6 | **BA/BB/BC** orchestration | DigNotify story-callback gated on `+0xa0` (no premature Umzug); real `sm_dig_message` setter; `sm_add_event` registration (no more "0" stub). |
| 4 | **B7** RNG | faithful MT19937 (`FUN_004454d0` sgenrand + `FUN_0049c670` genrand, seed `0x1105`, native draw semantics) replacing System.Random; per-session prewarm emulates the shared-stream decorrelation. Reference-vector test passes. |

**Honest limitations (inherent, not stubs):** (1) bit-exact reproduction of a SPECIFIC original
playthrough is UNACHIEVABLE — needs the cross-system shared-MT interleaving + a captured trace, neither
of which exists; the port delivers the faithful algorithm + native seed + per-session variety. (2) B8/B9
courtyard/stone-rim/scape effects are terrain-visual → **Play-verify**. Not committed (Play-first, then a
separate branch).

**⚠ CORRECTION** — the paragraph below described the tree state *before* the 1:1 rebuild
(43805a0). CURRENT tree: the split-template fix is present; the **high-depth cutoff was
REMOVED and the free-rect FIT test + fall-through ADDED** (`DigglesTclEngine.cs:~4331-4366`, code carries
the `§B1`/`§B2` citation and confirms "REMOVED — the native has NO such gate"); `-middist` is now an
independent centre-column exclusion (`§B6`); the corridor buckets are sourced by `BuildCorridorBuckets`
with cap-and-continue (`_corridorsSkipped`); `Mt19937.cs` now exists and the sm_/lg_ layer draws from
`ctx.StoryRng` (LevelGenerator's default `Rng` is still `System.Random`). So the deviations B1/B2/B6 are
FIXED, not "present". The original paragraph is kept below for provenance:

_(pre-rebuild snapshot)_ I have verified every load-bearing claim against the live tree. The split-template fix (Report 5) is present (`LevelGenerator.cs:1125-1171`); the two `CmdSmGetLevelInfo` deviations (Report 2) are present (`DigglesTclEngine.cs:4106` cutoff, `:4127-4137` no fit test); the corridor buckets are sourced from `_pool` (Report 3, `LevelGenerator.cs:761-772`); `Rng`/`StoryRng` are `System.Random` (Report 6). Here is the consolidated spec.

---

# DIGGLES WORLDGEN — CONSOLIDATED 100% RE + 1:1 PORT-PARITY PLAN

**Evidence ranking used to break ties:** live-dump (Report 6) > decompile (Reports 1-5, byte-verified) > Tcl source (Report 4) > prior artifact/doc. Where a report's claim was load-bearing I re-read the current port lines myself; those are marked **[tree-verified]**.

### Contradictions resolved up front
| # | Conflict | Resolution (stronger evidence) |
|---|---|---|
| C1 | R2: port MISSES the free-rect fit test; R1 treats fit as part of selection | **R2 correct.** `DigglesTclEngine.cs:4127-4137` **[tree-verified]**: port draws the dice, computes `seedW/seedH`, then `break`s — no `seedW>rw` rejection. `FreeRectFit` (:4152) only *clamps* the result rect, never *rejects* the utemp. Divergence real. |
| C2 | Does the corridor router run live? R3 "only for `-connect`", R6 "docs self-contradict / never runs" | **Both true, disjoint cases.** `FUN_00587480` runs iff `_connections≠∅`, which happens **only** for a placed utemp carrying `-connect`/`-digconnect` (`FUN_005fc3b0`←`FUN_00650f90:503`). Plain digs → empty → router body never executes ("never runs in shipped-data plain caves"). Port `RouterEnabled=true` **[tree-verified `LevelGenerator.cs:276`]** and DIV persists **when it does run** (wrong piece table). |
| C3 | Is the split-door fix necessary AND sufficient for monotony? R1 "necessary-not-sufficient"; R5 "THE fix" | **Both, different symptoms.** R5's fix is *necessary* for composite/unique caves to generate at all (buried-seam → whole-gen fail). R1 proves it does **not** change plain-dig monotony (2/300 clusters before & after). Keep both framings; they target symptom-4 vs symptom-1. |
| C4 | Utemp selection field: R2 `+0x54=index`, R4 `+0x50=name` | **`+0x54` = selected utemp index** (R2 byte-verified writer `FUN_0065cba0`↔reader `FUN_005fac90`, `param_1[0x15]`; corroborated by R1's "`+0x54!=-1` = utemp-matched"). R4's `+0x50` is the wrapper's resolved-name buffer/misattribution — non-load-bearing (port carries `ctx.StoryUtemp` by value). |
| C5 | What causes "monotony"? R1 (pose), R2 (over-priority), R6 (RNG) | **Layered, not conflicting** — see A10/B. Dominant lever = story caves not generating (C3) → only the 2 deterministic plain clusters show. RNG is near-powerless (R1 Exp F: 2/300 even with independent seeds). |
| C6 | The "same caves" cost fixes (capTerm, object-gate) | **Landed & dump-verified faithful** (R6 §2: `+0x1c` capTerm, `+0x50` session-gate, all 17 `.rdata` constants match). **Do NOT re-chase.** |

---

## PART A — HOW THE ORIGINAL WORLDGEN WORKS, END-TO-END

Pipeline: **dig breakthrough → latch → idle pump → `sm_scan` → `generate_next` → `sm_get_level_info` → `lg_set_starttemplatepro` → `lg_start` → `lg_get_level` → per-tick `load_next` stamp**, with a failcount retry channel wrapping the middle.

### A0. Boot / registration (Report 4)
`systeminit.tcl` → `classinit.tcl` (`def_class StoryMgr`, loads `story.tcl` body) → `lginit.tcl` (the `lg_*` template registry). New-game → `load_level`: `CampaignInit.tcl` (`sm_*` world/zone registry) then `Campaign.tcl`.
- `lginit.tcl`: `lg_addfilterclass {Zwerg Wuker Troll Pilz Kohle Kristallerz Golderz Eisenerz Schatztonne}`; `LG_set_templategroup` glob-expands `tcl/urw_gng_*`→`Urwald.Std.Gng/.Gsg/.Hol`; `.hol`/`.gsg` get `-levelcount 1`; four object set-piece caves get `-gamecount 1`.
- `CampaignInit.tcl`: `sm_create_map 512 640` (→128×160 story cells, res=4); `sm_set_digcount 32` (breakthrough weight threshold — **dump-confirmed `+0x9c=32` in both dumps**, R6); `sm_def_temp_group` (~30 composites); `sm_add_event` (~55); per-zone `sm_add_zone`+`sm_add_temp <name> <lo> <hi> -opts`.
- StoryMgr ctor `FUN_005fa7c0`: `resolution=4`, `failcount 0`/`maxfailures 4`, `digcount +0x9c` default **5** (overridden to 32), dig-callback flag **`+0xa0=0`** (`:33`, dead by default), `Generate_enabled 1`.

### A1. Dig → latch — `CStoryMgr::DigNotify` = **FUN_005fd060** (+ `FUN_005fcfb0`) [BV, R3/R4]
Fires from `dig_apply` **before** the carve. Per dig:
1. Bounds gate ≥8 tiles from every edge (`:29-32`).
2. Story dig-callback `"<cb> dig shut -1"` **gated on `+0xa0!=0`** (`:33-64`) — **dead in shipped data** (no live `sm_dig_message 1`).
3. **Clear latch** `+0x88/8c/90/94` (`:66-70`) — only the LATEST breakthrough survives.
4. Mark dug story cell grown (`FUN_005fa510`, `:71-73`).
5. Nibble gate on the dug tile `(n&8)==0 && n!=0` (`:75-77`).
6. Probe the 4 neighbours **in order +x, +y, −x, −y**; the first un-grown + nibble-passing dir **bumps `weight +0x98`** by **`+x→2, +y→4, −x→2, −y→1`** (dir codes `8/2/4/1`, via `FUN_005fcfb0:16-25`). When `weight > digcount(+0x9c)` → **LATCH `{dir, x, y}`** with `x,y` = the **±4 NEIGHBOUR tile** of the dug tile, `consumed(+0x94)=0`. **≤1 bump per dig; early-return after latch.**

### A2. Idle pump + consume — `story.tcl` state `idle` + `sm_scan` = **FUN_006616d0** [BV, R4]
Ticked 10 Hz. Per tick, in order: StartCnt warm-up (5×`wait_time 1.0`) → GameOver check → Umzug execution → `check_uebergang ×3` → `Generate_enabled` gate (dead) → **`load_next` (ONE stamp/tick; returns here if a piece streamed)** → only when the buffer is empty: `set area_list [sm_scan]; if {$area_list != 0} generate_next …` → `wait_time 0.5`. Net cadence: dig pressure → one breakthrough → one generation (one seed cluster) → its pieces stream one per 0.5 s. (`proc get_next_areas` is DEAD CODE; the live scanner is native `sm_scan`.)
- `sm_scan`: latch invalid (`+0x88|8c|90==0 OR +0x94!=0`) → Tcl `0`; else → `{(+0x8c)/4, (+0x90)/4}` (the /4 NEIGHBOUR **cell**) and set `consumed +0x94=1`. **Does NOT reset weight `+0x98`** — this is what enables the retry (A3).

### A3. `generate_next {x y}` orchestration + failcount retry (story.tcl:125-289 + `FUN_00652d10`) [BV, R4]
`x,y` = cell coords. Steps:
1. `zone = sm_get_zone (y*res)`; `lg_tp_clear`; `lg_tp_addtemplatesets "$zone.Std"`; `lg_tp_objfilter {Troll Zwerg}`; `lg_set_templategroupvalue $zone.Std.Hol <0.287|0.185|0.077|0.105>`.
2. `linfo = sm_get_level_info x y` (A4) → `"x y w h utempName"`.
3. Too-small abort: `if w*res<16 || h*res<16 → sm_map_set orgx orgy 1 1; return`.
4. `linfo = lg_set_starttemplatepro 1 1` (A5) → `"x y w h utemp"` (walk fail ⇒ `utemp="nodigmark"`); second too-small + `nodigmark` aborts.
5. `if utemp!="" → lg_tp_setfilter "$zone.Std.Gsg"` (REMOVE the ring pieces).
6. `define_obj_counts` → per-class `lg_set_objcount` deficits.
7. `optratio=0.10; lg_set_templateratio 0 0.10 1.0; lg_set_leveltype base`.
8. `lg_start` (A6).
9. `level = lg_get_level` (emits `{name x y}` triples; **marks + re-arms**).
10. **FAILURE HANDLING**: `if {[llength $level]==0} { if {failcount>=maxfailures(4)} failcount=0; incr failcount; return }` — a failed gen does **NOT** mark the scan cell grown and returns early, consuming nothing. `set failcount 0; incr gencount` on success.
11. `sm_draw_stone -exlimit`/`-utmat`; `sm_set_temp $level` (mark uniques used); `fill_temp_buffer $level` (+`lg_sort_level` nearest-first); the special-hooks foreach (zone shift + chained fill); `sm_map_set orgx orgy 1 1`.

**The re-arm law — `lg_get_level` = FUN_00652d10:280** [BV, R4/R6]: `local_30 = FUN_005f8080` = the **StoryMgr singleton** (NOT the generator). Per emitted placement it emits `{name x y}`, marks the placement's story rect grown (`FUN_005fa510`, `:276-279`), bumps object-session counter `+0x50` (`:84-93`), and **`*(local_30+0x98)=0`** zeroes dig-pressure. ⇒ an **emitting** gen resets weight (next breakthrough needs fresh 32); a **failed/empty** gen never runs the loop → **weight survives → next qualifying dig re-latches** → `sm_scan` re-fires → retry with advanced RNG. `sm_scan` deliberately does NOT reset weight so the retry is possible.
Port **[tree-verified `DigglesTclEngine.cs:3445-3471`]**: `ctx.StoryDigWeight=0` + `CommitSessionEmission` **only when `anyPlacement`** — faithful.

### A4. Free rect + spawn priority — `sm_get_level_info` = **FUN_005fac90** (wrapper `FUN_00661340`) [BV, R2]
Writes to the StoryMgr singleton: rect `+0x38/3c/40/44` (W,H,X,Y), **selected utemp index `+0x54`** (−1 = none), scan cell `+0x48/4c`, `+0x68=0`. Three stages:

**Stage 1 — free-rect expansion (`:67-117`).** 4-way grow of the un-grown region around the scan cell via `FUN_005fa8e0` (returns 0 if any probed story cell `==1`). Probe order **left(:78) up(:89) down(:98) right(:106)**; stored `+0x58(L)/5c(T)/60(R)/64(B)`. Port faithful **[tree-verified `:4054-4072`]**.

**Stage 2 — candidate scan (`:128-330`)**, per registry entry (stride 100), in **registration order**, gates (ALL must pass):
- **G1 used** `+0x14!=0`→skip (one-time).
- **G2 min-depth** `depth < +0x20 + zoneBase` (depth=`cellY*4`; `zoneBase=FUN_005fcdf0(depth)+4`, =0 for Urwald zone-0).
- **G3 events** (`-events`, `def[ev]+0xc!=0`) — no-op in shipped data.
- **G4 registration-zone** `+0x50 != FUN_005fcdf0(depth)`→skip.
- **G5 mindist/middist** (`:220`): skip if `distMetric < +0x28`. `distMetric` is degenerate (candidate pos slots never written pre-placement) ⇒ **`-mindist` reduces to "ref temp `+0x2c` must already be placed."** **`-middist` (`+0x60`, `:203-215`)**: if `middist>0 && abs(cellX − mapW/2)*4 < middist` → `distMetric=-1.0` → skip = **center-column EXCLUSION**.

**WEIGHT** (`:261-314`, all constants dump-verified R6):
```
weight = 0.8 if max==min (@0x7370d4, DEAD in shipped data)
       = +0x44 (-nonrelevant)                    if nonrelevant!=0
       = (depth − min − zoneBase + 1)/(max − min)  otherwise    (the RAMP; >1.0 past band)
```
`min=+0x20`, `max=+0x24`, normalized `min(lo,hi+1)/max(lo,hi+1)` (`FUN_0065cba0:238-249`). **There is NO high-depth skip** — `max` is used ONLY in the ramp.

**Stage 3 — SELECTION (`:634-820`), the priority draw**, iterate candidates in registration order:
1. `r = (rand&0x7fff)/32768 ∈[0,1)` (`:641-642`).
2. name==`"urw_kif_4"`→`r=0` (dead — never registered).
3. **Dice: `weight < r` → decline, next candidate** (`:675`). ⇒ P(accept)=min(weight,1); a past-band temp (weight≥1) is never declined.
4. Placement checks + **THE FIT TEST `FUN_005fa9a0(mode 1)`** (`:781`): reject → **next candidate** if the scan cell is grown (`:21`) **or the seed exceeds the free rect**: `seedW>freeRectW` (`:80`) or `seedH>freeRectH` (`:76`), where `seedW=(utW+8)/4+1`, `seedH=(utH+8)/4+1`, `freeRectW=+0x60−+0x58`, `freeRectH=+0x64−+0x5c`. `-exclusive` temps take an extra map-edge probe.
5. First candidate passing **dice AND fit** → `+0x54=index`; else `+0x54=-1`.
**So story-cave frequency = P(a candidate passes dice AND fits).** As the colony fills, free rects shrink → big story caves stop fitting → `-1` → normal cave. The **fit test is the native's primary throttle.**

**Result rect = `FUN_005fa9a0` fit inside the raw rect.** `+0x54` is STILL −1 at the fit call (`:630` clears it; index written at `:824` AFTER) ⇒ `FUN_005fa9a0` takes **case 1 = CENTERED** on the scan cell for both story and normal. (Only later, in A5, a matched utemp diverts to case-0 RANDOM placement.)

### A5. Anchor + rect re-place + utemp positioning — `lg_set_starttemplatepro` = **FUN_00650f90** [BV in Docs, R3/R4/R5]
1. **Latch gate** `+0x88 && +0x8c && +0x90` (`:177-178`), **cell snap `&=~3`** (`:179-180`).
2. **THE PRE-STEP** (`:182`=`FUN_005f7a90`, byte-verified `case1 y-=4; case2 y+=4; case4 x-=4; case8 x+=4`): the walk offset advances **one ±4 step ALONG the dig dir before iteration 0**, so the start piece's top-left = `latchCell + preStep` = **one full cell of virgin rock beyond the latch → the piece NEVER overlaps the player's tunnel.** The gnome connects by digging the virgin gap + the piece's closed wall.
3. **THE WALK** (≤4 iters, `:184-239`): constant latch-cell dig-nibble gate (miss ⇒ all 4 fail ⇒ `"nodigmark"`); a fresh **random dig-dir start piece** (`FUN_005f7980`→`FUN_00585d80`); pose probe `FUN_0058a510` (4-align, map inset ≥8, no covered `HasTpl` tile). Failure steps a further ±4 and retries. **NEAR-BORDER SWAP** (`:261-273`): anchor X <5 from left or ≥areaW−4 → swap the record's template for the MODE-0 pick (single onward door).
4. **Start-piece pick — `FUN_00585d80`**: filters ACTIVE POOL to 4×4 pieces; **down-dig mode 1** → exactly two `{left,right}` through-corridor doors (gng_001); **all other dirs** → exactly one onward-door cap (up→gng_023, +x→gng_021, −x→gng_022, down-mode0→gng_024). One match picked uniformly.
5. **UTEMP path** (`+0x54 != -1`):
   - **Rect RE-PLACE — `FUN_005fc0a0(dir, |walk|+4)`**: early-returns for **non-utemp** (`:27-29`) — R1 confirms virgin plain caves keep the centered rect. For a utemp it re-derives a **cursor-anchored** rect sized past the template in the dig direction, with watermark clamps `+0x58..+0x64` (native-exact clamp values un-recovered — see B5).
   - **Composite (group) — record `+0x3c != -1` (`:364-365`) → `FUN_005f7d10`**: iterate the sub-piece list (`+4` base, `+8` count, **stride 0x18**), per sub call `FUN_0058a510(base_x+sub_dx, base_y+sub_dy)`. **ATOMIC**: counts successes; if not all, roll back the appended records (`*(+0x198) -= n`) and return 0. **Single template** → `FUN_0058a510` directly.
   - **`FUN_0058a510`** = per-sub probe + register: name→index (`FUN_00583080`), 4-align + inset≥8 + no covered templated tile (`tile+0x13 bit0`), **append forced record `{tplIdx, x, y}` to `+0x194`.** It does **NOT** run the frontier validator (that's deferred to `lg_start`).
   - **CONNECT records — `FUN_005fc3b0`** (from `:503`): registers the start↔utemp `-connect` entries into `+0xa4` (stride 0x18: SrcRec/SrcDoor/DstRec/DstDoor/sizeHint/weight=0.4).
6. **`mat` material rects** (`{mat {0 x y w h}…}`, `:424`): type-0 rects offset by the composite origin → appended to CLevelGenerator `+0x17c` (count `+0x180`). **NOT written to the occupancy grid → play no part in door validation**; stamped into terrain in the realization pass (the connective courtyards/paths).

### A6. Grow + closure — `lg_start` = **FUN_00589790** (binding `FUN_00652bc0`) [BV, R1]
`FUN_00652bc0`: reset `FUN_0058a100` → grow `FUN_00589790` → free grid.
- **Reset `FUN_0058a100` (per-generation)**: zero objPlaced `+0x94`; **zero every template `placedThisLevel +0x24`** (0x54-stride loop); zero filled `+0xa0`; realloc+memset grid `+0x84`, set area `+0x88`; count free cells → `+0x9c`. **Does NOT touch `+0x50`** (EmittedThisSession = session-persistent). Port faithful.
- **`FUN_00589790` body**: forced placement `FUN_005896a0` (`:64`) → connect-routing loop `FUN_00587480` (`:69-115`, non-empty **only for `-connect` uniques**) → mark-area paint=1 (`:117-171`) → the grow `do…while` (`:172-259`):
  - `FUN_005862c0` seed/advance (`:173`); fail → rollback.
  - inner `while(frontier>0)`: **head door = `frontier[0]`** (`+0x8c[0..2]`=x,y,dir); `FUN_00586f40` cost-sorts the whole pool ascending (`:196`); scan candidates, try only edges with **`((edge.dir − doorDir)&3)==2`** (`:223`), place at **`(doorX−edge.lx, doorY−edge.ly)`** via `FUN_005842c0` (`:234`); first success → reload frontier; **head undockable → `ok=0`, break** (`:252`).
  - `FUN_005866a0(0)` (`:258`) = verified no-op.
  - outer cond **`fill/total < ratioA(+0x70) && ok`**. **story.tcl passes ratioA=0 ⇒ exactly ONE seed cluster per generation.**
- **`FUN_005896a0` (forced placement)**: walks `+0x194` records in group order (stride 0x10), each via **`FUN_005848b0`**; **ANY failure → return 0 → whole generation fails** (the failcount retry channel).
- **`FUN_005848b0`**: paint body into grid `+0x84`, toggle doors into the frontier via **`FUN_00583f60` (MergeEdges)**, run frontier validator **`FUN_00584120`** — **this is where a buried composite seam door is rejected.**
- **CLOSURE LAW (`:262`)**: head-door-undockable ⇒ `ok=0` ⇒ **whole generation fails & rolls back** → `lg_get_level` empty → failcount retry. Success ⇒ every door mated or capped.
- **Seed `FUN_005862c0`**: first pool piece `W==4&&H==4&&edges==2`; ≤16 random 4-aligned spots ≥4 inside area; **a forced start satisfies the first call at `:24` (returns without seeding).**

**Cost — `FUN_00586710`** (all `.rdata` constants dump-verified, R6):
```
size term = 2·|20−(W+H)|·(2−progress)·value·100 (dominant: separates pieces by hundreds)
jitter = (rand&0x7fff)/32768·(1−progress)·value·100 (≤~26; →0 once progress≥1)
once-gate:58-60 if(piece+0x41) if(0 < piece+0x50) return INT_MAX (SESSION emit gate)
capTerm:25 iVar=INT_MAX; if(+0x41) iVar=+0x1c; min(iVar,5)·200 + placedThisLevel(+0x24)·250
spacing:63-89 |dx|·|dy| < r·1.5 over objPieces +0x18c/+0x190
levelcount:120-122 piece+0x20 vs +0x24
```

### A7. The corridor router — `FUN_00587480` [R3]
Runs only for `-connect` records. Flood from the DST door step-off over `LOW16==0` (empty) cells stepping ±4 → gradient-ascend to the SRC step-off → emit a chain of **4×4 corridor pieces via `FUN_005842c0`**, picked from **dedicated corridor tables `+0xac` (general) / `+0x154` (straight mask 5) / `+0x130` (straight mask 10)** — **NOT the growth pool.** Direction/variation pass at `0x588ce1..0x5890c6` uses `DAT_00737058` (rate), `DAT_007376dc` (K), persistent accumulator `DAT_00c3c9b8`. A route that cannot carve → return 0 → `FUN_00589790:11` rollback.

### A8. The terrain / mesh STITCH [R3]
**The blit — `map_template` = `FUN_005992b0`**, destination-blind, template-wins:
- Reads block-idx (`_DstBuf_00`), material/corner (`_DstBuf`), RLE solidity (`_DstBuf_02`), heightmap (`_DstBuf_01`).
- Pass 1 (`:124-225`): **material-class resolution only** — file code `1→(worldCorner&3)+1` inherit, `2→3, 3→2, 4→1, 5→4`, `0` untouched; writes back to the SOURCE buffer.
- **Transparency (`:292-454`)**: an all-zero 2×2 terrain-corner window skips the per-tile block/material/height writes; those destination record fields survive.
- Non-transparent: **verbatim** block-idx→`tile+0` (`:379-415`), material/corner→`tile+0x18` (`:317-377`), heightmap→`tile+0x07` (`:453`). **dig-mark nibbles `tile+0x08` NEVER touched.** `HasTpl` `tile+0x13|=1` over the rect.
- **HasTpl + solidity are independent whole-rectangle passes**: `tile+0x13|=1` at
  `FUN_005992b0:461-529`; the expanded source raster is handed at `:553-568` to
  `FUN_00596550:31-141`, which copies every clipped `tile+0x3c[subY*8+subX]`, including
  transparent seam tiles.

**The "re-mesh" — `FUN_005a3dd0`/`FUN_005a3fd0` (`:570-607`)**: `FUN_005a3dd0` = effect-manager singleton alloc. `FUN_005a3fd0` = dispatcher → per effect-flag calls **`FUN_006c2340` = a dirty-RECT list manager** (DSFastArray). Passes over `rect` (0x40000), `rect±1` (0x400), a band (8) **re-derive DERIVED data ONLY** (EFFPASSWALK / EFFBLOCK / RENDERJOBS / MINIMAP / FLUIDALIST). **Nothing rewrites block-idx, height, or corner geometry at the seam.** ⇒ **Watertightness = non-overlap + authored coherence, NOT reconciliation.** There is no seam-fixup and none is needed because nothing incoherent is ever adjacent.

**Door model — `FUN_005826d0`** (`.ent map_entry 1 Xpx Ypx`, type gate `local_38==1`; 1 tile=8px) [R5, tree-verified faithful]:
- Quantize `q = ((v + (v>>31 & 7)) >> 3) & ~3`.
- Half-cell +2 edge encode from `rx=x%32, ry=y%32`: `ry<rx` → (`rx+ry<32`: `qx+=2` | else `qx+=4,qy+=2`); else (`rx+ry<32`: `qy+=2` | else `qx+=2,qy+=4`).
- **dir from boundary, last-match-wins**: `qx==0`→left, `qy==0`→top, `qx==W`→right, `qy==H`→bottom; default −1 → **door DROPPED** (`+0x198` decremented).
- **Split-template resolution**: `FUN_00583080` resolves EVERY template (split included) through `FUN_005826d0`, whose `.ent` read runs **OUTSIDE** the split guard (`:86` only skips the pmp-dims re-read for the `+0x40` split flag). ⇒ split parents read doors from the **merged** `.ent`; the split children's `.ent`s are empty.

**Door mating — `FUN_00583f60` (MergeEdges)** = pure coordinate toggle (door already in frontier → removed=mated; else appended). **`FUN_00584120`** rejects a frontier door iff <2 from the area border **OR all 4 diagonal cells non-zero** (buried). Composites avoid burial because **sub-piece `.ent` doors are hand-authored to COINCIDE** — the second tiling neighbor's placement cancels the shared seam door before its cells surround it. (R5 verified all 6 voodoo_dorf internal seams mate; exactly the (116,46) entrance stays open. Einsiedler: (66,24)+(132,34) open.)

**Junction lattice**: marquee tunnels sit at world tiles `≡{1,2} mod 4`; template bores sit at piece-local `{1,2}`; pieces land 4-aligned ⇒ bore ≡ tunnel by construction. **The connection is MADE BY DIGGING** the virgin gap + the piece's closed wall — never carved by the generator.

### A9. RNG model + per-generation draw ledger — `FUN_0049c670` / `FUN_004454d0` [R1/R6]
One SHARED game-wide MT19937 (state `DAT_00767990` [624 words], index `DAT_00768350`), **lazily seeded with the CONSTANT `0x1105`** (`FUN_004454d0`, mult `0x10dcd=69069` = classic 1997 sgenrand). Sole seeders: `FUN_0049c670:33`, `FUN_00598f10:38` — no time reseed. **Dumps prove it live** (loaded: index 450; meadow: index 14; all 624 words non-zero) — every engine system draws from it, so map-init `FUN_00598f10` alone burns ~512×640 draws → each generation samples a heavily-decorrelated stream position.

**Draw ledger, forced-start (dig) case, per generation:**
| site | draws |
|---|---|
| `FUN_005862c0` forced start (`:24`→LAB_00586523) | **0** |
| `FUN_005fac90` selection dice (`:641-642`) | **1 per candidate examined**, until dice+fit pass |
| `FUN_00586710` cost jitter (LAB_00586ea2) | **1 per VIABLE candidate**; INT_MAX (over-max/once-gate/overshoot/levelcount/tunnel) + 4×4-seed short-circuit draw **0** |
| `FUN_005862c0` non-forced seeding (`:89-90`) | **2 per placement attempt** (≤16) |
| `FUN_00587480` corridor | **1 template-pick per emitted segment** + variation-gate draws (`DAT_00737058`/`DAT_007376dc`) |
Port matches the count/order discipline (R1) but from a different stream (B7).

### A10. ⭐ THE VARIETY MECHANISM (the load-bearing insight, R1)
Empirically pinned by running the real Urwald pool (185 pieces post-`objfilter`) through the port grower:
| experiment | distinct clusters |
|---|---|
| forced start, **fixed pose**, 300 **independent** seeds | **2 / 300** |
| forced start, **varied pose**, 60 seeds | **23 / 60** |
| SEED mode (random position), 60 seeds | **50 / 60** |

**Conclusions the numbers force:** (1) The cost jitter is a **near-powerless tiebreaker** — the size term separates pieces by hundreds of cost units while the jitter ≤~26 and dies to 0 once `progress≥1` (after ~2 caves). It only ever flips one exact tie. (2) **Variety is POSE-driven**, and pose has only three real sources: **(a) utemp/story caves** (case-0 RANDOM placement in `FUN_005fa9a0` ⇒ varied composites — the 50/60 effect); **(b) accumulated prior-cave geometry** (`sm_get_level_info`'s raw rect stops at grown cells → asymmetric → the *centered* fit puts the breakthrough off-center → pose shifts); (c) start-chamber object dressing (cosmetic). **Plain digs into virgin rock are DETERMINISTIC BY FAITHFUL DESIGN** (forced 1-door start, breakthrough-centered rect, weak jitter). The original interleaves varied **story** caves with the deterministic plain ones — so if story caves don't fire, the player sees "a monotonous long set of similar caves."

---

## PART B — PORT DIVERGENCES (ranked by player-visible symptom)

> ⚠ **PART B IS THE PRE-REBUILD DIVERGENCE ANALYSIS.** The 1:1 rebuild (an earlier commit,
> evening) + 88df518 executed Phases 0-6. Current per-item state (against HEAD
> 0a9eb12): **B0 fit/split** applied; **B1 fit test + B2 cutoff-removed + B6 middist** — all FIXED
> (`DigglesTclEngine.cs:CmdSmGetLevelInfo`); **B3 corridor** — `BuildCorridorBuckets` + cap-and-continue
> (`_corridorsSkipped`) landed; **B5 rect re-place** — `FreeRectFit`/`FUN_005fc0a0` port present
> (`:4512+`); **B7 RNG** — `Mt19937.cs` added, sm_/lg_ draw from `ctx.StoryRng`; **B8/B9/B10** — `mat`
> rects, `sm_draw_stone -funnel/-exlimit/-utmat` (all four branches), dead-end prune landed, but
> **-exlimit's granite band was DISABLED in 88df518** (Play feedback: it blocked digging past a story
> cave; code kept commented). The `[NOT FIXED]`/`[tree-verified]` tags and line numbers **below are
> historical (pre-43805a0)** — do not treat them as the live tree.

**B0. [Symptom 4 · FIX APPLIED, needs Play-verify] Split-template door drop.** `LevelGenerator.cs:1103-1171` **[tree-verified applied]**. Old `LoadMeta` set `split=true` and returned before the door read → 5/7 voodoo_dorf + einsiedler sub-pieces loaded **0 doors** → doorless split sub-piece can't mate → its neighbor's reciprocal seam door gets buried (`FUN_00584120`) → `FUN_005896a0` fails the whole composite generation → **"composite/unique story caves never generate."** Fix captures `p.PmpBase` from the split header arg2, drops `split||` from the early-return, and guards only the pmp-dims re-read with `!split` (faithful to `FUN_005826d0:86/:146+`). R5 verified bit-exact: before=fail on vodo_002 burying (108,46); after=all 7 place, only (116,46) open. **Status: in-tree, uncommitted; two regression tests authored (`LevelGenTests.cs`); not yet Play-run.**

**B1. [Symptom 2 · NOT FIXED · HIGH] Missing free-rect fit test + fall-through.** `DigglesTclEngine.cs:4127-4137` **[tree-verified]**: the port draws the dice, sets `ctx.StoryUtemp`, computes `seedW/seedH`, then `break`s — it never compares `seedW/seedH` against `rw/rh`, and `FreeRectFit` (:4152) only *clamps* the rect. The native (`FUN_005fa9a0:76/:80`) **rejects** an oversized utemp and **falls through** to the next candidate or to `-1` (normal). ⇒ **the port converts native-NORMAL generations into port-STORY generations whenever a story cave wins the dice but wouldn't fit** = the "story caves spawn when they should not."

**B2. [Symptom 2/1 · NOT FIXED · HIGH · atomic with B1] Invented high-depth cutoff.** `DigglesTclEngine.cs:4106` **[tree-verified]** `if (wy > hi + zoneBase && rec.ZoneBorder.Length == 0) continue;` — **the native has NO such gate** (`max` is used only in the ramp). It was a band-aid to stop einsiedler (band 56-60, ramp≫1 past band) monopolizing selection — a symptom that exists *only because B1 removed the fit test*. The cutoff strips narrow-band uniques (einsiedler/troll_012/014) from eligibility once you dig past their band (native keeps them at weight≥1 = certain-if-fit), and over-exposes the wide-band `-nonrelevant` **feen (60-160, weight 0.474)** → **"feen spawns with priority."** The port's own comment (`:4104`) flags this as awaiting the RE pass that hit the spend limit — **that pass is Report 2; this resolves the TODO.** Must be removed **in the same change as B1** (removing it without the fit test makes einsiedler place hugely everywhere).

**B3. [Symptom 3 · NOT FIXED · MEDIUM] Corridor router sources pieces from the growth pool.** `LevelGenerator.cs:761-772` **[tree-verified]** `BuildCorridorBuckets` iterates `_pool`; the native draws from dedicated tables `+0xac/+0x154/+0x130` (`FUN_00587480`). When the router runs (a placed `-connect` utemp), a bend that lacks an exact 4×4 L-piece falls through `MatchBucket` to a **superset** piece carrying an extra open door → unmated frontier door → cluster bloat or an **uncapped wall gap = disconnection**; if no piece satisfies the required mask, `RouteCorridor→Fail` fails the whole gen → the story cave silently vanishes = **"the tunnel that should connect to the cave is NOT connected."** (R3 HIGH that the table is wrong; MEDIUM on the exact downstream failure — the `+0xac/+0x154/+0x130` population site is un-recovered.)

**B4. [Symptom 3 · UPSTREAM, verify] Overlap/burst holes are prevented upstream, not in the blit.** The blit is intentionally destination-blind template-wins (faithful), so it has zero tolerance for a stamp landing on a carve or prior stamp — it overwrites the carved block-59 floor (fills the tunnel = "not connected") and abuts the still-carved neighbor with disjoint edge-z (see-through hole). The original avoids this **entirely upstream** (pre-step non-overlap + grown-cell rect exclusion + the re-arm pacing). After the A3 re-arm fix (applied) this should hold, **but verify** that DigNotify's `StoryMap[...]=1` marking (`DigglesTclEngine.cs:4487`) actually feeds the `sm_get_level_info` rect solver (`StripFree`). **No stitch-side fix exists — the fix is upstream.**

**B5. [Symptom 1 · NOT FIXED/approximated · MEDIUM] `FUN_005fc0a0` utemp rect re-place + `FUN_005fa9a0` claim-modes.** `DigglesTclEngine.cs:4139-4159` / `CmdLgSetStartTemplatePro` **[tree-verified port keeps the centered rect + flags unknowns]**. The exact watermark clamps (`+0x58..+0x64`) and the `FUN_005fa9a0` case-0-random vs case-1-centered claim-modes are unrecovered. Effect: utemp caves may place at the wrong pose or fail their probe ("UTEMP NOT PLACED"), and — because case-0 random placement is *the* dominant variety source (A10) — a broken re-place also suppresses story-cave variety. Non-utemp early-return (`FUN_005fc0a0:27-29`) is correctly reproduced (centered).

**B6. [Symptom 2 · NOT FIXED · LOW-MED] `-middist` not applied independently (and inverted).** `DigglesTclEngine.cs:4112-4120` **[tree-verified]** evaluates `-middist` only as a *rescue inside* the `-mindist` block, and as `midOk` = "allow through near center" — the **opposite** of the native center-**exclusion** (`FUN_005fac90:203-215`: near center → skip). No shipped temp has both flags, so for middist-only temps (feen, kris_wasser_raetzel, kris_unq_raetsel_002, the lava temps) the block is skipped ⇒ `-middist` is **never applied** ⇒ they spawn in the central column the native forbids.

**B7. [Symptom 1 · NOT FIXED · LOW — does NOT fix monotony] RNG substitution.** `LevelGenerator.cs:121` + `DigglesTclContext.cs:403-405` **[tree-verified]**: `System.Random`, GUID+TickCount-seeded per session, consumed only by the sm_/lg_ layer. Native = one shared MT19937 const-seed `0x1105` advanced by every engine system. Consequence: **cannot reproduce a specific original playthrough**; consecutive caves marginally more RNG-correlated. **Explicitly NOT a monotony fix** (A10: jitter is near-powerless — 2/300 even with independent seeds). It is the deepest *structural* divergence but the lowest *symptom* impact. Note the tension: a naive switch to a fixed `MT(0x1105)` worldgen-only stream **reintroduces the "same caves every playthrough" bug** because the port lacks the shared-stream decorrelation.

**B8. [cosmetic · LOW] `mat` connective-ground paint dropped.** `DigglesTclEngine.cs:3847-3849` **[tree-verified]** `ParseTempGroupEntries` fails `int.TryParse("mat")` → drops the rows. Composites place buildings but not the courtyards/paths (`FUN_00650f90:424-434` type-0 rects → CLevelGenerator `+0x17c`). Not required for placement (mat never touches the occupancy grid).

**B9. [cosmetic · LOW] `sm_draw_stone -funnel/-exlimit/-utmat` are no-ops.** `CmdSmDrawStone` handles only `-border`. Grown-cave edges lack the final undiggable stone rim (`-exlimit`) and unique scape material (`-utmat`); `-funnel` boot border missing. `FUN_0065e090` branches.

**B10. [cosmetic · LOW] Finalize/dead-end prune `FUN_00585390` not ported.** `LevelGenerator.cs:44-46`. Occasional stubby 1-door dead-end pieces the original would delete/cap.

**Orchestration-adjacent (gate visibility, not growth-core):**
- **BA. [MED] DigNotify story-callback fires unconditionally** — missing the `+0xa0!=0` gate (`FUN_005fd060:33`; ctor default 0). `DigglesTclEngine.cs:4481` fires `"<cb> dig shut -1"` always → `newsticker_dig` runs → after ~20 digs prematurely `sm_set_event Uebergang_Urw_Met` + `Generate_enabled 1` → can fire the Umzug early. **Growth path unaffected** (sits outside the sub-gate).
- **BB. [MED] `sm_add_event` is a "0" stub** — ~55 event trigger bindings dropped; quest-log + the trigger-side of Umzug gating broken. `sm_set_event`/`sm_get_event` still work (the `check_uebergang` path survives). Growth unaffected.
- **BC. [LOW] `sm_dig_message` setter stub** (subset of BA).

---

## PART C — 1:1 IMPLEMENTATION PLAN (no stubs, no hardcode, phased)

**Ordering principle:** each phase is independently Play-testable and ordered by symptom impact × dependency. B1+B2 are one atomic change. Do NOT start B2 without B1.

### Phase 0 — Composite door load (B0) — *land + verify what's already in-tree*
- Keep `LevelGenerator.cs:1125-1171` as-is (faithful to `FUN_005826d0:86/:146+`, `FUN_00583080:83`).
- **Verify**: run the two authored EditMode tests (`SplitTemplate_LoadsMergedEntDoors`: vodo_002→2, vodo_008→4, vodo_004→1, vodo_005→2; `Composite_VoodooDorf_PlacesWithoutBuriedSeam`: all 7 place, only (116,46) open) under the project's Unity editor (6000.0.62f1 — not available in the RE sandbox). Then Play-verify a voodoo_dorf/einsiedler actually appears.
- **Follow-up (B8, defer)**: implement `mat` type-0 rect parse → terrain paint pass.

### Phase 1 — Story selection fidelity (B1 + B2 + B6) — *implements `FUN_005fa9a0` + `FUN_005fac90` selection; ATOMIC; highest not-done lever*
Edit `CmdSmGetLevelInfo` (`DigglesTclEngine.cs`):
1. **Delete line 4106** (the non-native high-depth cutoff).
2. **Reorder + add the fit test with fall-through** (`FUN_005fa9a0:76/:80`, dice-before-fit preserves the `:641-642`→`:781` draw order):
```csharp
if (weight < ctx.StoryRng.NextDouble) continue; // dice FIRST (:641-642,:675)
var (tw, th) = StoryTempSize(ctx, rec.Name);
int sW = tw > 0 ? StoryCell(tw + 8) + 1: 1; // FUN_005fa9a0 seed W (local_1c)
int sH = th > 0 ? StoryCell(th + 8) + 1: 1; // seed H (local_34)
if (sW > rw || sH > rh) continue; // FIT + FALL-THROUGH (:76/:80)
ctx.StoryUtemp = rec.Name; seedW = sW; seedH = sH; break;
```
   (`rw/rh` are the stage-1 raw free rect already in scope, story cells = native `freeRectW/H`; the fit alone dissolves the einsiedler monopoly, making the deleted cutoff unnecessary.)
3. **Split the gates** (`:4107-4120`): `-mindist` = ref-must-be-placed (degenerate distance); `-middist` = independent center-**exclusion**:
```csharp
if (rec.MinDistVal > 0f && rec.MinDistRef.Length > 0 && !RefPlaced(ctx, rec.MinDistRef)) continue;
if (rec.MidDist > 0f && Math.Abs(cx - ctx.StoryMapW/2)*4 < rec.MidDist) continue; //:203-215/:220
```
- **No hardcode**: `sW/sH` derive from `StoryTempSize` (composite extent from the `sm_def_temp_group` table); `rw/rh` from the live strip expansion.
- **Verify (headless)**: extend the C# harness (below) — at a shallow scan cell only band-appropriate temps win; an oversized utemp in a small rect returns `-1` (normal); feen is no longer selected in rects too small for it; einsiedler places when the rect fits and falls through when it doesn't; narrow-band uniques survive past their band until placed. Assert the selection-dice draw count == candidates-examined.

### Phase 2 — Utemp rect re-place + placement modes (B5) — *implements `FUN_005fc0a0` + `FUN_005fa9a0` claim-modes; unlocks story-cave variety*
- Recover the exact `FUN_005fc0a0` watermark clamps (`+0x58..+0x64`) and the `FUN_005fa9a0` case-0(random)/case-1(centered) branch selector from the decompile; port the cursor-anchored, dig-direction-sized rect for utemp generations, keeping the non-utemp early-return (`:27-29`).
- **No stub**: the case-0 random placement is the dominant variety source (A10) — it must draw its position exactly as `FUN_005fa9a0` case 0 (feasible-band random), not the current centered clamp.
- **Verify**: headless diag (already emits `feen dir=1 places AWAY-OK, started=1`) extended to assert varied poses across seeds (reproduce R1 Exp I: ≥~20/60 distinct); Play-verify voodoo_dorf/einsiedler land away from the breakthrough and connect.

### Phase 3 — Corridor piece tables (B3) — *implements `FUN_00587480` piece source*
- RE the population site of `+0xac/+0x154/+0x130` (search xrefs to `+0xac` in the CLevelGenerator ctor / an `lg_*` setter; likely the **Gng** ("Gang"=corridor) subset, not the full Gsg/Hol pool). Build `_corridorBuckets` from THAT set, replacing the `_pool` scan at `LevelGenerator.cs:761-772`.
- Port the direction/variation pass (`0x588ce1..0x5890c6`) with the real `DAT_00737058`/`DAT_007376dc`/`DAT_00c3c9b8` constants (currently guessed `_varRate=1/_varK=250` at `:731-732`) — extract from `.data` (needs a live in-corridor dump capture; R6 could not read them at rest).
- **Interim (no regression)**: restrict superset fallback to pieces whose extra doors are cappable, and make an unroutable corridor **cap-and-continue** rather than `Fail` the whole generation.
- **Verify**: harness asserts every routed corridor is fully mated (no uncapped door) and connects src↔dst; Play-verify a placed story cave's tunnel is continuous.

### Phase 4 — RNG faithfulness (B7) — *`FUN_004454d0`+`FUN_0049c670`; LOW priority, honest scope*
- Implement MT19937 exactly (`sgenrand` mult 69069 + `genrand`), expose as the worldgen RNG.
- **Honest caveat (state explicitly, do not over-promise):** bit-exact reproduction of a *specific original playthrough is unachievable* — it requires the shared-stream interleaving of every engine system (map-init alone burns ~512×640 draws) AND a captured original trace, neither of which exists (R6: both dumps idle). To keep per-session variety without the "same caves" bug, seed `MT(0x1105)` then **prewarm by a bounded per-session draw count** (or route the port's other RNG consumers through the same MT to emulate decorrelation). Value delivered: faithful algorithm + draw discipline + a deterministic seed for the harness. **This phase does not change monotony** (A10) — schedule it last.

### Phase 5 — Cosmetic fidelity (B8/B9/B10)
- `mat` type-0 rects → terrain paint (`FUN_00650f90:424-434`, CLevelGenerator `+0x17c`).
- `sm_draw_stone -funnel/-exlimit/-utmat` (`FUN_0065e090` branches) → boot funnel border + post-growth stone rim + unique scape material.
- `FUN_00585390` finalize/dead-end prune.

### Phase 6 — Orchestration-adjacent (BA/BB/BC)
- Gate the DigNotify story-callback behind `+0xa0` (default false) — stops the premature Umzug (`DigglesTclEngine.cs:4481`, `FUN_005fd060:33`).
- Implement `sm_add_event` trigger bindings (quest-log + Umzug trigger side).
- Real `sm_dig_message` setter.

---

### The test harness — WARRANTED; build a RULES/INVARIANTS-conformance harness (not a trace-diff)
**Why not a byte-diff against the original:** R6 proved both minidumps captured the generator **idle** (post-ctor defaults; no in-flight/completed level), and the shared-stream RNG interleaving is unreproducible — so **there is no original generation trace to diff against**, and there cannot be a bit-exact stream comparison. A trace-diff harness is **not** the right tool.

**What IS warranted and sufficient** — a standalone C# harness (extend R1's existing probe at a scratch probe (not in the repository), a dotnet-8 build of the real `LevelGenerator.cs` + `CmdSmGetLevelInfo` against the original data folder), because the **original's authored inputs (`.pmp`/`.ent`/`.tcl`) ARE the ground truth** and the placement laws are fully RE'd:
- **Seed:** a **fixed `MT19937(0x1105)`** (or, until Phase 4, a fixed `System.Random`) for reproducibility. Do NOT seed from the dump — the dump's mid-session state (index 450/14) has no matching original level to compare.
- **Invariants to assert** (each = a decompiled law):
  1. Every successful generation is **fully closed** — no open frontier door except authored entrances (`FUN_00589790:262`).
  2. No **buried** door in any placement (`FUN_00584120`).
  3. Composites place **all** sub-pieces (voodoo_dorf→7 with only (116,46) open; einsiedler→6 with (66,24)+(132,34) open — R5's independently-computed numbers from the real `.ent` bytes).
  4. **Selection**: at band D only band-appropriate temps win; an oversized utemp in a small free rect → `-1`/normal (`FUN_005fa9a0`); feen not selected in rects too small for it; einsiedler falls through when it doesn't fit and persists past its band until placed.
  5. **Draw discipline**: 0 for forced start, 2 per non-forced seed attempt (≤16), 1 per viable cost candidate, 1 per selection-candidate examined (A9).
  6. **Pose variety** present (reproduce R1 Exp I/H: varied pose → ≥~20/60 distinct clusters; SEED mode → ~50/60).
  7. Every routed corridor mated + connected (Phase 3).
- **Build/run:** `dotnet build` the probe project (already green in R1/R5); assert with xUnit/NUnit or the existing `LevelGenTests.cs` EditMode suite (`SplitTemplate_LoadsMergedEntDoors`, `Composite_VoodooDorf_PlacesWithoutBuriedSeam` already authored). Reuse R5's `sim_placement.py` as the cross-check oracle for the door-mating invariants.

**Net:** B0 (done, verify) + Phase 1 (fit test + cutoff removal + middist) are the two changes that convert the port from "monotonous plain caves + feen-priority + composites-never-generate" to the original's interleaved varied story/plain cadence; Phases 2-3 restore correct utemp pose/variety and corridor connectivity; Phases 4-6 are fidelity/adjacent and explicitly not monotony levers.
