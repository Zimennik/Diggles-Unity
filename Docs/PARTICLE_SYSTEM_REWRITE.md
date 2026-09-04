# Particle System — From-Scratch Rewrite (Design Doc)

> ⚠️ **ARCHITECTURE SUPERSEDED (pivot).** This doc's **render backend** decision — a Unity
> `ParticleSystem` (Shuriken) translation layer with per-type emitter classes (`IParticleEmitter`,
> `ShurikenParticleEmitter`, `CustomEmitters/…`, `ParticleTypeTable` replacing `ParticlePresetTable`) —
> was **reversed**. The shipped implementation is a **faithful CPU port**: Unity is used ONLY as an
> additive billboard renderer. None of §2/§5/§6/§7's Shuriken/`SetParticles`/`TextureSheetAnimation`
> classes were built. **What DID land and is still accurate:** the RE ground-truth in §1, §3, §4 and the
> "Behaviour families" — those describe the original engine and match the code today.
>
> **Doc → real code map (current):**
> - `ParticleSystemState.cs` — KEPT as planned (Tcl command state; `Sources`/`Temps`).
> - `ParticleType.cs` + `ParticleTypeTable.cs` — LANDED as planned (43-entry code-defined table; the §2.1
> enums/record match `Assets/Diggles/Runtime/Engine/Particles/ParticleType.cs`, with a few added fields:
> `SizeMode`, `AtlasRow0/Rows/Col0/Cols`, `FlipFrames`, `SizeJitter`, `Sway`, `EmitterWander`).
> - `DigglesParticlePresenter.cs` (planned) → **`ParticlePresenter.cs`** (real; `MonoBehaviour, IStartable`,
> `[Inject] DigglesTclContext`, drives everything in `LateUpdate`/`Pump`). Got a **per-source `SourceView`
> cache** (transform/mesh/material/cell-map no longer re-resolved every frame).
> - `IParticleEmitter` + `ShurikenParticleEmitter` + `CustomEmitters/*` (planned) → **NONE built.** All types
> run through one **`CpuParticleEmitter.cs`** which switches on `DispatchKind` internally
> (`IntegrateStandard/Swirl/Spiral/Ground/Flock`). `DispatchKind.Rope` is still a **TODO** (falls back to
> standard integration). The `ParticleLcg.cs` (faithful RNG) also landed (not foreseen here).
> - Shader (§6) → `DigglesShaders.Particle`; materials keyed by `(ParticleTex, renderMode)` in the
> presenter, atlas loaded via `TgaLoader` from `<DataRoot>/Texture/Misc` (the §7 path is accurate).
>
> Status: CPU port BUILT; Play-tuning of fog gate / blow impulse / intensity ongoing (see
> `memory/reference_particle_ingame.md`). Read this doc for the RE facts, not the architecture.

**Status (original doc):** DESIGN — for review before implementation.
**Decision:** render backend = ~~Unity `ParticleSystem` (Shuriken) + a
Tcl→PS translation layer~~ → **faithful CPU integrator (`CpuParticleEmitter`), Unity = billboard renderer
only**; **all 42 active types ported faithfully in one pass** (setId 5 is inert — no update fn in the
original). Source of truth = the reverse-engineered `Engine/Particle.cpp` (see
`memory/reference_particle_system.md`) + extracted constants in `re/notes/particle_constants.txt`.

> The implementation BEFORE this doc was built on a **wrong** RE (fabricated "render class → physics", alpha
> mislabeled "budget", invented atlas/collision/gradients, integrator never read). It was discarded.
> Salvaged & kept: the Tcl command layer + `ParticleSystemState.cs` (correct, headless-tested).

---

## 1. How the original actually works (recap)

```
Tcl change_particlesource <obj> <slot> <type> {relpos} {dir} <max> <num/frame> <visfog> [link][rng][verts][submesh]
   │ class_particle (per-class template, replayed at spawn) · create_particlesource (temp world) · obj_to_particle
   ▼
SOURCE record (per obj/slot) ── ParticleSystemState.Source (KEEP)
   ▼ per frame
EMITTER (0x88): pos, base-vel(=dir), drift-accum, particle sub-pool, state, max_range²
   ▼ dispatch by setId (FUN_00493e30 switch → 42 hand-coded fns)
PER-TYPE UPDATE FN ── 3 phases:
   A) emitter random-walk drift pos += rand; *= damping
   B) SPAWN ~num/frame particles pos = emitter + spread(shape); vel = dir + jitter; age=0; atlas cell; billboard mode
                                    (gated by active + !paused + not-fully-fogged)
   C) INTEGRATE live particles pos += vel; gravity/buoyancy/drag; COLOUR from age; age++; kill at ageCap or dist>rng
   ▼
RENDER: additive billboard quad (TLVERTEX: pos+rhw, diffuse, specular, uv)
   · FUN_00497c60 screen-aligned · FUN_00497090 velocity/world-oriented
   · atlas cell = particle.uv (low16=U tile, high16=V tile); grid = per-type tier {4,8,16}
```

**The per-setId baked preset is only `{texIndex 0..5, renderMode 0x0f/0x10, alpha 70..255}` + the atlas
grid tier `{4,8,16}`.** Everything dynamic (motion, colour, lifetime, spawn) is the hand-coded per-type
function. There is **no colour in the preset** — colour is computed per-frame from particle age inside the
type fn (two families: flat white + additive texture, or a warm/grey ramp). The alpha is a per-preset
material constant; only the warm-ramp types write per-vertex alpha.

---

## 2. Component architecture

> ⚠️ SUPERSEDED PLAN (see top banner). The file list below is the abandoned Shuriken design. Real files:
> `ParticleSystemState.cs`, `ParticleType.cs`, `ParticleTypeTable.cs`, `ParticlePresenter.cs`,
> `CpuParticleEmitter.cs`, `ParticleLcg.cs` (all under `Assets/Diggles/Runtime/Engine/[Particles/]`).
> `IParticleEmitter`, `ShurikenParticleEmitter`, and the `CustomEmitters/` classes were NOT built — their
> logic lives as `DispatchKind` switch-cases inside `CpuParticleEmitter`. §2.1's enums/record DID land.

```
Diggles.Runtime (asmdef)

ParticleSystemState.cs KEEP — POCO; Source/TempSource records driven by Tcl commands.
ParticleTypeTable.cs NEW — the 43-entry RE-truth table (code-defined). Replaces ParticlePresetTable.
ParticleType.cs NEW — one type's full spec (struct/record) + enums.
DigglesParticlePresenter.cs REWRITE of ParticlePresenter — reconciles State→emitters each LateUpdate,
                                          routes by DispatchKind, owns the 6 shared materials, temps→bursts.
IParticleEmitter.cs NEW — interface { Configure(type, source, ctx); SetActive; Burst; SetWorldPose; Tick }.
ShurikenParticleEmitter.cs NEW — IParticleEmitter over a Unity ParticleSystem (config-driven types).
   CustomEmitters/ NEW — IParticleEmitter impls that drive a PS via SetParticles (PS = renderer only):
     GroundCollisionEmitter.cs            — heightfield bounce/stick/slope-reflect (types 1,8,10,11,15,17,18,26).
     SwirlEmitter.cs                      — type 4 (age-driven expanding ring).
     SpiralEmitter.cs                     — type 31 (expanding helix).
     RopeEmitter.cs                       — type 25 (Verlet chain → LineRenderer, not PS).
     FlockEmitter.cs                      — type 33 (elf aura: leader/follower cohesion + self-spawn + bounds-wrap).
Shaders/DigglesParticle.shader REWRITE — clean additive billboard, atlas UV × vertex colour. (+ a Stretched variant if needed.)
```

Delete: `ParticlePresetTable.cs`, `ParticleEmitterController.cs`, `ParticlePresetTable.asset`(+meta), the old shader's hacks.

**DI / scene wiring:** unchanged pattern — `DigglesParticlePresenter` is a scene MonoBehaviour on
`DigglesControllers`, `[Inject]`s `DigglesTclContext`, registered via the installer (`IStartable`). It reads
`ctx.Particles` (the kept state) every `LateUpdate`, in gameplay AND cutscene (no `SequenceActive` gate).

**Why a translation layer (the player's idea, validated):** ~25 types are pure Shuriken config; the layer is
the one place where the ~17 special types branch to a `CustomEmitter` without polluting the common path.
Unity gives us GPU billboard rendering, sorting, culling, pooling for free; the custom emitters reuse the
same PS as a renderer via `SetParticles` (except the rope → `LineRenderer`).

### 2.1 Interfaces

```csharp
public enum ParticleTex { A128d=0, B128d=1, C256d=2, E256=3, D256d=4, A256d=5 } // == texIndex load order
public enum SpawnShape { Point, Box, Disc, Cone, Sphere, MeshVerts }
public enum AtlasMode { FixedCell, RandomCell, FlipbookByAge, DirIndexed }
public enum ColourMode { FlatWhite, ConstantArgb, WarmRamp, GreyRamp }
public enum Billboard { Screen, VelocityStretched }
public enum SpawnCount { Fixed, JitteredPlusMinus1, Single, GatedProb }
public enum DispatchKind { Shuriken, ShurikenSubEmitter, GroundCollision, Swirl, Spiral, Rope, Flock, Inert }

public sealed record ParticleType {
    int SetId; ParticleTex Tex; int RenderMode; byte Alpha; int AtlasGrid;  // baked preset facts
    DispatchKind Dispatch;
    // spawn
    SpawnShape Shape; Vector3 SpreadExtent; float SpreadRadius; SpawnCount CountMode; float GateProb;
    Vector3 VelJitter; bool VelFromDirRadial;
    // visual
    AtlasMode Atlas; int FixedU, FixedV; int[] FlipU, FlipV;   // flipbook seqs (36/37/38)
    Billboard Bill; ColourMode Colour; uint ConstArgb; float StartSize; float SizeRate;  // +grow/shrink
    // motion (per-frame consts from re/notes/particle_constants.txt; scaled by FpsRef at build)
    Vector3 Gravity; float Drag; bool Buoyancy; int BuoyancyFrames; float Damping;
    // lifetime
    int AgeCap; bool DistanceKill; bool GroundInteract;
}
```

`ParticleTypeTable.All[43]` is **code-defined** (RE fact, not designer content — matches the project's
reuse-not-author stance and "POCO/clean-arch-later"). An optional `ScriptableObject` override layer can be
added later for HD/tuning without changing the runtime path.

---

## 3. Calibration & coordinate model

- **Frame-rate reference `FpsRef` (default 12, tunable).** The original integrated per render-frame; vels
  are per-frame, ageCaps in frames. Map: `lifetime_s = AgeCap / FpsRef`, `emissionRate = num/frame · FpsRef`,
  `velocity(/s) = perFrameVel · FpsRef`, `gravity(/s²) = perFrameAccel · FpsRef²`. One global knob; calibrate
  visually against the smith/waterfall.
- **Coordinates.** `relpos` and `dir` are NATIVE-space vectors (same space as entity positions; **−Y = down**).
  Convert with the existing native→world path (`RuntimeWorldSpawner.NativeToLocal`/`WorldRoot`). The emitter
  GO is parented under the entity so a moving parent carries it; `localPosition = NativeToLocal(relpos)`.
  ⚠ This is the class of bug the old code got wrong (local/world velocity space) — handle explicitly + a test.
- **Size.** `StartSize` = the per-type `+0x28` value in native world units (0.4 / 0.5 / 0.6 / 1.15 / 1.3 /
  1.5 / 1.7 / 1.8 / 2.0 / 2.5 / 4.5 / 5.5 / 6.5 / 8.0). NOT the 4/8/16 tier (that's the atlas grid). A single
  global `SizeScale` knob calibrates billboard size to match the original's screen projection.
- **RNG.** Unity PS randomness replaces the LCG; for custom emitters use `Unity.Mathematics`/`System.Random`
  seeded per-emitter (vary by index, never `Math.random` in headless). Birth-age randomization (to desync the
  flipbook/fade across a batch) → PS `Start Frame = random` / random start-time.

---

## 4. The 43-type table

Render facts (`Tex`,`Mode`,`α`,`Grid`) from `FUN_00495f60`/`FUN_00498360` + the integrator size-tier switch.
Behaviour from the per-type fns. `Size` = startSize. `Life` = AgeCap (frames). `Bill` = billboard. `D` =
DispatchKind. Effects from real Tcl usage.

| id | effect | Tex | Mode | α | Grid | Shape | Size | Life | Bill | dist-kill | Colour | D |
|----|--------|-----|------|---|------|-------|------|------|------|-----------|--------|---|
| 0 | smith ember (disc, rising) | E256 | 10 | 100 | 4 | Disc | 2.15 | 32 | Screen | yes | WarmRamp | Shuriken |
| 1 | ground spark/splash | A256d | 0f | 255 | 8 | Box | 2.5 | 100 | Screen | bounds | FlatWhite | **Ground** |
| 2 | warm ember glow | A128d | 10 | 180 | 16 | Disc | 1.15 | 32 | Screen | yes | WarmRamp | Shuriken |
| 3 | **smith fire** (bigger 2) | A128d | 10 | 170 | 16 | Disc | 2.15 | 32 | Screen | yes | WarmRamp | Shuriken |
| 4 | swirl/shock ring | B128d | 0f | 255 | 8 | Point | 0.5 | 32 | Screen | no | FlatWhite | **Swirl** |
| 5 | *(inert — no fn)* | A128d | 10 | 255 | 16 | — | — | — | — | — | — | Inert |
| 6 | smoke (from mesh verts) | A128d | 0f | 255 | 16 | MeshVerts | 2.5 | 32 | Screen | yes | FlatWhite | SubEmitter? |
| 7 | dir spray streak | D256d | 0f | 200 | 4 | Box | 4.5 | 32 | **Stretch** | yes | ConstGrey | Shuriken |
| 8 | dust puff (sphere, ground) | B128d | 0f | 255 | 16 | Sphere | 0.4 | 95 | Screen | bounds | FlatWhite | **Ground** |
| 9 | smoke puff (point) | B128d | 0f | 255 | 16 | Point | 1.5 | 111 | Screen | yes | FlatWhite | Shuriken |
| 10 | droplet (1/frame, bounce) | B128d | 0f | 255 | 16 | Point | 0.4 | 95 | Screen | bounds | FlatWhite | **Ground** |
| 11 | **forge spark fountain** | A128d | 0f | 255 | 16 | Disc | 2.5 | 64 | Screen | both | FlatWhite | **Ground** |
| 12 | drift smoke (flipbook) | A128d | 0f | 255 | 16 | Point | — | 64 | Screen | yes | FlatWhite | Shuriken |
| 13 | smoke puff (warm, trail) | C256d | 10 | 100 | 4 | Disc | 2.0 | 65 | Screen | no | WarmRamp | SubEmitter |
| 14 | radial burst (decel) | C256d | 10 | 70 | 4 | Sphere | 2.0 | 64 | Screen | yes | GreyRamp | Shuriken |
| 15 | ground-hug debris | B128d | 0f | 100 | 16 | Disc | 0.6 | 95 | Screen | bounds | FlatWhite | **Ground** |
| 16 | warm rising cone+flash | C256d | 10 | 100 | 4 | Cone | 0.4 | 47 | Screen | no | WarmRamp | Shuriken |
| 17 | **Späne/shavings** | B128d | 0f | 255 | 8 | dir-fan | sm | 100 | Screen | bounds | FlatWhite | **Ground** |
| 18 | **Funken/sparks** | B128d | 10 | 170 | 8 | Point | 0.6 | 32 | Screen | bounds | WarmRamp(yellow) | **Ground** |
| 19 | **Staub/dust** | E256 | 0f | 100 | 4 | dir-box | — | 128 | Screen | yes | FlatWhite | Shuriken |
| 20 | dust/dirt cloud (brown) | C256d | 0f | 100 | 4 | Box | 1.7 | 32 | Screen | yes | ConstArgb(olive) | Shuriken |
| 21 | mist/spray (drift) | D256d | 0f | 170 | 8 | Box | 2.5 | 32 | Screen | both | FlatWhite | Shuriken |
| 22 | rising steam (sparse) | D256d | 0f | 200 | 4 | Box | 5.5 | 32 | Screen | no | ConstGrey | Shuriken |
| 23 | grey puff (gated ¼) | D256d | 0f | 200 | 8 | Box | rnd | 32 | Screen | no | ConstGrey | Shuriken |
| 24 | cell-anim sheet (buoyant) | D256d | 0f | 170 | 8 | Box | 2.5 | 32 | Screen | yes | FlatWhite | Shuriken |
| 25 | **beam/rope** (F6 test) | B128d | 10 | 220 | 8 | chain | 1.3 | 8 | Screen | no | fade | **Rope** |
| 26 | **Späne/shavings** | B128d | 0f | 255 | 8 | dir-fan | sm | 100 | Screen | no | FlatWhite | **Ground** |
| 27 | warm smoke (mesh verts) | A128d | 10 | 180 | 16 | MeshVerts | 1.15 | 32 | Screen | yes | WarmRamp | Shuriken(mesh) |
| 28 | rising grey smoke (small) | E256 | 10 | 170 | 4 | Point | 0.15 | 32 | Screen | yes | GreyRamp | Shuriken |
| 29 | rising grey smoke (disc) | E256 | 10 | 170 | 4 | Disc | 1.8 | 32 | Screen | yes | GreyRamp | Shuriken |
| 30 | rising grey smoke (disc) | E256 | 10 | 170 | 4 | Disc | 0.5 | 32 | Screen | yes | GreyRamp | Shuriken |
| 31 | expanding spiral | B128d | 0f | 255 | 8 | Point | 0.5 | 32 | Screen | no | FlatWhite | **Spiral** |
| 32 | warm sparkle burst | C256d | 10 | 180 | 4 | Sphere | 2.0 | 12 | Screen | bounds | WarmRamp | Shuriken |
| 33 | **ELF AURA** (flock) | C256d | 10 | 100 | 4 | Sphere | 8.0 | 65 | Screen | yes | WarmRamp(gold) | **Flock** |
| 34 | grey smoke (disc+mesh) | E256 | 10 | 100 | 4 | Disc/Mesh | 6.45 | 32 | Screen | yes | GreyRamp | Shuriken(mesh) |
| 35 | spinning settling dust | A128d | 0f | 255 | 16 | Box/Mesh | 4.5 | 32 | Screen | yes | FlatWhite | Shuriken(mesh) |
| 36 | poof burst A (9-frame) | A256d | 10 | 100 | 8 | Point | 6.5 | 10 | Screen | no | FlatWhite | Shuriken |
| 37 | poof burst B (9-frame) | A256d | 10 | 100 | 8 | Point | 6.5 | 10 | Screen | no | FlatWhite | Shuriken |
| 38 | poof burst C (9-frame) | A256d | 10 | 100 | 8 | Point | 6.5 | 9 | Screen | no | FlatWhite | Shuriken |
| 39 | long glow (z_methods) | A256d | 10 | 100 | 8 | Point | 6.5 | 48 | Screen | no | FlatWhite | Shuriken |
| 40 | long glow (z_methods) | A256d | 10 | 100 | 8 | Point | 6.5 | 48 | Screen | no | FlatWhite | Shuriken |
| 41 | warm ember cloud (pulse) | C256d | 10 | 180 | 4 | Sphere | 2.0 | 12 | Screen | bounds | ConstArgb(gold)+flicker | Shuriken |
| 42 | **flame tongue** (stretch) | E256 | 0f | 100 | 4 | Cone | rnd | 63 | Screen* | no | ConstArgb(ember) | **Stretch** |

\* 42 sets billboard byte 0 but stores a radial stretch axis → render as Stretched.

### Behaviour families (shared template; per-type deltas above)

- **Warm-ramp fire (0,2,3):** disc spawn (`angle=(rand%360)·π/180`, radius `(rand%100)/{100,180,…}`),
  buoyancy on +Y for first `BuoyancyFrames` (6 for 2, 16 for 3/0), `age²` flipbook (`V=col0, U=(rand%3..4)`),
  warm specular brightness by horizontal distance + diffuse alpha-fade. → PS: Cone/Disc shape, Color-over-Life
  (warm→transparent), Size grow, TextureSheet (curve), Gravity slightly +Y.
- **Grey rising smoke (28,29,30,34):** point/disc, wind-drift (emitter-drift accel ×0.1), `age<16` rise,
  size grows, grey brighten-then-`(age−k)·k`-fade; 29/30 add pull-to-center (radial force). → PS: shape +
  Color-over-Life + Size grow + (Force toward emitter for 29/30).
- **Stationary flipbook poofs (36,37,38):** no motion; size grows; explicit 9-frame `(V,U)` tables:
  36 V=[1×8,2] U=[0..7,0]; 37 V=[2×7,3,3] U=[1..7,0,1]; 38 V=[3×6,4,4,0] U=[2..7,0,1,0]. → PS: one-shot burst,
  Size grow, TextureSheet with explicit frame list / start-frame, no velocity.
- **Static glows (39,40):** stationary, slow grow, single fixed cell (0x40007 / 0x20005), 48-frame life. → trivial.
- **Box/sphere puffs (14,20,21,22,23,24,32,41):** shape emit + drag/buoyancy + size grow/shrink + flicker
  (32/41 re-roll atlas cell each frame) + bounds-recycle (24,32,41 reset to (2,2) on leaving the map rect). →
  PS: shape + Limit-Velocity(drag) + Size-over-Life + Random TextureSheet; bounds-recycle ≈ lifetime.
- **Mesh-vertex emission (6,27,34,35):** spawn from the entity's model vertices (objverts / `obj_to_particle`).
  → PS Shape = Mesh (needs the entity `Mesh`); the presenter passes the entity's `MeshFilter`.
- **Velocity-stretched (7,42):** 7 indexes the atlas by velocity direction; 42 stores a radial stretch axis
  + exponential size shrink (flame). → PS Renderer = Stretched Billboard; 7's dir-indexed cell ≈ a single cell.
- **Ground-collision (1,8,10,11,15,17,18,26):** sample the L2M heightfield (`FUN_00592f10`) → bounce / stick /
  per-axis slope-reflect (`vel ^= sign-mask` = negate) / downhill-roll / on-impact atlas swap (10) / settle.
  → `GroundCollisionEmitter` custom updater (see §5); Unity's world Collision module covers ~80% but not the
  slope-reflect/roll/impact-swap.
- **Custom motion:** 4 swirl (radius∝age spiral-out), 25 rope (Verlet chain + distance-constraint →
  `LineRenderer`), 31 spiral (helix radius∝age, sinks), 33 elf aura (leader/follower flocking + self-spawning
  children + bounds-wrap + random flash re-seed).

---

## 5. Custom emitters (the ~17 non-stock types)

> ⚠️ SUPERSEDED FORM (see top banner): these are NOT separate classes. Each became a `DispatchKind`
> switch-case (`IntegrateSwirl/Spiral/Ground/Flock`) inside the single `CpuParticleEmitter`; `Rope` (25) is
> still a TODO and integrates as standard. The physics described below is the ground-truth still targeted.

All conceptually keep a CPU particle array (mirroring the 0x44 struct fields we need:
pos, vel, age, size, colour, cell, flags) and each frame run the faithful integrator (constants from
`particle_constants.txt`), then push to the renderer:

- **`GroundCollisionEmitter` (1,8,10,11,15,17,18,26):** spawn per the type's shape; integrate `pos += vel`,
  apply gravity; each step sample terrain height under the particle (reuse `NavGraphService`/terrain mesh
  raycast or the L2M data) + the ±neighbour heights → on penetration negate the appropriate velocity
  component (slope reflect) + dampen, or stick/march-down + snap age (kill-soon), swap atlas cell on impact
  (10). Renderer = shared PS via `SetParticles`. *(Heightfield source: prefer the loaded L2M/terrain we
  already have over the rendered collider, to match the original's per-tile byte sampling.)*
- **`SwirlEmitter` (4):** bursty gate; each particle `pos.xz = emitter + cos/sin(age·k)·dt·age` (radius∝age),
  `vel.y` slowly drops; size grows; flat white; life 32.
- **`SpiralEmitter` (31):** like swirl but a sinking helix; constant white; sparse gate.
- **`RopeEmitter` (25):** Verlet chain — each particle links to its predecessor at fixed spacing; per frame
  relax a distance constraint; render via **`LineRenderer`** (not PS), white, ≤8-frame fade. (Only the F6
  debug source uses it; low priority but in-scope.)
- **`FlockEmitter` (33 — elf aura, hardest):** sphere-shell spawn; per particle: random-walk jitter, steer
  toward a stored leader index (cohesion `(leaderPos−pos)·k`), self-spawn children (new particle inherits
  parent index), bounds-wrap recycle, ~9% chance/frame to flash (age←0x3f, size←1.5, white spec). Gold
  diffuse `0xffe060`, warm specular fade. Render via PS `SetParticles`. Summoned by `Riesenelfe_Sequenz`
  `obj_init` (`change_particlesource this 0 33`); test via the boss summon in cutscene `riesenelfe_460a`.

---

## 6. Shader (`Diggles/Particle`, rewrite)

Minimal additive billboard (URP), no keyword guesswork:
- `Blend One One`, `ZWrite Off`, `ZTest LEqual`, `Cull Off`, queue Transparent.
- frag: `col = SAMPLE(_BaseMap, uv) * IN.color;` then add the specular contribution
  (`col.rgb += IN.specular.rgb` if we feed specular as a 2nd vertex stream) — mirrors the original TLVERTEX
  diffuse+specular additive. **Drop the `×3` brightness hack** (textures are dim by design; additive
  accumulation + correct alpha handles it — verify in Play, expose one `_Intensity` if needed).
- UVs come from PS TextureSheetAnimation; `_BaseMap` = the per-`Tex` atlas (one shared material per texIndex).
- `renderMode 0x0f vs 0x10`: TBD whether they differ (both additive in practice) — start identical; if a
  visible difference shows up, add a 2nd blend variant. (Likely 0x10 = ZTest/sort tweak.)
- Stretched variant: same shader, PS Renderer set to Stretched Billboard (no shader change needed).

---

## 7. Texture loading

6 atlases from `<DataRoot>/Texture/Misc`, load-order = enum: `particle_a_128d, particle_b_128d,
particle_c_256d, particle_e_256, particle_d_256d, particle_a_256d`. Loaded lazily by the presenter via
`TgaLoader`, one shared `Material` per texIndex (keyed by `ParticleTex`). Grid for sampling = the per-type
`AtlasGrid` (4/8/16), NOT a per-texture constant (same texture is sampled at different grids by different
types — set on the PS TextureSheetAnimation per emitter).

---

## 8. Rollout (all 42 faithful — internally phased for safe landing)

> 📜 Historical plan. The CPU port landed (steps 1-5 done; step-6 Play calibration ongoing). Steps that say
> "Shuriken path / PS TextureSheet" were realised as CPU integrator paths in `CpuParticleEmitter` instead.

1. **Framework + data:** `ParticleType`/`ParticleTypeTable` (fill all 43 from this doc + constants),
   `IParticleEmitter`, `DigglesParticlePresenter` skeleton + shared-material loading + shader. Delete old files.
2. **Shuriken path (`ShurikenParticleEmitter`):** the ~25 config types. Calibrate `FpsRef`/`SizeScale` on
   smith fire (3) + waterfall mist (21) + dust (19).
3. **Mesh-vertex emission (6,27,34,35)** + **Stretched (7,42)** + **SubEmitter (13)**.
4. **GroundCollisionEmitter (1,8,10,11,15,17,18,26)** — the heightfield family (steinmetz/schreinerei/forge).
5. **Custom motion (4 swirl, 31 spiral, 25 rope, 33 elf flock).**
6. **Calibration & verify in Play:** forge (3/6/11), workshops (17/18/19/26), elf aura (33 via
   `riesenelfe_460a`), waterfalls/crystals. Tune the global knobs; refine `renderMode` if needed.

Build-gate each phase: `dotnet build Diggles.sln` clean; headless tests for the kept state layer stay green;
add a coordinate round-trip test for relpos/dir native→world.

## 9. Open calibration knobs (tune in Play, not blockers)

- `FpsRef` (default 12) — frame→second scale for life/velocity/gravity.
- `SizeScale` — global billboard size ↔ original screen projection.
- `renderMode 0x0f/0x10` — confirm if visually distinct.
- Exact double angle/radius scales are known (π,180,100,300,350,380,850); per-type damping/gravity/jitter
  values are in `re/notes/particle_constants.txt` (resolve the `DAT_` address → value when authoring each row).
```
