# Unified Animation Controller — Research & Port Plan

> The proposed architecture (§6) has SHIPPED: `DigglesAnimController`,
> `DigglesModelLibrary`, `DigglesAnimator`, the anim-tester and the Tcl bridge
> (`set_class_anim`/`set_class_animset`/`action`) all exist and are wired. The
> "Current Unity code audit" (§5) and the build-sequence note are updated below to
> reflect that; §1–4 remain the verified RE.

Goal: a single, clean animation-controller architecture for **every** entity
(dwarves, animals, bosses, buildings, props), driven by **both** the original
Tcl scripts (initially) **and** new standalone C# systems (after migration).
This doc consolidates the RE research and proposes the architecture + an
anim-tester scene to validate it.

> Companion: `re/notes/ANIMATION_SYSTEM.md` (engine RE, nav + WalkClimbAction).
> This doc is the **port plan**; sections 1–5 are verified findings, sections
> 6–8 are the proposal.

---

## 1. How animation works in the original (recap)

- **10 FPS vertex-morph.** No bones, no skinning, no interpolation. Each anim
  frame is a full mesh snapshot stored in the `.3db`. The engine advances a
  frame index by real elapsed time and swaps the mesh.
  `frame = startFrame + round((now − start) * 10 + 0.05)` (`CAnimCtrl::GetFrameIdx`,
  `FUN_00462040`). Constants: FPS `DAT_007373f0 = 10.0`, round `DAT_00737190 = 0.05`.
- **Three modes** (`animinit.tcl:4-6`): `ANIM_STILL=0` (freeze at startFrame),
  `ANIM_ONCE=1` (play once, freeze on last, engine silently flips itself to STILL,
  **no callback**), `ANIM_LOOP=2` (wrap to 0 forever).
- **The engine never auto-chains** `start → loop → end`. Whoever started the anim
  (a Tcl script or an engine *action*) must know the clip length and schedule the
  next clip. This is why every compound move is authored as a `_start`/`_loop`/`_end`
  triple.
- **Clip = anim record** in the `.3db`: `Name`, frame count, mesh indices, a
  per-loop `MotionOffset` (root motion, used to derive walk speed) and
  `RotationDelta`. (`ThreeDbParser` reads these as `VecA`/`VecB`.)
- **Name resolution**: `set_anim this pilz.standard …` → split on `.` →
  `obj="pilz"`, `anim="standard"` → find the `.3db` whose `Objects[]` has that
  obj → find the anim by name within the object → `(db, objIdx, animIdx)`.

---

## 2. Tcl animation control surface (what scripts call)

Three layers: per-instance raw playback, per-class declarative tables, and the
engine *action* that consumes the tables.

| Command | Signature | Role | Engine handler |
|---|---|---|---|
| `set_anim` | `set_anim <obj> <name>.<anim> <startFrame> <mode>` | per-instance: start a concrete clip **now** | `FUN_0069faa0` |
| `class_defaultanim` | `class_defaultanim <name>.<anim> ?mode?` | the clip an instance shows at spawn | `FUN_00613520` |
| `set_class_anim` | `set_class_anim <state> <name>.<anim>` | build per-class **state→clip base map** | `FUN_00615650` |
| `set_class_animset` | `set_class_animset <N> { {state name.anim} … }` | build named **animset #N** (override map for a gait) | `FUN_00616d30` |
| `action … anim <state>` / `action … walk -animsets {…}` | — | engine **resolver + start/loop/stop sequencer** | (action subsystem) |
| `snd_anim` → `sound anim` | `snd_anim <name>.<anim> <frame> <snd>` | per-frame sound trigger (mostly commented out in shipped data) | engine `sound` |

**Who picks the clip:** *scripts* select the logical state and the animset-list
policy; the *engine action* resolves `<state>` → concrete clip via the class
table and sequences `start`(ONCE) → `loop`(LOOP) → `stop`(ONCE). The engine does
**not** auto-pick `set_class_anim` entries.

**Animsets** (defined in `classes/zwerg/z_anims.tcl:1276-1661`) are override maps
selected per path-segment by the walk action. Each carries the full slot set
(`standard, walk_start/loop/loop_wave/stop, turn_left/right_90/180, climb_*,
ladder_*, ground_to_wall, wall_to_ground, ladder_to_ground`):

| ID | Const | ID | Const |
|----|-------|----|-------|
| 0 | STANDARDWALK | 9 | FLEE |
| 1 | RUN | 10 | ZOMBIE |
| 2 | WALKTIRED | 11 | DIVER |
| 3 | SWIM | 12 | SKIPPWALK |
| 4 | WALKWITHPANNIER | 13 | DRUNKENWALK |
| 5 | HAMSTERRIDE | 14 | WALKFIT |
| 6 | HOVERBOARD | 15 | BARRELWALK |
| 7 | WALKWITHBOX | 16 | WALKSTRIKE |
| 8 | SNEAK | 256/257/258 | FIGHT KUNGFU / SWORD / TWOHAND |

A `-1` in an animset-list slot means "this gait forbidden" (e.g. tired can't run).
Other creatures (hamster, troll, wuker, fenrir, baby, …) define their own
sets 0/1/2 with smaller vocabularies.

---

## 3. Per-class animation catalog (summary)

**Dwarves — `mann` / `frau` (class `Zwerg`)** — richest by far (`mann.3db`, **1023
anims**). Spawn pose `mann.standard`. Master map: `classes/zwerg/z_anims.tcl`.
- Locomotion: walk/run/tired/fit/drunk/sneak/flee/zombie/swim cycles + turns +
  wall climb (`kletter_*`) + ladder (`leiter_*`) + mount/dismount.
- **Work with material variants** (`_holz`/`_metall`/`_stein`): build
  (`aufbauen_*`), dig (`buddeln_*`), generic work (`werkeln*`), hammer, chop,
  saw, plane, chisel, weld, drill, pneumatic hammer (~60 clips), laser drill;
  plus per-building work (farm, brewery, bar, temple, kitchen, hospital, …).
- Carry (box / pannier), leisure (sit/sleep/eat/bathe/gym/theatre/disco/…),
  a large systematic `talk_*` dialog family, full combat (kungfu/sword/twohand/
  shield with strikes, blocks, evades, 4 hit severities), death (`sterben`,
  `kletter_sterben`).
- 17 walk animsets + 3 fight animsets (see §2).

**Baby** (`baby.3db`): walk/crawl/climb/cry/school/tantrum; 3 animsets.

**Animals**: `Hamster` (`hamster.3db`, 28 anims — idle/sleep/clean/beg/eaten/hit
+ 3 gaits walk/hop/gallop + wheel), `Riesenhamster` (battle mount),
`Fisch`/`Schmetterling` (single decorative loop), `Spinne` (full creature: idle/
walk/run/jump/climb/hang/threaten/spin/death).

**Bosses / story creatures**: `Troll` (`troll.3db`, 280 anims — locomotion + rich
social set + `131a_`/`300a_` cutscene-prefixed clips), `Wuker`, `Fenris`
(`fenrir.3db` — combat + kneeling wounded phase + drink/drunk), `Drache` +
`Drachenbaby` (`drache01.3db` — sit/stand/fly pose-switching + fire-breath),
`Krake` (tentacle dummies).

**Buildings / production** (`produktionsstaetten.3db`, classes in `classes/work/`
& `classes/sparetime/`): construction stages + a working/machine loop + destroyed
look. See §4.

**Static props** (`oberwelt.3db`, ~143; also `wandboden`, `kristall`, `lavawelt`,
…): each has one `<obj>.standard`, played STILL (truly static — most trees/walls/
grass), LOOP (animated banners/tents), LOOP-with-random-start (crowd desync), or
texture-scroll (`class_defaulttxtanim`). Generated in bulk by `SetDummyClassesAll`
(`animinit.tcl:259-358`).

**Priority for the tester:** `Zwerg`(mann) → `Hamster` → a building (`Saegewerk`)
→ `Troll` → `Drachenbaby` → other bosses → `Baby` → 3 prop modes.

---

## 4. Building construction stages — the precise mechanism

The recollection ("each stage is a frame of an animation") is *directionally*
right but mechanically: **each stage is a separate single-frame named anim**, and
the stage is selected by `set_anim … STILL` (freeze on frame 0). There is **no**
engine-side `progress% → frameIndex` math — it all lives in Tcl.

- `set_buildupanim` (`misc/genericprod.tcl:776-814`) maps an integer
  `buildup_step` to a letter clip and freezes it:
  ```tcl
  switch $buildup_step { 0 {bau_a} 1 {bau_b} 2 {bau_c} 3 {bau_d} 4 {bau_e}
                         5 {bau_f} 6 {bau_g} 7 {bau_h} 8 {bau_i} 9 {bau_k}
                         default {standard} }
  set_anim this $cname.$ani 0 0;# 0 0 = startFrame 0, ANIM_STILL
  ```
  Letter sequence is `a,b,c,d,e,f,g,h,i,k,l` — **"j" is skipped**.
  `buildup_step > max_buildup_step` → `standard` (finished).
- Each `bau_*` / `standard` / `zerstoert` is a **single-frame** anim in the
  `.3db`. The working machine
  anim (e.g. `saegewerk.saegen`, 11 frames) is a normal LOOP.
- The **step counter is advanced by the worker dwarf**, one step per build action
  (`z_work_common.tcl:473-539`, `prod_buildup_rep`/`prod_builddown_rep`), which
  also plays the dwarf's `aufbauen_*` clip at a build-link point. The building's
  own mesh and the dwarf's work anim are **separate things** — don't conflate.
- `max_buildup_step = [llength $build_dummys]` (per-class obj_init). The number of
  `bau_*` meshes present can exceed the steps actually shown.
- **Destroyed**: `set_anim this $cname.zerstoert 0 1` (fallback `kaputt`)
  (`genericprod.tcl:227-233`). **Partial damage** = fire/smoke particles scaled by
  `1 − hp/maxhp` (`show_damage`), **not** a mesh change.

**Unity primitive needed:** `animator.SetAnim("bau_c", 0, Still)` — freeze on a
named single-frame anim. Already supported by `DigglesAnimator`.

---

## 5. Current Unity code audit (most gaps now closed)

| Component | State | Verdict |
|---|---|---|
| `Runtime/Engine/DigglesAnimator.cs` | Vertex-morph, STILL/ONCE/LOOP, named-clip dict, `MotionOffset`/`RotationDelta` exposed, bounds-union anti-flicker, `AddClip` + per-clip `Materials` + per-frame link offset/rotation. | **Kept** as the low-level layer. Supports buildings (STILL + named clip). |
| `Runtime/Engine/DigglesObject.cs` | Base entity: animator ref, velocity, `SpawnFaceWorld`, current action. | **Kept.** |
| `Editor/Importers/MarkerModelResolver.cs` | Builds clips at **import** time, still only a **hardcoded ~40-name preload** (`CommonClipPreloadNames`, `:245`) + the default clip. | **Superseded** for the tester by the runtime loader — the full-clip path is `DigglesModelLibrary` (see §6/§7 decision). This importer is now the model-browser / world-spawn preload only; mesh decode uses the bbox decode (`:424`). |
| Tcl bridge (`Runtime/Tcl/Engine/`) | `set_anim` **real** (`ApplyAnim`); `class_defaultanim` **real** and now **captures the mode arg** into `DigglesClass.DefaultAnimMode` (`CmdClassDefaultAnim`, `DigglesTclEngine.cs:5290`). | **Done.** |
| `set_class_anim` / `set_class_animset` | **REAL** (`CmdSetClassAnim`/`CmdSetClassAnimset`, `DigglesTclEngine.cs:5306/5315`) → fill `DigglesClass.AnimMap` / `Animsets`. The trailing face/eye payload on `set_class_anim` is still unused. | **Done** (payload = FACE_ANIMATION.md §7 follow-up). |
| `action` / `play` | **REAL** (`CmdAction`, `DigglesTclEngine.cs:182/5340`): verbs `anim`/`wait`/`walk`(→MetaWalk nav + animsets)/`rotate` with finish/break callbacks; `play` handled in the sequencer paths (`:2606/2688`). The resolver/sequencer layer exists on the Tcl bridge. | **Done.** |
| `DigglesClass` | Now has `DefaultAnim` + `DefaultAnimMode` + `AnimMap` + `Animsets` (`DigglesClass.cs:22/28-29`). | **Done.** |
| `Runtime/Tcl/Engine/ClimbAction.cs` / `MetaWalkAction` | The single C# move executor; gaits resolve THROUGH the class animsets (`z_anims.tcl set_class_animset`), no hardcoded clip names. (Replaces the old `WalkClimbAction`.) | **Done.** |

---

## 6. Proposed architecture — three layers

```
  ┌─────────────────────────────────────────────────────────────┐
  │ HIGH: Tcl bridge commands Native standalone systems │
  │ set_anim / class_defaultanim / (AI, production, │
  │ set_class_anim / animset / movement, cutscene) │
  │ action anim|walk │
  └───────────────┬─────────────────────────────┬────────────────┘
                  │   both call the same API     │
                  ▼                              ▼
  ┌─────────────────────────────────────────────────────────────┐
  │ MID: DigglesAnimController (NEW MonoBehaviour) │
  │ • holds the class's AnimMap + Animsets (from DigglesClass) │
  │ • PlayState(state, mode?, onFinish?) → resolve + sequence │
  │ • SetGait(animsetId) for locomotion override │
  │ • SetBuildupStep(n) / SetDestroyed for buildings │
  │ • PlayClip(name, frame, mode) low-level passthrough │
  │ • handles start→loop→stop chaining the engine won't do │
  └───────────────────────────┬─────────────────────────────────┘
                              ▼
  ┌─────────────────────────────────────────────────────────────┐
  │ LOW: DigglesAnimator (EXISTING, keep) │
  │ • named clips, 10 FPS morph, STILL/ONCE/LOOP, MotionOffset │
  └─────────────────────────────────────────────────────────────┘
```

Data on `DigglesClass`:
- `AnimMap: Dictionary<string state, AnimRef>` (from `set_class_anim`)
- `Animsets: Dictionary<int id, Dictionary<string state, AnimRef>>` (from `set_class_animset`)
- where `AnimRef = (objName, clipName, mode)`.

Why this shape:
- **Tcl-driven now:** implement `set_class_anim`/`set_class_animset` to fill the
  maps; implement `action … anim|walk` to call `PlayState`/locomotion. Scripts
  keep working unchanged.
- **Smooth migration:** native systems call the *same* `DigglesAnimController` API
  directly — no Tcl in the hot path. We can move subsystems off Tcl one at a time
  without touching the animator.
- **Buildings unified:** `SetBuildupStep(n)` is just `PlayClip("bau_<letter>", 0,
  Still)` with the letter table from §4; same component, no special case.

---

## 7. Clip loading — the blocker to solve first

A full anim-tester needs **every** clip of an object, not the ~40 preloaded names.
Two viable strategies (decision pending — see §8):

- **A. Import-time "load all".** Extend the resolver to bake every anim of an
  object into the imported asset. Simplest at runtime; heavy import/memory (mann
  ≈ 11 573 meshes — but meshes are already cached per `(db, meshIdx)` so it's
  bounded by unique meshes, one-time).
- **B. Runtime/editor on-demand clip builder.** A runtime-accessible `.3db`
  parser + mesh builder that produces a `DigglesAnimClip` by `(obj, clipName)` on
  request, cached. Lazy and forward-looking — the final standalone build must load
  `.3db` at runtime anyway, so this doubles as that foundation. Requires moving
  parsing out of the Editor-only assembly.

---

## 8. Anim-tester scene plan

A new scene + a tester tool that exercises **all objects, all animations, all
details**:
- **Object picker** — list the spawnable classes/objects (priority list §3).
- **Full clip list** for the selected object (needs §7), with per-clip metadata:
  frame count, duration (`frames/10`), `MotionOffset`, `RotationDelta`.
- **Playback controls** — play any clip with chosen mode (STILL/ONCE/LOOP),
  frame scrubber, loop/once indicator.
- **Buildings** — a build-step slider `bau_a … bau_k → standard`, plus
  `zerstoert`/`kaputt` and the machine-running loop.
- **Locomotion** — animset/gait switcher (walk/run/tired/swim/…); show that the
  same logical state resolves to different clips per animset.
- **Acceptance:** every clip of every priority object renders without errors;
  build stages step correctly; idle/walk/work/climb/combat/death all play.

---

## Decisions

1. **Clip loading → runtime on-demand loader.** Move `.3db` parsing + mesh
   building into a runtime-accessible service; build any clip by `(obj, name)` on
   request, cached. This doubles as the foundation the final standalone build
   needs (the game must load `.3db` at runtime anyway).
2. **Build order → controller + Tcl first, then the tester.** (The runtime loader
   is the prerequisite for both, so it lands first.)
3. **Tester UI → runtime UGUI in-scene.** A dedicated holder for the current
   object + a separate camera rendering to a `RenderTexture`, shown via a `RawImage`
   on a Canvas alongside the controls. Works in Play mode and in a build; reusable
   as a standalone debug tool.

## Build sequence — status

1. **Runtime clip loader** ✅ — `Runtime/Model/DigglesModelLibrary.cs`: parses
   `.3db` (parser+`TgaLoader`+`ThreeDbData` moved to `Runtime/Model/`), builds any
   clip by `(obj, clipName)` lazily, caches db/mesh/material/texture, reads the
   StreamingAssets TSV maps. `GetClipNames`, `GetClip`, `ObjectNames`, `ResolveClass`.
2. **`DigglesAnimController`** ✅ — `Runtime/Engine/`: `PlayClip`/`PlayState`/
   `PlayStartLoop`/`PlayOnceThen`/`SetBuildupStep`/`SetDestroyed`/`Idle`, resolve
   via active animset→base map, time-based start→loop chaining. `DigglesAnimator`
   gained `AddClip` + per-clip `Materials`. `DigglesClass` gained `AnimMap`,
   `Animsets`, `DefaultAnimMode`.
3. **Tcl wiring** ✅ — `set_class_anim` / `set_class_animset` populate the
   class tables; `class_defaultanim` captures its mode. The `action … anim|wait|
   walk|rotate` resolver is now built (`CmdAction`, `DigglesTclEngine.cs:5340`):
   `walk` builds a MetaWalk over the nav graph with Tcl-supplied animsets, `anim`
   runs an `AnimAction` on the controller, all with finish/break callbacks.
4. **Anim-tester scene** ✅ — `Animation_Test.unity` + `Runtime/Tools/DigglesAnimTester.cs`:
   spawns the picked object under a holder on the `AnimPreview` layer, a dedicated
   camera renders it to the `AnimPreview` RenderTexture shown by a RawImage. UGUI
   (TMP) object filter + dropdown, clip dropdown, mode, frame scrub, build-step
   slider; drag over the RawImage to yaw the model. **Verified:** models, anims,
   building stages all render/play correctly.

## Runtime model loading — verified facts (READ before touching mesh decode)

- **Vertex decode (corrected — earlier RE notes were wrong).** Each component is a
  16-bit value in **offset-binary clustered tightly near 0x8000**; the real
  coordinate occupies only a small window (~hundreds of values), and the per-mesh
  `BBox` (`ThreeDbMesh.BBoxMin/Max`) is the **exact AABB** of the decoded verts.
  Correct decode = per mesh, find each axis's actual unsigned value range across
  ALL submeshes, then `world = BBoxMin + (u−uMin)/(uMax−uMin)·(BBoxMax−BBoxMin)`.
  - A naive full-range linear map (`(u+32768)/65536`) throws verts to the bbox
    **corners → renders as a CUBE**. The old `MarkerModelResolver.DequantizeUnitCube`
    (unsigned/65535 → unit cube) collapses them ~**200× too small**. Both wrong.
  - Empirically scale ≈ 0.25 across mann + hamster, all axes. `ThreeDbMesh.Dequantize-
    Vertex` (signed) is **unused/wrong** — ignore it.
  - `MarkerModelResolver` (world-spawn / campaign browser) was **migrated to this
    same bbox decode**. It bakes the shared
    `DigglesModelLibrary.ModelToWorldScale` into the verts. Empirically the unit-cube
    and bbox decodes differ only by a single global constant (`unit-cube ≈ bbox_raw ×
    0.006098`, ratio stable to ~0.15 % across mann/hamster/buildings/props/troll and
    ~1.5 % across animation frames), so the OLD unit-cube path was a uniform mis-scale,
    **not** a per-frame distortion. The scale was first set to 0.006098 (to preserve
    the old size) but then **fixed to 0.005** (the engine-faithful 1/200) after a
    direct in-game size comparison: on our 1-tile = 1-unit world a dwarf ≈ 1.05 units,
    matching the game. This is ~0.82× the old size, so world characters shrank — the
    few ABSOLUTE `WalkClimbAction` offsets (climbStandoff/mountRise/leg, ~0.2) may want
    a minor recheck; the nav GRAPH (terrain-derived) is unaffected. Both paths now
    produce identical geometry; `ThreeDbScriptedImporter` keeps its own unit-cube decode
    (dev-tool / model-browser path only).
- **Orientation.** Models are authored head at mesh −Y, face at mesh +Z. Stand
  upright head-up with **world** rotation `Euler(180,0,0)` (mesh −Y→+Y, face→−Z).
  Use WORLD rotation, not local — `ObjectHolder` may carry a `Q_x(−90)` that would
  lay a locally-rotated model on its side.
- **Culling.** `Diggles/Model` now exposes `_Cull` (default `Off` = double-sided,
  preserves the world look). Solid models want single-sided (`Back`) or interior
  faces show through (e.g. a mushroom cap's underside). The tester sets Back for
  Opaque/Cutout, Off for Transparent. Triangle winding is correct (Back culls the
  right faces — verified, models are not inside-out).
