> 📜 RE SOURCE REPORT (4 of 6,). Disasm facts are ground truth; port-status notes PRE-DATE
> the 1:1 rebuild (43805a0). Consolidated + current status: **Docs/WORLDGEN_ORIGINAL_RE.md**.

I now have a fully ground-truthed picture — the entire trigger→consume→generate→stamp→re-arm chain verified against the decompile, plus the boot, the failcount law, the story-progress gating, and every port binding. Here is the complete spec.

---

# TCL-SIDE ORCHESTRATION OF THE DIGGLES LEVEL GENERATOR — COMPLETE CONTROL-FLOW SPEC

Scope: `classes/story/story.tcl` (the sole driver), `gameplay/CampaignInit.tcl`, `gameplay/Campaign.tcl`, `init/lginit.tcl`, `init/lgtools.tcl`, and every `sm_*`/`lg_*` native they call. Every native FUN_ marked **[BV]** = byte-verified against the decompile *this pass*; **[doc]** = cited from `Docs/LEVEL_GENERATOR.md` / port comments, consistent but not re-opened this pass.

Port file throughout: `Assets\Diggles\Runtime\Tcl\Engine\DigglesTclEngine.cs` (+ `World\LevelGenerator.cs`).

---

## 1. BOOT / REGISTRATION SEQUENCE

```
systeminit.tcl:27 call classinit.tcl → def_class StoryMgr (loads story.tcl body; class only, no instance yet)
systeminit.tcl:36 call lginit.tcl → lg_* TEMPLATE REGISTRY (see below)
── New Game ──
GameScripts/single_CampaignX.tcl:4 call_method $sm load_level Campaign
   story.tcl:1139 method load_level:
     :1140  call CampaignInit.tcl            → sm_* WORLD/ZONE REGISTRY + events (see below)
     :1141  call Campaign.tcl                → level boot + FIXED start area
```

**lginit.tcl** (the `lg_*` config; runs UNCHANGED):
- `:1` `if {[get_mapedit]} return` — skip in map editor.
- `:5` `lg_addfilterclass {Zwerg Wuker Troll Pilz Kohle Kristallerz Golderz Eisenerz Schatztonne}` — the object classes the obj-count/objfilter system tracks.
- `:7-41` `LG_set_templategroup <Urwald|Metall|Kristall|Lava>` — the recursive glob helper (`lgtools.tcl:1-73`) expands `'tcl/urw_gng_*'` etc. into flat dotted groups (`Urwald.Std.Gng/.Gsg/.Hol`) of bare `.tcl` filenames, then per group calls native `lg_set_templategroup <name> {files}`; `.hol`/`.gsg` groups additionally get `lg_set_templategroupprops -levelcount 1` (`lgtools.tcl:65-70`).
- `:45-50` `LG_set_templateprops {{urw_gng_050_a 1 1 -} …}` → `lg_set_templateprops <name> -gamecount 1 -levelcount 1` (`lgtools.tcl:75-100`) — the four object set-piece caves (Wuker/Schatztonne) get `-gamecount 1` (the intentional cost discount; see §7).

**CampaignInit.tcl** (the `sm_*` world/zone registry):
- `:34` `sm_create_map 512 640` — allocates the (512/4)×(640/4)=128×160 story-cell grid.
- `:36` `sm_set_digcount 32` — the breakthrough WEIGHT threshold (NOT resolution).
- `:79-586` `sm_def_temp_group <name> {{dx dy subtpl}… [{mat …}]}` — ~30 UNIQUE-COMPOSITE definitions (voodoo village, einsiedler, trollburg, the uebergang transitions, titanic, lorelei, …). Content of the unique temps.
- `:589-670` `sm_add_event <Name> [Trigger] [msg]` — ~55 story/quest events + trigger bindings.
- `:675-722` per-zone: `sm_add_zone "<Zone>"` then `sm_add_temp <name> [lo hi] [-opts]` — the depth-banded unique-temp registry (see §6). Opts seen: `-exclusive -zoneborder -stoneidx -mindist -middist -nonrelevant -digconnect -connect -checkalign`.
- `:731-786` `lappend newslist {…}` — the trigger→newsticker/quest table, incl. `{{dig} {dig dig}}` (the dig callback → newsticker_dig).

**Campaign.tcl** (the level; the generator is NOT used here):
- `:2` `map create 512 640`.
- `:3-4` `sm_draw_stone -border` / `-funnel` — paints the undiggable world-edge stone bands.
- `:12-13` `call urw_unq_start.tcl` / `urw_unq_hoehlenmalerei.tcl` — the FIXED start caves.
- `:22-191` `proc first_level` + `:195 first_level 208 0` — the hand-written, irandom-shuffled initial Urwald (cavelist/comblist/tunnlist/vodolist), ZERO `lg_*` calls; each piece is `sm_mark_temparea $x $y $tpl` (`:184`) then `MapTemplateSet`.
- `:20` `sm_set_temp {{urw_unq_start 208 0} {urw_unq_hoehlenmalerei 30 23}}`.

**StoryMgr instance construction** (`story.tcl:20-1024` `obj_init`): defines all procs; sets `resolution [sm_get_resolution]` (=4, `:953`), `failcount 0` / `maxfailures 4` / `gencount 0` (`:937,944,945`), `midx 8` (`:943`), the `*_oc_*` object targets (`:965-988`), `Uebergang_*/UR_WarningCnt_*/UR_WarningTimer_*` (`:990-1003`), `StartCnt 5` (`:1011`), `Generate_enabled 1` (`:1009`); `sm_register this "callback"` (`:1020`); `state_reset` + `state_triggerfresh this idle` (`:933-934`).

---

## 2. THE IDLE PUMP (`story.tcl` state idle,:1030-1133) — ticked every 10 Hz logic tick

The port drives this via `DigglesGameSim.Step` → `DigglesTclEngine.TickEntityState` (`DigglesGameSim.cs:250`), for every enabled stateful entity — so the StoryMgr `idle` body runs each tick. Per-tick order:

1. `:1032-1036` **StartCnt warm-up**: 5×`wait_time 1.0` before anything.
2. `:1041-1048` **GameOver check** every 6 ticks (no player gnomes → `GameOver`).
3. `:1051-1099` **Umzug execution**: if `Reloc_count>0`, relocate the flagged gnomes/rings/production and `map move 0 4` the world down (one row/tick).
4. `:1101-1112` **check_uebergang ×3** (Urwald→Metall, Metall→Kristall, Kristall→Lava zone transitions) — see §6.
5. `:1114-1117` `if {$Generate_enabled==0}` wait — **DEAD gate** (`Generate_enabled` is only ever set to 1 in shipped scripts; the `set …0` lines are all commented, `:524/540/556`).
6. `:1119-1122` `if {[load_next]} { gamedelayannounce 15; return }` — **ONE template stamp per tick**; if a piece was streamed, the tick ends here (does NOT scan).
7. `:1123-1130` only when the buffer is EMPTY: `set area_list [sm_scan]`; `if {$area_list != 0} { generate_next … }`.
8. `:1132` `wait_time 0.5` — `wait_time` (`:863-866`) = `state_disable this` + `action this wait $t {state_enable this}` — the action system is the pacer; the port's WaitAction drives it headless too.

Net cadence: dig accumulates pressure → ONE breakthrough → ONE generation (one seed cluster) → its 3-15 pieces stream one per 0.5 s → dig again for the next.

Note: `proc get_next_areas` (`:24-49`, the gnome-frontier scan) is **DEAD CODE** — defined but never called; the live scanner is native `sm_scan`. (This resolves the doc's "sm_scan source ambiguity" RISK: native `sm_scan` = the DigNotify latch consumer, not the gnome scan.)

---

## 3. THE DIG → GENERATION CONTROL FLOW (the core chain)

```
dig_apply (DigglesTclEngine.cs:1340) ← native DigApply FUN_006347a0:103, BEFORE the carve
  └─ StoryDigNotify = CStoryMgr::DigNotify FUN_005fd060 [BV]
        • bounds gate ≥8 tiles from every edge      (:29-32)
        • fire story callback "<cb> dig shut -1"    (:33-64, gated on +0xa0 — see §8 gap #4)
        • CLEAR latch +0x88/8c/90/94                (:66-70)   ← only the LATEST breakthrough survives
        • mark dug story cell grown FUN_005fa510    (:71-73)
        • nibble gate on dug tile (n&8)==0 && n!=0  (:75-77)
        • probe ±4 neighbours +x,+y,−x,−y:
             first un-grown + nibble-passing dir bumps weight +0x98
             (+2 +x, +4 +y, +2 −x, +1 −y via FUN_005fcfb0)     [BV]
             weight > digcount(+0x9c) ⇒ LATCH {dir,x,y}, consumed=0  (:89-158)
        ── one bump max per dig, early-return after latch ──
```
↓ (idle tick, buffer empty)
```
sm_scan = CTclGlobalMethods::SMScan FUN_006616d0 [BV]
   latch invalid (+0x88|8c|90==0 OR +0x94!=0) → Tcl int 0 (:52-65)
   else → {(+0x8c)/4, (+0x90)/4} (the /4 NEIGHBOUR cell) + set consumed +0x94=1 (:74-103)
   ⚠ does NOT reset the weight +0x98
```
↓ `generate_next [lindex $area_list 0] [lindex $area_list 1]` (story.tcl:1128)
```
generate_next {x y} (story.tcl:125-289) ── x,y are CELL coords ──
:128 zone = sm_get_zone (y*resolution) [FUN_005fcdf0 doc]
:136-138 lg_tp_clear; lg_tp_addtemplatesets "$zone.Std"; lg_tp_objfilter {Troll Zwerg}
:142-145 lg_set_templategroupvalue $zone.Std.Hol <0.287|0.185|0.077|0.105>
:152 linfo = sm_get_level_info $x $y = FUN_00661340→FUN_005fac90 [BV wrapper]
          → free-rect fit + unique-temp pick; returns "%i %i %i %i %s" = x y w h utempName
             (rect from singleton +0x40/44/38/3c; utemp name +0x50)          (00661340:136-138)
:155 if w*res<16 || h*res<16 → sm_map_set orgx orgy 1 1; return (too-small abort)
:163 linfo = lg_set_starttemplatepro 1 1 = FUN_00650f90 [doc, byte-verified in Docs]
          → walk-anchors a START PIECE one+ cell PAST the breakthrough; re-places the rect
             around the utemp+dig distance; registers forced records; returns "x y w h utemp"
             (walk fail ⇒ utemp="nodigmark")
:166-175 utemp/x/y/w/h ← linfo; xn,yn,xp,yp = rect*res
:181 if xp-xn<16 || yp-yn<16 → sm_map_set orgx orgy 1 1; return (too-small abort)
:188-190 if utemp!="" → lg_tp_setfilter "$zone.Std.Gsg" (REMOVE the Gsg ring pieces from the pool)
:191-195 if utemp=="nodigmark" → sm_map_set orgx orgy 1 1; return (anchor failed)
:197 define_obj_counts → per-class lg_set_objcount deficits (:59-90)
:202-207 optratio=0.10; lg_set_templateratio 0 0.10 1.0; lg_set_leveltype base
:208 lg_start = FUN_00589790 [doc] (seed→greedy dock→full-closure)
:209 level = lg_get_level = FUN_00652d10 [BV] (emits {name x y}³; MARKS + RE-ARMS, §4)
:212-222 ── FAILURE HANDLING (see §4) ──
:224 incr gencount
:227-228 sm_draw_stone -exlimit; sm_draw_stone -utmat (post-growth stone/material — PORT NO-OP)
:229 sm_set_temp $level = FUN_00660a80→FUN_005fa320 [doc] (marks uniques used)
:230 fill_temp_buffer $level → lappend temp_buffer + lg_sort_level (nearest-first)
:238-284 foreach placed piece: SPECIAL HOOKS (zone shift + chained fill; §6)
:287 sm_map_set orgx orgy 1 1 (mark the scan cell grown)
```
↓ (subsequent idle ticks, buffer non-empty)
```
load_next (story.tcl:291-327) ── ONE piece/tick ──
:293 buffer empty & !bSplit → return 0
:296-299 get_ingame_loading==1 → return 1 (already streaming)
:304-310 pop temp_buffer[0] → {template x y}; lrem temp_buffer 0
:314 call "data/templates/tcl/$template" (defines proc MapTemplateSet)
:322-324 set_ingame_loading 1; catch {MapTemplateSet $x $y}; set_ingame_loading 0 ← THE STAMP
        MapTemplateSet body → map_template <base>.pmp x y  (CmdMapTemplate:7231)
                            + map_setlayer2 x y <l2m>       (CmdMapSetLayer2:7248)
                            + new <Class> …                 (RuntimeWorldSpawner)
```

Both aborts and the streamer verified faithful in the port (CmdSmGetLevelInfo, CmdLgSetStartTemplatePro, the lg_start/lg_get_level bindings at DigglesTclEngine.cs:3473).

---

## 4. THE FAILCOUNT / RETRY + RE-ARM LAW (the pacing crux)

**Tcl side** (`story.tcl:211-225`):
```
if {[llength $level] == 0} {; # generation produced NO placements
    if {$failcount >= $maxfailures} { set failcount 0 }   ; # maxfailures=4
    incr failcount
    return                                    ; # NO sm_map_set → the scan cell stays UN-grown
}
set failcount 0
incr gencount
```
A failed generation does **not** mark the cell grown and returns early — so nothing is consumed. (The commented `try_raw_level`/`lg_mark_area` fallback at `:215-217` is DEAD.)

**Native re-arm** — `lg_get_level` = **FUN_00652d10 [BV]**:
- `:73` `local_30 = FUN_005f8080` — the **StoryMgr singleton** (the SAME object DigNotify bumps), **not** the generator.
- `:76-290` per-placement emit loop: for each placement it emits the `{name x y}` triple, marks its story rect grown `FUN_005fa510` (`:276-279`), bumps the object-bearing session counter `+0x50` (`:84-93`), and — the crux — **`:280 *(local_30 + 0x98) = 0`** zeroes the StoryMgr's dig-pressure weight.

Consequence, ground-truthed:
- A generation that **EMITS** placements → weight reset → the next breakthrough needs a fresh 32 pressure (paced growth).
- A **FAILED** (empty) generation → the loop body never runs → **`:280` never executes → weight survives** → the very next qualifying dig re-latches → `sm_scan` fires again next buffer-empty tick → retry with fresh RNG state. **This IS the failcount retry channel.** `sm_scan` (FUN_006616d0) deliberately does NOT reset the weight, so the retry is possible.

Port: correctly mirrored — `lg_get_level` binding resets `ctx.StoryDigWeight = 0` and calls `CommitSessionEmission` **only when placements were emitted** (`DigglesTclEngine.cs:3445-3471`). (The historical "`:280` = clear objcount" misread caused a new cave per dig swing; fixed.)

---

## 5. STORY-PROGRESS GATING — CHANNEL A: template eligibility & zone advancement

**Depth-banded eligibility.** Each `sm_add_temp <name> <lo> <hi> -opts` (CampaignInit `:677-722`) registers a unique/composite with a zone-relative Y-DEPTH band `[lo,hi]` (tiles), a zone tag (the most-recently `sm_add_zone`'d zone), and gating flags. In `sm_get_level_info` (FUN_005fac90) a temp competes only when the scan cell's world-Y falls in its band; a per-band probability ramp then picks one. As the colony digs DEEPER, deeper temps unlock in registration order, e.g. Urwald:
```
urw_der_einsiedler 56-60 → urw_unq_voodoo_dorf 60-110 → urw_unq_feen 60-160
→ urw_unq_troll_012/014 60-88 → urw_trollburg_ring 88-128 → urw_uebergang_swf 132-164
```
**Dependency chaining** via `-mindist <refTemp> <val>`: the deeper temps require an earlier one already placed (native distance term is degenerate ⇒ effectively "ref must be placed"). E.g. `urw_uebergang_swf … -mindist urw_trollburg_ring 0.1` — the transition can't generate until the trollburg ring exists. This SEQUENCES the story. `-exclusive 1` = place once; `-nonrelevant P` = flat probability P; `-middist V` = allow near the map's centre column even if the ref is unplaced.

**Zone advancement** — the `sm_set_zone` shift (the actual progression to the next world). When the uebergang COMPOSITE generates, its sub-templates stream in; `generate_next`'s foreach detects the boundary sub-piece and shifts the zone bound:
```
story.tcl:241 urw_unq_metalltor.tcl → sm_set_zone 0 [expr $y+8]; Urwald→Metall
        :243  swf_unq_bruecke.tcl    → sm_set_zone 1 [expr $y+8]   ; Metall→Kristall
        :245  lava_unq_start.tcl     → sm_set_zone 2 [expr $y+8]   ; Kristall→Lava
```
`sm_set_zone i y` sets zone i's UPPER Y-bound (initially all −∞ ⇒ whole world = Urwald). After the shift, cells below `y+8` resolve (`sm_get_zone`) to the next zone → its `swf_*`/`kris_*`/`lava_*` pool + uniques become eligible. The foreach also bootstraps the boss caves via chained `fill_temp_buffer` (`:257-275`, fenris etc.) and links troll/voodoo pairs via `sm_set_temp` (`:249-255`).

Port status: `sm_get_zone`/`sm_set_zone`/`sm_add_zone`/`sm_add_temp`/`sm_def_temp_group`/`sm_set_temp` all REAL and faithful (bands, `-mindist` chain, composite-completion). The generate_next foreach runs UNCHANGED, so the shift chain works — **conditional on the uebergang composite actually placing**, which the port flags as fragile (§8 gap #1/#2, the "UTEMP … NOT PLACED" instrumentation).

---

## 6. STORY-PROGRESS GATING — CHANNEL B: quest events & the Umzug (world-shift)

Distinct from template generation. Driven by `sm_add_event` bindings + the `newslist` + `check_uebergang` + physical `map move`:

- Quest triggers fire → the sm_register'd StoryMgr `callback` (`:398-442`) matches `newslist` entries → `newsticker_new/upd/del/ful/spec/dig` (quest-log) and set `QL_*` / `sm_set_event` flags.
- The transition `spec` entry (e.g. `{Trigger_urw_unq_metalltor_oeffnen} {spec Ueber_0}`, CampaignInit`:752`) → `newsticker_spec Ueber_0` (`:523-537`) starts a countdown `UR_WarningTimer_0 = gettime+2400`.
- `check_uebergang` (`:613-760`, idle`:1101`) then: (WTim block `:719-758`) once gnomes have moved below the transition marker or the timer expires, `sm_set_event Uebergang_Urw_Met` (`:743`); (Ueb block `:621-717`) next tick, with the event set AND the `Info_Pos_Uebergang_UM_*` markers present, it flags gnomes/rings/production for relocation and sets `Reloc_count` → idle `:1051-1099` `map move`s the world down (the "Umzug").
- `Ueber_3` (`:569-591`) = the final teleport to the endgame.

The dead dig-counter relic: `newsticker_dig` (`:763-798`) would set the uebergang event after 20 qualifying digs — but its driver (the DigNotify callback) is **disabled by default** (`+0xa0=0`, FUN_005fa7c0:33 **[BV]**) and NO active `sm_dig_message 1` exists in the shipped data (all three are commented; only `sm_dig_message 0` at `:792` is live). **So natively this path never runs** — the story advances via the quest triggers above.

---

## 7. COMPLETE `sm_*` / `lg_*` → NATIVE FUN_ MAP + PORT STATUS

`sm_*` StoryMgr layer:

| Command (caller) | Native | Port status |
|---|---|---|
| `sm_get_resolution` (story:953) | FUN_0065ffb0 → **returns 4** **[BV]** | ✅ real, returns "4" (3524) |
| `sm_set_digcount` (CampInit:36) | FUN_0065c750 → singleton +0x9c **[BV]** | ✅ `ctx.StoryDigThreshold` (3520) |
| `sm_create_map` (CampInit:34) | FUN_0065f430 [doc] | ✅ + boot reset (7202) |
| `sm_scan` (idle:1123) | FUN_006616d0 (latch consumer) **[BV]** | ✅ faithful (3627) |
| DigNotify (dig_apply) | FUN_005fd060 **[BV]** + FUN_005fcfb0 **[BV]** | ✅ growth path faithful (4474); ⚠ callback gate (gap #4) |
| `sm_get_level_info` (gen:152) | FUN_00661340 **[BV]** → FUN_005fac90 [doc] | ✅ real; ⚠ utemp-select order approximated (gap #2) |
| `sm_get_zone`/`sm_set_zone`/`sm_add_zone`/`sm_force_zone` | FUN_005fcdf0 / zone reg [doc] | ✅ real (3527-3550) |
| `sm_add_temp` (CampInit) | FUN_0065cba0 [doc] | ✅ real, all flags parsed (3998) |
| `sm_def_temp_group` (CampInit) | (native handler not re-confirmed this pass) | ✅ stores spec; consumed by composite expand + `-utmat`(no-op) (3555) |
| `sm_set_temp` (gen:229) | FUN_00660a80→FUN_005fa320 [doc] | ✅ real + composite completion (3560) |
| `sm_get_temppos` | (per doc) | ✅ real (3592) |
| `sm_mark_temparea` (Campaign:184) | FUN_00653350 [doc] | ✅ real (3606) |
| `sm_map_set` / `sm_map_get` | FUN_00660110→FUN_005fa510 / FUN_005fa5d0 [doc] | ✅ real rect-mark / OOB=1 (3376) |
| `sm_register` (story:1020) | [doc] | ✅ real (3379) |
| `sm_draw_stone -border` (Campaign:3) | FUN_0065e090 [doc] | ✅ real (bands + story-mark) |
| `sm_draw_stone -funnel/-exlimit/-utmat` (Campaign:4; gen:227-228) | FUN_0065e090 branches [doc] | ⚠ **NO-OP** (gap #5) |
| `sm_set_event` / `sm_get_event` | [doc] | ✅ real dict (2909) |
| `sm_add_event` (CampInit ~55×) | [doc] | ❌ **"0" STUB** — trigger bindings dropped (gap #3) |
| `sm_dig_message` (story:792) | FUN_0065c500 → singleton +0xa0 **[BV]** | ❌ **"0" STUB** (gap #4) |
| `sm_log` / `sm_music_theme` / `sm_send_message` | — | ⚪ stub/no-op (cosmetic) |

`lg_*` generator layer (all → `LevelGenerator.cs`; config setters + pool filters + `lg_start`/`lg_get_level`/`lg_sort_level` all ✅ real, DigglesTclEngine.cs:3389-3480):

| Command | Native | Port status |
|---|---|---|
| `lg_addfilterclass`, `lg_set_templategroup[props]`, `lg_set_templateprops`, `lg_set_template[group]value`, `lg_get_temp_size` | config setters [doc] | ✅ real |
| `lg_tp_clear/addtemplatesets/setfilter/objfilter` | pool filters [doc] | ✅ real (`setfilter` = REMOVE named group) |
| `lg_set_area/objcount/templateratio/leveltype` | FUN_00652830 (ratio) [doc] | ✅ real |
| `lg_set_starttemplatepro` (gen:163) | FUN_00650f90 [doc, BV in Docs] | ✅ real; ⚠ deep approximations (gap #1) |
| `lg_start` (gen:208) | FUN_00589790 [doc] | ✅ faithful port |
| `lg_get_level` (gen:209) | FUN_00652d10 **[BV]** | ✅ faithful incl. `:280` re-arm |
| `lg_sort_level` (fill_temp_buffer:112) | [doc] | ✅ real |
| `lg_mark_area` (story:179/217 COMMENTED) | [doc] | ⚪ no-op (never called live) |
| `lg_dec_templatecount` | FUN_00654fa0 [doc] | ⚪ stub (never called by shipped scripts) |

---

## 8. PORT TCL-SIDE GAPS, RANKED

**#1 — HIGH — `lg_set_starttemplatepro` (FUN_00650f90) deep approximations.** The port ports the pre-step, the walk/start-piece pick, and the FUN_005fc0a0 rect re-placement, but explicitly flags three unknowns (CmdLgSetStartTemplatePro, DigglesTclEngine.cs:4203-4212, 4269-4345, 4375-4390): the exact FUN_005fc0a0 watermark clamps + FUN_005fa9a0 claim-modes; the utemp positioned at the walked pose vs the native's AWAY-positioning; and the FUN_00587480 corridor router (registered but un-RE'd). Effect: unique/story caves may place differently or fail their pose probe ("UTEMP NOT PLACED",:4455). This is the likeliest cause of story caves generating wrong or not at all.

**#2 — HIGH — `sm_get_level_info` unique-temp SELECTION order (FUN_005fac90).** The band HIGH-gate + zoneborder-exempt heuristic (CmdSmGetLevelInfo:4100-4106) is admitted "empirically derived; exact native selection order flagged for follow-up (hit the Fable spend limit)". A wrong pick at a given depth shadows the correct story temp → wrong or missing story caves; directly feeds #1.

**#3 — MEDIUM — `sm_add_event` is a "0" stub.** CampaignInit registers ~55 events, most with trigger bindings (`sm_add_event <Name> <Trigger> [msg]`,:589-670) that auto-set the event when the trigger fires; the stub drops the binding. Quest-log (newsticker) and the trigger-side of the Umzug event-gating are broken. `sm_set_event`/`sm_get_event` still work, so explicitly-set events (incl. the check_uebergang path) survive. **Growth generation is unaffected** (uses depth bands + `sm_set_zone`).

**#4 — MEDIUM — DigNotify story-callback fires unconditionally (missing the `+0xa0` gate).** `StoryDigNotify` (DigglesTclEngine.cs:4481) fires `"<cb> dig shut -1"` whenever a callback is registered, but native FUN_005fd060:33 gates it on `+0xa0 != 0`; the ctor defaults `+0xa0=0` (FUN_005fa7c0:33 **[BV]**) and there is NO active `sm_dig_message 1` in the shipped data. So the native dig-callback / dig-warning counter is DEAD, but LIVE in the port → `newsticker_dig` runs, and after ~20 digs it prematurely `sm_set_event Uebergang_Urw_Met` + `Generate_enabled 1`, which can fire the Umzug early once the transition markers exist. Fix: don't fire the dig callback (or gate it behind a real `sm_dig_message`-set flag defaulting false). Growth path is unaffected (it sits outside the `+0xa0` sub-gate).

**#5 — MEDIUM-LOW — `sm_draw_stone -funnel/-exlimit/-utmat` are no-ops** (CmdSmDrawStone:3901 handles only `-border`). `-funnel` (a second boot border shape) and `-exlimit`/`-utmat` (post-growth stone-border + unique-material painting over each grown cave, gen:227-228) are skipped → grown-cave edges may lack the final undiggable stone rim / correct scape material. Growth itself works; this is edge/material fidelity.

**#6 — LOW — `sm_dig_message` setter stub** (subset of #4): `sm_dig_message 0` at `newsticker_dig:792` cannot disable the counter, so once #4's premature trigger fires it keeps re-firing. Cosmetic given #4's fix.

**#7 — NEGLIGIBLE** — `lg_mark_area` no-op (story.tcl only calls it in commented code), `lg_dec_templatecount`/`sm_log`/`sm_music_theme`/`sm_send_message` stubs (unused / cosmetic / audio).

**Non-command Play-integration dependency (outside the strict Tcl layer, but it gates visibility):** runtime `MapTemplateSet` stamps must trigger incremental terrain re-mesh + nav re-bake, and the StoryMgr `idle` must be ticked from the sim clock — the latter is confirmed wired (`DigglesGameSim.cs:250`); the incremental-terrain hook is the documented follow-up (`Docs/LEVEL_GENERATOR.md` §INTEGRATION) and determines whether grown caves actually appear/walk in Play.

---

Key correctness anchors re-verified this pass against the decompile: FUN_006616d0 (sm_scan, no weight reset), FUN_005fd060 + FUN_005fcfb0 (DigNotify probe order/weights, latch on `weight>+0x9c`), FUN_00652d10:280 (the re-arm `*(singleton+0x98)=0` inside the per-placement loop), FUN_0065c750 (digcount→+0x9c), FUN_0065ffb0 (resolution=4), FUN_00661340 (level-info return `x y w h name`), FUN_0065c500 + FUN_005fa7c0:33 (dig-message `+0xa0`, default 0).