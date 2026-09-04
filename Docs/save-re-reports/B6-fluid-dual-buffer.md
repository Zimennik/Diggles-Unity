# B6 — Fluid dual-buffer persistence law, tile word names, corpus statistics

> RE report,. Evidence: per-function decompile under
> the per-function decompile of `Diggles.exe`, instruction dump
> `Tools\SaveFormatResearch\save_sections_dump.txt` (FUN_0057ce70 at lines 156-1240),
> constants read from `Diggles.exe` PE sections, and a 103-save corpus probe
> (`Tools\SaveFormatResearch\probes\b6_fluid_probe.py`, all 103 saves parse to marker 4).

## Status

The full fluid load/tick consumption chain is now proven from the executable: the loader restores
tile buffers, flow words, indices, work sets, sources, and stoppers verbatim (with three
deliberate load-time overrides), and the first `CFluid::Tick` after a load skips exactly the
flow/commit step and the work-set rebuild step once. There is **no** pass anywhere that rebuilds
work sets or flow caches from cell state — the active set regrows only from saved set entries,
source emission, and the dig/set_water dirty queue. The four unnamed tile words at `+0x24..+0x33`
are the per-cell flow-momentum double buffer (prev/cur × horizontal/vertical); the well-known
`+0x1A..+0x20` pair is a Jacobi read/write split, not a ping-pong (buffer 1 is the live state all
queries read; buffer 0 is the start-of-tick snapshot that the transfer equations read). Synthetic
emission (buf0 == buf1, zero flow words, empty work sets, indices 0/1) is structurally accepted and
behaviorally identical for settled fluid — which is also what the original itself writes for
settled fluid; the corpus shows the only real divergences native produces are one-step-stale
residue on drained trails plus a frozen sub-threshold last delta on live cells. Mid-flow
continuation requires one honest addition (see Findings 8/9).

## Findings

### 1. Serializer `FUN_0057ce70` is bidirectional; load-time field law — PROVEN

`FUN_0057ce70(CFluid* this, stream)` (called only from world serializer `FUN_005931f0:267`,
caller list in `save_sections_dump.txt:159`) reads or writes depending on stream mode
(`*param_2`: 1 = load, 2 = save). Emission/consumption order (decompile lines 27-286; asm dump
lines 161-1240):

1. `Int32 sourceCount` → source array resized in place (`FUN_0057dee0` adds, `FUN_00463b00`
   removes); per source, 9 dwords (+0x00 ownerId, +0x04/+0x08/+0x0C offset floats, +0x10 type,
   +0x14 volume, +0x18 remaining, +0x1C height, +0x20 vpf) + 1 byte (+0x24 ventil) = 37 bytes.
   **Load override:** in-memory byte `+0x25` (source-dirty flag) is forced to 1 for every source
   (line 52 / asm 0057cf4d), and is *not* serialized.
2. `Int32 stopperCount` → 4 dwords + 1 byte + 6 dwords = 41 bytes each. **Load override:** the
   cached owner position (+0x04/+0x08/+0x0C) is overwritten with (-1000.0, -1000.0, 0.0)
   (lines 85-89 / asm 0057d043-0057d05c). This guarantees the first stopper pass sees a position
   mismatch and re-registers the stopper's dirty region.
3. Two `Int32` at `this+0xC6C` (current work-set index) and `this+0xC70` (previous index).
4. Four sparse `(tileX, tileY)` arrays, per index i in {0, 1}: first the `this+0xC74` group pair
   `[i]`, then the `this+0xC84` group pair `[i]` — order on disk: `c74[0], c84[0], c74[1], c84[1]`.
   On load the counts are read and the entries **appended** to the in-memory DSFastArrays.
5. One `waterdbg` byte at `this+0xCB5`.
6. **Load override:** the last statement sets `this+0x08 = 1` (line 282) — a "just loaded" flag
   consumed by the next tick (Finding 4).

The arrays are guaranteed empty before step 4 because the master loader `FUN_00556570` calls
`FUN_0055b510`, which at line 122 calls `FUN_0057cb60` = full CFluid reset (clears all four work
arrays, the dirty queue at `+0xCAD/+0xCB1`, all sources/stoppers, and sets curIdx = 0) — so the
"append" is an exact restore. PROVEN (FUN_0055b510:122, FUN_0057cb60:14-101).

### 2. Terrain-record fluid span is one raw 36-byte copy — PROVEN

`FUN_00592f90` (bidirectional, driven by the same stream mode) serializes the tile as: +0x00 dword,
+0x06 byte, +0x08 dword, +0x0C dword, +0x12 byte, +0x13 byte, +0x14 word, then a single raw
`0x24 = 36` byte block via the buffered stream helpers `FUN_006c6da0(0x24)` (read) /
`FUN_006c76b0(0x24)` (write) covering `+0x18..+0x3B` — i.e. material word, both fluid type bytes,
both fill floats, all four flow floats, and the 8 fog bytes (2+1+1+4+4+16+8 = 36; record total 53).
So on load `+0x24..+0x33` are restored to the live tile verbatim. On read mode the function then
resets the caches `+0x10`, `+0x04`, `+0x16` (FUN_00592f90:40-44), as already documented.

### 3. The two tile buffers are a Jacobi read/write split, not a ping-pong — PROVEN

The current/previous indices at `+0xC6C/+0xC70` do **not** select the tile buffers. Every tile
consumer hard-codes the roles:

- **Buffer 1 (`+0x1B` type, `+0x20` fill) = live, authoritative state.**
  - The `isunderwater` point query `FUN_0057a940:35` reads `tile[8]` = `+0x20`.
  - The fluid surface renderer `FUN_005763e0:130-131` reads `+0x1B & 7` and `tile[8]`.
  - Source emission `FUN_0057a1c0` reads/writes `tile[8]` (+0x20) and `+0x1B`
    (lines 106-116, 155-157, 250-264).
  - The set-rebuild activity test `FUN_005791a0:556-646` reads `+0x20` for "has fluid".
  - Transfer functions write results to `+0x20`/`+0x1B` only (`FUN_00577b20:100-109`,
    `FUN_00577e00:100-109`).
- **Buffer 0 (`+0x1A` type, `+0x1C` fill) = start-of-tick snapshot** read by the flow equations:
  transfer surface-pressure terms read `tile[7]` = `+0x1C` and the type-compatibility check reads
  `+0x1A` (`FUN_00577b20:51-82`); the per-cell type mixing in `FUN_00578c80:210-223` reads and
  writes `+0x1A`.
- The commit `buf1 → buf0` (`+0x1B→+0x1A`, `+0x20→+0x1C`) runs in `FUN_00578c80:89-93` at the
  start of the flow step, and **only for cells in the current active set**. A cell that leaves the
  set keeps whatever `+0x1A/+0x1C` it last committed — this is why real saves are full of stale
  buffer-0 values (Finding 10).

The `+0xC6C/+0xC70` indices ping-pong only the *coordinate work sets* (Finding 5). The save doc's
phrase "the current/previous indices select between those buffers" should be corrected accordingly.

### 4. Tick order and the one-tick load skip — PROVEN

`CFluid_Tick` (`00598920`, lines 103-111), gated on the global fluid-enable byte:

```
prevIdx = curIdx; curIdx = 1 - curIdx; // swap FIRST, every tick
FUN_0057adf0; // stopper refresh (owner moved / dirty → re-mark region)
FUN_0057a1c0; // source emission into buffer 1
if (this+0x08 == 0) { // skipped exactly once after a load
    FUN_00578c80();                         // commit buf1→buf0 + flow transfers, for set[curIdx]
    FUN_005791a0();                         // clear set[prevIdx], rebuild it as next tick's set
}
this+0x08 = 0;
```

Consequences after a load (saved indices c = cur, p = 1-c):

- Tick 1: swap → cur = 1-c. Only stoppers re-anchor (forced by the -1000 position override) and
  sources emit. No flow, no set rebuild.
- Tick 2: swap → cur = c. `FUN_00578c80` consumes **the set stored in slot `savedCur`**;
  `FUN_005791a0` **clears slot `savedPrev` before reading it** (`FUN_005791a0:37-57`), so whatever
  a writer stores in the `savedPrev` slot is discarded unread.

So of the four saved arrays, exactly one is ever consumed: `c84[savedCur]`. PROVEN by the swap
algebra plus the indexing in `FUN_00578c80:33-42` (uses `+0xC6C`) and `FUN_005791a0:36-57`
(clears `+0xC84` group at `[+0xC70]`, iterates at `[+0xC6C]`).

### 5. What the four sparse arrays are — PROVEN (c84) / LIKELY (c74)

- `this+0xC84/+0xC88` and `+0xC8C/+0xC90` (`c84[0]`, `c84[1]`): the double-buffered **active-cell
  set**. `set[cur]` = cells simulated this tick; `set[prev]` = next tick's set being built.
  Rebuild sources (`FUN_005791a0`): (a) cells of the current set whose fresh flow words exceed the
  activity threshold and still have fluid (adds self + all four neighbors, lines 551-665);
  (b) cells under sources whose `+0x25` dirty byte is set, plus east/south neighbors
  (lines 69-187); (c) the dig/set_water dirty queue `+0xCAD/+0xCB1` filled by `FUN_0057cd50`
  (MapValidator "FLUIDALIST" channel), plus east/south neighbors (lines 189-432). Dedup uses tile
  flag `+0x13` bit 2 (mask 4), which is **always cleared again** at the end of the rebuild
  (lines 447-512), then the set is sorted by `FUN_0057dc20` for determinism.
- `this+0xC74/+0xC78` and `+0xC7C/+0xC80` (`c74[0]`, `c74[1]`): consumed only by the
  `WaterDBGBlock` debug renderer `FUN_0056f5e0:41-48` (reads `c74[cur]`); no appender exists in any
  reachable code path found (grep over the whole decompile: only constructor, reset, serializer,
  debug renderer touch it), and the corpus shows 0 entries in 103/103 saves. Naming: **dead debug
  wet-cell list — always empty**. LIKELY (absence of writer cannot be proven exhaustively; corpus
  is unanimous). The second debug renderer `FUN_0056fb00:41-50` reads `c84[1-cur]`.

### 6. Names for tile `+0x24..+0x33` — PROVEN

Four little-endian floats forming the per-cell **flow-momentum double buffer**. Native tile axes:
X pair = cell (x,y) with its west neighbor (x-1,y); Y pair = cell (x,y) with (x,y-1)
(tile-Y = elevation axis; y-1 is "up" in the ant-farm view — Y is the gravity direction).

| Offset | Name | Law |
|---:|---|---|
| `+0x24` | `flowXPrev` | Last tick's horizontal momentum. Aging: `+0x24 ← +0x28` (`FUN_00578c80:91`). Consumed as transfer bias: `transfer += (flowXPrev(self) + flowXPrev(west)) * 0.5` (`FUN_00578c80:210`, `FUN_00577b20:83-84`, coefficient `DAT_0073708c = 0.5f`). |
| `+0x28` | `flowXCur` | This tick's horizontal momentum accumulator; zeroed in aging (`:93`), written after transfers (`:244`). Activity keep-alive: cell stays in the set while `abs(flowXCur) > 0.004f` (`FUN_005791a0:558,622`, mask `DAT_00737940 = 0x7FFFFFFF`, threshold `DAT_00736fc8 = 0.004f`) and self/west fill ≠ 0. |
| `+0x2C` | `flowYPrev` | Vertical (gravity-axis) analogue of `+0x24`; aging `+0x2C ← +0x30` (`:92`). |
| `+0x30` | `flowYCur` | Vertical accumulator (`:245`); same 0.004 activity test (`FUN_005791a0:556`). Source emission primes it directly with `0.05f` (`0x3D4CCCCD`, `FUN_0057a1c0:157`) whenever a source changes a cell's fill, which is what wakes a region up. |

Related constants read from the exe image: fill zero-clamp `DAT_00736fb8 = 0.001f`; transfer
coefficients `DAT_0073703c = 0.125f`, `DAT_007371c0 = 1.5f`; surface knee `DAT_0073752c = 8.0f`;
per-block flow-rate bits: horizontal `capacityDword >> 25` (7 bits, `FUN_00577b20:51`), vertical
`capacityDword >> 18 & 0x7F` (`FUN_00577e00:51`) — the same BDB `+0x55` dword whose bits 0-9 are
capacity and 10-13/14-17 the boundaries.

### 7. The loader does NOT rebuild fluid activity from cell state — PROVEN

The end of the world loader (`FUN_005931f0:242-252`) queues the whole world into the MapValidator
with mask `0x5F` and flushes it (`FUN_005a3fd0` + `FUN_005a46d0`; `FUN_005a3dd0` is just the
singleton getter, it takes no arguments — the decompile's `FUN_005a3dd0(0x7ff)` is an artifact).
Mask bit assignments (`FUN_005a3fd0:52-120` and channel labels in `FUN_005a46d0`): bit 5 = 0x20 is
FLUIDOBJS which cascades into bit 10 = 0x400 FLUIDALIST → `FUN_0057cd50` → fluid dirty queue.
`0x5F` = bits 0-4 and 6 — **neither fluid channel is queued**. Therefore post-load fluid activity
comes only from: the restored `c84[savedCur]` set, source emission, and future gameplay events.
There is no scan of tiles anywhere that reconstitutes the active set or the flow words.

### 8. Is the synthetic emission lawful? — PROVEN, with one behavioral caveat

A writer may emit: `buf0 == buf1` (live type/fill duplicated), all four flow words zero, all four
work sets empty, `curIdx=0, prevIdx=1`, `waterdbg=0`. Every consumer accepts it:

- Indices: only used as `< 2` array selectors (out-of-range raises the native bounds exception
  `0xC000008C`, so they MUST each be 0 or 1; `cur == prev` is never checked and is survivable
  because the first swap immediately makes them complementary, but complementary values are what
  the constructor (`FUN_0057d980:51-52`: cur=0, prev=1) and every real save contain — emit those).
- Empty sets: `FUN_00578c80`/`FUN_005791a0` loops simply don't run; sources re-seed their cells
  every tick via the `+0x25` mechanism, so source regions self-heal within 2 ticks.
- `buf0 == buf1`: for any cell that becomes active, the first commit overwrites buf0 anyway; for a
  cell that stays inactive but is *read as a neighbor* by a newly active cell, equal buffers give
  the true surface height — strictly better than the stale buf0 native itself leaves behind.
- Zero flow words: transfers still move fluid (the height-difference term does not need momentum);
  only the `*0.5` momentum bias starts at zero.

**Caveat (the one real behavioral difference):** fluid that was actively flowing at save time
freezes on load until a source or a dig/set_water event touches its region, because nothing
re-enters it into the active set. For settled fluid the synthetic form is observationally
identical to a native save. Native itself commonly saves with empty sets (84/103 corpus saves) —
settled fluid is the normal saved state.

### 9. Minimal faithful transition for mid-flow honesty — specification

If the port wants flowing water to keep flowing across a save/load into the original engine, the
writer must add exactly this (all consumers proven above):

1. Build the honest active-cell list: every cell the port's own sim considers non-settled, **plus
   its four orthogonal neighbors** (matching `FUN_005791a0`'s expansion), deduplicated and sorted
   by (y, x) ascending (the native comparator `FUN_0057dc20` sorts; byte-identical order matters
   only for byte-diff tests, not for acceptance).
2. Write it into `c84[curIdx]` (the slot indexed by the *current* index you emit — with
   `curIdx=0`, that is the second of the four arrays on disk). Leave `c84[prevIdx]` and both `c74`
   arrays empty: `c84[prevIdx]` is cleared unread on the first simulated tick, `c74` is dead.
3. For those cells, emit a non-zero keep-alive momentum so the set survives the rebuild:
   `flowYCur (+0x30) = 0.05f` is the executable's own priming value (`FUN_0057a1c0:157`) and is
   > the 0.004 threshold; `flowYPrev/flowXPrev/flowXCur = 0` are fine (the bias term merely starts
   at zero, exactly as it does when native wakes a region from the dirty queue).
4. Everything else stays as the port already writes it (live state into both tile buffers; source
   records with the 128 scale and −1.0/−10000 sentinels; stopper records — cached position may be
   anything since load overwrites it with −1000).

No dual-buffer simulation needs to be added to the port's one-buffer sim: buffer 0 is derivable
(equal to buffer 1) and the momentum words need only the constant seed above.

### 10. Corpus statistics (all 103 saves; probe `probes/b6_fluid_probe.py`) — PROVEN

- **Structure:** 103/103 parse through the fluid payload to marker 4. `cur/prev` is always a
  complementary pair — 0/1 or 1/0, never equal. `waterdbg` is 0 in every save. Tile flag `+0x13`
  bit 2 (the work-set dedup marker) is clear on every tile of every save, confirming it is
  transient-only.
- **Work sets:** the `c74` pair is empty in 103/103 saves. The `c84` pair is non-empty in 19/103
  saves (6 to 867 entries per slot; either slot alone or both slots populated occur). 84/103
  saves — including ones with hundreds of wet tiles — have all four sets empty: the original
  routinely saves settled fluid with no work entries.
- **Buffer divergence:** every save containing fluid (83/103) has `buf0 != buf1` somewhere
  (e.g. save_test: 654 wet tiles, 652 divergent; 533 type-diff, 144 fill-diff). Breakdown on three
  representative saves: type divergence exists **only on drained trails** (fill1 == 0 with stale
  buf0 type; live cells have type0 == type1 in 121/121, 333/333, 98/98 sampled cells), while live
  cells almost always carry a small frozen fill lag (fill0 == fill1 on only 2/121, 9/333, 0/98
  live cells; max |fill1−fill0| 0.005-1.32). This is exactly the Finding-3 mechanic: buf0 freezes
  one micro-step behind when a cell leaves the active set.
- **Flow words:** nonzero in all 83 fluid saves (95-1522 tiles), split roughly half on live cells
  (small residue, e.g. max 0.0048-0.108 in settled saves; up to 82-147 in mid-flow saves) and half
  on drained trails (stale, up to 3.4). Zeroing them in a synthesized save is cleaner than
  native's own output and is only ever read as a bias term for active cells.

## Port mapping recommendation

| Field | Disposition | Reason |
|---|---|---|
| Tile `+0x1B` type / `+0x20` fill (buffer 1) | **port state** (the one authoritative cell value) | Everything observable reads buffer 1 (Finding 3). |
| Tile `+0x1A` type / `+0x1C` fill (buffer 0) | **derivable-on-write** (duplicate buffer 1); on load, prefer buffer 1 and ignore buffer 0 | Buffer 0 is a start-of-tick snapshot, stale on most saved tiles (Finding 10); loading it as truth would resurrect drained ghosts. The port loader should treat buffer 1 as the cell state — corpus proves buffer 0 disagrees on ~90 % of wet tiles. |
| Tile `+0x24 flowXPrev`, `+0x2C flowYPrev` | **derivable-on-write** (0); ignore on load | Only a 0.5-weighted bias into the first transfer of already-active cells (Finding 6). |
| Tile `+0x28 flowXCur`, `+0x30 flowYCur` | **derivable-on-write**: 0 for settled cells, `0.05f` in `+0x30` for cells in the emitted active set; on load usable as a hint ("this cell was moving") but safe to ignore | Keep-alive vs the 0.004 threshold; 0.05 is the executable's own priming constant (Findings 6/9). |
| `curIdx/prevIdx` (`+0xC6C/+0xC70`) | **compatibility-retain** as constants 0/1 | Must each be < 2; complementary pair matches constructor and 103/103 corpus saves (Finding 8). |
| `c84[curIdx]` active set | **port state → honest emission** (non-settled cells + 4-neighbors) | The only array ever consumed after load (Finding 4); required for mid-flow continuation (Finding 9). Empty is lawful for settled fluid. |
| `c84[prevIdx]`, `c74[0]`, `c74[1]` | **compatibility-retain empty** | Cleared unread / dead debug list (Findings 4/5). |
| `waterdbg` byte | **compatibility-retain 0** | 103/103 corpus; only gates debug renderers. |
| Source records | **port state** (already mapped: 128 scale, −1/−10000 sentinels) | Read side proven identical; in-memory `+0x25` is forced on load, not serialized (Finding 1). |
| Stopper records | **port state**; cached position float triple may be emitted as anything valid | Load overwrites it with (−1000, −1000, 0) before use (Finding 1). |
| Tile `+0x13` bit 2 (mask 4) | **must be 0 in emitted tiles** | Transient dedup marker, clear in every corpus tile; a set bit would suppress the cell's first re-activation (Finding 5). |

The port's existing writer behavior documented in `Docs/FLUID_SYSTEM_RE.md` ("live cells into both
original buffers, clears the transient work sets") is confirmed lawful; the only recommended
change is Finding 9 (emit the active set + 0.05f `+0x30` seed for non-settled cells) if byte-honest
mid-flow continuation inside the original engine is a goal.

## Open questions

1. The exact dynamics of the momentum sum (`flowCur = flowPrev(self) + flowPrev(neighbor)` with no
   visible decay) versus the small residues seen in settled saves are not fully modeled; the
   stored values' growth/decay path does not affect the persistence law but remains uncharacterized
   (UNKNOWN — would need a live-dump trace across ticks).
2. No appender to the `c74` debug list was found; a cut/debug-build writer may exist behind the
   `waterdbg` flag (LIKELY dead; corpus-unanimous).
3. Why the one-tick skip re-consumes `set[savedCur]` (the pre-save tick's set) instead of the
   freshly built pending set in `set[savedPrev]` — the observable effect (one redundant, idempotent
   re-simulation of the last active set) is proven, but whether this was intentional warm-up or an
   off-by-one the game shipped with is unknowable from the binary alone.
