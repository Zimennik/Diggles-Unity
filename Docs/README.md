# Documentation index

Most documents are reverse-engineering references (`*_RE.md`): they describe how the original
`Diggles.exe` does something, with function addresses, offsets and script citations, and how the
port implements it. Read the one that owns a subsystem before changing that subsystem.

## Start here

| Document | What it covers |
|---|---|
| [`BACKLOG.md`](BACKLOG.md) | Open work, grouped by area. |
| [`ENGINE_ARCHITECTURE.md`](ENGINE_ARCHITECTURE.md) | Map of the original executable: entry, frame loop, subsystems, Tcl integration. |
| [`COORDINATE_SYSTEM.md`](COORDINATE_SYSTEM.md) | Axis conventions of terrain, markers and entities. Required before any spatial work. |
| [`TCL_OBJECT_MODEL.md`](TCL_OBJECT_MODEL.md), [`TCL_INIT_LOADING.md`](TCL_INIT_LOADING.md), [`TCL_INTERPRETER_PLAN.md`](TCL_INTERPRETER_PLAN.md) | The Tcl interpreter, object scoping and the boot chain of the original scripts. |
| [`ENGINE_TCL_API.txt`](ENGINE_TCL_API.txt), [`ENGINE_COMMAND_GAP.md`](ENGINE_COMMAND_GAP.md) | The engine command reference and the inventory of still-stubbed commands. |
| [`CONTINUOUS_INTEGRATION.md`](CONTINUOUS_INTEGRATION.md) | Headless test runner and data manifest check. |
| [`UI_ARCHITECTURE.md`](UI_ARCHITECTURE.md) | Model / controller / authored-prefab split of the UI. |

## Gameplay

| Document | What it covers |
|---|---|
| [`GNOME_BEHAVIOR_COMPLETE_RE.md`](GNOME_BEHAVIOR_COMPLETE_RE.md) + [`re_parts/`](re_parts/) | Movement, climbing, digging, work and idle behaviour of the gnomes. |
| [`ORIGINAL_PATHFINDING_RE.md`](ORIGINAL_PATHFINDING_RE.md), [`NAVIGATION_AND_MOVEMENT.md`](NAVIGATION_AND_MOVEMENT.md) | The `Calc1` pathfinder, `MetaWalk` and the port's navigation layer. |
| [`DIGGING_MECHANIC.md`](DIGGING_MECHANIC.md), [`ROOFED_TUNNEL_RE.md`](ROOFED_TUNNEL_RE.md) | Digging, dig marks, tunnels and caves. |
| [`WORK_DISPATCHER_NATIVE.md`](WORK_DISPATCHER_NATIVE.md), [`PRODUCTION_PANEL_RE.md`](PRODUCTION_PANEL_RE.md), [`PRODUCTION_SLOT_OVERRIDE_RE.md`](PRODUCTION_SLOT_OVERRIDE_RE.md) | Work assignment, production chains and the production panel. |
| [`ITEM_PLACEMENT_RE.md`](ITEM_PLACEMENT_RE.md), [`PLACEMENT_ATTACHMENT_RE.md`](PLACEMENT_ATTACHMENT_RE.md), [`HAT_LINK_SYSTEM.md`](HAT_LINK_SYSTEM.md) | Placing objects, attachments and link points. |
| [`LADDER_ELEVATOR_DOOR_RE.md`](LADDER_ELEVATOR_DOOR_RE.md) | Ladders, elevators and doors. |
| [`COMBAT_SYSTEM_RE.md`](COMBAT_SYSTEM_RE.md) | Fights, weapons, diplomacy and the opponent AI. |
| [`ENERGY_SYSTEM_RE.md`](ENERGY_SYSTEM_RE.md), [`FLUID_SYSTEM_RE.md`](FLUID_SYSTEM_RE.md), [`WATER_PHASE1_RE.md`](WATER_PHASE1_RE.md) | Energy distribution; water, sulfur and lava. |
| [`TUTORIAL_MODE.md`](TUTORIAL_MODE.md) | The tutorial campaign flow. |

## World and rendering

| Document | What it covers |
|---|---|
| [`RENDERING_PIPELINE_COMPLETE.md`](RENDERING_PIPELINE_COMPLETE.md) | How the original builds terrain from BDB blocks, PMP heightmaps and L2M cells. |
| [`RENDERING_LIGHTING_RESEARCH.md`](RENDERING_LIGHTING_RESEARCH.md), [`TUNNEL_TORCH_LIGHTING_RE.md`](TUNNEL_TORCH_LIGHTING_RE.md) | Lights, torches and the custom shaders. |
| [`WORLDGEN_ORIGINAL_RE.md`](WORLDGEN_ORIGINAL_RE.md), [`LEVEL_GENERATOR.md`](LEVEL_GENERATOR.md) + [`worldgen-re-reports/`](worldgen-re-reports/) | World growth and the level generator. |
| [`FOG_OF_WAR_RE.md`](FOG_OF_WAR_RE.md) | Fog of war simulation and presentation. |
| [`BACKDROP_PARALLAX_RE.md`](BACKDROP_PARALLAX_RE.md), [`RIM_CUTOUT_RENDER_RE.md`](RIM_CUTOUT_RENDER_RE.md) | Backdrops, parallax layers and cave rims. |
| [`PARTICLE_SYSTEM_REWRITE.md`](PARTICLE_SYSTEM_REWRITE.md) | The CPU particle system. |
| [`ANIMATION_CONTROLLER.md`](ANIMATION_CONTROLLER.md), [`FACE_ANIMATION.md`](FACE_ANIMATION.md) | Animation sets, vertex animation, faces. |
| [`PERFORMANCE.md`](PERFORMANCE.md) | Profiling findings and tools. |

## Presentation and persistence

| Document | What it covers |
|---|---|
| [`CUTSCENE_SYSTEM.md`](CUTSCENE_SYSTEM.md) | The sequencer behind cutscenes and the menu diorama. |
| [`AUDIO_SETUP.md`](AUDIO_SETUP.md), [`SOUND_SYSTEM_RE.md`](SOUND_SYSTEM_RE.md) | Music, voice and positional sound. |
| [`BOTTOM_MENU_RE.md`](BOTTOM_MENU_RE.md), [`NEWSTICKER_RE.md`](NEWSTICKER_RE.md), [`DOCUMENT_WINDOW_RE.md`](DOCUMENT_WINDOW_RE.md), [`UI_DIGGLE_INFO_PANEL_SPEC.md`](UI_DIGGLE_INFO_PANEL_SPEC.md) | UI panels. |
| [`ORIGINAL_SAVE_FORMAT_RE.md`](ORIGINAL_SAVE_FORMAT_RE.md), [`ORIGINAL_SAVE_ACTION_RE.md`](ORIGINAL_SAVE_ACTION_RE.md) + [`save-re-reports/`](save-re-reports/) | The native `.sav` format and the port's save/load pipeline. |
