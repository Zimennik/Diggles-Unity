# B2 — Object-type-dependent extensions of the version 0xA5 object body

## Status

All three type-dependent extension blocks of the `FUN_005d0ce0` object body are now semantically
named, and the native object-type taxonomy is fully resolved and fixture-confirmed. The types-1/11
"three actor-link records + string array + flag" block is the **family store** (partner, mother,
father smart-links; children display names; pregnancy flag). The type-1 "named-index stream" is the
**talkissue ledger** (conversation-topic name → float weight plus an independent running desire
sum), and the type-1 "four-value state" is the **production-planner gnome sub-state** (idle flag,
idle-since timestamp, current workplace ID, assigned workplace ID). The types-5/6/7/10 block is the
**CProd production sub-object** (product slots with order counts, the work-state code, assigned
worker, pack/unpack/buildup request bytes with a placement position, production progress, energy
fields, and a 16-seat guest table). Every field row below cites the decompile/instruction evidence
or a corpus statistic computed by the new probe
`Tools/SaveFormatResearch/probes/b2_type_extensions_probe.py` (103/103 saves parsed; output kept at
`probes/b2_corpus_out.txt`). Confidence is marked per finding; the handful of retained-raw bytes
that never branch in fixtures are explicitly left UNKNOWN.

## Findings

### 1. Native object type taxonomy (obj+0xE8) — PROVEN

The `Int32` object type serialized right after the body class name is `obj+0xE8`. Its source of
truth is the **class descriptor**: on load, `FUN_005c9fa0:75-76` re-derives it as
`obj+0xE8 = classdesc(+0xD4)->+0x54`, *overwriting* the archived value, and then allocates the
type-specific sub-objects from that class-derived value. The archived type therefore only matters
because the writer used it to decide the stream layout — layout authority on load is the class
table, and the two must agree (they do for every shipped class, since a class's kind is static).

The kind-string ↔ integer mapping is proven twice:

- `FUN_006a33d0:120-122` (`get_objtype`-family) matches the argument against the ordered list
  `none, dummy, gnome, monster, material, tool, production, energy, protection, store, transport,
  elevator, baby, info` (list index − 1 = type; the four `DAT_` strings were read out of
  `diggles.exe` at 0x722ca4/0x72bdb8/0x72bde8/0x727b60 = `none`/`tool`/`baby`/`info`);
- `FUN_005ffe40:13-56` is the exact reverse `switch` (type → string, default `"illegal"`), and the
  `obj_query -type` mask builder `FUN_005de500:381-677` assigns each name the bit `1 << type`
  (`"any"` = 0xFFFFFFFF).

| type | kind | corpus classes (probe, 103 saves) |
|---:|---|---|
| -1 | none | Zuschauer, AutoprodDigsiteFinder, ProductionManager (148) |
| 0 | dummy | Misc_Light, Dummy_*, Axt … (36,934) |
| 1 | gnome | Zwerg 2,569; Trompeter 8; Koenig 4 |
| 2 | monster | Troll, Fresspflanze, Wuker, Fenris_001 … (602) |
| 3 | material | Stein, Kohle, Pilzstamm, Kristallerz … (77,782) |
| 4 | tool | Reithamster, Keule, Steinschleuder, Fisch … (6,022) |
| 5 | production | Grenzstein, Farm, Zelt, Feuerstelle, Lager, Hauklotz … (4,255) |
| 6 | energy | Laufrad 99, Dampfmaschine 53, Reaktor 31 |
| 7 | protection | Holztuer, Plattmachfalle, Trolltuer, Steintuer … (447) |
| 8 | store | **absent from all 103 saves and from `def_class` usage in data/scripts** |
| 9 | transport | Holzkiepe, Grosse_Holzkiepe, Schubkarren, Weichensignal_* (2,665) |
| 10 | elevator | Leiter, Leiter_Kristall, Dampfaufzug, Aufzug … (1,385) |
| 11 | baby | Baby (39) |
| 12 | info | Info_Coll_*, PilzMyzel, FogRemover … (12,640) |

Cross-check: the 99/53/31 type-6 counts equal the marker-8 EnergyMgr source-record class histogram
already in `ORIGINAL_SAVE_FORMAT_RE.md` (Laufrad 99, Dampfmaschine 53, Reaktor 31) — the same
objects, registered into the energy manager by `FUN_005c9fa0:248-272` for type 6 only.

Load-side extras in `FUN_005c9fa0`: types 1/5/6/7/8/10/11 set `obj+0x3D0 |= 0x600`, types 3/4/9 set
`|= 0x400` (:80-96,:169-177); `obj+0x208 = -1` when `classdesc+0x8C == 0` (:77-79). Sub-object
allocation: type 1 → `+0x3E4`, `+0x3F0`, `+0x3F4`; type 11 → `+0x3F0` only; types 5/6/7/10 →
`+0x3E0` only (:96-155,:157-168,:178-273). This exactly matches the serializer gates in
`FUN_005d0ce0:363-393` and the probe's boundary law (types 1/11 → family; 1 → talkissue + four
values; 5/6/7/10 → CProd).

### 2. Types 1 and 11 — the family store at obj+0x3F0 (CFamily) — PROVEN

The Tcl surface is the object command **`partner_info`** (`TCL:CTclObjMethods::PartnerInfo`,
handler `FUN_006a95a0`, registration pair extracted from the exe at 0x67130c). Subcommand literals
read from the binary at 0x6a97e9-0x6a9d2c: `setpartner, addchild, setmother, setfather,
setpregnancy, getpregnancy, getpartner, getmother, getfather, getchildren, transfer`.

Sub-object layout (constructor `FUN_005db590` zeroes 0x5D bytes; three 0x1C-stride records):

| offset | record | field |
|---:|---|---|
| +0x00 | partner | target registry ID (`setpartner` writes `obj+0xDC`, FUN_006a95a0:180) |
| +0x0C/+0x10/+0x14 | partner | tracked pointer (+4-biased), death-callback `FUN_005db6f0`, owner |
| +0x18 | partner | state: 0 = never set, 1 = linked, 2 = target died |
| +0x1C/+0x28/+0x34 | mother | same triple (`setmother` → `FUN_005db7c0`, getter `FUN_005db890`) |
| +0x38/+0x44/+0x50 | father | same triple (`setfather` → `FUN_005db710`, getter `FUN_005db870`) |
| +0x54/+0x58 | children | string-vector buffer / count |
| +0x5C | — | pregnancy flag byte (`setpregnancy`/`getpregnancy`, FUN_006a95a0:414-434) |

Serialization (`FUN_005d0ce0` instruction dump `save_sections_dump.txt` 005d1690-005d16c0): three
`FUN_005dc500` calls at record bases **+0x00, +0x1C, +0x38** — i.e. archive order is **partner,
mother, father** — then the children count/strings, then the flag byte. Each `FUN_005dc500` record
emits `{Int32 id(+0), Int32 state(+0x18), objref from tracked ptr(+0xC)}`; on read the pointer is
re-attached through `FUN_00445640(ptr+4)` (the +4 bias is the object's embedded listener node), but
the death-callback word (+0x10) is **not** restored — the original relies on the lazy revalidator
`FUN_005db640` (state 1 → 2 whenever `registry[id]` is empty) instead.

Semantic notes, all fixture-verified by the probe:

- children are **display-name strings** (`addchild` copies the child's `obj+0xD8` name string and
  its interned id, FUN_006a95a0:214-234; corpus samples: Frank, Heino, Egnatius, Klementine …);
  counts run 0..4 (1,934/221/262/188/15);
- state triples: (1,1,1) 1,030; (1,0,0) 700; (0,1,1) 289; (1,2,2) 249; … deceased links are
  retained as state 2 with id kept and serialized ref −1 (715 records);
- for live (state 1) records the ref word equals the id word in 4,896 of 4,930; **34 live records
  have ref −1** (target invalid at write time) and **12 live ids are undeclared** — the loader must
  tolerate both (original does: revalidation is lazy);
- pregnancy flag: 33 × 1, 2,587 × 0 — a strict 0/1 boolean;
- `transfer` copies a **baby's** mother/father records into the receiving gnome's own family on
  grow-up (FUN_006a95a0:322-364; father 870→710, mother 890→7c0), which is why type 11 (baby)
  carries the same block; babies never gain the talkissue or planner extensions.

So the "actor links" are **not** mount/carry/follow: mount/carry state stays in the raw link word
at `obj+0x278` (already documented). These three records are purely the breeding/family graph.

### 3. Type 1 — the named-index stream = talkissue ledger at obj+0x3F4 — PROVEN

Handler `FUN_006aa650` = Tcl command **`talkissue`** (registration at 0x671b29; literals `add,
reduce, sum, get, set, list, delete`; errors "object is not a gnome", "invalid talkissue command").
Serializer `FUN_005ff730`:

1. `Int32` at sub+0x18 first (`FUN_00406f60(param_1+6)`,:33) — the **running talk-desire sum**
   (float bits; `talkissue <g> sum` returns it, and `reduce` subtracts from it —
   FUN_006aa650:336-356 `piVar2[6] -= value[idx]`);
2. then, per Tcl hash entry (`Tcl_FirstHashEntry`/`Tcl_NextHashEntry`,:36-102): sentinel `1`,
   issue-name string, one raw 4-byte value = the issue's **float weight**; terminated by `-1`.
   On read the pairs rebuild the value array + name→index hash (:10).

Corpus: up to ~14 pairs per gnome; the name universe is exactly the script codes from
`classes/zwerg/z_spare_talk.tcl` (`ubw, tlw, mnf, wtm, wal, asw, ocw, uqw, wis, amng, user, bth,
wthr, frgt, slp, npw, wmm, eat, pnb, elfe, fun, whu, ttp, yamm`, …). The saved sum matches the
Σ(weights) in only 849 of 2,581 gnomes — **the sum is independent state** (reduced after each
conversation without deleting entries) and must be persisted, not derived. The internal
"spoken-issue queue" array (sub+0x08/+0x0C, appended by `reduce`) is *not* serialized.

### 4. Type 1 — the four-value state = planner gnome sub-state at obj+0x3E4 — PROVEN

Serialized as four raw words `+0x34, +0x38, +0x3C, +0x40` (`FUN_005d0ce0:388-394`). The sub-object
is the per-gnome CProdPlanner record (constructed in `FUN_005c9fa0:97-138`, which also sets
`+8 = owner`, `+0x38 = float(gametime)`, `+0x44/45 = 1/0`).

| offset | meaning | evidence | verdict |
|---:|---|---|---|
| +0x34 | **idle-announce flag** (0/1): gnome offers itself to the auto-work pool | Tcl `prod_gnomeidle` = `TCL:CTclObjMethods::ProdGnomeIdle()` `FUN_006a86a0:85,91` writes 0/1; corpus: 2,051×0 / 530×1, no other values | PROVEN |
| +0x38 | **idle-since / last-dispatch game time** (float seconds) | init to now (FUN_005c9fa0:136-138); job-offer gate `gametime − sub[0x38] < sub[0x48] → skip` (FUN_00438210:35-38); port ProdDispatcher already models "idle-since(rec+0x38)" | PROVEN |
| +0x3C | **current workplace object ID** (building being worked at; −1 none) | assign: `FUN_005ede50:17-22` writes the CProd owner's `obj+0xDC`; release: `FUN_0043a0c0:17-66` uses it as a registry index and resets −1 | PROVEN |
| +0x40 | **assigned ("home") workplace object ID** (sticky job-preference; −1 none) | distance-cost bonus when the offer IS this building, penalty otherwise (FUN_00438210:296-302 × DAT_0073703c/DAT_00737534); `FUN_00433710` = "is this building someone's +0x40" | PROVEN |

Corpus: `+0x3C` = −1 ×1,065, declared ×612, **dangling ×904**; `+0x40` = −1 ×1,911, declared ×619,
dangling ×51 (targets: Dojo, Luxuskueche, Bar, Feuerstelle, Industriekueche, Schleiferei, Lager…).
Stale IDs of demolished buildings are normal retained state — the original never scrubs them on
save; consumers re-validate through the registry. Non-serialized planner fields (`+0xC` state code,
`+0x45` auto-work gate, `+0x48` cooldown, `+0x28/+0x2C` in-flight job target IDs — compared against
job-target `obj+0xDC` in FUN_00438210:74-82) reset to constructor defaults on load.

`obj+0xDC` = the object's own registry ID is itself proven by the reference serializer
`FUN_00555d70:31-48` (validates the `'bOna'` magic at obj+8, then writes `*(obj+0xDC)`).

### 5. Types 5/6/7/10 — the CProd production sub-object at obj+0x3E0 — PROVEN (field-level)

Allocated for kinds production/energy/protection/elevator (`FUN_005c9fa0:178-247`), then
`FUN_005ed6c0` immediately rebuilds the slot array from the class's tech-tree product list
(class → `FUN_0043e4d0` prod-info; one 0x1C slot per product; name interned at slot+8, prod-info
resolved into slot+0xC). The archive (`FUN_005ed160`, instruction dump
`save_missing_helpers_dump.txt`) then overlays:

**Slot array** (`Int32 count` at +0x10, entries 0x1C):

| slot offset | field | evidence | verdict |
|---:|---|---|---|
| +0x00, +0x04 | two registry indices resolved by name at construction (`FUN_00444990` binary-search name→value lookups, defaults −1; FUN_005ed6c0:97-132) | corpus: +0 is −1 in 9,364/16,627, +4 never −1 | LIKELY (identity of the two registries unnamed) |
| +0x08 | product class name (string) | FUN_005ed6c0:63-81; equals prod-info name in 16,627/16,627 | PROVEN |
| +0x0C | prod-info pointer, serialized as its name, re-resolved on load | 005ed1cd-005ed229 (`FUN_0043e4d0`) | PROVEN |
| +0x10 | **order count 0..10, 10 = infinite** ("oo" badge) | `FUN_005edc10:18-27` decrement w/ 0..10 clamp; `info_end_prod` `FUN_0069e210:82-97` skips decrement at 10; corpus: Farm/Pilz 10, Lager `_*_einlagern` 10, Holztuer `_Automatisch` 10 | PROVEN |
| +0x14 | float demand/production-priority score | scaled ×DAT_00737034/DAT_00737054 on completion (FUN_0069e210:87-96); zeroed with the invented byte (FUN_005ef0b0:592-596); corpus 0.0/1.0/small fractions | PROVEN (exact formula not chased) |
| +0x18 | invented/available byte | cleared+set in the demand sweep `FUN_005ef0b0:456-462,587-592` | PROVEN (recomputed) |
| +0x19 | buildable/requirements-met byte | set after the ingredient scan vs the per-player 0x200-entry stock table `FUN_005ef0b0:470-578` | PROVEN (recomputed) |

**Body fields**, in exact archive order after the slots:

| offset | field | evidence | verdict |
|---:|---|---|---|
| +0x1C | work-state code: 0 idle, 1 research, 2 produce, 3 bring, 4 pack, 5 buildup, 6 unpack | dispatcher emit stamps 2/1 (FUN_0043a8a0:78,91), path-B table (`re_parts/dispatcher-reconcile.md` §3); release resets 0 (FUN_0043a0c0:39); corpus: 0 ×6,116, 2 ×105, 3 ×49 | PROVEN |
| +0x24 | assigned worker objref | instruction 005ed286-28e (`LEA EDX,[EBX+0x24]; CALL 00555d70`); assign `FUN_005ede50:17`; all 227 corpus refs resolve | PROVEN |
| +0x28..+0x2D | request bytes: +0x28 unpack-requested (placement confirm `FUN_00531310:328` with companion +0x2B :329), +0x29 pack-requested (`set_prod_pack`; port PackRequested; companion +0x2C), +0x2A buildup-requested (`set_prod_buildup`; port BuildupRequested), +0x2D placement variant byte | request scan `FUN_00436440:31-118` builds kind-0/1 jobs from +0x29/+0x2A/+0x28 (+0x2D copied into the job); `set_boxed` clears +0x3D/+0x2C/+0x29 on box and +0x3D/+0x2B/+0x28 on unbox (FUN_0067dc80:145-161) | PROVEN |
| quirk | **+0x28 is serialized twice** (bytes +0x28,29,2A,2B,2C,2D then +0x28 again — 7 transfers) | instructions 005ed293-005ed2d8 (final `PUSH EDI` re-uses EBX+0x28) | PROVEN |
| quirk | **+0x2D carries allocator residue** (0xDF/0x73 observed 1,306+299 times) — constructor inits only +0x28..+0x2C | FUN_005c9fa0:215-216 (`dword +0x29 = 0`, byte +0x28 = 0); corpus | PROVEN |
| +0x30/+0x34/+0x38 | pending pack/unpack/buildup **placement position** (float x,y,z) | placement confirm writes them (FUN_00531310:325-331,344-346); request scan uses them as the job position (FUN_00436440:45-47,67-69); corpus non-zero rows decode to world floats (e.g. 250.4/61.0/6.75) | PROVEN |
| +0x3D..+0x3F, +0x44, +0x40..+0x43 | placement-flow/announce flag bytes (constructor defaults 0,0,0 / 0 / 0,1,1,1); +0x3D/+0x3E written by the placement flow (`FUN_00531310:330,341`), +0x41 gates the material-demand collection (`FUN_00434e60:300`) | serialized in the odd order …+0x3F, **+0x44**, +0x40..+0x43 (005ed2fe-005ed351) | LIKELY (individual bit meanings beyond +0x3D/+0x3E/+0x41 unnamed — retain raw) |
| +0x48 | **interned name-table ID of the class currently in production** ("Bp" research uses the same stamp) | stamped from `step[7]+8` at dispatch (FUN_0043a8a0:77,89 with `+0x1C=2/1`); compared against slot-name interned ids in `info_end_prod` (FUN_0069e210:77); corpus: −1 ×6,106, else small ids (0x12B..0xA56) | PROVEN — but **session-scoped**: the interned table is rebuilt each boot; the original loads the raw word and merely risks a no-op mismatch |
| +0x4C / +0x50 | production progress current / max steps | `info_progress_prod` `FUN_0069def0` (cur→+0x4C, max→+0x50, per `WORK_DISPATCHER_NATIVE.md`); reset to −1/−1 at dispatch (FUN_0043a8a0:79-80), 0/0 at completion (FUN_0069e210:106-108); corpus (4,8),(5,25),(−1,−1)… | PROVEN |
| +0x54 | invention-candidate slot index picked by the demand sweep (lowest unmet-requirement deficit) | `FUN_005ef0b0:507-514,693`; corpus −1 ×6,010 else small slot indices | PROVEN (recomputed each sweep) |
| +0x64 | production enabled flag (`set_prod_enabled`/`get_prod_enabled`) | handler `FUN_006950a0:87-88`; corpus 5,956×1 / 314×0 | PROVEN |
| +0x12C | energy class (int) | `set_energyclass` `FUN_00694170:117-127` (also mirrors into the EnergyMgr record +4 for type 6) | PROVEN |
| +0x130 | energy consumption (float) | `set_energyconsumption` `FUN_006944a0:77-86`; gate `FUN_005edcd0` | PROVEN |
| +0x134 | energy store (float) | `set_energystore` `FUN_006949a0:64-73` | PROVEN |
| +0x138 | energy max store (float) | `set_energymaxstore` `FUN_00695350:64-73` | PROVEN |
| +0x13C | current source load (float) | recomputed every energy tick (`FUN_005b9940:77`, `CGameThread_SrvDoThink:259`); 0 in all corpus rows | PROVEN (derivable) |
| +0x6C.. | **16 guest-seat records** `{Int32 seat-link id, objref guest, Int32 order count}` (stride 0xC; init {−1,0,0}) | Tcl `prod_guest` `FUN_006a8920` (literals `addlink, setlink, getlink, guestset, guestremove, guestget, guestfree, addorder, getorder, remorder, resetorder, nextorder`): addlink fills first free rec+0 (:162-192), guestremove clears rec+4/rec+8 by object (:277-307), guestget names rec+4 (:324-355), guestfree validates 'bOna' and returns a free seat (:357-416), remorder decrements rec+8 (:443-449); objref emission at 005ed487-005ed48c | PROVEN |
| +0x3C (byte, ver>0x87) | unnamed flag; corpus 31×1, rest 0 | serialized 005ed536-53c | UNKNOWN (retain) |
| +0x140 (byte, ver>0x9B) | unnamed flag; corpus 1×1 | FUN_005ed160:142-144 | UNKNOWN (retain) |

Live-value spot-check (save_test.sav): Saegewerk class 1 / cons 80, Dreherei 2/200, Dampfhammer
2/100, Schmelze 1/10, Mittelalterkueche 1/10, Tischlerei 1/100, Industriekueche 2/20; sources
Laufrad store 370/max 400, Dampfmaschine store 1920/max 2000 — exactly the techtreetunes numbers,
confirming the energy field mapping end-to-end. The dominant all-zero rows in the corpus are real
(wood-era classes have class 0 / consumption 0, and `set_energymaxstore` is script-driven only on
sources).

Guest seats belong to the sparetime service buildings (`classes/sparetime/bar.tcl` etc.:
`prod_guest addlink this 4/5/6/7/3/2/1` = the seat dummy-link ids that the corpus shows as
w0 ∈ 0..7; 555 guest refs all resolve; order counts 0..8). Non-service producers leave the table
at {−1,−1(ref),0}.

Not serialized (reset by constructor + `FUN_005ed6c0` on load): CProd+0x00 last-produced slot,
+0x04 in-progress counter (decremented by `info_end_prod` FUN_0069e210:61), +0x08, +0x20 owner
back-pointer, +0x5C/+0x60 UI-facing state (FUN_0043a0c0:29 writes 5/2 there on release), +0x68
float 1.0.

### 6. Emission order within the body — PROVEN

For a type-1 gnome the tail order is: family block (+0x3F0) → talkissue (+0x3F4) → four planner
words (+0x3E4). For type 11 only the family block; for 5/6/7/10 only CProd (+0x3E0), emitted
between the display/ownership group and the attachment-history byte — exactly as
`parse_a5_object_bodies.py` already encodes (`FUN_005d0ce0:363-394`).

## Port mapping recommendation

| Native field | Port state | Strategy |
|---|---|---|
| obj+0xE8 type | `DigglesClassDef.Kind` string | **derive on write** from the class kind via the proven table (1); on load require archived type == class kind (layout guard). Never store per-entity. |
| family partner/mother/father `{id,state}` | `_partner_partner/mother/father` pseudo-vars (DigglesTclEngine.cs:3829-3853) | map: state 1 → handle of the resolved entity; state 2 → the native "deceased" marker (port needs a tri-state: suggest keeping `{id,state}` verbatim in a new save-side struct and materializing the Tcl var only for state 1; getters already return 0/−1 for dead/never). Tolerate dangling ids (34 live-ref mismatches + 12 undeclared in fixtures). |
| family children strings | `_partner_children` Tcl list | map 1:1 (both are display-name lists). |
| family flag +0x5C | `_partner_preg` | map 1:1 boolean. |
| talkissue pairs | `DigglesTclEntity.TalkIssues` dict | map 1:1 (name → float). |
| talkissue sum +0x18 | **missing in port** (sum is recomputed by `talkissue sum`) | compatibility-retain the saved float and add a stored sum to the port ledger — it is NOT derivable (corpus: differs in 1,732/2,581 gnomes). |
| planner +0x34 idle flag | ProdDispatcher idle pool membership | map: 1 ⇒ re-announce to the pool on load. |
| planner +0x38 idle-since | ProdDispatcher idle-since timestamp | map 1:1 (same seconds domain as the saved game clock). |
| planner +0x3C current workplace | dispatcher's in-flight plan place | derivable-on-load from restored plans; retain raw for write-back. Tolerate dangling (904 in corpus). |
| planner +0x40 assigned workplace | **missing in port** | add a per-gnome `AssignedWorkplaceId`; it drives the native stickiness bonus/penalty and `FUN_00433710`. Compatibility-retain dangling ids. |
| CProd slot order counts +0x10 | `DigglesTclEntity.ProdSlots[class]` | map by slot class name; 10 = infinite matches the port's cap-10 semantics. Slot ARRAY itself is class-derived — rebuild, then overlay counts. |
| CProd slots +0x00/+0x04/+0x14/+0x18/+0x19 | none (recomputed natively) | +0x18/+0x19/+0x14/+0x54 are sweep-recomputed → derivable; +0x00/+0x04 unnamed registry indices → compatibility-retain raw until named. |
| CProd +0x1C work-state | Tcl `current_worktask` (per dispatcher doc) | derivable from the restored action/Tcl state; retain raw for byte-faithful write-back. |
| CProd +0x24 worker | dispatcher plan's gnome | resolve after identity pass; consistency-check against the gnome's +0x3C. |
| CProd +0x28/+0x2B unpack, +0x29/+0x2C pack, +0x2A buildup, +0x2D variant, +0x30..38 position, +0x3D/+0x3E flow bytes | `PackRequested`, `BuildupRequested` (+ placement position missing) | map the three request pairs to port booleans; **add the pending placement position** (port loses it today); keep +0x2D and the flag bytes raw (allocator residue is legal — do NOT boolean-validate +0x2D). |
| CProd +0x48 product stamp | none | compatibility-retain the raw word; on the port side prefer re-stamping from the restored plan (the interned id is session-scoped even in the original). |
| CProd +0x4C/+0x50 progress | BottomMenu progress (cur/max) | map 1:1 ints; −1/−1 = dispatched-not-started. |
| CProd +0x64 enabled | `set_prod_enabled` state (ProdSlotOverrides/enabled flag) | map 1:1. |
| CProd +0x12C..+0x138 | `EnergyClass/EnergyConsumption/EnergyStore/EnergyMaxStore` | map 1:1 (int + 3 floats); +0x13C load is derivable (EnergySystem recomputes). Keep consistent with the marker-8 EnergyMgr document (class/range live there for sources). |
| CProd 16 guest records | `Guests`/`GuestOrders` (currently 4 seats, no link ids) | widen the port table to 16 and add the seat-link id word; guest refs resolve after identities. |
| CProd +0x3C/+0x140 bytes | none | compatibility-retain raw (no fixture branches). |

General loader laws to honor: (a) the class kind decides the extension layout — reject an archive
whose stored type contradicts the port class table instead of misparsing; (b) every extension field
that holds a registry ID may dangle and must load losslessly; (c) the +0x28 double-emission and the
+0x2D residue byte are part of the byte-exact writer contract.

## Open questions

1. Slot words +0x00/+0x04: which two global name-keyed registries do the `FUN_00444990` lookups in
   `FUN_005ed6c0:97-132` hit (invention index vs. something else)? Values are overlay-loaded, so
   naming them is not load-blocking.
2. The exact per-bit meaning of CProd +0x3E/+0x3F/+0x40/+0x42/+0x43 (+0x41 = material-demand gate
   is known); corpus shows a handful of flipped combinations tied to pack/unpack flows.
3. The version-gated bytes CProd+0x3C (31×1) and +0x140 (1×1) — no branching consumer found yet.
4. Whether the interned-name table order (and therefore a stale +0x48) is reproducible across
   original-exe sessions of the same game version; irrelevant if the port re-stamps from plans.
5. `FUN_00436440`'s +0x28-branch job (kind 1 with the +0x2D flag) vs `FUN_005f0850`'s kind-7
   buildup collection from +0x2A: the two collectors overlap on the same request bytes; a precise
   arbitration trace would pin which layer serves which button, but does not change the save
   schema.
6. Slot +0x14's exact demand formula (scale constants DAT_00737034/DAT_00737054/DAT_00737138) —
   recomputed at runtime, retained raw in the save.
