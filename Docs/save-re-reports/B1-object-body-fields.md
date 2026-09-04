# B1 — Version 0xA5 generic object body: remaining field groups named

## Status

All seven target groups in the `FUN_005d0ce0` common object body now have instruction-verified
semantics. The method was: recover the exact memory offset each archive field round-trips to from
the instruction dump of `FUN_005d0ce0` (`Tools/SaveFormatResearch/save_sections_dump.txt`, listing
starts line 3950; `FUN_005d0ce0` is both reader and writer — archive mode 1 reads, mode 2 writes,
so the write function is its own read counterpart), then grep the full per-function decompile for
other users of each offset, then confirm with a 103-save corpus probe
(`Tools/SaveFormatResearch/probes/b1_field_probe.py`, 145,683 bodies, zero failures). Headline
results: the "16 fixed values" are a per-player fog-brightness cache; "route state" is actually the
Tcl state machine plus four Tcl-settable gameplay words plus the per-model-part frame vector;
the 512-value grid is the native `def_attrib` attribute page; "attachment history" is the per-part
frame-sequence table; the 64-slot support table is the per-object EVENT queue (a 64-entry FIFO
ring of `CEvent` records with two cursors and a delayed-event vector); the tail word is the
pick/target mode and the tail byte is the destroy-queue guard. The three 16-entry groups at
+0x2B8/+0x2F8/+0x308 have no runtime readers anywhere in the decompiled executable and are
retired legacy state.

## Offset map recovered from the FUN_005d0ce0 instruction stream

Anchors cite instruction addresses inside `FUN_005d0ce0` (save_sections_dump.txt). `obj` = CObj
base (constructor `FUN_005c83f0`, vtable `CObj::vftable`, magic `obj+0x8 = 0x616e4f62 "bOna"`).

| Archive order (parser name) | Native offset | Anchor | Meaning (this report) |
|---|---|---|---|
| 16 fixed values | +0x6C..+0xAB | `005d10a3 LEA EDX,[EDI+0x6C]` → `FUN_005b92b0` | per-player fog-brightness cache |
| object reference after Tcl vars | +0xD0 | `005d10ba` | state-scope object ref (ctor: self) |
| state string | +0xD8 | `005d10c7` | auto instance name (`FUN_005c9fa0:45-61` regenerates it) |
| state group 1 | +0x200/+0x204/+0x208/+0x20C | `005d10d4..0x1122` | current state / requested state / state gate / dead |
| state group 2 | +0x220/+0x224/+0x228/+0x22C | `005d1128..0x115b` | inventory slot use / place-snap mode / weapon class / shield class |
| "route" vector | ptr +0x23C, count +0x240 | `005d1160-116f` → `FUN_004d9df0` | per-model-part current frame IDs |
| "route state" word | +0x248 | `005d1174-1176` | walk result (`get_walkresult`) |
| "route time bits" | none (stack local) | `005d117b-1188` | retired: writes constant 0xFFFFFFFF, discarded on read |
| 512-value grid (Int32 presence) | page ptr +0x21C | `005d1192-11b5` → `FUN_005db100` | per-object attribute page (512 floats) |
| attachment history (byte presence) | table ptr +0x244 | `005d17a6-17b3` → `FUN_004d9b60` | per-part frame sequences |
| later 16×Int32 group | +0x2B8..+0x2F7 | `005d17c8` → `FUN_005b92b0` | retired (no runtime users) |
| later 16×byte group | +0x2F8..+0x307 | `005d17dc ADD ESI,0x40` + 16×1-byte raw | retired (constant pattern) |
| later 16×Int32 group | +0x308..+0x347 | `005d1849-184e LEA ESI,[EDI+0x308]` + 16×4-byte raw | retired floats (init -1.0f) |
| byte / word / byte | +0x348 / +0x34C / +0x349 | `005d18c0/18ce/18e8` | adjacent flags + count-down word (init 15), outside B1 scope |
| action variant (byte presence) | +0x3EC | `005d1902-1971` → `FUN_006071f0` | mover/mechanism logic (doors/elevators/platforms) |
| 64-slot support table | embedded at +0xEC | `005d1982 LEA ECX,[EDI+0xEC]` → `FUN_005bab70` | per-object event queue |
| its two lead words | +0x1EC / +0x1F0 | `FUN_005bab70:30-31` | ring read cursor / ring write cursor |
| its tail vector | ptr +0x1F4, count +0x1F8 | `FUN_005bab70:107-153` | delayed (scheduled) events |
| body-tail four-byte value | +0x2AC | `005d199a` (v>0xA0) | pick/target mode 0/1/2 |
| body-tail final byte | +0x404 | `005d19c3` (v>0xA2) | destroy-queue guard |

Related transient fields (present in memory, deliberately NOT serialized): +0x210/+0x214/+0x218
(state-machine scratch; +0x218 = suspend gate, `CObj_HandleStates:27`), +0x1FC (currently
dispatched event pointer, `FUN_00673400:91-100` "no event"), +0x2A8 (init 2, untouched by the
serializer).

## Findings

### 1. "16 fixed values" (+0x6C..+0xAB) = per-player fog-brightness cache — PROVEN

- `FUN_005cb3a0(obj, index)` is the getter: walks the link chain to the attachment root via the
  registry at `DAT_0077244c+0x488` (lines 21-33), then, if `obj[0x6C + index*4] < 0`, computes and
  caches (lines 41-51), returning the slot (line 57). Negative index returns 0xFF = fully lit
  (line 18-19).
- Compute chain: `FUN_005cb250` builds the object's occupied tile rect from position plus mesh
  extents of model refs +0x39C/+0x3A0 (lines 17-35); `FUN_00580ea0` samples the per-player fog
  byte over the rect corners/midpoints and keeps the max, saturating ≥0xE3 to 0xFF (lines 14-75).
  The fog byte accessor `FUN_00423700` hard-bounds the player index at 8 (lines 10-14), so only
  slots 0..7 are meaningfully computable; the 16-slot width is headroom. Tile lookup
  `FUN_00592f10` uses the native 0x7C tile stride (line 27-29), i.e. the same per-tile fog bytes
  at tile+0x34..+0x3B already documented in ORIGINAL_SAVE_FORMAT_RE.md.
- Bulk refresh: `FUN_005cb4a0` recomputes every negative slot (lines 20-57). Consumer example:
  render sync `FUN_005f5ef0:414-422` marks the object visible when
  `FUN_005cb3a0(currentPlayer) >= 0x80` (current player = `game+0x64`).
- Constructor init is -1000 (`FUN_005c83f0:329-340`, value 0xFFFFFC18); invalidation writes -1.
  The class-table entry has the same 16-slot cache (`FUN_005b7250:29-39`), which is why class
  records in the save also lead with 16 words.
- Corpus (145,683 bodies): slots 0..7 = { -1: 1,000,308; 0..255: 165,110; -1000: 46 };
  slots 8..15 = { -1: 1,165,416; -1000: 48 }. Exactly the value domain of a lazily invalidated
  byte cache; slots 8..15 never hold a computed value.

### 2. "Route state" decomposed — PROVEN (each item cited)

The old "route state" label covered three unrelated things:

a) **Tcl state machine** (+0x200 current state index, +0x204 requested state index, +0x208
   state-think gate): `CObj_HandleStates` (005cac90) runs exit/enter/think scripts when
   `+0x218 == 0 && +0x208 > 0` and the indices differ (lines 27-104); state records come from
   `FUN_0045f6d0`, per-state script blocks at +0x8 (enter), +0x20 (think), +0x38 (exit); the
   class's state count is `class+0x8C` (line 82). `FUN_005c9fa0:77-79` sets +0x208 = -1 for
   classes without states; the serializer's read fixup forces 0 → 1 for versions < 0xA5
   (`005d10fe-1114`); the death path `FUN_005c2910:77-81` reads attribute "atr_Hitpoints" and
   writes +0x208 = 0. Corpus: +0x208 ∈ { -1: 136,671; 0: 6,938; 1: 2,074 } — no other values, so
   it behaves as a tri-state gate, not a counter.
   +0x20C has no runtime user in the whole decompile and is 0 in all 145,683 bodies — dead.

b) **Tcl-settable gameplay words** (+0x220..+0x22C):
   - +0x220 inventory slot use — `TCL:CTclObjMethods::SetInventorySlotUse` (`FUN_00695830:75-90`,
     also mirrors the value to `class+0x64`). Corpus: always 1.
   - +0x224 place snap mode — `TCL:CTclObjMethods::SetPlaceSnapMode` (`FUN_0067d8b0:73-81`);
     consumed by the placement UI (`FUN_00530e80:119-129`, value 1→mode 8, 2→mode 9).
     Corpus: 0 except 99 × 2.
   - +0x228 weapon class index — `TCL:CTclObjMethods::SetWeaponClass` (`FUN_006897a0:75-83`);
     fight resolution uses it with default class "none" (`FUN_005beaf0:19-24`,
     `DAT_00722ca4 = "none"`). Corpus: 0 plus indices {39, 31, 32, 26, 43, 1} on 111 objects.
   - +0x22C shield class index — `TCL:CTclObjMethods::SetShieldClass` (`FUN_0068a5a0:75-83`);
     read as the defender's shield in `FUN_005beaf0:27-38`. Corpus: always 0 (command exists but
     unused in shipped content).

c) **Per-part frame vector** ({ptr +0x23C, count +0x240}, serialized by `FUN_004d9df0` as count +
   N raw words): the render sync `FUN_005f5ef0` fills `+0x23C[i]` from the +0x244 sequence records
   (`record.values[record.cursor]`, lines 218-257, setting dirty byte +0x3B4) and copies the
   vector into the render instance's own part list (lines 258-291). So the values are the current
   frame/mesh ID per model part (building stages, mechanism animations). Corpus lengths:
   0: 127,135; 1: 15,738; 5: 2,362; 2: 381; 3: 64; 4: 3.

d) **+0x248 walk result — the `get_walkresult` field**: `TCL:CTclObjMethods::GetWalkResult`
   returns exactly this word (`FUN_0068fb80:31,58`). Writers are the walk actions:
   `CActWalk_AutoRetry` propagates its result (`FUN_004075b0:104-109`), writes 3 when the goal
   rect is outside the world (`:50-51`), other action paths write 2
   (`FUN_00407430:64`, `FUN_00407bc0:65`) or copy `action+0x24` (`FUN_00407b30:12`). Constructor
   init 1 (`FUN_005c83f0` [0x92]). Corpus: { 1: 140,684; 4: 2,955; 2: 1,870; 3: 174 }.
   This is the native field behind the port's known `get_walkresult` stub blocker.

e) **Trailing constant**: `005d117b-1188` writes local = 0xFFFFFFFF, transfers it, and never
   stores the read value — a retired field. Corpus: 0xFFFFFFFF in all 145,683 bodies. The port
   document's name "RouteTimeBits" is a misnomer; it is write-only padding.

### 3. Optional 512-value grid (+0x21C, Int32 presence) = per-object attribute page — PROVEN

- The global registry singleton `DAT_00772460` (`FUN_005da0f0:22-61`) holds 512 named slots:
  names at +0x4, lower bound 0.0 at +0x804, upper bound 1.0 at +0x1004, global default values at
  +0x1804. Name→index lookup `FUN_005da2f0`.
- Tcl seams: `TCL:CTclGlobalMethods::Def_Attrib` (`FUN_0062dd10`… registration path
  `FUN_0062c040:43`), `Set_OwnerAttrib` (`FUN_0062c510:47`), `Get_OwnerAttrib`
  (`FUN_0062c9b0:37`), `EvtGenAttrib` (`FUN_0063eb70:63`). Object reads address slots by name,
  e.g. literal `"atr_Hitpoints"` in the death check (`FUN_005c2910:77`).
- Per-object override page: `FUN_005daf50:19-31` lazily allocates via `FUN_005da770` (the same
  ctor the loader calls at `005d11f6` with alloc tag 0x117) and writes through
  `FUN_005daad0`; the getter `FUN_005dae40` falls back to defaults when +0x21C is null (this is
  visible in the decay tick `FUN_005d4960:76-90`, which reads `mgr+0x1804+slot*4` when the object
  has no page). Set path clamps to the per-slot bounds and fires change notification
  (`FUN_005da9c0:17-61`).
- Owner-level values live in nine 0x808-byte pages at `registry+0xCD2C` (`FUN_005d4960:60-67`),
  which is what the already-documented 18×512-word block in the object-manager tail serializes.
- Fight processing reads/writes object attributes (`FUN_005bf660`,
  trace "TCL:CFightActionMgr::DoObjActions", calls `FUN_005dae40`/`FUN_005daf50` on
  `obj+0x21C`).
- Corpus: 36,285 objects carry a page; on every page the same ~340 slots are nonzero (the page is
  seeded from the global defaults at allocation, i.e. the number of registered attributes);
  float extremes (min ≈ -4.76e32, max ≈ 115,919.77) occur and load fine in the original, so raw
  bit retention is required.

### 4. "Attachment-history" (+0x244, byte presence) = per-part frame-sequence table — PROVEN

- Serialized shape (`FUN_004d9b60:15-28`): count, then per record (memory stride 0x10):
  int vector {ptr, count} via `FUN_004d9df0`, then word +0x8, then word +0xC.
- Runtime meaning (`FUN_005f5ef0:218-257`): record = { frameIds[], count, cursor, extra };
  each tick the object's +0x23C vector gets `frameIds[cursor]` (0 when the cursor has run past
  the end), then the dirty byte +0x3B4 is set. So the table holds per-model-part frame *sequences*
  with a play cursor — the mechanism behind staged building models (bau_a..bau_k) and animated
  machine parts; the +0x23C vector is its flattened output.
- The loader allocates the container with two zero words at `005d1771-17a0` (alloc tag 0xEB6)
  before `FUN_004d9b60` fills it.
- Corpus: present on 19,883 bodies; record count equals the +0x240 vector length in 18,250 of
  them (the rest differ transiently because the render sync resizes +0x23C on demand,
  `FUN_005f5ef0:222-224`). Record-count distribution matches the vector-length distribution
  exactly (1: 16,703; 5: 2,711; 2: 402; 3: 64; 4: 3).
- The word at record +0xC never has an instruction-level reader in the corpus decompile —
  UNKNOWN; retain raw.

### 5. The three later 16-entry groups (+0x2B8 words, +0x2F8 bytes, +0x308 words) — retired; LIKELY dead at runtime

- Emission: second `FUN_005b92b0` call with EDX = obj+0x2B8 (`005d17c8-17d2`), 16×1-byte raw loop
  over obj+0x2F8 (`005d17dc-1847`), 16×4-byte raw loop over obj+0x308 (`005d1849-18be`); all
  version-gated > 0x83.
- Constructor init (`FUN_005c83f0:97-126`): +0x2B8 words = -1; +0x2F8 bytes = {0×8, 1×8};
  +0x308 words = float -1.0f (0xBF800000).
- Exhaustive grep of the complete per-function decompile finds NO other function referencing
  +0x2B8/+0x2F8/+0x308 (or their int-index forms [0xae]/[0xbe]/[0xc2]) — only the constructor
  and the serializer. Verdict LIKELY (dead) rather than PROVEN only because compiler-folded or
  memcpy-style access cannot be fully excluded by text search.
- Corpus supports dormancy:
  - +0x2B8: -1 in 2,328,911 of 2,330,928 words; the remaining 2,017 words (~126 objects) hold
    small ints 0..60+ — stale values migrated through older-version saves.
  - +0x2F8: a single byte pattern in ALL 145,683 bodies: `01 00 00 00 00 00 00 00 01×8`
    (byte[0] flipped to 1 from the ctor's 0 by some one-shot init not located).
  - +0x308: 0xBF800000 (-1.0f) in 2,319,219 of 2,330,928 words; the rest are small floats
    (5.0, 60.0, ≈54..61) on a minority of legacy objects.

### 6. Body-tail final word (+0x2AC) and final byte (+0x404) — PROVEN

- **+0x2AC = pick/target mode {0,1,2}**, paired with flag bit 0x80000 in +0x3D0. Setter
  `FUN_005cd750`: disable → {0, clear bit}; enable → set bit, then type 1/11 (actors) → 2,
  otherwise non-"baum" classes → 1 (`DAT_0072ba20 = "baum"`, tree classes stay 0). Applied at
  creation from class-definition flag bit 4 (`FUN_005c7b70:74-91`). Consumers: object queries
  accept mode 1 unconditionally and mode 2 only when the object's owner (+0x36C) differs from the
  querying player (`FUN_005e6be0:72-75`); the render instance receives the value as its pick
  category (`FUN_005f5ef0:423`). Serialized only for versions > 0xA0, which is why it must be
  restored rather than recomputed: Tcl can toggle it at runtime after creation. Corpus:
  { 0: 118,571; 1: 24,492; 2: 2,620 }.
- **+0x404 = "already queued for destruction" guard byte.** Every kill path enqueues the object
  once into the CObjMgr pending-destroy list at `DAT_0077247c+0x40C/+0x410` guarded by this byte
  (identical pattern in `FUN_005d0a10:15-26`, `FUN_005cd1d0`, `FUN_005c8e90`, `FUN_005d4b40`,
  `FUN_005d57e0`, `FUN_005eae80`, `FUN_00607db0`, `FUN_004f7c30`, `FUN_004fa420`,
  `FUN_0058ba00`, `FUN_005b59f0`). The drain `FUN_00605c10` finalizes destruction: clears
  selection/current references at game+0xA0CC/0xA0D0/0xA0D8/0xA0E4, removes fluid
  sources/stoppers (`FUN_0057b5e0`/`FUN_0057c480`), runs the class destroy script, releases the
  registry slot. Serialized (v > 0xA2) so a mid-tick save keeps the mark, but the queue drains
  within the tick: corpus is 0 in all 145,683 bodies.

### 7. 64-slot support table (+0xEC) + tail vector = per-object EVENT queue — PROVEN

- Structure (`FUN_005bab70`): the two lead words are obj+0x1EC and obj+0x1F0 (param_1+0x40/0x41,
  lines 30-31), then 64 refcounted slot pointers at obj+0xEC..+0x1EB with per-slot presence
  bytes (lines 34-103), then a vector {ptr +0x1F4, count +0x1F8} of additional records
  (lines 13). Record payloads via `FUN_005b9f00`.
- **FIFO ring proof**: pop `FUN_005baae0` returns slot[+0x1EC] when `+0x1EC != +0x1F0` and
  advances the read cursor mod 64 (lines 16-37: `read+1 & 0x8000003f` with sign fix). Push side
  `FUN_005ba7a0` writes at slot[+0x1F0] and advances the write cursor mod 64 (lines 31-59).
  So +0x1EC = read cursor, +0x1F0 = write cursor. Pop does NOT null the consumed slot, so
  serialized slots outside [read, write) are stale residue — this explains the corpus: the
  pending window `(write - read) mod 64` is 0 on 145,679 of 145,683 bodies while up to 64 slots
  remain occupied (7,997 objects have all 64 slots populated by residue). Four objects in
  `save_tutorial3.sav`/`save_tutorial4.sav` were saved with genuinely pending events (windows of
  43 and 51), so the load side must actually deliver the [read, write) window, not assume it is
  empty.
- **Records are CEvents**: `CObj::HandleSingleEvent(CEventPtr pEvt)` (005caa00, symbol string at
  line 33) takes record word +0x8 as the event-class index and runs the per-class handler script
  from the 128-entry table at `class+0x90` (stride 0x18, lines 52-91), holding the record in
  obj+0x1FC while the Tcl handler runs (readable from Tcl via
  `TCL:CTclObjMethods::EventGet`, `FUN_00673400:91-100`). The drain loop is `FUN_005cabe0`.
- **Record layout** (`FUN_005b9f00`, cross-checked with the clone function `FUN_005ba0d0:36-78`):
  +0x8 event-class index — serialized as the fixed 32-byte class NAME resolved through the
  128-entry event-class registry at `game+0x95DC` (write: copy `classPtr+0xC` name, lines 30-47;
  read: `FUN_005ba5b0` name→index); four object references +0xC/+0x10/+0x14/+0x18; nine numeric
  args +0x28..+0x48; two strings +0x4C/+0x50; three words +0x54..+0x5C; one double +0x20 —
  written last in the archive — holding game time (clone stamps it from `game+0xA088`,
  `FUN_005ba0d0:78`).
- **Tail vector = delayed events**: `FUN_005ba7a0:19-30` promotes any record whose +0x20 double
  ≤ current game time from the +0x1F4 vector into the ring; so for vector entries +0x20 is the
  DUE time. Registration/creation seams: `TCL:CTclGlobalMethods::Def_Event` (`FUN_0062dd10:33`),
  `CTclObjMethods::CloneEvent` (`FUN_00672760:74`), `CTclGlobalMethods::EventGenerator`
  (`FUN_0063e700:38`), `Get/Set_EventEnabled` (`FUN_0062df80`/`FUN_0062e2a0`).
- **Relation to the marker-6 manager "support records"** (the 28-byte-prefix stream after the
  declarations): serialized by `FUN_00606500` as {raw4, raw8, raw8, raw4, object reference} +
  the same event payload; the event-manager tick `FUN_00606230` scans records whose prefix double
  at +0x8 has come due and CLONES the template event for delivery (`FUN_005ba0d0` call at line
  77). So the global stream is the repeating/scheduled event-generator table, NOT production or
  storage state. (Prefix field semantics beyond the due-time double: LIKELY due/interval pair +
  target ref; not fully pinned — see open questions.)
- Corpus: 673,830 serialized event records, every 32-byte name an `evt_*`/`anim_timer*` class:
  evt_timer0 228,070; evt_recource_init 69,118; evt_zwerg_workannounce 68,788;
  evt_zwerg_attribupdate 67,691; evt_pilz_aging 52,854; evt_btn_on 39,772; … Delayed vectors are
  almost always empty (exactly one save has one entry). Max due time 120,672.29 matches the
  known max corpus game time.

### 8. Adjacent context recovered while proving the above (not in the B1 target list)

- +0xDC is the object's stable registry ID (destroy drain `FUN_00605c10:57-88` uses it for
  selection/registry cleanup; equals the animation-controller index).
- +0x350 (first word of the "state350" triple) is the creation game-time float
  (`FUN_005c83f0:176`: `gametime / DAT_00737630`); [1]/[2] are the already-typed work window.
- +0x348/+0x349 byte pair and +0x34C word (ctor init 15): corpus { both bytes 1 on the same 920
  bodies; +0x34C = 15 except ~63 bodies with 1..14 } — a use/charge counter with two flags;
  semantics UNKNOWN (out of scope here).
- The +0x3EC "action variant" is the mover/mechanism logic object: `SetElevatorProperties`
  (`FUN_00695a60:86-161`) writes fields INSIDE it (its own +0x220/+0x224/+0x228 — not the CObj
  words of the same offsets), `CreateElevatorLogic` (`FUN_006792c0:35`) builds the related logic
  object. Corpus: 1,634 present; kinds {1: 1,176, 2: 249 (elevators with stop vectors), 0: 209}.
- Type-dependent sub-objects (+0x3E0/+0x3E4/+0x3F0/+0x3F4) areallocated by `FUN_005c9fa0`
  (lines 95-155) which the loader calls immediately after reading the type — that is why the
  serializer can rely on their presence without presence flags.

## Port mapping recommendation

| Field | Recommendation | Reason |
|---|---|---|
| +0x6C fog-brightness cache (16) | derivable-on-load; retain raw for byte-identical rebuilds; canonical writer may emit -1 ×16 | pure cache; original lazily recomputes any negative slot |
| +0x200/+0x204 state indices | map to port state machine (current/requested Tcl state) | live gameplay state; indices reference the class state list |
| +0x208 state gate | map ({-1 stateless, 0 disabled/dead, 1 active}) | controls whether state think runs; death sets 0 |
| +0x20C | compatibility-retain (always 0) | dead field |
| +0x220 inventory slot use | compatibility-retain (always 1 in corpus); expose to Tcl `SetInventorySlotUse` if implemented | Tcl-settable but unused by shipped content |
| +0x224 place snap mode | map to the port's placement/ghost-snap state | consumed by placement UI (modes 8/9) |
| +0x228/+0x22C weapon/shield class | map to FightResolver combatant config (index into fight-class table; 0 = "none") | fight resolution inputs |
| +0x23C/+0x240 part-frame vector | derivable from +0x244 (cursor application); retain raw until the port's building-stage/mechanism visuals consume it | render-derived output of the sequence table |
| +0x248 walk result | MAP — this is `get_walkresult`; feed from MetaWalkAction result codes (observed domain 1/2/3/4) | de-stubs the known work-dispatcher blocker |
| route placeholder word | writer emits 0xFFFFFFFF always | retired |
| +0x21C attribute page | MAP to the port AttributeSystem per-entity values (name-indexed via the def_attrib registry order); absent page = defaults; retain raw bits (garbage extremes are legal) | the native `set_attrib` store; port already has the registry |
| +0x244 sequences | map alongside +0x23C when mechanism/building-stage visuals are wired; otherwise retain raw incl. the unknown record word +0xC | drives part frames |
| +0x2B8 / +0x2F8 / +0x308 groups | compatibility-retain verbatim; canonical writer: -1×16 / `01 00×7 01×8` / 0xBF800000×16 | no runtime readers; corpus effectively constant |
| +0x348/+0x349/+0x34C | compatibility-retain | adjacent, semantics not yet named |
| +0x3EC mover object | retain raw now; map when doors/elevators are implemented (kind 2 carries elevator stops) | mechanism state incl. SetElevatorProperties data |
| event queue (+0xEC..+0x1F8) | MAP: pending events = ring slots in [read, write) mod 64 (in order), plus the delayed vector (due-time double). Slots outside the window are residue — retain for byte-identical rebuilds, never dispatch them. Resolve 32-byte names through the def_event registry; unresolvable names must fail preflight | live Tcl event delivery on load; residue must not be re-fired |
| +0x2AC pick/target mode | map (restore saved value; do not recompute from class) | Tcl/creation can change it after spawn; that is why v0xA0 started saving it |
| +0x404 destroy guard | assert 0 on load (tolerate+log 1 by dropping the object is the faithful alternative); writer emits current flag | queue drains within a tick; corpus always 0 |

Loader-order note: the original reconstructs type-dependent sub-objects in `FUN_005c9fa0`
directly after reading the class/type, BEFORE the rest of the body — a port mapper that
allocates its equivalents lazily must do so at the same boundary to keep the stream aligned.

## Open questions

1. Which one-shot init writes byte[0] = 1 in the +0x2F8 group (constant across the corpus but
   differs from the constructor's 0)? Text search found no writer; likely an inlined init.
   Practical impact: none — treat the 16 bytes as an opaque constant.
2. The word at frame-sequence record +0xC (serialized after the cursor) has no located reader —
   step/direction or wrap flag? Retained raw.
3. The exact meaning of the 28-byte prefix words of the global event-generator records
   (marker-6 stream): the double at prefix+0x8 is proven the due time (`FUN_00606230`); the
   second double is LIKELY the repeat interval and the trailing reference the target object, but
   the interval reload instruction was not pinned. Needed before the port can re-emit generators
   from live state instead of retaining them verbatim.
4. Who advances the +0x244 sequence cursors at runtime (candidates: the `anim_timer*` event
   handlers seen in the corpus histogram, or native mechanism ticks)? Matters only when the port
   implements staged-building visuals natively.
5. The ~126 legacy objects with non-default +0x2B8/+0x308 values: which pre-0xA5 engine build
   wrote them? Irrelevant for load correctness (no reader), relevant only for archaeology.
6. +0x348/+0x349/+0x34C (flag pair + count-down from 15 on 920 corpus objects) still need names —
   adjacent to but outside the B1 target list.
