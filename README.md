# Diggles Unity Port

A Unity 6 reimplementation of *Diggles: The Myth of Fenris* (Innonics, 2001; also known as
*Wiggles* or *Гномы*) that runs on top of the original game's data files. Like OpenRCT2 or OpenTTD, the
port contains no game content: it reads the shipped `data/` folder (models, terrain, textures,
sounds and the Tcl gameplay scripts) and reproduces the engine behaviour, which was
reverse-engineered from `Diggles.exe`.

## State of the port

Playable, far from finished. The world generates; you can walk, dig and build; capturing works
(clan flags are not rendered yet). What holds up today:

- Original asset pipeline: `.3db` models with vertex animation, BDB/PMP/L2M terrain, TGA
  textures, GUI sprites, sounds and music (with the original limiter).
- A C# Tcl interpreter that executes the unmodified gameplay scripts (684 classes) against a
  reimplementation of the engine's command set.
- Parallax backdrops and cave edges, fog of war, lighting, cutscenes, news ticker and documents.
- Localization: UI text and textures can reference the original game's strings and files by id.

Broken or missing (see [`Docs/BACKLOG.md`](Docs/BACKLOG.md) for details):

- Combat, almost entirely; most monsters and their behaviour.
- Navigation and world generation in places (stairs in particular).
- Most particle effects; some icons (TGA alpha).
- Water and the other fluids.
- Ladders, elevators, traps and doors; the tutorial flow; parts of the quests.
- Level transitions, the energy system, clan flags, performance on large worlds.
- Save/load: the original `.sav` format is decoded and mostly compatible, but not reliable yet.

The direction is to replace the engine-bound mechanics (particles, scripts, AI) with native
Unity/C# implementations piece by piece, not to rewrite the game. The original Tcl scripts stay
the behavioural reference until a native equivalent exists; after that, improving on them makes
sense, because the original scripts carry plenty of bugs of their own. Multiplayer is out of scope.

## Requirements

- Unity `6000.0.62f1` (install through Unity Hub).
- A copy of *Diggles: The Myth of Fenris* (Steam or GOG). Any language works; the text language
  is detected from the data.
- Windows. Other platforms are untested.

## Getting started

1. Clone the repository and open the folder in Unity Hub.
2. Point the port at the game data: open `Assets/StreamingAssets/diggles_config.json` and set
   `dataRoot` to the game's `data` folder (the default is the Steam install path; for GOG use the
   `data` folder inside the game directory). Use forward slashes.
3. Open `Assets/Diggles/Scenes/RuntimeWorldGeneration.unity` and press Play. The main menu
   appears; *New Game* starts the campaign.

For a standalone build (`File → Build Profiles`), a `diggles_config.json` placed next to the
executable overrides the shipped one.

Controls: left click selects and orders, drag to box-select; arrow keys / WASD scroll,
mouse wheel zooms, right-drag or Q/E rotates the camera; `+`/`-` change game speed;
F2 quicksave, F5 load the newest save; `` ` `` opens the Tcl console; F3 perf HUD.

## Repository layout

```
Assets/Diggles/
  Runtime/
    Engine/        game session, entities, animation, camera, cutscenes, audio, input, DI scope
    Engine/Sim     fixed-step simulation, timers, work dispatcher
    Tcl/           Tcl interpreter; Tcl/Engine = the engine command set and entity actions
    Navigation/    NavWorld, Calc1 pathfinder, floor paths
    World/         terrain builder, digging, fog of war, level generator, fluids, backdrops
    Save/          original .sav codec, documents, load/save services
    UI/            models, views and controllers for the authored UI prefabs
    Model/         .3db parser, model library, TGA loader
    Shaders/       terrain, model, fluid, fog and particle shaders
    Tools/         in-game diagnostics (perf HUD, tuning, test harnesses)
  Editor/          importers, campaign/model browsers, icon pipeline, Tcl test windows
  Tests/           EditMode (logic against the real data) and PlayMode (full boot) tests
  Prefabs/, Scenes/, Fonts/, Textures/, StreamingAssets/
Docs/              reverse-engineering references and architecture notes (start at Docs/README.md)
Tools/             save-format research scripts
.github/scripts/   headless test runner and data manifest check
```

Editor tools live under the `Diggles` menu: *Campaign Browser* builds a static world in the scene
view, *Spawn Diagnostics* spawns isolated test entities, *Tcl Test* runs scripts against the
interpreter, and `Tools/Diggles` has model and level browsers.

## Tests

In Unity: `Window → General → Test Runner`, EditMode or PlayMode, Run All. Data-backed tests
resolve the data folder from `diggles_config.json` (or `DIGGLES_DATA_ROOT`). Headless runs and
the data manifest check are described in [`Docs/CONTINUOUS_INTEGRATION.md`](Docs/CONTINUOUS_INTEGRATION.md).

## Contributing

- Behaviour comes from evidence, not guesses: the decompiled executable, the original Tcl
  scripts and memory dumps. The `Docs/*_RE.md` files record what is known; cite them (or the
  function address) in code comments when you implement a rule.
- Code comments and documentation are in English.
- Keep the original data untouched; the port must work with an unmodified installation.

## License and legal

The port's source code and documentation are released under the MIT license (see `LICENSE`).
*Diggles: The Myth of Fenris* and its data are the property of their rights holders; nothing
from the game is distributed here except the UI icons and font used by the authored menus for
fidelity. Third-party components keep their own licenses: DOTween (Demigiant), SoftMaskForUGUI
(MIT), TextMesh Pro (Unity), UIGradient (Brad Nelson), the Jura and MursGothic fonts.
