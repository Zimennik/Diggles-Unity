# Tunnel torches + underground darkness — RE of the original and the port contract

> Problem statement: in the original, caves/tunnels are DARK; the first time a gnome walks
> through them wall torches light them up; when the gnome leaves, the light dies and the space
> is swallowed by grey fog.
> The grey veil is already implemented (Docs/FOG_OF_WAR_RE.md). This document pins down the REST:
> where the torches come from, what autolight is, and how the original DARKENS unlit underground.
> All addresses = Diggles.exe (imageBase 0x400000), from the decompile (`_global` namespace).

## 0. Verdict (short)

Three mechanisms, each hanging off a DIFFERENT system:

1. **Torches are REAL objects of class `Misc_Light`, auto-spawned by the engine** near gnomes that
   carry the Autolight flag (the data contains NOT ONE authored Misc_Light — all tunnel lighting is
   produced at runtime). Model fackel (fackel.standard), flame = class_particle 2,
   light = class_light r=6 {0.9 0.9 0.8}, class_snaptowall 1 (emitter.tcl:3-11).
2. **"Goes out when you leave" is EMERGENT from FoW**: a torch is an ordinary object; its model is
   rendered only at fow ≥ 0x80, the flame at ≥ 0x81, and the full grey veil (f=1) erases the
   light's contribution to the terrain (lerp to flat grey). There is NO separate extinguish
   mechanism; a torch lives forever (no removal from the glow queue and no despawn law exist; the
   CObj destructor only releases the light slot, 005c8e90:245-248).
3. **Underground darkness = a per-tile BASELIGHT byte** (`tile+0x07`), recomputed over the
   dirty-rect channel "MapValidator:BASELIGHT" (mask 0x10): under open sky 0x14=20, underground
   ~0..8 (a plain tunnel is about 4/20 = 20% brightness). Point lights are added ON TOP of the
   base. The "where the sky begins" threshold is set by the Tcl `set_light_begin`
   (campaigns: 33).

## 1. Autolight — an object flag

- `set_autolight obj b` → **FUN_0068ad00** ("TCL:CTclObjMethods::SetAutoLight"):
  `obj+0x3d0` bit **0x20** (the same bit panel as viewinfog 0x80). `get_autolight` →
  FUN_0068b1a0 reads the same bit.
- Registration of the "Autolight"/0x20 property is also visible in the editor property registry
  FUN_004f09f0:71-73 (pairs "Physics"/0x10 set_physic, "Autolight"/0x20 set_autolight,
  "Boxed"/0x4000 set_boxed).
- In the data: `set_autolight this 1` appears only in zwerg.tcl:156 and baby.tcl:130 (with the
  comment "turn on light at gnome position"). Dug-out resource lumps are turned off:
  z_dignwalk.tcl:370 `set_autolight $present false`. Cutscenes toggle it on actors
  (swf_704.tcl:48/164 and others). Trolls/wuki do NOT set it.

## 2. The glow drop: how a gnome "asks for" a torch

Per-object render tick **FUN_005cbf70:81-107**: if `obj+0xe8==1` (active), bit 0x20 is set, not
-mapedit, the object's owner resolves to the LOCAL player (FUN_005b3680 vs `game+0x64`), and
`tick>10 && (tick&3)==1 && ((tick>>2)&3)==(obj+0xdc&3)` — i.e. **every object once per 16 render
ticks** — it calls **FUN_005cd520**:

- gate: `DAT_0077241c` (the light-map object) exists and `+0x29 != 0` (system enabled);
- drop position: `x' = obj.x + rand/32768 − 0.5` (jitter ±0.5 tile),
  `y' = obj.y − 0.2 − rand/32768` (0.2 to 1.2 tiles UP from the feet — onto the wall);
  constants: 0.2 = DAT_00737054, 0.5 = DAT_0073708c, 1/32768 = DAT_00736f94;
- bounds check ×8 against the light map's int rect `+0x94..0xa0`;
- **center fill byte < 0xF** (the point must lie in a NON-solid cell, FUN_004093f0);
- **z of the glow point = depth of the wall face** (byte-verified by disassembling 0x5cd520):
  `z = MAX(fill over the ±1 fine cross around the point; samples ==15 EXCLUDED; clamp ≤ 14)` —
  a cmp/cmovb chain plus `mov eax,14/cmova`. **The gnome's z (obj+0x14) is never read.**
  The asymmetry is intentional: the drop uses MAX-without-15 ±1 (the real face nearest the
  camera), the snap (§5a) uses MIN ±2 + 0.01 (the deepest one, so the torch does not bury itself);
- → **FUN_005912a0(glow list, x', y', z)**: the list lives at `light singleton+0x12000`
  ({ptr,count}, 0x10-byte entries: {x,y,z,age}); **dedup: Manhattan |dx|+|dy| < 10 tiles**
  (DAT_00737534) from an existing entry → only clamps age ≤ 10, no new entry; otherwise append
  {x,y,z,age=0}. The spawn happens exactly at the entry position (FUN_005d4250:199 passes &entry
  as pos;:250-253 finishes with FUN_005cd8e0), rotation = {0,0,0} (:190-192; the torch "looks out
  of the wall" purely through the model's own orientation — there is NO rotation law).

Light singleton `DAT_00772418` (FUN_00592d70): 0x800 × 0x24 dynamic lights
{x,y,z, r,g,b, radius, extra, flags} plus the glow list at the tail (+0x12000).

## 3. Torch spawn (FUN_005d4250:120-303 — a CObjMgr pass, every frame)

Gate for the whole block: local player (`game+0x64 == FUN_005b28d0->+0x14`), class
**"Misc_Light" exists** (FUN_006b8fb0 + FUN_00601f90).

1. **Ageing**: EVERY glow-list entry gets `age += 1` per pass (lines 139-152).
2. An entry with **age == 3** (line 165): if the light map is on, the position is inside the rect
   with a 1-tile margin, and **FUN_00590e60(x,y,z)** lets it through → **spawn a Misc_Light
   object** at the entry position (FUN_00601df0, an ordinary class spawn):
   - `obj+0x3d0 &= 0xfffff96f` — clears bits 0x10/0x80/0x200/0x400 (including **viewinfog
     0x80**: an auto torch is NOT a ghost in the grey fog);
   - random texture/animation variation from the class lists by the seed `obj+0x230`
     (lines 202-247, FUN_005cdae0);
   - `obj+0x36c = -1` (no owner → does not participate in FoW reveal);
   - the newborn's Tcl interpreter is deleted (class_disablescripting).
3. An entry with **age == 6** is removed from the list (lines 280-299). So a glow entry is a
   request deferred by 3 frames with a 6-frame lifetime; the REAL anti-duplicate is §4.

## 4. The placement law FUN_00590e60(x,y,z) — "may a torch go here"

Reject (return 0) if:
- x/y is outside the light map's float rect `+0xa4..0xb0` with a margin of **2.0**
  (DAT_00737330);
- **y < DAT_007683d8** — above the "sky begins" line (see §6): torches ONLY underground;
- the tile (x+0.5, y+0.5) has `tile+0x18 & 3 == 3` (fully dug) AND FUN_004753f0(pos) falls into an
  entry of the render list `+0x2868` (0x20 stride, positions inside the light rect) — by all
  indications a list of daylight shafts / sky openings: no torch is placed in a dug-out but
  sky-lit spot. (Port: approximated as "baselight==20 → reject");
- **SPACING**: collecting every live light (radius>0) from the spatial buckets (world+0x84a4, cell
  = 8 fine) in a 3×3 tile window: if for any of them `distSq(x,y) < radius + 4.0`
  (DAT_007374a0) → reject. Yes, squared distance compared against radius+4 — the units are
  inconsistent, but that is the literal code; for a torch with r=6 this gives dist < √10 ≈ 3.16
  tiles. That is both the torch spacing along a tunnel (≈3-4 tiles) and the duplicate guard (the
  torch itself enters the light list).

## 5a. class_snaptowall = a LIVE z snap onto the wall face

Proof chain (from the primary source):
- `class_snaptowall b` → FUN_00614740:93-95: **classdef+0x2c bit 0x8**.
- The object constructor FUN_005c7b70:74-91 maps class bits onto obj+0x3d0: bit 1
  (viewinfog) → 0x80; **bit 8 (snaptowall) → 0x20000**; bit 2 → 0x10 + world grid; the same
  place copies class_light (class+0x48 → obj+0x280..0x2a0 + slot FUN_00591480). Per-object Tcl:
  `set_snaptowall` FUN_0067d4b0:70-88 (same bit), `get_snaptowall` FUN_0067d6e0.
  The only class in the data carrying the class flag is Misc_Light (emitter.tcl:10).
- **The snap law** is FUN_005cbf70:125-176 (per-object render tick), flag 0x20000 and not
  0x4000 (boxed): every 16th tick (`(obj+0xdc ^ tick) & 0xf == 0`,:127-128), within the rect with
  a 4-fine margin (FUN_00409450(x·8, y·8, 4)):
  `z = min(fill(pos), fill(pos ± 2 fine in x), fill(pos ± 2 fine in y)) + 0.01`
  (a ±¼-tile cross; the fill sampler FUN_004093f0 = detail byte (fy&7)·8+(fx&7) of the tile
  fine>>3; 0.01 = DAT_00736fe0) → FUN_005cd7e0 (writes the position when it changes).
  **x/y are NOT touched — there is no snap to SIDE walls.** Rock occupies z ∈ [0, fill] (the front
  z=15 faces the camera), so z = fill = THE OPEN FACE: the torch hangs on the back wall of its tile
  and, because the snap is live, **moves deeper when a cave is dug above the tunnel** (fill drops,
  so the face gets deeper).
- Flag reset (:163-175): class+0x54==0 and (z < 0.02 (DAT_00736ff0) or the 4 surrounding tiles are
  solid) → 0x20000 is cleared (tracking ends; z=0.01 means the bottom was reached).
- The spawn z of the glow entry (entry+8 from FUN_005cd520) is visually irrelevant — the snap
  overwrites it within 16 ticks.

## 5b. Gnomes have NO personal light

For a vanilla gnome the light slot `obj+0x28c` is −1 forever. An object gets a light ONLY through:
(1) class_light on the class (constructor FUN_005c7b70:92-107; Zwerg has none), (2) static map
instances FUN_005d7780:238-247, (3) the Tcl `set_light` FUN_006884f0:88-93 (slot on/off; the
descriptor is written by `change_light` FUN_00673f40) — in the data these are used ONLY by
ovens/forges during production (schmiede.tcl:314-315 and others), the story Lorelei
(Lorelei.tcl:54/336) and the opened Schatzkiste (misc_items.tcl:92-115). There are no portable
lamps/lanterns in the data (def_class Fackel/Lampe/Laterne = 0 hits). `obj+0x3d0 & 1` in the light
update FUN_005cbf70:43-44 is the VISIBILITY bit (its only setter `| 1` is set_visibility
FUN_0067cca0:90-93). Autolight census over the data: zwerg.tcl:156, baby.tcl:130,
**wuker.tcl:42** set 1; **troll.tcl:83 sets 0** (trolls do not plant torches). Darkness around a
gnome until the first torch is AUTHENTIC; the original's "light around a gnome" is the trail of
auto torches produced within ~3 ticks.

## 5. Misc_Light and class_light

- `Misc_Light` (data/Scripts/classes/items/emitter.tcl:3-11): `class_light {0 -0.5 1.0} 6
  {0.9 0.9 0.8}` (the offset points up and into depth, towards the wall), `class_defaultanim
  fackel.standard`, `class_particle 2 … 32 1 …` (flame), `class_snaptowall 1`,
  `class_viewinfog 1`, `class_disablescripting`.
- `class_light {ox oy oz} R {r g b} [flare]` → FUN_00614990: class+0x30..38 = offset,
  +0x4c = R, +0x3c/40/44 = colour ×31 (DAT_007375a8, 5-bit), +0x50 = the 4th argument
  (default **356** = DAT_0073766c; every static LightSrc_* passes **−1**), +0x48 = 1.
  The semantics of +0x50 are not pinned down (it looks like a flicker period/amplitude: default on
  the torch, −1 = none on the static markers); the port does not implement flicker (see the
  deviations in §8).
- Every object with a light holds a slot `obj+0x28c` in the singleton; motion rewrites the entry
  and dirties rect mask **0x10** (FUN_00591a20:67-74/115-125) → BASELIGHT recomputation of the
  region. The destructor releases the slot (005c8e90:245-248, FUN_00591860).

## 6. Darkness: BASELIGHT (FUN_005906a0, channel "MapValidator:BASELIGHT")

MapValidator (FUN_005a46d0) drives dirty rects per channel; mask 0x10 → **FUN_005906a0** writes
the per-tile byte **`tile+0x07`**:

- **Sky**: if fineY < `DAT_007683d8`×8 (above the sky line) and a vertical scan upwards
  (24 steps of 2 fine, ≈6 tiles) finds an open cell (fill < 0xD) → **0x14 (20)**.
- **Otherwise** (underground, or near-surface but roofed): a convolution of fill deltas between
  neighbouring fine cells along the diagonal (4 iterations, clamps [−8,0] per term, starting slope
  ×2 in [−4,4]), result `/2 + 4`, clamped from below at 0 → **a plain tunnel gives ≈4, corners and
  niches trend to 0**. So underground without lights = 4/20 = **20% brightness**, locally down to
  zero.
- `set_light_begin num` → FUN_0064b620 ("SetLightBegin") → FUN_00590620: writes
  `DAT_007683d8` and dirties the sky rect. Default −10000 (FUN_00594160:258 — "there is no sky",
  the whole world counts as underground). Data: campaigns/Oberwelt use `set_light_begin 33`
  ("Der Himmel fängt in dieser Höhe an", single_Oberwelt.tcl:6), Campaign6000 → 6033, lava
  triggers → 2000.
- Point lights are summed ON TOP of the base while building vertex colours (the same per-vertex
  pipeline as fog: the "blackness" gate is bias>0 — see FOG_OF_WAR_RE §4; lights fail to punch
  through the grey veil only because f=1 lerps to flat grey).

## 7. Pseudo-lightmap Tcl readers

`get_lmap_red/green/blue` (FUN_0062fe40/00630190/006304e0 — "GetLMapRed/Green/Blue") do NOT read
the map: they return the **constant 8** if (x,y) is inside the light map rect, otherwise 0.
`get_lmap_light` is NOT registered in the exe (the string is absent). No uses in the data
(only a log line in utility.tcl:27 for autolight). Port: same constant semantics.

## 8. Port contract (Unity) + deviations

Port files: `Runtime/World/TunnelTorchSystem.cs` (the POCO law of §2-4 plus integration),
`Runtime/World/BaseLightMap.cs` (POCO §6 plus the texture presenter), hooks in
DigglesWorldRuntime (creation), DigglesGameSim (tick), DigglesTclEngine (registration of
set_light_begin / get_lmap_* / the spawn helper), and a term in the DigglesGround/DigglesModel
shaders.

1. **Glow/spawn**: the law of §2-4 bit for bit on the constants (jitter ±0.5, lift 0.2..1.2,
   Manhattan dedup 10, age 3/6, spacing distSq < r+4, margin 2, only below the sky line).
   Cadence: the original drops once per 16 RENDER frames and ages once per frame; the port lives on
   a 10 Hz sim tick: a drop every 5 ticks (0.5 s ≈ 16 frames @30fps), ageing once per tick
   (spawn 0.3 s after the drop instead of 0.1 s — a documented deviation).
2. **Autolight**: the sim reads the prop var `_autolight` (already written by the existing
   RegisterEntityProp from zwerg.tcl/baby.tcl; native default = false). Drop gates: owner == local
   player, not inside a container, alive.
3. **Torch spawn** goes through the ordinary `new` pipeline (model + class_light + particle ride
   the existing systems), owner=−1, selectable/hoverable off, viewinfog OFF (the native code clears
   bit 0x80).
4. **Extinguish/ignite** — nothing new: the render gate ≥0x80, the flame ≥0x81, the grey lerp
   erasing the light are already in the port (FOG_OF_WAR_RE §4-5).
5. **BASELIGHT**: a per-tile R8 texture (0..1 = byte/20), the law of §6 evaluated ON TILE NIBBLES
   (the original computes on fine cells — a documented approximation: the sky scan runs on tile
   fill, underground = 4/20 with an AO correction from solid neighbours). Recomputation: full on
   load plus on TilesChanged (digging). Shaders: (ambient+mainLight) × base, point _DigLights ON
   TOP without a multiplier. Gate `_BaseLightParams.z` (menu/Oberwelt scenes without terrain turn
   it off).
6. **Deviations**: flicker (+0x50=356) is not implemented; the FUN_004753f0 shafts are approximated
   as "baselight==20 → reject"; torch texture variation goes through the existing
   set_texturevariation path if the class declares it (Misc_Light has no variations in the data);
   the cadence of §8.1; models are darkened by the base factor — RE confirms this is FAITHFUL: in
   the original, models are lit by THE SAME per-vertex law as the terrain (BASELIGHT base +
   class_light sum + saturation 255 / spill 64; FUN_004856e0 from "CModelRenderer
   Vertexprocessing" — the full law is in RENDERING_LIGHTING_RESEARCH.md §2a).
7. **Wall snap**: the port implements the law of §5a literally — `class_snaptowall` is parsed into
   DigglesClass.SnapToWall; the torch spawn immediately sets z = min-fill cross + 0.01 (the native
   code could rely on the 16-tick snap; the port avoids the visible jump); TunnelTorchTick runs the
   live snap every 16th tick (staggered by handle^tick), so torches track a deepening wall (a cave
   dug above the tunnel). The cross runs on TILE NIBBLES (the same fine-vs-tile deviation as
   baselight). An early port hack that pushed x against a side wall was REMOVED (there are no
   native x/y snaps). The height comes from the drop law of §2 (0.2..1.2 up); the snap does not
   change it.
8. **Fog gating of lights**: a port bug had the torch model hidden (fow < 0x80) while its
   class_light kept shining through the veil (the contribution came from a bare Light GameObject
   with no renderer). Fix: UploadClassLights skips lights whose owner is FowHidden (the same 0x80
   threshold that hid the model); viewinfog statics (LightSrc markers) keep shining in the grey,
   like their ghost model. The refill runs every frame, so no separate invalidation is needed.
   **The gate is SMOOTH**: instead of the binary FowHidden, the refill samples the presenter's
   SMOOTHED display texel at the owner's tile (FogOfWarPresenter.DisplayAt — the same field the
   shaders use to draw the veil): torch factor = (g/255)·(1−r/255), so it dies in step with the
   creeping grey and lights up over the ~0.7 s reveal dissolve; viewinfog statics use g/255 (lit in
   grey, fading in smoothly out of black). The pure function is RuntimeWorldSpawner.FogLightFactor
   (test FogVeilGlideTests.FogLightFactor_Law); binary FowHidden remains the headless/menu
   fallback. One mechanism for both the veil and the light.
8a. **Smooth GREY veil** (the fog used to appear in jerky steps): the cause was that the display
   chase ran at a CONSTANT full-swing speed (255/SmoothSeconds ≈ 364 units/s): a fade target step
   of −4 bytes (≈10 decode units per 0.4 s fog tick) completed in ~30 ms and then sat idle until
   the next tick — the veil stuttered at 2.5 Hz (the black dissolve was smooth because its swing is
   full, 0.7 s). Fix: a per-channel speed captured when the target changes —
   `speed = min(255/SmoothSeconds, gap/GlideSeconds)` (GlideSeconds = 0.45, float accumulation of
   the position): large swings keep the OLD pace bit for bit (reveal timings and existing tests
   untouched), while small veil steps are stretched over ~0.45 s — slightly longer than a fog tick,
   so the steps merge into continuous creep. Sim bytes are untouched (the smoothing is
   display-side); the settle is byte-exact (the chase only exits on the target). Test:
   FogVeilGlideTests (glide across a tick plus invariance of the full swing).
9. **Darkness tuning**: BaseLightPresenter.MinFactor — default 0.15 (a sample floor in the shader
   that lifts the native black corners; calibrated to 0.6), a live tweak in the inspector on the
   world root; per-install it is the key `baseLightMin` (0..1) in diggles_config.json
   (DigglesConfig.ResolveBaseLightMin; −1 or absent = the inspector value).
10. **Personal gnome light** is NOT added (authentically absent, §5b). If a non-faithful lantern is
   ever wanted: the engine already supports `change_light this {0 -0.5 0} 2 {0.6 0.6 0.5};
   set_light this 1` (native handlers) — a deliberate deviation, to be decided separately.
11. **Light accumulation ceiling** ("3-4 torches blind you"): the native code sums the
   contributions of all lights per vertex WITHOUT a per-light cap, but the resulting intensity
   SATURATES at 255/channel (diffuse) plus a spill of ≤64 into the DX7 specular
   (FUN_004cc040:137-168; models FUN_004856e0:128-160). A single torch already saturates its own
   pool (≈390 out of 255), so overlaps are INVISIBLE in the original by construction. Port: the
   _DigLights sum is clamped per channel at 1.0 (= the native 255 diffuse = full texture
   brightness) in both shaders; a single torch (peak 0.9) stays below the ceiling, so the look is
   bit for bit; the spill is deliberately not ported (see RENDERING_LIGHTING_RESEARCH.md §2a
   "Port transplant").

## 9. Tests

`Assets/Diggles/Tests/Editor/TunnelTorchTests.cs`: Manhattan dedup; the age law (spawn exactly at
3, death at 6, one spawn per entry); spacing distSq < r+4; rejection above the sky line; rejection
in solid rock (fill 0xF); jitter range. `BaseLightMapTests`: sky 20, tunnel 4, solid underground
dark, a roof ≤6 tiles from the sky = 20, set_light_begin moving the boundary.
