# B4 — Fixed engine state block (section 1, 205 bytes) — full semantic map

## Status

The 205-byte fixed engine/application block after marker 1 is now fully mapped, byte by byte.
It is `FUN_006b7710` serializing two live objects: 17 fields (77 bytes) of the **CGameThread**
object (`*(g_App+0x5a4)`, `g_App` = `DAT_00772400`) and the CWigglesApp's **8 per-player
fog-of-war explored-rectangle records** (128 bytes at `g_App+0xA328`). The same function runs on
load in read mode, so every byte is restored into live memory; the loader post-fixes exactly one
byte (`+0x140` camera-moving flag → 0). Semantics: the game-sim "next tick" clock, the camera
glide target + current camera state (focus X/Y, zoom, two tilts), the `set_view_begin` fog/view
depth, two dead double slots, two uninitialized stack-garbage pointer slots, and the per-player
explored bounding boxes. A new-campaign save can synthesize every byte from port state
(`ctx.Sim.GameTime`, `DigglesCameraController`, `FogOfWarState.ExploredRect`) plus constants.
Corpus: all 103 saves parse with markers 1/2/3 validating at the computed offsets; both live
dumps (`diggles_loaded`, `diggles_meadow`) corroborate every field interpretation.

Evidence base: capstone disassembly of `FUN_006b7710`/`CWigglesApp_Run` from the shipped
`Diggles.exe` (probes in `Tools/SaveFormatResearch/probes/b4_*.py`), the per-function decompile
dir, the Tcl command registration table in `.text` (real command names, since the CallStat name
strings are duplicated/unreliable), a 103-save corpus probe, and minidump reads of both live dumps.

## Findings

### 1. Block owner identity — PROVEN

- Writer callsite `FUN_00557170` @ `005573ad-005573bc`: `MOV EAX,[0x772400]; MOV ECX,[EAX+0x5a4];
  CALL FUN_006b7710` (save_core_dump.txt:278-285). Loader callsite `FUN_00556570` @
  `0055682b-0055683a` passes the identical ECX (save_core_dump.txt:1991-1997). Same function,
  archive-mode-driven (mode 1 = read, 2 = write), so **emission order == restore order**.
- `*(g_App+0x5a4)` is stored by `FUN_006ae6b0:44` (CGameThread vtable slot 15 at `0x7363f4+0x3c`),
  called from `CWigglesApp_Run` @ `006b888b-006b889d` with ECX = the stack object at `ebp-0x1b8`
  whose vftable `0x7363f4` = `Threads::CGameThread::vftable` (disasm `006b87a7`).
- Live-dump proof of identity: in `diggles_loaded.dmp` `*(g_App+0x5a4)` = `0x12ff504` (a stack
  address) with vtable `0x009063f4`; in `diggles_meadow.dmp` = `0x16ff2d4` with vtable
  `0x00da63f4`. Both equal `0x7363f4` + the per-run relocation delta (`0x1D0000` / `0x670000`),
  i.e. **the object IS the CGameThread**, which lives in `CWigglesApp::Run`'s stack frame for the
  whole session. (g_App located in the dumps by pattern-searching the empty fog-rect records;
  static VAs are not directly mapped in these minidumps.)

### 2. Exact byte layout — PROVEN (disassembly `006b771d-006b7a32`)

`V` = CGameThread. Archive offsets are relative to the block start (file offset 21 of the
decompressed stream). Writer helpers: `FUN_00408960` 8 bytes, `FUN_00406f60` 4, `FUN_00409340` 1.

| Blk off | Size | Source | Meaning | Certainty |
|---:|---:|---|---|---|
| 0 | 8 | `V+0x80` double | sim next-tick clock (see #3) | PROVEN |
| 8 | 8 | `V+0x88` double | always 0.0; no consumer found | UNKNOWN (constant 0) |
| 16 | 8 | `V+0x90` double | always 0.0; no consumer found | UNKNOWN (constant 0) |
| 24 | 4 | `V+0x134` float | camera glide target focus X; idle = −1000.0 | PROVEN |
| 28 | 4 | `V+0x138` float | camera glide target focus Y; idle = −1000.0 | PROVEN |
| 32 | 4 | `V+0x13c` float | camera glide target zoom (0 when arg omitted) | PROVEN |
| 36 | 1 | `V+0x140` byte | camera glide in-flight bool (`get_cameramoving`) | PROVEN |
| 37 | 4 | `V+0x14c` float | glide target tilt dx (magnitude clamp 1.8) | PROVEN |
| 41 | 4 | `V+0x150` float | glide target tilt dy | PROVEN |
| 45 | 4 | `V+0x154` float | camera focus X, current (world/native tiles) | PROVEN |
| 49 | 4 | `V+0x158` float | camera focus Y, current | PROVEN |
| 53 | 4 | `V+0x15c` float | camera zoom, current | PROVEN |
| 57 | 4 | `V+0x160` float | camera tilt dx, current | PROVEN |
| 61 | 4 | `V+0x164` float | camera tilt dy, current | PROVEN |
| 65 | 4 | `V+0x168` | **uninitialized stack garbage** (see #7) | PROVEN garbage |
| 69 | 4 | `V+0x16c` | **uninitialized stack garbage** (heap ptr) | PROVEN garbage |
| 73 | 4 | `V+0x188` float | `set_view_begin` value (view/fog begin row) | PROVEN |
| 77 | 128 | `g_App+0xA328` | 8 × `{Int32 minX,minY,maxX,maxY}` per-player FoW explored rect, players 0..7 | PROVEN |

Total 24+12+1+40+128 = 205. The record loop (`006b7810-006b7a32`) transfers
`g_App+0xA328 + i*0x10 + {0,4,8,0xC}` for `i` = 0..7 — player-major, field order min-x, min-y,
max-x, max-y. `FUN_006b7710` contains **no version branch**: the block is 205 bytes in every
supported version.

### 3. `V+0x80` = simulation "next tick" clock — PROVEN

`CGameThread::SrvDoThink` (`FUN_006b4a30:132-153`): the fixed-step sim loop runs
`while (V+0x80 <= *(double*)(g_App+0xA088)) && budget(10)` and each step does
`g_App+0xA0B0 = V+0x80; V+0x80 += DAT_007371a8` where `DAT_007371a8` = **0.1** (exe data read;
the 10 Hz sim step). Fall-behind resync at `:531-536`: `V+0x80 = gametime + 0.0001`
(`_DAT_007370a0`). Corpus: `V+0x80 − gametime` ∈ **[0.0001, 0.0982]** in 103/103 saves — always
within one step ahead, exactly as the loop predicts. Live dumps: 1.700 vs 1.610 and 11.586 vs
11.554. On load it must be ≥ the restored gametime or the sim would replay catch-up ticks
(capped at 10/frame). **Restored and consumed → faithful.**

### 4. Camera fields — PROVEN

- Current state `+0x154/158/15c/160/164` = focus X, focus Y, zoom, tilt dx, tilt dy. Written by
  keyboard scroll/home-reset (`FUN_0052c510:174-194`; home key sets `0x15c=1.2f, 0x160=−0.2f,
  0x164=0`), by follow-mode (`FUN_006b5ab0:51-95`, follow object id in `g_App+0xA0D4`), read by
  the renderer (`FUN_0048ba80:139-144`, `FUN_00576c60:234-243`, `CDXExecBuf_Render:859`; dy is
  sign-flipped via XOR `0x80000000`). Defaults from `CWigglesApp_Run` disasm `006b8844-006b8887`:
  zoom 1.2, dx −0.2→ then 0 rewritten, matching the port's `DigglesCameraController`
  (FocusX/FocusY/Zoom/Dx/Dy, RotMagMax=1.8). Corpus ranges: focus X 69..422, focus Y 23..470
  (512×640 tile maps), zoom 1.0..2.5, dx −0.25..0, dy −0.033..0.5.
- Glide target `+0x134/138/13c/14c/150` + flag `+0x140` + (unserialized) duration `+0x144`:
  written by Tcl `set_view` (`FUN_006284e0:145-369`) and `set_viewpos` (`FUN_00629fe0:307-477`);
  tilt-target magnitude clamped to `DAT_00737268` = 1.8f (`006284e0:235-245`). When a glide
  starts, the "from" snapshot block `+0xF4..+0x130` is filled (`006284e0:381-393`), including
  glide start time = `(float)gametime`. `+0x140` is exported to Tcl as `get_cameramoving`
  (`FUN_00628de0:50`; real command name from the registration table at va `0061b2aa`).
- **Loader post-fix**: `FUN_00556570:138` forces `V+0x140 = 0` immediately after the block is
  restored — a glide is never resumed from a save. With the flag 0 the five target fields are
  dormant (only read while a glide is active; every new `set_view` rewrites them). Corpus:
  `+0x134/138` = −1000.0 (the constructor idle sentinel) and `+0x140` = 0 in **103/103 saves and
  both dumps**.

### 5. `V+0x188` = `set_view_begin` — PROVEN (writer/values), reader not yet located

Tcl `set_view_begin` (`FUN_0064ba50:73-82`; name from registration table va `0061be2e`) stores
the float; world reset `FUN_0055a060:249` writes the disabled default `−100.0f`. Corpus has
exactly 3 values and each traces to shipped scripts: **11** (`data/GameScripts/oberwelt.tcl:13`),
**23** (`data/Scripts/gameplay/Campaign.tcl:9`), **218.5** (`data/Scripts/classes/story/story.tcl:641`
= `py − 16` for deep worlds). Because level scripts do NOT re-run on load, the loader's restore of
this field is the only way the value survives — **restored and (by renderer) consumed → faithful**.
The exact render-side reader wasn't pinned down (it is not reachable via the `g_App+0x5a4` alias
in the decompile); see open questions.

### 6. `g_App+0xA328` = per-player FoW explored rectangles — PROVEN

- Init to empty `{0x8000, 0x8000, −1, −1}` per player: `FUN_00558770:744-748` and world reset
  `FUN_0055a060:40-55`.
- Expanded on every reveal: `CFogOfWar_RemoveFogLine` (`0057ea10:78-110`) and
  `CFogOfWar_RemoveFogBoxNoCollision` (`0057f170:93-97`) — inputs are fine cells, `>>3` converts
  to **tile units** before the min/max update (hence corpus values −10..470 on 512×640-tile maps).
- Consumed by `CFogOfWar_UpdateFoW:213`, minimap/overview draw (`FUN_0061f080`, `FUN_005f0330`,
  `FUN_0043bdd0`). **Restored and consumed → faithful.**
- Corpus: player 0 always populated (38 distinct rects, e.g. `(71,−10,436,240)`), player 1 always
  populated (story/AI player), players 2/3/6 occasionally, players 4/5/7 always empty. Live dumps
  match the corpus values for the same campaign state (loaded: p0 `(193,−10,285,36)`, p1
  `(219,49,249,79)`).
- Port state already exists: `FogOfWarState.ExploredRect` (`Assets/Diggles/Runtime/World/
  FogOfWarState.cs:60`). ⚠ The port's empty sentinel is `{0x8000,0x8000,−0x8000,−0x8000}`; the
  native empty maxima are **−1**, so the save writer must emit `−1`, not `−0x8000`.

### 7. `V+0x168` / `V+0x16c` = uninitialized stack garbage — PROVEN

Neither field is written anywhere in the CGameThread construction sequence (`CWigglesApp_Run`
disasm `006b87b6-006b8887` initializes `+0x170/174/178` and skips `+0x168/16c`) and no consumer
exists in the decompile. Corpus: `+0x168` takes 8 distinct values across 103 saves, **all** equal
to static VA `0x6E5998` plus a per-run 64KB-multiple ASLR delta; `0x6E5998` is a return address
inside CRT `free` (`006e597c FID_conflict__free`). Both live dumps de-relocate to the same
`0x6E5998` and dereference to identical CRT code bytes. `+0x16c` is a leftover 64KB-aligned heap
pointer (10 distinct corpus values; dumps dereference to heap-segment headers). The loader
restores these stale pointers over the live garbage and nothing ever reads them — harmless by
luck. **Write-garbage / read-inert.**

### 8. `V+0x88` / `V+0x90` — UNKNOWN semantics, constant 0

Zeroed in the constructor (`006b87f7/006b882e-006b883d`, xmm0 zero stores covering `+0x80..+0x97`
before `+0x80` is later advanced by the sim), 0.0 in 103/103 saves and both live dumps, and no
reader/writer found in any CGameThread method (`SrvDoThink`, `Cycle`, `FUN_006b1b00`,
`FUN_006aeb30`, `FUN_006b5a20`). Dead clock slots. Safe to write 0.0 always.

### 9. Header-adjacent scalars around the block — PROVEN (confirms existing doc)

- Offset 0: `Int32` version, writer always `0xA5` (`FUN_00557170:53`); loader accepts
  `0x86..0xA5` (`FUN_00556570:82`).
- Offset 4: native string, written from `PTR_0071ce50` (empty; `FUN_00557170:75-77`), i.e. bytes
  `01 00 00 00 00`; read (and discarded into a temp) when version > `0x7d`
  (`FUN_00556570:83-92`). Empty in 103/103 saves. Write-only in practice.
- Offset 9: `Double` game time; loader restores it to `g_App+0xA088` (`FUN_00556570:116`) then
  calls `FUN_00565850` = re-stamp all registered timer bases (writes `DAT_00737750` = −1.0 /
  0 into two pointer lists). `g_App+0xA088` is the master game clock read by ~100 functions
  (timers, `gettime`, glide start time). **Faithful; port source = `ctx.Sim.GameTime`.**
- Markers 1 (17), 2 (226), 3 (230) verified in 103/103; section 2 is empty (markers adjacent).

## Port mapping recommendation

For a NEW-CAMPAIGN synthesized save (offsets = block-relative):

| Field | Write from | Class |
|---|---|---|
| ver/header string/gametime (pre-block) | `0xA5`; empty string (`len=1`+NUL); `ctx.Sim.GameTime` | port state |
| 0 `V+0x80` | `ctx.Sim.GameTime` + next-tick remainder; simplest faithful choice: `gameTime + 0.0001` (native resync value, inside the observed [0.0001, 0.0982] envelope) | port state (derived) |
| 8/16 `V+0x88/90` | `0.0` | constant |
| 24/28 `V+0x134/138` | `−1000.0f` (idle sentinel; matches 103/103 corpus) | constant (dormant: loader zeroes the flag) |
| 32 `V+0x13c` | last scripted target zoom if tracked, else current zoom or `0` — dormant, any corpus-plausible value loads identically | compatibility-retain |
| 36 `V+0x140` | `0` (loader forces 0 anyway) | constant |
| 37/41 `V+0x14c/150` | current camera Dx/Dy (or the scripted default tilt) — dormant | compatibility-retain |
| 45..64 `V+0x154..0x164` | `DigglesCameraController.FocusX, FocusY, Zoom, Dx, Dy` | port state, **faithful** (camera resumes here) |
| 65/69 `V+0x168/16c` | `0` (native writes garbage; nothing reads it back) | constant |
| 73 `V+0x188` | port view-begin value (world script `set_view_begin`; port currently stubs the command — store the last value per session and persist it; default `−100.0f` if never set, `23` for campaign worlds) | port state, **faithful** |
| 77..204 | `FogOfWarState.ExploredRect[p]` for p=0..7, min-x/min-y/max-x/max-y as Int32 tiles; empty ⇒ `{0x8000, 0x8000, −1, −1}` (note: convert the port's `−0x8000` max sentinel to native `−1`) | port state, **faithful** |

Loader-side classification summary: everything is restored into live memory by the shared
serializer; *faithful* fields are `V+0x80`, gametime, `+0x154..0x164`, `+0x188`, and the A328
rects. *Dormant-after-load* are the glide targets (`+0x134/138/13c/14c/150`) because `+0x140` is
force-zeroed at `FUN_00556570:138`. *Inert* are `+0x88/90` (never read) and `+0x168/16c`
(garbage in, garbage kept).

## Open questions

1. The render-side reader of `V+0x188` (`set_view_begin`) was not located in the decompile (the
   value is certainly consumed — scripts tune it per world and reset uses −100). Likely accessed
   through a register-held CGameThread pointer that Ghidra didn't resolve; irrelevant for save
   synthesis but needed if the port implements the actual visibility cut.
2. What resets `V+0x134/138` back to −1000.0 after a glide completes was not pinned (corpus is
   uniformly −1000 even though set_view runs in every intro). Candidates: the glide interpolator
   end branch. Does not affect synthesis (constant −1000 is corpus-exact).
3. `V+0x88/+0x90` may be vestigial pause/lag accumulators from an older build; if some exotic code
   path writes them, no fixture exercises it. Constant 0 is corpus- and dump-exact.
4. The port's `FogOfWarState.ExploredRect` empty-max sentinel (−0x8000) differs from native (−1);
   confirm the port's minimap/consumers tolerate the native value when it round-trips through a
   load, and normalize on the save/load boundary.
