# B5 — Original post-load pipeline and global-state laws

## Status

This report reverse-engineers what the original executable does around and after parsing a
`.sav` archive: the pre-parse session teardown, every read-side derived rebuild, the loader
tail inside `FUN_00556570`, the caller-side post-load steps (display rebuild,
`evt_system_gamestart`), the failure model, the RNG law, the location of all Tcl state, the
identity of the version-`0x9A` block, and every file the load path touches outside the
archive. All claims are instruction- or corpus-backed; each carries PROVEN / LIKELY /
UNKNOWN. Two findings correct `Docs/ORIGINAL_SAVE_FORMAT_RE.md`: the version-`0x9A` "32
reserved archive-framing values" are in fact the `prodslot_override` table at `CGame+0xBC`
(a state pointer exists), and top-level markers are never validated by the original loader
(they are write-side breadcrumbs; the read side discards them). Bottom line for the
acceptance goal: the original rebuilds all derived/presentation state itself, runs no
archive integrity validation beyond two deliberate crash-sentinels in the action manager,
reads three kinds of external files (BlockDB by graphics quality, boot-time class scripts,
the AI script path stored in the archive), and reseeds nothing — a port save must be
byte-well-formed because any malformed section produces garbage propagation or a crash with
no dialog and no rollback.

Evidence bases: the per-function decompile of `Diggles.exe` (per-function decompile),
`Tools/SaveFormatResearch/save_core_dump.txt` (instruction dump of writer+loader),
`Tools/SaveFormatResearch/save_logic17_helpers_dump.txt`, the shipped
`Diggles.exe` image (PE string/immediate scans), the shipped `data/scripts` Tcl corpus, and
the 103-save corpus probed with `Tools/SaveFormatResearch/probes/probe_tcl_state.py`
(new, built on `parse_a5_object_bodies.py`).

## Findings

### 1. Load entry points and call topology — PROVEN

`FUN_00556570(filename)` has exactly three callers:

- `FUN_0053f0a0` — UI "start game from file". Extension-dispatched: `.tcl` →
  `FUN_005401f0` (new game from a level script), `.sav` → save load
  (`0053f0a0_FUN_0053f0a0.c:86-99`). This proves the `.sav` path never touches level
  `.tcl`/`.pmp` files.
- `FUN_00541650` — second UI path (load-game window), same pre/post steps
  (`00541650_FUN_00541650.c:93-113`).
- `FUN_006310f0` — the Tcl command `gameload` (name proven by exe immediate scan: handler
  `0x006310f0` pushed at VA `0x6188e9` inside `CTclGlobalMethods_Register` with
  inline-constructed name string `"gameload"` at VA `0x72d2d8`; `gamesave` = `0x00630eb0`
  at `0x61886e`). The Tcl path calls only `FUN_00556570` and cleans up its argument
  vectors — no display rebuild, no gamestart event (`006310f0_FUN_006310f0.c:74-85`).

Both UI paths run, before the loader: `FUN_0055b510` (full session shutdown, see #2),
set `CGame+0x8F=1`, store a double to `CGame+0xA090` (+ dirty byte `+0xA098`),
`FUN_005f8080` (CStoryMgr singleton ensure — lazy-init getter,
`005f8080_FUN_005f8080.c:14-20`), `FUN_005fa7c0`, `FUN_004d1090`/`FUN_004d1d90(0)`
(adaptive-sound ensure/reset), `FUN_0047de40(0)` (clear a 16-byte-record DSFastArray),
clear `CGame+0x8C` and `CGame+0x598` (`0053f0a0:94-108`, `00541650:93-108`).

### 2. Pre-parse teardown inside the loader — PROVEN

`FUN_00556570` (all line refs into `00556570_FUN_00556570.c`):

1. opens the gz reader (`FUN_006bcc50`, line 64); on open failure the whole load body is
   skipped silently (`if (cVar2 != 0)`, line 65);
2. sets `CGame+0x8F=1` (line 66) — a "session exists/was loaded" flag;
3. transfers the 4-byte version, stores it to `DAT_007723fc` and gates it:
   `if (0x1f < version - 0x86) goto cleanup` (lines 67-82). Rejection happens BEFORE any
   state teardown — via Tcl `gameload` a wrong-version file is a clean no-op; via the UI
   paths the session was already destroyed by the caller's `FUN_0055b510` (finding #1);
4. reads and immediately frees the header string (version > 0x7D, lines 83-92);
5. `CGame+0x89 = 1` (line 93) — exposed verbatim to Tcl as the `gameisloading` boolean
   command (`FUN_00631330` reads exactly this byte; string table adjacency
   `gameload`/`gameisloading` at VA `0x72d2d8/0x72d2e4`); cleared in the "Validate" phase
   (line 510). Not used by any shipped script (grep of `data/scripts`);
6. reset battery (lines 94-101): `FUN_0055b510` full shutdown, then redundantly
   `FUN_004244e0` (CElf singleton ensure, `DAT_0076a274`), `FUN_00427610(1)` (CElf
   reset: frees its two particle sources, look-at/-targets to -1), `FUN_00429970` (AI
   owner-manager ensure: 8×0x70 slots), `FUN_004313c0`+`FUN_00431420` (ensure+reset an
   8192-entry 12-byte per-object-slot cache at `DAT_0076a27c`), `FUN_00432390` (ensure
   `CProdPlannerMgr`: 8 per-player planners of 0x3094 bytes at `DAT_00c4ecd8`), and the
   named `CProdPlannerMgr_Reset`.

`FUN_0055b510` (session shutdown, `0055b510_FUN_0055b510.c:35-140`) zeroes game time
(`CGame+0xA088`) and `+0xA080`, resets registered time-caches (`FUN_00565850`: one pointer
list to the double at `DAT_00737750` = **-1.0** (PE read at VA `0x737750`), another list to
0), re-inits the CGame sub-state (`FUN_0055a060`: player records to `Nobody`, diplomacy
neutral, and the 32-entry `CGame+0xBC` table to 0 — `0055a060_FUN_0055a060.c:72-83`),
ensures/resets CElf, AI owners, per-object cache, planners, `CObjMgr_ShutDynObjects`
(destroys every dynamic object), resets the **support-record pool** (12288 = `0x3000` slots
of 0xd8 bytes, free-list refilled with descending ids — `0055b510:69-119`), class-light
manager, `FUN_005641c0(2,2)` (spatial-index bucket grid resize to minimal —
`ceil(w/8)*ceil(h/8)` records of 0x20 bytes, the doc's spatial formula;
`005641c0_FUN_005641c0.c:27-37`), fluid manager (`FUN_0057d980` +
`FUN_0057cb60`), adaptive sound, EnergyMgr (`FUN_005ee9d0`+`FUN_005eec60`), destroys
`DAT_0077246c`, and shuts the world (`FUN_005948b0(1)`).

Port law: everything in this list is rebuilt or overwritten on load — the save must NOT
contain planner state, dig-mark counters, or derived time-caches; and a load is always a
destructive replace (no rollback exists).

LIKELY (capacity): the support pool holds 0x3000 records; the corpus maximum is 9,143
(`save_test.sav`). A port save must stay ≤ 12,288 support records; exhaustion behavior is
untested (pool refill path `FUN_005b7250` runs under `RaiseException` guards).

### 3. Read-side derived rebuilds per section — PROVEN (identifications), with two LIKELY items

World (`FUN_005931f0`, read branch `005931f0_FUN_005931f0.c:98-301`):

- "World shut": `FUN_005948b0(0)` destroys the current world, then width/height are read;
- "World init": `FUN_00594160(width, height, 0, 0)` allocates the new world and — first
  time only (`if (world+0x24 != 0) skip`, `00594160:62`) — loads the block database:
  `data\BlockDB0.BDB` / `BlockDB1.BDB` / `BlockDB2.BDB` selected by the LOCAL graphics
  setting `DAT_0076838c` = LandScapeQuality (set in `FUN_00561f80`; falls back to
  `BlockDB2.BDB` if the selected file is missing — `fopen` probe at `00594160:141-149`,
  then the named `CBlockDB_LoadDB`). The BDB is a per-machine LOD choice, NOT world
  identity; tile `+0x00` block indices must therefore be valid in all three shipped DBs
  (the game's own invariant — the port must only emit indices that occur in the shipped
  data);
- the derived dimension fields (`world+0x94..+0xC0`, floats and ×8 products) are recomputed
  from the four dwords read after init, never trusted from memory (`005931f0:129-151`);
- tiles: 53-byte records via `FUN_00592f90` into the 0x7C-stride runtime tiles; native
  cache fields `+0x04/+0x10/+0x16` reset, `+0x3C..+0x7B` sub-grid not read (established in
  ORIGINAL_SAVE_FORMAT_RE.md, consistent with the stride/loop here);
- "World validate" (`005931f0:236-253`): sets `world+0x29..0x2A = {0,1}`, then queues a
  FULL-MAP dirty rectangle with flag mask `0x5F` into the dirty-rect manager
  (`FUN_005a3fd0`; manager ctor `FUN_005a3dd0`, two 13-slot rect vectors at
  `DAT_00772428`) and immediately processes it with mask `0x7FF` (`FUN_005a46d0`, 879
  lines — the consumer that re-derives per-tile passability/render/light data). This is
  where the deliberately-unsaved sub-grid and caches come back. LIKELY on the exact
  recompute inventory inside `FUN_005a46d0` (not needed for the writer: nothing derived is
  read from the archive);
- fluid: manager reset (`FUN_0057d980`) then transfer (`FUN_0057ce70`);
- fog: 8 fade lists re-allocated per saved count (`FUN_00581330`), pairs read raw;
- final `FUN_0059a320`:allocates a width×height int32 buffer at `DAT_00b9bf18`
  filled with `0x7FFFFFFF` (a "no value" cost/flood map). PROVEN allocation/fill; LIKELY
  the pathfinding flood buffer.

Objects (`FUN_005d6790`, read branch `005d6790_FUN_005d6790.c:52-…`): calls the named
`CObjMgr_ShutDynObjects` AGAIN, then per declaration: reads ID + class-name string,
resolves the class by binary search over the boot-time class registry `DAT_00772478`
(`FUN_006037d0` — two-bytes-at-a-time strcmp over sorted name records), unknown name → -1
handle, then creates the object shell via the registered creation callback
(`FUN_00601df0`, fn-ptr at registry+0x74). Object bodies are then read by the SAME
bidirectional `FUN_005d0ce0` used by the writer (call sites `005d6790:223` and `:339` for
the two version branches) — that is the "READ counterpart" asked for. Support records are
allocated from the 12288-slot pool and filled by `FUN_005b9f00`. Tcl variables are applied
as plain `set`s into each object's fresh interp (only name/value pairs exist in the
archive — finding #8). `obj_init` is NOT part of this path (no eval of class init here;
the only script-visible post-load hook is `evt_system_gamestart`, finding #5). UNKNOWN:
exact downstream behavior when the class handle is -1 (degenerate shell vs later crash) —
a port save must only reference boot-registered class names, which the shipped
`data/scripts` corpus defines.

Control/graphics/logic sections: `FUN_005ee9d0` re-derives EnergyMgr state on load without
consuming bytes (established); `FUN_00592d70/FUN_00591fb0` (class-light) and
`FUN_00461e20` (anim controllers) restore tables and rebuild the derived model pointer from
the model-DB slot (established); the newsticker global-object gate, icon table
(`FUN_004c4370`), cave rects, sparetime, triggers, horizon and adaptive-sound managers
restore counted tables exactly as documented in ORIGINAL_SAVE_FORMAT_RE.md — no additional
read-side fix-ups found in the loader itself for these.

### 4. Loader tail inside FUN_00556570 — PROVEN

After the last version-gated block (marker 18 read for >0xA2), the loader does exactly
three things (`00556570:481-518`, instructions `save_core_dump.txt:2976-3030`):

1. **Boxed-object icon rebuild.** Loop over all 0x2000 registry slots
   (`DAT_0077244c+0x488`): for every live object with `obj+0x3D0 & 0x4000` — bit 14 is the
   `set_boxed` state (Tcl handler `FUN_0067dc80`, name proven via exe scan: name string
   `"set_boxed"` at VA `0x725354` pushed with handler `0x0067dc80`) — it clears the
   object's icon-handle entries 8..15 (`LEA ECX,[EBX+0x2B8]; CALL FUN_005dc6a0`: frees the
   manager slots and sets handles to -1) and re-creates one type-5 icon named by the
   object's CLASS name (`FUN_005dc780(obj, *(*(obj+0xD4)+0xC)+0xC)`, icon type constant 5,
   z-offset from `DAT_0073773C`). Port consequence: saved type-5 icon-table entries for
   boxed objects are disposable — the original re-derives "what's in the box" icons from
   the class; the per-object handle array at `obj+0x2B8` (16 dwords inside the generic
   body) is released/reallocated safely either way.
2. **"Validate" phase** — despite the progress label, the only checks are: clear the
   `gameisloading` byte (`CGame+0x89=0`, line 510), and a CElf position fix-up: compute
   `|CElf.pos|²` from `CElf+0x50/54/58`; if greater than `DAT_00736f78` = **0.0** (PE read)
   set the 16-bit flag word `CElf+0x20 = 0x100` (lines 511-518). I.e. ANY non-origin
   restored elf position forces flag byte `+0x21=1`, `+0x20=0` — the serialized CElf flag
   bytes are overridden on load whenever the elf has a real position. There is NO other
   post-parse validation of anything.
3. Close the gz stream (lines 521-541). The final 4096-byte block is NEVER read — control
   flow goes straight from the >0xA2 marker read to the icon loop (no `FUN_006bce60(...,
   0x1000)` on the read side; write side emits it at `00557170:398`).

### 5. Caller-side post-load steps — PROVEN

Only the two UI paths (not Tcl `gameload`) then run:

- `FUN_005d4b40(CGame)` — display/presentation rebuild (`005d4b40_FUN_005d4b40.c:62-180`):
  iterates registry slots **2..0x1FFF** (IDs 0 and 1 are never processed), and for every
  object with display state 0/1 (`obj+0x2A8`) creates a fresh render instance
  (`FUN_005d7290`, into the two CGame display lists at `+0x468`/`+0x448`) and pushes the
  serialized flag bits into it: bit 5, bit 14 (`set_boxed`), owner `+0x36C`, bit 4
  (`FUN_005d7540/75d0/76f0/7660`). Objects tagged `'bOna'` at `+0x08` are also registered
  into a manager list at `DAT_0077247C+0x40C`. It then walks the CGame hash buckets at
  `+0x84A4..+0x84AC` for the static/map instances. Everything visual is re-derived —
  the save carries parameters, never instances.
- `FUN_005d6570` — allocates the string `"evt_system_gamestart"` and raises it through
  the event system (`FUN_005ba5b0`) (`005d6570_FUN_005d6570.c:26-45`). Shipped classes
  handle it: `Lorelei.tcl:625-627` (laser re-derive), `Trigger_Tournament.tcl:703`,
  `Trigger_Tutorial.tcl:1782`, `zwerg/z_events.tcl:75`
. HARD CONTRACT for the port writer: after loading a
  port save, these handlers run in the original — the object Tcl variables they read
  (e.g. `current_soundfile`, cloak state) must be present and coherent.
- `FUN_0054a8b0` — trims a pending DSFastArray (16-byte records) — presentation-queue
  cleanup, not a scheduler.

The Tcl `gameload` command performs none of these; in shipped content it is unused (grep
of `data/scripts` — zero hits), so the UI flow is the only exercised load path.
First-tick behavior: the game clock is the restored `CGame+0xA088` double (set at
`00556570:116` BEFORE section parsing, so restore-side managers can derive from it), the
registered time caches were reset to -1.0/0 (finding #2), and the main loop
(`CGameThread_Cycle`) simply resumes — there is no special first-tick scheduler state in
the archive.

### 6. Marker and sentinel law — PROVEN (corrects the format doc's framing)

On the READ side, every top-level marker (1,2,3,…,17,18 and 0x50) is transferred into a
scratch local (`&local_20`/`&local_1c`) and **never compared** — no branch in
`FUN_00556570` inspects a read marker value (whole-function decompile; the write branch
uses different locals holding the constants). Markers are write-side breadcrumbs; the
original loader would happily consume an archive with wrong marker values as long as the
byte layout still parses. They remain useful ONLY as the port parser's own validation aid.

The only intentional integrity checks in the entire pipeline are the two action-manager
sentinels `0x626C6133`/`0x626C6134` ("3alb"/"4alb"): on mismatch the code executes
`_DAT_00000000 = 0` — a deliberate write to address zero, i.e. an immediate access
violation crash (`00406fe0_FUN_00406fe0.c:98-134`). No dialog, no recovery.

### 7. Failure behavior — PROVEN mechanics, one LIKELY corollary

- Open failure: `FUN_006bcc50` returns 0 → the loader body is skipped; silent no-op
  (session already destroyed if entered via UI, finding #1/#2).
- Version outside `0x86..0xA5`: `goto` cleanup before teardown-inside-loader; silent.
- Mid-stream corruption: the gz layer (`FUN_006c6da0` = gzread) returns 0/partial on
  error and LATCHES the error state; `FUN_006bce60` only performs transfers while the
  archive error word is `0` or `-5` (Z_BUF_ERROR) (`006bce60:11-13`) and NEVER checks the
  amount read. After the first failure every subsequent read silently leaves the
  destination buffer with stale stack/heap garbage, which then drives counts, IDs and
  string lengths: the run ends in the ubiquitous `RaiseException(0xC000008C,
  EXCEPTION_NONCONTINUABLE, …)` bounds guards, a sentinel null-write (finding #6), or an
  allocator fault. There is no error dialog, no partial-world recovery, no transaction.
- Negative string length: `FUN_004092c0` allocates `length+2` bytes BEFORE any sign check;
  `FUN_006bce60` treats a negative size as a stream-error case ("request does not fit in
  an int", `006bce60:14-45`) and poisons the stream. Depending on the value this is
  either garbage propagation or an allocation-failure crash.
- gzip CRC: the loader never reads to end-of-stream (the 4096-byte pad is unread, finding
  #4), and zlib's gz CRC verification only runs at stream end — so the payload CRC is
  effectively NEVER verified on load. LIKELY (zlib 1.1.x gzio semantics + proven unread
  tail). Corollary: even single-bit corruption that still inflates loads "successfully".

Port law: the writer carries the full integrity burden. A structurally perfect archive is
a hard requirement — the original will not diagnose, only crash or corrupt. This also
means the port's own reader-side validation (markers, counts, sentinels) is strictly
stronger than the original's, which is the correct direction for acceptance.

### 8. Tcl state inventory — PROVEN (corpus + layout)

The archive contains exactly three kinds of Tcl-bearing state:

1. **Per-object interpreter variable dumps** in every `FUN_005d0ce0` body (presence int +
   count + name/value native-string pairs). The dump is the object's ENTIRE interp global
   table — corpus probe shows even `tcl_version`, `tcl_patchLevel`, `errorInfo`,
   `errorCode`, `tclDefaultLibrary` serialized (probe on
   `quick_Quicksave_20201216_1609.sav`: 141 bodies, 133 with interpreters).
2. **Eight AI-owner blocks** (enabled byte, owner index, script path string, interp
   presence, then the same name/value pair encoding) — established layout; read path
   proven in `00429d00_FUN_00429d00.c:268-284`.
3. **Script strings as data** (not dictionaries): trigger fire/callback/actuator strings,
   the three per-action Tcl strings, the StoryMgr callback script, newsticker click
   scripts — all established sections.

There is NO global-interpreter dump and NO separate campaign-flag section: the verified
layout is byte-complete through physical EOF across all 103 saves, leaving no room for one.

Corpus proof that campaign state lives in object variables:

- Sequential pair diff `quick_Quicksave_20201216_1609.sav` → `_1610.sav`
  (`probes/probe_tcl_state.py`): **object 3 = class `StoryMgr` (type 12)** carries the
  campaign state — `zone='Urwald'`, `generate_*` world-growth flags, `oc_list`, per-scape
  ore weights, `newsdata`, warning counters; between the two saves only `CheckCnt: 4→6`
  and the `gl` list order changed, while gnome objects (4,5,6…) changed dozens of
  behavior variables (`current_occupation`, `spt_*` desires, `current_worklist`, …).
- Tutorial progress: in `save_tutorial.sav` there is no StoryMgr; **object 52 =
  `Trigger_Tutorial`** owns 63 variables including `player_task_progress='0'`,
  `nexttask='elf_action -anim reden_b -speak 1270z'`, `current_soundfile='1270a1120g'`,
  `gnome_ref1='51'`, `tutorial='1'` — the complete lesson state.

Port law: to make campaign/tutorial progress survive, the port only has to write object
Tcl variables faithfully (including the boring `tcl_*` built-ins for byte-parity, though
the original applies them as ordinary `set`s — retaining them is the safe choice).

### 9. Version-0x9A block = `prodslot_override` table (NOT a planner, NOT CElf) — PROVEN

The task's "planner state" (format-doc row "version > 0x9A → an additional global value")
is `FUN_00557bf0` — a generic "transfer 32 dwords from EDX" helper (instruction dump
`save_logic17_helpers_dump.txt:2741-2800`; also reused by the per-object particle block
writer `FUN_005da020`). At both save-core call sites the buffer is
`MOV EDX,[0x00772400]; LEA EDX,[EDX+0xBC]` — **CGame+0xBC**, 32 signed ints (writer
`0x0055798D-96`, loader `0x00556F7D-86`). This REFUTES the format doc's "no state pointer /
archive framing" description.

`CGame+0xBC` is the **production-slot override table** already fully RE'd in
`Docs/PRODUCTION_SLOT_OVERRIDE_RE.md`: Tcl command `prodslot_override`
(handler `FUN_0061ea80`; `reset|normal|disable|hide|query`, values 0/1/2, index 0..31,
accessor `FUN_00524de0`, whole-table reset `FUN_0055a7c0`), consumed by the production
window builders `FUN_00520620`/`FUN_00521310` and the pack cell (`index 31` read by
`FUN_00521c80`). It is tutorial/tournament story-gating state (`Trigger_Tutorial.tcl`,
`Trigger_Tournament.tcl` disable slots 0..5 and 31, then progressively re-enable), which
is exactly why it is serialized — and why "23 corpus values are one" (saves taken
mid-lesson hold `disable`=1 entries).

The actual opponent build planner (`CProdPlannerMgr`, singleton `FUN_00432390`: 8
per-player planners of 0x3094 bytes; named `CProdPlannerMgr_Tick/Reset`,
`CProdPlanner_MaintenanceTick/GnomeIdle`) is **never serialized**: the loader resets it
(finding #2) and it re-plans from restored world/object state on its next tick. And the
78-byte `FUN_00429100` block between the AI owners and this table remains CElf (the
format doc's identification stands; nothing else is written between the AI owners and
marker 18 except CElf → `prodslot_override` → player sessions `FUN_00557c70`).

Port law: emit the 32 `prodslot_override` ints from the port's `ProductionSlotOverrides`
state (the port already models it); emit NOTHING for planners.

### 10. RNG law — PROVEN

- The CRT `rand` (`FUN_006d46ea`, per-thread LCG state at `ptd+0x18`, MSVC constants
  `0x343FD/0x269EC3`) is seeded exactly once per process: `srand(GetTickCount)` in the
  app-init `FUN_006b7a50` (`006b7a50:69-73`; `FUN_006d470b` = srand has no other caller).
- The particle system uses its own free-running inline LCG on the global seed
  `DAT_00767980` (e.g. `0049ca70:163-166`); nothing ever seeds or serializes it.
- The save pipeline neither writes nor reads any RNG state: the writer call inventory is
  closed (`FUN_00557170` whole-function), and the only raw block — the final 4096 bytes at
  `DAT_00ca4460` — is write-only, all-zero in 103/103 saves, and never consumed by the
  loader (finding #4).
- Tcl `expr rand` state is per-interp and not part of the variable dumps.

Port law: a port-generated save must contain NO RNG state anywhere; the original neither
expects nor restores determinism across a load (randomness diverges after every load by
design).

### 11. Files read from OUTSIDE the archive during load — PROVEN

1. **`data\BlockDB0.BDB` / `BlockDB1.BDB` / `BlockDB2.BDB`** — loaded lazily on the first
   world init of the process, selected by the local LandScapeQuality setting with a
   fallback probe to `BlockDB2.BDB` (finding #3). Precondition: the shipped block DBs;
   tile block indices in the save must be valid in them.
2. **The AI script path stored in the archive** (all 103 saves:
   `data/scripts/ai/std_ai.tcl`) — for each enabled AI owner the loader copies the saved
   path and calls `FUN_0042af20`, which `Tcl_CreateInterp`s a fresh interp, registers the
   `ai_*` command set, `Tcl_MakeSafe`s it, and evaluates the script (profiled eval helper
   `FUN_00555a00` = "TCL:CObj TclEval") BEFORE overlaying the saved variable dictionary
   (`00429d00:268-284`, `0042af20` tail). Precondition: that file must exist relative to
   the working directory; a port save must store exactly the shipped path.
3. **Boot-time class scripts** — object creation resolves class NAMES against the registry
   built at application boot from `data/scripts` (finding #3); the save carries no class
   code. Precondition: the original's own `data/` — every class name the port emits must
   be a shipped class.
4. Nothing else: no level `.tcl`, no `.pmp`, no texture/model reads occur in the load
   path itself (presentation loads happen lazily from the rebuilt display instances).
   The progress UI strings ("Loading", "World", …, "Validate") are in-exe constants.

### 12. Two corrections to ORIGINAL_SAVE_FORMAT_RE.md — PROVEN

1. "32 reserved archive-framing values … have no state pointer" → wrong; they are
   `CGame+0xBC` `prodslot_override` state (finding #9).
2. "Markers are validation sentinels" → true only for the port's parser; the original
   loader discards them unchecked (finding #6). (The doc's parsing/layout claims are
   unaffected.)

## Port mapping recommendation

| Field / subsystem | Port source when WRITING a save | Original behavior on LOAD | Reason |
|---|---|---|---|
| Top-level markers 1..18, 0x50 | emit exact constants | read & discarded | write-side breadcrumbs only; still required for port-reader validation (#6) |
| Action sentinels `3alb`/`4alb` | emit exact constants | CHECKED → null-write crash on mismatch | the only hard integrity gate (#6) |
| Header string (offset 4) | empty string (len 1 + NUL) | read & freed | ignored by loader (#2) |
| Game time `CGame+0xA088` | port session clock | restored before sections; managers derive from it | first-tick law (#5) |
| `CGame+0xA090` double / `+0xA098` | n/a (set by UI caller, not archive) | UI sets before load | not in archive (#1) |
| Terrain sub-grid `+0x3C..+0x7B`, tile caches `+0x04/+0x10/+0x16` | do not write (not in format) | rebuilt by full-map dirty pass 0x5F/0x7FF | derivable-on-load (#3) |
| Pathfind flood buffer, display instances, render lights, model pointers | never serialized | rebuilt (`FUN_0059a320`, `FUN_005d4b40`, dirty pass) | derivable-on-load (#3, #5) |
| Dig-mark per-player counters | do not write | fresh session constructs them at 0 (`FUN_005661c0:55-66`; established law) | transient (#2) |
| `CProdPlannerMgr` (opponent build planner) | do not write | `CProdPlannerMgr_Reset`, re-plans | transient (#2, #9) |
| `prodslot_override` 32×i32 at `CGame+0xBC` | port `ProductionSlotOverrides` (0/1/2 per slot) | restored verbatim; UI + story gating read it | compatibility-retain, story-critical (#9) |
| 8 AI owner blocks | enabled flag, owner index, EXACT shipped script path, var dict | interp rebuilt + script re-sourced from disk + vars overlaid | path is an environmental reference (#11) |
| Object Tcl variables (incl. `tcl_*` built-ins) | full per-entity var table; campaign flags live here (StoryMgr obj / Trigger_Tutorial obj) | applied as plain sets; `evt_system_gamestart` handlers then read them | proven progress carrier (#8, #5) |
| Object class names | only boot-registered shipped classes, ≤ 8192 objects, IDs ascending | binary-search lookup; unknown → -1 handle (degenerate) | environmental precondition (#3, #11) |
| Support records | ≤ 12,288 total | pool of 0x3000 slots | capacity law (#2) |
| Boxed objects (`+0x3D0` bit 14) + type-5 icons | write the flag; icon-table entries for boxed objects may be canonical/best-effort | loader frees handles 8..15 at `obj+0x2B8` and re-creates the class icon | derivable-on-load (#4) |
| CElf 78-byte block | per elf-re-spec | restored, then flags word forced to 0x100 for any non-origin position | loader overrides flags (#4) |
| Final 4096-byte block | 4096 zero bytes | never read | writer-only framing (#4, #10) |
| RNG (any) | none anywhere | reseeded once at app start only | no RNG in format (#10) |
| gzip container | any valid deflate; CRC still must be correct for OUR reader | CRC effectively unverified by original | don't rely on CRC to catch writer bugs (#7) |

## Open questions

1. Exact recompute inventory of the dirty-rect consumer `FUN_005a46d0` (which of
   passability/light/minimap/render-chunk it rebuilds per flag bit) — not needed for the
   writer, useful for load-parity debugging.
2. Behavior on unknown class name at object creation (degenerate -1-class shell vs delayed
   crash) — moot if the port only emits shipped class names, but worth one negative test
   in the acceptance harness (Phase E).
3. Support-pool exhaustion behavior above 12,288 records (guarded refill path
   `FUN_005b7250`) — corpus never exceeds 9,143; keep the port limit at the corpus scale.
4. Semantics of `CGame+0x598` and `+0x8C` cleared by the UI callers before load (flags
   consumed elsewhere; benign for the writer).
5. Whether any shipped-content Tcl var value depends on interp-local RNG or time in a way
   that breaks after the -1.0 time-cache reset (none observed in the tutorial/quicksave
   probes; watch during Phase E acceptance runs).
6. `FUN_00565850`'s registry membership (which globals register pointers into the -1.0/0
   reset lists) — LIKELY "last event time" caches; identify only if acceptance runs show
   time-anchored anomalies right after load.
