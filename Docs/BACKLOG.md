# Backlog

Open work, grouped by area. Items marked *verify* are implemented but have not been confirmed in a
full campaign playthrough; treat a reproducible mismatch against the original as a bug.

## Gameplay

- Work-front distribution for several diggers: workers converge on one tile; reverse-engineer the
  original candidate/claim rule before changing `dig_next` selection.
- Fog-of-war validity for dig marks: remove hidden marks that become invalid when fog opens.
- Hamster farm breeding state machine (two-seed persistent breeding).
- Transport polish: door placement, ladder preview (length, position, depth, speed), elevator
  snap/crate/icon, ceiling attachments. Spec: `LADDER_ELEVATOR_DOOR_RE.md`.
- Combat presentation: entity shatter/death effects and player-facing notifications; theft warnings,
  diplomacy escalation and opponent attack orders need a populated-campaign check (*verify*).
- Fluids: exact native relaxation coefficients and the `-ventil` flow effect; water wheel, fairy
  transfer puzzle, fish and diving-bell branches (*verify*). See `FLUID_SYSTEM_RE.md`.
- Inventions, hamster fleeing, construction placement blocking and worm traversal appear correct
  (*verify*).
- Headless animation clock: `AnimAction` without a controller finishes in a flat 0.1 s, so EditMode
  fight loops run about ten times faster than in Play. Resolving real clip lengths headlessly would
  make pacing rules testable.
- Two movement issues remain evidence-gated: an up/down hook while digging from a pit, and depth
  sinking after dismount.

## World and rendering

- Cave landscape backdrops where the original renders scenery behind a cutout (including the
  hamster quest cave); moving parallax landscape behind the starting meadow.
- Per-tile scape texture selection: lava and sulfur still take the moss path for L2M `cellId`
  bits 5..7.
- Tunnel support beams and moss/deco cadence after digging (*verify*); debris-flight sound
  (`FUN_00453f00`).
- Exact authored cave-cutout (`CBlockHole`) clipping.
- The `-exlimit` granite band of the level generator is disabled because it blocked progress past
  story caves; the faithful rim with a diggable pocket is deferred.
- Long term: move to Unity lighting/materials and the Unity particle system only after parity; the
  current custom light and CPU particle ports are the behavioural reference.

## Cutscenes and story

- `fairy_move` with the native orbital integrator (`FUN_0067a870`).
- Faster cutscene skip while keeping the script's final-state drain and cleanup.
- Gleipnir subtitle timing and clipped dialogue segments; unintended subtitles in menu cutscenes.
- Lightsaber transparency/emission; clan outfits, hats and hand props for herbalist actors.
- Rebuild the narrator elf (`CElf`) on its native fixed presentation plane instead of a world model.
- Apply the `pilz.wachsen01..03` growth clips so young mushrooms do not render fully grown.
- Per-clip visemes / lip-sync (deferred).
- Tutorial and tournament flow (`TUT` define, main-menu selection). See `TUTORIAL_MODE.md`.

## Audio

- Positional falloff, fog culling and voice caps in a dense late-game scene (*verify*).
- Music/voice fade and marker transition semantics where the stream controller still approximates.

## UI

- Diggle info panel controller over the authored layout (roster, HP/mood/work status, stats and
  knowledge, two-way selection sync). Spec: `UI_DIGGLE_INFO_PANEL_SPEC.md`.
- Save/load browser visual over the existing controller; loading transition.
- Speech bubbles, minimap, statistics, technology tree, in-game options, main-menu settings,
  loading screen, intro music.
- Inventory drag-and-drop; missing red storage-category icon variants.
- ID-based text localization around `DigglesText`; localized data textures.
- Console: spawn helpers and safe tuning commands.

## Performance

- Reproducible standalone profile (scene, entity count, time factor, FPS, allocations, draw calls).
- First-use `model.buildclip` hitches (130–620 ms): prewarm or async build.
- World-growth spikes (0.4–1 GB allocations, sim steps above 300 ms).
- Load-time optimization for native saves.
- Async MeshCollider cooking, staggered remesh and drop-item accumulation only with standalone
  evidence. See `PERFORMANCE.md`.

## Architecture (after the port is playable)

- Reversible model conversion with separated meshes/animations, keeping `.3db` compatibility.
- An editable world format plus BDB/PMP conversion tools.
- Migrating gameplay from Tcl to typed C# only behind parity tests; the scripts stay the reference.
- Authored prefabs and explicit initialization for runtime-spawned entities.
