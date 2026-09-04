# Engine Command Gap — Ranked Stub Report

> **Generated** from a full classification pass over every stubbed Tcl engine
> command in the port, verified against the original game data
> and the decompile.
>
> Companion docs: `ENGINE_TCL_API.txt` (authoritative command reference),
> `TCL_INTERPRETER_PLAN.md` / `TCL_OBJECT_MODEL.md` (interpreter stack),
> the documentation index (`README.md`).
>
> **Current-status correction,** the classification tables and severity counts
> below preserve the audit snapshot and are not a live stub registry. Since that
> snapshot, `get_prod_ownerstrength`, `set_prod_ownerstrength`, `get_fsource`,
> `get_linked_to`, `sm_send_message`, `vector_normalize`, `vector_roty`, `exec_deferred`,
> and `destruct` have real handlers. `class_objsnapclass` now drives native placement mode 7,
> the `-snapobj` event path, and the existing link system. `set/get_placesnapmode` drive the
> wall and ceiling trap laws. Door/path-influence commands and ladder/elevator transport are
> implemented, including cabin call/ride behavior. The fight definition/equipment/query
> foundation is also real: the shipped tables load, `class_fightdist`, equipment selection,
> attack positioning, escape lookup, and per-object fight fields no longer use stub defaults.
> The four `fight_setactions*` commands now run through `FightResolver`, including delayed
> damage/events, callbacks, facing/range results, ballistic state, training, and strikeback.
> The enemy-clan layer is also live: `AiSystem` runs the unchanged `std_ai.tcl` in isolated
> owner scopes, supplies its population/intruder/economy/placement queries, issues real attack
> orders, and processes theft warnings through bidirectional war diplomacy. The EnergyMgr command
> family and player/opponent production gate are now real; `ENERGY_SYSTEM_RE.md` owns their law. Tcl
> `gamesave` and `gameload` are REAL `OriginalSaveGameBackend` routes them
> through the native `.sav` services (byte-exact baseline patch writer for sessions loaded from
> an archive, the no-baseline assembler for fresh campaigns — both accepted by the original
> executable, E2). F2 quicksave / F5 load-newest and 5-minute rotating autosaves ride the same
> backend; saving is asynchronous (capture on the main thread, serialize+gzip+write in the
> background). See `ORIGINAL_SAVE_FORMAT_RE.md`.
> `prodslot_override` is also real: all five subcommands, positional product states, slot-31
> pack/unpack gating and original crossout overlay composition are
> covered by `PRODUCTION_SLOT_OVERRIDE_RE.md`.
> The CFluid command family is now real as well: sources and drains, finite volume budgets,
> stoppers, fill/type/surface queries, 10 Hz flow, water-aware object queries and fish movement,
> and original-texture presentation are covered by
> `FLUID_SYSTEM_RE.md`. Fluid rows below retain their evidence but no longer describe
> the current runtime.
> The original audit also assigned
> the wrong lifecycle law to `destruct`: native `destruct` shatters and hides; native `del`
> owns `obj_exit` and logical removal. Corrected rows below override the snapshot wording.

## What a "stub" is here

The port registers every original engine Tcl command. Commands that are not (yet)
faithfully implemented are registered as **stubs**: they accept any arguments and return
a constant (usually `"0"` or `""`) with **no side-effect**. The bulk-stub block lives in
`Assets/Diggles/Runtime/Tcl/Engine/DigglesTclEngine.cs` (~line 4951); real implementations
are individual `RegisterCommand` calls earlier in the same file (27 return-critical
commands were already de-stubbed in earlier waves).

**The branch rule** (the single most useful triage law): *a `"0"` stub only bugs out when
the Tcl script BRANCHES on its result, or when native/other-script state relies on its
side-effect.* Concretely:

1. **Return branched on** (`if {[cmd...]}`, `foreach x [cmd...]`, arithmetic) → the
   wrong constant steers live logic. Worst class.
2. **Side-effect relied upon** (setter/verb whose effect something else reads — native
   systems, paired getters, pathfinding, UI) → silent state divergence.
3. **Fire-and-forget side-effect nobody consumes** → cosmetic loss only.
4. **Never called by shipped data** → DEAD, the stub is free.

At the time of the snapshot, melee/ranged **combat**, the **AI opponent** (`std_ai.tcl`
enemy-clan brain), and **multiplayer** (`net`, lobby, MP screens) were intentionally inert
for the SP-first port. Combat and opponent behavior are implemented;
multiplayer remains out of scope. Their LOW labels below therefore describe the old triage,
not current runtime behavior.

## Severity counts

| Severity | Count | Meaning |
|---|---|---|
| CRITICAL | **0** | Blocks the core loop (dig/build/produce/task) right now — none found |
| HIGH | **20** | Real live-SP breakage (story beat, building mechanic, progression path) |
| MEDIUM | **54** | Live path but tolerable default, or cosmetic-but-pervasive |
| LOW | **94** | Debug-only, shadowed by Tcl procs, correct-by-default, or combat/MP-by-design |
| DEAD | **153** | Zero call sites in shipped data — stub is free |
| **Total** | **321** | |

No stub currently blocks the core dig/build/produce loop — that loop was de-stubbed in
earlier passes (the stub-shadowing problem).
The dangerous residue is concentrated in: **energy buildings, conquer/repair,
notifications, tech-tree init, story-trigger dispatch, and door/story pathfinding
influence.**

---

## CRITICAL

*None.* Every command whose stub would break the core loop today has already been
implemented. (The classic examples — `obj_query`, `get_walkresult`, `tasklist_add` — are
real; see `ENGINE_TCL_API.txt` + `DigglesTclEngine.cs`.)

---

## HIGH (20)

| Command | Native semantics | Strongest evidence call-site | What breaks while stubbed |
|---|---|---|---|
| `newsticker` (new/change/delete/mask) | Create/update clickable in-game notification banner and retained history; returns ticker id | `baby.tcl:429` `set id [newsticker new [get_owner this] -text "[lmsg zwerggeboren]..."]`; 102 sites across 14 files (z_work_common.tcl:18 inventions,:629 conquests, story.tcl ×47) | **Implemented.** Native defaults/options, owner filtering, category masks, scaled timeouts, history, original toggle art, scrolling HUD presentation, clicks, and runtime snapshot state are covered |
| `destruct` | Shatter the current-frame mesh and clear render visibility; it does **not** delete or call `obj_exit` (`FUN_0067bcf0` → `FUN_004845a0`) | `Trigger_Crystal.tcl:636` `destruct $Holztisch` standalone (no paired `del`) | **Implemented** `del`, not `destruct`, now owns the `obj_exit` chain and logical removal |
| `init_techtree` | Parse generated `gen_tt.tcl` into the native invention-prerequisite graph | `systeminit.tcl:44` `init_techtree scripts/gameplay/gen_tt.tcl` (inside `![minimalrun]` boot block) | Invention dependency graph never built natively; plausible root of the open **invention ordering** bug |
| `exec_deferred` | Schedule a script for execution on the next shell tick | `Trigger_Tournament.tcl:682-685` `exec_deferred {call data/GameScripts/single_Tutorial.tcl}` | **Implemented.** Queue draining uses a fixed snapshot, so nested deferred work waits one additional tick |
| `isunderwater` | Whether a vertex is under water (lava counts as water) — **branched pervasively** | `wasserrad.tcl:36` `if {... && [isunderwater [get_pos this]]}`; also z_events.tcl:1716 (drowning), Lavawelt.tcl:259/290, lager.tcl:103, Trigger_Urwald.tcl:152 | **Implemented.** Uses the BDB fluid boundaries and live normalized fill |
| `prodslot_override` | Override production-slot availability in the GUI (disable/reset/normal/hide/query) | `Trigger_Tutorial.tcl:14` `prodslot_override disable 0..5,31` (+:132/:456/:604/:721; Trigger_Tournament.tcl:53-59) | **Implemented.** Product-list ordinals and the fixed pack slot 31 now hide, cross out, and block input exactly as scripted |
| `get_prod_ownerstrength` | Read building owner-strength 0..1 (conquer/repair rope-pull state) — **branched** | `z_work_common.tcl:313` `if {[get_prod_ownerstrength $current_workplace] < 0.95}` → conquer vs prod_repair (+:618/:642) | **Implemented after the snapshot.** Reads the entity's persisted owner-strength value |
| `set_prod_ownerstrength` | Write building owner-strength | `z_work_common.tcl:619/643/645` `set_prod_ownerstrength $building [fincr strength -$offset]` / `... 1.0` | **Implemented after the snapshot.** Writes the same entity property read by the getter |
| `get_energysourceload` | Activity/load of an energy source — activation gate of every generator | `reaktor.tcl:92/97` (+ laufrad.tcl:92, dampfmaschine.tcl:93, wasserrad.tcl:41) `if {[get_energysourceload this] > 0 && $active==0}` | **Implemented.** Returns the sum of active native-style consumer lease amounts |
| `get_energystore` | Read stored energy of an energysource — **branched**; setter is live too | `z_work_prod.tcl:355` `set e [expr {[get_energystore $current_workplace] + $incval}]`; reaktor/laufrad/dampfmaschine:82-84 | **Implemented.** Generator refill work now mutates persistent source stores |
| `get_fsource` | Read fluid-source property (`-volume` etc.) — **branched** | `Trigger_Urwald.tcl:902` CheckWasser: `if {[get_fsource $wflag -volume] > 50} { return 0 }` | **Implemented.** Reads live merged source fields and remaining finite volume |
| `get_ingame_loading` | True while incremental in-game level loading is in progress — **branched** | `story.tcl:296` `if {[get_ingame_loading]==1} { return 1 }` (load_next reentrancy guard) | StoryMgr world-growth loader loses its "already loading" guard → potential double/overlapping loads |
| `set_doorproperties` | Set who may path through a door (openforclasses/openforfriends) | `tuerundtor.tcl:143/177/217` `set_doorproperties this openforclasses { Troll }`; tueren.tcl:18/58 | **Implemented.** Owner, diplomacy, class, proximity, blocker, and animation transitions are covered |
| `set_elevatorproperties` | Configure elevator range/accel/speed/max users | `transport.tcl:53/125/200` | **Implemented.** Configuration drives typed shaft routing, linear/accelerated cabin motion, and the 16-rider call table |
| `set_pf_influence` | Install a pathfinding cost rect for an object (INT_MAX = impassable) | `doors.tcl:11/27` closed door = `set_pf_influence this -1 -20 +1 +4 INT_MAX 0`; Metallwelt.tcl:12/227, Lorelei.tcl:809 story barriers | **Implemented.** The overlap-safe overlay feeds effective Calc1 block/cost/climb data |
| `class_objsnapclass` | Set obj-snapping class + range (classdef+0xce4/+0xce8, `FUN_00617250`) | `Lorelei.tcl:357` `class_objsnapclass Reflektor 1.5` (+ :823; Kristallwelt.tcl:75) | **Implemented** Mode 7 uses the weighted range, nearest exact-class host, link point 0 preview, and `-snapobj` confirm payload |
| `get_linked_to` | Object this object is linked to (−1 if none) — **branched** | `Lorelei.tcl:28/147/664/761`, `Kristallwelt.tcl:39/382` `if {... [get_linked_to $snapped_obj] != [get_ref this]}` | **Implemented after the snapshot.** The snap event reaches the original host `snapping_action`, whose `link_obj` attachment is live |
| `prod_assignworker` | Announce worker↔workplace binding to the native prod system | `z_events.tcl:1669` evt_zwerg_workannounce: `prod_assignworker this $current_workplace...` | If the dispatcher relies on the announcement, assignments can be lost/doubled (port's CProdPlanner may partly cover it) |
| `sm_send_message` | Post a semantic story-event message to the native StoryManager | `Trigger_Schwefel.tcl:1122` `sm_send_message this "Umzug"` (+ "kill"/"Eisen"; Urwald "RdL", Lavawelt "alleweg"; sequencer.tcl:348...) | **Implemented after the snapshot.** Routes semantic messages through the port's story-message path |
| `vector_normalize` | Return unit-length direction vector — pure math consumed by arithmetic | `hamster.tcl:214` `vector_mul [vector_normalize [vector_sub $ownpos $posvec]] $escape_range`; Lorelei.tcl:102/248/704/726 | **Implemented**, including a zero-vector guard; `vector_roty` landed in the same pass |

---

## MEDIUM (54)

Live paths with a tolerable default, or cosmetic-but-pervasive losses.

| Command | Native semantics | Strongest evidence call-site | What breaks while stubbed |
|---|---|---|---|
| **Energy / production config** | | | |
| `set_energyclass` | Set object's energy class | `schmiede.tcl:326` (~20 buildings' obj_init) | **Implemented.** Higher-class sources may feed equal-or-lower consumers |
| `set_energyconsumption` | Energy need during production | `schmiede.tcl:327` | **Implemented.** Positive values gate and charge production; zero remains free |
| `set_energymaxstore` | Max store of an energysource | `reaktor.tcl:128` | **Implemented.** Stored for the unchanged generator scripts |
| `set_energyrange` | Power radius of a source | `reaktor.tcl:126` | **Implemented.** Uses the native strict horizontal range law |
| `set_energystore` | Write energy store | `z_work_prod.tcl:359` | **Implemented.** Writes the source store read by availability and refill logic |
| `check_energy` | Enough energy for production? (branched) | `std_ai.tcl:1612` | **Implemented.** Player and opponent paths share owner/class/range/build/store readiness |
| `set_prodalloclock` | Lock item against double prod-allocation | `genericprod.tcl:532`, `z_procs.tcl:267` | Native-only consumer; possible double-allocation under CProdPlanner |
| `set_prod_directevents` | Switched (direct) production-event mode | `Lorenbahn.tcl:509`; fallen.tcl:83/264 | Traps/mine-cart mission objects emit default-mode events |
| `set_inventoryslotuse` | Slots an object occupies | `schmiede.tcl:328` (~28 buildings) | Building storage capacity uncapped — touches the boxed-building loop |
| **Fluids** | | | |
| `set_water` | Enable global water surface | `wasser.tcl:8/37/146/174` | **Implemented.** Enables the context fluid simulation and presenter |
| `set_fsource` | Configure a fluid source | `wasser.tcl:15/153/181`; Lavawelt.tcl:40/261 | **Implemented.** Merge semantics, source scale, finite budgets, and negative drains are live |
| `set_fstopper` / `rem_fstopper` | Fluid dam add/remove (doors) | `wasser.tcl:199`; `doors.tcl:21/32` | **Implemented.** Directional permeability gates neighbor transfer |
| `rem_fsource` | Remove a fluid source | `Lavawelt.tcl:267/1191/1197` | **Implemented.** Removes the entity-owned source record |
| `get_fill` | Water fill at (x,y) (branched) | `wasser.tcl:106` → `out_fill` propagation | **Implemented.** Returns the live normalized cell fill |
| **Transport** | | | |
| `create_doorlogic` | Register native door transport node | `tuerundtor.tcl:142/176/216` | **Implemented.** Closed and conditional doors affect routing and open for authorized movers |
| `create_elevatorlogic` | Wire elevator (cabin+rope) | `transport.tcl:52/124/199` | **Implemented.** Cabin/track identity, shaft scan, call/board/carry/dismount action, animations, and teardown are covered |
| `create_ladderlogic` / `set_ladderrange` | Register ladder + reach | `transport.tcl:318-319/363/410` | **Implemented.** Wall scan, projected climb band, depth settle, and dedicated ladder animations are covered |
| `delete_transportlogic` | Teardown of the above | `transport.tcl:295`; genericprod.tcl:235 | **Implemented.** Removes the holder and its navigation influence |
| **World / loader / story** | | | |
| `reset_map` | Clear a map rect before repopulating | `story.tcl:585/916` (growth), `urw_6a.tcl:288` | World-growth correctness partially covered by port's own terrain growth |
| `obj_clear` | Remove all dynamic objects and transient UI selection state | line 1 of every `single_*.tcl`; `sandbox_*.tcl`; deferred TrollQuest campaign restart | **Implemented.** Cancels building placement, snapshots the handle table, runs every entity through the real `del` teardown, clears selection and news rows, and is safe on an empty boot world and inside `exec_deferred`. The old “fresh scene, guard normally moot” verdict was falsified by mod campaigns and the native free-game scripts: a TrollQuest restart previously stacked thousands of entities and sound sources. |
| `set_ingame_loading` | Toggle in-game loading mode | `story.tcl:322-357` around MapTemplateSet | Loading still proceeds; native render-suppression lost (hitches) |
| `set_split_load` | Toggle split (chunked) load mode | `story.tcl:365/372` | Optimization-only toggle on live growth path |
| `set_activegameplay` | Freeze/unfreeze object AI+events | `Lavawelt.tcl:1277` `set_activegameplay $gnome 0` (+ Lorelei, fenris) | Gnomes keep wandering/thinking during story scenes |
| `remove_parked_obj` | Remove one actor's parked actions | `sequencer.tcl:383/501` | restore_obj_actions (real) covers most; stale entries could freeze an actor |
| `set_diplomacy` | Set owner↔owner relation (getter IS real) | `CampaignInit.tcl:4-13`; Trigger_Lava.tcl:138-219 | **Implemented before the combat wave.** One-directional writes, world/self rejection, native defaults, and owner-query consumers are covered |
| `ClassID` | Class name → id (−1 sentinel, branched) | `std_ai.tcl:2281` `if {[ClassID $classname]==-1} break` | AI-planner tier walk never breaks (AI-opponent only) |
| `sel` | Select node context for `new`/`set_event` | `systeminit.tcl:85/94`; sequences | Redundant if port's `new` self-parents; present in live boot |
| `find_free_place` | Find unoccupied spot near pos (result consumed) | `wuker.tcl:206` → `walk_pos` | Wuker critter walks to origin — localized |
| `ground_pos` | Fix vector to valid ground (result consumed) | `wuker.tcl:206` (seed of the above) | Same wuker route wrong — localized |
| **UI / info** | | | |
| `layout` (present/log/clear/print/addlink/autolink/addxlink/autoxlink/reload) | Document-window text/image renderer | `diary_*.tcl`, `onlinehelputils.tcl:88-95` (48+ files) | **Implemented.** Native render context, markup, original TGA images, file/Tcl links, secondary targets, and reload are covered by `DocumentWindowSystemTests` |
| `questlog` | Register quest-journal entry through `onlinehelputils.tcl` | `diary_15.tcl:9-22` ("Find the 6 Rings") | **Implemented.** The real Tcl proc is no longer shadowed, and `textwin info questlog` executes the unchanged journal document |
| `selection` (check/include/clear) | Read/mutate live selection set (branched) | `info_gnomes.tcl:34` `if {[selection check $gid]}` | Doc-panel click-to-select + highlight dead (3 panels only) |
| `set_objicon` | Status/mood icon above object | `z_events.tcl:1937` (hunger icon) | Need indicators absent — informational |
| `set_objworkicons` | Work-activity icon above a working gnome | `z_procs.tcl:692/919`; z_events.tcl:431 | No fetch/mine/carry icons over workers |
| `set_objname` | Set displayed name (`auto` = engine-generated gendered name) | `z_methods.tcl:250-259`, pzwerg.tcl:16/87 | Blank gnome names propagate into UI/newsticker text |
| `smalltalk` (add/get) | Register/fetch idle-chatter lines | `worktalk_en.tcl:27` + hundreds (via talkinit.tcl) | Gnome chatter/talk-bubble pool empty |
| **Visual / rendering** | | | |
| `set_texturevariation` | Pick texture-variation for live object | `zwerg.tcl:151` `[hf2i [random 4]]`; brut, drache | All gnomes/monsters render same variant (ties into clan-appearance task) |
| `set_objvariation` | Pick gfx model variation | `grabstein.tcl:46`, zelt.tcl:40 | All gravestones/tents = variation 0 |
| `generate_color_variation` | Per-tile scape color variation over rect | `Campaign.tcl:6` (EVERY campaign init) | Ground texture flat/uniform on all campaigns |
| `load_scape_texture` | Register ground scape texture per cellId | `txtinit.tcl:41-177` | The faithful texture↔cellId source (ties into the lava-renders-as-moss gap, see memory `reference_terrain_scape_textures`) |
| `set_light` | Object light on/off | `genericprod.tcl:174/188`; schmiede.tcl:315 | Production buildings never glow while working — core-loop-visible cosmetics |
| `set_autolight` | Object auto-emits a moving light | `zwerg.tcl:156` (every gnome) | Gnomes don't light their surroundings (carried-lamp visibility) |
| `set_rendermaterial` | Set render material (additiveonly/default) | `z_methods.tcl:524/539` (cloak) | Cloaked dwarves don't render additive/transparent |
| `set_forceipol` | Force smooth position interpolation | `muetzen.tcl:152` (hats — live); Lorenbahn.tcl:87/512 | Jerky motion on hats/mine-carts |
| `horizon_set` | Colored horizon/sky-tint zone | `infoobjs.tcl:697` (21 calls) | Ambient horizon coloring absent |
| `set_view_begin` | Camera upper scroll clamp | `story.tcl:641`; every Campaign*.tcl | Faithful scroll-clamp lost (port owns its camera) |
| `vector_roty` | Rotate vector about Y | `z_procs.tcl:854` (particle dir by facing); Lorelei.tcl:702 | Particle emission direction {0 0 0}; Lorelei laser vector wrong |
| **Placement / class metadata** | | | |
| `set_placesnapmode` | Placement snap 0=off/1=wall/2=floor (native `FUN_0067d8b0`) | `fallen.tcl:60/245` (traps non-default) | **Implemented** Modes 1/2 select the native wall-seat and ceiling-corner laws with box fallback |
| `set_snaptowall` | Snap-to-wall flag | `transport.tcl:308/355/400` | **Implemented.** Unboxed wall-mounted objects settle to the live hmap relief; boxed/contained products retain their putdown depth |
| `class_snaptowall` | Class-level snap flag | `emitter.tcl:10` | Wall-emitter placement metadata lost |
| `class_flagoffset` | Prod flag offset (x,z) | `bar.tcl:120`, disco.tcl:84 (49 sparetime sites) | Interaction flags at (0,0) — mispositioned markers |
| `get_class_era` | Class's tech era (feeds gen_tt.tcl) | `makettree.tcl:5` → written into gen_tt.tcl | Era flattened to 0 in the techtree data — contributor to invention ordering (fix with `init_techtree`) |

---

## LOW (94)

Grouped — these are debug-only, shadowed by real Tcl procs, correct-by-default constants,
or sat exclusively on combat / AI-opponent paths in the snapshot; multiplayer remains stubbed by design.

| Group | Commands | Why LOW |
|---|---|---|
| Combat action resolution (snapshot row, now implemented) | `fight_setactions`, `fight_setactions_ballistic`, `fight_setactions_strikeback`, `fight_setactions_training` | **Implemented.** The commands select original table actions, preserve callbacks and native range/facing results, drive delayed damage/events, training, ballistic result state, and explicit area strikeback. See `COMBAT_SYSTEM_RE.md` |
| Combat foundation (snapshot row, now implemented) | `fight_action`, `fight_weapon`, `fight_weapon_kombi`, `fight_map_obj2db`, `fight_map_class2db`, `def_hp_ratio`, `class_fightdist`, `set_weapon_class`, `set_shield_class`, `get_attack_pos`, `get_attackinprogress`, `set_attackinprogress`, `get_ballisticresult`, `get_best_weapon`, `get_escape_value`, `get_diedinfight`, `get_velcomp`, `get_weapon_class`, `get_weapon_id`, `get_weapon_pose`, `get_weapon_range`, `get_climbability`, `set_climbability`, `set_diedinfight`, `chack_weapon_exp` | **Implemented.** Full shipped-table load and command contracts are covered by `FightSystemTests`; see `COMBAT_SYSTEM_RE.md` |
| AI opponent (snapshot row, now implemented) | `ai`, `ai_log`, `ai_nprint`, `ai_npring`, `ai_getownerid`, `ai_tickhandler`, `ai_getintrudergnomes`, `ai_getintruderprods`, `ai_getpossibleinventions`, `ai_getprodlocations`, `ai_gnomepop_getcount`, `ai_gnomepop_getlist`, `ai_gnomepop_getobj`, `ai_gnomepop_getpos`, `ai_gnomepopscount`, `ai_prodpop_getcount`, `ai_prodpop_getlist`, `ai_prodpop_getobj`, `ai_prodpop_getpos`, `ai_prodpopscount`, `ai_setbuildup`, `ai_setpack`, `ai_validlocation`, `prod_get_task_active_places`, `prod_get_task_all_places`, `prod_get_task_total_cnt` | **Implemented.** Isolated per-owner `std_ai.tcl` execution, native stagger, population/FoW facts, intruders, production/invention queries, planner writes, placement search, attack orders, and theft-to-war diplomacy are covered by `AiSystemTests` |
| Authored level-generator presets (snapshot row, now implemented) | `lg_add_preset` | **Implemented.** Find-or-load metadata, area-relative pose validation, map-inset/HasTpl probing, and ordered forced-list emission are covered by `LevelGeneratorPresetTests` and the optional TrollQuest mod regression. This is also used by native `sandbox_*.tcl` / `multiplayer.tcl`, not merely MP win/lose screens. |
| MP-only (by design) | `net`, `get_mpstartpos` | multiplayer.tcl / MP Win-Lose screens only |
| Shadowed by real Tcl procs (stubs removed) | `ohlp_initstyle`, `paragraph`, `linkline`, `questlog`, `pickone` | `onlinehelputils.tcl` owns these procedures. Removing the bulk native registrations lets the unchanged Tcl definitions execute against the implemented `layout` surface |
| Correct-by-default constants | `minimalrun` (→0 = full run), `locale` (→"en" = shipped data), `get_local_player` (→0 = SP), `get_mapedit` (→0 = in-game), `perfoptions` (→0 keeps player AI off), `startcache` (→false = the intended fallback path) | The hardcoded return is exactly the right value for SP play |
| Debug / logging only | `sm_log`, `logdebug`, `time_line_log`, `print`, `set_logactions`, `get_light`, `get_attrib_names`, `tasklist_list` (one bowling-minigame branch degrades), `keybind` (dev bindings in single_Minimal) | Output-only; no gameplay effect |
| Loading-screen cosmetics | `show_loading`, `load_info`, `load_done`, `set_run_info` | Loading overlay text/progress only |
| Cosmetic / ambient | `snd_animlist` (per-anim sfx), `laser` (debug map FX), `set_light_begin`, `set_ground_begin`, `loading_objdetail` (LOD), `scroller` (credits), `notifyflare`, `set_objinfo` (tooltip key), `add_cave_skin_class`, `cave_skin` (wall decor), `fairy_move`, `gamedelayannounce`, `set_undeletable` (port has no GC honoring it), `set_user_groups`, `get_user_groups` (self-consistent empty round-trip), `inventionlinks` (help panel), `class_physcategory` (destructible physics unsimulated), `db_load` (anim DBs replaced by C# loader), `def_event` (declaration-only, dispatch is by name), `gui_new_game` (C# ScreenManager owns the flow), `Create_Instant_Trigger_Class` (deliberate — native TriggerService would be double-driven), `set_speechout` (real impl exists at DigglesTclEngine.cs:2533; bulk-stub entry is inert), `regexp`, `seek`, `ClassList` (makettree output feeds the stubbed init_techtree — inert until that lands) | Visual/QoL losses or already-covered-elsewhere |

---

## DEAD (153) — zero call sites in shipped data

Stub is free; do not spend effort. Compact list, grouped:

- **obj-find/ref family (superseded by `obj_query`)** (14): `obj_find`, `obj_find_list`,
  `obj_find_nearest`, `obj_find_nearest_storable`, `obj_find_nearest_storable_cb`,
  `obj_find_nearest_type`, `obj_find_own`, `obj_find_own_list`, `obj_find_var`,
  `obj_list`, `obj_next_visible`, `obj_eval`, `snap`, `get_next_relocobj`
- **AI-opponent snapshot commands now implemented** (10): `ai_gnomepop_getobj`,
  `ai_gnomepop_getpos`, `ai_npring`, `ai_prodpop_getcount`, `ai_prodpop_getobj`,
  `ai_prodpop_getpos`, `ai_setpack`, `ai_validlocation`, `ai_nextgnomepop`, and
  `ai_nextprodpop` are now real. The two `ai_next*` queries return the nearest population index.
- **Level-gen / StoryMgr variants never called (or only `//`-commented)** (11):
  `lg_dec_templatecount`, `lg_get_objcount`, `lg_set_callback`, `lg_set_starttemplate`,
  `lg_tp_addtemplates`, `lg_tp_log`, `lg_tp_mapfilter`, `lg_mark_area`, `sm_map_move`,
  `sm_music_theme`, `sm_reset`
- **Editor / dev / display-options / engine-internal** (42 remaining): `tclhelp`, `save_map`,
  `save_scene`, `show_maped`, `template_set`, `set_level`, `proddump`, `autoloadlevel`,
  `peek`, `poke`, `meminfo`, `perfwin`, `vtune`, `waterdbg`, `toggle_wf`,
  `output_3ddbstats`, `placelock_log`, `rjreusecheck`, `log_mask`, `logmod`,
  `camctrlmapping`, `inputmode`, `uncapturemouse`, `screen_mode`, `screen2world`,
  `screen3world`, `render_enable`, `render_gray`, `set_fov`, `set_brightness`,
  `set_contrast`, `get_brightness`, `get_contrast`, `load_all_textures`,
  `flush_all_textures`, `gameisloading`, `quit`, `sleep`,
  `time_schedule`, `reconnect`, `playerinfo`, `get_system_name`
- **Getters nothing ever reads** (33): `get_rendermaterial`, `get_fogofwar`, `fogofwar`,
  `get_forceipol`, `get_framevel`, `get_lmap_red`, `get_lmap_green`, `get_lmap_blue`,
  `get_lmap_light`, `get_logactions`, `get_map_height`, `get_map_width`,
  `get_obj_logicfogskip`, `get_obj_logicskip`, `get_pathlength`, `get_pathrandflags`,
  `get_placesnapmode`, `get_rel_pos`, `get_shield_class`, `get_snaptowall`,
  `get_prod_directevents`, `get_prodalloclock`, `get_prod_toolneed`,
  `get_attrib_display_name`, `get_escape_pos`, `get_energy`, `get_activegameplay`,
  `get_eventenabled`, `get_objinfo`, `get_maxprodera`, `get_prod_task_list`,
  `get_prod_total_task_cnt`
- **Setters/verbs nothing ever calls** (13): `set_icon`, `set_objiconoffset`,
  `set_renderlast`, `set_objectcollision`, `set_obj_logicskip`, `set_obj_logicfogskip`,
  `set_pathrandflags`, `set_prod_toolneed`, `set_maxprodera`, `set_diedinfight`,
  `set_eventenabled`, `set_remaining_sparetime`, `release_energy`
- **Misc unused** (28): `vector_setx`, `vector_sety`, `vector_rotx`, `vector_rotz`,
  `ground_fix`, `find_free_place_self`, `path`, `cwd`, `ref2path`, `ref_checkvar`,
  `play`, `reset_anim`, `db_search`, `db_findanim`, `update_objdetail`,
  `delete_lightning`, `scrollrange`, `horizon_clear`, `horizon_rem`, `chack_weapon_exp`,
  `clone_event`, `prod_valid`, `regsub`, `shader`, `Material`, `textstage`, `ClassInfo`,
  `ClassName`

---

## Current implementation order

Handler-name note: the decompile symbol table
(`the decompile`) names only the registrars —
**`CTclGlobalMethods_Register` @ `0x00618010`** (24.8 KB — registers every global Tcl
command and its handler) and **`CTclClassDefMethods_Register` @ `0x0060fc70`**. Individual
handlers are unnamed `FUN_*`; to find one, grep the command-name string in
`re/decompiled/Diggles/_global/00618010_CTclGlobalMethods_Register.c`'s registration
calls (or via the string xref in Ghidra). One handler is already identified:
`set_placesnapmode` = `FUN_0067d8b0`.

The correctness, attachment/transport, combat/opponent, native-persistence, EnergyMgr,
`prodslot_override`, newsticker, document/quest, fluid, and standard-tunnel waves are complete
and covered by the full gate. The remaining post-plan verification/follow-up order is:

1. **Combat core** — definitions, equipment, positioning, action resolution, delayed damage,
   callbacks, lethal state, ballistic result, training, and strikeback are implemented. Keep
   direction-pair presentation and navigation-side attack-position rejection in manual verify.
2. **Combat world behavior** — theft, warnings, diplomacy escalation, wars, and the enemy-clan
   opponent API are implemented. Keep populated-campaign behavior and theft-notification wording
   in manual verification; newsticker presentation is now implemented.
3. **Native persistence** — the original `.sav` archive has a validated lossless codec, typed
   world/core-object state, identity-first staging, variable-length native writer, live
   terrain/fog/fluid/common-object capture, atomic repository, and DI registrations. Mutable graph
   additions/removals, complete transactional commit, hotkeys/Tcl commands, and browser UX remain
   open; `.dqs` has been removed without migration support.
4. **Authored cave cutouts and cliff clipping** — the old roofed-tunnel item was based on a
   false `tile+0x08` layer interpretation. Standard target-11 tunnels are implemented; the
   remaining renderer work is the separate `CBlockHole`/scape-backdrop fidelity path.
5. **Technology-tree internals only on a reproducible mismatch** — inventions currently
    appear correct in manual play, so `init_techtree`/`get_class_era` are verification-led,
    not an unconditional rewrite target.

---

*Do not re-derive this from scratch: the per-command evidence (exact call-sites, branch
analysis) is embedded in the tables above. When de-stubbing, follow the RE-first rule —
cite the native handler decompile, not a plausible guess.*
