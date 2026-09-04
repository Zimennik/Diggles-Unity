# Tutorial mode

## Native flow

The executable's single-player dispatch table names the two boot scripts verbatim:

- Campaign: `data/GameScripts/single_Startcamp.tcl`
- Introduction: `data/GameScripts/single_Starttut.tcl`

Source: `../../re/notes/strings/Diggles.strings.txt:7772-7773`.

The introduction path is a plain Tcl world, not a `StoryMgr` level:

1. `single_Starttut.tcl:1-8` shows the loading veil, clears objects, calls
   `GameScripts/startseqs.tcl`, and sets `Trigger_Auftrag_Odin` context to `tutorial`.
2. `startseqs.tcl:1-18` clears objects again, creates a 300x300 map, sets the opening view,
   and stamps `unq_ende_b`, `unq_zwergenkoenig`, and `unq_fenris_first`.
3. `Trigger_Tournament.tcl:622-671` arms `odin_001`. Its destroy overload creates the
   `Trigger_tourn_2170` handoff with the same context.
4. `Trigger_Tournament.tcl:677-688` switches that context. `tutorial` queues
   `call data/GameScripts/single_Tutorial.tcl`; `tournament` queues
   `call data/GameScripts/single_Tournament.tcl`.
5. On the next simulation step, `single_Tutorial.tcl:1-27` clears the intro objects, calls
   `oberwelt.tcl`, creates seven `FogRemover` objects, and arms the tutorial trigger at +13 s
   and +18 s.
6. `oberwelt.tcl:1-20` creates the 500x200 overworld, sets view `131.78 23.95 2.5`, and
   stamps the tutorial, tournament, and scene regions. `obw_tutorial.tcl:278-285` creates a
   player-owned dwarf and `Trigger_Tutorial`.

Campaign still follows the existing port path. Native `single_Startcamp.tcl:1` calls
`single_CampaignX.tcl`; its first four lines are the same `obj_clear`, `sel /obj`,
`new StoryMgr`, `load_level Campaign` string used by `DigglesWorldRuntime.RunCampaign`.

## Port entry points

`DigglesWorldRuntime.NewGame(NewGameVariant)` is the UI binding seam:

- `NewGameVariant.Introduction` reads and evaluates
  `data/GameScripts/single_Starttut.tcl` after loading all runtime definitions.
- `NewGameVariant.Campaign` keeps the existing `RunCampaign` boot string and behavior.
- Parameterless `NewGame` remains campaign-compatible for the existing menu binding.

`GetBootScript` exposes the exact native path mapping. `HasStoryManager` captures the semantic
law: Introduction is `false`, Campaign is `true`. The introduction, tutorial, and tournament
handoff do not create a `StoryMgr`; setting the flag merely to unlock campaign UI would change Tcl
behavior through `is_storymgr`.

`RunWorldScript(string)` is the synchronous gameplay seam used by EditMode tests. It evaluates
the complete Tcl batch, emits terrain, and wires presentation without a coroutine.

## Mid-session world swap

`RuntimeMapBuilder.CreateMap` distinguishes initial creation from recreation after emission. On
recreation it clears the old builder arrays, drops live-mode references, sets
`WorldRecreationPending`, and raises a staging notification. It does not emit terrain inside the
Tcl command: `oberwelt.tcl:15-20` must still stamp all three templates into the new arrays first.

The lifecycle is:

```text
map create after emit
  -> inactive candidate content root + redirect SpawnParent
  -> remainder of Tcl batch stamps terrain and spawns entities
  -> EmitTerrain(candidate) after Eval/Step returns
  -> MarkValidated + RuntimeWorldSessionHost.ActivateCandidate
  -> BindSession + guarded WirePresentation
  -> dispose retired content root
```

The candidate retains the live `DigglesEngine` and Tcl context. This is required because
`exec_deferred` runs inside the existing simulation and `obj_clear` has already replaced the
entity set. Only the content root changes. Activation uses the same candidate/validation/session
host/presentation binder machinery as original-save loading.

At the staging boundary the old terrain, navigation, fog, base-light, and spawner presentation
references are detached. Because this candidate deliberately keeps the interpreter, it also resets
world-local fluid cells, energy leases, decoration seeds, place locks, horizon regions, and adaptive
music markers before the replacement templates run. After emission:

- `DiggableTerrain` and `NavWorldService` come from the new map dimensions.
- `WirePresentation` creates a new `FogOfWarState` when dimensions differ. The tutorial's
  `FogRemover` entities are already parented under the candidate and feed that new state.
- The real `set_view` state is copied to `DigglesCameraController`, and world bounds use the new
  map dimensions.
- The `(terrain, content root)` guard allows one presentation wire for activation and rejects a
  repeated bind of the same session.
- Old root-local presenters are destroyed before activation so their `OnDestroy` shader cleanup
  cannot clear the newly installed fog, base-light, or backdrop globals.

`FlushPendingWorldSwap` is public so EditMode tests can activate synchronously after a manual
`DigglesGameSim.Step`. Play mode also calls it from `Update`, after the frame-loop simulation has
finished the deferred batch.

## Verification

`Assets/Diggles/Tests/Editor/TutorialBootE2ETests.cs` covers:

- native boot-script mapping and `IsStoryMgr` semantics;
- direct `single_Tutorial.tcl` boot, 500x200 terrain, population, fog removers, and lesson timer
  state-machine progress;
- full `single_Starttut.tcl` Odin path, sequence-break drain, deferred 300x300 -> 500x200 swap,
  candidate-only emission, old-object removal, new fog/nav/camera wiring, and no double wiring;
- direct `s2010`, `s2015`, and `s2020` lesson jumps from `oberwelt.tcl:22-77`;
- both `Trigger_tourn_2170` context-switch branches from `Trigger_Tournament.tcl:677-688`.

## Deferred work

- Full tournament lesson/playthrough coverage. This lane verifies class loading and both context
  branches only.
- A visual `show_loading` veil. The command remains cosmetic; the swap is currently synchronous.
- Runtime F6-F12 bindings. `single_Tutorial.tcl:30-36` registers developer chapter jumps through
  the currently stubbed `keybind`; tests invoke the shipped procedures directly.
- `CElf` remains a known visual-risk item from the tutorial audit
  (``): the headless lesson and sequence state can advance, but the
  elf presentation rebuild is outside this engine lane.
- The menu's New Game sub-screen and disabled Multiplayer item belong to the separate UI lane;
  `MainMenuView` is unchanged here.
- Non-StoryMgr gameplay needs an explicit session-mode signal. Add that signal at
  `RuntimeGameSession`, preserve it through candidate activation, then replace presentation/save
  gates in `TimeControlService`, `BottomMenuController`, `QuickSaveHotkeyService`,
  `AutoSaveService`, `ScreenManager`, `DiggleInfoPanelLogic`, `NewsTickerWidget`, and
  `WorkClockWidget`. Do not solve this by setting `IsStoryMgr`: Tcl's `is_storymgr` command must
  remain false throughout the native introduction/tutorial chain. This wiring is outside this
  lane's owned files.

## Introduction cast and Odin stall repair

### Native model-selection law

`set_class_anim` is registered to `FUN_00615650`
(`../../re/decompiled/Diggles/_global/0060fc70_CTclClassDefMethods_Register.c:291-299`). That
handler parses the concrete animation descriptor and stores it in the class table
(`00615650_FUN_00615650.c:138-155`; the table allocation/storage is visible in
`006026e0_FUN_006026e0.c:28-40,73-80`; the descriptor parser splits the qualified name on `.` at
`00460fc0_FUN_00460fc0.c:25-32`). When a native animation action starts, it first resolves a
logical name through that table; only a miss is parsed as a literal animation. The resolved
descriptor is then applied and its frame duration stamps the action deadline
(`004082a0_FUN_004082a0.c:58-75,145-147`; the table lookup returns the descriptor fields at
`004613d0_FUN_004613d0.c:76-84`).

Therefore, model identity comes from the concrete animation descriptor's object component. There
is no evidence for a generic `kind=gnome -> mann.3db` fallback. The Unity port must choose a model
before its first action, so `RuntimeWorldSpawner` now uses a valid `set_class_anim` value's `obj.`
prefix only after the existing TSV, `class_defaultanim`, class-name, and current/`obj_init`
resolution paths fail.

`Koenig_im_Bett` is the shipped root case: it declares no `class_defaultanim`, but every `king*`
alias maps to `mann.koenig_*` (`../../original/data/Scripts/classes/zwerg/actors.tcl:503-527`). The
fallback consequently binds `mann`, without inventing a kind-wide rule. The rest of the requested
cast resolves through already-authored data:

| Class | Model source |
|---|---|
| `Hand_Gottes` | `odinshand` default/animation table (`characters/odin.tcl:4-33`) |
| `Fifi` | `fifi` default/animation table (`characters/fenris.tcl:1098-1133`) |
| `Riesenelfe` | `riesenelfe` default/animation table (`characters/riesenelfe.tcl:3-15`) |
| `Dummy_Koenigsschlafmuetze` | shipped TSV row (`Assets/StreamingAssets/class_model_map_v2.tsv:273`) |
| `Dummy_L1` | shipped TSV row (`class_model_map_v2.tsv:561`) |

The TSV also explicitly maps `Fifi`, `Hand_Gottes`, and `Riesenelfe` at lines 2092, 2131, and
2297. Genuine misses still fall through to the visible placeholder and its once-per-class warning;
the reason string now includes the class-animation-table stage.

### Stall law

`Trigger_Auftrag_Odin` targets `Koenig_im_Bett`
(`../../original/data/Scripts/classes/story/Trigger_Tournament.tcl:664-670`), and
`sequencer_activate` appends trigger actuators to the actor list in order
(`../../original/data/Scripts/classes/story/sequencer.tcl:395-403`). Thus `[Actor 0]` at
`odin_001.tcl:103` is the king. The line itself is a direct `set_anim`; the parser blocks only on a
`do_*` command (`sequencer.tcl:588-630`), so the next blocking beat is the hand's `clockring` action
at `odin_001.tcl:105`. Resolving both models restores their real clips and durations.

As a safety law, `AnimAction` gives a missing controller, missing clip, or failed clip a finite
0.1-second duration (`Assets/Diggles/Runtime/Tcl/Engine/EntityActions.cs:64-93`). That degraded
clock is intentionally retained: a placeholder can look wrong, but it cannot hold an actor gate
forever. `IntroCutsceneRepairTests` covers the exact fallback boundary and drives the real
`single_Starttut -> odin_001` path without setting the break flag; it must observe
`kingwakeupstart`, reach the first explanation segment, and shut down naturally. The existing
`TutorialBootE2ETests.IntroToTutorialSwap_DefersEmissionThenActivatesOneFullyWiredCandidate` remains
the skip-path 300x300 -> 500x200 handoff guard.

Voice timing is specified in `CUTSCENE_SYSTEM.md` §7. Floor z-fighting is deliberately unchanged;
it requires a separate visual Play/screenshot pass.
