# Facial Animation — Eyes & Mouth (visemes, blink, expression, gaze)

Reverse-engineered (Tcl scripts incl. cutscenes + decompile + model/texture
decode of `data/3db/mann.3db` and `data/Texture/`). How the original game animates a
dwarf's face — and the plan to port it.

> Verified against the code:
> `set_textureanimation`/`def_texture` (`DigglesTclEngine.cs`), `DigglesTextureAnimator`,
> `DigglesModelLibrary.RegisterTextureVariants`/`GetVariantTexture` and `scan`
> (`TclBuiltins.cs:46`) all present. The §7 "not yet wired" items still hold: the
> `set_class_anim` face/eye payload is still discarded (`CmdSetClassAnim`, `:5306`).

> **Status: ✅ IMPLEMENTED & VERIFIED IN PLAY.** Dwarves blink + change
> expression by mood, driven entirely by the original Tcl. The deciding LOGIC is 100% Tcl;
> we implemented the EXECUTION (`set_textureanimation` + `def_texture` + a per-submesh
> texture-flipbook component) plus the sim fixes that let the Tcl driver actually run
> (see §7). Companion to [`HAT_LINK_SYSTEM.md`](HAT_LINK_SYSTEM.md).

## TL;DR

Faces are **texture-swap flipbooks on two fixed submeshes** of the dwarf body mesh:
**submesh 3 = face/mouth**, **submesh 4 = eyes**. There is exactly ONE face material
(`ZMann_Gesicht_normal`) and ONE eye material (`ZMann_Augen_normal`) in the `.3db`; every
expression/viseme/eye-state is a **separate TGA** bound at runtime by index. No skeleton,
no morph targets, no per-expression meshes, no UV/atlas scrolling — just "show texture N on
submesh M, advance a frame list at ~10 FPS."

- **Policy (which face, when to blink, viseme lists, gaze)** = Tcl. We don't write it.
- **Mechanism (swap the texture, run the flipbook, render)** = native engine command
  `set_textureanimation` + the `def_texture` index→TGA registry. We must implement these.

## 1. Submesh layout & the two channels

The standard dwarf mesh has 5 submeshes (confirmed across all 11,533 dwarf meshes; see
`HAT_LINK_SYSTEM.md` §5 for the same submesh analysis). Enumerated clearest in
`Scripts/classes/zwerg/z_procs.tcl:1804-1833`:

| submesh | material | role |
|--------|----------|------|
| 0 | `ZMann_Koerper_unten` | clothing lower (player-color tint) |
| 1 | `ZMann_Koerper_oben` | clothing upper / arms (tint) |
| 2 | `ZMann_Haare` | hair color |
| **3** | `ZMann_Gesicht_normal` | **FACE / mouth / expression / visemes** |
| **4** | `ZMann_Augen_normal` | **EYES** |

Reset to neutral: `set_textureanimation this 3 0 0 0` (face) / `set_textureanimation this 4 0 0 0` (eyes).
The SAME command tints clothing/hair (submeshes 0/1/2) — e.g. hat color in `create_muetze`
(`z_work_common.tcl:777`). So the face system is the hat-tint mechanism applied to two more
submeshes.

**Clothing randomization (✅ verified in).** Each dwarf's `init`
(`z_methods.tcl:380-423`) picks a 2-hex-digit clothing code per clan/gender from `txtvarlist`
(de-duplicated against clanmates' `get_clothing`), then applies it with
`set_textureanimation this 0 [scan [string index $clothing 0] %x]` (upper) and `… 1 … 1 …`
(lower). Hair worked all along because it passes the variant index directly (`$haircolor`),
but clothing routes through `scan …%x` — which silently read **0** until the Tcl `scan`
command was implemented (the unknown-command handler returns "0"), so every dwarf wore the
default outfit. With `scan` real, clothing now both changes and randomizes per dwarf.

## 2. The index → texture registry (`def_texture`, `init/txtinit.tcl`)

The `.3db` only stores `_normal`. The variant TGAs are bound to a base material as an ordered
list via `def_texture <base_material> <var0> <var1> …`; the integer passed to
`set_textureanimation` indexes that list. Male dwarf (`txtinit.tcl:15-16`):

**Face (submesh 3, `ZMann_Gesicht_normal`):**
`0 normal · 1 angewiedert · 2 Angst · 3 boese · 4 eingeschnappt · 5 fies · 6 fies2 · 7 Weinen ·
8 Zunge · 9 A_I · 10 C_D · 11 E · 12 F_V · 13 M_B · 14 O_U · 15 W_Q · 16 CW_counter1_face …`
— indices **9–15 are VISEMES** (phoneme mouth shapes for lip-sync).

**Eyes (submesh 4, `ZMann_Augen_normal`):**
`0 normal · 1 Angestrengt · 2 Angst · 3 erstaunt · 4 fies · 5 gluecklich · 6 leer · 7 muede ·
8 schlaefrig · 9 schlafen(closed) · 10 skeptisch · 11 Sonnenbrille · 12 wach · 13 zugekniffen ·
14-17 wach_{links/rechts/oben/unten} · 18-21 normal_{l/r/o/u} · 22-25 schlaefrig_{…} · 26-29 muede_{…}`
— the directional `_links/_rechts/_oben/_unten` variants implement **eye focus/gaze**.

These indices match the numbering comment in `z_faceanim.tcl:43-65` exactly.

**Textures on disk** (per gender `ZMann_*` / `ZFrau_*`, plus elf/troll/etc.): 17 face TGAs in
`Texture/m128/ZMann_Gesicht_*.tga` (128×128, 32-bit BGRA, uncompressed TGA type 2) and 30 eye
TGAs in `Texture/m064/ZMann_Augen_*.tga` (64×64). LOD copies at `m256/m128/m064/m032/Gray`.
UVs are static across all frames — only the bound texture changes.

## 3. The engine command (native)

`set_textureanimation <objref> <submesh> <framelist> [<startframe=0>] [<animtype=2>]`
(`Docs/ENGINE_TCL_API.txt:1390`).

- `framelist` = a Tcl list of texture-variant indices (a flipbook). A single value = a static
  expression; a list = an animated loop (blink, viseme sequence).
- Handler = `CTclObjMethods::SetTextureAnimation` = `FUN_00690d40` → `FUN_004d9ca0`
  (lazily allocates a per-object texture-anim state at obj `+0x244`) → `FUN_004d9be0` (stores,
  **per submesh**, 16-byte entries: the copied frame-index array + count, the start frame, and
  the animtype). The engine then advances the list and binds the indexed texture each tick (~10 FPS).

So the flipbook timing + texture binding are ENGINE work — Tcl issues ONE call with a list and
the engine loops it.

## 4. The Tcl control layer (policy — runs as-is)

All in `Scripts/classes/zwerg/z_faceanim.tcl`:
- `set_fanim {face eyes}` (`:128`) — static face+eyes (`set_textureanimation this 3 $f`, `… 4 $e`).
- `start_fanim_sequence` (`:158`) — builds face/eye framelists → `set_textureanimation this 3 {…}` (`:231`) / `… 4 {…}` (`:236`).
- `set_fanim_feeling` (`:261`), `enable/disable_auto_fanim` (`:239/:251`), `set_eye_focus`/`adjust_eye_focus` (`:275/:279`).
- Expression/mood tables (`:68-105`): e.g. `fanim_bad_normal {3 0}` (boese face + normal eyes),
  `fanim_good_awake {6 12}` (fies2 + wach), `fanim_face_pfeifen {14 15 15 14}` (whistle).
- **Blink** = a flipbook through the eye list, e.g. `fanim_normal_blinzeln {0 0 0 9 0 0 0 … 9}`
  (index 9 = closed). Auto-fired ~9×/100s via `fanim_filler_sequences` (`:12`).

**Idle/state behaviour:** `evt_zwerg_attribupdate_proc` (`z_events.tcl:1781-1803`) computes
`current_common_mood` (`good/normal/bad` × `awake/dizzy/tired/sleep`) from attributes, then (when
auto-fanim is on) `set_fanim_feeling $mood` + `random_fanim_sequence` → the timed random blink.

## 5. Talking / lip-sync

Two layers, both present in cutscenes:
- **Gross jaw motion** = the normal **body** vertex anim — `mann.dialog_*` / `koenig_reden_*`
  clips (`z_anims.tcl:862-934`) physically move the jaw. `seq_talk` (`sequencer.tcl:75-177`)
  loops a generic talk-gesture clip (`talkac*`, `talkre*`) for the speech-bubble duration
  (`talktime_list`, set at `set_speechout` `:1501/:1643`). Normal-gameplay talking is NOT strict
  phoneme lip-sync — just the talk gesture.
- **Visemes** (face indices 9–15) = texture sequences over submesh 3, used for precise mouth
  shapes in scripted dialogue.

**Cutscene control** (`Scripts/classes/story/sequencer.tcl:1023-1040`):
- `sq_actor express <actor> <feeling>` → `set_special_feeling_fanim` (e.g. `good_awake`).
- `sq_actor mouth <actor> <framelist>` → `start_fanim` on the face submesh.
- `sq_actor eyes <actor> <framelist>` → `start_fanim` on the eye submesh.
- `sq_actor focus <actor> <target>` → `set_eyefocus` (gaze tracking).

Examples: `Scripts/sequences/Urwald/Urw_045.tcl:30,55-67` (`sq_actor eyes 0 {c c 9 c …}` blink loop,
then `{l l r r o o u u}` darting); `clip_010.tcl:211-212`; `Scripts/sequences/Menue/st006_end_2.tcl:2,11`
(`sq_actor express 1 good_awake`).

## 6. Port plan

Difficulty: **low–moderate**, smaller than the hat work — the mechanism is trivial and the Tcl
logic is already written. Build order (keystone first):

1. **`set_textureanimation <obj> <submesh> <framelist> [frame] [animtype]`** (currently a stub):
   per (obj, submesh) store the frame-index list + start + animtype; advance at ~10 FPS; each tick
   bind the variant texture for the current index onto that submesh's material. A single index =
   static. Reuse for hat/clothing tint too (submeshes 0/1/2).
2. **`def_texture <base> <var0> …`** (likely stubbed): build, per base material, the ordered
   variant-TGA list from `txtinit.tcl`; load TGAs via the existing `TgaLoader`. Map submesh →
   base material → variant list (so index N → the right TGA, per gender/clan).
3. **Per-submesh runtime texture binding** in Unity — set the `_MainTex` of the specific submesh's
   material instance (or `Renderer.SetPropertyBlock(mpb, submeshIndex)`). ⚠️ **The one technical
   risk:** verify the `Diggles/Model` shader (with the VAT morph) honours a per-submesh texture
   override. Check this first.
4. **Small `DigglesFaceAnimator`** (or a branch in the controller) ticking the flipbooks in sync
   with the body morph.
5. **Wire the face/eye payload in `set_class_anim`** — currently parsed-but-DISCARDED
   (`DigglesTclEngine.cs:827-829` comment "trailing face/eye payload we don't use yet"); feed it
   into the face system so per-clip visemes/expressions play.

Then `z_faceanim.tcl` (moods, blink, gaze) and the cutscene `sq_actor` calls work as-is.

**Reuse from existing work:** model/clip/submesh/material loading (`DigglesModelLibrary`), the
`TgaLoader`, the 10-FPS `DigglesAnimator` timing + per-frame hook, submesh handling learned on
hats (face=3, eyes=4 fixed), and the Tcl-bridge pattern.

**Phasing:**
- **MVP (quick win):** steps 1–3 → **blink + mood expressions** light up immediately (driven by
  `z_events`/`z_faceanim`, no other dependencies). Eye **gaze** follows for free (same primitive,
  directional eye variants).
- **Full lip-sync in cutscenes** depends on the **cinematic sequencer (Layer 6)**, which is not yet
  implemented — talking heads arrive with cutscenes. Basic facial mood/blink does NOT depend on it.

## 7. Port status (implemented)

What was built to make a standing dwarf blink + mood-express:
- **`set_textureanimation`** — real (`CmdSetTextureAnimation`, `DigglesTclEngine.cs`): resolves
  obj → submesh base material → `def_texture` variant textures → drives `DigglesTextureAnimator`.
- **`def_texture`** — real: registers variant lists in `DigglesModelLibrary` (`RegisterTextureVariants`
  / `GetVariantTexture`). `DigglesWorldRuntime.LoadRealClasses` now runs `init/txtinit.tcl` (it's
  normally pulled in by `systeminit`, which we skip), so the registry is populated.
- **`DigglesTextureAnimator`** (`Runtime/Engine/`) — per-submesh texture flipbook at 10 FPS via a
  per-instance material clone, so the VAT morph still composes. `DigglesAnimator` exposes
  `GetSubmeshBaseMaterialName` + a `MaterialVersion` counter.
- **Sim fixes that the Tcl driver needs (these were the actual blockers):**
  - `TimerScheduler` now honours `-repeat -1` as INFINITE (was coerced to one fire) → the dwarf's
    1-second `evt_zwerg_attribupdate` heartbeat repeats. General fix for all infinite timers.
  - `fincr` + `hmin/hmax/hfloor/hceil/hf2i/hi2f` implemented (were `"0"` stubs). `fincr` is the
    blink probability accumulator in `random_fanim_sequence` (`fincr sum $freq`) — stubbed, `sum`
    stayed 0, so `rnd < sum` was never true and the blink never rolled. Also fixes mood/attrib decay.
  - `gettime` returns the real clock (`ctx.Sim.GameTime`, was a `"0"` stub) → correct `-attime`
    re-enable timing for `disable_auto_fanim`/`evt_fanim_enable`.

Not yet wired (later increments):
- `set_class_anim`'s trailing `{face …}`/`{eyes …}` payload is still discarded — per-clip visemes
  during specific body anims. Mood/blink don't need it.
- Full cutscene lip-sync depends on the cinematic sequencer (Layer 6).
- `set_texturevariation`, `set_classaniminfo`/`get_classaniminfo` remain stubbed.

## Sources

- `original/data/Scripts/classes/zwerg/z_faceanim.tcl` (face logic + index table), `z_events.tcl`
  (mood/blink), `z_procs.tcl:1804-1833` (submesh map), `actors.tcl` (spawn-time face).
- `original/data/Scripts/init/txtinit.tcl:15-16` (index→TGA registry).
- `original/data/Scripts/classes/story/sequencer.tcl:1023-1040,75-177` (cutscene `sq_actor`, `seq_talk`).
- `original/data/Scripts/sequences/**` (cutscene examples — richest source for facial control).
- `re/decompiled/Diggles/_global/00690d40_FUN_00690d40.c` (`SetTextureAnimation`), `004d9ca0`, `004d9be0`.
- `Docs/ENGINE_TCL_API.txt:1390` (command signature).
- `original/data/Texture/m128/ZMann_Gesicht_*.tga` (17), `…/m064/ZMann_Augen_*.tga` (30).
