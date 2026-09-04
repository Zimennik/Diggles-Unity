# Cutscene / Cinematic Sequencer — design & port plan

> **Status: ENGINE DONE + HEADLESS-VERIFIED; Play presenter LIVE (refactored into DI services).**
> All 9 tasks complete: the full cutscene command surface (camera/dialogue/audio/particles/
> screen-FX/elf/story/actor) is implemented as pure-logic `*State` objects driven by the real
> sequencer.tcl Tcl + ticked by `DigglesGameSim`. Acceptance gate met: NUnit EditMode tests step
> real cutscenes through the sequencer with 0 exceptions (see `Tests/Editor/CutsceneSequencerTests.cs`,
> `CutsceneRotateBeatAndSkipTests.cs`, `MenuCutsceneTests.cs`, `PostCutsceneChurnTests.cs`).
>
> **Play integration (ARCHITECTURE CHANGED):** the old monolithic
> `DigglesCutscenePresenter` no longer exists — it was split (VContainer migration) into
> Inspector-wired, DI-registered scene services: `CutsceneCameraDriver` (camera pose),
> `ElfPresenter` (guide-elf model + verbs), `ParticlePresenter` (FX), `SubtitleView` (dialogue UI),
> `AudioController` (speech/streams), `ScreenManager` (fade/FX) — registered in
> `GameLifetimeScope`/`DigglesGameInstaller`; trace via `CutsceneTrace`. Cutscenes now play in
> Play unattended: the campaign **intro (`urw_3`) auto-plays on New Game** (`DigglesWorldRuntime.
> campaignIntroCutscene`), and the **main menu shows random backdrop cutscenes**
> (`PlayMenuCutscene`/`PlayRandomMenuCutscene`). Remaining refinements: exact isometric camera
> framing, the ~40 particle-type presets (decompile `Particle.cpp`), MP3 streaming polish,
> trigger-proximity auto-activation. The headless-verified ENGINE below is authoritative.

**Testing tool:** tick `showCutsceneTester` on the `DigglesWorldRuntime` in your scene
(e.g. `RuntimeWorldGeneration.unity`; set `mode=CampaignTcl` + `loadRealClasses` to get real
dwarves), press Play → an in-scene panel lists every cutscene under `Scripts/sequences`. Pick one
and **Play** (stage into the current world) or **Reload+Play** (respawn a fresh world first — the
faithful retest, since a cutscene mutates the world). Cutscenes are staged into the *spawned*
world, so set the runtime's campaign/environment to match the cutscene you're testing.
Code: `Runtime/Tools/CutsceneTester.cs` + `DigglesWorldRuntime.PlayCutscene/StopCutscene/
ReloadAndPlay`.

Companion docs: `TCL_INTERPRETER_PLAN.md`, `ANIMATION_CONTROLLER.md`, `FACE_ANIMATION.md`.
The authoritative behaviour spec is the original Tcl `sequencer.tcl` (2269 lines) — the
decompile does NOT cover the sequencer (TclGlobalMethods.cpp un-decompiled); engine
primitive contracts come from `ENGINE_TCL_API.txt`.

---

## 1. The core insight (why nothing plays today)

A cutscene = a **Trigger object** running a state machine (`eval_sequence`) over a plain-text
**script file** read **line by line**, each line `eval`'d as a Tcl command. The DSL
(`sq_*` / `do_*`) is **defined as Tcl procs INSIDE `sequencer.tcl`**, not native code.

Our engine currently registers ~270 of these names as **native no-op stubs**. Native commands
shadow Tcl procs in dispatch (`TclInterpreter.cs:139-140`), so the real sequencer procs never
run. **The whole port strategy is: un-shadow the Tcl (delete the shadowing stubs) and implement
the ~15 native LEAF primitives the procs actually call.** Same pattern that fixed clothing
(`scan`) and the face system (`set_textureanimation`).

Shadowing stubs to REMOVE (let the real Tcl run):
- File I/O: `open`, `gets`, `close` (`eof` too if stubbed) — `misc/file_wrapper.tcl` implements
  them in pure Tcl over `load_file` + `split "\n"` + a seek index. **No native file streaming
  needed.**
- The DSL: `sq_pen sq_actor sq_camera sq_object sq_wait sq_text sq_audio sq_color sq_sound
  sq_screenvibe sq_activate do_action do_wait do_particle do_text do_elf do_fee do_change
  do_tooltakeout do_toolputaway do_script` and the `+`/`-` variants — all defined in `sequencer.tcl`.
- `set_speechout`, `speechicon`, `screenvibe`, `set_view`, `set_viewpos`, `set_camerafollow`,
  `camera_set`, `seq_audiostream`, `adaptive_sound`, `set_sequence`, `set_sequenceactive`,
  `get_sequencebreaked`, `get_camerafollowdist`, `get_cameramoving`, `get_view`, `calc_text_time`,
  `textwin`, `start_fade`, `create_particlesource`, `set_particlesource`, `change_particlesource`,
  `set_roty`, `db_animlength`, `get_anglexz`, `get_alternateanimdb`, `get_classaniminfo`,
  `park_obj_actions`, `restore_obj_actions`, `remove_parked_obj`, `set_undeletable`, `get_mapedit`,
  `option`, `locale`, `gametime`, `sm_send_message`, `sm_get_event`/`sm_set_event`, `trigger`,
  `set_eyefocus`, `free_face_anim` (some already real) → implement for REAL.

Keep stubbed (out of cutscene scope): fight, production, level-gen, AI, map-edit, inventory, …
unless a target script needs them.

---

## 2. Runtime model (faithful)

**Sequencer = Trigger instance.** Member vars declared at `sequencer.tcl:302-354` (actors,
enable_list, wait_for, file, wait_time_var, viewpos, audio_marker, is_breaked, …). Each object
has its own Tcl scope (per the object-model work).

**`sequencer.tcl` is dual-loaded** via the `if {[in_class_def]} {…methods/states/events…} else
{…procs + member init…}` pattern: once during a Trigger class definition (adds methods/states),
once at instance init (defines the `sq_*`/`do_*` procs + initialises members). `classinit.tcl`
deliberately skips it as a standalone class (`classinit.tcl:73`).

**Activation** (`sequencer_activate`, `:362`): `gametime factor 1.0`; hide text; park idle dwarf
actions; arm eye-focus timer; `set viewpos [get_view]` (save camera to restore on exit); resolve
actors (actuator list or nearest player Zwerg); `parse_file` opens the script → `init_sequencer`
(`set_sequence 1`, reset enable-lists); `state_reset/state_trigger eval_sequence/state_enable`.

**Driver** (`eval_sequence` state, `:228`) — called once per sim tick while enabled. Returns
early (yields the frame) if any GATE is unsatisfied, in order:
1. break gate (`get_sequencebreaked`) → drain whole script this tick (see §4) then `shut_sequencer`.
2. `wait_time` (`:1374`): if `wait_time_var>0`, remaining=`min(0.5, wait_time_var-gettime)`; if >0
   it `state_disable this; action this wait <t> {state_enable this}` and returns (re-arms in ≤0.5s
   polling chunks); ≤0 clears and proceeds.
3. elf-busy gate (`wait_forelf && enable_elf`).
4. camera gate (`wait_camera`): hold while `get_camerafollowdist>4.0 || get_cameramoving`.
5. actor gate: for each `i` with `wait_for[i]==1`, if `enable_list[i]==1` (busy) → return.
Then `parse_next 0`; false (EOF) → `shut_sequencer`.

**`parse_next`** (`:588`) = the dispatcher. Reads lines (`gets $file`), trims, skips blank/`#`.
Prefix: `-` → skip when breaked; `+` → run even when breaked (sets `disable_state`); none → normal.
`eval $command`. **Blocking convention (`:626`): `wait_next = [string equal -length 3 "do_"
$command]`** — any `do_*` line stops the reader and yields; `sq_*` lines run and the reader
immediately continues. So a step = a batch of non-blocking `sq_*` setup + one blocking `do_*`.
On the very first line, if auto-idling, injects `sq_auto_idle`.

**Completion tracking.** `do_*` actor commands `sequence_disable $nr` (enable_list[nr]=1=busy)
and issue engine `action $actor <verb> <opts> {finish} {break}` where finish = `call_method this
seq_enable $nr` → `sequence_enable` sets enable_list[nr]=0=free. The per-frame actor gate observes
the flag. `sq_wait` (`:1305`) arms which actors the next `do_*` blocks on (`all`/`none`/`{0 1}`/`elf`).

**Time.** Single float clock `gettime` (= sim GameTime), scaled by `gametime factor`. Timed waits
= absolute deadlines polled in ≤0.5s `wait`-action chunks (above). We already have GameTime +
TimerScheduler + the action layer.

**Teardown** (`shut_sequencer`, `:535`): `set_camerafollow -1`; `restore_obj_actions`; clear speech;
`set_sequence 0`; re-enable actor state machines; `close $file`; `trigger delete this`; restore
saved camera (`set_view $viewpos…`); `destroy`; `incr sequence_counter`.

**Skip/break** (see §4): `get_sequencebreaked` true → `while {[parse_next 1]} {}` drains
to EOF in one tick with effects suppressed (each `do_*`/`sq_*` early-returns on `$is_breaked`,
doing only instantaneous end-state: beams/teleports/hide/event-flags/`+`-forced lines run; waits/
anims/sound collapse). Ensures correct end-state when the player skips.

---

## 3. Native primitives to implement (grouped) — the leaves

| Group | Commands | Binding |
|---|---|---|
| Camera | `set_view x y zoom dx dy [move freeze speed set]`, `set_viewpos x y z zoom dx dy …`, `get_view [1]`, `set_camerafollow <obj|-1> [zoom]`, `camera_set <speed> <waypoints>`, `get_camerafollowdist`, `get_cameramoving`, `viewlock`, `set_view_begin` | Unity `Camera` controller; interpolate over sim time; isometric framing (focus x,y on map plane + zoom + dx,dy rotation). |
| Text/Dialogue | `set_speechout <maxtime> <{color text}×4> [force]`, `speechicon <obj> add/clear/lock`, `textwin hide/show`, `calc_text_time <txt>`, `option get TextSpeed`, `locale` | TMP/Canvas subtitle bar + per-actor speech bubble; text from `data/scripts/text/sequences/<file>_<locale>.txt` via `parse_text`. |
| Audio | `seq_audiostream open/play/stop/close`, `adaptive_sound …`, `sound` | Real AudioClips/AudioSource; soundmarker files `data/scripts/sequences/soundmarker/<name>_<locale>.txt`. **Format: see §6.** |
| Particles | `set_particlesource`, `create_particlesource`, `change_particlesource`, `free_particlesource`, `particle_processing` | Convert original FX format → Unity `ParticleSystem` + our controller. **Format: see §6.** |
| Screen FX | `screenvibe att sus dec amx frx amy fry`, `start_fade <dur> <0=to|1=from>`, `cancel_fade`, `lightning`, `laserbeam`, `change_light` | Camera shake; fullscreen fade overlay; etc. |
| Sequencer state | `set_sequence 0|1`, `set_sequenceactive <obj> 0|1`, `get_sequenceactive`, `get_sequencebreaked`, `park_obj_actions <classes>`, `restore_obj_actions`, `remove_parked_obj <obj>`, `gametime start/stop/factor` | Freeze/unfreeze world AI; cutscene-mode flag; break flag (player skip). |
| Story/Trigger | `sm_send_message`, `sm_get_event`/`sm_set_event`, `trigger get_actuators/delete`, `set_undeletable`, `get_mapedit` | Minimal StoryManager + trigger lifecycle to instantiate & run a sequencer. |
| Actor glue | `action <obj> walk/run/anim/rotate/beam/move/wait …`, `set_roty`, `db_animlength <obj.anim>`, `get_anglexz <p1> <p2>`, `get_alternateanimdb`, `get_classaniminfo`, `set_eyefocus`, `start_fanim`, `get_objgender` | Mostly exists (action layer, anim controller, face). Fill gaps (rotate-by-angle, move, db_animlength, get_anglexz). |
| Elf | `elf move/path/crash/anim/text/lookat/sleep/standard/hide/idle/beam/finishcode/action` | Guide character object + verbs (used by `do_elf`). |

Engine command contracts: `ENGINE_TCL_API.txt`. Positions resolve via `parse_pos` (literal 3-list
| actor index | named "pen" var set by `sq_pen`).

---

## 4. Test strategy (the acceptance bar)

Headless NUnit EditMode tests (run via Unity 6 batch mode — confirmed working, see below) that
drive REAL sequence scripts through the actual `sequencer.tcl` + native primitives and assert:
- script progresses line-by-line to EOF (no unhandled command, no exception);
- camera transform reaches expected framing after `sq_camera`/`do_wait camera`;
- `do_text` sets the speech-out text/color/duration;
- `do_wait time` advances the clock; actor gates release on action-finish;
- audio: AudioSource gets the right clip + play window; particles: emitter created with params;
- skip/break drains to correct end-state.

Primary targets (increasing complexity): `Test/Gespraech.tcl` (camera+walk+rotate+multi-actor
dialogue), `Test/seq_test2.tcl`, a `Menue/start_*`, `Urwald/ErsterKontakt.tcl`. Then a broad
"every command resolves" smoke test across all `sequences/**`.

**Headless runner:**
```
<Unity install>/Editor/Unity.exe -batchmode -nographics \
  -projectPath "<project root>" -runTests -testPlatform EditMode \
  -testResults "<project root>/test-results.xml" -logFile "…/unity-test.log"
```

---

## 5. Phased plan (tasks #1–#9 in the task list)

1. **Foundation** — un-shadow stubs (file I/O + DSL); load `file_wrapper.tcl` + `sequencer.tcl`;
   instantiate a Sequencer trigger; tick `eval_sequence` via `DigglesGameSim`; drive Gespraech to EOF.
2. **Camera** — Unity camera controller + all camera primitives + interpolation.
3. **Dialogue/text** — speech UI + text-file loader + calc_text_time + option/locale.
4. **Audio** — seq_audiostream + adaptive_sound + sound + real audio loading + soundmarker loader.
5. **Particles** — set/create/change_particlesource + FX-format → ParticleSystem converter.
6. **Screen FX** — screenvibe + fade + lightning/laserbeam/change_light.
7. **Actor glue** — do_action verbs + set_roty/db_animlength/get_anglexz/face/tool/muetze.
8. **Elf + StoryManager/Trigger** — guide character + minimal story/trigger lifecycle.
9. **Comprehensive headless test suite** over real scripts (acceptance).

---

## 6. Asset formats (audio + particles)

_(Filled from research; see findings below.)_

### Audio — ZERO format work (all standard containers)
Two subsystems + the cutscene stream, all driven by existing Tcl:
- **`sound`** (SFX/speech, was DirectSound; Steam port uses Media Foundation): `sound bind
  <logic> <file.wav>` (name→clip), `sound anim <anim> <frame> <snd> [material room vol pitch …]`
  (fire on an anim frame), `sound animnoseq` (not during cutscenes), `sound play <snd> <vol>
  [pos]` (3D one-shot), `sound playglobal <snd> <vol>` (2D), `sound volume`, `sound animsound
  <0|1>`. Materials: any/sand/soil/stone/wood/metal/water/lava. Rooms: any/tunnel/small/medium/big.
  Data: `init/soundinit.tcl` (+ `z_soundinit.tcl`, `t_soundinit.tcl`) = pure bind+anim data.
  Helpers: `snd_anim`, `snd_animlist`.
- **`adaptive_sound`** (streaming music): a theme = intro + fight + N standard MP3 streams +
  positional markers. Verbs: `setintrostream/setfightstream/addstandardstream <theme> <file>`,
  `marker <theme> <pos>` / `markerobj` / `markerenable/disable/del…`, `changetheme/changethemenow`,
  `fight/primary/normal/changemode`, `start/stop/autofight`, `basevol/volfact`. Data:
  `init/adaptiveinit.tcl`. Bound to triggers via `sm_music_theme`; `sq_music`→`adaptive_sound marker`.
- **`seq_audiostream open/play <start> <stop>/stop/close`** — one cutscene voice MP3 with
  start/stop offsets (from soundmarker files). MP3s in `data/Sound/Sequenzen/`, `data/Music/`.

**Files:** 1227 `.wav` (RIFF/WAVE PCM, mostly mono 22050 Hz 8/16-bit) under `data/Sound/<cat>/`;
216 `.mp3` (standard MPEG) under `data/Music/` (`m001..m112`, `na001..`, menu/theme) + Sequenzen.
All directly importable. **Unity:** name→`AudioClip` dict; `play pos`→`PlayClipAtPoint` (3D) /
2D AudioSource; `sound anim`→fire off the anim controller's frame events; `adaptive_sound`→a
small intro/standard[]/fight crossfade state machine over AudioSource + positional marker zones;
`seq_audiostream`→one streaming AudioSource with a play-window. (Engine: `SoundSrcMgr.cpp`,
`SoundMultiplexer.cpp`, `SoundAnimMgr.cpp`, `DXSound.cpp`; only string-level in the decompile.)

### Particles / FX — no file format; integer-`type` presets baked in `Particle.cpp`
Effects are spawned imperatively; the visual identity is an int `type` (0..~40), each a preset
hardcoded in the engine (texture + blend + gravity + lifetime + size + color-ramp + spread).
Tcl surface:
- `change_particlesource <obj> <srcidx> <type> <relpos> <dir> <max_particles> <num_per_frame>
  <vis_in_fog> [link_id] [max_rng] [objverts] [submesh]` — configure a persistent source slot.
- `set_particlesource <obj> <srcidx> <activity>` — on/off. `blow_particlesource <obj> <idx> <dir>`
  — one-shot burst. `free_particlesource <obj> <idx>`. `create_particlesource <type> <pos> <dir>
  <num> <time>` — temporary self-expiring world-space source. `obj_to_particle <obj> <type>` —
  emit from every mesh vertex. `class_particle …` — in-`def_class` per-instance source.
  `particle_processing <bool>` — global enable.
- Params carried: type, relpos/pos, direction (velocity), max_particles, num_per_frame,
  is_vis_in_fog, optional link_id (attach to a model link-point — same system as hats), max_rng,
  objverts, submesh. Render modes: `particle_additive` / `particle_blend` (alpha). Shared textures:
  9 TGAs `data/Texture/Misc/particle_{a..e}_*.tga` (import as-is).

**Unity:** build a lookup of ~40 `ParticleSystem` presets keyed by the same int IDs + a controller
that the 6 commands configure (relpos via link-point/submesh, direction, rate, cap). The per-type
internals are recoverable only by decompiling `Engine\Particle.cpp` (funcs near 0x00492250,
0x00492ec0, 0x00495940, 0x00498360, 0x00498590, 0x0049a330, 0x0049aaa0, 0x0049b130). Plan:
implement the full command surface + controller now (NOT a stub), seed presets from the
rate/count/direction args + eyeballing; refine the preset table from the decompile as a follow-up
for exact look. (Engine TCL dispatch: `CTclGlobalMethods::CreateParticleSource`,
`CTclObjMethods::ChangeParticleSource/SetParticleSource/FreeParticleSource/ObjToParticle`.)

---

## 7. Voice-window law

The native `seq_audiostream` handler is `FUN_00667160` (the function header confirms address
`0x00667160`; the `seq_audiostream` name string at VA `0x0072e320` is registered by the push site
near `0x0061d9a8`). Its `play <start> <stop>` branch parses the two floats, calls
`SoloudAudioPlayer::SetStopPosition` and `SetPosition`, then `Run`. The audible stop is therefore
the stream's **absolute media stop position**, not a subtitle countdown and not completion of a
talk animation (`../../re/decompiled/Diggles/_global/00667160_FUN_00667160.c:160-180`).

The Tcl sequencer deliberately derives three related pieces of state from the same marker:

- `do_text` reads `start`/`stop` and computes `time = stop-start`, then starts the stream
  (`../../original/data/Scripts/classes/story/sequencer.tcl:1603-1619`).
- It gives that duration to `set_speechout` and either an actor wait or `talktime_list`
  (`sequencer.tcl:1642-1665`).
- `seq_talk` keeps the actor gate busy while the authored deadline has not elapsed **or** another
  authored talk animation remains. That explains why the original can visibly wait for an
  animation, but this gate does not extend the audio segment (`sequencer.tcl:124-175`).

Port contract:

1. Before a presenter starts (headless mode, async decode, missing file), `AudioState.Tick` drains
   `stop-start` on the sim clock. The window always terminates and cannot wedge a sequence test.
2. Once the `AudioSource` starts, its decompressed sample/media position owns the remaining window.
   `AudioController` schedules the DSP end and stops at the authored absolute marker, so sim catch-up
   cannot truncate a line.
3. A marker-backed subtitle binds to that same remaining value. `DialogueState` suspends its own
   countdown while bound, so audio and subtitle finish together in both headless and presented modes.
4. `stop`, `close`, and `set_sequence 0` synchronously clear the audio and its bound subtitle. This
   preserves the skip drain; the native `set_sequence 0` likewise stops and closes the stream
   (`../../re/decompiled/Diggles/_global/00649080_FUN_00649080.c:85-105`).

`Obw_Odin001_en.txt` contains 28 positive absolute windows, including `001a`
`55.453804..58.299623`; every one of the 27 `do_text` lines in `odin_001.tcl` resolves to one of
those markers (`../../original/data/Scripts/sequences/soundmarker/Obw_Odin001_en.txt:5-32`,
`../../original/data/Scripts/sequences/Oberwelt/odin_001.tcl:115-321`). Regression coverage is in
`Assets/Diggles/Tests/Editor/IntroCutsceneRepairTests.cs`.
