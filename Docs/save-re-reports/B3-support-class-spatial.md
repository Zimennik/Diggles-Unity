# B3 — Support records, class table, spatial index, object-manager tail

## Status

All four target structures are now semantically identified from read/write function pairs, runtime
consumers, and corpus probes over all 103 original saves. The "support records" are the global
timed Tcl event queue (`CEvent` objects created by `TCL:CTclObjMethods::TimerEvent`): mutable
gameplay state, not a class catalog, and not rebuildable from Tcl class definitions. The "class
table" is a 12,288-slot dormant render-proxy store owned by singleton `DAT_00772444`; the loader
fully restores it (model resolution + spatial re-bucketing), it does not ignore or merely validate
it. The spatial-index records are one-shot dormant scenery spawn seeds; their per-record layout is
position/rotation floats plus a class name, and the bucket membership law
`bucket = gridW * floor(y/8) + floor(x/8)` is corpus-proven on 121,031/121,031 records. The
object-manager tail decomposes into per-owner attribute float tables (`SetOwnerAttrib`), two
version-0x93 counters with proven rebuild laws, three always-empty LOD instance-list groups, and
32 UI/mission state words at `CGame+0xBC` (not object references). Probe:
`Tools/SaveFormatResearch/probes/b3_probe.py` (plus `b3_corpus_out.txt`, `b3_fields_out.txt`,
`b3_floor_check.txt`).

## Findings

### 1. Support records are the global timed event queue (`CEvent`) — PROVEN

1.1 **The payload object is `CEvent`.** The record constructor `FUN_005b9d30` installs
`CEvent::vftable` (line 23) and initializes the +0x20 double from the game-time double at
`CGame+0xA088` (line 47). PROVEN.

1.2 **Payload layout** (`FUN_005b9f00`, serialization order == emission order):

| Native offset | Archive form | Meaning |
|---|---|---|
| +0x08 | 32 raw bytes | event-type name; write copies the registered name from the 128-slot registry at `CGame+0x95DC` (`FUN_005b9f00:31-46`, bounds check `< 0x80`); read lowercases (`FUN_006b8fb0`) and resolves name→index (`FUN_005ba5b0`; also accepts literal `#<index>`) |
| +0x0C..+0x18 | 4 × object reference (`FUN_00555d70`) | subject objects (`-subject1..3` + self); corpus: only slot 0 ever used (237,047/237,047), slots 1..3 always −1 |
| +0x28..+0x48 | 9 × Int32 | numeric event args; corpus overwhelmingly 0 (`tail3`-adjacent fields also ~0) |
| +0x4C, +0x50 | 2 native strings | string args; empty in all 237,047 corpus records |
| +0x54..+0x5C | 3 × Int32 | more args; (0,0,0) in 236,968/237,047; rare float 1.0/2.0 in the first word |
| +0x20 | 8 bytes (double) | event creation timestamp (game seconds), written last |

Note: bytes after the name's NUL inside the 32-byte field are static-buffer residue
(`DAT_00ca6580` is reused between writes); the reader stops at NUL, so a writer may zero-pad.
PROVEN.

1.3 **Queue record prefix (28 bytes) and semantics.** The global queue is the vector at
`DAT_00772480` (`{recordsPtr, count}`), stride 0x28: `{+0x00 Int32 userid-tag, +0x08 double
fireTime, +0x10 double interval, +0x18 Int32 remainingRepeat, +0x1C object-ref owner, +0x20
CEvent*}`. Enqueue = `FUN_00605f40` (sentinel fire time → current game time, default repeat 1);
dispatch = `FUN_00606230`: fires every entry with `fireTime <= gameTime`, silently drops entries
whose owner object is dead (lines 46-71), delivers a cloned event to the owner (`FUN_005ba0d0` +
`FUN_005ba940`), then `fireTime += interval` and decrements the repeat count (−1 = infinite,
removed when < 1, lines 100-120). Cancel = `FUN_00606110`, matching on `+0x00 userid` and `+0x1C
owner`. PROVEN.

1.4 **Tcl surface.** `FUN_0069fef0` = `"TCL:CTclObjMethods::TimerEvent"` with options
`-attime`, `-interval`, `-repeat`, `-userid`, `-subject1..3` and error `"unregistered event"`
(lines 78, 181, 275-486); it constructs the `CEvent` (calls `FUN_005b9d30`) and enqueues via
`FUN_00605f40` (line 808). Event names must be pre-registered (`FUN_005ba430`, max name length
0x1F, max 128 types) — registration happens during script boot, so the name registry itself IS
deterministic from loaded Tcl; the queue contents are not. PROVEN.

1.5 **Where the count comes from.** `FUN_005d6790` (the CObjMgr section serializer for marker 6)
writes declarations, the `-1` terminator, one empty string, then calls `FUN_00606500` on
`DAT_00772480` (lines 216/308); `FUN_00606500`'s write branch emits `count = DAT_00772480[1]`
(queue length) followed by that many prefix+payload records. PROVEN.

1.6 **Mutable game state, not a catalog.** Corpus (103 saves, 237,047 records): every name is a
gameplay timer (`evt_timer0` ×220,620, `evt_zwerg_attribupdate`/`workannounce`/
`evt_talkissue_update`/`evt_sparewish_update` ×2,569 each, `evt_pilz_aging` ×1,793,
`evt_timer_destruction` ×2,234, plus `evt_fogremove`, `evt_scan`, `evt_baby_growup`,...). Fire
times are overwhelmingly future-dated relative to the header game time (e.g. `save_test.sav`:
8,657 future / 486 due of 9,143; tutorial: 24/24 future); all owners resolve to declared objects
in 103/103 saves; counts scale with world age within one campaign (quick_20201219 series grows
197 → 2,256 over a session) and differ entirely across campaigns. Therefore support records are
**live simulation state that must be captured from the runtime timer system**, never derived from
class definitions. PROVEN (corpus probe).

1.7 **Per-object support tables** (`FUN_005bab70`, body tail): two Int32 cursors (`obj[0x100]`,
`obj[0x104]`), a 64-slot table with per-slot presence bytes, then a counted vector — same
`FUN_005b9f00` payload. `FUN_005ba940` proves the 64-slot table is a **ring buffer of delivered
events**: a due event is stored at slot `obj[0x104]` and the cursor advances `(c+1) & 0x3F`
(lines 26-53); events whose creation timestamp is still in the future are appended to the dynamic
vector instead (lines 56-90; leaked source path `CodeLib\StdTools\KStdLib\Include\DSFastArray.h`).
`obj[0x104]` = write cursor PROVEN; `obj[0x100]` = read cursor LIKELY (not individually traced).

### 2. Class table (marker-A1+ family) = dormant render-proxy store — PROVEN structure, PROVEN loader behavior

2.1 **Owner and container.** Serializer `FUN_005b8d50`, invoked from the main pipeline on the
singleton returned by `FUN_005b78b0` (= `DAT_00772444`, lazily constructed by `FUN_005b79e0`).
For version ≥ 0xA1 it runs AFTER the object graph (`FUN_00556570:240-243`); < 0xA1 it ran before
marker 6 (`:225-228`). Container layout: `+0x00` Int32 (0 in 103/103 saves), `+0x04` Int32[0x3000]
free-slot stack, `+0xC004` stack top, `+0xC008` entry array (stride **0xD8**), `+0xC00C` capacity,
`+0xC010` one byte (observed 255 ×97 saves / 223 ×6 — allocator-residue-like, semantics UNKNOWN),
then two global Int32 vectors via `FUN_00442b60`. Write side emits only slots with entry
`+0x74 != -1` (`FUN_005b8d50:78`), as ordered `(index, record)` pairs terminated by −1. PROVEN.

2.2 **Entry layout** (`FUN_005b7690` transfer order): 0x40-byte block (`FUN_005b92b0`; observed
content includes 0.0/NaN-pattern words — an opaque cached transform block, keep raw), position
floats `+0x40/+0x44/+0x48`, Int32 `+0x58`, small Int32 vector (`FUN_004d9df0`; length 0 for 93%
of 112,233 corpus entries, else 1/2/3/5), `+0x64` self slot index, `+0x68` linked dynamic-object
ID, class/resource name string (`FUN_00602b40`), byte `+0x70` (0 except 23 corpus entries),
`+0x74` animation-controller ID (the live filter; cross-links into the marker-0x50 graphics
controller table — this is why "static map/render instances occupy additional controller
indices"), `+0x78/+0x7C/+0x80`, bytes `+0x84/+0x85` (0/1), 10 Int32 `+0x88..+0xAC`, instance-name
string `+0xB0` (non-empty in **all** 112,233 corpus entries; e.g. `"Charly"`, `"Fungus stem_1"`),
3 bytes, Int32 `+0xB8` (0/1/2), 6 Int32, byte `+0xD4`. PROVEN boundaries; several fields
unnamed.

2.3 **Corpus laws.** `+0x64 == slot index` in **112,233/112,233** entries (103/103 saves).
`+0x68` resolves to a declared object in 110,654/112,233 (1,579 dangling — accepted stale links,
e.g. 56 in `save_test.sav`). Resources are item/deco/actor classes (`Stein`, `Kohle`, `Bier`,
`Pilz*`, `Misc_Light`, `Dummy_*`, `Zuschauer`, `Zwerg`). Free stack contains exactly the unused
slots (0 collisions; top == 0x3000 − used − 1 in probed saves). PROVEN.

2.4 **What it is.** `FUN_005d4b40` (per-tick LOD pass over all 0x2000 object slots) converts
objects whose `obj+0x2A8` LOD state is 0/1 into lightweight render instances built from the
object's class (`obj+0xD4 → classObj+8` index), position `obj+0x0C` and transform `obj+0x40`;
`FUN_005b7e30` frees an entry (clears the back-link in the object's animation controller via
`FUN_0047dae0`/`FUN_0045f830`, pushes the slot onto the free stack, appends to a cleanup queue via
`FUN_00442910`); `FUN_005b72f0` re-buckets an entry on movement. So each entry is a cheap static
mirror of a (usually still-live) dynamic object used for distant rendering. PROVEN mechanism;
"dead-body/deco proxy" naming LIKELY.

2.5 **What the loader does: full restore, not validation.** Read side of `FUN_005b7690` sets the
entry's `+0x55` flag, resolves the model (`FUN_00453f00`) and re-inserts the slot into the spatial
bucket grid via `FUN_00564500(entry+0x64)` (lines 54-58). The container's free stack, stack top,
and the two vectors are restored raw. PROVEN. Consequently the table is **not ignorable** on the
write side of a compatibility archive; however, because the runtime LOD pass regenerates proxies
from objects continuously, an empty table + correct per-object `+0x2A8` states is LIKELY
self-healing in the original exe (unverified — Phase E test).

2.6 **The two trailing vectors.** The first (`vec1`) equals the exact set of used entry indices in
**103/103 saves** (insertion order, not sorted) — rebuild law: emit all used slots. PROVEN as a
set; ordering semantics UNKNOWN. The second (`vec2`) is unrelated to entry indices (lengths
0..2,794; mixed values, partially matching object IDs) — populated by `FUN_00442910` from both
entry frees and `CObj` destruction (`FUN_005c8e90:111-117`); a deferred cleanup queue. Semantics
UNKNOWN; must be captured or proven safely emptiable.

### 3. Spatial index = dormant scenery seeds; bucket law corpus-proven

3.1 **Runtime home.** Buckets live at `CObjMgr(DAT_0077244C)+0x84A4` (stride 0x20, capacity
`+0x84A8`, count `+0x84AC`, grid width `+0x849C`). A bucket holds three vectors: live-object
pointers (`+0x08/+0x0C`, NOT serialized; rebuilt as objects restore their positions),
dormant-proxy slot indices (`+0x10/+0x14`, NOT serialized; rebuilt during class-table load via
`FUN_00564500`), and the serialized 0x34-byte records (`+0x18/+0x1C`, transfer `FUN_005c6cf0`).
PROVEN (`FUN_005d57e0`, `FUN_005b7e30`, `FUN_005d4b40:164-334`).

3.2 **Record layout (stride 0x34 in memory).**

| Offset | Archive form | Meaning |
|---|---|---|
| +0x00..+0x08 | 3 × Int32 (float bits) | position X/Y/Z (PROVEN — bucket law + Z range −5.4..18) |
| +0x0C..+0x14 | 3 × Int32 (float bits) | rotation (values are π-family floats: 0.785, 1.571, 3.141...) — LIKELY Euler |
| +0x18, +0x1C | 2 × Int32 | small enums (variant/model selectors; 20-36 and 80-212 observed) — UNKNOWN names |
| — | class name string | class registry index `+0x28` written as name; read resolves via `FUN_006037d0` and stores `classObj+8`; cache ptr `+0x20` zeroed on load (`FUN_005c6cf0:40-67`) |
| +0x24 | Int32 | 0 or 2 — UNKNOWN |
| +0x2C | Int32 | flags word; bit 4 = "delete when rect-cleared" (`FUN_005d5570:75`); values 129/145/0x1100001/0x1100011 |
| +0x30 | byte (ver > 0x8C) | LOD tier 0/1/2 — record is instantiated when detail level covers it (`FUN_005d4b40:180`) |
| +0x31 | byte (ver > 0x8C) | player-mask-like byte (1/127/255 dominant) — LIKELY visibility mask, UNKNOWN exact |

3.3 **Membership law — PROVEN corpus-wide.** With `gridW = ceil(W/8)`:
`idx = gridW * floor(y/8) + floor(x/8)`; if `idx < 1` → bucket 0, else `idx % bucketCount`
(modulo is defensive). Recomputed from each record's own +0x00/+0x04 floats and compared with the
containing bucket: **121,031/121,031 records match in all 103 saves**
(`probes/b3_floor_check.txt`). The round-to-nearest variant used by the dormant-proxy re-bucketer
(`FUN_005b72f0`, `round(pos * 0.125)`) does NOT reproduce these buckets (148/1242 on tutorial) —
the 0x34 records use the truncating law. PROVEN.

3.4 **Not recomputable from the object table.** These records are consumed destructively: when
the detail condition is met, `FUN_005d4b40` spawns a lightweight instance from the record and
removes the record from the bucket (swap-with-last, lines 290-328). They carry classes with no
corresponding dynamic object (grass/moss/beams: `Dummy_Obw_rasen_*`, `Dummy_Moos_*`,
`Dummy_Balken_*`, `Dummy_Holzwand_*`) and their population shrinks/grows independently of the
object registry (tutorial: 1,242 records vs 484 objects; `save_SaveGame.sav`: 0 records). The
task's hypothesis "recompute the spatial section from the object table" is therefore impossible
by design; what IS deterministic is each record's bucket assignment from its own position (3.3)
and the bucket count from W/H. PROVEN.

### 4. Object-manager tail

4.1 **18 × 512 words = per-owner attribute float tables — PROVEN.** Nine blocks (owner −1..7 →
block `owner+1`, stride 0x808 at `CObjMgr+0x84E4`), two 512-float arrays serialized per block
(`FUN_005db100` ×2 per iteration in `FUN_005d6790:230-240/345-357`). Indexed by attribute registry
index (`< 0x200`), name-resolved via `FUN_005daad0`/`FUN_005da900`. Tcl surface:
`FUN_0062c510` = `"TCL:CTclGlobalMethods::SetOwnerAttrib"`, `FUN_0062d310` =
`"TCL:CTclGlobalMethods::GetOwnerAttribList"`; UI reads the current player's block
(`FUN_0050e2a0:46-74`), object code reads via `obj+0x78` attribute index (`FUN_0054b830:80-88`).
Corpus values are plain floats (defaults 1.0/2.0/0.9/0.8/0.5/5.0 with per-save mutations; blocks
for unused owners stay at the boot pattern). The split between the two arrays per owner (value vs
second table) is UNKNOWN-named; both are owner-attribute state and must be captured.

4.2 **Version-0x93 pair — PROVEN rebuild laws.** `FUN_005d6790:360-363` transfers
`CObjMgr+0x41C` then `+0x418`. `+0x41C` is the loader's declaration cursor (each declared ID is
stored there during load, line 94); saved value is **−1 in 103/103 saves** → writer emits −1.
`+0x418` is the object-slot high-water mark, but the saved value is stale garbage
(`save_test.sav`: 210 vs max ID 8189) and the loader **recomputes it from the restored slots**
at the end of `FUN_005d6790` (lines 424-444: last non-null slot + 1, masked to 0x1FFF) → writer
emits `maxUsedSlot + 1` canonically; the value cannot cause load divergence.

4.3 **Three LOD instance-list groups — PROVEN empty in corpus.** Each group = two counted
lightweight-instance lists (`FUN_005da080` → per-record `FUN_005d83c0`: class name (resolved to
index `+0x00`), 7 × Int32, 3 bytes, one Tcl string, Int32) + one Int32 vector + two bytes, at
`(+0x43C,+0x43D)`, `(+0x47C,+0x47D)`, `(+0x45C,+0x45D)`. These are the per-tick near/mid LOD
instance vectors filled by `FUN_005d4b40` (`+0x448`, `+0x468` bases). All 309 group instances in
the corpus are `(0, 0, 0, 0, 1)` — lists and vector empty, trailing bytes 0 and 1. Writer law:
emit empty lists + bytes (0,1). PROVEN empirically; the byte semantics remain UNKNOWN.

4.4 **32 top-level values (version 0x9B+) are `CGame+0xBC..+0x138`, not object references —
PROVEN buffer, LIKELY semantics.** The doc's "archive framing" description is superseded: the
instruction dump shows `LEA EDX,[EDX+0xBC]` with `EDX = [0x00772400]` at both call sites
(`save_core_dump.txt`, writer 0x00557987-96, loader 0x00556F77-86), and `FUN_00557bf0` transfers
32 × 4 bytes from/to that buffer (`EDX` advances +4 per iteration; `save_sections_dump.txt`
0x00557C23-5B). No `FUN_00555d70` call → not object references. Runtime consumers are UI:
`FUN_00520620`/`FUN_00521310` index it 0..31 per journal/list entry and skip entries whose word
== 2; `FUN_00521c80` tests slot 31 (`+0x138`) against 1 and 2. LIKELY: 32-slot mission/objective
progress states (0 = untouched / 1 / 2 = closed) surfaced in the document-window UI. Not
derivable; capture live.

4.5 **0xA0/0xA2 "additions".** Version > 0xA0 relocates the class-table+spatial block after the
object graph (`FUN_00556570:225-243`); version > 0xA2 appends the eight player-session records
(`FUN_00557c70`) and marker 18 (`:466-478`). Both already covered by ORIGINAL_SAVE_FORMAT_RE.md;
no additional hidden fields found inside `FUN_005d6790` beyond the 0x93 gate. PROVEN.

4.6 **Loader quirk worth retaining.** During declaration restore, `FUN_005d6790:126-173`
special-cases the literal class name `"Trigger_Swf_119"` (legacy-save fixup). A port writer never
needs to emit it, but a loader that re-implements declarations must not choke on it.

## Port mapping recommendation

| Structure / field | Recommendation | Reason |
|---|---|---|
| Global support/event records | **port state — capture live** from the port's Tcl timer service (`TimerEvent`); serialize `{userid, absolute fireTime (game s), interval, remainingRepeat (−1 = infinite), owner entity ID}` + payload `{name (≤31 chars, registered), subject0 = owner, subjects1..3 = −1, 9 int args, 2 strings, 3 ints, creation stamp}` | Mutable scheduled-simulation state (Finding 1.6); corpus shows the simple shape above covers 100% of records |
| Event-name registry (`CGame+0x95DC`) | **derivable-on-load** — names come from script boot; archive stores names, not indices | Read side resolves by lowercased name (1.2); index stability across sessions NOT required |
| Per-object 64-slot ring + cursors + future vector | **capture live if the port keeps delivered-event history; else emit cursors 0 / empty slots** and verify in Phase E | Ring holds already-delivered events kept for Tcl inspection; skipping them risks scripts that poll past events (open question 2) |
| Class table entries | **compatibility-retain on round-trip; for from-scratch writes prefer empty table + correct per-object `+0x2A8` LOD states**, pending Phase E validation | Loader fully restores it (2.5), but the LOD pass regenerates proxies each tick (2.4); synthesizing all 0xD8 fields (opaque 0x40 block, 10 unnamed ints) from port state is not yet possible |
| Class-table free stack / top / `vec1` | **derivable**: free stack = all unused slots (any order proven-safe only as observed LIFO content; emit descending unused), top = freeCount−1, `vec1` = used slot set | Corpus laws 2.3/2.6 |
| Class-table `+0x00` word / `+0xC010` byte | emit 0 / 255 | Constant (0) in 103/103; byte is residue-like (255 ×97, 223 ×6) |
| `vec2` cleanup queue | **capture live**; empty-emission unproven | Semantics UNKNOWN (2.6) |
| Spatial 0x34 records | **compatibility-retain**: import at load into a port-side "dormant deco seed" store keyed by position, delete on carve/ObjClear, write back verbatim; bucket assignment recomputed with the floor law (3.3) | Not derivable from objects (3.4); pure content-preserving store suffices because the port renders deco its own way |
| Spatial bucket count / assignment | **derivable**: `ceil(W/8)*ceil(H/8)`; `gridW*floor(y/8)+floor(x/8)`, `<1 → 0`, `% count` | 121,031/121,031 corpus proof |
| Owner-attribute tables (18×512 floats) | **port state — capture live** from the per-owner attribute system; index by the original attribute registry order; write both arrays per owner | PROVEN owner attribs (4.1); the port's `def_attrib` order must match the original boot order for cross-compat |
| `CObjMgr+0x41C` | constant −1 | 103/103 corpus + loader-cursor semantics (4.2) |
| `CObjMgr+0x418` | derivable: `maxUsedObjectSlot + 1` | Loader recomputes and overwrites (4.2) |
| 3 LOD groups | emit `(0,0,0)` lists + bytes `(0,1)` | 309/309 corpus instances empty (4.3) |
| 32 words at `CGame+0xBC` | **capture live** (map to the port's quest/document state when named); preserve verbatim on round-trip | UI mission-state words, not derivable (4.4) |

## Open questions

1. Does the original exe regenerate dormant proxies when loading a save with an **empty class
   table** but correct object LOD states (`+0x2A8`)? If yes, the from-scratch writer can skip the
   table entirely. Needs a Phase E acceptance run.
2. Do any shipped Tcl scripts read an object's delivered-event ring retroactively (i.e., is
   emitting empty rings + zero cursors observable)? Needs a Tcl-corpus grep for the event-query
   command bound to the ring reader (`obj[0x100]` consumer not yet traced).
3. `vec2` (second `FUN_00442b60` global queue): exact consumer and whether an empty emission is
   safe. Candidate: the deferred visual-cleanup queue fed by `FUN_00442910` from object/proxy
   deletion.
4. The two 512-float arrays per owner block: which is the attribute *value* array vs the second
   table (`FUN_005daad0` vs `FUN_005da900` name resolution) — needed only for semantic mapping,
   not for lossless capture.
5. Class-table entry unnamed fields (`0x40-byte block`, `+0x58`, `+0x78/+0x7C/+0x80`,
   `+0x88..+0xAC`, `+0xB8`, trailing 6 ints): required only if option "synthesize entries from
   objects" is pursued instead of empty-table emission.
6. Exact identity of the 32 `CGame+0xBC` slots (which quest/journal features use which index);
   slot 31 has special handling in `FUN_00521c80`.

## Empirical mutation results

Open questions 1, 3 and 4 (and B8 OQ2) were tested by mutating real fixtures and loading them in
the **original Steam exe** through the acceptance harness (`accept-harness/Run-SaveAcceptance.ps1`,
Route A boot-hook `gameload`). Method:

- **Mutation tool** `accept-harness/probes/mutate.py` re-walks the `0xA5` stream (reusing
  `parse_a5_object_bodies` + this report's `b3_probe` layout) to capture the exact decompressed
  byte offsets of the class-table section, its two `FUN_00442b60` vectors, the entry list, and the
  18×512 owner-attribute grids, then splices raw bytes and re-gzips. **Every mutant was
  re-validated through `parse_a5_object_bodies` to physical EOF before any harness run.** An
  identity re-gzip (`mutate.py identity`) loaded byte-for-identically and PASSed — the gzip
  round-trip is lossless.
- **Fixtures**: `save_tutorial.sav` (primary; 500×200, gametime 441.43), cross-checked on
  `save_tutorial4.sav` (500×200, gt 2907.57, +31 objects) and `save_SaveGame.sav` (**512×640
  campaign map**, gt 3516.86, 76 objects). The harness verdict PASS = `gameload` returned `rc=0`,
  `gameisloading=0`, and world state was consumed (post-load `gettime` = the save's own clock,
  map = the save's map); a native crash mid-load yields `CRASH_DURING_LOAD` (no verdict marker).
  The template was temporarily extended to emit `gettime` into the marker, then restored to
  pristine.
- **27 harness runs**: 22 PASS, 3 `CRASH_DURING_LOAD`, 1 REJECTED (garbage negative control),
  1 `BOOT_FAILED` (a transient boot-hook flake, content-independent — the same file PASSed on
  retry). Unmutated tutorial control PASSes on every attempt (`gettime1=441.428`, map `4000×1600`).

**Harness caveats (honest scope):** (1) The `-RoundTrip` echo is a **pre-start `gamesave`** (the
boot-hook world is *loaded but not started*, so `gamestats numgnomes=0`): the echo decompresses to
a valid `0xA5` header + terrain but **not a faithful object graph**, so `roundtrip_ok=true` proves
only that the loaded state did not wedge the engine — it is **not** proof of full re-serialization,
and the echo cannot be used to inspect the regenerated class table. All verdicts below therefore
rest on the **load** signal, not the round-trip. (2) A story-heavy campaign quicksave
(`quick_Quicksave_20201219_1255`, 486 objects + populated `CStoryMgr`) `CRASH_DURING_LOAD`s the
bare boot-hook `gameload` **even unmutated** (its control crashed identically) — this is a harness
limitation (missing campaign-script/`CStoryMgr` context at bare boot), **not** a mutation signal,
so its two mutants were discarded. A light save of the **same 512×640 map** (`save_SaveGame`) loads
fine, confirming the limit is populated-story context, not map size.

### Q-OQ1 — empty class table self-heal: **CONFIRMED** (open question 1 answered)

Mutant = the empty-form of §2.5: `pre_int=0`; **full free stack** reconstructed by freeing every
used entry (all `0x3000` slots free, stack top `0x2FFF`); container byte `255`; **vec1 empty**;
**vec2 empty**; **zero entries** (immediate `-1` terminator). The section shrinks (tutorial
133,623 → 49,171 bytes) and the downstream spatial index + all later markers stay byte-identical;
the file re-parses to EOF with `class entries=0`.

**Result: PASS on 3 fixtures** — `save_tutorial` (×3 runs), `save_tutorial4`, `save_SaveGame` —
every run `rc=0`, `loading=0`, `gettime` = the save's real clock, map = the save's map (identical
to each unmutated control), no crash. The original loader **accepts an empty class table and it is
non-fatal**: terrain/objects/clock load intact and the engine is not wedged. Combined with the
decompile-proven regeneration mechanism (§2.4/§2.5: the per-tick LOD pass `FUN_005d4b40` rebuilds
render proxies from live objects, independent of the saved table), a from-scratch writer **may emit
an empty class table** provided per-object `+0x2A8` LOD states are preserved. *Residual:* tick-by-
tick proxy regeneration under a running sim is not directly filmed (the boot-hook world is loaded-
but-not-started); what is proven empirically is accept + non-fatal + world-intact on 3 fixtures.
This upgrades the Port-mapping recommendation "empty table + correct `+0x2A8`" from *pending Phase E*
to **Phase-E validated**.

### Q-OQ3 — vec2 cleanup-queue empty emission: **CONFIRMED** (open question 3 answered)

Mutant = the second `FUN_00442b60` vector (`vec2`, deferred cleanup queue) count forced to `0`,
elements dropped (`vec1` and the entry list untouched; tutorial `vec2` 784→0 = −3,136 bytes;
`save_SaveGame` 6→0; re-parses to EOF). **Result: PASS on 3 fixtures** (`save_tutorial` ×2,
`save_tutorial4`, `save_SaveGame`) — all `rc=0`, gettime/map identical to control, no crash. The
loader restores `vec2` raw and nothing at load requires its content (consistent with §2.6:
deferred visual-cleanup queue fed by `FUN_00442910`). **Empty emission is safe** — a baseline-free
writer may emit `vec2` count `0`. The Port-mapping "capture live; empty-emission unproven" is now
**empty-emission PROVEN safe at load** (long-horizon cleanup behaviour still not exercised).

### Q-OQ4 — the second per-owner 512-float array: **role identified; capture-live** (open question 4)

Three tutorial mutants, ×2 each, **all PASS** (`rc=0`, gettime/map identical to control, no crash):
(a) second array zeroed, (b) second = copy of primary, (c) primary zeroed / second retained. So the
loader **tolerates any float content in either array** — they are plain data, not indices/pointers,
and none crashes load. (The harness cannot observe gameplay-semantic degradation, so the mutation
proves structural safety only.)

Corpus float-shape analysis (`accept-harness/probes/oq4_corpus.py`, 103/103 saves) shows the two
per-owner arrays are **DISTINCT, not primary+copy** (grid `2k` = array A / primary, grid `2k+1` =
array B / second):

| owner block | A==B (saves) | B all-zero | same nonzero-index set |
|---|---|---|---|
| own−1 (global) | 0/103 | 0/103 | 0/103 |
| own0 (human) | 0/103 | 0/103 | 0/103 |
| own+1 | 16/103 | 0/103 | 16/103 |
| own+2 | 92/103 | 0/103 | 92/103 |
| own+3 | 101/103 | 0/103 | 101/103 |
| own+4 | 103/103 | 0/103 | 103/103 |
| own+5/6/7 | 0/103 | 0/103 | 0/103 |

A==B only for **unused owners at boot defaults** (own+4). For **active owners A≠B in every save**,
they populate **different attribute indices** (B has indices A lacks, e.g. tutorial own0 B has
2,6–10,14,49 absent from A), and **B carries continuous/accumulator + timestamp-scale values that A
structurally never holds**: B index 447 (owner0) grows monotonically with game-time across the
 quicksave series (3340.6 → 4356.2 → 6317.8 → 8217.1 as gametime 4677 → 9748), whereas A
has **zero** values in the `1000<v<1e9` band across all 103 saves. This **refutes** "second ==
primary / previous-tick / min-max bound / per-attribute-timestamp *of* A": B is an **independent
second per-owner attribute table**.

Decompile confirms the split: the primary (A) is the value array objects read
(`FUN_0054b830`: `obj+0x78` attribute index → block `(owner+1)*0x808 + CObjMgr+0x84E4`, `index*4`),
which `SetOwnerAttrib` (`FUN_0062c510`) writes via `FUN_005daad0`, **clamped to a global registry
min (`+0x804`) / max (`+0x1004`)**; the second (B) is written by the paired second resolver
`FUN_005da900` and holds the parallel second-registry state (incl. the accumulators/timestamps).

**Likely role of the second array:** a second, independent per-owner attribute float table
(parallel `def_attrib` namespace) that shadows the primary for shared discrete attributes but
additionally holds continuous/accumulator attributes (rates, partial progress, game-time-scale
counters). **Safe emission rule for a baseline-free writer:** capture it from the port's
corresponding second owner-attribute table in the original boot attribute-index order; **do not
synthesize it from the primary** (neither zeroing nor copying reproduces the accumulator/timestamp
state). The loader accepts any finite floats without crashing, so a wrong second array will not
*block* load, but it silently corrupts that owner's second-table state — a faithful writer must
persist it. For an unused owner both arrays coincide at the boot-default pattern, so a fresh writer
may emit both = boot defaults there. This answers open question 4: **both arrays are owner-attribute
state and both must be captured; the second is not derivable from the first.**

### Q-OQ2 (B8) — CAnimCtrl model-slot numbering: **CONFIRMED deterministic**

Corpus decode (`accept-harness/probes/oq2_corpus.py`, 103/103) of every object-backed `CAnimCtrl`
record's `+0x38` model slot, joined to class via the declaration table (controller index == object
body ID): **639 / 642 distinct object classes have an identical model-slot set in every save they
appear in.** The only 3 that vary — `Kristallerz` `{7}`/`{7,32}`, `Halbzeug_holz`
`{3}`/`{3,7}`/`{7}`, `Halbzeug_stein` `{3}`/`{7}` — are transient carriable-item classes exhibiting
the B8-finding-5 **controller-index-reuse leak** (e.g. a `Kristallerz` object recycling an index
last held by a `Spinne` keeps that model's slot 32 until its first `SetAnim`); this is residue, not
registration non-determinism. Slot→class families are coherent single models (slot 0 = gnome, 1 =
gnome-alt/frau, 2 = tools, 6 = buildings, 7 = resource lumps, 8 = food/potions, 10 = hamsters, 12 =
transport/ladders, 4 = hat dummies, 255 = Info/markers, −1 = pure-logic objects). **The
model-slot→model mapping is boot-deterministic**: the port can reproduce `+0x38` by registering
models in the same class/script boot order (B8 §OQ2 recommendation validated). Full stable
class→slot table dumped in `accept-harness/probes/runlog/oq2_corpus.txt`.
