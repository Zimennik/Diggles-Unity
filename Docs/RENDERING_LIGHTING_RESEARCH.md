# Rendering & Lighting Research — Path to Native Unity

> 
> Progress **D1 ground fog LANDED** (as part of the fog-of-war shader —
> §1/§2/§9 updated); **Phase A step 3 LANDED** (`DigglesClassAsset` + `DigglesAssetResolver`
> mods→data — §5/§6/§8 updated). Phase A steps 1 (`DigglesLighting.hlsl` shared include) and
> 2 (`RenderConfig` SO) are still NOT built. D2/D3/D4 still deferred.

**Date:**. **Status:** research complete; direction chosen (see §7). Next work item = faithfulness deltas (§9).

This document maps the port's current rendering/lighting/model architecture, reconstructs the
ORIGINAL engine's rendering model (the faithfulness target), and analyses what it would take to
move toward native Unity lighting + modular, overridable models/animations/states.

Grounded in: direct reading of the 3 custom shaders + URP config, and 3 RE agents (decompile at
the decompile and the original data).

---

## 0. TL;DR — the reframe that matters

The intuition "we built custom lighting because Unity's lighting didn't work with our custom
shaders" is **imprecise**. The shaders are custom for **three independent reasons**, only one of
which is lighting:

1. **Ground material model** (not lighting). 3-way `stone/earth/moss` blend from **texture-arrays**,
   per-vertex weights (vertex color) and slices (uv1), via **MODULATE2X** (`base*detail*2`). Non-PBR,
   DX7-era. A stock `URP/Lit` **cannot** express this regardless of the lighting system.
2. **DX7 transparency emulation** (models, not lighting). `URP/Lit` won't honor `ZWrite=On` in
   Transparent mode → sort flicker. So `Diggles/Model` hand-rolls `Blend/ZWrite/AlphaToMask` + alpha
   classification (Opaque/Cutout/Transparent).
3. **Lighting** — and here:
   - The shaders **already use native URP lighting**: `GetMainLight` + `NdotL` + real shadow maps
     + `SampleSH()` ambient.
   - The only genuinely-custom lighting piece is the **`_DigLights` point-light loop** (torches /
     emitters, ≤128 lights), which reproduces the original's Gaussian `exp(-2(d/R)²)` and **bypasses
     URP Forward+ clustering flicker** + per-object light caps.

**Therefore "go native lighting" splits into 3 nearly-independent questions** (analysed in §4):
- **Q1 (lights):** real Unity point/spot lights instead of the custom buffer?
- **Q2 (materials):** ShaderGraph / stock materials instead of hand-HLSL?
- **Q3 (GI):** baked / probes / APV? — **rejected** for this game (procedural, dug at runtime).

---

## 1. Current architecture (as built)

### Shaders — `Assets/Diggles/Runtime/Shaders/`
| | DigglesGround | DigglesModel | DigglesParticle |
|---|---|---|---|
| Main light | URP `GetMainLight` + shadows | + `SampleSH` ambient | — (unlit) |
| Point lights | custom `_DigLights` buffer (Gaussian) | same buffer | — |
| Distinctive | texture-array 3-blend + MODULATE2X + per-scape detail (`_DetailFloor/Saturate/Contrast`) | DX7 ZWrite/blend + **VAT** GPU morph (10→60 FPS) | additive billboard, CPU-driven |
| Fog | native **fog-of-war** fog (warm-grey height fog `(170,170,155)` + black edge; **D1 landed**) | `MixFog` (Unity distance) + fog-of-war twin tint | — |
| Point-light shading | half-Lambert `dot*0.5+0.5` | half-Lambert | — |

### World mesh — `BdbPmpWorldBuilder.cs`
- Chunked **32×32 tiles**; per chunk **1 material per scape** present (urw/swf/kris/lava, 1–4).
- All 3 BDB sub-cells (top/ground, cliff/wall, bottom) merged into **one bucket per scape** → cliffs
  share the floor material (deliberate "diggable wall = diggable floor texture").
- Per-vertex: **COLORS** (stone/earth/moss weights r/g/b), **uv0** (tex coords), **uv1** (array slices
  `stoneSlice, mossSlice`). **No tangents.**
- **NORMALS = `RecalculateNormals` (smoothed).** The BDB *carries* per-vertex normal indices
  (`normIdxBuf`, `BdbData.cs`) but they are **unused**; `BdbPaletteEntry` has no normal field. Dedup
  key includes the tile coord → **hard normal seam at every tile border** (hidden today by weak light).

### Materials — `TerrainTextureCache.MaterialFor`
- `Shader.Find("Diggles/Ground")` (URP/Lit → Standard fallback). Binds `_StoneArr/_EarthArr/_MossArr`
  + scalar detail props. Materials cached for the world lifetime.

### class_light point lights — `RuntimeWorldSpawner`
- `AddLight` creates a bare `GameObject` (**no `UnityEngine.Light`!**). `UploadClassLights` fills
  global arrays `_DigLights/_DigLightCols/_DigLightCount` **every frame** (`DigglesWorldRuntime.LateUpdate`).
  Cap 128. ⚠️ `DigglesClass.cs:39,94` comments claiming a "UnityEngine.Light mapping" are **stale**.

### URP config — `New Universal Render Pipeline Asset(.asset / _Renderer.asset)`
- Rendering path = **Forward** (not Forward+). Main light per-pixel, shadows 2048, 1 cascade, **hard**.
- Additional lights = **per-vertex, limit 4**, no additional shadows.
- Scene `RuntimeWorldGeneration.unity`: ambient = Skybox mode; **one** `UnityEngine.Light` (directional,
  intensity 0.3, warm); **no** point/spot lights (class_lights feed the custom buffer, not Unity).

---

## 2. Original engine RE — the faithfulness target

Software **per-vertex** lighting (DX7 pre-lit `TLVERTEX`, no D3D hardware lights). **Two separate
paths** for terrain vs models.

### Terrain / cliffs (`FUN_004cc040`)
- Per-vertex shade = bilinear 4-corner brightness (**+64 ambient floor**) × fixed "sun" basis
  `3.6 + (-2.546)·nx + (-2.546)·ny` — **the vertical normal component is ×0; the sun never moves** —
  + point-light accumulation, × global gain `0.1`.
- **Static bake** (`generate_color_variation`), re-baked on dig and when lights move. Config toggle
  `ScapeLight`.
- **Cliffs**: same `FUN_004cc040` shader, shaded with per-vertex `sphere.dat` normals. ⚠️ `DAT_00b9bed0`
  is the **256-entry `sphere.dat` NORMAL table** (`fread(...,0xc,0x100,"data\sphere.dat")`), NOT an
  "82-entry colour palette" (earlier misread). Cliffs are LIT like the ground via those normals; how
  cliff ALBEDO is chosen is OPEN (needs RE).

### class_light point lights
- Array on ScapeRenderer `+0x12e4`, 7 floats `[x,y,z,R²,r,g,b]`, **rebuilt per-tile, per-frame** from
  nearby emitters (fully dynamic: `change_light/set_light/set_autolight`).
- Falloff `exp(-2(d/R)²)`, hard cull at `R` (range = falloff width only).
- ⚠️ **Correction to prior notes:** contribution is `× max(N·L, 0.1)` — **not** a flat range-independent
  peak. Our port's half-Lambert `dot*0.5+0.5` is a deliberate softer divergence.
- ~~**Terrain ONLY.** Models never read this array.~~ **REFUTED — see §2a:** models never
  read *this* array (`+0x12e4` is touched only by 4 ScapeRenderer functions), but the model renderer
  has its **own twin** of the same light list and runs the **identical per-vertex law**.

### Models (`CModelRenderer`) — CORRECTED (§2a has the full law + citations)
- ~~Toon path: one ambient/brightness per model × 2-tone palette. Immune to class_lights.~~ **WRONG.**
  The real model path: `CDisplay::DrawObjects` → `FUN_00486790:99-102` fills a per-object light array
  (`FUN_004861a0`, from the same 0x800-slot light singleton `FUN_00592d70`), then the
  "CModelRenderer Vertexprocessing" loops (`FUN_0048aa60:147-188` + 5 siblings `004899b0/0048ba80/`
  `0048a240/0048b290/0048c3f0`) call `FUN_004856e0` **per vertex** (blended keyframe position +
  blended `sphere.dat` normal `DAT_00b9bed0`) — the **same formula as terrain**: BASELIGHT base ×
  sun basis + fog-scaled Gaussian point-light sum, ×0.1 gain, 255-saturate + ≤64 overspill→specular.
  Unlit exceptions: texture modes 9 / 0x23 (glow/additive surfaces — `local_11` gate,
  `FUN_0048aa60:98-99`). So the port lighting models by the class-light buffer is **faithful in kind**
  (old D4 "divergence" withdrawn).

### §2a — The class-light COMBINE LAW

The exact native per-vertex intensity law — **the same formula for terrain and models**, two code
copies: terrain `FUN_004cc040`, models/objects `FUN_004856e0` (corner cache `FUN_004ca830` /
`FUN_00485370`). All constants byte-verified against `Diggles.exe` `.rdata`.

```
I_c(v) = (B(v)·sunBasis(N) + S(v) · Σ_i c_i,c · exp(−2·d_i²/R_i²) · max(N·L_i, 0.1)) × 0.1
DIFFUSE_c = min(I_c, 255) ← THE SATURATION — hard per-channel cap
SPECULAR_c = clamp(I_c − 255, 0, 64) ← overspill → DX7 specular (additive, texture-independent)
pixel_c = texture_c · DIFFUSE_c/255 + SPECULAR_c/255
⇒ absolute ceiling = albedo·1.0 + 64/255 ≈ albedo + 0.25, NO MATTER HOW MANY LIGHTS OVERLAP.
```

Where (citations):
- **B(v)** = `bilinear₄(BASELIGHT·16) + 64`: corner regs = `tile+0x07 << 8`
  (`FUN_004ca830:137/176/213/249`; models `FUN_00485370:24/58/92/126`), int bilinear (weights
  256×256) `>>0x14` ⇒ baselight×16 (`FUN_004cc040:58-61`), `+0x40` ambient floor.
- **sunBasis(N)** = `3.6 − 2.546·nx − 2.546·ny + 0·nz` (`FUN_004cc040:62-63`; `DAT_00737468`=3.6,
  `_DAT_0073777c`=−2.546, `DAT_007373e8`=2.546 — read from exe).
- **S(v)** = light scale from the per-player FOG byte (`tile+0x34+player`): corner = 0 if
  byte∈[1,0x7F]; else `min((byte−0x80)·1.3, 128)` (`DAT_007372e8` = **double 1.3**;
  `FUN_004ca830:148-166`, `FUN_00485370:31-51`); bilinear `>>0xf` = ×2 ⇒ ≤256, then
  **clamped ≤255** (`FUN_004cc040:124-126`). Fully-visible tile ⇒ S=255. S<0 (unexplored corners,
  −0x80) ⇒ lights skipped, base ×(1+S/256) darkening (`FUN_004cc040:67-72`, `DAT_00736fc4`=1/256).
- **c_i** = per-light channel coefficient in the per-tile/per-object render array (stride 0x1c =
  `{x,y,z,R²,r,g,b}`): `c = (float)(int)stored × 0.5`, `stored = (int)(classColour×31)`
  (`class_light` parse `FUN_00614990:189-223`, `DAT_007375a8`=31.0; builders: terrain
  `FUN_004c7d80:139-178`, models `FUN_004861a0:191-228`; z mapped `z×0.5+7`). Torch
  `{0.9,0.9,0.8}` ⇒ c = `{13.5, 13.5, 12}`.
- **Falloff/shape**: hard cull `distSq ≥ R²` (`FUN_004cc040:96`), `exp(−2·d²/R²)`
  (`:17`, exp = `FUN_006f5ca0`), `max(N·L, 0.1)` (`:13`, `DAT_00737034`=0.1).
  Accumulation = **plain per-channel SUM** over all in-range lights (`:115-117`) — no per-light cap.
- **Gain ×0.1**: terrain `renderer+0x1034` = `0x3dcccccd` (`FUN_004c4420:92`), applied
  `FUN_004cc040:134-136`; models: `cache+0x14`, same use (`FUN_004856e0:125-127`).
- **Saturation + overspill**: `FUN_004cc040:137-168` + final packs `:218` (diffuse) / `:228`
  (specular; overspill also fog-mixed `:187-189`). Identical block `FUN_004856e0:128-160/229-239`.

**Numeric consequence — why the native never blinds:** one torch on a visible tile (S=255) in a
tunnel (BASELIGHT=4): `I = ((4·16+64)·3.6 + 255·13.5·att·NdL)·0.1` ⇒ ≈390 at the pool ≫ 255 — a
**SINGLE torch already saturates** its pool (diffuse 255, spec capped 64) within ~3 tiles. The
2nd..4th overlapping torches (spacing ≈3.16 tiles) land in already-saturated territory ⇒
**bit-identical pixels**. Overlap is invisible **by design of the cap**, not by tuned intensities.

**Port transplant:** the port's
albedo-multiplier `1.0` == native diffuse 255 == "full texture brightness", so the faithful bound is:
accumulate the `_DigLights` loop into its own sum and **clamp per channel at 1.0** before adding to
the lighting term — `lighting += min(Σ torch, 1.0)`. A single torch peaks at 0.9 (< ceiling) ⇒ the
committed 9eda3ea single-torch look is **bit-identical**; overlapping torches plateau at the ceiling
like the native. The ≤64/255 specular overspill is **deliberately not routed**: natively it rides on
top of every saturated pool (single torch included); in port units the single torch sits *below* the
ceiling, so routing spill only for overlaps would fabricate a new overlap-only highlight — the exact
artifact being removed. `FogLightFactor` smoothing multiplies into `_DigLightCols` *before* the sum
⇒ veiled lights shrink the sum first, clamp after — order matches the native (S scales the sum,
saturation last).

### Other
- **Fog: YES** — warm grey `(170,170,155)`, height/distance, folded per-vertex
  (`heightFog = clamp(1 - (z-7.0)·0.125·1.02, 0,1)`).
- **Day/night: NO.** The clock does not drive lighting. (Port's 12h clock must NOT either.)
- **Shadows: YES, but projected shadow-meshes / 32×32 alpha maps** on the ground, only within
  `MaxShadowLightDist=8.0` of a light. **Not** cascaded depth maps.

### Where the port already diverged from the original
- Real movable directional "sun" + real-time depth shadow maps on ground AND models (original: neither).
- Models lit by class_lights (original: never).
- half-Lambert point lights vs `max(N·L,0.1)`.
- ~~Ground lost its fog~~ **RESOLVED:** the ground shader now folds the native warm-grey
  height fog `(170,170,155)` (D1) as part of the fog-of-war path (`DigglesGround.shader:185-205`).
→ **By the main light, the port already chose "Unity-native", not "faithful".**

---

## 3. Why each shader is custom (separate the concerns)
- **Ground albedo blend** → inherent to the faithful look; not about lighting; not expressible in stock Lit.
- **Model ZWrite/transparency** → URP/Lit limitation workaround; not about lighting.
- **VAT vertex morph** → custom feature (smooth 10→60 FPS); not in stock Lit.
- **`_DigLights` loop** → the only true "custom lighting"; exists for the Gaussian + Forward+ flicker.

---

## 4. Migration analysis

### Q1 — real Unity point/spot lights
- **Blocks today:** Forward renderer + additional lights per-vertex/limit 4; historical Forward+ flicker.
- **Reality:** the custom buffer is a *legitimate solution*, not a hack — dozens–128 mostly-static
  lights, exact Gaussian, no flicker/limits, O(lights×pixels) trivial on desktop. Real Unity lights
  give inverse-square (different look) and would light models (original didn't).
- **Why go native:** authoring workflow (place `Light` in-scene), shadows from points, GI integration.
- **Recommendation:** keep the Gaussian buffer as the faithful default; expose a shader **keyword
  seam** (`buffer ↔ URP additional lights`) so native is opt-in. Re-test Forward+ flicker on Unity 6
  (URP 17 is more mature — **verify, don't assume**).

### Q2 — ShaderGraph / stock materials (highest leverage)
- **Ground:** stock Lit impossible. Options: **(a)** custom **ShaderGraph on a Lit master** (own albedo
  node, but native main/additional/GI/shadows/fog "for free") ← best; **(b)** bake per-tile composite
  textures + stock Lit (RAM-heavy; the old `GroundTextureBlender` path).
- **Models:** ShaderGraph Lit + VAT via Custom Function in the vertex stage + alpha classification.
- **Key move:** porting both shaders to **ShaderGraph Lit** makes the materials participate in Unity's
  full lighting pipeline **without losing the custom look**, and removes hand-HLSL maintenance.

### Q3 — GI / probes / APV — REJECTED
- World is procedural and dug at runtime → baked GI & APV (need static geometry) don't fit; real-time
  GI not viable. Limit to ambient + optional reflection probe.

---

## 5. Modularity & overrides (data faithful, Unity-assembly procedural)

> **Update:** the SO descriptor + asset resolver of §6 are now BUILT —
> `Assets/Diggles/Runtime/DigglesClassAsset.cs` (+ `DigglesClassAssetRegistry.cs`) carries
> `materialOverride` / `shaderOverride` / `modelObjectOverride` / `defaultAnimOverride` /
> `animMapOverrides` / collider policy, and `Assets/Diggles/Runtime/DigglesAssetResolver.cs`
> implements the `mods/ → data/` search order (wired through `DigglesModelLibrary.Assets`).
> The per-class shader/material override is consumed in `RuntimeWorldSpawner.cs`. So the "missing"
> rows below are now partially closed; the remaining gap is a prefab/SO spawn layer + per-db
> material caching still blocking arbitrary per-object swaps.

Historically (pre-descriptor): no entity prefabs, no ScriptableObject registry, no
Addressables/`Resources.Load`; the shader was `Shader.Find`-hardcoded in **two** places
(`DigglesModelLibrary`, Editor `MarkerModelResolver`); materials cached per-db.

| Axis | Per-class | Per-object (runtime) |
|---|---|---|
| Mesh/model | ✅ TSV maps | ⚠️ only `set_alternateanimdb` (db-swap, same clip names) + cross-obj clips; no arbitrary swap |
| Material/shader | ❌ **missing** (everyone gets `Diggles/Model`) | ⚠️ texture only (`def_texture`), not shader |
| Anim clips/set | ✅ Tcl `set_class_anim/animset` | ✅ controller API, `set_alternateanimdb` |
| State/behaviour | ✅ Tcl `def_class/Inherits/states` | ✅ Tcl `state_trigger`, per-instance procs |

**Gaps (all on the Unity side):** no prefab/SO layer; shader hardcoded + materials per-db cached
(blocks per-object material/shader); no asset-path indirection for mods (`mods/ > data/`); no
per-instance whole-model swap. **Strength:** behaviour is Tcl → already moddable without recompile.

---

## 6. The unifying insight — one layer serves all three goals

> **Status: BUILT (Phase A step 3).** `DigglesClassAsset` (SO) + `DigglesAssetResolver`
> (`mods/ → data/`) now exist and are wired through `DigglesModelLibrary`/`RuntimeWorldSpawner`.
> The descriptor already carries model/material/shader/anim/collider overrides.

A per-class **ScriptableObject descriptor** + an **asset-resolution indirection** closes goals 1, 2, 3
at once:

```
DigglesClassAsset (SO): model db · default anim · anim/animset tables · collider policy ·
                         MATERIAL/SHADER · behaviour hook · (opt.) Prefab ref
        +   asset resolver with search order  mods/ → data/
        ↓   through the existing IDigglesEntitySpawner seam
```
- **(1) native light** → "use ShaderGraph Lit" becomes a descriptor field.
- **(2) override** → descriptor/prefab overrides model/material/shader/anim/state per-class & per-object.
- **(3) modularity/mods** → `mods/ > data/` + descriptor = modders replace anything incl. the shader.

Same principle already chosen for UI.

---

## 7. Decision (this session,)

- **Driver of "native light":** (a) stop hand-maintaining HLSL → ShaderGraph; (b) author lights as
  Unity `Light` components in-scene; (c) the long-term modernization principle. **NOT GI/PBR.**
- **Implication:** the point-light buffer CAN eventually be replaced by real Unity lights (for in-scene
  authoring), but only in the modernization phase — not because it's broken.
- **Start with:** the faithfulness deltas (§9), then the modular architecture (§8 Phase A).

---

## 8. Phased plan

**Phase A — make rendering modular, DON'T change the look (low risk):**
1. ⬜ **NOT DONE.** Extract lighting into a shared `DigglesLighting.hlsl` (one source of truth for
   Ground/Model) + keyword seam (`buffer ↔ URP additional`). (No `DigglesLighting.hlsl` on disk yet;
   the `_DigLights` loop is still duplicated per shader.)
2. ⬜ **NOT DONE.** `RenderConfig` SO (intensity, range scale, ambient, fog on/off, light path).
3. ✅ **DONE.** `DigglesClassAsset` (+ registry) + `DigglesAssetResolver` (`mods/ → data/`);
   per-class shader/material override routed through `RuntimeWorldSpawner`/`DigglesModelLibrary`.

**Phase B — native path as opt-in (modernization):**
4. ShaderGraph Lit variants of Ground/Model (custom albedo + VAT) → full native lighting; retires HLSL.
5. Real Unity point/spot lights via the descriptor; re-validate Forward+ flicker on Unity 6.
6. (Opt.) prefab spawn path through `IDigglesEntitySpawner`.

---

## 9. Faithfulness deltas backlog (do FIRST — RE-grounded)

**Clear wins** (improve faithfulness without regressing the player-approved look):
- **D1 — Ground fog. ✅ DONE.** The ground shader now folds the native
  warm-grey height fog `f = clamp01((1-(z-7.0)·0.125·1.02)·fogFactor)` → `(170,170,155)` plus the
  black-corner edge multiply, gated on `_FowParams.z` (fog-of-war active). See
  `DigglesGround.shader:185-205` (constants cross-checked to `FUN_004cc040` / `
  02-rendering.md §13`). The model shader carries the twin object tint (`DigglesModel.shader:236-250`).
- **D2 — BDB authored normals.** Replace `RecalculateNormals` with the BDB per-vertex normals
  (fixes tile-seam faceting; prerequisite for any stronger lighting). **Needs RE of the normal-index→
  vector decode for terrain** before coding.

**Forks** (faithful vs the player-approved look — confirm with user, don't blind-fix):
- **D3 — Point-light shading.** Original terrain = `max(N·L, 0.1)`; port = half-Lambert `dot*0.5+0.5`
  (softer, user-approved as "looks good"). Change only if stricter faithfulness is wanted.
- **D4 — Models & class_lights.** ~~Original: models immune to class_lights~~ **WITHDRAWN
  (§2a):** the original lights models per-vertex with the *same* class-light law as terrain — the
  port's model `_DigLights` loop is faithful in kind. The remaining (approved) divergence is only
  D3's half-Lambert shape.
- **D5 — Accumulation bound. ✅ DONE.** Native saturates the summed per-vertex intensity
  at 255/channel (+≤64 overspill→specular) — §2a. Ported as `min(Σ _DigLights, 1.0)` in BOTH shaders
  (fixes "3-4 torches blind the cave"; single-torch look bit-identical, sits below the ceiling).

### Entanglement finding — reshapes the deltas
The original terrain shade is a COHESIVE software formula (corner-luminance + **Z-less** directional
`3.6 − 2.546·nx − 2.546·ny` + Gaussian point lights `max(N·L,0.1)` + ×0.1 gain + HDR overspill + fog).
You CANNOT cherry-pick one piece into our Unity-native ground shader (Unity `GetMainLight`
full-vertical NdotL + real shadow maps — a different model) and get "faithful". So:
- **D2 (authored normals)** under our shader is only a MINOR geometric-quality fix (kills the per-tile
  seam-smoothing of `RecalculateNormals`); it does NOT make terrain look like the original. Decode is
  RE'd: u8 idx → `sphere.dat`[256]×3f unit vectors (= `DAT_00b9bed0`), native frame, Unity map
  `(nx, nz, −ny)`, same normal feeds directional + point N·L. Low priority → fold into Phase A.
- **D3 / D4** only cohere as part of a FULL faithful software-shading rewrite.

**The real choice for terrain lighting is binary:**
- **(A) Keep Unity-native terrain lighting** (current, approved; aligns with the modernization direction
  toward ShaderGraph Lit + real lights). Then the ONLY faithful-delta worth doing now is **D1 fog**;
  D2 is a minor quality fix (defer), D3/D4 are dropped.
- **(B) Reproduce the original software terrain shade** (full formula above) for true faithfulness — a
  real ground-shader rewrite that ABANDONS Unity's main light + real-time shadows for terrain, i.e.
  AGAINST the chosen Unity-native direction.

**Recommendation: (A)** — keep Unity-native terrain lighting; **D1 fog is now DONE** (landed with the
fog-of-war work); D2 deferred to Phase A; D3/D4 skipped. Pursue full faithfulness only as a
Phase-B opt-in shader variant if ever wanted.

---

## 10. Key files
- Shaders: `Assets/Diggles/Runtime/Shaders/DigglesGround.shader`, `DigglesModel.shader`, `DigglesParticle.shader`
  (+ fog-of-war: `FogOfWarBlack.shader`, `FogOfWarGradient.shader`; markers: `DigMark.shader`)
- Descriptor/mods: `Assets/Diggles/Runtime/DigglesClassAsset.cs`, `DigglesClassAssetRegistry.cs`, `DigglesAssetResolver.cs`
- World: `World/BdbPmpWorldBuilder.cs`, `World/BdbData.cs`, `World/GroundTextureBlender.cs`
- Lights/glue: `Engine/RuntimeWorldSpawner.cs`, `Engine/DigglesWorldRuntime.cs`, `Tcl/Engine/DigglesClass.cs`
- Models: `Model/DigglesModelLibrary.cs`, `Model/ThreeDbParser.cs`, `Engine/DigglesAnimator.cs`,
  `Engine/DigglesAnimController.cs`, `Editor/Importers/MarkerModelResolver.cs`
- Config: `Assets/New Universal Render Pipeline Asset.asset` (+ `_Renderer.asset`),
  `Assets/Diggles/Scenes/RuntimeWorldGeneration.unity`, `ProjectSettings/GraphicsSettings.asset`
- Original RE anchors: `FUN_004cc040` (terrain shade), `FUN_004ca080` (cliff), `CModelRenderer::*`
  (model shade + shadow meshes), palette `DAT_00b9bed0`, lights array `+0x12e4`.
