# Tcl Interpreter — Plan, Status & Roadmap

> All 7 phases below are now IMPLEMENTED (the interpreter drives the live
> campaign/gameplay/cutscene stack). This doc is kept as the phase-history +
> architecture map.

A C# implementation of a Tcl interpreter tailored for running Diggles'
`.tcl` scripts inside Unity. Replaces the original engine's
`tcl83.dll` dependency with managed code.

---

## Status snapshot

| Phase | Description | Status |
|---|---|---|
| 1 | Core interpreter (lexer + parser + evaluator + 50+ built-ins) | ✅ |
| 2 | Basic engine bridge (def_class, new, set_pos, set_anim,...) | ✅ |
| 3.0 | Parser hardening + stub mode (CRLF, `//`, brace-marker leak, etc.) | ✅ |
| 3.1 | Class lifecycle (method / state / event / obj_init / inherit) | ✅ |
| 3.2 | File loader (`call` / `source` / `load_file`) | ✅ |
| 3.3 | `append`, `lsearch`, `lreplace`, `lsort`, `subst`, object queries, vector ops | ✅ |
| 3.4 | Bulk-stub 270 engine commands (sequencer, FX, UI, sound, map, fight, AI, prod) | ✅ |
| 4 | **Bridge Tcl → Unity scene** (`new` spawns real GameObject) | ✅ |
| 5 | **Map system real impl** (`map_template`, `map_setlayer2`, `split_template`) | ✅ (terrain emits via the runtime world loader / faithful worldgen) |
| 6 | Sequencer / cinematic Unity bindings (camera, audio, text, particles) | ✅ (cutscene ENGINE done + verified; Play presenter debugged — see CUTSCENE_SYSTEM.md) |
| 7 | **Event loop + timer scheduling** (`timer_event`, state-body ticks) | ✅ (live: timer_event + per-tick state bodies drive gnome AI/work) |

**Current numbers** (against `the decompile`,
481 `.tcl` files): 469 / 481 (97.5 %) load + execute under stub-mode
without throwing. **195** stubbed calls remain across all 481 files
(down from **27 792** before phase 3.4 — 99.3 % drop). What's left
is single-call story-specific procs (`+rausbuddel`, `+elfenarbeit1`,
`+ringuebergeben`, …) defined inside specific cutscene class bodies
that don't survive isolated parsing.

---

## Architecture

```
unity/Diggles/Assets/Diggles/Runtime/Tcl/
├── TclException.cs — errors + Return/Break/Continue control-flow
├── TclInterpreter.cs — public API, scope stack, command registry,
│ alias-aware variable lookup, tolerant flags
├── TclLexer/Parser
│ └── TclParser.cs — script → command stream; brace-balanced
│ parsing; CRLF normalisation; `//` line strip
├── TclSubst.cs — `$var`, `[cmd]`, `\X` substitution; honours
│ TolerateMissingVars flag
├── TclBuiltins.cs — set, unset, proc, return, if, while, for,
│ foreach, break, continue, eval, catch, error,
│ puts, incr, expr, list, lindex, lrange,
│ llength, lappend, concat, split, join, string
│ (length/index/range/match/equal/compare/first/
│ last/map/replace/is …), format, info, global,
│ upvar, uplevel, array, switch, source,
│ append, lsearch, lreplace, lsort, lreverse,
│ subst, glob, scan (%d/%x/%o/%c/%s/%f)
├── TclExpr.cs — expr tokenizer + recursive-descent parser
│ for arithmetic / comparison / logical /
│ ternary / math-fn expressions
├── TclList.cs — Tcl list parse/format helpers
└── Engine/
    ├── DigglesClass.cs      — class definition (methods, events, states,
    │                          state-enter/leave, member vars, inherit
    │                          chain, init/exit code)
    ├── DigglesTclEntity.cs  — instance: position, rotation, velocity,
    │                          current anim, VarsScope, state machine
    ├── DigglesTclContext.cs — class registry, handle table, current
    │                          `this`, SpawnParent transform, DataRoot,
    │                          file cache, tolerance flags
    └── DigglesTclEngine.cs  — `Register(interp)` wires ALL engine
                               commands; ~600 LOC + ~270-name bulk stub
                               list + `+`/`-` prefix variants
```

**Test framework** (NUnit / Unity Test Runner). The Tcl-facing suites under
`Assets/Diggles/Tests/Editor/` have grown well past the original three; the
directly interpreter-relevant ones today are:

```
Assets/Diggles/Tests/Editor/ (asmdef: Diggles.Tests.Editor.asmdef)
├── TclInterpreterTests.cs — core-language unit tests
├── TclEngineTests.cs — engine integration tests
├── TclRealFilesTests.cs — real-file smoke tests (customcommands, Pilz, Rohstoffe, …)
├── TclObjectScopeTests.cs — per-object global scoping (ActiveGlobalOverride, ref_get/set)
├── TclFsCommandsTests.cs — glob / cd / pwd / file exists
├── TclBuiltinsTests.cs — later built-in coverage
├── TclExprVarTests.cs / TclExprBitwiseTests.cs — expr edge cases
├── TclSimTests.cs — headless sim (state-think / work loop)
└── TclSceneBridgeTests.cs — `new` → GameObject bridge
```

(Many more gameplay suites — dig, prod, worldgen, cutscene, fog — live alongside
these and exercise the interpreter end-to-end.)

Run via Unity `Window → General → Test Runner` (EditMode tab) or
batchmode: `Unity.exe -batchmode -runTests -testPlatform EditMode -testResults out.xml`.

`run-tests.sh` at `unity/` is a convenience wrapper — but Unity must
NOT be open on the project at the same time (lock conflict).

The interactive harness `Diggles → Tcl Test` (TclTestWindow.cs)
lets you paste a script, toggle stub mode + engine commands, and
load arbitrary `.tcl` files from disk.

---

## Phase 4 — Bridge Tcl → Unity scene (DONE — historical plan below)

> 📌 Implemented. `new` spawns real GameObjects via the runtime world loader;
> `CmdNew`/`CmdDel`/`CmdSetPos`/`CmdSetAnim` mirror to the bound transform +
> `DigglesAnimator`. The section below is the original design note.

**Goal:** `new ZwergXxx "" "X Y Z" "rX rY rZ"` in Tcl spawns a real
GameObject in the active scene, with all the existing infrastructure
(MarkerModelResolver mesh + materials + animator clips, DigglesObject
controller, WalkAction click-to-move) wired up.

### What needs to change

1. **DigglesTclContext** gets a `SpawnParent` Transform field (already
   present; just unused).
2. **CmdNew** in DigglesTclEngine, after registering the entity, if
   `ctx.SpawnParent != null`:
   - Calls `MarkerModelResolver.ResolveDynamic(className, dataRoot)` to
     get a `ResolvedDynamicModel` (or `Resolve` for static markers).
   - Creates a GameObject under SpawnParent named `ClassName_handle`.
   - Wires Position (axis-converted) + Rotation onto the GameObject.
   - Creates an inner "Model" child GameObject with `MeshFilter`,
     `MeshRenderer`, `DigglesAnimator`, mirroring the existing
     `BdbPmpWorldBuilder.SpawnTclMarkers` outer/inner split.
   - Adds `DigglesObject` MonoBehaviour and `BoxCollider` on the
     model child.
   - Saves the GameObject ref on `entity.GameObject`.
3. **CmdDel** does `Object.Destroy(ent.GameObject)` (already coded).
4. **CmdSetPos / CmdSetAnim / CmdSetRot** mirror writes back to the
   bound `GameObject.transform` / `DigglesAnimator.SetAnim(...)`.
5. **Editor menu entry** that creates an empty scene with a
   pre-configured `DigglesTclEngineHost` GameObject whose
   `SpawnParent` is itself, then runs a small Tcl script that spawns
   one of each character — quick visual smoke test.

### Acceptance

- Open Unity, menu `Diggles → Tcl Spawn Test`, see a small grid of
  spawned characters from a Tcl script alone (no static parsing path).
- Run a hand-typed Tcl script in TclTestWindow with engine + scene
  bindings enabled; `new Zwerg...` puts a dwarf into the scene.

### Risk register

- `MarkerModelResolver.Resolve` / `ResolveDynamic` currently take
  `dataRoot` + `AssetImportContext` (editor-only). Bridge layer must
  pass `ctx: null` and have the resolver tolerate that path
  (already handled in the existing static-spawn flow).
- Axis convention: Tcl entity `Position` is Diggles native
  `(X horiz, Y depth, Z height)`. The existing marker spawn does
  `localPosition = (m.x + tplX, m.z * 0.5 + 7, -(m.y + tplY))` to land
  in the OLD-style `−90X`-rotated world. Bridge layer must apply the
  same conversion — see `COORDINATE_SYSTEM.md`.
- TCL rotation `rY` is yaw; existing spawn converts via `markerRot =
  Quaternion.Euler(rX·Rad2Deg, −rY·Rad2Deg, rZ·Rad2Deg)`. Reuse the
  same formula.

---

## Phase 5 — Map system (DONE — historical plan below)

> 📌 Implemented. Terrain is now driven through the interpreter + the faithful
> world generator (see LEVEL_GENERATOR.md / worldgen rebuild), not the old
> static parser. The map/template commands below are wired.

Tcl-driven world generation. The old static parser over `Campaign.tcl` has
been replaced by the real interpreter path.

### Commands to promote from stub to real

- `map_template PATH WORLD_X WORLD_Y` — schedule PMP load at offset.
- `map_setlayer2 X Y PATH` — overlay L2M at offset.
- `sm_create_map W H` — initialise world dimensions.
- `sm_add_temp NAME ?X Y?` — register template by name.
- `sm_def_temp_group NAME {... }` — group templates for level gen.
- `sm_set_temp...` / `sm_set_zone...` — finalize world layout.
- `add_cave_skin_class SET DETAIL SIZE` — cave-deco class lookup.
- `split_template PARENT_TCL PMP W H { sub_tcl_list }` — chunked
  template loading.

Most of these are bookkeeping. Real implementation routes via the
existing `BdbPmpWorldBuilder` (or a new `TclDrivenWorldBuilder`).

### Acceptance

- Load `Campaign.tcl` through the interpreter; observe terrain +
  markers appearing identical to the current static-parse output.
- Switch the editor menu's default load path from static-parser to
  interpreter-driven.

---

## Phase 6 — Sequencer / cinematic Unity bindings (DONE — historical plan below)

> 📌 Implemented. The cutscene engine is done + verified; the Play-side
> presenter (camera fly, audio, dialogue text, particles) is wired and was
> debugged separately — see CUTSCENE_SYSTEM.md.
> The subsystem breakdown below is the original design note.

The cutscene system. The 270+ sequencer commands drive real camera moves,
audio, dialogue text and particles.

### Subsystems

1. **Timeline / queue runner** — `+sq_*` queued commands run in
   order on a coroutine. `do_wait time N` advances by N seconds.
   Need a `SequencerHost` MonoBehaviour with a `Coroutine` driver.
2. **Camera control** — `sq_camera fix X Y Z`, `sq_camera move...`,
   `set_view`, `set_viewpos`. Bind to `Camera.main`.
3. **Audio** — `sound bind NAME PATH`, `sound play NAME`, the
   `adaptive_sound` theme system, `sq_audio open NAME`. Bind to
   `AudioSource` (probably a pool).
4. **Text / dialogue UI** — `do_text ID N POS NAME`, `sq_text file
   NAME` (loads dialog file), `textwin clear / show / hide`,
   `paragraph LINE`, `linkline …`. Bind to a UI Canvas + TMP text.
5. **Particles** — `set_particlesource OBJ INDEX VAL`,
   `create_particlesource TYPE X Y Z DX DY DZ COLOR SPEED`,
   `change_particlesource …`, `class_particle …`. Bind to
   `ParticleSystem`.
6. **FX** — `start_fade R G B`, `cancel_fade`, `change_light`,
   `lightning …`, `laserbeam …`, `screenvibe AMP X Y FREQ TIME`.
7. **Actor / Object refs** — `Actor N` returns a stable handle to
   the N-th actor a sequence has bound; `sq_actor find CLASS RANGE
   LIMIT BUNDLE` binds them.

### Acceptance

- Open a known story trigger (e.g. `Trigger_FirstContact`) and see
  the in-game cutscene actually run: camera moves, dwarf speaks,
  voodoo cave reveals.
- Skipping a cutscene works (`sq_break` / esc).

### Risk register

- Real cinematic timing depends on exact frame pacing; we may need
  to model time the same way Diggles does (25 Hz fixed step).
- Some Diggles cutscenes assume specific class members or world
  state; missing setup may make them fall apart even with correct
  bindings. Iterate per-cutscene.

---

## Phase 7 — Event loop & timer scheduling (DONE — historical plan below)

> 📌 Implemented. `timer_event` + per-tick state bodies are live and drive
> gnome idle/AI/work each sim tick. The design note follows.

`timer_event OBJREF EVENT -interval N -repeat M -attime T` schedules
recurring events on entities. State bodies (`state NAME { … }`) run
on every tick while the entity is in that state.

### Components

- `EventScheduler` MonoBehaviour: time queue, dispatches matured
  events to entity event handlers.
- Tick driver: for each entity in a state, eval state body each
  frame (or every N frames).
- `event_get ENT -item` reads from current event payload.

### Acceptance

- Schedule `evt_pilz_aging` via `timer_event $h evt_pilz_aging
  -repeat 3 -interval 170 -attime $next`; observe the handler fire 3
  times at 170-second intervals (in game time).
- Run a `state idle {... }` body that increments a counter every
  frame; verify counter goes up.

---

## Out of scope (long-term, not blocking)

- Threading / event loops (`after`, `vwait`) — Diggles is single-
  threaded.
- General `package` / `package require` — Diggles doesn't use it.
- Standalone `tclsh` shell — we drive everything from Unity.
- Full encoding flexibility — we hard-code Latin-1 for `.tcl` reads
  (matches Diggles file convention).

---

## Reference materials

- `Docs/ENGINE_TCL_API.txt` — full extracted Diggles Tcl API
  reference (1700 lines, from `original/Doc/wiggles_tcl.htm`).
- `Docs/COORDINATE_SYSTEM.md` — axis conventions for terrain + markers
  (must-read before touching the Tcl ↔ Unity bridge).
- `re/notes/tcl_api_surface.md` — earlier RE'd surface.
- `re/notes/tcl_engine_commands.tsv` — call-frequency data from the
  original game's compiled-in command table.
