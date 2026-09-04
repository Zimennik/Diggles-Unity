# B8 — Rebuild-vs-require policy: CAnimCtrl table (marker 0x50→9) and object-icon table

Evidence base: per-function decompile the per-function decompile of `Diggles.exe`,
instruction dumps `Tools\SaveFormatResearch\save_core_dump.txt`, exe `.rdata` constant
extraction (`probes\b8_rdata_consts.py`), and a full-corpus cross-tab probe over all 103
configured saves (`probes\b8_animctrl_object_xtab.py`). Line numbers refer to the decompile
files (`<addr>_FUN_<addr>.c`).

## Status

Both tables are now closed at the policy level. The CAnimCtrl table is the render-side
animation authority, bound to objects purely by the shared instance index (`obj+0xDC` = object
save key = controller slot); load wipes all 8192 slots and installs exactly what the save
contains, so a port-written save MUST carry one semantically correct record per live object
index (model slot, anim id, mode, base frame, start time) and MAY omit the ~76% orphan records
(controllers of destroyed objects — the "static/non-object" population in the format doc is a
proven native leak, not a static-instance requirement). All fields are now typed to instruction
level: the 64-bit value is an absolute simulation-clock start timestamp (10 FPS law
`frame = base + round((now-start)*10.0+0.05)`), and the value vector is a copy of the model
animdb's per-frame frame-value list, fully derivable from (model slot, anim id). The object-icon
table is presentation-only and effectively write-only across load: object bodies do not
serialize the per-object icon holder, so the original itself orphans every loaded slot; types
0/3 are re-derived per tick from `atr_Hitpoints`/production state, type 5 comes from Tcl
`SetIcon`. An all-inactive 128-slot table is a safe, load-accepted emission; type-5 slots can be
emitted from port work-icon state for full visual continuity.

## Findings

### A. CAnimCtrl table

1. **Table identity and object binding law — PROVEN.** The table is 8192 refcounted
   `CAnimCtrl*` slots at `[DAT_00772400]+0x15C8` inside the graphics singleton (loader call site
   `00556C6C..7C`: `MOV ECX,[0x772400]; LEA ECX,[ECX+0x15C8]; CALL 0x461E20`; writer mirror at
   `005576CD..DD` — save_core_dump.txt:2535-2548, 682-695; destructor iterates the same member,
   FUN_006b3420:75-77). Objects reference their controller by instance index at `obj+0xDC`;
   the ObjMgr keeps the reverse map at `objMgr+0x488` (FUN_005d2290:15-35, FUN_005d2370:20-41).
   The saved object-body table is keyed by the *same* index: the write loop walks
   `objMgr+0x488[2..0x1FFF]` emitting one body per non-null slot (FUN_005d6790:32), and the
   read loop forces each body's index into the side channel `objMgr+0x41C` before recreating the
   object (FUN_005d6790:90-106), which the object constructor consumes via `FUN_005d2290`
   (bind given index) instead of `FUN_005d26a0` (allocate) — FUN_005c7b70:66-72. Therefore
   controller record ↔ object rebinding on load is **by index only**; no pointer or search.

2. **Load semantics: full replace, no tolerance for missing live-object records — PROVEN.**
   `FUN_00461e20` mode 1 first releases **all** 8192 slots (`FUN_00461dc0`, whole-table clear),
   then for each archived record allocates a fresh `CAnimCtrl`, installs it at the archived
   index, and deserializes fields (FUN_00461e20:19-62). Consequences:
   - controllers created earlier in the load (object constructors call
     `FUN_005cdae0(-1,-1)` → `FUN_00461b90`, which allocates a table entry per object;
     FUN_005c7b70:73) are destroyed and replaced by save content;
   - an object index with **no** saved record ends with a NULL slot. `FUN_005cb9a0` (the
     per-tick object↔controller sync) tolerates NULL (model/anim resolve to −1, object renders
     nothing — FUN_005cb9a0:35-53, 67-69), but `FUN_00419130:69-95` dereferences
     `table[obj+0xDC]` with **no NULL check**, and a NULL controller also permanently blanks the
     object's model until some code path calls SetAnim again. So a record is **required** for
     every live object index, with real content for every visible object.
   - Corpus law (probe, 103/103 saves): 613,909 records; **every one of 145,683 object body
     indices has a controller record (0 missing)**; self-index `+0x28` equals the outer index in
     all 613,909 records.

3. **Record field semantics — PROVEN** (creation `FUN_00461b90`, serializer `FUN_004620e0`,
   evaluator `FUN_00462040`, consumer `FUN_005cb9a0`):

   | offset | serialized | meaning | evidence |
   |---|---|---|---|
   | `+0x08` byte | yes | no reader found; widespread `0xDF` residue in corpus | LIKELY dead (see Open questions) |
   | `+0x10` double | yes | **animation start timestamp** in simulation-clock seconds; snapshot of `[DAT_00772400]+0xA0B0` taken at SetAnim | FUN_00461b90:98-100 |
   | `+0x18` int | yes | frame count of the current anim (copy of animdef `+4`) | FUN_00461b90:102-104 |
   | `+0x1C` int | yes | last computed frame; `-1` = fresh (first eval then yields 0/base, **not** base+elapsed) | FUN_00462040:20-22, 37 |
   | `+0x20/+0x24` vector | yes (count + elements) | copy of the model anim's **per-frame frame-value list**, indexed by current frame to produce the displayed mesh-frame value (`obj+0x3A0`) | copy loop FUN_00461b90:107-124; consumption FUN_005cb9a0:111-125 |
   | `+0x28` int | yes | self index (== table index; invariant, corpus 0/613,909 mismatches) | FUN_00461b90:67 |
   | `+0x2C` ptr | **no** | model pointer, rebuilt on load from `+0x38` via model DB `[DAT_00772400+0x5C8+slot*4]` | FUN_004620e0:24-34 |
   | `+0x30` int | yes | play mode: 0 Still, 1 Once (self-converts to 0 at last frame), 2 Loop; any other value degrades to frame 0 | FUN_00462040:14, 23-36 |
   | `+0x34` int | yes | base frame (start offset; final frame after Once completes) | FUN_00461b90:101; FUN_00462040:25-28 |
   | `+0x38` int | yes | model-DB slot `-1..255` | FUN_00461b90:79-96 |
   | `+0x3C` int | yes | anim id within the model's animdb | FUN_00461b90:97 |

   Version gate: `< 0x82` reads one extra discarded word (FUN_004620e0:36-38) — absent in `0xA5`.

4. **Frame/time law — PROVEN.** `FUN_00462040`:
   `elapsed = ROUND((simNow − start) * 10.0 + 0.05)`; Still → `base`; Once →
   `min(base+elapsed, count−1)` then mode:=0, base:=count−1; Loop → `(base+elapsed) % count`
   (only when `+0x1C != −1`; a fresh `−1` cache returns 0 once). Constants extracted from the
   exe `.rdata`: `DAT_007373F0 = 10.0` (double, frames/second), `DAT_00737190 = 0.05`,
   sim tick `DAT_007371A8 = 0.1 s` (probe `b8_rdata_consts.py`) — i.e. the known 10 FPS
   animation clock. The clock `+0xA0B0` is re-stamped from the game-thread simulation time
   every server tick (CGameThread_SrvDoThink:82, 152), and that simulation time is itself the
   header double at save offset 9 (ORIGINAL_SAVE_FORMAT_RE.md:84, from engine `+0xA088`), so
   saved absolute start-times stay meaningful after load. Corpus check: all 613,909 start
   times finite; none exceeds the header game time (probe).

5. **The 468,226 "static/non-object" controllers are orphans of destroyed objects, not
   world-build controllers — PROVEN (mechanism) / corpus-confirmed.**
   `FUN_00461b90` is the only controller-creating function and its only callers are
   `FUN_005cdae0` (CObj SetAnim core) and `FUN_0069faa0` (`TCL:CTclObjMethods::SetAnim`),
   both driven by `obj+0xDC` — there is no non-object creation path. Object detach
   (`FUN_005d24d0:43-44`) clears `obj+0xDC` and the ObjMgr reverse map but **never frees the
   controller slot**, so every destroyed object leaks its controller into the table. On index
   reuse the residue controller is adopted, its model slot reset (FUN_00461b90:32-41, 79) and
   fully overwritten by the first real SetAnim. Probe: 468,226 records (76.3%) have no object
   body; 99.7% of them still carry a valid model slot (a dead object's last anim); their count
   grows monotonically over one play session (204 → 1,513 across the..19 quicksave
   sequence). The format doc's "static map/render instances occupy additional indices" should
   be read as this leak; every *live* instance, including map deco, is in the object table
   (the body write loop covers every occupied ObjMgr slot).

6. **Omission policy — PROVEN by the load path.** Orphan records may be omitted: their slot
   stays NULL, nothing dereferences a slot without an object (`FUN_005cb9a0`/`FUN_00419130`
   only reach the table through `obj+0xDC`), and object creation re-allocates a controller on
   demand (FUN_00461b90:32-41). Records for live objects may **not** be omitted or defaulted
   to `model=-1` for visible objects (finding 2): the controller is the render authority —
   `FUN_005cb9a0` copies controller state *into* the object display fields each tick, not the
   other way around.

7. **Writer emission contract — PROVEN.** Mode 2 of `FUN_00461e20:64-85` walks indices
   0..0x1FFF ascending, writes `index` + record for each non-null slot, then terminator `-1`.
   Allocator cursor `objMgr+0x418` (round-robin next index, step 1 in SP / 8 in MP —
   FUN_005d26a0:14-38) is itself serialized for version > 0x92 (FUN_005d6790:360-362), keeping
   post-load index allocation deterministic.

### B. Object-icon table (128 slots)

8. **Serializer and load fixups — PROVEN.** `FUN_004c4370` reads/writes all 128 fixed 0x20-byte
   slots (no count, no terminator): object reference (`FUN_00555d70`), value (`+0x08`), opaque
   runtime word (`+0x0C`, always written `-1` by init `FUN_004c1640:73`), type (`+0x04`),
   XYZ offset (`+0x10..0x18`) — FUN_004c4370:15-21. Load fixup: unresolved object (or a
   production-block object at version < 0x84) → `type = -1` (deactivated/free; free test is
   `type == -1`, FUN_004c1640:31-44) — FUN_004c4370:22-25. **The loader validates nothing else
   and accepts an all-inactive table.**

9. **The per-object icon holder is NOT saved — PROVEN.** The holder lives at `obj+0x2B8`
   (16 slot-index words; flag bytes at holder`+0x40`; per-slot heights at holder`+0x50`;
   type-0 value cache at holder word `0x25` = obj word `0xD3` — identified by matching
   `FUN_005dc850`'s `param_1[0x25]` cache against `CObj_Tick`'s `param_1[0xD3]`,
   FUN_005dc850:198-201 / CObj_Tick:431). The object body serializer jumps from the byte at
   `obj+0x2B4` directly to `obj+0x35C` (FUN_005d0ce0:289-290), skipping the entire holder.
   Therefore after load **no object holds its saved icon slots**; all icon updates/releases go
   through holders (`FUN_005dc850`, `FUN_005dcc10`, per-tick `FUN_005dd100`), so loaded active
   slots keep drawing but are never updated or released — the original itself treats saved
   icon slots as frozen decoration and re-creates fresh slots when producers fire.

10. **Native producers (what re-creates icons after load) — PROVEN.**
    - **Type 0 (hitpoints):** `CObj_Tick:426-434` — for object kinds 1/2/0xB, when
      `hpFraction <= 0.5f` (`DAT_0073708C = 0.5f`) or `round(hpFraction * 15.0f)`
      (`DAT_00737568 = 15.0f`) differs from the holder cache, calls
      `FUN_005dc850(type=0, value=round(15*fraction))`. Also asserted from
      `FUN_005e3350:26-29` and `FUN_005242b0:58-61`. Held type-0 slots are refreshed every tick
      from `atr_Hitpoints` by `FUN_005dd100:47-85`.
    - **Type 3 (production progress):** asserted alongside type 0 in `FUN_005e3350:41-44`;
      held type-3 slots refreshed per tick in `FUN_005dd100` (second branch). Type 3 is absent
      from the 103-save corpus — it is transient producer-driven state.
    - **Type 2:** one-shot production icon created in the production tick
      (`FUN_005edea0:179-199`, `FUN_004c1640(type=2, value=0)`) guarded by the production
      record flag `prodRec+0x140`.
    - **Type 5 (class/work icons):** Tcl `SetIcon` (`FUN_0068e040:182` → `FUN_005dc850`);
      allowed only for local-player objects with a valid icon index (`FUN_004c1640:12-24`).
      Not re-asserted spontaneously after load — reappears on the next Tcl SetIcon call
      (work assignment change).
    - Corpus: 2,018 active slots — type 5 ×1912,
      type 0 ×58, type 1 ×47, type 2 ×1; every active slot resolves; opaque always −1.

11. **Requirement verdict — PROVEN.** The original does **not** require saved icon slots for
    correct gameplay: the table is presentation-only (drawn by `CDisplay_Draw` from the
    manager), the loader self-deactivates anything unresolvable, and the object side is
    reconstructed producer-driven. Omitting all slots costs only: (a) hp/production icons blank
    until the first tick that re-asserts them (≤1 sim tick for damaged objects), (b) type-5
    work icons blank until the next Tcl SetIcon. Conversely, emitting stale active slots is the
    *native* behavior but leaves frozen, unreleasable slots (bounded by 128).

## Port mapping recommendation

### CAnimCtrl (port doc: `OriginalSaveAnimationControllerDocument`)

Emission rule per **live object instance index** (same key the port uses for the object body
table) — one record, ascending index order, `-1` terminator:

| field | source in port | class |
|---|---|---|
| index / `+0x28` | object instance index (object-table key) | port state (required) |
| `+0x08` byte | emit `0` | compatibility-retain (no known reader) |
| `+0x10` start time | `simTimeAtSave` (header game-time double) | port state; re-anchor at save (see rule below) |
| `+0x18` frame count | current clip frame count (native animdb order) | derivable from (slot, anim) but REQUIRED in record |
| `+0x1C` cached frame | current display frame | port state (emit current frame, **not** −1, so the first native eval continues seamlessly — FUN_00462040:20-22) |
| vector | the clip's native per-frame value list from the source `.3db` animdb | derivable-on-load; emit the exact copy for writer acceptance |
| `+0x30` mode | `DigglesAnimator.AnimMode` — enum values already match native (Still=0/Once=1/Loop=2, DigglesAnimator.cs:74) | port state (required) |
| `+0x34` base frame | current display frame | port state (required) |
| `+0x38` model slot | native model-DB slot of the entity's model | port state (required; cross-table contract, see Open questions) |
| `+0x3C` anim id | index of the current clip within the model's animdb | port state (required) |

**Re-anchor rule (deterministic, exact):** at save time set `base = cached = currentFrame`,
`start = simNow`. Then native playback resumes at exactly the saved frame for Still, Once and
Loop (`frame = base + round((now-start)*10+0.05)`; elapsed = 0 at load). For a finished Once
anim emit mode 0 with `base = lastFrame`. Entities without a model (pure logic objects) emit
`slot = anim = -1`, count 0, empty vector, mode 0 — mirrors the 1,411 corpus records of that
shape.

**On load** the port may derive: model pointer/model asset (from `+0x38`), clip identity
(from `+0x38`+`+0x3C`), display frame (evaluator law, finding 4), and may ignore the vector
(recompute from its own model data). **Omit** all orphan records; keep the reader tolerant of
them (it already is).

### Object icons (port doc: `OriginalSaveObjectIconDocument`)

- **Required for original-exe acceptance: nothing.** Emit all 128 slots with
  `objref = null, type = -1, value = 0, opaque = -1, xyz = (0,0,0)`; the native loader
  deactivates/accepts (finding 8) and native producers rebuild types 0/2/3 within a tick.
- **Recommended fidelity pass (deterministic emission from entity state):**
  - type 0: for each object of kind 1/2/0xB with `hpFraction < 1.0`:
    `{obj, type=0, value=round(15*hpFraction), opaque=-1, xyz=(0,0,0)}` (creation path passes
    zero offset — FUN_005dc850:123-143);
  - type 5: for each local-player gnome with an active work icon (port's SetIcon model):
    `{obj, type=5, value=iconIndex, opaque=-1, xyz=(0,-0.3,0)}` (offset per FUN_005edea0:193-195
    creation pattern);
  - types 2/3: skip (producer-driven; type 3 absent from all 103 native saves).
- **On load** the port must treat loaded slots as one-time presentation hints only and drive
  its own icon presenter from entity state; it must NOT couple icon lifecycle to loaded slots —
  the original provably doesn't (finding 9).

## Open questions

1. `CAnimCtrl+0x08` byte: serialized but no reader located; corpus shows allocator residue.
   Treated as dead — emit 0. A residual reader in unexplored render code would be benign
   (bool-like) but is unverified. (LIKELY, not PROVEN.)
2. Model-slot numbering (`+0x38`) is a cross-table contract with whatever restores the model DB
   at `[DAT_00772400]+0x5C8` (0..255). The port must emit slots consistent with the section
   that persists model registration order (Display/"Graphics" state family, `FUN_0055a950` —
   not audited here). Needs one targeted check before writer acceptance runs.
   **RESOLVED (CONFIRMED deterministic):** corpus decode over 103/103 saves
   (`accept-harness/probes/oq2_corpus.py`) shows 639/642 object classes have an identical `+0x38`
   model-slot set in every save; the 3 exceptions (`Kristallerz`, `Halbzeug_holz`,
   `Halbzeug_stein`) are the finding-5 controller-index-reuse leak, not registration variance.
   The slot→model mapping is boot-order deterministic, so the port reproduces `+0x38` by
   registering models in the same class/script boot order. See
   `B3-support-class-spatial.md` §"Empirical mutation results → Q-OQ2".
3. `FUN_00479c10(1,1)` is called immediately before the table load (00556C67) — purpose
   unidentified (suspected render-cache flush); no impact found on record semantics.
4. Type-1 icon producer (47 corpus slots) not identified; type gate in `FUN_004c1640` only
   distinguishes 5 vs 0 vs "other for local player", so type 1 is another producer-driven
   local-player icon. Cosmetic.
5. Whether the original visibly duplicates hp icons after loading its own saves (stale loaded
   slot + fresh producer slot at the same position, finding 9/10) is untested live; irrelevant
   to the port policy but would confirm the orphan-slot analysis end-to-end.
6. 984 object-backed records carry non-0/1/2 mode residue; `FUN_00462040` degrades them to
   frame 0. The port emits only 0/1/2; the reader should keep accepting residue.
