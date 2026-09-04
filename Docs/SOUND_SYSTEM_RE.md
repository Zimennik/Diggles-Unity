# Sound system RE — positional falloff + voice/visibility limits

Reverse-engineered from the shipped `Diggles.exe` (Ghidra tree the `Diggles.exe` decompile).
This is the modernized Steam build: `FUN_004cfcb0:38` calls `SoloudAudioPlayer::Init`, so the
constants below are the ones the game the player plays actually uses. Every float constant is
byte-verified from `.rdata` (offsets in parentheses; script `scratchpad/readfloats.py`).

Answers manual test- items **20** (sounds feel global, not positional) and **21** (more
simultaneous sounds than the original). TL;DR:

* The port's **positional falloff is already faithful** (`WorldSoundService`, constants confirmed).
* The port was **missing two native LIMIT laws** — a hard **voice concurrency cap (~6)** and a
  **fog-of-war visibility cull** — so it stacked ~16 unculled voices into a "wash" that reads as
  global. Both are added; see `WorldSoundLaw`.

---

## 1. The play chain (Tcl → native → 3D position)

Tcl `sound` verbs are dispatched by `CTclGlobalMethods::Sound` **`FUN_00643a60`**:

| verb | native | notes |
|------|--------|-------|
| `bind <name> <file>` | `FUN_00643a60:231-267` | `fopen`-checks the WAV, errors `can't find sample file.` |
| `anim`/`animnoseq <db.anim> <frame> <snd> [mat] [room] [vol] [freq] [volvar] [freqvar]` | `FUN_004cf770` | per-anim-FRAME table; mat/room validated then **discarded** (dead args) |
| `play <name> <vol> [{x y z}]` | `FUN_00643a60:164-214` → **`FUN_004d19a0`** | positional one-shot |
| `playglobal <name> <vol>` | → **`FUN_004d1bb0`** | non-positional, no falloff/pan |
| `volume` / `animsound` | `FUN_00643a60:507-517` | master vol / global anim-SFX toggle (`global+0x95d8`) |

Two ways a world position enters the falloff:

1. **Per-anim-frame SFX** (the bulk of gameplay ambience — footsteps `mann.gehen_loop`, work loops,
   fire crackle). The per-object render tick **`FUN_005cb9a0`** detects a frame change, picks ONE
   random entry for that frame (`:153-166`), and calls `FUN_005d0820` → `FUN_004d19a0` with **the
   object's own world position**.
2. **`sound play`**. With an explicit `{x y z}` (5 args) the list is parsed to the sound position
   (`FUN_00643a60:181-188`). **Without** a position (4 args) it defaults to the **camera focus**
   `*(cam+0x154)/(+0x158)` (`FUN_00643a60:189-193`), i.e. dx=dy=0 → centered, effectively
   unattenuated (still fog-gated, see §3). `sound play` in the original scripts is mostly scripted
   event SFX (151 sites; `playglobal` only 3).

The **listener** is the camera state triple `(at.x, at.y, zoom)` = the `set_view` focus
(`FUN_00643a60:190-192` reads at.x/at.y from the same struct; the falloff reads `[2]` = zoom).

---

## 2. The POSITIONAL law — `FUN_004d19a0`

```
dx = soundPos.x - listener.x (:23)
zf = (zoom - 1) * 0.3 + 1 DAT_00737060 = 0.3 (:24)
dist = sqrt(dx² + dy² + 0.0) DAT_00736f78 = 0.0 (:25-26, FUN_0040b7e0 = sqrt)
att = max(0, dist * 0.25 * zf - 0.3) DAT_00737058 = 0.25 (:27-31)
gain = (1.0 / (att + 1.0)) / zf DAT_00737138 = 1.0 (:21,32)
volOut = round(vol100 * gain) (:33)
pan = clamp(-dx * 3.0, -100, 100) DAT_00737428 = 3.0 (:34-42)
       DirectSound SetPan(pan * -100)  →  panStereo = clamp(dx*0.03, -1, 1)  (:58)
pitch = freqFactor * 22050 Hz DAT_007376d4 = 22050 (:55)
```

**All constants byte-verified** vs `.rdata`: `0.3` (0x335e60), `0.25` (0x335e58), `1.0` (0x335f38),
`3.0` (0x336228), `0.0` (0x335d78), pan clamps `±100.0` (0x33640c / 0x3365d0), sign-flip mask
`0x80000000` (0x336770), `22050.0` (0x3364d4). z/depth is **ignored** — attenuation & pan are
ground-plane (x,y) only. The port (`WorldSoundLaw.Attenuate`) matches this exactly; nothing to fix.

**Play gate:** `10 < round(vol100*gain)` → play iff `vol*gain > 0.10` (`:43`). For anim SFX a
pre-attenuation gate `0x14 < round(vol*100)` → `vol > 0.20` (`FUN_005d0820:26`). Both already ported
(`MinPlayGain=0.10`, `MinAnimVol=0.20`). At default zoom 1.35 these already cull a 0.5-vol footstep
beyond ~14 tiles from focus — the falloff genuinely works when a listener is present.

`playglobal` (`FUN_004d1bb0`) = full volume, pan 0, **no fog gate** — global UI cues only.

---

## 3. The LIMIT laws (the fix — items 20 & 21)

### 3a. Voice concurrency cap ≈ 6 — `FUN_00454f40:19`

Every sample play resolves a voice through `CDXSound` `FUN_00454f40`. Its first statement:

```c
if (5 < *(int *)(DAT_0076a2dc + 0x18)) return 0; // refuse — no voice allocated
```

`DAT_0076a2dc` is the sound device (`FUN_00454de0` zero-inits three `{ptr,count}` buffer lists at
`+0x14/+0x18`, `+0x1c/+0x20`, `+0x24/+0x28`). `+0x18` = the active/playing list count; when it
exceeds **5** a new sample is **dropped** (`return 0`, the engine never steals). So the native never
plays more than ~**6** concurrent sample voices, and a repeat of a still-cached sample **reuses** its
buffer (name match in the free list, `:22-34,71-82`) rather than stacking a fresh voice.

The port's `WorldSoundService` pool was **16** with no cap → up to 16 simultaneous voices = "more
sounds than the original" (**item 21**), and the resulting wash of quiet distant SFX reads as
non-positional (**item 20**). **Fix:** `WorldSoundLaw.MaxVoices = 6` + drop-new-when-full (matches
`return 0`). Music/voice streams are separate paths — unaffected. `MaxVoices` is the primary **knob**.

### 3b. Fog-of-war visibility cull — `FUN_005ab220` (positional) / `FUN_005cb3a0` (anim/object)

`FUN_004d19a0:44` gates the actual play on a second condition:

```c
if ((10 < volOut) && (FUN_005ab220(*(DAT_00772400 + 0x64)), 99 < result)) { …play… }
```

`FUN_005ab220(playerLane)` rounds the **sound's** (x,y) to a tile and returns the **MAX** fog byte
over the 2×2 tile corner `(ix,iy),(ix,iy+1),(ix+1,iy),(ix+1,iy+1)` from `tile+0x34[player]`
(`FUN_005ab220:19-73`); tiles outside the loaded map window return 0 (`:22-25`). The lane index is
`*(DAT_00772400+0x64)` = the local player. Gate: **fog byte ≥ 100** (of 255).

The anim tick (`FUN_005cb9a0:132-134`) and the mesh-destructor (`FUN_004845a0:34-35`) apply the same
idea per-**object** via `FUN_005cb3a0 > 0x78 / 0x7f`, which caches the object's `GetMaxFow`
(`FUN_005cb3a0:50` → `FUN_00580ea0`, the 7-sample rect max = `FogOfWarState.SampleMaxFow`).

**Meaning:** a world sound plays only where the local player currently has fog byte ≥ ~100. Because
the fog fades −4/update once no unit reveals a tile (`FogOfWarState.FadeBlocks`), a tile stays
audible only ~15 s after the last revealer, then goes **silent**. Your own units self-reveal (byte
0xFF) so their SFX always pass; the cull removes **environment SFX in faded/unexplored areas and
other players' units** — exactly "off-screen events … not at all." The port omitted this entirely.

**Fix:** `WorldSoundLaw.FogAudible(fog, localPlayer, x, y)` mirrors `FUN_005ab220` (2×2 MAX ≥ 100),
wired into `WorldSoundService.PlayPositional`. **Fail-open** — skipped when `FogOfWar == null`
(menus/tests) or `SequenceActive` (cutscene audio is authored + camera-focused; never regress it),
so it can only *add* culling to live gameplay, never silence a scene wholesale. Threshold is a knob.

---

## 4. FUN_00453f00 (shatter-sound rider) — PREMISE CORRECTED

The rider assumed `FUN_00453f00` is "the native shatter-sound chooser invoked from `FUN_004845a0`."
It is **not**. `FUN_00453f00` is a **refcount / smart-pointer release** helper — it decrements
`obj[1]` under `LOCK`/`UNLOCK` and invokes the destructor `(**obj)(1)` at zero, over an array of
COM-like objects (`FUN_00453f00:18-58`). `FUN_004845a0` is the **MeshDestructor**: it bounds-checks
the object against the map window (`:30-33`), gates on the fog/visibility (`FUN_005cb3a0 > 0x7f`,
`:34-35`), fractures the deco (allocs shards `FUN_006ba810(0x1c3)`, sets the "shattered" flag
`+0x3b4 = 1`, `:96`), and calls `FUN_00453f00(iVar3)` **only to release** a refcounted array element
(stride `0xd8`, `:78-94`). **No sound is played in either function.**

The deco-shatter sound is therefore **data-driven** through the normal paths — either a `sound anim`
entry on the crumble animation (soundinit) or a Tcl `sound play` in the destruction handler (e.g.
`genericprod.tcl:87` `sound play [event_get this -text1] 1.0 [get_pos this]`, and the many
`sound play <sample> …` in `Lorenbahn.tcl` / boss sequences). No special native chooser exists, so
nothing needs a dedicated port seam beyond the positional path already wired here.

---

## 5. Port mapping

| law | native | port |
|-----|--------|------|
| positional falloff | `FUN_004d19a0` | `WorldSoundLaw.Attenuate` (constants verified) |
| play gate >0.10 / anim >0.20 | `FUN_004d19a0:43` / `FUN_005d0820:26` | `WorldSoundLaw.MinPlayGain` / `MinAnimVol` |
| voice cap ~6 + drop-new | `FUN_00454f40:19` | `WorldSoundLaw.MaxVoices` + `Acquire` (drop) |
| fog cull ≥100 (2×2 max) | `FUN_005ab220` | `WorldSoundLaw.FogAudible` → `FogOfWarState.FogAt` |
| listener | camera focus (x,y,zoom) | `DigglesCameraController.FocusX/FocusY/Zoom` |
| shatter sound | none (data-driven) | normal anim/`sound play` path |

Knobs (if tuning is needed in Play): `WorldSoundLaw.MaxVoices` (concurrency), `FogAudibleThreshold`
(cull tightness), `WorldSoundService.FogCullEnabled` (kill switch).
